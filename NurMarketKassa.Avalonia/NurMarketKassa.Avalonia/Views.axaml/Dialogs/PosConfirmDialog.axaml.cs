using Avalonia.Controls;
using Avalonia.Input;
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
        Shell.Title = title; // 2026-10-16: заголовок — в шапке оболочки ModalShell
        MessageText.Text = message;
        CancelButton.Content = cancelText ?? Tr.T("Нет", "Жок", "No", "Hayır", "Yo'q");
        ConfirmButton.Content = confirmText ?? Tr.T("Да", "Ооба", "Yes", "Evet", "Ha");

        // 2026-10-16: опасное действие — красная главная кнопка (класс danger оболочки, тот же #DC2626).
        if (accent == PosConfirmAccent.Danger)
            ConfirmButton.Classes.Add("danger");
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

    /// <summary>2026-10-20, живая проверка: окно «Товар не найден» открывается прямо во время сканирования, и
    /// следующий скан уходил в него — цифры пропадали, а Enter сканера нажимал главную кнопку «Добавить на склад».
    /// В таком окне Enter — безопасная «Пропустить», а код, пришедший со сканера, закрывает окно («Пропустить»)
    /// и передаётся дальше (<paramref name="onScan"/>, обычно — в очередь сканов чека). Ручные «Добавить на
    /// склад» мышью/касанием и Esc работают как раньше.</summary>
    public void PassScannerThrough(IBarcodeInputService scanner, Action<string> onScan)
    {
        ConfirmButton.IsDefault = false;
        CancelButton.IsDefault = true;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.None && e.Key != Key.Escape)
                scanner.ProcessKeyDown(e);
        }, RoutingStrategies.Tunnel);

        void OnScanned(string code)
        {
            if (_scanPassed)
                return;
            _scanPassed = true;
            onScan(code);
            Close(false);
        }

        scanner.BarcodeScanned += OnScanned;
        Closed += (_, _) => scanner.BarcodeScanned -= OnScanned;
    }

    private bool _scanPassed;

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => Close(true);
}
