using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-21: ответы «на всё», меню кнопками и безопасная подписка покупателя (бот, идеи 3, 6, 12 и ВАЖНО 3 отчёта 20.10).
/// • Стикер, фото, непонятная команда, чужая команда владельца — короткая подсказка с кнопками вместо тишины (было 23 % сообщений теста
///   без ответа).
/// • «/start &lt;ссылка клиента&gt;»: ссылка проверяется (подпись программы, TelegramSubscriberStore), затем номер телефона кнопкой
///   «Подтвердить номер» (Telegram сам присылает номер этого человека) сверяется с карточкой клиента. Мусор и рекламные метки
///   («/start insta») — обычное приветствие, клиентом не сохраняются; владелец по ссылке клиента не подписывается.
/// </summary>
public sealed partial class TelegramBotPollingService
{
    private sealed record PendingSubscription(string ClientId, string PhoneDigits, DateTime ExpiresUtc);

    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromMinutes(15);
    private readonly object _subscriptionGate = new();
    private readonly Dictionary<string, PendingSubscription> _pendingSubscriptions = new(StringComparer.Ordinal);
    private (DateTime AtUtc, Dictionary<string, string> Phones)? _clientPhones;
    private readonly SemaphoreSlim _clientPhonesGate = new(1, 1);

    private static string ShopName => Escape(UserPreferences.Instance.StoreName);

    /// <summary>Приветствие покупателю с меню кнопками.</summary>
    private static string CustomerGreeting() => TelegramAiChat.IsConfigured
        ? TelegramBotText.T(
            $"Здравствуйте! Я помощник магазина «{ShopName}». Спросите о товарах, ценах и наличии — отвечу сразу. Или выберите кнопку ниже.",
            $"Саламатсызбы! Мен «{ShopName}» дүкөнүнүн жардамчысымын. Товарлар, баалар жана бар-жогу тууралуу сураңыз — дароо жооп берем. Же төмөнкү баскычты тандаңыз.",
            $"Hello! I'm the assistant of “{ShopName}”. Ask about products, prices and availability — I'll answer right away. Or choose a button below.",
            $"Merhaba! «{ShopName}» mağazasının asistanıyım. Ürünler, fiyatlar ve stok hakkında sorun — hemen yanıtlarım. Ya da aşağıdan bir düğme seçin.",
            $"Assalomu alaykum! Men «{ShopName}» do'konining yordamchisiman. Mahsulotlar, narxlar va mavjudligi haqida so'rang — darhol javob beraman. Yoki pastdagi tugmani tanlang.")
        : TelegramBotText.T(
            $"Здравствуйте! Я помощник магазина «{ShopName}». Напишите название товара — подскажу цену и наличие. Кнопки ниже: каталог, ваш долг, телефон и адрес.",
            $"Саламатсызбы! Мен «{ShopName}» дүкөнүнүн жардамчысымын. Товардын атын жазыңыз — баасын жана бар-жогун айтам. Төмөндө: каталог, карызыңыз, телефон жана дарек.",
            $"Hello! I'm the assistant of “{ShopName}”. Type a product name and I'll tell you the price and availability. Buttons below: catalog, your debt, phone and address.",
            $"Merhaba! «{ShopName}» mağazasının asistanıyım. Ürün adını yazın — fiyatını ve stok durumunu söylerim. Aşağıda: katalog, borcunuz, telefon ve adres.",
            $"Assalomu alaykum! Men «{ShopName}» do'konining yordamchisiman. Mahsulot nomini yozing — narxi va mavjudligini aytaman. Pastda: katalog, qarzingiz, telefon va manzil.");

