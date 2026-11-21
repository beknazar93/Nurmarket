using System.Globalization;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): правка СУЩЕСТВУЮЩИХ строк возврата поставщику — количество, цена закупки, скидка
// строки, примечание; комментарий документа и обязательная причина правки. Расчёт и проверка — здесь (не во view): сумма строки = кол-во × цена − скидка,
// до копейки от нуля (ReturnLineMath). Тело PATCH — полный новый состав по контракту из ТЗ Умару (п. 4, не подтверждён сервером): строка с количеством 0
// не отправляется (= удалена из возврата); хотя бы одна строка должна остаться — иначе возврат аннулируют кнопкой «Удалить».
// 2026-11-11 (реальный контракт Умара): тело {version, edit_reason, comment?, items:[{id, qty, purchase_price}]}; СКИДКА И ПРИМЕЧАНИЕ СТРОКИ СЕРВЕР НЕ ХРАНИТ —
// не отправляются и в сумму не входят (сумма строки = кол-во × цена); отсутствующие строки сервер удаляет.
// 2026-11-21 (ответ Умара 21.11, п. 5; прежнее «new_lines_not_allowed» устарело): сервер принимает в PATCH новые строки без id — {product, qty, purchase_price?, receipt_item?}, меняет reason и compensation
// (откатывает старую компенсацию по старой сумме и проводит новую); дату менять нельзя (400 field_not_editable). Владелец/админ правит и после 24 ч (owner_override).

