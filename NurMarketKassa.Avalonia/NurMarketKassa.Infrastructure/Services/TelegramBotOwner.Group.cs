using System.Security.Cryptography;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-24 (ТЗ «Склад — ревизия, приёмка, поставщик, Telegram-группа», пакет Г1): «Группа заказов».
///
/// Владелец добавляет бота в группу сотрудников и подтверждает её тем же приёмом, что и себя: программа показывает одноразовый код
/// (6 цифр, 10 минут), владелец пишет в группе «/group 123456». Принимается, только если пишет ПОДТВЕРЖДЁННЫЙ владелец (from.id из
/// telegram-owner.json): чужой участник группы с верным кодом отказ получает, группа не сохраняется.
/// В группе заказов бот молчит: из команд слушает только /group, /groupoff и /help от владельца; всё остальное — без ответа, без ИИ и
/// без журнала обращений (см. TelegramBotPollingService.HandleOrdersGroupAsync). Сводки смены, отчёты, долги, прокат в эту группу
/// не уходят — только заказы (TelegramBotService.SendOrderAsync).
/// </summary>
public static partial class TelegramBotOwner
{
    /// <summary>Группу заказов подключили, отключили, удалили бота из неё или сменили получателей — окно настройки обновляет статус.</summary>
    public static event Action? OrdersGroupChanged;

    internal static void NotifyOrdersGroupChanged()
    {
        try
        {
            OrdersGroupChanged?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: обновление окна группы заказов не удалось ({ex.GetType().Name}).", "WARNING");
        }
    }

    /// <summary>Чат — группа заказов, и это НЕ чат владельца из настроек (если они совпали, группа обрабатывается как чат владельца).</summary>
    public static bool IsOrdersGroupOnly(string? chatId)
    {
        var prefs = UserPreferences.Instance;
        return !string.IsNullOrWhiteSpace(chatId) && !string.IsNullOrWhiteSpace(prefs.TelegramOrdersChatId)
               && string.Equals(chatId, prefs.TelegramOrdersChatId, StringComparison.Ordinal)
               && !string.Equals(chatId, prefs.TelegramChatId, StringComparison.Ordinal);
    }

    /// <summary>Начать подключение группы: код для показа в программе и задача, которая завершится, когда владелец отправит «/group код»
    /// в группе (или через 10 минут). Нужен уже подтверждённый владелец — без него отказ сразу.</summary>
    public static (string Code, Task<ConfirmResult> Completion) BeginGroup(CancellationToken ct = default)
    {
        if (!HasConfirmedOwner)
        {
            return ("", Task.FromResult(new ConfirmResult(false, null, null, Tr.T(
                "Сначала определите получателя (владельца).", "Адегенде алуучуну (ээсин) аныктаңыз.", "First detect the recipient (the owner).",
                "Önce alıcıyı (sahibi) belirleyin.", "Avval qabul qiluvchini (egani) aniqlang."))));
        }

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new Pending { Code = code, ExpiresUtc = DateTime.UtcNow + CodeLifetime, IsGroup = true };
        lock (Gate)
        {
            _pendingGroup?.Tcs.TrySetResult(new ConfirmResult(false, null, null, Tr.T("Код заменён новым.", "Код жаңысы менен алмаштырылды.",
                "The code was replaced with a new one.", "Kod yenisiyle değiştirildi.", "Kod yangisi bilan almashtirildi.")));
            _pendingGroup = pending;
        }

        _ = Task.Delay(CodeLifetime, CancellationToken.None).ContinueWith(_ => Finish(pending, new ConfirmResult(false, null, null, Tr.T(
            "Код не пришёл за 10 минут. Нажмите кнопку ещё раз — будет новый код.", "Код 10 мүнөттө келген жок. Баскычты кайра басыңыз — жаңы код чыгат.",
            "The code did not arrive within 10 minutes. Press the button again to get a new code.", "Kod 10 dakika içinde gelmedi. Yeni kod için düğmeye tekrar basın.",
            "Kod 10 daqiqada kelmadi. Tugmani qayta bosing — yangi kod chiqadi."))), TaskScheduler.Default);
        if (ct.CanBeCanceled)
            ct.Register(() => Finish(pending, new ConfirmResult(false, null, null, null)));
        if (TelegramBotPollingService.ActivePollers == 0)
            _ = Task.Run(() => ListenAsync(pending, ct), CancellationToken.None);
        PosLogger.Log("Телеграм-бот: ждём код группы заказов (10 минут).", "TELEGRAM");
        return (code, pending.Tcs.Task);
    }

