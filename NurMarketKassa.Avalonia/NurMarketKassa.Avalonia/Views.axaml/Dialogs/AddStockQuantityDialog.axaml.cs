using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// Второй шаг продажи при нулевом остатке (см. NoStockDialog): после «Подтвердить и
/// добавить» кассир указывает, сколько реально добавить на склад — эта величина проводится
/// через инвентаризационный акт (см. MainWindow.Dialogs.cs.TryReplenishStockForOverrideAsync),
/// а не просто "виртуально" пропускает проверку остатка.
/// </summary>
public partial class AddStockQuantityDialog : Window
{
    private readonly string _unit;

    public AddStockQuantityDialog()
    {
        InitializeComponent();
        _unit = Tr.T("шт.", "даана", "pcs", "adet", "dona");
        // 2026-10-03: скан штрихкода в это поле не должен становиться количеством.
        NurMarketKassa.AvaloniaHost.Services.QuantityInputGuard.Attach(QuantityBox, () =>
        {
            ErrorText.Text = Tr.T("Это похоже на штрихкод — впишите количество (не больше 5 знаков).",
                "Бул штрихкодго окшош — санын жазыңыз (5 белгиден ашпасын).",
                "That looks like a barcode — enter the quantity (up to 5 characters).",
                "Bu bir barkoda benziyor — miktarı girin (en fazla 5 karakter).",
                "Bu shtrix-kodga o'xshaydi — miqdorni kiriting (5 belgidan oshmasin).");
            ErrorText.IsVisible = true;
        });
    }

    public AddStockQuantityDialog(string productName, double suggestedQuantity, bool mustWeigh)
        : this()
    {
        _unit = mustWeigh ? Tr.T("кг", "кг", "kg", "kg", "kg") : Tr.T("шт.", "даана", "pcs", "adet", "dona");
        ProductNameText.Text = productName;
        QuantityBox.Text = FormatQty(suggestedQuantity);
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => CloseWithResult(null);

    private void BtnConfirm_Click(object? sender, RoutedEventArgs e)
    {
        var text = QuantityBox.Text?.Trim().Replace(',', '.') ?? "";
        if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
        {
            ErrorText.Text = Tr.T(
                $"Укажите положительное количество ({_unit}).",
                $"Оң сан көрсөтүңүз ({_unit}).", $"Enter a positive quantity ({_unit}).", $"Sıfırdan büyük bir miktar girin ({_unit}).", $"Musbat miqdorni kiriting ({_unit}).");
            ErrorText.IsVisible = true;
            return;
        }

        CloseWithResult(qty);
    }

    private void CloseWithResult(double? quantity)
    {
        // Close must run synchronously on the UI thread while ShowDialog's nested loop
        // is active — see NoStockDialog for the same constraint.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Close(quantity);
            return;
        }

        Dispatcher.UIThread.Post(() => Close(quantity));
    }
}
