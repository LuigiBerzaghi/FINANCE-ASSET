-- PUC Finance Asset Management - SQLite Schema
-- Atualizado: 2026-05-15

PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

-- ============================================================
-- FUNDOS
-- ============================================================
CREATE TABLE IF NOT EXISTS funds (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL UNIQUE,       -- "Fundo Alpha", "Fundo Beta"
    strategy        TEXT,                          -- "Long Only", "Long/Short", "Macro"
    initial_capital REAL    NOT NULL DEFAULT 1000000.00,
    total_shares    REAL    NOT NULL DEFAULT 1000000,  -- cotas — fixas
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    is_active       INTEGER NOT NULL DEFAULT 1
);

-- ============================================================
-- POSIÇÕES ATUAIS (snapshot — recalculado diariamente)
-- ============================================================
CREATE TABLE IF NOT EXISTS positions (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    fund_id         INTEGER NOT NULL REFERENCES funds(id),
    ticker          TEXT    NOT NULL,
    quantity        REAL    NOT NULL,              -- positivo = long, negativo = short
    avg_price       REAL    NOT NULL,              -- preço médio de entrada
    side            TEXT    NOT NULL CHECK(side IN ('long', 'short')),
    current_price   REAL,                          -- atualizado no batch diário
    market_value    REAL,                          -- quantity * current_price
    unrealized_pnl  REAL,                          -- (current_price - avg_price) * quantity
    updated_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    UNIQUE(fund_id, ticker)
);

-- ============================================================
-- TRADES (log imutável — nunca deleta, só insere)
-- ============================================================
CREATE TABLE IF NOT EXISTS trades (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    fund_id         INTEGER NOT NULL REFERENCES funds(id),
    ticker          TEXT    NOT NULL,
    side            TEXT    NOT NULL CHECK(side IN ('long', 'short')),
    quantity        REAL    NOT NULL,              -- sempre positivo
    price           REAL    NOT NULL,              -- preço de execução
    thesis          TEXT,                          -- justificativa do trade
    executed_at     TEXT    NOT NULL DEFAULT (datetime('now')),
    executed_by     TEXT                           -- nome do gestor
);

