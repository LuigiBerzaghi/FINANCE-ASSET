using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Recalcula o historico de um fundo (nav_history e position_history dos dias anteriores a hoje)
/// reaplicando os trades e precificando cada dia pelo fechamento historico em BRL.
/// O dia de hoje continua sendo calculado pelo NavCalculator no batch.
/// </summary>
public class NavHistoryRebuilder
{
    private readonly AppDbContext _db;
    private readonly PricingService _pricing;
    private readonly ILogger<NavHistoryRebuilder> _logger;

    public NavHistoryRebuilder(AppDbContext db, PricingService pricing, ILogger<NavHistoryRebuilder> logger)
    {
        _db = db;
        _pricing = pricing;
        _logger = logger;
    }

    /// <summary>
    /// Busca no Yahoo as series de fechamento em BRL de todos os tickers negociados pelo fundo,
    /// cobrindo todo o historico de NAV. Tickers sem dados ficam null (usa o preco medio).
    /// </summary>
    public async Task<Dictionary<string, DailySeries?>> LoadSeriesAsync(int fundId)
    {
        var tickers = await _db.Trades
            .Where(t => t.FundId == fundId)
            .Select(t => t.Ticker)
            .Distinct()
            .ToListAsync();

        var firstNavDate = await _db.NavHistory
            .Where(n => n.FundId == fundId)
            .MinAsync(n => (string?)n.Date);
        var firstTradeDate = await _db.Trades
            .Where(t => t.FundId == fundId)
            .MinAsync(t => (string?)t.ExecutedAt);

        var start = new[] { firstNavDate, firstTradeDate }
            .Select(ParseDate)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .DefaultIfEmpty(DateTime.Today)
            .Min()
            .AddDays(-10);

        var result = new Dictionary<string, DailySeries?>();
        foreach (var ticker in tickers)
        {
            var series = await _pricing.GetBrlCloseHistoryAsync(ticker, start);
            if (series == null || series.Count == 0)
            {
                _logger.LogWarning("Historico de {Ticker} indisponivel; dias passados usam o preco medio", ticker);
                series = null;
            }

            result[ticker] = series;
        }

        return result;
    }

