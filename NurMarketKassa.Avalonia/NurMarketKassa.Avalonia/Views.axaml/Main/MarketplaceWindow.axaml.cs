using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Маркетплейс как отдельное окно, а не вкладка настроек (2026-09-05, по просьбе
/// пользователя: "убери маркетплейс из настроек") — теперь он открывается напрямую из главного
/// меню (см. MainWindow.NavigateMarketplace) и из кнопки "Открыть в Маркетплейсе" на странице
/// "Экран" (см. ScreenSettingsView), минуя окно настроек целиком. Само содержимое
/// (MarketplaceView) не изменилось — оно и раньше было самодостаточным UserControl со своими
/// стилями, просто раньше показывалось внутри ContentHost окна настроек.</summary>
public partial class MarketplaceWindow : Window
{
    public MarketplaceWindow()
    {
        InitializeComponent();
    }

    public static MarketplaceWindow Open(Window? owner)
    {
        var window = new MarketplaceWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
        return window;
    }

    /// <summary>Сразу открывает вкладку "Доп. функции" — используется, когда переходят сюда
    /// целенаправленно за конкретной доп. услугой (голосовое управление, разблокировка темы
    /// и т.п.), а не просто посмотреть маркетплейс.</summary>
    public void ShowExtrasTab() => MarketplaceContent.ShowExtrasTab();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
