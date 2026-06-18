using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;
using PUCFinance.AssetManagement.Models.DTOs;
using PUCFinance.AssetManagement.Services;

namespace PUCFinance.AssetManagement.Controllers;

// ════════════════════════════════════════════════════════
// FUNDS
// ════════════════════════════════════════════════════════

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class FundsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ExportService _export;
    private readonly FundAccessService _fundAccess;
    private const double AnnualRiskFreeRate = 0.1450;

    public FundsController(AppDbContext db, ExportService export, FundAccessService fundAccess)
    {
        _db = db;
        _export = export;
        _fundAccess = fundAccess;
    }

    /// <summary>GET /api/funds — Lista todos os fundos com resumo</summary>
    [HttpGet]
    public async Task<ActionResult<List<FundSummaryResponse>>> GetAll()
    {
        var funds = await _fundAccess.VisibleFunds()
            .Include(f => f.Team)
            .ToListAsync();
        var summaries = new List<FundSummaryResponse>();

        foreach (var fund in funds)
        {
            var snapshot = await GetRealtimeSnapshotAsync(fund.Id, fund);

            summaries.Add(new FundSummaryResponse(
                Id: fund.Id,
                Name: fund.Name,
                Strategy: fund.Strategy,
                TeamId: fund.TeamId,
                TeamName: fund.Team?.Name,
                TotalEquity: snapshot.TotalEquity,
                ShareValue: snapshot.ShareValue,
                DailyReturn: snapshot.DailyReturn,
                CashBalance: snapshot.CashBalance,
                PositionCount: snapshot.Positions.Count
            ));
        }

        return Ok(summaries);
    }

    private sealed record RealtimeFundSnapshot(
        Fund Fund,
        string Date,
        List<Position> Positions,
        double CashBalance,
        double TotalMarketValue,
        double TotalEquity,
        double ShareValue,
        NavHistory? PreviousCloseNav,
        double BaseShareValue,
        double DailyReturn);

    private async Task<RealtimeFundSnapshot> GetRealtimeSnapshotAsync(int fundId, Fund? fund = null)
    {
        fund ??= await _db.Funds.FindAsync(fundId)
            ?? throw new InvalidOperationException($"Fundo {fundId} nao encontrado");

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var latestNav = await _db.NavHistory
            .Where(n => n.FundId == fundId)
            .OrderByDescending(n => n.Date)
            .FirstOrDefaultAsync();
        var previousCloseNav = await _db.NavHistory
            .Where(n => n.FundId == fundId && string.Compare(n.Date, today) < 0)
            .OrderByDescending(n => n.Date)
            .FirstOrDefaultAsync();
        var positions = await _db.Positions
            .Where(p => p.FundId == fundId)
            .ToListAsync();
        var cash = await _db.Cash.FindAsync(fundId);
        var cashBalance = cash?.Balance ?? latestNav?.CashBalance ?? fund.InitialCapital;
        var totalMarketValue = positions.Sum(CurrentMarketValue);
        var totalEquity = cashBalance + totalMarketValue;
        var shareValue = fund.TotalShares > 0
            ? totalEquity / fund.TotalShares
            : latestNav?.ShareValue ?? 0;
        var baseShareValue = previousCloseNav?.ShareValue ?? InitialShareValue(fund);
        var dailyReturn = baseShareValue > 0 ? (shareValue / baseShareValue) - 1 : 0;

        return new RealtimeFundSnapshot(
            fund,
            today,
            positions,
            cashBalance,
            totalMarketValue,
            totalEquity,
            shareValue,
            previousCloseNav,
            baseShareValue,
            dailyReturn);
    }

    private static double InitialShareValue(Fund fund)
    {
        return fund.TotalShares > 0 ? fund.InitialCapital / fund.TotalShares : 0;
    }

    private static double CurrentMarketValue(Position position)
    {
        if (position.MarketValue.HasValue)
            return position.MarketValue.Value;

        var price = position.CurrentPrice ?? position.AvgPrice;
        return position.Quantity * price;
    }

    private static double CurrentUnrealizedPnl(Position position)
    {
        if (position.UnrealizedPnl.HasValue)
            return position.UnrealizedPnl.Value;

        var price = position.CurrentPrice ?? position.AvgPrice;
        return (price - position.AvgPrice) * position.Quantity;
    }

    private static void UpsertRealtimeNavPoint(List<NavPointResponse> navs, RealtimeFundSnapshot snapshot)
    {
        var realtimePoint = new NavPointResponse(
            snapshot.Date,
            snapshot.TotalEquity,
            snapshot.ShareValue,
            snapshot.DailyReturn);

        var todayIndex = navs.FindIndex(n => n.Date == snapshot.Date);
        if (todayIndex >= 0)
            navs[todayIndex] = realtimePoint;
        else
            navs.Add(realtimePoint);
    }

    /// <summary>POST /api/funds — Cria um novo fundo</summary>
    [Authorize(Roles = AppRoles.Leader)]
    [HttpPost]
    public async Task<ActionResult<Fund>> Create([FromBody] CreateFundRequest request)
    {
        if (request.TeamId.HasValue && !await _db.Teams.AnyAsync(t => t.Id == request.TeamId.Value))
            return BadRequest(new { error = "Time informado nao existe" });

        var fund = new Fund
        {
            Name = request.Name,
            Strategy = request.Strategy,
            InitialCapital = request.InitialCapital,
            TotalShares = request.TotalShares,
            TeamId = request.TeamId
        };

        _db.Funds.Add(fund);
        await _db.SaveChangesAsync();

        // Cria registro de caixa
        _db.Cash.Add(new Cash { FundId = fund.Id, Balance = fund.InitialCapital });

        // Cria NAV dia 0
        _db.NavHistory.Add(new NavHistory
        {
            FundId = fund.Id,
            Date = DateTime.Today.ToString("yyyy-MM-dd"),
            TotalEquity = fund.InitialCapital,
            TotalShares = fund.TotalShares,
            ShareValue = fund.InitialCapital / fund.TotalShares,
            DailyReturn = 0,
            CashBalance = fund.InitialCapital
        });

        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetAll), new { id = fund.Id }, fund);
    }

    /// <summary>GET /api/funds/{id}/positions — Posições de um fundo</summary>
    [HttpGet("{id}/positions")]
    public async Task<ActionResult<List<PositionResponse>>> GetPositions(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var snapshot = await GetRealtimeSnapshotAsync(id, fund);

        var result = snapshot.Positions.Select(p => new PositionResponse(
            Ticker: p.Ticker,
            Side: p.Side,
            Quantity: p.Quantity,
            AvgPrice: p.AvgPrice,
            CurrentPrice: p.CurrentPrice,
            MarketValue: CurrentMarketValue(p),
            UnrealizedPnl: CurrentUnrealizedPnl(p),
            Weight: snapshot.TotalEquity > 0 ? CurrentMarketValue(p) / snapshot.TotalEquity : null
        )).ToList();

        return Ok(result);
    }

    /// <summary>GET /api/funds/{id}/members - Lista membros do time do fundo</summary>
    [Authorize(Roles = AppRoles.Leader)]
    [HttpGet("{id}/members")]
    public async Task<ActionResult<FundMembersResponse>> GetMembers(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var members = fund.TeamId.HasValue
            ? await _db.Users
                .Where(u => u.TeamId == fund.TeamId.Value)
                .OrderByDescending(u => u.IsActive)
                .ThenBy(u => u.Name)
                .Select(u => new FundMemberResponse(
                    u.Id,
                    u.Name,
                    u.Email,
                    u.Role,
                    u.IsActive == 1))
                .ToListAsync()
            : new List<FundMemberResponse>();

        return Ok(new FundMembersResponse(
            FundId: fund.Id,
            TeamId: fund.TeamId,
            TeamName: fund.Team?.Name,
            Members: members));
    }

    /// <summary>GET /api/funds/{id}/nav — Histórico de NAV</summary>
    [HttpGet("{id}/nav")]
    public async Task<ActionResult<List<NavPointResponse>>> GetNav(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var snapshot = await GetRealtimeSnapshotAsync(id, fund);

        var navs = await _db.NavHistory
            .Where(n => n.FundId == id)
            .OrderBy(n => n.Date)
            .Select(n => new NavPointResponse(n.Date, n.TotalEquity, n.ShareValue, n.DailyReturn))
            .ToListAsync();

        UpsertRealtimeNavPoint(navs, snapshot);

        return Ok(navs);
    }

    /// <summary>GET /api/funds/{id}/metrics — Métricas atuais</summary>
    [HttpGet("{id}/metrics")]
    public async Task<ActionResult<List<MetricsResponse>>> GetMetrics(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var snapshot = await GetRealtimeSnapshotAsync(id, fund);
        var navs = await _db.NavHistory
            .Where(n => n.FundId == id)
            .OrderBy(n => n.Date)
            .ToListAsync();

        var latestMetricDate = await _db.Metrics
            .Where(m => m.FundId == id)
            .MaxAsync(m => (string?)m.Date);

        var storedMetrics = latestMetricDate == null
            ? new Dictionary<string, Metric>()
            : await _db.Metrics
                .Where(m => m.FundId == id && m.Date == latestMetricDate)
                .ToDictionaryAsync(m => m.Period);

        var metrics = new List<MetricsResponse>
        {
            BuildRealtimeMetric("inception", null, navs, snapshot, storedMetrics.GetValueOrDefault("inception")),
            BuildRealtimeMetric("mtd", DateTime.Today.ToString("yyyy-MM-01"), navs, snapshot, storedMetrics.GetValueOrDefault("mtd")),
            BuildRealtimeMetric("ytd", DateTime.Today.ToString("yyyy-01-01"), navs, snapshot, storedMetrics.GetValueOrDefault("ytd"))
        };

        return Ok(metrics);
    }

    /// <summary>GET /api/funds/{id}/performance — Performance por ativo</summary>
    [HttpGet("{id}/performance")]
    public async Task<ActionResult<List<AssetPerformanceResponse>>> GetPerformance(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var snapshot = await GetRealtimeSnapshotAsync(id, fund);
        var positions = snapshot.Positions;

        var result = new List<AssetPerformanceResponse>();

        foreach (var pos in positions)
        {
            var realizedTotal = await _db.RealizedPnl
                .Where(r => r.FundId == id && r.Ticker == pos.Ticker)
                .SumAsync(r => (double?)r.Pnl) ?? 0;

            var marketValue = CurrentMarketValue(pos);
            var unrealizedPnl = CurrentUnrealizedPnl(pos);
            var totalPnl = unrealizedPnl + realizedTotal;
            var costBasis = Math.Abs(pos.Quantity) * pos.AvgPrice;
            double? returnPct = costBasis > 0 ? totalPnl / costBasis : null;
            var weight = snapshot.TotalEquity > 0 ? marketValue / snapshot.TotalEquity : 0;

            var dailyHistory = await _db.PositionHistory
                .Where(h => h.FundId == id && h.Ticker == pos.Ticker)
                .OrderBy(h => h.Date)
                .Select(h => new AssetDailyPoint(
                    h.Date, h.CurrentPrice, h.DailyReturn, h.Contribution, h.Weight))
                .ToListAsync();

            var totalContribution = CompoundReturns(
                dailyHistory.OrderBy(d => d.Date).Select(d => d.Contribution ?? 0));

            result.Add(new AssetPerformanceResponse(
                Ticker: pos.Ticker,
                Side: pos.Side,
                Quantity: pos.Quantity,
                AvgPrice: pos.AvgPrice,
                CurrentPrice: pos.CurrentPrice,
                MarketValue: marketValue,
                UnrealizedPnl: unrealizedPnl,
                RealizedPnl: realizedTotal,
                TotalPnl: totalPnl,
                ReturnPct: returnPct,
                Weight: weight,
                Contribution: totalContribution,
                DailyHistory: dailyHistory
            ));
        }

        return Ok(result.OrderByDescending(r => Math.Abs(r.TotalPnl)).ToList());
    }

    /// <summary>GET /api/funds/{id}/export — Exporta Excel do fundo</summary>
    [Authorize(Roles = AppRoles.Leader)]
    [HttpGet("{id}/export")]
    public async Task<IActionResult> ExportExcel(int id)
    {
        try
        {
            var fund = await _fundAccess.FindVisibleFundAsync(id);
            if (fund == null) return NotFound();

            var bytes = await _export.ExportFundAsync(id);
            var fileName = $"PUCFinance_{fund.Name}_{DateTime.Today:yyyy-MM-dd}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>GET /api/funds/{id}/exposure — Exposicao por classe de ativo</summary>
    [HttpGet("{id}/exposure")]
    public async Task<ActionResult<ExposureResponse>> GetExposure(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var snapshot = await GetRealtimeSnapshotAsync(id, fund);
        var positions = snapshot.Positions;
        var totalEquity = snapshot.TotalEquity;
        var cashBalance = snapshot.CashBalance;

        // Classifica cada posicao
        var classMap = new Dictionary<string, (string Label, double Long, double Short, int Count)>();
        var labels = new Dictionary<string, string>
        {
            {"equity_br", "Acoes BR"}, {"equity_us", "Acoes EUA"}, {"etf", "ETFs"},
            {"fx", "Cambio"}, {"commodity", "Commodities"}, {"crypto", "Cripto"},
            {"fixed_income", "Renda Fixa"}, {"unknown", "Outros"}
        };

        foreach (var pos in positions)
        {
            var asset = await _db.Assets.FindAsync(pos.Ticker);
            var assetClass = asset?.AssetClass ?? "unknown";
            var mv = CurrentMarketValue(pos);

            if (!classMap.ContainsKey(assetClass))
                classMap[assetClass] = (labels.GetValueOrDefault(assetClass, assetClass), 0, 0, 0);

            var current = classMap[assetClass];
            if (mv >= 0)
                classMap[assetClass] = (current.Label, current.Long + mv, current.Short, current.Count + 1);
            else
                classMap[assetClass] = (current.Label, current.Long, current.Short + Math.Abs(mv), current.Count + 1);
        }

        var totalLong = classMap.Values.Sum(c => c.Long);
        var totalShort = classMap.Values.Sum(c => c.Short);

        var byClass = classMap.Select(kv => new ClassExposure(
            AssetClass: kv.Key,
            Label: kv.Value.Label,
            LongValue: kv.Value.Long,
            ShortValue: kv.Value.Short,
            NetValue: kv.Value.Long - kv.Value.Short,
            GrossWeight: totalEquity > 0 ? (kv.Value.Long + kv.Value.Short) / totalEquity : 0,
            NetWeight: totalEquity > 0 ? (kv.Value.Long - kv.Value.Short) / totalEquity : 0,
            PositionCount: kv.Value.Count
        )).OrderByDescending(c => c.GrossWeight).ToList();

        return Ok(new ExposureResponse(
            GrossExposure: totalEquity > 0 ? (totalLong + totalShort) / totalEquity : 0,
            NetExposure: totalEquity > 0 ? (totalLong - totalShort) / totalEquity : 0,
            LongExposure: totalEquity > 0 ? totalLong / totalEquity : 0,
            ShortExposure: totalEquity > 0 ? totalShort / totalEquity : 0,
            CashWeight: totalEquity > 0 ? cashBalance / totalEquity : 0,
            ByClass: byClass
        ));
    }

    /// <summary>GET /api/funds/{id}/return-by-class — Retorno por classe de ativo</summary>
    [HttpGet("{id}/return-by-class")]
    public async Task<ActionResult<List<ReturnByClassResponse>>> GetReturnByClass(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var snapshot = await GetRealtimeSnapshotAsync(id, fund);
        var liveByClass = await GetRealtimeClassContributionsAsync(snapshot);
        var results = new List<ReturnByClassResponse>();
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var labels = new Dictionary<string, string>
        {
            {"equity_br", "Acoes BR"}, {"equity_us", "Acoes EUA"}, {"etf", "ETFs"},
            {"fx", "Cambio"}, {"commodity", "Commodities"}, {"crypto", "Cripto"},
            {"fixed_income", "Renda Fixa"}, {"unknown", "Outros"}
        };

        // Calcula para cada periodo
        foreach (var (period, fromDate) in new[] {
            ("inception", "2000-01-01"),
            ("mtd", DateTime.Today.ToString("yyyy-MM-01")),
            ("ytd", DateTime.Today.ToString("yyyy-01-01"))
        })
        {
            var history = await _db.PositionHistory
                .Where(h => h.FundId == id && string.Compare(h.Date, fromDate) >= 0 && h.Date != today)
                .ToListAsync();

            if (history.Count == 0 && liveByClass.Count == 0) continue;

            var tickers = history.Select(h => h.Ticker).Distinct().ToList();
            var assetClasses = await _db.Assets
                .Where(a => tickers.Contains(a.Ticker))
                .ToDictionaryAsync(a => a.Ticker, a => a.AssetClass);

            var dailyByClass = new Dictionary<string, Dictionary<string, double>>();
            var dailyTotal = new Dictionary<string, double>();

            foreach (var h in history)
            {
                var assetClass = assetClasses.GetValueOrDefault(h.Ticker) ?? "unknown";
                var contribution = h.Contribution ?? 0;

                if (!dailyByClass.ContainsKey(assetClass))
                    dailyByClass[assetClass] = new Dictionary<string, double>();

                dailyByClass[assetClass][h.Date] =
                    dailyByClass[assetClass].GetValueOrDefault(h.Date) + contribution;
                dailyTotal[h.Date] = dailyTotal.GetValueOrDefault(h.Date) + contribution;
            }

            if (string.Compare(today, fromDate) >= 0)
            {
                foreach (var kv in liveByClass)
                {
                    if (!dailyByClass.ContainsKey(kv.Key))
                        dailyByClass[kv.Key] = new Dictionary<string, double>();

                    dailyByClass[kv.Key][today] =
                        dailyByClass[kv.Key].GetValueOrDefault(today) + kv.Value;
                    dailyTotal[today] = dailyTotal.GetValueOrDefault(today) + kv.Value;
                }
            }

            var byClass = dailyByClass.ToDictionary(
                kv => kv.Key,
                kv => CompoundReturns(kv.Value.OrderBy(d => d.Key).Select(d => d.Value)));

            var totalReturn = CompoundReturns(dailyTotal.OrderBy(d => d.Key).Select(d => d.Value));

            results.Add(new ReturnByClassResponse(
                Period: period,
                TotalReturn: totalReturn,
                ByClass: byClass.Select(kv => new ClassReturn(
                    AssetClass: kv.Key,
                    Label: labels.GetValueOrDefault(kv.Key, kv.Key),
                    Contribution: kv.Value,
                    Weight: totalReturn != 0 ? kv.Value / totalReturn : 0
                )).OrderByDescending(c => Math.Abs(c.Contribution)).ToList()
            ));
        }

        return Ok(results);
    }

    /// <summary>GET /api/funds/{id}/cdi-comparison — Fundo vs CDI acumulado</summary>
    [HttpGet("{id}/cdi-comparison")]
    public async Task<ActionResult<CdiBenchmarkResponse>> GetCdiComparison(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var navs = await _db.NavHistory
            .Where(n => n.FundId == id)
            .OrderBy(n => n.Date)
            .ToListAsync();

        if (navs.Count == 0)
            return Ok(new CdiBenchmarkResponse(0, 0, 0, "inception", new()));

        var snapshot = await GetRealtimeSnapshotAsync(id, fund);
        var realtimeNavs = UpsertRealtimeNavHistory(navs, snapshot);
        var startDate = realtimeNavs.First().Date;
        var firstShareValue = navs.FirstOrDefault()?.ShareValue ?? InitialShareValue(fund);

        var cdis = await _db.Benchmarks
            .Where(b => b.Name == "CDI" && string.Compare(b.Date, startDate) > 0)
            .OrderBy(b => b.Date)
            .ToListAsync();

        // CDI acumulado desde o inicio do fundo
        var cdiIndex = 0;
        var cdiCumulativeFactor = 1.0;
        var latestCdiCumulative = 0.0;

        var series = realtimeNavs.Select(n =>
        {
            while (cdiIndex < cdis.Count && string.Compare(cdis[cdiIndex].Date, n.Date) <= 0)
            {
                cdiCumulativeFactor *= 1 + (cdis[cdiIndex].DailyReturn ?? 0);
                latestCdiCumulative = cdiCumulativeFactor - 1;
                cdiIndex++;
            }

            var fundCum = firstShareValue > 0 ? (n.ShareValue / firstShareValue) - 1 : 0;
            return new CdiComparisonPoint(n.Date, fundCum, latestCdiCumulative);
        }).ToList();

        var lastFund = series.Last().FundCumulative;
        var lastCdi = series.Last().CdiCumulative;

        return Ok(new CdiBenchmarkResponse(
            FundReturn: lastFund,
            CdiReturn: lastCdi,
            ExcessReturn: lastFund - lastCdi,
            Period: "inception",
            Series: series
        ));
    }

    private static double CompoundReturns(IEnumerable<double> returns)
    {
        var factor = 1.0;
        foreach (var value in returns)
        {
            factor *= 1 + value;
        }

        return factor - 1;
    }

    private static List<NavHistory> UpsertRealtimeNavHistory(
        List<NavHistory> navs,
        RealtimeFundSnapshot snapshot)
    {
        var result = navs
            .Where(n => n.Date != snapshot.Date)
            .ToList();

        result.Add(new NavHistory
        {
            FundId = snapshot.Fund.Id,
            Date = snapshot.Date,
            TotalEquity = snapshot.TotalEquity,
            TotalShares = snapshot.Fund.TotalShares,
            ShareValue = snapshot.ShareValue,
            DailyReturn = snapshot.DailyReturn,
            CashBalance = snapshot.CashBalance
        });

        return result.OrderBy(n => n.Date).ToList();
    }

    private static MetricsResponse BuildRealtimeMetric(
        string period,
        string? periodStart,
        List<NavHistory> closedNavs,
        RealtimeFundSnapshot snapshot,
        Metric? storedMetric)
    {
        var orderedNavs = closedNavs.OrderBy(n => n.Date).ToList();
        var baseShareValue = GetMetricBaseShareValue(periodStart, orderedNavs, snapshot);
        var shareValues = BuildMetricShareValues(periodStart, orderedNavs, snapshot, baseShareValue);
        var returns = BuildMetricReturns(periodStart, orderedNavs, snapshot);
        var cumulativeReturn = baseShareValue > 0 ? (snapshot.ShareValue / baseShareValue) - 1 : 0;
        var annualizedReturn = AnnualizedReturn(returns, cumulativeReturn);
        double? volatility = returns.Count >= 2 ? Volatility(returns) : null;
        double? sharpe = volatility.HasValue && volatility.Value != 0
            ? (annualizedReturn - AnnualRiskFreeRate) / volatility.Value
            : null;
        var maxDrawdown = MaxDrawdown(shareValues);

        return new MetricsResponse(
            Period: period,
            CumulativeReturn: cumulativeReturn,
            AnnualizedReturn: annualizedReturn,
            Volatility: volatility,
            SharpeRatio: sharpe,
            MaxDrawdown: maxDrawdown,
            Alpha: storedMetric?.Alpha,
            Beta: storedMetric?.Beta,
            BenchmarkName: storedMetric?.BenchmarkName);
    }

    private static double GetMetricBaseShareValue(
        string? periodStart,
        List<NavHistory> navs,
        RealtimeFundSnapshot snapshot)
    {
        if (periodStart == null)
            return InitialShareValue(snapshot.Fund);

        var previousNav = navs
            .Where(n => string.Compare(n.Date, periodStart) < 0)
            .LastOrDefault();
        if (previousNav != null)
            return previousNav.ShareValue;

        return InitialShareValue(snapshot.Fund);
    }

    private static List<double> BuildMetricShareValues(
        string? periodStart,
        List<NavHistory> navs,
        RealtimeFundSnapshot snapshot,
        double baseShareValue)
    {
        var values = new List<double> { baseShareValue };
        values.AddRange(navs
            .Where(n => n.Date != snapshot.Date)
            .Where(n => periodStart == null || string.Compare(n.Date, periodStart) >= 0)
            .Select(n => n.ShareValue));
        values.Add(snapshot.ShareValue);
        return values;
    }

    private static List<double> BuildMetricReturns(
        string? periodStart,
        List<NavHistory> navs,
        RealtimeFundSnapshot snapshot)
    {
        var returns = navs
            .Where(n => n.Date != snapshot.Date)
            .Where(n => periodStart == null || string.Compare(n.Date, periodStart) >= 0)
            .Where(n => n.DailyReturn.HasValue)
            .Select(n => n.DailyReturn!.Value)
            .ToList();

        if (periodStart == null || string.Compare(snapshot.Date, periodStart) >= 0)
            returns.Add(snapshot.DailyReturn);

        return returns;
    }

    private static double AnnualizedReturn(List<double> returns, double cumulativeReturn)
    {
        if (returns.Count == 0) return 0;
        if (cumulativeReturn <= -1) return -1;
        return Math.Pow(1 + cumulativeReturn, 252.0 / returns.Count) - 1;
    }

    private static double Volatility(List<double> returns)
    {
        if (returns.Count < 2) return 0;
        var mean = returns.Average();
        var variance = returns.Sum(r => Math.Pow(r - mean, 2)) / (returns.Count - 1);
        return Math.Sqrt(variance) * Math.Sqrt(252);
    }

    private static double MaxDrawdown(List<double> shareValues)
    {
        if (shareValues.Count < 2) return 0;

        var peak = shareValues.First();
        var maxDrawdown = 0.0;
        foreach (var shareValue in shareValues)
        {
            if (shareValue > peak)
                peak = shareValue;

            if (peak <= 0) continue;

            var drawdown = (peak - shareValue) / peak;
            if (drawdown > maxDrawdown)
                maxDrawdown = drawdown;
        }

        return -maxDrawdown;
    }

    private async Task<Dictionary<string, double>> GetRealtimeClassContributionsAsync(
        RealtimeFundSnapshot snapshot)
    {
        var baseEquity = snapshot.PreviousCloseNav?.TotalEquity ?? snapshot.Fund.InitialCapital;
        if (baseEquity <= 0)
            return new Dictionary<string, double>();

        var previousHistory = await _db.PositionHistory
            .Where(h => h.FundId == snapshot.Fund.Id && string.Compare(h.Date, snapshot.Date) < 0)
            .ToListAsync();
        var previousByTicker = previousHistory
            .GroupBy(h => h.Ticker)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.Date).First());

        var realizedToday = await _db.RealizedPnl
            .Where(r => r.FundId == snapshot.Fund.Id && r.ClosedAt.StartsWith(snapshot.Date))
            .GroupBy(r => r.Ticker)
            .Select(g => new { Ticker = g.Key, Pnl = g.Sum(r => r.Pnl) })
            .ToDictionaryAsync(x => x.Ticker, x => x.Pnl);

        var tickers = snapshot.Positions
            .Select(p => p.Ticker)
            .Concat(realizedToday.Keys)
            .Distinct()
            .ToList();
        var assetClasses = await _db.Assets
            .Where(a => tickers.Contains(a.Ticker))
            .ToDictionaryAsync(a => a.Ticker, a => a.AssetClass);

        var contributions = new Dictionary<string, double>();
        foreach (var position in snapshot.Positions)
        {
            var previousUnrealized = previousByTicker.GetValueOrDefault(position.Ticker)?.UnrealizedPnl ?? 0;
            var realized = realizedToday.GetValueOrDefault(position.Ticker);
            var pnlDelta = CurrentUnrealizedPnl(position) - previousUnrealized + realized;
            var assetClass = assetClasses.GetValueOrDefault(position.Ticker) ?? "unknown";
            contributions[assetClass] = contributions.GetValueOrDefault(assetClass) + (pnlDelta / baseEquity);
        }

        foreach (var kv in realizedToday)
        {
            if (snapshot.Positions.Any(p => p.Ticker == kv.Key))
                continue;

            var previousUnrealized = previousByTicker.GetValueOrDefault(kv.Key)?.UnrealizedPnl ?? 0;
            var pnlDelta = kv.Value - previousUnrealized;
            var assetClass = assetClasses.GetValueOrDefault(kv.Key) ?? "unknown";
            contributions[assetClass] = contributions.GetValueOrDefault(assetClass) + (pnlDelta / baseEquity);
        }

        return contributions;
    }
}

