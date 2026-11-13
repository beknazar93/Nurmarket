using System.Globalization;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): правка СУЩЕСТВУЮЩИХ строк возврата поставщику — количество, цена закупки, скидка
// строки, примечание; комментарий документа и обязательная причина правки. Расчёт и проверка — здесь (не во view): сумма строки = кол-во × цена − скидка,
// до копейки от нуля (ReturnLineMath). Тело PATCH — полный новый состав по контракту из ТЗ Умару (п. 4, не подтверждён сервером): строка с количеством 0
// не отправляется (= удалена из возврата); хотя бы одна строка должна остаться — иначе возврат аннулируют кнопкой «Удалить».
// 2026-11-11 (реальный контракт Умара): тело {version, edit_reason, comment?, items:[{id, qty, purchase_price}]}; СКИДКА И ПРИМЕЧАНИЕ СТРОКИ СЕРВЕР НЕ ХРАНИТ —
// не отправляются и в сумму не входят (сумма строки = кол-во × цена); строка без id → 400 new_lines_not_allowed, поэтому id обязателен; отсутствующие строки сервер удаляет.

/// <summary>Строка в окне правки (значения уже разобраны из полей ввода).</summary>
public sealed record SupplierReturnEditLine(string Id, string ProductId, string Name, string Unit, double Qty, double Price, double Discount, string Note, string Article = "", string Barcode = "") // 2026-11-12 (аудит Н-3): артикул и штрихкод — для поиска в длинной накладной
{
    public double Total => ReturnLineMath.LineTotal(Qty, Price, 0); // 2026-11-11: скидку строки сервер не хранит

    /// <summary>Штучная единица — количество только целое.</summary>
    public bool IsPiece => Unit.Trim().ToLowerInvariant() is "" or "шт" or "шт." or "даана" or "pcs" or "adet" or "dona";
}

public static class SupplierReturnEdit
{
    public const int ReasonMin = 5;
    public const int ReasonMax = 500;

    /// <summary>Строки возврата для окна правки (из карточки сервера).</summary>
    public static List<SupplierReturnEditLine> FromInfo(SupplierReturnInfo info) =>
        info.Lines.Select(l => new SupplierReturnEditLine(l.Id, l.ProductId, l.ProductName, l.Unit, l.Qty, l.PurchasePrice, l.Discount, l.Note, l.Article, l.Barcode)).ToList();

    public static double Total(IEnumerable<SupplierReturnEditLine> lines) => ReturnLineMath.Total(lines.Select(l => (l.Qty, l.Price, 0d))); // 2026-11-11: без скидки строки

