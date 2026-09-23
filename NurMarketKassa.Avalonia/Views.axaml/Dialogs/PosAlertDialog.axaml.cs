using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

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
    public PosAlertDialog()
    {
        InitializeComponent();
    }

    public PosAlertDialog(string title, string message, PosAlertKind kind = PosAlertKind.Info, string buttonText = "Понятно")
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = buttonText;

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
}
