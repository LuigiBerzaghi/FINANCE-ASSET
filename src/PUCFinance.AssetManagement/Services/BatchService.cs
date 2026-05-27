using PUCFinance.AssetManagement.Models.DTOs;

namespace PUCFinance.AssetManagement.Services;

public class BatchService
{
    private readonly PricingService _pricing;
    private readonly NavCalculator _nav;
    private readonly MetricsCalculator _metrics;
    private readonly CdiService _cdi;
    private readonly ILogger<BatchService> _logger;

    public BatchService(
        PricingService pricing,
        NavCalculator nav,
        MetricsCalculator metrics,
        CdiService cdi,
        ILogger<BatchService> logger)
    {
        _pricing = pricing;
        _nav = nav;
        _metrics = metrics;
        _cdi = cdi;
        _logger = logger;
    }

    public async Task<BatchResultResponse> RunDailyUpdateAsync()
    {
        _logger.LogInformation("=== Batch diario iniciado ===");

        try
        {
            // 1. Precos
            _logger.LogInformation("Etapa 1/4: Buscando precos...");
            var priceCount = await _pricing.FetchAndStorePricesAsync();

            // 2. CDI
            _logger.LogInformation("Etapa 2/4: Atualizando CDI...");
            await _cdi.FetchAndStoreCdiAsync();

            // 3. NAV
            _logger.LogInformation("Etapa 3/4: Recalculando NAV...");
            await _nav.CalculateAllAsync();

            // 4. Metricas
            _logger.LogInformation("Etapa 4/4: Calculando metricas...");
            await _metrics.CalculateAllAsync();

            _logger.LogInformation("=== Batch diario concluido com sucesso ===");

            return new BatchResultResponse(
                Status: "success",
                FundsUpdated: -1,
                PricesFetched: priceCount,
                Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch diario falhou");
            return new BatchResultResponse(
                Status: $"error: {ex.Message}",
                FundsUpdated: 0,
                PricesFetched: 0,
                Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
            );
        }
    }
}
