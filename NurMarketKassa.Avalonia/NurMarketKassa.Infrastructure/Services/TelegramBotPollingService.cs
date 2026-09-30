using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// Бот владельца, отвечающий на команды: выручка, топ товаров, что заказать, должники.
/// Плюс подписка клиентов — тем, кто нажал «Старт» по ссылке из чека, касса может слать
/// напоминания о долге прямо в Telegram.
///
/// ЧЕСТНОЕ ОГРАНИЧЕНИЕ. Telegram не даёт написать человеку по номеру телефона: бот может
/// отвечать только тем, кто сам ему написал. Поэтому должнику сообщение уходит автоматически
/// ТОЛЬКО если он подписался на бота; для остальных владелец получает список с готовой
/// ссылкой WhatsApp и текстом напоминания — одно нажатие, и сообщение уже набрано.
/// Настоящая SMS-рассылка потребовала бы договора с оператором, которого нет.
///
/// Второе ограничение, тоже честное: бот отвечает, пока КАССА ВКЛЮЧЕНА. Своего сервера у
/// программы нет, новые сообщения спрашивает сама касса (getUpdates). Выключили кассу — бот
/// молчит и ответит, когда её включат снова.
/// </summary>
public sealed partial class TelegramBotPollingService
{
    /// <summary>Сколько секунд Telegram держит ответ, если новых сообщений нет. 25 — компромисс:
    /// реже дёргаем сеть, но заметно меньше 60-секундного таймаута HTTP-клиента.</summary>
    private const int LongPollSeconds = 25;

    /// <summary>2026-09-29: пауза между напоминаниями при рассылке (~25 в секунду).</summary>
    private const int ReminderPauseMs = 40;

    private readonly ISalesApiService _sales;
    private readonly IClientsApiService? _clients;
    /// <summary>2026-09-28: новые адреса NurCRM (должники, chat_id клиента, малый остаток) — см.
    /// TelegramBotPollingService.Server.cs. null — только старый путь, как было.</summary>
    private readonly ClientDebtsApiService? _debtsApi;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _offset;
    private string? _lastPollError;

