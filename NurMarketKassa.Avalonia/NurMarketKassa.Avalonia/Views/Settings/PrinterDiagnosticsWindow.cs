using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PrinterSetup;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-13 (диагностика «Принтер не печатает?»): проверки <see cref="PrinterDiagnostics"/> по выбранному принтеру чеков или этикеток —
/// строки «✔ / ℹ / ⚠ / ✖» понятным текстом и кнопка «Исправить» там, где можно (очистить очередь, снять паузу и «автономно», переключить очередь на новый порт USB —
/// с разрешением Windows; открыть мастер; страница драйверов). Ничего не меняет без нажатия. Результат ShowDialog: true — кассир попросил открыть мастер.</summary>
public sealed class PrinterDiagnosticsWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly IPrinterSystem _sys;
    private readonly string? _devicePath;
    private readonly PrinterRole _role;
    private readonly PrinterLanguage _language;
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly ModalShell _shell;
    private bool _closed;

    /// <summary>Подмена действий «Исправить» с правами администратора (тесты): (действие, очередь, порт) → итог.</summary>
    public Func<PrinterFix, string, string?, Task<ElevatedOutcome>>? FixOverride { get; set; }

    /// <summary>Последний итог (для тестов).</summary>
    public IReadOnlyList<DiagnosticFinding> Findings { get; private set; } = Array.Empty<DiagnosticFinding>();

    /// <summary>Идёт проверка (для тестов).</summary>
    public Task? Running { get; private set; }

    public PrinterDiagnosticsWindow(string? devicePath, PrinterRole role, IPrinterSystem? system = null)
    {
        _sys = system ?? PrinterSystem.Instance;
        _devicePath = devicePath;
        _role = role;
        _language = role == PrinterRole.Label
            ? (UserPreferences.Instance.LabelRawLanguage == "tspl" ? PrinterLanguage.Tspl : PrinterLanguage.Unknown)
            : PrinterLanguage.EscPos;
        Title = WindowTitle;
        Width = 672;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var close = new Button { Name = "DiagClose", Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), IsCancel = true };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Finish(false);
        var again = new Button { Name = "DiagAgain", Content = T("Проверить снова", "Кайра текшерүү", "Check again", "Yeniden kontrol et", "Qayta tekshirish"), IsDefault = true };
        again.Classes.Add("modal-primary");
        again.Click += (_, _) => Start();
        var driver = new Button { Name = "DiagDriver", Content = T("Скачать драйвер Xprinter", "Xprinter драйверин жүктөө", "Download Xprinter driver", "Xprinter sürücüsünü indir", "Xprinter drayverini yuklab olish") };
        driver.Classes.Add("modal-cancel");
        driver.Click += (_, _) => PrinterDriverLinks.Open(PrinterDriverLinks.UrlFor(_role));

        var who = string.IsNullOrWhiteSpace(devicePath) ? T("не выбран", "тандалган эмес", "not selected", "seçilmedi", "tanlanmagan") : LabelPrinterSelector.DisplayName(devicePath);
        _shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = WindowTitle,
            Subtitle = (role == PrinterRole.Label
                ? T("Принтер этикеток: ", "Этикетка принтери: ", "Label printer: ", "Etiket yazıcısı: ", "Yorliq printeri: ")
                : T("Принтер чеков: ", "Чек принтери: ", "Receipt printer: ", "Fiş yazıcısı: ", "Chek printeri: ")) + who,
            Footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { driver, close, again } },
            Content = _results,
        };
        Content = _shell;
        Opened += (_, _) => Start();
    }

    public static string WindowTitle => T("Принтер не печатает?", "Принтер баспай жатабы?", "Printer not printing?", "Yazıcı yazdırmıyor mu?", "Printer chop etmayaptimi?");

    private void Start()
    {
        if (Running is { IsCompleted: false })
            return;
        Running = RunAsync();
    }

    private async Task RunAsync()
    {
        _results.Children.Clear();
        _results.Children.Add(Line(FindingLevel.Info, T("Проверяю…", "Текшерип жатам…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…"), null));
        IReadOnlyList<DiagnosticFinding> findings;
        try
        {
            findings = await Task.Run(() => PrinterDiagnostics.RunAsync(_sys, _devicePath, _role, _language));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Диагностика принтера: сбой {ex.GetType().Name}: {ex.Message}", "PRINTER");
            findings = [new DiagnosticFinding(FindingLevel.Error, ex.Message)];
        }
        Findings = findings;
        if (_closed)
            return;
        _results.Children.Clear();
        foreach (var f in findings)
            _results.Children.Add(Line(f.Level, f.Text, f));
    }

    private Control Line(FindingLevel level, string text, DiagnosticFinding? finding)
    {
        // Цветной кружок вместо значков ✔/✖ (их нет в части шрифтов Windows и на Linux): зелёный — в порядке, жёлтый — внимание, красный — ошибка, серый — сведения.
        var brush = level switch
        {
            FindingLevel.Ok => "BrushSuccess",
            FindingLevel.Warning => "BrushWarning",
            FindingLevel.Error => "BrushDanger",
            _ => "BrushTextMuted",
        };
        var mark = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(2, 4, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        mark.Bind(Border.BackgroundProperty, mark.GetResourceObservable(brush));
        var body = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        body.Bind(TextBlock.ForegroundProperty, body.GetResourceObservable("BrushText"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(body, 1);
        grid.Children.Add(mark);
        grid.Children.Add(body);
        if (finding is { Fix: not PrinterFix.None } f)
        {
            var fix = new Button { Name = "DiagFix" + f.Fix, Content = T("Исправить", "Оңдоо", "Fix", "Düzelt", "Tuzatish"), MinHeight = 40, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            fix.Classes.Add("modal-cancel");
            fix.Click += async (_, _) => await ApplyFix(f);
            Grid.SetColumn(fix, 2);
            grid.Children.Add(fix);
        }
        return grid;
    }

    private async Task ApplyFix(DiagnosticFinding f)
    {
        PosLogger.Log($"[UI] Диагностика принтера: «Исправить» — {f.Fix} ({f.FixArg}).", "PRINTER");
        switch (f.Fix)
        {
            case PrinterFix.RunWizard:
            case PrinterFix.CreateQueue:
                Finish(true);
                return;
            case PrinterFix.OpenDriverPage:
                PrinterDriverLinks.Open(PrinterDriverLinks.UrlFor(_role));
                return;
        }
        var queue = _devicePath ?? "";
        if (!_shell.TryBeginBusy())
            return;
        ElevatedOutcome outcome;
        try
        {
            if (FixOverride is { } fake)
                outcome = await fake(f.Fix, queue, f.FixArg);
            else
                outcome = f.Fix switch
                {
                    PrinterFix.ClearJobs => await PrinterSetupApply.ClearJobsAsync(queue),
                    PrinterFix.ResumeQueue => await PrinterQueueInstaller.ResumeQueueAsync(queue),
                    PrinterFix.MovePort when f.FixArg is { } port => await PrinterQueueInstaller.MovePortAsync(queue, port),
                    _ => new ElevatedOutcome(ElevatedResult.NotSupported),
                };
        }
        finally
        {
            _shell.EndBusy();
        }
        if (outcome.Result == ElevatedResult.Cancelled)
        {
            _results.Children.Insert(0, Line(FindingLevel.Warning, T("Windows не дал разрешение — ничего не изменено. Нажмите «Исправить» ещё раз и ответьте «Да».", "Windows уруксат берген жок — эч нерсе өзгөргөн жок. «Оңдоо» баскычын кайра басып, «Ооба» деп жооп бериңиз.",
                "Windows did not give permission — nothing was changed. Press “Fix” again and answer “Yes”.", "Windows izin vermedi — hiçbir şey değiştirilmedi. «Düzelt»e tekrar basıp «Evet» deyin.", "Windows ruxsat bermadi — hech narsa o'zgartirilmadi. «Tuzatish» tugmasini qayta bosing va «Ha» deb javob bering."), null));
            return;
        }
        if (outcome.Result != ElevatedResult.Done)
        {
            _results.Children.Insert(0, Line(FindingLevel.Error, T("Исправить не получилось: ", "Оңдоо мүмкүн болгон жок: ", "Could not fix: ", "Düzeltilemedi: ", "Tuzatib bo'lmadi: ") + (outcome.Details.Length > 0 ? outcome.Details : outcome.Result.ToString()), null));
            return;
        }
        Start(); // проверить заново
    }

    private void Finish(bool openWizard)
    {
        if (_closed)
            return;
        _closed = true;
        Close(openWizard);
    }

    /// <summary>Показать диагностику; true — кассир нажал «Исправить» → открыть мастер.</summary>
    public static async Task<bool> ShowAsync(Window? owner, string? devicePath, PrinterRole role)
    {
        PosLogger.Log($"[UI] Диагностика принтера: открыта ({(role == PrinterRole.Label ? "этикетки" : "чеки")}).", "PRINTER");
        var window = new PrinterDiagnosticsWindow(devicePath, role);
        return await window.ShowDialog<bool>(PosDialogHost.ResolveOwner(owner)).ConfigureAwait(true);
    }
}
