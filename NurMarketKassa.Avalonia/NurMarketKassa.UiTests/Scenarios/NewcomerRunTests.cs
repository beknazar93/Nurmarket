using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.UiTests.Screens;
using NurMarketKassa.ViewModels.Main;
using Xunit;

namespace NurMarketKassa.UiTests.Scenarios;

/// <summary>2026-11-13 (Н-1/Н-2): «прогон новичка» — замена проверки с живыми продавцами (владелец: возможности нет). Типовые задачи проходятся в настоящем окне кассы
/// (Avalonia.Headless, поддельный NurCRM) так, как их делал бы человек: щелчки, сканы, клавиши, набор в полях. Считаем действия (щелчок, скан, клавиша-команда, заполненное поле)
/// и символы, набранные в полях. Итог пишется в %TEMP%\nurmarket-tests\newcomer-steps-latest.md — его сравнивают «до» и «после» правок.
/// Окна кассы в Windows модальные и сами крутят очередь интерфейса, пока открыты, поэтому действия «в окне» заданы заранее: окно открылось — «робот» выполняет свои шаги
/// внутри него (<see cref="WhenOpened{T}"/>). Сторож закрывает зависшее окно через 20 с — задача тогда отмечается как не пройденная, прогон идёт дальше.
/// Тест не падает из-за числа шагов (это замер).</summary>
[Collection("UiScenarios")]
public sealed class NewcomerRunTests : IDisposable
{
    private readonly string? _savedShift = PosApp.ActiveShiftId;
    private static bool _hooked;
    private static NewcomerRunTests? _current;

    private readonly List<Window> _opened = new();
    private readonly Dictionary<Type, Action<Window>> _drivers = new();
    private Window? _main;
    private Run? _run;

