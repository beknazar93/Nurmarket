using Avalonia;
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

/// <summary>2026-11-20 (ответ Умара 20.11, В.4, этап 1): «Заказы под клиента» с серверным заказом (customer-orders/): в карточке «Выдать — наличные / карта» и «Отменить заказ» идут через сервер, местные кнопки «Отметить», «Оформить продажу»,
/// «Изменить» скрыты (иначе 409 reserved_stock); выдача шлёт POST deliver с суммой к доплате, отмена с предоплатой спрашивает PIN и сообщает о возврате из кассы смены. Чек-лист окон: кнопки действий не налезают друг на друга и на края,
/// текст не обрезан (1024×600 и 1366×768, ru/ky/uz). Сервер подменён (<see cref="FakeOrders"/>), диалоги подтверждения — через Deps.Confirm. Снимки: %TEMP%\nurmarket-tests\ui-actual\partsorders-server-*.png.</summary>
[Collection("UiScenarios")]
public sealed class PartsOrdersServerUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "partsorders-server-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public PartsOrdersServerUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ModalShell.WorkingAreaForTests = null;
        AutoPartsRules.ActiveOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private sealed class FakeOrders : ICustomerOrdersApi
    {
        public List<CustomerOrderDto> Open { get; } = new();
        public List<(string Id, string Method, decimal? Received)> Delivered { get; } = new();
        public List<string> Cancelled { get; } = new();
        public Exception? DeliverError { get; set; }
        public int Lists { get; private set; }

        public Task<IReadOnlyList<CustomerOrderDto>> ListOpenAsync(CancellationToken ct = default)
        {
            Lists++;
            return Task.FromResult<IReadOnlyList<CustomerOrderDto>>(Open.ToList());
        }

        public Task<CustomerOrderDto?> GetAsync(string orderId, CancellationToken ct = default) => Task.FromResult<CustomerOrderDto?>(null);

        public Task<CustomerOrderDeliverResult> DeliverAsync(string orderId, string method, decimal? received, CancellationToken ct = default)
        {
            if (DeliverError is not null)
                throw DeliverError;
            Delivered.Add((orderId, method, received));
            var order = new CustomerOrderDto { Id = orderId, Number = "CO-000007", Status = "delivered", CustomerName = "Айбек", SaleId = "sale-1", Total = 1900m, LeftToPay = 0m, PrepaymentAmount = 500m };
            return Task.FromResult(new CustomerOrderDeliverResult("sale-1", 100m, order));
        }

        public Task<CustomerOrderDto?> CancelAsync(string orderId, CancellationToken ct = default)
        {
            Cancelled.Add(orderId);
            return Task.FromResult<CustomerOrderDto?>(new CustomerOrderDto { Id = orderId, Number = "CO-000007", Status = "canceled", CustomerName = "Айбек", PrepaymentAmount = 500m, PrepaymentRefunded = true });
        }
    }

    private static CustomerOrderDto ServerOrder(string status = "ready") => new()
    {
        Id = "9f2c", Number = "CO-000007", Status = status, CustomerName = "СТО «Авто-Мастер» Бишкек", CustomerPhone = "+996700111222", DueDate = new DateTime(2026, 11, 25), Note = "к пятнице",
        Items = { new CustomerOrderItem("p-1", "Фильтр масляный Toyota 90915-YZZJ2", 2m, 500m, 1000m, "reserved"), new CustomerOrderItem("p-2", "Колодки передние", 1m, 900m, 900m, "ready") },
        PrepaymentAmount = 500m, Total = 1900m, LeftToPay = 1400m, Version = "3",
    };

    private PartsOrderDeps Deps(FakeOrders server, out List<(string Text, bool Danger)> asked, bool answer = true, bool pin = true)
    {
        var list = new List<(string, bool)>();
        asked = list;
        return new PartsOrderDeps
        {
            Store = new PartsOrderStore(Path.Combine(_dir, "orders-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 20, 10, 0, 0)),
            SellAsync = _ => Task.FromResult(2),
            AskPin = () => Task.FromResult(pin),
            OpenUrl = _ => true,
            ShopName = () => "Автодом",
            StartTariff = () => false,
            ServerOrders = () => server,
            Confirm = (_, text, _, _, _, danger) => { list.Add((text, danger)); return Task.FromResult(answer); },
        };
    }

    private static T Named<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static bool Has(Window w, string name) => w.GetVisualDescendants().OfType<Button>().Any(b => b.Name == name);

    private static void Click(Window w, string name) => Named<Button>(w, name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static string Code(AppLanguage l) => l switch
    {
        AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.Turkish => "tr", AppLanguage.English => "en", _ => "ru",
    };

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            UiShot.Settle(2);
            await Task.Delay(20);
        }
    }

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
    }

    [AvaloniaFact]
    public async Task Server_order_card_has_server_actions_only_and_deliver_posts_the_balance()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var server = new FakeOrders();
        server.Open.Add(ServerOrder());
        var deps = Deps(server, out var asked);
        var window = new PartsOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            await WaitAsync(() => deps.Store.All().Count == 1);
            server.Lists.Should().BeGreaterThan(0, "при открытии окно подтянуло заказы с сервера");
            window.Select(deps.Store.All().Single().Id);
            UiShot.Settle(6);

            Has(window, "PartsOrderDeliverCash").Should().BeTrue();
            Has(window, "PartsOrderDeliverCard").Should().BeTrue();
            Has(window, "PartsOrderCancel").Should().BeTrue();
            Has(window, "PartsOrderRefreshServer").Should().BeTrue("кнопка «Обновить с сервера» видна при признаке customer_orders");
            foreach (var local in new[] { "PartsOrderAdvance", "PartsOrderSell", "PartsOrderEdit" })
                Has(window, local).Should().BeFalse($"{local}: серверным заказом управляет сервер (иначе 409 reserved_stock)");

            Click(window, "PartsOrderDeliverCash");
            await WaitAsync(() => server.Delivered.Count == 1);

            server.Delivered.Should().ContainSingle().Which.Should().Be(("9f2c", "cash", 1400m), "к оплате — «к доплате» сервера, а не полная сумма");
            asked.Should().ContainSingle().Which.Text.Should().Contain("№7");
            deps.Store.All().Single().Status.Should().Be(PartsOrderStatus.Issued);
            window.StatusMessage.Should().Contain("выдан").And.Contain("Сдача");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Deliver_error_from_the_server_is_shown_in_words_and_the_order_stays_open()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var server = new FakeOrders { DeliverError = new ApiException("srv", 409, System.Text.Json.JsonDocument.Parse("""{"code":"shift_not_open"}""").RootElement.Clone()) };
        server.Open.Add(ServerOrder());
        var deps = Deps(server, out _);
        var window = new PartsOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            await WaitAsync(() => deps.Store.All().Count == 1);
            window.Select(deps.Store.All().Single().Id);
            UiShot.Settle(6);

            Click(window, "PartsOrderDeliverCard");
            await WaitAsync(() => window.StatusMessage.Contains("смену"));

            window.StatusMessage.Should().Contain("Откройте смену");
            deps.Store.All().Single().Status.Should().Be(PartsOrderStatus.Arrived, "заказ не выдан");
            server.Delivered.Should().BeEmpty();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Cancel_with_prepayment_asks_the_pin_first_then_cancels_on_the_server_and_says_where_the_money_came_from()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var server = new FakeOrders();
        server.Open.Add(ServerOrder("reserved"));
        var denied = Deps(server, out _, pin: false);
        var window = new PartsOrdersWindow(denied);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            await WaitAsync(() => denied.Store.All().Count == 1);
            window.Select(denied.Store.All().Single().Id);
            UiShot.Settle(6);

            Click(window, "PartsOrderCancel");
            await WaitAsync(() => window.StatusMessage.Contains("PIN"));
            window.StatusMessage.Should().Contain("PIN не принят");
            server.Cancelled.Should().BeEmpty("без PIN руководителя сервер не вызывается");
        }
        finally
        {
            window.Close();
        }

        var ok = Deps(server, out _);
        var window2 = new PartsOrdersWindow(ok);
        try
        {
            using var _ = UiShot.Render(window2, new UiSize(1024, 600));
            await WaitAsync(() => ok.Store.All().Count == 1);
            window2.Select(ok.Store.All().Single().Id);
            UiShot.Settle(6);

            Click(window2, "PartsOrderCancel");
            await WaitAsync(() => server.Cancelled.Count == 1);

            server.Cancelled.Should().ContainSingle().Which.Should().Be("9f2c");
            ok.Store.All().Single().Status.Should().Be(PartsOrderStatus.Cancelled);
            window2.StatusMessage.Should().Contain("из кассы текущей смены");
        }
        finally
        {
            window2.Close();
        }
    }

    [AvaloniaFact]
    public async Task Without_the_server_flag_the_window_is_as_before()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var deps = new PartsOrderDeps
        {
            Store = new PartsOrderStore(Path.Combine(_dir, "orders-plain.json"), () => new DateTime(2026, 11, 20, 10, 0, 0)),
            SellAsync = _ => Task.FromResult(1),
            ServerOrders = () => null,
        };
        var local = new PartsOrder { ClientName = "Местный", Lines = { new PartsOrderLine { Title = "Свеча", Qty = 1, Price = 100 } } };
        deps.Store.Save(local);
        var window = new PartsOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            window.Select(local.Id);
            UiShot.Settle(6);

            Has(window, "PartsOrderRefreshServer").Should().BeFalse();
            Has(window, "PartsOrderDeliverCash").Should().BeFalse();
            foreach (var name in new[] { "PartsOrderAdvance", "PartsOrderSell", "PartsOrderEdit", "PartsOrderCancel" })
                Has(window, name).Should().BeTrue(name);
        }
        finally
        {
            window.Close();
        }
    }

    public static IEnumerable<object[]> Cases() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }
            .SelectMany(l => new[] { (1024, 600), (1366, 768) }.Select(s => new object[] { l, s.Item1, s.Item2 }));

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Server_card_text_is_not_clipped_and_the_action_buttons_do_not_overlap(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var server = new FakeOrders();
        server.Open.Add(ServerOrder());
        var deps = Deps(server, out _);
        var window = new PartsOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            await WaitAsync(() => deps.Store.All().Count == 1);
            window.Select(deps.Store.All().Single().Id);
            UiShot.Settle(6);

            TextClipChecker.Find(window, $"partsorders-server-{width}", Code(lang)).Should().BeEmpty($"карточка серверного заказа не обрезана ({Code(lang)} {width}x{height})");

            // Кнопки действий и «Обновить с сервера»: попарно не пересекаются и целиком внутри окна.
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Name is { Length: > 0 } n && n.StartsWith("PartsOrder", StringComparison.Ordinal) && !n.StartsWith("PartsOrderRow_", StringComparison.Ordinal)).ToList();
            buttons.Should().HaveCountGreaterThanOrEqualTo(5);
            var rects = buttons.Select(b => (b.Name, Rect: new Rect(b.TranslatePoint(default, window) ?? default, b.Bounds.Size))).ToList();
            for (var i = 0; i < rects.Count; i++)
            {
                rects[i].Rect.Width.Should().BeGreaterThan(48, rects[i].Name);
                new Rect(window.Bounds.Size).Inflate(1).Contains(rects[i].Rect).Should().BeTrue($"{rects[i].Name} внутри окна");
                for (var j = i + 1; j < rects.Count; j++)
                    rects[i].Rect.Deflate(1).Intersects(rects[j].Rect.Deflate(1)).Should().BeFalse($"{rects[i].Name} и {rects[j].Name} не пересекаются");
            }

            Save(window, $"partsorders-server-{Code(lang)}-{width}x{height}.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Plan_block_prompt_texts_use_the_server_detail_the_upgrade_name_and_exist_in_all_languages()
    {
        TestHost.Ensure();
        var block = new PlanBlock("plan_client_not_allowed", "mini", "desktop_pos", null, null, null, "start", "Тариф «Мини» работает только с телефона. Смените тариф на «Старт».");
        var oldLanguage = UserPreferences.Instance.Language;
        try
        {
            UserPreferences.Instance.Language = AppLanguage.Russian;
            PlanBlockPrompt.Message(block).Should().Contain("работает только с телефона").And.Contain("сохранены");
            PlanBlockPrompt.ConfirmText(block).Should().Be("Перейти на «Старт»");
            PlanBlockPrompt.Title(block).Should().Contain("Тариф");

            var noDetail = block with { Detail = null };
            PlanBlockPrompt.Message(noDetail).Should().Contain("«Мини»").And.Contain("«Старт»").And.Contain("только с телефона");
            PlanBlockPrompt.Message(new PlanBlock("plan_limit", "mini", null, "max_staff", 1, 1, "start", null)).Should().Contain("лимит");

            var texts = new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek }.Select(l =>
            {
                UserPreferences.Instance.Language = l;
                return PlanBlockPrompt.Message(noDetail) + "|" + PlanBlockPrompt.ConfirmText(noDetail) + "|" + PlanBlockPrompt.Title(noDetail);
            }).ToList();
            texts.Should().OnlyHaveUniqueItems();
        }
        finally
        {
            UserPreferences.Instance.Language = oldLanguage;
        }
    }
}
