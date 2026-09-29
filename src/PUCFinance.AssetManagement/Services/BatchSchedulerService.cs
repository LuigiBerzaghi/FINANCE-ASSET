using Cronos;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Roda o batch diario dentro do proprio app: uma vez logo apos a subida (recupera o dia se o app
/// reiniciou ou perdeu o horario) e depois no horario agendado (padrao: 19:00 de Brasilia, seg-sex).
/// BATCH_SCHEDULE muda o horario (expressao cron, fuso de Brasilia); BATCH_SCHEDULE=off desliga.
/// </summary>
public class BatchSchedulerService : BackgroundService
{
    public const string DefaultSchedule = "0 19 * * 1-5";

    // Espera a carga do Tesouro Direto e a migracao de cambio da subida terminarem
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<BatchSchedulerService> _logger;

    public BatchSchedulerService(IServiceProvider services, IConfiguration config, ILogger<BatchSchedulerService> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var setting = _config["BATCH_SCHEDULE"]?.Trim();
        if (string.Equals(setting, "off", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Batch automatico desligado (BATCH_SCHEDULE=off)");
            return;
        }

        CronExpression schedule;
        try
        {
            schedule = CronExpression.Parse(string.IsNullOrWhiteSpace(setting) ? DefaultSchedule : setting);
        }
        catch (CronFormatException ex)
        {
            _logger.LogError(ex, "BATCH_SCHEDULE invalido ({Value}); usando {Default}", setting, DefaultSchedule);
            schedule = CronExpression.Parse(DefaultSchedule);
        }

        var timeZone = BrazilTimeZone();

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            await RunBatchAsync("subida do app");

            while (!stoppingToken.IsCancellationRequested)
            {
                var next = schedule.GetNextOccurrence(DateTimeOffset.UtcNow, timeZone);
                if (next == null)
                    return;

                _logger.LogInformation("Proximo batch automatico: {Next:dd/MM/yyyy HH:mm} (Brasilia)",
                    TimeZoneInfo.ConvertTime(next.Value, timeZone));

                // Task.Delay aceita no maximo ~24 dias; espera em etapas
                while (next.Value > DateTimeOffset.UtcNow)
                {
                    var wait = next.Value - DateTimeOffset.UtcNow;
                    await Task.Delay(wait > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : wait, stoppingToken);
                }

                await RunBatchAsync("agendado");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App desligando
        }
    }

    private async Task RunBatchAsync(string reason)
    {
        try
        {
            _logger.LogInformation("Batch automatico ({Reason}) iniciando", reason);
            using var scope = _services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<BatchService>().RunDailyUpdateAsync();
            _logger.LogInformation("Batch automatico ({Reason}): {Status}", reason, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch automatico ({Reason}) falhou", reason);
        }
    }

    /// <summary>Fuso de Brasilia; se o sistema nao tiver a base de fusos, UTC-3 fixo (sem horario de verao desde 2019).</summary>
    private static TimeZoneInfo BrazilTimeZone()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception)
            {
                // tenta o proximo
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasilia", "BRT");
    }
}
