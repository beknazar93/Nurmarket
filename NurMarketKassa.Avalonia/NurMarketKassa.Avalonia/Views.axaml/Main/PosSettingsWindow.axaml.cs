using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.ViewModels.Settings;

#nullable enable

namespace NurMarketKassa.AvaloniaHost.Views
{
    public partial class PosSettingsWindow : Window, IOwnerSection
    {
        public SettingsViewModel SettingsVm { get; }

        private readonly ScaleSettingsView _scaleView = new();
        private readonly PrintSettingsView _printView = new();
        private readonly ScreenSettingsView _screenView = new();
        private readonly MonitorSettingsView _monitorView;
        private readonly UpdatesSettingsView _updatesView = new();
        private readonly OperationsSettingsView _operationsView = new();
        private readonly SettingsView _customizationView = new();
        private readonly AccountView _accountView = new();
        private readonly EmployeesSettingsView _employeesView = new();
        private readonly KeyBindingsSettingsView _keysView = new();

        private Button[] _navButtons = Array.Empty<Button>();

        // --- Scale ---
        private CheckBox ScaleEnabledCheck => _scaleView.ScaleEnabledCheck;
        private ComboBox ScaleComCombo => _scaleView.ScaleComCombo;
        private ComboBox Scale2ComCombo => _scaleView.Scale2ComCombo;
        private ComboBox Scale3ComCombo => _scaleView.Scale3ComCombo;
        private TextBlock StatusScalePortText => _scaleView.StatusScalePortText;
        private TextBox ScaleBaudBox => _scaleView.ScaleBaudBox;
        private TextBox ScaleHexBox => _scaleView.ScaleHexBox;
        private TextBox ScalePollBox => _scaleView.ScalePollBox;
        private Border ScaleAlert => _scaleView.ScaleAlert;
        private TextBlock ScaleAlertText => _scaleView.ScaleAlertText;

        // --- Pole display (дисплей цены покупателя) ---
        private CheckBox PoleDisplayEnabledCheck => _scaleView.PoleDisplayEnabledCheck;
        private ComboBox PoleDisplayComCombo => _scaleView.PoleDisplayComCombo;
        private TextBox PoleDisplayBaudBox => _scaleView.PoleDisplayBaudBox;
        private Button TestPoleDisplayButton => _scaleView.TestPoleDisplayButton;
        private Border PoleDisplayAlert => _scaleView.PoleDisplayAlert;
        private TextBlock PoleDisplayAlertText => _scaleView.PoleDisplayAlertText;
        private ComboBox PoleDisplayProtocolCombo => _scaleView.PoleDisplayProtocolCombo;
        private Button FindPoleDisplayButton => _scaleView.FindPoleDisplayButton;
        private WrapPanel PoleDisplayProbePanel => _scaleView.PoleDisplayProbePanel;

        // --- Print ---
        private CheckBox ReceiptEnabledCheck => _printView.ReceiptEnabledCheck;
        private TextBox ReceiptLptBox => _printView.ReceiptLptBox;
        private ComboBox DiscoveredPrintersCombo => _printView.DiscoveredPrintersCombo;
        private Button BtnFindPrinters => _printView.BtnFindPrinters;
        private TextBlock StatusPortText => _printView.StatusPortText;
        private Button BtnPhysicalPrint => _printView.BtnPhysicalPrint;
        private TextBox ReceiptRetryBox => _printView.ReceiptRetryBox;
        private CheckBox CashDrawerEnabledCheck => _printView.CashDrawerEnabledCheck;
        private ComboBox CashDrawerPinCombo => _printView.CashDrawerPinCombo;
        private Button BtnTestCashDrawer => _printView.BtnTestCashDrawer;
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

        /// <summary>2026-09-08: тот же "Размер шрифта", но продублирован в блоке ESC/POS-настроек
        /// (владелец не видел его там, где настраивает именно текстовую печать — "вот тут
        /// ненастроить размер"). Два визуальных места, одно значение (UserPreferences.
        /// GraphicFontSize) — держатся в синхроне через SyncFontSizeCombo, см. WireChildEvents.</summary>
        private ComboBox TextFontSizeCombo => _printView.TextFontSizeCombo;
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
        private CheckBox TrueFullscreenCheck => _screenView.TrueFullscreenCheck;
        private CheckBox LowPerformanceModeCheck => _screenView.LowPerformanceModeCheck;
        private Button VoiceControlOpenMarketplaceButton => _screenView.VoiceControlOpenMarketplaceButton;
        private CheckBox AutostartCheck => _screenView.AutostartCheck;
        private CheckBox AutoTouchKeyboardCheck => _screenView.AutoTouchKeyboardCheck;
        private CheckBox ShowCatalogPhotosCheck => _screenView.ShowCatalogPhotosCheck;
        private RadioButton LanguageRussianRadio => _screenView.LanguageRussianRadio;
        private RadioButton LanguageKyrgyzRadio => _screenView.LanguageKyrgyzRadio;
        private RadioButton LanguageTurkishRadio => _screenView.LanguageTurkishRadio;
        private RadioButton LanguageUzbekRadio => _screenView.LanguageUzbekRadio;
        private RadioButton LanguageEnglishRadio => _screenView.LanguageEnglishRadio;

        /// <summary>Радиокнопка ↔ язык, в обе стороны — единственное место, которое надо
        /// расширить, если появится ещё один язык интерфейса.</summary>
        private IEnumerable<(RadioButton Radio, AppLanguage Language)> LanguageRadios()
        {
            yield return (LanguageRussianRadio, AppLanguage.Russian);
            yield return (LanguageKyrgyzRadio, AppLanguage.Kyrgyz);
            yield return (LanguageTurkishRadio, AppLanguage.Turkish);
            yield return (LanguageUzbekRadio, AppLanguage.Uzbek);
            yield return (LanguageEnglishRadio, AppLanguage.English);
        }
        private ComboBox CashboxCombo => _screenView.CashboxCombo;
        private TextBox StoreNameBox => _screenView.StoreNameBox;
        private TextBox StoreAddressBox => _screenView.StoreAddressBox;
        private CheckBox ShowInnCheck => _screenView.ShowInnCheck;
        private RadioButton DoubleClickToCartRadio => _screenView.DoubleClickToCartRadio;
        private RadioButton SingleClickToCartRadio => _screenView.SingleClickToCartRadio;
        private CheckBox ResetManualAddQtyCheck => _screenView.ResetManualAddQtyCheck;
        private Slider UiScaleSlider => _screenView.UiScaleSlider;

        // --- Updates ---
        private TextBlock AppVersionText => _updatesView.AppVersionText;
        private Button CheckUpdateButton => _updatesView.CheckUpdateButton;
        private ProgressBar UpdateProgressBar => _updatesView.UpdateProgressBar;
        private TextBlock UpdateStatusText => _updatesView.UpdateStatusText;
        private Button UpdateNowButton => _updatesView.UpdateNowButton;
        private TextBlock CatalogDiagnosticsText => _updatesView.CatalogDiagnosticsText;
        private TextBlock WhatsNewTitleText => _updatesView.WhatsNewTitleText;
        private TextBlock WhatsNewText => _updatesView.WhatsNewText;
        private Button ShowVersionsButton => _updatesView.ShowVersionsButton;
        private StackPanel VersionsListPanel => _updatesView.VersionsListPanel;

        public PosSettingsWindow() : this(ResolveSettingsViewModel())
        {
        }

        private static SettingsViewModel ResolveSettingsViewModel() =>
            NurMarketKassa.AvaloniaHost.App.GetRequiredService<SettingsViewModel>();

        /// <summary>Масштаб интерфейса (Настройки → Экран → "Масштаб") применяется и к самому
        /// окну настроек, не только к MainWindow — чтобы изменение было видно сразу, не закрывая
        /// это окно (см. ScreenSettingsView.UiScaleChanged, подписка в WireChildEvents).</summary>
        internal void RefreshUiScale() => UiScaleHelper.Apply(UiScaleTransform, 1000, 820);

