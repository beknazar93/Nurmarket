using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Окружение наблюдателя. По умолчанию — настоящая касса; стенд подставляет свои функции (сервер, принтер, часы, смена).</summary>
public sealed class PhonePrintEnv
{
    public Func<string?> CurrentShiftId { get; init; } = () => ReceiptHistoryService.CurrentShiftId();
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    public Func<bool> IsOutage { get; init; } = () => ServerOutageMonitor.IsOutage;
    /// <summary>Сколько ещё сервер просит не слать запросы (Retry-After после 429).</summary>
    public Func<TimeSpan> ThrottleLeft { get; init; } = () => ApiThrottle.RemainingBlock;
    /// <summary>Первая страница продаж компании с даты (page_size, date_from).
    /// 2026-10-27, ревью К-П1 (С-1): один точный запрос — новые сверху (ordering=-created_at), без запасных вариантов запроса и без
    /// повторов на 429 внутри (PosSalesListAsync делал до 3 повторов и перебирал адреса): паузу после 429 держит сам наблюдатель.</summary>
    public Func<int, DateTime, CancellationToken, Task<List<JsonElement>>> ListSales { get; init; } = async (size, from, ct) =>
    {
        var api = SettingsCloudSync.ApiProvider?.Invoke() ?? throw new ApiException("нет сессии", 401);
        var query = new Dictionary<string, string>
        {
            ["page"] = "1",
            ["page_size"] = size.ToString(CultureInfo.InvariantCulture),
            ["ordering"] = "-created_at",
            ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var data = await api.RequestAsync(HttpMethod.Get, "api/main/pos/sales/", null, query, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return NurMarketApiClient.UnwrapList(data);
    };
    public Func<string, CancellationToken, Task<JsonElement>> GetSale { get; init; } =
        (id, ct) => App.SalesApi.PosSaleGetAsync(id, ct);
    /// <summary>Печать готового текста: null — напечатано, иначе причина отказа (как ReceiptHistoryService.PrintAsync).</summary>
    public Func<string, Task<string?>> Print { get; init; } = text => ReceiptHistoryService.PrintAsync(text);
    /// <summary>2026-10-27, ревью К-П1 (В-1): печать отклонена ДО отправки на принтер (выключена или принтер не выбран) —
    /// только тогда отказ значит «точно не напечатано». «Принтер не ответил» у LPT приходит через 8 с, а задание ждёт порт до 15 с
    /// и может выйти на бумагу потом, поэтому любой другой отказ — «исход неизвестен» (повтор пойдёт с «ПОВТОРНАЯ ПЕЧАТЬ»).</summary>
    public Func<bool> RefusedBeforeSend { get; init; } = () =>
        !UserPreferences.Instance.ReceiptEnabled || HardwareModeHelper.IsNonePort(UserPreferences.Instance.ReceiptDevicePath);
    /// <summary>2026-10-27, ревью К-П1 (В-3): касса (cashbox), на которой этот ПК работает сейчас. Печатаем только по ней.</summary>
    public Func<string?> CurrentCashboxId { get; init; } = () => NurMarketKassa.PosApp.PosCashboxId;
    /// <summary>Свои продажи по журналу обмена LAN (если включён): их id не считаются «чужими». null — LAN выключен.</summary>
    public Func<HashSet<string>?> OwnSaleIds { get; init; } = new LanOwnSales().Read;

    /// <summary>Читает свои продажи из журнала LAN инкрементально (курсор по номеру события).</summary>
    private sealed class LanOwnSales
    {
        private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
        private long _seq;

        public HashSet<string>? Read()
        {
            try
            {
                if (!UserPreferences.Instance.LanSyncEnabled)
                    return null;
                var (events, last) = LanJournal.ReadOwnAfter(_seq, 500);
                // 2026-10-27, ревью К-П1 (В-3): продажи соседних касс по LAN — тоже не «телефон» (иначе чек соседа печатался бы вторым).
                var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                foreach (var e in events)
                {
                    _seq = Math.Max(_seq, e.Seq);
                    string? id = null;
                    if (e.Kind == LanJournal.KindSale)
                        id = JsonSerializer.Deserialize<LanSalePayload>(e.Json, web)?.ServerSaleId;
                    else if (e.Kind == LanJournal.KindUploaded)
                        id = JsonSerializer.Deserialize<LanUploadedPayload>(e.Json, web)?.ServerSaleId;
                    if (!string.IsNullOrWhiteSpace(id))
                        _ids.Add(id.Trim());
                }
                foreach (var peer in LanJournal.ReadPeerSales(DateTime.UtcNow.AddHours(-12)))
                    if (!string.IsNullOrWhiteSpace(peer.Sale.ServerSaleId))
                        _ids.Add(peer.Sale.ServerSaleId!.Trim());
                return _ids;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Чеки с телефона: журнал LAN не прочитан ({ex.Message}).", "DEBUG");
                return _ids.Count > 0 ? _ids : null;
            }
        }
    }
}

/// <summary>2026-10-27, пакет К-П1 (ТЗ «Мобилка и касса», путь A, правила отбора 3A п. 1–6): наблюдатель за сервером.
/// Раз в 5 секунд читает первую страницу продаж компании за сегодня (существующий SalesApiService.PosSalesListAsync) и
/// печатает на этой кассе чеки телефона. Метки «продано с телефона» у продажи на сервере нет, поэтому «с телефона» =
/// «чужая смена на назначенной этой кассе»:
///  1) статус paid / debt / partially_returned;
///  2) касса продажи назначена этому ПК (PhonePrintSettings: включено здесь + ключ PhonePrintRouting на сервере);
///  3) смена продажи — не текущая смена этой кассы и не смена, которую эта касса уже видела своей; id нет среди своих
///     продаж из журнала LAN;
///  4) продажа не старше 3 минут и появилась на сервере уже после включения наблюдения («нулевой» опрос после включения
///     запоминает всё бывшее как «ignored»); старше 3 минут — только в список «Не напечатаны»;
///  5) сотрудник (user_display) — из необязательного списка владельца, пусто = все;
///  6) id нет в журнале напечатанного (PhoneSalePrintJournal).
/// Не печатает при отсутствии связи, в аварии сервера и пока сервер просит паузу (429, Retry-After). Ошибки сети — молча.
/// Чек строит существующий SaleReceiptTextBuilder (ширина, шапка и кодировка — настройки кассы) + строка «Продажа с телефона».
/// Денежный ящик не открывается (в печать уходит только готовый текст, способ оплаты не передаётся).</summary>
public sealed class PhoneSalePrintWatcher
{
    // 2026-10-27, ревью К-П1 (С-4): 8 с ± 1 с вместо ровных 5 с (кассы не бьют в сервер синхронно; задержка печати остаётся ≤ 10 с), страница 10 строк.
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PrintTimeout = TimeSpan.FromSeconds(40);
    private const int PageSize = 10;

    private readonly PhoneSalePrintJournal _journal;
    private readonly PhonePrintSettings _settings;
    private readonly PhonePrintEnv _env;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private DateTimeOffset _pauseUntil = DateTimeOffset.MinValue;
    private int _errorStreak;
    private int _throttleStreak;
    private DateTime _lastErrorLogUtc = DateTime.MinValue;

    private static PhoneSalePrintWatcher? _current;
    /// <summary>Наблюдатель этой кассы (после <see cref="StartDefault"/>).</summary>
    public static PhoneSalePrintWatcher? Current => _current;

    public PhoneSalePrintWatcher(PhoneSalePrintJournal journal, PhonePrintSettings settings, PhonePrintEnv env)
    {
        _journal = journal;
        _settings = settings;
        _env = env;
    }

    public PhoneSalePrintJournal Journal => _journal;
    public PhonePrintSettings Settings => _settings;

    /// <summary>Состоялась ли печать/появилась запись — окна обновляют список. Вызывается из фонового потока.</summary>
    public event Action? Changed;

    /// <summary>Последнее состояние связи одной строкой (для меню и журнала).</summary>
    public string LastNote { get; private set; } = "";
    public DateTimeOffset? LastPollOk { get; private set; }

    /// <summary>Запуск на этой кассе: один раз за сеанс (повторный вызов ничего не делает). Не в программе владельца.</summary>
    public static PhoneSalePrintWatcher StartDefault()
    {
        if (_current != null)
            return _current;
        var w = new PhoneSalePrintWatcher(new PhoneSalePrintJournal(PhoneSalePrintJournal.DefaultPath), PhonePrintSettings.Default, new PhonePrintEnv());
        if (Interlocked.CompareExchange(ref _current, w, null) != null)
            return _current!;
        w.Start();
        return w;
    }

    public void Start()
    {
        if (_cts != null)
            return;
        // Запуск кассы: «printing» с прошлого раза — исход неизвестен, автоматически не перепечатываем (в список «Не напечатаны»).
        var interrupted = _journal.RecoverInterrupted();
        if (interrupted > 0)
            PosLogger.Log($"Чеки с телефона: после перезапуска {interrupted} чек(ов) в состоянии «печатается» — исход неизвестен, оставлены в списке «Не напечатаны».", "WARNING");
        _journal.Prune();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => LoopAsync(ct), ct);
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch { /* уже остановлен */ }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Чеки с телефона: сбой цикла наблюдения ({ex.Message}).", "WARNING");
            }

            // Ошибки сети: 10 → 20 → 40 → 60 с. Пауза сервера (429) уже учтена в _pauseUntil внутри опроса.
            // Без ошибок — 8 с ± 1 с случайно (2026-10-27, ревью К-П1, С-4).
            var delay = _errorStreak == 0
                ? PollInterval + TimeSpan.FromMilliseconds(Random.Shared.Next(-1000, 1001))
                : TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(_errorStreak, 4))));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { PosLogger.Log($"Чеки с телефона: подписчик не обработал изменение ({ex.Message}).", "WARNING"); }
    }

    // ------------------------------------------------------------------ опрос

    /// <summary>Один опрос сервера. Возвращает число чеков, отправленных на печать в этом опросе. Не бросает (кроме отмены).</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        if (!await _pollGate.WaitAsync(0, ct).ConfigureAwait(false))
            return 0; // предыдущий опрос ещё идёт (долгая печать)
        try
        {
            return await PollCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task<int> PollCoreAsync(CancellationToken ct)
    {
        if (!_settings.Enabled)
        {
            LastNote = "off";
            return 0;
        }

        var now = _env.Now();
        if (now < _pauseUntil)
        {
            LastNote = "pause";
            return 0;
        }

        // 2026-10-27, ревью К-П1 (С-4): паузу сервера проверяем ДО запроса назначения (во время паузы не ходим в сеть вообще).
        var left = _env.ThrottleLeft();
        if (left > TimeSpan.Zero)
        {
            _pauseUntil = now + left;
            LastNote = "pause";
            return 0;
        }

        // Назначение печатающей кассы — раз в 2 минуты (внутри), при обрыве остаётся последнее известное (но не старше 10 минут, см. MyCashboxes).
        await _settings.RefreshRoutingAsync(false, ct).ConfigureAwait(false);
        var mine = _settings.MyCashboxes();
        // 2026-10-27, ревью К-П1 (В-3): назначение на кассу, с которой ПК уже не работает, не печатаем (чужие чеки на эту бумагу).
        var here = _env.CurrentCashboxId()?.Trim();
        if (!string.IsNullOrEmpty(here))
            mine.RemoveWhere(cb => !string.Equals(cb, here, StringComparison.OrdinalIgnoreCase));
        if (mine.Count == 0)
        {
            LastNote = "not-assigned";
            return 0; // не назначено (или назначена другая касса) — в сеть за продажами не ходим
        }

        if (_env.IsOutage())
        {
            LastNote = "offline";
            return 0;
        }

        List<JsonElement> rows;
        try
        {
            // За полночь: продажа, оплаченная в 23:59, видна и в первом опросе нового дня.
            var from = now.LocalDateTime.AddMinutes(-10).Date;
            rows = await _env.ListSales(PageSize, from, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex) when (ex.StatusCode == 429)
        {
            // 2026-10-27, ревью К-П1 (С-4): печать телефона уступает сервер кассе — пауза не меньше 30 с (дольше, если так просит
            // Retry-After), каждый следующий 429 подряд — вдвое дольше, до 5 минут; сбрасывается после удачного опроса.
            _throttleStreak++;
            var floor = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(_throttleStreak - 1, 4))));
            var wait = _env.ThrottleLeft();
            if (wait < floor)
                wait = floor;
            _pauseUntil = now + wait;
            LastNote = "pause";
            PosLogger.Log($"Чеки с телефона: сервер просит паузу {wait.TotalSeconds:0} с (429) — опрос приостановлен.", "API");
            return 0;
        }
        catch (Exception ex)
        {
            // Нет связи, таймаут, 5xx — без шума: одна строка в журнал раз в 5 минут, опрос повторится сам (с нарастающей паузой).
            _errorStreak++;
            LastNote = "offline";
            if (DateTime.UtcNow - _lastErrorLogUtc > TimeSpan.FromMinutes(5))
            {
                _lastErrorLogUtc = DateTime.UtcNow;
                PosLogger.Log($"Чеки с телефона: сервер не ответил ({ex.GetType().Name}) — повторю.", "DEBUG");
            }
            return 0;
        }

        _errorStreak = 0;
        _throttleStreak = 0;
        LastNote = "ok";
        LastPollOk = now;
        return await ProcessRowsAsync(rows, mine, now, ct).ConfigureAwait(false);
    }

    private sealed class Candidate
    {
        public string Id = "";
        public string? Cashbox;
        public string? Staff;
        public DateTime? TimeUtc;
    }

    private async Task<int> ProcessRowsAsync(List<JsonElement> rows, HashSet<string> mine, DateTimeOffset now, CancellationToken ct)
    {
        var currentShift = _env.CurrentShiftId();
        _journal.RememberShift(currentShift);

        var staffFilter = _settings.Staff;
        var own = _env.OwnSaleIds();
        var token = _settings.EnabledToken;
        // «Нулевой» опрос после включения: всё, что уже есть на сервере, — «до включения наблюдения» (правило 4).
        var baselinePoll = !string.Equals(_journal.BaselineFor, token, StringComparison.Ordinal);

        var toPrint = new List<Candidate>();
        var listChanged = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                var id = PosSaleRowFormatter.TrySaleId(row);
                if (string.IsNullOrEmpty(id) || !seen.Add(id) || _journal.Get(id) != null)
                    continue; // правило 6

                // Правило 1: оплаченная продажа (в долг и частичный возврат — тоже чек).
                var status = (Str(row, "status") ?? "").ToLowerInvariant();
                if (status is not ("paid" or "debt" or "partially_returned"))
                    continue;

                // Правило 2: касса продажи назначена этому ПК. Нет кассы в ответе — не гадаем.
                var cashbox = IdOf(row, "cashbox") ?? IdOf(row, "cashbox_id");
                if (cashbox is null || !mine.Contains(cashbox))
                    continue;

                // Правило 3: «чужая» смена. Нет смены в ответе — не гадаем (не печатаем).
                var shift = IdOf(row, "shift") ?? IdOf(row, "shift_id");
                if (shift is null
                    || (currentShift is not null && string.Equals(shift, currentShift.Trim(), StringComparison.OrdinalIgnoreCase))
                    || _journal.IsOwnShift(shift)
                    || own?.Contains(id) == true)
                    continue;

                // Правило 5: необязательный список сотрудников.
                var staff = Str(row, "user_display");
                if (staffFilter.Count > 0
                    && !staffFilter.Any(f => staff != null && staff.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                var time = SaleTimeUtc(row);
                var entry = new PhonePrintEntry
                {
                    SaleId = id,
                    CashboxId = cashbox,
                    Number = SalesWindow.TryReceiptNumber(row),
                    Staff = staff,
                    Total = RowTotal(row),
                    SaleTimeUtc = time,
                    DiscoveredUtc = now.UtcDateTime,
                    UpdatedUtc = now.UtcDateTime,
                };

                if (baselinePoll)
                {
                    entry.Status = PhonePrintStatus.Ignored;
                    _journal.TryAdd(entry);
                    continue;
                }

                // Правило 4: старше 3 минут (или время не прочитано) — автоматически не печатаем, в список «Не напечатаны».
                if (time is null || now.UtcDateTime - time.Value > MaxAge)
                {
                    entry.Status = PhonePrintStatus.Skipped;
                    entry.Error = Tr.T(
                        "Чек старше 3 минут — автоматически не печатается.",
                        "Чек 3 мүнөттөн эски — өзү басылбайт.",
                        "The receipt is older than 3 minutes — not printed automatically.",
                        "Fiş 3 dakikadan eski — otomatik yazdırılmaz.",
                        "Chek 3 daqiqadan eski — avtomatik chop etilmaydi.");
                    if (_journal.TryAdd(entry))
                    {
                        listChanged = true;
                        PosLogger.Log($"Чеки с телефона: продажа {id} старше 3 минут — в список «Не напечатаны».", "PHONE");
                    }
                    continue;
                }

                toPrint.Add(new Candidate { Id = id, Cashbox = cashbox, Staff = staff, TimeUtc = time });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 2026-10-27, ревью К-П1 (С-5): сбой разбора одной строки не останавливает остальные продажи.
                PosLogger.Log($"Чеки с телефона: строка продажи пропущена из-за сбоя разбора ({ex.Message}).", "WARNING");
            }
        }

        if (baselinePoll)
        {
            // Метка ставится только после успешного чтения: нет связи — «нулевой» опрос повторится.
            _journal.SetBaselineFor(token);
            PosLogger.Log("Чеки с телефона: наблюдение включено — продажи, уже бывшие на сервере, не печатаются.", "PHONE");
        }

        if (listChanged)
            RaiseChanged();

        // 2026-10-27, ревью К-П1 (В-2): назначение могло уйти к другой кассе (кэш до 2 мин) — свежая проверка перед бумагой, один раз за опрос.
        // Не прочиталось — не печатаем сейчас: следующий опрос попробует снова (чек ещё не в журнале; через 3 минуты уйдёт в «Не напечатаны»).
        if (toPrint.Count > 0)
        {
            if (!await _settings.RefreshRoutingAsync(true, ct).ConfigureAwait(false))
            {
                PosLogger.Log("Чеки с телефона: назначение печатающей кассы не прочиталось — печать этого опроса отложена.", "PHONE");
                return 0;
            }
            var freshMine = _settings.MyCashboxes();
            var hereNow = _env.CurrentCashboxId()?.Trim();
            if (!string.IsNullOrEmpty(hereNow))
                freshMine.RemoveWhere(cb => !string.Equals(cb, hereNow, StringComparison.OrdinalIgnoreCase));
            toPrint.RemoveAll(x => x.Cashbox is null || !freshMine.Contains(x.Cashbox));
        }

        var printed = 0;
        foreach (var c in toPrint.OrderBy(x => x.TimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await PrintNewAsync(c, now, ct).ConfigureAwait(false))
                    printed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 2026-10-27, ревью К-П1 (С-5): сбой одного чека (например, разбор подробностей) не держит остальные.
                PosLogger.Log($"Чеки с телефона: продажа {c.Id} пропущена из-за сбоя разбора ({ex.Message}).", "WARNING");
            }
        }

        return printed;
    }

    // ------------------------------------------------------------------ печать

    private async Task<bool> PrintNewAsync(Candidate c, DateTimeOffset now, CancellationToken ct)
    {
        // Подробности продажи — до записи в журнал: нет связи — ничего не записано, следующий опрос попробует снова (пока ≤ 3 минут).
        JsonElement detail;
        try
        {
            detail = await _env.GetSale(c.Id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки с телефона: продажа {c.Id} не прочитана ({ex.Message}) — повторю.", "DEBUG");
            return false;
        }

        var number = SalesWindow.TryReceiptNumber(detail);
        var staff = c.Staff ?? CartDisplayHelper.TryCashierName(detail);
        var total = SalesWindow.ReadSaleTotal(detail) ?? 0m;
        var entry = new PhonePrintEntry
        {
            SaleId = c.Id,
            CashboxId = c.Cashbox,
            Number = number,
            Staff = staff,
            Total = total,
            SaleTimeUtc = c.TimeUtc,
            DiscoveredUtc = now.UtcDateTime,
            UpdatedUtc = now.UtcDateTime,
            Status = PhonePrintStatus.Printing,
            Attempts = 1,
        };
        // Запись «printing» — ДО принтера. Не записалась (или уже есть) — не печатаем: лучше пропустить, чем напечатать дважды.
        if (!_journal.TryAdd(entry))
            return false;

        return await PrintAndFinishAsync(entry, detail, maybePrinted: false).ConfigureAwait(false);
    }

    /// <summary>Печать и запись итога. Вызывающий уже поставил «printing».</summary>
    private async Task<bool> PrintAndFinishAsync(PhonePrintEntry entry, JsonElement detail, bool maybePrinted)
    {
        string? error;
        var timedOut = false;
        var buildFailed = false;
        try
        {
            string text;
            try
            {
                text = BuildText(detail, entry.Number, entry.Staff, maybePrinted);
            }
            catch
            {
                buildFailed = true; // текст не собран — на принтер ничего не ушло
                throw;
            }
            var task = _env.Print(text);
            if (await Task.WhenAny(task, Task.Delay(PrintTimeout)).ConfigureAwait(false) != task)
            {
                timedOut = true;
                error = Tr.T(
                    "Принтер не ответил за 40 секунд — неизвестно, вышел ли чек.",
                    "Принтер 40 секундда жооп берген жок — чек чыккан-чыкпаганы белгисиз.",
                    "The printer did not answer in 40 seconds — it is unknown whether the receipt came out.",
                    "Yazıcı 40 saniyede yanıt vermedi — fişin çıkıp çıkmadığı bilinmiyor.",
                    "Printer 40 soniyada javob bermadi — chek chiqqani noma'lum.");
            }
            else
            {
                error = await task.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            error = Tr.T("Не удалось напечатать чек: ", "Чекти басып чыгаруу мүмкүн болгон жок: ", "Could not print the receipt: ", "Fiş yazdırılamadı: ", "Chekni chop etib bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Чеки с телефона: печать продажи {entry.SaleId} не удалась: {ex}", "PRINTER");
        }

        if (error is null)
        {
            _journal.Finish(entry.SaleId, PhonePrintStatus.Printed, null, false);
            PosLogger.Log($"Чеки с телефона: чек {entry.Number ?? entry.SaleId} напечатан ({entry.Staff}, {entry.Total:N2}).", "PHONE");
            RaiseChanged();
            return true;
        }

        // 2026-10-27, ревью К-П1 (В-1): отказ после отправки — исход неизвестен (LPT: «не ответил» через 8 с, а задание ждёт порт до 15 с и может выйти),
        // повтор пойдёт с «ПОВТОРНАЯ ПЕЧАТЬ». «Точно не напечатано» — только отказ до отправки (печать выключена, принтер не выбран) или сбой сборки текста.
        var unknown = maybePrinted || timedOut || (!buildFailed && !_env.RefusedBeforeSend());
        _journal.Finish(entry.SaleId, PhonePrintStatus.Failed, error, unknown);
        PosLogger.Log($"Чеки с телефона: чек {entry.Number ?? entry.SaleId} не напечатан — {error}", "PRINTER");
        RaiseChanged();
        return false;
    }

    /// <summary>Кнопка «Напечатать» в списке «Не напечатаны» (также повтор после отказа принтера). Возвращает null — напечатано,
    /// иначе понятная причина. Двойное нажатие и чек, уже напечатанный другим путём, — отказ без печати.</summary>
    public async Task<string?> RetryAsync(string saleId, CancellationToken ct = default)
    {
        var before = _journal.TryBeginRetry(saleId);
        if (before is null)
            return Tr.T(
                "Этот чек уже печатается или напечатан.",
                "Бул чек азыр басылып жатат же басылып чыккан.",
                "This receipt is already being printed or has been printed.",
                "Bu fiş zaten yazdırılıyor veya yazdırıldı.",
                "Bu chek allaqachon chop etilmoqda yoki chop etilgan.");

        JsonElement detail;
        try
        {
            detail = await _env.GetSale(saleId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Печати не было — возвращаем прежний статус, чек остаётся в списке.
            _journal.Finish(saleId, before.Status, before.Error, before.MaybePrinted);
            RaiseChanged();
            return Tr.T("Не удалось получить чек с сервера: ", "Чекти серверден алуу мүмкүн болгон жок: ", "Could not get the receipt from the server: ", "Fiş sunucudan alınamadı: ", "Chekni serverdan olib bo'lmadi: ") + ex.Message;
        }

        var ok = await PrintAndFinishAsync(before, detail, before.MaybePrinted).ConfigureAwait(false);
        return ok ? null : _journal.Get(saleId)?.Error ?? Tr.T("Не напечатано.", "Басылган жок.", "Not printed.", "Yazdırılamadı.", "Chop etilmadi.");
    }

    /// <summary>Чек, не напечатанный автоматически: отказ принтера, старше 3 минут, неясный исход.</summary>
    public List<PhonePrintEntry> Unprinted() =>
        _journal.Snapshot()
            .Where(e => e.Status is PhonePrintStatus.Failed or PhonePrintStatus.Skipped)
            .OrderByDescending(e => e.SaleTimeUtc ?? e.DiscoveredUtc)
            .ToList();

    // ------------------------------------------------------------------ текст чека

    /// <summary>Чек телефона: существующий построитель, как у копии из «Продаж», плюс строка «Продажа с телефона: сотрудник»
    /// над разделителем шапки (ТЗ 3B/В-6). «ПОВТОРНАЯ ПЕЧАТЬ» — только когда исход прошлой печати неизвестен.</summary>
    public static string BuildText(JsonElement detail, string? number, string? staff, bool isReprint)
    {
        var text = SaleReceiptTextBuilder.Build(
            detail,
            number,
            SalesWindow.ReadSaleDiscount(detail),
            SalesWindow.ReadSaleTotal(detail),
            isReprint);
        var label = Tr.T("Продажа с телефона", "Телефондон сатуу", "Sale from phone", "Telefondan satış", "Telefondan sotuv");
        var line = string.IsNullOrWhiteSpace(staff) ? label : label + ": " + staff.Trim();
        var lines = text.Split('\n').ToList();
        var dash = lines.FindIndex(l => l.Length >= 8 && l.All(ch => ch == '-'));
        lines.Insert(dash < 0 ? Math.Min(1, lines.Count) : dash, line);
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ разбор строки списка продаж

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static string? IdOf(JsonElement row, string key)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Object)
            return v.TryGetProperty("id", out var id) ? Scalar(id) : null;
        return Scalar(v);
    }

    private static string? Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    };

    /// <summary>Время продажи: момент оплаты (paid_at), иначе создания (created_at). Без пояса — местное время ПК.</summary>
    private static DateTime? SaleTimeUtc(JsonElement row)
    {
        foreach (var key in new[] { "paid_at", "created_at" })
        {
            if (Str(row, key) is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                return at.UtcDateTime;
        }
        return null;
    }

    private static decimal RowTotal(JsonElement row)
    {
        foreach (var key in new[] { "total", "grand_total", "total_amount", "amount", "total_sum", "final_total", "order_total" })
        {
            if (row.TryGetProperty(key, out var v))
            {
                var text = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    return d;
            }
        }
        return 0m;
    }
}
