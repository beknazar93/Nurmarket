using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-26: «Печать на A4» окна «Печать штрихкодов и ценников» — боковая панель настроек листа слева и предпросмотр листа справа,
/// как на странице «Печать штрих-кодов» NurCRM: заголовок листа, ориентация, товаров в ряд, строк на лист, копии каждого и по позициям,
/// высота штрихкода, «Штрих-код / Название / Цена» с размером шрифта и Ж/К/П, «Скидка (зачеркнуть старую цену)», ПЛУ, описание.
/// Предпросмотр и печать рисует один и тот же <see cref="A4SheetService"/> (штрихкоды — общий BarcodeLabelService.DrawLinearBlock).
/// </summary>
public sealed class BarcodeA4SheetWindow : Window
{
    private sealed class Row
    {
        public required BarcodePrintItem Item { get; init; }
        public int? Copies { get; set; }
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly BarcodePrintSettings _settings;
    private readonly A4SheetOptions _o;
    private readonly string? _storeName;
    private readonly List<Row> _rows;
    private readonly ModalShell _shell;
    private readonly TextBox _title;
    private readonly ComboBox _orientation;
    private readonly NumericUpDown _columns, _rowsBox, _copies;
    private readonly ListBox _positions;
    private readonly Button _resetButton;
    private readonly Slider _heightSlider;
    private readonly TextBlock _heightLabel;
    private readonly CheckBox _barcode, _name, _price, _discount, _plu, _description;
    private readonly NumericUpDown _nameSize, _priceSize;
    private readonly ToggleButton _nameB, _nameI, _nameU, _priceB, _priceI, _priceU;
    private readonly TextBlock _summary;
    private readonly TextBlock _warning;
    private readonly ComboBox _printerCombo;
    private readonly Image _pageImage;
    private readonly TextBlock _pageText;
    private readonly Button _prevPage, _nextPage;
    private readonly Button _printButton;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Bitmap? _bitmap;
    private int _pageIndex;
    private bool _init = true;

    /// <summary>Что сообщить в главном окне после закрытия (например, «отправлено на печать»).</summary>
    public string? ResultMessage { get; private set; }

