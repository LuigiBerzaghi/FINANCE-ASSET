using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Titulos publicos no dia a dia do fundo: mantem o catalogo de ativos com os titulos ofertados e
/// credita cupons e resgates (vencimento) no caixa. Chamado pelo batch diario.
/// </summary>
public class TreasuryService
{
    /// <summary>Carga inicial e batch podem sincronizar o catalogo ao mesmo tempo.</summary>
    private static readonly SemaphoreSlim CatalogLock = new(1, 1);

    private readonly AppDbContext _db;
    private readonly TesouroDiretoClient _tesouro;
    private readonly TreasuryIndexService _indexes;
    private readonly TradeService _trades;
    private readonly ILogger<TreasuryService> _logger;

    public TreasuryService(
        AppDbContext db,
        TesouroDiretoClient tesouro,
        TreasuryIndexService indexes,
        TradeService trades,
        ILogger<TreasuryService> logger)
    {
        _db = db;
        _tesouro = tesouro;
        _indexes = indexes;
        _trades = trades;
        _logger = logger;
    }

    /// <summary>
    /// Cadastra no catalogo de ativos (classe Renda Fixa) os titulos ofertados hoje e desativa os que sairam de oferta.
    /// </summary>
    public async Task SyncCatalogAsync()
    {
        var latest = await _tesouro.GetLatestAsync();
        if (latest.Count == 0)
            return;

        await CatalogLock.WaitAsync();
        try
        {
            var offered = latest.ToDictionary(q => q.Bond.Ticker);
            var existing = await _db.Assets
                .Where(a => a.Exchange == TesouroDireto.Exchange || offered.Keys.Contains(a.Ticker))
                .ToDictionaryAsync(a => a.Ticker);

            foreach (var (ticker, quote) in offered)
            {
                if (!existing.TryGetValue(ticker, out var asset))
                {
                    asset = new Asset { Ticker = ticker };
                    _db.Assets.Add(asset);
                }

                asset.Name = quote.Bond.DisplayName;
                asset.AssetClass = TesouroDireto.AssetClass;
                asset.Sector = quote.Bond.Type.Name;
                asset.Exchange = TesouroDireto.Exchange;
                asset.Currency = PricingService.BaseCurrency;
                asset.YahooTicker = null;
                asset.IsActive = 1;
            }

            foreach (var asset in existing.Values.Where(a => !offered.ContainsKey(a.Ticker)))
                asset.IsActive = 0;

            await _db.SaveChangesAsync();
        }
        catch
        {
            // Nada pendente pode ser salvo depois por outra etapa que use o mesmo contexto
            _db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            CatalogLock.Release();
        }
    }

    private sealed record PlannedEvent(int FundId, string Ticker, DateTime Date, string Kind, double AmountPerUnit);

