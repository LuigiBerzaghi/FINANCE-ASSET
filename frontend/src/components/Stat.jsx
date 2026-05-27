export default function Stat({ label, value, sub, color }) {
  return (
    <div style={{
      padding: '16px 20px',
      background: 'var(--surface)',
      border: '1px solid var(--border)',
      borderRadius: 6,
      flex: '1 1 180px',
      minWidth: 160,
    }}>
      <div style={{
        fontSize: 11,
        color: 'var(--text-muted)',
        textTransform: 'uppercase',
        letterSpacing: '0.08em',
        marginBottom: 6,
      }}>
        {label}
      </div>
      <div style={{
        fontSize: 22,
        fontWeight: 700,
        color: color || 'var(--text)',
        letterSpacing: '-0.02em',
      }}>
        {value}
      </div>
      {sub && (
        <div style={{ fontSize: 11, color: 'var(--text-dim)', marginTop: 4 }}>
          {sub}
        </div>
      )}
    </div>
  );
}
