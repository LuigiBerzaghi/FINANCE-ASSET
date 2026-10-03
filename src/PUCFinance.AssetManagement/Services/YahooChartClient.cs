using System.Globalization;
using System.Net;
using System.Text.Json;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Fechamentos diarios de um simbolo do Yahoo e proventos em dinheiro (data ex → valor bruto por acao,
/// na moeda de cotacao), com as datas no fuso da bolsa.
/// </summary>
public sealed record YahooChart(
    string Symbol,
    string? Currency,
    IReadOnlyList<(string Date, double Close)> Closes,
    IReadOnlyList<(string Date, double Amount)> Dividends);

/// <summary>
/// Cliente da API de grafico do Yahoo Finance (v8/finance/chart).
/// Os candles vem com horario em UTC; cambio (fuso de Londres) comeca as 23:00 UTC do dia anterior,
/// entao a data de cada candle e convertida para o fuso da bolsa antes de ser usada.
/// </summary>
public class YahooChartClient
{
    public const string HttpClientName = "yahoo";

    private readonly HttpClient _http;
    private readonly ILogger<YahooChartClient> _logger;

    public YahooChartClient(IHttpClientFactory httpFactory, ILogger<YahooChartClient> logger)
    {
        _http = httpFactory.CreateClient(HttpClientName);
        _logger = logger;
    }

    public static void Configure(HttpClient http)
    {
        http.BaseAddress = new Uri("https://query1.finance.yahoo.com/");
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; PUCFinance/1.0)");
    }

    /// <summary>
    /// Fechamentos diarios e proventos de <paramref name="from"/> ate hoje. O ultimo ponto pode ser o preco
    /// do pregao em andamento. Retorna null se o simbolo nao existe ou a consulta falhou.
    /// </summary>
    public async Task<YahooChart?> GetDailyAsync(string symbol, DateTime from)
    {
        var period1 = new DateTimeOffset(DateTime.SpecifyKind(from.Date, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var period2 = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var url = $"v8/finance/chart/{Uri.EscapeDataString(symbol)}?period1={period1}&period2={period2}&interval=1d&includePrePost=false&events=div";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(url);
                if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var json = await JsonDocument.ParseAsync(stream);
                return Parse(symbol, json.RootElement);
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao consultar o Yahoo para {Symbol}", symbol);
                return null;
            }
        }

        _logger.LogWarning("Yahoo indisponivel para {Symbol} apos 3 tentativas", symbol);
        return null;
    }

    private static YahooChart? Parse(string symbol, JsonElement root)
    {
        if (!root.TryGetProperty("chart", out var chart)
            || !chart.TryGetProperty("result", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
            return null;

        var result = results[0];
        var meta = result.GetProperty("meta");
        var currency = meta.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
        var gmtOffset = meta.TryGetProperty("gmtoffset", out var g) && g.ValueKind == JsonValueKind.Number
            ? g.GetInt32()
            : 0;
        var timeZone = meta.TryGetProperty("exchangeTimezoneName", out var tz) && tz.ValueKind == JsonValueKind.String
            ? FindTimeZone(tz.GetString())
            : null;

        string LocalDate(long unixSeconds)
        {
            var utc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            var local = timeZone != null
                ? TimeZoneInfo.ConvertTime(utc, timeZone)
                : utc.ToOffset(TimeSpan.FromSeconds(gmtOffset));
            return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var dividends = ParseDividends(result, LocalDate);
        var closes = new List<(string Date, double Close)>();
        if (!result.TryGetProperty("timestamp", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array)
            return new YahooChart(symbol, currency, closes, dividends);

        var closeValues = result.GetProperty("indicators").GetProperty("quote")[0].GetProperty("close");

        // Se duas barras caem na mesma data (barra diaria + preco atual), fica a mais recente
        var byDate = new SortedDictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < timestamps.GetArrayLength() && i < closeValues.GetArrayLength(); i++)
        {
            if (closeValues[i].ValueKind != JsonValueKind.Number)
                continue;

            var close = closeValues[i].GetDouble();
            if (close <= 0)
                continue;

            byDate[LocalDate(timestamps[i].GetInt64())] = close;
        }

        closes.AddRange(byDate.Select(kv => (kv.Key, kv.Value)));
        return new YahooChart(symbol, currency, closes, dividends);
    }

    /// <summary>
    /// Proventos em events.dividends ({"amount": valor bruto por acao, "date": data ex em Unix}).
    /// O Yahoo nao separa dividendo de JCP; dois proventos na mesma data ex sao somados.
    /// </summary>
    private static List<(string Date, double Amount)> ParseDividends(JsonElement result, Func<long, string> localDate)
    {
        var byDate = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (result.TryGetProperty("events", out var events)
            && events.ValueKind == JsonValueKind.Object
            && events.TryGetProperty("dividends", out var dividends)
            && dividends.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in dividends.EnumerateObject())
            {
                var dividend = item.Value;
                if (dividend.ValueKind != JsonValueKind.Object
                    || !dividend.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number
                    || !dividend.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.Number)
                    continue;

                var value = amount.GetDouble();
                if (value <= 0)
                    continue;

                var day = localDate(date.GetInt64());
                byDate[day] = byDate.GetValueOrDefault(day) + value;
            }
        }

        return byDate.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static TimeZoneInfo? FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
