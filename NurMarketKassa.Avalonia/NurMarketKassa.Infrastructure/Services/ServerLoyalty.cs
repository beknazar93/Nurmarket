using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-05, запрос команды NurCRM («для Бекназара»): «баланс в окне оплаты с сервера, выгрузка накопленных баллов,
/// процент начисления в настройку магазина». Бонусы покупателя теперь на сервере NurCRM — одни на все кассы и в приложении
/// NurCRM, где покупатель видит свой баланс:
/// • баланс — <c>bonus_balance</c> клиента (<c>GET api/main/clients/{id}/</c>);
/// • начисление и списание — <c>POST api/main/clients/{id}/bonus/ {delta, reason: earn | redeem | manual, sale, note}</c>
///   (проверено 05.10: чужая причина → 400, списание больше баланса → 400 «Недостаточно бонусов у клиента»);
/// • баллы, накопленные раньше на этом компьютере, — один раз <c>POST api/main/clients/bonus/import/</c>
///   (сервер сам не даст загрузить дважды и не тронет клиента, у которого уже есть движения на сервере);
/// • включены ли баллы и процент начисления — настройка магазина <c>GET/PATCH api/main/app-shop-settings/</c>
///   (points_enabled, points_percent), общая для всех касс и приложения.
/// Локальный баланс (ClientLoyaltyStore) остаётся запасным: без связи касса показывает последний известный, а операции
/// без связи ждут в очереди (loyalty-queue.json) и уходят при следующей связи.
/// </summary>
public static class ServerLoyalty
{
    /// <summary>Клиент API сервера (ставит App при запуске).</summary>
    public static Func<NurMarketApiClient?>? ApiProvider { get; set; }

    private static readonly SemaphoreSlim QueueLock = new(1, 1);
    private static readonly object FileLock = new();

    /// <summary>Баллы включены в настройке магазина на сервере (null — ещё не знаем, тогда как раньше).</summary>
    public static bool? PointsEnabled { get; private set; }

    /// <summary>Процент начисления из настройки магазина (null — ещё не знаем).</summary>
    public static double? PointsPercent { get; private set; }

    /// <summary>Сколько процентов начислять: из настройки магазина на сервере, без связи — последнее известное.</summary>
    public static double EarnPercent => PointsPercent ?? UserPreferences.Instance.LoyaltyEarnPercent;

    /// <summary>Начислять ли баллы: владелец не выключил их в настройке магазина.</summary>
    public static bool EarnEnabled => PointsEnabled ?? true;

