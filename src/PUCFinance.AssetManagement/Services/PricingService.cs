using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>Cotacao na moeda original do ativo e convertida para BRL.</summary>
public sealed record PriceQuote(double NativePrice, string Currency, double FxRate, double PriceBrl);

/// <summary>Provento em dinheiro por acao na data ex: valor bruto na moeda do ativo e convertido para BRL.</summary>
public sealed record DividendQuote(string ExDate, double NativeAmount, string Currency, double FxRate, double AmountBrl);

/// <summary>
/// Ticker do Yahoo e moeda de cotacao de um ativo. PriceDivisor converte cotacoes em
/// centavos para a unidade da moeda (ex.: acoes de Londres em GBp → GBP = /100).
/// </summary>
public sealed record ResolvedTicker(string YahooTicker, string Currency, double PriceDivisor = 1);

/// <summary>Serie diaria de fechamentos (yyyy-MM-dd → valor), com busca do ultimo valor ate uma data.</summary>
public sealed class DailySeries
{
    private readonly List<string> _dates = new();
    private readonly List<double> _values = new();

    public DailySeries(IEnumerable<KeyValuePair<string, double>> points)
    {
        foreach (var point in points.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (_dates.Count > 0 && _dates[^1] == point.Key)
            {
                _values[^1] = point.Value;
                continue;
            }

            _dates.Add(point.Key);
            _values.Add(point.Value);
        }
    }

    public int Count => _dates.Count;

    /// <summary>Ultimo valor com data menor ou igual a <paramref name="date"/> (yyyy-MM-dd).</summary>
    public double? ValueAt(string date)
    {
        var index = _dates.BinarySearch(date, StringComparer.Ordinal);
        if (index < 0)
            index = ~index - 1;

        return index >= 0 ? _values[index] : null;
    }
}

public class PricingService
{
    public const string BaseCurrency = "BRL";

    private readonly AppDbContext _db;
    private readonly YahooChartClient _yahoo;
    private readonly TesouroDiretoClient _tesouro;
    private readonly ILogger<PricingService> _logger;
    private readonly Dictionary<string, ResolvedTicker?> _resolveCache = new();
    private readonly Dictionary<string, (DateTime From, YahooChart? Chart)> _chartCache = new();

    // Mapeamento de tickers internos → Yahoo Finance
    // BR: adiciona .SA | EUA: direto | Cripto: -USD | FX: =X
    private static readonly Dictionary<string, string> TickerOverrides = new()
    {
        // Benchmarks
        { "IBOVESPA", "^BVSP" },
        { "SPX", "^GSPC" },
        // Adicionar overrides específicos aqui se necessário
    };

    // Moedas que o Yahoo cota em centavos
    private static readonly Dictionary<string, (string Currency, double Divisor)> MinorUnitCurrencies = new()
    {
        { "GBp", ("GBP", 100) },
        { "GBX", ("GBP", 100) },
        { "ZAc", ("ZAR", 100) },
        { "ILA", ("ILS", 100) },
    };

    public PricingService(AppDbContext db, YahooChartClient yahoo, TesouroDiretoClient tesouro, ILogger<PricingService> logger)
    {
        _db = db;
        _yahoo = yahoo;
        _tesouro = tesouro;
        _logger = logger;
    }

    /// <summary>
    /// Converte ticker interno para o formato do Yahoo Finance.
    /// PETR4 → PETR4.SA | AAPL → AAPL | IBOVESPA → ^BVSP
    /// Ativos do catalogo usam o yahoo_ticker cadastrado (ver ResolveAsync).
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
    /// Resolve o ticker do Yahoo e a moeda de cotacao. Ativos do catalogo usam o cadastro;
    /// tickers da B3 (.SA) e indices sao BRL; os demais tem a moeda consultada no Yahoo.
    /// Retorna null quando a moeda nao pode ser determinada.
    /// </summary>
    public async Task<ResolvedTicker?> ResolveAsync(string ticker)
    {
        ticker = ticker.Trim().ToUpper();
        if (_resolveCache.TryGetValue(ticker, out var cached))
            return cached;

        var resolved = await ResolveUncachedAsync(ticker);
        _resolveCache[ticker] = resolved;
        return resolved;
    }

