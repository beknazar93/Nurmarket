namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-06 (Б-8, живой тест Этапа 1): «Сессия истекла» открывалось поверх ещё не закрытого фонового анонса («Просрочка при продаже») — две модалки сразу
/// (журнал: «PosConfirmDialog, владелец PosConfirmDialog, модалок 2»). Общее правило для ФОНОВЫХ информационных окон (дежурный оповещений, подсказки, утренний отчёт и т. п.):
/// <list type="bullet">
/// <item>пока открыто более важное окно («Сессия истекла», «Вход скоро истечёт») или сессия потеряна и вопрос вот-вот появится — фоновый анонс ОТКЛАДЫВАЕТСЯ (ждёт, не теряется);</item>
/// <item>пока открыт фоновый анонс, окна «Сессия истекла» / «Вход скоро истечёт» сами откладываются на несколько секунд (<see cref="BackgroundOpen"/>) и появляются сразу после его закрытия.</item>
/// </list>
/// Окно более высокого приоритета в начале и конце показа вызывает <see cref="BeginImportant"/> (Dispose — окно закрыто).
/// </summary>
public static class ModalAnnouncements
{
    private static int _important;
    private static int _background;

    /// <summary>Сколько ждать между проверками.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Дольше этого анонс не откладываем — показываем (6 часов: рабочая смена; «не терять», но и не висеть вечно).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(6);

    /// <summary>Сейчас открыт фоновый анонс — более важные окна ждут его закрытия.</summary>
    public static bool BackgroundOpen => Volatile.Read(ref _background) > 0;

    /// <summary>Открыто важное окно (его показ начал <see cref="BeginImportant"/>).</summary>
    public static bool ImportantShowing => Volatile.Read(ref _important) > 0;

    /// <summary>Фоновый анонс должен подождать: важное окно открыто, либо сессия потеряна (вопрос «Войдите снова» появится сам — не мешаем ему).</summary>
    public static bool MustWait => ImportantShowing || SessionExpiryMonitor.IsExpired;

    /// <summary>Окно более высокого приоритета открыто — Dispose при закрытии.</summary>
    public static IDisposable BeginImportant()
    {
        Interlocked.Increment(ref _important);
        return new Scope(() => Interlocked.Decrement(ref _important));
    }

    /// <summary>Показать фоновый анонс, когда нет более важного окна. Ждёт (опрос раз в <see cref="PollInterval"/>, не дольше <see cref="MaxWait"/>), пока <paramref name="mustWait"/> (по умолчанию
    /// <see cref="MustWait"/>) не станет false; на время показа учитывается как открытый фоновый анонс. Исключения показа уходят вызывающему.</summary>
    /// <param name="what">Название анонса — для одной строки в журнале при откладывании.</param>
    public static async Task<T> ShowWhenFreeAsync<T>(Func<Task<T>> show, string what, Func<bool>? mustWait = null, TimeSpan? poll = null, TimeSpan? maxWait = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(show);
        var wait = mustWait ?? (() => MustWait);
        var step = poll ?? PollInterval;
        var limit = maxWait ?? MaxWait;
        if (wait())
        {
            PosLogger.Log($"Анонс «{what}» отложен: открыто более важное окно или сессия потеряна — покажу, как только освободится.", "UI");
            var started = DateTime.UtcNow;
            while (wait() && DateTime.UtcNow - started < limit)
                await Task.Delay(step, ct).ConfigureAwait(true);
        }

        Interlocked.Increment(ref _background);
        try
        {
            return await show().ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Decrement(ref _background);
        }
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                onDispose();
        }
    }
}
