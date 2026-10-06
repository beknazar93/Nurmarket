using System.Collections.ObjectModel;
using Avalonia.Controls;
using NurMarketKassa.Services;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class OperationsSettingsView : UserControl
{
    private ObservableCollection<BankQrSetting> _bankSettings = new();

    // Ни у одного банка нет логотипа-по-умолчанию (2026-09-05: раньше "Элкарт" был
    // единственным исключением со своей встроенной картинкой Assets/Elkart-logo.png, но
    // пользователь попросил убрать все "мини-логотипы" из системы) — показывается общая
    // векторная иконка (см. XAML, Border/TextBlock с IsVisible="{Binding HasNoLogo}",
    // Segoe MDL2 Assets glyph), пока кассир сам не загрузит свой логотип через
    // ChangeLogo_Click — тогда путь сохраняется в UserPreferences.BankLogoPaths и переживает
    // перезапуск кассы.
    // Показываем только три основных банка. Полный список из двадцати шести — в диалоге
    // «Добавить банк»: владелец выбирает оттуда свои, и они появляются в этом списке.
    // Вываливать все 26 сразу нельзя — владелец жаловался, что список слишком длинный,
    // а магазин работает с одним-двумя банками.
    private string[] _banks => KyrgyzBanks.DefaultVisible;

    public OperationsSettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += Lan_Attached;
        DetachedFromVisualTree += Lan_Detached;
    }

    // ── Работа без интернета: обмен продажами по локальной сети ─────────────────────

    private DispatcherTimer? _lanStatusTimer;
    private bool _lanLoading;

    private void RefreshLanUi()
    {
        LanTitle.Text = Tr.T("Работа без интернета: обмен между кассами",
            "Интернетсиз иштөө: кассалар ортосунда алмашуу",
            "Working without internet: exchange between tills",
            "İnternetsiz çalışma: kasalar arası veri paylaşımı",
            "Internetsiz ishlash: kassalar o'rtasida almashuv");
        LanDesc.Text = Tr.T(
            "Если интернет пропал, кассы магазина и программа владельца передают друг другу продажи по локальной сети (Wi-Fi или кабель), а на одном компьютере — внутри него. Остаток уменьшается сразу на всех кассах, владелец видит выручку. На сервер каждый чек отправляет только та касса, где его пробили.",
            "Интернет жоголсо, дүкөндүн кассалары жана ээсинин программасы сатууларды бири-бирине жергиликтүү тармак (Wi-Fi же кабель) аркылуу, ал эми бир компьютерде — анын ичинде өткөрүп берет. Калдык бардык кассаларда дароо азаят, ээси түшкөн акчаны көрөт. Ар бир чекти серверге аны урган касса гана жөнөтөт.",
            "If the internet goes down, the store's tills and the owner program pass sales to each other over the local network (Wi-Fi or cable), and on a single computer — within it. Stock goes down on all tills at once and the owner sees the revenue. Each receipt is sent to the server only by the till that rang it up.",
            "İnternet kesilirse mağazanın kasaları ve sahip programı satışları yerel ağ (Wi-Fi veya kablo) üzerinden, tek bilgisayarda ise kendi içinde birbirine aktarır. Stok tüm kasalarda hemen düşer, mağaza sahibi ciroyu görür. Her fişi sunucuya yalnızca onu kesen kasa gönderir.",
            "Internet uzilib qolsa, do'kon kassalari va egasining dasturi sotuvlarni bir-biriga mahalliy tarmoq (Wi-Fi yoki kabel) orqali, bitta kompyuterda esa uning ichida uzatadi. Qoldiq barcha kassalarda darhol kamayadi, do'kon egasi tushumni ko'radi. Har bir chekni serverga faqat uni urgan kassa yuboradi.");
        LanEnabledCheck.Content = Tr.T("Обмен включён", "Алмашуу күйгүзүлгөн", "Exchange is on", "Veri paylaşımı açık", "Almashuv yoqilgan");
        LanCodeLabel.Text = Tr.T(
            "Код магазина — одинаковый на всех кассах и в программе владельца этой точки. Пустой — обмен только внутри этого компьютера.",
            "Дүкөндүн коду — ушул дүкөндүн бардык кассаларында жана ээсинин программасында бирдей. Бош болсо — алмашуу ушул компьютердин ичинде гана.",
            "Shop code — the same on every till and in the owner program of this store. Empty — exchange only within this computer.",
            "Mağaza kodu — bu mağazanın tüm kasalarında ve sahip programında aynı olmalı. Boş bırakılırsa paylaşım yalnızca bu bilgisayar içinde yapılır.",
            "Do'kon kodi — shu do'konning barcha kassalarida va egasining dasturida bir xil. Bo'sh bo'lsa — almashuv faqat shu kompyuter ichida.");
        LanCodeBox.Watermark = Tr.T("например, NUR-7KQ4MZ", "мисалы, NUR-7KQ4MZ", "e.g. NUR-7KQ4MZ", "örneğin NUR-7KQ4MZ", "masalan, NUR-7KQ4MZ");
        LanSaveCodeButton.Content = Tr.T("Сохранить код", "Кодду сактоо", "Save code", "Kodu kaydet", "Kodni saqlash");
        LanNewCodeButton.Content = Tr.T("Придумать код", "Код түзүү", "Generate code", "Kod oluştur", "Kod yaratish");
        LanFirewallHint.Text = Tr.T(
            "После сохранения кода Windows может спросить разрешение для сети — нажмите «Разрешить», иначе другие кассы эту не увидят.",
            "Кодду сактагандан кийин Windows тармакка уруксат сурашы мүмкүн — «Уруксат берүү» дегенди басыңыз, болбосо башка кассалар бул кассаны көрбөйт.",
            "After you save the code, Windows may ask for network permission — click “Allow”, otherwise other tills won't see this one.",
            "Kodu kaydettikten sonra Windows ağ izni isteyebilir — «İzin ver»e basın, aksi hâlde diğer kasalar bu kasayı göremez.",
            "Kodni saqlagandan keyin Windows tarmoq uchun ruxsat so'rashi mumkin — «Ruxsat berish»ni bosing, aks holda boshqa kassalar bu kassani ko'rmaydi.");

        var prefs = UserPreferences.Instance;
        _lanLoading = true;
        LanEnabledCheck.IsChecked = prefs.LanSyncEnabled;
        _lanLoading = false;
        LanCodeBox.Text = prefs.LanShopCode ?? "";
        UpdateLanStatus();
    }

    private void UpdateLanStatus() =>
        LanStatusText.Text = NurMarketKassa.Services.Lan.LanSyncService.Instance.StatusLine();

    private void OnLanStatusChanged() => Dispatcher.UIThread.Post(UpdateLanStatus);

    private void Lan_Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        NurMarketKassa.Services.Lan.LanSyncService.Instance.StatusChanged += OnLanStatusChanged;
        // Появление и пропажа соседей событием не сообщаются — строку состояния обновляем сами.
        _lanStatusTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => UpdateLanStatus());
        _lanStatusTimer.Start();
    }

    private void Lan_Detached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        NurMarketKassa.Services.Lan.LanSyncService.Instance.StatusChanged -= OnLanStatusChanged;
        _lanStatusTimer?.Stop();
    }

    private void LanEnabled_Changed(object? sender, RoutedEventArgs e)
    {
        if (_lanLoading)
            return;
        var prefs = UserPreferences.Instance;
        var enabled = LanEnabledCheck.IsChecked == true;
        if (prefs.LanSyncEnabled == enabled)
            return;
        prefs.LanSyncEnabled = enabled;
        prefs.SaveToDisk();
        if (enabled)
            NurMarketKassa.Services.Lan.LanSyncService.Instance.Start();
        else
            NurMarketKassa.Services.Lan.LanSyncService.Instance.Stop();
        UpdateLanStatus();
    }

    private void LanSaveCode_Click(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var code = (LanCodeBox.Text ?? "").Trim();
        LanCodeBox.Text = code;
        if (string.Equals(prefs.LanShopCode ?? "", code, StringComparison.Ordinal))
        {
            UpdateLanStatus();
            return;
        }

        prefs.LanShopCode = code;
        prefs.SaveToDisk();
        NurMarketKassa.Services.Lan.LanSyncService.Instance.Restart();
        UpdateLanStatus();
    }

    /// <summary>Код — общий секрет магазина: по нему кассы узнают своих. Случайный надёжнее
    /// придуманного вручную («1234» подберёт кто угодно в той же сети).</summary>
    private void LanNewCode_Click(object? sender, RoutedEventArgs e)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[6];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        LanCodeBox.Text = "NUR-" + new string(chars);
    }

    // ── Сфера магазина ───────────────────────────────────────────────────────────────

    private void RefreshSphereUi()
    {
        SphereTitle.Text = Tr.T("Сфера магазина", "Дүкөндүн тармагы", "Store type", "Mağaza türü", "Do'kon turi");
        SphereHint.Text = Tr.T(
            "Под что настроена касса. Меняется в любой момент, на продажи и отчёты не влияет.",
            "Касса кайсы тармакка ыңгайлаштырылган. Каалаган убакта өзгөртсө болот, сатууларга жана отчётторго таасир этпейт.",
            "What the till is set up for. You can change it at any time; sales and reports aren't affected.",
            "Kasanın hangi işe göre ayarlandığı. Her zaman değiştirilebilir; satışları ve raporları etkilemez.",
            "Kassa qaysi soha uchun sozlangan. Istalgan vaqtda o'zgartirish mumkin, sotuv va hisobotlarga ta'sir qilmaydi.");
        SphereGroceryRadio.Content = Tr.T("Продуктовый", "Азык-түлүк", "Grocery", "Market (gıda)", "Oziq-ovqat");
        SphereClothingRadio.Content = Tr.T("Одежда и похожие", "Кийим жана ушул сыяктуулар", "Clothing and similar", "Giyim ve benzeri", "Kiyim va shunga o'xshash");
        SphereServicesRadio.Content = Tr.T("Услуги", "Кызматтар", "Services", "Hizmetler", "Xizmatlar");

        var sphere = UserPreferences.Instance.MarketSphere;
        SphereGroceryRadio.IsChecked = sphere == MarketSpheres.Grocery;
        SphereClothingRadio.IsChecked = sphere == MarketSpheres.Clothing;
        SphereServicesRadio.IsChecked = sphere == MarketSpheres.Services;
        SphereDescription.Text = sphere switch
        {
            MarketSpheres.Clothing => Tr.T(
                "При оплате появляется блок «Консультант»: кто помог покупателю, и процент ему с этой продажи — как на сайте.",
                "Төлөө учурунда «Консультант» блогу чыгат: сатып алуучуга ким жардам бергени жана ага ушул сатуудан канча пайыз тиешелүү экени — сайттагыдай.",
                "The payment window shows a “Consultant” block: who helped the customer and their percentage of the sale, as on the website.",
                "Ödeme sırasında «Danışman» bölümü görünür: müşteriye kim yardım etti ve bu satıştan alacağı yüzde — sitedeki gibi.",
                "To'lov oynasida «Maslahatchi» bloki paydo bo'ladi: xaridorga kim yordam bergani va unga shu sotuvdan beriladigan foiz — saytdagidek."),
            MarketSpheres.Services => Tr.T(
                "Каталог открывается на вкладке «Услуги». Услуги продаются без остатка: касса не просит пополнить склад и не показывает у них количество.",
                "Каталог «Кызматтар» өтмөгүндө ачылат. Кызматтар калдыксыз сатылат: касса кампаны толуктоону сурабайт жана алардын санын көрсөтпөйт.",
                "The catalog opens on the “Services” tab. Services are sold without stock: the till doesn't ask to restock them or show their quantity.",
                "Katalog «Hizmetler» sekmesinde açılır. Hizmetler stoksuz satılır: kasa stok eklemenizi istemez ve miktarlarını göstermez.",
                "Katalog «Xizmatlar» bo'limida ochiladi. Xizmatlar qoldiqsiz sotiladi: kassa omborni to'ldirishni so'ramaydi va ularning sonini ko'rsatmaydi."),
            _ => Tr.T(
                "Обычная касса магазина, как было.",
                "Дүкөндүн кадимки кассасы, мурункудай.",
                "A regular store till, as before.",
                "Her zamanki mağaza kasası.",
                "Oddiy do'kon kassasi, avvalgidek."),
        };
        // 2026-10-06 (О-31, О-32): правила обмена — только у одежды.
        RefreshExchangeRulesCard();
    }

    private void Sphere_Click(object? sender, RoutedEventArgs e)
    {
        var sphere = SphereClothingRadio.IsChecked == true ? MarketSpheres.Clothing
            : SphereServicesRadio.IsChecked == true ? MarketSpheres.Services
            : MarketSpheres.Grocery;
        MarketSpheres.Set(sphere);
        RefreshSphereUi();
        // 2026-09-28: вид магазина теперь хранится и у компании на сервере — все кассы
        // перестраиваются сами при входе (BE-18). См. OperationsSettingsView.Sphere.cs.
        _ = SaveSphereOnServerAsync(sphere);
    }

    public void LoadBankQrSettings()
    {
        RefreshSphereUi();
        RefreshLanUi();
        _bankSettings = new ObservableCollection<BankQrSetting>();
        var prefs = UserPreferences.Instance;

        // Обработчик сравнивает значение перед записью, поэтому эта установка не вызывает
        // лишнего сохранения настроек.
        DynamicQrCheck.IsChecked = prefs.DynamicPaymentQrEnabled;

        TelegramTokenBox.Text = prefs.TelegramBotToken ?? "";
        TelegramSummaryCheck.IsChecked = prefs.TelegramShiftSummaryEnabled;
        TelegramCommandsCheck.IsChecked = prefs.TelegramCommandsEnabled;
        OwnerPhoneBox.Text = prefs.OwnerPhone ?? "";
        TelegramAiKeyBox.Text = prefs.TelegramAiKey ?? "";
        GroqKeyBox.Text = prefs.GroqApiKey ?? "";
        OpenRouterKeyBox.Text = prefs.OpenRouterApiKey ?? "";
        // 2026-10-05, владелец: «строго соблюдай разделение тарифов». Бот на «Старте» — только купленный в
        // Маркетплейсе; ИИ (ключи Gemini, Groq, OpenRouter) — только «Стандарт».
        TelegramCard.IsVisible = TariffGate.CanUseTelegramBot;
        AiKeysPanel.IsVisible = TariffGate.CanUseAi;
        AiTariffLockText.IsVisible = !TariffGate.CanUseAi;
        AiTariffLockText.Text = TariffGate.AiLockedMessage;
        UpdateTelegramStatus();

        void AddBankRow(string bank, bool isCustom)
        {
            string? qrPath = prefs.BankQrPaths?.TryGetValue(bank, out var qr) == true ? qr : null;
            string? logoPath = prefs.BankLogoPaths?.TryGetValue(bank, out var customLogo) == true
                ? customLogo
                : null;

            // Пустой список отмеченных означает «владелец ещё не выбирал» — отмечаем те же
            // три банка, что показывались до обновления, чтобы касса не изменилась сама собой.
            var visible = prefs.VisibleBankNames is { Count: > 0 }
                ? prefs.VisibleBankNames
                : KyrgyzBanks.DefaultVisible.ToList();

            var row = new BankQrSetting
            {
                BankName = bank,
                LogoPath = logoPath,
                QrCodePath = qrPath,
                IsCustom = isCustom,
                ShowAtCheckout = visible.Contains(bank, StringComparer.OrdinalIgnoreCase),
            };
            row.PropertyChanged += BankRow_PropertyChanged;
            _bankSettings.Add(row);
        }

        foreach (var bank in _banks)
            AddBankRow(bank, isCustom: false);
        foreach (var bank in prefs.CustomBankNames)
            AddBankRow(bank, isCustom: true);

        BankQrItemsControl.ItemsSource = _bankSettings;
    }

    private void UpdateTelegramStatus()
    {
        var prefs = UserPreferences.Instance;
        // 2026-10-05, владелец: «при подключении в админке бота касса тоже должна подключаться». Бот на сервере NurCRM —
        // общий для компании: сводки и ответы идут с сервера, на кассе его подключать не нужно.
        if (NurMarketKassa.Services.Api.ServerTelegramBotApi.LastKnownServerMode == true)
        {
            TelegramStatusText.Text = Tr.T("✓ Бот работает на сервере NurCRM для всей компании — на этой кассе подключать его не нужно.",
                "✓ Бот NurCRM серверинде бүт компания үчүн иштейт — бул кассада аны туташтыруунун кереги жок.",
                "✓ The bot runs on the NurCRM server for the whole company — no need to connect it on this till.",
                "✓ Bot, tüm şirket için NurCRM sunucusunda çalışıyor — bu kasada bağlamaya gerek yok.",
                "✓ Bot NurCRM serverida butun kompaniya uchun ishlaydi — bu kassada uni ulash shart emas.");
            return;
        }
        if (string.IsNullOrWhiteSpace(prefs.TelegramChatId))
        {
            TelegramStatusText.Text = Tr.T("Получатель не определён.", "Алуучу аныкталган жок.", "Recipient not set.", "Alıcı belirlenmedi.", "Qabul qiluvchi aniqlanmagan.");
            return;
        }

        TelegramStatusText.Text = string.IsNullOrWhiteSpace(prefs.TelegramChatTitle)
            ? Tr.T($"Получатель определён (чат {prefs.TelegramChatId}).", $"Алуучу аныкталды (чат {prefs.TelegramChatId}).", $"Recipient set (chat {prefs.TelegramChatId}).", $"Alıcı belirlendi (sohbet {prefs.TelegramChatId}).", $"Qabul qiluvchi aniqlandi (chat {prefs.TelegramChatId}).")
            : Tr.T($"Получатель: {prefs.TelegramChatTitle}.", $"Алуучу: {prefs.TelegramChatTitle}.", $"Recipient: {prefs.TelegramChatTitle}.", $"Alıcı: {prefs.TelegramChatTitle}.", $"Qabul qiluvchi: {prefs.TelegramChatTitle}.");
    }

    private void SaveTelegramToken()
    {
        var prefs = UserPreferences.Instance;
        var token = (TelegramTokenBox.Text ?? "").Trim();
        if (prefs.TelegramBotToken == token)
            return;
        prefs.TelegramBotToken = token;
        prefs.SaveToDisk();
    }

    /// <summary>Узнаёт chat_id владельца по последним сообщениям боту. Владелец должен сначала
    /// нажать «Старт» у своего бота — иначе Telegram нам просто нечего показать.</summary>
    private async void TelegramDetect_Click(object? sender, RoutedEventArgs e)
    {
        SaveTelegramToken();
        TelegramDetectButton.IsEnabled = false;
        TelegramStatusText.Text = Tr.T("Спрашиваю Telegram…", "Telegram'дан суралууда…", "Asking Telegram…", "Telegram'a soruluyor…", "Telegram'dan so'ralmoqda…");
        try
        {
            var (chatId, name, error) = await TelegramBotService
                .TryDetectChatIdAsync(UserPreferences.Instance.TelegramBotToken ?? "")
                .ConfigureAwait(true);

            if (error is not null || chatId is null)
            {
                TelegramStatusText.Text = error ?? Tr.T("Не удалось определить получателя.", "Алуучуну аныктоо мүмкүн болгон жок.", "Couldn't detect the recipient.", "Alıcı belirlenemedi.", "Qabul qiluvchini aniqlab bo'lmadi.");
                return;
            }

            var prefs = UserPreferences.Instance;
            prefs.TelegramChatId = chatId;
            prefs.TelegramChatTitle = name;
            prefs.SaveToDisk();
            UpdateTelegramStatus();
        }
        finally
        {
            TelegramDetectButton.IsEnabled = true;
        }
    }

    private async void TelegramTest_Click(object? sender, RoutedEventArgs e)
    {
        SaveTelegramToken();
        TelegramTestButton.IsEnabled = false;
        TelegramStatusText.Text = Tr.T("Отправляю…", "Жөнөтүлүүдө…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…");
        try
        {
            var shop = UserPreferences.Instance.StoreName;
            var error = await TelegramBotService
                .SendAsync($"<b>{shop}</b>\n\nПробное сообщение из кассы. Если вы его видите — бот настроен верно.")
                .ConfigureAwait(true);
            TelegramStatusText.Text = error ?? Tr.T("Отправлено — проверьте Telegram.", "Жөнөтүлдү — Telegram'ды текшериңиз.", "Sent — check Telegram.", "Gönderildi — Telegram'ı kontrol edin.", "Yuborildi — Telegram'ni tekshiring.");
        }
        finally
        {
            TelegramTestButton.IsEnabled = true;
        }
    }

    private void TelegramSummary_Changed(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var enabled = TelegramSummaryCheck.IsChecked == true;
        if (prefs.TelegramShiftSummaryEnabled == enabled)
            return;
        prefs.TelegramShiftSummaryEnabled = enabled;
        prefs.SaveToDisk();
    }

    /// <summary>Открывает мастер подключения бота. После закрытия перечитываем поля: мастер
    /// мог сохранить токен, получателя и переключатели, и настройки не должны показывать
    /// устаревшие значения.</summary>
    private async void TelegramWizard_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new TelegramBotSetupWindow();

        if (owner != null)
            await window.ShowDialog(owner).ConfigureAwait(true);
        else
            window.Show();

        var prefs = UserPreferences.Instance;
        TelegramTokenBox.Text = prefs.TelegramBotToken ?? "";
        TelegramSummaryCheck.IsChecked = prefs.TelegramShiftSummaryEnabled;
        TelegramCommandsCheck.IsChecked = prefs.TelegramCommandsEnabled;
        UpdateTelegramStatus();
    }

    private void TelegramCommands_Changed(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var enabled = TelegramCommandsCheck.IsChecked == true;
        if (prefs.TelegramCommandsEnabled == enabled)
            return;

        prefs.TelegramCommandsEnabled = enabled;
        prefs.SaveToDisk();

        // Опрос поднимаем (или гасим) сразу, не дожидаясь перезапуска кассы: владелец только
        // что включил функцию и ждёт, что бот начнёт отвечать прямо сейчас.
        App.GetRequiredService<MainWindowHostBridge>().Window?.StartTelegramBot();
    }

    /// <summary>Ручная рассылка напоминаний. Уходит только подписавшимся на бота — Telegram не
    /// позволяет написать человеку по номеру телефона (см. TelegramBotPollingService).</summary>
    private async void TelegramDebtReminders_Click(object? sender, RoutedEventArgs e)
    {
        if (!TelegramBotService.IsConfigured)
        {
            TelegramStatusText.Text = Tr.T(
                "Сначала подключите бота: вставьте токен и определите получателя.",
                "Адегенде ботту туташтырыңыз: токенди коюп, алуучуну аныктаңыз.",
                "Connect the bot first: paste the token and detect the recipient.",
                "Önce botu bağlayın: token'ı yapıştırın ve alıcıyı belirleyin.",
                "Avval botni ulang: tokenni qo'ying va qabul qiluvchini aniqlang.");
            return;
        }

        TelegramDebtRemindersButton.IsEnabled = false;
        TelegramStatusText.Text = Tr.T("Рассылаю напоминания…", "Эскертмелер жөнөтүлүүдө…", "Sending reminders…", "Hatırlatmalar gönderiliyor…", "Eslatmalar yuborilmoqda…");
        try
        {
            // 2026-09-28: должники и chat_id — со сводки сервера (BE-03/BE-04), старый путь — запасной.
            var bot = new TelegramBotPollingService(
                App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>(),
                App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>(),
                App.GetRequiredService<NurMarketKassa.Services.Api.ClientDebtsApiService>());

            var sent = await bot.SendDebtRemindersAsync().ConfigureAwait(true);
            TelegramStatusText.Text = sent > 0
                ? Tr.T($"Отправлено напоминаний: {sent}.", $"Жөнөтүлгөн эскертмелер: {sent}.", $"Reminders sent: {sent}.", $"Gönderilen hatırlatma sayısı: {sent}.", $"Yuborilgan eslatmalar: {sent}.")
                : Tr.T(
                    "Некому отправлять: должники не подписаны на бота. Список со ссылками WhatsApp пришлёт команда /dolgi.",
                    "Алуучулар жок: карызкорлор ботко жазылышкан эмес. WhatsApp шилтемелери бар тизмени /dolgi буйругу жөнөтөт.",
                    "No one to send to: the debtors haven't subscribed to the bot. The /dolgi command will send you a list with WhatsApp links.",
                    "Gönderilecek kimse yok: borçlular bota abone olmamış. WhatsApp bağlantılı listeyi /dolgi komutu gönderir.",
                    "Eslatma yuboriladigan hech kim yo'q: qarzdorlar botga obuna bo'lmagan. WhatsApp havolalari bilan ro'yxatni /dolgi buyrug'i yuboradi.");
        }
        catch (Exception ex)
        {
            TelegramStatusText.Text = Tr.T("Не удалось разослать напоминания: ", "Эскертмелерди жөнөтүү мүмкүн болгон жок: ", "Couldn't send reminders: ", "Hatırlatmalar gönderilemedi: ", "Eslatmalarni yuborib bo'lmadi: ") + ex.Message;
        }
        finally
        {
            TelegramDebtRemindersButton.IsEnabled = true;
        }
    }

    /// <summary>2026-09-30: ключ ИИ-помощника бота (Google Gemini, бесплатный). Пустое поле — ИИ выключен.</summary>
    private void TelegramAiKey_LostFocus(object? sender, RoutedEventArgs e) => SaveTelegramAiKey();

    private void SaveTelegramAiKey()
    {
        var prefs = UserPreferences.Instance;
        var key = (TelegramAiKeyBox.Text ?? "").Trim();
        if ((prefs.TelegramAiKey ?? "") == key)
            return;
        prefs.TelegramAiKey = string.IsNullOrEmpty(key) ? null : key;
        prefs.SaveToDisk();
    }

    private void TelegramAiGetKey_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://aistudio.google.com/app/apikey") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TelegramAiStatusText.Text = "https://aistudio.google.com/app/apikey — " + ex.Message;
        }
    }

    private async void TelegramAiTest_Click(object? sender, RoutedEventArgs e)
    {
        SaveTelegramAiKey();
        var key = UserPreferences.Instance.TelegramAiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            TelegramAiStatusText.Text = Tr.T("Вставьте ключ Google.", "Google ачкычын коюңуз.", "Paste the Google key.", "Google anahtarını yapıştırın.", "Google kalitini qo'ying.");
            return;
        }

        TelegramAiTestButton.IsEnabled = false;
        TelegramAiStatusText.Text = Tr.T("Проверяю…", "Текшерилүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…");
        try
        {
            var (ok, message) = await TelegramAiChat.TestKeyAsync(key!, CancellationToken.None).ConfigureAwait(true);
            TelegramAiStatusText.Text = ok
                ? Tr.T("ИИ отвечает: ", "ЖИ жооп берет: ", "AI replies: ", "YZ yanıt veriyor: ", "SI javob bermoqda: ") + message
                : Tr.T("Не получилось: ", "Болбоду: ", "Failed: ", "Olmadı: ", "Bo'lmadi: ") + message;
        }
        finally
        {
            TelegramAiTestButton.IsEnabled = true;
        }
    }

    /// <summary>2026-10-05, владелец: «где вставить ключ ИИ? тут нету». Ключи Groq и OpenRouter — как ключ Gemini:
    /// сохраняются при уходе из поля, «Проверить» — запрос к модели (AiProviders). Пустое поле — выключено.</summary>
    private void GroqKey_LostFocus(object? sender, RoutedEventArgs e) => SaveProviderKey(GroqKeyBox, isGroq: true);

    private void OpenRouterKey_LostFocus(object? sender, RoutedEventArgs e) => SaveProviderKey(OpenRouterKeyBox, isGroq: false);

    private static void SaveProviderKey(TextBox box, bool isGroq)
    {
        var prefs = UserPreferences.Instance;
        var key = (box.Text ?? "").Trim();
        if (((isGroq ? prefs.GroqApiKey : prefs.OpenRouterApiKey) ?? "") == key)
            return;
        if (isGroq)
            prefs.GroqApiKey = string.IsNullOrEmpty(key) ? null : key;
        else
            prefs.OpenRouterApiKey = string.IsNullOrEmpty(key) ? null : key;
        prefs.SaveToDisk();
    }

    private void GroqGetKey_Click(object? sender, RoutedEventArgs e) => OpenKeyPage("https://console.groq.com/keys", GroqStatusText);

    private void OpenRouterGetKey_Click(object? sender, RoutedEventArgs e) => OpenKeyPage("https://openrouter.ai/keys", OpenRouterStatusText);

    private static void OpenKeyPage(string url, TextBlock status)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            status.Text = url + " — " + ex.Message;
        }
    }

    private async void GroqTest_Click(object? sender, RoutedEventArgs e) =>
        await TestProviderKeyAsync(GroqKeyBox, GroqTestButton, GroqStatusText, isGroq: true).ConfigureAwait(true);

    private async void OpenRouterTest_Click(object? sender, RoutedEventArgs e) =>
        await TestProviderKeyAsync(OpenRouterKeyBox, OpenRouterTestButton, OpenRouterStatusText, isGroq: false).ConfigureAwait(true);

    private static async Task TestProviderKeyAsync(TextBox box, Button button, TextBlock status, bool isGroq)
    {
        SaveProviderKey(box, isGroq);
        var key = isGroq ? UserPreferences.Instance.GroqApiKey : UserPreferences.Instance.OpenRouterApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            status.Text = Tr.T("Вставьте ключ.", "Ачкычты коюңуз.", "Paste the key.", "Anahtarı yapıştırın.", "Kalitni qo'ying.");
            return;
        }

        button.IsEnabled = false;
        status.Text = Tr.T("Проверяю…", "Текшерилүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…");
        try
        {
            var (ok, message) = isGroq
                ? await AiProviders.TestGroqAsync(key!, CancellationToken.None).ConfigureAwait(true)
                : await AiProviders.TestOpenRouterAsync(key!, CancellationToken.None).ConfigureAwait(true);
            status.Text = (ok ? "✓ " : Tr.T("Не получилось: ", "Болбоду: ", "Failed: ", "Olmadı: ", "Bo'lmadi: ")) + message;
            PosLogger.Log($"Настройки: ключ {(isGroq ? "Groq" : "OpenRouter")} {(ok ? "работает" : "не подошёл")}.", "INFO");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OwnerPhone_LostFocus(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var phone = (OwnerPhoneBox.Text ?? "").Trim();
        if (prefs.OwnerPhone == phone)
            return;
        prefs.OwnerPhone = phone;
        prefs.SaveToDisk();
    }

    /// <summary>Переключатель «QR с суммой чека». По умолчанию выключен: формула контрольной
    /// суммы ELQR сверена с настоящими QR MBank, но приложения других банков на таком коде не
    /// проверялись — включать осознанно, после пробного сканирования.</summary>
    private void DynamicQr_Changed(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var enabled = DynamicQrCheck.IsChecked == true;
        if (prefs.DynamicPaymentQrEnabled == enabled)
            return;
        prefs.DynamicPaymentQrEnabled = enabled;
        prefs.SaveToDisk();
    }


    /// <summary>Сохраняет отметки «показывать при оплате». Пишем весь список целиком, а не
    /// по одной записи: так в настройках не остаётся банков, которые владелец уже снял.</summary>
    private void BankRow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BankQrSetting.ShowAtCheckout) || _bankSettings is null)
            return;

        var prefs = UserPreferences.Instance;
        prefs.VisibleBankNames = _bankSettings
            .Where(x => x.ShowAtCheckout)
            .Select(x => x.BankName)
            .ToList();
        prefs.SaveToDisk();
    }

    private void AddBank_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var name = AddBankDialog.Show(owner, KyrgyzBanks.All
            .Where(x => !_banks.Contains(x, StringComparer.OrdinalIgnoreCase)
                && !UserPreferences.Instance.CustomBankNames.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToList());
        if (string.IsNullOrWhiteSpace(name))
            return;

        var prefs = UserPreferences.Instance;
        bool alreadyExists = _banks.Contains(name, StringComparer.OrdinalIgnoreCase)
            || prefs.CustomBankNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (alreadyExists)
        {
            PosMessageBox.Show(
                Tr.T("Банк с таким названием уже есть в списке.", "Мындай аталыштагы банк тизмеде бар.", "A bank with this name is already on the list.", "Bu adda bir banka listede zaten var.", "Bunday nomli bank ro'yxatda allaqachon bor."),
                Tr.T("Новый банк", "Жаңы банк", "New bank", "Yeni banka", "Yangi bank"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        prefs.CustomBankNames.Add(name);
        if (!prefs.VisibleBankNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            // Банк добавляют, чтобы им платить. Заставлять после этого искать галочку —
            // лишний шаг, который владелец пропустит и решит, что добавление не сработало.
            if (prefs.VisibleBankNames.Count == 0)
                prefs.VisibleBankNames = KyrgyzBanks.DefaultVisible.ToList();
            prefs.VisibleBankNames.Add(name);
        }
        prefs.SaveToDisk();
        LoadBankQrSettings();
    }

    private void RemoveBank_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting } || !setting.IsCustom)
            return;

        if (PosMessageBox.Show(
                Tr.T(
                    $"Удалить банк {setting.BankName} из списка вместе с загруженным QR-кодом?",
                    $"{setting.BankName} банкын жүктөлгөн QR-коду менен кошо тизмеден өчүрөсүзбү?",
                    $"Remove bank {setting.BankName} from the list along with its uploaded QR code?",
                    $"{setting.BankName} bankası, yüklenen QR koduyla birlikte listeden silinsin mi?",
                    $"{setting.BankName} banki yuklangan QR-kodi bilan birga ro'yxatdan o'chirilsinmi?"),
                Tr.T("Удаление банка", "Банкты өчүрүү", "Remove bank", "Bankayı sil", "Bankni o'chirish"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var prefs = UserPreferences.Instance;
        string? previousQrPath = setting.QrCodePath;
        prefs.CustomBankNames.RemoveAll(b => string.Equals(b, setting.BankName, StringComparison.OrdinalIgnoreCase));
        prefs.BankQrPaths?.Remove(setting.BankName);
        prefs.BankLogoPaths?.Remove(setting.BankName);
        prefs.SaveToDisk();
        DeleteManagedQrFile(previousQrPath);
        LoadBankQrSettings();
    }

    private async void ChangeLogo_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting })
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T($"Выберите логотип для {setting.BankName}", $"{setting.BankName} үчүн логотип тандаңыз", $"Select a logo for {setting.BankName}", $"{setting.BankName} için logo seçin", $"{setting.BankName} uchun logotip tanlang"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Tr.T("Изображения", "Сүрөттөр", "Images", "Görseller", "Rasmlar")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
            return;

        setting.LogoPath = path;

        var prefs = UserPreferences.Instance;
        prefs.BankLogoPaths ??= new Dictionary<string, string>();
        prefs.BankLogoPaths[setting.BankName] = path;
        prefs.SaveToDisk();
    }

    private async void LoadQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BankQrSetting setting)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T($"Выберите QR-код для банка {setting.BankName}", $"{setting.BankName} банкы үчүн QR-код тандаңыз", $"Select a QR code for {setting.BankName}", $"{setting.BankName} bankası için QR kodu seçin", $"{setting.BankName} banki uchun QR-kod tanlang"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Tr.T("Изображения", "Сүрөттөр", "Images", "Görseller", "Rasmlar")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
            return;

        if (topLevel is Window owner)
            await OpenQrEditorAsync(owner, setting, path);
    }

    private async void EditQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting }
            || string.IsNullOrWhiteSpace(setting.QrCodePath)
            || !File.Exists(setting.QrCodePath)
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        await OpenQrEditorAsync(owner, setting, setting.QrCodePath);
    }

    private void RemoveQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting })
            return;

        if (PosMessageBox.Show(
                Tr.T($"Убрать QR-код банка {setting.BankName}?", $"{setting.BankName} банкынын QR-кодун өчүрөсүзбү?", $"Remove the QR code for {setting.BankName}?", $"{setting.BankName} bankasının QR kodu kaldırılsın mı?", $"{setting.BankName} bankining QR-kodi olib tashlansinmi?"),
                Tr.T("Удаление QR-кода", "QR-кодду өчүрүү", "Remove QR code", "QR kodunu kaldır", "QR-kodni olib tashlash"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        string? previousPath = setting.QrCodePath;
        setting.QrCodePath = null;
        SaveBankQrSettings();
        DeleteManagedQrFile(previousPath);
    }

    private async Task OpenQrEditorAsync(Window owner, BankQrSetting setting, string sourcePath)
    {
        string? previousPath = setting.QrCodePath;
        var dialog = new QrCropDialog(sourcePath, setting.BankName);
        string? editedPath = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(editedPath))
            return;

        setting.QrCodePath = editedPath;
        SaveBankQrSettings();

        if (!string.Equals(previousPath, editedPath, StringComparison.OrdinalIgnoreCase))
            DeleteManagedQrFile(previousPath);
    }

    private static void DeleteManagedQrFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            string managedDirectory = Path.GetFullPath(QrCropDialog.GetManagedQrDirectory())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            if (candidate.StartsWith(managedDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                File.Delete(candidate);
        }
        catch
        {
            // The preference is already removed; failure to clean an old managed copy is harmless.
        }
    }

    private void SaveBankQrSettings()
    {
        var prefs = UserPreferences.Instance;
        prefs.BankQrPaths ??= new Dictionary<string, string>();
        prefs.BankQrPaths.Clear();
        foreach (var bs in _bankSettings)
        {
            if (!string.IsNullOrEmpty(bs.QrCodePath))
                prefs.BankQrPaths[bs.BankName] = bs.QrCodePath;
        }

        prefs.SaveToDisk();

        // Экран покупателя кеширует InformationImagePath до следующего события
        // (продажа/статус оплаты). Без явного пинка новый QR не появится там,
        // пока не пройдёт следующая продажа.
        _ = App.GetRequiredService<AvaloniaCustomerDisplayService>().ApplySettingsAsync();
    }
}
