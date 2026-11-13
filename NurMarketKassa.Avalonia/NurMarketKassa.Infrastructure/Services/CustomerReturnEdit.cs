using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-11 (сервер Умара, боевой app.nurcrm.kg: правка и аннулирование возврата от клиента за 24 ч): правка СУЩЕСТВУЮЩИХ строк возврата от клиента.
// Контракт: PATCH api/main/pos/returns/{id}/ {version, edit_reason, items:[{sale_item_id, quantity}]} — items = ПОЛНЫЙ новый состав возврата (строка с 0 не
// отправляется = убрана). Цены, скидки и суммы НЕ отправляются: деньги пересчитывает сервер; касса показывает сумму ориентировочно (кол-во × цена строки).
// Максимум строки = сколько ещё можно вернуть по чеку (returnable_qty строки чека — уже без этого возврата) + количество в этом возврате.
// Расчёт и проверки — здесь (не во view), окно — CustomerReturnEditDialog.

/// <summary>Строка возврата от клиента в окне правки. <see cref="Max"/> — потолок количества (в чеке − возвращено другими возвратами).</summary>
public sealed record CustomerReturnEditLine(string SaleItemId, string Name, string Unit, double Qty, double Max, double UnitPrice)
{
    /// <summary>Сумма строки ориентировочно (точную считает сервер).</summary>
    public double Total => ReturnLineMath.LineTotal(Qty, UnitPrice, 0);

    /// <summary>Штучная единица — количество только целое.</summary>
    public bool IsPiece => Unit.Trim().ToLowerInvariant() is "" or "шт" or "шт." or "даана" or "pcs" or "adet" or "dona";
}

public static class CustomerReturnEdit
{
    public const int ReasonMin = 5;
    public const int ReasonMax = 500;

    /// <summary>Строки для окна правки: из карточки возврата (GET pos/returns/{id}/) и карточки чека (GET pos/sales/{id}/; default — чек не получен,
    /// тогда количество можно только уменьшать). Одинаковые sale_item_id складываются.</summary>
    public static List<CustomerReturnEditLine> FromCards(JsonElement returnCard, JsonElement saleCard)
    {
        var saleLines = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in Items(saleCard))
        {
            if (ReturnLineCheck.SaleItemId(it) is { Length: > 0 } sid)
                saleLines.TryAdd(sid, it);
            if (Str(it, "id") is { Length: > 0 } rowId)
                saleLines.TryAdd(rowId, it);
        }

