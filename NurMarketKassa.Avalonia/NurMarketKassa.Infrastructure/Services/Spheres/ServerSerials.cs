using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>Одна найденная сервером продажа серийного номера (<c>GET serials/?serial=…</c>, поле <c>results[]</c>).</summary>
public sealed class ServerSerialHit
{
    public string Serial { get; init; } = "";
    public string SaleId { get; init; } = "";
    public string SaleNumber { get; init; } = "";
    public string SaleStatus { get; init; } = "";
    public string SaleItemId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTime? SoldAt { get; init; }
    public DateTime? WarrantyUntil { get; init; }
    public bool WarrantyActive { get; init; }
    public bool Returned { get; init; }
    public DateTime? ReturnedAt { get; init; }
    public string SaleReturnId { get; init; } = "";
    /// <summary>Офлайн-чек с номером, который уже числился проданным: сервер его принял и пометил (видно в поиске).</summary>
    public bool Duplicate { get; init; }

    /// <summary>Запись местного журнала из этой находки (для гарантийного талона и общего поиска).</summary>
    public SerialRecord ToRecord() => new()
    {
        SaleId = SaleId,
        ReceiptNo = SaleNumber,
        SoldAt = SoldAt ?? default,
        ProductId = ProductId,
        Title = Name,
        Serial = Serial,
        WarrantyUntil = WarrantyUntil,
        WarrantyMonths = WarrantyUntil is { } u && SoldAt is { } s ? ServerSerials.MonthsBetween(s, u) : 0,
    };

    /// <summary>Что сказать кассиру: «Продан 12.11.2026, чек №1234 · Возвращён · Гарантия действует до 13.11.2027 · Повтор номера». 5 языков.</summary>
    public string StatusText()
    {
        var parts = new List<string>();
        var sold = SoldAt is { } d ? SerialJournal.Day(d) : "";
        var no = SaleNumber.Length > 0 ? SaleNumber : "";
        parts.Add(Returned
            ? Tr.T($"Возвращён{(ReturnedAt is { } r ? " " + SerialJournal.Day(r) : "")}",
                $"Кайтарылган{(ReturnedAt is { } r2 ? " " + SerialJournal.Day(r2) : "")}",
                $"Returned{(ReturnedAt is { } r3 ? " " + SerialJournal.Day(r3) : "")}",
                $"İade edildi{(ReturnedAt is { } r4 ? " " + SerialJournal.Day(r4) : "")}",
                $"Qaytarilgan{(ReturnedAt is { } r5 ? " " + SerialJournal.Day(r5) : "")}")
            : Tr.T($"Продан {sold}".Trim() + (no.Length > 0 ? $", чек №{no}" : ""),
                $"Сатылган {sold}".Trim() + (no.Length > 0 ? $", чек №{no}" : ""),
                $"Sold {sold}".Trim() + (no.Length > 0 ? $", receipt {no}" : ""),
                $"Satıldı {sold}".Trim() + (no.Length > 0 ? $", fiş {no}" : ""),
                $"Sotilgan {sold}".Trim() + (no.Length > 0 ? $", chek №{no}" : "")));
        if (WarrantyUntil is { } u)
            parts.Add(WarrantyActive
                ? Tr.T($"гарантия действует до {SerialJournal.Day(u)}", $"кепилдик {SerialJournal.Day(u)} чейин иштейт", $"warranty active until {SerialJournal.Day(u)}", $"garanti {SerialJournal.Day(u)} tarihine kadar geçerli", $"kafolat {SerialJournal.Day(u)} gacha amal qiladi")
                : Tr.T($"гарантия истекла {SerialJournal.Day(u)}", $"кепилдик {SerialJournal.Day(u)} бүттү", $"warranty expired {SerialJournal.Day(u)}", $"garanti {SerialJournal.Day(u)} tarihinde bitti", $"kafolat {SerialJournal.Day(u)} da tugagan"));
        if (Duplicate)
            parts.Add(Tr.T("повтор номера (проверьте чеки)", "номер кайталанган (чектерди текшериңиз)", "duplicate number (check the receipts)", "tekrarlanan numara (fişleri kontrol edin)", "raqam takrorlangan (cheklarni tekshiring)"));
        return string.Join(" · ", parts);
    }
}

/// <summary>Ответ <c>GET serials/?serial=…</c>: <c>sold</c> — есть непроданный-обратно экземпляр; <c>Results</c> — все продажи этого номера.</summary>
public sealed class ServerSerialLookup
{
    public string Serial { get; init; } = "";
    public bool Sold { get; init; }
    public IReadOnlyList<ServerSerialHit> Results { get; init; } = Array.Empty<ServerSerialHit>();
}

/// <summary>
/// 2026-11-13 (сервер apl1311+sn1311, «Сервер 13.11 — серийные номера, гарантия, Электроника»): разбор и сборка полей серийников на проводе —
/// <c>serial_numbers</c> строки корзины/чека, <c>warranty_until</c> строки проданного чека, ответ поиска <c>serials/</c>. Отправку серийников включает флаг
/// компании electronics_2026_11 (<see cref="Enabled"/>); без него касса работает как раньше (номера только в местном журнале).
/// </summary>
public static class ServerSerials
{
    /// <summary>Лимиты сервера: до 500 номеров в строке, каждый до 64 знаков.</summary>
    public const int MaxPerLine = 500;
    public const int MaxLength = 64;

    /// <summary>Слать ли серийники серверу (флаг компании electronics_2026_11 из capabilities; подменяется в проверках).</summary>
    public static bool Enabled => ElectronicsProfile.ServerFlagEnabled();

