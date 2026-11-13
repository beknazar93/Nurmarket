using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13: документ возврата от клиента, как его видит сервер (строка списка возвратов).</summary>
public sealed record ServerReturnDoc(string Id, string? SaleId, decimal Amount, bool Voided);

/// <summary>2026-11-13: итог сверки журнала кассы с возвратами сервера — какие записи поправлены (суммы «до/после» и сторно в смене).</summary>
public sealed record ReconcileReport(IReadOnlyList<ReturnJournalChange> Changes)
{
    public bool Any => Changes.Count > 0;

    /// <summary>Изменение суммы возвратов (после − до): отрицательное — возвраты стали меньше, деньги вернулись в ящик.</summary>
    public decimal DeltaAmount => Changes.Sum(c => c.NewAmount - c.OldAmount);

    public decimal DeltaCash => Changes.Sum(c => c.DeltaCash);

    /// <summary>Текст кассиру (5 языков): возврат изменён на другом устройстве, разница и что записано в смену.</summary>
    public string NoticeText()
    {
        var more = DeltaAmount > 0.004m;
        var sum = MoneyText.Som(Math.Abs(DeltaAmount));
        var cash = MoneyText.Som(Math.Abs(DeltaCash));
        var n = Changes.Count;
        var prev = Changes.Any(c => c.PreviousShift);
        var text = more
            ? Tr.T($"Возврат изменён на другом устройстве (сайт, программа владельца или другая касса): возвратов стало больше на {sum} (наличными {cash}). Поправка записана в смену ({n}). Проверьте «Касса:».",
                $"Кайтаруу башка түзмөктө өзгөртүлдү (сайт, ээсинин программасы же башка касса): кайтаруулар {sum} көбөйдү (накталай {cash}). Оңдоо сменага жазылды ({n}). «Касса:» текшериңиз.",
                $"A return was changed on another device (website, owner program or another till): returns grew by {sum} (cash {cash}). A correction was recorded in the shift ({n}). Check “Till:”.",
                $"İade başka bir cihazda değiştirildi (site, sahip programı veya başka kasa): iadeler {sum} arttı (nakit {cash}). Düzeltme vardiyaya yazıldı ({n}). «Kasa:» değerini kontrol edin.",
                $"Qaytarish boshqa qurilmada o'zgartirildi (sayt, egasining dasturi yoki boshqa kassa): qaytarishlar {sum} ga ko'paydi (naqd {cash}). Tuzatish smenaga yozildi ({n}). «Kassa:»ni tekshiring.")
            : Tr.T($"Возврат изменён или удалён на другом устройстве (сайт, программа владельца или другая касса): возвратов стало меньше на {sum} (наличными {cash} вернулись в ящик). Поправка записана в смену ({n}). Проверьте «Касса:».",
                $"Кайтаруу башка түзмөктө өзгөртүлдү же өчүрүлдү (сайт, ээсинин программасы же башка касса): кайтаруулар {sum} азайды (накталай {cash} кутуга кайтты). Оңдоо сменага жазылды ({n}). «Касса:» текшериңиз.",
                $"A return was changed or deleted on another device (website, owner program or another till): returns dropped by {sum} (cash {cash} came back to the drawer). A correction was recorded in the shift ({n}). Check “Till:”.",
                $"İade başka bir cihazda değiştirildi veya silindi (site, sahip programı veya başka kasa): iadeler {sum} azaldı (nakit {cash} çekmeceye döndü). Düzeltme vardiyaya yazıldı ({n}). «Kasa:» değerini kontrol edin.",
                $"Qaytarish boshqa qurilmada o'zgartirildi yoki o'chirildi (sayt, egasining dasturi yoki boshqa kassa): qaytarishlar {sum} ga kamaydi (naqd {cash} tortmaga qaytdi). Tuzatish smenaga yozildi ({n}). «Kassa:»ni tekshiring.");
        if (prev)
            text += " " + Tr.T("Часть возвратов была в прошлой смене — в окне закрытия это строка «Отмена возврата прошлой смены».",
                "Кайтаруулардын бир бөлүгү өткөн сменада болгон — жабуу терезесинде бул «Өткөн сменанын кайтарууну жокко чыгаруу» сабы.",
                "Some of the returns were in a previous shift — in the close-shift window this is the “Reversal of a previous shift's return” row.",
                "İadelerin bir kısmı önceki vardiyadaydı — kapatma penceresinde bu «Önceki vardiya iadesinin iptali» satırıdır.",
                "Qaytarishlarning bir qismi o'tgan smenada bo'lgan — yopish oynasida bu «O'tgan smena qaytarishini bekor qilish» qatori.");
        return text;
    }
}

