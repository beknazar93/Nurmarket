namespace NurMarketKassa.Services;

// 2026-11-01 (просьба владельца «рядом с товаром пишется, кто и когда редактировал»): правки конкретной позиции накладной по журналу версий
// (ReceiptEditStore → receipt-edits.json; те же данные пишутся построчно в purchase-edits.jsonl). Ключ позиции — id строки прихода, для товара из
// дополнительного прихода ("new:…") и для цены продажи — id товара. Записей нет (старая накладная до журнала) — отметки нет.

public sealed record LineEdit(DateTime At, string By, string ByRole, string Reason, string Field, string Before, string After, int Version, string AddendumId);

public static class ReceiptEditLineHistory
{
    /// <summary>Правки позиции (проведённые и частично проведённые), новые сверху.</summary>
    public static List<LineEdit> For(ReceiptHistory history, string lineId, string productId, bool sharedProduct = false)
    {
        var result = new List<LineEdit>();
        foreach (var e in history.Edits.Where(e => e.Status is "done" or "partial" or "abandoned"))
        {
            var addendum = e.Steps.FirstOrDefault(s => s.Kind == "addendum")?.ServerId ?? "";
            foreach (var f in ReceiptEditAuditLog.FieldChanges(e))
            {
                // 2026-11-01 (приёмка 1.17.72, Н-3): две строки одного товара не путаем — если у записи есть id строки, сравниваем ТОЛЬКО его; по товару — лишь у записей без id строки
                // (цена продажи товара в каталоге, старые записи) и когда в накладной товар в одной строке (sharedProduct == false)
                var same = f.LineId.Length > 0 ? lineId.Length > 0 && f.LineId == lineId
                    : productId.Length > 0 && !sharedProduct && string.Equals(f.ProductId, productId, StringComparison.OrdinalIgnoreCase);
                if (!same || f.Status is "failed" or "not_posted" or "unknown" or "pending")
                    continue;
                result.Add(new LineEdit(e.At, e.By, e.ByRole, e.Reason, f.Field, f.Before, f.After, e.Version, f.Field == "added_item" ? addendum : ""));
            }
        }
        return result.OrderByDescending(x => x.At).ToList();
    }

    public static string RoleName(string role) => role.ToLowerInvariant() switch
    {
        "owner" => Tr.T("владелец", "ээси", "owner", "sahip", "egasi"),
        "admin" => Tr.T("администратор", "администратор", "administrator", "yönetici", "administrator"),
        "" => "",
        _ => role,
    };

    private static string Who(LineEdit e) => e.By + (e.ByRole.Length > 0 ? $" ({RoleName(e.ByRole)})" : "");

    /// <summary>«✎ Имя (роль), 01.11 15:42 · количество 10 → 8» — что сделано последней правкой.</summary>
    public static string Mark(LineEdit e)
    {
        var verb = e.Field switch
        {
            "removed" => Tr.T("удалил(а) ", "өчүрдү ", "removed by ", "silen: ", "o'chirdi: "),
            "added_item" => Tr.T("добавил(а) ", "кошту ", "added by ", "ekleyen: ", "qo'shdi: "),
            _ => "",
        };
        var what = e.Field switch
        {
            "removed" => "",
            "added_item" when e.AddendumId.Length == 0 => Tr.T($" · в накладную, {e.After}", $" · накладнойго, {e.After}", $" · to the invoice, {e.After}", $" · faturaya, {e.After}", $" · yuk xatiga, {e.After}"),
            "added_item" => Tr.T($" · доп. приход, {e.After}", $" · кошумча кирүү, {e.After}", $" · additional receipt, {e.After}", $" · ek giriş, {e.After}", $" · qo'shimcha kirim, {e.After}"),
            "sale_price" => Tr.T($" · цена продажи {e.Before} → {e.After}", $" · сатуу баасы {e.Before} → {e.After}", $" · sale price {e.Before} → {e.After}", $" · satış fiyatı {e.Before} → {e.After}", $" · sotuv narxi {e.Before} → {e.After}"),
            "markup_percent" => Tr.T($" · наценка {e.Before} → {e.After} %", $" · үстөк {e.Before} → {e.After} %", $" · markup {e.Before} → {e.After} %", $" · kâr {e.Before} → {e.After} %", $" · ustama {e.Before} → {e.After} %"),
            _ => $" · {ReceiptEditAuditLog.FieldLabel(e.Field)} {e.Before} → {e.After}",
        };
        return $"✎ {verb}{Who(e)}, {e.At:dd.MM HH:mm}{what}";
    }

    /// <summary>Вся история позиции для подсказки и окна: время, кто, было → стало, причина.</summary>
    public static string Full(IReadOnlyList<LineEdit> events) =>
        string.Join("\n", events.Select(e => Mark(e) + " · " + Tr.T("причина: ", "себеби: ", "reason: ", "neden: ", "sabab: ") + e.Reason + $" (v{e.Version})"));
}
