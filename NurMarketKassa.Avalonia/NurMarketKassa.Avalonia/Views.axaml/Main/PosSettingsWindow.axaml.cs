using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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

        // 2026-11-01 (Р-6): первым полем — до создания страниц; запоминание переводов Tr.T только пока окно строится (выключается сразу
        // после конструктора и на время NavigateTo), чтобы не нагружать всю программу.
        private readonly IDisposable _trCapture = Tr.BeginCapture();
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
        private ComboBox LabelPrinterCombo => _printView.LabelPrinterCombo;
        private Button BtnFindLabelPrinters => _printView.BtnFindLabelPrinters;
        private TextBlock StatusPortText => _printView.StatusPortText;
        private Button BtnPhysicalPrint => _printView.BtnPhysicalPrint;
        private TextBox ReceiptRetryBox => _printView.ReceiptRetryBox;
        private CheckBox CashDrawerEnabledCheck => _printView.CashDrawerEnabledCheck;
        private ComboBox CashDrawerPinCombo => _printView.CashDrawerPinCombo;
        private Button BtnTestCashDrawer => _printView.BtnTestCashDrawer;
        private ComboBox ReceiptPaperWidthCombo => _printView.ReceiptPaperWidthCombo;
        private TextBox GraphicWidthBox => _printView.GraphicWidthBox;
        private TextBox ReceiptCharWidthBox => _printView.ReceiptCharWidthBox;
        // 2026-10-01: своя ширина печати для 58 и 80 мм (0 — стандарт); правки в полях держим
        // здесь, пока кассир переключает ширину ленты, и сохраняем вместе с остальными настройками.
        private int _charWidth58, _charWidth80, _dots58, _dots80;
        private int _shownPaperMm;
        // 2026-10-05, владелец: «XP-80 — невозможно настроить ширину». Вариант списка «80 мм — 42 симв.» (XP-80 и похожие).
        private const string Narrow80Tag = "80n";
        private const int Narrow80Chars = 42, Narrow80Dots = 512;
        private bool _shownNarrow80;
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
        private CheckBox AutostartCheck => _screenView.AutostartCheck;
        private CheckBox AutoTouchKeyboardCheck => _screenView.AutoTouchKeyboardCheck;
        private CheckBox ShowCatalogPhotosCheck => _screenView.ShowCatalogPhotosCheck;
        private RadioButton LanguageRussianRadio => _screenView.LanguageRussianRadio;
        private RadioButton LanguageKyrgyzRadio => _screenView.LanguageKyrgyzRadio;
        private RadioButton LanguageTurkishRadio => _screenView.LanguageTurkishRadio;
        private RadioButton LanguageUzbekRadio => _screenView.LanguageUzbekRadio;
        private RadioButton LanguageEnglishRadio => _screenView.LanguageEnglishRadio;

        /// <summary>2026-11-06: набор платных функций изменился (ключ или покупка на сайте, ServerAddons) — платные языки показываются/прячутся сразу. Приходит из любого потока.</summary>
        private void OnLanguagePackMaybeChanged() => Dispatcher.UIThread.Post(() =>
        {
            foreach (var (radio, language) in LanguageRadios())
                radio.IsVisible = LanguagePackGate.IsAvailable(language);
            _screenView.LanguagePackHint.IsVisible = !LanguagePackGate.IsUnlocked;
        });

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
        private CheckBox DebtPrepayChoiceCheck => _operationsView.DebtPrepayChoiceCheck;
        private TextBox DebtPenaltyBox => _operationsView.DebtPenaltyBox;
        private TextBox RentalPenaltyBox => _operationsView.RentalPenaltyBox;
        private TextBox OfflineDebtMaxBox => _operationsView.OfflineDebtMaxBox;
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
        // 2026-10-27, аудит В-10: страницы настроек прокручиваются, поэтому подгонка — по ширине (высота 560 вместо 820): на 1024×600
        // окно настроек сжималось дважды (0,65) и текст становился нечитаемым.
        internal void RefreshUiScale() => UiScaleHelper.Apply(UiScaleTransform, 1000, 560);

        public PosSettingsWindow(SettingsViewModel settingsVm)
        {
            SettingsVm = settingsVm;
            // 2026-11-01 (Р-6): конструктор синхронный — Post выполнится после него; запоминание переводов выключается.
            Dispatcher.UIThread.Post(() => _trCapture.Dispose(), DispatcherPriority.Send);
            DataContext = this;
            InitializeComponent();
            // 2026-10-29: окно известно опросу общих настроек (SettingsCloudSync.EditingProbe) — см. HasUnsavedEdits.
            lock (Instances)
                Instances.Add(new WeakReference<PosSettingsWindow>(this));
            // 2026-10-23 (аудит Б-9): заголовок системной рамки — цветом темы кассы.
            NurMarketKassa.AvaloniaHost.Services.WindowChrome.Apply(this);
            this.FitToScreen();
            // 2026-10-05, снимок владельца с телефона: отдельная колонка «Закрыть» справа отнимала у вкладок
            // треть ширины, а крестик закрытия на Android и так есть в заголовке окна.
            if (OperatingSystem.IsAndroid())
                SidebarCloseButton.IsVisible = false;

            _customizationView.DataContext = SettingsVm;
            _monitorView = new MonitorSettingsView(new MonitorSettingsViewModel(
                this, App.GetRequiredService<AvaloniaCustomerDisplayService>()));
            WireChildEvents();
            _operationsView.LoadBankQrSettings();

            _navButtons = new[] { NavScales, NavPrint, NavScreen, NavMonitor, NavUpdates, NavOperations, NavCustomization, NavAccount, NavEmployees, NavKeys };
            // 2026-11-01 («дизайн верхних кнопок»): стрелки прокрутки ряда вкладок — подсказка и имя для диктора на 5 языках.
            var navPrev = Tr.T("Назад", "Артка", "Previous", "Geri", "Orqaga");
            var navNext = Tr.T("Вперёд", "Алдыга", "Next", "İleri", "Oldinga");
            ToolTip.SetTip(NavScrollLeft, navPrev);
            Avalonia.Automation.AutomationProperties.SetName(NavScrollLeft, navPrev);
            ToolTip.SetTip(NavScrollRight, navNext);
            Avalonia.Automation.AutomationProperties.SetName(NavScrollRight, navNext);
            // Клавиши кассира в программе владельца не нужны: там нет ни чека, ни каталога.
            NavKeys.IsVisible = !NurMarketKassa.Services.AppMode.IsOwner;
            // 2026-10-05, владелец: «убери кастомизацию из настроек» — вкладка (обои, «стекло», размытие) скрыта;
            // код вкладки оставлен, вернуть — убрать эту строку.
            NavCustomization.IsVisible = false;
            // 2026-10-20, аудит программы владельца (п. 11): у программы владельца своя папка настроек (%APPDATA%\NurMarketOwner),
            // касса берёт свои — весы, принтер чеков и экран покупателя здесь кассу не меняют. Вкладки железа скрыты,
            // сверху подпись, окно — «Настройки»; первая вкладка — «Экран» (язык, масштаб, вид меню, «Оплата долга»).
            // В кассе всё как было.
            if (NurMarketKassa.Services.AppMode.IsOwner)
            {
                NavScales.IsVisible = false;
                NavPrint.IsVisible = false;
                NavMonitor.IsVisible = false;
                Classes.Add("owner-mode");
                OwnerHardwareHint.IsVisible = true;
                ApplyOwnerModeTexts();
                NavigateTo(2);
            }
            else
                NavigateTo(0);
            // 2026-09-29, владелец: «настройки таб меню сделай» — вкладки сверху; ряд пересчитывается
            // при смене ширины окна (масштаб, 800×600, раздел владельца) и языка (подписи другой длины).
            NavBar.SizeChanged += (_, e) =>
            {
                if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                    Dispatcher.UIThread.Post(UpdateNavColumns, DispatcherPriority.Loaded);
            };
            Tr.LanguageChanged += OnNavLanguageChanged;
            TariffGate.PacksChanged += OnLanguagePackMaybeChanged; // 2026-11-06: «Языковой пакет» купили на сайте при открытых Настройках — языки открываются без перезапуска
            Closed += (_, _) => { Tr.LanguageChanged -= OnNavLanguageChanged; TariffGate.PacksChanged -= OnLanguagePackMaybeChanged; _trCapture.Dispose(); Tr.ClearCaptured(); };
            // 2026-11-01 (Р-6, П-1 приёмки): скрытое окно (раздел владельца) не держит запомненные переводы.
            DetachedFromVisualTree += (_, _) => Tr.ClearCaptured();
            // 2026-10-27, аудит С-22: правки на вкладках «Весы / Печать / Экран» при закрытии окна без «Сохранить» терялись молча.
            // Состояние полей запоминается перед первым действием пользователя; при закрытии, если оно изменилось, — вопрос.
            AddHandler(PointerPressedEvent, (_, _) => _dirtyBaseline ??= SnapshotEditable(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyDownEvent, (_, _) => _dirtyBaseline ??= SnapshotEditable(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            Closing += OnSettingsClosing;
            // 2026-10-27 (Д/У Настроек): низкое окно — класс short (см. PosSettingsWindow.axaml, стиль SettingsHeaderCard)
            SizeChanged += (_, e) => { var low = e.NewSize.Height < 640; if (Classes.Contains("short") != low) Classes.Set("short", low); };
            // 2026-10-20 (живой аудит кассы, К17): подсказка обещала «Применяется сразу, без перезапуска кассы», а язык
            // менялся только после «Сохранить». Теперь — сразу по нажатию (и запоминается); «Сохранить» ничего не ломает.
            foreach (var (radio, language) in LanguageRadios())
            {
                radio.IsCheckedChanged += (_, _) =>
                {
                    var prefs = UserPreferences.Instance;
                    if (radio.IsChecked != true || prefs.Language == language)
                        return;
                    prefs.Language = language;
                    LocalizationManager.Apply(language);
                    prefs.SaveToDisk();
                    PosLogger.Log($"Язык кассы изменён в настройках сразу: {language}.", "SETTINGS");
                };
            }
            // 2026-09-30, владелец: «оптимизация экрана настроек к каждому экрану» — на узкой странице
            // строки в несколько колонок встают столбиком (Views/Settings/SettingsReflow.cs).
            ContentHost.SizeChanged += (_, e) =>
            {
                if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                    ReflowPage();
            };

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
            _printView.ReceiptNetPanel.SetAddress(prefs.ReceiptDevicePath); // 2026-11-01: «Сеть (LAN)» принтера чеков
            ReceiptEscRBox.Text = prefs.ReceiptEscR?.ToString() ?? "";
            ReceiptRetryBox.Text = prefs.ReceiptRetryCount.ToString();
            CashDrawerEnabledCheck.IsChecked = prefs.CashDrawerEnabled;
            SelectComboByTag(CashDrawerPinCombo, prefs.CashDrawerPin.ToString(CultureInfo.InvariantCulture));

            _charWidth58 = prefs.ReceiptCharWidth58;
            _charWidth80 = prefs.ReceiptCharWidth80;
            _dots58 = prefs.ReceiptDots58;
            _dots80 = prefs.ReceiptDots80;
            _shownPaperMm = 0;
            _shownNarrow80 = ReceiptPaperProfile.NormalizePaperWidthMm(prefs.ReceiptPaperWidthMm) >= ReceiptPaperProfile.Paper80mm
                             && _charWidth80 == Narrow80Chars && _dots80 is 0 or Narrow80Dots;
            SelectComboByTag(ReceiptPaperWidthCombo, _shownNarrow80 ? Narrow80Tag : prefs.ReceiptPaperWidthMm.ToString(CultureInfo.InvariantCulture));
            ApplyPaperWidthToUi(prefs.ReceiptPaperWidthMm);

            FullscreenCheck.IsChecked = prefs.Fullscreen;
            TrueFullscreenCheck.IsChecked = prefs.TrueFullscreen;
            LowPerformanceModeCheck.IsChecked = prefs.LowPerformanceMode;
#if NURANDROID
            AddAndroidOrientationRow(); // 2026-11-06: В4 А4-20 — ориентация экрана Android-кассы
#endif
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
            DebtPrepayChoiceCheck.IsChecked = prefs.DebtPrepaymentChooseMethod;
            DebtPenaltyBox.Text = prefs.DebtLatePenaltyText;
            RentalPenaltyBox.Text = prefs.RentalLatePenaltyPerDay > 0 ? prefs.RentalLatePenaltyPerDay.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";
            OfflineDebtMaxBox.Text = prefs.OfflineDebtMaxPerClient > 0 ? prefs.OfflineDebtMaxPerClient.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";

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
            // 2026-09-29, владелец: блок «Дополнительные весы» убран из настроек — виден, только
            // если доп. весы уже включены (чтобы у такого магазина осталась возможность их выключить).
            _scaleView.ExtraScalesCard.IsVisible = prefs.Scale2Enabled || prefs.Scale3Enabled;

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
            // 2026-11-06: В4 АЛ-3 — графический чек рисовал System.Drawing (только Windows), поэтому на Linux и Android режим «Графика» был закрыт.
            // 2026-11-06 (В18): теперь графический чек рисует Skia (GraphicReceiptSkia) — режим доступен везде; на настоящем принтере Linux/Android не проверялся.

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
            RefreshLabelPrinters();
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
            BtnFindLabelPrinters.Click += (_, _) => RefreshLabelPrinters();
            // 2026-11-01 (LAN): адрес принтера чеков из блока «Сеть» сразу попадает в поле порта; принтер этикеток — по «Использовать этот принтер»
            _printView.ReceiptNetPanel.AddressApplied += (_, _) =>
            {
                if (_printView.ReceiptNetPanel.CurrentAddress is { } netAddress && !string.Equals(ReceiptLptBox.Text, netAddress, StringComparison.OrdinalIgnoreCase))
                    ReceiptLptBox.Text = netAddress;
            };
            _printView.LabelNetPanel.UseRequested += (_, address) => UseLabelNetwork(address);
            _printView.BtnForgetLabelPrinter.Click += ForgetLabelPrinter_Click;
            LabelPrinterCombo.SelectionChanged += LabelPrinterCombo_SelectionChanged;
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
            // 2026-10-03: выбор способа предоплаты долга — сохраняется сразу.
            DebtPrepayChoiceCheck.IsCheckedChanged += (_, _) =>
            {
                UserPreferences.Instance.DebtPrepaymentChooseMethod = DebtPrepayChoiceCheck.IsChecked == true;
                UserPreferences.Instance.SaveToDisk();
            };
            // 2026-10-06, владелец: «в чеке — инфо о штрафе за просрочку» (долг и прокат). Сохраняется через секунду после ввода.
            _operationsView.DebtPenaltyLabel.Text = Tr.T("Штраф за просрочку долга — печатается в чеке продажи в долг (например: «50 сом за каждый день»). Пусто — не печатать.",
                "Карызды кечиктиргени үчүн айып — карызга сатуунун чегинде басылат (мисалы: «ар бир күн үчүн 50 сом»). Бош — басылбайт.",
                "Late debt penalty — printed on credit-sale receipts (e.g. \"50 som per day\"). Empty — not printed.",
                "Borç gecikme cezası — veresiye satış fişine yazılır (örn.: \"günlük 50 som\"). Boş — yazılmaz.",
                "Qarzni kechiktirish jarimasi — qarzga sotuv chekida chiqadi (masalan: «har kun uchun 50 so'm»). Bo'sh — chiqmaydi.");
            _operationsView.RentalPenaltyLabel.Text = Tr.T("Штраф за сутки просрочки проката, сом — подставляется в «Новый прокат» и печатается в чеке",
                "Прокатты кечиктирген ар бир сутка үчүн айып, сом — «Жаңы прокатка» коюлат жана чекте басылат",
                "Rental late penalty per day, som — filled into \"New rental\" and printed on the receipt",
                "Kiralama gecikme cezası (günlük, som) — \"Yeni kiralama\"ya girilir ve fişe yazılır",
                "Prokatni kechiktirgan har sutka uchun jarima, so'm — «Yangi prokat»ga qo'yiladi va chekda chiqadi");
            // 2026-10-29: «Макс. долг без связи» — предел долга на клиента, сом. 0 — по лимиту клиента с сервера; лимита нет нигде — касса просит подтверждение кассира.
            _operationsView.OfflineDebtMaxLabel.Text = Tr.T("Макс. долг без связи на клиента, сом — до этой суммы касса без интернета оформляет «в долг». 0 — по лимиту клиента; лимита нет — касса спросит подтверждение кассира.",
                "Байланышсыз кардарга макс. карыз, сом — ушул сумманын чегинде касса интернетсиз «карызга» жазат. 0 — кардардын лимити боюнча; лимит жок болсо — касса кассирден ырастоо сурайт.",
                "Max debt without a connection per client, som — up to this amount the till makes credit sales without the internet. 0 — by the client's limit; no limit anywhere — the till asks the cashier to confirm.",
                "Bağlantısız müşteri başına maks. borç, som — kasa internetsiz bu tutara kadar veresiye satış yapar. 0 — müşterinin limitine göre; hiçbir yerde limit yoksa kasa kasiyerden onay ister.",
                "Aloqasiz mijoz uchun maks. qarz, so'm — kassa internetsiz shu summagacha «qarzga» rasmiylashtiradi. 0 — mijoz limiti bo'yicha; limit hech qayerda yo'q bo'lsa — kassa kassirdan tasdiq so'raydi.");
            var penaltySave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            EventHandler penaltyFlush = (_, _) =>
            {
                penaltySave.Stop();
                var prefsNow = UserPreferences.Instance;
                prefsNow.DebtLatePenaltyText = (DebtPenaltyBox.Text ?? "").Trim();
                prefsNow.RentalLatePenaltyPerDay = double.TryParse((RentalPenaltyBox.Text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                    System.Globalization.CultureInfo.InvariantCulture, out var perDay) && perDay > 0 ? perDay : 0;
                prefsNow.OfflineDebtMaxPerClient = double.TryParse((OfflineDebtMaxBox.Text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                    System.Globalization.CultureInfo.InvariantCulture, out var offlineMax) && offlineMax > 0 ? offlineMax : 0;
                prefsNow.SaveToDisk();
            };
            penaltySave.Tick += penaltyFlush;
            OfflineDebtMaxBox.TextChanged += (_, _) => { penaltySave.Stop(); penaltySave.Start(); };
            DebtPenaltyBox.TextChanged += (_, _) => { penaltySave.Stop(); penaltySave.Start(); };
            RentalPenaltyBox.TextChanged += (_, _) => { penaltySave.Stop(); penaltySave.Start(); };
            // 2026-11-07 (память, Б-J1): закрыли окно, пока таймер отложенного сохранения ещё ждёт, — сохраняем сразу и таймер останавливаем (запущенный DispatcherTimer лежит в списке диспетчера и держал закрытые «Настройки»).
            Closed += (_, _) => { if (penaltySave.IsEnabled) penaltyFlush(penaltySave, EventArgs.Empty); };
            _screenView.SaveRequested += ScreenSaveRequested;
            _screenView.UiScaleChanged += (_, _) => RefreshUiScale();

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

        private void OnNavLanguageChanged()
        {
            ApplyOwnerModeTexts();
            RetranslateCodeTexts();
            Dispatcher.UIThread.Post(UpdateNavColumns, DispatcherPriority.Loaded);
        }

        /// <summary>2026-11-01 (Р-6, живая регрессия 1.17.71): подписи, заданные кодом через Tr.T один раз (вкладки Печать, Экран, Операции,
        /// Весы, принтеры и LAN), после смены языка внутри открытого окна оставались прежними. Все страницы окна обходятся, а текст, который
        /// раньше выдал Tr.T (Tr.Retranslate), заменяется на текущий язык. Поля ввода, списки и подписи из ресурсов (DynamicResource) не трогаются.</summary>
        private void RetranslateCodeTexts()
        {
            try
            {
                var roots = new Control?[] { this, _scaleView, _printView, _screenView, _monitorView, _updatesView, _operationsView, _customizationView,
                    _accountView, _employeesView, _keysView };
                var done = new HashSet<Control>();
                foreach (var root in roots)
                {
                    if (root is null)
                        continue;
                    Fix(root, done);
                    foreach (var c in root.GetLogicalDescendants().OfType<Control>())
                        Fix(c, done);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Настройки: подписи не переведены после смены языка ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }

            static void Fix(Control c, HashSet<Control> done)
            {
                if (!done.Add(c))
                    return;
                if (c is TextBlock tb && (tb.Inlines is null || tb.Inlines.Count == 0) && Tr.Retranslate(tb.Text) is { } t)
                    tb.Text = t;
                if (c is ContentControl cc && cc.Content is string s && Tr.Retranslate(s) is { } cs)
                    cc.Content = cs;
                if (c is Avalonia.Controls.Primitives.HeaderedContentControl hc && hc.Header is string h && Tr.Retranslate(h) is { } hs)
                    hc.Header = hs;
                if (c is TextBox box && box.Watermark is string w && Tr.Retranslate(w) is { } ws)
                    box.Watermark = ws;
                if (ToolTip.GetTip(c) is string tip && Tr.Retranslate(tip) is { } tipText)
                    ToolTip.SetTip(c, tipText);
                if (Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } an && Tr.Retranslate(an) is { } ans)
                    Avalonia.Automation.AutomationProperties.SetName(c, ans);
            }
        }

        /// <summary>2026-10-20, аудит программы владельца (п. 11): название окна и подпись про железо кассы — только у владельца.</summary>
        private void ApplyOwnerModeTexts()
        {
            if (!NurMarketKassa.Services.AppMode.IsOwner)
                return;
            Title = Tr.T("Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar");
            OwnerHardwareHint.Text = Tr.T(
                "Настройки железа кассы (весы, принтер, экран покупателя) меняются в самой кассе.",
                "Кассанын жабдууларынын жөндөөлөрү (тараза, принтер, сатып алуучунун экраны) кассанын өзүндө өзгөртүлөт.",
                "Till hardware settings (scales, printer, customer display) are changed in the till itself.",
                "Kasa donanımı ayarları (terazi, yazıcı, müşteri ekranı) kasanın kendisinde değiştirilir.",
                "Kassa uskunalari sozlamalari (tarozi, printer, xaridor ekrani) kassaning o'zida o'zgartiriladi.");
        }

        /// <summary>2026-11-01 («дизайн верхних кнопок»): вкладки настроек — ВСЕГДА один ровный ряд (раньше при нехватке ширины — два неровных
        /// ряда вроде 5 + 4). Помещаются — растягиваются на всю ширину; нет (кыргызский «Ыңгайлаштыруу», узбекский «Yangilanishlar», окно
        /// 800×600, масштаб 125 %) — каждой вкладке остаётся ширина самой длинной подписи, ряд прокручивается, по краям стрелки ‹ ›.
        /// Скрытые вкладки («Клавиши» у владельца) не считаются. Ширина «внутри карточки» берётся от самой карточки, а не от прокрутки, —
        /// иначе показ стрелок менял бы ширину прокрутки и расчёт «мигал».</summary>
        private void UpdateNavColumns()
        {
            var visible = _navButtons.Where(b => b.IsVisible).ToList();
            double full = (NavScroll.Parent as Grid)?.Bounds.Width ?? 0;
            if (visible.Count == 0 || full <= 0)
                return;

            double widest = 0;
            foreach (var button in visible)
            {
                button.Measure(Size.Infinity);
                widest = Math.Max(widest, button.DesiredSize.Width);
            }

            // Запас на полужирную подпись выбранной вкладки — она чуть шире обычной.
            widest += 4;
            double need = widest * visible.Count;
            bool scroll = need > full + 0.5;
            const double arrowsWidth = 64; // две стрелки по 32
            double viewport = scroll ? full - arrowsWidth : full;
            if (NavTabsGrid.Rows != 1 || NavTabsGrid.Columns != visible.Count)
            {
                NavTabsGrid.Rows = 1;
                NavTabsGrid.Columns = visible.Count;
            }
            NavTabsGrid.MinWidth = Math.Max(viewport, need);
            NavScrollLeft.IsVisible = scroll;
            NavScrollRight.IsVisible = scroll;
        }

        private void NavScrollLeft_Click(object? sender, RoutedEventArgs e) =>
            NavScroll.Offset = new Vector(Math.Max(0, NavScroll.Offset.X - NavScroll.Viewport.Width * 0.6), 0);

        private void NavScrollRight_Click(object? sender, RoutedEventArgs e) =>
            NavScroll.Offset = new Vector(Math.Min(Math.Max(0, NavScroll.Extent.Width - NavScroll.Viewport.Width), NavScroll.Offset.X + NavScroll.Viewport.Width * 0.6), 0);

        /// <summary>2026-11-01: ←/→ на вкладке переключают вкладки (фокус и страница), как у сегментов; крайняя вкладка — остаётся на месте.</summary>
        private void NavTabsGrid_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key != Avalonia.Input.Key.Left && e.Key != Avalonia.Input.Key.Right)
                return;
            var visible = _navButtons.Where(b => b.IsVisible).ToList();
            var index = visible.FindIndex(b => b.IsFocused);
            if (index < 0)
                index = visible.FindIndex(b => b.Classes.Contains("nav-active"));
            if (index < 0)
                return;
            e.Handled = true;
            var next = Math.Clamp(index + (e.Key == Avalonia.Input.Key.Right ? 1 : -1), 0, visible.Count - 1);
            if (next == index)
                return;
            visible[next].Focus();
            SidebarNav_Click(visible[next], new RoutedEventArgs());
        }

        /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): «Закрыть» внизу
        /// списка вкладок закрывала бы раздел — переход в другой раздел и так делается меню слева.</summary>
        public void AsOwnerSection()
        {
            SidebarCloseButton.IsVisible = false;
            _embeddedSection = true; // 2026-10-27 (С-22): закрывает оболочка владельца — вопрос о несохранённом не задаём
        }

        private static readonly List<WeakReference<PosSettingsWindow>> Instances = new();

        /// <summary>2026-10-29: true — окно настроек сейчас на экране (само или разделом программы владельца). Пока так, настройки компании с
        /// сервера (SettingsCloudSync.RefreshCompanyAsync) не применяются: поля страниц читаются при открытии, и «Сохранить» с устаревшим
        /// полем вернул бы на сервер старое значение поверх новой правки владельца; список сотрудников тоже не должен перерисовываться под
        /// руками. Применятся в течение 15 с после закрытия окна / ухода из раздела.</summary>
        public static bool HasUnsavedEdits()
        {
            lock (Instances)
            {
                Instances.RemoveAll(w => !w.TryGetTarget(out _));
                foreach (var w in Instances)
                    if (w.TryGetTarget(out var win) && win.IsEffectivelyVisibleForSync())
                        return true;
            }
            return false;
        }

        // Окно показано само или его содержимое встроено разделом программы владельца и сейчас на экране.
        private bool IsEffectivelyVisibleForSync() => IsVisible || (Content is Avalonia.Visual v && v.IsEffectivelyVisible);

        private string? _dirtyBaseline;
        private bool _closeConfirmed;
        private bool _embeddedSection;

        /// <summary>2026-10-27 (С-22): значения редактируемых полей страниц с кнопкой «Сохранить» (Весы, Печать, Экран).
        /// Язык (применяется сразу) не входит.</summary>
        private string SnapshotEditable()
        {
            var languageRadios = LanguageRadios().Select(r => r.Radio).ToList();
            var sb = new System.Text.StringBuilder();
            foreach (var page in new Control[] { _scaleView, _printView, _screenView })
                foreach (var c in page.GetLogicalDescendants())
                {
                    switch (c)
                    {
                        case TextBox { IsReadOnly: false } tb: sb.Append(tb.Text); break;
                        case Avalonia.Controls.Primitives.ToggleButton tg when !languageRadios.Contains(tg): sb.Append(tg.IsChecked); break;
                        case ComboBox cb: sb.Append(cb.SelectedIndex); break;
                        case Slider sl: sb.Append(sl.Value); break;
                        default: continue;
                    }
                    sb.Append((char)1);
                }
            return sb.ToString();
        }

        private void OnSettingsClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_closeConfirmed || _embeddedSection || e.CloseReason != WindowCloseReason.WindowClosing)
                return;
            if (_dirtyBaseline is null || SnapshotEditable() == _dirtyBaseline)
                return;
            e.Cancel = true;
            // вопрос — после выхода из обработчика закрытия (диалог с вложенным циклом внутри Closing не показываем)
            Dispatcher.UIThread.Post(() => _ = AskCloseWithUnsavedAsync(), DispatcherPriority.Background);
        }

        private async Task AskCloseWithUnsavedAsync()
        {
            var leave = await PosDialogs.ConfirmYesNoModalAsync(this,
                Tr.T("Есть несохранённые изменения. Закрыть окно без сохранения?",
                    "Сакталбаган өзгөртүүлөр бар. Терезени сактабай жабасызбы?",
                    "There are unsaved changes. Close the window without saving?",
                    "Kaydedilmemiş değişiklikler var. Pencere kaydetmeden kapatılsın mı?",
                    "Saqlanmagan o'zgarishlar bor. Oynani saqlamasdan yopasizmi?"),
                Tr.T("Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar"),
                confirmText: Tr.T("Закрыть без сохранения", "Сактабай жабуу", "Close without saving", "Kaydetmeden kapat", "Saqlamasdan yopish"),
                cancelText: Tr.T("Вернуться", "Кайтуу", "Go back", "Geri dön", "Qaytish"),
                danger: true).ConfigureAwait(true);
            if (!leave)
                return;
            _closeConfirmed = true;
            Close();
        }

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

        /// <summary>2026-10-27, жалоба «Нур Советник открывает не ту вкладку»: страница настроек по ключу словаря (screen, updates, operations,
        /// customization, account, employees, keys); false — страницы нет (в программе владельца железо кассы скрыто).</summary>
        public bool SelectNavTab(string tab)
        {
            var (index, button) = tab switch
            {
                "screen" => (2, NavScreen),
                "updates" => (4, NavUpdates),
                "operations" => (5, (Button?)_navButtons[5]),
                "customization" => (6, (Button?)_navButtons[6]),
                "account" => (7, (Button?)_navButtons[7]),
                "employees" => (8, (Button?)_navButtons[8]),
                "keys" => (9, (Button?)_navButtons[9]),
                _ => (-1, (Button?)null),
            };
            if (index < 0 || button is null || !button.IsVisible)
                return false;
            NavigateTo(index);
            return button.Classes.Contains("nav-active");
        }

        private Button? _pageSave;

        /// <summary>2026-10-22 (модалки, пакет 4): закреплённая «Сохранить» внизу окна — ищет на открытой странице её
        /// единственную главную кнопку «Сохранить» (btn-primary с текстом common.save), прячет её и нажимает её же:
        /// обработчик, проверки и «Сохранено» страницы — прежние. Доступность кнопки страницы повторяется внизу.</summary>
        private void BindPinnedSave()
        {
            if (_pageSave is not null)
            {
                _pageSave.PropertyChanged -= PageSave_PropertyChanged;
                _pageSave.IsVisible = true;
                _pageSave = null;
            }
            var saveText = this.TryFindResource("common.save", out var v) ? v as string : null;
            var found = saveText is null || ContentHost.Content is not Control page
                ? new List<Button>()
                : page.GetLogicalDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("btn-primary") && Equals(b.Content, saveText)).ToList();
            if (found.Count != 1)
            {
                // 2026-10-22 (аудит новой сборки, П3): страница без «Сохранить» (Операции, Аккаунт, Обновления) — внизу пометка,
                // что переключатели применяются сразу. Если страница ещё не прогрузила текст кнопки, вторая попытка (Loaded) всё исправит.
                var autoApply = found.Count == 0 && ContentHost.Content is Control;
                AutoApplyNote.Text = Tr.T("Изменения применяются сразу — кнопка «Сохранить» не нужна.",
                    "Өзгөрүүлөр дароо колдонулат — «Сактоо» баскычы талап кылынбайт.",
                    "Changes apply immediately — no Save button needed.",
                    "Değişiklikler hemen uygulanır — Kaydet düğmesi gerekmez.",
                    "O'zgarishlar darhol qo'llaniladi — «Saqlash» tugmasi shart emas.");
                AutoApplyNote.IsVisible = autoApply;
                PinnedSaveButton.IsVisible = false;
                PinnedSaveBar.IsVisible = autoApply;
                return;
            }
            AutoApplyNote.IsVisible = false;
            PinnedSaveButton.IsVisible = true;
            _pageSave = found[0];
            _pageSave.IsVisible = false;
            _pageSave.PropertyChanged += PageSave_PropertyChanged;
            PinnedSaveButton.IsEnabled = _pageSave.IsEnabled;
            PinnedSaveBar.IsVisible = true;
        }

        private void PageSave_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == IsEnabledProperty && sender is Button b)
                PinnedSaveButton.IsEnabled = b.IsEnabled;
        }

        private void PinnedSave_Click(object? sender, RoutedEventArgs e) =>
            _pageSave?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        /// <summary>Страница настроек под ширину окна: узко — колонки столбиком (SettingsReflow).</summary>
        private void ReflowPage() =>
            NurMarketKassa.AvaloniaHost.Views.Settings.SettingsReflow.Apply(ContentHost.Content as Control, ContentHost.Bounds.Width);

        private void NavigateTo(int index)
        {
            using var trCapture = Tr.BeginCapture(); // 2026-11-01 (Р-6): страницы, строящиеся при первом показе, тоже запоминаются — на время метода
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

            Dispatcher.UIThread.Post(ReflowPage, DispatcherPriority.Loaded);
            BindPinnedSave();
            // 2026-10-22 (модалки, пакет 4): повтор после загрузки страницы — если текст её «Сохранить» (DynamicResource)
            // ещё не подставился при первом поиске. Повторный вызов безопасен: та же кнопка находится снова.
            Dispatcher.UIThread.Post(BindPinnedSave, DispatcherPriority.Loaded);

            for (int i = 0; i < _navButtons.Length; i++)
            {
                if (i == index)
                    _navButtons[i].Classes.Add("nav-active");
                else
                    _navButtons[i].Classes.Remove("nav-active");
            }
            // 2026-11-01: ряд вкладок может прокручиваться — выбранная вкладка не должна уйти за край.
            if (index >= 0 && index < _navButtons.Length)
            {
                var activeTab = _navButtons[index];
                Dispatcher.UIThread.Post(() => activeTab.BringIntoView(), DispatcherPriority.Loaded);
            }
        }

        private void ReceiptLptBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (NetworkPrinterAddress.IsNetworkPort(ReceiptLptBox.Text))
                _printView.ReceiptNetPanel.SetAddress(ReceiptLptBox.Text);
            RefreshPrinterPortStatus();
            UpdateLabelPrinterInfo();
        }

        // 2026-11-01 (LAN): принтер этикеток по сети — добавить в список и выбрать (адрес запоминается при «Сохранить»).
        private void UseLabelNetwork(string address)
        {
            var canon = NetworkPrinterAddress.Canonical(address);
            var list = (LabelPrinterCombo.ItemsSource as IEnumerable<DiscoveredPrinter>)?.ToList() ?? new List<DiscoveredPrinter>();
            var item = list.FirstOrDefault(p => LabelPrinterSelector.SamePrinter(p.DevicePath, canon));
            if (item is null)
            {
                item = NetworkPrinterAddress.ToDiscovered(canon);
                list.Add(item);
            }
            if (!list.Any(x => x.DevicePath.Length == 0))
                list.Insert(0, NoLabelPrinter);
            _labelPrinterInit = true;
            LabelPrinterCombo.ItemsSource = list;
            LabelPrinterCombo.SelectedItem = item;
            _labelPrinterInit = false;
            _labelPrinterChoice = canon;
            _labelLiveText = "";
            UpdateLabelPrinterInfo();
            RefreshLabelLiveStatus();
        }

        // 2026-11-01 (просьба владельца: два принтера разной модели одновременно): принтер этикеток и штрихкодов — отдельный выбор,
        // не связанный с принтером чеков. Список — без PDF/XPS/OneNote/факса, камеры и Bluetooth; записывается при «Сохранить».
        private bool _labelPrinterInit;
        private string? _labelPrinterChoice;
        // 2026-11-01 (П-7): живой статус принтера этикеток — при открытии и раз в 10 с, пока окно открыто (в фоне, не во время печати)
        private string _labelLiveText = "";
        private Avalonia.Threading.DispatcherTimer? _labelLiveTimer;
        private bool _labelNoAnswer;

        /// <summary>2026-11-01 (Р-3): пункт «— не выбран —» (пустой путь) — выбранный принтер этикеток можно сбросить; «Забыть принтер» выбирает его же.</summary>
        private static DiscoveredPrinter NoLabelPrinter => new(
            Tr.T("— не выбран —", "— тандалган эмес —", "— not selected —", "— seçilmedi —", "— tanlanmagan —"), "");

        private void ForgetLabelPrinter_Click(object? sender, RoutedEventArgs e)
        {
            if (LabelPrinterCombo.ItemsSource is IEnumerable<DiscoveredPrinter> items && items.FirstOrDefault(p => p.DevicePath.Length == 0) is { } none)
                LabelPrinterCombo.SelectedItem = none;
        }

        private async void RefreshLabelLiveStatus()
        {
            if (LabelPrinterCombo.SelectedItem is not DiscoveredPrinter printer || printer.DevicePath.Length == 0)
            {
                _labelLiveText = "";
                _labelNoAnswer = false;
                return;
            }
            var path = printer.DevicePath;
            var status = await Task.Run(() => LabelPrinterSelector.CheckLive(path));
            if (LabelPrinterCombo.SelectedItem is not DiscoveredPrinter now || now.DevicePath != path)
                return;
            _labelLiveText = LabelPrinterSelector.LiveText(status);
            _labelNoAnswer = status == LabelPrinterSelector.LiveStatus.NoAnswer; // Р-3: выбор не сбрасывается молча — только предупреждение
            UpdateLabelPrinterInfo();
        }

        private void StartLabelLiveTimer()
        {
            if (_labelLiveTimer != null)
                return;
            _labelLiveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _labelLiveTimer.Tick += (_, _) => RefreshLabelLiveStatus();
            _labelLiveTimer.Start();
            Closed += (_, _) => _labelLiveTimer?.Stop();
        }

        private async void RefreshLabelPrinters()
        {
            IReadOnlyList<DiscoveredPrinter> list;
            try
            {
                list = await Task.Run(LabelPrinterSelector.GetChoices);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Label printer list failed: {ex.GetType().Name}", "WARNING");
                list = Array.Empty<DiscoveredPrinter>();
            }
            var keep = _labelPrinterChoice ?? UserPreferences.Instance.LabelPrinterDevicePath;
            var withNone = new List<DiscoveredPrinter> { NoLabelPrinter };
            withNone.AddRange(list);
            _labelPrinterInit = true;
            LabelPrinterCombo.ItemsSource = withNone;
            // пусто — «— не выбран —»; сохранённый, но не найденный принтер остаётся невыбранным в списке с предупреждением (не сбрасывается молча)
            LabelPrinterCombo.SelectedItem = string.IsNullOrWhiteSpace(keep)
                ? withNone[0]
                : withNone.FirstOrDefault(p => p.DevicePath.Length > 0 && LabelPrinterSelector.SamePrinter(p.DevicePath, keep));
            _labelPrinterInit = false;
            if (LabelPrinterCombo.SelectedItem is DiscoveredPrinter netSel && NetworkPrinterAddress.IsNetworkPort(netSel.DevicePath))
                _printView.LabelNetPanel.SetAddress(netSel.DevicePath);
            _labelLiveText = "";
            UpdateLabelPrinterInfo();
            RefreshLabelLiveStatus();
            StartLabelLiveTimer();
        }

        private void LabelPrinterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_labelPrinterInit)
                return;
            if (LabelPrinterCombo.SelectedItem is DiscoveredPrinter p)
            {
                _labelPrinterChoice = p.DevicePath; // "" — «не выбран»
                _labelNoAnswer = false;
                if (NetworkPrinterAddress.IsNetworkPort(p.DevicePath))
                    _printView.LabelNetPanel.SetAddress(p.DevicePath);
            }
            _labelLiveText = "";
            UpdateLabelPrinterInfo();
            RefreshLabelLiveStatus();
        }

        /// <summary>Подписи под выбором: что за принтер этикеток, чем печатает, и предупреждение, если это тот же принтер, что для чеков.</summary>
        private void UpdateLabelPrinterInfo()
        {
            if (_printView.LabelPrinterKindText is null)
                return;
            var receiptPath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
            var receiptName = (DiscoveredPrintersCombo.ItemsSource as IEnumerable<DiscoveredPrinter>)
                ?.FirstOrDefault(p => LabelPrinterSelector.SamePrinter(p.DevicePath, receiptPath))?.Label ?? receiptPath;
            var receiptKind = LabelPrinterSelector.DescribeKind(receiptPath);
            _printView.ReceiptPrinterSummaryText.Text = Tr.T(
                $"Принтер чеков: {receiptName} — {receiptKind} (меняется ниже, в блоке «Оборудование»).",
                $"Чек принтери: {receiptName} — {receiptKind} (төмөндө, «Жабдуулар» блогунда өзгөртүлөт).",
                $"Receipt printer: {receiptName} — {receiptKind} (change it below, in the “Equipment” block).",
                $"Fiş yazıcısı: {receiptName} — {receiptKind} (aşağıdaki «Donanım» bölümünden değiştirilir).",
                $"Chek printeri: {receiptName} — {receiptKind} (pastda, «Uskunalar» blokida o'zgartiriladi).");

            var selected = LabelPrinterCombo.SelectedItem as DiscoveredPrinter;
            if (selected is { DevicePath.Length: 0 })
                selected = null; // «— не выбран —»
            var saved = _labelPrinterChoice ?? UserPreferences.Instance.LabelPrinterDevicePath;
            if (selected is null)
            {
                _printView.LabelPrinterKindText.Text = "";
                var notFound = !string.IsNullOrWhiteSpace(saved);
                _printView.LabelPrinterWarnText.Text = LabelPrinterSelector.HintText(notFound ? LabelPrinterState.NotFound : LabelPrinterState.NotSelected);
                _printView.LabelPrinterWarnText.IsVisible = true;
                return;
            }
            var kind = LabelPrinterSelector.DescribeKind(selected.DevicePath);
            var how = LabelPrinterSelector.DescribeLanguage(selected.DevicePath);
            _printView.LabelPrinterKindText.Text = Tr.T(
                $"Подключение: {kind}. Как печатает: {how}." + (_labelLiveText.Length > 0 ? $" Состояние: {_labelLiveText}." : ""),
                $"Туташуу: {kind}. Кандай басат: {how}." + (_labelLiveText.Length > 0 ? $" Абалы: {_labelLiveText}." : ""),
                $"Connection: {kind}. Printing method: {how}." + (_labelLiveText.Length > 0 ? $" State: {_labelLiveText}." : ""),
                $"Bağlantı: {kind}. Yazdırma yöntemi: {how}." + (_labelLiveText.Length > 0 ? $" Durum: {_labelLiveText}." : ""),
                $"Ulanish: {kind}. Qanday chop etadi: {how}." + (_labelLiveText.Length > 0 ? $" Holati: {_labelLiveText}." : ""));
            var same = LabelPrinterSelector.IsSameAsReceiptPrinter(selected.DevicePath, receiptPath);
            var notAnswering = _labelNoAnswer;
            // 2026-11-01 (С-8): очередь «Generic / Text Only» печатает картинку пустой или мусором — предупреждаем.
            var textOnly = LabelPrinterSelector.LooksLikeTextOnlyQueue(selected.Label);
            _printView.LabelPrinterWarnText.IsVisible = same || textOnly || notAnswering;
            if (textOnly && !same)
                _printView.LabelPrinterWarnText.Text = LabelPrinterSelector.TextOnlyWarning;
            if (notAnswering && !same && !textOnly)
                _printView.LabelPrinterWarnText.Text = Tr.T(
                    "Принтер не отвечает — проверьте питание, кабель или сеть. Выбор не сброшен; чтобы убрать принтер, нажмите «Забыть принтер».",
                    "Принтер жооп бербейт — кубатты, кабелди же тармакты текшериңиз. Тандоо өзгөргөн жок; принтерди алып салуу үчүн «Принтерди унутуу» басыңыз.",
                    "The printer is not responding — check power, cable or network. The choice was not reset; to remove the printer press “Forget printer”.",
                    "Yazıcı yanıt vermiyor — güç, kablo veya ağı kontrol edin. Seçim sıfırlanmadı; yazıcıyı kaldırmak için «Yazıcıyı unut»a basın.",
                    "Printer javob bermayapti — quvvat, kabel yoki tarmoqni tekshiring. Tanlov o'chirilmadi; printerni olib tashlash uchun «Printerni unutish» ni bosing.");
            if (same)
                _printView.LabelPrinterWarnText.Text = Tr.T(
                    "Для этикеток выбран тот же принтер, что и для чеков: задания пойдут в один порт по очереди, одновременной работы двух принтеров не будет. Если у вас два принтера — выберите для этикеток второй.",
                    "Этикеткага чек принтери эле тандалды: тапшырмалар бир портко кезек менен барат, эки принтер бир убакта иштебейт. Эки принтериңиз болсо — этикеткага экинчисин тандаңыз.",
                    "The same printer is chosen for labels and receipts: jobs go to one port one after another, so two printers will not work at the same time. If you have two printers, pick the second one for labels.",
                    "Etiket için fiş yazıcısı ile aynı yazıcı seçildi: işler tek porta sırayla gider, iki yazıcı aynı anda çalışmaz. İki yazıcınız varsa etiket için ikincisini seçin.",
                    "Yorliq uchun chek printeri bilan bir xil printer tanlandi: vazifalar bitta portga navbat bilan boradi, ikki printer bir vaqtda ishlamaydi. Ikkita printeringiz bo'lsa — yorliq uchun ikkinchisini tanlang.");
        }

        /// <summary>Сканирует установленные принтеры Windows + LPT/COM-порты и заполняет список выбора.</summary>
        private void RefreshDiscoveredPrinters()
        {
            var found = PrinterDiscoveryService.Discover();
            DiscoveredPrintersCombo.ItemsSource = found;
            DiscoveredPrintersCombo.SelectedItem = found.FirstOrDefault(p =>
                string.Equals(p.DevicePath, ReceiptLptBox.Text, StringComparison.OrdinalIgnoreCase)
                || (NetworkPrinterAddress.IsNetworkPort(ReceiptLptBox.Text) && LabelPrinterSelector.SamePrinter(p.DevicePath, ReceiptLptBox.Text)));
        }

        private void FindPrinters_Click(object? sender, RoutedEventArgs e) => RefreshDiscoveredPrinters();

        private void DiscoveredPrintersCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DiscoveredPrintersCombo.SelectedItem is DiscoveredPrinter printer)
                ReceiptLptBox.Text = printer.DevicePath;
        }

        private void ReceiptPaperWidthCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ReceiptPaperWidthCombo?.SelectedItem is not ComboBoxItem item)
                return;
            var tag = item.Tag?.ToString();
            var narrow = tag == Narrow80Tag;
            if (narrow || int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm))
            {
                mm = narrow ? ReceiptPaperProfile.Paper80mm : int.Parse(tag!, CultureInfo.InvariantCulture);
                // Переход на «80 мм — 42» или обратно на обычные 80 мм: ширину ставит сам вариант (поля можно поправить).
                if (narrow != _shownNarrow80 && _shownPaperMm != 0)
                {
                    StashPrintWidthFromUi();
                    if (narrow)
                    {
                        _charWidth80 = Narrow80Chars;
                        _dots80 = Narrow80Dots;
                    }
                    else if (mm >= ReceiptPaperProfile.Paper80mm)
                    {
                        _charWidth80 = 0;
                        _dots80 = 0;
                    }
                    _shownPaperMm = 0;
                }
                _shownNarrow80 = narrow;
                ApplyPaperWidthToUi(mm);
            }
        }

        private void ApplyPaperWidthToUi(int paperWidthMm)
        {
            var normalized = ReceiptPaperProfile.NormalizePaperWidthMm(paperWidthMm);
            StashPrintWidthFromUi();
            _shownPaperMm = normalized;
            var is80 = normalized >= ReceiptPaperProfile.Paper80mm;
            var chars = is80 ? _charWidth80 : _charWidth58;
            var dots = is80 ? _dots80 : _dots58;
            if (GraphicWidthBox != null)
                GraphicWidthBox.Text = (dots > 0 ? dots : ReceiptPaperProfile.GetDefaultRasterWidthPixels(normalized)).ToString(CultureInfo.InvariantCulture);
            if (ReceiptCharWidthBox != null)
                ReceiptCharWidthBox.Text = (chars > 0 ? chars : ReceiptPaperProfile.GetDefaultCharWidth(normalized)).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>2026-10-01: запомнить поля «Символов в строке» и «Графика (точки)» для ширины ленты,
        /// которая сейчас показана. Стандартное или недопустимое значение — 0 (стандарт).</summary>
        private void StashPrintWidthFromUi()
        {
            if (_shownPaperMm == 0 || GraphicWidthBox == null || ReceiptCharWidthBox == null)
                return;
            var is80 = _shownPaperMm >= ReceiptPaperProfile.Paper80mm;
            int chars = 0, dots = 0;
            if (int.TryParse(ReceiptCharWidthBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)
                && c is >= ReceiptPaperProfile.MinCharWidth and <= ReceiptPaperProfile.MaxCharWidth
                && c != ReceiptPaperProfile.GetDefaultCharWidth(_shownPaperMm))
                chars = c;
            if (int.TryParse(GraphicWidthBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
                && d is >= ReceiptPaperProfile.MinDots and <= ReceiptPaperProfile.MaxDots
                && d != ReceiptPaperProfile.GetDefaultRasterWidthPixels(_shownPaperMm))
                dots = d;
            if (is80) { _charWidth80 = chars; _dots80 = dots; }
            else { _charWidth58 = chars; _dots58 = dots; }
        }

        /// <summary>2026-10-01: перенести свою ширину печати в настройки (сразу действует на печать).</summary>
        private void ApplyPrintWidthToPrefs(UserPreferences prefs)
        {
            StashPrintWidthFromUi();
            prefs.ReceiptCharWidth58 = _charWidth58;
            prefs.ReceiptCharWidth80 = _charWidth80;
            prefs.ReceiptDots58 = _dots58;
            prefs.ReceiptDots80 = _dots80;
        }

        private static int ReadPaperWidthMmFromUi(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: Narrow80Tag })
                return ReceiptPaperProfile.Paper80mm;
            if (combo.SelectedItem is ComboBoxItem item
                && int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm))
            {
                return ReceiptPaperProfile.NormalizePaperWidthMm(mm);
            }

            return ReceiptPaperProfile.Paper58mm;
        }

        private int _netStatusVersion;

        /// <summary>2026-11-01 (LAN): проверка подключения к сетевому принтеру чеков — с задержкой после ввода и в фоне (таймаут 2 с), интерфейс не ждёт.</summary>
        private async Task RefreshNetworkPortStatusAsync(string port)
        {
            var version = ++_netStatusVersion;
            StatusPortText.Text = Tr.T("… проверяю сеть", "… тармакты текшерип жатам", "… checking the network", "… ağ kontrol ediliyor", "… tarmoq tekshirilmoqda");
            StatusPortText.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            await Task.Delay(600);
            if (version != _netStatusVersion)
                return;
            var probe = await Task.Run(() => PrinterPortService.ProbePort(port));
            if (version != _netStatusVersion)
                return;
            StatusPortText.Text = probe.Message;
            StatusPortText.Foreground = probe.IsAvailable
                ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        }

        private void RefreshPrinterPortStatus()
        {
            if (StatusPortText == null)
                return;

            if (NetworkPrinterAddress.IsNetworkPort(ReceiptLptBox.Text))
            {
                _ = RefreshNetworkPortStatusAsync(ReceiptLptBox.Text!);
                return;
            }
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

            // 2026-11-01 (LAN): у сетевого принтера отдельной пробы на потоке интерфейса нет — недоступность покажет сама печать (таймаут 3 с, в фоне)
            var probe = NetworkPrinterAddress.IsNetworkPort(devicePath) ? new PrinterPortService.PortProbeResult(true, "", "tcp") : PrinterPortService.ProbePort(devicePath);
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
                    var bytes = ReceiptPrintService.GenerateGraphicTestBytes(settings, storeName);
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
                    // 2026-10-01: пробная печать — с шириной из полей, ещё до сохранения.
                    var charWidth = ReceiptPaperProfile.GetCharWidth(ReadPaperWidthMmFromUi(ReceiptPaperWidthCombo));
                    if (int.TryParse(ReceiptCharWidthBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uiChars)
                        && uiChars is >= ReceiptPaperProfile.MinCharWidth and <= ReceiptPaperProfile.MaxCharWidth)
                        charWidth = uiChars;
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

        /// <summary>2026-10-05: процент начисления бонусов — в настройку магазина на сервере (менять может владелец или администратор).</summary>
        private async Task SaveLoyaltyPercentToServerAsync(double percent)
        {
            try
            {
                await ServerLoyalty.SavePercentAsync(percent).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Бонусы: процент не сохранён на сервере ({ex.Message}).", "WARNING");
                PosMessageBox.Show(this,
                    Tr.T("Процент бонусов сохранён на этой кассе, но не в настройке магазина на сервере: ",
                        "Бонус пайызы ушул кассада сакталды, бирок сервердеги дүкөн жөндөөсүндө эмес: ",
                        "The bonus percentage was saved on this till but not in the shop settings on the server: ",
                        "Bonus yüzdesi bu kasada kaydedildi ama sunucudaki mağaza ayarlarında değil: ",
                        "Bonus foizi shu kassada saqlandi, lekin serverdagi do'kon sozlamasida emas: ") + ex.Message,
                    Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
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

                UpdateStatusText.Text = OperatingSystem.IsAndroid()
                    // 2026-10-05: на Android обновление ставит сам Android — окно установки (AndroidApkUpdateService).
                    ? Tr.T("Обновление скачано. Сейчас откроется установка — нажмите «Установить».", "Жаңыртуу жүктөлдү. Азыр орнотуу ачылат — «Орнотуу» басыңыз.",
                        "Update downloaded. The installer will open now — tap “Install”.", "Güncelleme indirildi. Şimdi yükleyici açılacak — «Yükle»ye dokunun.",
                        "Yangilanish yuklab olindi. Hozir o'rnatish ochiladi — «O'rnatish»ni bosing.")
                    : Tr.T("Обновление скачано. Касса сейчас перезапустится…", "Жаңыртуу жүктөлдү. Касса азыр кайра ачылат…", "Update downloaded. The till will now restart…", "Güncelleme indirildi. Kasa şimdi yeniden başlatılacak…", "Yangilanish yuklab olindi. Kassa hozir qayta ishga tushadi…");
                await Task.Delay(1200).ConfigureAwait(true);

                // Не возвращает управление — Velopack завершает процесс изнутри. На Android — возвращает: окно
                // установки открыл Android, кнопки снова доступны.
                updateService.ApplyUpdateAndRestart();
                if (OperatingSystem.IsAndroid())
                {
                    UpdateProgressBar.IsVisible = false;
                    CheckUpdateButton.IsEnabled = true;
                    UpdateNowButton.IsEnabled = true;
                    ShowVersionsButton.IsEnabled = true;
                }
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
                MessageBoxImage.Warning,
                // 2026-10-22 (модалки, пакет 4): глагол «Установить N» вместо «Продолжить».
                MessageBoxResult.OK,
                Tr.T($"Установить {version.Version}", $"{version.Version} орнотуу", $"Install {version.Version}", $"{version.Version} kur", $"{version.Version} o'rnatish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish")) == MessageBoxResult.Yes;
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

            // 2026-10-27, аудит С-22: мусор в «скорости» весов/табло молча превращался в 9600/2400 — теперь поле подсвечивается
            // красным, настройки не сохраняются (проверка до любых присваиваний).
            if (!TryReadBaud(ScaleBaudBox, 9600, out var baud)
                || !TryReadBaud(PoleDisplayBaudBox, 2400, out var poleDisplayBaud)
                || !TryReadBaud(_scaleView.Scale2BaudBox, 9600, out _)
                || !TryReadBaud(_scaleView.Scale3BaudBox, 9600, out _))
                return false;

            prefs.ScaleEnabled = ScaleEnabledCheck.IsChecked == true;
            prefs.ScaleComPort = GetSelectedScaleComPort();
            prefs.ScaleBaudRate = baud;
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
            prefs.PoleDisplayBaudRate = poleDisplayBaud;
            prefs.PoleDisplayProtocol = PoleDisplayService.NormalizeProtocol(
                (PoleDisplayProtocolCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString());

            prefs.SaveToDisk();

            // Recreate the singleton reader so the new COM settings are used immediately.
            App.GetRequiredService<NurMarketKassa.Services.Hardware.IWeightScaleService>().Start();
            NurMarketKassa.Services.Hardware.PoleDisplayService.Instance.Start();
            _scaleView.SaveStatusText.Text = Tr.T("Настройки весов сохранены.", "Тараза жөндөөлөрү сакталды.", "Scale settings saved.", "Tartı ayarları kaydedildi.", "Tarozi sozlamalari saqlandi.");
            RefreshScalePortStatus();
            _dirtyBaseline = null; // 2026-10-27 (С-22): сохранено — следующая правка снова считается несохранённой
            return true;
        }

        private bool SavePrintSettings()
        {
            var prefs = UserPreferences.Instance;

            var receiptWasEnabled = prefs.ReceiptEnabled;
            prefs.ReceiptEnabled = ReceiptEnabledCheck.IsChecked == true;
            // 2026-09-29: печать чека включили здесь заново — окно оплаты снова предлагает печатать,
            // даже если раньше кассир выключил там «Напечатать чек» (UserPreferences.CheckoutPrintReceipt).
            if (!receiptWasEnabled && prefs.ReceiptEnabled)
                prefs.CheckoutPrintReceipt = null;
            prefs.ReceiptDevicePath = HardwarePortHelper.NormalizeLptPort(ReceiptLptBox.Text);
            // 2026-11-01 (LAN): сетевой адрес — в едином виде TCP:ХОСТ:ПОРТ; неверный адрес не сохраняется (прежний остаётся)
            if (NetworkPrinterAddress.IsNetworkPort(prefs.ReceiptDevicePath))
            {
                if (NetworkPrinterAddress.TryParse(prefs.ReceiptDevicePath, out _, out _, out var netError))
                {
                    prefs.ReceiptDevicePath = NetworkPrinterAddress.Canonical(prefs.ReceiptDevicePath);
                    NetworkPrinterAddress.Remember(prefs.ReceiptDevicePath);
                }
                else
                {
                    _printView.SaveStatusText.Text = Tr.T("Настройки не сохранены.", "Жөндөөлөр сакталган жок.", "Settings not saved.", "Ayarlar kaydedilmedi.", "Sozlamalar saqlanmadi.");
                    PosMessageBox.Show(netError ?? "", Tr.T("Настройки печати", "Басып чыгаруу жөндөөлөрү", "Print settings", "Yazdırma ayarları", "Chop etish sozlamalari"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
                    return false;
                }
            }
            // 2026-11-01: принтер этикеток и штрихкодов — отдельная настройка (виртуальные PDF/XPS/OneNote/факс не запоминаются).
            if (LabelPrinterCombo.SelectedItem is DiscoveredPrinter labelPrinter && labelPrinter.DevicePath.Length == 0)
                prefs.LabelPrinterDevicePath = null; // «— не выбран —» / «Забыть принтер»: печать этикеток без принтера покажет подсказку «Настройки → Печать»
            else if (LabelPrinterCombo.SelectedItem is DiscoveredPrinter labelPrinter2 && !labelPrinter2.IsVirtual)
            {
                prefs.LabelPrinterDevicePath = labelPrinter2.DevicePath;
                if (NetworkPrinterAddress.IsNetworkPort(labelPrinter2.DevicePath))
                    NetworkPrinterAddress.Remember(labelPrinter2.DevicePath);
            }
            // Процент начисления бонусов. Пустое или нечисловое значение оставляет прежнее, а не
            // обнуляет программу молча; диапазон тот же, что при чтении файла настроек (0..100).
            if (double.TryParse(
                    (_operationsView.LoyaltyEarnPercentBox.Text ?? "").Trim().Replace(',', '.'),
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                    CultureInfo.InvariantCulture,
                    out var loyaltyPercent))
            {
                var newPercent = Math.Clamp(loyaltyPercent, 0, 100);
                // 2026-10-05, запрос NurCRM: «процент начисления в настройку магазина» — общий для всех касс и приложения NurCRM.
                if (Math.Abs(newPercent - ServerLoyalty.EarnPercent) > 0.001)
                    _ = SaveLoyaltyPercentToServerAsync(newPercent);
                prefs.LoyaltyEarnPercent = newPercent;
            }

            prefs.CashDrawerEnabled = CashDrawerEnabledCheck.IsChecked == true;
            prefs.CashDrawerPin =
                CashDrawerPinCombo.SelectedItem is ComboBoxItem pinItem &&
                int.TryParse(pinItem.Tag?.ToString(), out int drawerPin)
                    ? drawerPin
                    : 0;
            prefs.ReceiptPaperWidthMm = ReadPaperWidthMmFromUi(ReceiptPaperWidthCombo);
            ApplyPrintWidthToPrefs(prefs);
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
            _dirtyBaseline = null; // 2026-10-27 (С-22)
            return true;
        }

        private bool SaveScreenSettings()
        {
            var prefs = UserPreferences.Instance;

            var lowPerfChanged = prefs.LowPerformanceMode != (LowPerformanceModeCheck.IsChecked == true);
            prefs.LowPerformanceMode = LowPerformanceModeCheck.IsChecked == true;

            // 2026-10-27, аудит С-21: режим окна применяется при запуске кассы — запоминаем, менялся ли он, и говорим об этом.
            var fullscreenChanged = prefs.Fullscreen != (FullscreenCheck.IsChecked == true)
                || prefs.TrueFullscreen != (TrueFullscreenCheck.IsChecked == true);
            prefs.Fullscreen = FullscreenCheck.IsChecked == true;
            prefs.TrueFullscreen = TrueFullscreenCheck.IsChecked == true;
            prefs.Autostart = AutostartCheck.IsChecked == true;
            prefs.AutoShowTouchKeyboard = AutoTouchKeyboardCheck.IsChecked == true;
            prefs.ShowCatalogPhotos = ShowCatalogPhotosCheck.IsChecked == true;
            foreach (var product in CatalogCacheService.Products)
                product.RefreshPhotoVisibility();

            prefs.UiScalePercent = UiScaleSlider.Value;
            App.GetRequiredService<MainWindowHostBridge>().Window?.RefreshUiScale();
            UiScaleHelper.NotifyPreferenceChanged(); // 2026-11-01: и программа владельца
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
                // 2026-11-02 (приёмка 1.17.73, п. 1): нужно перезапустить смену/кассу — окном, не гаснущей плашкой.
                App.GetRequiredService<IUserPrompts>()
                    .ShowWarning(Tr.T(
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
            _dirtyBaseline = null; // 2026-10-27 (С-22)
            if (fullscreenChanged)
                _screenView.SaveStatusText.Text += " " + Tr.T(
                    "Полноэкранный режим применится после перезапуска кассы.",
                    "Толук экран режими кассаны кайра иштеткенден кийин колдонулат.",
                    "Full-screen mode will apply after the till is restarted.",
                    "Tam ekran modu kasa yeniden başlatıldıktan sonra uygulanır.",
                    "To'liq ekran rejimi kassa qayta ishga tushirilgandan keyin qo'llanadi.");
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

                PlatformLauncher.OpenFile(tempPdfPath); // 2026-11-06: В4 А5-05 — общий запуск файла
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
            // 2026-10-01: пробная печать — с шириной из поля «Графика (точки)», ещё до сохранения.
            if (int.TryParse(GraphicWidthBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uiDots)
                && uiDots is >= ReceiptPaperProfile.MinDots and <= ReceiptPaperProfile.MaxDots)
                paperWidth = uiDots / 8 * 8;

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

        /// <summary>2026-10-27 (С-22): скорость порта — положительное целое. Пусто — значение по умолчанию (подставляется в поле);
        /// мусор — красная рамка, подсказка в строке состояния, false.</summary>
        private bool TryReadBaud(TextBox box, int defaultBaud, out int baud)
        {
            box.ClearValue(TextBox.BorderBrushProperty);
            var text = box.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                baud = defaultBaud;
                box.Text = defaultBaud.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out baud) && baud is > 0 and <= 4_000_000)
                return true;

            box.BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DC2626"));
            void Reset(object? s, TextChangedEventArgs e) { box.ClearValue(TextBox.BorderBrushProperty); box.TextChanged -= Reset; }
            box.TextChanged += Reset;
            _scaleView.SaveStatusText.Text = Tr.T("Настройки не сохранены: скорость порта — целое число, например 9600.",
                "Жөндөөлөр сакталган жок: порттун ылдамдыгы — бүтүн сан, мисалы 9600.",
                "Settings not saved: the port speed must be a whole number, for example 9600.",
                "Ayarlar kaydedilmedi: port hızı tam sayı olmalı, örneğin 9600.",
                "Sozlamalar saqlanmadi: port tezligi butun son bo'lishi kerak, masalan 9600.");
            box.Focus();
            return false;
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
            return files.Count > 0 ? await NurMarketKassa.AvaloniaHost.Services.ImportFileBridge.PermanentPathAsync(files[0]).ConfigureAwait(true) : null; // 2026-11-05 (А4-06): QR банка на Android — копия в папку данных
        }
    }
}
