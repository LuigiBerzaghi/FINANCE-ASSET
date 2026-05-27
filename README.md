# PUC Finance — Asset Management System

Sistema de paper trading para a célula de Asset Management da PUC Finance.

## Stack

- **Backend:** C# / ASP.NET 8 Web API
- **Database:** SQLite (arquivo local, versionado no repo)
- **Frontend:** React (TBD)
- **Preços:** Yahoo Finance
- **Automação:** GitHub Actions (batch diário às 18h BRT)

## Setup Local

```bash
# Clone
git clone https://github.com/ThomasJKobayashi/PUCFinance-AssetManagement.git
cd PUCFinance-AssetManagement

# Restore + Run
dotnet run --project src/PUCFinance.AssetManagement

# Swagger UI disponível em:
# http://localhost:5000/swagger
```

## API Endpoints

| Método | Rota | Descrição |
|--------|------|-----------|
| GET | `/api/funds` | Lista todos os fundos com resumo |
| POST | `/api/funds` | Cria novo fundo |
| GET | `/api/funds/{id}/positions` | Posições de um fundo |
| GET | `/api/funds/{id}/nav` | Histórico de NAV |
| GET | `/api/funds/{id}/metrics` | Métricas (Sharpe, Vol, etc.) |
| POST | `/api/trades` | Executa um trade |
| GET | `/api/trades/fund/{fundId}` | Histórico de trades |
| POST | `/api/batch/run` | Executa batch diário manualmente |

## Estrutura

```
├── database/
│   ├── schema.sql          ← Schema do SQLite
│   ├── seed.sql            ← Dados iniciais
│   └── pucfinance.db       ← Banco (criado automaticamente)
│
├── src/PUCFinance.AssetManagement/
│   ├── Controllers/        ← Endpoints da API
│   ├── Data/               ← DbContext (EF Core)
│   ├── Models/             ← Entidades + DTOs
│   ├── Services/           ← Lógica de negócio
│   └── Program.cs          ← Entry point
│
├── frontend/               ← React (TODO)
│
└── .github/workflows/
    └── daily_update.yml    ← GitHub Actions
```

## Pipeline Diário

1. GitHub Actions dispara às 18h BRT (21h UTC)
2. `PricingService` busca preços no Yahoo Finance
3. `NavCalculator` recalcula patrimônio e cota de cada fundo
4. `MetricsCalculator` calcula Sharpe, Vol, Drawdown, Alpha, Beta
5. Banco atualizado é commitado de volta no repo

## Métricas

- **Retorno acumulado:** (cota_final / cota_inicial) - 1
- **Volatilidade:** σ(retornos_diários) × √252
- **Sharpe Ratio:** (retorno_anualizado - CDI) / volatilidade
- **Max Drawdown:** maior queda pico-vale
- **Alpha/Beta:** regressão linear contra Ibovespa
