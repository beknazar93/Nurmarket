using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class UpdatesSettingsView : UserControl
{
    public UpdatesSettingsView()
    {
        InitializeComponent();
        RenderTesterState();
    }

    /// <summary>Канал обновлений — см. UpdateChannel.</summary>
    private void RenderTesterState()
    {
        var tester = UpdateChannel.IsTester;
        TesterCodeBox.IsVisible = !tester;
        TesterActivateButton.IsVisible = !tester;
        TesterOffButton.IsVisible = tester;
        TesterStatusText.Text = tester
            ? Tr.T("Включён тестовый канал: касса получает версии, которые ещё проходят проверку.",
                "Тесттик канал күйгүзүлгөн: касса текшерүүдөгү версияларды алат.",
                "Test channel is on: this till receives versions still under testing.",
                "Test kanalı açık: kasa test aşamasındaki sürümleri alır.",
                "Test kanali yoqilgan: kassa hali sinovdagi versiyalarni oladi.")
            : Tr.T("Обычный канал: обновления приходят после завершения тестирования.",
                "Кадимки канал: жаңыртуулар текшерүү бүткөндөн кийин келет.",
                "Regular channel: updates arrive after testing is finished.",
                "Normal kanal: güncellemeler test bittikten sonra gelir.",
                "Oddiy kanal: yangilanishlar sinov tugagach keladi.");
    }

    private void TesterActivate_Click(object? sender, RoutedEventArgs e)
    {
        if (UpdateChannel.TryActivate(TesterCodeBox.Text))
        {
            TesterCodeBox.Text = "";
            RenderTesterState();
            return;
        }

        TesterStatusText.Text = Tr.T("Код неверный.", "Код туура эмес.", "Wrong code.", "Kod yanlış.", "Kod noto'g'ri.");
    }

    private void TesterOff_Click(object? sender, RoutedEventArgs e)
    {
        UpdateChannel.Deactivate();
        RenderTesterState();
    }
}
