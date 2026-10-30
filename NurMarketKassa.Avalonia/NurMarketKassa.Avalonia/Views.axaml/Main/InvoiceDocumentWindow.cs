using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30, форма «Накладная прихода» по макету design/Invoice-Doc (docs/Дизайн/Накладная прихода — разбор примера и макет): просмотр проведённой накладной
/// и правка количества («Б-лайт», только владелец и только в окне 24/48 ч/всегда, см. ReceiptEditService). Заменяет окно «Позиции прихода» (PurchaseReceiptDialog):
/// открывается из «Закупок» и из карточки товара («Накладная №…»), те же параметры и те же результаты <see cref="Action"/> («return» / «repeat»).
/// Шапка «Приход ПР-… от дата», статус, реквизиты; таблица на 500+ строк (виртуальный список, закреплённые шапка и итог); семь плиток итога; цвета строк:
/// ниже закупки — красная полоса, маржа &lt; 10 % — жёлтая, количество 0 — серая строка. Закупка/наценка — только ролям с правом на закупочные цены.
/// Создание накладной остаётся в «Принять товар» (там таблица приёмки); здесь — просмотр, печать, PDF, CSV и правка.</summary>
public sealed partial class InvoiceDocumentWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>return — открыть возврат по приходу, repeat — новый приём с теми же товарами.</summary>
    public string? Action { get; private set; }

    /// <summary>Только для стенда снимков: размер «экрана» (1296×879 или 1024×600), чтобы проверить оба вида на любом мониторе.</summary>
    internal static (double W, double H)? AreaOverride { get; set; }

    private readonly SupplierReceipt _r;
    private readonly PurchaseReceiptNote? _note;
    private readonly bool _debtRecorded;
    private readonly SupplierInfo? _supplier;
    private readonly PurchaseReceiptContext? _context;
    private readonly string _docNumber;
    private readonly ReceiptPayKind _pay;
    private readonly bool _showMoney;
    private readonly bool _compact;
    private readonly bool _showReturnRepeat;
    private readonly List<InvoiceRow> _rows = new();
    private List<InvoiceRow> _shown = new();
    private readonly ReceiptEditStore _store = ReceiptEditStore.Default;
    private readonly string _baseFingerprint;
    private readonly int _baseVersion;

    private readonly ListBox _list = new() { Focusable = true, SelectionMode = SelectionMode.Single };
    private readonly TextBox _search = new() { Watermark = "", MinHeight = 38 };
    private readonly TextBox _barcode = new() { Watermark = "", MinHeight = 38, Width = 160 };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _counter = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly KpiTileGrid _tiles = new() { Spacing = 8 };
    private readonly TextBlock _message = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Border _banner = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), IsVisible = false };
    private readonly TextBlock _bannerText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _windowLabel = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusHost = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _titleText = new() { FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };

    private string _chip = "all";
    private bool _edit;
    private ReceiptEditAccess _access;
    private DispatcherTimer? _clock;

    public InvoiceDocumentWindow(SupplierReceipt r, PurchaseReceiptNote? note, bool debtRecorded, SupplierInfo? supplier, PurchaseReceiptContext? context = null, bool showReturnRepeat = false)
    {
        _showReturnRepeat = showReturnRepeat;
        _r = r;
        _note = note;
        _debtRecorded = debtRecorded;
        _supplier = supplier;
        _context = context;
        _docNumber = DocumentNumbering.ReceiptText(r.Id);
        _pay = ReceiptEditPlanner.ParsePayKind(note?.PayKind) is var pk && pk != ReceiptPayKind.Unknown ? pk : debtRecorded ? ReceiptPayKind.Debt : ReceiptPayKind.Unknown;
        _showMoney = WarehouseWindow.RevisionShowPurchase;
        _baseFingerprint = InvoiceMath.Fingerprint(r);
        _baseVersion = _store.Version(r.Id);

        // Окно: 1296×879 — карточка 1240×820; 1024×600 и меньше — на всю рабочую область (колонки скрываются по приоритету).
        var area = Screens?.Primary?.WorkingArea;
        var scale = Screens?.Primary?.Scaling ?? 1.0;
        var availW = AreaOverride?.W ?? (area is { } a ? a.Width / scale : 1296);
        var availH = AreaOverride?.H ?? (area is { } a2 ? a2.Height / scale : 879);
        _compact = availW < 1250 || availH < 760;
        Title = T($"Приход {_docNumber}", $"Кирүү {_docNumber}", $"Delivery {_docNumber}", $"Giriş {_docNumber}", $"Kirim {_docNumber}");
        SectionCards.AsModal(this, _compact ? Math.Max(640, availW) : 1272);
        SizeToContent = SizeToContent.Manual;
        Height = _compact ? Math.Max(480, availH) : Math.Min(864, availH);
        MinWidth = 640;
        _footer.MaxWidth = Math.Max(500, Width - 56);
        MinHeight = 480;

        BuildRows();
        Content = BuildShell();
        ApplyAccess();
        Refresh();

        KeyDown += OnWindowKeyDown;
        Opened += (_, _) =>
        {
            _ = LoadPreviousPurchasesAsync();
            _ = CheckOtherPcAsync();
            // из карточки товара / двойного щелчка по позиции реестра — строка товара выделена и видна
            if (_context?.HighlightProductId is { Length: > 0 } hp && _rows.FirstOrDefault(x => string.Equals(x.ProductId, hp, StringComparison.OrdinalIgnoreCase)) is { } hit)
            {
                _list.SelectedItem = hit;
                _list.ScrollIntoView(hit);
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => _clock?.Stop();
    }

    // ------------------------------------------------------------------ данные

    private void BuildRows()
    {
        var history = _store.Get(_r.Id);
        var markup = _showMoney ? PurchaseMarkupTable.Rows(_r) : null;
        var inputs = ReceiptEditService.BuildInputs(_r, history, new Dictionary<string, double>());
        var byId = new Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in CatalogCacheService.Products)
                byId[t.Id] = t;
        }
        catch (InvalidOperationException)
        {
            // каталог обновлялся в этот момент — штрихкоды и остатки подтянутся при проверке правки
        }
        for (var i = 0; i < _r.Lines.Count; i++)
        {
            var l = _r.Lines[i];
            var input = inputs[i];
            var effective = ReceiptEditPlanner.Effective(input);
            double stock = double.NaN;
            string barcode = "";
            if (byId.TryGetValue(l.ProductId, out var tile))
            {
                barcode = tile.Barcode ?? "";
                stock = tile.Quantity;
            }
            var view = markup?[i].View;
            var row = new InvoiceRow(effective, view?.Sale ?? 0)
            {
                No = i + 1, LineId = l.Id, ProductId = l.ProductId, Code = l.Code, Barcode = barcode, Name = l.ProductName, Unit = l.Unit.Length > 0 ? l.Unit : "шт",
                ReturnedQty = l.ReturnedQty, Purchase = _showMoney ? l.PurchasePrice : 0, SaleEstimated = view?.Estimated ?? false,
                IsExtra = history.Addenda.Any(a => string.Equals(a.ProductId, l.ProductId, StringComparison.OrdinalIgnoreCase) && a.Remaining > 0.0005),
                Input = input,
            };
            if (!double.IsNaN(stock))
                row.Stock = stock;
            row.QtyEdited += OnQtyEdited;
            row.PriceEdited += _ => Recalc();
            _rows.Add(row);
        }
    }

    /// <summary>Закупка предыдущего прихода по товару — для маркера «↑ +12 %» и бейджа «новый». Читается в фоне из истории закупок кассы (локальная база).</summary>
    private async Task LoadPreviousPurchasesAsync()
    {
        if (!_showMoney || _rows.Count == 0)
            return;
        var at = _r.CreatedAt ?? DateTime.Now;
        var found = await Task.Run(() =>
        {
            var result = new Dictionary<string, (double? Prev, bool Known)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var id in _rows.Select(x => x.ProductId).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var earlier = PurchaseReceivingService.Instance.LoadLocalHistory(id).Where(h => h.At < at.AddMinutes(-1) && !string.Equals(h.ReceiptId, _r.Id, StringComparison.OrdinalIgnoreCase)).OrderByDescending(h => h.At).ToList();
                    result[id] = (earlier.Count > 0 ? earlier[0].PurchasePrice : null, true);
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Накладная: прошлые закупки не прочитаны ({ex.Message}).", "WARNING");
            }
            return result;
        }).ConfigureAwait(true);
        foreach (var row in _rows)
            if (found.TryGetValue(row.ProductId, out var f) && f.Known)
            {
                row.Prev = f.Prev;
                row.IsNew = f.Prev is null;
            }
        Refresh();
    }

    // ------------------------------------------------------------------ оболочка

    private Control BuildShell()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto") };
        _search.Watermark = T("Поиск по накладной", "Накладнойдон издөө", "Search the invoice", "Faturada ara", "Yuk xatidan qidirish") + (_compact ? "" : "  Ctrl+F");
        _barcode.Watermark = T("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod") + "  F2";
        _search.TextChanged += (_, _) => ApplyFilter();
        _barcode.KeyDown += OnBarcodeKeyDown;
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark);
        Avalonia.Automation.AutomationProperties.SetName(_barcode, _barcode.Watermark);
        _banner.Child = _bannerText;
        _banner.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushWarningSoft"));
        _bannerText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        _message.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _counter.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        _titleText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        _titleText.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_titleText);
        _statusHost.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_statusHost, 1);
        head.Children.Add(_statusHost);
        Grid.SetColumn(_windowLabel, 2);
        _windowLabel.Margin = new Thickness(14, 0, 0, 0);
        head.Children.Add(_windowLabel);
        root.Children.Add(head);

        _strip.Child = BuildRequisites();
        Grid.SetRow(_strip, 1);
        root.Children.Add(_strip);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(_banner);
        stack.Children.Add(BuildToolbar());
        Grid.SetRow(stack, 2);
        root.Children.Add(stack);

        var table = BuildTable();
        Grid.SetRow(table, 4);
        root.Children.Add(table);

        _tiles.MinTileWidth = 150;
        _tiles.Margin = new Thickness(0, 8, 0, 0);
        _tileLine.Margin = new Thickness(0, 6, 0, 0);
        _tileLine.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var totalsHost = new Panel();
        totalsHost.Children.Add(_compact ? _tileLine : _tiles);
        Grid.SetRow(totalsHost, 5);
        root.Children.Add(totalsHost);

        Grid.SetRow(_message, 6);
        root.Children.Add(_message);

        var shell = SectionCards.Shell(Title ?? "", null, root, _footer);
        shell.ContentScroll = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        shell.ScreenHeightShare = _compact ? 1.0 : 0.97;
        shell.Margin = new Thickness(_compact ? 2 : 16);
        shell.ShowClose = true;
        UpdateTitle(shell);
        _shell = shell;
        return shell;
    }

    private ModalShell? _shell;
    private readonly Border _strip = new();
    private readonly TextBlock _tileLine = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };

    private void UpdateTitle(ModalShell? shell = null)
    {
        shell ??= _shell;
        var date = _r.CreatedAt?.ToString("dd.MM.yyyy", Ru) ?? "—";
        var text = T($"Приход {_docNumber} от {date}", $"Кирүү {_docNumber}, {date}", $"Delivery {_docNumber} of {date}", $"Giriş {_docNumber}, {date}", $"Kirim {_docNumber}, {date}");
        if (_edit)
            text += T($" · Правка: версия {_baseVersion} → {_baseVersion + 1}", $" · Оңдоо: версия {_baseVersion} → {_baseVersion + 1}", $" · Edit: version {_baseVersion} → {_baseVersion + 1}",
                $" · Düzenleme: sürüm {_baseVersion} → {_baseVersion + 1}", $" · Tahrir: versiya {_baseVersion} → {_baseVersion + 1}");
        if (shell is not null)
            shell.Title = text;
        _titleText.Text = text;
        _titleText.IsVisible = false; // заголовок показывает оболочка
    }

    private Control BuildRequisites()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, _compact ? 2 : 6) };
        void Item(string label, string value, string? tip = null, bool grey = false)
        {
            var box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(_compact ? 8 : 10, _compact ? 2 : 5), Margin = new Thickness(0, 0, 6, _compact ? 3 : 6), Opacity = grey ? 0.6 : 1 };
            box.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var a = new TextBlock { Text = label, FontSize = 12.5 };
            a.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            var b = new TextBlock { Text = value, FontSize = 13.5, FontWeight = FontWeight.SemiBold };
            b.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            sp.Children.Add(a);
            sp.Children.Add(b);
            box.Child = sp;
            if (tip is not null)
                ToolTip.SetTip(box, tip);
            wrap.Children.Add(box);
        }
        var soon = T("Станет доступно после обновления сервера", "Сервер жаңырганда жеткиликтүү болот", "Available after the server update", "Sunucu güncellemesinden sonra kullanılabilir olacak", "Server yangilangach mavjud bo'ladi");
        Item(T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), _r.SupplierName.Length > 0 ? _r.SupplierName : "—", _edit ? soon : null, grey: _edit);
        if (PurchaseNumberUi.SupplierDocText(_r.SupplierDocNumber, _r.SupplierDocDate).Length > 0)
            Item(T("№ накладной поставщика", "Жеткирүүчүнүн накладной №", "Supplier invoice no.", "Tedarikçi irsaliye no.", "Yetkazuvchi yuk xati №"),
                _r.SupplierDocNumber + (_r.SupplierDocDate is { } sd ? " · " + sd.ToString("dd.MM.yyyy", Ru) : ""));
        Item(T("Принял", "Кабыл алды", "Received by", "Kabul eden", "Qabul qildi"), _r.CreatedBy.Length > 0 ? _r.CreatedBy : "—");
        Item(T("Дата", "Күнү", "Date", "Tarih", "Sana"), _r.CreatedAt?.ToString("dd.MM.yyyy HH:mm", Ru) ?? "—");
        if (_showMoney)
            Item(T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), PayText(), _edit ? soon : null, grey: _edit);
        if (_r.ReturnedAmount > 0.005)
            Item(T("Возвращено", "Кайтарылды", "Returned", "İade edilen", "Qaytarildi"), MoneyText.Som(_r.ReturnedAmount));
        return wrap;
    }

    private string PayText()
    {
        var pay = _note?.PayKind switch
        {
            "cash" => T("оплачено из основной кассы", "негизги кассадан төлөндү", "paid from the main till", "ana kasadan ödendi", "asosiy kassadan to'landi"),
            "drawer" => T("оплачено из ящика смены", "сменанын кассасынан төлөндү", "paid from the shift drawer", "vardiya kasasından ödendi", "smena kassasidan to'landi"),
            "partial" => T($"предоплата {MoneyText.Som(_note.Paid)}, долг {MoneyText.Som(_note.Debt)}", $"алдын ала {MoneyText.Som(_note.Paid)}, карыз {MoneyText.Som(_note.Debt)}",
                $"prepaid {MoneyText.Som(_note.Paid)}, debt {MoneyText.Som(_note.Debt)}", $"ön ödeme {MoneyText.Som(_note.Paid)}, borç {MoneyText.Som(_note.Debt)}", $"oldindan {MoneyText.Som(_note.Paid)}, qarz {MoneyText.Som(_note.Debt)}"),
            "debt" => T("в долг поставщику", "жеткирүүчүгө карызга", "on credit", "veresiye", "qarzga"),
            _ => _debtRecorded ? T("в долг поставщику", "жеткирүүчүгө карызга", "on credit", "veresiye", "qarzga")
                : T("неизвестно (сервер не хранит)", "белгисиз (сервер сактабайт)", "unknown (the server doesn't store it)", "bilinmiyor (sunucu saklamaz)", "noma'lum (server saqlamaydi)"),
        };
        if (_note?.DueDate is { } due && _note.Debt > 0.005)
            pay += T($", до {due:dd.MM.yyyy}", $", {due:dd.MM.yyyy} чейин", $", by {due:dd.MM.yyyy}", $", {due:dd.MM.yyyy} tarihine kadar", $", {due:dd.MM.yyyy} gacha");
        return pay;
    }

    private Control BuildOptions()
    {
        // Флажки макета относятся к созданию накладной (таблица приёмки): здесь они показаны, но выключены, с подсказкой — где включить.
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var hint = T("Работает при создании накладной: «Закупки → Принять товар»", "Накладнойду түзүүдө иштейт: «Сатып алуулар → Товар кабыл алуу»", "Works when creating an invoice: Purchases → Receive goods",
            "Fatura oluştururken çalışır: Satın alma → Mal kabul", "Yuk xatini yaratishda ishlaydi: Xaridlar → Tovar qabul qilish");
        foreach (var text in new[]
                 {
                     T("Добавить ценник для всех покупаемых товаров", "Сатып алынган бардык товарга баа жазуусун кошуу", "Add a price tag for all purchased items", "Alınan tüm ürünlere etiket ekle", "Barcha xarid qilingan mahsulotlarga narx yorlig'i qo'shish"),
                     T("Предварительное сканирование", "Алдын ала сканерлөө", "Pre-scan mode", "Ön tarama", "Oldindan skanerlash"),
                 })
        {
            var cb = new CheckBox { Content = text, IsEnabled = false, Margin = new Thickness(0, 0, 14, 0), FontSize = 12.5 };
            ToolTip.SetTip(cb, hint);
            row.Children.Add(cb);
        }
        return row;
    }

    private Control BuildToolbar()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };
        Control Gap(Control c) { c.Margin = new Thickness(0, 0, 8, 6); return c; }
        var insert = SectionCards.Secondary(T("Вставить товар", "Товар кошуу", "Insert item", "Ürün ekle", "Mahsulot qo'shish") + "  Ins");
        insert.MinHeight = 38;
        insert.Height = 38;
        insert.Padding = new Thickness(12, 0);
        insert.FontSize = 13;
        insert.Click += (_, _) => FocusSearchForInsert();
        bar.Children.Add(Gap(insert));
        bar.Children.Add(Gap(_barcode));
        _search.Width = _compact ? 190 : 260;
        _search.Height = 38;
        _barcode.Height = 38;
        bar.Children.Add(Gap(_search));
        bar.Children.Add(Gap(_chips));
        _counter.Margin = new Thickness(6, 0, 12, 6);
        bar.Children.Add(_counter);
        if (!_compact)
            bar.Children.Add(BuildOptions());
        return bar;
    }

    // ------------------------------------------------------------------ доступ и статус

    private bool InQueue()
    {
        try
        {
            return BackgroundReceivingQueue.Instance.Snapshot().Any(j => string.Equals(j.ReceiptId, _r.Id, StringComparison.OrdinalIgnoreCase) && ReceivingJobStatus.IsActive(j.Status));
        }
        catch
        {
            return false;
        }
    }

    private bool IsOwner() => App.AuthApi is { } auth && PermissionService.CanEditPurchaseDocuments(auth.UserPayload);

    private void ApplyAccess()
    {
        _access = ReceiptEditPolicy.Evaluate(IsOwner(), ReceiptEditPolicy.Current, _r.CreatedAt, ReceiptEditPolicy.ServerNow, InQueue(), ReceiptEditService.IsFullyReturned(_r));
        UpdateStatus();
        UpdateWindowLabel();
        BuildFooter();
        UpdateBanner();
        _clock ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _clock.Tick -= OnClock;
        _clock.Tick += OnClock;
        _clock.Start();
    }

    private void OnClock(object? sender, EventArgs e)
    {
        var was = _access.Allowed;
        _access = ReceiptEditPolicy.Evaluate(IsOwner(), ReceiptEditPolicy.Current, _r.CreatedAt, ReceiptEditPolicy.ServerNow, InQueue(), ReceiptEditService.IsFullyReturned(_r));
        UpdateWindowLabel();
        UpdateBanner();
        if (was && !_access.Allowed)
            BuildFooter(); // окно истекло, пока форма открыта: «Проверить →» выключается
    }

    private void UpdateStatus()
    {
        var edited = _store.IsEdited(_r.Id);
        var version = _store.Version(_r.Id);
        string text;
        string bg = "BrushSuccessSoft", fg = "BrushText";
        if (_store.Queue(_r.Id).Count > 0)
        {
            text = T("Правка в очереди", "Оңдоо кезекте", "Edit queued", "Düzenleme kuyrukta", "Tahrir navbatda");
            bg = "BrushWarningSoft";
        }
        else if (edited)
            text = T($"Исправлен · v{version}", $"Оңдолду · v{version}", $"Corrected · v{version}", $"Düzeltildi · v{version}", $"Tuzatildi · v{version}");
        else if (_r.ReturnedAmount > 0.005 && _r.ReturnedAmount < _r.Total - 0.005)
        {
            text = T("Возврат частичный", "Жарым-жартылай кайтаруу", "Partial return", "Kısmi iade", "Qisman qaytarish");
            bg = "BrushWarningSoft";
        }
        else if (ReceiptEditService.IsFullyReturned(_r))
        {
            text = T("Возвращён", "Кайтарылды", "Returned", "İade edildi", "Qaytarilgan");
            bg = "BrushPanelSoft";
            fg = "BrushTextSoft";
        }
        else
            text = T("Проведён", "Өткөрүлдү", "Posted", "İşlendi", "O'tkazildi");
        _statusHost.Child = SectionCards.Pill(this, text, bg, fg);
        if (_edit)
            _statusHost.Child = SectionCards.Pill(this, T("Правка", "Оңдоо", "Editing", "Düzenleme", "Tahrir"), "BrushAccentSoft", "BrushText");
    }

    private void UpdateWindowLabel()
    {
        if (!IsOwner() && !_store.IsEdited(_r.Id))
        {
            _windowLabel.IsVisible = false;
            return;
        }
        _windowLabel.IsVisible = IsOwner();
        if (!IsOwner())
            return;
        string text;
        string brush = "BrushTextSoft";
        if (_access.Allowed)
        {
            if (_access.Remaining is { } left)
            {
                text = "⏱ " + T($"Можно править ещё {ReceiptEditPolicy.RemainingText(left)}", $"Дагы {ReceiptEditPolicy.RemainingText(left)} оңдоого болот", $"Editable for {ReceiptEditPolicy.RemainingText(left)} more",
                    $"{ReceiptEditPolicy.RemainingText(left)} daha düzenlenebilir", $"Yana {ReceiptEditPolicy.RemainingText(left)} tahrirlash mumkin");
                brush = left < TimeSpan.FromHours(1) ? "BrushWarning" : "BrushSuccess";
            }
            else
            {
                text = T("Правка без ограничения времени", "Оңдоо убакыт чектөөсүз", "Editing has no time limit", "Düzenleme süre sınırsız", "Tahrirlash vaqt cheklovisiz");
                brush = "BrushSuccess";
            }
        }
        else
            text = ReceiptEditPolicy.DeniedText(_access);
        _windowLabel.Text = text;
        _windowLabel.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
    }

    /// <summary>2026-10-30 (приёмка 1.17.66): предупреждение вверху окна — правка с другого ПК, номер выдан повторно (две кассы взяли один).</summary>
    private string? _notice;

    /// <summary>Открытие накладной: (1) пометка «версия N» в возвратах и доприходах на сервере — правку мог сделать другой ПК, чей журнал здесь не виден;
    /// (2) номер документа сдвинут из-за коллизии касс. Нет связи — мягкое «правка с другого ПК может быть не видна — обновите».</summary>
    private async Task CheckOtherPcAsync()
    {
        var parts = new List<string>();
        if (DocumentNumbering.Store.IsConflicted(DocSeries.Receipt, _r.Id))
        {
            PosLogger.Log($"Накладная {_docNumber}: номер выдан повторно (две кассы взяли один) — документу назначен следующий свободный.", "WARNING");
            parts.Add(T($"Номер {_docNumber} выдан этому приходу повторно: другая касса взяла тот же номер раньше. На бумажной копии мог остаться прежний номер — сверьте.",
                $"{_docNumber} номери бул кирүүгө кайра берилди: башка касса ошол номерди мурун алган. Кагаз көчүрмөдө мурунку номер калышы мүмкүн — салыштырыңыз.",
                $"Number {_docNumber} was issued to this receipt again: another till took the same number earlier. The paper copy may carry the old number — check it.",
                $"{_docNumber} numarası bu girişe yeniden verildi: başka bir kasa aynı numarayı daha önce aldı. Kağıt kopyada eski numara kalmış olabilir — kontrol edin.",
                $"{_docNumber} raqami bu kirimga qayta berildi: boshqa kassa bu raqamni oldinroq olgan. Qog'oz nusxada eski raqam qolgan bo'lishi mumkin — tekshiring."));
        }
        if (_r.SupplierId.Length > 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var remote = await new ApiReceiptEditServer(App.GetRequiredService<NurMarketApiClient>()).RemoteEditVersionAsync(_r.SupplierId, _docNumber, _r.Id, _r.CreatedAt, cts.Token) /* 2026-10-30 (приёмка 1.17.67): по серверному id прихода, а не по номеру */.ConfigureAwait(true);
                var history = _store.Get(_r.Id);
                if (remote > history.Version && !ReceiptEditService.HasPostedVersion(history, remote)) // 2026-10-31 (приёмка 1.17.68, 4а): отклонённая попытка не считается версией
                    parts.Add(ReceiptEditService.ForeignEditText(remote));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Накладная {_docNumber}: правки с других ПК не проверены ({ex.Message}).", "INFO");
                if (IsOwner())
                    parts.Add(ReceiptEditService.RemoteUnknownText);
            }
        }
        _notice = parts.Count == 0 ? null : string.Join("\n", parts);
        UpdateBanner();
    }

    private void UpdateBanner()
    {
        string? text = null;
        var q = _store.Queue(_r.Id);
        var unfinished = _store.Get(_r.Id).Edits.LastOrDefault(e => e.Status is "applying" or "partial");
        if (unfinished is not null)
            text = T($"Правка v{unfinished.Version} не доведена до конца (выполнено {unfinished.Steps.Count(s => s.Status == "done")} из {unfinished.Steps.Count} шагов). Нажмите «Изменить» → «Довести правку» или откажитесь от неё.",
                $"v{unfinished.Version} оңдоосу аягына чыккан жок. «Өзгөртүү» → «Аягына чыгаруу» басыңыз же баш тартыңыз.", $"Edit v{unfinished.Version} is not finished. Press “Edit” → “Finish the edit”, or abandon it.",
                $"v{unfinished.Version} düzenlemesi bitmedi. «Düzenle» → «Düzenlemeyi tamamla» veya vazgeçin.", $"v{unfinished.Version} tahriri tugallanmagan. «O'zgartirish» → «Tahrirni tugatish» yoki voz keching.");
        else if (q.Count > 0)
            text = T("Есть правка, поставленная в очередь без связи. Нажмите «Изменить» и подтвердите её паролем — перед отправкой приход сверится с сервером.",
                "Байланышсыз кезекке коюлган оңдоо бар. «Өзгөртүү» басып, сырсөз менен ырастаңыз — жөнөтүүдөн мурун кирүү сервер менен салыштырылат.",
                "An edit was queued without a connection. Press “Edit” and confirm it with your password — the receipt is checked against the server before sending.",
                "Bağlantısız kuyruğa alınmış bir düzenleme var. «Düzenle»ye basıp parolayla onaylayın — göndermeden önce giriş sunucuyla karşılaştırılır.",
                "Aloqasiz navbatga qo'yilgan tahrir bor. «O'zgartirish»ni bosib parol bilan tasdiqlang — yuborishdan oldin kirim server bilan solishtiriladi.");
        else if (_edit && _access.Remaining is { } left && left < TimeSpan.FromHours(1))
            text = T($"Осталось {ReceiptEditPolicy.RemainingText(left)} — после этого проверить и сохранить правку будет нельзя.", $"{ReceiptEditPolicy.RemainingText(left)} калды — андан кийин оңдоону сактоого болбойт.",
                $"{ReceiptEditPolicy.RemainingText(left)} left — after that the edit can't be saved.", $"{ReceiptEditPolicy.RemainingText(left)} kaldı — sonra düzenleme kaydedilemez.", $"{ReceiptEditPolicy.RemainingText(left)} qoldi — undan keyin tahrirni saqlab bo'lmaydi.");
        else if (_edit && !_access.Allowed)
            text = ReceiptEditPolicy.DeniedText(_access);
        else if (_notice is { Length: > 0 })
            text = _notice;
        _bannerText.Text = text ?? "";
        _banner.IsVisible = !string.IsNullOrEmpty(text);
    }

    // ------------------------------------------------------------------ подвал

    private Button Outline(string text, Action click)
    {
        var b = SectionCards.Secondary(text);
        b.Click += (_, _) => click();
        return b;
    }

    private void BuildFooter()
    {
        _footer.Children.Clear();
        var wrap = _footer;
        void Add(Button b)
        {
            b.Margin = new Thickness(8, 3, 0, 3);
            if (_compact)
            {
                b.MinHeight = 40;
                b.Padding = new Thickness(12, 0);
            }
            wrap.Children.Add(b);
        }
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        if (_edit)
        {
            var cancel = Outline(T("Отменить правки", "Оңдоолорду жокко чыгаруу", "Discard edits", "Düzenlemeleri iptal et", "Tahrirlarni bekor qilish"), () => CancelEdit());
            Add(close);
            Add(cancel);
            Add(Outline(T("История версий", "Версиялар тарыхы", "Version history", "Sürüm geçmişi", "Versiyalar tarixi"), () => _ = ShowHistoryAsync()));
            var check = SectionCards.Primary(T("Проверить →", "Текшерүү →", "Review →", "Gözden geçir →", "Tekshirish →"));
            check.IsEnabled = _access.Allowed;
            check.Click += (_, _) => _ = ReviewAsync();
            ToolTip.SetTip(check, "Ctrl+S");
            Add(check);
        }
        else
        {
            Add(close);
            var print = Outline(T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish") + (_compact ? "" : "  Ctrl+P"), () => _ = PrintAsync());
            Add(print);
            var pdf = Outline("PDF", () => _ = SavePdfAsync());
            pdf.IsEnabled = _rows.Count > 0;
            Add(pdf);
            var csv = Outline(_compact ? "CSV" : "Excel / CSV", () => SaveCsv());
            csv.IsEnabled = _rows.Count > 0;
            Add(csv);
            if (_store.IsEdited(_r.Id) || IsOwner())
                Add(Outline(_compact ? T("Версии", "Версиялар", "Versions", "Sürümler", "Versiyalar") : T("История версий", "Версиялар тарыхы", "Version history", "Sürüm geçmişi", "Versiyalar tarixi"), () => _ = ShowHistoryAsync()));
            if (_context is null || _showReturnRepeat)
            {
                var giveBack = Outline(T("Вернуть", "Кайтаруу", "Return", "İade", "Qaytarish"), () => { Action = "return"; Close(); });
                giveBack.IsEnabled = _r.ReturnedAmount < _r.Total - 0.005;
                Add(giveBack);
                var repeat = Outline(T("Повторить", "Кайталоо", "Repeat", "Tekrarla", "Takrorlash"), () => { Action = "repeat"; Close(); });
                ToolTip.SetTip(repeat, T("Новый приём с этими же товарами и ценами", "Ушул эле товарлар жана баалар менен жаңы кабыл алуу", "New receiving with the same products and prices", "Aynı ürün ve fiyatlarla yeni kabul", "Shu mahsulotlar va narxlar bilan yangi qabul"));
                Add(repeat);
            }
            if (_context?.InvoiceFilePath is { Length: > 0 } filePath && !OperatingSystem.IsAndroid())
                Add(Outline(T("Открыть файл накладной", "Накладнойдун файлын ачуу", "Open invoice file", "Fatura dosyasını aç", "Yuk xati faylini ochish"), () => PurchaseDocs.OpenFile(filePath)));
            // «Изменить» — только владельцу (у остальных кнопки нет совсем); окно истекло — кнопка есть, но выключена с причиной.
            if (IsOwner() && !OperatingSystem.IsAndroid() && _showMoney)
            {
                var edit = SectionCards.Primary(T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish"));
                edit.IsEnabled = _access.Allowed;
                if (!_access.Allowed)
                    ToolTip.SetTip(edit, ReceiptEditPolicy.DeniedText(_access));
                edit.Click += (_, _) => _ = EnterEditAsync();
                Add(edit);
                // 2026-10-30 (приёмка 1.17.67, «Важно» 5): окно правки истекло, а начатая правка не доведена — довести или отказаться от неё можно и теперь.
                if (!_access.Allowed && _store.Get(_r.Id).Edits.Any(e => e.Status is "applying" or "partial"))
                {
                    var finish = SectionCards.Primary(T("Довести / отменить правку", "Оңдоону аягына чыгаруу / жокко чыгаруу", "Finish / cancel the edit", "Düzenlemeyi tamamla / iptal et", "Tahrirni tugatish / bekor qilish"));
                    finish.Click += (_, _) => _ = EnterEditAsync();
                    Add(finish);
                }
            }
        }
    }

    // ------------------------------------------------------------------ печать, PDF, CSV

    private PurchaseDocs.Party Party => new(_r.SupplierName, _supplier?.Phone ?? "", _supplier?.Inn ?? "", _supplier?.Address ?? "");

    private ClientDocRenderer.Input BuildDoc(bool withMarkup)
    {
        var lines = _rows.Where(x => x.Qty > 0).Select(x => new PurchaseDocs.DocLine(x.Name, x.Unit, x.Qty, x.Purchase,
            withMarkup && _showMoney && x.Sale > 0 ? PurchaseDocs.MarkupSuffix(new MarkupView(InvoiceMath.MarkupFromSale(x.Purchase, x.Sale), x.Sale, InvoiceMath.ProfitPerUnit(x.Purchase, x.Sale), x.SaleEstimated, false, false)) : null)).ToList();
        var supplierDoc = PurchaseNumberUi.SupplierDocText(_r.SupplierDocNumber, _r.SupplierDocDate);
        var totals = withMarkup && _showMoney ? (MarkupTotals?)ReceiptMarkupLog.Totals(_rows.Where(x => x.Qty > 0).Select(x => (x.Qty, x.Purchase, new MarkupView(InvoiceMath.MarkupFromSale(x.Purchase, x.Sale), x.Sale, 0, x.SaleEstimated, false, false)))) : null;
        var note = PurchaseDocs.ReceiptNote(supplierDoc.Length > 0 ? char.ToUpperInvariant(supplierDoc[0]) + supplierDoc[1..] : null, totals);
        // «ИСПРАВЛЕНО · версия N от … · причина: …» — шапкой примечания у правленной накладной.
        if (ReceiptEditService.CorrectedStamp(_store.Get(_r.Id)) is { } stamp)
            note = stamp + (note is { Length: > 0 } ? "\n" + note : "");
        return PurchaseDocs.Build(this, "receipt", Party, _docNumber, _r.CreatedAt ?? DateTime.Now, lines, note);
    }

    private string VersionSuffix => _store.IsEdited(_r.Id) ? $"_v{_store.Version(_r.Id)}" : "";

    private async Task PrintAsync()
    {
        var error = await PurchaseDocs.PrintAsync(BuildDoc(DocumentNumbering.Settings.PrintMarkup), "Накладная " + _docNumber + VersionSuffix).ConfigureAwait(true);
        Say(error ?? T("Отправлено на принтер.", "Принтерге жөнөтүлдү.", "Sent to the printer.", "Yazıcıya gönderildi.", "Printerga yuborildi."));
    }

    private async Task SavePdfAsync()
    {
        var name = PurchaseDocs.FileName("receipt", _r.SupplierName, _docNumber + VersionSuffix, _r.CreatedAt ?? DateTime.Now);
        var (path, error) = await PurchaseDocs.SavePdfAsync(BuildDoc(DocumentNumbering.Settings.PdfMarkup), name).ConfigureAwait(true);
        Say(path is not null ? T("PDF сохранён: ", "PDF сакталды: ", "PDF saved: ", "PDF kaydedildi: ", "PDF saqlandi: ") + path
            : T("PDF не сохранён: ", "PDF сакталган жок: ", "PDF not saved: ", "PDF kaydedilemedi: ", "PDF saqlanmadi: ") + error);
    }

    /// <summary>CSV (UTF-8 с BOM, разделитель «;») — открывается в Excel. Файл в «Документы\NurMarket\Закупки», как PDF.</summary>
    private void SaveCsv()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NurMarket", "Закупки");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, PurchaseDocs.FileName("receipt", _r.SupplierName, _docNumber + VersionSuffix, _r.CreatedAt ?? DateTime.Now).Replace(".pdf", ".csv"));
            File.WriteAllText(file, BuildCsv(), new System.Text.UTF8Encoding(true));
            Say(T("Файл сохранён: ", "Файл сакталды: ", "File saved: ", "Dosya kaydedildi: ", "Fayl saqlandi: ") + file);
            PurchaseDocs.OpenFile(file);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Накладная: CSV не сохранён ({ex.Message}).", "WARNING");
            Say(T("Файл не сохранён: ", "Файл сакталган жок: ", "File not saved: ", "Dosya kaydedilemedi: ", "Fayl saqlanmadi: ") + ex.Message);
        }
    }

    /// <summary>Строки накладной в CSV; закупка, сумма, наценка и цена реализации — только при праве на закупочные цены.</summary>
    internal string BuildCsv()
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new System.Text.StringBuilder();
        var cols = new List<string> { "№", T("Код", "Коду", "Code", "Kod", "Kod"), T("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), T("Наименование", "Аталышы", "Name", "Ad", "Nomi"), T("Ед.", "Бирдик", "Unit", "Birim", "Birlik"), T("Количество", "Саны", "Qty", "Adet", "Miqdor") };
        if (_showMoney)
            cols.AddRange(new[] { T("Цена покупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), T("Наценка %", "Үстөк %", "Markup %", "Kâr marjı %", "Ustama %"),
                T("Цена реализации", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotish narxi"), T("Прибыль с шт.", "Даанасынан пайда", "Profit per unit", "Birim kâr", "Dona foydasi") });
        sb.AppendLine(string.Join(";", cols.Select(Q)));
        foreach (var x in _rows)
        {
            var cells = new List<string> { x.No.ToString(Ru), Q(x.Code), Q(x.Barcode), Q(x.Name), Q(x.Unit), x.Qty.ToString("0.###", Ru) };
            if (_showMoney)
                cells.AddRange(new[] { x.Purchase.ToString("0.##", Ru), ReceivingPricing.LineTotal(x.Qty, x.Purchase).ToString("0.00", Ru), InvoiceMath.MarkupFromSale(x.Purchase, x.Sale)?.ToString("0.##", Ru) ?? "",
                    x.Sale > 0 ? x.Sale.ToString("0.##", Ru) : "", x.Sale > 0 ? InvoiceMath.ProfitPerUnit(x.Purchase, x.Sale).ToString("0.##", Ru) : "" });
            sb.AppendLine(string.Join(";", cells));
        }
        return sb.ToString();
    }

    private void Say(string text)
    {
        if (_compact)
        {
            _message.MaxLines = 2;
            _message.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(_message, text);
        }
        _message.Text = text;
        _message.IsVisible = text.Length > 0;
    }

    // ------------------------------------------------------------------ клавиши, закрытие

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var typing = e.Source is TextBox;
        if (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.None)
        {
            FocusSearchForInsert();
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            _barcode.Focus();
            _barcode.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.P && !_edit)
        {
            _ = PrintAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S && _edit && _access.Allowed)
        {
            _ = ReviewAsync();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z && _edit && !(e.Source is TextBox { IsFocused: true } tb && tb.Tag is not null && false))
        {
            UndoLast();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _edit && !typing && _list.SelectedItem is InvoiceRow row)
        {
            row.Qty = 0;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !typing && (_search.Text ?? "").Length > 0)
        {
            _search.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && typing && e.Source == _search && (_search.Text ?? "").Length > 0)
        {
            _search.Text = "";
            e.Handled = true;
        }
    }

    private bool _closeConfirmed;

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_edit || !_rows.Any(r => r.Changed))
            return;
        e.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        var ok = await PosConfirmDialog.ShowModalAsync(this, T("Закрыть без сохранения?", "Сактабай жабасызбы?", "Close without saving?", "Kaydetmeden kapatılsın mı?", "Saqlamasdan yopilsinmi?"),
            T("Изменённые количества будут потеряны.", "Өзгөртүлгөн сандар жоголот.", "The changed quantities will be lost.", "Değiştirilen miktarlar kaybolacak.", "O'zgartirilgan miqdorlar yo'qoladi."),
            T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), T("Вернуться", "Кайтуу", "Back", "Geri", "Orqaga"), PosConfirmAccent.Danger).ConfigureAwait(true);
        if (ok)
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
