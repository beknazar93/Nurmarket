using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-06 (просьба владельца: «удаление товара даже если что-то связано через спец код, и как это отразится в аналитике/сводке»): единый путь удаления товара
// в «Складе» и в программе владельца. Здесь — всё, что не зависит от окон: счётчики истории из ответа 409, связи товара, тексты предпросмотра последствий и сам
// шаг «удалить → сервер отказал из-за истории → один вопрос про архив → архив». Окно (WarehouseWindow.ProductActions) только показывает диалоги и зовёт этот класс.
// Правило сервера (Django ProtectedError: товар с приходами/продажами/перемещениями не удаляется) не обходим — вместо удаления предлагаем архив (признак product_archive).

/// <summary>Сколько документов ссылается на товар — из ответа 409 <c>product_has_history</c> (<c>data.receipts / sales / transfers / supplier_returns / purchase_orders</c>).</summary>
public sealed record ProductHistoryCounts(int Receipts, int Sales, int Transfers, int SupplierReturns, int PurchaseOrders)
{
    public static readonly ProductHistoryCounts Empty = new(0, 0, 0, 0, 0);

    public int Total => Receipts + Sales + Transfers + SupplierReturns + PurchaseOrders;

    public static ProductHistoryCounts FromError(ApiException ex) => FromPayload(ex.Payload);

    public static ProductHistoryCounts FromPayload(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } p || !p.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
            return Empty;
        return new ProductHistoryCounts(Num(d, "receipts"), Num(d, "sales"), Num(d, "transfers"), Num(d, "supplier_returns"), Num(d, "purchase_orders"));
    }

    private static int Num(JsonElement d, string key)
    {
        if (!d.TryGetProperty(key, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return Math.Max(0, n);
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s))
            return Math.Max(0, s);
        return 0;
    }

    /// <summary>«приходов: 1, продаж: 3» — только ненулевые; пусто, если ничего нет.</summary>
    public string ListText()
    {
        var parts = new List<string>();
        if (Receipts > 0) parts.Add(Tr.T($"приходов: {Receipts}", $"кирешелер: {Receipts}", $"receipts: {Receipts}", $"girişler: {Receipts}", $"kirimlar: {Receipts}"));
        if (Sales > 0) parts.Add(Tr.T($"продаж: {Sales}", $"сатуулар: {Sales}", $"sales: {Sales}", $"satışlar: {Sales}", $"sotuvlar: {Sales}"));
        if (Transfers > 0) parts.Add(Tr.T($"перемещений: {Transfers}", $"жылдыруулар: {Transfers}", $"transfers: {Transfers}", $"transferler: {Transfers}", $"ko'chirishlar: {Transfers}"));
        if (SupplierReturns > 0) parts.Add(Tr.T($"возвратов поставщику: {SupplierReturns}", $"жеткирүүчүгө кайтаруулар: {SupplierReturns}", $"supplier returns: {SupplierReturns}", $"tedarikçi iadeleri: {SupplierReturns}", $"yetkazib beruvchiga qaytarishlar: {SupplierReturns}"));
        if (PurchaseOrders > 0) parts.Add(Tr.T($"заказов поставщику: {PurchaseOrders}", $"жеткирүүчүгө буйрутмалар: {PurchaseOrders}", $"purchase orders: {PurchaseOrders}", $"tedarikçi siparişleri: {PurchaseOrders}", $"yetkazib beruvchiga buyurtmalar: {PurchaseOrders}"));
        return string.Join(", ", parts);
    }
}

/// <summary>Что связано с товаром на этой кассе (считает окно по локальным данным): комплекты, размеры/цвета, доп. штрихкоды, коды (PLU/артикул), весы.</summary>
public sealed record ProductLinks(
    IReadOnlyList<string> InBundles,
    bool IsBundle,
    int Variants,
    int AlternateBarcodes,
    IReadOnlyList<string> Scales,
    string? Codes)
{
    public static readonly ProductLinks None = new(Array.Empty<string>(), false, 0, 0, Array.Empty<string>(), null);

    public bool HasAny => InBundles.Count > 0 || IsBundle || Variants > 0 || AlternateBarcodes > 0 || Scales.Count > 0 || !string.IsNullOrWhiteSpace(Codes);
}

/// <summary>Всё, что нужно для предпросмотра последствий удаления.</summary>
public sealed record ProductRemovalInput(string Title, double Stock, string StockText, double PurchasePrice, double SalePrice, ProductLinks Links);

