using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-11-19 («Смета», ТЗ 2–6): редактор сметы. Шапка (объект, клиент из базы или вручную, телефон, адрес, «действует до» — 7 дней, уровень цены «розница / прораб», запас и скидка сметы, примечание),
/// строка добавления (название, штрихкод или артикул: Enter или сканер добавляют позицию; «Своя позиция», «Рассчитать материал», «+ Раздел»), таблица позиций (количество, цена, скидка и запас строки, раздел,
/// сумма — всё редактируется на месте), итоги. Кассир видит цены продажи; закупку и маржу — только владелец и сотрудники с правом на закупку. Печать, текст для WhatsApp/Telegram, «Пересчитать цены»
/// (после срока), «Нормы расхода», «Добавить всё в чек» — только в кассе. Правки сохраняются сами (через 0,7 с и при закрытии). Все суммы считает <see cref="EstimateRules"/> — окно не округляет.
/// Esc закрывает окно.
/// </summary>
public sealed class EstimateEditorWindow : Window
{
    private readonly EstimateDeps _deps;
    private readonly EstimateDoc _doc;
    private readonly ModalShell _shell = new();
    private readonly TextBox _title, _client, _phone, _until, _address, _reserve, _discount, _note, _add;
    private readonly ComboBox _level = new() { Name = "EstimateLevel", Height = 40 };
    private readonly StackPanel _rows = new();
    private readonly ListBox _suggest = new() { Name = "EstimateSuggest", MaxHeight = 150, IsVisible = false };
    private readonly ListBox _clientSuggest = new() { Name = "EstimateClientSuggest", MaxHeight = 150, IsVisible = false };
    private readonly TextBlock _totals = ServiceDeskUi.Text("", 15, true);
    private readonly TextBlock _internal = ServiceDeskUi.Text("", 12.5, brush: "BrushTextSoft");
    private readonly TextBlock _expired = ServiceDeskUi.Text("", 13, true, "BrushDanger");
    private readonly TextBlock _status = ServiceDeskUi.Text("", 12.5, brush: "BrushTextSoft");
    private readonly TextBlock _hint = ServiceDeskUi.Text("", 12.5, brush: "BrushTextSoft");
    private readonly TextBlock _headSummary = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 860 };
    private readonly Expander _headExpander = new() { Name = "EstimateHeaderExpander", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, TextBlock> _sums = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private List<ClientDocsUi.Buyer>? _buyers;
    private bool _filling;
    private bool _dirty;

    /// <summary>true — всё перенесено в чек: список смет закроется следом, кассир сразу видит чек.</summary>
    public bool AddedToCart { get; private set; }

    public EstimateDoc Doc => _doc;

    public string StatusMessage => _status.Text ?? "";

    public string TotalsText => _totals.Text ?? "";

    public string InternalText => _internal.IsVisible ? _internal.Text ?? "" : "";

    public bool ExpiredBannerVisible => _expired.IsVisible;

    public int RowCount => _rows.Children.OfType<Control>().Count(c => c.Name?.StartsWith("EstimateRow_", StringComparison.Ordinal) == true);

    public EstimateEditorWindow(EstimateDeps deps, EstimateDoc doc)
    {
        _deps = deps;
        _doc = doc;
        Title = EstimateUi.T("Смета", "Смета", "Estimate", "Keşif", "Smeta") + " " + EstimateRules.Code(doc.Number);
        Width = 1040;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        Opened += (_, _) => this.FitToKassaScreen();
        Closing += (_, _) => PersistNow();

        _title = EstimateUi.Box("EstimateTitle", doc.Title, 260, EstimateUi.T("Название / объект", "Аталышы / объект", "Name / site", "Ad / şantiye", "Nomi / obyekt"));
        _client = EstimateUi.Box("EstimateClient", doc.ClientName, 210, EstimateUi.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"));
        _phone = EstimateUi.Box("EstimatePhone", doc.Phone, 150, EstimateUi.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), maxLength: 30);
        _until = EstimateUi.Box("EstimateUntil", EstimateUi.Day(doc.ValidUntil), 120, EstimateUi.T("Действует до", "Чейин жарактуу", "Valid until", "Geçerlilik", "Amal qiladi"), maxLength: 10);
        _address = EstimateUi.Box("EstimateAddress", doc.Address, 300, EstimateUi.T("Адрес объекта", "Объекттин дареги", "Site address", "Şantiye adresi", "Obyekt manzili"));
        _reserve = EstimateUi.Box("EstimateReserve", doc.ReservePercent == 0m ? "" : EstimateUi.Num(doc.ReservePercent), 90, EstimateUi.T("Запас сметы, %", "Сметанын запасы, %", "Estimate allowance, %", "Keşif fire payı, %", "Smeta zaxirasi, %"), "0", 5, right: true);
        _discount = EstimateUi.Box("EstimateDiscount", doc.DiscountPercent == 0m ? "" : EstimateUi.Num(doc.DiscountPercent), 90, EstimateUi.T("Общая скидка, %", "Жалпы арзандатуу, %", "Overall discount, %", "Genel indirim, %", "Umumiy chegirma, %"), "0", 5, right: true);
        _note = EstimateUi.Box("EstimateNote", doc.Note, 300, EstimateUi.T("Примечание", "Эскертүү", "Note", "Not", "Izoh"), maxLength: 400);
        _add = EstimateUi.Box("EstimateAdd", "", 400, EstimateUi.T("Добавить товар", "Товар кошуу", "Add a product", "Ürün ekle", "Tovar qo'shish"),
            EstimateUi.T("Название, штрихкод или артикул — Enter или сканер", "Аты, штрихкод же артикул — Enter же сканер", "Name, barcode or article — Enter or scan", "Ad, barkod veya stok kodu — Enter veya tarayıcı", "Nomi, shtrix-kod yoki artikul — Enter yoki skaner"), 80);
        BuildUi();
        RebuildRows();
        UpdateTotals();
    }

    // ───────────────────────────── построение ─────────────────────────────

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.ScreenHeightShare = 1.0;
        _shell.Margin = new Thickness(16);
        _shell.ContentScroll = ScrollBarVisibility.Disabled;

        void Bind(TextBox box, Action<string> set)
        {
            box.TextChanged += (_, _) => { if (!_filling) { set(box.Text ?? ""); MarkDirty(); } };
        }
        Bind(_title, t => _doc.Title = t.Trim());
        Bind(_phone, t => _doc.Phone = t.Trim());
        Bind(_address, t => _doc.Address = t.Trim());
        Bind(_note, t => _doc.Note = t.Trim());
        _client.TextChanged += (_, _) =>
        {
            if (_filling)
                return;
            _doc.ClientName = (_client.Text ?? "").Trim();
            _doc.ClientId = null; // введено вручную — связь с клиентом базы снимается, пока не выбран из подсказки
            MarkDirty();
            _ = FillClientSuggestionsAsync();
        };
        _until.TextChanged += (_, _) =>
        {
            if (_filling)
                return;
            if (DateTime.TryParseExact((_until.Text ?? "").Trim(), "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            {
                _doc.ValidUntil = d;
                MarkDirty();
                UpdateTotals();
            }
        };
        _reserve.TextChanged += (_, _) =>
        {
            if (_filling)
                return;
            _doc.ReservePercent = EstimateRules.TryParseNumber(_reserve.Text, 100m) ?? 0m;
            MarkDirty();
            RefreshSums();
        };
        _discount.TextChanged += (_, _) =>
        {
            if (_filling)
                return;
            _doc.DiscountPercent = EstimateRules.TryParseNumber(_discount.Text, 100m) ?? 0m;
            MarkDirty();
            UpdateTotals();
        };
        _level.ItemsSource = new[]
        {
            EstimateUi.T("Розница", "Чекене", "Retail", "Perakende", "Chakana"),
            EstimateUi.T("Прораб (опт)", "Прораб (дүң)", "Foreman (bulk)", "Usta (toptan)", "Prorab (ulgurji)"),
        };
        _level.SelectedIndex = (int)_doc.Level;
        Avalonia.Automation.AutomationProperties.SetName(_level, EstimateUi.T("Уровень цены", "Баа деңгээли", "Price level", "Fiyat düzeyi", "Narx darajasi"));
        _level.SelectionChanged += (_, _) =>
        {
            if (_filling || _level.SelectedIndex < 0)
                return;
            SetLevel((EstimateLevel)_level.SelectedIndex);
        };
        _suggest.Tapped += (_, _) => { if (_suggest.SelectedItem is ListBoxItem { Tag: EstimateCatalogInfo info }) AddProduct(info); };
        _clientSuggest.Tapped += (_, _) => { if (_clientSuggest.SelectedItem is ListBoxItem { Tag: ClientDocsUi.Buyer b }) PickBuyer(b); };
        _add.KeyDown += AddKeyDown;
        _add.TextChanged += (_, _) => FillSuggestions();

        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Название / объект", "Аталышы / объект", "Name / site", "Ad / şantiye", "Nomi / obyekt"), _title, 240));
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), _client, 190));
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), _phone, 140));
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Действует до", "Чейин жарактуу", "Valid until", "Geçerlilik", "Amal qiladi"), _until, 110));
        _level.HorizontalAlignment = HorizontalAlignment.Stretch;
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Цена", "Баа", "Price", "Fiyat", "Narx"), _level, 190));
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Адрес объекта", "Объекттин дареги", "Site address", "Şantiye adresi", "Obyekt manzili"), _address, 280));
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Запас, %", "Запас, %", "Allowance, %", "Fire, %", "Zaxira, %"), _reserve, 100));
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Скидка, %", "Арзандатуу, %", "Discount, %", "İndirim, %", "Chegirma, %"), _discount, 110));
        head.Children.Add(EstimateUi.Labeled(EstimateUi.T("Примечание", "Эскертүү", "Note", "Not", "Izoh"), _note, 280));

        // 2026-11-19: на 1024×600 шапка занимала полэкрана — у сметы с позициями она свёрнута в одну строку («Объект · клиент · телефон · до …»), у новой — раскрыта.
        ServiceDeskUi.Use(_headSummary, TextBlock.ForegroundProperty, "BrushText");
        _headExpander.Header = _headSummary;
        _headExpander.Content = head;
        _headExpander.IsExpanded = _doc.Lines.Count == 0;
        Avalonia.Automation.AutomationProperties.SetName(_headExpander, EstimateUi.T("Шапка сметы", "Сметанын башы", "Estimate header", "Keşif başlığı", "Smeta sarlavhasi"));

        var addRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _add.Margin = new Thickness(0, 0, 8, 6);
        addRow.Children.Add(_add);
        addRow.Children.Add(ServiceDeskUi.Action("EstimateAddCustom", EstimateUi.T("＋ Своя позиция", "＋ Өз позициясы", "＋ Custom line", "＋ Özel kalem", "＋ O'z pozitsiyasi"), () => AddCustomLine()));
        addRow.Children.Add(ServiceDeskUi.Action("EstimateCalc", EstimateUi.T("Рассчитать материал", "Материалды эсептөө", "Calculate material", "Malzeme hesapla", "Materialni hisoblash"), () => _ = OpenCalcAsync(), primary: true));
        addRow.Children.Add(ServiceDeskUi.Action("EstimateAddSection", EstimateUi.T("＋ Раздел", "＋ Бөлүм", "＋ Section", "＋ Bölüm", "＋ Bo'lim"), () => AddSection()));

        var table = new StackPanel();
        table.Children.Add(BuildHeaderRow());
        table.Children.Add(_rows);
        var scroll = new ScrollViewer { Content = table, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        _expired.Name = "EstimateExpired";
        _expired.IsVisible = false;
        _totals.Name = "EstimateTotals";
        _internal.Name = "EstimateInternal";
        _status.Name = "EstimateStatusText";
        _hint.Name = "EstimateHint";
        _hint.IsVisible = false;

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        actions.Children.Add(ServiceDeskUi.Action("EstimatePrint", EstimateUi.T("Печать…", "Басуу…", "Print…", "Yazdır…", "Chop etish…"), () => _ = OpenPrintAsync(), primary: true));
        actions.Children.Add(ServiceDeskUi.Action("EstimateCopy", EstimateUi.T("Скопировать текстом", "Текст катары көчүрүү", "Copy as text", "Metin olarak kopyala", "Matn sifatida nusxalash"), () => _ = CopyTextAsync()));
        actions.Children.Add(ServiceDeskUi.Action("EstimateWhatsApp", EstimateUi.T("Открыть в WhatsApp", "WhatsApp'та ачуу", "Open in WhatsApp", "WhatsApp'ta aç", "WhatsApp'da ochish"), () => _ = OpenWhatsAppAsync()));
        actions.Children.Add(ServiceDeskUi.Action("EstimateRefresh", EstimateUi.T("Пересчитать цены", "Баалар кайра эсептөө", "Recalculate prices", "Fiyatları yeniden hesapla", "Narxlarni qayta hisoblash"), () => RefreshPrices()));
        if (_deps.Till is not null)
            actions.Children.Add(ServiceDeskUi.Action("EstimateToCart", EstimateUi.T("Добавить всё в чек", "Баарын чекке кошуу", "Add all to the receipt", "Hepsini fişe ekle", "Hammasini chekka qo'shish"), () => _ = AddToCartAsync(), primary: true));

        // 2026-11-19: кнопки действий чуть компактнее (13 px, поля 10×4) — на 1024×600 они должны помещаться в одну строку, иначе таблице не хватает высоты.
        foreach (var b in actions.Children.OfType<Button>())
        {
            b.Padding = new Thickness(10, 4);
            b.MinHeight = 38;
            b.FontSize = 13;
            b.Margin = new Thickness(0, 0, 6, 4);
        }

        var bottom = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        bottom.Children.Add(_expired);
        bottom.Children.Add(_totals);
        bottom.Children.Add(_internal);
        bottom.Children.Add(actions);
        bottom.Children.Add(_status);

        var top = new StackPanel();
        top.Children.Add(_headExpander);
        top.Children.Add(_clientSuggest);
        top.Children.Add(addRow);
        top.Children.Add(_suggest);
        top.Children.Add(_hint);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(bottom);
        dock.Children.Add(scroll);
        _shell.Content = dock;

        // 2026-11-19: на экране 1024×600 таблице нужна высота — нижней полосы с «Закрыть» нет (закрывает ✕ в шапке и Esc), «Нормы расхода» — в калькуляторе материала.
        _shell.Footer = null;
        _shell.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Control>("PART_FooterGrid")?.Parent is Control footerBand)
                footerBand.IsVisible = false;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                e.Handled = true;
                Close(AddedToCart);
            }
        };
        _saveTimer.Tick += (_, _) => PersistNow();
        Content = _shell;
    }

    private string[] ColumnWidths(bool sections) => sections
        ? new[] { "34", "*", "60", "88", "100", "66", "66", "118", "118", "40" }
        : new[] { "34", "*", "60", "88", "100", "66", "66", "118", "40" };

    private Grid NewRowGrid() => new() { ColumnDefinitions = new ColumnDefinitions(string.Join(",", ColumnWidths(_doc.Sections.Count > 0))) };

    private Control BuildHeaderRow()
    {
        var g = NewRowGrid();
        g.Margin = new Thickness(0, 4, 0, 2);
        var titles = new List<string>
        {
            "№", EstimateUi.T("Товар / позиция", "Товар / позиция", "Product / line", "Ürün / kalem", "Tovar / pozitsiya"), EstimateUi.T("Ед.", "Бирд.", "Unit", "Birim", "Birl."),
            EstimateUi.T("Кол-во", "Саны", "Qty", "Miktar", "Soni"), EstimateUi.T("Цена", "Баасы", "Price", "Fiyat", "Narx"),
            EstimateUi.T("Скид. %", "Арз. %", "Disc. %", "İnd. %", "Chg. %"), EstimateUi.T("Запас %", "Запас %", "Spare %", "Fire %", "Zaxira %"),
        };
        if (_doc.Sections.Count > 0)
            titles.Add(EstimateUi.T("Раздел", "Бөлүм", "Section", "Bölüm", "Bo'lim"));
        titles.Add(EstimateUi.T("Сумма", "Суммасы", "Total", "Tutar", "Summa"));
        titles.Add("");
        for (var i = 0; i < titles.Count; i++)
        {
            var t = ServiceDeskUi.Text(titles[i], 11.5, true, "BrushTextSoft");
            Grid.SetColumn(t, i);
            t.Margin = new Thickness(2, 0, 2, 0);
            if (i == titles.Count - 2)
                t.TextAlignment = TextAlignment.Right;
            g.Children.Add(t);
        }
        return g;
    }

    /// <summary>Перестроить таблицу: сначала позиции без раздела, затем разделы (в том числе пустые).</summary>
    public void RebuildRows()
    {
        _rows.Children.Clear();
        _sums.Clear();
        // Шапка таблицы зависит от наличия разделов — заменяем её тоже.
        if (_rows.Parent is StackPanel table && table.Children.Count > 0 && table.Children[0] is Grid)
            table.Children[0] = BuildHeaderRow();
        var n = 0;
        var loose = _doc.Lines.Where(l => string.IsNullOrWhiteSpace(l.SectionId) || _doc.Sections.All(s => s.Id != l.SectionId)).ToList();
        foreach (var l in loose)
            _rows.Children.Add(BuildLineRow(l, ++n));
        foreach (var s in _doc.Sections)
        {
            _rows.Children.Add(BuildSectionRow(s));
            foreach (var l in _doc.Lines.Where(x => x.SectionId == s.Id))
                _rows.Children.Add(BuildLineRow(l, ++n));
        }
        if (_doc.Lines.Count == 0 && _doc.Sections.Count == 0)
            _rows.Children.Add(ServiceDeskUi.Text(EstimateUi.T("Позиций пока нет. Найдите товар сверху, нажмите «Рассчитать материал» или «＋ Своя позиция».", "Азырынча позиция жок. Жогортон товарды табыңыз, «Материалды эсептөө» же «＋ Өз позициясы» басыңыз.", "No lines yet. Find a product above, press “Calculate material” or “＋ Custom line”.", "Henüz kalem yok. Yukarıdan ürün bulun, «Malzeme hesapla» veya «＋ Özel kalem»e basın.", "Hozircha pozitsiya yo'q. Yuqoridan tovar toping, «Materialni hisoblash» yoki «＋ O'z pozitsiyasi» ni bosing."), 13, brush: "BrushTextSoft"));
        UpdateTotals();
    }

    private Control BuildSectionRow(EstimateSection s)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 2), Name = "EstimateSection_" + s.Id };
        var name = EstimateUi.Box("SectionName_" + s.Id, s.Name, 260, EstimateUi.T("Название раздела", "Бөлүмдүн аталышы", "Section name", "Bölüm adı", "Bo'lim nomi"), maxLength: 60);
        name.FontWeight = FontWeight.SemiBold;
        name.TextChanged += (_, _) => { if (!_filling) { s.Name = (name.Text ?? "").Trim(); MarkDirty(); } };
        var sum = ServiceDeskUi.Text("", 13, true, "BrushTextSoft");
        sum.VerticalAlignment = VerticalAlignment.Center;
        _sums["section:" + s.Id] = sum;
        var del = ServiceDeskUi.Action("SectionDelete_" + s.Id, "✕", () => RemoveSection(s.Id), danger: true);
        del.MinWidth = 40;
        del.Margin = new Thickness(0);
        ToolTip.SetTip(del, EstimateUi.T("Убрать раздел (позиции останутся)", "Бөлүмдү алып салуу (позициялар калат)", "Remove the section (lines stay)", "Bölümü kaldır (kalemler kalır)", "Bo'limni olib tashlash (pozitsiyalar qoladi)"));
        row.Children.Add(name);
        row.Children.Add(sum);
        row.Children.Add(del);
        return row;
    }

    private TextBox NumBox(string name, decimal? value, double width, string label, Action<decimal?> apply, decimal max, bool emptyIsNull = false, string? watermark = null)
    {
        var box = EstimateUi.Box(name, value is null ? "" : EstimateUi.Num(value.Value), width, label, watermark, 12, right: true);
        box.Margin = new Thickness(2, 0, 2, 0);
        box.TextChanged += (_, _) =>
        {
            if (_filling)
                return;
            var text = box.Text ?? "";
            var parsed = EstimateRules.TryParseNumber(text, max);
            if (string.IsNullOrWhiteSpace(text))
            {
                apply(emptyIsNull ? null : 0m);
                ServiceDeskUi.Use(box, Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty, "BrushInputBorder");
            }
            else if (parsed is { } v)
            {
                apply(v);
                ServiceDeskUi.Use(box, Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty, "BrushInputBorder");
            }
            else
            {
                ServiceDeskUi.Use(box, Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty, "BrushDanger");
                return;
            }
            MarkDirty();
            RefreshSums();
        };
        return box;
    }

    private Control BuildLineRow(EstimateLine l, int number)
    {
        var g = NewRowGrid();
        g.Name = "EstimateRow_" + number;
        g.Margin = new Thickness(0, 0, 0, 4);
        var col = 0;
        void Put(Control c)
        {
            Grid.SetColumn(c, col++);
            g.Children.Add(c);
        }
        var num = ServiceDeskUi.Text(number.ToString(System.Globalization.CultureInfo.InvariantCulture), 13, brush: "BrushTextSoft", wrap: false);
        num.VerticalAlignment = VerticalAlignment.Center;
        Put(num);
        var name = EstimateUi.Box("LineName_" + number, l.Name, 100, EstimateUi.T("Название позиции", "Позициянын аталышы", "Line name", "Kalem adı", "Pozitsiya nomi"), maxLength: 200);
        name.Width = double.NaN;
        name.HorizontalAlignment = HorizontalAlignment.Stretch;
        name.Margin = new Thickness(2, 0, 2, 0);
        name.TextChanged += (_, _) => { if (!_filling) { l.Name = (name.Text ?? "").Trim(); MarkDirty(); } };
        if (!string.IsNullOrWhiteSpace(l.CalcNote))
            ToolTip.SetTip(name, l.CalcNote);
        Put(name);
        var unit = EstimateUi.Box("LineUnit_" + number, l.Unit, 56, EstimateUi.T("Единица", "Бирдик", "Unit", "Birim", "Birlik"), maxLength: 12);
        unit.Margin = new Thickness(2, 0, 2, 0);
        unit.TextChanged += (_, _) => { if (!_filling) { l.Unit = (unit.Text ?? "").Trim(); MarkDirty(); RefreshSums(); } };
        Put(unit);
        Put(NumBox("LineQty_" + number, l.Qty, 84, EstimateUi.T("Количество", "Саны", "Quantity", "Miktar", "Miktar"), v => l.Qty = v ?? 0m, EstimateRules.MaxQty));
        Put(NumBox("LinePrice_" + number, l.Price, 96, EstimateUi.T("Цена", "Баасы", "Price", "Fiyat", "Narx"), v => l.Price = v ?? 0m, 100_000_000m));
        Put(NumBox("LineDisc_" + number, l.DiscountPercent == 0m ? null : l.DiscountPercent, 62, EstimateUi.T("Скидка строки, %", "Саптын арзандатуусу, %", "Line discount, %", "Satır indirimi, %", "Qator chegirmasi, %"), v => l.DiscountPercent = v ?? 0m, 100m, watermark: "0"));
        Put(NumBox("LineReserve_" + number, l.ReservePercent, 62, EstimateUi.T("Запас строки, %", "Саптын запасы, %", "Line allowance, %", "Satır fire payı, %", "Qator zaxirasi, %"), v => l.ReservePercent = v, 100m, emptyIsNull: true, watermark: EstimateUi.Num(_doc.ReservePercent)));
        if (_doc.Sections.Count > 0)
        {
            var combo = new ComboBox { Name = "LineSection_" + number, Height = 40, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(2, 0, 2, 0) };
            var names = new List<string> { "—" };
            names.AddRange(_doc.Sections.Select(s => string.IsNullOrWhiteSpace(s.Name) ? "…" : s.Name));
            combo.ItemsSource = names;
            var idx = _doc.Sections.FindIndex(s => s.Id == l.SectionId);
            combo.SelectedIndex = idx < 0 ? 0 : idx + 1;
            Avalonia.Automation.AutomationProperties.SetName(combo, EstimateUi.T("Раздел", "Бөлүм", "Section", "Bölüm", "Bo'lim"));
            combo.SelectionChanged += (_, _) =>
            {
                if (_filling || combo.SelectedIndex < 0)
                    return;
                l.SectionId = combo.SelectedIndex == 0 ? null : _doc.Sections[combo.SelectedIndex - 1].Id;
                MarkDirty();
                Dispatcher.UIThread.Post(RebuildRows, DispatcherPriority.Background);
            };
            Put(combo);
        }
        var sum = ServiceDeskUi.Text("", 13.5, true, wrap: false);
        sum.TextAlignment = TextAlignment.Right;
        sum.VerticalAlignment = VerticalAlignment.Center;
        sum.Margin = new Thickness(0, 0, 6, 0);
        _sums[l.Id] = sum;
        Put(sum);
        var del = ServiceDeskUi.Action("LineRemove_" + number, "✕", () => RemoveLine(l.Id), danger: true);
        del.MinWidth = 36;
        del.Padding = new Thickness(6, 6);
        del.Margin = new Thickness(0);
        Avalonia.Automation.AutomationProperties.SetName(del, EstimateUi.T("Убрать позицию", "Позицияны алып салуу", "Remove the line", "Kalemi kaldır", "Pozitsiyani olib tashlash") + ": " + l.Name);
        Put(del);
        return g;
    }

    // ───────────────────────────── итоги ─────────────────────────────

    private void RefreshSums()
    {
        foreach (var l in _doc.Lines)
            if (_sums.TryGetValue(l.Id, out var tb))
                tb.Text = MoneyText.Amount(EstimateRules.LineSum(l, _doc));
        foreach (var s in _doc.Sections)
            if (_sums.TryGetValue("section:" + s.Id, out var tb))
                tb.Text = MoneyText.Som(EstimateRules.SectionTotal(_doc, s.Id));
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        foreach (var l in _doc.Lines)
            if (_sums.TryGetValue(l.Id, out var tb) && string.IsNullOrEmpty(tb.Text))
                tb.Text = MoneyText.Amount(EstimateRules.LineSum(l, _doc));
        foreach (var s in _doc.Sections)
            if (_sums.TryGetValue("section:" + s.Id, out var tb) && string.IsNullOrEmpty(tb.Text))
                tb.Text = MoneyText.Som(EstimateRules.SectionTotal(_doc, s.Id));
        var total = EstimateUi.T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami") + ": " + MoneyText.Som(EstimateRules.Total(_doc));
        _totals.Text = _doc.DiscountPercent > 0m
            ? EstimateUi.T("Сумма", "Суммасы", "Subtotal", "Ara toplam", "Summa") + ": " + MoneyText.Som(EstimateRules.Subtotal(_doc)) + " · "
              + EstimateUi.T("скидка", "арзандатуу", "discount", "indirim", "chegirma") + " " + EstimateUi.Qty(_doc.DiscountPercent) + " %: −" + MoneyText.Som(EstimateRules.DiscountAmount(_doc)) + " · " + total
            : total;
        _internal.IsVisible = _deps.CanSeeCost();
        if (_internal.IsVisible)
            _internal.Text = EstimateText.InternalSummary(_doc);
        var expired = EstimateRules.IsExpired(_doc, _deps.Today());
        _expired.IsVisible = expired;
        if (expired)
            _expired.Text = EstimateUi.T("Срок сметы прошёл — цены могли измениться. Нажмите «Пересчитать цены».", "Сметанын мөөнөтү өттү — баалар өзгөрүшү мүмкүн. «Баалар кайра эсептөө» басыңыз.", "The estimate has expired — prices may have changed. Press “Recalculate prices”.", "Keşfin süresi doldu — fiyatlar değişmiş olabilir. «Fiyatları yeniden hesapla»ya basın.", "Smeta muddati o'tdi — narxlar o'zgargan bo'lishi mumkin. «Narxlarni qayta hisoblash» ni bosing.");
        _shell.Title = $"{EstimateUi.Title} {EstimateRules.Code(_doc.Number)} · {EstimateUi.StatusName(_doc.Status)}";
        _headSummary.Text = string.Join(" · ", new[] { _doc.Title, _doc.ClientName, _doc.Phone, EstimateUi.T("до ", "чейин ", "until ", "şu tarihe kadar ", "gacha ") + EstimateUi.Day(_doc.ValidUntil) }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private void Say(string text) => _status.Text = text;

    // ───────────────────────────── сохранение ─────────────────────────────

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateTotals();
    }

    /// <summary>Сохранить сейчас (таймер, закрытие окна, перед печатью и переносом в чек).</summary>
    public void PersistNow()
    {
        _saveTimer.Stop();
        if (!_dirty)
            return;
        try
        {
            _deps.Store.Save(_doc);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Смета: файл занят, правка будет сохранена позже ({ex.Message}).", "WARNING");
            _saveTimer.Start();
        }
    }

    // ───────────────────────────── позиции ─────────────────────────────

    private void FillSuggestions()
    {
        _suggest.Items.Clear();
        _hint.IsVisible = false;
        var q = (_add.Text ?? "").Trim();
        if (q.Length == 0)
        {
            _suggest.IsVisible = false;
            return;
        }
        foreach (var p in _deps.Catalog.Search(q, 8))
            _suggest.Items.Add(new ListBoxItem { Tag = p, MinHeight = 44, Content = $"{p.Name} · {MoneyText.Som(EstimateRules.PriceFor(p, _doc.Level))}" + (string.IsNullOrWhiteSpace(p.Unit) ? "" : " / " + p.Unit) });
        _suggest.IsVisible = _suggest.ItemCount > 0;
        if (_suggest.ItemCount > 0)
            _suggest.SelectedIndex = 0;
    }

    private void AddKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && _suggest.IsVisible && _suggest.ItemCount > 0)
        {
            e.Handled = true;
            _suggest.SelectedIndex = Math.Clamp(_suggest.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _suggest.ItemCount - 1);
            return;
        }
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        AddFromBox();
    }

    /// <summary>Enter или сканер: выбранная подсказка, иначе единственный найденный (точный штрихкод). false — ничего не найдено.</summary>
    public bool AddFromBox()
    {
        var text = (_add.Text ?? "").Trim();
        if (text.Length == 0)
            return false;
        var info = _suggest.IsVisible ? (_suggest.SelectedItem as ListBoxItem)?.Tag as EstimateCatalogInfo : null;
        if (info is null && _deps.Catalog.Search(text, 2) is { Count: 1 } one)
            info = one[0];
        if (info is null)
        {
            FillSuggestions();
            _hint.IsVisible = true;
            _hint.Text = EstimateUi.T("Такого товара нет в каталоге. Для работы или доставки нажмите «＋ Своя позиция».", "Мындай товар каталогто жок. Иш же жеткирүү үчүн «＋ Өз позициясы» басыңыз.", "No such product in the catalog. For work or delivery press “＋ Custom line”.", "Katalogda böyle bir ürün yok. İş veya teslimat için «＋ Özel kalem»e basın.", "Katalogda bunday tovar yo'q. Ish yoki yetkazib berish uchun «＋ O'z pozitsiyasi» ni bosing.");
            return false;
        }
        AddProduct(info);
        return true;
    }

    /// <summary>Товар из каталога: тот же товар повторно — плюс 1 к количеству строки; иначе новая строка (количество 1, цена по уровню сметы, закупка и остаток запоминаются).</summary>
    public EstimateLine AddProduct(EstimateCatalogInfo info)
    {
        var existing = _doc.Lines.FirstOrDefault(l => !l.IsCustom && l.ProductId == info.Id && l.CalcNote is null);
        EstimateLine line;
        if (existing is not null)
        {
            existing.Qty += 1m;
            line = existing;
        }
        else
        {
            line = new EstimateLine { Qty = 1m, Unit = "шт" };
            EstimateRules.FillFromCatalog(line, info, _doc.Level);
            _doc.Lines.Add(line);
        }
        _add.Text = "";
        _suggest.IsVisible = false;
        _hint.IsVisible = false;
        MarkDirty();
        RebuildRows();
        _add.Focus();
        return line;
    }

    /// <summary>Своя позиция (работа, доставка, услуга): без каталога, количество 1, цену вписывает продавец.</summary>
    public EstimateLine AddCustomLine()
    {
        var line = new EstimateLine { Unit = EstimateUi.T("усл.", "кызм.", "svc", "hzm.", "xizm."), Qty = 1m, ReservePercent = 0m };
        _doc.Lines.Add(line);
        MarkDirty();
        RebuildRows();
        Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>().LastOrDefault(t => t.Name?.StartsWith("LineName_", StringComparison.Ordinal) == true)?.Focus(), DispatcherPriority.Background);
        return line;
    }

    /// <summary>Строка из калькулятора материала.</summary>
    public void AddCalcLine(EstimateLine line)
    {
        _doc.Lines.Add(line);
        MarkDirty();
        RebuildRows();
    }

    public EstimateSection AddSection()
    {
        var presets = new[]
        {
            EstimateUi.T("Стены", "Дубалдар", "Walls", "Duvarlar", "Devorlar"), EstimateUi.T("Пол", "Пол", "Floor", "Zemin", "Pol"), EstimateUi.T("Потолок", "Шып", "Ceiling", "Tavan", "Shift"),
            EstimateUi.T("Работа", "Иш", "Work", "İş", "Ish"), EstimateUi.T("Доставка", "Жеткирүү", "Delivery", "Teslimat", "Yetkazib berish"),
        };
        var name = presets.FirstOrDefault(p => _doc.Sections.All(s => s.Name != p)) ?? EstimateUi.T("Раздел", "Бөлүм", "Section", "Bölüm", "Bo'lim") + " " + (_doc.Sections.Count + 1);
        var section = new EstimateSection { Name = name };
        _doc.Sections.Add(section);
        MarkDirty();
        RebuildRows();
        return section;
    }

    public void RemoveSection(string id)
    {
        foreach (var l in _doc.Lines.Where(l => l.SectionId == id))
            l.SectionId = null;
        _doc.Sections.RemoveAll(s => s.Id == id);
        MarkDirty();
        RebuildRows();
    }

    public void RemoveLine(string id)
    {
        _doc.Lines.RemoveAll(l => l.Id == id);
        MarkDirty();
        RebuildRows();
    }

    private void SetLevel(EstimateLevel level)
    {
        EstimateRules.SetLevel(_doc, level, _deps.Catalog.Find, _deps.Store.Now);
        MarkDirty();
        RebuildRows();
        Say(EstimateUi.T("Цены пересчитаны по уровню: ", "Баалар деңгээл боюнча кайра эсептелди: ", "Prices recalculated for the level: ", "Fiyatlar düzeye göre yeniden hesaplandı: ", "Narxlar daraja bo'yicha qayta hisoblandi: ") + (string)_level.SelectedItem!);
    }

    /// <summary>Установить уровень цены (для стенда).</summary>
    public void SelectLevel(EstimateLevel level) => _level.SelectedIndex = (int)level;

    // ───────────────────────────── клиент ─────────────────────────────

    private async Task FillClientSuggestionsAsync()
    {
        var q = (_client.Text ?? "").Trim();
        if (q.Length < 2)
        {
            _clientSuggest.IsVisible = false;
            return;
        }
        try
        {
            _buyers ??= await _deps.Buyers().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смета: список клиентов не получен ({ex.Message}) — клиента можно вписать вручную.", "WARNING");
            _buyers = new();
        }
        var digits = new string(q.Where(char.IsDigit).ToArray());
        var found = _buyers.Where(b => SearchMatch.Matches(q, b.Name, b.Company) || (digits.Length >= 3 && new string(b.Phone.Where(char.IsDigit).ToArray()).Contains(digits))).Take(5).ToList();
        _clientSuggest.Items.Clear();
        foreach (var b in found)
            _clientSuggest.Items.Add(new ListBoxItem { Tag = b, MinHeight = 44, Content = string.Join(" · ", new[] { b.Company.Length > 0 ? $"{b.Company} ({b.Name})" : b.Name, b.Phone }.Where(x => !string.IsNullOrWhiteSpace(x))) });
        _clientSuggest.IsVisible = found.Count > 0;
    }

    private void PickBuyer(ClientDocsUi.Buyer b)
    {
        _filling = true;
        _doc.ClientId = b.Id;
        _doc.ClientName = b.Company.Length > 0 ? b.Company : b.Name;
        _doc.Phone = b.Phone;
        if (string.IsNullOrWhiteSpace(_doc.Address))
            _doc.Address = b.Address;
        _client.Text = _doc.ClientName;
        _phone.Text = _doc.Phone;
        _address.Text = _doc.Address;
        _filling = false;
        _clientSuggest.IsVisible = false;
        MarkDirty();
    }

    // ───────────────────────────── действия ─────────────────────────────

    private async Task OpenCalcAsync()
    {
        var dlg = new EstimateCalcDialog(_deps, _doc, line => { AddCalcLine(line); return true; });
        await dlg.ShowDialog<bool>(this).ConfigureAwait(true);
    }

    private async Task OpenPrintAsync()
    {
        PersistNow();
        var dlg = new EstimatePrintWindow(_deps, _doc);
        await dlg.ShowDialog<bool>(this).ConfigureAwait(true);
        MarkSent();
    }

    private void MarkSent()
    {
        if (_doc.Status != EstimateStatus.Draft)
            return;
        _doc.Status = EstimateStatus.Sent;
        _dirty = true;
        PersistNow();
        UpdateTotals();
    }

    /// <summary>«Скопировать текстом»: готовый текст для WhatsApp/Telegram — в буфер обмена. Закупки и маржи в тексте нет.</summary>
    public async Task<string> CopyTextAsync()
    {
        var text = EstimateText.Message(_doc, UserPreferences.Instance.StoreName);
        try
        {
            if (_deps.CopyText is { } copy)
                await copy(text).ConfigureAwait(true);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смета: текст не скопирован ({ex.Message}).", "WARNING");
            Say(EstimateUi.T("Не удалось скопировать текст.", "Текстти көчүрүү мүмкүн болгон жок.", "Could not copy the text.", "Metin kopyalanamadı.", "Matnni nusxalab bo'lmadi."));
            return text;
        }
        MarkSent();
        Say(EstimateUi.T("Текст сметы скопирован — вставьте его в WhatsApp или Telegram.", "Смета тексти көчүрүлдү — WhatsApp же Telegram'га коюңуз.", "The estimate text is copied — paste it into WhatsApp or Telegram.", "Keşif metni kopyalandı — WhatsApp veya Telegram'a yapıştırın.", "Smeta matni nusxalandi — WhatsApp yoki Telegram'ga qo'ying."));
        return text;
    }

    /// <summary>«Открыть в WhatsApp»: чат с клиентом (по телефону сметы) и готовый текст; отправляет сам продавец.</summary>
    public Task<string> OpenWhatsAppAsync()
    {
        var text = EstimateText.Message(_doc, UserPreferences.Instance.StoreName);
        _deps.OpenWhatsApp(_doc.Phone, text);
        MarkSent();
        Say(EstimateUi.T("Открыт WhatsApp с готовым текстом — нажмите «Отправить» сами.", "WhatsApp даяр текст менен ачылды — «Жөнөтүү» өзүңүз басыңыз.", "WhatsApp is open with the text ready — press Send yourself.", "WhatsApp hazır metinle açıldı — Gönder'e kendiniz basın.", "WhatsApp tayyor matn bilan ochildi — «Yuborish» ni o'zingiz bosing."));
        return Task.FromResult(text);
    }

    /// <summary>«Пересчитать цены»: цены, закупка и остаток из каталога; срок — снова 7 дней. Возвращает изменения.</summary>
    public List<EstimateRules.PriceChange> RefreshPrices()
    {
        var changes = EstimateRules.RefreshPrices(_doc, _deps.Catalog.Find, _deps.Store.Now);
        if (EstimateRules.IsExpired(_doc, _deps.Today()))
            _doc.ValidUntil = _deps.Today().AddDays(EstimateRules.DefaultValidDays);
        _filling = true;
        _until.Text = EstimateUi.Day(_doc.ValidUntil);
        _filling = false;
        MarkDirty();
        RebuildRows();
        var changed = changes.Count(c => !c.Missing);
        var missing = changes.Count(c => c.Missing);
        Say(changed == 0 && missing == 0
            ? EstimateUi.T("Цены не изменились.", "Баалар өзгөргөн жок.", "Prices have not changed.", "Fiyatlar değişmedi.", "Narxlar o'zgarmadi.")
            : EstimateUi.T($"Цены обновлены: изменилось позиций — {changed}", $"Баалар жаңыртылды: өзгөргөн позициялар — {changed}", $"Prices updated: lines changed — {changed}", $"Fiyatlar güncellendi: değişen kalem — {changed}", $"Narxlar yangilandi: o'zgargan pozitsiyalar — {changed}")
              + (missing > 0 ? EstimateUi.T($"; товара нет в каталоге — {missing}", $"; каталогто жок товар — {missing}", $"; not in the catalog — {missing}", $"; katalogda yok — {missing}", $"; katalogda yo'q — {missing}") : "") + ". "
              + string.Join("; ", changes.Where(c => !c.Missing).Take(3).Select(c => $"{c.Line.Name.Trim()}: {MoneyText.Amount(c.OldPrice)} → {MoneyText.Amount(c.NewPrice)}")));
        return changes;
    }

    /// <summary>«Добавить всё в чек» (только касса): по ТЕКУЩИМ ценам каталога. Если цена или остаток изменились с момента сметы — показываем что именно и спрашиваем. Нет остатка — решает
    /// существующее правило продажи без остатка (окно кассы при добавлении товара). Своя позиция — строка «Доп. услуга». Всё перенесено — статус «Принята».</summary>
    public async Task<bool> AddToCartAsync()
    {
        var till = _deps.Till;
        if (till is null)
            return false;
        PersistNow();
        var items = EstimateCartPlanner.Plan(_doc, _deps.Catalog.Find);
        if (items.Count == 0)
        {
            Say(EstimateUi.T("В смете нет позиций.", "Сметада позиция жок.", "The estimate has no lines.", "Keşifte kalem yok.", "Smetada pozitsiya yo'q."));
            return false;
        }
        var message = EstimateUi.T($"Добавить в чек позиций: {items.Count(i => !i.Missing)} по текущим ценам?", $"Чекке позиция кошосузбу: {items.Count(i => !i.Missing)} (азыркы баа боюнча)?", $"Add {items.Count(i => !i.Missing)} lines to the receipt at current prices?", $"{items.Count(i => !i.Missing)} kalem güncel fiyatlarla fişe eklensin mi?", $"{items.Count(i => !i.Missing)} ta pozitsiya hozirgi narxlar bo'yicha chekka qo'shilsinmi?");
        if (EstimateCartPlanner.HasChanges(items, _doc.Level))
            message += "\n\n" + EstimateUi.T("С момента составления сметы изменилось:", "Смета түзүлгөндөн бери өзгөрдү:", "Changed since the estimate was made:", "Keşif hazırlandığından beri değişenler:", "Smeta tuzilgandan beri o'zgargan:") + "\n" + EstimateCartPlanner.ChangesText(items, _doc.Level);
        if (_doc.DiscountPercent > 0m || _doc.Lines.Any(l => l.DiscountPercent > 0m))
            message += "\n\n" + EstimateUi.T("Скидки сметы в чек не переносятся — примените скидку в чеке.", "Сметанын арзандатуулары чекке өтпөйт — арзандатууну чекте колдонуңуз.", "Estimate discounts are not carried to the receipt — apply the discount in the receipt.", "Keşif indirimleri fişe aktarılmaz — indirimi fişte uygulayın.", "Smeta chegirmalari chekka o'tmaydi — chegirmani chekda qo'llang.");
        var ok = _deps.Confirm is { } confirm
            ? await confirm(message, EstimateUi.Title).ConfigureAwait(true)
            : await PosDialogs.ConfirmYesNoModalAsync(this, message, EstimateUi.Title, confirmText: EstimateUi.T("Добавить в чек", "Чекке кошуу", "Add to the receipt", "Fişe ekle", "Chekka qo'shu")).ConfigureAwait(true);
        if (!ok)
            return false;
        var added = 0;
        foreach (var item in items)
        {
            if (item.Missing)
                continue;
            var done = item.IsCustom
                ? till.AddCustomLine(item.Line.Name.Trim().Length == 0 ? EstimateUi.Title : item.Line.Name.Trim(), EstimateRules.LineSum(item.Line, _doc))
                : await till.AddProductAsync(item.Line.ProductId!, item.Qty).ConfigureAwait(true);
            if (done)
                added++;
        }
        if (added == items.Count)
        {
            _doc.Status = EstimateStatus.Accepted;
            _dirty = true;
            PersistNow();
            UpdateTotals();
            AddedToCart = true;
            Say(EstimateUi.T("Все позиции добавлены в чек. Статус сметы — «Принята».", "Бардык позициялар чекке кошулду. Смета статусу — «Кабыл алынды».", "All lines are in the receipt. The estimate status is “Accepted”.", "Tüm kalemler fişe eklendi. Keşif durumu — «Kabul edildi».", "Barcha pozitsiyalar chekka qo'shildi. Smeta holati — «Qabul qilindi»."));
            Close(true);
            return true;
        }
        Say(EstimateUi.T($"В чек добавлено {added} из {items.Count}. Остальное не добавлено (нет товара в каталоге или отказ). Статус сметы не менялся.", $"Чекке {added}/{items.Count} кошулду. Калганы кошулган жок (каталогто жок же баш тартуу). Смета статусу өзгөргөн жок.", $"{added} of {items.Count} lines added to the receipt. The rest were not added (not in the catalog or declined). The status is unchanged.", $"Fişe {added}/{items.Count} kalem eklendi. Kalanlar eklenmedi (katalogda yok veya vazgeçildi). Durum değişmedi.", $"Chekka {added}/{items.Count} pozitsiya qo'shildi. Qolganlari qo'shilmadi (katalogda yo'q yoki rad etildi). Holat o'zgarmadi."));
        return false;
    }
}
