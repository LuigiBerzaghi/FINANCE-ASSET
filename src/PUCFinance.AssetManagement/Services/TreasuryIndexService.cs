using System.Globalization;
using System.Text.Json;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Valor Nominal Atualizado (VNA) dos titulos indexados, a partir das series mensais do Banco Central:
/// IPCA (SGS 433) para NTN-B e IGP-M (SGS 189) para NTN-C. Base R$ 1.000 em jul/2000.
/// No dia de pagamento do mes M, o VNA acumula a inflacao de jul/2000 ate M-1.
/// </summary>
public class TreasuryIndexService
{
    private const int IpcaSeries = 433;
    private const int IgpmSeries = 189;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    // Series mudam uma vez por mes: cache compartilhado entre requisicoes
    private static readonly Dictionary<int, (DateTimeOffset LoadedAt, Dictionary<(int Year, int Month), double> Values)> Cache = new();
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    private readonly HttpClient _http;
    private readonly ILogger<TreasuryIndexService> _logger;

    public TreasuryIndexService(IHttpClientFactory httpFactory, ILogger<TreasuryIndexService> logger)
    {
        _http = httpFactory.CreateClient();
        _logger = logger;
    }

    /// <summary>VNA da NTN-B na data de pagamento (null se o IPCA do mes anterior ainda nao saiu).</summary>
    public Task<double?> IpcaVnaAsync(DateTime paymentDate) => VnaAsync(IpcaSeries, paymentDate);

    /// <summary>VNA da NTN-C na data de pagamento (null se o IGP-M do mes anterior ainda nao saiu).</summary>
    public Task<double?> IgpmVnaAsync(DateTime paymentDate) => VnaAsync(IgpmSeries, paymentDate);

    private async Task<double?> VnaAsync(int series, DateTime paymentDate)
    {
        var monthly = await GetSeriesAsync(series);
        if (monthly == null)
            return null;

        var vna = 1000.0;
        var (year, month) = (2000, 7);
        while (year < paymentDate.Year || (year == paymentDate.Year && month < paymentDate.Month))
        {
            if (!monthly.TryGetValue((year, month), out var rate))
            {
                _logger.LogInformation("Serie SGS {Series}: {Month:00}/{Year} ainda nao publicado", series, month, year);
                return null;
            }

            vna *= 1 + rate / 100;
            if (++month == 13)
                (year, month) = (year + 1, 1);
        }

        return vna;
    }

    private async Task<Dictionary<(int Year, int Month), double>?> GetSeriesAsync(int series)
    {
        await CacheLock.WaitAsync();
        try
        {
            if (Cache.TryGetValue(series, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < CacheFor)
                return cached.Values;

            try
            {
                var url = $"https://api.bcb.gov.br/dados/serie/bcdata.sgs.{series}/dados?formato=json&dataInicial=01/07/2000";
                using var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var json = await JsonDocument.ParseAsync(stream);

                var values = new Dictionary<(int, int), double>();
                foreach (var item in json.RootElement.EnumerateArray())
                {
                    var date = DateTime.ParseExact(item.GetProperty("data").GetString()!, "dd/MM/yyyy", CultureInfo.InvariantCulture);
                    var value = double.Parse(item.GetProperty("valor").GetString()!, CultureInfo.InvariantCulture);
                    values[(date.Year, date.Month)] = value;
                }

                Cache[series] = (DateTimeOffset.UtcNow, values);
                return values;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar a serie SGS {Series} no Banco Central", series);
                return cached.Values;
            }
        }
        finally
        {
            CacheLock.Release();
        }
    }
}
