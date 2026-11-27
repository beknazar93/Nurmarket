using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (просьба владельца: «Телеграм-бот, бот-продавец, ЭСФ, ККМ онлайн в Маркетплейс — с описанием»; «все новые функции — с описанием и как работает»):
/// карточки функций, которых сервер как модули пока не отдаёт (<see cref="MarketplaceFeatureCards.Ids"/>): у каждой значок, цвет, строка «что делает», страница на 5 языках и демонстрация;
/// состояние («Скоро» / «Доступно» / «Входит в программу» / «Цена уточняется») берётся из признаков сервера, цены на карточке НЕТ, пока её не прислал сервер (рекомендованные цены — только в ТЗ Умару);
/// пока сервер модуля не знает, «+» для тарифа «Своё» нет, а когда заведёт — карточка становится обычной серверной (цена с сервера, «+»). Вёрстка страниц — в MarketplaceDemosTests (те же коды).</summary>
[Collection("UiScenarios")]
public sealed class MarketplaceFeatureCardsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "feature-cards-" + Guid.NewGuid().ToString("N")[..8]);
    private IDisposable? _fixture;

    public MarketplaceFeatureCardsTests() => TestHost.Ensure();

    public void Dispose()
    {
        _fixture?.Dispose();
        ServerCapabilities.ResetMemoryForTests();
        ModalShell.WorkingAreaForTests = null;
        PublicModules.SetForTests(null);
        TariffDraft.ResetForTests();
    }

    public static IEnumerable<object[]> Languages() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek }.Select(l => new object[] { l });

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.English => "en", AppLanguage.Turkish => "tr", _ => "ru" };

    private static MarketplaceItem Card(string id) => new LocalMarketplaceCatalogSource(_ => { }).GetItems().Single(i => i.Id == "module:" + id);

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Every_feature_card_has_icon_color_line_page_text_and_demo(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        MarketplaceFeatureCards.Ids.Should().HaveCount(12).And.OnlyHaveUniqueItems(); // 2026-11-27: + «Конструктор акций» (promotions)
        MarketplaceFeatureCards.Ids.Should().NotIntersectWith(TariffBuilderFixture.LiveIds, "это функции, которых на сервере как модулей нет; когда сервер заведёт код — переехать в TariffBuilderFixture.Live");
        foreach (var id in MarketplaceFeatureCards.Ids)
        {
            MarketplaceModuleContent.HasLine(id).Should().BeTrue($"{id}: строка «что делает»");
            var line = MarketplaceModuleContent.LineOf(id)!;
            line.Length.Should().BeInRange(25, 140, $"{id} {lang}");
            line.Should().NotEndWith("...").And.NotContain("TODO").And.NotContain("  ", $"{id} {lang}");
            var icon = MarketplaceModuleContent.IconKeyOf(id);
            MarketplaceIcons.Has(icon).Should().BeTrue($"{id}: значок «{icon}»");
            icon.Should().NotBe(MarketplaceIcons.FallbackKey).And.NotBe("puzzle-outline").And.NotBe(MarketplaceModuleContent.FallbackIcon, $"{id}: свой значок, не запасной");
            Color.TryParse(MarketplaceModuleContent.ColorOf(id), out _).Should().BeTrue(id);
            var text = MarketplaceModuleContent.TextOf(id)!;
            text.Short.Should().NotBeNullOrWhiteSpace(id);
            text.What.Length.Should().BeGreaterThan(80, $"{id} {lang}");
            text.What.Any(char.IsDigit).Should().BeTrue($"{id} {lang}: пример с числами");
            text.Steps.Length.Should().BeGreaterThanOrEqualTo(2, id);
            text.Audience.Length.Should().BeGreaterThanOrEqualTo(1, id);
            MarketplaceDemos.Has(id).Should().BeTrue($"{id}: демонстрация «как работает»");
            MarketplaceDemos.StepsOf(id).Count.Should().BeInRange(3, 5, id);
            MarketplaceDemos.GainsOf(id).Count.Should().Be(3, id);
        }
        MarketplaceFeatureCards.Ids.Select(MarketplaceModuleContent.IconKeyOf).Should().OnlyHaveUniqueItems("у каждой функции свой значок");
        MarketplaceFeatureCards.Ids.Select(MarketplaceModuleContent.ColorOf).Should().OnlyHaveUniqueItems("у каждой функции свой цвет");
    }

    [AvaloniaFact]
    public async Task Texts_of_feature_cards_are_translated_not_copied()
    {
        await TestHost.WarmAsync();
        var byLang = new Dictionary<AppLanguage, string>();
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            byLang[lang] = string.Join("|", MarketplaceFeatureCards.Ids.Select(id => MarketplaceModuleContent.LineOf(id) + string.Join("", MarketplaceModuleContent.TextOf(id)!.Steps)
                + string.Join("", MarketplaceDemos.StepsOf(id).Select(s => s.Caption))));
        }
        byLang.Values.Should().OnlyHaveUniqueItems("все пять языков — разные тексты");
    }

    [AvaloniaFact]
    public async Task Cards_follow_server_flags_show_no_invented_price_and_have_no_plus_until_the_server_knows_the_module()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        ServerCapabilities.ResetMemoryForTests();
        ServerCapabilities.SetFeaturesForTests("showcase_chat"); // ответ сервера есть, признаков бота нет

        // Признака нет — «Скоро»: включать нечего, цены нет.
        foreach (var id in new[] { MarketplaceFeatureCards.BotSeller, MarketplaceFeatureCards.BotBargain })
        {
            var soon = Card(id);
            soon.IsComingSoon.Should().BeTrue($"{id}: сервер не прислал признак");
            soon.Primary.Should().BeNull(id);
            soon.Price.Should().BeNull(id);
            soon.Note.Should().Contain("Скоро: ждём сервер NurCRM");
            TariffDraft.TargetOf(soon.Id).Should().BeNull($"{id}: модуля на сервере нет — в заявку «Своё» класть нельзя");
        }

        // Признаки пришли — «Доступно», цены всё равно нет: «Цена уточняется».
        ServerCapabilities.SetFeaturesForTests("showcase_chat", ServerCapabilities.BotSeller, ServerCapabilities.BotBargain);
        foreach (var id in new[] { MarketplaceFeatureCards.BotSeller, MarketplaceFeatureCards.BotBargain })
        {
            var live = Card(id);
            live.IsComingSoon.Should().BeFalse(id);
            live.Status.Should().Be(MarketplaceStatus.Installed, id);
            live.StatusText.Should().Be("Доступно");
            live.Price.Should().BeNull($"{id}: цену не придумываем");
            live.PriceTerms.Should().Contain("Цена уточняется");
            live.Primary.Should().NotBeNull(id);
            live.IsModule.Should().BeTrue();
        }

        // Постоянные функции кассы: «Входит в программу», без цены и без «+».
        foreach (var id in new[] { MarketplaceFeatureCards.Esf, MarketplaceFeatureCards.ClientMailing, MarketplaceFeatureCards.OneCExport, MarketplaceFeatureCards.ShopQr,
                     MarketplaceFeatureCards.PaymentQr, MarketplaceFeatureCards.ReceiptLink, MarketplaceFeatureCards.StockScan })
        {
            var c = Card(id);
            c.Status.Should().Be(MarketplaceStatus.Installed, id);
            c.StatusText.Should().Be("Входит в программу", id);
            c.Price.Should().BeNull(id);
            c.Primary.Should().NotBeNull(id);
            TariffDraft.TargetOf(c.Id).Should().BeNull(id);
        }

        // Заявка в поддержку и онлайн-ККМ без включённого режима.
        var fill = Card(MarketplaceFeatureCards.FillBase);
        fill.Status.Should().Be(MarketplaceStatus.Available);
        fill.StatusText.Should().Be("Цену называет поддержка");
        fill.Price.Should().BeNull();
        var kkm = Card(MarketplaceFeatureCards.Ekassa);
        kkm.Status.Should().Be(MarketplaceStatus.Available);
        kkm.StatusText.Should().Be("Цена уточняется");
        kkm.Primary!.Label.Should().Be("Как подключить");
        kkm.Price.Should().BeNull();

        // Ни у одной такой карточки нет цены (рекомендованные цены — только в ТЗ Умару).
        MarketplaceFeatureCards.Ids.Select(Card).Should().OnlyContain(c => c.Price == null);

        // Кнопка «Где найти» запускает «open:<код>».
        var commands = new List<string>();
        var items = new LocalMarketplaceCatalogSource(commands.Add).GetItems();
        items.Single(i => i.Id == "module:" + MarketplaceFeatureCards.Esf).Primary!.Run();
        commands.Should().ContainSingle().Which.Should().Be("open:" + MarketplaceFeatureCards.Esf);
        MarketplaceFeatureCards.OpenInfo(MarketplaceFeatureCards.Esf).Body.Should().StartWith("1. ").And.Contain("2. ");
    }

    [AvaloniaFact]
    public async Task When_the_server_adds_the_module_the_card_becomes_a_regular_server_one_with_price_and_plus()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        const string id = MarketplaceFeatureCards.BotSeller;
        // Подмена ответов сервера: модуль bot_seller есть в реестре компании и в public/modules с серверной ценой (число — только для проверки, не рекомендация).
        using (var doc = System.Text.Json.JsonDocument.Parse("{\"profile\":\"grocery\",\"version\":\"1\",\"modules\":[],\"items\":[{\"id\":\"bot_seller\",\"enabled\":false,\"source\":\"off\"}],"
                                                              + "\"catalog\":[{\"id\":\"bot_seller\",\"name\":\"Бот-продавец\",\"names\":{\"ru\":\"Бот-продавец\"},\"group\":\"bot\",\"default_in\":[]}],\"profiles\":[]}"))
            CompanyModules.SetForTests(CompanyModulesSnapshot.Parse(doc.RootElement), seenProfile: "grocery");
        PublicModules.SetForTests([new PublicModule(id, "Бот-продавец", "bot", 777m, 0m, [], 7)]);
        var items = new LocalMarketplaceCatalogSource(_ => { }).GetItems();
        items.Count(i => i.Id == "module:" + id).Should().Be(1, "одна карточка, без двойника");
        var card = items.Single(i => i.Id == "module:" + id);
        card.Price!.Label.Should().Contain("777", "цена — серверная");
        card.Primary!.Label.Should().Be("Включить");
        card.Line.Should().Be(MarketplaceModuleContent.LineOf(id));
        card.MockDemo.Should().NotBeEmpty("демонстрация и описание остались");
        TariffDraft.TargetOf(card.Id).Should().NotBeNull("сервер знает модуль — «+» для «Своё» появился");
    }

    /// <summary>2026-11-27: «Конструктор акций» (модуль promotions, признак promotions_v2): свой значок (не пазл и не значок старой карточки «Акции видно сразу»), своё описание
    /// без шаблона «из набора модулей», демонстрация с примером «вода 100 × 3 → 200»; без признака — «Скоро», с признаком — «Доступно» и «Цена уточняется» (в public/modules цены нет).</summary>
    [AvaloniaFact]
    public async Task Promotions_card_has_own_icon_and_text_and_follows_promotions_v2_flag()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        const string id = MarketplaceFeatureCards.Promotions;
        var icon = MarketplaceModuleContent.IconKeyOf(id);
        icon.Should().Be("ticket-percent-outline");
        icon.Should().NotBe("puzzle-outline").And.NotBe(MarketplaceModuleContent.FallbackIcon).And.NotBe("tag-multiple-outline", "не путать со старой карточкой «Акции видно сразу»");
        MarketplaceIcons.Has(icon).Should().BeTrue();

        ServerCapabilities.ResetMemoryForTests();
        ServerCapabilities.SetFeaturesForTests("showcase_chat");
        var soon = Card(id);
        soon.IsComingSoon.Should().BeTrue("без promotions_v2 — «Скоро»");
        soon.Primary.Should().BeNull();
        TariffDraft.TargetOf(soon.Id).Should().BeNull("модуля promotions в public/modules пока нет — в «Своё» не кладём");

        ServerCapabilities.SetFeaturesForTests("showcase_chat", ServerCapabilities.PromotionsV2);
        var card = Card(id);
        card.Title.Should().Be("Конструктор акций");
        card.IsComingSoon.Should().BeFalse();
        card.StatusText.Should().Be("Доступно");
        card.Price.Should().BeNull("цену не придумываем");
        card.PriceTerms.Should().Contain("Цена уточняется");
        card.IconKey.Should().Be(icon);
        card.LongDescription.Should().Contain("третья бутылка бесплатно").And.NotContain("из набора модулей");
        card.ShortDescription.Should().NotContain("Модуль магазина");
        card.Line.Should().Contain("третий бесплатно");
        card.HowTo.Should().HaveCountGreaterThanOrEqualTo(2).And.Contain(s => s.Contains("«Акции»"));
        card.Audience.Should().Contain(a => a.Contains("минимаркет"));
        card.MockDemo.Should().HaveCount(4);
        card.MockDemo.SelectMany(s => s.Scene.Parts).OfType<MockNote>().Select(n => n.Text).Should().Contain("Добавьте ещё 1 — будет бесплатно");
        card.MockDemo.SelectMany(s => s.Scene.Parts).OfType<MockTotal>().Select(t => t.Value).Should().Contain("200");
        MarketplaceFeatureCards.OpenInfo(id).Body.Should().StartWith("1. ").And.Contain("«Акции»");
    }

    /// <summary>2026-11-27: страница «Конструктора акций» на 1024×600 (ky/uz — самые длинные подписи): все 4 кадра демонстрации без обрезаний и наложений; снимки — в ui-actual.</summary>
    [AvaloniaTheory]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Promotions_page_fits_1024x600_without_clipping(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        ServerCapabilities.ResetMemoryForTests();
        ServerCapabilities.SetFeaturesForTests("showcase_chat", ServerCapabilities.PromotionsV2);
        ModalShell.WorkingAreaForTests = new Size(1024, 600);
        var window = new MarketplaceWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            var store = window.GetVisualDescendants().OfType<MarketplaceStoreView>().First();
            store.OpenItem("module:" + MarketplaceFeatureCards.Promotions);
            UiShot.Settle(4);
            var player = window.GetVisualDescendants().OfType<MarketplaceMockDemoPlayer>().Single();
            player.StepCount.Should().Be(4);
            for (var step = 0; step < player.StepCount; step++)
            {
                player.Show(step);
                UiShot.Settle(2);
                TextClipChecker.Find(window, $"promotions-{step}", Code(lang)).Select(v => v.Key + " — " + v.Detail).Should().BeEmpty($"шаг {step + 1} {lang}: текст не обрезан");
                FormFieldsOverlapUiTests.FindViolations(window, $"promotions-{step}").Select(v => v.Key + " — " + v.Detail).Should().BeEmpty($"шаг {step + 1} {lang}: подписи не налезают");
                using var shot = UiShot.Render(window);
                MarketplaceModuleLinesTests.Save(shot, $"marketplace-promotions-{Code(lang)}-1024x600-step{step + 1}");
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian, 1366, 768)]
    [InlineData(AppLanguage.Kyrgyz, 1366, 768)]
    [InlineData(AppLanguage.Uzbek, 1366, 768)]
    [InlineData(AppLanguage.Russian, 1024, 600)]
    [InlineData(AppLanguage.Kyrgyz, 1024, 600)]
    [InlineData(AppLanguage.Uzbek, 1024, 600)]
    public async Task Feature_tiles_show_title_and_line_without_clipping_or_overlap(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        ServerCapabilities.ResetMemoryForTests();
        ServerCapabilities.SetFeaturesForTests("showcase_chat", ServerCapabilities.BotSeller, ServerCapabilities.BotBargain, ServerCapabilities.PromotionsV2); // у «Скоро» строки под названием нет; 2026-11-27: + конструктор акций
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new MarketplaceWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(width, height))) { }
            var store = window.GetVisualDescendants().OfType<MarketplaceStoreView>().First();
            store.SetFilter(MarketplaceFilter.Features);
            UiShot.Settle(6);
            var all = UiDrive.AllText(window);
            foreach (var id in MarketplaceFeatureCards.Ids)
            {
                all.Should().Contain(MarketplaceModuleContent.LineOf(id)!, $"плитка «{id}» показывает строку «что делает» ({lang} {width}×{height})");
                all.Should().Contain(Card(id).Title, $"плитка «{id}» показывает название");
            }
            TextClipChecker.Find(window, "feature-tiles", Code(lang)).Select(v => v.Key + " — " + v.Detail).Should().BeEmpty($"{lang} {width}×{height}: текст плиток не обрезан");
            FormFieldsOverlapUiTests.FindViolations(window, "feature-tiles").Select(v => v.Key + " — " + v.Detail).Should().BeEmpty($"{lang} {width}×{height}: подписи не налезают");
            using (var shot = UiShot.Render(window))
                MarketplaceModuleLinesTests.Save(shot, $"marketplace-feature-tiles-{Code(lang)}-{width}x{height}");
            // снимки для глаз: плитка (прокрутка к функции) и страница «Торг бота» с демонстрацией
            store.OpenItem("module:" + MarketplaceFeatureCards.BotBargain);
            UiShot.Settle(4);
            using var page = UiShot.Render(window);
            MarketplaceModuleLinesTests.Save(page, $"marketplace-feature-page-bot_bargain-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
