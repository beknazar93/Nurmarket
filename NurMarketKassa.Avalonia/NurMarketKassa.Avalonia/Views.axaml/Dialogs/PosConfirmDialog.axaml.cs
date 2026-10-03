using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;

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
        string? confirmText = null,
        string? cancelText = null,
        PosConfirmAccent accent = PosConfirmAccent.Primary)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        CancelButton.Content = cancelText ?? Tr.T("Нет", "Жок", "No", "Hayır", "Yo'q");
        ConfirmButton.Content = confirmText ?? Tr.T("Да", "Ооба", "Yes", "Evet", "Ha");

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
        string? confirmText = null,
        string? cancelText = null,
        PosConfirmAccent accent = PosConfirmAccent.Primary) =>
        PosDialogHost.Show(new PosConfirmDialog(title, message, confirmText, cancelText, accent), owner) == true;

    /// <summary>2026-10-04, Android-касса: то же из async-кода (в Windows — прежний синхронный Show).</summary>
    public static async Task<bool> ShowModalAsync(
        Window? owner,
        string title,
        string message,
        string? confirmText = null,
        string? cancelText = null,
        PosConfirmAccent accent = PosConfirmAccent.Primary) =>
        await PosDialogHost.ShowModalAsync(new PosConfirmDialog(title, message, confirmText, cancelText, accent), owner).ConfigureAwait(true) == true;

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}
