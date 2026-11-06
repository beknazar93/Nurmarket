using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-Б, функция 8): «Электронный чек через Telegram-бот магазина» — без сервера. Идея — OSPOS/Loyverse (чек по email/SMS), Odoo (QR на чеке); код свой.
// Telegram не даёт написать человеку по номеру телефона, поэтому: на чеке и экране покупателя — QR/ссылка t.me/<бот>?start=receipts → покупатель жмёт «Старт» и
// кнопкой «Поделиться номером» (Telegram сам присылает его настоящий номер) → бот запоминает пару «телефон ↔ chat_id» (здесь, TelegramReceiptLinks) → дальше касса сама
// шлёт чек тому, у кого в чеке выбран клиент с этим телефоном (TelegramReceiptDelivery). Согласие — явное (Старт + номер), отписка — команда /stop (бот) или блокировка бота.
// Файл связей один на компьютер и общий для кассы и программы владельца (опрос бота ведёт любая из них): %LOCALAPPDATA%\NurMarketKassa\telegram-receipt-links.json.

/// <summary>Одна привязка: телефон покупателя (номер целиком с кодом страны; в файлах до 2026-11-06 — последние 9 цифр) и его чат с ботом.</summary>
public sealed class TelegramReceiptLink
{
    public string Phone { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string? Name { get; set; }
    /// <summary>Какому боту дано согласие (имя бота без @): смена бота = согласие не переносится.</summary>
    public string? Bot { get; set; }
    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.Now;
}

public static class TelegramReceiptLinks
{
    /// <summary>Параметр «start» общей ссылки магазина: «Получать чеки и бонусы в Telegram».</summary>
    public const string StartParameter = "receipts";

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static List<TelegramReceiptLink>? _cache;
    private static DateTime _stamp;

    private static string FilePath => Path.Combine(AntiFraudSettingsStore.Folder, "telegram-receipt-links.json");

    /// <summary>Последние 9 цифр номера (0555 123 456, +996 555 123 456, 996555123456 → 555123456). Короче 9 цифр — как есть.</summary>
    public static string LastDigits(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 9 ? digits[^9..] : digits;
    }

