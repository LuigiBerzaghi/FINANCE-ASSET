import { useState } from 'react';
import { post, setAuthToken } from '../lib/api';

export default function LoginForm({ onLogin }) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [leaderMode, setLeaderMode] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const inputStyle = {
    background: 'var(--surface-alt)',
    border: '1px solid var(--border)',
    borderRadius: 4,
    color: 'var(--text)',
    padding: '10px 12px',
    fontSize: 13,
    outline: 'none',
    width: '100%',
  };

  const labelStyle = {
    fontSize: 10,
    color: 'var(--text-muted)',
    textTransform: 'uppercase',
    letterSpacing: '0.08em',
    marginBottom: 6,
    display: 'block',
  };

  const handleSubmit = async (event) => {
    event.preventDefault();
    setLoading(true);
    setError(null);

    try {
      const data = await post('/auth/login', leaderMode ? { email, password } : { email });
      setAuthToken(data.token);
      onLogin?.(data.user);
    } catch (e) {
      setError(e.message);
    }

    setLoading(false);
  };

  const toggleLeaderMode = () => {
    setLeaderMode((current) => !current);
    setPassword('');
    setError(null);
  };

  return (
    <div style={{
      minHeight: '100vh',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      background: 'var(--bg)',
      color: 'var(--text)',
      padding: 24,
    }}>
      <form onSubmit={handleSubmit} style={{
        width: '100%',
        maxWidth: 380,
        background: 'var(--surface)',
        border: '1px solid var(--border)',
        borderRadius: 8,
        padding: 24,
        display: 'flex',
        flexDirection: 'column',
        gap: 16,
      }}>
        <div>
          <div style={{ fontSize: 16, fontWeight: 800, color: 'var(--accent)', marginBottom: 4 }}>
            PUC FINANCE
          </div>
          <div style={{ color: 'var(--text-muted)', fontSize: 11 }}>ASSET MANAGEMENT</div>
        </div>

        {leaderMode && (
          <div style={{ color: 'var(--accent)', fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.08em' }}>
            Admin
          </div>
        )}

        <div>
          <label style={labelStyle}>Login</label>
          <input
            style={inputStyle}
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="nome@pucfinance"
            autoComplete="username"
            autoCapitalize="none"
          />
        </div>

        {leaderMode && (
          <div>
            <label style={labelStyle}>Senha</label>
            <input
              style={inputStyle}
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="current-password"
            />
          </div>
        )}

        <button type="submit" disabled={loading} style={{
          padding: '10px 16px',
          borderRadius: 4,
          border: 'none',
          background: 'var(--accent-solid)',
          color: '#fff',
          cursor: loading ? 'wait' : 'pointer',
          fontSize: 13,
          fontWeight: 700,
          opacity: loading ? 0.7 : 1,
        }}>
          {loading ? 'Entrando...' : 'Entrar'}
        </button>

        <button type="button" onClick={toggleLeaderMode} style={{
          padding: '8px 16px',
          borderRadius: 4,
          border: '1px solid var(--border)',
          background: 'transparent',
          color: 'var(--text-muted)',
          cursor: 'pointer',
          fontSize: 12,
        }}>
          {leaderMode ? 'Voltar para login de gestor' : 'Admin'}
        </button>

        {error && <div style={{ color: 'var(--red)', fontSize: 12 }}>{error}</div>}
      </form>
    </div>
  );
}
