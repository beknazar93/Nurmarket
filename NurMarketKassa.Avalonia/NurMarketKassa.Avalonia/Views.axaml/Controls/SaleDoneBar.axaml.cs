using System.Globalization;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-10-18, редизайн (этап 6, макет MAfterPay): что показать на плашке «Продажа прошла».
/// Total и PaymentMethod — из подтверждённого окна оплаты, CashReceived/Change — те же, что ушли на
/// экран покупателя; Note — сообщение для кассира при офлайн-продаже («Чек сохранён, отправится…»).</summary>
public sealed record SaleDoneInfo(double? Total, string? PaymentMethod, double? CashReceived, double? Change, string? Note);

/// <summary>2026-10-18, редизайн (этап 6): плашка «Продажа прошла» внизу главного окна вместо окна.
/// Не модальная и без фокуса — закрывается сама через 3 с, по ✕, по любой клавише/скану и касанию кассы
/// (это делает MainWindow.SaleDoneBar.cs). Пока над ней мышь (не дольше 10 с всего) или идёт «Печать копии» — время стоит.</summary>
public partial class SaleDoneBar : UserControl
{
    /// <summary>Сколько плашка видна (макет MAfterPay: «закроется сама через 3 с»).</summary>
    public static readonly TimeSpan ShowTime = TimeSpan.FromSeconds(3);

    /// <summary>2026-10-18, ревью этапа 6: наведение мыши держит плашку не дольше 10 с всего — курсор, оставшийся на месте
    /// (например, над плиткой каталога), не должен держать её вечно. Пока идёт «Печать копии» — ждём печать.</summary>
    public static readonly TimeSpan MaxShowTime = TimeSpan.FromSeconds(10);

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly DispatcherTimer _timer;
    private TimeSpan _elapsed;
    private TimeSpan _shownTotal;
    private DateTime _lastTickUtc;
    private bool _hovered;
    private bool _printing;

    /// <summary>«Печать копии» — существующая «Печать последнего чека» (BasketExtraActions). Задаёт MainWindow.</summary>
    public Func<Button, Task>? PrintCopy { get; set; }

    /// <summary>2026-10-27: «Чек» — открыть окно с чеком этой продажи (задаёт MainWindow); null — кнопки нет.</summary>
    public Func<Button, Task>? ShowReceipt { get; set; }

