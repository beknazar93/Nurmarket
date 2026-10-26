using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>Телеграм-бот владельца: вечерняя сводка по смене и тревоги приходят ему в телефон,
/// пока он не в магазине.
///
/// Почему именно Telegram, а не «мобильное приложение»: касса стоит за роутером без внешнего
/// адреса, но ИСХОДЯЩИЙ HTTPS-запрос уходит откуда угодно. Значит, чтобы владелец получал
/// сводку, не нужно ни своего сервера, ни белого IP, ни пробросов портов, ни магазина
/// приложений — только бот, созданный за две минуты у @BotFather. Это единственная функция из
/// найденных, которая не требует ни одного внешнего договора.
///
/// ЧЕСТНОЕ ОГРАНИЧЕНИЕ, которое надо понимать: надёжно работает только направление
/// «касса → владелец». Обратное («владелец спрашивает бота») требует, чтобы касса сама
/// опрашивала Telegram, а значит работает, ТОЛЬКО ПОКА КАССА ВКЛЮЧЕНА (TelegramBotPollingService).
/// 2026-10-21: chat_id владельца при настройке — по одноразовому коду (TelegramBotOwner).</summary>
public static class TelegramBotService
{
    private const string ApiRoot = "https://api.telegram.org";

    /// <summary>Таймаут намеренно небольшой: отправка сводки не должна задерживать закрытие
    /// смены, а если Telegram недоступен — лучше быстро записать это в лог и идти дальше.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramBotToken)
        && !string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramChatId);

    /// <summary>Отправляет сообщение владельцу. Возвращает текст ошибки или null при успехе.
    /// Никогда не бросает исключение: вызывается из закрытия смены и с фоновых потоков.</summary>
    public static Task<string?> SendAsync(string text, CancellationToken ct = default)
    {
        var chatId = UserPreferences.Instance.TelegramChatId;
        if (string.IsNullOrWhiteSpace(chatId))
        {
            // 2026-10-21 (бот, К1): получатель — по коду из программы.
            return Task.FromResult<string?>(Tr.T(
                "Не определён получатель: нажмите «Определить получателя» и отправьте боту код из программы.",
                "Алуучу аныкталган жок: «Алуучуну аныктоо» баскычын басып, ботко программадагы кодду жөнөтүңүз.",
                "No recipient: press “Detect recipient” and send the code from the program to the bot.",
                "Alıcı yok: «Alıcıyı belirle»ye basın ve programdaki kodu bota gönderin.",
                "Qabul qiluvchi yo'q: «Qabul qiluvchini aniqlash»ni bosing va dasturdagi kodni botga yuboring."));
        }

        return SendToAsync(chatId!, text, ct);
    }

    /// <summary>2026-10-24 (ТЗ «Склад…», пакет Г1): заказ из бота — получатели по настройке «Группа заказов» (TelegramOrdersTarget):
    /// "owner" (по умолчанию) — как раньше, чат владельца (при подключённой группе — личка владельца); "group" — только группа; "both" — группа и личка владельца.
    /// Одинаковые чаты не дублируются. В группу телефон покупателя и сумма идут только при TelegramOrdersShowAmount (по умолчанию нет).
    /// Группа не принимает (бота удалили, 403/400) — заказ уходит владельцу в личку с пометкой «Не удалось отправить в группу».
    /// Сводки смены, отчёты, долги и прокат группе заказов не шлются никогда (они идут через <see cref="SendAsync"/>).
    /// <paramref name="heading"/> — готовая HTML-строка заголовка; остальное — обычный текст (экранируется здесь).
    /// Возвращает текст ошибки или null; не бросает исключений.</summary>
    public static async Task<string?> SendOrderAsync(string heading, string name, string? phone, IEnumerable<string> items,
        string? amountLine, string? comment, CancellationToken ct = default)
    {
        var itemList = items.ToList();
        string Compose(bool withPhone, bool withAmount, string? note) =>
            (note != null ? note + "\n" : "")
            + heading + "\n" + Escape(name) + (withPhone ? " · " + Escape(phone) : "")
            + "\n" + string.Join("\n", itemList.Select(t => "• " + Escape(t)))
            + (withAmount && !string.IsNullOrWhiteSpace(amountLine) ? "\n" + Escape(amountLine) : "")
            + (string.IsNullOrWhiteSpace(comment) ? "" : "\nКомментарий: " + Escape(comment));

        var prefs = UserPreferences.Instance;
        var groupId = prefs.TelegramOrdersChatId?.Trim();
        var target = prefs.TelegramOrdersTarget;
        if (string.IsNullOrEmpty(groupId) || target is not ("group" or "both" or "owner"))
            return await SendAsync(Compose(true, true, null), ct).ConfigureAwait(false); // группы нет — как раньше

        // Владельцу (личка): личный чат владельца, затем чат из настроек; саму группу заказов сюда не берём — в неё полные данные не идут.
        async Task<string?> ToOwnerAsync(string text, bool includeMainChat)
        {
            var destinations = new List<string>();
            if (TelegramBotOwner.OwnerPrivateChatId() is { } privateChat && privateChat != groupId)
                destinations.Add(privateChat);
            if (includeMainChat && prefs.TelegramChatId is { Length: > 0 } mainChat && mainChat != groupId && !destinations.Contains(mainChat))
                destinations.Add(mainChat);
            if (destinations.Count == 0)
                return Tr.T("Личный чат владельца не известен.", "Ээсинин жеке чаты белгисиз.", "The owner's private chat is unknown.",
                    "Sahibin özel sohbeti bilinmiyor.", "Eganing shaxsiy chati noma'lum.");
            string? error = null;
            foreach (var chat in destinations)
            {
                error = await SendToAsync(chat, text, ct).ConfigureAwait(false);
                if (error == null)
                    break;
            }
            return error;
        }

        if (target == "owner")
            return await ToOwnerAsync(Compose(true, true, null), includeMainChat: true).ConfigureAwait(false); // «только мне в личку»

        var showMore = prefs.TelegramOrdersShowAmount;
        var groupError = await SendToAsync(groupId, Compose(showMore, showMore, null), ct).ConfigureAwait(false);
        if (groupError != null)
            PosLogger.Log("Телеграм-бот: заказ в группу заказов не ушёл — отправляю владельцу.", "TELEGRAM");
        if (target == "group" && groupError == null)
            return null;

        // «Группа и личка», либо группа не приняла заказ (бота удалили, 403 / 400) — владельцу в личку, при отказе группы — с пометкой.
        var title = prefs.TelegramOrdersChatTitle;
        var note = groupError == null ? null : Tr.T($"⚠️ Не удалось отправить в группу «{Escape(title)}» — заказ пришёл только вам.",
            $"⚠️ «{Escape(title)}» тобуна жөнөтүү мүмкүн болгон жок — буйрутма сизге гана келди.",
            $"⚠️ Could not send to the group “{Escape(title)}” — the order came only to you.",
            $"⚠️ «{Escape(title)}» grubuna gönderilemedi — sipariş yalnızca size geldi.",
            $"⚠️ «{Escape(title)}» guruhiga yuborib bo'lmadi — buyurtma faqat sizga keldi.");
        var ownerError = await ToOwnerAsync(Compose(true, true, note), includeMainChat: groupError != null).ConfigureAwait(false);

        // null — заказ дошёл владельцу (для «both» группа уже получила своё).
        return ownerError;
    }

    /// <summary>Отправляет сообщение В КОНКРЕТНЫЙ чат — владельцу, клиенту-должнику или любому,
    /// кто подписался на бота. Отдельно от <see cref="SendAsync"/>, потому что получатель здесь
    /// не из настроек: рассылка напоминаний идёт по чатам подписавшихся клиентов.</summary>
    public static async Task<string?> SendToAsync(string chatId, string text, CancellationToken ct = default, object? replyMarkup = null)
    {
        var token = UserPreferences.Instance.TelegramBotToken;

        if (string.IsNullOrWhiteSpace(token))
            return "Не задан токен бота.";
        if (string.IsNullOrWhiteSpace(chatId))
            return "Не указан получатель сообщения.";
        // 2026-10-21 (бот, S8a): чат заблокировал бота — 10 минут не шлём ему вовсе (раньше 2 попытки и 4 строки WARNING на каждое сообщение).
        if (IsBlocked(chatId))
            return BlockedError;

        // 2026-09-29 (стресс-тест бота): отчёт длиннее 4096 символов (/dolgi, /ostatki с длинными
        // названиями) Telegram отклонял целиком — владелец не получал ничего. Теперь — частями по строкам.
        // 2026-10-21 (бот, ВАЖНО 7): часть, которую Telegram не принял из-за разметки, уходит простым текстом САМА (ссылки — адресом
        // в скобках), остальные части не повторяются. Раньше весь отчёт слался заново: часть 1 дублировалась, ссылки WhatsApp пропадали.
        // Кнопки меню (replyMarkup) — под последней частью.
        var parts = SplitForTelegram(text);
        for (var i = 0; i < parts.Count; i++)
        {
            var markup = i == parts.Count - 1 ? replyMarkup : null;
            var (error, newChat, kind) = await SendPartAsync(token!, chatId, parts[i], markup, ct).ConfigureAwait(false);
            chatId = newChat;
            if (kind == PartResult.Parse)
            {
                PosLogger.Log($"Телеграм-бот: часть {i + 1} из {parts.Count} не принята ({error}) — отправляю её простым текстом.", "TELEGRAM");
                (error, chatId, kind) = await SendPartAsync(token!, chatId, PlainPart(parts[i]), markup, ct).ConfigureAwait(false);
            }
            if (kind != PartResult.Ok)
                return error;
        }

        return null;
    }

    /// <summary>2026-10-06, владелец: «к ИИ и боту дай полный доступ к товарам». Сообщение с кнопками под ним
    /// (inline_keyboard: (текст, callback_data) в ряд). Ошибка — текст причины, null — отправлено.
    /// 2026-10-21: через общий ограничитель темпа и нарезку (раньше — мимо них, длинный текст резался посреди тега).</summary>
    public static Task<string?> SendWithButtonsAsync(string chatId, string text, IReadOnlyList<(string Text, string Data)> buttons, CancellationToken ct = default) =>
        SendToAsync(chatId, text, ct, new Dictionary<string, object?>
        {
            ["inline_keyboard"] = new[] { buttons.Select(b => new Dictionary<string, string> { ["text"] = b.Text, ["callback_data"] = b.Data }).ToArray() },
        });

    // ───── 2026-10-21 (бот, S8a): чаты, заблокировавшие бота ─────
    private const string BlockedError = "Telegram отклонил сообщение: Forbidden: bot was blocked by the user";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> BlockedChats = new(StringComparer.Ordinal);
    private static readonly TimeSpan BlockedMemory = TimeSpan.FromMinutes(10);

    /// <summary>Чат недавно ответил 403 (заблокировал бота, удалил аккаунт, выгнал из группы).</summary>
    public static bool IsBlocked(string chatId) =>
        BlockedChats.TryGetValue(chatId, out var at) && DateTime.UtcNow - at < BlockedMemory;

    /// <summary>Часть без разметки: теги убраны, ссылка — «текст (адрес)», чтобы WhatsApp-ссылки не терялись.</summary>
    private static string PlainPart(string html)
    {
        var withLinks = System.Text.RegularExpressions.Regex.Replace(html, "<a\\s+href=\"([^\"]*)\"[^>]*>(.*?)</a>",
            m => m.Groups[2].Value + " (" + m.Groups[1].Value + ")", System.Text.RegularExpressions.RegexOptions.Singleline);
        var plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(withLinks, "<[^>]+>", ""));
        return Escape(plain);
    }

    /// <summary>2026-10-21 (бот, идея 3): список команд в меню Telegram («/» в поле ввода) на 5 языках. scope null — для всех,
    /// иначе — для одного чата (владельца). Ошибка не критична — пишется в журнал один раз.</summary>
    internal static async Task SetMyCommandsAsync(object commands, object? scope, string? languageCode, CancellationToken ct)
    {
        var token = UserPreferences.Instance.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token))
            return;
        try
        {
            var payload = new Dictionary<string, object?> { ["commands"] = commands };
            if (scope != null)
                payload["scope"] = scope;
            if (languageCode != null)
                payload["language_code"] = languageCode;
            using var response = await Http.PostAsJsonAsync($"{ApiRoot}/bot{token}/setMyCommands", payload, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && !_commandsErrorLogged)
            {
                _commandsErrorLogged = true;
                PosLogger.Log($"Телеграм-бот: список команд не обновлён (код {(int)response.StatusCode}).", "TELEGRAM");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_commandsErrorLogged)
                PosLogger.Log($"Телеграм-бот: список команд не обновлён ({ex.GetType().Name}).", "TELEGRAM");
            _commandsErrorLogged = true;
        }
    }

    private static bool _commandsErrorLogged;

    /// <summary>Ответ на нажатие кнопки (убирает «часики» на кнопке) и, если задан messageId, убирает кнопки у сообщения.</summary>
    public static async Task AnswerCallbackAsync(string callbackId, string? toast, string? chatId, long? messageId, CancellationToken ct = default)
    {
        var token = UserPreferences.Instance.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token))
            return;
        try
        {
            using var r1 = await Http.PostAsJsonAsync($"{ApiRoot}/bot{token}/answerCallbackQuery",
                new Dictionary<string, object?> { ["callback_query_id"] = callbackId, ["text"] = toast ?? "" }, ct).ConfigureAwait(false);
            if (chatId != null && messageId != null)
            {
                using var r2 = await Http.PostAsJsonAsync($"{ApiRoot}/bot{token}/editMessageReplyMarkup",
                    new Dictionary<string, object?> { ["chat_id"] = chatId, ["message_id"] = messageId, ["reply_markup"] = new Dictionary<string, object?> { ["inline_keyboard"] = Array.Empty<object>() } }, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Не критично: результат действия придёт отдельным сообщением.
        }
    }

    /// <summary>2026-09-30: «печатает…» в чате, пока ИИ-помощник готовит ответ (несколько секунд) —
    /// чтобы владелец видел, что бот его услышал. Ошибка здесь ни на что не влияет.</summary>
    public static async Task SendTypingAsync(string chatId, CancellationToken ct = default)
    {
        var token = UserPreferences.Instance.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            return;
        try
        {
            using var response = await Http
                .PostAsJsonAsync($"{ApiRoot}/bot{token}/sendChatAction",
                    new Dictionary<string, object?> { ["chat_id"] = chatId, ["action"] = "typing" }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Не критично: это только индикатор.
        }
    }

    /// <summary>2026-10-01, владелец: «у нас есть обработка голосовых команд — ответ голосом тоже реализуй
    /// в боте». Скачивает голосовое сообщение (OGG/Opus) по file_id: getFile → файл. null — не вышло
    /// или файл больше 5 МБ (≈ 5 минут речи — для вопроса боту этого с запасом).</summary>
    public static async Task<byte[]?> DownloadFileAsync(string fileId, CancellationToken ct = default)
    {
        var token = UserPreferences.Instance.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fileId))
            return null;
        try
        {
            using var info = await Http.GetAsync($"{ApiRoot}/bot{token}/getFile?file_id={Uri.EscapeDataString(fileId)}", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await info.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("file_path", out var path))
                return null;
            if (result.TryGetProperty("file_size", out var size) && size.TryGetInt64(out var bytes) && bytes > 5_000_000)
                return null;
            return await Http.GetByteArrayAsync($"{ApiRoot}/file/bot{token}/{path.GetString()}", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: голосовое не скачано ({ex.GetType().Name}).", "TELEGRAM");
            return null;
        }
    }

    /// <summary>Голосовой ответ (OGG/Opus) — sendVoice. Ошибка — текст причины, null — отправлено.</summary>
    public static async Task<string?> SendVoiceAsync(string chatId, byte[] ogg, CancellationToken ct = default)
    {
        var token = UserPreferences.Instance.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            return "Не задан токен бота.";
        try
        {
            // 2026-10-21: голос — тоже сообщение, в общем пределе темпа.
            await TelegramBotRateLimiter.WaitAsync(chatId, ct).ConfigureAwait(false);
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId), "chat_id");
            var file = new ByteArrayContent(ogg);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/ogg");
            form.Add(file, "voice", "answer.ogg");
            using var response = await Http.PostAsync($"{ApiRoot}/bot{token}/sendVoice", form, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>Одно сообщение. 2026-09-29 (стресс-тест): на 429 «Too Many Requests» Telegram
    /// говорит, сколько подождать (retry_after), — раньше сообщение просто терялось (ответ владельцу,
    /// напоминание должнику при рассылке). Теперь ждём и повторяем, до трёх раз. Ответ 400 с
    /// migrate_to_chat_id — группу превратили в супергруппу: шлём в новый чат и запоминаем его.</summary>
    private enum PartResult { Ok, Parse, Blocked, Failed }

    /// <summary>2026-10-21 (бот, п. 4 и ВАЖНО 6): перед каждой отправкой — общий ограничитель темпа (≤25/с на бота, ~1/с в чат).
    /// 429 с любой паузой до 15 минут больше не теряет ответ (раньше — только до 60 с и 3 попыток): чат ждёт сам, остальные чаты —
    /// нет (очередь по чатам). 403 — без повторов, чат запоминается как заблокировавший.</summary>
    private static async Task<(string? Error, string ChatId, PartResult Kind)> SendPartAsync(string token, string chatId, string text, object? replyMarkup, CancellationToken ct)
    {
        var waited429 = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await TelegramBotRateLimiter.WaitAsync(chatId, ct).ConfigureAwait(false);
                var payload = new Dictionary<string, object?>
                {
                    ["chat_id"] = chatId,
                    ["text"] = text,
                    ["parse_mode"] = "HTML",
                    ["disable_web_page_preview"] = true,
                };
                if (replyMarkup != null)
                    payload["reply_markup"] = replyMarkup;

                using var response = await Http
                    .PostAsJsonAsync($"{ApiRoot}/bot{token}/sendMessage", payload, ct)
                    .ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return (null, chatId, PartResult.Ok);

                var code = (int)response.StatusCode;
                var (retryAfter, migrateTo) = TryReadParameters(body);
                if (code == 429 && attempt <= 6 && retryAfter is > 0 and <= 900)
                {
                    if (!waited429 && retryAfter > 5)
                        PosLogger.Log($"Телеграм-бот: Telegram просит подождать {retryAfter} с — ответ этому чату уйдёт позже, другие чаты не ждут.", "TELEGRAM");
                    waited429 = true;
                    TelegramBotRateLimiter.Penalize(chatId, retryAfter.Value);
                    continue;
                }

                // Сбой на стороне Telegram (502/503/504 от его шлюза) — до двух повторов через 2 и 4 с.
                if (code >= 500 && attempt <= 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(migrateTo) && migrateTo != chatId && attempt <= 3)
                {
                    RememberMigratedChat(chatId, migrateTo!);
                    chatId = migrateTo!;
                    continue;
                }

                // Telegram кладёт человекочитаемую причину в description — она гораздо полезнее
                // кода состояния («chat not found», «bot was blocked by the user»).
                var reason = TryReadDescription(body) ?? $"код {code}";
                if (code == 403)
                {
                    if (BlockedChats.TryAdd(chatId, DateTime.UtcNow) || !IsBlocked(chatId))
                        PosLogger.Log($"Telegram sendMessage failed: {reason} — этому чату 10 минут не пишем.", "WARNING");
                    BlockedChats[chatId] = DateTime.UtcNow;
                    return ("Telegram отклонил сообщение: " + reason, chatId, PartResult.Blocked);
                }
                if (code == 400 && reason.Contains("parse", StringComparison.OrdinalIgnoreCase))
                    return ("Telegram отклонил сообщение: " + reason, chatId, PartResult.Parse);
                PosLogger.Log($"Telegram sendMessage failed: {reason}", "WARNING");
                return ("Telegram отклонил сообщение: " + reason, chatId, PartResult.Failed);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return ("Telegram не ответил за 20 секунд — проверьте интернет.", chatId, PartResult.Failed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ("Отправка отменена.", chatId, PartResult.Failed);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Telegram send failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
                return ("Не удалось связаться с Telegram: " + ex.Message, chatId, PartResult.Failed);
            }
        }
    }

    /// <summary>2026-09-29: группа владельца стала супергруппой (Telegram меняет ей chat_id). Если это
    /// чат владельца из настроек — запоминаем новый, иначе сводки и ответы на команды уходили бы
    /// в пустоту, а команды из новой группы бот не считал бы командами владельца.</summary>
    public static void RememberMigratedChat(string oldChatId, string newChatId)
    {
        // 2026-10-24 (Г1): группа заказов тоже переезжает на новый chat_id (раньше обновлялся только чат владельца).
        TelegramBotOwner.RememberMigratedOrdersGroup(oldChatId, newChatId);
        var prefs = UserPreferences.Instance;
        if (!string.Equals(prefs.TelegramChatId, oldChatId, StringComparison.Ordinal))
            return;

        prefs.TelegramChatId = newChatId;
        prefs.SaveToDisk();
        PosLogger.Log($"Телеграм-бот: группа владельца стала супергруппой, получатель обновлён ({oldChatId} → {newChatId}).", "TELEGRAM");
    }

    /// <summary>Предел Telegram — 4096 символов ВИДИМОГО текста (теги и адреса ссылок не в счёт).
    /// Режем по строкам, чтобы не разорвать тег или HTML-сущность; строку длиннее предела — по
    /// символам без разметки (так бывает только на совсем уж странных данных).</summary>
    internal static List<string> SplitForTelegram(string text, int limit = 4000)
    {
        var parts = new List<string>();
        if (VisibleLength(text) <= limit)
        {
            parts.Add(text);
            return parts;
        }

        // 2026-10-21 (бот, S0): тег, открытый на одной строке и закрытый на другой (<i>…\n…</i>), на границе частей закрывается в конце
        // части и открывается заново в начале следующей — раньше такие части Telegram отклонял (S0: 2664 частей с битой разметкой).
        var open = new List<string>();
        string CloseAll() => string.Concat(Enumerable.Reverse(open).Select(t => "</" + TagName(t) + ">"));
        var current = new StringBuilder();
        var currentLength = 0;
        void Flush()
        {
            var body = current.ToString().TrimEnd();
            if (VisibleLength(body) > 0)
                parts.Add(body + CloseAll());
            current.Clear().Append(string.Concat(open));
            currentLength = 0;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var lineLength = VisibleLength(line) + 1;
            if (currentLength + lineLength > limit && currentLength > 0)
                Flush();

            if (lineLength > limit)
            {
                // Строка сама длиннее предела: режем без разметки, не разрывая суррогатную пару и сущность (&amp; — сначала
                // раскодируем, режем по символам, потом кодируем обратно).
                Flush();
                ApplyTags(line, open);
                var plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(line, "<[^>]*>", ""));
                while (plain.Length + 1 > limit)
                {
                    var cut = char.IsHighSurrogate(plain[limit - 1]) ? limit - 1 : limit;
                    parts.Add(Escape(plain[..cut]));
                    plain = plain[cut..];
                }
                current.Clear().Append(string.Concat(open));
                line = Escape(plain);
                lineLength = VisibleLength(line) + 1;
                current.Append(line).Append('\n');
                currentLength += lineLength;
                continue;
            }

            current.Append(line).Append('\n');
            currentLength += lineLength;
            ApplyTags(line, open);
        }

        var tail = current.ToString().TrimEnd();
        if (VisibleLength(tail) > 0)
            parts.Add(tail + CloseAll());
        return parts;
    }

    private static string TagName(string openTag)
    {
        var inner = openTag.Trim('<', '>', ' ');
        var space = inner.IndexOf(' ');
        return (space > 0 ? inner[..space] : inner).ToLowerInvariant();
    }

    /// <summary>Учесть теги строки в списке открытых: «&lt;b&gt;» — открыть, «&lt;/b&gt;» — закрыть последний такой.</summary>
    private static void ApplyTags(string line, List<string> open)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(line, "<[^>]*>"))
        {
            var tag = m.Value;
            if (tag.StartsWith("</", StringComparison.Ordinal))
            {
                var name = tag[2..^1].Trim().ToLowerInvariant();
                var index = open.FindLastIndex(t => TagName(t) == name);
                if (index >= 0)
                    open.RemoveAt(index);
            }
            else if (!tag.EndsWith("/>", StringComparison.Ordinal))
            {
                open.Add(tag);
            }
        }
    }

    private static int VisibleLength(string html)
    {
        var length = 0;
        var inTag = false;
        for (var i = 0; i < html.Length; i++)
        {
            var ch = html[i];
            if (ch == '<')
            {
                inTag = true;
            }
            else if (ch == '>' && inTag)
            {
                inTag = false;
            }
            else if (!inTag)
            {
                // &amp; &lt; &gt; &quot; — один символ на экране.
                if (ch == '&')
                {
                    var semicolon = html.IndexOf(';', i);
                    if (semicolon > i && semicolon - i <= 8)
                        i = semicolon;
                }

                length++;
            }
        }

        return length;
    }

    private static (int? RetryAfter, string? MigrateTo) TryReadParameters(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object)
                return (null, null);
            int? retry = p.TryGetProperty("retry_after", out var r) && r.TryGetInt32(out var seconds) ? seconds : null;
            var migrate = p.TryGetProperty("migrate_to_chat_id", out var m) ? m.ToString() : null;
            return (retry, migrate);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>Длинный опрос новых сообщений бота. <paramref name="offset"/> — id первого
    /// ещё не обработанного обновления (Telegram удаляет всё, что меньше него), timeout — во
    /// сколько секунд Telegram может держать ответ, если сообщений нет.
    ///
    /// Возвращает сырые объекты update. Разбирает их TelegramBotPollingService — здесь только
    /// транспорт, чтобы вся работа с сетью осталась в одном классе.</summary>
    public static async Task<(List<JsonElement> Updates, string? Error)> GetUpdatesAsync(
        long offset, int timeoutSeconds, CancellationToken ct)
    {
        var token = UserPreferences.Instance.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token))
            return (new List<JsonElement>(), "Не задан токен бота.");

        try
        {
            // Свой HttpClient с таймаутом больше серверного: у общего Http таймаут 20 секунд,
            // и длинный опрос на 25 обрывался бы на каждом круге.
            var url = $"{ApiRoot}/bot{token}/getUpdates?timeout={timeoutSeconds}&offset={offset}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await LongPollHttp.SendAsync(request, ct).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (new List<JsonElement>(), TryReadDescription(body) ?? $"код {(int)response.StatusCode}");

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Array)
            {
                return (new List<JsonElement>(), null);
            }

            return (result.EnumerateArray().Select(e => e.Clone()).ToList(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (new List<JsonElement>(), ex.Message);
        }
    }

    private static readonly HttpClient LongPollHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    // 2026-10-21 (бот, К1): TryDetectChatIdAsync («получатель = чат последнего сообщения боту от кого угодно») убран — получатель
    // и владелец подтверждаются одноразовым кодом: TelegramBotOwner.Begin (программа показывает код, владелец отправляет его боту).

    /// <summary>Проверяет токен и возвращает имя бота: username (для ссылок t.me) и
    /// человекочитаемое название. Используется мастером подключения, чтобы владелец увидел,
    /// ЧЕЙ токен он вставил, ещё до первой отправки.</summary>
    public static async Task<(string? Username, string? Title, string? Error)> GetBotInfoAsync(
        string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (null, null, "Вставьте токен бота.");

        try
        {
            using var response = await Http.GetAsync($"{ApiRoot}/bot{token.Trim()}/getMe", ct)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, null, "Telegram отклонил токен: "
                    + (TryReadDescription(body) ?? "проверьте, что он скопирован целиком"));
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var result))
                return (null, null, "Telegram ответил неожиданным образом.");

            var username = result.TryGetProperty("username", out var u) ? u.GetString() : null;
            var title = result.TryGetProperty("first_name", out var f) ? f.GetString() : null;
            return (username, title, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, null, "Telegram не ответил за 20 секунд — проверьте интернет.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Telegram getMe failed: {ex.Message}", "WARNING");
            return (null, null, "Не удалось связаться с Telegram: " + ex.Message);
        }
    }

    /// <summary>getMe — единственный способ узнать имя бота, не спрашивая его у владельца.
    /// Неудача здесь не критична: она лишь означает, что персональную ссылку покупателю
    /// пока не построить, поэтому ошибку не возвращаем, а только пишем в журнал.</summary>
    public static async Task TryRememberBotUsernameAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync($"{ApiRoot}/bot{token}/getMe", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var result)
                && result.TryGetProperty("username", out var username)
                && username.GetString() is { Length: > 0 } name)
            {
                UserPreferences.Instance.TelegramBotUsername = name;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Telegram getMe failed: {ex.Message}", "WARNING");
        }
    }

    private static string? TryReadDescription(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Вечерняя сводка по закрытой смене — то, ради чего функция и нужна.
    /// Все цифры уже посчитаны в ShiftModel и ShiftCashOperationsStore, здесь только
    /// форматирование.</summary>
    public static string BuildShiftSummary(ShiftModel shift, decimal deposits, decimal withdrawals, string? shopName)
    {
        string Money(decimal v) => v.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " сом";

        // 2026-10-21 (бот, сводка смены): все строки на языке программы владельца — раньше 2 строки были на языке интерфейса, остальные
        // по-русски (смесь языков).
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(shopName))
            sb.AppendLine($"<b>{Escape(shopName)}</b>");

        sb.AppendLine(Tr.T("<b>Смена закрыта</b>", "<b>Смена жабылды</b>", "<b>Shift closed</b>", "<b>Vardiya kapandı</b>", "<b>Smena yopildi</b>"));
        sb.AppendLine();
        sb.AppendLine(Tr.T("Кассир: ", "Кассир: ", "Cashier: ", "Kasiyer: ", "Kassir: ") + Escape(shift.Cashier));
        if (shift.OpenedAt is { } opened)
            sb.AppendLine(Tr.T("Открыта: ", "Ачылды: ", "Opened: ", "Açıldı: ", "Ochildi: ") + $"{opened:dd.MM.yyyy HH:mm}");
        if (shift.ClosedAt is { } closed)
            sb.AppendLine(Tr.T("Закрыта: ", "Жабылды: ", "Closed: ", "Kapandı: ", "Yopildi: ") + $"{closed:dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("<b>" + Tr.T("Выручка: ", "Түшүм: ", "Revenue: ", "Ciro: ", "Tushum: ") + Money(shift.Revenue) + "</b>");
        if (shift.SalesCount is { } count)
        {
            sb.AppendLine(Tr.T("Чеков: ", "Чектер: ", "Receipts: ", "Fiş: ", "Cheklar: ") + count);
            if (count > 0)
                sb.AppendLine(Tr.T("Средний чек: ", "Орточо чек: ", "Average receipt: ", "Ortalama fiş: ", "O'rtacha chek: ") + Money(shift.Revenue / count));
        }

        sb.AppendLine();
        if (shift.CashSales is { } cash)
            sb.AppendLine(Tr.T("Наличными: ", "Накталай: ", "Cash: ", "Nakit: ", "Naqd: ") + Money(cash));
        if (shift.NonCashSales is { } card)
            sb.AppendLine(Tr.T("Безналичными: ", "Накталай эмес: ", "Non-cash: ", "Nakitsiz: ", "Naqd emas: ") + Money(card));
        if (shift.DebtSales is { } debt && debt > 0m)
            sb.AppendLine(Tr.T("В долг: ", "Карызга: ", "On credit: ", "Veresiye: ", "Qarzga: ") + Money(debt));

        // Скидки и оплата бонусами берутся из локальной таблицы кассы: сервер отдаёт скидку
        // одной суммой, в которой бонусы неотличимы от обычной скидки.
        // Возвраты, списания, расход и оплата долгов — из локального журнала событий смены.
        var events = ShiftEventsStore.TotalsForShift(shift.Id);
        var eventLines = new (string Title, string Kind)[]
        {
            (Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar"), ShiftEventsStore.KindReturn),
            (Tr.T("Списания", "Эсептен чыгаруулар", "Write-offs", "Zayiatlar", "Hisobdan chiqarishlar"), ShiftEventsStore.KindWriteOff),
            (Tr.T("Расход", "Чыгым", "Expenses", "Giderler", "Xarajat"), ShiftEventsStore.KindExpense),
            // 2026-10-18, ревью: наличные и безнал — отдельно (безнал в ящик не попадает); второй круг — на 5 языках.
            (Tr.T("Оплата долгов наличными", "Карыздарды накталай төлөө", "Debt payments in cash", "Nakit borç ödemeleri", "Qarz to'lovlari naqd"), ShiftEventsStore.KindDebtPayment),
            (Tr.T("Оплата долгов безналом", "Карыздарды накталай эмес төлөө", "Debt payments non-cash", "Nakitsiz borç ödemeleri", "Qarz to'lovlari naqd emas"), ShiftEventsStore.KindDebtPaymentNonCash),
        };
        if (eventLines.Any(e => events.TryGetValue(e.Kind, out var v) && v > 0.005))
        {
            sb.AppendLine();
            foreach (var (title, kind) in eventLines)
            {
                if (events.TryGetValue(kind, out var value) && value > 0.005)
                    sb.AppendLine($"{title}: {Money((decimal)value)}");
            }
        }

        var adjustments = ClientLoyaltyStore.AdjustmentsForShift(shift.Id);
        if (adjustments.Discounts > 0.005 || adjustments.PointsRedeemed > 0.005)
        {
            sb.AppendLine();
            sb.AppendLine(Tr.T("Скидки: ", "Арзандатуулар: ", "Discounts: ", "İndirimler: ", "Chegirmalar: ") + Money((decimal)adjustments.Discounts)
                          + Tr.T($" ({adjustments.Receipts} чек.)", $" ({adjustments.Receipts} чек)", $" ({adjustments.Receipts} receipts)", $" ({adjustments.Receipts} fiş)", $" ({adjustments.Receipts} chek)"));
            if (adjustments.PointsRedeemed > 0.005)
                sb.AppendLine(Tr.T("Оплачено бонусами: ", "Бонус менен төлөндү: ", "Paid with bonuses: ", "Bonusla ödenen: ", "Bonus bilan to'langan: ") + Money((decimal)adjustments.PointsRedeemed));
        }

        if (deposits > 0m || withdrawals > 0m)
        {
            sb.AppendLine();
            if (deposits > 0m)
                sb.AppendLine(Tr.T("Внесения: +", "Салымдар: +", "Cash in: +", "Para girişi: +", "Kirim: +") + Money(deposits));
            if (withdrawals > 0m)
                sb.AppendLine(Tr.T("Изъятия: -", "Алуулар: -", "Cash out: -", "Para çıkışı: -", "Chiqim: -") + Money(withdrawals));
        }

        if (shift.OpeningCash is { } opening)
        {
            sb.AppendLine();
            sb.AppendLine(Tr.T("Начальная сумма: ", "Баштапкы сумма: ", "Opening cash: ", "Açılış tutarı: ", "Boshlang'ich summa: ") + Money(opening));
            if (shift.ClosingCash is { } actual)
            {
                // 2026-10-21 (бот, К3): ровно как бумажный отчёт смены (ShiftDetailsDialog ~760): сначала ожидаемый остаток СЕРВЕРА
                // (ExpectedCash), иначе формула сервера: с полем расхода сервера — начальная + наличные − расход (прочие приходы
                // сервер в ожидаемый остаток не включает), без него — как раньше, с внесениями. Раньше бот всегда прибавлял внесения
                // и не смотрел ExpectedCash: «Недостача/Излишек» в телефоне расходились с бумажкой на сумму внесений и оплат долгов.
                var expected = shift.ExpectedCash
                    ?? (shift.ExpenseTotal is not null
                        ? opening + (shift.CashSales ?? 0m) - withdrawals
                        : opening + (shift.CashSales ?? 0m) + deposits - withdrawals);
                var diff = actual - expected;
                sb.AppendLine(Tr.T("Ожидалось в кассе: ", "Кассада болушу керек: ", "Expected in the drawer: ", "Kasada olması gereken: ", "Kassada bo'lishi kerak: ") + Money(expected));
                sb.AppendLine(Tr.T("Фактически: ", "Иш жүзүндө: ", "Actual: ", "Gerçekte: ", "Haqiqatda: ") + Money(actual));
                if (Math.Abs(diff) >= 0.01m)
                    sb.AppendLine(diff > 0
                        ? "<b>" + Tr.T("Излишек: +", "Ашыкча: +", "Surplus: +", "Fazla: +", "Ortiqcha: +") + Money(diff) + "</b>"
                        : "<b>" + Tr.T("Недостача: ", "Жетишпестик: ", "Shortage: ", "Eksik: ", "Kamomad: ") + Money(diff) + "</b>");
                else
                    sb.AppendLine(Tr.T("Расхождений нет", "Айырма жок", "No discrepancy", "Fark yok", "Farq yo'q"));
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Экранирование под parse_mode=HTML: имя магазина или кассира с символом &lt;
    /// иначе развалит разметку, и Telegram отклонит всё сообщение целиком.</summary>
    private static string Escape(string? text) => (text ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>Короткое тревожное сообщение — для будущих уведомлений
    /// (смена не закрыта, товар кончился, аномалия у кассира).</summary>
    public static string BuildAlert(string title, string details) =>
        $"<b>⚠ {Escape(title)}</b>\n\n{Escape(details)}";
}
