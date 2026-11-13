using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13: локальное хранилище квитанций «Ремонта и сервиса». Сервер ремонт пока не умеет (features.repair = false), поэтому данные — файл
/// repair-orders.json в %LOCALAPPDATA%\NurMarketKassa (схема pos_local.db не меняется), общий для кассы и программы владельца на одном ПК; запись — под
/// межпроцессным замком AtomicFile.Update; файл — данные компании (CompanyStoreRegistry). Между компьютерами НЕ синхронизируется (до серверного ТЗ).
/// Деньги стор не трогает: предоплата, возврат и оплата работ проводятся кассой существующими путями, здесь только записываются суммы.</summary>
public sealed class RepairOrderStore
{
    public const string FileName = "repair-orders.json";
    private const int MaxLog = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTime> _now;

    public RepairOrderStore(string path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
    }

    public static RepairOrderStore Default => new(Path.Combine(AppPaths.KassaLocal, FileName));

    public RepairData Load() => Parse(AtomicFile.ReadAllText(_path));

    private static RepairData Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new RepairData();
        try { return JsonSerializer.Deserialize<RepairData>(text, Json) ?? new RepairData(); }
        catch (JsonException) { return new RepairData(); }
    }

    private void Mutate(Func<RepairData, bool> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Update(_path, text =>
        {
            var data = Parse(text);
            return change(data) ? JsonSerializer.Serialize(data, Json) : null;
        });
    }

    public List<RepairOrder> All() => Load().Orders.OrderByDescending(o => o.Number).ToList();

    public RepairOrder? Find(string id) => Load().Orders.FirstOrDefault(o => o.Id == id);

    /// <summary>Новая квитанция: присваивает номер и время, статус «Принят». Возвращает сохранённую копию.</summary>
    public RepairOrder Create(RepairOrder draft, string by)
    {
        RepairOrder? saved = null;
        Mutate(d =>
        {
            d.LastNumber = Math.Max(d.LastNumber, d.Orders.Count == 0 ? 0 : d.Orders.Max(o => o.Number)) + 1;
            draft.Number = d.LastNumber;
            draft.AcceptedAt = _now();
            draft.AcceptedBy = by ?? "";
            draft.Status = RepairStatus.Accepted;
            draft.Serial = ImeiValidator.Normalize(draft.Serial);
            draft.Estimate = RepairRules.Money(draft.Estimate);
            draft.Prepay = RepairRules.Money(draft.Prepay);
            draft.Log.Add(new RepairLogEntry { At = draft.AcceptedAt, By = by ?? "", Action = "accept" });
            d.Orders.Add(draft);
            saved = draft;
            return true;
        });
        return saved!;
    }

    /// <summary>Правка квитанции под замком. false — квитанции нет или правка вернула false (ничего не меняли).</summary>
    public bool Update(string id, Func<RepairOrder, bool> change, string by, string action)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || !change(o))
                return false;
            o.Log.Add(new RepairLogEntry { At = _now(), By = by ?? "", Action = action });
            if (o.Log.Count > MaxLog)
                o.Log.RemoveRange(0, o.Log.Count - MaxLog);
            done = true;
            return true;
        });
        return done;
    }

    /// <summary>Перевод статуса «принят → в работе → готов» (итог работ задаётся при «Готов»). Выдача и отмена — отдельные методы (с деньгами).</summary>
    public bool Move(string id, RepairStatus to, string by, decimal? finalPrice = null)
    {
        if (to is RepairStatus.Issued or RepairStatus.Cancelled)
            return false;
        return Update(id, o =>
        {
            if (!RepairRules.CanMove(o.Status, to))
                return false;
            if (to == RepairStatus.Ready)
            {
                o.FinalPrice = RepairRules.Money(finalPrice ?? (o.FinalPrice > 0 ? o.FinalPrice : o.Estimate));
                o.ReadyAt = _now();
            }
            o.Status = to;
            return true;
        }, by, "to-" + to.ToString().ToLowerInvariant());
    }

    /// <summary>Итог работ можно поправить, пока квитанция «Готов» и оплата ещё не ушла в чек.</summary>
    public bool SetFinalPrice(string id, decimal finalPrice, string by) => Update(id, o =>
    {
        if (o.Status != RepairStatus.Ready || o.PaymentInCart > 0 || finalPrice < 0)
            return false;
        o.FinalPrice = RepairRules.Money(finalPrice);
        return true;
    }, by, "final-price");

    /// <summary>Строка оплаты работ добавлена в чек кассы (ждёт «чек оплачен»).</summary>
    public bool MarkPaymentInCart(string id, decimal amount, string by) => Update(id, o =>
    {
        if (o.Status != RepairStatus.Ready || amount <= 0)
            return false;
        o.PaymentInCart = RepairRules.Money(amount);
        return true;
    }, by, "payment-in-cart");

    /// <summary>Строку убрали из чека / чек не оплатили — снова можно добавить оплату.</summary>
    public bool CancelPaymentInCart(string id, string by) => Update(id, o =>
    {
        if (o.PaymentInCart <= 0)
            return false;
        o.PaymentInCart = 0;
        return true;
    }, by, "payment-cancelled");

    /// <summary>Запись возврата клиенту из кассы (отмена или переплата). Сумму проводит касса изъятием, здесь только запись.</summary>
    public bool RecordRefund(string id, decimal amount, string by) => Update(id, o =>
    {
        if (amount <= 0 || RepairRules.Money(o.Refunded + amount) > RepairRules.Money(o.Prepay))
            return false;
        o.Refunded = RepairRules.Money(o.Refunded + amount);
        return true;
    }, by, "refund");

    /// <summary>Выдача: только из «Готов»; доплата (если есть) уже проведена продажей услуги и подтверждена («чек оплачен»); переплата предоплаты возвращена клиенту.</summary>
    public bool Issue(string id, string by) => Update(id, o =>
    {
        if (o.Status != RepairStatus.Ready)
            return false;
        var due = RepairRules.AmountDue(o.FinalPrice, o.Prepay);
        if (due > 0 && o.PaymentInCart + 0.001m < due)
            return false;
        if (o.Refunded + 0.001m < RepairRules.Overpaid(o.FinalPrice, o.Prepay))
            return false;
        o.PaidAtIssue = due > 0 ? RepairRules.Money(o.PaymentInCart) : 0m;
        o.PaymentInCart = 0;
        o.IssuedAt = _now();
        o.IssuedBy = by ?? "";
        o.Status = RepairStatus.Issued;
        return true;
    }, by, "issue");

    /// <summary>Отмена приёма. Нельзя, пока не возвращена предоплата или в чеке висит строка оплаты.</summary>
    public bool Cancel(string id, string by) => Update(id, o =>
    {
        if (!RepairRules.CanMove(o.Status, RepairStatus.Cancelled) || RepairRules.PrepayToRefund(o) > 0 || o.PaymentInCart > 0)
            return false;
        o.Status = RepairStatus.Cancelled;
        return true;
    }, by, "cancel");

    public bool MarkNotified(string id, string by) => Update(id, o => { o.NotifiedAt = _now(); return true; }, by, "notified");

    /// <summary>Поиск по номеру, клиенту, телефону, устройству, серийному номеру (хвост номера тоже).</summary>
    public static IEnumerable<RepairOrder> Filter(IEnumerable<RepairOrder> all, string? query, RepairStatus? status)
    {
        var q = (query ?? "").Trim();
        var qs = ImeiValidator.Normalize(q);
        var digits = q.TrimStart('Р', 'р', 'P', 'p', '-').TrimStart('0');
        return all.Where(o => status is null || o.Status == status).Where(o =>
            q.Length == 0
            || RepairRules.Code(o).Contains(q, StringComparison.OrdinalIgnoreCase)
            || (digits.Length > 0 && o.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == digits)
            || o.ClientName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || o.ClientPhone.Contains(q, StringComparison.OrdinalIgnoreCase)
            || o.Device.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (qs.Length >= 4 && o.Serial.Contains(qs, StringComparison.OrdinalIgnoreCase)));
    }
}
