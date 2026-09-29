using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Preenche os dias uteis passados sem NAV (dias em que o batch nao rodou) com fechamentos historicos.
/// So acrescenta linhas em nav_history/position_history: trades, posicoes, caixa e NAVs existentes
/// ficam como estao.
/// </summary>
public class NavBackfillService
{
    private readonly AppDbContext _db;
    private readonly PricingService _pricing;
    private readonly ILogger<NavBackfillService> _logger;

    public NavBackfillService(AppDbContext db, PricingService pricing, ILogger<NavBackfillService> logger)
    {
        _db = db;
        _pricing = pricing;
        _logger = logger;
    }

    /// <summary>Retorna quantos dias (somando todos os fundos) foram preenchidos.</summary>
    public async Task<int> FillMissingDaysAsync()
    {
        var market = await MarketSeries.LoadAsync(_db);
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var funds = await _db.Funds.AsNoTracking().Where(f => f.IsActive == 1).ToListAsync();

        var filled = 0;
        foreach (var fund in funds)
        {
            try
            {
                filled += await FillFundAsync(fund, market.Calendar, yesterday);
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "Preenchimento de NAV do fundo {Fund} falhou", fund.Name);
            }
        }

        return filled;
    }

    private async Task<int> FillFundAsync(Fund fund, BusinessCalendar calendar, string yesterday)
    {
        var existingDates = await _db.NavHistory
            .Where(n => n.FundId == fund.Id)
            .Select(n => n.Date)
            .ToListAsync();
        if (existingDates.Count == 0)
            return 0;

        var first = existingDates.Min(StringComparer.Ordinal)!;
        var existing = existingDates.ToHashSet();
        var missing = calendar.Days(first, yesterday).Where(d => !existing.Contains(d)).ToList();
        if (missing.Count == 0)
            return 0;

        // Precos historicos em BRL (rede) antes de pegar a trava
        var trades = await _db.Trades.AsNoTracking().Where(t => t.FundId == fund.Id).ToListAsync();
        var events = await _db.TreasuryEvents.AsNoTracking().Where(e => e.FundId == fund.Id).ToListAsync();
        var from = DateTime.ParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-10);
        var series = new Dictionary<string, DailySeries>();
        foreach (var ticker in trades.Select(t => t.Ticker).Distinct())
        {
            var history = await _pricing.GetBrlCloseHistoryAsync(ticker, from);
            if (history == null || history.Count == 0)
            {
                _logger.LogWarning("Fundo {Fund}: sem historico de precos de {Ticker}; dias sem NAV ficam para o proximo batch",
                    fund.Name, ticker);
                return 0;
            }

            series[ticker] = history;
        }

        await TradeService.PortfolioLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var navs = await _db.NavHistory.Where(n => n.FundId == fund.Id).ToListAsync();
            var shareByDate = navs.ToDictionary(n => n.Date, n => n.ShareValue);
            var inserted = new HashSet<string>();

            foreach (var day in missing)
            {
                if (shareByDate.ContainsKey(day))
                    continue;

                var state = FundLedger.Replay(fund.InitialCapital, trades, events, day);
                var previousDate = shareByDate.Keys
                    .Where(d => string.CompareOrdinal(d, day) < 0)
                    .DefaultIfEmpty()
                    .Max(StringComparer.Ordinal);

                var snapshots = new List<(string Ticker, PositionState State, double Price, double MarketValue, double? Return)>();
                var complete = true;
                foreach (var (ticker, position) in state.Positions)
                {
                    var price = series.GetValueOrDefault(ticker)?.ValueAt(day);
                    if (price is not > 0)
                    {
                        complete = false;
                        break;
                    }

                    var previousPrice = previousDate == null ? null : series[ticker].ValueAt(previousDate);
                    var coupons = previousDate == null
                        ? 0
                        : events.Where(e => e.Ticker == ticker && e.Kind == TreasuryEventKinds.Coupon
                                && string.CompareOrdinal(e.EventDate, previousDate) > 0
                                && string.CompareOrdinal(e.EventDate, day) <= 0)
                            .Sum(e => e.AmountPerUnit);
                    double? assetReturn = previousPrice > 0 ? (price.Value + coupons - previousPrice.Value) / previousPrice.Value : null;

                    snapshots.Add((ticker, position, price.Value, position.Quantity * price.Value, assetReturn));
                }

                // Nunca grava NAV com preco inventado: sem fechamento do dia, o dia fica de fora
                if (!complete)
                {
                    _logger.LogWarning("Fundo {Fund}: sem fechamento em {Day} para alguma posicao; dia nao preenchido", fund.Name, day);
                    continue;
                }

                var totalEquity = state.Cash + snapshots.Sum(s => s.MarketValue);
                var shareValue = fund.TotalShares > 0 ? totalEquity / fund.TotalShares : 0;
                double? dailyReturn = previousDate != null && shareByDate[previousDate] > 0
                    ? shareValue / shareByDate[previousDate] - 1
                    : null;

                _db.NavHistory.Add(new NavHistory
                {
                    FundId = fund.Id,
                    Date = day,
                    TotalEquity = totalEquity,
                    TotalShares = fund.TotalShares,
                    ShareValue = shareValue,
                    DailyReturn = dailyReturn,
                    CashBalance = state.Cash
                });

                foreach (var s in snapshots)
                {
                    var weight = totalEquity > 0 ? s.MarketValue / totalEquity : 0;
                    _db.PositionHistory.Add(new PositionHistory
                    {
                        FundId = fund.Id,
                        Ticker = s.Ticker,
                        Date = day,
                        Quantity = s.State.Quantity,
                        AvgPrice = s.State.AvgPrice,
                        Side = s.State.Side,
                        CurrentPrice = s.Price,
                        MarketValue = s.MarketValue,
                        UnrealizedPnl = (s.Price - s.State.AvgPrice) * s.State.Quantity,
                        DailyReturn = s.Return,
                        Contribution = (s.Return ?? 0) * weight,
                        Weight = weight
                    });
                }

                shareByDate[day] = shareValue;
                inserted.Add(day);
            }

            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            if (inserted.Count > 0)
                _logger.LogInformation("Fundo {Fund}: NAV preenchido em {Count} dia(s) sem registro ({First} a {Last})",
                    fund.Name, inserted.Count, inserted.Min(StringComparer.Ordinal), inserted.Max(StringComparer.Ordinal));

            return inserted.Count;
        }
        catch
        {
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            TradeService.PortfolioLock.Release();
        }
    }
}
