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
/// опрашивала Telegram, а значит работает, ТОЛЬКО ПОКА КАССА ВКЛЮЧЕНА. Поэтому здесь сделаны
/// уведомления, а не чат: опрос (<see cref="TryDetectChatIdAsync"/>) используется ровно один
/// раз — чтобы узнать chat_id владельца при настройке.</summary>
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
            return Task.FromResult<string?>(
                "Не определён получатель: напишите боту «/start» и нажмите «Определить получателя».");
        }

        return SendToAsync(chatId!, text, ct);
    }

    /// <summary>Отправляет сообщение В КОНКРЕТНЫЙ чат — владельцу, клиенту-должнику или любому,
    /// кто подписался на бота. Отдельно от <see cref="SendAsync"/>, потому что получатель здесь
    /// не из настроек: рассылка напоминаний идёт по чатам подписавшихся клиентов.</summary>
    public static async Task<string?> SendToAsync(string chatId, string text, CancellationToken ct = default)
    {
        var token = UserPreferences.Instance.TelegramBotToken;

        if (string.IsNullOrWhiteSpace(token))
            return "Не задан токен бота.";
        if (string.IsNullOrWhiteSpace(chatId))
            return "Не указан получатель сообщения.";

        // 2026-09-29 (стресс-тест бота): отчёт длиннее 4096 символов (/dolgi, /ostatki с длинными
        // названиями) Telegram отклонял целиком — владелец не получал ничего. Теперь — частями по строкам.
        foreach (var part in SplitForTelegram(text))
        {
            string? error;
            (error, chatId) = await SendPartAsync(token!, chatId, part, ct).ConfigureAwait(false);
            if (error != null)
                return error;
        }

        return null;
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
    private static async Task<(string? Error, string ChatId)> SendPartAsync(string token, string chatId, string text, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["chat_id"] = chatId,
                    ["text"] = text,
                    ["parse_mode"] = "HTML",
                    ["disable_web_page_preview"] = true,
                };

                using var response = await Http
                    .PostAsJsonAsync($"{ApiRoot}/bot{token}/sendMessage", payload, ct)
                    .ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return (null, chatId);

                var (retryAfter, migrateTo) = TryReadParameters(body);
                if ((int)response.StatusCode == 429 && attempt <= 3 && retryAfter is > 0 and <= 60)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryAfter.Value), ct).ConfigureAwait(false);
                    continue;
                }

                // Сбой на стороне Telegram (502/503/504 от его шлюза) — один повтор через 2 с.
                if ((int)response.StatusCode >= 500 && attempt == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
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
                var reason = TryReadDescription(body) ?? $"код {(int)response.StatusCode}";
                PosLogger.Log($"Telegram sendMessage failed: {reason}", "WARNING");
                return ("Telegram отклонил сообщение: " + reason, chatId);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return ("Telegram не ответил за 20 секунд — проверьте интернет.", chatId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ("Отправка отменена.", chatId);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Telegram send failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
                return ("Не удалось связаться с Telegram: " + ex.Message, chatId);
            }
        }
    }

    /// <summary>2026-09-29: группа владельца стала супергруппой (Telegram меняет ей chat_id). Если это
    /// чат владельца из настроек — запоминаем новый, иначе сводки и ответы на команды уходили бы
    /// в пустоту, а команды из новой группы бот не считал бы командами владельца.</summary>
    public static void RememberMigratedChat(string oldChatId, string newChatId)
    {
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

        var current = new StringBuilder();
        var currentLength = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            var lineLength = VisibleLength(line) + 1;
            if (currentLength + lineLength > limit && current.Length > 0)
            {
                parts.Add(current.ToString().TrimEnd());
                current.Clear();
                currentLength = 0;
            }

            while (lineLength > limit)
            {
                // Строка сама длиннее предела: режем без разметки, не разрывая суррогатную пару.
                var plain = System.Text.RegularExpressions.Regex.Replace(line, "<[^>]*>", "");
                var cut = char.IsHighSurrogate(plain[limit - 1]) ? limit - 1 : limit;
                parts.Add(Escape(System.Net.WebUtility.HtmlDecode(plain[..cut])));
                line = plain[cut..];
                lineLength = VisibleLength(line) + 1;
            }

            current.Append(line).Append('\n');
            currentLength += lineLength;
        }

        if (current.ToString().Trim().Length > 0)
            parts.Add(current.ToString().TrimEnd());
        return parts;
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

    /// <summary>Узнаёт chat_id владельца: он пишет боту «/start», мы читаем последние
    /// сообщения. Это единственное место, где касса опрашивает Telegram.</summary>
    public static async Task<(string? ChatId, string? DisplayName, string? Error)> TryDetectChatIdAsync(
        string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (null, null, "Сначала вставьте токен бота.");

        // Заодно запоминаем имя бота: без него не построить персональную ссылку покупателя
        // (t.me/ИмяБота?start=idКлиента), а другого места, где владелец его вводил бы, нет.
        await TryRememberBotUsernameAsync(token.Trim(), ct).ConfigureAwait(false);

        try
        {
            using var response = await Http.GetAsync($"{ApiRoot}/bot{token.Trim()}/getUpdates?limit=20", ct)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return (null, null, "Telegram отклонил токен: " + (TryReadDescription(body) ?? "проверьте, что он скопирован целиком"));

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.GetArrayLength() == 0)
            {
                return (null, null,
                    "Сообщений боту пока нет. Откройте бота в Telegram, нажмите «Старт» и повторите.");
            }

            // Берём последнее — если владелец писал боту несколько раз, актуален свежий чат.
            foreach (var update in result.EnumerateArray().Reverse())
            {
                if (!update.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("chat", out var chat)
                    || !chat.TryGetProperty("id", out var id))
                {
                    continue;
                }

                var name = chat.TryGetProperty("first_name", out var first) ? first.GetString() : null;
                if (chat.TryGetProperty("username", out var user) && user.GetString() is { Length: > 0 } u)
                    name = string.IsNullOrWhiteSpace(name) ? "@" + u : $"{name} (@{u})";

                return (id.ToString(), name, null);
            }

            return (null, null, "В сообщениях боту не нашлось чата. Напишите боту «/start» ещё раз.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Telegram getUpdates failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return (null, null, "Не удалось связаться с Telegram: " + ex.Message);
        }
    }

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
    private static async Task TryRememberBotUsernameAsync(string token, CancellationToken ct)
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

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(shopName))
            sb.AppendLine($"<b>{Escape(shopName)}</b>");

        sb.AppendLine("<b>Смена закрыта</b>");
        sb.AppendLine();
        sb.AppendLine($"Кассир: {Escape(shift.Cashier)}");
        if (shift.OpenedAt is { } opened)
            sb.AppendLine($"Открыта: {opened:dd.MM.yyyy HH:mm}");
        if (shift.ClosedAt is { } closed)
            sb.AppendLine($"Закрыта: {closed:dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"<b>Выручка: {Money(shift.Revenue)}</b>");
        if (shift.SalesCount is { } count)
        {
            sb.AppendLine($"Чеков: {count}");
            if (count > 0)
                sb.AppendLine($"Средний чек: {Money(shift.Revenue / count)}");
        }

        sb.AppendLine();
        if (shift.CashSales is { } cash)
            sb.AppendLine($"Наличными: {Money(cash)}");
        if (shift.NonCashSales is { } card)
            sb.AppendLine($"Безналичными: {Money(card)}");
        if (shift.DebtSales is { } debt && debt > 0m)
            sb.AppendLine($"В долг: {Money(debt)}");

        // Скидки и оплата бонусами берутся из локальной таблицы кассы: сервер отдаёт скидку
        // одной суммой, в которой бонусы неотличимы от обычной скидки.
        // Возвраты, списания, расход и оплата долгов — из локального журнала событий смены.
        var events = ShiftEventsStore.TotalsForShift(shift.Id);
        var eventLines = new (string Title, string Kind)[]
        {
            ("Возвраты", ShiftEventsStore.KindReturn),
            ("Списания", ShiftEventsStore.KindWriteOff),
            ("Расход", ShiftEventsStore.KindExpense),
            ("Оплата долгов", ShiftEventsStore.KindDebtPayment),
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
            sb.AppendLine($"Скидки: {Money((decimal)adjustments.Discounts)} ({adjustments.Receipts} чек.)");
            if (adjustments.PointsRedeemed > 0.005)
                sb.AppendLine($"Оплачено бонусами: {Money((decimal)adjustments.PointsRedeemed)}");
        }

        if (deposits > 0m || withdrawals > 0m)
        {
            sb.AppendLine();
            if (deposits > 0m)
                sb.AppendLine($"Внесения: +{Money(deposits)}");
            if (withdrawals > 0m)
                sb.AppendLine($"Изъятия: -{Money(withdrawals)}");
        }

        if (shift.OpeningCash is { } opening)
        {
            sb.AppendLine();
            sb.AppendLine($"Начальная сумма: {Money(opening)}");
            if (shift.ClosingCash is { } actual)
            {
                // Тот же расчёт, что в печатном отчёте смены (ShiftDetailsDialog) — чтобы
                // цифра в телефоне совпадала с бумажкой, которую держит кассир.
                var expected = opening + (shift.CashSales ?? 0m) + deposits - withdrawals;
                var diff = actual - expected;
                sb.AppendLine($"Ожидалось в кассе: {Money(expected)}");
                sb.AppendLine($"Фактически: {Money(actual)}");
                if (Math.Abs(diff) >= 0.01m)
                    sb.AppendLine(diff > 0 ? $"<b>Излишек: +{Money(diff)}</b>" : $"<b>Недостача: {Money(diff)}</b>");
                else
                    sb.AppendLine("Расхождений нет");
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
