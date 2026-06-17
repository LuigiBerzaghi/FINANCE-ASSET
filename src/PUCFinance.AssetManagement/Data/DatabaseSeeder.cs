using Microsoft.EntityFrameworkCore;
using PUCFinance.AssetManagement.Models;

namespace PUCFinance.AssetManagement.Data;

public static class DatabaseSeeder
{
    public static async Task SeedAsync(AppDbContext db, ILogger logger)
    {
        await SeedFundsAsync(db);
        await SeedAssetsAsync(db);

        await db.SaveChangesAsync();
        logger.LogInformation("Database seed verified.");
    }

    private static async Task SeedFundsAsync(AppDbContext db)
    {
        if (!await db.Funds.AnyAsync())
        {
            db.Funds.AddRange(
                new Fund { Name = "Alpha", Strategy = "Long Only", InitialCapital = 1_000_000, TotalShares = 1_000_000 },
                new Fund { Name = "Beta", Strategy = "Long/Short", InitialCapital = 1_000_000, TotalShares = 1_000_000 },
                new Fund { Name = "Gamma", Strategy = "Macro", InitialCapital = 1_000_000, TotalShares = 1_000_000 }
            );

            await db.SaveChangesAsync();
        }

        var funds = await db.Funds.ToListAsync();
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        foreach (var fund in funds)
        {
            if (!await db.Cash.AnyAsync(c => c.FundId == fund.Id))
            {
                db.Cash.Add(new Cash
                {
                    FundId = fund.Id,
                    Balance = fund.InitialCapital
                });
            }

            if (!await db.NavHistory.AnyAsync(n => n.FundId == fund.Id))
            {
                db.NavHistory.Add(new NavHistory
                {
                    FundId = fund.Id,
                    Date = today,
                    TotalEquity = fund.InitialCapital,
                    TotalShares = fund.TotalShares,
                    ShareValue = fund.TotalShares > 0 ? fund.InitialCapital / fund.TotalShares : 0,
                    DailyReturn = 0,
                    CashBalance = fund.InitialCapital
                });
            }
        }
    }

    private static async Task SeedAssetsAsync(AppDbContext db)
    {
        var existingTickers = (await db.Assets
            .Select(a => a.Ticker)
            .ToListAsync())
            .ToHashSet();

        foreach (var asset in Assets)
        {
            if (existingTickers.Contains(asset.Ticker))
                continue;

            db.Assets.Add(new Asset
            {
                Ticker = asset.Ticker,
                Name = asset.Name,
                AssetClass = asset.AssetClass,
                Sector = asset.Sector,
                Exchange = asset.Exchange,
                Currency = asset.Currency,
                YahooTicker = asset.YahooTicker,
                IsActive = 1
            });
        }
    }

    private sealed record SeedAsset(
        string Ticker,
        string Name,
        string AssetClass,
        string Sector,
        string Exchange,
        string Currency,
        string? YahooTicker = null);

