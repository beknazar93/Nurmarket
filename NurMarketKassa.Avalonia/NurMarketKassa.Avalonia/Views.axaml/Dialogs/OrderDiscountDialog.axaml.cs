using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class OrderDiscountDialog : Window
{
    public string DiscountMode { get; private set; } = "percent";
    public string DiscountScope { get; private set; } = "check";
    public string DiscountValue { get; private set; } = "";
    public bool ClearRequested { get; private set; }

    // 2026-10-04: переключение тумблера кассиром запоминается (UserPreferences.DiscountModePercent),
    // программные установки при открытии окна — нет.
    private bool _initializing = true;

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
        else if (!string.IsNullOrEmpty(pct))
        {
            DiscountTypeToggle.IsChecked = true; // percent
            ValueBox.Text = pct;
        }
        else
        {
            // Скидки ещё нет — режим, который кассир выбрал в прошлый раз.
            DiscountTypeToggle.IsChecked = UserPreferences.Instance.DiscountModePercent;
            ValueBox.Text = "";
        }

        ScopeCheckBox.IsChecked = false; // весь чек
        DiscountScope = "check";
        SyncModeUi();
        _initializing = false;
        ValueBox.TextChanged += (_, _) => UpdatePreview();
        ScopeCheckBox.IsCheckedChanged += (_, _) => UpdatePreview();
    }

    /// <summary>2026-10-21 (пакет 6, п. 18): сумма, от которой считается скидка (весь чек после скидок на строки или
    /// строка «цена × количество»), — для предпросмотра «Было → станет». 0 — предпросмотра нет.</summary>
    private decimal _previewBase;

    public void SetPreviewBase(double amount)
    {
        _previewBase = Math.Round((decimal)Math.Max(0, amount), 2);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var raw = (ValueBox.Text ?? "").Trim().Replace(',', '.');
        // Галочка «к выбранному товару» в окне скидки на чек — база другая (строка), предпросмотр не показываем.
        if (_previewBase <= 0m || (ScopePanel.IsVisible && ScopeCheckBox.IsChecked == true) || !decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) || value <= 0m)
        {
            PreviewText.IsVisible = false;
            return;
        }

        var discount = DiscountTypeToggle.IsChecked == true
            ? Math.Round(_previewBase * Math.Min(value, 100m) / 100m, 2, MidpointRounding.AwayFromZero)
            : value;
        PreviewText.IsVisible = true;
        if (discount > _previewBase)
        {
            PreviewText.Text = Tr.T($"Скидка больше суммы ({MoneyText.Som(_previewBase)}).", $"Арзандатуу суммадан көп ({MoneyText.Som(_previewBase)}).",
                $"The discount is more than the amount ({MoneyText.Som(_previewBase)}).", $"İndirim tutardan fazla ({MoneyText.Som(_previewBase)}).",
                $"Chegirma summadan ko'p ({MoneyText.Som(_previewBase)}).");
            return;
        }
        var after = MoneyText.Som(_previewBase - discount);
        var before = MoneyText.Amount(_previewBase);
        var minus = MoneyText.Amount(discount);
        PreviewText.Text = Tr.T($"Было {before} → станет {after} (скидка {minus})", $"Болчу {before} → болот {after} (арзандатуу {minus})",
            $"Was {before} → becomes {after} (discount {minus})", $"Önce {before} → sonra {after} (indirim {minus})",
            $"Edi {before} → bo'ladi {after} (chegirma {minus})");
    }

    public void SetItemMode(string itemTitle, string? currentDiscountType, decimal? currentDiscountValue)
    {
        _initializing = true;
        Title = Tr.T("Скидка на товар", "Товарга арзандатуу", "Product discount", "Ürün indirimi", "Mahsulotga chegirma");
        Shell.Title = Tr.T("Скидка на товар", "Товарга арзандатуу", "Product discount", "Ürün indirimi", "Mahsulotga chegirma");
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
            DiscountTypeToggle.IsChecked = UserPreferences.Instance.DiscountModePercent;
            ValueBox.Text = "";
        }

        SyncModeUi();
        _initializing = false;
    }

    private void DiscountType_Changed(object? sender, RoutedEventArgs e)
    {
        SyncModeUi();
        if (_initializing)
            return;
        var prefs = UserPreferences.Instance;
        var percent = DiscountTypeToggle.IsChecked == true;
        if (prefs.DiscountModePercent == percent)
            return;
        prefs.DiscountModePercent = percent;
        prefs.SaveToDisk();
    }

    /// <summary>2026-10-17, этап 3: кассир выбрал «%» или «сом» — переключаем скрытый тумблер, дальше всё
    /// как раньше (DiscountType_Changed: подписи, запоминание режима).</summary>
    private void ModeSegment_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } segment)
            return;
        var percent = ReferenceEquals(segment, PercentSegment);
        if ((DiscountTypeToggle.IsChecked == true) != percent)
            DiscountTypeToggle.IsChecked = percent;
    }

    private void SyncModeUi()
    {
        var isPercent = DiscountTypeToggle.IsChecked == true;
        DiscountMode = isPercent ? "percent" : "sum";
        // 2026-10-17, этап 3: сегменты «%» / «сом» показывают режим тумблера (он скрыт, состояние — в нём).
        PercentSegment.IsChecked = isPercent;
        SumSegment.IsChecked = !isPercent;
        ValueLabel.Text = isPercent ? Tr.T("Введите процент скидки", "Арзандатуу пайызын киргизиңиз", "Enter discount percentage", "İndirim yüzdesini girin", "Chegirma foizini kiriting") : Tr.T("Введите сумму скидки", "Арзандатуу суммасын киргизиңиз", "Enter discount amount", "İndirim tutarını girin", "Chegirma summasini kiriting");
        DiscountTypeLabel.Text = isPercent ? Tr.T("Режим скидки: Процент (%)", "Арзандатуу режими: Пайыз (%)", "Discount mode: percent (%)", "İndirim modu: Yüzde (%)", "Chegirma rejimi: Foiz (%)") : Tr.T("Режим скидки: Сумма (сом)", "Арзандатуу режими: Сумма (сом)", "Discount mode: amount (som)", "İndirim modu: Tutar (som)", "Chegirma rejimi: Summa (so'm)");

        if (ScopePanel.IsVisible)
            DiscountScope = ScopeCheckBox.IsChecked == true ? "item" : "check";
        UpdatePreview();
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
