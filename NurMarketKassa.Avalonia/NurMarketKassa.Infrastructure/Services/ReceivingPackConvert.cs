using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-11 (Умар включил на боевом сервере): атомарный перевод остатка товара склада в упаковки — POST main/products/&lt;id&gt;/convert-to-packages/.
/// Сервер под блокировкой товара делит остаток на «штук в упаковке», умножает цену на них и заводит упаковку «штука» (цена штуки = прежняя цена) — гонки с продажами нет
/// (раньше касса читала остаток и слала PATCH packages_input — между чтением и записью могла пройти продажа). expected_quantity — остаток, который кассир видел
/// в подтверждении: другой — 409 stock_changed. Ключ повтора — один на попытку (подтверждение), повтор очередью после обрыва — тот же.
/// Здесь: тело запроса, разбор ответа и отказов, тексты на 5 языках, свежая карточка для подтверждения, варианты округления.</summary>
public static class ReceivingPackConvert
{
    /// <summary>Признак сервера в capabilities (если появится). Адрес вызывается и без признака: 404/405 — откат на прежний PATCH с предупреждением.</summary>
    public const string Capability = "products_convert_to_packages";

    /// <summary>2026-11-11 (сводка Умара): значения по умолчанию в запросе перевода.</summary>
    public const string DefaultUnit = "упак", DefaultPackageName = "Штука";

    public enum Kind
    {
        Ok,
        /// <summary>404/405 — сервер не знает адреса (старый сервер).</summary>
        NotSupported,
        StockChanged,
        NotDivisible,
        AlreadyHasPackages,
        WeightNotSupported,
        VariantsNotSupported,
        HasPromotionTiers,
        InOpenCart,
        PriceOverflow,
        /// <summary>2026-11-11 (сводка Умара): остаток товара отрицательный — переводить в упаковки нечего.</summary>
        NegativeStock,
        Other,
    }

    /// <summary>Итог запроса. <see cref="NewQuantity"/>/<see cref="NewPrice"/> — из ответа успеха (если сервер их вернул); <see cref="CurrentQuantity"/> — из отказа stock_changed.</summary>
    public sealed record Outcome(Kind Kind, double? CurrentQuantity = null, double? NewQuantity = null, double? NewPrice = null, string? ServerMessage = null, int? Status = null);

    private static readonly (string Code, Kind Kind)[] Codes =
    {
        ("stock_changed", Kind.StockChanged),
        ("not_divisible", Kind.NotDivisible),
        ("already_has_packages", Kind.AlreadyHasPackages),
        ("weight_product_not_supported", Kind.WeightNotSupported),
        ("variants_not_supported", Kind.VariantsNotSupported),
        ("has_promotion_tiers", Kind.HasPromotionTiers),
        ("product_in_open_cart", Kind.InOpenCart),
        ("price_overflow", Kind.PriceOverflow),
        ("negative_stock", Kind.NegativeStock), // 2026-11-11 (сводка Умара)
    };

    /// <summary>Тело запроса строки: штук в упаковке, единица, название упаковки, остаток из подтверждения, согласие на округление, ключ. Своей цены упаковки НЕ шлём — её считает сервер.</summary>
    public static Dictionary<string, object?> BuildBody(ReceivingLineVm line, string key)
    {
        var body = new Dictionary<string, object?>
        {
            ["quantity_in_package"] = line.PiecesPerPack,
            ["expected_quantity"] = line.PackConvertStockSeen,
            ["allow_rounding"] = line.PackConvertAllowRounding,
            ["idempotency_key"] = key,
        };
        // 2026-11-11 (сводка Умара): по умолчанию unit «упак» (единица товара после перевода — упаковка) и package_name «Штука» (упаковка поштучной продажи).
        // Раньше шли line.Unit (единица ШТУКИ, «шт») и пустое название — товар после перевода оставался «шт» при остатке в упаковках. Название из строки («Пачка») — как раньше.
        body["unit"] = DefaultUnit;
        body["package_name"] = string.IsNullOrWhiteSpace(line.PackName) ? DefaultPackageName : line.PackName.Trim();
        return body;
    }

