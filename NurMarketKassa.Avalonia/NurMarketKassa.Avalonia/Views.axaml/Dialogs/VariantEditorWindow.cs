using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(top);
        body.Children.Add(_rows);
        body.Children.Add(tools);
        _shell = SectionCards.Shell(Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colors", "Bedenler ve renkler", "O'lchamlar va ranglar"),
            productTitle, body, SectionCards.Buttons(close, _save), _status);
        Content = _shell;

        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
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
        row.Barcode.Text = v.Barcode ?? "";
        row.Active.IsChecked = v.IsActive;

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
        del.Click += (_, _) =>
        {
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

    private async Task SaveAsync()
    {
        // 2026-10-22 (модалки, пакет 3): второй клик/вызов во время сохранения ничего не делает; окно занято (IsBusy).
        if (_saving)
            return;
        _saving = true;
        var ownBusy = _shell.TryBeginBusy();
        _save.IsEnabled = false;
        var saved = 0;
        var errors = new List<string>();
        try
        {
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

            foreach (var row in _items)
            {
                var size = row.Size.Text?.Trim() ?? "";
                var color = row.Color.Text?.Trim() ?? "";
                if (size.Length == 0 && color.Length == 0)
                    continue;
                double? price = double.TryParse(row.Price.Text?.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Inv, out var p) && p > 0 ? p : null;
                double qty = double.TryParse(row.Qty.Text?.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Inv, out var q) ? q : 0;
                var dto = new ProductVariantDto
                {
                    Id = row.Source.Id,
                    Size = size,
                    Color = color,
                    Price = price,
                    Quantity = qty,
                    Barcode = string.IsNullOrWhiteSpace(row.Barcode.Text) ? null : row.Barcode.Text.Trim(),
                    IsActive = row.Active.IsChecked == true,
                };
                var unchanged = row.Source.Id != null && row.Source.Size == dto.Size && row.Source.Color == dto.Color
                                && Nullable.Equals(row.Source.Price, dto.Price) && Math.Abs(row.Source.Quantity - dto.Quantity) < 0.0005
                                && (row.Source.Barcode ?? "") == (dto.Barcode ?? "") && row.Source.IsActive == dto.IsActive;
                if (unchanged)
                    continue;
                try
                {
                    row.Source = await _api.SaveProductVariantAsync(_productId, dto).ConfigureAwait(true);
                    saved++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{size} {color}: {ex.Message}");
                }
            }

            PosLogger.Log($"Размеры/цвета товара {_productId}: сохранено {saved}, ошибок {errors.Count}.", "CATALOG");
            UpdateStockNote();
            // 2026-10-06 (О-01): свежие размеры — в кеш кассы и справочник штрихкодов (скан этикетки размера).
            if (saved > 0)
            {
                try
                {
                    ProductVariantCache.Put(_productId, await _api.GetProductVariantsAsync(_productId).ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Размеры товара {_productId} после сохранения не перечитаны: {ex.Message}", "CATALOG");
                }
            }
            _status.Text = errors.Count == 0
                ? Tr.T($"Сохранено изменений: {saved}.", $"Сакталды: {saved}.", $"Saved changes: {saved}.", $"Kaydedilen değişiklik: {saved}.", $"Saqlangan o'zgarishlar: {saved}.")
                : Tr.T("Не всё сохранилось: ", "Баары сакталган жок: ", "Not everything was saved: ", "Hepsi kaydedilmedi: ", "Hammasi saqlanmadi: ") + string.Join("; ", errors);
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
            string code;
            do
            {
                var first12 = "29" + string.Concat(Enumerable.Range(0, 10).Select(_ => System.Security.Cryptography.RandomNumberGenerator.GetInt32(10)));
                var sum = 0;
                for (var i = 0; i < 12; i++)
                    sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
                code = first12 + ((10 - sum % 10) % 10).ToString(Inv);
            }
            while (taken.Contains(code) || VariantBarcodeIndex.Contains(code));
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
        await SaveAsync().ConfigureAwait(true);
        var rows = _items.Where(r => !string.IsNullOrWhiteSpace(r.Barcode.Text)).ToList();
        if (rows.Count == 0)
        {
            _status.Text = Tr.T("Нет штрихкодов — нажмите «Создать штрихкоды» и «Сохранить».", "Штрихкоддор жок — «Штрихкоддорду түзүү» жана «Сактоо» басыңыз.",
                "No barcodes — press “Create barcodes” and Save.", "Barkod yok — «Barkod oluştur» ve Kaydet'e basın.", "Shtrix-kodlar yo'q — «Shtrix-kodlar yaratish» va «Saqlash»ni bosing.");
            return;
        }
        var printer = UserPreferences.Instance.LabelPrinterDevicePath;
        if (string.IsNullOrWhiteSpace(printer))
        {
            _status.Text = Tr.T("Принтер этикеток не выбран — выберите его один раз в окне «Этикетка» любого товара на складе.",
                "Этикетка принтери тандалган эмес — аны кампадагы каалаган товардын «Этикетка» терезесинен бир жолу тандаңыз.",
                "No label printer selected — choose it once in the “Label” window of any product in the warehouse.",
                "Etiket yazıcısı seçilmedi — depodaki herhangi bir ürünün «Etiket» penceresinden bir kez seçin.",
                "Yorliq printeri tanlanmagan — uni ombordagi istalgan mahsulotning «Yorliq» oynasida bir marta tanlang.");
            return;
        }

        static double Qty(Row r) => double.TryParse(r.Qty.Text?.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Inv, out var q) && q > 0 ? Math.Ceiling(q) : 0;
        var byStock = (int)rows.Sum(Qty);
        var choice = await AskLabelCountAsync(byStock, rows.Count).ConfigureAwait(true);
        if (choice is null)
            return;

        var template = LabelTemplateStore.Load();
        var printed = 0;
        var failed = 0;
        foreach (var row in rows)
        {
            var copies = choice == true ? (int)Qty(row) : 1;
            if (copies <= 0)
                continue;
            double price = double.TryParse(row.Price.Text?.Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Inv, out var p) && p > 0 ? p : _basePrice;
            var variantText = string.Join(", ", new[] { row.Size.Text?.Trim(), row.Color.Text?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
            while (copies > 0)
            {
                var batch = Math.Min(copies, 99);
                var request = new LabelPrintRequest(_productTitle, row.Barcode.Text!.Trim(), price.ToString("0.00", Inv) + " сом", batch, printer, template,
                    StoreName: UserPreferences.Instance.StoreName, VariantText: variantText);
                var result = await Task.Run(() => BarcodeLabelService.Print(request)).ConfigureAwait(true);
                if (result == LabelPrintResult.Success)
                    printed += batch;
                else
                    failed++;
                copies -= batch;
            }
        }
        PosLogger.Log($"Размеры товара {_productId}: напечатано этикеток {printed}, сбоев {failed}.", "CATALOG");
        _status.Text = failed == 0
            ? Tr.T($"Этикетки отправлены на печать: {printed} шт.", $"Этикеткалар басууга жөнөтүлдү: {printed} даана.", $"Labels sent to the printer: {printed}.",
                $"Etiketler yazdırmaya gönderildi: {printed}.", $"Yorliqlar chop etishga yuborildi: {printed} dona.")
            : Tr.T($"Напечатано {printed} шт., не напечаталось строк: {failed} — проверьте принтер этикеток.", $"{printed} даана басылды, басылбаган саптар: {failed} — этикетка принтерин текшериңиз.",
                $"Printed {printed}, failed rows: {failed} — check the label printer.", $"{printed} yazdırıldı, yazdırılamayan satır: {failed} — etiket yazıcısını kontrol edin.",
                $"{printed} dona chop etildi, chop etilmagan qatorlar: {failed} — yorliq printerini tekshiring.");
    }

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
        target.Bind(property, this.GetResourceObservable(key));
}
