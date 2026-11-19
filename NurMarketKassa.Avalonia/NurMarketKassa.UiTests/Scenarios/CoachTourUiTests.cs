using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.UiTests.Screens;
using NurMarketKassa.ViewModels.Main;
using SkiaSharp;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.UiTests.Scenarios;

/// <summary>2026-11-19 (владелец, п. 6 и 8): интерактивное обучение в настоящем окне кассы (Avalonia.Headless, поддельный NurCRM): подсветка рядом с нужным элементом,
/// клики проходят сквозь «окно», шаги проходятся настоящими действиями, учебная продажа ничего не пишет на сервер/в очередь/смену, текст карточек не обрезан на 1024×600 (ru, ky, uz).
/// Снимки для глаз — %TEMP%\nurmarket-tests\ui-coach\*.png.</summary>
[Collection("UiScenarios")]
public sealed class CoachTourUiTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;
    private static readonly string ShotDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-coach");
    private static readonly UiSize Small = new(1024, 600);

    public CoachTourUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        TrainingMode.Exit();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
    }

    public void Dispose()
    {
        TrainingMode.Exit();
        PosApp.ActiveShiftId = _savedShift;
    }

    private static void Save(Window window, string name, UiSize size)
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

    private static MainWindowViewModel Vm(Window main) => (MainWindowViewModel)main.DataContext!;

    private static T Named<T>(Window w, string name) where T : Control => w.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void StartTour(MainWindow main)
    {
        Vm(main).SideMenu.NavigateFirstRunWizardCommand.Execute(null); // пункт меню «Помощь → Обучение»
        UiShot.Settle(8, 40);
        main.CoachTourActive.Should().BeTrue("пункт меню «Обучение» запускает обучение; Content=" + main.Content?.GetType().Name
            + "; открыто: " + UiRobot.OpenNames() + "; проблемы: " + string.Join(" | ", UiRobot.Problems));
    }

    private static Button PayButton(Window main) =>
        main.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, Vm(main).Basket.PayCommand));

    private static Button MenuButton(Window main) =>
        main.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, Vm(main).Toolbar.ToggleSideMenuCommand));

    private static Control Tile(Window main, string title) =>
        main.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == title
            && t.FindAncestorOfType<NurMarketKassa.AvaloniaHost.Views.Main.Controls.CatalogPanelView>() is not null);

    private static Point CenterIn(Control target, Visual space) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), space)!.Value;

    /// <summary>Что лежит под точкой окна: часть слоя обучения (затемнение) или настоящий элемент кассы.</summary>
    private static bool IsOverlayAt(Window main, Point point) =>
        main.InputHitTest(point) is Visual visual && (visual is CoachMarkOverlay || visual.FindAncestorOfType<CoachMarkOverlay>() is not null);

    private static void AssertCardFits(MainWindow main)
    {
        var overlay = main.CoachOverlay!;
        var card = overlay.CardRect;
        card.Left.Should().BeGreaterThanOrEqualTo(0);
        card.Top.Should().BeGreaterThanOrEqualTo(0);
        card.Right.Should().BeLessThanOrEqualTo(overlay.Bounds.Width + 0.5, "карточка не вылезает вправо");
        card.Bottom.Should().BeLessThanOrEqualTo(overlay.Bounds.Height + 0.5, "карточка не вылезает вниз");
        var cardControl = Named<Border>(main, "CoachCard");
        foreach (var button in cardControl.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
        {
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), cardControl)!.Value.X;
            right.Should().BeLessThanOrEqualTo(cardControl.Bounds.Width + 0.5, $"кнопка «{button.Name}» не обрезана справа");
        }
    }

    private static string Counter(Window main) => Named<TextBlock>(main, "CoachCounter").Text!;

    // ───────────── весь путь настоящими действиями ─────────────

    [AvaloniaFact]
    public async Task Tour_GoesThroughRealActions_HighlightsTargets_PassesClicks_AndWritesNothing()
    {
        await TestHost.WarmAsync();
        PosApp.ActiveShiftId = null;
        var startedUtc = DateTime.UtcNow.AddSeconds(-1);
        var main = OpenMain(Small);
        using var guard = UiRobot.Guard();
        try
        {
            var queueBefore = OfflinePendingSalesStore.LoadAll().Count;
            var requestsBefore = UiFake.Server.Server.LogEntries.Count();
            StartTour(main);
            var engine = main.CoachEngine!;
            var overlay = main.CoachOverlay!;

            // Шаг 1: карточка по центру, подсветки нет, кнопка «Далее» есть.
            engine.Index.Should().Be(0);
            overlay.HoleRect.Should().BeNull("первый шаг — без подсветки");
            AssertCardFits(main);
            UiDrive.Click(main, Named<Button>(main, "CoachNext"));
            UiShot.Settle(6, 40);

            // Шаг 2: подсветка вокруг кнопки меню ☰; клик по ней проходит сквозь «окно» и открывает меню; шаг засчитан сам.
            engine.Index.Should().Be(1);
            var menu = MenuButton(main);
            overlay.HoleRect.Should().NotBeNull();
            overlay.HoleRect!.Value.Contains(CenterIn(menu, overlay)).Should().BeTrue("«окно» подсветки вокруг кнопки меню");
            IsOverlayAt(main, CenterIn(menu, main)).Should().BeFalse("клик сквозь «окно» доходит до кнопки меню");
            IsOverlayAt(main, new Point(main.Bounds.Width / 2, main.Bounds.Height - 20)).Should().BeTrue("вне «окна» затемнение клики не пускает");
            Save(main, "coach-1-menu-light-ru-1024x600", Small);
            UiDrive.Click(main, menu);
            UiShot.Settle(8, 40);
            Vm(main).IsSideMenuOpen.Should().BeTrue();
            engine.Index.Should().Be(2, "меню открыто — шаг «Меню кассы» пройден действием");
            Vm(main).CloseSideMenu();
            UiShot.Settle(8, 40);
            engine.Index.Should().Be(3, "меню закрыто — шаг «Настройки и помощь» пройден");

            // Шаг 4: принтер/весы — с кнопками мастеров; пропускаем.
            Named<Button>(main, "CoachAction_printer").IsEffectivelyVisible.Should().BeTrue();
            Named<Button>(main, "CoachAction_scales").IsEffectivelyVisible.Should().BeTrue();
            AssertCardFits(main);
            UiDrive.Click(main, Named<Button>(main, "CoachNext"));
            UiShot.Settle(10, 40);

            // Шаг 5: поиск товара — включается учебный режим; подсвечено поле ввода; товар жмётся плиткой (затемнение здесь клики пускает).
            engine.Index.Should().Be(4);
            TrainingMode.IsActive.Should().BeTrue("на шаге поиска товара включается учебный режим; тур=" + main.CoachTourActive + ", CanEnter=" + Vm(main).Basket.CanEnterTraining + ", Busy=" + Vm(main).Basket.IsBusy + ", строк=" + Vm(main).Basket.Lines.Count);
            overlay.HoleRect.Should().NotBeNull("подсвечено поле ввода");
            Save(main, "coach-2-find-light-ru-1024x600", Small);
            var tile = Tile(main, "Хлеб");
            IsOverlayAt(main, CenterIn(tile, main)).Should().BeFalse("плитку товара можно нажать");
            UiDrive.Click(main, tile);
            UiRobot.WaitFor(() => Vm(main).Basket.Lines.Count == 1).Should().BeTrue("товар попал в чек; открыто: " + UiRobot.OpenNames());
            UiShot.Settle(8, 40);
            engine.Index.Should().Be(5, "строка в чеке — шаг «Найдите товар» пройден");
            UiDrive.Click(main, Named<Button>(main, "CoachNext")); // «Корзина и скидка»
            UiShot.Settle(8, 40);

            // Шаг 7: «Оплатить» — настоящее окно оплаты в учебном режиме.
            engine.Index.Should().Be(6);
            var pay = PayButton(main);
            overlay.HoleRect!.Value.Contains(CenterIn(pay, overlay)).Should().BeTrue("подсвечена кнопка «Оплатить»");
            IsOverlayAt(main, CenterIn(pay, main)).Should().BeFalse();
            var paidInDialog = false;
            UiRobot.WhenOpened<NurMarketKassa.AvaloniaHost.Views.Dialogs.CheckoutDialog>(dialog =>
            {
                var vm = (NurMarketKassa.ViewModels.CheckoutViewModel)dialog.DataContext!;
                UiDrive.Type(dialog, UiDrive.Find<TextBox>(dialog, "CashReceivedBox"), "100");
                UiDrive.Click(dialog, UiDrive.ButtonByText(dialog, vm.PayButtonText));
                paidInDialog = !dialog.IsVisible;
            });
            UiDrive.Click(main, pay);
            UiRobot.WaitFor(() => Vm(main).Basket.Lines.Count == 0 && !Vm(main).Basket.IsBusy, 8000).Should().BeTrue("учебная продажа прошла; открыто: " + UiRobot.OpenNames());
            UiShot.Settle(10, 50);
            paidInDialog.Should().BeTrue();
            engine.Index.Should().Be(7, "оплата в учебном режиме — шаг «Оплатите чек» пройден");

            // Главное: ничего никуда не ушло.
            UiFake.Server.Server.LogEntries.Skip(requestsBefore)
                .Where(e => !string.Equals(e.RequestMessage.Method, "GET", StringComparison.OrdinalIgnoreCase)
                            || e.RequestMessage.Path.Contains("checkout", StringComparison.OrdinalIgnoreCase)
                            || e.RequestMessage.Path.Contains("/pos/sales", StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty("обучение не создаёт запросов оплаты, смены или продажи");
            OfflinePendingSalesStore.LoadAll().Count.Should().Be(queueBefore, "в офлайн-очереди нет новых чеков");
            SoldLineItemsStore.LoadSince(startedUtc).Should().BeEmpty("в истории проданных товаров нет записей");
            PosApp.ActiveShiftId.Should().BeNull("смена не открыта");

            // Шаги «Смена» и «Где помощь» → экран «Обучение завершено!».
            UiDrive.Click(main, Named<Button>(main, "CoachNext"));
            UiShot.Settle(6, 40);
            Named<Button>(main, "CoachNext").Content.Should().Be(CoachTour.FinishText, "последний шаг — «Завершить»");
            UiDrive.Click(main, Named<Button>(main, "CoachNext"));
            UiShot.Settle(10, 50);
            engine.IsFinished.Should().BeTrue();
            Named<TextBlock>(main, "CoachDoneTitle").Text.Should().Be(CoachTour.DoneTitle);
            Named<Button>(main, "CoachStartWork").IsEffectivelyVisible.Should().BeTrue();
            Named<Button>(main, "CoachAgain").IsEffectivelyVisible.Should().BeTrue();
            Named<Button>(main, "CoachOpenHelp").IsEffectivelyVisible.Should().BeTrue();
            Named<Border>(main, "TrainingBar").IsVisible.Should().BeFalse("жёлтая полоса учебного режима исчезла");
            TrainingMode.IsActive.Should().BeFalse();
            AssertCardFits(main);
            Save(main, "coach-3-done-light-ru-1024x600", Small);

            // «Начать работу» — слой исчезает.
            UiDrive.Click(main, Named<Button>(main, "CoachStartWork"));
            UiShot.Settle(6, 40);
            main.CoachTourActive.Should().BeFalse();
            main.GetVisualDescendants().OfType<CoachMarkOverlay>().Should().BeEmpty();
            UiRobot.Problems.Should().BeEmpty();
        }
        finally
        {
            CloseMain(main);
        }
    }

    // ───────────── «Пройти ещё раз», «Назад», «Пропустить» в настоящем окне; кнопки не обрезаны на ky/uz ─────────────

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Tour_EveryStep_FitsAndIsNotClipped_At1024x600(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var main = OpenMain(Small);
        try
        {
            StartTour(main);
            var engine = main.CoachEngine!;
            var overlay = main.CoachOverlay!;
            for (var i = 0; i < engine.Count; i++)
            {
                UiShot.Settle(6, 40);
                AssertCardFits(main);
                var clipped = TextClipChecker.Find(main, "coach-" + engine.Current.Id, lang.ToString()).Where(v => v.Detail.Contains("Coach")).ToList();
                clipped.Should().BeEmpty("текст карточки обучения не обрезан: " + string.Join("; ", clipped.Select(v => v.Detail)));
                if (engine.Current.Id == "devices")
                    Save(main, $"coach-devices-light-{lang}-1024x600".ToLowerInvariant(), Small);
                if (overlay.HoleRect is { } hole)
                    hole.Intersects(overlay.CardRect).Should().BeFalse($"выноска не закрывает подсвеченный элемент (шаг {engine.Current.Id})");
                engine.Next(); // «Пропустить шаг» / «Далее»
            }
            engine.IsFinished.Should().BeTrue();
            UiShot.Settle(6, 40);
            AssertCardFits(main);
            var done = TextClipChecker.Find(main, "coach-done", lang.ToString()).Where(v => v.Detail.Contains("Coach")).ToList();
            done.Should().BeEmpty("экран «Обучение завершено!» не обрезан: " + string.Join("; ", done.Select(v => v.Detail)));
            Save(main, $"coach-done-light-{lang}-1024x600".ToLowerInvariant(), Small);
        }
        finally
        {
            TrainingMode.Exit();
            CloseMain(main);
            UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        }
    }

    [AvaloniaFact]
    public async Task Tour_QuitAndRestartAndBack_Work_AndQuitLeavesNoTrainingMode()
    {
        await TestHost.WarmAsync();
        var main = OpenMain(Small);
        try
        {
            StartTour(main);
            var engine = main.CoachEngine!;
            engine.Next(); // sphere
            engine.Next(); // menu-open: пропуск без действия
            UiShot.Settle(6, 40);
            engine.Index.Should().Be(2);
            UiDrive.Click(main, Named<Button>(main, "CoachBack"));
            UiShot.Settle(6, 40);
            engine.Index.Should().Be(1, "«Назад» возвращает на предыдущий шаг");
            engine.Next();
            engine.Next();
            engine.Next(); // devices → find: включается учебный режим
            UiShot.Settle(8, 40);
            engine.Current.Id.Should().Be("find");
            TrainingMode.IsActive.Should().BeTrue();
            UiDrive.Click(main, Named<Button>(main, "CoachQuit"));
            UiShot.Settle(6, 40);
            main.CoachTourActive.Should().BeFalse("«Закончить позже» закрывает обучение");
            TrainingMode.IsActive.Should().BeFalse("и выключает учебный режим");
            Named<Border>(main, "TrainingBar").IsVisible.Should().BeFalse();
            StartTour(main); // снова из меню — с первого шага
            main.CoachEngine!.Index.Should().Be(0);
            TrainingMode.IsActive.Should().BeFalse("до шага оплаты учебного режима нет");
        }
        finally
        {
            TrainingMode.Exit();
            CloseMain(main);
        }
    }
}
