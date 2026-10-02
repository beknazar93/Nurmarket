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
/// </summary>
public sealed class VariantEditorWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ICatalogApiService _api;
    private readonly string _productId;
    private readonly double _basePrice;
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _quickSizes = new() { Watermark = "S, M, L, XL", MinWidth = 180 };
    private readonly TextBox _quickColors = new() { Watermark = "Чёрный, Белый", MinWidth = 200 };
    private readonly Button _save = new() { Padding = new Thickness(22, 10), MinWidth = 140, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly List<Row> _items = new();
    private readonly List<string> _deletedIds = new();

    private sealed class Row
    {
        public ProductVariantDto Source = new();
        public TextBox Size = new() { Width = 90 };
        public TextBox Color = new() { Width = 150 };
        public TextBox Price = new() { Width = 110 };
        public TextBox Qty = new() { Width = 80 };
        public TextBox Barcode = new() { Width = 170 };
        public CheckBox Active = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        public Control? View;
    }

    public VariantEditorWindow(ICatalogApiService api, string productId, string productTitle, double basePrice)
    {
        _api = api;
        _productId = productId;
        _basePrice = basePrice;
        Title = Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colors", "Bedenler ve renkler", "O'lchamlar va ranglar") + " — " + productTitle;
        Width = 900;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var root = new DockPanel { Margin = new Thickness(24, 18) };

        var top = new StackPanel { Spacing = 8 };
        DockPanel.SetDock(top, Dock.Top);
        var title = new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        top.Children.Add(title);
        top.Children.Add(Soft(Tr.T(
            $"Каждый размер/цвет — со своим остатком. Цена пустая — продаётся по цене товара ({basePrice:0.##} сом); ниже — касса покажет скидку. В кассе (сфера «Одежда») при выборе товара откроется выбор размера и цвета.",
            $"Ар бир өлчөм/түс — өз калдыгы менен. Баасы бош — товардын баасы ({basePrice:0.##} сом); арзаныраак — касса арзандатуу көрсөтөт. Кассада («Кийим» тармагы) товарды тандаганда өлчөм жана түс тандалат.",
            $"Each size/color has its own stock. Empty price — product price ({basePrice:0.##} som); lower — the till shows a discount. At the till (Clothing mode) a size/color picker opens.",
            $"Her beden/renk kendi stokuyla. Fiyat boş — ürün fiyatı ({basePrice:0.##} som); daha düşük — kasa indirim gösterir. Kasada (Giyim) beden/renk seçimi açılır.",
            $"Har bir o'lcham/rang — o'z qoldig'i bilan. Narx bo'sh — mahsulot narxi ({basePrice:0.##} so'm); pastroq — kassa chegirma ko'rsatadi. Kassada (Kiyim) o'lcham/rang tanlovi ochiladi.")));

        // Быстро заполнить: размеры × цвета.
        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 6) };
        quick.Children.Add(Soft(Tr.T("Быстро:", "Тез:", "Quick:", "Hızlı:", "Tez:"), center: true));
        quick.Children.Add(_quickSizes);
        quick.Children.Add(Soft("×", center: true));
        quick.Children.Add(_quickColors);
        var gen = new Button { Content = Tr.T("Добавить все сочетания", "Бардык айкалыштарды кошуу", "Add all combinations", "Tüm kombinasyonları ekle", "Barcha birikmalarni qo'shish") };
        gen.Click += (_, _) => GenerateCombos();
        quick.Children.Add(gen);
        top.Children.Add(quick);

        // Заголовки колонок.
        var head = RowGrid();
        AddCell(head, Soft(Tr.T("Размер", "Өлчөм", "Size", "Beden", "O'lcham")), 0);
        AddCell(head, Soft(Tr.T("Цвет", "Түс", "Color", "Renk", "Rang")), 1);
        AddCell(head, Soft(Tr.T("Цена", "Баа", "Price", "Fiyat", "Narx")), 2);
        AddCell(head, Soft(Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq")), 3);
        AddCell(head, Soft(Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod")), 4);
        AddCell(head, Soft(Tr.T("Продаётся", "Сатылат", "On sale", "Satışta", "Sotuvda")), 5);
        top.Children.Add(head);
        root.Children.Add(top);

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        bottom.Children.Add(_status);
        var addRow = new Button { Content = Tr.T("+ Добавить строку", "+ Сап кошуу", "+ Add row", "+ Satır ekle", "+ Qator qo'shish"), Margin = new Thickness(0, 0, 10, 0) };
        addRow.Click += (_, _) => AddRow(new ProductVariantDto());
        Grid.SetColumn(addRow, 1);
        bottom.Children.Add(addRow);
        var close = new Button { Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(18, 10) };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        bottom.Children.Add(close);
        _save.Content = Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");
        _save.Classes.Add("btn-primary");
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        Grid.SetColumn(_save, 3);
        bottom.Children.Add(_save);
        root.Children.Add(bottom);

        root.Children.Add(new ScrollViewer { Content = _rows });
        Content = root;

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
                return double.TryParse(s.Replace(',', '.'), NumberStyles.Any, Inv, out var n) ? 100 + n : 10_000;
            }
            foreach (var v in list.OrderBy(v => Rank(v.Size)).ThenBy(v => v.Size, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Color, StringComparer.CurrentCultureIgnoreCase))
                AddRow(v);
            _status.Text = list.Count == 0
                ? Tr.T("Вариантов пока нет — добавьте размеры и цвета.", "Варианттар азырынча жок — өлчөмдөрдү жана түстөрдү кошуңуз.", "No variants yet — add sizes and colors.", "Henüz varyant yok — beden ve renk ekleyin.", "Hozircha variantlar yo'q — o'lcham va ranglarni qo'shing.")
                : Tr.T($"Вариантов: {list.Count}", $"Варианттар: {list.Count}", $"Variants: {list.Count}", $"Varyant: {list.Count}", $"Variantlar: {list.Count}");
        }
        catch (Exception ex)
        {
            _status.Text = Tr.T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Could not load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + ex.Message;
        }
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
        var del = new Button { Content = "✕", Width = 36, Height = 32, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
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
                double? price = double.TryParse(row.Price.Text?.Replace(',', '.'), NumberStyles.Any, Inv, out var p) && p > 0 ? p : null;
                double qty = double.TryParse(row.Qty.Text?.Replace(',', '.'), NumberStyles.Any, Inv, out var q) ? q : 0;
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
            _status.Text = errors.Count == 0
                ? Tr.T($"Сохранено изменений: {saved}.", $"Сакталды: {saved}.", $"Saved changes: {saved}.", $"Kaydedilen değişiklik: {saved}.", $"Saqlangan o'zgarishlar: {saved}.")
                : Tr.T("Не всё сохранилось: ", "Баары сакталган жок: ", "Not everything was saved: ", "Hepsi kaydedilmedi: ", "Hammasi saqlanmadi: ") + string.Join("; ", errors);
        }
        finally
        {
            _save.IsEnabled = true;
        }
    }

    private static Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("100,160,120,90,180,90,44") };

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
