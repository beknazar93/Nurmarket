using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Settings;

#nullable enable

namespace NurMarketKassa.AvaloniaHost.Views
{
    public partial class PosSettingsWindow : Window
    {
        public SettingsViewModel SettingsVm { get; }

        private readonly ScaleSettingsView _scaleView = new();
        private readonly PrintSettingsView _printView = new();
        private readonly ScreenSettingsView _screenView = new();
        private readonly MonitorSettingsView _monitorView;
        private readonly UpdatesSettingsView _updatesView = new();
        private readonly OperationsSettingsView _operationsView = new();
        private readonly SettingsView _customizationView = new();

        private Button[] _navButtons = Array.Empty<Button>();

        // --- Scale ---
        private CheckBox ScaleEnabledCheck => _scaleView.ScaleEnabledCheck;
        private ComboBox ScaleComCombo => _scaleView.ScaleComCombo;
        private TextBlock StatusScalePortText => _scaleView.StatusScalePortText;
        private TextBox ScaleBaudBox => _scaleView.ScaleBaudBox;
        private TextBox ScaleHexBox => _scaleView.ScaleHexBox;
        private TextBox ScalePollBox => _scaleView.ScalePollBox;
        private Border ScaleAlert => _scaleView.ScaleAlert;
        private TextBlock ScaleAlertText => _scaleView.ScaleAlertText;

        // --- Print ---
        private CheckBox ReceiptEnabledCheck => _printView.ReceiptEnabledCheck;
        private TextBox ReceiptLptBox => _printView.ReceiptLptBox;
        private TextBlock StatusPortText => _printView.StatusPortText;
        private Button BtnPhysicalPrint => _printView.BtnPhysicalPrint;
        private TextBox ReceiptRetryBox => _printView.ReceiptRetryBox;
        private ComboBox ReceiptPaperWidthCombo => _printView.ReceiptPaperWidthCombo;
        private TextBox GraphicWidthBox => _printView.GraphicWidthBox;
        private RadioButton TextModeRadio => _printView.TextModeRadio;
        private RadioButton GraphicModeRadio => _printView.GraphicModeRadio;
        private ComboBox ReceiptEncCombo => _printView.ReceiptEncCombo;
        private ComboBox ReceiptTableCombo => _printView.ReceiptTableCombo;
        private TextBox ReceiptEscRBox => _printView.ReceiptEscRBox;
        private CheckBox GraphicReceiptEnabledCheck => _printView.GraphicReceiptEnabledCheck;
        private ComboBox GraphicFontCombo => _printView.GraphicFontCombo;
        private ComboBox GraphicFontSizeCombo => _printView.GraphicFontSizeCombo;
        private CheckBox ShowStoreNameCheck => _printView.ShowStoreNameCheck;
        private CheckBox ShowAddressCheck => _printView.ShowAddressCheck;
        private CheckBox ShowReceiptNumberCheck => _printView.ShowReceiptNumberCheck;
        private CheckBox ShowDateCheck => _printView.ShowDateCheck;
        private CheckBox ShowItemsCheck => _printView.ShowItemsCheck;
        private CheckBox ShowTotalCheck => _printView.ShowTotalCheck;
        private CheckBox ShowQrCodeCheck => _printView.ShowQrCodeCheck;
        private TextBlock GraphicQrStatusText => _printView.GraphicQrStatusText;
        private TextBox StatusText => _printView.StatusText;
        private Border PrintErrorPanel => _printView.PrintErrorPanel;
        private TextBox TxtPrintErrorDetails => _printView.TxtPrintErrorDetails;

        // --- Screen ---
        private CheckBox FullscreenCheck => _screenView.FullscreenCheck;
        private CheckBox AutostartCheck => _screenView.AutostartCheck;
        private CheckBox AutoTouchKeyboardCheck => _screenView.AutoTouchKeyboardCheck;
        private TextBox StoreNameBox => _screenView.StoreNameBox;
        private TextBox StoreAddressBox => _screenView.StoreAddressBox;
        private CheckBox ShowInnCheck => _screenView.ShowInnCheck;
        private RadioButton DoubleClickToCartRadio => _screenView.DoubleClickToCartRadio;
        private RadioButton SingleClickToCartRadio => _screenView.SingleClickToCartRadio;
        private CheckBox ResetManualAddQtyCheck => _screenView.ResetManualAddQtyCheck;

