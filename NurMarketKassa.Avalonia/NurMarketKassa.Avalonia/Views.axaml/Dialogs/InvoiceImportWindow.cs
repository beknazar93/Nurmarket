using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-27, просьба владельца «загрузка электронных накладных» (Склад → Приёмка): окно предпросмотра накладной. Шапка — поставщик
/// (подобран по ИНН/названию из списка кассы, иначе выбрать или «＋»), номер и дата, предупреждение «Накладная уже загружалась»; таблица строк со
/// статусом («Найден», «Похожий — подтвердите», «Новый · из справочника», «Новый · заполнить», «Не распознан»), правка количества, закупки, наценки
/// и цены продажи (считает ReceivingLineVm), удаление строки; итог строк против итога из файла. «Добавить в приёмку» отдаёт строки вызывающему —
/// дальше обычная приёмка и «Оприходовать». Окно денег и остатков не проводит и в сеть не ходит.
/// Окно собрано в коде в ModalShell (как остальные диалоги), поля и кнопки ≥ 44 px, светлая/тёмная тема — ресурсами темы.</summary>
public sealed class InvoiceImportWindow : Window
{
    /// <summary>Что нужно окну от вызывающего.</summary>
    public sealed class Request
    {
        public required InvoicePlan Plan { get; init; }
        public string FileName { get; init; } = "";
        public string FileHash { get; init; } = "";
        public IReadOnlyList<PurchaseReceivingService.Supplier> Suppliers { get; init; } = [];
        public PurchaseReceivingService.Supplier? CurrentSupplier { get; init; }

        /// <summary>Штрихкоды, уже занятые в открытой приёмке (чтобы внутренний код не повторился).</summary>
        public Func<IEnumerable<string?>> TakenCodes { get; init; } = () => [];

        /// <summary>Создать поставщика (диалог «Новый поставщик»); null — недоступно (нет связи).</summary>
        public Func<Window, IReadOnlyList<PurchaseReceivingService.Supplier>, Task<PurchaseReceivingService.Supplier?>>? CreateSupplier { get; init; }

        /// <summary>Строка заголовков и столбцы можно поправить (Excel/CSV): вернуть новый план или null.</summary>
        public Func<Window, Task<InvoicePlan?>>? RemapColumns { get; init; }
    }

    /// <summary>Итог окна: строки для приёмки и выбранный поставщик.</summary>
    public sealed record Result(IReadOnlyList<ReceivingLineVm> Lines, PurchaseReceivingService.Supplier? Supplier, string Number, DateTime? Date, double Total, int Unrecognized,
        string FileInn = "", double? FileTotal = null, string FileSupplierName = "");

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static string Money(double v) => v.ToString("N2", Ru);

    private readonly Request _req;
    private InvoicePlan _plan;
    private PurchaseReceivingService.Supplier? _supplier;
    private bool _supplierChosenByUser;

