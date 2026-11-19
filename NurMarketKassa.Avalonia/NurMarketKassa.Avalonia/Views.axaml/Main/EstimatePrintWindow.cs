using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-19 («Смета», ТЗ 4): предпросмотр и печать сметы. A4 — тот же бланк, что КП «Клиентов» (<see cref="ClientDocRenderer"/>, заголовок «Смета», реквизиты продавца из «Клиентов → Документы»);
/// принтер A4 выбирается из списка (Windows — спулер, Linux — CUPS). Лента 58/80 мм — текст через принтер чеков кассы (в программе владельца печать на ленту — в кассе). «Открыть PDF» — готовый PDF во временной папке.
/// Закупки и маржи в печатных формах нет. Esc — закрыть.</summary>
public sealed class EstimatePrintWindow : Window
{
    private readonly EstimateDeps _deps;
    private readonly EstimateDoc _doc;
    private readonly ModalShell _shell = new();
    private readonly StackPanel _pages = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ComboBox _printers = new() { Name = "EstimatePrinter", Height = 40, MinWidth = 220 };
    private readonly TextBlock _info = new() { Name = "EstimatePrintInfo", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private int _gen;

    public int PageCount { get; private set; }

    public string InfoText => _info.Text ?? "";

    public EstimatePrintWindow(EstimateDeps deps, EstimateDoc doc)
    {
        _deps = deps;
        _doc = doc;
        Title = EstimateUi.T("Печать сметы", "Сметаны басуу", "Print the estimate", "Keşfi yazdır", "Smetani chop etish");
        Width = 1000;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        Opened += (_, _) => { this.FitToKassaScreen(); _ = RenderAsync(); };
        BuildUi();
    }

    private ClientDocRenderer.Input MakeInput()
    {
        var profile = ClientDocsStore.Profile.Clone();
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = UserPreferences.Instance.StoreName;
        return new ClientDocRenderer.Input(EstimateText.ToClientDoc(_doc), profile, ClientDocRenderer.DefaultTemplate(ClientDocKind.Quote), ClientDocsUi.AccentHex(this), null);
    }

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.Margin = new Thickness(16);
        _shell.ScreenHeightShare = 1.0;
        _shell.ContentScroll = ScrollBarVisibility.Disabled;
        ServiceDeskUi.Use(_info, TextBlock.ForegroundProperty, "BrushTextSoft");

        if (ClientDocPrint.DirectPrintSupported)
        {
            try
            {
                var list = ClientDocPrint.Printers();
                _printers.ItemsSource = list;
                var def = ClientDocPrint.DefaultPrinter();
                _printers.SelectedItem = list.Contains(def ?? "") ? def : list.FirstOrDefault();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Смета: список принтеров A4 не прочитан ({ex.Message}).", "WARNING");
            }
        }
        _printers.IsVisible = ClientDocPrint.DirectPrintSupported;

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        bar.Children.Add(_printers);
        bar.Children.Add(ServiceDeskUi.Action("EstimatePrintA4", EstimateUi.T("Печать A4", "A4 басуу", "Print A4", "A4 yazdır", "A4 chop etish"), () => _ = PrintA4Async(), primary: true));
        bar.Children.Add(ServiceDeskUi.Action("EstimatePrintRibbon", EstimateUi.T("Печать на ленту", "Лентага басуу", "Print on the receipt tape", "Fiş şeridine yazdır", "Lentaga chop etish"), () => _ = PrintRibbonAsync()));
        bar.Children.Add(ServiceDeskUi.Action("EstimateOpenPdf", EstimateUi.T("Открыть PDF", "PDF ачуу", "Open PDF", "PDF aç", "PDF ochish"), () => _ = OpenPdfAsync()));
        foreach (var b in bar.Children.OfType<Button>())
            b.Margin = new Thickness(8, 0, 0, 0);
        _printers.Margin = new Thickness(0, 0, 0, 0);

        var scroll = new ScrollViewer { Content = _pages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var dock = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(bar);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_info, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(_info);
        dock.Children.Add(scroll);
        _shell.Content = dock;

        var close = new Button { Name = "EstimatePrintClose", Content = EstimateUi.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), MinWidth = 120, IsCancel = true };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Close();
        _shell.Footer = close;
        Content = _shell;
    }

