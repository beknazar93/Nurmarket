using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NurMarketKassa.AvaloniaHost.Views;
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

    /// <summary>2026-11-19 (список владельца, п. 8): узбекский и турецкий (слова длиннее русских) — светлая тема, окна 1024×600.</summary>
    public static IEnumerable<object[]> MatrixUzTr()
    {
        foreach (var screen in ScreenCatalog.All)
        foreach (var lang in new[] { AppLanguage.Uzbek, AppLanguage.Turkish })
            yield return [screen.Name, UiTheme.Light, lang];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public Task Screen_TextIsNotClipped(string screenName, UiTheme theme, AppLanguage lang) => CheckAsync(screenName, theme, lang, UiSize.Small);

    [AvaloniaTheory]
    [MemberData(nameof(MatrixUzTr))]
    public Task Screen_TextIsNotClipped_UzTr(string screenName, UiTheme theme, AppLanguage lang) => CheckAsync(screenName, theme, lang, new UiSize(1024, 600));

    /// <summary>2026-11-19 (список владельца, п. 8): окна, которых нет в каталоге снимков (разделы и мастера «Маркетплейс», «Журнал», «Склад»-мелочь,
    /// «Старая загрузка»…), — на всех четырёх языках (ru, ky, uz, tr), светлая тема, 1024×600.</summary>
    internal static readonly Dictionary<string, Func<Window>> ExtraWindows = new() // 2026-11-20: internal — список использует FormFieldsOverlapUiTests
    {
        ["x-marketplace"] = () => new MarketplaceWindow(),
        ["x-logs-errors"] = () => new LogsAndErrorsWindow(),
        ["x-irregular-receipts"] = () => new IrregularReceiptsWindow(),
        ["x-knowledge-base"] = () => new KnowledgeBaseWindow(),
        ["x-remote-support"] = () => new RemoteSupportWindow(),
        ["x-restock"] = () => new RestockSuggestionsWindow(),
        ["x-writeoff-history"] = () => new WriteOffHistoryWindow(),
        ["x-timesheet"] = () => new StaffTimesheetWindow(),
        ["x-bulk-import"] = () => new BulkImportWindow(),
        ["x-client-mailing"] = () => ClientMailingFixture.CreateWindow(), // 2026-11-26: «Клиенты → Рассылка» с готовым списком клиентов
        ["x-receipt-editor"] = () => new NurMarketKassa.AvaloniaHost.Views.Settings.ReceiptEditorWindow(), // 2026-11-21: «Редактор чека»
    };

    public static IEnumerable<object[]> MatrixExtra()
    {
        foreach (var name in ExtraWindows.Keys)
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek, AppLanguage.Turkish })
            yield return [name, lang];
    }

    [AvaloniaTheory]
    [MemberData(nameof(MatrixExtra))]
    public Task ExtraWindow_TextIsNotClipped(string name, AppLanguage lang) => CheckAsync(name, UiTheme.Light, lang, new UiSize(1024, 600), ExtraWindows[name]);

    private static async Task CheckAsync(string screenName, UiTheme theme, AppLanguage lang, UiSize windowSize, Func<Window>? create = null)
    {
        await TestHost.WarmAsync();
        var screen = create is null ? ScreenCatalog.Get(screenName) : null;
        UiShot.Setup(theme, lang);
        var window = create is null ? screen!.Create() : create();
        try
        {
            using var _ = UiShot.Render(window, screen is null || screen.Kind == ScreenKind.Window ? windowSize : null);
            var langCode = lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.Turkish => "tr", _ => "ru" };
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
