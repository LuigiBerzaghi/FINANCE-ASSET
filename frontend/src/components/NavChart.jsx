import {
  AreaChart, Area, XAxis, YAxis, Tooltip, ResponsiveContainer,
  CartesianGrid, ReferenceLine,
} from 'recharts';
import { fmtBRL } from '../lib/format';

export default function NavChart({ navData }) {
  if (!navData?.length) {
    return (
      <div style={{ color: 'var(--text-muted)', padding: 40, textAlign: 'center' }}>
        Sem historico de NAV
      </div>
    );
  }

  const initialEquity = navData[0]?.totalEquity;

  return (
    <ResponsiveContainer width="100%" height={280}>
      <AreaChart data={navData} margin={{ top: 10, right: 10, left: 0, bottom: 0 }}>
        <defs>
          <linearGradient id="navGrad" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--chart-stroke)" stopOpacity={0.25} />
            <stop offset="100%" stopColor="var(--chart-stroke)" stopOpacity={0} />
          </linearGradient>
        </defs>
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
          tickFormatter={(v) => `R$${(v / 1000).toFixed(0)}k`}
          domain={['dataMin - 1000', 'dataMax + 1000']}
        />
        <Tooltip
          contentStyle={{
            background: 'var(--surface)',
            border: '1px solid var(--border)',
            borderRadius: 6,
            fontSize: 12,
          }}
          labelStyle={{ color: 'var(--text-muted)' }}
          formatter={(v) => [fmtBRL(v), 'Patrimonio']}
        />
        <Area
          type="monotone"
          dataKey="totalEquity"
          stroke="var(--chart-stroke)"
          strokeWidth={2}
          fill="url(#navGrad)"
        />
        {initialEquity != null && (
          <ReferenceLine
            y={initialEquity}
            stroke="var(--text-dim)"
            strokeDasharray="4 4"
            label={{ value: 'Inicio', fill: 'var(--text-dim)', fontSize: 10 }}
          />
        )}
      </AreaChart>
    </ResponsiveContainer>
  );
}
