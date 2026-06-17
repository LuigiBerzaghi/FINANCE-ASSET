import { useState } from 'react';
import { post } from '../lib/api';

export default function CreateFundForm({ teams = [], onCreated }) {
  const [name, setName] = useState('');
  const [strategy, setStrategy] = useState('');
  const [teamId, setTeamId] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const handleCreate = async () => {
    if (!name.trim()) return;
    setLoading(true);
    setError(null);
    try {
      await post('/funds', {
        name: name.trim(),
        strategy: strategy.trim() || null,
        teamId: teamId ? parseInt(teamId) : null,
      });
      setName('');
      setStrategy('');
      setTeamId('');
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
      {teams.length > 0 && (
        <select style={{ ...inputStyle, width: 180 }} value={teamId} onChange={(e) => setTeamId(e.target.value)}>
          <option value="">Sem time</option>
          {teams.map((team) => (
            <option key={team.id} value={team.id}>{team.name}</option>
          ))}
        </select>
      )}
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
