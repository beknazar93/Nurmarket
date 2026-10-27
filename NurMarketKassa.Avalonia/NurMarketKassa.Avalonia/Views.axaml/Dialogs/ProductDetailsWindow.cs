using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NurMarketKassa.AvaloniaHost.Converters;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-01, владелец: «в режиме магазина одежды и другое в каталоге должна быть кнопка
/// "Подробнее", в которой есть описание товара, введённое продавцом». Карточка товара: фото, название,
/// цена, остаток, категория, бренд, штрихкод и описание. Описание приходит с сервера в каталоге; в
/// локальной базе кассы его нет — если в каталоге пусто, карточка спрашивает товар у сервера.
/// Окно собрано в коде, цвета — из темы.
/// 2026-10-23, владелец: «описание товара нормальным сделай» — карточка как в магазине приложений: крупное фото (клик —
/// на весь экран), название, цена, плашка наличия, чипы категории и бренда, таблица «Характеристики», описание со сворачиванием.
/// </summary>
public sealed class ProductDetailsWindow : Window
{
    private readonly CatalogProductTileVm _product;
    private readonly Action _addToCart;
    private readonly TextBlock _description = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
    private readonly Button _toggle = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 8), MinHeight = 44, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private bool _expanded;
    // 2026-10-23: длиннее — сворачиваем до 6 строк с кнопкой «Показать полностью».
    private const int CollapsedLines = 6;
    private const int LongTextChars = 260;

    public ProductDetailsWindow(CatalogProductTileVm product, Action addToCart)
    {
        _product = product;
        _addToCart = addToCart;
        Title = product.Title;
        // 2026-10-22 (модалки, пакет 1): общая оболочка ModalShell (M 672) вместо своей рамки 600 без шапки — название
        // в шапке с ✕ (Esc), тело прокручивается, «Закрыть» / «Добавить в чек» закреплены в подвале (были < 44 px).
        SectionCards.AsModal(this, 672);

        var root = new StackPanel { Spacing = 18 };

        // 2026-10-23: фото слева (или значок-заглушка), справа — название, цена, наличие, чипы.
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        top.Children.Add(BuildPhoto(product));

        var main = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = product.Title, FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, LineHeight = 28 };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        main.Children.Add(name);
        var price = new TextBlock
        {
            Text = $"{LocalCartService.ParsePrice(product.PriceLine).ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} "
                   + Tr.T("сом", "сом", "som", "som", "so'm"),
            FontSize = 28,
            FontWeight = FontWeight.Bold,
        };
        Use(price, TextBlock.ForegroundProperty, "BrushCatalogPrice");
        main.Children.Add(price);
        if (!product.IsService)
            main.Children.Add(StockPill(product));
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var chip in new[] { product.Category, product.Brand })
        {
            if (string.IsNullOrWhiteSpace(chip))
                continue;
            var pill = SectionCards.Pill(this, chip.Trim());
            pill.Margin = new Thickness(0, 0, 8, 6);
            pill.Padding = new Thickness(12, 5);
            ((TextBlock)pill.Child!).FontSize = 13;
            chips.Children.Add(pill);
        }
        if (chips.Children.Count > 0)
            main.Children.Add(chips);
        Grid.SetColumn(main, 1);
        top.Children.Add(main);
        root.Children.Add(top);

        // Характеристики: «подпись — значение», только заполненные.
        var specs = BuildSpecs(product);
        if (specs != null)
        {
            root.Children.Add(Heading(Tr.T("Характеристики", "Мүнөздөмөлөр", "Specifications", "Özellikler", "Xususiyatlar")));
            root.Children.Add(specs);
        }

        // Описание продавца.
        root.Children.Add(Heading(Tr.T("Описание", "Сүрөттөмө", "Description", "Açıklama", "Tavsif")));
        var descBox = new StackPanel { Spacing = 0 };
        descBox.Children.Add(_description);
        _toggle.Click += (_, _) =>
        {
            _expanded = !_expanded;
            ApplyCollapse();
        };
        descBox.Children.Add(_toggle);
        root.Children.Add(descBox);
        ShowDescription(product.Description, loading: string.IsNullOrWhiteSpace(product.Description));

        // Кнопки. 2026-10-22 (модалки, пакет 1): «Закрыть» — обводка, Esc и ✕; «Добавить в чек» — одна главная (Enter).
        var close = SectionCards.Cancel(Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var add = SectionCards.Primary(Tr.T("Добавить в чек", "Чекке кошуу", "Add to receipt", "Fişe ekle", "Chekka qo'shish"), isDefault: true);
        var added = false;
        add.Click += (_, _) =>
        {
            // Двойное нажатие (или Enter + клик) не добавляет товар дважды.
            if (added)
                return;
            added = true;
            _addToCart();
            Close();
        };

        // 2026-10-26 (ТЗ хозмаг, ХМ5): «Сколько нужно?» — калькулятор расхода, только в магазине «Хозтовары и стройматериалы».
        var buttons = SectionCards.Buttons(close, add);
        if (MarketSpheres.IsHardware && !product.IsService)
            buttons.Children.Insert(0, BuildCalcButton());

        Content = SectionCards.Shell(Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), null, root, buttons);

        if (string.IsNullOrWhiteSpace(product.Description))
            Opened += async (_, _) => await LoadDescriptionAsync().ConfigureAwait(true);
    }

    /// <summary>2026-10-26 (ХМ5): кнопка «Сколько нужно?» открывает калькулятор расхода; «В чек» из него кладёт товар в чек тем же
    /// способом, что добавление с известным количеством (MainWindow.AddWeighedProductWithKnownWeightAsync), и закрывает карточку.</summary>
    private Button BuildCalcButton()
    {
        var calc = SectionCards.Secondary(Tr.T("Сколько нужно?", "Канча керек?", "How much do I need?", "Ne kadar gerekir?", "Qancha kerak?"));
        calc.Click += async (_, _) =>
        {
            var window = new HardwareCalcWindow(_product, quantity =>
                (Owner as Views.MainKassir.MainWindow ?? throw new InvalidOperationException("нет окна кассы"))
                    .AddWeighedProductWithKnownWeightAsync(_product, quantity));
            if (await window.ShowDialog<bool>(this).ConfigureAwait(true))
                Close();
        };
        return calc;
    }

    /// <summary>2026-10-23: фото 170×170 (клик — на весь экран) или заглушка-значок «товар», если фото нет.</summary>
    private Control BuildPhoto(CatalogProductTileVm product)
    {
        var frame = new Border
        {
            Width = 170,
            Height = 170,
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 20, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Use(frame, Border.BackgroundProperty, "BrushPanelSoft");
        Use(frame, Border.BorderBrushProperty, "BrushBorder");
        if (!string.IsNullOrWhiteSpace(product.ProductImagePath)
            && AssetPathToBitmapConverter.Instance.Convert(product.ProductImagePath, typeof(Bitmap), null, CultureInfo.CurrentCulture) is Bitmap bitmap)
        {
            frame.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
            frame.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(frame, Tr.T("Открыть фото на весь экран", "Сүрөттү толук экранда ачуу", "Open the photo full screen", "Fotoğrafı tam ekran aç", "Rasmni to'liq ekranda ochish"));
            frame.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                ShowFullPhoto(bitmap);
            };
        }
        else
        {
            var icon = new PathIcon
            {
                Width = 56,
                Height = 56,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Data = Application.Current?.TryFindResource("IconPackage", out var g) == true ? g as Geometry : null,
            };
            Use(icon, TemplatedControl.ForegroundProperty, "BrushTextSoft");
            icon.Opacity = 0.55;
            frame.Child = icon;
        }
        return frame;
    }

    /// <summary>2026-10-23: фото на весь экран; закрывается кликом, касанием или любой клавишей.</summary>
    private void ShowFullPhoto(Bitmap bitmap)
    {
        var viewer = new Window
        {
            WindowState = WindowState.FullScreen,
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Black,
            ShowInTaskbar = false,
            Content = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(12) },
        };
        viewer.PointerPressed += (_, _) => viewer.Close();
        viewer.KeyDown += (_, _) => viewer.Close();
        _ = viewer.ShowDialog(this);
    }

    /// <summary>2026-10-23: плашка наличия — «В наличии 12 шт» (зелёная), «Заканчивается · 5 шт» (оранжевая), «Нет в наличии» (серая).</summary>
    private Control StockPill(CatalogProductTileVm product)
    {
        string text, bg, fg;
        if (product.Quantity <= 0)
        {
            text = Tr.T("Нет в наличии", "Кампада жок", "Out of stock", "Stokta yok", "Mavjud emas");
            bg = "BrushPanelSoft";
            fg = "BrushTextSoft";
        }
        else if (product.IsLowStock)
        {
            text = Tr.T("Заканчивается", "Бүтүп баратат", "Running low", "Azalıyor", "Tugayapti") + " · " + product.StockWithUnitText;
            bg = "BrushWarningSoft";
            fg = "BrushWarning";
        }
        else
        {
            text = Tr.T("В наличии", "Кампада бар", "In stock", "Stokta", "Mavjud") + " " + product.StockWithUnitText;
            bg = "BrushSuccessSoft";
            fg = "BrushSuccess";
        }
        var pill = SectionCards.Pill(this, text, bg, fg);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        pill.Padding = new Thickness(12, 5);
        ((TextBlock)pill.Child!).FontSize = 14;
        return pill;
    }

    /// <summary>2026-10-23: таблица «Характеристики»; пустые поля не показываем. null — показывать нечего.</summary>
    private Control? BuildSpecs(CatalogProductTileVm product)
    {
        var rows = new List<(string Label, string Value)>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add((label, value.Trim()));
        }
        Add(Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), product.Barcode);
        Add(Tr.T("Артикул", "Артикул", "SKU", "Stok kodu", "Artikul"), product.Article);
        Add(Tr.T("Код товара", "Товар коду", "Product code", "Ürün kodu", "Mahsulot kodi"), product.ProductCode);
        Add(Tr.T("Единица измерения", "Өлчөө бирдиги", "Unit", "Birim", "O'lchov birligi"), product.Unit);
        Add(Tr.T("Страна", "Өлкө", "Country", "Ülke", "Mamlakat"), product.Country);
        if (product.WeightKg is > 0)
            Add(Tr.T("Вес", "Салмагы", "Weight", "Ağırlık", "Og'irligi"),
                product.WeightKg.Value.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) + " " + Tr.T("кг", "кг", "kg", "kg", "kg"));
        if (rows.Count == 0)
            return null;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = rows[i].Label, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 11, 8, 11) };
            Use(l, TextBlock.ForegroundProperty, "BrushTextSoft");
            var v = new TextBlock { Text = rows[i].Value, FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 11, 16, 11) };
            Use(v, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetRow(l, i);
            Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            if (i > 0)
            {
                var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
                Use(line, Border.BackgroundProperty, "BrushBorder");
                Grid.SetRow(line, i);
                Grid.SetColumnSpan(line, 2);
                grid.Children.Add(line);
            }
        }
        var box = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = grid, ClipToBounds = true };
        Use(box, Border.BackgroundProperty, "BrushPanelSoft");
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        return box;
    }

    private TextBlock Heading(string text)
    {
        var head = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 2, 0, -6) };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        return head;
    }

    private async Task LoadDescriptionAsync()
    {
        try
        {
            var detail = await App.CatalogApi.ProductsDetailAsync(_product.Id).ConfigureAwait(true);
            string? text = null;
            if (detail is JsonElement d && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("description", out var el) && el.ValueKind == JsonValueKind.String)
                text = el.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                _product.Description = text;
            ShowDescription(text, loading: false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Карточка товара: описание с сервера не получено ({ex.Message}).", "CATALOG");
            ShowDescription(null, loading: false);
        }
    }

    private void ShowDescription(string? text, bool loading)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            // 2026-10-23: абзацы сохраняем, три и более переводов строки подряд сжимаем до пустой строки.
            var clean = System.Text.RegularExpressions.Regex.Replace(text.Replace("\r\n", "\n").Trim(), "\n{3,}", "\n\n");
            _description.Text = clean;
            Use(_description, TextBlock.ForegroundProperty, "BrushText");
            _expanded = false;
            _toggle.IsVisible = clean.Length > LongTextChars || clean.Count(c => c == '\n') >= CollapsedLines;
            ApplyCollapse();
            return;
        }

        _toggle.IsVisible = false;
        _description.MaxLines = 0;
        _description.TextTrimming = TextTrimming.None;
        _description.Text = loading
            ? Tr.T("Загружаю описание…", "Сүрөттөмө жүктөлүүдө…", "Loading the description…", "Açıklama yükleniyor…", "Tavsif yuklanmoqda…")
            : Tr.T("Описание не добавлено продавцом", "Сүрөттөмөнү сатуучу кошкон жок", "No description added by the seller", "Satıcı açıklama eklemedi", "Sotuvchi tavsif qo'shmagan");
        Use(_description, TextBlock.ForegroundProperty, "BrushTextSoft");
    }

    /// <summary>2026-10-23: длинное описание свёрнуто до 6 строк; кнопка «Показать полностью» / «Свернуть».</summary>
    private void ApplyCollapse()
    {
        var collapsed = _toggle.IsVisible && !_expanded;
        _description.MaxLines = collapsed ? CollapsedLines : 0;
        _description.TextTrimming = collapsed ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        var label = new TextBlock
        {
            Text = _expanded
                ? Tr.T("Свернуть", "Жыйыштыруу", "Collapse", "Daralt", "Yig'ish")
                : Tr.T("Показать полностью", "Толугу менен көрсөтүү", "Show all", "Tamamını göster", "To'liq ko'rsatish"),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
        };
        Use(label, TextBlock.ForegroundProperty, "BrushAccentStrong");
        _toggle.Content = label;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
