using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

// 2026-10-25, ТЗ «оприходование в фоне» (Q1): модель задачи очереди фоновых приходов. Хранится в JSON-файле
// (%APPDATA%\<NurMarketKassa|NurMarketOwner>\background-receipts\queue.json), схема SQLite не затронута.

/// <summary>Статусы задачи. Активные (queued, sending, retry_wait, finishing) держат очередь; done/partial/failed/needs_check — итог.</summary>
public static class ReceivingJobStatus
{
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string RetryWait = "retry_wait";
    public const string Finishing = "finishing";
    public const string Done = "done";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string NeedsCheck = "needs_check";

    public static bool IsActive(string? s) => s is Queued or Sending or RetryWait or Finishing;
    public static bool IsFinal(string? s) => s is Done or Partial or Failed or NeedsCheck;
}

/// <summary>Исход строки: pending — ещё не отправлена; posted — принята сервером; rejected — отказ сервера по существу (4xx);
/// unknown — ответ не дошёл (повторим); check — нужна проверка кассиром (автоматически не повторяется).</summary>
public static class ReceivingLineOutcome
{
    public const string Pending = "pending";
    public const string Posted = "posted";
    public const string Rejected = "rejected";
    public const string Unknown = "unknown";
    public const string Check = "check";
}

/// <summary>Откуда пришла задача.</summary>
public static class ReceivingJobSource
{
    public const string Warehouse = "warehouse";
    public const string Invoice = "invoice";
    public const string Purchases = "purchases";
    /// <summary>2026-10-26 (ТЗ «Склад — массовое добавление», М1): часть большой пачки от BulkReceivingFeeder.</summary>
    public const string Bulk = "bulk";
}

public sealed class JobSupplier
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Inn { get; set; }
    public string? Phone { get; set; }

    public static JobSupplier? From(PurchaseReceivingService.Supplier? s) =>
        s is null ? null : new JobSupplier { Id = s.Id, Name = s.Name, Inn = s.Inn, Phone = s.Phone };

    public PurchaseReceivingService.Supplier ToSupplier() => new(Id, Name, Inn, Phone);
}