    /// <summary>Ответ на стикер, фото, пустое сообщение, непонятную команду — вместо тишины.</summary>
    private static string CustomerHint(bool nonText) => nonText
        ? TelegramBotText.T("Я понимаю текст 🙂 Напишите, что ищете (например «есть сахар?»), или выберите кнопку ниже.",
            "Мен текстти түшүнөм 🙂 Эмне издеп жатканыңызды жазыңыз (мисалы «шекер барбы?») же төмөнкү баскычты тандаңыз.",
            "I understand text 🙂 Type what you are looking for (for example “do you have sugar?”) or choose a button below.",
            "Metni anlıyorum 🙂 Ne aradığınızı yazın (örneğin «şeker var mı?») ya da aşağıdan bir düğme seçin.",
            "Men matnni tushunaman 🙂 Nima qidirayotganingizni yozing (masalan «shakar bormi?») yoki pastdagi tugmani tanlang.")
        : TelegramBotText.T("Такой команды нет 🙂 Напишите вопрос обычными словами или выберите кнопку ниже.",
            "Мындай буйрук жок 🙂 Суроону жөнөкөй сөз менен жазыңыз же төмөнкү баскычты тандаңыз.",
            "There is no such command 🙂 Type your question in plain words or choose a button below.",
            "Böyle bir komut yok 🙂 Sorunuzu normal kelimelerle yazın ya da aşağıdan bir düğme seçin.",
            "Bunday buyruq yo'q 🙂 Savolingizni oddiy so'zlar bilan yozing yoki pastdagi tugmani tanlang.");

    private static string OwnerOnlyForCustomer() => TelegramBotText.T(
        "Эта команда — для владельца магазина. Вам доступны каталог, ваш долг, телефон и адрес — кнопки ниже.",
        "Бул буйрук дүкөндүн ээси үчүн. Сизге каталог, карызыңыз, телефон жана дарек жеткиликтүү — төмөнкү баскычтар.",
        "This command is for the shop owner. You can use the catalog, your debt, the phone and the address — buttons below.",
        "Bu komut mağaza sahibi içindir. Size katalog, borcunuz, telefon ve adres açık — aşağıdaki düğmeler.",
        "Bu buyruq do'kon egasi uchun. Sizga katalog, qarzingiz, telefon va manzil ochiq — pastdagi tugmalar.");

    /// <summary>Участник группы владельца (не сам владелец): должники, ИИ с данными магазина и изменения товаров — нельзя.</summary>
    private static string OwnerOnlyInGroup() => TelegramBotText.T(
        "Должники, вопросы ИИ и изменения товаров — только для владельца. Владелец подтверждается кодом: в программе «Телеграм-бот» → «Определить получателя», затем в эту группу — /code и цифры.",
        "Карызкорлор, ЖИге суроолор жана товарларды өзгөртүү — ээси үчүн гана. Ээси код менен ырасталат: программада «Телеграм-бот» → «Алуучуну аныктоо», андан кийин бул топко /code жана сандар.",
        "Debtors, AI questions and product changes are for the owner only. The owner is confirmed with a code: in the program “Telegram bot” → “Detect recipient”, then send /code and the digits to this group.",
        "Borçlular, yapay zekâ soruları ve ürün değişiklikleri yalnızca sahip içindir. Sahip kodla doğrulanır: programda «Telegram botu» → «Alıcıyı belirle», sonra bu gruba /code ve rakamlar.",
        "Qarzdorlar, SI savollari va mahsulot o'zgarishlari faqat ega uchun. Ega kod bilan tasdiqlanadi: dasturda «Telegram bot» → «Qabul qiluvchini aniqlash», so'ng shu guruhga /code va raqamlar.");

    private static string ShopPhoneText()
    {
        var phone = UserPreferences.Instance.OwnerPhone;
        return string.IsNullOrWhiteSpace(phone)
            ? TelegramBotText.T("Телефон магазина пока не указан — напишите вопрос здесь.", "Дүкөндүн телефону азырынча көрсөтүлгөн эмес — суроону ушул жерге жазыңыз.",
                "The shop phone is not set yet — write your question here.", "Mağaza telefonu henüz belirtilmedi — sorunuzu buraya yazın.", "Do'kon telefoni hali ko'rsatilmagan — savolingizni shu yerga yozing.")
            : TelegramBotText.T($"📞 Телефон магазина «{ShopName}»: {Escape(phone)}", $"📞 «{ShopName}» дүкөнүнүн телефону: {Escape(phone)}",
                $"📞 Phone of “{ShopName}”: {Escape(phone)}", $"📞 «{ShopName}» mağazasının telefonu: {Escape(phone)}", $"📞 «{ShopName}» do'koni telefoni: {Escape(phone)}");
    }

