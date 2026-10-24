using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// Диалог оплаты Avalonia-кассы: выбор способа оплаты, скидка и подтверждение суммы.
/// </summary>
public partial class CheckoutDialog : Window
{
    private readonly CheckoutViewModel _viewModel;
    private readonly IDialogService _dialogService;

    public CheckoutDialog() : this(
        new CheckoutViewModel(new CartTotalsCalculator.CartTotals(), "", ""),
        ResolveService<IDialogService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public CheckoutDialog(CheckoutViewModel viewModel, IDialogService dialogService)
    {
        _viewModel = viewModel;
        _dialogService = dialogService;
        // Блок «Консультант» (сфера «Одежда»): сотрудники и их процент — с сервера, с кэшем.
        _viewModel.ConfigureConsultants(ConsultantDirectory.ListAsync, ConsultantDirectory.DefaultPercentAsync);
        InitializeComponent();
        DataContext = _viewModel;
        AttachCloseHandler();
        this.ClampToScreenHeight();
        // 2026-10-21 (пакет 6, п. 27): строка «Консультант: …» над кнопками — всегда видна, блок выбора — в прокрутке.
        UpdateConsultantSummary();
        // 2026-10-22 (аудит новой сборки, П4): строка «Консультант: …» внизу — только если блок консультанта вне экрана.
        ContentScroll.ScrollChanged += (_, _) => UpdateConsultantSummaryVisibility();
        ContentScroll.LayoutUpdated += (_, _) => UpdateConsultantSummaryVisibility();
        UpdateErrorVisibility();
        // 2026-10-24 (повторная приёмка, Б-1): список клиентов грузится не в потоке окна — событие приходит из чужого потока,
        // а обновление интерфейса из него падало («Call from invalid thread»). Обработчик выполняется в потоке окна.
        void OnViewModelPropertyChanged(string? propertyName)
        {
            UpdateErrorVisibility();
            if (propertyName is nameof(CheckoutViewModel.SelectedConsultant) or nameof(CheckoutViewModel.IsConsultantEnabled))
                UpdateConsultantSummary();
            // 2026-10-22 (аудит 1.17.59, П-В, Б8): клиент выбран в режиме «В долг» — прокручиваем к предоплате и сроку.
            if (propertyName is nameof(CheckoutViewModel.HasSelectedClient) or nameof(CheckoutViewModel.IsDebtMode)
                && _viewModel.IsDebtMode && _viewModel.HasSelectedClient)
                Dispatcher.UIThread.Post(() => this.FindControl<Control>("DebtSection")?.BringIntoView(), DispatcherPriority.Background);
        }
        _viewModel.PropertyChanged += (_, e) =>
        {
            var name = e.PropertyName;
            if (Dispatcher.UIThread.CheckAccess())
                OnViewModelPropertyChanged(name);
            else
                Dispatcher.UIThread.Post(() => OnViewModelPropertyChanged(name));
        };

        // Сумма наличными сразу выделена — кассир может просто начать печатать цифры,
        // не стирая предложенную сумму вручную, либо сразу нажать Enter для оплаты без изменений.
        // 2026-10-04, владелец (Android): «при оплате клавиатура мешает». На Android фокус в поле суммы сразу
        // открывает экранную клавиатуру поверх окна оплаты — там курсор не ставим: клавиатура появится, когда
        // кассир сам нажмёт на поле.
        // 2026-10-17, редизайн (этап 2): окно оплаты — одна колонка шириной панели, поэтому перестройка
        // двух колонок в столбик на телефоне (NarrowStack, класс narrow) больше не нужна.

        Opened += (_, _) =>
        {
            // 2026-10-17: место окна ещё раз — уже с настоящим масштабом экрана и после общей подгонки
            // окон под экран (DialogScreenFit.RegisterForAllWindows ставит свой MaxHeight).
            if (_dockOwner is { } dockOwner)
            {
                ApplyDock(dockOwner);
                QueueRedock();
            }

            if (_viewModel.IsCashMode && !OperatingSystem.IsAndroid())
            {
                CashReceivedBox.Focus();
                CashReceivedBox.SelectAll();
            }
        };
        Closed += (_, _) => DetachDockOwner();
        // 2026-10-23 (ТЗ компактной оплаты): высота окна теперь по содержимому, поэтому после её смены (другой способ оплаты, клиент,
        // консультант) место окна пересчитывается: низ остаётся у низа главного окна, окно растёт вверх.
        SizeChanged += (_, e) => OnOwnSizeChanged(e);

        // Tunnel: RadioButton сам забирает Enter (отмечает себя), ComboBox — Enter и стрелки,
        // а ScrollViewer правой колонки — прокрутку. Разбираем клавиши раньше них.
        // 2026-10-20 (П1-0): раньше всех — отличаем сканер от кассира (OnScannerGuardTunnel).
        AddHandler(KeyDownEvent, OnScannerGuardTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPaymentKeyTunnel, RoutingStrategies.Tunnel);
    }

    // 2026-10-20, П1-0 (живая проверка: во время сканирования открылось окно оплаты большого чека, и продажа прошла
    // наличными «без сдачи»): Enter, которым сканер заканчивает код, в окне оплаты нажимал «Оплатить», а цифры кода
    // попадали в поле «Получено». Сканер печатает символы быстрее 60 мс друг за другом и сразу жмёт Enter — так
    // кассир не набирает. Такой Enter (и Enter сразу после кода, начатого в окне кассы) окно оплаты не принимает:
    // оплата не проводится, поле «Получено» возвращается к тому, что было до скана, кассиру — подсказка.
    // Ручной набор суммы и Enter кассира работают как раньше.
    private const int ScanKeyGapMs = 60;
    private const int ScanEnterGapMs = 120;
    private const int ScanMinChars = 4;

    private readonly AvaloniaKeyboardWedgeBarcodeService? _scanner =
        App.AppHost?.Services.GetService<IBarcodeInputService>() as AvaloniaKeyboardWedgeBarcodeService;

    private int _burstChars;
    private long _lastBurstCharAt;
    private TextBox? _burstBox;
    private string? _burstText;

    private static long KeyTime() =>
        AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestamp() is { } ts ? (long)ts : Environment.TickCount64;

    private static bool IsScanChar(Key key) =>
        key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9 or >= Key.A and <= Key.Z;

    private void OnScannerGuardTunnel(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None)
            return;
        var now = KeyTime();

        if (IsScanChar(e.Key))
        {
            var fast = _burstChars > 0 && now - _lastBurstCharAt is >= 0 and <= ScanKeyGapMs;
            if (!fast)
            {
                _burstChars = 0;
                _burstBox = FocusManager?.GetFocusedElement() as TextBox;
                _burstText = _burstBox?.Text;
            }
            _burstChars++;
            _lastBurstCharAt = now;
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            _burstChars = 0;
            return;
        }

        var burst = _burstChars >= ScanMinChars && now - _lastBurstCharAt is >= 0 and <= ScanEnterGapMs;
        var fromScanner = burst || _scanner is { HasFreshCompleteScan: true } or { ScanJustCompleted: true };
        _burstChars = 0;
        if (!fromScanner)
            return;

        e.Handled = true;
        _scanner?.DiscardBuffer();
        if (burst && _burstBox is not null)
        {
            _burstBox.Text = _burstText;
            _burstBox.SelectAll();
        }
        _viewModel.ErrorMessage = Tr.T(
            "Скан штрихкода во время оплаты не принят, оплата не проведена. Чтобы добавить товар, закройте окно оплаты (Esc).",
            "Төлөм учурунда штрихкод кабыл алынган жок, төлөм өткөрүлгөн жок. Товар кошуу үчүн төлөм терезесин жабыңыз (Esc).",
            "The barcode scan was ignored during payment; nothing was paid. Close the payment window (Esc) to add a product.",
            "Ödeme sırasında barkod okutması kabul edilmedi, ödeme yapılmadı. Ürün eklemek için ödeme penceresini kapatın (Esc).",
            "To'lov vaqtida shtrix-kod qabul qilinmadi, to'lov o'tkazilmadi. Mahsulot qo'shish uchun to'lov oynasini yoping (Esc).");
        PosLogger.Log("[PAYMENT] Enter сканера в окне оплаты не принят (скан во время оплаты) — оплата не проведена.", "PAYMENT");
    }

