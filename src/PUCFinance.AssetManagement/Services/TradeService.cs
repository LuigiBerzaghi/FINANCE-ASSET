using System.Globalization;
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

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>
    /// Limite de venda a descoberto: a soma das posicoes vendidas (a preco de mercado) nao pode passar de
    /// 100% do patrimonio do fundo. Vale para todos os fundos; reduzir ou zerar posicao vendida e sempre permitido.
    /// </summary>
    public const double MaxShortExposure = 1.0;

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
    /// Fecha total ou parcialmente uma posicao do fundo com um trade no sentido oposto (vende o comprado,
    /// recompra o vendido), ao preco atual. A quantidade nao pode passar da posicao: fechar nunca inverte.
    /// </summary>
    public async Task<Trade> ClosePositionAsync(ClosePositionRequest request, string? executedBy)
    {
        if (string.IsNullOrWhiteSpace(request.Thesis))
            throw new ArgumentException("Informe a justificativa do fechamento");

        var ticker = request.Ticker.Trim().ToUpper();
        var held = await _db.Positions
            .Where(p => p.FundId == request.FundId && p.Ticker == ticker)
            .Select(p => (double?)p.Quantity)
            .FirstOrDefaultAsync()
            ?? throw new InvalidOperationException($"O fundo nao tem posicao aberta em {ticker}");

        var side = held > 0 ? "short" : "long";
        return await ExecuteTradeAsync(
            new ExecuteTradeRequest(request.FundId, ticker, side, request.Quantity, request.Thesis.Trim(), executedBy,
                request.Amount),
            closeOnly: true);
    }

    /// <summary>
    /// Executa um trade: busca preco atual (convertido para BRL), registra no log, atualiza posicao e caixa.
    /// Com <paramref name="closeOnly"/>, o trade so pode reduzir ou zerar a posicao existente (fechamento).
    /// Com Amount (valor em BRL), a quantidade e calculada no preco da execucao, arredondada para baixo pela
    /// menor quantidade negociavel do ativo: o valor do trade nunca passa do valor pedido.
    /// </summary>
    public async Task<Trade> ExecuteTradeAsync(ExecuteTradeRequest request, bool closeOnly = false)
    {
        var fund = await _db.Funds.FindAsync(request.FundId)
            ?? throw new InvalidOperationException($"Fundo {request.FundId} nao encontrado");

        var byAmount = request.Amount.HasValue;
        if (byAmount && request.Quantity > 0)
            throw new ArgumentException("Informe a quantidade ou o valor, nao os dois");
        if (byAmount && !(request.Amount > 0))
            throw new ArgumentException("Valor deve ser positivo");
        if (!byAmount && request.Quantity <= 0)
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

        if (byAmount)
        {
            var step = await _pricing.QuantityStepAsync(ticker);
            var quantity = PricingService.QuantityForAmount(request.Amount!.Value, price, step);
            if (quantity < step)
                throw new InvalidOperationException(string.Format(PtBr,
                    "Valor abaixo do minimo para {0}: a menor quantidade negociavel ({1} {2}) custa {3:C2} agora",
                    ticker, step.ToString("0.########", PtBr), isTreasury ? "titulo" : "unidade", step * price));
            request = request with { Quantity = quantity };
        }

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
                        $"Venda maior que a posicao em {ticker}: o fundo tem {held.ToString("N2", PtBr)} titulo(s). Titulos publicos nao podem ser vendidos a descoberto.");
            }

            var signedQuantity = request.Side == "long" ? request.Quantity : -request.Quantity;

            // Fechamento: confere a posicao dentro da trava (outro trade pode ter mudado a posicao)
            if (closeOnly)
            {
                var current = await _db.Positions
                    .Where(p => p.FundId == request.FundId && p.Ticker == ticker)
                    .Select(p => (double?)p.Quantity)
                    .FirstOrDefaultAsync() ?? 0;
                if (current == 0 || Math.Sign(current) == Math.Sign(signedQuantity))
                    throw new InvalidOperationException($"O fundo nao tem posicao aberta em {ticker} para fechar");

                // Por valor: o valor da posicao inteira (em centavos, como aparece na tela) zera a posicao,
                // mesmo que o preco tenha casas alem dos centavos
                if (byAmount && request.Quantity < Math.Abs(current)
                    && request.Amount >= Math.Round(Math.Abs(current) * price, 2) - 0.005)
                {
                    request = request with { Quantity = Math.Abs(current) };
                    signedQuantity = request.Side == "long" ? request.Quantity : -request.Quantity;
                }
                if (request.Quantity > Math.Abs(current) + 1e-9)
                    throw new InvalidOperationException(byAmount
                        ? string.Format(PtBr,
                            "Valor maior que a posicao em {0}: a posicao inteira vale {1:C2} agora. Use Zerar para fechar tudo.",
                            ticker, Math.Abs(current) * price)
                        : $"Quantidade maior que a posicao em {ticker}: o fundo tem {Math.Abs(current).ToString("N2", PtBr)}. Fechar nao pode inverter a posicao.");
            }

            // Venda a descoberto: o fundo nao pode ficar vendido em mais que 100% do patrimonio
            if (signedQuantity < 0)
                await EnsureShortLimitAsync(request.FundId, ticker, signedQuantity, price, cash.Balance);

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

    /// <summary>
    /// Recusa a venda que aumentaria a exposicao vendida do fundo alem de MaxShortExposure do patrimonio.
    /// O ativo negociado e avaliado no preco da execucao; as demais posicoes, no ultimo preco do batch.
    /// A venda em si nao muda o patrimonio (o caixa sobe e a posicao vendida desce do mesmo valor).
    /// </summary>
    private async Task EnsureShortLimitAsync(int fundId, string ticker, double signedQuantity, double price, double cash)
    {
        var positions = await _db.Positions.AsNoTracking().Where(p => p.FundId == fundId).ToListAsync();
        var current = positions.FirstOrDefault(p => p.Ticker == ticker)?.Quantity ?? 0;
        var others = positions.Where(p => p.Ticker != ticker).ToList();

        var equity = cash + current * price + others.Sum(p => p.Quantity * (p.CurrentPrice ?? p.AvgPrice));
        var otherShorts = others.Where(p => p.Quantity < 0).Sum(p => -p.Quantity * (p.CurrentPrice ?? p.AvgPrice));
        var shortBefore = otherShorts + Math.Max(0, -current) * price;
        var shortAfter = otherShorts + Math.Max(0, -(current + signedQuantity)) * price;

        // Venda que so reduz posicao comprada (ou nao aumenta o vendido) nao tem limite
        if (shortAfter <= shortBefore + 1e-9)
            return;

        var limit = Math.Max(0, equity) * MaxShortExposure;
        if (shortAfter <= limit + 0.005)
            return;

        var available = Math.Max(0, limit - shortBefore) + Math.Max(0, current) * price;
        var share = equity > 0 ? (shortAfter / equity * 100).ToString("N0", PtBr) + "% do patrimonio" : "com patrimonio zerado ou negativo";
        throw new InvalidOperationException(string.Format(PtBr,
            "Venda a descoberto acima do limite: o fundo ficaria vendido em {0:C2} ({1}). O limite e {2:N0}% do patrimonio ({3:C2}). Maximo para vender agora: {4:C2}.",
            shortAfter, share, MaxShortExposure * 100, limit, available));
    }

    /// <summary>
    /// Dados para a tela calcular quanto ainda da para vender de um ativo sem passar do limite de venda a
    /// descoberto (no preco que a tela tiver): caixa, valor e vendido das demais posicoes e a quantidade do ativo.
    /// </summary>
    public async Task<ShortLimitResponse> GetShortLimitAsync(int fundId, string? ticker)
    {
        ticker = ticker?.Trim().ToUpper() ?? string.Empty;
        var cash = (await _db.Cash.AsNoTracking().FirstOrDefaultAsync(c => c.FundId == fundId))?.Balance ?? 0;
        var positions = await _db.Positions.AsNoTracking().Where(p => p.FundId == fundId).ToListAsync();
        var others = positions.Where(p => p.Ticker != ticker).ToList();

        return new ShortLimitResponse(
            MaxShortExposure,
            cash,
            others.Sum(p => p.Quantity * (p.CurrentPrice ?? p.AvgPrice)),
            others.Where(p => p.Quantity < 0).Sum(p => -p.Quantity * (p.CurrentPrice ?? p.AvgPrice)),
            positions.FirstOrDefault(p => p.Ticker == ticker)?.Quantity ?? 0);
    }

    private async Task ValidateTreasuryTradeAsync(string ticker, ExecuteTradeRequest request)
    {
        // Por valor a quantidade e calculada depois, ja em multiplos de 0,01
        var cents = request.Quantity / TesouroDireto.QuantityStep;
        if (!request.Amount.HasValue && Math.Abs(cents - Math.Round(cents)) > 1e-6)
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
