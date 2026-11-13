using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (владелец: «Возврат чеков добавь и программу владельца»): возврат чека В ПРОГРАММЕ ВЛАДЕЛЬЦА (AppMode.IsOwner, без кассовой смены) —
/// настоящие окна в Avalonia.Headless против поддельного NurCRM (WireMock). Карточка чека «Продаж» владельца: кнопки «Возврат» и «Правка возвратов»;
/// окно возврата из неё: частичный возврат дробного количества (1,25 кг), двойное нажатие — один запрос с Idempotency-Key; полный возврат чека в долг;
/// ошибки сервера понятным текстом (в т.ч. sale_item_id); нет права «Возврат»; чек вне срока возврата (сфера «Одежда»); правка/удаление возврата ≤ 24 ч
/// из списка «Возвраты по чеку» (PIN руководителя, If-Match, X-Confirm-Token); фильтр периода «Неделя»/«Месяц» во «Возвратах» и в окне возврата.
/// Модальные окна (подтверждение, причина, итог, PIN) отвечает «автопилот» — таймер внутри цикла диспетчера Headless.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\owner-return-{окно}-{язык}-{ширина}x{высота}.png.</summary>
[Collection("UiScenarios")]
public sealed class OwnerReceiptReturnUiTests : IDisposable
{
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly string? _savedShift = PosApp.ActiveShiftId;
    private readonly bool _wasOwner = AppMode.IsOwner;
    private readonly string _savedSphere = UserPreferences.Instance.MarketSphere;
    private readonly int _savedExchangeDays = UserPreferences.Instance.ExchangeDaysLimit;

