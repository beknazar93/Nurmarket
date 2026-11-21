using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.UiTests.Screens;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Scenarios;

/// <summary>2026-11-13 (Н-1 «Первый час» и Н-2 «Главный экран кассы»): настоящее окно кассы в Avalonia.Headless против поддельного NurCRM.
/// Учебная продажа: ни одного изменяющего запроса к серверу, ни одной записи в офлайн-очередь, историю продаж и смену. Одно поле ввода, размеры кнопок,
/// «3*код», шпаргалка клавиш, мастер первого запуска. Снимки для глаз — %TEMP%\nurmarket-tests\ui-n1n2\*.png.</summary>
[Collection("UiScenarios")]
public sealed class FirstHourUiTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;
    private static readonly string ShotDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-n1n2");

    public FirstHourUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        TrainingMode.Exit();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
    }

    public void Dispose()
    {
        TrainingMode.Exit();
        SetShift(_savedShift);
    }

    // ───────────── общая обвязка ─────────────

    private static NurMarketKassa.Ui.Shared.IAppSession Session => TestHost.Services.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>();

    /// <summary>Смена открыта/закрыта и для служб кассы (PosApp), и для окна кассы (IAppSession).</summary>
    private static void SetShift(string? shiftId)
    {
        PosApp.ActiveShiftId = shiftId;
        Session.ActiveShiftId = shiftId;
    }

    private static void Save(Window window, string name, UiSize? size = null)
    {
        Directory.CreateDirectory(ShotDir);
        using var shot = UiShot.Render(window, size);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), data.ToArray());
    }

    private static MainWindow OpenMain(UiSize size)
    {
        var main = (MainWindow)ScreenCatalog.Get("main-kassa").Create();
        UiRobot.Reset(main);
        using var _ = UiShot.Render(main, size);
        main.Activate();
        UiShot.Settle(10, 40);
        return main;
    }

    private static void CloseMain(Window main)
    {
        UiRobot.CloseAll();
        UiRobot.Main = null;
        main.Close();
        UiShot.Pump();
    }

    private static BasketPanelViewModel Basket(Window main) => ((MainWindowViewModel)main.DataContext!).Basket;

    private static TextBlock? WaitTile(Window main, string title)
    {
        UiRobot.WaitFor(() => FindTile(main, title) is not null, 8000);
        return FindTile(main, title);
    }

    private static TextBlock? FindTile(Window main, string title) =>
        main.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == title
            && t.FindAncestorOfType<NurMarketKassa.AvaloniaHost.Views.Main.Controls.CatalogPanelView>() is not null);

    // 2026-11-20: плитки каталога строятся после асинхронной загрузки списка — на нагруженной машине в полном прогоне окно кассы ещё пустое; ждём, а не падаем.
    private static Control Tile(Window main, string title) =>
        WaitTile(main, title)
        ?? throw new InvalidOperationException($"2026-11-20: плитки «{title}» нет; язык=" + NurMarketKassa.Services.Tr.T("ru", "ky", "en", "tr", "uz")
            + "; видимые тексты каталога: " + string.Join(" | ", main.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.FindAncestorOfType<NurMarketKassa.AvaloniaHost.Views.Main.Controls.CatalogPanelView>() is not null)
                .Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).Take(30))
            + "; сфера=" + NurMarketKassa.Services.UserPreferences.Instance.MarketSphere + "/" + NurMarketKassa.Services.UserPreferences.Instance.MarketSphereDetail);

    private static Button PayButton(Window main) =>
        main.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("cart-accent") && b.IsEffectivelyVisible);

    private static T Named<T>(Window w, string name) where T : Control =>
        w.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>«Учебная продажа» из меню ☰ → «Помощь»: выполняется команда этого пункта (само меню в Headless раскрывается не полностью — пункт проверяется по команде).</summary>
    private static void EnterTrainingFromMenu(Window main)
    {
        ((MainWindowViewModel)main.DataContext!).SideMenu.NavigateTrainingSaleCommand.Execute(null);
        UiShot.Settle(6, 40);
    }

    /// <summary>Запросы к поддельному серверу, которые что-то меняют (не GET) или касаются оплаты, смены, продаж.</summary>
    private static List<string> ChangingRequests(int skip) =>
        UiFake.Server.Server.LogEntries.Skip(skip)
            .Where(e => !string.Equals(e.RequestMessage.Method, "GET", StringComparison.OrdinalIgnoreCase)
                        || e.RequestMessage.Path.Contains("checkout", StringComparison.OrdinalIgnoreCase)
                        || e.RequestMessage.Path.Contains("/pos/sales", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.RequestMessage.Method + " " + e.RequestMessage.Path)
            .ToList();

    // ───────────── Н-1: учебная продажа ─────────────

    [AvaloniaFact]
    public async Task TrainingSale_InRealWindow_SendsNothingToServer_WritesNothingToQueueShiftOrHistory()
    {
        await TestHost.WarmAsync();
        SetShift(null); // смена закрыта: учебная продажа не должна её открывать
        var startedUtc = DateTime.UtcNow.AddSeconds(-1);
        var main = OpenMain(UiSize.Large);
        using var guard = UiRobot.Guard();
        try
        {
            var queueBefore = OfflinePendingSalesStore.LoadAll().Count;
            EnterTrainingFromMenu(main);
            TrainingMode.IsActive.Should().BeTrue("пункт меню «Учебная продажа» включает учебный режим");
            var bar = Named<Border>(main, "TrainingBar");
            bar.IsEffectivelyVisible.Should().BeTrue("плашка учебного режима на экране");
            Named<TextBlock>(main, "TrainingBarTitle").Text.Should().Contain("Учебный режим").And.Contain("не учитываются");
            Named<TextBlock>(main, "TrainingBarHint").Text.Should().Contain("Шаг 1");
            Named<Button>(main, "TrainingExitButton").IsEffectivelyVisible.Should().BeTrue("выход из режима — на плашке");
            Save(main, "main-training-step1-light-ru-1366x768");

            var requestsBefore = UiFake.Server.Server.LogEntries.Count();

            // Шаг 1: товар в чек (смена закрыта — окно «Открыть смену» появляться не должно).
            UiDrive.Click(main, Tile(main, "Хлеб"));
            UiRobot.WaitFor(() => Basket(main).Lines.Count == 1).Should().BeTrue("товар добавлен в учебный чек; открыто: " + UiRobot.OpenNames());
            UiRobot.Opened.OfType<OpenShiftDialog>().Should().BeEmpty("учебный режим смену не открывает");
            Named<TextBlock>(main, "TrainingBarHint").Text.Should().Contain("Шаг 2");
            Save(main, "main-training-step2-light-ru-1366x768");

            // Шаг 2–3: «Оплатить» → окно оплаты → «получено» → «Оплатить».
            var paidInDialog = false;
            UiRobot.WhenOpened<CheckoutDialog>(dialog =>
            {
                var vm = (NurMarketKassa.ViewModels.CheckoutViewModel)dialog.DataContext!;
                UiDrive.AllText(dialog).Should().Contain("Учебный режим", "окно оплаты напоминает, что оплата не настоящая");
                Save(dialog, "checkout-training-light-ru");
                UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "CashReceivedBox"), "100");
                UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, vm.PayButtonText));
                paidInDialog = !dialog.IsVisible;
            });
            UiDrive.Click(main, PayButton(main));
            UiRobot.WaitFor(() => Basket(main).Lines.Count == 0 && !Basket(main).IsBusy, 8000).Should().BeTrue("учебная продажа очистила чек; открыто: " + UiRobot.OpenNames());
            paidInDialog.Should().BeTrue("кассир прошёл настоящее окно оплаты");
            UiShot.Settle(10, 50);
            TrainingMode.SalesDone.Should().Be(1);
            Named<TextBlock>(main, "TrainingBarHint").Text.Should().Contain("Готово");
            Save(main, "main-training-done-light-ru-1366x768");

            // Главное: ничего никуда не ушло.
            ChangingRequests(requestsBefore).Should().BeEmpty("учебная продажа не создаёт ни одного запроса оплаты, смены или продажи");
            OfflinePendingSalesStore.LoadAll().Count.Should().Be(queueBefore, "в офлайн-очереди нет новых чеков");
            SoldLineItemsStore.LoadSince(startedUtc).Should().BeEmpty("в истории проданных товаров нет записей");
            PosApp.ActiveShiftId.Should().BeNull("смена не открыта и не изменена");
            Session.ActiveShiftId.Should().BeNull("окно кассы тоже не считает смену открытой");
            UiRobot.Problems.Should().BeEmpty();

            // Выход из режима — кнопкой на плашке; учебные товары из чека убираются.
            UiDrive.Click(main, Tile(main, "Хлеб"));
            UiRobot.WaitFor(() => Basket(main).Lines.Count == 1).Should().BeTrue();
            UiDrive.Click(main, Named<Button>(main, "TrainingExitButton"));
            UiShot.Settle(5, 30);
            TrainingMode.IsActive.Should().BeFalse();
            Named<Border>(main, "TrainingBar").IsVisible.Should().BeFalse();
            Basket(main).Lines.Should().BeEmpty("учебный товар не остаётся в настоящем чеке");
            ChangingRequests(requestsBefore).Should().BeEmpty();
        }
        finally
        {
            CloseMain(main);
        }
    }

    [AvaloniaFact]
    public async Task Training_RealCheckoutService_RefusesAndSendsNothing()
    {
        // Страховка: даже если окно ошибётся и позовёт настоящую службу оплаты, в учебном режиме она отказывает до любого запроса и до офлайн-очереди.
        var api = TestHost.Services.GetRequiredService<NurMarketApiClient>();
        await api.LoginAsync("cashier@test.kg", "12345678");
        var cart = MakeCart(CartJson([("Хлеб", 2, 45, 0)]));
        var service = new PosCheckoutService(cart, new SalesApiService(api), new ShiftApiService(api),
            Null<IShiftStateService>(), Null<IReceiptPrinterService>(), Null<IMediator>(), Null<IAutonomousAuthService>());
        PosApp.ActiveShiftId = Guid.NewGuid().ToString();
        var requestsBefore = UiFake.Server.Server.LogEntries.Count();
        var queueBefore = OfflinePendingSalesStore.LoadAll().Count;

        TrainingMode.Enter();
        var result = await service.CheckoutAsync(new PosCheckoutRequest { PaymentMethod = "cash", CashReceived = "100.00", PrintReceipt = false, ExpectedTotal = 90 });

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be(TrainingMode.BlockedText);
        ChangingRequests(requestsBefore).Should().BeEmpty();
        OfflinePendingSalesStore.LoadAll().Count.Should().Be(queueBefore);
    }

    // ───────────── Н-2: главный экран ─────────────

    public static IEnumerable<object[]> Sizes()
    {
        yield return [1366, 768];
        yield return [1024, 600];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task MainScreen_HasOneInputField_BigPay_AndTouchSizedButtons(int width, int height)
    {
        await TestHost.WarmAsync();
        SetShift(PosApp.ActiveShiftId is { Length: > 0 } open ? open : Guid.NewGuid().ToString());
        var main = OpenMain(new UiSize(width, height));
        try
        {
            var inputs = main.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible && !t.IsReadOnly).ToList();
            inputs.Should().ContainSingle("на экране кассы одно поле ввода (скан, код, название)").Which.Name.Should().Be("ProductSearchBox");
            inputs[0].Watermark.Should().Be(KassaMainViewRulesText.SearchWatermark);

            var pay = PayButton(main);
            var actions = main.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("cart-action") && b.IsEffectivelyVisible).ToList();
            actions.Should().HaveCountGreaterOrEqualTo(4);
            actions.Should().OnlyContain(b => b.Bounds.Height >= 44, "кнопки под чеком — не ниже 44 px");
            actions.Should().OnlyContain(b => b.FontSize >= 13.9, "шрифт кнопок под чеком — 14");
            pay.Bounds.Height.Should().BeGreaterOrEqualTo(height < 640 ? 52 : 64, "«Оплатить» — самая крупная кнопка экрана");
            pay.Bounds.Height.Should().BeGreaterThan(actions.Max(b => b.Bounds.Height));
            pay.Bounds.Width.Should().BeGreaterThan(actions.Max(b => b.Bounds.Width) * 2);

            // Пустой чек подсказывает, что делать.
            UiDrive.AllText(main).Should().Contain("Отсканируйте товар");
            Named<Button>(main, "EmptyCartKeysLink").IsEffectivelyVisible.Should().BeTrue("ссылка на шпаргалку клавиш в пустом чеке");
        }
        finally
        {
            CloseMain(main);
        }
    }

    /// <summary>Текст подсказки поля — из приложения (KassaMainViewRules закрыт для тестов окон, поэтому сверяем с тем же переводом).</summary>
    private static class KassaMainViewRulesText
    {
        public static string SearchWatermark => Tr.T("Скан, код или название", "Скан, код же аты", "Scan, code or name", "Tara, kod veya ad", "Skan, kod yoki nom");
    }

    private static void TypeKeys(Window w, string text)
    {
        foreach (var ch in text)
        {
            if (ch == '*')
            {
                w.KeyPress(Key.Multiply, RawInputModifiers.None, PhysicalKey.NumPadMultiply, "*");
                w.KeyRelease(Key.Multiply, RawInputModifiers.None, PhysicalKey.NumPadMultiply, "*");
            }
            else
            {
                var key = PhysicalKey.Digit0 + (ch - '0');
                w.KeyPressQwerty(key, RawInputModifiers.None);
                w.KeyReleaseQwerty(key, RawInputModifiers.None);
            }
        }
    }

    [AvaloniaFact]
    public async Task SingleField_CodeAndQuantityPrefix_GoToReceipt_ByEnter()
    {
        await TestHost.WarmAsync();
        SetShift(PosApp.ActiveShiftId is { Length: > 0 } open ? open : Guid.NewGuid().ToString());
        var main = OpenMain(UiSize.Large);
        using var guard = UiRobot.Guard();
        try
        {
            var search = Named<TextBox>(main, "ProductSearchBox");

            // «3*код» + Enter — три штуки (Enter в поле делает то же, что раньше кнопка «Добавить»).
            UiDrive.Type(main, search, "3*4600000000001");
            Thread.Sleep(400); // набор руками: Enter не должен считаться хвостом скана
            main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiRobot.WaitFor(() => Basket(main).Lines.Count == 1 && Basket(main).Lines[0].Quantity == 3)
                .Should().BeTrue($"«3*код» добавил три штуки; строк {Basket(main).Lines.Count}, открыто: {UiRobot.OpenNames()}");
            (search.Text ?? "").Should().BeEmpty("поле очищено для следующего кода");

            // «2*» + Enter — количество ждёт следующий товар; метка видна; потом код + Enter.
            UiDrive.Type(main, search, "2*");
            Thread.Sleep(400);
            main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiRobot.WaitFor(() => Basket(main).HasPendingQuantity).Should().BeTrue("«2*» — количество для следующего товара");
            Named<Border>(main, "PendingQuantityChip").IsEffectivelyVisible.Should().BeTrue();
            UiDrive.Type(main, search, "4600000000001");
            Thread.Sleep(400);
            main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiRobot.WaitFor(() => Basket(main).Lines.Count == 1 && Basket(main).Lines[0].Quantity == 5).Should().BeTrue("ещё две штуки к трём");
            Basket(main).HasPendingQuantity.Should().BeFalse();

            // Название: найден один товар — Enter кладёт его в чек, окно «товар не найден» не появляется.
            UiDrive.Type(main, search, "Хлеб");
            UiRobot.WaitFor(() => ((MainWindowViewModel)main.DataContext!).Catalog.Products.Count == 1, 3000);
            Thread.Sleep(400);
            main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiRobot.WaitFor(() => Basket(main).Lines.Count == 1 && Basket(main).Lines[0].Quantity == 6).Should().BeTrue("Enter по названию с одним найденным товаром — товар в чек");
            UiRobot.Opened.Where(w => w is not CheckoutDialog).Should().BeEmpty("лишних окон не было: " + UiRobot.OpenNames());
            Save(main, "main-with-lines-light-ru-1366x768");
        }
        finally
        {
            Basket(main).ClearTrainingReceipt(); // чек теста не оставляем следующему сценарию (метод только чистит чек)
            CloseMain(main);
        }
    }

    [AvaloniaFact]
    public async Task TypedQuantityOutsideField_ThenScan_AddsThatMany_AndEscCancels()
    {
        await TestHost.WarmAsync();
        SetShift(PosApp.ActiveShiftId is { Length: > 0 } open ? open : Guid.NewGuid().ToString());
        var main = OpenMain(UiSize.Large);
        using var guard = UiRobot.Guard();
        try
        {
            // 2026-11-21 (прогон 1.17.91): в полном прогоне в чеке уже лежал товар (3 шт от соседнего сценария «3*код»), скан «4 *» давал 7 вместо 4 — тест начинается с пустого чека.
            var leftover = Basket(main).Lines.Count;
            Basket(main).ClearTrainingReceipt();
            UiShot.Settle(3, 20);
            main.Focus();
            // Человек набирает «3», потом «*» (паузы длиннее сканерных), курсор не в поле.
            TypeKeys(main, "3");
            Thread.Sleep(150);
            TypeKeys(main, "*");
            UiRobot.WaitFor(() => Basket(main).HasPendingQuantity).Should().BeTrue("«3», «*» вне поля — количество для следующего скана");
            Basket(main).ManualQuantity.Should().Be("3");

            // Esc отменяет.
            main.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            UiShot.Settle(3, 20);
            Basket(main).HasPendingQuantity.Should().BeFalse("Esc сбрасывает «3*»");

            // Снова «4 *» и скан.
            Thread.Sleep(300);
            TypeKeys(main, "4");
            Thread.Sleep(150);
            TypeKeys(main, "*");
            UiRobot.WaitFor(() => Basket(main).ManualQuantity == "4").Should().BeTrue();
            Thread.Sleep(400);
            TypeKeys(main, "4600000000001");
            main.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiRobot.WaitFor(() => Basket(main).Lines.Count == 1 && Basket(main).Lines[0].Quantity == 4, 5000)
                .Should().BeTrue($"скан после «4 *» — четыре штуки; в чеке до теста было строк {leftover}; строк {Basket(main).Lines.Count}, количество {string.Join('/', Basket(main).Lines.Select(l => l.Quantity))}, ожидание «N*» {Basket(main).HasPendingQuantity}/{Basket(main).ManualQuantity}, открыто: {UiRobot.OpenNames()}"); // 2026-11-21: диагностика (в полном прогоне падает, отдельно проходит)
            Basket(main).HasPendingQuantity.Should().BeFalse();
        }
        finally
        {
            Basket(main).ClearTrainingReceipt();
            CloseMain(main);
        }
    }

    // ───────────── Н-2: шпаргалка клавиш ─────────────

    [AvaloniaFact]
    public void HotkeysCheatSheet_ListsAssignedKeys_AndFitsSmallScreen()
    {
        var sections = KassaHotkeysWindow.Build(action => action == NurMarketKassa.AvaloniaHost.Services.PosHotkeyAction.Pay ? "Enter" : "");
        sections.Should().HaveCount(3);
        sections.SelectMany(s => s.Rows).Should().Contain(r => r.Keys == "Enter").And.Contain(r => r.Keys == "?").And.Contain(r => r.Keys == "Esc");
        sections.SelectMany(s => s.Rows).Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Keys) && !string.IsNullOrWhiteSpace(r.Action), "снятое назначение в шпаргалку не попадает");

        foreach (var (theme, lang) in new[] { (UiTheme.Light, AppLanguage.Russian), (UiTheme.Dark, AppLanguage.Kyrgyz), (UiTheme.Light, AppLanguage.Kyrgyz), (UiTheme.Dark, AppLanguage.Russian) })
        {
            UiShot.Setup(theme, lang);
            var window = new KassaHotkeysWindow();
            try
            {
                Save(window, "hotkeys-" + UiShot.Slug(theme, lang));
                TextClipChecker.Find(window, "n1n2", "x").Should().BeEmpty();
            }
            finally
            {
                window.Close();
                UiShot.Pump();
            }
        }
    }

    [AvaloniaFact(Skip = "2026-11-19: в Avalonia.Headless модальная шпаргалка не закрывается клавишей из «робота» окна (TopLevel must be a headless window) — открытие по F1/«?» проверить вручную; содержимое и вид окна проверяет HotkeysCheatSheet_ListsAssignedKeys_AndFitsSmallScreen")]
    public async Task HotkeysCheatSheet_OpensByF1_WhenNoProductGroupOnF1_AndFromEmptyReceiptLink()
    {
        await TestHost.WarmAsync();
        SetShift(PosApp.ActiveShiftId is { Length: > 0 } open ? open : Guid.NewGuid().ToString());
        var main = OpenMain(UiSize.Large);
        using var guard = UiRobot.Guard(8000);
        try
        {
            var shown = 0;
            UiRobot.WhenOpened<KassaHotkeysWindow>(w =>
            {
                shown++;
                w.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                w.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            });
            main.Focus();
            main.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
            main.KeyReleaseQwerty(PhysicalKey.F1, RawInputModifiers.None);
            UiRobot.WaitFor(() => shown == 1 && UiRobot.Opened.All(w => !w.IsVisible)).Should().BeTrue("F1 открывает шпаргалку (на F1 нет группы товаров), Esc закрывает");

            UiDrive.Click(main, Named<Button>(main, "EmptyCartKeysLink"));
            UiRobot.WaitFor(() => shown == 2 && UiRobot.Opened.All(w => !w.IsVisible)).Should().BeTrue("ссылка «Клавиши: ?» в пустом чеке открывает ту же шпаргалку");
            UiRobot.Problems.Should().BeEmpty();
        }
        finally
        {
            CloseMain(main);
        }
    }

    // ───────────── Н-1: мастер первого запуска ─────────────

    [AvaloniaFact]
    public void FirstRunWizard_GoesThroughFiveSteps_SkipsAndStartsTraining()
    {
        var calls = new List<string>();
        var products = 0;
        FirstRunWizardWindow.Actions Actions() => new()
        {
            SphereText = () => "Продукты",
            ChooseSphere = () => { calls.Add("sphere"); return Task.FromResult<string?>("Вид магазина: Продукты"); },
            SetupPrinter = () => { calls.Add("printer"); return Task.FromResult<string?>(null); },
            SetupScale = () => { calls.Add("scale"); return Task.FromResult<string?>(null); },
            AddProducts = () => { calls.Add("add"); products++; return Task.FromResult<string?>(FirstRunWizardWindow.ProductCountText(products)); },
            ImportProducts = () => { calls.Add("import"); return Task.FromResult<string?>(null); },
            ProductCount = () => products,
        };

        foreach (var (theme, lang) in new[] { (UiTheme.Light, AppLanguage.Russian), (UiTheme.Dark, AppLanguage.Kyrgyz) })
        {
            UiShot.Setup(theme, lang);
            calls.Clear();
            products = 0;
            var owner = new Window { Width = 1024, Height = 600 };
            owner.Show();
            var wizard = new FirstRunWizardWindow(Actions());
            var result = wizard.ShowDialog<bool>(owner);
            UiShot.Settle(4);
            var slug = UiShot.Slug(theme, lang);
            try
            {
                wizard.Step.Should().Be(1);
                Save(wizard, $"wizard-step1-{slug}");
                TextClipChecker.Find(wizard, "n1n2", "x").Should().BeEmpty();
                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardSphere"));
                UiShot.Settle(3);
                calls.Should().Contain("sphere");
                UiDrive.AllText(wizard).Should().Contain("Продукты");

                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
                wizard.Step.Should().Be(2);
                Save(wizard, $"wizard-step2-{slug}");
                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardPrinter"));
                UiShot.Settle(3);
                calls.Should().Contain("printer");

                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext")); // «Пропустить» — принтера нет
                wizard.Step.Should().Be(3);
                Save(wizard, $"wizard-step3-{slug}");
                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
                wizard.Step.Should().Be(4);
                for (var i = 0; i < 3; i++)
                {
                    UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardAddProducts"));
                    UiShot.Settle(3);
                }
                products.Should().Be(3, "три первых товара — три нажатия «Добавить товары»");
                Save(wizard, $"wizard-step4-{slug}");
                TextClipChecker.Find(wizard, "n1n2", "x").Should().BeEmpty();

                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardBack"));
                wizard.Step.Should().Be(3);
                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
                wizard.Step.Should().Be(5);
                Save(wizard, $"wizard-step5-{slug}");
                TextClipChecker.Find(wizard, "n1n2", "x").Should().BeEmpty();

                UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext")); // «Начать учебную продажу»
                UiShot.Settle(3);
                result.IsCompleted.Should().BeTrue();
                result.Result.Should().BeTrue("последняя кнопка мастера ведёт в учебную продажу");
            }
            finally
            {
                if (wizard.IsVisible)
                    wizard.Close();
                owner.Close();
                UiShot.Pump();
            }
        }
    }

    [AvaloniaFact]
    public void FirstRunWizard_OfferedOnlyToNewEmptyShop_Once()
    {
        FirstRunWizardWindow.ShouldOfferAtStartup(alreadyDone: false, productCount: 0, canChangeSettings: true).Should().BeTrue();
        FirstRunWizardWindow.ShouldOfferAtStartup(alreadyDone: true, productCount: 0, canChangeSettings: true).Should().BeFalse("мастер уже показывали");
        FirstRunWizardWindow.ShouldOfferAtStartup(alreadyDone: false, productCount: 5, canChangeSettings: true).Should().BeFalse("магазин уже с товарами");
        FirstRunWizardWindow.ShouldOfferAtStartup(alreadyDone: false, productCount: 0, canChangeSettings: false).Should().BeFalse("кассиру без права «Настройки» мастер не навязываем");

        var folder = Path.Combine(Path.GetTempPath(), "nm-first-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            FirstRunWizardWindow.IsDone(folder).Should().BeFalse();
            FirstRunWizardWindow.MarkDone(folder);
            FirstRunWizardWindow.IsDone(folder).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ───────────── снимки главного экрана (до/после сравниваются глазами) ─────────────

    public static IEnumerable<object[]> ShotMatrix()
    {
        foreach (var theme in new[] { UiTheme.Light, UiTheme.Dark })
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz })
        foreach (var size in new[] { new UiSize(1024, 600), new UiSize(1366, 768) })
            yield return [theme, lang, size.Width, size.Height];
    }

    [AvaloniaTheory]
    [MemberData(nameof(ShotMatrix))]
    public async Task MainScreen_Shots_EmptyAndTraining(UiTheme theme, AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        SetShift(null);
        UiShot.Setup(theme, lang);
        var size = new UiSize(width, height);
        var main = OpenMain(size);
        try
        {
            var slug = UiShot.Slug(theme, lang, size);
            Save(main, "main-empty-" + slug, size);
            TextClipChecker.Find(main, "n1n2", "x").Should().BeEmpty("на главном экране нет обрезанных надписей");
            EnterTrainingFromMenu(main);
            TrainingMode.IsActive.Should().BeTrue();
            Save(main, "main-training-" + slug, size);
            var bar = Named<Border>(main, "TrainingBar");
            bar.Bounds.Height.Should().BeLessOrEqualTo(height < 640 ? 84 : 72, "плашка учебного режима не съедает экран");
            TextClipChecker.Find(main, "n1n2", "x").Should().BeEmpty("надписи плашки учебного режима не обрезаны");
        }
        finally
        {
            TrainingMode.Exit();
            CloseMain(main);
        }
    }
}