        var list = new List<CustomerReturnEditLine>();
        foreach (var item in Items(returnCard))
        {
            var sid = SaleItemIdOf(item);
            var qty = Num(item, "quantity") ?? Num(item, "qty") ?? 0;
            var hasSale = sid.Length > 0 && saleLines.TryGetValue(sid, out _);
            var sale = hasSale ? saleLines[sid] : default;
            var name = FirstStr(item, "product_name", "name", "title");
            if (name.Length == 0 && hasSale)
                name = CartDisplayHelper.ItemName(sale);
            var unit = FirstStr(item, "unit", "unit_display");
            if (unit.Length == 0 && hasSale)
                unit = FirstStr(sale, "unit", "unit_display", "product_unit");
            var price = UnitPriceOf(item, qty) is { } p && p > 0 ? p : hasSale ? CartDisplayHelper.UnitPrice(sale) : 0;
            var max = hasSale ? CartDisplayHelper.RefundableQuantity(sale) + qty : qty;
            // 2026-11-12 (аудит Н-1): сервер не прислал ни returnable_qty, ни «возвращено» — потолок был «в чеке + в возврате» (больше проданного); ограничиваем количеством строки чека
            if (hasSale && Num(sale, "quantity") is { } sold && sold > 1e-9)
                max = Math.Min(max, Math.Max(sold, qty));
            var same = list.FindIndex(l => sid.Length > 0 && string.Equals(l.SaleItemId, sid, StringComparison.OrdinalIgnoreCase));
            if (same >= 0)
            {
                var prev = list[same];
                list[same] = prev with { Qty = prev.Qty + qty, Max = hasSale ? max + prev.Qty : prev.Max + qty };
                continue;
            }
            list.Add(new CustomerReturnEditLine(sid, name, unit, qty, Math.Max(max, qty), price));
        }
        return list;
    }

    /// <summary>id строки чека у строки возврата: sale_item_id, sale_item (строка или объект с id).</summary>
    internal static string SaleItemIdOf(JsonElement item)
    {
        if (Str(item, "sale_item_id") is { Length: > 0 } a)
            return a;
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("sale_item", out var si))
        {
            if (si.ValueKind is JsonValueKind.String or JsonValueKind.Number && si.ToString() is { Length: > 0 } b)
                return b;
            if (si.ValueKind == JsonValueKind.Object && Str(si, "id") is { Length: > 0 } c)
                return c;
        }
        return "";
    }

    private static double? UnitPriceOf(JsonElement item, double qty)
    {
        foreach (var key in new[] { "unit_price", "price" })
            if (Num(item, key) is { } p && p > 0)
                return p;
        if (qty > 1e-9)
            foreach (var key in new[] { "amount", "line_total", "total", "refund_amount", "sum" })
                if (Num(item, key) is { } t && t > 0)
                    return t / qty;
        return null;
    }

    public static double Total(IEnumerable<CustomerReturnEditLine> lines) => ReturnLineMath.Total(lines.Select(l => (l.Qty, l.UnitPrice, 0d)));

    /// <summary>Проверка перед сохранением; null — всё в порядке, иначе текст ошибки (5 языков).</summary>
    /// <param name="original">2026-11-12 (аудит В-5): исходные строки окна — нетронутая строка не проверяется на «штучное — целое» (раньше строка без единицы в ответе сервера считалась штучной, и дробное
    /// исходное количество 1,255 блокировало сохранение всего документа); у строки без единицы с дробным исходным количеством штучной проверки нет и при правке. null — все строки считаются изменёнными.</param>
    public static string? Validate(IReadOnlyList<CustomerReturnEditLine> lines, string reason, IReadOnlyList<CustomerReturnEditLine>? original = null)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var was = original is null ? null : original.Count == lines.Count ? original[i] : original.FirstOrDefault(o => o.SaleItemId.Length > 0 && string.Equals(o.SaleItemId, l.SaleItemId, StringComparison.OrdinalIgnoreCase));
            if (was is not null && Math.Abs(was.Qty - l.Qty) <= 0.0005 && l.Qty >= 0 && (l.Qty == 0 || l.SaleItemId.Length > 0))
                continue; // строка не тронута — не проверяем (проверять изменения, а не исходные данные сервера)
            var wholeOnly = l.IsPiece && !(l.Unit.Trim().Length == 0 && was is not null && Math.Abs(was.Qty - Math.Round(was.Qty)) > 0.0001);
            var name = l.Name.Length > 0 ? l.Name : "—";
            var max = l.Max.ToString("0.###", CultureInfo.CurrentCulture);
            string? problem = l.Qty < 0 ? Tr.T("количество не может быть меньше 0", "саны 0дөн аз болбойт", "the quantity cannot be below 0", "miktar 0'dan az olamaz", "miqdor 0 dan kam bo'lmaydi")
                : l.Qty > 0 && wholeOnly && Math.Abs(l.Qty - Math.Round(l.Qty)) > 0.0001 ? Tr.T("для штучного товара — целое число", "даана товар үчүн — бүтүн сан", "a whole number for piece goods", "adetli ürün için tam sayı", "donali mahsulot uchun — butun son")
                : l.Qty > l.Max + 0.0005 ? Tr.T($"больше, чем можно вернуть по чеку (не больше {max})", $"чек боюнча кайтарууга мүмкүн болгондон көп ({max} ашпашы керек)",
                    $"more than can be returned under the receipt (at most {max})", $"fişe göre iade edilebilecekten fazla (en çok {max})", $"chek bo'yicha qaytarish mumkin bo'lganidan ko'p ({max} dan oshmasin)")
                : l.Qty > 0 && l.SaleItemId.Length == 0 ? Tr.T("сервер не прислал строку чека — правка невозможна, обновите список", "сервер чектин сабын жөнөткөн жок — оңдоо мүмкүн эмес, тизмени жаңыртыңыз",
                    "the server did not send the receipt line — editing is impossible, refresh the list", "sunucu fiş satırını göndermedi — düzenleme mümkün değil, listeyi yenileyin", "server chek qatorini yubormadi — tahrirlab bo'lmaydi, ro'yxatni yangilang")
                : null;
            if (problem is not null)
                return $"«{name}»: {problem}.";
        }
        if (!lines.Any(l => l.Qty > 0))
            return Tr.T("В возврате должна остаться хотя бы одна строка с количеством больше 0. Чтобы отменить возврат целиком, нажмите «Удалить возврат».",
                "Кайтарууда саны 0дөн көп жок дегенде бир сап калышы керек. Кайтарууну толугу менен жокко чыгаруу үчүн «Кайтарууну өчүрүү» баскычын басыңыз.",
                "At least one line with a quantity above 0 must remain. To cancel the whole return, press “Delete return”.",
                "En az bir satırda 0'dan büyük miktar kalmalı. İadenin tamamını iptal etmek için «İadeyi sil»e basın.",
                "Qaytarishda miqdori 0 dan katta kamida bitta qator qolishi kerak. Qaytarishni butunlay bekor qilish uchun «Qaytarishni o'chirish» tugmasini bosing.");
        var r = (reason ?? "").Trim();
        if (r.Length is < ReasonMin or > ReasonMax)
            return Tr.T($"Укажите причину правки (от {ReasonMin} до {ReasonMax} знаков) — она попадёт в журнал изменений.",
                $"Оңдоонун себебин жазыңыз ({ReasonMin}–{ReasonMax} белги) — ал өзгөртүүлөр журналына түшөт.",
                $"Enter the reason for the change ({ReasonMin} to {ReasonMax} characters) — it goes to the change log.",
                $"Düzenleme nedenini girin ({ReasonMin}–{ReasonMax} karakter) — değişiklik günlüğüne yazılır.",
                $"Tahrirlash sababini kiriting ({ReasonMin}–{ReasonMax} belgi) — u o'zgarishlar jurnaliga yoziladi.");
        return null;
    }

    public static bool HasChanges(IReadOnlyList<CustomerReturnEditLine> original, IReadOnlyList<CustomerReturnEditLine> edited)
    {
        if (original.Count != edited.Count)
            return true;
        for (var i = 0; i < original.Count; i++)
            if (Math.Abs(original[i].Qty - edited[i].Qty) > 0.0005)
                return true;
        return false;
    }

    /// <summary>2026-11-12 (аудит В-2, В-3): состав возврата на сервере (свежая карточка) совпадает с тем, что отправило окно (строки с количеством 0 не считаются) — значит, наша правка
    /// дошла; иначе версию изменил кто-то другой.</summary>
    public static bool SameComposition(IReadOnlyList<CustomerReturnEditLine> server, IReadOnlyList<CustomerReturnEditLine> sent)
    {
        var a = server.Where(l => l.Qty > 0.0005).OrderBy(l => l.SaleItemId, StringComparer.OrdinalIgnoreCase).ToList();
        var b = sent.Where(l => l.Qty > 0.0005).OrderBy(l => l.SaleItemId, StringComparer.OrdinalIgnoreCase).ToList();
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].SaleItemId, b[i].SaleItemId, StringComparison.OrdinalIgnoreCase) || Math.Abs(a[i].Qty - b[i].Qty) > 0.0005)
                return false;
        return true;
    }

    /// <summary>Сумма документа возврата из карточки/ответа сервера (amount, иначе total_amount / total); null — не прислана.</summary>
    public static decimal? DocAmount(JsonElement card)
    {
        foreach (var name in new[] { "amount", "total_amount", "total" })
            if (ServerErrorCodes.Number(card, name) is { } v && v >= 0m)
                return v;
        return null;
    }

    /// <summary>Тело PATCH pos/returns/{id}/ (версия добавляется в ReturnEditApi.PatchAsync): ровно {edit_reason, items:[{sale_item_id, quantity}]}.</summary>
    public static Dictionary<string, object?> BuildPatchBody(IReadOnlyList<CustomerReturnEditLine> lines, string reason) => new()
    {
        ["edit_reason"] = (reason ?? "").Trim(),
        ["items"] = lines.Where(l => l.Qty > 0).Select(l => new Dictionary<string, object?>
        {
            ["sale_item_id"] = l.SaleItemId,
            ["quantity"] = l.Qty.ToString("0.###", CultureInfo.InvariantCulture),
        }).ToList(),
    };

    /// <summary>Подпись состава для ключа повтора: тот же состав после обрыва — тот же Idempotency-Key.</summary>
    public static string Signature(string returnId, int version, IReadOnlyList<CustomerReturnEditLine> lines, string reason) =>
        returnId + "|v" + version + "|" + string.Join(";", lines.Select(l => $"{l.SaleItemId}:{l.Qty.ToString("0.###", CultureInfo.InvariantCulture)}")) + "|" + (reason ?? "").Trim();

    private static IEnumerable<JsonElement> Items(JsonElement x)
    {
        if (x.ValueKind != JsonValueKind.Object)
            return Enumerable.Empty<JsonElement>();
        foreach (var name in new[] { "items", "lines" })
            if (x.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
                return a.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList();
        return Enumerable.Empty<JsonElement>();
    }

    private static string Str(JsonElement e, string name) => ReturnEditState.Str(e, name);

    private static string FirstStr(JsonElement e, params string[] names) => names.Select(n => Str(e, n)).FirstOrDefault(v => v.Length > 0) ?? "";

    private static double? Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? d : null;
}
