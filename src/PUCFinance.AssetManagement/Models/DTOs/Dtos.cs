namespace PUCFinance.AssetManagement.Models.DTOs;

// ── Requests ──────────────────────────────────────────

public record CreateFundRequest(
    string Name,
    string? Strategy,
    double InitialCapital = 1_000_000,
    double TotalShares = 1_000_000
);

public record ExecuteTradeRequest(
    int FundId,
    string Ticker,
    string Side,        // "long" ou "short"
    double Quantity,
    double Price,
    string? Thesis,
    string? ExecutedBy
);

// ── Responses ─────────────────────────────────────────

public record FundSummaryResponse(
    int Id,
    string Name,
    string? Strategy,
    double TotalEquity,
    double ShareValue,
    double DailyReturn,
    double CashBalance,
    int PositionCount
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
    double Price,
    string? Thesis,
    string ExecutedAt,
    string? ExecutedBy
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
    string? BenchmarkName
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

public record CdiBenchmarkResponse(
    double FundReturn,
    double CdiReturn,
    double ExcessReturn,        // fund - CDI
    string Period,
    List<CdiComparisonPoint> Series
);

public record CdiComparisonPoint(
    string Date,
    double FundCumulative,      // retorno acumulado do fundo
    double CdiCumulative        // retorno acumulado do CDI
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