        // --- Updates ---
        private TextBlock AppVersionText => _updatesView.AppVersionText;
        private ProgressBar UpdateProgressBar => _updatesView.UpdateProgressBar;
        private TextBlock UpdateStatusText => _updatesView.UpdateStatusText;
        private TextBlock CatalogDiagnosticsText => _updatesView.CatalogDiagnosticsText;

        public PosSettingsWindow() : this(ResolveSettingsViewModel())
        {
        }

        private static SettingsViewModel ResolveSettingsViewModel() =>
            NurMarketKassa.AvaloniaHost.App.GetRequiredService<SettingsViewModel>();

        public PosSettingsWindow(SettingsViewModel settingsVm)
        {
            SettingsVm = settingsVm;
            DataContext = this;
            InitializeComponent();

            _customizationView.DataContext = SettingsVm;
            _monitorView = new MonitorSettingsView(new MonitorSettingsViewModel(
                this, App.GetRequiredService<AvaloniaCustomerDisplayService>()));
            WireChildEvents();
            _operationsView.LoadBankQrSettings();

            _navButtons = new[] { NavScales, NavPrint, NavScreen, NavMonitor, NavUpdates, NavOperations, NavCustomization };
            NavigateTo(0);

            if (UserPreferences.Instance.Fullscreen)
            {
                SystemDecorations = SystemDecorations.None;
                WindowState = WindowState.Maximized;
            }

            var prefs = UserPreferences.Instance;

            ScaleEnabledCheck.IsChecked = prefs.ScaleEnabled;
            ScaleBaudBox.Text = prefs.ScaleBaudRate.ToString();
            ScaleHexBox.Text = prefs.ScaleRequestHex ?? "";
            ScalePollBox.Text = prefs.ScalePollMs.ToString();

            ReceiptEnabledCheck.IsChecked = prefs.ReceiptEnabled;
            ReceiptLptBox.Text = prefs.ReceiptDevicePath;
            ReceiptEscRBox.Text = prefs.ReceiptEscR?.ToString() ?? "";
            ReceiptRetryBox.Text = prefs.ReceiptRetryCount.ToString();

            SelectComboByTag(ReceiptPaperWidthCombo, prefs.ReceiptPaperWidthMm.ToString(CultureInfo.InvariantCulture));
            ApplyPaperWidthToUi(prefs.ReceiptPaperWidthMm);

            FullscreenCheck.IsChecked = prefs.Fullscreen;
            AutostartCheck.IsChecked = prefs.Autostart || AutostartHelper.IsEnabled();
            AutoTouchKeyboardCheck.IsChecked = prefs.AutoShowTouchKeyboard;
            StoreNameBox.Text = prefs.StoreName;
            StoreAddressBox.Text = prefs.StoreAddress;
            ShowInnCheck.IsChecked = prefs.ShowInn;
            ShowStoreNameCheck.IsChecked = prefs.ShowStoreName;
            ShowAddressCheck.IsChecked = prefs.ShowAddress;
            ShowReceiptNumberCheck.IsChecked = prefs.ShowReceiptNumber;
            ShowDateCheck.IsChecked = prefs.ShowDate;
            ShowItemsCheck.IsChecked = prefs.ShowItems;
            ShowTotalCheck.IsChecked = prefs.ShowTotal;
            ShowQrCodeCheck.IsChecked = prefs.ShowQrCode;

            DoubleClickToCartRadio.IsChecked = !prefs.SingleClickToCart;
            SingleClickToCartRadio.IsChecked = prefs.SingleClickToCart;
            ResetManualAddQtyCheck.IsChecked = prefs.ResetManualAddQtyAfterAdd;

            var ports = ScaleReaderService.GetAvailablePorts().ToList();
            if (!ports.Contains(prefs.ScaleComPort, StringComparer.OrdinalIgnoreCase))
                ports.Insert(0, prefs.ScaleComPort);
            ScaleComCombo.ItemsSource = ports;
            SelectScaleComPort(prefs.ScaleComPort);
            RefreshScalePortStatus();

            SelectComboByTag(ReceiptEncCombo, prefs.ReceiptEncoding.ToLowerInvariant());
            string tableTag = prefs.ReceiptEscPosTable?.ToString() ?? "";
            foreach (var item in ReceiptTableCombo.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag?.ToString() == tableTag)
                {
                    ReceiptTableCombo.SelectedItem = item;
                    break;
                }
            }
            if (ReceiptTableCombo.SelectedItem == null && ReceiptTableCombo.Items.Count > 0)
                ReceiptTableCombo.SelectedIndex = 0;

