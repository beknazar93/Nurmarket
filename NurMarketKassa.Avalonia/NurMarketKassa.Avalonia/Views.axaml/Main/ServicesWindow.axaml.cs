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
        PosDialogs.Info(this, Tr.T("Интеграция с WhatsApp пока в разработке.", "WhatsApp менен интеграция азырынча иштелип чыгууда.", "WhatsApp integration is still in development.", "WhatsApp entegrasyonu henüz geliştiriliyor.", "WhatsApp bilan integratsiya hali ishlab chiqilmoqda."), Tr.T("В разработке", "Иштелип чыгууда", "In development", "Geliştiriliyor", "Ishlab chiqilmoqda"));

    private void TelegramConnect_Click(object? sender, RoutedEventArgs e) =>
        PosDialogs.Info(this, Tr.T("Интеграция с Telegram пока в разработке.", "Telegram менен интеграция азырынча иштелип чыгууда.", "Telegram integration is still in development.", "Telegram entegrasyonu henüz geliştiriliyor.", "Telegram bilan integratsiya hali ishlab chiqilmoqda."), Tr.T("В разработке", "Иштелип чыгууда", "In development", "Geliştiriliyor", "Ishlab chiqilmoqda"));

    private void CashierInterface_Click(object? sender, RoutedEventArgs e) =>
        PosDialogs.Info(this, Tr.T("Этот раздел пока в разработке.", "Бул бөлүм азырынча иштелип чыгууда.", "This section is still in development.", "Bu bölüm henüz geliştiriliyor.", "Bu bo'lim hali ishlab chiqilmoqda."), Tr.T("В разработке", "Иштелип чыгууда", "In development", "Geliştiriliyor", "Ishlab chiqilmoqda"));
}
