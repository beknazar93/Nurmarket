using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 1»): окна «Бот-продавец», «Проверить бота», «Диалоги бота» и «Продажи бота» на заглушке сервера (сеть не нужна).
/// (1) Раскладка на 1024×600 и 1366×768 в ru, ky, uz: поля не налезают друг на друга, на подписи и кнопки, текст не обрезан (те же проверки, что у остальных окон — FormFieldsOverlapUiTests.FindViolations
/// и TextClipChecker), в том числе с раскрытой перепиской и с расписанием «по дням». (2) «Сохранить» шлёт только изменённые поля, неверные часы не уходят на сервер. (3) «Проверить бота» показывает ответ и «позвал бы продавца».
/// (4) «Взять» / «Вернуть боту» шлют действие. (5) Кнопки в разделе «Телеграм-бот» видны только при признаках bot_seller / bot_handoff / bot_sales_stats.</summary>
[Collection("UiScenarios")]
public sealed class BotSellerUiTests
{
    private static readonly UiSize[] Sizes = [new(1024, 600), new(1366, 768)];

    internal static BotSellerConfig Config(BotSellerHours? hours = null) => new(true, true, "friendly", "Здравствуйте!", "Магазин NBS", ["даром"], "auto", true, hours ?? BotSellerHours.Default,
        "Продавец ответит {when}", 120, 30000, "Возврат в течение 14 дней при сохранении чека и упаковки.", "По Бишкеку 150 сом", "Наличные, QR, карта", "Ежедневно 9:00–21:00",
        new BotBargainConfig(true, 5m, 10m, true, 3, ["c1"], false));

