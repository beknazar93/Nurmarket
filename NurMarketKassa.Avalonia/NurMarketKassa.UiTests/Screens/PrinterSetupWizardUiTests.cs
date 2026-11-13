using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Services.PrinterSetup;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (владелец: «готовые драйверы для чековых принтеров и принтеров штрихкодов, в основном Xprinter»): мастер «Подключить принтер» и
/// окно «Принтер не печатает?» в Avalonia.Headless на ПОДМЕННОЙ системе (USB-принтеры, очереди, сеть, создание очереди и печать — подмены; настоящие принтеры
/// этого ПК не трогаются). 1024×600 и 1366×768, ru и ky: окно не выше и не шире экрана, текст не обрезан; сквозной проход шагов 1–5 для чека и этикетки,
/// отказ в разрешении Windows → «USB напрямую», поиск в сети, «Исправить» в диагностике. Снимки: %TEMP%\nurmarket-tests\ui-actual\printer-*.png.</summary>
[Collection("UiScenarios")]
public sealed class PrinterSetupWizardUiTests : IDisposable
{
    private const string Iface80 = @"\\?\usb#vid_0416&pid_5011#5&2a1b3c&0&1#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";
    private const string Iface365 = @"\\?\usb#vid_0fe6&pid_811e#5&99aa&0&2#{28d78fad-5a12-11d1-ae5b-0000f803a8c2}";

    private readonly (string Receipt, bool Enabled, int Paper, int Cw80, int D80, string Enc, int? Table, bool Drawer, int Pin, string? Label, string LabelLang, double Gap) _keep;

    public PrinterSetupWizardUiTests()
    {
        var p = UserPreferences.Instance;
        _keep = (p.ReceiptDevicePath, p.ReceiptEnabled, p.ReceiptPaperWidthMm, p.ReceiptCharWidth80, p.ReceiptDots80, p.ReceiptEncoding, p.ReceiptEscPosTable,
            p.CashDrawerEnabled, p.CashDrawerPin, p.LabelPrinterDevicePath, p.LabelRawLanguage, p.LabelGapMm);
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        (p.ReceiptDevicePath, p.ReceiptEnabled, p.ReceiptPaperWidthMm, p.ReceiptCharWidth80, p.ReceiptDots80, p.ReceiptEncoding, p.ReceiptEscPosTable,
            p.CashDrawerEnabled, p.CashDrawerPin, p.LabelPrinterDevicePath, p.LabelRawLanguage, p.LabelGapMm) = _keep;
        ModalShell.WorkingAreaForTests = null;
    }

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private sealed class FakeSystem : IPrinterSystem
    {
        public List<WindowsQueueInfo> Queues { get; } = new();
        public List<UsbPrinterDevice> Usb { get; } = new();
        public List<DiscoveredPrinter> Others { get; } = new();
        public PrinterStatusReport? Status { get; set; }
        public IReadOnlyList<WindowsQueueInfo> ListQueues() => Queues.ToList();
        public IReadOnlyList<UsbPrinterDevice> ListUsbPrinters() => Usb.ToList();
        public IReadOnlyList<DiscoveredPrinter> ListOtherPorts() => Others.ToList();
        public PrinterPortService.PortProbeResult Probe(string devicePath) => new(true, "● ok", "x");
        public Task<PrinterStatusReport?> QueryStatusAsync(string devicePath, PrinterLanguage language, CancellationToken ct) => Task.FromResult(Status);
    }

    private static FakeSystem XprinterPc()
    {
        var sys = new FakeSystem();
        sys.Usb.Add(new UsbPrinterDevice(Iface80, 0x0416, 0x5011, "Xprinter", "XP-80C", "USB001"));
        sys.Queues.Add(new WindowsQueueInfo("POS-58", "USB005", "POS-58 Driver", 0, 0, 0));
        sys.Queues.Add(new WindowsQueueInfo("Microsoft Print to PDF", "PORTPROMPT:", "Microsoft Print To PDF", 0, 0, 0));
        sys.Others.Add(new DiscoveredPrinter("🔌 COM3", "COM3"));
        return sys;
    }