    /// <summary>«/команда», «/команда@ИмяБота арг». false — не команда или команда другому боту.</summary>
    private static bool ParseCommand(string text, out string command, out string argument)
    {
        command = "";
        argument = "";
        var t = (text ?? "").Trim();
        if (!t.StartsWith('/'))
            return false;
        var space = t.IndexOfAny(new[] { ' ', '\n', '\t' });
        var head = space < 0 ? t : t[..space];
        argument = space < 0 ? "" : t[(space + 1)..].Trim();
        var at = head.IndexOf('@');
        if (at > 0)
        {
            var addressee = head[(at + 1)..];
            var mine = UserPreferences.Instance.TelegramBotUsername?.Trim().TrimStart('@');
            if (!string.IsNullOrEmpty(mine) && !string.Equals(addressee, mine, StringComparison.OrdinalIgnoreCase))
                return false; // команда другому боту в этой группе — не наша
            head = head[..at];
        }
        command = head.ToLowerInvariant();
        return true;
    }

    /// <summary>«/group 123456» (и «/group@ИмяБота 123456»). true — это была команда /group (reply — что ответить в чат, null — молчать).
    /// Группа сохраняется только если: чат group/supergroup, пишет подтверждённый владелец, код верный и не истёк, не больше 5 ошибок с чата.</summary>
    internal static bool TryHandleGroup(string text, string chatId, string? chatTitle, string? chatType, long? fromId, out string? reply)
    {
        reply = null;
        if (!ParseCommand(text, out var command, out var argument) || command != "/group")
            return false;

        var prefs = UserPreferences.Instance;
        var owner = IsOwnerUser(fromId);
        if (owner)
            TelegramBotText.Use(prefs.Language);

        if (chatType is not ("group" or "supergroup"))
        {
            // Личный чат: владельцу подсказка, остальным — обычное сообщение (не наша команда).
            if (owner)
                reply = TelegramBotText.T("Команду /group нужно отправить в самой группе: добавьте бота в группу и напишите там /group и код из программы.",
                    "/group буйругун топтун өзүнө жөнөтүңүз: ботту топко кошуп, ошол жерге /group жана программадагы кодду жазыңыз.",
                    "Send the /group command in the group itself: add the bot to the group and write /group and the code from the program there.",
                    "/group komutunu grubun kendisinde gönderin: botu gruba ekleyin ve orada /group ile programdaki kodu yazın.",
                    "/group buyrug'ini guruhning o'zida yuboring: botni guruhga qo'shing va u yerda /group va dasturdagi kodni yozing.");
            return owner;
        }

        Pending? p;
        lock (Gate)
            p = _pendingGroup is { } x && x.ExpiresUtc > DateTime.UtcNow ? x : null;
        if (p is null)
        {
            // Кода нет (не нажимали «Определить группу» или он истёк): владельцу — подсказка, чужому — тишина.
            if (owner)
                reply = TelegramBotText.T("Этого кода уже нет. Нажмите «Определить группу» в программе — появится новый код.",
                    "Бул код жок. Программада «Топту аныктоо» баскычын басыңыз — жаңы код чыгат.",
                    "There is no active code. Press “Detect group” in the program to get a new code.",
                    "Etkin kod yok. Yeni kod için programda «Grubu belirle»ye basın.",
                    "Faol kod yo'q. Dasturda «Guruhni aniqlash»ni bosing — yangi kod chiqadi.");
            return true;
        }

        var code = argument.Replace(" ", "");
        var isSixDigits = code.Length == 6 && code.All(char.IsAsciiDigit);
        // Попытки считаются по паре «чат + человек»: чужой не может заблокировать владельцу ввод в той же группе.
        var attemptKey = chatId + "|" + (fromId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
        lock (Gate)
        {
            var wrong = p.WrongByChat.GetValueOrDefault(attemptKey);
            if (wrong >= MaxWrongPerChat || p.WrongByChat.Values.Sum() >= 10 * MaxWrongPerChat)
                return true; // попытки кончились — молчим

            if (!owner)
            {
                p.WrongByChat[attemptKey] = wrong + 1;
                reply = TelegramBotText.T("Подтвердить группу может только владелец магазина.", "Топту дүкөндүн ээси гана ырастай алат.",
                    "Only the shop owner can confirm the group.", "Grubu yalnızca mağaza sahibi onaylayabilir.", "Guruhni faqat do'kon egasi tasdiqlay oladi.");
                return true;
            }

            if (!isSixDigits)
            {
                reply = TelegramBotText.T("Напишите команду и 6 цифр кода из программы, например: /group 123456", "Буйрукту жана программадагы 6 сандан турган кодду жазыңыз, мисалы: /group 123456",
                    "Write the command and the 6-digit code from the program, for example: /group 123456", "Komutu ve programdaki 6 haneli kodu yazın, örneğin: /group 123456",
                    "Buyruq va dasturdagi 6 xonali kodni yozing, masalan: /group 123456");
                return true;
            }

            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(code), System.Text.Encoding.ASCII.GetBytes(p.Code)))
            {
                p.WrongByChat[attemptKey] = wrong + 1;
                reply = TelegramBotText.T("Код не подошёл. Проверьте цифры в программе.", "Код туура эмес. Программадагы сандарды текшериңиз.",
                    "Wrong code. Check the digits in the program.", "Kod yanlış. Programdaki rakamları kontrol edin.", "Kod noto'g'ri. Dasturdagi raqamlarni tekshiring.");
                return true;
            }
        }

