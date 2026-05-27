using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Data;
using PUCFinance.AssetManagement.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ────────────────────────────────────────────
var dbPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "database", "pucfinance.db");
dbPath = Path.GetFullPath(dbPath);

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

// ── CORS (React dev server) ─────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy =>
    {
        policy.WithOrigins("http://localhost:3000", "http://localhost:5173")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

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

app.UseCors("DevCors");
app.MapControllers();

app.Run();
