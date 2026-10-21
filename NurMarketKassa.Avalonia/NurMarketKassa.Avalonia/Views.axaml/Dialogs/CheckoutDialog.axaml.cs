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
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CheckoutViewModel.SelectedConsultant) or nameof(CheckoutViewModel.IsConsultantEnabled))
                UpdateConsultantSummary();
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
            // 2026-10-17: место панели ещё раз — уже с настоящим масштабом экрана и после общей подгонки
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

    /// <summary>2026-10-21 (пакет 6, п. 27): «Консультант: Айгерим» / «Консультант: не выбран — выбрать» над кнопками.</summary>
    private void UpdateConsultantSummary()
    {
        var name = _viewModel.IsConsultantEnabled ? _viewModel.SelectedConsultant?.Name : null;
        ConsultantSummaryText.Text = _viewModel.ConsultantTitleText + ": " + (string.IsNullOrWhiteSpace(name)
            ? Tr.T("не выбран — выбрать ›", "тандалган жок — тандоо ›", "not chosen — choose ›", "seçilmedi — seç ›", "tanlanmagan — tanlash ›")
            : name + "  ›");
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

    private async void ChooseClient_Click(object? sender, RoutedEventArgs e) =>
        await ClientPickerDialog.Open(this, _viewModel).ConfigureAwait(true);

    // ------------------------------------------------------------------ панель у края главного окна
    // 2026-10-17, редизайн (этап 2), макет Pay: оплата — панелью справа во всю высоту главного окна,
    // чек кассы остаётся виден. Окно по-прежнему модальное (ShowDialog), меняются только его место и
    // размер. Деньги, привязки и клавиши не затронуты.

    /// <summary>Уже/ниже этого главное окно — панель не ставим, окно оплаты по центру, как раньше.</summary>
    private const double MinDockOwnerWidth = 760;
    private const double MinDockOwnerHeight = 480;

    /// <summary>Ниже этого панель плотнее (класс compact): 1024×768 и 1366×768 дают ≈ 697.</summary>
    private const double CompactPanelHeight = 760;

    private Window? _dockOwner;
    private bool _redockQueued;

    /// <summary>Ширина панели: треть главного окна, но 400…480 точек (на 1024 — 400, на 1366 — 451,
    /// с 1455 — 480) и не шире самого окна.</summary>
    internal static double PanelWidthFor(double ownerWidth) =>
        Math.Min(ownerWidth, Math.Clamp(Math.Round(ownerWidth * 0.33), 400, 480));

    /// <summary>Ставит окно оплаты панелью у правого края главного окна (вызывать до ShowDialog — так окно
    /// сразу появляется на месте, без прыжка из центра). Если справа на главном экране чек (раскладки
    /// «Классика», «Карточки», «Минимал», «Профи»), панель встаёт вплотную слева от чека — чек виден.
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
        var main = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return main is not null && !ReferenceEquals(main, owner) && IsDockable(main) ? main : null;
    }

    private static bool IsDockable(Window window) =>
        window.IsVisible
        && window.WindowState != WindowState.Minimized
        && !window.GetType().Name.Contains("CustomerDisplay", StringComparison.Ordinal)
        && window.ClientSize.Width >= MinDockOwnerWidth
        && window.ClientSize.Height >= MinDockOwnerHeight;

    /// <summary>Считает и ставит место и размер панели по клиентской области окна target
    /// (в пределах рабочей области его экрана).</summary>
    private bool ApplyDock(Window target)
    {
        try
        {
            if (!IsDockable(target))
                return false;
            var client = target.ClientSize;
            var width = PanelWidthFor(client.Width);
            var left = client.Width - width;

            var receipt = FindReceipt(target);
            if (receipt.Span is { } span && span.Width > 0)
            {
                // Чек хотя бы на четверть ушёл бы под панель — ставим панель слева от него.
                var overlap = Math.Min(client.Width, span.Right) - Math.Max(left, span.Left);
                if (overlap > span.Width * 0.25)
                    left = Math.Max(0, span.Left - width);
            }
            ShowLinesCount(receipt.Lines);

            var rect = new PixelRect(
                target.PointToScreen(new Point(left, 0)),
                target.PointToScreen(new Point(left + width, client.Height)));
            var screen = target.Screens.ScreenFromWindow(target);
            if (screen is not null)
                rect = rect.Intersect(screen.WorkingArea);
            var scaling = target.RenderScaling > 0 ? target.RenderScaling : 1.0;
            if (rect.Width < 300 * scaling || rect.Height < 360 * scaling)
                return false;

            var panelWidth = rect.Width / scaling;
            var panelHeight = rect.Height / scaling;
            SizeToContent = SizeToContent.Manual;
            WindowStartupLocation = WindowStartupLocation.Manual;
            MinWidth = 0;
            MinHeight = 0;
            MaxWidth = panelWidth;
            MaxHeight = panelHeight;
            Width = panelWidth;
            Height = panelHeight;
            Position = rect.Position;
            Classes.Set("compact", panelHeight < CompactPanelHeight);
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно оплаты: место панели: {ex.Message}", "WARNING");
            return false;
        }
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
        LinesText.Text = Tr.T($"{lines} {ru}", $"{lines} позиция", lines == 1 ? "1 item" : $"{lines} items",
            $"{lines} ürün", $"{lines} ta pozitsiya");
        LinesText.IsVisible = true;
        SubtitleText.IsVisible = false;
    }

    private void DockOwner_PositionChanged(object? sender, PixelPointEventArgs e) => QueueRedock();

    private void DockOwner_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty || e.Property == WindowStateProperty)
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
