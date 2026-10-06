using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, владелец: «возврат для поставщиков тоже добавь». Раздел программы владельца «Возвраты поставщикам»:
/// список возвратов товара поставщику (сервер NurCRM, GET api/main/suppliers/returns/ — те же, что на сайте) с периодом,
/// поиском и итогом; «Новый возврат» — поставщик, по приходу (видно, сколько ещё можно вернуть) или со склада, количество,
/// причина (брак, излишек, ошибка поставки, просрочка, другое) и что делать с деньгами: вернуть в кассу, уменьшить долг
/// поставщику или только списать со склада (POST api/main/suppliers/{id}/returns/ — тот же запрос, что у сайта).
/// Склад и долг поставщику меняет сервер.</summary>
public sealed class SupplierReturnsWindow : Window, IOwnerSection
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly NurMarketApiClient _api;
    private readonly Grid _root = new() { Margin = new Thickness(24, 16, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold };
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBox _search;
    private readonly TextBlock _summary = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly HashSet<string> _expanded = new();
    private string _period = "month";
    private int _loadGen;

    internal static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    internal static string ReasonName(string reason) => reason switch
    {
        "defect" => T("Брак", "Бузук", "Defect", "Kusurlu", "Brak"),
        "surplus" => T("Излишек", "Ашыкча", "Surplus", "Fazla", "Ortiqcha"),
        "wrong_item" => T("Ошибка поставки", "Жеткирүүдөгү ката", "Wrong item delivered", "Yanlış teslimat", "Yetkazishdagi xato"),
        "expired" => T("Просрочка", "Мөөнөтү өткөн", "Expired", "Süresi geçmiş", "Muddati o'tgan"),
        _ => T("Другое", "Башка", "Other", "Diğer", "Boshqa"),
    };

    internal static string CompensationName(string compensation) => compensation switch
    {
        "cash" => T("Деньги вернули в кассу", "Акча кассага кайтарылды", "Money returned to the till", "Para kasaya iade edildi", "Pul kassaga qaytarildi"),
        "debt_offset" => T("Уменьшен долг поставщику", "Жеткирүүчүгө карыз азайды", "Debt to the supplier reduced", "Tedarikçiye borç azaldı", "Yetkazib beruvchiga qarz kamaydi"),
        _ => T("Только списано со склада", "Кампадан гана чыгарылды", "Written off the stock only", "Yalnızca stoktan düşüldü", "Faqat ombordan chiqarildi"),
    };

    public SupplierReturnsWindow()
    {
        _api = App.GetRequiredService<NurMarketApiClient>();
        Title = T("Возвраты поставщикам", "Жеткирүүчүлөргө кайтаруулар", "Returns to suppliers", "Tedarikçiye iadeler", "Yetkazib beruvchilarga qaytarishlar");
        Width = 1100;
        Height = 800;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        _title.Text = Title;
        Use(_title, TextBlock.ForegroundProperty, "BrushText");
        var hint = new TextBlock
        {
            Text = T("Брак, излишек, ошибка поставки — товар уходит поставщику, остаток на складе уменьшается, деньги — в кассу или в счёт долга поставщику. Возвраты общие с сайтом NurCRM.",
                "Бузук, ашыкча, жеткирүүдөгү ката — товар жеткирүүчүгө кетет, кампадагы калдык азаят, акча — кассага же жеткирүүчүнүн карызынын эсебине. Кайтаруулар NurCRM сайты менен жалпы.",
                "Defects, surplus, wrong deliveries — goods go back to the supplier, stock goes down, money goes to the till or against the debt to the supplier. Returns are shared with the NurCRM website.",
                "Kusur, fazlalık, yanlış teslimat — ürün tedarikçiye gider, stok azalır, para kasaya ya da tedarikçi borcuna sayılır. İadeler NurCRM sitesiyle ortaktır.",
                "Brak, ortiqcha, yetkazishdagi xato — mahsulot yetkazib beruvchiga qaytadi, ombordagi qoldiq kamayadi, pul — kassaga yoki yetkazib beruvchi qarzi hisobiga. Qaytarishlar NurCRM sayti bilan umumiy."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        BuildPeriods();
        tools.Children.Add(_periods);
        _search = UiKit.Input(this, T("Поиск: поставщик, товар, комментарий…", "Издөө: жеткирүүчү, товар, комментарий…", "Search: supplier, product, comment…", "Ara: tedarikçi, ürün, yorum…", "Qidirish: yetkazib beruvchi, mahsulot, izoh…"), 40);
        _search.Margin = new Thickness(12, 0, 0, 0);
        var searchTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        searchTimer.Tick += async (_, _) =>
        {
            searchTimer.Stop();
            await LoadAsync().ConfigureAwait(true);
        };
        _search.TextChanged += (_, _) =>
        {
            searchTimer.Stop();
            searchTimer.Start();
        };
        Grid.SetColumn(_search, 1);
        tools.Children.Add(_search);
        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Margin = new Thickness(8, 0, 0, 0);
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Grid.SetColumn(refresh, 2);
        tools.Children.Add(refresh);
        var create = UiKit.Primary(this, T("＋ Новый возврат", "＋ Жаңы кайтаруу", "＋ New return", "＋ Yeni iade", "＋ Yangi qaytarish"));
        create.Margin = new Thickness(8, 0, 0, 0);
        create.Click += async (_, _) =>
        {
            var dialog = new SupplierReturnDialog();
            if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
                await LoadAsync().ConfigureAwait(true);
        };
        Grid.SetColumn(create, 3);
        tools.Children.Add(create);
        tools.Classes.Add("no-reflow");

        Use(_summary, TextBlock.ForegroundProperty, "BrushText");
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(hint, 1);
        Grid.SetRow(tools, 2);
        Grid.SetRow(_summary, 3);
        Grid.SetRow(scroll, 4);
        _root.Children.Add(_title);
        _root.Children.Add(hint);
        _root.Children.Add(tools);
        _root.Children.Add(_summary);
        _root.Children.Add(scroll);
        Content = _root;

        // Узкий экран (телефон): поиск и кнопки — строкой ниже периода.
        NarrowLayout.Attach(this, 900, narrow =>
        {
            tools.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,Auto,Auto" : "Auto,*,Auto,Auto");
            tools.RowDefinitions = narrow ? new RowDefinitions("Auto,8,Auto") : new RowDefinitions();
            Grid.SetColumnSpan(_periods, narrow ? 3 : 1);
            Grid.SetRow(_search, narrow ? 2 : 0);
            Grid.SetColumn(_search, narrow ? 0 : 1);
            _search.Margin = new Thickness(narrow ? 0 : 12, 0, 0, 0);
            Grid.SetRow(refresh, narrow ? 2 : 0);
            Grid.SetColumn(refresh, narrow ? 1 : 2);
            Grid.SetRow(create, narrow ? 2 : 0);
            Grid.SetColumn(create, narrow ? 2 : 3);
        });

        _summary.Text = T("Загружаю возвраты…", "Кайтаруулар жүктөлүүдө…", "Loading returns…", "İadeler yükleniyor…", "Qaytarishlar yuklanmoqda…");
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    public void AsOwnerSection()
    {
        _title.IsVisible = false;
        _root.Margin = OwnerSectionLayout.Margin;
    }

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("today", T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun")), ("week", T("Неделя", "Апта", "Week", "Hafta", "Hafta")),
                     ("month", T("Месяц", "Ай", "Month", "Ay", "Oy")), ("all", T("Всё время", "Бардык убакыт", "All time", "Tüm zamanlar", "Butun davr")),
                 })
        {
            var chip = UiKit.Chip(this, text, key == _period);
            chip.MinHeight = 36;
            chip.Click += async (_, _) =>
            {
                _period = key;
                BuildPeriods();
                await LoadAsync().ConfigureAwait(true);
            };
            _periods.Children.Add(chip);
        }
    }

    private async Task LoadAsync()
    {
        var gen = ++_loadGen;
        var query = new Dictionary<string, string> { ["page"] = "1", ["limit"] = "200" };
        var from = _period switch
        {
            "today" => DateTime.Today,
            "week" => DateTime.Today.AddDays(-6),
            "month" => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
            _ => (DateTime?)null,
        };
        if (from is { } f)
        {
            query["date_from"] = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            query["date_to"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if ((_search.Text ?? "").Trim() is { Length: > 0 } search)
            query["search"] = search;
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, "api/main/suppliers/returns/", null, query, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            if (gen != _loadGen)
                return;
            var rows = data.ValueKind == JsonValueKind.Array ? data : data.TryGetProperty("results", out var r) ? r : default;
            var count = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : rows.ValueKind == JsonValueKind.Array ? rows.GetArrayLength() : 0;
            var total = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("meta", out var meta) ? Num(meta, "total_amount") : 0;
            _list.Children.Clear();
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            {
                _summary.Text = T("Возвратов поставщикам за этот период нет.", "Бул мезгилде жеткирүүчүлөргө кайтаруу жок.", "No returns to suppliers for this period.",
                    "Bu dönemde tedarikçiye iade yok.", "Bu davrda yetkazib beruvchilarga qaytarish yo'q.");
                return;
            }
            if (total <= 0)
                total = rows.EnumerateArray().Sum(x => Num(x, "total_amount"));
            _summary.Text = T($"Возвратов: {count} на {Money(total)}", $"Кайтаруулар: {count}, суммасы {Money(total)}", $"Returns: {count}, total {Money(total)}",
                $"İadeler: {count}, toplam {Money(total)}", $"Qaytarishlar: {count}, jami {Money(total)}");
            foreach (var row in rows.EnumerateArray())
                _list.Children.Add(BuildCard(row));
            PosLogger.Log($"Возвраты поставщикам: {count} шт.", "INFO");
        }
        catch (Exception ex)
        {
            if (gen != _loadGen)
                return;
            _summary.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + ServerTelegramBotApi.DescribeFields(ex);
            PosLogger.Log($"Возвраты поставщикам: не загружены ({ex.Message}).", "WARNING");
        }
    }

    private Control BuildCard(JsonElement row)
    {
        var id = Str(row, "id");
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12), Cursor = new Cursor(StandardCursorType.Hand) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var body = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var who = new StackPanel { Spacing = 2 };
        var name = new TextBlock { Text = Str(row, "supplier_name") is { Length: > 0 } s ? s : "—", FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        who.Children.Add(name);
        DateTime.TryParse(Str(row, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at);
        var meta = new TextBlock
        {
            Text = string.Join(" · ", new[]
            {
                at == default ? "" : at.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Ru),
                ReasonName(Str(row, "reason")),
                CompensationName(Str(row, "compensation") is { Length: > 0 } comp ? comp : Str(row, "compensation_type")),
                Str(row, "created_by_name"),
            }.Where(x => x.Length > 0)),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        Use(meta, TextBlock.ForegroundProperty, "BrushTextSoft");
        who.Children.Add(meta);
        if (Str(row, "comment") is { Length: > 0 } comment)
        {
            var note = new TextBlock { Text = "«" + comment + "»", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic };
            Use(note, TextBlock.ForegroundProperty, "BrushTextSoft");
            who.Children.Add(note);
        }
        head.Children.Add(who);
        var sum = new TextBlock { Text = Money(Num(row, "total_amount")), FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        Use(sum, TextBlock.ForegroundProperty, "BrushText");
        Grid.SetColumn(sum, 1);
        head.Children.Add(sum);
        body.Children.Add(head);

        var lines = row.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items
            : row.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array ? l : default;
        var expanded = _expanded.Contains(id);
        var more = new TextBlock
        {
            Text = expanded ? T("▴ Скрыть товары", "▴ Товарларды жашыруу", "▴ Hide products", "▴ Ürünleri gizle", "▴ Mahsulotlarni yashirish")
                : T($"▾ Товары ({(lines.ValueKind == JsonValueKind.Array ? lines.GetArrayLength() : 0)})", $"▾ Товарлар ({(lines.ValueKind == JsonValueKind.Array ? lines.GetArrayLength() : 0)})",
                    $"▾ Products ({(lines.ValueKind == JsonValueKind.Array ? lines.GetArrayLength() : 0)})", $"▾ Ürünler ({(lines.ValueKind == JsonValueKind.Array ? lines.GetArrayLength() : 0)})",
                    $"▾ Mahsulotlar ({(lines.ValueKind == JsonValueKind.Array ? lines.GetArrayLength() : 0)})"),
            FontSize = 12.5,
        };
        Use(more, TextBlock.ForegroundProperty, "BrushAccentStrong");
        body.Children.Add(more);
        if (expanded && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                var qty = Num(line, "qty");
                var price = Num(line, "purchase_price");
                var text = new TextBlock
                {
                    Text = $"{Str(line, "product_name")} — {qty.ToString("0.###", Ru)} {Str(line, "unit")} × {Money(price)} = {Money(Num(line, "line_total") is > 0 and var lt ? lt : qty * price)}",
                    FontSize = 13, TextWrapping = TextWrapping.Wrap,
                };
                Use(text, TextBlock.ForegroundProperty, "BrushText");
                body.Children.Add(text);
            }
        }
        card.Child = body;
        card.PointerPressed += (_, _) =>
        {
            if (!_expanded.Remove(id))
                _expanded.Add(id);
            var index = _list.Children.IndexOf(card);
            if (index >= 0)
                _list.Children[index] = BuildCard(row);
        };
        return card;
    }

    internal static string Money(double v) => v.ToString("N2", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

    internal static double Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>«Новый возврат поставщику»: поставщик → приход или склад → количество по строкам → причина, деньги, комментарий.
/// Результат ShowDialog — true, если возврат оформлен на сервере.</summary>
public sealed class SupplierReturnDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private sealed class Line
    {
        public string ProductId = "";
        public string Name = "";
        public string Unit = "";
        public double Price;
        public double Max;
        public string? ReceiptItemId;
        public TextBox Qty = null!;
    }

    private readonly NurMarketApiClient _api;
    private readonly ComboBox _supplier = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
    private readonly ComboBox _receipt = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40, IsVisible = false };
    private readonly ComboBox _cashbox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
    private readonly StackPanel _sourceChips = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly WrapPanel _reasonChips = new();
    private readonly StackPanel _compChips = new() { Spacing = 6 };
    private readonly StackPanel _lines = new() { Spacing = 6 };
    private readonly TextBox _comment = new() { MinHeight = 38, Watermark = T("Комментарий (необязательно)", "Комментарий (милдеттүү эмес)", "Comment (optional)", "Yorum (isteğe bağlı)", "Izoh (ixtiyoriy)") };
    private readonly TextBlock _total = new() { FontSize = 16, FontWeight = FontWeight.Bold };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save;
    private readonly List<Line> _rows = new();
    private List<(string Id, string Name)> _suppliers = new();
    private List<(string Id, string Title)> _receipts = new();
    private List<(string Id, string Name)> _cashboxes = new();
    private bool _byReceipt = true;
    private string _reason = "defect";
    private string _compensation = "cash";
    private Panel? _cashboxPanel;

    public SupplierReturnDialog()
    {
        _api = App.GetRequiredService<NurMarketApiClient>();
        Title = T("Новый возврат поставщику", "Жеткирүүчүгө жаңы кайтаруу", "New return to supplier", "Tedarikçiye yeni iade", "Yetkazib beruvchiga yangi qaytarish");
        Width = 720;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        var title = new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(title);

        panel.Children.Add(Label(T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi")));
        _supplier.SelectionChanged += async (_, _) => await OnSupplierChangedAsync().ConfigureAwait(true);
        panel.Children.Add(_supplier);

        panel.Children.Add(Label(T("Что возвращаем", "Эмнени кайтарабыз", "What is returned", "Ne iade ediliyor", "Nima qaytariladi")));
        panel.Children.Add(_sourceChips);
        _receipt.SelectionChanged += async (_, _) => await LoadReceiptLinesAsync().ConfigureAwait(true);
        panel.Children.Add(_receipt);
        panel.Children.Add(_lines);

        panel.Children.Add(Label(T("Причина", "Себеп", "Reason", "Neden", "Sabab")));
        panel.Children.Add(_reasonChips);
        panel.Children.Add(Label(T("Деньги", "Акча", "Money", "Para", "Pul")));
        panel.Children.Add(_compChips);
        var cashboxPanel = new StackPanel { Spacing = 4 };
        cashboxPanel.Children.Add(Label(T("Касса, куда придут деньги", "Акча келе турган касса", "Till that receives the money", "Paranın gireceği kasa", "Pul tushadigan kassa")));
        cashboxPanel.Children.Add(_cashbox);
        _cashboxPanel = cashboxPanel;
        panel.Children.Add(cashboxPanel);
        panel.Children.Add(_comment);

        Use(_total, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(_total);
        Use(_error, TextBlock.ForegroundProperty, "BrushDanger");
        panel.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        _save = UiKit.Primary(this, T("Оформить возврат", "Кайтарууну тариздөө", "Make the return", "İadeyi kaydet", "Qaytarishni rasmiylashtirish"));
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        panel.Children.Add(buttons);

        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        BuildChips();
        UpdateTotal();
        Opened += async (_, _) => await LoadSuppliersAsync().ConfigureAwait(true);
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void BuildChips()
    {
        _sourceChips.Children.Clear();
        foreach (var (byReceipt, text) in new[]
                 {
                     (true, T("По приходу", "Кириш боюнча", "From a delivery", "Girişten", "Kirim bo'yicha")),
                     (false, T("Со склада", "Кампадан", "From stock", "Stoktan", "Ombordan")),
                 })
        {
            var chip = UiKit.Chip(this, text, byReceipt == _byReceipt);
            chip.MinHeight = 34;
            chip.Click += async (_, _) =>
            {
                _byReceipt = byReceipt;
                BuildChips();
                await OnSupplierChangedAsync().ConfigureAwait(true);
            };
            _sourceChips.Children.Add(chip);
        }
        _reasonChips.Children.Clear();
        foreach (var reason in new[] { "defect", "surplus", "wrong_item", "expired", "other" })
        {
            var chip = UiKit.Chip(this, SupplierReturnsWindow.ReasonName(reason), reason == _reason);
            chip.MinHeight = 34;
            chip.Margin = new Thickness(0, 0, 8, 8);
            chip.Click += (_, _) =>
            {
                _reason = reason;
                BuildChips();
            };
            _reasonChips.Children.Add(chip);
        }
        _compChips.Children.Clear();
        foreach (var (comp, text, hint) in new[]
                 {
                     ("cash", T("Вернуть деньги в кассу", "Акчаны кассага кайтаруу", "Return money to the till", "Parayı kasaya iade et", "Pulni kassaga qaytarish"),
                         T("Остаток уменьшится, в кассу придёт сумма возврата по закупочной цене.", "Калдык азаят, кассага сатып алуу баасы боюнча сумма келет.",
                             "Stock goes down, the till receives the return amount at the purchase price.", "Stok azalır, kasaya alış fiyatından iade tutarı girer.",
                             "Qoldiq kamayadi, kassaga xarid narxi bo'yicha qaytarish summasi tushadi.")),
                     ("debt_offset", T("Уменьшить долг поставщику", "Жеткирүүчүгө карызды азайтуу", "Reduce the debt to the supplier", "Tedarikçiye borcu azalt", "Yetkazib beruvchiga qarzni kamaytirish"),
                         T("Остаток уменьшится, долг перед поставщиком снизится на сумму возврата.", "Калдык азаят, жеткирүүчүгө карыз кайтаруу суммасына азаят.",
                             "Stock goes down, the debt to the supplier drops by the return amount.", "Stok azalır, tedarikçiye borç iade tutarı kadar düşer.",
                             "Qoldiq kamayadi, yetkazib beruvchiga qarz qaytarish summasiga kamayadi.")),
                     ("none", T("Только списать со склада", "Кампадан гана чыгаруу", "Write off the stock only", "Yalnızca stoktan düş", "Faqat ombordan chiqarish"),
                         T("Товар уйдёт со склада без движения денег — если поставщик заменит товар.", "Товар акча кыймылысыз кампадан чыгат — жеткирүүчү алмаштырса.",
                             "Goods leave the stock without money moving — if the supplier replaces them.", "Ürün para hareketi olmadan stoktan çıkar — tedarikçi değiştirirse.",
                             "Mahsulot pul harakatisiz ombordan chiqadi — yetkazib beruvchi almashtirsa.")),
                 })
        {
            var chip = UiKit.Chip(this, text, comp == _compensation);
            chip.MinHeight = 34;
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            chip.Click += (_, _) =>
            {
                _compensation = comp;
                BuildChips();
            };
            _compChips.Children.Add(chip);
            if (comp == _compensation)
            {
                var h = new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 4) };
                Use(h, TextBlock.ForegroundProperty, "BrushTextSoft");
                _compChips.Children.Add(h);
            }
        }
        if (_cashboxPanel is not null)
            _cashboxPanel.IsVisible = _compensation == "cash" && _cashboxes.Count > 0;
        _receipt.IsVisible = _byReceipt;
    }

    private static List<JsonElement> Rows(JsonElement data) =>
        data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList()
        : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().ToList()
        : new List<JsonElement>();

    private async Task LoadSuppliersAsync()
    {
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, "api/main/clients/", null,
                new Dictionary<string, string> { ["type"] = "suppliers", ["page_size"] = "500" }, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            _suppliers = Rows(data).Select(x => (SupplierReturnsWindow.Str(x, "id"), SupplierReturnsWindow.Str(x, "full_name") is { Length: > 0 } n ? n : SupplierReturnsWindow.Str(x, "llc")))
                .Where(x => x.Item1.Length > 0).OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
            _supplier.ItemsSource = _suppliers.Select(x => x.Name).ToList();
            _supplier.PlaceholderText = _suppliers.Count == 0
                ? T("Поставщиков нет — добавьте их на сайте NurCRM", "Жеткирүүчүлөр жок — NurCRM сайтында кошуңуз", "No suppliers — add them on the NurCRM website", "Tedarikçi yok — NurCRM sitesinde ekleyin", "Yetkazib beruvchilar yo'q — NurCRM saytida qo'shing")
                : T("Выберите поставщика", "Жеткирүүчүнү тандаңыз", "Choose a supplier", "Tedarikçi seçin", "Yetkazib beruvchini tanlang");
        }
        catch (Exception ex)
        {
            ShowError(T("Поставщики не загрузились: ", "Жеткирүүчүлөр жүктөлгөн жок: ", "Suppliers didn't load: ", "Tedarikçiler yüklenmedi: ", "Yetkazib beruvchilar yuklanmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
        }
        try
        {
            var data = await _api.ConstructionCashboxesListAsync().ConfigureAwait(true);
            _cashboxes = Rows(data).Select(x => (SupplierReturnsWindow.Str(x, "id"), SupplierReturnsWindow.Str(x, "name") is { Length: > 0 } n ? n : SupplierReturnsWindow.Str(x, "title")))
                .Where(x => x.Item1.Length > 0).ToList();
            _cashbox.ItemsSource = _cashboxes.Select(x => x.Name.Length > 0 ? x.Name : x.Id[..8]).ToList();
            var preferred = _cashboxes.FindIndex(x => x.Id == UserPreferences.Instance.PreferredCashboxId);
            _cashbox.SelectedIndex = _cashboxes.Count == 0 ? -1 : Math.Max(0, preferred);
            BuildChips();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат поставщику: кассы не получены ({ex.Message}) — сервер возьмёт свою.", "WARNING");
        }
    }

    private string? SupplierId => _supplier.SelectedIndex >= 0 && _supplier.SelectedIndex < _suppliers.Count ? _suppliers[_supplier.SelectedIndex].Id : null;

    private async Task OnSupplierChangedAsync()
    {
        _rows.Clear();
        _lines.Children.Clear();
        UpdateTotal();
        if (SupplierId is not { } supplierId)
            return;
        HideError();
        try
        {
            if (_byReceipt)
            {
                var data = await _api.RequestAsync(HttpMethod.Get, "api/main/suppliers/receipts/", null,
                    new Dictionary<string, string> { ["supplier_id"] = supplierId, ["limit"] = "50" }, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                _receipts = Rows(data).Where(x => SupplierReturnsWindow.Str(x, "supplier_id") is var s && (s.Length == 0 || s == supplierId))
                    .Select(x =>
                    {
                        DateTime.TryParse(SupplierReturnsWindow.Str(x, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at);
                        var returned = SupplierReturnsWindow.Num(x, "returned_amount");
                        return (SupplierReturnsWindow.Str(x, "id"),
                            $"{(at == default ? "" : at.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))} · {SupplierReturnsWindow.Money(SupplierReturnsWindow.Num(x, "total_amount"))}"
                            + (returned > 0 ? T($" · уже возвращено {SupplierReturnsWindow.Money(returned)}", $" · кайтарылган {SupplierReturnsWindow.Money(returned)}",
                                $" · already returned {SupplierReturnsWindow.Money(returned)}", $" · iade edilen {SupplierReturnsWindow.Money(returned)}", $" · qaytarilgan {SupplierReturnsWindow.Money(returned)}") : ""));
                    }).ToList();
                _receipt.ItemsSource = _receipts.Select(x => x.Title).ToList();
                _receipt.PlaceholderText = _receipts.Count == 0
                    ? T("Приходов от этого поставщика нет — выберите «Со склада»", "Бул жеткирүүчүдөн кириш жок — «Кампадан» тандаңыз", "No deliveries from this supplier — choose “From stock”", "Bu tedarikçiden giriş yok — «Stoktan» seçin", "Bu yetkazib beruvchidan kirim yo'q — «Ombordan»ni tanlang")
                    : T("Выберите приход", "Киришти тандаңыз", "Choose a delivery", "Girişi seçin", "Kirimni tanlang");
                _receipt.SelectedIndex = -1;
            }
            else
            {
                var data = await _api.RequestAsync(HttpMethod.Get, $"api/main/suppliers/{supplierId}/products/", null, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
                foreach (var p in Rows(data))
                    AddLine(SupplierReturnsWindow.Str(p, "id"), SupplierReturnsWindow.Str(p, "name"), SupplierReturnsWindow.Str(p, "unit"), SupplierReturnsWindow.Num(p, "purchase_price"),
                        SupplierReturnsWindow.Num(p, "quantity"), null, T("на складе", "кампада", "in stock", "stokta", "omborda"));
                if (_rows.Count == 0)
                    _lines.Children.Add(Hint(T("У этого поставщика нет товаров на складе.", "Бул жеткирүүчүнүн кампада товары жок.", "This supplier has no products in stock.", "Bu tedarikçinin stokta ürünü yok.", "Bu yetkazib beruvchining omborda mahsuloti yo'q.")));
            }
        }
        catch (Exception ex)
        {
            ShowError(T("Не загрузилось: ", "Жүктөлгөн жок: ", "Didn't load: ", "Yüklenmedi: ", "Yuklanmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
        }
    }

    private async Task LoadReceiptLinesAsync()
    {
        _rows.Clear();
        _lines.Children.Clear();
        UpdateTotal();
        if (_receipt.SelectedIndex < 0 || _receipt.SelectedIndex >= _receipts.Count)
            return;
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, $"api/main/suppliers/receipts/{_receipts[_receipt.SelectedIndex].Id}/", null, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            var items = data.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Array ? i : data.TryGetProperty("lines", out var l) ? l : default;
            if (items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                    AddLine(SupplierReturnsWindow.Str(it, "product_id") is { Length: > 0 } pid ? pid : SupplierReturnsWindow.Str(it, "product"), SupplierReturnsWindow.Str(it, "product_name"),
                        SupplierReturnsWindow.Str(it, "unit"), SupplierReturnsWindow.Num(it, "purchase_price"),
                        it.TryGetProperty("returnable_qty", out _) ? SupplierReturnsWindow.Num(it, "returnable_qty") : SupplierReturnsWindow.Num(it, "qty"),
                        SupplierReturnsWindow.Str(it, "id"), T("можно вернуть", "кайтарса болот", "returnable", "iade edilebilir", "qaytarish mumkin"));
            if (_rows.Count == 0)
                _lines.Children.Add(Hint(T("В приходе нет товаров.", "Кириште товар жок.", "The delivery has no products.", "Girişte ürün yok.", "Kirimda mahsulot yo'q.")));
        }
        catch (Exception ex)
        {
            ShowError(T("Приход не загрузился: ", "Кириш жүктөлгөн жок: ", "The delivery didn't load: ", "Giriş yüklenmedi: ", "Kirim yuklanmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
        }
    }

    private void AddLine(string productId, string name, string unit, double price, double max, string? receiptItemId, string maxLabel)
    {
        if (productId.Length == 0)
            return;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var info = new StackPanel { Spacing = 1 };
        var title = new TextBlock { Text = name.Length > 0 ? name : "—", FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var unitText = unit.Length > 0 ? unit : T("шт", "даана", "pcs", "adet", "dona");
        var meta = Hint($"{maxLabel}: {max.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"))} {unitText} · {SupplierReturnsWindow.Money(price)}");
        info.Children.Add(title);
        info.Children.Add(meta);
        row.Children.Add(info);
        var qty = new TextBox { Width = 110, MinHeight = 36, Watermark = "0", IsEnabled = max > 0, HorizontalContentAlignment = HorizontalAlignment.Right };
        qty.TextChanged += (_, _) => UpdateTotal();
        Grid.SetColumn(qty, 1);
        row.Children.Add(qty);
        _lines.Children.Add(row);
        _rows.Add(new Line { ProductId = productId, Name = name, Unit = unitText, Price = price, Max = max, ReceiptItemId = receiptItemId, Qty = qty });
    }

    private static double ParseQty(string? text) =>
        double.TryParse((text ?? "").Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void UpdateTotal()
    {
        var total = _rows.Sum(r => Math.Max(0, ParseQty(r.Qty.Text)) * r.Price);
        _total.Text = T("Сумма возврата: ", "Кайтаруунун суммасы: ", "Return amount: ", "İade tutarı: ", "Qaytarish summasi: ") + SupplierReturnsWindow.Money(total);
    }

    private async Task SaveAsync()
    {
        HideError();
        if (SupplierId is not { } supplierId)
        {
            ShowError(T("Выберите поставщика.", "Жеткирүүчүнү тандаңыз.", "Choose a supplier.", "Tedarikçi seçin.", "Yetkazib beruvchini tanlang."));
            return;
        }
        var items = new List<Dictionary<string, object?>>();
        foreach (var r in _rows)
        {
            var qty = ParseQty(r.Qty.Text);
            if (qty == 0 && string.IsNullOrWhiteSpace(r.Qty.Text))
                continue;
            var piece = r.Unit is "шт" or "даана" or "pcs" or "adet" or "dona" or "";
            string? problem = qty <= 0 ? T("укажите количество больше 0", "0дөн көп санды жазыңыз", "enter a quantity above 0", "0'dan büyük adet girin", "0 dan katta miqdor kiriting")
                : piece && Math.Abs(qty - Math.Round(qty)) > 0.0001 ? T("для штучного товара — целое число", "даана товар үчүн — бүтүн сан", "a whole number for piece goods", "adetli ürün için tam sayı", "donali mahsulot uchun — butun son")
                : qty > r.Max + 0.0001 ? T($"нельзя вернуть больше {r.Max:0.###}", $"{r.Max:0.###} ашык кайтарууга болбойт", $"cannot return more than {r.Max:0.###}", $"{r.Max:0.###} fazlası iade edilemez", $"{r.Max:0.###} dan ko'p qaytarib bo'lmaydi")
                : null;
            if (problem is not null)
            {
                ShowError($"«{r.Name}»: {problem}.");
                return;
            }
            var item = new Dictionary<string, object?>
            {
                ["product_id"] = r.ProductId,
                ["qty"] = qty.ToString("0.###", CultureInfo.InvariantCulture),
                ["purchase_price"] = r.Price.ToString("0.###", CultureInfo.InvariantCulture),
            };
            if (r.ReceiptItemId is { Length: > 0 } rid)
                item["receipt_item_id"] = rid;
            items.Add(item);
        }
        if (items.Count == 0)
        {
            ShowError(T("Укажите количество хотя бы у одного товара.", "Жок дегенде бир товардын санын жазыңыз.", "Enter a quantity for at least one product.", "En az bir ürün için adet girin.", "Kamida bitta mahsulot uchun miqdor kiriting."));
            return;
        }
        var body = new Dictionary<string, object?>
        {
            ["reason"] = _reason,
            ["comment"] = (_comment.Text ?? "").Trim(),
            ["compensation"] = _compensation,
            ["items"] = items,
        };
        if (_byReceipt && _receipt.SelectedIndex >= 0 && _receipt.SelectedIndex < _receipts.Count)
            body["receipt_id"] = _receipts[_receipt.SelectedIndex].Id;
        if (_compensation == "cash" && _cashbox.SelectedIndex >= 0 && _cashbox.SelectedIndex < _cashboxes.Count)
            body["cashbox_id"] = _cashboxes[_cashbox.SelectedIndex].Id;
        _save.IsEnabled = false;
        try
        {
            await _api.RequestAsync(HttpMethod.Post, $"api/main/suppliers/{supplierId}/returns/", body, null, CancellationToken.None, TimeSpan.FromSeconds(40)).ConfigureAwait(true);
            PosLogger.Log($"Возврат поставщику оформлен: {items.Count} строк(и), {_reason}, {_compensation}.", "INFO");
            Close(true);
        }
        catch (Exception ex)
        {
            _save.IsEnabled = true;
            ShowError(T("Не оформлено: ", "Тариздөлгөн жок: ", "Not saved: ", "Kaydedilmedi: ", "Rasmiylashtirilmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
            PosLogger.Log($"Возврат поставщику: не оформлен ({ex.Message}).", "WARNING");
        }
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private void HideError() => _error.IsVisible = false;

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