    /// <summary>
    /// Reescreve nav_history e position_history do fundo para as datas anteriores a hoje.
    /// Nao abre transacao; o chamador decide.
    /// </summary>
    public async Task RebuildAsync(int fundId, IReadOnlyDictionary<string, DailySeries?> seriesByTicker)
    {
        var fund = await _db.Funds.FindAsync(fundId)
            ?? throw new InvalidOperationException($"Fundo {fundId} nao encontrado");

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var navs = await _db.NavHistory
            .Where(n => n.FundId == fundId && string.Compare(n.Date, today) < 0)
            .OrderBy(n => n.Date)
            .ToListAsync();

        if (navs.Count == 0)
            return;

        var trades = await _db.Trades
            .Where(t => t.FundId == fundId)
            .OrderBy(t => t.ExecutedAt)
            .ThenBy(t => t.Id)
            .ToListAsync();

        await _db.PositionHistory
            .Where(h => h.FundId == fundId && string.Compare(h.Date, today) < 0)
            .ExecuteDeleteAsync();

        var positions = new SortedDictionary<string, PositionState>(StringComparer.Ordinal);
        var cash = fund.InitialCapital;
        var tradeIndex = 0;
        string? previousDate = null;
        double? previousShareValue = null;

        foreach (var nav in navs)
        {
            // Trades executados ate o fim do dia entram no NAV daquele dia
            while (tradeIndex < trades.Count && string.CompareOrdinal(TradeDate(trades[tradeIndex]), nav.Date) <= 0)
            {
                var trade = trades[tradeIndex++];
                var signedQuantity = trade.Side == "long" ? trade.Quantity : -trade.Quantity;
                var (next, _) = PositionMath.Apply(
                    positions.GetValueOrDefault(trade.Ticker), signedQuantity, trade.Price, trade.Side);

                if (next == null)
                    positions.Remove(trade.Ticker);
                else
                    positions[trade.Ticker] = next;

                cash += -(signedQuantity * trade.Price);
            }

            var snapshots = positions.Select(kv =>
            {
                var series = seriesByTicker.GetValueOrDefault(kv.Key);
                var close = series?.ValueAt(nav.Date);
                var price = close ?? kv.Value.AvgPrice;
                var previousClose = previousDate == null ? null : series?.ValueAt(previousDate);
                double? dailyReturn = close.HasValue && previousClose > 0
                    ? (close.Value - previousClose.Value) / previousClose.Value
                    : null;

                return (Ticker: kv.Key, State: kv.Value, Price: price,
                    MarketValue: kv.Value.Quantity * price, DailyReturn: dailyReturn);
            }).ToList();

            var totalEquity = cash + snapshots.Sum(s => s.MarketValue);
            var shareValue = fund.TotalShares > 0 ? totalEquity / fund.TotalShares : 0;

            nav.TotalEquity = totalEquity;
            nav.TotalShares = fund.TotalShares;
            nav.ShareValue = shareValue;
            nav.CashBalance = cash;
            if (previousShareValue > 0)
                nav.DailyReturn = (shareValue - previousShareValue.Value) / previousShareValue.Value;

            foreach (var s in snapshots)
            {
                var weight = totalEquity > 0 ? s.MarketValue / totalEquity : 0;
                _db.PositionHistory.Add(new PositionHistory
                {
                    FundId = fundId,
                    Ticker = s.Ticker,
                    Date = nav.Date,
                    Quantity = s.State.Quantity,
                    AvgPrice = s.State.AvgPrice,
                    Side = s.State.Side,
                    CurrentPrice = s.Price,
                    MarketValue = s.MarketValue,
                    UnrealizedPnl = (s.Price - s.State.AvgPrice) * s.State.Quantity,
                    DailyReturn = s.DailyReturn,
                    Contribution = (s.DailyReturn ?? 0) * weight,
                    Weight = weight
                });
            }

            previousDate = nav.Date;
            previousShareValue = shareValue;
        }

        // Metricas de dias passados foram calculadas sobre o historico antigo; o batch recalcula a de hoje
        await _db.Metrics
            .Where(m => m.FundId == fundId && string.Compare(m.Date, today) < 0)
            .ExecuteDeleteAsync();

        await _db.SaveChangesAsync();

        _logger.LogInformation("Historico recalculado: {Fund} | {Days} dias", fund.Name, navs.Count);
    }

    /// <summary>
    /// Substitui os precos guardados dos tickers pelos fechamentos em BRL nas datas de NAV
    /// (de qualquer fundo) anteriores a hoje. A tabela de precos e compartilhada entre fundos.
    /// </summary>
    public async Task ReplacePricesAsync(
        IEnumerable<string> tickers,
        IReadOnlyDictionary<string, DailySeries?> seriesByTicker)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var dates = await _db.NavHistory
            .Where(n => string.Compare(n.Date, today) < 0)
            .Select(n => n.Date)
            .Distinct()
            .ToListAsync();

        foreach (var ticker in tickers.Distinct())
        {
            await _db.Prices.Where(p => p.Ticker == ticker).ExecuteDeleteAsync();

            var series = seriesByTicker.GetValueOrDefault(ticker);
            if (series == null)
                continue;

            foreach (var date in dates)
            {
                var close = series.ValueAt(date);
                if (close.HasValue)
                    await _pricing.UpsertPriceAsync(ticker, date, close.Value);
            }
        }

        await _db.SaveChangesAsync();
    }

    private static string TradeDate(Trade trade) =>
        trade.ExecutedAt.Length >= 10 ? trade.ExecutedAt[..10] : trade.ExecutedAt;

    /// <summary>Le a parte yyyy-MM-dd de uma data ou timestamp.</summary>
    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 10)
            return null;

        return DateTime.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
