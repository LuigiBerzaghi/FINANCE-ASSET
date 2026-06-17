using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Fund> Funds => Set<Fund>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Cash> Cash => Set<Cash>();
    public DbSet<NavHistory> NavHistory => Set<NavHistory>();
    public DbSet<Price> Prices => Set<Price>();
    public DbSet<Benchmark> Benchmarks => Set<Benchmark>();
    public DbSet<Metric> Metrics => Set<Metric>();
    public DbSet<RealizedPnl> RealizedPnl => Set<RealizedPnl>();
    public DbSet<PositionHistory> PositionHistory => Set<PositionHistory>();
    public DbSet<Asset> Assets => Set<Asset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Team
        modelBuilder.Entity<Team>(e =>
        {
            e.ToTable("teams");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.Name).IsUnique();
        });

        // AppUser
        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("app_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TeamId).HasColumnName("team_id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Email).HasColumnName("email").IsRequired();
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
            e.Property(x => x.Role).HasColumnName("role").IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId);
        });

        // Fund
        modelBuilder.Entity<Fund>(e =>
        {
            e.ToTable("funds");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TeamId).HasColumnName("team_id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Strategy).HasColumnName("strategy");
            e.Property(x => x.InitialCapital).HasColumnName("initial_capital");
            e.Property(x => x.TotalShares).HasColumnName("total_shares");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId);
        });

        // Position
        modelBuilder.Entity<Position>(e =>
        {
            e.ToTable("positions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FundId).HasColumnName("fund_id");
            e.Property(x => x.Ticker).HasColumnName("ticker");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.AvgPrice).HasColumnName("avg_price");
            e.Property(x => x.Side).HasColumnName("side");
            e.Property(x => x.CurrentPrice).HasColumnName("current_price");
            e.Property(x => x.MarketValue).HasColumnName("market_value");
            e.Property(x => x.UnrealizedPnl).HasColumnName("unrealized_pnl");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.FundId, x.Ticker }).IsUnique();
            e.HasOne<Fund>().WithMany().HasForeignKey(x => x.FundId);
        });

        // Trade
        modelBuilder.Entity<Trade>(e =>
        {
            e.ToTable("trades");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FundId).HasColumnName("fund_id");
            e.Property(x => x.Ticker).HasColumnName("ticker");
            e.Property(x => x.Side).HasColumnName("side");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.Price).HasColumnName("price");
            e.Property(x => x.Thesis).HasColumnName("thesis");
            e.Property(x => x.ExecutedAt).HasColumnName("executed_at");
            e.Property(x => x.ExecutedBy).HasColumnName("executed_by");
            e.HasOne<Fund>().WithMany().HasForeignKey(x => x.FundId);
        });

        // Cash
        modelBuilder.Entity<Cash>(e =>
        {
            e.ToTable("cash");
            e.HasKey(x => x.FundId);
            e.Property(x => x.FundId).HasColumnName("fund_id");
            e.Property(x => x.Balance).HasColumnName("balance");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<Fund>().WithOne().HasForeignKey<Cash>(x => x.FundId);
        });

        // NavHistory
        modelBuilder.Entity<NavHistory>(e =>
        {
            e.ToTable("nav_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FundId).HasColumnName("fund_id");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.TotalEquity).HasColumnName("total_equity");
            e.Property(x => x.TotalShares).HasColumnName("total_shares");
            e.Property(x => x.ShareValue).HasColumnName("share_value");
            e.Property(x => x.DailyReturn).HasColumnName("daily_return");
            e.Property(x => x.CashBalance).HasColumnName("cash_balance");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.FundId, x.Date }).IsUnique();
            e.HasOne<Fund>().WithMany().HasForeignKey(x => x.FundId);
        });

        // Price
        modelBuilder.Entity<Price>(e =>
        {
            e.ToTable("prices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Ticker).HasColumnName("ticker");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.Open).HasColumnName("open");
            e.Property(x => x.High).HasColumnName("high");
            e.Property(x => x.Low).HasColumnName("low");
            e.Property(x => x.Close).HasColumnName("close");
            e.Property(x => x.AdjClose).HasColumnName("adj_close");
            e.Property(x => x.Volume).HasColumnName("volume");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.FetchedAt).HasColumnName("fetched_at");
            e.HasIndex(x => new { x.Ticker, x.Date }).IsUnique();
        });

        // Benchmark
        modelBuilder.Entity<Benchmark>(e =>
        {
            e.ToTable("benchmarks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.Value).HasColumnName("value");
            e.Property(x => x.DailyReturn).HasColumnName("daily_return");
            e.Property(x => x.Cumulative).HasColumnName("cumulative");
            e.Property(x => x.Source).HasColumnName("source");
            e.HasIndex(x => new { x.Name, x.Date }).IsUnique();
        });

        // Metric
        modelBuilder.Entity<Metric>(e =>
        {
            e.ToTable("metrics");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FundId).HasColumnName("fund_id");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.Period).HasColumnName("period");
            e.Property(x => x.CumulativeReturn).HasColumnName("cumulative_return");
            e.Property(x => x.AnnualizedReturn).HasColumnName("annualized_return");
            e.Property(x => x.Volatility).HasColumnName("volatility");
            e.Property(x => x.SharpeRatio).HasColumnName("sharpe_ratio");
            e.Property(x => x.MaxDrawdown).HasColumnName("max_drawdown");
            e.Property(x => x.Alpha).HasColumnName("alpha");
            e.Property(x => x.Beta).HasColumnName("beta");
            e.Property(x => x.BenchmarkName).HasColumnName("benchmark_name");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.FundId, x.Date, x.Period }).IsUnique();
            e.HasOne<Fund>().WithMany().HasForeignKey(x => x.FundId);
        });

        // RealizedPnl
        modelBuilder.Entity<RealizedPnl>(e =>
        {
            e.ToTable("realized_pnl");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FundId).HasColumnName("fund_id");
            e.Property(x => x.Ticker).HasColumnName("ticker");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.EntryPrice).HasColumnName("entry_price");
            e.Property(x => x.ExitPrice).HasColumnName("exit_price");
            e.Property(x => x.Pnl).HasColumnName("pnl");
            e.Property(x => x.Side).HasColumnName("side");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.HasOne<Fund>().WithMany().HasForeignKey(x => x.FundId);
        });

        // PositionHistory
        modelBuilder.Entity<PositionHistory>(e =>
        {
            e.ToTable("position_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FundId).HasColumnName("fund_id");
            e.Property(x => x.Ticker).HasColumnName("ticker");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.AvgPrice).HasColumnName("avg_price");
            e.Property(x => x.Side).HasColumnName("side");
            e.Property(x => x.CurrentPrice).HasColumnName("current_price");
            e.Property(x => x.MarketValue).HasColumnName("market_value");
            e.Property(x => x.UnrealizedPnl).HasColumnName("unrealized_pnl");
            e.Property(x => x.DailyReturn).HasColumnName("daily_return");
            e.Property(x => x.Contribution).HasColumnName("contribution");
            e.Property(x => x.Weight).HasColumnName("weight");
            e.HasIndex(x => new { x.FundId, x.Ticker, x.Date }).IsUnique();
            e.HasOne<Fund>().WithMany().HasForeignKey(x => x.FundId);
        });

        // Asset
        modelBuilder.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.Ticker);
            e.Property(x => x.Ticker).HasColumnName("ticker");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.AssetClass).HasColumnName("asset_class");
            e.Property(x => x.Sector).HasColumnName("sector");
            e.Property(x => x.Exchange).HasColumnName("exchange");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.YahooTicker).HasColumnName("yahoo_ticker");
            e.Property(x => x.IsActive).HasColumnName("is_active");
        });
    }
}
