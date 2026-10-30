using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

// 2026-10-25, ТЗ «оприходование в фоне» (Q1): состояние повторов прихода вне памяти одной строки.
// Раньше «что уже отправили» (ключ прихода, прошлый PATCH остатка, цена продажи до прихода, созданный товар) жило в памяти
// PurchaseReceivingService: _pendingReceiptKey — ОДИН ключ на всё приложение (второй приход затирал ключ первого), а три
// ConditionalWeakTable были привязаны к объекту строки окна и пропадали с перезапуском. Очередь фоновых приходов
// (BackgroundReceivingQueue) хранит это состояние в задаче на диске и передаёт в PostAsync. Без состояния (state == null)
// PostAsync работает ровно как раньше.

/// <summary>Исход отправки документа прихода от поставщика. Unknown выставляется ДО отправки: убили процесс на полпути — при старте
/// это «исход неизвестен», и перед повтором приход сначала ищется на сервере.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReceivingReceiptOutcome
{
    NotSent,
    Posted,
    Rejected,
    Unknown,
}

/// <summary>Прошлый PATCH остатка без поставщика: остаток до прибавки и записанный (см. три исхода в PurchaseReceivingService).</summary>
public sealed class StockPatchState
{
    public double PreStock { get; set; }
    public double Target { get; set; }
}

/// <summary>Состояние одного прихода между попытками. Все изменения — под <see cref="Gate"/>; после каждого — <see cref="Changed"/>
/// (очередь пишет задачу на диск синхронно, до следующего запроса к серверу).</summary>
public sealed class ReceivingPostState
{
    [JsonIgnore] public object Gate { get; } = new();

    /// <summary>Ключ идемпотентности ЭТОГО прихода (свой у каждой задачи). Создаётся до отправки и хранится на диске.</summary>
    public string? ReceiptKey { get; set; }
    public DateTime? ReceiptFirstSentUtc { get; set; }
    public ReceivingReceiptOutcome ReceiptOutcome { get; set; }
    public string? ReceiptId { get; set; }
    public double? ReceiptTotal { get; set; }
    public DateTime? ReceiptAt { get; set; }

    // 2026-10-30 (нумерация закупок): номер и дата накладной ПОСТАВЩИКА (вводит кассир / читает импорт накладной) — уходят вместе с приходом;
    // DocNumber — номер документа в кассе («ПР-2026-000123» или временный «ПР-2026-ч5»), для показа в очереди и итоге.
    public string? SupplierDocNumber { get; set; }
    public DateTime? SupplierDocDate { get; set; }
    public string? DocNumber { get; set; }

    public Dictionary<string, StockPatchState> StockPatches { get; set; } = new();
    public Dictionary<string, double> CreatedSalePrice { get; set; } = new();
    public Dictionary<string, double> PriceBefore { get; set; } = new();
    public HashSet<string> CreateAttempted { get; set; } = new();
    public HashSet<string> VariantsStarted { get; set; } = new();
    public HashSet<string> VariantsDone { get; set; } = new();
    /// <summary>Ключ строки → posted | rejected | unknown | check. Строка без записи — ещё не обработана.</summary>
    public Dictionary<string, string> LineOutcome { get; set; } = new();

    /// <summary>Ключ строки (JobLine.Key) по объекту строки приёмки. Задаёт очередь.</summary>
    [JsonIgnore] public Func<ReceivingLineVm, string> KeyOf { get; set; } = l => l.Barcode + "|" + l.ProductName;

    /// <summary>true — остановка программы (а не таймаут/авария): отмену выпускаем наружу, задача останется «отправляется».</summary>
    [JsonIgnore] public Func<bool>? StopRequested { get; set; }

    /// <summary>Приход с этим номером уже принадлежит другой задаче очереди (сверка не должна принять его за свой).</summary>
    [JsonIgnore] public Func<string, bool>? ReceiptClaimed { get; set; }

    /// <summary>Попытка оборвалась (таймаут/авария): цикл по строкам останавливается, дальше — повтор очередью.</summary>
    [JsonIgnore] public bool Interrupted { get; set; }

    /// <summary>HTTP-статус последнего отказа сервера (429, 401, 5xx…); 0 — сбой без ответа (обрыв, таймаут).</summary>
    [JsonIgnore] public int? LastStatusCode { get; set; }

    [JsonIgnore] public int CreatedThisAttempt { get; set; }

    /// <summary>2026-10-30 (скорость массовой загрузки): сколько строк прихода обрабатывается одновременно (создание товара, запись остатка). 1 — по одной, как всегда;
    /// больше 1 ставит только очередь для задач мастера «Массовая загрузка» (BackgroundReceivingQueue.BulkPostParallel).</summary>
    [JsonIgnore] public int Parallelism { get; set; } = 1;

    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();

