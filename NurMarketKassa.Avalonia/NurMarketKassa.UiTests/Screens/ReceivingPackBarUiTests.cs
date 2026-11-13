using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (владелец: «упаковку тоже сделай СВЕРХУ, если поместится!»): способ приёмки («В штуках / Упаковками / Поштучная продажа») на верхней панели обеих приёмок.
/// Проверяем в настоящих окнах (Avalonia.Headless): помещается без среза и без выхода за окно на 1024×600 / 1366×768 / 1900×1060 (ru, ky и все 5 языков), смена способа не меняет высоту
/// верхней панели и не сдвигает таблицу, сама панель с переключателем не выше, чем без него; способ по умолчанию применяется к НОВОЙ строке, «Ко всем строкам» — с вопросом.
/// 2026-11-12 (аудит Л-9, решение владельца): способ НЕ запоминается между открытиями и днями — новое окно «В штуках», после проведения и в новый день сброс; не «В штуках» — подсветка и метка у поля скана;
/// F4 / стрелки меняют способ с клавиатуры; фокус после выбора — в поле скана; настоящий повторный скан; «Вернуть» после «Ко всем строкам»; 4 строки таблицы на 1024×600.</summary>
[Collection("UiScenarios")]
public sealed class ReceivingPackBarUiTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768), (1900, 1060) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    public static IEnumerable<object[]> AllLanguages() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek }
            .SelectMany(l => new[] { (1024, 600), (1366, 768), (1900, 1060) }.Select(s => new object[] { s.Item1, s.Item2, l }));

    // ------------------------------------------------------------------ вспомогательное

    private static object WarehouseBar(WarehouseWindow window) => typeof(WarehouseWindow).GetField("_rcvPackBar", Any)!.GetValue(window)!;

    private static object PurchasesBar(PurchasesWindow window) => typeof(PurchasesWindow).GetField("_purchasePackBar", Any)!.GetValue(window)!;

    private static Control? Focused(Window w) => TopLevel.GetTopLevel(w)!.FocusManager!.GetFocusedElement() as Control;

    private static void PressKey(Window w, Avalonia.Input.Key key, Avalonia.Input.PhysicalKey physical, Avalonia.Input.RawInputModifiers mods = Avalonia.Input.RawInputModifiers.None)
    {
        w.KeyPress(key, mods, physical, null);
        w.KeyRelease(key, mods, physical, null);
        UiShot.Settle(8);
    }

    /// <summary>Скан через поле скана, как у кассира: текст и Enter.</summary>
    private static void ScanViaField(Window w, TextBox scan, string code)
    {
        scan.Text = code;
        scan.Focus();
        UiShot.Pump();
        w.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        UiShot.Settle(6, 20);
    }

    private static async Task Within(Task task, int ms = 4000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!task.IsCompleted && DateTime.UtcNow < until)
            UiShot.Settle(1, 30);
        task.IsCompleted.Should().BeTrue("задача завершилась (диалог закрыт / действие выполнено)");
        await task;
    }

    /// <summary>2026-11-13 (зависание теста): «Ко всем строкам» открывает вопрос через PosDialogHost.ShowModalAsync → Show → вложенный цикл диспетчера (Dispatcher.MainLoop). В Headless он стартует
    /// внутри Settle и не возвращает управление тесту, пока окно открыто, — тест не мог нажать кнопку и висел навсегда. Как «автопилот» OwnerReceiptReturnUiTests: ответ даёт таймер внутри
    /// вложенного цикла. Возвращает текст вопроса (null — вопроса не было). Касса тут ни при чём: у кассира тот же вложенный цикл — обычный цикл программы.</summary>
    private static async Task<string?> AnswerQuestionAsync(Window window, Task task, string button)
    {
        string? text = null;
        var timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(60), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
        {
            if (text is null && window.OwnedWindows.OfType<NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmDialog>().FirstOrDefault(d => d.IsVisible) is { } q)
            {
                text = UiDrive.AllText(q);
                Log("вопрос: " + text.Replace((char)10, (char)32));
                UiDrive.ButtonByText(q, button).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
        });
        timer.Start();
        try
        {
            await Within(task, 8000);
        }
        finally
        {
            timer.Stop();
        }
        return text;
    }

    private static void SetClock(Func<DateTime> clock) =>
        // 2026-11-12: ReceivingPackBar — internal (CS0122, проект тестов не собирался) — берём тип по имени из сборки кассы.
        typeof(WarehouseWindow).Assembly.GetType("NurMarketKassa.AvaloniaHost.Views.ReceivingPackBar", throwOnError: true)!
            .GetField("Clock", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, clock);

    private static Func<DateTime> RealClock() => () => DateTime.Now;

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SkiaSharp.SKImage.FromBitmap(shot);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static bool WaitFor(Func<bool> condition, int ms = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until)
            UiShot.Settle(1, 40);
        return condition();
    }

    private static T Get<T>(object o, string property) => (T)o.GetType().GetProperty(property, Any)!.GetValue(o)!;

    private static void Call(object o, string method, params object?[] args) => o.GetType().GetMethod(method, Any)!.Invoke(o, args);

    private static ReceivingPackDefaultMode ModeOf(object bar) => Get<ReceivingPackDefaultMode>(bar, "Mode");

    private static void Log(string text)
    {
        if (Environment.GetEnvironmentVariable("PACKBAR_LOG") is { Length: > 0 } path)
            File.AppendAllText(path, text + Environment.NewLine);
    }

    private static string LangCode(AppLanguage lang) => lang switch { AppLanguage.Russian => "ru", AppLanguage.Kyrgyz => "ky", AppLanguage.English => "en", AppLanguage.Turkish => "tr", _ => "uz" };

    private static CatalogProductTileVm Tile(string id, string code, string title = "Вода питьевая 0,5 л") =>
        new(id, title, "30 сом", false) { Barcode = code, Unit = "шт", Quantity = 10, PurchasePrice = 20 };

    /// <summary>Положение переключателя в границах контейнера и окна, виден ли, не обрезаны ли его надписи.</summary>
    private static void CheckFits(Window window, Control host, Control container, int width, string tag)
    {
        host.IsEffectivelyVisible.Should().BeTrue($"переключатель виден ({tag})");
        host.Bounds.Width.Should().BeGreaterThan(40, tag);
        var topLeft = host.TranslatePoint(new Point(0, 0), container)!.Value;
        var bottomRight = host.TranslatePoint(new Point(host.Bounds.Width, host.Bounds.Height), container)!.Value;
        topLeft.X.Should().BeGreaterThanOrEqualTo(-0.5, $"слева от переключателя нет среза ({tag})");
        bottomRight.X.Should().BeLessThanOrEqualTo(container.Bounds.Width + 0.5, $"справа от переключателя нет среза: {bottomRight.X:0} из {container.Bounds.Width:0} ({tag})");
        bottomRight.Y.Should().BeLessThanOrEqualTo(container.Bounds.Height + 0.5, $"переключатель не вылезает вниз за контейнер ({tag})");
        var inWindow = host.TranslatePoint(new Point(host.Bounds.Width, host.Bounds.Height), window)!.Value;
        inWindow.X.Should().BeLessThanOrEqualTo(width + 0.5, $"переключатель внутри окна ({tag})");
        // каждая кнопка и каждое поле переключателя целиком внутри контейнера
        foreach (var c in host.GetVisualDescendants().OfType<Control>().Where(c => c is Button or RadioButton or TextBox && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Opacity > 0.01))
        {
            var br = c.TranslatePoint(new Point(c.Bounds.Width, c.Bounds.Height), container)!.Value;
            br.X.Should().BeLessThanOrEqualTo(container.Bounds.Width + 0.5, $"«{UiDrive.TextOf(c)}» внутри границ ({tag})");
        }
        // 2026-11-12 (Т-14): фильтр не должен молча отбросить всё — надписи переключателя обязаны найтись (сегменты или кнопка), и проверяем каждую по границам её TextBlock.
        var barTexts = host.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
        barTexts.Should().NotBeEmpty($"надписи переключателя найдены ({tag})");
        var clipped = TextClipChecker.Find(window, "receiving-packbar", LangCode(UserPreferences.Instance.Language))
            .Where(v => barTexts.Any(t => v.Key.Contains(t.Text!, StringComparison.Ordinal) || v.Detail.Contains(t.Text!, StringComparison.Ordinal))).ToList();
        clipped.Should().BeEmpty($"надписи переключателя не обрезаны ({tag})");
        foreach (var t in barTexts.Where(t => t.TextWrapping == TextWrapping.NoWrap && t.TextTrimming == TextTrimming.None))
        {
            var need = new FormattedText(t.Text!, System.Globalization.CultureInfo.CurrentUICulture, t.FlowDirection,
                new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, null).Width;
            t.Bounds.Width.Should().BeGreaterThanOrEqualTo(need - 3, $"«{t.Text}» целиком ({tag})");
        }
    }

    // ------------------------------------------------------------------ «Склад → Приёмка»: помещается, высота не меняется

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Warehouse_TopBar_FitsWithoutClip_AndModeChangeKeepsTopPanelHeight(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var bar = WarehouseBar(window);
            var host = Get<ContentControl>(bar, "Host");
            var panel = UiDrive.Find<Border>(window, "ReceivingCtlPanel");
            var wrap = UiDrive.Find<WrapPanel>(window, "ReceivingSupplierWrap"); // 2026-11-12 (Д-4): узкое окно — «Приём» в ряду «Поставщик»
            var toolsWrap = UiDrive.Find<WrapPanel>(window, "ReceivingToolsWrap");
            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            Control container = panel.IsVisible ? panel : wrap;
            var wide = Get<bool>(bar, "IsWide");
            Log($"Склад {tag}: панель {(panel.IsVisible ? "видна" : "в ряду «Поставщик»")}, вид {(wide ? "сегменты" : "кнопка")}, ширина вида {Get<double>(bar, "WideWidth"):0}, панель {panel.Bounds.Width:0}×{panel.Bounds.Height:0}, ряд кнопок {toolsWrap.Bounds.Height:0}, ряд «Поставщик» {wrap.Bounds.Height:0}, таблица сверху {grid.TranslatePoint(new Point(0, 0), window)!.Value.Y:0}");

            if (!panel.IsVisible)
                wide.Should().BeFalse($"в ряду «Поставщик» (узкое окно) — только кнопка «Приём ▾» ({tag})");
            if (width >= 1900)
                panel.IsVisible.Should().BeTrue(tag);
            CheckFits(window, host, container, width, tag);
            // 2026-11-13: переключатель лежит в сетке внутри панели (ReceivingCtlGrid) или прямо в ряду «Поставщик» — «на верхней панели» = панель среди его предков.
            host.GetVisualAncestors().Should().Contain(container, $"переключатель на верхней панели ({tag})");

            double Y() => grid.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var panelH = panel.IsVisible ? panel.Bounds.Height : 0;
            var wrapH = wrap.Bounds.Height;
            var toolsH = toolsWrap.Bounds.Height;
            var gridY = Y();
            foreach (var mode in new[] { ReceivingPackDefaultMode.Packs, ReceivingPackDefaultMode.PackSale, ReceivingPackDefaultMode.Pieces })
            {
                Call(bar, "Choose", mode);
                UiShot.Settle(8);
                ModeOf(bar).Should().Be(mode, tag);
                (panel.IsVisible ? panel.Bounds.Height : 0).Should().BeApproximately(panelH, 0.5, $"высота верхней панели не меняется при смене способа на {mode} ({tag})");
                wrap.Bounds.Height.Should().BeApproximately(wrapH, 0.5, $"высота ряда, где лежит переключатель, не меняется ({tag})");
                toolsWrap.Bounds.Height.Should().BeApproximately(toolsH, 0.5, $"высота ряда кнопок не меняется ({tag})");
                Y().Should().BeApproximately(gridY, 0.5, $"таблица не сдвигается ({tag})");
                CheckFits(window, host, container, width, tag + " " + mode);
            }

            // панель с переключателем не выше, чем без него
            host.IsVisible = false;
            UiShot.Settle(8);
            (panel.IsVisible ? panel.Bounds.Height : 0).Should().BeApproximately(panelH, 0.5, $"переключатель не увеличил верхнюю панель ({tag})");
            toolsWrap.Bounds.Height.Should().BeApproximately(toolsH, 0.5, $"переключатель не добавил ряд в панель кнопок ({tag})");
            Y().Should().BeApproximately(gridY, 0.5, $"таблица на месте и без переключателя: он не добавил ряд ({tag})");
            host.IsVisible = true;
            UiShot.Settle(6);
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllLanguages))]
    public async Task Warehouse_TopBar_AllFiveLanguages_NoClip(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var bar = WarehouseBar(window);
            var host = Get<ContentControl>(bar, "Host");
            var panel = UiDrive.Find<Border>(window, "ReceivingCtlPanel");
            var wrap = UiDrive.Find<WrapPanel>(window, "ReceivingSupplierWrap");
            Log($"Склад {tag}: вид {(Get<bool>(bar, "IsWide") ? "сегменты" : "кнопка")}, панель {(panel.IsVisible ? "видна" : "в ряду «Поставщик»")}");
            CheckFits(window, host, panel.IsVisible ? panel : wrap, width, tag);
            // в каждом из режимов надписи выбранного и невыбранного сегмента на месте
            foreach (var mode in new[] { ReceivingPackDefaultMode.Packs, ReceivingPackDefaultMode.PackSale })
            {
                Call(bar, "Choose", mode);
                Call(bar, "SetDefaultSize", 12.0, true, null);
                UiShot.Settle(8);
                CheckFits(window, host, panel.IsVisible ? panel : wrap, width, tag + " " + mode);
            }
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Склад → Приёмка»: способ — новой строке, «Ко всем», запоминание

    [AvaloniaFact]
    public async Task Warehouse_DefaultMode_GoesToNewLines_RealRepeatScan_ApplyAllAsksWhenValuesEntered()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("w1", "4600000000011", "Вода питьевая 0,5 л"));
        CatalogCacheService.Products.Add(Tile("w2", "4600000000028", "Сок яблочный 1 л"));
        CatalogCacheService.Products.Add(Tile("w3", "4600000000035", "Чай чёрный"));
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var packSizesDir = Path.Combine(Path.GetTempPath(), "nm-packbar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packSizesDir);
        ReceivingPackSizes.DataDirOverride = packSizesDir;
        ReceivingPackSizes.Reset();
        var oldAsk = UserPreferences.Instance.AskQtyOnRepeatScan;
        UserPreferences.Instance.AskQtyOnRepeatScan = false;
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(14);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            var bar = WarehouseBar(window);

            // 1. по умолчанию «В штуках»: новая строка как раньше
            Log("STEP 1");
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Pieces);
            var first = vm.AddReceivingProduct(Tile("w1", "4600000000011", "Вода питьевая 0,5 л"), 1);
            first.PackMode.Should().BeFalse();
            first.Quantity.Should().Be(1);

            // 2. «Упаковками» + 12 по умолчанию → новая строка (список) и скан сразу в упаковках
            Log("STEP 2");
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            Call(bar, "SetDefaultSize", 12.0, true, null);
            UiShot.Settle(6);
            var second = vm.AddReceivingProduct(Tile("w2", "4600000000028", "Сок яблочный 1 л"), 1);
            second.PackMode.Should().BeTrue("новая строка получила способ с верхней панели");
            second.PackSale.Should().BeFalse();
            second.PiecesPerPack.Should().Be(12);
            second.Quantity.Should().Be(12, "1 упаковка × 12 шт");
            var scanBox = UiDrive.Find<TextBox>(window, "ReceivingScanBox");
            ScanViaField(window, scanBox, "4600000000035");
            WaitFor(() => vm.ReceivingLines.Any(l => l.ProductId == "w3")).Should().BeTrue("скан добавил строку");
            UiShot.Settle(10);
            var scanned = vm.ReceivingLines.First(l => l.ProductId == "w3");
            scanned.PackMode.Should().BeTrue("скан — тоже новая строка");
            scanned.PiecesPerPack.Should().Be(12);
            first.PackMode.Should().BeFalse("уже добавленная строка сама не меняется");
            ((string?)UiDrive.AllText(window)).Should().Contain("упак.", "в строке виден способ (ссылка «упак.→шт ▾»)");

            // 3. 2026-11-12 (Т-13): НАСТОЯЩИЙ повторный скан того же кода — не новая строка, +1 упаковка, способ остаётся
            Log("STEP 3");
            scanned.PackCount.Should().BeApproximately(1, 1e-9);
            ScanViaField(window, scanBox, "4600000000035");
            vm.ReceivingLines.Count(l => l.ProductId == "w3").Should().Be(1, "повторный скан не создал вторую строку");
            scanned.PackMode.Should().BeTrue("повторный скан не меняет способ строки");
            scanned.PackCount.Should().BeApproximately(2, 1e-9, "повторный скан прибавил упаковку");
            scanned.Quantity.Should().Be(24, "штуки: 2 упаковки × 12");
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs, "способ панели от повторного скана не меняется");
            first.PackMode.Should().BeFalse("и другие строки тоже");

            // 4. «Ко всем строкам»: у строки «first» (в штуках, количество 1, цен нет — ничего не введено) — без вопроса; «Применить» меняет
            Log("STEP 4");
            first.PurchasePrice = 0;
            first.SalePrice = 0;
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            await Within((Task)bar.GetType().GetMethod("ApplyToAllAsync", Any)!.Invoke(bar, null)!);
            window.OwnedWindows.OfType<NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmDialog>().Should().BeEmpty();
            UiShot.Settle(8);
            first.PackMode.Should().BeTrue("«Ко всем строкам» применил способ к строке без введённых чисел сразу");
            first.PiecesPerPack.Should().Be(12);

            // 5. строка с введённым количеством: «Ко всем строкам» сперва спрашивает; «Отмена» ничего не меняет, «Применить» меняет
            Log("STEP 5");
            second.SetPackMode(false);
            second.Quantity = 7;
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            var task = (Task)bar.GetType().GetMethod("ApplyToAllAsync", Any)!.Invoke(bar, null)!;
            var asked = await AnswerQuestionAsync(window, task, "Отмена");
            asked.Should().NotBeNull("в строке уже введено количество — спросили");
            asked.Should().Contain("пересчита");
            UiShot.Settle(6);
            second.PackMode.Should().BeFalse("«Отмена» — строка не тронута");
            second.Quantity.Should().Be(7);

            task = (Task)bar.GetType().GetMethod("ApplyToAllAsync", Any)!.Invoke(bar, null)!;
            (await AnswerQuestionAsync(window, task, "Применить")).Should().NotBeNull();
            UiShot.Settle(8);
            second.PackMode.Should().BeTrue("«Применить» — способ включён");
            second.Quantity.Should().Be(7, "введённое число остаётся, пересчитывается в упаковки");

            // 6. 2026-11-12 (Л-9): способ НЕ переживает перезапуск; размер «шт в упак.» и «Подробнее ▸» — переживают
            Log("STEP 6");
            ReceivingViewPrefs.SetDetails(true);
            Call(bar, "Choose", ReceivingPackDefaultMode.PackSale);
            ReceivingViewPrefs.ReloadForTests();
            ReceivingViewPrefs.PackSize.Should().Be(12, "размер запомнен");
            ReceivingViewPrefs.Details.Should().BeTrue("запись размера не стёрла «Подробнее ▸»");
        }
        finally
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = oldAsk;
            ReceivingPackSizes.DataDirOverride = null;
            ReceivingPackSizes.Reset();
            try { Directory.Delete(packSizesDir, true); } catch { }
            ReceivingViewPrefs.ResetForTests();
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Повторный скан в режиме «Упаковками» с окном «Сколько?»: количество — в упаковках, прибавляется к той же строке.</summary>
    [AvaloniaFact]
    public async Task Warehouse_RepeatScan_InPackMode_AsksPacks_AndAddsThemToTheSameLine()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("w3", "4600000000035", "Чай чёрный"));
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var dir = Path.Combine(Path.GetTempPath(), "nm-packbar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ReceivingPackSizes.DataDirOverride = dir;
        ReceivingPackSizes.Reset();
        var oldAsk = UserPreferences.Instance.AskQtyOnRepeatScan;
        UserPreferences.Instance.AskQtyOnRepeatScan = true;
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(14);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            vm.RepeatScanGuard.Reset();
            var bar = WarehouseBar(window);
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            Call(bar, "SetDefaultSize", 12.0, true, null);
            var scan = UiDrive.Find<TextBox>(window, "ReceivingScanBox");

            ScanViaField(window, scan, "4600000000035");
            var line = vm.ReceivingLines.Single();
            line.PackMode.Should().BeTrue();
            line.PackCount.Should().BeApproximately(1, 1e-9);

            ScanViaField(window, scan, "4600000000035"); // тот же код подряд — окно «Сколько?»
            var dialog = window.OwnedWindows.OfType<NurMarketKassa.AvaloniaHost.Views.Dialogs.ReceivingRepeatQtyDialog>().FirstOrDefault(d => d.IsVisible);
            dialog.Should().NotBeNull("повторный скан того же кода открывает окно количества");
            UiDrive.AllText(dialog!).Should().Contain("упак.", "в режиме «Упаковками» спрашиваем упаковки");
            UiDrive.Type(dialog!, dialog!.QuantityBox, "3");
            dialog.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
            UiShot.Settle(8, 20);
            line.PackCount.Should().BeApproximately(4, 1e-9, "1 + 3 упаковки");
            line.Quantity.Should().Be(48);
            vm.ReceivingLines.Count.Should().Be(1, "та же строка, а не новая");
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs);
        }
        finally
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = oldAsk;
            ReceivingPackSizes.DataDirOverride = null;
            ReceivingPackSizes.Reset();
            try { Directory.Delete(dir, true); } catch { }
            ReceivingViewPrefs.ResetForTests();
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Warehouse_SegmentClick_ChoosesMode_ReturnsFocusToScan_AndAFreshWindowStartsInPieces()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1900, 1060));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var bar = WarehouseBar(window);
            Get<bool>(bar, "IsWide").Should().BeTrue("на 1900×1060 помещаются сегменты");
            var segs = Get<ContentControl>(bar, "Host").GetVisualDescendants().OfType<RadioButton>().ToList();
            segs.Select(s => s.Content as string).Should().Equal("В штуках", "Упаковками", "Поштучная продажа");
            segs.Count(s => s.IsChecked == true).Should().Be(1);
            segs[0].IsChecked.Should().BeTrue();
            ToolTip.GetTip(segs[1]).Should().BeOfType<string>().Which.Should().Contain("шт в упак");
            segs.All(s => s.Focusable).Should().BeTrue("2026-11-12 (У-1): сегменты доступны с клавиатуры");

            UiDrive.Click(window, segs[1]);
            UiShot.Settle(8);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs, "нажатие сегмента выбрало способ");
            segs[1].IsChecked.Should().BeTrue();
            segs[0].IsChecked.Should().BeFalse();
            Focused(window).Should().BeSameAs(UiDrive.Find<TextBox>(window, "ReceivingScanBox"), "2026-11-12 (Т-13): после выбора способа курсор вернулся в поле скана");

            // поле «шт/упак.» прозрачно в «В штуках» и видно в «Упаковками»; ввод запоминается
            var size = Get<ContentControl>(bar, "Host").GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "шт/упак.");
            size.IsHitTestVisible.Should().BeTrue();
            UiDrive.Type(window, size, "24");
            UiShot.Settle(6);
            Get<double>(bar, "DefaultSize").Should().Be(24, "число сразу идёт в новые строки");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            UiShot.Settle(6);
            ReceivingViewPrefs.PackSize.Should().Be(24);
            Focused(window).Should().BeSameAs(UiDrive.Find<TextBox>(window, "ReceivingScanBox"), "Enter в поле размера возвращает курсор в поле скана");

            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
            ReceivingViewPrefs.ReloadForTests(); // как после перезапуска кассы
            var next = TestHost.Services.GetRequiredService<WarehouseWindow>();
            try
            {
                using var __ = UiShot.Render(next, new UiSize(1900, 1060));
                UiDrive.Find<TabControl>(next, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(next, "ReceivingTabItem");
                UiShot.Settle(16);
                var nextBar = WarehouseBar(next);
                ModeOf(nextBar).Should().Be(ReceivingPackDefaultMode.Pieces, "2026-11-12 (Л-9): новое открытие приёмки — «В штуках», вчерашний «Упаковками» не возвращается");
                Get<double>(nextBar, "DefaultSize").Should().Be(24, "а «шт в упак. по умолчанию» запомнено");
                Get<ContentControl>(nextBar, "Host").GetVisualDescendants().OfType<RadioButton>().Single(r => r.IsChecked == true).Content.Should().Be("В штуках");
            }
            finally
            {
                foreach (var owned in next.OwnedWindows.ToList())
                    owned.Close();
                next.Close();
                UiShot.Pump();
            }
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ Л-9: подсветка, метка, сброс после проведения и в новый день

    [AvaloniaTheory]
    [InlineData(1900, 1060)]
    [InlineData(1024, 600)]
    public async Task Warehouse_NonPiecesMode_IsHighlighted_AndScanFieldShowsABadge(int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var bar = WarehouseBar(window);
            var host = Get<ContentControl>(bar, "Host");
            var scan = UiDrive.Find<TextBox>(window, "ReceivingScanBox");
            var badge = Get<Control>(bar, "ScanBadge");
            var box = (Border)bar.GetType().GetField("_segBox", Any)!.GetValue(bar)!;
            var compact = (Button)bar.GetType().GetField("_compact", Any)!.GetValue(bar)!;

            scan.InnerRightContent.Should().BeSameAs(badge, "метка лежит в поле скана");
            badge.IsVisible.Should().BeFalse("в «В штуках» метки нет");
            Get<bool>(bar, "IsHighlighted").Should().BeFalse();
            var calmBorder = box.BorderBrush;
            var calmCompact = compact.BorderBrush;

            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            UiShot.Settle(10);
            Get<bool>(bar, "IsHighlighted").Should().BeTrue();
            badge.IsVisible.Should().BeTrue("«Упаковками» — метка у поля скана горит");
            badge.IsEffectivelyVisible.Should().BeTrue();
            badge.GetVisualDescendants().OfType<TextBlock>().Single().Text.Should().Be("УПАКОВКАМИ");
            ToolTip.GetTip(badge).Should().BeOfType<string>().Which.Should().Contain("одна упаковка").And.Contain("F4");
            if (Get<bool>(bar, "IsWide"))
                box.BorderBrush.Should().NotBeSameAs(calmBorder, "сегменты подсвечены тёплой рамкой");
            else
                compact.BorderBrush.Should().NotBeSameAs(calmCompact, "кнопка подсвечена тёплой рамкой");
            SaveShot(window, $"packbar-highlight-packs-{width}x{height}");

            Call(bar, "Choose", ReceivingPackDefaultMode.PackSale);
            UiShot.Settle(8);
            badge.GetVisualDescendants().OfType<TextBlock>().Single().Text.Should().Be("ПОШТУЧНАЯ ПРОДАЖА");

            Call(bar, "Choose", ReceivingPackDefaultMode.Pieces);
            UiShot.Settle(8);
            badge.IsVisible.Should().BeFalse("вернули «В штуках» — метка погасла");
            Get<bool>(bar, "IsHighlighted").Should().BeFalse();
            box.BorderBrush.Should().Be(calmBorder, "рамка вернулась обычной");
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Warehouse_AfterPosting_ModeReturnsToPieces_ButAnUnrelatedPropertyChangeDoesNot()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(14);
            var vm = (WarehouseViewModel)window.DataContext!;
            var bar = WarehouseBar(window);
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs);

            // посторонний признак — способ остаётся
            typeof(WarehouseViewModel).GetMethod("OnPropertyChanged", Any)!.Invoke(vm, new object?[] { "ReceivingBackgroundNotice" });
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs, "уведомление окна способ не сбрасывает");

            // приход поставлен в очередь: ViewModel включает IsReceivingPosting (WarehouseViewModel.CommitReceivingCoreAsync после Enqueue)
            typeof(WarehouseViewModel).GetField("_isReceivingPosting", Any)!.SetValue(vm, true);
            typeof(WarehouseViewModel).GetMethod("OnPropertyChanged", Any)!.Invoke(vm, new object?[] { "IsReceivingPosting" });
            UiShot.Settle(6);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Pieces, "после проведения — «В штуках»");
            Get<ContentControl>(bar, "Host").GetVisualDescendants().OfType<RadioButton>().Single(r => r.IsChecked == true).Content.Should().Be("В штуках");
            Get<Control>(bar, "ScanBadge").IsVisible.Should().BeFalse("метка погасла");
            typeof(WarehouseViewModel).GetField("_isReceivingPosting", Any)!.SetValue(vm, false);
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Warehouse_NewDay_ResetsTheModeBeforeTheNextNewLine()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("d1", "4600000000011"));
        CatalogCacheService.Products.Add(Tile("d2", "4600000000028", "Сок"));
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(14);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            var bar = WarehouseBar(window);
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            Call(bar, "SetDefaultSize", 12.0, true, null);

            vm.AddReceivingProduct(Tile("d1", "4600000000011"), 1).PackMode.Should().BeTrue("в тот же день способ действует");

            SetClock(() => DateTime.Now.AddDays(1)); // окно простояло через полночь
            var next = vm.AddReceivingProduct(Tile("d2", "4600000000028", "Сок"), 1);
            next.PackMode.Should().BeFalse("в новый день первая же новая строка — «В штуках»");
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Pieces);
            next.Quantity.Should().Be(1);
        }
        finally
        {
            SetClock(RealClock());
            ReceivingViewPrefs.ResetForTests();
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ У-1: с клавиатуры

    [AvaloniaFact]
    public async Task Warehouse_F4_ShiftF4_AndArrowKeys_ChangeTheMode_WithoutTheMouse()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1900, 1060));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var bar = WarehouseBar(window);
            var scan = UiDrive.Find<TextBox>(window, "ReceivingScanBox");
            scan.Focus();
            UiShot.Settle(4);

            PressKey(window, Avalonia.Input.Key.F4, Avalonia.Input.PhysicalKey.F4);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs, "F4 — следующий способ");
            Focused(window).Should().BeSameAs(scan, "курсор остался в поле скана — сканировать можно сразу");
            PressKey(window, Avalonia.Input.Key.F4, Avalonia.Input.PhysicalKey.F4);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.PackSale);
            PressKey(window, Avalonia.Input.Key.F4, Avalonia.Input.PhysicalKey.F4);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Pieces, "по кругу");
            PressKey(window, Avalonia.Input.Key.F4, Avalonia.Input.PhysicalKey.F4, Avalonia.Input.RawInputModifiers.Shift);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.PackSale, "Shift+F4 — назад");
            PressKey(window, Avalonia.Input.Key.F4, Avalonia.Input.PhysicalKey.F4, Avalonia.Input.RawInputModifiers.Control);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.PackSale, "Ctrl+F4 — не наша клавиша");
            Call(bar, "Choose", ReceivingPackDefaultMode.Pieces);

            // стрелки на сегментах: фокус остаётся на панели
            var segs = Get<ContentControl>(bar, "Host").GetVisualDescendants().OfType<RadioButton>().ToList();
            segs[0].Focus();
            UiShot.Settle(4);
            Focused(window).Should().BeSameAs(segs[0]);
            PressKey(window, Avalonia.Input.Key.Right, Avalonia.Input.PhysicalKey.ArrowRight);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs, "стрелка вправо");
            UiShot.Settle(8);
            Focused(window).Should().BeSameAs(segs[1], "фокус — на выбранном сегменте, можно жать дальше");
            PressKey(window, Avalonia.Input.Key.Right, Avalonia.Input.PhysicalKey.ArrowRight);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.PackSale);
            PressKey(window, Avalonia.Input.Key.Left, Avalonia.Input.PhysicalKey.ArrowLeft);
            PressKey(window, Avalonia.Input.Key.Left, Avalonia.Input.PhysicalKey.ArrowLeft);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Pieces, "стрелка влево");
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ Л-10 и Л-24

    [AvaloniaFact]
    public async Task Warehouse_PackSale_OverAWarehouseProduct_SaysOnceToUseTheLine_AndSizeMustBeAWholeNumberFromTwo()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("e1", "4600000000011", "Вода"));
        CatalogCacheService.Products.Add(Tile("e2", "4600000000028", "Сок"));
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1900, 1060));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            var bar = WarehouseBar(window);
            Call(bar, "Choose", ReceivingPackDefaultMode.PackSale);
            UiShot.Settle(6);

            // Л-24: размер для «поштучной продажи» — целое от 2
            var size = Get<ContentControl>(bar, "Host").GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "шт/упак.");
            UiDrive.Type(window, size, "1");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            UiShot.Settle(6);
            Get<double>(bar, "DefaultSize").Should().Be(0, "1 для поштучной продажи не принято");
            vm.ReceivingBackgroundNotice.Should().Contain("целое число от 2", "сказали почему");
            size.Text.Should().BeNullOrEmpty("в поле осталось прежнее принятое (пусто)");
            UiDrive.Type(window, size, "0,5");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            UiShot.Settle(6);
            Get<double>(bar, "DefaultSize").Should().Be(0, "0,5 тоже нет");
            UiDrive.Type(window, size, "12");
            UiShot.Settle(4);
            Get<double>(bar, "DefaultSize").Should().Be(12, "12 принято");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            UiShot.Settle(6);

            // Л-10: товар склада поштучной продажи не получает — и об этом сказано, один раз
            vm.ShowReceivingInfo("");
            var water = vm.AddReceivingProduct(Tile("e1", "4600000000011", "Вода"), 1);
            water.PackSale.Should().BeFalse("товару склада поштучную продажу включают в самой строке");
            vm.ReceivingBackgroundNotice.Should().Contain("в самой строке", "панель не промолчала");
            vm.ReceivingBackgroundNotice.Should().Contain("Поштучная продажа");
            vm.ShowReceivingInfo("");
            vm.AddReceivingProduct(Tile("e2", "4600000000028", "Сок"), 1);
            vm.ReceivingBackgroundNotice.Should().BeEmpty("второй раз подряд не повторяем — один раз на выбор способа");
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            Call(bar, "Choose", ReceivingPackDefaultMode.PackSale); // выбрали способ заново — можно напомнить снова
            vm.ReceivingLines.Clear();
            vm.AddReceivingProduct(Tile("e1", "4600000000011", "Вода"), 1);
            vm.ReceivingBackgroundNotice.Should().Contain("в самой строке");

            // «Упаковками» — Л-24 не мешает дробному размеру
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            UiShot.Settle(4);
            UiDrive.Type(window, size, "0,5");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            UiShot.Settle(6);
            Get<double>(bar, "DefaultSize").Should().Be(0.5, "для обычных упаковок дробное число допустимо, как раньше");
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ У-2: «Вернуть» после «Ко всем строкам»

    [AvaloniaFact]
    public async Task Warehouse_ApplyToAll_AsksWhenLinesHavePrices_AndCanBeUndone()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1900, 1060));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            var lines = Enumerable.Range(0, 3).Select(i => new ReceivingLineVm { ProductName = "Чай " + i, Barcode = "46020000010" + i, Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 20, SalePrice = 30 }).ToList();
            foreach (var l in lines)
                vm.ReceivingLines.Add(l);
            UiShot.Settle(8);
            var bar = WarehouseBar(window);
            Call(bar, "SetDefaultSize", 12.0, true, null);
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);

            var task = (Task)bar.GetType().GetMethod("ApplyToAllAsync", Any)!.Invoke(bar, null)!;
            (await AnswerQuestionAsync(window, task, "Применить")).Should().NotBeNull("2026-11-12 (У-2): в строках введены цены — спросили, хотя количество 1");
            UiShot.Settle(8);
            lines.Should().OnlyContain(l => l.PackMode && l.Quantity == 12);
            Get<bool>(bar, "CanUndo").Should().BeTrue("после «Ко всем строкам» есть «Вернуть»");
            var flyout = (Flyout)bar.GetType().GetField("_undoFlyout", Any)!.GetValue(bar)!;
            ((StackPanel)flyout.Content!).Children.OfType<TextBlock>().Single().Text.Should().Be("Изменено строк: 3.");

            ((bool)bar.GetType().GetMethod("UndoLastApply", Any)!.Invoke(bar, null)!).Should().BeTrue();
            UiShot.Settle(8);
            lines.Should().OnlyContain(l => !l.PackMode && l.Quantity == 1 && l.PurchasePrice == 20 && l.SalePrice == 30, "способ и числа вернулись");
            Get<bool>(bar, "CanUndo").Should().BeFalse();
            ((bool)bar.GetType().GetMethod("UndoLastApply", Any)!.Invoke(bar, null)!).Should().BeFalse("второй раз возвращать нечего");
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ Д-4: на 1024×600 видно не меньше 4 строк таблицы; Д-15: пустая таблица; Д-8: штрихкод целиком

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Kyrgyz)]
    public async Task Warehouse_Receiving_OnA1024x600Till_ShowsAtLeastFourTableRows_AndTheBarcodeInFull(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            for (var i = 0; i < 8; i++)
                vm.ReceivingLines.Add(new ReceivingLineVm { ProductName = "Товар " + i, Barcode = "46070000000" + (10 + i), Source = ReceivingSource.Unknown, Quantity = 2, PurchasePrice = 20, SalePrice = 30 });
            UiShot.Settle(14);
            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            var body = grid.Bounds.Height - grid.ColumnHeaderHeight;
            var rows = body / grid.RowHeight;
            Log($"Склад {tag}: таблица {grid.Bounds.Width:0}×{grid.Bounds.Height:0}, шапка {grid.ColumnHeaderHeight:0}, строка {grid.RowHeight:0}, видно строк {rows:0.0}, сверху {grid.TranslatePoint(new Point(0, 0), window)!.Value.Y:0}");
            rows.Should().BeGreaterThanOrEqualTo(4, $"Д-4: на 1024×600 видно не меньше 4 строк таблицы ({tag})");
            SaveShot(window, $"warehouse-receiving-rows-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");

            // если колонка «Штрихкод» показана — 13 цифр видны целиком, не обрезаны; на узком виде штрихкод — под названием (подстрока)
            var cells = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is { Length: >= 13 } text && text.StartsWith("4607000000", StringComparison.Ordinal)).ToList();
            cells.Should().NotBeEmpty($"штрихкоды строк найдены на экране ({tag})");
            var clip = TextClipChecker.Find(window, "receiving-barcode", lang.ToString()).Where(v => v.Key.Contains("46070000000", StringComparison.Ordinal)).ToList();
            clip.Should().BeEmpty($"Д-8: штрихкод не обрезан ({tag})");
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            ((WarehouseViewModel)window.DataContext!).ReceivingLines.Clear();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(1366, 768, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Kyrgyz)]
    public async Task Warehouse_EmptyReceiving_ExplanationIsEitherWhole_OrHiddenWhole(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var vm = (WarehouseViewModel)window.DataContext!;
            vm.ReceivingLines.Clear();
            UiShot.Settle(14);
            var grid = UiDrive.Find<DataGrid>(window, "ReceivingGrid");
            var state = UiDrive.Find<StackPanel>(window, "ReceivingEmptyState");
            state.IsVisible.Should().BeTrue($"таблица пуста — пояснение есть ({tag})");
            var gridBottom = grid.TranslatePoint(new Point(0, grid.Bounds.Height), window)!.Value.Y;
            var visible = state.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0 && (c is TextBlock || c is Border)).ToList();
            visible.Should().NotBeEmpty($"хотя бы заголовок виден ({tag})");
            foreach (var c in visible)
            {
                var bottom = c.TranslatePoint(new Point(0, c.Bounds.Height), window)!.Value.Y;
                bottom.Should().BeLessThanOrEqualTo(gridBottom + 0.5, $"{c.GetType().Name} не срезан снизу ({tag})");
            }
            var hint = UiDrive.Find<TextBlock>(window, "ReceivingEmptyHint");
            if (hint.IsEffectivelyVisible)
            {
                var need = new FormattedText(hint.Text!, System.Globalization.CultureInfo.CurrentUICulture, hint.FlowDirection, new Typeface(hint.FontFamily, hint.FontStyle, hint.FontWeight, hint.FontStretch), hint.FontSize, null)
                    { MaxTextWidth = hint.Bounds.Width }.Height;
                hint.Bounds.Height.Should().BeGreaterThanOrEqualTo(need - 4, $"пояснение показано целиком по высоте ({tag})");
                var right = hint.TranslatePoint(new Point(hint.Bounds.Width, 0), window)!.Value.X;
                right.Should().BeLessThanOrEqualTo(window.Bounds.Width, $"пояснение не вылезает вправо ({tag})");
            }
            TextClipChecker.Find(window, "receiving-empty", lang.ToString()).Where(v => v.Key.Contains("Отсканируйте", StringComparison.Ordinal) || v.Key.Contains("Строка появится", StringComparison.Ordinal)).Should().BeEmpty($"текст пояснения не срезан ({tag})");
            SaveShot(window, $"warehouse-receiving-empty-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ узкое окно: кнопка вместо сегментов, окно с тремя пунктами

    [AvaloniaFact]
    public async Task Warehouse_NarrowWindow_ShowsButtonWithThreeItemsInsteadOfSegments()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var window = TestHost.Services.GetRequiredService<WarehouseWindow>();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiDrive.Find<TabControl>(window, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(window, "ReceivingTabItem");
            UiShot.Settle(16);
            var bar = WarehouseBar(window);
            var host = Get<ContentControl>(bar, "Host");
            Get<bool>(bar, "IsWide").Should().BeFalse("на 1024×600 сегменты не помещаются — меню");
            host.GetVisualDescendants().OfType<RadioButton>().Should().BeEmpty();
            var button = host.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible);
            UiDrive.TextOf(button)!.Should().Contain("Приём:").And.Contain("в штуках").And.EndWith("▾");
            var flyout = (Avalonia.Controls.Flyout)bar.GetType().GetField("_flyout", Any)!.GetValue(bar)!;
            var items = ((StackPanel)flyout.Content!).Children.OfType<Button>().ToList();
            items.Count.Should().Be(4, "три способа и «Ко всем строкам»");
            items.Take(3).All(i => i.Focusable).Should().BeTrue("2026-11-12 (У-1): пункты окна доступны с клавиатуры");
            UiDrive.TextOf(items[0])!.Should().Contain("В штуках");
            UiDrive.TextOf(items[1])!.Should().Contain("Упаковками");
            UiDrive.TextOf(items[2])!.Should().Contain("Поштучная продажа");
            // 2026-11-12 (Т-14): окно открывается настоящим щелчком по кнопке, а не рефлексией; пункт — нажатием кнопки пункта
            flyout.IsOpen.Should().BeFalse();
            UiDrive.Click(window, button);
            UiShot.Settle(6);
            flyout.IsOpen.Should().BeTrue("щелчок по «Приём: … ▾» открыл окно с тремя пунктами");
            items[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UiShot.Settle(8);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs);
            UiDrive.TextOf(button)!.Should().Contain("упаковками");
            UiDrive.TextOf(items[1])!.Should().Contain("✓", "выбранный пункт отмечен");
        }
        finally
        {
            ReceivingViewPrefs.ResetForTests();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Закупки → Приём товара»

    private static List<ReceivingLineVm> PurchaseLines(PurchasesWindow window)
    {
        var rs = typeof(PurchasesWindow).GetField("_receive", Any)!.GetValue(window)!;
        return ((IEnumerable<ReceivingLineVm>)rs.GetType().GetField("Lines")!.GetValue(rs)!).ToList();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Purchases_TopBar_FitsWithoutClip_AndModeChangeKeepsToolsHeight(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ReceivingViewPrefs.ResetForTests();
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("pk1", "4600000000011"));
        var window = new PurchasesWindow();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", "Вода питьевая 0,5 л", "шт", 1.0, (double?)null) }));
            UiShot.Settle(16);
            var bar = PurchasesBar(window);
            var host = Get<ContentControl>(bar, "Host");
            var fastBox = (StackPanel)host.Parent!;
            var tools = (Grid)fastBox.Parent!;
            var wide = Get<bool>(bar, "IsWide");
            var twoRows = Grid.GetRow(fastBox) == 1;
            Log($"Закупки {tag}: вид {(wide ? "сегменты" : "кнопка")}, {(twoRows ? "два ряда" : "один ряд")}, ширина вида {Get<double>(bar, "WideWidth"):0}, панель {tools.Bounds.Width:0}×{tools.Bounds.Height:0}");
            var scan = tools.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Watermark is { Length: > 0 } wm && wm.Contains("штрихкод", StringComparison.OrdinalIgnoreCase));
            if (!twoRows)
                scan.Bounds.Width.Should().BeGreaterThanOrEqualTo(250, $"поле скана не сжато упаковкой ({tag})");
            if (width <= 1100)
                wide.Should().BeFalse($"на узком окне — кнопка ({tag})");

            // переключатель в границах панели (в одном ряду «Приём» — внутри fastBox, который внутри tools)
            CheckFits(window, host, tools, width, tag);
            var toolsH = tools.Bounds.Height;
            var cardTop = window.GetVisualDescendants().OfType<Border>().First(b => b.CornerRadius.TopLeft == 12 && b.ClipToBounds && b.IsEffectivelyVisible);
            double Y() => cardTop.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var y0 = Y();
            foreach (var mode in new[] { ReceivingPackDefaultMode.Packs, ReceivingPackDefaultMode.PackSale, ReceivingPackDefaultMode.Pieces })
            {
                Call(bar, "Choose", mode);
                UiShot.Settle(8);
                tools.Bounds.Height.Should().BeApproximately(toolsH, 0.5, $"высота панели не меняется при смене способа ({tag} {mode})");
                Y().Should().BeApproximately(y0, 0.5, $"таблица не сдвигается ({tag} {mode})");
                CheckFits(window, host, tools, width, tag + " " + mode);
            }
            host.IsVisible = false;
            UiShot.Settle(8);
            tools.Bounds.Height.Should().BeApproximately(toolsH, 0.5, $"переключатель не увеличил верхнюю панель ({tag})");
            host.IsVisible = true;
            UiShot.Settle(6);
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
            ReceivingViewPrefs.ResetForTests();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }

    [AvaloniaFact]
    public async Task Purchases_DefaultMode_GoesToNewLines_ButNotToPrefilledOrExistingOnes()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("pk1", "4600000000011", "Вода питьевая 0,5 л"));
        CatalogCacheService.Products.Add(Tile("pk2", "4600000000028", "Сок яблочный 1 л"));
        var dir = Path.Combine(Path.GetTempPath(), "nm-packbar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ReceivingPackSizes.DataDirOverride = dir;
        ReceivingPackSizes.Reset();
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", "Вода питьевая 0,5 л", "шт", 3.0, (double?)null) }));
            UiShot.Settle(14);
            var bar = PurchasesBar(window);
            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            Call(bar, "SetDefaultSize", 12.0, true, null);
            UiShot.Settle(6);

            PurchaseLines(window).Single().PackMode.Should().BeFalse("строка из заказа пришла до выбора и с готовым количеством — не трогаем");
            var add = typeof(PurchasesWindow).GetMethod("ReceiveAddTile", Any)!;
            add.Invoke(window, new object?[] { CatalogCacheService.Products.First(p => p.Id == "pk2"), 1.0, false, false });
            UiShot.Settle(10);
            var lines = PurchaseLines(window);
            var fresh = lines.First(l => l.ProductId == "pk2");
            fresh.PackMode.Should().BeTrue("новая строка получила способ с верхней панели");
            fresh.PiecesPerPack.Should().Be(12);
            fresh.Quantity.Should().Be(12);
            UiDrive.AllText(window).Should().Contain("упак.", "в строке виден способ");

            // та же строка добавлена снова — это не новая строка: упаковки не удваиваются и способ не пересчитывается
            add.Invoke(window, new object?[] { CatalogCacheService.Products.First(p => p.Id == "pk2"), 1.0, false, false });
            UiShot.Settle(8);
            PurchaseLines(window).Count(l => l.ProductId == "pk2").Should().Be(1);
            PurchaseLines(window).First(l => l.ProductId == "pk2").PiecesPerPack.Should().Be(12);
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
            ReceivingPackSizes.DataDirOverride = null;
            ReceivingPackSizes.Reset();
            try { Directory.Delete(dir, true); } catch { }
            ReceivingViewPrefs.ResetForTests();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }

    // ------------------------------------------------------------------ «Закупки → Приём товара»: Л-9, Д-9, У-1

    [AvaloniaFact]
    public async Task Purchases_NonPiecesMode_HasABadgeInTheScanField_ResetsAfterPosting_AndF4Cycles()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ReceivingViewPrefs.ResetForTests();
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("pk1", "4600000000011"));
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", "Вода питьевая 0,5 л", "шт", 1.0, (double?)null) }));
            UiShot.Settle(16);
            var bar = PurchasesBar(window);
            var badge = Get<Control>(bar, "ScanBadge");
            var scan = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Watermark is { Length: > 0 } wm && wm.Contains("штрихкод", StringComparison.OrdinalIgnoreCase));
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Pieces, "новое открытие приёма — «В штуках»");
            scan.InnerRightContent.Should().BeSameAs(badge, "метка лежит в поле скана");
            badge.IsVisible.Should().BeFalse();

            Call(bar, "Choose", ReceivingPackDefaultMode.Packs);
            UiShot.Settle(8);
            badge.IsVisible.Should().BeTrue("«Упаковками» — метка у поля скана горит");
            SaveShot(window, "packbar-purchases-packs-1366x768");

            // F4 — по кругу, курсор остаётся в поле скана
            scan.Focus();
            UiShot.Settle(4);
            PressKey(window, Avalonia.Input.Key.F4, Avalonia.Input.PhysicalKey.F4);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.PackSale, "F4 — следующий способ");
            PressKey(window, Avalonia.Input.Key.F4, Avalonia.Input.PhysicalKey.F4, Avalonia.Input.RawInputModifiers.Shift);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Packs, "Shift+F4 — назад");

            // проведено: способ возвращается «В штуках» (вызывает ReceivePostCoreAsync сразу после постановки в очередь)
            Call(bar, "ResetAfterPosting");
            UiShot.Settle(6);
            ModeOf(bar).Should().Be(ReceivingPackDefaultMode.Pieces);
            badge.IsVisible.Should().BeFalse();
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
            ReceivingViewPrefs.ResetForTests();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Kyrgyz)]
    [InlineData(1366, 768, AppLanguage.Russian)]
    public async Task Purchases_Receive_MoneyNeverBreaksBetweenNumberAndCurrency_AndTheTotalIsAtMostTwoLines(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        ReceivingViewPrefs.ResetForTests();
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.Add(Tile("pk1", "4600000000011", "Ак Кант 1 кг"));
        CatalogCacheService.Products.Add(Tile("pk2", "4600000000028", "Печенье"));
        var window = new PurchasesWindow();
        var tag = $"{width}x{height} {lang}";
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            window.OpenReceive(new ReceivePrefill(null, null, null, null, new[] { ("pk1", "Ак Кант 1 кг", "шт", 5.0, (double?)960.0), ("pk2", "Печенье", "шт", 12.0, (double?)18.5) }));
            UiShot.Settle(16);
            var sums = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is { } x && x.Contains("4\u00A0800,00", StringComparison.Ordinal)).ToList();
            sums.Should().NotBeEmpty($"сумма строки 5 × 960 = 4 800,00 с неразрывными пробелами найдена ({tag})");
            foreach (var t in sums.Where(t => t.FontWeight == FontWeight.SemiBold && !t.Text!.Contains('·')))
            {
                t.Text.Should().NotContain(" ", $"в сумме нет обычного пробела — перенос между числом и «сом» невозможен ({tag})");
                var oneLine = new FormattedText(t.Text!, System.Globalization.CultureInfo.CurrentUICulture, t.FlowDirection, new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, null).Height;
                t.Bounds.Height.Should().BeLessThanOrEqualTo(oneLine + 4, $"сумма «{t.Text}» в одну строку ({tag})");
            }
            var footer = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text is { } x && x.StartsWith(lang == AppLanguage.Kyrgyz ? "Позициялар" : "Позиций", StringComparison.Ordinal));
            var lineHeight = new FormattedText("Ag", System.Globalization.CultureInfo.CurrentUICulture, footer.FlowDirection, new Typeface(footer.FontFamily, footer.FontStyle, footer.FontWeight, footer.FontStretch), footer.FontSize, null).Height;
            footer.Bounds.Height.Should().BeLessThanOrEqualTo(lineHeight * 2 + 4, $"Д-9: итог слева не больше 2 строк ({tag})");
            if (width <= 1100)
                footer.Text.Should().NotContain(lang == AppLanguage.Kyrgyz ? "сатуу" : "продажа на", $"на узком окне итог короче ({tag})");
            SaveShot(window, $"receiving-purchases-total-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToList())
                owned.Close();
            window.Close();
            UiShot.Pump();
            ReceivingViewPrefs.ResetForTests();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }
}
