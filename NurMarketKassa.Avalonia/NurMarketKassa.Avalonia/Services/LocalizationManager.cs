using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Свап словаря строк интерфейса кассира в рантайме — тот же механизм, что и переключение
/// темы (Application.Resources.MergedDictionaries), но с ручной заменой словаря, так как
/// Avalonia ThemeDictionaries поддерживают только ось Dark/Light, а не язык.
/// </summary>
public static class LocalizationManager
{
    private static ResourceInclude? _current;
    // 2026-11-01 (аудит «10 из 10», Д-12): словарь языка разбирался из axaml при КАЖДОЙ смене языка, а замена шла двумя изменениями
    // (снять + добавить = два пробуждения всех привязок DynamicResource во всех окнах). Теперь разобранные словари запоминаются,
    // а замена — одним изменением (по индексу).
    private static readonly Dictionary<AppLanguage, ResourceInclude> Cache = new();

    public static void Apply(AppLanguage language)
    {
        var app = Application.Current;
        if (app is null)
            return;

        if (!Cache.TryGetValue(language, out var next))
        {
            var fileName = language switch
            {
                AppLanguage.Kyrgyz => "Strings.Ky.axaml",
                AppLanguage.Turkish => "Strings.Tr.axaml",
                AppLanguage.Uzbek => "Strings.Uz.axaml",
                AppLanguage.English => "Strings.En.axaml",
                _ => "Strings.Ru.axaml",
            };
            var uri = new Uri($"avares://NurMarketKassa.Avalonia/Resources/{fileName}");
            next = new ResourceInclude(uri) { Source = uri };
            Cache[language] = next;
        }

        var merged = app.Resources.MergedDictionaries;
        var index = _current is null ? -1 : merged.IndexOf(_current);
        if (!ReferenceEquals(_current, next))
        {
            if (index >= 0)
                merged[index] = next;
            else
                merged.Add(next);
            _current = next;
        }

        // Строки, собранные в коде (Tr.T), сами не обновятся — сообщаем ViewModel-ям (2026-09-07).
        Tr.NotifyLanguageChanged();
    }
}
