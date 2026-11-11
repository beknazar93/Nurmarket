using System.Globalization;
using System.Linq;
using NurMarketKassa.Core.Application;

namespace NurMarketKassa.Services;

/// <summary>2026-11-10 (владелец: «и фильтр в возврате!», «функцию возврата добавь в продажи»): общий фильтр списков чеков — окно возврата
/// (ReturnSaleDialog) и «Продажи» (SalesWindow) берут ОДИН этот код. Период, сумма от–до, способ оплаты, статус чека, кассир, клиент и товар
/// работают вместе; «Сбросить» — <see cref="Reset"/>. Чистая логика без окон — проверяется тестами.</summary>
public sealed class SaleListFilter
{
    public enum PeriodKind { All, Today, Week, Month, Date }
    public enum PaymentKind { All, Cash, Card, Qr, Debt, Mixed }
    public enum StatusKind { All, Normal, Debt, PartlyReturned, Returned }

    public PeriodKind Period { get; set; } = PeriodKind.All;
    /// <summary>День для <see cref="PeriodKind.Date"/>.</summary>
    public DateTime? Date { get; set; }
    public decimal? AmountFrom { get; set; }
    public decimal? AmountTo { get; set; }
    public PaymentKind Payment { get; set; } = PaymentKind.All;
    public StatusKind Status { get; set; } = StatusKind.All;
    /// <summary>Имя кассира (точно, без регистра); пусто — все.</summary>
    public string? Cashier { get; set; }
    /// <summary>Клиент: часть имени или телефона; пусто — все.</summary>
    public string? Client { get; set; }
    /// <summary>Товар: название, штрихкод или артикул (см. <see cref="SaleProductQuery"/>); пусто — все.</summary>
    public string? Product { get; set; }

    /// <summary>Сколько фильтров задано (для подписи «Фильтры (N)»). Товар не считается — у него своё поле.</summary>
    public int ActiveCount =>
        (Period != PeriodKind.All ? 1 : 0) + (AmountFrom is not null || AmountTo is not null ? 1 : 0) + (Payment != PaymentKind.All ? 1 : 0)
        + (Status != StatusKind.All ? 1 : 0) + (string.IsNullOrWhiteSpace(Cashier) ? 0 : 1) + (string.IsNullOrWhiteSpace(Client) ? 0 : 1);

    public bool IsActive => ActiveCount > 0 || !string.IsNullOrWhiteSpace(Product);

    public void Reset()
    {
        Period = PeriodKind.All;
        Date = null;
        AmountFrom = AmountTo = null;
        Payment = PaymentKind.All;
        Status = StatusKind.All;
        Cashier = Client = Product = null;
    }

    /// <summary>Период [from; toExclusive) на дату <paramref name="now"/>; null — без ограничения. «Неделя» и «Месяц» — последние 7 и 30 дней включая сегодня.</summary>
    public (DateTime From, DateTime ToExclusive)? PeriodRange(DateTime now)
    {
        var today = now.Date;
        return Period switch
        {
            PeriodKind.Today => (today, today.AddDays(1)),
            PeriodKind.Week => (today.AddDays(-6), today.AddDays(1)),
            PeriodKind.Month => (today.AddDays(-29), today.AddDays(1)),
            PeriodKind.Date when Date is { } d => (d.Date, d.Date.AddDays(1)),
            _ => null,
        };
    }

