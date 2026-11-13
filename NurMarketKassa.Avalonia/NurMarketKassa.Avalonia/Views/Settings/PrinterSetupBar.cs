using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PrinterSetup;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-13 (владелец: «готовые драйверы для чековых принтеров и принтеров штрихкодов, в основном Xprinter»): строка кнопок на странице
/// Настройки → Печать — «Подключить принтер» (мастер), «Принтер не печатает?» (диагностика), «Скачать драйвер Xprinter» (официальный сайт) с пояснением,
/// когда драйвер нужен. Над кнопками — подсказка, если принтер ещё не настроен. На Android не показывается (там свой поиск Bluetooth/USB).</summary>
public sealed class PrinterSetupBar : StackPanel
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly PrinterRole _role;
    private readonly Func<string?> _currentPath;
    private readonly Border _hint;
    private readonly TextBlock _hintText;

    /// <summary>Мастер сохранил настройки — странице надо перечитать поля.</summary>
    public event EventHandler<PrinterWizardResult>? Applied;

    public Button WizardButton { get; }
    public Button DiagnosticsButton { get; }
    public Button DriverButton { get; }

    /// <param name="currentPath">Текущий путь принтера на странице (что сейчас в поле, ещё не сохранённое тоже).</param>
    public PrinterSetupBar(PrinterRole role, Func<string?> currentPath)
    {
        _role = role;
        _currentPath = currentPath;
        Spacing = 6;
        Margin = new Thickness(0, 4, 0, 4);
        IsVisible = !OperatingSystem.IsAndroid();

        _hintText = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        _hintText.Bind(TextBlock.ForegroundProperty, _hintText.GetResourceObservable("BrushText"));
        _hint = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), Child = _hintText, IsVisible = false };
        _hint.Bind(Border.BackgroundProperty, _hint.GetResourceObservable("BrushWarningSoft"));

        WizardButton = Make(role == PrinterRole.Label ? "BtnLabelPrinterWizard" : "BtnPrinterWizard", PrinterSetupWizardWindow.WizardTitle, primary: true);
        WizardButton.Click += async (_, _) => await OpenWizard();
        DiagnosticsButton = Make(role == PrinterRole.Label ? "BtnLabelPrinterDiag" : "BtnPrinterDiag", PrinterDiagnosticsWindow.WindowTitle, primary: false);
        DiagnosticsButton.Click += async (_, _) =>
        {
            if (await PrinterDiagnosticsWindow.ShowAsync(TopLevel.GetTopLevel(this) as Window, _currentPath(), _role))
                await OpenWizard();
        };
        DriverButton = Make(role == PrinterRole.Label ? "BtnLabelPrinterDriver" : "BtnPrinterDriver",
            T("Скачать драйвер Xprinter", "Xprinter драйверин жүктөө", "Download Xprinter driver", "Xprinter sürücüsünü indir", "Xprinter drayverini yuklab olish"), primary: false);
        DriverButton.Click += (_, _) =>
        {
            var url = PrinterDriverLinks.UrlFor(_role);
            if (!PrinterDriverLinks.Open(url))
                ShowHint(url);
        };
        ToolTip.SetTip(DriverButton, PrinterDriverLinks.WhenNeededText);

        var driverNote = new TextBlock { Text = PrinterDriverLinks.WhenNeededText, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        driverNote.Bind(TextBlock.ForegroundProperty, driverNote.GetResourceObservable("BrushTextSoft"));
        Children.Add(_hint);
        Children.Add(ButtonRow(WizardButton, DiagnosticsButton, DriverButton));
        Children.Add(driverNote);
    }

    private static WrapPanel ButtonRow(params Control[] items) => PrinterSetupWizardWindow.ButtonRow(items);

    private static Button Make(string name, string text, bool primary)
    {
        var b = new Button { Name = name, Content = text, MinHeight = 44, Padding = new Thickness(14, 6) };
        b.Classes.Add(primary ? "PrintGoldButton" : "SettingsFlatButton");
        return b;
    }

    /// <summary>Подсказка «принтер не настроен» (или ссылка, если браузер не открылся); пусто — скрыть.</summary>
    public void ShowHint(string? text)
    {
        _hintText.Text = text ?? "";
        _hint.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    /// <summary>Подсказка для принтера чеков: «не настроен» (пусто или LPT1, которого нет).</summary>
    public void RefreshReceiptHint(string? devicePath)
    {
        var unconfigured = PrinterSetupApply.ReceiptLooksUnconfigured(devicePath, p => !OperatingSystem.IsWindows() || PrinterPortService.LptDeviceExists(p));
        ShowHint(unconfigured
            ? T("Принтер чеков ещё не настроен. Нажмите «Подключить принтер» — мастер найдёт принтер (в том числе Xprinter без драйвера) и сделает пробную печать.",
                "Чек принтери азырынча жөндөлгөн эмес. «Принтерди туташтыруу» баскычын басыңыз — чебер принтерди (анын ичинде драйверсиз Xprinter) таап, сыноо басып чыгарат.",
                "The receipt printer is not set up yet. Press “Connect printer” — the wizard will find the printer (including Xprinter without a driver) and make a test print.",
                "Fiş yazıcısı henüz ayarlanmadı. «Yazıcıyı bağla»ya basın — sihirbaz yazıcıyı (sürücüsüz Xprinter dahil) bulur ve deneme baskısı yapar.",
                "Chek printeri hali sozlanmagan. «Printerni ulash» tugmasini bosing — usta printerni (shu jumladan drayversiz Xprinter) topadi va sinov chop etadi.")
            : null);
    }

    /// <summary>Подсказка для принтера этикеток: не выбран.</summary>
    public void RefreshLabelHint(string? devicePath) =>
        ShowHint(string.IsNullOrWhiteSpace(devicePath)
            ? T("Принтер этикеток не выбран. «Подключить принтер» найдёт его (XP-365B, XP-420B и другие) и напечатает пробную этикетку.",
                "Этикетка принтери тандалган эмес. «Принтерди туташтыруу» аны (XP-365B, XP-420B жана башкалар) таап, сыноо этикеткасын басат.",
                "No label printer selected. “Connect printer” will find it (XP-365B, XP-420B and others) and print a test label.",
                "Etiket yazıcısı seçilmedi. «Yazıcıyı bağla» onu (XP-365B, XP-420B ve diğerleri) bulur ve deneme etiketi basar.",
                "Yorliq printeri tanlanmagan. «Printerni ulash» uni (XP-365B, XP-420B va boshqalar) topadi va sinov yorlig'ini chop etadi.")
            : null);

    private async Task OpenWizard()
    {
        var result = await PrinterSetupWizardWindow.ShowAsync(TopLevel.GetTopLevel(this) as Window, _role);
        if (result is not null)
            Applied?.Invoke(this, result);
    }
}