            GraphicReceiptEnabledCheck.IsChecked = prefs.GraphicReceiptEnabled;
            TextModeRadio.IsChecked = prefs.SelectedPrintMode == PrintMode.Text;
            GraphicModeRadio.IsChecked = prefs.SelectedPrintMode == PrintMode.Graphic;

            SelectComboByTag(GraphicFontCombo, TestReceiptLineBuilder.FontFamily);

            string savedFont = prefs.GraphicFontFamily;
            foreach (var item in GraphicFontCombo.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag?.ToString() == savedFont)
                {
                    GraphicFontCombo.SelectedItem = item;
                    break;
                }
            }
            if (GraphicFontCombo.SelectedItem == null)
                GraphicFontCombo.SelectedIndex = 0;

            var fontSize = prefs.GraphicFontSize > 0 ? prefs.GraphicFontSize : TestReceiptLineBuilder.DefaultFontSizePt;
            if (prefs.GraphicFontSize <= 0)
                prefs.GraphicFontSize = TestReceiptLineBuilder.DefaultFontSizePt;

            SelectGraphicFontSizeCombo(fontSize);

            if (!string.IsNullOrEmpty(prefs.QrCodePath))
                GraphicQrStatusText.Text = $"✅ QR-код сохранён: {Path.GetFileName(prefs.QrCodePath)}";
            else
                GraphicQrStatusText.Text = "QR-код не загружен";

