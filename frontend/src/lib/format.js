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
  v != null ? v.toLocaleString('pt-BR', { maximumFractionDigits: 8 }) : '—';

// Maior quantidade (multiplo de step) cujo valor nao passa de amount; mesma regra do servidor
// (PricingService.QuantityForAmount). O servidor recalcula no preco da execucao.
export const quantityForAmount = (amount, price, step) => {
  if (!(amount > 0) || !(price > 0) || !(step > 0)) return 0;
  const units = Math.floor((amount / price / step) * (1 + 1e-12) + 1e-9);
  const decimals = step >= 1 ? 0 : Math.round(-Math.log10(step));
  return Number((units * step).toFixed(decimals));
};

export const parseDecimal = (v) => parseFloat(String(v ?? '').replace(',', '.'));

// Cota de fundo (CVM): FI- + CNPJ da classe, mais a subclasse quando houver (mesma regra do servidor)
export const isFundTicker = (ticker) => /^FI-\d{14}(-[A-Z0-9]{1,40})?$/.test(ticker || '');

// Valor de cota de fundo: ate 8 casas, como a CVM publica
export const fmtQuota = (v) =>
  v != null
    ? v.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL', minimumFractionDigits: 2, maximumFractionDigits: 8 })
    : '—';

// yyyy-MM-dd -> dd/MM/yyyy (ou dd/MM)
export const fmtDate = (iso, withYear = true) => {
  if (!iso) return '—';
  const [y, m, d] = iso.slice(0, 10).split('-');
  return withYear ? `${d}/${m}/${y}` : `${d}/${m}`;
};

// Horario de Brasilia enviado pelo servidor (ISO com fuso, ex.: 2026-10-13T14:00:00-03:00) -> 13/10 14h (Brasilia).
// O corte das ordens de fundos e no horario de Brasilia, qualquer que seja o fuso do navegador.
export const fmtBrasilia = (iso) => {
  const m = /^\d{4}-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(iso || '');
  if (!m) return '—';
  const [, month, day, hour, minute] = m;
  return `${day}/${month} ${hour}h${minute !== '00' ? minute : ''} (Brasilia)`;
};

// Timestamp do servidor em UTC (yyyy-MM-dd HH:mm:ss) ou ISO com fuso -> data e hora locais
export const fmtDateTime = (value) => {
  if (!value) return '—';
  const date = new Date(/[zZ]|[+-]\d{2}:\d{2}$/.test(value) ? value : `${value.replace(' ', 'T')}Z`);
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleString('pt-BR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
};