    /// <summary>Нарисовать листы A4 (в фоне; устаревший результат отбрасывается).</summary>
    public async Task RenderAsync()
    {
        var gen = ++_gen;
        var input = MakeInput();
        List<byte[]> pages;
        try
        {
            pages = await Task.Run(() => ClientDocRenderer.PageImages(input, 96)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смета: предпросмотр не построен ({ex}).", "WARNING");
            _info.Text = EstimateUi.T("Предпросмотр не построен.", "Алдын ала көрүү түзүлгөн жок.", "The preview could not be built.", "Önizleme oluşturulamadı.", "Ko'rinish yaratilmadi.");
            return;
        }
        if (gen != _gen)
            return;
        _pages.Children.Clear();
        foreach (var png in pages)
        {
            using var ms = new MemoryStream(png);
            _pages.Children.Add(new Border { Child = new Image { Source = new Bitmap(ms), Stretch = Stretch.Uniform, MaxWidth = 560 }, Background = Brushes.White, BoxShadow = BoxShadows.Parse("0 2 10 0 #33000000") });
        }
        PageCount = pages.Count;
        _info.Text = pages.Count == 1
            ? EstimateUi.T("1 страница A4", "1 бет A4", "1 A4 page", "1 A4 sayfa", "1 ta A4 sahifa")
            : EstimateUi.T($"Страниц A4: {pages.Count}", $"A4 беттер: {pages.Count}", $"A4 pages: {pages.Count}", $"A4 sayfa: {pages.Count}", $"A4 sahifalar: {pages.Count}");
    }

    public async Task<bool> PrintA4Async()
    {
        if (!ClientDocPrint.DirectPrintSupported || _printers.SelectedItem is not string printer)
        {
            _info.Text = EstimateUi.T("Принтер A4 не найден — откройте PDF и напечатайте его.", "A4 принтери табылган жок — PDF ачып басыңыз.", "No A4 printer found — open the PDF and print it.", "A4 yazıcı bulunamadı — PDF'i açıp yazdırın.", "A4 printer topilmadi — PDF ni ochib chop eting.");
            return false;
        }
        _info.Text = EstimateUi.T("Печатаю…", "Басылууда…", "Printing…", "Yazdırılıyor…", "Chop etilmoqda…");
        var input = MakeInput();
        var error = await Task.Run(() => ClientDocPrint.Print(printer, EstimateText.Title(_doc), ClientDocRenderer.PageImages(input, 200))).ConfigureAwait(true);
        _info.Text = error ?? EstimateUi.T("Отправлено на печать.", "Басууга жөнөтүлдү.", "Sent to the printer.", "Yazdırmaya gönderildi.", "Chop etishga yuborildi.");
        return error is null;
    }

    public async Task<bool> PrintRibbonAsync()
    {
        var error = await _deps.PrintRibbon(EstimateText.Receipt(_doc)).ConfigureAwait(true);
        _info.Text = error ?? EstimateUi.T("Отправлено на ленту чеков.", "Чек лентасына жөнөтүлдү.", "Sent to the receipt printer.", "Fiş yazıcısına gönderildi.", "Chek printeriga yuborildi.");
        return error is null;
    }

    public async Task<string?> OpenPdfAsync()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "nurmarket-estimate-" + EstimateRules.Code(_doc.Number) + ".pdf");
            var input = MakeInput();
            await Task.Run(() => ClientDocRenderer.SavePdf(input, path)).ConfigureAwait(true);
            if (!PlatformLauncher.OpenFile(path))
                _info.Text = EstimateUi.T("PDF сохранён: ", "PDF сакталды: ", "PDF saved: ", "PDF kaydedildi: ", "PDF saqlandi: ") + path;
            return path;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смета: PDF не создан ({ex.Message}).", "WARNING");
            _info.Text = EstimateUi.T("Не удалось создать PDF.", "PDF түзүү мүмкүн болгон жок.", "Could not create the PDF.", "PDF oluşturulamadı.", "PDF yaratib bo'lmadi.");
            return null;
        }
    }
}
