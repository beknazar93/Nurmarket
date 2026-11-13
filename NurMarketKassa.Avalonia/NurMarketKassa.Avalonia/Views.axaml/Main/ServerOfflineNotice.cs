using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-31 (стресс-тест 1.17.68, п. 9): при сбое сервера отчёты молча показывали нули или «продаж нет». Общая плашка
/// «Сервер NurCRM недоступен — данные не получены» с кнопкой «Повторить» (5 языков).</summary>
internal static class ServerOfflineNotice
{
    /// <summary>2026-11-01 (В-08): одна фраза о сбое сервера (ServerOutageMonitor.UnavailableText) вместо «Нет связи с сервером».</summary>
    public static string Text => ServerOutageMonitor.UnavailableText + " — " + Tr.T("данные не получены. Проверьте интернет и повторите.",
        "маалымат алынган жок. Интернетти текшерип, кайталаңыз.", "data was not received. Check the internet and retry.",
        "veri alınamadı. İnterneti kontrol edip tekrar deneyin.", "ma'lumot olinmadi. Internetni tekshirib, qayta urinib ko'ring.");

    public static string RetryText => StateBlock.RetryText;

    /// <summary>Плашка «Сервер NurCRM недоступен» + «Повторить». retry вызывается по нажатию.
    /// 2026-11-12 (оценка UX/UI, Д-2): теперь это общий <see cref="StateBlock"/> (вариант «нет связи»), а не отдельная карточка в углу.</summary>
    public static Control Build(Control host, Action? retry) => StateBlock.Offline(host, retry);
}