    /// <summary>Перевести остаток строки в упаковки. Отказ сервера по существу (4xx с кодом) и 404/405 — <see cref="Outcome"/>; сеть, таймаут, 5xx, 408/429 — исключение
    /// (очередь повторит с ТЕМ ЖЕ ключом). Ключа у строки нет (проведение без окна подтверждения) — создаётся и запоминается в строке.</summary>
    public static async Task<Outcome> ConvertAsync(ICatalogApiService api, ReceivingLineVm line, CancellationToken ct)
    {
        var key = line.PackConvertKey ??= Guid.NewGuid().ToString();
        try
        {
            var response = await api.ConvertToPackagesAsync(line.ProductId!, BuildBody(line, key), key, ct).ConfigureAwait(false);
            return ParseOk(response);
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 405)
        {
            return new Outcome(Kind.NotSupported, Status: ex.StatusCode, ServerMessage: ex.Message);
        }
        catch (ApiException ex) when (ex.StatusCode is >= 400 and < 500 and not 408 and not 429 and not 401)
        {
            return ParseRefusal(ex);
        }
    }

    /// <summary>Ответ успеха: товар целиком или {"product": {...}}; новый остаток и цена — если есть.</summary>
    public static Outcome ParseOk(JsonElement response)
    {
        var product = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object ? p : response;
        return new Outcome(Kind.Ok,
            NewQuantity: Number(response, "new_quantity") ?? Number(product, "quantity"),
            NewPrice: Number(response, "new_price") ?? Number(product, "price"));
    }