            AppVersionText.Text = "Текущая версия: " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "неизвестно");
            RefreshCatalogDiagnostics();
            RefreshPrinterPortStatus();
        }

        private void WireChildEvents()
        {
            ScaleComCombo.SelectionChanged += ScaleComCombo_SelectionChanged;
            _scaleView.CheckScaleButton.Click += CheckScale_Click;
            _scaleView.SaveRequested += ScaleSaveRequested;

            ReceiptLptBox.TextChanged += ReceiptLptBox_TextChanged;
            ReceiptPaperWidthCombo.SelectionChanged += ReceiptPaperWidthCombo_SelectionChanged;
            BtnPhysicalPrint.Click += BtnPhysicalPrint_Click;
            _printView.TestTextPrintButton.Click += TestTextPrint_Click;
            _printView.TestGraphicPrintButton.Click += TestGraphicPrint_Click;
            _printView.LoadGraphicQrButton.Click += LoadGraphicQrCode_Click;
            _printView.DeleteGraphicQrButton.Click += DeleteGraphicQrCode_Click;
            _printView.SaveRequested += PrintSaveRequested;

            DoubleClickToCartRadio.IsCheckedChanged += ClickToCartMode_Changed;
            SingleClickToCartRadio.IsCheckedChanged += ClickToCartMode_Changed;
            ResetManualAddQtyCheck.IsCheckedChanged += ClickToCartMode_Changed;
            _screenView.SaveRequested += ScreenSaveRequested;

            _updatesView.CheckUpdateButton.Click += CheckUpdate_Click;
        }

        private void SidebarNav_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn)
                return;

            int index = btn.Tag switch
            {
                int i => i,
                string s when int.TryParse(s, out var n) => n,
                _ => -1
            };

            if (index >= 0)
                NavigateTo(index);
        }

        private void SidebarClose_Click(object? sender, RoutedEventArgs e) => Close(false);

        private void NavigateTo(int index)
        {
            ContentHost.Content = index switch
            {
                0 => _scaleView,
                1 => _printView,
                2 => _screenView,
                3 => _monitorView,
                4 => _updatesView,
                5 => _operationsView,
                6 => _customizationView,
                _ => _scaleView
            };

            for (int i = 0; i < _navButtons.Length; i++)
            {
                if (i == index)
                    _navButtons[i].Classes.Add("nav-active");
                else
                    _navButtons[i].Classes.Remove("nav-active");
            }
        }

        private void ReceiptLptBox_TextChanged(object? sender, TextChangedEventArgs e) =>
            RefreshPrinterPortStatus();

        private void ReceiptPaperWidthCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ReceiptPaperWidthCombo?.SelectedItem is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm))
            {
                ApplyPaperWidthToUi(mm);
            }
        }

        private void ApplyPaperWidthToUi(int paperWidthMm)
        {
            var normalized = ReceiptPaperProfile.NormalizePaperWidthMm(paperWidthMm);
            if (GraphicWidthBox != null)
                GraphicWidthBox.Text = ReceiptPaperProfile.GetRasterWidthPixels(normalized).ToString(CultureInfo.InvariantCulture);
        }

        private static int ReadPaperWidthMmFromUi(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm))
            {
                return ReceiptPaperProfile.NormalizePaperWidthMm(mm);
            }

            return ReceiptPaperProfile.Paper58mm;
        }

        private void RefreshPrinterPortStatus()
        {
            if (StatusPortText == null)
                return;

            var probe = PrinterPortService.ProbePort(ReceiptLptBox.Text);
            StatusPortText.Text = probe.Message;
            StatusPortText.Foreground = probe.IsAvailable
                ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        }

        private void ClearPrintError()
        {
            if (TxtPrintErrorDetails != null)
                TxtPrintErrorDetails.Text = "";
            if (PrintErrorPanel != null)
                PrintErrorPanel.IsVisible = false;
        }

        private void ShowPrintError(Exception ex, string devicePath)
        {
            if (TxtPrintErrorDetails == null || PrintErrorPanel == null)
                return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Порт: {devicePath}");

            var current = ex;
            int level = 0;
            while (current != null)
            {
                var prefix = level == 0 ? "Ошибка: " : new string(' ', level * 2) + "↳ ";
                sb.AppendLine($"{prefix}{current.GetType().Name}: {current.Message}");

                if (current is System.ComponentModel.Win32Exception w32)
                    sb.AppendLine($"  Win32-код: {w32.NativeErrorCode}");

                current = current.InnerException;
                level++;
            }

            TxtPrintErrorDetails.Text = sb.ToString().TrimEnd();
            PrintErrorPanel.IsVisible = true;
        }

        private void BtnPhysicalPrint_Click(object? sender, RoutedEventArgs e)
        {
            var devicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                StatusText.Text = "❌ Укажите порт принтера (LPT1, COM3 или имя очереди Windows).";
                return;
            }

            var probe = PrinterPortService.ProbePort(devicePath);
            if (!probe.IsAvailable)
            {
                StatusText.Text = $"❌ Порт недоступен: {probe.Message}";
                RefreshPrinterPortStatus();
                return;
            }

            ClearPrintError();

            try
            {
                var cfg = BuildReceiptSettingsFromUi();
                var contentSettings = BuildGraphicSettingsFromUi(devicePath);
                var storeName = StoreNameBox.Text ?? string.Empty;
                int retry = cfg.RetryCount;

                if (GraphicModeRadio.IsChecked == true)
                {
                    if (GraphicReceiptEnabledCheck.IsChecked != true)
                    {
                        StatusText.Text = "❌ Графический чек выключен. Включите «Включить графический чек».";
                        return;
                    }

                    var settings = BuildGraphicSettingsFromUi(devicePath);
                    var bytes = GraphicReceiptGenerator.GenerateTestReceiptImage(settings, storeName);
                    ReceiptPrintService.SendRawBytes(devicePath, bytes, retry);
                    StatusText.Text = $"✅ Графический чек ({bytes.Length} байт) отправлен на {devicePath}";
                }
                else
                {
                    var testText = ReceiptPdfPreviewService.BuildTextTestReceipt(contentSettings, storeName);
                    var charWidth = ReceiptPaperProfile.GetCharWidth(ReadPaperWidthMmFromUi(ReceiptPaperWidthCombo));
                    var payload = EscPosTextReceiptPrinter.BuildEscPosPayload(cfg, testText, charWidth);
                    ReceiptPrintService.SendRawBytes(devicePath, payload, retry);
                    StatusText.Text = $"✅ Текстовый ESC/POS чек ({payload.Length} байт) отправлен на {devicePath}";
                }

                RefreshPrinterPortStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Ошибка печати в порт: {ex.Message}";
                ShowPrintError(ex, devicePath);
                PosLogger.Log($"Физическая печать: {ex}", "PRINTER");
                RefreshPrinterPortStatus();
            }
        }

        private void ClickToCartMode_Changed(object? sender, RoutedEventArgs e)
        {
            var prefs = UserPreferences.Instance;
            prefs.SingleClickToCart = SingleClickToCartRadio.IsChecked == true;
            prefs.ResetManualAddQtyAfterAdd = ResetManualAddQtyCheck.IsChecked == true;
            prefs.SaveToDisk();
        }

        private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn) btn.IsEnabled = false;
            UpdateProgressBar.IsVisible = true;
            UpdateStatusText.IsVisible = true;
            UpdateStatusText.Text = "Проверка обновлений недоступна в Avalonia-сборке.";
            UpdateProgressBar.IsVisible = false;
            if (sender is Button b) b.IsEnabled = true;
            await Task.CompletedTask;
        }

        private static void SelectComboByTag(ComboBox box, string value)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private void SelectGraphicFontSizeCombo(float fontSize)
        {
            foreach (var item in GraphicFontSizeCombo.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag != null
                    && float.TryParse(item.Tag.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out float val)
                    && Math.Abs(val - fontSize) < 0.01f)
                {
                    GraphicFontSizeCombo.SelectedItem = item;
                    return;
                }
            }

            GraphicFontSizeCombo.SelectedIndex = 2;
        }

        private float ReadGraphicFontSizeFromUi()
        {
            if (GraphicFontSizeCombo.SelectedItem is ComboBoxItem sizeItem
                && sizeItem.Tag != null
                && float.TryParse(sizeItem.Tag.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out float size)
                && size > 0)
            {
                return size;
            }

            return TestReceiptLineBuilder.DefaultFontSizePt;
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

        private void ScaleSaveRequested(object? sender, EventArgs e) => SaveScaleSettings();

        private void PrintSaveRequested(object? sender, EventArgs e) => SavePrintSettings();

        private void ScreenSaveRequested(object? sender, EventArgs e) => SaveScreenSettings();

        private bool SaveScaleSettings()
        {
            var prefs = UserPreferences.Instance;

            prefs.ScaleEnabled = ScaleEnabledCheck.IsChecked == true;
            prefs.ScaleComPort = GetSelectedScaleComPort();
            int.TryParse(ScaleBaudBox.Text?.Trim(), out int baud);
            prefs.ScaleBaudRate = baud > 0 ? baud : 9600;
            prefs.ScaleRequestHex = string.IsNullOrWhiteSpace(ScaleHexBox.Text) ? null : ScaleHexBox.Text.Trim();
            int.TryParse(ScalePollBox.Text?.Trim(), out int poll);
            prefs.ScalePollMs = poll >= 0 ? poll : 0;

            try
            {
                if (prefs.ScaleEnabled)
                    ScaleReaderService.ValidateSettings(prefs.ToScaleSettings());
            }
            catch (Exception ex)
            {
                _scaleView.SaveStatusText.Text = "Настройки не сохранены.";
                PosMessageBox.Show(ex.Message, "Настройки весов", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            prefs.SaveToDisk();

            // Recreate the singleton reader so the new COM settings are used immediately.
            App.GetRequiredService<NurMarketKassa.Services.Hardware.IWeightScaleService>().Start();
            _scaleView.SaveStatusText.Text = "Настройки весов сохранены.";
            RefreshScalePortStatus();
            return true;
        }

        private bool SavePrintSettings()
        {
            var prefs = UserPreferences.Instance;

            prefs.ReceiptEnabled = ReceiptEnabledCheck.IsChecked == true;
            prefs.ReceiptDevicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
            prefs.ReceiptPaperWidthMm = ReadPaperWidthMmFromUi(ReceiptPaperWidthCombo);
            prefs.GraphicPaperWidthPixels = ReceiptPaperProfile.GetRasterWidthPixels(prefs.ReceiptPaperWidthMm);
            prefs.ReceiptEncoding = (ReceiptEncCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wpc1251";

            prefs.ReceiptEscPosTable = null;
            if (ReceiptTableCombo.SelectedItem is ComboBoxItem tableItem && int.TryParse(tableItem.Tag?.ToString(), out int tableByte))
                prefs.ReceiptEscPosTable = tableByte;
            if (int.TryParse(ReceiptEscRBox.Text?.Trim(), out int escR))
                prefs.ReceiptEscR = escR;
            else
                prefs.ReceiptEscR = null;
            int.TryParse(ReceiptRetryBox.Text?.Trim(), out int retry);
            prefs.ReceiptRetryCount = retry >= 1 ? retry : 3;

            try
            {
                if (prefs.ReceiptEnabled)
                    EscPosTextReceiptPrinter.ValidateSettings(prefs.ToReceiptPrinterSettings());
            }
            catch (Exception ex)
            {
                _printView.SaveStatusText.Text = "Настройки не сохранены.";
                PosMessageBox.Show(ex.Message, "Настройки печати", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            prefs.ShowStoreName = ShowStoreNameCheck.IsChecked == true;
            prefs.ShowAddress = ShowAddressCheck.IsChecked == true;
            prefs.ShowReceiptNumber = ShowReceiptNumberCheck.IsChecked == true;
            prefs.ShowDate = ShowDateCheck.IsChecked == true;
            prefs.ShowItems = ShowItemsCheck.IsChecked == true;
            prefs.ShowTotal = ShowTotalCheck.IsChecked == true;
            prefs.ShowQrCode = ShowQrCodeCheck.IsChecked == true;

            prefs.GraphicReceiptEnabled = GraphicReceiptEnabledCheck.IsChecked == true;
            prefs.GraphicFontSize = ReadGraphicFontSizeFromUi();

            if (TextModeRadio.IsChecked == true)
                prefs.SelectedPrintMode = PrintMode.Text;
            else if (GraphicModeRadio.IsChecked == true)
                prefs.SelectedPrintMode = PrintMode.Graphic;

            prefs.GraphicPaperWidthPixels = ReceiptPaperProfile.GetRasterWidthPixels(prefs.ReceiptPaperWidthMm);

            var fontItem = GraphicFontCombo.SelectedItem as ComboBoxItem;
            prefs.GraphicFontFamily = fontItem?.Tag?.ToString() ?? "Consolas";

            prefs.SaveToDisk();
            _printView.SaveStatusText.Text = "Настройки печати сохранены.";
            RefreshPrinterPortStatus();
            return true;
        }

        private bool SaveScreenSettings()
        {
            var prefs = UserPreferences.Instance;

            prefs.Fullscreen = FullscreenCheck.IsChecked == true;
            prefs.Autostart = AutostartCheck.IsChecked == true;
            prefs.AutoShowTouchKeyboard = AutoTouchKeyboardCheck.IsChecked == true;
            prefs.SingleClickToCart = SingleClickToCartRadio.IsChecked == true;
            prefs.ResetManualAddQtyAfterAdd = ResetManualAddQtyCheck.IsChecked == true;

            prefs.StoreName = StoreNameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(prefs.StoreName))
                prefs.StoreName = "MARKET PLUS";
            prefs.StoreAddress = StoreAddressBox.Text?.Trim() ?? string.Empty;
            prefs.ShowInn = ShowInnCheck.IsChecked == true;

            prefs.SaveToDisk();
            AutostartHelper.SyncFromPreference(prefs.Autostart);
            _screenView.SaveStatusText.Text = "Настройки экрана сохранены.";
            return true;
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (!SaveScaleSettings() || !SavePrintSettings() || !SaveScreenSettings())
                return;

            _monitorView.SaveSettings();

            Close(true);
        }

        private async void LoadGraphicQrCode_Click(object? sender, RoutedEventArgs e)
        {
            var path = await PickImagePathAsync("Выберите QR-код (сохранится для будущего)");
            if (!string.IsNullOrEmpty(path))
            {
                var prefs = UserPreferences.Instance;
                prefs.QrCodePath = path;
                prefs.SaveToDisk();
                GraphicQrStatusText.Text = $"✅ QR-код сохранён: {Path.GetFileName(path)}";
            }
        }

        private void DeleteGraphicQrCode_Click(object? sender, RoutedEventArgs e)
        {
            var prefs = UserPreferences.Instance;
            prefs.QrCodePath = "";
            prefs.SaveToDisk();
            GraphicQrStatusText.Text = "QR-код не загружен";
        }

        private async void TestGraphicPrint_Click(object? sender, RoutedEventArgs e)
        {
            if (!GraphicReceiptEnabledCheck.IsChecked == true)
            {
                StatusText.Text = "❌ Графический чек выключен. Включите его в настройках (чекбокс «Включить графический чек»).";
                return;
            }

            if (GraphicModeRadio.IsChecked != true)
            {
                StatusText.Text = "❌ Сейчас выбран текстовый режим. Переключите на графический в настройках.";
                return;
            }

            try
            {
                var devicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
                var settings = BuildGraphicSettingsFromUi(devicePath);
                var storeName = StoreNameBox.Text ?? string.Empty;
                var tempPdfPath = Path.Combine(Path.GetTempPath(), $"test_graphic_{Guid.NewGuid():N}.pdf");

                ReceiptPdfPreviewService.GenerateGraphicReceiptPdf(tempPdfPath, settings, storeName);

                var dialog = new ReceiptPreviewDialog("Предпросмотр: Графический чек", tempPdfPath);
                await dialog.ShowDialog<bool>(this);
                StatusText.Text = "Предпросмотр графического чека готов. Для физической печати нажмите «Печать в сам порт».";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Ошибка: {ex.Message}";
            }
        }

        private async void TestTextPrint_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = BuildReceiptSettingsFromUi();
                var contentSettings = BuildGraphicSettingsFromUi(cfg.DevicePath);
                var storeName = StoreNameBox.Text ?? string.Empty;
                var encoding = (ReceiptEncCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wpc1251";
                int? escTable = null;
                if (ReceiptTableCombo.SelectedItem is ComboBoxItem tableItem
                    && int.TryParse(tableItem.Tag?.ToString(), out int tableByte))
                {
                    escTable = tableByte;
                }

                var testText = ReceiptPdfPreviewService.BuildTextTestReceipt(contentSettings, storeName);
                var tempPdfPath = Path.Combine(Path.GetTempPath(), $"test_pos_{Guid.NewGuid():N}.pdf");

                ReceiptPdfPreviewService.GenerateTextReceiptPdf(tempPdfPath, testText, encoding, escTable);

                var dialog = new ReceiptPreviewDialog("Предпросмотр: Текстовый чек (ESC/POS)", tempPdfPath);
                await dialog.ShowDialog<bool>(this);
                StatusText.Text = "Предпросмотр текстового чека готов. Для физической печати нажмите «Печать в сам порт».";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Ошибка текстовой печати: {ex.Message}";
            }
        }

        private ReceiptPrinterSettings BuildReceiptSettingsFromUi()
        {
            int? tableByte = null;
            if (ReceiptTableCombo.SelectedItem is ComboBoxItem tableItem
                && int.TryParse(tableItem.Tag?.ToString(), out int parsedTable))
            {
                tableByte = parsedTable;
            }

            int? escR = int.TryParse(ReceiptEscRBox.Text?.Trim(), out int parsedEscR) ? parsedEscR : null;
            int.TryParse(ReceiptRetryBox.Text?.Trim(), out int retry);

            return new ReceiptPrinterSettings
            {
                Enabled = ReceiptEnabledCheck.IsChecked == true,
                DevicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text),
                TextEncoding = (ReceiptEncCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "wpc1251",
                EscPosTableByte = tableByte,
                EscRByte = escR,
                RetryCount = retry >= 1 ? retry : 3,
            };
        }

        private GraphicReceiptSettings BuildGraphicSettingsFromUi(string devicePath)
        {
            var prefs = UserPreferences.Instance;
            var paperMm = ReadPaperWidthMmFromUi(ReceiptPaperWidthCombo);
            var paperWidth = ReceiptPaperProfile.GetRasterWidthPixels(paperMm);

            return new GraphicReceiptSettings
            {
                PaperWidthPixels = paperWidth,
                FontFamily = (GraphicFontCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                             ?? TestReceiptLineBuilder.FontFamily,
                FontSize = TestReceiptLineBuilder.ResolveFontSize(ReadGraphicFontSizeFromUi()),
                DevicePath = devicePath,
                ShowStoreName = ShowStoreNameCheck.IsChecked == true,
                ShowAddress = ShowAddressCheck.IsChecked == true,
                ShowInn = ShowInnCheck.IsChecked == true,
                ShowReceiptNumber = ShowReceiptNumberCheck.IsChecked == true,
                ShowDate = ShowDateCheck.IsChecked == true,
                ShowItems = ShowItemsCheck.IsChecked == true,
                ShowTotal = ShowTotalCheck.IsChecked == true,
                ShowQrCode = ShowQrCodeCheck.IsChecked == true,
                QrCodePath = prefs.QrCodePath,
                StoreAddress = StoreAddressBox.Text?.Trim() ?? string.Empty,
                StoreInn = UserPreferences.Instance.StoreInn ?? string.Empty,
                GraphicPrintMode = GraphicModeRadio.IsChecked == true,
            };
        }

        private void ScaleComCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
            RefreshScalePortStatus();

        private void SelectScaleComPort(string? savedPort)
        {
            var port = HardwarePortHelper.NormalizeComPort(savedPort, "");
            if (string.IsNullOrWhiteSpace(port))
                return;

            foreach (var item in ScaleComCombo.Items)
            {
                if (item is string existing
                    && string.Equals(existing, port, StringComparison.OrdinalIgnoreCase))
                {
                    ScaleComCombo.SelectedItem = existing;
                    return;
                }
            }

            ScaleComCombo.Items.Add(port);
            ScaleComCombo.SelectedItem = port;
        }

        private string GetSelectedScaleComPort()
        {
            if (ScaleComCombo.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
                return HardwarePortHelper.NormalizeComPort(selected);

            return HardwarePortHelper.NormalizeComPort("");
        }

        private void RefreshScalePortStatus()
        {
            if (StatusScalePortText == null)
                return;

            var port = GetSelectedScaleComPort();
            var activeScale = App.GetRequiredService<NurMarketKassa.Services.Hardware.IWeightScaleService>();
            if (activeScale.IsAvailable
                && string.Equals(
                    HardwarePortHelper.NormalizeComPort(UserPreferences.Instance.ScaleComPort),
                    HardwarePortHelper.NormalizeComPort(port),
                    StringComparison.OrdinalIgnoreCase))
            {
                StatusScalePortText.Text = $"Порт используется весами. {activeScale.Status}";
                StatusScalePortText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
                return;
            }

            var probe = ScaleReaderService.ProbePort(port);
            StatusScalePortText.Text = probe.Message;
            StatusScalePortText.Foreground = probe.State switch
            {
                ScaleReaderService.ScalePortState.Available => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
                ScaleReaderService.ScalePortState.Busy => new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C)),
                ScaleReaderService.ScalePortState.NotSpecified => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                _ => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            };
        }

        private async void CheckScale_Click(object? sender, RoutedEventArgs e)
        {
            var prefs = UserPreferences.Instance;
            if (!prefs.ScaleEnabled)
            {
                ShowScaleAlert("Весы выключены. Включите на вкладке «Весы».", true);
                return;
            }
            try
            {
                ScaleReaderService.ValidateSettings(prefs.ToScaleSettings());
                var scale = App.GetRequiredService<NurMarketKassa.Services.Hardware.IWeightScaleService>();
                scale.Start();
                await Task.Delay(2000);
                double? weight = scale.LastWeight;
                string status = scale.Status;
                string msg = weight.HasValue ? $"Текущий вес: {weight.Value:F3} кг. Статус: {status}." : $"Статус: {status}. Данные не получены.";
                ShowScaleAlert(msg, false);
            }
            catch (Exception ex)
            {
                ShowScaleAlert("Ошибка весов: " + ex.Message, true);
            }
            finally
            {
                RefreshScalePortStatus();
            }
        }

        private void RefreshCatalogDiagnostics()
        {
            if (CatalogDiagnosticsText == null)
                return;

            CatalogDiagnosticsText.Text = "Диагностика каталога недоступна в Avalonia-сборке.";
        }

        private void ShowScaleAlert(string message, bool isError)
        {
            ScaleAlert.IsVisible = true;
            ScaleAlertText.Text = message;
            ScaleAlert.Background = ThemeBrush(
                isError ? "BrushWarningSoft" : "BrushSuccessSoft",
                isError ? Brushes.DarkGoldenrod : Brushes.DarkGreen);
            ScaleAlert.BorderBrush = ThemeBrush(
                isError ? "BrushWarning" : "BrushUiStatusOk",
                isError ? Brushes.Orange : Brushes.Green);
        }

        private IBrush ThemeBrush(string key, IBrush fallback) =>
            Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
            && value is IBrush brush
                ? brush
                : fallback;

        private async Task<string?> PickImagePathAsync(string title)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } }
                }
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
    }
}
