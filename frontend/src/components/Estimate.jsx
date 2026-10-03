// Previa das contas abaixo dos campos de trade/fechamento, com cor fixa por tipo de numero
// (sempre com rotulo escrito, para nao depender so da cor):
// quantidade = destaque; valor da operacao = cor do lado (compra/long verde, venda/short vermelho);
// sobra no caixa = cinza; avisos = amarelo.

export const sideColor = (side) => (side === 'long' ? 'var(--green)' : 'var(--red)');

export const COLORS = {
  quantity: 'var(--accent)',
  leftover: 'var(--text-muted)',
};

export function EstimateLine({ items, note }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 16px', alignItems: 'baseline', fontSize: 12 }}>
        {items.filter(Boolean).map((item) => (
          <span key={item.label} style={{ whiteSpace: 'nowrap' }}>
            <span style={{
              fontSize: 10, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: '0.06em', marginRight: 6,
            }}>
              {item.label}
            </span>
            <strong style={{ color: item.color, fontWeight: 700 }}>{item.value}</strong>
          </span>
        ))}
      </div>
      {note && <div style={{ fontSize: 10, color: 'var(--text-dim)' }}>{note}</div>}
    </div>
  );
}

export function EstimateWarning({ children }) {
  return (
    <div style={{ fontSize: 12, color: 'var(--yellow)', fontWeight: 600 }}>
      ⚠ {children}
    </div>
  );
}

export function EstimateNote({ children }) {
  return <div style={{ fontSize: 11, color: 'var(--text-dim)' }}>{children}</div>;
}
