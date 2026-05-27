import { LineChart, Line, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid, Legend } from 'recharts';
import { fmtPct } from '../lib/format';

export default function CdiChart({ cdiData }) {
  if (!cdiData || !cdiData.series?.length) {
    return (
      <div style={{ color: 'var(--text-muted)', padding: 20, fontSize: 12 }}>
        CDI sera carregado apos rodar o batch
      </div>
    );
  }

  const chartData = cdiData.series.map((p) => ({
    date: p.date,
    fundo: p.fundCumulative * 100,
    cdi: p.cdiCumulative * 100,
  }));

  return (
    <div>
      {/* Stats */}
      <div style={{ display: 'flex', gap: 16, marginBottom: 12 }}>
        <div>
          <div style={{ fontSize: 10, color: 'var(--text-dim)', textTransform: 'uppercase', letterSpacing: '0.08em', marginBottom: 2 }}>Fundo</div>
          <div style={{ fontSize: 16, fontWeight: 600, color: cdiData.fundReturn >= 0 ? 'var(--green)' : 'var(--red)' }}>
            {fmtPct(cdiData.fundReturn)}
          </div>
        </div>
        <div>
          <div style={{ fontSize: 10, color: 'var(--text-dim)', textTransform: 'uppercase', letterSpacing: '0.08em', marginBottom: 2 }}>CDI</div>
          <div style={{ fontSize: 16, fontWeight: 600, color: 'var(--yellow)' }}>
            {fmtPct(cdiData.cdiReturn)}
          </div>
        </div>
        <div>
          <div style={{ fontSize: 10, color: 'var(--text-dim)', textTransform: 'uppercase', letterSpacing: '0.08em', marginBottom: 2 }}>Excesso</div>
          <div style={{ fontSize: 16, fontWeight: 600, color: cdiData.excessReturn >= 0 ? 'var(--green)' : 'var(--red)' }}>
            {fmtPct(cdiData.excessReturn)}
          </div>
        </div>
      </div>

      {/* Chart */}
      <ResponsiveContainer width="100%" height={220}>
        <LineChart data={chartData} margin={{ top: 5, right: 10, left: 0, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
          <XAxis
            dataKey="date"
            tick={{ fill: 'var(--text-dim)', fontSize: 10 }}
            tickLine={false}
            axisLine={{ stroke: 'var(--border)' }}
          />
          <YAxis
            tick={{ fill: 'var(--text-dim)', fontSize: 10 }}
            tickLine={false}
            axisLine={{ stroke: 'var(--border)' }}
            tickFormatter={(v) => `${v.toFixed(1)}%`}
          />
          <Tooltip
            contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 6, fontSize: 12 }}
            formatter={(v, name) => [`${v.toFixed(2)}%`, name === 'fundo' ? 'Fundo' : 'CDI']}
          />
          <Legend
            wrapperStyle={{ fontSize: 11, color: 'var(--text-muted)' }}
            formatter={(value) => value === 'fundo' ? 'Fundo' : 'CDI'}
          />
          <Line type="monotone" dataKey="fundo" stroke="var(--chart-stroke)" strokeWidth={2} dot={false} />
          <Line type="monotone" dataKey="cdi" stroke="var(--yellow)" strokeWidth={1.5} dot={false} strokeDasharray="4 4" />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
