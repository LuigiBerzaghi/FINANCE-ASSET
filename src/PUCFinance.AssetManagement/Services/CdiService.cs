using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Busca CDI diario da API do Bacen (serie 12) e acumula como benchmark.
/// </summary>
public class CdiService
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly ILogger<CdiService> _logger;

    public CdiService(AppDbContext db, IHttpClientFactory httpFactory, ILogger<CdiService> logger)
    {
        _db = db;
        _http = httpFactory.CreateClient();
        _logger = logger;
    }

    /// <summary>
    /// Busca CDI diario do Bacen e salva na tabela benchmarks.
    /// Serie 12 = CDI diario (taxa % ao dia).
    /// </summary>
    public async Task FetchAndStoreCdiAsync()
    {
        try
        {
            // Pega a ultima data que temos
            var lastDate = await _db.Benchmarks
                .Where(b => b.Name == "CDI")
                .OrderByDescending(b => b.Date)
                .Select(b => b.Date)
                .FirstOrDefaultAsync();

            var from = lastDate != null
                ? DateTime.ParseExact(lastDate[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddDays(1)
                : DateTime.Today.AddMonths(-6); // 6 meses de historico inicial

            var to = DateTime.Today;
            if (from > to) return;

            var fromStr = from.ToString("dd/MM/yyyy");
            var toStr = to.ToString("dd/MM/yyyy");

            // API do Bacen - Serie 12 (CDI diario)
            var url = "https://api.bcb.gov.br/dados/serie/bcdata.sgs.12/dados"
                + $"?formato=json&dataInicial={Uri.EscapeDataString(fromStr)}&dataFinal={Uri.EscapeDataString(toStr)}";

            using var response = await _http.GetAsync(url);
            var content = await response.Content.ReadAsStringAsync();

            // Sem CDI publicado no periodo (fim de semana, feriado ou o proprio dia: o Bacen publica com
            // um dia de atraso) a API responde 404 com {"erro": ...} em vez de uma lista
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound || !content.TrimStart().StartsWith('['))
            {
                if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    response.EnsureSuccessStatusCode();

                _logger.LogInformation("Bacen ainda sem CDI novo para {From} a {To}", fromStr, toStr);
                return;
            }

            response.EnsureSuccessStatusCode();
            var records = JsonSerializer.Deserialize<List<BcbRecord>>(content);

            if (records == null || records.Count == 0)
            {
                _logger.LogWarning("Nenhum dado CDI retornado do Bacen");
                return;
            }

            // Pega o ultimo valor acumulado que temos
            var lastCumulative = (await _db.Benchmarks
                .Where(b => b.Name == "CDI")
                .OrderByDescending(b => b.Date)
                .Select(b => (double?)b.Cumulative)
                .FirstOrDefaultAsync()) ?? 0.0;

            var cumulative = 1.0 + lastCumulative;
            var count = 0;

            foreach (var record in records)
            {
                var date = DateTime.ParseExact(record.data, "dd/MM/yyyy", null).ToString("yyyy-MM-dd");
                var dailyRate = double.Parse(record.valor, System.Globalization.CultureInfo.InvariantCulture) / 100;

                cumulative *= (1 + dailyRate);

                var existing = await _db.Benchmarks
                    .FirstOrDefaultAsync(b => b.Name == "CDI" && b.Date == date);

                if (existing == null)
                {
                    _db.Benchmarks.Add(new Benchmark
                    {
                        Name = "CDI",
                        Date = date,
                        Value = dailyRate * 100, // taxa % ao dia
                        DailyReturn = dailyRate,
                        Cumulative = cumulative - 1, // retorno acumulado desde o inicio
                        Source = "bacen"
                    });
                    count++;
                }
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("CDI atualizado: {Count} novos registros", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar CDI do Bacen");
        }
    }

    private record BcbRecord(string data, string valor);
}
