using System.Globalization;

namespace PUCFinance.AssetManagement.Services;

/// <summary>
/// Horario de Brasilia e dias uteis nacionais (fins de semana e feriados nacionais, como no calendario
/// da ANBIMA). Usado no agendamento do batch e no horario de corte das ordens de fundos.
/// </summary>
public static class BrazilTime
{
    /// <summary>Fuso de Brasilia; se o sistema nao tiver a base de fusos, UTC-3 fixo (sem horario de verao desde 2019).</summary>
    public static readonly TimeZoneInfo Zone = FindZone();

    /// <summary>Data e hora atuais em Brasilia.</summary>
    public static DateTime Now => ToLocal(DateTimeOffset.UtcNow);

    public static DateTime ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).DateTime;

    /// <summary>Instante de uma data e hora de Brasilia.</summary>
    public static DateTimeOffset FromLocal(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    public static bool IsBusinessDay(DateTime date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(date.Date);

    /// <summary>Primeiro dia util depois de <paramref name="date"/>.</summary>
    public static DateTime NextBusinessDay(DateTime date)
    {
        var day = date.Date.AddDays(1);
        while (!IsBusinessDay(day))
            day = day.AddDays(1);
        return day;
    }

    /// <summary>
    /// Feriados nacionais: datas fixas (Consciencia Negra desde 2024) e moveis pela Pascoa
    /// (Carnaval, Sexta-feira Santa e Corpus Christi).
    /// </summary>
    public static bool IsHoliday(DateTime date)
    {
        var fixedHoliday = (date.Month, date.Day) switch
        {
            (1, 1) or (4, 21) or (5, 1) or (9, 7) or (10, 12) or (11, 2) or (11, 15) or (12, 25) => true,
            (11, 20) => date.Year >= 2024,
            _ => false,
        };
        if (fixedHoliday)
            return true;

        var easter = Easter(date.Year);
        var offset = (date.Date - easter).Days;
        return offset is -48 or -47 or -2 or 60;
    }

    public static string ToIsoDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Domingo de Pascoa (algoritmo de Meeus/Jones/Butcher, calendario gregoriano).</summary>
    private static DateTime Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception)
            {
                // tenta o proximo
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasilia", "BRT");
    }
}
