using NurMarketKassa.Core.Application;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

public sealed partial class PurchasesWindow
{
    /// <summary>Состояние страницы «Рекомендуемый заказ».</summary>
    private sealed class RecommendState
    {
        public PurchasesSupplierPicker Picker = null!;
        public TextBox Search = null!;
        public readonly StackPanel Chips = new() { Orientation = Orientation.Horizontal };
        public readonly NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid Tiles = new() { MinTileWidth = 150, Spacing = 8 };
        public readonly StackPanel Rows = new();
        public Grid Head = null!;
        public Border Settings = null!;
        public readonly StackPanel PeriodSegs = new() { Orientation = Orientation.Horizontal };
        public readonly StackPanel CoverSegs = new() { Orientation = Orientation.Horizontal };
        public readonly StackPanel SafetySegs = new() { Orientation = Orientation.Horizontal };
        public readonly TextBlock Note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        public Button Make = null!;
        public readonly Dictionary<string, double> Order = new(StringComparer.OrdinalIgnoreCase);
        public List<PlannerRow> Rows0 = new();
        public string Filter = "all";
        public PlannerSettings Settings0 = new();
        public PurchaseOrder? Editing;
        public bool Narrow;
        public bool Compact;
        public int Gen;
        public bool Loading;
    }

    private RecommendState? _recommend;

