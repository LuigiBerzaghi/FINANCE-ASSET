import { useState } from 'react';
import { post } from '../lib/api';
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

const inputStyle = {
  background: 'var(--surface-alt)',
  border: '1px solid var(--border)',
  borderRadius: 4,
  color: 'var(--text)',
  padding: '8px 12px',
  fontSize: 13,
  outline: 'none',
  width: '100%',
  boxSizing: 'border-box',
};

const labelStyle = {
  fontSize: 10,
  color: 'var(--text-muted)',
  textTransform: 'uppercase',
  letterSpacing: '0.08em',
  marginBottom: 4,
  display: 'block',
};

// Fechamento total ou parcial de uma posicao: o lado vem da posicao (vende o comprado, recompra o vendido)
// e a quantidade nao passa da posicao, entao fechar nunca inverte. Justificativa obrigatoria.
function ClosePanel({ position, fundId, onCancel, onDone }) {
  const held = Math.abs(position.quantity);
  const isLong = position.quantity > 0;
  const [quantity, setQuantity] = useState(String(held));
  const [thesis, setThesis] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const qty = parseFloat(String(quantity).replace(',', '.'));
  const estimated = position.currentPrice != null && qty > 0 ? qty * position.currentPrice : null;

  const submit = async () => {
    setError(null);
    if (!(qty > 0)) return setError('Informe a quantidade a fechar');
    if (qty > held + 1e-9) return setError(`A quantidade nao pode passar da posicao (${fmtQty(held)})`);
    if (!thesis.trim()) return setError('Informe a justificativa do fechamento');

    setLoading(true);
    try {
      const trade = await post('/trades/close', {
        fundId: parseInt(fundId),
        ticker: position.ticker,
        quantity: qty,
        thesis: thesis.trim(),
      });
      onDone(
        `${qty >= held - 1e-9 ? 'Posicao fechada' : 'Posicao reduzida'}: ${isLong ? 'venda' : 'recompra'} de `
        + `${fmtQty(trade.quantity)} ${trade.ticker} a ${fmtBRL(trade.price)} cada`
        + ` = ${fmtBRL(trade.price * trade.quantity)} no total`,
      );
    } catch (e) {
      setError(e.message);
      setLoading(false);
    }
  };

  return (
    <div style={{
      marginTop: 12, padding: 14, borderRadius: 6, border: '1px solid var(--border)', background: 'var(--surface-alt)',
      display: 'grid', gridTemplateColumns: '1fr 2fr', gap: 12,
    }}>
      <div style={{ gridColumn: '1 / -1', fontSize: 13, color: 'var(--text)' }}>
        Fechar posicao em <strong>{position.ticker}</strong>: {isLong ? 'comprado' : 'vendido'} em {fmtQty(held)}.
        {' '}O fechamento {isLong ? 'vende' : 'recompra'} ao preco de mercado no momento da execucao.
      </div>
      <div>
        <label style={labelStyle}>Quantidade (max. {fmtQty(held)})</label>
        <input style={inputStyle} type="number" min="0" max={held} step="any" value={quantity}
          onChange={(e) => { setQuantity(e.target.value); setError(null); }} />
        <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 4 }}>
          {estimated != null ? `Valor estimado: ${fmtBRL(estimated)}` : ''}
        </div>
      </div>
      <div>
        <label style={labelStyle}>Justificativa (obrigatoria)</label>
        <input style={inputStyle} placeholder="Por que esta fechando a posicao..." value={thesis}
          onChange={(e) => { setThesis(e.target.value); setError(null); }} />
      </div>
      <div style={{ gridColumn: '1 / -1', display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
        <button onClick={submit} disabled={loading}
          style={{
            padding: '8px 18px', borderRadius: 4, border: 'none', cursor: loading ? 'wait' : 'pointer',
            background: 'var(--accent-solid)', color: '#fff', fontWeight: 700, fontSize: 12,
            textTransform: 'uppercase', letterSpacing: '0.05em', opacity: loading ? 0.6 : 1,
          }}>
          {loading ? 'Executando...' : (qty >= held - 1e-9 ? 'Fechar posicao' : 'Fechar parcialmente')}
        </button>
        <button onClick={onCancel} disabled={loading}
          style={{
            padding: '8px 14px', borderRadius: 4, border: '1px solid var(--border)', background: 'transparent',
            color: 'var(--text-dim)', fontSize: 12, cursor: 'pointer',
          }}>
          Cancelar
        </button>
        {error && <span style={{ color: 'var(--red)', fontSize: 12 }}>{error}</span>}
      </div>
    </div>
  );
}

export default function PositionsTable({ positions, fundId, onClosed }) {
  const [closing, setClosing] = useState(null);
  const [message, setMessage] = useState(null);
  const canClose = Boolean(fundId);

  if (!positions?.length) {
    return (
      <div style={{ color: 'var(--text-muted)', padding: 20 }}>
        Nenhuma posição aberta
        {message && <div style={{ color: 'var(--green)', fontSize: 12, marginTop: 8 }}>{message}</div>}
      </div>
    );
  }

  const done = (text) => {
    setClosing(null);
    setMessage(text);
    onClosed?.();
    // O batch pos-trade atualiza precos e NAV em background
    setTimeout(() => onClosed?.(), 5000);
  };

  return (
    <div style={{ overflowX: 'auto' }}>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
        <thead>
          <tr>
            {[...cols, ...(canClose ? [{ key: 'close', label: '', align: 'center' }] : [])].map((c) => (
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
              {canClose && (
                <td style={{ padding: '10px 12px', textAlign: 'center' }}>
                  <button
                    onClick={() => { setMessage(null); setClosing(p); }}
                    style={{
                      padding: '3px 8px',
                      borderRadius: 3,
                      border: '1px solid var(--accent-solid)',
                      background: 'transparent',
                      color: 'var(--text)',
                      fontSize: 10,
                      cursor: 'pointer',
                      fontWeight: 600,
                    }}
                  >
                    Fechar
                  </button>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
      {closing && (
        <ClosePanel
          key={closing.ticker}
          position={closing}
          fundId={fundId}
          onCancel={() => setClosing(null)}
          onDone={done}
        />
      )}
      {message && <div style={{ color: 'var(--green)', fontSize: 12, marginTop: 10 }}>{message}</div>}
    </div>
  );
}
