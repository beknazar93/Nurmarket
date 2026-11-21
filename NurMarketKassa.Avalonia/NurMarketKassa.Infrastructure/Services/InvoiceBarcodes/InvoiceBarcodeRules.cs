using System.Globalization;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-11-20 (владелец: «при создании накладных и штрихкоды тоже добавь»; в мобилке то же — invoice_bind.dart / barcode_bind.dart): строка накладной для проверки кода —
/// чем она владеет (основной штрихкод и доп. штрихкоды). <see cref="Key"/> — сама строка окна (чтобы не сравнивать строку с собой).</summary>
public sealed record InvoiceCodeRow(object Key, string Name, string? ProductId, string Barcode, IReadOnlyList<string> Extras);

/// <summary>Чем считанный код мешает строке накладной.</summary>
public enum InvoiceCodeVerdict
{
    /// <summary>Код свободен — можно привязывать.</summary>
    Free,
    /// <summary>Код пустой.</summary>
    Empty,
    /// <summary>Не цифры или не 4–14 знаков.</summary>
    BadFormat,
    /// <summary>Код уже у другой строки этой же накладной (на сервере его ещё нет).</summary>
    DuplicateInInvoice,
    /// <summary>Код уже у другого товара каталога.</summary>
    OwnedByOther,
    /// <summary>Код уже у самого товара строки — записывать нечего.</summary>
    AlreadyOwn,
}

public sealed record InvoiceCodeCheck(InvoiceCodeVerdict Verdict, CatalogProductTileVm? Owner = null, string? RowName = null);

/// <summary>Куда записать считанный код у существующего товара: основной (товар без кода), дополнительный, или уже есть.</summary>
public enum InvoiceBindPlan { Main, Alt, Already }

/// <summary>Скан кода упаковки при приходе: сколько штук добавить и подпись «пачка = N шт.».</summary>
public sealed record InvoicePackHit(AlternateBarcodeVariant Variant, double Quantity, string Note);

/// <summary>2026-11-20: чистая логика штрихкодов в накладных (приход, импорт накладной, возврат) — без окон и сети, проверяется тестами.</summary>
public static class InvoiceBarcodeRules
{
    public const int MinLength = 4;
    public const int MaxLength = 14;

    /// <summary>Код без мусора (пробелы, управляющие символы) — как у поиска кассы.</summary>
    public static string Clean(string? raw) => ProductCodeLookup.Normalize(raw);