    public TelegramBotPollingService(ISalesApiService sales, IClientsApiService? clients, ClientDebtsApiService? debtsApi = null)
    {
        _sales = sales;
        _clients = clients;
        _debtsApi = debtsApi;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Запускает опрос. Повторный вызов при уже запущенном опросе ничего не делает —
    /// вызывается и при входе в кассу, и при сохранении настроек бота.</summary>
    public void Start()
    {
        if (IsRunning)
            return;
        if (!TelegramBotService.IsConfigured || !UserPreferences.Instance.TelegramCommandsEnabled)
            return;

        // Тариф: на «Старт» бот — платная доп. услуга (Маркетплейс → Доп. функции).
        // На «Стандарт» и выше входит в тариф.
        if (!TariffGate.CanUseTelegramBot)
        {
            PosLogger.Log("Телеграм-бот не запущен: функция недоступна на текущем тарифе.", "TELEGRAM");
            return;
        }

        _cts = new CancellationTokenSource();

        // 2026-09-30: бот теперь может слушать и касса, и программа владельца на одном компьютере.
        // Два опроса одного токена Telegram не терпит (409 Conflict) — команды слушает та
        // программа, что заняла замок первой; вторая попробует снова при следующем вызове Start.
        if (!TryTakePollLock(_cts.Token))
        {
            _cts.Dispose();
            _cts = null;
            if (!_lockBusyLogged)
                PosLogger.Log("Телеграм-бот: команды уже слушает вторая программа на этом компьютере.", "TELEGRAM");
            _lockBusyLogged = true;
            return;
        }

        _lockBusyLogged = false;
        _loop = Task.Run(() => RunAsync(_cts.Token));
        PosLogger.Log("Телеграм-бот: опрос команд запущен.", "TELEGRAM");
    }

    private bool _lockBusyLogged;

    /// <summary>Замок на сеанс пользователя: держит его отдельный поток до отмены опроса. Mutex,
    /// а не семафор: если программа упадёт, Windows сама освободит замок (у следующего владельца
    /// AbandonedMutexException = замок получен).</summary>
    private static bool TryTakePollLock(CancellationToken ct)
    {
        using var decided = new ManualResetEventSlim();
        var taken = false;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, @"Local\NurMarketTelegramBotPoll");
            try
            {
                taken = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                taken = true;
            }

            decided.Set();
            if (!taken)
                return;
            ct.WaitHandle.WaitOne();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true,
            Name = "TelegramBotPollLock",
        };
        thread.Start();
        decided.Wait();
        return taken;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Уже остановлен — нормальная ситуация при закрытии кассы.
        }

        _cts = null;
        _loop = null;
    }

    /// <summary>Сообщение старше этого срока, пришедшее, пока бот был выключен, остаётся без ответа.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private static bool IsStale(JsonElement update) =>
        update.TryGetProperty("message", out var m)
        && m.TryGetProperty("date", out var d)
        && d.TryGetInt64(out var unix)
        && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix) > StaleAfter;

    private async Task RunAsync(CancellationToken ct)
    {
        // Первый круг делаем с offset = -1: Telegram отдаёт только ПОСЛЕДНЕЕ сообщение, и бот
        // не начнёт отвечать на всё, что владелец писал, пока касса была выключена.
        var first = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 2026-09-29 (стресс-тест бота): первый круг — без ожидания (timeout 0). С длинным
                // опросом первая же команда, пришедшая в течение 25 с после включения кассы или
                // сохранения настроек бота, считалась «старой» и молча пропускалась.
                // 2026-10-01, владелец: «опять сдох» — сообщения, пришедшие, пока касса перезапускалась
                // (обновление, 1–3 минуты), раньше выбрасывались все. Теперь первый круг берёт все
                // накопившиеся (offset 0), а старше StaleAfter пропускаются — на вчерашнее бот не отвечает.
                var (updates, error) = await TelegramBotService
                    .GetUpdatesAsync(first ? 0 : _offset, first ? 0 : LongPollSeconds, ct)
                    .ConfigureAwait(false);

                if (error != null)
                {
                    // Не засоряем журнал: одна и та же ошибка сети пишется один раз, а не
                    // каждые 25 секунд круглые сутки.
                    if (error != _lastPollError)
                    {
                        PosLogger.Log($"Телеграм-бот: опрос не удался ({error}).", "WARNING");
                        _lastPollError = error;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    continue;
                }

                _lastPollError = null;

                foreach (var update in updates)
                {
                    if (update.TryGetProperty("update_id", out var id) && id.TryGetInt64(out var updateId))
                        _offset = Math.Max(_offset, updateId + 1);

                    if (IsStale(update))
                        continue;   // пришло давно, пока бот был выключен, — не отвечаем

                    await HandleUpdateAsync(update, ct).ConfigureAwait(false);
                }

                first = false;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: сбой обработки ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleUpdateAsync(JsonElement update, CancellationToken ct)
    {
        if (!update.TryGetProperty("message", out var message)
            || !message.TryGetProperty("chat", out var chat)
            || !chat.TryGetProperty("id", out var chatIdElement))
        {
            return;
        }

        var chatId = chatIdElement.ValueKind == JsonValueKind.Number
            ? chatIdElement.GetInt64().ToString(CultureInfo.InvariantCulture)
            : chatIdElement.GetString();
        if (string.IsNullOrWhiteSpace(chatId))
            return;

        // 2026-09-29: группу владельца превратили в супергруппу — Telegram присылает служебное
        // сообщение с новым chat_id. Без этого команды из новой группы бот не считал владельцем.
        if (message.TryGetProperty("migrate_to_chat_id", out var migrateTo))
        {
            TelegramBotService.RememberMigratedChat(chatId!, migrateTo.ToString());
            return;
        }

        var text = message.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var isOwner = string.Equals(chatId, UserPreferences.Instance.TelegramChatId, StringComparison.Ordinal);
        var command = text!.Trim();

        // 2026-10-01, владелец: «фиксируй количество обращений клиентов к боту» — каждое сообщение
        // покупателя записывается (TelegramInquiryStore); владелец спрашивает «сколько обращений».
        var senderName = message.TryGetProperty("from", out var sender) && sender.TryGetProperty("first_name", out var senderFirst)
            ? senderFirst.GetString()
            : null;
        if (!isOwner)
            TelegramInquiryStore.Record(chatId!, senderName, text!);

        // «/команда@ИмяБота» — так Telegram присылает команды в групповых чатах.
        var at = command.IndexOf('@');
        var space = command.IndexOf(' ');
        var argument = space > 0 ? command[(space + 1)..].Trim() : "";
        if (space > 0)
            command = command[..space];
        if (at > 0 && (space < 0 || at < space))
            command = command[..at];

        command = command.TrimStart('/').ToLowerInvariant();

        // 2026-09-30: помощник — обычный текст без «/» понимается по смыслу («сколько заработали
        // сегодня», «кто должен», «цена кола»), см. TelegramAssistant. Бесплатно и без интернета.
        if (!text.TrimStart().StartsWith('/'))
        {
            var (mapped, direct, isChat) = TelegramAssistant.Understand(text, isOwner);

            // Разговор (приветствие, «почему…», непонятный вопрос) — отвечает нейросеть, если
            // владелец вписал бесплатный ключ Google Gemini; иначе — готовая подсказка помощника.
            if (isChat && isOwner && TelegramAiChat.IsConfigured)
            {
                _ = TelegramBotService.SendTypingAsync(chatId!, ct);
                var (answer, error) = await TelegramAiChat.AskAsync(chatId!, text, ct).ConfigureAwait(false);
                if (answer != null)
                {
                    await SendReplyAsync(chatId!, answer, ct).ConfigureAwait(false);
                    return;
                }

                PosLogger.Log($"ИИ-помощник: {error}", "TELEGRAM");
                if (direct != null)
                    direct = $"<i>ИИ сейчас недоступен: {Escape(error)}</i>\n\n" + direct;
            }

            if (direct != null)
            {
                await SendReplyAsync(chatId!, direct, ct).ConfigureAwait(false);
                return;
            }

            // 2026-09-30, решение владельца «консультант для всех»: покупатель (не владелец) пишет
            // обычный текст не про свой долг — отвечает ИИ-продавец только по каталогу (товары, цены,
            // наличие). Выручка, долги и чужие данные в его сводку не попадают вовсе.
            if (mapped == null && !isOwner && TelegramAiChat.IsConfigured)
            {
                _ = TelegramBotService.SendTypingAsync(chatId!, ct);
                var (answer, error) = await TelegramAiChat.AskCustomerAsync(chatId!, text, ct).ConfigureAwait(false);
                if (answer == null)
                    PosLogger.Log($"ИИ-консультант: {error}", "TELEGRAM");
                // ИИ недоступен — отвечаем сами по каталогу, а не пустым «извините».
                await SendReplyAsync(chatId!, answer ?? TelegramAssistant.CustomerFallback(text,
                        UserPreferences.Instance.StoreName, UserPreferences.Instance.OwnerPhone), ct)
                    .ConfigureAwait(false);
                return;
            }

            if (mapped == null)
                return;
            command = mapped;
            argument = "";
        }

        // 2026-09-30, владелец: «очень долго отвечает». Отчёты (советы, ABC) считаются по всей истории
        // продаж — владелец сразу видит «печатает…», а долгий расчёт пишется в журнал.
        if (isOwner)
            _ = TelegramBotService.SendTypingAsync(chatId!, ct);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var reply = command switch
        {
            "start" when !string.IsNullOrWhiteSpace(argument) => SubscribeClient(chatId!, argument, message),
            "start" when isOwner => TelegramReportBuilder.BuildHelp(),
            // 2026-09-30: с ИИ-консультантом — приглашение спросить о товарах.
            "start" when TelegramAiChat.IsConfigured =>
                $"Здравствуйте! Я помощник магазина «{Escape(UserPreferences.Instance.StoreName)}». "
                + "Спросите меня о товарах, ценах и наличии — отвечу сразу.",
            "start" => "Здравствуйте! Чтобы получать напоминания о задолженности, откройте ссылку, которую вам дали на кассе.",
            "help" or "помощь" when isOwner => TelegramReportBuilder.BuildHelp(),
            "segodnya" or "сегодня" when isOwner => TelegramReportBuilder.BuildRevenue(1, "Сегодня"),
            "nedelya" or "неделя" when isOwner => TelegramReportBuilder.BuildRevenue(7, "За 7 дней"),
            "top" or "топ" when isOwner => TelegramReportBuilder.BuildTopProducts(7),
            "abc" or "абс" or "авс" when isOwner => TelegramReportBuilder.BuildAbc(30),
            "sezon" or "сезон" or "сезонность" when isOwner => TelegramReportBuilder.BuildSeasonality(),
            "soveti" or "советы" or "рекомендации" when isOwner => TelegramReportBuilder.BuildRecommendations(30),
            "zakaz" or "заказ" when isOwner => TelegramReportBuilder.BuildRestockSuggestions(),
            // 2026-09-28: остатки теперь спрашиваются у сервера (quantity_lte, BE-05) — асинхронно,
            // ниже; каталог кассы — запасной путь.
            "ostatki" or "остатки" when isOwner => null,
            // Долги живут только на сервере — эти две команды требуют запроса и обрабатываются
            // ниже, асинхронно.
            "dolgi" or "долги" when isOwner => null,
            "dolg" or "долг" => null,
            _ when isOwner => TelegramReportBuilder.BuildHelp(),
            _ => null,
        };

        if (watch.ElapsedMilliseconds > 3000)
            PosLogger.Log($"Телеграм-бот: /{command} считался {watch.ElapsedMilliseconds / 1000.0:0.#} с.", "TELEGRAM");

        if (reply != null)
        {
            await SendReplyAsync(chatId!, reply, ct).ConfigureAwait(false);
            return;
        }

        if (isOwner && command is "ostatki" or "остатки")
        {
            var stock = await TryBuildLowStockFromServerAsync(ct).ConfigureAwait(false)
                ?? TelegramReportBuilder.BuildLowStock();
            await SendReplyAsync(chatId!, stock, ct).ConfigureAwait(false);
            return;
        }

        if (isOwner && command is "dolgi" or "долги")
        {
            await SendReplyAsync(chatId!, await BuildDebtorsReportAsync(ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);
            return;
        }

        if (command is "dolg" or "долг")
        {
            var clientId = TelegramSubscriberStore.GetClientId(chatId!);
            // 2026-09-28: подписался через другую кассу — связка есть только на сервере (BE-04).
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = await FindClientByChatOnServerAsync(chatId!, ct).ConfigureAwait(false);
            var answer = string.IsNullOrWhiteSpace(clientId)
                ? "Вы ещё не привязаны к карточке клиента. Откройте ссылку, которую вам дали на кассе."
                : await BuildClientDebtAsync(clientId!, ct).ConfigureAwait(false);
            await SendReplyAsync(chatId!, answer, ct).ConfigureAwait(false);
        }
    }

    /// <summary>2026-09-30: ответ с проверкой. Раньше ошибка Telegram (например, «не удалось разобрать
    /// разметку») терялась молча — владелец просто не получал ответа. Теперь причина пишется в
    /// журнал, а ответ повторяется простым текстом без оформления.</summary>
    private static async Task SendReplyAsync(string chatId, string text, CancellationToken ct)
    {
        var error = await TelegramBotService.SendToAsync(chatId, text, ct).ConfigureAwait(false);
        if (error == null)
            return;

        PosLogger.Log($"Телеграм-бот: ответ не отправлен ({error}) — повторяю простым текстом.", "TELEGRAM");
        var plain = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", ""));
        var retry = await TelegramBotService.SendToAsync(chatId, Escape(plain), ct).ConfigureAwait(false);
        if (retry != null)
            PosLogger.Log($"Телеграм-бот: и простым текстом не отправлено ({retry}).", "TELEGRAM");
    }

    /// <summary>«/start &lt;id клиента&gt;» — покупатель перешёл по персональной ссылке с чека и
    /// тем самым разрешил боту себе писать. Без этого шага Telegram написать ему не позволит.</summary>
    private string SubscribeClient(string chatId, string clientId, JsonElement message)
    {
        var name = message.TryGetProperty("from", out var from) && from.TryGetProperty("first_name", out var first)
            ? first.GetString()
            : null;

        // 2026-09-29: повторное «Старт» по той же ссылке — связка уже есть, сервер не трогаем.
        var alreadyBound = string.Equals(TelegramSubscriberStore.GetClientId(chatId), clientId.Trim(), StringComparison.OrdinalIgnoreCase);
        TelegramSubscriberStore.Subscribe(chatId, clientId.Trim(), name);
        PosLogger.Log($"Телеграм-бот: подписан клиент {clientId}.", "TELEGRAM");
        // 2026-09-28: и в карточку клиента на сервере — раньше связка жила только в этой кассе.
        if (!alreadyBound)
            PushSubscriberToServer(chatId, clientId.Trim());

        return "Готово! Теперь напоминания о задолженности и об акциях будут приходить сюда.\n"
             + "Команда /dolg покажет ваш текущий долг.";
    }

    /// <summary>Список должников для владельца. У каждого — сумма и готовая ссылка WhatsApp с
    /// уже набранным текстом: Telegram написать по номеру телефона не даёт, а WhatsApp по
    /// ссылке wa.me открывается в один тап и не требует никакого договора.</summary>
    public async Task<string> BuildDebtorsReportAsync(CancellationToken ct)
    {
        // 2026-09-28: сводка сервера одним запросом (BE-03); ниже — старый путь, запасной.
        if (await TryLoadServerDebtorsAsync(ct).ConfigureAwait(false) is { } serverDebtors)
            return BuildDebtorsReportFromServer(serverDebtors, TelegramSubscriberStore.TryGetChatId);

        List<JsonElement> debts;
        try
        {
            debts = await _sales.PosDebtSalesAsync(null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return "Не удалось получить долги с сервера: " + Escape(ex.Message);
        }

        var byClient = new Dictionary<string, (string Name, double Amount, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var debt in debts)
        {
            var clientId = ReadString(debt, "client");
            if (string.IsNullOrWhiteSpace(clientId))
                continue;

            // debt_amount — остаток долга; total — сумма чека. Если остатка в ответе нет,
            // берём сумму чека, иначе должник молча выпал бы из отчёта с нулём.
            var amount = ReadDouble(debt, "debt_amount") ?? ReadDouble(debt, "total") ?? 0;
            if (amount <= 0.005)
                continue;

            var name = ReadString(debt, "client_name") ?? "Без имени";
            if (byClient.TryGetValue(clientId!, out var existing))
                byClient[clientId!] = (existing.Name, existing.Amount + amount, existing.Count + 1);
            else
                byClient[clientId!] = (name, amount, 1);
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>Должники</b>");

        if (byClient.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("Непогашенных долгов нет.");
            return sb.ToString();
        }

        var phones = await LoadPhonesAsync(ct).ConfigureAwait(false);
        var total = byClient.Values.Sum(v => v.Amount);

        sb.AppendLine($"Всего: {total.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} сом " +
                      $"у {byClient.Count} чел.");
        sb.AppendLine();

        foreach (var (clientId, info) in byClient.OrderByDescending(x => x.Value.Amount).Take(25))
        {
            var amountText = info.Amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
            sb.AppendLine($"<b>{Escape(info.Name)}</b> — {amountText} сом ({info.Count} чек.)");

            if (TelegramSubscriberStore.TryGetChatId(clientId) is { } subscriberChat
                && !string.IsNullOrWhiteSpace(subscriberChat))
            {
                sb.AppendLine("  подписан на бота — напоминание уйдёт автоматически");
            }
            else if (phones.TryGetValue(clientId, out var phone) && !string.IsNullOrWhiteSpace(phone))
            {
                var digits = new string(phone.Where(char.IsDigit).ToArray());
                var reminder = Uri.EscapeDataString(
                    $"Здравствуйте, {info.Name}! Напоминаем о задолженности {amountText} сом. Спасибо!");
                sb.AppendLine($"  <a href=\"https://wa.me/{digits}?text={reminder}\">написать в WhatsApp</a>");
            }
            else
            {
                sb.AppendLine("  <i>нет телефона в карточке клиента</i>");
            }
        }

        return sb.ToString();
    }

    /// <summary>Долг конкретного клиента — ответ на «/dolg» самому покупателю.</summary>
    private async Task<string> BuildClientDebtAsync(string clientId, CancellationToken ct)
    {
        // 2026-09-28: долг клиента — из сводки должников сервера (BE-03); нет в сводке — долга нет.
        // 2026-09-29: сводка не старше минуты — см. TryLoadServerDebtorsAsync.
        if (await TryLoadServerDebtorsAsync(ct, DebtorsCacheForClients).ConfigureAwait(false) is { } serverDebtors)
        {
            var owed = serverDebtors
                .Where(d => string.Equals(d.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                .Sum(d => d.DebtTotal);
            return owed <= 0.005
                ? "За вами задолженности нет. Спасибо!"
                : $"Ваша задолженность: <b>{owed.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} сом</b>.";
        }

        try
        {
            var debts = await _sales.PosDebtSalesAsync(clientId, ct).ConfigureAwait(false);
            var total = debts.Sum(d => ReadDouble(d, "debt_amount") ?? ReadDouble(d, "total") ?? 0);

            return total <= 0.005
                ? "За вами задолженности нет. Спасибо!"
                : $"Ваша задолженность: <b>{total.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} сом</b>.";
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: долг клиента не получен ({ex.Message}).", "WARNING");
            return "Сейчас не получается проверить долг. Попробуйте позже.";
        }
    }

    /// <summary>Рассылает напоминания о долге ТЕМ, КТО ПОДПИСАН на бота. Возвращает, сколько
    /// сообщений ушло. Неподписанным написать нельзя — их владелец видит в /dolgi со ссылкой
    /// на WhatsApp.</summary>
    public async Task<int> SendDebtRemindersAsync(CancellationToken ct = default)
    {
        // 2026-09-28: должники и chat_id — с сервера (BE-03/BE-04): напоминание получит и тот,
        // кто нажал «Старт» через другую кассу; chat_id этой кассы — если на сервере его нет.
        if (await TryLoadServerDebtorsAsync(ct).ConfigureAwait(false) is { } serverDebtors)
        {
            var sentFromServer = 0;
            foreach (var debtor in serverDebtors.Where(d => d.DebtTotal > 0.005))
            {
                var debtorChat = !string.IsNullOrWhiteSpace(debtor.TelegramChatId)
                    ? debtor.TelegramChatId
                    : TelegramSubscriberStore.TryGetChatId(debtor.ClientId);
                if (string.IsNullOrWhiteSpace(debtorChat))
                    continue;

                var reminder = $"Напоминаем о задолженности: <b>{debtor.DebtTotal.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} сом</b>.";
                if (await TelegramBotService.SendToAsync(debtorChat!, reminder, ct).ConfigureAwait(false) == null)
                    sentFromServer++;
                // 2026-09-29: не больше ~25 сообщений в секунду — предел Telegram около 30; без паузы
                // на сотнях подписчиков шли отказы 429.
                await Task.Delay(ReminderPauseMs, ct).ConfigureAwait(false);
            }

            PosLogger.Log($"Телеграм-бот: напоминаний о долге отправлено {sentFromServer} (сводка сервера).", "TELEGRAM");
            return sentFromServer;
        }

        List<JsonElement> debts;
        try
        {
            debts = await _sales.PosDebtSalesAsync(null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: напоминания не отправлены ({ex.Message}).", "WARNING");
            return 0;
        }

        var byClient = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var debt in debts)
        {
            var clientId = ReadString(debt, "client");
            if (string.IsNullOrWhiteSpace(clientId))
                continue;

            var amount = ReadDouble(debt, "debt_amount") ?? ReadDouble(debt, "total") ?? 0;
            if (amount > 0.005)
                byClient[clientId!] = byClient.GetValueOrDefault(clientId!) + amount;
        }

        var sent = 0;
        foreach (var (clientId, amount) in byClient)
        {
            var chatId = TelegramSubscriberStore.TryGetChatId(clientId);
            if (string.IsNullOrWhiteSpace(chatId))
                continue;

            var text = $"Напоминаем о задолженности: <b>{amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} сом</b>.";
            if (await TelegramBotService.SendToAsync(chatId!, text, ct).ConfigureAwait(false) == null)
                sent++;
            await Task.Delay(ReminderPauseMs, ct).ConfigureAwait(false);
        }

        PosLogger.Log($"Телеграм-бот: напоминаний о долге отправлено {sent}.", "TELEGRAM");
        return sent;
    }

    private async Task<Dictionary<string, string>> LoadPhonesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_clients == null)
            return result;

        try
        {
            foreach (var client in await _clients.GetClientsAsync(null, ct).ConfigureAwait(false))
            {
                var id = ReadString(client, "id");
                var phone = ReadString(client, "phone");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(phone))
                    result[id!] = phone!;
            }
        }
        catch (Exception ex)
        {
            // Без телефонов отчёт всё равно полезен — суммы и имена в нём есть.
            PosLogger.Log($"Телеграм-бот: телефоны клиентов не загружены ({ex.Message}).", "WARNING");
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static double? ReadDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string Escape(string? text) => (text ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
