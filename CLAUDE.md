# CLAUDE.md

Este arquivo orienta o Claude Code (claude.ai/code) ao trabalhar com o código deste repositório.

## Projeto

Sistema de paper trading (negociação simulada) da célula de Asset Management da PUC Finance. Web API em ASP.NET Core 8 (`src/PUCFinance.AssetManagement`) + SPA em React/Vite (`frontend/`). Textos da interface, mensagens de log e comentários no código são em português (escritos sem acentos no código, ex.: "nao", "Lider").

Obs.: `README.md` e `docs/ownership-runbook.md` estão parcialmente desatualizados (SQLite no repositório, sem autenticação, flag `--batch` na CLI). O `docs/heroku-postgres.md` reflete o modelo de deploy atual.

## Comandos

```bash
# Backend (http://localhost:5000, Swagger em /swagger)
dotnet run --project src/PUCFinance.AssetManagement
dotnet build PUCFinance.AssetManagement.sln

# Servidor de desenvolvimento do frontend (http://localhost:5173, redireciona /api -> localhost:5000)
cd frontend && npm install && npm run dev
cd frontend && npm run build        # gera frontend/dist

# Imagem completa de produção (o dist do frontend é copiado para o wwwroot do backend)
docker build -t pucfinance .
```

Não há projeto de testes nem linter configurado.

## Arquitetura

**Deploy único.** O Dockerfile compila o app Vite, copia o `dist/` para o `wwwroot` do backend, e a API serve os arquivos via `UseStaticFiles` + `MapFallbackToFile("index.html")`. Destinos de deploy: Heroku (`heroku.yml`, container stack) e Railway (`railway.toml`). A variável de ambiente `PORT` define a porta.

**Seleção do banco de dados (`Program.cs`).** Usa Postgres se `POSTGRES_CONNECTION_STRING` ou `DATABASE_URL` (formato de URL do Heroku, convertido em string de conexão Npgsql) estiver definida; caso contrário, SQLite em `DATABASE_PATH`, ou `/data/pucfinance.db` no Railway, ou `database/pucfinance.db` localmente (a raiz do repositório é encontrada procurando por `database/schema.sql`). O código precisa funcionar nos dois bancos.

**Sem migrations do EF.** O schema vem do `EnsureCreatedAsync()` na inicialização, seguido de `DatabaseSeeder.SeedAsync`. Os arquivos `database/schema.sql` / `seed.sql` não são executados. Como o `EnsureCreated` nunca altera um banco já existente, adições de schema em tabelas existentes precisam ser feitas manualmente em `DatabaseSeeder.EnsureAuthSchemaAsync` (com `CREATE TABLE IF NOT EXISTS` / `ALTER TABLE ... ADD COLUMN` específicos de cada banco, protegidos por `ColumnExistsAsync`), além de no `AppDbContext`.

**O seeder é idempotente e roda a cada inicialização.** Ele remove fundos/usuários fictícios antigos e faz upsert dos fundos, equipes, gestores e vínculos reais, da conta do líder e do catálogo de ativos. Para mudar pessoas ou fundos pré-cadastrados, edite os arrays estáticos em `Data/DatabaseSeeder.cs`.

**Convenções de dados.** Nomes de tabelas/colunas são em snake_case, mapeados explicitamente em `AppDbContext.OnModelCreating` (adicione o mapeamento de qualquer propriedade nova). Datas são armazenadas como strings (`yyyy-MM-dd`, timestamps `yyyy-MM-dd HH:mm:ss`) e comparadas com `string.Compare`. Booleanos são `int` (`IsActive == 1`). Valores monetários/quantidades são `double`. Posições vendidas usam quantidade negativa com `Side = "short"`.

**Autenticação.** Token bearer próprio assinado com HMAC (`AuthTokenService`, segredo em `AUTH_TOKEN_SECRET`, validade em `AUTH_TOKEN_HOURS`), validado por `SimpleBearerAuthenticationHandler`; não é JWT. Dois papéis (`AppRoles`): `leader` (faz login com senha; vê todos os fundos, cria fundos, exporta, vê membros, roda o batch) e `manager` (faz login só com e-mail, sem senha). A visibilidade dos fundos é controlada pelo `FundAccessService`: gestores só veem fundos cujo `TeamId` corresponde a uma equipe da qual fazem parte (`team_members`). Todo endpoint ligado a um fundo deve passar por `FindVisibleFundAsync` / `CanAccessFundAsync` e retornar 404 (não 403) quando o acesso não é permitido. O frontend guarda o token no localStorage (`frontend/src/lib/api.js`) e decide a interface com base em `authUser.role === 'leader'`.

