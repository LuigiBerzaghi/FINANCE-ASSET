import { useState } from 'react';
import { fmtPct } from '../lib/format';

export default function ReturnByClass({ data }) {
  const [period, setPeriod] = useState('inception');

  if (!data?.length) {
    return (
      <div style={{ color: 'var(--text-muted)', padding: 20, fontSize: 12 }}>
        Retorno por classe sera calculado apos multiplos dias de batch
      </div>
    );
  }

  const periodData = data.find((d) => d.period === period);
  if (!periodData) return null;

  const maxAbs = Math.max(...periodData.byClass.map((c) => Math.abs(c.contribution)), 0.001);

  return (
    <div>
      {/* Period tabs */}
      <div style={{ display: 'flex', gap: 4, marginBottom: 12 }}>
        {[
          { key: 'inception', label: 'Inicio' },
          { key: 'mtd', label: 'MTD' },
          { key: 'ytd', label: 'YTD' },
        ].map((p) => {
          const exists = data.some((d) => d.period === p.key);
          if (!exists) return null;
          return (
            <button key={p.key} onClick={() => setPeriod(p.key)}
              style={{
                padding: '4px 10px', borderRadius: 4, border: 'none', cursor: 'pointer',
                fontSize: 10, textTransform: 'uppercase', letterSpacing: '0.06em',
                background: period === p.key ? 'var(--accent-dim)' : 'transparent',
                color: period === p.key ? 'var(--accent)' : 'var(--text-dim)',
                fontWeight: period === p.key ? 600 : 400,
              }}>
              {p.label}
            </button>
          );
        })}
      </div>

      {/* Total */}
      <div style={{ marginBottom: 12, padding: '8px 0', borderBottom: '1px solid var(--border)' }}>
        <span style={{ fontSize: 10, color: 'var(--text-dim)', textTransform: 'uppercase', marginRight: 8 }}>
          Retorno total
        </span>
        <span style={{
          fontSize: 16, fontWeight: 600,
          color: periodData.totalReturn >= 0 ? 'var(--green)' : 'var(--red)',
        }}>
          {fmtPct(periodData.totalReturn)}
        </span>
      </div>

      {/* Class breakdown */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {periodData.byClass.map((c) => {
          const barWidth = Math.abs(c.contribution) / maxAbs * 100;
          const isPositive = c.contribution >= 0;
          return (
            <div key={c.assetClass} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <div style={{ width: 80, fontSize: 11, color: 'var(--text-muted)', flexShrink: 0 }}>
                {c.label}
              </div>
              <div style={{ flex: 1, height: 20, background: 'var(--surface-alt)', borderRadius: 3, position: 'relative', overflow: 'hidden' }}>
                <div style={{
                  position: 'absolute',
                  [isPositive ? 'left' : 'right']: 0,
                  top: 0, bottom: 0,
                  width: `${Math.min(barWidth, 100)}%`,
                  background: isPositive ? 'var(--green-dim)' : 'var(--red-dim)',
                  borderRadius: 3,
                  transition: 'width 0.3s',
                }} />
              </div>
              <div style={{
                width: 60, textAlign: 'right', fontSize: 12, fontWeight: 500,
                color: isPositive ? 'var(--green)' : 'var(--red)',
              }}>
                {fmtPct(c.contribution)}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
