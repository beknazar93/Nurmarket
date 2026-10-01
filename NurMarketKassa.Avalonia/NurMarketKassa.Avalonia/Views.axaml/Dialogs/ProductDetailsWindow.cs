using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
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
/// </summary>
public sealed class ProductDetailsWindow : Window
{
    private readonly CatalogProductTileVm _product;
    private readonly Action _addToCart;
    private readonly TextBlock _description = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, LineHeight = 22 };

    public ProductDetailsWindow(CatalogProductTileVm product, Action addToCart)
    {
        _product = product;
        _addToCart = addToCart;
        Title = product.Title;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };

        var root = new StackPanel { Margin = new Thickness(28, 24), Spacing = 14 };

        var title = new TextBlock { Text = product.Title, FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);

        // Фото и главное: цена, остаток.
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (!string.IsNullOrWhiteSpace(product.ProductImagePath)
            && AssetPathToBitmapConverter.Instance.Convert(product.ProductImagePath, typeof(Bitmap), null, CultureInfo.CurrentCulture) is Bitmap bitmap)
        {
            top.Children.Add(new Border
            {
                Width = 150,
                Height = 150,
                CornerRadius = new CornerRadius(12),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 18, 0),
                Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill },
            });
        }

        var facts = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var price = new TextBlock
        {
            Text = $"{LocalCartService.ParsePrice(product.PriceLine).ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} "
                   + Tr.T("сом", "сом", "som", "som", "so'm"),
            FontSize = 26,
            FontWeight = FontWeight.Bold,
        };
        Use(price, TextBlock.ForegroundProperty, "BrushCatalogPrice");
        facts.Children.Add(price);
        if (!product.IsService)
            facts.Children.Add(Fact(Tr.T("Остаток", "Калдык", "In stock", "Stok", "Qoldiq"), product.StockWithUnitText));
        facts.Children.Add(Fact(Tr.T("Категория", "Категория", "Category", "Kategori", "Kategoriya"), product.Category));
        facts.Children.Add(Fact(Tr.T("Бренд", "Бренд", "Brand", "Marka", "Brend"), product.Brand));
        facts.Children.Add(Fact(Tr.T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), product.Barcode));
        Grid.SetColumn(facts, top.Children.Count);
        top.Children.Add(facts);
        root.Children.Add(top);

        // Описание продавца.
        var head = new TextBlock { Text = Tr.T("Описание", "Сүрөттөмө", "Description", "Açıklama", "Tavsif"), FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(head, TextBlock.ForegroundProperty, "BrushTextSoft");
        root.Children.Add(head);
        var box = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12),
            Child = new ScrollViewer { MaxHeight = 300, Content = _description },
        };
        Use(box, Border.BackgroundProperty, "BrushPanelSoft");
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        root.Children.Add(box);
        ShowDescription(product.Description, loading: string.IsNullOrWhiteSpace(product.Description));

        // Кнопки.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var close = new Button { Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), Padding = new Thickness(20, 10), MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        var add = new Button { Content = Tr.T("Добавить в чек", "Чекке кошуу", "Add to receipt", "Fişe ekle", "Chekka qo'shish"), Padding = new Thickness(20, 10), MinWidth = 160, HorizontalContentAlignment = HorizontalAlignment.Center };
        add.Classes.Add("btn-primary");
        add.Click += (_, _) =>
        {
            _addToCart();
            Close();
        };
        buttons.Children.Add(close);
        buttons.Children.Add(add);
        root.Children.Add(buttons);

        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = root,
        };
        Use(frame, Border.BorderBrushProperty, "BrushBorder");
        Content = frame;

        if (string.IsNullOrWhiteSpace(product.Description))
            Opened += async (_, _) => await LoadDescriptionAsync().ConfigureAwait(true);
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
            _description.Text = text.Trim();
            Use(_description, TextBlock.ForegroundProperty, "BrushText");
            return;
        }

        _description.Text = loading
            ? Tr.T("Загружаю описание…", "Сүрөттөмө жүктөлүүдө…", "Loading the description…", "Açıklama yükleniyor…", "Tavsif yuklanmoqda…")
            : Tr.T("Описание не заполнено. Его можно добавить на складе: карточка товара → «Описание».",
                "Сүрөттөмө толтурулган эмес. Аны кампада кошсо болот: товардын карточкасы → «Сүрөттөмө».",
                "No description yet. Add it in the warehouse: product card → “Description”.",
                "Açıklama yok. Depoda ekleyebilirsiniz: ürün kartı → «Açıklama».",
                "Tavsif kiritilmagan. Uni omborda qo'shish mumkin: mahsulot kartasi → «Tavsif».");
        Use(_description, TextBlock.ForegroundProperty, "BrushTextSoft");
    }

    private Control Fact(string label, string? value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var l = new TextBlock { Text = label + ":", FontSize = 13.5 };
        Use(l, TextBlock.ForegroundProperty, "BrushTextSoft");
        var v = new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330 };
        Use(v, TextBlock.ForegroundProperty, "BrushText");
        row.Children.Add(l);
        row.Children.Add(v);
        return row;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
