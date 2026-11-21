using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (владелец: «при создании накладных и штрихкоды тоже добавь»; в мобилке то же): штрихкоды в накладных программы. Настоящие окна в Avalonia.Headless:
/// «Закупки → Приём товара» (поле штрихкода у каждой строки, «Сканировать» у строки без кода, «+N» доп. штрихкоды, скан кода упаковки = N штук, занятый код — вопрос «Выбрать его / Оставить /
/// Отмена»), «Закупка по накладной» (InvoiceEditorWindow), окна «Доп. штрихкоды» и «Штрихкод уже занят». Размеры 1024×600 и 1366×768, языки ru, ky, uz: элементы строки не налезают друг на друга и
/// не выходят за окно, текст не обрезан. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\invoice-barcode-{окно}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class InvoiceBarcodeUiTests
{
    private const string NoCodeId = "ib1";
    private const string PackId = "ib2";
    private const string PackBarcode = "4600000000028";
    private const string PackCode = "4600000000200";   // доп. штрихкод «Пачка», 12 штук
    private const string SecondCode = "4600000000301"; // второй обычный код того же товара

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static void SeedCatalog()
    {
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(new CatalogProductTileVm(NoCodeId, "Вода питьевая 0,5 л «Асу» (длинное название для проверки переноса)", "30 сом", false) { Barcode = "", Unit = "шт", Quantity = 10, PurchasePrice = 20 });
        CatalogCacheService.Products.Add(new CatalogProductTileVm(PackId, "Сок яблочный 1 л", "90 сом", false)
        {
            Barcode = PackBarcode, Unit = "шт", Quantity = 10, PurchasePrice = 60,
            AlternateBarcodeVariants =
            [
                new AlternateBarcodeVariant { Barcode = PackCode, Name = "Пачка", Quantity = 12 },
                new AlternateBarcodeVariant { Barcode = SecondCode, Name = null, Quantity = 0 },
            ],
        });
    }

    /// <summary>2026-11-20: окна для проверки «поля не налезают» (FormFieldsOverlapUiTests): «Закупка по накладной» со строками без штрихкода, «Доп. штрихкоды», «Штрихкод уже занят».</summary>
    internal static Window CreateEditorForLayout()
    {
        SeedCatalog();
        return new InvoiceEditorWindow(Draft());
    }

    internal static Window CreateExtrasForLayout() =>
        new BarcodeExtrasDialog("Сок яблочный 1 л", PackBarcode, [new AlternateBarcodeVariant { Barcode = PackCode, Name = "Пачка", Quantity = 12 }, new AlternateBarcodeVariant { Barcode = SecondCode }], _ => Task.FromResult(""));

    internal static Window CreateConflictForLayout() => new BarcodeConflictDialog(PackBarcode, "Сок яблочный 1 л");

    private static ReceivePrefill Prefill(params (string Id, string Name)[] items) =>
        new(null, null, null, null, items.Select(i => (i.Id, i.Name, "шт", 1.0, (double?)20)).ToArray());

    private static IEnumerable<T> Visible<T>(Window w, string tag) where T : Control =>
        w.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && Equals(c.Tag, tag));

    private static Rect RectIn(Window w, Control c) => new(c.TranslatePoint(default, w) ?? default, c.Bounds.Size);

    private static void ScanViaField(Window window, TextBox scan, string code)
    {
        scan.Text = code;
        scan.Focus();
        UiShot.Pump();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiShot.Settle(6, 20);
    }

    private static TextBox ScanField(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && (t.Watermark ?? "").Contains("штрихкод", StringComparison.OrdinalIgnoreCase));

    private static T? Dialog<T>(Window owner) where T : Window => owner.OwnedWindows.OfType<T>().FirstOrDefault(d => d.IsVisible);

    /// <summary>Поле, «Сканировать» и «+N» одной строки не налезают друг на друга и не выходят за окно.</summary>
    private static void NoOverlap(Window window, IReadOnlyList<Control> items, string tag)
    {
        var bounds = window.Bounds;
        for (var i = 0; i < items.Count; i++)
        {
            var a = RectIn(window, items[i]);
            a.Left.Should().BeGreaterThanOrEqualTo(-0.5, tag);
            a.Right.Should().BeLessThanOrEqualTo(bounds.Width + 0.5, $"{tag}: {Describe(items[i])} не выходит за правый край окна");
            for (var j = i + 1; j < items.Count; j++)
            {
                var b = RectIn(window, items[j]);
                var inter = a.Intersect(b);
                (inter.Width > 1.5 && inter.Height > 1.5).Should().BeFalse($"{tag}: {Describe(items[i])} и {Describe(items[j])} не налезают друг на друга ({a} / {b})");
            }
        }
    }

    private static string? ExtrasText(Button b) => b.Content as string;

    private static string Describe(Control c) => c is Button b ? $"кнопка «{UiDrive.TextOf(b)}»" : c is TextBox t ? $"поле «{t.Text}»" : c.GetType().Name;

    // ------------------------------------------------------------------ «Закупки → Приём товара»

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Receive_BarcodeCell_ShowsFieldScanAndExtras_NoOverlap_NoClipping(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        SeedCatalog();
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(Prefill((NoCodeId, "Вода питьевая 0,5 л «Асу»"), (PackId, "Сок яблочный 1 л")));
            UiShot.Settle(12);
            var tag = $"{width}x{height} {lang}";

            var boxes = Visible<TextBox>(window, "inv-barcode").OrderBy(b => RectIn(window, b).Top).ToList();
            boxes.Should().HaveCount(2, $"у каждой строки приёма есть поле штрихкода ({tag})");
            boxes[0].IsReadOnly.Should().BeFalse($"у товара без штрихкода поле правится ({tag})");
            boxes[0].Text.Should().BeNullOrEmpty();
            boxes[1].IsReadOnly.Should().BeTrue($"у товара со штрихкодом поле только читается ({tag})");
            boxes[1].Text.Should().Be(PackBarcode);

            var scans = Visible<Button>(window, "inv-scan").ToList();
            scans.Should().HaveCount(1, $"«Сканировать» — только у строки без штрихкода ({tag})");
            var extras = Visible<Button>(window, "inv-extras").OrderBy(b => RectIn(window, b).Top).ToList();
            extras.Should().HaveCount(2, tag);
            ExtrasText(extras[0]).Should().Be("+");
            ExtrasText(extras[1]).Should().Be("+2", "у «Сока» два доп. штрихкода");

            // поле + «Сканировать» + «+N» одной строки: рядом, не налезают (проверка по каждой строке)
            NoOverlap(window, [boxes[0], scans[0], extras[0]], tag + " строка 1");
            NoOverlap(window, [boxes[1], extras[1]], tag + " строка 2");
            window.Bounds.Width.Should().BeGreaterThan(0);
            TextClipChecker.Find(window, "purchases-receive-barcodes", lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");
            SaveShot(window, $"invoice-barcode-receive-{Code(lang)}-{width}x{height}");

            // «Сканировать»: курсор в поле штрихкода этой строки
            UiDrive.Click(window, scans[0]);
            UiShot.Settle(3);
            boxes[0].IsFocused.Should().BeTrue($"«Сканировать» ставит курсор в поле штрихкода ({tag})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
        }
    }

    [AvaloniaFact]
    public async Task Receive_ScanPackCode_SetsPackQuantity_WithNote_ThenMainCodeAddsOne()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SeedCatalog();
        var old = UserPreferences.Instance.AskQtyOnRepeatScan;
        UserPreferences.Instance.AskQtyOnRepeatScan = false; // повторный скан без вопроса «Сколько штук?»
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            window.OpenReceive(null);
            UiShot.Settle(8);
            var scan = ScanField(window);

            ScanViaField(window, scan, PackCode);
            QuantityCells(window).Should().Contain("12", "скан кода упаковки (доп. штрихкод «Пачка», 12 штук) ставит количество строки = 12");
            UiDrive.AllText(window).Should().Contain("= 12 шт.", "подпись о пачке (имя упаковки варианта — «Пачка»)");
            UiDrive.AllText(window).Should().Contain("Сок яблочный 1 л");

            ScanViaField(window, scan, PackCode);
            QuantityCells(window).Should().Contain("24", "ещё одна пачка — ещё 12 штук в той же строке");

            ScanViaField(window, scan, PackBarcode);
            QuantityCells(window).Should().Contain("25", "основной штрихкод — одна штука, как раньше");

            ScanViaField(window, scan, SecondCode);
            QuantityCells(window).Should().Contain("26", "второй обычный код (количество не задано) — +1");
        }
        finally
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = old;
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
        }
    }

    private static List<string?> QuantityCells(Window w) =>
        w.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Tag as string == "rcv-cell").Select(t => t.Text).ToList();

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task Receive_TypedCodeOwnedByAnotherProduct_AsksChooseKeepCancel(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        SeedCatalog();
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            window.OpenReceive(Prefill((NoCodeId, "Вода питьевая 0,5 л «Асу»")));
            UiShot.Settle(10);
            var box = Visible<TextBox>(window, "inv-barcode").Single();

            // 1) занятый код + «Оставить как есть»: код остаётся у «Сока», у строки кода нет
            UiDrive.Type(window, box, PackBarcode);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(10, 30);
            var dialog = Dialog<BarcodeConflictDialog>(window);
            dialog.Should().NotBeNull("код уже у другого товара — вопрос");
            var text = UiDrive.AllText(dialog!);
            text.Should().Contain(PackBarcode).And.Contain("Сок яблочный 1 л");
            TextClipChecker.Find(dialog!, "barcode-conflict", lang.ToString()).Should().BeEmpty();
            dialog!.Bounds.Width.Should().BeLessThanOrEqualTo(1024);
            SaveShot(dialog, $"invoice-barcode-conflict-{Code(lang)}-1024x600");
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Оставить как есть", "Калтыруу"));
            UiShot.Settle(10, 30);
            Visible<TextBox>(window, "inv-barcode").Single().Text.Should().BeNullOrEmpty("«Оставить» — строка без кода");

            // 2) тот же код + «Выбрать этот товар»: строка становится «Соком»
            UiDrive.Type(window, Visible<TextBox>(window, "inv-barcode").Single(), PackBarcode);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(10, 30);
            dialog = Dialog<BarcodeConflictDialog>(window);
            dialog.Should().NotBeNull();
            UiDrive.Click(dialog!, UiDrive.ButtonByText(dialog!, "Выбрать этот товар", "Ушул товарды тандоо"));
            UiShot.Settle(12, 30);
            var all = UiDrive.AllText(window);
            all.Should().Contain("Сок яблочный 1 л");
            all.Should().NotContain("Вода питьевая 0,5 л «Асу» (длинное", "строка «Воды» заменена товаром-владельцем кода");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
        }
    }

    /// <summary>2026-11-21 (ответ Умара): код свободен в каталоге кассы, но сервер при записи ответил 409 barcode_duplicate (код уже у другого товара: product_id, name) — тот же вопрос
    /// «выбрать его / оставить / отмена» с названием ИЗ ОТВЕТА сервера, без повторного поиска по каталогу. 1024×600, ru и ky.</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task Receive_ServerSaysBarcodeDuplicate_AsksWithOwnerNameFromServer(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        SeedCatalog();
        const string freeCode = "4600000000999";
        var owner = new ServerBarcodeOwner(PackId, "Сок яблочный 1 л", "15", "alternate", freeCode);
        InvoiceBarcodeBinder.OwnerOverride = (_, _) => Task.FromResult(new InvoiceCodeOwner(null, false)); // каталог кассы кода не знает
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<System.Text.Json.JsonElement?>(System.Text.Json.JsonDocument.Parse($"{{\"id\":\"{NoCodeId}\",\"version\":3,\"barcode\":\"4600000000777\",\"alternate_barcodes\":[]}}").RootElement.Clone()); // на сервере у товара код уже есть (его поставили на другой кассе) — запись пойдёт доп. кодом
        InvoiceBarcodeBinder.AddOverride = (_, _, _, _) => Task.FromResult(new BarcodeWriteOutcome(false, ServerBarcodeErrors.DuplicateText(owner), owner));
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            window.OpenReceive(Prefill((NoCodeId, "Вода питьевая 0,5 л «Асу»")));
            UiShot.Settle(10);
            var box = Visible<TextBox>(window, "inv-barcode").Single();
            UiDrive.Type(window, box, freeCode);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(10, 30);
            var dialog = Dialog<BarcodeConflictDialog>(window);
            dialog.Should().NotBeNull("сервер сказал «код уже у товара …» — вопрос, а не молчаливый отказ");
            UiDrive.AllText(dialog!).Should().Contain(freeCode).And.Contain("Сок яблочный 1 л");
            TextClipChecker.Find(dialog!, "barcode-conflict-server", lang.ToString()).Should().BeEmpty();
            dialog!.Bounds.Width.Should().BeLessThanOrEqualTo(1024);
            SaveShot(dialog, $"invoice-barcode-server-duplicate-{Code(lang)}-1024x600");
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Оставить как есть", "Калтыруу"));
            UiShot.Settle(10, 30);
            Visible<TextBox>(window, "inv-barcode").Single().Text.Should().NotBe(freeCode, "«Оставить» — код не принят");
        }
        finally
        {
            InvoiceBarcodeBinder.OwnerOverride = null;
            InvoiceBarcodeBinder.ReadCardOverride = null;
            InvoiceBarcodeBinder.AddOverride = null;
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
        }
    }

    [AvaloniaFact]
    public async Task Receive_FreeCode_OnProductWithoutCode_IsWrittenToServer_AndBadFormatIsRefused()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SeedCatalog();
        var saved = new List<IReadOnlyDictionary<string, object?>>();
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<System.Text.Json.JsonElement?>(System.Text.Json.JsonDocument.Parse("""{"id":"ib1","barcode":"","alternate_barcodes":[]}""").RootElement.Clone());
        InvoiceBarcodeBinder.SaveOverride = (_, fields, _) => { saved.Add(fields); return Task.FromResult<string?>(null); };
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            window.OpenReceive(Prefill((NoCodeId, "Вода питьевая 0,5 л «Асу»")));
            UiShot.Settle(10);
            var box = Visible<TextBox>(window, "inv-barcode").Single();

            // цифры 4–14: набранные буквы отбрасываются полем, короткий код не принимается
            UiDrive.Type(window, box, "12");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(10, 30);
            saved.Should().BeEmpty("3 цифры — не штрихкод: на сервер не уходит");
            UiDrive.AllText(window).Should().Contain("4 до 14");

            box = Visible<TextBox>(window, "inv-barcode").Single();
            UiDrive.Type(window, box, "4600000000999");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(12, 30);
            saved.Should().ContainSingle("свободный код у товара без штрихкода записывается товару на сервере");
            saved[0]["barcode"].Should().Be("4600000000999");
            var after = Visible<TextBox>(window, "inv-barcode").Single();
            after.Text.Should().Be("4600000000999");
            after.IsReadOnly.Should().BeTrue("код поставлен — поле больше не правится, «Сканировать» исчезла");
            Visible<Button>(window, "inv-scan").Should().BeEmpty();
        }
        finally
        {
            InvoiceBarcodeBinder.ReadCardOverride = null;
            InvoiceBarcodeBinder.SaveOverride = null;
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
        }
    }

    // ------------------------------------------------------------------ «Закупка по накладной» (ИИ / фото / XML)

    private static InvoiceEditorWindow.PurchaseDraft Draft()
    {
        var tile = CatalogCacheService.Products.First(p => p.Id == NoCodeId);
        var draft = new InvoiceEditorWindow.PurchaseDraft { Suppliers = [] };
        draft.Rows.Add(new InvoiceEditorWindow.DraftRow
        {
            Line = new ReceivingLineVm { Source = ReceivingSource.Unknown, Unit = "шт" }, InvoiceName = "Сок гранатовый 1 л (новый товар)", Barcode = "", Unit = "шт", Qty = "5", Purchase = "60", Price = "90",
        });
        draft.Rows.Add(new InvoiceEditorWindow.DraftRow
        {
            Line = PurchaseReceivingService.FromTile(tile, 3), Existing = tile, InvoiceName = tile.Title, Barcode = "", Unit = "шт", Qty = "3", Purchase = "20", Price = "30",
        });
        return draft;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task InvoiceEditor_BarcodeColumn_ScanAndExtras_NoOverlap(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        SeedCatalog();
        var window = new InvoiceEditorWindow(Draft());
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(10);
            var tag = $"{width}x{height} {lang}";
            var scans = Visible<Button>(window, "inv-scan").OrderBy(b => RectIn(window, b).Top).ToList();
            var extras = Visible<Button>(window, "inv-extras").OrderBy(b => RectIn(window, b).Top).ToList();
            scans.Should().HaveCount(2, $"оба товара без штрихкода (новый и товар склада без кода) — у обоих «Сканировать» ({tag})");
            extras.Should().HaveCount(2, tag);
            NoOverlap(window, [scans[0], extras[0]], tag + " строка 1");
            NoOverlap(window, [scans[1], extras[1]], tag + " строка 2");
            TextClipChecker.Find(window, "invoice-editor-barcodes", lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");
            SaveShot(window, $"invoice-barcode-editor-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
        }
    }

    [AvaloniaFact]
    public async Task InvoiceEditor_DuplicateCodeInsideInvoice_IsRefused_AndRevertedToPrevious()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SeedCatalog();
        var draft = Draft();
        draft.Rows[0].Barcode = "4600000000555";
        draft.Rows[0].Line.Barcode = "4600000000555";
        var window = new InvoiceEditorWindow(draft);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(10);
            // у товара склада без кода вписываем код новой строки накладной: дубль внутри накладной
            var codeBoxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && !t.IsReadOnly && (t.Watermark ?? "") is { Length: > 0 } w && w.Contains("скан", StringComparison.OrdinalIgnoreCase)).OrderBy(b => RectIn(window, b).Top).ToList();
            codeBoxes.Should().HaveCount(2, "у обеих строк правимое поле штрихкода");
            codeBoxes[0].Text.Should().Be("4600000000555");
            UiDrive.Type(window, codeBoxes[1], "4600000000555");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(10, 30);
            draft.Rows[1].Barcode.Should().BeNullOrEmpty("код уже стоит у другой строки накладной — не принят");
            UiDrive.AllText(window).Should().Contain("уже стоит у другой строки");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
        }
    }

    // ------------------------------------------------------------------ окно «Доп. штрихкоды»

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task ExtrasDialog_AddScanCodeWithPack_Validates_NoOverlap(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var current = new List<AlternateBarcodeVariant> { new() { Barcode = PackCode, Name = "Пачка", Quantity = 12 } };
        var checkedCodes = new List<string>();
        var dialog = new BarcodeExtrasDialog("Сок яблочный 1 л", PackBarcode, current, code => { checkedCodes.Add(code); return Task.FromResult(code == "4600000000777" ? "Код занят" : ""); });
        try
        {
            var task = dialog.ShowDialog<List<AlternateBarcodeVariant>?>(owner);
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(height, $"окно не выше экрана ({tag})");
            dialog.Bounds.Width.Should().BeLessThanOrEqualTo(width, tag);
            var fields = dialog.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0).OrderBy(t => RectIn(dialog, t).Top).ThenBy(t => RectIn(dialog, t).Left).ToList();
            NoOverlap(dialog, fields.Cast<Control>().Concat(dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0)).ToList(), tag);
            TextClipChecker.Find(dialog, "barcode-extras", lang.ToString()).Should().BeEmpty($"текст не обрезан ({tag})");
            SaveShot(dialog, $"invoice-barcode-extras-{Code(lang)}-{width}x{height}");

            // занятый код — ошибка у поля, список не растёт; свободный — добавляется с названием и упаковкой
            var code = dialog.GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, "inv-extra-code"));
            UiDrive.Type(dialog, code, "4600000000777");
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(8, 30);
            UiDrive.AllText(dialog).Should().Contain("Код занят");
            code = dialog.GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, "inv-extra-code"));
            code.Text = "";
            UiDrive.Type(dialog, code, "4600000000888");
            var qty = dialog.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && Equals(t.HorizontalContentAlignment, Avalonia.Layout.HorizontalAlignment.Right)).Last();
            qty.Text = "24";
            dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(8, 30);
            UiDrive.AllText(dialog).Should().Contain("4600000000888");
            UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, "Готово", "Даяр", "Tayyor"));
            UiShot.Settle(8, 30);
            task.IsCompleted.Should().BeTrue();
            var result = await task;
            result.Should().NotBeNull();
            result!.Select(v => v.Barcode).Should().Equal(PackCode, "4600000000888");
            result.Single(v => v.Barcode == "4600000000888").Quantity.Should().Be(24);
            result.Single(v => v.Barcode == PackCode).Name.Should().Be("Пачка");
            checkedCodes.Should().Contain("4600000000888");
        }
        finally
        {
            dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
    }
}
