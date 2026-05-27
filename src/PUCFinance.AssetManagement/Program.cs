using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ────────────────────────────────────────────
// Railway: usa /data/pucfinance.db (volume persistente)
// Local: usa database/pucfinance.db relativo ao projeto
string dbPath;
if (Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT") != null)
{
    dbPath = "/data/pucfinance.db";
}
else
{
    dbPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "database", "pucfinance.db");
    dbPath = Path.GetFullPath(dbPath);
}

Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// ── Services ────────────────────────────────────────────
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<TradeService>();
builder.Services.AddScoped<NavCalculator>();
builder.Services.AddScoped<MetricsCalculator>();
builder.Services.AddScoped<BatchService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<CdiService>();
builder.Services.AddHttpClient();

// ── API ─────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "PUC Finance - Asset Management", Version = "v1" });
});

// ── CORS ────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// ── Railway: porta dinâmica ─────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// ── Inicializa o banco ──────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// ── Middleware ───────────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("AllowAll");

// Serve o frontend buildado (wwwroot/)
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// Fallback: qualquer rota que nao seja /api vai pro index.html do React
app.MapFallbackToFile("index.html");

app.Run();
