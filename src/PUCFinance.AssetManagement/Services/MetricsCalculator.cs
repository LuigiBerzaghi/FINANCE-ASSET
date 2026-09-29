using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Grava o retrato diario das metricas de cada fundo (tabela metrics, usada no Excel).
/// A tela calcula as mesmas metricas em tempo real no FundsController; as formulas ficam em PerformanceMath.
/// </summary>
public class MetricsCalculator
{
    private readonly AppDbContext _db;
    private readonly ILogger<MetricsCalculator> _logger;

    public MetricsCalculator(AppDbContext db, ILogger<MetricsCalculator> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Calcula métricas de todos os fundos ativos para todos os períodos.
    /// </summary>
    public async Task CalculateAllAsync()
    {
        var funds = await _db.Funds
            .Where(f => f.IsActive == 1)
            .ToListAsync();

        var market = await MarketSeries.LoadAsync(_db);
        foreach (var fund in funds)
        {
            await CalculateFundMetricsAsync(fund.Id, market, fund.Benchmark);
        }
    }

    public async Task CalculateFundMetricsAsync(int fundId, MarketSeries? market = null, string? benchmark = null)
    {
        var navs = await _db.NavHistory
            .Where(n => n.FundId == fundId)
            .OrderBy(n => n.Date)
            .ToListAsync();

        if (navs.Count == 0) return;

        market ??= await MarketSeries.LoadAsync(_db);
        benchmark = FundBenchmarks.Resolve(benchmark ?? (await _db.Funds.FindAsync(fundId))?.Benchmark);
        var points = navs.Select(n => new SharePoint(n.Date, n.ShareValue)).ToList();
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        foreach (var (period, start) in MetricPeriods.For(DateTime.Today))
        {
            // MTD/YTD so existem se ha NAV no periodo
            if (start != null && !navs.Any(n => string.Compare(n.Date, start) >= 0))
                continue;

            await SaveMetric(fundId, today, period, benchmark, PerformanceMath.Compute(points, start, market, benchmark));
        }
    }

    private async Task SaveMetric(int fundId, string date, string period, string benchmark, PerformanceResult result)
    {
        var metric = await _db.Metrics
            .FirstOrDefaultAsync(m => m.FundId == fundId && m.Date == date && m.Period == period);

        if (metric == null)
        {
            metric = new Metric { FundId = fundId, Date = date, Period = period };
            _db.Metrics.Add(metric);
        }

        metric.CumulativeReturn = result.CumulativeReturn;
        metric.AnnualizedReturn = result.AnnualizedReturn;
        metric.Volatility = result.Volatility;
        metric.SharpeRatio = result.SharpeRatio;
        metric.MaxDrawdown = result.MaxDrawdown;
        metric.Alpha = result.Alpha;
        metric.Beta = result.Beta;
        metric.BenchmarkName = benchmark;

        await _db.SaveChangesAsync();
    }
}

/// <summary>Periodos das metricas: desde o inicio, mes corrente e ano corrente.</summary>
public static class MetricPeriods
{
    public static IEnumerable<(string Period, string? Start)> For(DateTime today) =>
    [
        ("inception", null),
        ("mtd", today.ToString("yyyy-MM-01")),
        ("ytd", today.ToString("yyyy-01-01")),
    ];
}
