import { fmtBRL, fmtQty } from '../lib/format';
import { del } from '../lib/api';

export default function TradesTable({ trades, onDelete }) {
  if (!trades?.length) {
    return <div style={{ color: 'var(--text-muted)', padding: 20 }}>Nenhum trade registrado</div>;
  }

  const headers = ['Data', 'Ticker', 'Side', 'Qtd', 'Preco', 'Tese', 'Gestor', ''];

  const handleDelete = async (id, ticker) => {
    if (!confirm(`Deletar trade de ${ticker}? Isso reverte a posicao e o caixa.`)) return;
    try {
      await del(`/trades/${id}`);
      onDelete?.();
    } catch (e) {
      alert('Erro ao deletar: ' + e.message);
    }
  };

  return (
    <div style={{ overflowX: 'auto', maxHeight: 360, overflowY: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
        <thead>
          <tr>
            {headers.map((h) => (
              <th
                key={h}
                style={{
                  padding: '8px 10px',
                  textAlign: 'left',
                  color: 'var(--text-muted)',
                  borderBottom: '1px solid var(--border)',
                  fontSize: 10,
                  textTransform: 'uppercase',
                  letterSpacing: '0.08em',
                  fontWeight: 500,
                  position: 'sticky',
                  top: 0,
                  background: 'var(--surface)',
                }}
              >
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {trades.map((t, i) => (
            <tr
              key={i}
              style={{ borderBottom: '1px solid var(--border)' }}
              onMouseEnter={(e) => (e.currentTarget.style.background = 'var(--surface-alt)')}
              onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}
            >
              <td style={{ padding: '8px 10px', color: 'var(--text-dim)' }}>
                {t.executedAt?.slice(0, 10)}
              </td>
              <td style={{ padding: '8px 10px', color: 'var(--text)', fontWeight: 600 }}>
                {t.ticker}
              </td>
              <td style={{ padding: '8px 10px' }}>
                <span
                  style={{
                    padding: '2px 6px',
                    borderRadius: 3,
                    fontSize: 10,
                    fontWeight: 600,
                    textTransform: 'uppercase',
                    background: t.side === 'long' ? 'var(--green-dim)' : 'var(--red-dim)',
                    color: t.side === 'long' ? 'var(--green)' : 'var(--red)',
                  }}
                >
                  {t.side}
                </span>
              </td>
              <td style={{ padding: '8px 10px', color: 'var(--text)', textAlign: 'right' }}>
                {fmtQty(t.quantity)}
              </td>
              <td style={{ padding: '8px 10px', color: 'var(--text)', textAlign: 'right' }}>
                {fmtBRL(t.price)}
              </td>
              <td
                style={{
                  padding: '8px 10px',
                  color: 'var(--text-muted)',
                  maxWidth: 200,
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}
              >
                {t.thesis || '—'}
              </td>
              <td style={{ padding: '8px 10px', color: 'var(--text-dim)' }}>
                {t.executedBy || '—'}
              </td>
              <td style={{ padding: '8px 10px', textAlign: 'center' }}>
                <button
                  onClick={() => handleDelete(t.id, t.ticker)}
                  style={{
                    padding: '3px 8px',
                    borderRadius: 3,
                    border: '1px solid var(--red)',
                    background: 'transparent',
                    color: 'var(--red)',
                    fontSize: 10,
                    cursor: 'pointer',
                    fontWeight: 600,
                    opacity: 0.7,
                    transition: 'opacity 0.15s',
                  }}
                  onMouseEnter={(e) => (e.currentTarget.style.opacity = '1')}
                  onMouseLeave={(e) => (e.currentTarget.style.opacity = '0.7')}
                >
                  Deletar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