**Preços e moeda (`PricingService`, `YahooChartClient`).** Fundos são em BRL; todo preço de ativo é convertido. `ResolveAsync` define o ticker do Yahoo e a moeda: ativos do catálogo `assets` usam `yahoo_ticker`/`currency` cadastrados (ex.: `BTC` → `BTC-USD`, `CL` → `CL=F`); fora do catálogo, `.SA` e índices (`^`) são BRL e o resto tem a moeda lida do Yahoo (GBp/ZAc/ILA são centavos → divide por 100). Moeda desconhecida = preço indisponível, nunca chute. `GetQuoteAsync` devolve preço nativo, câmbio (`{MOEDA}BRL=X`) e preço em BRL. `YahooChartClient` chama a API v8/chart direto e converte cada candle para a data no fuso da bolsa (câmbio começa 23:00 UTC do dia anterior). Benchmarks (IBOVESPA) ficam em pontos, sem conversão. A tabela `prices` guarda ativos em BRL.

**Trading (`TradeService`).** Trade é precificado no servidor via `GetQuoteAsync`; `trades.price` é sempre BRL, com `currency` e `fx_rate` (BRL por unidade da moeda) gravados. A regra de posição (preço médio, P&L realizado em redução/inversão) está em `PositionMath.Apply`, função pura reutilizada na execução, no replay e no histórico. Excluir um trade chama `RebuildFundAsync`, que reaplica todos os trades do fundo numa transação. Alterações de carteira passam pela trava estática `TradeService.PortfolioLock`; após criar/excluir, o controller dispara `BatchService.RunDailyUpdateAsync()` em background.

**Migração de moeda (`CurrencyMigrationService`, `NavHistoryRebuilder`).** Roda em background a cada boot (`Program.cs`, para não estourar os 60s do Heroku) e processa trades com `fx_rate IS NULL` (gravados antes da conversão existir), fundo a fundo e em transação. Converte pelo câmbio do dia do trade; se o código antigo usava outro instrumento no Yahoo (`ToYahooTicker(ticker) != yahoo_ticker`), reprecifica pelo fechamento do ativo correto naquele dia. Depois reconstrói posições/caixa, `nav_history` e `position_history` dos dias anteriores a hoje com fechamentos históricos em BRL, substitui os `prices` desses tickers e apaga métricas antigas; em seguida roda o batch. Se câmbio/moeda falhar, o fundo fica intacto e é tentado no próximo boot.

**Batch diário (`BatchService`).** Um por vez (`BatchGate`); NAV e métricas rodam sob `PortfolioLock`. Preços (Yahoo, em BRL) -> CDI (série 12 do SGS do Banco Central, `CdiService`) -> NAV por fundo (`NavCalculator`, grava `nav_history` + `position_history`) -> métricas (`MetricsCalculator`, períodos `inception`/`mtd`/`ytd`, alpha/beta contra o benchmark). Disparado por `POST /api/batch/run`, que aceita um header `X-Batch-Token` igual a `BATCH_TOKEN` ou um líder autenticado. O `.github/workflows/daily_update.yml` chama esse endpoint no app publicado (variável de repositório `APP_BASE_URL` + secret `BATCH_TOKEN`) às 21:00 UTC, de segunda a sexta.

**Valores em tempo real vs. armazenados.** O `FundsController` não devolve apenas o resultado do batch: `GetRealtimeSnapshotAsync` recalcula o patrimônio, a cota e o retorno do dia a partir das posições e do caixa atuais, e insere esse valor como o ponto de hoje nas respostas de NAV, métricas, retorno por classe e comparação com o CDI. As linhas salvas em `metrics` só fornecem alpha/beta. O Sharpe usa uma taxa livre de risco anual fixa no código (`AnnualRiskFreeRate` em `FundsController`).

**Organização do código.** Todos os controllers que não são de autenticação ficam em um único arquivo, `Controllers/Controllers.cs`; autenticação e equipes ficam em `Controllers/AuthController.cs`. As entidades estão todas em `Models/Entities.cs`, e os DTOs (records C#) em `Models/DTOs/Dtos.cs`. O estado e o carregamento de dados do frontend ficam principalmente em `frontend/src/App.jsx`; os gráficos usam Recharts.