    internal bool IsStopping(CancellationToken ct) => StopRequested?.Invoke() ?? ct.IsCancellationRequested;

    // ------------------------------------------------------------------ документ прихода

    /// <summary>Перед отправкой: ключ (создаётся один раз) и исход «неизвестен» — записать на диск.</summary>
    internal string BeginSend()
    {
        string key;
        lock (Gate)
        {
            if (ReceiptOutcome == ReceivingReceiptOutcome.Rejected)
            {
                ReceiptOutcome = ReceivingReceiptOutcome.NotSent;
                ReceiptKey = null;
                ReceiptFirstSentUtc = null;
            }
            ReceiptKey ??= Guid.NewGuid().ToString();
            ReceiptOutcome = ReceivingReceiptOutcome.Unknown;
            ReceiptFirstSentUtc ??= DateTime.UtcNow;
            key = ReceiptKey;
        }
        RaiseChanged();
        return key;
    }

    internal void MarkPosted(string? id, double? total, DateTime? at)
    {
        lock (Gate)
        {
            ReceiptOutcome = ReceivingReceiptOutcome.Posted;
            ReceiptId = id;
            ReceiptTotal = total;
            ReceiptAt = at;
        }
        RaiseChanged();
    }

    /// <summary>Сервер отказал по существу: ключ забыт, повтор (если кассир нажмёт «Повторить») пойдёт с новым.</summary>
    internal void MarkRejected()
    {
        lock (Gate)
        {
            ReceiptOutcome = ReceivingReceiptOutcome.Rejected;
            ReceiptKey = null;
            ReceiptFirstSentUtc = null;
        }
        RaiseChanged();
    }

    // ------------------------------------------------------------------ строки

    internal bool TryGetPatch(ReceivingLineVm l, out StockPatchState patch)
    {
        lock (Gate)
        {
            var ok = StockPatches.TryGetValue(KeyOf(l), out var p);
            patch = p ?? new StockPatchState();
            return ok;
        }
    }

    internal void SetPatch(ReceivingLineVm l, double preStock, double target)
    {
        lock (Gate)
            StockPatches[KeyOf(l)] = new StockPatchState { PreStock = preStock, Target = target };
        RaiseChanged();
    }

    internal void RemovePatch(ReceivingLineVm l)
    {
        lock (Gate)
            StockPatches.Remove(KeyOf(l));
        RaiseChanged();
    }

    internal bool TryGetCreatedPrice(ReceivingLineVm l, out double price)
    {
        lock (Gate)
            return CreatedSalePrice.TryGetValue(KeyOf(l), out price);
    }

    internal void SetCreatedPrice(ReceivingLineVm l, double price)
    {
        lock (Gate)
            CreatedSalePrice[KeyOf(l)] = price;
        RaiseChanged();
    }

    internal bool TryGetPriceBefore(ReceivingLineVm l, out double price)
    {
        lock (Gate)
            return PriceBefore.TryGetValue(KeyOf(l), out price);
    }

    internal void SetPriceBefore(ReceivingLineVm l, double price)
    {
        lock (Gate)
            PriceBefore[KeyOf(l)] = price;
        RaiseChanged();
    }

    internal bool WasCreateAttempted(ReceivingLineVm l)
    {
        lock (Gate)
            return CreateAttempted.Contains(KeyOf(l));
    }

    internal void MarkCreateAttempt(ReceivingLineVm l)
    {
        lock (Gate)
            CreateAttempted.Add(KeyOf(l));
        RaiseChanged();
    }

    internal void SetOutcome(ReceivingLineVm l, string outcome)
    {
        lock (Gate)
            LineOutcome[KeyOf(l)] = outcome;
        RaiseChanged();
    }

    internal void MarkVariants(ReceivingLineVm l, bool done)
    {
        lock (Gate)
        {
            if (done)
                VariantsDone.Add(KeyOf(l));
            else
                VariantsStarted.Add(KeyOf(l));
        }
        RaiseChanged();
    }

    internal bool VariantsTouched(ReceivingLineVm l, out bool done)
    {
        lock (Gate)
        {
            var k = KeyOf(l);
            done = VariantsDone.Contains(k);
            return VariantsStarted.Contains(k) || done;
        }
    }

    /// <summary>Запомнить отказ сервера (для очереди: 429/401 попыток не тратят).</summary>
    internal void NoteFailure(Exception ex)
    {
        PosLogger.Log($"Приход в фоне: сбой запроса ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        lock (Gate)
            LastStatusCode = ex is ApiException { StatusCode: { } code } ? code : 0;
    }

    // ------------------------------------------------------------------ на диск

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson()
    {
        lock (Gate)
            return JsonSerializer.Serialize(this, Json);
    }

    public static ReceivingPostState FromJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ReceivingPostState();
        return JsonSerializer.Deserialize<ReceivingPostState>(text, Json) ?? new ReceivingPostState();
    }
}
