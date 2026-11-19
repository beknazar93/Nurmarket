namespace NurMarketKassa.Services.Fiscal;

/// <summary>2026-11-14: строка чека для ККМ. Деньги — decimal. <see cref="VatRate"/> — ставка НДС строки в процентах (null — ставка компании).</summary>
public sealed record FiscalLine(string Name, decimal Quantity, decimal Price, decimal Total, decimal? VatRate = null);

/// <summary>2026-11-14: часть оплаты. <see cref="Method"/>: cash | card | qr | transfer | debt (долг = постоплата) — уточнить по документации ККМ (Ф3).</summary>
public sealed record FiscalPayment(string Method, decimal Amount);

/// <summary>2026-11-14: запрос на пробитие продажи или возврата. <see cref="IdempotencyKey"/> — один на чек и на все повторы (защита от двойного чека: повтор после
/// таймаута должен вернуть тот же документ, а не создать второй — проверить по документации, Ф4). Для возврата — реквизиты исходного чека (требование закона).</summary>
public sealed record FiscalReceiptRequest(
    FiscalDocKind Kind,
    string IdempotencyKey,
    string DocId,
    string? ReceiptNumber,
    DateTimeOffset CreatedAt,
    IReadOnlyList<FiscalLine> Lines,
    IReadOnlyList<FiscalPayment> Payments,
    decimal Total,
    string? OriginalDocNumber = null,
    string? OriginalMemoryNumber = null,
    decimal? NspRate = null);

/// <summary>2026-11-14: итог обращения к ККМ: успех с реквизитами (контракт совпадает с сервером) или ошибка. <see cref="Retryable"/> — повтор имеет смысл
/// (нет связи, таймаут, ККМ занята), иначе чек уходит в «Ошибку» и ждёт решения человека.</summary>
public sealed record FiscalResult(bool Success, FiscalRequisites? Requisites, string? ErrorCode, string? ErrorText, bool Retryable)
{
    public static FiscalResult Ok(FiscalRequisites requisites) => new(true, requisites, null, null, false);

    public static FiscalResult Fail(string code, string text, bool retryable) => new(false, null, code, text, retryable);
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum FiscalShiftAction { Open, Close }

public enum FiscalReportKind { X, Z }

public sealed record FiscalShiftResult(bool Success, string? KkmShiftId, string? ErrorCode, string? ErrorText)
{
    public static FiscalShiftResult Fail(string code, string text) => new(false, null, code, text);
}

/// <summary>Отчёт ККМ (X/Z): <see cref="Text"/> — готовый текст для показа и печати, <see cref="Total"/> — итог для сверки с отчётом смены NurCRM.</summary>
public sealed record FiscalReportResult(bool Success, string? Text, decimal? Total, string? ErrorCode, string? ErrorText)
{
    public static FiscalReportResult Fail(string code, string text) => new(false, null, null, code, text);
}

/// <summary>2026-11-14 (подготовка к eKassa Telemedia Group, документации API у нас нет): абстракция онлайн-ККМ. Касса говорит только с этим интерфейсом;
/// реализацию eKassa подставят в <see cref="FiscalProviders.Current"/>, когда придут документация и тестовый доступ (список «что подставить» — docs/Отчеты/«Фискализация…»).
/// Провайдер НИКОГДА не пишет на сервер NurCRM — реквизиты на сервер пишет <see cref="FiscalApi"/> по результату.</summary>
public interface IFiscalProvider
{
    /// <summary>Название для показа («ЭККМ не подключена», «eKassa»).</summary>
    string Name { get; }

    /// <summary>ККМ подключена и настроена (адрес, логин, ККМ выбрана). false — очередь ничего не пробивает и ничего не считает ошибкой.</summary>
    bool IsConnected { get; }

    Task<FiscalResult> SaleAsync(FiscalReceiptRequest request, CancellationToken ct = default);

    Task<FiscalResult> ReturnAsync(FiscalReceiptRequest request, CancellationToken ct = default);

    Task<FiscalShiftResult> ShiftAsync(FiscalShiftAction action, CancellationToken ct = default);

    Task<FiscalReportResult> ReportAsync(FiscalReportKind kind, CancellationToken ct = default);
}

/// <summary>2026-11-14: заглушка «ЭККМ не подключена» — провайдер по умолчанию. Ничего не пробивает и в сеть не ходит.</summary>
public sealed class NotConnectedFiscalProvider : IFiscalProvider
{
    public const string ErrorCode = "not_connected";

    public static string NotConnectedText => Tr.T(
        "ЭККМ не подключена: ожидается документация Telemedia (eKassa).",
        "ЭККМ туташкан эмес: Telemedia (eKassa) документациясы күтүлүүдө.",
        "Online cash register is not connected: waiting for the Telemedia (eKassa) documentation.",
        "Çevrimiçi ÖKC bağlı değil: Telemedia (eKassa) belgeleri bekleniyor.",
        "Onlayn KKM ulanmagan: Telemedia (eKassa) hujjatlari kutilmoqda.");

    public string Name => Tr.T("ЭККМ не подключена", "ЭККМ туташкан эмес", "Online cash register not connected", "Çevrimiçi ÖKC bağlı değil", "Onlayn KKM ulanmagan");

    public bool IsConnected => false;

    public Task<FiscalResult> SaleAsync(FiscalReceiptRequest request, CancellationToken ct = default) =>
        Task.FromResult(FiscalResult.Fail(ErrorCode, NotConnectedText, retryable: true));

    public Task<FiscalResult> ReturnAsync(FiscalReceiptRequest request, CancellationToken ct = default) =>
        Task.FromResult(FiscalResult.Fail(ErrorCode, NotConnectedText, retryable: true));

    public Task<FiscalShiftResult> ShiftAsync(FiscalShiftAction action, CancellationToken ct = default) =>
        Task.FromResult(FiscalShiftResult.Fail(ErrorCode, NotConnectedText));

    public Task<FiscalReportResult> ReportAsync(FiscalReportKind kind, CancellationToken ct = default) =>
        Task.FromResult(FiscalReportResult.Fail(ErrorCode, NotConnectedText));
}

/// <summary>2026-11-14: текущий провайдер ККМ. По умолчанию — заглушка «ЭККМ не подключена». Реализацию eKassa подставляют один раз при запуске (Ф1).</summary>
public static class FiscalProviders
{
    private static volatile IFiscalProvider _current = new NotConnectedFiscalProvider();

    public static IFiscalProvider Current
    {
        get => _current;
        set => _current = value ?? new NotConnectedFiscalProvider();
    }

    public static void ResetForTests() => _current = new NotConnectedFiscalProvider();
}
