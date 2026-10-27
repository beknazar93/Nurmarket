using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Services;
using NurMarketKassa.Services.PhoneLink;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-27, пакет К-П5 (ТЗ «Мобилка и касса», п. 4.2): телефон — беспроводной сканер кассы. Операция <c>scan</c>
/// с телом {"code":"штрихкод","seq":число[, "at":секунды]}: штрихкод встаёт в чек кассы как обычный скан (Basket.EnqueueBarcode), но только
/// если главное окно кассы открыто и активно. Класс не знает про окна и каталог — всё приходит делегатами
/// (так его можно проверить стендом без настоящего окна):
///  — <c>isTillReady</c>: окно кассы активно и готово к приёму (быстрая проверка до поиска товара);
///  — <c>lookup</c>: найден ли товар в каталоге кассы (без побочных действий на кассе) и как называется;
///  — <c>enqueue</c>: поставить код в очередь сканов кассы и сказать, принят ли он (окно активно, оплата не идёт).
/// Ответы: added / duplicate (тот же телефон и seq — уже добавлено, ok:true) / not_found / till_inactive / busy / stale / bad_request.
/// Новые операции (печать чека, корзина) добавляются так же: свой обработчик + <c>server.Register("op", …)</c>.
///
/// 2026-10-27, ревью Ф-2/Ф-3/Ф-5/С-1/С-2:
///  — готовность окна проверяется ещё раз ПРЯМО при постановке в очередь (<c>enqueue</c>), а не только до поиска товара (поиск — до секунд);
///  — пока идёт оплата, скан не принимается: ответ <c>busy</c> («идёт оплата») — товар не уйдёт в чек следующего покупателя;
///  — телефон может прислать время скана <c>at</c> (секунды UTC): скан старше 30 с — <c>stale</c>, в чек не добавляется;
///  — принятые seq запоминаются на диске (<see cref="PhoneLinkPairing.RememberScanSeq"/>): после перезапуска кассы повтор не добавит товар;
///  — поиск товара ограничен 2,5 с (телефон ждёт ответ 4 с), дольше — как «нет связи с сервером».</summary>
public sealed class PhoneScanHandler
{
    public enum LookupKind { Found, NotFound, Unavailable }

    /// <summary>Что вышло при постановке кода в очередь кассы.</summary>
    public enum EnqueueResult
    {
        /// <summary>Код поставлен в очередь сканов чека.</summary>
        Added,
        /// <summary>Окно кассы не активно (свёрнуто, поверх диалог, возврат…).</summary>
        TillInactive,
        /// <summary>Идёт оплата — скан не принимается (телефон повторит позже).</summary>
        Busy,
    }

    /// <param name="Kind">Нашли ли.</param>
    /// <param name="Name">Название товара, если известно.</param>
    /// <param name="Code">Код в том виде, как его надо поставить в очередь (очищенный от мусора); null — как пришёл.</param>
    public sealed record LookupResult(LookupKind Kind, string? Name = null, string? Code = null);

    private const int MaxCodeLength = 128;
    private const int RememberSeqPerPhone = 500;
    /// <summary>Скан старше этого (по времени <c>at</c>, присланному телефоном) в чек не попадает.</summary>
    public static readonly TimeSpan MaxScanAge = TimeSpan.FromSeconds(30);
    /// <summary>Сколько ждём поиск товара: телефон ждёт ответ 4 с.</summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromMilliseconds(2500);

