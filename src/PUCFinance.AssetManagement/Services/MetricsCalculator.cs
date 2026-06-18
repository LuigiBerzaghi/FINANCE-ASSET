using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

public class MetricsCalculator
{
    private readonly AppDbContext _db;
    private readonly ILogger<MetricsCalculator> _logger;

    // CDI anualizado aproximado (atualizar conforme Selic muda)
    // TODO: puxar da API do Bacen automaticamente
    private const double AnnualRiskFreeRate = 0.1450; // ~14.50% aa (Selic maio/2026)

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

        foreach (var fund in funds)
        {
            await CalculateFundMetricsAsync(fund.Id);
        }
    }

    public async Task CalculateFundMetricsAsync(int fundId)
    {
        var navs = await _db.NavHistory
            .Where(n => n.FundId == fundId)
            .OrderBy(n => n.Date)
            .ToListAsync();

        if (navs.Count == 0) return;

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var dailyReturns = navs
            .Where(n => n.DailyReturn.HasValue)
            .Select(n => n.DailyReturn!.Value)
            .ToList();


        // Desde o início
        await SaveMetric(fundId, today, "inception", dailyReturns, navs);

        // MTD
        var mtdStart = DateTime.Today.ToString("yyyy-MM-01");
        var mtdNavs = BuildPeriodNavs(navs, mtdStart);
        var mtdReturns = PeriodReturns(navs, mtdStart);
        if (HasNavInPeriod(navs, mtdStart))
            await SaveMetric(fundId, today, "mtd", mtdReturns, mtdNavs);

        // YTD
        var ytdStart = DateTime.Today.ToString("yyyy-01-01");
        var ytdNavs = BuildPeriodNavs(navs, ytdStart);
        var ytdReturns = PeriodReturns(navs, ytdStart);
        if (HasNavInPeriod(navs, ytdStart))
            await SaveMetric(fundId, today, "ytd", ytdReturns, ytdNavs);
    }

    private async Task SaveMetric(int fundId, string date, string period,
        List<double> dailyReturns, List<NavHistory> navs)
    {
        var cumReturn = CumulativeReturn(navs);
        var annualizedReturn = AnnualizedReturn(dailyReturns, cumReturn);
        var vol = Volatility(dailyReturns);
        var sharpe = SharpeRatio(annualizedReturn, vol);
        var mdd = MaxDrawdown(navs);
        var (alpha, beta) = await AlphaBeta(fundId, navs);

        var existing = await _db.Metrics
            .FirstOrDefaultAsync(m => m.FundId == fundId && m.Date == date && m.Period == period);

        if (existing != null)
        {
            existing.CumulativeReturn = cumReturn;
            existing.AnnualizedReturn = annualizedReturn;
            existing.Volatility = vol;
            existing.SharpeRatio = sharpe;
            existing.MaxDrawdown = mdd;
            existing.Alpha = alpha;
            existing.Beta = beta;
            existing.BenchmarkName = "IBOVESPA";
        }
        else
        {
            _db.Metrics.Add(new Metric
            {
                FundId = fundId,
                Date = date,
                Period = period,
                CumulativeReturn = cumReturn,
                AnnualizedReturn = annualizedReturn,
                Volatility = vol,
                SharpeRatio = sharpe,
                MaxDrawdown = mdd,
                Alpha = alpha,
                Beta = beta,
                BenchmarkName = "IBOVESPA"
            });
        }

        await _db.SaveChangesAsync();
    }

    // ── Cálculos ────────────────────────────────────────────

    private static List<NavHistory> BuildPeriodNavs(List<NavHistory> navs, string periodStart)
    {
        var periodNavs = navs
            .Where(n => string.Compare(n.Date, periodStart) >= 0)
            .ToList();

        var baseNav = navs
            .Where(n => string.Compare(n.Date, periodStart) < 0)
            .LastOrDefault();

        if (baseNav != null)
            periodNavs.Insert(0, baseNav);

        return periodNavs;
    }

    private static List<double> PeriodReturns(List<NavHistory> navs, string periodStart)
    {
        return navs
            .Where(n => string.Compare(n.Date, periodStart) >= 0 && n.DailyReturn.HasValue)
            .Select(n => n.DailyReturn!.Value)
            .ToList();
    }

    private static bool HasNavInPeriod(List<NavHistory> navs, string periodStart)
    {
        return navs.Any(n => string.Compare(n.Date, periodStart) >= 0);
    }

    /// <summary>
    /// Retorno acumulado = (cota_final / cota_inicial) - 1
    /// </summary>
    private static double CumulativeReturn(List<NavHistory> navs)
    {
        if (navs.Count < 2) return 0;
        var first = navs.First().ShareValue;
        var last = navs.Last().ShareValue;
        return first > 0 ? (last / first) - 1 : 0;
    }

    /// <summary>
    /// Volatilidade anualizada = desvio_padrão(retornos_diários) × √252
    /// </summary>
    private static double Volatility(List<double> returns)
    {
        if (returns.Count < 2) return 0;
        var mean = returns.Average();
        var variance = returns.Sum(r => Math.Pow(r - mean, 2)) / (returns.Count - 1);
        return Math.Sqrt(variance) * Math.Sqrt(252);
    }

    private static double AnnualizedReturn(List<double> returns, double cumulativeReturn)
    {
        if (returns.Count == 0) return 0;
        if (cumulativeReturn <= -1) return -1;

        return Math.Pow(1 + cumulativeReturn, 252.0 / returns.Count) - 1;
    }

    /// <summary>
    /// Sharpe = (retorno_anualizado - risk_free) / volatilidade
    /// </summary>
    private static double SharpeRatio(double annualizedReturn, double vol)
    {
        if (vol == 0) return 0;
        return (annualizedReturn - AnnualRiskFreeRate) / vol;
    }

    /// <summary>
    /// Max Drawdown = maior queda percentual do pico ao vale.
    /// </summary>
    private static double MaxDrawdown(List<NavHistory> navs)
    {
        if (navs.Count < 2) return 0;

        var peak = navs.First().ShareValue;
        var maxDd = 0.0;

        foreach (var nav in navs)
        {
            if (nav.ShareValue > peak)
                peak = nav.ShareValue;

            var drawdown = (peak - nav.ShareValue) / peak;
            if (drawdown > maxDd)
                maxDd = drawdown;
        }

        return -maxDd; // negativo por convenção
    }

    /// <summary>
    /// Alpha e Beta via regressão linear simples contra o benchmark.
    /// Beta = Cov(Rf, Rb) / Var(Rb)
    /// Alpha = Rf_mean - Beta × Rb_mean (anualizado)
    /// </summary>
    private async Task<(double? alpha, double? beta)> AlphaBeta(
        int fundId, List<NavHistory> navs)
    {
        // Pega retornos do benchmark nas mesmas datas
        var dates = navs.Where(n => n.DailyReturn.HasValue).Select(n => n.Date).ToList();

        var benchmarks = await _db.Benchmarks
            .Where(b => b.Name == "IBOVESPA" && dates.Contains(b.Date) && b.DailyReturn.HasValue)
            .OrderBy(b => b.Date)
            .ToListAsync();

        if (benchmarks.Count < 10) return (null, null); // precisa de dados suficientes

        // Alinha por data
        var benchDict = benchmarks.ToDictionary(b => b.Date, b => b.DailyReturn!.Value);
        var pairedFund = new List<double>();
        var pairedBench = new List<double>();

        var fundDict = navs
            .Where(n => n.DailyReturn.HasValue)
            .ToDictionary(n => n.Date, n => n.DailyReturn!.Value);

        foreach (var date in dates)
        {
            if (benchDict.TryGetValue(date, out var br) && fundDict.TryGetValue(date, out var fr))
            {
                pairedFund.Add(fr);
                pairedBench.Add(br);
            }
        }

        if (pairedFund.Count < 10) return (null, null);

        var meanF = pairedFund.Average();
        var meanB = pairedBench.Average();

        var covariance = pairedFund.Zip(pairedBench, (f, b) => (f - meanF) * (b - meanB)).Sum()
                         / (pairedFund.Count - 1);
        var varianceB = pairedBench.Sum(b => Math.Pow(b - meanB, 2)) / (pairedBench.Count - 1);

        if (varianceB == 0) return (null, null);

        var beta = covariance / varianceB;
        var alpha = (meanF - beta * meanB) * 252; // anualizado

        return (alpha, beta);
    }
}
