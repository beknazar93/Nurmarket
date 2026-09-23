using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public enum PosConfirmAccent
{
    Primary,
    Danger,
}

public partial class PosConfirmDialog : Window
{
    public PosConfirmDialog()
    {
        InitializeComponent();
    }

    public PosConfirmDialog(
        string title,
        string message,
        string confirmText = "Да",
        string cancelText = "Нет",
        PosConfirmAccent accent = PosConfirmAccent.Primary)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Content = cancelText;
        ConfirmButton.Content = confirmText;

        if (accent == PosConfirmAccent.Danger)
        {
            ConfirmButton.Background = new SolidColorBrush(Color.Parse("#DC2626"));
            ConfirmButton.Foreground = Brushes.White;
        }
    }

    public static bool Show(
        Window? owner,
        string title,
        string message,
        string confirmText = "Да",
        string cancelText = "Нет",
        PosConfirmAccent accent = PosConfirmAccent.Primary) =>
        PosDialogHost.Show(new PosConfirmDialog(title, message, confirmText, cancelText, accent), owner) == true;

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}
