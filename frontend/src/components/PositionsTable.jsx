import { fmtBRL, fmtPct, fmtQty } from '../lib/format';

const cols = [
  { key: 'ticker', label: 'Ticker', align: 'left' },
  { key: 'side', label: 'Side', align: 'center' },
  { key: 'quantity', label: 'Qtd', align: 'right', fmt: fmtQty },
  { key: 'avgPrice', label: 'Preço Médio', align: 'right', fmt: fmtBRL },
  { key: 'currentPrice', label: 'Preço Atual', align: 'right', fmt: fmtBRL },
  { key: 'marketValue', label: 'Valor', align: 'right', fmt: fmtBRL },
  { key: 'unrealizedPnl', label: 'P&L', align: 'right', fmt: fmtBRL, color: true },
  { key: 'weight', label: 'Peso', align: 'right', fmt: fmtPct },
];

export default function PositionsTable({ positions }) {
  if (!positions?.length) {
    return <div style={{ color: 'var(--text-muted)', padding: 20 }}>Nenhuma posição aberta</div>;
  }

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
        <thead>
          <tr>
            {cols.map((c) => (
              <th
                key={c.key}
                style={{
                  padding: '10px 12px',
                  textAlign: c.align,
                  color: 'var(--text-muted)',
                  borderBottom: '1px solid var(--border)',
                  fontSize: 10,
                  textTransform: 'uppercase',
                  letterSpacing: '0.08em',
                  fontWeight: 500,
                }}
              >
                {c.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {positions.map((p, i) => (
            <tr
              key={i}
              style={{ borderBottom: '1px solid var(--border)' }}
              onMouseEnter={(e) => (e.currentTarget.style.background = 'var(--surface-alt)')}
              onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
            >
              {cols.map((c) => {
                const val = p[c.key];
                let color = 'var(--text)';
                if (c.color && val != null) color = val >= 0 ? 'var(--green)' : 'var(--red)';
                if (c.key === 'side') color = val === 'long' ? 'var(--green)' : 'var(--red)';
                const display = c.fmt ? c.fmt(val) : val;

                return (
                  <td key={c.key} style={{ padding: '10px 12px', textAlign: c.align, color }}>
                    {c.key === 'side' ? (
                      <span
                        style={{
                          padding: '2px 8px',
                          borderRadius: 3,
                          fontSize: 10,
                          fontWeight: 600,
                          textTransform: 'uppercase',
                          letterSpacing: '0.05em',
                          background: val === 'long' ? 'var(--green-dim)' : 'var(--red-dim)',
                          color: val === 'long' ? 'var(--green)' : 'var(--red)',
                        }}
                      >
                        {val}
                      </span>
                    ) : (
                      display
                    )}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
