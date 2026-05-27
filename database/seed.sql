-- PUC Finance - Seed Data

-- Fundos de exemplo
INSERT OR IGNORE INTO funds (name, strategy, initial_capital, total_shares) VALUES
    ('Alpha',   'Long Only',   1000000.00, 1000000),
    ('Beta',    'Long/Short',  1000000.00, 1000000),
    ('Gamma',   'Macro',       1000000.00, 1000000);

-- Caixa inicial
INSERT OR IGNORE INTO cash (fund_id, balance) VALUES
    (1, 1000000.00),
    (2, 1000000.00),
    (3, 1000000.00);

-- NAV dia 0
INSERT OR IGNORE INTO nav_history (fund_id, date, total_equity, total_shares, share_value, daily_return, cash_balance) VALUES
    (1, date('now'), 1000000.00, 1000000, 1.0000, 0.0, 1000000.00),
    (2, date('now'), 1000000.00, 1000000, 1.0000, 0.0, 1000000.00),
    (3, date('now'), 1000000.00, 1000000, 1.0000, 0.0, 1000000.00);

-- ============================================================
-- ATIVOS PRE-CLASSIFICADOS
-- ============================================================

-- Acoes BR
INSERT OR IGNORE INTO assets (ticker, name, asset_class, sector, exchange, currency) VALUES
    ('PETR4', 'Petrobras PN', 'equity_br', 'Petroleo e Gas', 'B3', 'BRL'),
    ('VALE3', 'Vale ON', 'equity_br', 'Mineracao', 'B3', 'BRL'),
    ('ITUB4', 'Itau Unibanco PN', 'equity_br', 'Bancos', 'B3', 'BRL'),
    ('BBDC4', 'Bradesco PN', 'equity_br', 'Bancos', 'B3', 'BRL'),
    ('BBAS3', 'Banco do Brasil ON', 'equity_br', 'Bancos', 'B3', 'BRL'),
    ('WEGE3', 'WEG ON', 'equity_br', 'Bens Industriais', 'B3', 'BRL'),
    ('ABEV3', 'Ambev ON', 'equity_br', 'Bebidas', 'B3', 'BRL'),
    ('RENT3', 'Localiza ON', 'equity_br', 'Aluguel de Carros', 'B3', 'BRL'),
    ('MGLU3', 'Magazine Luiza ON', 'equity_br', 'Varejo', 'B3', 'BRL'),
    ('SUZB3', 'Suzano ON', 'equity_br', 'Papel e Celulose', 'B3', 'BRL'),
    ('JBSS3', 'JBS ON', 'equity_br', 'Alimentos', 'B3', 'BRL'),
    ('ELET3', 'Eletrobras ON', 'equity_br', 'Energia Eletrica', 'B3', 'BRL'),
    ('B3SA3', 'B3 ON', 'equity_br', 'Servicos Financeiros', 'B3', 'BRL'),
    ('RDOR3', 'Rede D Or ON', 'equity_br', 'Saude', 'B3', 'BRL'),
    ('EMBR3', 'Embraer ON', 'equity_br', 'Aeronautica', 'B3', 'BRL'),
    ('CYRE3', 'Cyrela ON', 'equity_br', 'Construcao', 'B3', 'BRL'),
    ('VIVT3', 'Telefonica Brasil ON', 'equity_br', 'Telecomunicacoes', 'B3', 'BRL'),
    ('HAPV3', 'Hapvida ON', 'equity_br', 'Saude', 'B3', 'BRL'),
    ('CSAN3', 'Cosan ON', 'equity_br', 'Energia', 'B3', 'BRL'),
    ('PRIO3', 'PRIO ON', 'equity_br', 'Petroleo e Gas', 'B3', 'BRL');

