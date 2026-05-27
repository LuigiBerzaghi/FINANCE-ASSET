import {
  LineChart, Line, XAxis, YAxis, Tooltip, ResponsiveContainer,
  CartesianGrid, ReferenceLine,
} from 'recharts';
import { fmtNum } from '../lib/format';

export default function ShareChart({ navData }) {
  if (!navData?.length) return null;

  return (
    <ResponsiveContainer width="100%" height={200}>
      <LineChart data={navData} margin={{ top: 10, right: 10, left: 0, bottom: 0 }}>
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
          domain={['dataMin - 0.001', 'dataMax + 0.001']}
          tickFormatter={(v) => v.toFixed(4)}
        />
        <Tooltip
          contentStyle={{
            background: 'var(--surface)',
            border: '1px solid var(--border)',
            borderRadius: 6,
            fontSize: 12,
          }}
          formatter={(v) => [fmtNum(v), 'Cota']}
        />
        <ReferenceLine y={1.0} stroke="var(--text-dim)" strokeDasharray="4 4" />
        <Line type="monotone" dataKey="shareValue" stroke="var(--chart-line)" strokeWidth={2} dot={false} />
      </LineChart>
    </ResponsiveContainer>
  );
}