    /// <summary>Число из поля ввода (запятая или точка); пусто/мусор — null.</summary>
    public static double? ParseNumber(string? text) =>
        double.TryParse((text ?? "").Replace(" ", "").Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;

    /// <summary>Проверка перед сохранением; null — всё в порядке, иначе текст ошибки (5 языков).</summary>
    /// <param name="original">2026-11-12 (аудит В-5): исходные строки окна — нетронутая строка не проверяется на «штучное — целое» (строка без единицы в ответе сервера считалась штучной, и дробное
    /// исходное количество 1,255 блокировало сохранение всей накладной); у строки без единицы с дробным исходным количеством штучной проверки нет и при правке. null — все строки считаются изменёнными.</param>
    public static string? Validate(IReadOnlyList<SupplierReturnEditLine> lines, string reason, IReadOnlyList<SupplierReturnEditLine>? original = null) =>
        Validate(lines, reason, original, out _);

    /// <param name="badIndex">2026-11-12 (аудит С-6): номер строки с ошибкой (−1 — ошибка не в строке) — окно раскрывает и подсвечивает её, даже если она скрыта страницей или поиском.</param>
    public static string? Validate(IReadOnlyList<SupplierReturnEditLine> lines, string reason, IReadOnlyList<SupplierReturnEditLine>? original, out int badIndex)
    {
        badIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var was = original is null ? null : original.Count == lines.Count ? original[i] : original.FirstOrDefault(o => o.Id.Length > 0 && string.Equals(o.Id, l.Id, StringComparison.Ordinal));
            if (was is not null && Math.Abs(was.Qty - l.Qty) <= 0.0005 && Math.Abs(was.Price - l.Price) <= 0.00005 && (l.Qty <= 0 || l.Id.Length > 0))
                continue; // строка не тронута — не проверяем (проверять изменения, а не исходные данные сервера)
            var wholeOnly = l.IsPiece && !(l.Unit.Trim().Length == 0 && was is not null && Math.Abs(was.Qty - Math.Round(was.Qty)) > 0.0001);
            var name = l.Name.Length > 0 ? l.Name : "—";
            string? problem = l.Qty < 0 ? Tr.T("количество не может быть меньше 0", "саны 0дөн аз болбойт", "the quantity cannot be below 0", "miktar 0'dan az olamaz", "miqdor 0 dan kam bo'lmaydi")
                : l.Qty > 0 && wholeOnly && Math.Abs(l.Qty - Math.Round(l.Qty)) > 0.0001 ? Tr.T("для штучного товара — целое число", "даана товар үчүн — бүтүн сан", "a whole number for piece goods", "adetli ürün için tam sayı", "donali mahsulot uchun — butun son")
                : l.Price < 0 ? Tr.T("цена не может быть меньше 0", "баасы 0дөн аз болбойт", "the price cannot be below 0", "fiyat 0'dan az olamaz", "narx 0 dan kam bo'lmaydi")
                // 2026-11-11: скидка строки не проверяется — сервер её не хранит и окно её не отправляет; id строки обязателен (иначе 400 new_lines_not_allowed)
                : l.Qty > 0 && l.Id.Length == 0 ? Tr.T("сервер не прислал id строки — правка невозможна, обновите список", "сервер саптын id жөнөткөн жок — оңдоо мүмкүн эмес, тизмени жаңыртыңыз",
                    "the server did not send the line id — editing is impossible, refresh the list", "sunucu satır kimliğini göndermedi — düzenleme mümkün değil, listeyi yenileyin", "server qator id sini yubormadi — tahrirlab bo'lmaydi, ro'yxatni yangilang")
                : null;
            if (problem is not null)
            {
                badIndex = i;
                return $"«{name}»: {problem}.";
            }
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

    /// <summary>Изменилось ли что-нибудь по сравнению с исходными строками и комментарием.</summary>
    public static bool HasChanges(IReadOnlyList<SupplierReturnEditLine> original, IReadOnlyList<SupplierReturnEditLine> edited, string originalComment, string comment)
    {
        if (!string.Equals((originalComment ?? "").Trim(), (comment ?? "").Trim(), StringComparison.Ordinal) || original.Count != edited.Count)
            return true;
        for (var i = 0; i < original.Count; i++)
        {
            var a = original[i];
            var b = edited[i];
            if (Math.Abs(a.Qty - b.Qty) > 0.0005 || Math.Abs(a.Price - b.Price) > 0.00005) // 2026-11-11: скидка/примечание строки не отправляются
                return true;
        }
        return false;
    }

    /// <summary>Тело PATCH suppliers/returns/{id}/ (версия добавляется в ReturnEditApi.PatchAsync).</summary>
    /// <param name="original">2026-11-12 (аудит В-4): исходные строки — у нетронутой строки уходят исходные количество и цена до последнего знака (формат «R»), а не округлённые до 3–4 знаков
    /// (цена упаковки 33,3333 не превращается в 33,33 у строк, которые никто не трогал).</param>
    public static Dictionary<string, object?> BuildPatchBody(IReadOnlyList<SupplierReturnEditLine> lines, string reason, string comment, IReadOnlyList<SupplierReturnEditLine>? original = null)
    {
        var originalById = original?.Where(o => o.Id.Length > 0).GroupBy(o => o.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var items = lines.Where(l => l.Qty > 0).Select(l =>
        {
            // 2026-11-11 (реальный контракт): ровно {id, qty, purchase_price} — без скидки, примечания и product_id
            var was = originalById is not null && originalById.TryGetValue(l.Id, out var o) ? o : null;
            return new Dictionary<string, object?>
            {
                ["id"] = l.Id,
                ["qty"] = was is not null && was.Qty == l.Qty ? l.Qty.ToString("R", CultureInfo.InvariantCulture) : l.Qty.ToString("0.###", CultureInfo.InvariantCulture),
                ["purchase_price"] = was is not null && was.Price == l.Price ? l.Price.ToString("R", CultureInfo.InvariantCulture) : l.Price.ToString("0.####", CultureInfo.InvariantCulture),
            };
        }).ToList();
        return new Dictionary<string, object?>
        {
            ["edit_reason"] = (reason ?? "").Trim(),
            ["comment"] = (comment ?? "").Trim(),
            ["items"] = items,
        };
    }

    /// <summary>2026-11-12 (аудит В-3): состав накладной на сервере (свежая карточка) совпадает с отправленным окном (строки с количеством 0 не считаются) — наша правка дошла; иначе версию
    /// изменил кто-то другой. Количество и цена сравниваются с допуском на округление отправки.</summary>
    public static bool SameComposition(IReadOnlyList<SupplierReturnEditLine> server, IReadOnlyList<SupplierReturnEditLine> sent)
    {
        var a = server.Where(l => l.Qty > 0.0005).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
        var b = sent.Where(l => l.Qty > 0.0005).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal) || Math.Abs(a[i].Qty - b[i].Qty) > 0.0005 || Math.Abs(a[i].Price - b[i].Price) > 0.00005)
                return false;
        return true;
    }

    /// <summary>Подпись состава для ключа повтора: тот же состав после обрыва — тот же Idempotency-Key.</summary>
    public static string Signature(string returnId, int version, IReadOnlyList<SupplierReturnEditLine> lines, string reason, string comment) =>
        returnId + "|v" + version + "|" + string.Join(";", lines.Select(l => $"{l.Id}:{l.Qty:0.###}:{l.Price:0.####}"))
        + "|" + (reason ?? "").Trim() + "|" + (comment ?? "").Trim();
}