    /// <summary>«Рекомендуемый заказ» сайта: анализ остатков и продаж поставщика перед приходом. order — открыть сохранённый заказ на правку.</summary>
    public void OpenRecommend(PurchaseOrder? order)
    {
        var data = PurchasesStore.Load();
        var s = new RecommendState { Editing = order, Settings0 = new PlannerSettings(data.PeriodDays, data.CoverDays, data.SafetyDays) };
        _recommend = s;
        _receive = null;
        _recommendSource = null;
        if (order is not null)
            foreach (var l in order.Lines.Where(l => l.Qty > 0))
                s.Order[l.ProductId] = l.Qty;
        var page = new Grid { Margin = PageMargin, RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*,Auto") };

        var back = UiKit.Ghost(this, T("← К закупкам", "← Сатып алууларга", "← Purchases", "← Satın almalar", "← Xaridlarga"));
        back.Height = 44;
        Avalonia.Automation.AutomationProperties.SetName(back, T("Назад к закупкам", "Сатып алууларга кайтуу", "Back to purchases", "Satın almalara dön", "Xaridlarga qaytish"));
        back.Click += (_, _) => CloseSub();
        var titleBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        var title = new TextBlock
        {
            Text = order is null ? T("Рекомендуемый заказ", "Сунушталган буйрутма", "Recommended order", "Önerilen sipariş", "Tavsiya etilgan buyurtma")
                : T($"Заказ {order.Number}", $"Буйрутма {order.Number}", $"Order {order.Number}", $"Sipariş {order.Number}", $"Buyurtma {order.Number}"),
            FontSize = 20, FontWeight = FontWeight.Bold,
        };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var subtitle = new TextBlock
        {
            Text = T("Анализ остатков и продаж поставщика перед закупкой", "Сатып алуудан мурун жеткирүүчүнүн калдыктарын жана сатууларын талдоо",
                "Supplier stock and sales analysis before purchasing", "Satın almadan önce tedarikçi stok ve satış analizi", "Xariddan oldin qoldiq va sotuvlar tahlili"),
            FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Use(subtitle, TextBlock.ForegroundProperty, "BrushTextSoft");
        titleBox.Children.Add(title);
        titleBox.Children.Add(subtitle);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(back);
        Grid.SetColumn(titleBox, 1);
        head.Children.Add(titleBox);
        page.Children.Add(head);

        // Поставщик и поиск (название, штрихкод — сканер тоже сюда).
        s.Picker = new PurchasesSupplierPicker(_svc) { ShowAdd = false };
        s.Picker.SetSuppliers(_filterSupplier.All);
        s.Picker.Changed += async _ => await RecommendLoadAsync().ConfigureAwait(true);
        s.Search = UiKit.Search(this, T("Поиск: название или штрихкод", "Издөө: аты же штрихкод", "Search: name or barcode", "Ara: ad veya barkod", "Qidirish: nomi yoki shtrix-kod"), 44);
        Avalonia.Automation.AutomationProperties.SetName(s.Search, s.Search.Watermark);
        s.Search.TextChanged += (_, _) => RecommendRender();
        var pick = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 0, 0, 10) };
        pick.Children.Add(s.Picker);
        Grid.SetColumn(s.Search, 1);
        s.Search.Margin = new Thickness(10, 0, 0, 0);
        pick.Children.Add(s.Search);
        Grid.SetRow(pick, 1);
        page.Children.Add(pick);

        // Чипы «Все / Требуют закупки / Критический остаток», «Настройки расчёта», «Заполнить рекомендуемый заказ», «Очистить».
        var tools = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var chips = UiKit.Segment(s.Chips);
        chips.Margin = new Thickness(0, 0, 10, 6);
        tools.Children.Add(chips);
        var settingsButton = UiKit.Ghost(this, T("⚙ Настройки расчёта", "⚙ Эсептөө жөндөөлөрү", "⚙ Calculation settings", "⚙ Hesap ayarları", "⚙ Hisob sozlamalari"));
        settingsButton.Height = 44;
        settingsButton.Margin = new Thickness(0, 0, 8, 6);
        Avalonia.Automation.AutomationProperties.SetName(settingsButton, T("Настройки расчёта", "Эсептөө жөндөөлөрү", "Calculation settings", "Hesap ayarları", "Hisob sozlamalari"));
        // Настройки открыты — плитки прячутся (на 1024×600 таблице иначе не остаётся места); итог виден на кнопке «Сформировать закупку».
        settingsButton.Click += (_, _) =>
        {
            s.Settings.IsVisible = !s.Settings.IsVisible;
            s.Tiles.IsVisible = !s.Settings.IsVisible && !s.Compact;
        };
        tools.Children.Add(settingsButton);
        var fill = UiKit.Ghost(this, T("Заполнить рекомендуемый заказ", "Сунушталган буйрутманы толтуруу", "Fill recommended order", "Önerilen siparişi doldur", "Tavsiya etilgan buyurtmani to'ldirish"));
        fill.Height = 44;
        fill.Margin = new Thickness(0, 0, 8, 6);
        Avalonia.Automation.AutomationProperties.SetName(fill, fill.Content as string);
        fill.Click += (_, _) =>
        {
            foreach (var r in s.Rows0.Where(r => r.NeedsPurchase))
                s.Order[r.Product.Id] = r.Recommended;
            RecommendRender();
        };
        tools.Children.Add(fill);
        var clear = UiKit.Ghost(this, T("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash"));
        clear.Height = 44;
        clear.Margin = new Thickness(0, 0, 8, 6);
        Avalonia.Automation.AutomationProperties.SetName(clear, T("Очистить заказ", "Буйрутманы тазалоо", "Clear the order", "Siparişi temizle", "Buyurtmani tozalash"));
        clear.Click += (_, _) =>
        {
            s.Order.Clear();
            RecommendRender();
        };
        tools.Children.Add(clear);
        Grid.SetRow(tools, 2);
        page.Children.Add(tools);

        // Настройки расчёта и формула (как у сайта), скрыты по умолчанию.
        var formula = new TextBlock
        {
            Text = T("Рекомендация = продажи в день × (запас до закупки + страховой запас) − текущий остаток. Формула не заменяет проверку закупщика.",
                "Сунуш = күнүнө сатуу × (сатып алууга чейинки запас + камдык запас) − учурдагы калдык. Формула сатып алуучунун текшерүүсүн алмаштырбайт.",
                "Recommendation = sales per day × (stock until next purchase + safety stock) − current stock. The formula doesn't replace the buyer's check.",
                "Öneri = günlük satış × (sonraki alıma kadar stok + güvenlik stoğu) − mevcut stok. Formül alıcının kontrolünün yerini tutmaz.",
                "Tavsiya = kunlik sotuv × (keyingi xaridgacha zaxira + xavfsizlik zaxirasi) − joriy qoldiq. Formula xaridor tekshiruvini almashtirmaydi."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        };
        Use(formula, TextBlock.ForegroundProperty, "BrushTextSoft");
        var settingsGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, segs) in new[]
                 {
                     (T("Период анализа", "Талдоо мезгили", "Analysis period", "Analiz dönemi", "Tahlil davri"), s.PeriodSegs),
                     (T("Запас до следующей закупки", "Кийинки сатып алууга чейинки запас", "Stock until next purchase", "Sonraki alıma kadar stok", "Keyingi xaridgacha zaxira"), s.CoverSegs),
                     (T("Страховой запас", "Камдык запас", "Safety stock", "Güvenlik stoğu", "Xavfsizlik zaxirasi"), s.SafetySegs),
                 })
        {
            var box = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 16, 6) };
            box.Children.Add(UiKit.Label(this, label));
            box.Children.Add(UiKit.Segment(segs));
            settingsGrid.Children.Add(box);
        }
        s.Settings = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10), IsVisible = false,
            Child = new StackPanel { Children = { settingsGrid, formula } } };
        Use(s.Settings, Border.BackgroundProperty, "BrushPanelSoft");
        Use(s.Settings, Border.BorderBrushProperty, "BrushBorder");
        Grid.SetRow(s.Settings, 3);
        page.Children.Add(s.Settings);
        RecommendBuildSettings();

        s.Tiles.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(s.Tiles, 4);
        page.Children.Add(s.Tiles);
        Use(s.Note, TextBlock.ForegroundProperty, "BrushTextSoft");
        Grid.SetRow(s.Note, 5);
        page.Children.Add(s.Note);

        s.Head = new Grid();
        var headBox = new Border { Padding = new Thickness(14, 8, 8, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = s.Head };
        Use(headBox, Border.BackgroundProperty, "BrushPanelSoft");
        Use(headBox, Border.BorderBrushProperty, "BrushBorder");
        var scroll = new ScrollViewer { Content = s.Rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        table.Children.Add(headBox);
        Grid.SetRow(scroll, 1);
        table.Children.Add(scroll);
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = table };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Grid.SetRow(card, 6);
        page.Children.Add(card);

        // Низ: «Сохранить заказ», «WhatsApp», «PDF» — обводкой; «Сформировать закупку (N)» — жёлтая.
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        var save = UiKit.Ghost(this, order is null ? T("Сохранить заказ", "Буйрутманы сактоо", "Save order", "Siparişi kaydet", "Buyurtmani saqlash") : T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Height = 48;
        Avalonia.Automation.AutomationProperties.SetName(save, T("Сохранить заказ поставщику", "Жеткирүүчүгө буйрутманы сактоо", "Save the supplier order", "Tedarikçi siparişini kaydet", "Buyurtmani saqlash"));
        save.Click += (_, _) => RecommendSave(null);
        Grid.SetColumn(save, 1);
        footer.Children.Add(save);
        var whatsapp = UiKit.Ghost(this, "WhatsApp");
        whatsapp.Height = 48;
        whatsapp.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(whatsapp, T("Отправить заказ поставщику в WhatsApp", "Буйрутманы WhatsApp'ка жөнөтүү", "Send the order to the supplier via WhatsApp", "Siparişi WhatsApp'tan gönder", "Buyurtmani WhatsApp orqali yuborish"));
        whatsapp.Click += (_, _) => RecommendSave("whatsapp");
        Grid.SetColumn(whatsapp, 2);
        footer.Children.Add(whatsapp);
        var pdf = UiKit.Ghost(this, "PDF");
        pdf.Height = 48;
        pdf.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(pdf, T("Заказ поставщику в PDF", "Буйрутма PDF", "Supplier order as PDF", "Sipariş PDF", "Buyurtma PDF"));
        pdf.Click += (_, _) => RecommendSave("pdf");
        Grid.SetColumn(pdf, 3);
        footer.Children.Add(pdf);
        s.Make = UiKit.Primary(this, "");
        s.Make.Height = 48;
        s.Make.Focusable = true;
        s.Make.Margin = new Thickness(8, 0, 0, 0);
        s.Make.Click += (_, _) => RecommendMake();
        Grid.SetColumn(s.Make, 4);
        footer.Children.Add(s.Make);
        Grid.SetRow(footer, 7);
        page.Children.Add(footer);

        page.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 1000;
            // Низкий экран (касса 1024×600): плитки прячутся (число к заказу — на жёлтой кнопке, критичные — на чипе).
            s.Compact = e.NewSize.Height < 600;
            s.Tiles.IsVisible = !s.Compact && !s.Settings.IsVisible;
            if (narrow == s.Narrow && s.Head.ColumnDefinitions.Count > 0)
                return;
            s.Narrow = narrow;
            subtitle.IsVisible = !narrow;
            title.FontSize = narrow ? 17 : 20;
            RecommendRender();
        };
        ShowSub(page);
        if (order is not null)
            s.Picker.Select(_filterSupplier.All.FirstOrDefault(x => x.Id == order.SupplierId)
                            ?? new SupplierInfo(order.SupplierId, order.SupplierName, order.SupplierPhone, "", "", "", "", "", "", "", null));
        else if (_filterSupplier.Selected is { } current)
            s.Picker.Select(current);
        else
            RecommendRender();
        if (s.Picker.All.Count == 0)
            _ = s.Picker.LoadAsync();
        PosLogger.Log($"Закупки: открыт рекомендуемый заказ{(order is null ? "" : " (заказ " + order.Number + ")")}.", "UI");
    }

    private void RecommendBuildSettings()
    {
        if (_recommend is not { } s)
            return;
        void Build(StackPanel panel, int[] values, int current, Func<int, PlannerSettings> make)
        {
            panel.Children.Clear();
            foreach (var v in values)
            {
                var seg = UiKit.Seg(T($"{v} дн.", $"{v} күн", $"{v} d", $"{v} gün", $"{v} kun"), v == current);
                seg.MinHeight = 44;
                seg.Click += async (_, _) =>
                {
                    s.Settings0 = make(v);
                    PurchasesStore.SaveSettings(s.Settings0.PeriodDays, s.Settings0.CoverDays, s.Settings0.SafetyDays);
                    RecommendBuildSettings();
                    // Рекомендации сервера зависят от настроек — спрашиваем заново; свой расчёт — пересчёт без сети.
                    await RecommendLoadAsync(reloadServer: _recommendSource?.ServerRows is not null).ConfigureAwait(true);
                };
                panel.Children.Add(seg);
            }
        }
        Build(s.PeriodSegs, new[] { 7, 14, 30 }, s.Settings0.PeriodDays, v => s.Settings0 with { PeriodDays = v });
        Build(s.CoverSegs, new[] { 7, 14, 21, 30 }, s.Settings0.CoverDays, v => s.Settings0 with { CoverDays = v });
        Build(s.SafetySegs, new[] { 0, 3, 7 }, s.Settings0.SafetyDays, v => s.Settings0 with { SafetyDays = v });
    }

    // Данные поставщика держим между пересчётами (смена настроек не ходит на сервер).
    // ServerRows — строки, посчитанные сервером (как у сайта); null — считаем сами по Products/Sales.
    private sealed record RecommendSource(List<PlannerProduct> Products, Dictionary<string, (DateTime At, double Qty)> Last, List<PlannerSale> Sales, DateTime? HistoryStart, bool SalesOk,
        List<PlannerRow>? ServerRows = null);
    private RecommendSource? _recommendSource;

    private async Task RecommendLoadAsync(bool reloadServer = true)
    {
        if (_recommend is not { } s)
            return;
        var gen = ++s.Gen;
        if (s.Picker.Selected is not { } supplier)
        {
            s.Rows0 = new List<PlannerRow>();
            RecommendRender();
            return;
        }
        s.Loading = true;
        s.Note.Text = T("Считаю по остаткам и продажам…", "Калдыктар жана сатуулар боюнча эсептелүүдө…", "Calculating from stock and sales…", "Stok ve satışlardan hesaplanıyor…", "Qoldiq va sotuvlar bo'yicha hisoblanmoqda…");
        s.Note.IsVisible = true;
        RecommendRender();
        try
        {
            if (reloadServer || _recommendSource is null)
                _recommendSource = await RecommendSourceAsync(supplier).ConfigureAwait(true);
            if (gen != s.Gen || _recommend != s)
                return;
            ShowRecommendSource(_recommendSource);
        }
        catch (Exception ex)
        {
            if (gen != s.Gen)
                return;
            PosLogger.Log($"Закупки: рекомендуемый заказ «{supplier.Name}» не посчитан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            s.Rows0 = new List<PlannerRow>();
            s.Note.Text = T("Товары поставщика не загрузились: ", "Жеткирүүчүнүн товарлары жүктөлгөн жок: ", "The supplier's products didn't load: ", "Tedarikçi ürünleri yüklenmedi: ", "Yetkazib beruvchi mahsulotlari yuklanmadi: ")
                          + ServerTelegramBotApi.DescribeFields(ex);
        }
        finally
        {
            if (gen == s.Gen)
                s.Loading = false;
            RecommendRender();
        }
    }

    /// <summary>Товары поставщика (suppliers/{id}/products/, товары из его приходов, товары каталога с этим поставщиком), последние закупки
    /// по приходам, продажи этого ПК за 60 дней (для периода и прошлого периода).</summary>
    private async Task<RecommendSource> RecommendSourceAsync(SupplierInfo supplier)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // Как сайт: рекомендации считает сервер (продажи всех касс, остатки, последние закупки). Нет адреса или ошибка — считаем сами.
        try
        {
            var settings = _recommend?.Settings0 ?? new PlannerSettings();
            if (await _svc.GetRecommendationsAsync(supplier.Id, settings, cts.Token).ConfigureAwait(true) is { } server)
            {
                PosLogger.Log($"Закупки: рекомендуемый заказ «{supplier.Name}» — сервер, товаров {server.Count}, к заказу {server.Count(r => r.NeedsPurchase)} ({settings.PeriodDays}/{settings.CoverDays}/{settings.SafetyDays} дн.).", "INFO");
                return new RecommendSource(new(), new(), new(), null, true, server);
            }
            PosLogger.Log("Закупки: у сервера нет suppliers/{id}/recommendations/ — рекомендуемый заказ считаем сами.", "INFO");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Закупки: рекомендации сервера не получены ({ex.Message}) — считаем сами.", "WARNING");
        }
        var productsTask = _svc.GetProductsAsync(supplier.Id, cts.Token);
        var receiptsTask = _svc.GetReceiptsPageAsync(1, 100, supplier.Id, null, null, cts.Token);
        List<SupplierProduct> products;
        try
        {
            products = await productsTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: товары поставщика «{supplier.Name}» не загружены ({ex.Message}) — беру из приходов и каталога.", "WARNING");
            products = new List<SupplierProduct>();
        }
        var receipts = new List<SupplierReceipt>();
        try
        {
            receipts = (await receiptsTask.ConfigureAwait(true)).Receipts;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: приходы поставщика «{supplier.Name}» не загружены ({ex.Message}).", "WARNING");
        }
        var last = new Dictionary<string, (DateTime At, double Qty)>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in receipts)
            foreach (var l in r.Lines.Where(l => l.ProductId.Length > 0))
            {
                names.TryAdd(l.ProductId, l.ProductName);
                if (r.CreatedAt is { } at && (!last.TryGetValue(l.ProductId, out var was) || at > was.At))
                    last[l.ProductId] = (at, l.Qty);
            }
        var ids = new Dictionary<string, SupplierProduct?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products.Where(p => p.Id.Length > 0))
            ids[p.Id] = p;
        foreach (var id in names.Keys)
            ids.TryAdd(id, null);
        foreach (var tile in CatalogCacheService.Products.Where(t => !string.IsNullOrWhiteSpace(t.ClientName) && string.Equals(t.ClientName!.Trim(), supplier.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            ids.TryAdd(tile.Id, null);
        var list = new List<PlannerProduct>();
        foreach (var (id, sp) in ids)
        {
            var tile = CatalogCacheService.Products.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            var name = tile?.Title ?? sp?.Name ?? names.GetValueOrDefault(id) ?? id;
            list.Add(new PlannerProduct(id, name, tile?.Barcode ?? "", sp?.Code is { Length: > 0 } c ? c : tile?.ProductCode ?? "",
                string.IsNullOrWhiteSpace(tile?.Unit) ? "шт" : tile!.Unit!, tile?.Quantity ?? sp?.Quantity ?? 0,
                sp?.PurchasePrice is > 0 and var pp ? pp : tile?.PurchasePrice ?? 0, tile?.IsWeighted ?? false));
        }
        // Продажи этого ПК: 60 дней — и для периода (до 30 дней), и для прошлого такого же периода (рост «+250 %»).
        var sales = new List<PlannerSale>();
        DateTime? start = null;
        var ok = true;
        try
        {
            var since = DateTime.Today.AddDays(-60).ToUniversalTime();
            var wanted = list.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = await Task.Run(() => SoldLineItemsStore.LoadSince(since)).ConfigureAwait(true);
            sales = rows.Where(r => wanted.Contains(r.ProductId)).Select(r => new PlannerSale(r.ProductId, r.Quantity, r.SoldAt)).ToList();
            start = await Task.Run(SoldLineItemsStore.GetEarliestDate).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ok = false;
            PosLogger.Log($"Закупки: история продаж для рекомендуемого заказа не прочитана ({ex.Message}).", "WARNING");
        }
        PosLogger.Log($"Закупки: рекомендуемый заказ «{supplier.Name}» — товаров {list.Count}, продаж за 60 дн. {sales.Count}, приходов {receipts.Count}.", "INFO");
        return new RecommendSource(list, last, sales, start, ok);
    }

    /// <summary>Пересчитать по загруженным данным (и стенд снимков без сети).</summary>
    private void ShowRecommendSource(RecommendSource src)
    {
        if (_recommend is not { } s)
            return;
        _recommendSource = src;
        s.Rows0 = src.ServerRows is { } server ? PurchasePlanner.Order(server) : PurchasePlanner.Build(src.Products, src.Sales, src.Last, s.Settings0, DateTime.Now, src.HistoryStart);
        s.Note.Text = src.ServerRows is not null ? ""
            : !src.SalesOk
            ? T("История продаж на этом компьютере недоступна — рекомендации без продаж. Остатки и последние закупки — с сервера.",
                "Бул компьютерде сатуу тарыхы жеткиликсиз — сунуштар сатуусуз. Калдыктар жана акыркы сатып алуулар — серверден.",
                "The sales history on this computer is unavailable — recommendations without sales. Stock and last purchases come from the server.",
                "Bu bilgisayarda satış geçmişi yok — öneriler satışsız. Stok ve son alımlar sunucudan.",
                "Bu kompyuterda sotuv tarixi mavjud emas — tavsiyalar sotuvsiz. Qoldiq va oxirgi xaridlar serverdan.")
            : src.HistoryStart is { } start && start > DateTime.UtcNow.AddDays(-s.Settings0.PeriodDays)
                ? T($"Продажи на этом компьютере — с {start.ToLocalTime():dd.MM.yyyy}: средняя в день считается по этим дням.",
                    $"Бул компьютердеги сатуулар — {start.ToLocalTime():dd.MM.yyyy} баштап: күнүнө орточо ушул күндөр боюнча.",
                    $"Sales on this computer since {start.ToLocalTime():dd.MM.yyyy}: the daily average uses these days.",
                    $"Bu bilgisayardaki satışlar {start.ToLocalTime():dd.MM.yyyy} tarihinden itibaren: günlük ortalama bu günlere göre.",
                    $"Bu kompyuterdagi sotuvlar {start.ToLocalTime():dd.MM.yyyy} dan: kunlik o'rtacha shu kunlar bo'yicha.")
                : "";
        s.Note.IsVisible = s.Note.Text.Length > 0;
        RecommendRender();
    }

    // 2026-10-23: фильтр «Требуют закупки» как на сайте — только рекомендация > 0 (раньше ещё и строки с вписанным количеством).
    private IEnumerable<PlannerRow> RecommendShown()
    {
        var s = _recommend!;
        var q = (s.Search.Text ?? "").Trim();
        return s.Rows0.Where(r => (s.Filter switch { "need" => r.NeedsPurchase, "critical" => r.IsCritical, _ => true })
                                  && (q.Length == 0 || SearchMatch.Matches(q, r.Product.Name, r.Product.Barcode, r.Product.Code))); // 2026-10-29: единый поиск
    }

    private string RecommendColumns => _recommend?.Narrow == true ? "2.2*,0.8*,1*,1.2*,0,1.3*,1*,104" : "2.4*,0.8*,1*,1.2*,1.1*,1.3*,1*,110";

    private void RecommendRender()
    {
        if (_recommend is not { } s)
            return;
        // Чипы с количеством.
        s.Chips.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("all", T("Все товары", "Бардык товарлар", "All products", "Tüm ürünler", "Barcha mahsulotlar") + $" ({s.Rows0.Count})"),
                     ("need", T("Требуют закупки", "Сатып алуу керек", "Need purchasing", "Alım gerekli", "Xarid kerak") + $" ({s.Rows0.Count(r => r.NeedsPurchase)})"),
                     ("critical", T("Критический остаток", "Критикалык калдык", "Critical stock", "Kritik stok", "Kritik qoldiq") + $" ({s.Rows0.Count(r => r.IsCritical)})"),
                 })
        {
            var seg = UiKit.Seg(text, key == s.Filter);
            seg.MinHeight = 44;
            seg.Click += (_, _) =>
            {
                s.Filter = key;
                RecommendRender();
            };
            s.Chips.Children.Add(seg);
        }
        // Плитки.
        var sum = s.Rows0.Where(r => s.Order.ContainsKey(r.Product.Id)).Sum(r => s.Order[r.Product.Id] * r.Product.PurchasePrice);
        s.Tiles.Children.Clear();
        s.Tiles.Children.Add(UiKit.Kpi(T("Товаров поставщика", "Жеткирүүчүнүн товарлары", "Supplier's products", "Tedarikçi ürünleri", "Mahsulotlar"), s.Rows0.Count.ToString(CultureInfo.InvariantCulture)));
        s.Tiles.Children.Add(UiKit.Kpi(T("Нужно заказать", "Буйрутма керек", "Need ordering", "Sipariş gerekli", "Buyurtma kerak"), s.Rows0.Count(r => r.NeedsPurchase).ToString(CultureInfo.InvariantCulture)));
        var critical = s.Rows0.Count(r => r.IsCritical);
        s.Tiles.Children.Add(UiKit.Kpi(T("Критический остаток", "Критикалык калдык", "Critical stock", "Kritik stok", "Kritik qoldiq"), critical.ToString(CultureInfo.InvariantCulture), danger: critical > 0));
        s.Tiles.Children.Add(UiKit.Kpi(T("Предварительная сумма", "Болжолдуу сумма", "Estimated total", "Tahmini toplam", "Taxminiy summa"), MoneyText.Som(sum)));
        var count = s.Order.Count(x => x.Value > 0);
        s.Make.Content = T($"Сформировать закупку ({count})", $"Сатып алууну түзүү ({count})", $"Create purchase ({count})", $"Alım oluştur ({count})", $"Xarid yaratish ({count})");
        Avalonia.Automation.AutomationProperties.SetName(s.Make, T("Сформировать закупку — приём товара с этим заказом", "Сатып алууну түзүү", "Create a purchase with this order", "Bu siparişle alım oluştur", "Shu buyurtma bilan xarid yaratish"));
        s.Make.IsEnabled = count > 0 && s.Picker.Selected is not null;

        // Шапка таблицы.
        s.Head.Children.Clear();
        s.Head.ColumnDefinitions = new ColumnDefinitions(RecommendColumns);
        var heads = new[]
        {
            T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"),
            T("Продажи 7 / 30 дн.", "Сатуу 7 / 30 күн", "Sales 7 / 30 d", "Satış 7 / 30 g", "Sotuv 7 / 30 kun"), T("В день · хватит на", "Күнүнө · жетет", "Per day · lasts", "Günlük · yeter", "Kuniga · yetadi"),
            T("Последняя закупка", "Акыркы сатып алуу", "Last purchase", "Son alım", "Oxirgi xarid"), T("Статус", "Абалы", "Status", "Durum", "Holat"),
            T("Рекомендация", "Сунуш", "Recommended", "Öneri", "Tavsiya"), T("Заказать", "Буйрутма", "Order", "Sipariş", "Buyurtma"),
        };
        for (var i = 0; i < heads.Length; i++)
        {
            var t = new TextBlock { Text = heads[i], FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = i is 1 or 6 ? TextAlignment.Right : TextAlignment.Left, IsVisible = !(s.Narrow && i == 4) };
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, i);
            s.Head.Children.Add(t);
        }
        s.Rows.Children.Clear();
        if (s.Picker.Selected is null)
        {
            s.Rows.Children.Add(SectionCards.Empty(this, T("Выберите поставщика — покажем его товары: остаток, продажи, на сколько дней хватит и сколько заказать.",
                "Жеткирүүчүнү тандаңыз — анын товарларын көрсөтөбүз: калдык, сатуулар, канча күнгө жетет жана канча буйрутма кылуу керек.",
                "Choose a supplier — we'll show their products: stock, sales, how many days it lasts and how much to order.",
                "Tedarikçi seçin — ürünlerini gösteririz: stok, satışlar, kaç gün yeter ve ne kadar sipariş edilmeli.",
                "Yetkazib beruvchini tanlang — mahsulotlarini ko'rsatamiz: qoldiq, sotuvlar, necha kunga yetadi va qancha buyurtma qilish kerak.")));
            return;
        }
        if (s.Loading && s.Rows0.Count == 0)
            return;
        var shown = RecommendShown().Take(400).ToList();
        if (shown.Count == 0)
        {
            s.Rows.Children.Add(SectionCards.Empty(this, s.Rows0.Count == 0
                ? T("У этого поставщика пока нет товаров. Товары появятся после первого прихода от него или если указать поставщика в карточке товара.",
                    "Бул жеткирүүчүнүн товарлары азырынча жок. Алгачкы кириштен кийин же товардын карточкасында жеткирүүчүнү көрсөтсөңүз пайда болот.",
                    "This supplier has no products yet. They appear after the first delivery or when the supplier is set in the product card.",
                    "Bu tedarikçinin henüz ürünü yok. İlk girişten sonra veya ürün kartında tedarikçi belirtilince görünür.",
                    "Bu yetkazib beruvchining mahsulotlari hali yo'q. Birinchi kirimdan keyin yoki kartada ko'rsatilsa paydo bo'ladi.")
                : s.Filter == "need"
                    ? T("Всего хватает — по продажам и запасу заказывать нечего.", "Баары жетиштүү — сатуу жана запас боюнча буйрутма кылуунун кереги жок.", "Everything is sufficient — nothing to order by sales and stock.",
                        "Her şey yeterli — satış ve stoğa göre sipariş gerekmiyor.", "Hammasi yetarli — buyurtma kerak emas.")
                    : T("По этому поиску товаров нет.", "Бул издөө боюнча товар жок.", "No products match this search.", "Bu aramaya uyan ürün yok.", "Bu qidiruv bo'yicha mahsulot yo'q.")));
            return;
        }
        foreach (var r in shown)
            s.Rows.Children.Add(RecommendRow(r));
    }

    private Control RecommendRow(PlannerRow r)
    {
        var s = _recommend!;
        var p = r.Product;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(RecommendColumns) };
        var who = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var name = Cell(p.Name, "BrushText", 14, FontWeight.SemiBold);
        name.Margin = default;
        name.MaxLines = 2;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        who.Children.Add(name);
        var code = string.Join(" · ", new[] { p.Barcode, p.Code }.Where(x => x.Length > 0));
        if (code.Length > 0)
        {
            var c = Cell(code, "BrushTextSoft", 12);
            c.Margin = default;
            who.Children.Add(c);
        }
        if (r.NewSales || r.TrendPercent is { } tr && Math.Abs(tr) >= 20)
        {
            var trend = Cell(r.NewSales ? T("+ новые продажи", "+ жаңы сатуулар", "+ new sales", "+ yeni satışlar", "+ yangi sotuvlar") : (r.TrendPercent > 0 ? "+" : "") + r.TrendPercent!.Value.ToString("0", Ru) + " %",
                r.NewSales || r.TrendPercent > 0 ? "BrushSuccess" : "BrushDanger", 12);
            trend.Margin = default;
            ToolTip.SetTip(trend, T("Продажи за период против прошлого такого же периода", "Мезгилдеги сатуулар мурунку ушундай мезгилге салыштырмалуу", "Sales this period vs the previous one",
                "Bu dönemin satışları önceki döneme göre", "Davrdagi sotuvlar oldingi davrga nisbatan"));
            who.Children.Add(trend);
        }
        g.Children.Add(who);
        var unit = p.Unit.Length > 0 ? p.Unit : "шт";
        Put(g, 1, RightCell(Q(p.Stock) + " " + unit, p.Stock <= 0 ? "BrushDanger" : "BrushText"));
        Put(g, 2, Cell($"{Q(r.Sales7)} / {Q(r.Sales30)}", "BrushText", 13.5));
        var perDay = r.PerDay > 0 ? r.PerDay.ToString(r.PerDay >= 10 ? "0.#" : "0.##", Ru) : "0";
        var days = r.DaysLeft is { } d ? (d >= 999 ? "999+" : d.ToString(d >= 10 ? "0" : "0.#", Ru)) + " " + T("дн.", "күн", "d", "gün", "kun") : "—";
        Put(g, 3, Cell(perDay + " · " + days, "BrushText", 13.5));
        var last = Cell(r.LastPurchaseAt is { } at ? at.ToString("dd.MM", Ru) + " · " + Q(r.LastPurchaseQty) + " " + unit : "—", "BrushTextSoft", 13);
        last.IsVisible = !s.Narrow;
        Put(g, 4, last);
        var (text, bg, fg) = r.Status switch
        {
            PlannerStatus.OutOfStock => (T("Нет в наличии", "Жок", "Out of stock", "Stokta yok", "Mavjud emas"), "BrushDangerSoft", "BrushDangerOnSoft"),
            PlannerStatus.Critical => (T("Критический остаток", "Критикалык калдык", "Critical stock", "Kritik stok", "Kritik qoldiq"), "BrushDangerSoft", "BrushDangerOnSoft"),
            PlannerStatus.Soon => (T("Скоро закончится", "Жакында бүтөт", "Running out soon", "Yakında bitecek", "Tez orada tugaydi"), "BrushWarningSoft", "BrushText"),
            PlannerStatus.Low => (T("Низкий остаток", "Аз калдык", "Low stock", "Düşük stok", "Kam qoldiq"), "BrushPanelSoft", "BrushText"),
            PlannerStatus.Enough => (T("Хватает", "Жетиштүү", "Enough", "Yeterli", "Yetarli"), "BrushSuccessSoft", "BrushSuccess"),
            _ => (T("Нет продаж", "Сатуу жок", "No sales", "Satış yok", "Sotuv yo'q"), "BrushPanelSoft", "BrushTextSoft"),
        };
        var pill = SectionCards.Pill(this, text, bg, fg);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        pill.Margin = new Thickness(0, 0, 10, 0);
        // Узкая колонка (1024): длинный статус переносится, а не обрезается.
        if (pill.Child is TextBlock pillText)
            pillText.TextWrapping = TextWrapping.Wrap;
        Put(g, 5, pill);
        var rec = RightCell(r.Recommended > 0 ? Q(r.Recommended) + " " + unit + " ⓘ" : "—", r.Recommended > 0 ? "BrushText" : "BrushTextSoft");
        // 2026-10-23: строки сервера — средняя в день не округлённая (продажи периода / дни периода), как считает сервер; иначе подсказка расходилась с числом на 1.
        var exactPerDay = _recommendSource?.ServerRows is not null ? r.SalesPeriod / Math.Max(1, s.Settings0.PeriodDays) : r.PerDay;
        var need = exactPerDay * (s.Settings0.CoverDays + s.Settings0.SafetyDays) - Math.Max(0, p.Stock);
        ToolTip.SetTip(rec, T($"{perDay} в день × ({s.Settings0.CoverDays} + {s.Settings0.SafetyDays} дн.) − остаток {Q(Math.Max(0, p.Stock))} = {need.ToString("0.##", Ru)} → {Q(r.Recommended)} {unit}",
            $"{perDay} күнүнө × ({s.Settings0.CoverDays} + {s.Settings0.SafetyDays} күн) − калдык {Q(Math.Max(0, p.Stock))} = {need.ToString("0.##", Ru)} → {Q(r.Recommended)} {unit}",
            $"{perDay} per day × ({s.Settings0.CoverDays} + {s.Settings0.SafetyDays} d) − stock {Q(Math.Max(0, p.Stock))} = {need.ToString("0.##", Ru)} → {Q(r.Recommended)} {unit}",
            $"günde {perDay} × ({s.Settings0.CoverDays} + {s.Settings0.SafetyDays} g) − stok {Q(Math.Max(0, p.Stock))} = {need.ToString("0.##", Ru)} → {Q(r.Recommended)} {unit}",
            $"kuniga {perDay} × ({s.Settings0.CoverDays} + {s.Settings0.SafetyDays} kun) − qoldiq {Q(Math.Max(0, p.Stock))} = {need.ToString("0.##", Ru)} → {Q(r.Recommended)} {unit}"));
        Put(g, 6, rec);
        var box = NumberBox(s.Order.GetValueOrDefault(p.Id), T("Заказать", "Буйрутма", "Order", "Sipariş", "Buyurtma") + " " + p.Name, v =>
        {
            if (v > 0)
                s.Order[p.Id] = v;
            else
                s.Order.Remove(p.Id);
            RecommendTotals();
        });
        box.Margin = new Thickness(0);
        Put(g, 7, box);
        var row = new Border { Padding = new Thickness(14, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = g, MinHeight = 56 };
        Use(row, Border.BorderBrushProperty, "BrushBorder");
        return row;
    }

    private TextBlock RightCell(string text, string brush)
    {
        var t = Cell(text, brush, 13.5);
        t.TextAlignment = TextAlignment.Right;
        return t;
    }

    /// <summary>Поменяли количество — плитки и кнопку без перерисовки строк (поле не теряет фокус).</summary>
    private void RecommendTotals()
    {
        if (_recommend is not { } s)
            return;
        var sum = s.Rows0.Where(r => s.Order.ContainsKey(r.Product.Id)).Sum(r => s.Order[r.Product.Id] * r.Product.PurchasePrice);
        if (s.Tiles.Children.Count == 4)
            s.Tiles.Children[3] = UiKit.Kpi(T("Предварительная сумма", "Болжолдуу сумма", "Estimated total", "Tahmini toplam", "Taxminiy summa"), MoneyText.Som(sum));
        var count = s.Order.Count(x => x.Value > 0);
        s.Make.Content = T($"Сформировать закупку ({count})", $"Сатып алууну түзүү ({count})", $"Create purchase ({count})", $"Alım oluştur ({count})", $"Xarid yaratish ({count})");
        s.Make.IsEnabled = count > 0 && s.Picker.Selected is not null;
    }

    private List<PurchaseOrderLine> RecommendLines()
    {
        var s = _recommend!;
        return s.Rows0.Where(r => s.Order.TryGetValue(r.Product.Id, out var q) && q > 0)
            .Select(r => new PurchaseOrderLine { ProductId = r.Product.Id, Name = r.Product.Name, Code = r.Product.Code.Length > 0 ? r.Product.Code : r.Product.Barcode, Unit = r.Product.Unit, Qty = s.Order[r.Product.Id], Price = r.Product.PurchasePrice })
            .ToList();
    }

    /// <summary>«Сохранить заказ» (then: whatsapp — открыть чат поставщика с текстом, pdf — заказ в PDF). Заказ попадает во вкладку «Заказы поставщикам».</summary>
    private async void RecommendSave(string? then)
    {
        if (_recommend is not { } s || s.Picker.Selected is not { } supplier)
            return;
        var lines = RecommendLines();
        if (lines.Count == 0)
        {
            s.Note.Text = T("Впишите количество в колонке «Заказать» или нажмите «Заполнить рекомендуемый заказ».", "«Буйрутма» тилкесине санын жазыңыз же «Сунушталган буйрутманы толтурууну» басыңыз.",
                "Enter quantities in the “Order” column or press “Fill recommended order”.", "«Sipariş» sütununa miktar girin veya «Önerilen siparişi doldur»a basın.",
                "«Buyurtma» ustuniga miqdor kiriting yoki «Tavsiya etilgan buyurtmani to'ldirish»ni bosing.");
            s.Note.IsVisible = true;
            return;
        }
        var order = s.Editing ?? new PurchaseOrder();
        order.SupplierId = supplier.Id;
        order.SupplierName = supplier.Name;
        order.SupplierPhone = supplier.Phone;
        order.Lines = lines;
        if (order.Author.Length == 0)
            order.Author = PosApp.CurrentUserDisplayName ?? "";
        order = PurchasesStore.SaveOrder(order);
        s.Editing = order;
        PosLogger.Log($"Закупки: заказ {order.Number} «{supplier.Name}» сохранён ({lines.Count} поз., {order.Total:0.00}).", "INFO");
        var docLines = lines.Select(l => new PurchaseDocs.DocLine(l.Name, l.Unit, l.Qty, l.Price)).ToList();
        string message;
        if (then == "whatsapp")
        {
            PurchaseDocs.OpenWhatsApp(supplier.Phone, PurchaseDocs.OrderText(supplier.Name, order.Number, order.CreatedAt, docLines, order.Note));
            PurchasesStore.SetOrderStatus(order.Id, "sent");
            message = T($"Заказ {order.Number} сохранён и открыт в WhatsApp — отметили «Отправлен».", $"{order.Number} буйрутмасы сакталып, WhatsApp'та ачылды — «Жөнөтүлдү» деп белгиленди.",
                $"Order {order.Number} saved and opened in WhatsApp — marked “Sent”.", $"{order.Number} siparişi kaydedildi ve WhatsApp'ta açıldı — «Gönderildi».",
                $"{order.Number} buyurtma saqlandi va WhatsApp'da ochildi — «Yuborildi».");
        }
        else if (then == "pdf")
        {
            var (path, error) = await PurchaseDocs.SavePdfAsync(PurchaseDocs.Build(this, "order", new PurchaseDocs.Party(supplier.Name, supplier.Phone, supplier.Inn, supplier.Address),
                order.Number, order.CreatedAt, docLines, order.Note), PurchaseDocs.FileName("order", supplier.Name, order.Number, order.CreatedAt)).ConfigureAwait(true);
            message = path is not null
                ? T($"Заказ {order.Number} сохранён, PDF: {path}", $"{order.Number} буйрутмасы сакталды, PDF: {path}", $"Order {order.Number} saved, PDF: {path}", $"{order.Number} kaydedildi, PDF: {path}", $"{order.Number} saqlandi, PDF: {path}")
                : T("PDF не сохранён: ", "PDF сакталган жок: ", "PDF not saved: ", "PDF kaydedilemedi: ", "PDF saqlanmadi: ") + error;
        }
        else
            message = T($"Заказ {order.Number} сохранён — он во вкладке «Заказы поставщикам», по нему можно принять товар.", $"{order.Number} буйрутмасы сакталды — «Жеткирүүчүлөргө буйрутмалар» өтмөгүндө.",
                $"Order {order.Number} saved — it's in the “Supplier orders” tab; goods can be received against it.", $"{order.Number} kaydedildi — «Tedarikçi siparişleri» sekmesinde.",
                $"{order.Number} saqlandi — «Buyurtmalar» bo'limida.");
        s.Note.Text = message;
        s.Note.IsVisible = true;
    }

    /// <summary>«Сформировать закупку (N)» сайта — приём товара с этими строками (заказ сохраняется, чтобы приход отметил принятое).</summary>
    private void RecommendMake()
    {
        if (_recommend is not { } s || s.Picker.Selected is not { } supplier)
            return;
        var lines = RecommendLines();
        if (lines.Count == 0)
            return;
        var order = s.Editing;
        if (order is not null)
        {
            order.Lines = lines;
            order = PurchasesStore.SaveOrder(order);
        }
        OpenReceive(new ReceivePrefill(supplier.Id, supplier.Name, order?.Id, order?.Number,
            lines.Select(l => (l.ProductId, l.Name, l.Unit, l.Qty, (double?)(l.Price > 0 ? l.Price : null))).ToList()));
    }
}
