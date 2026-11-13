using System.Globalization;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PrinterSetup;

#nullable enable

namespace NurMarketKassa.AvaloniaHost.Views
{
    // 2026-11-13 (владелец: «готовые драйверы для чековых принтеров и принтеров штрихкодов, в основном Xprinter»): мастер «Подключить принтер» сам
    // сохраняет настройки печати; здесь — перечитать поля страницы «Печать» после него (иначе «Сохранить» страницы записал бы старые значения поверх)
    // и подсказка «принтер не настроен» над кнопками мастера.
    public partial class PosSettingsWindow
    {
        private void WirePrinterWizard()
        {
            _printView.PrinterWizardApplied += OnPrinterWizardApplied;
            ReceiptLptBox.TextChanged += (_, _) => _printView.ReceiptSetupBar.RefreshReceiptHint(ReceiptLptBox.Text);
            LabelPrinterCombo.SelectionChanged += (_, _) => _printView.LabelSetupBar.RefreshLabelHint(
                LabelPrinterCombo.SelectedItem is DiscoveredPrinter p ? p.DevicePath : UserPreferences.Instance.LabelPrinterDevicePath);
            _printView.ReceiptSetupBar.RefreshReceiptHint(UserPreferences.Instance.ReceiptDevicePath);
            _printView.LabelSetupBar.RefreshLabelHint(UserPreferences.Instance.LabelPrinterDevicePath);
        }

        private void OnPrinterWizardApplied(object? sender, PrinterWizardResult result)
        {
            var prefs = UserPreferences.Instance;
            if (result.Role == PrinterRole.Label)
            {
                _labelPrinterChoice = prefs.LabelPrinterDevicePath;
                RefreshLabelPrinters();
                _printView.LabelSetupBar.RefreshLabelHint(prefs.LabelPrinterDevicePath);
                return;
            }
            ReceiptEnabledCheck.IsChecked = prefs.ReceiptEnabled;
            ReceiptLptBox.Text = prefs.ReceiptDevicePath;
            CashDrawerEnabledCheck.IsChecked = prefs.CashDrawerEnabled;
            SelectComboByTag(CashDrawerPinCombo, prefs.CashDrawerPin.ToString(CultureInfo.InvariantCulture));
            _charWidth80 = prefs.ReceiptCharWidth80;
            _dots80 = prefs.ReceiptDots80;
            _shownPaperMm = 0;
            _shownNarrow80 = ReceiptPaperProfile.NormalizePaperWidthMm(prefs.ReceiptPaperWidthMm) >= ReceiptPaperProfile.Paper80mm
                             && _charWidth80 == Narrow80Chars && _dots80 is 0 or Narrow80Dots;
            SelectComboByTag(ReceiptPaperWidthCombo, _shownNarrow80 ? Narrow80Tag : prefs.ReceiptPaperWidthMm.ToString(CultureInfo.InvariantCulture));
            ApplyPaperWidthToUi(prefs.ReceiptPaperWidthMm);
            SelectComboByTag(ReceiptEncCombo, prefs.ReceiptEncoding.ToLowerInvariant());
            if (ReceiptTableCombo.ItemCount > 0)
                ReceiptTableCombo.SelectedIndex = 0; // «Авто» — таблица по кодировке, как на пробной ленте мастера
            _printView.ReceiptSetupBar.RefreshReceiptHint(prefs.ReceiptDevicePath);
            RefreshDiscoveredPrinters();
        }
    }
}
