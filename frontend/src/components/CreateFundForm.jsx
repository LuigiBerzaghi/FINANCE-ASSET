import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { get, post } from '../lib/api';

export default function CreateFundForm({ onCreated }) {
  const [name, setName] = useState('');
  const [strategy, setStrategy] = useState('');
  const [benchmark, setBenchmark] = useState('IBOVESPA');
  const [managers, setManagers] = useState([]);
  const [selected, setSelected] = useState([]);
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const pickerRef = useRef(null);
  const panelRef = useRef(null);
  const [panelPos, setPanelPos] = useState(null);

  useEffect(() => {
    get('/managers').then(setManagers).catch(() => setManagers([]));
  }, []);

  // A lista e desenhada fora do card (que corta o que passa da borda), posicionada sob o botao
  useLayoutEffect(() => {
    if (!open) return undefined;
    const place = () => {
      const r = pickerRef.current?.getBoundingClientRect();
      if (r) setPanelPos({ top: r.bottom + 4, left: r.left, width: Math.max(r.width, 280) });
    };
    place();
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
    return () => {
      window.removeEventListener('scroll', place, true);
      window.removeEventListener('resize', place);
    };
  }, [open]);

  // Fecha a lista ao clicar fora dela (fora do botao e da propria lista)
  useEffect(() => {
    if (!open) return undefined;
    const close = (e) => {
      if (!pickerRef.current?.contains(e.target) && !panelRef.current?.contains(e.target)) setOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [open]);

  const toggle = (id) =>
    setSelected((current) => (current.includes(id) ? current.filter((x) => x !== id) : [...current, id]));

  const selectedNames = managers.filter((m) => selected.includes(m.id)).map((m) => m.name);
  const pickerLabel = selectedNames.length === 0
    ? 'Gestores: nenhum'
    : `Gestores: ${selectedNames.length <= 2 ? selectedNames.join(', ') : `${selectedNames.length} selecionados`}`;

  const handleCreate = async () => {
    if (!name.trim()) return;
    setLoading(true);
    setError(null);
    try {
      await post('/funds', {
        name: name.trim(),
        strategy: strategy.trim() || null,
        benchmark,
        managerIds: selected,
      });
      setName('');
      setStrategy('');
      setBenchmark('IBOVESPA');
      setSelected([]);
      setOpen(false);
      onCreated?.();
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
    transition: 'background 0.2s, border-color 0.2s, color 0.2s',
  };

  return (
    <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
      <input style={{ ...inputStyle, width: 160 }} placeholder="Nome do fundo" value={name} onChange={(e) => setName(e.target.value)} />
      <input style={{ ...inputStyle, width: 160 }} placeholder="Estrategia" value={strategy} onChange={(e) => setStrategy(e.target.value)} />

      <div ref={pickerRef} style={{ position: 'relative' }}>
        <button type="button" onClick={() => setOpen((o) => !o)}
          title="Gestores que vao ver e operar o fundo"
          style={{ ...inputStyle, width: 220, textAlign: 'left', cursor: 'pointer', display: 'flex', justifyContent: 'space-between', gap: 8 }}>
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{pickerLabel}</span>
          <span style={{ color: 'var(--text-dim)' }}>▾</span>
        </button>
        {open && panelPos && createPortal(
          <div ref={panelRef} style={{
            position: 'fixed', top: panelPos.top, left: panelPos.left, width: panelPos.width, zIndex: 1000,
            maxHeight: 280, overflowY: 'auto',
            background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 6, padding: 6,
            boxShadow: '0 8px 24px rgba(0,0,0,0.35)',
          }}>
            {managers.length === 0 && (
              <div style={{ color: 'var(--text-muted)', fontSize: 12, padding: 8 }}>Nenhum gestor cadastrado</div>
            )}
            {managers.map((m) => (
              <label key={m.id} style={{
                display: 'flex', alignItems: 'center', gap: 8, padding: '6px 8px', borderRadius: 4, cursor: 'pointer',
                fontSize: 13, color: 'var(--text)', background: selected.includes(m.id) ? 'var(--accent-dim)' : 'transparent',
              }}>
                <input type="checkbox" checked={selected.includes(m.id)} onChange={() => toggle(m.id)} />
                <span>{m.name}</span>
                <span style={{ color: 'var(--text-dim)', fontSize: 11, marginLeft: 'auto' }}>{m.email}</span>
              </label>
            ))}
          </div>,
          document.body,
        )}
      </div>

      <select style={{ ...inputStyle, width: 200 }} value={benchmark} onChange={(e) => setBenchmark(e.target.value)}
        title="Referencia usada no Alpha/Beta das metricas">
        <option value="IBOVESPA">Referencia: IBOVESPA (acoes)</option>
        <option value="CDI">Referencia: CDI (multimercado / renda fixa)</option>
      </select>
      <button onClick={handleCreate} disabled={loading}
        style={{
          padding: '8px 16px', borderRadius: 4, border: '1px solid var(--accent)',
          background: 'transparent', color: 'var(--accent)', cursor: 'pointer', fontSize: 12,
          fontWeight: 600, transition: 'all 0.2s',
        }}>
        + Criar Fundo
      </button>
      {error && <span style={{ color: 'var(--red)', fontSize: 11 }}>{error}</span>}
    </div>
  );
}