        public PosSettingsWindow(SettingsViewModel settingsVm)
        {
            SettingsVm = settingsVm;
            DataContext = this;
            InitializeComponent();
            this.FitToScreen();

            _customizationView.DataContext = SettingsVm;
            _monitorView = new MonitorSettingsView(new MonitorSettingsViewModel(
                this, App.GetRequiredService<AvaloniaCustomerDisplayService>()));
            WireChildEvents();
            _operationsView.LoadBankQrSettings();

            _navButtons = new[] { NavScales, NavPrint, NavScreen, NavMonitor, NavUpdates, NavOperations, NavCustomization, NavAccount, NavEmployees, NavKeys };
            // Клавиши кассира в программе владельца не нужны: там нет ни чека, ни каталога.
            NavKeys.IsVisible = !NurMarketKassa.Services.AppMode.IsOwner;
            NavigateTo(0);

            FullscreenHelper.Apply(this);

            RefreshUiScale();

            var prefs = UserPreferences.Instance;

            // Карточка процента бонусов видна только после покупки программы лояльности —
            // без неё настройка бессмысленна (баллы не начисляются вообще).
            _operationsView.LoyaltyCard.IsVisible = prefs.LoyaltyEnabled;
            _operationsView.LoyaltyEarnPercentBox.Text =
                prefs.LoyaltyEarnPercent.ToString("0.##", CultureInfo.InvariantCulture);

            ScaleEnabledCheck.IsChecked = prefs.ScaleEnabled;
            ScaleBaudBox.Text = prefs.ScaleBaudRate.ToString();
            ScaleHexBox.Text = prefs.ScaleRequestHex ?? "";
            ScalePollBox.Text = prefs.ScalePollMs.ToString();
            _scaleView.LoadLanScaleSettings();
            _scaleView.LoadScaleBrand();

            PoleDisplayEnabledCheck.IsChecked = prefs.PoleDisplayEnabled;
            PoleDisplayBaudBox.Text = prefs.PoleDisplayBaudRate.ToString();
            _scaleView.PoleDisplayProtocolLabel.Text = Tr.T("Тип табло", "Табло түрү", "Display type", "Ekran türü", "Tablo turi");
            _scaleView.PoleDisplayDescText.Text = Tr.T(
                "Табло цены для покупателя на COM-порту (не второй монитор). Цифровое табло «0.00» (как на моноблоках CY25) обычно работает на 2400 бод; если не знаете порт — нажмите «Найти табло».",
                "COM-порттогу сатып алуучу үчүн баа таблосу (экинчи монитор эмес). «0.00» сандык таблосу (CY25 моноблокторундагыдай) адатта 2400 бод менен иштейт; портту билбесеңиз — «Таблону табуу» баскычын басыңыз.",
                "Customer price display on a COM port (not a second monitor). A numeric “0.00” display (as on CY25 terminals) usually runs at 2400 baud; if you don't know the port, press “Find display”.",
                "COM portundaki müşteri fiyat ekranı (ikinci monitör değil). «0.00» sayısal ekran (CY25 hepsi bir arada cihazlardaki gibi) genellikle 2400 baud ile çalışır; portu bilmiyorsanız «Ekranı bul» düğmesine basın.",
                "COM portga ulangan xaridor narx tablosi (ikkinchi monitor emas). «0.00» raqamli tablo (CY25 monobloklaridagi kabi) odatda 2400 bod tezlikda ishlaydi; portni bilmasangiz — «Tabloni topish» tugmasini bosing.");
            FindPoleDisplayButton.Content = Tr.T("Найти табло", "Таблону табуу", "Find display", "Ekranı bul", "Tabloni topish");
            PoleDisplayProtocolCombo.ItemsSource = new[]
            {
                new ComboBoxItem
                {
                    Tag = PoleDisplayService.ProtocolLed,
                    Content = Tr.T("Цифровое табло «0.00» (8 цифр, LED)", "Сандык табло «0.00» (8 сан, LED)",
                        "Numeric display “0.00” (8 digits, LED)", "Sayısal ekran «0.00» (8 hane, LED)", "Raqamli tablo «0.00» (8 raqam, LED)"),
                },
                new ComboBoxItem
                {
                    Tag = PoleDisplayService.ProtocolText,
                    Content = Tr.T("Текстовый дисплей, 2 строки (CD5220)", "Тексттик дисплей, 2 сап (CD5220)",
                        "Text display, 2 lines (CD5220)", "Metin ekranı, 2 satır (CD5220)", "Matnli displey, 2 qator (CD5220)"),
                },
            };
            SelectComboByTag(PoleDisplayProtocolCombo, PoleDisplayService.NormalizeProtocol(prefs.PoleDisplayProtocol));

            ReceiptEnabledCheck.IsChecked = prefs.ReceiptEnabled;
            ReceiptLptBox.Text = prefs.ReceiptDevicePath;
            ReceiptEscRBox.Text = prefs.ReceiptEscR?.ToString() ?? "";
            ReceiptRetryBox.Text = prefs.ReceiptRetryCount.ToString();
            CashDrawerEnabledCheck.IsChecked = prefs.CashDrawerEnabled;
            SelectComboByTag(CashDrawerPinCombo, prefs.CashDrawerPin.ToString(CultureInfo.InvariantCulture));

            SelectComboByTag(ReceiptPaperWidthCombo, prefs.ReceiptPaperWidthMm.ToString(CultureInfo.InvariantCulture));
            ApplyPaperWidthToUi(prefs.ReceiptPaperWidthMm);

            FullscreenCheck.IsChecked = prefs.Fullscreen;
            TrueFullscreenCheck.IsChecked = prefs.TrueFullscreen;
            LowPerformanceModeCheck.IsChecked = prefs.LowPerformanceMode;
            AutostartCheck.IsChecked = prefs.Autostart || AutostartHelper.IsEnabled();
            AutoTouchKeyboardCheck.IsChecked = prefs.AutoShowTouchKeyboard;
            ShowCatalogPhotosCheck.IsChecked = prefs.ShowCatalogPhotos;
            foreach (var (radio, language) in LanguageRadios())
            {
                // Платный «Языковой пакет» (2026-09-07): без активации видны только русский и кыргызский.
                radio.IsVisible = LanguagePackGate.IsAvailable(language);
                radio.IsChecked = prefs.Language == language;
            }
            _screenView.LanguagePackHint.IsVisible = !LanguagePackGate.IsUnlocked;
            UiScaleSlider.Value = prefs.UiScalePercent;
            _screenView.PreselectCashbox(prefs.PreferredCashboxId, prefs.PreferredCashboxName);
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

            // Вид кассы (раскладка) выбирается карточками прямо в ScreenSettingsView
            // (2026-09-28, шесть раскладок вместо двух радиокнопок) и применяется сразу.

            var ports = ScaleReaderService.GetAvailablePorts().ToList();
            if (!ports.Contains(prefs.ScaleComPort, StringComparer.OrdinalIgnoreCase))
                ports.Insert(0, prefs.ScaleComPort);
            ScaleComCombo.ItemsSource = ports;
            SelectScaleComPort(prefs.ScaleComPort);
            RefreshScalePortStatus();

            // Дополнительные весы (2026-09-26): тот же список COM-портов, свой выбор у каждых.
            _scaleView.ExtraScalesTitle.Text = Tr.T("Дополнительные весы", "Кошумча таразалар", "Additional scales", "Ek tartılar", "Qo'shimcha tarozilar");
            _scaleView.ExtraScalesDesc.Text = Tr.T(
                "До трёх весов одновременно. Касса берёт вес с тех весов, на которых лежит товар. Запрос веса и интервал — как у основных весов.",
                "Бир убакта үч таразага чейин. Касса товар турган таразадан салмакты алат. Салмак суроосу жана аралык — негизги таразадагыдай.",
                "Up to three scales at once. The till reads the weight from whichever scale the item is on. Weight request and polling interval are the same as for the main scale.",
                "Aynı anda üç tartıya kadar. Kasa, ağırlığı ürünün üzerinde durduğu tartıdan alır. Ağırlık sorgusu ve sorgulama aralığı ana tartıdakiyle aynıdır.",
                "Bir vaqtning o'zida uchtagacha tarozi. Kassa og'irlikni mahsulot turgan tarozidan oladi. Og'irlik so'rovi va interval — asosiy tarozidagi kabi.");
            _scaleView.Scale2Label.Text = Tr.T("Весы 2", "Тараза 2", "Scale 2", "Tartı 2", "Tarozi 2");
            _scaleView.Scale3Label.Text = Tr.T("Весы 3", "Тараза 3", "Scale 3", "Tartı 3", "Tarozi 3");
            FillExtraScale(Scale2ComCombo, _scaleView.Scale2EnabledCheck, _scaleView.Scale2BaudBox, prefs.Scale2Enabled, prefs.Scale2ComPort, prefs.Scale2BaudRate);
            FillExtraScale(Scale3ComCombo, _scaleView.Scale3EnabledCheck, _scaleView.Scale3BaudBox, prefs.Scale3Enabled, prefs.Scale3ComPort, prefs.Scale3BaudRate);

            // Полный список подключения (спулер здесь не участвует, но WinUSB/LPT/COM/raw-USB —
            // да), а не только COM-порты: у пользователя дисплей цены оказался USB-устройством,
            // которое не появлялось в списке ScaleReaderService.GetAvailablePorts() (тот видит
            // только настоящие COM-порты) — см. PoleDisplayService, 2026-09-04.
            var poleDisplayPorts = PrinterDiscoveryService.Discover().ToList();
            if (!string.IsNullOrWhiteSpace(prefs.PoleDisplayComPort) &&
                !poleDisplayPorts.Any(p => string.Equals(p.DevicePath, prefs.PoleDisplayComPort, StringComparison.OrdinalIgnoreCase)))
            {
                poleDisplayPorts.Insert(0, new DiscoveredPrinter($"🔌 {prefs.PoleDisplayComPort}", prefs.PoleDisplayComPort));
            }
            PoleDisplayComCombo.ItemsSource = poleDisplayPorts;
            PoleDisplayComCombo.SelectedItem = poleDisplayPorts.FirstOrDefault(p =>
                string.Equals(p.DevicePath, prefs.PoleDisplayComPort, StringComparison.OrdinalIgnoreCase))
                ?? poleDisplayPorts.FirstOrDefault();

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
                GraphicQrStatusText.Text = Tr.T(
                    $"✅ QR-код сохранён: {Path.GetFileName(prefs.QrCodePath)}",
                    $"✅ QR-код сакталды: {Path.GetFileName(prefs.QrCodePath)}",
                    $"✅ QR code saved: {Path.GetFileName(prefs.QrCodePath)}",
                    $"✅ QR kodu kaydedildi: {Path.GetFileName(prefs.QrCodePath)}",
                    $"✅ QR-kod saqlandi: {Path.GetFileName(prefs.QrCodePath)}");
            else
                GraphicQrStatusText.Text = Tr.T("QR-код не загружен", "QR-код жүктөлгөн эмес", "QR code not uploaded", "QR kodu yüklenmedi", "QR-kod yuklanmagan");

            // Метка "Текущая версия:" уже выводится отдельным TextBlock над этим ("currentVersionLabel") —
            // здесь только само значение, без повторения подписи и без языка, привязанного к коду.
            AppVersionText.Text = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "—";
            RefreshWhatsNew();
            RefreshCatalogDiagnostics();
            RefreshPrinterPortStatus();
            RefreshDiscoveredPrinters();
        }

        /// <summary>Список изменений установленной версии. Он и раньше существовал
        /// (AppChangelog, на пяти языках), но показывался ОДИН раз всплывающим окном сразу
        /// после обновления и больше нигде — открыть его повторно было негде.</summary>
        private void RefreshWhatsNew()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
            WhatsNewTitleText.Text = Tr.T(
                $"Что нового в версии {version}",
                $"{version} версиясындагы жаңылыктар",
                $"What's new in version {version}",
                $"{version} sürümünde neler yeni",
                $"{version} versiyasida nima yangi");
            WhatsNewText.Text = NurMarketKassa.Services.AppChangelog.LatestAsBulletedText();
        }

