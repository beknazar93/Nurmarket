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
        RenderOwnerAppState();
        // 2026-10-21: версия — «1.17.58», без служебного «.0» в конце (её ставит PosSettingsWindow: Version.ToString()) (пакет 3, п. 141).
        AppVersionText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty && ShortVersion(AppVersionText.Text) is { } shorter && shorter != AppVersionText.Text)
                AppVersionText.Text = shorter;
        };
        AddGlobalCatalogStatus();
    }

    /// <summary>2026-10-27: строка «Справочник: N товаров, обновлён …, добавлено с сервера: M» под номером версии
    /// (встроенный справочник готовых наименований NurCRM — GlobalProductCatalog). XAML не менялся: строка добавляется в ту же колонку.</summary>
    private void AddGlobalCatalogStatus()
    {
        if (AppVersionText.Parent is not StackPanel host)
            return;
        var line = new TextBlock { FontSize = 12, Foreground = this.TryFindResource("BrushTextSoft", ActualThemeVariant, out var b) ? b as IBrush : null, IsVisible = false };
        host.Children.Add(line);
        System.Threading.Tasks.Task.Run(() => NurMarketKassa.Services.GlobalProducts.GlobalProductCatalog.Instance.GetStatus()).ContinueWith(t =>
        {
            if (t.IsFaulted || !t.Result.Available)
                return;
            var s = t.Result;
            var n = s.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            var added = s.AddedFromServer.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            var date = string.IsNullOrWhiteSpace(s.ExportedAt) ? "—" : s.ExportedAt;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                line.Text = Tr.T($"Справочник: {n} товаров, обновлён {date}, добавлено с сервера: {added}",
                    $"Маалымдама: {n} товар, жаңыртылган {date}, серверден кошулду: {added}",
                    $"Catalog: {n} products, updated {date}, added from the server: {added}",
                    $"Katalog: {n} ürün, güncelleme {date}, sunucudan eklenen: {added}",
                    $"Ma'lumotnoma: {n} mahsulot, yangilangan {date}, serverdan qo'shilgan: {added}");
                line.IsVisible = true;
            });
        });
    }

    /// <summary>2026-10-21: «1.17.58.0» → «1.17.58»; не версия — null (пакет 3, п. 141).</summary>
    private static string? ShortVersion(string? text) =>
        System.Version.TryParse(text?.Trim(), out var v) && v.Revision == 0 ? v.ToString(3) : null;

    /// <summary>2026-09-30: «Программа владельца» на этом компьютере — ярлык «NurMarket Владелец».
    /// Карточка только в кассе (не в самой программе владельца и не в отдельном её пакете).</summary>
    private void RenderOwnerAppState()
    {
        OwnerAppCard.IsVisible = !AppMode.IsOwner && !Services.OwnerShortcuts.IsSeparateOwnerInstall;
        if (!OwnerAppCard.IsVisible)
            return;

        OwnerAppTitle.Text = Tr.T("Программа владельца", "Ээсинин программасы", "Owner app", "Sahip programı", "Egasining dasturi");
        OwnerAppDesc.Text = Tr.T("«NurCRM Владелец» — склад, продажи, финансы, аналитика и зарплата. Ставится вместе с кассой; на кассовом компьютере её можно не ставить.",
            "«NurCRM Владелец» — кампа, сатуулар, каржы, аналитика жана эмгек акы. Касса менен бирге орнотулат; кассалык компьютерге коюлбаса да болот.",
            "“NurCRM Владелец” — warehouse, sales, finance, analytics and salary. Installed with the till; it can be left off the till computer.",
            "«NurCRM Владелец» — depo, satışlar, finans, analiz ve maaş. Kasa ile birlikte kurulur; kasa bilgisayarına kurulmayabilir.",
            "«NurCRM Владелец» — ombor, sotuvlar, moliya, tahlil va ish haqi. Kassa bilan birga o'rnatiladi; kassa kompyuteriga qo'yilmasa ham bo'ladi.");
        var installed = Services.OwnerShortcuts.IsInstalled();
        OwnerAppStatus.Text = installed
            ? Tr.T("✓ Установлена на этом компьютере (ярлык «NurCRM Владелец»).", "✓ Бул компьютерге орнотулган («NurCRM Владелец» энбелгиси).", "✓ Installed on this computer (“NurCRM Владелец” shortcut).", "✓ Bu bilgisayarda kurulu («NurCRM Владелец» kısayolu).", "✓ Bu kompyuterga o'rnatilgan («NurCRM Владелец» yorlig'i).")
            : Tr.T("Не установлена — на этом компьютере только касса.", "Орнотулган эмес — бул компьютерде касса гана.", "Not installed — only the till on this computer.", "Kurulu değil — bu bilgisayarda yalnızca kasa var.", "O'rnatilmagan — bu kompyuterda faqat kassa.");
        OwnerAppAddButton.Content = installed
            ? Tr.T("Восстановить ярлык", "Энбелгини калыбына келтирүү", "Restore shortcut", "Kısayolu geri yükle", "Yorliqni tiklash")
            : Tr.T("Установить программу владельца", "Ээсинин программасын орнотуу", "Install owner app", "Sahip programını kur", "Egasining dasturini o'rnatish");
        OwnerAppRemoveButton.Content = Tr.T("Убрать с этого компьютера", "Бул компьютерден алып салуу", "Remove from this computer", "Bu bilgisayardan kaldır", "Bu kompyuterdan olib tashlash");
        OwnerAppRemoveButton.IsVisible = installed;
    }

    private void OwnerAppAdd_Click(object? sender, RoutedEventArgs e)
    {
        Services.OwnerShortcuts.ApplyChoice(withOwner: true);
        RenderOwnerAppState();
    }

    private void OwnerAppRemove_Click(object? sender, RoutedEventArgs e)
    {
        Services.OwnerShortcuts.ApplyChoice(withOwner: false);
        RenderOwnerAppState();
    }

    /// <summary>Канал обновлений — см. UpdateChannel.</summary>
    private void RenderTesterState()
    {
        var tester = UpdateChannel.IsTester;
        // 2026-10-21: «Тестовые версии / Код тестировщика» — для тестировщиков кассы; владельцу блок не нужен. Уже включённый
        // тестовый канал остаётся виден — чтобы его можно было выключить (пакет 3, п. 141).
        TesterCard.IsVisible = !AppMode.IsOwner || tester;
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
