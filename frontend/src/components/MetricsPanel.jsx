import { fmtPct, fmtNum } from '../lib/format';

function MetricRow({ label, inception, mtd, ytd, fmt }) {
  return (
    <tr style={{ borderBottom: '1px solid var(--border)' }}>
      <td style={{ padding: '6px 10px', color: 'var(--text-muted)', fontSize: 11, textTransform: 'uppercase' }}>
        {label}
      </td>
      <td style={{ padding: '6px 10px', color: 'var(--text)', textAlign: 'right', fontSize: 12 }}>
        {fmt(inception)}
      </td>
      <td style={{ padding: '6px 10px', color: 'var(--text)', textAlign: 'right', fontSize: 12 }}>
        {fmt(mtd)}
      </td>
      <td style={{ padding: '6px 10px', color: 'var(--text)', textAlign: 'right', fontSize: 12 }}>
        {fmt(ytd)}
      </td>
    </tr>
  );
}

export default function MetricsPanel({ metrics }) {
  if (!metrics?.length) {
    return (
      <div style={{ color: 'var(--text-muted)', padding: 20, fontSize: 12 }}>
        Métricas serão calculadas após múltiplos dias de NAV
      </div>
    );
  }

  const inception = metrics.find((m) => m.period === 'inception');
  const mtd = metrics.find((m) => m.period === 'mtd');
  const ytd = metrics.find((m) => m.period === 'ytd');
  const minObservations = inception?.minObservations ?? 0;
  const benchmark = inception?.benchmarkName || 'IBOVESPA';
  const isCdi = benchmark === 'CDI';
  const short = [['Inicio', inception], ['MTD', mtd], ['YTD', ytd]]
    .filter(([, m]) => m && m.observations < minObservations);
  const noteStyle = { color: 'var(--text-dim)', fontSize: 11, marginTop: 8, lineHeight: 1.5 };

  return (
    <div>
    <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
      <thead>
        <tr>
          {['Métrica', 'Início', 'MTD', 'YTD'].map((h, i) => (
            <th
              key={h}
              style={{
                padding: '6px 10px',
                textAlign: i === 0 ? 'left' : 'right',
                color: 'var(--text-dim)',
                fontSize: 10,
                textTransform: 'uppercase',
              }}
            >
              {h}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        <MetricRow label="Retorno" inception={inception?.cumulativeReturn} mtd={mtd?.cumulativeReturn} ytd={ytd?.cumulativeReturn} fmt={fmtPct} />
        <MetricRow label="Volatilidade" inception={inception?.volatility} mtd={mtd?.volatility} ytd={ytd?.volatility} fmt={fmtPct} />
        <MetricRow label="Sharpe" inception={inception?.sharpeRatio} mtd={mtd?.sharpeRatio} ytd={ytd?.sharpeRatio} fmt={(v) => fmtNum(v, 2)} />
        <MetricRow label="Max DD" inception={inception?.maxDrawdown} mtd={mtd?.maxDrawdown} ytd={ytd?.maxDrawdown} fmt={fmtPct} />
        <MetricRow label={isCdi ? 'Alpha (vs CDI)' : 'Alpha (vs IBOV)'} inception={inception?.alpha} mtd={mtd?.alpha} ytd={ytd?.alpha} fmt={fmtPct} />
        {!isCdi && (
          <MetricRow label="Beta (vs IBOV)" inception={inception?.beta} mtd={mtd?.beta} ytd={ytd?.beta} fmt={(v) => fmtNum(v, 2)} />
        )}
      </tbody>
    </table>
    {short.length > 0 && (
      <div style={noteStyle}>
        Volatilidade, Sharpe{isCdi ? ' e Alpha' : ', Alpha e Beta'} aparecem com pelo menos {minObservations} dias de historico no periodo
        ({short.map(([label, m]) => `${label}: ${m.observations}`).join(', ')}).
      </div>
    )}
    <div style={noteStyle}>
      Sharpe usa o CDI do periodo como taxa livre de risco
      {inception?.riskFreeRate != null && ` (desde o inicio: ${fmtPct(inception.riskFreeRate)} a.a.)`}.
      {isCdi
        ? ' Referencia do fundo: CDI. Alpha e o retorno anualizado acima do CDI; beta nao se aplica.'
        : ' Referencia do fundo: IBOVESPA. Alpha e Beta por regressao contra o indice.'}
    </div>
    </div>
  );
}