    public SaleDoneBar()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += OnTick;
        // Наведение мыши останавливает время (касание пальцем — нет: палец «входит» только на время нажатия).
        PointerEntered += (_, e) => _hovered = e.Pointer.Type == PointerType.Mouse;
        PointerExited += (_, _) => _hovered = false;
    }

    public bool IsShown => IsVisible;

    public void Show(SaleDoneInfo info)
    {
        TitleText.Text = Tr.T("Продажа прошла", "Сатуу өттү", "Sale completed", "Satış tamamlandı", "Sotuv amalga oshdi");

        var parts = new List<string>();
        if (info.Total is { } total)
            parts.Add(Money(total));
        if (MethodName(info.PaymentMethod) is { Length: > 0 } method)
            parts.Add(info.PaymentMethod == "cash" && info.CashReceived is > 0.004
                ? method + " " + Number(info.CashReceived.Value)
                : method);
        DetailsText.Text = string.Join(" · ", parts);
        DetailsText.IsVisible = parts.Count > 0;

        NoteText.Text = info.Note ?? "";
        NoteText.IsVisible = !string.IsNullOrWhiteSpace(info.Note);

        var hasChange = info.Change is > 0.004;
        ChangeBlock.IsVisible = hasChange;
        ChangeLabel.Text = Tr.T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim");
        ChangeText.Text = hasChange ? Money(info.Change!.Value) : "";
        ApplyNarrow(double.IsNaN(Width) ? Bounds.Width : Width);

        PrintCopyText.Text = Tr.T("Печать копии", "Көчүрмөнү басуу", "Print a copy", "Kopya yazdır", "Nusxasini chop etish");
        PrintCopyButton.IsVisible = PrintCopy != null;
        ReceiptText.Text = Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek");
        ReceiptButton.IsVisible = ShowReceipt != null;
        ToolTip.SetTip(CloseButton, Tr.T("Скрыть", "Жашыруу", "Hide", "Gizle", "Yashirish"));

        _elapsed = TimeSpan.Zero;
        _shownTotal = TimeSpan.Zero;
        _lastTickUtc = DateTime.UtcNow;
        _hovered = false;
        TimeFill.Width = double.NaN;
        TimeFill.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        IsVisible = true;
        _timer.Start();
    }

    /// <summary>2026-10-18, ревью этапа 6: нажатие внутри «Печать копии» или ✕ — их собственное действие; любое другое
    /// нажатие по плашке её убирает (MainWindow.OnSaleDonePointerTunnel).</summary>
    public bool IsOwnButton(Avalonia.Visual source) =>
        ReferenceEquals(source, PrintCopyButton) || ReferenceEquals(source, CloseButton) || ReferenceEquals(source, ReceiptButton)
        || PrintCopyButton.IsVisualAncestorOf(source) || CloseButton.IsVisualAncestorOf(source) || ReceiptButton.IsVisualAncestorOf(source);

    /// <summary>Убрать плашку сразу (скан, клавиша, касание кассы, новая оплата, ✕).</summary>
    public void Dismiss()
    {
        _timer.Stop();
        IsVisible = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var delta = now - _lastTickUtc;
        _shownTotal += delta;
        if (!_printing && (!_hovered || _shownTotal >= MaxShowTime))
            _elapsed += delta;
        _lastTickUtc = now;

        var left = 1 - _elapsed.TotalMilliseconds / ShowTime.TotalMilliseconds;
        if (left <= 0 || (!_printing && _shownTotal >= MaxShowTime))
        {
            Dismiss();
            return;
        }

        TimeFill.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        TimeFill.Width = Math.Max(0, TimeTrack.Bounds.Width * left);
    }

    private async void PrintCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (PrintCopy == null || _printing)
            return;
        _printing = true;
        try
        {
            // Результат («Копия чека … напечатана» / ошибка) показывает сама печать — строкой под чеком
            // или уведомлением, как из меню «Ещё».
            await PrintCopy(PrintCopyButton).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Плашка «Продажа прошла»: печать копии: {ex.Message}", "PRINTER");
        }
        finally
        {
            _printing = false;
            _lastTickUtc = DateTime.UtcNow;
        }
    }

    // 2026-10-27: плашка убирается, окно «Чек» открывается поверх (оно модальное и держит кассу, пока кассир его не закроет).
    private async void ReceiptButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ShowReceipt == null)
            return;
        var show = ShowReceipt;
        Dismiss();
        try
        {
            await show(ReceiptButton).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Плашка «Продажа прошла»: окно чека: {ex.Message}", "PAYMENT");
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Dismiss();

    /// <summary>Узкая плашка (телефон, &lt; 420): сдача — строкой под текстом, иначе заголовок ломался по слогам.</summary>
    private void ApplyNarrow(double width)
    {
        var narrow = width > 0 && width < 420;
        Grid.SetColumn(ChangeBlock, narrow ? 1 : 2);
        Grid.SetRow(ChangeBlock, narrow ? 1 : 0);
        ChangeBlock.HorizontalAlignment = narrow
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Stretch;
        ChangeBlock.Margin = narrow ? new Avalonia.Thickness(12, 8, 0, 0) : default;
    }

    private string? MethodName(string? method)
    {
        var key = method switch
        {
            "cash" => "csh.checkout.cash",
            "transfer" => "csh.checkout.transfer",
            "debt" => "csh.checkout.debt",
            "mixed" => "csh.checkout.mixed",
            _ => null,
        };
        return key != null && this.TryFindResource(key, ActualThemeVariant, out var value) && value is string s ? s : null;
    }

    private static string Number(double value) =>
        value.ToString(Math.Abs(value - Math.Round(value)) < 0.005 ? "N0" : "N2", Ru);

    private static string Money(double value) =>
        Number(value) + " " + Tr.T("сом", "сом", "som", "som", "so'm");
}
