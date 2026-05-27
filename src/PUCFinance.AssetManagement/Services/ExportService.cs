using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;

namespace PUCFinance.AssetManagement.Services;

public class ExportService
{
    private readonly AppDbContext _db;

    public ExportService(AppDbContext db) => _db = db;

    /// <summary>
    /// Gera um Excel com abas: Resumo, Posicoes, Trades, NAV Diario, Metricas, Performance por Ativo
    /// </summary>
    public async Task<byte[]> ExportFundAsync(int fundId)
    {
        var fund = await _db.Funds.FindAsync(fundId)
            ?? throw new InvalidOperationException($"Fundo {fundId} nao encontrado");

        var positions = await _db.Positions.Where(p => p.FundId == fundId).ToListAsync();
        var trades = await _db.Trades.Where(t => t.FundId == fundId).OrderByDescending(t => t.ExecutedAt).ToListAsync();
        var navHistory = await _db.NavHistory.Where(n => n.FundId == fundId).OrderBy(n => n.Date).ToListAsync();
        var metrics = await _db.Metrics.Where(m => m.FundId == fundId).OrderByDescending(m => m.Date).ToListAsync();
        var posHistory = await _db.PositionHistory.Where(h => h.FundId == fundId).OrderBy(h => h.Date).ThenBy(h => h.Ticker).ToListAsync();
        var realized = await _db.RealizedPnl.Where(r => r.FundId == fundId).ToListAsync();
        var cash = await _db.Cash.FindAsync(fundId);
        var latestNav = navHistory.LastOrDefault();

        using var wb = new XLWorkbook();

        // ── Resumo ──────────────────────────
        var wsResumo = wb.AddWorksheet("Resumo");
        wsResumo.Cell(1, 1).Value = "PUC Finance - " + fund.Name;
        wsResumo.Cell(1, 1).Style.Font.Bold = true;
        wsResumo.Cell(1, 1).Style.Font.FontSize = 14;

        var resumoData = new (string, string)[]
        {
            ("Estrategia", fund.Strategy ?? "—"),
            ("Capital Inicial", $"R$ {fund.InitialCapital:N2}"),
            ("Total de Cotas", $"{fund.TotalShares:N0}"),
            ("Patrimonio Atual", $"R$ {latestNav?.TotalEquity ?? fund.InitialCapital:N2}"),
            ("Valor da Cota", $"{latestNav?.ShareValue ?? 1.0:F4}"),
            ("Caixa", $"R$ {cash?.Balance ?? fund.InitialCapital:N2}"),
            ("Posicoes Abertas", $"{positions.Count}"),
            ("Total de Trades", $"{trades.Count}"),
            ("Data", DateTime.Today.ToString("dd/MM/yyyy")),
        };
        for (int i = 0; i < resumoData.Length; i++)
        {
            wsResumo.Cell(3 + i, 1).Value = resumoData[i].Item1;
            wsResumo.Cell(3 + i, 1).Style.Font.Bold = true;
            wsResumo.Cell(3 + i, 2).Value = resumoData[i].Item2;
        }
        wsResumo.Columns().AdjustToContents();

        // ── Posicoes ────────────────────────
        var wsPos = wb.AddWorksheet("Posicoes");
        var posHeaders = new[] { "Ticker", "Side", "Quantidade", "Preco Medio", "Preco Atual", "Valor Mercado", "P&L Nao Realizado", "Peso %" };
        for (int i = 0; i < posHeaders.Length; i++)
        {
            wsPos.Cell(1, i + 1).Value = posHeaders[i];
            wsPos.Cell(1, i + 1).Style.Font.Bold = true;
        }
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var totalEquity = latestNav?.TotalEquity ?? fund.InitialCapital;
            wsPos.Cell(i + 2, 1).Value = p.Ticker;
            wsPos.Cell(i + 2, 2).Value = p.Side;
            wsPos.Cell(i + 2, 3).Value = p.Quantity;
            wsPos.Cell(i + 2, 4).Value = p.AvgPrice;
            wsPos.Cell(i + 2, 5).Value = p.CurrentPrice ?? 0;
            wsPos.Cell(i + 2, 6).Value = p.MarketValue ?? 0;
            wsPos.Cell(i + 2, 7).Value = p.UnrealizedPnl ?? 0;
            wsPos.Cell(i + 2, 8).Value = totalEquity > 0 ? ((p.MarketValue ?? 0) / totalEquity * 100) : 0;
        }
        wsPos.Columns().AdjustToContents();

        // ── Trades ──────────────────────────
        var wsTrades = wb.AddWorksheet("Trades");
        var tradeHeaders = new[] { "Data", "Ticker", "Side", "Quantidade", "Preco", "Tese", "Gestor" };
        for (int i = 0; i < tradeHeaders.Length; i++)
        {
            wsTrades.Cell(1, i + 1).Value = tradeHeaders[i];
            wsTrades.Cell(1, i + 1).Style.Font.Bold = true;
        }
        for (int i = 0; i < trades.Count; i++)
        {
            var t = trades[i];
            wsTrades.Cell(i + 2, 1).Value = t.ExecutedAt;
            wsTrades.Cell(i + 2, 2).Value = t.Ticker;
            wsTrades.Cell(i + 2, 3).Value = t.Side;
            wsTrades.Cell(i + 2, 4).Value = t.Quantity;
            wsTrades.Cell(i + 2, 5).Value = t.Price;
            wsTrades.Cell(i + 2, 6).Value = t.Thesis ?? "";
            wsTrades.Cell(i + 2, 7).Value = t.ExecutedBy ?? "";
        }
        wsTrades.Columns().AdjustToContents();

