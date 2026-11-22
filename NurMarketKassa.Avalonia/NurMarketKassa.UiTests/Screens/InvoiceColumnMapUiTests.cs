using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.InvoiceImport;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-22 (жалоба владельца «не понятно как загружать с накладной»): диалог «Столбцы накладной» — столбцы списком с примером значения, инструкция из трёх шагов, предпросмотр «что станет товаром»,
/// понятная причина, почему «Продолжить» недоступна, отметка «Количество = 1 для всех» для файла без количества. Размеры 1024×600 и 1366×768, языки ru, ky, uz:
/// выбор столбцов, предпросмотр и «Продолжить»; поля и подписи не налезают друг на друга (в том числе при раскрытом списке и «Ещё столбцы»), текст не обрезан, окно не больше экрана.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\invoice-columns-*.png. Память «запомнить» пишется во временную папку.</summary>
[Collection("UiScenarios")]
public sealed class InvoiceColumnMapUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "invoice-columns-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public InvoiceColumnMapUiTests()
    {
        TestHost.Ensure();
        InvoiceImportRegistry.DataDirOverride = _dir;
    }

    public void Dispose()
    {
        InvoiceImportRegistry.DataDirOverride = null;
        ModalShell.WorkingAreaForTests = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    private static ProductFileTable Csv(params string[] lines)
    {
        var read = ProductFileReader.ReadCsv(Encoding.UTF8.GetBytes(string.Join("\n", lines)), "Штрих Товар NURCRM.csv");
        read.Ok.Should().BeTrue(read.Error);
        return read.Table!;
    }

    /// <summary>Каталог без количества, как «Штрих Товар NURCRM.xlsx»: штрихкод, наименование, цена, закупочная.</summary>
    private static ProductFileTable CatalogTable() => Csv(
        "Штрих Код;Наименование;Цена;Закупочная",
        ";АЛЬЯНС;0;",
        "4084500490468;Old spice КИЛИМАНЖ сухой;177;153.79",
        "4084500507050;OLD SPISE Lagon спрей;145;131.05",
        "4084500569478;Тайд капсула Альп свежесть 12шт;480;436.75",
        "4084500569508;Тайд капсула Колор 15шт;680;607.5",
        "4084500569999;Лосьон для тела увлажняющий «Нежность» 400 мл с пантенолом;320;281.2");

    private static ProductFileTable InvoiceTable() => Csv(
        "№;Наименование;Ед. изм.;Количество;Цена;Сумма",
        "1;Молоко «Айналайын» 1 л;шт;10;85;850",
        "2;Кефир «Нурсүт» 0,9 л;шт;6;70;420");

    private static void Shot(Window w, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        using var bmp = UiShot.Render(w);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(dir, file), data.ToArray());
    }

    private static List<UserControl> Pickers(Window w) => w.GetVisualDescendants().OfType<UserControl>().Where(u => u.GetType().Name == "ColumnPicker").ToList();

    private static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    /// <summary>Видимая часть элемента: пересечение с областями прокрутки предков (то, что ушло под прокрутку, под подвал не «налезает»). null — целиком скрыт.</summary>
    private static Rect? VisibleRect(Visual v, Visual root)
    {
        var r = RectIn(v, root);
        foreach (var a in v.GetVisualAncestors())
        {
            if (a is not ScrollViewer sv || ReferenceEquals(a, root))
                continue;
            r = r.Intersect(RectIn(sv, root));
            if (r.Width <= 0 || r.Height <= 0)
                return null;
        }
        return r;
    }

    private static bool Shown(Visual v)
    {
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
            if (!cur.IsVisible || cur.Opacity <= 0.01)
                return false;
        return v.Bounds.Width > 0 && v.Bounds.Height > 0;
    }

    /// <summary>Поля формы (кнопки-списки столбцов, отметки) и подписи не налезают друг на друга, ничего не выходит за ширину окна; текст не обрезан; окно не больше экрана.</summary>
    private static void Clean(Window window, string scope, int width, int height, AppLanguage lang)
    {
        FormFieldsOverlapUiTests.FindViolations(window, scope).Select(v => v.Key + " — " + v.Detail).ToList().Should().BeEmpty($"поля и подписи не налезают ({scope} {width}x{height} {lang})");
        TextClipChecker.Find(window, scope, Code(lang)).Select(v => v.Key + " — " + v.Detail).ToList().Should().BeEmpty($"текст не обрезан ({scope} {width}x{height} {lang})");
        window.Bounds.Height.Should().BeLessThanOrEqualTo(height + 0.5, $"окно не выше экрана ({scope} {width}x{height} {lang})");
        window.Bounds.Width.Should().BeLessThanOrEqualTo(width + 0.5, $"окно не шире экрана ({scope} {width}x{height} {lang})");

        // «Поля» этого диалога — кнопки-списки столбцов (ColumnPicker, только верхняя кнопка), «Ещё столбцы» и отметки; подписи — TextBlock вне кнопок.
        var fields = new List<Visual>();
        foreach (var p in Pickers(window).Where(Shown))
            if (p.GetVisualDescendants().OfType<Button>().FirstOrDefault(Shown) is { } top)
                fields.Add(top);
        fields.AddRange(window.GetVisualDescendants().OfType<CheckBox>().Where(Shown));
        var rects = fields.Select(f => (F: f, R: VisibleRect(f, window))).Where(x => x.R is not null).Select(x => (x.F, R: x.R!.Value)).ToList();
        for (var i = 0; i < rects.Count; i++)
        for (var j = i + 1; j < rects.Count; j++)
        {
            var inter = rects[i].R.Intersect(rects[j].R);
            (inter.Width > 2 && inter.Height > 2).Should().BeFalse($"поля не перекрываются ({scope} {width}x{height} {lang}): {rects[i].R} и {rects[j].R}");
        }
        foreach (var f in fields)
        {
            var r = RectIn(f, window);
            (r.Left >= -0.5 && r.Right <= window.Bounds.Width + 0.5).Should().BeTrue($"поле {r} в пределах ширины окна ({scope} {width}x{height} {lang})");
        }
        var labels = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => Shown(t) && !string.IsNullOrWhiteSpace(t.Text) && !t.GetVisualAncestors().Any(a => a is Button or CheckBox)).ToList();
        foreach (var l in labels)
        {
            if (VisibleRect(l, window) is not { } lr)
                continue;
            foreach (var (f, r) in rects)
            {
                var inter = lr.Intersect(r);
                (inter.Width > 2 && inter.Height > 2).Should().BeFalse($"подпись «{l.Text}» не налезает на поле {r} ({scope} {width}x{height} {lang})");
            }
        }
    }

    private static string Everything(Window w) => string.Join("\n", w.GetVisualDescendants().OfType<TextBlock>().Where(Shown).Select(t => t.Text ?? ""));

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task CatalogFile_NoQuantity_ShowsReasonThenCountsQuantityAsOne(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var table = CatalogTable();
        var detection = InvoiceTableParser.Detect(table);
        var window = new InvoiceColumnMapWindow(table, detection.HeaderIndex, detection.Mapping, "Штрих Товар NURCRM.xlsx");
        try
        {
            using (var _ = UiShot.Render(window, new UiSize(width, height)))
            {
            }
            UiShot.Settle(6, 30);
            var code = Code(lang);

            // Автоопределение: штрихкод, наименование, цена закупки («Закупочная») и цена продажи («Цена»), количества нет.
            window.ColumnOf(InvoiceField.Barcode).Should().Be(0);
            window.ColumnOf(InvoiceField.Name).Should().Be(1);
            window.ColumnOf(InvoiceField.Price).Should().Be(3);
            window.ColumnOf(InvoiceField.Retail).Should().Be(2);
            window.ColumnOf(InvoiceField.Quantity).Should().Be(-1);

            // «Продолжить» недоступна и объясняет, чего не хватает; указан пример значения у столбцов.
            window.CanContinue.Should().BeFalse();
            window.HintText.Should().NotBeNullOrWhiteSpace();
            window.HintText.Should().Contain(lang switch { AppLanguage.Kyrgyz => "саны", AppLanguage.Uzbek => "miqdor", _ => "количество" });
            var text = Everything(window);
            text.Should().Contain("4084500490468", "пример значения из первой строки данных");
            text.Should().Contain("Закупочная");
            text.Should().Contain(lang switch { AppLanguage.Kyrgyz => "1. Файл тандалды", AppLanguage.Uzbek => "1. Fayl tanlandi", _ => "1. Файл выбран" }, "инструкция из трёх шагов сверху");
            Shot(window, $"invoice-columns-blocked-{code}-{width}x{height}.png");
            Clean(window, "invoice-columns-blocked", width, height, lang);

            // Список столбцов раскрывается в самом окне и ничего не налезает.
            var qtyPicker = Pickers(window)[3]; // Строка заголовков, Наименование, Штрихкод, Количество
            qtyPicker.GetVisualDescendants().OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(6, 30);
            qtyPicker.GetVisualDescendants().OfType<Button>().Count(Shown).Should().BeGreaterThan(4, "список: «не выбрано» и столбцы A–D");
            Shot(window, $"invoice-columns-list-open-{code}-{width}x{height}.png");
            Clean(window, "invoice-columns-list-open", width, height, lang);
            qtyPicker.GetVisualDescendants().OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // закрыть

            // Явная опция «Количество = 1 для всех»: «Продолжить» доступна, предпросмотр — штрихкод, название, количество, цена.
            window.SetQuantityOne(true);
            UiShot.Settle(6, 30);
            window.CanContinue.Should().BeTrue();
            window.HintText.Should().BeEmpty();
            text = Everything(window);
            text.Should().Contain("153.79", "цена закупки в предпросмотре");
            text.Should().Contain("Old spice КИЛИМАНЖ сухой");
            Shot(window, $"invoice-columns-ready-{code}-{width}x{height}.png");
            Clean(window, "invoice-columns-ready", width, height, lang);

            // «Ещё столбцы» раскрываются без наложения.
            var more = window.GetVisualDescendants().OfType<Button>().First(b => (UiDrive.TextOf(b) ?? "").Contains("▾"));
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(6, 30);
            Clean(window, "invoice-columns-more", width, height, lang);

            window.Accept();
            window.Mapping.Should().NotBeNull();
            window.Mapping!.QuantityOne.Should().BeTrue();
            window.Mapping.IsComplete.Should().BeTrue();
            window.Remember.Should().BeTrue("«Запомнить для файлов с такими же заголовками» включено по умолчанию");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task ClassicInvoice_IsReadyAtOnce_AndMissingFieldBlocksContinue(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var table = InvoiceTable();
        var detection = InvoiceTableParser.Detect(table);
        var window = new InvoiceColumnMapWindow(table, detection.HeaderIndex, detection.Mapping, "накладная.xlsx");
        try
        {
            using (var _ = UiShot.Render(window, new UiSize(width, height)))
            {
            }
            UiShot.Settle(6, 30);
            window.CanContinue.Should().BeTrue("столбцы подобраны сами");
            window.HintText.Should().BeEmpty();
            Everything(window).Should().Contain("Молоко «Айналайын» 1 л", "предпросмотр: первая строка накладной");
            Clean(window, "invoice-columns-classic", width, height, lang);

            // Убрали цену — «Продолжить» недоступна, причина названа.
            window.PickColumn(InvoiceField.Price, -1);
            window.PickColumn(InvoiceField.Sum, -1);
            UiShot.Settle(6, 30);
            window.CanContinue.Should().BeFalse();
            window.HintText.Should().NotBeNullOrWhiteSpace();
            Clean(window, "invoice-columns-classic-noprice", width, height, lang);

            // Выбрали столбец «Цена» снова — снова можно продолжать.
            window.PickColumn(InvoiceField.Price, 4);
            UiShot.Settle(6, 30);
            window.CanContinue.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
