using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Billing;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (оплата подписки через ELQR): окно «Подписка» в Avalonia.Headless на заглушке IBillingApi (сервер не нужен). Проверяется: выбор срока и тарифа, счёт со строками сервера, QR и таймер,
/// опрос статуса до «Оплачено ✓», «Время оплаты вышло» и «Создать новый счёт», ошибка банка 502 (можно повторить новым счётом), обрыв связи (тот же Idempotency-Key, повторное нажатие не плодит счета),
/// «нет прав у кассира», «оплата не подключена», учебная оплата только в test, текст не обрезан на 1024×600 и 1366×768 (ru, ky, uz). Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\subscription-*.png.</summary>
[Collection("UiScenarios")]
public sealed class SubscriptionWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 20, 0, 0, TimeSpan.FromHours(6));

    private sealed class FakeBillingApi : IBillingApi
    {
        public BillingConfig Config = Cfg();
        public Exception? ConfigError;
        public Exception? CreateError;
        public int FailCreates;                       // сколько первых созданий падает обрывом связи
        public List<string> Keys { get; } = [];
        public int Created;
        public List<string?> Periods { get; } = [];                   // 2026-11-21: period каждого счёта (null — по месяцам)
        public List<int> CreatedMonths { get; } = [];
        public Queue<BillingInvoice> Polls { get; } = new();
        public bool PollFails;
        public int PollCount;
        public List<string> TestPays { get; } = [];
        public List<string> Cancels { get; } = [];
        public string Qr = MakeQr();
        public IReadOnlyList<BillingInvoice> History = [];

        public Task<BillingConfig> GetConfigAsync(CancellationToken ct = default) =>
            ConfigError is not null ? Task.FromException<BillingConfig>(ConfigError) : Task.FromResult(Config);

        public Task<BillingInvoice> CreateInvoiceAsync(int months, string? planCode, string? period, string idempotencyKey, CancellationToken ct = default)
        {
            Periods.Add(period);
            return CreateInvoiceAsync(months, planCode, idempotencyKey, ct);
        }

        public Task<BillingInvoice> CreateInvoiceAsync(int months, string? planCode, string idempotencyKey, CancellationToken ct = default)
        {
            CreatedMonths.Add(months);
            Keys.Add(idempotencyKey);
            if (FailCreates-- > 0)
                throw new HttpRequestException("обрыв связи");
            if (CreateError is not null)
                throw CreateError;
            Created++;
            return Task.FromResult(Invoice("pending", months, Qr));
        }

        public Task<BillingInvoice> GetInvoiceAsync(string invoiceId, CancellationToken ct = default)
        {
            PollCount++;
            if (PollFails)
                throw new HttpRequestException("нет сети");
            return Task.FromResult(Polls.Count > 0 ? Polls.Dequeue() : Invoice("pending", 3, null));
        }

        public Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(string? status = null, CancellationToken ct = default) => Task.FromResult(History);

        public Task<BillingInvoice?> CancelInvoiceAsync(string invoiceId, CancellationToken ct = default)
        {
            Cancels.Add(invoiceId);
            return Task.FromResult<BillingInvoice?>(Invoice("cancelled", 3, null));
        }

        public Task<BillingInvoice?> TestPayAsync(string invoiceId, string result, CancellationToken ct = default)
        {
            TestPays.Add(result);
            Polls.Enqueue(Invoice(result == "success" ? "paid" : result == "processing" ? "processing" : result, 3, null, paidUntil: "2027-01-19"));
            return Task.FromResult<BillingInvoice?>(null);
        }
    }

    private static string MakeQr()
    {
        using var bmp = new SKBitmap(64, 64);
        bmp.Erase(SKColors.Black);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(data.ToArray());
    }

    private static BillingConfig Cfg(string mode = "test", bool available = true, string? until = "2026-12-19", bool year = false) =>
        BillingParser.ParseConfig(JsonDocument.Parse("{\"available\":" + (available ? "true" : "false") + ",\"mode\":\"" + mode + "\",\"bank_fee\":\"30.00\",\"months\":[1,3,6,12],\"current_plan\":\"standard\","
            + (until is null ? "" : "\"valid_until\":\"" + until + "\",")
            + (year ? "\"plans\":[{\"code\":\"start\",\"price_month\":\"1300.00\",\"price_year\":\"13000.00\"},{\"code\":\"standard\",\"price_month\":\"3600.00\",\"price_year\":\"36000.00\"}],\"periods\":[\"month\",\"year\"],\"year_paid_months\":10,"
                : "\"plans\":[{\"code\":\"start\",\"price_month\":\"1300.00\"},{\"code\":\"standard\",\"price_month\":\"3600.00\"}],")
            + "\"qr_ttl_seconds\":1200}").RootElement.Clone());

    private static BillingInvoice Invoice(string status, int months, string? qr, string? paidUntil = null) =>
        BillingParser.ParseInvoice(JsonDocument.Parse("{\"invoice_id\":\"inv-1\",\"status\":\"" + status + "\",\"test_mode\":true,\"plan\":\"standard\",\"plan_name\":\"Стандарт\",\"months\":" + months
            + ",\"subtotal\":\"10800.00\",\"discount\":\"0.00\",\"bank_fee\":\"30.00\",\"amount\":\"10830.00\","
            + "\"lines\":[{\"title\":\"Подписка NurCRM, 3 мес.\",\"amount\":\"10800.00\"},{\"title\":\"Комиссия банка — 30 сом\",\"amount\":\"30.00\"}],"
            + (qr is null ? "" : "\"qr_image\":\"" + qr + "\",\"qr_link\":\"https://qr.example/pay/abc\",")
            + "\"expires_at\":\"" + Now.AddMinutes(20).ToString("O") + "\","
            + (paidUntil is null ? "" : "\"valid_until\":\"" + paidUntil + "\",")
            + "\"created_at\":\"" + Now.ToString("O") + "\"}").RootElement.Clone());

    private static string Norm(string text) => text.Replace((char)0x00A0, ' ').Replace((char)0x202F, ' ');

    private static SubscriptionWindow Make(FakeBillingApi api, bool canManage = true, Action<string>? afterPaid = null)
    {
        var window = new SubscriptionWindow(api, canManage) { Clock = () => Now, AfterPaid = () => { afterPaid?.Invoke("paid"); return Task.CompletedTask; } };
        return window;
    }

    [AvaloniaFact]
    public async Task HappyPath_Choose_Invoice_Qr_Timer_Polling_Paid()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi();
        var refreshed = 0;
        var window = Make(api, afterPaid: _ => refreshed++);
        try
        {
            window.Show();
            await window.LoadAsync();
            window.IsChoosePage.Should().BeTrue();
            window.MonthChoices.Should().Equal(1, 3, 6, 12);
            window.UntilText.Should().Contain("19.12.2026").And.Contain("Подписка до");
            window.PayEnabled.Should().BeTrue();
            window.TestPayVisible.Should().BeFalse("до счёта учебной оплаты нет");

            window.SetMonths(3);
            await window.CreateForTestAsync();
            window.IsPayPage.Should().BeTrue();
            window.HasQr.Should().BeTrue("QR из base64 PNG");
            window.LineTexts.Select(l => l.Title).Should().Equal("Подписка NurCRM, 3 мес.", "Комиссия банка — 30 сом");
            Norm(window.AmountText).Should().Contain("10 830");
            window.TimerText.Should().Contain("20:00");
            window.StatusText.Should().Contain("Ожидаем оплату");
            window.TestPayVisible.Should().BeTrue("config.mode == test и владелец");
            window.CopyLinkVisible.Should().BeTrue();
            api.Keys.Should().HaveCount(1);

            api.Polls.Enqueue(Invoice("processing", 3, null));
            await window.PollForTestAsync();
            window.StatusText.Should().Contain("Обрабатывается");

            api.Polls.Enqueue(Invoice("paid", 3, null, paidUntil: "2027-03-19"));
            await window.PollForTestAsync();
            window.StatusText.Should().Contain("Оплачено ✓").And.Contain("19.03.2027");
            window.UntilText.Should().Contain("19.03.2027", "шапка обновилась");
            window.TestPayVisible.Should().BeFalse();
            window.HasQr.Should().BeFalse("после оплаты QR не нужен");
            refreshed.Should().Be(1, "профиль компании обновлён один раз");

            await window.PollForTestAsync(); // счёт закрыт — опрос больше ничего не делает
            api.PollCount.Should().Be(2);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-21 (ответ Умара 21.11): «Месяц / Год» — год = 10 месяцев: переключатель есть, когда сервер отдал periods и price_year; цена тарифа за год — только серверная;
    /// счёт на год уходит с period "year" (12 месяцев); без годовой оплаты на сервере переключателя нет, а «12 мес.» остаётся в списке сроков.</summary>
    [AvaloniaFact]
    public async Task Year_Toggle_SendsPeriodYear_ShowsServerYearPrice_AndHiddenWithoutServerSupport()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi { Config = Cfg(year: true) };
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            window.PeriodChoiceVisible.Should().BeTrue();
            window.MonthChoices.Should().Equal(1, 3, 6);
            Norm(window.PlanChipText("standard")).Should().Contain("3 600").And.Contain("/ мес");
            window.SetYear(true);
            Norm(window.PlanChipText("standard")).Should().Contain("36 000").And.Contain("/ год");
            window.YearNoteText.Should().Contain("10");
            await window.CreateForTestAsync();
            api.Periods.Should().Equal("year");
            api.CreatedMonths.Should().Equal(12);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }

        var old = new FakeBillingApi();
        var window2 = Make(old);
        try
        {
            window2.Show();
            await window2.LoadAsync();
            window2.PeriodChoiceVisible.Should().BeFalse("сервер года не отдал — переключателя нет");
            window2.MonthChoices.Should().Equal(1, 3, 6, 12);
            window2.SetMonths(12);
            await window2.CreateForTestAsync();
            old.Periods.Should().BeEmpty("без года — прежний счёт по месяцам");
        }
        finally
        {
            window2.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task DoubleClickOnPay_DoesNotCreateSecondInvoice()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi();
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            var first = window.CreateForTestAsync();
            var second = window.CreateForTestAsync(); // второе нажатие, пока первое идёт
            await Task.WhenAll(first, second);
            api.Created.Should().Be(1);
            api.Keys.Should().HaveCount(1);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Expired_ShowsTimeIsUp_AndNewInvoiceUsesNewKey()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi();
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            await window.CreateForTestAsync();
            api.Polls.Enqueue(Invoice("expired", 3, null));
            await window.PollForTestAsync();
            window.StatusText.Should().Contain("Время оплаты вышло").And.Contain("новый счёт");
            window.NewInvoiceVisible.Should().BeTrue();
            window.HasQr.Should().BeFalse("старый QR не показываем");

            UiDrive.Click(window, UiDrive.ButtonByText(window, "Создать новый счёт"));
            window.IsChoosePage.Should().BeTrue();
            await window.CreateForTestAsync();
            api.Keys.Should().HaveCount(2).And.OnlyHaveUniqueItems("новая попытка — новый ключ, старый счёт не переиспользуется");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task LocalExpiry_WhenServerStillPending_AfterTimerRunsOut()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi();
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            await window.CreateForTestAsync();
            window.Clock = () => Now.AddMinutes(21); // срок вышел 1 минуту назад
            await window.TickForTestAsync();
            window.StatusText.Should().Contain("Время оплаты вышло");
            window.NewInvoiceVisible.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task BankUnavailable502_ShowsMessage_AndRetryCreatesNewAttempt()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi
        {
            CreateError = new ApiException("bank", 502, JsonDocument.Parse("{\"code\":\"bank_unavailable\",\"data\":{\"invoice_id\":\"inv-err\"}}").RootElement.Clone()),
        };
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            await window.CreateForTestAsync();
            window.IsChoosePage.Should().BeTrue("остаёмся на выборе — можно повторить");
            window.ErrorText.Should().NotBeNull().And.Contain("Банк").And.Contain("ещё раз");
            window.PayEnabled.Should().BeTrue();

            api.CreateError = null;
            await window.CreateForTestAsync();
            window.IsPayPage.Should().BeTrue();
            api.Keys.Should().HaveCount(2).And.OnlyHaveUniqueItems("после ответа банка 502 — новый счёт с новым ключом");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task NetworkDrop_OnCreate_KeepsSameKey_ForSafeRetry()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi { FailCreates = 1 };
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            await window.CreateForTestAsync();
            window.ErrorText.Should().NotBeNull().And.Contain("не задвоится");
            await window.CreateForTestAsync();
            api.Keys.Should().HaveCount(2);
            api.Keys[0].Should().Be(api.Keys[1], "обрыв связи — повтор с тем же ключом: сервер вернёт тот же счёт");
            window.IsPayPage.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task LostConnection_AfterQr_ShowsMessage_AndPollingContinues()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi();
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            await window.CreateForTestAsync();
            api.PollFails = true;
            await window.PollForTestAsync();
            UiDrive.AllText(window).Should().Contain("Нет связи с сервером").And.Contain("проверка продолжится");
            window.IsPayPage.Should().BeTrue();
            window.HasQr.Should().BeTrue("QR остаётся на экране");

            api.PollFails = false;
            api.Polls.Enqueue(Invoice("paid", 3, null, paidUntil: "2027-01-19"));
            await window.PollForTestAsync(); // продолжили по invoice_id
            UiDrive.AllText(window).Should().NotContain("Нет связи с сервером");
            window.StatusText.Should().Contain("Оплачено ✓");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Cashier_SeesDateOnly_NoPayButton()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi();
        var window = Make(api, canManage: false);
        try
        {
            window.Show();
            await window.LoadAsync();
            window.PayEnabled.Should().BeFalse("кассиру «Оплатить» не показывается");
            window.UntilText.Should().Contain("19.12.2026");
            UiDrive.AllText(window).Should().Contain("владелец или администратор");
            await window.CreateForTestAsync();
            api.Created.Should().Be(0);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task NotConnected_ConfigUnavailable_Or409_ShowsExplanation()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        foreach (var api in new[]
                 {
                     new FakeBillingApi { Config = Cfg(available: false) },
                     new FakeBillingApi { ConfigError = new ApiException("x", 409, JsonDocument.Parse("{\"code\":\"billing_unavailable\"}").RootElement.Clone()) },
                 })
        {
            var window = Make(api);
            try
            {
                window.Show();
                await window.LoadAsync();
                window.IsNotConnected.Should().BeTrue();
                window.PayEnabled.Should().BeFalse();
                UiDrive.AllText(window).Should().Contain("Оплата подписки через приложение ещё не подключена для вашей компании");
            }
            finally
            {
                window.Close();
                UiShot.Pump();
            }
        }
    }

    [AvaloniaFact]
    public async Task TestPay_OnlyInTestMode_LiveHasNoSuchButton_AndSuccessPaysTheInvoice()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);

        var live = new FakeBillingApi { Config = Cfg(mode: "live") };
        var liveWindow = Make(live);
        try
        {
            liveWindow.Show();
            await liveWindow.LoadAsync();
            await liveWindow.CreateForTestAsync();
            liveWindow.TestPayVisible.Should().BeFalse("в боевом режиме учебной оплаты нет вообще");
            await liveWindow.TestPayForTestAsync("success");
            live.TestPays.Should().BeEmpty("в боевом режиме запрос test-pay не уходит");
            UiDrive.AllText(liveWindow).Should().NotContain("Учебная оплата");
        }
        finally
        {
            liveWindow.Close();
            UiShot.Pump();
        }

        var test = new FakeBillingApi();
        var testWindow = Make(test);
        try
        {
            testWindow.Show();
            await testWindow.LoadAsync();
            await testWindow.CreateForTestAsync();
            testWindow.TestPayVisible.Should().BeTrue();
            UiDrive.AllText(testWindow).Should().Contain("Учебная оплата (тест)");
            await testWindow.TestPayForTestAsync("success");
            test.TestPays.Should().Equal("success");
            testWindow.StatusText.Should().Contain("Оплачено ✓").And.Contain("19.01.2027");
        }
        finally
        {
            testWindow.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Back_CancelsUnpaidInvoice_AndReturnsToChoice()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi();
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            await window.CreateForTestAsync();
            await window.BackForTestAsync();
            api.Cancels.Should().Equal("inv-1");
            window.IsChoosePage.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ExpiryInHeader_SoonIsWarned_ExpiredIsMarked()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var soon = Make(new FakeBillingApi { Config = Cfg(until: "2026-11-22") });
        var expired = Make(new FakeBillingApi { Config = Cfg(until: "2026-11-10") });
        try
        {
            soon.Show();
            await soon.LoadAsync();
            soon.UntilText.Should().Contain("22.11.2026").And.Contain("осталось 3 дн.");
            expired.Show();
            await expired.LoadAsync();
            expired.UntilText.Should().Contain("просрочена");
        }
        finally
        {
            soon.Close();
            expired.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task History_ShowsDateTermAmountStatus()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeBillingApi { History = [Invoice("paid", 3, null), Invoice("expired", 1, null)] };
        var window = Make(api);
        try
        {
            window.Show();
            await window.LoadAsync();
            UiShot.Settle(4);
            window.HistoryRows.Should().Be(2);
            var all = Norm(UiDrive.AllText(window));
            all.Should().Contain("История платежей").And.Contain("Оплачено").And.Contain("Истёк").And.Contain("10 830");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OnlyOneWindow_PerProgram()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        WindowSingleton.ResetForTests();
        var api = new FakeBillingApi();
        var first = WindowSingleton.ShowOnce<SubscriptionWindow>(null, () => Make(api));
        var second = WindowSingleton.ShowOnce<SubscriptionWindow>(null, () => Make(api));
        try
        {
            second.Should().BeSameAs(first);
            WindowSingleton.Count.Should().Be(1);
        }
        finally
        {
            first.Close();
            UiShot.Pump();
        }
    }

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task ChooseAndPayScreens_FitScreen_NoClippedText(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var api = new FakeBillingApi { History = [Invoice("paid", 3, null)] };
        var window = Make(api);
        var langCode = lang == AppLanguage.Kyrgyz ? "ky" : lang == AppLanguage.Uzbek ? "uz" : "ru";
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await window.LoadAsync();
            UiShot.Settle(8);
            Check(window, tag, langCode, width, height, "choose");

            await window.CreateForTestAsync();
            UiShot.Settle(8);
            window.IsPayPage.Should().BeTrue();
            Check(window, tag, langCode, width, height, "pay");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static void Check(SubscriptionWindow window, string tag, string langCode, int width, int height, string page)
    {
        var clipped = TextClipChecker.Find(window, "subscription-" + page, langCode);
        clipped.Should().BeEmpty($"текст не обрезан ({tag}, {page}): " + string.Join("; ", clipped.Select(v => v.Detail)));
        foreach (var btn in window.GetVisualDescendants().OfType<Button>().Where(b => b.GetType() == typeof(Button) && b.IsEffectivelyVisible && b.Bounds.Height > 0 && b.Classes.Contains("modal-primary")))
        {
            var p = btn.TranslatePoint(default, window)!.Value;
            (p.Y + btn.Bounds.Height).Should().BeLessThanOrEqualTo(window.Bounds.Height + 0.5, $"главная кнопка «{UiDrive.TextOf(btn)}» внутри окна ({tag}, {page})");
        }
        window.Bounds.Width.Should().BeLessThanOrEqualTo(width + 0.5, tag);
        window.Bounds.Height.Should().BeLessThanOrEqualTo(height + 0.5, tag);

        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"subscription-{page}-{langCode}-{width}x{height}.png"), data.ToArray());
    }
}
