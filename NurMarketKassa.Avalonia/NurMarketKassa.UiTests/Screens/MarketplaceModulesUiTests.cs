using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (ответ Умара 20.11, В.16 / В.23; решение «цены только серверные»): Маркетплейс показывает модули с сервера — «Входит в вашу сферу», цена «N сом/мес» из public/modules,
/// «Включить» у выключенных и «Выключить» у включённых (в том числе Смета и Прокат), общие карточки «Модуль» для остальных модулей. Прежние цены 1200 / 1000 разово и «Бесплатно» убраны.
/// Окно — 1024×600 и 1366×768, ru / ky / uz: текст не обрезан, поля и подписи не налезают. Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\marketplace-modules-*.png.</summary>
[Collection("UiScenarios")]
public sealed class MarketplaceModulesUiTests : IDisposable
{
    private const string Json = """
        {"profile":"electronics","profile_base":"grocery","version":"5",
         "modules":["serials","warranty","repair","trade_in","rental"],
         "items":[{"id":"serials","enabled":true,"source":"default"},{"id":"warranty","enabled":true,"source":"default"},
                  {"id":"repair","enabled":true,"source":"default"},{"id":"trade_in","enabled":true,"source":"default"},
                  {"id":"rental","enabled":true,"source":"added"},{"id":"estimate","enabled":false,"source":"off"},{"id":"variants","enabled":false,"source":"off"},{"id":"haggle","enabled":false,"source":"off"}],
         "flags":["electronics_2026_11"],
         "catalog":[{"id":"serials","name":"Серийные номера","names":{"ru":"Серийные номера","en":"Serial numbers"},"group":"electronics","default_in":["electronics"]},
                    {"id":"warranty","name":"Гарантия","names":{"ru":"Гарантия","en":"Warranty"},"group":"electronics","default_in":["electronics","autoparts"]},
                    {"id":"repair","name":"Ремонт","names":{"ru":"Ремонт","en":"Repair"},"group":"electronics","default_in":["electronics"]},
                    {"id":"trade_in","name":"Trade-in","names":{"ru":"Trade-in","en":"Trade-in"},"group":"electronics","default_in":["electronics"]},
                    {"id":"rental","name":"Прокат","names":{"ru":"Прокат","en":"Rental"},"group":"stroymag","default_in":["stroymag"]},
                    {"id":"estimate","name":"Смета","names":{"ru":"Смета","en":"Estimate"},"group":"stroymag","default_in":["stroymag"]},
                    {"id":"variants","name":"Размеры и цвета","names":{"ru":"Размеры и цвета","en":"Sizes and colours"},"group":"clothing","default_in":["clothing"]},
                    {"id":"haggle","name":"Торг","names":{"ru":"Торг","en":"Haggling"},"group":"bazaar","default_in":["bazaar_retail","bazaar_wholesale"]}],
         "profiles":[{"id":"electronics","name":"Электроника","names":{"ru":"Электроника"},"base":"grocery","modules":["serials","warranty","repair","trade_in"],"flags":[]},
                     {"id":"stroymag","name":"Строймаг","names":{"ru":"Строймаг"},"base":"grocery","modules":["estimate","rental"],"flags":[]}]}
        """;

    private const string Prices = """
        {"currency":"KGS","modules":[{"id":"serials","name":{"ru":"Серийные номера"},"group":"electronics","monthly":"200.00","once":"0.00","default_in":["electronics"]},
                                      {"id":"estimate","name":{"ru":"Смета"},"group":"stroymag","monthly":"300.00","once":"0.00","default_in":["stroymag"]},
                                      {"id":"rental","name":{"ru":"Прокат"},"group":"stroymag","monthly":"300.00","once":"0.00","default_in":["stroymag"]},
                                      {"id":"repair","name":{"ru":"Ремонт"},"group":"electronics","monthly":"300.00","once":"0.00","default_in":["electronics"]},
                                      {"id":"variants","name":{"ru":"Размеры и цвета"},"group":"clothing","monthly":"200.00","once":"0.00","default_in":["clothing"]},
                                      {"id":"haggle","name":{"ru":"Торг"},"group":"bazaar","monthly":"100.00","once":"0.00","default_in":["bazaar_retail"]}]}
        """;