/// <summary>Строка в окне правки (значения уже разобраны из полей ввода).</summary>
public sealed record SupplierReturnEditLine(string Id, string ProductId, string Name, string Unit, double Qty, double Price, double Discount, string Note, string Article = "", string Barcode = "", // 2026-11-12 (аудит Н-3): артикул и штрихкод — для поиска в длинной накладной
    // 2026-11-21 (ответ Умара 21.11, п. 5): IsNew — строка добавлена в возврат при правке (id у неё ещё нет, в PATCH уходит {product, qty, purchase_price?}); PriceSet — цену ввели руками
    // (false: purchase_price не отправляется, сервер возьмёт цену из строки прихода или закупочную товара).
    bool IsNew = false, bool PriceSet = true)
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
            if (l.IsNew && l.Qty <= 0)
                continue; // 2026-11-21: добавленная строка с количеством 0 в PATCH не уходит
            if (was is not null && Math.Abs(was.Qty - l.Qty) <= 0.0005 && Math.Abs(was.Price - l.Price) <= 0.00005 && (l.Qty <= 0 || l.Id.Length > 0))
                continue; // строка не тронута — не проверяем (проверять изменения, а не исходные данные сервера)
            var wholeOnly = l.IsPiece && !(l.Unit.Trim().Length == 0 && was is not null && Math.Abs(was.Qty - Math.Round(was.Qty)) > 0.0001);
            var name = l.Name.Length > 0 ? l.Name : "—";
            string? problem = l.Qty < 0 ? Tr.T("количество не может быть меньше 0", "саны 0дөн аз болбойт", "the quantity cannot be below 0", "miktar 0'dan az olamaz", "miqdor 0 dan kam bo'lmaydi")
                : l.Qty > 0 && wholeOnly && Math.Abs(l.Qty - Math.Round(l.Qty)) > 0.0001 ? Tr.T("для штучного товара — целое число", "даана товар үчүн — бүтүн сан", "a whole number for piece goods", "adetli ürün için tam sayı", "donali mahsulot uchun — butun son")
                : l.Price < 0 ? Tr.T("цена не может быть меньше 0", "баасы 0дөн аз болбойт", "the price cannot be below 0", "fiyat 0'dan az olamaz", "narx 0 dan kam bo'lmaydi")
                // 2026-11-11: скидка строки не проверяется — сервер её не хранит и окно её не отправляет; id строки обязателен (иначе 400 new_lines_not_allowed)
                : l.IsNew && l.ProductId.Length == 0 ? Tr.T("у добавленной строки нет товара", "кошулган сапта товар жок", "the added line has no product", "eklenen satırda ürün yok", "qo'shilgan qatorda mahsulot yo'q")
                : l.Qty > 0 && l.Id.Length == 0 && !l.IsNew ? Tr.T("сервер не прислал id строки — правка невозможна, обновите список", "сервер саптын id жөнөткөн жок — оңдоо мүмкүн эмес, тизмени жаңыртыңыз",
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
    /// <param name="originalReturnReason">2026-11-21: причина и вид компенсации САМОГО возврата (reason / compensation) — их тоже можно менять; null — не сравниваются.</param>
    public static bool HasChanges(IReadOnlyList<SupplierReturnEditLine> original, IReadOnlyList<SupplierReturnEditLine> edited, string originalComment, string comment,
        string? originalReturnReason = null, string? returnReason = null, string? originalCompensation = null, string? compensation = null)
    {
        edited = edited.Where(l => !(l.IsNew && l.Qty <= 0)).ToList(); // добавленная строка с количеством 0 — как будто её нет
        if (!string.Equals((originalComment ?? "").Trim(), (comment ?? "").Trim(), StringComparison.Ordinal) || original.Count != edited.Count)
            return true;
        if (edited.Any(l => l.IsNew))
            return true;
        if (returnReason is not null && !string.Equals((originalReturnReason ?? "").Trim(), returnReason.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;
        if (compensation is not null && !string.Equals((originalCompensation ?? "").Trim(), compensation.Trim(), StringComparison.OrdinalIgnoreCase))
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

    /// <summary>2026-11-21 (владелец: «редактор возвраты поставщикам нету!»): предпросмотр «что изменится» до PIN — по строкам окна и виду компенсации возврата (cash / debt_offset / none).
    /// Склад: сколько вернётся на склад или уйдёт со склада по каждой изменённой строке; деньги/долг: на сколько изменится сумма, которую поставщик вернул в кассу или зачёл в долг.
    /// Это ОЖИДАНИЕ по правилам возврата — окончательно склад, кассу и долг пересчитывает сервер. Пусто — изменений нет. Строки списка — в том же порядке и числе, что исходные.</summary>
    /// <param name="newCompensation">2026-11-21 (ответ Умара 21.11): вид компенсации изменён — сервер откатывает прежнюю по старой сумме и проводит новую по новой; null или тот же — не менялся.</param>
    public static IReadOnlyList<string> Preview(IReadOnlyList<SupplierReturnEditLine> original, IReadOnlyList<SupplierReturnEditLine> edited, string compensation, string? newCompensation = null)
    {
        var result = new List<string>();
        var stock = new List<string>();
        // 2026-11-21: добавленные строки (в конце списка) — товар уйдёт со склада целиком; без цены — сервер возьмёт её из строки прихода или закупочную товара
        var added = edited.Skip(original.Count).Where(l => l.IsNew && l.Qty > 0.0005).ToList();
        for (var i = 0; i < edited.Count; i++)
        {
            var was = i < original.Count ? original[i] : edited[i] with { Qty = 0 };
            var now = edited[i];
            if (i >= original.Count && !(now.IsNew && now.Qty > 0.0005))
                continue;
            var delta = was.Qty - now.Qty; // >0 — в возврате стало меньше: товар вернётся на склад
            if (Math.Abs(delta) <= 0.0005)
                continue;
            var name = was.Name.Length > 0 ? was.Name : "—";
            var n = Math.Abs(delta).ToString("0.###", CultureInfo.CurrentCulture) + (was.Unit.Length > 0 ? " " + was.Unit : "");
            stock.Add(delta > 0
                ? Tr.T($"«{name}»: +{n} вернётся на склад", $"«{name}»: +{n} кампага кайтат", $"“{name}”: +{n} returns to stock", $"«{name}»: +{n} stoka döner", $"«{name}»: +{n} omborga qaytadi")
                : Tr.T($"«{name}»: −{n} уйдёт со склада", $"«{name}»: −{n} кампадан чыгат", $"“{name}”: −{n} leaves stock", $"«{name}»: −{n} stoktan çıkar", $"«{name}»: −{n} ombordan chiqadi"));
        }
        const int Max = 5;
        foreach (var s in stock.Take(Max))
            result.Add(Tr.T("Склад: ", "Кампа: ", "Stock: ", "Stok: ", "Ombor: ") + s);
        if (stock.Count > Max)
            result.Add(Tr.T($"Склад: и ещё {stock.Count - Max} строк(и)", $"Кампа: дагы {stock.Count - Max} сап", $"Stock: and {stock.Count - Max} more line(s)", $"Stok: ve {stock.Count - Max} satır daha", $"Ombor: yana {stock.Count - Max} qator"));
        var oldTotal = Total(original);
        var newTotal = Total(edited);
        var diff = newTotal - oldTotal;
        var oldComp = (compensation ?? "").Trim().ToLowerInvariant();
        var newComp = (newCompensation ?? compensation ?? "").Trim().ToLowerInvariant();
        if (oldComp != newComp)
        {
            // 2026-11-21: вид компенсации меняется — сервер откатывает прежнюю по старой сумме и проводит новую по новой (а не разницу)
            var o = MoneyText.Som(oldTotal);
            var n = MoneyText.Som(newTotal);
            result.Add(oldComp switch
            {
                "cash" => Tr.T($"Откатится прежнее: из кассы уйдёт {o} (деньги, что вернул поставщик)", $"Мурункусу жокко чыгат: кассадан {o} кетет (жеткирүүчү кайтарган акча)", $"Rolled back: {o} leaves the till (the money the supplier returned)", $"Eski işlem geri alınır: kasadan {o} çıkar (tedarikçinin iade ettiği para)", $"Avvalgisi bekor qilinadi: kassadan {o} chiqadi (yetkazib beruvchi qaytargan pul)"),
                "debt_offset" => Tr.T($"Откатится прежнее: долг поставщику вырастет на {o} (зачёт отменится)", $"Мурункусу жокко чыгат: жеткирүүчүгө карыз {o} көбөйөт (эсептөө жокко чыгат)", $"Rolled back: the supplier debt grows by {o} (the offset is cancelled)", $"Eski işlem geri alınır: tedarikçi borcu {o} artar (mahsup iptal edilir)", $"Avvalgisi bekor qilinadi: yetkazib beruvchiga qarz {o} ga ko'payadi (hisob bekor qilinadi)"),
                _ => Tr.T("Откатится прежнее: деньги и долг не затрагивались", "Мурункусу жокко чыгат: акча жана карыз өзгөргөн эмес", "Rolled back: money and debt were not affected", "Eski işlem geri alınır: para ve borç etkilenmemişti", "Avvalgisi bekor qilinadi: pul va qarz o'zgarmagan edi"),
            });
            result.Add(newComp switch
            {
                "cash" => Tr.T($"Проведётся новое: в кассу поставщик вернёт {n}", $"Жаңысы өткөрүлөт: жеткирүүчү кассага {n} кайтарат", $"Posted anew: the supplier gives {n} back to the till", $"Yenisi işlenir: tedarikçi kasaya {n} öder", $"Yangisi o'tkaziladi: yetkazib beruvchi kassaga {n} qaytaradi"),
                "debt_offset" => Tr.T($"Проведётся новое: долг поставщику уменьшится на {n} (зачёт)", $"Жаңысы өткөрүлөт: жеткирүүчүгө карыз {n} азаят (эсептөө)", $"Posted anew: the supplier debt drops by {n} (offset)", $"Yenisi işlenir: tedarikçi borcu {n} azalır (mahsup)", $"Yangisi o'tkaziladi: yetkazib beruvchiga qarz {n} ga kamayadi (hisob)"),
                _ => Tr.T("Проведётся новое: только со склада, деньги и долг не затрагиваются", "Жаңысы өткөрүлөт: кампадан гана, акча жана карыз өзгөрбөйт", "Posted anew: from stock only, money and debt are not affected", "Yenisi işlenir: yalnızca stoktan, para ve borç etkilenmez", "Yangisi o'tkaziladi: faqat ombordan, pul va qarz o'zgarmaydi"),
            });
        }
        else if (Math.Abs(diff) > 0.004)
        {
            var d = MoneyText.Som(Math.Abs(diff));
            var less = diff < 0;
            result.Add((compensation ?? "").Trim().ToLowerInvariant() switch
            {
                "cash" => less
                    ? Tr.T($"Деньги: в кассу поставщик вернёт на {d} меньше", $"Акча: жеткирүүчү кассага {d} аз кайтарат", $"Money: the supplier gives {d} less back to the till", $"Para: tedarikçi kasaya {d} daha az öder", $"Pul: yetkazib beruvchi kassaga {d} kam qaytaradi")
                    : Tr.T($"Деньги: в кассу поставщик вернёт на {d} больше", $"Акча: жеткирүүчү кассага {d} көп кайтарат", $"Money: the supplier gives {d} more back to the till", $"Para: tedarikçi kasaya {d} daha fazla öder", $"Pul: yetkazib beruvchi kassaga {d} ko'p qaytaradi"),
                "debt_offset" => less
                    ? Tr.T($"Долг поставщику: зачтётся на {d} меньше — долг станет больше", $"Жеткирүүчүгө карыз: {d} аз эсептелет — карыз көбөйөт", $"Supplier debt: {d} less is offset — the debt grows", $"Tedarikçi borcu: {d} daha az mahsup edilir — borç artar", $"Yetkazib beruvchiga qarz: {d} kam hisoblanadi — qarz ko'payadi")
                    : Tr.T($"Долг поставщику: зачтётся на {d} больше — долг станет меньше", $"Жеткирүүчүгө карыз: {d} көп эсептелет — карыз азаят", $"Supplier debt: {d} more is offset — the debt shrinks", $"Tedarikçi borcu: {d} daha fazla mahsup edilir — borç azalır", $"Yetkazib beruvchiga qarz: {d} ko'p hisoblanadi — qarz kamayadi"),
                _ => Tr.T("Деньги и долг не затрагиваются: возврат был только со склада", "Акча жана карыз өзгөрбөйт: кайтаруу кампадан гана болгон", "Money and debt are not affected: the return was from stock only",
                    "Para ve borç etkilenmez: iade yalnızca stoktan yapılmıştı", "Pul va qarz o'zgarmaydi: qaytarish faqat ombordan edi"),
            });
        }
        if (added.Any(l => !l.PriceSet && l.Price <= 0))
            result.Add(Tr.T("Цену добавленных строк без цены сервер возьмёт из строки прихода или закупочную товара — сумма уточнится после сохранения",
                "Баасы жок кошулган саптардын баасын сервер кирүү сабынан же товардын сатып алуу баасынан алат — сумма сактагандан кийин такталат",
                "For added lines without a price the server takes the price from the receipt line or the product's purchase price — the total is refined after saving",
                "Fiyatsız eklenen satırların fiyatını sunucu giriş satırından veya ürünün alış fiyatından alır — tutar kaydettikten sonra netleşir",
                "Narxsiz qo'shilgan qatorlarning narxini server kirim qatoridan yoki mahsulotning xarid narxidan oladi — summa saqlagandan keyin aniqlanadi"));
        return result;
    }

    /// <summary>Тело PATCH suppliers/returns/{id}/ (версия добавляется в ReturnEditApi.PatchAsync).</summary>
    /// <param name="original">2026-11-12 (аудит В-4): исходные строки — у нетронутой строки уходят исходные количество и цена до последнего знака (формат «R»), а не округлённые до 3–4 знаков
    /// (цена упаковки 33,3333 не превращается в 33,33 у строк, которые никто не трогал).</param>
    /// <param name="returnReason">2026-11-21 (ответ Умара 21.11): новая причина САМОГО возврата (reason: defect/surplus/…); null — не менялась и в тело не идёт (edit_reason — причина правки — идёт всегда).</param>
    /// <param name="compensation">Новый вид компенсации (cash / debt_offset / none); null — не менялся. Сервер откатывает старую и проводит новую.</param>
    public static Dictionary<string, object?> BuildPatchBody(IReadOnlyList<SupplierReturnEditLine> lines, string reason, string comment, IReadOnlyList<SupplierReturnEditLine>? original = null,
        string? returnReason = null, string? compensation = null)
    {
        var originalById = original?.Where(o => o.Id.Length > 0).GroupBy(o => o.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var items = lines.Where(l => l.Qty > 0).Select(l =>
        {
            if (l.IsNew)
            {
                // 2026-11-21: новая строка — {product, qty, purchase_price?}; без цены сервер берёт её из строки прихода или закупочную товара (receipt_item касса не знает — не отправляется)
                var added = new Dictionary<string, object?> { ["product"] = l.ProductId, ["qty"] = l.Qty.ToString("0.###", CultureInfo.InvariantCulture) };
                if (l.PriceSet)
                    added["purchase_price"] = l.Price.ToString("0.####", CultureInfo.InvariantCulture);
                return added;
            }
            // 2026-11-11 (реальный контракт): ровно {id, qty, purchase_price} — без скидки, примечания и product_id
            var was = originalById is not null && originalById.TryGetValue(l.Id, out var o) ? o : null;
            return new Dictionary<string, object?>
            {
                ["id"] = l.Id,
                ["qty"] = was is not null && was.Qty == l.Qty ? l.Qty.ToString("R", CultureInfo.InvariantCulture) : l.Qty.ToString("0.###", CultureInfo.InvariantCulture),
                ["purchase_price"] = was is not null && was.Price == l.Price ? l.Price.ToString("R", CultureInfo.InvariantCulture) : l.Price.ToString("0.####", CultureInfo.InvariantCulture),
            };
        }).ToList();
        var body = new Dictionary<string, object?>
        {
            ["edit_reason"] = (reason ?? "").Trim(),
            ["comment"] = (comment ?? "").Trim(),
            ["items"] = items,
        };
        if (!string.IsNullOrWhiteSpace(returnReason))
            body["reason"] = returnReason.Trim();
        if (!string.IsNullOrWhiteSpace(compensation))
            body["compensation"] = compensation.Trim();
        return body;
    }

    /// <summary>2026-11-12 (аудит В-3): состав накладной на сервере (свежая карточка) совпадает с отправленным окном (строки с количеством 0 не считаются) — наша правка дошла; иначе версию
    /// изменил кто-то другой. Количество и цена сравниваются с допуском на округление отправки.</summary>
    public static bool SameComposition(IReadOnlyList<SupplierReturnEditLine> server, IReadOnlyList<SupplierReturnEditLine> sent)
    {
        var b = sent.Where(l => l.Qty > 0.0005 && !l.IsNew).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
        var newSent = sent.Where(l => l.Qty > 0.0005 && l.IsNew).ToList();
        var sentIds = new HashSet<string>(b.Select(l => l.Id), StringComparer.Ordinal);
        var a = server.Where(l => l.Qty > 0.0005 && sentIds.Contains(l.Id)).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
        // 2026-11-21: добавленные строки у сервера получили свои id — сверяем число и набор товаров/количеств среди «лишних» серверных строк
        var serverExtra = server.Where(l => l.Qty > 0.0005 && !sentIds.Contains(l.Id)).ToList();
        if (serverExtra.Count != newSent.Count
            || !serverExtra.Select(l => l.ProductId + ":" + l.Qty.ToString("0.###", CultureInfo.InvariantCulture)).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(newSent.Select(l => l.ProductId + ":" + l.Qty.ToString("0.###", CultureInfo.InvariantCulture)).OrderBy(x => x, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase))
            return false;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal) || Math.Abs(a[i].Qty - b[i].Qty) > 0.0005 || Math.Abs(a[i].Price - b[i].Price) > 0.00005)
                return false;
        return true;
    }

    /// <summary>Подпись состава для ключа повтора: тот же состав после обрыва — тот же Idempotency-Key.</summary>
    public static string Signature(string returnId, int version, IReadOnlyList<SupplierReturnEditLine> lines, string reason, string comment, string? returnReason = null, string? compensation = null) =>
        returnId + "|v" + version + "|" + string.Join(";", lines.Select(l => $"{(l.IsNew ? "new-" + l.ProductId + (l.PriceSet ? "" : "~") : l.Id)}:{l.Qty:0.###}:{l.Price:0.####}"))
        + "|" + (reason ?? "").Trim() + "|" + (comment ?? "").Trim()
        + (string.IsNullOrWhiteSpace(returnReason) && string.IsNullOrWhiteSpace(compensation) ? "" : "|" + returnReason + "|" + compensation); // 2026-11-21: причина/вид компенсации возврата — часть состава
}