    /// <summary>Новый/привязываемый код: только цифры ASCII, 4–14 знаков (как в импорте и карточке товара).</summary>
    public static bool IsValidNew(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length is >= MinLength and <= MaxLength && code.All(char.IsAsciiDigit);

    /// <summary>Два кода — один и тот же: без учёта регистра/пробелов; UPC-A 12 цифр и EAN-13 с нулём впереди — одна запись.</summary>
    public static bool Same(string? a, string? b)
    {
        var x = (a ?? "").Trim();
        var y = (b ?? "").Trim();
        if (x.Length == 0 || y.Length == 0)
            return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
            return true;
        return BarcodeForms.Alternate(x) is { } ax && string.Equals(ax, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>У товара каталога есть этот код — основной или дополнительный.</summary>
    public static bool Owns(CatalogProductTileVm tile, string code) =>
        Same(tile.Barcode, code) || (tile.AlternateBarcodeVariants ?? new List<AlternateBarcodeVariant>()).Any(v => Same(v.Barcode, code));

    /// <summary>Товар каталога с этим кодом (основным или дополнительным) в заданном списке; null — свободен.</summary>
    public static CatalogProductTileVm? OwnerIn(IEnumerable<CatalogProductTileVm> catalog, string code) =>
        catalog.FirstOrDefault(p => Owns(p, code));

    /// <summary>У товара есть хоть какой-то штрихкод (основной или дополнительный).</summary>
    public static bool HasAnyCode(CatalogProductTileVm tile) =>
        !string.IsNullOrWhiteSpace(tile.Barcode) || (tile.AlternateBarcodeVariants ?? new List<AlternateBarcodeVariant>()).Any(v => !string.IsNullOrWhiteSpace(v.Barcode));

    /// <summary>Проверка кода для строки накладной. <paramref name="catalogOwner"/> — товар каталога с этим кодом (null — свободен; ищет вызывающий:
    /// база кассы, затем сервер), <paramref name="rowProductId"/> — товар строки (null — новый), <paramref name="rows"/> — все строки накладной.
    /// Порядок как в мобилке: сначала другие строки этой накладной (их коды ещё не на сервере), потом каталог.</summary>
    public static InvoiceCodeCheck Check(string? raw, string? rowProductId, object? rowKey, IEnumerable<InvoiceCodeRow> rows, CatalogProductTileVm? catalogOwner)
    {
        var code = Clean(raw);
        if (code.Length == 0)
            return new InvoiceCodeCheck(InvoiceCodeVerdict.Empty);
        if (!IsValidNew(code))
            return new InvoiceCodeCheck(InvoiceCodeVerdict.BadFormat);
        var other = rows.FirstOrDefault(r => !ReferenceEquals(r.Key, rowKey) && (Same(r.Barcode, code) || r.Extras.Any(e => Same(e, code))));
        if (other is not null)
            return new InvoiceCodeCheck(InvoiceCodeVerdict.DuplicateInInvoice, RowName: other.Name);
        if (catalogOwner is null)
            return new InvoiceCodeCheck(InvoiceCodeVerdict.Free);
        return !string.IsNullOrWhiteSpace(rowProductId) && string.Equals(catalogOwner.Id, rowProductId, StringComparison.OrdinalIgnoreCase)
            ? new InvoiceCodeCheck(InvoiceCodeVerdict.AlreadyOwn, catalogOwner)
            : new InvoiceCodeCheck(InvoiceCodeVerdict.OwnedByOther, catalogOwner);
    }

    /// <summary>Куда писать код у существующего товара.</summary>
    public static InvoiceBindPlan PlanFor(CatalogProductTileVm tile, string code) =>
        Owns(tile, code) ? InvoiceBindPlan.Already
        : string.IsNullOrWhiteSpace(tile.Barcode) ? InvoiceBindPlan.Main
        : InvoiceBindPlan.Alt;

    /// <summary>Список вариантов с добавленным (тот же код — заменяется: имя и «штук в упаковке» обновляются).</summary>
    public static List<AlternateBarcodeVariant> MergeVariants(IEnumerable<AlternateBarcodeVariant>? current, AlternateBarcodeVariant add)
    {
        var list = (current ?? []).Where(v => !Same(v.Barcode, add.Barcode)).ToList();
        list.Add(add);
        return list;
    }

    /// <summary>Тело поля alternate_barcodes сервера: [{barcode, name, quantity}] (как BulkCardsLoader и карточка товара). 2026-11-21 (ответ Умара): quantity — целое &gt; 0 или null (0 и дробное сервер отклоняет),
    /// name до 255, barcode до 64 знаков.</summary>
    public static object[] ToServerList(IEnumerable<AlternateBarcodeVariant> variants) =>
        variants.Where(v => !string.IsNullOrWhiteSpace(v.Barcode)).Select(v => (object)ToServerItem(v.Barcode, v.Name, v.Quantity)).ToArray();

    public const int ServerBarcodeMax = 64;
    public const int ServerNameMax = 255;

    /// <summary>2026-11-21 (ответ Умара): «штук в упаковке» для сервера — целое &gt; 1 или null (нет упаковки: 0, 1, пусто, не число). Дробное округляется до целого; слишком большое — null.</summary>
    public static long? ServerQuantity(double quantity)
    {
        if (!double.IsFinite(quantity) || quantity > PackScanRules.MaxPackQuantity)
            return null;
        var n = (long)Math.Round(quantity, MidpointRounding.AwayFromZero);
        return n > 1 ? n : null;
    }

    /// <summary>Один элемент alternate_barcodes: barcode (до 64), name (до 255; пусто — ""), quantity (целое или null).</summary>
    public static Dictionary<string, object?> ToServerItem(string? barcode, string? name, double quantity)
    {
        var code = (barcode ?? "").Trim();
        var title = (name ?? "").Trim();
        return new Dictionary<string, object?>
        {
            ["barcode"] = code.Length > ServerBarcodeMax ? code[..ServerBarcodeMax] : code,
            ["name"] = title.Length > ServerNameMax ? title[..ServerNameMax] : title,
            ["quantity"] = ServerQuantity(quantity),
        };
    }

    /// <summary>Скан кода УПАКОВКИ при приходе: у товара есть доп. штрихкод с «штук в упаковке» N &gt; 1 — количество строки = N штук. Продажа по скану упаковки (PackScanRules) не затрагивается.
    /// Весовым товарам, товарам с поштучной продажей из упаковки (склад ведёт упаковками) и основному штрихкоду — null: +1, как раньше.</summary>
    public static InvoicePackHit? PackFor(CatalogProductTileVm tile, string? scanned)
    {
        if (tile is null || string.IsNullOrWhiteSpace(scanned) || tile.MustWeigh || tile.HasPieceOption)
            return null;
        if (Same(tile.Barcode, scanned))
            return null;
        // 2026-11-21 (ответ Умара): сервер доп. штрихкод при приходе НЕ умножает — и у товара с упаковками (sale_units) тоже; прежнее «+1 при скане упаковки» для них было неверным (остаток рос на 1 вместо N).
        // Скан штрихкода упаковки сервера (sale_units[].barcode) — в строку идут базовые единицы: factor штук/м² (qty в базовой единице БЕЗ sale_unit_id — так сервер тоже принимает приход);
        // доп. штрихкод с «штук в упаковке» N — N штук, как у обычного товара (ниже).
        if (ProductSaleUnits.FindByBarcode(scanned) is { } unitHit && string.Equals(unitHit.ProductId, tile.Id, StringComparison.OrdinalIgnoreCase)
            && (double)unitHit.Unit.Factor is var factor && double.IsFinite(factor) && factor > 1d && factor <= PackScanRules.MaxPackQuantity)
        {
            var pieces = Math.Round(factor, 3);
            var unitVariant = new AlternateBarcodeVariant { Barcode = scanned!.Trim(), Name = unitHit.Unit.Name, Quantity = pieces };
            return new InvoicePackHit(unitVariant, pieces, PackNote(pieces, unitHit.Unit.Name));
        }
        var variant = tile.AlternateBarcodeVariants?.FirstOrDefault(v => Same(v.Barcode, scanned));
        if (variant is null)
            return null;
        var n = variant.Quantity;
        if (!double.IsFinite(n) || n <= 1d || n > PackScanRules.MaxPackQuantity)
            return null;
        n = Math.Round(n, 3);
        return new InvoicePackHit(variant, n, PackNote(n, variant.Name));
    }

    /// <summary>2026-11-21: округление «штук в упаковке» до 3 знаков — вынесено из окна (проверка check-money-in-views: расчётов в окнах быть не должно).</summary>
    public static double RoundPackQuantity(double value) => Math.Round(value, 3);

    /// <summary>«пачка = 12 шт.» (или своё имя упаковки варианта: «Коробка = 200 шт.»).</summary>
    public static string PackNote(double pieces, string? name)
    {
        var n = pieces.ToString("0.###", CultureInfo.InvariantCulture);
        var label = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return label is null
            ? Tr.T($"пачка = {n} шт.", $"пачка = {n} даана", $"pack = {n} pcs", $"paket = {n} adet", $"pachka = {n} dona")
            : $"{label} = {n} " + Tr.T("шт.", "даана", "pcs", "adet", "dona");
    }

    /// <summary>Несколько кодов из текста (по строкам, «,» «;» пробелам) — для XML/буфера; пустые и повторы отброшены.</summary>
    public static List<string> SplitCodes(string? text)
    {
        var result = new List<string>();
        foreach (var part in (text ?? "").Split(['\n', '\r', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var code = Clean(part);
            if (code.Length > 0 && !result.Any(c => Same(c, code)))
                result.Add(code);
        }
        return result;
    }

    // ------------------------------------------------------------------ тексты (5 языков)

    /// <summary>Что не так с кодом, словами: пусто — всё в порядке.</summary>
    public static string FormatHint(string? code)
    {
        if (string.IsNullOrEmpty(code) || IsValidNew(code))
            return "";
        if (!code.All(char.IsAsciiDigit))
            return Tr.T("Штрихкод — только цифры (4–14 знаков).", "Штрих-код — цифралар гана (4–14 белги).", "A barcode is digits only (4–14 characters).",
                "Barkod yalnızca rakamlardan oluşur (4–14 karakter).", "Shtrix-kod faqat raqamlardan iborat (4–14 belgi).");
        return Tr.T("Штрихкод — от 4 до 14 цифр (EAN-8, EAN-13, UPC, GTIN-14).", "Штрих-код — 4төн 14кө чейин цифра (EAN-8, EAN-13, UPC, GTIN-14).",
            "A barcode is 4 to 14 digits (EAN-8, EAN-13, UPC, GTIN-14).", "Barkod 4 ile 14 rakam arasındadır (EAN-8, EAN-13, UPC, GTIN-14).",
            "Shtrix-kod 4 dan 14 gacha raqam (EAN-8, EAN-13, UPC, GTIN-14).");
    }

    public static string OwnedByOtherText(string code, string ownerTitle) =>
        Tr.T($"Штрихкод {code} уже у товара «{ownerTitle}».", $"{code} штрихкоду «{ownerTitle}» товарында мурунтан бар.", $"Barcode {code} already belongs to “{ownerTitle}”.",
            $"{code} barkodu zaten «{ownerTitle}» ürününde var.", $"{code} shtrix-kodi «{ownerTitle}» mahsulotida allaqachon bor.");

    public static string DuplicateInInvoiceText(string code, string rowName) =>
        Tr.T($"Штрихкод {code} уже стоит у другой строки этой накладной («{rowName}»). Один код не может быть у двух товаров.",
            $"{code} штрихкоду бул накладнойдун башка сабында («{rowName}») бар. Бир код эки товарда болушу мүмкүн эмес.",
            $"Barcode {code} is already on another line of this invoice (“{rowName}”). One code cannot be on two products.",
            $"{code} barkodu bu faturanın başka satırında zaten var («{rowName}»). Bir kod iki üründe olamaz.",
            $"{code} shtrix-kodi bu yuk xatining boshqa qatorida allaqachon bor («{rowName}»). Bitta kod ikki mahsulotda bo'lishi mumkin emas.");

    /// <summary>Текст проверки кода для окон (ошибка — не пустой; свободный/свой код — пусто).</summary>
    public static string VerdictText(InvoiceCodeCheck check, string code) => check.Verdict switch
    {
        InvoiceCodeVerdict.BadFormat => FormatHint(Clean(code)).Length > 0 ? FormatHint(Clean(code)) : FormatHint("0"),
        InvoiceCodeVerdict.DuplicateInInvoice => DuplicateInInvoiceText(code, check.RowName ?? ""),
        InvoiceCodeVerdict.OwnedByOther => OwnedByOtherText(code, check.Owner?.Title ?? ""),
        _ => "",
    };
}