    private void CashReceivedBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        Pay();
    }

    private void Pay()
    {
        if (_viewModel.PayCommand.CanExecute(null))
            _viewModel.PayCommand.Execute(null);
    }

    /// <summary>Способы оплаты в том порядке, в каком их читают на экране (2×2): так их обходят
    /// стрелки Влево/Вправо.</summary>
    private RadioButton[] PaymentOptions => [PayCashOption, PayTransferOption, PayDebtOption, PayMixedOption];

    /// <summary>Оплата с клавиатуры (2026-09-27, просьба владельца: «кассир стрелкой выбирает
    /// безналичные»):
    ///   ← / →  — предыдущий / следующий способ оплаты: Наличные → Безналичные → В долг → Смешанная;
    ///   ↑ / ↓  — вверх / вниз по сетке способов (2×2): Наличные ↕ В долг, Безналичные ↕ Смешанная
    ///            (2026-09-27, «стрелка вниз-вверх при оплате не работает» — раньше ↑/↓ листали
    ///            только банк и на «Наличных» не делали ничего). Банк — ↑/↓ в самом списке банков
    ///            (Tab до него), это обычное поведение списка;
    ///   Enter  — оплатить, как кнопка «Оплатить».
    /// Работает, когда фокус на кнопке способа оплаты, в поле «Получено», в списке банков (←/→)
    /// или нигде. В поле «Получено» ←/→ по-прежнему двигают курсор по цифрам и переключают способ
    /// оплаты, только когда двигать курсор некуда: сумма выделена целиком (так окно и открывается)
    /// или курсор уже у края. Остальные поля (суммы смешанной оплаты, долг, бонусы, консультант) и
    /// раскрытый список банков клавиши получают как раньше.</summary>
    private void OnPaymentKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || BankComboBox.IsDropDownOpen)
            return;

        var focused = FocusManager?.GetFocusedElement();
        var onOption = focused is RadioButton option && Array.IndexOf(PaymentOptions, option) >= 0;
        var onBank = ReferenceEquals(focused, BankComboBox);
        var nothingFocused = focused is null || ReferenceEquals(focused, this);

        switch (e.Key)
        {
            case Key.Left or Key.Right:
                var forward = e.Key == Key.Right;
                if (onOption || onBank || nothingFocused
                    || (ReferenceEquals(focused, CashReceivedBox) && IsCashCaretAtEdge(forward)))
                    e.Handled = MovePaymentMethod(forward ? 1 : -1);
                break;

            // Строка вниз/вверх в сетке 2×2. В самом списке банков Вверх/Вниз листают банки —
            // это делает ComboBox, туда не вмешиваемся.
            case Key.Up or Key.Down when onOption || nothingFocused || ReferenceEquals(focused, CashReceivedBox):
                e.Handled = MovePaymentMethod(e.Key == Key.Down ? 2 : -2);
                break;

            case Key.Enter when onOption || onBank || nothingFocused:
                e.Handled = true;
                Pay();
                break;

            // 2026-10-20, живая проверка: в «Смешанной» Enter в полях сумм ничего не делал (поле съедало
            // клавишу). Как в наличных: суммы сходятся — Enter оплачивает (проверки те же, что у кнопки).
            case Key.Enter when (ReferenceEquals(focused, MixedCashBox) || ReferenceEquals(focused, MixedNonCashBox))
                                && _viewModel.IsMixedMode && _viewModel.MixedAmountsMatch:
                e.Handled = true;
                Pay();
                break;
        }
    }

    /// <summary>Переключает способ оплаты так же, как клик мышью (RadioButton.Toggle ставит
    /// IsChecked через SetCurrentValue — привязка к модели остаётся). Наличные сразу отдают фокус
    /// полю «Получено» с выделенной суммой — как при открытии окна: можно печатать сумму или
    /// жать Enter. Остальные способы получают фокус на своей кнопке.</summary>
    private bool MovePaymentMethod(int step)
    {
        var options = PaymentOptions;
        var current = Array.FindIndex(options, option => option.IsChecked == true);
        var next = current + step;
        if (current < 0 || next < 0 || next >= options.Length)
            return false;

        options[next].SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        if (_viewModel.IsCashMode)
        {
            CashReceivedBox.Focus(NavigationMethod.Directional);
            CashReceivedBox.SelectAll();
        }
        else
        {
            options[next].Focus(NavigationMethod.Directional);
        }
        return true;
    }

    /// <summary>Двигать курсор в поле «Получено» в эту сторону некуда: сумма выделена целиком
    /// или курсор уже в начале/конце.</summary>
    private bool IsCashCaretAtEdge(bool forward)
    {
        var length = CashReceivedBox.Text?.Length ?? 0;
        var start = Math.Min(CashReceivedBox.SelectionStart, CashReceivedBox.SelectionEnd);
        var end = Math.Max(CashReceivedBox.SelectionStart, CashReceivedBox.SelectionEnd);
        if (length == 0 || (start == 0 && end == length))
            return true;
        if (start != end)
            return false;
        return forward ? CashReceivedBox.CaretIndex >= length : CashReceivedBox.CaretIndex <= 0;
    }

    private void AttachCloseHandler()
    {
        _viewModel.RequestClose += async result =>
        {
            if (!result)
            {
                Close(false);
                return;
            }

            if (_viewModel.IsPrintReceiptEnabled && !HardwareModeHelper.IsPrinterPortConfigured())
            {
                var decision = await _dialogService.ShowPrinterNotConnectedAsync().ConfigureAwait(true);
                if (decision == PrinterNotConnectedResult.Cancel)
                    return;

                // 2026-09-29: только для этой оплаты — выбор кассира в «Напечатать чек» не меняем.
                _viewModel.SkipPrintForThisPayment();
            }

            // Clicking "Оплатить" here is already the cashier's confirmation — the extra
            // "Подтвердить оплату?" Да/Нет dialog this used to show was a redundant second
            // click on top of it.
            Close(true);
        };
    }

    // 2026-10-24 (приёмка, ПР-9): после Enter без клиента в «В долг» показывались две подсказки об одном и том же: тёмная
    // PayBlockedHint «Чтобы продать в долг, выберите клиента.» и красная ErrorMessage «Для продажи в долг выберите клиента.».
    // Пока видна подсказка, красную с тем же смыслом прячем; любые другие ошибки показываются как раньше.
    // Текст ошибки — тот же Tr.T, что в CheckoutViewModel.ExecutePay (модель не трогаем).
    private static bool IsDebtNeedsClientError(string? text) =>
        !string.IsNullOrEmpty(text) && text == Tr.T("Для продажи в долг выберите клиента.",
            "Карызга сатуу үчүн клиентти тандаңыз.",
            "Select a client to sell on credit.",
            "Veresiye satış için müşteri seçin.",
            "Qarzga sotish uchun mijozni tanlang.");

    private void UpdateErrorVisibility()
    {
        var hintShown = _viewModel.HasPayBlockedHint && _viewModel.IsDebtMode && !_viewModel.HasSelectedClient;
        ErrorText.IsVisible = _viewModel.HasError && !(hintShown && IsDebtNeedsClientError(_viewModel.ErrorMessage));
    }

    /// <summary>2026-10-21 (пакет 6, п. 27): «Консультант: Айгерим» / «Консультант: не выбран — выбрать» над кнопками.</summary>
    private void UpdateConsultantSummary()
    {
        var name = _viewModel.IsConsultantEnabled ? _viewModel.SelectedConsultant?.Name : null;
        ConsultantSummaryText.Text = _viewModel.ConsultantTitleText + ": " + (string.IsNullOrWhiteSpace(name)
            ? Tr.T("не выбран — выбрать ›", "тандалган жок — тандоо ›", "not chosen — choose ›", "seçilmedi — seç ›", "tanlanmagan — tanlash ›")
            : name + "  ›");
    }

    /// <summary>2026-10-22 (аудит новой сборки, П4): строка «Консультант: …» над кнопками нужна, пока блок консультанта не виден
    /// на экране (он внизу прокрутки). Когда блок на виду — строка скрыта: слово «Консультант» не стоит дважды, подвал ниже.</summary>
    private void UpdateConsultantSummaryVisibility()
    {
        var show = _viewModel.ShowConsultantSection;
        if (show && ConsultantSection.IsEffectivelyVisible && ConsultantSection.Bounds.Height > 0
            && ConsultantSection.TranslatePoint(new Point(0, 0), ContentScroll) is { } top)
        {
            // Гистерезис: пока строка показана, окно прокрутки ниже на её высоту — считаем по высоте «как без неё», иначе на границе мигало бы.
            var viewport = ContentScroll.Viewport.Height + (ConsultantSummaryButton.IsVisible ? ConsultantSummaryButton.Bounds.Height + 6 : 0);
            var inView = viewport > 0 && top.Y >= 0 && top.Y + Math.Min(ConsultantSection.Bounds.Height, 64) <= viewport + 1;
            show = !inView;
        }
        if (ConsultantSummaryButton.IsVisible != show)
            ConsultantSummaryButton.IsVisible = show;
    }

    /// <summary>2026-10-21: касание строки консультанта — прокрутка к его блоку (он внизу прокручиваемой части).</summary>
    private void ConsultantSummary_Click(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsConsultantEnabled)
            _viewModel.IsConsultantEnabled = true;
        Dispatcher.UIThread.Post(() => ConsultantSection.BringIntoView(), DispatcherPriority.Background);
    }

    public string PaymentMethodKey => _viewModel.PaymentMethod;

    public bool IsPrintReceiptEnabled => _viewModel.IsPrintReceiptEnabled;

    public string CashReceivedForApi
    {
        get
        {
            if (_viewModel.PaymentMethod == "cash")
            {
                var normalized = CheckoutValidation.NormalizeDecimal(_viewModel.CashReceived);
                var total = _viewModel.EffectiveTotalDue;
                return string.IsNullOrEmpty(normalized)
                    ? total.ToString("0.00", CultureInfo.InvariantCulture)
                    : normalized;
            }

            return "0.00";
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private async void ChooseClient_Click(object? sender, RoutedEventArgs e)
    {
        await ClientPickerDialog.Open(this, _viewModel).ConfigureAwait(true);
        RestoreFocusAfterClientPicker();
    }

    /// <summary>2026-10-23 (ТЗ компактной оплаты): карточка выбора клиента закрылась — фокус возвращается в окно оплаты: наличные — в «Получено»
    /// с выделенной суммой (не Android), иначе — на выбранную плитку способа оплаты. Тогда Enter = «Оплатить», а не повторное открытие выбора
    /// клиента (фокус оставался на кнопке «Выбрать клиента»).</summary>
    private void RestoreFocusAfterClientPicker() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
                return;
            if (_viewModel.IsCashMode && !OperatingSystem.IsAndroid())
            {
                CashReceivedBox.Focus();
                CashReceivedBox.SelectAll();
            }
            else
            {
                Array.Find(PaymentOptions, option => option.IsChecked == true)?.Focus();
            }
        }, DispatcherPriority.Loaded);

    // ------------------------------------------------------------------ окно у края главного окна
    // 2026-10-17, редизайн (этап 2), макет Pay: оплата — окном справа в главном окне, чек кассы остаётся виден. Окно по-прежнему
    // модальное (ShowDialog), меняются только его место и размер. Деньги, привязки и клавиши не затронуты.
    // 2026-10-23 (ТЗ компактной оплаты): раньше окно было «панелью во всю высоту» главного окна (Height = MaxHeight = высота клиентской
    // области) — на 1296×879 всегда 879 px, внизу пустота, на 1024×600 всегда прокрутка. Теперь высота — по содержимому
    // (SizeToContent=Height, потолок MaxHeight), низ окна прижат к низу главного окна, а при ширине главного окна от 1000 окно в две колонки.

    /// <summary>Уже/ниже этого главное окно — окно оплаты не прилипает, стоит по центру, как раньше.</summary>
    private const double MinDockOwnerWidth = 760;
    private const double MinDockOwnerHeight = 480;

    /// <summary>От этой ширины главного окна окно оплаты — в две колонки (класс two-col), уже — в одну.</summary>
    private const double TwoColOwnerWidth = 1000;

    /// <summary>Ширина самого окна оплаты, от которой не прилипшее окно (Android-планшет, маленькое главное окно) тоже делится на две колонки.</summary>
    private const double TwoColWindowWidth = 680;

    /// <summary>Отступ окна от края главного окна: снизу и сверху (для потолка высоты) и справа, когда чека слева нет.</summary>
    private const double DockMargin = 8;

    /// <summary>Ниже этой доступной высоты окно плотнее (класс compact): 1366×768 даёт ≈ 712.</summary>
    private const double CompactPanelHeight = 760;

    /// <summary>Ниже этой доступной высоты — самый плотный вид (класс tiny): моноблок 1024×600.</summary>
    private const double TinyPanelHeight = 650;

    /// <summary>Высота окна, которую ещё не успели измерить (до показа): по ней первый раз ставится низ окна, после показа место уточняется.</summary>
    private const double GuessWindowHeight = 460;

    private Window? _dockOwner;
    private bool _redockQueued;

    /// <summary>Ширина окна в одну колонку: треть главного окна, но 400…480 точек (на 1024 — 400, на 1366 — 451,
    /// с 1455 — 480) и не шире самого окна.</summary>
    internal static double PanelWidthFor(double ownerWidth) =>
        Math.Min(ownerWidth, Math.Clamp(Math.Round(ownerWidth * 0.33), 400, 480));

    /// <summary>Ширина окна в две колонки: 60 % главного окна, но 680…800 точек (1296 — 778, 1024 — 680).</summary>
    internal static double TwoColWidthFor(double ownerWidth) =>
        Math.Clamp(Math.Round(ownerWidth * 0.6), 680, 800);

    /// <summary>Ставит окно оплаты у правого края главного окна (вызывать до ShowDialog — так окно
    /// сразу появляется на месте, без прыжка из центра). Если справа на главном экране чек (раскладки
    /// «Классика», «Карточки», «Минимал», «Профи») и слева от него хватает места, окно встаёт вплотную слева от чека — чек виден.
    /// Нет подходящего окна (маленькое, свёрнуто, экран покупателя) или Android — false, окно по центру.</summary>
    public bool DockTo(Window owner)
    {
        if (OperatingSystem.IsAndroid())
            return false;
        var target = IsDockable(owner) ? owner : MainKassaWindow(owner);
        if (target is null || !ApplyDock(target))
            return false;
        if (!ReferenceEquals(_dockOwner, target))
        {
            DetachDockOwner();
            _dockOwner = target;
            target.PositionChanged += DockOwner_PositionChanged;
            target.PropertyChanged += DockOwner_PropertyChanged;
        }
        return true;
    }

    private static Window? MainKassaWindow(Window owner)
    {
        // 2026-10-21: на Android окна — слои (NurMarketKassa.Window), панель оплаты не прилипает (DockTo → false); иначе CS1503.
#if NURANDROID
        return null;
#else
        var main = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return main is not null && !ReferenceEquals(main, owner) && IsDockable(main) ? main : null;
#endif
    }

    private static bool IsDockable(Window window) =>
        window.IsVisible
        && window.WindowState != WindowState.Minimized
        && !window.GetType().Name.Contains("CustomerDisplay", StringComparison.Ordinal)
        && window.ClientSize.Width >= MinDockOwnerWidth
        && window.ClientSize.Height >= MinDockOwnerHeight;

    /// <summary>Считает и ставит место и размер окна по клиентской области окна target (в пределах рабочей области его экрана):
    /// ширина и число колонок — по ширине главного окна; потолок высоты (MaxHeight) — его высота минус 2×8; низ окна — у низа
    /// клиентской области минус 8; по горизонтали — слева от чека, если он справа и слева от него хватает места, иначе — у правого края минус 8.</summary>
    private bool ApplyDock(Window target)
    {
        try
        {
            if (!IsDockable(target))
                return false;
            var client = target.ClientSize;
            var twoCol = client.Width >= TwoColOwnerWidth;
            var width = twoCol ? TwoColWidthFor(client.Width) : PanelWidthFor(client.Width);
            var left = client.Width - DockMargin - width;

            var receipt = FindReceipt(target);
            if (receipt.Span is { } span && span.Width > 0)
            {
                // Чек хотя бы на четверть ушёл бы под окно, а слева от него есть место — ставим окно левым краем чека, чек виден.
                var overlap = Math.Min(client.Width, span.Right) - Math.Max(left, span.Left);
                if (overlap > span.Width * 0.25 && span.Left >= width + DockMargin)
                    left = span.Left - width;
            }
            ShowLinesCount(receipt.Lines);

            var scaling = target.RenderScaling > 0 ? target.RenderScaling : 1.0;
            var origin = target.PointToScreen(new Point(0, 0));
            var area = new PixelRect(origin, new PixelSize((int)Math.Round(client.Width * scaling), (int)Math.Round(client.Height * scaling)));
            var screen = target.Screens.ScreenFromWindow(target);
            if (screen is not null)
                area = area.Intersect(screen.WorkingArea);
            if (area.Width < 300 * scaling || area.Height < 360 * scaling)
                return false;

            // Потолок высоты: доступная область минус отступы сверху и снизу; от него считаются плотность (compact/tiny) и прокрутка тела.
            var maxHeight = area.Height / scaling - 2 * DockMargin;
            var height = Bounds.Height > 0 ? Bounds.Height : Math.Min(GuessWindowHeight, maxHeight);

            var x = origin.X + (int)Math.Round(left * scaling);
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - (int)Math.Round(width * scaling)));
            var y = area.Bottom - (int)Math.Round((height + DockMargin) * scaling);
            y = Math.Max(area.Y, y);

            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            MinWidth = 0;
            MinHeight = 0;
            MaxWidth = width;
            MaxHeight = maxHeight;
            if (double.IsNaN(Width) || Math.Abs(Width - width) > 0.5)
                Width = width; // 2026-10-23: ту же ширину заново не ставим — иначе бесконечный пересчёт («качели»)
            Position = new PixelPoint(x, y);
            Classes.Set("two-col", twoCol);
            Classes.Set("compact", maxHeight < CompactPanelHeight);
            Classes.Set("tiny", maxHeight < TinyPanelHeight); // 2026-10-23: моноблок 1024×600 (аудит круг 3, Б-6)
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно оплаты: место окна: {ex.Message}", "WARNING");
            return false;
        }
    }

    /// <summary>2026-10-23 (ТЗ компактной оплаты): высота окна изменилась — окно у края главного окна ставится заново (низ на месте, рост вверх);
    /// не прилипшее окно (Android, маленькое главное окно) делится на две колонки по своей фактической ширине.</summary>
    private void OnOwnSizeChanged(SizeChangedEventArgs e)
    {
        if (_dockOwner is not null)
        {
            if (e.HeightChanged)
                QueueRedock();
            return;
        }
        var twoCol = Bounds.Width >= TwoColWindowWidth;
        if (Classes.Contains("two-col") != twoCol)
            Classes.Set("two-col", twoCol);
    }

    /// <summary>Строки чека на главном экране — список, привязанный к строкам корзины (CartLineItemVm),
    /// во всех шести раскладках. Берём самый крупный видимый: его левый и правый край в точках окна.</summary>
    private static (Rect? Span, int Lines) FindReceipt(Window target)
    {
        Rect? best = null;
        var lines = 0;
        foreach (var items in target.GetVisualDescendants().OfType<ItemsControl>())
        {
            if (items.ItemsSource is not IEnumerable<CartLineItemVm> || !items.IsEffectivelyVisible
                || items.Bounds.Width < 80 || items.TransformToVisual(target) is not { } transform)
                continue;
            var rect = new Rect(items.Bounds.Size).TransformToAABB(transform);
            if (best is { } b && b.Width * b.Height >= rect.Width * rect.Height)
                continue;
            best = rect;
            lines = items.ItemsSource is System.Collections.ICollection collection ? collection.Count : 0;
        }
        return (best, lines);
    }

    /// <summary>Подзаголовок «3 позиции» (номера чека до оплаты ещё нет — его даёт сервер).</summary>
    private void ShowLinesCount(int lines)
    {
        if (lines <= 0)
            return;
        var mod10 = lines % 10;
        var mod100 = lines % 100;
        var ru = mod10 == 1 && mod100 != 11 ? "позиция"
            : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "позиции"
            : "позиций";
        // 2026-10-22 (аудит новой сборки, П4): ky — «сап» вместо русского «позиция». 2026-10-23: «· 3 позиции» стоит рядом с заголовком в одной строке.
        LinesText.Text = "· " + Tr.T($"{lines} {ru}", $"{lines} сап",
            lines == 1 ? "1 item" : $"{lines} items",
            $"{lines} ürün", $"{lines} ta pozitsiya");
        LinesText.IsVisible = true;
        SubtitleText.IsVisible = false;
    }

    private void DockOwner_PositionChanged(object? sender, PixelPointEventArgs e) => QueueRedock();

    private void DockOwner_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 2026-10-21: у TopLevel оболочки Android нет ClientSizeProperty (CS0117); там панель не прилипает вовсе.
#if NURANDROID
        if (e.Property == WindowStateProperty)
#else
        if (e.Property == TopLevel.ClientSizeProperty || e.Property == WindowStateProperty)
#endif
            QueueRedock();
    }

    /// <summary>Главное окно сдвинули/изменили размер — панель за ним (один раз на пачку событий).</summary>
    private void QueueRedock()
    {
        if (_redockQueued)
            return;
        _redockQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _redockQueued = false;
            if (_dockOwner is { } dockOwner && IsVisible)
                ApplyDock(dockOwner);
        }, DispatcherPriority.Background);
    }

    internal void DetachDockOwner()
    {
        if (_dockOwner is not { } dockOwner)
            return;
        dockOwner.PositionChanged -= DockOwner_PositionChanged;
        dockOwner.PropertyChanged -= DockOwner_PropertyChanged;
        _dockOwner = null;
    }
}
