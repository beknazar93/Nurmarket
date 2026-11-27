using System.Text;
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
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (замечание владельца: «Где описание и гифки его работы!? Создай для ВСЕХ функций!»): у каждой функции Маркетплейса без готовых GIF — демонстрация работы,
/// нарисованная кодом (MarketplaceDemos.*): 3–5 шагов с подписью на 5 языках, кадр помещается в рамку 640×340, три пункта «Что получите». Список модулей — TariffBuilderFixture.Live
/// (29 модулей сервера) + «Выход мяса» (carcass_yield) + 7 пакетов «Стандарта». НОВЫЙ МОДУЛЬ СЕРВЕРА БЕЗ ДЕМОНСТРАЦИИ — КРАСНЫЙ ТЕСТ.
/// Таблица «что есть у каждой карточки» для отчёта: %TEMP%\nurmarket-tests\marketplace-inventory.md. Снимки: %TEMP%\nurmarket-tests\ui-actual\marketplace-demo-*.png.</summary>
[Collection("UiScenarios")]
public sealed class MarketplaceDemosTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "demos-" + Guid.NewGuid().ToString("N")[..8]);
    private IDisposable? _fixture;

    public MarketplaceDemosTests() => TestHost.Ensure();

    public void Dispose()
    {
        _fixture?.Dispose();
        ModalShell.WorkingAreaForTests = null;
        TariffDraft.ResetForTests();
    }

    private static readonly string[] Packs = ["pack:ai", "pack:clients", "pack:salesanalytics", "pack:restock", "pack:salary", "pack:debts", "pack:service"];

    /// <summary>Карточки со своей страницей и своими «Чем поможет» (Смета, Прокат, Ремонт, Trade-in): им нужна демонстрация, а три пункта «Что получите» уже есть на странице.</summary>
    private static bool OwnCard(string key) => Array.IndexOf(ModuleIds.WithOwnCard, key) >= 0;

    /// <summary>Все коды, которым положена демонстрация: 29 модулей сервера, «Выход мяса» (сервер его пока не отдаёт) и пакеты.</summary>
    private static IEnumerable<string> AllKeys() => TariffBuilderFixture.LiveIds.Append(ModuleIds.CarcassYield).Append(ModuleIds.RepairStatusSite).Concat(MarketplaceFeatureCards.Ids).Concat(Packs); // 2026-11-26: + функции без модуля сервера (у «Телеграм-бота владельца» свои снимки и GIF в Assets)

    public static IEnumerable<object[]> Languages() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek }.Select(l => new object[] { l });

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.English => "en", AppLanguage.Turkish => "tr", _ => "ru" };

    // ───────────── данные ─────────────

    [AvaloniaFact]
    public async Task Every_server_module_and_pack_has_a_demo_in_the_registry()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        foreach (var key in AllKeys())
            MarketplaceDemos.Has(key).Should().BeTrue($"у «{key}» нет демонстрации работы: добавьте запись в MarketplaceDemos.* (новый модуль сервера без демонстрации — красный тест)");
        MarketplaceDemos.Keys.Should().OnlyContain(k => AllKeys().Contains(k, StringComparer.OrdinalIgnoreCase), "в реестре нет лишних кодов, которых нет ни в модулях сервера, ни среди пакетов");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Demos_have_3_to_5_steps_captions_and_three_gains_in_every_language(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        foreach (var key in AllKeys())
        {
            var steps = MarketplaceDemos.StepsOf(key);
            steps.Count.Should().BeInRange(3, 5, $"{key} {lang}: 3–5 шагов-кадров");
            foreach (var step in steps)
            {
                step.Caption.Should().NotBeNullOrWhiteSpace($"{key} {lang}");
                step.Caption.Length.Should().BeInRange(15, 130, $"{key} {lang}: подпись шага «{step.Caption}» — короткая фраза");
                step.Scene.Title.Should().NotBeNullOrWhiteSpace($"{key} {lang}");
                step.Scene.Parts.Should().NotBeEmpty($"{key} {lang}: в кадре есть содержимое");
                foreach (var text in TextsOf(step))
                    text.Should().NotContain("TODO").And.NotContain("  ", $"{key} {lang}: «{text}»");
            }
            steps.Select(s => s.Caption).Should().OnlyHaveUniqueItems($"{key} {lang}: подписи шагов различаются");

            if (key.StartsWith("pack:", StringComparison.Ordinal))
            {
                // 2026-11-26: у пакета «Стандарта» — подробное «Что делает» с примером чисел и «Для кого» (в списке пакетов была одна фраза).
                var what = MarketplaceDemos.WhatOf(key);
                what.Length.Should().BeInRange(90, 520, $"{key} {lang}: «Что делает» — 2–3 предложения");
                what.Any(char.IsDigit).Should().BeTrue($"{key} {lang}: в «Что делает» есть пример с числами");
                MarketplaceDemos.AudienceOf(key).Count.Should().BeGreaterThanOrEqualTo(2, $"{key} {lang}: «Для кого»");
            }

            var gains = MarketplaceDemos.GainsOf(key);
            if (OwnCard(key))
                continue; // у Сметы, Проката, Ремонта и Trade-in «Чем поможет» своё, на странице
            gains.Count.Should().Be(3, $"{key} {lang}: «Что получите» — ровно 3 пункта");
            foreach (var gain in gains)
            {
                MarketplaceIcons.Has(gain.IconKey).Should().BeTrue($"{key}: значок «{gain.IconKey}» есть в MarketplaceIcons");
                gain.Title.Length.Should().BeInRange(3, 40, $"{key} {lang}: заголовок «{gain.Title}»");
                gain.Text.Length.Should().BeInRange(20, 150, $"{key} {lang}: текст «{gain.Text}»");
            }
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Module_what_it_does_has_an_example_with_numbers_and_audience_and_steps(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var noNumbers = new List<string>();
        foreach (var id in TariffBuilderFixture.LiveIds.Append(ModuleIds.CarcassYield).Append(ModuleIds.RepairStatusSite).Concat(MarketplaceFeatureCards.Ids).Where(i => !OwnCard(i)))
        {
            var text = MarketplaceModuleContent.TextOf(id)!;
            if (!text.What.Any(char.IsDigit))
                noNumbers.Add(id);
            text.Steps.Length.Should().BeGreaterThanOrEqualTo(2, $"{id} {lang}: «Как включить» по шагам");
            text.Audience.Length.Should().BeGreaterThanOrEqualTo(1, $"{id} {lang}: «Кому подходит»");
        }
        noNumbers.Should().BeEmpty($"{lang}: в «Что делает» должен быть пример с числами");
    }

    [AvaloniaFact]
    public async Task Demo_texts_are_translated_not_copied_between_languages()
    {
        await TestHost.WarmAsync();
        var ru = new Dictionary<string, string>();
        foreach (var key in AllKeys())
        {
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
            ru[key] = string.Join("|", MarketplaceDemos.StepsOf(key).Select(s => s.Caption));
        }
        foreach (var lang in new[] { AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            foreach (var key in AllKeys())
                string.Join("|", MarketplaceDemos.StepsOf(key).Select(s => s.Caption)).Should().NotBe(ru[key], $"{key}: подписи шагов на {lang} — перевод, а не русский текст");
        }
        // 2026-11-26: пять языков — пять разных наборов подписей по всем функциям.
        var byLang = new Dictionary<AppLanguage, string>();
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            byLang[lang] = string.Join("|", AllKeys().SelectMany(k => MarketplaceDemos.StepsOf(k).Select(s => s.Caption)));
        }
        byLang.Values.Should().OnlyHaveUniqueItems();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Every_demo_frame_fits_into_the_stage_without_cutting_text(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var tooTall = new List<string>();
        var window = new Window { Width = 700, Height = 500 };
        var host = new StackPanel();
        window.Content = new ScrollViewer { Content = host };
        window.Show();
        try
        {
            foreach (var key in AllKeys())
            {
                var steps = MarketplaceDemos.StepsOf(key);
                for (var i = 0; i < steps.Count; i++)
                {
                    var body = MarketplaceMockScene.BuildBody(steps[i].Scene);
                    host.Children.Add(body);
                    body.Measure(new Size(MarketplaceMockScene.BodyWidth, double.PositiveInfinity));
                    if (body.DesiredSize.Height > MarketplaceMockScene.BodyHeight + 0.5)
                        tooTall.Add($"{key} шаг {i + 1}: {body.DesiredSize.Height:0} px при {MarketplaceMockScene.BodyHeight:0}");
                    host.Children.Remove(body);
                }
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
        tooTall.Should().BeEmpty($"кадр демонстрации не вылезает за рамку 640×340 ({lang}): сократите тексты или уберите часть строк");
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Demo_players_are_laid_out_without_clipped_text_for_every_function(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        foreach (var width in new[] { 760.0, 420.0 })
        {
            var host = new StackPanel { Width = width, Margin = new Thickness(16) };
            var window = new Window { Width = width + 32, Content = new ScrollViewer { Content = host } };
            window.Show();
            try
            {
                foreach (var key in AllKeys())
                {
                    var player = new MarketplaceMockDemoPlayer(MarketplaceDemos.StepsOf(key), "#2563EB");
                    host.Children.Add(player);
                    UiShot.Settle(2);
                    for (var i = 0; i < player.StepCount; i++)
                    {
                        player.Show(i);
                        UiShot.Settle(1);
                        TextClipChecker.Find(window, key + "-step" + (i + 1), Code(lang)).Select(v => v.Detail).Should().BeEmpty($"{key} шаг {i + 1} {lang} {width}: текст демонстрации не обрезан");
                    }
                    host.Children.Remove(player);
                }
            }
            finally
            {
                window.Close();
                UiShot.Pump();
            }
        }
    }

    [AvaloniaFact]
    public async Task Player_steps_through_pauses_and_stops_its_timer_when_the_page_goes_away()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var before = MarketplaceMockDemoPlayer.RunningTimers;
        var player = new MarketplaceMockDemoPlayer(MarketplaceDemos.StepsOf("part_numbers"), "#DC2626");
        var window = new Window { Width = 800, Height = 600, Content = player };
        window.Show();
        try
        {
            UiShot.Settle(2);
            MarketplaceMockDemoPlayer.RunningTimers.Should().Be(before + 1, "пока баннер на экране — один таймер");
            player.Index.Should().Be(0);
            player.Show(player.StepCount); // за последним — снова первый
            player.Index.Should().Be(0);
            player.Show(-1);
            player.Index.Should().Be(player.StepCount - 1);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
        MarketplaceMockDemoPlayer.RunningTimers.Should().Be(before, "ушли со страницы — таймер остановлен");
    }

    // ───────────── страница функции в окне ─────────────

    public static IEnumerable<object[]> PageCases()
    {
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
        {
            yield return new object[] { 1024, 600, lang };
            yield return new object[] { 1366, 768, lang };
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageCases))]
    public async Task Every_module_page_shows_the_demo_instead_of_an_empty_banner_and_does_not_clip_or_overlap(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new MarketplaceWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(width, height))) { }
            var store = window.GetVisualDescendants().OfType<MarketplaceStoreView>().First();
            foreach (var id in TariffBuilderFixture.LiveIds.Append(ModuleIds.CarcassYield).Append(ModuleIds.RepairStatusSite).Concat(MarketplaceFeatureCards.Ids))
            {
                store.OpenItem(OwnCard(id) ? id : "module:" + id);
                UiShot.Settle(4);
                var player = window.GetVisualDescendants().OfType<MarketplaceMockDemoPlayer>().SingleOrDefault();
                player.Should().NotBeNull($"{id} {lang} {width}: на странице функции демонстрация вместо пустого баннера");
                player!.StepCount.Should().BeInRange(3, 5, id);
                player.Bounds.Height.Should().BeGreaterThan(200, $"{id}: баннер с демонстрацией не пустой");
                foreach (var step in new[] { 0, player.StepCount - 1 })
                {
                    player.Show(step);
                    UiShot.Settle(2);
                    TextClipChecker.Find(window, $"page-{id}-{step}", Code(lang)).Select(v => v.Key + " — " + v.Detail).Should().BeEmpty($"{id} шаг {step + 1} {lang} {width}×{height}: текст не обрезан");
                    FormFieldsOverlapUiTests.FindViolations(window, $"page-{id}-{step}").Select(v => v.Key + " — " + v.Detail).Should().BeEmpty($"{id} {lang} {width}×{height}: поля и подписи не налезают");
                }
                // «Что делает», «Как включить», «Для кого» и «Чем поможет» — на странице.
                var all = UiDrive.AllText(window);
                var text = MarketplaceModuleContent.TextOf(id);
                if (text is not null)
                    all.Should().Contain(text.What[..40], $"{id} {lang}: «Что делает» на странице");
                foreach (var gain in MarketplaceDemos.GainsOf(id))
                    all.Should().Contain(gain.Title, $"{id} {lang}: «Что получите» — «{gain.Title}»");
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── «Статус ремонта на витрине»: привязан к «Ремонту», включается признаком repair_status_public ─────────────

    [AvaloniaFact]
    public async Task Repair_status_on_storefront_follows_the_server_flag_and_the_repair_module()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        try
        {
            ServerCapabilities.ResetMemoryForTests();
            ServerCapabilities.SetFeaturesForTests("showcase_chat"); // ответ сервера получен, признака repair_status_public в нём нет
            var soon = new LocalMarketplaceCatalogSource(_ => { }).GetItems().Single(i => i.Id == "module:" + ModuleIds.RepairStatusSite);
            soon.Title.Should().Be("Статус ремонта на витрине");
            soon.IsComingSoon.Should().BeTrue("«Ремонт» включён, а признак repair_status_public сервер не прислал");
            soon.Primary.Should().BeNull("включать нечего — «Скоро»");
            soon.Secondary.Should().BeEmpty();
            soon.Note.Should().Contain("Скоро: ждём сервер NurCRM");
            soon.IconKey.Should().Be("cellphone-message");
            soon.Line.Should().Be(MarketplaceModuleContent.LineOf(ModuleIds.RepairStatusSite));
            soon.MockDemo.Should().HaveCount(5, "демонстрация из пяти шагов: приём, квитанция, клиент вводит код, «Готово» и Telegram, «Готов, к доплате»");
            soon.Benefits.Should().HaveCount(3);
            soon.RelatedIds.Should().Contain("repair");
            TariffDraft.TargetOf(soon.Id).Should().BeNull("отдельного модуля на сервере нет — в заявку тарифа его класть нельзя");

            // Сервер прислал признак (он приходит, когда у компании включён «Ремонт») — карточка «Включено», без цены и переключателя.
            ServerCapabilities.SetFeaturesForTests("showcase_chat", RepairStatusPublic.Capability);
            var live = new LocalMarketplaceCatalogSource(_ => { }).GetItems().Single(i => i.Id == "module:" + ModuleIds.RepairStatusSite);
            live.IsComingSoon.Should().BeFalse("сервер прислал признак repair_status_public");
            live.Status.Should().Be(MarketplaceStatus.Installed);
            live.Primary.Should().BeNull("привязано к «Ремонту» — отдельного переключателя нет");
            live.Secondary.Should().BeEmpty();
            live.Price.Should().BeNull("отдельной платы нет");
            live.Note.Should().Contain("Привязано к модулю «Ремонт»");
            live.MockDemo.Should().HaveCount(5);

            // «Ремонт» выключен — карточка предлагает включить его.
            using (var doc = System.Text.Json.JsonDocument.Parse(TariffBuilderFixture.CompanyJson(["condition"])))
                CompanyModules.SetForTests(CompanyModulesSnapshot.Parse(doc.RootElement), seenProfile: "grocery");
            ServerCapabilities.SetFeaturesForTests("showcase_chat");
            var needs = new LocalMarketplaceCatalogSource(_ => { }).GetItems().Single(i => i.Id == "module:" + ModuleIds.RepairStatusSite);
            needs.IsComingSoon.Should().BeFalse();
            needs.Status.Should().Be(MarketplaceStatus.Available);
            needs.Primary.Should().NotBeNull("«Включить Ремонт»");
            needs.Note.Should().Contain("Включите модуль «Ремонт»");
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
        }
    }

    // ───────────── снимки и таблица для отчёта ─────────────

    [AvaloniaTheory]
    [InlineData(UiTheme.Light)]
    [InlineData(UiTheme.Dark)]
    public async Task Demo_pages_are_saved_for_eyes(UiTheme theme)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(theme, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        ModalShell.WorkingAreaForTests = new Size(1366, 768);
        var window = new MarketplaceWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(1366, 768))) { }
            var store = window.GetVisualDescendants().OfType<MarketplaceStoreView>().First();
            var t = theme == UiTheme.Dark ? "dark" : "light";
            foreach (var (id, step) in new[] { ("module:part_numbers", 1), ("module:carcass_yield", 1), ("module:haggle", 1), ("repair", 2), ("module:repair_status_site", 2), ("theme:forest", 0) })
            {
                store.OpenItem(id);
                UiShot.Settle(4);
                window.GetVisualDescendants().OfType<MarketplaceMockDemoPlayer>().FirstOrDefault()?.Show(step);
                UiShot.Settle(2);
                using (var shot = UiShot.Render(window))
                    MarketplaceModuleLinesTests.Save(shot, $"marketplace-demo-{id.Replace(':', '-')}-{t}");
                // второй снимок — страница прокручена к демонстрации (подпись шага, точки, пауза и «Что получите» видны)
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().OrderByDescending(v => v.Extent.Height).First();
                scroller.Offset = new Vector(0, 380);
                UiShot.Settle(3);
                using var scrolled = UiShot.Render(window);
                MarketplaceModuleLinesTests.Save(scrolled, $"marketplace-demo-{id.Replace(':', '-')}-{t}-scrolled");
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Inventory_of_all_marketplace_cards_is_written_for_the_report_and_has_no_empty_banners()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var prefs = UserPreferences.Instance;
        var plan = prefs.LastKnownPlanName;
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        prefs.LastKnownPlanName = "Старт"; // на «Старте» в каталоге есть и пакеты «Стандарта»
        var rows = new StringBuilder();
        var emptyNow = new List<string>();
        int total = 0, specs = 0, gifBefore = 0, demoNow = 0, slides = 0, emptyBefore = 0;
        try
        {
            var items = new LocalMarketplaceCatalogSource(_ => { }).GetItems();
            foreach (var item in items.OrderBy(i => i.Group).ThenBy(i => i.Id))
            {
                total++;
                var hasSpec = item.LongDescription.Length >= 80 && item.HowTo.Count >= 2 && item.Audience.Count >= 1;
                var gif = item.Demo != null && MarketplaceMedia.ResolveDemo(item) != null;
                var slideCount = item.Slides.Count(s => MarketplaceMedia.ResolveSlide(item, s) != null);
                var mock = item.MockDemo.Count;
                var hero = item.HeroVisual != null; // темы и виды кассы: большой предпросмотр вместо обложки
                var bannerEmptyBefore = slideCount == 0 && !hero;
                var bannerEmptyNow = slideCount == 0 && !hero && mock == 0;
                if (hasSpec) specs++;
                if (gif) gifBefore++;
                if (gif || mock > 0) demoNow++;
                if (slideCount > 0) slides++;
                if (bannerEmptyBefore) emptyBefore++;
                if (bannerEmptyNow && !item.IsComingSoon) emptyNow.Add(item.Id);
                rows.AppendLine($"| `{item.Id}` | {item.Title.Replace("|", "/")} | {item.Group} | {(hasSpec ? "да" : "нет")} | {item.Benefits.Count} | {(gif ? "GIF" : "нет")} | {(mock > 0 ? mock + " шаг." : "нет")} | {slideCount} | {(bannerEmptyBefore ? "было пусто" : "нет")} | {(bannerEmptyNow ? (item.IsComingSoon ? "«Скоро»" : "ПУСТО") : "нет")} |");
            }
        }
        finally
        {
            prefs.LastKnownPlanName = plan;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"Всего карточек: {total}; полное описание (Что делает + Как включить + Для кого): {specs}; GIF-демо: {gifBefore}; демо (GIF или код): {demoNow}; со слайдами-картинками: {slides}; баннер без картинки было: {emptyBefore}; стало: {emptyNow.Count}.");
        sb.AppendLine();
        sb.AppendLine("| id | название | группа | описание | «Что получите» (шт.) | GIF | демо кодом | слайды | пустой баннер (было) | пустой баннер (стало) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        sb.Append(rows);
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "marketplace-inventory.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        emptyNow.Should().BeEmpty("у каждой карточки есть картинка, предпросмотр или демонстрация — пустого баннера нет (таблица: " + path + ")");
    }

    private static IEnumerable<string> TextsOf(MockStep step)
    {
        yield return step.Caption;
        yield return step.Scene.Title;
        foreach (var part in step.Scene.Parts)
            switch (part)
            {
                case MockInput p: yield return p.Label; yield return p.Value; break;
                case MockRow p: yield return p.Text; if (p.Right.Length > 0) yield return p.Right; if (p.Sub.Length > 0) yield return p.Sub; break;
                case MockButton p: yield return p.Text; break;
                case MockNote p: yield return p.Text; break;
                case MockToggle p: yield return p.Label; break;
                case MockTotal p: yield return p.Label; yield return p.Value; break;
                case MockChips p: foreach (var s in p.Items) yield return s; break;
                case MockTiles p: foreach (var s in p.Items) yield return s; break;
            }
    }
}
