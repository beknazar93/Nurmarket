using System.Text.Json;

namespace NurMarketKassa.Core.Contracts;

/// <summary>
/// Контракт завершения продажи: онлайн-оплата, офлайн-сохранение, печать чека и новый чек.
/// </summary>
public interface IPosCheckoutService
{
    Task<PosCheckoutResult> CheckoutAsync(PosCheckoutRequest request, CancellationToken cancellationToken = default);

    Task<bool> ApplyOrderDiscountAsync(
        Dictionary<string, string> discountBody,
        CancellationToken cancellationToken = default);

    Task PrepareCartForCheckoutAsync(CancellationToken cancellationToken = default);

    Task<string?> RestartSaleSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>Параметры оплаты из диалога Checkout.</summary>
public sealed class PosCheckoutRequest
{
    public required string PaymentMethod { get; init; }
    public required string CashReceived { get; init; }
    public required bool PrintReceipt { get; init; }
    public Dictionary<string, string>? OrderDiscountBody { get; init; }
    /// <summary>UUID клиента из клиентской базы — обязателен для продажи «в долг».</summary>
    public string? ClientId { get; init; }
    /// <summary>Безналичная часть смешанной оплаты (PaymentMethod == "mixed") — CashReceived тогда несёт наличную часть.</summary>
    public string? NonCashReceived { get; init; }
    /// <summary>Консультант продажи (сфера «Одежда», 2026-09-25) — id пользователя-сотрудника;
    /// null — без консультанта. Поля те же, что отправляет сайт.</summary>
    public string? ConsultantId { get; init; }
    public bool ConsultantCommissionEnabled { get; init; }
    /// <summary>Процент консультанта строкой «0.00» (0–100).</summary>
    public string? ConsultantCommissionPercent { get; init; }
    /// <summary>Имя консультанта — только для строки «Консультант» в печатном чеке.</summary>
    public string? ConsultantName { get; init; }
    /// <summary>2026-09-28, продажа №1136: итог, который кассир видел в окне оплаты и по которому
    /// взял деньги. Перед отправкой на сервер с ним сверяется итог запроса (быстрый путь) или
    /// серверной корзины (старый путь): расхождение больше 0,01 — продажа молча не уходит.
    /// null — сверки нет (старые вызовы).</summary>
    public double? ExpectedTotal { get; init; }
    /// <summary>2026-10-06: график долга (оплата «В долг») — одним платежом на дату, названную клиентом, или рассрочка
    /// по дням / месяцам, как «Отсрочка» на сайте NurCRM. На сервер (DebtDueDateSync) и в чек.</summary>
    public DebtSchedulePlan? DebtSchedule { get; init; }
}

/// <summary>2026-10-06: график погашения долга. Unit — "day" или "month"; Count — число платежей; Interval — через сколько
/// дней / месяцев следующий платёж. Одним платежом — Count = 1, срок — FirstDueDate.</summary>
public sealed record DebtSchedulePlan(string Unit, int Count, int Interval, DateTime FirstDueDate, IReadOnlyList<DebtSchedulePayment> Payments)
{
    public bool IsMonths => string.Equals(Unit, "month", StringComparison.OrdinalIgnoreCase);
    public DateTime LastDueDate => Payments.Count > 0 ? Payments[^1].DueDate : FirstDueDate;
}

public sealed record DebtSchedulePayment(int Number, DateTime DueDate, decimal Amount);

/// <summary>Результат оплаты для UI.</summary>
public sealed class PosCheckoutResult
{
    public bool IsSuccess { get; init; }
    public bool SavedOffline { get; init; }
    public string? ErrorMessage { get; init; }
    public string? InfoMessage { get; init; }
    public double TotalAmount { get; init; }
    public JsonElement? CheckoutResponse { get; init; }
    public string? CartJsonSnapshot { get; init; }
    public bool ReceiptPrintAttempted { get; init; }
    public bool ReceiptPrinted { get; init; }

    /// <summary>2026-10-04, отчёт о производительности (п. 9): печать чека идёт в фоне уже после сброса
    /// чека — касса готова к следующему покупателю, не дожидаясь принтера (на ПК с принтером в ошибке —
    /// 1,6 с на каждой продаже). Результат печати: false — чек не напечатан, кассиру показывается то же
    /// «чек не напечатан; используйте повторную печать». null — печать не запрашивалась (или прошла до
    /// возврата, тогда смотрите <see cref="ReceiptPrinted"/>).</summary>
    public Task<bool>? ReceiptPrintTask { get; init; }
    /// <summary>2026-09-28, продажа №1136: сервер провёл продажу на сумму, отличную от итога окна
    /// оплаты (например, акцию товара поменяли на сайте в последние минуты). Продажа уже есть —
    /// кассиру показывается это сообщение с разницей, чтобы вернуть или добрать деньги.</summary>
    public string? TotalMismatchWarning { get; set; }

    public static PosCheckoutResult Succeeded(
        double total,
        string? cartJson,
        JsonElement? response = null,
        string? info = null,
        bool receiptPrintAttempted = false,
        bool receiptPrinted = false,
        Task<bool>? receiptPrintTask = null) =>
        new()
        {
            IsSuccess = true,
            TotalAmount = total,
            CartJsonSnapshot = cartJson,
            CheckoutResponse = response,
            InfoMessage = info,
            ReceiptPrintAttempted = receiptPrintAttempted,
            ReceiptPrinted = receiptPrinted,
            ReceiptPrintTask = receiptPrintTask,
        };

    public static PosCheckoutResult OfflineSaved(
        double total,
        string cartJson,
        string? info = null,
        bool receiptPrintAttempted = false,
        bool receiptPrinted = false,
        Task<bool>? receiptPrintTask = null) =>
        new()
        {
            IsSuccess = true,
            SavedOffline = true,
            TotalAmount = total,
            CartJsonSnapshot = cartJson,
            InfoMessage = info,
            ReceiptPrintAttempted = receiptPrintAttempted,
            ReceiptPrinted = receiptPrinted,
            ReceiptPrintTask = receiptPrintTask,
        };

    public static PosCheckoutResult Failed(string error) =>
        new() { IsSuccess = false, ErrorMessage = error };
}
