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
        ApplyOrdersGroupTexts();
        RefreshOrdersGroup();
        TelegramBotOwner.OrdersGroupChanged += OnOrdersGroupChanged;
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
        ServerAiGetKeyButton.Content = Tr.T("Получить ключ (бесплатно)", "Ачкыч алуу (акысыз)", "Get a key (free)", "Anahtar al (ücretsiz)", "Kalit olish (bepul)");
    }

    /// <summary>2026-10-04: подпись и кнопка блока «ИИ на сервере» — по тому, есть ли ключ на сервере.</summary>
    private void ShowServerAi(bool serverMode, bool keySet)
    {
        ServerAiPanel.IsVisible = serverMode;
        ServerAiHint.Text = keySet
            ? Tr.T("ИИ на сервере подключён. Чтобы заменить ключ Google Gemini, вставьте новый и нажмите «Заменить ключ ИИ».",
                "Сервердеги ЖИ туташкан. Google Gemini ачкычын алмаштыруу үчүн жаңысын коюп, «ЖИ ачкычын алмаштыруу» дегенди басыңыз.",
                "The AI on the server is connected. To replace the Google Gemini key, paste a new one and press “Replace AI key”.",
                "Sunucudaki yapay zekâ bağlı. Google Gemini anahtarını değiştirmek için yenisini yapıştırıp «Yapay zekâ anahtarını değiştir»e basın.",
                "Serverdagi SI ulangan. Google Gemini kalitini almashtirish uchun yangisini qo'yib, «SI kalitini almashtirish»ni bosing.")
            : Tr.T("ИИ отвечает покупателям на вопросы о товарах и помогает оформить заказ. Нужен бесплатный ключ Google Gemini: нажмите «Получить ключ», войдите в Google, нажмите «Create API key», скопируйте ключ и вставьте сюда.",
                "ЖИ сатып алуучулардын товарлар боюнча суроолоруна жооп берет жана заказ тариздөөгө жардам берет. Акысыз Google Gemini ачкычы керек: «Ачкыч алуу» дегенди басыңыз, Google'га кириңиз, «Create API key» басыңыз, ачкычты көчүрүп, бул жерге коюңуз.",
                "The AI answers customers' questions about products and helps place orders. You need a free Google Gemini key: press “Get a key”, sign in to Google, press “Create API key”, copy the key and paste it here.",
                "Yapay zekâ müşterilerin ürün sorularını yanıtlar ve sipariş vermeye yardım eder. Ücretsiz bir Google Gemini anahtarı gerekir: «Anahtar al»a basın, Google'a giriş yapın, «Create API key»e basın, anahtarı kopyalayıp buraya yapıştırın.",
                "SI xaridorlarning mahsulotlar haqidagi savollariga javob beradi va buyurtma berishga yordam beradi. Bepul Google Gemini kaliti kerak: «Kalit olish»ni bosing, Google'ga kiring, «Create API key»ni bosing, kalitni nusxalab, shu yerga qo'ying.");
        ServerAiSaveButton.Content = keySet
            ? Tr.T("Заменить ключ ИИ", "ЖИ ачкычын алмаштыруу", "Replace AI key", "Yapay zekâ anahtarını değiştir", "SI kalitini almashtirish")
            : Tr.T("Включить ИИ на сервере", "Серверде ЖИни күйгүзүү", "Turn on AI on the server", "Sunucuda yapay zekâyı aç", "Serverda SIni yoqish");
    }

    private async void ServerAiSave_Click(object? sender, RoutedEventArgs e)
    {
        var api = ServerApi;
        var key = (ServerAiKeyBox.Text ?? "").Trim();
        if (api is null)
            return;
        if (key.Length < 20)
        {
            ServerActionStatus.Text = Tr.T("Вставьте ключ Google Gemini (он начинается с «AIza»).", "Google Gemini ачкычын коюңуз («AIza» менен башталат).",
                "Paste the Google Gemini key (it starts with “AIza”).", "Google Gemini anahtarını yapıştırın («AIza» ile başlar).", "Google Gemini kalitini qo'ying («AIza» bilan boshlanadi).");
            return;
        }
        ServerAiSaveButton.IsEnabled = false;
        ServerProgress.IsVisible = true;
        ServerActionStatus.Text = Tr.T("Отправляю ключ ИИ на сервер…", "ЖИ ачкычы серверге жөнөтүлүүдө…", "Sending the AI key to the server…", "Yapay zekâ anahtarı sunucuya gönderiliyor…", "SI kaliti serverga yuborilmoqda…");
        try
        {
            var s = await api.PatchSettingsAsync(new Dictionary<string, object?> { ["ai_key"] = key, ["ai_enabled"] = true }).ConfigureAwait(true);
            ServerAiKeyBox.Text = "";
            ShowServerStatus(s);
            PosLogger.Log("Бот: ключ ИИ отправлен на сервер (в программе не сохраняется).", "TELEGRAM");
            // Сразу проверяем: ключ мог оказаться неверным.
            ServerTestAi_Click(sender, e);
        }
        catch (Exception ex)
        {
            ServerActionStatus.Text = Tr.T("Сервер не принял ключ: ", "Сервер ачкычты кабыл алган жок: ", "The server did not accept the key: ", "Sunucu anahtarı kabul etmedi: ", "Server kalitni qabul qilmadi: ")
                                      + ServerTelegramBotApi.Describe(ex);
        }
        finally
        {
            ServerAiSaveButton.IsEnabled = true;
            ServerProgress.IsVisible = false;
        }
    }

    private void ServerAiGetKey_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бот: страница ключа Gemini не открылась: {ex.Message}", "TELEGRAM");
        }
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
            ServerChips.Children.Clear();
            ServerStatus.Text = Tr.T("Сервер NurCRM пока не поддерживает бота.", "NurCRM сервери азырынча ботту колдобойт.", "The NurCRM server does not support the bot yet.",
                "NurCRM sunucusu henüz botu desteklemiyor.", "NurCRM serveri hali botni qo'llab-quvvatlamaydi.");
            ServerMoveButton.IsEnabled = false;
            ShowServerAi(false, false);
            return;
        }

        var on = s.IsServerMode;
        ShowServerAi(on, s.AiKeySet);
        TelegramBotStatusChips.Fill(ServerChips, s);
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
        ServerProgress.IsVisible = true;
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
            ServerProgress.IsVisible = false;
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
        ServerProgress.IsVisible = true;
        ServerActionStatus.Text = Tr.T("Спрашиваю ИИ на сервере…", "Сервердеги ЖИден суралууда…", "Asking the AI on the server…", "Sunucudaki yapay zekâya soruluyor…", "Serverdagi SIdan so'ralmoqda…");
        try
        {
            var r = await api.TestAiAsync().ConfigureAwait(true);
            var answer = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("answer", out var a) ? a.ToString() : "";
            var model = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("model", out var m) ? m.ToString() : "";
            // 2026-10-04, стресс-тест бота: под нагрузкой сервер отдавал «ok» с пустым ответом ИИ — раньше
            // это показывалось как «ИИ отвечает».
            ServerActionStatus.Text = answer.Trim().Length == 0
                ? Tr.T("ИИ на сервере вернул пустой ответ — Google Gemini перегружен, попробуйте ещё раз через минуту.",
                    "Сервердеги ЖИ бош жооп кайтарды — Google Gemini жүктөлгөн, бир мүнөттөн кийин кайра аракет кылыңыз.",
                    "The AI on the server returned an empty answer — Google Gemini is overloaded, try again in a minute.",
                    "Sunucudaki yapay zekâ boş yanıt döndürdü — Google Gemini yoğun, bir dakika sonra tekrar deneyin.",
                    "Serverdagi SI bo'sh javob qaytardi — Google Gemini band, bir daqiqadan keyin qayta urinib ko'ring.")
                : Tr.T("ИИ на сервере отвечает", "Сервердеги ЖИ жооп берет", "The AI on the server answers", "Sunucudaki yapay zekâ yanıt veriyor", "Serverdagi SI javob beradi")
                  + (model.Length > 0 ? $" ({model})" : "") + ": " + answer;
        }
        catch (Exception ex)
        {
            ServerActionStatus.Text = Tr.T("ИИ не ответил: ", "ЖИ жооп берген жок: ", "The AI did not answer: ", "Yapay zekâ yanıt vermedi: ", "SI javob bermadi: ") + ServerTelegramBotApi.Describe(ex);
        }
        finally
        {
            ServerTestAiButton.IsEnabled = true;
            ServerProgress.IsVisible = false;
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
        // 2026-10-21 (бот, К1): пока ждём код — ссылка сразу отправит его боту («Старт» с кодом).
        if (!string.IsNullOrWhiteSpace(_botUsername))
            OpenUrl(_ownerCode is { } code ? $"https://t.me/{_botUsername}?start=own_{code}" : $"https://t.me/{_botUsername}");
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
            // 2026-10-21 (бот, ВАЖНО 13): другой бот — прежний получатель и владелец к нему не относятся, подтверждаются заново.
            if (TelegramBotOwner.BotId(prefs.TelegramBotToken) is { } oldBot && oldBot != TelegramBotOwner.BotId(token))
            {
                prefs.TelegramChatId = null;
                prefs.TelegramChatTitle = null;
                TelegramBotOwner.Reset();
                TestButton.IsEnabled = false;
                DetectStatus.Text = "";
            }
            prefs.TelegramBotToken = token;
            prefs.TelegramBotUsername = username;
            prefs.SaveToDisk();
            RefreshOrdersGroup(); // 2026-10-24 (Г1): другой бот — прежняя группа заказов сброшена

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

    /// <summary>2026-10-21 (бот, К1): код подтверждения, пока он ждёт отправки боту (для ссылки «Открыть моего бота»).</summary>
    private string? _ownerCode;
    private CancellationTokenSource? _detectCts;

    /// <summary>2026-10-21 (бот, К1): получатель и владелец — по одноразовому коду. Раньше «Определить получателя» брало чат
    /// последнего сообщения боту от кого угодно: написал покупатель — выручка и должники уходили ему.</summary>
    private async void Detect_Click(object? sender, RoutedEventArgs e)
    {
        DetectButton.IsEnabled = false;
        _detectCts?.Cancel();
        _detectCts = new CancellationTokenSource();
        try
        {
            var token = UserPreferences.Instance.TelegramBotToken ?? "";
            if (string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramBotUsername))
                await TelegramBotService.TryRememberBotUsernameAsync(token.Trim()).ConfigureAwait(true);
            _botUsername = UserPreferences.Instance.TelegramBotUsername ?? _botUsername;
            var (code, completion) = TelegramBotOwner.Begin(_detectCts.Token);
            _ownerCode = code;
            DetectStatus.Text = Tr.T(
                $"Отправьте боту код {code} из СВОЕГО Telegram: в личный чат с ботом — просто цифры (или нажмите «Открыть моего бота»), в группу магазина — /code {code}. Код действует 10 минут. Жду…",
                $"Ботко {code} кодун ӨЗҮҢҮЗДҮН Telegram'дан жөнөтүңүз: бот менен жеке чатка — сандарды гана (же «Ботумду ачуу» баскычын басыңыз), дүкөндүн тобуна — /code {code}. Код 10 мүнөт жарактуу. Күтүп жатам…",
                $"Send the code {code} to the bot from YOUR Telegram: in a private chat with the bot — just the digits (or press “Open my bot”), in the shop group — /code {code}. The code is valid for 10 minutes. Waiting…",
                $"{code} kodunu bota KENDİ Telegram'ınızdan gönderin: botla özel sohbette — sadece rakamlar (veya «Botumu aç»a basın), mağaza grubunda — /code {code}. Kod 10 dakika geçerli. Bekliyorum…",
                $"{code} kodini botga O'Z Telegram'ingizdan yuboring: bot bilan shaxsiy chatga — faqat raqamlar (yoki «Botimni ochish»ni bosing), do'kon guruhiga — /code {code}. Kod 10 daqiqa amal qiladi. Kutyapman…");
            var result = await completion.ConfigureAwait(true);
            _ownerCode = null;
            if (!result.Ok || result.ChatId == null)
            {
                if (result.Error != null)
                    DetectStatus.Text = result.Error;
                return;
            }

            TestButton.IsEnabled = true;
            var name = result.Title;
            DetectStatus.Text = string.IsNullOrWhiteSpace(name)
                ? Tr.T($"Готово: вы подтверждены как владелец, сводки пойдут в чат {result.ChatId}.", $"Даяр: сиз ээси катары ырасталдыңыз, жыйынтыктар {result.ChatId} чатына жөнөтүлөт.", $"Done: you are confirmed as the owner, summaries will go to chat {result.ChatId}.", $"Hazır: sahip olarak doğrulandınız, özetler {result.ChatId} sohbetine gönderilecek.", $"Tayyor: siz ega sifatida tasdiqlandingiz, hisobotlar {result.ChatId} chatiga yuboriladi.")
                : Tr.T($"Готово: вы подтверждены как владелец, сводки пойдут в чат «{name}».", $"Даяр: сиз ээси катары ырасталдыңыз, жыйынтыктар «{name}» чатына жөнөтүлөт.", $"Done: you are confirmed as the owner, summaries will go to the “{name}” chat.", $"Hazır: sahip olarak doğrulandınız, özetler «{name}» sohbetine gönderilecek.", $"Tayyor: siz ega sifatida tasdiqlandingiz, hisobotlar «{name}» chatiga yuboriladi.");

            StartBotIfEnabled();
            RefreshOrdersGroup(); // 2026-10-24 (Г1): владелец подтверждён — теперь можно подключать группу заказов
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 2026-10-21: окно закрыли — код больше не ждём (опрос подтверждения не висит 10 минут).
        _detectCts?.Cancel();
        _groupCts?.Cancel();
        TelegramBotOwner.OrdersGroupChanged -= OnOrdersGroupChanged;
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------ Группа заказов (ТЗ «Склад…», пакет Г1)

    private bool _fillingOrders;
    private CancellationTokenSource? _groupCts;

    private void OnOrdersGroupChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshOrdersGroup);

    private void ApplyOrdersGroupTexts()
    {
        OrdersGroupTitle.Text = Tr.T("Группа заказов", "Буйрутмалар тобу", "Orders group", "Sipariş grubu", "Buyurtmalar guruhi");
        OrdersGroupHint.Text = Tr.T(
            "Заказы из бота можно получать в общей группе сотрудников — вместо личного чата или вместе с ним. Сводки смены, выручка и долги в группу не отправляются никогда.",
            "Боттон келген буйрутмаларды кызматкерлердин жалпы тобунда алса болот — жеке чаттын ордуна же аны менен бирге. Смена жыйынтыгы, түшүм жана карыздар топко эч качан жөнөтүлбөйт.",
            "Orders from the bot can go to a shared staff group — instead of your private chat or together with it. Shift summaries, revenue and debts are never sent to the group.",
            "Bottan gelen siparişler ortak personel grubuna gidebilir — özel sohbetinizin yerine veya onunla birlikte. Vardiya özetleri, ciro ve borçlar gruba asla gönderilmez.",
            "Botdan kelgan buyurtmalarni xodimlarning umumiy guruhida olish mumkin — shaxsiy chat o'rniga yoki u bilan birga. Smena hisobotlari, tushum va qarzlar guruhga hech qachon yuborilmaydi.");
        OrdersGroupDetectButton.Content = Tr.T("Определить группу", "Топту аныктоо", "Detect group", "Grubu belirle", "Guruhni aniqlash");
        OrdersGroupDisconnectButton.Content = Tr.T("Отключить", "Өчүрүү", "Disconnect", "Bağlantıyı kes", "Uzish");
        OrdersTargetGroupRadio.Content = Tr.T("Только в группу", "Топко гана", "Group only", "Yalnızca gruba", "Faqat guruhga");
        OrdersTargetBothRadio.Content = Tr.T("В группу и мне в личку", "Топко жана мага жекече", "Group and my private chat", "Gruba ve bana özelden", "Guruhga va menga shaxsiy");
        OrdersTargetOwnerRadio.Content = Tr.T("Только мне в личку", "Мага жекече гана", "Only my private chat", "Yalnızca bana özelden", "Faqat menga shaxsiy");
        OrdersShowAmountCheck.Content = Tr.T("Показывать в группе сумму заказа и телефон покупателя", "Топто заказдын суммасын жана сатып алуучунун телефонун көрсөтүү",
            "Show the order amount and the customer's phone in the group", "Grupta sipariş tutarını ve müşterinin telefonunu göster", "Guruhda buyurtma summasi va xaridorning telefonini ko'rsatish");
        OrdersGroupPrivateHint.Text = Tr.T("Чтобы заказы приходили вам в личку, один раз напишите боту /start в личном чате.",
            "Буйрутмалар сизге жекече келиши үчүн, ботко жеке чатта бир жолу /start деп жазыңыз.",
            "To get orders in your private chat, write /start to the bot there once.",
            "Siparişlerin size özelden gelmesi için bota özel sohbette bir kez /start yazın.",
            "Buyurtmalar sizga shaxsiy kelishi uchun botga shaxsiy chatda bir marta /start deb yozing.");
    }

    /// <summary>Статус и доступность элементов блока «Группа заказов» — из настроек.</summary>
    private void RefreshOrdersGroup()
    {
        var prefs = UserPreferences.Instance;
        var serverMode = NurMarketKassa.Services.Api.ServerTelegramBotApi.LastKnownServerMode == true;
        var connected = !string.IsNullOrWhiteSpace(prefs.TelegramOrdersChatId);
        var removed = !connected && prefs.TelegramOrdersBotRemoved;
        var title = string.IsNullOrWhiteSpace(prefs.TelegramOrdersChatTitle) ? "" : prefs.TelegramOrdersChatTitle!;
        var botReady = !string.IsNullOrWhiteSpace(prefs.TelegramBotToken) && TelegramBotOwner.HasConfirmedOwner;
        var waiting = _groupCts != null && !_groupCts.IsCancellationRequested;

        if (serverMode)
        {
            OrdersGroupStatus.Text = Tr.T("Сервер пока не умеет группу заказов. Она работает, когда бот подключён в программе.",
                "Сервер буйрутмалар тобун азырынча колдобойт. Бот программага туташкан учурда иштейт.",
                "The server does not support the orders group yet. It works when the bot is connected in the program.",
                "Sunucu henüz sipariş grubunu desteklemiyor. Bot programa bağlıyken çalışır.",
                "Server buyurtmalar guruhini hali qo'llamaydi. U bot dasturda ulangan bo'lganda ishlaydi.");
        }
        else if (connected)
        {
            var when = prefs.TelegramOrdersBoundAtUtc?.ToLocalTime().ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var at = when == null ? "" : " " + when;
            OrdersGroupStatus.Text = Tr.T($"Группа «{title}» подключена{at}", $"«{title}» тобу туташтырылды{at}", $"Group “{title}” connected{at}",
                $"«{title}» grubu bağlandı{at}", $"«{title}» guruhi ulandi{at}");
        }
        else if (removed)
        {
            OrdersGroupStatus.Text = Tr.T($"Бот удалён из группы «{title}»", $"Бот «{title}» тобунан чыгарылды", $"The bot was removed from the group “{title}”",
                $"Bot «{title}» grubundan çıkarıldı", $"Bot «{title}» guruhidan olib tashlandi");
        }
        else
        {
            OrdersGroupStatus.Text = Tr.T("Не подключена", "Туташтырылган эмес", "Not connected", "Bağlı değil", "Ulanmagan");
        }

        if (!serverMode && !botReady && !connected && !waiting)
        {
            OrdersGroupInstruction.IsVisible = true;
            OrdersGroupInstruction.Text = Tr.T("Сначала определите получателя (владельца) — шаг 3 выше.", "Адегенде алуучуну (ээсин) аныктаңыз — жогорудагы 3-кадам.",
                "First detect the recipient (the owner) — step 3 above.", "Önce alıcıyı (sahibi) belirleyin — yukarıdaki 3. adım.", "Avval qabul qiluvchini (egani) aniqlang — yuqoridagi 3-qadam.");
        }
        else if (!waiting)
        {
            OrdersGroupInstruction.IsVisible = false;
        }

        OrdersGroupDetectButton.IsEnabled = !serverMode && botReady && !waiting;
        OrdersGroupDisconnectButton.IsEnabled = !serverMode && (connected || removed);
        var radios = !serverMode && connected;
        _fillingOrders = true;
        OrdersTargetGroupRadio.IsEnabled = OrdersTargetBothRadio.IsEnabled = OrdersTargetOwnerRadio.IsEnabled = OrdersShowAmountCheck.IsEnabled = radios;
        OrdersTargetGroupRadio.IsChecked = prefs.TelegramOrdersTarget == "group";
        OrdersTargetBothRadio.IsChecked = prefs.TelegramOrdersTarget == "both";
        OrdersTargetOwnerRadio.IsChecked = prefs.TelegramOrdersTarget is not ("group" or "both");
        OrdersShowAmountCheck.IsChecked = prefs.TelegramOrdersShowAmount;
        _fillingOrders = false;
        OrdersGroupPrivateHint.IsVisible = connected && prefs.TelegramOrdersTarget == "both";
    }

    /// <summary>«Определить группу»: код на 10 минут и инструкция; группа сохраняется, когда владелец пришлёт в ней «/group код».</summary>
    private async void OrdersGroupDetect_Click(object? sender, RoutedEventArgs e)
    {
        OrdersGroupDetectButton.IsEnabled = false;
        _groupCts?.Cancel();
        var cts = _groupCts = new CancellationTokenSource();
        try
        {
            var token = UserPreferences.Instance.TelegramBotToken ?? "";
            if (string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramBotUsername))
                await TelegramBotService.TryRememberBotUsernameAsync(token.Trim()).ConfigureAwait(true);
            _botUsername = UserPreferences.Instance.TelegramBotUsername ?? _botUsername;
            var bot = string.IsNullOrWhiteSpace(_botUsername) ? "" : "@" + _botUsername;
            var (code, completion) = TelegramBotOwner.BeginGroup(cts.Token);
            if (code.Length == 0)
            {
                var early = await completion.ConfigureAwait(true);
                OrdersGroupInstruction.IsVisible = true;
                OrdersGroupInstruction.Text = early.Error ?? "";
                return;
            }
            OrdersGroupInstruction.IsVisible = true;
            OrdersGroupInstruction.Text = Tr.T(
                $"1. Добавьте бота {bot} в группу (группа → Добавить участника).\n2. Отправьте в группе: /group {code}\nКод действует 10 минут. Жду…",
                $"1. Ботту {bot} топко кошуңуз (топ → Мүчө кошуу).\n2. Топто жөнөтүңүз: /group {code}\nКод 10 мүнөт жарактуу. Күтүп жатам…",
                $"1. Add the bot {bot} to the group (group → Add member).\n2. Send in the group: /group {code}\nThe code is valid for 10 minutes. Waiting…",
                $"1. {bot} botunu gruba ekleyin (grup → Üye ekle).\n2. Grupta gönderin: /group {code}\nKod 10 dakika geçerli. Bekliyorum…",
                $"1. {bot} botini guruhga qo'shing (guruh → A'zo qo'shish).\n2. Guruhda yuboring: /group {code}\nKod 10 daqiqa amal qiladi. Kutyapman…");
            var result = await completion.ConfigureAwait(true);
            if (result.Ok)
            {
                StartBotIfEnabled();
                OrdersGroupInstruction.IsVisible = false;
            }
            else if (result.Error != null && !cts.IsCancellationRequested)
            {
                OrdersGroupInstruction.Text = result.Error;
            }
        }
        finally
        {
            // Отменили (закрытие окна / новый код) — состояние уже обновил тот, кто отменил.
            if (_groupCts == cts)
                _groupCts = null;
            RefreshOrdersGroup();
        }
    }

    private void OrdersGroupDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        _groupCts?.Cancel();
        TelegramBotOwner.DisconnectOrdersGroup();
        OrdersGroupInstruction.IsVisible = false;
        RefreshOrdersGroup();
    }

    private void OrdersTarget_Changed(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _fillingOrders || sender is not RadioButton { IsChecked: true } radio)
            return;
        var prefs = UserPreferences.Instance;
        prefs.TelegramOrdersTarget = radio == OrdersTargetGroupRadio ? "group" : radio == OrdersTargetBothRadio ? "both" : "owner";
        prefs.SaveToDisk();
        OrdersGroupPrivateHint.IsVisible = prefs.TelegramOrdersTarget == "both";
    }

    private void OrdersShowAmount_Changed(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _fillingOrders)
            return;
        var prefs = UserPreferences.Instance;
        prefs.TelegramOrdersShowAmount = OrdersShowAmountCheck.IsChecked == true;
        prefs.SaveToDisk();
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestStatus.Text = Tr.T("Отправляю…", "Жөнөтүлүүдө…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…");
        try
        {
            // 2026-10-21 (бот): «&» и «<» в названии магазина ломали пробное сообщение — экранируем; текст на языке программы.
            var shop = System.Net.WebUtility.HtmlEncode(UserPreferences.Instance.StoreName ?? "");
            var error = await TelegramBotService
                .SendAsync($"<b>{shop}</b>\n\n" + Tr.T("Пробное сообщение из кассы. Если вы его видите — бот подключён.\n\nНажмите кнопку «📊 Сегодня» внизу или команду /segodnya.",
                    "Кассадан сынак билдирүү. Аны көрүп жатсаңыз — бот туташкан.\n\nТөмөнкү «📊 Бүгүн» баскычын же /segodnya буйругун басыңыз.",
                    "A test message from the kassa. If you see it, the bot is connected.\n\nPress the “📊 Today” button below or the /segodnya command.",
                    "Kasadan deneme mesajı. Görüyorsanız bot bağlı.\n\nAşağıdaki «📊 Bugün» düğmesine veya /segodnya komutuna basın.",
                    "Kassadan sinov xabari. Uni ko'rayotgan bo'lsangiz — bot ulangan.\n\nPastdagi «📊 Bugun» tugmasini yoki /segodnya buyrug'ini bosing."))
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
