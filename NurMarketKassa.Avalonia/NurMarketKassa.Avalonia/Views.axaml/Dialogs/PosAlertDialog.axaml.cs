using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public enum PosAlertKind
{
    Info,
    Warning,
    Error,
    Success,
}

public partial class PosAlertDialog : Window
{
    private string _title = "";
    private string _message = "";

    public PosAlertDialog() => InitializeComponent();

    public PosAlertDialog(string title, string message, PosAlertKind kind = PosAlertKind.Info, string buttonText = "Понятно")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        _title = title;
        _message = message;

        // Этап 3 бэклога "Доработки": "в любом случае и в любых обстоятельствах ошибки
        // должны иметь 2 кнопки" — Закрыть / Отложить ошибку. Применяется единообразно ко
        // ВСЕМ Warning/Error алертам, т.к. PosAlertDialog — единственное итоговое окно, через
        // которое проходят все ~100 мест показа ошибок в приложении (см. PosDialogs/PosMessageBox/
        // IUserPrompts — все они в итоге открывают именно это окно), так что кнопка "Отложить
        // ошибку" появляется сразу везде без правки каждого места по отдельности.
        var isErrorOrWarning = kind is PosAlertKind.Error or PosAlertKind.Warning;
        OkButton.Content = isErrorOrWarning ? Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish") : buttonText;
        DeferButton.IsVisible = isErrorOrWarning;

        if (kind == PosAlertKind.Error)
        {
            OkButton.Background = new SolidColorBrush(Color.Parse("#DC2626"));
            OkButton.Foreground = Brushes.White;
        }
    }

    public static void Show(Window? owner, string title, string message, PosAlertKind kind = PosAlertKind.Info, string buttonText = "Понятно")
    {
        var dlg = new PosAlertDialog(title, message, kind, buttonText);
        PosDialogHost.Show(dlg, owner);
    }

    public static Task ShowAsync(Window? owner, string title, string message, PosAlertKind kind = PosAlertKind.Info, string buttonText = "Понятно")
    {
        var dlg = new PosAlertDialog(title, message, kind, buttonText);
        return PosDialogHost.ShowAsync(dlg, owner);
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    /// <summary>
    /// "Отложить ошибку": по спецификации кассир продолжает работу как будто ничего не было —
    /// закрывается точно так же, как обычная кнопка "Закрыть" (ни один существующий вызывающий
    /// код не различает эти два исхода), но сначала пишет запись в лог с меткой [Отложено
    /// кассиром], которую подхватывает новый экран "Логи и ошибки" (LogsAndErrorsWindow).
    /// </summary>
    private void DeferButton_Click(object? sender, RoutedEventArgs e)
    {
        PosLogger.Log($"[Отложено кассиром] {_title}: {_message}", "ERROR");
        Close(true);
    }
}
