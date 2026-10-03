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
    private readonly DividendService _dividends;
    private readonly NavBackfillService _backfill;
    private readonly ILogger<BatchService> _logger;

    public BatchService(
        PricingService pricing,
        NavCalculator nav,
        MetricsCalculator metrics,
        CdiService cdi,
        IbovespaService ibovespa,
        TreasuryService treasury,
        DividendService dividends,
        NavBackfillService backfill,
        ILogger<BatchService> logger)
    {
        _pricing = pricing;
        _nav = nav;
        _metrics = metrics;
        _cdi = cdi;
        _ibovespa = ibovespa;
        _treasury = treasury;
        _dividends = dividends;
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
            _logger.LogInformation("Etapa 1/7: Tesouro Direto (catalogo, cupons e vencimentos)...");
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

            // 2. Proventos de acoes (dividendos/JCP) na data ex, antes do NAV: o caixa recebe o que o preco perdeu
            _logger.LogInformation("Etapa 2/7: Proventos de acoes (dividendos e JCP)...");
            try
            {
                var dividends = await _dividends.ProcessDueDividendsAsync();
                if (dividends > 0)
                    _logger.LogInformation("Proventos: {Count} lancamento(s) de dividendo/JCP", dividends);
            }
            catch (Exception ex)
            {
                // Proventos pendentes entram no proximo batch (o replay credita na data ex correta)
                _logger.LogError(ex, "Etapa de proventos falhou");
            }

            // 3. Precos
            _logger.LogInformation("Etapa 3/7: Buscando precos...");
            var priceCount = await _pricing.FetchAndStorePricesAsync();

            // 4. CDI e IBOVESPA (benchmarks das metricas e calendario de dias uteis)
            _logger.LogInformation("Etapa 4/7: Atualizando CDI e IBOVESPA...");
            await _cdi.FetchAndStoreCdiAsync();
            await _ibovespa.FetchAndStoreAsync();

            // 5. Dias uteis passados sem NAV (batch que nao rodou): preenchidos com fechamentos historicos
            _logger.LogInformation("Etapa 5/7: Preenchendo dias sem NAV...");
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
                // 6. NAV de hoje
                _logger.LogInformation("Etapa 6/7: Recalculando NAV...");
                await _nav.CalculateAllAsync();

                // 7. Metricas
                _logger.LogInformation("Etapa 7/7: Calculando metricas...");
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
