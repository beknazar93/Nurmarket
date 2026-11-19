using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (пакеты Н-6 / Н-7, «UX и дизайн — место среди аналогов»): малый экран, сенсор и доступность — автоматом.
/// (1) Контраст текста: вторичный серый текст и тексты статусов (готово / внимание / ошибка) на фонах темы — не ниже 4,5:1 во всех темах.
/// (2) Цели касания: на ключевых экранах кассы в сенсорной теме на 1024×600 кнопки и поля не меньше 44 px. Уже известные мелкие цели —
///     в Snapshots/known-small-targets.txt («храповик»: новая мелкая цель — провал; исправил — удали строку).
/// (3) «Текст не обрезан» на 1024×600 в ru / ky / uz для больших окон (TextNotClippedTests проверяет только ru/ky и 1024×640).</summary>
[Collection("UiScenarios")]
public sealed class AccessibilityN7UiTests
{
    // ------------------------------------------------------------------ (1) контраст

    private static readonly string[] TextKeys = ["BrushText", "BrushTextMuted", "BrushTextSoft", "BrushUiStatusOk", "BrushUiStatusWarn"];
    private static readonly string[] BackgroundKeys = ["BrushWindow", "BrushPanel", "BrushPanelSoft"];

    /// <summary>Текст на «своём» мягком фоне: зелёный на мягком зелёном (плашка «Отчёт сохранён», сдача), красный на мягком красном (плашка ошибки).</summary>
    private static readonly (string Text, string Background)[] SoftPairs =
    [
        ("BrushUiStatusOk", "BrushSuccessSoft"),
        ("BrushDangerOnSoft", "BrushDangerSoft"),
    ];

    private static string Hex(string key)
    {
        var app = Application.Current!;
        app.TryFindResource(key, app.ActualThemeVariant, out var value).Should().BeTrue($"в теме есть кисть {key}");
        var color = ((ISolidColorBrush)value!).Color;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    [AvaloniaTheory]
    [InlineData(UiTheme.Light)]
    [InlineData(UiTheme.Dark)]
    [InlineData(UiTheme.Touch)]
    public async Task TextBrushes_HaveContrastAtLeast45(UiTheme theme)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, AppLanguage.Russian);
        var low = new List<string>();
        foreach (var text in TextKeys)
        foreach (var background in BackgroundKeys)
            Check(text, background);
        foreach (var (text, background) in SoftPairs)
            Check(text, background);
        low.Should().BeEmpty($"контраст текста в теме {theme} — не ниже 4,5:1");

