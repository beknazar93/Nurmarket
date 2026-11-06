using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class AdminSupportWindow : Window
{
    private const string Phone = "+996221068796";

    public AdminSupportWindow()
    {
        InitializeComponent();
    }

    private void Phone_Click(object? sender, RoutedEventArgs e) =>
        OpenUrl($"tel:{Phone}");

    private void WhatsApp_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-11-06: В4 А5-05 — общий запуск ссылки (на Android — Intent, откроется приложение WhatsApp).
        if (!PlatformLauncher.OpenUrl($"https://wa.me/{Phone}"))
        {
            PosMessageBox.Show(this, Tr.T("Не удалось открыть WhatsApp.", "WhatsApp'ты ачуу мүмкүн болгон жок.", "Could not open WhatsApp.", "WhatsApp açılamadı.", "WhatsApp'ni ochib bo'lmadi."), Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
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
        // 2026-11-06: В4 А5-05 — общий запуск ссылки.
        if (!PlatformLauncher.OpenUrl(url))
        {
            PosMessageBox.Show(this, Tr.T("Не удалось открыть ссылку.", "Шилтемени ачуу мүмкүн болгон жок.", "Could not open the link.", "Bağlantı açılamadı.", "Havolani ochib bo'lmadi."), Tr.T("Поддержка", "Колдоо", "Support", "Destek", "Yordam"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
