export default function FundTabs({ funds, active, onChange }) {
  return (
    <div style={{
      display: 'flex',
      gap: 2,
      background: 'var(--surface-alt)',
      borderRadius: 6,
      padding: 3,
    }}>
      {funds.map((f) => (
        <button
          key={f.id}
          onClick={() => onChange(f.id)}
          style={{
            padding: '8px 20px',
            borderRadius: 4,
            border: 'none',
            cursor: 'pointer',
            fontSize: 13,
            fontWeight: active === f.id ? 700 : 400,
            background: active === f.id ? 'var(--surface)' : 'transparent',
            color: active === f.id ? 'var(--accent)' : 'var(--text-muted)',
            transition: 'all 0.15s',
          }}
        >
          {f.name}
          {f.strategy && (
            <span style={{ fontSize: 10, color: 'var(--text-dim)', marginLeft: 6 }}>
              {f.strategy}
            </span>
          )}
        </button>
      ))}
    </div>
  );
}
