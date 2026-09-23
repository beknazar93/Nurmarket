using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class AdminSupportWindow : Window
{
    private const string Phone = "+996559340032";

    public AdminSupportWindow()
    {
        InitializeComponent();
    }

    private void Phone_Click(object? sender, RoutedEventArgs e) =>
        OpenUrl($"tel:{Phone}");

    private void WhatsApp_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://wa.me/{Phone}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            PosMessageBox.Show(this, $"Не удалось открыть WhatsApp: {ex.Message}", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Website_Click(object? sender, RoutedEventArgs e) =>
        OpenUrl("https://nurcrm.kg/");

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            PosMessageBox.Show(this, "Не удалось открыть ссылку.", "Поддержка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
