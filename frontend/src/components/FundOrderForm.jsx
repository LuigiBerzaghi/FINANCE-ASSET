import { useEffect, useState } from 'react';
import { get, post } from '../lib/api';
import { fmtBRL, fmtBrasilia, fmtDate, fmtQty, fmtQuota, parseDecimal } from '../lib/format';
import { COLORS, EstimateLine, EstimateNote, EstimateWarning, sideColor } from './Estimate';

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

const tagStyle = (color) => ({
  fontSize: 10, padding: '1px 6px', borderRadius: 3, fontWeight: 600,
  border: `1px solid ${color}`, color, whiteSpace: 'nowrap',
});

const toggleStyle = (active) => ({
  padding: '1px 8px', borderRadius: 3, cursor: 'pointer', fontSize: 10, fontWeight: 600,
  border: `1px solid ${active ? 'var(--accent)' : 'var(--border)'}`,
  background: active ? 'var(--accent-dim)' : 'transparent',
  color: active ? 'var(--accent)' : 'var(--text-muted)',
});

// Erro de GET: o corpo vem como texto ({"error": "..."})
const apiError = (e) => {
  try {
    return JSON.parse(e.message).error || e.message;
  } catch {
    return e.message;
  }
};

// Descricao da ordem registrada para a mensagem de sucesso
export const describeOrder = (order) => {
  const what = order.side === 'long'
    ? `aplicacao de ${fmtBRL(order.amount)}`
    : order.redeemAll
      ? 'resgate total'
      : order.quantity != null
        ? `resgate de ${fmtQty(order.quantity)} cotas`
        : `resgate de ${fmtBRL(order.amount)}`;
  return `Ordem registrada: ${what} em ${order.name || order.ticker}, pela cota de ${fmtDate(order.quoteDate)}.`
    + ' Executa quando a CVM publicar essa cota (cerca de 2 dias uteis);'
    + ` da para cancelar ate ${fmtBrasilia(order.cancelUntil)}.`;
};

