using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Grava o fechamento diario do IBOVESPA (^BVSP, Yahoo) na tabela benchmarks, usada no Alpha/Beta.
/// Busca desde alguns dias antes do ultimo registro (ou do primeiro NAV), entao dias em que o batch
/// nao rodou sao preenchidos na execucao seguinte.
/// </summary>
public class IbovespaService
{
    public const string BenchmarkName = "IBOVESPA";
    private const string YahooSymbol = "^BVSP";

    private readonly AppDbContext _db;
    private readonly YahooChartClient _yahoo;
    private readonly ILogger<IbovespaService> _logger;

    public IbovespaService(AppDbContext db, YahooChartClient yahoo, ILogger<IbovespaService> logger)
    {
        _db = db;
        _yahoo = yahoo;
        _logger = logger;
    }

    public async Task FetchAndStoreAsync()
    {
        try
        {
            var lastStored = await _db.Benchmarks
                .Where(b => b.Name == BenchmarkName)
                .MaxAsync(b => (string?)b.Date);
            var firstNav = await _db.NavHistory.MinAsync(n => (string?)n.Date);

            var anchor = lastStored ?? firstNav ?? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var from = DateTime.ParseExact(anchor[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-10);

            var chart = await _yahoo.GetDailyAsync(YahooSymbol, from);
            if (chart == null || chart.Closes.Count == 0)
            {
                _logger.LogWarning("IBOVESPA: Yahoo nao retornou fechamentos desde {From:yyyy-MM-dd}", from);
                return;
            }

            var firstDate = chart.Closes[0].Date;
            var existing = await _db.Benchmarks
                .Where(b => b.Name == BenchmarkName && string.Compare(b.Date, firstDate) >= 0)
                .ToDictionaryAsync(b => b.Date);

            // Fechamento anterior ao trecho baixado e o primeiro fechamento ja salvo (base do acumulado)
            var previous = await _db.Benchmarks
                .Where(b => b.Name == BenchmarkName && string.Compare(b.Date, firstDate) < 0)
                .OrderByDescending(b => b.Date)
                .Select(b => (double?)b.Value)
                .FirstOrDefaultAsync();
            var baseValue = await _db.Benchmarks
                .Where(b => b.Name == BenchmarkName)
                .OrderBy(b => b.Date)
                .Select(b => (double?)b.Value)
                .FirstOrDefaultAsync() ?? chart.Closes[0].Close;

            var added = 0;
            foreach (var (date, close) in chart.Closes)
            {
                if (!existing.TryGetValue(date, out var row))
                {
                    row = new Benchmark { Name = BenchmarkName, Date = date, Source = "yahoo" };
                    _db.Benchmarks.Add(row);
                    added++;
                }

                row.Value = close;
                row.DailyReturn = previous > 0 ? close / previous.Value - 1 : null;
                row.Cumulative = baseValue > 0 ? close / baseValue - 1 : null;
                previous = close;
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("IBOVESPA atualizado: {Added} novos dias (ultimo {Date})", added, chart.Closes[^1].Date);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(ex, "Erro ao atualizar o IBOVESPA");
        }
    }
}
