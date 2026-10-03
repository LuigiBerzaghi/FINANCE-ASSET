using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Services;

/// <summary>P&L realizado por um trade ou por resgate no vencimento.</summary>
public sealed record LedgerRealized(string Ticker, string ClosedAt, RealizedFill Fill);

/// <summary>Resultado do replay: posicoes, caixa e quantidades de cada evento (cupom, resgate ou provento).</summary>
public sealed class LedgerState
{
    public SortedDictionary<string, PositionState> Positions { get; } = new(StringComparer.Ordinal);
    public double Cash { get; set; }
    public List<LedgerRealized> Realized { get; } = new();

    /// <summary>
    /// Quantidade em carteira na vespera de cada evento (id → quantidade). Em proventos tem sinal
    /// (vendido negativo); em cupons/resgates e zero quando o fundo nao tinha o titulo.
    /// </summary>
    public Dictionary<int, double> EventQuantities { get; } = new();
}

/// <summary>
/// Reaplica trades, eventos de titulos publicos (cupons e resgates) e proventos de acoes em ordem cronologica.
/// Eventos de uma data valem para quem tinha o ativo na vespera, entao entram antes dos trades do mesmo dia;
/// no vencimento o ultimo cupom e pago antes do resgate. Provento: o comprado recebe e o vendido paga
/// (quantidade com sinal × valor por acao).
/// </summary>
public static class FundLedger
{
    /// <param name="untilDate">Se informado (yyyy-MM-dd), considera apenas itens com data &lt;= untilDate.</param>
    public static LedgerState Replay(
        double initialCapital,
        IEnumerable<Trade> trades,
        IEnumerable<TreasuryEvent> events,
        string? untilDate = null)
    {
        var timeline = events
            .Select(e => (Date: e.EventDate, Order: e.Kind == TreasuryEventKinds.Maturity ? 1 : 0, Stamp: e.EventDate, Id: e.Id, Trade: (Trade?)null, Event: (TreasuryEvent?)e))
            .Concat(trades.Select(t => (Date: DateOf(t.ExecutedAt), Order: 2, Stamp: t.ExecutedAt, Id: t.Id, Trade: (Trade?)t, Event: (TreasuryEvent?)null)))
            .Where(x => untilDate == null || string.CompareOrdinal(x.Date, untilDate) <= 0)
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .ThenBy(x => x.Order)
            .ThenBy(x => x.Stamp, StringComparer.Ordinal)
            .ThenBy(x => x.Id);

        var state = new LedgerState { Cash = initialCapital };

        foreach (var item in timeline)
        {
            if (item.Trade is { } trade)
            {
                var signed = trade.Side == "long" ? trade.Quantity : -trade.Quantity;
                Apply(state, trade.Ticker, signed, trade.Price, trade.Side, trade.ExecutedAt);
                state.Cash -= signed * trade.Price;
                continue;
            }

            var ev = item.Event!;
            var quantity = state.Positions.TryGetValue(ev.Ticker, out var position) ? position.Quantity : 0;

            // Provento: o vendido paga ao doador da acao (quantidade negativa debita o caixa)
            if (ev.Kind == TreasuryEventKinds.Dividend)
            {
                state.EventQuantities[ev.Id] = quantity;
                state.Cash += quantity * ev.AmountPerUnit;
                continue;
            }

            var held = quantity > 0 ? quantity : 0;
            state.EventQuantities[ev.Id] = held;
            if (held <= 0)
                continue;

            state.Cash += held * ev.AmountPerUnit;

            // Vencimento: o titulo e resgatado pelo valor do evento
            if (ev.Kind == TreasuryEventKinds.Maturity)
                Apply(state, ev.Ticker, -held, ev.AmountPerUnit, "short", ev.EventDate);
        }

        return state;
    }

    private static void Apply(LedgerState state, string ticker, double signed, double price, string side, string closedAt)
    {
        var (next, realized) = PositionMath.Apply(state.Positions.GetValueOrDefault(ticker), signed, price, side);
        if (realized != null)
            state.Realized.Add(new LedgerRealized(ticker, closedAt, realized));

        if (next == null)
            state.Positions.Remove(ticker);
        else
            state.Positions[ticker] = next;
    }

    public static string DateOf(string timestamp) =>
        timestamp.Length >= 10 ? timestamp[..10] : timestamp;
}
