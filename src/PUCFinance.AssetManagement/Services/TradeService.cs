using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;
using PUCFinance.AssetManagement.Models.DTOs;

namespace PUCFinance.AssetManagement.Services;

public class TradeService
{
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
    /// Executa um trade: busca preco atual, registra no log, atualiza posicao e caixa.
    /// </summary>
    public async Task<Trade> ExecuteTradeAsync(ExecuteTradeRequest request)
    {
        var fund = await _db.Funds.FindAsync(request.FundId)
            ?? throw new InvalidOperationException($"Fundo {request.FundId} nao encontrado");

        if (request.Quantity <= 0)
            throw new ArgumentException("Quantidade deve ser positiva");

        if (request.Side is not ("long" or "short"))
            throw new ArgumentException("Side deve ser 'long' ou 'short'");

        // Busca preco atual do Yahoo Finance
        var price = await _pricing.GetLatestPriceAsync(request.Ticker.Trim().ToUpper());
        if (price == null || price <= 0)
            throw new InvalidOperationException($"Nao foi possivel obter preco para {request.Ticker}. Verifique se o ticker esta correto.");

        var cash = await _db.Cash.FindAsync(request.FundId)
            ?? throw new InvalidOperationException($"Registro de caixa nao encontrado para fundo {request.FundId}");

        var signedQuantity = request.Side == "long" ? request.Quantity : -request.Quantity;
        var cashImpact = -(signedQuantity * price.Value);

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
            Price = price.Value,
            Thesis = request.Thesis,
            ExecutedBy = request.ExecutedBy
        };
        _db.Trades.Add(trade);

        // 2. Atualiza posicao
        await UpdatePositionAsync(request.FundId, trade.Ticker, signedQuantity, price.Value, request.Side, trade.ExecutedAt);

        // 3. Atualiza caixa
        cash.Balance += cashImpact;
        cash.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Trade executado: {Side} {Qty} {Ticker} @ {Price} (auto) | Fundo: {Fund} | Tese: {Thesis}",
            request.Side, request.Quantity, trade.Ticker, price.Value, fund.Name, request.Thesis);

        return trade;
    }

    /// <summary>
    /// Deleta um trade e reverte a posicao e o caixa.
    /// </summary>
    public async Task DeleteTradeAsync(int tradeId)
    {
        var trade = await _db.Trades.FindAsync(tradeId)
            ?? throw new InvalidOperationException($"Trade {tradeId} nao encontrado");

        var cash = await _db.Cash.FindAsync(trade.FundId)
            ?? throw new InvalidOperationException($"Caixa nao encontrado para fundo {trade.FundId}");

        var fund = await _db.Funds.FindAsync(trade.FundId)
            ?? throw new InvalidOperationException($"Fundo {trade.FundId} nao encontrado");

        var remainingTrades = await _db.Trades
            .Where(t => t.FundId == trade.FundId && t.Id != tradeId)
            .OrderBy(t => t.ExecutedAt)
            .ThenBy(t => t.Id)
            .ToListAsync();

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var fundPositions = await _db.Positions
            .Where(p => p.FundId == trade.FundId)
            .ToListAsync();
        _db.Positions.RemoveRange(fundPositions);

        var fundRealizedPnl = await _db.RealizedPnl
            .Where(r => r.FundId == trade.FundId)
            .ToListAsync();
        _db.RealizedPnl.RemoveRange(fundRealizedPnl);

        _db.Trades.Remove(trade);
        cash.Balance = fund.InitialCapital;
        cash.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        await _db.SaveChangesAsync();

        foreach (var remaining in remainingTrades)
        {
            var remainingSignedQuantity = remaining.Side == "long"
                ? remaining.Quantity
                : -remaining.Quantity;

            await UpdatePositionAsync(
                remaining.FundId,
                remaining.Ticker,
                remainingSignedQuantity,
                remaining.Price,
                remaining.Side,
                remaining.ExecutedAt);

            cash.Balance += -(remainingSignedQuantity * remaining.Price);
        }

        cash.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation(
            "Trade deletado: {Side} {Qty} {Ticker} @ {Price} | Fundo: {FundId}",
            trade.Side, trade.Quantity, trade.Ticker, trade.Price, trade.FundId);
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

        if (position == null)
        {
            _db.Positions.Add(new Position
            {
                FundId = fundId,
                Ticker = ticker,
                Quantity = signedQuantity,
                AvgPrice = price,
                Side = side
            });
            return;
        }

        var oldQuantity = position.Quantity;
        var newQuantity = oldQuantity + signedQuantity;

        if (Math.Sign(oldQuantity) != Math.Sign(signedQuantity))
        {
            var closedQuantity = Math.Min(Math.Abs(signedQuantity), Math.Abs(oldQuantity));
            var pnl = position.Side == "long"
                ? (price - position.AvgPrice) * closedQuantity
                : (position.AvgPrice - price) * closedQuantity;

            _db.RealizedPnl.Add(new RealizedPnl
            {
                FundId = fundId,
                Ticker = ticker,
                Quantity = closedQuantity,
                EntryPrice = position.AvgPrice,
                ExitPrice = price,
                Pnl = pnl,
                Side = position.Side,
                ClosedAt = executedAt
            });
        }

        if (Math.Abs(newQuantity) < 0.0001)
        {
            _db.Positions.Remove(position);
        }
        else if (Math.Sign(oldQuantity) == Math.Sign(signedQuantity))
        {
            position.AvgPrice = ((Math.Abs(oldQuantity) * position.AvgPrice) +
                                 (Math.Abs(signedQuantity) * price)) /
                                (Math.Abs(oldQuantity) + Math.Abs(signedQuantity));
            position.Quantity = newQuantity;
        }
        else if (Math.Sign(newQuantity) == Math.Sign(oldQuantity))
        {
            position.Quantity = newQuantity;
        }
        else
        {
            position.Quantity = newQuantity;
            position.AvgPrice = price;
            position.Side = newQuantity > 0 ? "long" : "short";
        }

        position.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