public enum ProductRemovalOutcome
{
    /// <summary>Сервер удалил товар.</summary>
    Deleted,
    /// <summary>Сервер отказал из-за истории, владелец согласился — товар в архиве.</summary>
    Archived,
    /// <summary>Сервер отказал из-за истории, владелец отказался от архива — товар остался.</summary>
    ArchiveDeclined,
    /// <summary>Сервер не удалил из-за ссылок, а архива нет (нет признака product_archive / ответ не «product_has_history») — нужен совет, что делать.</summary>
    ArchiveUnavailable,
    /// <summary>Ни один путь удаления не принят сервером (404/405).</summary>
    NotAccepted,
    /// <summary>Архив не удался (ошибка в <see cref="ProductRemovalResult.Error"/>).</summary>
    ArchiveFailed,
}

public sealed record ProductRemovalResult(ProductRemovalOutcome Outcome, ProductHistoryCounts Counts, Exception? Error = null);

public static class ProductRemoval
{
    /// <summary>2026-10-30: ответ сервера «нельзя удалить — на объект ссылаются другие записи» (ProtectedError): 409 либо текст/тело с «ссылаются», «protected», «referenced», «foreign key», «has_references».</summary>
    public static bool IsInUseError(ApiException ex)
    {
        if (ex.StatusCode == 409)
            return true;
        var text = ex.Message + " " + (ex.Payload?.ToString() ?? "");
        return text.Contains("ссылаются", StringComparison.OrdinalIgnoreCase)
            || text.Contains("protected", StringComparison.OrdinalIgnoreCase)
            || text.Contains("referenced", StringComparison.OrdinalIgnoreCase)
            || text.Contains("foreign key", StringComparison.OrdinalIgnoreCase)
            || text.Contains("has_references", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Единый путь на сервере: DELETE → (409 product_has_history) → признак product_archive → ОДИН вопрос «в архив?» → POST archive.
    /// <paramref name="archiveSupported"/> — есть ли у сервера признак product_archive (ошибка проверки = нет); <paramref name="confirmArchive"/> — вопрос владельцу со счётчиками.
    /// Местное удаление, журнал и обновление каталога — на вызывающем (по исходу).</summary>
    public static async Task<ProductRemovalResult> RunAsync(ICatalogApiService api, string productId,
        Func<CancellationToken, Task<bool>> archiveSupported, Func<ProductHistoryCounts, Task<bool>> confirmArchive, CancellationToken ct = default)
    {
        ProductHistoryCounts counts;
        try
        {
            return await api.DeleteProductAsync(productId, ct).ConfigureAwait(true)
                ? new ProductRemovalResult(ProductRemovalOutcome.Deleted, ProductHistoryCounts.Empty)
                : new ProductRemovalResult(ProductRemovalOutcome.NotAccepted, ProductHistoryCounts.Empty);
        }
        catch (ApiException ex) when (IsInUseError(ex))
        {
            counts = ProductHistoryCounts.FromError(ex);
            if (SupplierApiService.ErrorCode(ex) != "product_has_history")
                return new ProductRemovalResult(ProductRemovalOutcome.ArchiveUnavailable, counts, ex);
        }

        bool supported;
        try { supported = await archiveSupported(ct).ConfigureAwait(true); }
        catch (Exception cex) when (cex is not OutOfMemoryException and not OperationCanceledException)
        {
            PosLogger.Log($"Склад: признак product_archive не получен ({cex.Message}).", "WARNING");
            supported = false;
        }
        if (!supported)
            return new ProductRemovalResult(ProductRemovalOutcome.ArchiveUnavailable, counts);
        if (!await confirmArchive(counts).ConfigureAwait(true))
            return new ProductRemovalResult(ProductRemovalOutcome.ArchiveDeclined, counts);
        try
        {
            await api.ArchiveProductAsync(productId, ct).ConfigureAwait(true);
            return new ProductRemovalResult(ProductRemovalOutcome.Archived, counts);
        }
        catch (Exception aex) when (aex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new ProductRemovalResult(ProductRemovalOutcome.ArchiveFailed, counts, aex);
        }
    }

    // ───────────── тексты (5 языков) ─────────────

    /// <summary>Сумма остатка по закупке (остаток × закупочная цена); закупки нет — 0.</summary>
    public static double StockCost(double stock, double purchasePrice) => stock > 0.0005 && purchasePrice > 0 ? stock * purchasePrice : 0;

    /// <summary>Честная строка про аналитику и сводку — одна для предпросмотра и для итога.</summary>
    public static string AnalyticsNote(double stock, double purchasePrice, double salePrice)
    {
        var baseText = Tr.T(
            "История продаж и приходов сохранится. Из текущих остатков, «Заканчивается», «Нет продаж» и топов нового периода товар исчезнет.",
            "Сатуулар жана кирешелер тарыхы сакталат. Азыркы калдыктардан, «Бүтүп баратат», «Сатуу жок» жана жаңы мезгилдин топторунан товар жоголот.",
            "Sales and receipt history is kept. The product disappears from current stock, “Running low”, “No sales” and the tops of a new period.",
            "Satış ve giriş geçmişi korunur. Ürün güncel stoktan, «Azalıyor», «Satış yok» ve yeni dönemin listelerinden kaybolur.",
            "Sotuvlar va kirimlar tarixi saqlanadi. Mahsulot joriy qoldiqlardan, «Tugayapti», «Sotuv yo'q» va yangi davr topidan yo'qoladi.");
        if (stock <= 0.0005)
            return baseText;
        var parts = new List<string>();
        if (purchasePrice > 0)
        {
            var cost = MoneyText.Som(StockCost(stock, purchasePrice));
            parts.Add(Tr.T($"«Склад по закупке» уменьшится на {cost}", $"«Сатып алуу боюнча кампа» {cost} азаят", $"“Stock at cost” will drop by {cost}", $"«Alış değerli depo» {cost} azalır", $"«Xarid bo'yicha ombor» {cost} ga kamayadi"));
        }
        if (salePrice > 0)
        {
            var value = MoneyText.Som(stock * salePrice);
            parts.Add(Tr.T($"«по продаже» — на {value}", $"«сатуу боюнча» — {value}", $"“at retail” by {value}", $"«satış değerli» {value}", $"«sotuv bo'yicha» — {value} ga"));
        }
        var loss = purchasePrice > 0
            ? Tr.T($"списание остатка — убыток {MoneyText.Som(StockCost(stock, purchasePrice))} в «Списаниях» смены и аналитики (уменьшит прибыль периода)",
                   $"калдыкты эсептен чыгаруу — {MoneyText.Som(StockCost(stock, purchasePrice))} чыгым (смена жана аналитиканын «Эсептен чыгаруулар» бөлүмүндө, мезгилдин пайдасын азайтат)",
                   $"the stock write-off is a {MoneyText.Som(StockCost(stock, purchasePrice))} loss in the shift and analytics “Write-offs” (lowers the period's profit)",
                   $"stok düşümü {MoneyText.Som(StockCost(stock, purchasePrice))} zarardır: vardiya ve analizin «Düşümler» bölümünde (dönem kârını azaltır)",
                   $"qoldiqni hisobdan chiqarish — {MoneyText.Som(StockCost(stock, purchasePrice))} zarar: smena va tahlilning «Hisobdan chiqarishlar» bo'limida (davr foydasini kamaytiradi)")
            : Tr.T("закупочная цена не указана — убыток от списания посчитается по цене продажи, проверьте закупку в карточке",
                   "сатып алуу баасы көрсөтүлгөн эмес — чыгым сатуу баасы боюнча эсептелет, карточкадагы баасын текшериңиз",
                   "no purchase price — the write-off loss will be counted at the sale price, check the card",
                   "alış fiyatı yok — düşüm zararı satış fiyatıyla hesaplanır, kartı kontrol edin",
                   "xarid narxi yo'q — hisobdan chiqarish zarari sotuv narxi bo'yicha hisoblanadi, kartani tekshiring");
        parts.Add(loss);
        return baseText + " " + string.Join("; ", parts) + ".";
    }

    /// <summary>Текст связей товара (комплекты, размеры, коды, весы); пусто — связей нет.</summary>
    public static string LinksText(ProductLinks links)
    {
        var lines = new List<string>();
        if (links.InBundles.Count > 0)
        {
            var names = string.Join(", ", links.InBundles.Take(5).Select(n => "«" + n + "»")) + (links.InBundles.Count > 5 ? $" +{links.InBundles.Count - 5}" : "");
            lines.Add(Tr.T($"Входит в комплекты: {names}. Комплекты с этим товаром придётся поправить: откройте комплект и уберите товар из состава (касса сама состав не меняет).",
                $"Топтомдордун курамында: {names}. Бул товары бар топтомдорду оңдоо керек: топтомду ачып, товарды курамдан алып салыңыз (касса курамды өзү өзгөртпөйт).",
                $"Part of bundles: {names}. Those bundles will need fixing: open the bundle and remove the product from its composition (the till does not change the composition by itself).",
                $"Setlerin içinde: {names}. Bu setleri düzeltmeniz gerekir: seti açıp ürünü bileşimden çıkarın (kasa bileşimi kendisi değiştirmez).",
                $"To'plamlar tarkibida: {names}. Bu to'plamlarni tuzatish kerak: to'plamni ochib, mahsulotni tarkibdan olib tashlang (kassa tarkibni o'zi o'zgartirmaydi)."));
        }
        if (links.IsBundle)
            lines.Add(Tr.T("Это комплект: его состав пропадёт вместе с ним, товары состава останутся.", "Бул топтом: курамы аны менен жоголот, курамындагы товарлар калат.",
                "This is a bundle: its composition goes with it, the component products stay.", "Bu bir set: bileşimi onunla gider, bileşen ürünler kalır.", "Bu to'plam: tarkibi u bilan yo'qoladi, tarkibdagi mahsulotlar qoladi."));
        if (links.Variants > 0)
            lines.Add(Tr.T($"Размеры и цвета ({links.Variants}) уйдут вместе с товаром.", $"Өлчөмдөр жана түстөр ({links.Variants}) товар менен кетет.",
                $"Sizes and colours ({links.Variants}) go with the product.", $"Beden ve renkler ({links.Variants}) ürünle birlikte gider.", $"O'lcham va ranglar ({links.Variants}) mahsulot bilan ketadi."));
        if (links.AlternateBarcodes > 0)
            lines.Add(Tr.T($"Доп. штрихкоды ({links.AlternateBarcodes}) перестанут находить товар на кассе.", $"Кошумча штрихкоддор ({links.AlternateBarcodes}) кассада товарды таппай калат.",
                $"Extra barcodes ({links.AlternateBarcodes}) will no longer find the product at the till.", $"Ek barkodlar ({links.AlternateBarcodes}) kasada ürünü artık bulmaz.", $"Qo'shimcha shtrix-kodlar ({links.AlternateBarcodes}) kassada mahsulotni topmaydi."));
        if (!string.IsNullOrWhiteSpace(links.Codes))
            lines.Add(Tr.T($"Коды товара ({links.Codes}) освободятся: по ним он больше не найдётся.", $"Товардын коддору ({links.Codes}) бошойт: алар боюнча ал табылбайт.",
                $"The product codes ({links.Codes}) become free: it can no longer be found by them.", $"Ürün kodları ({links.Codes}) boşalır: bunlarla artık bulunmaz.", $"Mahsulot kodlari ({links.Codes}) bo'shaydi: ular bo'yicha topilmaydi."));
        if (links.Scales.Count > 0)
        {
            var scales = string.Join(", ", links.Scales.Take(4).Select(n => "«" + n + "»"));
            lines.Add(Tr.T($"Товар есть в списке весов {scales}: после удаления обновите весы (PLU и клавиша останутся на весах, пока не отправите новый список).",
                $"Товар тараза тизмесинде {scales}: өчүргөндөн кийин таразаларды жаңыртыңыз (жаңы тизме жөнөтмөйүнчө PLU жана баскыч таразада калат).",
                $"The product is in the scale list {scales}: update the scales after deletion (its PLU and key stay on the scale until you send a new list).",
                $"Ürün terazi listesinde {scales}: silindikten sonra terazileri güncelleyin (yeni liste gönderilene kadar PLU ve tuş terazide kalır).",
                $"Mahsulot tarozi ro'yxatida {scales}: o'chirgandan keyin tarozilarni yangilang (yangi ro'yxat yuborilmaguncha PLU va tugma tarozida qoladi)."));
        }
        return string.Join("\n", lines);
    }

    /// <summary>Предпросмотр последствий — один текст в окне подтверждения «Удалить».</summary>
    public static string PreviewText(ProductRemovalInput i)
    {
        var lines = new List<string>
        {
            Tr.T($"Удалить «{i.Title}»? Товар исчезнет из каталога NurCRM и из кассы.", $"«{i.Title}» өчүрүлсүнбү? Товар NurCRM каталогунан жана кассадан жоголот.",
                $"Delete “{i.Title}”? The product will be removed from the NurCRM catalog and from the till.", $"«{i.Title}» silinsin mi? Ürün NurCRM kataloğundan ve kasadan kaldırılacak.", $"«{i.Title}» o'chirilsinmi? Mahsulot NurCRM katalogidan va kassadan yo'qoladi."),
        };
        if (i.Stock > 0.0005)
            lines.Add(Tr.T($"Остаток {i.StockText} будет списан актом «Списание» (остаток станет 0) — это убыток по закупочной цене, а не продажа.",
                $"Калдык {i.StockText} «Эсептен чыгаруу» актысы менен эсептен чыгарылат (калдык 0 болот) — бул сатып алуу баасы боюнча чыгым, сатуу эмес.",
                $"The stock of {i.StockText} will be written off with a “Write-off” document (stock becomes 0) — a loss at purchase price, not a sale.",
                $"{i.StockText} stok «Düşüm» belgesiyle düşülecek (stok 0 olur) — alış fiyatıyla zarar, satış değil.",
                $"{i.StockText} qoldiq «Hisobdan chiqarish» akti bilan chiqariladi (qoldiq 0 bo'ladi) — xarid narxi bo'yicha zarar, sotuv emas."));
        var links = LinksText(i.Links);
        if (links.Length > 0)
            lines.Add(links);
        lines.Add(Tr.T("Если по товару есть приходы, продажи или перемещения, сервер его не удалит — тогда предложим отправить в архив (товар скроется, история останется).",
            "Эгер товар боюнча кирешелер, сатуулар же жылдыруулар болсо, сервер аны өчүрбөйт — анда архивге жиберүүнү сунуштайбыз (товар жашырылат, тарых калат).",
            "If the product has receipts, sales or transfers, the server will not delete it — then we will offer the archive (the product is hidden, the history stays).",
            "Üründe giriş, satış veya transfer varsa sunucu silmez — o zaman arşivi öneririz (ürün gizlenir, geçmiş kalır).",
            "Mahsulotda kirim, sotuv yoki ko'chirish bo'lsa, server uni o'chirmaydi — unda arxivni taklif qilamiz (mahsulot yashiriladi, tarix qoladi)."));
        lines.Add(AnalyticsNote(i.Stock, i.PurchasePrice, i.SalePrice));
        return string.Join("\n\n", lines);
    }

    /// <summary>Единственный вопрос про архив (после отказа сервера): счётчики документов + остаток, который исчезнет, + строка про аналитику.</summary>
    public static string ArchiveText(string title, ProductHistoryCounts counts, double stockLeft, double purchasePrice, double salePrice, bool stockWrittenOff)
    {
        var list = counts.ListText();
        var withList = list.Length > 0 ? " (" + list + ")" : "";
        var text = Tr.T($"По «{title}» есть документы{withList}, поэтому сервер его не удаляет. Отправить товар в архив? Он пропадёт из каталога, продаж и поиска, а история приходов и продаж останется.",
            $"«{title}» боюнча документтер бар{withList}, ошондуктан сервер аны өчүрбөйт. Товарды архивге жиберүүбү? Ал каталогдон, сатуудан жана издөөдөн жоголот, ал эми кирешелер менен сатуулардын тарыхы калат.",
            $"“{title}” has documents{withList}, so the server does not delete it. Send the product to the archive? It disappears from the catalog, sales and search, while the receipt and sales history stays.",
            $"«{title}» için belgeler var{withList}, bu yüzden sunucu silmez. Ürün arşive gönderilsin mi? Katalogdan, satışlardan ve aramadan kaybolur, giriş ve satış geçmişi kalır.",
            $"«{title}» bo'yicha hujjatlar bor{withList}, shuning uchun server uni o'chirmaydi. Mahsulot arxivga yuborilsinmi? U katalog, sotuv va qidiruvdan yo'qoladi, kirim va sotuvlar tarixi qoladi.");
        if (stockWrittenOff)
            text += " " + Tr.T("Остаток уже списан актом.", "Калдык актыны менен эсептен чыгарылган.", "The stock has already been written off.", "Stok zaten belgeyle düşüldü.", "Qoldiq allaqachon akt bilan chiqarilgan.");
        else if (stockLeft > 0.0005)
            text += " " + Tr.T($"Внимание: на складе числится {stockLeft:0.###} — этот остаток исчезнет из кассы.", $"Көңүл буруңуз: кампада {stockLeft:0.###} бар — бул калдык кассадан жоголот.",
                $"Note: {stockLeft:0.###} is in stock — it will disappear from the till.", $"Dikkat: stokta {stockLeft:0.###} var — bu stok kasadan kaybolur.", $"Diqqat: omborda {stockLeft:0.###} bor — bu qoldiq kassadan yo'qoladi.");
        return text + "\n\n" + AnalyticsNote(stockWrittenOff ? 0 : stockLeft, purchasePrice, salePrice);
    }

    /// <summary>Архива нет (сервер без product_archive) или сервер отказал иначе: объясняем, что делать, без тупика.</summary>
    public static string NoArchiveText(string title, ProductHistoryCounts counts, bool stockWrittenOff)
    {
        var list = counts.ListText();
        var withList = list.Length > 0 ? " (" + list + ")" : "";
        var stockPart = stockWrittenOff
            ? Tr.T("Остаток уже списан — товар остался с нулём на складе. ", "Калдык эсептен чыгарылган — товар кампада нөл калдык менен калды. ", "The stock is already written off — the product stays with zero stock. ", "Stok zaten düşüldü — ürün sıfır stokla kaldı. ", "Qoldiq allaqachon chiqarilgan — mahsulot nol qoldiq bilan qoldi. ")
            : "";
        return Tr.T($"«{title}» нельзя удалить: по нему есть история{withList}, и сервер её сохраняет. Архива на этом сервере нет, поэтому: {stockPart}обнулите остаток кнопкой «Остаток» и переименуйте товар (например, «ЯЯЯ не использовать»), чтобы он не мешал. История продаж и приходов сохранится.",
            $"«{title}» товарын өчүрүүгө болбойт: ал боюнча тарых бар{withList} жана сервер аны сактайт. Бул серверде архив жок, ошондуктан: {stockPart}«Калдык» баскычы менен калдыкты нөлгө түшүрүп, товардын атын өзгөртүңүз (мисалы, «ЯЯЯ колдонулбайт») — ал тоскоол болбойт. Сатуулар менен кирешелердин тарыхы калат.",
            $"“{title}” can't be deleted: it has history{withList} and the server keeps it. This server has no archive, so: {stockPart}zero the stock with the “Stock” button and rename the product (for example “ZZZ do not use”) so it stays out of the way. The sales and receipt history is kept.",
            $"«{title}» silinemez: geçmişi var{withList} ve sunucu bunu saklar. Bu sunucuda arşiv yok, bu yüzden: {stockPart}«Stok» düğmesiyle stoğu sıfırlayın ve ürünü yeniden adlandırın (örneğin «ZZZ kullanma») — engel olmaz. Satış ve giriş geçmişi korunur.",
            $"«{title}» mahsulotini o'chirib bo'lmaydi: uning tarixi bor{withList} va server uni saqlaydi. Bu serverda arxiv yo'q, shuning uchun: {stockPart}«Qoldiq» tugmasi bilan qoldiqni nolga tushiring va mahsulot nomini o'zgartiring (masalan, «YYY ishlatilmaydi») — u xalaqit bermaydi. Sotuvlar va kirimlar tarixi qoladi.");
    }

    /// <summary>Итоговое сообщение после архива: что произошло и как это видно в аналитике.</summary>
    public static string ArchivedDoneText() => Tr.T("Товар отправлен в архив. История сохранена; из остатков и «Заканчивается» он исчез.",
        "Товар архивге жөнөтүлдү. Тарых сакталды; калдыктардан жана «Бүтүп баратат» бөлүмүнөн жоголду.",
        "The product was sent to the archive. History is kept; it is gone from stock and “Running low”.",
        "Ürün arşive gönderildi. Geçmiş korundu; stoktan ve «Azalıyor» listesinden çıktı.",
        "Mahsulot arxivga yuborildi. Tarix saqlandi; qoldiqlar va «Tugayapti» dan yo'qoldi.");

    public static string DeletedDoneText() => Tr.T("Товар удалён. История продаж и приходов сохранена; из остатков он исчез.",
        "Товар өчүрүлдү. Сатуулар жана кирешелер тарыхы сакталды; калдыктардан жоголду.",
        "Product deleted. Sales and receipt history is kept; it is gone from stock.",
        "Ürün silindi. Satış ve giriş geçmişi korundu; stoktan çıktı.",
        "Mahsulot o'chirildi. Sotuvlar va kirimlar tarixi saqlandi; qoldiqlardan yo'qoldi.");
}
