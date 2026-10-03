import { useState } from 'react';
import { get, post } from '../lib/api';
import { fmtBRL, fmtPct, fmtQty, parseDecimal, quantityForAmount } from '../lib/format';
import { COLORS, EstimateLine, EstimateWarning, sideColor } from './Estimate';

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

const TREASURY_TICKER = /^[A-Z]+-[A-Z]{3}\d{4}$/;

const shortcutStyle = (disabled) => ({
  padding: '0 10px', borderRadius: 4, border: '1px solid var(--border)', background: 'transparent',
  color: 'var(--text)', fontSize: 11, cursor: 'pointer', whiteSpace: 'nowrap', opacity: disabled ? 0.4 : 1,
});

// Fechamento total ou parcial de uma posicao: o lado vem da posicao (vende o comprado, recompra o vendido)
// e a quantidade nao passa da posicao, entao fechar nunca inverte. Justificativa obrigatoria.
// Por valor, o servidor calcula a quantidade no preco da execucao (arredondada para baixo).
function ClosePanel({ position, fundId, onCancel, onDone }) {
  const held = Math.abs(position.quantity);
  const isLong = position.quantity > 0;
  const isTreasury = TREASURY_TICKER.test(position.ticker);
  const [inputMode, setInputMode] = useState('quantity');
  const [quantity, setQuantity] = useState(String(held));
  const [amount, setAmount] = useState('');
  // Preco de referencia para a estimativa por valor (titulo: PU de venda) e menor quantidade negociavel
  const [market, setMarket] = useState({ price: position.currentPrice, step: isTreasury ? 0.01 : 1 });
  const [thesis, setThesis] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const byValue = inputMode === 'value';
  const amountValue = parseDecimal(amount) || 0;
  const positionValue = market.price > 0 ? held * market.price : null;
  // Por valor: o valor da posicao inteira (em centavos, como aparece na tela) zera a posicao (mesma regra do servidor)
  const coversAll = byValue && positionValue != null && amountValue >= Math.round(positionValue * 100) / 100 - 0.005;
  const fromAmount = byValue ? quantityForAmount(amountValue, market.price, market.step) : 0;
  const qty = byValue ? (coversAll && fromAmount < held ? held : fromAmount) : parseDecimal(quantity);
  const isTotal = qty >= held - 1e-9;
  // Metade arredondada para baixo: inteiro para acoes; centesimos para titulos publicos e cripto
  const half = Number.isInteger(held) ? Math.floor(held / 2) : Math.floor((held / 2) * 100) / 100;
  const pick = (value) => { setInputMode('quantity'); setQuantity(String(value)); setError(null); };
  const estimated = !byValue && position.currentPrice != null && qty > 0 ? qty * position.currentPrice : null;

  // Previa: quantidade (destaque), valor (cor do lado do fechamento: vender = vermelho, recomprar = verde),
  // sobra (cinza) e avisos (amarelo)
  const closeSide = isLong ? 'short' : 'long';
  const unitShort = isTreasury ? 'titulo(s)' : 'un.';
  const actionLabel = isLong ? 'Valor da venda' : 'Valor da recompra';
  // Valor que nao pode ser executado (abaixo do minimo ou acima da posicao): aviso amarelo e botao bloqueado
  const blocked = byValue && amountValue > 0 && market.price > 0 && (qty < market.step || qty > held + 1e-9);
  let preview = null;
  if (!byValue && estimated != null) {
    preview = (
      <EstimateLine items={[
        { label: 'Quantidade', value: `${fmtQty(qty)} ${unitShort}`, color: COLORS.quantity },
        { label: `${actionLabel} estimado`, value: fmtBRL(estimated), color: sideColor(closeSide) },
      ]} />
    );
  } else if (byValue && !(amountValue > 0) && positionValue != null) {
    preview = (
      <EstimateLine items={[
        { label: 'Posicao inteira', value: `${fmtQty(held)} ${unitShort} = ${fmtBRL(positionValue)}`, color: sideColor(closeSide) },
      ]} />
    );
  } else if (byValue && amountValue > 0 && market.price > 0 && qty < market.step) {
    preview = (
      <EstimateWarning>
        Valor abaixo do minimo: {fmtBRL(market.step * market.price)} ({fmtQty(market.step)} {isTreasury ? 'titulo' : 'unidade'})
      </EstimateWarning>
    );
  } else if (byValue && amountValue > 0 && market.price > 0 && qty > held + 1e-9) {
    preview = (
      <EstimateWarning>
        Valor maior que a posicao (vale {fmtBRL(positionValue)} agora). Use Zerar para fechar tudo.
      </EstimateWarning>
    );
  } else if (byValue && amountValue > 0 && market.price > 0) {
    preview = (
      <EstimateLine
        items={[
          { label: 'Quantidade', value: `≈ ${fmtQty(qty)} ${unitShort}${isTotal ? ' (posicao inteira)' : ''}`, color: COLORS.quantity },
          { label: actionLabel, value: fmtBRL(qty * market.price), color: sideColor(closeSide) },
          { label: 'Sobra', value: fmtBRL(Math.max(0, amountValue - qty * market.price)), color: COLORS.leftover },
        ]}
        note="Quantidade final calculada no preco da execucao"
      />
    );
  }

  const switchToValue = () => {
    setInputMode('value');
    setError(null);
    get(`/prices/current/${position.ticker}`)
      .then((d) => setMarket({
        price: isTreasury ? d.sellPrice : d.price,
        step: d.quantityStep || (isTreasury ? 0.01 : 1),
      }))
      .catch(() => {});
  };

  const submit = async () => {
    setError(null);
    if (blocked) return;
    if (byValue) {
      if (!(amountValue > 0)) return setError('Informe o valor a fechar');
      if (market.price > 0 && qty < market.step) {
        return setError(`Valor abaixo do minimo: ${fmtBRL(market.step * market.price)} (${fmtQty(market.step)} ${isTreasury ? 'titulo' : 'unidade'})`);
      }
      if (market.price > 0 && qty > held + 1e-9) {
        return setError(`Valor maior que a posicao (vale ${fmtBRL(positionValue)} agora). Use Zerar para fechar tudo.`);
      }
    } else {
      if (!(qty > 0)) return setError('Informe a quantidade a fechar');
      if (qty > held + 1e-9) return setError(`A quantidade nao pode passar da posicao (${fmtQty(held)})`);
    }
    if (!thesis.trim()) return setError('Informe a justificativa do fechamento');

    setLoading(true);
    try {
      const trade = await post('/trades/close', {
        fundId: parseInt(fundId),
        ticker: position.ticker,
        quantity: byValue ? 0 : qty,
        amount: byValue ? amountValue : null,
        thesis: thesis.trim(),
      });
      const zeroed = trade.quantity >= held - 1e-9;
      onDone(
        `${zeroed ? 'Posicao zerada' : 'Posicao reduzida'}: ${isLong ? 'venda' : 'recompra'} de `
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
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 6 }}>
          <label style={labelStyle}>{byValue ? 'Valor (R$)' : `Quantidade (max. ${fmtQty(held)})`}</label>
          <div style={{ display: 'flex', gap: 2, marginBottom: 4 }}>
            {[['quantity', 'Qtd'], ['value', 'R$']].map(([key, label]) => (
              <button key={key} type="button" disabled={loading}
                onClick={() => (key === 'value' ? switchToValue() : (setInputMode('quantity'), setError(null)))}
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
        <div style={{ display: 'flex', gap: 6 }}>
          {byValue ? (
            <input style={inputStyle} type="number" min="0" step="any" placeholder="10000,00" value={amount}
              onChange={(e) => { setAmount(e.target.value); setError(null); }} />
          ) : (
            <input style={inputStyle} type="number" min="0" max={held} step="any" value={quantity}
              onChange={(e) => { setQuantity(e.target.value); setError(null); }} />
          )}
          <button type="button" onClick={() => pick(held)} disabled={loading} style={shortcutStyle(loading)}>
            Zerar
          </button>
          <button type="button" onClick={() => pick(half)} disabled={loading || !(half > 0)} style={shortcutStyle(!(half > 0))}>
            Metade
          </button>
        </div>
      </div>
      <div>
        <label style={labelStyle}>Justificativa (obrigatoria)</label>
        <input style={inputStyle} placeholder="Por que esta fechando a posicao..." value={thesis}
          onChange={(e) => { setThesis(e.target.value); setError(null); }} />
      </div>
      {preview && <div style={{ gridColumn: '1 / -1' }}>{preview}</div>}
      <div style={{ gridColumn: '1 / -1', display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
        <button onClick={submit} disabled={loading || blocked}
          style={{
            padding: '8px 18px', borderRadius: 4, border: 'none', cursor: loading ? 'wait' : (blocked ? 'not-allowed' : 'pointer'),
            background: 'var(--accent-solid)', color: '#fff', fontWeight: 700, fontSize: 12,
            textTransform: 'uppercase', letterSpacing: '0.05em', opacity: loading || blocked ? 0.45 : 1,
          }}>
          {loading ? 'Executando...' : (blocked ? 'Fechar' : (isTotal ? 'Zerar posicao' : 'Fechar parcialmente'))}
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
