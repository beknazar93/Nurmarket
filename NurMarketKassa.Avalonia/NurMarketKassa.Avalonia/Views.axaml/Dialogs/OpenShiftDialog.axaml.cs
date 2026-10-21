using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class OpenShiftDialog : Window
{
    // Поле заполняется значением в инвариантной культуре (точка), поэтому и разбор
    // должен быть инвариантным — иначе на ru-RU/de-DE сумма теряется или растёт в 100 раз.
    // 2026-10-15, ревью: NumberStyles.Number считал запятую разделителем тысяч — «1500,50» с русской
    // цифровой клавиатуры становилось 150050 (как в CloseShiftDialog). Общий разбор — CashAmountInput.
    public decimal OpeningCash => CashAmountInput.Parse(OpeningCashBox.Text) ?? 0;

    public decimal? SuggestedBalance { get; set; }

    public OpenShiftDialog()
    {
        InitializeComponent();
        Opened += OnDialogOpened;
    }

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (SuggestedBalance.HasValue)
        {
            SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(SuggestedBalance);
            OpeningCashBox.Text = SuggestedBalance.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        OpeningCashBox.Focus();
        OpeningCashBox.SelectAll();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!CashAmountInput.TryParse(OpeningCashBox.Text, out var amount) || amount < 0)
        {
            PosMessageBox.Show(this, Tr.T("Введите сумму не меньше нуля.", "Нөлдөн кем эмес сумманы киргизиңиз.", "Enter an amount of zero or more.", "Sıfır veya daha büyük bir tutar girin.", "Noldan kam bo'lmagan summani kiriting."), Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