    /// <summary>2026-11-06: В3 Л5-19 — ключ сравнения номеров ЦЕЛИКОМ с кодом страны (раньше — последние 9 цифр, и +996 555 123 456 совпадал с +998 555 123 456).
    /// 9 цифр («555123456») и «0» + 9 цифр («0555123456») — местные номера Кыргызстана, к ним добавляется 996; номера с кодом страны («996…», «998…», «+7…») остаются как есть,
    /// «00» в начале (международный выход) отбрасывается. Короче 9 цифр — как есть (для привязки не годится, см. <see cref="IsUsablePhone"/>).</summary>
    public static string PhoneKey(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal) && digits.Length > 11)
            digits = digits[2..];
        if (digits.Length == 9)
            return "996" + digits;
        if (digits.Length == 10 && digits[0] == '0')
            return "996" + digits[1..];
        return digits;
    }

    /// <summary>Привязка, сохранённая до 2026-11-06, хранит только последние 9 цифр: подходит только к номеру Кыргызстана (996…) с тем же хвостом.</summary>
    internal static bool LegacyLinkMatches(string storedPhone, string key) =>
        storedPhone.Length <= 9 && key.StartsWith("996", StringComparison.Ordinal) && key.EndsWith(storedPhone, StringComparison.Ordinal);

    /// <summary>Подходит ли номер для привязки/отправки (не меньше 9 цифр).</summary>
    public static bool IsUsablePhone(string? phone) => LastDigits(phone).Length >= 9;

    /// <summary>Имя бота без «@»; пусто — бот не настроен.</summary>
    public static string? BotName()
    {
        var bot = UserPreferences.Instance.TelegramBotUsername?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(bot) ? null : bot;
    }

    /// <summary>Ссылка «Получать чеки и бонусы в Telegram» (для QR и текста); null — имя бота ещё не сохранено.</summary>
    public static string? ShopLink() => BotName() is { } bot ? $"https://t.me/{bot}?start={StartParameter}" : null;

    /// <summary>Свой формат параметра «start»: приходит от бота как «/start receipts». Регистр не важен (на бумаге ссылка печатается заглавными).</summary>
    public static bool IsReceiptsStart(string? argument) => string.Equals((argument ?? "").Trim(), StartParameter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Две строки для печатного чека: призыв и короткая ссылка. Пусто — выключено в настройках или нет имени бота.</summary>
    public static IReadOnlyList<string> PaperLines()
    {
        var r = AntiFraudSettingsStore.Current.Receipts;
        if (!r.Enabled || !r.LinkOnPaper || BotName() is not { } bot)
            return Array.Empty<string>();
        return new[]
        {
            Tr.T("Чеки и бонусы в Telegram:", "Чектер жана бонустар Telegram'да:", "Receipts and bonuses in Telegram:", "Fişler ve bonuslar Telegram'da:", "Chek va bonuslar Telegram'da:"),
            $"t.me/{bot}?start={StartParameter}",
        };
    }

    // ───────── связи ─────────

    public static IReadOnlyList<TelegramReceiptLink> All()
    {
        lock (Gate)
            return Load().ToList();
    }

    public static int Count
    {
        get
        {
            lock (Gate)
                return Load().Count;
        }
    }

    /// <summary>Привязать телефон к чату (согласие получено). Тот же чат с другим номером — номер заменяется; тот же номер в другом чате — оба чата получают чек.</summary>
    public static void Link(string? phone, string chatId, string? name)
    {
        var key = PhoneKey(phone); // 2026-11-06: В3 Л5-19 — номер целиком, а не последние 9 цифр
        if (LastDigits(phone).Length < 9 || string.IsNullOrWhiteSpace(chatId))
            return;
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(l => l.ChatId == chatId);
            list.Add(new TelegramReceiptLink { Phone = key, ChatId = chatId, Name = name, Bot = BotName(), LinkedAt = DateTimeOffset.Now });
            Save(list);
        }
        PosLogger.Log("Чеки в Telegram: покупатель привязал номер к боту.", "TELEGRAM");
    }

    /// <summary>Отписка (/stop или бот заблокирован): убирает все привязки этого чата. true — что-то было.</summary>
    public static bool Unlink(string chatId)
    {
        lock (Gate)
        {
            var list = Load();
            if (list.RemoveAll(l => l.ChatId == chatId) == 0)
                return false;
            Save(list);
        }
        PosLogger.Log("Чеки в Telegram: покупатель отписался.", "TELEGRAM");
        return true;
    }

    /// <summary>Чаты, куда слать чек этому телефону (согласие дано нынешнему боту); пусто — не привязан.</summary>
    public static IReadOnlyList<string> ChatsForPhone(string? phone)
    {
        if (LastDigits(phone).Length < 9)
            return Array.Empty<string>();
        var key = PhoneKey(phone); // 2026-11-06: В3 Л5-19 — сравнение номера целиком (старые привязки на 9 цифр — только для 996, см. LegacyLinkMatches)
        var bot = BotName();
        lock (Gate)
            return Load().Where(l => (l.Phone == key || LegacyLinkMatches(l.Phone, key)) && (l.Bot is null || bot is null || string.Equals(l.Bot, bot, StringComparison.OrdinalIgnoreCase)))
                .Select(l => l.ChatId).Distinct().ToList();
    }

    /// <summary>Тесты: другая папка и сброс кеша.</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            _cache = null;
            _stamp = default;
        }
    }

    private static List<TelegramReceiptLink> Load()
    {
        var stamp = DateTime.MinValue;
        try { stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue; } catch (Exception) { /* файл занят — берём кеш */ }
        if (_cache is not null && stamp == _stamp)
            return _cache;
        try
        {
            _cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<TelegramReceiptLink>>(AtomicFile.ReadAllText(FilePath) ?? "[]", Json) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки в Telegram: файл связей не прочитан ({ex.GetType().Name}) — список пуст.", "WARNING");
            _cache ??= new();
        }
        _stamp = stamp;
        return _cache;
    }

    private static void Save(List<TelegramReceiptLink> list)
    {
        _cache = list;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(list, Json));
            _stamp = File.GetLastWriteTimeUtc(FilePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки в Telegram: связи не записаны ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }
}

/// <summary>Результат ручной отправки чека кнопкой «Отправить → Telegram-бот».</summary>
public enum TelegramReceiptSendResult
{
    Sent,
    /// <summary>Функция выключена или бот не настроен.</summary>
    Off,
    /// <summary>У клиента нет телефона.</summary>
    NoPhone,
    /// <summary>Телефон не привязан к боту (покупатель не нажал «Старт»).</summary>
    NotLinked,
    Error,
}