    private static void SaveShot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), data.ToArray());
    }

    private static void Until(Func<bool> condition, int ms = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < deadline)
            UiShot.Settle(1, 20);
        condition().Should().BeTrue("условие должно выполниться");
    }

    private static void Fits(Window w, int width, int height, string tag, string shotName, AppLanguage lang)
    {
        UiShot.Settle(4);
        w.Bounds.Height.Should().BeLessThanOrEqualTo(height, $"окно не выше экрана ({tag})");
        w.Bounds.Width.Should().BeLessThanOrEqualTo(width, $"окно не шире экрана ({tag})");
        TextClipChecker.Find(w, shotName, lang == AppLanguage.Kyrgyz ? "ky" : "ru").Should().BeEmpty($"текст не обрезан ({tag})");
        foreach (var b in new[] { "WizardCancel", "WizardNext" }.Select(n => w.GetVisualDescendantsSafe<Button>(n)).Where(b => b is { IsEffectivelyVisible: true }))
        {
            var right = b!.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), w)!.Value;
            right.X.Should().BeLessThanOrEqualTo(w.Bounds.Width, $"кнопка {b.Name} внутри окна ({tag})");
            right.Y.Should().BeLessThanOrEqualTo(w.Bounds.Height, $"кнопка {b.Name} видна ({tag})");
        }
        SaveShot(w, shotName);
    }

    // ------------------------------------------------------------------ чековый: все шаги, создание очереди, пробный чек, сохранение

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Receipt_AllSteps_CreateQueue_TestPrint_Save(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(width, height));
        var sys = XprinterPc();
        var created = new List<(string Name, string Port)>();
        var sent = new List<(string Path, byte[] Bytes)>();
        var wizard = new PrinterSetupWizardWindow(PrinterRole.Receipt, sys)
        {
            PersistSettings = false,
            CreateQueueOverride = (name, port) =>
            {
                created.Add((name, port));
                sys.Queues.Add(new WindowsQueueInfo(name, port, PrinterQueueInstaller.GenericTextOnlyDriver, 0, 0, 0));
                return Task.FromResult(new ElevatedOutcome(ElevatedResult.Done));
            },
            SendOverride = (path, bytes) => { sent.Add((path, bytes)); return Task.CompletedTask; },
        };
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var tag = $"{width}x{height} {lang}";
        try
        {
            var task = wizard.ShowDialog<PrinterWizardResult?>(owner);
            UiShot.Settle(8);
            wizard.Step.Should().Be(1);
            UiDrive.Find<RadioButton>(wizard, "WizardRoleReceipt").IsChecked.Should().BeTrue();
            Fits(wizard, width, height, tag, $"printer-wizard-1-{slug}", lang);

            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            var list = UiDrive.Find<ListBox>(wizard, "WizardPrinterList");
            Until(() => list.ItemCount > 0);
            var items = list.ItemsSource!.Cast<PrinterCandidate>().ToList();
            items.Should().NotContain(c => c.Title.Contains("PDF"), "виртуальный принтер не показывается");
            items[0].Kind.Should().Be(PrinterCandidateKind.UsbWithoutQueue, "USB без очереди — первым");
            list.SelectedItem.Should().Be(items[0], "единственный USB-принтер выбран сам");
            UiDrive.AllText(wizard).Should().Contain("XP-80C");
            Fits(wizard, width, height, tag, $"printer-wizard-2-{slug}", lang);

            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            wizard.Step.Should().Be(3);
            UiDrive.Find<Button>(wizard, "WizardNext").IsEnabled.Should().BeFalse("пока очереди нет — дальше нельзя");
            UiDrive.Find<TextBox>(wizard, "WizardQueueName").Text.Should().StartWith(PrinterQueueInstaller.QueuePrefix).And.Contain("80");
            Fits(wizard, width, height, tag, $"printer-wizard-3-{slug}", lang);
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardCreateQueue"));
            Until(() => created.Count == 1 && UiDrive.Find<Button>(wizard, "WizardNext").IsEnabled);
            created[0].Port.Should().Be("USB001");
            wizard.DevicePath.Should().Be(created[0].Name, "печать — в созданную очередь");

            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            wizard.Step.Should().Be(4);
            (UiDrive.Find<ComboBox>(wizard, "WizardPaper").SelectedItem as ComboBoxItem)?.Tag.Should().Be("80n", "XP-80 — 80 мм, 42 знака");
            Fits(wizard, width, height, tag, $"printer-wizard-4-{slug}", lang);

            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            wizard.Step.Should().Be(5);
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardTestPrint"));
            Until(() => sent.Count == 1);
            sent[0].Path.Should().Be(created[0].Name);
            sent[0].Bytes.Take(2).Should().Equal(new byte[] { 0x1B, 0x40 });
            UiDrive.Click(wizard, UiDrive.Find<RadioButton>(wizard, "WizardEnc2"));
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardPrintedBad"));
            UiShot.Settle(3);
            UiDrive.Find<Button>(wizard, "WizardProblemBothBad").IsEffectivelyVisible.Should().BeTrue("подсказки «что не так»");
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardProblemBothBad"));
            Fits(wizard, width, height, tag, $"printer-wizard-5-{slug}", lang);

            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardPrintedOk"));
            Until(() => task.IsCompleted);
            var result = await task;
            result.Should().NotBeNull();
            result!.Role.Should().Be(PrinterRole.Receipt);
            var p = UserPreferences.Instance;
            p.ReceiptDevicePath.Should().Be(created[0].Name);
            p.ReceiptEnabled.Should().BeTrue();
            p.ReceiptPaperWidthMm.Should().Be(80);
            p.ReceiptCharWidth80.Should().Be(42);
            p.ReceiptEncoding.Should().Be("wpc1251", "выбрана строка 2 (CP1251)");
            p.ReceiptEscPosTable.Should().BeNull();
        }
        finally
        {
            wizard.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ отказ в разрешении Windows → USB напрямую / драйвер

    [AvaloniaFact]
    public void UacRefused_OffersDirectUsbAndDriver()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ModalShell.WorkingAreaForTests = new Size(1024, 600);
        var owner = new Window();
        using var _shot = UiShot.Render(owner, new UiSize(1024, 600));
        var wizard = new PrinterSetupWizardWindow(PrinterRole.Receipt, XprinterPc())
        {
            PersistSettings = false,
            CreateQueueOverride = (_, _) => Task.FromResult(new ElevatedOutcome(ElevatedResult.Cancelled)),
        };
        try
        {
            _ = wizard.ShowDialog<PrinterWizardResult?>(owner);
            UiShot.Settle(6);
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            var list = UiDrive.Find<ListBox>(wizard, "WizardPrinterList");
            Until(() => list.SelectedItem is not null);
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            UiDrive.Find<Button>(wizard, "WizardDirectUsb").IsEffectivelyVisible.Should().BeFalse("до отказа не предлагается");
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardCreateQueue"));
            Until(() => UiDrive.Find<Button>(wizard, "WizardDirectUsb").IsEffectivelyVisible);
            UiDrive.Find<Button>(wizard, "WizardDriver").IsEffectivelyVisible.Should().BeTrue("и ссылка на драйвер производителя");
            UiDrive.Find<Button>(wizard, "WizardNext").IsEnabled.Should().BeFalse();
            UiDrive.AllText(wizard).Should().Contain("ничего не изменено");
            UiShot.Settle(3);
            var direct = UiDrive.Find<Button>(wizard, "WizardDirectUsb");
            var bottom = direct.TranslatePoint(new Point(0, direct.Bounds.Height), wizard)!.Value.Y;
            var footerTop = UiDrive.Find<Button>(wizard, "WizardNext").TranslatePoint(new Point(0, 0), wizard)!.Value.Y;
            bottom.Should().BeLessThan(footerTop, "после отказа кнопка «напрямую в USB» видна без прокрутки (1024×600)");
            SaveShot(wizard, "printer-wizard-uac-refused-ru-1024x600");
            UiDrive.Click(wizard, direct);
            UiDrive.Find<Button>(wizard, "WizardNext").IsEnabled.Should().BeTrue();
            wizard.DevicePath.Should().Be(Iface80, "печать напрямую в USB-интерфейс");
        }
        finally
        {
            wizard.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ этикетки: TSPL, пробная этикетка, поиск в сети

    [AvaloniaFact]
    public async Task Label_Tspl_TestLabel_Save_AndNetworkSearch()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Kyrgyz);
        ModalShell.WorkingAreaForTests = new Size(1366, 768);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(1366, 768));
        var sys = new FakeSystem();
        sys.Queues.Add(new WindowsQueueInfo("NurCRM Этикетки", "USB002", PrinterQueueInstaller.GenericTextOnlyDriver, 0, 0, 0));
        sys.Usb.Add(new UsbPrinterDevice(Iface365, 0x0FE6, 0x811E, "Xprinter", "XP-365B", "USB002"));
        var sent = new List<byte[]>();
        var wizard = new PrinterSetupWizardWindow(PrinterRole.Label, sys)
        {
            PersistSettings = false,
            SendOverride = (_, bytes) => { sent.Add(bytes); return Task.CompletedTask; },
            NetworkScanOverride = (progress, _) =>
            {
                progress.Report(new NetworkPrinterScanner.Progress(254, 254, 1));
                return Task.FromResult<IReadOnlyList<string>>(new[] { "TCP:192.168.1.77:9100" });
            },
        };
        try
        {
            var task = wizard.ShowDialog<PrinterWizardResult?>(owner);
            UiShot.Settle(6);
            UiDrive.Find<RadioButton>(wizard, "WizardRoleLabel").IsChecked.Should().BeTrue();
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            var list = UiDrive.Find<ListBox>(wizard, "WizardPrinterList");
            Until(() => list.ItemCount == 1);
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardScanNetwork"));
            Until(() => list.ItemCount == 2);
            list.ItemsSource!.Cast<PrinterCandidate>().Should().Contain(c => c.Kind == PrinterCandidateKind.Network && c.DevicePath == "TCP:192.168.1.77:9100");
            list.SelectedItem = list.ItemsSource!.Cast<PrinterCandidate>().First(c => c.Kind == PrinterCandidateKind.WindowsQueue);
            UiShot.Settle(2);
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            wizard.Step.Should().Be(3);
            UiDrive.Find<Button>(wizard, "WizardNext").IsEnabled.Should().BeTrue("очередь уже есть — создавать не нужно");
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            UiDrive.Find<RadioButton>(wizard, "WizardLangTspl").IsChecked.Should().BeTrue("XP-365B — TSPL");
            UiDrive.Find<ComboBox>(wizard, "WizardLabelSize").IsEffectivelyVisible.Should().BeTrue();
            UiDrive.Find<ComboBox>(wizard, "WizardPaper").IsEffectivelyVisible.Should().BeFalse("ширина ленты — только для чеков");
            SaveShot(wizard, "printer-wizard-label-4-ky-1366x768");
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext"));
            UiDrive.Find<RadioButton>(wizard, "WizardEnc1").IsEffectivelyVisible.Should().BeFalse("кодировка — только для чеков");
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardTestPrint"));
            Until(() => sent.Count == 1);
            System.Text.Encoding.ASCII.GetString(sent[0]).Should().StartWith("SIZE ");
            UiDrive.Click(wizard, UiDrive.Find<Button>(wizard, "WizardNext")); // «Сохранить»
            Until(() => task.IsCompleted);
            (await task)!.Role.Should().Be(PrinterRole.Label);
            UserPreferences.Instance.LabelPrinterDevicePath.Should().Be("NurCRM Этикетки");
            UserPreferences.Instance.LabelRawLanguage.Should().Be("tspl");
        }
        finally
        {
            wizard.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ «Принтер не печатает?»

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void Diagnostics_ShowsFindings_FixButtons_FitsScreen(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var owner = new Window();
        using var _shot = UiShot.Render(owner, new UiSize(width, height));
        var sys = new FakeSystem();
        sys.Queues.Add(new WindowsQueueInfo("POS-80", "USB001", "POS-80 11.3.0.1", Status: 0x1, Attributes: 0x400, Jobs: 4));
        sys.Usb.Add(new UsbPrinterDevice(Iface80, 0x0416, 0x5011, "Xprinter", "XP-80C", "USB002"));
        var fixes = new List<(PrinterFix Fix, string Queue, string? Arg)>();
        var window = new PrinterDiagnosticsWindow("POS-80", PrinterRole.Receipt, sys)
        {
            FixOverride = (fix, queue, arg) =>
            {
                fixes.Add((fix, queue, arg));
                if (fix == PrinterFix.MovePort)
                {
                    sys.Queues[0] = sys.Queues[0] with { PortName = arg!, Status = 0, Attributes = 0, Jobs = 0 };
                }
                return Task.FromResult(new ElevatedOutcome(ElevatedResult.Done));
            },
        };
        var slug = $"{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}";
        var tag = $"{width}x{height} {lang}";
        try
        {
            _ = window.ShowDialog<bool>(owner);
            Until(() => window.Findings.Count > 0 && window.Running is { IsCompleted: true });
            UiShot.Settle(4);
            window.Findings.Should().Contain(f => f.Fix == PrinterFix.ResumeQueue);
            window.Findings.Should().Contain(f => f.Fix == PrinterFix.ClearJobs);
            window.Findings.Should().Contain(f => f.Fix == PrinterFix.MovePort && f.FixArg == "USB002");
            UiDrive.Find<Button>(window, "DiagFixMovePort").IsEffectivelyVisible.Should().BeTrue();
            window.Bounds.Height.Should().BeLessThanOrEqualTo(height, tag);
            window.Bounds.Width.Should().BeLessThanOrEqualTo(width, tag);
            TextClipChecker.Find(window, $"printer-diag-{slug}", lang == AppLanguage.Kyrgyz ? "ky" : "ru").Should().BeEmpty($"текст не обрезан ({tag})");
            SaveShot(window, $"printer-diag-{slug}");

            UiDrive.Click(window, UiDrive.Find<Button>(window, "DiagFixMovePort"));
            Until(() => fixes.Count == 1 && window.Running is { IsCompleted: true } && !window.Findings.Any(f => f.Fix == PrinterFix.MovePort));
            fixes[0].Should().Be((PrinterFix.MovePort, "POS-80", "USB002"));
            window.Findings.Should().NotContain(f => f.Level == FindingLevel.Error, "после переключения порта ошибок нет");
            SaveShot(window, $"printer-diag-fixed-{slug}");
        }
        finally
        {
            window.Close();
            owner.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ кнопки на странице «Печать»

    [AvaloniaTheory]
    [InlineData(1024, 600, AppLanguage.Russian)]
    [InlineData(1024, 600, AppLanguage.Kyrgyz)]
    public void PrintSettingsPage_HasWizardDiagnosticsDriverButtons_AndUnconfiguredHint(int width, int height, AppLanguage lang)
    {
        UiShot.Setup(UiTheme.Light, lang);
        var view = new PrintSettingsView();
        var window = new Window { Content = view };
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            view.ReceiptSetupBar.RefreshReceiptHint("");
            view.LabelSetupBar.RefreshLabelHint(null);
            UiShot.Settle(4);
            foreach (var name in new[] { "BtnPrinterWizard", "BtnPrinterDiag", "BtnPrinterDriver", "BtnLabelPrinterWizard", "BtnLabelPrinterDiag", "BtnLabelPrinterDriver" })
                UiDrive.Find<Button>(window, name).IsEffectivelyVisible.Should().BeTrue($"кнопка {name} видна");
            UiDrive.AllText(window).Should().Contain(lang == AppLanguage.Kyrgyz ? "Чек принтери азырынча жөндөлгөн эмес" : "Принтер чеков ещё не настроен");
            view.ReceiptSetupBar.RefreshReceiptHint("POS-80");
            UiShot.Settle(2);
            UiDrive.AllText(window).Should().NotContain(lang == AppLanguage.Kyrgyz ? "Чек принтери азырынча жөндөлгөн эмес" : "Принтер чеков ещё не настроен", "очередь задана — подсказки нет");
            SaveShot(window, $"printer-settings-page-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{width}x{height}");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Diagnostics_RunWizardFix_ClosesWithTrue()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        ModalShell.WorkingAreaForTests = new Size(1024, 600);
        var owner = new Window();
        using var _ = UiShot.Render(owner, new UiSize(1024, 600));
        var window = new PrinterDiagnosticsWindow("", PrinterRole.Label, new FakeSystem());
        try
        {
            var task = window.ShowDialog<bool>(owner);
            Until(() => window.Findings.Count > 0);
            UiShot.Settle(3);
            UiDrive.Click(window, UiDrive.Find<Button>(window, "DiagFixRunWizard"));
            Until(() => task.IsCompleted);
            (await task).Should().BeTrue("«Исправить» → открыть мастер");
        }
        finally
        {
            window.Close();
            owner.Close();
            UiShot.Pump();
        }
    }
}

internal static class PrinterUiTestExtensions
{
    public static T? GetVisualDescendantsSafe<T>(this Window w, string name) where T : Control =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<T>().FirstOrDefault(c => c.Name == name);
}
