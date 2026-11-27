using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (стресс-тест новых окон дня, docs/Отчеты/Стресс-тест QR, бота, торга, ремонта 26.11.2026.md): окна бота-продавца, торга и «Ремонт» с БОЛЬШИМИ данными
/// (30 диалогов с длинными текстами и эмодзи на странице, 30 товаров с длинными названиями, 1 000 квитанций ремонта) на 800×600, 1024×600, 1366×768, 1920×1080 и «масштабе» 125/150 %
/// (логический размер 1024×600 / 1366×768, делённый на масштаб), светлая и тёмная тема, ru/ky/uz/en/tr. Проверки — те же, что у остальных окон (FormFieldsOverlapUiTests, TextClipChecker),
/// плюс высота кнопок (цель нажатия) и Esc. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\stress1126-*.png.</summary>
[Collection("UiScenarios")]
public sealed class StressNewWindowsUiTests : IDisposable
{
    private static readonly string ShotDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "stress1126-" + Guid.NewGuid().ToString("N")[..8]);

    public StressNewWindowsUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static string Code(AppLanguage l) => l switch
    {
        AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.English => "en", AppLanguage.Turkish => "tr", _ => "ru",
    };

    private const string LongName = "Куртка зимняя мужская пуховая с капюшоном и мехом, размер 52–54, цвет тёмно-синий 🧥 (поставка ноябрь 2026, партия №A-123456789)";
    private const string LongText = "Здравствуйте! 😀 Хочу заказать доставку в Ош, но сначала уточните, пожалуйста, есть ли размер 52 и можно ли оплатить при получении картой Элкарт или через QR? 🙏🙏🙏 "
        + "Ещёоченьдлинноесловобезпробеловкотороенедолжноломатьразметкуокнаникогданикогда";

    /// <summary>Заглушка с большими страницами: 30 диалогов и 30 товаров на странице, «есть следующая».</summary>
    private sealed class BigApi : IBotSellerApi, IBotBargainApi
    {
        private readonly BotSellerUiTests.FakeApi _base = new();

        public Task<BotSellerConfig?> GetSellerConfigAsync(CancellationToken ct = default) => _base.GetSellerConfigAsync(ct);
        public Task<BotSellerConfig?> PatchSellerConfigAsync(IReadOnlyDictionary<string, object?> part, CancellationToken ct = default) => _base.PatchSellerConfigAsync(part, ct);

        public Task<BotDialogsPage> GetDialogsAsync(string? status, DateTime from, DateTime to, int page, CancellationToken ct = default) =>
            Task.FromResult(new BotDialogsPage(5000, true, Enumerable.Range(1, 30).Select(i => new BotDialog(
                (page * 1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), i % 3 == 0 ? "" : "Айбек Токтогулов-Сыдыков " + i + " 👨‍🔧", "very_long_username_" + i, 999, 12,
                new DateTimeOffset(2026, 11, 26, 10, i % 60, 0, TimeSpan.FromHours(6)), LongText, (i % 4) switch { 0 => "needs_human", 1 => "human", 2 => "callback", _ => "bot" },
                i % 2 == 0 ? new BotHandoff("angry", "waiting", LongText, "+996 555 123 456", "", "", null, null) : null)).ToList()));

        public Task<BotDialogDetail?> GetDialogAsync(string chatId, CancellationToken ct = default) => _base.GetDialogAsync(chatId, ct);
        public Task<BotDialogAction> DialogActionAsync(string chatId, string action, CancellationToken ct = default) => _base.DialogActionAsync(chatId, action, ct);
        public Task<BotDialogReply> ReplyToDialogAsync(string chatId, string text, CancellationToken ct = default) => _base.ReplyToDialogAsync(chatId, text, ct);
        public Task<BotSalesStats> GetSalesStatsAsync(DateTime from, DateTime to, CancellationToken ct = default) => _base.GetSalesStatsAsync(from, to, ct);
        public Task<BotTestAnswer> TestAsCustomerAsync(string question, CancellationToken ct = default) => _base.TestAsCustomerAsync(question, ct);

        /// <summary>Страницы дальше этой сервер отдаёт 404 (как живой сервер NBS за концом списка).</summary>
        public int MaxPage { get; init; } = int.MaxValue;
        public List<int> PagesAsked { get; } = new();

        public Task<BotBargainProductsPage> GetBargainProductsAsync(string? search, string? categoryId, bool onlyRules, int page, CancellationToken ct = default)
        {
            PagesAsked.Add(page);
            if (page > MaxPage)
                throw new ApiException("Неверная страница.", 404, System.Text.Json.JsonDocument.Parse("{\"detail\":\"Неверная страница.\",\"code\":\"not_found\"}").RootElement.Clone());
            return Task.FromResult(new BotBargainProductsPage(15000, true, Enumerable.Range(1, 30).Select(i => new BotBargainProduct(
                "p" + i, LongName + " " + i, 1_000_000.99m + i, i % 2 == 0 ? null : 750_000.01m, "c1", i % 3 == 0 ? null : i % 3 == 1, i % 2 == 0 ? 900_000.55m : null, i % 2 == 0 ? 12.5m : null, i % 2 == 0, 900_000.55m)).ToList()));
        }

        public Task<BotBargainProduct?> SaveBargainProductAsync(string productId, BotBargainRule rule, CancellationToken ct = default) => _base.SaveBargainProductAsync(productId, rule, ct);
        public Task ResetBargainProductAsync(string productId, CancellationToken ct = default) => _base.ResetBargainProductAsync(productId, ct);
        public Task<BotBargainTest> TestBargainAsync(string productId, decimal? offer, bool reset, CancellationToken ct = default) => _base.TestBargainAsync(productId, offer, reset, ct);

        public Task<BotBargainOffersPage> GetBargainOffersAsync(DateTime from, DateTime to, string? decision, int page, CancellationToken ct = default) =>
            Task.FromResult(new BotBargainOffersPage(5000, true, Enumerable.Range(1, 30).Select(i => new BotBargainOffer(
                new DateTimeOffset(2026, 11, 26, 16, i % 60, 0, TimeSpan.FromHours(6)), "test:" + i, i % 2 == 0, LongName, 1_000_000.99m, 899_999.01m, i % 2 == 0 ? "accept" : "counter", 950_000.5m, 5, 900_000.55m, 12.5m, i % 3 == 0 ? "A-" + i : null)).ToList()));

        public Task<IReadOnlyList<BotCategory>> GetCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BotCategory>>(Enumerable.Range(1, 200).Select(i => new BotCategory("c" + i, "Категория очень длинного названия " + i)).ToList());
    }

    private RepairOrderStore BigRepairStore()
    {
        var store = new RepairOrderStore(Path.Combine(_dir, "repair-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 26, 10, 0, 0));
        var rows = Enumerable.Range(1, 1000).Select(i => RepairServerRow.Parse(System.Text.Json.JsonDocument.Parse(
            $"{{\"id\":\"s{i}\",\"external_id\":\"k{i}\",\"code\":\"Р-{i:0000}\",\"status\":\"{(i % 5 == 0 ? "ready" : "in_work")}\",\"device\":{System.Text.Json.JsonSerializer.Serialize(LongName)},\"customer_name\":\"Клиент с очень длинным именем и фамилией {i} 😀\",\"customer_phone\":\"+996 555 {i:000000}\",\"estimate\":\"1000000.99\",\"final_price\":\"1250000.55\",\"prepaid\":\"250000.25\",\"problem\":{System.Text.Json.JsonSerializer.Serialize(LongText)},\"updated_at\":\"2026-11-26T10:00:00Z\"}}").RootElement)!).ToList();
        store.MergeServerRows(rows, "2026-11-26T10:00:00Z");
        return store;
    }

    private static RepairOrdersDeps RepairDeps(RepairOrderStore store, Func<string, string, Task<bool>>? confirm = null) => new()
    {
        Store = store, Till = null, Print = _ => Task.FromResult<string?>(null), OpenUrl = _ => true,
        Confirm = confirm ?? ((_, _) => Task.FromResult(false)), Cashier = () => "Тест", Today = () => new DateTime(2026, 11, 26),
        StatusUrl = (_, _) => Task.FromResult<string?>(null), Pull = _ => Task.FromResult(0),
    };

    private Window Create(string name, BigApi api, RepairOrderStore? repairs) => name switch
    {
        "bot-settings" => new BotSellerSettingsWindow(api),
        "bot-check" => new BotCheckWindow(api),
        "bot-dialogs" => new BotDialogsWindow(api),
        "bot-stats" => new BotSalesStatsWindow(api),
        "bargain-products" => new BotBargainProductsWindow(api),
        "bargain-offers" => new BotBargainOffersWindow(api),
        "bargain-test" => new BotBargainTestWindow(api, "p1", LongName, 1_000_000.99m),
        "repair" => new RepairOrdersWindow(RepairDeps(repairs!)),
        _ => throw new InvalidOperationException(name),
    };

    private static void Save(Window window, string name)
    {
        Directory.CreateDirectory(ShotDir);
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), data.ToArray());
    }

    /// <summary>Кнопки ниже 32 px по высоте (цель нажатия на сенсорной кассе) — видимые, не служебные (стрелки прокрутки, выпадающих списков).</summary>
    private static IEnumerable<string> SmallTargets(Window w, string scope) =>
        w.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Bounds.Height > 0 && b.Bounds.Height < 32 && b.TemplatedParent is null && b.Content is string)
            .Select(b => $"{scope}: кнопка «{b.Content}» высотой {b.Bounds.Height:0} px");

    public static IEnumerable<object[]> Windows() =>
        new[] { "bot-settings", "bot-check", "bot-dialogs", "bot-stats", "bargain-products", "bargain-offers", "bargain-test", "repair" }.Select(n => new object[] { n });

    [AvaloniaTheory]
    [MemberData(nameof(Windows))]
    public void BigData_AllSizesThemesLanguages_NoOverlapNoClipping(string name)
    {
        var sizes = new[]
        {
            new UiSize(800, 600), new UiSize(1024, 600), new UiSize(1366, 768), new UiSize(1920, 1080),
            new UiSize(819, 480), // 1024×600 при масштабе 125 %
            new UiSize(910, 512), // 1366×768 при масштабе 150 %
        };
        var runs = sizes.Select(s => (Theme: UiTheme.Light, Lang: AppLanguage.Russian, Size: s))
            .Concat(new[] { AppLanguage.Kyrgyz, AppLanguage.Uzbek, AppLanguage.English, AppLanguage.Turkish }.Select(l => (Theme: UiTheme.Dark, Lang: l, Size: new UiSize(1024, 600))))
            .Concat(new[] { (Theme: UiTheme.Dark, Lang: AppLanguage.Russian, Size: new UiSize(800, 600)) });
        var found = new List<string>();
        var small = new List<string>();
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.BotSeller, ServerCapabilities.BotHandoff, ServerCapabilities.BotSalesStats, ServerCapabilities.BotBargain);
        var repairs = name == "repair" ? BigRepairStore() : null;
        try
        {
            foreach (var (theme, lang, size) in runs)
            {
                UiShot.Setup(theme, lang);
                var window = Create(name, new BigApi(), repairs);
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using (UiShot.Render(window, size)) { }
                    UiShot.Settle(6);
                    if (window is RepairOrdersWindow rw)
                    {
                        rw.Select("k5"); // готовая квитанция с деньгами и длинными текстами
                        UiShot.Settle(4);
                    }
                    if (name == "bot-dialogs")
                    {
                        UiDrive.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Content is string s && (s == "Ответить" || s == "Жооп берүү" || s == "Javob berish" || s == "Reply" || s == "Yanıtla")));
                        UiShot.Settle(4);
                    }
                    if (name == "bargain-products")
                    {
                        UiDrive.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Content is string s && (s == "Изменить" || s == "Өзгөртүү" || s == "O'zgartirish" || s == "Edit" || s == "Düzenle")));
                        UiShot.Settle(4);
                    }
                    var renderMs = sw.ElapsedMilliseconds;
                    var scope = $"{name}|{Code(lang)}|{theme}|{size.Width}x{size.Height}";
                    var problems = FormFieldsOverlapUiTests.FindViolations(window, scope).Select(v => v.Detail).ToList();
                    problems.AddRange(TextClipChecker.Find(window, scope, Code(lang)).Select(v => v.Detail));
                    found.AddRange(problems);
                    small.AddRange(SmallTargets(window, scope));
                    if (renderMs > 8000)
                        found.Add($"{scope}: окно открывалось {renderMs} мс");
                    if (lang == AppLanguage.Russian || size.Width == 1024)
                        Save(window, $"stress1126-{name}-{Code(lang)}-{theme.ToString().ToLowerInvariant()}-{size.Width}x{size.Height}");
                }
                finally
                {
                    window.Close();
                    UiShot.Pump();
                }
            }
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
        }
        File.WriteAllLines(Path.Combine(ShotDir, $"stress1126-{name}-targets.txt"), small.Distinct());
        found.Should().BeEmpty("поля и подписи не налезают, текст не обрезан (" + name + "):\n" + string.Join("\n", found.Distinct().Take(25)));
    }

    [AvaloniaFact]
    public void Repair_ThousandReceipts_ListIsCapped_SearchFindsTheRest_TimerStopsOnClose()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var store = BigRepairStore();
        var window = new RepairOrdersWindow(RepairDeps(store));
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            UiShot.Settle(4);
            var openMs = sw.ElapsedMilliseconds;
            window.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("Row_", StringComparison.Ordinal) == true).Should().Be(RepairOrdersWindow.MaxRows);
            UiDrive.AllText(window).Should().Contain("Показаны последние " + RepairOrdersWindow.MaxRows + " из 1000");

            sw.Restart();
            UiDrive.Type(window, window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "RepairSearch"), "Р-0007");
            UiShot.Settle(4);
            var searchMs = sw.ElapsedMilliseconds;
            window.GetVisualDescendants().OfType<Button>().Where(b => b.Name?.StartsWith("Row_", StringComparison.Ordinal) == true).Select(b => b.Name).Should().Contain("Row_7", "старая квитанция за пределами списка находится поиском");

            openMs.Should().BeLessThan(10000, $"окно с 1 000 квитанций открылось за {openMs} мс");
            searchMs.Should().BeLessThan(5000, $"поиск по 1 000 квитанций: {searchMs} мс");
            File.WriteAllText(Path.Combine(ShotDir, "stress1126-repair-perf.txt"), $"open={openMs}ms search={searchMs}ms");

            var timer = typeof(RepairOrdersWindow).GetField("_syncTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window) as Avalonia.Threading.DispatcherTimer;
            timer.Should().NotBeNull("опрос сервера раз в 90 с запущен при открытии");
            window.Close();
            UiShot.Pump();
            timer!.IsEnabled.Should().BeFalse("после закрытия окна опрос остановлен");
        }
        finally
        {
            if (window.IsVisible)
                window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Repair_TakeServer_FromBanner_AsksConfirmation_NoMeansNoChange()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var store = new RepairOrderStore(Path.Combine(_dir, "repair-c.json"), () => new DateTime(2026, 11, 26, 10, 0, 0));
        var o = store.Create(new RepairOrder { ClientName = "Айгуль", ClientPhone = "0700123456", Device = "Samsung", Fault = "Экран", Estimate = 1500m }, "Тест", serverNumber: false);
        store.Update(o.Id, x => { x.Device = "Samsung A54 (моя правка)"; return true; }, "Тест", "edit");
        store.MarkConflict(o.Id, "{\"id\":\"b1\",\"status\":\"in_work\",\"device\":\"Samsung (сервер)\",\"updated_at\":\"2026-11-26T10:05:00Z\"}");
        var asked = 0;
        var answer = false;
        var window = new RepairOrdersWindow(RepairDeps(store, (_, _) => { asked++; return Task.FromResult(answer); }));
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            window.Select(o.Id);
            UiShot.Settle(4);
            UiDrive.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RepairTakeServer"));
            UiShot.Settle(4);
            asked.Should().Be(1, "«Взять с сервера» спрашивает подтверждение");
            store.Find(o.Id)!.Device.Should().Be("Samsung A54 (моя правка)", "«Нет» — правки кассира не тронуты");
            store.Find(o.Id)!.HasConflict.Should().BeTrue();

            answer = true;
            UiDrive.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RepairTakeServer"));
            UiShot.Settle(4);
            store.Find(o.Id)!.Device.Should().Be("Samsung (сервер)");
            store.Find(o.Id)!.HasConflict.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Products_PageBeyondEnd404_GoesBackToFirstPage_NotServerUnsupported()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new BigApi { MaxPage = 1 };
        var window = new BotBargainProductsWindow(api);
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            UiShot.Settle(4);
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Дальше ›"));
            UiShot.Settle(6);
            api.PagesAsked.Should().Equal(1, 2, 1);
            var text = UiDrive.AllText(window);
            text.Should().NotContain("Сервер пока не поддерживает", "404 за концом списка — не «сервер не умеет»");
            text.Should().Contain("Страница 1").And.Contain(LongName);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData("bargain-offers")]
    [InlineData("bargain-test")]
    [InlineData("bot-stats")]
    [InlineData("bot-check")]
    public void Esc_ClosesWindow(string name)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = Create(name, new BigApi(), null);
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            UiShot.Settle(3);
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
            UiShot.Pump();
            window.IsVisible.Should().BeFalse("Esc закрывает окно");
        }
        finally
        {
            if (window.IsVisible)
                window.Close();
            UiShot.Pump();
        }
    }
}