/// <summary>2026-11-13 (проверка денег перед 1.17.86, п. 2, п. 3): журнал возвратов кассы (<see cref="SaleReturnLedger"/>) и смена должны сходиться с возвратами сервера и тогда, когда возврат
/// изменили НЕ в окне этой кассы: правка дошла, а ответ потерян (п. 2); возврат исправили или аннулировали в программе владельца, на сайте или на другой кассе (п. 3, деньги ушли
/// из ящика ЭТОЙ кассы). Касса сверяет сумму документа сервера с той, что записана в журнале (<see cref="SaleLedgerRefund.ServerAmount"/>), и при разнице пишет поправку тем же путём,
/// что и правка в окне (<see cref="CustomerReturnJournals"/>): журнал пересчитан, в ТЕКУЩУЮ смену — сторно (возврат прошлой смены — с пометкой, п. 7). Повтор безопасен: после поправки
/// сумма в журнале равна серверной, и сверка больше ничего не пишет. Смены нет — ничего не трогаем (поправка запишется, когда смена откроется).</summary>
public static class CustomerReturnReconciler
{
    /// <summary>Поправить журнал и смену по одному документу сервера. null — записи этого документа в журнале нет (возврат не наш), сумма совпала, о сумме журнал не знает или смены нет.</summary>
    public static ReturnJournalChange? ApplyDoc(string docId, string? saleId, decimal serverAmount, bool voided, string? shiftId)
    {
        if (string.IsNullOrWhiteSpace(docId) || string.IsNullOrWhiteSpace(shiftId))
            return null;
        // только по id документа (сумма 0 — без подбора «по сумме»): сверка не должна принять чужой возврат за свой
        var peek = SaleReturnLedger.ChangeRefundByDoc(saleId, docId, 0m, (_, _) => false, dryRun: true);
        // 2026-11-13: только обычные возвраты (Source = return); обмен товара и записи без пометки не трогаем — у обмена другие суммы и деньги (в ящик уходит одна сдача)
        if (peek is null || peek.Before.Source != SaleLedgerRefund.SourceReturn)
            return null;
        if (voided)
            return CustomerReturnJournals.Void(peek.SaleId, docId, serverAmount, shiftId);
        if (peek.Before.ServerAmount is not { } known || Math.Abs(known - serverAmount) < 0.005m)
            return null;
        return CustomerReturnJournals.Edit(peek.SaleId, docId, known, serverAmount, Array.Empty<CustomerReturnEditLine>(), shiftId);
    }

