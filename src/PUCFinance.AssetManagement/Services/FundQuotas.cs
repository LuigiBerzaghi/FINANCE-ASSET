using System.Globalization;
using System.Text.RegularExpressions;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Cotas de fundos de investimento abertos, precificadas pelo informe diario da CVM. Ticker interno =
/// FI- + CNPJ da classe (14 digitos), mais o codigo da subclasse quando a cota e de uma subclasse:
/// FI-07593972000186, FI-00888897000131-MZMRC1747322915.
/// Cotas nao sao negociadas na hora: aplicacao e resgate viram ordens (FundOrderService) executadas pela
/// cota do dia da ordem quando a CVM a publica (cerca de 2 dias uteis depois). Assim ninguem compra ou
/// vende sabendo o preco, como nos fundos reais.
/// </summary>
public static class FundQuotas
{
    public const string AssetClass = "fund";
    public const string Exchange = "CVM";
    public const string TickerPrefix = "FI-";

    /// <summary>Cotas sao fracionarias: menor quantidade de 0,00000001 cota.</summary>
    public const double QuantityStep = 0.00000001;

    /// <summary>Horario de corte (Brasilia): ordens ate 14h entram na cota do dia; depois, na do proximo dia util.</summary>
    public const int CutoffHour = 14;

    /// <summary>
    /// Ordem sem cota publicada e recusada quando a CVM ja publicou cotas (de outros fundos) tantos dias
    /// corridos depois da data da cota (fundo que parou de informar a cota): libera o caixa reservado.
    /// </summary>
    public const int ExpireAfterDays = 15;

    /// <summary>
    /// A cota do dia D entra no arquivo da CVM publicado de madrugada em D+2 (dias uteis): e a cota que a
    /// carteira conhece nesse dia. O historico usado para preencher dias passados segue o mesmo atraso.
    /// </summary>
    public const int PublicationLagDays = 2;

    private static readonly Regex TickerPattern = new(
        "^FI-(?<cnpj>\\d{14})(?:-(?<sub>[A-Z0-9]{1,40}))?$",
        RegexOptions.Compiled);

    public static bool IsFundTicker(string? ticker) =>
        ticker != null && TickerPattern.IsMatch(ticker.Trim().ToUpperInvariant());

    public static string BuildTicker(string cnpjDigits, string? subclass) =>
        string.IsNullOrWhiteSpace(subclass)
            ? TickerPrefix + cnpjDigits
            : $"{TickerPrefix}{cnpjDigits}-{subclass.Trim().ToUpperInvariant()}";

    /// <summary>CNPJ (so digitos) e subclasse de um ticker de fundo; null se o ticker nao e de fundo.</summary>
    public static (string Cnpj, string? Subclass)? ParseTicker(string ticker)
    {
        var match = TickerPattern.Match(ticker.Trim().ToUpperInvariant());
        if (!match.Success)
            return null;

        var subclass = match.Groups["sub"];
        return (match.Groups["cnpj"].Value, subclass.Success ? subclass.Value : null);
    }

    public static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());

    public static string FormatCnpj(string digits) =>
        digits.Length == 14
            ? $"{digits[..2]}.{digits[2..5]}.{digits[5..8]}/{digits[8..12]}-{digits[12..]}"
            : digits;

    /// <summary>
    /// Data da cota de uma ordem feita em <paramref name="instant"/>: o proprio dia se for dia util antes do
    /// horario de corte (Brasilia); senao, o proximo dia util.
    /// </summary>
    public static DateTime QuoteDateFor(DateTimeOffset instant)
    {
        var local = BrazilTime.ToLocal(instant);
        return BrazilTime.IsBusinessDay(local.Date) && local.Hour < CutoffHour
            ? local.Date
            : BrazilTime.NextBusinessDay(local.Date);
    }

    /// <summary>Dia em que a cota de <paramref name="quoteDate"/> passa a ser conhecida (publicada pela CVM).</summary>
    public static string KnownOn(string quoteDate)
    {
        var day = ParseDate(quoteDate);
        for (var i = 0; i < PublicationLagDays; i++)
            day = BrazilTime.NextBusinessDay(day);
        return BrazilTime.ToIsoDate(day);
    }

    /// <summary>Ate quando a ordem pode ser cancelada pelo gestor: o horario de corte do dia da cota.</summary>
    public static DateTimeOffset CancelDeadline(string quoteDate) =>
        BrazilTime.FromLocal(ParseDate(quoteDate).AddHours(CutoffHour));

    public static DateTime ParseDate(string isoDate) =>
        DateTime.ParseExact(isoDate[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
