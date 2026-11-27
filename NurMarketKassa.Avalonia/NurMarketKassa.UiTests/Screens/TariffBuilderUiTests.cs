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
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (требование владельца: «тариф «Своё» должен собираться из Маркетплейса»): набор «в тариф» ОДИН — TariffDraft. Плитки и страницы Маркетплейса, полоса «Ваш тариф» над витриной и диалог
/// «Тариф «Своё»» читают и пишут его: отметили на плитке — галочка в диалоге уже стоит, сняли в диалоге — плитка снова с «+». Цену считает заглушка вместо сервера (users/company/tariff/quote/ с packs[], modules[], remove[]).
/// В диалоге все 29 модулей сервера — строками «значок · название · что делает · цена · переключатель»; на 1024×600 и 1366×768 (ru, ky, uz) текст не обрезан, поля не налезают.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\tariff-custom-modules-*.png и marketplace-tariff-strip-*.png.</summary>
[Collection("UiScenarios")]
public sealed class TariffBuilderUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "tariff-builder-" + Guid.NewGuid().ToString("N")[..8]);
    private IDisposable? _fixture;

    public TariffBuilderUiTests() => TestHost.Ensure();

    public void Dispose()
    {
        _fixture?.Dispose();
        ModalShell.WorkingAreaForTests = null;
        TariffDraft.ResetForTests();
    }

    /// <summary>Заглушка сервера: тариф «Старт» 1300, пакет — 400 в месяц и 1500 разово, модуль не из профиля «grocery» — цена из public/modules; итог считает она, касса — нет.</summary>
    public sealed class BuilderTariffApi : ITariffApi
    {
        public readonly List<(List<string> Packs, List<string>? Modules, List<string> Remove)> Quotes = [];
        public readonly List<(List<string> Packs, List<string>? Modules, List<string> Remove)> Submits = [];
        public Exception? QuoteError;

        public Task<TariffCatalog> GetCatalogAsync(CancellationToken ct = default) => Task.FromResult(TariffBuilderFixture.Catalog(includedInStandard: false));

        public Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, CancellationToken ct = default) => QuoteAsync(packs, null, null, null, "month", ct);

        public Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, string? sphere, IReadOnlyList<string>? remove, string period, CancellationToken ct = default)
        {
            if (QuoteError is not null)
                throw QuoteError;
            Quotes.Add((packs.ToList(), modules?.ToList(), remove?.ToList() ?? []));
            var mods = modules ?? [];
            var included = mods.Where(m => TariffBuilderFixture.GroceryDefaults.Contains(m)).Select(m => new TariffQuoteModule(m, m, 0m)).ToList();
            var extra = mods.Where(m => !TariffBuilderFixture.GroceryDefaults.Contains(m)).Select(m => new TariffQuoteModule(m, m, TariffBuilderFixture.Live.Single(l => l.Id == m).Monthly)).ToList();
            var packsMonthly = packs.Count * 400m;
            var extraSum = extra.Sum(e => e.Monthly);
            var rows = packs.Select(p => new TariffQuotePack(p, 400m, 1500m, false)).ToList();
            return Task.FromResult(new TariffQuote(packsMonthly, packs.Count * 1500m, 1300m, rows, false, 3600m, "start", sphere, included, extra, extraSum, 1300m + packsMonthly + extraSum,
                "start", [], 1300m, period));
        }

        public Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, string idempotencyKey, CancellationToken ct = default) =>
            SubmitCustomAsync(packs, null, null, "month", idempotencyKey, ct);

        public Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, IReadOnlyList<string>? remove, string period, string idempotencyKey, CancellationToken ct = default)
        {
            Submits.Add((packs.ToList(), modules?.ToList(), remove?.ToList() ?? []));
            return Task.FromResult(new TariffOrder("o-777", "pending", 1700m, 1500m, null));
        }
    }

    private static string Norm(string text) => text.Replace((char)0x00A0, ' ').Replace((char)0x202F, ' ');

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    private async Task<TariffCustomWindow> OpenDialogAsync(BuilderTariffApi api, UiSize? size = null)
    {
        var window = new TariffCustomWindow(api, "Старт");
        using (UiShot.Render(window, size)) { }
        await window.LoadAsync();
        UiShot.Settle(8);
        return window;
    }

    private static Border Chip(Window window, string itemId) =>
        window.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Tag == itemId)
            .GetVisualDescendants().OfType<Border>().First(b => (string?)b.Tag == "tariff-toggle");

    /// <summary>2026-11-26: есть ли на экране кружки «+» / «✓» на плитках (они только в режиме сборки тарифа «Своё»).</summary>
    private static bool HasToggles(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Any(b => (string?)b.Tag == "tariff-toggle");

    private static void Search(Window window, string text)
    {
        var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
        box.Text = text;
        UiShot.Settle(4);
    }

    private (MarketplaceWindow Window, MarketplaceStoreView Store) OpenMarketplace(BuilderTariffApi api, int width, int height)
    {
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new MarketplaceWindow();
        using (UiShot.Render(window, new UiSize(width, height))) { }
        var store = window.GetVisualDescendants().OfType<MarketplaceStoreView>().First();
        store.TariffApiProvider = () => api;
        store.SetFilter(MarketplaceFilter.Features);
        UiShot.Settle(6);
        return (window, store);
    }

    // ───────────── диалог «Своё»: все модули сервера строками ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Dialog_lists_every_live_module_with_line_price_toggle_and_fits_the_screen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair"); // как на снимке владельца: сфера «продуктовый» + «Состояние» и «Ремонт» из других сфер
        TariffDraft.CanBuildOverride = () => true;
        var api = new BuilderTariffApi();
        var window = new TariffCustomWindow(api, "Старт");
        try
        {
            using (UiShot.Render(window, new UiSize(width, height))) { }
            await window.LoadAsync();
            UiShot.Settle(8);
            var tag = $"{width}x{height} {lang}";

            window.ModuleIdsShown.Should().BeEquivalentTo(TariffBuilderFixture.LiveIds, $"в диалоге все 29 модулей сервера ({tag})");
            window.GetVisualDescendants().OfType<CheckBox>().Count().Should().Be(11 + 29, "11 пакетов и 29 функций — один переключатель на строку");
            foreach (var id in TariffBuilderFixture.LiveIds)
            {
                var line = window.ModuleLineText(id);
                line.Should().NotBeNullOrWhiteSpace($"у модуля «{id}» в диалоге есть строка «что делает» ({tag})");
                line.Should().Be(MarketplaceModuleContent.LineOf(id), id);
            }

            // Состояния строк: профиль «Продуктовый» — «Входит в ваш тариф»; доп. модули — «Включено»; остальные — просто цена.
            foreach (var id in TariffBuilderFixture.GroceryDefaults)
            {
                window.IsModuleChecked(id).Should().BeTrue($"{id}: входит в сферу ({tag})");
                window.ModuleStatusText(id).Should().NotBeNullOrWhiteSpace(id);
            }
            window.IsModuleChecked("condition").Should().BeTrue();
            window.IsModuleChecked("repair").Should().BeTrue();
            window.IsModuleChecked("haggle").Should().BeFalse();
            window.ModuleStatusText("haggle").Should().BeEmpty("не включён и не выбран — только цена");

            var all = Norm(UiDrive.AllText(window));
            foreach (var id in new[] { "expiry_batches", "quick_items", "scales_labels", "condition", "repair" })
                all.Should().Contain(MarketplaceModuleContent.LineOf(id)!, $"модуль «{id}» из снимка владельца показан с описанием ({tag})");
            all.Should().Contain("100 ").And.Contain("300 ", "цены модулей — с сервера (public/modules)");
            all.Should().NotContain("Модули других сфер:", "названия функций больше не сплошным абзацем");

            // Текст не обрезан, кнопки внутри окна, горизонтальной прокрутки нет.
            var clipped = TextClipChecker.Find(window, "tariff-custom-modules", Code(lang));
            clipped.Should().BeEmpty($"текст не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            foreach (var btn in window.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button) && b.IsEffectivelyVisible && b.Bounds.Height > 0))
            {
                var p = btn.TranslatePoint(default, window)!.Value;
                (p.Y + btn.Bounds.Height).Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, $"кнопка «{UiDrive.TextOf(btn)}» внутри окна ({tag})");
                (p.X + btn.Bounds.Width).Should().BeLessThanOrEqualTo(window.Bounds.Width + 0.5, $"кнопка «{UiDrive.TextOf(btn)}» не выходит за ширину окна ({tag})");
            }
            foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>())
                (scroll.Extent.Width - scroll.Viewport.Width).Should().BeLessThanOrEqualTo(1.0, $"нет горизонтальной прокрутки ({tag})");

            // Строки не налезают друг на друга: у каждой строки списка (значок + название + описание) подпись не заходит на цену справа.
            var rows = window.GetVisualDescendants().OfType<CheckBox>().ToList();
            foreach (var box in rows)
            {
                var texts = box.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.Bounds.Height > 0).ToList();
                for (var i = 1; i < texts.Count; i++)
                {
                    var a = texts[i - 1].TranslatePoint(default, window)!.Value;
                    var b = texts[i].TranslatePoint(default, window)!.Value;
                    b.Y.Should().BeGreaterThanOrEqualTo(a.Y + texts[i - 1].Bounds.Height - 1.0, $"название и описание строки не налезают ({tag})");
                }
            }

            using (var shot = UiShot.Render(window))
                MarketplaceModuleLinesTests.Save(shot, $"tariff-custom-modules-{Code(lang)}-{width}x{height}");

            // Второй снимок — список функций (прокрутка до середины списка модулей): значок, название, «что делает», цена, состояние.
            var scrollHost = window.GetVisualDescendants().OfType<ScrollViewer>().OrderByDescending(s => s.Extent.Height).First();
            scrollHost.Offset = new Vector(0, Math.Max(0, scrollHost.Extent.Height - scrollHost.Viewport.Height) * 0.62);
            UiShot.Settle(4);
            using var shotList = UiShot.Render(window);
            MarketplaceModuleLinesTests.Save(shotList, $"tariff-custom-modules-list-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── одно состояние: Маркетплейс ↔ полоса ↔ диалог ─────────────

    [AvaloniaFact]
    public async Task Selection_is_shared_between_marketplace_tile_strip_and_dialog_both_directions()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        TariffDraft.CanBuildOverride = () => true;
        UserPreferences.Instance.LastKnownPlanName = "Старт"; // тариф компании (TariffGate.CurrentPlanName в проверках пуст) — фикстура вернёт прежнее
        var api = new BuilderTariffApi();
        var (market, store) = OpenMarketplace(api, 1366, 768);
        TariffCustomWindow? dialog = null;
        try
        {
            var strip = store.TariffStripForTest;
            strip.Should().NotBeNull("владельцу над витриной — полоса «Ваш тариф»");
            Norm(strip!.PlanText).Should().Be("Ваш тариф: Старт · 1 300 сом в месяц", "название и цена тарифа — из каталога сервера");
            strip.OpenButton.Content.Should().Be("Собрать тариф «Своё»");
            strip.ResetButton.IsVisible.Should().BeFalse();

            // 0) обычное время: кружков «+» нет; «Собрать тариф «Своё»» включает режим сборки — и «+» появляются
            HasToggles(market).Should().BeFalse("вне режима сборки плитки без «+»");
            TariffDraft.BuildMode.Should().BeFalse();
            UiDrive.Click(market, strip.OpenButton);
            UiShot.Settle(4);
            TariffDraft.BuildMode.Should().BeTrue("кнопка «Собрать тариф «Своё»» включила режим сборки");
            HasToggles(market).Should().BeTrue("в режиме сборки у плиток есть «+»");
            strip.OpenButton.Content.Should().Be("Оформить тариф «Своё»");
            strip.ResetButton.IsVisible.Should().BeTrue();
            strip.ResetButton.Content.Should().Be("Отмена");
            strip.ChangesLine.Should().StartWith("Режим сборки", "подсказка, пока ничего не отмечено");

            // 1) кружок «+» на плитке «Торг» (реальный клик) → набор → полоса
            Search(market, "Торг");
            var chip = Chip(market, "module:haggle");
            ToolTip.GetTip(chip).Should().Be(TariffDraft.AddLabel);
            UiDrive.Click(market, chip);
            UiShot.Settle(4);
            TariffDraft.AddedModules.Should().Equal("haggle");
            ToolTip.GetTip(Chip(market, "module:haggle")).Should().Be(TariffDraft.RemoveLabel, "плитка показывает «✓ — убрать из тарифа»");
            strip.ChangesLine.Should().Be("Что изменится: +Торг");
            strip.OpenButton.Content.Should().Be("Оформить тариф «Своё»");
            strip.ResetButton.IsVisible.Should().BeTrue();
            await strip.QuoteNowForTest();
            Norm(strip.TotalText).Should().Be("Итого в месяц: 1 800 сом", "1300 + «Торг» 100 + «Состояние» 100 + «Ремонт» 300 — итог от сервера");
            api.Quotes.Last().Modules.Should().Contain("haggle").And.Contain("repair").And.Contain("scales_labels");

            // 2) диалог открывается с уже отмеченным «Торг»
            dialog = await OpenDialogAsync(api, new UiSize(1366, 768));
            dialog.IsModuleChecked("haggle").Should().BeTrue("выбор с плитки уже в диалоге");
            dialog.ModuleStatusText("haggle").Should().Be("Будет добавлено");
            dialog.ChangesLineText.Should().Be("Что изменится: +Торг");

            // 3) снимаем в диалоге → плитка и полоса вернулись
            dialog.SetModuleChecked("haggle", false);
            TariffDraft.HasChanges.Should().BeFalse();
            ToolTip.GetTip(Chip(market, "module:haggle")).Should().Be(TariffDraft.AddLabel, "снято в диалоге — плитка снова с «+»");
            strip.ChangesLine.Should().StartWith("Режим сборки", "набор пуст, режим ещё включён — снова подсказка");
            strip.OpenButton.Content.Should().Be("Оформить тариф «Своё»");

            // 4) пакет отмечаем в диалоге → набор → полоса; «Сбросить» в полосе → снята галочка в диалоге
            dialog.SetChecked("ai", true);
            TariffDraft.AddedPacks.Should().Equal("ai");
            TariffDraft.IsPackSelected("ai").Should().BeTrue();
            strip.ChangesLine.Should().Be("Что изменится: +ИИ-помощник");
            UiDrive.Click(market, strip.ResetButton);
            TariffDraft.HasChanges.Should().BeFalse();
            dialog.IsPackChecked("ai").Should().BeFalse("«Отмена» в полосе снимает отметку в открытом диалоге");
            TariffDraft.BuildMode.Should().BeFalse("«Отмена» выходит из режима сборки");
            UiShot.Settle(4);
            HasToggles(market).Should().BeFalse("вышли из режима — «+» с плиток исчезли");
            strip.OpenButton.Content.Should().Be("Собрать тариф «Своё»", "прежний вид полосы");
            strip.ChangesLine.Should().BeEmpty();
            strip.ResetButton.IsVisible.Should().BeFalse();

            // 5) модуль из сферы: снова режим сборки; «✓» на плитке → «убрать из тарифа» → в диалоге «Будет снято»
            TariffDraft.EnterBuildMode();
            UiShot.Settle(4);
            Search(market, "Быстрые товары");
            UiDrive.Click(market, Chip(market, "module:quick_items"));
            TariffDraft.RemovedModules.Should().Equal("quick_items");
            dialog.IsModuleChecked("quick_items").Should().BeFalse();
            dialog.ModuleStatusText("quick_items").Should().Be("Будет снято");
            strip.ChangesLine.Should().Be("Что изменится: −Быстрые товары");
        }
        finally
        {
            dialog?.Close();
            market.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Dialog_quote_and_submit_carry_the_shared_selection_and_clear_it_afterwards()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        TariffDraft.CanBuildOverride = () => true;
        var api = new BuilderTariffApi();
        TariffDraft.SetModule("haggle", true, false);   // выбрано раньше, в Маркетплейсе
        TariffDraft.SetPack("ai", true, false);
        TariffDraft.SetModule("repair", false, true);   // «Ремонт» снимаем
        TariffDraft.EnterBuildMode();
        var window = new TariffCustomWindow(api, "Старт") { Confirm = ask => { Asked = ask; return Task.FromResult(true); } };
        try
        {
            using (UiShot.Render(window, new UiSize(1366, 768))) { }
            await window.LoadAsync();
            UiShot.Settle(8);
            window.IsPackChecked("ai").Should().BeTrue("пакет, отмеченный в Маркетплейсе, виден в диалоге");
            window.IsModuleChecked("haggle").Should().BeTrue();
            window.IsModuleChecked("repair").Should().BeFalse();
            window.ModuleStatusText("repair").Should().Be("Будет снято");

            await window.QuoteForTestAsync();
            UiShot.Settle(6);
            var q = api.Quotes.Last();
            q.Packs.Should().Equal("ai");
            q.Modules.Should().Contain("haggle").And.NotContain("repair", "в расчёте — включённые + добавленные − снятые");
            window.CanSubmit.Should().BeTrue();
            // 1300 + пакет 400 + «Состояние» 100 + «Торг» 100 (Ремонт снят) — цифра сервера
            Norm(window.SummaryText).Should().Contain("1 900", "итог — total_month сервера");

            await window.SubmitForTestAsync();
            Asked.Should().Contain("Добавить функции: Торг").And.Contain("Снять функции: Ремонт");
            api.Submits.Should().ContainSingle();
            api.Submits[0].Packs.Should().Equal("ai");
            api.Submits[0].Modules.Should().Contain("haggle").And.NotContain("repair");
            window.DoneText.Should().Contain("o-777", "номер заявки из ответа сервера");
            TariffDraft.HasChanges.Should().BeFalse("заявка отправлена — общий набор очищен");
            TariffDraft.BuildMode.Should().BeFalse("заявка отправлена — режим сборки выключен, Маркетплейс как прежде");
            window.IsModuleChecked("haggle").Should().BeTrue("в окне остаётся то, что отправили");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private string Asked = "";

    // ───────────── полоса ─────────────

    [AvaloniaFact]
    public async Task Strip_shows_plan_changes_server_total_failure_text_and_opens_dialog()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir);
        UserPreferences.Instance.LastKnownPlanName = "Старт";
        var api = new BuilderTariffApi();
        var strip = new TariffStrip(api);
        var opened = 0;
        strip.OpenDialog = () => { opened++; return Task.CompletedTask; };
        var window = new Window { Content = strip };
        try
        {
            using (UiShot.Render(window, new UiSize(900, 200))) { }
            Norm(strip.PlanText).Should().Contain("Ваш тариф: ").And.Contain("1 300");
            strip.ChangesLine.Should().BeEmpty();
            strip.TotalText.Should().BeEmpty("без выбора итога нет — расчёт не нужен");

            // 2026-11-26: «Собрать тариф «Своё»» без набора включает режим сборки (диалог не открывает); «Отмена» возвращает прежний вид
            strip.OpenButton.Content.Should().Be("Собрать тариф «Своё»");
            UiDrive.Click(window, strip.OpenButton);
            opened.Should().Be(0, "первый шаг — режим сборки, не диалог");
            TariffDraft.BuildMode.Should().BeTrue();
            strip.ChangesLine.Should().StartWith("Режим сборки");
            strip.ResetButton.Content.Should().Be("Отмена");
            UiDrive.Click(window, strip.ResetButton);
            TariffDraft.BuildMode.Should().BeFalse();
            strip.ChangesLine.Should().BeEmpty();
            strip.ResetButton.IsVisible.Should().BeFalse();

            TariffDraft.SetModule("currencies", true, false);
            TariffDraft.SetPack("scales", true, false);
            UiShot.Settle(3);
            strip.ChangesLine.Should().Be("Что изменится: +Весы, +Курсы валют");
            strip.TotalText.Should().Be("Считаю…");
            await strip.QuoteNowForTest();
            Norm(strip.TotalText).Should().Be("Итого в месяц: 1 900 сом", "1300 + пакет 400 + «Курсы валют» 200 — итог от сервера, касса не считает");

            api.QuoteError = new HttpRequestException("нет связи");
            TariffDraft.SetModule("haggle", true, false);
            await strip.QuoteNowForTest();
            strip.TotalText.Should().Be("Не удалось рассчитать, повторите", "нет ответа сервера — без цифры");

            UiDrive.Click(window, strip.OpenButton);
            opened.Should().Be(1, "кнопка открывает диалог «Своё»");
            UiDrive.Click(window, strip.ResetButton);
            TariffDraft.HasChanges.Should().BeFalse();
            strip.ChangesLine.Should().BeEmpty();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Strip_and_tile_toggles_exist_only_for_the_owner()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        var api = new BuilderTariffApi();

        TariffDraft.CanBuildOverride = () => false;
        var (marketA, storeA) = OpenMarketplace(api, 1366, 768);
        try
        {
            storeA.TariffStripForTest.Should().BeNull("кассиру и администратору полосы «Своё» нет");
            marketA.GetVisualDescendants().OfType<Border>().Any(b => (string?)b.Tag == "tariff-toggle").Should().BeFalse("и кружков «+» на плитках нет");
        }
        finally
        {
            marketA.Close();
            UiShot.Pump();
        }

        TariffDraft.CanBuildOverride = () => true;
        var (marketB, storeB) = OpenMarketplace(api, 1366, 768);
        try
        {
            storeB.TariffStripForTest.Should().NotBeNull();
            HasToggles(marketB).Should().BeFalse("и у владельца вне режима сборки кружков нет");
            TariffDraft.EnterBuildMode();
            UiShot.Settle(4);
            marketB.GetVisualDescendants().OfType<Border>().Count(b => (string?)b.Tag == "tariff-toggle").Should().BeGreaterThan(10, "у пакетов и функций, которые знает сервер, есть кружок");
            // Темы, виды кассы и «Лояльность» — не про тариф: у них кружка нет.
            storeB.SetFilter(MarketplaceFilter.Themes);
            UiShot.Settle(4);
            marketB.GetVisualDescendants().OfType<Border>().Any(b => (string?)b.Tag == "tariff-toggle").Should().BeFalse("у тем кружка нет");
        }
        finally
        {
            marketB.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Detail_page_has_add_to_tariff_action_that_flips_with_the_shared_selection()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        TariffDraft.CanBuildOverride = () => true;
        var api = new BuilderTariffApi();
        var (market, store) = OpenMarketplace(api, 1366, 768);
        try
        {
            store.OpenItem("module:haggle");
            UiShot.Settle(6);
            // 2026-11-26: в обычное время на странице прежний набор кнопок — «Включить», без «Добавить в тариф «Своё»»
            UiDrive.ButtonByText(market, "Включить").Should().NotBeNull();
            market.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) is { } t && (t.Contains(TariffDraft.AddLabel) || t.Contains(TariffDraft.RemoveLabel)))
                .Should().BeFalse("кнопки «Добавить в тариф» вне режима сборки нет");
            TariffDraft.EnterBuildMode();
            UiShot.Settle(6);
            UiDrive.ButtonByText(market, "Включить").Should().NotBeNull("в режиме сборки главная кнопка остаётся");
            var add = UiDrive.ButtonByText(market, TariffDraft.AddLabel);
            UiDrive.Click(market, add);
            UiShot.Settle(6);
            TariffDraft.AddedModules.Should().Equal("haggle");
            UiDrive.ButtonByText(market, TariffDraft.RemoveLabel).Should().NotBeNull("страница перерисовалась: теперь «Убрать из тарифа»");
            // Меняем в другом месте (как диалог) — страница следует за набором.
            TariffDraft.Clear();
            UiShot.Settle(6);
            UiDrive.ButtonByText(market, TariffDraft.AddLabel).Should().NotBeNull();

            // «Ремонт» (своя карточка) и пакет «ИИ» (pack:ai на Старте) — тоже про тариф.
            TariffDraft.TargetOf("repair").Should().Be(new TariffTarget(TariffTargetKind.Module, "repair"));
            TariffDraft.TargetOf("scales").Should().Be(new TariffTarget(TariffTargetKind.Pack, "scales"));
            TariffDraft.TargetOf("telegram").Should().Be(new TariffTarget(TariffTargetKind.Pack, "telegram_bot"));
            TariffDraft.TargetOf("pack:salesanalytics").Should().Be(new TariffTarget(TariffTargetKind.Pack, "sales_analytics"));
            TariffDraft.TargetOf("loyalty").Should().BeNull("лояльность — не пакет тарифа");
            TariffDraft.TargetOf("theme:classic").Should().BeNull();
            TariffDraft.TargetOf("module:zz_unknown").Should().BeNull("сервер модуль не знает — класть в запрос нельзя");
        }
        finally
        {
            market.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Marketplace_strip_with_pending_selection_fits_and_is_saved_for_eyes(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition", "repair");
        TariffDraft.CanBuildOverride = () => true;
        var api = new BuilderTariffApi();
        var (market, store) = OpenMarketplace(api, width, height);
        try
        {
            TariffDraft.EnterBuildMode(); // 2026-11-26: полоса с кнопками «Отмена» и «Оформить» — самый широкий вид
            TariffDraft.SetModule("haggle", true, false);
            TariffDraft.SetModule("currencies", true, false);
            TariffDraft.SetPack("ai", true, false);
            TariffDraft.SetModule("quick_items", false, true);
            UiShot.Settle(4);
            await store.TariffStripForTest!.QuoteNowForTest();
            UiShot.Settle(4);
            var strip = store.TariffStripForTest!;
            var tag = $"{width}x{height} {lang}";
            var origin = strip.TranslatePoint(default, market)!.Value;
            (origin.X + strip.Bounds.Width).Should().BeLessThanOrEqualTo(market.Bounds.Width + 0.5, $"полоса внутри окна по ширине ({tag})");
            foreach (var btn in strip.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            {
                var p = btn.TranslatePoint(default, market)!.Value;
                (p.X + btn.Bounds.Width).Should().BeLessThanOrEqualTo(market.Bounds.Width + 0.5, $"кнопка «{UiDrive.TextOf(btn)}» не выходит за окно ({tag})");
                btn.Bounds.Width.Should().BeGreaterThan(40);
            }
            var texts = strip.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.Bounds.Height > 0).ToList();
            texts.Should().NotBeEmpty();
            for (var i = 1; i < texts.Count; i++)
            {
                var a = texts[i - 1].TranslatePoint(default, market)!.Value;
                var b = texts[i].TranslatePoint(default, market)!.Value;
                if (Math.Abs(a.X - b.X) < 1)
                    b.Y.Should().BeGreaterThanOrEqualTo(a.Y + texts[i - 1].Bounds.Height - 1.0, $"строки полосы не налезают ({tag})");
            }
            // Текст полосы не заходит под кнопки справа.
            var buttonsLeft = strip.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Min(b => b.TranslatePoint(default, market)!.Value.X);
            foreach (var tb in texts.Where(t => t.GetVisualAncestors().OfType<Button>().FirstOrDefault() is null))
            {
                var p = tb.TranslatePoint(default, market)!.Value;
                (p.X + tb.Bounds.Width).Should().BeLessThanOrEqualTo(buttonsLeft + 1.0, $"текст «{tb.Text}» не под кнопками ({tag})");
            }

            using var shot = UiShot.Render(market);
            MarketplaceModuleLinesTests.Save(shot, $"marketplace-tariff-strip-{Code(lang)}-{width}x{height}");
        }
        finally
        {
            market.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Every_pack_code_of_the_server_catalog_has_its_own_icon_and_color_in_the_dialog()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        TariffBuilderFixture.PackCodes.Should().HaveCount(11, "11 пакетов в GET public/tariffs на 26.11.2026");
        foreach (var code in TariffBuilderFixture.PackCodes)
        {
            TariffRowVisuals.HasPack(code).Should().BeTrue($"у пакета «{code}» нет значка в TariffRowVisuals");
            var (icon, color) = TariffRowVisuals.Pack(code);
            MarketplaceIcons.Has(icon).Should().BeTrue($"{code}: значок «{icon}» есть в MarketplaceIcons");
            icon.Should().NotBe(MarketplaceModuleContent.FallbackIcon, code);
            Avalonia.Media.Color.TryParse(color, out _).Should().BeTrue($"{code}: цвет — hex");
        }
        TariffBuilderFixture.PackCodes.Select(c => TariffRowVisuals.Pack(c).Icon).Should().OnlyHaveUniqueItems("у каждого пакета свой значок");
    }

    // ───────────── чистая логика общего набора ─────────────

    [AvaloniaFact]
    public async Task Draft_stores_only_differences_from_the_current_plan_and_raises_changed_once_per_change()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _fixture = TariffBuilderFixture.Apply(_dir, "condition");
        var raised = 0;
        void On() => raised++;
        TariffDraft.Changed += On;
        try
        {
            TariffDraft.SetModule("haggle", true, false);
            TariffDraft.SetModule("haggle", true, false);
            raised.Should().Be(1, "повтор того же выбора — не изменение");
            TariffDraft.SetModule("haggle", false, false);
            TariffDraft.HasChanges.Should().BeFalse("вернули как было — отличий нет");
            raised.Should().Be(2);

            TariffDraft.SetModule("condition", false, true);
            TariffDraft.RemovedModules.Should().Equal("condition");
            TariffDraft.IsModuleSelected("condition").Should().BeFalse();
            TariffDraft.EffectiveModules().Should().NotContain("condition").And.Contain("scales_labels");
            TariffDraft.SetModule("condition", true, true);
            TariffDraft.HasChanges.Should().BeFalse();

            TariffDraft.ChangesText().Should().BeEmpty();
            TariffDraft.SetPack("ai", true, false);
            TariffDraft.SetModule("haggle", true, false);
            TariffDraft.SetModule("quick_items", false, true);
            TariffDraft.ChangesText(2).Should().StartWith("+").And.Contain("…и ещё 1");
            TariffDraft.ChangeCount.Should().Be(3);
            TariffDraft.Clear();
            TariffDraft.HasChanges.Should().BeFalse();
        }
        finally
        {
            TariffDraft.Changed -= On;
        }
    }
}
