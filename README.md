# PUC Finance — Asset Management System

Sistema de paper trading para a célula de Asset Management da PUC Finance.

## Stack

- **Backend:** C# / ASP.NET 8 Web API
- **Database:** SQLite (arquivo local, versionado no repo)
- **Frontend:** React (TBD)
- **Preços:** Yahoo Finance
- **Automação:** batch diário agendado dentro do próprio app (19h de Brasília, seg–sex)

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
```

## Pipeline Diário

1. O próprio app roda o batch (`BatchSchedulerService`) ao subir e às 19h de Brasília, de segunda a sexta. O botão "Run Batch" roda na hora.
2. Tesouro Direto: catálogo de títulos, cupons e vencimentos
3. `PricingService` busca preços (Yahoo Finance, convertidos para BRL; títulos pelo CSV do Tesouro)
4. CDI (Banco Central) e IBOVESPA (Yahoo) em `benchmarks`
5. Dias úteis sem NAV são preenchidos com fechamentos históricos (`NavBackfillService`)
6. `NavCalculator` recalcula patrimônio e cota de cada fundo
7. `MetricsCalculator` calcula Retorno, Vol, Sharpe (vs CDI), Drawdown, Alpha, Beta (vs IBOVESPA)

## Métricas

- **Retorno acumulado:** (cota_final / cota_inicial) - 1
- **Volatilidade:** σ(retornos_diários) × √252
- **Sharpe Ratio:** (retorno_anualizado - CDI) / volatilidade
- **Max Drawdown:** maior queda pico-vale
- **Alpha/Beta:** regressão linear contra Ibovespa
