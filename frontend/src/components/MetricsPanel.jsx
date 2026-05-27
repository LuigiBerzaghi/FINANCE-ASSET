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

  return (
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
        <MetricRow label="Alpha" inception={inception?.alpha} mtd={mtd?.alpha} ytd={ytd?.alpha} fmt={fmtPct} />
        <MetricRow label="Beta" inception={inception?.beta} mtd={mtd?.beta} ytd={ytd?.beta} fmt={(v) => fmtNum(v, 2)} />
      </tbody>
    </table>
  );
}