    /// <summary>Сверить все возвраты этой кассы (сделаны онлайн, не старше <paramref name="within"/>) со списком документов сервера. Документа нет в списке — не трогаем.</summary>
    public static ReconcileReport Reconcile(IReadOnlyList<ServerReturnDoc> docs, string? shiftId, TimeSpan within)
    {
        var changes = new List<ReturnJournalChange>();
        if (string.IsNullOrWhiteSpace(shiftId) || docs.Count == 0)
            return new ReconcileReport(changes);
        var byId = new Dictionary<string, ServerReturnDoc>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in docs)
            byId.TryAdd(d.Id, d);
        foreach (var (saleId, refund) in SaleReturnLedger.ServerDocRefunds(within))
        {
            if (!byId.TryGetValue(refund.ServerDocId!, out var doc))
                continue;
            if (ApplyDoc(doc.Id, saleId, doc.Amount, doc.Voided, shiftId) is { } change)
                changes.Add(change);
        }
        return new ReconcileReport(changes);
    }

    /// <summary>2026-11-13 («возврат из программы владельца не виден журналу кассы»): по чекам, проданным не раньше <paramref name="rangeStart"/>, записать в журнал, когда и на какую сумму
    /// сервер знает возвраты, которых этот журнал не видит (сделаны в программе владельца, на сайте, другой кассой). Возврат без связи на кассе учитывает это (OfflineOperationsService).
    /// Отклонение до 2 % от суммы чека (но не меньше 1 сома) — скидка на чек при возврате считается сервером иначе, это не чужой возврат.</summary>
    public static void NoteForeignReturns(IReadOnlyList<ServerReturnDoc> docs, DateTimeOffset rangeStart, DateTimeOffset now)
    {
        foreach (var entry in SaleReturnLedger.SoldSince(rangeStart))
        {
            var server = docs.Where(d => !d.Voided && string.Equals(d.SaleId, entry.SaleId, StringComparison.OrdinalIgnoreCase)).Sum(d => d.Amount);
            var mine = entry.Refunds.Sum(r => r.ServerAmount ?? r.Amount);
            var foreign = server - mine;
            if (foreign <= Math.Max(1m, entry.Total * 0.02m))
                foreign = 0m;
            SaleReturnLedger.NoteServerReturns(entry.SaleId, now, foreign);
        }
    }

    public static ServerReturnDoc ToDoc(NurCrmReportsApi.PosReturn r) => new(r.Id, r.SaleId, r.Amount, NurCrmReportsApi.IsVoided(r));
}

