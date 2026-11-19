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
        // 2026-10-23 (аудит Б-9): заголовок системной рамки — цветом темы кассы.
        NurMarketKassa.AvaloniaHost.Services.WindowChrome.Apply(this);
    }

    public static MarketplaceWindow Open(Window? owner)
    {
        // 2026-11-19 (список владельца, п. 7): одно окно на программу — повторное нажатие выводит открытое вперёд.
        return NurMarketKassa.AvaloniaHost.Views.Dialogs.WindowSingleton.ShowOnce(owner, () => new MarketplaceWindow());
    }

    /// <summary>Сразу открывает вкладку "Доп. функции" — используется, когда переходят сюда
    /// целенаправленно за конкретной доп. услугой (голосовое управление, разблокировка темы
    /// и т.п.), а не просто посмотреть маркетплейс.</summary>
    public void ShowExtrasTab() => MarketplaceContent.ShowExtrasTab();

    /// <summary>Раздел программы владельца: название уже стоит в её заголовке, а разделы
    /// переключаются меню — своя шапка с «Закрыть» там только повторяет его (2026-09-27).</summary>
    public MarketplaceWindow AsSection()
    {
        WindowHeader.IsVisible = false;
        return this;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
