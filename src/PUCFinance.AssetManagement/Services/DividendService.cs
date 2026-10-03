using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Proventos em dinheiro de acoes (dividendos e JCP): na data ex, quem tinha a acao na vespera recebe
/// (comprado) ou paga (vendido) o valor bruto por acao. Os valores vem do Yahoo (events.dividends),
/// convertidos para BRL pelo cambio da data ex, e viram linhas em treasury_events (kind = dividend),
/// creditadas no caixa pelo replay do fundo. Chamado pelo batch diario.
/// </summary>
public class DividendService
{
    /// <summary>Proventos com data ex anterior a esta data nao sao lancados (o caixa do passado fica como estava).</summary>
    public const string DefaultStartDate = "2026-10-05";

    private readonly AppDbContext _db;
    private readonly PricingService _pricing;
    private readonly TradeService _trades;
    private readonly IConfiguration _config;
    private readonly ILogger<DividendService> _logger;

    public DividendService(
        AppDbContext db,
        PricingService pricing,
        TradeService trades,
        IConfiguration config,
        ILogger<DividendService> logger)
    {
        _db = db;
        _pricing = pricing;
        _trades = trades;
        _config = config;
        _logger = logger;
    }

    private sealed record PlannedDividend(int FundId, string Ticker, string ExDate, double Quantity, DividendQuote Quote);

    /// <summary>
    /// Data a partir da qual os proventos sao lancados. DIVIDENDS_START_DATE (yyyy-MM-dd) muda a data;
    /// valor invalido usa o padrao.
    /// </summary>
    public DateTime StartDate()
    {
        var setting = _config["DIVIDENDS_START_DATE"]?.Trim();
        if (!string.IsNullOrEmpty(setting)
            && DateTime.TryParseExact(setting, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var configured))
            return configured;

        if (!string.IsNullOrEmpty(setting))
            _logger.LogWarning("DIVIDENDS_START_DATE invalido ({Value}); usando {Default}", setting, DefaultStartDate);

        return DateTime.ParseExact(DefaultStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Registra os proventos com data ex entre a data inicial e hoje para os fundos que tinham a acao
    /// (comprada ou vendida) na vespera e reconstroi o caixa dos fundos afetados. Proventos ja registrados
    /// nunca sao alterados. Retorna quantos lancamentos foram criados.
    /// </summary>
    public async Task<int> ProcessDueDividendsAsync()
    {
        var start = StartDate();
        var today = DateTime.Today;
        if (today < start)
            return 0;

        var startIso = ToIsoDate(start);
        var todayIso = ToIsoDate(today);

        var allTrades = await _db.Trades.AsNoTracking().ToListAsync();
        if (allTrades.Count == 0)
            return 0;

        var funds = await _db.Funds.AsNoTracking().ToDictionaryAsync(f => f.Id);
        var existingEvents = await _db.TreasuryEvents.AsNoTracking().ToListAsync();

        // Acoes que cada fundo pode ter tido na vespera de alguma data ex desde o inicio:
        // as que tinha na vespera da data inicial e as negociadas depois dela
        var candidates = new Dictionary<int, HashSet<string>>();
        foreach (var fundTrades in allTrades.GroupBy(t => t.FundId))
        {
            if (!funds.TryGetValue(fundTrades.Key, out var fund))
                continue;

            var fundEvents = existingEvents.Where(e => e.FundId == fund.Id).ToList();
            var before = FundLedger.Replay(fund.InitialCapital, fundTrades, fundEvents, ToIsoDate(start.AddDays(-1)));
            var tickers = before.Positions.Keys
                .Concat(fundTrades
                    .Where(t => string.CompareOrdinal(FundLedger.DateOf(t.ExecutedAt), startIso) >= 0)
                    .Select(t => t.Ticker))
                .Where(t => !TesouroDireto.IsTreasuryTicker(t))
                .ToHashSet(StringComparer.Ordinal);

            if (tickers.Count > 0)
                candidates[fund.Id] = tickers;
        }

        var planned = new List<PlannedDividend>();
        foreach (var ticker in candidates.Values.SelectMany(t => t).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
        {
            var dividends = await _pricing.GetDividendsBrlAsync(ticker, start);
            if (dividends == null)
            {
                _logger.LogWarning("Proventos de {Ticker} indisponiveis no Yahoo; tenta no proximo batch", ticker);
                continue;
            }

            foreach (var dividend in dividends.Where(d => string.CompareOrdinal(d.ExDate, todayIso) <= 0))
            {
                foreach (var (fundId, tickers) in candidates)
                {
                    if (!tickers.Contains(ticker))
                        continue;

                    if (existingEvents.Any(e => e.FundId == fundId && e.Ticker == ticker
                            && e.EventDate == dividend.ExDate && e.Kind == TreasuryEventKinds.Dividend))
                        continue;

                    // Quem tinha a acao no fim da vespera da data ex recebe (comprado) ou paga (vendido)
                    var fund = funds[fundId];
                    var eve = ToIsoDate(DateTime.ParseExact(dividend.ExDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-1));
                    var state = FundLedger.Replay(fund.InitialCapital,
                        allTrades.Where(t => t.FundId == fundId),
                        existingEvents.Where(e => e.FundId == fundId),
                        eve);
                    if (!state.Positions.TryGetValue(ticker, out var position) || position.Quantity == 0)
                        continue;

                    planned.Add(new PlannedDividend(fundId, ticker, dividend.ExDate, position.Quantity, dividend));
                }
            }
        }

        if (planned.Count == 0)
            return 0;

        await TradeService.PortfolioLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            foreach (var item in planned)
            {
                _db.TreasuryEvents.Add(new TreasuryEvent
                {
                    FundId = item.FundId,
                    Ticker = item.Ticker,
                    EventDate = item.ExDate,
                    Kind = TreasuryEventKinds.Dividend,
                    AmountPerUnit = item.Quote.AmountBrl
                });

                _logger.LogInformation(
                    "Provento: {Ticker} data ex {Date} = R$ {Amount:N6} por acao ({Native} {Currency} x {Fx}) | {Quantity} acoes | fundo {FundId}",
                    item.Ticker, item.ExDate, item.Quote.AmountBrl, item.Quote.NativeAmount, item.Quote.Currency,
                    item.Quote.FxRate, item.Quantity, item.FundId);
            }

            await _db.SaveChangesAsync();

            foreach (var fundId in planned.Select(p => p.FundId).Distinct())
                await _trades.RebuildFundAsync(fundId);

            await transaction.CommitAsync();
        }
        catch
        {
            // A transacao foi desfeita: nada pendente pode ser salvo por outra etapa do batch
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            TradeService.PortfolioLock.Release();
        }

        return planned.Count;
    }

    private static string ToIsoDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
