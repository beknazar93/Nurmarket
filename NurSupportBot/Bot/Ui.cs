using System.Globalization;
using NurSupportBot.Telegram;

namespace NurSupportBot.Bot;

/// <summary>Мелочи оформления: экранирование, деньги, типовые кнопки.</summary>
public static class Ui
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Экранирование для parse_mode=HTML: Telegram требует заменять только &amp;, &lt; и &gt;.
    /// WebUtility.HtmlEncode превращал «ёлочки» и эмодзи в числовые коды — работало, но раздувало текст.</summary>
    public static string H(string? text) => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Money(decimal value) => value.ToString("N2", Ru);

    public static Button Menu => Button.Cb("🏠 Главное меню", "m");
    public static Button Operator(long? nodeId = null) => Button.Cb("🆘 Связаться с оператором", nodeId is { } id ? $"op:{id}" : "op");
    public static Button Back(string data) => Button.Cb("⬅️ Назад", data);

    /// <summary>Часовой пояс клиентов — Бишкек (UTC+6); сервер может жить в UTC.</summary>
    public static DateTime Today
    {
        get
        {
            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Bishkek");
            }
            catch (Exception)
            {
                zone = TimeZoneInfo.CreateCustomTimeZone("UTC+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");
            }
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        }
    }
}