    private static readonly SeedAsset[] Assets =
    [
        new("PETR4", "Petrobras PN", "equity_br", "Petroleo e Gas", "B3", "BRL"),
        new("VALE3", "Vale ON", "equity_br", "Mineracao", "B3", "BRL"),
        new("ITUB4", "Itau Unibanco PN", "equity_br", "Bancos", "B3", "BRL"),
        new("BBDC4", "Bradesco PN", "equity_br", "Bancos", "B3", "BRL"),
        new("BBAS3", "Banco do Brasil ON", "equity_br", "Bancos", "B3", "BRL"),
        new("WEGE3", "WEG ON", "equity_br", "Bens Industriais", "B3", "BRL"),
        new("ABEV3", "Ambev ON", "equity_br", "Bebidas", "B3", "BRL"),
        new("RENT3", "Localiza ON", "equity_br", "Aluguel de Carros", "B3", "BRL"),
        new("MGLU3", "Magazine Luiza ON", "equity_br", "Varejo", "B3", "BRL"),
        new("SUZB3", "Suzano ON", "equity_br", "Papel e Celulose", "B3", "BRL"),
        new("JBSS3", "JBS ON", "equity_br", "Alimentos", "B3", "BRL"),
        new("ELET3", "Eletrobras ON", "equity_br", "Energia Eletrica", "B3", "BRL"),
        new("B3SA3", "B3 ON", "equity_br", "Servicos Financeiros", "B3", "BRL"),
        new("RDOR3", "Rede D Or ON", "equity_br", "Saude", "B3", "BRL"),
        new("EMBR3", "Embraer ON", "equity_br", "Aeronautica", "B3", "BRL"),
        new("CYRE3", "Cyrela ON", "equity_br", "Construcao", "B3", "BRL"),
        new("VIVT3", "Telefonica Brasil ON", "equity_br", "Telecomunicacoes", "B3", "BRL"),
        new("HAPV3", "Hapvida ON", "equity_br", "Saude", "B3", "BRL"),
        new("CSAN3", "Cosan ON", "equity_br", "Energia", "B3", "BRL"),
        new("PRIO3", "PRIO ON", "equity_br", "Petroleo e Gas", "B3", "BRL"),

        new("BOVA11", "iShares Ibovespa", "etf", "Indice", "B3", "BRL"),
        new("IVVB11", "iShares S&P 500 BRL", "etf", "Indice", "B3", "BRL"),
        new("SMAL11", "iShares Small Cap", "etf", "Indice", "B3", "BRL"),
        new("HASH11", "Hashdex Crypto", "etf", "Cripto", "B3", "BRL"),
        new("IMAB11", "Itau IMA-B", "etf", "Renda Fixa", "B3", "BRL"),

        new("AAPL", "Apple", "equity_us", "Tecnologia", "NASDAQ", "USD", "AAPL"),
        new("MSFT", "Microsoft", "equity_us", "Tecnologia", "NASDAQ", "USD", "MSFT"),
        new("NVDA", "NVIDIA", "equity_us", "Tecnologia", "NASDAQ", "USD", "NVDA"),
        new("AMZN", "Amazon", "equity_us", "Tecnologia", "NASDAQ", "USD", "AMZN"),
        new("GOOGL", "Alphabet", "equity_us", "Tecnologia", "NASDAQ", "USD", "GOOGL"),
        new("TSLA", "Tesla", "equity_us", "Automotivo", "NASDAQ", "USD", "TSLA"),
        new("META", "Meta Platforms", "equity_us", "Tecnologia", "NASDAQ", "USD", "META"),
        new("JPM", "JPMorgan Chase", "equity_us", "Bancos", "NYSE", "USD", "JPM"),

        new("USDBRL", "Dolar/Real", "fx", "Cambio", "FX", "BRL", "USDBRL=X"),
        new("EURBRL", "Euro/Real", "fx", "Cambio", "FX", "BRL", "EURBRL=X"),
        new("EURUSD", "Euro/Dolar", "fx", "Cambio", "FX", "USD", "EURUSD=X"),
        new("USDJPY", "Dolar/Iene", "fx", "Cambio", "FX", "JPY", "JPY=X"),

        new("CL", "Petroleo WTI", "commodity", "Energia", "NYMEX", "USD", "CL=F"),
        new("GC", "Ouro", "commodity", "Metais Preciosos", "COMEX", "USD", "GC=F"),
        new("SI", "Prata", "commodity", "Metais Preciosos", "COMEX", "USD", "SI=F"),
        new("NG", "Gas Natural", "commodity", "Energia", "NYMEX", "USD", "NG=F"),
        new("ZS", "Soja", "commodity", "Agricultura", "CBOT", "USD", "ZS=F"),
        new("ZC", "Milho", "commodity", "Agricultura", "CBOT", "USD", "ZC=F"),

        new("BTC", "Bitcoin", "crypto", "Cripto", "Global", "USD", "BTC-USD"),
        new("ETH", "Ethereum", "crypto", "Cripto", "Global", "USD", "ETH-USD"),
        new("SOL", "Solana", "crypto", "Cripto", "Global", "USD", "SOL-USD")
    ];
}