    public OwnerReceiptReturnUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        PosApp.ActiveShiftId = null; // программа владельца кассовую смену не открывает
        IsOwnerProp.SetValue(null, true);
    }

    public void Dispose()
    {
        IsOwnerProp.SetValue(null, _wasOwner);
        PosApp.ActiveShiftId = _savedShift;
        UserPreferences.Instance.MarketSphere = _savedSphere;
        UserPreferences.Instance.ExchangeDaysLimit = _savedExchangeDays;
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static string Slug(AppLanguage lang, int w, int h) => $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{w}x{h}";

    // ───────────── заглушки сервера ─────────────

    private static string At(int daysAgo) => DateTimeOffset.Now.AddDays(-daysAgo).AddMinutes(-5).ToString("O");

    private static string Row(string id, int number, string total, string method, string status, string returnStatus, int daysAgo = 0) =>
        "{\"id\":\"" + id + "\",\"number\":" + number + ",\"status\":\"" + status + "\",\"return_status\":\"" + returnStatus + "\",\"total\":\"" + total
        + "\",\"payment_method\":\"" + method + "\",\"created_at\":\"" + At(daysAgo) + "\",\"first_item_name\":\"Сахар\",\"user_display\":\"Айбек\"}";

    /// <summary>s-701 — наличные 340 (сахар 2,5 кг × 100 и хлеб 2 × 45); s-702 — в долг 500 (клиент c-1); s-703 — возвращён целиком; s-704 — 30 дней назад.</summary>
    private static void StubSales()
    {
        var rows = new[]
        {
            Row("s-701", 3701, "340.00", "cash", "paid", "none"),
            Row("s-702", 3702, "500.00", "debt", "debt", "none"),
            Row("s-703", 3703, "120.00", "cash", "canceled", "full"),
            Row("s-704", 3704, "90.00", "cash", "paid", "none", daysAgo: 30),
        };
        UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":4,\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", rows) + "]}");
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-701/", 200, "{\"id\":\"s-701\",\"number\":3701,\"status\":\"paid\",\"return_status\":\"none\",\"payment_method\":\"cash\",\"total\":\"340.00\",\"created_at\":\"" + At(0) + "\",\"items\":["
            + "{\"id\":\"si-71\",\"product_id\":\"p-71\",\"name\":\"Сахар\",\"quantity\":\"2.5\",\"unit_price\":\"100\",\"total\":\"250\",\"returnable_qty\":2.5},"
            + "{\"id\":\"si-72\",\"product_id\":\"p-72\",\"name\":\"Хлеб\",\"quantity\":\"2\",\"unit_price\":\"45\",\"total\":\"90\",\"returnable_qty\":2}]}");
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-702/", 200, "{\"id\":\"s-702\",\"number\":3702,\"status\":\"debt\",\"return_status\":\"none\",\"payment_method\":\"debt\",\"total\":\"500.00\",\"client\":\"c-1\",\"created_at\":\"" + At(0) + "\",\"items\":["
            + "{\"id\":\"si-81\",\"product_id\":\"p-81\",\"name\":\"Масло моторное\",\"quantity\":\"1\",\"unit_price\":\"500\",\"total\":\"500\",\"returnable_qty\":1}]}");
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-703/", 200, "{\"id\":\"s-703\",\"number\":3703,\"status\":\"canceled\",\"return_status\":\"full\",\"payment_method\":\"cash\",\"total\":\"120.00\",\"created_at\":\"" + At(0) + "\",\"items\":["
            + "{\"id\":\"si-91\",\"product_id\":\"p-91\",\"name\":\"Лампа\",\"quantity\":\"1\",\"unit_price\":\"120\",\"total\":\"120\",\"returnable_qty\":0,\"returned_qty\":1}]}");
        UiFake.Server.Json("GET", "/api/main/pos/sales/s-704/", 200, "{\"id\":\"s-704\",\"number\":3704,\"status\":\"paid\",\"return_status\":\"none\",\"payment_method\":\"cash\",\"total\":\"90.00\",\"created_at\":\"" + At(30) + "\",\"items\":["
            + "{\"id\":\"si-95\",\"product_id\":\"p-95\",\"name\":\"Футболка\",\"quantity\":\"1\",\"unit_price\":\"90\",\"total\":\"90\",\"returnable_qty\":1}]}");
        UiFake.Server.Json("GET", "/api/main/pos/returns/", 200, "[]");
    }

    private static async Task<NurMarketApiClient> LoginAsync()
    {
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("owner@test.kg", "12345678");
        return api;
    }

    // ───────────── цикл диспетчера и «автопилот» модальных окон ─────────────

    /// <summary>Цикл диспетчера, пока условие не выполнится: в Headless таймеры (и автопилот) срабатывают только внутри цикла.</summary>
    private static bool RunUntil(Func<bool> condition, int ms = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource();
            using var stop = DispatcherTimer.RunOnce(() => cts.Cancel(), TimeSpan.FromMilliseconds(80));
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
        return condition();
    }

    private static IEnumerable<Window> Walk(IEnumerable<Window> roots)
    {
        foreach (var w in roots)
        {
            yield return w;
            foreach (var child in Walk(w.OwnedWindows))
                yield return child;
        }
    }

    private static void Press(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Ключ повтора возврата: сервер берёт его из тела (idempotency_key), заголовок — если есть.</summary>
    private static string? ReturnKey(WireMock.Logging.ILogEntry post) =>
        Header(post, "Idempotency-Key") ?? BodyOf(post)["idempotency_key"]?.GetValue<string>();

    /// <summary>Отвечает на модальные окна возврата, как владелец: подтверждения — «да» (или по <see cref="Answer"/>), причина — текст, итог — «Понятно»,
    /// PIN руководителя — принят сервером (токен), причина удаления — текст. Тексты всех окон — в <see cref="Questions"/> и <see cref="Alerts"/>.</summary>
    private sealed class Autopilot : IDisposable
    {
        private readonly Window[] _roots;
        private readonly HashSet<Window> _handled = new();
        private readonly DispatcherTimer _timer;
        public List<string> Questions { get; } = new();
        public List<string> Alerts { get; } = new();
        public Func<string, bool> Answer { get; set; } = _ => true;
        public bool PinAccepted { get; set; } = true;

        public Autopilot(params Window[] roots)
        {
            _roots = roots;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => Tick());
            _timer.Start();
        }

        private void Tick()
        {
            foreach (var w in Walk(_roots).ToList())
            {
                if (!w.IsVisible || _handled.Contains(w))
                    continue;
                switch (w)
                {
                    case PosConfirmDialog confirm:
                        _handled.Add(w);
                        var q = UiDrive.AllText(confirm) + "\n" + confirm.Title;
                        Questions.Add(q);
                        Press(confirm.FindControl<Button>(Answer(q) ? "ConfirmButton" : "CancelButton")!);
                        break;
                    case ReturnLineReasonDialog reason:
                        _handled.Add(w);
                        reason.FindControl<TextBox>("ReasonBox")!.Text = "Не подошёл товар";
                        Press(reason.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("modal-primary")));
                        break;
                    case PosAlertDialog alert:
                        _handled.Add(w);
                        Alerts.Add(UiDrive.AllText(alert));
                        Press(alert.FindControl<Button>("OkButton")!);
                        break;
                    case SimpleInputDialog input:
                        _handled.Add(w);
                        input.GetVisualDescendants().OfType<TextBox>().First().Text = "дубль возврата";
                        Press(UiDrive.ButtonByText(input, "Далее", "Андан ары"));
                        break;
                    case ManagerPinDialog pin:
                        _handled.Add(w);
                        pin.Apply(PinAccepted
                            ? new ReturnConfirmResult(true, "tok-1", "Владелец", null)
                            : new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.PinInvalid, "pin_invalid", "")));
                        break;
                }
            }
        }

        /// <summary>Перед остановкой — ещё ~2 с отвечает на поздние окна (перечитывание чека, сверка долга после возврата): иначе синхронное окно,
        /// всплывшее после конца теста, держало бы вложенный цикл следующего теста вечно (полный прогон «зависал»).</summary>
        public void Dispose()
        {
            RunUntil(() => false, 2000);
            _timer.Stop();
        }
    }

    // ───────────── карточка чека «Продаж» владельца ─────────────

    private static async Task<SalesWindow> OwnerSalesAsync(int width = 1366, int height = 768)
    {
        var window = TestHost.Services.GetRequiredService<SalesWindow>();
        window.AsOwnerSection(); // раздел «Продажи» программы владельца
        UiShot.Render(window, new UiSize(width, height));
        await Task.CompletedTask;
        return window;
    }

    private static async Task<Popup> OpenCardAsync(SalesWindow window, string saleId, string number)
    {
        var show = typeof(SalesWindow).GetMethod("ShowReceiptDetailsByIdAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)show.Invoke(window, [saleId, number])!;
        UiShot.Settle(4, 30);
        var popup = window.FindControl<Popup>("ReceiptDetailsPopup")!;
        popup.IsOpen.Should().BeTrue("карточка чека открыта");
        return popup;
    }

    private static T InPopup<T>(Popup popup, string name) where T : Control =>
        popup.Child!.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"{typeof(T).Name}#{name} нет в карточке чека");

    private static ReturnSaleDialog? ReturnDialog(Window root) => Walk([root]).OfType<ReturnSaleDialog>().FirstOrDefault(d => d.IsVisible);

    private static void SaveShot(TopLevel top, string name)
    {
        UiShot.Pump();
        using var frame = top.CaptureRenderedFrame() ?? throw new InvalidOperationException("Кадр не получен");
        using var ms = new MemoryStream();
        frame.Save(ms);
        ms.Position = 0;
        using var bitmap = SKBitmap.Decode(ms);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task OwnerCard_ShowsReturnButton_OpensSameReturnWindow_FitsScreen(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        StubSales();
        await LoginAsync();
        var window = await OwnerSalesAsync(width, height);
        using var pilot = new Autopilot(window);
        try
        {
            var popup = await OpenCardAsync(window, "s-701", "№003701");
            var ret = InPopup<Button>(popup, "ReceiptReturnButton");
            ret.IsVisible.Should().BeTrue("в программе владельца у чека есть «Возврат»");
            ret.IsEnabled.Should().BeTrue();
            UiDrive.TextOf(ret).Should().Contain(lang == AppLanguage.Kyrgyz ? "Кайтаруу" : "Возврат");
            // кнопка — внутри карточки по ширине
            var cardTop = TopLevel.GetTopLevel(popup.Child)!;
            var right = ret.TranslatePoint(new Point(ret.Bounds.Width, 0), cardTop)!.Value.X;
            right.Should().BeLessThanOrEqualTo(cardTop.Bounds.Width + 0.5, "кнопка не выходит за карточку");
            // аудит 12.11, Д-5: карточка не выше окна (отступ), кнопка «Возврат» видна целиком по высоте
            popup.Child!.Bounds.Height.Should().BeLessThanOrEqualTo(height - 24 + 0.5, $"карточка чека помещается в окно {width}×{height}");
            var bottom = ret.TranslatePoint(new Point(0, ret.Bounds.Height), popup.Child)!.Value.Y;
            bottom.Should().BeLessThanOrEqualTo(popup.Child.Bounds.Height + 0.5, "«Возврат» не обрезан снизу");
            SaveShot(cardTop, "owner-return-card-" + Slug(lang, width, height));

            Press(ret);
            RunUntil(() => ReturnDialog(window) is { Lines.Count: > 0 }).Should().BeTrue("открылось то же окно возврата, что в кассе, сразу с этим чеком");
            popup.IsOpen.Should().BeFalse("карточка чека закрыта, чтобы не лежать поверх окна возврата");
            var dialog = ReturnDialog(window)!;
            dialog.Lines.Select(l => l.LineId).Should().Equal("si-71", "si-72");
            dialog.FindControl<Button>("ExchangeSelectedButton")!.IsVisible.Should().BeFalse("обмена в программе владельца нет (нет ящика и смены)");
            // окно возврата подгоняется под экран (FitToOwnerWindow); «экран» Headless больше окна теста — ставим размер экрана теста, как в тестах окна кассы
            dialog.WindowState = WindowState.Normal;
            dialog.SizeToContent = SizeToContent.Manual;
            dialog.Width = width;
            dialog.Height = height;
            UiShot.Settle(4);
            foreach (var b in new[] { "ReturnSelectedButton", "ReturnWholeReceiptButton" }.Select(n => dialog.FindControl<Button>(n)!))
            {
                var p = b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), dialog)!.Value;
                p.X.Should().BeLessThanOrEqualTo(width + 1, $"{b.Name} виден целиком при {width}×{height}");
                p.Y.Should().BeLessThanOrEqualTo(height + 1, $"{b.Name} виден целиком при {width}×{height}");
            }
            UiShot.Settle(3);
            SaveShot(dialog, "owner-return-dialog-" + Slug(lang, width, height));
            // аудит 12.11, Д-14: «Вернуть выбранные» — главная (акцентная) кнопка; активна, когда что-то отмечено
            var main = dialog.FindControl<Button>("ReturnSelectedButton")!;
            main.Classes.Should().Contain("nur-primary");
            main.IsEnabled.Should().BeFalse("ничего не отмечено");
            dialog.Lines[0].IsSelected = true;
            UiShot.Settle(3);
            main.IsEnabled.Should().BeTrue("позиция отмечена — главная кнопка активна");
            SaveShot(dialog, "owner-return-dialog-selected-" + Slug(lang, width, height));
            dialog.Lines[0].IsSelected = false;
            RunUntil(() => !dialog.IsBusy, 4000); // возврат дописал всё (перечитал чек, сверил долг), только потом окно закрывается
            dialog.Close();
            UiShot.Pump();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerCard_FullyReturnedReceipt_ReturnDisabledWithHint()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        var window = await OwnerSalesAsync();
        try
        {
            // аудит 12.11, Д-14: в колонке «Чек» — названия товаров (поле name), а не «?»
            RunUntil(() => UiDrive.AllText(window).Contains("Сахар ×2.5")).Should().BeTrue(UiDrive.AllText(window));
            UiDrive.AllText(window).Should().NotContain("? ×");
            var popup = await OpenCardAsync(window, "s-703", "№003703");
            var ret = InPopup<Button>(popup, "ReceiptReturnButton");
            ret.IsVisible.Should().BeTrue();
            ret.IsEnabled.Should().BeFalse("чек возвращён целиком — возвращать нечего");
            InPopup<TextBlock>(popup, "ReceiptReturnFullHint").IsVisible.Should().BeTrue("пояснение строкой — на сенсорном экране подсказки нет");
            ToolTip.GetTip(ret).Should().Be(ReturnSaleLauncher.FullyReturnedHint);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task KassaMode_SalesCard_HasNoNewButtons()
    {
        IsOwnerProp.SetValue(null, false); // касса — прежняя карточка чека
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        var window = TestHost.Services.GetRequiredService<SalesWindow>();
        UiShot.Render(window, new UiSize(1366, 768));
        try
        {
            var popup = await OpenCardAsync(window, "s-701", "№003701");
            popup.Child!.GetLogicalDescendants().OfType<Button>().Should().NotContain(b => b.Name == "ReceiptReturnButton", "режим кассы не меняется");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── оформление возврата из программы владельца ─────────────

    [AvaloniaFact]
    public async Task OwnerReturn_PartialFractional_DoubleClick_OneRequestWithKey_NoShift_NoPrint()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        var window = await OwnerSalesAsync();
        using var pilot = new Autopilot(window);
        try
        {
            var popup = await OpenCardAsync(window, "s-701", "№003701");
            Press(InPopup<Button>(popup, "ReceiptReturnButton"));
            RunUntil(() => ReturnDialog(window) is { Lines.Count: 2 }).Should().BeTrue();
            var dialog = ReturnDialog(window)!;
            foreach (var l in dialog.Lines)
                l.IsSelected = false;
            var sugar = dialog.Lines.Single(l => l.LineId == "si-71");
            sugar.AllowFractionalQuantity.Should().BeTrue("2,5 кг — дробное количество");
            sugar.IsSelected = true;
            sugar.ReturnQuantity = 1.25;
            UiShot.Settle(2);

            var button = dialog.FindControl<Button>("ReturnSelectedButton")!;
            Press(button);
            Press(button); // двойное нажатие
            RunUntil(() => pilot.Alerts.Any(a => a.Contains("Возврат оформлен"))).Should().BeTrue(string.Join("\n---\n", pilot.Questions.Concat(pilot.Alerts)));

            var posts = Requests(UiFake.Server, "POST", "/api/main/pos/sales/s-701/return/");
            posts.Should().ContainSingle("двойное нажатие — один запрос возврата");
            ReturnKey(posts[0]).Should().NotBeNullOrWhiteSpace("деньги — с ключом повтора");
            var item = BodyOf(posts[0])["items"]!.AsArray().Should().ContainSingle().Subject!;
            item["sale_item_id"]!.GetValue<string>().Should().Be("si-71");
            double.Parse(item["quantity"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1.25, "возвращено 1,25 кг из 2,5");
            pilot.Questions.Should().Contain(q => q.Contains("Сумма возврата"), "подтверждение с суммой — как в кассе");
            pilot.Alerts.Should().NotContain(a => a.Contains("не напечатался"), "в программе владельца чек на принтер не печатается");
            RunUntil(() => !dialog.IsBusy, 4000); // возврат дописал всё (перечитал чек, сверил долг), только потом окно закрывается
            dialog.Close();
            UiShot.Pump();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerReturn_DebtReceipt_WholeReturn_ReducesDebt_OneRequest()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        UiFake.Server.Json("POST", "/api/main/pos/sales/s-702/return/", 200, "{\"id\":\"ret-702\",\"sale\":\"s-702\",\"amount\":\"500.00\"}");
        await LoginAsync();
        var window = await OwnerSalesAsync();
        using var pilot = new Autopilot(window);
        try
        {
            var popup = await OpenCardAsync(window, "s-702", "№003702");
            Press(InPopup<Button>(popup, "ReceiptReturnButton"));
            RunUntil(() => ReturnDialog(window) is { Lines.Count: 1 }).Should().BeTrue();
            var dialog = ReturnDialog(window)!;
            UiDrive.AllText(dialog).Should().Contain("в счёт долга клиента", "чек в долг — возврат уменьшает долг, как в кассе");
            var whole = dialog.FindControl<Button>("ReturnWholeReceiptButton")!;
            Press(whole);
            Press(whole);
            RunUntil(() => pilot.Alerts.Any(a => a.Contains("Полный возврат чека оформлен"))).Should().BeTrue(string.Join("\n---\n", pilot.Questions.Concat(pilot.Alerts)));
            var posts = Requests(UiFake.Server, "POST", "/api/main/pos/sales/s-702/return/");
            posts.Should().ContainSingle();
            ReturnKey(posts[0]).Should().NotBeNullOrWhiteSpace();
            pilot.Alerts.Last(a => a.Contains("Полный возврат")).Should().Contain("долг", "у чека в долг — на сколько уменьшен долг клиента");
            RunUntil(() => !dialog.IsBusy, 4000); // возврат дописал всё (перечитал чек, сверил долг), только потом окно закрывается
            dialog.Close();
            UiShot.Pump();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(400, "{\"detail\":\"Возврат по этому чеку запрещён настройками компании\"}", "запрещён настройками компании")]
    [InlineData(400, "{\"items\":[\"Есть позиции не из этого чека: несуществующие sale_item_id\"]}", "Возврат НЕ проведён")]
    public async Task OwnerReturn_ServerRefusal_ShownAsPlainText_NothingDone(int status, string body, string expected)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        UiFake.Server.Json("POST", "/api/main/pos/sales/s-701/return/", status, body);
        await LoginAsync();
        var window = await OwnerSalesAsync();
        using var pilot = new Autopilot(window);
        try
        {
            var popup = await OpenCardAsync(window, "s-701", "№003701");
            Press(InPopup<Button>(popup, "ReceiptReturnButton"));
            RunUntil(() => ReturnDialog(window) is { Lines.Count: 2 }).Should().BeTrue();
            var dialog = ReturnDialog(window)!;
            foreach (var l in dialog.Lines)
                l.IsSelected = l.LineId == "si-72";
            UiShot.Settle(2);
            Press(dialog.FindControl<Button>("ReturnSelectedButton")!);
            RunUntil(() => pilot.Alerts.Any(a => a.Contains(expected))).Should().BeTrue(string.Join("\n---\n", pilot.Questions.Concat(pilot.Alerts)));
            pilot.Alerts.Should().NotContain(a => a.Contains("Возврат оформлен"));
            pilot.Alerts.Should().NotContain(a => a.Contains("{\""), "без сырого ответа сервера");
            RunUntil(() => !dialog.IsBusy, 4000); // возврат дописал всё (перечитал чек, сверил долг), только потом окно закрывается
            dialog.Close();
            UiShot.Pump();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerReturn_NoPermission_NoButton_LauncherSaysNoRights()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true,\"can_view_market_employee_sales\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u2\",\"email\":\"seller@test.kg\",\"full_name\":\"Продавец\",\"role\":\"salesperson\",\"can_view_cashier\":true,\"can_view_market_employee_sales\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await LoginAsync();
        var window = await OwnerSalesAsync();
        using var pilot = new Autopilot(window);
        try
        {
            ReturnSaleLauncher.CanReturn().Should().BeFalse();
            var popup = await OpenCardAsync(window, "s-701", "№003701");
            InPopup<Button>(popup, "ReceiptReturnButton").IsVisible.Should().BeFalse("без права «Возврат» кнопки нет");
            popup.IsOpen = false;
            var opened = ReceiptReturnActions.OpenReturnAsync(window, "s-701", "№003701");
            RunUntil(() => opened.IsCompleted).Should().BeTrue();
            (await opened).Should().BeFalse();
            pilot.Alerts.Should().Contain(a => a.Contains("Недостаточно прав для возврата"));
            ReturnDialog(window).Should().BeNull("окно возврата не открылось");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            UiFake.Reset();
            await LoginAsync(); // вернуть владельца для следующих тестов
        }
    }

    [AvaloniaFact]
    public async Task OwnerReturn_ReceiptOutOfReturnTerm_WarnsLikeKassa_CancelSendsNothing()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.MarketSphere = MarketSpheres.Clothing; // срок обмена/возврата — правило сферы «Одежда»
        UserPreferences.Instance.ExchangeDaysLimit = 14;
        StubSales();
        await LoginAsync();
        var window = await OwnerSalesAsync();
        using var pilot = new Autopilot(window) { Answer = q => !q.Contains("Срок обмена прошёл") };
        try
        {
            var opened = ReceiptReturnActions.OpenReturnAsync(window, "s-704", "№003704");
            RunUntil(() => ReturnDialog(window) is { Lines.Count: 1 }).Should().BeTrue();
            var dialog = ReturnDialog(window)!;
            dialog.FindControl<Button>("ExchangeSelectedButton")!.IsVisible.Should().BeFalse("обмен — только на кассе");
            dialog.Lines[0].IsSelected = true;
            UiShot.Settle(2);
            Press(dialog.FindControl<Button>("ReturnSelectedButton")!);
            RunUntil(() => pilot.Questions.Any(q => q.Contains("Срок обмена прошёл"))).Should().BeTrue(string.Join("\n---\n", pilot.Questions));
            RunUntil(() => false, 600);
            Requests(UiFake.Server, "POST", "/api/main/pos/sales/s-704/return/").Should().BeEmpty("владелец отказался — возврат не отправлен");
            pilot.Questions.Single(q => q.Contains("Срок обмена прошёл")).Should().Contain("прошло 30 дн.");
            RunUntil(() => !dialog.IsBusy, 4000); // возврат дописал всё (перечитал чек, сверил долг), только потом окно закрывается
            dialog.Close();
            UiShot.Pump();
            RunUntil(() => opened.IsCompleted, 2000);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── фильтр периода ─────────────

    [AvaloniaFact]
    public async Task OwnerReturnDialog_PeriodWeek_AsksServerForLastSevenDays()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        var window = await OwnerSalesAsync();
        using var pilot = new Autopilot(window);
        try
        {
            var opened = ReturnSaleLauncher.OpenAsync(window);
            RunUntil(() => ReturnDialog(window) is { Sales.Count: > 0 }).Should().BeTrue();
            var dialog = ReturnDialog(window)!;
            Press(UiDrive.Find<Button>(dialog, "ReturnFiltersToggle"));
            UiShot.Settle(3);
            var period = dialog.GetVisualDescendants().OfType<ComboBox>().First(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Период");
            period.SelectedIndex = (int)SaleListFilter.PeriodKind.Week;
            var from = DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd");
            RunUntil(() => Requests(UiFake.Server, "GET", "/api/main/pos/sales/").Any(r => r.RequestMessage.Url.Contains("date_from=" + from))).Should().BeTrue("«Неделя» — чеки за 7 дней с сервера");
            period.SelectedIndex = (int)SaleListFilter.PeriodKind.Month;
            var monthFrom = DateTime.Today.AddDays(-29).ToString("yyyy-MM-dd");
            RunUntil(() => Requests(UiFake.Server, "GET", "/api/main/pos/sales/").Any(r => r.RequestMessage.Url.Contains("date_from=" + monthFrom))).Should().BeTrue("«Месяц» — за 30 дней");
            RunUntil(() => !dialog.IsBusy, 4000); // возврат дописал всё (перечитал чек, сверил долг), только потом окно закрывается
            dialog.Close();
            UiShot.Pump();
            RunUntil(() => opened.IsCompleted, 2000);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task OwnerSalesAndFinance_ReturnsTab_WeekAndMonth_LoadReturnsForPeriod()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        var finance = TestHost.Services.GetRequiredService<FinanceWindow>();
        typeof(FinanceWindow).GetMethod("AsSalesAndFinance", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(finance, null); // «Продажи и финансы» владельца
        finance.AsOwnerSection();
        UiShot.Render(finance, new UiSize(1366, 768));
        try
        {
            finance.FindControl<TabControl>("FinanceTabs")!.SelectedItem = finance.FindControl<TabItem>("ReturnsTab");
            var week = finance.GetVisualDescendants().OfType<RadioButton>().First(r => "Week".Equals(r.Tag));
            week.IsChecked = true;
            week.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var today = ServerClock.Today;
            var weekFrom = PeriodRange.WeekFrom(today).ToString("yyyy-MM-dd");
            RunUntil(() => Requests(UiFake.Server, "GET", "/api/main/pos/returns/").Any(r => r.RequestMessage.Url.Contains("date_from=" + weekFrom))).Should().BeTrue("«Неделя» во «Возвратах»");
            var month = finance.GetVisualDescendants().OfType<RadioButton>().First(r => "Month".Equals(r.Tag));
            month.IsChecked = true;
            month.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var monthFrom = PeriodRange.MonthFrom(today).ToString("yyyy-MM-dd");
            RunUntil(() => Requests(UiFake.Server, "GET", "/api/main/pos/returns/").Any(r => r.RequestMessage.Url.Contains("date_from=" + monthFrom))).Should().BeTrue("«Месяц» во «Возвратах»");
        }
        finally
        {
            finance.Close();
            UiShot.Pump();
        }
    }

    // ───────────── правка и удаление возврата ≤ 24 ч ─────────────

    private static async Task WithPosReturnFeatureAsync(NurMarketApiClient api, Func<Task> body)
    {
        UiFake.Server.Json("GET", "/api/main/capabilities/", 200, "[\"pos_return_edit_24h\"]");
        await ServerCapabilities.LoadAsync(api, force: true);
        try
        {
            await body();
        }
        finally
        {
            UiFake.Server.Json("GET", "/api/main/capabilities/", 200, "[]");
            await ServerCapabilities.LoadAsync(api, force: true);
            ServerCapabilities.Reset();
        }
    }

    private static string ReturnDoc(string id, int version, bool canEdit, string? block, int hoursLeft) =>
        "{\"id\":\"" + id + "\",\"sale\":\"s-701\",\"sale_number\":3701,\"amount\":\"125.00\",\"created_at\":\"" + DateTimeOffset.UtcNow.AddHours(-1).ToString("O")
        + "\",\"reason\":\"Не подошёл товар\",\"status\":\"posted\",\"version\":" + version + ",\"can_edit\":" + (canEdit ? "true" : "false") + ",\"can_void\":" + (canEdit ? "true" : "false")
        + (block is null ? "" : ",\"edit_block_reason\":\"" + block + "\"")
        + ",\"editable_until\":\"" + DateTimeOffset.UtcNow.AddHours(hoursLeft).ToString("O") + "\",\"server_now\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\""
        + ",\"items\":[{\"sale_item_id\":\"si-71\",\"name\":\"Сахар\",\"qty\":\"1.25\",\"quantity\":\"1.25\",\"amount\":\"125.00\"}]}";

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    public async Task OwnerCard_EditReturns_ListWithin24h_VoidWithManagerPin_ExpiredShowsReason(AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        StubSales();
        var api = await LoginAsync();
        await WithPosReturnFeatureAsync(api, async () =>
        {
            UiFake.Server.Json("GET", "/api/main/pos/returns/", 200, "[" + ReturnDoc("pr-1", 3, true, null, 20) + "," + ReturnDoc("pr-2", 1, false, "edit_window_expired", -2) + "]");
            UiFake.Server.Json("GET", "/api/main/pos/returns/pr-1/", 200, ReturnDoc("pr-1", 3, true, null, 20));
            UiFake.Server.Json("POST", "/api/main/pos/returns/pr-1/void/", 200, "{\"id\":\"pr-1\",\"status\":\"voided\",\"version\":4}");
            var window = await OwnerSalesAsync(1024, 600);
            using var pilot = new Autopilot(window);
            try
            {
                var popup = await OpenCardAsync(window, "s-701", "№003701");
                RunUntil(() => InPopup<Button>(popup, "CustomerReturnsEditButton").IsVisible).Should().BeTrue("у чека есть возврат, который можно изменить, — «Правка возвратов»");
                popup.Child!.Bounds.Height.Should().BeLessThanOrEqualTo(600 - 24 + 0.5, "с «Правкой возвратов» и пояснением карточка всё равно в окне 1024×600 (Д-5)");
                SaveShot(TopLevel.GetTopLevel(popup.Child)!, "owner-return-card-edit-" + Slug(lang, 1024, 600));
                Press(InPopup<Button>(popup, "CustomerReturnsEditButton"));
                Window? list = null;
                RunUntil(() => (list = Walk([window]).FirstOrDefault(w => w.IsVisible && w.Title is { } t && (t.Contains("Возвраты по чеку") || t.Contains("Чек боюнча кайтаруулар"))
                                                                           && w.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && UiDrive.TextOf(b) is "Удалить" or "Өчүрүү"))) is not null)
                    .Should().BeTrue("открылся список возвратов чека");
                popup.IsOpen.Should().BeFalse();
                var text = UiDrive.AllText(list!);
                text.Should().Contain(lang == AppLanguage.Kyrgyz ? "саат" : "ещё", "подсказка «можно изменить ещё N ч» — по времени сервера");
                var buttons = list!.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(UiDrive.TextOf).ToList();
                buttons.Count(b => b == (lang == AppLanguage.Kyrgyz ? "Өчүрүү" : "Удалить")).Should().Be(1, "удалить можно только возврат в пределах 24 ч");
                buttons.Count(b => b == (lang == AppLanguage.Kyrgyz ? "Оңдоо" : "Редактировать")).Should().Be(1);
                list!.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Name == "ReturnEditBlockHint", "у просроченного — пояснение вместо кнопок");
                UiShot.Settle(3);
                SaveShot(list!, "owner-return-list-" + Slug(lang, 1024, 600));

                Press(UiDrive.ButtonByText(list!, lang == AppLanguage.Kyrgyz ? "Өчүрүү" : "Удалить"));
                RunUntil(() => Requests(UiFake.Server, "POST", "/api/main/pos/returns/pr-1/void/").Count == 1 && pilot.Alerts.Count > 0)
                    .Should().BeTrue(string.Join("\n---\n", pilot.Questions.Concat(pilot.Alerts)));
                var post = Requests(UiFake.Server, "POST", "/api/main/pos/returns/pr-1/void/").Single();
                Header(post, "Idempotency-Key").Should().NotBeNullOrWhiteSpace();
                Header(post, "If-Match").Should().Be("\"3\"", "версия документа — защита от чужой правки");
                Header(post, "X-Confirm-Token").Should().Be("tok-1", "PIN руководителя подтверждён");
                BodyOf(post)["reason"]!.GetValue<string>().Should().Be("дубль возврата");
                list!.Close();
                UiShot.Pump();
            }
            finally
            {
                window.Close();
                UiShot.Pump();
            }
        });
    }

    [AvaloniaFact]
    public async Task OwnerCard_EditReturns_FeatureOff_ExplainsInsteadOfList()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        StubSales();
        await LoginAsync();
        ServerCapabilities.Reset();
        var window = await OwnerSalesAsync();
        using var pilot = new Autopilot(window);
        try
        {
            var popup = await OpenCardAsync(window, "s-701", "№003701");
            InPopup<Button>(popup, "CustomerReturnsEditButton").IsVisible.Should().BeFalse("без признака сервера pos_return_edit_24h правки возвратов нет — как в кассе");
            var shown = ReceiptReturnActions.OpenReturnsListAsync(window, "s-701", "№003701");
            RunUntil(() => shown.IsCompleted).Should().BeTrue();
            pilot.Alerts.Should().Contain(a => a.Contains("пока не включены сервером"));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
