using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-09 (владелец: «при повторном скане того же товара остановить сканер и спросить количество — и это же на закупках в десктопе»): настоящие окна в Avalonia.Headless.
/// Окно «Сколько штук?» при 1024×600 и 1366×768 (ru, ky): поле количества в фокусе, текст не обрезан, Enter — добавить, Esc — отмена, «+»/«−», штуки целые, литры дробные,
/// СКАН ВО ВРЕМЯ ОКНА не добавляет (символы сканера убираются из поля, Enter сканера окно не принимает). Сквозные сценарии: «Склад → Приёмка» (поле скана → второй скан того же кода → окно →
/// +N к той же строке; другой код — без окна; «Отмена» ничего не меняет; настройка выключена — как раньше) и «Закупки → Приём товара».
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\repeat-qty-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class ReceivingRepeatScanUiTests
{
    private const string Code = "4600000000011";
    private const string OtherCode = "4600000000028";

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static ReceivingLineVm Line(string unit = "шт", double qty = 3, string barcode = Code, string name = "Молоко «Простоквашино» 3,2 % 1 л (длинное название для проверки)") => new()
    {
        Barcode = barcode, ProductName = name, Unit = unit, Source = ReceivingSource.Warehouse, ProductId = "p-" + barcode, Quantity = qty, PurchasePrice = 100, SalePrice = 130,
    };

    private static async Task<T> Within<T>(Task<T> task, int ms = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 20);
        task.IsCompleted.Should().BeTrue("окно должно закрыться");
        return await task;
    }

    private static void Press(Window w, PhysicalKey key) => w.KeyPressQwerty(key, RawInputModifiers.None);

    /// <summary>Скан как с USB-сканера: быстрая серия цифр и Enter сразу за ней.</summary>
    private static void ScanInto(Window w, string digits)
    {
        foreach (var ch in digits)
        {
            Press(w, Enum.Parse<PhysicalKey>("Digit" + ch));
            w.KeyTextInput(ch.ToString());
        }
        Press(w, PhysicalKey.Enter);
        UiShot.Pump();
    }

    // ------------------------------------------------------------------ окно «Сколько штук?»

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Dialog_FocusedField_TextNotClipped_FitsScreen_EnterAdds(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var line = Line();
        var dialog = new ReceivingRepeatQtyDialog(line);
        try
        {
            var task = dialog.ShowDialog<double?>(owner);
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";

            dialog.QuantityBox.IsFocused.Should().BeTrue($"поле количества в фокусе ({tag})");
            dialog.QuantityBox.Text.Should().Be("1");
            dialog.QuantityBox.SelectionStart.Should().Be(0);
            dialog.QuantityBox.SelectionEnd.Should().Be(1, "число выделено: набор заменяет его");
            var text = UiDrive.AllText(dialog);
            text.Should().Contain("Молоко «Простоквашино»");
            text.Should().Contain((lang == AppLanguage.Kyrgyz ? "Кабыл алууда бар: 3 " : "Уже в приёмке: 3 ") + UnitCatalog.Display("шт"));
            text.Should().NotContain("..", "точка после единицы не двоится");
            TextClipChecker.Find(dialog, "repeat-qty", lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(height, $"окно не выше экрана ({tag})");
            dialog.Bounds.Width.Should().BeLessThanOrEqualTo(width, tag);
            UiDrive.ButtonByText(dialog, "Добавить", "Кошуу").IsEnabled.Should().BeTrue();
            SaveShot(dialog, $"repeat-qty-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");

            UiDrive.Type(dialog, dialog.QuantityBox, "7");
            Press(dialog, PhysicalKey.Enter);
            (await Within(task)).Should().Be(7, $"Enter добавляет введённое число ({tag})");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Dialog_Escape_Cancels_ReturnsNull()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(1024, 600));
        var dialog = new ReceivingRepeatQtyDialog(Line());
        try
        {
            var task = dialog.ShowDialog<double?>(owner);
            UiShot.Settle(6);
            UiDrive.Type(dialog, dialog.QuantityBox, "9");
            Press(dialog, PhysicalKey.Escape);
            (await Within(task)).Should().BeNull("Esc — отмена, число не возвращается");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Dialog_PlusMinus_Whole_ForPieces_AndFractionalForLitres()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(1024, 600));
        var pieces = new ReceivingRepeatQtyDialog(Line("шт"));
        var litres = new ReceivingRepeatQtyDialog(Line("л", 2.5, name: "Масло"));
        try
        {
            var t1 = pieces.ShowDialog<double?>(owner);
            UiShot.Settle(6);
            UiDrive.Click(pieces, UiDrive.Find<Button>(pieces, "RepeatQtyPlus"));
            UiDrive.Click(pieces, UiDrive.Find<Button>(pieces, "RepeatQtyPlus"));
            pieces.QuantityBox.Text.Should().Be("3", "штуки — шаг 1");
            UiDrive.Click(pieces, UiDrive.Find<Button>(pieces, "RepeatQtyMinus"));
            pieces.QuantityBox.Text.Should().Be("2");
            for (var i = 0; i < 4; i++)
                UiDrive.Click(pieces, UiDrive.Find<Button>(pieces, "RepeatQtyMinus"));
            pieces.QuantityBox.Text.Should().Be("1", "меньше одной штуки не бывает");
            UiDrive.Type(pieces, pieces.QuantityBox, "2,5");
            pieces.QuantityBox.Text.Should().Be("25", "у штучного товара запятая не вводится — только цифры");
            UiDrive.Type(pieces, pieces.QuantityBox, "4a");
            pieces.QuantityBox.Text.Should().Be("4", "буквы не вводятся");
            UiDrive.Click(pieces, UiDrive.Find<Button>(pieces, "RepeatQtyAdd"));
            (await Within(t1)).Should().Be(4);

            var t2 = litres.ShowDialog<double?>(owner);
            UiShot.Settle(6);
            UiDrive.AllText(litres).Should().Contain("2,5 л");
            UiDrive.Click(litres, UiDrive.Find<Button>(litres, "RepeatQtyPlus"));
            litres.QuantityBox.Text.Should().Be("1,5", "литры — шаг 0,5");
            UiDrive.Type(litres, litres.QuantityBox, "0,25");
            litres.QuantityBox.Text.Should().Be("0,25", "у литров дробное можно");
            UiDrive.Click(litres, UiDrive.Find<Button>(litres, "RepeatQtyAdd"));
            (await Within(t2)).Should().Be(0.25);
        }
        finally
        {
            pieces.Close();
            litres.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Dialog_ScanWhileOpen_IsIgnored_DoesNotAddOrChangeNumber()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(1024, 600));
        var dialog = new ReceivingRepeatQtyDialog(Line());
        try
        {
            var task = dialog.ShowDialog<double?>(owner);
            UiShot.Settle(6);
            dialog.QuantityBox.Focus();
            UiShot.Pump();

            ScanInto(dialog, OtherCode);
            task.IsCompleted.Should().BeFalse("Enter сканера не нажал «Добавить»");
            dialog.IgnoredScans.Should().Be(1);
            dialog.QuantityBox.Text.Should().Be("1", "штрихкод из поля убран, число прежнее");
            UiDrive.ButtonByText(dialog, "Добавить").IsEnabled.Should().BeTrue();

            // обычный набор человеком после скана работает
            UiDrive.Type(dialog, dialog.QuantityBox, "5");
            Press(dialog, PhysicalKey.Enter);
            (await Within(task)).Should().Be(5);
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Склад → Приёмка»: сквозной сценарий через поле скана

    private static async Task<(WarehouseWindow Window, WarehouseViewModel Vm, TextBox Scan)> OpenWarehouseReceivingAsync(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        using var _ = UiShot.Render(window, new UiSize(1366, 768));
        UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
        UiShot.Settle();
        var vm = (WarehouseViewModel)window.DataContext!;
        vm.ReceivingLines.Clear();
        vm.RepeatScanGuard.Reset();
        return (window, vm, UiDrive.Find<TextBox>(window, "ReceivingScanBox"));
    }

    private static void ScanViaField(Window window, TextBox scan, string code)
    {
        scan.Text = code;
        scan.Focus();
        UiShot.Pump();
        Press(window, PhysicalKey.Enter);
        UiShot.Settle(4, 20);
    }

    private static ReceivingRepeatQtyDialog? OpenDialog(Window owner) => owner.OwnedWindows.OfType<ReceivingRepeatQtyDialog>().FirstOrDefault(d => d.IsVisible);

    [AvaloniaFact]
    public async Task WarehouseReceiving_RepeatScan_AsksQuantity_AddsToSameLine_OtherCodeNoQuestion()
    {
        var (window, vm, scan) = await OpenWarehouseReceivingAsync(AppLanguage.Russian);
        var oldPref = UserPreferences.Instance.AskQtyOnRepeatScan;
        UserPreferences.Instance.AskQtyOnRepeatScan = true;
        try
        {
            var a = Line(qty: 1);
            var b = Line(qty: 1, barcode: OtherCode, name: "Кефир");
            vm.ReceivingLines.Add(a);
            vm.ReceivingLines.Add(b);

            ScanViaField(window, scan, Code); // первый скан: как сейчас, +1
            OpenDialog(window).Should().BeNull("первый скан не спрашивает");
            a.Quantity.Should().Be(2);

            ScanViaField(window, scan, Code); // тот же код подряд — окно
            var dialog = OpenDialog(window);
            dialog.Should().NotBeNull("повторный скан того же кода открывает «Сколько штук?»");
            a.Quantity.Should().Be(2, "пока окно открыто, ничего не добавлено");
            UiDrive.AllText(dialog!).Should().Contain("Уже в приёмке: 2 шт");
            ScanInto(dialog!, OtherCode); // скан во время окна — игнорируется
            a.Quantity.Should().Be(2);
            b.Quantity.Should().Be(1, "скан во время окна не добавил другому товару");
            UiDrive.Type(dialog!, dialog!.QuantityBox, "5");
            Press(dialog, PhysicalKey.Enter);
            UiShot.Settle(6, 20);
            OpenDialog(window).Should().BeNull();
            a.Quantity.Should().Be(7, "5 добавлено к той же строке (2 + 5)");
            vm.ReceivingLines.Count(l => l.Barcode == Code).Should().Be(1, "не дубль строки");

            ScanViaField(window, scan, OtherCode); // другой код — без вопроса
            OpenDialog(window).Should().BeNull();
            b.Quantity.Should().Be(2);
            ScanViaField(window, scan, Code); // «первый после другого» — снова обычный +1
            OpenDialog(window).Should().BeNull("между сканами был другой код");
            a.Quantity.Should().Be(8);
        }
        finally
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = oldPref;
            vm.ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task WarehouseReceiving_RepeatScan_Cancel_ChangesNothing_AndDisabledPreferenceKeepsOldBehaviour()
    {
        var (window, vm, scan) = await OpenWarehouseReceivingAsync(AppLanguage.Russian);
        var oldPref = UserPreferences.Instance.AskQtyOnRepeatScan;
        try
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = true;
            var a = Line(qty: 1);
            vm.ReceivingLines.Add(a);
            ScanViaField(window, scan, Code);
            ScanViaField(window, scan, Code);
            var dialog = OpenDialog(window);
            dialog.Should().NotBeNull();
            UiDrive.Type(dialog!, dialog!.QuantityBox, "9");
            Press(dialog, PhysicalKey.Escape);
            UiShot.Settle(6, 20);
            OpenDialog(window).Should().BeNull();
            a.Quantity.Should().Be(2, "«Отмена» ничего не меняет");
            vm.ReceivingLines.Count.Should().Be(1);

            UserPreferences.Instance.AskQtyOnRepeatScan = false; // «пик-пик» как раньше
            vm.RepeatScanGuard.Reset();
            ScanViaField(window, scan, Code);
            ScanViaField(window, scan, Code);
            ScanViaField(window, scan, Code);
            OpenDialog(window).Should().BeNull("настройка выключена — вопроса нет");
            a.Quantity.Should().Be(5, "каждый скан прибавляет единицу");
        }
        finally
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = oldPref;
            vm.ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-11 (Д-8, живая проверка «Склада» на NBS): скан → ручная правка «Принимаем» (набирали ~28 с) → скан того же кода молча прибавил +1.
    /// Пауза 20 с отсчитывалась от скана и истекала во время набора. Теперь правка количества строки последнего кода — не сброс: пауза считается от конца правки,
    /// повторный скан спрашивает «Сколько штук?». Время «набора» имитируем сдвигом момента последнего скана в охране (без ожидания 20 с).</summary>
    [AvaloniaFact]
    public async Task WarehouseReceiving_ManualQuantityEdit_ThenSameScan_StillAsksQuantity()
    {
        var (window, vm, scan) = await OpenWarehouseReceivingAsync(AppLanguage.Russian);
        var oldPref = UserPreferences.Instance.AskQtyOnRepeatScan;
        UserPreferences.Instance.AskQtyOnRepeatScan = true;
        try
        {
            var a = Line(qty: 1);
            vm.ReceivingLines.Add(a);
            ScanViaField(window, scan, Code);
            OpenDialog(window).Should().BeNull("первый скан не спрашивает");
            a.Quantity.Should().Be(2);

            // «кладовщик набирал число 25 с»: момент скана в охране — 25 с назад
            var atField = typeof(ReceivingRepeatScanGuard).GetField("_at", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            atField.SetValue(vm.RepeatScanGuard, DateTime.UtcNow - TimeSpan.FromSeconds(25));

            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            var qtyColumn = grid.Columns[5]; // «Принимаем» (WarehouseWindow.RcvColQty)
            grid.SelectedItem = a;
            grid.CurrentColumn = qtyColumn;
            grid.BeginEdit().Should().BeTrue();
            UiShot.Settle(8);
            var box = TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as TextBox;
            box.Should().NotBeNull("правка «Принимаем» — поле в фокусе");
            box!.SelectAll();
            window.KeyTextInput("6");
            UiShot.Settle(4);
            grid.CommitEdit();
            UiShot.Settle(6);
            a.Quantity.Should().Be(6, "ручная правка принята");
            vm.RepeatScanGuard.LastCode.Should().Be(Code, "правка количества — не сброс «тот же код подряд»");

            ScanViaField(window, scan, Code);
            var dialog = OpenDialog(window);
            dialog.Should().NotBeNull("Д-8: первый повтор после ручной правки спрашивает «Сколько штук?», а не прибавляет +1 молча");
            a.Quantity.Should().Be(6, "пока окно открыто, ничего не добавлено");
            Press(dialog!, PhysicalKey.Escape);
            UiShot.Settle(6, 20);
        }
        finally
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = oldPref;
            vm.ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task WarehouseReceiving_RepeatScan_PackMode_AsksPacks_AddsPacks()
    {
        var (window, vm, scan) = await OpenWarehouseReceivingAsync(AppLanguage.Russian);
        var oldPref = UserPreferences.Instance.AskQtyOnRepeatScan;
        UserPreferences.Instance.AskQtyOnRepeatScan = true;
        try
        {
            var a = Line(qty: 24);
            vm.ReceivingLines.Add(a);
            a.SetPackMode(true, 24); // 1 упаковка по 24 шт
            ScanViaField(window, scan, Code); // 2026-11-11 (Б-4): в режиме упаковок скан — +1 упаковка (было +1 шт: 40 → 40,04 «не меняется»)
            a.PackCount.Should().BeApproximately(2, 1e-9, "скан коробки в режиме упаковок — +1 упаковка");
            a.Quantity.Should().Be(48);
            ScanViaField(window, scan, Code); // повтор
            var dialog = OpenDialog(window);
            dialog.Should().NotBeNull();
            UiDrive.AllText(dialog!).Should().Contain("упак.", "в режиме «Упаковками» спрашивается количество упаковок");
            var packsBefore = a.PackCount;
            UiDrive.Type(dialog!, dialog!.QuantityBox, "2");
            Press(dialog, PhysicalKey.Enter);
            UiShot.Settle(6, 20);
            a.PackCount.Should().BeApproximately(packsBefore + 2, 1e-5, "две упаковки прибавлены");
            a.Quantity.Should().BeApproximately(48 + 2 * 24, 1e-9, "штуки: 2 упаковки + 2 × 24");
            vm.ReceivingLines.Count.Should().Be(1);
        }
        finally
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = oldPref;
            vm.ReceivingLines.Clear();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Закупки → Приём товара»

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task PurchasesReceive_RepeatScan_AsksQuantity_AddsToSameLine(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var oldPref = UserPreferences.Instance.AskQtyOnRepeatScan;
        UserPreferences.Instance.AskQtyOnRepeatScan = true;
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk1", "Вода питьевая 0,5 л", "30 сом", false) { Barcode = Code, Unit = "шт", Quantity = 10, PurchasePrice = 20 });
        CatalogCacheService.Products.Add(new CatalogProductTileVm("pk2", "Сок яблочный 1 л", "90 сом", false) { Barcode = OtherCode, Unit = "шт", Quantity = 10, PurchasePrice = 60 });
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", "Вода питьевая 0,5 л", "шт", 1.0, (double?)20) }));
            UiShot.Settle(12);
            var tag = $"{width}x{height} {lang}";
            var scan = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && (t.Watermark ?? "").Contains("штрихкод", StringComparison.OrdinalIgnoreCase));

            ScanViaField(window, scan, Code); // первый скан: +1
            OpenDialog(window).Should().BeNull($"первый скан не спрашивает ({tag})");
            UiDrive.AllText(window).Should().Contain("Вода питьевая 0,5 л");

            ScanViaField(window, scan, Code); // повтор — окно
            var dialog = OpenDialog(window);
            dialog.Should().NotBeNull($"повторный скан того же кода открывает «Сколько штук?» ({tag})");
            // 2026-11-11: было «1» — тест повторял баг (строка заказа «1» обнулялась после отрисовки, скан давал 1); верно: 1 из заказа + 1 скан = 2
            UiDrive.AllText(dialog!).Should().Contain((lang == AppLanguage.Kyrgyz ? "Кабыл алууда бар: 2 " : "Уже в приёмке: 2 ") + UnitCatalog.Display("шт"), "1 из заказа + первый скан");
            dialog!.QuantityBox.IsFocused.Should().BeTrue(tag);
            TextClipChecker.Find(dialog, "repeat-qty-purchases", lang.ToString()).Should().BeEmpty(tag);
            ScanInto(dialog, OtherCode); // скан во время окна — игнорируется
            UiDrive.Type(dialog, dialog.QuantityBox, "5");
            Press(dialog, PhysicalKey.Enter);
            UiShot.Settle(8, 20);
            OpenDialog(window).Should().BeNull();

            // в таблице у «Воды» количество 2 + 5 = 7, другой товар не добавлялся
            var qtyBoxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Tag as string == "rcv-cell").Select(t => t.Text).ToList();
            qtyBoxes.Should().Contain("7", $"2 + 5 в той же строке ({tag})");
            UiDrive.AllText(window).Should().NotContain("Сок яблочный", "скан во время окна ничего не добавил");

            ScanViaField(window, scan, OtherCode); // другой код — без вопроса, новая строка
            OpenDialog(window).Should().BeNull(tag);
            UiDrive.AllText(window).Should().Contain("Сок яблочный 1 л");
        }
        finally
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = oldPref;
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }
}