    /// <summary>Строка списка подходит под все фильтры (кроме товара — его проверяет вызывающий через <see cref="SaleProductQuery"/>, у него свой источник строк чека).</summary>
    public bool Matches(Row row, DateTime now)
    {
        if (PeriodRange(now) is { } range && (row.Date == DateTime.MinValue || row.Date < range.From || row.Date >= range.ToExclusive))
            return false;
        if (AmountFrom is { } min && row.Total < min - 0.004m)
            return false;
        if (AmountTo is { } max && row.Total > max + 0.004m)
            return false;
        if (Payment != PaymentKind.All && PaymentOf(row.PaymentMethod) != Payment)
            return false;
        if (Status != StatusKind.All && !StatusMatches(row))
            return false;
        if (!string.IsNullOrWhiteSpace(Cashier) && !string.Equals((row.Cashier ?? "").Trim(), Cashier.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(Client) && !SearchMatch.Matches(Client, row.ClientName, row.ClientPhone))
            return false;
        return true;
    }

    private bool StatusMatches(Row row)
    {
        var returnState = ReturnStateOf(row.Status, row.ReturnStatus);
        return Status switch
        {
            StatusKind.Returned => returnState == ReturnState.Full,
            StatusKind.PartlyReturned => returnState == ReturnState.Partial,
            StatusKind.Debt => returnState != ReturnState.Full && IsDebtSale(row),
            StatusKind.Normal => returnState == ReturnState.None && !IsDebtSale(row),
            _ => true,
        };
    }

    /// <summary>Долговой чек: способ оплаты «долг» или статус «debt» (сервер так помечает неоплаченную продажу в долг).</summary>
    public static bool IsDebtSale(Row row) =>
        PaymentOf(row.PaymentMethod) == PaymentKind.Debt
        || string.Equals((row.Status ?? "").Trim(), "debt", StringComparison.OrdinalIgnoreCase);

    public enum ReturnState { None, Partial, Full }

    /// <summary>Возвращён ли чек: по return_status (none/partial/full, ответ Умара 01.11) и по status (canceled/returned — целиком, partially_returned — частично).</summary>
    public static ReturnState ReturnStateOf(string? status, string? returnStatus)
    {
        var rs = (returnStatus ?? "").Trim().ToLowerInvariant();
        if (CartDisplayHelper.IsFullyReturnedStatus(rs))
            return ReturnState.Full;
        if (rs is "partial" or "partially_returned")
            return ReturnState.Partial;
        var st = (status ?? "").Trim().ToLowerInvariant();
        if (st is "returned" or "refunded" or "canceled" or "cancelled")
            return ReturnState.Full;
        if (st is "partially_returned" or "partial_return")
            return ReturnState.Partial;
        return ReturnState.None;
    }

    /// <summary>Способ оплаты по ключу сервера (cash, card, transfer, mbank, qr, debt, mixed…).</summary>
    public static PaymentKind PaymentOf(string? method)
    {
        var key = (method ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "cash" => PaymentKind.Cash,
            "card" or "transfer" or "noncash" or "non_cash" or "cashless" or "pos" or "terminal" => PaymentKind.Card,
            "qr" or "mbank" or "elsom" or "odengi" or "o!dengi" or "balance" or "megapay" or "optima" => PaymentKind.Qr,
            "debt" or "credit" => PaymentKind.Debt,
            "mixed" or "split" => PaymentKind.Mixed,
            _ when key.Contains("qr", StringComparison.Ordinal) => PaymentKind.Qr,
            _ when key.Contains("нал", StringComparison.Ordinal) => PaymentKind.Cash,
            _ => PaymentKind.All, // неизвестный способ — ни под один фильтр способа не попадает
        };
    }

    /// <summary>Данные строки списка, нужные фильтру.</summary>
    public sealed record Row(
        DateTime Date,
        decimal Total,
        string? PaymentMethod,
        string? Status,
        string? ReturnStatus,
        string? Cashier,
        string? ClientName,
        string? ClientPhone = null);

    /// <summary>Сумма из поля ввода («1 500», «1500,50»); пусто или не число — null.</summary>
    public static decimal? ParseAmount(string? text)
    {
        var t = (text ?? "").Replace(" ", "").Replace(" ", "").Replace(',', '.').Trim();
        return t.Length > 0 && decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
    }
}

/// <summary>2026-11-10 (владелец: «поиск по товару!»): поиск чеков по товару — название (слова в любом порядке, как общий поиск SearchMatch),
/// штрихкод или артикул/код (точное совпадение по каталогу кассы: из кода берутся id и название товара). Строки чека дают вызывающие:
/// список (first_item_name), локальная история продаж кассы, карточки чеков (кэш).</summary>
public sealed class SaleProductQuery
{
    private readonly string _query;
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public SaleProductQuery(string? query, IEnumerable<(string? Id, string? Name, IEnumerable<string?> Codes)>? catalog = null)
    {
        _query = (query ?? "").Trim();
        if (_query.Length == 0 || catalog is null)
            return;
        foreach (var (id, name, codes) in catalog)
        {
            if (!codes.Any(c => !string.IsNullOrWhiteSpace(c) && CodeMatches(c!, _query)))
                continue;
            if (!string.IsNullOrWhiteSpace(id))
                _ids.Add(id.Trim());
            if (!string.IsNullOrWhiteSpace(name))
                _names.Add(SearchMatch.Normalize(name));
        }
    }

    public bool IsEmpty => _query.Length == 0;
    public string Text => _query;

    /// <summary>Строка чека подходит: название содержит слова запроса, либо это товар, найденный по штрихкоду/артикулу.</summary>
    public bool MatchesLine(string? name, string? productId = null)
    {
        if (IsEmpty)
            return false;
        if (!string.IsNullOrWhiteSpace(productId) && _ids.Contains(productId.Trim()))
            return true;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return _names.Contains(SearchMatch.Normalize(name)) || SearchMatch.Matches(_query, name);
    }

    /// <summary>Состав чека одной строкой («Хлеб, Молоко ×2…», как колонка «Чек» в «Продажах»): слова запроса или название товара, найденного по коду.</summary>
    public bool MatchesText(string? text)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(text))
            return false;
        if (SearchMatch.Matches(_query, text))
            return true;
        var normalized = SearchMatch.Normalize(text);
        return _names.Any(n => n.Length > 0 && normalized.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>Первая подходящая строка из списка (название и id) — для подсветки; null — нет.</summary>
    public string? FirstMatch(IEnumerable<(string? Name, string? ProductId)> lines)
    {
        foreach (var (name, pid) in lines)
            if (MatchesLine(name, pid))
                return string.IsNullOrWhiteSpace(name) ? _query : name;
        return null;
    }

    /// <summary>Штрихкод/артикул: точно (без регистра) или список кодов через запятую/пробел (доп. штрихкоды).</summary>
    private static bool CodeMatches(string codes, string query) =>
        codes.Split(new[] { ',', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(c => string.Equals(c, query, StringComparison.OrdinalIgnoreCase));
}