    private static NurMarketApiClient? Api => ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    private static string DataDir => Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName);

    private static string QueuePath => Path.Combine(DataDir, "loyalty-queue.json");

    private static string Inv(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    /// <summary>После входа и обновления компании: настройка магазина, разовая выгрузка баллов, очередь.</summary>
    public static async Task AfterCompanyRefreshAsync(string? companyId)
    {
        try
        {
            await RefreshShopSettingsAsync().ConfigureAwait(false);
            if (UserPreferences.Instance.LoyaltyEnabled && !string.IsNullOrWhiteSpace(companyId))
                await ImportLocalBalancesOnceAsync(companyId).ConfigureAwait(false);
            await FlushQueueAsync().ConfigureAwait(false);
            EnsureFlushTimer();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бонусы: синхронизация с сервером не прошла ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Настройка магазина: баллы включены и процент. Процент запоминается для работы без связи.</summary>
    public static async Task RefreshShopSettingsAsync(CancellationToken ct = default)
    {
        if (Api is not { } api)
            return;
        var data = await api.RequestAsync(HttpMethod.Get, "api/main/app-shop-settings/", null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object)
            return;
        if (data.TryGetProperty("points_enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False)
            PointsEnabled = en.ValueKind == JsonValueKind.True;
        var percent = Num(data, "points_percent");
        if (!double.IsNaN(percent))
        {
            PointsPercent = Math.Clamp(percent, 0, 100);
            var prefs = UserPreferences.Instance;
            if (Math.Abs(prefs.LoyaltyEarnPercent - PointsPercent.Value) > 0.001)
            {
                prefs.LoyaltyEarnPercent = PointsPercent.Value;
                prefs.SaveToDisk();
            }
        }
    }

    /// <summary>Процент начисления — в настройку магазина на сервере (её может менять владелец или администратор).</summary>
    public static async Task SavePercentAsync(double percent, CancellationToken ct = default)
    {
        var api = Api ?? throw new InvalidOperationException(Tr.T("нет входа в NurCRM", "NurCRMге кирүү жок", "not signed in to NurCRM", "NurCRM'e giriş yok", "NurCRMga kirilmagan"));
        await api.RequestAsync(new HttpMethod("PATCH"), "api/main/app-shop-settings/",
            new Dictionary<string, object?> { ["points_percent"] = Inv(Math.Clamp(percent, 0, 100)) }, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        PointsPercent = Math.Clamp(percent, 0, 100);
        PosLogger.Log($"Бонусы: процент начисления {PointsPercent:0.##} % сохранён в настройке магазина.", "INFO");
    }

    /// <summary>Баланс клиента на сервере; null — нет связи или входа (тогда показывается локальный).</summary>
    public static async Task<double?> GetBalanceAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || Api is not { } api)
            return null;
        try
        {
            var data = await api.RequestAsync(HttpMethod.Get, $"api/main/clients/{Uri.EscapeDataString(clientId)}/", null, null, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var balance = Num(data, "bonus_balance");
            return double.IsNaN(balance) ? null : balance;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) // 2026-11-03 (С-15): таймаут запроса — не отмена вызывающего
        {
            PosLogger.Log($"Бонусы: баланс клиента не получен с сервера ({ex.Message}) — показан локальный.", "WARNING");
            return null;
        }
    }

    /// <param name="Key">2026-11-01 (проверка бэкенда): ключ повтора (Idempotency-Key) этой операции — стабильный, сохраняется в очереди на диске ДО отправки. Сервер на повтор с тем же ключом отвечает
    /// прежним результатом, баланс не удваивается. Нет ключа (записи старой очереди) — вычисляется из полей операции (<see cref="KeyOf"/>), тоже стабильно.</param>
    /// <param name="Attempts">2026-11-02 (аудит №2, К3-10): сколько раз сервер ОТВЕТИЛ на эту операцию ошибкой, не окончательной (5xx и т. п.) — по <see cref="MaxServerAttempts"/> она
    /// считается не принятой и уходит в «Некорректные чеки». Обрыв связи не считается: без связи операция ждёт сколько нужно.</param>
    private sealed record QueuedOp(string ClientId, double Delta, string Reason, string? SaleId, string? Note, DateTime AtUtc, string? Key = null, int Attempts = 0);

    private const int MaxServerAttempts = 20;

    private enum SendResult { Done, Rejected, ServerError, NoConnection }

    /// <summary>Ключ повтора операции с бонусами. Есть чек: id чека + тип (earn / redeem / manual) + клиент + сумма — одна и та же операция по чеку всегда даёт один ключ
    /// (начисление после продажи, списание на оплату, снятие при возврате/обмене — <see cref="ClientLoyaltyStore.ReverseRemainingForSale"/> — идут этим же путём).
    /// Чека нет (ручная корректировка) — случайный ключ, который живёт в записи очереди до определённого исхода. Хэш — чтобы ключ был короткий и без служебных символов.</summary>
    /// <summary>Ключ операции: записанный в очереди, а у записей старой очереди — стабильный из полей операции.</summary>
    private static string OpKey(QueuedOp op) =>
        op.Key ?? KeyOf(op.ClientId, op.Delta, op.Reason, op.SaleId, op.AtUtc.ToString("O", CultureInfo.InvariantCulture));

    private static string KeyOf(string clientId, double delta, string reason, string? saleId, string? legacyStamp = null)
    {
        var material = !string.IsNullOrWhiteSpace(saleId)
            ? $"{reason}|{saleId.Trim()}|{clientId.Trim()}|{Inv(delta)}"
            : legacyStamp is not null
                ? $"{reason}|{clientId.Trim()}|{Inv(delta)}|{legacyStamp}"
                : Guid.NewGuid().ToString("N");
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return "nurcrm-bonus-" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>Начислить (delta &gt; 0) или списать (delta &lt; 0) баллы на сервере. Без связи — в очередь.</summary>
    public static async Task PostAsync(string clientId, double delta, string reason, string? saleId, string? note)
    {
        if (string.IsNullOrWhiteSpace(clientId) || Math.Abs(delta) < 0.005)
            return;
        var rounded = Math.Round(delta, 2);
        var op = new QueuedOp(clientId, rounded, reason, saleId, note, DateTime.UtcNow, KeyOf(clientId, rounded, reason, saleId));
        // 2026-11-01 (проверка бэкенда): сначала операция с ключом — на диск (очередь), потом отправка. Оборвалась связь или закрыли программу посреди отправки — при следующем сбросе очереди
        // операция уйдёт ТЕМ ЖЕ ключом, и сервер вернёт прежний результат вместо второго начисления/списания. Определённый исход (принято / окончательный отказ) снимает запись.
        Enqueue(op);
        await FlushQueueAsync(waitForLock: true).ConfigureAwait(false);
    }

    /// <summary>true — сервер принял или окончательно отказал (повтор не поможет); false — нет связи, повторить позже.</summary>
    private static async Task<SendResult> TrySendAsync(QueuedOp op)
    {
        if (Api is not { } api)
            return SendResult.NoConnection;
        var key = OpKey(op);
        // 2026-11-05 (сервер ceba67c, idempotency_lookup_bonus_transfers): повторная отправка из очереди (первая уже была давно) — сначала спросить журнал сервера по ключу:
        // done — операция уже проведена, не повторяем; in_progress — сервер ещё выполняет, не повторяем (подождём следующего сброса очереди); failed — повторим тем же ключом.
        if (DateTime.UtcNow - op.AtUtc > TimeSpan.FromSeconds(20))
        {
            switch (await NurMarketKassa.Services.Api.IdempotencyJournal.CheckAsync(api, key).ConfigureAwait(false))
            {
                case NurMarketKassa.Services.Api.IdempotencyVerdict.Done:
                    PosLogger.Log($"Бонусы: операция {op.Reason} {op.Delta:+0.##;-0.##} уже проведена на сервере (журнал ключей) — повтор не нужен.", "INFO");
                    return SendResult.Done;
                case NurMarketKassa.Services.Api.IdempotencyVerdict.InProgress:
                    PosLogger.Log($"Бонусы: операция {op.Reason} {op.Delta:+0.##;-0.##} ещё выполняется на сервере — повтор позже, не дублируем.", "INFO");
                    return SendResult.NoConnection; // не считаем попыткой сервера: очередь вернётся к ней при следующем сбросе
            }
        }
        var body = new Dictionary<string, object?> { ["delta"] = Inv(op.Delta), ["reason"] = op.Reason, ["idempotency_key"] = key };
        if (!string.IsNullOrWhiteSpace(op.SaleId))
            body["sale"] = op.SaleId;
        if (!string.IsNullOrWhiteSpace(op.Note))
            body["note"] = op.Note;
        try
        {
            var data = await api.PostWithIdempotencyKeyAsync($"api/main/clients/{Uri.EscapeDataString(op.ClientId)}/bonus/", body, key, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var balance = Num(data, "bonus_balance");
            PosLogger.Log($"Бонусы на сервере: {op.Reason} {op.Delta:+0.##;-0.##}, баланс клиента {(double.IsNaN(balance) ? "?" : balance.ToString("0.##", CultureInfo.InvariantCulture))}.", "INFO");
            return SendResult.Done;
        }
        catch (ApiException ex) when (ex.StatusCode is >= 400 and < 500 && ex.StatusCode != 401 && ex.StatusCode != 408 && ex.StatusCode != 429)
        {
            // «Недостаточно бонусов» (списали на другой кассе) и т. п. — повтор не поможет.
            PosLogger.Log($"Бонусы: сервер не принял {op.Reason} {op.Delta:+0.##;-0.##} ({ex.StatusCode}: {ex.Message}).", "WARNING");
            NotifyRejected(op, ex.Message);
            return SendResult.Rejected;
        }
        catch (ApiException ex)
        {
            // Сервер ответил, но не по существу (5xx, 401, 408, 429) — повторим, но не бесконечно (MaxServerAttempts).
            PosLogger.Log($"Бонусы: сервер ответил ошибкой ({ex.StatusCode}: {ex.Message}) — операция {op.Reason} {op.Delta:+0.##;-0.##} в очереди.", "WARNING");
            return SendResult.ServerError;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бонусы: нет связи с сервером ({ex.Message}) — операция {op.Reason} {op.Delta:+0.##;-0.##} в очереди.", "WARNING");
            return SendResult.NoConnection;
        }
    }

    /// <summary>2026-11-02 (аудит №2, К3-10): окончательный отказ сервера по баллам не должен проходить молча — скидка бонусами уже выдана покупателю, а баллы на сервере не списаны (или
    /// начисление потеряно). Запись в «Некорректные чеки» (владелец видит), WARNING в журнале.</summary>
    private static void NotifyRejected(QueuedOp op, string? serverText)
    {
        try
        {
            var what = op.Delta < 0
                ? Tr.T("списание", "алуу", "redemption", "harcama", "yechish")
                : Tr.T("начисление", "эсептөө", "accrual", "kazanım", "hisoblash");
            var amount = Math.Abs(op.Delta).ToString("0.##", CultureInfo.InvariantCulture);
            var sale = string.IsNullOrWhiteSpace(op.SaleId) ? "" : Tr.T($", чек {op.SaleId}", $", чек {op.SaleId}", $", receipt {op.SaleId}", $", fiş {op.SaleId}", $", chek {op.SaleId}");
            IrregularReceiptsStore.Append(new IrregularReceiptEntry
            {
                Tag = IrregularReceiptEntry.BonusRejected,
                CashierName = PosApp.CurrentUserDisplayName,
                Note = Tr.T(
                    $"Сервер не принял баллы: {what} {amount}{sale}. Причина: {serverText}. Скидка бонусами выдана, баллы не списаны/не начислены — проверьте баланс клиента.",
                    $"Сервер баллдарды кабыл алган жок: {what} {amount}{sale}. Себеби: {serverText}. Бонус менен арзандатуу берилди, баллдар алынган/эсептелген жок — кардардын балансын текшериңиз.",
                    $"The server did not accept the points: {what} {amount}{sale}. Reason: {serverText}. The bonus discount was given, but the points were not deducted/added — check the client's balance.",
                    $"Sunucu puanları kabul etmedi: {what} {amount}{sale}. Neden: {serverText}. Bonus indirimi verildi, puanlar düşülmedi/eklenmedi — müşterinin bakiyesini kontrol edin.",
                    $"Server ballarni qabul qilmadi: {what} {amount}{sale}. Sabab: {serverText}. Bonus chegirmasi berildi, ballar yechilmadi/hisoblanmadi — mijoz balansini tekshiring."),
                Total = Math.Abs(op.Delta),
                CartJson = "{}",
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бонусы: отказ сервера не записан в «Некорректные чеки»: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-11-02 (К3-10): учесть ответ сервера ошибкой — счётчик попыток операции в очереди +1 (под замком файла, по ключу). true — предел исчерпан, операция снята.</summary>
    private static bool CountServerAttempt(QueuedOp op)
    {
        try
        {
            lock (FileLock)
            {
                var queue = ReadQueue();
                var key = OpKey(op);
                var index = queue.FindIndex(x => OpKey(x) == key);
                if (index < 0)
                    return false;
                var updated = queue[index] with { Key = OpKey(queue[index]), Attempts = queue[index].Attempts + 1 };
                if (updated.Attempts >= MaxServerAttempts)
                {
                    queue.RemoveAt(index);
                    WriteQueue(queue);
                    PosLogger.Log($"Бонусы: сервер {MaxServerAttempts} раз ответил ошибкой — операция {op.Reason} {op.Delta:+0.##;-0.##} снята с очереди как не принятая.", "WARNING");
                    NotifyRejected(op, Tr.T("сервер не отвечает по существу", "сервер маңызы боюнча жооп бербейт", "the server keeps failing", "sunucu sürekli hata veriyor", "server doimiy xato qaytaryapti"));
                    return true;
                }
                queue[index] = updated;
                WriteQueue(queue);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Бонусы: счётчик попыток не обновлён (файл занят: {ex.Message}).", "WARNING");
        }
        return false;
    }

    // 2026-11-02 (К3-10): раньше очередь досылалась только при следующей операции с баллами или при входе — таймера не было. Теперь раз в 90 секунд (пока есть вход).
    private static Timer? _flushTimer;

    private static void EnsureFlushTimer()
    {
        if (_flushTimer is not null)
            return;
        lock (FileLock)
        {
            _flushTimer ??= new Timer(_ =>
            {
                try { _ = FlushQueueAsync(); }
                catch (Exception ex) { PosLogger.Log($"Бонусы: таймер очереди: {ex.Message}", "WARNING"); }
            }, null, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(90));
        }
    }

    /// <summary>2026-10-30 (приёмка 1.17.65, блокер 3): операции, не записанные на диск из-за занятого файла очереди (антивирус). Держим в памяти
    /// и дописываем при следующей успешной записи: «файл занят» больше не превращается в «пустую очередь» и не затирает бонусы.</summary>
    private static readonly List<QueuedOp> Unsaved = new();

    /// <summary>Читает очередь. «Файл занят» (IOException после повторов 3×50 мс) пробрасывается — вызывающий НЕ пишет поверх.</summary>
    private static List<QueuedOp> ReadQueue()
    {
        lock (FileLock)
        {
            List<QueuedOp> list;
            try
            {
                // 2026-10-29 (аварийный аудит, п. 7): битый файл — из копии .bak (AtomicFile), а не «пустая очередь бонусов».
                var text = AtomicFile.ReadAllText(QueuePath);
                list = text is not null ? JsonSerializer.Deserialize<List<QueuedOp>>(text) ?? new() : new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw;
            }
            catch
            {
                list = new();
            }

            foreach (var u in Unsaved)
            {
                if (!list.Contains(u))
                    list.Add(u);
            }

            return list;
        }
    }

    private static void WriteQueue(List<QueuedOp> queue)
    {
        lock (FileLock)
        {
            Directory.CreateDirectory(DataDir);
            if (queue.Count == 0)
            {
                if (File.Exists(QueuePath))
                    File.Delete(QueuePath);
                if (File.Exists(QueuePath + ".bak"))
                    File.Delete(QueuePath + ".bak"); // 2026-10-29: пустая очередь — устаревшую копию не оставляем
                Unsaved.Clear();
                return;
            }
            AtomicFile.WriteAllText(QueuePath, JsonSerializer.Serialize(queue)); // 2026-10-29: атомарно, с копией .bak
            Unsaved.Clear();
        }
    }

    private static void Enqueue(QueuedOp op)
    {
        try
        {
            // 2026-11-01 (финальная приёмка 1.17.73, Ф-1): прочитать актуальный файл → добавить → записать — одним куском под замком файла (раньше чтение и запись были двумя
            // отдельными захватами, и параллельная запись другой операции терялась).
            lock (FileLock)
            {
                var queue = ReadQueue();
                queue.Add(op);
                WriteQueue(queue);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 2026-10-30: файл очереди занят — операцию держим в памяти, файл не трогаем.
            lock (FileLock)
                Unsaved.Add(op);
            PosLogger.Log($"Бонусы: файл очереди занят ({ex.Message}) — операция {op.Reason} {op.Delta:+0.##;-0.##} ждёт в памяти, файл не затираем.", "WARNING");
        }
    }

    /// <summary>2026-11-01 (Ф-1): убирает из очереди ровно отправленную операцию (по ключу): читает АКТУАЛЬНЫЙ файл под замком файла, удаляет её, записывает. Не удалось (файл занят) —
    /// запись остаётся и уйдёт ещё раз тем же ключом: сервер ответит прежним результатом, баланс не удвоится.</summary>
    private static void RemoveSent(QueuedOp op)
    {
        try
        {
            lock (FileLock)
            {
                var queue = ReadQueue();
                var key = OpKey(op);
                queue.RemoveAll(x => OpKey(x) == key);
                WriteQueue(queue);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Бонусы: отправленная операция {op.Reason} {op.Delta:+0.##;-0.##} не снята с очереди (файл занят: {ex.Message}) — уйдёт ещё раз тем же ключом.", "WARNING");
        }
    }

    /// <summary>Отправить операции, ждавшие связи (по порядку; первая неудача — остальные ждут дальше).</summary>
    /// <param name="waitForLock">2026-11-01: true — ждать, пока другая отправка очереди закончится (до 8 с): операция уже записана на диск, а не потерялась бы при занятом замке.</param>
    public static async Task FlushQueueAsync(bool waitForLock = false)
    {
        if (!await QueueLock.WaitAsync(waitForLock ? TimeSpan.FromSeconds(8) : TimeSpan.Zero).ConfigureAwait(false))
            return;
        try
        {
            // 2026-11-01 (финальная приёмка 1.17.73, Ф-1, БЛОКЕР): раньше очередь читалась один раз в начале, пока шла отправка (до 20 с на операцию) в неё дописывали операции
            // других продаж, а в конце на диск писался СТАРЫЙ снимок — чужая операция (списание баллов!) стиралась. Теперь: отправка идёт БЕЗ замка файла, после каждой принятой операции под
            // замком файла читается актуальный файл, убирается ровно эта операция (по ключу) и файл записывается — добавленное за это время не затрагивается. Операции, дописанные во время
            // сброса, этот же сброс подхватывает следующим кругом.
            var attempted = new HashSet<string>(StringComparer.Ordinal);
            var sent = 0;
            while (true)
            {
                QueuedOp? next;
                try { next = ReadQueue().FirstOrDefault(x => !attempted.Contains(OpKey(x))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PosLogger.Log($"Бонусы: файл очереди занят ({ex.Message}) — отправка отложена, файл не затираем.", "WARNING"); // 2026-10-30
                    break;
                }
                if (next is null)
                    break;
                attempted.Add(OpKey(next));
                var result = await TrySendAsync(next).ConfigureAwait(false);
                if (result == SendResult.ServerError)
                {
                    CountServerAttempt(next);
                    break;
                }
                if (result == SendResult.NoConnection)
                    break; // первая неудача — остальные ждут дальше
                RemoveSent(next); // принято или окончательный отказ (уведомление уже записано)
                sent++;
            }
            if (sent > 0)
            {
                int left;
                try { left = ReadQueue().Count; } catch { left = -1; }
                PosLogger.Log($"Бонусы: из очереди отправлено {sent}, осталось {left}.", "INFO");
            }
        }
        finally
        {
            QueueLock.Release();
        }
    }

    /// <summary>Разово: баллы, накопленные на этом компьютере, — на сервер. Отметка — файл на компанию.</summary>
    public static async Task ImportLocalBalancesOnceAsync(string companyId)
    {
        var flag = Path.Combine(DataDir, $"loyalty-imported-{companyId}.flag");
        if (File.Exists(flag) || Api is not { } api)
            return;
        var balances = DatabaseService.Instance.GetAllClientLoyaltyBalances().Where(b => b.Balance >= 0.01).ToList();
        if (balances.Count > 0)
        {
            var items = balances.Select(b => new Dictionary<string, object?>
            {
                ["client_id"] = b.ClientId,
                ["balance"] = Inv(b.Balance),
                ["note"] = "NurCRM: баллы, накопленные на кассе до " + DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            }).ToList();
            var data = await api.RequestAsync(HttpMethod.Post, "api/main/clients/bonus/import/", new Dictionary<string, object?> { ["items"] = items }, null,
                CancellationToken.None, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            var text = data.ValueKind == JsonValueKind.Undefined ? "" : data.GetRawText();
            PosLogger.Log($"Бонусы: выгружены на сервер балансы {balances.Count} клиент(ов), сумма {balances.Sum(b => b.Balance):0.##}. Ответ: {(text.Length > 300 ? text[..300] : text)}", "INFO");
        }
        else
        {
            PosLogger.Log("Бонусы: на этом компьютере накопленных баллов нет — выгружать нечего.", "INFO");
        }
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(flag, DateTime.UtcNow.ToString("O"));
    }
}
