using Microsoft.EntityFrameworkCore;
using Npgsql;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Database
// Heroku: DATABASE_URL from Heroku Postgres
// Local: database/pucfinance.db at the repository root
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var postgresConnectionString = GetPostgresConnectionString();
    if (!string.IsNullOrWhiteSpace(postgresConnectionString))
    {
        options.UseNpgsql(postgresConnectionString);
        return;
    }

    var dbPath = GetSqlitePath();
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    options.UseSqlite($"Data Source={dbPath}");
});

// Services
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<TradeService>();
builder.Services.AddScoped<NavCalculator>();
builder.Services.AddScoped<MetricsCalculator>();
builder.Services.AddScoped<BatchService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<CdiService>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddSingleton<AuthTokenService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddScoped<FundAccessService>();
builder.Services.AddHttpClient();

// API
builder.Services.AddAuthentication(SimpleBearerAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SimpleBearerAuthenticationHandler>(
        SimpleBearerAuthenticationHandler.SchemeName,
        options => { });
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "PUC Finance - Asset Management", Version = "v1" });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// Railway injects PORT dynamically
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await InitializeDatabaseAsync(db, app.Logger);
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("AllowAll");

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("index.html");

app.Run();

string GetRepositoryRoot()
{
    var candidates = new[]
    {
        builder.Environment.ContentRootPath,
        Path.Combine(builder.Environment.ContentRootPath, "..", ".."),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."),
        AppContext.BaseDirectory
    };

    foreach (var candidate in candidates)
    {
        var fullPath = Path.GetFullPath(candidate);
        if (File.Exists(Path.Combine(fullPath, "database", "schema.sql")))
        {
            return fullPath;
        }
    }

    return Path.GetFullPath(builder.Environment.ContentRootPath);
}

async Task InitializeDatabaseAsync(AppDbContext db, ILogger logger)
{
    await db.Database.EnsureCreatedAsync();
    await DatabaseSeeder.SeedAsync(db, logger);
}

string GetSqlitePath()
{
    var dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH");
    if (string.IsNullOrWhiteSpace(dbPath))
    {
        dbPath = Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT") != null
            ? "/data/pucfinance.db"
            : Path.Combine(GetRepositoryRoot(), "database", "pucfinance.db");
    }

    return Path.GetFullPath(dbPath);
}

string? GetPostgresConnectionString()
{
    var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(connectionString))
        return connectionString;

    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (string.IsNullOrWhiteSpace(databaseUrl))
        return null;

    var databaseUri = new Uri(databaseUrl);
    var userInfo = databaseUri.UserInfo.Split(':', 2);
    var connectionBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = databaseUri.Host,
        Port = databaseUri.Port > 0 ? databaseUri.Port : 5432,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
        Database = databaseUri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Require
    };

    return connectionBuilder.ConnectionString;
}