// ════════════════════════════════════════════════════════
// ASSETS (dropdown de ativos)
// ════════════════════════════════════════════════════════

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AssetsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AssetsController(AppDbContext db) => _db = db;

    /// <summary>GET /api/assets — Lista todos os ativos disponiveis</summary>
    [HttpGet]
    public async Task<ActionResult<List<AssetResponse>>> GetAll([FromQuery] string? assetClass = null)
    {
        var query = _db.Assets.Where(a => a.IsActive == 1);

        if (!string.IsNullOrEmpty(assetClass))
            query = query.Where(a => a.AssetClass == assetClass);

        var assets = await query
            .OrderBy(a => a.AssetClass)
            .ThenBy(a => a.Ticker)
            .Select(a => new AssetResponse(
                a.Ticker, a.Name, a.AssetClass, a.Sector, a.Exchange, a.Currency))
            .ToListAsync();

        return Ok(assets);
    }

    /// <summary>GET /api/assets/classes — Lista classes de ativo</summary>
    [HttpGet("classes")]
    public async Task<ActionResult<List<string>>> GetClasses()
    {
        var classes = await _db.Assets
            .Select(a => a.AssetClass)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        return Ok(classes);
    }
}

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class TradesController : ControllerBase
{
    private readonly TradeService _tradeService;
    private readonly AppDbContext _db;
    private readonly FundAccessService _fundAccess;
    private readonly CurrentUserService _currentUser;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TradesController> _logger;

    public TradesController(
        TradeService tradeService,
        AppDbContext db,
        FundAccessService fundAccess,
        CurrentUserService currentUser,
        IServiceScopeFactory scopeFactory,
        ILogger<TradesController> logger)
    {
        _tradeService = tradeService;
        _db = db;
        _fundAccess = fundAccess;
        _currentUser = currentUser;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>POST /api/trades — Executa um trade (preco buscado automaticamente)</summary>
    [HttpPost]
    public async Task<ActionResult<Trade>> Execute([FromBody] ExecuteTradeRequest request)
    {
        try
        {
            if (!await _fundAccess.CanAccessFundAsync(request.FundId))
                return NotFound(new { error = "Fundo nao encontrado" });

            var securedRequest = request with { ExecutedBy = _currentUser.Name };
            var trade = await _tradeService.ExecuteTradeAsync(securedRequest);

            QueuePostTradeBatch(trade.FundId, trade.Id);

            return CreatedAtAction(nameof(GetByFund), new { fundId = trade.FundId }, trade);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Dispara o batch automatico depois de uma mudanca em trades.</summary>
    private void QueuePostTradeBatch(int fundId, int tradeId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var batch = scope.ServiceProvider.GetRequiredService<BatchService>();
                var result = await batch.RunDailyUpdateAsync();

                _logger.LogInformation(
                    "Batch automatico pos-trade concluido. Trade: {TradeId} | Fundo: {FundId} | Status: {Status} | Precos: {PricesFetched}",
                    tradeId,
                    fundId,
                    result.Status,
                    result.PricesFetched);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Batch automatico pos-trade falhou. Trade: {TradeId} | Fundo: {FundId}",
                    tradeId,
                    fundId);
            }
        });
    }

    /// <summary>DELETE /api/trades/{id} - Deleta um trade e reverte posicao/caixa</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id)
    {
        try
        {
            var fundId = await _db.Trades
                .Where(t => t.Id == id)
                .Select(t => (int?)t.FundId)
                .FirstOrDefaultAsync();

            if (!fundId.HasValue || !await _fundAccess.CanAccessFundAsync(fundId.Value))
                return NotFound(new { error = "Trade nao encontrado" });

            await _tradeService.DeleteTradeAsync(id);
            QueuePostTradeBatch(fundId.Value, id);
            return Ok(new { message = "Trade deletado com sucesso" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>GET /api/trades/fund/{fundId} — Historico de trades de um fundo</summary>
    [HttpGet("fund/{fundId}")]
    public async Task<ActionResult<List<TradeResponse>>> GetByFund(int fundId)
    {
        if (!await _fundAccess.CanAccessFundAsync(fundId))
            return NotFound(new { error = "Fundo nao encontrado" });

        var trades = await _db.Trades
            .Where(t => t.FundId == fundId)
            .OrderByDescending(t => t.ExecutedAt)
            .Select(t => new TradeResponse(
                t.Id, t.Ticker, t.Side, t.Quantity, t.Price,
                t.Thesis, t.ExecutedAt, t.ExecutedBy))
            .ToListAsync();

        return Ok(trades);
    }
}

// ════════════════════════════════════════════════════════
// BATCH (chamado pelo GitHub Actions)
// ════════════════════════════════════════════════════════

[ApiController]
[Route("api/[controller]")]
public class BatchController : ControllerBase
{
    private readonly BatchService _batch;
    private readonly IConfiguration _config;

    public BatchController(BatchService batch, IConfiguration config)
    {
        _batch = batch;
        _config = config;
    }

    /// <summary>POST /api/batch/run — Executa o pipeline diário</summary>
    [HttpPost("run")]
    public async Task<ActionResult<BatchResultResponse>> Run()
    {
        var batchToken = _config["BATCH_TOKEN"];
        var providedToken = Request.Headers["X-Batch-Token"].FirstOrDefault();
        var hasValidBatchToken = !string.IsNullOrWhiteSpace(batchToken)
            && string.Equals(providedToken, batchToken, StringComparison.Ordinal);
        var isAuthenticatedLeader = User.Identity?.IsAuthenticated == true && User.IsInRole(AppRoles.Leader);

        if (!hasValidBatchToken && !isAuthenticatedLeader)
            return Unauthorized(new { error = "Invalid batch token or insufficient permissions" });

        return await RunBatchAsync();
    }

    private async Task<ActionResult<BatchResultResponse>> RunBatchAsync()
    {
        var result = await _batch.RunDailyUpdateAsync();
        if (!string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status500InternalServerError, result);

        return Ok(result);
    }
}
// ════════════════════════════════════════════════════════
// PRICES (consulta de preco atual)
// ════════════════════════════════════════════════════════

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class PricesController : ControllerBase
{
    private readonly PricingService _pricing;

    public PricesController(PricingService pricing) => _pricing = pricing;

    /// <summary>GET /api/prices/current/{ticker} — Preco atual de um ticker</summary>
    [HttpGet("current/{ticker}")]
    public async Task<ActionResult> GetCurrentPrice(string ticker)
    {
        var price = await _pricing.GetLatestPriceAsync(ticker.Trim().ToUpper());
        if (price == null)
            return NotFound(new { error = $"Preco nao encontrado para {ticker}" });

        return Ok(new { ticker = ticker.Trim().ToUpper(), price = price.Value });
    }
}

