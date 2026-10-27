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

    public static string T(string ru, string ky, string en, string tr, string uz) =>
        UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => ky,
            AppLanguage.English => en,
            AppLanguage.Turkish => tr,
            AppLanguage.Uzbek => uz,
            _ => ru,
        };

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