    /// <summary>
    /// Registra cupons e resgates com data ate hoje para titulos que os fundos tinham na vespera e
    /// reconstroi o caixa/posicoes dos fundos afetados. Eventos cujo indice (IPCA/IGP-M) ainda nao
    /// saiu ficam para o proximo batch. Retorna quantos eventos foram criados.
    /// </summary>
    public async Task<int> ProcessDueEventsAsync()
    {
        var today = DateTime.Today;
        var allTrades = await _db.Trades.AsNoTracking().ToListAsync();
        var treasuryTrades = allTrades.Where(t => TesouroDireto.IsTreasuryTicker(t.Ticker)).ToList();
        if (treasuryTrades.Count == 0)
            return 0;

        var funds = await _db.Funds.AsNoTracking().ToDictionaryAsync(f => f.Id);
        var existingEvents = await _db.TreasuryEvents.AsNoTracking().ToListAsync();
        var planned = new List<PlannedEvent>();

        foreach (var fundGroup in treasuryTrades.GroupBy(t => t.FundId))
        {
            if (!funds.TryGetValue(fundGroup.Key, out var fund))
                continue;

            var fundTrades = allTrades.Where(t => t.FundId == fund.Id).ToList();
            var fundEvents = existingEvents.Where(e => e.FundId == fund.Id).ToList();

            foreach (var tickerGroup in fundGroup.GroupBy(t => t.Ticker))
            {
                var bond = await _tesouro.FindBondAsync(tickerGroup.Key);
                if (bond == null)
                {
                    _logger.LogWarning("Titulo {Ticker} nao encontrado no Tesouro Direto; cupons/resgate nao processados", tickerGroup.Key);
                    continue;
                }

                var firstTrade = NavHistoryRebuilder.ParseDate(tickerGroup.Min(t => t.ExecutedAt))!.Value;
                var due = TesouroDireto.CouponDates(bond, firstTrade, today)
                    .Select(d => (Date: d, Kind: TreasuryEventKinds.Coupon))
                    .ToList();

                if (bond.Maturity.Date <= today && bond.Maturity.Date > firstTrade)
                {
                    if (bond.Type.Redemption == TreasuryRedemptionRule.Unsupported)
                        _logger.LogWarning("{Bond}: vencimento com fluxo mensal nao suportado; posicao mantida", bond.DisplayName);
                    else
                        due.Add((bond.Maturity.Date, TreasuryEventKinds.Maturity));
                }

                foreach (var (date, kind) in due)
                {
                    var iso = TesouroDireto.ToIsoDate(date);
                    if (fundEvents.Any(e => e.Ticker == bond.Ticker && e.EventDate == iso && e.Kind == kind))
                        continue;

                    // Quem tinha o titulo no fim da vespera recebe o evento
                    var state = FundLedger.Replay(fund.InitialCapital, fundTrades, fundEvents,
                        TesouroDireto.ToIsoDate(date.AddDays(-1)));
                    if (!state.Positions.TryGetValue(bond.Ticker, out var position) || position.Quantity <= 0)
                        continue;

                    var amount = kind == TreasuryEventKinds.Coupon
                        ? await CouponPerUnitAsync(bond, date)
                        : await RedemptionPerUnitAsync(bond);
                    if (amount is not > 0)
                    {
                        _logger.LogInformation("{Bond}: {Kind} de {Date} aguardando indice/preco; tenta no proximo batch",
                            bond.DisplayName, kind, iso);
                        continue;
                    }

                    planned.Add(new PlannedEvent(fund.Id, bond.Ticker, date, kind, amount.Value));
                }
            }
        }

        if (planned.Count == 0)
            return 0;

        await TradeService.PortfolioLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            foreach (var ev in planned)
            {
                _db.TreasuryEvents.Add(new TreasuryEvent
                {
                    FundId = ev.FundId,
                    Ticker = ev.Ticker,
                    EventDate = TesouroDireto.ToIsoDate(ev.Date),
                    Kind = ev.Kind,
                    AmountPerUnit = ev.AmountPerUnit
                });

                _logger.LogInformation("Tesouro: {Kind} {Ticker} em {Date} = R$ {Amount:N6} por titulo (fundo {FundId})",
                    ev.Kind, ev.Ticker, TesouroDireto.ToIsoDate(ev.Date), ev.AmountPerUnit, ev.FundId);
            }

            await _db.SaveChangesAsync();

            foreach (var fundId in planned.Select(e => e.FundId).Distinct())
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

    /// <summary>Cupom por titulo: taxa semestral sobre o valor nominal (R$ 1.000 ou VNA).</summary>
    public async Task<double?> CouponPerUnitAsync(TreasuryBond bond, DateTime date)
    {
        var nominal = bond.Type.Coupon switch
        {
            TreasuryCouponRule.Prefixed => 1000,
            TreasuryCouponRule.Ipca => await _indexes.IpcaVnaAsync(date),
            TreasuryCouponRule.Igpm => await _indexes.IgpmVnaAsync(date),
            _ => null,
        };

        return nominal * TesouroDireto.SemiannualCouponRate(bond);
    }

    /// <summary>Valor de resgate por titulo no vencimento (sem o ultimo cupom, que e um evento separado).</summary>
    public async Task<double?> RedemptionPerUnitAsync(TreasuryBond bond)
    {
        switch (bond.Type.Redemption)
        {
            case TreasuryRedemptionRule.Par:
                return 1000;
            case TreasuryRedemptionRule.IpcaVna:
                return await _indexes.IpcaVnaAsync(bond.Maturity);
            case TreasuryRedemptionRule.IgpmVna:
                return await _indexes.IgpmVnaAsync(bond.Maturity);
            case TreasuryRedemptionRule.LastPrice:
                var maturity = TesouroDireto.ToIsoDate(bond.Maturity);
                var last = (await _tesouro.GetHistoryAsync(bond.Ticker))
                    .LastOrDefault(q => string.CompareOrdinal(q.BaseDate, maturity) <= 0 && q.BasePrice > 0);
                return last?.BasePrice;
            default:
                return null;
        }
    }
}
