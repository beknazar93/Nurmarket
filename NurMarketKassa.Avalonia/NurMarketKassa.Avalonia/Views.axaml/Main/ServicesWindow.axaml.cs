using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class ServicesWindow : Window
{
    public ServicesWindow()
    {
        InitializeComponent();
        FullscreenHelper.Apply(this);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void WhatsAppConnect_Click(object? sender, RoutedEventArgs e) =>
        PosDialogs.Info(this, "Интеграция с WhatsApp пока в разработке.", "В разработке");

    private void TelegramConnect_Click(object? sender, RoutedEventArgs e) =>
        PosDialogs.Info(this, "Интеграция с Telegram пока в разработке.", "В разработке");

    private void CashierInterface_Click(object? sender, RoutedEventArgs e) =>
        PosDialogs.Info(this, "Этот раздел пока в разработке.", "В разработке");
}
