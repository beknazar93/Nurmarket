using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-21: очередь сообщений бота (бот, К4 и ВАЖНО 1–2 отчёта 20.10).
///
/// БЫЛО: все сообщения обрабатывались строго по одному: каждое ждало ответа ИИ или NurCRM по предыдущему. При настоящей скорости ИИ —
/// 0,5 сообщ./с и p95 205 с (S1b); один «висящий» ответ (ИИ до 45 с, /dolgi до 110 с) или 429 одного чата останавливал бота для всех,
/// включая владельца. После перезапуска кассы первая пачка обрабатывалась заново (S3a: 24 дубля), сбой в первой пачке давал повторы
/// каждые 30 с, сбой в обычной — молча терял сообщение и ставил паузу 30 с для всех.
///
/// СТАЛО:
/// • у каждого чата своя очередь (порядок внутри чата сохраняется), чаты обрабатываются параллельно — до 100 одновременно, у чата
///   владельца — своя полоса вне общей очереди; вопросы ИИ от покупателей — не больше 8 одновременно (остальные ждут до 20 с,
///   затем отвечает поиск по каталогу);
/// • каждое принятое обновление (update_id) записывается на диск (%APPDATA%\NurMarketKassa\telegram-updates-&lt;бот&gt;.json) ДО того,
///   как Telegram получит подтверждение: после перезапуска бот продолжает с того же места, необработанные доделывает, обработанные
///   не повторяет; сообщение, дважды уронившее обработку при запуске, пропускается (не крутится бесконечно);
/// • сбой обработки одного сообщения — повтор через 1,5 с, затем короткий ответ «повторите через минуту»; остальные чаты не ждут.
/// </summary>
public sealed partial class TelegramBotPollingService
{
    private static int _activePollers;

    /// <summary>Сколько опросов бота работает в этой программе (для подтверждения владельца кодом).</summary>
    public static int ActivePollers => Volatile.Read(ref _activePollers);

    // Работник чата почти всё время ждёт (паузу Telegram ~1/с в чат, ответ ИИ) — 16 работников давали всего ~16 ответов/с на всех.
    // Тяжёлое ограничено отдельно: ИИ покупателей — 3 одновременно, должники — один запрос на всех.
    private const int MaxParallelChats = 100;
    // Вопросы ИИ от покупателей одновременно: 3 давали очередь при 5 сообщ./с и ответе ИИ 2 с (S1b p95 49 с); 8 — с запасом,
    // а предел бесплатного ключа Google всё равно срежет лишнее (тогда отвечает поиск по каталогу).
    private const int MaxCustomerAi = 8;
    private static readonly TimeSpan CustomerAiWait = TimeSpan.FromSeconds(20);
    private static readonly SemaphoreSlim CustomerAiSlots = new(MaxCustomerAi, MaxCustomerAi);
    private readonly SemaphoreSlim _chatSlots = new(MaxParallelChats, MaxParallelChats);
    private readonly object _queueGate = new();
    private readonly Dictionary<string, Queue<(long Id, JsonElement Update)>> _chatQueues = new(StringComparer.Ordinal);
    private readonly List<Task> _workers = new();
    private UpdateInbox? _inbox;
    private static string? _commandsPublishedFor;

    /// <summary>Ключ очереди: чат сообщения или кнопки.</summary>
    private static string ChatKey(JsonElement update)
    {
        if (update.TryGetProperty("message", out var m) && m.TryGetProperty("chat", out var c) && c.TryGetProperty("id", out var id))
            return id.ToString();
        // 2026-10-24 (Г1): бота добавили в группу / удалили из неё — в очередь этой группы (порядок с её сообщениями сохраняется).
        if (update.TryGetProperty("my_chat_member", out var mcm) && mcm.TryGetProperty("chat", out var mcc) && mcc.TryGetProperty("id", out var mcid))
            return mcid.ToString();
        if (update.TryGetProperty("callback_query", out var cb))
        {
            if (cb.TryGetProperty("message", out var cm) && cm.TryGetProperty("chat", out var cc) && cc.TryGetProperty("id", out var cid))
                return cid.ToString();
            if (cb.TryGetProperty("from", out var f) && f.TryGetProperty("id", out var fid))
                return fid.ToString();
        }
        return "_";
    }