    public BarcodeA4SheetWindow(IReadOnlyList<BarcodePrintItem> items, IReadOnlyList<int?> perItemCopies, BarcodePrintSettings settings, string? storeName)
    {
        _settings = settings;
        _o = settings.A4.Clone();
        _storeName = storeName;
        _rows = items.Select((it, i) => new Row { Item = it, Copies = i < perItemCopies.Count ? perItemCopies[i] : null }).ToList();

        Title = T("Печать на A4", "A4 басуу", "Print on A4", "A4'e yazdır", "A4 ga chop etish");
        Width = 1180;
        Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        FontFamily = new FontFamily("Segoe UI, Inter, Noto Sans");
        Use(this, BackgroundProperty, "DialogWindowBackground");
        Use(this, TransparencyLevelHintProperty, "DialogTransparencyHint");
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ComboBox>()) { Setters = { new Avalonia.Styling.Setter(MinHeightProperty, 44.0), new Avalonia.Styling.Setter(ComboBox.CornerRadiusProperty, new CornerRadius(8)) } });
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<NumericUpDown>()) { Setters = { new Avalonia.Styling.Setter(MinHeightProperty, 44.0), new Avalonia.Styling.Setter(NumericUpDown.CornerRadiusProperty, new CornerRadius(8)) } });
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<CheckBox>()) { Setters = { new Avalonia.Styling.Setter(MinHeightProperty, 44.0) } });

        // ---- левая панель
        _title = new TextBox
        {
            Text = _o.Title, MinHeight = 44, CornerRadius = new CornerRadius(10), VerticalContentAlignment = VerticalAlignment.Center,
            Watermark = T("Напр.: ИП «Ромашка», г. Бишкек", "Мис.: «Ромашка» ИП, Бишкек ш.", "E.g.: Daisy Ltd., Bishkek", "Örn.: Papatya Ltd., Bişkek", "Masalan: «Romashka» YaTT, Bishkek sh."),
        };
        _orientation = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        _orientation.Items.Add(T("Книжная (вертикальная)", "Китеп форматы (тик)", "Portrait (vertical)", "Dikey", "Kitob (vertikal)"));
        _orientation.Items.Add(T("Альбомная (горизонтальная)", "Альбом форматы (туурасынан)", "Landscape (horizontal)", "Yatay", "Albom (gorizontal)"));
        _orientation.SelectedIndex = _o.Landscape ? 1 : 0;
        _columns = Num(1, 10, _o.Columns);
        _rowsBox = Num(1, 30, _o.Rows);
        _copies = Num(1, 99, Math.Clamp(settings.A4Copies, 1, 99));

        _positions = new ListBox { MaxHeight = 190, MinHeight = 60 };
        _positions.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Row>((row, _) => BuildRow(row), true);
        _resetButton = Ghost("");
        _resetButton.HorizontalAlignment = HorizontalAlignment.Right;
        _resetButton.MinHeight = 36;
        _resetButton.Click += (_, _) =>
        {
            foreach (var r in _rows) r.Copies = null;
            RefreshPositions();
            Changed();
        };

        _heightSlider = new Slider { Minimum = 16, Maximum = 140, Value = Math.Clamp(_o.BarcodeHeightPx, 16, 140), TickFrequency = 1, MinHeight = 32 };
        _heightLabel = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(_heightLabel, TextBlock.ForegroundProperty, "BrushTextSoft");

        _barcode = Check(T("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), _o.ShowBarcode);
        _name = Check(T("Название", "Аталышы", "Name", "Ad", "Nomi"), _o.ShowName);
        _price = Check(T("Цена", "Баасы", "Price", "Fiyat", "Narx"), _o.ShowPrice);
        _discount = Check(T("Скидка (зачеркнуть старую цену)", "Арзандатуу (эски баанын үстүнөн сызуу)", "Discount (strike through the old price)", "İndirim (eski fiyatı çiz)", "Chegirma (eski narxni chizish)"), _o.ShowDiscount);
        _plu = Check(T("ПЛУ", "ПЛУ", "PLU", "PLU", "PLU"), _o.ShowPlu);
        _description = Check(T("Описание", "Сүрөттөмө", "Description", "Açıklama", "Tavsif"), _o.ShowDescription);
        _nameSize = Num(6, 40, (decimal)_o.Name.SizePt, 84);
        _priceSize = Num(6, 60, (decimal)_o.Price.SizePt, 84);
        // 2026-10-27, аудит Н-19: подписи кнопок «жирный / курсив / подчёркнутый» были жёстко по-русски (Ж К П) — теперь на языке кассы.
        (_nameB, _nameI, _nameU) = (Tog(T("Ж", "Ж", "B", "K", "B"), FontWeight.Bold, FontStyle.Normal, false, _o.Name.Bold), Tog(T("К", "К", "I", "İ", "I"), FontWeight.Normal, FontStyle.Italic, false, _o.Name.Italic), Tog(T("П", "П", "U", "A", "U"), FontWeight.Normal, FontStyle.Normal, true, _o.Name.Underline));
        (_priceB, _priceI, _priceU) = (Tog(T("Ж", "Ж", "B", "K", "B"), FontWeight.Bold, FontStyle.Normal, false, _o.Price.Bold), Tog(T("К", "К", "I", "İ", "I"), FontWeight.Normal, FontStyle.Italic, false, _o.Price.Italic), Tog(T("П", "П", "U", "A", "U"), FontWeight.Normal, FontStyle.Normal, true, _o.Price.Underline));

        _summary = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(_summary, TextBlock.ForegroundProperty, "BrushTextSoft");
        _warning = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(_warning, TextBlock.ForegroundProperty, "BrushWarning");
        _printerCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = T("Выбрать принтер…", "Принтерди тандоо…", "Choose a printer…", "Yazıcı seç…", "Printerni tanlash…") };

        var left = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 8, 0) };
        left.Children.Add(Field(T("Заголовок листа (адрес и т.п.)", "Барактын аталышы (дарек ж.б.)", "Sheet title (address etc.)", "Sayfa başlığı (adres vb.)", "Varaq sarlavhasi (manzil va h.k.)"), _title));
        left.Children.Add(Field(T("Ориентация листа", "Барактын багыты", "Sheet orientation", "Sayfa yönü", "Varaq yo'nalishi"), _orientation));
        var grid3 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*,8,*") };
        grid3.Children.Add(Field(T("Товаров в ряд", "Катарда", "Per row", "Satırda", "Qatorda"), _columns));
        var f2 = Field(T("Строк на лист", "Барактагы катар", "Rows per sheet", "Sayfada satır", "Varaqda qator"), _rowsBox); Grid.SetColumn(f2, 2); grid3.Children.Add(f2);
        var f3 = Field(T("Копий каждого", "Ар биринин көчүрмөсү", "Copies of each", "Her birinin kopyası", "Har birining nusxasi"), _copies); Grid.SetColumn(f3, 4); grid3.Children.Add(f3);
        left.Children.Add(grid3);

        var posHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var posTitle = new TextBlock { Text = T("Копии по позициям", "Позициялар боюнча көчүрмөлөр", "Copies by item", "Kalemlere göre kopyalar", "Pozitsiyalar bo'yicha nusxalar"), FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Use(posTitle, TextBlock.ForegroundProperty, "BrushTextSoft");
        posHeader.Children.Add(posTitle);
        Grid.SetColumn(_resetButton, 1);
        posHeader.Children.Add(_resetButton);
        left.Children.Add(posHeader);
        left.Children.Add(_positions);

        left.Children.Add(_heightLabel);
        left.Children.Add(_heightSlider);
        left.Children.Add(Panel(_barcode));
        left.Children.Add(Panel(StyleRow(_name, _nameSize, _nameB, _nameI, _nameU)));
        left.Children.Add(Panel(StyleRow(_price, _priceSize, _priceB, _priceI, _priceU)));
        left.Children.Add(Panel(_discount));
        left.Children.Add(Panel(_plu));
        left.Children.Add(Panel(_description));
        left.Children.Add(Field(T("Принтер для листов A4", "A4 барактары үчүн принтер", "Printer for A4 sheets", "A4 sayfalar için yazıcı", "A4 varaqlar uchun printer"), _printerCombo));
        left.Children.Add(_summary);
        left.Children.Add(_warning);
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // ---- предпросмотр
        _pageImage = new Image { Stretch = Stretch.Uniform };
        _pageText = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
        Use(_pageText, TextBlock.ForegroundProperty, "BrushTextSoft");
        _prevPage = Ghost("‹");
        _prevPage.MinWidth = 52;
        _prevPage.Click += (_, _) => { _pageIndex = Math.Max(0, _pageIndex - 1); RefreshPreview(); };
        _nextPage = Ghost("›");
        _nextPage.MinWidth = 52;
        _nextPage.Click += (_, _) => { _pageIndex++; RefreshPreview(); };
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8), Children = { _prevPage, _pageText, _nextPage } };
        var paper = new Border { BoxShadow = BoxShadows.Parse("0 4 16 0 #33000000"), Child = _pageImage, Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        right.Children.Add(nav);
        Grid.SetRow(paper, 1);
        right.Children.Add(paper);
        var rightCard = new Border { Padding = new Thickness(12), CornerRadius = new CornerRadius(12), Child = right };
        Use(rightCard, Border.BackgroundProperty, "BrushInputAlt");

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("380,12,*") };
        body.Children.Add(leftScroll);
        Grid.SetColumn(rightCard, 2);
        body.Children.Add(rightCard);

        _status = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var cancel = new Button { IsCancel = true, MinWidth = 110, Content = T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish") };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => { SaveSettings(); Close(); };
        _printButton = new Button { MinWidth = 160, Content = T("Печать A4", "A4 басуу", "Print A4", "A4 yazdır", "A4 chop etish") };
        _printButton.Classes.Add("modal-primary");
        _printButton.Click += async (_, _) => await PrintAsync().ConfigureAwait(true);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { cancel, _printButton } };

        _shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = T("Печать на A4", "A4 басуу", "Print on A4", "A4'e yazdır", "A4 ga chop etish"),
            Subtitle = T("Лист с сеткой этикеток: настройте слева, предпросмотр — справа.", "Этикеткалар торчосу бар барак: сол жакта жөндөңүз, алдын ала көрүү — оң жакта.",
                "A sheet with a grid of labels: set it up on the left, the preview is on the right.", "Etiket ızgaralı sayfa: solda ayarlayın, önizleme sağda.", "Yorliqlar to'rli varaq: chapda sozlang, ko'rinish — o'ngda."),
            ContentScroll = ScrollBarVisibility.Disabled,
            ScreenHeightShare = 1.0,
            Hint = _status,
            Footer = footer,
            Content = body,
        };
        Content = _shell;

        _resetButton.Content = ResetText();
        RefreshPositions();
        UpdateHeightLabel();

        // обработчики — после сборки
        _timer.Tick += (_, _) => { _timer.Stop(); RefreshPreview(); };
        _title.TextChanged += (_, _) => Changed();
        _orientation.SelectionChanged += (_, _) => Changed();
        _columns.ValueChanged += (_, _) => Changed();
        _rowsBox.ValueChanged += (_, _) => Changed();
        _copies.ValueChanged += (_, _) => { RefreshPositions(); Changed(); };
        _heightSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) { UpdateHeightLabel(); Changed(); } };
        foreach (var c in new[] { _barcode, _name, _price, _discount, _plu, _description })
            c.IsCheckedChanged += (_, _) => Changed();
        foreach (var n in new[] { _nameSize, _priceSize })
            n.ValueChanged += (_, _) => Changed();
        foreach (var t in new[] { _nameB, _nameI, _nameU, _priceB, _priceI, _priceU })
            t.IsCheckedChanged += (_, _) => Changed();
        Opened += (_, _) => _ = LoadPrintersAsync();
        Closing += (_, _) => SaveSettings();
        _init = false;
        ReadOptions();
        RefreshPreview();
    }

    // ------------------------------------------------------------------ данные

    /// <summary>Настройки листа, как они сейчас в окне (стенд читает их).</summary>
    public A4SheetOptions Options => _o;

    private int GlobalCopies => (int)(_copies.Value ?? 1);

    private List<A4SheetItem> BuildSheetItems() => _rows
        .Select(r => BarcodePrintCatalog.ToSheetItem(r.Item with { Copies = r.Copies ?? GlobalCopies }, _o.ShowDiscount, _storeName))
        .ToList();

    private void ReadOptions()
    {
        _o.Title = _title.Text ?? "";
        _o.Landscape = _orientation.SelectedIndex == 1;
        _o.Columns = (int)(_columns.Value ?? 3);
        _o.Rows = (int)(_rowsBox.Value ?? 8);
        _o.BarcodeHeightPx = (int)_heightSlider.Value;
        _o.ShowBarcode = _barcode.IsChecked == true;
        _o.ShowName = _name.IsChecked == true;
        _o.ShowPrice = _price.IsChecked == true;
        _o.ShowDiscount = _discount.IsChecked == true;
        _o.ShowPlu = _plu.IsChecked == true;
        _o.ShowDescription = _description.IsChecked == true;
        _o.Name = new A4TextStyle { SizePt = (double)(_nameSize.Value ?? 12), Bold = _nameB.IsChecked == true, Italic = _nameI.IsChecked == true, Underline = _nameU.IsChecked == true };
        _o.Price = new A4TextStyle { SizePt = (double)(_priceSize.Value ?? 16), Bold = _priceB.IsChecked == true, Italic = _priceI.IsChecked == true, Underline = _priceU.IsChecked == true };
    }

    private void Changed()
    {
        if (_init)
            return;
        ReadOptions();
        _timer.Stop();
        _timer.Start();
    }

    private void SaveSettings()
    {
        ReadOptions();
        _settings.A4 = _o.Clone();
        _settings.A4Copies = GlobalCopies;
        if (_printerCombo.SelectedItem is DiscoveredPrinter p)
            _settings.A4PrinterName = p.DevicePath;
        BarcodePrintSettingsStore.Save(_settings);
    }

    // ------------------------------------------------------------------ предпросмотр

    private void RefreshPreview()
    {
        try
        {
            var items = BuildSheetItems();
            var expanded = A4SheetService.Expand(items);
            var geo = A4SheetService.GetGeometry(_o);
            var pages = Math.Max(1, A4SheetService.PageCount(expanded.Count, _o));
            _pageIndex = Math.Clamp(_pageIndex, 0, pages - 1);
            using var bmp = A4SheetService.RenderPage(expanded, _pageIndex, _o, 120);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var next = new Bitmap(ms);
            _pageImage.Source = next;
            _bitmap?.Dispose();
            _bitmap = next;
            _pageText.Text = T($"Лист {_pageIndex + 1} из {pages}", $"{_pageIndex + 1}/{pages}-барак", $"Sheet {_pageIndex + 1} of {pages}", $"Sayfa {_pageIndex + 1}/{pages}", $"{_pageIndex + 1}/{pages}-varaq");
            _prevPage.IsEnabled = _pageIndex > 0;
            _nextPage.IsEnabled = _pageIndex < pages - 1;

            var withCode = _rows.Count(r => !string.IsNullOrWhiteSpace(r.Item.Barcode));
            var labels = expanded.Count;
            _summary.Text = T($"Товаров с штрих-кодом: {withCode} · Этикеток: {labels} · Листов: {pages} (по {geo.PerPage} на лист)",
                $"Штрих-коду бар товарлар: {withCode} · Этикеткалар: {labels} · Барактар: {pages} (бирөөдө {geo.PerPage})",
                $"Products with a barcode: {withCode} · Labels: {labels} · Sheets: {pages} ({geo.PerPage} per sheet)",
                $"Barkodlu ürün: {withCode} · Etiket: {labels} · Sayfa: {pages} (sayfada {geo.PerPage})",
                $"Shtrix-kodli mahsulotlar: {withCode} · Yorliqlar: {labels} · Varaqlar: {pages} (varaqda {geo.PerPage})");
            var noCode = _rows.Count - withCode;
            _warning.IsVisible = _o.ShowBarcode && noCode > 0;
            _warning.Text = T($"У {noCode} товаров нет штрихкода — штрихкод на их этикетках не напечатается.", $"{noCode} товардо штрих-код жок — алардын этикеткаларында штрих-код басылбайт.",
                $"{noCode} products have no barcode — it will not be printed on their labels.", $"{noCode} ürünün barkodu yok — etiketlerine barkod basılmaz.", $"{noCode} ta mahsulotda shtrix-kod yo'q — ularning yorliqlariga shtrix-kod chop etilmaydi.");
            _printButton.IsEnabled = labels > 0;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"A4 sheet preview failed: {ex}", "WARNING");
            _status.Text = T("Не удалось построить предпросмотр листа.", "Барактын алдын ала көрүнүшүн түзүү мүмкүн болгон жок.", "Could not build the sheet preview.", "Sayfa önizlemesi oluşturulamadı.", "Varaq ko'rinishini yaratib bo'lmadi.");
        }
    }

    // ------------------------------------------------------------------ печать

    private async Task LoadPrintersAsync()
    {
        IReadOnlyList<DiscoveredPrinter> list;
        try
        {
            list = await Task.Run(() => BarcodePrintWindow.PrinterListOverride?.Invoke() ?? BarcodeLabelService.GetAvailablePrinters()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"A4 sheet window: printer enumeration failed: {ex.GetType().Name}", "WARNING");
            list = Array.Empty<DiscoveredPrinter>();
        }
        var plain = list.Where(p => !BarcodeLabelService.IsRawDevicePath(p.DevicePath)).ToList();
        _printerCombo.ItemsSource = plain;
        _printerCombo.SelectedItem = plain.FirstOrDefault(p => string.Equals(p.DevicePath, _settings.A4PrinterName, StringComparison.OrdinalIgnoreCase)) ?? plain.FirstOrDefault();
    }

    /// <summary>«Печать A4» — листы на выбранный принтер Windows (спулер).</summary>
    public async Task PrintAsync()
    {
        if (_printerCombo.SelectedItem is not DiscoveredPrinter printer)
        {
            _status.Text = T("Принтер не подключён — выберите принтер для листов A4.", "Принтер туташкан эмес — A4 барактары үчүн принтерди тандаңыз.", "No printer connected — choose a printer for A4 sheets.", "Yazıcı bağlı değil — A4 sayfalar için bir yazıcı seçin.", "Printer ulanmagan — A4 varaqlar uchun printerni tanlang.");
            return;
        }
        if (!_shell.TryBeginBusy())
            return;
        ReadOptions();
        SaveSettings();
        var items = BuildSheetItems();
        var options = _o.Clone();
        var labels = A4SheetService.LabelCount(items);
        var pages = A4SheetService.PageCount(labels, options);
        _status.Text = T($"Печать… (листов: {pages})", $"Басып жатат… (барак: {pages})", $"Printing… ({pages} sheets)", $"Yazdırılıyor… ({pages} sayfa)", $"Chop etilmoqda… ({pages} ta varaq)");
        LabelPrintResult result;
        try
        {
            var print = BarcodePrintWindow.SheetPrintOverride ?? ((i, o, p) => A4SheetService.Print(i, o, p));
            result = await Task.Run(() => print(items, options, printer.DevicePath)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"A4 sheet window: print failed: {ex}", "ERROR");
            result = LabelPrintResult.Failed;
        }
        finally
        {
            _shell.EndBusy();
        }
        switch (result)
        {
            case LabelPrintResult.Success:
                ResultMessage = T($"Листы A4 отправлены на печать: {labels} этикеток, {pages} листов.", $"A4 барактары басууга жөнөтүлдү: {labels} этикетка, {pages} барак.", $"A4 sheets sent to the printer: {labels} labels, {pages} sheets.", $"A4 sayfalar yazdırmaya gönderildi: {labels} etiket, {pages} sayfa.", $"A4 varaqlar chop etishga yuborildi: {labels} ta yorliq, {pages} ta varaq.");
                _status.Text = ResultMessage;
                Close();
                break;
            case LabelPrintResult.PrinterNotFound:
                _status.Text = T("Принтер не найден — выберите другой.", "Принтер табылган жок — башкасын тандаңыз.", "Printer not found — choose another.", "Yazıcı bulunamadı — başkasını seçin.", "Printer topilmadi — boshqasini tanlang.");
                break;
            default:
                _status.Text = T("Ошибка печати. Подробности в журнале приложения.", "Басуу катасы. Толук маалымат колдонмонун журналында.", "Printing error. See the app log for details.", "Yazdırma hatası. Ayrıntılar uygulama günlüğünde.", "Chop etishda xato. Batafsil ma'lumot ilova jurnalida.");
                break;
        }
    }

    // ------------------------------------------------------------------ элементы

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));

    private static Button Ghost(string text)
    {
        var b = new Button
        {
            Content = text, MinHeight = 44, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), FontSize = 13.5,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        b.Classes.Add("sec-outline");
        return b;
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal value, double width = double.NaN) => new()
    {
        Minimum = min, Maximum = max, Value = value, Increment = 1, FormatString = "0", MinHeight = 44, Width = width, HorizontalAlignment = double.IsNaN(width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
    };

    private static CheckBox Check(string text, bool isChecked) => new() { Content = text, IsChecked = isChecked, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };

    private ToggleButton Tog(string text, FontWeight weight, FontStyle style, bool underline, bool isChecked)
    {
        var tb = new TextBlock { Text = text, FontWeight = weight, FontStyle = style, TextDecorations = underline ? TextDecorations.Underline : null, HorizontalAlignment = HorizontalAlignment.Center };
        var t = new ToggleButton { Content = tb, IsChecked = isChecked, Width = 44, Height = 44, CornerRadius = new CornerRadius(8), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false };
        Avalonia.Automation.AutomationProperties.SetName(t, text == T("Ж", "Ж", "B", "K", "B") ? T("Жирный", "Калың", "Bold", "Kalın", "Qalin") : text == T("К", "К", "I", "İ", "I") ? T("Курсив", "Курсив", "Italic", "İtalik", "Kursiv") : T("Подчёркнутый", "Асты сызылган", "Underline", "Altı çizili", "Tagi chizilgan")); // 2026-10-27: сравнение с текстом кнопки на языке кассы (было "Ж"/"К" — в en/tr/uz имя читалось неверно)
        return t;
    }

    private StackPanel Field(string caption, Control control)
    {
        var label = new TextBlock { Text = caption, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
        return new StackPanel { Spacing = 3, Children = { label, control } };
    }

    private Border Panel(Control child)
    {
        var b = new Border { Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(8), Child = child };
        Use(b, Border.BackgroundProperty, "BrushInputAlt");
        return b;
    }

    private static Control StyleRow(CheckBox check, NumericUpDown size, ToggleButton b, ToggleButton i, ToggleButton u)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto") };
        g.Children.Add(check);
        size.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(size, 1);
        g.Children.Add(size);
        foreach (var (t, col) in new[] { (b, 2), (i, 3), (u, 4) })
        {
            t.Margin = new Thickness(0, 0, 4, 0);
            Grid.SetColumn(t, col);
            g.Children.Add(t);
        }
        return g;
    }

    private Control BuildRow(Row row)
    {
        var name = new TextBlock { Text = row.Item.Product.Title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        ToolTip.SetTip(name, row.Item.Product.Title);
        var box = Num(1, 99, row.Copies ?? GlobalCopies, 100);
        box.ValueChanged += (_, _) =>
        {
            if (_init)
                return;
            var v = (int)(box.Value ?? 1);
            row.Copies = v == GlobalCopies ? null : v;
            _resetButton.Content = ResetText();
            Changed();
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 48 };
        g.Children.Add(name);
        Grid.SetColumn(box, 1);
        g.Children.Add(box);
        return g;
    }

    private string ResetText()
    {
        var n = _rows.Count(r => r.Copies != null);
        return T($"Сбросить к общему ({GlobalCopies})", $"Жалпыга кайтаруу ({GlobalCopies})", $"Reset to common ({GlobalCopies})", $"Ortak değere sıfırla ({GlobalCopies})", $"Umumiyga qaytarish ({GlobalCopies})") + (n > 0 ? $" · {n}" : "");
    }

    private void RefreshPositions()
    {
        var was = _init;
        _init = true;
        _positions.ItemsSource = _rows.ToList();
        _init = was;
        _resetButton.Content = ResetText();
    }

    private void UpdateHeightLabel() =>
        _heightLabel.Text = T($"Высота штрих-кода: {(int)_heightSlider.Value}px", $"Штрих-код бийиктиги: {(int)_heightSlider.Value}px", $"Barcode height: {(int)_heightSlider.Value}px", $"Barkod yüksekliği: {(int)_heightSlider.Value}px", $"Shtrix-kod balandligi: {(int)_heightSlider.Value}px");
}