    private async Task<ResolvedTicker?> ResolveUncachedAsync(string ticker)
    {
        // Titulos publicos: precificados pelo Tesouro Direto, em BRL
        if (TesouroDireto.IsTreasuryTicker(ticker))
            return new ResolvedTicker(ticker, BaseCurrency);

        var asset = await _db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Ticker == ticker);
        if (asset != null)
        {
            var yahooTicker = string.IsNullOrWhiteSpace(asset.YahooTicker)
                ? ToYahooTicker(ticker)
                : asset.YahooTicker.Trim();
            return NormalizeCurrency(yahooTicker, string.IsNullOrWhiteSpace(asset.Currency) ? BaseCurrency : asset.Currency.Trim());
        }

        var fallbackTicker = ToYahooTicker(ticker);
        if (fallbackTicker.EndsWith(".SA", StringComparison.OrdinalIgnoreCase) || fallbackTicker.StartsWith('^'))
            return new ResolvedTicker(fallbackTicker, BaseCurrency);

        var chart = await GetChartAsync(fallbackTicker, DateTime.Today.AddDays(-7));
        if (!string.IsNullOrWhiteSpace(chart?.Currency))
            return NormalizeCurrency(fallbackTicker, chart.Currency.Trim());

        _logger.LogWarning("Ticker {Ticker} ({Yahoo}) — moeda nao encontrada no Yahoo", ticker, fallbackTicker);
        return null;
    }

    private static ResolvedTicker NormalizeCurrency(string yahooTicker, string currency)
    {
        if (MinorUnitCurrencies.TryGetValue(currency, out var minor))
            return new ResolvedTicker(yahooTicker, minor.Currency, minor.Divisor);

        return new ResolvedTicker(yahooTicker, currency.ToUpperInvariant());
    }

    /// <summary>
    /// Busca preço de fechamento mais recente de um ticker (último dia útil),
    /// na moeda original do ativo. Usado para benchmarks (indices em pontos).
    /// </summary>
    public async Task<double?> GetLatestPriceAsync(string ticker)
    {
        var resolved = await ResolveAsync(ticker);
        var yahooTicker = resolved?.YahooTicker ?? ToYahooTicker(ticker);
        var close = await GetCloseAsync(yahooTicker, null);
        return close / (resolved?.PriceDivisor ?? 1);
    }

    /// <summary>
    /// Busca o preço atual e converte para BRL pelo câmbio mais recente.
    /// </summary>
    public async Task<PriceQuote?> GetQuoteAsync(string ticker)
    {
        // Titulos publicos sao marcados pelo PU de venda (o que o fundo receberia hoje)
        if (TesouroDireto.IsTreasuryTicker(ticker))
            return await GetTreasuryQuoteAsync(ticker, "short");

        var resolved = await ResolveAsync(ticker);
        if (resolved == null)
            return null;

        var close = await GetCloseAsync(resolved.YahooTicker, null);
        if (close == null)
            return null;

        var fxRate = await GetFxToBrlAsync(resolved.Currency);
        if (fxRate == null)
            return null;

        var nativePrice = close.Value / resolved.PriceDivisor;
        return new PriceQuote(nativePrice, resolved.Currency, fxRate.Value, nativePrice * fxRate.Value);
    }

    /// <summary>
    /// Preco de execucao de um trade. Titulos publicos: compra pelo PU de compra e venda pelo PU de venda;
    /// demais ativos: preco atual convertido para BRL.
    /// </summary>
    public async Task<PriceQuote?> GetTradeQuoteAsync(string ticker, string side)
    {
        ticker = ticker.Trim().ToUpper();
        return TesouroDireto.IsTreasuryTicker(ticker)
            ? await GetTreasuryQuoteAsync(ticker, side)
            : await GetQuoteAsync(ticker);
    }

    private async Task<PriceQuote?> GetTreasuryQuoteAsync(string ticker, string side)
    {
        var quote = await _tesouro.GetLatestQuoteAsync(ticker);
        var price = side == "long" ? quote?.BuyPrice : quote?.SellPrice;
        return price > 0 ? new PriceQuote(price.Value, BaseCurrency, 1, price.Value) : null;
    }

    /// <summary>
    /// Fechamento do ativo (na unidade da moeda, ja sem centavos) no último pregão até a data.
    /// </summary>
    public async Task<double?> GetNativeCloseOnAsync(ResolvedTicker resolved, DateTime date)
    {
        var close = await GetCloseAsync(resolved.YahooTicker, date);
        return close / resolved.PriceDivisor;
    }

    /// <summary>
    /// Taxa para converter 1 unidade da moeda em BRL (ex.: USD → USDBRL=X).
    /// Com data, usa o último fechamento até aquela data.
    /// </summary>
    public async Task<double?> GetFxToBrlAsync(string currency, DateTime? date = null)
    {
        currency = currency.Trim().ToUpper();
        if (currency == BaseCurrency)
            return 1;

        return await GetCloseAsync(FxTicker(currency), date);
    }

    /// <summary>
    /// Serie diaria de fechamentos em BRL desde <paramref name="from"/>: preço nativo × câmbio do mesmo dia
    /// (ou o último câmbio disponível antes dele). Null se o ticker ou o câmbio não tiverem dados.
    /// </summary>
    public async Task<DailySeries?> GetBrlCloseHistoryAsync(string ticker, DateTime from)
    {
        if (TesouroDireto.IsTreasuryTicker(ticker))
        {
            var history = await _tesouro.GetHistoryAsync(ticker);
            var fromIso = TesouroDireto.ToIsoDate(from);
            var treasuryPoints = history
                .Where(q => string.CompareOrdinal(q.BaseDate, fromIso) >= 0 && q.SellPrice > 0)
                .Select(q => new KeyValuePair<string, double>(q.BaseDate, q.SellPrice))
                .ToList();
            return treasuryPoints.Count > 0 ? new DailySeries(treasuryPoints) : null;
        }

        var resolved = await ResolveAsync(ticker);
        if (resolved == null)
            return null;

        var native = await GetChartAsync(resolved.YahooTicker, from);
        if (native == null || native.Closes.Count == 0)
            return null;

        DailySeries? fx = null;
        if (resolved.Currency != BaseCurrency)
        {
            // Começa antes para ter câmbio no primeiro dia da série
            var fxChart = await GetChartAsync(FxTicker(resolved.Currency), from.AddDays(-10));
            if (fxChart == null || fxChart.Closes.Count == 0)
                return null;

            fx = new DailySeries(fxChart.Closes.Select(p => new KeyValuePair<string, double>(p.Date, p.Close)));
        }

        var points = new List<KeyValuePair<string, double>>();
        foreach (var (date, close) in native.Closes)
        {
            var rate = fx == null ? 1 : fx.ValueAt(date);
            if (rate == null)
                continue;

            points.Add(new(date, close / resolved.PriceDivisor * rate.Value));
        }

        return new DailySeries(points);
    }

    /// <summary>
    /// Proventos em dinheiro (dividendos/JCP) com data ex a partir de <paramref name="from"/>, valor bruto por acao
    /// convertido para BRL pelo cambio da data ex. Titulos publicos nao tem (cupons sao do TreasuryService).
    /// Null se o ticker nao foi encontrado no Yahoo; proventos sem cambio ficam de fora (tenta no proximo batch).
    /// </summary>
    public async Task<List<DividendQuote>?> GetDividendsBrlAsync(string ticker, DateTime from)
    {
        if (TesouroDireto.IsTreasuryTicker(ticker))
            return new List<DividendQuote>();

        var resolved = await ResolveAsync(ticker);
        if (resolved == null)
            return null;

        var chart = await GetChartAsync(resolved.YahooTicker, from);
        if (chart == null)
            return null;

        var fromIso = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var result = new List<DividendQuote>();
        foreach (var (date, amount) in chart.Dividends.Where(d => string.CompareOrdinal(d.Date, fromIso) >= 0))
        {
            var exDate = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var fxRate = await GetFxToBrlAsync(resolved.Currency, exDate);
            if (fxRate is not > 0)
            {
                _logger.LogWarning("Provento de {Ticker} em {Date}: sem cambio {Currency}BRL; tenta no proximo batch",
                    ticker, date, resolved.Currency);
                continue;
            }

            var native = amount / resolved.PriceDivisor;
            result.Add(new DividendQuote(date, native, resolved.Currency, fxRate.Value, native * fxRate.Value));
        }

        return result;
    }

    /// <summary>Fracao minima negociavel de criptomoedas (satoshi).</summary>
    public const double CryptoQuantityStep = 0.00000001;

    /// <summary>
    /// Menor quantidade negociavel de um ativo, usada para converter um valor em BRL em quantidade:
    /// titulos publicos em 0,01; criptomoedas em fracoes de 0,00000001; demais ativos em unidades inteiras.
    /// </summary>
    public async Task<double> QuantityStepAsync(string ticker)
    {
        ticker = ticker.Trim().ToUpper();
        if (TesouroDireto.IsTreasuryTicker(ticker))
            return TesouroDireto.QuantityStep;

        var assetClass = await _db.Assets.AsNoTracking()
            .Where(a => a.Ticker == ticker)
            .Select(a => a.AssetClass)
            .FirstOrDefaultAsync();
        return assetClass == "crypto" ? CryptoQuantityStep : 1;
    }

    /// <summary>
    /// Maior quantidade (multiplo de <paramref name="step"/>) cujo valor nao passa de <paramref name="amount"/>.
    /// </summary>
    public static double QuantityForAmount(double amount, double price, double step)
    {
        var units = Math.Floor(amount / price / step * (1 + 1e-12) + 1e-9);
        var decimals = step >= 1 ? 0 : (int)Math.Round(-Math.Log10(step));
        return Math.Round(units * step, decimals);
    }

    private static string FxTicker(string currency) => $"{currency}{BaseCurrency}=X";

    /// <summary>
    /// Fechamento mais recente de um símbolo do Yahoo. Com data, o último fechamento até ela.
    /// </summary>
    private async Task<double?> GetCloseAsync(string yahooTicker, DateTime? date)
    {
        var reference = date?.Date ?? DateTime.Today;
        // Busca alguns dias para trás para cobrir fins de semana e feriados
        var chart = await GetChartAsync(yahooTicker, reference.AddDays(-10));
        if (chart == null)
            return null;

        var limit = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var close = chart.Closes
            .Where(c => limit == null || string.CompareOrdinal(c.Date, limit) <= 0)
            .Select(c => (double?)c.Close)
            .LastOrDefault();

        if (close == null)
            _logger.LogWarning("{Yahoo} — sem fechamento até {Date}", yahooTicker, limit ?? "hoje");

        return close;
    }

    /// <summary>Consulta o Yahoo uma vez por símbolo e reaproveita enquanto cobrir o período pedido.</summary>
    private async Task<YahooChart?> GetChartAsync(string yahooTicker, DateTime from)
    {
        if (_chartCache.TryGetValue(yahooTicker, out var cached) && cached.From <= from.Date)
            return cached.Chart;

        var chart = await _yahoo.GetDailyAsync(yahooTicker, from.Date);
        _chartCache[yahooTicker] = (from.Date, chart);
        return chart;
    }

    /// <summary>
    /// Busca e persiste preços de todos os tickers que estão em posições ativas.
    /// Ativos sao gravados em BRL; benchmarks ficam no valor original (pontos do indice).
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
        var benchmarks = new[] { "IBOVESPA" };
        tickers.AddRange(benchmarks);

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var count = 0;

        foreach (var ticker in tickers)
        {
            var price = benchmarks.Contains(ticker)
                ? await GetLatestPriceAsync(ticker)
                : (await GetQuoteAsync(ticker))?.PriceBrl;
            if (price == null) continue;

            await UpsertPriceAsync(ticker, today, price.Value);
            count++;
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Preços atualizados: {Count}/{Total} tickers", count, tickers.Count);
        return count;
    }

    /// <summary>Insere ou atualiza o fechamento de um ticker numa data (não salva).</summary>
    public async Task UpsertPriceAsync(string ticker, string date, double close)
    {
        var existing = _db.Prices.Local.FirstOrDefault(p => p.Ticker == ticker && p.Date == date)
            ?? await _db.Prices.FirstOrDefaultAsync(p => p.Ticker == ticker && p.Date == date);

        if (existing != null)
        {
            existing.Close = close;
            existing.FetchedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            return;
        }

        _db.Prices.Add(new Price
        {
            Ticker = ticker,
            Date = date,
            Close = close,
            Source = "yahoo"
        });
    }
}
