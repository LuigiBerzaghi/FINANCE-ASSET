export const fmtBRL = (v) =>
  v != null ? v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' }) : '—';

export const fmtMoney = (v, currency = 'BRL') =>
  v != null ? v.toLocaleString('pt-BR', { style: 'currency', currency }) : '—';

export const fmtPct = (v) =>
  v != null ? `${(v * 100).toFixed(2)}%` : '—';

export const fmtNum = (v, d = 4) =>
  v != null ? v.toFixed(d) : '—';

// Taxa de titulo publico no formato do Tesouro Direto (ex.: IPCA + 7,56%, 13,55% a.a.)
const RATE_INDEX = { NTNB: 'IPCA', NTNBP: 'IPCA', EDUCA: 'IPCA', RENDA: 'IPCA', NTNC: 'IGP-M', LFT: 'Selic' };
export const fmtRate = (code, rate) => {
  if (rate == null) return '—';
  const pct = `${rate.toLocaleString('pt-BR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}%`;
  return RATE_INDEX[code] ? `${RATE_INDEX[code]} + ${pct}` : `${pct} a.a.`;
};

export const fmtQty = (v) =>
  v != null ? v.toLocaleString('pt-BR') : '—';
