using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

    public CustomServiceDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
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
