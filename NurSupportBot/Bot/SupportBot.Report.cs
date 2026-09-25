using NurSupportBot.Data;
using NurSupportBot.Services;
using NurSupportBot.Telegram;
using static NurSupportBot.Bot.Ui;

namespace NurSupportBot.Bot;

public sealed partial class SupportBot
{
    private static InlineKeyboard PeriodKeyboard => new InlineKeyboard()
        .Row(Button.Cb("Сегодня", "rep:d"), Button.Cb("Вчера", "rep:y"))
        .Row(Button.Cb("Неделя", "rep:w"), Button.Cb("Месяц", "rep:m"))
        .Row(Menu);

    private async Task ShowReportMenuAsync(long chat, long? editMsgId, BotUser user, CancellationToken ct)
    {
        if (!user.LoggedIn)
        {
            await ShowAsync(chat, editMsgId,
                "📊 Финансовый отчёт берётся с сервера NurCRM, поэтому сначала войдите с логином NurCRM. Пароль бот не хранит.",
                new InlineKeyboard().Row(Button.Cb("🔐 Войти в NurCRM", "login")).Row(Menu), ct).ConfigureAwait(false);
            return;
        }
        await ShowAsync(chat, editMsgId,
            $"📊 <b>Финансовый отчёт</b> · {H(user.CompanyName)}\n\nЗа какой период? Цифры те же, что в аналитике на сайте NurCRM.",
            PeriodKeyboard, ct).ConfigureAwait(false);
    }

    private async Task SendReportAsync(long chat, long tgId, string period, CancellationToken ct)
    {
        var user = _db.GetUser(tgId)!;
        var refresh = _vault.Decrypt(user.RefreshEnc);
        if (refresh == null)
        {
            await ShowReportMenuAsync(chat, null, user, ct).ConfigureAwait(false);
            return;
        }

        // Неделя — с понедельника, месяц — с 1-го числа: так же считает касса.
        var today = Today;
        var (from, to, label) = period switch
        {
            "y" => (today.AddDays(-1), today.AddDays(-1), "вчера, " + today.AddDays(-1).ToString("dd.MM.yyyy", Ru)),
            "w" => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today, "неделя"),
            "m" => (new DateTime(today.Year, today.Month, 1), today, "месяц"),
            _ => (today, today, "сегодня, " + today.ToString("dd.MM.yyyy", Ru)),
        };
        if (period is "w" or "m")
            label += $" ({from:dd.MM} — {to:dd.MM.yyyy})";

        try
        {
            var access = await _crm.RefreshAsync(refresh, ct).ConfigureAwait(false);
            if (access == null)
            {
                _db.Logout(tgId);
                await _tg.SendMessageAsync(chat, "Вход в NurCRM устарел — войдите заново.",
                    new InlineKeyboard().Row(Button.Cb("🔐 Войти в NurCRM", "login")).Row(Menu), ct: ct).ConfigureAwait(false);
                return;
            }

            var r = await _crm.SalesReportAsync(access, from, to, ct).ConfigureAwait(false);
            _db.Log(tgId, "report", query: period);
            var html = $"""
                📊 <b>{H(user.CompanyName)}</b> · {H(label)}

                💰 Выручка: <b>{Money(r.Revenue)} сом</b>
                🧾 Чеков: {r.Receipts} · средний чек {Money(r.AvgReceipt)}
                💵 Наличные: {Money(r.Cash)}
                💳 Безналичные: {Money(r.NonCash)}
                ↩️ Возвраты: {Money(r.Returns)} ({r.ReturnsCount})
                """;
            if (r.Profit is { } profit)
                html += $"\n📈 Прибыль: {Money(profit)}" + (r.Margin is { } margin ? $" ({margin.ToString("0.#", Ru)} %)" : "");
            await _tg.SendMessageAsync(chat, html, PeriodKeyboard, ct: ct).ConfigureAwait(false);
        }
        catch (NurCrmException ex)
        {
            if (ex.Status == System.Net.HttpStatusCode.Unauthorized)
                _db.Logout(tgId);
            await _tg.SendMessageAsync(chat, "❌ " + H(ex.Message), PeriodKeyboard, ct: ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await _tg.SendMessageAsync(chat, "❌ Сервер NurCRM сейчас недоступен. Попробуйте чуть позже.", PeriodKeyboard, ct: ct)
                .ConfigureAwait(false);
        }
    }
}
