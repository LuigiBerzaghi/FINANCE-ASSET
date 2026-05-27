import { useState } from 'react';
import { post } from '../lib/api';
import { fmtBRL } from '../lib/format';

export default function TradeForm({ funds, onSubmit }) {
  const [form, setForm] = useState({
    fundId: '', ticker: '', side: 'long', quantity: '', price: '', thesis: '', executedBy: '',
  });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [success, setSuccess] = useState(null);

  const handleSubmit = async () => {
    setError(null);
    setSuccess(null);
    setLoading(true);
    try {
      const payload = {
        fundId: parseInt(form.fundId),
        ticker: form.ticker.trim().toUpperCase(),
        side: form.side,
        quantity: parseFloat(form.quantity),
        price: parseFloat(form.price),
        thesis: form.thesis || null,
        executedBy: form.executedBy || null,
      };
      if (!payload.fundId || !payload.ticker || !payload.quantity || !payload.price) {
        throw new Error('Preencha fundo, ticker, quantidade e preco');
      }
      await post('/trades', payload);
      setSuccess(`Trade executado: ${payload.side.toUpperCase()} ${payload.quantity} ${payload.ticker} @ ${fmtBRL(payload.price)}`);
      setForm((f) => ({ ...f, ticker: '', quantity: '', price: '', thesis: '' }));
      onSubmit?.();
    } catch (e) {
      setError(e.message);
    }
    setLoading(false);
  };

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
    transition: 'background 0.2s, border-color 0.2s, color 0.2s',
  };

  const labelStyle = {
    fontSize: 10,
    color: 'var(--text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.08em',
    marginBottom: 4,
    display: 'block',
  };

  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: 12 }}>
      <div>
        <label style={labelStyle}>Fundo</label>
        <select style={inputStyle} value={form.fundId} onChange={(e) => setForm((f) => ({ ...f, fundId: e.target.value }))}>
          <option value="">Selecione</option>
          {funds.map((f) => (
            <option key={f.id} value={f.id}>{f.name}</option>
          ))}
        </select>
      </div>
      <div>
        <label style={labelStyle}>Ticker</label>
        <input style={inputStyle} placeholder="PETR4" value={form.ticker}
          onChange={(e) => setForm((f) => ({ ...f, ticker: e.target.value }))} />
      </div>
      <div>
        <label style={labelStyle}>Side</label>
        <div style={{ display: 'flex', gap: 4 }}>
          {['long', 'short'].map((s) => (
            <button key={s} onClick={() => setForm((f) => ({ ...f, side: s }))}
              style={{
                flex: 1, padding: '8px 0', borderRadius: 4, cursor: 'pointer',
                fontSize: 12, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em',
                background: form.side === s ? (s === 'long' ? 'var(--green-dim)' : 'var(--red-dim)') : 'var(--surface-alt)',
                color: form.side === s ? (s === 'long' ? 'var(--green)' : 'var(--red)') : 'var(--text-dim)',
                border: `1px solid ${form.side === s ? (s === 'long' ? 'var(--green)' : 'var(--red)') : 'var(--border)'}`,
                transition: 'all 0.15s',
              }}>{s}</button>
          ))}
        </div>
      </div>
      <div>
        <label style={labelStyle}>Quantidade</label>
        <input style={inputStyle} type="number" placeholder="100" value={form.quantity}
          onChange={(e) => setForm((f) => ({ ...f, quantity: e.target.value }))} />
      </div>
      <div>
        <label style={labelStyle}>Preco</label>
        <input style={inputStyle} type="number" step="0.01" placeholder="38.50" value={form.price}
          onChange={(e) => setForm((f) => ({ ...f, price: e.target.value }))} />
      </div>
      <div>
        <label style={labelStyle}>Gestor</label>
        <input style={inputStyle} placeholder="Nome" value={form.executedBy}
          onChange={(e) => setForm((f) => ({ ...f, executedBy: e.target.value }))} />
      </div>
      <div style={{ gridColumn: '1 / -1' }}>
        <label style={labelStyle}>Tese</label>
        <input style={inputStyle} placeholder="Justificativa do trade..." value={form.thesis}
          onChange={(e) => setForm((f) => ({ ...f, thesis: e.target.value }))} />
      </div>
      <div style={{ gridColumn: '1 / -1', display: 'flex', gap: 12, alignItems: 'center' }}>
        <button onClick={handleSubmit} disabled={loading}
          style={{
            padding: '10px 24px', borderRadius: 4, border: 'none', cursor: loading ? 'wait' : 'pointer',
            background: 'var(--accent-solid)', color: '#fff', fontWeight: 700, fontSize: 13,
            textTransform: 'uppercase', letterSpacing: '0.05em',
            opacity: loading ? 0.6 : 1,
            transition: 'background 0.2s',
          }}>
          {loading ? 'Executando...' : 'Executar Trade'}
        </button>
        {error && <span style={{ color: 'var(--red)', fontSize: 12 }}>{error}</span>}
        {success && <span style={{ color: 'var(--green)', fontSize: 12 }}>{success}</span>}
      </div>
    </div>
  );
}
