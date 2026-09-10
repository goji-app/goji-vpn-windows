using System.Globalization;

namespace GodjiVpn.Utils;

public static class DateFormat
{
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>"2026-09-05T16:11:51Z" → "5 сентября". Возвращает исходную строку, если
    /// распарсить не удалось. Портировано 1:1 из util/DateFormat.kt.</summary>
    public static string FormatDate(string isoDateTime)
    {
        if (!DateTimeOffset.TryParse(isoDateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return isoDateTime;
        return parsed.ToLocalTime().ToString("d MMMM", RuCulture);
    }
}
