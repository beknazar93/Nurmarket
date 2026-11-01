using System;

namespace NurMarketKassa.Services;

/// <summary>Inline translation helper for cashier-screen strings built outside XAML (ViewModel
/// messages, dialogs) — Russian by default, Kyrgyz when selected in Settings → Экран.
/// 2026-09-07: добавлена перегрузка на все пять языков интерфейса (en/tr/uz раньше молча
/// получали русский) и событие <see cref="LanguageChanged"/> — LocalizationManager поднимает
/// его после смены словаря XAML, чтобы ViewModel-и перечитали строки, которые считаются
/// кодом (кнопка «Оплатить», плашки «Штучный/Весовой», статус каталога): раньше они
/// оставались на старом языке до следующего изменения корзины/каталога.</summary>
public static class Tr
{
    public static event Action? LanguageChanged;

    public static void NotifyLanguageChanged() => LanguageChanged?.Invoke();

    public static string T(string ru, string ky) =>
        UserPreferences.Instance.Language == AppLanguage.Kyrgyz ? ky : ru;

    public static string T(string ru, string ky, string en, string tr, string uz)
    {
        if (_captureUsers > 0)
            Track(ru, ky, en, tr, uz);
        return UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => ky,
            AppLanguage.English => en,
            AppLanguage.Turkish => tr,
            AppLanguage.Uzbek => uz,
            _ => ru,
        };
    }

    // 2026-11-01 (Р-6, живая регрессия 1.17.71): окно «Настройки кассы» собирает подписи кодом через Tr.T один раз, и после смены
    // языка внутри открытого окна они оставались русскими. Пока идёт ПОСТРОЕНИЕ окна (BeginCapture ... Dispose), Tr запоминает
    // пятёрки переводов (по каждому варианту), а окно при смене языка заменяет ими свои подписи (Retranslate). Вне построения
    // запоминание выключено (горячий путь Tr.T — одно чтение числа). Словарь ограничен (CaptureLimit, счётчик отдельный, без
    // Count) и освобождается окном при закрытии/скрытии (ClearCaptured). Ссылок на элементы интерфейса не хранит — только строки.
    private static int _captureUsers;
    private static int _capturedCount;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> Captured = new(StringComparer.Ordinal);
    private const int CaptureLimit = 5000;

    private static void Track(string ru, string ky, string en, string tr, string uz)
    {
        if (System.Threading.Volatile.Read(ref _capturedCount) >= CaptureLimit)
            return;
        string[] all = [ru, ky, en, tr, uz];
        foreach (var variant in all)
            if (variant is { Length: >= 2 } && Captured.TryAdd(variant, all))
                System.Threading.Interlocked.Increment(ref _capturedCount);
    }

    /// <summary>Освобождает запомненные переводы (окно закрыто или скрыто).</summary>
    public static void ClearCaptured()
    {
        Captured.Clear();
        System.Threading.Volatile.Write(ref _capturedCount, 0);
    }

    /// <summary>Включает запоминание переводов до Dispose — только на время построения окна, которому нужна смена языка «на месте».</summary>
    public static IDisposable BeginCapture()
    {
        System.Threading.Interlocked.Increment(ref _captureUsers);
        return new CaptureScope();
    }

    private sealed class CaptureScope : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _done, 1) != 0)
                return;
            System.Threading.Interlocked.Decrement(ref _captureUsers); // словарь остаётся для Retranslate; очищает его окно (ClearCaptured)
        }
    }

    /// <summary>Подпись, полученная раньше через Tr.T (на любом языке), на текущем языке; неизвестная — null.</summary>
    public static string? Retranslate(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Captured.TryGetValue(text, out var all))
            return null;
        var now = UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => all[1],
            AppLanguage.English => all[2],
            AppLanguage.Turkish => all[3],
            AppLanguage.Uzbek => all[4],
            _ => all[0],
        };
        return string.IsNullOrEmpty(now) || now == text ? null : now;
    }

    /// <summary>2026-10-27 (приёмка Opus 26.10, Д-8): форма русского существительного после числа — «1 строка / 2 строки / 5 строк»
    /// (one — 1, 21, 31…; few — 2–4, 22–24…; many — 0, 5–20, 25–30…). Остальные языки склонения после числа не имеют (кроме английского: один/много —
    /// см. <see cref="PluralEn"/>).</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        var abs = Math.Abs(n);
        var t = abs % 100;
        if (t is >= 11 and <= 14)
            return many;
        return (abs % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    /// <summary>Английская форма после числа: «1 line / 2 lines».</summary>
    public static string PluralEn(long n, string one, string many) => Math.Abs(n) == 1 ? one : many;
}