// Boleta de cotas de fundos (dados da CVM): aplicacao por valor e resgate por valor, cotas ou total.
// A ordem vale pela cota do dia (ate o horario de corte; depois, a do proximo dia util) e e executada pelo
// batch quando a CVM publicar essa cota (~2 dias uteis): ninguem sabe o preco ao dar a ordem, como nos
// fundos reais. Aplicacao pendente reserva o valor no caixa.
export default function FundOrderForm({ funds, activeFund, currentUser, onSubmit }) {
  const isLeader = currentUser?.role === 'leader';
  const defaultFundId = activeFund || funds[0]?.id || '';
  const [portfolioId, setPortfolioId] = useState(defaultFundId ? String(defaultFundId) : '');
  const fundId = isLeader ? portfolioId : defaultFundId;

  const [query, setQuery] = useState('');
  const [results, setResults] = useState(null);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState(null);
  const [selected, setSelected] = useState(null);
  const [infoLoading, setInfoLoading] = useState(false);
  const [side, setSide] = useState('long');
  const [redeemMode, setRedeemMode] = useState('value');
  const [amount, setAmount] = useState('');
  const [quantity, setQuantity] = useState('');
  const [thesis, setThesis] = useState('');
  const [limits, setLimits] = useState(null);
  const [limitsVersion, setLimitsVersion] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [success, setSuccess] = useState(null);

  useEffect(() => {
    if (!isLeader) return;
    setPortfolioId(defaultFundId ? String(defaultFundId) : '');
  }, [defaultFundId, isLeader]);

  // Busca por nome ou CNPJ, com espera curta entre teclas
  useEffect(() => {
    const q = query.trim();
    if (q.length < 3) {
      setResults(null);
      setSearchError(null);
      setSearching(false);
      return undefined;
    }

    let cancelled = false;
    const timer = setTimeout(async () => {
      setSearching(true);
      try {
        const data = await get(`/fund-orders/search?q=${encodeURIComponent(q)}`);
        if (!cancelled) {
          setResults(data);
          setSearchError(null);
        }
      } catch (e) {
        if (!cancelled) {
          setResults(null);
          setSearchError(apiError(e));
        }
      }
      if (!cancelled) setSearching(false);
    }, 350);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [query]);

  // Caixa, caixa reservado, exposicao e cotas que o fundo ja tem deste fundo investido
  useEffect(() => {
    if (!fundId || !selected) {
      setLimits(null);
      return;
    }
    get(`/trades/limits/${fundId}?ticker=${encodeURIComponent(selected.fund.ticker)}`)
      .then(setLimits)
      .catch(() => setLimits(null));
  }, [fundId, selected, limitsVersion]);

  const selectFund = async (result) => {
    setResults(null);
    setQuery('');
    setError(null);
    setSuccess(null);
    setInfoLoading(true);
    try {
      setSelected(await get(`/fund-orders/info/${encodeURIComponent(result.ticker)}`));
    } catch (e) {
      setSelected(null);
      setError(apiError(e));
    }
    setInfoLoading(false);
  };

  const quota = selected?.fund.lastQuota;
  const held = limits?.heldQuantity ?? 0;
  const amountValue = parseDecimal(amount) || 0;
  const quantityValue = parseDecimal(quantity) || 0;
  const isApplication = side === 'long';

  // Aplicacao: limitada pelo caixa livre (sem o reservado) e pela exposicao bruta (mesma conta do servidor)
  const reserved = limits?.reservedCash ?? 0;
  const cashAvailable = limits ? Math.max(0, limits.cash - reserved) : null;
  let grossAvailable = null;
  if (limits && quota > 0) {
    const equity = limits.cash + limits.otherPositionsValue + held * quota;
    const limit = Math.max(0, equity) * limits.maxGrossExposure;
    const room = Math.max(limit - limits.otherGrossExposure, Math.abs(held) * quota);
    grossAvailable = Math.max(0, room - held * quota);
  }
  const grossBinds = grossAvailable != null && cashAvailable != null && grossAvailable < cashAvailable;
  const applyAvailable = grossBinds ? grossAvailable : cashAvailable;

  // Resgate: estimativa na ultima cota publicada (vale a cota do dia da ordem)
  const heldValue = held > 0 && quota > 0 ? held * quota : null;
  const redeemQty = redeemMode === 'all'
    ? held
    : redeemMode === 'quantity'
      ? quantityValue
      : (quota > 0 ? amountValue / quota : 0);

  let preview = null;
  let blocked = !selected || !fundId;
  if (selected && isApplication && !selected.acceptsApplications) {
    blocked = true;
    preview = <EstimateWarning>Este fundo nao aceita novas aplicacoes (encerrado, fechado ou exclusivo); so resgate</EstimateWarning>;
  } else if (selected && isApplication) {
    const over = applyAvailable != null && amountValue > applyAvailable + 0.005;
    blocked = blocked || over;
    if (over && grossBinds) {
      preview = (
        <EstimateWarning>
          Acima do limite de exposicao bruta: disponivel para aplicar {fmtBRL(applyAvailable)}
          {' '}(comprado + vendido ate {Math.round(limits.maxGrossExposure * 100)}% do patrimonio do fundo)
        </EstimateWarning>
      );
    } else if (over) {
      preview = (
        <EstimateWarning>
          Caixa insuficiente: a aplicacao de {fmtBRL(amountValue)} passa do caixa disponivel ({fmtBRL(cashAvailable)}
          {reserved > 0 ? `; ${fmtBRL(reserved)} ja reservado para aplicacoes pendentes` : ''})
        </EstimateWarning>
      );
    } else if (amountValue > 0 && quota > 0) {
      preview = (
        <EstimateLine
          items={[
            { label: 'Cotas', value: `≈ ${fmtQty(Math.floor((amountValue / quota) * 1e8) / 1e8)}`, color: COLORS.quantity },
            { label: 'Aplicacao', value: fmtBRL(amountValue), color: sideColor('long') },
            applyAvailable != null && { label: grossBinds ? 'Disponivel p/ aplicar' : 'Caixa disponivel', value: fmtBRL(applyAvailable), color: COLORS.leftover },
          ]}
          note={`Estimativa na ultima cota publicada (${fmtDate(selected.fund.lastQuotaDate)}); as cotas saem da cota de ${fmtDate(selected.nextQuoteDate)}`}
        />
      );
    } else if (applyAvailable != null) {
      preview = (
        <EstimateLine items={[
          { label: grossBinds ? 'Disponivel p/ aplicar' : 'Caixa disponivel', value: fmtBRL(applyAvailable), color: COLORS.leftover },
          reserved > 0 && { label: 'Reservado (ordens pendentes)', value: fmtBRL(reserved), color: COLORS.leftover },
        ]} />
      );
    }
  } else if (selected) {
    if (!(held > 0)) {
      blocked = true;
      preview = <EstimateWarning>O fundo nao tem cotas de {selected.fund.name} para resgatar</EstimateWarning>;
    } else if (redeemMode === 'quantity' && quantityValue > held + 1e-9) {
      blocked = true;
      preview = <EstimateWarning>Quantidade maior que as cotas do fundo ({fmtQty(held)}). Use Total para sair de tudo.</EstimateWarning>;
    } else if (redeemMode === 'value' && heldValue != null && amountValue > Math.round(heldValue * 100) / 100 + 0.005) {
      blocked = true;
      preview = <EstimateWarning>Valor maior que a posicao (vale {fmtBRL(heldValue)} na ultima cota). Use Total para sair de tudo.</EstimateWarning>;
    } else if (redeemQty > 0 && quota > 0) {
      preview = (
        <EstimateLine
          items={[
            { label: 'Cotas', value: `${redeemMode === 'value' ? '≈ ' : ''}${fmtQty(redeemQty)}${redeemMode === 'all' ? ' (todas)' : ''}`, color: COLORS.quantity },
            { label: 'Resgate estimado', value: fmtBRL(redeemQty * quota), color: sideColor('short') },
            { label: 'Cotas no fundo', value: fmtQty(held), color: COLORS.leftover },
          ]}
          note={`Estimativa na ultima cota publicada (${fmtDate(selected.fund.lastQuotaDate)}); o resgate sai pela cota de ${fmtDate(selected.nextQuoteDate)}`}
        />
      );
    } else {
      preview = <EstimateLine items={[{ label: 'Cotas no fundo', value: `${fmtQty(held)} = ${fmtBRL(heldValue)}`, color: COLORS.leftover }]} />;
    }
  }

  const submit = async () => {
    setError(null);
    setSuccess(null);
    if (!fundId) return setError('Selecione o fundo da carteira');
    if (!selected) return setError('Escolha o fundo pela busca');

    const payload = { fundId: parseInt(fundId), ticker: selected.fund.ticker, side, thesis: thesis.trim() || null };
    if (isApplication) {
      if (!(amountValue > 0)) return setError('Informe o valor da aplicacao');
      payload.amount = amountValue;
    } else {
      if (redeemMode === 'all') payload.redeemAll = true;
      else if (redeemMode === 'quantity') {
        if (!(quantityValue > 0)) return setError('Informe a quantidade de cotas');
        payload.quantity = quantityValue;
      } else {
        if (!(amountValue > 0)) return setError('Informe o valor do resgate');
        payload.amount = amountValue;
      }
      if (!payload.thesis) return setError('Informe a justificativa do resgate');
    }
    if (blocked) return undefined;

    setLoading(true);
    try {
      const order = await post('/fund-orders', payload);
      setSuccess(describeOrder(order));
      setAmount('');
      setQuantity('');
      setThesis('');
      setLimitsVersion((v) => v + 1);
      onSubmit?.();
    } catch (e) {
      setError(e.message);
    }
    setLoading(false);
    return undefined;
  };

  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: 12 }}>
      {isLeader && (
        <div>
          <label style={labelStyle}>Fundo da carteira</label>
          <select style={inputStyle} value={portfolioId} onChange={(e) => setPortfolioId(e.target.value)}>
            <option value="">Selecione</option>
            {funds.map((f) => (
              <option key={f.id} value={f.id}>{f.name}</option>
            ))}
          </select>
        </div>
      )}
      <div style={{ gridColumn: 'span 2', minWidth: 0 }}>
        <label style={labelStyle}>Fundo investido (nome ou CNPJ)</label>
        <input style={inputStyle} placeholder="Ex.: Kinea, crédito privado, 26.324.289" value={query}
          onChange={(e) => setQuery(e.target.value)} />
        {searching && <div style={{ fontSize: 11, color: 'var(--text-dim)', marginTop: 4 }}>Buscando na CVM...</div>}
        {searchError && <div style={{ fontSize: 11, color: 'var(--red)', marginTop: 4 }}>{searchError}</div>}
        {results && !searching && results.length === 0 && (
          <div style={{ fontSize: 11, color: 'var(--text-dim)', marginTop: 4 }}>Nenhum fundo aberto a aplicacao encontrado</div>
        )}
        {results && results.length > 0 && (
          <div style={{
            marginTop: 4, maxHeight: 280, overflowY: 'auto', border: '1px solid var(--border)', borderRadius: 4,
            background: 'var(--surface)',
          }}>
            {results.map((r) => (
              <button key={r.ticker} type="button" onClick={() => selectFund(r)}
                style={{
                  display: 'block', width: '100%', textAlign: 'left', padding: '8px 10px', cursor: 'pointer',
                  background: 'transparent', border: 'none', borderBottom: '1px solid var(--border)', color: 'var(--text)',
                }}
                onMouseEnter={(e) => (e.currentTarget.style.background = 'var(--surface-alt)')}
                onMouseLeave={(e) => (e.currentTarget.style.background = 'transparent')}>
                <div style={{ fontSize: 12, fontWeight: 600 }}>{r.name}</div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: '2px 10px', fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}>
                  <span>{r.cnpj}</span>
                  {r.classification && <span>{r.classification}</span>}
                  {r.isFic && <span style={tagStyle('var(--accent)')}>FIC</span>}
                  {r.targetAudience && <span>{r.targetAudience}</span>}
                  <span>Cota {fmtQuota(r.lastQuota)} ({fmtDate(r.lastQuotaDate, false)})</span>
                  {r.equity != null && <span>PL {fmtBRL(r.equity)}</span>}
                </div>
              </button>
            ))}
          </div>
        )}
      </div>
      <div>
        <label style={labelStyle}>Operacao</label>
        <div style={{ display: 'flex', gap: 4 }}>
          {[['long', 'Aplicar'], ['short', 'Resgatar']].map(([s, label]) => (
            <button key={s} type="button" onClick={() => { setSide(s); setError(null); }}
              style={{
                flex: 1, padding: '8px 0', borderRadius: 4, cursor: 'pointer',
                fontSize: 12, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em',
                background: side === s ? (s === 'long' ? 'var(--green-dim)' : 'var(--red-dim)') : 'var(--surface-alt)',
                color: side === s ? (s === 'long' ? 'var(--green)' : 'var(--red)') : 'var(--text-dim)',
                border: `1px solid ${side === s ? (s === 'long' ? 'var(--green)' : 'var(--red)') : 'var(--border)'}`,
                transition: 'all 0.15s',
              }}>{label}</button>
          ))}
        </div>
      </div>
      <div>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 6 }}>
          <label style={labelStyle}>
            {isApplication || redeemMode === 'value' ? 'Valor (R$)' : (redeemMode === 'quantity' ? 'Cotas' : 'Resgate total')}
          </label>
          {!isApplication && (
            <div style={{ display: 'flex', gap: 2, marginBottom: 4 }}>
              {[['value', 'R$'], ['quantity', 'Cotas'], ['all', 'Total']].map(([key, label]) => (
                <button key={key} type="button" onClick={() => { setRedeemMode(key); setError(null); }}
                  style={toggleStyle(redeemMode === key)}>{label}</button>
              ))}
            </div>
          )}
        </div>
        {!isApplication && redeemMode === 'all' ? (
          <div style={{ ...inputStyle, minHeight: 34, color: 'var(--text-muted)', display: 'flex', alignItems: 'center' }}>
            {held > 0 ? `${fmtQty(held)} cotas` : 'Sem cotas'}
          </div>
        ) : !isApplication && redeemMode === 'quantity' ? (
          <input style={inputStyle} type="number" min="0" step="any" placeholder="100" value={quantity}
            onChange={(e) => setQuantity(e.target.value)} />
        ) : (
          <input style={inputStyle} type="number" min="0" step="any" placeholder="10000,00" value={amount}
            onChange={(e) => setAmount(e.target.value)} />
        )}
      </div>
      <div>
        <label style={labelStyle}>Gestor</label>
        <div style={{ ...inputStyle, minHeight: 34, color: 'var(--text-muted)', display: 'flex', alignItems: 'center' }}>
          {currentUser?.name || 'Usuario autenticado'}
        </div>
      </div>
      {infoLoading && <div style={{ gridColumn: '1 / -1' }}><EstimateNote>Carregando dados do fundo...</EstimateNote></div>}
      {selected && (
        <div style={{
          gridColumn: '1 / -1', display: 'flex', flexDirection: 'column', gap: 6,
          padding: '10px 12px', borderRadius: 4, border: '1px solid var(--border)', background: 'var(--surface-alt)',
          fontSize: 12, color: 'var(--text-muted)',
        }}>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 12px', alignItems: 'center' }}>
            <strong style={{ color: 'var(--text)' }}>{selected.fund.name}</strong>
            {selected.fund.isFic && <span style={tagStyle('var(--accent)')}>FIC</span>}
            <button type="button" onClick={() => { setSelected(null); setError(null); setSuccess(null); }}
              style={{ ...toggleStyle(false), marginLeft: 'auto' }}>Trocar fundo</button>
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '4px 24px' }}>
            <span>CNPJ {selected.fund.cnpj}</span>
            {(selected.fund.anbimaClass || selected.fund.classification) && <span>{selected.fund.anbimaClass || selected.fund.classification}</span>}
            {selected.fund.targetAudience && <span>Publico: {selected.fund.targetAudience}</span>}
            <span>Ultima cota: <strong style={{ color: 'var(--text)' }}>{fmtQuota(selected.fund.lastQuota)}</strong> em {fmtDate(selected.fund.lastQuotaDate)} (CVM)</span>
          </div>
          <div>
            Ordem dada agora entra na <strong style={{ color: 'var(--text)' }}>cota de {fmtDate(selected.nextQuoteDate)}</strong>
            {' '}(ordens ate {selected.cutoffHour}h de Brasilia entram na cota do dia; depois, na do proximo dia util) e e executada
            {' '}quando a CVM publicar essa cota, cerca de 2 dias uteis depois. O preco so e conhecido na execucao.
            {' '}Cancelamento pelo gestor ate {fmtBrasilia(selected.cancelUntil)}.
          </div>
        </div>
      )}
      {preview && <div style={{ gridColumn: '1 / -1' }}>{preview}</div>}
      <div style={{ gridColumn: '1 / -1' }}>
        <label style={labelStyle}>{isApplication ? 'Tese' : 'Justificativa (obrigatoria)'}</label>
        <input style={inputStyle} placeholder={isApplication ? 'Por que aplicar neste fundo...' : 'Por que esta resgatando...'}
          value={thesis} onChange={(e) => setThesis(e.target.value)} />
      </div>
      <div style={{ gridColumn: '1 / -1' }}>
        <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap' }}>
          <button type="button" onClick={submit} disabled={loading || blocked}
            style={{
              padding: '10px 24px', borderRadius: 4, border: 'none', cursor: loading ? 'wait' : (blocked ? 'not-allowed' : 'pointer'),
              background: 'var(--accent-solid)', color: '#fff', fontWeight: 700, fontSize: 13,
              textTransform: 'uppercase', letterSpacing: '0.05em',
              opacity: loading || blocked ? 0.45 : 1,
              transition: 'background 0.2s',
            }}>
            {loading ? 'Registrando...' : (isApplication ? 'Registrar aplicacao' : 'Registrar resgate')}
          </button>
          {error && <span style={{ color: 'var(--red)', fontSize: 12 }}>{error}</span>}
          {success && <span style={{ color: 'var(--green)', fontSize: 12 }}>{success}</span>}
        </div>
      </div>
    </div>
  );
}
