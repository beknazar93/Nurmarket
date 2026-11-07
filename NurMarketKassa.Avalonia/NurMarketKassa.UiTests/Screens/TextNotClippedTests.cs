using Avalonia.Headless.XUnit;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-06 (В14): «текст не обрезан» на каждом экране каталога — светлая и сенсорная темы (у тёмной та же раскладка), ru и ky (самые длинные слова),
/// окна — в 1024×640, диалоги — по содержимому. Список уже известных нарушений — Snapshots/known-text-clipping.txt (метод «храповика»: новое нарушение —
/// провал теста; чинить — удалять строку; обновить список осознанно: test-safe.ps1 -Phase ui -UpdateSnapshots).</summary>
public sealed class TextNotClippedTests
{
    public static IEnumerable<object[]> Matrix()
    {
        foreach (var screen in ScreenCatalog.All)
        foreach (var theme in new[] { UiTheme.Light, UiTheme.Touch })
        foreach (var lang in UiShot.Languages)
            yield return [screen.Name, theme, lang];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Screen_TextIsNotClipped(string screenName, UiTheme theme, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var screen = ScreenCatalog.Get(screenName);
        UiShot.Setup(theme, lang);
        var window = screen.Create();
        try
        {
            using var _ = UiShot.Render(window, screen.Kind == ScreenKind.Window ? UiSize.Small : null);
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var found = TextClipChecker.Find(window, screenName, langCode);
            var known = KnownClipping.Load();
            if (Snapshots.PngSnapshot.UpdateMode)
            {
                KnownClipping.Add(found.Select(v => v.Key));
                return;
            }
            var fresh = found.Where(v => !known.Contains(v.Key)).ToList();
            Assert.True(fresh.Count == 0, $"Новые обрезанные тексты на экране «{screenName}» ({langCode}, {theme}):\n" + string.Join("\n", fresh.Select(v => "  " + v.Detail)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}

/// <summary>Список известных нарушений «текст обрезан» (по одному ключу на строку).</summary>
internal static class KnownClipping
{
    private static readonly string FilePath = Path.Combine(Snapshots.PngSnapshot.BaselineDir, "..", "known-text-clipping.txt");

    public static HashSet<string> Load() =>
        File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet() : [];

    public static void Add(IEnumerable<string> keys)
    {
        var set = Load();
        var before = set.Count;
        foreach (var k in keys)
            set.Add(k);
        if (set.Count == before && File.Exists(FilePath))
            return;
        var lines = new List<string> { "# 2026-11-06 (В14): известные обрезанные тексты (экран|язык|текст|ширина/высота). Чинить — удалять строку. Новые нарушения валят TextNotClippedTests." };
        lines.AddRange(set.OrderBy(x => x, StringComparer.Ordinal));
        File.WriteAllLines(FilePath, lines);
    }
}