        void Check(string text, string background)
        {
            var ratio = AccentThemeService.Contrast(Hex(text), Hex(background));
            if (ratio < 4.5)
                low.Add($"{text} {Hex(text)} на {background} {Hex(background)}: {ratio:0.00}:1");
        }
    }

    // ------------------------------------------------------------------ (2) цели касания

    private const double MinTarget = 44;

    /// <summary>Ключевые экраны кассы: имя → как создать и большой ли это экран (тогда 1024×600).</summary>
    public static IEnumerable<object[]> TouchScreens() =>
    [
        ["checkout"], ["open-shift"], ["close-shift"], ["return-sale"], ["pay-debt"], ["sales"], ["finance"], ["clients"],
        ["receipt-history"], ["debts"],
    ];

    private static (Window Window, bool Big) CreateScreen(string name) => name switch
    {
        "receipt-history" => (New<ReceiptHistoryWindow>(), true),
        "debts" => (New<DebtsWindow>(), true),
        _ => (ScreenCatalog.Get(name).Create(), ScreenCatalog.Get(name).Kind == ScreenKind.Window),
    };

    private static Window New<T>() where T : Window
    {
        if (TestHost.Services.GetService(typeof(T)) is Window registered)
            return registered;
        var ctor = typeof(T).GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        return (Window)ctor.Invoke(ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray());
    }

    /// <summary>Самостоятельные элементы, в которые попадают пальцем (части чужих шаблонов — стрелки прокрутки, крестик поля, заголовки
    /// колонок — не считаются: у них своя логика и свой размер).</summary>
    private static bool IsTarget(Control c) =>
        c is Button or ToggleButton or TextBox or ComboBox or CalendarDatePicker
        && c.TemplatedParent is null && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.Bounds.Width > 0 && c.Bounds.Height > 0;

    private static string Label(Control c)
    {
        if (!string.IsNullOrWhiteSpace(c.Name))
            return c.Name!;
        var text = UiDrive.TextOf(c);
        if (string.IsNullOrWhiteSpace(text) && c is TextBox box)
            text = box.Watermark;
        text = (text ?? "").Replace('\n', ' ').Replace('|', '/').Trim();
        return text.Length == 0 ? "?" : text.Length > 30 ? text[..30] : text;
    }

    [AvaloniaTheory]
    [MemberData(nameof(TouchScreens))]
    public async Task KeyScreens_TouchTargetsAreAtLeast44(string screenName)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Touch, AppLanguage.Russian);
        var (window, big) = CreateScreen(screenName);
        try
        {
            using var _ = UiShot.Render(window, big ? new UiSize(1024, 600) : null);
            UiShot.Settle(10, 40);
            var found = window.GetVisualDescendants().OfType<Control>().Where(IsTarget)
                .Where(c => c.Bounds.Height < MinTarget - 0.5 || c.Bounds.Width < MinTarget - 0.5)
                .Select(c => (Key: $"{screenName}|{c.GetType().Name}|{Label(c)}", Detail: $"{c.GetType().Name} «{Label(c)}» {c.Bounds.Width:0}×{c.Bounds.Height:0}"))
                .GroupBy(v => v.Key).Select(g => g.First()).ToList();
            var known = KnownSmallTargets.Load();
            if (Snapshots.PngSnapshot.UpdateMode)
            {
                KnownSmallTargets.Replace(screenName, found.Select(v => v.Key));
                return;
            }
            var fresh = found.Where(v => !known.Contains(v.Key)).ToList();
            Assert.True(fresh.Count == 0, $"Новые цели касания меньше 44 px на экране «{screenName}» (сенсорная тема):\n" + string.Join("\n", fresh.Select(v => "  " + v.Detail)));
        }
        finally
        {
            try { window.Close(); } catch (Exception) { }
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ (3) текст не обрезан: 1024×600, ru / ky / uz

    public static IEnumerable<object[]> SmallScreenMatrix()
    {
        foreach (var screen in ScreenCatalog.All.Where(s => s.Kind == ScreenKind.Window && s.SkipReason is null))
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
            yield return [screen.Name, lang];
    }

    private static string Code(AppLanguage lang) => lang switch
    {
        AppLanguage.Kyrgyz => "ky",
        AppLanguage.Uzbek => "uz",
        _ => "ru",
    };

    [AvaloniaTheory]
    [MemberData(nameof(SmallScreenMatrix))]
    public async Task BigWindows_TextIsNotClipped_At1024x600(string screenName, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        var screen = ScreenCatalog.Get(screenName);
        UiShot.Setup(UiTheme.Light, lang);
        var window = screen.Create();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            // Ключ с пометкой размера — не смешивается со списком TextNotClippedTests (1024×640).
            var found = TextClipChecker.Find(window, screenName + "@1024x600", Code(lang));
            var known = KnownClipping.Load();
            if (Snapshots.PngSnapshot.UpdateMode)
            {
                KnownClipping.Add(found.Select(v => v.Key));
                return;
            }
            var fresh = found.Where(v => !known.Contains(v.Key)).ToList();
            Assert.True(fresh.Count == 0, $"Новые обрезанные тексты на экране «{screenName}» (1024×600, {Code(lang)}):\n" + string.Join("\n", fresh.Select(v => "  " + v.Detail)));
        }
        finally
        {
            try { window.Close(); } catch (Exception) { }
            UiShot.Pump();
        }
    }
}

/// <summary>2026-11-13 (Н-7): список известных целей касания меньше 44 px (экран|тип|имя или надпись), по одной на строку.</summary>
internal static class KnownSmallTargets
{
    private static readonly string FilePath = Path.Combine(Snapshots.PngSnapshot.BaselineDir, "..", "known-small-targets.txt");

    public static HashSet<string> Load() =>
        File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet() : [];

    /// <summary>Режим обновления: строки экрана заменяются найденными (исправленные цели из списка уходят сами).</summary>
    public static void Replace(string screen, IEnumerable<string> keys)
    {
        var set = Load();
        set.RemoveWhere(k => k.StartsWith(screen + "|", StringComparison.Ordinal));
        foreach (var k in keys)
            set.Add(k);
        var lines = new List<string>
        {
            "# 2026-11-13 (Н-7): известные цели касания меньше 44 px в сенсорной теме (экран|тип|имя или надпись). Чинить — удалять строку.",
        };
        lines.AddRange(set.OrderBy(x => x, StringComparer.Ordinal));
        File.WriteAllLines(FilePath, lines);
    }
}
