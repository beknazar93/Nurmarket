using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Диалог «Дополнительная услуга» (2026-09-07): название, сумма, количество и тип
/// «Доход / Расход» (по умолчанию доход). Возвращает введённые значения через свойства,
/// вызывающий код (MainWindow.Dialogs.AddCustomItemAsync) кладёт строку в чек.</summary>
public partial class CustomServiceDialog : Window
{
    public string ServiceName { get; private set; } = "";
    public double Price { get; private set; }
    public double Quantity { get; private set; } = 1;
    public bool IsExpense { get; private set; }

    /// <summary>2026-10-03: кассир выбрал товар из каталога вместо «Доп. услуги» — его и кладём в чек.</summary>
    public CatalogProductTileVm? SelectedProduct { get; private set; }

    public CustomServiceDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
        NameBox.TextChanged += (_, _) => ShowCatalogSuggestions();
    }

    /// <summary>2026-10-03, проверка у клиента: из 142 строк продаж 2 были «Доп. услугой» «конфет», хотя конфеты
    /// есть в каталоге — такие строки не списывают остаток и идут без себестоимости (прибыль завышена, «строки
    /// продаж без товара»). Похожие товары каталога — кнопками под названием (не больше 4), только для дохода.</summary>
    private void ShowCatalogSuggestions()
    {
        SuggestPanel.Children.Clear();
        var text = (NameBox.Text ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
        var keys = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3).Select(w => w.Length > 4 ? w[..4] : w).ToList();
        if (keys.Count == 0 || ExpenseRadio.IsChecked == true)
        {
            SuggestPanel.IsVisible = false;
            return;
        }

        List<CatalogProductTileVm> found;
        try
        {
            found = CatalogCacheService.Products
                .Where(p => !string.IsNullOrWhiteSpace(p.Title)
                            && keys.All(k => p.Title.ToLowerInvariant().Replace('ё', 'е').Contains(k, StringComparison.Ordinal)))
                .OrderByDescending(p => p.Quantity > 0).ThenBy(p => p.Title.Length).Take(4).ToList();
        }
        catch
        {
            found = [];
        }

        if (found.Count == 0)
        {
            SuggestPanel.IsVisible = false;
            return;
        }

        var hint = new TextBlock
        {
            Text = Tr.T("Есть в каталоге — лучше продать товаром (спишется остаток):", "Каталогдо бар — товар катары саткан жакшы (калдык кемийт):",
                "In the catalog — better sell it as a product (stock is deducted):", "Katalogda var — ürün olarak satmak daha iyi (stoktan düşülür):",
                "Katalogda bor — mahsulot sifatida sotgan ma'qul (qoldiqdan ayiriladi):"),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        SuggestPanel.Children.Add(hint);
        foreach (var product in found)
        {
            var b = new Button
            {
                Content = $"{product.Title} — {product.PriceLine}" + (product.Quantity > 0 ? "" : Tr.T(" (нет в наличии)", " (калдыкта жок)", " (out of stock)", " (stokta yok)", " (mavjud emas)")),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            };
            b.Bind(Button.BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
            b.Bind(Button.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
            b.Bind(Button.ForegroundProperty, this.GetResourceObservable("BrushText"));
            b.Click += (_, _) =>
            {
                SelectedProduct = product;
                Close(true);
            };
            SuggestPanel.Children.Add(b);
        }
        SuggestPanel.IsVisible = true;
    }

    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            ShowError(Tr.T("Введите название", "Аталышын киргизиңиз", "Enter a name", "Bir ad girin", "Nomini kiriting"));
            NameBox.Focus();
            return;
        }

        if (!TryParseNumber(PriceBox.Text, out var price) || price <= 0)
        {
            ShowError(Tr.T("Введите сумму больше нуля", "Нөлдөн чоң сумма киргизиңиз", "Enter an amount greater than zero", "Sıfırdan büyük bir tutar girin", "Noldan katta summa kiriting"));
            PriceBox.Focus();
            return;
        }

        if (!TryParseNumber(QuantityBox.Text, out var quantity) || quantity <= 0)
        {
            ShowError(Tr.T("Количество должно быть больше нуля", "Саны нөлдөн чоң болушу керек", "Quantity must be greater than zero", "Miktar sıfırdan büyük olmalıdır", "Miqdor noldan katta bo'lishi kerak"));
            QuantityBox.Focus();
            return;
        }

        ServiceName = name;
        Price = price;
        Quantity = quantity;
        IsExpense = ExpenseRadio.IsChecked == true;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }

    /// <summary>Сумму вводят и с запятой, и с точкой — принимаем оба варианта.</summary>
    private static bool TryParseNumber(string? text, out double value)
    {
        var normalized = (text ?? "").Trim().Replace(',', '.').Replace(" ", "");
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }
}