    public NewcomerRunTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        _current = this;
        if (!_hooked)
        {
            _hooked = true;
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => _current?.OnWindowOpened(w));
        }
    }

    public void Dispose()
    {
        _current = null;
        SetShift(_savedShift);
    }

    private static readonly string ProgressFile = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "newcomer-steps-progress.txt");

    private static void Progress(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProgressFile)!);
            File.AppendAllText(ProgressFile, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // журнал хода не должен мешать замеру
        }
    }

    /// <summary>Счётчик одной задачи.</summary>
    private sealed class Run(string title)
    {
        public string Title { get; } = title;
        public int Clicks, Scans, Keys, Fields, Typed;
        public string? Failure;
        public readonly List<string> Log = new();
        public int Actions => Clicks + Scans + Keys + Fields;

        public void Note(string line)
        {
            Log.Add(line);
            Progress("   " + line);
        }

        public void Fail(string why)
        {
            Failure ??= why;
            Progress("   !! " + why);
        }

        public void Click(Window w, Control c, string what)
        {
            Clicks++;
            Note("щелчок: " + what);
            try
            {
                // Что реально лежит под указателем (если не сам элемент — щелчок перехватит другое: затемнение, плашка).
                var point = c.TranslatePoint(new Avalonia.Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w);
                if (point is { } pt && w.InputHitTest(pt) is Avalonia.Visual hit && !ReferenceEquals(hit, c) && !c.IsVisualAncestorOf(hit) && !hit.IsVisualAncestorOf(c))
                    Progress($"      под указателем не элемент, а {hit.GetType().Name} {(hit as Control)?.Name} [{string.Join(' ', (hit as Control)?.Classes ?? new Classes())}]; окно включено: {w.IsEnabled}");
            }
            catch (Exception ex)
            {
                Progress("      (проверка указателя: " + ex.Message + ")");
            }
            UiDrive.Click(w, c);
            UiShot.Settle(3, 20);
        }

        public void Key(Window w, PhysicalKey key, string what)
        {
            Keys++;
            Note("клавиша: " + what);
            w.KeyPressQwerty(key, RawInputModifiers.None);
            w.KeyReleaseQwerty(key, RawInputModifiers.None);
            UiShot.Settle(3, 20);
        }

        public void Field(Window w, TextBox box, string text, string what)
        {
            Fields++;
            Typed += text.Length;
            Note($"поле: {what} = «{text}»");
            UiDrive.Type(w, box, text);
            UiShot.Settle(2, 20);
        }

        /// <summary>Скан сканером: цифры подряд и Enter — одно действие человека.</summary>
        public void Scan(Window w, string code, string what)
        {
            Scans++;
            Note("скан: " + what);
            foreach (var ch in code)
            {
                var key = PhysicalKey.Digit0 + (ch - '0');
                w.KeyPressQwerty(key, RawInputModifiers.None);
                w.KeyReleaseQwerty(key, RawInputModifiers.None);
            }
            w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            w.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            UiShot.Settle(6, 30);
        }
    }

    // ───────────── окна, которые открывает касса ─────────────

    private void WhenOpened<T>(Action<T> drive) where T : Window => _drivers[typeof(T)] = w => drive((T)w);

    private void OnWindowOpened(Window w)
    {
        if (ReferenceEquals(w, _main) || _run is null)
            return;
        _opened.Add(w);
        var run = _run;
        run.Note("открылось окно: " + w.GetType().Name);
        if (!_drivers.TryGetValue(w.GetType(), out var driver))
        {
            run.Note("  (действий для окна не задано; кнопки: " + string.Join(", ", w.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(UiDrive.TextOf).Where(t => !string.IsNullOrWhiteSpace(t)).Take(6)) + ")");
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                UiShot.Settle(4, 25);
                if (w.IsVisible)
                    driver(w);
            }
            catch (Exception ex)
            {
                run.Fail($"окно {w.GetType().Name}: {ex.GetType().Name}: {ex.Message}");
                try { w.Close(); } catch { /* уже закрыто */ }
            }
        }, DispatcherPriority.Background);
    }

    private void CloseExtra()
    {
        foreach (var w in _opened.Where(w => w.IsVisible).Reverse().ToList())
        {
            try { w.Close(); } catch { /* окно уже закрыто */ }
        }
    }

    private string OpenNames() => string.Join(", ", _opened.Where(w => w.IsVisible).Select(w => w.GetType().Name));

    private static bool WaitFor(Func<bool> condition, int ms = 4000)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            UiShot.Pump();
            if (condition())
                return true;
            Thread.Sleep(25);
        }
        UiShot.Pump();
        return condition();
    }

    private static Control Tile(Window main, string title) =>
        main.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == title
            && t.FindAncestorOfType<NurMarketKassa.AvaloniaHost.Views.Main.Controls.CatalogPanelView>() is not null)
        ?? throw new InvalidOperationException($"плитка «{title}» не найдена в каталоге");

    private static Button PayButton(Window main) =>
        main.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("cart-accent") && b.IsEffectivelyVisible);

    private static BasketPanelViewModel Basket(Window main) => ((MainWindowViewModel)main.DataContext!).Basket;

    private static double TotalQuantity(Window main) => Basket(main).Lines.Sum(l => Convert.ToDouble(l.Quantity));

    // ───────────── «роботы» окон ─────────────

    private void DriveOpenShift(Run run) => WhenOpened<OpenShiftDialog>(open =>
    {
        // Человек вводит остаток и жмёт «Открыть смену» — два действия. Поддельный сервер смену не открывает (ожидание сервера блокирует кассу на десятки секунд),
        // поэтому поле заполняется по-настоящему, нажатие кнопки только считается, окно закрывается, а смену выставляет тест.
        run.Field(open, UiDrive.Find<TextBox>(open, "OpeningCashBox"), "0", "остаток в кассе");
        var button = UiDrive.ButtonByText(open, "Открыть смену");
        run.Clicks++;
        run.Note("щелчок: «Открыть смену» (посчитан; смену выставляет тест — поддельный сервер её не открывает)");
        open.Close();
        _shiftOpenedByRobot = true;
    });

    private bool _shiftOpenedByRobot;

    private void DriveCheckoutCash(Run run, bool byKeyboard) => WhenOpened<CheckoutDialog>(dialog =>
    {
        var vm = (NurMarketKassa.ViewModels.CheckoutViewModel)dialog.DataContext!;
        if (byKeyboard)
        {
            run.Key(dialog, PhysicalKey.Enter, "Enter — оплатить без сдачи");
            if (dialog.IsVisible)
            {
                run.Field(dialog, UiDrive.Find<TextBox>(dialog, "CashReceivedBox"), "1000", "получено");
                run.Key(dialog, PhysicalKey.Enter, "Enter — оплатить");
            }
        }
        else
        {
            run.Field(dialog, UiDrive.Find<TextBox>(dialog, "CashReceivedBox"), "1000", "получено");
            run.Click(dialog, UiDrive.ButtonByText(dialog, vm.PayButtonText), "«Оплатить» в окне оплаты");
        }
        if (dialog.IsVisible)
            run.Fail("окно оплаты не закрылось после «Оплатить»");
    });

    private void CheckPaid(Run run, Window main)
    {
        if (!WaitFor(() => Basket(main).Lines.Count == 0 && !Basket(main).IsBusy, 10000))
            run.Fail("после оплаты чек не очистился; открыто: " + OpenNames());
    }

    [AvaloniaFact]
    public async Task NewcomerRun_CountsStepsOfTypicalTasks()
    {
        await TestHost.WarmAsync();
        try { File.Delete(ProgressFile); } catch { /* нет файла */ }
        CaptureKassaLog();
        var runs = new List<Run>();
        var main = (MainWindow)ScreenCatalog.Get("main-kassa").Create();
        _main = main;
        try
        {
            using var _ = UiShot.Render(main, UiSize.Large);
            main.Activate();
            UiShot.Settle(10, 40);

            // ── 1. Первый вход → первая продажа (смена закрыта, один товар мышью) ──
            runs.Add(Guarded("1. Первый вход → первая продажа (1 товар, наличные)", run =>
            {
                SetShift(null);
                // Вход: почта, пароль, «Войти» — три действия (окно входа здесь не поднимается: кассир уже вошёл).
                run.Fields += 2;
                run.Typed += "cashier@test.kg".Length + "12345678".Length;
                run.Clicks++;
                run.Note("вход: почта, пароль, «Войти» (3 действия, посчитаны без нажатия)");
                DriveOpenShift(run);
                DriveCheckoutCash(run, byKeyboard: false);
                run.Click(main, Tile(main, "Хлеб"), "плитка «Хлеб»");
                if (!WaitFor(() => Basket(main).Lines.Count > 0, 1500) && _shiftOpenedByRobot)
                {
                    // В настоящей кассе после «Открыть смену» товар добавляется тем же щелчком — здесь щелчок повторяется без счёта.
                    SetShift(Guid.NewGuid().ToString());
                    UiShot.Settle(5, 40);
                    run.Note("повторный щелчок по плитке (не считается: в настоящей кассе товар добавляется первым щелчком после открытия смены)");
                    UiDrive.Click(main, Tile(main, "Хлеб"));
                }
                if (!WaitFor(() => Basket(main).Lines.Count > 0, 5000))
                {
                    run.Fail("товар не попал в чек; открыто: " + OpenNames());
                    return;
                }
                run.Click(main, PayButton(main), "«Оплатить»");
                CheckPaid(run, main);
            }));

            // ── 2. Три товара сканом, оплата с клавиатуры: скан → Enter → Enter ──
            runs.Add(Guarded("2. Продажа 3 товаров сканом (скан → Enter → Enter)", run =>
            {
                EnsureShift();
                DriveOpenShift(run);
                DriveCheckoutCash(run, byKeyboard: true);
                for (var i = 1; i <= 3; i++)
                {
                    run.Scan(main, "4600000000001", $"товар {i}");
                    WaitFor(() => TotalQuantity(main) >= i - 0.001, 3000);
                    Thread.Sleep(900); // пауза между сканами одного кода (защита кассы от двойного чтения — 800 мс)
                }
                if (!WaitFor(() => TotalQuantity(main) >= 3 - 0.001, 5000))
                {
                    run.Fail($"в чеке не 3 шт. (строк {Basket(main).Lines.Count}, штук {TotalQuantity(main):0.###}); открыто: " + OpenNames());
                    return;
                }
                Thread.Sleep(500); // Enter сразу за сканом касса считает частью скана
                run.Key(main, PhysicalKey.Enter, "Enter — «Оплатить»");
                CheckPaid(run, main);
            }));

            // ── 3. Весовой товар ──
            runs.Add(Guarded("3. Весовой товар (ручной вес, наличные)", run =>
            {
                EnsureShift();
                DriveOpenShift(run);
                DriveCheckoutCash(run, byKeyboard: false);
                WhenOpened<WeighedProductDialog>(weigh =>
                {
                    var box = weigh.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
                    run.Field(weigh, box, "1,5", "вес, кг");
                    run.Click(weigh, UiDrive.Find<Button>(weigh, "OkButton"), "«Добавить» в окне веса");
                });
                run.Click(main, Tile(main, "Яблоки"), "плитка «Яблоки»");
                if (!WaitFor(() => Basket(main).Lines.Count > 0, 5000))
                {
                    run.Fail("весовой товар не попал в чек; открыто: " + OpenNames());
                    return;
                }
                run.Click(main, PayButton(main), "«Оплатить»");
                CheckPaid(run, main);
            }));

            // ── 4. Смешанная оплата ──
            runs.Add(Guarded("4. Смешанная оплата (наличные + безнал)", run =>
            {
                EnsureShift();
                DriveOpenShift(run);
                WhenOpened<CheckoutDialog>(dialog =>
                {
                    var vm = (NurMarketKassa.ViewModels.CheckoutViewModel)dialog.DataContext!;
                    run.Click(dialog, UiDrive.Find<RadioButton>(dialog, "PayMixedOption"), "способ «Смешанная»");
                    run.Field(dialog, UiDrive.Find<TextBox>(dialog, "MixedCashBox"), "20", "наличными");
                    run.Field(dialog, UiDrive.Find<TextBox>(dialog, "MixedNonCashBox"), "25", "безналом");
                    run.Click(dialog, UiDrive.ButtonByText(dialog, vm.PayButtonText), "«Оплатить» в окне оплаты");
                    if (dialog.IsVisible)
                        run.Fail("окно оплаты не закрылось (смешанная)");
                });
                run.Click(main, Tile(main, "Хлеб"), "плитка «Хлеб»");
                if (!WaitFor(() => Basket(main).Lines.Count > 0, 5000))
                {
                    run.Fail("товар не попал в чек; открыто: " + OpenNames());
                    return;
                }
                run.Click(main, PayButton(main), "«Оплатить»");
                CheckPaid(run, main);
            }));

            // ── 5. Возврат последнего чека ──
            runs.Add(Guarded("5. Возврат чека целиком", run =>
            {
                EnsureShift();
                var saleRow = "{\"id\":\"sale-1001\",\"number\":1001,\"status\":\"paid\",\"total\":\"45.00\",\"created_at\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\",\"payment_method\":\"cash\"}";
                UiFake.Server.Json("GET", "/api/main/pos/sales/", 200, "{\"count\":1,\"next\":null,\"previous\":null,\"results\":[" + saleRow + "]}");
                var returned = false;
                WhenOpened<ReturnSaleDialog>(ret =>
                {
                    UiShot.Settle(12, 50);
                    var grid = UiDrive.Find<DataGrid>(ret, "SalesDataGrid");
                    var row = grid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => r.IsEffectivelyVisible);
                    if (row is null)
                    {
                        run.Fail("в окне возврата нет чеков");
                        ret.Close();
                        return;
                    }
                    run.Click(ret, row, "строка чека");
                    UiShot.Settle(10, 50);
                    var whole = UiDrive.Find<Button>(ret, "ReturnWholeReceiptButton");
                    if (!whole.IsEffectivelyVisible || !whole.IsEffectivelyEnabled)
                    {
                        run.Fail("кнопка «Вернуть весь чек» недоступна (строки чека не загрузились)");
                        ret.Close();
                        return;
                    }
                    run.Click(ret, whole, "«Вернуть весь чек»");
                    UiShot.Settle(10, 50);
                    returned = true;
                    if (ret.IsVisible)
                    {
                        run.Note("окно возврата осталось открытым — закрываем");
                        ret.Close();
                    }
                });
                WhenOpened<PosConfirmDialog>(confirm =>
                {
                    var yes = confirm.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Name == "PrimaryButton")
                        ?? confirm.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.IsDefault);
                    run.Click(confirm, yes, "подтверждение («" + UiDrive.TextOf(yes) + "»)");
                });
                var menu = main.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("header-menu") && b.IsEffectivelyVisible);
                run.Click(main, menu, "меню ☰");
                UiShot.Settle(6, 40);
                var mi = main.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "MiReturn" && b.IsEffectivelyVisible)
                    ?? main.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Classes.Contains("sidebar-menu-item")
                        && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text is { } text && text.StartsWith("Возврат", StringComparison.OrdinalIgnoreCase)));
                if (mi is null)
                {
                    run.Fail("пункт «Возврат» не виден в меню");
                    return;
                }
                run.Click(main, mi, "пункт «Возврат»");
                WaitFor(() => returned || run.Failure is not null, 8000);
                if (!returned)
                    run.Fail("возврат не выполнен; открыто: " + OpenNames());
            }));
        }
        finally
        {
            _run = null;
            PosLogger.Mirror = _savedMirror;
            CloseExtra();
            // 2026-11-27 (полный прогон 27.11: тест висел 10 мин до blame-hang): задачи 2–5 открывают смену (EnsureShift), а закрытие окна кассы при открытой смене
            // спрашивает «Смена не закрыта» синхронным окном (ShiftNotClosedDialog.Prompt — вложенный цикл диспетчера). Робот к этому моменту уже снят (_run = null),
            // отвечать некому — UiShot.Pump() не возвращался. Смена «закрывается» тестом до закрытия окна (как и выставлялась им), Dispose вернёт прежнюю.
            SetShift(null);
            Progress("== закрытие окна кассы");
            main.Close();
            UiShot.Pump();
            Progress("== окно кассы закрыто");
        }

        WriteReport(runs);
        Assert.True(runs.Count == 5, "прогон новичка не дошёл до конца");
    }

    private Run Guarded(string title, Action<Run> body)
    {
        var run = new Run(title);
        Progress("== " + title);
        _run = run;
        _drivers.Clear();
        _opened.Clear();
        // Сторож: окно, в котором «робот» застрял, закрывается через 20 с (и дальше каждые 5 с), чтобы прогон не висел.
        using var watchdog = new Timer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_run, run))
                return;
            var stuck = _opened.LastOrDefault(w => w.IsVisible);
            if (stuck is null)
                return;
            run.Fail("сторож закрыл зависшее окно " + stuck.GetType().Name);
            try { stuck.Close(); } catch { /* уже закрыто */ }
        }), null, 20000, 5000);
        try
        {
            body(run);
        }
        catch (Exception ex)
        {
            run.Fail(ex.GetType().Name + ": " + ex.Message);
        }
        UiShot.Settle(5, 30);
        if (_opened.Any(w => w.IsVisible))
        {
            run.Note("после задачи остались окна: " + OpenNames());
            CloseExtra();
            UiShot.Settle(3, 30);
        }
        _run = null;
        if (_main is not null && Basket(_main).Lines.Count > 0)
        {
            run.Note($"в чеке после задачи осталось строк: {Basket(_main).Lines.Count} — чек очищен для следующей задачи");
            WaitFor(() => !Basket(_main).IsBusy, 15000);
            ((NurMarketKassa.Interfaces.ICartService)TestHost.Services.GetService(typeof(NurMarketKassa.Interfaces.ICartService))!).ResetForNewReceipt();
            Basket(_main).RefreshFromCart();
            UiShot.Settle(3, 30);
        }
        if (run.Failure is not null)
            DumpKassaLog();
        else
            lock (KassaLog)
                KassaLog.Clear();
        return run;
    }

    private static readonly List<string> KassaLog = new();
    private static Action<string, string>? _savedMirror;

    /// <summary>Журнал кассы на время прогона — в память (PosLogger.Mirror): при неудаче задачи последние строки уходят в файл хода.</summary>
    private static void CaptureKassaLog()
    {
        _savedMirror = PosLogger.Mirror;
        PosLogger.Mirror = (message, category) =>
        {
            lock (KassaLog)
                KassaLog.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + category + "] " + message);
            _savedMirror?.Invoke(message, category);
        };
    }

    private static void DumpKassaLog()
    {
        List<string> lines;
        lock (KassaLog)
        {
            lines = KassaLog.TakeLast(30).ToList();
            KassaLog.Clear();
        }
        foreach (var line in lines)
            Progress("   журнал: " + (line.Length > 300 ? line[..300] : line).Replace('\r', ' ').Replace('\n', ' '));
    }

    private static void EnsureShift() => SetShift(PosApp.ActiveShiftId is { Length: > 0 } id ? id : Guid.NewGuid().ToString());

    /// <summary>Смена открыта/закрыта и для служб кассы (PosApp), и для окна кассы (IAppSession) — как после настоящего открытия смены.</summary>
    private static void SetShift(string? shiftId)
    {
        PosApp.ActiveShiftId = shiftId;
        ((NurMarketKassa.Ui.Shared.IAppSession)TestHost.Services.GetService(typeof(NurMarketKassa.Ui.Shared.IAppSession))!).ActiveShiftId = shiftId;
    }

    private static void WriteReport(List<Run> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Задача | Действий | из них: щелчки / сканы / клавиши / поля | Набрано символов | Итог |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in runs)
            sb.AppendLine($"| {r.Title} | {r.Actions} | {r.Clicks} / {r.Scans} / {r.Keys} / {r.Fields} | {r.Typed} | {(r.Failure is null ? "пройдено" : "НЕ пройдено: " + r.Failure)} |");
        sb.AppendLine();
        foreach (var r in runs)
        {
            sb.AppendLine("### " + r.Title);
            foreach (var line in r.Log)
                sb.AppendLine("- " + line);
            sb.AppendLine();
        }
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "newcomer-steps-latest.md"), sb.ToString(), Encoding.UTF8);
    }
}
