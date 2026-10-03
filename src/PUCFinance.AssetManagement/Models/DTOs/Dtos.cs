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

// ── Responses ─────────────────────────────────────────

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
    string Benchmark
);

public record PositionResponse(
    string Ticker,
    string Side,
    double Quantity,
    double AvgPrice,
    double? CurrentPrice,
    double? MarketValue,
    double? UnrealizedPnl,
    double? Weight           // % do patrimônio
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
    double? FxRate          // BRL por unidade da moeda
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