    private readonly Button _supplierButton = new() { MinHeight = 44, MinWidth = 200, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Button _newSupplierButton = new() { MinHeight = 44, MinWidth = 44, Content = "＋", FontSize = 20 };
    private readonly Border _supplierPanel = new() { IsVisible = false, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(4) };
    private readonly TextBox _supplierSearch = new() { MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ListBox _supplierList = new() { MaxHeight = 200 };
    private readonly TextBlock _supplierNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _numberBox = new() { MinHeight = 44, MinWidth = 120, MaxLength = 40, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _dateBox = new() { MinHeight = 44, MinWidth = 124, MaxLength = 30, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly CheckBox _withVat = new() { MinHeight = 44 };
    private readonly TextBox _markupBox = new() { MinHeight = 44, MinWidth = 90, MaxLength = 6, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RadioButton _genCode = new() { GroupName = "InvoiceNewCode", IsChecked = true, MinHeight = 44 };
    private readonly RadioButton _noCode = new() { GroupName = "InvoiceNewCode", MinHeight = 44 };
    private readonly Border _optionsRow = new();
    private readonly Button _optionsToggle = new() { MinHeight = 44 };
    private readonly Border _warningsBox = new() { IsVisible = false, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8) };
    private readonly TextBlock _warningsText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Border _duplicateBox = new() { IsVisible = false, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8) };
    private readonly TextBlock _duplicateText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly CheckBox _duplicateOverride = new() { MinHeight = 44 };
    private readonly CheckBox _riskOverride = new() { MinHeight = 44, IsVisible = false }; // 2026-10-27 (Ск-1/Ск-14): сумма не сошлась / валюта не сом — отметка «всё равно добавить»
    private readonly DataGrid _grid = new();
    private readonly TextBlock _summary = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Button _add;
    private readonly Button _columns = new() { MinHeight = 44 };
    private InvoiceImportEntry? _duplicate;
    private DataGridColumn? _colName, _colBarcode, _colUnit;

    public Result? Chosen { get; private set; }

    public InvoiceImportWindow(Request req)
    {
        _req = req;
        _plan = req.Plan;
        Title = T("Загрузка накладной", "Накладнойду жүктөө", "Load an invoice", "Fatura yükleme", "Yuk xatini yuklash");
        SectionCards.AsModal(this, 1240);
        MinWidth = 640;

        _supplier = ChooseInitialSupplier();
        BuildTexts();
        _supplierList.ItemTemplate = new FuncDataTemplate<PurchaseReceivingService.Supplier>((s, _) =>
        {
            var tb = new TextBlock { Text = s?.Name ?? "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), FontSize = 13.5 };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable("BrushText"));
            return tb;
        });
        _supplierList.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(MinHeightProperty, 44.0) } });
        BuildGrid();

        // ---- шапка: поставщик · номер · дата
        var header = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Control c, double right = 10)
        {
            c.Margin = new Thickness(0, 0, right, 8);
            header.Children.Add(c);
        }
        Add(Caption(T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi")), 6);
        Add(_supplierButton, 6);
        Add(_newSupplierButton, 18);
        Add(Caption("№"), 6);
        Add(_numberBox, 10);
        Add(_dateBox, 14);
        Add(_columns, 10);
        Add(_optionsToggle, 0);

        _supplierPanel.Bind(Border.BackgroundProperty, _supplierPanel.GetResourceObservable("BrushPanelSoft"));
        _supplierPanel.Bind(Border.BorderBrushProperty, _supplierPanel.GetResourceObservable("BrushBorder"));
        _supplierPanel.Child = new StackPanel { Spacing = 4, Children = { _supplierSearch, _supplierList } };
        _supplierPanel.HorizontalAlignment = HorizontalAlignment.Left;
        _supplierPanel.Width = 420;
        _supplierNote.Bind(TextBlock.ForegroundProperty, _supplierNote.GetResourceObservable("BrushTextSoft"));

        // ---- параметры (сворачиваются на низком экране)
        var options = new WrapPanel { Orientation = Orientation.Horizontal };
        void AddOpt(Control c)
        {
            c.Margin = new Thickness(0, 0, 18, 6);
            options.Children.Add(c);
        }
        AddOpt(_withVat);
        AddOpt(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { Caption(T("Наценка для новых, %", "Жаңыларга кошумча, %", "Markup for new items, %", "Yeni ürünler için kâr, %", "Yangilar uchun ustama, %")), _markupBox },
        });
        AddOpt(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { Caption(T("Новый товар без штрихкода:", "Штрих-коду жок жаңы товар:", "New item without a barcode:", "Barkodsuz yeni ürün:", "Shtrix-kodsiz yangi mahsulot:")), _genCode, _noCode },
        });
        _optionsRow.Child = options;

        // ---- плашки
        _warningsBox.Bind(Border.BackgroundProperty, _warningsBox.GetResourceObservable("BrushWarningSoft"));
        _warningsText.Bind(TextBlock.ForegroundProperty, _warningsText.GetResourceObservable("BrushText"));
        _warningsBox.Child = new StackPanel { Children = { new ScrollViewer { MaxHeight = 66, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _warningsText }, _riskOverride } };
        _duplicateBox.Bind(Border.BackgroundProperty, _duplicateBox.GetResourceObservable("BrushDangerSoft"));
        _duplicateText.Bind(TextBlock.ForegroundProperty, _duplicateText.GetResourceObservable("BrushDangerOnSoft"));
        _duplicateBox.Child = new StackPanel { Children = { _duplicateText, _duplicateOverride } };

        var top = new StackPanel { Spacing = 2, Children = { header, _supplierPanel, _supplierNote, _optionsRow, _duplicateBox, _warningsBox } };
        _top = top;
        _grid.Margin = new Thickness(0, 6, 0, 0);
        _grid.MinHeight = 170;
        _grid.Height = 360;
        var rootGrid = new StackPanel { Children = { top, _grid } };

        // ---- низ окна
        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.MinHeight = 44;
        cancel.Click += (_, _) => Close();
        _add = SectionCards.Primary("", isDefault: false);
        _add.MinHeight = 44;
        _add.Click += (_, _) => Commit();
        _summary.Bind(TextBlock.ForegroundProperty, _summary.GetResourceObservable("BrushTextSoft"));

        var shell = SectionCards.Shell(Title, SubtitleText(), rootGrid, SectionCards.Buttons(cancel, _add), _summary);
        Content = shell;

        WireEvents();
        BindPlan();
        _optionsRow.IsVisible = !LowScreen();
        UpdateOptionsToggle();
        Opened += (_, _) => Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
    }

    private StackPanel? _top;
    private bool _optionsUserOpened;

    /// <summary>Подогнать высоту таблицы под окно: карточка не выше 85 % экрана (ModalShell), таблице отдаётся остаток (не меньше 200, не больше 560);
    /// на низком экране (1024×600) параметры сворачиваются под «Параметры ▾». Вызывается при открытии; стенд зовёт после того, как поставил высоту окна.</summary>
    public void Refit()
    {
        if (Content is not ModalShell shell)
            return;
        var cap = double.IsInfinity(shell.MaxHeight) ? 760 : shell.MaxHeight;
        if (cap < 620)
        {
            // 1024×600: подзаголовок и параметры уступают место таблице (параметры открываются кнопкой «Параметры»).
            if (shell.Subtitle is not null)
                shell.Subtitle = null;
            if (_optionsRow.IsVisible && !_optionsUserOpened)
            {
                _optionsRow.IsVisible = false;
                UpdateOptionsToggle();
            }
            UpdateLayout();
        }
        var topHeight = _top?.Bounds.Height ?? 150;
        var chrome = 170; // шапка окна, подвал с кнопками, поля тени
        var natural = 40 + _plan.Rows.Count * 46 + 16;
        _grid.Height = Math.Clamp(Math.Min(natural, cap - topHeight - chrome), 190, 560);
    }

