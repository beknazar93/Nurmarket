using System.Security.Cryptography;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-21: кто владелец бота (бот, п. К1 и ВАЖНО 4 отчёта 20.10).
///
/// БЫЛО: «Определить получателя» брало чат ПОСЛЕДНЕГО сообщения боту от кого угодно — написал покупатель, владелец нажал кнопку, и
/// выручка, должники с телефонами и «Выполнить» уходили покупателю. В группе владельцем считался каждый участник.
///
/// СТАЛО: программа показывает одноразовый код (6 цифр, 10 минут), владелец отправляет его боту из своего Telegram (личный чат или
/// группа магазина: «/code 123456»). Получатель сводок = чат, откуда пришёл код; владелец = from.id отправителя (хранится здесь,
/// %APPDATA%\NurMarketKassa\telegram-owner.json, по id бота). В группе отчёты видят все участники, а должники, вопросы ИИ и
/// «Выполнить» — только подтверждённый владелец. Код, пришедший не от того, кто его ввёл в программе, угадать нельзя: 1 из миллиона,
/// не больше 5 попыток с чата.
/// </summary>
public static class TelegramBotOwner
{
    public sealed record ConfirmResult(bool Ok, string? ChatId, string? Title, string? Error);

    private sealed class Pending
    {
        public required string Code { get; init; }
        public required DateTime ExpiresUtc { get; init; }
        public TaskCompletionSource<ConfirmResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, int> WrongByChat { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Stored(string BotId, List<long> UserIds, DateTime ConfirmedAtUtc);

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private const int MaxWrongPerChat = 5;
    private static readonly object Gate = new();
    private static Pending? _pending;
    private static Stored? _stored;
    private static bool _loaded;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "telegram-owner.json");

    /// <summary>Номер бота — часть токена до «:» (не секрет: виден в адресе t.me). По нему старый владелец не переходит к новому боту.</summary>
    public static string? BotId(string? token)
    {
        var t = (token ?? "").Trim();
        var colon = t.IndexOf(':');
        return colon > 0 ? t[..colon] : null;
    }

    private static string? CurrentBotId => BotId(UserPreferences.Instance.TelegramBotToken);

    private static Stored? Current()
    {
        lock (Gate)
        {
            if (!_loaded)
            {
                try
                {
                    _stored = File.Exists(FilePath) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) : null;
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Телеграм-бот: файл владельца не прочитан ({ex.GetType().Name}).", "TELEGRAM");
                    _stored = null;
                }
                _loaded = true;
            }
            return _stored is { } s && s.BotId == CurrentBotId ? s : null;
        }
    }

    private static void Save(Stored? s)
    {
        lock (Gate)
        {
            _stored = s;
            _loaded = true;
            try
            {
                if (s is null)
                {
                    if (File.Exists(FilePath))
                        File.Delete(FilePath);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(s));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: файл владельца не сохранён ({ex.GetType().Name}).", "WARNING");
            }
        }
    }

    /// <summary>Подтверждён ли владелец (кодом или своим личным чатом).</summary>
    public static bool HasConfirmedOwner => Current() is { UserIds.Count: > 0 };

    /// <summary>Этот человек (from.id) — подтверждённый владелец.</summary>
    public static bool IsOwnerUser(long? userId) => userId is { } id && Current() is { } s && s.UserIds.Contains(id);

    /// <summary>Личный чат владельца из настроек: в личном чате id чата = id человека, так что это тот же владелец — запоминаем его,
    /// чтобы старые настройки (до 21.10) продолжали работать и в группе без нового кода.</summary>
    internal static void LearnFromPrivateChat(long userId)
    {
        if (CurrentBotId is not { } bot || IsOwnerUser(userId))
            return;
        var s = Current();
        var ids = s?.UserIds.ToList() ?? new List<long>();
        ids.Add(userId);
        Save(new Stored(bot, ids, s?.ConfirmedAtUtc ?? DateTime.UtcNow));
        PosLogger.Log("Телеграм-бот: владелец запомнен по личному чату из настроек.", "TELEGRAM");
    }

