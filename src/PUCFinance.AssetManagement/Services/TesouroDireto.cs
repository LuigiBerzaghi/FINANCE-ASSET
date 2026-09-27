using System.Globalization;
using System.Text.RegularExpressions;

namespace PUCFinance.AssetManagement.Services;

/// <summary>Tipo de titulo publico: codigo interno, nome no Tesouro Direto e regras de fluxo.</summary>
public sealed record TreasuryBondType(
    string Code,
    string Name,
    TreasuryCouponRule Coupon,
    TreasuryRedemptionRule Redemption);

public enum TreasuryCouponRule
{
    None,
    /// <summary>NTN-F: 10% a.a. sobre R$ 1.000, pago em 1/jan e 1/jul.</summary>
    Prefixed,
    /// <summary>NTN-B: 6% a.a. sobre o VNA corrigido pelo IPCA, pago no dia 15 do mes de vencimento e 6 meses depois.</summary>
    Ipca,
    /// <summary>NTN-C: sobre o VNA corrigido pelo IGP-M, pago em 1/jan e 1/jul (2031: 12% a.a.; demais: 6% a.a.).</summary>
    Igpm,
}

public enum TreasuryRedemptionRule
{
    /// <summary>R$ 1.000 no vencimento (LTN, NTN-F).</summary>
    Par,
    /// <summary>VNA corrigido pelo IPCA (NTN-B e NTN-B Principal).</summary>
    IpcaVna,
    /// <summary>VNA corrigido pelo IGP-M (NTN-C).</summary>
    IgpmVna,
    /// <summary>Ultimo PU publicado ate o vencimento (LFT).</summary>
    LastPrice,
    /// <summary>Titulos que viram fluxo mensal (Educa+, Renda+): sem resgate automatico.</summary>
    Unsupported,
}

/// <summary>Titulo identificado: tipo + data de vencimento.</summary>
public sealed record TreasuryBond(TreasuryBondType Type, DateTime Maturity)
{
    public string Ticker => TesouroDireto.BuildTicker(Type.Code, Maturity);
    public string DisplayName => $"{Type.Name} {Maturity.Year}";
}

/// <summary>
/// Regras dos titulos do Tesouro Direto. Ticker interno = codigo + mes + ano do vencimento,
/// ex.: NTNB-MAI2035 (Tesouro IPCA+ com Juros Semestrais 15/05/2035), LTN-JAN2029.
/// </summary>
public static class TesouroDireto
{
    public const string AssetClass = "fixed_income";
    public const string Exchange = "Tesouro Direto";

    /// <summary>Menor fracao negociavel: 0,01 titulo.</summary>
    public const double QuantityStep = 0.01;

    public static readonly IReadOnlyList<TreasuryBondType> Types =
    [
        new("LTN", "Tesouro Prefixado", TreasuryCouponRule.None, TreasuryRedemptionRule.Par),
        new("NTNF", "Tesouro Prefixado com Juros Semestrais", TreasuryCouponRule.Prefixed, TreasuryRedemptionRule.Par),
        new("NTNBP", "Tesouro IPCA+", TreasuryCouponRule.None, TreasuryRedemptionRule.IpcaVna),
        new("NTNB", "Tesouro IPCA+ com Juros Semestrais", TreasuryCouponRule.Ipca, TreasuryRedemptionRule.IpcaVna),
        new("LFT", "Tesouro Selic", TreasuryCouponRule.None, TreasuryRedemptionRule.LastPrice),
        new("NTNC", "Tesouro IGPM+ com Juros Semestrais", TreasuryCouponRule.Igpm, TreasuryRedemptionRule.IgpmVna),
        new("EDUCA", "Tesouro Educa+", TreasuryCouponRule.None, TreasuryRedemptionRule.Unsupported),
        new("RENDA", "Tesouro Renda+ Aposentadoria Extra", TreasuryCouponRule.None, TreasuryRedemptionRule.Unsupported),
    ];

    private static readonly string[] Months = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];

    private static readonly Regex TickerPattern = new(
        "^(?<code>LTN|NTNF|NTNBP|NTNB|LFT|NTNC|EDUCA|RENDA)-(?<month>JAN|FEV|MAR|ABR|MAI|JUN|JUL|AGO|SET|OUT|NOV|DEZ)(?<year>\\d{4})$",
        RegexOptions.Compiled);

    public static TreasuryBondType? FindTypeByName(string name) =>
        Types.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static TreasuryBondType? FindTypeByCode(string code) =>
        Types.FirstOrDefault(t => t.Code == code);

    public static string BuildTicker(string code, DateTime maturity) =>
        $"{code}-{Months[maturity.Month - 1]}{maturity.Year}";

    public static bool IsTreasuryTicker(string? ticker) =>
        ticker != null && TickerPattern.IsMatch(ticker.Trim().ToUpperInvariant());

    /// <summary>Tipo, mes e ano de um ticker (o dia vem do CSV do Tesouro).</summary>
    public static (TreasuryBondType Type, int Month, int Year)? ParseTicker(string ticker)
    {
        var match = TickerPattern.Match(ticker.Trim().ToUpperInvariant());
        if (!match.Success)
            return null;

        var type = FindTypeByCode(match.Groups["code"].Value)!;
        var month = Array.IndexOf(Months, match.Groups["month"].Value) + 1;
        return (type, month, int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Datas de pagamento de cupom ate o vencimento (inclusive).</summary>
    public static IEnumerable<DateTime> CouponDates(TreasuryBond bond, DateTime from, DateTime to)
    {
        if (bond.Type.Coupon == TreasuryCouponRule.None)
            yield break;

        var last = to.Date < bond.Maturity.Date ? to.Date : bond.Maturity.Date;
        var day = bond.Type.Coupon == TreasuryCouponRule.Ipca ? 15 : 1;
        var months = bond.Type.Coupon == TreasuryCouponRule.Ipca
            ? new[] { bond.Maturity.Month, (bond.Maturity.Month + 5) % 12 + 1 }
            : new[] { 1, 7 };

        for (var year = from.Year; year <= last.Year; year++)
        {
            foreach (var month in months.OrderBy(m => m))
            {
                var date = new DateTime(year, month, day);
                if (date > from.Date && date <= last)
                    yield return date;
            }
        }
    }

    /// <summary>Taxa de cupom semestral (fracao do valor nominal).</summary>
    public static double SemiannualCouponRate(TreasuryBond bond) => bond.Type.Coupon switch
    {
        TreasuryCouponRule.Prefixed => Math.Sqrt(1.10) - 1,
        TreasuryCouponRule.Ipca => Math.Sqrt(1.06) - 1,
        TreasuryCouponRule.Igpm => bond.Maturity.Year == 2031 ? Math.Sqrt(1.12) - 1 : Math.Sqrt(1.06) - 1,
        _ => 0,
    };

    public static string ToIsoDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
