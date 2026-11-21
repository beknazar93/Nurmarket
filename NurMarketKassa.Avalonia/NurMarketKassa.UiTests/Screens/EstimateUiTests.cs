using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19: окна «Сметы» в Avalonia.Headless на заглушках (временный файл, свой каталог, касса, печать, WhatsApp, буфер): список (поиск, дубль, удаление с подтверждением), редактор (позиции,
/// своя позиция, уровень «прораб», запас, скидки, закупка скрыта от кассира, «Пересчитать цены», текст и WhatsApp, «Добавить всё в чек» с изменениями цены и остатка), калькулятор материала, печать,
/// карточка Маркетплейса. Текст не обрезан на 1024×600 в ru, ky, uz, tr, en. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\estimate-*.png.</summary>
[Collection("UiScenarios")]
public sealed class EstimateUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "estimate-ui-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTime Today = new(2026, 11, 19);

    public EstimateUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ModalShell.WorkingAreaForTests = null;
        HardwareCalcSettingsReset();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static void HardwareCalcSettingsReset() => NurMarketKassa.Services.Hardware.HardwareCalcSettings.Use(null);

    private sealed class FakeCatalog : IEstimateCatalog
    {
        public Dictionary<string, EstimateCatalogInfo> Items { get; } = new()
        {
            ["p1"] = new("p1", "Краска акриловая белая 10 л", "банка", 3200m, 3000m, 2500m, 20m, false),
            ["p2"] = new("p2", "Плитка керамическая 30х60 1,44 м2", "уп", 1800m, 1700m, 1400m, 40m, false),
            ["p3"] = new("p3", "Клей плиточный 25 кг", "мешок", 450m, 420m, 350m, 3m, false),
        };

        public List<EstimateCatalogInfo> Search(string query, int max = 8) =>
            Items.Values.Where(i => i.Id == query || i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(max).ToList();

        public EstimateCatalogInfo? Find(string id) => Items.GetValueOrDefault(id);
    }

    private sealed class FakeTill : IEstimateTill
    {
        public List<(string Id, decimal Qty)> Products { get; } = new();
        public List<(string Name, decimal Amount)> Custom { get; } = new();
        public HashSet<string> Refuse { get; } = new();

        public Task<bool> AddProductAsync(string productId, decimal quantity)
        {
            if (Refuse.Contains(productId))
                return Task.FromResult(false);
            Products.Add((productId, quantity));
            return Task.FromResult(true);
        }

        public bool AddCustomLine(string name, decimal amount)
        {
            Custom.Add((name, amount));
            return true;
        }
    }

    private sealed class Spy
    {
        public List<string> Copied { get; } = new();
        public List<(string? Phone, string Text)> Whats { get; } = new();
        public List<string> Ribbon { get; } = new();
        public List<string> Questions { get; } = new();
        public bool Answer { get; set; } = true;
        public bool SeeCost { get; set; }
    }

    private (EstimateDeps Deps, FakeCatalog Catalog, FakeTill? Till, Spy Spy) Deps(bool till = true, bool seeCost = false)
    {
        var catalog = new FakeCatalog();
        var fake = till ? new FakeTill() : null;
        var spy = new Spy { SeeCost = seeCost };
        var deps = new EstimateDeps
        {
            Store = new EstimateStore(Path.Combine(_dir, "estimates-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => Today.AddHours(10)),
            Catalog = catalog,
            Till = fake,
            PrintRibbon = text => { spy.Ribbon.Add(text); return Task.FromResult<string?>(null); },
            OpenWhatsApp = (phone, text) => spy.Whats.Add((phone, text)),
            CopyText = text => { spy.Copied.Add(text); return Task.CompletedTask; },
            Confirm = (message, _) => { spy.Questions.Add(message); return Task.FromResult(spy.Answer); },
            CanSeeCost = () => spy.SeeCost,
            CanEditNorms = () => true,
            Today = () => Today,
            Buyers = () => Task.FromResult(new List<ClientDocsUi.Buyer>()),
            Cashier = () => "Тест",
        };
        return (deps, catalog, fake, spy);
    }

    private static string Code(AppLanguage l) => l switch
    {
        AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.Turkish => "tr", AppLanguage.English => "en", _ => "ru",
    };

    public static IEnumerable<object[]> Languages() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek, AppLanguage.Turkish, AppLanguage.English }.Select(l => new object[] { l });

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
    }

    private static T Named<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>Ввод в поле: TextChanged в Headless приходит через очередь окна — даём ей отработать.</summary>
    private static void Type(Window w, string name, string text)
    {
        Named<TextBox>(w, name).Text = text;
        UiShot.Settle(3);
    }

    private static EstimateDoc Filled(EstimateStore store, FakeCatalog catalog, EstimateLevel level = EstimateLevel.Retail)
    {
        var doc = store.Create(d =>
        {
            d.Title = "Ремонт комнаты 18 м² на ул. Ленина";
            d.ClientName = "Бакыт Асанов";
            d.Phone = "0700 123 456";
            d.Address = "ул. Ленина 5, кв. 12";
            d.Level = level;
            d.DiscountPercent = 5m;
        });
        doc.Sections.Add(new EstimateSection { Name = "Стены" });
        doc.Sections.Add(new EstimateSection { Name = "Пол" });
        var paint = new EstimateLine { Qty = 2m, Unit = "банка", SectionId = doc.Sections[0].Id };
        EstimateRules.FillFromCatalog(paint, catalog.Items["p1"], level);
        var tile = new EstimateLine { Qty = 10m, Unit = "уп", SectionId = doc.Sections[1].Id, ReservePercent = 0m, CalcNote = "Площадь 12 м² → 10 уп." };
        EstimateRules.FillFromCatalog(tile, catalog.Items["p2"], level);
        var glue = new EstimateLine { Qty = 5m, Unit = "мешок", SectionId = doc.Sections[1].Id };
        EstimateRules.FillFromCatalog(glue, catalog.Items["p3"], level);
        var work = new EstimateLine { Name = "Работа: укладка плитки и покраска стен", Unit = "усл.", Qty = 1m, Price = 15000m, ReservePercent = 0m };
        doc.Lines.AddRange([paint, tile, glue, work]);
        store.Save(doc);
        return doc;
    }

    // ───────────────────────── список ─────────────────────────

    [AvaloniaFact]
    public async Task List_SearchDuplicateDelete_AndHint()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, catalog, _, spy) = Deps();
        var first = Filled(deps.Store, catalog);
        deps.Store.Create(d => { d.Title = "Баня"; d.ClientName = "Омурбек"; });
        var window = new EstimatesWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);
            window.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("Row_", StringComparison.Ordinal) == true).Should().Be(2);
            window.StatusMessage.Should().Contain("только на этом компьютере", "подсказка про локальное хранение");

            // Поиск по клиенту
            Type(window, "EstimateSearch", "Омурбек");
            UiShot.Settle(4);
            window.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("Row_", StringComparison.Ordinal) == true).Should().Be(1);
            Type(window, "EstimateSearch", "");
            UiShot.Settle(4);

            // Дублирование: черновик, новый номер, те же позиции
            window.Select(first.Id);
            var copy = window.Duplicate(first.Id)!;
            copy.Number.Should().Be(3);
            copy.Status.Should().Be(EstimateStatus.Draft);
            deps.Store.Find(copy.Id)!.Lines.Should().HaveCount(4);

            // Удаление — только с подтверждением
            spy.Answer = false;
            (await window.DeleteAsync(copy.Id)).Should().BeFalse();
            deps.Store.Find(copy.Id).Should().NotBeNull("отказ — смета осталась");
            spy.Answer = true;
            (await window.DeleteAsync(copy.Id)).Should().BeTrue();
            deps.Store.Find(copy.Id).Should().BeNull();
            spy.Questions.Should().HaveCount(2);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task List_TextIsNotClipped_At1024x600(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (deps, catalog, _, _) = Deps(seeCost: true);
        var doc = Filled(deps.Store, catalog);
        deps.Store.SetStatus(doc.Id, EstimateStatus.Accepted);
        var old = deps.Store.Create(d => { d.Title = "Старый объект"; d.ValidUntil = Today.AddDays(-3); });
        var window = new EstimatesWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);
            window.Select(old.Id);
            UiShot.Settle(4);
            TextClipChecker.Find(window, "estimate-list", Code(lang)).Should().BeEmpty($"список смет не обрезан ({Code(lang)})");
            window.Select(doc.Id);
            UiShot.Settle(4);
            TextClipChecker.Find(window, "estimate-list-detail", Code(lang)).Should().BeEmpty($"карточка сметы не обрезана ({Code(lang)})");
            Save(window, $"estimate-list-{Code(lang)}.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────────────────── редактор ─────────────────────────

    [AvaloniaFact]
    public async Task Editor_AddProduct_ScanEnter_CustomLine_EditInPlace_Totals()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, _, _, _) = Deps();
        var doc = deps.Store.Create();
        var window = new EstimateEditorWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);

            // Сканер: точный код + Enter — одна позиция; повтор — плюс 1 к количеству
            Type(window, "EstimateAdd", "p2");
            window.AddFromBox().Should().BeTrue();
            window.Doc.Lines.Should().ContainSingle().Which.Price.Should().Be(1800m);
            Type(window, "EstimateAdd", "p2");
            window.AddFromBox();
            window.Doc.Lines.Should().ContainSingle().Which.Qty.Should().Be(2m, "тот же товар — увеличивает количество");

            // Не найдено — подсказка, позиция не добавилась
            Type(window, "EstimateAdd", "несуществующий");
            window.AddFromBox().Should().BeFalse();
            window.Doc.Lines.Should().HaveCount(1);

            // Своя позиция
            var work = window.AddCustomLine();
            UiShot.Settle(4);
            Type(window, "LinePrice_2", "2500");
            work.Price.Should().Be(2500m);
            Type(window, "LineQty_1", "10");
            window.Doc.Lines[0].Qty.Should().Be(10m);

            // Скидка строки и запас строки
            Type(window, "LineDisc_1", "10");
            Type(window, "LineReserve_1", "10");
            window.Doc.Lines[0].DiscountPercent.Should().Be(10m);
            window.Doc.Lines[0].ReservePercent.Should().Be(10m);
            // 10 × 1,1 = 11 упаковок × (1800 − 10 %) = 1620 → 17 820; + работа 2 500
            EstimateRules.Total(window.Doc).Should().Be(20320m);
            window.TotalsText.Should().Contain(MoneyText.Som(20320m));

            // Общая скидка
            Type(window, "EstimateDiscount", "10");
            window.Doc.DiscountPercent.Should().Be(10m);
            window.TotalsText.Should().Contain(MoneyText.Som(18288m));

            // Неверное число не портит данные
            Type(window, "LinePrice_2", "25x0");
            work.Price.Should().Be(2500m);

            // Автосохранение при закрытии
            window.PersistNow();
            var saved = deps.Store.Find(doc.Id)!;
            saved.Lines.Should().HaveCount(2);
            saved.DiscountPercent.Should().Be(10m);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Editor_ForemanLevel_RefreshPrices_ExpiryBanner_AndPurchaseHiddenFromCashier()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, catalog, _, spy) = Deps(seeCost: false);
        var doc = Filled(deps.Store, catalog);
        doc.ValidUntil = Today.AddDays(-1);
        deps.Store.Save(doc);
        var window = new EstimateEditorWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);
            window.ExpiredBannerVisible.Should().BeTrue("срок прошёл — «Цены могли измениться»");
            window.InternalText.Should().BeEmpty("кассир не видит закупку и маржу");

            // Прораб: цены строк-товаров — оптовые
            window.SelectLevel(EstimateLevel.Foreman);
            UiShot.Settle(4);
            window.Doc.Lines.First(l => l.ProductId == "p1").Price.Should().Be(3000m);
            window.Doc.Lines.Single(l => l.IsCustom).Price.Should().Be(15000m, "своя позиция не меняется");

            // Каталог подорожал — «Пересчитать цены» обновляет, срок снова 7 дней, баннер уходит
            catalog.Items["p1"] = catalog.Items["p1"] with { Wholesale = 3100m };
            var changes = window.RefreshPrices();
            changes.Should().ContainSingle(c => c.NewPrice == 3100m);
            window.Doc.ValidUntil.Should().Be(Today.AddDays(7));
            window.ExpiredBannerVisible.Should().BeFalse();
            window.StatusMessage.Should().Contain("3");

            // Владелец / право на закупку — видит себестоимость и маржу
            spy.SeeCost = true;
            Type(window, "LineQty_1", "3");
            window.InternalText.Should().NotBeNullOrWhiteSpace().And.Contain(MoneyText.Som(EstimateRules.Cost(window.Doc)));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Editor_CopyText_AndWhatsApp_HaveNoPurchase_AndMarkSent()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, catalog, _, spy) = Deps(seeCost: true);
        var doc = Filled(deps.Store, catalog);
        var window = new EstimateEditorWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            var text = await window.CopyTextAsync();
            spy.Copied.Should().ContainSingle().Which.Should().Be(text);
            text.Should().Contain("Краска").And.Contain("Работа: укладка").And.Contain("Бакыт").And.Contain("26.11.2026");
            text.Should().NotContain("2500").And.NotContain("2 500,00").And.NotContain("Маржа");
            doc.Status.Should().Be(EstimateStatus.Sent);
            deps.Store.Find(doc.Id)!.Status.Should().Be(EstimateStatus.Sent);

            await window.OpenWhatsAppAsync();
            spy.Whats.Should().ContainSingle().Which.Phone.Should().Be("0700 123 456");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Editor_AddAllToCart_ShowsChanges_AsksFirst_AcceptsEstimate()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, catalog, till, spy) = Deps();
        var doc = Filled(deps.Store, catalog);
        // С момента сметы: краска подорожала 3 000 → 3 200; клея осталось 3 вместо нужных 5 (было 10)
        doc.Lines.First(l => l.ProductId == "p1").Price = 3000m;
        doc.Lines.First(l => l.ProductId == "p3").StockAtTime = 10m;
        deps.Store.Save(doc);
        var window = new EstimateEditorWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);

            // Отказ — в чеке ничего, статус прежний
            spy.Answer = false;
            (await window.AddToCartAsync()).Should().BeFalse();
            till!.Products.Should().BeEmpty();
            till.Custom.Should().BeEmpty();
            doc.Status.Should().Be(EstimateStatus.Draft);
            var question = spy.Questions.Should().ContainSingle().Subject;
            question.Should().Contain("Краска", "показано, что изменилось: цена").And.Contain("→");
            question.Should().Contain("Клей", "показано, что изменилось: остаток");
            question.Should().Contain("Скидки сметы", "скидки в чек не переносятся — об этом сказано");

            // Согласие — всё в чек: товары по количеству с запасом, своя позиция — «Доп. услуга» на сумму строки
            spy.Answer = true;
            (await window.AddToCartAsync()).Should().BeTrue();
            till.Products.Should().Equal(("p1", 2m), ("p2", 10m), ("p3", 5m));
            till.Custom.Should().ContainSingle().Which.Should().Be(("Работа: укладка плитки и покраска стен", 15000m));
            window.AddedToCart.Should().BeTrue();
            doc.Status.Should().Be(EstimateStatus.Accepted);
            deps.Store.Find(doc.Id)!.Status.Should().Be(EstimateStatus.Accepted);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Editor_AddAllToCart_PartialOrMissing_DoesNotAcceptEstimate()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, catalog, till, spy) = Deps();
        var doc = Filled(deps.Store, catalog);
        till!.Refuse.Add("p3"); // кассир отказался от продажи без остатка
        var window = new EstimateEditorWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            (await window.AddToCartAsync()).Should().BeFalse();
            till.Products.Should().HaveCount(2);
            doc.Status.Should().NotBe(EstimateStatus.Accepted, "перенесено не всё");
            window.StatusMessage.Should().Contain("3", "добавлено 3 из 4");

            catalog.Items.Remove("p2"); // товара нет в каталоге
            spy.Questions.Clear();
            till.Refuse.Clear();
            till.Products.Clear();
            await window.AddToCartAsync();
            spy.Questions.Single().Should().Contain("Плитка", "сказано, что товара больше нет в каталоге");
            till.Products.Select(p => p.Id).Should().NotContain("p2");
            doc.Status.Should().NotBe(EstimateStatus.Accepted);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Editor_InOwnerProgram_HasNoAddToCartButton()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, catalog, _, _) = Deps(till: false, seeCost: true);
        var doc = Filled(deps.Store, catalog);
        var window = new EstimateEditorWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            window.GetVisualDescendants().OfType<Button>().Should().NotContain(b => b.Name == "EstimateToCart");
            (await window.AddToCartAsync()).Should().BeFalse();
            window.InternalText.Should().NotBeEmpty("в программе владельца видны себестоимость и маржа");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Editor_TextIsNotClipped_At1024x600(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (deps, catalog, _, _) = Deps(seeCost: true);
        var doc = Filled(deps.Store, catalog, EstimateLevel.Foreman);
        doc.ValidUntil = Today.AddDays(-2);
        var window = new EstimateEditorWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(6);
            TextClipChecker.Find(window, "estimate-editor", Code(lang)).Should().BeEmpty($"редактор сметы не обрезан ({Code(lang)})");
            Save(window, $"estimate-editor-{Code(lang)}.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────────────────── раскладка редактора (замечание владельца «очень неудобная!», 20.11) ─────────────────────────

    public static IEnumerable<object[]> LayoutCases() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.SelectMany(l => new[] { (1024, 600), (1366, 768) }.Select(sz => new object[] { l, sz.Item1, sz.Item2 }));

    private static Avalonia.Rect RectOf(Window w, Control c) => new(Avalonia.VisualExtensions.TranslatePoint(c, default, w)!.Value, c.Bounds.Size);

    /// <summary>2026-11-20: таблица позиций — главная часть окна. Закреплённый заголовок виден целиком, колонки строки совпадают с заголовком по X (±2 px), таблица занимает заметную долю высоты окна
    /// (свёрнутая шапка — больше, развёрнутая — меньше), нижняя панель закреплена и видна, 20 позиций прокручиваются внутри таблицы, а таблица и нижняя панель не растут. Доли высоты — в отчёте
    /// ui-actual/estimate-layout.txt (папка nurmarket-tests во временной папке), снимки — estimate-editor-table-*.png.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(LayoutCases))]
    public async Task Editor_Table_IsMainPart_HeaderAligned_BottomPinned_Scrolls(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (deps, catalog, _, _) = Deps(seeCost: true);
        var doc = Filled(deps.Store, catalog);
        var window = new EstimateEditorWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            var expander = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "EstimateHeaderExpander");
            var table = Named<Grid>(window, "EstimateTable");
            var sv = table.GetVisualDescendants().OfType<ScrollViewer>().First();
            var report = new List<string>();
            foreach (var expanded in new[] { false, true })
            {
                expander.IsExpanded = expanded;
                UiShot.Settle(6);
                var t = RectOf(window, sv);
                Save(window, $"estimate-editor-table-{Code(lang)}-{width}x{height}-{(expanded ? "open" : "closed")}.png");
                var frac = t.Height / window.ClientSize.Height;
                report.Add($"{Code(lang)} {width}x{height} шапка {(expanded ? "развёрнута" : "свёрнута")}: таблица {t.Height:0} px = {frac:P0} окна");
                frac.Should().BeGreaterThan(expanded ? (height <= 600 ? 0.10 : 0.28) : (height <= 600 ? 0.38 : 0.50), "таблица позиций — главная часть окна (" + report.Last() + ")");

                // заголовок виден целиком и закреплён
                var head = Named<Grid>(window, "EstimateHeaderRow");
                var hr = RectOf(window, head);
                hr.Left.Should().BeGreaterThanOrEqualTo(-1);
                hr.Right.Should().BeLessThanOrEqualTo(window.ClientSize.Width + 1);
                (hr.Bottom <= t.Top + 1).Should().BeTrue("заголовок над строками, не внутри прокрутки");
                TextClipChecker.Find(window, "estimate-editor", Code(lang)).Should().BeEmpty("текст заголовка и строк не обрезан");

                // колонки строки совпадают с заголовком по X
                var row = Named<Grid>(window, "EstimateRow_1");
                (string Field, int Col)[] cols = [("LineName_1", 1), ("LineUnit_1", 2), ("LineQty_1", 3), ("LinePrice_1", 4), ("LineDisc_1", 5), ("LineReserve_1", 6)];
                var misaligned = new List<string>();
                foreach (var (field, col) in cols)
                {
                    var f = RectOf(window, Named<TextBox>(window, field));
                    var h = RectOf(window, Named<TextBlock>(window, "EstimateHead_" + col));
                    if (Math.Abs(f.Left - h.Left) > 2)
                        misaligned.Add($"{field}: поле {f.Left:0.#}…{f.Right:0.#}, заголовок {h.Left:0.#}…{h.Right:0.#}, колонка {row.ColumnDefinitions[col].ActualWidth:0.#}, заголовок-сетка {Named<Grid>(window, "EstimateHeaderRow").ColumnDefinitions[col].ActualWidth:0.#}");
                }
                misaligned.Should().BeEmpty($"колонки строки выровнены с заголовком ({Code(lang)} {width}x{height}, строка {RectOf(window, row).Left:0.#}…{RectOf(window, row).Right:0.#}, заголовок {RectOf(window, head).Left:0.#}…{RectOf(window, head).Right:0.#})");
                var sumCol = row.ColumnDefinitions.Count - 2;
                Math.Abs(RectOf(window, Named<TextBlock>(window, "LineSum_1")).Right - RectOf(window, Named<TextBlock>(window, "EstimateHead_" + sumCol)).Right)
                    .Should().BeLessThanOrEqualTo(2, "«Сумма» выровнена по правому краю с заголовком");

                // нижняя панель закреплена и видна целиком
                var print = RectOf(window, Named<Button>(window, "EstimatePrint"));
                print.Bottom.Should().BeLessThanOrEqualTo(window.ClientSize.Height + 1, "кнопки внизу видны");
                (t.Bottom <= print.Top + 1).Should().BeTrue("таблица не заходит под нижнюю панель");
                var totals = RectOf(window, Named<TextBlock>(window, "EstimateTotals"));
                totals.Bottom.Should().BeLessThanOrEqualTo(window.ClientSize.Height + 1, "«Итого» видно целиком");
                (totals.Top >= t.Bottom - 1).Should().BeTrue("«Итого» под таблицей");
            }

            // 20 позиций: прокручиваются внутри таблицы, размер таблицы и нижней панели не меняется
            expander.IsExpanded = false;
            UiShot.Settle(4);
            var before = RectOf(window, sv).Height;
            var printBefore = RectOf(window, Named<Button>(window, "EstimatePrint")).Top;
            for (var i = 0; i < 20; i++)
                window.AddCustomLine();
            UiShot.Settle(8);
            sv.Extent.Height.Should().BeGreaterThan(sv.Viewport.Height, "20 позиций не помещаются — таблица прокручивается");
            RectOf(window, sv).Height.Should().BeApproximately(before, 1, "таблица не растёт от числа позиций");
            RectOf(window, Named<Button>(window, "EstimatePrint")).Top.Should().BeApproximately(printBefore, 1, "нижняя панель закреплена");
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.AppendAllLines(Path.Combine(dir, "estimate-layout.txt"), report);
            expander.IsExpanded = false;
            Save(window, $"estimate-editor-table-{Code(lang)}-{width}x{height}.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────────────────── калькулятор материала ─────────────────────────

    [AvaloniaFact]
    public async Task Calc_TileRoom_PacksRoundedUp_LinksProduct_AddsLine()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Use(NurMarketKassa.Services.Hardware.HardwareCalcSettings.CreateDefault());
        var (deps, catalog, _, _) = Deps();
        var doc = deps.Store.Create();
        var added = new List<EstimateLine>();
        var dlg = new EstimateCalcDialog(deps, doc, line => { added.Add(line); return true; }, EstimateMaterial.Tile);
        try
        {
            using var _ = UiShot.Render(dlg, new UiSize(1024, 600));
            UiShot.Settle(6);
            dlg.SetField("len", "4"); UiShot.Settle(2);
            dlg.SetField("wid", "3"); UiShot.Settle(2);
            UiShot.Settle(3);
            dlg.Last!.IsValid.Should().BeTrue();
            dlg.Last.Raw.Packs.Should().Be(10, "12 м² + запас 10 % при упаковке 1,44 м² — 10 упаковок вверх");
            Named<TextBlock>(dlg, "CalcFormula").Text.Should().Contain("10");

            // Без товара — «своя» строка упаковками
            dlg.AddLine(close: false).Should().BeTrue();
            added.Should().ContainSingle().Which.IsCustom.Should().BeTrue();

            // С товаром — цена каталога, упаковки; привязка запоминается
            dlg.Link(catalog.Items["p2"]);
            dlg.AddLine(close: false).Should().BeTrue();
            added[1].ProductId.Should().Be("p2");
            added[1].Qty.Should().Be(10m);
            added[1].Price.Should().Be(1800m);
            added[1].ReservePercent.Should().Be(0m, "запас уже в упаковках");
            deps.Store.GetLink("tile").Should().Be("p2");

            // Проёмы больше площади — ошибка, строка не добавляется
            dlg.SetField("open", "20"); UiShot.Settle(2);
            UiShot.Settle(3);
            dlg.Last!.IsValid.Should().BeFalse();
            dlg.AddLine(close: false).Should().BeFalse();
            added.Should().HaveCount(2);

            // Материал: краска стены — норма из таблицы, банки
            dlg.SetField("open", "0"); UiShot.Settle(2);
            dlg.SelectMaterial(EstimateMaterial.Paint);
            UiShot.Settle(3);
            dlg.SetField("hgt", "2.5"); UiShot.Settle(3);
            dlg.Last!.IsValid.Should().BeTrue();
            dlg.Last.Area.Should().Be(35m, "периметр 14 × высота 2,5");
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Calc_TextIsNotClipped_At1024x600(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Use(NurMarketKassa.Services.Hardware.HardwareCalcSettings.CreateDefault());
        var (deps, catalog, _, _) = Deps();
        var doc = deps.Store.Create();
        var dlg = new EstimateCalcDialog(deps, doc, _ => true, EstimateMaterial.Paint);
        try
        {
            using var _ = UiShot.Render(dlg, new UiSize(1024, 600));
            UiShot.Settle(5);
            dlg.SetField("len", "4"); UiShot.Settle(2);
            dlg.SetField("wid", "3"); UiShot.Settle(2);
            dlg.Link(catalog.Items["p1"]);
            UiShot.Settle(4);
            TextClipChecker.Find(dlg, "estimate-calc", Code(lang)).Should().BeEmpty($"калькулятор не обрезан ({Code(lang)})");
            dlg.SelectMaterial(EstimateMaterial.Wallpaper);
            UiShot.Settle(4);
            dlg.SetField("len", "4"); UiShot.Settle(2);
            dlg.SetField("wid", "3"); UiShot.Settle(2);
            UiShot.Settle(3);
            TextClipChecker.Find(dlg, "estimate-calc-wallpaper", Code(lang)).Should().BeEmpty($"калькулятор обоев не обрезан ({Code(lang)})");
            Save(dlg, $"estimate-calc-{Code(lang)}.png");
        }
        finally
        {
            dlg.Close();
            UiShot.Pump();
        }
    }

    // ───────────────────────── печать ─────────────────────────

    [AvaloniaFact]
    public async Task Print_PreviewHasA4Pages_RibbonTextHasNoPurchase()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, catalog, _, spy) = Deps(seeCost: true);
        var doc = Filled(deps.Store, catalog);
        var window = new EstimatePrintWindow(deps, doc);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            await window.RenderAsync();
            UiShot.Settle(6);
            window.PageCount.Should().BeGreaterThan(0, "предпросмотр A4 построен бланком «Клиентов»");
            (await window.PrintRibbonAsync()).Should().BeTrue();
            var ribbon = spy.Ribbon.Should().ContainSingle().Subject;
            ribbon.Should().Contain("СМЕТА").And.Contain("Краска").And.Contain(EstimateRules.Code(doc.Number));
            ribbon.Should().NotContain("2 500").And.NotContain("Маржа");
            TextClipChecker.Find(window, "estimate-print", "ru").Should().BeEmpty("окно печати не обрезано");
            Save(window, "estimate-print-ru.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────────────────── Маркетплейс ─────────────────────────

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Marketplace_HasFreeEstimateCard_On5Languages(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var commands = new List<string>();
        var item = new LocalMarketplaceCatalogSource(commands.Add).GetItems().Single(i => i.Id == "estimate");
        item.Price.Should().BeNull("модуль бесплатный (цена — EstimateFeature.PriceSom = 0)");
        item.Title.Should().NotBeNullOrWhiteSpace();
        item.ShortDescription.Should().NotBeNullOrWhiteSpace();
        item.LongDescription.Should().NotBeNullOrWhiteSpace();
        item.Features.Should().NotBeEmpty();
        item.Primary.Should().NotBeNull();
        item.Primary!.Run();
        commands.Should().ContainSingle().Which.Should().Be("open:estimate");
    }
}