        private void WireChildEvents()
        {
            ScaleComCombo.SelectionChanged += ScaleComCombo_SelectionChanged;
            _scaleView.CheckScaleButton.Click += CheckScale_Click;
            _scaleView.SaveRequested += ScaleSaveRequested;
            TestPoleDisplayButton.Click += TestPoleDisplay_Click;
            FindPoleDisplayButton.Click += FindPoleDisplay_Click;

            ReceiptLptBox.TextChanged += ReceiptLptBox_TextChanged;
            BtnFindPrinters.Click += FindPrinters_Click;
            DiscoveredPrintersCombo.SelectionChanged += DiscoveredPrintersCombo_SelectionChanged;
            ReceiptPaperWidthCombo.SelectionChanged += ReceiptPaperWidthCombo_SelectionChanged;
            BtnPhysicalPrint.Click += BtnPhysicalPrint_Click;
            BtnTestCashDrawer.Click += BtnTestCashDrawer_Click;
            _printView.TestTextPrintButton.Click += TestTextPrint_Click;
            _printView.TestGraphicPrintButton.Click += TestGraphicPrint_Click;
            _printView.LoadGraphicQrButton.Click += LoadGraphicQrCode_Click;
            _printView.DeleteGraphicQrButton.Click += DeleteGraphicQrCode_Click;
            _printView.SaveRequested += PrintSaveRequested;
            GraphicFontSizeCombo.SelectionChanged += (_, _) => SyncFontSizeCombo(GraphicFontSizeCombo, TextFontSizeCombo);
            TextFontSizeCombo.SelectionChanged += (_, _) => SyncFontSizeCombo(TextFontSizeCombo, GraphicFontSizeCombo);

            DoubleClickToCartRadio.IsCheckedChanged += ClickToCartMode_Changed;
            SingleClickToCartRadio.IsCheckedChanged += ClickToCartMode_Changed;
            ResetManualAddQtyCheck.IsCheckedChanged += ClickToCartMode_Changed;
            _screenView.SaveRequested += ScreenSaveRequested;
            _screenView.UiScaleChanged += (_, _) => RefreshUiScale();
            VoiceControlOpenMarketplaceButton.Click += (_, _) => NavigateToMarketplaceExtras();

            _updatesView.CheckUpdateButton.Click += CheckUpdate_Click;
            _updatesView.UpdateNowButton.Click += UpdateNow_Click;
            _updatesView.ShowVersionsButton.Click += ShowVersions_Click;
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

        /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): «Закрыть» внизу
        /// списка вкладок закрывала бы раздел — переход в другой раздел и так делается меню слева.</summary>
        public void AsOwnerSection() => SidebarCloseButton.IsVisible = false;

        /// <summary>Открывает окно настроек сразу на странице "Монитор" (этап 7 бэклога
        /// "Доработки" — кнопка "Открыть настройки" на упрощённом окне проверки второго
        /// монитора ведёт прямиком сюда).</summary>
        internal void NavigateToMonitor() => NavigateTo(3);

        /// <summary>Программа лояльности живёт на вкладке "Операции" (LoyaltyEnabledCheck и т.п.,
        /// см. OperationsSettingsView) — карточка в Маркетплейс → "Доп. функции" ведёт сюда
        /// (2026-09-05: раньше это была статичная заглушка "Скоро", хотя фича уже реализована).</summary>
        internal void NavigateToOperations() => NavigateTo(5);

        /// <summary>Маркетплейс больше не вкладка настроек (2026-09-05, по просьбе
        /// пользователя: "убери маркетплейс из настроек") — открывается отдельным окном поверх
        /// текущего, сразу на вкладке "Доп. функции"; кнопка на странице "Экран" по-прежнему
        /// ведёт сюда же.</summary>
        internal void NavigateToMarketplaceExtras()
        {
            var marketplace = MarketplaceWindow.Open(this);
            marketplace.ShowExtrasTab();
        }

        /// <summary>Открыть окно сразу на вкладке "Обновления" — используется значком
        /// "доступно обновление" в шапке кассы (см. MainWindow.NavigateSettingsUpdates).</summary>
        public void SelectUpdatesTab() => NavigateTo(4);

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
                7 => _accountView,
                8 => _employeesView,
                9 => _keysView,
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

        /// <summary>Сканирует установленные принтеры Windows + LPT/COM-порты и заполняет список выбора.</summary>
        private void RefreshDiscoveredPrinters()
        {
            var found = PrinterDiscoveryService.Discover();
            DiscoveredPrintersCombo.ItemsSource = found;
            DiscoveredPrintersCombo.SelectedItem = found.FirstOrDefault(p =>
                string.Equals(p.DevicePath, ReceiptLptBox.Text, StringComparison.OrdinalIgnoreCase));
        }

        private void FindPrinters_Click(object? sender, RoutedEventArgs e) => RefreshDiscoveredPrinters();

