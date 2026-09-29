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

**Tesouro Direto (`TesouroDireto`, `TesouroDiretoClient`, `TreasuryService`).** Títulos públicos são ativos com ticker `{CÓDIGO}-{MÊS}{ANO}` (ex.: `NTNB-MAI2035`, `LTN-JAN2029`, `LFT-MAR2029`; códigos LTN, NTNF, NTNBP, NTNB, LFT, NTNC, EDUCA, RENDA em `TesouroDireto.Types`). Preços vêm do CSV oficial do Tesouro Transparente (`PrecoTaxaTesouroDireto.csv`, ~15 MB, D-1), baixado e mantido em memória pelo singleton `TesouroDiretoClient` (renova a cada 1h; se falhar, usa a cópia anterior). Compra executa no PU de compra, venda no PU de venda; marcação diária pelo PU de venda. Não há venda a descoberto; quantidade em múltiplos de 0,01. `GET /api/treasury/bonds` lista os títulos ofertados para o seletor da tela. Cupons (NTN-B: VNA pelo IPCA SGS 433 × (1,06^0,5−1); NTN-F: 1000 × (1,10^0,5−1); NTN-C: VNA pelo IGP-M SGS 189, 12% a.a. na 2031) e resgates no vencimento (LTN/NTN-F R$ 1.000; NTN-B/NTN-B Principal VNA; LFT último PU) viram linhas em `treasury_events`, criadas pelo batch (`TreasuryService.ProcessDueEventsAsync`) para quem tinha o título na véspera. Educa+/Renda+ (fluxo mensal) não têm resgate automático.

**Replay do fundo (`FundLedger`).** Posições, caixa e P&L realizado são derivados de trades + `treasury_events` em ordem cronológica (no mesmo dia: cupom, depois resgate, depois trades). `TradeService.RebuildFundAsync` e `NavHistoryRebuilder` usam o mesmo replay; o caixa nunca deve ser alterado por fora dele (exceto o incremento do trade novo em `ExecuteTradeAsync`). O retorno diário de um título soma o cupom pago no período (o PU cai no dia do pagamento).

**Batch diário (`BatchService`, `BatchSchedulerService`).** Agendado dentro do próprio app (hosted service com `Cronos`): uma vez ~2 min após a subida e às 19:00 de Brasília, seg–sex (`BATCH_SCHEDULE` muda o cron; `off` desliga — útil localmente). Exige dyno que não dorme (o Heroku é Basic). Um por vez (`BatchGate`); NAV e métricas rodam sob `PortfolioLock`. Etapas: Tesouro (catálogo, cupons, resgates) -> Preços (Yahoo em BRL; títulos pelo PU de venda) -> CDI (`CdiService`) e IBOVESPA (`IbovespaService`) -> preenchimento de dias úteis sem NAV com fechamentos históricos (`NavBackfillService`, só acrescenta linhas) -> NAV de hoje (`NavCalculator`) -> métricas (`MetricsCalculator`). Também disparado por `POST /api/batch/run` (botão "Run Batch"), que aceita `X-Batch-Token` igual a `BATCH_TOKEN` ou um líder autenticado.

**Valores em tempo real vs. armazenados.** O `FundsController` não devolve apenas o resultado do batch: `GetRealtimeSnapshotAsync` recalcula o patrimônio, a cota e o retorno do dia a partir das posições e do caixa atuais, e insere esse valor como o ponto de hoje nas respostas de NAV, métricas, retorno por classe e comparação com a referência do fundo (`/benchmark-comparison`: gráfico "Fundo vs IBOVESPA" ou "Fundo vs CDI"). O caixa dos fundos não rende nada (decisão do usuário): renda sem risco é via Tesouro Selic.

**Métricas (`PerformanceMath`).** Uma única implementação, usada pela tela (`FundsController.GetMetrics`, em tempo real) e pelo registro diário (`MetricsCalculator`, tabela `metrics`, usada no Excel). Cada observação é o intervalo entre dois NAVs em dias úteis diferentes, com sua duração em dias úteis: buracos no histórico e fins de semana não contam como "1 dia". O calendário de dias úteis vem das datas do CDI (`BusinessCalendar`; fora da série, dias de semana). Anualização por dias úteis (252). Sharpe contra o CDI do período, sem taxa fixa. Referência por fundo (`funds.benchmark`, `FundBenchmarks`): IBOVESPA → alpha/beta por regressão contra o índice nos mesmos intervalos; CDI → alpha = retorno anualizado − CDI do período, sem beta. O seeder preenche o padrão só quando está vazio (Renda Variável e Beta: IBOVESPA; Best Ideas, Multimercado e Renda Fixa: CDI); fundos novos escolhem na criação. Volatilidade, Sharpe, Alpha e Beta só com `PerformanceMath.MinObservations` (20) observações. O IBOVESPA diário fica em `benchmarks` (`IbovespaService`, no batch, com preenchimento automático de dias faltantes); o CDI também (`CdiService`; o Bacen responde 404/`{"erro"}` quando não há CDI novo).

**Organização do código.** Todos os controllers que não são de autenticação ficam em um único arquivo, `Controllers/Controllers.cs`; autenticação e equipes ficam em `Controllers/AuthController.cs`. As entidades estão todas em `Models/Entities.cs`, e os DTOs (records C#) em `Models/DTOs/Dtos.cs`. O estado e o carregamento de dados do frontend ficam principalmente em `frontend/src/App.jsx`; os gráficos usam Recharts.
