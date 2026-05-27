using Microsoft.EntityFrameworkCore;
using OoplesFinance.YahooFinanceAPI;
using OoplesFinance.YahooFinanceAPI.Enums;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

public class PricingService
{
    private readonly AppDbContext _db;
    private readonly ILogger<PricingService> _logger;
    private readonly YahooClient _yahoo = new();

    // Mapeamento de tickers internos → Yahoo Finance
    // BR: adiciona .SA | EUA: direto | Cripto: -USD | FX: =X
    private static readonly Dictionary<string, string> TickerOverrides = new()
    {
        // Benchmarks
        { "IBOVESPA", "^BVSP" },
        { "SPX", "^GSPC" },
        // Adicionar overrides específicos aqui se necessário
    };

    public PricingService(AppDbContext db, ILogger<PricingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Converte ticker interno para o formato do Yahoo Finance.
    /// PETR4 → PETR4.SA | AAPL → AAPL | BTC → BTC-USD
    /// </summary>
    public static string ToYahooTicker(string ticker)
    {
        ticker = ticker.Trim().ToUpper();

        if (TickerOverrides.TryGetValue(ticker, out var mapped))
            return mapped;

        // Ações BR: 4-6 chars, termina em dígito (PETR4, VALE3, BOVA11)
        if (ticker.Length >= 4 && ticker.Length <= 6 && char.IsDigit(ticker[^1]) && !ticker.Contains('.'))
            return $"{ticker}.SA";

        return ticker;
    }

    /// <summary>
    /// Busca preço de fechamento mais recente de um ticker via histórico (último dia útil).
    /// </summary>
    public async Task<double?> GetLatestPriceAsync(string ticker)
    {
        var yahooTicker = ToYahooTicker(ticker);

        try
        {
            // Puxa últimos 5 dias pra garantir que pega pelo menos 1 dia útil
            var startDate = DateTime.Now.AddDays(-5);
            var history = await _yahoo.GetHistoricalDataAsync(yahooTicker, DataFrequency.Daily, startDate);
            var latest = history.LastOrDefault();

            if (latest != null)
            {
                return latest.Close;
            }

            _logger.LogWarning("Ticker {Ticker} ({Yahoo}) — sem dados históricos recentes", ticker, yahooTicker);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar preço de {Ticker} ({Yahoo})", ticker, yahooTicker);
            return null;
        }
    }

    /// <summary>
    /// Busca preços históricos de um ticker a partir de uma data.
    /// </summary>
    public async Task<List<Price>> GetHistoricalPricesAsync(string ticker, DateTime from)
    {
        var yahooTicker = ToYahooTicker(ticker);
        var prices = new List<Price>();

        try
        {
            var history = await _yahoo.GetHistoricalDataAsync(yahooTicker, DataFrequency.Daily, from);

            foreach (var candle in history)
            {
                prices.Add(new Price
                {
                    Ticker = ticker,
                    Date = candle.Date.ToString("yyyy-MM-dd"),
                    Open = candle.Open,
                    High = candle.High,
                    Low = candle.Low,
                    Close = candle.Close,
                    AdjClose = candle.Close,
                    Volume = (long)candle.Volume,
                    Source = "yahoo"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar histórico de {Ticker} ({Yahoo})", ticker, yahooTicker);
        }

        return prices;
    }

    /// <summary>
    /// Busca e persiste preços de todos os tickers que estão em posições ativas.
    /// Retorna a quantidade de preços atualizados.
    /// </summary>
    public async Task<int> FetchAndStorePricesAsync()
    {
        // Pega todos os tickers únicos das posições ativas
        var tickers = await _db.Positions
            .Select(p => p.Ticker)
            .Distinct()
            .ToListAsync();

        // Adiciona benchmarks
        tickers.Add("IBOVESPA");

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var count = 0;

        foreach (var ticker in tickers)
        {
            var price = await GetLatestPriceAsync(ticker);
            if (price == null) continue;

            // Upsert no banco
            var existing = await _db.Prices
                .FirstOrDefaultAsync(p => p.Ticker == ticker && p.Date == today);

            if (existing != null)
            {
                existing.Close = price.Value;
                existing.FetchedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                _db.Prices.Add(new Price
                {
                    Ticker = ticker,
                    Date = today,
                    Close = price.Value,
                    Source = "yahoo"
                });
            }

            count++;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Preços atualizados: {Count}/{Total} tickers", count, tickers.Count);
        return count;
    }
}
