using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PaymentStatusDialog : Window
{
    private readonly DispatcherTimer _spinnerTimer;
    private double _spinnerAngle;

    public PaymentStatusDialog() : this(0) { }

    public PaymentStatusDialog(double totalAmount)
    {
        InitializeComponent();
        AmountText.Text = Tr.T($"{totalAmount:0.00} сом", $"{totalAmount:0.00} сом", $"{totalAmount:0.00} som", $"{totalAmount:0.00} som", $"{totalAmount:0.00} so'm");
        StatusTitle.Text = Tr.T("Проводим оплату…", "Төлөм өтүп жатат…", "Processing the payment…", "Ödeme gerçekleştiriliyor…", "To'lov amalga oshirilmoqda…");
        StatusMessage.Text = Tr.T("Пожалуйста, подождите. Не закрывайте кассу.", "Күтө туруңуз. Кассаны жаппаңыз.", "Please wait. Don't close the till.", "Lütfen bekleyin. Kasayı kapatmayın.", "Iltimos, kuting. Kassani yopmang.");
        CloseButton.Content = Tr.T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli");

        // 30fps вместо 60fps — визуально неотличимо для простого вращения,
        // но вдвое меньше пробуждений таймера на слабом CPU во время оплаты.
        _spinnerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _spinnerTimer.Tick += OnSpinnerTick;
        _spinnerTimer.Start();

        // 2026-09-23. Пока оплата «крутится», в окне не было ни одной кнопки: рамки нет
        // (SystemDecorations="None"), CloseButton скрыт и показывается только при ошибке,
        // Escape не обрабатывался. Если запрос к серверу зависал, кассир с очередью оставался
        // заблокирован намертво — выход только через диспетчер задач.
        //
        // Кнопку показываем не сразу: первые секунды ожидание нормально, и ранний «выход»
        // провоцировал бы закрывать окно, пока оплата ещё идёт. Через 20 секунд ожидание уже
        // ненормально, и человек должен иметь возможность вернуться к чеку.
        _escapeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _escapeTimer.Tick += OnEscapeAvailable;
        _escapeTimer.Start();

        Closed += (_, _) =>
        {
            StopSpinner();
            StopEscapeTimer();
        };
    }

    private DispatcherTimer? _escapeTimer;

    /// <summary>Окно ждёт ответа сервера. Пока true, закрытие по Escape запрещено: оплата
    /// могла уже уйти, и закрывать окно вслепую нельзя.</summary>
    private bool _waiting = true;

    private void OnEscapeAvailable(object? sender, EventArgs e)
    {
        StopEscapeTimer();
        if (!_waiting)
            return;

        StatusMessage.Text = Tr.T(
            "Сервер отвечает дольше обычного. Оплата может быть уже проведена — проверьте чек в «Продажах», прежде чем пробивать заново.",
            "Сервер адаттагыдан узак жооп берип жатат. Төлөм өтүп кеткен болушу мүмкүн — кайра өткөрүүдөн мурун «Сатуулар» бөлүмүнөн чекти текшериңиз.",
            "The server is taking longer than usual. The payment may already have gone through — check the receipt in “Sales” before charging again.",
            "Sunucu normalden geç yanıt veriyor. Ödeme zaten geçmiş olabilir — yeniden tahsil etmeden önce fişi «Satışlar» bölümünde kontrol edin.",
            "Server odatdagidan uzoqroq javob bermoqda. To'lov allaqachon o'tgan bo'lishi mumkin — chekni qayta o'tkazishdan oldin uni «Sotuvlar»da tekshiring.");

        CloseButton.Content = Tr.T("Закрыть окно", "Терезени жабуу", "Close window", "Pencereyi kapat", "Oynani yopish");
        CloseButton.IsVisible = true;
    }

    private void StopEscapeTimer()
    {
        if (_escapeTimer is null)
            return;

        _escapeTimer.Stop();
        _escapeTimer.Tick -= OnEscapeAvailable;
        _escapeTimer = null;
    }

    /// <summary>2026-10-04, отчёт о производительности (п. 9): окно «Оплата успешно» — первая же клавиша
    /// закрывает его сразу, не дожидаясь паузы. Кассир сканирует следующий товар, пока окно ещё видно, —
    /// раньше этот скан пропадал целиком (окно держало фокус 1,2 с). Символ клавиши отдаётся общему
    /// разбору сканера (тот же, что у окна кассы), остальные символы скана и Enter приходят уже в окно
    /// кассы — товар добавляется в новый чек.</summary>
    private bool _closeOnAnyKey;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_closeOnAnyKey)
        {
            _closeOnAnyKey = false;
            e.Handled = true;
            Close(true);
            if (e.Key != Key.Escape)
            {
                try
                {
                    (App.AppHost?.Services.GetService(typeof(NurMarketKassa.Interfaces.IBarcodeInputService))
                        as NurMarketKassa.Interfaces.IBarcodeInputService)?.ProcessKeyDown(e);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Окно «Платёж принят»: клавиша не передана сканеру: {ex.Message}", "PAYMENT");
                }
            }

            return;
        }

        // Escape работает только когда кнопка выхода уже показана: до этого момента оплата
        // штатно выполняется, и закрывать окно нечего.
        if (e.Key == Key.Escape && CloseButton.IsVisible)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    public void ShowResult(bool isSuccess, string? message)
    {
        _waiting = false;
        StopSpinner();
        StopEscapeTimer();
        LoadingPanel.IsVisible = false;
        ResultCircle.IsVisible = true;
        AmountText.IsVisible = false;

        if (isSuccess)
        {
            ResultCircle.Background = ThemeBrush("BrushSuccessSoft", Brushes.DarkGreen);
            ResultIcon.Foreground = ThemeBrush("BrushUiStatusOk", Brushes.Green);
            ResultIcon.Text = "✓";
            StatusTitle.Text = Tr.T("Оплата успешно", "Төлөм ийгиликтүү өттү", "Payment successful", "Ödeme başarılı", "To'lov muvaffaqiyatli");
            StatusTitle.Foreground = ThemeBrush("BrushUiStatusOk", Brushes.Green);
            StatusMessage.Text = string.IsNullOrWhiteSpace(message)
                ? Tr.T("Платёж принят. Открываем новый чек.", "Төлөм кабыл алынды. Жаңы чек ачылууда.", "Payment accepted. Opening a new receipt.", "Ödeme alındı. Yeni fiş açılıyor.", "To'lov qabul qilindi. Yangi chek ochilmoqda.")
                : message;
            CloseButton.IsVisible = false;
            _closeOnAnyKey = true; // 2026-10-04, п. 9: следующий скан закрывает окно сразу
            return;
        }

        ResultCircle.Background = ThemeBrush("BrushDangerSoft", Brushes.DarkRed);
        ResultIcon.Foreground = ThemeBrush("BrushDanger", Brushes.Red);
        ResultIcon.Text = "×";
        StatusTitle.Text = Tr.T("Оплата не прошла", "Төлөм өтпөй калды", "Payment failed", "Ödeme başarısız oldu", "To'lov amalga oshmadi");
        StatusTitle.Foreground = ThemeBrush("BrushDanger", Brushes.Red);
        StatusMessage.Text = string.IsNullOrWhiteSpace(message)
            ? Tr.T("Не удалось выполнить оплату. Попробуйте ещё раз.", "Төлөмдү аткаруу мүмкүн болгон жок. Кайра аракет кылыңыз.", "Could not complete the payment. Please try again.", "Ödeme tamamlanamadı. Lütfen tekrar deneyin.", "To'lovni amalga oshirib bo'lmadi. Qaytadan urinib ko'ring.")
            : message;
        CloseButton.IsVisible = true;
    }

    private void OnSpinnerTick(object? sender, EventArgs e)
    {
        _spinnerAngle = (_spinnerAngle + 10) % 360;
        if (SpinnerPath.RenderTransform is RotateTransform rotation)
            rotation.Angle = _spinnerAngle;
    }

    private void StopSpinner()
    {
        _spinnerTimer.Stop();
        _spinnerTimer.Tick -= OnSpinnerTick;
    }

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
        && value is IBrush brush
            ? brush
            : fallback;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
