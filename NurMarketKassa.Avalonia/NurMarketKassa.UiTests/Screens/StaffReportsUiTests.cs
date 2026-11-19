using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;
using static NurMarketKassa.Services.OwnerReportsMath;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (Л-13 «Отчёты владельца»): раздел «Кассиры и часы» — обе вкладки, сравнение периодов, ru и ky, 1024×600 и 1366×768:
/// текст не обрезан, на тепловой карте ровно 7 × 24 ячеек, стрелки ↑↓ и % на месте, раздел отдаёт таблицу для Excel. Снимки (PNG) пишутся в
/// %TEMP%\nurmarket-tests\staffreport для просмотра глазами (эталонов нет — вид сверяется человеком).</summary>
[Collection("UiScenarios")]
public sealed class StaffReportsUiTests
{
    private static readonly DateTime From = new(2026, 11, 7);
    private static readonly DateTime To = new(2026, 11, 13);

    private static List<SaleFact> Sales(int shift, decimal factor)
    {
        var list = new List<SaleFact>();
        var cashiers = new[] { "Айгуль Сыдыкова", "Бектур", "Нургуль Иманалиева-Токтогулова" };
        for (var day = 0; day < 7; day++)
            for (var hour = 9; hour < 21; hour++)
                for (var i = 0; i <= (hour * 7 + day * 3 + shift) % 4; i++)
                    list.Add(new SaleFact(From.AddDays(day + shift).AddHours(hour).AddMinutes(i * 7), cashiers[(hour + day + i) % 3],
                        Math.Round((150 + (hour * 37 + day * 91 + i * 13) % 900) * factor, 2), hour % 5 == 0 ? 25m : 0m,
                        (hour + day + i) % 11 == 0 ? "canceled" : (hour + i) % 13 == 0 ? "partially_returned" : "paid"));
        return list;
    }

    public static IEnumerable<object[]> Cases() =>
        from size in new[] { (1024, 600), (1366, 768) }
        from lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }
        from tab in new[] { "cashiers", "hours" }
        select new object[] { size.Item1, size.Item2, lang, tab };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void Tabs_FitAreNotClipped_AndShowComparison(int width, int height, AppLanguage lang, string tab)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var window = new StaffReportsWindow();
        try
        {
            window.ShowForPreview(tab, Sales(0, 1.1m), Sales(-7, 1m), From, To, CompareBase.Previous, new Dictionary<string, int> { ["Бектур"] = 4 });
            using var bmp = UiShot.Render(window, new UiSize(width, height));
            Save(bmp, $"{tab}-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text!).ToList();
            texts.Should().Contain(t => t.StartsWith('↑') || t.StartsWith('↓') || t.StartsWith('→'), "в сравнении есть стрелка и процент");
            if (tab == "hours")
            {
                // 7 дней × 24 часа ячеек: рамки высотой 30 с тултипом либо пустые «серые».
                window.GetVisualDescendants().OfType<Border>().Count(b => b.Height == 30 && b.CornerRadius.TopLeft == 3).Should().Be(7 * 24);
            }
            else
            {
                texts.Should().Contain("Айгуль Сыдыкова");
                texts.Should().Contain(ReportLabels.Total);
            }
            var found = TextClipChecker.Find(window, "staff-reports-" + tab, lang == AppLanguage.Kyrgyz ? "ky" : "ru");
            found.Should().BeEmpty("текст отчёта не обрезан: " + string.Join("; ", found.Select(v => v.Detail)));
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void ExportTable_ForBothTabs_HasRowsAndTotals()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new StaffReportsWindow();
        try
        {
            window.ShowForPreview("cashiers", Sales(0, 1m), Sales(-7, 1m), From, To, CompareBase.YearAgo);
            var cashiers = ((IOwnerSectionExcel)window).BuildExportTable();
            cashiers.Should().NotBeNull();
            cashiers!.Rows.Should().HaveCount(3);
            cashiers.Columns.Should().Contain(c => c.Total == XlTotal.Sum);
            cashiers.Columns.Count.Should().Be(cashiers.Rows[0].Length, "колонок столько же, сколько значений в строке");

            window.ShowForPreview("hours", Sales(0, 1m), null, From, To, CompareBase.None);
            var hours = ((IOwnerSectionExcel)window).BuildExportTable();
            hours.Should().NotBeNull();
            hours!.Rows.Should().HaveCount(7);
            hours.Columns.Count.Should().Be(hours.Rows[0].Length);
            ((decimal)hours.Rows.Sum(r => (decimal)r[^2]!)).Should().Be(TotalsOf(Sales(0, 1m)).Revenue, "итог таблицы равен выручке отчёта");
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    [AvaloniaFact]
    public void EmptyPeriod_ShowsHintNotEmptyTables()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new StaffReportsWindow();
        try
        {
            window.ShowForPreview("cashiers", [], null, From, To, CompareBase.None);
            UiShot.Render(window, UiSize.Small).Dispose();
            window.GetVisualDescendants().OfType<Border>().Should().Contain(b => b.Classes.Contains("state-block"));
            ((IOwnerSectionExcel)window).BuildExportTable().Should().BeNull();
        }
        finally { window.Close(); UiShot.Pump(); }
    }

    private static void Save(SKBitmap bmp, string name)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "staffreport");
            Directory.CreateDirectory(dir);
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
        }
        catch (IOException)
        {
            // снимок — необязательная вещь, тест из-за него не падает
        }
    }
}
