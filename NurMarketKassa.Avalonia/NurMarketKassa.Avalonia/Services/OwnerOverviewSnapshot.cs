using System.Collections.Generic;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-05: цифры «Сводки» программы владельца (отчёт сервера NurCRM) — для «ИИ-советника». Живой
/// случай на тестовом аккаунте: советник по локальной истории продаж назвал выручку за 7 дней 1 353 572 сом,
/// а «Сводка» (сервер) — 886 049 сом. Сводка публикует сюда свои карточки, график по дням и лучшие товары;
/// советник берёт выручку отсюда, а не из локальной истории.</summary>
public static class OwnerOverviewSnapshot
{
    private static readonly object Gate = new();
    private static readonly SortedDictionary<string, string> Parts = new();

    public static void Set(string key, string text)
    {
        lock (Gate)
            Parts[key] = text;
    }

    /// <summary>Текст для нейросети или null, если «Сводка» ещё не загружалась.</summary>
    public static string? Text
    {
        get
        {
            lock (Gate)
                return Parts.Count == 0 ? null : string.Join("\n", Parts.Values);
        }
    }
}
