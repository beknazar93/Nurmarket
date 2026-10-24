using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
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
        public bool Narrow;
        public readonly HashSet<ReceivingLineVm> EditingPrice = new();
    }

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
        s.Picker.Changed += _ => ReceiveRefresh();
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
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        s.Scan = UiKit.Input(this, T("Сканируйте штрихкод или найдите товар по названию", "Штрихкодду сканерлеңиз же товарды аты боюнча табыңыз",
            "Scan a barcode or find a product by name", "Barkodu okutun veya ürünü adıyla bulun", "Shtrix-kodni skanerlang yoki mahsulotni nomi bo'yicha toping"), 44);
        Avalonia.Automation.AutomationProperties.SetName(s.Scan, s.Scan.Watermark);
        s.Scan.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await ReceiveAddByCodeAsync((s.Scan.Text ?? "").Trim()).ConfigureAwait(true);
        };
        s.Scan.TextChanged += (_, _) => ReceiveSuggest();
        tools.Children.Add(s.Scan);
        var supplierGoods = UiKit.Ghost(this, T("Товары поставщика", "Жеткирүүчүнүн товарлары", "Supplier's products", "Tedarikçi ürünleri", "Yetkazib beruvchi mahsulotlari"));
        supplierGoods.Height = 44;
        supplierGoods.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(supplierGoods, supplierGoods.Content as string);
        supplierGoods.Click += async (_, _) => await ReceiveSupplierGoodsAsync().ConfigureAwait(true);
        Grid.SetColumn(supplierGoods, 1);
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
        Grid.SetColumn(zero, 2);
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
            s.EditingPrice.Clear();
            ReceiveRender();
        };
        Grid.SetColumn(clear, 3);
        tools.Children.Add(clear);
        var toolsBox = new StackPanel { Children = { tools } };
        s.SuggestBox = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 8), IsVisible = false, Child = s.Suggest };
        Use(s.SuggestBox, Border.BackgroundProperty, "BrushPanel");
        Use(s.SuggestBox, Border.BorderBrushProperty, "BrushBorder");
        toolsBox.Children.Add(s.SuggestBox);
        s.Message.Margin = new Thickness(0, 0, 0, 8);
        toolsBox.Children.Add(s.Message);
        Grid.SetRow(toolsBox, 3);
        page.Children.Add(toolsBox);

        // Таблица товаров.
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
        Grid.SetRow(card, 5);
        page.Children.Add(card);

        // Низ: итог и одна жёлтая «Оприходовать товары» (шаг 3 — окно «Подтверждение оплаты»).
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        Use(s.Footer, TextBlock.ForegroundProperty, "BrushText");
        footer.Children.Add(s.Footer);
        s.Post = UiKit.Primary(this, T("Оприходовать товары", "Товарларды кирешке алуу", "Post the goods", "Malları kaydet", "Tovarlarni kirim qilish"));
        s.Post.Height = 48;
        s.Post.Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(s.Post, s.Post.Content as string);
        s.Post.Click += async (_, _) => await ReceivePostAsync().ConfigureAwait(true);
        Grid.SetColumn(s.Post, 1);
        footer.Children.Add(s.Post);
        Grid.SetRow(footer, 6);
        page.Children.Add(footer);

        page.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 1000;
            // Низкий экран (касса 1024×600): плитки прячутся — те же цифры в строке итога внизу.
            s.Tiles.IsVisible = e.NewSize.Height >= 600;
            if (narrow == s.Narrow && s.Head.ColumnDefinitions.Count > 0)
                return;
            s.Narrow = narrow;
            title.FontSize = narrow ? 17 : 20;
            supplierGoods.Content = narrow ? T("Товары пост.", "Жеткир. товары", "Supplier items", "Ted. ürünleri", "Yetk. mahsulot") : T("Товары поставщика", "Жеткирүүчүнүн товарлары", "Supplier's products", "Tedarikçi ürünleri", "Yetkazib beruvchi mahsulotlari");
            ReceiveRender();
        };
        ShowSub(page);

        if (prefill?.SupplierId is { Length: > 0 } sid)
        {
            var known = _filterSupplier.All.FirstOrDefault(x => x.Id == sid)
                        ?? new SupplierInfo(sid, prefill.SupplierName ?? "", "", "", "", "", "", "", "", "", null);
            s.Picker.Select(known, raise: false);
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

    /// <summary>Enter в поле: штрихкод/код — общий поиск кассы (этот ПК, затем сервер, затем общая база CRM и «новый товар»), иначе — первый
    /// найденный по названию.</summary>
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
            if (ReceiveMatches(text).FirstOrDefault() is { } byName)
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
            if (line.ProductId is { Length: > 0 } && s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)) is { } same)
                same.Quantity += 1;
            else if (line.IsNew && s.Lines.FirstOrDefault(x => x.IsNew && string.Equals(x.Barcode, line.Barcode, StringComparison.OrdinalIgnoreCase)) is { } sameNew)
                sameNew.Quantity += 1;
            else
            {
                s.Lines.Add(line);
                if (line.IsNew)
                    ReceiveMessage(T("Новый товар: впишите название, цену закупки и цену продажи — он появится на складе при оприходовании.",
                        "Жаңы товар: атын, сатып алуу жана сатуу баасын жазыңыз — кирешке алганда кампада пайда болот.",
                        "New product: enter the name, purchase and selling price — it appears in stock when posted.",
                        "Yeni ürün: adı, alış ve satış fiyatını girin — kaydedildiğinde stokta görünür.",
                        "Yangi mahsulot: nomi, xarid va sotuv narxini kiriting — kirim qilinganda omborda paydo bo'ladi."));
            }
            s.Scan.Text = "";
            ReceiveRender();
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

    private static IEnumerable<CatalogProductTileVm> ReceiveMatches(string text)
    {
        var q = text.Trim();
        if (q.Length < 2)
            return Enumerable.Empty<CatalogProductTileVm>();
        return CatalogCacheService.Products.Where(p => p.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                                       || (p.Barcode ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                                       || (p.Article ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).Take(8);
    }

    /// <summary>Подсказки под полем: до 8 товаров по названию/штрихкоду (нажатие — в приём).</summary>
    private void ReceiveSuggest()
    {
        if (_receive is not { } s)
            return;
        s.Suggest.Children.Clear();
        var text = (s.Scan.Text ?? "").Trim();
        var digitsOnly = text.Length > 0 && text.All(char.IsDigit);
        var matches = digitsOnly && text.Length >= 8 ? new List<CatalogProductTileVm>() : ReceiveMatches(text).ToList();
        foreach (var p in matches)
        {
            var name = new TextBlock { Text = p.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            var meta = new TextBlock { Text = $"{(string.IsNullOrWhiteSpace(p.Barcode) ? "" : p.Barcode + " · ")}{T("остаток", "калдык", "stock", "stok", "qoldiq")} {Q(p.Quantity)}", FontSize = 12 };
            Use(meta, TextBlock.ForegroundProperty, "BrushTextSoft");
            var b = new Button
            {
                Content = new StackPanel { Spacing = 1, Children = { name, meta } }, MinHeight = 44, Padding = new Thickness(10, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(8),
            };
            Avalonia.Automation.AutomationProperties.SetName(b, p.Title);
            b.Click += (_, _) =>
            {
                ReceiveAddTile(p, 1);
                s.Scan.Text = "";
                s.Scan.Focus();
            };
            s.Suggest.Children.Add(b);
        }
        s.SuggestBox.IsVisible = matches.Count > 0;
    }

    private void ReceiveAddTile(CatalogProductTileVm tile, double qty)
    {
        if (_receive is not { } s)
            return;
        if (s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, tile.Id, StringComparison.OrdinalIgnoreCase)) is { } same)
            same.Quantity += qty;
        else
            s.Lines.Add(PurchaseReceivingService.FromTile(tile, qty));
        s.SuggestBox.IsVisible = false;
        ReceiveRender();
    }

    /// <summary>«Товары поставщика» (как таблица сайта): его товары (suppliers/{id}/products/ и товары из его приходов) — строками с
    /// количеством 0 и последней ценой закупки; принимаются только строки с количеством.</summary>
    private async Task ReceiveSupplierGoodsAsync()
    {
        if (_receive is not { } s)
            return;
        if (s.Picker.Selected is not { } supplier)
        {
            ReceiveMessage(T("Сначала выберите поставщика.", "Адегенде жеткирүүчүнү тандаңыз.", "Choose a supplier first.", "Önce tedarikçi seçin.", "Avval yetkazib beruvchini tanlang."), warning: true);
            return;
        }
        ReceiveMessage(T("Загружаю товары поставщика…", "Жеткирүүчүнүн товарлары жүктөлүүдө…", "Loading the supplier's products…", "Tedarikçi ürünleri yükleniyor…", "Yetkazib beruvchi mahsulotlari yuklanmoqda…"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var products = await _svc.GetProductsAsync(supplier.Id, cts.Token).ConfigureAwait(true);
            List<PurchaseReceivingService.SupplierProduct> fromReceipts;
            try
            {
                fromReceipts = await PurchaseReceivingService.Instance.LoadSupplierProductsAsync(new PurchaseReceivingService.Supplier(supplier.Id, supplier.Name), cts.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Закупки: товары из приходов «{supplier.Name}» не загружены ({ex.Message}).", "WARNING");
                fromReceipts = new();
            }
            if (_receive != s)
                return;
            var lastPrice = fromReceipts.ToDictionary(x => x.ProductId, x => x.LastPurchasePrice, StringComparer.OrdinalIgnoreCase);
            var ids = products.Select(p => (p.Id, p.Name, p.PurchasePrice)).Concat(fromReceipts.Select(r => (r.ProductId, r.Name, r.LastPurchasePrice)))
                .Where(x => x.Item1.Length > 0).GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var added = 0;
            foreach (var (id, name, price) in ids)
            {
                if (s.Lines.Any(x => string.Equals(x.ProductId, id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var line = await LineForProductAsync(id, name, "", 0, lastPrice.TryGetValue(id, out var lp) && lp > 0 ? lp : price > 0 ? price : null).ConfigureAwait(true);
                if (_receive != s)
                    return;
                if (line is null)
                    continue;
                s.Lines.Add(line);
                added++;
            }
            ReceiveMessage(ids.Count == 0
                ? T("У этого поставщика ещё нет товаров — отсканируйте их или найдите по названию.", "Бул жеткирүүчүнүн товарлары азырынча жок — аларды сканерлеңиз же аты боюнча табыңыз.",
                    "This supplier has no products yet — scan them or find them by name.", "Bu tedarikçinin henüz ürünü yok — okutun veya adıyla bulun.",
                    "Bu yetkazib beruvchining mahsulotlari hali yo'q — skanerlang yoki nomi bo'yicha toping.")
                : T($"Добавлено товаров поставщика: {added}. Впишите количество — принимаются только строки с количеством.",
                    $"Жеткирүүчүнүн товарлары кошулду: {added}. Санын жазыңыз — саны бар саптар гана кабыл алынат.",
                    $"Supplier's products added: {added}. Enter quantities — only lines with a quantity are received.",
                    $"Eklenen tedarikçi ürünü: {added}. Miktar girin — yalnızca miktarı olan satırlar kabul edilir.",
                    $"Qo'shilgan mahsulotlar: {added}. Miqdorni kiriting — faqat miqdori bor qatorlar qabul qilinadi."));
            ReceiveRender();
        }
        catch (Exception ex)
        {
            ReceiveMessage(T("Товары поставщика не загрузились: ", "Жеткирүүчүнүн товарлары жүктөлгөн жок: ", "The supplier's products didn't load: ", "Tedarikçi ürünleri yüklenmedi: ", "Yetkazib beruvchi mahsulotlari yuklanmadi: ")
                           + ServerTelegramBotApi.DescribeFields(ex), warning: true);
        }
    }

    private void ReceiveMessage(string? text, bool warning = false)
    {
        if (_receive is not { } s)
            return;
        s.Message.IsVisible = !string.IsNullOrEmpty(text);
        s.Message.Text = text ?? "";
        Use(s.Message, TextBlock.ForegroundProperty, warning ? "BrushDanger" : "BrushTextSoft");
    }

    // ------------------------------------------------------------------ таблица

    private string ReceiveColumns => _receive?.Narrow == true ? "2.2*,0,1.1*,1*,1*,1*,Auto" : "2.4*,0.8*,1.1*,1*,1*,1.1*,Auto";

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
            T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"), T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"),
            T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), T("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi"), "",
        };
        for (var i = 0; i < heads.Length; i++)
        {
            var t = new TextBlock { Text = heads[i], FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = i is 1 or 4 ? TextAlignment.Right : TextAlignment.Left, IsVisible = !(s.Narrow && i == 1), MinWidth = i == 6 ? 44 : 0 };
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, i);
            s.Head.Children.Add(t);
        }
        s.Rows.Children.Clear();
        if (s.Lines.Count == 0)
        {
            s.Rows.Children.Add(SectionCards.Empty(this, T("Отсканируйте товары или нажмите «Товары поставщика» — его товары встанут списком, останется вписать количество и цену закупки.",
                "Товарларды сканерлеңиз же «Жеткирүүчүнүн товарларын» басыңыз — анын товарлары тизме болуп чыгат, санын жана сатып алуу баасын жазуу калат.",
                "Scan products or press “Supplier's products” — their products appear as a list; just enter quantity and purchase price.",
                "Ürünleri okutun veya «Tedarikçi ürünleri»ne basın — ürünleri liste olarak gelir; miktar ve alış fiyatını girmeniz yeterli.",
                "Mahsulotlarni skanerlang yoki «Yetkazib beruvchi mahsulotlari»ni bosing — mahsulotlar ro'yxat bo'lib chiqadi, miqdor va xarid narxini kiritish qoladi.")));
        }
        foreach (var line in s.Lines)
            s.Rows.Children.Add(ReceiveRow(line));
        ReceiveRefresh();
    }

    private Control ReceiveRow(ReceivingLineVm line)
    {
        var s = _receive!;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(ReceiveColumns) };
        // Товар: название (у нового — поле), штрихкод/код и пометка «новый».
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        if (line.IsNew)
        {
            var nameBox = new TextBox { Text = line.ProductName, Watermark = T("Название нового товара", "Жаңы товардын аты", "New product name", "Yeni ürün adı", "Yangi mahsulot nomi"), Height = 40, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center };
            Avalonia.Automation.AutomationProperties.SetName(nameBox, nameBox.Watermark);
            nameBox.TextChanged += (_, _) => line.ProductName = nameBox.Text ?? "";
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

        var sum = Cell(MoneyText.Som(line.LineTotal), "BrushText", 13.5, FontWeight.SemiBold);
        sum.TextAlignment = TextAlignment.Right;
        var price = NumberBox(line.PurchasePrice, T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi") + " " + line.ProductName, v =>
        {
            line.PurchasePrice = v;
            sum.Text = MoneyText.Som(line.LineTotal);
            ReceiveRefresh();
        });
        Grid.SetColumn(price, 2);
        g.Children.Add(price);
        var qty = NumberBox(line.Quantity, T("Количество", "Саны", "Quantity", "Miktar", "Miqdor") + " " + line.ProductName, v =>
        {
            line.Quantity = v;
            sum.Text = MoneyText.Som(line.LineTotal);
            ReceiveRefresh();
        });
        Grid.SetColumn(qty, 3);
        g.Children.Add(qty);
        Grid.SetColumn(sum, 4);
        g.Children.Add(sum);

        // Цена продажи: у товара со склада — текстом и «✎» (менять только явно, сервер не должен её трогать); у нового — поле.
        Control sale;
        if (line.IsNew || s.EditingPrice.Contains(line))
            sale = NumberBox(line.SalePrice, T("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi") + " " + line.ProductName, v => line.SalePrice = v);
        else
        {
            var text = Cell(line.SalePrice > 0 ? MoneyText.Som(line.SalePrice) : "—", "BrushText", 13.5);
            text.Margin = new Thickness(0, 0, 4, 0);
            var edit = new Button { Content = "✎", Width = 36, Height = 36, MinHeight = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            edit.Classes.Add("sec-outline");
            var editName = T("Изменить цену продажи", "Сатуу баасын өзгөртүү", "Change the selling price", "Satış fiyatını değiştir", "Sotuv narxini o'zgartirish");
            ToolTip.SetTip(edit, editName + T(" — новая цена уйдёт в карточку товара при оприходовании.", " — жаңы баа кирешке алганда товардын карточкасына кетет.",
                " — the new price goes to the product card when posted.", " — yeni fiyat kaydedildiğinde ürün kartına yazılır.", " — yangi narx kirim qilinganda kartaga yoziladi."));
            Avalonia.Automation.AutomationProperties.SetName(edit, editName + " " + line.ProductName);
            edit.Click += (_, _) =>
            {
                s.EditingPrice.Add(line);
                ReceiveRender();
            };
            sale = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { text, edit } };
            if (line.SalePrice > 0 && line.PurchasePrice > line.SalePrice + 0.004)
                ToolTip.SetTip(text, T("Цена закупки выше цены продажи — продажа будет в убыток.", "Сатып алуу баасы сатуу баасынан жогору — сатуу зыян менен болот.",
                    "The purchase price is above the selling price — sales will be at a loss.", "Alış fiyatı satış fiyatından yüksek — satış zararına olur.",
                    "Xarid narxi sotuv narxidan yuqori — sotuv zarariga bo'ladi."));
        }
        Grid.SetColumn(sale, 5);
        g.Children.Add(sale);

        var remove = new Button { Content = "✕", Width = 44, Height = 44, MinHeight = 44, Padding = new Thickness(0), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 15 };
        Use(remove, Button.ForegroundProperty, "BrushTextSoft");
        Avalonia.Automation.AutomationProperties.SetName(remove, T("Убрать строку", "Сапты алып салуу", "Remove the line", "Satırı kaldır", "Qatorni olib tashlash") + " " + line.ProductName);
        remove.Click += (_, _) =>
        {
            s.Lines.Remove(line);
            s.EditingPrice.Remove(line);
            ReceiveRender();
        };
        Grid.SetColumn(remove, 6);
        g.Children.Add(remove);

        var box = new Border { Padding = new Thickness(14, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = g, MinHeight = 56 };
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        if (line.Quantity <= 0)
            box.Opacity = 0.72;
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
        s.Footer.Text = s.Picker.Selected is null
            ? T("Выберите поставщика (шаг 1).", "Жеткирүүчүнү тандаңыз (1-кадам).", "Choose a supplier (step 1).", "Tedarikçi seçin (1. adım).", "Yetkazib beruvchini tanlang (1-qadam).")
            : active.Count == 0
                ? T("Добавьте товары и впишите количество (шаг 2).", "Товарларды кошуп, санын жазыңыз (2-кадам).", "Add products and enter quantities (step 2).", "Ürün ekleyip miktar girin (2. adım).", "Mahsulot qo'shib, miqdorni kiriting (2-qadam).")
                : T($"К оприходованию: {active.Count} поз. на {MoneyText.Som(active.Sum(l => l.LineTotal))}", $"Кирешке: {active.Count} поз., {MoneyText.Som(active.Sum(l => l.LineTotal))}",
                    $"To post: {active.Count} items, {MoneyText.Som(active.Sum(l => l.LineTotal))}", $"Kaydedilecek: {active.Count} kalem, {MoneyText.Som(active.Sum(l => l.LineTotal))}",
                    $"Kirim: {active.Count} poz., {MoneyText.Som(active.Sum(l => l.LineTotal))}");
        s.Post.IsEnabled = !s.Busy && s.Picker.Selected is not null && active.Count > 0;
    }

    // ------------------------------------------------------------------ оприходовать

    private async Task ReceivePostAsync()
    {
        if (_receive is not { } s || s.Busy || s.Picker.Selected is not { } supplierInfo)
            return;
        var lines = s.Lines.Where(l => l.Quantity > 0).ToList();
        var problems = PurchaseReceivingService.Validate(lines);
        if (problems.Count > 0)
        {
            ReceiveMessage(T("Нельзя оприходовать: ", "Кирешке алууга болбойт: ", "Can't post: ", "Kaydedilemez: ", "Kirim qilib bo'lmaydi: ") + string.Join("; ", problems.Take(4))
                           + (problems.Count > 4 ? T($" …и ещё {problems.Count - 4}", $" …жана дагы {problems.Count - 4}", $" …and {problems.Count - 4} more", $" …ve {problems.Count - 4} tane daha", $" …va yana {problems.Count - 4}") : ""), warning: true);
            return;
        }
        var total = Math.Round(lines.Sum(l => l.LineTotal), 2);
        var dialog = new PurchasePaymentDialog(supplierInfo.Name, lines.Count, total);
        await dialog.ShowDialog(this).ConfigureAwait(true);
        if (dialog.Result is not { } pay)
            return;

        s.Busy = true;
        ReceiveFooter();
        ReceiveMessage(T("Оприходую… не закрывайте окно.", "Кирешке алынууда… терезени жаппаңыз.", "Posting… don't close the window.", "Kaydediliyor… pencereyi kapatmayın.", "Kirim qilinmoqda… oynani yopmang."));
        PurchaseOutcome outcome;
        try
        {
            var supplier = new PurchaseReceivingService.Supplier(supplierInfo.Id, supplierInfo.Name, supplierInfo.Inn, supplierInfo.Phone);
            outcome = await PurchasePaymentService.PostAsync(lines, supplier, pay, s.OrderId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: приход не проведён ({ex}).", "WARNING");
            ReceiveMessage(T("Не удалось оприходовать: ", "Кирешке алынган жок: ", "Posting failed: ", "Kaydedilemedi: ", "Kirim qilinmadi: ") + ServerTelegramBotApi.DescribeFields(ex), warning: true);
            s.Busy = false;
            ReceiveFooter();
            return;
        }
        s.Busy = false;
        var posted = outcome.Result.PostedLines;
        // Остаток в каталоге кассы — сразу (продавать принятое можно, не дожидаясь синхронизации), от текущего остатка плитки.
        foreach (var line in posted.Where(l => !l.IsNew))
        {
            var tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase));
            if (tile is null)
                continue;
            try
            {
                var qty = Math.Round(tile.Quantity + line.Quantity, 3);
                StockSyncService.ApplyQuantityToTileOnUi(tile, qty, tile.MustWeigh);
                CatalogCacheService.PersistProductStock(tile.Id, qty, tile.MustWeigh);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Закупки: остаток товара {tile.Id} в каталоге не обновлён ({ex.Message}).", "WARNING");
            }
        }
        if (outcome.Result.Created > 0 || posted.Any(l => l.IsNew))
            _ = Task.Run(async () =>
            {
                try
                {
                    await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Закупки: каталог после новых товаров не обновлён ({ex.Message}).", "WARNING");
                }
            });
        foreach (var line in posted)
        {
            s.Lines.Remove(line);
            s.EditingPrice.Remove(line);
        }
        ReceiveMessage(null);
        ReceiveRender();

        if (posted.Count > 0)
        {
            var supplier = s.Picker.Selected!;
            var done = new PurchaseDoneDialog(supplier, outcome, posted.Select(l => new PurchaseDocs.DocLine(l.ProductName, l.Unit, l.Quantity, l.PurchasePrice)).ToList());
            await done.ShowDialog(this).ConfigureAwait(true);
        }
        else
            ReceiveMessage(outcome.Summary + (outcome.Result.Errors.Count > 0 ? "\n" + string.Join("\n", outcome.Result.Errors.Take(6)) : ""), warning: true);

        // Всё принято — назад к истории (с новым приходом наверху); не всё — строки с ошибкой остаются, их можно поправить и повторить.
        if (posted.Count > 0 && !s.Lines.Any(l => l.Quantity > 0))
        {
            CloseSub();
            await ReloadAsync().ConfigureAwait(true);
        }
        else if (outcome.Result.Errors.Count > 0)
            ReceiveMessage(string.Join("\n", outcome.Result.Errors.Take(6)), warning: true);
    }

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
