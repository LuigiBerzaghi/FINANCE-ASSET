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
                PositionCount: snapshot.Positions.Count,
                Benchmark: FundBenchmarks.Resolve(fund.Benchmark)
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
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return BadRequest(new { error = "Informe o nome do fundo" });

        if (await _db.Funds.AnyAsync(f => f.Name == name))
            return BadRequest(new { error = $"Ja existe um fundo chamado {name}" });

        if (request.TeamId.HasValue && !await _db.Teams.AnyAsync(t => t.Id == request.TeamId.Value))
            return BadRequest(new { error = "Time informado nao existe" });

        if (request.Benchmark != null && !FundBenchmarks.IsValid(request.Benchmark))
            return BadRequest(new { error = "Benchmark deve ser IBOVESPA ou CDI" });

        // Gestores escolhidos: precisam existir, estar ativos e ser gestores
        var managerIds = (request.ManagerIds ?? []).Distinct().ToList();
        var managers = await _db.Users
            .Where(u => managerIds.Contains(u.Id) && u.IsActive == 1 && u.Role == AppRoles.Manager)
            .Select(u => u.Id)
            .ToListAsync();
        if (managers.Count != managerIds.Count)
            return BadRequest(new { error = "Gestor invalido ou inativo na lista de gestores" });

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Cada fundo tem um time com o mesmo nome; os gestores do fundo sao os membros desse time
        var teamId = request.TeamId;
        if (managerIds.Count > 0)
        {
            var team = await _db.Teams.FirstOrDefaultAsync(t => t.Name == name);
            if (team != null && await _db.Funds.AnyAsync(f => f.TeamId == team.Id))
                return BadRequest(new { error = $"Ja existe um time chamado {name} ligado a outro fundo" });

            if (team == null)
            {
                team = new Team { Name = name };
                _db.Teams.Add(team);
                await _db.SaveChangesAsync();
            }

            var existingMembers = await _db.TeamMembers
                .Where(m => m.TeamId == team.Id)
                .Select(m => m.UserId)
                .ToListAsync();
            foreach (var userId in managerIds.Except(existingMembers))
                _db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = userId });

            teamId = team.Id;
        }

        var fund = new Fund
        {
            Name = name,
            Strategy = string.IsNullOrWhiteSpace(request.Strategy) ? null : request.Strategy.Trim(),
            InitialCapital = request.InitialCapital,
            TotalShares = request.TotalShares,
            TeamId = teamId,
            Benchmark = FundBenchmarks.Resolve(request.Benchmark)
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
        await transaction.CommitAsync();
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
                .Where(u => _db.TeamMembers.Any(m => m.TeamId == fund.TeamId.Value && m.UserId == u.Id))
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

    /// <summary>GET /api/funds/{id}/metrics — Métricas em tempo real (inclui a cota de hoje)</summary>
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

        var points = UpsertRealtimeNavHistory(navs, snapshot)
            .Select(n => new SharePoint(n.Date, n.ShareValue))
            .ToList();
        var market = await MarketSeries.LoadAsync(_db);
        var benchmark = FundBenchmarks.Resolve(fund.Benchmark);

        var metrics = MetricPeriods.For(DateTime.Today)
            .Select(p =>
            {
                var r = PerformanceMath.Compute(points, p.Start, market, benchmark);
                return new MetricsResponse(
                    Period: p.Period,
                    CumulativeReturn: r.CumulativeReturn,
                    AnnualizedReturn: r.AnnualizedReturn,
                    Volatility: r.Volatility,
                    SharpeRatio: r.SharpeRatio,
                    MaxDrawdown: r.MaxDrawdown,
                    Alpha: r.Alpha,
                    Beta: r.Beta,
                    BenchmarkName: benchmark,
                    RiskFreeRate: r.RiskFreeRate,
                    Observations: r.Observations,
                    BusinessDays: r.BusinessDays,
                    MinObservations: PerformanceMath.MinObservations);
            })
            .ToList();

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

            // Cupons de titulos publicos e proventos de acoes sao resultado realizado do ativo
            // (provento de posicao vendida tem total negativo: o fundo pagou)
            realizedTotal += await _db.TreasuryEvents
                .Where(e => e.FundId == id && e.Ticker == pos.Ticker
                    && (e.Kind == TreasuryEventKinds.Coupon || e.Kind == TreasuryEventKinds.Dividend))
                .SumAsync(e => (double?)e.Total) ?? 0;

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

    /// <summary>GET /api/funds/{id}/benchmark-comparison — Fundo vs a referencia dele (IBOVESPA ou CDI), acumulado</summary>
    [HttpGet("{id}/benchmark-comparison")]
    public async Task<ActionResult<BenchmarkComparisonResponse>> GetBenchmarkComparison(int id)
    {
        var fund = await _fundAccess.FindVisibleFundAsync(id);
        if (fund == null) return NotFound();

        var benchmark = FundBenchmarks.Resolve(fund.Benchmark);
        var navs = await _db.NavHistory
            .Where(n => n.FundId == id)
            .OrderBy(n => n.Date)
            .ToListAsync();

        if (navs.Count == 0)
            return Ok(new BenchmarkComparisonResponse(benchmark, 0, 0, 0, "inception", new()));

        var snapshot = await GetRealtimeSnapshotAsync(id, fund);
        var realtimeNavs = UpsertRealtimeNavHistory(navs, snapshot);
        var startDate = realtimeNavs.First().Date;
        var firstShareValue = navs.FirstOrDefault()?.ShareValue ?? InitialShareValue(fund);

        // Acumulado da referencia em cada data de NAV, a partir da data inicial do fundo
        Func<string, double> benchmarkCumulative;
        if (benchmark == FundBenchmarks.Cdi)
        {
            var cdis = await _db.Benchmarks
                .Where(b => b.Name == "CDI" && string.Compare(b.Date, startDate) > 0)
                .OrderBy(b => b.Date)
                .Select(b => new { b.Date, Rate = b.DailyReturn ?? 0 })
                .ToListAsync();

            benchmarkCumulative = date => cdis
                .Where(c => string.CompareOrdinal(c.Date, date) <= 0)
                .Aggregate(1.0, (factor, c) => factor * (1 + c.Rate)) - 1;
        }
        else
        {
            var closes = await _db.Benchmarks
                .Where(b => b.Name == IbovespaService.BenchmarkName && b.Value > 0)
                .Select(b => new { b.Date, b.Value })
                .ToListAsync();
            var index = new DailySeries(closes.Select(c => new KeyValuePair<string, double>(c.Date, c.Value)));
            var start = index.ValueAt(startDate);

            benchmarkCumulative = date => start > 0 && index.ValueAt(date) is { } value ? value / start.Value - 1 : 0;
        }

        var series = realtimeNavs
            .Select(n => new BenchmarkComparisonPoint(
                n.Date,
                firstShareValue > 0 ? (n.ShareValue / firstShareValue) - 1 : 0,
                benchmarkCumulative(n.Date)))
            .ToList();

        var last = series.Last();
        return Ok(new BenchmarkComparisonResponse(
            BenchmarkName: benchmark,
            FundReturn: last.FundCumulative,
            BenchmarkReturn: last.BenchmarkCumulative,
            ExcessReturn: last.FundCumulative - last.BenchmarkCumulative,
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

        // Cupons de titulos publicos e proventos com data ex hoje entram como resultado do dia do ativo
        var couponsToday = await _db.TreasuryEvents
            .Where(e => e.FundId == snapshot.Fund.Id && e.EventDate == snapshot.Date
                && (e.Kind == TreasuryEventKinds.Coupon || e.Kind == TreasuryEventKinds.Dividend))
            .GroupBy(e => e.Ticker)
            .Select(g => new { Ticker = g.Key, Total = g.Sum(e => e.Total) })
            .ToListAsync();
        foreach (var coupon in couponsToday)
            realizedToday[coupon.Ticker] = realizedToday.GetValueOrDefault(coupon.Ticker) + coupon.Total;

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
                t.Thesis, t.ExecutedAt, t.ExecutedBy, t.Currency, t.FxRate))
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
    private readonly TesouroDiretoClient _tesouro;

    public PricesController(PricingService pricing, TesouroDiretoClient tesouro)
    {
        _pricing = pricing;
        _tesouro = tesouro;
    }

    /// <summary>GET /api/prices/current/{ticker} — Preco atual em BRL e na moeda original</summary>
    [HttpGet("current/{ticker}")]
    public async Task<ActionResult> GetCurrentPrice(string ticker)
    {
        // Titulo publico: PU de compra (preco do trade de compra) e PU de venda
        if (TesouroDireto.IsTreasuryTicker(ticker))
        {
            var bond = await _tesouro.GetLatestQuoteAsync(ticker);
            if (bond == null)
                return NotFound(new { error = $"{ticker.Trim().ToUpper()} nao esta sendo negociado no Tesouro Direto" });

            var price = bond.CanBuy ? bond.BuyPrice : bond.SellPrice;
            return Ok(new
            {
                ticker = bond.Bond.Ticker,
                price,
                nativePrice = price,
                currency = PricingService.BaseCurrency,
                fxRate = 1.0,
                name = bond.Bond.DisplayName,
                buyPrice = bond.BuyPrice,
                sellPrice = bond.SellPrice,
                buyRate = bond.BuyRate,
                sellRate = bond.SellRate,
                baseDate = bond.BaseDate
            });
        }

        var quote = await _pricing.GetQuoteAsync(ticker.Trim().ToUpper());
        if (quote == null)
            return NotFound(new { error = $"Preco nao encontrado para {ticker}" });

        return Ok(new
        {
            ticker = ticker.Trim().ToUpper(),
            price = quote.PriceBrl,
            nativePrice = quote.NativePrice,
            currency = quote.Currency,
            fxRate = quote.FxRate
        });
    }
}

// ════════════════════════════════════════════════════════
// TESOURO DIRETO (titulos publicos)
// ════════════════════════════════════════════════════════

[ApiController]
[Authorize]
[Route("api/treasury")]
public class TreasuryController : ControllerBase
{
    private readonly TesouroDiretoClient _tesouro;

    public TreasuryController(TesouroDiretoClient tesouro) => _tesouro = tesouro;

    /// <summary>GET /api/treasury/bonds — Titulos ofertados hoje, com taxas e precos (PU)</summary>
    [HttpGet("bonds")]
    public async Task<ActionResult<List<TreasuryBondResponse>>> GetBonds()
    {
        var latest = await _tesouro.GetLatestAsync();
        if (latest.Count == 0)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Precos do Tesouro Direto indisponiveis no momento" });

        return Ok(latest.Select(q => new TreasuryBondResponse(
            Ticker: q.Bond.Ticker,
            Name: q.Bond.DisplayName,
            Type: q.Bond.Type.Name,
            Code: q.Bond.Type.Code,
            Maturity: TesouroDireto.ToIsoDate(q.Bond.Maturity),
            BaseDate: q.BaseDate,
            BuyRate: q.BuyRate,
            SellRate: q.SellRate,
            BuyPrice: q.BuyPrice,
            SellPrice: q.SellPrice,
            CanBuy: q.CanBuy)).ToList());
    }
}