        // ── NAV Diario ──────────────────────
        var wsNav = wb.AddWorksheet("NAV Diario");
        var navHeaders = new[] { "Data", "Patrimonio", "Cotas", "Valor Cota", "Retorno Dia %", "Caixa" };
        for (int i = 0; i < navHeaders.Length; i++)
        {
            wsNav.Cell(1, i + 1).Value = navHeaders[i];
            wsNav.Cell(1, i + 1).Style.Font.Bold = true;
        }
        for (int i = 0; i < navHistory.Count; i++)
        {
            var n = navHistory[i];
            wsNav.Cell(i + 2, 1).Value = n.Date;
            wsNav.Cell(i + 2, 2).Value = n.TotalEquity;
            wsNav.Cell(i + 2, 3).Value = n.TotalShares;
            wsNav.Cell(i + 2, 4).Value = n.ShareValue;
            wsNav.Cell(i + 2, 5).Value = (n.DailyReturn ?? 0) * 100;
            wsNav.Cell(i + 2, 6).Value = n.CashBalance;
        }
        wsNav.Columns().AdjustToContents();

        // ── Metricas ────────────────────────
        var wsMetrics = wb.AddWorksheet("Metricas");
        var metHeaders = new[] { "Data", "Periodo", "Retorno %", "Volatilidade %", "Sharpe", "Max DD %", "Alpha %", "Beta", "Benchmark" };
        for (int i = 0; i < metHeaders.Length; i++)
        {
            wsMetrics.Cell(1, i + 1).Value = metHeaders[i];
            wsMetrics.Cell(1, i + 1).Style.Font.Bold = true;
        }
        for (int i = 0; i < metrics.Count; i++)
        {
            var m = metrics[i];
            wsMetrics.Cell(i + 2, 1).Value = m.Date;
            wsMetrics.Cell(i + 2, 2).Value = m.Period;
            wsMetrics.Cell(i + 2, 3).Value = (m.CumulativeReturn ?? 0) * 100;
            wsMetrics.Cell(i + 2, 4).Value = (m.Volatility ?? 0) * 100;
            wsMetrics.Cell(i + 2, 5).Value = m.SharpeRatio ?? 0;
            wsMetrics.Cell(i + 2, 6).Value = (m.MaxDrawdown ?? 0) * 100;
            wsMetrics.Cell(i + 2, 7).Value = (m.Alpha ?? 0) * 100;
            wsMetrics.Cell(i + 2, 8).Value = m.Beta ?? 0;
            wsMetrics.Cell(i + 2, 9).Value = m.BenchmarkName ?? "";
        }
        wsMetrics.Columns().AdjustToContents();

        // ── Performance por Ativo ───────────
        var wsPerfAtivo = wb.AddWorksheet("Performance Ativo");
        var perfHeaders = new[] { "Data", "Ticker", "Side", "Quantidade", "Preco Medio", "Preco Atual", "Valor Mercado", "P&L", "Retorno Dia %", "Contribuicao %", "Peso %" };
        for (int i = 0; i < perfHeaders.Length; i++)
        {
            wsPerfAtivo.Cell(1, i + 1).Value = perfHeaders[i];
            wsPerfAtivo.Cell(1, i + 1).Style.Font.Bold = true;
        }
        for (int i = 0; i < posHistory.Count; i++)
        {
            var h = posHistory[i];
            wsPerfAtivo.Cell(i + 2, 1).Value = h.Date;
            wsPerfAtivo.Cell(i + 2, 2).Value = h.Ticker;
            wsPerfAtivo.Cell(i + 2, 3).Value = h.Side;
            wsPerfAtivo.Cell(i + 2, 4).Value = h.Quantity;
            wsPerfAtivo.Cell(i + 2, 5).Value = h.AvgPrice;
            wsPerfAtivo.Cell(i + 2, 6).Value = h.CurrentPrice ?? 0;
            wsPerfAtivo.Cell(i + 2, 7).Value = h.MarketValue ?? 0;
            wsPerfAtivo.Cell(i + 2, 8).Value = h.UnrealizedPnl ?? 0;
            wsPerfAtivo.Cell(i + 2, 9).Value = (h.DailyReturn ?? 0) * 100;
            wsPerfAtivo.Cell(i + 2, 10).Value = (h.Contribution ?? 0) * 100;
            wsPerfAtivo.Cell(i + 2, 11).Value = (h.Weight ?? 0) * 100;
        }
        wsPerfAtivo.Columns().AdjustToContents();

        // ── P&L Realizado ───────────────────
        if (realized.Count > 0)
        {
            var wsReal = wb.AddWorksheet("PnL Realizado");
            var realHeaders = new[] { "Data", "Ticker", "Side", "Quantidade", "Preco Entrada", "Preco Saida", "P&L" };
            for (int i = 0; i < realHeaders.Length; i++)
            {
                wsReal.Cell(1, i + 1).Value = realHeaders[i];
                wsReal.Cell(1, i + 1).Style.Font.Bold = true;
            }
            for (int i = 0; i < realized.Count; i++)
            {
                var r = realized[i];
                wsReal.Cell(i + 2, 1).Value = r.ClosedAt;
                wsReal.Cell(i + 2, 2).Value = r.Ticker;
                wsReal.Cell(i + 2, 3).Value = r.Side;
                wsReal.Cell(i + 2, 4).Value = r.Quantity;
                wsReal.Cell(i + 2, 5).Value = r.EntryPrice;
                wsReal.Cell(i + 2, 6).Value = r.ExitPrice;
                wsReal.Cell(i + 2, 7).Value = r.Pnl;
            }
            wsReal.Columns().AdjustToContents();
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
