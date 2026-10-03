using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;
using PUCFinance.AssetManagement.Models.DTOs;

namespace PUCFinance.AssetManagement.Services;

public class TradeService
{
    /// <summary>
    /// Serializa alteracoes de posicoes/caixa (trades, exclusoes e a migracao de moeda)
    /// para que duas operacoes nao reconstruam o mesmo fundo ao mesmo tempo.
    /// </summary>
    public static readonly SemaphoreSlim PortfolioLock = new(1, 1);

    private readonly AppDbContext _db;
    private readonly PricingService _pricing;
    private readonly TesouroDiretoClient _tesouro;
    private readonly ILogger<TradeService> _logger;

    public TradeService(AppDbContext db, PricingService pricing, TesouroDiretoClient tesouro, ILogger<TradeService> logger)
    {
        _db = db;
        _pricing = pricing;
        _tesouro = tesouro;
        _logger = logger;
    }

    /// <summary>
    /// Executa um trade: busca preco atual (convertido para BRL), registra no log, atualiza posicao e caixa.
    /// </summary>
    public async Task<Trade> ExecuteTradeAsync(ExecuteTradeRequest request)
    {
        var fund = await _db.Funds.FindAsync(request.FundId)
            ?? throw new InvalidOperationException($"Fundo {request.FundId} nao encontrado");

        if (request.Quantity <= 0)
            throw new ArgumentException("Quantidade deve ser positiva");

        if (request.Side is not ("long" or "short"))
            throw new ArgumentException("Side deve ser 'long' ou 'short'");

        var ticker = request.Ticker.Trim().ToUpper();
        var isTreasury = TesouroDireto.IsTreasuryTicker(ticker);
        if (isTreasury)
            await ValidateTreasuryTradeAsync(ticker, request);

        // Preco atual ja em BRL (acoes: Yahoo; titulos publicos: PU de compra ou de venda do Tesouro)
        var quote = await _pricing.GetTradeQuoteAsync(ticker, request.Side);
        if (quote == null || quote.PriceBrl <= 0)
            throw new InvalidOperationException($"Nao foi possivel obter preco para {request.Ticker}. Verifique se o ticker esta correto.");
        var price = quote.PriceBrl;

        await PortfolioLock.WaitAsync();
        try
        {
            var cash = await _db.Cash.FindAsync(request.FundId)
                ?? throw new InvalidOperationException($"Registro de caixa nao encontrado para fundo {request.FundId}");

            // Recarrega o saldo: outra operacao pode ter alterado o caixa enquanto o preco era buscado
            await _db.Entry(cash).ReloadAsync();

            // Titulo publico nao pode ser vendido a descoberto: so vende o que o fundo tem
            if (isTreasury && request.Side == "short")
            {
                var held = await _db.Positions
                    .Where(p => p.FundId == request.FundId && p.Ticker == ticker)
                    .Select(p => (double?)p.Quantity)
                    .FirstOrDefaultAsync() ?? 0;
                if (request.Quantity > held + 1e-9)
                    throw new InvalidOperationException(
                        $"Venda maior que a posicao em {ticker}: o fundo tem {held:N2} titulo(s). Titulos publicos nao podem ser vendidos a descoberto.");
            }

            var signedQuantity = request.Side == "long" ? request.Quantity : -request.Quantity;
            var cashImpact = -(signedQuantity * price);

            if (cash.Balance + cashImpact < 0)
                throw new InvalidOperationException(
                    $"Caixa insuficiente. Disponivel: {cash.Balance:N2}, necessario: {-cashImpact:N2}");

            // 1. Registra o trade
            var trade = new Trade
            {
                FundId = request.FundId,
                Ticker = ticker,
                Side = request.Side,
                Quantity = request.Quantity,
                Price = price,
                Currency = quote.Currency,
                FxRate = quote.FxRate,
                Thesis = request.Thesis,
                ExecutedBy = request.ExecutedBy
            };
            _db.Trades.Add(trade);

            // 2. Atualiza posicao
            await UpdatePositionAsync(request.FundId, trade.Ticker, signedQuantity, price, request.Side, trade.ExecutedAt);

            // 3. Atualiza caixa
            cash.Balance += cashImpact;
            cash.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "Trade executado: {Side} {Qty} {Ticker} @ {Price} BRL ({Native} {Currency} x {Fx}) | Fundo: {Fund} | Tese: {Thesis}",
                request.Side, request.Quantity, trade.Ticker, price, quote.NativePrice, quote.Currency,
                quote.FxRate, fund.Name, request.Thesis);

            return trade;
        }
        finally
        {
            PortfolioLock.Release();
        }
    }

    private async Task ValidateTreasuryTradeAsync(string ticker, ExecuteTradeRequest request)
    {
        var cents = request.Quantity / TesouroDireto.QuantityStep;
        if (Math.Abs(cents - Math.Round(cents)) > 1e-6)
            throw new ArgumentException("Titulos publicos sao negociados em multiplos de 0,01");

        var quote = await _tesouro.GetLatestQuoteAsync(ticker);
        if (quote == null)
            throw new InvalidOperationException(
                $"{ticker} nao esta sendo negociado no Tesouro Direto (vencido ou fora de oferta)");

        if (request.Side == "long" && !quote.CanBuy)
            throw new InvalidOperationException(
                $"{quote.Bond.DisplayName} esta disponivel apenas para venda no Tesouro Direto");

        // Classifica o titulo como Renda Fixa na exposicao, mesmo antes do batch sincronizar o catalogo
        if (!await _db.Assets.AnyAsync(a => a.Ticker == ticker))
        {
            var asset = new Asset
            {
                Ticker = ticker,
                Name = quote.Bond.DisplayName,
                AssetClass = TesouroDireto.AssetClass,
                Sector = quote.Bond.Type.Name,
                Exchange = TesouroDireto.Exchange,
                Currency = PricingService.BaseCurrency,
                IsActive = 1
            };
            _db.Assets.Add(asset);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // A sincronizacao do catalogo cadastrou o titulo ao mesmo tempo: segue com o cadastro dela
                _db.Entry(asset).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// Deleta um trade e reverte a posicao e o caixa.
    /// </summary>
    public async Task DeleteTradeAsync(int tradeId)
    {
        await PortfolioLock.WaitAsync();
        try
        {
            var trade = await _db.Trades.FindAsync(tradeId)
                ?? throw new InvalidOperationException($"Trade {tradeId} nao encontrado");

            await using var transaction = await _db.Database.BeginTransactionAsync();

            _db.Trades.Remove(trade);
            await _db.SaveChangesAsync();

            await RebuildFundAsync(trade.FundId);
            await transaction.CommitAsync();

            _logger.LogInformation(
                "Trade deletado: {Side} {Qty} {Ticker} @ {Price} | Fundo: {FundId}",
                trade.Side, trade.Quantity, trade.Ticker, trade.Price, trade.FundId);
        }
        finally
        {
            PortfolioLock.Release();
        }
    }

    /// <summary>
    /// Reconstroi posicoes, P&L realizado e caixa de um fundo reaplicando todos os seus trades,
    /// eventos de titulos publicos (cupons e resgates) e proventos em ordem cronologica. Tambem atualiza a
    /// quantidade/valor de cada evento e remove eventos de ativos que o fundo nao tinha na vespera.
    /// Nao abre transacao nem pega a trava; o chamador decide.
    /// </summary>
    public async Task RebuildFundAsync(int fundId)
    {
        var cash = await _db.Cash.FindAsync(fundId)
            ?? throw new InvalidOperationException($"Caixa nao encontrado para fundo {fundId}");

        var fund = await _db.Funds.FindAsync(fundId)
            ?? throw new InvalidOperationException($"Fundo {fundId} nao encontrado");

        var trades = await _db.Trades.Where(t => t.FundId == fundId).ToListAsync();
        var events = await _db.TreasuryEvents.Where(e => e.FundId == fundId).ToListAsync();
        var state = FundLedger.Replay(fund.InitialCapital, trades, events);

        _db.Positions.RemoveRange(await _db.Positions.Where(p => p.FundId == fundId).ToListAsync());
        _db.RealizedPnl.RemoveRange(await _db.RealizedPnl.Where(r => r.FundId == fundId).ToListAsync());
        await _db.SaveChangesAsync();

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var (ticker, position) in state.Positions)
        {
            _db.Positions.Add(new Position
            {
                FundId = fundId,
                Ticker = ticker,
                Quantity = position.Quantity,
                AvgPrice = position.AvgPrice,
                Side = position.Side,
                UpdatedAt = now
            });
        }

        foreach (var realized in state.Realized)
        {
            _db.RealizedPnl.Add(new RealizedPnl
            {
                FundId = fundId,
                Ticker = realized.Ticker,
                Quantity = realized.Fill.Quantity,
                EntryPrice = realized.Fill.EntryPrice,
                ExitPrice = realized.Fill.ExitPrice,
                Pnl = realized.Fill.Pnl,
                Side = realized.Fill.Side,
                ClosedAt = realized.ClosedAt
            });
        }

        foreach (var ev in events)
        {
            // Provento vale para comprado (recebe) e vendido (paga); cupom/resgate so para quem tinha o titulo
            var quantity = state.EventQuantities.GetValueOrDefault(ev.Id);
            var participated = ev.Kind == TreasuryEventKinds.Dividend ? quantity != 0 : quantity > 0;
            if (!participated)
            {
                _db.TreasuryEvents.Remove(ev);
                continue;
            }

            ev.Quantity = quantity;
            ev.Total = quantity * ev.AmountPerUnit;
        }

        cash.Balance = state.Cash;
        cash.UpdatedAt = now;
        await _db.SaveChangesAsync();
    }

    private async Task UpdatePositionAsync(
        int fundId,
        string ticker,
        double signedQuantity,
        double price,
        string side,
        string executedAt)
    {
        var position = await _db.Positions
            .FirstOrDefaultAsync(p => p.FundId == fundId && p.Ticker == ticker);

        var current = position == null
            ? null
            : new PositionState(position.Quantity, position.AvgPrice, position.Side);

        var (next, realized) = PositionMath.Apply(current, signedQuantity, price, side);

        if (realized != null)
        {
            _db.RealizedPnl.Add(new RealizedPnl
            {
                FundId = fundId,
                Ticker = ticker,
                Quantity = realized.Quantity,
                EntryPrice = realized.EntryPrice,
                ExitPrice = realized.ExitPrice,
                Pnl = realized.Pnl,
                Side = realized.Side,
                ClosedAt = executedAt
            });
        }

        if (next == null)
        {
            if (position != null)
                _db.Positions.Remove(position);
            return;
        }

        if (position == null)
        {
            _db.Positions.Add(new Position
            {
                FundId = fundId,
                Ticker = ticker,
                Quantity = next.Quantity,
                AvgPrice = next.AvgPrice,
                Side = next.Side
            });
            return;
        }

        position.Quantity = next.Quantity;
        position.AvgPrice = next.AvgPrice;
        position.Side = next.Side;
        position.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
