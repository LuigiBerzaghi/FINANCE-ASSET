namespace PUCFinance.AssetManagement.Models.DTOs;

// ── Requests ──────────────────────────────────────────

public record CreateFundRequest(
    string Name,
    string? Strategy,
    double InitialCapital = 1_000_000,
    double TotalShares = 1_000_000,
    int? TeamId = null,
    string? Benchmark = null,  // IBOVESPA (padrao) ou CDI
    int[]? ManagerIds = null   // gestores do fundo: cria o time do fundo com eles
);

public record LoginRequest(
    string Email,
    string? Password = null
);

public record AuthResponse(
    string Token,
    string ExpiresAt,
    AuthUserResponse User
);

public record AuthUserResponse(
    int Id,
    string Name,
    string Email,
    string Role
);

public record TeamResponse(
    int Id,
    string Name
);

public record ManagerResponse(
    int Id,
    string Name,
    string Email
);

public record FundMembersResponse(
    int FundId,
    int? TeamId,
    string? TeamName,
    List<FundMemberResponse> Members
);

public record FundMemberResponse(
    int Id,
    string Name,
    string Email,
    string Role,
    bool IsActive
);

public record ExecuteTradeRequest(
    int FundId,
    string Ticker,
    string Side,        // "long" ou "short"
    double Quantity,
    string? Thesis,
    string? ExecutedBy,
    double? Amount = null  // por valor (BRL): o servidor calcula a quantidade no preco da execucao
);

/// <summary>Fechamento total ou parcial de uma posicao: o lado e definido pela posicao (nunca inverte).</summary>
public record ClosePositionRequest(
    int FundId,
    string Ticker,
    double Quantity,    // quanto fechar, ate a quantidade inteira da posicao
    string? Thesis,     // justificativa obrigatoria
    double? Amount = null  // ou quanto fechar em BRL (no lugar da quantidade)
);

/// <summary>
/// Ordem de aplicacao (long, por valor) ou resgate (short: por valor, por quantidade de cotas ou total)
/// em cota de fundo. Executada pela cota do dia da ordem quando a CVM publicar (ver FundOrderService).
/// </summary>
public record CreateFundOrderRequest(
    int FundId,
    string Ticker,          // FI- + CNPJ (+ subclasse), da busca de fundos
    string Side,            // "long" = aplicacao, "short" = resgate
    double? Amount = null,  // aplicacao ou resgate por valor (BRL)
    double? Quantity = null,  // resgate por quantidade de cotas
    bool RedeemAll = false, // resgate total
    string? Thesis = null   // justificativa (obrigatoria no resgate)
);

// ── Responses ─────────────────────────────────────────

/// <summary>Base para a tela calcular os limites de venda a descoberto e de exposicao bruta de um ativo (ver TradeService.GetTradeLimitsAsync).</summary>
public record TradeLimitsResponse(
    double MaxShortExposure,      // fracao do patrimonio (1 = 100%)
    double Cash,
    double OtherPositionsValue,   // valor das demais posicoes (vendidas negativas)
    double OtherShortExposure,    // soma (positiva) das demais posicoes vendidas
    double HeldQuantity,          // quantidade do ativo no fundo (vendido negativo)
    double MaxGrossExposure,      // comprado + vendido, fracao do patrimonio (2 = 200%)
    double OtherGrossExposure,    // soma em modulo das demais posicoes (comprado + vendido) e das aplicacoes pendentes
    double ReservedCash = 0       // caixa reservado para aplicacoes em fundos pendentes (nao pode ser usado em compras)
);

/// <summary>Fundo aberto a aplicacoes (cadastro da CVM) com a ultima cota publicada.</summary>
public record FundSearchResultResponse(
    string Ticker,
    string Cnpj,
    string? Subclass,
    string Name,
    string? Classification,   // Renda Fixa, Multimercado, Acoes, Cambial
    string? AnbimaClass,
    bool IsFic,               // classe de cotas (investe em outros fundos)
    string? TargetAudience,   // Publico Geral, Qualificado, Profissional
    double LastQuota,
    string LastQuotaDate,
    double? Equity            // patrimonio liquido na ultima cota
);

/// <summary>Dados do fundo para a boleta: ultima cota e em que cota entra uma ordem dada agora.</summary>
public record FundOrderInfoResponse(
    FundSearchResultResponse Fund,
    bool AcceptsApplications, // aberto a aplicacoes no cadastro da CVM (resgate vale mesmo se nao)
    string NextQuoteDate,     // cota de uma ordem dada agora (dia util ate o corte; senao, o proximo)
    int CutoffHour,           // horario de corte, Brasilia
    string CancelUntil,       // ate quando o gestor pode cancelar (ISO 8601 com fuso)
    double QuantityStep
);

public record FundOrderResponse(
    int Id,
    int FundId,
    string Ticker,
    string? Name,
    string Side,              // long = aplicacao, short = resgate
    double? Amount,
    double? Quantity,
    bool RedeemAll,
    string QuoteDate,         // cota da ordem (yyyy-MM-dd)
    string CancelUntil,       // prazo de cancelamento do gestor (ISO 8601 com fuso)
    bool CanCancel,           // o usuario atual pode cancelar agora
    string Status,            // pending, executed, cancelled, rejected
    string? Thesis,
    string? CreatedBy,
    string CreatedAt,
    string? ResolvedAt,
    double? Price,            // cota usada na execucao
    string? PriceDate,
    int? TradeId,
    double? ExecutedQuantity, // cotas do trade gerado
    string? Message
);

