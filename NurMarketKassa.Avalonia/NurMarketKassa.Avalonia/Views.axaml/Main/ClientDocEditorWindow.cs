using NurMarketKassa.Core.Application;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-21, владелец: «КП и товарную накладную, выставление счёта — тоже дизайн, пару вариантов подготовь, далее сделай её
/// редактируемой». Редактор документа клиента: номер (автонумерация, можно править — без повторов), статус, шаблон бланка
/// (классический / современный / минимал — запоминается по виду документа), покупатель (клиент NurCRM или «+ Новый клиент»),
/// дата и срок, строки (поиск или скан из каталога, количество, ед., цена, скидка — правка в таблице; свободная строка; строки
/// из чека клиента), скидка на документ, НДС, сумма прописью, примечание, реквизиты продавца (с запоминанием) и логотип. Справа —
/// живой предпросмотр листа A4 (на узком экране — вкладкой). Черновик сохраняется сам.
///
/// Enter в поле добавления товара добавляет ОДНУ строку (тот же товар ещё раз — +1 к количеству этой строки), поле очищается;
/// повторный/двойной Enter ничего не дублирует и ничего не удаляет (жалоба владельца на старую программу) — см. <see cref="DocLineAdder"/>.</summary>
public sealed partial class ClientDocEditorWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly ClientDoc _doc;
    private ClientDocProfile _profile;
    /// <summary>2026-10-21 (ревью документов, A5): реквизиты на момент открытия/последнего сохранения — в общие уходят только
    /// поля, изменённые в этом окне (устаревшие остальные не затирают изменённые на другом ПК).</summary>
    private ClientDocProfile _profileBase;
    /// <summary>2026-10-21 (ревью документов, мелочи): итог до пересчёта — «Оплачен» при выросшей сумме становится «Частично».</summary>
    private double? _lastTotal;
    private bool _goneShown;
    private bool _isNew;
    private bool _filling;
    private bool _dirty;
    private bool _profileDirty;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private int _renderGen;
    /// <summary>2026-10-21: снимок документа на момент последнего сохранения — TextChanged у TextBox приходит после заполнения
    /// полей (асинхронно), и открытый без правок документ не должен «сохраняться» (дата изменения, отправка на сервер).</summary>
    private string _savedSnapshot = "";
    private (string? Path, byte[]? Bytes) _logo;
    public readonly DocLineAdder Adder = new();

    // шапка
    private readonly TextBox _number = new() { Width = 130, MaxLength = 30, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _numberWarn = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ComboBox _status = new() { MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button[] _tplButtons = new Button[ClientDocRenderer.Templates.Length];
    private readonly Button _viewForm;
    private readonly Button _viewPreview;
    private readonly Border _viewSeg;
    private bool _showPreviewNarrow;
    private readonly TextBlock _tplLabel;
    private bool _narrow;

    // покупатель
    private readonly TextBox _buyerSearch;
    private readonly ListBox _buyerList = new() { MaxHeight = 190, IsVisible = false };
    private readonly TextBox _client;
    private readonly TextBox _inn;
    private readonly TextBox _phone;
    private readonly TextBox _address;
    private List<ClientDocsUi.Buyer> _buyers = new();

    // даты и условия
    private readonly CalendarDatePicker _date = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42 };
    private readonly CalendarDatePicker _until = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42 };
    private readonly StackPanel _untilBox = new() { Spacing = 4 };
    private readonly TextBox _basedOn;
    private readonly ComboBox _lang = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42 };
    private readonly CheckBox _vatOn = new();
    private readonly TextBox _vat = new() { Width = 64, Text = "12", HorizontalContentAlignment = HorizontalAlignment.Right };

    // строки
    public readonly TextBox AddBox;
    private readonly ListBox _suggest = new() { MaxHeight = 230, IsVisible = false };
    private readonly TextBlock _addHint = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    public readonly StackPanel RowsPanel = new() { Spacing = 0 };
    private readonly Button _fromSale;

    // итоги
    private readonly TextBox _docDiscount = new() { Width = 120, HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _subtotal = new() { FontSize = 13.5 };
    private readonly TextBlock _discountText = new() { FontSize = 13.5 };
    private readonly TextBlock _vatText = new() { FontSize = 13.5 };
    private readonly TextBlock _total = new() { FontSize = 20, FontWeight = FontWeight.Bold };
    private readonly TextBlock _words = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _note;
    private readonly CheckBox _rememberTerms = new();

    // продавец
    private readonly TextBlock _sellerSummary = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _sellerFields = new() { Spacing = 10, IsVisible = false };
    private readonly Dictionary<string, TextBox> _seller = new();
    private readonly TextBlock _logoText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _stamp = new();

    // предпросмотр
    private readonly StackPanel _pages = new() { Spacing = 14, Margin = new Thickness(14) };
    private readonly TextBlock _previewInfo = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _previewCard;
    private readonly ScrollViewer _formScroll;
    private readonly Grid _main = new();

    // низ
    private readonly TextBlock _saveState = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Button _openFile;
    private string? _lastFile;
    private readonly Button _kindAction;
    private readonly Button _payAction;

    public ClientDoc Doc => _doc;

    public ClientDocEditorWindow(ClientDoc doc, bool isNew)
    {
        _doc = doc;
        _isNew = isNew;
        _profile = ClientDocsStore.Profile;
        _profileBase = _profile.Clone();
        _doc.Template ??= _profile.Templates.TryGetValue(ClientDocProfile.KindKey(doc.Kind), out var t) ? t : ClientDocRenderer.DefaultTemplate(doc.Kind);
        _doc.Lang ??= "ru";

        Title = ClientDocsUi.KindLong(doc.Kind) + " " + doc.Number;
        Width = 1360;
        Height = 900;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Res(this, BackgroundProperty, "BrushWindowBackdrop");

        // ── шапка: вид, номер, статус, шаблон ──
        var kindTitle = new TextBlock { Text = ClientDocsUi.KindLong(doc.Kind), FontSize = 20, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Res(kindTitle, TextBlock.ForegroundProperty, "BrushText");
        var noLabel = new TextBlock { Text = "№", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Res(noLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        Avalonia.Automation.AutomationProperties.SetName(_number, T("Номер документа", "Документтин номери", "Document number", "Belge numarası", "Hujjat raqami"));
        _number.TextChanged += (_, _) => NumberChanged();
        Res(_numberWarn, TextBlock.ForegroundProperty, "BrushDanger");
        Avalonia.Automation.AutomationProperties.SetName(_status, T("Статус", "Статус", "Status", "Durum", "Holat"));
        foreach (var (_, text) in ClientDocsUi.Statuses(doc.Kind))
            _status.Items.Add(text);
        _status.SelectionChanged += (_, _) => StatusChanged();

        string[] tplText = { T("Классический", "Классикалык", "Classic", "Klasik", "Klassik"), T("Современный", "Заманбап", "Modern", "Modern", "Zamonaviy"), T("Минимал", "Минимал", "Minimal", "Minimal", "Minimal"), T("Фирменный NUR CRM", "Фирмалык NUR CRM", "NUR CRM brand", "NUR CRM kurumsal", "NUR CRM brend") };
        var tplPanel = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < ClientDocRenderer.Templates.Length; i++)
        {
            var name = ClientDocRenderer.Templates[i];
            _tplButtons[i] = UiKit.Seg(tplText[i], name == _doc.Template);
            _tplButtons[i].Click += (_, _) => SetTemplate(name);
            tplPanel.Children.Add(_tplButtons[i]);
        }
        var tplSeg = UiKit.Segment(tplPanel);
        tplSeg.VerticalAlignment = VerticalAlignment.Center;
        _tplLabel = new TextBlock { Text = T("Бланк", "Бланк", "Template", "Şablon", "Blanka"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Res(_tplLabel, TextBlock.ForegroundProperty, "BrushTextSoft");

        _viewForm = UiKit.Seg(T("Документ", "Документ", "Document", "Belge", "Hujjat"), true);
        _viewPreview = UiKit.Seg(T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Ko'rib chiqish"), false);
        _viewForm.Click += (_, _) => SetNarrowView(false);
        _viewPreview.Click += (_, _) => SetNarrowView(true);
        _viewSeg = UiKit.Segment(new StackPanel { Orientation = Orientation.Horizontal, Children = { _viewForm, _viewPreview } });
        _viewSeg.VerticalAlignment = VerticalAlignment.Center;
        _viewSeg.IsVisible = false;

        var headLeft = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headLeft.Children.Add(kindTitle);
        headLeft.Children.Add(noLabel);
        headLeft.Children.Add(_number);
        _status.Margin = new Thickness(10, 0, 0, 0);
        headLeft.Children.Add(_status);
        // 2026-10-31 (стресс-тест 1.17.68, 800×480): правая часть шапки (бланк + «Документ/Предпросмотр») занимала почти всю ширину и сжимала левую
        // до нуля — поле «Номер документа» уходило за край, «Предпросмотр» обрезался. Теперь это WrapPanel, а на узком окне (< 900) она переносится
        // второй строкой под номер (ApplyLayout).
        tplSeg.Margin = new Thickness(0, 2, 10, 2);
        _viewSeg.Margin = new Thickness(0, 2, 0, 2);
        var headRight = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _tplLabel, tplSeg, _viewSeg } };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 0, 0, 4) };
        _headLeft = headLeft;
        _headRight = headRight;
        head.Children.Add(headLeft);
        Grid.SetColumn(headRight, 1);
        head.Children.Add(headRight);
        var headBlock = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 10), Children = { head, _numberWarn } };

        // ── покупатель ──
        _buyerSearch = UiKit.Search(this, T("Найти клиента: имя, телефон или ИНН", "Кардарды табуу: аты, телефону же ИНН", "Find a client: name, phone or TIN", "Müşteri bul: ad, telefon veya VKN", "Mijozni topish: ism, telefon yoki STIR"), 42);
        Avalonia.Automation.AutomationProperties.SetName(_buyerSearch, T("Поиск клиента", "Кардарды издөө", "Client search", "Müşteri ara", "Mijoz qidirish"));
        _buyerSearch.TextChanged += (_, _) => FillBuyerList();
        _buyerSearch.KeyDown += BuyerSearchKeyDown;
        _buyerList.SelectionChanged += (_, _) => { };
        _buyerList.DoubleTapped += (_, _) => PickBuyer(_buyerList.SelectedItem as ListBoxItem);
        _buyerList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                PickBuyer(_buyerList.SelectedItem as ListBoxItem);
            }
        };
        // 2026-10-22 (модалки, пакет 2): касание по кнопке «Выбрать» в строке выбирает именно эту строку (её обработчик) —
        // общее касание списка тогда не срабатывает, иначе оно взяло бы прежнюю выделенную строку.
        _buyerList.Tapped += (_, e) =>
        {
            if (e.Source is Avalonia.Visual v && (v as Button ?? Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(v)) is not null)
                return;
            PickBuyer(_buyerList.SelectedItem as ListBoxItem);
        };
        var newClient = UiKit.Ghost(this, T("+ Новый клиент", "+ Жаңы кардар", "+ New client", "+ Yeni müşteri", "+ Yangi mijoz"));
        newClient.Height = 42;
        newClient.Focusable = true;
        newClient.Click += async (_, _) => await NewBuyerAsync().ConfigureAwait(true);
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        searchRow.Children.Add(_buyerSearch);
        newClient.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(newClient, 1);
        searchRow.Children.Add(newClient);
        _client = Box(T("Покупатель / компания", "Сатып алуучу / компания", "Buyer / company", "Alıcı / şirket", "Xaridor / kompaniya"), v => _doc.Client = v.Trim());
        _inn = Box("ИНН", v => _doc.Inn = v.Trim());
        _phone = Box(T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), v => _doc.Phone = v.Trim());
        _address = Box(T("Адрес", "Дарек", "Address", "Adres", "Manzil"), v => _doc.Address = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        var buyerCard = Card(T("Покупатель", "Сатып алуучу", "Buyer", "Alıcı", "Xaridor"),
            searchRow, _buyerList, TwoCols(Field(T("Покупатель / компания", "Сатып алуучу / компания", "Buyer / company", "Alıcı / şirket", "Xaridor / kompaniya"), _client), Field("ИНН", _inn)),
            TwoCols(Field(T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), _phone), Field(T("Адрес", "Дарек", "Address", "Adres", "Manzil"), _address)));

        // ── даты и условия ──
        _date.SelectedDateChanged += (_, _) =>
        {
            // 2026-10-21: события полей приходят и после заполнения — то же значение не считается правкой.
            if (_filling || _date.SelectedDate is not { } d || d.Date == _doc.Date.Date)
                return;
            _doc.Date = d.Date + DateTime.Now.TimeOfDay;
            Changed();
        };
        _until.SelectedDateChanged += (_, _) =>
        {
            if (_filling || _until.SelectedDate is not { } d || d.Date == _doc.Until.Date)
                return;
            _doc.Until = d.Date;
            Changed();
        };
        foreach (var p in new[] { _date, _until })
            p.SelectedDateFormat = CalendarDatePickerFormat.Short;
        var untilLabel = Label(doc.Kind == ClientDocKind.Quote ? T("Действует до", "Жарактуу мөөнөтү", "Valid until", "Geçerlilik", "Amal qiladi")
            : T("Оплатить до", "Төлөө мөөнөтү", "Pay by", "Son ödeme", "To'lash muddati"));
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var days in new[] { 3, 7, 14, 30 })
        {
            var chip = UiKit.Chip(this, T($"+{days} дн.", $"+{days} күн", $"+{days} d", $"+{days} gün", $"+{days} kun"), false);
            // 2026-10-22 (модалки, пакет 2): касание 44 px (было 32).
            chip.MinHeight = 44;
            chip.Padding = new Thickness(12, 2);
            chip.Margin = new Thickness(0, 0, 6, 4);
            chip.Focusable = true;
            chip.Click += (_, _) =>
            {
                _doc.Until = _doc.Date.Date.AddDays(days);
                _filling = true;
                _until.SelectedDate = _doc.Until;
                _filling = false;
                Changed();
            };
            chips.Children.Add(chip);
        }
        _untilBox.Children.Add(untilLabel);
        _untilBox.Children.Add(_until);
        _untilBox.Children.Add(chips);
        _untilBox.IsVisible = doc.Kind != ClientDocKind.Waybill;
        _basedOn = Box(T("Основание", "Негиз", "Basis", "Dayanak", "Asos"), v => _doc.BasedOn = v.Trim());
        _basedOn.Watermark = doc.Kind == ClientDocKind.Waybill ? T("например: счёт СЧ-0012 или договор № 5", "мисалы: эсеп СЧ-0012 же келишим № 5", "e.g. invoice СЧ-0012 or contract No. 5", "örn.: fatura СЧ-0012 veya sözleşme No. 5", "masalan: hisob СЧ-0012 yoki shartnoma № 5")
            : T("необязательно", "милдеттүү эмес", "optional", "isteğe bağlı", "ixtiyoriy");
        _lang.Items.Add("Русский");
        _lang.Items.Add("Кыргызча");
        Avalonia.Automation.AutomationProperties.SetName(_lang, T("Язык бланка", "Бланктын тили", "Document language", "Belge dili", "Hujjat tili"));
        _lang.SelectionChanged += (_, _) =>
        {
            var lang = _lang.SelectedIndex == 1 ? "ky" : "ru";
            if (_filling || lang == _doc.Lang)
                return;
            _doc.Lang = lang;
            Changed();
        };
        _vatOn.Content = T("НДС в том числе, %", "КНС анын ичинде, %", "VAT included, %", "KDV dahil, %", "QQS shu jumladan, %");
        _vatOn.IsCheckedChanged += (_, _) => VatChanged();
        _vat.TextChanged += (_, _) => VatChanged();
        Avalonia.Automation.AutomationProperties.SetName(_vat, T("Ставка НДС", "КНС ставкасы", "VAT rate", "KDV oranı", "QQS stavkasi"));
        var vatRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 22, 0, 0), Children = { _vatOn, _vat } };
        var dateCard = Card(T("Даты и условия", "Күндөр жана шарттар", "Dates and terms", "Tarihler ve koşullar", "Sanalar va shartlar"),
            TwoCols(Field(T("Дата документа", "Документтин күнү", "Document date", "Belge tarihi", "Hujjat sanasi"), _date), _untilBox),
            TwoCols(Field(T("Основание", "Негиз", "Basis", "Dayanak", "Asos"), _basedOn), Field(T("Язык бланка", "Бланктын тили", "Document language", "Belge dili", "Hujjat tili"), _lang)),
            vatRow);

        // ── строки ──
        AddBox = UiKit.Input(this, T("Товар: название, штрихкод или скан — Enter добавит строку", "Товар: аты, штрихкод же скан — Enter сап кошот",
            "Product: name, barcode or scan — Enter adds a line", "Ürün: ad, barkod veya okutma — Enter satır ekler", "Tovar: nomi, shtrix-kod yoki skan — Enter qator qo'shadi"), 44);
        Avalonia.Automation.AutomationProperties.SetName(AddBox, T("Добавить товар", "Товар кошуу", "Add product", "Ürün ekle", "Tovar qo'shish"));
        AddBox.TextChanged += (_, _) => FillSuggestions();
        AddBox.KeyDown += AddBoxKeyDown;
        _suggest.Tapped += (_, _) => AddSuggestion(_suggest.SelectedItem as ListBoxItem);
        _suggest.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AddSuggestion(_suggest.SelectedItem as ListBoxItem);
            }
        };
        Res(_addHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        var freeLine = UiKit.Ghost(this, T("+ Свободная строка", "+ Эркин сап", "+ Free line", "+ Serbest satır", "+ Erkin qator"));
        freeLine.Height = 44;
        freeLine.Focusable = true;
        ToolTip.SetTip(freeLine, T("Услуга, доставка, монтаж — не из каталога", "Кызмат, жеткирүү, орнотуу — каталогдон эмес", "Service, delivery, installation — not from the catalog", "Hizmet, teslimat, montaj — katalog dışı", "Xizmat, yetkazib berish, o'rnatish — katalogdan emas"));
        freeLine.Click += (_, _) => AddFreeLine();
        _fromSale = UiKit.Ghost(this, T("Из чека клиента", "Кардардын чегинен", "From a client's receipt", "Müşteri fişinden", "Mijoz chekidan"));
        _fromSale.Height = 44;
        _fromSale.Focusable = true;
        _fromSale.Click += async (_, _) => await ShowSalesAsync().ConfigureAwait(true);
        var addRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        addRow.Children.Add(AddBox);
        freeLine.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(freeLine, 1);
        addRow.Children.Add(freeLine);
        _fromSale.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_fromSale, 2);
        addRow.Children.Add(_fromSale);
        var linesHead = LineGrid(Th("№"), Th(T("Наименование", "Аталышы", "Item", "Ürün", "Nomi")), Th(T("Ед.", "Бирд.", "Unit", "Birim", "Birl.")),
            Th(T("Кол-во", "Саны", "Qty", "Adet", "Soni"), true), Th(T("Цена", "Баасы", "Price", "Fiyat", "Narxi"), true), Th(T("Скидка %", "Арзанд. %", "Disc. %", "İnd. %", "Cheg. %"), true),
            Th(T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), true), new Control());
        var headBorder = new Border { Padding = new Thickness(8, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = linesHead, Margin = new Thickness(0, 10, 0, 0) };
        Res(headBorder, Border.BackgroundProperty, "BrushPanelSoft");
        Res(headBorder, Border.BorderBrushProperty, "BrushBorder");
        var linesCard = Card(T("Товары и услуги", "Товарлар жана кызматтар", "Products and services", "Ürünler ve hizmetler", "Tovarlar va xizmatlar"), addRow, _suggest, _addHint, headBorder, RowsPanel);

        // ── итоги ──
        Avalonia.Automation.AutomationProperties.SetName(_docDiscount, T("Скидка на документ", "Документке арзандатуу", "Document discount", "Belge indirimi", "Hujjatga chegirma"));
        _docDiscount.Watermark = T("0 или 5%", "0 же 5%", "0 or 5%", "0 veya %5", "0 yoki 5%");
        _docDiscount.TextChanged += (_, _) => DiscountChanged();
        foreach (var tb in new[] { _subtotal, _discountText, _vatText, _total })
            Res(tb, TextBlock.ForegroundProperty, "BrushText");
        Res(_words, TextBlock.ForegroundProperty, "BrushTextSoft");
        _docDiscount.Height = 40;
        _docDiscount.VerticalAlignment = VerticalAlignment.Center;
        var discRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top, Children = { Label(T("Скидка на документ, сом или %", "Документке арзандатуу, сом же %", "Document discount, som or %", "Belge indirimi, som veya %", "Hujjatga chegirma, so'm yoki %")), _docDiscount } };
        ((TextBlock)discRow.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        var totalsRight = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, Children = { _subtotal, _discountText, _vatText, _total } };
        foreach (var tb in totalsRight.Children.OfType<TextBlock>())
            tb.HorizontalAlignment = HorizontalAlignment.Right;
        var totalsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        totalsGrid.Children.Add(discRow);
        Grid.SetColumn(totalsRight, 1);
        totalsGrid.Children.Add(totalsRight);
        var totalsCard = Card(T("Итог", "Жыйынтык", "Total", "Toplam", "Jami"), totalsGrid, _words);

        // ── примечание ──
        _note = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxLength = 1500 };
        _note.Watermark = doc.Kind switch
        {
            ClientDocKind.Quote => T("Условия: доставка, гарантия, сроки поставки…", "Шарттар: жеткирүү, кепилдик, жеткирүү мөөнөтү…", "Terms: delivery, warranty, lead time…", "Koşullar: teslimat, garanti, teslim süresi…", "Shartlar: yetkazib berish, kafolat, muddat…"),
            _ => T("Примечание к документу", "Документке эскертүү", "Note to the document", "Belge notu", "Hujjatga izoh"),
        };
        Avalonia.Automation.AutomationProperties.SetName(_note, T("Примечание и условия", "Эскертүү жана шарттар", "Note and terms", "Not ve koşullar", "Izoh va shartlar"));
        _note.TextChanged += (_, _) =>
        {
            var note = string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text!.Trim();
            if (_filling || note == _doc.Note)
                return;
            _doc.Note = note;
            if (_rememberTerms.IsChecked == true)
                RememberTerms();
            Changed();
        };
        _rememberTerms.Content = T("Подставлять в новые документы этого вида", "Ушул түрдөгү жаңы документтерге коюу", "Use in new documents of this kind", "Bu türdeki yeni belgelerde kullan", "Shu turdagi yangi hujjatlarga qo'yish");
        _rememberTerms.IsCheckedChanged += (_, _) =>
        {
            if (_filling)
                return;
            if (_rememberTerms.IsChecked == true)
                RememberTerms();
            else
            {
                _profile.Terms.Remove(ClientDocProfile.KindKey(_doc.Kind));
                ProfileChanged();
            }
        };
        var noteCard = Card(T("Примечание и условия", "Эскертүү жана шарттар", "Note and terms", "Not ve koşullar", "Izoh va shartlar"), _note, _rememberTerms);

        // ── продавец ──
        var editSeller = UiKit.Ghost(this, T("Изменить реквизиты", "Реквизиттерди өзгөртүү", "Edit details", "Bilgileri düzenle", "Rekvizitlarni o'zgartirish"));
        editSeller.Height = 44;
        editSeller.Focusable = true;
        editSeller.Click += (_, _) =>
        {
            _sellerFields.IsVisible = !_sellerFields.IsVisible;
            editSeller.Content = _sellerFields.IsVisible ? T("Скрыть реквизиты", "Реквизиттерди жашыруу", "Hide details", "Bilgileri gizle", "Rekvizitlarni yashirish")
                : T("Изменить реквизиты", "Реквизиттерди өзгөртүү", "Edit details", "Bilgileri düzenle", "Rekvizitlarni o'zgartirish");
        };
        Res(_sellerSummary, TextBlock.ForegroundProperty, "BrushText");
        var sellerHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        sellerHead.Children.Add(_sellerSummary);
        Grid.SetColumn(editSeller, 1);
        editSeller.Margin = new Thickness(10, 0, 0, 0);
        sellerHead.Children.Add(editSeller);
        TextBox S(string key, string label)
        {
            var b = Box(label, v => SellerChanged(key, v));
            _seller[key] = b;
            return b;
        }
        _sellerFields.Children.Add(TwoCols(Field(T("Название магазина / компании", "Дүкөндүн / компаниянын аталышы", "Shop / company name", "Mağaza / şirket adı", "Do'kon / kompaniya nomi"), S("name", "name")), Field("ИНН", S("inn", "inn"))));
        _sellerFields.Children.Add(TwoCols(Field(T("Адрес", "Дарек", "Address", "Adres", "Manzil"), S("address", "address")), Field(T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), S("phone", "phone"))));
        _sellerFields.Children.Add(TwoCols(Field(T("Банк", "Банк", "Bank", "Banka", "Bank"), S("bank", "bank")), Field("БИК", S("bik", "bik"))));
        _sellerFields.Children.Add(TwoCols(Field(T("Расчётный счёт", "Эсептешүү эсеби", "Bank account", "Banka hesabı", "Hisob raqami"), S("account", "account")), Field(T("Руководитель", "Жетекчи", "Director", "Yönetici", "Rahbar"), S("director", "director"))));
        _sellerFields.Children.Add(TwoCols(Field(T("Бухгалтер", "Бухгалтер", "Accountant", "Muhasebeci", "Buxgalter"), S("accountant", "accountant")), new Control()));
        var chooseLogo = UiKit.Ghost(this, T("Выбрать логотип…", "Логотип тандоо…", "Choose a logo…", "Logo seç…", "Logotip tanlash…"));
        chooseLogo.Height = 44;
        chooseLogo.Focusable = true;
        chooseLogo.Click += async (_, _) => await ChooseLogoAsync().ConfigureAwait(true);
        var removeLogo = UiKit.Ghost(this, T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"));
        removeLogo.Height = 44;
        removeLogo.Focusable = true;
        removeLogo.Click += (_, _) =>
        {
            _profile.LogoPath = null;
            _logo = (null, null);
            ProfileChanged();
            UpdateSellerSummary();
        };
        Res(_logoText, TextBlock.ForegroundProperty, "BrushTextSoft");
        _sellerFields.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { chooseLogo, removeLogo, _logoText } });
        _stamp.Content = T("Место для печати (М.П.)", "Мөөр үчүн орун (М.О.)", "Space for the stamp", "Kaşe alanı", "Muhr uchun joy");
        _stamp.IsCheckedChanged += (_, _) =>
        {
            if (_filling || _profile.ShowStamp == (_stamp.IsChecked == true))
                return;
            _profile.ShowStamp = _stamp.IsChecked == true;
            ProfileChanged();
        };
        _sellerFields.Children.Add(_stamp);
        var sellerNote = new TextBlock
        {
            Text = T("Реквизиты запоминаются для всех документов и уходят в общие настройки компании (логотип — только на этом ПК).",
                "Реквизиттер бардык документтер үчүн эсте калат жана компаниянын жалпы жөндөөлөрүнө кетет (логотип — ушул ПКда гана).",
                "Details are remembered for all documents and saved to shared company settings (the logo — on this PC only).",
                "Bilgiler tüm belgeler için hatırlanır ve şirket ortak ayarlarına kaydedilir (logo — yalnızca bu bilgisayarda).",
                "Rekvizitlar barcha hujjatlar uchun eslab qolinadi va kompaniya umumiy sozlamalariga saqlanadi (logotip — faqat shu kompyuterda)."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        Res(sellerNote, TextBlock.ForegroundProperty, "BrushTextSoft");
        _sellerFields.Children.Add(sellerNote);
        var sellerCard = Card(T("Продавец (реквизиты)", "Сатуучу (реквизиттер)", "Seller (details)", "Satıcı (bilgiler)", "Sotuvchi (rekvizitlar)"), sellerHead, _sellerFields);

        var form = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 12, 0), Children = { buyerCard, linesCard, totalsCard, dateCard, noteCard, sellerCard } };
        _formScroll = new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        // ── предпросмотр ──
        Res(_previewInfo, TextBlock.ForegroundProperty, "BrushTextSoft");
        var previewHead = new Border { Padding = new Thickness(14, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
        Res(previewHead, Border.BorderBrushProperty, "BrushBorder");
        var previewTitle = new TextBlock { Text = T("Предпросмотр A4", "A4 алдын ала көрүү", "A4 preview", "A4 önizleme", "A4 ko'rinish"), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Res(previewTitle, TextBlock.ForegroundProperty, "BrushText");
        var ph = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        ph.Children.Add(previewTitle);
        Grid.SetColumn(_previewInfo, 1);
        ph.Children.Add(_previewInfo);
        previewHead.Child = ph;
        var pagesScroll = new ScrollViewer { Content = _pages, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var pv = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        pv.Children.Add(previewHead);
        Grid.SetRow(pagesScroll, 1);
        pv.Children.Add(pagesScroll);
        _previewCard = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = pv };
        Res(_previewCard, Border.BackgroundProperty, "BrushPanelSoft");
        Res(_previewCard, Border.BorderBrushProperty, "BrushBorder");

        _main.Children.Add(_formScroll);
        _main.Children.Add(_previewCard);

        // ── низ ──
        Res(_saveState, TextBlock.ForegroundProperty, "BrushTextSoft");
        Res(_message, TextBlock.ForegroundProperty, "BrushText");
        _openFile = UiKit.Ghost(this, T("Открыть файл", "Файлды ачуу", "Open file", "Dosyayı aç", "Faylni ochish"));
        _openFile.Height = 44;
        _openFile.IsVisible = false;
        _openFile.Focusable = true;
        _openFile.Click += (_, _) => OpenPath(_lastFile);

        _kindAction = Btn(doc.Kind == ClientDocKind.Quote ? T("Сделать счёт", "Эсеп түзүү", "Make an invoice", "Fatura oluştur", "Hisob yaratish")
            : T("Сделать накладную", "Накладной түзүү", "Make a waybill", "İrsaliye oluştur", "Yuk xati yaratish"));
        _kindAction.IsVisible = doc.Kind != ClientDocKind.Waybill;
        _kindAction.Click += (_, _) => MakeNext();
        _payAction = Btn(T("Отметить оплату", "Төлөмдү белгилөө", "Mark payment", "Ödemeyi işaretle", "To'lovni belgilash"));
        _payAction.IsVisible = doc.Kind == ClientDocKind.Invoice;
        _payAction.Flyout = PayFlyout();
        var send = Btn(T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"));
        send.Flyout = SendFlyout();
        var print = Btn(T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish"));
        print.Flyout = PrintFlyout();
        var pdf = UiKit.Primary(this, "PDF");
        pdf.Height = 42;
        pdf.Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(pdf, T("Сохранить PDF", "PDF сактоо", "Save PDF", "PDF kaydet", "PDF saqlash"));
        pdf.Click += async (_, _) => await SavePdfAsync().ConfigureAwait(true);
        var more = Btn("⋯");
        Avalonia.Automation.AutomationProperties.SetName(more, T("Ещё: Word, скопировать, удалить", "Дагы: Word, көчүрүү, өчүрүү", "More: Word, copy, delete", "Diğer: Word, kopyala, sil", "Yana: Word, nusxalash, o'chirish"));
        more.Flyout = MoreFlyout();
        var close = Btn(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { more, _kindAction, _payAction, send, print, pdf, close })
        {
            b.Margin = new Thickness(8, 4, 0, 4);
            buttons.Children.Add(b);
        }
        var leftFoot = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _saveState, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _message, _openFile } } } };
        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(leftFoot);
        Grid.SetColumn(buttons, 1);
        foot.Children.Add(buttons);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(headBlock);
        Grid.SetRow(_main, 1);
        root.Children.Add(_main);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);
        Content = root;

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _ = RenderPreviewAsync();
        };

        FillFromDoc();
        _savedSnapshot = Snapshot();
        ApplyLayout(Width);
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        EscapeKey.Attach(this, closeInner: () =>
        {
            if (_suggest.IsVisible)
            {
                _suggest.IsVisible = false;
                return true;
            }
            if (_buyerList.IsVisible)
            {
                _buyerList.IsVisible = false;
                return true;
            }
            return false;
        }, closeWindow: Close);
        Opened += async (_, _) =>
        {
            this.FitToScreen();
            ApplyLayout(Bounds.Width);
            _ = RenderPreviewAsync();
            if (_doc.Lines.Count == 0 && string.IsNullOrWhiteSpace(_doc.Client))
                _buyerSearch.Focus();
            else
                AddBox.Focus();
            _buyers = await ClientDocsUi.BuyersAsync().ConfigureAwait(true);
        };
        Closing += (_, _) =>
        {
            _saveTimer.Stop();
            _previewTimer.Stop();
            if (_dirty)
                SaveNow();
            if (_profileDirty)
                SaveProfileNow();
            _ = ClientDocsStore.FlushAsync();
        };
        ClientDocsStore.Changed += OnStoreChanged;
        Closed += (_, _) => ClientDocsStore.Changed -= OnStoreChanged;
    }

    // ───────────────────────────── заполнение ─────────────────────────────

    private void FillFromDoc()
    {
        _lastTotal = null;
        _filling = true;
        try
        {
            Title = ClientDocsUi.KindLong(_doc.Kind) + " " + _doc.Number;
            _number.Text = _doc.Number;
            var idx = ClientDocsUi.Statuses(_doc.Kind).Select(s => s.Code).ToList().IndexOf(_doc.Status);
            _status.SelectedIndex = idx < 0 ? 0 : idx;
            _client.Text = _doc.Client;
            _inn.Text = _doc.Inn;
            _phone.Text = _doc.Phone;
            _address.Text = _doc.Address ?? "";
            _date.SelectedDate = _doc.Date.Date;
            _until.SelectedDate = _doc.Until.Date;
            _basedOn.Text = _doc.BasedOn;
            _lang.SelectedIndex = _doc.Lang == "ky" ? 1 : 0;
            _vatOn.IsChecked = _doc.VatPercent is > 0;
            if (_doc.VatPercent is > 0 and var v)
                _vat.Text = v.ToString("0.##", CultureInfo.InvariantCulture);
            _docDiscount.Text = _doc.Discount > 0.004 ? _doc.Discount.ToString("0.##", CultureInfo.InvariantCulture) : "";
            _note.Text = _doc.Note ?? "";
            _rememberTerms.IsChecked = _profile.Terms.TryGetValue(ClientDocProfile.KindKey(_doc.Kind), out var terms) && !string.IsNullOrWhiteSpace(terms) && terms == (_doc.Note ?? "");
            foreach (var (key, box) in _seller)
                box.Text = SellerValue(key) ?? "";
            _stamp.IsChecked = _profile.ShowStamp;
            for (var i = 0; i < _tplButtons.Length; i++)
                UiKit.SetActive(_tplButtons[i], ClientDocRenderer.Templates[i] == _doc.Template);
        }
        finally
        {
            _filling = false;
        }
        RebuildRows();
        UpdateTotals();
        UpdateSellerSummary();
        UpdateSaveState();
    }

    private string? SellerValue(string key) => key switch
    {
        "name" => _profile.Name, "inn" => _profile.Inn, "address" => _profile.Address, "phone" => _profile.Phone, "bank" => _profile.Bank,
        "bik" => _profile.Bik, "account" => _profile.Account, "director" => _profile.Director, "accountant" => _profile.Accountant, _ => null,
    };

    private void SellerChanged(string key, string v)
    {
        var value = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        if (value == (string.IsNullOrWhiteSpace(SellerValue(key)) ? null : SellerValue(key)!.Trim()))
            return;
        switch (key)
        {
            case "name": _profile.Name = value; break;
            case "inn": _profile.Inn = value; break;
            case "address": _profile.Address = value; break;
            case "phone": _profile.Phone = value; break;
            case "bank": _profile.Bank = value; break;
            case "bik": _profile.Bik = value; break;
            case "account": _profile.Account = value; break;
            case "director": _profile.Director = value; break;
            case "accountant": _profile.Accountant = value; break;
        }
        UpdateSellerSummary();
        ProfileChanged();
    }

    private void UpdateSellerSummary()
    {
        var parts = new[] { _profile.Name, string.IsNullOrWhiteSpace(_profile.Inn) ? null : "ИНН " + _profile.Inn, _profile.Address, _profile.Bank }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        _sellerSummary.Text = parts.Count == 0
            ? T("Реквизиты не заполнены — нажмите «Изменить реквизиты».", "Реквизиттер толтурулган жок — «Реквизиттерди өзгөртүү» басыңыз.", "Details are empty — press “Edit details”.", "Bilgiler boş — «Bilgileri düzenle»ye basın.", "Rekvizitlar to'ldirilmagan — «Rekvizitlarni o'zgartirish»ni bosing.")
            : string.Join(" · ", parts);
        _logoText.Text = string.IsNullOrWhiteSpace(_profile.LogoPath)
            ? T("Логотип не выбран", "Логотип тандалган жок", "No logo", "Logo seçilmedi", "Logotip tanlanmagan")
            : Path.GetFileName(_profile.LogoPath);
    }

    private void OnStoreChanged()
    {
        UpdateSaveState();
        // 2026-10-21 (ревью документов, A3): документ изменён на другом устройстве (или ему дан новый номер), а здесь правок нет —
        // показать новую версию, чтобы следующее сохранение этого окна не затёрло её старой.
        if (_isNew || _dirty || _filling)
            return;
        var fresh = ClientDocsStore.Get(_doc.Id);
        if (fresh == null)
        {
            if (!_goneShown)
                ShowMessage(T("Документ удалён на другом устройстве. Любая правка здесь сохранит его снова.", "Документ башка түзмөктө өчүрүлгөн. Бул жерде өзгөртсөңүз, ал кайра сакталат.",
                    "The document was deleted on another device. Any edit here will save it again.", "Belge başka bir cihazda silindi. Burada yapılan bir düzenleme onu yeniden kaydeder.",
                    "Hujjat boshqa qurilmada o'chirilgan. Bu yerda o'zgartirsangiz, u qayta saqlanadi."));
            _goneShown = true;
            return;
        }
        if (ClientDocsStore.CompareVersion(fresh, _doc) <= 0)
            return;
        _doc.CopyFrom(fresh);
        FillFromDoc();
        _savedSnapshot = Snapshot();
        _ = RenderPreviewAsync();
        var notice = ClientDocsStore.Notice;
        ShowMessage(notice != null && notice.Contains(_doc.Number, StringComparison.OrdinalIgnoreCase) ? notice : T("Документ изменён на другом устройстве — показана новая версия.", "Документ башка түзмөктө өзгөртүлгөн — жаңы версия көрсөтүлдү.",
            "The document was changed on another device — showing the new version.", "Belge başka bir cihazda değiştirildi — yeni sürüm gösteriliyor.",
            "Hujjat boshqa qurilmada o'zgartirilgan — yangi versiya ko'rsatildi."));
    }

    private void UpdateSaveState()
    {
        var saved = _isNew
            ? T("Новый документ — сохранится сам, как только появится клиент или товар.", "Жаңы документ — кардар же товар кошулганда өзү сакталат.", "New document — it saves itself once a client or product is added.",
                "Yeni belge — müşteri veya ürün eklenince kendiliğinden kaydedilir.", "Yangi hujjat — mijoz yoki tovar qo'shilganda o'zi saqlanadi.")
            : T("Черновик сохраняется сам", "Долбоор өзү сакталат", "The draft saves itself", "Taslak kendiliğinden kaydedilir", "Qoralama o'zi saqlanadi");
        var where = ClientDocsStore.State switch
        {
            ClientDocsStore.SyncState.Saved => T(" · в NurCRM", " · NurCRMде", " · in NurCRM", " · NurCRM'de", " · NurCRM'da"),
            ClientDocsStore.SyncState.Saving => T(" · отправляю в NurCRM…", " · NurCRMге жөнөтүлүүдө…", " · sending to NurCRM…", " · NurCRM'e gönderiliyor…", " · NurCRM'ga yuborilmoqda…"),
            ClientDocsStore.SyncState.LocalOnly => T(" · на этом ПК (нет связи)", " · ушул ПКда (байланыш жок)", " · on this PC (offline)", " · bu bilgisayarda (bağlantı yok)", " · shu kompyuterda (aloqa yo'q)"),
            ClientDocsStore.SyncState.Forbidden => T(" · только на этом ПК (нет прав)", " · ушул ПКда гана (укук жок)", " · this PC only (no rights)", " · yalnızca bu bilgisayarda (yetki yok)", " · faqat shu kompyuterda (huquq yo'q)"),
            _ => "",
        };
        _saveState.Text = saved + (_isNew ? "" : where);
    }

    // ───────────────────────────── изменения ─────────────────────────────

    private void Changed()
    {
        if (_filling)
            return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void ProfileChanged()
    {
        if (_filling)
            return;
        _profileDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RememberTerms()
    {
        var key = ClientDocProfile.KindKey(_doc.Kind);
        if (string.IsNullOrWhiteSpace(_doc.Note))
            _profile.Terms.Remove(key);
        else
            _profile.Terms[key] = _doc.Note!;
        ProfileChanged();
    }

    /// <summary>Пустой новый документ не сохраняем (номер не «съедается»); дальше — сам после каждого изменения.</summary>
    private bool WorthSaving => !_isNew || _doc.Lines.Count > 0 || !string.IsNullOrWhiteSpace(_doc.Client) || !string.IsNullOrWhiteSpace(_doc.Note);

    private string Snapshot()
    {
        var c = _doc.Clone();
        c.UpdatedRaw = null;
        c.UpdatedAtRaw = null;
        return System.Text.Json.JsonSerializer.Serialize(c, ClientDocsStore.Json);
    }

    private void SaveNow()
    {
        if (_profileDirty)
            SaveProfileNow();
        if (!_dirty || !WorthSaving)
            return;
        if (!_isNew && Snapshot() == _savedSnapshot)
        {
            _dirty = false;
            return;
        }
        if (_isNew && ClientDocsStore.NumberTaken(_doc.Number, _doc.Kind, _doc.Id))
        {
            // Пока документ был открыт, такой номер занял другой (телефон, другое окно) — следующий свободный.
            _doc.Number = ClientDocsStore.NextNumber(_doc.Kind);
            _filling = true;
            _number.Text = _doc.Number;
            _filling = false;
        }
        _dirty = false;
        ClientDocsStore.Save(_doc);
        // 2026-10-21 (ревью документов, A5): номер мог замениться (занят другим устройством).
        if ((_number.Text ?? "").Trim() != _doc.Number)
        {
            _filling = true;
            _number.Text = _doc.Number;
            _filling = false;
        }
        _savedSnapshot = Snapshot();
        _isNew = false;
        Title = ClientDocsUi.KindLong(_doc.Kind) + " " + _doc.Number;
        UpdateSaveState();
    }

    private void SaveProfileNow()
    {
        _profileDirty = false;
        // 2026-10-21 (ревью документов, A5): только изменённые в окне поля.
        ClientDocsStore.SaveProfile(_profile, _profileBase);
        _profileBase = _profile.Clone();
    }

    private void NumberChanged()
    {
        if (_filling)
            return;
        var n = (_number.Text ?? "").Trim();
        if (n == _doc.Number)
        {
            _numberWarn.IsVisible = false;
            return;
        }
        if (n.Length == 0)
        {
            _numberWarn.Text = T("Номер не может быть пустым.", "Номер бош болбошу керек.", "The number cannot be empty.", "Numara boş olamaz.", "Raqam bo'sh bo'lmasligi kerak.");
            _numberWarn.IsVisible = true;
            return;
        }
        if (ClientDocsStore.NumberTaken(n, _doc.Kind, _doc.Id))
        {
            _numberWarn.Text = T($"Номер {n} уже есть у другого документа — оставлен {_doc.Number}.", $"{n} номери башка документте бар — {_doc.Number} калды.",
                $"Number {n} is already used — {_doc.Number} is kept.", $"{n} numarası zaten kullanılıyor — {_doc.Number} korundu.", $"{n} raqami boshqa hujjatda bor — {_doc.Number} qoldi.");
            _numberWarn.IsVisible = true;
            return;
        }
        _numberWarn.IsVisible = false;
        _doc.Number = n;
        Changed();
    }

    /// <summary>2026-10-24 (приёмка, ПР-7): строка документа считается только с названием и суммой больше 0 —
    /// пустая строка (без названия, сумма 0) не делает документ «непустым».</summary>
    private static bool IsRealLine(ClientDocLine l) => !string.IsNullOrWhiteSpace(l.Name) && l.Sum > 0;

    private bool HasRealLines => _doc.Lines.Any(IsRealLine);

    /// <summary>2026-10-24 (аудит, круг 8, С-17): текст отказа для пустого документа.</summary>
    private static string EmptyDocText() => T(
        "Добавьте хотя бы одну строку — пустой документ не превращается в счёт/накладную и не выставляется.",
        "Жок дегенде бир сап кошуңуз — бош документ эсепке/накладнойго айланбайт жана берилбейт.",
        "Add at least one line — an empty document cannot become an invoice/waybill or be issued.",
        "En az bir satır ekleyin — boş belge faturaya/irsaliyeye dönüşmez ve düzenlenmez.",
        "Hech bo'lmaganda bitta qator qo'shing — bo'sh hujjat hisob/yuk xatiga aylanmaydi va berilmaydi.");

    private void StatusChanged()
    {
        if (_filling)
            return;
        var list = ClientDocsUi.Statuses(_doc.Kind);
        if (_status.SelectedIndex < 0 || _status.SelectedIndex >= list.Count)
            return;
        var (code, text) = list[_status.SelectedIndex];
        if (code == _doc.Status)
            return;
        // 2026-10-24 (аудит, круг 8, С-17): пустой документ нельзя вручную выставить/отправить/принять — вернуть прежний статус.
        // 2026-10-24 (приёмка, ПР-7): пустая строка (без названия, сумма 0) — не строка; предупреждение без «Отложить ошибку» (Info).
        if (!HasRealLines && code is not ("draft" or "declined"))
        {
            var back = list.Select(s => s.Code).ToList().IndexOf(_doc.Status);
            _filling = true;
            _status.SelectedIndex = back < 0 ? 0 : back;
            _filling = false;
            PosDialogs.Info(this, EmptyDocText());
            return;
        }
        _doc.Status = code;
        if (code == "paid" && _doc.Paid < _doc.Total)
            _doc.Paid = _doc.Total;
        _doc.AddHistory(T("Статус: ", "Статус: ", "Status: ", "Durum: ", "Holat: ") + text);
        Changed();
    }

    private void SetStatus(string code, string history)
    {
        _doc.Status = code;
        _doc.AddHistory(history);
        _filling = true;
        var idx = ClientDocsUi.Statuses(_doc.Kind).Select(s => s.Code).ToList().IndexOf(code);
        _status.SelectedIndex = idx < 0 ? 0 : idx;
        _filling = false;
        Changed();
    }

    private void SetTemplate(string name)
    {
        _doc.Template = name;
        for (var i = 0; i < _tplButtons.Length; i++)
            UiKit.SetActive(_tplButtons[i], ClientDocRenderer.Templates[i] == name);
        // Шаблон запоминается по виду документа.
        _profile.Templates[ClientDocProfile.KindKey(_doc.Kind)] = name;
        _profileDirty = true;
        Changed();
        _previewTimer.Stop();
        _ = RenderPreviewAsync();
    }

    private void VatChanged()
    {
        if (_filling)
            return;
        double? vat = _vatOn.IsChecked == true && TryNum(_vat.Text, out var v) && v is > 0 and < 100 ? v : null;
        if (vat == _doc.VatPercent)
            return;
        _doc.VatPercent = vat;
        _profile.VatPercent = _doc.VatPercent;
        _profileDirty = true;
        UpdateTotals();
        Changed();
    }

    private void DiscountChanged()
    {
        if (_filling)
            return;
        UpdateTotals();
        Changed();
    }

    /// <summary>2026-10-21 (ревью документов, A6): скидка документа разбирается из поля при каждом пересчёте итогов — «10%» растёт
    /// при добавлении строк, «500» при подытоге 300 не остаётся навсегда 300.</summary>
    private void ApplyDocDiscount()
    {
        var text = (_docDiscount.Text ?? "").Trim();
        double disc = 0;
        if (text.EndsWith('%') && TryNum(text.TrimEnd('%'), out var pct))
            disc = ClientDoc.Round2(_doc.Subtotal * Math.Clamp(pct, 0, 100) / 100);
        else if (TryNum(text, out var abs))
            disc = Math.Max(0, abs);
        _doc.Discount = Math.Min(disc, _doc.Subtotal);
    }

    private void UpdateTotals()
    {
        ApplyDocDiscount();
        // 2026-10-21 (ревью документов, мелочи): сумма оплаченного счёта выросла — «Частично оплачен».
        if (_lastTotal is { } was && _doc.Total > was + 0.004 && _doc.Kind == ClientDocKind.Invoice && _doc.Status == "paid" && _doc.Paid > 0.004 && _doc.Left > 0.004)
        {
            _doc.Status = "partial";
            _doc.AddHistory(T("Сумма выросла — статус «Оплачен частично»", "Сумма өстү — статус «Жарым-жартылай төлөндү»", "The amount grew — status “Partly paid”",
                "Tutar arttı — durum «Kısmen ödendi»", "Summa oshdi — holat «Qisman to'landi»"));
            var wasFilling = _filling;
            _filling = true;
            var idx = ClientDocsUi.Statuses(_doc.Kind).Select(s => s.Code).ToList().IndexOf("partial");
            if (idx >= 0)
                _status.SelectedIndex = idx;
            _filling = wasFilling;
        }
        _lastTotal = _doc.Total;
        var som = ClientDocsUi.Som;
        _subtotal.Text = T("Подытог: ", "Аралык жыйынтык: ", "Subtotal: ", "Ara toplam: ", "Oraliq jami: ") + ClientDocsUi.Money(_doc.Subtotal) + " " + som;
        _discountText.IsVisible = _doc.Discount > 0.004;
        _discountText.Text = T("Скидка: −", "Арзандатуу: −", "Discount: −", "İndirim: −", "Chegirma: −") + ClientDocsUi.Money(Math.Min(_doc.Discount, _doc.Subtotal)) + " " + som;
        _vatText.Text = _doc.VatPercent is > 0 and var v
            ? T($"В т.ч. НДС {v:0.##}%: ", $"Анын ичинде КНС {v:0.##}%: ", $"Incl. VAT {v:0.##}%: ", $"Dahil KDV %{v:0.##}: ", $"Shu jumladan QQS {v:0.##}%: ") + ClientDocsUi.Money(_doc.VatAmount) + " " + som
            : T("Без НДС", "КНСсиз", "No VAT", "KDV'siz", "QQSsiz");
        _total.Text = T("Итого: ", "Жыйынтык: ", "Total: ", "Toplam: ", "Jami: ") + ClientDocsUi.Money(_doc.Total) + " " + som;
        _words.Text = MoneyWords.Amount(_doc.Total, _doc.Lang);
        _payAction.IsVisible = _doc.Kind == ClientDocKind.Invoice && _doc.Status != "paid";
    }

    // ───────────────────────────── покупатель ─────────────────────────────

    private void FillBuyerList()
    {
        var q = (_buyerSearch.Text ?? "").Trim();
        _buyerList.Items.Clear();
        if (q.Length == 0)
        {
            _buyerList.IsVisible = false;
            return;
        }
        var digits = new string(q.Where(char.IsDigit).ToArray());
        var found = _buyers.Where(b => SearchMatch.Matches(q, b.Name, b.Company) // 2026-10-29: единый поиск
                                       || (digits.Length >= 3 && (new string(b.Phone.Where(char.IsDigit).ToArray()).Contains(digits) || b.Inn.Contains(digits))))
            .Take(8).ToList();
        foreach (var b in found)
        {
            var line = string.Join(" · ", new[] { b.Company.Length > 0 ? $"{b.Company} ({b.Name})" : b.Name, b.Phone, b.Inn.Length > 0 ? "ИНН " + b.Inn : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
            // 2026-10-22 (модалки, пакет 2): у строки — кнопка «Выбрать» 44 px (отчёт «Сенсорный моноблок» п. 5.7: выбор был
            // только касанием/двойным касанием строки, кнопки не было).
            var lineText = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var pick = UiKit.Ghost(this, T("Выбрать", "Тандоо", "Choose", "Seç", "Tanlash"));
            pick.MinHeight = 44;
            pick.Height = 44;
            pick.Focusable = true;
            pick.Margin = new Thickness(8, 0, 0, 0);
            pick.VerticalAlignment = VerticalAlignment.Center;
            var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(pick, 1);
            rowGrid.Children.Add(lineText);
            rowGrid.Children.Add(pick);
            var buyerItem = new ListBoxItem { Content = rowGrid, Tag = b, MinHeight = 48, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            pick.Click += (_, _) => PickBuyer(buyerItem);
            _buyerList.Items.Add(buyerItem);
        }
        if (found.Count == 0)
            _buyerList.Items.Add(new ListBoxItem
            {
                Content = T("Не нашли — нажмите «+ Новый клиент» или впишите покупателя ниже", "Табылган жок — «+ Жаңы кардар» басыңыз же сатып алуучуну төмөндө жазыңыз",
                    "Not found — press “+ New client” or type the buyer below", "Bulunamadı — «+ Yeni müşteri»ye basın veya alıcıyı aşağıya yazın", "Topilmadi — «+ Yangi mijoz»ni bosing yoki xaridorni pastga yozing"),
                IsEnabled = false,
            });
        _buyerList.IsVisible = true;
        if (found.Count > 0)
            _buyerList.SelectedIndex = 0;
    }

    private void BuyerSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _buyerList.IsVisible && _buyerList.ItemCount > 0)
        {
            e.Handled = true;
            _buyerList.Focus();
            return;
        }
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        PickBuyer(_buyerList.SelectedItem as ListBoxItem);
    }

    private void PickBuyer(ListBoxItem? item)
    {
        if (item?.Tag is not ClientDocsUi.Buyer b)
            return;
        ClientDocsUi.ApplyBuyer(_doc, b);
        _filling = true;
        _client.Text = _doc.Client;
        _inn.Text = _doc.Inn;
        _phone.Text = _doc.Phone;
        _address.Text = _doc.Address ?? "";
        _buyerSearch.Text = "";
        _filling = false;
        _buyerList.IsVisible = false;
        Changed();
        AddBox.Focus();
    }

    private async Task NewBuyerAsync()
    {
        var b = await ClientDocsUi.CreateBuyerAsync(this).ConfigureAwait(true);
        if (b == null)
            return;
        _buyers = await ClientDocsUi.BuyersAsync().ConfigureAwait(true);
        PickBuyer(new ListBoxItem { Tag = b });
    }

    // ───────────────────────────── строки ─────────────────────────────

    private List<CatalogProductTileVm> Search(string query, int max = 8)
    {
        List<CatalogProductTileVm> all;
        try
        {
            all = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return new();
        }
        var q = query.Trim();
        if (q.Length == 0)
            return new();
        var exact = all.Where(p => string.Equals(p.Barcode, q, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, q, StringComparison.OrdinalIgnoreCase)
                                   || (p.Article is { Length: > 0 } a && string.Equals(a, q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0)
            return exact.Take(max).ToList();
        return SearchMatch.Products(all, q) // 2026-10-29: единый поиск (слова в любом порядке, ё=е, раскладка/транслит)
            .OrderByDescending(p => p.Title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(p => p.Title.Length)
            .Take(max).ToList();
    }

    private void FillSuggestions()
    {
        _suggest.Items.Clear();
        var q = (AddBox.Text ?? "").Trim();
        if (q.Length == 0)
        {
            _suggest.IsVisible = false;
            _addHint.IsVisible = false;
            return;
        }
        var found = Search(q);
        foreach (var p in found)
            _suggest.Items.Add(new ListBoxItem
            {
                // 2026-10-22 (модалки, пакет 2): строка подсказки товара 44 px — касание пальцем (было 38).
                Tag = p, MinHeight = 44,
                Content = $"{p.Title} · {ClientDocsUi.Money(p.PriceValue)} {ClientDocsUi.Som}" + (string.IsNullOrWhiteSpace(p.Barcode) ? "" : " · " + p.Barcode),
            });
        _suggest.IsVisible = found.Count > 0;
        if (found.Count > 0)
            _suggest.SelectedIndex = 0;
        _addHint.IsVisible = found.Count == 0;
        _addHint.Text = T("Такого товара нет в каталоге. Для услуги или доставки нажмите «+ Свободная строка».", "Мындай товар каталогдо жок. Кызмат же жеткирүү үчүн «+ Эркин сап» басыңыз.",
            "No such product in the catalog. For a service or delivery press “+ Free line”.", "Katalogda böyle bir ürün yok. Hizmet veya teslimat için «+ Serbest satır»a basın.",
            "Katalogda bunday tovar yo'q. Xizmat yoki yetkazib berish uchun «+ Erkin qator»ni bosing.");
    }

    /// <summary>Enter в поле товара — ОДНА строка (см. <see cref="DocLineAdder"/>). Стрелки — по подсказкам. Esc — скрыть подсказки.</summary>
    private void AddBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && _suggest.IsVisible && _suggest.ItemCount > 0)
        {
            e.Handled = true;
            var i = _suggest.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
            _suggest.SelectedIndex = Math.Clamp(i, 0, _suggest.ItemCount - 1);
            return;
        }
        if (e.Key != Key.Enter)
            return;
        // Enter не уходит дальше (кнопки окна, строки таблицы) — ничего, кроме добавления.
        e.Handled = true;
        AddFromBox();
    }

    /// <summary>Добавить товар из поля: выбранная подсказка, иначе точный штрихкод, иначе единственный найденный.</summary>
    public DocLineAdder.Result AddFromBox()
    {
        var text = (AddBox.Text ?? "").Trim();
        if (text.Length == 0)
            return Adder.Bounce();
        var product = _suggest.IsVisible ? (_suggest.SelectedItem as ListBoxItem)?.Tag as CatalogProductTileVm : null;
        if (product == null)
        {
            var found = Search(text, 2);
            product = found.Count == 1 ? found[0] : null;
        }
        if (product == null)
        {
            FillSuggestions();
            return DocLineAdder.Result.NotFound;
        }
        return AddProduct(product);
    }

    private void AddSuggestion(ListBoxItem? item)
    {
        if (item?.Tag is CatalogProductTileVm p)
            AddProduct(p);
    }

    public DocLineAdder.Result AddProduct(CatalogProductTileVm product)
    {
        var result = Adder.Add(_doc, product.Id, product.Title, string.IsNullOrWhiteSpace(product.Unit) ? "шт" : product.Unit, product.PriceValue);
        AddBox.Text = "";
        _suggest.IsVisible = false;
        _addHint.IsVisible = false;
        if (result is DocLineAdder.Result.Added or DocLineAdder.Result.Increased)
        {
            RebuildRows();
            UpdateTotals();
            Changed();
        }
        AddBox.Focus();
        return result;
    }

    private void AddFreeLine()
    {
        var line = new ClientDocLine { Name = "", Unit = T("усл.", "кызм.", "svc", "hzm.", "xizm."), Qty = 1, Free = true };
        _doc.Lines.Add(line);
        RebuildRows();
        UpdateTotals();
        Changed();
        Dispatcher.UIThread.Post(() => (RowsPanel.Children.LastOrDefault() as Avalonia.LogicalTree.ILogical)?.GetLogicalDescendants().OfType<TextBox>().Skip(0).FirstOrDefault()?.Focus(), DispatcherPriority.Background);
    }
}

/// <summary>2026-10-21, владелец (жалоба на старую программу: Enter добавлял строку дважды или удалял её): добавление товара в документ.
/// Один товар — одна строка: повтор того же товара добавляет +1 к количеству этой строки. Повтор того же товара быстрее
/// <see cref="BounceMs"/> (двойной Enter, дребезг клавиши, сканер, шлющий Enter дважды) — игнорируется. Пустое поле — ничего.</summary>
public sealed class DocLineAdder
{
    public enum Result { Added, Increased, Ignored, NotFound }

    public const int BounceMs = 400;
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;
    private string? _lastId;
    private long _lastAt = long.MinValue / 2;

    /// <summary>Пустой Enter: ничего не добавляет и не удаляет.</summary>
    public Result Bounce() => Result.Ignored;

    public Result Add(ClientDoc doc, string? productId, string name, string unit, double price)
    {
        var now = Clock();
        var id = string.IsNullOrWhiteSpace(productId) ? "name:" + name.Trim().ToLowerInvariant() : productId!;
        if (id == _lastId && now - _lastAt < BounceMs)
            return Result.Ignored;
        _lastId = id;
        _lastAt = now;
        var same = doc.Lines.FirstOrDefault(l => l.Free != true && !string.IsNullOrWhiteSpace(productId) && string.Equals(l.ProductId, productId, StringComparison.OrdinalIgnoreCase));
        if (same != null)
        {
            same.Qty = Math.Round(same.Qty + 1, 3);
            return Result.Increased;
        }
        var line = new ClientDocLine { ProductId = productId ?? "", Name = name.Trim(), Unit = unit, Qty = 1 };
        line.SetPrice(price, 0);
        doc.Lines.Add(line);
        return Result.Added;
    }
}
