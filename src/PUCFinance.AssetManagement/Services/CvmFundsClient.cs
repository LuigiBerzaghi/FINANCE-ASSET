using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace PUCFinance.AssetManagement.Services;

/// <summary>Cota de um fundo numa data (yyyy-MM-dd), em BRL.</summary>
public sealed record FundQuote(string Date, double Quota);

/// <summary>Classe ou subclasse de fundo aberta a aplicacoes, segundo o cadastro da CVM.</summary>
public sealed record CvmFund(
    string Ticker,
    string Cnpj,              // formatado: 00.000.000/0000-00
    string? Subclass,
    string Name,
    string? Classification,   // Renda Fixa, Multimercado, Acoes, Cambial
    string? AnbimaClass,
    bool IsFic,               // classe de cotas: investe em outros fundos
    string? TargetAudience);  // Publico Geral, Qualificado, Profissional

/// <summary>Fundo encontrado na busca, com a cota mais recente publicada.</summary>
public sealed record CvmFundMatch(CvmFund Fund, FundQuote LastQuote, double? Equity);

/// <summary>
/// Le os dados abertos da CVM e mantem em memoria (singleton):
/// - informe diario (inf_diario_fi_AAAAMM.zip, um arquivo por mes, ~12 MB): cota de cada classe/subclasse
///   por dia. A cota do dia D sai no arquivo publicado de madrugada em D+2 (os fundos tem ate D+1 para enviar).
/// - cadastro (registro_fundo_classe.zip): nome, classificacao e se a classe/subclasse aceita aplicacoes.
/// Os meses atual e anterior sao renovados a cada 1h (a cota do fim do mes chega no arquivo do mes anterior);
/// meses mais antigos (historico) ficam no maximo 3 em memoria. Se a CVM falhar, usa a copia anterior.
/// </summary>
public class CvmFundsClient
{
    public const string HttpClientName = "cvm";

    private const string DailyUrl = "https://dados.cvm.gov.br/dados/FI/DOC/INF_DIARIO/DADOS/inf_diario_fi_{0}.zip";
    private const string RegistryUrl = "https://dados.cvm.gov.br/dados/FI/CAD/DADOS/registro_fundo_classe.zip";
    private const string ActiveStatus = "Em Funcionamento Normal";

    private static readonly TimeSpan RecentRefresh = TimeSpan.FromHours(1);
    private static readonly TimeSpan OldRefresh = TimeSpan.FromDays(1);
    private static readonly TimeSpan RegistryRefresh = TimeSpan.FromHours(12);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private const int MaxOldMonths = 3;

