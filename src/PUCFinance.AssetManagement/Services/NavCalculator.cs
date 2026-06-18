using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

public class NavCalculator
{
    private readonly AppDbContext _db;
    private readonly ILogger<NavCalculator> _logger;

    public NavCalculator(AppDbContext db, ILogger<NavCalculator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task CalculateAllAsync()
    {
        var funds = await _db.Funds
            .Where(f => f.IsActive == 1)
            .ToListAsync();

        foreach (var fund in funds)
        {
            await CalculateFundNavAsync(fund.Id);
        }

        _logger.LogInformation("NAV recalculado para {Count} fundos", funds.Count);
    }

    public async Task CalculateFundNavAsync(int fundId)
    {
        var fund = await _db.Funds.FindAsync(fundId)
            ?? throw new InvalidOperationException($"Fundo {fundId} nao encontrado");

        var positions = await _db.Positions
            .Where(p => p.FundId == fundId)
            .ToListAsync();

        var cash = await _db.Cash.FindAsync(fundId);
        var cashBalance = cash?.Balance ?? fund.InitialCapital;

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var totalMarketValue = 0.0;

        // Atualiza cada posicao com preco atual
        foreach (var pos in positions)
        {
            var priceRecord = await _db.Prices
                .Where(p => p.Ticker == pos.Ticker)
                .OrderByDescending(p => p.Date)
                .FirstOrDefaultAsync();

            if (priceRecord != null)
            {
                pos.CurrentPrice = priceRecord.Close;
                pos.MarketValue = pos.Quantity * priceRecord.Close;
                pos.UnrealizedPnl = (priceRecord.Close - pos.AvgPrice) * pos.Quantity;
                pos.UpdatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                pos.CurrentPrice = pos.AvgPrice;
                pos.MarketValue = pos.Quantity * pos.AvgPrice;
                pos.UnrealizedPnl = 0;
            }

            totalMarketValue += pos.MarketValue ?? 0;
        }

        var totalEquity = totalMarketValue + cashBalance;
        var shareValue = totalEquity / fund.TotalShares;

        // Retorno diario (vs dia anterior)
        var previousNav = await _db.NavHistory
            .Where(n => n.FundId == fundId && n.Date != today)
            .OrderByDescending(n => n.Date)
            .FirstOrDefaultAsync();

        double? dailyReturn = null;
        if (previousNav != null && previousNav.ShareValue > 0)
        {
            dailyReturn = (shareValue - previousNav.ShareValue) / previousNav.ShareValue;
        }

        var currentTickers = positions.Select(p => p.Ticker).ToList();
        var todaysHistory = await _db.PositionHistory
            .Where(h => h.FundId == fundId && h.Date == today)
            .ToListAsync();
        var staleHistory = todaysHistory
            .Where(h => !currentTickers.Contains(h.Ticker))
            .ToList();
        _db.PositionHistory.RemoveRange(staleHistory);

        // Snapshot de cada posicao no position_history
        foreach (var pos in positions)
        {
            // Retorno diario do ativo
            var previousPosPrice = await _db.Prices
                .Where(p => p.Ticker == pos.Ticker && p.Date != today)
                .OrderByDescending(p => p.Date)
                .FirstOrDefaultAsync();

            double? assetDailyReturn = null;
            if (previousPosPrice != null && previousPosPrice.Close > 0 && pos.CurrentPrice.HasValue)
            {
                assetDailyReturn = (pos.CurrentPrice.Value - previousPosPrice.Close) / previousPosPrice.Close;
            }

            var weight = totalEquity > 0 ? (pos.MarketValue ?? 0) / totalEquity : 0;
            var contribution = (assetDailyReturn ?? 0) * weight;

            var existingHist = await _db.PositionHistory
                .FirstOrDefaultAsync(h => h.FundId == fundId && h.Ticker == pos.Ticker && h.Date == today);

            if (existingHist != null)
            {
                existingHist.Quantity = pos.Quantity;
                existingHist.AvgPrice = pos.AvgPrice;
                existingHist.Side = pos.Side;
                existingHist.CurrentPrice = pos.CurrentPrice;
                existingHist.MarketValue = pos.MarketValue;
                existingHist.UnrealizedPnl = pos.UnrealizedPnl;
                existingHist.DailyReturn = assetDailyReturn;
                existingHist.Contribution = contribution;
                existingHist.Weight = weight;
            }
            else
            {
                _db.PositionHistory.Add(new PositionHistory
                {
                    FundId = fundId,
                    Ticker = pos.Ticker,
                    Date = today,
                    Quantity = pos.Quantity,
                    AvgPrice = pos.AvgPrice,
                    Side = pos.Side,
                    CurrentPrice = pos.CurrentPrice,
                    MarketValue = pos.MarketValue,
                    UnrealizedPnl = pos.UnrealizedPnl,
                    DailyReturn = assetDailyReturn,
                    Contribution = contribution,
                    Weight = weight,
                });
            }
        }

        // Upsert nav_history
        var existingNav = await _db.NavHistory
            .FirstOrDefaultAsync(n => n.FundId == fundId && n.Date == today);

        if (existingNav != null)
        {
            existingNav.TotalEquity = totalEquity;
            existingNav.ShareValue = shareValue;
            existingNav.DailyReturn = dailyReturn;
            existingNav.CashBalance = cashBalance;
        }
        else
        {
            _db.NavHistory.Add(new NavHistory
            {
                FundId = fundId,
                Date = today,
                TotalEquity = totalEquity,
                TotalShares = fund.TotalShares,
                ShareValue = shareValue,
                DailyReturn = dailyReturn,
                CashBalance = cashBalance
            });
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "NAV calculado: {Fund} | Patrimonio: {Equity:N2} | Cota: {Share:F4} | Retorno: {Return:P2}",
            fund.Name, totalEquity, shareValue, dailyReturn ?? 0);
    }
}
