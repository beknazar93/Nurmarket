using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (Д-1 и Д-8, согласие владельца 13.11): одна вторичная и одна акцентная кнопка на всю программу. Проверка числом в настоящих окнах:
/// у всех видимых кнопок с «вторичными» и «главными» классами (и их старыми псевдонимами) один радиус (ключ шкалы NurRadiusControl), высота не меньше
/// NurHeightControl (44, сенсорная цель), у вторичных рамка 1 px. Светлая и тёмная тема, ru и ky, 1024×640.
/// Снимки «после» для глаз: %TEMP%\nurmarket-tests\ui-d1\{окно}-{тема}-{язык}.png.</summary>
public sealed class ButtonConsistencyUiTests
{
    private static readonly string[] Secondary = ["nur-secondary", "modal-cancel", "modal-secondary", "sec-outline", "btn-secondary", "SecondaryButton", "SecondaryActionButton"];
    private static readonly string[] Primary = ["nur-primary", "modal-primary", "sec-primary", "btn-primary", "PrimaryButton", "PrimaryActionButton"];

    private static readonly Dictionary<string, Func<Window>> Windows = new()
    {
        ["marketplace"] = () => new MarketplaceWindow(),
        ["logs-errors"] = () => new LogsAndErrorsWindow(),
        ["irregular-receipts"] = () => new IrregularReceiptsWindow(),
        ["knowledge-base"] = () => new KnowledgeBaseWindow(),
        ["remote-support"] = () => new RemoteSupportWindow(),
        ["restock"] = () => new RestockSuggestionsWindow(),
        ["writeoff-history"] = () => new WriteOffHistoryWindow(),
        ["timesheet"] = () => new StaffTimesheetWindow(),
        ["bulk-import"] = () => new BulkImportWindow(),
        // окна из общего каталога снимков: модальное окно, раздел, карточка товара, склад
        ["confirm"] = () => ScreenCatalog.Get("confirm").Create(),
        ["close-shift"] = () => ScreenCatalog.Get("close-shift").Create(),
        ["sales"] = () => ScreenCatalog.Get("sales").Create(),
        ["product-edit"] = () => ScreenCatalog.Get("product-edit").Create(),
        ["warehouse"] = () => ScreenCatalog.Get("warehouse").Create(),
    };

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var name in Windows.Keys)
        foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
        foreach (var lang in UiShot.Languages)
            yield return [name, theme, lang];
    }

    private static string ShotDir
    {
        get
        {
            var d = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-d1");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Buttons_OneRadius_OneHeight_OneBorder(string name, UiTheme theme, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, lang);
        var window = Windows[name]();
        try
        {
            using (var bmp = UiShot.Render(window, UiSize.Small))
            using (var image = SKImage.FromBitmap(bmp))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 90))
                File.WriteAllBytes(Path.Combine(ShotDir, $"{name}-{UiShot.Slug(theme, lang)}.png"), data.ToArray());

            Assert.True(window.TryFindResource("NurRadiusControl", window.ActualThemeVariant, out var r) && r is CornerRadius, "нет ключа шкалы NurRadiusControl");
            var radius = (CornerRadius)r!;
            Assert.True(window.TryFindResource("NurHeightControl", window.ActualThemeVariant, out var h) && h is double, "нет ключа шкалы NurHeightControl");
            var height = (double)h!;

            var problems = new List<string>();
            foreach (var b in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
            {
                var sec = b.Classes.Any(c => Secondary.Contains(c));
                var pri = b.Classes.Any(c => Primary.Contains(c));
                if (!sec && !pri)
                    continue;
                // кнопка с двумя классами (выбранный пункт списка в карточке товара) считается главной
                var label = $"«{(b.Content as string) ?? b.Name ?? b.Content?.GetType().Name}» [{string.Join(' ', b.Classes)}]";
                if (b.CornerRadius != radius)
                    problems.Add($"{label}: радиус {b.CornerRadius}, нужен {radius}");
                if (b.Bounds.Height < height - 0.5)
                    problems.Add($"{label}: высота {b.Bounds.Height:0.#}, нужно не меньше {height}");
                if (sec && !pri && b.BorderThickness != new Thickness(1))
                    problems.Add($"{label}: рамка {b.BorderThickness}, нужна 1");
            }
            Assert.True(problems.Count == 0, $"{name} ({theme}, {lang}):\n" + string.Join("\n", problems));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
