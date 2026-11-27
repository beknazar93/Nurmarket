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

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 2: торг»): окна торга на заглушке сервера. (1) Раскладка на 1024×600 и 1366×768 в ru, ky, uz: «Бот-продавец» с блоком «Торг», «Правила торга по товарам» (в том числе с раскрытой правкой),
/// «Проверить торг» с ответом, «Журнал оферт», отчёт с блоком «Торг» — поля и подписи не налезают, текст не обрезан. (2) Правка и сброс правил, ошибка 400 по-русски, симулятор, блок «Торг» в настройках шлёт только изменённое,
/// кнопки видны только при признаке bot_bargain, строка «Цена по торгу» в заказе витрины.</summary>
[Collection("UiScenarios")]
public sealed class BotBargainUiTests
{
    private static readonly UiSize[] Sizes = [new(1024, 600), new(1366, 768)];

    private static string Code(AppLanguage lang) => lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };

    public static IEnumerable<object[]> Matrix() =>
        new[] { "settings-bargain", "products", "products-edit", "test", "offers", "stats-bargain" }
            .SelectMany(name => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(lang => new object[] { name, lang }));

    private static Window Create(string name, BotSellerUiTests.FakeApi api) => name switch
    {
        "settings-bargain" => new BotSellerSettingsWindow(api),
        "products" or "products-edit" => new BotBargainProductsWindow(api),
        "test" => new BotBargainTestWindow(api, "p1", "Куртка зимняя, размер 52", 135000m),
        "offers" => new BotBargainOffersWindow(api),
        "stats-bargain" => new BotSalesStatsWindow(api),
        _ => throw new InvalidOperationException(name),
    };

    private static TextBox BoxNamed(Window w, string name) =>
        w.GetVisualDescendants().OfType<TextBox>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public void Window_FitsWithoutOverlapOrClipping(string name, AppLanguage lang)
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, lang);
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.BotSeller, ServerCapabilities.BotSalesStats, ServerCapabilities.BotBargain);
        var found = new List<string>();
        try
        {
            foreach (var size in Sizes)
            {
                var window = Create(name, new BotSellerUiTests.FakeApi());
                try
                {
                    using (UiShot.Render(window, size)) { }
                    UiShot.Settle(4);
                    if (name == "products-edit")
                    {
                        UiDrive.Click(window, UiDrive.ButtonByText(window, "Изменить", "Өзгөртүү", "O'zgartirish"));
                        UiShot.Settle(4);
                        UiDrive.AllText(window).Should().Contain("130000", "правка раскрыта, минимальная цена товара подставлена");
                    }
                    if (name == "test")
                    {
                        UiDrive.Type(window, window.GetVisualDescendants().OfType<TextBox>().Single(), "100000");
                        UiDrive.Click(window, UiDrive.ButtonByText(window, "Предложить цену", "Баа сунуштоо", "Narx taklif qilish"));
                        UiShot.Settle(4);
                        UiDrive.AllText(window).Should().Contain("Могу предложить", "ответ симулятора показан");
                    }
                    var scope = $"bargain-{name}|{size.Width}x{size.Height}";
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

    [AvaloniaFact]
    public void Products_ShowMoneyFloorAndRules_EditSavesAndResetResets()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new BotSellerUiTests.FakeApi();
        var window = new BotBargainProductsWindow(api);
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            var text = UiDrive.AllText(window);
            text.Should().Contain(MoneyText.Som(135000m)).And.Contain(MoneyText.Som(111000m)).And.Contain("Пол цены: " + MoneyText.Som(130000m)).And.Contain("Торг разрешён").And.Contain("Макс. скидка: 7 %");
            text.Should().Contain("Закупка: —", "закупка не задана — прочерк, не «0 сом»");

            UiDrive.Click(window, UiDrive.ButtonByText(window, "Изменить"));
            UiDrive.Type(window, BoxNamed(window, "Минимальная цена для торга, сом"), "128000");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить"));
            UiShot.Settle(4);
            api.Saved.Should().ContainSingle().Which.Should().Be(("p1", new BotBargainRule(true, 128000m, 7m)));

            // 2026-11-26 (стресс-тест): сброс — только вторым нажатием («Да, сбросить»), первое лишь спрашивает.
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сбросить правила"));
            UiShot.Settle(4);
            api.Resets.Should().BeEmpty("первое нажатие только спрашивает подтверждение");
            UiDrive.AllText(window).Should().Contain("Да, сбросить");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Да, сбросить"));
            UiShot.Settle(4);
            api.Resets.Should().Equal("p1");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Products_Save400_ShowsRussianFieldNames_AndRejectsNonNumbers()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new BotSellerUiTests.FakeApi
        {
            SaveError = new ApiException("Ошибка", 400, System.Text.Json.JsonDocument.Parse("{\"detail\":\"0…50\",\"fields\":{\"max_discount_percent\":[\"0…50\"],\"min_price\":[\"Не выше цены товара.\"]}}").RootElement.Clone()),
        };
        var window = new BotBargainProductsWindow(api);
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Изменить"));
            UiDrive.Type(window, BoxNamed(window, "Минимальная цена для торга, сом"), "abc");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить"));
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("числа");

            // 2026-11-26 (стресс-тест): цена выше цены товара теперь отсекается до сервера — для проверки ответа 400 берём допустимую цену.
            UiDrive.Type(window, BoxNamed(window, "Минимальная цена для торга, сом"), "999999");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить"));
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("не может быть выше цены товара", "проверка до сервера");
            api.Saved.Should().BeEmpty("неверная цена на сервер не ушла");

            UiDrive.Type(window, BoxNamed(window, "Минимальная цена для торга, сом"), "120000");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить"));
            UiShot.Settle(4);
            var text = UiDrive.AllText(window);
            text.Should().Contain("Минимальная цена для торга: Не выше цены товара.").And.Contain("Макс. скидка, %: 0…50").And.NotContain("min_price");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void TestWindow_SendsOfferAndReset_ShowsDecisionFloorAndText()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var api = new BotSellerUiTests.FakeApi();
        var window = new BotBargainTestWindow(api, "p1", "Куртка", 135000m);
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            UiDrive.Type(window, window.GetVisualDescendants().OfType<TextBox>().Single(), "100000");
            UiDrive.Click(window, window.GetVisualDescendants().OfType<CheckBox>().Single());
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Предложить цену"));
            UiShot.Settle(4);
            api.Tests.Should().Equal(("p1", (decimal?)100000m, true));
            var text = UiDrive.AllText(window);
            text.Should().Contain("Встречная цена").And.Contain("Цена: " + MoneyText.Som(130500m)).And.Contain("пол цены: " + MoneyText.Som(126000m)).And.Contain("Беру");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Предложить цену"));
            UiShot.Settle(4);
            api.Tests.Last().Reset.Should().BeFalse("«заново» сбрасывается после первого предложения — следующая ступень");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Offers_ShowEveryFieldOfTheDecision()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new BotBargainOffersWindow(new BotSellerUiTests.FakeApi());
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            var text = UiDrive.AllText(window);
            text.Should().Contain("Лучшая цена").And.Contain("Договорились").And.Contain("· тест").And.Contain("В списке: " + MoneyText.Som(135000m)).And.Contain("Предложение: " + MoneyText.Som(99000m))
                .And.Contain("Ступень: 3").And.Contain("Пол: " + MoneyText.Som(122100m)).And.Contain("Лимит скидки: 20 %").And.Contain("Заказ №A-17");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Stats_BargainBlock_ShowsNumbersWithCommonFormatters()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.BotSalesStats, ServerCapabilities.BotBargain);
        var window = new BotSalesStatsWindow(new BotSellerUiTests.FakeApi());
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            var text = UiDrive.AllText(window);
            text.Should().Contain("предложений бота: 40").And.Contain("приняли покупатели: 18").And.Contain("заказов по торгу: 9").And.Contain(MoneyText.Som(18250.5m))
                .And.Contain("средняя скидка: 4,6 %").And.Contain("22,5 % предложений").And.Contain("• Куртка зимняя — предложили торг 12 раз · заказов 4");
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Stats_BargainBlock_HiddenWithoutFeature()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.BotSalesStats);
        var window = new BotSalesStatsWindow(new BotSellerUiTests.FakeApi());
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().NotContain("предложений бота");
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Settings_BargainBlock_SendsOnlyChangedBargainFields_AndChecksRanges()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.BotSeller, ServerCapabilities.BotBargain);
        var api = new BotSellerUiTests.FakeApi();
        var window = new BotSellerSettingsWindow(api);
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("Торг включён").And.Contain("Куртки");

            UiDrive.Type(window, BoxNamed(window, "Лимит скидки, % (0–50)"), "70");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить"));
            UiShot.Settle(4);
            api.LastPatch.Should().BeNull("лимит скидки вне 0–50 на сервер не уходит");
            UiDrive.AllText(window).Should().Contain("от 0 до 50");

            UiDrive.Type(window, BoxNamed(window, "Лимит скидки, % (0–50)"), "8");
            window.GetVisualDescendants().OfType<CheckBox>().First(c => (c.Content as string) == "Шапки и шарфы").IsChecked = true;
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Сохранить"));
            UiShot.Settle(4);
            api.LastPatch.Should().NotBeNull();
            api.LastPatch!.Keys.Should().Equal("bargain");
            var bargain = (Dictionary<string, object?>)api.LastPatch["bargain"]!;
            bargain.Keys.Should().BeEquivalentTo("max_discount_percent", "allowed_categories");
            bargain["max_discount_percent"].Should().Be(8m);
            ((List<string>)bargain["allowed_categories"]!).Should().BeEquivalentTo("c1", "c2");
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Settings_BargainBlock_HiddenWithoutFeature()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.BotSeller);
        var window = new BotSellerSettingsWindow(new BotSellerUiTests.FakeApi());
        try
        {
            using (UiShot.Render(window, Sizes[0])) { }
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().NotContain("Торг включён");
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Check_ShowsBargainReplyAndOffer()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new BotCheckWindow(new CheckApi());
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            UiDrive.Type(window, window.GetVisualDescendants().OfType<TextBox>().Single(), "Отдадите за 100?");
            UiDrive.Click(window, UiDrive.ButtonByText(window, "Спросить"));
            UiShot.Settle(4);
            UiDrive.AllText(window).Should().Contain("Торг (предложение " + MoneyText.Som(100m) + "): На «123» цена фиксированная: 123 сом.");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Заглушка только для «Проверить бота»: ответ с полем bargain.</summary>
    private sealed class CheckApi : IBotSellerApi
    {
        public Task<BotSellerConfig?> GetSellerConfigAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BotSellerConfig?> PatchSellerConfigAsync(IReadOnlyDictionary<string, object?> sellerConfigPart, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BotDialogsPage> GetDialogsAsync(string? status, DateTime from, DateTime to, int page, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BotDialogDetail?> GetDialogAsync(string chatId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BotDialogAction> DialogActionAsync(string chatId, string action, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BotDialogReply> ReplyToDialogAsync(string chatId, string text, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BotSalesStats> GetSalesStatsAsync(DateTime from, DateTime to, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<BotTestAnswer> TestAsCustomerAsync(string question, CancellationToken ct = default) =>
            Task.FromResult(new BotTestAnswer("Здравствуйте!", "m", null, null, 1, new BotBargainNote("На «123» цена фиксированная: 123 сом.", 100m)));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BotSection_ShowsBargainButtonOnlyWithFeature(bool bargain)
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ServerCapabilities.SetFeaturesForTests(bargain ? [ServerCapabilities.BotBargain, ServerCapabilities.BotSeller] : [ServerCapabilities.BotSeller]);
        var window = new TelegramBotAnalyticsWindow();
        try
        {
            using (UiShot.Render(window, new UiSize(1024, 600))) { }
            UiShot.Settle(4);
            window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && UiDrive.LabelOf(b) == "Торг по товарам").Should().Be(bargain);
            var found = FormFieldsOverlapUiTests.FindViolations(window, "telegrambot-bargain").Select(v => v.Detail)
                .Concat(TextClipChecker.Find(window, "telegrambot-bargain", "ru").Select(v => v.Detail)).ToList();
            found.Should().BeEmpty(string.Join("\n", found.Take(10)));
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void SiteOrder_BargainLine_UsesAgreedListAndDiscount_OnlyForBargainedItems()
    {
        TestHost.Ensure();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var bargained = new SiteOrderItem("p1", "Куртка", 1, 577500, 548626, Bargain: new SiteOrderBargain(577500, 548626, 5));
        SiteOrdersWindow.BargainNote(bargained).Should().StartWith("Цена по торгу: ").And.Contain("548").And.Contain("(в списке ").And.Contain("577").And.EndWith("скидка 5 %)");
        SiteOrdersWindow.BargainNote(new SiteOrderItem("p2", "Шапка", 1, 100, 100)).Should().BeNull();
    }
}
