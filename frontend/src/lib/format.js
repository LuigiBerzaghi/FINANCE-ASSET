export const fmtBRL = (v) =>
  v != null ? v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' }) : '—';

export const fmtPct = (v) =>
  v != null ? `${(v * 100).toFixed(2)}%` : '—';

export const fmtNum = (v, d = 4) =>
  v != null ? v.toFixed(d) : '—';

export const fmtQty = (v) =>
  v != null ? v.toLocaleString('pt-BR') : '—';