        private void DiscoveredPrintersCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DiscoveredPrintersCombo.SelectedItem is DiscoveredPrinter printer)
                ReceiptLptBox.Text = printer.DevicePath;
        }

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
            sb.AppendLine(Tr.T($"Порт: {devicePath}", $"Порт: {devicePath}", $"Port: {devicePath}", $"Port: {devicePath}", $"Port: {devicePath}"));

            var current = ex;
            int level = 0;
            while (current != null)
            {
                var prefix = level == 0 ? Tr.T("Ошибка: ", "Ката: ", "Error: ", "Hata: ", "Xato: ") : new string(' ', level * 2) + "↳ ";
                sb.AppendLine($"{prefix}{current.GetType().Name}: {current.Message}");

                if (current is System.ComponentModel.Win32Exception w32)
                    sb.AppendLine(Tr.T($"  Win32-код: {w32.NativeErrorCode}", $"  Win32 коду: {w32.NativeErrorCode}", $"  Win32 code: {w32.NativeErrorCode}", $"  Win32 kodu: {w32.NativeErrorCode}", $"  Win32 kodi: {w32.NativeErrorCode}"));

                current = current.InnerException;
                level++;
            }

            TxtPrintErrorDetails.Text = sb.ToString().TrimEnd();
            PrintErrorPanel.IsVisible = true;
        }

        /// <summary>Проверка ящика прямо из настроек — с портом и контактом, выбранными сейчас, без
        /// предварительного сохранения: ящик, подключённый не к тому контакту, на команду просто не
        /// реагирует (ошибки при этом нет), поэтому перебрать оба варианта нужно быстро. По этой же
        /// причине успех формулируется как "команда отправлена", а не "ящик открыт" — подтвердить
        /// открытие принтер не может.</summary>
        private void BtnTestCashDrawer_Click(object? sender, RoutedEventArgs e)
        {
            var devicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                StatusText.Text = Tr.T(
                    "❌ Сначала укажите порт принтера — ящик открывается через него.",
                    "❌ Адегенде принтердин портун көрсөтүңүз — акча кутусу ал аркылуу ачылат.",
                    "❌ Specify the printer port first — the cash drawer opens through it.",
                    "❌ Önce yazıcı portunu belirtin — para çekmecesi onun üzerinden açılır.",
                    "❌ Avval printer portini ko'rsating — pul qutisi u orqali ochiladi.");
                return;
            }

            var pin = CashDrawerPinCombo.SelectedItem is ComboBoxItem pinItem &&
                      int.TryParse(pinItem.Tag?.ToString(), out int selectedPin)
                ? selectedPin
                : 0;

            try
            {
                ReceiptPrintService.OpenCashDrawer(pin, devicePath);
                StatusText.Text = pin == 0
                    ? Tr.T(
                        "✅ Команда отправлена. Если ящик не открылся — выберите «Контакт 5» и нажмите ещё раз.",
                        "✅ Буйрук жөнөтүлдү. Эгер акча кутусу ачылбаса — «5-контакт» вариантын тандап, дагы бир жолу басыңыз.",
                        "✅ Command sent. If the drawer didn't open, select “Pin 5” and press again.",
                        "✅ Komut gönderildi. Çekmece açılmadıysa «Pin 5» seçeneğine geçip tekrar basın.",
                        "✅ Buyruq yuborildi. Agar pul qutisi ochilmasa — «5-kontakt» variantini tanlang va yana bir marta bosing.")
                    : Tr.T(
                        "✅ Команда отправлена. Если ящик не открылся — выберите «Контакт 2 (обычно)» и нажмите ещё раз.",
                        "✅ Буйрук жөнөтүлдү. Эгер акча кутусу ачылбаса — «2-контакт (көбүнчө)» вариантын тандап, дагы бир жолу басыңыз.",
                        "✅ Command sent. If the drawer didn't open, select “Pin 2 (standard)” and press again.",
                        "✅ Komut gönderildi. Çekmece açılmadıysa «Pin 2 (genelde)» seçeneğine geçip tekrar basın.",
                        "✅ Buyruq yuborildi. Agar pul qutisi ochilmasa — «2-kontakt (odatda)» variantini tanlang va yana bir marta bosing.");
            }
            catch (Exception ex)
            {
                StatusText.Text = Tr.T(
                    $"❌ Не удалось открыть ящик: {ex.Message}",
                    $"❌ Акча кутусун ачуу мүмкүн болгон жок: {ex.Message}",
                    $"❌ Couldn't open the cash drawer: {ex.Message}",
                    $"❌ Para çekmecesi açılamadı: {ex.Message}",
                    $"❌ Pul qutisini ochib bo'lmadi: {ex.Message}");
            }
        }

        private void BtnPhysicalPrint_Click(object? sender, RoutedEventArgs e)
        {
            var devicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
            if (string.IsNullOrWhiteSpace(devicePath))
            {
                StatusText.Text = Tr.T(
                    "❌ Укажите порт принтера (LPT1, COM3 или имя очереди Windows).",
                    "❌ Принтердин портун көрсөтүңүз (LPT1, COM3 же Windows кезегинин аты).",
                    "❌ Specify the printer port (LPT1, COM3 or a Windows print queue name).",
                    "❌ Yazıcı portunu belirtin (LPT1, COM3 veya Windows yazdırma kuyruğu adı).",
                    "❌ Printer portini ko'rsating (LPT1, COM3 yoki Windows navbati nomi).");
                return;
            }

            var probe = PrinterPortService.ProbePort(devicePath);
            if (!probe.IsAvailable)
            {
                StatusText.Text = Tr.T($"❌ Порт недоступен: {probe.Message}", $"❌ Порт жеткиликсиз: {probe.Message}", $"❌ Port unavailable: {probe.Message}", $"❌ Port kullanılamıyor: {probe.Message}", $"❌ Port mavjud emas: {probe.Message}");
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
                        StatusText.Text = Tr.T(
                            "❌ Графический чек выключен. Включите «Включить графический чек».",
                            "❌ Графикалык чек өчүк. «Макетти күйгүзүү» которгучун күйгүзүңүз.",
                            "❌ Graphic receipt is off. Turn on “Enable layout”.",
                            "❌ Grafik fiş kapalı. «Yerleşimi etkinleştir» anahtarını açın.",
                            "❌ Grafik chek o'chirilgan. «Maketni yoqish» almashtirgichini yoqing.");
                        return;
                    }

                    var settings = BuildGraphicSettingsFromUi(devicePath);
                    var bytes = GraphicReceiptGenerator.GenerateTestReceiptImage(settings, storeName);
                    ReceiptPrintService.SendRawBytes(devicePath, bytes, retry);
                    StatusText.Text = Tr.T(
                        $"✅ Графический чек ({bytes.Length} байт) отправлен на {devicePath}",
                        $"✅ Графикалык чек ({bytes.Length} байт) жөнөтүлдү: {devicePath}",
                        $"✅ Graphic receipt ({bytes.Length} bytes) sent to {devicePath}",
                        $"✅ Grafik fiş ({bytes.Length} bayt) gönderildi: {devicePath}",
                        $"✅ Grafik chek ({bytes.Length} bayt) yuborildi: {devicePath}");
                }
                else
                {
                    var testText = ReceiptPdfPreviewService.BuildTextTestReceipt(contentSettings, storeName);
                    var charWidth = ReceiptPaperProfile.GetCharWidth(ReadPaperWidthMmFromUi(ReceiptPaperWidthCombo));
                    var payload = EscPosTextReceiptPrinter.BuildEscPosPayload(cfg, testText, charWidth);
                    ReceiptPrintService.SendRawBytes(devicePath, payload, retry);
                    StatusText.Text = Tr.T(
                        $"✅ Текстовый ESC/POS чек ({payload.Length} байт) отправлен на {devicePath}",
                        $"✅ Тексттик ESC/POS чек ({payload.Length} байт) жөнөтүлдү: {devicePath}",
                        $"✅ Text ESC/POS receipt ({payload.Length} bytes) sent to {devicePath}",
                        $"✅ Metin ESC/POS fişi ({payload.Length} bayt) gönderildi: {devicePath}",
                        $"✅ Matnli ESC/POS chek ({payload.Length} bayt) yuborildi: {devicePath}");
                }

                RefreshPrinterPortStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = Tr.T(
                    $"❌ Ошибка печати в порт: {ex.Message}",
                    $"❌ Портко басып чыгаруу катасы: {ex.Message}",
                    $"❌ Error printing to the port: {ex.Message}",
                    $"❌ Porta yazdırma hatası: {ex.Message}",
                    $"❌ Portga chop etishda xato: {ex.Message}");
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
            UpdateNowButton.IsVisible = false;
            UpdateProgressBar.IsVisible = true;
            UpdateProgressBar.IsIndeterminate = true;
            UpdateStatusText.IsVisible = true;
            UpdateStatusText.Text = Tr.T("Проверка обновлений…", "Жаңыртуулар текшерилүүдө…", "Checking for updates…", "Güncellemeler kontrol ediliyor…", "Yangilanishlar tekshirilmoqda…");

            try
            {
                var result = await App.GetRequiredService<IAppUpdateService>()
                    .CheckAsync().ConfigureAwait(true);

                UpdateStatusText.Text = !result.IsConfigured
                    ? Tr.T(
                        "Проверка обновлений не настроена (не задан адрес манифеста).",
                        "Жаңыртууларды текшерүү жөндөлгөн эмес (манифесттин дареги көрсөтүлгөн эмес).",
                        "Update checking is not configured (no manifest address set).",
                        "Güncelleme kontrolü yapılandırılmamış (manifest adresi belirtilmemiş).",
                        "Yangilanishlarni tekshirish sozlanmagan (manifest manzili ko'rsatilmagan).")
                    : result.ErrorMessage != null
                        ? Tr.T(
                            $"Не удалось проверить обновления: {result.ErrorMessage}",
                            $"Жаңыртууларды текшерүү мүмкүн болгон жок: {result.ErrorMessage}",
                            $"Couldn't check for updates: {result.ErrorMessage}",
                            $"Güncellemeler kontrol edilemedi: {result.ErrorMessage}",
                            $"Yangilanishlarni tekshirib bo'lmadi: {result.ErrorMessage}")
                        : result.IsUpdateAvailable
                            ? Tr.T(
                                $"Доступна новая версия {result.LatestVersion}.",
                                $"Жаңы версия жеткиликтүү: {result.LatestVersion}.",
                                $"New version {result.LatestVersion} is available.",
                                $"Yeni sürüm mevcut: {result.LatestVersion}.",
                                $"Yangi versiya mavjud: {result.LatestVersion}.")
                            : result.TestingVersion is { } testing
                                ? Tr.T(
                                    $"Версия {testing} сейчас находится в тестировании. По его завершении можно будет обновиться.",
                                    $"{testing} версиясы азыр текшерүүдөн өтүүдө. Текшерүү бүткөндөн кийин жаңыртууга болот.",
                                    $"Version {testing} is currently being tested. You'll be able to update once testing is complete.",
                                    $"{testing} sürümü şu anda test ediliyor. Test tamamlandığında güncelleyebileceksiniz.",
                                    $"{testing} versiyasi hozir sinovdan o'tmoqda. Sinov tugagach yangilash mumkin bo'ladi.")
                                : Tr.T("У вас установлена последняя версия.", "Сизде акыркы версия орнотулган.", "You have the latest version.", "En son sürüm yüklü.", "Sizda eng so'nggi versiya o'rnatilgan.");

                UpdateNowButton.IsVisible = result.IsUpdateAvailable;
            }
            finally
            {
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.IsVisible = false;
                if (sender is Button b) b.IsEnabled = true;
            }
        }

        private async void UpdateNow_Click(object? sender, RoutedEventArgs e) =>
            await DownloadAndApplyPendingUpdateAsync().ConfigureAwait(true);

        /// <summary>Общий для "Обновить" и "Откат на прошлую версию" код скачивания/установки —
        /// оба случая уже подготовили ожидающий пакет через IAppUpdateService (обычное новое
        /// обновление или PrepareRollback на выбранную прошлую версию).</summary>
        private async Task DownloadAndApplyPendingUpdateAsync()
        {
            var updateService = App.GetRequiredService<IAppUpdateService>();
            CheckUpdateButton.IsEnabled = false;
            UpdateNowButton.IsEnabled = false;
            ShowVersionsButton.IsEnabled = false;
            UpdateProgressBar.IsVisible = true;
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 0;
            UpdateStatusText.IsVisible = true;
            UpdateStatusText.Text = Tr.T("Скачивание обновления… 0%", "Жаңыртуу жүктөлүүдө… 0%", "Downloading update… 0%", "Güncelleme indiriliyor… %0", "Yangilanish yuklab olinmoqda… 0%");

            try
            {
                await updateService.DownloadAsync(percent =>
                {
                    // DownloadUpdatesAsync репортит прогресс с фонового потока Velopack.
                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateProgressBar.Value = percent;
                        UpdateStatusText.Text = Tr.T($"Скачивание обновления… {percent}%", $"Жаңыртуу жүктөлүүдө… {percent}%", $"Downloading update… {percent}%", $"Güncelleme indiriliyor… %{percent}", $"Yangilanish yuklab olinmoqda… {percent}%");
                    });
                }).ConfigureAwait(true);

                UpdateStatusText.Text = Tr.T("Обновление скачано. Касса сейчас перезапустится…", "Жаңыртуу жүктөлдү. Касса азыр кайра ачылат…", "Update downloaded. The till will now restart…", "Güncelleme indirildi. Kasa şimdi yeniden başlatılacak…", "Yangilanish yuklab olindi. Kassa hozir qayta ishga tushadi…");
                await Task.Delay(1200).ConfigureAwait(true);

                // Не возвращает управление — Velopack завершает процесс изнутри.
                updateService.ApplyUpdateAndRestart();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Update download/apply failed: {ex}", "WARNING");
                UpdateStatusText.Text = Tr.T($"Не удалось обновить: {ex.Message}", $"Жаңыртуу мүмкүн болгон жок: {ex.Message}", $"Could not update: {ex.Message}", $"Güncellenemedi: {ex.Message}", $"Yangilab bo'lmadi: {ex.Message}");
                UpdateProgressBar.IsVisible = false;
                CheckUpdateButton.IsEnabled = true;
                UpdateNowButton.IsEnabled = true;
                ShowVersionsButton.IsEnabled = true;
            }
        }

        private async void ShowVersions_Click(object? sender, RoutedEventArgs e)
        {
            // Уже показан — второй клик по той же кнопке сворачивает список обратно,
            // без повторного похода на GitHub.
            if (VersionsListPanel.IsVisible)
            {
                VersionsListPanel.IsVisible = false;
                ShowVersionsButton.Content = Tr.T("Показать версии", "Версияларды көрсөтүү", "Show versions", "Sürümleri göster", "Versiyalarni ko'rsatish");
                return;
            }

            ShowVersionsButton.IsEnabled = false;
            UpdateStatusText.IsVisible = true;
            UpdateStatusText.Text = Tr.T("Загрузка списка версий…", "Версиялардын тизмеси жүктөлүүдө…", "Loading the list of versions…", "Sürüm listesi yükleniyor…", "Versiyalar ro'yxati yuklanmoqda…");

            try
            {
                var updateService = App.GetRequiredService<IAppUpdateService>();
                var versions = await updateService.ListVersionsAsync().ConfigureAwait(true);

                VersionsListPanel.Children.Clear();
                if (versions.Count == 0)
                {
                    UpdateStatusText.Text = Tr.T(
                        "Не удалось получить список версий (нет связи с GitHub или релизы недоступны).",
                        "Версиялардын тизмесин алуу мүмкүн болгон жок (GitHub менен байланыш жок же релиздер жеткиликсиз).",
                        "Couldn't get the list of versions (no connection to GitHub or releases are unavailable).",
                        "Sürüm listesi alınamadı (GitHub'a bağlantı yok veya sürümlere erişilemiyor).",
                        "Versiyalar ro'yxatini olib bo'lmadi (GitHub bilan aloqa yo'q yoki relizlar mavjud emas).");
                    return;
                }

                foreach (var version in versions)
                    VersionsListPanel.Children.Add(BuildVersionRow(version, updateService));

                VersionsListPanel.IsVisible = true;
                ShowVersionsButton.Content = Tr.T("Скрыть версии", "Версияларды жашыруу", "Hide versions", "Sürümleri gizle", "Versiyalarni yashirish");
                UpdateStatusText.Text = "";
                UpdateStatusText.IsVisible = false;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Version list load failed: {ex}", "WARNING");
                UpdateStatusText.Text = Tr.T(
                    $"Не удалось получить список версий: {ex.Message}",
                    $"Версиялардын тизмесин алуу мүмкүн болгон жок: {ex.Message}",
                    $"Couldn't get the list of versions: {ex.Message}",
                    $"Sürüm listesi alınamadı: {ex.Message}",
                    $"Versiyalar ro'yxatini olib bo'lmadi: {ex.Message}");
            }
            finally
            {
                ShowVersionsButton.IsEnabled = true;
            }
        }

        private Control BuildVersionRow(AppReleaseVersion version, IAppUpdateService updateService)
        {
            var label = version.IsCurrent ? Tr.T($"{version.Version} (текущая)", $"{version.Version} (учурдагы)", $"{version.Version} (current)", $"{version.Version} (mevcut)", $"{version.Version} (joriy)") : version.Version;
            var versionText = new TextBlock
            {
                Text = label,
                FontWeight = version.IsCurrent ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var rollbackButton = new Button
            {
                Classes = { "SettingsFlatButton" },
                Content = Tr.T("Откатить", "Кайтаруу", "Roll back", "Geri al", "Qaytarish"),
                IsEnabled = !version.IsCurrent,
            };
            rollbackButton.Click += async (_, _) => await RollbackToVersion_Click(version, updateService).ConfigureAwait(true);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(versionText, 0);
            Grid.SetColumn(rollbackButton, 1);
            row.Children.Add(versionText);
            row.Children.Add(rollbackButton);
            return row;
        }

        private async Task RollbackToVersion_Click(AppReleaseVersion version, IAppUpdateService updateService)
        {
            ShowVersionsButton.IsEnabled = false;
            string? notes = null;
            try
            {
                notes = await updateService.GetReleaseNotesAsync(version.Version).ConfigureAwait(true);
                // Разметка описания на пяти языках → обычный текст на языке программы (2026-09-28).
                notes = NurMarketKassa.Services.ReleaseNotesText.Plain(notes);
            }
            finally
            {
                ShowVersionsButton.IsEnabled = true;
            }

            // Вопрос — первым: описание версии бывает длинным, и раньше вопрос с кнопками
            // оказывался где-то под ним (2026-09-24, «не видна кнопка»).
            var message = string.IsNullOrWhiteSpace(notes)
                ? Tr.T(
                    $"Установить версию {version.Version} вместо текущей? Касса скачает пакет и перезапустится.",
                    $"Учурдагы версиянын ордуна {version.Version} версиясын орнотосузбу? Касса пакетти жүктөп алып, кайра ачылат.",
                    $"Install version {version.Version} instead of the current one? The till will download the package and restart.",
                    $"Mevcut sürüm yerine {version.Version} sürümü kurulsun mu? Kasa paketi indirip yeniden başlayacak.",
                    $"Joriy versiya o'rniga {version.Version} versiyasini o'rnatasizmi? Kassa paketni yuklab oladi va qayta ishga tushadi.")
                : Tr.T(
                    $"Установить версию {version.Version} вместо текущей? Касса скачает пакет и перезапустится.",
                    $"Учурдагы версиянын ордуна {version.Version} версиясын орнотосузбу? Касса пакетти жүктөп алып, кайра ачылат.",
                    $"Install version {version.Version} instead of the current one? The till will download the package and restart.",
                    $"Mevcut sürüm yerine {version.Version} sürümü kurulsun mu? Kasa paketi indirip yeniden başlayacak.",
                    $"Joriy versiya o'rniga {version.Version} versiyasini o'rnatasizmi? Kassa paketni yuklab oladi va qayta ishga tushadi.")
                  + Tr.T(
                      $"\n\nЧто было в версии {version.Version}:\n\n{notes}",
                      $"\n\n{version.Version} версиясындагы өзгөрүүлөр:\n\n{notes}",
                      $"\n\nWhat's new in version {version.Version}:\n\n{notes}",
                      $"\n\n{version.Version} sürümünde neler vardı:\n\n{notes}",
                      $"\n\n{version.Version} versiyasida nimalar bor edi:\n\n{notes}");

            var confirmed = PosMessageBox.Show(
                this,
                message,
                Tr.T("Откат версии", "Версияны кайтаруу", "Version rollback", "Sürümü geri alma", "Versiyani qaytarish"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed)
                return;

            if (!updateService.PrepareRollback(version.Version))
            {
                UpdateStatusText.IsVisible = true;
                UpdateStatusText.Text = Tr.T(
                    $"Версия {version.Version} больше не найдена в списке релизов.",
                    $"{version.Version} версиясы релиздердин тизмесинен табылган жок.",
                    $"Version {version.Version} is no longer in the list of releases.",
                    $"{version.Version} sürümü artık sürüm listesinde yok.",
                    $"{version.Version} versiyasi relizlar ro'yxatida endi yo'q.");
                return;
            }

            await DownloadAndApplyPendingUpdateAsync().ConfigureAwait(true);
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
            SelectFontSizeCombo(GraphicFontSizeCombo, fontSize);
            SelectFontSizeCombo(TextFontSizeCombo, fontSize);
        }

        private static void SelectFontSizeCombo(ComboBox combo, float fontSize)
        {
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag != null
                    && float.TryParse(item.Tag.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out float val)
                    && Math.Abs(val - fontSize) < 0.01f)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            combo.SelectedIndex = 2;
        }

        private bool _suppressFontSizeSync;

        /// <summary>Держит GraphicFontSizeCombo (карточка "Настройки чека") и TextFontSizeCombo
        /// (карточка ESC/POS) в синхроне — это два поля одного и того же значения
        /// UserPreferences.GraphicFontSize, показанные в двух местах.</summary>
        private void SyncFontSizeCombo(ComboBox source, ComboBox target)
        {
            if (_suppressFontSizeSync)
                return;
            if (source.SelectedItem is not ComboBoxItem item)
                return;

            _suppressFontSizeSync = true;
            foreach (var targetItem in target.Items.OfType<ComboBoxItem>())
            {
                if (Equals(targetItem.Tag?.ToString(), item.Tag?.ToString()))
                {
                    target.SelectedItem = targetItem;
                    break;
                }
            }
            _suppressFontSizeSync = false;
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
                _scaleView.SaveStatusText.Text = Tr.T("Настройки не сохранены.", "Жөндөөлөр сакталган жок.", "Settings not saved.", "Ayarlar kaydedilmedi.", "Sozlamalar saqlanmadi.");
                PosMessageBox.Show(ex.Message, Tr.T("Настройки весов", "Тараза жөндөөлөрү", "Scale settings", "Tartı ayarları", "Tarozi sozlamalari"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
                return false;
            }

            var scale2 = ReadExtraScale(Scale2ComCombo, _scaleView.Scale2EnabledCheck, _scaleView.Scale2BaudBox);
            var scale3 = ReadExtraScale(Scale3ComCombo, _scaleView.Scale3EnabledCheck, _scaleView.Scale3BaudBox);
            var usedPorts = new List<string>();
            if (prefs.ScaleEnabled)
                usedPorts.Add(prefs.ScaleComPort);
            var extras = new[]
            {
                (Name: Tr.T("Весы 2", "Тараза 2", "Scale 2", "Tartı 2", "Tarozi 2"), Scale: scale2),
                (Name: Tr.T("Весы 3", "Тараза 3", "Scale 3", "Tartı 3", "Tarozi 3"), Scale: scale3),
            };
            foreach (var (name, extra) in extras)
            {
                if (!extra.Enabled)
                    continue;
                if (extra.Port.Length == 0 || usedPorts.Contains(extra.Port, StringComparer.OrdinalIgnoreCase))
                {
                    _scaleView.SaveStatusText.Text = Tr.T("Настройки не сохранены.", "Жөндөөлөр сакталган жок.", "Settings not saved.", "Ayarlar kaydedilmedi.", "Sozlamalar saqlanmadi.");
                    PosMessageBox.Show(extra.Port.Length == 0
                            ? Tr.T($"{name}: выберите COM-порт.", $"{name}: COM-портту тандаңыз.", $"{name}: select a COM port.", $"{name}: bir COM portu seçin.", $"{name}: COM portni tanlang.")
                            : Tr.T(
                                $"{name}: порт {extra.Port} уже занят другими весами — у каждых весов свой порт.",
                                $"{name}: {extra.Port} портун башка тараза колдонуп жатат — ар бир таразанын өз порту болушу керек.",
                                $"{name}: port {extra.Port} is already used by another scale — each scale needs its own port.",
                                $"{name}: {extra.Port} portu zaten başka bir tartı tarafından kullanılıyor — her tartının kendi portu olmalı.",
                                $"{name}: {extra.Port} portini boshqa tarozi band qilgan — har bir tarozining o'z porti bo'lishi kerak."),
                        Tr.T("Настройки весов", "Тараза жөндөөлөрү", "Scale settings", "Tartı ayarları", "Tarozi sozlamalari"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    return false;
                }
                usedPorts.Add(extra.Port);
            }

            (prefs.Scale2Enabled, prefs.Scale2ComPort, prefs.Scale2BaudRate) = scale2;
            (prefs.Scale3Enabled, prefs.Scale3ComPort, prefs.Scale3BaudRate) = scale3;

            prefs.PoleDisplayEnabled = PoleDisplayEnabledCheck.IsChecked == true;
            prefs.PoleDisplayComPort = GetSelectedPoleDisplayComPort();
            int.TryParse(PoleDisplayBaudBox.Text?.Trim(), out int poleDisplayBaud);
            prefs.PoleDisplayBaudRate = poleDisplayBaud > 0 ? poleDisplayBaud : 2400;
            prefs.PoleDisplayProtocol = PoleDisplayService.NormalizeProtocol(
                (PoleDisplayProtocolCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString());

            prefs.SaveToDisk();

            // Recreate the singleton reader so the new COM settings are used immediately.
            App.GetRequiredService<NurMarketKassa.Services.Hardware.IWeightScaleService>().Start();
            NurMarketKassa.Services.Hardware.PoleDisplayService.Instance.Start();
            _scaleView.SaveStatusText.Text = Tr.T("Настройки весов сохранены.", "Тараза жөндөөлөрү сакталды.", "Scale settings saved.", "Tartı ayarları kaydedildi.", "Tarozi sozlamalari saqlandi.");
            RefreshScalePortStatus();
            return true;
        }

        private bool SavePrintSettings()
        {
            var prefs = UserPreferences.Instance;

            prefs.ReceiptEnabled = ReceiptEnabledCheck.IsChecked == true;
            prefs.ReceiptDevicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
            // Процент начисления бонусов. Пустое или нечисловое значение оставляет прежнее, а не
            // обнуляет программу молча; диапазон тот же, что при чтении файла настроек (0..100).
            if (double.TryParse(
                    (_operationsView.LoyaltyEarnPercentBox.Text ?? "").Trim().Replace(',', '.'),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var loyaltyPercent))
            {
                prefs.LoyaltyEarnPercent = Math.Clamp(loyaltyPercent, 0, 100);
            }

            prefs.CashDrawerEnabled = CashDrawerEnabledCheck.IsChecked == true;
            prefs.CashDrawerPin =
                CashDrawerPinCombo.SelectedItem is ComboBoxItem pinItem &&
                int.TryParse(pinItem.Tag?.ToString(), out int drawerPin)
                    ? drawerPin
                    : 0;
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
                _printView.SaveStatusText.Text = Tr.T("Настройки не сохранены.", "Жөндөөлөр сакталган жок.", "Settings not saved.", "Ayarlar kaydedilmedi.", "Sozlamalar saqlanmadi.");
                PosMessageBox.Show(ex.Message, Tr.T("Настройки печати", "Басып чыгаруу жөндөөлөрү", "Print settings", "Yazdırma ayarları", "Chop etish sozlamalari"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
            _printView.SaveStatusText.Text = Tr.T("Настройки печати сохранены.", "Басып чыгаруу жөндөөлөрү сакталды.", "Print settings saved.", "Yazdırma ayarları kaydedildi.", "Chop etish sozlamalari saqlandi.");
            RefreshPrinterPortStatus();
            return true;
        }

        private bool SaveScreenSettings()
        {
            var prefs = UserPreferences.Instance;

            var lowPerfChanged = prefs.LowPerformanceMode != (LowPerformanceModeCheck.IsChecked == true);
            prefs.LowPerformanceMode = LowPerformanceModeCheck.IsChecked == true;

            prefs.Fullscreen = FullscreenCheck.IsChecked == true;
            prefs.TrueFullscreen = TrueFullscreenCheck.IsChecked == true;
            prefs.Autostart = AutostartCheck.IsChecked == true;
            prefs.AutoShowTouchKeyboard = AutoTouchKeyboardCheck.IsChecked == true;
            prefs.ShowCatalogPhotos = ShowCatalogPhotosCheck.IsChecked == true;
            foreach (var product in CatalogCacheService.Products)
                product.RefreshPhotoVisibility();

            prefs.UiScalePercent = UiScaleSlider.Value;
            App.GetRequiredService<MainWindowHostBridge>().Window?.RefreshUiScale();
            RefreshUiScale();

            var newLanguage = LanguageRadios().FirstOrDefault(r => r.Radio.IsChecked == true).Language;
            if (prefs.Language != newLanguage)
            {
                prefs.Language = newLanguage;
                LocalizationManager.Apply(newLanguage);
            }

            var selectedCashbox = _screenView.SelectedCashbox;
            var cashboxChanged = prefs.PreferredCashboxId != selectedCashbox.Id;
            prefs.PreferredCashboxId = selectedCashbox.Id;
            prefs.PreferredCashboxName = selectedCashbox.Id is null ? null : selectedCashbox.Name;
            if (cashboxChanged && string.IsNullOrWhiteSpace(App.PosCashboxId))
            {
                // Смена ещё не открыта — можно применить выбор сразу, без перезапуска кассы.
                if (selectedCashbox.Id is { } id)
                {
                    App.PosCashboxId = id;
                    NurMarketKassa.App.PosCashboxId = id;
                }
            }
            else if (cashboxChanged)
            {
                App.GetRequiredService<IUserPrompts>()
                    .ShowToast(Tr.T(
                        "Касса изменена. Применится после перезапуска смены/кассы.",
                        "Касса өзгөртүлдү. Смена же касса кайра ачылгандан кийин күчүнө кирет.",
                        "Till changed. It takes effect after the shift or the till is restarted.",
                        "Kasa değiştirildi. Vardiya veya kasa yeniden başlatıldıktan sonra geçerli olur.",
                        "Kassa o'zgartirildi. Smena yoki kassa qayta ishga tushirilgandan keyin kuchga kiradi."));
            }
            prefs.SingleClickToCart = SingleClickToCartRadio.IsChecked == true;
            prefs.ResetManualAddQtyAfterAdd = ResetManualAddQtyCheck.IsChecked == true;

            prefs.StoreName = StoreNameBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(prefs.StoreName))
                prefs.StoreName = "MARKET PLUS";
            prefs.StoreAddress = StoreAddressBox.Text?.Trim() ?? string.Empty;
            prefs.ShowInn = ShowInnCheck.IsChecked == true;

            prefs.SaveToDisk();
            AutostartHelper.SyncFromPreference(prefs.Autostart);
            _screenView.SaveStatusText.Text = lowPerfChanged
                ? Tr.T(
                    "Настройки экрана сохранены. Перезапустите кассу, чтобы применить режим слабого устройства.",
                    "Экран жөндөөлөрү сакталды. Алсыз түзмөк режимин колдонуу үчүн кассаны кайра иштетиңиз.",
                    "Screen settings saved. Restart the till to apply low-power device mode.",
                    "Ekran ayarları kaydedildi. Zayıf cihaz modunu uygulamak için kasayı yeniden başlatın.",
                    "Ekran sozlamalari saqlandi. Zaif qurilma rejimini qo'llash uchun kassani qayta ishga tushiring.")
                : Tr.T("Настройки экрана сохранены.", "Экран жөндөөлөрү сакталды.", "Screen settings saved.", "Ekran ayarları kaydedildi.", "Ekran sozlamalari saqlandi.");
            return true;
        }

        private async void LoadGraphicQrCode_Click(object? sender, RoutedEventArgs e)
        {
            var path = await PickImagePathAsync(Tr.T("Выберите QR-код (сохранится для будущего)", "QR-кодду тандаңыз (кийинкиге сакталат)", "Select a QR code (it will be saved for later)", "QR kodu seçin (sonraki kullanımlar için kaydedilir)", "QR-kodni tanlang (keyingi safar uchun saqlanadi)"));
            if (!string.IsNullOrEmpty(path))
            {
                var prefs = UserPreferences.Instance;
                prefs.QrCodePath = path;
                prefs.SaveToDisk();
                GraphicQrStatusText.Text = Tr.T(
                    $"✅ QR-код сохранён: {Path.GetFileName(path)}",
                    $"✅ QR-код сакталды: {Path.GetFileName(path)}",
                    $"✅ QR code saved: {Path.GetFileName(path)}",
                    $"✅ QR kodu kaydedildi: {Path.GetFileName(path)}",
                    $"✅ QR-kod saqlandi: {Path.GetFileName(path)}");
            }
        }

        private void DeleteGraphicQrCode_Click(object? sender, RoutedEventArgs e)
        {
            var prefs = UserPreferences.Instance;
            prefs.QrCodePath = "";
            prefs.SaveToDisk();
            GraphicQrStatusText.Text = Tr.T("QR-код не загружен", "QR-код жүктөлгөн эмес", "QR code not uploaded", "QR kodu yüklenmedi", "QR-kod yuklanmagan");
        }

        private async void TestGraphicPrint_Click(object? sender, RoutedEventArgs e)
        {
            if (!GraphicReceiptEnabledCheck.IsChecked == true)
            {
                StatusText.Text = Tr.T(
                    "❌ Графический чек выключен. Включите его в настройках (чекбокс «Включить графический чек»).",
                    "❌ Графикалык чек өчүк. Аны жөндөөлөрдөн күйгүзүңүз («Макетти күйгүзүү» которгучу).",
                    "❌ Graphic receipt is off. Turn it on in the settings (the “Enable layout” switch).",
                    "❌ Grafik fiş kapalı. Ayarlardan açın («Yerleşimi etkinleştir» anahtarı).",
                    "❌ Grafik chek o'chirilgan. Uni sozlamalarda yoqing («Maketni yoqish» almashtirgichi).");
                return;
            }

            if (GraphicModeRadio.IsChecked != true)
            {
                StatusText.Text = Tr.T(
                    "❌ Сейчас выбран текстовый режим. Переключите на графический в настройках.",
                    "❌ Азыр тексттик режим тандалган. Жөндөөлөрдөн графикалык режимге которуңуз.",
                    "❌ Text mode is currently selected. Switch to graphics mode in the settings.",
                    "❌ Şu anda metin modu seçili. Ayarlardan grafik moduna geçin.",
                    "❌ Hozir matn rejimi tanlangan. Sozlamalarda grafik rejimga o'tkazing.");
                return;
            }

            try
            {
                var devicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
                var settings = BuildGraphicSettingsFromUi(devicePath);
                var storeName = StoreNameBox.Text ?? string.Empty;
                var tempPdfPath = Path.Combine(Path.GetTempPath(), $"test_graphic_{Guid.NewGuid():N}.pdf");

                ReceiptPdfPreviewService.GenerateGraphicReceiptPdf(tempPdfPath, settings, storeName);

                Process.Start(new ProcessStartInfo(tempPdfPath) { UseShellExecute = true });
                StatusText.Text = Tr.T(
                    "Предпросмотр графического чека открыт в программе для просмотра PDF. Для физической печати нажмите «Печать в сам порт».",
                    "Графикалык чектин алдын ала көрүнүшү PDF көрүүчү программада ачылды. Кагазга басып чыгаруу үчүн порттун жанындагы «Текшерүү» баскычын басыңыз.",
                    "The graphic receipt preview is open in your PDF viewer. To print on paper, press “Test” next to the printer port.",
                    "Grafik fiş önizlemesi PDF görüntüleyicide açıldı. Kâğıda yazdırmak için yazıcı portunun yanındaki «Test» düğmesine basın.",
                    "Grafik chekning oldindan ko'rinishi PDF ko'ruvchi dasturda ochildi. Qog'ozga chop etish uchun printer porti yonidagi «Test» tugmasini bosing.");
            }
            catch (Exception ex)
            {
                StatusText.Text = Tr.T($"❌ Ошибка: {ex.Message}", $"❌ Ката: {ex.Message}", $"❌ Error: {ex.Message}", $"❌ Hata: {ex.Message}", $"❌ Xato: {ex.Message}");
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

                var dialog = new ReceiptPreviewDialog(Tr.T("Предпросмотр: Текстовый чек (ESC/POS)", "Алдын ала көрүү: тексттик чек (ESC/POS)", "Preview: text receipt (ESC/POS)", "Önizleme: metin fişi (ESC/POS)", "Oldindan ko'rish: matnli chek (ESC/POS)"), testText);
                await dialog.ShowDialog<bool>(this);
                StatusText.Text = Tr.T(
                    "Предпросмотр текстового чека готов. Для физической печати нажмите «Печать в сам порт».",
                    "Тексттик чектин алдын ала көрүнүшү даяр. Кагазга басып чыгаруу үчүн порттун жанындагы «Текшерүү» баскычын басыңыз.",
                    "The text receipt preview is ready. To print on paper, press “Test” next to the printer port.",
                    "Metin fişi önizlemesi hazır. Kâğıda yazdırmak için yazıcı portunun yanındaki «Test» düğmesine basın.",
                    "Matnli chekning oldindan ko'rinishi tayyor. Qog'ozga chop etish uchun printer porti yonidagi «Test» tugmasini bosing.");
            }
            catch (Exception ex)
            {
                StatusText.Text = Tr.T(
                    $"❌ Ошибка текстовой печати: {ex.Message}",
                    $"❌ Тексттик басып чыгаруу катасы: {ex.Message}",
                    $"❌ Text printing error: {ex.Message}",
                    $"❌ Metin yazdırma hatası: {ex.Message}",
                    $"❌ Matnli chop etishda xato: {ex.Message}");
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

        private string GetSelectedPoleDisplayComPort() =>
            PoleDisplayComCombo.SelectedItem is DiscoveredPrinter selected ? selected.DevicePath : "";

        private static void FillExtraScale(ComboBox combo, CheckBox enabled, TextBox baudBox, bool isEnabled, string? savedPort, int baud)
        {
            var ports = ScaleReaderService.GetAvailablePorts().ToList();
            var port = HardwarePortHelper.NormalizeComPort(savedPort, "");
            if (port.Length > 0 && !ports.Contains(port, StringComparer.OrdinalIgnoreCase))
                ports.Insert(0, port);
            combo.ItemsSource = ports;
            combo.SelectedItem = ports.FirstOrDefault(p => string.Equals(p, port, StringComparison.OrdinalIgnoreCase));
            enabled.IsChecked = isEnabled;
            baudBox.Text = (baud > 0 ? baud : 9600).ToString(CultureInfo.InvariantCulture);
        }

        private static (bool Enabled, string Port, int Baud) ReadExtraScale(ComboBox combo, CheckBox enabled, TextBox baudBox)
        {
            var port = combo.SelectedItem is string selected ? HardwarePortHelper.NormalizeComPort(selected, "") : "";
            int.TryParse(baudBox.Text?.Trim(), out var baud);
            return (enabled.IsChecked == true, port, baud > 0 ? baud : 9600);
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
                StatusScalePortText.Text = Tr.T(
                    $"Порт используется весами. {activeScale.Status}",
                    $"Портту тараза колдонуп жатат. {activeScale.Status}",
                    $"The port is in use by the scale. {activeScale.Status}",
                    $"Port tartı tarafından kullanılıyor. {activeScale.Status}",
                    $"Portdan tarozi foydalanmoqda. {activeScale.Status}");
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
                ShowScaleAlert(Tr.T(
                    "Весы выключены. Включите на вкладке «Весы».",
                    "Тараза өчүк. Аны «Тараза» өтмөгүндө күйгүзүңүз.",
                    "The scale is turned off. Turn it on in the “Scales” tab.",
                    "Tartı kapalı. «Tartılar» sekmesinden açın.",
                    "Tarozi o'chirilgan. Uni «Tarozilar» bo'limida yoqing."), true);
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
                string msg = weight.HasValue
                    ? Tr.T(
                        $"Текущий вес: {weight.Value:F3} кг. Статус: {status}.",
                        $"Учурдагы салмак: {weight.Value:F3} кг. Абалы: {status}.",
                        $"Current weight: {weight.Value:F3} kg. Status: {status}.",
                        $"Mevcut ağırlık: {weight.Value:F3} kg. Durum: {status}.",
                        $"Joriy og'irlik: {weight.Value:F3} kg. Holat: {status}.")
                    : Tr.T(
                        $"Статус: {status}. Данные не получены.",
                        $"Абалы: {status}. Маалымат алынган жок.",
                        $"Status: {status}. No data received.",
                        $"Durum: {status}. Veri alınamadı.",
                        $"Holat: {status}. Ma'lumot olinmadi.");
                ShowScaleAlert(msg, false);
            }
            catch (Exception ex)
            {
                ShowScaleAlert(Tr.T("Ошибка весов: ", "Тараза катасы: ", "Scale error: ", "Tartı hatası: ", "Tarozi xatosi: ") + ex.Message, true);
            }
            finally
            {
                RefreshScalePortStatus();
            }
        }

        /// <summary>Требует сначала сохранить настройки (как и "Проверить весы" выше) — так
        /// тест всегда идёт по реально сохранённому порту/скорости, а не по тому, что просто
        /// набрано в поле и ещё не применилось.</summary>
        private void TestPoleDisplay_Click(object? sender, RoutedEventArgs e)
        {
            var prefs = UserPreferences.Instance;
            if (!prefs.PoleDisplayEnabled)
            {
                ShowPoleDisplayAlert(Tr.T(
                    "Дисплей выключен. Включите и нажмите «Сохранить», затем «Тест».",
                    "Дисплей өчүк. Аны күйгүзүп, «Сактоо», андан кийин «Дисплейди текшерүү» баскычын басыңыз.",
                    "The display is turned off. Turn it on and press “Save”, then “Test display”.",
                    "Ekran kapalı. Açın ve «Kaydet»e, ardından «Ekranı test et»e basın.",
                    "Displey o'chirilgan. Uni yoqing va «Saqlash», so'ng «Displeyni sinash» tugmasini bosing."), true);
                return;
            }

            var display = NurMarketKassa.Services.Hardware.PoleDisplayService.Instance;
            if (!display.IsAvailable)
            {
                ShowPoleDisplayAlert(Tr.T(
                    $"Порт не открыт: {display.Status}. Сохраните настройки и попробуйте снова.",
                    $"Порт ачылган жок: {display.Status}. Жөндөөлөрдү сактап, кайра аракет кылыңыз.",
                    $"The port isn't open: {display.Status}. Save the settings and try again.",
                    $"Port açık değil: {display.Status}. Ayarları kaydedip tekrar deneyin.",
                    $"Port ochilmagan: {display.Status}. Sozlamalarni saqlang va qayta urinib ko'ring."), true);
                return;
            }

            var (sent, message) = display.ShowTest();
            ShowPoleDisplayAlert(sent ? message : Tr.T($"Не удалось отправить: {message}", $"Жөнөтүү мүмкүн болгон жок: {message}", $"Couldn't send: {message}", $"Gönderilemedi: {message}", $"Yuborib bo'lmadi: {message}"), !sent);
        }

        private CancellationTokenSource? _poleProbeCts;

        /// <summary>«Найти табло» (2026-09-26): на каждый COM-порт уходит число-метка порт.скорость
        /// (3.2400 = COM3, 2400 бод). Табло принимает только свой вариант — его номер и остаётся на
        /// экране; кассир нажимает кнопку с этим номером, и порт со скоростью подставляются сами.</summary>
        private async void FindPoleDisplay_Click(object? sender, RoutedEventArgs e)
        {
            if (_poleProbeCts != null)
            {
                _poleProbeCts.Cancel();
                return;
            }

            var prefs = UserPreferences.Instance;
            var skip = new List<string>();
            if (prefs.ScaleEnabled)
                skip.Add(HardwarePortHelper.NormalizeComPort(prefs.ScaleComPort, ""));
            if (prefs.ReceiptEnabled)
                skip.Add(prefs.ReceiptDevicePath);

            // На время перебора отпускаем порт табло, чтобы запись из корзины не мешала.
            PoleDisplayService.Instance.Stop();
            _poleProbeCts = new CancellationTokenSource();
            FindPoleDisplayButton.Content = Tr.T("Остановить", "Токтотуу", "Stop", "Durdur", "To'xtatish");
            PoleDisplayProbePanel.Children.Clear();
            PoleDisplayProbePanel.IsVisible = false;
            try
            {
                var probes = await PoleDisplayService.ProbeLedAsync(skip,
                    status => Dispatcher.UIThread.Post(() => ShowPoleDisplayAlert(Tr.T("Идёт поиск: ", "Издөө жүрүп жатат: ", "Searching: ", "Aranıyor: ", "Qidirilmoqda: ") + status, false)),
                    _poleProbeCts.Token).ConfigureAwait(true);

                var reachable = probes.Where(p => p.Error == null).ToList();
                if (reachable.Count == 0)
                {
                    ShowPoleDisplayAlert(probes.Count == 0
                        ? Tr.T(
                            "COM-портов не найдено (кроме портов принтера и весов). Табло подключено по USB? Выберите его в списке «Устройство».",
                            "COM-порттор табылган жок (принтер менен таразанын порттору эсепке алынган жок). Табло USB аркылуу туташканбы? Аны «Түзмөк» тизмесинен тандаңыз.",
                            "No COM ports found (other than the printer and scale ports). Is the display connected via USB? Select it in the “Device” list.",
                            "COM portu bulunamadı (yazıcı ve tartı portları hariç). Ekran USB ile mi bağlı? «Cihaz» listesinden seçin.",
                            "COM portlar topilmadi (printer va tarozi portlaridan tashqari). Tablo USB orqali ulanganmi? Uni «Qurilma» ro'yxatidan tanlang.")
                        : Tr.T("Ни один COM-порт не открылся: ", "Бир да COM-порт ачылган жок: ", "None of the COM ports opened: ", "Hiçbir COM portu açılmadı: ", "Birorta ham COM port ochilmadi: ") + string.Join("; ", probes.Select(p => $"{p.Port}: {p.Error}").Distinct()), true);
                    return;
                }

                ShowPoleDisplayAlert(
                    Tr.T(
                        "Посмотрите на табло: на нём осталось число подошедшего варианта (например, 3.2400 — это COM3, 2400 бод). " +
                        "Нажмите кнопку с этим числом, затем «Сохранить». Если табло так и показывает 0.00 — выберите «Текстовый дисплей» и попробуйте «Тест».",
                        "Таблону караңыз: анда ылайык келген вариант сан менен көрүнүп турат (мисалы, 3.2400 — бул COM3, 2400 бод). " +
                        "Ошол сан жазылган баскычты, андан кийин «Сактоо» баскычын басыңыз. Эгер табло мурдагыдай эле 0.00 көрсөтсө — «Тексттик дисплей» вариантын тандап, «Дисплейди текшерүү» баскычын басып көрүңүз.",
                        "Look at the display: it now shows the number of the matching option (for example, 3.2400 means COM3, 2400 baud). " +
                        "Press the button with that number, then “Save”. If the display still shows 0.00, select “Text display” and try “Test display”.",
                        "Ekrana bakın: üzerinde çalışan seçeneğin numarası kalmıştır (örneğin 3.2400 — COM3, 2400 baud demektir). " +
                        "Bu numaranın yazılı olduğu düğmeye, ardından «Kaydet»e basın. Ekran hâlâ 0.00 gösteriyorsa «Metin ekranı» seçeneğine geçin ve «Ekranı test et» düğmesini deneyin.",
                        "Tabloga qarang: unda mos kelgan variant raqami qoldi (masalan, 3.2400 — bu COM3, 2400 bod). " +
                        "Shu raqamli tugmani, so'ng «Saqlash»ni bosing. Agar tablo hamon 0.00 ko'rsatsa — «Matnli displey» variantini tanlang va «Displeyni sinash» tugmasini bosib ko'ring."),
                    false);
                foreach (var probe in reachable)
                {
                    var button = new Button
                    {
                        Content = $"{probe.Label}  ({probe.Port}, {probe.BaudRate})",
                        Margin = new Thickness(0, 0, 8, 8),
                    };
                    var chosen = probe;
                    button.Click += (_, _) => ApplyPoleDisplayProbe(chosen);
                    PoleDisplayProbePanel.Children.Add(button);
                }

                PoleDisplayProbePanel.IsVisible = true;
            }
            catch (OperationCanceledException)
            {
                ShowPoleDisplayAlert(Tr.T("Поиск остановлен.", "Издөө токтотулду.", "Search stopped.", "Arama durduruldu.", "Qidiruv to'xtatildi."), false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Дисплей цены: поиск не удался: {ex}", "POLE_DISPLAY");
                ShowPoleDisplayAlert(Tr.T("Поиск не удался: ", "Издөө ишке ашкан жок: ", "Search failed: ", "Arama başarısız: ", "Qidiruv amalga oshmadi: ") + ex.Message, true);
            }
            finally
            {
                _poleProbeCts.Dispose();
                _poleProbeCts = null;
                FindPoleDisplayButton.Content = Tr.T("Найти табло", "Таблону табуу", "Find display", "Ekranı bul", "Tabloni topish");
                PoleDisplayService.Instance.Start();
            }
        }

        private void ApplyPoleDisplayProbe(PoleDisplayProbe probe)
        {
            PoleDisplayEnabledCheck.IsChecked = true;
            SelectComboByTag(PoleDisplayProtocolCombo, PoleDisplayService.ProtocolLed);
            PoleDisplayBaudBox.Text = probe.BaudRate.ToString(CultureInfo.InvariantCulture);

            if (PoleDisplayComCombo.ItemsSource is IEnumerable<DiscoveredPrinter> items)
            {
                var list = items.ToList();
                var match = list.FirstOrDefault(p => string.Equals(p.DevicePath, probe.Port, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    match = new DiscoveredPrinter($"🔌 {probe.Port}", probe.Port);
                    list.Insert(0, match);
                    PoleDisplayComCombo.ItemsSource = list;
                }

                PoleDisplayComCombo.SelectedItem = match;
            }

            ShowPoleDisplayAlert(Tr.T(
                $"Выбрано: {probe.Port}, {probe.BaudRate} бод, цифровое табло. Нажмите «Сохранить».",
                $"Тандалды: {probe.Port}, {probe.BaudRate} бод, сандык табло. «Сактоо» баскычын басыңыз.",
                $"Selected: {probe.Port}, {probe.BaudRate} baud, numeric display. Press “Save”.",
                $"Seçildi: {probe.Port}, {probe.BaudRate} baud, sayısal ekran. «Kaydet»e basın.",
                $"Tanlandi: {probe.Port}, {probe.BaudRate} bod, raqamli tablo. «Saqlash» tugmasini bosing."), false);
        }

        private void ShowPoleDisplayAlert(string message, bool isError)
        {
            PoleDisplayAlert.IsVisible = true;
            PoleDisplayAlertText.Text = message;
            PoleDisplayAlert.Background = ThemeBrush(
                isError ? "BrushWarningSoft" : "BrushSuccessSoft",
                isError ? Brushes.DarkGoldenrod : Brushes.DarkGreen);
            PoleDisplayAlert.BorderBrush = ThemeBrush(
                isError ? "BrushWarning" : "BrushUiStatusOk",
                isError ? Brushes.Orange : Brushes.Green);
        }

        private void RefreshCatalogDiagnostics()
        {
            if (CatalogDiagnosticsText == null)
                return;

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? Tr.T("неизвестно", "белгисиз", "unknown", "bilinmiyor", "noma'lum");
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dbPath = DatabaseService.Instance.DatabasePath;
            var dbSizeText = File.Exists(dbPath)
                ? $"{new FileInfo(dbPath).Length / 1024.0 / 1024.0:F2} {Tr.T("МБ", "МБ", "MB", "MB", "MB")}"
                : Tr.T("файл не найден", "файл табылган жок", "file not found", "dosya bulunamadı", "fayl topilmadi");
            var lastSync = CatalogCacheService.LastSyncTime is { } syncedAt
                ? syncedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
                : Tr.T("ещё не выполнялась", "азырынча аткарыла элек", "never", "henüz yapılmadı", "hali bajarilmagan");
            var crashReportsCount = CrashReportService.PendingReportCount();

            CatalogDiagnosticsText.Text =
                Tr.T($"Версия приложения: {version}\n", $"Колдонмонун версиясы: {version}\n", $"App version: {version}\n", $"Uygulama sürümü: {version}\n", $"Dastur versiyasi: {version}\n") +
                Tr.T($"Рабочая директория: {baseDir}\n", $"Иштөө директориясы: {baseDir}\n", $"Working directory: {baseDir}\n", $"Çalışma dizini: {baseDir}\n", $"Ish katalogi: {baseDir}\n") +
                Tr.T($"Локальная база: {dbPath} ({dbSizeText})\n", $"Жергиликтүү база: {dbPath} ({dbSizeText})\n", $"Local database: {dbPath} ({dbSizeText})\n", $"Yerel veritabanı: {dbPath} ({dbSizeText})\n", $"Mahalliy baza: {dbPath} ({dbSizeText})\n") +
                Tr.T($"Товаров в кэше каталога: {CatalogCacheService.Products.Count}\n", $"Каталог кэшиндеги товарлар: {CatalogCacheService.Products.Count}\n", $"Products in catalog cache: {CatalogCacheService.Products.Count}\n", $"Katalog önbelleğindeki ürünler: {CatalogCacheService.Products.Count}\n", $"Katalog keshidagi mahsulotlar: {CatalogCacheService.Products.Count}\n") +
                Tr.T($"Последняя синхронизация каталога: {lastSync}\n", $"Каталогдун акыркы синхрондоштуруусу: {lastSync}\n", $"Last catalog sync: {lastSync}\n", $"Son katalog senkronizasyonu: {lastSync}\n", $"Katalogning oxirgi sinxronlanishi: {lastSync}\n") +
                Tr.T($"Необработанных отчётов об ошибках: {crashReportsCount}", $"Иштетиле элек ката отчёттору: {crashReportsCount}", $"Unprocessed error reports: {crashReportsCount}", $"İşlenmemiş hata raporları: {crashReportsCount}", $"Ko'rib chiqilmagan xato hisobotlari: {crashReportsCount}");
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
                    new FilePickerFileType(Tr.T("Изображения", "Сүрөттөр", "Images", "Görseller", "Rasmlar")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } }
                }
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
    }
}
