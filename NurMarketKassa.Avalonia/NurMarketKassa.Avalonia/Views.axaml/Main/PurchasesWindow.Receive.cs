using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-22 (закупки как в вебе): с чего начать приём — заказ поставщику (что осталось принять), прошлый приход («Повторить
/// закупку»), «Рекомендуемый заказ» или «Пополнение» (строки с количеством).</summary>
public sealed record ReceivePrefill(string? SupplierId, string? SupplierName, string? OrderId, string? OrderNumber,
    IReadOnlyList<(string ProductId, string Name, string Unit, double Qty, double? Price)> Lines)
{
    public static ReceivePrefill FromOrder(PurchaseOrder o) => new(o.SupplierId, o.SupplierName, o.Id, o.Number,
        o.Lines.Where(l => l.Left > 0.0005).Select(l => (l.ProductId, l.Name, l.Unit, l.Left, (double?)(l.Price > 0 ? l.Price : null))).ToList());

    public static ReceivePrefill FromReceipt(SupplierReceipt r) => new(r.SupplierId, r.SupplierName, null, null,
        r.Lines.Where(l => l.ProductId.Length > 0).Select(l => (l.ProductId, l.ProductName, l.Unit, l.Qty, (double?)(l.PurchasePrice > 0 ? l.PurchasePrice : null))).ToList());
}

public sealed partial class PurchasesWindow
{
    /// <summary>Состояние открытого мастера «Приём товара».</summary>
    private sealed class ReceiveState
    {
        public PurchasesSupplierPicker Picker = null!;
        public readonly ObservableCollection<ReceivingLineVm> Lines = new();
        public readonly StackPanel Rows = new();
        public readonly StackPanel Steps = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        public readonly NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid Tiles = new() { MinTileWidth = 150, Spacing = 8 };
        public TextBox Scan = null!;
        public readonly StackPanel Suggest = new() { Spacing = 2 };
        public Border SuggestBox = null!;
        public readonly TextBlock Message = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        public Button Post = null!;
        public readonly TextBlock Footer = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        public Grid Head = null!;
        public string? OrderId;
        public string? OrderNumber;
        public bool Busy;
        public TextBlock? Title;
        public Border MessageBox = null!;
        public bool Narrow;
        // 2026-10-24, ТЗ «Склад» (Б2): «скан → строка» как в Склад → Приёмка (Б1).
        public CheckBox FastNext = null!;
        public Button Raise = null!;
        public Button SupplierGoods = null!;
        public ScrollViewer Scroll = null!;
        public Border GoodsPanel = null!;
        public TextBox GoodsSearch = null!;
        public ListBox GoodsList = null!;
        public readonly TextBlock GoodsTitle = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        public List<SupplierGoodsRow> GoodsRows = new();
        public HashSet<string> SupplierIds = new(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource? GoodsCts;
        public bool GoodsOpen;
        public bool GoodsLoading;
        public bool GoodsPicking;
        public bool AddedFromPanel;
        public ReceivingLineVm? Highlight;
        public readonly Dictionary<ReceivingLineVm, RowParts> RowMap = new();
        // Скан внутри ячейки таблицы (поле «Количество» и др.): быстрая серия символов + Enter — это сканер, а не человек.
        public long FsTick;
        public ulong? FsRaw;
        public string FsBuf = "";
        public int FsRun;
        public TextBox? FsBox;
        public string? FsSnap;
        public int FsCaret;
    }

    /// <summary>Поля одной строки таблицы приёма: нужны, чтобы вести курсор по ячейкам и обновлять числа без пересборки строки.</summary>
    private sealed class RowParts
    {
        public Border Box = null!;
        public TextBox? Name;
        public TextBox Qty = null!, Purchase = null!, Markup = null!, Sale = null!;
        public bool Syncing;
        public IDisposable? HighlightBinding;
        public Action<bool> Refresh = _ => { };
    }

    /// <summary>Строка панели «Товары поставщика».</summary>
    private sealed record SupplierGoodsRow(string ProductId, string Name, string Unit, double Price, string Info, string SearchKey);

    private ReceiveState? _receive;

    private bool ReceiveHasWork => _receive is { } r && r.Lines.Any(l => l.Quantity > 0);

    /// <summary>«＋ Приём товара» (prefill — по заказу, прошлому приходу, рекомендуемому заказу или «Пополнению»).</summary>
    public void OpenReceive(ReceivePrefill? prefill)
    {
        var s = new ReceiveState { OrderId = prefill?.OrderId, OrderNumber = prefill?.OrderNumber };
        _receive = s;
        _recommend = null;
        var page = new Grid { Margin = PageMargin, RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto") };

        // Шапка: «← К закупкам», название с шагами «1 Поставщик → 2 Товары → 3 Оплата».
        var back = UiKit.Ghost(this, T("← К закупкам", "← Сатып алууларга", "← Purchases", "← Satın almalar", "← Xaridlarga"));
        back.Height = 44;
        Avalonia.Automation.AutomationProperties.SetName(back, T("Назад к закупкам", "Сатып алууларга кайтуу", "Back to purchases", "Satın almalara dön", "Xaridlarga qaytish"));
        back.Click += async (_, _) =>
        {
            if (ReceiveHasWork && !await ConfirmAsync(T("Выйти без оприходования? Набранные товары не сохранятся.", "Кабыл албай чыгасызбы? Терилген товарлар сакталбайт.",
                    "Leave without posting? The entered products won't be saved.", "Kabul etmeden çıkılsın mı? Girilen ürünler kaydedilmez.", "Qabul qilmasdan chiqasizmi? Kiritilgan mahsulotlar saqlanmaydi."),
                    T("Выйти", "Чыгуу", "Leave", "Çık", "Chiqish")).ConfigureAwait(true))
                return;
            CloseSub();
        };
        var title = new TextBlock
        {
            Text = T("Приём товара", "Товар кабыл алуу", "Receive goods", "Mal kabul", "Tovar qabul qilish") + (s.OrderNumber is { } num ? T($" по заказу {num}", $" — {num} буйрутмасы боюнча", $" for order {num}", $" — {num} siparişi", $" — {num} buyurtma bo'yicha") : ""),
            FontSize = 20, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        s.Title = title;
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(back);
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        Grid.SetColumn(s.Steps, 2);
        s.Steps.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(s.Steps);
        page.Children.Add(head);

        // Шаг 1: поставщик (поиск и «＋») и плитки «Поставщик / Позиций / Количество / Сумма накладной».
        s.Picker = new PurchasesSupplierPicker(_svc);
        s.Picker.SetSuppliers(_filterSupplier.All);
        s.Picker.Changed += sup =>
        {
            ReceiveRefresh();
            _ = ReceiveLoadGoodsAsync(s, sup);
        };
        var supplierRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 10) };
        var supplierLabel = UiKit.Label(this, T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"));
        supplierLabel.VerticalAlignment = VerticalAlignment.Center;
        supplierLabel.Margin = new Thickness(0, 0, 12, 0);
        supplierRow.Children.Add(supplierLabel);
        Grid.SetColumn(s.Picker, 1);
        supplierRow.Children.Add(s.Picker);
        Grid.SetRow(supplierRow, 1);
        page.Children.Add(supplierRow);
        s.Tiles.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(s.Tiles, 2);
        page.Children.Add(s.Tiles);

        // Шаг 2: товары — скан/поиск (общий поиск кассы ProductCodeLookup), «Товары поставщика», «Очистить количества», «Очистить список».
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        s.Scan = UiKit.Input(this, T("Сканируйте штрихкод или найдите товар по названию", "Штрихкодду сканерлеңиз же товарды аты боюнча табыңыз",
            "Scan a barcode or find a product by name", "Barkodu okutun veya ürünü adıyla bulun", "Shtrix-kodni skanerlang yoki mahsulotni nomi bo'yicha toping"), 44);
        Avalonia.Automation.AutomationProperties.SetName(s.Scan, s.Scan.Watermark);
        // 2026-10-24, ТЗ «Склад» (Б2): подсказка про порядок работы — скан первым, поставщика можно выбрать позже.
        ToolTip.SetTip(s.Scan, ReceiveHintText());
        s.Scan.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await ReceiveAddByCodeAsync((s.Scan.Text ?? "").Trim()).ConfigureAwait(true);
        };
        s.Scan.TextChanged += (_, _) => ReceiveSuggest();
        tools.Children.Add(s.Scan);
        // 2026-10-24, ТЗ «Склад» (Б2): как в Склад → Приёмка — флажок «Сразу к скану» (курсор остаётся в поле скана, каждый скан = +1).
        s.FastNext = new CheckBox { Content = T("Сразу к скану", "Дароо сканерге", "Straight to scan", "Doğrudan okutmaya", "Darhol skanerga"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Use(s.FastNext, ContentControl.ForegroundProperty, "BrushText");
        ToolTip.SetTip(s.FastNext, T("Курсор остаётся в поле скана, каждый скан прибавляет 1 к количеству.", "Курсор сканер талаасында калат, ар бир скан санга 1 кошот.",
            "The cursor stays in the scan field; every scan adds 1 to the quantity.", "İmleç okutma alanında kalır, her okutma miktara 1 ekler.", "Kursor skaner maydonida qoladi, har bir skan soniga 1 qo'shadi."));
        Avalonia.Automation.AutomationProperties.SetName(s.FastNext, s.FastNext.Content as string);
        Grid.SetColumn(s.FastNext, 1);
        tools.Children.Add(s.FastNext);
        var supplierGoods = s.SupplierGoods = UiKit.Ghost(this, ReceiveGoodsButtonText(s));
        supplierGoods.Height = 44;
        supplierGoods.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(supplierGoods, T("Товары поставщика", "Жеткирүүчүнүн товарлары", "Supplier's products", "Tedarikçi ürünleri", "Yetkazib beruvchi mahsulotlari"));
        supplierGoods.Click += (_, _) => ReceiveToggleGoods(s);
        Grid.SetColumn(supplierGoods, 2);
        tools.Children.Add(supplierGoods);
        var zero = UiKit.Ghost(this, T("Обнулить кол-во", "Санын нөлдөө", "Zero quantities", "Miktarları sıfırla", "Miqdorni nollash"));
        zero.Height = 44;
        zero.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(zero, T("Очистить количества", "Сандарды тазалоо", "Clear quantities", "Miktarları temizle", "Miqdorlarni tozalash"));
        zero.Click += (_, _) =>
        {
            foreach (var l in s.Lines)
                l.Quantity = 0;
            ReceiveRender();
        };
        Grid.SetColumn(zero, 3);
        tools.Children.Add(zero);
        var clear = UiKit.Ghost(this, T("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash"));
        clear.Height = 44;
        clear.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(clear, T("Очистить список товаров", "Товарлар тизмесин тазалоо", "Clear the product list", "Ürün listesini temizle", "Mahsulotlar ro'yxatini tozalash"));
        clear.Click += async (_, _) =>
        {
            if (s.Lines.Count > 0 && !await ConfirmAsync(T("Убрать все товары из приёма?", "Бардык товарларды кабыл алуудан алып салуу керекпи?", "Remove all products from receiving?",
                    "Tüm ürünler kabulden çıkarılsın mı?", "Barcha mahsulotlar qabuldan olib tashlansinmi?"), T("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash")).ConfigureAwait(true))
                return;
            s.Lines.Clear();
            ReceiveRender();
        };
        Grid.SetColumn(clear, 4);
        tools.Children.Add(clear);
        var toolsBox = new StackPanel { Children = { tools } };
        s.SuggestBox = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 8), IsVisible = false, Child = s.Suggest };
        Use(s.SuggestBox, Border.BackgroundProperty, "BrushPanel");
        Use(s.SuggestBox, Border.BorderBrushProperty, "BrushBorder");
        toolsBox.Children.Add(s.SuggestBox);
        toolsBox.Children.Add(ReceiveBuildGoodsPanel(s));
        // 2026-10-25 (Q4): плашка «Операция выполняется в фоне» — жёлто-нейтральная (фон BrushWarningSoft), обычные сообщения — как раньше, без фона.
        s.MessageBox = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), IsVisible = false, Child = s.Message };
        toolsBox.Children.Add(s.MessageBox);
        Grid.SetRow(toolsBox, 3);
        page.Children.Add(toolsBox);

        // Таблица товаров.
        s.Head = new Grid();
        var headBox = new Border { Padding = new Thickness(14, 8, 8, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = s.Head };
        Use(headBox, Border.BackgroundProperty, "BrushPanelSoft");
        Use(headBox, Border.BorderBrushProperty, "BrushBorder");
        var scroll = s.Scroll = new ScrollViewer { Content = s.Rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        table.Children.Add(headBox);
        Grid.SetRow(scroll, 1);
        table.Children.Add(scroll);
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = table };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Grid.SetRow(card, 5);
        page.Children.Add(card);

        // Низ: итог и одна жёлтая «Оприходовать товары» (шаг 3 — окно «Подтверждение оплаты»).
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        Use(s.Footer, TextBlock.ForegroundProperty, "BrushText");
        footer.Children.Add(s.Footer);
        // 2026-10-24, ТЗ «Склад» (Б2): «Обновить цены продажи (N)» — видна, пока есть строки с подорожанием и непоправленной продажей.
        s.Raise = UiKit.Ghost(this, "");
        s.Raise.Height = 48;
        s.Raise.Margin = new Thickness(0, 0, 10, 0);
        s.Raise.IsVisible = false;
        s.Raise.Click += async (_, _) => await ReceiveRaiseAsync(s).ConfigureAwait(true);
        Grid.SetColumn(s.Raise, 1);
        footer.Children.Add(s.Raise);
        s.Post = UiKit.Primary(this, T("Оприходовать товары", "Товарларды кирешке алуу", "Post the goods", "Malları kaydet", "Tovarlarni kirim qilish"));
        s.Post.Height = 48;
        s.Post.Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(s.Post, s.Post.Content as string);
        s.Post.Click += async (_, _) => await ReceivePostAsync().ConfigureAwait(true);
        Grid.SetColumn(s.Post, 2);
        footer.Children.Add(s.Post);
        Grid.SetRow(footer, 6);
        page.Children.Add(footer);

        page.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 1000;
            // Низкий экран (касса 1024×600): плитки прячутся — те же цифры в строке итога внизу.
            s.Tiles.IsVisible = e.NewSize.Height >= 600;
            // 2026-10-24: панель «Товары поставщика» — выше на большом окне, ниже на 1024×600 (таблице остаётся место).
            s.GoodsList.Height = e.NewSize.Height >= 700 ? 220 : e.NewSize.Height >= 520 ? 150 : 110;
            if (narrow == s.Narrow && s.Head.ColumnDefinitions.Count > 0)
                return;
            s.Narrow = narrow;
            title.FontSize = narrow ? 17 : 20;
            supplierGoods.Content = ReceiveGoodsButtonText(s);
            // 2026-10-24 (Б2): на узком окне (1024) подсказка поля скана короче — не обрезается.
            s.Scan.Watermark = narrow
                ? T("Штрихкод или название", "Штрихкод же аталышы", "Barcode or name", "Barkod veya ad", "Shtrix-kod yoki nomi")
                : T("Сканируйте штрихкод или найдите товар по названию", "Штрихкодду сканерлеңиз же товарды аты боюнча табыңыз",
                    "Scan a barcode or find a product by name", "Barkodu okutun veya ürünü adıyla bulun", "Shtrix-kodni skanerlang yoki mahsulotni nomi bo'yicha toping");
            ReceiveRender();
        };
        // 2026-10-24, ТЗ «Склад» (Б2): скан во время правки ячейки — как в Склад → Приёмка: правка остаётся как была, штрихкод идёт в приём.
        page.AddHandler(InputElement.KeyDownEvent, (_, e) => ReceiveFieldScanKey(s, e), RoutingStrategies.Tunnel);
        ShowSub(page);

        if (prefill?.SupplierId is { Length: > 0 } sid)
        {
            var known = _filterSupplier.All.FirstOrDefault(x => x.Id == sid)
                        ?? new SupplierInfo(sid, prefill.SupplierName ?? "", "", "", "", "", "", "", "", "", null);
            s.Picker.Select(known, raise: false);
            _ = ReceiveLoadGoodsAsync(s, known);
        }
        // Из «Пополнения» приходит только название поставщика — выбираем его, когда список загрузится.
        var byName = prefill?.SupplierId is not { Length: > 0 } && prefill?.SupplierName is { Length: > 0 } ? prefill.SupplierName : null;
        if (s.Picker.All.Count == 0)
            _ = s.Picker.LoadAsync().ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectByName(s, byName)), TaskScheduler.Default);
        else
            SelectByName(s, byName);
        ReceiveRender();
        if (prefill is { Lines.Count: > 0 })
            _ = ReceivePrefillAsync(prefill);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => s.Scan.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        PosLogger.Log($"Закупки: открыт приём товара{(prefill is null ? "" : $" ({prefill.Lines.Count} строк заранее{(prefill.OrderNumber is { } n ? ", заказ " + n : "")})")}.", "UI");
    }

    private void SelectByName(ReceiveState s, string? name)
    {
        if (name is null || s.Picker.Selected is not null || _receive != s)
            return;
        if (s.Picker.All.FirstOrDefault(x => string.Equals(x.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase)) is { } found)
            s.Picker.Select(found);
    }

    private async Task ReceivePrefillAsync(ReceivePrefill prefill)
    {
        if (_receive is not { } s)
            return;
        foreach (var l in prefill.Lines)
        {
            var line = await LineForProductAsync(l.ProductId, l.Name, l.Unit, l.Qty, l.Price).ConfigureAwait(true);
            if (_receive != s)
                return;
            if (line is not null && s.Lines.All(x => !string.Equals(x.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)))
                s.Lines.Add(line);
        }
        ReceiveRender();
    }

    /// <summary>Строка приёма для товара склада: из каталога кассы (остаток, цены, штрихкод) или с сервера (товара в каталоге ещё нет).</summary>
    private static async Task<ReceivingLineVm?> LineForProductAsync(string productId, string name, string unit, double qty, double? purchasePrice)
    {
        var tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
        if (tile is not null)
        {
            var line = PurchaseReceivingService.FromTile(tile, qty);
            if (purchasePrice is > 0 and var pp)
                line.PurchasePrice = pp;
            return line;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (await PosApp.CatalogApi.ProductsDetailAsync(productId, cts.Token).ConfigureAwait(true) is { ValueKind: JsonValueKind.Object } p)
            {
                var price = SupplierApiService.Num(p, "price");
                var purchase = SupplierApiService.Num(p, "purchase_price");
                var title = SupplierApiService.Str(p, "name") is { Length: > 0 } n ? n : name;
                var u = SupplierApiService.Str(p, "unit") is { Length: > 0 } un ? un : unit.Length > 0 ? unit : "шт";
                return new ReceivingLineVm
                {
                    ProductId = productId, Barcode = SupplierApiService.Str(p, "barcode"), ProductName = title, Unit = u, OriginalName = title, OriginalUnit = u,
                    Source = ReceivingSource.Warehouse, StockBefore = SupplierApiService.Num(p, "quantity"), Quantity = qty,
                    PurchasePrice = purchasePrice is > 0 and var given ? given : purchase, SalePrice = price, OriginalPurchasePrice = purchase, OriginalSalePrice = price,
                    OriginalMarkupPercent = SupplierApiService.Num(p, "markup_percent"), // 2026-10-24 (Б2): запасной источник прежней наценки для «Обновить цены продажи»
                };
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: товар {productId} не прочитан ({ex.Message}).", "WARNING");
        }
        return null;
    }

    // ------------------------------------------------------------------ добавление товаров

    /// <summary>Подсказка про порядок работы (всплывающая у поля скана и текст пустого списка): скан первым, поставщика можно выбрать позже.</summary>
    private static string ReceiveHintText() => T(
        "Сканируйте товары по накладной. После скана курсор встаёт в «Количество»: число → Enter → цена закупки → Enter → цена продажи → Enter — и снова скан. Поставщика можно выбрать в любой момент до «Оприходовать товары». Незнакомый штрихкод — новая строка: впишите название.",
        "Накладная боюнча товарларды сканерлеңиз. Сканерден кийин курсор «Саны» уячасына тургандыктан: сан → Enter → сатып алуу баасы → Enter → сатуу баасы → Enter — жана кайра скан. Жеткирүүчүнү «Товарларды кирешке алуу» басылганга чейин каалаган убакта тандаса болот. Белгисиз штрихкод — жаңы сап: атын жазыңыз.",
        "Scan the items of the delivery note. After a scan the cursor lands in “Quantity”: number → Enter → purchase price → Enter → selling price → Enter — and scan again. You can pick the supplier at any moment before “Post the goods”. An unknown barcode is a new line: type its name.",
        "İrsaliyedeki ürünleri okutun. Okutmadan sonra imleç «Miktar» hücresinde olur: sayı → Enter → alış fiyatı → Enter → satış fiyatı → Enter — ve yine okutma. Tedarikçiyi «Malları kaydet»ten önce istediğiniz an seçebilirsiniz. Bilinmeyen barkod — yeni satır: adını yazın.",
        "Nakladnoy bo'yicha mahsulotlarni skanerlang. Skanerdan keyin kursor «Miqdor» katakchasida turadi: son → Enter → xarid narxi → Enter → sotuv narxi → Enter — va yana skaner. Yetkazib beruvchini «Tovarlarni kirim qilish»dan oldin istalgan paytda tanlash mumkin. Notanish shtrix-kod — yangi qator: nomini yozing.");

    /// <summary>Enter в поле: штрихкод/код — общий поиск кассы (этот ПК, затем сервер, затем общая база CRM и «новый товар»), иначе — первый
    /// найденный по названию. 2026-10-24 (Б2): строка встаёт СВЕРХУ, курсор — в «Количество» (повторный скан — +1, строка поднимается и подсвечивается).</summary>
    private async Task ReceiveAddByCodeAsync(string text)
    {
        if (_receive is not { } s || text.Length == 0)
            return;
        ReceiveMessage(null);
        var code = ProductCodeLookup.Normalize(text);
        var local = ProductCodeLookup.FindLocal(code, allowWeightLabel: false)?.Product;
        if (local is not null)
        {
            ReceiveAddTile(local, 1);
            s.Scan.Text = "";
            return;
        }
        var looksLikeCode = code.Length >= 4 && code.All(char.IsLetterOrDigit) && code.Count(char.IsDigit) >= Math.Max(4, code.Length - 2);
        if (!looksLikeCode)
        {
            if (ReceiveMatches(text, s.SupplierIds).FirstOrDefault() is { } byName)
            {
                ReceiveAddTile(byName, 1);
                s.Scan.Text = "";
            }
            else
                ReceiveMessage(T($"«{text}» не найден. Отсканируйте штрихкод — новый товар можно завести прямо здесь.", $"«{text}» табылган жок. Штрихкодду сканерлеңиз — жаңы товарды ушул жерде түзсө болот.",
                    $"“{text}” not found. Scan the barcode — a new product can be created right here.", $"«{text}» bulunamadı. Barkodu okutun — yeni ürün burada oluşturulabilir.",
                    $"«{text}» topilmadi. Shtrix-kodni skanerlang — yangi mahsulotni shu yerda yaratish mumkin."), warning: true);
            return;
        }
        s.Scan.IsEnabled = false;
        try
        {
            // Сервер (товар добавили на сайте минуту назад, доп. штрихкод), затем общая база CRM, затем «новый товар» — как «Приёмка» склада.
            var server = await ProductCodeLookup.FindOnServerAsync(code).ConfigureAwait(true);
            if (server.Hit?.Product is { } found)
            {
                ReceiveAddTile(found, 1);
                s.Scan.Text = "";
                return;
            }
            var line = await PurchaseReceivingService.Instance.LookupAsync(code, 1).ConfigureAwait(true);
            if (_receive != s)
                return;
            var target = line;
            var added = true;
            if (line.ProductId is { Length: > 0 } && s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                same.Quantity += 1;
                target = same;
                added = false;
            }
            else if (line.IsNew && s.Lines.FirstOrDefault(x => x.IsNew && string.Equals(x.Barcode, line.Barcode, StringComparison.OrdinalIgnoreCase)) is { } sameNew)
            {
                sameNew.Quantity += 1;
                target = sameNew;
                added = false;
            }
            if (added && line.IsNew)
                ReceiveMessage(T("Новый товар: впишите название, цену закупки и цену продажи — он появится на складе при оприходовании.",
                    "Жаңы товар: атын, сатып алуу жана сатуу баасын жазыңыз — кирешке алганда кампада пайда болот.",
                    "New product: enter the name, purchase and selling price — it appears in stock when posted.",
                    "Yeni ürün: adı, alış ve satış fiyatını girin — kaydedildiğinde stokta görünür.",
                    "Yangi mahsulot: nomi, xarid va sotuv narxini kiriting — kirim qilinganda omborda paydo bo'ladi."));
            s.Scan.Text = "";
            ReceiveShowTop(s, target, false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: скан {code} не обработан ({ex.Message}).", "WARNING");
            ReceiveMessage(T("Не удалось найти товар: ", "Товар табылган жок: ", "Couldn't find the product: ", "Ürün bulunamadı: ", "Mahsulot topilmadi: ") + ex.Message, warning: true);
        }
        finally
        {
            s.Scan.IsEnabled = true;
            s.Scan.Focus();
        }
    }

    /// <summary>2026-10-24 (Б2): при выбранном поставщике его товары идут в подсказках первыми.</summary>
    private static IEnumerable<CatalogProductTileVm> ReceiveMatches(string text, HashSet<string>? firstIds = null)
    {
        var q = text.Trim();
        if (q.Length < 2)
            return Enumerable.Empty<CatalogProductTileVm>();
        return CatalogCacheService.Products.Where(p => p.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                                       || (p.Barcode ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                                       || (p.Article ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => firstIds is { Count: > 0 } && firstIds.Contains(p.Id) ? 0 : 1)
            .ThenBy(p => p.Title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).Take(8);
    }

    /// <summary>Подсказки под полем: до 8 товаров по названию/штрихкоду (нажатие — в приём).</summary>
    private void ReceiveSuggest()
    {
        if (_receive is not { } s)
            return;
        s.Suggest.Children.Clear();
        var text = (s.Scan.Text ?? "").Trim();
        var digitsOnly = text.Length > 0 && text.All(char.IsDigit);
        var matches = digitsOnly && text.Length >= 8 ? new List<CatalogProductTileVm>() : ReceiveMatches(text, s.SupplierIds).ToList();
        foreach (var p in matches)
        {
            var name = new TextBlock { Text = p.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            var meta = new TextBlock { Text = $"{(string.IsNullOrWhiteSpace(p.Barcode) ? "" : p.Barcode + " · ")}{T("остаток", "калдык", "stock", "stok", "qoldiq")} {Q(p.Quantity)}", FontSize = 12 };
            Use(meta, TextBlock.ForegroundProperty, "BrushTextSoft");
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            content.Children.Add(new StackPanel { Spacing = 1, Children = { name, meta } });
            // 2026-10-24 (Б2): значок «поставщик» — товар выбранного поставщика.
            if (s.SupplierIds.Contains(p.Id))
            {
                var badge = SectionCards.Pill(this, T("поставщик", "жеткирүүчү", "supplier", "tedarikçi", "yetkazib beruvchi"), "BrushSuccessSoft", "BrushSuccess");
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(badge, 1);
                content.Children.Add(badge);
            }
            var b = new Button
            {
                Content = content, MinHeight = 44, Padding = new Thickness(10, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(8),
            };
            Avalonia.Automation.AutomationProperties.SetName(b, p.Title);
            b.Click += (_, _) =>
            {
                s.Scan.Text = "";
                ReceiveAddTile(p, 1);
            };
            s.Suggest.Children.Add(b);
        }
        s.SuggestBox.IsVisible = matches.Count > 0;
    }

    private void ReceiveAddTile(CatalogProductTileVm tile, double qty, bool fromPanel = false)
    {
        if (_receive is not { } s)
            return;
        var line = s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, tile.Id, StringComparison.OrdinalIgnoreCase));
        if (line is not null)
            line.Quantity += qty;
        else
            line = PurchaseReceivingService.FromTile(tile, qty);
        ReceiveShowTop(s, line, fromPanel);
    }

    // ------------------------------------------------------------------ 2026-10-24, ТЗ «Склад» (Б2): строка сверху, курсор по ячейкам, подсветка

    /// <summary>Строка (новая или найденная повторным сканом) встаёт первой в списке: курсор — в «Количество» с выделенным числом (у нового товара без
    /// названия — в «Название»), строка 1,5 с подсвечена. Флажок «Сразу к скану» оставляет курсор в поле скана.</summary>
    private void ReceiveShowTop(ReceiveState s, ReceivingLineVm line, bool fromPanel)
    {
        s.AddedFromPanel = fromPanel;
        s.SuggestBox.IsVisible = false;
        var shown = s.RowMap.TryGetValue(line, out var parts);
        var index = s.Lines.IndexOf(line);
        if (index > 0)
            s.Lines.RemoveAt(index);
        if (index != 0)
            s.Lines.Insert(0, line);
        if (shown)
        {
            var at = s.Rows.Children.IndexOf(parts!.Box);
            if (at > 0)
            {
                s.Rows.Children.RemoveAt(at);
                s.Rows.Children.Insert(0, parts.Box);
            }
            s.Scroll.ScrollToHome();
            ReceiveRefresh();
        }
        else if (s.RowMap.Count == 0 || s.RowMap.Count != s.Lines.Count - 1)
            ReceiveRender(); // первая строка заменяет заглушку «Отсканируйте товары…» (или список разошёлся с таблицей — пересобираем)
        else
        {
            s.Rows.Children.Insert(0, ReceiveRow(line));
            s.Scroll.ScrollToHome();
            ReceiveRefresh();
        }
        ReceiveHighlight(s, line);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_receive != s || !s.Lines.Contains(line))
                return;
            if (s.FastNext.IsChecked == true)
                ReceiveFocusScan(s);
            else
                ReceiveFocusCell(s, line);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void ReceiveFocusCell(ReceiveState s, ReceivingLineVm line)
    {
        if (!s.RowMap.TryGetValue(line, out var p))
            return;
        p.Refresh(true);
        var box = line.IsNew && string.IsNullOrWhiteSpace(line.ProductName) && p.Name is not null ? p.Name : p.Qty;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>Конец цепочки Enter: снова в поле скана (если товар добавлен из панели поставщика и она открыта — в её поиск).</summary>
    private void ReceiveFocusScan(ReceiveState s)
    {
        var target = s.AddedFromPanel && s.GoodsOpen ? s.GoodsSearch : s.Scan;
        target.Focus();
        target.SelectAll();
    }

    private void ReceiveHighlight(ReceiveState s, ReceivingLineVm line)
    {
        if (s.Highlight is { } old && !ReferenceEquals(old, line) && s.RowMap.TryGetValue(old, out var po))
            ReceiveClearHighlight(po);
        s.Highlight = line;
        if (s.RowMap.TryGetValue(line, out var p))
            ReceiveApplyHighlight(p);
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (_receive != s || !ReferenceEquals(s.Highlight, line))
                return;
            s.Highlight = null;
            if (s.RowMap.TryGetValue(line, out var cur))
                ReceiveClearHighlight(cur);
        }, TimeSpan.FromMilliseconds(1500));
    }

    private static void ReceiveApplyHighlight(RowParts p)
    {
        p.HighlightBinding?.Dispose();
        p.HighlightBinding = p.Box.Bind(Border.BackgroundProperty, p.Box.GetResourceObservable("BrushWarningSoft"));
    }

    private static void ReceiveClearHighlight(RowParts p)
    {
        p.HighlightBinding?.Dispose();
        p.HighlightBinding = null;
        p.Box.ClearValue(Border.BackgroundProperty);
    }

    // ------------------------------------------------------------------ скан во время правки ячейки

    private static void ReceiveFieldScanReset(ReceiveState s)
    {
        s.FsBuf = "";
        s.FsRun = 0;
        s.FsBox = null;
        s.FsSnap = null;
    }

    /// <summary>2026-10-24 (Б2), как в Склад → Приёмка: штрихкод не должен печататься в «Количество»/цену. Скан в ячейке — только быстрая серия
    /// (символы чаще раза в 50 мс, ≥4 символов) и Enter сразу за ней; тогда ячейка возвращается к тексту до серии и штрихкод идёт в приём.
    /// Обычный ввод человека (пауза между клавишами больше 50 мс) не затрагивается.</summary>
    private void ReceiveFieldScanKey(ReceiveState s, KeyEventArgs e)
    {
        if (_receive != s)
            return;
        if (e.Source is not TextBox box || !Equals(box.Tag, "rcv-cell"))
        {
            ReceiveFieldScanReset(s);
            return;
        }
        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta))
            return;

        var now = Environment.TickCount64;
        // Пауза — по времени нажатия клавиши, а не обработки (пачка клавиш в очереди не выглядит «быстрой»), как в AvaloniaKeyboardWedgeBarcodeService.
        var raw = AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestamp();
        var sinceLast = raw is { } rawTs && s.FsRaw is { } lastRawTs && rawTs >= lastRawTs
            ? (long)Math.Min(rawTs - lastRawTs, long.MaxValue)
            : now - s.FsTick;
        var sameBurst = s.FsBuf.Length > 0 && ReferenceEquals(box, s.FsBox);
        var fast = sameBurst && sinceLast is >= 0 and <= 50;
        s.FsTick = now;
        s.FsRaw = raw;

        if (e.Key == Key.Enter)
        {
            if (sameBurst && sinceLast is >= 0 and <= 150 && s.FsBuf.Length >= 4 && s.FsRun >= 2)
            {
                e.Handled = true;
                var code = s.FsBuf;
                box.Text = s.FsSnap;
                box.CaretIndex = Math.Min(s.FsCaret, box.Text?.Length ?? 0);
                ReceiveFieldScanReset(s);
                _ = ReceiveAddByCodeAsync(code);
                return;
            }
            ReceiveFieldScanReset(s);
            return;
        }

        var symbol = e.KeySymbol;
        if (symbol is not { Length: 1 } || !(char.IsLetterOrDigit(symbol[0]) || symbol[0] == '-'))
        {
            ReceiveFieldScanReset(s);
            return;
        }
        if (fast)
            s.FsRun++;
        else
        {
            s.FsBuf = "";
            s.FsRun = 0;
            s.FsBox = box;
            s.FsSnap = box.Text;
            s.FsCaret = box.CaretIndex;
        }
        s.FsBuf += symbol;
    }

    // ------------------------------------------------------------------ «Товары поставщика»: панель с поиском вместо добавления всех строк

    private string ReceiveGoodsButtonText(ReceiveState s)
    {
        if (s.GoodsLoading)
            return T("Товары поставщика…", "Жеткирүүчүнүн товарлары…", "Supplier's products…", "Tedarikçi ürünleri…", "Yetkazib beruvchi mahsulotlari…");
        var n = s.Picker?.Selected is not null && s.GoodsRows.Count > 0 ? $" ({s.GoodsRows.Count})" : "";
        return s.Narrow
            ? T("Товары пост." + n, "Жеткир. товары" + n, "Supplier items" + n, "Ted. ürünleri" + n, "Yetk. mahsulot" + n)
            : T("Товары поставщика" + n, "Жеткирүүчүнүн товарлары" + n, "Supplier's products" + n, "Tedarikçi ürünleri" + n, "Yetkazib beruvchi mahsulotlari" + n);
    }

    private void ReceiveGoodsButton(ReceiveState s)
    {
        s.SupplierGoods.Content = ReceiveGoodsButtonText(s);
        s.SupplierGoods.IsEnabled = !s.GoodsLoading;
    }

    private Control ReceiveBuildGoodsPanel(ReceiveState s)
    {
        s.GoodsSearch = UiKit.Input(this, T("Найти товар поставщика: название или штрихкод — Enter добавит первый",
            "Жеткирүүчүнүн товарын табуу: аталышы же штрихкод — Enter биринчисин кошот", "Find a supplier product: name or barcode — Enter adds the first",
            "Tedarikçi ürününü bul: ad veya barkod — Enter ilkini ekler", "Yetkazib beruvchi mahsulotini topish: nomi yoki shtrix-kod — Enter birinchisini qo'shadi"), 40);
        Avalonia.Automation.AutomationProperties.SetName(s.GoodsSearch, s.GoodsSearch.Watermark);
        s.GoodsSearch.TextChanged += (_, _) => ReceiveApplyGoodsFilter(s);
        s.GoodsSearch.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            if (s.GoodsList.ItemsSource is IEnumerable<SupplierGoodsRow> found && found.FirstOrDefault() is { } first)
                _ = ReceiveAddGoodsAsync(s, first);
        };
        // Список виртуализирован (ListBox): 600 и больше товаров не строят 600 строк с полями ввода.
        s.GoodsList = new ListBox
        {
            Height = 150, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<SupplierGoodsRow>((r, _) => ReceiveGoodsRowView(r), true),
        };
        s.GoodsList.SelectionChanged += (_, _) =>
        {
            if (s.GoodsPicking || s.GoodsList.SelectedItem is not SupplierGoodsRow row)
                return;
            s.GoodsPicking = true;
            try
            {
                s.GoodsList.SelectedItem = null; // нажатие = «добавить»; следующее нажатие по тому же товару сработает снова
            }
            finally
            {
                s.GoodsPicking = false;
            }
            _ = ReceiveAddGoodsAsync(s, row);
        };
        var close = new Button
        {
            Content = "✕", Width = 36, Height = 36, MinHeight = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Use(close, Button.ForegroundProperty, "BrushTextSoft");
        Avalonia.Automation.AutomationProperties.SetName(close, T("Закрыть список товаров поставщика", "Жеткирүүчүнүн товарларынын тизмесин жабуу", "Close the supplier's products list", "Tedarikçi ürün listesini kapat", "Yetkazib beruvchi mahsulotlari ro'yxatini yopish"));
        close.Click += (_, _) => ReceiveToggleGoods(s);
        Use(s.GoodsTitle, TextBlock.ForegroundProperty, "BrushTextSoft");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(s.GoodsTitle);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        s.GoodsPanel = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 8), IsVisible = false,
            Child = new StackPanel { Spacing = 6, Children = { head, s.GoodsSearch, s.GoodsList } },
        };
        Use(s.GoodsPanel, Border.BackgroundProperty, "BrushPanel");
        Use(s.GoodsPanel, Border.BorderBrushProperty, "BrushBorder");
        return s.GoodsPanel;
    }

    private Control ReceiveGoodsRowView(SupplierGoodsRow r)
    {
        var name = new TextBlock { Text = r.Name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        var info = new TextBlock { Text = r.Info, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(info, TextBlock.ForegroundProperty, "BrushTextSoft");
        var plus = new TextBlock { Text = "＋", FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
        Use(plus, TextBlock.ForegroundProperty, "BrushText");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 40 };
        g.Children.Add(new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { name, info } });
        Grid.SetColumn(plus, 1);
        g.Children.Add(plus);
        Avalonia.Automation.AutomationProperties.SetName(g, r.Name);
        return g;
    }

    private void ReceiveApplyGoodsFilter(ReceiveState s)
    {
        var query = (s.GoodsSearch.Text ?? "").Trim();
        IEnumerable<SupplierGoodsRow> rows = s.GoodsRows;
        if (query.Length > 0)
            rows = rows.Where(r => r.SearchKey.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        s.GoodsList.ItemsSource = rows.ToList();
    }

    /// <summary>Кнопка «Товары поставщика (N)»: открыть/закрыть панель.</summary>
    private void ReceiveToggleGoods(ReceiveState s)
    {
        if (s.Picker.Selected is null)
        {
            ReceiveMessage(T("Сначала выберите поставщика.", "Адегенде жеткирүүчүнү тандаңыз.", "Choose a supplier first.", "Önce tedarikçi seçin.", "Avval yetkazib beruvchini tanlang."), warning: true);
            return;
        }
        if (s.GoodsLoading)
            return;
        ReceiveMessage(null);
        s.GoodsOpen = !s.GoodsOpen;
        s.GoodsPanel.IsVisible = s.GoodsOpen;
        if (s.GoodsOpen)
            s.GoodsSearch.Focus();
        else
            s.Scan.Focus();
    }

    /// <summary>Товары выбранного поставщика (suppliers/{id}/products/ и товары из его приходов) — в панель поиска. Строки приёма не добавляются:
    /// 600 товаров — это 600 строк списка панели, а не 600 строк с полями ввода (так сидели «по полчаса, ища нужную»).</summary>
    private async Task ReceiveLoadGoodsAsync(ReceiveState s, SupplierInfo? supplier)
    {
        s.GoodsCts?.Cancel();
        s.GoodsRows = new List<SupplierGoodsRow>();
        s.SupplierIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        s.GoodsList.ItemsSource = null;
        s.GoodsSearch.Text = "";
        s.GoodsOpen = false;
        s.GoodsPanel.IsVisible = false;
        if (supplier is null)
        {
            s.GoodsLoading = false;
            ReceiveGoodsButton(s);
            return;
        }
        var cts = s.GoodsCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        s.GoodsLoading = true;
        ReceiveGoodsButton(s);
        var productsTask = _svc.GetProductsAsync(supplier.Id, cts.Token);
        var receiptsTask = PurchaseReceivingService.Instance.LoadSupplierProductsAsync(new PurchaseReceivingService.Supplier(supplier.Id, supplier.Name), cts.Token);
        var products = new List<SupplierProduct>();
        var fromReceipts = new List<PurchaseReceivingService.SupplierProduct>();
        var failed = 0;
        string? failText = null;
        try
        {
            products = await productsTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failed++;
            failText = ex.Message;
            if (!cts.IsCancellationRequested)
                PosLogger.Log($"Закупки: товары «{supplier.Name}» не загружены ({ex.Message}).", "WARNING");
        }
        try
        {
            fromReceipts = await receiptsTask.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            failed++;
            failText ??= ex.Message;
            if (!cts.IsCancellationRequested)
                PosLogger.Log($"Закупки: товары из приходов «{supplier.Name}» не загружены ({ex.Message}).", "WARNING");
        }
        if (_receive != s || !ReferenceEquals(s.GoodsCts, cts))
            return;

        var catalog = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in CatalogCacheService.Products)
            catalog.TryAdd(t.Id, t);
        var rows = new List<SupplierGoodsRow>(fromReceipts.Count + products.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in fromReceipts) // уже свежие приходы первыми
        {
            if (r.ProductId.Length == 0 || !seen.Add(r.ProductId))
                continue;
            catalog.TryGetValue(r.ProductId, out var tile);
            var name = tile?.Title is { Length: > 0 } tt ? tt : r.Name;
            if (name.Length == 0)
                continue;
            var info = (r.LastAt.Year >= 2000 ? r.LastAt.ToLocalTime().ToString("dd.MM.yyyy") + " · " : "") + Q(r.LastQuantity) + (r.LastPurchasePrice > 0 ? " × " + r.LastPurchasePrice.ToString("0.##", Ru) : "");
            rows.Add(new SupplierGoodsRow(r.ProductId, name, tile?.Unit ?? "", r.LastPurchasePrice, info, $"{name} {tile?.Barcode} {tile?.Article}"));
        }
        foreach (var p in products.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (p.Id.Length == 0 || !seen.Add(p.Id))
                continue;
            catalog.TryGetValue(p.Id, out var tile);
            var name = tile?.Title is { Length: > 0 } tt ? tt : p.Name;
            if (name.Length == 0)
                continue;
            var info = (tile is not null ? T("остаток ", "калдык ", "stock ", "stok ", "qoldiq ") + Q(tile.Quantity) : "")
                       + (p.PurchasePrice > 0 ? (tile is not null ? " · " : "") + T("закупка ", "сатып алуу ", "purchase ", "alış ", "xarid ") + p.PurchasePrice.ToString("0.##", Ru) : "");
            rows.Add(new SupplierGoodsRow(p.Id, name, tile?.Unit ?? "", p.PurchasePrice, info, $"{name} {tile?.Barcode ?? p.Code} {tile?.Article ?? p.Article}"));
        }
        s.GoodsRows = rows;
        foreach (var row in rows)
            s.SupplierIds.Add(row.ProductId);
        s.GoodsLoading = false;
        ReceiveGoodsButton(s);
        s.GoodsTitle.Text = rows.Count > 0
            ? T($"Товары «{supplier.Name}» ({rows.Count}) — последний приход: дата · количество × цена закупки. Найдите нужный и нажмите:",
                $"«{supplier.Name}» товарлары ({rows.Count}) — акыркы кирешеси: күнү · саны × сатып алуу баасы. Керектүүсүн тапып басыңыз:",
                $"Products of “{supplier.Name}” ({rows.Count}) — last receipt: date · quantity × purchase price. Find the one you need and press it:",
                $"«{supplier.Name}» ürünleri ({rows.Count}) — son giriş: tarih · miktar × alış fiyatı. İhtiyacınız olanı bulup basın:",
                $"«{supplier.Name}» mahsulotlari ({rows.Count}) — oxirgi kirim: sana · soni × xarid narxi. Keraklisini topib bosing:")
            : failed == 2
                ? T("Товары поставщика не загрузились: ", "Жеткирүүчүнүн товарлары жүктөлгөн жок: ", "The supplier's products didn't load: ", "Tedarikçi ürünleri yüklenmedi: ", "Yetkazib beruvchi mahsulotlari yuklanmadi: ") + failText
                : T("У этого поставщика ещё нет товаров — отсканируйте их или найдите по названию.", "Бул жеткирүүчүнүн товарлары азырынча жок — аларды сканерлеңиз же аты боюнча табыңыз.",
                    "This supplier has no products yet — scan them or find them by name.", "Bu tedarikçinin henüz ürünü yok — okutun veya adıyla bulun.",
                    "Bu yetkazib beruvchining mahsulotlari hali yo'q — skanerlang yoki nomi bo'yicha toping.");
        ReceiveApplyGoodsFilter(s);
    }

    /// <summary>Нажатие по товару в панели: строка сверху (повторное нажатие — +1), закупка — последняя у этого поставщика, если у товара своей ещё нет;
    /// курсор — в «Количество».</summary>
    private async Task ReceiveAddGoodsAsync(ReceiveState s, SupplierGoodsRow row)
    {
        try
        {
            var line = s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, row.ProductId, StringComparison.OrdinalIgnoreCase));
            if (line is not null)
                line.Quantity += 1;
            else
            {
                line = await LineForProductAsync(row.ProductId, row.Name, row.Unit, 1, null).ConfigureAwait(true);
                if (_receive != s)
                    return;
                if (line is null)
                {
                    ReceiveMessage(T("Товар не удалось прочитать — попробуйте ещё раз.", "Товарды окуу мүмкүн болгон жок — дагы аракет кылыңыз.", "Couldn't read the product — try again.",
                        "Ürün okunamadı — tekrar deneyin.", "Mahsulotni o'qib bo'lmadi — qayta urinib ko'ring."), warning: true);
                    return;
                }
                if (s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)) is { } raced)
                {
                    raced.Quantity += 1;
                    line = raced;
                }
                else if (row.Price > 0 && line.PurchasePrice <= 0)
                    line.PurchasePrice = row.Price;
            }
            s.GoodsSearch.Text = "";
            ReceiveShowTop(s, line, true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: товар поставщика {row.ProductId} не добавлен ({ex.Message}).", "WARNING");
        }
    }

    // ------------------------------------------------------------------ подорожание: «Обновить цены продажи» и подтверждение перед «Оприходовать»

    private async Task ReceiveRaiseAsync(ReceiveState s)
    {
        var dialog = new ReceivingRaiseDialog(s.Lines.Where(l => l.Quantity > 0).ToList());
        await dialog.ShowDialog(this).ConfigureAwait(true);
        if (dialog.AppliedCount > 0)
            PosLogger.Log($"Закупки: «Обновить цены продажи» — {dialog.AppliedCount} строк.", "STOCK");
        if (_receive == s)
            ReceiveRefresh();
    }

    /// <summary>Перед «Оприходовать»: «Цена продажи изменится у K товаров» со списком (прежняя → новая). false — кассир передумал.</summary>
    private Task<bool> ReceiveConfirmPriceChangesAsync(IReadOnlyList<ReceivingLineVm> changed)
    {
        var shown = changed.Take(12).Select(l =>
            $"• {(string.IsNullOrWhiteSpace(l.ProductName) ? l.Barcode : l.ProductName)}: {l.OriginalSalePrice:0.##} → {l.SalePrice:0.##}").ToList();
        if (changed.Count > shown.Count)
            shown.Add(T($"…и ещё {changed.Count - shown.Count}", $"…жана дагы {changed.Count - shown.Count}", $"…and {changed.Count - shown.Count} more",
                $"…ve {changed.Count - shown.Count} tane daha", $"…va yana {changed.Count - shown.Count}"));
        var message = T($"Цена продажи изменится у {changed.Count} товаров:", $"{changed.Count} товардын сатуу баасы өзгөрөт:",
                $"The sale price will change for {changed.Count} products:", $"{changed.Count} ürünün satış fiyatı değişecek:",
                $"{changed.Count} ta mahsulotning sotuv narxi o'zgaradi:")
            + "\n\n" + string.Join("\n", shown);
        return PosConfirmDialog.ShowModalAsync(this,
            T("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"), message,
            T("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"),
            T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Primary);
    }

    private void ReceiveMessage(string? text, bool warning = false, bool notice = false)
    {
        if (_receive is not { } s)
            return;
        s.Message.IsVisible = !string.IsNullOrEmpty(text);
        s.MessageBox.IsVisible = s.Message.IsVisible;
        s.Message.Text = text ?? "";
        Use(s.Message, TextBlock.ForegroundProperty, notice ? "BrushText" : warning ? "BrushDanger" : "BrushTextSoft");
        // notice — нейтральная плашка «в фоне» (не красная): фон и поля; без неё сообщение выглядит как раньше.
        s.MessageBox.Padding = notice ? new Thickness(12, 8) : new Thickness(0);
        if (notice)
            Use(s.MessageBox, Border.BackgroundProperty, "BrushWarningSoft");
        else
            s.MessageBox.ClearValue(Border.BackgroundProperty);
    }

    // ------------------------------------------------------------------ таблица

    // 2026-10-24, ТЗ «Склад» (Б2): колонки как в Склад → Приёмка — Товар · Остаток · Количество · Закупка · Наценка % · Продажа · Сумма · ✕.
    private string ReceiveColumns => _receive?.Narrow == true ? "2*,0,0.95*,1.15*,0.95*,1.15*,1*,Auto" : "2.3*,0.7*,0.95*,1.15*,0.95*,1.15*,1.1*,Auto";

    private void ReceiveRefresh()
    {
        if (_receive is not { } s)
            return;
        ReceiveTiles();
        ReceiveSteps();
        ReceiveFooter();
    }

    private void ReceiveRender()
    {
        if (_receive is not { } s)
            return;
        s.Head.Children.Clear();
        s.Head.ColumnDefinitions = new ColumnDefinitions(ReceiveColumns);
        var heads = new[]
        {
            T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"), T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"),
            T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), T("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %"),
            T("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), "",
        };
        for (var i = 0; i < heads.Length; i++)
        {
            var t = new TextBlock { Text = heads[i], FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = i is 1 or 6 ? TextAlignment.Right : TextAlignment.Left, IsVisible = !(s.Narrow && i == 1), MinWidth = i == 7 ? 44 : 0 };
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, i);
            s.Head.Children.Add(t);
        }
        s.Rows.Children.Clear();
        s.RowMap.Clear();
        if (s.Lines.Count == 0)
        {
            s.Rows.Children.Add(SectionCards.Empty(this, ReceiveHintText() + " " + T("Или нажмите «Товары поставщика» — найдите нужный товар в списке, а не прокручивайте сотни строк.",
                "Же «Жеткирүүчүнүн товарларын» басыңыз — керектүү товарды тизмеден табыңыз, жүздөгөн саптарды сыдырбаңыз.",
                "Or press “Supplier's products” — find the product in the list instead of scrolling hundreds of lines.",
                "Veya «Tedarikçi ürünleri»ne basın — yüzlerce satırı kaydırmak yerine ürünü listeden bulun.",
                "Yoki «Yetkazib beruvchi mahsulotlari»ni bosing — yuzlab qatorni aylantirmasdan mahsulotni ro'yxatdan toping.")));
        }
        foreach (var line in s.Lines)
            s.Rows.Children.Add(ReceiveRow(line));
        ReceiveRefresh();
    }

    private static string ReceiveNum(double v) => v > 0 ? v.ToString("0.###", Ru) : "";

    private static string ReceiveMarkup(ReceivingLineVm line) => line.MarkupPercent is { } m ? m.ToString("0.##", Ru) : "";

    /// <summary>Читает число из ячейки (запятая или точка). Пустая ячейка — 0; непонятное число — false (строка остаётся как была).</summary>
    private static bool ReceiveReadCell(TextBox box, out double value)
    {
        value = 0;
        var t = (box.Text ?? "").Trim().Replace(" ", "").Replace(',', '.');
        if (t.Length == 0)
            return true;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || v >= 1e9)
            return false;
        value = Math.Round(v, 3);
        return true;
    }

    /// <summary>Ячейка-поле таблицы приёма (40 px, число справа). Tag «rcv-cell» — по нему сканер в ячейке узнаёт свои поля.</summary>
    private TextBox ReceiveCellBox(string text, string name)
    {
        var box = new TextBox
        {
            Text = text, Watermark = "0", Height = 40, MinHeight = 40, FontSize = 14, Margin = new Thickness(0, 0, 10, 0), Tag = "rcv-cell",
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        box.Classes.Add("num");
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        box.GotFocus += (_, _) => box.SelectAll();
        return box;
    }

    private Control ReceiveRow(ReceivingLineVm line)
    {
        var s = _receive!;
        var parts = new RowParts();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(ReceiveColumns) };
        // Товар: название (у нового — поле), штрихкод/код и пометка «новый».
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        if (line.IsNew)
        {
            var nameBox = new TextBox { Text = line.ProductName, Watermark = T("Название нового товара", "Жаңы товардын аты", "New product name", "Yeni ürün adı", "Yangi mahsulot nomi"), Height = 40, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, Tag = "rcv-cell" };
            Avalonia.Automation.AutomationProperties.SetName(nameBox, nameBox.Watermark);
            nameBox.TextChanged += (_, _) => line.ProductName = nameBox.Text ?? "";
            parts.Name = nameBox;
            who.Children.Add(nameBox);
        }
        else
        {
            var name = Cell(line.ProductName, "BrushText", 14, FontWeight.SemiBold);
            name.Margin = default;
            name.MaxLines = 2;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            who.Children.Add(name);
        }
        var meta = Cell((line.Barcode.Length > 0 ? line.Barcode : "—") + (line.IsNew ? " · " + T("новый товар", "жаңы товар", "new product", "yeni ürün", "yangi mahsulot") : "")
                        + (s.Narrow && !line.IsNew ? " · " + T("ост. ", "калд. ", "stock ", "stok ", "qold. ") + Q(line.StockBefore) : ""), line.IsNew ? "BrushDanger" : "BrushTextSoft", 12);
        meta.Margin = default;
        who.Children.Add(meta);
        g.Children.Add(who);

        var stock = Cell(line.IsNew ? "—" : Q(line.StockBefore) + " " + line.Unit, "BrushTextSoft", 13.5);
        stock.TextAlignment = TextAlignment.Right;
        stock.IsVisible = !s.Narrow;
        Grid.SetColumn(stock, 1);
        g.Children.Add(stock);

        string Lbl(string ru, string ky, string en, string tr, string uz) => T(ru, ky, en, tr, uz) + " " + line.ProductName;
        var qty = ReceiveCellBox(ReceiveNum(line.Quantity), Lbl("Количество", "Саны", "Quantity", "Miktar", "Miqdor"));
        var purchase = ReceiveCellBox(ReceiveNum(line.PurchasePrice), Lbl("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"));
        var markup = ReceiveCellBox(ReceiveMarkup(line), Lbl("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %"));
        var sale = ReceiveCellBox(ReceiveNum(line.SalePrice), Lbl("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi"));
        parts.Qty = qty;
        parts.Purchase = purchase;
        parts.Markup = markup;
        parts.Sale = sale;
        // Под закупкой — «▲ +12 %» (оранжевым) и «было 45,00»; под продажей — «маржа 4 %» (красным); значения те же, что в Склад → Приёмка (ReceivingLineVm).
        var raiseText = new TextBlock { FontSize = 11.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(raiseText, TextBlock.ForegroundProperty, "BrushWarning");
        var prevText = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(prevText, TextBlock.ForegroundProperty, "BrushTextSoft");
        var lowText = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(lowText, TextBlock.ForegroundProperty, "BrushDanger");
        var purchaseSub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Height = 15, Margin = new Thickness(0, -2, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, Children = { raiseText, prevText } };
        var saleSub = new StackPanel { Orientation = Orientation.Horizontal, Height = 15, Margin = new Thickness(0, -2, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, Children = { lowText } };
        var sum = Cell(MoneyText.Som(line.LineTotal), "BrushText", 13.5, FontWeight.SemiBold);
        sum.TextAlignment = TextAlignment.Right;

        // Обновление чисел строки без пересборки (курсор остаётся в поле): все поля, где сейчас не стоит курсор, показывают актуальные значения.
        void Sync(bool force)
        {
            parts.Syncing = true;
            try
            {
                void Put(TextBox b, string t)
                {
                    if ((force || !b.IsFocused) && b.Text != t)
                        b.Text = t;
                }
                Put(qty, ReceiveNum(line.Quantity));
                Put(purchase, ReceiveNum(line.PurchasePrice));
                Put(markup, ReceiveMarkup(line));
                Put(sale, ReceiveNum(line.SalePrice));
                sum.Text = MoneyText.Som(line.LineTotal);
                raiseText.Text = line.PurchaseRaiseText;
                prevText.Text = line.PreviousPurchaseText;
                lowText.Text = line.LowMarginText;
                if (parts.Box is { } rowBox)
                    rowBox.Opacity = line.Quantity <= 0 ? 0.72 : 1;
            }
            finally
            {
                parts.Syncing = false;
            }
        }
        parts.Refresh = Sync;
        Sync(true);

        // Ввод в ячейку сразу попадает в строку. Закупка при неизменной продаже пересчитывает наценку (продажа сама не меняется);
        // наценка меняет цену продажи (ReceivingLineVm.MarkupText: округление вверх до сома); цена продажи меняет наценку.
        qty.TextChanged += (_, _) =>
        {
            if (parts.Syncing || !ReceiveReadCell(qty, out var v))
                return;
            line.Quantity = v;
            ReceiveRefresh();
        };
        purchase.TextChanged += (_, _) =>
        {
            if (parts.Syncing || !ReceiveReadCell(purchase, out var v))
                return;
            line.PurchasePrice = v;
            ReceiveRefresh();
        };
        markup.TextChanged += (_, _) =>
        {
            if (parts.Syncing)
                return;
            var t = (markup.Text ?? "").Trim();
            if (t.Length == 0)
                return;
            line.MarkupText = t;
            ReceiveRefresh();
        };
        sale.TextChanged += (_, _) =>
        {
            if (parts.Syncing || !ReceiveReadCell(sale, out var v))
                return;
            line.SalePrice = v;
            ReceiveRefresh();
        };
        foreach (var cell in new[] { qty, purchase, markup, sale })
            cell.LostFocus += (_, _) => Sync(false);

        // Enter и Tab: «Название» → «Количество» → «Закупка» → «Цена продажи» → поле скана (наценку Enter пропускает, её правят мышью или Shift+Tab).
        void Chain(TextBox from, Func<TextBox?> next) => from.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Tab) || e.KeyModifiers != KeyModifiers.None)
                return;
            e.Handled = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_receive != s)
                    return;
                var to = next();
                if (to is null)
                    ReceiveFocusScan(s);
                else
                {
                    to.Focus();
                    to.SelectAll();
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel);
        if (parts.Name is { } nm)
            Chain(nm, () => qty);
        Chain(markup, () => sale);
        Chain(qty, () => purchase);
        Chain(purchase, () => sale);
        Chain(sale, () => null);

        Grid.SetColumn(qty, 2);
        g.Children.Add(qty);
        var purchaseBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { purchase, purchaseSub } };
        Grid.SetColumn(purchaseBox, 3);
        g.Children.Add(purchaseBox);
        Grid.SetColumn(markup, 4);
        g.Children.Add(markup);
        var saleBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { sale, saleSub } };
        Grid.SetColumn(saleBox, 5);
        g.Children.Add(saleBox);
        Grid.SetColumn(sum, 6);
        g.Children.Add(sum);

        var remove = new Button { Content = "✕", Width = 44, Height = 44, MinHeight = 44, Padding = new Thickness(0), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 15 };
        Use(remove, Button.ForegroundProperty, "BrushTextSoft");
        Avalonia.Automation.AutomationProperties.SetName(remove, T("Убрать строку", "Сапты алып салуу", "Remove the line", "Satırı kaldır", "Qatorni olib tashlash") + " " + line.ProductName);
        remove.Click += (_, _) =>
        {
            s.Lines.Remove(line);
            ReceiveRender();
        };
        Grid.SetColumn(remove, 7);
        g.Children.Add(remove);

        var box = new Border { Padding = new Thickness(14, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = g, MinHeight = 66 };
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        if (line.Quantity <= 0)
            box.Opacity = 0.72;
        parts.Box = box;
        s.RowMap[line] = parts;
        if (ReferenceEquals(s.Highlight, line))
            ReceiveApplyHighlight(parts);

        // Числа строки живут вместе с моделью (ячейка, «Обновить цены продажи», повторный скан); подписка — только пока строка на экране.
        System.ComponentModel.PropertyChangedEventHandler onChanged = (_, _) => Sync(false);
        box.AttachedToVisualTree += (_, _) => line.PropertyChanged += onChanged;
        box.DetachedFromVisualTree += (_, _) => line.PropertyChanged -= onChanged;
        return box;
    }

    /// <summary>Поле числа (запятая или точка), 40 px; изменение — сразу в строку.</summary>
    private TextBox NumberBox(double value, string name, Action<double> changed)
    {
        var box = new TextBox
        {
            Text = value > 0 ? value.ToString("0.###", Ru) : "", Watermark = "0", Height = 40, MinHeight = 40, FontSize = 14, Margin = new Thickness(0, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        box.Classes.Add("num");
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        box.TextChanged += (_, _) =>
        {
            var t = (box.Text ?? "").Trim().Replace(" ", "").Replace(',', '.');
            if (t.Length == 0)
                changed(0);
            else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 1e9)
                changed(Math.Round(v, 3));
        };
        box.GotFocus += (_, _) => box.SelectAll();
        return box;
    }

    private void ReceiveTiles()
    {
        if (_receive is not { } s)
            return;
        var active = s.Lines.Where(l => l.Quantity > 0).ToList();
        s.Tiles.Children.Clear();
        s.Tiles.Children.Add(UiKit.Kpi(T("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), active.Count.ToString(CultureInfo.InvariantCulture)));
        s.Tiles.Children.Add(UiKit.Kpi(T("Общее количество", "Жалпы саны", "Total quantity", "Toplam miktar", "Umumiy miqdor"), Q(active.Sum(l => l.Quantity))));
        s.Tiles.Children.Add(UiKit.Kpi(T("Сумма накладной", "Накладнойдун суммасы", "Invoice total", "Fatura toplamı", "Nakladnoy summasi"), MoneyText.Som(active.Sum(l => l.LineTotal))));
        // 2026-10-24 (Б2): «подорожало: N» — строк, где закупка выше прежней (как в итоге Склад → Приёмка).
        if (ReceivingPricing.CountRaised(active) is > 0 and var raised)
            s.Tiles.Children.Add(UiKit.Kpi(T("Подорожало", "Кымбаттаган", "Price rose", "Fiyatı arttı", "Qimmatlagan"), raised.ToString(CultureInfo.InvariantCulture)));
    }

    private void ReceiveSteps()
    {
        if (_receive is not { } s)
            return;
        s.Steps.Children.Clear();
        var hasSupplier = s.Picker.Selected is not null;
        var hasGoods = s.Lines.Any(l => l.Quantity > 0);
        foreach (var (n, text, done, current) in new[]
                 {
                     (1, T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), hasSupplier, !hasSupplier),
                     (2, T("Товары", "Товарлар", "Goods", "Ürünler", "Tovarlar"), hasGoods, hasSupplier && !hasGoods),
                     (3, T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), false, hasSupplier && hasGoods),
                 })
        {
            var pill = SectionCards.Pill(this, (done ? "✓ " : n + ". ") + text, done ? "BrushSuccessSoft" : current ? "BrushWarningSoft" : "BrushPanelSoft",
                done ? "BrushSuccess" : current ? "BrushText" : "BrushTextSoft");
            pill.Padding = new Thickness(12, 6);
            s.Steps.Children.Add(pill);
        }
    }

    private void ReceiveFooter()
    {
        if (_receive is not { } s)
            return;
        var active = s.Lines.Where(l => l.Quantity > 0).ToList();
        var raised = ReceivingPricing.CountRaised(active);
        var raisedText = raised > 0 ? T($" · подорожало: {raised}", $" · кымбаттаган: {raised}", $" · price rose: {raised}", $" · fiyatı arttı: {raised}", $" · qimmatlagan: {raised}") : "";
        s.Footer.Text = active.Count == 0
            ? s.Picker.Selected is null
                ? T("Сканируйте товары или выберите поставщика (шаг 1).", "Товарларды сканерлеңиз же жеткирүүчүнү тандаңыз (1-кадам).", "Scan the products or choose a supplier (step 1).", "Ürünleri okutun veya tedarikçi seçin (1. adım).", "Mahsulotlarni skanerlang yoki yetkazib beruvchini tanlang (1-qadam).")
                : T("Добавьте товары и впишите количество (шаг 2).", "Товарларды кошуп, санын жазыңыз (2-кадам).", "Add products and enter quantities (step 2).", "Ürün ekleyip miktar girin (2. adım).", "Mahsulot qo'shib, miqdorni kiriting (2-qadam).")
            : T($"К оприходованию: {active.Count} поз. на {MoneyText.Som(active.Sum(l => l.LineTotal))}", $"Кирешке: {active.Count} поз., {MoneyText.Som(active.Sum(l => l.LineTotal))}",
                $"To post: {active.Count} items, {MoneyText.Som(active.Sum(l => l.LineTotal))}", $"Kaydedilecek: {active.Count} kalem, {MoneyText.Som(active.Sum(l => l.LineTotal))}",
                $"Kirim: {active.Count} poz., {MoneyText.Som(active.Sum(l => l.LineTotal))}") + raisedText
              + (s.Picker.Selected is null ? T(" — выберите поставщика (шаг 1).", " — жеткирүүчүнү тандаңыз (1-кадам).", " — choose a supplier (step 1).", " — tedarikçi seçin (1. adım).", " — yetkazib beruvchini tanlang (1-qadam).") : "");
        // 2026-10-24 (Б2): «Обновить цены продажи (N)» — пока есть строки с подорожанием и непоправленной продажей.
        var raisable = PurchaseReceivingService.CountRaisableSalePrices(active);
        s.Raise.IsVisible = raisable > 0;
        if (raisable > 0)
        {
            s.Raise.Content = T($"Обновить цены продажи ({raisable})", $"Сатуу баасын жаңыртуу ({raisable})", $"Update sale prices ({raisable})", $"Satış fiyatlarını güncelle ({raisable})", $"Sotuv narxlarini yangilash ({raisable})");
            Avalonia.Automation.AutomationProperties.SetName(s.Raise, s.Raise.Content as string);
        }
        s.Post.IsEnabled = !s.Busy && s.Picker.Selected is not null && active.Count > 0;
    }

    // ------------------------------------------------------------------ оприходовать

    // 2026-10-25, ТЗ «оприходование в фоне» (Q4): «Оприходовать товары» → проверки и подтверждение цен → «Подтверждение оплаты» (способ оплаты, касса/ящик
    // смены, долг выбираются ДО постановки) → задача уходит в очередь BackgroundReceivingQueue (приход и оплата проводятся в фоне, переживают закрытие окна
    // и перезапуск), список приёма очищается сразу. s.Busy — только синхронная часть; дальше ждём итог не дольше 0,5 с: успели — диалог «Оприходование
    // завершено» как раньше, нет — плашка «Операция выполняется в фоне» и итог по JobFinished.
    private async Task ReceivePostAsync()
    {
        if (_receive is not { } s || s.Busy || s.Picker.Selected is not { } supplierInfo)
            return;
        s.Busy = true;
        ReceiveFooter();
        try
        {
            await ReceivePostCoreAsync(s, supplierInfo).ConfigureAwait(true);
        }
        finally
        {
            s.Busy = false;
            if (_receive == s)
                ReceiveFooter();
        }
    }

    private async Task ReceivePostCoreAsync(ReceiveState s, SupplierInfo supplierInfo)
    {
        var lines = s.Lines.Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0)
            return;
        var problems = PurchaseReceivingService.Validate(lines);
        if (problems.Count > 0)
        {
            ReceiveMessage(T("Нельзя оприходовать: ", "Кирешке алууга болбойт: ", "Can't post: ", "Kaydedilemez: ", "Kirim qilib bo'lmaydi: ") + string.Join("; ", problems.Take(4))
                           + (problems.Count > 4 ? T($" …и ещё {problems.Count - 4}", $" …жана дагы {problems.Count - 4}", $" …and {problems.Count - 4} more", $" …ve {problems.Count - 4} tane daha", $" …va yana {problems.Count - 4}") : ""), warning: true);
            return;
        }
        // 2026-10-24, ТЗ «Склад» (Б2): меняются цены продажи — короткое подтверждение со списком, как в Склад → Приёмка.
        if (PurchaseReceivingService.SalePriceChanges(lines) is { Count: > 0 } changedPrices
            && !await ReceiveConfirmPriceChangesAsync(changedPrices).ConfigureAwait(true))
            return;
        var total = Math.Round(lines.Sum(l => l.LineTotal), 2);
        var dialog = new PurchasePaymentDialog(supplierInfo.Name, lines.Count, total);
        await dialog.ShowDialog(this).ConfigureAwait(true);
        if (dialog.Result is not { } pay || _receive != s)
            return;
        var sinceConfirm = System.Diagnostics.Stopwatch.StartNew();

        // Оплата выбрана — всё нужное для финишера (смена, касса, срок долга) запоминаем в задаче сейчас: к концу фоновой работы смена могла смениться.
        var supplier = new PurchaseReceivingService.Supplier(supplierInfo.Id, supplierInfo.Name, supplierInfo.Inn, supplierInfo.Phone);
        var employee = PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId;
        var paidNow = pay.Kind is PurchasePayKind.Cash or PurchasePayKind.Drawer;
        var shiftId = PosApp.ActiveShiftId;
        Dictionary<string, string>? fields = null;
        if (pay.Kind == PurchasePayKind.Drawer)
            fields = new Dictionary<string, string>
            {
                ["cashbox_role"] = "pos_main",
                ["cashbox"] = PosApp.PosCashboxId ?? "",
                ["shift"] = shiftId ?? "",
            };
        PosLogger.Log($"Закупки: приход от «{supplier.Name}» ({supplier.Id}), строк {lines.Count}, оплата {pay.Kind}" +
                      (pay.Kind == PurchasePayKind.Partial ? $" — сразу {pay.PaidNow:0.00}{(pay.PartialFromDrawer ? " из ящика смены" : $" из кассы «{pay.CashboxName}»")}" : "") +
                      (pay.Kind is PurchasePayKind.Debt or PurchasePayKind.Partial ? $", срок {pay.DueDays} дн." : "") + " — в очередь фоновых приходов.", "PAYMENT");

        EnsureBackgroundSubscription();
        var postContent = s.Post.Content;
        s.Post.Content = T("Оприходую…", "Кирешке алынууда…", "Posting…", "Kaydediliyor…", "Kirim qilinmoqda…");
        string jobId;
        try
        {
            jobId = BackgroundReceivingQueue.Instance.Enqueue(new ReceivingJobRequest(ReceivingJobSource.Purchases, lines, supplier, paidNow, employee, pay.DueDays, fields,
                "purchases", PurchaseFinishPayload.From(pay, s.OrderId, shiftId)));
        }
        catch (Exception ex)
        {
            // Очередь закрыта или диск не принял запись: строки остаются в списке, кассир повторит.
            s.Post.Content = postContent;
            PosLogger.Log($"Закупки: приход не поставлен в очередь ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ReceiveMessage(ex is InvalidOperationException ? ex.Message
                : T("Не удалось поставить приход в очередь: ", "Кирүүнү кезекке коюу мүмкүн болгон жок: ", "Could not queue the delivery: ", "Mal kabul kuyruğa alınamadı: ", "Kirimni navbatga qo'yib bo'lmadi: ") + ex.Message, warning: true);
            return;
        }

        // Задача на диске — список очищаем сразу и готовимся к следующей накладной (заказ уже закрыт этой задачей — к новому приёму не привязываем).
        _bgWaiting.Add(jobId);
        foreach (var line in lines)
            s.Lines.Remove(line);
        if (s.OrderId is not null)
        {
            s.OrderId = null;
            s.OrderNumber = null;
            if (s.Title is not null)
                s.Title.Text = T("Приём товара", "Товар кабыл алуу", "Receive goods", "Mal kabul", "Tovar qabul qilish");
        }
        ReceiveMessage(null);
        ReceiveRender();
        ReceiveFocusScan(s);

        ReceivingJob? done;
        try
        {
            // 0,5 с отсчитываем от подтверждения оплаты: диск и перерисовка списка уже съели часть, индикатор не должен гореть дольше
            // (запас 40 мс на шаг системного таймера Windows ≈ 15 мс).
            var left = Math.Clamp(0.46 - sinceConfirm.Elapsed.TotalSeconds, 0.05, 0.46);
            done = await BackgroundReceivingQueue.Instance.WaitAsync(jobId, TimeSpan.FromSeconds(left)).ConfigureAwait(true);
            // Задача могла закончиться как раз на границе 0,5 с (обработчик JobFinished пропустил её, пока мы ждали) — смотрим ещё раз.
            if (done is null && BackgroundReceivingQueue.Instance.Get(jobId) is { IsFinal: true } late)
                done = late;
        }
        finally
        {
            _bgWaiting.Remove(jobId);
            s.Post.Content = postContent;
        }
        if (done is null)
        {
            if (_receive == s)
                ReceiveMessage(BackgroundNoticeText, notice: true);
            return;
        }
        _bgShown.Add(done.Id);
        await ReceiveShowResultAsync(s, done).ConfigureAwait(true);
    }

    /// <summary>«Операция выполняется в фоне — можно продолжать работу» (плашка вместо долгого ожидания).</summary>
    private static string BackgroundNoticeText => T("Операция выполняется в фоне — можно продолжать работу", "Операция фондо аткарылууда — иштей берсеңиз болот",
        "The operation is running in the background — you can keep working", "İşlem arka planda yürütülüyor — çalışmaya devam edebilirsiniz",
        "Amal fonda bajarilmoqda — ishni davom ettirishingiz mumkin");

    /// <summary>Задача закончилась за 0,5 с: как раньше — «Оприходование завершено» (накладная, печать), не принятые строки возвращаются в список.</summary>
    private async Task ReceiveShowResultAsync(ReceiveState s, ReceivingJob job)
    {
        var posted = job.Lines.Where(l => l.Outcome == ReceivingLineOutcome.Posted).ToList();
        if (job.Status != ReceivingJobStatus.Done && _receive == s)
        {
            // Отказ сервера / не отправлялись: строки снова в списке — поправить и повторить (как при ошибке раньше); «исход неизвестен» остаётся в очереди.
            foreach (var back in BackgroundReceivingQueue.Instance.TakeBackLines(job.Id))
                s.Lines.Add(back);
            ReceiveRender();
        }
        if (posted.Count > 0)
        {
            var result = new PurchaseReceivingService.PostResult(posted.Select(l => l.ToVm()).ToList(), posted.Sum(l => l.LineTotal), job.Created, job.Errors,
                job.ReceiptId, job.ReceiptTotal, job.ReceiptAt, job.State.ReceiptKey);
            var outcome = new PurchaseOutcome(result, job.Summary ?? PurchaseReceivingService.SummaryText(result), job.Errors.Count > 0 || job.Status != ReceivingJobStatus.Done, null);
            var supplier = s.Picker.Selected;
            if (supplier is not null)
            {
                var done = new PurchaseDoneDialog(supplier, outcome, posted.Select(l => new PurchaseDocs.DocLine(l.ProductName, l.Unit, l.Quantity, l.PurchasePrice)).ToList());
                await done.ShowDialog(this).ConfigureAwait(true);
            }
        }
        else if (_receive == s)
            ReceiveMessage(BackgroundResultText(job, out _), warning: true);

        if (_receive != s)
            return;
        // Всё принято — назад к истории (с новым приходом наверху); не всё — строки с ошибкой остаются, их можно поправить и повторить.
        if (posted.Count > 0 && !s.Lines.Any(l => l.Quantity > 0))
        {
            CloseSub();
            await ReloadAsync().ConfigureAwait(true);
        }
        else if (job.Errors.Count > 0)
            ReceiveMessage(string.Join("\n", job.Errors.Take(6)), warning: true);
    }

    // ------------------------------------------------------------------ итог фоновой задачи, пока окно открыто

    private readonly HashSet<string> _bgWaiting = new();
    private readonly HashSet<string> _bgShown = new();
    private bool _bgSubscribed;

    /// <summary>Один раз за жизнь окна: слушаем JobFinished (итог задач «Закупок», дошедших после того, как окно перестало ждать), отписка в Closed.</summary>
    private void EnsureBackgroundSubscription()
    {
        if (_bgSubscribed)
            return;
        _bgSubscribed = true;
        void OnFinished(ReceivingJob job)
        {
            if (job.Source == ReceivingJobSource.Purchases)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => ReceiveOnBackgroundFinished(job));
        }
        BackgroundReceivingQueue.Instance.JobFinished += OnFinished;
        Closed += (_, _) => BackgroundReceivingQueue.Instance.JobFinished -= OnFinished;
    }

    private async void ReceiveOnBackgroundFinished(ReceivingJob job)
    {
        // Окно ещё ждёт эту задачу или уже показало её итог (диалог) — не дублируем.
        if (_bgWaiting.Contains(job.Id) || !_bgShown.Add(job.Id))
            return;
        try
        {
            var text = BackgroundResultText(job, out var warning);
            if (_receive is not null)
                ReceiveMessage(text, warning);
            else if (warning)
                SetStatus(text, true);   // успех уже показал общий тост «Приходов в фоне» (BackgroundReceiptsHost), здесь — только замечания по деньгам
            if (job.PostedCount > 0)
                await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: итог фонового прихода {job.Id} не показан ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Итог фоновой задачи словами кассира (раздел 3 п. 5 ТЗ): принято — на какую сумму и от кого; нет — куда смотреть.</summary>
    private static string BackgroundResultText(ReceivingJob job, out bool warning)
    {
        var name = job.Supplier?.Name ?? "—";
        var posted = job.PostedCount;
        var sum = MoneyText.Som(job.ReceiptTotal is > 0 and var t ? t : job.Lines.Where(l => l.Outcome == ReceivingLineOutcome.Posted).Sum(l => l.LineTotal));
        var extra = job.Summary is { Length: > 0 } sm ? string.Join("\n", sm.Split('\n').Skip(1)) : "";
        warning = job.Status != ReceivingJobStatus.Done || job.Errors.Count > 0;
        if (posted > 0)
            return (job.Status == ReceivingJobStatus.Done ? "✓ " : "⚠ ")
                   + T($"Приход оприходован: {posted} поз. на {sum} (поставщик «{name}»)", $"Кирүү кабыл алынды: {posted} поз., {sum} (жеткирүүчү «{name}»)",
                       $"Delivery posted: {posted} items, {sum} (supplier “{name}”)", $"Mal kabul edildi: {posted} kalem, {sum} (tedarikçi «{name}»)",
                       $"Kirim qabul qilindi: {posted} poz., {sum} (yetkazib beruvchi «{name}»)")
                   + (job.Status == ReceivingJobStatus.Done ? "" : " — " + job.StatusText)
                   + (extra.Length > 0 ? "\n" + extra : "")
                   + (job.Errors.Count > 0 ? "\n" + string.Join("\n", job.Errors.Take(4)) : "");
        return "⚠ " + T("Приход не оприходован — откройте «Приходы в фоне»", "Кирүү кабыл алынган жок — «Фондогу кирүүлөрдү» ачыңыз", "The delivery wasn't posted — open “Background receipts”",
                   "Mal kabul işlenmedi — «Arka plandaki girişler»i açın", "Kirim o'tkazilmadi — «Fondagi kirimlar»ni oching")
               + (job.LastError is { Length: > 0 } err ? ": " + err : "");
    }

    // 2026-10-25 (Q4): финишер «purchases» очереди фоновых приходов — расход/изъятие/долг и запись в «Закупки» после прихода
    // (PurchasePaymentService.BackgroundFinisherAsync). Регистрируется при загрузке сборки (PurchaseFinisherSetup), до любого BackgroundReceivingQueue.Resume:
    // задача, оставшаяся от прошлого запуска, не потеряет оплату. Метод можно вызвать и вручную — повторная регистрация безвредна.
    public static void RegisterBackgroundFinisher() => PurchaseFinisherSetup.Register();

    private async Task<bool> ConfirmAsync(string text, string yes)
    {
        var w = new Window { Title = TitleText };
        SectionCards.AsModal(w, 480);
        var ok = false;
        var no = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        no.Click += (_, _) => w.Close();
        var go = SectionCards.Primary(yes);
        go.Click += (_, _) =>
        {
            ok = true;
            w.Close();
        };
        var body = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        Use(body, TextBlock.ForegroundProperty, "BrushText");
        w.Content = SectionCards.Shell(TitleText, null, body, SectionCards.Buttons(no, go));
        await w.ShowDialog(this).ConfigureAwait(true);
        return ok;
    }
}