    /// <summary>Номера без пустых и повторов (без учёта регистра и пробелов, как у сервера), порядок сохранён.</summary>
    public static List<string> Distinct(IEnumerable<string?> serials)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var raw in serials)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0)
                continue;
            if (seen.Add(ImeiValidator.Normalize(s)))
                list.Add(s);
        }
        return list;
    }

    /// <summary>Склейка двух списков номеров одной строки (одинаковые строки товара в чеке склеиваются — номера тоже, без повторов).</summary>
    public static List<string> Merge(IEnumerable<string?> first, IEnumerable<string?> second) => Distinct(first.Concat(second));

    /// <summary><c>serial_numbers</c> в тело продажи: массив строк, не больше <see cref="MaxPerLine"/>. null — номеров нет.</summary>
    public static JsonArray? ToJson(IEnumerable<string?> serials)
    {
        var list = Distinct(serials);
        if (list.Count == 0)
            return null;
        var arr = new JsonArray();
        foreach (var s in list.Take(MaxPerLine))
            arr.Add(s);
        return arr;
    }

    /// <summary>Номера строки чека с сервера (<c>serial_numbers</c>).</summary>
    public static List<string> LineSerials(JsonElement line)
    {
        var list = new List<string>();
        if (line.ValueKind == JsonValueKind.Object && line.TryGetProperty("serial_numbers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } t)
                    list.Add(t);
        return list;
    }

    /// <summary>«Гарантия до» строки проданного чека (<c>warranty_until</c>, дата «2027-11-13»). null — гарантии нет или поля нет.</summary>
    public static DateTime? LineWarrantyUntil(JsonElement line) =>
        line.ValueKind == JsonValueKind.Object && line.TryGetProperty("warranty_until", out var w) && w.ValueKind == JsonValueKind.String ? ParseDate(w.GetString()) : null;

    public static DateTime? ParseDate(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d.LocalDateTime
            : null;

    /// <summary>Дата продажи по карточке чека: sold_at, затем created_at; нет — null.</summary>
    public static DateTime? SaleDate(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in new[] { "sold_at", "created_at" })
            if (sale.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && ParseDate(v.GetString()) is { } d)
                return d;
        return null;
    }

    /// <summary>Сколько целых месяцев между продажей и «гарантия до» (для талона: «Срок гарантии: N мес.»). Не меньше 0.</summary>
    public static int MonthsBetween(DateTime soldAt, DateTime until)
    {
        var months = (until.Year - soldAt.Year) * 12 + until.Month - soldAt.Month;
        if (until.Day < soldAt.Day && until.Day != DateTime.DaysInMonth(until.Year, until.Month))
            months--;
        return Math.Clamp(months, 0, 120);
    }

    /// <summary>Записи «серийник ↔ чек ↔ гарантия» из карточки проданного чека (<c>GET pos/sales/{id}/</c>): строки с <c>serial_numbers</c>. Пусто — сервер номеров не вернул
    /// (старый сервер, флаг выключен, номеров нет).</summary>
    public static List<SerialRecord> RecordsFromSale(JsonElement sale, string? receiptNo = null)
    {
        var result = new List<SerialRecord>();
        if (sale.ValueKind != JsonValueKind.Object)
            return result;
        var saleId = sale.TryGetProperty("id", out var idEl) && idEl.ValueKind is JsonValueKind.String or JsonValueKind.Number ? idEl.ToString() : "";
        var no = receiptNo
                 ?? (sale.TryGetProperty("number", out var nEl) && nEl.ValueKind is JsonValueKind.String or JsonValueKind.Number ? nEl.ToString() : "");
        var soldAt = SaleDate(sale) ?? DateTime.Now;
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            var serials = LineSerials(line);
            if (serials.Count == 0)
                continue;
            var until = LineWarrantyUntil(line);
            var months = until is { } u ? MonthsBetween(soldAt, u) : 0;
            foreach (var serial in serials)
                result.Add(new SerialRecord
                {
                    SaleId = saleId,
                    ReceiptNo = no,
                    SoldAt = soldAt,
                    ProductId = CartDisplayHelper.TryProductId(line) ?? "",
                    Title = CartDisplayHelper.ItemName(line).Trim(),
                    Serial = serial,
                    WarrantyMonths = months,
                    WarrantyUntil = until,
                });
        }
        return result;
    }

    /// <summary>Разбор ответа <c>GET serials/?serial=…</c>.</summary>
    public static ServerSerialLookup ParseLookup(JsonElement d, string requested)
    {
        var results = new List<ServerSerialHit>();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
                if (r.ValueKind == JsonValueKind.Object)
                    results.Add(ParseHit(r));
        var sold = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("sold", out var s) && s.ValueKind == JsonValueKind.True;
        var serial = d.ValueKind == JsonValueKind.Object && Str(d, "serial") is { Length: > 0 } ss ? ss : requested;
        return new ServerSerialLookup { Serial = serial, Sold = sold, Results = results };
    }

    private static ServerSerialHit ParseHit(JsonElement r) => new()
    {
        Serial = Str(r, "serial"),
        SaleId = Str(r, "sale_id"),
        SaleNumber = Str(r, "sale_number"),
        SaleStatus = Str(r, "sale_status"),
        SaleItemId = Str(r, "sale_item_id"),
        ProductId = Str(r, "product_id"),
        Name = Str(r, "name"),
        SoldAt = ParseDate(Str(r, "sold_at")),
        WarrantyUntil = ParseDate(Str(r, "warranty_until")),
        WarrantyActive = Bool(r, "warranty_active"),
        Returned = Bool(r, "returned"),
        ReturnedAt = ParseDate(Str(r, "returned_at")),
        SaleReturnId = Str(r, "sale_return_id"),
        Duplicate = Bool(r, "duplicate"),
    };

    private static string Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" }
            : "";

    private static bool Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
