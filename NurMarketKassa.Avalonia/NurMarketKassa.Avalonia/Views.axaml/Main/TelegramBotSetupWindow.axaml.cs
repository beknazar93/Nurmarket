using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// Мастер подключения телеграм-бота: от создания у @BotFather до пробного сообщения.
///
/// Зачем отдельное окно, а не поля в настройках: без объяснения порядка действий владелец
/// упирался в то, что токен вставлен, а сводка не приходит — потому что Telegram не даёт боту
/// написать первым, и сначала нужно самому нажать «Старт» у своего бота. Здесь шаги идут по
/// порядку и каждый следующий включается только после того, как предыдущий действительно
/// выполнен.
///
/// Создать бота из программы нельзя в принципе: @BotFather — обычный чат в Telegram, API для
/// регистрации ботов у него нет ни у кого. Поэтому первый шаг открывает чат, а дальше касса
/// делает всё сама.
/// </summary>
public partial class TelegramBotSetupWindow : Window
{
    private string? _botUsername;

    public TelegramBotSetupWindow()
    {
        InitializeComponent();
    }

    private bool _fillingChecks;

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        TokenBox.Text = prefs.TelegramBotToken ?? "";
        // 2026-09-30: галочки ставятся из настроек БЕЗ сохранения. Раньше первая же галочка вызывала
        // Toggles_Changed, и тот записывал вторую (ещё пустую) — простое открытие мастера выключало
        // «Отвечать на команды», и бот переставал отвечать.
        _fillingChecks = true;
        SummaryCheck.IsChecked = prefs.TelegramShiftSummaryEnabled;
        CommandsCheck.IsChecked = prefs.TelegramCommandsEnabled;
        _fillingChecks = false;

        // Бот уже подключён — открываем окно сразу в «рабочем» состоянии, чтобы владелец мог
        // проверить связь или переключить настройки, не проходя шаги заново.
        _botUsername = prefs.TelegramBotUsername;
        if (!string.IsNullOrWhiteSpace(_botUsername))
        {
            OpenMyBotButton.IsEnabled = true;
            DetectButton.IsEnabled = true;
            TokenStatus.Text = Tr.T($"Бот подключён: @{_botUsername}", $"Бот туташтырылды: @{_botUsername}", $"Bot connected: @{_botUsername}", $"Bot bağlandı: @{_botUsername}", $"Bot ulandi: @{_botUsername}");
        }

        if (!string.IsNullOrWhiteSpace(prefs.TelegramChatId))
        {
            TestButton.IsEnabled = true;
            DetectStatus.Text = string.IsNullOrWhiteSpace(prefs.TelegramChatTitle)
                ? Tr.T($"Получатель определён (чат {prefs.TelegramChatId}).", $"Алуучу аныкталды (чат {prefs.TelegramChatId}).", $"Recipient detected (chat {prefs.TelegramChatId}).", $"Alıcı belirlendi (sohbet {prefs.TelegramChatId}).", $"Qabul qiluvchi aniqlandi (chat {prefs.TelegramChatId}).")
                : Tr.T($"Получатель: {prefs.TelegramChatTitle}.", $"Алуучу: {prefs.TelegramChatTitle}.", $"Recipient: {prefs.TelegramChatTitle}.", $"Alıcı: {prefs.TelegramChatTitle}.", $"Qabul qiluvchi: {prefs.TelegramChatTitle}.");
        }