public record FundSummaryResponse(
    int Id,
    string Name,
    string? Strategy,
    int? TeamId,
    string? TeamName,
    double TotalEquity,
    double ShareValue,
    double DailyReturn,
    double CashBalance,
    int PositionCount,
    string Benchmark,
    double ReservedCash = 0  // caixa reservado para aplicacoes em fundos pendentes
);

public record PositionResponse(
    string Ticker,
    string Side,
    double Quantity,
    double AvgPrice,
    double? CurrentPrice,
    double? MarketValue,
    double? UnrealizedPnl,
    double? Weight,          // % do patrimônio
    string? Name = null      // nome do ativo no catalogo (ex.: fundo da CVM)
);

public record NavPointResponse(
    string Date,
    double TotalEquity,
    double ShareValue,
    double? DailyReturn
);

public record TradeResponse(
    int Id,
    string Ticker,
    string Side,
    double Quantity,
    double Price,           // BRL
    string? Thesis,
    string ExecutedAt,
    string? ExecutedBy,
    string Currency,        // moeda de cotacao do ativo
    double? FxRate,         // BRL por unidade da moeda
    string? Name = null     // nome do ativo no catalogo (ex.: fundo da CVM)
);

public record MetricsResponse(
    string Period,
    double? CumulativeReturn,
    double? AnnualizedReturn,
    double? Volatility,
    double? SharpeRatio,
    double? MaxDrawdown,
    double? Alpha,
    double? Beta,
    string? BenchmarkName,
    double? RiskFreeRate,   // CDI do periodo, anualizado (base do Sharpe)
    int Observations,       // intervalos entre NAVs usados no calculo
    int BusinessDays,       // dias uteis do periodo
    int MinObservations     // minimo para volatilidade, Sharpe, alpha e beta
);

public record BatchResultResponse(
    string Status,
    int FundsUpdated,
    int PricesFetched,
    string Timestamp
);

// ── Performance por ativo ────────────────────────────

public record AssetPerformanceResponse(
    string Ticker,
    string Side,
    double Quantity,
    double AvgPrice,
    double? CurrentPrice,
    double? MarketValue,
    double? UnrealizedPnl,
    double RealizedPnl,
    double TotalPnl,
    double? ReturnPct,          // retorno % total
    double? Weight,             // peso atual no fundo
    double? Contribution,       // contribuicao ao retorno do fundo (soma)
    List<AssetDailyPoint> DailyHistory
);

public record AssetDailyPoint(
    string Date,
    double? Price,
    double? DailyReturn,
    double? Contribution,
    double? Weight
);

// ── Exposicao ────────────────────────────────────────

public record ExposureResponse(
    double GrossExposure,       // soma(|market_value|) / patrimonio
    double NetExposure,         // (long - |short|) / patrimonio
    double LongExposure,        // soma(long_market_value) / patrimonio
    double ShortExposure,       // soma(|short_market_value|) / patrimonio
    double CashWeight,          // caixa / patrimonio
    List<ClassExposure> ByClass
);

public record ClassExposure(
    string AssetClass,
    string Label,               // "Acoes BR", "FX", etc
    double LongValue,
    double ShortValue,
    double NetValue,
    double GrossWeight,         // % do patrimonio
    double NetWeight,
    int PositionCount
);

// ── Retorno por classe ───────────────────────────────

public record ReturnByClassResponse(
    string Period,              // "mtd", "ytd", "inception"
    double TotalReturn,
    List<ClassReturn> ByClass
);

public record ClassReturn(
    string AssetClass,
    string Label,
    double Contribution,        // contribuicao em pontos percentuais
    double Weight               // % do retorno total explicado
);

// ── CDI Benchmark ────────────────────────────────────

/// <summary>Fundo vs a referencia dele (IBOVESPA ou CDI), acumulado desde o inicio.</summary>
public record BenchmarkComparisonResponse(
    string BenchmarkName,
    double FundReturn,
    double BenchmarkReturn,
    double ExcessReturn,        // fundo - referencia
    string Period,
    List<BenchmarkComparisonPoint> Series
);

public record BenchmarkComparisonPoint(
    string Date,
    double FundCumulative,      // retorno acumulado do fundo
    double BenchmarkCumulative  // retorno acumulado da referencia
);

// ── Asset (para dropdown) ────────────────────────────

public record AssetResponse(
    string Ticker,
    string? Name,
    string AssetClass,
    string? Sector,
    string? Exchange,
    string Currency
);

/// <summary>Titulo publico ofertado no Tesouro Direto (taxas em % a.a., precos por titulo).</summary>
public record TreasuryBondResponse(
    string Ticker,
    string Name,
    string Type,
    string Code,
    string Maturity,
    string BaseDate,
    double BuyRate,
    double SellRate,
    double BuyPrice,
    double SellPrice,
    bool CanBuy
);
