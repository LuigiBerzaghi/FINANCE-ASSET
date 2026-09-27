import { useEffect, useMemo, useState } from 'react';
import { post, get } from '../lib/api';
import { fmtBRL, fmtMoney, fmtQty, fmtRate } from '../lib/format';

const MODES = [
  { key: 'market', label: 'Acoes, ETFs e outros' },
  { key: 'tesouro', label: 'Tesouro Direto' },
];

export default function TradeForm({ funds, activeFund, currentUser, onSubmit }) {
  const [mode, setMode] = useState('market');
  const [form, setForm] = useState({
    fundId: '', ticker: '', side: 'long', quantity: '', thesis: '',
  });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [success, setSuccess] = useState(null);
  const [currentPrice, setCurrentPrice] = useState(null);
  const [quote, setQuote] = useState(null);
  const [priceLoading, setPriceLoading] = useState(false);
  const [bonds, setBonds] = useState(null);
  const [bondsError, setBondsError] = useState(null);
  const isLeader = currentUser?.role === 'leader';
  const isTesouro = mode === 'tesouro';
  const defaultFundId = activeFund || funds[0]?.id || '';

  useEffect(() => {
    if (!isLeader) return;
    setForm((f) => ({ ...f, fundId: defaultFundId ? String(defaultFundId) : '' }));
  }, [defaultFundId, isLeader]);

  useEffect(() => {
    if (!isTesouro || bonds) return;
    setBondsError(null);
    get('/treasury/bonds')
      .then(setBonds)
      .catch(() => setBondsError('Nao foi possivel carregar os titulos do Tesouro Direto. Tente novamente em instantes.'));
  }, [isTesouro, bonds]);

  const bondGroups = useMemo(() => {
    const groups = new Map();
    for (const b of bonds || []) {
      if (!groups.has(b.type)) groups.set(b.type, []);
      groups.get(b.type).push(b);
    }
    return [...groups.entries()];
  }, [bonds]);

  const selectedBond = isTesouro ? (bonds || []).find((b) => b.ticker === form.ticker) : null;
  const quantity = parseFloat(form.quantity || 0);
  const bondUnitPrice = selectedBond ? (form.side === 'long' ? selectedBond.buyPrice : selectedBond.sellPrice) : null;

  const switchMode = (next) => {
    if (next === mode) return;
    setMode(next);
    setForm((f) => ({ ...f, ticker: '', side: 'long', quantity: '' }));
    setCurrentPrice(null);
    setQuote(null);
    setError(null);
    setSuccess(null);
  };

  const fetchPrice = async (ticker) => {
    if (!ticker || ticker.trim().length < 2) {
      setCurrentPrice(null);
      setQuote(null);
      return;
    }
    setPriceLoading(true);
    try {
      const data = await get(`/prices/current/${ticker.trim().toUpperCase()}`);
      setCurrentPrice(data.price);
      setQuote(data);
    } catch {
      setCurrentPrice(null);
      setQuote(null);
    }
    setPriceLoading(false);
  };

  const handleSubmit = async () => {
    setError(null);
    setSuccess(null);
    setLoading(true);
    try {
      const selectedFundId = isLeader ? form.fundId : defaultFundId;
      const payload = {
        fundId: parseInt(selectedFundId),
        ticker: form.ticker.trim().toUpperCase(),
        side: form.side,
        quantity: parseFloat(form.quantity),
        thesis: form.thesis || null,
        executedBy: null,
      };
      if (!payload.fundId || !payload.ticker || !payload.quantity) {
        const what = isTesouro ? 'titulo' : 'ticker';
        throw new Error(isLeader ? `Preencha fundo, ${what} e quantidade` : `Preencha ${what} e quantidade`);
      }
      const trade = await post('/trades', payload);
      const nativeUnit = trade.currency && trade.currency !== 'BRL' && trade.fxRate > 0
        ? ` (${fmtMoney(trade.price / trade.fxRate, trade.currency)} cada)`
        : '';
      const action = isTesouro
        ? (payload.side === 'long' ? 'Compra executada:' : 'Venda executada:')
        : `Trade executado: ${payload.side.toUpperCase()}`;
      setSuccess(
        `${action} ${fmtQty(trade.quantity)} ${trade.ticker}`
        + ` a ${fmtBRL(trade.price)} cada${nativeUnit}`
        + ` = ${fmtBRL(trade.price * trade.quantity)} no total`,
      );
      setForm((f) => ({ ...f, ticker: '', quantity: '', thesis: '' }));
      setCurrentPrice(null);
      setQuote(null);
      onSubmit?.();
      setTimeout(() => onSubmit?.(), 5000);
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

  const sideLabel = (s) => (isTesouro ? (s === 'long' ? 'Comprar' : 'Vender') : s);

  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: 12 }}>
      <div style={{ gridColumn: '1 / -1', display: 'flex', gap: 4, flexWrap: 'wrap' }}>
        {MODES.map((m) => (
          <button key={m.key} onClick={() => switchMode(m.key)}
            style={{
              padding: '6px 14px', borderRadius: 4, cursor: 'pointer', fontSize: 12,
              border: `1px solid ${mode === m.key ? 'var(--accent)' : 'var(--border)'}`,
              background: mode === m.key ? 'var(--accent-dim)' : 'transparent',
              color: mode === m.key ? 'var(--accent)' : 'var(--text-muted)',
              fontWeight: mode === m.key ? 600 : 400,
            }}>{m.label}</button>
        ))}
      </div>
      {isLeader && (
        <div>
          <label style={labelStyle}>Fundo</label>
          <select style={inputStyle} value={form.fundId} onChange={(e) => setForm((f) => ({ ...f, fundId: e.target.value }))}>
            <option value="">Selecione</option>
            {funds.map((f) => (
              <option key={f.id} value={f.id}>{f.name}</option>
            ))}
          </select>
        </div>
      )}
      {isTesouro ? (
        <div style={{ gridColumn: 'span 2' }}>
          <label style={labelStyle}>Titulo</label>
          <select style={inputStyle} value={form.ticker} disabled={!bonds}
            onChange={(e) => setForm((f) => ({ ...f, ticker: e.target.value }))}>
            <option value="">{bonds ? 'Selecione o titulo' : 'Carregando titulos...'}</option>
            {bondGroups.map(([type, list]) => (
              <optgroup key={type} label={type}>
                {list.map((b) => (
                  <option key={b.ticker} value={b.ticker}>
                    {b.name} | {fmtRate(b.code, b.buyRate)} | {fmtBRL(b.canBuy ? b.buyPrice : b.sellPrice)}{b.canBuy ? '' : ' (so venda)'}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>
          {bondsError && <div style={{ fontSize: 11, color: 'var(--red)', marginTop: 4 }}>{bondsError}</div>}
        </div>
      ) : (
        <div>
          <label style={labelStyle}>Ticker</label>
          <input style={inputStyle} placeholder="PETR4" value={form.ticker}
            onChange={(e) => setForm((f) => ({ ...f, ticker: e.target.value }))}
            onBlur={(e) => fetchPrice(e.target.value)} />
          {priceLoading && (
            <div style={{ fontSize: 11, color: 'var(--text-dim)', marginTop: 4 }}>Buscando preco...</div>
          )}
          {currentPrice && !priceLoading && (
            <div style={{ fontSize: 12, color: 'var(--green)', marginTop: 4, fontWeight: 600 }}>
              Preco atual: {fmtBRL(currentPrice)}
            </div>
          )}
          {quote && quote.currency !== 'BRL' && !priceLoading && (
            <div style={{ fontSize: 11, color: 'var(--text-dim)', marginTop: 2 }}>
              {fmtMoney(quote.nativePrice, quote.currency)} × {quote.fxRate?.toFixed(4)} {quote.currency}/BRL = {fmtBRL(quote.price)}
            </div>
          )}
        </div>
      )}
      <div>
        <label style={labelStyle}>{isTesouro ? 'Operacao' : 'Side'}</label>
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
              }}>{sideLabel(s)}</button>
          ))}
        </div>
      </div>
      <div>
        <label style={labelStyle}>{isTesouro ? 'Quantidade de titulos' : 'Quantidade'}</label>
        <input style={inputStyle} type="number" placeholder={isTesouro ? '1,00' : '100'} value={form.quantity}
          step={isTesouro ? '0.01' : 'any'} min={isTesouro ? '0.01' : undefined}
          onChange={(e) => setForm((f) => ({ ...f, quantity: e.target.value }))} />
        {!isTesouro && currentPrice && form.quantity && (
          <div style={{ fontSize: 11, color: 'var(--text-dim)', marginTop: 4 }}>
            Custo estimado: {fmtBRL(currentPrice * quantity)}
          </div>
        )}
        {isTesouro && bondUnitPrice > 0 && form.quantity && (
          <div style={{ fontSize: 11, color: 'var(--text-dim)', marginTop: 4 }}>
            {form.side === 'long' ? 'Custo estimado' : 'Valor estimado da venda'}: {fmtBRL(bondUnitPrice * quantity)}
          </div>
        )}
      </div>
      <div>
        <label style={labelStyle}>Gestor</label>
        <div style={{
          ...inputStyle,
          minHeight: 34,
          color: 'var(--text-muted)',
          display: 'flex',
          alignItems: 'center',
        }}>
          {currentUser?.name || 'Usuario autenticado'}
        </div>
      </div>
      {selectedBond && (
        <div style={{
          gridColumn: '1 / -1', display: 'flex', flexWrap: 'wrap', gap: '8px 24px',
          padding: '10px 12px', borderRadius: 4, border: '1px solid var(--border)', background: 'var(--surface-alt)',
          fontSize: 12, color: 'var(--text-muted)',
        }}>
          <span><strong style={{ color: 'var(--text)' }}>{selectedBond.name}</strong> ({selectedBond.ticker})</span>
          <span>Vencimento: {selectedBond.maturity.split('-').reverse().join('/')}</span>
          <span>Taxa compra: <strong style={{ color: 'var(--green)' }}>{fmtRate(selectedBond.code, selectedBond.buyRate)}</strong></span>
          <span>Taxa venda: <strong style={{ color: 'var(--red)' }}>{fmtRate(selectedBond.code, selectedBond.sellRate)}</strong></span>
          <span>PU compra: <strong style={{ color: 'var(--green)' }}>{selectedBond.canBuy ? fmtBRL(selectedBond.buyPrice) : 'indisponivel'}</strong></span>
          <span>PU venda: <strong style={{ color: 'var(--red)' }}>{fmtBRL(selectedBond.sellPrice)}</strong></span>
          <span>Precos de {selectedBond.baseDate.split('-').reverse().join('/')} (Tesouro Transparente)</span>
        </div>
      )}
      <div style={{ gridColumn: '1 / -1' }}>
        <label style={labelStyle}>Tese</label>
        <input style={inputStyle} placeholder="Justificativa do trade..." value={form.thesis}
          onChange={(e) => setForm((f) => ({ ...f, thesis: e.target.value }))} />
      </div>
      <div style={{ gridColumn: '1 / -1' }}>
        <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap' }}>
          <button onClick={handleSubmit} disabled={loading}
            style={{
              padding: '10px 24px', borderRadius: 4, border: 'none', cursor: loading ? 'wait' : 'pointer',
              background: 'var(--accent-solid)', color: '#fff', fontWeight: 700, fontSize: 13,
              textTransform: 'uppercase', letterSpacing: '0.05em',
              opacity: loading ? 0.6 : 1,
              transition: 'background 0.2s',
            }}>
            {loading ? 'Buscando preco e executando...' : 'Executar Trade'}
          </button>
          {error && <span style={{ color: 'var(--red)', fontSize: 12 }}>{error}</span>}
          {success && <span style={{ color: 'var(--green)', fontSize: 12 }}>{success}</span>}
        </div>
      </div>
    </div>
  );
}
