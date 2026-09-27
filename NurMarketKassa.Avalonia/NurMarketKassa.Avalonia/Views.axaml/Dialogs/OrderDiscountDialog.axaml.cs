using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class OrderDiscountDialog : Window
{
    public string DiscountMode { get; private set; } = "percent";
    public string DiscountScope { get; private set; } = "check";
    public string DiscountValue { get; private set; } = "";
    public bool ClearRequested { get; private set; }

    public OrderDiscountDialog() : this("", "") { }

    public OrderDiscountDialog(string currentPercent, string currentSum)
    {
        InitializeComponent();

        var pct = (currentPercent ?? "").Trim();
        var sum = (currentSum ?? "").Trim();

        if (!string.IsNullOrEmpty(sum))
        {
            DiscountTypeToggle.IsChecked = false; // sum
            ValueBox.Text = sum;
        }
        else
        {
            DiscountTypeToggle.IsChecked = true; // percent
            ValueBox.Text = pct;
        }

        ScopeCheckBox.IsChecked = false; // весь чек
        DiscountScope = "check";
        SyncModeUi();
    }

    public void SetItemMode(string itemTitle, string? currentDiscountType, decimal? currentDiscountValue)
    {
        Title = Tr.T("Скидка на товар", "Товарга арзандатуу", "Product discount", "Ürün indirimi", "Mahsulotga chegirma");
        HeaderTitleText.Text = Tr.T("Скидка на товар", "Товарга арзандатуу", "Product discount", "Ürün indirimi", "Mahsulotga chegirma");
        ItemTitleLabel.Text = itemTitle;
        ItemTitleLabel.IsVisible = true;
        ScopePanel.IsVisible = false;
        ScopeCheckBox.IsChecked = true;
        DiscountScope = "item";

        if (currentDiscountValue.HasValue && currentDiscountType != null)
        {
            if (currentDiscountType == "percent")
            {
                DiscountTypeToggle.IsChecked = true;
                ValueBox.Text = currentDiscountValue.Value.ToString("F0", CultureInfo.InvariantCulture);
            }
            else if (currentDiscountType == "sum")
            {
                DiscountTypeToggle.IsChecked = false;
                ValueBox.Text = currentDiscountValue.Value.ToString("F2", CultureInfo.InvariantCulture);
            }
        }
        else
        {
            ValueBox.Text = "";
        }

        SyncModeUi();
    }

    private void DiscountType_Changed(object? sender, RoutedEventArgs e) => SyncModeUi();

    private void SyncModeUi()
    {
        var isPercent = DiscountTypeToggle.IsChecked == true;
        DiscountMode = isPercent ? "percent" : "sum";
        ValueLabel.Text = isPercent ? Tr.T("Введите процент скидки", "Арзандатуу пайызын киргизиңиз", "Enter discount percentage", "İndirim yüzdesini girin", "Chegirma foizini kiriting") : Tr.T("Введите сумму скидки", "Арзандатуу суммасын киргизиңиз", "Enter discount amount", "İndirim tutarını girin", "Chegirma summasini kiriting");
        DiscountTypeLabel.Text = isPercent ? Tr.T("Режим скидки: Процент (%)", "Арзандатуу режими: Пайыз (%)", "Discount mode: percent (%)", "İndirim modu: Yüzde (%)", "Chegirma rejimi: Foiz (%)") : Tr.T("Режим скидки: Сумма (сом)", "Арзандатуу режими: Сумма (сом)", "Discount mode: amount (som)", "İndirim modu: Tutar (som)", "Chegirma rejimi: Summa (so'm)");

        if (ScopePanel.IsVisible)
            DiscountScope = ScopeCheckBox.IsChecked == true ? "item" : "check";
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        ErrorText.Text = "";
        SyncModeUi();

        var raw = (ValueBox.Text ?? "").Trim();
        string? error = null;

        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var val)
            || decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out val))
        {
            if (DiscountMode == "percent")
            {
                if (val < 0 || val > 100)
                    error = Tr.T("Процент должен быть от 0 до 100", "Пайыз 0дөн 100гө чейин болушу керек", "Percentage must be between 0 and 100", "Yüzde 0 ile 100 arasında olmalıdır", "Foiz 0 dan 100 gacha bo'lishi kerak");
            }
            else if (val < 0)
            {
                error = Tr.T("Сумма не может быть отрицательной", "Сумма терс болбошу керек", "Amount cannot be negative", "Tutar negatif olamaz", "Summa manfiy bo'lishi mumkin emas");
            }
        }
        else
        {
            error = Tr.T("Введите корректное число", "Туура сан киргизиңиз", "Enter a valid number", "Geçerli bir sayı girin", "To'g'ri son kiriting");
        }

        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }

        DiscountValue = val.ToString(CultureInfo.InvariantCulture);
        ClearRequested = false;
        Close(true);
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        ClearRequested = true;
        DiscountValue = "";
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