-- ============================================================
-- CAIXA (um registro por fundo — atualizado a cada trade/batch)
-- ============================================================
CREATE TABLE IF NOT EXISTS cash (
    fund_id         INTEGER PRIMARY KEY REFERENCES funds(id),
    balance         REAL    NOT NULL DEFAULT 1000000.00,
    updated_at      TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- ============================================================
-- HISTÓRICO DE NAV (um registro por fundo por dia)
-- ============================================================
CREATE TABLE IF NOT EXISTS nav_history (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    fund_id         INTEGER NOT NULL REFERENCES funds(id),
    date            TEXT    NOT NULL,              -- YYYY-MM-DD
    total_equity    REAL    NOT NULL,              -- patrimônio = market_value + caixa
    total_shares    REAL    NOT NULL,              -- cotas
    share_value     REAL    NOT NULL,              -- valor da cota
    daily_return    REAL,                          -- retorno % no dia
    cash_balance    REAL    NOT NULL,
    created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    UNIQUE(fund_id, date)
);

-- ============================================================
-- PREÇOS HISTÓRICOS (cache local do Yahoo Finance)
-- ============================================================
CREATE TABLE IF NOT EXISTS prices (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ticker          TEXT    NOT NULL,
    date            TEXT    NOT NULL,              -- YYYY-MM-DD
    open            REAL,
    high            REAL,
    low             REAL,
    close           REAL    NOT NULL,              -- preço de fechamento (usado no NAV)
    adj_close       REAL,
    volume          INTEGER,
    source          TEXT    NOT NULL DEFAULT 'yahoo',
    fetched_at      TEXT    NOT NULL DEFAULT (datetime('now')),
    UNIQUE(ticker, date)
);

-- ============================================================
-- BENCHMARKS (Ibovespa, CDI, etc.)
-- ============================================================
CREATE TABLE IF NOT EXISTS benchmarks (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    name            TEXT    NOT NULL,              -- "IBOVESPA", "CDI"
    date            TEXT    NOT NULL,              -- YYYY-MM-DD
    value           REAL    NOT NULL,              -- pontos (IBOV) ou taxa (CDI)
    daily_return    REAL,                          -- retorno % no dia
    cumulative      REAL,                          -- retorno acumulado desde início
    source          TEXT,
    UNIQUE(name, date)
);

-- ============================================================
-- MÉTRICAS (calculadas por período por fundo)
-- ============================================================
CREATE TABLE IF NOT EXISTS metrics (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    fund_id             INTEGER NOT NULL REFERENCES funds(id),
    date                TEXT    NOT NULL,          -- data do cálculo
    period              TEXT    NOT NULL,           -- "daily", "mtd", "ytd", "inception"
    cumulative_return   REAL,
    annualized_return   REAL,
    volatility          REAL,                      -- desvio padrão anualizado
    sharpe_ratio        REAL,                      -- (retorno - rf) / vol
    max_drawdown        REAL,                      -- maior queda pico-vale
    alpha               REAL,                      -- vs benchmark
    beta                REAL,                      -- vs benchmark
    benchmark_name      TEXT,                      -- qual benchmark usado
    created_at          TEXT    NOT NULL DEFAULT (datetime('now')),
    UNIQUE(fund_id, date, period)
);

-- ============================================================
-- REALIZED P&L (registra lucro/prejuízo ao fechar posição)
-- ============================================================
CREATE TABLE IF NOT EXISTS realized_pnl (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    fund_id         INTEGER NOT NULL REFERENCES funds(id),
    ticker          TEXT    NOT NULL,
    quantity        REAL    NOT NULL,              -- quantidade fechada
    entry_price     REAL    NOT NULL,              -- preço médio de entrada
    exit_price      REAL    NOT NULL,              -- preço de saída
    pnl             REAL    NOT NULL,              -- (exit - entry) * qty (invertido pra short)
    side            TEXT    NOT NULL CHECK(side IN ('long', 'short')),
    closed_at       TEXT    NOT NULL DEFAULT (datetime('now'))
);

-- ============================================================
-- ATIVOS (classificação e metadados)
-- ============================================================
CREATE TABLE IF NOT EXISTS assets (
    ticker          TEXT    PRIMARY KEY,
    name            TEXT,                           -- "Petrobras PN", "Dólar/Real"
    asset_class     TEXT    NOT NULL,               -- "equity_br", "equity_us", "fx", "commodity", "etf", "crypto", "fixed_income"
    sector          TEXT,                           -- "Petróleo", "Bancos", "Mineração"
    exchange        TEXT,                           -- "B3", "NYSE", "NASDAQ", "CME"
    currency        TEXT    NOT NULL DEFAULT 'BRL', -- moeda de cotação
    yahoo_ticker    TEXT,                           -- ticker no Yahoo Finance (se diferente)
    is_active       INTEGER NOT NULL DEFAULT 1
);

-- ============================================================
-- ÍNDICES
-- ============================================================
CREATE INDEX IF NOT EXISTS idx_positions_fund      ON positions(fund_id);
CREATE INDEX IF NOT EXISTS idx_trades_fund          ON trades(fund_id);
CREATE INDEX IF NOT EXISTS idx_trades_ticker        ON trades(ticker);
CREATE INDEX IF NOT EXISTS idx_trades_date          ON trades(executed_at);
CREATE INDEX IF NOT EXISTS idx_nav_fund_date        ON nav_history(fund_id, date);
CREATE INDEX IF NOT EXISTS idx_prices_ticker_date   ON prices(ticker, date);
CREATE INDEX IF NOT EXISTS idx_benchmarks_name_date ON benchmarks(name, date);
CREATE INDEX IF NOT EXISTS idx_metrics_fund_date    ON metrics(fund_id, date);
CREATE INDEX IF NOT EXISTS idx_realized_fund        ON realized_pnl(fund_id);

-- ============================================================
-- HISTÓRICO DE POSIÇÕES (snapshot diário — registrado no batch)
-- ============================================================
CREATE TABLE IF NOT EXISTS position_history (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    fund_id         INTEGER NOT NULL REFERENCES funds(id),
    ticker          TEXT    NOT NULL,
    date            TEXT    NOT NULL,              -- YYYY-MM-DD
    quantity        REAL    NOT NULL,
    avg_price       REAL    NOT NULL,
    side            TEXT    NOT NULL,
    current_price   REAL,
    market_value    REAL,
    unrealized_pnl  REAL,
    daily_return    REAL,                          -- retorno % do ativo no dia
    contribution    REAL,                          -- contribuição ao retorno do fundo
    weight          REAL,                          -- peso no patrimônio
    UNIQUE(fund_id, ticker, date)
);

CREATE INDEX IF NOT EXISTS idx_pos_hist_fund_date ON position_history(fund_id, date);
CREATE INDEX IF NOT EXISTS idx_pos_hist_ticker    ON position_history(fund_id, ticker);