    /// <summary>Сменили токен (другой бот) — прежний владелец и получатель недействительны.</summary>
    public static void Reset()
    {
        Save(null);
        lock (Gate)
        {
            _pending?.Tcs.TrySetResult(new ConfirmResult(false, null, null, null));
            _pending = null;
        }
    }

    /// <summary>Начать подтверждение: код для показа в программе и задача, которая завершится, когда код придёт боту (или через 10 минут).
    /// Если бота в этой программе сейчас никто не слушает (первая настройка), код ждёт сама эта функция.</summary>
    public static (string Code, Task<ConfirmResult> Completion) Begin(CancellationToken ct = default)
    {
        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new Pending { Code = code, ExpiresUtc = DateTime.UtcNow + CodeLifetime };
        lock (Gate)
        {
            _pending?.Tcs.TrySetResult(new ConfirmResult(false, null, null, Tr.T("Код заменён новым.", "Код жаңысы менен алмаштырылды.",
                "The code was replaced with a new one.", "Kod yenisiyle değiştirildi.", "Kod yangisi bilan almashtirildi.")));
            _pending = pending;
        }

        _ = Task.Delay(CodeLifetime, CancellationToken.None).ContinueWith(_ => Expire(pending), TaskScheduler.Default);
        if (ct.CanBeCanceled)
            ct.Register(() => Finish(pending, new ConfirmResult(false, null, null, null)));
        if (TelegramBotPollingService.ActivePollers == 0)
            _ = Task.Run(() => ListenAsync(pending, ct), CancellationToken.None);
        PosLogger.Log("Телеграм-бот: ждём код подтверждения владельца (10 минут).", "TELEGRAM");
        return (code, pending.Tcs.Task);
    }

    private static void Expire(Pending p) => Finish(p, new ConfirmResult(false, null, null, Tr.T(
        "Код не пришёл за 10 минут. Нажмите кнопку ещё раз — будет новый код.", "Код 10 мүнөттө келген жок. Баскычты кайра басыңыз — жаңы код чыгат.",
        "The code did not arrive within 10 minutes. Press the button again to get a new code.", "Kod 10 dakika içinde gelmedi. Yeni kod için düğmeye tekrar basın.",
        "Kod 10 daqiqada kelmadi. Tugmani qayta bosing — yangi kod chiqadi.")));

    private static void Finish(Pending p, ConfirmResult r)
    {
        lock (Gate)
        {
            if (_pending == p)
                _pending = null;
        }
        p.Tcs.TrySetResult(r);
    }

