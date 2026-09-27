using System.Globalization;

namespace PUCFinance.AssetManagement.Services;

/// <summary>Taxas e precos de um titulo numa data base (fechamento da manha do Tesouro Direto).</summary>
public sealed record TreasuryQuote(
    TreasuryBond Bond,
    string BaseDate,
    double BuyRate,
    double SellRate,
    double BuyPrice,
    double SellPrice,
    double BasePrice)
{
    /// <summary>PU de compra zerado = titulo disponivel so para resgate.</summary>
    public bool CanBuy => BuyPrice > 0;
}

/// <summary>
/// Le o CSV oficial de precos e taxas do Tesouro Direto (Tesouro Transparente, dados abertos,
/// atualizado diariamente) e mantem em memoria. Singleton: o arquivo tem ~15 MB.
/// </summary>
public class TesouroDiretoClient
{
    public const string HttpClientName = "tesouro";

    private const string CsvUrl =
        "https://www.tesourotransparente.gov.br/ckan/dataset/df56aa42-484a-4a59-8184-7676580c81e3/resource/796d2059-14e9-44e3-80c9-2d9e30b405c1/download/PrecoTaxaTesouroDireto.csv";

    // Historico mantido em memoria; datas anteriores nao sao usadas pelo sistema
    private static readonly DateTime KeepFrom = new(2018, 1, 1);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TesouroDiretoClient> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private Snapshot? _snapshot;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public TesouroDiretoClient(IHttpClientFactory httpFactory, ILogger<TesouroDiretoClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public static void Configure(HttpClient http)
    {
        http.Timeout = TimeSpan.FromMinutes(3);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; PUCFinance/1.0)");
    }

    private sealed class Snapshot
    {
        public required DateTimeOffset LoadedAt { get; init; }
        public required string LatestBaseDate { get; init; }
        public required IReadOnlyList<TreasuryQuote> Latest { get; init; }
        public required IReadOnlyDictionary<string, List<TreasuryQuote>> HistoryByTicker { get; init; }
    }

    /// <summary>Titulos da data base mais recente (os negociaveis hoje).</summary>
    public async Task<IReadOnlyList<TreasuryQuote>> GetLatestAsync()
    {
        var snapshot = await GetSnapshotAsync();
        return snapshot?.Latest ?? [];
    }

    /// <summary>Cotacao mais recente do titulo, se ele ainda e negociado.</summary>
    public async Task<TreasuryQuote?> GetLatestQuoteAsync(string ticker)
    {
        var snapshot = await GetSnapshotAsync();
        return snapshot?.Latest.FirstOrDefault(q => q.Bond.Ticker == ticker.Trim().ToUpperInvariant());
    }

    /// <summary>Historico diario do titulo (datas base em ordem crescente), inclusive depois de vencido.</summary>
    public async Task<IReadOnlyList<TreasuryQuote>> GetHistoryAsync(string ticker)
    {
        var snapshot = await GetSnapshotAsync();
        return snapshot != null && snapshot.HistoryByTicker.TryGetValue(ticker.Trim().ToUpperInvariant(), out var history)
            ? history
            : [];
    }

    /// <summary>Identifica o titulo (com a data exata de vencimento) a partir do ticker.</summary>
    public async Task<TreasuryBond?> FindBondAsync(string ticker)
    {
        var history = await GetHistoryAsync(ticker);
        return history.Count > 0 ? history[^1].Bond : null;
    }

    /// <summary>Baixa o CSV em segundo plano (chamado na subida do app para esquentar o cache).</summary>
    public Task WarmUpAsync() => GetSnapshotAsync();

    private async Task<Snapshot?> GetSnapshotAsync()
    {
        var current = _snapshot;
        if (current != null && DateTimeOffset.UtcNow - current.LoadedAt < RefreshAfter)
            return current;

        await _loadLock.WaitAsync();
        try
        {
            current = _snapshot;
            if (current != null && DateTimeOffset.UtcNow - current.LoadedAt < RefreshAfter)
                return current;

            // Evita martelar o servidor do Tesouro quando ele esta fora: usa o que ja tem
            if (DateTimeOffset.UtcNow - _lastAttempt < RetryAfterFailure)
                return current;

            _lastAttempt = DateTimeOffset.UtcNow;
            var loaded = await DownloadAsync();
            if (loaded != null)
                _snapshot = loaded;

            return _snapshot;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<Snapshot?> DownloadAsync()
    {
        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            var started = DateTimeOffset.UtcNow;
            await using var stream = await http.GetStreamAsync(CsvUrl);
            var snapshot = await ParseAsync(stream);
            _logger.LogInformation("Tesouro Direto: {Count} titulos na data base {Date} (carregado em {Seconds:F1}s)",
                snapshot.Latest.Count, snapshot.LatestBaseDate, (DateTimeOffset.UtcNow - started).TotalSeconds);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao baixar precos do Tesouro Direto");
            return null;
        }
    }

    /// <summary>Formato: Tipo Titulo;Data Vencimento;Data Base;Taxa Compra Manha;Taxa Venda Manha;PU Compra Manha;PU Venda Manha;PU Base Manha</summary>
    private static async Task<Snapshot> ParseAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
        await reader.ReadLineAsync(); // cabecalho

        var byTicker = new Dictionary<string, List<TreasuryQuote>>();
        var bonds = new Dictionary<(string, DateTime), TreasuryBond>();
        var latestDate = DateTime.MinValue;

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var parts = line.Split(';');
            if (parts.Length < 8)
                continue;

            var type = TesouroDireto.FindTypeByName(parts[0]);
            if (type == null
                || !TryParseDate(parts[1], out var maturity)
                || !TryParseDate(parts[2], out var baseDate)
                || baseDate < KeepFrom)
                continue;

            if (!bonds.TryGetValue((type.Code, maturity), out var bond))
            {
                bond = new TreasuryBond(type, maturity);
                bonds[(type.Code, maturity)] = bond;
            }

            var quote = new TreasuryQuote(
                bond,
                TesouroDireto.ToIsoDate(baseDate),
                ParseNumber(parts[3]),
                ParseNumber(parts[4]),
                ParseNumber(parts[5]),
                ParseNumber(parts[6]),
                ParseNumber(parts[7]));

            if (!byTicker.TryGetValue(bond.Ticker, out var list))
            {
                list = new List<TreasuryQuote>();
                byTicker[bond.Ticker] = list;
            }

            list.Add(quote);
            if (baseDate > latestDate)
                latestDate = baseDate;
        }

        foreach (var list in byTicker.Values)
            list.Sort((a, b) => string.CompareOrdinal(a.BaseDate, b.BaseDate));

        var latestIso = TesouroDireto.ToIsoDate(latestDate);
        var latest = byTicker.Values
            .Select(list => list[^1])
            .Where(q => q.BaseDate == latestIso)
            .OrderBy(q => q.Bond.Type.Name)
            .ThenBy(q => q.Bond.Maturity)
            .ToList();

        return new Snapshot
        {
            LoadedAt = DateTimeOffset.UtcNow,
            LatestBaseDate = latestIso,
            Latest = latest,
            HistoryByTicker = byTicker,
        };
    }

    private static bool TryParseDate(string value, out DateTime date) =>
        DateTime.TryParseExact(value.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static double ParseNumber(string value) =>
        double.TryParse(value.Trim().Replace(".", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
}