    private static string ShopAddressText()
    {
        var address = UserPreferences.Instance.StoreAddress;
        return string.IsNullOrWhiteSpace(address)
            ? TelegramBotText.T("Адрес магазина пока не указан — спросите здесь, подскажем.", "Дүкөндүн дареги азырынча көрсөтүлгөн эмес — ушул жерден сураңыз.",
                "The shop address is not set yet — ask here and we'll help.", "Mağaza adresi henüz belirtilmedi — buradan sorun.", "Do'kon manzili hali ko'rsatilmagan — shu yerda so'rang.")
            : TelegramBotText.T($"📍 Адрес магазина «{ShopName}»: {Escape(address)}", $"📍 «{ShopName}» дүкөнүнүн дареги: {Escape(address)}",
                $"📍 Address of “{ShopName}”: {Escape(address)}", $"📍 «{ShopName}» mağazasının adresi: {Escape(address)}", $"📍 «{ShopName}» do'koni manzili: {Escape(address)}");
    }

    /// <summary>«/start &lt;параметр&gt;» от покупателя: проверка ссылки, затем телефона. Возвращает ответ и кнопки.</summary>
    private async Task<(string Text, object? Markup)> StartWithArgumentAsync(string chatId, bool isOwner, bool isGroup, string argument, CancellationToken ct)
    {
        var (clientId, signed) = TelegramSubscriberStore.ParseStartParameter(argument);
        if (clientId is null)
        {
            // Рекламная метка или мусор: обычное приветствие, клиентом не сохраняем.
            PosLogger.Log("Телеграм-бот: «Старт» по метке без клиента — обычное приветствие.", "TELEGRAM");
            return isOwner ? (TelegramReportBuilder.BuildHelp(), TelegramBotText.OwnerKeyboard()) : (CustomerGreeting(), TelegramBotText.CustomerKeyboard());
        }
        if (isOwner || isGroup)
        {
            return (TelegramBotText.T("Это ссылка для покупателя — владельцу и группе подписываться не нужно.",
                "Бул шилтеме сатып алуучу үчүн — ээсине жана топко жазылуунун кереги жок.",
                "This link is for a customer — the owner and groups don't need to subscribe.",
                "Bu bağlantı müşteri içindir — sahibin ve grupların abone olmasına gerek yok.",
                "Bu havola xaridor uchun — ega va guruhga obuna bo'lish shart emas."), isOwner ? TelegramBotText.OwnerKeyboard() : null);
        }
        if (string.Equals(TelegramSubscriberStore.GetClientId(chatId), clientId, StringComparison.OrdinalIgnoreCase))
            return (SubscribedText(), TelegramBotText.CustomerKeyboard());

        var (ok, found, phone) = await FindClientPhoneAsync(clientId, ct).ConfigureAwait(false);
        var digits = LastDigits(phone);
        if (digits.Length >= 9)
        {
            lock (_subscriptionGate)
            {
                if (_pendingSubscriptions.Count > 500)
                    foreach (var old in _pendingSubscriptions.Where(p => p.Value.ExpiresUtc < DateTime.UtcNow).Select(p => p.Key).ToList())
                        _pendingSubscriptions.Remove(old);
                _pendingSubscriptions[chatId] = new PendingSubscription(clientId, digits, DateTime.UtcNow + SubscriptionLifetime);
            }
            PosLogger.Log("Телеграм-бот: «Старт» по ссылке клиента — жду подтверждения номера.", "TELEGRAM");
            return (TelegramBotText.T("Чтобы получать напоминания о долге и акции, подтвердите номер телефона кнопкой ниже — сверю его с карточкой клиента.",
                "Карыз жана акциялар тууралуу эскертмелерди алуу үчүн төмөнкү баскыч менен телефон номериңизди ырастаңыз — кардардын картасы менен салыштырам.",
                "To get debt reminders and promotions, confirm your phone number with the button below — I'll check it against the customer card.",
                "Borç hatırlatmaları ve kampanyalar için aşağıdaki düğmeyle telefon numaranızı doğrulayın — müşteri kartıyla karşılaştıracağım.",
                "Qarz eslatmalari va aksiyalarni olish uchun pastdagi tugma bilan telefon raqamingizni tasdiqlang — mijoz kartasi bilan solishtiraman."),
                TelegramBotText.ContactKeyboard());
        }
        if (signed && ok && found)
        {
            // Подписанная программой ссылка, а телефона в карточке нет — сверять не с чем: подписываем по ссылке.
            return (CompleteSubscription(chatId, clientId, null), TelegramBotText.CustomerKeyboard());
        }
        if (signed && !ok)
        {
            return (TelegramBotText.T("Сейчас не получается проверить ссылку — нет связи с сервером. Откройте её ещё раз через пару минут.",
                "Азыр шилтемени текшерүү мүмкүн эмес — сервер менен байланыш жок. Бир нече мүнөттөн кийин кайра ачыңыз.",
                "I can't check the link right now — no connection to the server. Open it again in a couple of minutes.",
                "Bağlantıyı şu an kontrol edemiyorum — sunucuyla bağlantı yok. Birkaç dakika sonra tekrar açın.",
                "Hozir havolani tekshirib bo'lmayapti — server bilan aloqa yo'q. Bir necha daqiqadan keyin qayta oching."), TelegramBotText.CustomerKeyboard());
        }
        PosLogger.Log("Телеграм-бот: ссылка клиента не прошла проверку (без подписи и без телефона в карточке) — не подписываю.", "TELEGRAM");
        return (TelegramBotText.T("Эта ссылка устарела. Попросите на кассе новую ссылку — по ней напоминания будут приходить сюда.",
            "Бул шилтеме эскирди. Кассадан жаңы шилтеме сураңыз — ошол аркылуу эскертмелер ушул жерге келет.",
            "This link is outdated. Ask for a new link at the till — reminders will then come here.",
            "Bu bağlantı eski. Kasadan yeni bir bağlantı isteyin — hatırlatmalar buraya gelir.",
            "Bu havola eskirgan. Kassadan yangi havola so'rang — eslatmalar shu yerga keladi."), TelegramBotText.CustomerKeyboard());
    }