    private readonly string? _account = UserPreferences.Instance.LastDataAccountKey;
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly bool _estimate = UserPreferences.Instance.EstimateEnabled;
    private readonly bool _rental = UserPreferences.Instance.RentalEnabled;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "modules-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public MarketplaceModulesUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        CompanyModules.DirOverride = _dir;
        UserPreferences.Instance.LastDataAccountKey = "c:ui-modules";
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = "electronics";
        UserPreferences.Instance.EstimateEnabled = false;
        UserPreferences.Instance.RentalEnabled = false;
        CompanyModules.ResetForTests();
        using var doc = JsonDocument.Parse(Json);
        CompanyModules.SetForTests(CompanyModulesSnapshot.Parse(doc.RootElement), seenProfile: "electronics");
        using var prices = JsonDocument.Parse(Prices);
        PublicModules.SetForTests(PublicModules.Parse(prices.RootElement, out _));
    }

    public void Dispose()
    {
        CompanyModules.ResetForTests();
        CompanyModules.DirOverride = null;
        PublicModules.SetForTests(null);
        var p = UserPreferences.Instance;
        p.LastDataAccountKey = _account;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.EstimateEnabled = _estimate;
        p.RentalEnabled = _rental;
        ModalShell.WorkingAreaForTests = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    // ───────────── карточки ─────────────

    [AvaloniaFact]
    public async Task Cards_show_server_prices_included_and_turn_off_buttons_without_old_prices()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var commands = new List<string>();
        var items = new LocalMarketplaceCatalogSource(commands.Add).GetItems();
        items.Select(i => i.Id).Should().OnlyHaveUniqueItems();

        var repair = items.Single(i => i.Id == "repair");
        repair.Status.Should().Be(MarketplaceStatus.Installed);
        repair.StatusText.Should().Be("Входит в вашу сферу", "ремонт — модуль профиля «Электроника», без ключа");
        repair.Price.Should().BeNull("входит в сферу — без доплаты");
        repair.Secondary.Should().ContainSingle(a => a.Label == "Выключить");
        repair.Secondary[0].Run();
        commands.Should().Equal("module-off:repair");
        repair.Primary!.Label.Should().Be("Открыть ремонт");

        var rental = items.Single(i => i.Id == "rental");
        rental.Status.Should().Be(MarketplaceStatus.Installed, "владелец добавил модуль «Прокат»");
        rental.StatusText.Should().Be("Включено");
        rental.Secondary.Should().ContainSingle(a => a.Label == "Выключить", "у уже включённого Проката есть «Выключить»");

        var estimate = items.Single(i => i.Id == "estimate");
        estimate.Status.Should().Be(MarketplaceStatus.Available);
        estimate.Price!.Label.Should().Be("300 сом/мес", "цена с сервера (public/modules), не «Бесплатно»");
        estimate.TileStatusLine.Should().Be("300 сом/мес");
        estimate.Primary!.Label.Should().Be("Включить");
        commands.Clear();
        estimate.Primary.Run();
        commands.Should().Equal("module-on:estimate");

        foreach (var card in new[] { repair, rental, estimate, items.Single(i => i.Id == "trade_in") })
        {
            (card.Price?.Amount is 1200m or 1000m).Should().BeFalse($"прежние разовые цены убраны ({card.Id})");
            card.TileStatusLine.Should().NotContain("Бесплатно", $"({card.Id})");
            card.PriceTerms.Should().NotContain("через поддержку").And.NotContain("Бесплатно");
        }

        // Модули без своей карточки — общая карточка «Модуль».
        var variants = items.Single(i => i.Id == "module:variants");
        variants.Title.Should().Be("Размеры и цвета");
        variants.Price!.Label.Should().Be("200 сом/мес");
        variants.Primary!.Label.Should().Be("Включить");
        var serials = items.Single(i => i.Id == "module:serials");
        serials.StatusText.Should().Be("Входит в вашу сферу");
        serials.Secondary.Should().ContainSingle(a => a.Label == "Выключить");
        items.Any(i => i.Id == "module:estimate" || i.Id == "module:repair").Should().BeFalse("у этих четырёх своя карточка");
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Turkish)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Module_cards_have_all_texts_in_every_language(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var items = new LocalMarketplaceCatalogSource(_ => { }).GetItems();
        foreach (var id in new[] { "repair", "trade_in", "estimate", "rental", "module:variants", "module:serials", "module:haggle" })
        {
            var card = items.Single(i => i.Id == id);
            card.Title.Should().NotBeNullOrWhiteSpace($"{id} {lang}");
            card.ShortDescription.Should().NotBeNullOrWhiteSpace($"{id} {lang}");
            card.TileStatusLine.Should().NotBeNullOrWhiteSpace($"{id} {lang}");
            card.PriceTerms.Should().NotBeNullOrWhiteSpace($"{id} {lang}");
            (card.Primary?.Label ?? card.Secondary.FirstOrDefault()?.Label).Should().NotBeNullOrWhiteSpace($"{id} {lang}: есть действие");
        }
    }

    [AvaloniaFact]
    public async Task Without_server_answer_cards_work_the_old_way_without_invented_prices()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        CompanyModules.SetForTests(null);
        PublicModules.SetForTests(null);
        UserPreferences.Instance.MarketSphereDetail = "hozmag";
        var commands = new List<string>();
        var items = new LocalMarketplaceCatalogSource(commands.Add).GetItems();

        items.Any(i => i.Id.StartsWith("module:", StringComparison.Ordinal)).Should().BeFalse("общие карточки — только при ответе сервера");
        var estimate = items.Single(i => i.Id == "estimate");
        estimate.Price.Should().BeNull("цену не придумываем: сервер её не отдал");
        estimate.TileStatusLine.Should().Be("Не включено");
        estimate.Primary!.Label.Should().Be("Включить");
        estimate.Primary.Run();
        commands.Should().ContainSingle().Which.Should().Be("open:estimate", "по-старому: «открыть» включает признак и открывает окно");
        items.Single(i => i.Id == "repair").Price.Should().NotBeNull("платная функция без ключа и без модуля — «Платно», а не «Бесплатно»");
    }

    /// <summary>2026-11-21 (ответ Умара 21.11): модуль с trial_days > 0 — «Попробовать 7 дн.» рядом с «Включить» (команда module-trial); на пробе — «Проба до …»; Прокат вне Строймага — цена сервера, не «Бесплатно».</summary>
    [AvaloniaFact]
    public async Task Trial_button_and_trial_status_and_rental_price_outside_stroymag()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        using (var prices = JsonDocument.Parse(Prices.Replace("\"monthly\":\"300.00\"", "\"trial_days\":7,\"monthly\":\"300.00\"")))
            PublicModules.SetForTests(PublicModules.Parse(prices.RootElement, out _));
        // Прокат у «Электроники» не включён (профиль не Строймаг) — цена и проба с сервера
        using (var doc = JsonDocument.Parse(Json.Replace("{\"id\":\"rental\",\"enabled\":true,\"source\":\"added\"}", "{\"id\":\"rental\",\"enabled\":false,\"source\":\"off\"}").Replace(",\"rental\"]", "]")))
            CompanyModules.SetForTests(CompanyModulesSnapshot.Parse(doc.RootElement), seenProfile: "electronics");
        var commands = new List<string>();
        var items = new LocalMarketplaceCatalogSource(commands.Add).GetItems();

        var rental = items.Single(i => i.Id == "rental");
        rental.Price!.Label.Should().Be("300 сом/мес", "цена только с сервера");
        rental.TileStatusLine.Should().NotContain("Бесплатно").And.NotContain("по умолчанию");
        rental.Primary!.Label.Should().Be("Включить");
        rental.Secondary.Should().ContainSingle(a => a.Label.StartsWith("Попробовать 7"));
        rental.Secondary[0].Run();
        commands.Should().Equal("module-trial:rental");

        // на пробе
        using (var doc = JsonDocument.Parse(Json.Replace("{\"id\":\"rental\",\"enabled\":true,\"source\":\"added\"}", "{\"id\":\"rental\",\"enabled\":true,\"source\":\"trial\",\"trial_until\":\"2026-11-28T12:00:00+00:00\"}")))
            CompanyModules.SetForTests(CompanyModulesSnapshot.Parse(doc.RootElement), seenProfile: "electronics");
        var onTrial = new LocalMarketplaceCatalogSource(_ => { }).GetItems().Single(i => i.Id == "rental");
        onTrial.StatusText.Should().StartWith("Проба до 28.11.2026");
        onTrial.Secondary.Should().ContainSingle(a => a.Label == "Выключить");
    }

    // ───────────── окно ─────────────

    public static IEnumerable<object[]> Cases()
    {
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
        {
            yield return new object[] { 1024, 600, lang };
            yield return new object[] { 1366, 768, lang };
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Marketplace_window_and_module_page_do_not_overlap_or_clip(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new MarketplaceWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(width, height))) { }
            Check(window, $"marketplace-modules-store-{Code(lang)}-{width}x{height}", width, height, lang);

            var store = window.GetVisualDescendants().OfType<MarketplaceStoreView>().First();
            foreach (var id in new[] { "estimate", "module:variants" })
            {
                store.OpenItem(id);
                UiShot.Settle();
                Check(window, $"marketplace-modules-{id.Replace(':', '-')}-{Code(lang)}-{width}x{height}", width, height, lang);
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── Настройки → Операции: порядок и названия сфер ─────────────

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Settings_sphere_buttons_follow_choice_window_order_and_names(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var view = new OperationsSettingsView();
        view.LoadBankQrSettings();
        var window = new Window { Content = view };
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 640))) { }
            var wanted = SphereCatalog.ChoiceList().Select(p => p.Title.Get()).ToList();
            var radios = view.FindControl<WrapPanel>("SphereRadioPanel")!.Children.OfType<RadioButton>().Select(r => r.Content?.ToString() ?? "").ToList();

            radios.Should().HaveCount(wanted.Count);
            for (var i = 0; i < wanted.Count; i++)
                radios[i].Should().EndWith(wanted[i], $"кнопка {i + 1}: порядок и название — как на плитках окна выбора сферы ({lang})");
            if (lang == AppLanguage.Russian)
                radios.Should().Contain(r => r.EndsWith("Магазин одежды", StringComparison.Ordinal)).And.NotContain(r => r.Contains("Одежда и похожие"));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static void Check(Window window, string name, int width, int height, AppLanguage lang)
    {
        using (var shot = UiShot.Render(window))
        using (var image = SKImage.FromBitmap(shot))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 90))
        {
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
        }
        FormFieldsOverlapUiTests.FindViolations(window, name).Select(v => v.Key + " — " + v.Detail).ToList().Should().BeEmpty($"поля и подписи не налезают ({name})");
        TextClipChecker.Find(window, name, Code(lang)).Select(v => v.Key + " — " + v.Detail).ToList().Should().BeEmpty($"текст не обрезан ({name})");
        window.Bounds.Width.Should().BeLessThanOrEqualTo(width + 0.5);
        window.Bounds.Height.Should().BeLessThanOrEqualTo(height + 0.5);
    }
}