    // ------------------------------------------------------------------ тексты

    private string SubtitleText()
    {
        var d = _plan.Document;
        return T($"Файл «{_req.FileName}» · {d.Format} · строк: {d.Items.Count}. Ничего не оприходовано — строки сначала попадут в приёмку.",
            $"«{_req.FileName}» файлы · {d.Format} · саптар: {d.Items.Count}. Эч нерсе кирешеге алынган жок — саптар алгач кабыл алууга түшөт.",
            $"File “{_req.FileName}” · {d.Format} · lines: {d.Items.Count}. Nothing is received yet — lines go to the receiving list first.",
            $"«{_req.FileName}» dosyası · {d.Format} · satır: {d.Items.Count}. Henüz stoğa alınmadı — satırlar önce mal kabul listesine gider.",
            $"«{_req.FileName}» fayli · {d.Format} · qatorlar: {d.Items.Count}. Hali hech narsa kirim qilinmadi — qatorlar avval qabul ro'yxatiga tushadi.");
    }

    private void BuildTexts()
    {
        _supplierSearch.Watermark = T("Поиск по названию или телефону", "Аталышы же телефону боюнча издөө", "Search by name or phone", "Ad veya telefona göre ara", "Nomi yoki telefon bo'yicha qidirish");
        ToolTip.SetTip(_newSupplierButton, T("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi"));
        Avalonia.Automation.AutomationProperties.SetName(_newSupplierButton, T("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi"));
        _newSupplierButton.Classes.Add("modal-secondary");
        _supplierButton.Classes.Add("modal-secondary");
        _columns.Classes.Add("modal-secondary");
        _optionsToggle.Classes.Add("modal-ghost");
        _columns.Content = T("Столбцы…", "Мамычалар…", "Columns…", "Sütunlar…", "Ustunlar…");
        _columns.IsVisible = _req.RemapColumns is not null;
        _numberBox.Text = _plan.Document.Header.Number;
        _dateBox.Text = _plan.Document.Header.Date?.ToString("dd.MM.yyyy", Ru) ?? "";
        _numberBox.Watermark = T("номер накладной", "накладной номери", "invoice number", "fatura numarası", "yuk xati raqami");
        ToolTip.SetTip(_numberBox, T("Номер накладной", "Накладнойдун номери", "Invoice number", "Fatura numarası", "Yuk xati raqami"));
        ToolTip.SetTip(_dateBox, T("Дата накладной", "Накладнойдун күнү", "Invoice date", "Fatura tarihi", "Yuk xati sanasi"));
        _dateBox.Watermark = T("дд.мм.гггг", "кк.аа.жжжж", "dd.mm.yyyy", "gg.aa.yyyy", "kk.oo.yyyy");
        _withVat.Content = T("Закупка с НДС", "НДС менен сатып алуу", "Purchase incl. VAT", "KDV'li alış", "QQS bilan xarid");
        _withVat.IsVisible = _plan.Document.AnyVat;
        _markupBox.Watermark = T("напр. 25", "мис. 25", "e.g. 25", "örn. 25", "masalan 25");
        _genCode.Content = T("присвоить код", "код ыйгаруу", "assign a code", "kod ata", "kod berish");
        _noCode.Content = T("оставить без кода", "кодсуз калтыруу", "leave without code", "kodsuz bırak", "kodsiz qoldirish");
        ToolTip.SetTip(_genCode, T("Внутренний штрихкод EAN-13 (не с 0 и 2) — товар можно будет сканировать и печатать ценник.",
            "Ички EAN-13 штрих-коду (0 жана 2 менен башталбайт) — товарды сканерлеп, баа белгисин басууга болот.",
            "An internal EAN-13 barcode (never starting with 0 or 2) — the item can be scanned and labelled.",
            "Dahili EAN-13 barkod (0 ve 2 ile başlamaz) — ürün okutulabilir ve etiketlenebilir.",
            "Ichki EAN-13 shtrix-kod (0 va 2 bilan boshlanmaydi) — mahsulotni skanerlash va narx yorlig'ini chop etish mumkin."));
        _duplicateOverride.Content = T("Всё равно загрузить эту накладную", "Баары бир ушул накладнойду жүктөө", "Load this invoice anyway", "Yine de bu faturayı yükle", "Baribir shu yuk xatini yuklash");
    }