    /// <summary>Похоже ли сообщение на код подтверждения: «123456», «/code 123456», «/start own_123456».</summary>
    private static string? ExtractCode(string text, out bool explicitForm)
    {
        var t = text.Trim();
        explicitForm = false;
        var lower = t.ToLowerInvariant();
        foreach (var prefix in new[] { "/start", "/code", "/kod", "/код" })
        {
            if (!lower.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var rest = t[prefix.Length..];
            if (rest.StartsWith('@'))
                rest = rest.Contains(' ') ? rest[rest.IndexOf(' ')..] : "";
            t = rest.Trim();
            explicitForm = prefix != "/start";
            break;
        }
        if (t.StartsWith("own_", StringComparison.OrdinalIgnoreCase))
        {
            t = t[4..];
            explicitForm = true;
        }
        t = t.Replace(" ", "");
        return t.Length == 6 && t.All(char.IsAsciiDigit) ? t : null;
    }

    /// <summary>Обработать код из сообщения боту. true — сообщение было кодом (reply — что ответить, null — молчать).</summary>
    internal static bool TryHandle(string text, string chatId, string? chatTitle, long? fromId, out string? reply)
    {
        reply = null;
        var code = ExtractCode(text, out var explicitForm);
        if (code is null)
            return false;

        Pending? p;
        lock (Gate)
            p = _pending is { } x && x.ExpiresUtc > DateTime.UtcNow ? x : null;
        if (p is null)
        {
            if (!explicitForm)
                return false;
            reply = TelegramBotText.T("Этот код уже не действует. Нажмите «Определить получателя» в программе — появится новый код.",
                "Бул код жараксыз. Программада «Алуучуну аныктоо» баскычын басыңыз — жаңы код чыгат.",
                "This code is no longer valid. Press “Detect recipient” in the program to get a new code.",
                "Bu kod artık geçerli değil. Yeni kod için programda «Alıcıyı belirle»ye basın.",
                "Bu kod endi yaroqsiz. Dasturda «Qabul qiluvchini aniqlash»ni bosing — yangi kod chiqadi.");
            return true;
        }

        lock (Gate)
        {
            var wrong = p.WrongByChat.GetValueOrDefault(chatId);
            if (wrong >= MaxWrongPerChat)
                return explicitForm; // попытки этого чата кончились — молчим
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(code), System.Text.Encoding.ASCII.GetBytes(p.Code)) || fromId is null)
            {
                p.WrongByChat[chatId] = wrong + 1;
                if (!explicitForm)
                    return false; // просто 6 цифр (номер заказа, часть телефона) — обычное сообщение
                reply = TelegramBotText.T("Код не подошёл. Проверьте цифры в программе.", "Код туура эмес. Программадагы сандарды текшериңиз.",
                    "Wrong code. Check the digits in the program.", "Kod yanlış. Programdaki rakamları kontrol edin.", "Kod noto'g'ri. Dasturdagi raqamlarni tekshiring.");
                return true;
            }
        }

