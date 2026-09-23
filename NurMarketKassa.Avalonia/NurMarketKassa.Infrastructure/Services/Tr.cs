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
}
