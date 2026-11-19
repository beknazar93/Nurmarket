using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Fiscal;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-14 (П0-1, подготовка к eKassa): карточка «Фискальный чек (ККМ)» (статус, ФД, ФМ, РН, QR «Открыть / Копировать», замок пробитого чека), карточка «Налоги и ККМ» в настройках
/// (без «Сохранить» ничего не уходит), отчёт «Продажи без фискального чека» (строки, страницы, ошибка сервера блоком состояния). ru и ky, 1024×600 и 1366×768; кнопки ≥ 44 px, текст не обрезан.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\fiscal-*.png. Поддельный сервер — WireMock, ККМ и сервер Умара не трогаются.</summary>
[Collection("UiScenarios")]
public sealed class FiscalUiTests : IDisposable
{
    // 2026-11-19: Dispose раньше обнулял ClientResolver насовсем — все следующие по алфавиту сценарии (Owner*, LayoutStability) оставались без клиента отчётов
    // (список возвратов не запрашивался, «Править возврат» не появлялась). Возвращаем прежний делегат.
    private readonly Func<NurMarketApiClient?>? _savedResolver = NurCrmReportsApi.ClientResolver;

    public FiscalUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        NurCrmReportsApi.ClientResolver = _savedResolver;
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "fiscal-" + name + ".png"), data.ToArray());
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"Нет элемента {name}");

    private static T? TryFind<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    private static string TextOf(Visual root, string name) => Find<TextBlock>(root, name).Text ?? "";

    private static FiscalRequisites Accepted() => new(FiscalStatus.Accepted, "FD-100500", "FM-7788", "RN-0001234", "SIGN-ABCD-1234", "https://ofd.example.kg/check?id=100500",
        new DateTimeOffset(2026, 11, 14, 10, 15, 0, TimeSpan.FromHours(6)), "KS-12");

    private static Window HostWindow(Control content, int width, int height, double cardWidth = 520)
    {
        var panel = new StackPanel { Margin = new Thickness(16), Width = Math.Min(cardWidth, width - 32), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        panel.Children.Add(content);
        return new Window { Content = new ScrollViewer { Content = panel }, Width = width, Height = height };
    }

    // ───────────── карточка «Фискальный чек (ККМ)» ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Card_shows_accepted_failed_and_not_fiscalized_states(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var card = new FiscalRequisitesCard { ForceVisible = true };
        var window = HostWindow(card, width, height);
        try
        {
            // Пробит: реквизиты, QR и замок.
            card.Show(Accepted(), FiscalDocKind.Sale);
            UiShot.Render(window, new UiSize(width, height));
            card.IsVisible.Should().BeTrue();
            TextOf(card, "FiscalStatusText").Should().Be(FiscalStatusNames.Label(FiscalStatus.Accepted));
            TextOf(window, "FiscalDocText").Should().Be("FD-100500");
            TextOf(window, "FiscalMemoryText").Should().Be("FM-7788");
            TextOf(window, "FiscalRegText").Should().Be("RN-0001234");
            TextOf(window, "FiscalSignText").Should().Be("SIGN-ABCD-1234");
            TextOf(window, "FiscalAtText").Should().Be("14.11.2026 10:15");
            TextOf(window, "FiscalShiftText").Should().Be("KS-12");
            TextOf(window, "FiscalLockText").Should().Be(FiscalErrors.LockedSaleText);
            Find<Control>(window, "FiscalLockText").IsVisible.Should().BeTrue();
            Find<StackPanel>(window, "FiscalQrRow").IsVisible.Should().BeTrue();
            card.QrUrl.Should().Be("https://ofd.example.kg/check?id=100500");
            var open = Find<Button>(window, "FiscalQrOpenButton");
            var copy = Find<Button>(window, "FiscalQrCopyButton");
            open.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, $"кнопка «Открыть» — цель нажатия ≥ 44 ({slug})");
            copy.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, slug);
            open.IsEnabled.Should().BeTrue();
            Save(window, "card-accepted-" + slug);
            TextClipChecker.Find(window, "fiscal-card-accepted", lang.ToString()).Should().BeEmpty($"текст не обрезан ({slug})");

            // Ошибка ККМ: красный статус и причина, замка нет, QR нет.
            card.Show(new FiscalRequisites(FiscalStatus.Failed, Error: "Нет связи с ККМ: таймаут 30 с"), FiscalDocKind.Sale);
            UiShot.Settle(2);
            TextOf(card, "FiscalStatusText").Should().Be(FiscalStatusNames.Label(FiscalStatus.Failed));
            TextOf(window, "FiscalErrorText").Should().Contain("таймаут 30 с");
            Find<Control>(window, "FiscalLockText").IsVisible.Should().BeFalse("непробитый чек можно править");
            Find<StackPanel>(window, "FiscalQrRow").IsVisible.Should().BeFalse();
            Save(window, "card-failed-" + slug);

            // Не пробит (старый чек, fiscal_status none).
            card.Show(FiscalRequisites.None, FiscalDocKind.Sale);
            UiShot.Settle(2);
            TextOf(card, "FiscalStatusText").Should().Be(FiscalStatusNames.Label(FiscalStatus.None));
            Find<Control>(window, "FiscalLockText").IsVisible.Should().BeFalse();
            Find<Control>(window, "FiscalErrorText").IsVisible.Should().BeFalse();
            TryFind<TextBlock>(window, "FiscalDocText").Should().BeNull("реквизитов нет — строк нет");
            Save(window, "card-none-" + slug);

            // Возврат: свой заголовок и свой текст замка.
            card.Show(Accepted(), FiscalDocKind.Return);
            UiShot.Settle(2);
            TextOf(window, "FiscalLockText").Should().Be(FiscalErrors.LockedReturnText);
            Find<TextBlock>(window, "FiscalTitle").Text!.ToLowerInvariant().Should().Contain(Tr.T("возврат", "кайтаруу", "return", "iade", "qaytarish"));
            Save(window, "card-return-" + slug);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Card_is_hidden_when_fiscalization_is_off_and_does_not_take_space()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        FiscalSettings.ShowsFiscalUi.Should().BeFalse("по умолчанию режим «выключено»");
        var card = new FiscalRequisitesCard();
        var window = HostWindow(card, 1024, 600);
        try
        {
            card.Show(Accepted(), FiscalDocKind.Sale);
            UiShot.Render(window, new UiSize(1024, 600));
            card.IsVisible.Should().BeFalse("без фискализации экран остаётся как сегодня");
            card.Bounds.Height.Should().Be(0);

            // Place в панель карточки чека: при выключенном режиме карточки скрыты; с принудительным показом — карточка чека и по одной на каждый возврат.
            var panel = new StackPanel();
            FiscalRequisitesCard.Place(panel, 0, Accepted(), new[] { Accepted() });
            panel.Children.OfType<FiscalRequisitesCard>().Should().HaveCount(2).And.OnlyContain(c => !c.IsVisible);
            FiscalRequisitesCard.Place(panel, 0, FiscalRequisites.None, null, force: true);
            panel.Children.OfType<FiscalRequisitesCard>().Should().ContainSingle("карточки прежнего чека убираются").Which.IsVisible.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Card_open_button_is_disabled_for_a_broken_link_and_copy_is_available()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var card = new FiscalRequisitesCard { ForceVisible = true };
        var window = HostWindow(card, 1024, 600);
        try
        {
            card.Show(Accepted() with { QrUrl = "не ссылка" }, FiscalDocKind.Sale);
            UiShot.Render(window, new UiSize(1024, 600));
            Find<Button>(window, "FiscalQrOpenButton").IsEnabled.Should().BeFalse();
            Find<Button>(window, "FiscalQrCopyButton").IsEnabled.Should().BeTrue();
            card.Show(Accepted() with { QrUrl = null }, FiscalDocKind.Sale);
            UiShot.Settle(2);
            Find<StackPanel>(window, "FiscalQrRow").IsVisible.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── «Налоги и ККМ» в настройках ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Settings_card_sends_nothing_until_save_and_validates_the_form(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var saves = new List<FiscalSettingsForm>();
        var card = new FiscalSettingsCard
        {
            ForceVisible = true, SkipServerRead = true,
            SaveOverride = form =>
            {
                saves.Add(form);
                return Task.FromResult(new FiscalCapabilities(true, FiscalMode.Kassa, form.FiscalEnabled, form.TaxRegime, form.VatPayer, form.VatRates, form.NspRate));
            },
        };
        var window = HostWindow(card, width, height, cardWidth: 640);
        try
        {
            UiShot.Render(window, new UiSize(width, height));
            var save = Find<Button>(window, "FiscalSaveButton");
            save.Bounds.Height.Should().BeGreaterThanOrEqualTo(44, $"«Сохранить» ≥ 44 ({slug})");
            foreach (var name in new[] { "FiscalRegimeBox", "FiscalRatesBox", "FiscalNspBox" })
                Find<TextBox>(window, name).Bounds.Height.Should().BeGreaterThanOrEqualTo(44, name);
            TextOf(window, "FiscalProviderText").Should().Be(NotConnectedFiscalProvider.NotConnectedText);
            TextOf(window, "FiscalProviderText").Should().Contain("Telemedia");
            TextOf(window, "FiscalModeText").Should().Be(FiscalFormRules.ModeText(FiscalMode.Off), "режим пока «выключено» — как у компании без фискализации");

            // Ввод и потеря фокуса — ничего не отправляют.
            var regime = Find<TextBox>(window, "FiscalRegimeBox");
            regime.Text = "general";
            Find<TextBox>(window, "FiscalRatesBox").Text = "0; 12";
            Find<TextBox>(window, "FiscalNspBox").Text = "2";
            Find<CheckBox>(window, "FiscalVatPayerCheck").IsChecked = true;
            UiShot.Settle(2);
            saves.Should().BeEmpty("правки уходят на сервер только по кнопке «Сохранить»");
            Save(window, "settings-edited-" + slug);

            // Неверная ставка — текст ошибки, на сервер ничего.
            Find<TextBox>(window, "FiscalRatesBox").Text = "12; abc";
            await card.SaveAsync();
            saves.Should().BeEmpty();
            Find<Control>(window, "FiscalSaveResult").IsVisible.Should().BeTrue();
            TextOf(window, "FiscalSaveResult").Should().Contain("abc");

            // Верная форма — одно сохранение с правильными значениями.
            Find<TextBox>(window, "FiscalRatesBox").Text = "0; 12";
            await card.SaveAsync();
            UiShot.Settle(2);
            saves.Should().ContainSingle();
            saves[0].TaxRegime.Should().Be("general");
            saves[0].VatPayer.Should().BeTrue();
            saves[0].VatRates.Should().Equal(0m, 12m);
            saves[0].NspRate.Should().Be(2m);
            TextOf(window, "FiscalSaveResult").Should().Be(Tr.T("Сохранено.", "Сакталды.", "Saved.", "Kaydedildi.", "Saqlandi."));
            Save(window, "settings-saved-" + slug);
            TextClipChecker.Find(window, "fiscal-settings", lang.ToString()).Should().BeEmpty($"текст не обрезан ({slug})");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Settings_card_is_hidden_without_fiscal_feature_and_forbidden_save_explains_rights()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var hidden = new FiscalSettingsCard { SkipServerRead = true };
        hidden.IsVisible.Should().BeFalse("сервер не знает fiscal_kassa — карточки нет");

        var card = new FiscalSettingsCard
        {
            ForceVisible = true, SkipServerRead = true,
            SaveOverride = _ => throw new ApiException("forbidden", 403),
        };
        var window = HostWindow(card, 1024, 600, 640);
        try
        {
            UiShot.Render(window, new UiSize(1024, 600));
            await card.SaveAsync();
            UiShot.Settle(2);
            TextOf(window, "FiscalSaveResult").Should().Contain(Tr.T("владелец", "ээси", "owner", "sahip", "egasi"));
            Find<Button>(window, "FiscalSaveButton").IsEnabled.Should().BeTrue("после отказа кнопка снова доступна");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── отчёт «Продажи без фискального чека» ─────────────

    private static List<FiscalReportRow> SampleRows(int count) => Enumerable.Range(1, count).Select(i => new FiscalReportRow(
        "s" + i, (1000 + i).ToString(), new DateTime(2026, 11, 10, 9, 0, 0).AddMinutes(i * 7), 120.5m * i, i % 2 == 0 ? "Айгерим Садыкова" : "Нурлан",
        "cash", i % 3 == 0 ? FiscalStatus.Failed : FiscalStatus.None, i % 3 == 0 ? "Нет связи с ККМ" : null)).ToList();

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Report_shows_rows_pages_and_exports_all_loaded_rows(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var window = new FiscalSalesReportWindow();
        try
        {
            var rows = SampleRows(120);
            window.ShowForPreview(rows, new DateTime(2026, 10, 15), new DateTime(2026, 11, 14), 120);
            UiShot.Render(window, new UiSize(width, height));
            Find<StackPanel>(window, "FiscalReportBody").Children.Should().HaveCount(1 + FiscalSalesReportWindow.RowsPerPage, "шапка + 50 строк");
            TextOf(window, "FiscalReportStatus").Should().Contain("120");
            var pager = Find<StackPanel>(window, "FiscalReportPager");
            pager.IsVisible.Should().BeTrue("120 строк — 3 страницы");
            Find<Button>(window, "FiscalReportPrev").IsEnabled.Should().BeFalse();
            Find<Button>(window, "FiscalReportNext").Bounds.Height.Should().BeGreaterThanOrEqualTo(44, slug);
            Save(window, "report-" + slug);

            Press(Find<Button>(window, "FiscalReportNext"));
            UiShot.Settle(2);
            Find<Button>(window, "FiscalReportPrev").IsEnabled.Should().BeTrue();
            Find<StackPanel>(window, "FiscalReportBody").Children.Should().HaveCount(1 + FiscalSalesReportWindow.RowsPerPage);
            Press(Find<Button>(window, "FiscalReportNext"));
            UiShot.Settle(2);
            Find<StackPanel>(window, "FiscalReportBody").Children.Should().HaveCount(1 + 20, "на третьей странице 20 строк");
            Find<Button>(window, "FiscalReportNext").IsEnabled.Should().BeFalse();
            TextClipChecker.Find(window, "fiscal-report", lang.ToString()).Should().BeEmpty($"текст не обрезан ({slug})");

            // Excel — все 120 загруженных строк, а не одна страница.
            var table = ((IOwnerSectionExcel)window).BuildExportTable();
            table.Should().NotBeNull();
            table!.Rows.Should().HaveCount(120);
            table.Columns.Should().HaveCount(8);
            table.Rows[0][3].Should().Be(120.5m);
            table.Title.Should().Be(FiscalSalesReportWindow.TitleText);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Report_server_error_is_shown_as_state_block_with_retry_and_empty_is_a_friendly_state()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        NurCrmReportsApi.ClientResolver = () => TestHost.Services.GetRequiredService<NurMarketApiClient>();
        UiFake.Server.Json("GET", "/api/main/reports/sales-without-fiscal/", 403, "{\"detail\":\"Нет прав\"}");
        var window = new FiscalSalesReportWindow();
        try
        {
            UiShot.Render(window, new UiSize(1024, 600));
            Wait(() => window.GetVisualDescendants().OfType<Control>().Any(c => c.Classes.Contains("state-block")));
            var block = window.GetVisualDescendants().OfType<Control>().First(c => c.Classes.Contains("state-block"));
            block.Tag.Should().Be(StateKind.Error, "ошибка сервера — блок состояния, а не красная строка");
            Find<StackPanel>(window, "FiscalReportBody").Children.Should().BeEmpty();
            Save(window, "report-error-ru-1024x600");

            // Пустой период — «Все продажи периода пробиты».
            window.ShowForPreview(new List<FiscalReportRow>(), new DateTime(2026, 11, 14), new DateTime(2026, 11, 14), 0);
            UiShot.Settle(2);
            window.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("state-block")).Should().ContainSingle()
                .Which.Tag.Should().Be(StateKind.Empty);
            ((IOwnerSectionExcel)window).BuildExportTable().Should().BeNull("выгружать нечего");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // «Далее» стоит под 50 строками — вне видимой части окна, поэтому нажатие без мыши (как клавишей)
    private static void Press(Button b) => b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static void Wait(Func<bool> done, int maxMs = 6000)
    {
        var started = DateTime.UtcNow;
        while (!done() && (DateTime.UtcNow - started).TotalMilliseconds < maxMs)
            UiShot.Settle(1, 40);
        UiShot.Settle(2, 30);
    }
}