public static class TelegramReceiptDelivery
{
    /// <summary>Функция включена и бот может писать покупателям (нужен токен; чат владельца для этого не нужен).</summary>
    public static bool IsAvailable =>
        AntiFraudSettingsStore.Current.Receipts.Enabled
        && !string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramBotToken)
        && TariffGate.CanUseTelegramBot;

    /// <summary>Текст сообщения (HTML): магазин, чек, строка «не забудьте» про долг, баланс бонусов, как отписаться.</summary>
    public static string BuildMessage(string receiptText, string? debtLine, double? bonusBalance, string? shopName)
    {
        var shop = Escape(string.IsNullOrWhiteSpace(shopName) ? "NurCRM" : shopName.Trim());
        var text = $"<b>{shop}</b>\n<pre>{Escape(receiptText.Trim())}</pre>";
        if (!string.IsNullOrWhiteSpace(debtLine))
            text += "\n⏰ " + Escape(debtLine!.Trim());
        if (bonusBalance is > 0.004)
            text += "\n🎁 " + Escape(Tr.T($"Бонусов на счёте: {bonusBalance:0.##}", $"Эсепте бонус: {bonusBalance:0.##}", $"Bonus balance: {bonusBalance:0.##}", $"Bonus bakiyesi: {bonusBalance:0.##}", $"Hisobdagi bonus: {bonusBalance:0.##}"));
        text += "\n\n<i>" + Escape(Tr.T("Чтобы не получать чеки в Telegram — напишите /stop.", "Telegram'да чек албаш үчүн /stop жазыңыз.", "To stop receiving receipts in Telegram, send /stop.",
            "Telegram'da fiş almak istemezseniz /stop yazın.", "Telegram'da chek olishni to'xtatish uchun /stop yozing.")) + "</i>";
        return text;
    }

    /// <summary>После продажи: если у клиента телефон привязан к боту — отправить чек. В фоне, ничего не бросает, кассиру ничего не показывает.</summary>
    public static void AfterSale(string? clientId, string? clientPhone, SaleReceiptCardData card)
    {
        if (!IsAvailable)
            return;
        var phone = !string.IsNullOrWhiteSpace(clientPhone) ? clientPhone : OfflineClientCache.Get(clientId)?.Phone;
        if (!TelegramReceiptLinks.IsUsablePhone(phone) || TelegramReceiptLinks.ChatsForPhone(phone).Count == 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await SendCardAsync(phone, clientId, card).ConfigureAwait(false);
                PosLogger.Log($"Чеки в Telegram: после продажи — {result}.", "TELEGRAM");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Чеки в Telegram: после продажи не отправлено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        });
    }

    /// <summary>То же из данных только что проведённой продажи (корзина-снимок, номер чека, способ оплаты): карточку чека строим здесь, как плашка «Продажа прошла».
    /// Дёшево уходит, если функция выключена или телефон не привязан (карточка не строится).</summary>
    public static void AfterSale(string? clientId, string? clientPhone, string? cartJson, string? saleNumber, double total, string? paymentMethod,
        string? cashReceivedText, DateTime soldAt, bool savedOffline)
    {
        try
        {
            if (!IsAvailable)
                return;
            var phone = !string.IsNullOrWhiteSpace(clientPhone) ? clientPhone : OfflineClientCache.Get(clientId)?.Phone;
            if (!TelegramReceiptLinks.IsUsablePhone(phone) || TelegramReceiptLinks.ChatsForPhone(phone).Count == 0)
                return;
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            var cart = doc.RootElement;
            var totals = CartTotalsCalculator.Calculate(cart);
            var card = SaleReceiptCardData.Build(cart, saleNumber, (decimal)(totals.LineDiscounts + totals.OrderDiscount), (decimal)total,
                paymentMethodOverride: paymentMethod, cashReceivedOverride: cashReceivedText, soldAt: soldAt, savedOffline: savedOffline);
            AfterSale(clientId, phone, card);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки в Telegram: чек для отправки не собран ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Отправить чек всем чатам, привязанным к телефону. Заблокировавшие бота отписываются.</summary>
    public static async Task<TelegramReceiptSendResult> SendCardAsync(string? phone, string? clientId, SaleReceiptCardData card, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return TelegramReceiptSendResult.Off;
        if (!TelegramReceiptLinks.IsUsablePhone(phone))
            return TelegramReceiptSendResult.NoPhone;
        var chats = TelegramReceiptLinks.ChatsForPhone(phone);
        if (chats.Count == 0)
            return TelegramReceiptSendResult.NotLinked;

        double? bonus = null;
        if (AntiFraudSettingsStore.Current.Receipts.Bonus && !string.IsNullOrWhiteSpace(clientId))
            bonus = await ServerLoyalty.GetBalanceAsync(clientId!, ct).ConfigureAwait(false) ?? ClientLoyaltyStore.GetBalance(clientId!);
        // Напоминание о долге: строка оплаты «долг» чека (остаток и срок) — предупредительным цветом на экране кассы, здесь — отдельной строкой.
        var debt = card.Payment.Where(r => r.Accent == 1).Select(r => $"{r.Label}: {r.Value}").FirstOrDefault();
        var message = BuildMessage(card.ToText(), debt, bonus, card.Shop);

        var ok = false;
        foreach (var chat in chats)
        {
            var error = await TelegramBotService.SendToAsync(chat, message, ct).ConfigureAwait(false);
            if (error is null)
                ok = true;
            else if (TelegramBotService.IsBlocked(chat))
                TelegramReceiptLinks.Unlink(chat);
            else
                PosLogger.Log($"Чеки в Telegram: сообщение не ушло ({error}).", "WARNING");
        }
        return ok ? TelegramReceiptSendResult.Sent : TelegramReceiptSendResult.Error;
    }

    /// <summary>Напоминание о долге клиенту, который привязал телефон (используется рассылкой должникам как запасной путь, если подписки по карточке нет).</summary>
    public static string? ChatForDebtReminder(string? phone) => TelegramReceiptLinks.ChatsForPhone(phone).FirstOrDefault();

    private static string Escape(string? text) => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
