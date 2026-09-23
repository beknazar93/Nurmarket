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

    public static void Apply(AppLanguage language)
    {
        var app = Application.Current;
        if (app is null)
            return;

        if (_current is not null)
            app.Resources.MergedDictionaries.Remove(_current);

        var fileName = language switch
        {
            AppLanguage.Kyrgyz => "Strings.Ky.axaml",
            AppLanguage.Turkish => "Strings.Tr.axaml",
            AppLanguage.Uzbek => "Strings.Uz.axaml",
            AppLanguage.English => "Strings.En.axaml",
            _ => "Strings.Ru.axaml",
        };
        var uri = new Uri($"avares://NurMarketKassa.Avalonia/Resources/{fileName}");

        _current = new ResourceInclude(uri) { Source = uri };
        app.Resources.MergedDictionaries.Add(_current);

        // Строки, собранные в коде (Tr.T), сами не обновятся — сообщаем ViewModel-ям (2026-09-07).
        Tr.NotifyLanguageChanged();
    }
}