        // Код верный: этот чат — получатель, этот человек — владелец.
        var prefs = UserPreferences.Instance;
        var title = string.IsNullOrWhiteSpace(chatTitle) ? null : chatTitle;
        prefs.TelegramChatId = chatId;
        prefs.TelegramChatTitle = title;
        prefs.SaveToDisk();
        if (CurrentBotId is { } bot)
            Save(new Stored(bot, new List<long> { fromId!.Value }, DateTime.UtcNow));
        PosLogger.Log($"Телеграм-бот: владелец подтверждён кодом (чат {(chatId.StartsWith('-') ? "группа" : "личный")}).", "TELEGRAM");
        Finish(p, new ConfirmResult(true, chatId, title, null));
        reply = TelegramBotText.T(
            $"✅ Готово! Вы подтверждены как владелец магазина «{Escape(prefs.StoreName)}». Сюда будут приходить сводки смен, а команды — в меню ниже.",
            $"✅ Даяр! Сиз «{Escape(prefs.StoreName)}» дүкөнүнүн ээси катары ырасталдыңыз. Смена жыйынтыктары ушул жерге келет, буйруктар — төмөнкү менюда.",
            $"✅ Done! You are confirmed as the owner of “{Escape(prefs.StoreName)}”. Shift summaries will come here; commands are in the menu below.",
            $"✅ Tamam! «{Escape(prefs.StoreName)}» mağazasının sahibi olarak doğrulandınız. Vardiya özetleri buraya gelecek, komutlar aşağıdaki menüde.",
            $"✅ Tayyor! Siz «{Escape(prefs.StoreName)}» do'konining egasi sifatida tasdiqlandingiz. Smena hisobotlari shu yerga keladi, buyruqlar — pastdagi menyuda.");
        return true;
    }

    /// <summary>Бота в программе никто не слушает (первая настройка) — ждём код сами, долгим опросом. Чужие сообщения в это время
    /// не обрабатываются: бот ещё не настроен и всё равно бы молчал.</summary>
    private static async Task ListenAsync(Pending p, CancellationToken ct)
    {
        long offset = 0;
        var first = true;
        var errors = 0;
        while (!p.Tcs.Task.IsCompleted && !ct.IsCancellationRequested)
        {
            if (TelegramBotPollingService.ActivePollers > 0)
            {
                // Опрос бота запустился — код увидит он.
                await Task.WhenAny(p.Tcs.Task, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
                continue;
            }
            List<JsonElement> updates;
            string? error;
            try
            {
                (updates, error) = await TelegramBotService.GetUpdatesAsync(offset, first ? 0 : 20, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            first = false;
            if (error != null)
            {
                // Опрос бота в этой программе запустился одновременно с нами (409) — дальше код увидит он.
                if (TelegramBotPollingService.ActivePollers > 0)
                    continue;
                if (error.Contains("webhook", StringComparison.OrdinalIgnoreCase))
                {
                    Finish(p, new ConfirmResult(false, null, null, Tr.T(
                        "Бот работает на сервере NurCRM — получателя подтверждает сервер: нажмите «Обновить настройки на сервере».",
                        "Бот NurCRM серверинде иштейт — алуучуну сервер ырастайт: «Сервердеги жөндөөлөрдү жаңыртуу» баскычын басыңыз.",
                        "The bot runs on the NurCRM server — the server confirms the recipient: press “Update the settings on the server”.",
                        "Bot NurCRM sunucusunda çalışıyor — alıcıyı sunucu doğrular: «Sunucudaki ayarları güncelle»ye basın.",
                        "Bot NurCRM serverida ishlaydi — qabul qiluvchini server tasdiqlaydi: «Serverdagi sozlamalarni yangilash»ni bosing.")));
                    return;
                }
                if (error.Contains("Conflict", StringComparison.OrdinalIgnoreCase) || error.Contains("terminated by other", StringComparison.OrdinalIgnoreCase))
                {
                    Finish(p, new ConfirmResult(false, null, null, Tr.T(
                        "Команды бота сейчас слушает другая программа на этом компьютере (касса или программа владельца) — подтвердите получателя в ней.",
                        "Боттун буйруктарын азыр бул компьютердеги башка программа угуп жатат (касса же ээсинин программасы) — алуучуну ошол жерден ырастаңыз.",
                        "Another program on this computer (the kassa or the owner app) is listening to the bot — confirm the recipient there.",
                        "Bot komutlarını şu anda bu bilgisayardaki başka bir program dinliyor (kasa veya sahip programı) — alıcıyı orada doğrulayın.",
                        "Bot buyruqlarini hozir shu kompyuterdagi boshqa dastur tinglayapti (kassa yoki ega dasturi) — qabul qiluvchini o'sha yerda tasdiqlang.")));
                    return;
                }
                if (++errors >= 6)
                {
                    Finish(p, new ConfirmResult(false, null, null, Tr.T("Нет связи с Telegram: ", "Telegram менен байланыш жок: ", "No connection to Telegram: ",
                        "Telegram ile bağlantı yok: ", "Telegram bilan aloqa yo'q: ") + error));
                    return;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            errors = 0;
            foreach (var u in updates)
            {
                if (u.TryGetProperty("update_id", out var idEl) && idEl.TryGetInt64(out var id))
                    offset = Math.Max(offset, id + 1);
                if (!u.TryGetProperty("message", out var m) || !m.TryGetProperty("text", out var textEl) || !m.TryGetProperty("chat", out var chat)
                    || !chat.TryGetProperty("id", out var chatIdEl))
                    continue;
                var chatId = chatIdEl.ToString();
                long? fromId = m.TryGetProperty("from", out var from) && from.TryGetProperty("id", out var fid) && fid.TryGetInt64(out var f) ? f : null;
                if (TryHandle(textEl.GetString() ?? "", chatId, ChatTitle(chat), fromId, out var reply) && reply != null)
                    await TelegramBotService.SendToAsync(chatId, reply, ct, TelegramBotText.OwnerKeyboard()).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Название чата для настроек: группа — её название, личный чат — имя (@ник).</summary>
    internal static string? ChatTitle(JsonElement chat)
    {
        if (chat.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } t)
            return t;
        var name = chat.TryGetProperty("first_name", out var first) ? first.GetString() : null;
        if (chat.TryGetProperty("username", out var user) && user.GetString() is { Length: > 0 } u)
            name = string.IsNullOrWhiteSpace(name) ? "@" + u : $"{name} (@{u})";
        return name;
    }

    private static string Escape(string? text) => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