        var title = string.IsNullOrWhiteSpace(chatTitle) ? null : chatTitle;
        BindOrdersGroup(chatId, title);
        Finish(p, new ConfirmResult(true, chatId, title, null));
        PosLogger.Log("Телеграм-бот: группа заказов подключена кодом владельца.", "TELEGRAM");
        var tail = prefs.TelegramOrdersShowAmount
            ? TelegramBotText.T(" Долги сюда не приходят.", " Карыздар бул жерге келбейт.", " Debts are not sent here.", " Borçlar buraya gönderilmez.", " Qarzlar bu yerga yuborilmaydi.")
            : TelegramBotText.T(" Суммы и долги сюда не приходят.", " Суммалар жана карыздар бул жерге келбейт.", " Amounts and debts are not sent here.",
                " Tutarlar ve borçlar buraya gönderilmez.", " Summalar va qarzlar bu yerga yuborilmaydi.");
        var shop = Escape(prefs.StoreName);
        reply = TelegramBotText.T($"✅ Эта группа получает заказы магазина «{shop}».", $"✅ Бул топ «{shop}» дүкөнүнүн буйрутмаларын алат.",
            $"✅ This group now receives the orders of “{shop}”.", $"✅ Bu grup «{shop}» mağazasının siparişlerini alıyor.", $"✅ Bu guruh «{shop}» do'konining buyurtmalarini oladi.") + tail;
        return true;
    }

    /// <summary>Сообщение в чате группы заказов (она не совпадает с чатом владельца): отвечаем только владельцу и только на
    /// /group, /groupoff, /help. Всё остальное — null: ни ответа, ни ИИ, ни журнала обращений.</summary>
    internal static string? OrdersGroupReply(string text, string chatId, string? chatTitle, string? chatType, long? fromId)
    {
        if (!ParseCommand(text, out var command, out _) || command is not ("/group" or "/groupoff" or "/help"))
            return null;
        if (!IsOwnerUser(fromId))
            return null;

        var prefs = UserPreferences.Instance;
        TelegramBotText.Use(prefs.Language);
        switch (command)
        {
            case "/group":
                return TryHandleGroup(text, chatId, chatTitle, chatType, fromId, out var groupReply) ? groupReply : null;
            case "/groupoff":
                DisconnectOrdersGroup();
                return TelegramBotText.T("Группа отключена: заказы сюда больше не приходят.", "Топ өчүрүлдү: буйрутмалар бул жерге башка келбейт.",
                    "The group is disconnected: orders will no longer come here.", "Grup bağlantısı kesildi: siparişler artık buraya gelmeyecek.",
                    "Guruh uzildi: buyurtmalar endi bu yerga kelmaydi.");
            default:
                var shop = Escape(prefs.StoreName);
                return TelegramBotText.T($"Эта группа получает заказы магазина «{shop}». Больше бот здесь ничего не делает.\nВладельцу: /group и код из программы — подключить другую группу, /groupoff — отключить эту.",
                    $"Бул топ «{shop}» дүкөнүнүн буйрутмаларын алат. Бот бул жерде башка эч нерсе кылбайт.\nЭсиси үчүн: /group жана программадагы код — башка топту туташтыруу, /groupoff — муну өчүрүү.",
                    $"This group receives the orders of “{shop}”. The bot does nothing else here.\nFor the owner: /group and the code from the program — connect another group, /groupoff — disconnect this one.",
                    $"Bu grup «{shop}» mağazasının siparişlerini alır. Bot burada başka bir şey yapmaz.\nSahip için: /group ve programdaki kod — başka grup bağla, /groupoff — bunu kapat.",
                    $"Bu guruh «{shop}» do'konining buyurtmalarini oladi. Bot bu yerda boshqa hech narsa qilmaydi.\nEga uchun: /group va dasturdagi kod — boshqa guruhni ulash, /groupoff — buni uzish.");
        }
    }

    private static void BindOrdersGroup(string chatId, string? title)
    {
        var prefs = UserPreferences.Instance;
        prefs.TelegramOrdersChatId = chatId;
        prefs.TelegramOrdersChatTitle = title;
        prefs.TelegramOrdersBoundAtUtc = DateTime.UtcNow;
        prefs.TelegramOrdersBotRemoved = false;
        // Владелец подключил группу, чтобы заказы шли в неё: «только мне в личку» (значение по умолчанию) меняем на «только в группу».
        // Если он уже выбрал другой вариант — не трогаем.
        if (prefs.TelegramOrdersTarget == "owner")
            prefs.TelegramOrdersTarget = "group";
        prefs.SaveToDisk();
        NotifyOrdersGroupChanged();
    }

    private static void ClearOrdersGroupFields(bool resetTarget)
    {
        var prefs = UserPreferences.Instance;
        prefs.TelegramOrdersChatId = null;
        prefs.TelegramOrdersChatTitle = null;
        prefs.TelegramOrdersBoundAtUtc = null;
        prefs.TelegramOrdersBotRemoved = false;
        if (resetTarget)
        {
            prefs.TelegramOrdersTarget = "owner";
            prefs.TelegramOrdersShowAmount = false;
        }
    }

    /// <summary>«Отключить» / «/groupoff»: заказы снова идут как раньше — владельцу.</summary>
    public static void DisconnectOrdersGroup()
    {
        ClearOrdersGroupFields(resetTarget: true);
        UserPreferences.Instance.SaveToDisk();
        PosLogger.Log("Телеграм-бот: группа заказов отключена.", "TELEGRAM");
        NotifyOrdersGroupChanged();
    }

    /// <summary>Бота удалили из группы заказов (my_chat_member left/kicked): группа очищена, в окне — «Бот удалён из группы».
    /// Название остаётся для статуса. false — это была не группа заказов.</summary>
    internal static bool MarkOrdersGroupRemoved(string chatId)
    {
        var prefs = UserPreferences.Instance;
        if (!string.Equals(chatId, prefs.TelegramOrdersChatId, StringComparison.Ordinal))
            return false;
        prefs.TelegramOrdersChatId = null;
        prefs.TelegramOrdersBotRemoved = true;
        prefs.SaveToDisk();
        PosLogger.Log("Телеграм-бот: бота удалили из группы заказов — группа отключена.", "TELEGRAM");
        NotifyOrdersGroupChanged();
        return true;
    }

    /// <summary>Супергруппа: Telegram сменил группе chat_id (migrate_to_chat_id) — группа заказов переезжает на новый.</summary>
    internal static void RememberMigratedOrdersGroup(string oldChatId, string newChatId)
    {
        var prefs = UserPreferences.Instance;
        if (!string.Equals(prefs.TelegramOrdersChatId, oldChatId, StringComparison.Ordinal))
            return;
        prefs.TelegramOrdersChatId = newChatId;
        prefs.SaveToDisk();
        PosLogger.Log("Телеграм-бот: группа заказов стала супергруппой, получатель обновлён.", "TELEGRAM");
        NotifyOrdersGroupChanged();
    }

    /// <summary>Личный чат владельца для «в группу и мне в личку»: чат из настроек, если он личный, иначе личный чат подтверждённого
    /// владельца (в личном чате id чата = id человека). null — владелец не известен.</summary>
    public static string? OwnerPrivateChatId()
    {
        var chat = UserPreferences.Instance.TelegramChatId?.Trim();
        if (!string.IsNullOrEmpty(chat) && !chat.StartsWith('-'))
            return chat;
        return Current() is { UserIds.Count: > 0 } s
            ? s.UserIds[0].ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}
