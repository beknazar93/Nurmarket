using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-19 (запрос владельца, п. 1): отдельная вкладка «ИИ» в Настройках кассы. Раньше ключи ИИ (Gemini, Groq, OpenRouter) лежали внутри карточки
/// Telegram-бота, а тумблеры Нур Советника — ниже неё, на вкладке «Операции». Теперь обе карточки снимаются с «Операций» (<see cref="OperationsSettingsView.DetachAiCards"/>)
/// и показываются здесь под своей шапкой. Поля, их сохранение и права (тариф «Стандарт», показ ключей) остались в <see cref="OperationsSettingsView"/> без изменений.</summary>
public sealed class AiSettingsView : UserControl
{
    public AiSettingsView(OperationsSettingsView operations)
    {
        Styles.Add(new StyleInclude(new Uri("avares://NurMarketKassa.Avalonia/"))
        {
            Source = new Uri("avares://NurMarketKassa.Avalonia/Views/Settings/SettingsSharedStyles.axaml"),
        });
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        var list = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 16) };
        list.Classes.Add("SettingsCardList");
        list.Bind(MaxWidthProperty, list.GetResourceObservable("SettingsPageMaxWidth"));

        var title = new TextBlock { Text = Tr.T("Искусственный интеллект", "Жасалма интеллект", "Artificial intelligence", "Yapay zeka", "Sun'iy intellekt") };
        title.Classes.Add("SettingsPageTitle");
        var subtitle = new TextBlock
        {
            Text = Tr.T("Ключи нейросетей для Telegram-бота и Нур Советника и то, что советнику разрешено делать.",
                "Telegram-бот жана Нур Кеңешчи үчүн нейрон тармак ачкычтары жана кеңешчиге эмне кылууга уруксат.",
                "Neural-network keys for the Telegram bot and Nur Advisor, and what the advisor is allowed to do.",
                "Telegram botu ve Nur Danışman için yapay zeka anahtarları ve danışmanın neler yapabileceği.",
                "Telegram-bot va Nur Maslahatchi uchun neyrotarmoq kalitlari va maslahatchiga nima qilish mumkinligi."),
        };
        subtitle.Classes.Add("SettingsPageSubtitle");
        var header = new Border { Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } } };
        header.Classes.Add("SettingsHeaderCard");
        header.HorizontalAlignment = HorizontalAlignment.Stretch;
        list.Children.Add(header);

        foreach (var card in operations.DetachAiCards())
            list.Children.Add(card);

        var scroll = new ScrollViewer { Content = list };
        scroll.Classes.Add("SettingsPageScroll");
        var host = new Border { Child = scroll };
        host.Classes.Add("SettingsContentHost");
        Content = host;
    }
}