    /// <summary>Заголовок колонки: длинное слово (кырг. «Сатып алуу») переносится, а не обрезается.</summary>
    private static TextBlock H(string text) => new()
    {
        Text = text, TextWrapping = text.Length > 9 ? TextWrapping.Wrap : TextWrapping.NoWrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MaxHeight = 34,
    };

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, FontSize = 13 };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushText"));
        return t;
    }

    private bool LowScreen()
    {
        try
        {
            var screen = DialogScreenFit.KassaScreen(this);
            return screen is not null && screen.WorkingArea.Height / Math.Max(0.5, screen.Scaling) < 760;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateOptionsToggle()
    {
        _optionsToggle.Content = (_optionsRow.IsVisible ? "▴ " : "▾ ") + T("Параметры", "Параметрлер", "Options", "Seçenekler", "Parametrlar");
    }

    // ------------------------------------------------------------------ таблица

    private void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.CanUserSortColumns = false;
        _grid.CanUserReorderColumns = false;
        _grid.RowHeight = 46;
        _grid.IsReadOnly = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _grid.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = H(T("Статус", "Статус", "Status", "Durum", "Holat")), Width = new DataGridLength(184), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<InvoicePlanRow>((_, _) => new StatusCell(r =>
            {
                r.Confirm();
                RefreshSummary();
            }, r =>
            {
                r.MakeNew(_plan.BuildNewLine);
                RefreshSummary();
            }), supportsRecycling: false),
        });
        _colName = new DataGridTemplateColumn
        {
            Header = H(T("Наименование", "Аталышы", "Name", "Ad", "Nomi")), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180,
            CellTemplate = new FuncDataTemplate<InvoicePlanRow>((_, _) => NameCell(), supportsRecycling: false),
            CellEditingTemplate = new FuncDataTemplate<InvoicePlanRow>((_, _) =>
            {
                var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, MinHeight = 40 };
                box.Bind(TextBox.TextProperty, new Binding("Line.ProductName") { Mode = BindingMode.TwoWay });
                return box;
            }, supportsRecycling: false),
        };
        _grid.Columns.Add(_colName);
        _colBarcode = new DataGridTextColumn { Header = H(T("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod")), Width = new DataGridLength(148), Binding = new Binding("Line.Barcode") };
        _grid.Columns.Add(_colBarcode);
        _colUnit = new DataGridTextColumn { Header = H(T("Ед.", "Бир.", "Unit", "Birim", "Birlik")), Width = new DataGridLength(72), Binding = new Binding("Line.Unit") };
        _grid.Columns.Add(_colUnit);
        _grid.Columns.Add(new DataGridTextColumn { Header = H(T("Кол-во", "Саны", "Qty", "Miktar", "Soni")), Width = new DataGridLength(94), Binding = new Binding("Line.QuantityText") { Converter = WarehouseWindow.CommaDecimal } });
        _grid.Columns.Add(new DataGridTextColumn { Header = H(T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid")), Width = new DataGridLength(100), Binding = new Binding("Line.PurchasePriceText") { Converter = WarehouseWindow.CommaDecimal } });
        _grid.Columns.Add(new DataGridTextColumn { Header = H(T("Нац. %", "Кош. %", "Markup %", "Kâr %", "Ust. %")), Width = new DataGridLength(88), Binding = new Binding("Line.MarkupText") { Converter = WarehouseWindow.CommaDecimal } });
        _grid.Columns.Add(new DataGridTextColumn { Header = H(T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv")), Width = new DataGridLength(104), Binding = new Binding("Line.SalePriceText") { Converter = WarehouseWindow.CommaDecimal } });
        _grid.Columns.Add(new DataGridTextColumn { Header = H(T("Сумма", "Сумма", "Amount", "Tutar", "Summa")), Width = new DataGridLength(112), IsReadOnly = true, Binding = new Binding("Line.LineTotal") { StringFormat = "N2" } });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "", Width = new DataGridLength(58), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<InvoicePlanRow>((_, _) => RemoveCell(), supportsRecycling: false),
        });

        _grid.BeginningEdit += (_, e) =>
        {
            // Название, штрихкод и единицу правим только у новых товаров (у найденного они живут в карточке товара).
            if (e.Row.DataContext is InvoicePlanRow row && (ReferenceEquals(e.Column, _colName) || ReferenceEquals(e.Column, _colBarcode) || ReferenceEquals(e.Column, _colUnit)) && row.IsFound)
                e.Cancel = true;
        };
        _grid.CellEditEnded += (_, _) => Dispatcher.UIThread.Post(RefreshSummary, DispatcherPriority.Background);
    }

    /// <summary>Статус строки (цвет по ключу темы) и, у «похожего» товара, кнопки «✓ Это он» и «≠ Другой товар» прямо в ячейке: на узком окне
    /// таблица прокручивается вбок, а решение по строке остаётся на виду слева.</summary>
    private sealed class StatusCell : Grid
    {
        private InvoicePlanRow? _row;
        private readonly TextBlock _text = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        private readonly Button _confirm;
        private readonly Button _other;

        public StatusCell(Action<InvoicePlanRow> onConfirm, Action<InvoicePlanRow> onOther)
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto");
            _confirm = Small("✓", T("Это он — подтвердить", "Ушул эле — ырастоо", "That's it — confirm", "Bu o — onayla", "Aynan shu — tasdiqlash"));
            _other = Small("≠", T("Это другой товар — создать новым", "Бул башка товар — жаңы кылып түзүү", "A different product — create as new", "Başka bir ürün — yeni olarak oluştur", "Boshqa mahsulot — yangi qilib yaratish"));
            _confirm.Click += (_, _) => { if (_row is not null) onConfirm(_row); };
            _other.Click += (_, _) => { if (_row is not null) onOther(_row); };
            SetColumn(_text, 0);
            SetColumn(_confirm, 1);
            SetColumn(_other, 2);
            Children.Add(_text);
            Children.Add(_confirm);
            Children.Add(_other);
        }

        private static Button Small(string text, string tip)
        {
            var b = new Button { Content = text, Width = 44, Height = 44, MinWidth = 44, MinHeight = 44, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0), FontSize = 16 };
            b.Classes.Add("modal-secondary");
            ToolTip.SetTip(b, tip);
            Avalonia.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_row is not null)
                _row.PropertyChanged -= OnChanged;
            _row = DataContext as InvoicePlanRow;
            if (_row is not null)
                _row.PropertyChanged += OnChanged;
            Refresh();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_row is not null)
                _row.PropertyChanged -= OnChanged;
            _row = null;
        }

        private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (_row is null)
                return;
            _text.Text = _row.StatusText;
            _confirm.IsVisible = _row.NeedsConfirm;
            _other.IsVisible = _row.CanDecide;
            if (this.TryFindResource(_row.StatusBrushKey, ActualThemeVariant, out var brush) && brush is IBrush b)
                _text.Foreground = b;
        }
    }

    private static Control NameCell()
    {
        var main = new TextBlock { FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
        main.Bind(TextBlock.TextProperty, new Binding("Line.ProductName"));
        main.Bind(TextBlock.ForegroundProperty, main.GetResourceObservable("BrushText"));
        var sub = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        sub.Bind(TextBlock.TextProperty, new Binding("SubText"));
        sub.Bind(Visual.IsVisibleProperty, new Binding("HasSubText"));
        sub.Bind(TextBlock.ForegroundProperty, sub.GetResourceObservable("BrushTextSoft"));
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Spacing = 1, Children = { main, sub } };
        // Подсказка с полным текстом (название и замечания длинные).
        panel.Bind(ToolTip.TipProperty, new Binding("SubText"));
        return panel;
    }

    private Control RemoveCell()
    {
        var remove = new Button { Content = "✕", Width = 44, Height = 44, MinWidth = 44, MinHeight = 44, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        remove.Classes.Add("modal-secondary");
        var tip = T("Убрать строку", "Сапты алып салуу", "Remove the line", "Satırı kaldır", "Qatorni olib tashlash");
        ToolTip.SetTip(remove, tip);
        Avalonia.Automation.AutomationProperties.SetName(remove, tip);
        remove.Click += (s, _) =>
        {
            if (s is Button { DataContext: InvoicePlanRow r })
            {
                _grid.CommitEdit();
                _plan.Rows.Remove(r);
                RefreshSummary();
                Dispatcher.UIThread.Post(Refit, DispatcherPriority.Background);
            }
        };
        return remove;
    }

    // ------------------------------------------------------------------ события

    private void WireEvents()
    {
        _supplierButton.Click += (_, _) =>
        {
            _supplierPanel.IsVisible = !_supplierPanel.IsVisible;
            if (_supplierPanel.IsVisible)
            {
                FillSupplierList();
                _supplierSearch.Focus();
            }
        };
        _supplierSearch.TextChanged += (_, _) => FillSupplierList();
        _supplierList.SelectionChanged += (_, e) =>
        {
            if (_supplierListSync || _supplierList.SelectedItem is not PurchaseReceivingService.Supplier picked)
                return;
            _supplier = string.IsNullOrEmpty(picked.Id) ? null : picked;
            _supplierChosenByUser = true;
            _supplierPanel.IsVisible = false;
            UpdateSupplierView();
        };
        _newSupplierButton.Click += async (_, _) =>
        {
            if (_req.CreateSupplier is null)
                return;
            _newSupplierButton.IsEnabled = false;
            try
            {
                var created = await _req.CreateSupplier(this, _req.Suppliers).ConfigureAwait(true);
                if (created is not null)
                {
                    _supplier = created;
                    _supplierChosenByUser = true;
                    UpdateSupplierView();
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"INVOICE_IMPORT: окно «Новый поставщик» не открылось: {ex.Message}", "WARNING");
            }
            finally
            {
                _newSupplierButton.IsEnabled = true;
            }
        };
        _numberBox.TextChanged += (_, _) => RefreshDuplicate();
        _dateBox.TextChanged += (_, _) => RefreshDuplicate();
        _duplicateOverride.IsCheckedChanged += (_, _) => RefreshSummary();
        _riskOverride.IsCheckedChanged += (_, _) => RefreshSummary();
        _withVat.IsCheckedChanged += (_, _) =>
        {
            _grid.CommitEdit();
            _plan.SetPricesWithVat(_withVat.IsChecked == true);
            RefreshSummary();
        };
        _markupBox.TextChanged += (_, _) =>
        {
            _grid.CommitEdit();
            _plan.SetDefaultMarkup(InvoiceText.TryParseNumber(_markupBox.Text, out var m) && m > 0 && m < 1000 ? m : null);
            RefreshSummary();
        };
        _genCode.IsCheckedChanged += (_, _) => _plan.SetGenerateBarcodes(_genCode.IsChecked == true);
        _optionsToggle.Click += (_, _) =>
        {
            _optionsRow.IsVisible = !_optionsRow.IsVisible;
            _optionsUserOpened = _optionsRow.IsVisible;
            UpdateOptionsToggle();
            Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
        };
        _columns.Click += async (_, _) =>
        {
            if (_req.RemapColumns is null)
                return;
            _grid.CommitEdit();
            var next = await _req.RemapColumns(this).ConfigureAwait(true);
            if (next is null)
                return;
            _plan = next;
            _numberBox.Text = string.IsNullOrWhiteSpace(_numberBox.Text) ? _plan.Document.Header.Number : _numberBox.Text;
            BindPlan();
        };
    }

    private bool _supplierListSync;

    private void FillSupplierList()
    {
        var query = (_supplierSearch.Text ?? "").Trim();
        var digits = new string(query.Where(char.IsDigit).ToArray());
        var none = new PurchaseReceivingService.Supplier("", T("Без поставщика", "Жеткирүүчүсүз", "No supplier", "Tedarikçisiz", "Yetkazib beruvchisiz"));
        var items = new List<PurchaseReceivingService.Supplier> { none };
        items.AddRange(_req.Suppliers.Where(s => !string.IsNullOrEmpty(s.Id)
            && (query.Length == 0 || s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (digits.Length >= 3 && new string((s.Phone ?? "").Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal))
                || (digits.Length >= 6 && (s.Inn ?? "").Contains(digits, StringComparison.Ordinal)))));
        _supplierListSync = true;
        try
        {
            _supplierList.ItemsSource = items;
            _supplierList.SelectedItem = null;
        }
        finally
        {
            _supplierListSync = false;
        }
    }

    // ------------------------------------------------------------------ поставщик

    private PurchaseReceivingService.Supplier? ChooseInitialSupplier()
    {
        var h = _plan.Document.Header;
        if (_req.Suppliers.Count > 0 && (h.SupplierName.Length > 0 || h.SupplierInn.Length > 0)
            && PurchaseReceivingService.MatchSupplier(_req.Suppliers, h.SupplierName, h.SupplierInn) is { } matched)
            return matched;
        // В накладной поставщика нет (или он не найден) — остаётся тот, что уже выбран в приёмке.
        return h.SupplierName.Length == 0 && h.SupplierInn.Length == 0 ? _req.CurrentSupplier : null;
    }

    private void UpdateSupplierView()
    {
        var h = _plan.Document.Header;
        ToolTip.SetTip(_supplierButton, null);
        _supplierButton.Content = new TextBlock
        {
            Text = _supplier?.Name ?? T("Выберите поставщика ▾", "Жеткирүүчүнү тандаңыз ▾", "Pick a supplier ▾", "Tedarikçi seçin ▾", "Yetkazib beruvchini tanlang ▾"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        string note;
        if (_supplier is null && (h.SupplierName.Length > 0 || h.SupplierInn.Length > 0))
        {
            var who = h.SupplierName.Length > 0 ? $"«{h.SupplierName}»" : "";
            var inn = h.SupplierInn.Length > 0 ? " ИНН " + h.SupplierInn : "";
            note = T($"Поставщик из накладной — {who}{inn} — среди ваших не найден. Выберите из списка или нажмите ＋, чтобы создать нового. Можно и без поставщика.",
                $"Накладнойдогу жеткирүүчү — {who}{inn} — тизмеден табылган жок. Тизмеден тандаңыз же жаңысын түзүү үчүн ＋ басыңыз. Жеткирүүчүсүз да болот.",
                $"The invoice supplier — {who}{inn} — is not in your list. Pick one or press ＋ to create it. You can also continue without a supplier.",
                $"Faturadaki tedarikçi — {who}{inn} — listenizde yok. Listeden seçin ya da ＋ ile oluşturun. Tedarikçisiz de devam edebilirsiniz.",
                $"Yuk xatidagi yetkazib beruvchi — {who}{inn} — ro'yxatingizda yo'q. Ro'yxatdan tanlang yoki ＋ bilan yarating. Yetkazib beruvchisiz ham davom etish mumkin.");
        }
        else if (_supplier is not null && !_supplierChosenByUser && (h.SupplierName.Length > 0 || h.SupplierInn.Length > 0))
        {
            var byInn = h.SupplierInn.Length > 0 && string.Equals(new string((_supplier.Inn ?? "").Where(char.IsDigit).ToArray()), h.SupplierInn, StringComparison.Ordinal);
            note = byInn
                ? T("Поставщик подобран по ИНН из накладной", "Жеткирүүчү накладнойдогу ИНН боюнча табылды", "Supplier matched by the invoice TIN", "Tedarikçi fatura vergi numarasına göre eşleşti", "Yetkazib beruvchi yuk xatidagi STIR bo'yicha topildi")
                : T("Поставщик подобран по названию из накладной — проверьте", "Жеткирүүчү накладнойдогу аталыш боюнча табылды — текшериңиз", "Supplier matched by the invoice name — please check", "Tedarikçi fatura adına göre eşleşti — kontrol edin", "Yetkazib beruvchi yuk xatidagi nom bo'yicha topildi — tekshiring");
            if (byInn)
            {
                // коротко, чтобы не отнимать строку у таблицы на низком экране: подсказка — всплывающая у кнопки поставщика
                ToolTip.SetTip(_supplierButton, note);
                note = "";
            }
        }
        else
            note = "";
        _supplierNote.Text = note;
        _supplierNote.IsVisible = note.Length > 0;
        RefreshDuplicate();
    }

    // ------------------------------------------------------------------ план, дубль, итоги

    private void BindPlan()
    {
        _grid.ItemsSource = _plan.Rows;
        _withVat.IsVisible = _plan.Document.AnyVat;
        _withVat.IsChecked = _plan.PricesWithVat;
        _plan.ExternalCodes = _req.TakenCodes;
        _plan.SetGenerateBarcodes(_genCode.IsChecked == true);
        var warnings = _plan.Warnings;
        _warningsBox.IsVisible = warnings.Count > 0;
        _warningsText.Text = string.Join("\n", warnings.Select(w => "⚠ " + w));
        // 2026-10-27 (ревью Ск-1/Ск-14): сумма строк не сошлась с итогом файла (часть строк могла не прочитаться) или валюта не сом (цены не пересчитываются) —
        // «Добавить» доступно только после явной отметки; раньше это было только предупреждение, которое легко пропустить.
        var cur = _plan.Document.Header.Currency;
        var riskParts = new List<string>();
        if (_plan.TotalMismatch)
            riskParts.Add(T("сумма строк не совпала с итогом накладной — возможно, часть строк не прочитана", "саптардын суммасы накладнойдун жыйынтыгына дал келген жок — кээ бир саптар окулбай калышы мүмкүн",
                "the sum of lines does not match the invoice total — some lines may be unread", "satır toplamı fatura toplamıyla uyuşmuyor — bazı satırlar okunmamış olabilir", "qatorlar yig'indisi yuk xati jamiga mos kelmadi — ba'zi qatorlar o'qilmagan bo'lishi mumkin"));
        if (_plan.ForeignCurrency)
            riskParts.Add(T($"валюта накладной — {cur}, цены не пересчитываются в сомы", $"накладнойдун валютасы — {cur}, баалар сомго кайра эсептелбейт",
                $"the invoice currency is {cur}, prices are not converted to som", $"fatura para birimi {cur}, fiyatlar soma çevrilmez", $"yuk xati valyutasi — {cur}, narxlar so'mga o'tkazilmaydi"));
        _riskOverride.IsChecked = false;
        _riskOverride.IsVisible = riskParts.Count > 0;
        _riskOverride.Content = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            Text = T("Я проверил(а) файл: ", "Файлды текшердим: ", "I have checked the file: ", "Dosyayı kontrol ettim: ", "Faylni tekshirdim: ") + string.Join("; ", riskParts)
                   + T(" — всё равно добавить", " — баары бир кошуу", " — add anyway", " — yine de ekle", " — baribir qo'shish"),
        };
        if (Content is ModalShell shell && !(double.IsFinite(shell.MaxHeight) && shell.MaxHeight < 620))
            shell.Subtitle = SubtitleText();
        UpdateSupplierView();
        RefreshSummary();
    }

    private DateTime? ParsedDate => InvoiceText.ParseDate(_dateBox.Text);

    private void RefreshDuplicate()
    {
        // 2026-10-27 (ревью Ск-2): ключ дубля не зависит от поставщика в окне — номер, дата, ИНН и итог ИЗ ФАЙЛА (формула — в InvoiceImportRegistry).
        _duplicate = InvoiceImportRegistry.FindDuplicate(_req.FileHash, _numberBox.Text ?? "", ParsedDate,
            _plan.Document.Header.SupplierInn, InvoiceImportRegistry.FileTotalOf(_plan.Document.Header));
        _duplicateBox.IsVisible = _duplicate is not null;
        if (_duplicate is not null)
        {
            var when = _duplicate.LoadedAt.ToString("dd.MM.yyyy HH:mm", Ru);
            var who = _duplicate.SupplierName.Length > 0 ? _duplicate.SupplierName : "—";
            var state = _duplicate.Posted
                ? T("оприходована", "кирешеге алынган", "already received", "stoğa alınmış", "kirim qilingan")
                : T("лежит в приёмке (ещё не оприходована)", "кабыл алууда (кирешеге алына элек)", "is in the receiving list (not received yet)", "mal kabulde (henüz stoğa alınmadı)", "qabulda (hali kirim qilinmagan)");
            _duplicateText.Text = T($"Накладная с таким номером уже загружалась: {when} (№ {_duplicate.Number}, поставщик {who}, строк {_duplicate.Items}) — {state}. Повторная загрузка задвоит приход и долг поставщику.",
                $"Мындай номерлүү накладной мурун жүктөлгөн: {when} (№ {_duplicate.Number}, жеткирүүчү {who}, саптар {_duplicate.Items}) — {state}. Кайра жүктөө кирешени жана жеткирүүчүгө карызды эки эсе кылат.",
                $"An invoice with this number was already loaded on {when} (no. {_duplicate.Number}, supplier {who}, {_duplicate.Items} lines) — {state}. Loading it again would double the receipt and the supplier debt.",
                $"Bu numaralı fatura daha önce yüklendi: {when} (no {_duplicate.Number}, tedarikçi {who}, {_duplicate.Items} satır) — {state}. Tekrar yüklemek girişi ve tedarikçi borcunu ikiye katlar.",
                $"Bunday raqamli yuk xati avval yuklangan: {when} (№ {_duplicate.Number}, yetkazib beruvchi {who}, {_duplicate.Items} qator) — {state}. Qayta yuklash kirim va yetkazib beruvchiga qarzni ikki baravar qiladi.");
        }
        else
            _duplicateOverride.IsChecked = false;
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var totals = _plan.Totals();
        var unconfirmed = _plan.UnconfirmedCount;
        var unrecognized = _plan.UnrecognizedCount;
        var parts = new List<string>
        {
            T($"Строк: {totals.Included}", $"Саптар: {totals.Included}", $"Lines: {totals.Included}", $"Satır: {totals.Included}", $"Qatorlar: {totals.Included}"),
            T($"сумма закупки {Money(totals.LinesTotal)}", $"сатып алуу суммасы {Money(totals.LinesTotal)}", $"purchase total {Money(totals.LinesTotal)}", $"alış toplamı {Money(totals.LinesTotal)}", $"xarid summasi {Money(totals.LinesTotal)}"),
        };
        if (totals.FileTotal is { } ft)
            parts.Add(totals.Matches
                ? T($"в накладной {Money(ft)} — сходится", $"накладнойдо {Money(ft)} — дал келет", $"invoice says {Money(ft)} — matches", $"faturada {Money(ft)} — uyuşuyor", $"yuk xatida {Money(ft)} — mos")
                : T($"В НАКЛАДНОЙ {Money(ft)} — РАСХОЖДЕНИЕ {Money(totals.Difference)}", $"НАКЛАДНОЙДО {Money(ft)} — АЙЫРМА {Money(totals.Difference)}", $"INVOICE SAYS {Money(ft)} — DIFFERENCE {Money(totals.Difference)}",
                    $"FATURADA {Money(ft)} — FARK {Money(totals.Difference)}", $"YUK XATIDA {Money(ft)} — FARQ {Money(totals.Difference)}"));
        else
            parts.Add(T("итога цифрой в файле нет", "файлда сан менен жыйынтык жок", "no numeric total in the file", "dosyada rakamla toplam yok", "faylda raqamli jami yo'q"));
        if (unconfirmed > 0)
            parts.Add(T($"подтвердите похожие товары: {unconfirmed}", $"окшош товарларды ырастаңыз: {unconfirmed}", $"confirm similar items: {unconfirmed}", $"benzer ürünleri onaylayın: {unconfirmed}", $"o'xshash mahsulotlarni tasdiqlang: {unconfirmed}"));
        if (unrecognized > 0)
            parts.Add(T($"не распознано (не пойдут в приёмку): {unrecognized}", $"таанылган жок (кабыл алууга кирбейт): {unrecognized}", $"not recognized (will be skipped): {unrecognized}",
                $"tanınmadı (alınmayacak): {unrecognized}", $"tanilmadi (qabulga kirmaydi): {unrecognized}"));
        _summary.Text = string.Join(" · ", parts);
        _summary.Foreground = null;
        _summary.Bind(TextBlock.ForegroundProperty, _summary.GetResourceObservable(!totals.Matches || unconfirmed > 0 ? "BrushWarning" : "BrushTextSoft"));

        var blockedByDuplicate = _duplicate is not null && _duplicateOverride.IsChecked != true;
        var blockedByRisk = _riskOverride.IsVisible && _riskOverride.IsChecked != true;
        _add.IsEnabled = totals.Included > 0 && unconfirmed == 0 && !blockedByDuplicate && !blockedByRisk;
        _add.Content = T($"Добавить в приёмку ({totals.Included})", $"Кабыл алууга кошуу ({totals.Included})", $"Add to receiving ({totals.Included})", $"Mal kabule ekle ({totals.Included})", $"Qabulga qo'shish ({totals.Included})");
        Avalonia.Automation.AutomationProperties.SetName(_add, _add.Content?.ToString());
    }

    // ------------------------------------------------------------------ готово

    private void Commit()
    {
        _grid.CommitEdit();
        // 2026-10-27 (ревью Ск-4): правка количества/названия при CommitEdit могла вернуть «нераспознанную» похожую строку в список и снять готовность —
        // состояние кнопки пересчитываем сейчас, а не ждём отложенного RefreshSummary.
        RefreshSummary();
        if (!_add.IsEnabled || _plan.UnconfirmedCount > 0)
            return;
        var lines = _plan.ToReceivingLines(_req.TakenCodes());
        if (lines.Count == 0)
            return;
        var totals = _plan.Totals();
        var h = _plan.Document.Header;
        Chosen = new Result(lines, _supplier, (_numberBox.Text ?? "").Trim(), ParsedDate, totals.LinesTotal, _plan.UnrecognizedCount,
            h.SupplierInn, InvoiceImportRegistry.FileTotalOf(h), h.SupplierName);
        Close();
    }

    /// <summary>Поставщик, выбранный в окне (для проверки и снимков стенда).</summary>
    public PurchaseReceivingService.Supplier? SelectedSupplier => _supplier;

    /// <summary>Для стенда: окно собрано, показать выбранного поставщика и плашки.</summary>
    public void Prepare() => UpdateSupplierView();
}