    /// <summary>Покупатель нажал «Подтвердить номер» — Telegram прислал его контакт.</summary>
    private async Task HandleContactAsync(string chatId, long? fromId, JsonElement contact, string? name, CancellationToken ct)
    {
        PendingSubscription? pending;
        lock (_subscriptionGate)
        {
            pending = _pendingSubscriptions.TryGetValue(chatId, out var p) && p.ExpiresUtc > DateTime.UtcNow ? p : null;
        }
        if (pending is null)
        {
            await SendReplyAsync(chatId, TelegramBotText.T("Спасибо! Номер сейчас не нужен — просто напишите вопрос.", "Рахмат! Номер азыр керек эмес — суроону жаза бериңиз.",
                "Thank you! The number isn't needed now — just type your question.", "Teşekkürler! Numara şu an gerekmiyor — sorunuzu yazın.",
                "Rahmat! Raqam hozir kerak emas — savolingizni yozing."), ct, TelegramBotText.CustomerKeyboard()).ConfigureAwait(false);
            return;
        }
        long? contactUser = contact.TryGetProperty("user_id", out var u) && u.TryGetInt64(out var uid) ? uid : null;
        if (contactUser is null || contactUser != fromId)
        {
            await SendReplyAsync(chatId, TelegramBotText.T("Отправьте, пожалуйста, СВОЙ номер — кнопкой «Подтвердить номер».", "Сураныч, ӨЗҮҢҮЗДҮН номериңизди «Номерди ырастоо» баскычы менен жөнөтүңүз.",
                "Please send YOUR OWN number with the “Confirm my number” button.", "Lütfen KENDİ numaranızı «Numaramı doğrula» düğmesiyle gönderin.",
                "Iltimos, O'Z raqamingizni «Raqamni tasdiqlash» tugmasi bilan yuboring."), ct, TelegramBotText.ContactKeyboard()).ConfigureAwait(false);
            return;
        }
        lock (_subscriptionGate)
            _pendingSubscriptions.Remove(chatId);
        var phone = contact.TryGetProperty("phone_number", out var ph) ? ph.GetString() : null;
        if (LastDigits(phone) != pending.PhoneDigits)
        {
            PosLogger.Log("Телеграм-бот: номер не совпал с карточкой клиента — подписка не оформлена.", "TELEGRAM");
            await SendReplyAsync(chatId, TelegramBotText.T("Номер не совпадает с номером в карточке клиента. Обратитесь на кассу — там проверят и дадут новую ссылку.",
                "Номер кардардын картасындагы номер менен дал келбейт. Кассага кайрылыңыз — текшерип, жаңы шилтеме беришет.",
                "The number doesn't match the one in the customer card. Please ask at the till — they will check it and give you a new link.",
                "Numara müşteri kartındaki numarayla eşleşmiyor. Kasaya başvurun — kontrol edip yeni bağlantı verirler.",
                "Raqam mijoz kartasidagi raqamga mos kelmadi. Kassaga murojaat qiling — tekshirib, yangi havola berishadi."), ct, TelegramBotText.CustomerKeyboard()).ConfigureAwait(false);
            return;
        }
        await SendReplyAsync(chatId, CompleteSubscription(chatId, pending.ClientId, name), ct, TelegramBotText.CustomerKeyboard()).ConfigureAwait(false);
    }

