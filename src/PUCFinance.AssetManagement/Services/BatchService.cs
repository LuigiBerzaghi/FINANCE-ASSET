using PUCFinance.AssetManagement.Models.DTOs;

namespace PUCFinance.AssetManagement.Services;

public class BatchService
{
    /// <summary>Um batch por vez (agendador, pos-trade, pos-migracao e botao podem disparar juntos).</summary>
    private static readonly SemaphoreSlim BatchGate = new(1, 1);

    private readonly PricingService _pricing;
    private readonly NavCalculator _nav;
    private readonly MetricsCalculator _metrics;
    private readonly CdiService _cdi;
    private readonly IbovespaService _ibovespa;
    private readonly TreasuryService _treasury;
    private readonly NavBackfillService _backfill;
    private readonly ILogger<BatchService> _logger;

    public BatchService(
        PricingService pricing,
        NavCalculator nav,
        MetricsCalculator metrics,
        CdiService cdi,
        IbovespaService ibovespa,
        TreasuryService treasury,
        NavBackfillService backfill,
        ILogger<BatchService> logger)
    {
        _pricing = pricing;
        _nav = nav;
        _metrics = metrics;
        _cdi = cdi;
        _ibovespa = ibovespa;
        _treasury = treasury;
        _backfill = backfill;
        _logger = logger;
    }

    public async Task<BatchResultResponse> RunDailyUpdateAsync()
    {
        await BatchGate.WaitAsync();
        try
        {
            return await RunDailyUpdateLockedAsync();
        }
        finally
        {
            BatchGate.Release();
        }
    }

    private async Task<BatchResultResponse> RunDailyUpdateLockedAsync()
    {
        _logger.LogInformation("=== Batch diario iniciado ===");

        try
        {
            // 1. Tesouro Direto: catalogo de titulos, cupons e resgates (antes dos precos e do NAV)
            _logger.LogInformation("Etapa 1/6: Tesouro Direto (catalogo, cupons e vencimentos)...");
            try
            {
                await _treasury.SyncCatalogAsync();
                var events = await _treasury.ProcessDueEventsAsync();
                if (events > 0)
                    _logger.LogInformation("Tesouro Direto: {Count} cupom(ns)/resgate(s) creditado(s)", events);
            }
            catch (Exception ex)
            {
                // Sem o Tesouro o resto do batch ainda vale; eventos pendentes entram no proximo
                _logger.LogError(ex, "Etapa do Tesouro Direto falhou");
            }

            // 2. Precos
            _logger.LogInformation("Etapa 2/6: Buscando precos...");
            var priceCount = await _pricing.FetchAndStorePricesAsync();

            // 3. CDI e IBOVESPA (benchmarks das metricas e calendario de dias uteis)
            _logger.LogInformation("Etapa 3/6: Atualizando CDI e IBOVESPA...");
            await _cdi.FetchAndStoreCdiAsync();
            await _ibovespa.FetchAndStoreAsync();

            // 4. Dias uteis passados sem NAV (batch que nao rodou): preenchidos com fechamentos historicos
            _logger.LogInformation("Etapa 4/6: Preenchendo dias sem NAV...");
            try
            {
                var filled = await _backfill.FillMissingDaysAsync();
                if (filled > 0)
                    _logger.LogInformation("NAV preenchido para {Count} dia(s) sem registro", filled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Preenchimento de dias sem NAV falhou; tenta de novo no proximo batch");
            }

            // NAV e metricas leem/gravam posicoes: sem trades ou reconstrucoes no meio
            await TradeService.PortfolioLock.WaitAsync();
            try
            {
                // 5. NAV de hoje
                _logger.LogInformation("Etapa 5/6: Recalculando NAV...");
                await _nav.CalculateAllAsync();

                // 6. Metricas
                _logger.LogInformation("Etapa 6/6: Calculando metricas...");
                await _metrics.CalculateAllAsync();
            }
            finally
            {
                TradeService.PortfolioLock.Release();
            }

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
