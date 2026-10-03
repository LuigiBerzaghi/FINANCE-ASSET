import { useEffect, useMemo, useState } from 'react';
import { post, get } from '../lib/api';
import { fmtBRL, fmtMoney, fmtQty, fmtRate, parseDecimal, quantityForAmount } from '../lib/format';
import { COLORS, EstimateLine, EstimateNote, EstimateWarning, sideColor } from './Estimate';

const MODES = [
  { key: 'market', label: 'Acoes, ETFs e outros' },
  { key: 'tesouro', label: 'Tesouro Direto' },
];

export default function TradeForm({ funds, activeFund, currentUser, onSubmit }) {
  const [mode, setMode] = useState('market');
  const [form, setForm] = useState({
    fundId: '', ticker: '', side: 'long', quantity: '', thesis: '',
  });
  // Por quantidade ou por valor (BRL): por valor o servidor calcula a quantidade no preco da execucao
  const [inputMode, setInputMode] = useState('quantity');
  const [amount, setAmount] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [success, setSuccess] = useState(null);
  const [currentPrice, setCurrentPrice] = useState(null);
  const [quote, setQuote] = useState(null);
  const [priceLoading, setPriceLoading] = useState(false);
  const [bonds, setBonds] = useState(null);
  const [bondsError, setBondsError] = useState(null);
  // Caixa do fundo e base do limite de venda a descoberto (100% do patrimonio) para o ativo escolhido
  const [shortLimit, setShortLimit] = useState(null);
  const [limitsVersion, setLimitsVersion] = useState(0);
  const isLeader = currentUser?.role === 'leader';
  const isTesouro = mode === 'tesouro';
  const defaultFundId = activeFund || funds[0]?.id || '';
  const tradeFundId = isLeader ? form.fundId : defaultFundId;
  // Ativo para a conta dos limites: o titulo escolhido no Tesouro ou o ticker com preco carregado
  const limitTicker = isTesouro ? form.ticker : quote?.ticker;

  useEffect(() => {
    if (!isLeader) return;
    setForm((f) => ({ ...f, fundId: defaultFundId ? String(defaultFundId) : '' }));
  }, [defaultFundId, isLeader]);

  useEffect(() => {
    if (!tradeFundId || !limitTicker) {
      setShortLimit(null);
      return;
    }
    get(`/trades/limits/${tradeFundId}?ticker=${encodeURIComponent(limitTicker)}`)
      .then(setShortLimit)
      .catch(() => setShortLimit(null));
  }, [tradeFundId, limitTicker, limitsVersion]);

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
  const byValue = inputMode === 'value';
  const unitPrice = isTesouro ? bondUnitPrice : currentPrice;
  const quantityStep = isTesouro ? 0.01 : (quote?.quantityStep ?? 1);
  const amountValue = parseDecimal(amount) || 0;
  const estimatedQty = byValue ? quantityForAmount(amountValue, unitPrice, quantityStep) : 0;
  const unitName = isTesouro ? 'titulo(s)' : 'unidade(s)';

  const switchMode = (next) => {
    if (next === mode) return;
    setMode(next);
    setForm((f) => ({ ...f, ticker: '', side: 'long', quantity: '' }));
    setAmount('');
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
    if (blocked) return;
    setError(null);
    setSuccess(null);
    setLoading(true);
    try {
      const selectedFundId = isLeader ? form.fundId : defaultFundId;
      const payload = {
        fundId: parseInt(selectedFundId),
        ticker: form.ticker.trim().toUpperCase(),
        side: form.side,
        quantity: byValue ? 0 : parseFloat(form.quantity),
        amount: byValue ? amountValue : null,
        thesis: form.thesis || null,
        executedBy: null,
      };
      if (!payload.fundId || !payload.ticker || !(byValue ? payload.amount > 0 : payload.quantity)) {
        const what = isTesouro ? 'titulo' : 'ticker';
        const size = byValue ? 'valor' : 'quantidade';
        throw new Error(isLeader ? `Preencha fundo, ${what} e ${size}` : `Preencha ${what} e ${size}`);
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
      setAmount('');
      setCurrentPrice(null);
      setQuote(null);
      setLimitsVersion((v) => v + 1);
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

  // Previa das contas: quantidade (destaque), valor (cor do lado), sobra (cinza) e avisos (amarelo)
  const valueLabel = isTesouro && form.side === 'long' ? 'Custo' : 'Valor';
  const unitShort = isTesouro ? 'titulo(s)' : 'un.';
  // Limite de venda a descoberto: o fundo nao pode ficar vendido em mais que 100% do patrimonio
  // (mesma conta do servidor, no preco desta tela; vender o que o fundo tem comprado nao conta)
  let shortAvailable = null;
  if (shortLimit && !isTesouro && form.side === 'short' && unitPrice > 0) {
    const held = shortLimit.heldQuantity;
    const equity = shortLimit.cash + shortLimit.otherPositionsValue + held * unitPrice;
    const limit = Math.max(0, equity) * shortLimit.maxShortExposure;
    const shortBefore = shortLimit.otherShortExposure + Math.max(0, -held) * unitPrice;
    shortAvailable = Math.max(0, limit - shortBefore) + Math.max(0, held) * unitPrice;
  }
  // Compra: limitada pelo caixa do fundo (mesma trava do servidor, que recusa caixa insuficiente)
  const cashAvailable = shortLimit && form.side === 'long' ? Math.max(0, shortLimit.cash) : null;
  const orderValue = (byValue ? estimatedQty : quantity) * (unitPrice || 0);
  const overShortLimit = shortAvailable != null && orderValue > shortAvailable + 0.005;
  const overCash = cashAvailable != null && unitPrice > 0 && orderValue > cashAvailable + 0.005;
  let limitItem = null;
  if (shortAvailable != null && !overShortLimit) {
    limitItem = { label: 'Disponivel p/ vender', value: fmtBRL(shortAvailable), color: COLORS.leftover };
  } else if (cashAvailable != null && !overCash) {
    limitItem = { label: 'Caixa disponivel', value: fmtBRL(cashAvailable), color: COLORS.leftover };
  }
  // Valor abaixo da menor quantidade negociavel, acima do limite de venda ou sem caixa: aviso amarelo e botao bloqueado
  const belowMinimum = byValue && amountValue > 0 && unitPrice > 0 && estimatedQty < quantityStep;
  const blocked = belowMinimum || overShortLimit || overCash;
  let preview = null;
  if (overCash && !belowMinimum) {
    preview = (
      <EstimateWarning>
        Caixa insuficiente: {isTesouro ? 'o custo' : 'a compra'} de {fmtBRL(orderValue)} passa do caixa disponivel
        {' '}({fmtBRL(cashAvailable)})
      </EstimateWarning>
    );
  } else if (overShortLimit && !belowMinimum) {
    preview = (
      <EstimateWarning>
        Acima do limite de venda a descoberto: disponivel para vender {fmtBRL(shortAvailable)}
        {' '}(limite de {Math.round(shortLimit.maxShortExposure * 100)}% do patrimonio do fundo)
      </EstimateWarning>
    );
  } else if (!byValue && unitPrice > 0 && quantity > 0) {
    preview = (
      <EstimateLine items={[
        { label: 'Quantidade', value: `${fmtQty(quantity)} ${unitShort}`, color: COLORS.quantity },
        { label: `${valueLabel} estimado`, value: fmtBRL(unitPrice * quantity), color: sideColor(form.side) },
        limitItem,
      ]} />
    );
  } else if (byValue && amountValue > 0 && !(unitPrice > 0)) {
    preview = <EstimateNote>{isTesouro ? 'Selecione o titulo para ver a quantidade' : 'Informe o ticker para ver a quantidade'}</EstimateNote>;
  } else if (byValue && amountValue > 0 && estimatedQty < quantityStep) {
    preview = (
      <EstimateWarning>
        Valor abaixo do minimo: {fmtBRL(quantityStep * unitPrice)} ({fmtQty(quantityStep)} {isTesouro ? 'titulo' : 'unidade'})
      </EstimateWarning>
    );
  } else if (byValue && amountValue > 0) {
    preview = (
      <EstimateLine
        items={[
          { label: 'Quantidade', value: `≈ ${fmtQty(estimatedQty)} ${unitShort}`, color: COLORS.quantity },
          { label: valueLabel, value: fmtBRL(estimatedQty * unitPrice), color: sideColor(form.side) },
          { label: 'Sobra no caixa', value: fmtBRL(amountValue - estimatedQty * unitPrice), color: COLORS.leftover },
          limitItem,
        ]}
        note="Quantidade final calculada no preco da execucao"
      />
    );
  } else if (limitItem) {
    preview = <EstimateLine items={[limitItem]} />;
  }

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
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 6 }}>
          <label style={labelStyle}>
            {byValue ? 'Valor (R$)' : (isTesouro ? 'Quantidade de titulos' : 'Quantidade')}
          </label>
          <div style={{ display: 'flex', gap: 2, marginBottom: 4 }}>
            {[['quantity', 'Qtd'], ['value', 'R$']].map(([key, label]) => (
              <button key={key} type="button" onClick={() => { setInputMode(key); setError(null); }}
                title={key === 'quantity' ? 'Por quantidade' : 'Por valor'}
                style={{
                  padding: '1px 8px', borderRadius: 3, cursor: 'pointer', fontSize: 10, fontWeight: 600,
                  border: `1px solid ${inputMode === key ? 'var(--accent)' : 'var(--border)'}`,
                  background: inputMode === key ? 'var(--accent-dim)' : 'transparent',
                  color: inputMode === key ? 'var(--accent)' : 'var(--text-muted)',
                }}>{label}</button>
            ))}
          </div>
        </div>
        {byValue ? (
          <input style={inputStyle} type="number" placeholder="10000,00" value={amount} min="0" step="any"
            onChange={(e) => setAmount(e.target.value)} />
        ) : (
          <input style={inputStyle} type="number" placeholder={isTesouro ? '1,00' : '100'} value={form.quantity}
            step={isTesouro ? '0.01' : 'any'} min={isTesouro ? '0.01' : undefined}
            onChange={(e) => setForm((f) => ({ ...f, quantity: e.target.value }))} />
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
      {preview && <div style={{ gridColumn: '1 / -1' }}>{preview}</div>}
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
          <button onClick={handleSubmit} disabled={loading || blocked}
            style={{
              padding: '10px 24px', borderRadius: 4, border: 'none', cursor: loading ? 'wait' : (blocked ? 'not-allowed' : 'pointer'),
              background: 'var(--accent-solid)', color: '#fff', fontWeight: 700, fontSize: 13,
              textTransform: 'uppercase', letterSpacing: '0.05em',
              opacity: loading || blocked ? 0.45 : 1,
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
