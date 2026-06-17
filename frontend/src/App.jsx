import { useState, useEffect, useCallback } from 'react';
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid } from 'recharts';
import { clearAuthToken, download, get, getAuthToken, post } from './lib/api';
import { fmtBRL, fmtPct, fmtNum } from './lib/format';
import { useTheme } from './lib/useTheme';
import Section from './components/Section';
import Stat from './components/Stat';
import FundTabs from './components/FundTabs';
import PositionsTable from './components/PositionsTable';
import TradesTable from './components/TradesTable';
import NavChart from './components/NavChart';
import ShareChart from './components/ShareChart';
import MetricsPanel from './components/MetricsPanel';
import TradeForm from './components/TradeForm';
import CreateFundForm from './components/CreateFundForm';
import ThemeToggle from './components/ThemeToggle';
import ExposureChart from './components/ExposureChart';
import CdiChart from './components/CdiChart';
import ReturnByClass from './components/ReturnByClass';
import LoginForm from './components/LoginForm';

export default function App() {
  const { theme, toggle: toggleTheme } = useTheme();
  const [authReady, setAuthReady] = useState(false);
  const [authUser, setAuthUser] = useState(null);
  const [funds, setFunds] = useState([]);
  const [teams, setTeams] = useState([]);
  const [activeFund, setActiveFund] = useState(null);
  const [positions, setPositions] = useState([]);
  const [navData, setNavData] = useState([]);
  const [trades, setTrades] = useState([]);
  const [metrics, setMetrics] = useState([]);
  const [batchStatus, setBatchStatus] = useState(null);
  const [batchLoading, setBatchLoading] = useState(false);
  const [view, setView] = useState('dashboard');
  const [exposure, setExposure] = useState(null);
  const [cdiData, setCdiData] = useState(null);
  const [returnByClass, setReturnByClass] = useState([]);

  const loadFunds = useCallback(async () => {
    try {
      const data = await get('/funds');
      setFunds(data);
      setActiveFund((current) => (
        data.some((fund) => fund.id === current) ? current : (data[0]?.id ?? null)
      ));
    } catch (e) {
      console.error('Erro ao carregar fundos:', e);
    }
  }, []);

  const loadFundData = useCallback(async () => {
    if (!activeFund) return;
    try {
      const [pos, nav, trd, met, exp, cdi, rbc] = await Promise.all([
        get(`/funds/${activeFund}/positions`),
        get(`/funds/${activeFund}/nav`),
        get(`/trades/fund/${activeFund}`),
        get(`/funds/${activeFund}/metrics`).catch(() => []),
        get(`/funds/${activeFund}/exposure`).catch(() => null),
        get(`/funds/${activeFund}/cdi-comparison`).catch(() => null),
        get(`/funds/${activeFund}/return-by-class`).catch(() => []),
      ]);
      setPositions(pos);
      setNavData(nav);
      setTrades(trd);
      setMetrics(met);
      setExposure(exp);
      setCdiData(cdi);
      setReturnByClass(rbc);
    } catch (e) {
      console.error('Erro ao carregar dados do fundo:', e);
    }
  }, [activeFund]);

  useEffect(() => {
    let cancelled = false;

    const restoreSession = async () => {
      if (!getAuthToken()) {
        setAuthReady(true);
        return;
      }

      try {
        const user = await get('/auth/me');
        if (!cancelled) setAuthUser(user);
      } catch {
        clearAuthToken();
      } finally {
        if (!cancelled) setAuthReady(true);
      }
    };

    restoreSession();
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (!authUser) {
      setFunds([]);
      setActiveFund(null);
      return;
    }

    loadFunds();
  }, [authUser, loadFunds]);

  useEffect(() => {
    if (authUser?.role !== 'leader') {
      setTeams([]);
      if (view === 'funds') setView('dashboard');
      return;
    }

    get('/teams').then(setTeams).catch(() => setTeams([]));
  }, [authUser, view]);

  useEffect(() => { loadFundData(); }, [activeFund, loadFundData]);

  const runBatch = async () => {
    setBatchLoading(true);
    setBatchStatus(null);
    try {
      const result = await post('/batch/run', {});
      setBatchStatus(result);
      await loadFunds();
      await loadFundData();
    } catch (e) {
      setBatchStatus({ status: `error: ${e.message}` });
    }
    setBatchLoading(false);
  };

  const exportExcel = () => {
    if (!activeFund) return;
    download(`/funds/${activeFund}/export`)
      .then((blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        const fundName = currentFund?.name || 'fundo';
        link.href = url;
        link.download = `PUCFinance_${fundName}_${new Date().toISOString().slice(0, 10)}.xlsx`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
      })
      .catch((e) => setBatchStatus({ status: `error: ${e.message}` }));
  };

  const logout = () => {
    clearAuthToken();
    setAuthUser(null);
    setTeams([]);
    setFunds([]);
    setActiveFund(null);
    setView('dashboard');
  };

  if (!authReady) {
    return (
      <div style={{
        minHeight: '100vh',
        background: 'var(--bg)',
        color: 'var(--text-muted)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        fontSize: 13,
      }}>
        Carregando...
      </div>
    );
  }

  if (!authUser) {
    return <LoginForm onLogin={setAuthUser} />;
  }

  const isLeader = authUser.role === 'leader';
  const currentFund = funds.find((f) => f.id === activeFund);

  const allocationData = positions
    .filter((p) => p.weight != null && p.weight > 0)
    .map((p) => ({ name: p.ticker, value: Math.abs(p.weight * 100) }))
    .sort((a, b) => b.value - a.value);

  const cashWeight = currentFund ? (currentFund.cashBalance / currentFund.totalEquity) * 100 : 0;
  if (cashWeight > 0) allocationData.push({ name: 'Caixa', value: cashWeight });

  return (
    <div style={{ minHeight: '100vh', background: 'var(--bg)', color: 'var(--text)' }}>
      {/* Header */}
      <div style={{
        padding: '16px 24px',
        borderBottom: '1px solid var(--border)',
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'center',
        background: 'var(--surface)',
        transition: 'background 0.2s, border-color 0.2s',
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
          <div style={{ fontSize: 16, fontWeight: 800, letterSpacing: '-0.03em', color: 'var(--accent)' }}>
            PUC FINANCE
          </div>
          <span style={{ color: 'var(--text-dim)', fontSize: 11 }}>ASSET MANAGEMENT</span>
        </div>
        <div style={{ display: 'flex', gap: 4, alignItems: 'center' }}>
          {(isLeader ? ['dashboard', 'trade', 'funds'] : ['dashboard', 'trade']).map((v) => (
            <button key={v} onClick={() => setView(v)}
              style={{
                padding: '6px 14px', borderRadius: 4, border: 'none', cursor: 'pointer',
                fontSize: 12,
                background: view === v ? 'var(--accent-dim)' : 'transparent',
                color: view === v ? 'var(--accent)' : 'var(--text-muted)',
                fontWeight: view === v ? 600 : 400,
                textTransform: 'capitalize',
              }}>{v === 'funds' ? 'Fundos' : v === 'trade' ? 'Trade' : 'Dashboard'}</button>
          ))}
          <div style={{ width: 1, height: 20, background: 'var(--border)', margin: '0 8px' }} />
          {isLeader && (
            <button onClick={runBatch} disabled={batchLoading}
              style={{
                padding: '6px 14px', borderRadius: 4, border: '1px solid var(--border)',
                background: 'transparent',
                color: batchLoading ? 'var(--text-dim)' : 'var(--yellow)',
                cursor: batchLoading ? 'wait' : 'pointer',
                fontSize: 12, fontWeight: 600,
              }}>
              {batchLoading ? 'Atualizando...' : 'Run Batch'}
            </button>
          )}
          {isLeader && activeFund && (
            <button onClick={exportExcel}
              style={{
                padding: '6px 14px', borderRadius: 4, border: '1px solid var(--border)',
                background: 'transparent',
                color: 'var(--green)',
                cursor: 'pointer',
                fontSize: 12, fontWeight: 600,
              }}>
              Export Excel
            </button>
          )}
          <ThemeToggle theme={theme} onToggle={toggleTheme} />
          <div style={{ color: 'var(--text-dim)', fontSize: 11, marginLeft: 8 }}>
            {authUser.name}{authUser.teamName ? ` | ${authUser.teamName}` : ' | Lider'}
          </div>
          <button onClick={logout}
            style={{
              padding: '6px 10px', borderRadius: 4, border: '1px solid var(--border)',
              background: 'transparent', color: 'var(--text-muted)', cursor: 'pointer',
              fontSize: 12,
            }}>
            Sair
          </button>
        </div>
      </div>

      {/* Batch Status */}
      {batchStatus && (
        <div style={{
          padding: '8px 24px', fontSize: 11,
          background: batchStatus.status === 'success' ? 'var(--green-dim)' : 'var(--red-dim)',
          color: batchStatus.status === 'success' ? 'var(--green)' : 'var(--red)',
          borderBottom: '1px solid var(--border)',
        }}>
          Batch: {batchStatus.status} | Precos: {batchStatus.pricesFetched} | {batchStatus.timestamp}
        </div>
      )}

      <div style={{ padding: 24, maxWidth: 1400, margin: '0 auto' }}>
        {funds.length > 0 && (
          <div style={{ marginBottom: 20 }}>
            <FundTabs funds={funds} active={activeFund} onChange={setActiveFund} />
          </div>
        )}

        {funds.length === 0 && (
          <Section title="Nenhum fundo criado">
            {isLeader ? (
              <>
                <p style={{ color: 'var(--text-muted)', marginBottom: 16, fontSize: 13 }}>
                  Crie o primeiro fundo para comecar a operar.
                </p>
                <CreateFundForm teams={teams} onCreated={loadFunds} />
              </>
            ) : (
              <p style={{ color: 'var(--text-muted)', fontSize: 13 }}>
                Nenhum fundo esta vinculado ao seu time.
              </p>
            )}
          </Section>
        )}

        {/* Dashboard */}
        {view === 'dashboard' && currentFund && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
              <Stat label="Patrimonio" value={fmtBRL(currentFund.totalEquity)} />
              <Stat label="Valor da Cota" value={fmtNum(currentFund.shareValue)}
                color={currentFund.shareValue >= 1 ? 'var(--green)' : 'var(--red)'} />
              <Stat label="Retorno Dia" value={fmtPct(currentFund.dailyReturn)}
                color={currentFund.dailyReturn >= 0 ? 'var(--green)' : 'var(--red)'} />
              <Stat label="Caixa" value={fmtBRL(currentFund.cashBalance)} />
              <Stat label="Posicoes" value={currentFund.positionCount} color="var(--accent)" />
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr', gap: 20 }}>
              <Section title="Patrimonio (NAV)">
                <NavChart navData={navData} />
              </Section>
              <Section title="Fundo vs CDI">
                <CdiChart cdiData={cdiData} />
              </Section>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 20 }}>
              <Section title="Exposicao">
                <ExposureChart exposure={exposure} />
              </Section>
              <Section title="Retorno por Classe">
                <ReturnByClass data={returnByClass} />
              </Section>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '3fr 2fr', gap: 20 }}>
              <Section title="Posicoes Abertas">
                <PositionsTable positions={positions} />
              </Section>
              <Section title="Metricas">
                <MetricsPanel metrics={metrics} />
              </Section>
            </div>

            {allocationData.length > 0 && (
              <Section title="Alocacao">
                <ResponsiveContainer width="100%" height={200}>
                  <BarChart data={allocationData} layout="vertical" margin={{ left: 50 }}>
                    <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" horizontal={false} />
                    <XAxis type="number" tick={{ fill: 'var(--text-dim)', fontSize: 10 }} tickFormatter={(v) => `${v.toFixed(1)}%`} />
                    <YAxis type="category" dataKey="name" tick={{ fill: 'var(--text)', fontSize: 12 }} width={60} />
                    <Tooltip
                      contentStyle={{ background: 'var(--surface)', border: '1px solid var(--border)', borderRadius: 6 }}
                      formatter={(v) => [`${v.toFixed(2)}%`, 'Peso']}
                    />
                    <Bar dataKey="value" fill="var(--bar-fill)" radius={[0, 4, 4, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </Section>
            )}

            <Section title="Historico de Trades">
              <TradesTable trades={trades} onDelete={() => { loadFunds(); loadFundData(); }} />
            </Section>
          </div>
        )}

        {/* Trade */}
        {view === 'trade' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
            <Section title="Executar Trade">
              <TradeForm funds={funds} activeFund={activeFund} currentUser={authUser} onSubmit={() => { loadFunds(); loadFundData(); }} />
            </Section>
            <Section title="Trades Recentes">
              <TradesTable trades={trades} onDelete={() => { loadFunds(); loadFundData(); }} />
            </Section>
          </div>
        )}

        {/* Funds */}
        {view === 'funds' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
            <Section title="Criar Novo Fundo">
              <CreateFundForm teams={teams} onCreated={loadFunds} />
            </Section>
            <Section title="Todos os Fundos">
              {funds.length === 0 ? (
                <div style={{ color: 'var(--text-muted)' }}>Nenhum fundo criado</div>
              ) : (
                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))', gap: 16 }}>
                  {funds.map((f) => (
                    <div key={f.id}
                      onClick={() => { setActiveFund(f.id); setView('dashboard'); }}
                      style={{
                        padding: 20, background: 'var(--surface-alt)',
                        border: `1px solid ${activeFund === f.id ? 'var(--accent)' : 'var(--border)'}`,
                        borderRadius: 8, cursor: 'pointer', transition: 'border-color 0.2s, background 0.2s',
                      }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 12 }}>
                        <span style={{ fontSize: 16, fontWeight: 700 }}>{f.name}</span>
                        {f.strategy && (
                          <span style={{
                            fontSize: 10, padding: '2px 8px', borderRadius: 3,
                            background: 'var(--accent-dim)', color: 'var(--accent)', fontWeight: 600,
                          }}>{f.strategy}</span>
                        )}
                      </div>
                      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8, fontSize: 12 }}>
                        <div>
                          <div style={{ color: 'var(--text-dim)', fontSize: 10, textTransform: 'uppercase' }}>Patrimonio</div>
                          <div style={{ marginTop: 2 }}>{fmtBRL(f.totalEquity)}</div>
                        </div>
                        <div>
                          <div style={{ color: 'var(--text-dim)', fontSize: 10, textTransform: 'uppercase' }}>Cota</div>
                          <div style={{ marginTop: 2, color: f.shareValue >= 1 ? 'var(--green)' : 'var(--red)' }}>{fmtNum(f.shareValue)}</div>
                        </div>
                        <div>
                          <div style={{ color: 'var(--text-dim)', fontSize: 10, textTransform: 'uppercase' }}>Caixa</div>
                          <div style={{ marginTop: 2 }}>{fmtBRL(f.cashBalance)}</div>
                        </div>
                        <div>
                          <div style={{ color: 'var(--text-dim)', fontSize: 10, textTransform: 'uppercase' }}>Posicoes</div>
                          <div style={{ marginTop: 2, color: 'var(--accent)' }}>{f.positionCount}</div>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </Section>
          </div>
        )}

        {/* Footer */}
        <div style={{
          marginTop: 40, padding: '16px 0', borderTop: '1px solid var(--border)',
          display: 'flex', justifyContent: 'space-between', alignItems: 'center',
        }}>
          <span style={{ fontSize: 10, color: 'var(--text-dim)' }}>PUC Finance — Celula de Asset Management</span>
          <span style={{ fontSize: 10, color: 'var(--text-dim)' }}>Precos: Yahoo Finance (delay 15min)</span>
        </div>
      </div>
    </div>
  );
}
