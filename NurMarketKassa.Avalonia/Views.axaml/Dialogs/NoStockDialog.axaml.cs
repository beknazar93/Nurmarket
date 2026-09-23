using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class NoStockDialog : Window
{
    public bool GoToSite { get; private set; }

    public NoStockDialog()
    {
        InitializeComponent();
    }

    public NoStockDialog(
        string productName,
        double warehouseStock,
        double quantityInCurrentCart,
        double quantityReservedElsewhere,
        double availableToAdd,
        bool mustWeigh = false)
        : this()
    {
        var unit = mustWeigh ? "кг" : "шт.";
        var warehouseText = $"{FormatQty(warehouseStock)} {unit}";

        ProductNameText.Text = $"Товар:\n{productName}";
        if (warehouseStock <= 1e-6)
        {
            TitleText.Text = "Товар закончился на складе";
            MessageText.Text = "По данным каталога остаток товара действительно равен нулю.";
        }
        else if (quantityReservedElsewhere <= 1e-6
                 && quantityInCurrentCart >= warehouseStock - 1e-6)
        {
            TitleText.Text = "Остаток уже в корзине";
            MessageText.Text = $"Весь доступный остаток ({warehouseText}) уже добавлен в текущую корзину.";
        }
        else if (quantityReservedElsewhere > 1e-6 && availableToAdd <= 1e-6)
        {
            TitleText.Text = "Остаток уже зарезервирован";
            MessageText.Text = quantityInCurrentCart > 1e-6
                ? $"Весь доступный остаток ({warehouseText}) распределён между текущим и другими открытыми или отложенными чеками."
                : $"Весь доступный остаток ({warehouseText}) находится в других открытых или отложенных чеках.";
        }
        else
        {
            TitleText.Text = "Недостаточно остатка";
            MessageText.Text = "Выбранное количество превышает количество, которое ещё можно добавить в этот чек.";
        }

        AvailableText.Text =
            $"Остаток на складе: {warehouseText}\nМожно добавить: {FormatQty(availableToAdd)} {unit}";
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        // Close must run synchronously on the UI thread while ShowDialog's nested loop
        // is active. Dispatcher.Post after a blocking GetResult() never runs → dialog stuck.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Close(true);
            return;
        }

        Dispatcher.UIThread.Post(() => Close(true));
    }
}