    /// <summary>Отказ сервера: код (code / error / error_code / reason, в том числе внутри detail/error) и current_quantity.</summary>
    public static Outcome ParseRefusal(ApiException ex)
    {
        var payload = ex.Payload;
        var code = payload is { } pl ? FindCode(pl) : null;
        code ??= Codes.Select(c => c.Code).FirstOrDefault(c => ex.Message.Contains(c, StringComparison.OrdinalIgnoreCase));
        var kind = Codes.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)).Kind;
        if (code is null)
            kind = Kind.Other;
        double? current = payload is { } p2 ? FindNumber(p2, "current_quantity") : null;
        return new Outcome(kind, CurrentQuantity: current, ServerMessage: ex.Message, Status: ex.StatusCode);
    }

    private static string? FindCode(JsonElement e, int depth = 0)
    {
        if (depth > 3)
            return null;
        if (e.ValueKind == JsonValueKind.String)
            return Codes.FirstOrDefault(c => string.Equals(c.Code, e.GetString()?.Trim(), StringComparison.OrdinalIgnoreCase)).Code;
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
                if (FindCode(x, depth + 1) is { } inArray)
                    return inArray;
            return null;
        }
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in new[] { "code", "error_code", "error", "reason", "type", "status" })
            if (e.TryGetProperty(name, out var v) && FindCode(v, depth + 1) is { } found)
                return found;
        foreach (var name in new[] { "detail", "errors", "non_field_errors" })
            if (e.TryGetProperty(name, out var v) && FindCode(v, depth + 1) is { } found)
                return found;
        return null;
    }

    private static double? FindNumber(JsonElement e, string key, int depth = 0)
    {
        if (depth > 3 || e.ValueKind != JsonValueKind.Object)
            return null;
        if (Number(e, key) is { } n)
            return n;
        foreach (var name in new[] { "detail", "error", "errors", "data" })
            if (e.TryGetProperty(name, out var v) && FindNumber(v, key, depth + 1) is { } inner)
                return inner;
        return null;
    }

    private static double? Number(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d) ? d : null;

    // ------------------------------------------------------------------ свежая карточка (перед подтверждением)

    /// <summary>Остаток и размер упаковки товара по свежей карточке с сервера (не из кэша каталога).</summary>
    public sealed record FreshCard(double Quantity, double PackSize);

    /// <summary>Свежая карточка товара: остаток и первая упаковка (касса, как и чтение каталога, ведёт одну). null — карточку не получили.</summary>
    public static async Task<FreshCard?> ReadFreshAsync(ICatalogApiService api, string productId, CancellationToken ct)
    {
        if (await api.ProductsDetailAsync(productId, ct).ConfigureAwait(false) is not { ValueKind: JsonValueKind.Object } card)
            return null;
        double pack = 0;
        if (card.TryGetProperty("packages", out var pkgs) && pkgs.ValueKind == JsonValueKind.Array)
            foreach (var pkg in pkgs.EnumerateArray())
                if (Number(pkg, "quantity_in_package") is > 0 and var q)
                {
                    pack = q;
                    break;
                }
        return new FreshCard(Number(card, "quantity") ?? 0, pack);
    }

    // ------------------------------------------------------------------ округление

    /// <summary>Остаток (в штуках) не делится на упаковки: точное частное и ближайшее целое (как округлит сервер, точное число — по его ответу).</summary>
    public static (double Exact, double Rounded, double DeltaPieces) RoundingPreview(double stockPieces, double perPack)
    {
        if (perPack <= 0)
            return (0, 0, 0);
        var exact = (decimal)stockPieces / (decimal)perPack;
        var rounded = Math.Round(exact, 0, MidpointRounding.AwayFromZero);
        var delta = rounded * (decimal)perPack - (decimal)stockPieces;
        return ((double)Math.Round(exact, 3, MidpointRounding.AwayFromZero), (double)rounded, (double)delta);
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string NameOf(ReceivingLineVm l) => string.IsNullOrWhiteSpace(l.ProductName) ? l.Barcode : l.ProductName;

    /// <summary>Вопрос «Округлить?»: на сколько изменится остаток.</summary>
    public static string RoundingQuestion(ReceivingLineVm line)
    {
        var stock = line.PackConvertStockSeen;
        var per = line.PiecesPerPack;
        var (exact, rounded, delta) = RoundingPreview(stock, per);
        var sign = delta > 0 ? "+" : "";
        var name = NameOf(line);
        return Tr.T($"{name}: остаток {N(stock)} шт не делится на упаковки по {N(per)} шт ({N(stock)} ÷ {N(per)} = {N(exact)}).\n\nОкруглить до целых упаковок? Остаток станет {N(rounded)} упак. ({N(rounded * per)} шт) — изменится на {sign}{N(delta)} шт. Точное число скажет сервер, оно будет в журнале склада как корректировка.\n\n«Отмена» — ничего не менять (поправьте остаток ревизией или выберите другое «шт в упак.»).",
            $"{name}: калдык {N(stock)} даана {N(per)} даанадан таңгакка бөлүнбөйт ({N(stock)} ÷ {N(per)} = {N(exact)}).\n\nБүтүн таңгакка тегеректейлиби? Калдык {N(rounded)} таңгак ({N(rounded * per)} даана) болот — {sign}{N(delta)} даанага өзгөрөт. Так санды сервер айтат, ал кампанын журналында оңдоо катары жазылат.\n\n«Жокко чыгаруу» — эч нерсе өзгөртпөө (калдыкты ревизия менен оңдоңуз же башка «даана/таңгак» тандаңыз).",
            $"{name}: the stock of {N(stock)} pcs does not divide into packs of {N(per)} ({N(stock)} ÷ {N(per)} = {N(exact)}).\n\nRound to whole packs? The stock will become {N(rounded)} packs ({N(rounded * per)} pcs) — a change of {sign}{N(delta)} pcs. The server gives the exact number; it is logged in the warehouse journal as an adjustment.\n\n“Cancel” — change nothing (fix the stock with a stocktake or choose another “pcs/pack”).",
            $"{name}: {N(stock)} adet stok {N(per)} adetlik kolilere bölünmüyor ({N(stock)} ÷ {N(per)} = {N(exact)}).\n\nTam kolilere yuvarlansın mı? Stok {N(rounded)} koli ({N(rounded * per)} adet) olacak — {sign}{N(delta)} adet değişir. Kesin sayıyı sunucu verir, depo günlüğüne düzeltme olarak yazılır.\n\n«İptal» — hiçbir şey değiştirme (stoğu sayımla düzeltin veya başka «adet/koli» seçin).",
            $"{name}: {N(stock)} dona qoldiq {N(per)} donalik qadoqlarga bo'linmaydi ({N(stock)} ÷ {N(per)} = {N(exact)}).\n\nButun qadoqlarga yaxlitlansinmi? Qoldiq {N(rounded)} qadoq ({N(rounded * per)} dona) bo'ladi — {sign}{N(delta)} donaga o'zgaradi. Aniq sonni server aytadi, u ombor jurnalida tuzatish sifatida yoziladi.\n\n«Bekor qilish» — hech narsani o'zgartirmaslik (qoldiqni inventarizatsiya bilan tuzating yoki boshqa «dona/qadoq» tanlang).");
    }

    /// <summary>Строка пересчитана в упаковки, уже заведённые у товара (already_has_packages).</summary>
    public static string AdoptedText(ReceivingLineVm line, double size) =>
        Tr.T($"{NameOf(line)}: у товара уже есть упаковка по {N(size)} шт (упак. = {N(size)} шт) — строка пересчитана в её упаковки: {N(line.Quantity)} упак. Проверьте количество и цены и проведите ещё раз.",
            $"{NameOf(line)}: товардын {N(size)} даанадан таңгагы бар (таңгак = {N(size)} даана) — сап анын таңгагына кайра эсептелди: {N(line.Quantity)} таңгак. Санын жана бааларын текшерип, кайра өткөрүңүз.",
            $"{NameOf(line)}: the product already has a pack of {N(size)} pcs (pack = {N(size)} pcs) — the line was recalculated into its packs: {N(line.Quantity)} packs. Check the quantity and prices and post again.",
            $"{NameOf(line)}: üründe zaten {N(size)} adetlik koli var (koli = {N(size)} adet) — satır onun kolilerine çevrildi: {N(line.Quantity)} koli. Miktarı ve fiyatları kontrol edip tekrar kaydedin.",
            $"{NameOf(line)}: mahsulotda allaqachon {N(size)} donalik qadoq bor (qadoq = {N(size)} dona) — qator uning qadoqlariga qayta hisoblandi: {N(line.Quantity)} qadoq. Miqdor va narxlarni tekshirib, qayta o'tkazing.");

    /// <summary>Упаковка уже есть, а штуки строки на её упаковки ровно не делятся.</summary>
    public static string CannotAdoptText(ReceivingLineVm line, double size) =>
        Tr.T($"{NameOf(line)}: у товара уже есть упаковка по {N(size)} шт (упак. = {N(size)} шт), а {N(line.Quantity)} × {N(line.PiecesPerPack)} шт на неё ровно не делится. Введите количество в упаковках по {N(size)} шт.",
            $"{NameOf(line)}: товардын {N(size)} даанадан таңгагы бар (таңгак = {N(size)} даана), ал эми {N(line.Quantity)} × {N(line.PiecesPerPack)} даана ага так бөлүнбөйт. Санды {N(size)} даанадан таңгак менен жазыңыз.",
            $"{NameOf(line)}: the product already has a pack of {N(size)} pcs (pack = {N(size)} pcs), and {N(line.Quantity)} × {N(line.PiecesPerPack)} pcs does not divide into it evenly. Enter the quantity in packs of {N(size)} pcs.",
            $"{NameOf(line)}: üründe zaten {N(size)} adetlik koli var (koli = {N(size)} adet), {N(line.Quantity)} × {N(line.PiecesPerPack)} adet ona tam bölünmüyor. Miktarı {N(size)} adetlik kolilerle girin.",
            $"{NameOf(line)}: mahsulotda allaqachon {N(size)} donalik qadoq bor (qadoq = {N(size)} dona), {N(line.Quantity)} × {N(line.PiecesPerPack)} dona unga teng bo'linmaydi. Miqdorni {N(size)} donalik qadoqlarda kiriting.");

    /// <summary>Свежую карточку не получили — подтверждение без неё не показываем.</summary>
    public static string NoFreshCardText(ReceivingLineVm line) =>
        Tr.T($"{NameOf(line)}: не удалось получить свежий остаток с сервера — перевод в упаковки не начат. Проверьте связь и повторите.",
            $"{NameOf(line)}: серверден жаңы калдык алынган жок — таңгакка которуу башталган жок. Байланышты текшерип, кайталаңыз.",
            $"{NameOf(line)}: could not get the current stock from the server — the pack conversion was not started. Check the connection and try again.",
            $"{NameOf(line)}: sunucudan güncel stok alınamadı — koliye çevirme başlatılmadı. Bağlantıyı kontrol edip tekrar deneyin.",
            $"{NameOf(line)}: serverdan joriy qoldiq olinmadi — qadoqqa o'tkazish boshlanmadi. Aloqani tekshirib, qayta urinib ko'ring.");

    /// <summary>Понятная причина отказа сервера (строка не проведена).</summary>
    public static string Describe(Outcome o, ReceivingLineVm line)
    {
        var name = NameOf(line);
        var per = N(line.PiecesPerPack);
        var cur = o.CurrentQuantity is { } c ? N(c) : "?";
        var stock = N(line.PackConvertStockSeen);
        var neg = o.CurrentQuantity is { } nq ? $" ({N(nq)})" : ""; // 2026-11-11: negative_stock — остаток из ответа, если сервер его прислал
        const string notPostedRu = " Строка не проведена.", notPostedKy = " Сап өткөрүлгөн жок.", notPostedEn = " The line was not posted.", notPostedTr = " Satır işlenmedi.", notPostedUz = " Qator o'tkazilmadi.";
        return o.Kind switch
        {
            Kind.StockChanged => Tr.T($"{name}: остаток изменился (стало {cur}) — обновите и повторите.{notPostedRu}",
                $"{name}: калдык өзгөрдү (болду {cur}) — жаңылап, кайталаңыз.{notPostedKy}",
                $"{name}: the stock has changed (now {cur}) — refresh and try again.{notPostedEn}",
                $"{name}: stok değişti (şimdi {cur}) — yenileyip tekrar deneyin.{notPostedTr}",
                $"{name}: qoldiq o'zgardi (endi {cur}) — yangilab, qayta urinib ko'ring.{notPostedUz}"),
            Kind.NotDivisible => Tr.T($"{name}: остаток {stock} не делится на упаковки по {per} шт — проведите ещё раз и выберите «Округлить» или поправьте остаток (ревизия).{notPostedRu}",
                $"{name}: калдык {stock} {per} даанадан таңгакка бөлүнбөйт — кайра өткөрүп, «Тегеректөө» тандаңыз же калдыкты оңдоңуз (ревизия).{notPostedKy}",
                $"{name}: the stock of {stock} does not divide into packs of {per} — post again and choose “Round”, or fix the stock (stocktake).{notPostedEn}",
                $"{name}: {stock} stok {per} adetlik kolilere bölünmüyor — tekrar kaydedip «Yuvarla»yı seçin veya stoğu düzeltin (sayım).{notPostedTr}",
                $"{name}: {stock} qoldiq {per} donalik qadoqlarga bo'linmaydi — qayta o'tkazib «Yaxlitlash»ni tanlang yoki qoldiqni tuzating (inventarizatsiya).{notPostedUz}"),
            Kind.AlreadyHasPackages => Tr.T($"{name}: у товара уже есть упаковка — сменить её из приёмки нельзя. Откройте строку заново: она примет упаковку товара (упак. = N шт).{notPostedRu}",
                $"{name}: товардын таңгагы бар — аны кабыл алуудан өзгөртүүгө болбойт. Сапты кайра ачыңыз: ал товардын таңгагын алат (таңгак = N даана).{notPostedKy}",
                $"{name}: the product already has a pack — it can't be changed from receiving. Open the line again: it will take the product's pack (pack = N pcs).{notPostedEn}",
                $"{name}: üründe zaten koli var — mal kabulden değiştirilemez. Satırı yeniden açın: ürünün kolisini alır (koli = N adet).{notPostedTr}",
                $"{name}: mahsulotda allaqachon qadoq bor — uni qabuldan o'zgartirib bo'lmaydi. Qatorni qayta oching: u mahsulot qadog'ini oladi (qadoq = N dona).{notPostedUz}"),
            Kind.WeightNotSupported => Tr.T($"{name}: весовой товар нельзя перевести в упаковки.{notPostedRu}",
                $"{name}: салмактуу товарды таңгакка которууга болбойт.{notPostedKy}",
                $"{name}: a weighed product can't be converted to packs.{notPostedEn}",
                $"{name}: tartılı ürün koliye çevrilemez.{notPostedTr}",
                $"{name}: vaznli mahsulotni qadoqqa o'tkazib bo'lmaydi.{notPostedUz}"),
            Kind.VariantsNotSupported => Tr.T($"{name}: у товара есть размеры (варианты) — перевести в упаковки нельзя.{notPostedRu}",
                $"{name}: товардын өлчөмдөрү (варианттары) бар — таңгакка которууга болбойт.{notPostedKy}",
                $"{name}: the product has sizes (variants) — it can't be converted to packs.{notPostedEn}",
                $"{name}: ürünün bedenleri (varyantları) var — koliye çevrilemez.{notPostedTr}",
                $"{name}: mahsulotning o'lchamlari (variantlari) bor — qadoqqa o'tkazib bo'lmaydi.{notPostedUz}"),
            Kind.HasPromotionTiers => Tr.T($"{name}: у товара есть цены от количества (акция «больше — дешевле») — сначала уберите их в карточке товара.{notPostedRu}",
                $"{name}: товардын санга жараша баалары бар (акция «көбүрөөк — арзаныраак») — адегенде аларды товардын картасынан алып салыңыз.{notPostedKy}",
                $"{name}: the product has quantity price tiers (a “buy more, pay less” promotion) — remove them in the product card first.{notPostedEn}",
                $"{name}: ürünün miktara bağlı fiyatları var («çok al az öde» kampanyası) — önce ürün kartında kaldırın.{notPostedTr}",
                $"{name}: mahsulotning miqdorga qarab narxlari bor («ko'proq — arzonroq» aksiyasi) — avval ularni mahsulot kartasida olib tashlang.{notPostedUz}"),
            Kind.InOpenCart => Tr.T($"{name}: товар сейчас в открытом чеке на кассе — закройте или отложите чек и повторите.{notPostedRu}",
                $"{name}: товар азыр кассадагы ачык чекте — чекти жаап же кийинкиге калтырып, кайталаңыз.{notPostedKy}",
                $"{name}: the product is in an open receipt at a till right now — close or park the receipt and try again.{notPostedEn}",
                $"{name}: ürün şu an kasada açık bir fişte — fişi kapatın veya bekletin ve tekrar deneyin.{notPostedTr}",
                $"{name}: mahsulot hozir kassadagi ochiq chekda — chekni yoping yoki keyinga qoldiring va qayta urinib ko'ring.{notPostedUz}"),
            Kind.PriceOverflow => Tr.T($"{name}: цена упаковки (цена × {per}) больше предела сервера — уменьшите «шт в упак.» или цену.{notPostedRu}",
                $"{name}: таңгактын баасы (баа × {per}) сервердин чегинен ашат — «даана/таңгакты» же бааны азайтыңыз.{notPostedKy}",
                $"{name}: the pack price (price × {per}) exceeds the server limit — reduce “pcs/pack” or the price.{notPostedEn}",
                $"{name}: koli fiyatı (fiyat × {per}) sunucu sınırını aşıyor — «adet/koli»yi veya fiyatı azaltın.{notPostedTr}",
                $"{name}: qadoq narxi (narx × {per}) server chegarasidan oshadi — «dona/qadoq»ni yoki narxni kamaytiring.{notPostedUz}"),
            // 2026-11-11 (сводка Умара): negative_stock — остаток в минусе, делить на упаковки нечего
            Kind.NegativeStock => Tr.T($"{name}: остаток товара на сервере в минусе{neg} — перевести его в упаковки нельзя. Сначала поправьте остаток (ревизия), потом проведите ещё раз.{notPostedRu}",
                $"{name}: сервердеги товардын калдыгы минуста{neg} — аны таңгакка которууга болбойт. Адегенде калдыкты оңдоп (ревизия), анан кайра өткөрүңүз.{notPostedKy}",
                $"{name}: the product's stock on the server is negative{neg} — it can't be converted to packs. Fix the stock first (stocktake), then post again.{notPostedEn}",
                $"{name}: ürünün sunucudaki stoğu ekside{neg} — koliye çevrilemez. Önce stoğu düzeltin (sayım), sonra tekrar kaydedin.{notPostedTr}",
                $"{name}: serverdagi mahsulot qoldig'i minusda{neg} — uni qadoqqa o'tkazib bo'lmaydi. Avval qoldiqni tuzating (inventarizatsiya), keyin qayta o'tkazing.{notPostedUz}"),
            _ => Tr.T($"{name}: сервер не перевёл остаток в упаковки — {o.ServerMessage}.{notPostedRu}",
                $"{name}: сервер калдыкты таңгакка которгон жок — {o.ServerMessage}.{notPostedKy}",
                $"{name}: the server did not convert the stock to packs — {o.ServerMessage}.{notPostedEn}",
                $"{name}: sunucu stoğu koliye çevirmedi — {o.ServerMessage}.{notPostedTr}",
                $"{name}: server qoldiqni qadoqqa o'tkazmadi — {o.ServerMessage}.{notPostedUz}"),
        };
    }

    /// <summary>Предупреждение: сервер не знает convert-to-packages — сработал прежний способ (чтение остатка + PATCH).</summary>
    public static string FallbackWarning(ReceivingLineVm line) =>
        Tr.T($"{NameOf(line)}: сервер пока не умеет переводить остаток в упаковки одной операцией — сработал прежний способ. Если в эти секунды была продажа, проверьте остаток товара.",
            $"{NameOf(line)}: сервер калдыкты таңгакка бир операция менен которууну азырынча билбейт — мурунку ыкма иштеди. Ушул секундаларда сатуу болсо, товардын калдыгын текшериңиз.",
            $"{NameOf(line)}: the server can't convert the stock to packs in one operation yet — the old method was used. If a sale happened in those seconds, check the product's stock.",
            $"{NameOf(line)}: sunucu stoğu tek işlemde koliye çeviremiyor — eski yöntem kullanıldı. O saniyelerde satış olduysa ürünün stoğunu kontrol edin.",
            $"{NameOf(line)}: server qoldiqni bitta amalda qadoqqa o'tkaza olmaydi — eski usul ishlatildi. Shu soniyalarda sotuv bo'lgan bo'lsa, mahsulot qoldig'ini tekshiring.");
}
