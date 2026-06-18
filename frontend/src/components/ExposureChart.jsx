import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid, ReferenceLine, Cell } from 'recharts';
import { fmtPct } from '../lib/format';

export default function ExposureChart({ exposure }) {
  if (!exposure) return null;

  // Simpler: just use netWeight directly
  const chartData = exposure.byClass
    .filter((c) => c.grossWeight > 0.001)
    .map((c) => ({
      name: c.label,
      value: c.netWeight * 100,
      count: c.positionCount,
    }));

  if (exposure.cashWeight > 0.001) {
    chartData.push({ name: 'Caixa', value: exposure.cashWeight * 100, count: 0 });
  }

  chartData.sort((a, b) => Math.abs(b.value) - Math.abs(a.value));

  return (
    <div>
      {/* Stats row */}
      <div style={{ display: 'flex', gap: 16, marginBottom: 16, flexWrap: 'wrap' }}>
        {[
          { label: 'Bruta', value: exposure.grossExposure, color: 'var(--text)' },
          { label: 'Liquida', value: exposure.netExposure, color: exposure.netExposure >= 0 ? 'var(--green)' : 'var(--red)' },
          { label: 'Long', value: exposure.longExposure, color: 'var(--green)' },
          { label: 'Short', value: exposure.shortExposure, color: 'var(--red)' },
          { label: 'Caixa', value: exposure.cashWeight, color: 'var(--text-muted)' },
        ].map((s) => (
          <div key={s.label} style={{ minWidth: 80 }}>
            <div style={{ fontSize: 10, color: 'var(--text-dim)', textTransform: 'uppercase', letterSpacing: '0.08em', marginBottom: 4 }}>
              {s.label}
            </div>
            <div style={{ fontSize: 18, fontWeight: 600, color: s.color }}>
              {fmtPct(s.value)}
            </div>
          </div>
        ))}
      </div>

      {/* Bar chart */}
      {chartData.length > 0 && (
        <ResponsiveContainer width="100%" height={Math.max(120, chartData.length * 36)}>
          <BarChart data={chartData} layout="vertical" margin={{ left: 80, right: 20 }}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" horizontal={false} />
            <XAxis
              type="number"
              tick={{ fill: 'var(--text-dim)', fontSize: 10 }}
              tickFormatter={(v) => `${v.toFixed(0)}%`}
            />
            <YAxis
              type="category"
              dataKey="name"
              tick={{ fill: 'var(--text)', fontSize: 11 }}
              width={75}
            />
            <Tooltip
              contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 6, fontSize: 12 }}
              formatter={(v) => [`${v.toFixed(2)}%`, 'Exposicao']}
            />
            <ReferenceLine x={0} stroke="var(--text-dim)" />
            <Bar dataKey="value" radius={[0, 4, 4, 0]}>
              {chartData.map((entry, i) => (
                <Cell key={i} fill={entry.value >= 0 ? 'var(--accent-solid)' : 'var(--red)'} />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      )}
    </div>
  );
}
