import { LineChart, Line, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid, Legend } from 'recharts';
import { fmtPct } from '../lib/format';

// Fundo vs a referencia dele: IBOVESPA (fundos de acoes) ou CDI (multimercado e renda fixa)
export default function BenchmarkChart({ data }) {
  if (!data || !data.series?.length) {
    return (
      <div style={{ color: 'var(--text-muted)', padding: 20, fontSize: 12 }}>
        Comparacao sera carregada apos o batch
      </div>
    );
  }

  const benchmark = data.benchmarkName || 'Referencia';
  const labelFor = (key) => (key === 'fundo' ? 'Fundo' : benchmark);
  const chartData = data.series.map((p) => ({
    date: p.date,
    fundo: p.fundCumulative * 100,
    referencia: p.benchmarkCumulative * 100,
  }));

  const statLabel = { fontSize: 10, color: 'var(--text-dim)', textTransform: 'uppercase', letterSpacing: '0.08em', marginBottom: 2 };

  return (
    <div>
      {/* Stats */}
      <div style={{ display: 'flex', gap: 16, marginBottom: 12 }}>
        <div>
          <div style={statLabel}>Fundo</div>
          <div style={{ fontSize: 16, fontWeight: 600, color: data.fundReturn >= 0 ? 'var(--green)' : 'var(--red)' }}>
            {fmtPct(data.fundReturn)}
          </div>
        </div>
        <div>
          <div style={statLabel}>{benchmark}</div>
          <div style={{ fontSize: 16, fontWeight: 600, color: 'var(--yellow)' }}>
            {fmtPct(data.benchmarkReturn)}
          </div>
        </div>
        <div>
          <div style={statLabel}>Excesso</div>
          <div style={{ fontSize: 16, fontWeight: 600, color: data.excessReturn >= 0 ? 'var(--green)' : 'var(--red)' }}>
            {fmtPct(data.excessReturn)}
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
            formatter={(v, name) => [`${v.toFixed(2)}%`, labelFor(name)]}
          />
          <Legend
            wrapperStyle={{ fontSize: 11, color: 'var(--text-muted)' }}
            formatter={labelFor}
          />
          <Line type="monotone" dataKey="fundo" stroke="var(--chart-stroke)" strokeWidth={2} dot={false} />
          <Line type="monotone" dataKey="referencia" stroke="var(--yellow)" strokeWidth={1.5} dot={false} strokeDasharray="4 4" />
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
