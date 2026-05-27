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
    /// Executa um trade: registra no log, atualiza posição e caixa.
    /// </summary>
    public async Task<Trade> ExecuteTradeAsync(ExecuteTradeRequest request)
    {
        // Validações
        var fund = await _db.Funds.FindAsync(request.FundId)
            ?? throw new InvalidOperationException($"Fundo {request.FundId} não encontrado");

        if (request.Quantity <= 0)
            throw new ArgumentException("Quantidade deve ser positiva");

        if (request.Price <= 0)
            throw new ArgumentException("Preço deve ser positivo");

        if (request.Side is not ("long" or "short"))
            throw new ArgumentException("Side deve ser 'long' ou 'short'");

        var cash = await _db.Cash.FindAsync(request.FundId)
            ?? throw new InvalidOperationException($"Registro de caixa não encontrado para fundo {request.FundId}");

        // Custo do trade
        // Long (compra) = gasta caixa | Short (venda a descoberto) = recebe caixa
        var signedQuantity = request.Side == "long" ? request.Quantity : -request.Quantity;
        var cashImpact = -(signedQuantity * request.Price); // compra: negativo, short: positivo

        if (cash.Balance + cashImpact < 0)
            throw new InvalidOperationException(
                $"Caixa insuficiente. Disponível: {cash.Balance:N2}, necessário: {-cashImpact:N2}");

        // 1. Registra o trade (log imutável)
        var trade = new Trade
        {
            FundId = request.FundId,
            Ticker = request.Ticker.Trim().ToUpper(),
            Side = request.Side,
            Quantity = request.Quantity,
            Price = request.Price,
            Thesis = request.Thesis,
            ExecutedBy = request.ExecutedBy
        };
        _db.Trades.Add(trade);

        // 2. Atualiza posição
        await UpdatePositionAsync(request.FundId, trade.Ticker, signedQuantity, request.Price, request.Side);

        // 3. Atualiza caixa
        cash.Balance += cashImpact;
        cash.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Trade executado: {Side} {Qty} {Ticker} @ {Price} | Fundo: {Fund} | Tese: {Thesis}",
            request.Side, request.Quantity, trade.Ticker, request.Price, fund.Name, request.Thesis);

        return trade;
    }

    /// <summary>
    /// Atualiza a posição existente ou cria uma nova.
    /// Trata: abertura, aumento, redução e fechamento de posição.
    /// </summary>
    private async Task UpdatePositionAsync(int fundId, string ticker, double signedQuantity, double price, string side)
    {
        var position = await _db.Positions
            .FirstOrDefaultAsync(p => p.FundId == fundId && p.Ticker == ticker);

        if (position == null)
        {
            // Nova posição
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

        // Fechando ou reduzindo posição? Registra P&L realizado
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
                Side = position.Side
            });
        }

        if (Math.Abs(newQuantity) < 0.0001)
        {
            // Posição zerada
            _db.Positions.Remove(position);
        }
        else if (Math.Sign(newQuantity) == Math.Sign(oldQuantity))
        {
            // Aumentando posição → recalcula preço médio
            position.AvgPrice = ((Math.Abs(oldQuantity) * position.AvgPrice) +
                                 (Math.Abs(signedQuantity) * price)) /
                                (Math.Abs(oldQuantity) + Math.Abs(signedQuantity));
            position.Quantity = newQuantity;
        }
        else
        {
            // Invertendo posição (fechou e abriu no outro lado)
            position.Quantity = newQuantity;
            position.AvgPrice = price;
            position.Side = newQuantity > 0 ? "long" : "short";
        }

        position.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
