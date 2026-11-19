using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (Л-5 «Возвраты»): окно возврата — ОДНО поле поиска чека (номер, сумма, дата, клиент, телефон, карта, товар), «Нет чека?», выбор «куда товар»
/// (на полку / в брак / поставщику) в окне причины, подстановка товара в «Возврат поставщику». ru/ky, 1024×600 и 1366×768; снимки — %TEMP%\nurmarket-tests\ui-actual\return-l5-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ReturnL5UiTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;

    public ReturnL5UiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        PosApp.ActiveShiftId = Guid.NewGuid().ToString();
    }

    public void Dispose() => PosApp.ActiveShiftId = _savedShift;

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static string Row(string id, int number, string total, string item, int daysAgo, string? client = null, string? phone = null, string? card = null) =>
        "{\"id\":\"" + id + "\",\"number\":" + number + ",\"status\":\"paid\",\"return_status\":\"none\",\"total\":\"" + total + "\",\"payment_method\":\"" + (card is null ? "cash" : "card")
        + "\",\"created_at\":\"" + new DateTimeOffset(DateTime.Today.AddHours(12).AddDays(-daysAgo)).ToString("O") + "\",\"first_item_name\":\"" + item + "\",\"user_display\":\"Айбек\""
        + (client is null ? "" : ",\"client\":\"c-1\",\"client_name\":\"" + client + "\"")
        + (phone is null ? "" : ",\"client_phone\":\"" + phone + "\"")
        + (card is null ? "" : ",\"card_last4\":\"" + card + "\"") + "}";

    private static void StubList()
    {
        var rows = new[]
        {
            Row("s-3200", 2101, "3200.00", "Масло моторное 5W-40", 0),
            Row("s-260", 2102, "260.00", "Хомут", 1, "Азамат Усупов", "+996 700 123 456"),
            Row("s-3540", 2103, "3540.00", "Ступица подшипник передний Ласетти", 2, "Бектур", "0555 99 88 77", "4417"),
            Row("s-800", 2104, "800.00", "Свеча", 3),
        };
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":4,\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", rows) + "]}");
    }

    private static async Task LoginAsync() =>
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static void WaitFor(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 30);
    }

    private static List<string> Search(ReturnSaleDialog dialog, string text, int expected)
    {
        UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "ReturnProductSearchBox"), text);
        UiShot.Settle(16, 30); // поиск — с паузой 250 мс: без неё прочитался бы список прошлого запроса
        WaitFor(() => dialog.FilteredSales.Count == expected, 3000);
        return dialog.FilteredSales.Select(r => r.SaleId).ToList();
    }

    // ------------------------------------------------------------------ единое поле поиска

    [AvaloniaFact]
    public async Task ReturnDialog_OneSearchField_FindsByNumberAmountDateClientPhoneCard()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubList();
        await LoginAsync();
        var dialog = new ReturnSaleDialog();
        try
        {
            UiShot.Render(dialog, new UiSize(1366, 768));
            WaitFor(() => dialog.Sales.Count == 4);
            dialog.Sales.Should().HaveCount(4);
            dialog.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "SearchReceiptBox").Should().BeFalse("поле «номер» убрано — поле поиска одно");

            Search(dialog, "2102", 1).Should().Equal("s-260");                       // номер чека
            Search(dialog, "№2104", 1).Should().Equal("s-800");                      // «№» и «#» не мешают
            Search(dialog, "3 540", 1).Should().Equal("s-3540");                     // сумма
            Search(dialog, DateTime.Now.AddDays(-1).ToString("dd.MM"), 1).Should().Equal("s-260"); // дата (вчера)
            Search(dialog, "азамат", 1).Should().Equal("s-260");                     // клиент
            Search(dialog, "3456", 1).Should().Equal("s-260");                       // последние 4 цифры телефона
            Search(dialog, "8877", 1).Should().Equal("s-3540");
            Search(dialog, "4417", 1).Should().Equal("s-3540");                      // последние 4 цифры карты
            Search(dialog, "ласетти ступица", 1).Should().Equal("s-3540");           // товар — как раньше
            UiDrive.Find<TextBox>(dialog, "ReturnProductSearchBox").Text = ""; UiShot.Settle(16, 30); WaitFor(() => dialog.FilteredSales.Count == 4, 3000); dialog.FilteredSales.Should().HaveCount(4, "пустое поле — все чеки");
            Search(dialog, "несуществующий", 0).Should().BeEmpty();
            UiDrive.AllText(dialog).Should().Contain("Чек не найден", "пустой результат — подсказка, а не пустая таблица");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task ReturnDialog_SearchRowAndNoReceiptButton_FitScreen_NotClipped(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        StubList();
        await LoginAsync();
        var dialog = new ReturnSaleDialog();
        try
        {
            UiShot.Render(dialog, new UiSize(width, height));
            WaitFor(() => dialog.Sales.Count == 4);
            UiShot.Settle(4, 30);
            var box = UiDrive.Find<TextBox>(dialog, "ReturnProductSearchBox");
            var noReceipt = UiDrive.Find<Button>(dialog, "NoReceiptButton");
            box.IsEffectivelyVisible.Should().BeTrue();
            noReceipt.IsEffectivelyVisible.Should().BeTrue("«Нет чека?» всегда на виду внизу окна");
            foreach (var c in new Control[] { box, noReceipt })
            {
                var topLeft = c.TranslatePoint(new Avalonia.Point(0, 0), dialog)!.Value;
                (topLeft.X + c.Bounds.Width).Should().BeLessThanOrEqualTo(width + 1, $"{c.GetType().Name} не выходит за правый край при {width}×{height}");
                (topLeft.Y + c.Bounds.Height).Should().BeLessThanOrEqualTo(height + 1, $"{c.GetType().Name} не уходит за нижний край при {width}×{height}");
            }
            // В таблице видно не меньше 3 чеков, а «Обновить» и «Ещё» — в строке заголовка (не отдельным рядом)
            var grid = dialog.FindControl<DataGrid>("SalesDataGrid")!;
            grid.Bounds.Height.Should().BeGreaterThan(36 + 3 * 40, $"список чеков не сжат поиском ({lang} {width}×{height})");
            TextClipChecker.Find(dialog, "return-l5-search", lang.ToString()).Where(v => v.Detail.Contains("Нет чека") || v.Detail.Contains("Чек жокпу") || v.Detail.Contains("Поиск")).Should().BeEmpty();
            SaveShot(dialog, $"return-l5-search-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «куда товар»

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void ReasonDialog_Outcome_DefaultShelf_ChipDefectSetsDefect_SupplierSelectable(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var dialog = new ReturnLineReasonDialog(2);
        try
        {
            UiShot.Render(dialog, new UiSize(width, height));
            UiShot.Settle(4, 30);
            dialog.Outcome.Should().Be(ReturnProductOutcome.Shelf, "по умолчанию — на полку, как всегда было");
            dialog.IsDefect.Should().BeFalse();
            var radios = dialog.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "return-outcome").ToList();
            radios.Should().HaveCount(3);
            radios[0].IsChecked.Should().BeTrue();
            // быстрая причина «Брак» ставит исход «В брак»
            var defectChip = dialog.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("modal-chip") && b.Content is string s && (s == "Брак" || s == "Бузук"));
            UiDrive.Click(dialog, defectChip);
            dialog.Outcome.Should().Be(ReturnProductOutcome.Defect);
            dialog.IsDefect.Should().BeTrue();
            dialog.ReasonText.Should().Be(lang == AppLanguage.Kyrgyz ? "Бузук" : "Брак");
            radios[1].IsChecked.Should().BeTrue("переключатель следует за причиной");
            // «Поставщику»
            UiDrive.Click(dialog, radios[2]);
            dialog.Outcome.Should().Be(ReturnProductOutcome.Supplier);
            dialog.IsDefect.Should().BeFalse("поставщику — товар сначала возвращается на склад");
            // «На полку» обратно
            UiDrive.Click(dialog, radios[0]);
            dialog.Outcome.Should().Be(ReturnProductOutcome.Shelf);
            // «Своя причина» очищает поле
            UiDrive.Click(dialog, dialog.GetVisualDescendants().OfType<Button>().First(b => b.Content is string s && s.StartsWith("✎", StringComparison.Ordinal)));
            dialog.ReasonText.Should().BeEmpty();

            var shell = dialog.Content as Control;
            shell!.Bounds.Height.Should().BeLessThanOrEqualTo(height + 1, $"окно причины помещается по высоте ({lang} {width}×{height})");
            TextClipChecker.Find(dialog, "return-l5-reason", lang.ToString()).Should().BeEmpty();
            SaveShot(dialog, $"return-l5-reason-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ товар → «Возврат поставщику»

    [AvaloniaFact]
    public void SupplierReturnDialog_Preset_FillsQuantityAfterSupplierChosen_AndWarnsIfMissing()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Json("GET", "/api/main/clients/", 200, "{\"results\":[{\"id\":\"sup-1\",\"full_name\":\"Поставщик А\"}]}");
        UiFake.Server.Json("GET", "/api/main/suppliers/sup-1/products/", 200,
            "[{\"id\":\"p-1\",\"name\":\"Подшипник\",\"unit\":\"шт\",\"purchase_price\":300,\"quantity\":5},{\"id\":\"p-3\",\"name\":\"Глушитель\",\"unit\":\"шт\",\"purchase_price\":900,\"quantity\":0}]");
        var owner = new Window { Width = 1366, Height = 768 };
        owner.Show();
        var dialog = new SupplierReturnDialog();
        try
        {
            dialog.ApplyPreset([("p-1", "Подшипник", 2), ("p-2", "Ступица", 1)], "defect");
            ModalShell.WorkingAreaForTests = new Size(1366, 768);
            _ = dialog.ShowDialog<bool>(owner);
            UiShot.Settle(6, 30);
            UiDrive.AllText(dialog).Should().Contain("Выберите поставщика — товар подставится", "до выбора поставщика — подсказка со списком товаров");
            var supplier = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            WaitFor(() => supplier.ItemCount > 0);
            supplier.SelectedIndex = 0;
            WaitFor(() => dialog.GetVisualDescendants().OfType<TextBox>().Any(t => t.Text == "2"), 4000);
            UiShot.Settle(4, 30);
            dialog.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text).Should().Contain("2", "количество «Подшипник» подставлено");
            UiDrive.AllText(dialog).Should().Contain("Ступица", "товара, которого у поставщика нет, предупреждение называет по имени");
            UiDrive.AllText(dialog).Should().Contain("Сумма возврата: 600", "сумма пересчитана: 2 × 300");
            SaveShot(dialog, "return-l5-supplier-preset");
        }
        finally
        {
            ModalShell.WorkingAreaForTests = null;
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }
}
