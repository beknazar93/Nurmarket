using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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
            ShowTesterResult(true, Tr.T("Код принят — тестовый канал включён. Проверяю обновления…",
                "Код кабыл алынды — тесттик канал күйгүзүлдү. Жаңыртууларды текшерип жатам…",
                "Code accepted — test channel is on. Checking for updates…",
                "Kod kabul edildi — test kanalı açıldı. Güncellemeler kontrol ediliyor…",
                "Kod qabul qilindi — test kanali yoqildi. Yangilanishlar tekshirilmoqda…"));

            // 2026-09-28, просьба владельца: после верного кода сразу проверить обновления, как будто
            // нажали «Проверить обновления» (обработчик — PosSettingsWindow.CheckUpdate_Click).
            // Сама установка не запускается: кнопку «Обновить» человек нажимает сам.
            if (CheckUpdateButton.IsEnabled)
            {
                CheckUpdateButton.BringIntoView();
                CheckUpdateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            return;
        }

        ShowTesterResult(false, Tr.T("Код неверный — тестовый канал не включён. Проверьте код и попробуйте ещё раз.",
            "Код туура эмес — тесттик канал күйгүзүлгөн жок. Кодду текшерип, кайра аракет кылыңыз.",
            "Wrong code — the test channel is not on. Check the code and try again.",
            "Kod yanlış — test kanalı açılmadı. Kodu kontrol edip tekrar deneyin.",
            "Kod noto'g'ri — test kanali yoqilmadi. Kodni tekshirib, qayta urinib ko'ring."));
        TesterCodeBox.Focus();
        TesterCodeBox.SelectAll();
    }

    private void TesterOff_Click(object? sender, RoutedEventArgs e)
    {
        UpdateChannel.Deactivate();
        RenderTesterState();
        TesterResultBorder.IsVisible = false;
    }

    /// <summary>Зелёная галочка или красный крестик под полем кода (2026-09-28).</summary>
    private void ShowTesterResult(bool ok, string text)
    {
        var key = ok ? "BrushSuccess" : "BrushDanger";
        var soft = ok ? "BrushSuccessSoft" : "BrushDangerSoft";
        var brush = this.TryFindResource(key, ActualThemeVariant, out var b) ? b as IBrush : null;
        var softBrush = this.TryFindResource(soft, ActualThemeVariant, out var s) ? s as IBrush : null;

        TesterResultIcon.Text = ok ? "✓" : "✗";
        TesterResultIcon.Foreground = brush;
        TesterResultText.Text = text;
        TesterResultText.Foreground = brush;
        TesterResultBorder.BorderBrush = brush;
        TesterResultBorder.Background = softBrush;
        TesterResultBorder.IsVisible = true;
    }
}
