using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>Valor da cota numa data (yyyy-MM-dd).</summary>
public sealed record SharePoint(string Date, double ShareValue);

/// <summary>Metricas de um periodo. Campos null = historico insuficiente ou dado de mercado ausente.</summary>
public sealed record PerformanceResult(
    double CumulativeReturn,
    double? AnnualizedReturn,
    double? Volatility,
    double? SharpeRatio,
    double MaxDrawdown,
    double? Alpha,
    double? Beta,
    double? RiskFreeRate,
    int Observations,
    int BusinessDays);

/// <summary>
/// Calendario de dias uteis a partir das datas do CDI (o Bacen publica um valor por dia util,
/// ja sem feriados). Fora do periodo coberto pelo CDI (antes do primeiro ou depois do ultimo
/// publicado), considera dias uteis os dias de semana.
/// </summary>
public sealed class BusinessCalendar
{
    private readonly List<string> _cdiDates;
    private readonly HashSet<string> _cdiSet;

    public BusinessCalendar(IEnumerable<string> cdiDates)
    {
        _cdiDates = cdiDates.Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        _cdiSet = _cdiDates.ToHashSet();
    }

    private string? First => _cdiDates.Count > 0 ? _cdiDates[0] : null;
    private string? Last => _cdiDates.Count > 0 ? _cdiDates[^1] : null;