    private static BotDialog Dialog(string chat, string name, string status, BotHandoff? handoff = null) =>
        new(chat, name, "user" + chat, 4, 0, new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(6)), "хочу поговорить с менеджером про доставку в Ош", status, handoff);

    internal sealed class FakeApi : IBotSellerApi, IBotBargainApi
    {
        // 2026-11-26 (торг, Ф2): правила товаров, симулятор, журнал оферт и категории
        public List<BotBargainProduct> Products =
        [
            new("p1", "Куртка зимняя, размер 52", 135000m, 111000m, "c1", true, 130000m, 7m, true, 130000m),
            new("p2", "Шапка вязаная", 600m, null, null, null, null, null, false, 583m),
        ];
        public List<(string Id, BotBargainRule Rule)> Saved = [];
        public List<string> Resets = [];
        public List<(string Id, decimal? Offer, bool Reset)> Tests = [];
        public Exception? SaveError;

        public Task<BotBargainProductsPage> GetBargainProductsAsync(string? search, string? categoryId, bool onlyRules, int page, CancellationToken ct = default) =>
            Task.FromResult(new BotBargainProductsPage(Products.Count, false, Products));

        public Task<BotBargainProduct?> SaveBargainProductAsync(string productId, BotBargainRule rule, CancellationToken ct = default)
        {
            if (SaveError is not null)
                throw SaveError;
            Saved.Add((productId, rule));
            return Task.FromResult<BotBargainProduct?>(Products[0]);
        }

        public Task ResetBargainProductAsync(string productId, CancellationToken ct = default)
        {
            Resets.Add(productId);
            return Task.CompletedTask;
        }

        public Task<BotBargainTest> TestBargainAsync(string productId, decimal? offer, bool reset, CancellationToken ct = default)
        {
            Tests.Add((productId, offer, reset));
            return Task.FromResult(new BotBargainTest("counter", 130500m, 135000m, "Могу предложить «Куртка» за 130 500 сом вместо 135 000 сом. Цена держится 30 минут.", true, 126000m));
        }

        public Task<BotBargainOffersPage> GetBargainOffersAsync(DateTime from, DateTime to, string? decision, int page, CancellationToken ct = default) =>
            Task.FromResult(new BotBargainOffersPage(2, false,
            [
                new BotBargainOffer(new DateTimeOffset(2026, 11, 26, 16, 6, 0, TimeSpan.FromHours(6)), "test:1", true, "Куртка зимняя, размер 52", 135000m, 99000m, "decline", 123000m, 3, 122100m, 20m, null),
                new BotBargainOffer(new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.FromHours(6)), "770013812", false, "Шапка вязаная", 600m, 560m, "accept", 560m, 2, 540m, 10m, "A-17"),
            ]));

        public Task<IReadOnlyList<BotCategory>> GetCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BotCategory>>([new BotCategory("c1", "Куртки"), new BotCategory("c2", "Шапки и шарфы")]);

        public BotSellerConfig? Seller = Config();
        public Dictionary<string, object?>? LastPatch;
        public List<string> Actions = [];
        public List<BotDialog> Dialogs =
        [
            Dialog("1", "Айбек", "needs_human", new BotHandoff("human", "waiting", "Доставляете в Ош?", "0555123456", "", "", null, null)),
            Dialog("2", "Мирлан", "human"),
            Dialog("3", "Айгуль", "bot"),
            Dialog("4", "Нурлан", "bot", new BotHandoff("human", "returned", "", "", "", "Nur", new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(6)), null)),
        ];
        public List<string> Replies = [];
        public Exception? ReplyError;

        public Task<BotSellerConfig?> GetSellerConfigAsync(CancellationToken ct = default) => Task.FromResult(Seller);

        public Task<BotSellerConfig?> PatchSellerConfigAsync(IReadOnlyDictionary<string, object?> part, CancellationToken ct = default)
        {
            LastPatch = new Dictionary<string, object?>(part);
            return Task.FromResult(Seller);
        }

        public Task<BotDialogsPage> GetDialogsAsync(string? status, DateTime from, DateTime to, int page, CancellationToken ct = default) =>
            Task.FromResult(new BotDialogsPage(Dialogs.Count, false, Dialogs));

        public Task<BotDialogDetail?> GetDialogAsync(string chatId, CancellationToken ct = default) =>
            Task.FromResult<BotDialogDetail?>(new BotDialogDetail(Dialogs[0],
                [new BotDialogMessage(new DateTimeOffset(2026, 11, 26, 9, 58, 0, TimeSpan.FromHours(6)), "Доставляете в Ош?", "К сожалению, не знаю — позову продавца.", false, null),
                    new BotDialogMessage(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.FromHours(6)), "Хочу поговорить с менеджером", "", true, null)],
                [new BotDialogOrder("A-17", 1234.5m, "new", null)]));

        public Task<BotDialogAction> DialogActionAsync(string chatId, string action, CancellationToken ct = default)
        {
            Actions.Add(action + ":" + chatId);
            return Task.FromResult(new BotDialogAction(action == "take" ? "human" : "bot", true));
        }

        public Task<BotDialogReply> ReplyToDialogAsync(string chatId, string text, CancellationToken ct = default)
        {
            if (ReplyError is not null)
                throw ReplyError;
            Replies.Add(chatId + ":" + text);
            return Task.FromResult(new BotDialogReply("human", true));
        }

        public Task<BotSalesStats> GetSalesStatsAsync(DateTime from, DateTime to, CancellationToken ct = default) =>
            Task.FromResult(new BotSalesStats(125, 640, 98, 14, 253400.5m, 9, 181250m, 2, 18100m, 11.2, 21, 21, new Dictionary<string, int> { ["human"] = 12, ["unknown"] = 9 }, 4, 17, 1, 0, 95, 70,
                [new BotUnknownQuestion(new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.FromHours(6)), "7", "Доставляете в Ош?", "Бакыт", null), new BotUnknownQuestion(null, "8", "Есть ли гарантия на утюг?")],
                new BotBargainStats(14, 40, 18, 9, 18250.5m, 4.6, 22.5, [new BotBargainTop("Куртка зимняя", 12, 4, "p1"), new BotBargainTop("Шапка вязаная", 5, 1, "p2")])));

        public Task<BotTestAnswer> TestAsCustomerAsync(string question, CancellationToken ct = default) =>
            Task.FromResult(new BotTestAnswer("Здравствуйте! Яблоки Жашыл — 52 сом, в наличии. Оформить заказ?", "gemini-3.5-flash-lite", "human",
                new BotOrderDraft("Айбек", "0555123456", [new BotOrderDraftItem("Куртка зимняя", "v1", "52", "чёрный", "2"), new BotOrderDraftItem("Шапка", "", "", "", "1")], "Позвоните перед доставкой"), 3));
    }

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    public static IEnumerable<object[]> Matrix() =>
        new[] { "settings", "settings-per-day", "check", "dialogs", "dialogs-transcript", "dialogs-reply", "stats" }
            .SelectMany(name => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(lang => new object[] { name, lang }));

    private static Window Create(string name, FakeApi api) => name switch
    {
        "settings" => new BotSellerSettingsWindow(api),
        "settings-per-day" => new BotSellerSettingsWindow(new FakeApi { Seller = Config(new BotSellerHours(false, true, "", "")) }),
        "check" => new BotCheckWindow(api),
        "dialogs" or "dialogs-transcript" or "dialogs-reply" => new BotDialogsWindow(api),
        "stats" => new BotSalesStatsWindow(api),
        _ => throw new InvalidOperationException(name),
    };

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Window_FitsWithoutOverlapOrClipping(string name, AppLanguage lang)
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, lang);
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.BotSeller, ServerCapabilities.BotHandoff, ServerCapabilities.BotSalesStats, ServerCapabilities.BotBargain);
        var found = new List<string>();
        try
        {
            foreach (var size in Sizes)
            {
                var window = Create(name, new FakeApi());
                try
                {
                    using (UiShot.Render(window, size)) { }
                    UiShot.Settle(4);
                    if (name == "check")
                    {
                        UiDrive.Type(window, window.GetVisualDescendants().OfType<TextBox>().Single(), "Сколько стоят яблоки?");
                        UiDrive.Click(window, UiDrive.ButtonByText(window, "Спросить", "Суроо", "So'rash"));
                        UiShot.Settle(4);
                        UiDrive.AllText(window).Should().Contain("Яблоки Жашыл", "ответ бота показан");
                    }
                    if (name == "dialogs-transcript")
                    {
                        UiDrive.Click(window, UiDrive.ButtonByText(window, "Переписка", "Кат алышуу", "Yozishma"));
                        UiShot.Settle(4);
                        UiDrive.AllText(window).Should().Contain("A-17", "переписка раскрыта, заказ диалога виден");
                    }
                    if (name == "dialogs-reply")
                    {
                        UiDrive.Click(window, UiDrive.ButtonByText(window, "Ответить", "Жооп берүү", "Javob berish"));
                        UiShot.Settle(4);
                        UiDrive.AllText(window).Should().Contain("0 / 4000", "поле ответа покупателю раскрыто, счётчик виден");
                    }
                    var scope = $"bot-{name}|{size.Width}x{size.Height}";
                    found.AddRange(FormFieldsOverlapUiTests.FindViolations(window, scope).Select(v => v.Detail));
                    found.AddRange(TextClipChecker.Find(window, scope, Code(lang)).Select(v => v.Detail));
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
        found.Should().BeEmpty("поля и подписи не налезают, текст не обрезан (" + name + ", " + Code(lang) + "):\n" + string.Join("\n", found.Take(20)));
    }

    private static TextBox BoxNamed(Window w, string name) =>
        w.GetVisualDescendants().OfType<TextBox>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);

    [AvaloniaFact]
    public void Settings_Save_SendsOnlyChangedFields_AndRejectsWrongHours()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeApi();
        var window = new BotSellerSettingsWindow(api);
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("Возврат в течение 14 дней", "значения с сервера показаны");

            UiDrive.Type(window, BoxNamed(window, "Часы продавца: с"), "99:99");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить"));
            UiShot.Settle(4);
            api.LastPatch.Should().BeNull("неверные часы на сервер не уходят");
            UiDrive.AllText(window).Should().Contain("ЧЧ:ММ");

            UiDrive.Type(window, BoxNamed(window, "Часы продавца: с"), "09:00");
            UiDrive.Type(window, BoxNamed(window, "Подпись в первом ответе"), "Магазин «Нур»");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить"));
            UiShot.Settle(4);
            api.LastPatch.Should().NotBeNull();
            api.LastPatch!.Keys.Should().Equal("signature");
            api.LastPatch["signature"].Should().Be("Магазин «Нур»");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Settings_OldServer_ShowsMessage_AndCannotSave()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new BotSellerSettingsWindow(new FakeApi { Seller = null });
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("пока не отдаёт");
            UiDrive.ButtonByText(window, "Сохранить").IsEnabled.Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Check_ShowsSellerHandoffAndOrderNotes()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new BotCheckWindow(new FakeApi());
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            UiDrive.Type(window, window.GetVisualDescendants().OfType<TextBox>().Single(), "Сколько стоят яблоки?");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Спросить"));
            UiShot.Settle(4);
            var text = UiDrive.AllText(window);
            text.Should().Contain("Яблоки Жашыл").And.Contain("Бот позвал бы продавца").And.Contain("попросил продавца").And.Contain("Бот оформил бы заказ").And.Contain("Найдено товаров в каталоге: 3")
                .And.Contain("Имя: Айбек").And.Contain("Телефон: 0555123456").And.Contain("• Куртка зимняя (размер 52, цвет чёрный) × 2").And.Contain("• Шапка × 1").And.Contain("Комментарий: Позвоните перед доставкой");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Dialogs_TakeAndReturn_SendActions_AndButtonsDependOnStatus()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeApi();
        var window = new BotDialogsWindow(api);
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => UiDrive.LabelOf(b)).ToList();
            buttons.Count(t => t == "Взять").Should().Be(3, "«Взять» — у диалогов бота и ждущих продавца, не у того, что уже у продавца");
            UiDrive.AllText(window).Should().Contain("Диалог возвращён боту", "закрытая заявка подписана");
            buttons.Count(t => t == "Вернуть боту").Should().Be(2, "«Вернуть боту» — у диалогов ждущих и у продавца");
            UiDrive.AllText(window).Should().Contain("Ждёт продавца").And.Contain("0555123456");

            UiDrive.Click(window, UiDrive.ButtonByText(window, "Взять"));
            UiShot.Settle(4);
            api.Actions.Should().Equal("take:1");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Вернуть боту"));
            UiShot.Settle(4);
            api.Actions.Should().Equal("take:1", "return:1");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Stats_ShowsMoneyWithCommonFormatter_AndUnknownQuestions()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.BotSalesStats);
        var window = new BotSalesStatsWindow(new FakeApi());
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            var text = UiDrive.AllText(window);
            text.Should().Contain(MoneyText.Som(253400.5m)).And.Contain(MoneyText.Som(181250m)).And.Contain("11,2 %").And.Contain("1 мин 35 с");
            text.Should().Contain("Доставляете в Ош?").And.Contain("Есть ли гарантия на утюг?").And.Contain("Правила магазина").And.Contain("Бакыт · Доставляете в Ош?");
            text.Should().NotContain("Открыть «Бот-продавец»", "без признака bot_seller перехода в настройки нет");
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Dialogs_Reply_SendsText_AndShowsTelegramError()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeApi();
        var window = new BotDialogsWindow(api);
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Ответить"));
            var box = BoxNamed(window, "Ответ покупателю");
            UiDrive.Type(window, box, "Доставим завтра");
            UiDrive.AllText(window).Should().Contain("15 / 4000");
            box.MaxLength.Should().Be(4000);

            api.ReplyError = new ApiException("Telegram не доставил", 502, System.Text.Json.JsonDocument.Parse("{\"code\":\"telegram_error\",\"detail\":\"chat not found\"}").RootElement.Clone());
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Отправить"));
            UiShot.Settle(4);
            api.Replies.Should().BeEmpty();
            UiDrive.AllText(window).Should().Contain("Ошибка Telegram: chat not found");
            box.Text.Should().Be("Доставим завтра", "текст остаётся для повторной отправки");

            api.ReplyError = null;
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Отправить"));
            UiShot.Settle(4);
            api.Replies.Should().Equal("1:Доставим завтра");
            UiDrive.AllText(window).Should().Contain("Отправлено покупателю");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-26 (стресс-тест): пустой ответ отправить нельзя; таймаут — исход неизвестен, тот же текст повторно уходит только вторым нажатием (покупатель не получит его дважды по ошибке).</summary>
    [AvaloniaFact]
    public void Dialogs_Reply_EmptyDisabled_TimeoutNeedsSecondPressForSameText()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new FakeApi();
        var window = new BotDialogsWindow(api);
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Ответить"));
            var box = BoxNamed(window, "Ответ покупателю");
            var send = UiDrive.ButtonByText(window, "Отправить");
            send.IsEnabled.Should().BeFalse("пустой ответ отправить нельзя");
            UiDrive.Type(window, box, "   ");
            send.IsEnabled.Should().BeFalse("одни пробелы — тоже пусто");
            UiDrive.Type(window, box, "Привет 👋 доставим завтра");
            send.IsEnabled.Should().BeTrue();

            api.ReplyError = new TaskCanceledException("timeout");
            UiDrive.Click(window, send);
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("Неизвестно, дошло ли сообщение");

            api.ReplyError = null;
            UiDrive.Click(window, send);
            UiShot.Settle(4);
            api.Replies.Should().BeEmpty("тот же текст после неизвестного исхода — сначала предупреждение");
            UiDrive.AllText(window).Should().Contain("мог уже дойти до покупателя");

            UiDrive.Click(window, send);
            UiShot.Settle(4);
            api.Replies.Should().Equal("1:Привет 👋 доставим завтра");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void BotSection_ShowsSellerButtonsOnlyWithServerFeatures(bool seller, bool handoff, bool stats)
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var features = new List<string> { "unrelated_feature" };
        if (seller) features.Add(ServerCapabilities.BotSeller);
        if (handoff) features.Add(ServerCapabilities.BotHandoff);
        if (stats) features.Add(ServerCapabilities.BotSalesStats);
        ServerCapabilities.SetFeaturesForTests(features.ToArray());
        var window = new TelegramBotAnalyticsWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            UiShot.Settle(4);
            bool Shown(string text) => window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && UiDrive.LabelOf(b) == text);
            Shown("Бот-продавец").Should().Be(seller);
            Shown("Диалоги бота").Should().Be(handoff);
            Shown("Продажи бота").Should().Be(stats);
            var found = FormFieldsOverlapUiTests.FindViolations(window, "telegrambot-with-seller").Select(v => v.Detail)
                .Concat(TextClipChecker.Find(window, "telegrambot-with-seller", "ru").Select(v => v.Detail)).ToList();
            found.Should().BeEmpty(string.Join("\n", found.Take(10)));
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
            window.Close();
            UiShot.Pump();
        }
    }
}
