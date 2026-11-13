using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (владелец: «UX и UI очень плохой… корректировка возвратных накладных клиентов и поставщиков должна быть удобной»): вид и удобство окон
/// «Править накладную» (возврат поставщику) и «Изменение возврата от клиента» в Avalonia.Headless — ru / ky / uz при 1024×600 и 1366×768, тёмная тема.
/// Проверяется числами, а не только картинкой: строка товара — одна линия (не выше 64 px), на 1024×600 видно не меньше 4 строк, обязательная «Причина правки»
/// видна без прокрутки, «✕» убирает строку (количество 0, название зачёркнуто) и «↩» возвращает её, «−/+» держат количество в пределах 0…«можно до»,
/// под суммой — «было … · меньше/больше на …», текст не обрезан, главная кнопка в окне. Снимки: %TEMP%\nurmarket-tests\ui-actual\retux-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ReturnEditUxUiTests
{
    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Lang(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private sealed class ScreenRestore : IDisposable
    {
        public void Dispose() => ModalShell.WorkingAreaForTests = null;
    }

    private static IDisposable FakeScreen(int width, int height)
    {
        ModalShell.WorkingAreaForTests = new Size(width, height);
        return new ScreenRestore();
    }

    private static SupplierReturnInfo Info(int lines)
    {
        var items = string.Join(",", Enumerable.Range(1, lines).Select(i => i % 3 == 0
            ? $"{{\"id\":\"l{i}\",\"product_id\":\"p{i}\",\"product_name\":\"Сыр российский весовой №{i}\",\"qty\":\"1.255\",\"unit\":\"кг\",\"purchase_price\":\"150.5\"}}"
            : $"{{\"id\":\"l{i}\",\"product_id\":\"p{i}\",\"product_name\":\"Молоко «Простоквашино» 3,2 % 1 л (длинное название для проверки) №{i}\",\"qty\":\"2\",\"unit\":\"шт\",\"purchase_price\":\"150\"}}"));
        return SupplierApiService.ParseReturn(JsonDocument.Parse(
            "{\"id\":\"r1\",\"number_display\":\"ВП-2026-000123\",\"version\":3,\"status\":\"posted\",\"can_edit\":true,\"can_void\":true,"
            + "\"editable_until\":\"2026-11-11T08:00:00Z\",\"server_now\":\"2026-11-11T03:00:00Z\",\"total_amount\":\"520.00\",\"comment\":\"брак упаковки\","
            + "\"items\":[" + items + "]}").RootElement.Clone());
    }

    private static List<TextBox> QtyBoxes(Window dialog) => dialog.GetVisualDescendants().OfType<TextBox>()
        .Where(t => t.IsEffectivelyVisible && (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").StartsWith(Tr.T("Кол-во", "Саны", "Qty", "Adet", "Soni") + ":", StringComparison.Ordinal)).ToList();

    private static List<Button> RowButtons(Window dialog, params string[] glyphs) => dialog.GetVisualDescendants().OfType<Button>()
        .Where(b => b.IsEffectivelyVisible && b.Content is string s && glyphs.Contains(s)).ToList();

    /// <summary>Элемент целиком виден в прокручиваемой части окна (без прокрутки).</summary>
    private static bool InViewport(Control c)
    {
        var viewer = c.GetVisualAncestors().OfType<ScrollViewer>().First();
        var top = c.TranslatePoint(new Point(0, 0), viewer);
        return top is { } p && p.Y >= -0.5 && p.Y + c.Bounds.Height <= viewer.Bounds.Height + 0.5;
    }

    private static void Fits(Window dialog, int width, int height, string screen, AppLanguage lang)
    {
        var tag = $"{screen} {width}x{height} {lang}";
        dialog.Bounds.Height.Should().BeLessThanOrEqualTo(height, $"окно не выше экрана ({tag})");
        dialog.Bounds.Width.Should().BeLessThanOrEqualTo(width, $"окно не шире экрана ({tag})");
        foreach (var b in dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && (b.Classes.Contains("modal-primary") || b.Classes.Contains("modal-secondary") || b.Classes.Contains("modal-cancel"))))
        {
            var p = b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), dialog);
            p.Should().NotBeNull();
            p!.Value.Y.Should().BeLessThanOrEqualTo(dialog.Bounds.Height - 16 + 0.5, $"кнопка подвала видна целиком ({tag})");
            p.Value.X.Should().BeLessThanOrEqualTo(dialog.Bounds.Width - 16 + 0.5, $"кнопка подвала не обрезана справа ({tag})");
        }
        TextClipChecker.Find(dialog, screen, lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void SupplierEdit_CompactRows_ReasonVisible_RemoveRestore_Delta(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fakeScreen = FakeScreen(width, height);
        using var _ = UiShot.Render(owner, new UiSize(width, height));

        // 1) короткая накладная (2 строки): всё окно без прокрутки — строки, причина и комментарий видны сразу
        var small = new SupplierReturnEditDialog(Info(2), "ВП-2026-000123");
        try
        {
            small.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            var reason = small.GetVisualDescendants().OfType<TextBox>().First(t => (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").StartsWith(Tr.T("Причина правки", "Оңдоонун себеби", "Reason for the change", "Düzenleme nedeni", "Tahrirlash sababi"), StringComparison.Ordinal));
            InViewport(reason).Should().BeTrue($"обязательная «Причина правки» видна без прокрутки ({width}x{height} {lang})");
            var boxes = QtyBoxes(small);
            boxes.Should().HaveCount(2);
            foreach (var box in boxes)
                ((Control)box.Parent!).Bounds.Height.Should().BeLessThanOrEqualTo(64, "строка товара — одна линия таблицы, без подписей в каждой строке");

            // «✕» убирает строку, «↩» возвращает
            small.DeltaTextForTests.Should().Be(Tr.T("без изменений", "өзгөрүүсүз", "no changes", "değişiklik yok", "o'zgarishsiz"));
            UiDrive.Click(small, RowButtons(small, "✕")[0]);
            UiShot.Settle(2);
            small.RowBoxesForTests(0).Qty!.Text.Should().Be("0", "«✕» ставит количество 0 — строка убирается из накладной");
            RowButtons(small, "↩").Should().HaveCount(1, "у убранной строки кнопка — «вернуть строку»");
            small.DeltaTextForTests.Should().Contain(MoneyText.Som(300m), "под суммой — на сколько она стала меньше (2 × 150)");
            SaveShot(small, $"retux-supplier-edit-{Lang(lang)}-{width}x{height}");
            Fits(small, width, height, "retux-supplier-edit", lang);
            UiDrive.Click(small, RowButtons(small, "↩")[0]);
            UiShot.Settle(2);
            small.RowBoxesForTests(0).Qty!.Text.Should().Be("2", "«↩» возвращает исходное количество");
            small.DeltaTextForTests.Should().Be(Tr.T("без изменений", "өзгөрүүсүз", "no changes", "değişiklik yok", "o'zgarishsiz"));
        }
        finally
        {
            small.Close();
            UiShot.Pump();
        }

        // 2) накладная на 12 строк: на 1024×600 видно не меньше 4 строк (раньше 2–3), подвал с суммой и кнопками на месте
        var big = new SupplierReturnEditDialog(Info(12), "ВП-2026-000123");
        try
        {
            big.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            big.SetQtyForTests(1, "5");
            UiShot.Settle(2);
            QtyBoxes(big).Count(InViewport).Should().BeGreaterThanOrEqualTo(4, $"на экране {width}x{height} видно не меньше 4 строк накладной ({lang})");
            big.DeltaTextForTests.Should().Contain(MoneyText.Som(450m), "под суммой — на сколько она стала больше (3 × 150)");
            Fits(big, width, height, "retux-supplier-edit-12", lang);
            SaveShot(big, $"retux-supplier-edit-12-{Lang(lang)}-{width}x{height}");
        }
        finally
        {
            big.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    private static List<CustomerReturnEditLine> CustomerLines() => CustomerReturnEdit.FromCards(
        JsonDocument.Parse("{\"id\":\"pr1\",\"sale\":\"s1\",\"version\":3,\"can_edit\":true,\"can_void\":true,\"amount\":\"350.00\","
            + "\"items\":[{\"sale_item_id\":\"si-1\",\"name\":\"Молоко «Простоквашино» 3,2 % 1 л (длинное название для проверки переноса)\",\"quantity\":\"2\",\"amount\":\"200.00\"},"
            + "{\"sale_item_id\":\"si-2\",\"name\":\"Сыр российский\",\"unit\":\"кг\",\"quantity\":\"1.5\",\"price\":\"100\"}]}").RootElement.Clone(),
        JsonDocument.Parse("{\"id\":\"s1\",\"items\":[{\"id\":\"si-1\",\"quantity\":\"5\",\"returnable_qty\":\"3\"},{\"id\":\"si-2\",\"quantity\":\"2\",\"returnable_qty\":\"0.5\"}]}").RootElement.Clone());

    private static ReturnDocRef CustomerDoc() => new(ReturnDocKind.Customer, "pr1", "№1115 · 11.11 10:05", 350,
        ReturnEditState.Parse(JsonDocument.Parse("{\"version\":3,\"can_edit\":true,\"can_void\":true,\"editable_until\":\"2026-11-12T08:00:00Z\",\"server_now\":\"2026-11-11T09:00:00Z\"}").RootElement.Clone()));

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void CustomerEdit_Stepper_ClampsToMax_RemoveRestore_Delta(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var fakeScreen = FakeScreen(width, height);
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var dialog = new CustomerReturnEditDialog(CustomerDoc(), CustomerLines());
        try
        {
            dialog.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            var boxes = QtyBoxes(dialog);
            boxes.Should().HaveCount(2);
            foreach (var box in boxes)
                ((Control)box.Parent!).Bounds.Height.Should().BeLessThanOrEqualTo(80,"строка товара — одна линия: название с «было · можно до», «− количество +», сумма, «✕»");
            var reason = dialog.GetVisualDescendants().OfType<TextBox>().First(t => (Avalonia.Automation.AutomationProperties.GetName(t) ?? "").StartsWith(Tr.T("Причина правки", "Оңдоонун себеби", "Reason for the change", "Düzenleme nedeni", "Tahrirlash sababi"), StringComparison.Ordinal));
            InViewport(reason).Should().BeTrue($"обязательная «Причина правки» видна без прокрутки ({width}x{height} {lang})");

            // «+» не даёт больше «можно до 5», «−» не уходит ниже 0
            var plus = RowButtons(dialog, "+")[0];
            for (var i = 0; i < 6; i++)
                UiDrive.Click(dialog, plus);
            UiShot.Settle(2);
            boxes[0].Text.Should().Be("5", "«+» останавливается на «можно до 5»");
            dialog.DeltaTextForTests.Should().Contain(MoneyText.Som(300m), "под суммой — на сколько клиенту вернётся больше (3 × 100)");
            var minus = RowButtons(dialog, "−")[0];
            for (var i = 0; i < 4; i++)
                UiDrive.Click(dialog, minus);
            UiShot.Settle(2);
            boxes[0].Text.Should().Be("1");
            dialog.DeltaTextForTests.Should().Contain(MoneyText.Som(100m), "под суммой — на сколько клиенту вернётся меньше (1 × 100)");

            // «✕» убирает строку, «↩» возвращает исходное количество
            UiDrive.Click(dialog, RowButtons(dialog, "✕")[1]);
            UiShot.Settle(2);
            boxes[1].Text.Should().Be("0");
            RowButtons(dialog, "↩").Should().HaveCount(1);
            dialog.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.TextDecorations == TextDecorations.Strikethrough, "название убранной строки зачёркнуто");
            Fits(dialog, width, height, "retux-customer-edit", lang);
            SaveShot(dialog, $"retux-customer-edit-{Lang(lang)}-{width}x{height}");
            UiDrive.Click(dialog, RowButtons(dialog, "↩")[0]);
            UiShot.Settle(2);
            boxes[1].Text.Should().Be("1,5", "«↩» возвращает исходное количество");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void BothEditDialogs_DarkTheme_Kyrgyz_1024x600_Fit()
    {
        UiShot.Setup(UiTheme.Dark, AppLanguage.Kyrgyz);
        var owner = new Window();
        using var fakeScreen = FakeScreen(1024, 600);
        using var _ = UiShot.Render(owner, new UiSize(1024, 600));
        var supplier = new SupplierReturnEditDialog(Info(12), "ВП-2026-000123");
        var customer = new CustomerReturnEditDialog(CustomerDoc(), CustomerLines());
        try
        {
            supplier.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            supplier.SetQtyForTests(0, "0");
            UiShot.Settle(2);
            Fits(supplier, 1024, 600, "retux-supplier-edit-dark", AppLanguage.Kyrgyz);
            SaveShot(supplier, "retux-supplier-edit-dark-ky-1024x600");
            supplier.Close();
            UiShot.Pump();

            customer.ShowDialog<bool>(owner);
            UiShot.Settle(8);
            customer.SetQtyForTests(0, "1");
            UiShot.Settle(2);
            Fits(customer, 1024, 600, "retux-customer-edit-dark", AppLanguage.Kyrgyz);
            SaveShot(customer, "retux-customer-edit-dark-ky-1024x600");
        }
        finally
        {
            supplier.Close();
            customer.Close();
            owner.Close();
            UiShot.Pump();
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        }
    }
}
