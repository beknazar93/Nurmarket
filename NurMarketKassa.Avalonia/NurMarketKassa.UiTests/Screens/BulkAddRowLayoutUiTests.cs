using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (жалоба владельца «наименование огромное — исправь массовое добавление товара!», снимок программы владельца 1850×750):
/// в «Склад → Массовое добавление» поле «Название» растягивалось на всю ширину строки (~1170 px) и на высоту левой колонки (~100 px: штрихкод, «На складе…»,
/// цена, единица, «Цены ▾» стопкой). Окно настоящее (Avalonia.Headless); строки: найденный товар, найденный с длинным названием (90 символов), новый без названия
/// и цен, новый с длинным названием и ошибкой, найденный без цен (поля цен открыты).
/// 2026-11-12 (аудит Д-11, У-3, У-7, Т-15): пределы теперь жёсткие (поле названия 40 px, строка без цен ≤ 68, с ошибкой ≤ 90, с ценами ≤ 160, первая строка ≤ 50, выравнивание ±3 px);
/// проверен именно СЛУЧАЙ ВЛАДЕЛЬЦА — оболочка программы владельца 1850×750 и 1100×750 (название не уже 200 px, не шире 640); список с виртуализацией (1000 строк создают
/// только видимые, добавление строки не зависит от длины списка); у названия в заглушке «*».
/// 2026-11-13 (жалоба «в массовой загрузке наименование очень большое», снова): поле названия было 600 px — теперь не шире 450 (поле 440); те же пределы для мастера «Массовая загрузка товаров» (колонка Name ≤ 460) и старого окна загрузки (колонка ≤ 420), см. ниже.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\bulk-add-{язык}-{ширина}x{высота}.png и bulk-add-owner-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class BulkAddRowLayoutUiTests
{
    private const string LongName = "Реле зарядки Audi красное очень длинное название для проверки обрезки многоточием в строке массового добавления 1234567890";
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 640), (1366, 768), (1440, 800), (1900, 1060) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static readonly string MetricsFile = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual", "bulk-add-metrics.txt");

    private static BulkScanRow Known(string barcode, string name, double stock, double sale, double purchase)
    {
        var tile = new CatalogProductTileVm("w-" + barcode, name, sale.ToString("0.##", CultureInfo.InvariantCulture) + " сом", false) { Barcode = barcode, Unit = "шт", Quantity = stock, PurchasePrice = purchase };
        var line = PurchaseReceivingService.FromTile(tile, 2);
        return new BulkScanRow(line);
    }

    private static BulkScanRow Unknown(string barcode, string name) =>
        new(new ReceivingLineVm { Barcode = barcode, ProductName = name, Source = ReceivingSource.Unknown, Quantity = 1 });

    private static ScrollViewer ListScroll(ItemsControl list) =>
        list.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "BulkListScroll");

    private static void Shot(Window window, string file)
    {
        var dir = Path.GetDirectoryName(MetricsFile)!;
        Directory.CreateDirectory(dir);
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(dir, file), data.ToArray());
    }

    /// <summary>Шесть строк-образцов: найденный, найденный с длинным названием, новый пустой, новый с длинным названием и ошибкой, найденный без цен, найденный с ошибкой.</summary>
    private static BulkScanRow[] AddSamples(BulkScanSession session, bool extra)
    {
        var known = Known("1026702182836", "TEST-0811-товар-1", 10, 110, 80);
        var knownLong = Known("4600000000028", LongName, 3, 250, 185);
        var fresh = Unknown("2000000000011", "");
        var freshLong = Unknown("2000000000028", LongName);
        var noPrices = Known("4600000000035", "Товар без цен", 5, 0, 0);
        var withError = Known("4600000000042", "Товар с ошибкой", 7, 90, 60);
        typeof(BulkScanRow).GetMethod("SetError", Flags)!.Invoke(freshLong, ["Закупка: впишите цену больше нуля"]);
        typeof(BulkScanRow).GetMethod("SetError", Flags)!.Invoke(withError, ["Не удалось принять товар: нет связи"]);
        var all = new[] { known, knownLong, fresh, freshLong, noPrices, withError };
        foreach (var r in all)
            session.Rows.Add(r);
        if (extra)
            for (var i = 0; i < 44; i++) // ещё 44 строки — список из 50 (прокрутка)
                session.Rows.Add(Known($"46000010{i:00000}", "Товар " + i, i, 100, 70));
        return all;
    }

    /// <summary>Проверки строки по жёстким пределам: название 36–40 px высотой, строка без цен ≤ 68 (с текстом ошибки ≤ 90), с ценами ≤ 160, первая строка ≤ 50, всё внутри списка, выравнивание ±3.</summary>
    private static void CheckRow(Window window, ItemsControl list, BulkScanSession session, BulkScanRow row, string tag, double minName, double maxName, Rect listBounds)
    {
        var index = session.Rows.IndexOf(row);
        list.ScrollIntoView(index); // список с виртуализацией: строка вне окна не существует — подвести к ней
        UiShot.Settle(4, 20);
        var container = list.ContainerFromIndex(index);
        container.Should().NotBeNull($"строка {row.Line.Barcode} создана ({tag})");
        var border = container!.GetVisualDescendants().OfType<Border>().First(b => ReferenceEquals(b.DataContext, row));
        var name = border.GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, "name"));
        var line1 = border.GetVisualDescendants().OfType<Grid>().First();
        var priceFields = border.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && !Equals(t.Tag, "name") && !Equals(t.Tag, "qty"));
        Directory.CreateDirectory(Path.GetDirectoryName(MetricsFile)!);
        File.AppendAllLines(MetricsFile, [$"{tag} {row.Line.Barcode}: row {border.Bounds.Height:0.#} line1 {line1.Bounds.Height:0.#} name {name.Bounds.Width:0.#}x{name.Bounds.Height:0.#} priceFields={priceFields}"]);

        name.Bounds.Height.Should().BeLessThanOrEqualTo(40.5, $"поле названия не выше 40 ({tag}, {row.Line.Barcode})");
        name.Bounds.Height.Should().BeGreaterThanOrEqualTo(36, $"поле названия не схлопнуто ({tag}, {row.Line.Barcode})");
        name.Bounds.Width.Should().BeLessThanOrEqualTo(maxName + 0.5, $"поле названия не шире {maxName} ({tag}, {row.Line.Barcode})");
        name.Bounds.Width.Should().BeGreaterThanOrEqualTo(minName, $"Д-11: поле названия не уже {minName} ({tag}, {row.Line.Barcode})");
        line1.Bounds.Height.Should().BeLessThanOrEqualTo(50, $"первая строка не выше 50 ({tag}, {row.Line.Barcode})");
        if (!priceFields)
            border.Bounds.Height.Should().BeLessThanOrEqualTo(row.HasError ? 90 : 68, $"строка без полей цен не выше 68 (с текстом ошибки — до 90) ({tag}, {row.Line.Barcode})");
        else
            border.Bounds.Height.Should().BeLessThanOrEqualTo(160, $"строка с полями цен не выше 160 ({tag}, {row.Line.Barcode})");

        foreach (var c in border.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0 && !c.GetVisualAncestors().OfType<TextBox>().Any())) // внутри поля ввода текст прокручивается и обрезается самим полем
        {
            var p = c.TranslatePoint(default, window)!.Value;
            (p.X + c.Bounds.Width).Should().BeLessThanOrEqualTo(listBounds.Right + 1, $"{c.GetType().Name} не срезан справа ({tag}, {row.Line.Barcode})");
            p.X.Should().BeGreaterThanOrEqualTo(listBounds.Left - 1, $"{c.GetType().Name} не срезан слева ({tag}, {row.Line.Barcode})");
            if (c.GetType() == typeof(Border) || c is TextBox or Button or CheckBox)
            {
                var pb = c.TranslatePoint(default, border)!.Value;
                (pb.Y + c.Bounds.Height).Should().BeLessThanOrEqualTo(border.Bounds.Height + 0.5, $"{c.GetType().Name} внутри строки ({tag}, {row.Line.Barcode})");
            }
        }

        var cross = border.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "✕"));
        cross.TranslatePoint(new Point(cross.Bounds.Width, 0), border)!.Value.X.Should().BeLessThanOrEqualTo(border.Bounds.Width + 0.5, $"✕ в строке ({tag})");

        // галочка, название, количество и ✕ на одной линии — допуск 3 px (было 12: «одна линия» с запасом ничего не проверяла)
        var centers = new List<double>();
        foreach (var c in new Control?[] { border.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(), name, border.GetVisualDescendants().OfType<TextBox>().First(t => Equals(t.Tag, "qty")), cross })
            if (c is { IsEffectivelyVisible: true })
                centers.Add(c.TranslatePoint(new Point(0, c.Bounds.Height / 2), border)!.Value.Y);
        (centers.Max() - centers.Min()).Should().BeLessThanOrEqualTo(3, $"галочка, название, количество и ✕ на одной линии ({tag}, {row.Line.Barcode})");

        // 2026-11-12 (У-7): у НОВОГО товара название обязательное — в заглушке «*»; и в любом языке
        name.Watermark.Should().EndWith("*", $"заглушка названия с отметкой обязательного поля ({tag})");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task BulkAdd_Row_IsCompact_NameBoxNotHuge_InAllStates(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            Directory.CreateDirectory(Path.GetDirectoryName(MetricsFile)!);
            var tab = (TabItem)typeof(WarehouseWindow).GetField("_bulkTab", Flags)!.GetValue(window)!;
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = tab;
            UiShot.Settle();
            var session = (BulkScanSession)typeof(WarehouseWindow).GetField("_bulk", Flags)!.GetValue(window)!;
            var list = (ItemsControl)typeof(WarehouseWindow).GetField("_bulkList", Flags)!.GetValue(window)!;
            var all = AddSamples(session, extra: true);
            UiShot.Settle(10);

            var tag = $"{width}x{height} {lang}";
            var listBounds = new Rect(list.TranslatePoint(default, window)!.Value, list.Bounds.Size);
            var scroll = ListScroll(list);
            var scrollRect = new Rect(scroll.TranslatePoint(default, window)!.Value, scroll.Bounds.Size);
            foreach (var row in all)
                CheckRow(window, list, session, row, tag, minName: 200, maxName: 450, listBounds);

            // в окне видно несколько строк: наверху списка реализованные строки, помещающиеся целиком
            scroll.Offset = default;
            UiShot.Settle(6, 20);
            var shown = list.GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is BulkScanRow && b.TranslatePoint(default, window) is { } pt && pt.Y >= scrollRect.Top && pt.Y + b.Bounds.Height <= scrollRect.Bottom + 0.5);
            shown.Should().BeGreaterThanOrEqualTo(2, $"в окне видно несколько строк ({tag})");

            // 50 строк — это прокрутка, а не раздувание окна; и это виртуальный список: создана лишь малая часть
            scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height, $"50 строк прокручиваются ({tag})");
            scroll.Extent.Width.Should().BeLessThanOrEqualTo(scroll.Viewport.Width + 1, $"горизонтальной прокрутки нет ({tag})");
            list.GetVisualDescendants().OfType<Border>().Where(b => b.DataContext is BulkScanRow && b.IsEffectivelyVisible).Select(b => b.DataContext).Distinct().Count().Should().BeLessThan(50, $"список с виртуализацией: созданы не все 50 строк ({tag})"); // 2026-11-13: считаем разные строки (DataContext), а не все Border — у одной строки их несколько (75 на 1900×1060 при 50 строках); после обхода всех 50 строк переработанные контейнеры остаются в дереве скрытыми — считаем только видимые (реально созданные в окне)

            Shot(window, $"bulk-add-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-12 (Т-15): СЛУЧАЙ ВЛАДЕЛЬЦА — раздел «Склад» внутри оболочки программы владельца (1850×750 из жалобы и узкое 1100×750): те же пределы, название 200…450 px.</summary>
    [AvaloniaTheory]
    [InlineData(1850, 750)]
    [InlineData(1100, 750)]
    public async Task OwnerShell_BulkAdd_RowsKeepTheLimits_NameBetween200And640(int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
        var shell = new OwnerShellWindow { Width = width, Height = height };
        WarehouseWindow? wh = null;
        var tag = $"владелец {width}x{height}";
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", Flags, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(6, 30);
            typeof(OwnerShellWindow).GetMethod("ApplySectionScale", Flags)!.Invoke(shell, [wh!]);
            typeof(OwnerShellWindow).GetMethod("SyncSectionBounds", Flags)!.Invoke(shell, null);
            UiShot.Settle(6, 30);
            var tab = (TabItem)typeof(WarehouseWindow).GetField("_bulkTab", Flags)!.GetValue(wh!)!;
            UiDrive.Find<TabControl>(wh!, "WarehouseTabs").SelectedItem = tab;
            UiShot.Settle(8, 30);
            var session = (BulkScanSession)typeof(WarehouseWindow).GetField("_bulk", Flags)!.GetValue(wh!)!;
            var list = (ItemsControl)typeof(WarehouseWindow).GetField("_bulkList", Flags)!.GetValue(wh!)!;
            var all = AddSamples(session, extra: true);
            UiShot.Settle(10, 30);

            var listBounds = new Rect(list.TranslatePoint(default, wh!)!.Value, list.Bounds.Size);
            // глубина вложенности оболочки: проверяем геометрию в системе окна раздела
            foreach (var row in all)
                CheckRow(wh!, list, session, row, tag, minName: 200, maxName: 450, listBounds);
            ListScroll(list).Offset = default;
            UiShot.Settle(6, 20);
            Shot(shell, $"bulk-add-owner-{width}x{height}.png");
        }
        finally
        {
            try { shell.Close(); } catch { }
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-13: мастер «Массовая загрузка товаров» (BulkImportWizardWindow): колонка «Название» не шире 460 px и не уже 160 на 1850×750, 1366×768, 1024×640; снимок bulk-wizard-{ширина}x{высота}.png.</summary>
    [AvaloniaTheory]
    [InlineData(1850, 750)]
    [InlineData(1366, 768)]
    [InlineData(1024, 640)]
    public async Task BulkImportWizard_NameColumn_IsLimited(int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new BulkImportWizardWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            var csv = "Штрихкод;Название;Количество;Закупка;Цена продажи;Единица\n"
                + "2000000000011;" + LongName + ";5;80;110;шт\n2000000000028;Короткое;2;10;15;шт\n";
            var table = ProductFileReader.ReadCsv(System.Text.Encoding.UTF8.GetBytes(csv), "t.csv").Table!;
            var mapping = ProductImportMapping.AutoDetect(table.Headers, table);
            var plan = BulkImportPlan.Build(table, mapping, new BulkImportSettings(), new List<CatalogProductTileVm>());
            typeof(BulkImportWizardWindow).GetField("_table", Flags)!.SetValue(window, table);
            typeof(BulkImportWizardWindow).GetField("_mapping", Flags)!.SetValue(window, mapping);
            typeof(BulkImportWizardWindow).GetField("_plan", Flags)!.SetValue(window, plan);
            typeof(BulkImportWizardWindow).GetMethod("BuildRows", Flags)!.Invoke(window, null);
            UiShot.Settle(8, 30);
            var grid = (DataGrid)typeof(BulkImportWizardWindow).GetField("_grid", Flags)!.GetValue(window)!;
            grid.Columns.Should().NotBeEmpty();
            var col = grid.Columns.First(c => Equals(c.Tag, "Name"));
            Directory.CreateDirectory(Path.GetDirectoryName(MetricsFile)!);
            File.AppendAllLines(MetricsFile, [$"мастер {width}x{height}: колонка названия {col.ActualWidth:0.#}"]);
            col.ActualWidth.Should().BeLessThanOrEqualTo(460.5, "колонка названия мастера не шире 460");
            col.ActualWidth.Should().BeGreaterThanOrEqualTo(159.5, "колонка названия мастера не уже 160");
            Shot(window, $"bulk-wizard-{width}x{height}.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-13: старое окно «Массовая загрузка товаров» (BulkImportWindow, CSV с заменой остатка): колонка «Название» не шире 420 px (была 2* ≈ 550 px на 1366); снимок bulk-old-{ширина}x{высота}.png.</summary>
    [AvaloniaTheory]
    [InlineData(1850, 750)]
    [InlineData(1366, 768)]
    [InlineData(1024, 640)]
    public async Task BulkImportOldWindow_NameColumn_IsLimited(int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new BulkImportWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            var grid = UiDrive.Find<DataGrid>(window, "PreviewGrid");
            grid.ItemsSource = new[]
            {
                new { RowNumber = 1, Name = LongName, Barcode = "2000000000011", QuantityText = "5", PriceText = "110", StatusText = "Будет создан" },
                new { RowNumber = 2, Name = "Короткое", Barcode = "2000000000028", QuantityText = "2", PriceText = "15", StatusText = "Обновится" },
            };
            UiShot.Settle(8, 30);
            var col = grid.Columns[1];
            Directory.CreateDirectory(Path.GetDirectoryName(MetricsFile)!);
            File.AppendAllLines(MetricsFile, [$"старое окно {width}x{height}: колонка названия {col.ActualWidth:0.#}"]);
            col.ActualWidth.Should().BeLessThanOrEqualTo(420.5, "колонка названия старого окна не шире 420");
            Shot(window, $"bulk-old-{width}x{height}.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-12 (У-3): 1000 сканов подряд — виртуальный список создаёт только видимые строки; ещё одна строка добавляется быстро и не зависит от длины списка; конец списка достижим.</summary>
    [AvaloniaFact]
    public async Task BulkAdd_ThousandRows_AreVirtualized_AndAddingOneMoreStaysFast()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var tab = (TabItem)typeof(WarehouseWindow).GetField("_bulkTab", Flags)!.GetValue(window)!;
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = tab;
            UiShot.Settle();
            var session = (BulkScanSession)typeof(WarehouseWindow).GetField("_bulk", Flags)!.GetValue(window)!;
            var list = (ItemsControl)typeof(WarehouseWindow).GetField("_bulkList", Flags)!.GetValue(window)!;

            var fill = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++)
                session.Rows.Add(Known($"46000020{i:00000}", "Товар " + i, i % 20, 100, 70));
            UiShot.Settle(10, 20);
            fill.Stop();
            session.Rows.Count.Should().Be(1000);

            var realized = list.GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is BulkScanRow);
            realized.Should().BeLessThan(60, "1000 строк, а создано только видимые (раньше создавались все — 30 тысяч элементов)");
            realized.Should().BeGreaterThanOrEqualTo(3);
            var scroll = ListScroll(list);
            scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height * 10, "список длинный и прокручивается");
            fill.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "1000 добавлений вместе с раскладкой — секунды, а не минуты");

            // ещё одна строка (новая — наверх): отклик не зависит от того, что в списке 1000 строк
            var add = Stopwatch.StartNew();
            session.Rows.Insert(0, Known("4600003000001", "Новый скан", 1, 100, 70));
            UiShot.Settle(4, 20);
            add.Stop();
            add.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "добавление строки в список из 1000 — быстро");
            list.GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is BulkScanRow).Should().BeLessThan(60);

            // конец списка достижим, и там строки есть
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            UiShot.Settle(8, 20);
            var last = session.Rows[^1];
            list.GetVisualDescendants().OfType<Border>().Any(b => ReferenceEquals(b.DataContext, last)).Should().BeTrue("прокрутили в конец — последняя строка создана и видна");
            list.GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is BulkScanRow).Should().BeLessThan(60);

            // «Очистить» большой список
            session.Clear();
            UiShot.Settle(6, 20);
            list.GetVisualDescendants().OfType<Border>().Count(b => b.DataContext is BulkScanRow).Should().Be(0);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