/// <summary>Снимок строки приёмки (копия значений ReceivingLineVm — окна передают в очередь копию, а не свои объекты).</summary>
public sealed class JobLine
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string? ProductId { get; set; }
    public string Barcode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Unit { get; set; } = "шт";
    [JsonConverter(typeof(JsonStringEnumConverter))] public ReceivingSource Source { get; set; }
    public double StockBefore { get; set; }
    public double Quantity { get; set; }
    public double PurchasePrice { get; set; }
    public double SalePrice { get; set; }
    public string OriginalName { get; set; } = "";
    public string OriginalUnit { get; set; } = "";
    public double OriginalPurchasePrice { get; set; }
    public double OriginalSalePrice { get; set; }
    public double OriginalMarkupPercent { get; set; }
    public string? CategoryName { get; set; }
    public bool IsWeight { get; set; }
    public Dictionary<string, double>? VariantQuantities { get; set; }
    public Dictionary<string, string>? VariantLabels { get; set; }
    public string Outcome { get; set; } = ReceivingLineOutcome.Pending;
    public bool StockApplied { get; set; }
    public string? Error { get; set; }
    /// <summary>2026-10-26 (М1): номер строки в файле импорта (для «Скачать строки с ошибками»); у обычных приходов null.</summary>
    public int? FileRow { get; set; }

    [JsonIgnore] public bool IsNew => Source != ReceivingSource.Warehouse;
    // 2026-10-28: сумма строки с округлением до копеек по итогу — как в таблице приёмки (ReceivingPricing.LineTotal)
    [JsonIgnore] public double LineTotal => ReceivingPricing.LineTotal(Quantity, PurchasePrice);

    public static JobLine From(ReceivingLineVm l) => new()
    {
        ProductId = l.ProductId,
        Barcode = l.Barcode,
        ProductName = l.ProductName,
        Unit = l.Unit,
        Source = l.Source,
        StockBefore = l.StockBefore,
        Quantity = l.Quantity,
        PurchasePrice = l.PurchasePrice,
        SalePrice = l.SalePrice,
        OriginalName = l.OriginalName,
        OriginalUnit = l.OriginalUnit,
        OriginalPurchasePrice = l.OriginalPurchasePrice,
        OriginalSalePrice = l.OriginalSalePrice,
        OriginalMarkupPercent = l.OriginalMarkupPercent,
        CategoryName = l.CategoryName,
        IsWeight = l.IsWeight,
        VariantQuantities = l.VariantQuantities is { Count: > 0 } vq ? new Dictionary<string, double>(vq) : null,
        VariantLabels = l.VariantLabels is { Count: > 0 } vl ? new Dictionary<string, string>(vl) : null,
    };

    public ReceivingLineVm ToVm()
    {
        var vm = new ReceivingLineVm
        {
            StockBefore = StockBefore,
            OriginalName = OriginalName,
            OriginalUnit = OriginalUnit,
            OriginalPurchasePrice = OriginalPurchasePrice,
            OriginalSalePrice = OriginalSalePrice,
            OriginalMarkupPercent = OriginalMarkupPercent,
        };
        vm.ProductId = ProductId;
        vm.Barcode = Barcode;
        vm.ProductName = ProductName;
        vm.Unit = Unit;
        vm.Source = Source;
        vm.CategoryName = CategoryName;
        vm.IsWeight = IsWeight;
        if (VariantQuantities is { Count: > 0 })
            vm.SetVariants(new Dictionary<string, double>(VariantQuantities), VariantLabels is null ? new Dictionary<string, string>() : new Dictionary<string, string>(VariantLabels));
        vm.Quantity = Quantity;
        vm.PurchasePrice = PurchasePrice;
        vm.SalePrice = SalePrice;
        return vm;
    }
}

/// <summary>Один приход в очереди.</summary>
public sealed class ReceivingJob
{
    public string Id { get; set; } = "";
    public string CompanyId { get; set; } = "";
    /// <summary>warehouse | invoice | purchases (<see cref="ReceivingJobSource"/>).</summary>
    public string Source { get; set; } = ReceivingJobSource.Warehouse;
    public DateTime CreatedAt { get; set; }
    public string? Employee { get; set; }
    public JobSupplier? Supplier { get; set; }
    public bool PaidNow { get; set; }
    public Dictionary<string, string>? PaymentFields { get; set; }
    public int DebtDays { get; set; } = 30;
    /// <summary>Имя финишера (шаги после прихода, регистрирует окно: «purchases») и его данные (JSON) — для пакета Q4.</summary>
    public string? Finisher { get; set; }
    public string? FinisherPayload { get; set; }
    public List<JobLine> Lines { get; set; } = new();

    public string Status { get; set; } = ReceivingJobStatus.Queued;
    /// <summary>Сколько автоматических попыток уже не удалось (повторы 5 с … 5 мин; 429, авария и нет входа попыток не тратят).</summary>
    public int Attempts { get; set; }
    /// <summary>UTC. Для retry_wait — когда пробовать снова.</summary>
    public DateTime? NextAttemptAt { get; set; }
    /// <summary>retry_wait: ждём связь с сервером (авария/нет входа), а не расписание повторов.</summary>
    public bool WaitingForServer { get; set; }
    public string? LastError { get; set; }
    /// <summary>Тексты сервиса приёма (уже на языке кассира) — для «Подробнее».</summary>
    public List<string> Errors { get; set; } = new();