    /// <summary>Поставить обновление в очередь его чата; у чата нет работника — запустить.</summary>
    private void Dispatch(long id, JsonElement update, CancellationToken ct)
    {
        var key = ChatKey(update);
        lock (_queueGate)
        {
            if (_chatQueues.TryGetValue(key, out var queue))
            {
                queue.Enqueue((id, update));
                return;
            }
            queue = new Queue<(long, JsonElement)>();
            queue.Enqueue((id, update));
            _chatQueues[key] = queue;
            var ownerLane = string.Equals(key, UserPreferences.Instance.TelegramChatId, StringComparison.Ordinal);
            _workers.RemoveAll(t => t.IsCompleted);
            _workers.Add(Task.Run(() => DrainChatAsync(key, queue, ownerLane, ct), CancellationToken.None));
        }
    }

    private async Task DrainChatAsync(string key, Queue<(long Id, JsonElement Update)> queue, bool ownerLane, CancellationToken ct)
    {
        if (!ownerLane)
        {
            try
            {
                await _chatSlots.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // остановка: очередь осталась в файле — доделает следующий запуск
            }
        }

        try
        {
            while (true)
            {
                (long Id, JsonElement Update) item;
                var skipped = new List<long>();
                lock (_queueGate)
                {
                    if (ct.IsCancellationRequested || !queue.TryDequeue(out item))
                    {
                        _chatQueues.Remove(key);
                        return;
                    }
                    // 2026-10-21 (бот, S2): покупатель шлёт сообщения быстрее, чем Telegram разрешает отвечать (1/с в чат), —
                    // отвечаем на последние, а не на каждое через минуты (раньше один такой чат держал весь бот).
                    if (queue.Count >= (ownerLane ? FloodBacklogOwner : FloodBacklog))
                    {
                        skipped.Add(item.Id);
                        while (queue.Count > 3)
                            skipped.Add(queue.Dequeue().Id);
                        item = queue.Dequeue();
                    }
                }
                if (skipped.Count > 0)
                {
                    foreach (var id in skipped)
                        _inbox?.Done(id);
                    PosLogger.Log($"Телеграм-бот: чат прислал много сообщений подряд — пропущено {skipped.Count}, отвечаю на последние.", "TELEGRAM");
                    await NoteFloodAsync(key, item.Update, ct).ConfigureAwait(false);
                }
                await ProcessOneAsync(item.Id, item.Update, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!ownerLane)
                _chatSlots.Release();
        }
    }

    /// <summary>Сколько непрочитанных сообщений одного покупателя считается наплывом (отвечаем на последние 3).</summary>
    private const int FloodBacklog = 8;
    private const int FloodBacklogOwner = 20;
    private readonly Dictionary<string, DateTime> _floodNoticeAt = new(StringComparer.Ordinal);

    /// <summary>Один раз в 10 минут — короткое объяснение, почему ответ не на каждое сообщение.</summary>
    private async Task NoteFloodAsync(string chatId, JsonElement update, CancellationToken ct)
    {
        // 2026-10-24 (Г1): в группе заказов бот молчит и про наплыв сообщений.
        if (TelegramBotOwner.IsOrdersGroupOnly(chatId))
            return;
        lock (_queueGate)
        {
            if (_floodNoticeAt.TryGetValue(chatId, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10))
                return;
            _floodNoticeAt[chatId] = DateTime.UtcNow;
        }
        var code = update.TryGetProperty("message", out var m) && m.TryGetProperty("from", out var f) && f.TryGetProperty("language_code", out var lc) ? lc.GetString() : null;
        if (TelegramBotText.FromCode(code) is { } lang)
            TelegramBotText.Use(lang);
        await TelegramBotService.SendToAsync(chatId, TelegramBotText.T("Вы прислали много сообщений подряд — отвечаю на последние 🙂",
            "Сиз катары менен көп билдирүү жөнөттүңүз — акыркыларына жооп берем 🙂", "You sent many messages in a row — I'm answering the latest ones 🙂",
            "Arka arkaya çok mesaj gönderdiniz — son mesajlara yanıt veriyorum 🙂", "Ketma-ket ko'p xabar yubordingiz — oxirgilariga javob beraman 🙂"), ct).ConfigureAwait(false);
    }

    private async Task ProcessOneAsync(long id, JsonElement update, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await HandleUpdateAsync(update, ct).ConfigureAwait(false);
                // Программу закрыли, пока ответ ждал паузы Telegram (429) или сервера, — ответ не ушёл: сообщение остаётся в файле,
                // следующий запуск ответит на него (а не потеряет).
                if (ct.IsCancellationRequested)
                    return;
                _inbox?.Done(id);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // остаётся в файле, следующий запуск ответит
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: сбой обработки сообщения ({ex.GetType().Name}: {ex.Message})" + (attempt == 1 ? " — повторяю через 1,5 с." : " — отвечаю «повторите».") , "WARNING");
                if (attempt == 1)
                {
                    try
                    {
                        await Task.Delay(1500, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        // Два сбоя подряд: человеку — короткий ответ вместо тишины, сообщение закрыто (не повторяется по кругу).
        try
        {
            if (update.TryGetProperty("message", out var m) && m.TryGetProperty("chat", out var c) && c.TryGetProperty("id", out var chat))
            {
                // 2026-10-24 (Г1): группе заказов бот не отвечает ничем — даже извинением.
                if (TelegramBotOwner.IsOrdersGroupOnly(chat.ToString()))
                    return;
                var lang = m.TryGetProperty("from", out var from) && from.TryGetProperty("language_code", out var lc) ? TelegramBotText.FromCode(lc.GetString()) : null;
                if (lang is { } l && !string.Equals(chat.ToString(), UserPreferences.Instance.TelegramChatId, StringComparison.Ordinal))
                    TelegramBotText.Use(l);
                await TelegramBotService.SendToAsync(chat.ToString(), TelegramBotText.T(
                    "Не получилось ответить 🙏 Повторите, пожалуйста, через минуту.", "Жооп бере алган жокмун 🙏 Бир мүнөттөн кийин кайталаңызчы.",
                    "Sorry, I couldn't answer 🙏 Please try again in a minute.", "Yanıt veremedim 🙏 Lütfen bir dakika sonra tekrar deneyin.",
                    "Javob bera olmadim 🙏 Iltimos, bir daqiqadan keyin qayta urinib ko'ring."), ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Не критично.
        }
        _inbox?.Done(id);
    }

    /// <summary>Записывает очередь на диск не чаще раза в 300 мс (и при остановке).</summary>
    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _inbox?.Flush();
            TelegramInquiryStore.Flush();
        }
    }

    /// <summary>Дождаться работников при остановке (не дольше 5 с) и записать очередь.</summary>
    private async Task StopWorkersAsync()
    {
        Task[] workers;
        lock (_queueGate)
            workers = _workers.ToArray();
        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Остановка не должна зависнуть из-за одного долгого ответа.
        }
        _inbox?.Flush();
        TelegramInquiryStore.Flush();
        TelegramReminderLog.Flush();
    }

    /// <summary>2026-10-21 (бот, идея 3): команды в меню Telegram на 5 языках — покупателям и отдельно чату владельца.
    /// Один раз на токен и чат владельца за запуск программы.</summary>
    private static async Task PublishCommandsAsync(CancellationToken ct)
    {
        var prefs = UserPreferences.Instance;
        var key = TelegramBotOwner.BotId(prefs.TelegramBotToken) + "|" + prefs.TelegramChatId;
        if (Interlocked.Exchange(ref _commandsPublishedFor, key) == key)
            return;
        try
        {
            foreach (var (code, commands) in TelegramBotText.CommandSets(owner: false))
                await TelegramBotService.SetMyCommandsAsync(commands, null, code, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(prefs.TelegramChatId))
            {
                var scope = new Dictionary<string, object?> { ["type"] = "chat", ["chat_id"] = prefs.TelegramChatId };
                foreach (var (code, commands) in TelegramBotText.CommandSets(owner: true))
                    await TelegramBotService.SetMyCommandsAsync(commands, scope, code, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _commandsPublishedFor = null;
        }
    }

    /// <summary>
    /// Принятые, но ещё не обработанные обновления и следующий offset — на диске. Файл маленький (обычно пустой список), пишется
    /// целиком через временный файл (сбой при записи не портит старый).
    /// </summary>
    private sealed class UpdateInbox
    {
        private sealed record FileItem(long Id, int Attempts, string Update);

        private sealed record FileModel(long NextOffset, List<FileItem> Pending);

        private readonly string _path;
        private readonly object _gate = new();
        private readonly object _fileGate = new();
        private readonly SortedDictionary<long, (string Json, int Attempts)> _pending = new();
        private List<(long Id, string Json, int Attempts)> _recovered = new();
        private bool _dirty;

        public long NextOffset { get; private set; }

        public int PendingCount
        {
            get
            {
                lock (_gate)
                    return _pending.Count;
            }
        }

        private UpdateInbox(string path) => _path = path;

        public static UpdateInbox Open(string botId)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa",
                $"telegram-updates-{string.Concat(botId.Where(char.IsLetterOrDigit))}.json");
            var inbox = new UpdateInbox(path);
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path)) is { } model)
                {
                    inbox.NextOffset = model.NextOffset;
                    foreach (var item in model.Pending ?? new List<FileItem>())
                    {
                        inbox._pending[item.Id] = (item.Update, item.Attempts + 1);
                        inbox._recovered.Add((item.Id, item.Update, item.Attempts + 1));
                    }
                    inbox._dirty = inbox._recovered.Count > 0;
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: файл очереди не прочитан ({ex.GetType().Name}) — начинаю с новых сообщений.", "WARNING");
            }
            return inbox;
        }

        /// <summary>Необработанное с прошлого запуска (попытка уже +1).</summary>
        public List<(long Id, JsonElement Update, int Attempts)> TakeRecovered()
        {
            var list = new List<(long, JsonElement, int)>();
            lock (_gate)
            {
                foreach (var (id, json, attempts) in _recovered)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        list.Add((id, doc.RootElement.Clone(), attempts));
                    }
                    catch (JsonException)
                    {
                        _pending.Remove(id);
                    }
                }
                _recovered = new();
            }
            return list;
        }

        /// <summary>true — новое обновление, его надо обработать; false — уже было (обработано или в очереди) или устарело.</summary>
        public bool Accept(long id, JsonElement update, bool stale)
        {
            lock (_gate)
            {
                if (_pending.ContainsKey(id) || id < NextOffset)
                    return false;
                NextOffset = id + 1;
                _dirty = true;
                if (stale)
                    return false;
                _pending[id] = (update.GetRawText(), 0);
                return true;
            }
        }

        public void Done(long id)
        {
            lock (_gate)
            {
                if (_pending.Remove(id))
                    _dirty = true;
            }
        }

        public void Flush()
        {
            // Снимок и запись — под одним замком файла: две записи подряд не перепутают старый и новый снимок.
            lock (_fileGate)
            {
                FileModel model;
                lock (_gate)
                {
                    if (!_dirty)
                        return;
                    model = new FileModel(NextOffset, _pending.Select(p => new FileItem(p.Key, p.Value.Attempts, p.Value.Json)).ToList());
                    _dirty = false;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var tmp = _path + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(model));
                    File.Move(tmp, _path, true);
                }
                catch (Exception ex)
                {
                    lock (_gate)
                        _dirty = true;
                    PosLogger.Log($"Телеграм-бот: файл очереди не записан ({ex.GetType().Name}).", "WARNING");
                }
            }
        }
    }
}