    private readonly Func<CancellationToken, Task<bool>> _isTillReady;
    private readonly Func<string, CancellationToken, Task<LookupResult>> _lookup;
    private readonly Func<string, CancellationToken, Task<EnqueueResult>> _enqueue;
    private readonly PhoneLinkPairing? _store;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, SeqMemory> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <param name="store">Список телефонов, где seq переживают перезапуск; null — только в памяти (стенды).</param>
    public PhoneScanHandler(Func<CancellationToken, Task<bool>> isTillReady,
        Func<string, CancellationToken, Task<LookupResult>> lookup, Func<string, CancellationToken, Task<EnqueueResult>> enqueue,
        PhoneLinkPairing? store = null, Func<DateTime>? utcNow = null)
    {
        _isTillReady = isTillReady;
        _lookup = lookup;
        _enqueue = enqueue;
        _store = store;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Сколько кодов принято с телефонов с запуска (для проверки/состояния).</summary>
    public long AddedCount { get; private set; }

    public async Task<PhoneLinkReply> HandleAsync(PhoneLinkCall call, CancellationToken ct)
    {
        if (call.Body["code"] is not JsonValue codeNode || !codeNode.TryGetValue<string>(out var rawCode))
            return PhoneLinkReply.Fail(PhoneLinkCodes.BadRequest, "Нет штрихкода.");
        var code = rawCode.Trim();
        if (code.Length == 0 || code.Length > MaxCodeLength || ContainsControl(code))
            return PhoneLinkReply.Fail(PhoneLinkCodes.BadRequest, "Неверный штрихкод.");
        if (!TryReadSeq(call.Body["seq"], out var seq))
            return PhoneLinkReply.Fail(PhoneLinkCodes.BadRequest, "Нет номера скана (seq).");

        var echo = new JsonObject { ["seq"] = seq };

        // Этот скан уже был принят (телефон повторил из-за потерянного ответа) — не добавляем второй раз.
        if (WasSeen(call.Phone, seq))
            return PhoneLinkReply.Success(PhoneLinkCodes.Duplicate, null, echo);

        // 2026-10-27, ревью С-1: телефон прислал время скана — старый скан не должен попасть в чек следующего покупателя.
        if (TryReadSeq(call.Body["at"], out var scannedAt) && scannedAt > 0)
        {
            var age = _utcNow() - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(scannedAt, 0, 253402300799L)).UtcDateTime;
            if (age > MaxScanAge)
                return PhoneLinkReply.Fail(PhoneLinkCodes.Stale, "Скан устарел (старше 30 секунд) — отсканируйте ещё раз.", echo);
        }

        // Тот же запрос уже обрабатывается (телефон повторил, не дождавшись ответа) — пусть повторит позже.
        var flightKey = call.Phone + "|" + seq;
        lock (_gate)
        {
            if (!_inFlight.Add(flightKey))
                return PhoneLinkReply.Fail(PhoneLinkCodes.Busy, "Этот скан уже обрабатывается, повторите.", echo);
        }
        string? foundName = null;
        try
        {
            if (!await _isTillReady(ct).ConfigureAwait(false))
                return PhoneLinkReply.Fail(PhoneLinkCodes.TillInactive, "Откройте окно кассы.", echo);

            LookupResult found;
            try
            {
                using var lookupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lookupCts.CancelAfter(LookupBudget);
                found = await _lookup(code, lookupCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Поиск не уложился в 2,5 с — телефон ждёт ответ 4 с, отвечаем как при недоступном сервере.
                found = new LookupResult(LookupKind.Unavailable);
            }
            foundName = found.Name;
            if (found.Kind == LookupKind.NotFound)
                return PhoneLinkReply.Fail(PhoneLinkCodes.NotFound, "Товар не найден в каталоге кассы.", echo);
            if (found.Kind == LookupKind.Unavailable)
                return PhoneLinkReply.Fail(PhoneLinkCodes.NotFound, "Товар не найден в каталоге кассы (нет связи с сервером).", echo);

            // 2026-10-27, ревью Ф-2/Ф-3: окно и оплата проверяются ещё раз в момент постановки в очередь (поиск мог идти секунды).
            var queued = await _enqueue(string.IsNullOrWhiteSpace(found.Code) ? code : found.Code!, ct).ConfigureAwait(false);
            if (queued == EnqueueResult.Busy)
                return PhoneLinkReply.Fail(PhoneLinkCodes.Busy, "Идёт оплата — сканируйте после неё.", echo);
            if (queued != EnqueueResult.Added)
                return PhoneLinkReply.Fail(PhoneLinkCodes.TillInactive, "Откройте окно кассы.", echo);

            lock (_gate)
            {
                Remember(call.Phone, seq);
                AddedCount++;
            }
        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(flightKey);
        }

        if (!string.IsNullOrWhiteSpace(foundName))
            echo["name"] = foundName;
        return PhoneLinkReply.Success(PhoneLinkCodes.Added, null, echo);
    }

    private bool WasSeen(string phone, long seq)
    {
        lock (_gate)
        {
            if (_seen.TryGetValue(phone, out var memory) && memory.Contains(seq))
                return true;
        }
        return _store?.IsScanSeqKnown(phone, seq) == true;
    }

    private void Remember(string phone, long seq)
    {
        if (!_seen.TryGetValue(phone, out var memory))
            _seen[phone] = memory = new SeqMemory();
        memory.Add(seq);
        _store?.RememberScanSeq(phone, seq);
    }

    private static bool ContainsControl(string text)
    {
        foreach (var c in text)
            if (char.IsControl(c))
                return true;
        return false;
    }

    private static bool TryReadSeq(JsonNode? node, out long seq)
    {
        seq = 0;
        if (node is not JsonValue value)
            return false;
        if (value.TryGetValue<long>(out seq))
            return true;
        if (value.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15)
        {
            seq = (long)d;
            return true;
        }
        return false;
    }

    /// <summary>Последние seq одного телефона (по порядку добавления, не больше 500).</summary>
    private sealed class SeqMemory
    {
        private readonly HashSet<long> _set = new();
        private readonly Queue<long> _order = new();

        public bool Contains(long seq) => _set.Contains(seq);

        public bool Add(long seq)
        {
            if (!_set.Add(seq))
                return false;
            _order.Enqueue(seq);
            while (_order.Count > RememberSeqPerPhone)
                _set.Remove(_order.Dequeue());
            return true;
        }
    }
}