    private static string SubscribedText() => TelegramBotText.T(
        "Готово! Теперь напоминания о задолженности и об акциях будут приходить сюда. Кнопка «💳 Мой долг» покажет ваш текущий долг.",
        "Даяр! Эми карыз жана акциялар тууралуу эскертмелер ушул жерге келет. «💳 Менин карызым» баскычы учурдагы карызыңызды көрсөтөт.",
        "Done! Debt and promotion reminders will now come here. The “💳 My debt” button shows your current debt.",
        "Tamam! Borç ve kampanya hatırlatmaları artık buraya gelecek. «💳 Borcum» düğmesi güncel borcunuzu gösterir.",
        "Tayyor! Endi qarz va aksiyalar haqidagi eslatmalar shu yerga keladi. «💳 Mening qarzim» tugmasi joriy qarzingizni ko'rsatadi.");

    /// <summary>Подписка подтверждена: связка в этой кассе и chat_id — в карточку клиента на сервере.</summary>
    private string CompleteSubscription(string chatId, string clientId, string? name)
    {
        var alreadyBound = string.Equals(TelegramSubscriberStore.GetClientId(chatId), clientId, StringComparison.OrdinalIgnoreCase);
        TelegramSubscriberStore.Subscribe(chatId, clientId, name);
        TelegramReminderLog.Forget(chatId);
        PosLogger.Log($"Телеграм-бот: подписан клиент {clientId} (ссылка и номер проверены).", "TELEGRAM");
        if (!alreadyBound)
            PushSubscriberToServer(chatId, clientId);
        return SubscribedText();
    }

    private static string LastDigits(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 9 ? digits[^9..] : digits;
    }

    /// <summary>Телефон клиента для сверки: (сервер ответил, клиент найден, телефон). Список клиентов — копия на 5 минут.</summary>
    private async Task<(bool Ok, bool Found, string? Phone)> FindClientPhoneAsync(string clientId, CancellationToken ct)
    {
        if (_clients == null)
            return (false, false, null);
        try
        {
            var cache = _clientPhones;
            if (cache is null || DateTime.UtcNow - cache.Value.AtUtc > TimeSpan.FromMinutes(5))
            {
                await _clientPhonesGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    cache = _clientPhones;
                    if (cache is null || DateTime.UtcNow - cache.Value.AtUtc > TimeSpan.FromMinutes(5))
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(20));
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var client in await _clients.GetClientsAsync(null, timeout.Token).ConfigureAwait(false))
                            if (ReadString(client, "id") is { Length: > 0 } id)
                                map[id] = ReadString(client, "phone") ?? "";
                        cache = (DateTime.UtcNow, map);
                        _clientPhones = cache;
                    }
                }
                finally
                {
                    _clientPhonesGate.Release();
                }
            }
            return cache.Value.Phones.TryGetValue(clientId, out var phone) ? (true, true, phone) : (true, false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: клиенты для проверки подписки не загружены ({ex.GetType().Name}).", "WARNING");
            return (false, false, null);
        }
    }

    /// <summary>Вопрос покупателя к ИИ — не больше 8 одновременно; ждём место до 20 с, иначе отвечает поиск по каталогу.</summary>
    private static async Task<(string? Answer, string? Error)> AskCustomerLimitedAsync(string chatId, string text, CancellationToken ct)
    {
        if (!await CustomerAiSlots.WaitAsync(CustomerAiWait, ct).ConfigureAwait(false))
            return (null, "ИИ занят другими покупателями");
        try
        {
            return await TelegramAiChat.AskCustomerAsync(chatId, text, ct).ConfigureAwait(false);
        }
        finally
        {
            CustomerAiSlots.Release();
        }
    }
}
