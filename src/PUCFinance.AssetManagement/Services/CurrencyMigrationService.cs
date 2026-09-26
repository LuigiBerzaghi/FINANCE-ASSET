using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Corrige os trades gravados antes da conversao de moeda (fx_rate nulo): ativos cotados em
/// outra moeda tinham o preco original tratado como BRL. Para cada fundo afetado, converte os
/// trades pelo cambio do dia de cada trade e recalcula posicoes, caixa, historico de NAV e precos.
/// Cada fundo e processado numa transacao; se algo falhar, ele fica para o proximo boot.
/// </summary>
public class CurrencyMigrationService
{
    private readonly AppDbContext _db;
    private readonly PricingService _pricing;
    private readonly TradeService _trades;
    private readonly NavHistoryRebuilder _history;
    private readonly ILogger<CurrencyMigrationService> _logger;

    public CurrencyMigrationService(
        AppDbContext db,
        PricingService pricing,
        TradeService trades,
        NavHistoryRebuilder history,
        ILogger<CurrencyMigrationService> logger)
    {
        _db = db;
        _pricing = pricing;
        _trades = trades;
        _history = history;
        _logger = logger;
    }

    /// <summary>Como corrigir um trade legado. PriceBrl null = trade em BRL, preco mantido.</summary>
    private sealed record Conversion(string Currency, double FxRate, double? PriceBrl, string Reason);

    /// <summary>Retorna true se algum fundo teve trades estrangeiros convertidos.</summary>
    public async Task<bool> RunAsync()
    {
        var fundIds = await _db.Trades
            .Where(t => t.FxRate == null)
            .Select(t => t.FundId)
            .Distinct()
            .ToListAsync();

        var changed = false;
        foreach (var fundId in fundIds)
        {
            try
            {
                changed |= await ConvertFundAsync(fundId);
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "Conversao de moeda do fundo {FundId} falhou; sera tentada no proximo boot", fundId);
            }
        }

        return changed;
    }

    private async Task<bool> ConvertFundAsync(int fundId)
    {
        // 1. Cambios e historicos (rede) antes de pegar a trava
        var legacyTrades = await _db.Trades
            .AsNoTracking()
            .Where(t => t.FundId == fundId && t.FxRate == null)
            .ToListAsync();

        var plan = new Dictionary<int, Conversion>();
        foreach (var trade in legacyTrades)
        {
            var resolved = await _pricing.ResolveAsync(trade.Ticker);
            if (resolved == null)
            {
                _logger.LogWarning("Trade {TradeId} ({Ticker}): moeda desconhecida, fundo {FundId} fica para o proximo boot",
                    trade.Id, trade.Ticker, fundId);
                return false;
            }

            // O codigo antigo nao usava o yahoo_ticker do catalogo: BTC puxava um ETF, CL a Colgate etc.
            var wrongInstrument = !string.Equals(
                PricingService.ToYahooTicker(trade.Ticker), resolved.YahooTicker, StringComparison.OrdinalIgnoreCase);

            if (!wrongInstrument && resolved.Currency == PricingService.BaseCurrency && resolved.PriceDivisor == 1)
            {
                plan[trade.Id] = new Conversion(PricingService.BaseCurrency, 1, null, "BRL");
                continue;
            }

            var executedAt = NavHistoryRebuilder.ParseDate(trade.ExecutedAt);
            var fxRate = executedAt == null ? null : await _pricing.GetFxToBrlAsync(resolved.Currency, executedAt);
            if (fxRate == null || fxRate <= 0)
            {
                _logger.LogWarning("Trade {TradeId} ({Ticker}): cambio {Currency}/BRL de {Date} indisponivel, fundo {FundId} fica para o proximo boot",
                    trade.Id, trade.Ticker, resolved.Currency, trade.ExecutedAt, fundId);
                return false;
            }

            double? nativePrice = trade.Price / resolved.PriceDivisor;
            var reason = $"{nativePrice:F4} {resolved.Currency} x {fxRate:F4}";
            if (wrongInstrument)
            {
                // Preco gravado era de outro ativo: usa o fechamento do ativo correto no dia do trade
                nativePrice = await _pricing.GetNativeCloseOnAsync(resolved, executedAt!.Value);
                if (nativePrice == null || nativePrice <= 0)
                {
                    _logger.LogWarning("Trade {TradeId} ({Ticker}): preco de {Yahoo} em {Date} indisponivel, fundo {FundId} fica para o proximo boot",
                        trade.Id, trade.Ticker, resolved.YahooTicker, trade.ExecutedAt, fundId);
                    return false;
                }

                reason = $"reprecificado por {resolved.YahooTicker}: {nativePrice:F4} {resolved.Currency} x {fxRate:F4} (antes {trade.Price:F4})";
            }

            plan[trade.Id] = new Conversion(resolved.Currency, fxRate.Value, nativePrice.Value * fxRate.Value, reason);
        }

        var foreignTickers = legacyTrades
            .Where(t => plan[t.Id].PriceBrl != null)
            .Select(t => t.Ticker)
            .Distinct()
            .ToList();

        var series = foreignTickers.Count > 0
            ? await _history.LoadSeriesAsync(fundId)
            : new Dictionary<string, DailySeries?>();

        // 2. Grava tudo numa transacao, sem trades concorrentes no meio
        await TradeService.PortfolioLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            var trades = await _db.Trades
                .Where(t => t.FundId == fundId && t.FxRate == null)
                .ToListAsync();

            foreach (var trade in trades)
            {
                if (!plan.TryGetValue(trade.Id, out var conversion))
                    continue;

                if (conversion.PriceBrl != null)
                {
                    _logger.LogInformation("Trade {TradeId} {Ticker}: {Reason} = {PriceBrl} BRL",
                        trade.Id, trade.Ticker, conversion.Reason, conversion.PriceBrl.Value);
                    trade.Price = conversion.PriceBrl.Value;
                }

                trade.Currency = conversion.Currency;
                trade.FxRate = conversion.FxRate;
            }

            await _db.SaveChangesAsync();

            if (foreignTickers.Count > 0)
            {
                await _trades.RebuildFundAsync(fundId);

                var cash = await _db.Cash.FindAsync(fundId);
                if (cash?.Balance < 0)
                {
                    _logger.LogWarning(
                        "Fundo {FundId}: caixa ficou negativo ({Balance:N2} BRL) com os precos corrigidos; revise os trades do fundo",
                        fundId, cash.Balance);
                }

                await _history.RebuildAsync(fundId, series);
                await _history.ReplacePricesAsync(foreignTickers, series);
            }

            await transaction.CommitAsync();
        }
        finally
        {
            TradeService.PortfolioLock.Release();
        }

        if (foreignTickers.Count > 0)
        {
            _logger.LogInformation("Fundo {FundId}: trades em moeda estrangeira convertidos para BRL ({Tickers})",
                fundId, string.Join(", ", foreignTickers));
        }

        return foreignTickers.Count > 0;
    }

}
