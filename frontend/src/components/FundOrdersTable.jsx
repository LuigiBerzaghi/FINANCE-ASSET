import { useState } from 'react';
import { post } from '../lib/api';
import { fmtBRL, fmtBrasilia, fmtDate, fmtDateTime, fmtQty, fmtQuota } from '../lib/format';

const STATUS = {
  pending: { label: 'Pendente', color: 'var(--yellow)', background: 'var(--yellow-dim)' },
  executed: { label: 'Executada', color: 'var(--green)', background: 'var(--green-dim)' },
  cancelled: { label: 'Cancelada', color: 'var(--text-muted)', background: 'var(--surface-alt)' },
  rejected: { label: 'Recusada', color: 'var(--red)', background: 'var(--red-dim)' },
};

const cell = { padding: '8px 10px', verticalAlign: 'top' };

const operation = (o) => (o.side === 'long' ? 'Aplicacao' : (o.redeemAll ? 'Resgate total' : 'Resgate'));

const size = (o) => {
  if (o.redeemAll) return 'Todas as cotas';
  if (o.quantity != null) return `${fmtQty(o.quantity)} cotas`;
  return fmtBRL(o.amount);
};

// Ordens de aplicacao/resgate em cotas de fundos: pendentes (aguardando a CVM publicar a cota do dia da
// ordem) e as ja executadas, canceladas ou recusadas. O gestor cancela ate o horario de corte do dia da cota.
export default function FundOrdersTable({ orders, onChange }) {
  const [busy, setBusy] = useState(null);
  const [error, setError] = useState(null);

  if (!orders?.length) {
    return <div style={{ color: 'var(--text-muted)', padding: 20 }}>Nenhuma ordem de fundo registrada</div>;
  }

  const cancel = async (order) => {
    if (!confirm(`Cancelar a ordem de ${operation(order).toLowerCase()} em ${order.name || order.ticker}?`)) return;
    setBusy(order.id);
    setError(null);
    try {
      await post(`/fund-orders/${order.id}/cancel`, {});
      onChange?.();
    } catch (e) {
      setError(e.message);
    }
    setBusy(null);
  };

  const headers = ['Ordem', 'Fundo', 'Operacao', 'Tamanho', 'Cota de', 'Status', 'Execucao', 'Gestor', ''];

  return (
    <div>
      <div style={{ overflowX: 'auto', maxHeight: 360, overflowY: 'auto' }}>
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
          <thead>
            <tr>
              {headers.map((h, i) => (
                <th key={i} style={{
                  padding: '8px 10px', textAlign: 'left', color: 'var(--text-muted)', borderBottom: '1px solid var(--border)',
                  fontSize: 10, textTransform: 'uppercase', letterSpacing: '0.08em', fontWeight: 500,
                  position: 'sticky', top: 0, background: 'var(--surface)',
                }}>{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {orders.map((o) => {
              const status = STATUS[o.status] || STATUS.cancelled;
              return (
                <tr key={o.id} style={{ borderBottom: '1px solid var(--border)' }}>
                  <td style={{ ...cell, color: 'var(--text-dim)', whiteSpace: 'nowrap' }}>{fmtDateTime(o.createdAt)}</td>
                  <td style={{ ...cell, color: 'var(--text)', maxWidth: 280 }}>
                    <div style={{ fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={o.name || o.ticker}>
                      {o.name || o.ticker}
                    </div>
                    <div style={{ fontSize: 10, color: 'var(--text-dim)' }}>{o.ticker}</div>
                  </td>
                  <td style={{ ...cell, color: o.side === 'long' ? 'var(--green)' : 'var(--red)', fontWeight: 600, whiteSpace: 'nowrap' }}>
                    {operation(o)}
                  </td>
                  <td style={{ ...cell, color: 'var(--text)', whiteSpace: 'nowrap' }}>{size(o)}</td>
                  <td style={{ ...cell, color: 'var(--text)', whiteSpace: 'nowrap' }}>{fmtDate(o.quoteDate)}</td>
                  <td style={cell}>
                    <span style={{
                      padding: '2px 6px', borderRadius: 3, fontSize: 10, fontWeight: 600, textTransform: 'uppercase',
                      background: status.background, color: status.color, whiteSpace: 'nowrap',
                    }}>{status.label}</span>
                    {o.message && <div style={{ fontSize: 10, color: 'var(--text-dim)', marginTop: 4, maxWidth: 220 }}>{o.message}</div>}
                    {o.status === 'pending' && (
                      <div style={{ fontSize: 10, color: 'var(--text-dim)', marginTop: 4, maxWidth: 220 }}>
                        Aguardando a CVM publicar a cota
                        {o.canCancel ? `; cancela ate ${fmtBrasilia(o.cancelUntil)}` : ''}
                      </div>
                    )}
                  </td>
                  <td style={{ ...cell, color: 'var(--text)', whiteSpace: 'nowrap' }}>
                    {o.status === 'executed' && o.price != null ? (
                      <>
                        <div>{fmtQty(o.executedQuantity)} cotas a {fmtQuota(o.price)}</div>
                        <div style={{ fontSize: 10, color: 'var(--text-dim)' }}>
                          = {fmtBRL(o.executedQuantity != null ? o.executedQuantity * o.price : null)} (cota de {fmtDate(o.priceDate)})
                        </div>
                      </>
                    ) : '—'}
                  </td>
                  <td style={{ ...cell, color: 'var(--text-dim)' }}>{o.createdBy || '—'}</td>
                  <td style={{ ...cell, textAlign: 'center' }}>
                    {o.canCancel && (
                      <button type="button" onClick={() => cancel(o)} disabled={busy === o.id}
                        title={`Gestor pode cancelar ate ${fmtBrasilia(o.cancelUntil)}`}
                        style={{
                          padding: '3px 8px', borderRadius: 3, border: '1px solid var(--red)', background: 'transparent',
                          color: 'var(--red)', fontSize: 10, cursor: busy === o.id ? 'wait' : 'pointer', fontWeight: 600,
                          whiteSpace: 'nowrap',
                        }}>
                        {busy === o.id ? 'Cancelando...' : 'Cancelar'}
                      </button>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {error && <div style={{ color: 'var(--red)', fontSize: 12, marginTop: 8 }}>{error}</div>}
    </div>
  );
}
