using Xunit;
using Avalonia.Headless.XUnit;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-06 (В14): снимки экранов — каждый экран × 3 темы (светлая, тёмная, сенсорная) × ru/ky × размер окна. Эталоны — Snapshots/Baselines/*.png
/// (вдвое уменьшенные); допуск и отчёт расхождений — Snapshots/PngSnapshot.cs. Обновить: tools/qa/test-safe.ps1 -Phase ui -UpdateSnapshots.</summary>
public sealed class ScreenSnapshotTests
{
    public static IEnumerable<object[]> Matrix() => ScreenCatalog.SnapshotMatrix();

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Screen_MatchesBaseline(string screenName, UiTheme theme, AppLanguage lang, string size)
    {
        await TestHost.WarmAsync();
        var screen = ScreenCatalog.Get(screenName);
        var uiSize = ScreenCatalog.ParseSize(size);
        UiShot.Setup(theme, lang);
        var window = screen.Create();
        try
        {
            var name = $"{screenName}-{UiShot.Slug(theme, lang, uiSize)}";
            var problem = UiShot.Shoot(window, name, uiSize);
            Assert.True(problem is null, $"{name}: {problem}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
