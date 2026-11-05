using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-01, магазин одежды: «Размеры и цвета» товара — варианты NurCRM (размер, цвет, своя цена,
/// остаток, штрихкод, продаётся ли). На сайте NurCRM такого редактора нет, поэтому он здесь, в
/// карточке товара склада. Пустая цена = цена товара; цена ниже — касса покажет «Скидка −N%».
/// «Быстро заполнить»: размеры × цвета → все сочетания. Сохранение — POST/PATCH/DELETE вариантов.
///
/// 2026-10-06, исследование «Кассы для одежды» (О-23, О-25), владелец: «делай всё по этапно». «Создать штрихкоды» —
/// размерам без штрихкода внутренние EAN-13 (на «29», без повторов с товарами и другими размерами); «Этикетки» —
/// этикетка на каждый размер (название, размер и цвет, цена, штрихкод) по остатку или по одной. Скан такой этикетки
/// в кассе сразу добавляет этот размер (VariantBarcodeIndex).
/// </summary>
public sealed class VariantEditorWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ICatalogApiService _api;
    private readonly string _productId;
    private readonly string _productTitle;
    private readonly double _basePrice;
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    // 2026-10-22 (витрина одежды): «остаток товара ≠ сумма по размерам» — касса, бот и витрина NurCRM показывают наличие по-разному
    // (NBS: Nike — товар 53, размеры 89). Только подсказка: остаток товара отсюда не меняем (PATCH quantity может создать движение денег).
    private readonly TextBlock _stockNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private double? _productStock;
    // 2026-10-22 (модалки, пакет 3): поля 44 px (класс modal-field), пример цвета — на языке программы.
    private readonly TextBox _quickSizes = new() { Watermark = "S, M, L, XL", MinWidth = 180, Classes = { "modal-field" } };
    private readonly TextBox _quickColors = new()
    {
        Watermark = Tr.T("Чёрный, Белый", "Кара, Ак", "Black, White", "Siyah, Beyaz", "Qora, Oq"), MinWidth = 200, Classes = { "modal-field" },
    };
    private readonly Button _save = SectionCards.Primary(Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
    private readonly List<Row> _items = new();
    private readonly List<string> _deletedIds = new();
    // 2026-10-22 (модалки, пакет 3): общая оболочка (912) и защита от двойного нажатия «Сохранить»/«Этикетки»: пока идёт
    // запись на сервер или печать, подвал, ✕ и Esc выключены (окно не закрывается посреди сохранения).
    private ModalShell _shell = null!;
    private bool _saving;
    private bool _printing;

    private sealed class Row
    {
        public ProductVariantDto Source = new();
        public TextBox Size = new() { Width = 90, Classes = { "modal-field" } };
        public TextBox Color = new() { Width = 140, Classes = { "modal-field" } };
        public TextBox Price = new() { Width = 100, Classes = { "modal-field" } };
        public TextBox Qty = new() { Width = 80, Classes = { "modal-field" } };
        public TextBox Barcode = new() { Width = 170, Classes = { "modal-field" } };
        public CheckBox Active = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, MinHeight = 44 };
        public Control? View;
        /// <summary>2026-11-01 (С2-04/С2-05): остаток размера, который знает окно: сервер при открытии или после последнего
        /// успешного сохранения/перечитывания. Остаток шлём, только если число в поле отличается от него.</summary>
        public double BaseQty;
    }

    public VariantEditorWindow(ICatalogApiService api, string productId, string productTitle, double basePrice)
    {
        _api = api;
        _productId = productId;
        _productTitle = productTitle;
        _basePrice = basePrice;
        Title = Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colors", "Bedenler ve renkler", "O'lchamlar va ranglar") + " — " + productTitle;
        // 2026-10-22 (модалки, пакет 3): окно без рамки ОС в общей оболочке ModalShell (L = 912, было своё окно 900×680):
        // заголовок и ✕ сверху, «Закрыть» (Esc = ✕) и «Сохранить» — в закреплённом подвале, строки прокручиваются между ними.
        // Enter «Сохранить» не нажимает: в колонке «Штрихкод» сканер заканчивает код Enter'ом.
        SectionCards.AsModal(this, 912);

        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(Soft(Tr.T(
            $"Каждый размер/цвет — со своим остатком. Цена пустая — продаётся по цене товара ({basePrice:0.##} сом); ниже — касса покажет скидку. В кассе (сфера «Одежда») при выборе товара откроется выбор размера и цвета.",
            $"Ар бир өлчөм/түс — өз калдыгы менен. Баасы бош — товардын баасы ({basePrice:0.##} сом); арзаныраак — касса арзандатуу көрсөтөт. Кассада («Кийим» тармагы) товарды тандаганда өлчөм жана түс тандалат.",
            $"Each size/color has its own stock. Empty price — product price ({basePrice:0.##} som); lower — the till shows a discount. At the till (Clothing mode) a size/color picker opens.",
            $"Her beden/renk kendi stokuyla. Fiyat boş — ürün fiyatı ({basePrice:0.##} som); daha düşük — kasa indirim gösterir. Kasada (Giyim) beden/renk seçimi açılır.",
            $"Har bir o'lcham/rang — o'z qoldig'i bilan. Narx bo'sh — mahsulot narxi ({basePrice:0.##} so'm); pastroq — kassa chegirma ko'rsatadi. Kassada (Kiyim) o'lcham/rang tanlovi ochiladi.")));

        // Быстро заполнить: размеры × цвета.
        // 2026-10-22 (модалки, пакет 3): ряд переносится (длинные надписи ky/tr/uz не обрезаются), кнопка — обводкой 44 px.
        var quick = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        quick.Children.Add(Soft(Tr.T("Быстро:", "Тез:", "Quick:", "Hızlı:", "Tez:"), center: true));
        quick.Children.Add(_quickSizes);
        quick.Children.Add(Soft("×", center: true));
        quick.Children.Add(_quickColors);
        var gen = SectionCards.Secondary(Tr.T("Добавить все сочетания", "Бардык айкалыштарды кошуу", "Add all combinations", "Tüm kombinasyonları ekle", "Barcha birikmalarni qo'shish"));
        gen.Click += (_, _) => GenerateCombos();
        quick.Children.Add(gen);
        foreach (var c in quick.Children)
            c.Margin = new Thickness(0, 0, 8, 6);
        top.Children.Add(quick);
        Use(_stockNote, TextBlock.ForegroundProperty, "BrushWarning");
        top.Children.Add(_stockNote);

        // 2026-10-06, исследование «Кассы для одежды» (О-20): готовые размерные сетки — одной кнопкой в поле размеров.
        var presets = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        presets.Children.Add(Soft(Tr.T("Сетки:", "Торчолор:", "Size sets:", "Beden setleri:", "O'lcham to'plamlari:"), center: true));
        foreach (var (name, sizes) in new[]
                 {
                     ("XS–XXL", "XS, S, M, L, XL, XXL"),
                     (Tr.T("Женская 40–54", "Аялдарга 40–54", "Women 40–54", "Kadın 40–54", "Ayollar 40–54"), "40, 42, 44, 46, 48, 50, 52, 54"),
                     (Tr.T("Мужская 44–60", "Эркектерге 44–60", "Men 44–60", "Erkek 44–60", "Erkaklar 44–60"), "44, 46, 48, 50, 52, 54, 56, 58, 60"),
                     (Tr.T("Обувь 35–46", "Бут кийим 35–46", "Shoes 35–46", "Ayakkabı 35–46", "Poyabzal 35–46"), "35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46"),
                     (Tr.T("Детская по росту", "Балдарга бою боюнча", "Kids by height", "Çocuk boya göre", "Bolalar bo'yi bo'yicha"), "86, 92, 98, 104, 110, 116, 122, 128, 134, 140, 146, 152, 158, 164"),
                     (Tr.T("Джинсы W26–W36", "Джинсы W26–W36", "Jeans W26–W36", "Kot W26–W36", "Jinsi W26–W36"), "W26, W27, W28, W29, W30, W31, W32, W33, W34, W36"),
                 })
        {
            // 2026-10-22 (модалки, пакет 3): чип 44 px (класс modal-chip, было 30).
            var b = new Button { Content = name, Margin = new Thickness(6, 0, 0, 6), Classes = { "modal-chip" } };
            b.Click += (_, _) => _quickSizes.Text = sizes;
            presets.Children.Add(b);
        }
        top.Children.Add(presets);

        // Заголовки колонок.
        var head = RowGrid();
        AddCell(head, Soft(Tr.T("Размер", "Өлчөм", "Size", "Beden", "O'lcham")), 0);
        AddCell(head, Soft(Tr.T("Цвет", "Түс", "Color", "Renk", "Rang")), 1);
        AddCell(head, Soft(Tr.T("Цена", "Баа", "Price", "Fiyat", "Narx")), 2);
        AddCell(head, Soft(Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq")), 3);
        AddCell(head, Soft(Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod")), 4);
        AddCell(head, Soft(Tr.T("Продаётся", "Сатылат", "On sale", "Satışta", "Sotuvda")), 5);
        top.Children.Add(head);

        // 2026-10-22 (модалки, пакет 3): действия со строками — рядом со строками (обводкой, 44 px); в подвале только
        // «Закрыть» и одна жёлтая «Сохранить» (было 5 кнопок в ряд — на 910 px не помещались).
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var tools = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var addRow = SectionCards.Secondary(Tr.T("+ Добавить строку", "+ Сап кошуу", "+ Add row", "+ Satır ekle", "+ Qator qo'shish"));
        addRow.Margin = new Thickness(0, 0, 10, 6);
        addRow.Click += (_, _) => AddRow(new ProductVariantDto());
        tools.Children.Add(addRow);
        // 2026-10-06 (О-23, О-25): штрихкоды размеров и этикетки.
        var makeCodes = SectionCards.Secondary(Tr.T("Создать штрихкоды", "Штрихкоддорду түзүү", "Create barcodes", "Barkod oluştur", "Shtrix-kodlar yaratish"));
        makeCodes.Margin = new Thickness(0, 0, 10, 6);
        ToolTip.SetTip(makeCodes, Tr.T("Размерам без штрихкода — свои штрихкоды для этикеток", "Штрихкоду жок өлчөмдөргө — этикетка үчүн өз штрихкоддору",
            "Own barcodes for sizes without one — for labels", "Barkodu olmayan bedenlere etiket için barkod", "Shtrix-kodi yo'q o'lchamlarga — yorliq uchun shtrix-kodlar"));
        makeCodes.Click += (_, _) => GenerateBarcodes();
        tools.Children.Add(makeCodes);
        var labels = SectionCards.Secondary(Tr.T("Этикетки", "Этикеткалар", "Labels", "Etiketler", "Yorliqlar"));
        labels.Margin = new Thickness(0, 0, 10, 6);
        labels.Click += async (_, _) => await PrintLabelsAsync().ConfigureAwait(true);
        tools.Children.Add(labels);
        var close = SectionCards.Cancel(Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        _save.Click += async (_, _) => _ = await SaveAsync().ConfigureAwait(true);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(top);
        body.Children.Add(_rows);
        body.Children.Add(tools);
        _shell = SectionCards.Shell(Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colors", "Bedenler ve renkler", "O'lchamlar va ranglar"),
            productTitle, body, SectionCards.Buttons(close, _save), _status);
        Content = _shell;

        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
        // 2026-11-02 (С3-13): «Закрыть», ✕ и Esc с несохранёнными правками спрашивают, а не теряют их молча.
        Closing += (_, e) =>
        {
            if (_closeConfirmed || _saving || _printing || e.CloseReason != WindowCloseReason.WindowClosing || !HasUnsavedChanges())
                return;
            e.Cancel = true;
            _ = ConfirmCloseAsync();
        };
    }

    private bool _closeConfirmed;

    private bool _closeAsking;

    private async Task ConfirmCloseAsync()
    {
        if (_closeAsking)
            return;
        _closeAsking = true;
        bool discard;
        try
        {
            discard = await ConfirmDiscardAsync().ConfigureAwait(true);
        }
        finally
        {
            _closeAsking = false;
        }
        if (!discard)
            return;
        _closeConfirmed = true;
        Close();
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        return await PosDialogs.ConfirmYesNoModalAsync(this,
            Tr.T("В окне есть несохранённые изменения (размеры, цены, остатки, штрихкоды). Закрыть без сохранения? Они пропадут.",
                "Терезеде сакталбаган өзгөрүүлөр бар (өлчөмдөр, баалар, калдыктар, штрихкоддор). Сактабай жабасызбы? Алар жок болот.",
                "There are unsaved changes (sizes, prices, stock, barcodes). Close without saving? They will be lost.",
                "Kaydedilmemiş değişiklikler var (beden, fiyat, stok, barkod). Kaydetmeden kapatılsın mı? Kaybolurlar.",
                "Saqlanmagan o'zgarishlar bor (o'lcham, narx, qoldiq, shtrix-kod). Saqlamasdan yopilsinmi? Ular yo'qoladi."),
            Tr.T("Закрыть без сохранения?", "Сактабай жабасызбы?", "Close without saving?", "Kaydetmeden kapat?", "Saqlamasdan yopilsinmi?"),
            Tr.T("Закрыть без сохранения", "Сактабай жабуу", "Close without saving", "Kaydetmeden kapat", "Saqlamasdan yopish"),
            Tr.T("Вернуться", "Кайтуу", "Go back", "Geri dön", "Qaytish"), danger: true).ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        _status.Text = Tr.T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        try
        {
            var list = await _api.GetProductVariantsAsync(_productId).ConfigureAwait(true);
            // 2026-10-02: размеры по порядку (S, M, L, XL; 42, 44…), как в окне выбора в кассе, а не по алфавиту.
            string[] order = { "XXXS", "XXS", "XS", "S", "M", "L", "XL", "XXL", "XXXL", "4XL", "5XL" };
            double Rank(string? size)
            {
                var s = (size ?? "").Trim();
                var i = Array.FindIndex(order, x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                    return i;
                return double.TryParse(s.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Inv, out var n) ? 100 + n : 10_000;
            }
            foreach (var v in list.OrderBy(v => Rank(v.Size)).ThenBy(v => v.Size, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Color, StringComparer.CurrentCultureIgnoreCase))
                AddRow(v);
            await LoadProductStockAsync().ConfigureAwait(true);
            UpdateStockNote();
            _status.Text = list.Count == 0
                ? Tr.T("Вариантов пока нет — добавьте размеры и цвета.", "Варианттар азырынча жок — өлчөмдөрдү жана түстөрдү кошуңуз.", "No variants yet — add sizes and colors.", "Henüz varyant yok — beden ve renk ekleyin.", "Hozircha variantlar yo'q — o'lcham va ranglarni qo'shing.")
                : Tr.T($"Вариантов: {list.Count}", $"Варианттар: {list.Count}", $"Variants: {list.Count}", $"Varyant: {list.Count}", $"Variantlar: {list.Count}");
        }
        catch (Exception ex)
        {
            _status.Text = Tr.T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Could not load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + ex.Message;
        }
    }

    /// <summary>2026-10-22 (витрина одежды): общий остаток товара с сервера (карточка товара) — для сверки с размерами.</summary>
    private async Task LoadProductStockAsync()
    {
        try
        {
            var detail = await _api.ProductsDetailAsync(_productId).ConfigureAwait(true);
            if (detail is { ValueKind: System.Text.Json.JsonValueKind.Object } d && d.TryGetProperty("quantity", out var q)
                && double.TryParse(q.ValueKind == System.Text.Json.JsonValueKind.String ? q.GetString() : q.GetRawText(), NumberStyles.Any, Inv, out var stock))
                _productStock = stock;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Размеры товара {_productId}: остаток товара не получен ({ex.Message}).", "CATALOG");
        }
    }

    private void UpdateStockNote()
    {
        var sum = _items.Where(r => r.Active.IsChecked == true)
            .Sum(r => double.TryParse(r.Qty.Text?.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Inv, out var q) && q > 0 ? q : 0);
        var differs = _productStock is { } stock && _items.Count > 0 && Math.Abs(stock - sum) >= 0.0005;
        _stockNote.IsVisible = differs;
        if (!differs)
            return;
        var p = _productStock!.Value.ToString("0.###", Inv);
        var v = sum.ToString("0.###", Inv);
        _stockNote.Text = Tr.T(
            $"Остаток товара — {p} шт., а по размерам вместе — {v} шт. Касса, Telegram-бот и онлайн-витрина могут показывать разное наличие. Поправьте остатки размеров здесь или общий остаток товара (ревизия / приход).",
            $"Товардын калдыгы — {p} даана, ал эми өлчөмдөр боюнча бардыгы — {v} даана. Касса, Telegram-бот жана онлайн-витрина ар башка калдык көрсөтүшү мүмкүн. Өлчөмдөрдүн калдыгын бул жерден же товардын жалпы калдыгын (ревизия / кирүү) оңдоңуз.",
            $"Product stock is {p} pcs, but sizes add up to {v} pcs. The till, the Telegram bot and the online showcase may show different availability. Fix the size stock here or the product's total stock (stocktake / receiving).",
            $"Ürün stoku {p} adet, bedenlerin toplamı ise {v} adet. Kasa, Telegram botu ve çevrimiçi vitrin farklı stok gösterebilir. Beden stoklarını burada ya da ürünün toplam stokunu (sayım / mal kabul) düzeltin.",
            $"Mahsulot qoldig'i — {p} dona, o'lchamlar bo'yicha jami — {v} dona. Kassa, Telegram-bot va onlayn vitrina turlicha mavjudlik ko'rsatishi mumkin. O'lchamlar qoldig'ini shu yerda yoki mahsulotning umumiy qoldig'ini (inventarizatsiya / kirim) tuzating.");
    }

    private void AddRow(ProductVariantDto v)
    {
        var row = new Row { Source = v };
        row.Size.Text = v.Size;
        row.Color.Text = v.Color;
        row.Price.Text = v.Price is { } p ? p.ToString("0.##", Inv) : "";
        row.Price.Watermark = _basePrice.ToString("0.##", Inv);
        row.Qty.Text = v.Quantity.ToString("0.###", Inv);
        row.BaseQty = v.Quantity;
        row.Barcode.Text = v.Barcode ?? "";
        row.Active.IsChecked = v.IsActive;
        // 2026-11-01 (С2-04): красная рамка «неверное значение» снимается, как только поле правят.
        foreach (var box in new[] { row.Size, row.Color, row.Price, row.Qty, row.Barcode })
        {
            var b = box;
            b.TextChanged += (_, _) => b.ClearValue(TextBox.BorderBrushProperty);
        }

        var grid = RowGrid();
        AddCell(grid, row.Size, 0);
        AddCell(grid, row.Color, 1);
        AddCell(grid, row.Price, 2);
        AddCell(grid, row.Qty, 3);
        AddCell(grid, row.Barcode, 4);
        AddCell(grid, row.Active, 5);
        // 2026-10-22 (модалки, пакет 3): ✕ строки 44×44 (было 36×32), обводкой, как modal-close.
        var del = new Button { Content = "✕", Width = 44, Height = 44, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "modal-close" } };
        ToolTip.SetTip(del, Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"));
        del.Click += async (_, _) =>
        {
            // 2026-11-02 (С3-04): размер с остатком или штрихкодом удаляем только после подтверждения (остаток, история и этикетки пропадут).
            if (!string.IsNullOrWhiteSpace(row.Source.Id) && (row.Source.Quantity > 0 || !string.IsNullOrWhiteSpace(row.Source.Barcode)))
            {
                var label = VariantLabel(row);
                var qty = row.Source.Quantity.ToString("0.###", Inv);
                var sure = await PosDialogs.ConfirmYesNoModalAsync(this,
                    Tr.T($"Удалить размер «{label}»? Его остаток ({qty} шт.) и история пропадут с сервера, напечатанные этикетки с его штрихкодом на кассе перестанут находиться. Удаление произойдёт при нажатии «Сохранить»; вернуть размер потом будет нельзя.",
                        $"«{label}» өлчөмү өчүрүлсүнбү? Анын калдыгы ({qty} даана) жана тарыхы сервердеги жок болот, штрихкоду менен басылган этикеткалар кассада табылбай калат. Өчүрүү «Сактоо» басылганда болот; кийин кайтарууга болбойт.",
                        $"Delete the size “{label}”? Its stock ({qty} pcs) and history will disappear from the server, and printed labels with its barcode will no longer be found at the till. The deletion happens when you press Save; it cannot be undone.",
                        $"«{label}» bedeni silinsin mi? Stoku ({qty} adet) ve geçmişi sunucudan silinir, barkodlu basılı etiketler kasada bulunamaz. Silme «Kaydet»e basınca yapılır; geri alınamaz.",
                        $"«{label}» o'lchami o'chirilsinmi? Uning qoldig'i ({qty} dona) va tarixi serverdan yo'qoladi, shtrix-kodi bilan chop etilgan yorliqlar kassada topilmaydi. O'chirish «Saqlash» bosilganda amalga oshadi; qaytarib bo'lmaydi."),
                    Tr.T("Удалить размер", "Өлчөмдү өчүрүү", "Delete size", "Bedeni sil", "O'lchamni o'chirish"),
                    Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
                    Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), danger: true).ConfigureAwait(true);
                if (!sure || !_items.Contains(row))
                    return;
            }
            if (!string.IsNullOrWhiteSpace(row.Source.Id))
                _deletedIds.Add(row.Source.Id!);
            _items.Remove(row);
            _rows.Children.Remove(row.View!);
        };
        AddCell(grid, del, 6);
        row.View = grid;
        _items.Add(row);
        _rows.Children.Add(grid);
    }

    private void GenerateCombos()
    {
        static List<string> Split(string? s) =>
            (s ?? "").Split(new[] { ',', ';', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sizes = Split(_quickSizes.Text);
        var colors = Split(_quickColors.Text);
        if (sizes.Count == 0) sizes.Add("");
        if (colors.Count == 0) colors.Add("");
        var added = 0;
        foreach (var s in sizes)
        foreach (var c in colors)
        {
            if (s.Length == 0 && c.Length == 0)
                continue;
            if (_items.Any(r => string.Equals(r.Size.Text?.Trim(), s, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(r.Color.Text?.Trim(), c, StringComparison.OrdinalIgnoreCase)))
                continue;
            AddRow(new ProductVariantDto { Size = s, Color = c });
            added++;
        }

        _status.Text = Tr.T($"Добавлено строк: {added}. Впишите остатки и нажмите «Сохранить».", $"Сап кошулду: {added}. Калдыктарды жазып, «Сактоо» басыңыз.", $"Rows added: {added}. Enter stock and press Save.", $"Eklenen satır: {added}. Stokları girip Kaydet'e basın.", $"Qo'shilgan qatorlar: {added}. Qoldiqlarni kiriting va «Saqlash»ni bosing.");
    }

    private static string VariantLabel(Row row)
    {
        var s = string.Join(", ", new[] { row.Size.Text?.Trim(), row.Color.Text?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return s.Length == 0 ? "?" : s;
    }

    /// <summary>2026-11-01 (С2-04): красная рамка поля, где значение неверное.</summary>
    private void MarkBad(TextBox box) =>
        box.BorderBrush = this.TryFindResource("BrushDanger", ActualThemeVariant, out var b) && b is IBrush br ? br : Brushes.IndianRed;

    /// <summary>2026-11-01 (С2-04): остаток размера — число 0 или больше. Пусто, «abc», «−5», «1.000» — ошибка (раньше уходили на
    /// сервер нулём). «1 000» и «12,5» разбираются теми же правилами, что суммы в окне оплаты.</summary>
    private static bool TryReadQty(Row row, out double qty) =>
        CheckoutValidation.TryParseAmount(row.Qty.Text, out qty, 3) == CheckoutValidation.AmountParse.Ok && qty >= 0;

    /// <summary>2026-11-01 (С2-04): цена размера: пусто или 0 = цена товара (null); «abc» — ошибка (раньше молча становилась ценой товара).</summary>
    private static CheckoutValidation.AmountParse TryReadPrice(Row row, out double? price)
    {
        price = null;
        var parse = CheckoutValidation.TryParseAmount(row.Price.Text, out var p);
        if (parse == CheckoutValidation.AmountParse.Ok && p > 0)
            price = p;
        return parse == CheckoutValidation.AmountParse.Empty ? CheckoutValidation.AmountParse.Ok : parse;
    }

    /// <summary>2026-11-01 (С2-06): в строке есть то, чего ещё нет на сервере (новая строка, правка поля, удалённая строка).</summary>
    private bool HasUnsavedChanges()
    {
        if (_deletedIds.Count > 0)
            return true;
        foreach (var row in _items)
        {
            var size = row.Size.Text?.Trim() ?? "";
            var color = row.Color.Text?.Trim() ?? "";
            if (size.Length == 0 && color.Length == 0)
                continue;
            if (row.Source.Id == null || row.Source.Size != size || row.Source.Color != color
                || (row.Source.Barcode ?? "") != (row.Barcode.Text?.Trim() ?? "") || row.Source.IsActive != (row.Active.IsChecked == true))
                return true;
            if (TryReadPrice(row, out var price) != CheckoutValidation.AmountParse.Ok || !Nullable.Equals(row.Source.Price, price))
                return true;
            if (!TryReadQty(row, out var qty) || Math.Abs(qty - row.BaseQty) >= 0.0005)
                return true;
        }
        return false;
    }

    /// <summary>2026-11-01 (С4/дубли штрихкодов): штрихкоды, которые правили в этом окне и которые уже есть у другой строки окна,
    /// у другого товара (основной или дополнительный) или у размера другого товара. Строки без правки штрихкода не проверяются.</summary>
    private List<(Row Row, string Text)> FindBarcodeDuplicates()
    {
        var result = new List<(Row, string)>();
        var others = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in CatalogCacheService.Products.ToList())
            {
                if (string.Equals(p.Id, _productId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(p.Barcode))
                    others.TryAdd(p.Barcode.Trim(), p.Title);
                foreach (var a in p.AlternateBarcodeVariants ?? new List<NurMarketKassa.Models.Pos.AlternateBarcodeVariant>())
                    if (!string.IsNullOrWhiteSpace(a.Barcode))
                        others.TryAdd(a.Barcode.Trim(), p.Title);
            }
        }
        catch (InvalidOperationException)
        {
            // каталог обновлялся в этот момент — проверим по тому, что успели собрать
        }
        foreach (var row in _items)
        {
            var code = row.Barcode.Text?.Trim() ?? "";
            if (code.Length == 0 || (row.Size.Text?.Trim() ?? "").Length + (row.Color.Text?.Trim() ?? "").Length == 0)
                continue;
            if (row.Source.Id != null && string.Equals(row.Source.Barcode?.Trim() ?? "", code, StringComparison.OrdinalIgnoreCase))
                continue; // штрихкод не менялся
            var label = VariantLabel(row);
            var twin = _items.FirstOrDefault(o => o != row && string.Equals(o.Barcode.Text?.Trim(), code, StringComparison.OrdinalIgnoreCase));
            if (twin != null)
                result.Add((row, $"{code}: {label} / {VariantLabel(twin)}"));
            else if (others.TryGetValue(code, out var title))
                result.Add((row, $"{code}: {label} / {title}"));
            else if (VariantBarcodeIndex.Find(code) is { } hit && !string.Equals(hit.ProductId, _productId, StringComparison.OrdinalIgnoreCase))
                result.Add((row, $"{code}: {label} / {Tr.T("размер другого товара", "башка товардын өлчөмү", "size of another product", "başka ürünün bedeni", "boshqa mahsulot o'lchami")}"));
        }
        return result;
    }

    /// <summary>true — всё сохранено без ошибок (2026-11-01, С2-06: печать этикеток идёт только после true).</summary>
    private async Task<bool> SaveAsync()
    {
        // 2026-10-22 (модалки, пакет 3): второй клик/вызов во время сохранения ничего не делает; окно занято (IsBusy).
        if (_saving)
            return false;
        _saving = true;
        var ownBusy = _shell.TryBeginBusy();
        _save.IsEnabled = false;
        var saved = 0;
        var errors = new List<string>();
        var failedRows = new HashSet<Row>(); // строки с неверным значением или отказом сервера — их поля не затираем перечитыванием
        try
        {
            // 2026-11-01 (С2-04): сначала проверка всех строк — число в остатке и цене, ничего не уходит на сервер вслепую.
            var work = new List<(Row Row, string Size, string Color, double? Price, double Qty)>();
            foreach (var row in _items)
            {
                var size = row.Size.Text?.Trim() ?? "";
                var color = row.Color.Text?.Trim() ?? "";
                if (size.Length == 0 && color.Length == 0)
                    continue;
                var label = VariantLabel(row);
                var bad = false;
                var priceParse = TryReadPrice(row, out var price);
                if (priceParse != CheckoutValidation.AmountParse.Ok)
                {
                    MarkBad(row.Price);
                    errors.Add($"{label}: {CheckoutValidation.AmountProblemText(priceParse)}");
                    bad = true;
                }
                if (!TryReadQty(row, out var qty))
                {
                    MarkBad(row.Qty);
                    errors.Add(Tr.T($"{label}: остаток — число 0 или больше (например 12); строка не сохранена.", $"{label}: калдык — 0 же андан чоң сан (мисалы 12); сап сакталган жок.",
                        $"{label}: stock must be a number 0 or more (e.g. 12); row not saved.", $"{label}: stok 0 veya daha büyük bir sayı olmalı (örn. 12); satır kaydedilmedi.",
                        $"{label}: qoldiq 0 yoki undan katta son bo'lishi kerak (masalan 12); qator saqlanmadi."));
                    bad = true;
                }
                if (bad)
                {
                    failedRows.Add(row);
                    continue;
                }
                work.Add((row, size, color, price, qty));
            }

            // 2026-11-02 (С3-05): пара «размер + цвет» в товаре одна (без учёта регистра). Строку, которая уже сохранена и не менялась,
            // не трогаем; новые и изменённые дубли подсвечиваются красным и не сохраняются.
            var pairBlocked = new HashSet<Row>();
            foreach (var g in _items.Where(r => (r.Size.Text?.Trim() ?? "").Length + (r.Color.Text?.Trim() ?? "").Length > 0)
                         .GroupBy(r => ((r.Size.Text?.Trim() ?? "").ToUpperInvariant(), (r.Color.Text?.Trim() ?? "").ToUpperInvariant())).Where(g => g.Count() > 1))
            {
                foreach (var r in g)
                {
                    var rs = r.Size.Text?.Trim() ?? "";
                    var rc = r.Color.Text?.Trim() ?? "";
                    if (r.Source.Id != null && r.Source.Size == rs && r.Source.Color == rc)
                        continue;
                    MarkBad(r.Size);
                    MarkBad(r.Color);
                    pairBlocked.Add(r);
                    failedRows.Add(r);
                    errors.Add(Tr.T($"{VariantLabel(r)}: такой размер с таким цветом уже есть в товаре; строка не сохранена.", $"{VariantLabel(r)}: мындай өлчөм ушул түс менен товарда мурунтан бар; сап сакталган жок.",
                        $"{VariantLabel(r)}: this size and color already exist for the product; row not saved.", $"{VariantLabel(r)}: bu beden ve renk ürüne zaten eklenmiş; satır kaydedilmedi.",
                        $"{VariantLabel(r)}: bunday o'lcham va rang mahsulotda allaqachon bor; qator saqlanmadi."));
                }
            }
            if (pairBlocked.Count > 0)
                work.RemoveAll(w => pairBlocked.Contains(w.Row));

            // 2026-11-01: дубли штрихкодов — предупреждение до записи.
            var dups = FindBarcodeDuplicates().Where(d => work.Any(w => w.Row == d.Row)).ToList();
            if (dups.Count > 0)
            {
                foreach (var d in dups)
                    MarkBad(d.Row.Barcode);
                var list = string.Join("\n", dups.Select(d => d.Text).Take(8));
                var go = await PosDialogs.ConfirmYesNoModalAsync(this,
                    Tr.T($"Такой штрихкод уже есть (штрихкод: размер / где совпало):\n{list}\n\nПри скане этикетка может открыть не тот товар или размер. Сохранить всё равно?",
                        $"Мындай штрихкод мурунтан бар (штрихкод: өлчөм / кайда дал келди):\n{list}\n\nСкандаганда этикетка башка товарды же өлчөмдү ачып коюшу мүмкүн. Баары бир сактайбызбы?",
                        $"This barcode already exists (barcode: size / where it matches):\n{list}\n\nA scan may open the wrong product or size. Save anyway?",
                        $"Bu barkod zaten var (barkod: beden / eşleştiği yer):\n{list}\n\nTarama yanlış ürünü ya da bedeni açabilir. Yine de kaydedilsin mi?",
                        $"Bunday shtrix-kod allaqachon bor (shtrix-kod: o'lcham / qayerda mos keldi):\n{list}\n\nSkanerlashda yorliq boshqa mahsulot yoki o'lchamni ochishi mumkin. Baribir saqlansinmi?"),
                    Tr.T("Штрихкод повторяется", "Штрихкод кайталанат", "Duplicate barcode", "Barkod tekrarı", "Shtrix-kod takrorlanadi"),
                    Tr.T("Сохранить всё равно", "Баары бир сактоо", "Save anyway", "Yine de kaydet", "Baribir saqlash"),
                    Tr.T("Исправить", "Оңдоо", "Fix it", "Düzelt", "Tuzatish")).ConfigureAwait(true);
                if (!go)
                {
                    _status.Text = Tr.T("Не сохранено: поправьте повторяющиеся штрихкоды (красные поля).", "Сакталган жок: кайталанган штрихкоддорду оңдоңуз (кызыл талаалар).",
                        "Not saved: fix the duplicate barcodes (red fields).", "Kaydedilmedi: tekrarlanan barkodları düzeltin (kırmızı alanlar).", "Saqlanmadi: takrorlangan shtrix-kodlarni tuzating (qizil maydonlar).");
                    return false;
                }
            }

            // 2026-11-01 (С2-05): остаток шлём, только если его меняли. Перед этим сверяем с сервером: если с момента открытия
            // (или прошлого сохранения) остаток изменился — продажа или приход, — не перезаписываем, а спрашиваем и применяем разницу.
            var qtyChanged = new HashSet<Row>();
            Dictionary<string, ProductVariantDto>? serverNow = null;
            var needServer = work.Any(w => w.Row.Source.Id != null && Math.Abs(w.Qty - w.Row.BaseQty) >= 0.0005);
            if (needServer)
            {
                try
                {
                    serverNow = (await _api.GetProductVariantsAsync(_productId).ConfigureAwait(true))
                        .Where(v => !string.IsNullOrWhiteSpace(v.Id)).GroupBy(v => v.Id!).ToDictionary(g => g.Key, g => g.First());
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Размеры товара {_productId}: остатки для сверки не получены ({ex.Message}).", "CATALOG");
                }
            }
            var finalQty = new Dictionary<Row, double>();
            var skipRows = new HashSet<Row>();
            foreach (var w in work)
            {
                var row = w.Row;
                if (row.Source.Id == null)
                {
                    qtyChanged.Add(row); // новый размер: остаток уходит вместе с созданием
                    finalQty[row] = w.Qty;
                    continue;
                }
                if (Math.Abs(w.Qty - row.BaseQty) < 0.0005)
                    continue;
                var label = VariantLabel(row);
                if (serverNow == null)
                {
                    // сервер не ответил — остаток вслепую не пишем; остальные поля строки сохраняются, число в поле остаётся
                    errors.Add(Tr.T($"{label}: остаток не проверен (сервер не ответил) и не изменён.", $"{label}: калдык текшерилген жок (сервер жооп берген жок), өзгөргөн жок.",
                        $"{label}: stock not checked (server did not answer) and not changed.", $"{label}: stok kontrol edilemedi (sunucu yanıt vermedi), değiştirilmedi.",
                        $"{label}: qoldiq tekshirilmadi (server javob bermadi) va o'zgartirilmadi."));
                    failedRows.Add(row);
                    continue;
                }
                if (!serverNow.TryGetValue(row.Source.Id, out var srv))
                {
                    errors.Add(Tr.T($"{label}: такого размера уже нет на сервере — закройте и откройте окно заново.", $"{label}: мындай өлчөм серверде жок — терезени жаап кайра ачыңыз.",
                        $"{label}: this size no longer exists on the server — close and reopen the window.", $"{label}: bu beden sunucuda yok — pencereyi kapatıp yeniden açın.",
                        $"{label}: bunday o'lcham serverda yo'q — oynani yopib qayta oching."));
                    failedRows.Add(row);
                    skipRows.Add(row);
                    continue;
                }
                var target = w.Qty;
                if (Math.Abs(srv.Quantity - row.BaseQty) >= 0.0005)
                {
                    var delta = w.Qty - row.BaseQty;
                    var result = srv.Quantity + delta;
                    string F(double x) => x.ToString("0.###", Inv);
                    var apply = result >= 0 && await PosDialogs.ConfirmYesNoModalAsync(this,
                        Tr.T($"Остаток размера «{label}» изменился на сервере: было {F(row.BaseQty)}, стало {F(srv.Quantity)} (вероятно, продажа или приход). Вы ввели {F(w.Qty)} (изменение {delta.ToString("+0.###;-0.###", Inv)}).\n\nПрименить ваше изменение к текущему остатку? Получится {F(result)}.\n«Оставить как на сервере» — остаток будет {F(srv.Quantity)}.",
                            $"«{label}» өлчөмүнүн калдыгы серверде өзгөргөн: болчу {F(row.BaseQty)}, болду {F(srv.Quantity)} (сатуу же кирүү болгон окшойт). Сиз {F(w.Qty)} киргиздиңиз (өзгөрүү {delta.ToString("+0.###;-0.###", Inv)}).\n\nӨзгөрүүңүздү азыркы калдыкка колдонобузбу? Болот {F(result)}.\n«Сервердегидей калтыруу» — калдык {F(srv.Quantity)} болот.",
                            $"The stock of “{label}” changed on the server: was {F(row.BaseQty)}, now {F(srv.Quantity)} (probably a sale or receipt). You entered {F(w.Qty)} (change {delta.ToString("+0.###;-0.###", Inv)}).\n\nApply your change to the current stock? The result will be {F(result)}.\n“Keep server value” — stock stays {F(srv.Quantity)}.",
                            $"«{label}» bedeninin stoku sunucuda değişti: önce {F(row.BaseQty)}, şimdi {F(srv.Quantity)} (muhtemelen satış veya giriş). Siz {F(w.Qty)} girdiniz (değişiklik {delta.ToString("+0.###;-0.###", Inv)}).\n\nDeğişikliğiniz mevcut stoğa uygulansın mı? Sonuç {F(result)} olur.\n«Sunucudaki kalsın» — stok {F(srv.Quantity)} kalır.",
                            $"«{label}» o'lchami qoldig'i serverda o'zgargan: avval {F(row.BaseQty)}, hozir {F(srv.Quantity)} (ehtimol sotuv yoki kirim). Siz {F(w.Qty)} kiritdingiz (o'zgarish {delta.ToString("+0.###;-0.###", Inv)}).\n\nO'zgarishingiz joriy qoldiqqa qo'llansinmi? Natija {F(result)} bo'ladi.\n«Serverdagidek qoldirish» — qoldiq {F(srv.Quantity)} bo'ladi."),
                        Tr.T("Остаток изменился", "Калдык өзгөргөн", "Stock changed", "Stok değişti", "Qoldiq o'zgargan"),
                        Tr.T("Применить изменение", "Өзгөрүүнү колдонуу", "Apply my change", "Değişikliği uygula", "O'zgarishni qo'llash"),
                        Tr.T("Оставить как на сервере", "Сервердегидей калтыруу", "Keep server value", "Sunucudaki kalsın", "Serverdagidek qoldirish")).ConfigureAwait(true);
                    if (!apply)
                    {
                        // ваше число не применяем: поле показывает остаток сервера
                        row.Qty.Text = F(srv.Quantity);
                        row.BaseQty = srv.Quantity;
                        row.Source.Quantity = srv.Quantity;
                        continue;
                    }
                    target = result;
                }
                qtyChanged.Add(row);
                finalQty[row] = target;
            }

            foreach (var id in _deletedIds.ToList())
            {
                try
                {
                    await _api.DeleteProductVariantAsync(_productId, id).ConfigureAwait(true);
                    _deletedIds.Remove(id);
                    saved++;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
            }

            foreach (var w in work)
            {
                var row = w.Row;
                if (skipRows.Contains(row))
                    continue;
                var send = qtyChanged.Contains(row);
                var dto = new ProductVariantDto
                {
                    Id = row.Source.Id,
                    Size = w.Size,
                    Color = w.Color,
                    Price = w.Price,
                    Quantity = send ? finalQty[row] : row.BaseQty,
                    SendQuantity = send, // 2026-11-01: остаток шлём, только если его меняли (сравнение с BaseQty, а не с Source)
                    Barcode = string.IsNullOrWhiteSpace(row.Barcode.Text) ? null : row.Barcode.Text.Trim(),
                    IsActive = row.Active.IsChecked == true,
                };
                var unchanged = row.Source.Id != null && row.Source.Size == dto.Size && row.Source.Color == dto.Color
                                && Nullable.Equals(row.Source.Price, dto.Price) && !send
                                && (row.Source.Barcode ?? "") == (dto.Barcode ?? "") && row.Source.IsActive == dto.IsActive;
                if (unchanged)
                    continue;
                try
                {
                    row.Source = await _api.SaveProductVariantAsync(_productId, dto).ConfigureAwait(true);
                    // 2026-11-01 (С2-05): поле и «исходный» остаток — по ответу сервера, чтобы второе сохранение не слало устаревшее число.
                    row.Qty.Text = row.Source.Quantity.ToString("0.###", Inv);
                    row.BaseQty = row.Source.Quantity;
                    saved++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{w.Size} {w.Color}: {ex.Message}");
                    failedRows.Add(row);
                }
            }

            PosLogger.Log($"Размеры/цвета товара {_productId}: сохранено {saved}, ошибок {errors.Count}.", "CATALOG");
            // 2026-10-06 (О-01): свежие размеры — в кеш кассы и справочник штрихкодов (скан этикетки размера).
            if (saved > 0)
            {
                try
                {
                    var fresh = await _api.GetProductVariantsAsync(_productId).ConfigureAwait(true);
                    ProductVariantCache.Put(_productId, fresh);
                    // 2026-11-01 (С2-05): остатки строк — по серверу (продажа между сохранениями не теряется при следующей записи).
                    foreach (var row in _items)
                    {
                        if (row.Source.Id == null || failedRows.Contains(row))
                            continue;
                        var f = fresh.FirstOrDefault(v => string.Equals(v.Id, row.Source.Id, StringComparison.OrdinalIgnoreCase));
                        if (f == null || (TryReadQty(row, out var cur) && Math.Abs(cur - row.BaseQty) >= 0.0005))
                            continue; // нет на сервере или кассир уже правит остаток
                        row.Source.Quantity = f.Quantity;
                        row.BaseQty = f.Quantity;
                        row.Qty.Text = f.Quantity.ToString("0.###", Inv);
                    }
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Размеры товара {_productId} после сохранения не перечитаны: {ex.Message}", "CATALOG");
                }
                // 2026-11-02 (С3-13): сервер держит остаток товара равным сумме по размерам — после записи перечитываем и его, иначе подсказка вверху врёт.
                await LoadProductStockAsync().ConfigureAwait(true);
            }
            UpdateStockNote();
            _status.Text = errors.Count == 0
                ? Tr.T($"Сохранено изменений: {saved}.", $"Сакталды: {saved}.", $"Saved changes: {saved}.", $"Kaydedilen değişiklik: {saved}.", $"Saqlangan o'zgarishlar: {saved}.")
                : Tr.T("Не всё сохранилось: ", "Баары сакталган жок: ", "Not everything was saved: ", "Hepsi kaydedilmedi: ", "Hammasi saqlanmadi: ") + string.Join("; ", errors);
            return errors.Count == 0;
        }
        finally
        {
            _save.IsEnabled = true;
            _saving = false;
            if (ownBusy)
                _shell.EndBusy();
        }
    }

    /// <summary>2026-10-06 (О-23): внутренний EAN-13 на «29» (диапазон 20–29 — для своих штрихкодов магазина) без
    /// повторов: ни с товарами каталога, ни с известными кассе размерами, ни со строками этого окна. В сфере «Одежда»
    /// касса не читает такие коды как весовые (Р-08), а знакомый размер находит раньше весового разбора.</summary>
    private void GenerateBarcodes()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in CatalogCacheService.Products.ToList())
            {
                if (!string.IsNullOrWhiteSpace(p.Barcode))
                    taken.Add(p.Barcode.Trim());
                foreach (var a in p.AlternateBarcodeVariants ?? new List<NurMarketKassa.Models.Pos.AlternateBarcodeVariant>())
                    if (!string.IsNullOrWhiteSpace(a.Barcode))
                        taken.Add(a.Barcode.Trim());
            }
        }
        catch (InvalidOperationException)
        {
            // каталог обновлялся в этот момент — проверка по справочнику размеров и строкам окна всё равно есть
        }
        foreach (var r in _items)
            if (!string.IsNullOrWhiteSpace(r.Barcode.Text))
                taken.Add(r.Barcode.Text.Trim());

        var made = 0;
        foreach (var row in _items)
        {
            if (!string.IsNullOrWhiteSpace(row.Barcode.Text) || (string.IsNullOrWhiteSpace(row.Size.Text) && string.IsNullOrWhiteSpace(row.Color.Text)))
                continue;
            // 2026-11-05 (аудит №3, С2-39): общий генератор кассы (первая цифра не 0 и не 2). Прежние коды на «29…» кассой разбирались как весовая этикетка.
            var code = NurMarketKassa.Services.BarcodeGenerator.Generate(taken, c => VariantBarcodeIndex.Contains(c));
            taken.Add(code);
            row.Barcode.Text = code;
            made++;
        }
        _status.Text = made == 0
            ? Tr.T("Штрихкоды есть у всех размеров.", "Бардык өлчөмдөрдө штрихкод бар.", "All sizes already have barcodes.", "Tüm bedenlerin barkodu var.", "Barcha o'lchamlarda shtrix-kod bor.")
            : Tr.T($"Создано штрихкодов: {made}. Нажмите «Сохранить».", $"Түзүлгөн штрихкоддор: {made}. «Сактоо» басыңыз.", $"Barcodes created: {made}. Press Save.",
                $"Oluşturulan barkod: {made}. Kaydet'e basın.", $"Yaratilgan shtrix-kodlar: {made}. «Saqlash»ni bosing.");
        PosLogger.Log($"Размеры товара {_productId}: создано штрихкодов {made}.", "CATALOG");
    }

    /// <summary>2026-10-06 (О-25): этикетки размеров — сначала сохраняет правки (чтобы штрихкоды знали сервер и касса),
    /// потом спрашивает «по остатку» или «по одной» и печатает на принтер этикеток из настроек.</summary>
    private async Task PrintLabelsAsync()
    {
        // 2026-10-22 (модалки, пакет 3): двойное нажатие «Этикетки» не печатает дважды; окно занято на время печати.
        if (_printing)
            return;
        _printing = true;
        var ownBusy = _shell.TryBeginBusy();
        try
        {
            await PrintLabelsCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _printing = false;
            if (ownBusy)
                _shell.EndBusy();
        }
    }

    private async Task PrintLabelsCoreAsync()
    {
        if (!BarcodePrintAccess.IsAllowed()) // 2026-11-01 (П-10)
        {
            _status.Text = BarcodePrintAccess.DeniedText;
            return;
        }
        if (!LabelPrinterSelector.PrintingSupported) // 2026-11-02 (П-3): печать этикеток работает только в Windows
        {
            _status.Text = LabelPrinterSelector.UnsupportedText;
            return;
        }
        // 2026-11-01 (С2-06): этикетки печатаем только с СОХРАНЁННЫХ кодов. Есть несохранённое — предупреждаем и сохраняем по согласию;
        // не всё сохранилось — не печатаем (код, которого нет на сервере и в кеше кассы, на кассе даст «не найден»).
        if (HasUnsavedChanges())
        {
            var saveFirst = await PosDialogs.ConfirmYesNoModalAsync(this,
                Tr.T("В окне есть несохранённые изменения (размеры, штрихкоды, остатки). Этикетки печатаются только с сохранённых штрихкодов — сначала сохраните.",
                    "Терезеде сакталбаган өзгөрүүлөр бар (өлчөмдөр, штрихкоддор, калдыктар). Этикеткалар сакталган штрихкоддор менен гана басылат — алгач сактаңыз.",
                    "There are unsaved changes (sizes, barcodes, stock). Labels are printed only with saved barcodes — save first.",
                    "Kaydedilmemiş değişiklikler var (beden, barkod, stok). Etiketler yalnızca kaydedilmiş barkodlarla yazdırılır — önce kaydedin.",
                    "Saqlanmagan o'zgarishlar bor (o'lcham, shtrix-kod, qoldiq). Yorliqlar faqat saqlangan shtrix-kodlar bilan chop etiladi — avval saqlang."),
                Tr.T("Сначала сохраните", "Алгач сактаңыз", "Save first", "Önce kaydedin", "Avval saqlang"),
                Tr.T("Сохранить и печатать", "Сактап басуу", "Save and print", "Kaydet ve yazdır", "Saqlab chop etish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
            if (!saveFirst)
                return;
            if (!await SaveAsync().ConfigureAwait(true))
                return; // причина уже в строке состояния
        }
        // Печатаем строки, чей код совпадает с сохранённым на сервере.
        var rows = _items.Where(r => !string.IsNullOrWhiteSpace(r.Barcode.Text) && r.Source.Id != null
            && string.Equals(r.Source.Barcode?.Trim() ?? "", r.Barcode.Text!.Trim(), StringComparison.Ordinal)).ToList();
        if (rows.Count == 0)
        {
            _status.Text = Tr.T("Нет штрихкодов — нажмите «Создать штрихкоды» и «Сохранить».", "Штрихкоддор жок — «Штрихкоддорду түзүү» жана «Сактоо» басыңыз.",
                "No barcodes — press “Create barcodes” and Save.", "Barkod yok — «Barkod oluştur» ve Kaydet'e basın.", "Shtrix-kodlar yo'q — «Shtrix-kodlar yaratish» va «Saqlash»ni bosing.");
            return;
        }
        var printer = UserPreferences.Instance.LabelPrinterDevicePath;
        if (string.IsNullOrWhiteSpace(printer))
        {
            // 2026-11-01: единая подсказка — принтер этикеток выбирается в Настройки → Печать.
            _status.Text = LabelPrinterSelector.MissingHint();
            return;
        }

        var template = LabelTemplateStore.Load();
        // 2026-11-02 (С3-06): после сбоя печати остаток задания помнится; следующее «Этикетки» предлагает допечатать только его,
        // а не всё заново (иначе уже напечатанные этикетки дублируются).
        var savedCodes = rows.Select(r => r.Barcode.Text!.Trim()).ToHashSet(StringComparer.Ordinal);
        var rest = _labelRest?.Where(j => j.Remaining > 0 && savedCodes.Contains(j.Barcode)).ToList();
        var resume = false;
        if (rest is { Count: > 0 })
        {
            var left = rest.Sum(j => j.Remaining);
            resume = await PosDialogs.ConfirmYesNoModalAsync(this,
                Tr.T($"Прошлая печать остановилась: напечатано {_labelPlanPrinted} из {_labelPlanTotal}. Допечатать только остальные — {left} шт.? Уже напечатанные повторно не печатаются.\n«Заново» — выбрать количество с начала (будут дубли уже напечатанных).",
                    $"Мурунку басуу токтоп калды: {_labelPlanPrinted}/{_labelPlanTotal} басылды. Калганын гана басабызбы — {left} даана? Басылгандар кайра басылбайт.\n«Кайрадан» — санын башынан тандоо (басылгандар кайталанат).",
                    $"The last print stopped: {_labelPlanPrinted} of {_labelPlanTotal} printed. Print only the rest — {left} pcs? Already printed labels are not printed again.\n“From scratch” — choose the count again (already printed labels will be duplicated).",
                    $"Önceki yazdırma durdu: {_labelPlanPrinted}/{_labelPlanTotal} basıldı. Yalnızca kalanı yazdıralım mı — {left} adet? Basılanlar tekrar basılmaz.\n«Baştan» — adedi yeniden seçin (basılanlar tekrarlanır).",
                    $"Oldingi chop etish to'xtadi: {_labelPlanPrinted}/{_labelPlanTotal} chop etildi. Faqat qolganini chop etamizmi — {left} dona? Chop etilganlar qayta chop etilmaydi.\n«Boshidan» — sonni qayta tanlang (chop etilganlar takrorlanadi)."),
                Tr.T("Допечатать остальные", "Калганын басуу", "Print the rest", "Kalanı yazdır", "Qolganini chop etish"),
                Tr.T($"Допечатать остальные: {left} шт.", $"Калганын басуу: {left} даана", $"Print the rest: {left}", $"Kalanı yazdır: {left}", $"Qolganini chop etish: {left} dona"),
                Tr.T("Заново", "Кайрадан", "From scratch", "Baştan", "Boshidan")).ConfigureAwait(true);
        }
        List<LabelJob> jobs;
        if (resume)
        {
            jobs = rest!;
        }
        else
        {
            _labelRest = null;
            static double Qty(Row r) => double.TryParse(r.Qty.Text?.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Inv, out var q) && q > 0 ? Math.Ceiling(q) : 0;
            var byStock = (int)rows.Sum(Qty);
            var choice = await AskLabelCountAsync(byStock, rows.Count).ConfigureAwait(true);
            if (choice is null)
                return;
            jobs = new List<LabelJob>();
            foreach (var row in rows)
            {
                var copies = choice == true ? (int)Qty(row) : 1;
                if (copies <= 0)
                    continue;
                double price = double.TryParse(row.Price.Text?.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Inv, out var p) && p > 0 ? p : _basePrice;
                jobs.Add(new LabelJob
                {
                    Barcode = row.Barcode.Text!.Trim(),
                    Variant = string.Join(", ", new[] { row.Size.Text?.Trim(), row.Color.Text?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    PriceText = price.ToString("0.00", CultureInfo.GetCultureInfo("ru-RU")) + " сом", // 2026-11-05 (С2-43): «85,00 сом», как во всей программе
                    Remaining = copies,
                });
            }
            _labelPlanTotal = jobs.Sum(j => j.Remaining);
            _labelPlanPrinted = 0;
            if (_labelPlanTotal == 0)
                return;
        }

        // Печать идёт до первого сбоя; на сбое останавливаемся (раньше цикл шёл дальше, и повтор печатал всё заново).
        var printedNow = 0;
        var last = LabelPrintResult.Success;
        foreach (var job in jobs)
        {
            while (job.Remaining > 0)
            {
                var batch = Math.Min(job.Remaining, 99);
                var request = new LabelPrintRequest(_productTitle, job.Barcode, job.PriceText, batch, printer, template,
                    StoreName: UserPreferences.Instance.StoreName, VariantText: job.Variant);
                BarcodeLabelService.ClearPartial(printer); // 2026-11-02 (С3-16): «N из M» хранится по принтеру
                var result = await Task.Run(() => BarcodeLabelService.Print(request)).ConfigureAwait(true);
                if (result == LabelPrintResult.Success)
                {
                    printedNow += batch;
                    job.Remaining -= batch;
                    continue;
                }
                last = result;
                if (result == LabelPrintResult.PartiallySent)
                {
                    // часть этикеток задания ушла (счёт частями по 50) — её вычитаем, остальное допечатается отдельно
                    var sent = Math.Clamp(BarcodeLabelService.PartialFor(printer)?.Sent ?? 0, 0, batch);
                    printedNow += sent;
                    job.Remaining -= sent;
                }
                break;
            }
            if (last != LabelPrintResult.Success)
                break;
        }
        _labelPlanPrinted += printedNow;
        var restJobs = jobs.Where(j => j.Remaining > 0).ToList();
        _labelRest = restJobs.Count > 0 ? restJobs : null;
        var restCount = restJobs.Sum(j => j.Remaining);
        PosLogger.Log($"Размеры товара {_productId}: напечатано этикеток {printedNow} (всего {_labelPlanPrinted} из {_labelPlanTotal}), итог {last}.", "CATALOG");
        if (printedNow > 0)
            LabelPrinterSelector.LogSent(null, printer, printedNow);
        if (last == LabelPrintResult.Success)
        {
            BarcodeLabelService.ClearPartial(printer); // допечатано — прежний «N из M» больше не нужен
            _status.Text = Tr.T($"Этикетки отправлены на печать: {_labelPlanPrinted} шт.", $"Этикеткалар басууга жөнөтүлдү: {_labelPlanPrinted} даана.", $"Labels sent to the printer: {_labelPlanPrinted}.",
                    $"Etiketler yazdırmaya gönderildi: {_labelPlanPrinted}.", $"Yorliqlar chop etishga yuborildi: {_labelPlanPrinted} dona.")
                + " " + LabelPrinterSelector.SentText(LabelPrinterSelector.DisplayName(printer)) + ".";
            return;
        }
        var reason = last == LabelPrintResult.PartiallySent ? LabelPrinterSelector.PartialText(printer)
            : last == LabelPrintResult.PrinterNotFound ? LabelPrinterSelector.HintText(LabelPrinterState.NotFound)
            : Tr.T("Проверьте принтер этикеток.", "Этикетка принтерин текшериңиз.", "Check the label printer.", "Etiket yazıcısını kontrol edin.", "Yorliq printerini tekshiring.");
        _status.Text = Tr.T($"Напечатано {_labelPlanPrinted} из {_labelPlanTotal}. ", $"{_labelPlanPrinted}/{_labelPlanTotal} басылды. ", $"Printed {_labelPlanPrinted} of {_labelPlanTotal}. ", $"{_labelPlanPrinted}/{_labelPlanTotal} basıldı. ", $"{_labelPlanPrinted}/{_labelPlanTotal} chop etildi. ")
            + reason + " "
            + Tr.T($"Нажмите «Этикетки» — предложим допечатать только остальные ({restCount} шт.).", $"«Этикеткалар» басыңыз — калганын гана басууну сунуштайбыз ({restCount} даана).",
                $"Press “Labels” — we will offer to print only the rest ({restCount}).", $"«Etiketler»e basın — yalnızca kalanı yazdırmayı önereceğiz ({restCount} adet).", $"«Yorliqlar»ni bosing — faqat qolganini chop etishni taklif qilamiz ({restCount} dona).");
    }

    // 2026-11-02 (С3-06): остаток задания печати этикеток после сбоя (копии, ещё не напечатанные).
    private sealed class LabelJob
    {
        public string Barcode = "";
        public string Variant = "";
        public string PriceText = "";
        public int Remaining;
    }

    private List<LabelJob>? _labelRest;
    private int _labelPlanTotal;
    private int _labelPlanPrinted;

    /// <summary>true — по остатку каждого размера, false — по одной на размер, null — отмена.</summary>
    private async Task<bool?> AskLabelCountAsync(int byStock, int perSize)
    {
        bool? answer = null;
        // 2026-10-22 (модалки, пакет 3): окно без рамки ОС в общей оболочке (S = 512). Два варианта — большими кнопками
        // в теле (жёлтая — «По остатку», Enter; вторая — обводкой), «Отмена» в подвале = Esc = ✕ (раньше Esc не работал).
        var dlg = new Window { Title = Tr.T("Этикетки", "Этикеткалар", "Labels", "Etiketler", "Yorliqlar") };
        SectionCards.AsModal(dlg, 512);
        var panel = new StackPanel { Spacing = 10 };
        var stock = SectionCards.Primary(Tr.T($"По остатку — {byStock} шт.", $"Калдык боюнча — {byStock} даана", $"By stock — {byStock}", $"Stoğa göre — {byStock}", $"Qoldiq bo'yicha — {byStock} dona"),
            isDefault: byStock > 0);
        stock.HorizontalAlignment = HorizontalAlignment.Stretch;
        stock.IsEnabled = byStock > 0;
        stock.Click += (_, _) => { answer = true; dlg.Close(); };
        var one = SectionCards.Secondary(Tr.T($"По одной на размер — {perSize} шт.", $"Ар бир өлчөмгө бирден — {perSize} даана", $"One per size — {perSize}", $"Beden başına bir — {perSize}", $"Har bir o'lchamga bittadan — {perSize} dona"));
        one.HorizontalAlignment = HorizontalAlignment.Stretch;
        one.Click += (_, _) => { answer = false; dlg.Close(); };
        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => dlg.Close();
        panel.Children.Add(stock);
        panel.Children.Add(one);
        dlg.Content = SectionCards.Shell(Tr.T("Сколько этикеток напечатать?", "Канча этикетка басуу керек?", "How many labels to print?", "Kaç etiket yazdırılsın?", "Nechta yorliq chop etilsin?"),
            _productTitle, panel, SectionCards.Buttons(cancel));
        await dlg.ShowDialog(this).ConfigureAwait(true);
        return answer;
    }

    // 2026-10-22 (модалки, пакет 3): колонки под карточку 880 (было 784 + отступы — шире тела), ✕ 44 + отступ.
    private static Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("100,150,110,90,180,90,52") };

    private static void AddCell(Grid grid, Control c, int col)
    {
        c.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(c, col);
        grid.Children.Add(c);
    }

    private TextBlock Soft(string text, bool center = false)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Top };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));
}
