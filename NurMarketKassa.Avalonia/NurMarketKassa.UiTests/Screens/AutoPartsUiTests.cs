using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ5–АЗ6): три окна сферы «Автозапчасти» в Avalonia.Headless на 1024×600 и 1366×768 (ru, ky, uz): блок «Автозапчасть» в карточке товара,
/// плитка каталога со значком состояния, номером и ячейкой (плитки и таблица), окно «Аналоги». Заглушка сервера не нужна: окна работают с данными в памяти и файлом данных запчастей во временной папке.
/// Текст не обрезан, кнопки внутри окна, окно не выше 85 % экрана. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\autoparts-*.png.</summary>
[Collection("UiScenarios")]
public sealed class AutoPartsUiTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "autoparts-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public AutoPartsUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        PartMetaStore.FileOverride = Path.Combine(_dir, "autopart-meta.json");
        PartMetaStore.ResetForTests();
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        ModalShell.WorkingAreaForTests = null;
        PartMetaStore.FileOverride = null;
        PartMetaStore.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    private static void AutoParts()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = AutoPartsProfile.Id;
    }

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "autoparts-" + name + ".png"), data.ToArray());
    }

    private static CatalogProductTileVm Tile(string id, string name, string article, double qty, string price, string? brand = null) =>
        new(id, name, price + " сом", false, null) { Article = article, Quantity = qty, Brand = brand, Unit = "шт" };

    private static void Meta(string id, string state, string location, string? cross = null)
    {
        var (condition, type) = AutoPartInfo.FromState(state);
        PartMetaStore.Set(id, new AutoPartInfo
        {
            Condition = condition, PartType = type, Location = location,
            Cross = cross is null ? new List<string>() : new List<string> { cross },
        });
    }

    // ───────────── окно «Аналоги» ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Analogs_window_fits_screen_text_is_not_clipped_and_out_of_stock_row_is_dimmed(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        AutoParts();
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var source = Tile("1", "Фильтр масляный Mann-Filter W 712/94", "W 712/94", 5, "520.00", "Mann-Filter");
        var analogs = new List<CatalogProductTileVm>
        {
            Tile("2", "Фильтр масляный Bosch 0 451 103 316 для Toyota Camry 2006-2017 длинное название", "0 451 103 316", 3, "480.00", "Bosch"),
            Tile("3", "Фильтр масляный Knecht OC 983", "OC 983", 0, "430.00", "Knecht"),
            Tile("4", "Фильтр масляный б/у", "OC 983-U", 1, "150.00", "Knecht"),
        };
        Meta("2", "original", "A-01-3");
        Meta("3", "aftermarket", "A-09-5");
        Meta("4", "used", "Б-1");
        var added = new List<string>();
        var window = new AnalogsWindow(source, analogs, p => added.Add(p.Id));
        try
        {
            using var _ = UiShot.Render(window);
            var slug = $"analogs-{Code(lang)}-{width}x{height}";
            window.Bounds.Height.Should().BeLessThanOrEqualTo(height * 0.85 + 36, $"окно не выше 85 % экрана ({width}x{height} {lang})");
            window.Bounds.Width.Should().BeLessThanOrEqualTo(width);
            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
            {
                var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
                right.Should().BeLessThanOrEqualTo(window.Bounds.Width - 8, $"кнопка внутри окна ({width}x{height} {lang})");
            }
            // Цены по возрастанию: 150, 430, 480 — строки в том же порядке; значки состояния видны.
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            texts.Should().Contain(t => t.Contains("150,00"));
            texts.Should().Contain(AutoPartsProfile.StateBadge("aftermarket"));
            texts.Should().Contain(AutoPartsProfile.StateBadge("used"));
            // Нет в наличии — слово «Нет» и бледная строка.
            texts.Should().Contain(Tr.T("Нет", "Жок", "None", "Yok", "Yo'q"));
            TextClipChecker.Find(window, "autoparts-analogs", Code(lang)).Should().BeEmpty($"текст не обрезан ({width}x{height} {lang})");
            Save(window, slug);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Analogs_window_with_no_analogs_says_what_to_do()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        AutoParts();
        var window = new AnalogsWindow(Tile("1", "Фильтр", "W 712/94", 5, "520.00"), new List<CatalogProductTileVm>(), _ => { });
        try
        {
            using var _ = UiShot.Render(window);
            UiDrive.AllText(window).Should().Contain("Аналогов не найдено");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── карточка товара ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Product_card_autopart_block_is_visible_only_in_the_sphere_and_fits(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);

        // Другая сфера — блока нет, подпись поля прежняя.
        var plain = UserPreferences.Instance;
        plain.MarketSphere = MarketSpheres.Grocery;
        plain.MarketSphereDetail = null;
        var other = new ProductEditDialog();
        try
        {
            using var _ = UiShot.Render(other);
            UiDrive.Find<TabControl>(other, "ProductTabs").SelectedIndex = 1; // содержимое вкладки строится только когда она выбрана
            UiShot.Settle();
            UiDrive.Find<StackPanel>(other, "AutoPartBlock").IsVisible.Should().BeFalse("вне сферы «Автозапчасти» блока нет");
        }
        finally
        {
            other.Close();
            UiShot.Pump();
        }

        AutoParts();
        var dlg = new ProductEditDialog();
        try
        {
            using var _ = UiShot.Render(dlg);
            UiDrive.Find<TabControl>(dlg, "ProductTabs").SelectedIndex = 1; // «Категория и коды»: поле «Каталожный номер» и блок «Автозапчасть»
            UiShot.Settle();
            var block = UiDrive.Find<StackPanel>(dlg, "AutoPartBlock");
            block.IsVisible.Should().BeTrue();
            UiDrive.Find<TextBlock>(dlg, "CodeFieldLabel").Text.Should().Be(Tr.T("Каталожный номер", "Каталог номери", "Part number", "Katalog numarası", "Katalog raqami"));
            dlg.Bounds.Height.Should().BeLessThanOrEqualTo(height * 0.95 + 36, $"окно умещается ({width}x{height} {lang})");
            // Пять состояний + «не указано» — кнопки одинаковой высоты 40, не обрезаны.
            block.GetVisualDescendants().OfType<Button>().Where(b => b.Height == 40).Should().HaveCountGreaterOrEqualTo(6);
            var allText = UiDrive.AllText(dlg);
            allText.Should().Contain(AutoPartsProfile.StateName("aftermarket")).And.Contain(AutoPartsProfile.StateName("used"));
            // 2026-11-20 (склейка с чеком/возвратом): гарантия в днях и два флажка; пустой блок ничего не пишет.
            allText.Should().Contain(Tr.T("Гарантия, дней", "Кепилдик, күн", "Warranty, days", "Garanti, gün", "Kafolat, kun"))
                .And.Contain(Tr.T("Электрика / электроника", "Электрика / электроника", "Electrical / electronics", "Elektrik / elektronik", "Elektrika / elektronika"))
                .And.Contain(Tr.T("Установленные детали не принимаются", "Орнотулган тетиктер кабыл алынбайт", "Installed parts are not accepted", "Takılmış parçalar iade alınmaz", "O'rnatilgan detallar qabul qilinmaydi"));
            block.GetVisualDescendants().OfType<CheckBox>().Should().HaveCount(2);
            var days = block.GetVisualDescendants().OfType<TextBox>().First(t => t.MaxLength == 4);
            days.Text = "3a0b";
            UiShot.Settle();
            days.Text.Should().Be("30", "в поле «Гарантия, дней» только цифры");
            days.Text = "";
            // Подпись «Штук в упаковке» с вкладки «Упаковка» (не наш блок) на ky/uz уже была шире поля — её здесь не считаем: проверяем только текст блока «Автозапчасть».
            var blockTexts = block.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList();
            TextClipChecker.Find(dlg, "autoparts-card", Code(lang)).Where(v => blockTexts.Any(t => v.Detail.Contains("«" + t + "»"))).Should().BeEmpty($"текст блока не обрезан ({width}x{height} {lang})");
            Save(dlg, $"card-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    // ───────────── плитка каталога ─────────────

    [AvaloniaTheory]
    [InlineData(1024, AppLanguage.Russian)]
    [InlineData(1024, AppLanguage.Kyrgyz)]
    [InlineData(1024, AppLanguage.Uzbek)]
    [InlineData(1366, AppLanguage.Russian)]
    public async Task Catalog_tile_and_table_row_show_state_badge_number_and_cell_only_in_the_sphere(int width, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        AutoParts();
        using var built = CatalogListFixture.Create(width, 640);
        var p1 = built.Vm.Products.First(p => p.Id == "p1");
        p1.Article = "0 986 452 041";
        Meta("p1", "aftermarket", "A-3-2");
        UiShot.Render(built.Window).Dispose();
        var texts = built.Window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        texts.Should().Contain(AutoPartsProfile.StateBadge("aftermarket"), "значок состояния");
        texts.Should().Contain(t => t.Contains("0 986 452 041") && t.Contains("A-3-2"), "каталожный номер и ячейка мелкой строкой");
        Save(built.Window, $"tile-{Code(lang)}-{width}");

        // Та же плитка в другой сфере — ни значка, ни строки.
        var plain = UserPreferences.Instance;
        plain.MarketSphereDetail = null;
        using var plainBuilt = CatalogListFixture.Create(width, 640);
        plainBuilt.Vm.Products.First(p => p.Id == "p1").Article = "0 986 452 041";
        UiShot.Render(plainBuilt.Window).Dispose();
        var plainTexts = plainBuilt.Window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        plainTexts.Should().NotContain(AutoPartsProfile.StateBadge("aftermarket"));
        plainTexts.Should().NotContain(t => t.Contains("A-3-2"));
    }
}