/// <summary>2026-11-13 (проверка денег, п. 2, п. 3): фоновая сверка возвратов кассы с сервером. Раз в несколько минут (и при закрытии смены) — один запрос списка возвратов за нужные дни
/// (с аннулированными); изменённая или аннулированная сумма возврата этой кассы → поправка в журнал и смену, кассиру — сообщение. Не работает в программе владельца (у неё нет смены) и пока
/// на экране окно правки возврата (<see cref="Hold"/>): оно само пишет те же поправки.</summary>
public static class ReturnJournalSync
{
    /// <summary>Как часто сверять, если у кассы есть свежие (меньше суток) возвраты: изменить их можно только 24 часа.</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Как часто сверять «чужие» возвраты по недавним чекам (для возврата без связи) и возвраты старше суток.</summary>
    public static TimeSpan ForeignEvery { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Глубина: возвраты этой кассы не старше трёх суток (правка — до 24 ч; запас на выключенную кассу).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(3);

    /// <summary>Свежие возвраты — проверяются на каждом такте.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(26);

    /// <summary>Чеки не старше этого проверяются на возвраты «не отсюда».</summary>
    public static readonly TimeSpan ForeignDepth = TimeSpan.FromDays(7);

    /// <summary>Только для тестов: подмена запроса списка возвратов (даты — с какого дня).</summary>
    public static Func<DateTime, CancellationToken, Task<IReadOnlyList<ServerReturnDoc>?>>? DocsProvider { get; set; }

    /// <summary>Только для тестов: «сейчас» (UTC).</summary>
    public static Func<DateTime>? NowUtcOverride { get; set; }

    private static DateTime NowUtc => NowUtcOverride?.Invoke() ?? DateTime.UtcNow;

    private static int _holds;
    private static int _started;
    private static DateTime _lastForeignUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static readonly SyncCenter.KickSignal Wake = new();

    private sealed class HoldToken : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref _holds);
        }
    }

    /// <summary>Пока токен жив, сверка молчит: окно правки/аннулирования возврата ходит на сервер и пишет журнал само (иначе сверка успела бы увидеть сумму сервера раньше окна).</summary>
    public static IDisposable Hold()
    {
        Interlocked.Increment(ref _holds);
        return new HoldToken();
    }

    /// <summary>Окно правки/аннулирования возврата сейчас ходит на сервер (<see cref="Hold"/>).</summary>
    public static bool IsHeld => Volatile.Read(ref _holds) > 0;

    /// <summary>Сверить журнал кассы со списком документов, который кассе и так пришёл (окно «Возвраты по чеку», история чеков): без нового запроса. Программа владельца, смена закрыта или
    /// идёт правка в окне — ничего не делает. Поправки — в журнал и текущую смену, кассиру — сообщение.</summary>
    public static ReconcileReport? ReconcileDocs(IReadOnlyList<ServerReturnDoc> docs)
    {
        if (AppMode.IsOwner || IsHeld || string.IsNullOrWhiteSpace(PosApp.ActiveShiftId) || docs.Count == 0)
            return null;
        try
        {
            var report = CustomerReturnReconciler.Reconcile(docs, PosApp.ActiveShiftId, Window);
            if (report.Any)
                Announce(report);
            return report;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"RETURNS: сверка списка возвратов с журналом не выполнена ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Сообщить кассиру о поправке (журнал, плашка главного окна).</summary>
    public static void Announce(ReconcileReport report)
    {
        PosLogger.Log($"RETURNS: сверка с сервером — поправлено возвратов: {report.Changes.Count}, разница {report.DeltaAmount:0.00} (наличными {report.DeltaCash:0.00}).", "SALES");
        ShiftStateService.RaiseShiftNotice(report.NoticeText(), true);
    }

    /// <summary>Запустить фон (один раз за запуск; после входа в компанию).</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await SyncCenter.WaitAdaptiveAsync(() => SyncCenter.QuietInterval(Interval), Wake, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    if (!SyncCenter.ShouldPause())
                        await TickAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.LogThrottled("return-sync-loop-" + ex.GetType().Name, $"RETURNS: сбой сверки возвратов с сервером: {ex.GetType().Name}: {ex.Message}", "WARNING");
                }
            }
        });
    }

    /// <summary>Сверить как можно скорее (смена открывается/закрывается, окно возвратов чека загрузилось).</summary>
    public static void Kick() => Wake.Kick();

    /// <summary>Один проход. <paramref name="force"/> — не ждать срока «чужих» возвратов (окно закрытия смены). null — сверять нечего/нельзя; иначе итог (возможно пустой).</summary>
    public static async Task<ReconcileReport?> TickAsync(bool force = false, CancellationToken ct = default)
    {
        if (AppMode.IsOwner || Volatile.Read(ref _holds) > 0)
            return null;
        var shiftId = PosApp.ActiveShiftId;
        var fresh = SaleReturnLedger.ServerDocRefunds(Fresh);
        var foreignDue = force || NowUtc - _lastForeignUtc >= ForeignEvery;
        if (fresh.Count == 0 && !foreignDue)
            return null;
        if (!await OneAtATime.WaitAsync(0, ct).ConfigureAwait(false))
            return null;
        try
        {
            var today = DateTime.Now.Date;
            var from = fresh.Count > 0 ? fresh.Min(f => f.Refund.At.LocalDateTime.Date) : today;
            if (foreignDue)
                from = from < today - ForeignDepth ? from : today - ForeignDepth;
            var docs = await LoadDocsAsync(from, ct).ConfigureAwait(false);
            if (docs is null)
                return null;
            if (Volatile.Read(ref _holds) > 0) // пока ждали сервер, открылось окно правки — оно запишет само
                return null;
            if (foreignDue)
            {
                CustomerReturnReconciler.NoteForeignReturns(docs, new DateTimeOffset(from, DateTimeOffset.Now.Offset), DateTimeOffset.Now);
                _lastForeignUtc = NowUtc;
            }
            var report = CustomerReturnReconciler.Reconcile(docs, shiftId, Window);
            if (report.Any)
                Announce(report);
            return report;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.LogThrottled("return-sync-" + ex.GetType().Name, $"RETURNS: возвраты с сервером не сверены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return null;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static async Task<IReadOnlyList<ServerReturnDoc>?> LoadDocsAsync(DateTime from, CancellationToken ct)
    {
        if (DocsProvider is { } provider)
            return await provider(from, ct).ConfigureAwait(false);
        var list = await NurCrmReportsApi.ListReturnsAsync(from, DateTime.Today.AddDays(1), ct: ct, includeVoided: ReturnEditGate.FeatureOn(ReturnDocKind.Customer)).ConfigureAwait(false);
        return list?.Select(CustomerReturnReconciler.ToDoc).ToList();
    }

    /// <summary>Только для тестов: забыть состояние.</summary>
    internal static void ResetForTests()
    {
        _lastForeignUtc = DateTime.MinValue;
        DocsProvider = null;
        NowUtcOverride = null;
    }
}
