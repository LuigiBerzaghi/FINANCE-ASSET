namespace PUCFinance.AssetManagement.Models;

public static class AppRoles
{
    public const string Leader = "leader";
    public const string Manager = "manager";
}

public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public class AppUser
{
    public int Id { get; set; }
    public int? TeamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = AppRoles.Manager;
    public int IsActive { get; set; } = 1;
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    public Team? Team { get; set; }
}

public class TeamMember
{
    public int TeamId { get; set; }
    public int UserId { get; set; }
    public Team? Team { get; set; }
    public AppUser? User { get; set; }
}

public class Fund
{
    public int Id { get; set; }
    public int? TeamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Strategy { get; set; }
    public double InitialCapital { get; set; } = 1_000_000;
    public double TotalShares { get; set; } = 1_000_000;
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    public int IsActive { get; set; } = 1;
    public string? Benchmark { get; set; }   // IBOVESPA ou CDI (ver FundBenchmarks)
    public Team? Team { get; set; }
}

/// <summary>
/// Referencia de cada fundo nas metricas. IBOVESPA: alpha/beta por regressao contra o indice
/// (fundos de acoes). CDI: alpha = retorno anualizado acima do CDI e sem beta (multimercado, renda fixa).
/// </summary>
public static class FundBenchmarks
{
    public const string Ibovespa = "IBOVESPA";
    public const string Cdi = "CDI";

    public static bool IsValid(string? value) => value?.Trim().ToUpperInvariant() is Ibovespa or Cdi;

    public static string Resolve(string? value) => IsValid(value) ? value!.Trim().ToUpperInvariant() : Ibovespa;
}

public class Position
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public double AvgPrice { get; set; }
    public string Side { get; set; } = "long";
    public double? CurrentPrice { get; set; }
    public double? MarketValue { get; set; }
    public double? UnrealizedPnl { get; set; }
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public class Trade
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string Side { get; set; } = "long";
    public double Quantity { get; set; }
    public double Price { get; set; }              // sempre em BRL
    public string Currency { get; set; } = "BRL";  // moeda de cotacao do ativo
    public double? FxRate { get; set; }            // BRL por unidade da moeda; null = trade legado nao convertido
    public string? Thesis { get; set; }
    public string ExecutedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    public string? ExecutedBy { get; set; }
}

public class Cash
{
    public int FundId { get; set; }
    public double Balance { get; set; }
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public class NavHistory
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public string Date { get; set; } = string.Empty;
    public double TotalEquity { get; set; }
    public double TotalShares { get; set; }
    public double ShareValue { get; set; }
    public double? DailyReturn { get; set; }
    public double CashBalance { get; set; }
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public class Price
{
    public int Id { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public double? Open { get; set; }
    public double? High { get; set; }
    public double? Low { get; set; }
    public double Close { get; set; }
    public double? AdjClose { get; set; }
    public long? Volume { get; set; }
    public string Source { get; set; } = "yahoo";
    public string FetchedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public class Benchmark
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public double Value { get; set; }
    public double? DailyReturn { get; set; }
    public double? Cumulative { get; set; }
    public string? Source { get; set; }
}

public class Metric
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public string Date { get; set; } = string.Empty;
    public string Period { get; set; } = "daily";
    public double? CumulativeReturn { get; set; }
    public double? AnnualizedReturn { get; set; }
    public double? Volatility { get; set; }
    public double? SharpeRatio { get; set; }
    public double? MaxDrawdown { get; set; }
    public double? Alpha { get; set; }
    public double? Beta { get; set; }
    public string? BenchmarkName { get; set; }
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public class RealizedPnl
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public double EntryPrice { get; set; }
    public double ExitPrice { get; set; }
    public double Pnl { get; set; }
    public string Side { get; set; } = "long";
    public string ClosedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public class PositionHistory
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public double AvgPrice { get; set; }
    public string Side { get; set; } = "long";
    public double? CurrentPrice { get; set; }
    public double? MarketValue { get; set; }
    public double? UnrealizedPnl { get; set; }
    public double? DailyReturn { get; set; }
    public double? Contribution { get; set; }
    public double? Weight { get; set; }
}

public class Asset
{
    public string Ticker { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string AssetClass { get; set; } = string.Empty;
    public string? Sector { get; set; }
    public string? Exchange { get; set; }
    public string Currency { get; set; } = "BRL";
    public string? YahooTicker { get; set; }
    public int IsActive { get; set; } = 1;
}

/// <summary>
/// Cupom ou resgate de titulo publico creditado no caixa do fundo. AmountPerUnit e o valor por titulo;
/// Quantity e Total sao recalculados pela reconstrucao do fundo (quantidade em carteira na vespera).
/// </summary>
public class TreasuryEvent
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public string Ticker { get; set; } = string.Empty;
    public string EventDate { get; set; } = string.Empty;
    public string Kind { get; set; } = TreasuryEventKinds.Coupon;
    public double AmountPerUnit { get; set; }
    public double Quantity { get; set; }
    public double Total { get; set; }
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
}

public static class TreasuryEventKinds
{
    public const string Coupon = "coupon";
    public const string Maturity = "maturity";
}