    public string? ReceiptId { get; set; }
    public double? ReceiptTotal { get; set; }
    public DateTime? ReceiptAt { get; set; }
    public int Created { get; set; }
    public bool DebtDone { get; set; }
    public bool FinisherDone { get; set; }
    public string? Summary { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>Подпись состава (поставщик + строки) — защита от двойного нажатия.</summary>
    public string Signature { get; set; } = "";

    // 2026-10-26 (ТЗ «Склад — массовое добавление», М1): подача пачки частями. Все поля необязательные — старый queue.json читается как раньше.
    /// <summary>Общий номер пачки (BulkReceivingFeeder) и номер этой части в ней; у обычных приходов null.</summary>
    public string? BatchId { get; set; }
    public int? BatchPart { get; set; }
    /// <summary>Имя крючка «подготовка» (RegisterPreparer): воркер вызывает его до PostAsync. null — без подготовки.</summary>
    public string? Preparer { get; set; }
    /// <summary>Готовая задача bulk хранится сжатой: строки сброшены, остались счётчики (queue.json не раздувается).</summary>
    public int CompactedLineCount { get; set; }
    public int CompactedPostedCount { get; set; }
    public double CompactedTotal { get; set; }

    [JsonIgnore] public ReceivingPostState State { get; set; } = new();

    /// <summary>Состояние повторов (<see cref="ReceivingPostState"/>) одной строкой JSON — для файла очереди.</summary>
    public string? StateData
    {
        get => State.ToJson();
        set => State = ReceivingPostState.FromJson(value);
    }

    // ------------------------------------------------------------------ для окон (в файл не пишется)

    [JsonIgnore] public bool IsActive => ReceivingJobStatus.IsActive(Status);
    [JsonIgnore] public bool IsFinal => ReceivingJobStatus.IsFinal(Status);
    // 2026-10-26 (М1): у сжатой готовой задачи bulk строк нет — берём счётчики.
    [JsonIgnore] public int LineCount => Lines.Count > 0 ? Lines.Count : CompactedLineCount;
    [JsonIgnore] public int PostedCount => Lines.Count > 0 ? Lines.Count(l => l.Outcome == ReceivingLineOutcome.Posted) : CompactedPostedCount;
    [JsonIgnore] public double Total => Lines.Count > 0 ? Lines.Sum(l => l.LineTotal) : CompactedTotal;
    /// <summary>Задача другой компании — ждёт, пока в неё войдут (проставляет Snapshot).</summary>
    [JsonIgnore] public bool ForeignCompany { get; set; }
    /// <summary>Есть замечания (цены/размеры/долг) при том, что приход проведён.</summary>
    [JsonIgnore] public bool HasWarnings => Errors.Count > 0;

    /// <summary>Откуда: «Склад» / «Накладная» / «Закупки».</summary>
    [JsonIgnore]
    public string SourceText => Source switch
    {
        ReceivingJobSource.Invoice => Tr.T("Накладная", "Накладная", "Invoice", "İrsaliye", "Yuk xati"),
        ReceivingJobSource.Purchases => Tr.T("Закупки", "Сатып алуулар", "Purchases", "Alımlar", "Xaridlar"),
        // 2026-10-26 (М1): те же слова, что у вкладки склада «Массовое добавление».
        ReceivingJobSource.Bulk => Tr.T("Массовое добавление", "Массалык кошуу", "Bulk add", "Toplu ekleme", "Ommaviy qo'shish"),
        _ => Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"),
    };

    /// <summary>Статус для строки списка «Приходы в фоне» (раздел 3 п. 6 ТЗ).</summary>
    [JsonIgnore]
    public string StatusText
    {
        get
        {
            if (ForeignCompany && !IsFinal)
                return Tr.T("другая компания — войдите в неё", "башка компания — ага кириңиз", "another company — sign in to it", "başka şirket — ona giriş yapın", "boshqa kompaniya — unga kiring");
            switch (Status)
            {
                case ReceivingJobStatus.Queued:
                    return Tr.T("в очереди", "кезекте", "queued", "sırada", "navbatda");
                case ReceivingJobStatus.Sending:
                    return Tr.T("отправляется…", "жөнөтүлүүдө…", "sending…", "gönderiliyor…", "yuborilmoqda…");
                case ReceivingJobStatus.Finishing:
                    return Tr.T("завершается…", "аякталууда…", "finishing…", "tamamlanıyor…", "yakunlanmoqda…");
                case ReceivingJobStatus.RetryWait when WaitingForServer:
                    return Tr.T("ждёт связь с сервером", "сервер менен байланышты күтүүдө", "waiting for the server connection", "sunucu bağlantısı bekleniyor", "server aloqasi kutilmoqda");
                case ReceivingJobStatus.RetryWait:
                    var left = NextAttemptAt is { } at ? Math.Max(0, (int)Math.Ceiling((at - DateTime.UtcNow).TotalSeconds)) : 0;
                    var n = Math.Min(Attempts + 1, BackgroundReceivingQueue.MaxRetries);
                    return Tr.T($"повтор через {left} с (попытка {n} из {BackgroundReceivingQueue.MaxRetries})",
                        $"{left} с кийин кайталоо ({n}/{BackgroundReceivingQueue.MaxRetries}-аракет)",
                        $"retry in {left} s (attempt {n} of {BackgroundReceivingQueue.MaxRetries})",
                        $"{left} sn sonra tekrar (deneme {n}/{BackgroundReceivingQueue.MaxRetries})",
                        $"{left} soniyadan keyin qayta urinish ({n}/{BackgroundReceivingQueue.MaxRetries}-urinish)");
                case ReceivingJobStatus.Done:
                    var at2 = (FinishedAt ?? CreatedAt).ToString("HH:mm", CultureInfo.InvariantCulture);
                    return Tr.T($"оприходован {at2}", $"кабыл алынды {at2}", $"received {at2}", $"kabul edildi {at2}", $"qabul qilindi {at2}");
                case ReceivingJobStatus.Partial:
                    return Tr.T($"частично: принято {PostedCount} из {LineCount}", $"жарым-жартылай: {PostedCount}/{LineCount} кабыл алынды",
                        $"partial: {PostedCount} of {LineCount} received", $"kısmen: {PostedCount}/{LineCount} kabul edildi", $"qisman: {LineCount} tadan {PostedCount} tasi qabul qilindi");
                case ReceivingJobStatus.NeedsCheck:
                    return Tr.T("нужна проверка: ", "текшерүү керек: ", "needs checking: ", "kontrol gerekli: ", "tekshirish kerak: ") + (LastError ?? "");
                default:
                    return Tr.T("ошибка: ", "ката: ", "error: ", "hata: ", "xato: ") + (LastError ?? "");
            }
        }
    }

    public ReceivingJob Clone()
    {
        var copy = JsonSerializer.Deserialize<ReceivingJob>(JsonSerializer.Serialize(this))!;
        return copy;
    }
}

/// <summary>Что принять в очередь. Lines — копируются (окно может сразу очистить свой список).</summary>
public sealed record ReceivingJobRequest(
    string Source,
    IReadOnlyList<ReceivingLineVm> Lines,
    PurchaseReceivingService.Supplier? Supplier,
    bool PaidNow,
    string? Employee,
    int DebtDays = 30,
    IReadOnlyDictionary<string, string>? PaymentFields = null,
    string? Finisher = null,
    string? FinisherPayload = null,
    // 2026-10-26 (М1): подача пачки частями — номер пачки/части, крючок подготовки, номера строк файла (параллельно Lines).
    string? BatchId = null,
    int? BatchPart = null,
    string? Preparer = null,
    IReadOnlyList<int?>? FileRows = null);

/// <summary>Итог шага после прихода, который выполняет окно-источник (пакет Q4: деньги закупки). Done=false — повторить позже.</summary>
public sealed record FinisherResult(bool Done, string? Text = null, IReadOnlyList<string>? Errors = null);