        ApplyServerTexts();
        _ = RefreshServerStatusAsync();
    }

    // ------------------------------------------------------------------ бот на сервере (ТЗ часть 5)

    private ServerTelegramBotApi? ServerApi =>
        App.AppHost?.Services.GetService(typeof(ServerTelegramBotApi)) as ServerTelegramBotApi;

    private void ApplyServerTexts()
    {
        ServerTitle.Text = Tr.T("Бот на сервере — работает всегда", "Сервердеги бот — дайыма иштейт", "Bot on the server — always on",
            "Sunucudaki bot — her zaman açık", "Serverdagi bot — doim ishlaydi");
        ServerHint.Text = Tr.T(
            "Сейчас бот отвечает, только пока на этом компьютере открыта касса или программа владельца. Перенесите его на сервер NurCRM — он будет отвечать круглые сутки, даже когда компьютер выключен: команды, отчёты, ИИ, консультант для покупателей, заказы и сводка по закрытию смены. Токен и ключ ИИ хранятся на сервере в зашифрованном виде.",
            "Азыр бот бул компьютерде касса же ээсинин программасы ачык турганда гана жооп берет. Аны NurCRM серверине көчүрүңүз — компьютер өчүк болсо да күнү-түнү жооп берет: буйруктар, отчёттор, ЖИ, сатып алуучулар үчүн кеңешчи, заказдар жана смена жабылганда жыйынтык. Токен жана ЖИ ачкычы серверде шифрленип сакталат.",
            "Right now the bot answers only while the kassa or the owner app is open on this computer. Move it to the NurCRM server and it will answer around the clock, even when the computer is off: commands, reports, AI, the customer consultant, orders and the shift-close summary. The token and the AI key are stored encrypted on the server.",
            "Şu anda bot yalnızca bu bilgisayarda kasa veya işletme sahibi programı açıkken yanıt veriyor. NurCRM sunucusuna taşıyın — bilgisayar kapalıyken bile günün her saati yanıt verir: komutlar, raporlar, yapay zekâ, müşteri danışmanı, siparişler ve vardiya kapanış özeti. Token ve yapay zekâ anahtarı sunucuda şifreli saklanır.",
            "Hozir bot faqat shu kompyuterda kassa yoki ega dasturi ochiq bo'lganda javob beradi. Uni NurCRM serveriga ko'chiring — kompyuter o'chiq bo'lsa ham kecha-kunduz javob beradi: buyruqlar, hisobotlar, SI, xaridorlar uchun maslahatchi, buyurtmalar va smena yopilganda hisobot. Token va SI kaliti serverda shifrlangan holda saqlanadi.");
        ServerMoveButton.Content = Tr.T("Перенести бота на сервер", "Ботту серверге көчүрүү", "Move the bot to the server", "Botu sunucuya taşı", "Botni serverga ko'chirish");
        ServerTestButton.Content = Tr.T("Пробное сообщение с сервера", "Серверден сынак билдирүү", "Test message from the server", "Sunucudan deneme mesajı", "Serverdan sinov xabari");
        ServerTestAiButton.Content = Tr.T("Проверить ИИ на сервере", "Сервердеги ЖИни текшерүү", "Check AI on the server", "Sunucudaki yapay zekâyı kontrol et", "Serverdagi SIni tekshirish");
        ServerBackButton.Content = Tr.T("Вернуть на этот компьютер", "Бул компьютерге кайтаруу", "Move back to this computer", "Bu bilgisayara geri al", "Shu kompyuterga qaytarish");
    }

    private async Task RefreshServerStatusAsync()
    {
        var api = ServerApi;
        if (api is null)
            return;
        try
        {
            ShowServerStatus(await api.GetSettingsAsync().ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            ServerStatus.Text = Tr.T("Сервер не ответил: ", "Сервер жооп берген жок: ", "The server did not respond: ", "Sunucu yanıt vermedi: ", "Server javob bermadi: ")
                                + ServerTelegramBotApi.Describe(ex);
        }
    }

    private void ShowServerStatus(ServerBotSettings? s)
    {
        if (s is null)
        {
            ServerStatus.Text = Tr.T("Сервер NurCRM пока не поддерживает бота.", "NurCRM сервери азырынча ботту колдобойт.", "The NurCRM server does not support the bot yet.",
                "NurCRM sunucusu henüz botu desteklemiyor.", "NurCRM serveri hali botni qo'llab-quvvatlamaydi.");
            ServerMoveButton.IsEnabled = false;
            return;
        }

        var on = s.IsServerMode;
        ServerTestButton.IsEnabled = on;
        ServerTestAiButton.IsEnabled = on && s.AiKeySet;
        ServerBackButton.IsEnabled = s.TokenSet;
        ServerMoveButton.Content = on
            ? Tr.T("Обновить настройки на сервере", "Сервердеги жөндөөлөрдү жаңыртуу", "Update the settings on the server", "Sunucudaki ayarları güncelle", "Serverdagi sozlamalarni yangilash")
            : Tr.T("Перенести бота на сервер", "Ботту серверге көчүрүү", "Move the bot to the server", "Botu sunucuya taşı", "Botni serverga ko'chirish");
        if (!on)
        {
            // 2026-10-02, владелец: «на сервер выгрузи всё равно бота и ИИ — на сервере бот работал, просто
            // долго». Перенос доступен всегда; скорость ответа сервера — ТЗ часть 7, раздел 1.
            ServerMoveButton.IsEnabled = true;
            ServerStatus.Text = Tr.T("Бот работает на этом компьютере.", "Бот бул компьютерде иштейт.", "The bot runs on this computer.", "Bot bu bilgisayarda çalışıyor.", "Bot shu kompyuterda ishlaydi.");
            return;
        }

        var bot = string.IsNullOrWhiteSpace(s.BotUsername) ? "" : " @" + s.BotUsername;
        var webhook = s.WebhookOk
            ? Tr.T("связь с Telegram в порядке", "Telegram менен байланыш жакшы", "Telegram connection OK", "Telegram bağlantısı tamam", "Telegram bilan aloqa yaxshi")
            : Tr.T("нет связи с Telegram", "Telegram менен байланыш жок", "no Telegram connection", "Telegram bağlantısı yok", "Telegram bilan aloqa yo'q") + (string.IsNullOrWhiteSpace(s.WebhookError) ? "" : $" ({s.WebhookError})");
        var ai = s.AiKeySet
            ? Tr.T("ИИ подключён", "ЖИ туташкан", "AI connected", "Yapay zekâ bağlı", "SI ulangan")
            : Tr.T("ИИ без ключа", "ЖИ ачкычсыз", "AI has no key", "Yapay zekâ anahtarı yok", "SI kalitsiz");
        var owner = string.IsNullOrWhiteSpace(s.OwnerChatId)
            ? Tr.T("получатель не задан — напишите боту /start и нажмите «Перенести» ещё раз", "алуучу коюлган эмес — ботко /start жазып, «Көчүрүү» дегенди кайра басыңыз",
                "no recipient — send /start to the bot and press “Move” again", "alıcı yok — bota /start yazıp «Taşı»ya yeniden basın", "qabul qiluvchi yo'q — botga /start yozing va «Ko'chirish»ni qayta bosing")
            : Tr.T("получатель задан", "алуучу коюлган", "recipient set", "alıcı ayarlı", "qabul qiluvchi belgilangan");
        if (s.IsStuck)
        {
            // 2026-10-01: сервер принимает сообщения, но не отвечает (ТЗ часть 7, раздел 1).
            ServerStatus.Text = Tr.T($"⚠ Бот{bot} на сервере не отвечает на сообщения больше 2 минут. Напишите в поддержку NurCRM; пока можно нажать «Вернуть на этот компьютер».",
                $"⚠ Серверде бот{bot} 2 мүнөттөн ашык билдирүүлөргө жооп бербей жатат. NurCRM колдоосуна жазыңыз; азырынча «Бул компьютерге кайтаруу» дегенди басса болот.",
                $"⚠ Bot{bot} on the server has not answered messages for over 2 minutes. Contact NurCRM support; for now you can press “Move back to this computer”.",
                $"⚠ Sunucudaki bot{bot} 2 dakikadan uzun süredir mesajlara yanıt vermiyor. NurCRM desteğine yazın; şimdilik «Bu bilgisayara geri al»a basabilirsiniz.",
                $"⚠ Serverdagi bot{bot} 2 daqiqadan ko'proq xabarlarga javob bermayapti. NurCRM qo'llab-quvvatlashiga yozing; hozircha «Shu kompyuterga qaytarish»ni bosish mumkin.");
            return;
        }
        ServerStatus.Text = Tr.T($"✅ Бот{bot} работает на сервере круглые сутки: {webhook}, {ai}, {owner}.",
            $"✅ Бот{bot} серверде күнү-түнү иштейт: {webhook}, {ai}, {owner}.",
            $"✅ Bot{bot} runs on the server around the clock: {webhook}, {ai}, {owner}.",
            $"✅ Bot{bot} sunucuda günün her saati çalışıyor: {webhook}, {ai}, {owner}.",
            $"✅ Bot{bot} serverda kecha-kunduz ishlaydi: {webhook}, {ai}, {owner}.");
    }

    /// <summary>Передаёт серверу токен, получателя, ключ ИИ и переключатели и ставит режим «server»:
    /// сервер сам вызывает setWebhook, а опрос в программе выключается (TelegramBotPollingService).</summary>
    private async void ServerMove_Click(object? sender, RoutedEventArgs e)
    {
        var api = ServerApi;
        var prefs = UserPreferences.Instance;
        if (api is null)
            return;
        if (string.IsNullOrWhiteSpace(prefs.TelegramBotToken))
        {
            ServerActionStatus.Text = Tr.T("Сначала подключите бота (шаги 1–2).", "Адегенде ботту туташтырыңыз (1–2-кадамдар).", "Connect the bot first (steps 1–2).",
                "Önce botu bağlayın (1–2. adımlar).", "Avval botni ulang (1–2-qadamlar).");
            return;
        }

        ServerMoveButton.IsEnabled = false;
        ServerActionStatus.Text = Tr.T("Переношу на сервер…", "Серверге көчүрүлүүдө…", "Moving to the server…", "Sunucuya taşınıyor…", "Serverga ko'chirilmoqda…");
        try
        {
            var settings = await api.MoveCurrentBotToServerAsync().ConfigureAwait(true);
            PosLogger.Log($"Телеграм-бот перенесён на сервер NurCRM (вебхук {(settings?.WebhookOk == true ? "ок" : "нет")}).", "TELEGRAM");
            ShowServerStatus(settings);
            ServerActionStatus.Text = Tr.T("Готово. Опрос в программе выключается сам — дальше отвечает сервер.", "Даяр. Программадагы сурамжылоо өзү өчөт — мындан ары сервер жооп берет.",
                "Done. Polling in this program turns off by itself — the server answers from now on.", "Tamam. Bu programdaki sorgulama kendiliğinden kapanır — artık sunucu yanıt verir.",
                "Tayyor. Dasturdagi so'rov o'zi o'chadi — endi server javob beradi.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: перенос на сервер не удался: {ServerTelegramBotApi.Describe(ex)}", "WARNING");
            ServerActionStatus.Text = Tr.T("Не удалось: ", "Болбой калды: ", "Failed: ", "Başarısız: ", "Bo'lmadi: ") + ServerTelegramBotApi.Describe(ex);
        }
        finally
        {
            ServerMoveButton.IsEnabled = true;
        }
    }

    private async void ServerTest_Click(object? sender, RoutedEventArgs e)
    {
        var api = ServerApi;
        if (api is null)
            return;
        ServerTestButton.IsEnabled = false;
        try
        {
            await api.TestMessageAsync().ConfigureAwait(true);
            ServerActionStatus.Text = Tr.T("Сервер отправил сообщение — проверьте Telegram.", "Сервер билдирүү жөнөттү — Telegram'ды текшериңиз.", "The server sent a message — check Telegram.",
                "Sunucu mesaj gönderdi — Telegram'ı kontrol edin.", "Server xabar yubordi — Telegram'ni tekshiring.");
        }
        catch (Exception ex)
        {
            ServerActionStatus.Text = Tr.T("Не удалось: ", "Болбой калды: ", "Failed: ", "Başarısız: ", "Bo'lmadi: ") + ServerTelegramBotApi.Describe(ex);
        }
        finally
        {
            ServerTestButton.IsEnabled = true;
        }
    }

    private async void ServerTestAi_Click(object? sender, RoutedEventArgs e)
    {
        var api = ServerApi;
        if (api is null)
            return;
        ServerTestAiButton.IsEnabled = false;
        ServerActionStatus.Text = Tr.T("Спрашиваю ИИ на сервере…", "Сервердеги ЖИден суралууда…", "Asking the AI on the server…", "Sunucudaki yapay zekâya soruluyor…", "Serverdagi SIdan so'ralmoqda…");
        try
        {
            var r = await api.TestAiAsync().ConfigureAwait(true);
            var answer = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("answer", out var a) ? a.ToString() : "";
            var model = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("model", out var m) ? m.ToString() : "";
            ServerActionStatus.Text = Tr.T("ИИ на сервере отвечает", "Сервердеги ЖИ жооп берет", "The AI on the server answers", "Sunucudaki yapay zekâ yanıt veriyor", "Serverdagi SI javob beradi")
                                      + (model.Length > 0 ? $" ({model})" : "") + (answer.Length > 0 ? ": " + answer : ".");
        }
        catch (Exception ex)
        {
            ServerActionStatus.Text = Tr.T("ИИ не ответил: ", "ЖИ жооп берген жок: ", "The AI did not answer: ", "Yapay zekâ yanıt vermedi: ", "SI javob bermadi: ") + ServerTelegramBotApi.Describe(ex);
        }
        finally
        {
            ServerTestAiButton.IsEnabled = true;
        }
    }

    /// <summary>Убрать бота с сервера (сервер снимает вебхук) — бот снова отвечает из программы.</summary>
    private async void ServerBack_Click(object? sender, RoutedEventArgs e)
    {
        var api = ServerApi;
        if (api is null)
            return;
        ServerBackButton.IsEnabled = false;
        try
        {
            var settings = await api.PatchSettingsAsync(new Dictionary<string, object?> { ["mode"] = "local", ["token"] = "" }).ConfigureAwait(true);
            PosLogger.Log("Телеграм-бот возвращён на этот компьютер (снят с сервера NurCRM).", "TELEGRAM");
            ShowServerStatus(settings);
            ServerActionStatus.Text = Tr.T("Бот снова работает на этом компьютере (опрос включится в течение 5 минут).", "Бот кайра бул компьютерде иштейт (сурамжылоо 5 мүнөттүн ичинде күйөт).",
                "The bot runs on this computer again (polling resumes within 5 minutes).", "Bot yeniden bu bilgisayarda çalışıyor (sorgulama 5 dakika içinde başlar).",
                "Bot yana shu kompyuterda ishlaydi (so'rov 5 daqiqa ichida yoqiladi).");
            StartBotIfEnabled();
        }
        catch (Exception ex)
        {
            ServerActionStatus.Text = Tr.T("Не удалось: ", "Болбой калды: ", "Failed: ", "Başarısız: ", "Bo'lmadi: ") + ServerTelegramBotApi.Describe(ex);
            ServerBackButton.IsEnabled = true;
        }
    }

    private void OpenBotFather_Click(object? sender, RoutedEventArgs e) => OpenUrl("https://t.me/BotFather");

    private void OpenMyBot_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_botUsername))
            OpenUrl($"https://t.me/{_botUsername}");
    }

    private async void CopyNewBot_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync("/newbot").ConfigureAwait(true);
    }

    private async void PasteToken_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        var text = await clipboard.GetTextAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(text))
            TokenBox.Text = text.Trim();
    }

    /// <summary>Проверяет токен через getMe и запоминает его. Пока токен не подтверждён, шаги 3
    /// и 4 заблокированы: без рабочего токена они всё равно ничего не сделают, а кассир получил
    /// бы невнятную ошибку.</summary>
    private async void CheckToken_Click(object? sender, RoutedEventArgs e)
    {
        var token = (TokenBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            TokenStatus.Text = Tr.T("Вставьте токен из чата с BotFather.", "BotFather чатынан алынган токенди коюңуз.", "Paste the token from the chat with BotFather.", "BotFather sohbetindeki token'ı yapıştırın.", "BotFather bilan chatdagi tokenni qo'ying.");
            return;
        }

        CheckTokenButton.IsEnabled = false;
        TokenStatus.Text = Tr.T("Проверяю токен…", "Токен текшерилүүдө…", "Checking the token…", "Token kontrol ediliyor…", "Token tekshirilmoqda…");
        try
        {
            var (username, title, error) = await TelegramBotService.GetBotInfoAsync(token).ConfigureAwait(true);
            if (error != null || string.IsNullOrWhiteSpace(username))
            {
                TokenStatus.Text = error ?? Tr.T("Telegram не принял токен.", "Telegram токенди кабыл алган жок.", "Telegram rejected the token.", "Telegram token'ı kabul etmedi.", "Telegram tokenni qabul qilmadi.");
                OpenMyBotButton.IsEnabled = false;
                DetectButton.IsEnabled = false;
                return;
            }

            var prefs = UserPreferences.Instance;
            prefs.TelegramBotToken = token;
            prefs.TelegramBotUsername = username;
            prefs.SaveToDisk();

            _botUsername = username;
            OpenMyBotButton.IsEnabled = true;
            DetectButton.IsEnabled = true;
            TokenStatus.Text = string.IsNullOrWhiteSpace(title)
                ? Tr.T($"Токен верный. Бот: @{username}", $"Токен туура. Бот: @{username}", $"Token is valid. Bot: @{username}", $"Token geçerli. Bot: @{username}", $"Token to'g'ri. Bot: @{username}")
                : Tr.T($"Токен верный. Бот: {title} (@{username})", $"Токен туура. Бот: {title} (@{username})", $"Token is valid. Bot: {title} (@{username})", $"Token geçerli. Bot: {title} (@{username})", $"Token to'g'ri. Bot: {title} (@{username})");
        }
        finally
        {
            CheckTokenButton.IsEnabled = true;
        }
    }

    private async void Detect_Click(object? sender, RoutedEventArgs e)
    {
        DetectButton.IsEnabled = false;
        DetectStatus.Text = Tr.T("Спрашиваю Telegram…", "Telegram'дан суралууда…", "Asking Telegram…", "Telegram'a soruluyor…", "Telegram'dan so'ralmoqda…");
        try
        {
            var (chatId, name, error) = await TelegramBotService
                .TryDetectChatIdAsync(UserPreferences.Instance.TelegramBotToken ?? "")
                .ConfigureAwait(true);

            if (error != null || chatId == null)
            {
                DetectStatus.Text = error ?? Tr.T("Не удалось определить получателя.", "Алуучуну аныктоо мүмкүн болгон жок.", "Could not detect the recipient.", "Alıcı belirlenemedi.", "Qabul qiluvchini aniqlab bo'lmadi.");
                return;
            }

            var prefs = UserPreferences.Instance;
            prefs.TelegramChatId = chatId;
            prefs.TelegramChatTitle = name;
            prefs.SaveToDisk();

            TestButton.IsEnabled = true;
            DetectStatus.Text = string.IsNullOrWhiteSpace(name)
                ? Tr.T($"Готово: сводки пойдут в чат {chatId}.", $"Даяр: жыйынтыктар {chatId} чатына жөнөтүлөт.", $"Done: summaries will go to chat {chatId}.", $"Hazır: özetler {chatId} sohbetine gönderilecek.", $"Tayyor: hisobotlar {chatId} chatiga yuboriladi.")
                : Tr.T($"Готово: сводки пойдут в чат «{name}».", $"Даяр: жыйынтыктар «{name}» чатына жөнөтүлөт.", $"Done: summaries will go to the “{name}” chat.", $"Hazır: özetler «{name}» sohbetine gönderilecek.", $"Tayyor: hisobotlar «{name}» chatiga yuboriladi.");

            StartBotIfEnabled();
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestStatus.Text = Tr.T("Отправляю…", "Жөнөтүлүүдө…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…");
        try
        {
            var shop = UserPreferences.Instance.StoreName;
            var error = await TelegramBotService
                .SendAsync($"<b>{shop}</b>\n\nПробное сообщение из кассы. Если вы его видите — бот подключён.\n\n"
                           + "Попробуйте команду /segodnya.")
                .ConfigureAwait(true);
            TestStatus.Text = error ?? Tr.T("Отправлено — проверьте Telegram.", "Жөнөтүлдү — Telegram'ды текшериңиз.", "Sent — check Telegram.", "Gönderildi — Telegram'ı kontrol edin.", "Yuborildi — Telegram'ni tekshiring.");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Toggles_Changed(object? sender, RoutedEventArgs e)
    {
        // Loaded ещё не отработал — не перезаписываем настройки значениями по умолчанию.
        if (!IsLoaded || _fillingChecks)
            return;

        var prefs = UserPreferences.Instance;
        prefs.TelegramShiftSummaryEnabled = SummaryCheck.IsChecked == true;
        prefs.TelegramCommandsEnabled = CommandsCheck.IsChecked == true;
        prefs.SaveToDisk();
        StartBotIfEnabled();
    }

    private static void StartBotIfEnabled()
    {
        try
        {
            App.GetRequiredService<MainWindowHostBridge>().Window?.StartTelegramBot();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: перезапуск из мастера не удался ({ex.Message}).", "WARNING");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось открыть ссылку {url}: {ex.Message}", "WARNING");
        }
    }
}
