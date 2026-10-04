using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;

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

        // Сумма наличными сразу выделена — кассир может просто начать печатать цифры,
        // не стирая предложенную сумму вручную, либо сразу нажать Enter для оплаты без изменений.
        // 2026-10-04, владелец (Android): «при оплате клавиатура мешает». На Android фокус в поле суммы сразу
        // открывает экранную клавиатуру поверх окна оплаты — там курсор не ставим: клавиатура появится, когда
        // кассир сам нажмёт на поле. Узкий экран (вертикальный телефон) — «Итог» над способами оплаты.
        if (OperatingSystem.IsAndroid())
            NurMarketKassa.AvaloniaHost.Services.NarrowStack.Attach(this, CheckoutColumnsGrid, 700, "Auto,16,*");

        Opened += (_, _) =>
        {
            if (_viewModel.IsCashMode && !OperatingSystem.IsAndroid())
            {
                CashReceivedBox.Focus();
                CashReceivedBox.SelectAll();
            }
        };

        // Tunnel: RadioButton сам забирает Enter (отмечает себя), ComboBox — Enter и стрелки,
        // а ScrollViewer правой колонки — прокрутку. Разбираем клавиши раньше них.
        AddHandler(KeyDownEvent, OnPaymentKeyTunnel, RoutingStrategies.Tunnel);
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
}
