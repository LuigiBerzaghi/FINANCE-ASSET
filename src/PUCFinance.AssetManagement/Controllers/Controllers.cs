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
[Route("api/[controller]")]
public class FundsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ExportService _export;

    public FundsController(AppDbContext db, ExportService export)
    {
        _db = db;
        _export = export;
    }

    /// <summary>GET /api/funds — Lista todos os fundos com resumo</summary>
    [HttpGet]
    public async Task<ActionResult<List<FundSummaryResponse>>> GetAll()
    {
        var funds = await _db.Funds.Where(f => f.IsActive == 1).ToListAsync();
        var summaries = new List<FundSummaryResponse>();

        foreach (var fund in funds)
        {
            var latestNav = await _db.NavHistory
                .Where(n => n.FundId == fund.Id)
                .OrderByDescending(n => n.Date)
                .FirstOrDefaultAsync();

            var positionCount = await _db.Positions.CountAsync(p => p.FundId == fund.Id);

            summaries.Add(new FundSummaryResponse(
                Id: fund.Id,
                Name: fund.Name,
                Strategy: fund.Strategy,
                TotalEquity: latestNav?.TotalEquity ?? fund.InitialCapital,
                ShareValue: latestNav?.ShareValue ?? 1.0,
                DailyReturn: latestNav?.DailyReturn ?? 0,
                CashBalance: latestNav?.CashBalance ?? fund.InitialCapital,
                PositionCount: positionCount
            ));
        }

        return Ok(summaries);
    }

    /// <summary>POST /api/funds — Cria um novo fundo</summary>
    [HttpPost]
    public async Task<ActionResult<Fund>> Create([FromBody] CreateFundRequest request)
    {
        var fund = new Fund
        {
            Name = request.Name,
            Strategy = request.Strategy,
            InitialCapital = request.InitialCapital,
            TotalShares = request.TotalShares
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
        var fund = await _db.Funds.FindAsync(id);
        if (fund == null) return NotFound();

        var positions = await _db.Positions
            .Where(p => p.FundId == id)
            .ToListAsync();

        var latestNav = await _db.NavHistory
            .Where(n => n.FundId == id)
            .OrderByDescending(n => n.Date)
            .FirstOrDefaultAsync();

        var totalEquity = latestNav?.TotalEquity ?? fund.InitialCapital;

        var result = positions.Select(p => new PositionResponse(
            Ticker: p.Ticker,
            Side: p.Side,
            Quantity: p.Quantity,
            AvgPrice: p.AvgPrice,
            CurrentPrice: p.CurrentPrice,
            MarketValue: p.MarketValue,
            UnrealizedPnl: p.UnrealizedPnl,
            Weight: totalEquity > 0 ? (p.MarketValue ?? 0) / totalEquity : null
        )).ToList();

        return Ok(result);
    }

    /// <summary>GET /api/funds/{id}/nav — Histórico de NAV</summary>
    [HttpGet("{id}/nav")]
    public async Task<ActionResult<List<NavPointResponse>>> GetNav(int id)
    {
        var navs = await _db.NavHistory
            .Where(n => n.FundId == id)
            .OrderBy(n => n.Date)
            .Select(n => new NavPointResponse(n.Date, n.TotalEquity, n.ShareValue, n.DailyReturn))
            .ToListAsync();

        return Ok(navs);
    }

    /// <summary>GET /api/funds/{id}/metrics — Métricas atuais</summary>
    [HttpGet("{id}/metrics")]
    public async Task<ActionResult<List<MetricsResponse>>> GetMetrics(int id)
    {
        var latestDate = await _db.Metrics
            .Where(m => m.FundId == id)
            .MaxAsync(m => (string?)m.Date);

        if (latestDate == null) return Ok(new List<MetricsResponse>());

        var metrics = await _db.Metrics
            .Where(m => m.FundId == id && m.Date == latestDate)
            .Select(m => new MetricsResponse(
                m.Period, m.CumulativeReturn, m.AnnualizedReturn,
                m.Volatility, m.SharpeRatio, m.MaxDrawdown,
                m.Alpha, m.Beta, m.BenchmarkName))
            .ToListAsync();

        return Ok(metrics);
    }

    /// <summary>GET /api/funds/{id}/performance — Performance por ativo</summary>
    [HttpGet("{id}/performance")]
    public async Task<ActionResult<List<AssetPerformanceResponse>>> GetPerformance(int id)
    {
        var fund = await _db.Funds.FindAsync(id);
        if (fund == null) return NotFound();

        var positions = await _db.Positions.Where(p => p.FundId == id).ToListAsync();
        var latestNav = await _db.NavHistory
            .Where(n => n.FundId == id).OrderByDescending(n => n.Date).FirstOrDefaultAsync();
        var totalEquity = latestNav?.TotalEquity ?? fund.InitialCapital;

        var result = new List<AssetPerformanceResponse>();

        foreach (var pos in positions)
        {
            var realizedTotal = await _db.RealizedPnl
                .Where(r => r.FundId == id && r.Ticker == pos.Ticker)
                .SumAsync(r => (double?)r.Pnl) ?? 0;

            var totalPnl = (pos.UnrealizedPnl ?? 0) + realizedTotal;
            var costBasis = Math.Abs(pos.Quantity) * pos.AvgPrice;
            double? returnPct = costBasis > 0 ? totalPnl / costBasis : null;
            var weight = totalEquity > 0 ? (pos.MarketValue ?? 0) / totalEquity : 0;

            var dailyHistory = await _db.PositionHistory
                .Where(h => h.FundId == id && h.Ticker == pos.Ticker)
                .OrderBy(h => h.Date)
                .Select(h => new AssetDailyPoint(
                    h.Date, h.CurrentPrice, h.DailyReturn, h.Contribution, h.Weight))
                .ToListAsync();

            var totalContribution = dailyHistory.Sum(d => d.Contribution ?? 0);

            result.Add(new AssetPerformanceResponse(
                Ticker: pos.Ticker,
                Side: pos.Side,
                Quantity: pos.Quantity,
                AvgPrice: pos.AvgPrice,
                CurrentPrice: pos.CurrentPrice,
                MarketValue: pos.MarketValue,
                UnrealizedPnl: pos.UnrealizedPnl,
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
    [HttpGet("{id}/export")]
    public async Task<IActionResult> ExportExcel(int id)
    {
        try
        {
            var fund = await _db.Funds.FindAsync(id);
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
        var fund = await _db.Funds.FindAsync(id);
        if (fund == null) return NotFound();

        var positions = await _db.Positions.Where(p => p.FundId == id).ToListAsync();
        var latestNav = await _db.NavHistory
            .Where(n => n.FundId == id).OrderByDescending(n => n.Date).FirstOrDefaultAsync();
        var cash = await _db.Cash.FindAsync(id);

        var totalEquity = latestNav?.TotalEquity ?? fund.InitialCapital;
        var cashBalance = cash?.Balance ?? fund.InitialCapital;

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
            var mv = pos.MarketValue ?? 0;

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
        var fund = await _db.Funds.FindAsync(id);
        if (fund == null) return NotFound();

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
                .Where(h => h.FundId == id && string.Compare(h.Date, fromDate) >= 0)
                .ToListAsync();

            if (history.Count == 0) continue;

            // Agrupa contribuicao por classe
            var byClass = new Dictionary<string, double>();

            foreach (var h in history)
            {
                var asset = await _db.Assets.FindAsync(h.Ticker);
                var assetClass = asset?.AssetClass ?? "unknown";
                if (!byClass.ContainsKey(assetClass)) byClass[assetClass] = 0;
                byClass[assetClass] += h.Contribution ?? 0;
            }

            var totalReturn = byClass.Values.Sum();

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
        var fund = await _db.Funds.FindAsync(id);
        if (fund == null) return NotFound();

        var navs = await _db.NavHistory
            .Where(n => n.FundId == id)
            .OrderBy(n => n.Date)
            .ToListAsync();

        if (navs.Count < 2) return Ok(new CdiBenchmarkResponse(0, 0, 0, "inception", new()));

        var startDate = navs.First().Date;
        var firstShareValue = navs.First().ShareValue;

        var cdis = await _db.Benchmarks
            .Where(b => b.Name == "CDI" && string.Compare(b.Date, startDate) >= 0)
            .OrderBy(b => b.Date)
            .ToListAsync();

        // CDI acumulado desde o inicio do fundo
        var cdiCumulative = 1.0;
        var cdiByDate = new Dictionary<string, double>();

        foreach (var cdi in cdis)
        {
            cdiCumulative *= (1 + (cdi.DailyReturn ?? 0));
            cdiByDate[cdi.Date] = cdiCumulative - 1;
        }

        var series = navs.Select(n =>
        {
            var fundCum = firstShareValue > 0 ? (n.ShareValue / firstShareValue) - 1 : 0;
            var cdiCum = cdiByDate.GetValueOrDefault(n.Date, 0);
            return new CdiComparisonPoint(n.Date, fundCum, cdiCum);
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
}

// ════════════════════════════════════════════════════════
// ASSETS (dropdown de ativos)
// ════════════════════════════════════════════════════════

[ApiController]
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
[Route("api/[controller]")]
public class TradesController : ControllerBase
{
    private readonly TradeService _tradeService;
    private readonly AppDbContext _db;

    public TradesController(TradeService tradeService, AppDbContext db)
    {
        _tradeService = tradeService;
        _db = db;
    }

    /// <summary>POST /api/trades — Executa um trade</summary>
    [HttpPost]
    public async Task<ActionResult<Trade>> Execute([FromBody] ExecuteTradeRequest request)
    {
        try
        {
            var trade = await _tradeService.ExecuteTradeAsync(request);
            return CreatedAtAction(nameof(GetByFund), new { fundId = trade.FundId }, trade);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>GET /api/trades/fund/{fundId} — Histórico de trades de um fundo</summary>
    [HttpGet("fund/{fundId}")]
    public async Task<ActionResult<List<TradeResponse>>> GetByFund(int fundId)
    {
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

    public BatchController(BatchService batch) => _batch = batch;

    /// <summary>POST /api/batch/run — Executa o pipeline diário</summary>
    [HttpPost("run")]
    public async Task<ActionResult<BatchResultResponse>> Run()
    {
        var result = await _batch.RunDailyUpdateAsync();
        return Ok(result);
    }
}