    public bool IsBusinessDay(string date)
    {
        if (First != null && string.CompareOrdinal(date, First) >= 0 && string.CompareOrdinal(date, Last) <= 0)
            return _cdiSet.Contains(date);

        return Parse(date).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    /// <summary>Ultimo dia util menor ou igual a data (sabado → sexta).</summary>
    public string ToBusinessDay(string date)
    {
        var day = Parse(date);
        for (var i = 0; i < 15; i++)
        {
            var iso = Iso(day);
            if (IsBusinessDay(iso))
                return iso;
            day = day.AddDays(-1);
        }

        return date;
    }

    /// <summary>Dias uteis no intervalo (from, to].</summary>
    public IEnumerable<string> Days(string from, string to)
    {
        for (var day = Parse(from).AddDays(1); day <= Parse(to); day = day.AddDays(1))
        {
            var iso = Iso(day);
            if (IsBusinessDay(iso))
                yield return iso;
        }
    }

    public int Count(string from, string to) => Days(from, to).Count();

    private static DateTime Parse(string date) =>
        DateTime.ParseExact(date[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Iso(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Series de mercado usadas nas metricas: CDI diario (taxa livre de risco e calendario) e IBOVESPA.</summary>
public sealed record MarketSeries(BusinessCalendar Calendar, IReadOnlyDictionary<string, double> CdiDaily, DailySeries? Ibovespa)
{
    public static async Task<MarketSeries> LoadAsync(AppDbContext db)
    {
        var cdi = await db.Benchmarks
            .Where(b => b.Name == "CDI" && b.DailyReturn.HasValue)
            .Select(b => new { b.Date, Rate = b.DailyReturn!.Value })
            .ToListAsync();

        var ibov = await db.Benchmarks
            .Where(b => b.Name == "IBOVESPA" && b.Value > 0)
            .Select(b => new { b.Date, b.Value })
            .ToListAsync();

        return new MarketSeries(
            new BusinessCalendar(cdi.Select(c => c.Date)),
            cdi.GroupBy(c => c.Date).ToDictionary(g => g.Key, g => g.Last().Rate),
            ibov.Count > 0 ? new DailySeries(ibov.Select(i => new KeyValuePair<string, double>(i.Date, i.Value))) : null);
    }
}

/// <summary>
/// Metricas de desempenho a partir da serie de cotas. Cada observacao e o intervalo entre dois pontos
/// de NAV em dias uteis diferentes, com a duracao em dias uteis: buracos no historico (batch que nao rodou)
/// e fins de semana nao contam como "um dia".
/// </summary>
public static class PerformanceMath
{
    public const int TradingDaysPerYear = 252;

    /// <summary>Volatilidade, Sharpe, Alpha e Beta so aparecem a partir deste numero de observacoes.</summary>
    public const int MinObservations = 20;

    /// <param name="points">Cotas em ordem de data; o primeiro ponto e a criacao do fundo.</param>
    /// <param name="periodStart">Inicio do periodo (yyyy-MM-dd) ou null para desde o inicio.</param>
    /// <param name="benchmark">Referencia do fundo (FundBenchmarks): IBOVESPA ou CDI.</param>
    public static PerformanceResult Compute(
        IReadOnlyList<SharePoint> points,
        string? periodStart,
        MarketSeries market,
        string benchmark = FundBenchmarks.Ibovespa)
    {
        var calendar = market.Calendar;

        // Um ponto por dia util: sabado vira sexta; no mesmo dia util vale o ultimo valor
        var normalized = points
            .Where(p => p.ShareValue > 0)
            .OrderBy(p => p.Date, StringComparer.Ordinal)
            .Select(p => new SharePoint(calendar.ToBusinessDay(p.Date), p.ShareValue))
            .GroupBy(p => p.Date)
            .Select(g => g.Last())
            .OrderBy(p => p.Date, StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
            return new PerformanceResult(0, null, null, null, 0, null, null, null, 0, 0);

        // Base: ultimo ponto antes do periodo; se o fundo nasceu dentro dele, o ponto de criacao
        var baseIndex = periodStart == null
            ? 0
            : normalized.FindLastIndex(p => string.CompareOrdinal(p.Date, periodStart) < 0);
        if (baseIndex < 0)
            baseIndex = 0;

        var series = normalized.Skip(baseIndex).ToList();
        var basePoint = series[0];
        var last = series[^1];

        var returns = new List<(double Return, int Days, string From, string To)>();
        for (var i = 1; i < series.Count; i++)
        {
            var days = Math.Max(1, calendar.Count(series[i - 1].Date, series[i].Date));
            returns.Add((series[i].ShareValue / series[i - 1].ShareValue - 1, days, series[i - 1].Date, series[i].Date));
        }

        var observations = returns.Count;
        var businessDays = returns.Sum(r => r.Days);
        var cumulative = last.ShareValue / basePoint.ShareValue - 1;

        double? annualized = businessDays > 0
            ? (cumulative <= -1 ? -1 : Math.Pow(1 + cumulative, (double)TradingDaysPerYear / businessDays) - 1)
            : null;

        var riskFree = businessDays > 0 ? AnnualizedRiskFree(market, basePoint.Date, last.Date, businessDays) : null;
        double? volatility = observations >= Math.Max(2, MinObservations) ? Volatility(returns) : null;
        double? sharpe = volatility > 0 && annualized.HasValue && riskFree.HasValue
            ? (annualized.Value - riskFree.Value) / volatility.Value
            : null;

        // Contra o CDI nao ha beta (o CDI quase nao oscila): alpha e o retorno anualizado acima do CDI
        var (alpha, beta) = observations < MinObservations
            ? (null, null)
            : benchmark == FundBenchmarks.Cdi
                ? (annualized - riskFree, (double?)null)
                : AlphaBeta(returns, market.Ibovespa);

        return new PerformanceResult(
            cumulative,
            annualized,
            volatility,
            sharpe,
            MaxDrawdown(series),
            alpha,
            beta,
            riskFree,
            observations,
            businessDays);
    }

    /// <summary>
    /// Volatilidade anualizada com intervalos de duracoes diferentes: a variancia diaria e estimada
    /// ponderando cada intervalo pelos seus dias uteis (com todos de 1 dia, e o desvio-padrao comum).
    /// </summary>
    private static double Volatility(List<(double Return, int Days, string From, string To)> returns)
    {
        var dailyMean = returns.Sum(r => r.Return) / returns.Sum(r => r.Days);
        var variance = returns.Sum(r => Math.Pow(r.Return - dailyMean * r.Days, 2) / r.Days) / (returns.Count - 1);
        return Math.Sqrt(variance * TradingDaysPerYear);
    }

    /// <summary>CDI acumulado no periodo, anualizado. Dias sem CDI publicado usam a taxa conhecida mais proxima.</summary>
    private static double? AnnualizedRiskFree(MarketSeries market, string from, string to, int businessDays)
    {
        if (market.CdiDaily.Count == 0)
            return null;

        var known = market.CdiDaily.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var factor = 1.0;
        foreach (var day in market.Calendar.Days(from, to))
        {
            if (!market.CdiDaily.TryGetValue(day, out var rate))
            {
                var previous = known.LastOrDefault(kv => string.CompareOrdinal(kv.Key, day) < 0);
                rate = previous.Key != null ? previous.Value : known[0].Value;
            }

            factor *= 1 + rate;
        }

        return Math.Pow(factor, (double)TradingDaysPerYear / businessDays) - 1;
    }

    /// <summary>
    /// Regressao dos retornos do fundo contra o IBOVESPA nos mesmos intervalos.
    /// Beta = Cov / Var; Alpha anualizado pelo numero medio de dias uteis por intervalo.
    /// </summary>
    private static (double? Alpha, double? Beta) AlphaBeta(
        List<(double Return, int Days, string From, string To)> returns,
        DailySeries? ibovespa)
    {
        if (ibovespa == null)
            return (null, null);

        var pairs = new List<(double Fund, double Bench, int Days)>();
        foreach (var r in returns)
        {
            var start = ibovespa.ValueAt(r.From);
            var end = ibovespa.ValueAt(r.To);
            if (start > 0 && end > 0)
                pairs.Add((r.Return, end.Value / start.Value - 1, r.Days));
        }

        if (pairs.Count < MinObservations)
            return (null, null);

        var meanFund = pairs.Average(p => p.Fund);
        var meanBench = pairs.Average(p => p.Bench);
        var covariance = pairs.Sum(p => (p.Fund - meanFund) * (p.Bench - meanBench)) / (pairs.Count - 1);
        var variance = pairs.Sum(p => Math.Pow(p.Bench - meanBench, 2)) / (pairs.Count - 1);
        if (variance == 0)
            return (null, null);

        var beta = covariance / variance;
        var alphaPerInterval = meanFund - beta * meanBench;
        var alpha = alphaPerInterval / pairs.Average(p => p.Days) * TradingDaysPerYear;
        return (alpha, beta);
    }

    /// <summary>Maior queda percentual do pico ao vale (negativa por convencao).</summary>
    private static double MaxDrawdown(List<SharePoint> series)
    {
        var peak = series[0].ShareValue;
        var maxDrawdown = 0.0;
        foreach (var point in series)
        {
            if (point.ShareValue > peak)
                peak = point.ShareValue;

            if (peak > 0)
                maxDrawdown = Math.Max(maxDrawdown, (peak - point.ShareValue) / peak);
        }

        return -maxDrawdown;
    }
}