    // O informe diario mensal existe desde 2021; historico mais antigo nao e usado pelo sistema
    private const int MaxHistoryMonths = 24;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CvmFundsClient> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, MonthData> _months = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.Ordinal);
    private Registry? _registry;

    public CvmFundsClient(IHttpClientFactory httpFactory, ILogger<CvmFundsClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public static void Configure(HttpClient http)
    {
        http.Timeout = TimeSpan.FromMinutes(3);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; PUCFinance/1.0)");
    }

    private readonly record struct QuotePoint(int Date, double Quota);

    private sealed class MonthData
    {
        public required string Month { get; init; }
        public required DateTimeOffset LoadedAt { get; init; }
        public required IReadOnlyDictionary<string, QuotePoint[]> Quotes { get; init; }
        public required IReadOnlyDictionary<string, double> LatestEquity { get; init; }
        public int LatestDate { get; init; }  // data mais recente com cota de algum fundo (yyyyMMdd; 0 = vazio)
        public DateTimeOffset LastUsed { get; set; }
    }

    private sealed record RegistryEntry(CvmFund Fund, string SearchKey, string CnpjDigits);

    private sealed class Registry
    {
        public required DateTimeOffset LoadedAt { get; init; }
        public required IReadOnlyDictionary<string, RegistryEntry> ByTicker { get; init; }
    }

    /// <summary>
    /// Fundos abertos a aplicacao cujo nome contem todas as palavras da busca (ou cujo CNPJ contem os
    /// digitos), com cota publicada no mes atual ou no anterior; maiores patrimonios primeiro.
    /// Null se o cadastro ou as cotas da CVM estao indisponiveis.
    /// </summary>
    public async Task<IReadOnlyList<CvmFundMatch>?> SearchAsync(string query, int limit)
    {
        await _lock.WaitAsync();
        try
        {
            var registry = await LoadRegistryLockedAsync();
            var recent = await LoadRecentMonthsLockedAsync();
            if (registry == null || recent.Count == 0)
                return null;

            var text = query.Trim();
            var ticker = FundQuotas.IsFundTicker(text) ? text.ToUpperInvariant() : null;
            var byCnpj = text.Length > 0 && text.All(c => char.IsAsciiDigit(c) || c is '.' or '/' or '-' or ' ');
            var digits = FundQuotas.Digits(text);
            var words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (ticker == null && (byCnpj ? digits.Length < 3 : words.Length == 0))
                return [];

            var matches = new List<CvmFundMatch>();
            foreach (var entry in registry.ByTicker.Values)
            {
                var hit = ticker != null
                    ? entry.Fund.Ticker == ticker
                    : byCnpj
                        ? entry.CnpjDigits.Contains(digits, StringComparison.Ordinal)
                        : words.All(w => entry.SearchKey.Contains(w, StringComparison.Ordinal));
                if (!hit)
                    continue;

                var quote = LatestQuote(recent, entry.Fund.Ticker);
                if (quote == null)
                    continue;

                matches.Add(new CvmFundMatch(entry.Fund, quote.Value.Quote, quote.Value.Equity));
            }

            return matches
                .OrderByDescending(m => m.Equity ?? 0)
                .ThenBy(m => m.Fund.Name, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Classe/subclasse aberta a aplicacoes pelo ticker; null se nao existe, esta encerrada, e exclusiva
    /// ou e fechada. Lanca InvalidOperationException se o cadastro da CVM estiver indisponivel.
    /// </summary>
    public async Task<CvmFund?> GetFundAsync(string ticker)
    {
        await _lock.WaitAsync();
        try
        {
            var registry = await LoadRegistryLockedAsync()
                ?? throw new InvalidOperationException("Cadastro de fundos da CVM indisponivel no momento. Tente novamente em instantes.");
            return registry.ByTicker.TryGetValue(ticker.Trim().ToUpperInvariant(), out var entry) ? entry.Fund : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Cota mais recente publicada (mes atual ou anterior); null se o fundo nao tem cota recente ou se a CVM esta
    /// indisponivel (melhor sem preco, que mantem a ultima marcacao, do que uma cota velha).
    /// </summary>
    public async Task<FundQuote?> GetLatestQuoteAsync(string ticker)
    {
        await _lock.WaitAsync();
        try
        {
            var recent = await LoadRecentMonthsLockedAsync();
            return LatestQuote(recent, ticker.Trim().ToUpperInvariant())?.Quote;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Data mais recente com cota publicada de algum fundo (mes atual ou anterior); null se a CVM esta
    /// indisponivel. Mostra ate onde os dados da CVM chegaram.
    /// </summary>
    public async Task<string?> GetLatestDataDateAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var latest = (await LoadRecentMonthsLockedAsync()).Select(m => m.LatestDate).DefaultIfEmpty(0).Max();
            return latest > 0 ? ToIsoDate(latest) : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Primeira cota publicada com data maior ou igual a <paramref name="date"/> (yyyy-MM-dd): a cota de uma
    /// ordem desse dia (feriado ou dia sem cota cai no proximo dia com cota). Null se ainda nao saiu ou se
    /// algum mes nao pode ser lido (espera o proximo batch em vez de pular para a cota de um mes seguinte).
    /// </summary>
    public async Task<FundQuote?> GetQuoteOnOrAfterAsync(string ticker, string date)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        var from = ToIntDate(date);

        await _lock.WaitAsync();
        try
        {
            foreach (var month in MonthsSince(FundQuotas.ParseDate(date)))
            {
                var data = await LoadMonthLockedAsync(month);
                if (data == null)
                    return null;

                if (!data.Quotes.TryGetValue(ticker, out var points))
                    continue;

                foreach (var point in points)
                {
                    if (point.Date >= from)
                        return new FundQuote(ToIsoDate(point.Date), point.Quota);
                }
            }

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Cotas diarias desde <paramref name="from"/> ate a mais recente, em ordem de data. Vazio se algum mes nao
    /// pode ser lido: um historico com buraco marcaria dias passados com cota errada.
    /// </summary>
    public async Task<IReadOnlyList<FundQuote>> GetHistoryAsync(string ticker, DateTime from)
    {
        ticker = ticker.Trim().ToUpperInvariant();
        var fromInt = ToIntDate(BrazilTime.ToIsoDate(from));
        var result = new List<FundQuote>();

        await _lock.WaitAsync();
        try
        {
            foreach (var month in MonthsSince(from))
            {
                var data = await LoadMonthLockedAsync(month);
                if (data == null)
                    return [];

                if (!data.Quotes.TryGetValue(ticker, out var points))
                    continue;

                result.AddRange(points
                    .Where(p => p.Date >= fromInt)
                    .Select(p => new FundQuote(ToIsoDate(p.Date), p.Quota)));
            }
        }
        finally
        {
            _lock.Release();
        }

        return result;
    }

    // ── Informe diario ───────────────────────────────

    private static string MonthKey(DateTime date) => date.ToString("yyyyMM", CultureInfo.InvariantCulture);

    private static string CurrentMonth() => MonthKey(BrazilTime.Now);

    private static string PreviousMonth() => MonthKey(BrazilTime.Now.AddMonths(-1));

    /// <summary>Meses de <paramref name="from"/> ate o atual, em ordem (no maximo MaxHistoryMonths).</summary>
    private static List<string> MonthsSince(DateTime from)
    {
        var now = BrazilTime.Now;
        var current = new DateTime(now.Year, now.Month, 1);
        var start = new DateTime(from.Year, from.Month, 1);
        var oldest = current.AddMonths(-(MaxHistoryMonths - 1));
        if (start < oldest)
            start = oldest;

        var months = new List<string>();
        for (var month = start; month <= current; month = month.AddMonths(1))
            months.Add(MonthKey(month));
        return months;
    }

    /// <summary>
    /// Mes atual e anterior, o mais recente primeiro. Sem o mes atual (CVM fora do ar e nada em cache) a lista
    /// vem vazia: a cota do mes anterior seria velha. O anterior entra se estiver disponivel.
    /// </summary>
    private async Task<List<MonthData>> LoadRecentMonthsLockedAsync()
    {
        var current = await LoadMonthLockedAsync(CurrentMonth());
        if (current == null)
            return [];

        var result = new List<MonthData> { current };
        var previous = await LoadMonthLockedAsync(PreviousMonth());
        if (previous != null)
            result.Add(previous);

        return result;
    }

    private static (FundQuote Quote, double? Equity)? LatestQuote(IEnumerable<MonthData> newestFirst, string ticker)
    {
        foreach (var month in newestFirst)
        {
            if (month.Quotes.TryGetValue(ticker, out var points) && points.Length > 0)
            {
                var last = points[^1];
                double? equity = month.LatestEquity.TryGetValue(ticker, out var value) ? value : null;
                return (new FundQuote(ToIsoDate(last.Date), last.Quota), equity);
            }
        }

        return null;
    }

    private async Task<MonthData?> LoadMonthLockedAsync(string month)
    {
        var now = DateTimeOffset.UtcNow;
        var recent = month == CurrentMonth() || month == PreviousMonth();
        _months.TryGetValue(month, out var cached);

        if (cached != null && now - cached.LoadedAt < (recent ? RecentRefresh : OldRefresh))
        {
            cached.LastUsed = now;
            return cached;
        }

        // CVM fora do ar: usa o que ja tem e tenta de novo daqui a pouco
        if (_lastAttempt.TryGetValue(month, out var lastAttempt) && now - lastAttempt < RetryAfterFailure)
            return cached;

        _lastAttempt[month] = now;
        var loaded = await DownloadMonthAsync(month);
        if (loaded == null)
            return cached;

        loaded.LastUsed = now;
        _months[month] = loaded;
        EvictOldMonths();
        return loaded;
    }

    /// <summary>Mantem os meses atual e anterior e os MaxOldMonths meses antigos usados mais recentemente.</summary>
    private void EvictOldMonths()
    {
        var keep = new HashSet<string>(StringComparer.Ordinal) { CurrentMonth(), PreviousMonth() };
        var stale = _months.Values
            .Where(m => !keep.Contains(m.Month))
            .OrderByDescending(m => m.LastUsed)
            .Skip(MaxOldMonths)
            .Select(m => m.Month)
            .ToList();

        foreach (var month in stale)
        {
            _months.Remove(month);
            _lastAttempt.Remove(month);
        }
    }

    private async Task<MonthData?> DownloadMonthAsync(string month)
    {
        var url = string.Format(CultureInfo.InvariantCulture, DailyUrl, month);
        try
        {
            var started = DateTimeOffset.UtcNow;
            using var buffer = await DownloadAsync(url);
            if (buffer == null)
            {
                // Arquivo do mes ainda nao criado (inicio do mes): sem cotas, sem erro
                _logger.LogInformation("CVM: informe diario de {Month} ainda nao publicado", month);
                return Empty(month);
            }

            var data = ParseMonth(month, buffer);
            _logger.LogInformation("CVM: informe diario de {Month} com cotas de {Count} fundos (carregado em {Seconds:F1}s)",
                month, data.Quotes.Count, (DateTimeOffset.UtcNow - started).TotalSeconds);
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao baixar o informe diario de fundos da CVM ({Month})", month);
            return null;
        }
    }

    /// <summary>Baixa um arquivo inteiro para a memoria (o zip precisa de leitura aleatoria). Null se nao existe (404).</summary>
    private async Task<MemoryStream?> DownloadAsync(string url)
    {
        var http = _httpFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer);
        buffer.Position = 0;
        return buffer;
    }

    private static MonthData Empty(string month) => new()
    {
        Month = month,
        LoadedAt = DateTimeOffset.UtcNow,
        Quotes = new Dictionary<string, QuotePoint[]>(),
        LatestEquity = new Dictionary<string, double>(),
    };

    /// <summary>
    /// Formato (desde a RCVM 175): TP_FUNDO_CLASSE;CNPJ_FUNDO_CLASSE;ID_SUBCLASSE;DT_COMPTC;VL_TOTAL;VL_QUOTA;
    /// VL_PATRIM_LIQ;CAPTC_DIA;RESG_DIA;NR_COTST. Colunas localizadas pelo nome (arquivos antigos usam CNPJ_FUNDO).
    /// </summary>
    private static MonthData ParseMonth(string month, Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Informe diario {month} sem arquivo CSV");

        using var reader = new StreamReader(entry.Open(), Encoding.Latin1);
        var header = (reader.ReadLine() ?? throw new InvalidDataException($"Informe diario {month} vazio")).Split(';');
        var cnpjColumn = Column(header, "CNPJ_FUNDO_CLASSE", "CNPJ_FUNDO");
        var subclassColumn = OptionalColumn(header, "ID_SUBCLASSE");
        var dateColumn = Column(header, "DT_COMPTC");
        var quotaColumn = Column(header, "VL_QUOTA");
        var equityColumn = OptionalColumn(header, "VL_PATRIM_LIQ");
        var minParts = new[] { cnpjColumn, subclassColumn, dateColumn, quotaColumn, equityColumn }.Max() + 1;

        var points = new Dictionary<string, List<QuotePoint>>(StringComparer.Ordinal);
        var equity = new Dictionary<string, (int Date, double Value)>(StringComparer.Ordinal);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(';');
            if (parts.Length < minParts)
                continue;

            var cnpj = FundQuotas.Digits(parts[cnpjColumn]);
            var date = ParseIntDate(parts[dateColumn]);
            if (cnpj.Length != 14 || date == 0)
                continue;

            if (!double.TryParse(parts[quotaColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var quota)
                || !double.IsFinite(quota) || quota <= 0)
                continue;

            var ticker = FundQuotas.BuildTicker(cnpj, subclassColumn >= 0 ? parts[subclassColumn] : null);
            if (!points.TryGetValue(ticker, out var list))
            {
                list = new List<QuotePoint>();
                points[ticker] = list;
            }

            list.Add(new QuotePoint(date, quota));

            if (equityColumn >= 0
                && double.TryParse(parts[equityColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value)
                && (!equity.TryGetValue(ticker, out var previous) || date >= previous.Date))
                equity[ticker] = (date, value);
        }

        var quotes = new Dictionary<string, QuotePoint[]>(points.Count, StringComparer.Ordinal);
        foreach (var (ticker, list) in points)
            quotes[ticker] = Consolidate(list);

        return new MonthData
        {
            Month = month,
            LoadedAt = DateTimeOffset.UtcNow,
            Quotes = quotes,
            LatestEquity = equity.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal),
            LatestDate = quotes.Values.Select(p => p[^1].Date).DefaultIfEmpty(0).Max(),
        };
    }

    /// <summary>Pontos em ordem de data; se a mesma data vier repetida (reenvio), vale a ultima linha.</summary>
    private static QuotePoint[] Consolidate(List<QuotePoint> points)
    {
        // O arquivo costuma vir em ordem de data por fundo: uma passada so
        var ordered = new List<QuotePoint>(points.Count);
        foreach (var point in points)
        {
            if (ordered.Count == 0 || point.Date > ordered[^1].Date)
                ordered.Add(point);
            else if (point.Date == ordered[^1].Date)
                ordered[^1] = point;
            else
                return points
                    .Select((p, index) => (Point: p, Index: index))
                    .GroupBy(x => x.Point.Date)
                    .Select(g => g.MaxBy(x => x.Index).Point)
                    .OrderBy(p => p.Date)
                    .ToArray();
        }

        return ordered.ToArray();
    }

    // ── Cadastro ─────────────────────────────────────

    private async Task<Registry?> LoadRegistryLockedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var cached = _registry;
        if (cached != null && now - cached.LoadedAt < RegistryRefresh)
            return cached;

        const string attemptKey = "registry";
        if (_lastAttempt.TryGetValue(attemptKey, out var lastAttempt) && now - lastAttempt < RetryAfterFailure)
            return cached;

        _lastAttempt[attemptKey] = now;
        try
        {
            var started = DateTimeOffset.UtcNow;
            using var buffer = await DownloadAsync(RegistryUrl)
                ?? throw new InvalidDataException("Cadastro de fundos da CVM nao encontrado (404)");
            var registry = ParseRegistry(buffer);
            _logger.LogInformation("CVM: cadastro com {Count} classes/subclasses abertas a aplicacao (carregado em {Seconds:F1}s)",
                registry.ByTicker.Count, (DateTimeOffset.UtcNow - started).TotalSeconds);
            _registry = registry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao baixar o cadastro de fundos da CVM");
        }

        return _registry;
    }

    private sealed record ClassRow(
        string Cnpj, string Name, string Status, string? Classification, string? AnbimaClass, bool IsFic,
        string? TargetAudience, bool Investable);

    /// <summary>
    /// registro_classe.csv (classes de fundos FIF) e registro_subclasse.csv. Entram so classes e subclasses
    /// em funcionamento normal, nao exclusivas e abertas (que aceitam aplicacoes de qualquer cotista).
    /// </summary>
    private static Registry ParseRegistry(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var byTicker = new Dictionary<string, RegistryEntry>(StringComparer.Ordinal);
        var classesById = new Dictionary<string, ClassRow>(StringComparer.Ordinal);

        foreach (var row in ReadCsv(zip, "registro_classe.csv"))
        {
            var type = row.Get("Tipo_Classe") ?? string.Empty;
            var cnpj = FundQuotas.Digits(row.Get("CNPJ_Classe") ?? string.Empty);
            var name = row.Get("Denominacao_Social")?.Trim();
            if (!type.Contains("FIF", StringComparison.OrdinalIgnoreCase) || cnpj.Length != 14 || string.IsNullOrEmpty(name))
                continue;

            var status = row.Get("Situacao")?.Trim() ?? string.Empty;
            var classRow = new ClassRow(
                cnpj,
                name,
                status,
                Blank(row.Get("Classificacao")),
                Blank(row.Get("Classificacao_Anbima")),
                row.Get("Classe_Cotas")?.Trim() == "S",
                Blank(row.Get("Publico_Alvo")),
                IsInvestable(status, row.Get("Exclusivo"), row.Get("Forma_Condominio")));

            var id = row.Get("ID_Registro_Classe")?.Trim();
            if (!string.IsNullOrEmpty(id))
                classesById[id] = classRow;

            if (classRow.Investable)
                AddEntry(byTicker, classRow, subclass: null, classRow.Name, classRow.TargetAudience);
        }

        foreach (var row in ReadCsv(zip, "registro_subclasse.csv"))
        {
            var classId = row.Get("ID_Registro_Classe")?.Trim();
            var subclass = row.Get("ID_Subclasse")?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(classId) || string.IsNullOrEmpty(subclass)
                || !classesById.TryGetValue(classId, out var classRow) || classRow.Status != ActiveStatus)
                continue;

            if (!IsInvestable(row.Get("Situacao"), row.Get("Exclusivo"), row.Get("Forma_Condominio")))
                continue;

            var name = SubclassName(classRow.Name, row.Get("Denominacao_Social"));
            AddEntry(byTicker, classRow, subclass, name, Blank(row.Get("Publico_Alvo")) ?? classRow.TargetAudience);
        }

        return new Registry { LoadedAt = DateTimeOffset.UtcNow, ByTicker = byTicker };
    }

    private static void AddEntry(
        Dictionary<string, RegistryEntry> byTicker, ClassRow classRow, string? subclass, string name, string? targetAudience)
    {
        var ticker = FundQuotas.BuildTicker(classRow.Cnpj, subclass);
        if (!FundQuotas.IsFundTicker(ticker) || byTicker.ContainsKey(ticker))
            return;

        var fund = new CvmFund(
            ticker,
            FundQuotas.FormatCnpj(classRow.Cnpj),
            subclass,
            name,
            classRow.Classification,
            classRow.AnbimaClass,
            classRow.IsFic,
            targetAudience);
        byTicker[ticker] = new RegistryEntry(fund, Normalize(name), classRow.Cnpj);
    }

    private static bool IsInvestable(string? status, string? exclusive, string? condominium) =>
        status?.Trim() == ActiveStatus
        && exclusive?.Trim() != "S"
        && !string.Equals(condominium?.Trim(), "Fechado", StringComparison.OrdinalIgnoreCase);

    /// <summary>Subclasse com nome curto ("SUBCLASSE A") leva o nome da classe na frente.</summary>
    private static string SubclassName(string className, string? subclassName)
    {
        var name = subclassName?.Trim();
        if (string.IsNullOrEmpty(name))
            return className;

        return name.Length <= 30 ? $"{className} - {name}" : name;
    }

    /// <summary>Linha de um CSV da CVM com acesso as colunas pelo nome do cabecalho.</summary>
    private readonly struct CsvRow(IReadOnlyDictionary<string, int> columns, string[] parts)
    {
        public string? Get(string column) =>
            columns.TryGetValue(column, out var index) && index < parts.Length ? parts[index] : null;
    }

    private static IEnumerable<CsvRow> ReadCsv(ZipArchive zip, string fileName)
    {
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Cadastro da CVM sem o arquivo {fileName}");

        using var reader = new StreamReader(entry.Open(), Encoding.Latin1);
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var header = (reader.ReadLine() ?? string.Empty).Split(';');
        for (var i = 0; i < header.Length; i++)
            columns.TryAdd(header[i].Trim(), i);

        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return new CsvRow(columns, line.Split(';'));
    }

    // ── Utilitarios ──────────────────────────────────

    private static int Column(string[] header, params string[] names)
    {
        var index = OptionalColumn(header, names);
        return index >= 0
            ? index
            : throw new InvalidDataException($"Coluna {string.Join("/", names)} nao encontrada no informe diario");
    }

    private static int OptionalColumn(string[] header, params string[] names)
    {
        foreach (var name in names)
        {
            var index = Array.FindIndex(header, h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                return index;
        }

        return -1;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Maiusculas sem acentos, com pontuacao trocada por espaco (para a busca por nome).</summary>
    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            builder.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }

        return builder.ToString();
    }

    /// <summary>yyyy-MM-dd → yyyyMMdd (0 se invalida).</summary>
    private static int ParseIntDate(string value)
    {
        value = value.Trim();
        return value.Length == 10
            && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Year * 10000 + date.Month * 100 + date.Day
            : 0;
    }

    private static int ToIntDate(string isoDate) => ParseIntDate(isoDate[..10]);

    private static string ToIsoDate(int date) =>
        string.Create(CultureInfo.InvariantCulture, $"{date / 10000:D4}-{date / 100 % 100:D2}-{date % 100:D2}");
}
