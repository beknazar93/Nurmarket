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

    private static readonly TimeSpan ServerModeRecheck = TimeSpan.FromMinutes(5);

    /// <summary>2026-10-01, владелец: расчёт на 15 000 клиентов — учитывать масштабируемость. Раньше каждая программа спрашивала NurCRM о режиме бота раз в 5 минут:
    /// 15 000 клиентов × 2 программы = 100 запросов в секунду впустую. Теперь режим узнаём у самого
    /// Telegram (getUpdates отказывает, пока у бота вебхук, — это нагрузка не на NurCRM), а NurCRM
    /// спрашиваем при старте и раз в час.</summary>
    private static readonly TimeSpan SettingsRecheck = TimeSpan.FromMinutes(60);
    private DateTime _serverModeCheckedAt = DateTime.MinValue;
    private DateTime _serverModeUntil = DateTime.MinValue;
    private bool _serverModeLogged;

    /// <summary>2026-10-01: бот работает на сервере NurCRM (режим «server» в настройках бота сервера
    /// или вебхук у Telegram) — не чаще раза в 5 минут спрашиваем сервер.</summary>
    private async Task<bool> IsServerModeAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _serverModeUntil)
            return true;
        if (DateTime.UtcNow - _serverModeCheckedAt < SettingsRecheck)
            return false;
        _serverModeCheckedAt = DateTime.UtcNow;

        var api = ServerTelegramBotApi.Current;
        if (api is null)
            return false;
        try
        {
            var settings = await api.GetSettingsAsync(ct).ConfigureAwait(false);
            var server = settings?.IsServerMode == true;
            if (server)
            {
                _serverModeUntil = DateTime.UtcNow + ServerModeRecheck;
                if (!_serverModeLogged)
                    PosLogger.Log($"Телеграм-бот работает на сервере NurCRM (@{settings!.BotUsername}) — опрос в программе выключен.", "TELEGRAM");
                _serverModeLogged = true;
            }
            else if (_serverModeLogged)
            {
                PosLogger.Log("Телеграм-бот снова на этом компьютере — опрос включён.", "TELEGRAM");
                _serverModeLogged = false;
            }
            return server;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Сервер не ответил — работаем, как раньше: лучше ответить из программы, чем молчать.
            PosLogger.Log($"Телеграм-бот: режим бота на сервере не узнан ({ex.GetType().Name}).", "TELEGRAM");
            return false;
        }
    }

    /// <summary>Не больше стольких принятых, но не отвеченных сообщений: дальше новые ждут у Telegram (он хранит их сутки),
    /// а файл очереди остаётся маленьким.</summary>
    private const int MaxPendingUpdates = 300;

    private async Task RunAsync(CancellationToken ct)
    {
        // 2026-10-21 (бот, К4 и ВАЖНО 1): сообщения — в очередь по чатам (TelegramBotPollingService.Queue.cs), offset и необработанные
        // update_id — на диске: после перезапуска бот продолжает с того же места без повторных ответов.
        Interlocked.Increment(ref _activePollers);
        _inbox = UpdateInbox.Open(TelegramBotOwner.BotId(UserPreferences.Instance.TelegramBotToken) ?? "bot");
        _offset = _inbox.NextOffset;
        foreach (var (id, update, attempts) in _inbox.TakeRecovered())
        {
            if (attempts > 2 || IsStale(update))
            {
                if (attempts > 2)
                    PosLogger.Log("Телеграм-бот: сообщение дважды прерывало работу при запуске — пропускаю его.", "WARNING");
                _inbox.Done(id);
                continue;
            }
            Dispatch(id, update, ct);
        }
        _inbox.Flush();
        var flusher = FlushLoopAsync(ct);
        _ = PublishCommandsAsync(ct);

        var first = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Очередь переполнена (наплыв сообщений) — новые подождут у Telegram, пока бот отвечает на принятые.
                while (_inbox.PendingCount >= MaxPendingUpdates && !ct.IsCancellationRequested)
                    await Task.Delay(200, ct).ConfigureAwait(false);

                // 2026-10-01, ТЗ часть 5: бот перенесён на сервер NurCRM (вебхук, работает и при
                // выключенной программе) — здесь Telegram не опрашиваем, только раз в 5 минут
                // проверяем, не вернули ли бота на этот компьютер.
                if (await IsServerModeAsync(ct).ConfigureAwait(false))
                {
                    await Task.Delay(ServerModeRecheck, ct).ConfigureAwait(false);
                    first = true;
                    continue;
                }

                // 2026-09-29 (стресс-тест бота): первый круг — без ожидания (timeout 0). С длинным
                // опросом первая же команда, пришедшая в течение 25 с после включения кассы или
                // сохранения настроек бота, считалась «старой» и молча пропускалась.
                // 2026-10-01, владелец: «опять сдох» — сообщения, пришедшие, пока касса перезапускалась
                // (обновление, 1–3 минуты), раньше выбрасывались все. Теперь первый круг берёт все
                // накопившиеся (offset 0), а старше StaleAfter пропускаются — на вчерашнее бот не отвечает.
                // 2026-10-21: первый круг — с сохранённого offset (на диске), а не с 0: уже отвеченное не приходит повторно.
                var (updates, error) = await TelegramBotService
                    .GetUpdatesAsync(_offset, first ? 0 : LongPollSeconds, ct)
                    .ConfigureAwait(false);

                if (error != null && error.Contains("webhook", StringComparison.OrdinalIgnoreCase))
                {
                    // Telegram не отдаёт getUpdates, пока у бота вебхук — значит, бот на сервере.
                    _serverModeUntil = DateTime.UtcNow + ServerModeRecheck;
                    NurMarketKassa.Services.Api.ServerTelegramBotApi.NoteModeFromTelegram(true);
                    if (!_serverModeLogged)
                        PosLogger.Log("Телеграм-бот: у бота включён вебхук (бот работает на сервере NurCRM) — опрос в программе выключен.", "TELEGRAM");
                    _serverModeLogged = true;
                    continue;
                }

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
                if (_serverModeLogged)
                {
                    // 2026-10-01: Telegram снова отдаёт сообщения — вебхук снят, бот вернули на компьютер.
                    PosLogger.Log("Телеграм-бот снова на этом компьютере — опрос включён.", "TELEGRAM");
                    _serverModeLogged = false;
                    NurMarketKassa.Services.Api.ServerTelegramBotApi.NoteModeFromTelegram(false);
                }

                foreach (var update in updates)
                {
                    if (!update.TryGetProperty("update_id", out var id) || !id.TryGetInt64(out var updateId))
                        continue;
                    // Старше StaleAfter (пришло, пока бот был выключен) — не отвечаем; уже принятое — не повторяем.
                    if (_inbox.Accept(updateId, update, IsStale(update)))
                        Dispatch(updateId, update, ct);
                }

                _offset = _inbox.NextOffset;
                // На диск ДО следующего getUpdates: он подтверждает Telegram эту пачку.
                _inbox.Flush();
                first = false;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Сбой самого опроса (не обработки сообщения — та в своей очереди): короткая пауза, ответы в очереди идут дальше.
                PosLogger.Log($"Телеграм-бот: сбой опроса ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        try
        {
            await flusher.ConfigureAwait(false);
            await StopWorkersAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activePollers);
        }
    }

    private async Task HandleUpdateAsync(JsonElement update, CancellationToken ct)
    {
        // 2026-10-06: кнопки «Выполнить / Отмена» под предложенными ИИ действиями с товарами.
        if (update.TryGetProperty("callback_query", out var callback))
        {
            await HandleProductActionCallbackAsync(callback, ct).ConfigureAwait(false);
            return;
        }

        // 2026-10-24 (Г1): бота добавили в группу или удалили из неё (my_chat_member).
        if (update.TryGetProperty("my_chat_member", out var memberUpdate))
        {
            await HandleMyChatMemberAsync(memberUpdate, ct).ConfigureAwait(false);
            return;
        }

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

        // 2026-10-24 (Г1): группа заказов — бот в ней молчит. Из команд слушаем только /group, /groupoff, /help от подтверждённого владельца;
        // всё остальное (чужие команды, вопросы, «долг», голосовые) — без ответа, без ИИ, без журнала обращений и без запоминания языка.
        if (TelegramBotOwner.IsOrdersGroupOnly(chatId))
        {
            await HandleOrdersGroupAsync(message, chat, chatId!, ct).ConfigureAwait(false);
            return;
        }

        var text = message.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;

        // 2026-10-21 (бот, К1 и ВАЖНО 4): роли — по человеку (from.id), а не только по чату. В группе владельца отчёты видят все
        // участники, а должники, ИИ с данными магазина, «да / нет» и «Выполнить» — только подтверждённый кодом владелец. Личный чат
        // подтверждённого владельца — тоже чат владельца (если сводки идут в группу).
        var prefs = UserPreferences.Instance;
        var chatType = chat.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var isGroup = chatType is "group" or "supergroup" or "channel" || chatId!.StartsWith('-');
        var hasFrom = message.TryGetProperty("from", out var sender);
        long? fromId = hasFrom && sender.TryGetProperty("id", out var fromIdElement) && fromIdElement.TryGetInt64(out var fid) ? fid : null;
        var languageCode = hasFrom && sender.TryGetProperty("language_code", out var lcElement) ? lcElement.GetString() : null;
        var isOwnerChat = string.Equals(chatId, prefs.TelegramChatId, StringComparison.Ordinal);
        if (isOwnerChat && !isGroup && fromId is { } privateOwner)
            TelegramBotOwner.LearnFromPrivateChat(privateOwner);
        var ownerUser = TelegramBotOwner.IsOwnerUser(fromId);
        var isOwner = isOwnerChat || (!isGroup && ownerUser);
        var isOwnerFull = isOwner && (!isGroup || ownerUser);
        // 2026-10-21 (бот, идея 10): язык ответа — владельцу язык программы, покупателю язык его Telegram.
        var customerLanguage = TelegramBotText.FromCode(languageCode);
        TelegramBotText.Use(isOwner ? prefs.Language : customerLanguage ?? prefs.Language);
        if (!isOwner && customerLanguage is { } rememberLanguage)
            TelegramBotText.Remember(chatId, rememberLanguage);
        var ownerMenu = isOwner ? TelegramBotText.OwnerKeyboard() : null;
        var customerMenu = isOwner || isGroup ? null : TelegramBotText.CustomerKeyboard();
        var senderName = hasFrom && sender.TryGetProperty("first_name", out var senderFirst) ? senderFirst.GetString() : null;

        // 2026-10-24 (Г1): «/group 123456» в новой группе — подключение группы заказов (только от подтверждённого владельца).
        if (text != null && TelegramBotOwner.TryHandleGroup(text, chatId, TelegramBotOwner.ChatTitle(chat), chatType, fromId, out var groupCodeReply))
        {
            if (groupCodeReply != null)
                await SendReplyAsync(chatId, groupCodeReply, ct).ConfigureAwait(false);
            return;
        }

        // 2026-10-21 (К1): код подтверждения владельца из программы («123456», «/code 123456», «/start own_123456»).
        if (text != null && TelegramBotOwner.TryHandle(text, chatId, TelegramBotOwner.ChatTitle(chat), fromId, out var codeReply))
        {
            if (codeReply != null)
                await SendReplyAsync(chatId, codeReply, ct,
                    string.Equals(chatId, prefs.TelegramChatId, StringComparison.Ordinal) ? TelegramBotText.OwnerKeyboard() : null).ConfigureAwait(false);
            return;
        }

        // 2026-10-21 (ВАЖНО 3): номер телефона кнопкой «Подтвердить номер» — сверка с карточкой клиента.
        if (message.TryGetProperty("contact", out var contact))
        {
            if (!isGroup)
                await HandleContactAsync(chatId, fromId, contact, senderName, ct).ConfigureAwait(false);
            return;
        }

        // 2026-10-01, владелец: «ответ голосом тоже реализуй в боте». Голосовое → текст (Gemini),
        // дальше — как обычное сообщение; ответ уходит текстом (с расшифровкой) и голосом.
        var isVoice = false;
        ReplyVoiceTranscript.Value = null;
        if (string.IsNullOrWhiteSpace(text)
            && message.TryGetProperty("voice", out var voice)
            && voice.TryGetProperty("file_id", out var voiceFileId)
            && TelegramVoice.IsAvailable)
        {
            _ = TelegramBotService.SendTypingAsync(chatId!, ct);
            var audio = await TelegramBotService.DownloadFileAsync(voiceFileId.GetString() ?? "", ct).ConfigureAwait(false);
            text = audio == null ? null : await TelegramVoice.TranscribeAsync(audio, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                await SendReplyAsync(chatId!, TelegramBotText.T("Не расслышал 🙂 Повторите, пожалуйста, или напишите текстом.", "Угулбай калды 🙂 Кайталаңызчы же текст менен жазыңыз.",
                    "I didn't catch that 🙂 Please repeat or type it.", "Anlayamadım 🙂 Lütfen tekrarlayın ya da yazın.", "Eshitmadim 🙂 Iltimos, takrorlang yoki yozing."), ct).ConfigureAwait(false);
                return;
            }

            isVoice = true;
            ReplyVoiceTranscript.Value = text;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // 2026-10-21 (бот, идея 12): стикер, фото, пустое сообщение — подсказка вместо тишины (в группе молчим: там общаются люди).
            if (!isGroup)
                await SendReplyAsync(chatId, isOwner ? TelegramReportBuilder.BuildHelp() : CustomerHint(nonText: true), ct, ownerMenu ?? customerMenu).ConfigureAwait(false);
            return;
        }

        var command = text!.Trim();

        // 2026-10-01, владелец: «фиксируй количество обращений клиентов к боту» — каждое сообщение
        // покупателя записывается (TelegramInquiryStore); владелец спрашивает «сколько обращений».
        if (!isOwner)
            TelegramInquiryStore.Record(chatId!, senderName, (isVoice ? "🎤 " : "") + text!);

        // 2026-10-21 (бот, идея 3): кнопка меню («📊 Сегодня», «💳 Мой долг» на любом из 5 языков) — как команда.
        if (TelegramBotText.CommandForButton(command, isOwner) is { } buttonCommand)
            text = command = "/" + buttonCommand;

        // 2026-10-06, владелец: «к ИИ и боту дай полный доступ к товарам». Ответ «да» / «нет» на предложенные ИИ действия
        // с товарами (кнопки — в HandleProductActionCallbackAsync; текстом и голосом — здесь).
        if (isOwnerFull && ProductActionPlan.PendingToken(chatId!) is { } pendingToken && (ProductActionPlan.IsYes(text!) || ProductActionPlan.IsNo(text!)))
        {
            // 2026-10-16: со временем карточки — старая карточка, такая же как уже выполненная, отвечает «карточка устарела».
            var steps = ProductActionPlan.Take(pendingToken, chatId!, out var proposedUtc);
            if (steps is null)
                return;
            if (ProductActionPlan.IsNo(text!))
            {
                await SendReplyAsync(chatId!, NothingChangedText(), ct).ConfigureAwait(false);
                return;
            }
            _ = TelegramBotService.SendTypingAsync(chatId!, ct);
            var done = await ProductActionPlan.ExecuteAllAsync(steps, "Телеграм-бот", ct, proposedUtc).ConfigureAwait(false);
            await SendReplyAsync(chatId!, Escape(done), ct).ConfigureAwait(false);
            return;
        }

        // Просьба изменить товар («спиши 5 молока», «опиши кока-колу», «срок годности хлеба до…») — сразу к ИИ, а не в готовые отчёты.
        if (isOwnerFull && TelegramAiChat.IsConfigured && !text!.TrimStart().StartsWith('/') && ProductActionPlan.LooksLikeAction(text))
        {
            await AskOwnerAiAsync(chatId!, text, ct).ConfigureAwait(false);
            return;
        }

        // «/команда@ИмяБота» — так Telegram присылает команды в групповых чатах.
        var at = command.IndexOf('@');
        var space = command.IndexOf(' ');
        var argument = space > 0 ? command[(space + 1)..].Trim() : "";
        if (space > 0)
            command = command[..space];
        if (at > 0 && (space < 0 || at < space))
        {
            // 2026-10-21 (бот, S1 «startother»): «/start@ДругойБот …» — команда другому боту, не нам.
            var addressee = command[(at + 1)..];
            if (command.StartsWith('/') && !string.IsNullOrWhiteSpace(prefs.TelegramBotUsername)
                && System.Text.RegularExpressions.Regex.IsMatch(addressee, "^[A-Za-z0-9_]{3,}$")
                && !string.Equals(addressee, prefs.TelegramBotUsername!.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase))
                return;
            command = command[..at];
        }

        command = TelegramBotText.Alias(command.TrimStart('/').ToLowerInvariant());

        // 2026-09-30: помощник — обычный текст без «/» понимается по смыслу («сколько заработали
        // сегодня», «кто должен», «цена кола»), см. TelegramAssistant. Бесплатно и без интернета.
        if (!text.TrimStart().StartsWith('/'))
        {
            if (!isOwner && await TelegramAiChat.HandlePendingCustomerOrderAsync(chatId!, text, ct).ConfigureAwait(false) is { } orderReply)
            {
                await SendReplyAsync(chatId!, orderReply, ct).ConfigureAwait(false);
                return;
            }

            var (mapped, direct, isChat) = TelegramAssistant.Understand(text, isOwner, isOwnerFull);

            // Разговор (приветствие, «почему…», непонятный вопрос) — отвечает нейросеть, если
            // владелец вписал бесплатный ключ Google Gemini; иначе — готовая подсказка помощника.
            if (isChat && isOwnerFull && TelegramAiChat.IsConfigured)
            {
                // 2026-10-06: владельцу — ответ ИИ с возможными действиями с товарами (кнопки подтверждения).
                var error = await AskOwnerAiAsync(chatId!, text, ct).ConfigureAwait(false);
                if (error == null)
                    return;

                PosLogger.Log($"ИИ-помощник: {error}", "TELEGRAM");
                if (direct != null)
                    direct = TelegramBotText.T("<i>ИИ сейчас недоступен — отвечаю сам.</i>\n\n", "<i>ЖИ азыр иштебей жатат — өзүм жооп берем.</i>\n\n",
                        "<i>The AI is unavailable right now — answering myself.</i>\n\n", "<i>Yapay zekâ şu an kullanılamıyor — kendim yanıtlıyorum.</i>\n\n",
                        "<i>SI hozir ishlamayapti — o'zim javob beraman.</i>\n\n") + direct;
            }

            if (direct != null)
            {
                await SendReplyAsync(chatId!, direct, ct, ownerMenu).ConfigureAwait(false);
                return;
            }

            // 2026-09-30, решение владельца «консультант для всех»: покупатель (не владелец) пишет
            // обычный текст не про свой долг — отвечает ИИ-продавец только по каталогу (товары, цены,
            // наличие). Выручка, долги и чужие данные в его сводку не попадают вовсе.
            // 2026-10-21 (К4): не больше 8 вопросов ИИ одновременно; без ИИ — поиск по каталогу (раньше покупатель без ключа ИИ получал тишину).
            if (mapped == null && !isOwner && !isGroup)
            {
                string? answer = null;
                if (TelegramAiChat.IsConfigured)
                {
                    _ = TelegramBotService.SendTypingAsync(chatId!, ct);
                    var (aiAnswer, error) = await AskCustomerLimitedAsync(chatId!, text, ct).ConfigureAwait(false);
                    if (aiAnswer == null)
                        PosLogger.Log($"ИИ-консультант: {error}", "TELEGRAM");
                    answer = aiAnswer;
                }
                // ИИ недоступен — отвечаем сами по каталогу, а не пустым «извините».
                await SendReplyAsync(chatId!, answer ?? TelegramAssistant.CustomerFallback(text,
                        UserPreferences.Instance.StoreName, UserPreferences.Instance.OwnerPhone), ct, answer == null ? customerMenu : null)
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

        // 2026-10-21: команды владельца от покупателя — короткий ответ с меню (раньше тишина); должники в группе — только владельцу.
        var ownerCommand = command is "segodnya" or "сегодня" or "nedelya" or "неделя" or "mesyac" or "top" or "топ" or "abc" or "абс" or "авс"
            or "sezon" or "сезон" or "сезонность" or "soveti" or "советы" or "рекомендации" or "zakaz" or "заказ" or "ostatki" or "остатки"
            or "dolgi" or "долги";
        object? markup = null;
        var reply = command switch
        {
            "start" when !string.IsNullOrWhiteSpace(argument) => null,
            "start" or "menu" when isOwner => TelegramReportBuilder.BuildHelp(),
            "start" or "menu" when !isGroup => CustomerGreeting(),
            "help" or "помощь" when isOwner => TelegramReportBuilder.BuildHelp(),
            "help" when !isGroup => CustomerGreeting(),
            _ when ownerCommand && !isOwner => isGroup ? "" : OwnerOnlyForCustomer(),
            "dolgi" or "долги" when !isOwnerFull => OwnerOnlyInGroup(),
            "top" or "топ" => TelegramReportBuilder.BuildTopProducts(7),
            "abc" or "абс" or "авс" => TelegramReportBuilder.BuildAbc(30),
            "sezon" or "сезон" or "сезонность" => TelegramReportBuilder.BuildSeasonality(),
            "soveti" or "советы" or "рекомендации" => TelegramReportBuilder.BuildRecommendations(30),
            "zakaz" or "заказ" => TelegramReportBuilder.BuildRestockSuggestions(),
            // 2026-10-21 (К2): выручка — с сервера, как «Сводка» программы владельца (асинхронно, ниже).
            "segodnya" or "сегодня" or "nedelya" or "неделя" or "mesyac" => null,
            // 2026-09-28: остатки теперь спрашиваются у сервера (quantity_lte, BE-05) — асинхронно,
            // ниже; каталог кассы — запасной путь.
            "ostatki" or "остатки" => null,
            // Долги живут только на сервере — эти две команды требуют запроса и обрабатываются
            // ниже, асинхронно.
            "dolgi" or "долги" => null,
            "dolg" or "долг" => null,
            "catalog" when !isGroup => TelegramAssistant.CatalogOverview(UserPreferences.Instance.StoreName),
            "call" when !isGroup => ShopPhoneText(),
            "address" when !isGroup => ShopAddressText(),
            _ when isOwner => TelegramReportBuilder.BuildHelp(),
            // 2026-10-21 (идея 12): неизвестная команда покупателя — подсказка с меню (в группе молчим).
            _ => isGroup ? "" : CustomerHint(nonText: false),
        };
        if (reply != null)
            markup = isOwner ? ownerMenu : customerMenu;

        if (watch.ElapsedMilliseconds > 3000)
            PosLogger.Log($"Телеграм-бот: /{command} считался {watch.ElapsedMilliseconds / 1000.0:0.#} с.", "TELEGRAM");

        if (reply != null)
        {
            if (reply.Length > 0)
                await SendReplyAsync(chatId!, reply, ct, markup).ConfigureAwait(false);
            return;
        }

        if (command == "start")
        {
            var (startText, startMarkup) = await StartWithArgumentAsync(chatId, isOwner, isGroup, argument, ct).ConfigureAwait(false);
            await SendReplyAsync(chatId, startText, ct, startMarkup).ConfigureAwait(false);
            return;
        }

        if (isOwner && command is "segodnya" or "сегодня" or "nedelya" or "неделя" or "mesyac")
        {
            var period = command is "segodnya" or "сегодня" ? "today" : command == "mesyac" ? "month" : "week";
            await SendReplyAsync(chatId, await TelegramReportBuilder.BuildRevenueAsync(period, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return;
        }

        if (isOwner && command is "ostatki" or "остатки")
        {
            var stock = await TryBuildLowStockFromServerAsync(ct).ConfigureAwait(false)
                ?? TelegramReportBuilder.BuildLowStock();
            await SendReplyAsync(chatId!, stock, ct).ConfigureAwait(false);
            return;
        }

        if (isOwnerFull && command is "dolgi" or "долги")
        {
            await SendReplyAsync(chatId!, await BuildDebtorsReportAsync(ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);
            return;
        }

        if (command is "dolg" or "долг" && !isGroup)
        {
            var clientId = TelegramSubscriberStore.GetClientId(chatId!);
            // 2026-09-28: подписался через другую кассу — связка есть только на сервере (BE-04).
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = await FindClientByChatOnServerAsync(chatId!, ct).ConfigureAwait(false);
            var answer = string.IsNullOrWhiteSpace(clientId)
                ? TelegramBotText.T("Вы ещё не привязаны к карточке клиента. Откройте ссылку, которую вам дали на кассе.",
                    "Сиз азырынча кардардын картасына байланган жоксуз. Кассадан берилген шилтемени ачыңыз.",
                    "You are not linked to a customer card yet. Open the link you were given at the till.",
                    "Henüz bir müşteri kartına bağlı değilsiniz. Kasada size verilen bağlantıyı açın.",
                    "Siz hali mijoz kartasiga bog'lanmagansiz. Kassada berilgan havolani oching.")
                : await BuildClientDebtAsync(clientId!, ct).ConfigureAwait(false);
            await SendReplyAsync(chatId!, answer, ct, customerMenu).ConfigureAwait(false);
        }
    }

    private static string NothingChangedText() => TelegramBotText.T("Хорошо, ничего не меняю.", "Макул, эч нерсе өзгөртпөйм.", "OK, I'm not changing anything.",
        "Tamam, hiçbir şeyi değiştirmiyorum.", "Yaxshi, hech narsani o'zgartirmayman.");

    /// <summary>2026-09-30: ответ с проверкой. Раньше ошибка Telegram (например, «не удалось разобрать
    /// разметку») терялась молча — владелец просто не получал ответа. Теперь причина пишется в
    /// журнал, а ответ повторяется простым текстом без оформления.</summary>
    /// <summary>Расшифровка голосового вопроса, на который сейчас отвечаем (null — вопрос был текстом).
    /// Первый ответ показывает её курсивом и уходит ещё и голосом.</summary>
    private static readonly AsyncLocal<string?> ReplyVoiceTranscript = new();

    /// <summary>2026-10-06: ответ ИИ владельцу; предложенные действия с товарами — сообщением с кнопками «Выполнить / Отмена»
    /// (или ответ «да» / «нет»). null — ответ отправлен, иначе — причина, почему ИИ не ответил.</summary>
    private static async Task<string?> AskOwnerAiAsync(string chatId, string text, CancellationToken ct)
    {
        _ = TelegramBotService.SendTypingAsync(chatId, ct);
        var (answer, steps, error) = await TelegramAiChat.AskOwnerBotAsync(chatId, text, ct).ConfigureAwait(false);
        if (answer == null)
            return error ?? "нет ответа";
        await SendReplyAsync(chatId, answer, ct).ConfigureAwait(false);
        // 2026-10-19: шаги накладной (приход, новые товары с количеством) бот не проводит и «Выполнить» к ним не показывает — сразу
        // текст, куда идти; кнопки — только если остались другие действия.
        if (steps.Any(ProductActionPlan.IsPurchaseStep))
        {
            await SendReplyAsync(chatId, ProductActionPlan.PurchaseOnlyText(), ct).ConfigureAwait(false);
            PosLogger.Log($"Телеграм-бот: шаги накладной ({steps.Count(ProductActionPlan.IsPurchaseStep)}) — только закупкой в программе владельца.", "TELEGRAM");
            steps = steps.Where(st => !ProductActionPlan.IsPurchaseStep(st)).ToList();
        }
        if (steps.Count > 0)
        {
            var token = ProductActionPlan.Remember(chatId, steps);
            var list = TelegramBotText.T("<b>Подтвердите изменения:</b>\n", "<b>Өзгөртүүлөрдү ырастаңыз:</b>\n", "<b>Confirm the changes:</b>\n",
                           "<b>Değişiklikleri onaylayın:</b>\n", "<b>O'zgarishlarni tasdiqlang:</b>\n")
                       + string.Join("\n", steps.Select(st => "• " + Escape(ProductActionPlan.Describe(st))))
                       + TelegramBotText.T("\n\n<i>Можно ответить «да» или «нет».</i>", "\n\n<i>«ооба» же «жок» деп жооп берсе болот.</i>",
                           "\n\n<i>You can reply “yes” or “no”.</i>", "\n\n<i>«evet» veya «hayır» diye yanıtlayabilirsiniz.</i>", "\n\n<i>«ha» yoki «yo'q» deb javob berish mumkin.</i>");
            var sendError = await TelegramBotService.SendWithButtonsAsync(chatId, list,
                new[]
                {
                    (TelegramBotText.T("✅ Выполнить", "✅ Аткаруу", "✅ Do it", "✅ Uygula", "✅ Bajarish"), "pa:" + token + ":y"),
                    (TelegramBotText.T("✖ Отмена", "✖ Жокко чыгаруу", "✖ Cancel", "✖ İptal", "✖ Bekor qilish"), "pa:" + token + ":n"),
                }, ct).ConfigureAwait(false);
            if (sendError != null)
                await SendReplyAsync(chatId, list, ct).ConfigureAwait(false);
            PosLogger.Log($"Телеграм-бот: ИИ предложил действий с товарами: {steps.Count}.", "TELEGRAM");
        }
        return null;
    }

    /// <summary>Нажатие «Выполнить / Отмена» под действиями с товарами — только из чата владельца.</summary>
    private static async Task HandleProductActionCallbackAsync(JsonElement callback, CancellationToken ct)
    {
        var id = callback.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var data = callback.TryGetProperty("data", out var dataEl) ? dataEl.GetString() ?? "" : "";
        string? chatId = null;
        long? messageId = null;
        if (callback.TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var cid))
                chatId = cid.ValueKind == JsonValueKind.Number ? cid.GetInt64().ToString(CultureInfo.InvariantCulture) : cid.GetString();
            if (msg.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out var m))
                messageId = m;
        }
        // 2026-10-24 (Г1): в группе заказов бот молчит, кнопок туда не шлёт — нажатие молча гасим.
        if (TelegramBotOwner.IsOrdersGroupOnly(chatId))
        {
            await TelegramBotService.AnswerCallbackAsync(id, null, null, null, ct).ConfigureAwait(false);
            return;
        }
        var parts = data.Split(':');
        if (parts.Length != 3 || parts[0] != "pa" || chatId == null)
        {
            await TelegramBotService.AnswerCallbackAsync(id, null, null, null, ct).ConfigureAwait(false);
            return;
        }
        // 2026-10-21 (бот, ВАЖНО 4): нажать «Выполнить» может только сам владелец (from.id), а не любой участник группы владельца.
        TelegramBotText.Use(UserPreferences.Instance.Language);
        long? pressedBy = callback.TryGetProperty("from", out var from) && from.TryGetProperty("id", out var fromIdEl) && fromIdEl.TryGetInt64(out var f) ? f : null;
        var isGroupChat = chatId.StartsWith('-');
        if (!string.Equals(chatId, UserPreferences.Instance.TelegramChatId, StringComparison.Ordinal) && !(!isGroupChat && TelegramBotOwner.IsOwnerUser(pressedBy))
            || (isGroupChat && !TelegramBotOwner.IsOwnerUser(pressedBy))
            || (!isGroupChat && pressedBy is { } p && p.ToString(CultureInfo.InvariantCulture) != chatId))
        {
            await TelegramBotService.AnswerCallbackAsync(id, TelegramBotText.T("Только владелец может подтверждать изменения.", "Өзгөртүүлөрдү ээси гана ырастай алат.",
                "Only the owner can confirm changes.", "Değişiklikleri yalnızca sahip onaylayabilir.", "O'zgarishlarni faqat ega tasdiqlay oladi."), null, null, ct).ConfigureAwait(false);
            return;
        }
        // 2026-10-16: владелец дважды прислал ту же накладную — два ключа; нажатая старая карточка после выполнения новой отвечает
        // «карточка устарела» (ExecuteAllAsync по времени карточки), приход второй раз не проходит.
        var steps = ProductActionPlan.Take(parts[1], chatId, out var proposedUtc);
        if (steps is null)
        {
            await TelegramBotService.AnswerCallbackAsync(id, TelegramBotText.T("Уже выполнено или устарело.", "Аткарылган же эскирген.", "Already done or outdated.",
                "Zaten yapıldı veya eskidi.", "Allaqachon bajarilgan yoki eskirgan."), chatId, messageId, ct).ConfigureAwait(false);
            return;
        }
        if (parts[2] != "y")
        {
            await TelegramBotService.AnswerCallbackAsync(id, TelegramBotText.T("Отменено", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi"), chatId, messageId, ct).ConfigureAwait(false);
            await SendReplyAsync(chatId, NothingChangedText(), ct).ConfigureAwait(false);
            return;
        }
        await TelegramBotService.AnswerCallbackAsync(id, TelegramBotText.T("Выполняю…", "Аткарып жатам…", "Working on it…", "Uyguluyorum…", "Bajaryapman…"), chatId, messageId, ct).ConfigureAwait(false);
        var done = await ProductActionPlan.ExecuteAllAsync(steps, "Телеграм-бот", ct, proposedUtc).ConfigureAwait(false);
        await SendReplyAsync(chatId, Escape(done), ct).ConfigureAwait(false);
    }

    /// <summary>2026-10-24 (Г1): сообщение в группе заказов. Отвечает только владельцу на /group, /groupoff, /help.</summary>
    private static async Task HandleOrdersGroupAsync(JsonElement message, JsonElement chat, string chatId, CancellationToken ct)
    {
        var text = message.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            return;
        var type = chat.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        long? fromId = message.TryGetProperty("from", out var sender) && sender.TryGetProperty("id", out var fromIdElement) && fromIdElement.TryGetInt64(out var fid) ? fid : null;
        var reply = TelegramBotOwner.OrdersGroupReply(text, chatId, TelegramBotOwner.ChatTitle(chat), type, fromId);
        if (reply != null)
            await SendReplyAsync(chatId, reply, ct).ConfigureAwait(false);
    }

    /// <summary>2026-10-24 (Г1): my_chat_member — статус самого бота в чате изменился. Удалили из группы заказов (left / kicked) —
    /// группа отключается, владельцу в личку одно сообщение. Добавили в группу — только запись в журнал: подключение — только кодом.</summary>
    private static async Task HandleMyChatMemberAsync(JsonElement update, CancellationToken ct)
    {
        if (!update.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var idElement))
            return;
        var chatId = idElement.ValueKind == JsonValueKind.Number ? idElement.GetInt64().ToString(CultureInfo.InvariantCulture) : idElement.GetString();
        if (string.IsNullOrWhiteSpace(chatId))
            return;
        var type = chat.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (type is not ("group" or "supergroup" or "channel"))
            return;
        string? status = null;
        var isMember = true;
        if (update.TryGetProperty("new_chat_member", out var member))
        {
            status = member.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
            // «restricted» с is_member=false — бот тоже уже не в группе.
            if (member.TryGetProperty("is_member", out var memberFlag) && memberFlag.ValueKind == JsonValueKind.False)
                isMember = false;
        }

        if (status is "left" or "kicked" || (status == "restricted" && !isMember))
        {
            var title = UserPreferences.Instance.TelegramOrdersChatTitle;
            if (!TelegramBotOwner.MarkOrdersGroupRemoved(chatId!))
            {
                PosLogger.Log("Телеграм-бот: бота удалили из группы (это не группа заказов).", "TELEGRAM");
                return;
            }
            var owner = TelegramBotOwner.OwnerPrivateChatId();
            if (owner != null)
            {
                TelegramBotText.Use(UserPreferences.Instance.Language);
                var name = Escape(string.IsNullOrWhiteSpace(title) ? "" : title);
                await TelegramBotService.SendToAsync(owner, TelegramBotText.T(
                    $"⚠️ Бота удалили из группы «{name}». Заказы туда больше не идут — они приходят вам. Чтобы вернуть группу, добавьте бота обратно и нажмите «Определить группу» в программе.",
                    $"⚠️ Бот «{name}» тобунан чыгарылды. Буйрутмалар ал жакка башка барбайт — сизге келет. Топту кайтаруу үчүн ботту кайра кошуп, программада «Топту аныктоо» баскычын басыңыз.",
                    $"⚠️ The bot was removed from the group “{name}”. Orders no longer go there — they come to you. To restore the group, add the bot back and press “Detect group” in the program.",
                    $"⚠️ Bot «{name}» grubundan çıkarıldı. Siparişler artık oraya gitmiyor — size geliyor. Grubu geri getirmek için botu tekrar ekleyin ve programda «Grubu belirle»ye basın.",
                    $"⚠️ Bot «{name}» guruhidan olib tashlandi. Buyurtmalar endi u yerga bormaydi — sizga keladi. Guruhni qaytarish uchun botni qayta qo'shing va dasturda «Guruhni aniqlash»ni bosing."), ct).ConfigureAwait(false);
            }
        }
        else if (status is "member" or "administrator")
        {
            PosLogger.Log("Телеграм-бота добавили в группу (подключение группы заказов — только кодом /group).", "TELEGRAM");
        }
    }

    private static async Task SendReplyAsync(string chatId, string text, CancellationToken ct, object? markup = null)
    {
        var transcript = ReplyVoiceTranscript.Value;
        ReplyVoiceTranscript.Value = null;
        if (transcript != null)
        {
            var error0 = await TelegramBotService.SendToAsync(chatId, $"<i>🎤 {Escape(transcript)}</i>\n\n" + text, ct, markup).ConfigureAwait(false);
            if (error0 == null)
            {
                _ = SendVoiceReplyAsync(chatId, text, ct);
                return;
            }
        }

        // 2026-10-21 (бот, ВАЖНО 7): часть с битой разметкой SendToAsync сам повторяет простым текстом — весь ответ заново не шлём
        // (раньше часть 1 длинного отчёта дублировалась, а на 403 шли бесполезные повторы).
        var error = await TelegramBotService.SendToAsync(chatId, text, ct, markup).ConfigureAwait(false);
        if (error != null && !TelegramBotService.IsBlocked(chatId) && !ct.IsCancellationRequested)
            PosLogger.Log($"Телеграм-бот: ответ не отправлен ({error}).", "TELEGRAM");
    }

    /// <summary>Голосовой ответ вдогонку к текстовому: не задерживает текст, ошибка — только в журнал.</summary>
    private static async Task SendVoiceReplyAsync(string chatId, string text, CancellationToken ct)
    {
        try
        {
            var ogg = await TelegramVoice.SynthesizeAsync(text, ct).ConfigureAwait(false);
            if (ogg == null)
                return;
            var error = await TelegramBotService.SendVoiceAsync(chatId, ogg, ct).ConfigureAwait(false);
            if (error != null)
                PosLogger.Log($"Голос в боте: голосовой ответ не отправлен ({error}).", "TELEGRAM");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голос в боте: сбой озвучки ({ex.Message}).", "TELEGRAM");
        }
    }

    // 2026-10-21 (бот, ВАЖНО 3): «/start <id клиента>» больше не подписывает сразу — см. StartWithArgumentAsync (подпись ссылки и
    // сверка телефона) в TelegramBotPollingService.Menu.cs; подписка — CompleteSubscription.

    /// <summary>Список должников для владельца. У каждого — сумма и готовая ссылка WhatsApp с
    /// уже набранным текстом: Telegram написать по номеру телефона не даёт, а WhatsApp по
    /// ссылке wa.me открывается в один тап и не требует никакого договора.</summary>
    public async Task<string> BuildDebtorsReportAsync(CancellationToken ct)
    {
        // 2026-09-28: сводка сервера одним запросом (BE-03); ниже — старый путь, запасной.
        // 2026-10-21, «Нур Советник до 10» (О2): суммы — тот же точный расчёт, что «Сводка» и «Долги клиентов» программы
        // (ExactDebtorsProvider), телефоны и чаты — из сводки сервера. Быстрые списки не вычитали оплаты по сделкам (245 856 вместо 225 856).
        var exactDebtors = await TryExactDebtorsAsync(ct).ConfigureAwait(false);
        if (await TryLoadServerDebtorsAsync(ct).ConfigureAwait(false) is { } serverDebtors)
            return BuildDebtorsReportFromServer(exactDebtors is null ? serverDebtors : MergeExactDebtors(exactDebtors, serverDebtors), TelegramSubscriberStore.TryGetChatId);
        if (exactDebtors is not null)
            return BuildDebtorsReportFromServer(exactDebtors, TelegramSubscriberStore.TryGetChatId);

        List<JsonElement> debts;
        try
        {
            debts = await _sales.PosDebtSalesAsync(null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // 2026-10-21 (бот, S9): владельцу — понятный текст, техническая причина — в журнал (раньше сырой текст исключения).
            PosLogger.Log($"Телеграм-бот: долги с сервера не получены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return TelegramBotText.T("Сервер NurCRM сейчас не отвечает — список должников не получен. Попробуйте через пару минут или откройте «Долги клиентов» в программе.",
                "NurCRM сервери азыр жооп бербей жатат — карызкорлордун тизмеси алынган жок. Бир нече мүнөттөн кийин кайталаңыз же программадан «Кардарлардын карыздарын» ачыңыз.",
                "The NurCRM server is not responding — the debtor list was not received. Try again in a couple of minutes or open “Customer debts” in the program.",
                "NurCRM sunucusu şu an yanıt vermiyor — borçlu listesi alınamadı. Birkaç dakika sonra tekrar deneyin veya programda «Müşteri borçları»nı açın.",
                "NurCRM serveri hozir javob bermayapti — qarzdorlar ro'yxati olinmadi. Bir necha daqiqadan keyin urinib ko'ring yoki dasturda «Mijozlar qarzlari»ni oching.");
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

            var name = ReadString(debt, "client_name") ?? "";
            if (byClient.TryGetValue(clientId!, out var existing))
                byClient[clientId!] = (existing.Name, existing.Amount + amount, existing.Count + 1);
            else
                byClient[clientId!] = (name, amount, 1);
        }

        // 2026-10-21: тот же вид и те же 5 языков, что у отчёта по сводке сервера.
        var phones = byClient.Count == 0 ? new Dictionary<string, string>() : await LoadPhonesAsync(ct).ConfigureAwait(false);
        var list = byClient.Select(kv => new DebtorInfo(kv.Key, kv.Value.Name, phones.GetValueOrDefault(kv.Key), null, kv.Value.Amount, kv.Value.Count, null)).ToList();
        return BuildDebtorsReportFromServer(list, TelegramSubscriberStore.TryGetChatId);
    }

    private static string NoDebtText() => TelegramBotText.T("За вами задолженности нет. Спасибо!", "Сизде карыз жок. Рахмат!", "You have no debt. Thank you!",
        "Borcunuz yok. Teşekkürler!", "Sizda qarz yo'q. Rahmat!");

    private static string DebtText(double amount)
    {
        var sum = amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
        return TelegramBotText.T($"Ваша задолженность: <b>{sum} сом</b>.", $"Сиздин карызыңыз: <b>{sum} сом</b>.", $"Your debt: <b>{sum} som</b>.",
            $"Borcunuz: <b>{sum} som</b>.", $"Sizning qarzingiz: <b>{sum} so'm</b>.");
    }

    /// <summary>Долг конкретного клиента — ответ на «/dolg» самому покупателю.</summary>
    private async Task<string> BuildClientDebtAsync(string clientId, CancellationToken ct)
    {
        // 2026-10-21 (бот, S9): покупатель ждёт не дольше 15 с, даже если NurCRM «висит» (раньше — до 55 с, вместе со всеми).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // 2026-09-28: долг клиента — из сводки должников сервера (BE-03); нет в сводке — долга нет.
            // 2026-09-29: сводка не старше минуты — см. TryLoadServerDebtorsAsync.
            if (await TryLoadServerDebtorsAsync(timeout.Token, DebtorsCacheForClients).ConfigureAwait(false) is { } serverDebtors)
            {
                var owed = serverDebtors
                    .Where(d => string.Equals(d.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                    .Sum(d => d.DebtTotal);
                return owed <= 0.005 ? NoDebtText() : DebtText(owed);
            }

            var debts = await _sales.PosDebtSalesAsync(clientId, timeout.Token).ConfigureAwait(false);
            var total = debts.Sum(d => ReadDouble(d, "debt_amount") ?? ReadDouble(d, "total") ?? 0);
            return total <= 0.005 ? NoDebtText() : DebtText(total);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log($"Телеграм-бот: долг клиента не получен ({ex.GetType().Name}).", "WARNING");
            return TelegramBotText.T("Сейчас не получается проверить долг. Попробуйте позже.", "Азыр карызды текшерүү мүмкүн эмес. Кийинчерээк кайталаңыз.",
                "I can't check the debt right now. Please try later.", "Şu an borcu kontrol edemiyorum. Lütfen daha sonra deneyin.",
                "Hozir qarzni tekshirib bo'lmayapti. Keyinroq urinib ko'ring.");
        }
    }

    /// <summary>Сколько подписчиков пропущено в последней рассылке: им уже писали меньше 20 часов назад.</summary>
    public int LastRemindersSkippedRecent { get; private set; }

    /// <summary>Рассылает напоминания о долге ТЕМ, КТО ПОДПИСАН на бота. Возвращает, сколько
    /// сообщений ушло. Неподписанным написать нельзя — их владелец видит в /dolgi со ссылкой
    /// на WhatsApp.
    /// 2026-10-21 (бот, ВАЖНО 8): одному чату — не чаще раза в 20 часов (журнал «кому когда»: telegram-reminders.json); в тексте —
    /// название магазина и язык покупателя; заблокировавшие бота (403) отписываются и здесь, и в карточке на сервере.</summary>
    public async Task<int> SendDebtRemindersAsync(CancellationToken ct = default)
    {
        LastRemindersSkippedRecent = 0;
        // 2026-09-28: должники и chat_id — с сервера (BE-03/BE-04): напоминание получит и тот,
        // кто нажал «Старт» через другую кассу; chat_id этой кассы — если на сервере его нет.
        List<(string ClientId, double Amount, string? ServerChat)> targets;
        if (await TryLoadServerDebtorsAsync(ct).ConfigureAwait(false) is { } serverDebtors)
        {
            targets = serverDebtors.Where(d => d.DebtTotal > 0.005).Select(d => (d.ClientId, d.DebtTotal, d.TelegramChatId)).ToList();
        }
        else
        {
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
            targets = byClient.Select(kv => (kv.Key, kv.Value, (string?)null)).ToList();
        }

        var shop = Escape(UserPreferences.Instance.StoreName);
        var sent = 0;
        var blocked = 0;
        try
        {
            foreach (var (clientId, amount, serverChat) in targets)
            {
                var chatId = !string.IsNullOrWhiteSpace(serverChat) ? serverChat : TelegramSubscriberStore.TryGetChatId(clientId);
                if (string.IsNullOrWhiteSpace(chatId))
                    continue;
                if (TelegramReminderLog.SentRecently(chatId!))
                {
                    LastRemindersSkippedRecent++;
                    continue;
                }

                TelegramBotText.Use(TelegramBotText.ForChat(chatId!));
                var sum = amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
                var text = TelegramBotText.T(
                    $"<b>{shop}</b>\nНапоминаем о задолженности: <b>{sum} сом</b>. Спасибо, что вы с нами!\nУточнить долг — кнопка «💳 Мой долг».",
                    $"<b>{shop}</b>\nКарызыңызды эскертебиз: <b>{sum} сом</b>. Биз менен болгонуңузга рахмат!\nКарызды тактоо — «💳 Менин карызым» баскычы.",
                    $"<b>{shop}</b>\nA friendly reminder of your debt: <b>{sum} som</b>. Thank you for being with us!\nCheck the debt — the “💳 My debt” button.",
                    $"<b>{shop}</b>\nBorcunuzu hatırlatırız: <b>{sum} som</b>. Bizi tercih ettiğiniz için teşekkürler!\nBorcu görmek için — «💳 Borcum» düğmesi.",
                    $"<b>{shop}</b>\nQarzingizni eslatamiz: <b>{sum} so'm</b>. Biz bilan bo'lganingiz uchun rahmat!\nQarzni bilish — «💳 Mening qarzim» tugmasi.");
                var error = await TelegramBotService.SendToAsync(chatId!, text, ct, TelegramBotText.CustomerKeyboard()).ConfigureAwait(false);
                if (error == null)
                {
                    sent++;
                    TelegramReminderLog.MarkSent(chatId!, clientId, amount);
                }
                else if (TelegramBotService.IsBlocked(chatId!))
                {
                    // Заблокировал бота — отписываем: в следующий раз ему не пишем и не засоряем журнал.
                    blocked++;
                    TelegramSubscriberStore.Unsubscribe(chatId!);
                    if (!string.IsNullOrWhiteSpace(serverChat))
                        ClearSubscriberOnServer(clientId);
                }
                // 2026-09-29: не больше ~25 сообщений в секунду — предел Telegram около 30; без паузы
                // на сотнях подписчиков шли отказы 429. 2026-10-21: темп держит и TelegramBotRateLimiter.
                await Task.Delay(ReminderPauseMs, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            TelegramReminderLog.Flush();
        }

        PosLogger.Log($"Телеграм-бот: напоминаний о долге отправлено {sent}; уже писали за 20 ч — {LastRemindersSkippedRecent}; заблокировали бота и отписаны — {blocked}.", "TELEGRAM");
        return sent;
    }

    /// <summary>403 при рассылке: убрать chat_id из карточки клиента на сервере (иначе другие кассы продолжат слать).</summary>
    private void ClearSubscriberOnServer(string clientId)
    {
        if (_debtsApi == null || !Guid.TryParse(clientId, out _))
            return;
        var api = _debtsApi;
        _ = Task.Run(async () =>
        {
            try
            {
                await ApiThrottle.RunBulkAsync(() => api.SetClientTelegramChatIdAsync(clientId, null)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: отписка клиента {clientId} на сервере не сохранена ({ex.GetType().Name}).", "WARNING");
            }
        });
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