-- ETFs BR
INSERT OR IGNORE INTO assets (ticker, name, asset_class, sector, exchange, currency) VALUES
    ('BOVA11', 'iShares Ibovespa', 'etf', 'Indice', 'B3', 'BRL'),
    ('IVVB11', 'iShares S&P 500 BRL', 'etf', 'Indice', 'B3', 'BRL'),
    ('SMAL11', 'iShares Small Cap', 'etf', 'Indice', 'B3', 'BRL'),
    ('HASH11', 'Hashdex Crypto', 'etf', 'Cripto', 'B3', 'BRL'),
    ('IMAB11', 'Itau IMA-B', 'etf', 'Renda Fixa', 'B3', 'BRL');

-- Acoes EUA
INSERT OR IGNORE INTO assets (ticker, name, asset_class, sector, exchange, currency, yahoo_ticker) VALUES
    ('AAPL', 'Apple', 'equity_us', 'Tecnologia', 'NASDAQ', 'USD', 'AAPL'),
    ('MSFT', 'Microsoft', 'equity_us', 'Tecnologia', 'NASDAQ', 'USD', 'MSFT'),
    ('NVDA', 'NVIDIA', 'equity_us', 'Tecnologia', 'NASDAQ', 'USD', 'NVDA'),
    ('AMZN', 'Amazon', 'equity_us', 'Tecnologia', 'NASDAQ', 'USD', 'AMZN'),
    ('GOOGL', 'Alphabet', 'equity_us', 'Tecnologia', 'NASDAQ', 'USD', 'GOOGL'),
    ('TSLA', 'Tesla', 'equity_us', 'Automotivo', 'NASDAQ', 'USD', 'TSLA'),
    ('META', 'Meta Platforms', 'equity_us', 'Tecnologia', 'NASDAQ', 'USD', 'META'),
    ('JPM', 'JPMorgan Chase', 'equity_us', 'Bancos', 'NYSE', 'USD', 'JPM');

-- FX
INSERT OR IGNORE INTO assets (ticker, name, asset_class, sector, exchange, currency, yahoo_ticker) VALUES
    ('USDBRL', 'Dolar/Real', 'fx', 'Cambio', 'FX', 'BRL', 'USDBRL=X'),
    ('EURBRL', 'Euro/Real', 'fx', 'Cambio', 'FX', 'BRL', 'EURBRL=X'),
    ('EURUSD', 'Euro/Dolar', 'fx', 'Cambio', 'FX', 'USD', 'EURUSD=X'),
    ('USDJPY', 'Dolar/Iene', 'fx', 'Cambio', 'FX', 'JPY', 'JPY=X');

-- Commodities
INSERT OR IGNORE INTO assets (ticker, name, asset_class, sector, exchange, currency, yahoo_ticker) VALUES
    ('CL', 'Petroleo WTI', 'commodity', 'Energia', 'NYMEX', 'USD', 'CL=F'),
    ('GC', 'Ouro', 'commodity', 'Metais Preciosos', 'COMEX', 'USD', 'GC=F'),
    ('SI', 'Prata', 'commodity', 'Metais Preciosos', 'COMEX', 'USD', 'SI=F'),
    ('NG', 'Gas Natural', 'commodity', 'Energia', 'NYMEX', 'USD', 'NG=F'),
    ('ZS', 'Soja', 'commodity', 'Agricultura', 'CBOT', 'USD', 'ZS=F'),
    ('ZC', 'Milho', 'commodity', 'Agricultura', 'CBOT', 'USD', 'ZC=F');

-- Cripto
INSERT OR IGNORE INTO assets (ticker, name, asset_class, sector, exchange, currency, yahoo_ticker) VALUES
    ('BTC', 'Bitcoin', 'crypto', 'Cripto', 'Global', 'USD', 'BTC-USD'),
    ('ETH', 'Ethereum', 'crypto', 'Cripto', 'Global', 'USD', 'ETH-USD'),
    ('SOL', 'Solana', 'crypto', 'Cripto', 'Global', 'USD', 'SOL-USD');
