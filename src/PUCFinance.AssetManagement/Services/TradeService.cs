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
    private readonly ILogger<TradeService> _logger;

    public TradeService(AppDbContext db, PricingService pricing, ILogger<TradeService> logger)
    {
        _db = db;
        _pricing = pricing;
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

        // Busca preco atual do Yahoo Finance, ja convertido para BRL
        var quote = await _pricing.GetQuoteAsync(request.Ticker.Trim().ToUpper());
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

            var signedQuantity = request.Side == "long" ? request.Quantity : -request.Quantity;
            var cashImpact = -(signedQuantity * price);

            if (cash.Balance + cashImpact < 0)
                throw new InvalidOperationException(
                    $"Caixa insuficiente. Disponivel: {cash.Balance:N2}, necessario: {-cashImpact:N2}");

            // 1. Registra o trade
            var trade = new Trade
            {
                FundId = request.FundId,
                Ticker = request.Ticker.Trim().ToUpper(),
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
    /// Reconstroi posicoes, P&L realizado e caixa de um fundo reaplicando todos os seus trades
    /// em ordem cronologica. Nao abre transacao nem pega a trava; o chamador decide.
    /// </summary>
    public async Task RebuildFundAsync(int fundId)
    {
        var cash = await _db.Cash.FindAsync(fundId)
            ?? throw new InvalidOperationException($"Caixa nao encontrado para fundo {fundId}");

        var fund = await _db.Funds.FindAsync(fundId)
            ?? throw new InvalidOperationException($"Fundo {fundId} nao encontrado");

        var trades = await _db.Trades
            .Where(t => t.FundId == fundId)
            .OrderBy(t => t.ExecutedAt)
            .ThenBy(t => t.Id)
            .ToListAsync();

        var fundPositions = await _db.Positions
            .Where(p => p.FundId == fundId)
            .ToListAsync();
        _db.Positions.RemoveRange(fundPositions);

        var fundRealizedPnl = await _db.RealizedPnl
            .Where(r => r.FundId == fundId)
            .ToListAsync();
        _db.RealizedPnl.RemoveRange(fundRealizedPnl);

        cash.Balance = fund.InitialCapital;
        await _db.SaveChangesAsync();

        foreach (var trade in trades)
        {
            var signedQuantity = trade.Side == "long" ? trade.Quantity : -trade.Quantity;

            await UpdatePositionAsync(
                trade.FundId,
                trade.Ticker,
                signedQuantity,
                trade.Price,
                trade.Side,
                trade.ExecutedAt);

            cash.Balance += -(signedQuantity * trade.Price);

            // Salva a cada trade: UpdatePositionAsync consulta o banco e nao enxerga posicoes so adicionadas
            await _db.SaveChangesAsync();
        }

        cash.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
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
