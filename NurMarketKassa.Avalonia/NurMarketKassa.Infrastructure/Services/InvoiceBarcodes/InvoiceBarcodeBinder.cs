using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>Итог привязки кода к товару склада.</summary>
/// <summary><see cref="Duplicate"/> (2026-11-21) — сервер ответил 409 barcode_duplicate: код уже у другого товара (product_id, название) — окно спрашивает «выбрать его / оставить / отмена» без поиска по каталогу.</summary>
public sealed record InvoiceBindResult(bool Ok, InvoiceBindPlan Plan, string Message, List<AlternateBarcodeVariant>? Variants = null, ServerBarcodeOwner? Duplicate = null);

/// <summary>Кто владеет кодом: товар каталога (null — свободен) и «сервер не ответил» (тогда проверка неполная — как в мобилке, привязываем без неё).</summary>
public sealed record InvoiceCodeOwner(CatalogProductTileVm? Owner, bool Unavailable);

/// <summary>2026-11-20 (владелец: «при создании накладных и штрихкоды тоже добавь», как в мобилке — invoice_bind.dart): запись штрихкода в товар СКЛАДА из накладной —
/// основной (у товара кода нет) или дополнительный (с названием варианта и «штук в упаковке»). Читает карточку с сервера, пишет PATCH (как BulkBarcodeGenerator и
/// BulkCardsLoader), затем обновляет каталог. Новый товар код не пишет — он уходит в создание (PurchaseReceivingService, ExtraBarcodes строки).</summary>
public static class InvoiceBarcodeBinder
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // Подмены для тестов (без сети и базы кассы).
    internal static Func<string, CancellationToken, Task<InvoiceCodeOwner>>? OwnerOverride;
    internal static Func<string, CancellationToken, Task<JsonElement?>>? ReadCardOverride;
    internal static Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task<string?>>? SaveOverride;
    // 2026-11-21: добавить/убрать ОДИН доп. код (POST/DELETE products/<id>/barcodes/) — в проверках подменяются (productId, вариант/код, версия карточки).
    internal static Func<string, AlternateBarcodeVariant, string?, CancellationToken, Task<BarcodeWriteOutcome>>? AddOverride;
    internal static Func<string, string, string?, CancellationToken, Task<BarcodeWriteOutcome>>? RemoveOverride;

    /// <summary>Кому принадлежит код: сначала база кассы (основной и доп. штрихкоды, размеры), затем сервер. Артикул и весовая этикетка владением не считаются.</summary>
    public static async Task<InvoiceCodeOwner> FindOwnerAsync(string code, CancellationToken ct = default)
    {
        if (OwnerOverride is { } custom)
            return await custom(code, ct).ConfigureAwait(false);
        try
        {
            if (ProductCodeLookup.FindLocal(code, allowWeightLabel: false) is { Kind: not (ProductCodeLookup.HitKind.Article or ProductCodeLookup.HitKind.WeightLabel) } local)
                return new InvoiceCodeOwner(local.Product, false);
            var server = await ProductCodeLookup.FindOnServerAsync(code, ct).ConfigureAwait(true);
            if (server.Hit is { Kind: not (ProductCodeLookup.HitKind.Article or ProductCodeLookup.HitKind.WeightLabel) } hit)
                return new InvoiceCodeOwner(hit.Product, false);
            return new InvoiceCodeOwner(null, server.Unavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Накладная: владелец штрихкода {PurchaseReceivingService.MaskCode(code)} не определён ({ex.GetType().Name}).", "WARNING");
            return new InvoiceCodeOwner(null, true);
        }
    }

    /// <summary>Карточка товара с сервера: основной код и доп. штрихкоды (с названием и количеством). null — карточка не прочиталась (писать нельзя: можно затереть чужое).</summary>
    public static async Task<(string Barcode, List<AlternateBarcodeVariant> Alts)?> ReadCardAsync(string productId, CancellationToken ct = default)
    {
        var full = await ReadCardFullAsync(productId, ct).ConfigureAwait(false);
        return full is null ? null : (full.Barcode, full.Alts);
    }

    /// <summary>2026-11-21: карточка товара с версией (поле <c>version</c> → If-Match при добавлении/удалении одного кода; null — сервер версию не прислал).</summary>
    public sealed record Card(string Barcode, List<AlternateBarcodeVariant> Alts, string? Version);

    public static async Task<Card?> ReadCardFullAsync(string productId, CancellationToken ct = default)
    {
        JsonElement? el = ReadCardOverride is { } custom
            ? await custom(productId, ct).ConfigureAwait(false)
            : await PosApp.CatalogApi.ProductsDetailAsync(productId, ct).ConfigureAwait(false);
        if (el is not { ValueKind: JsonValueKind.Object } card)
            return null;
        return new Card(ParseBarcode(card), ParseAlternates(card), ServerBarcodeErrors.VersionOf(card));
    }

    /// <summary>Основной штрихкод из карточки сервера.</summary>
    public static string ParseBarcode(JsonElement card) =>
        card.TryGetProperty("barcode", out var b) && b.ValueKind is JsonValueKind.String ? (b.GetString() ?? "").Trim() : "";

    /// <summary>Доп. штрихкоды карточки: строки или объекты {barcode, name, quantity}.</summary>
    public static List<AlternateBarcodeVariant> ParseAlternates(JsonElement card)
    {
        var list = new List<AlternateBarcodeVariant>();
        if (!card.TryGetProperty("alternate_barcodes", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                list.Add(new AlternateBarcodeVariant { Barcode = s.Trim() });
            else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("barcode", out var bc))
            {
                var code = bc.ValueKind == JsonValueKind.Number ? bc.GetRawText() : bc.GetString();
                if (string.IsNullOrWhiteSpace(code))
                    continue;
                var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var qty = el.TryGetProperty("quantity", out var q) && JsonNumericReader.TryToDouble(q, out var qv) ? qv : 0;
                list.Add(new AlternateBarcodeVariant { Barcode = code.Trim(), Name = name, Quantity = qty });
            }
        }
        return list;
    }

    /// <summary>Привязать <paramref name="code"/> к товару склада. Нет основного кода и это не упаковка — код становится основным; иначе дописывается в доп. штрихкоды
    /// (<paramref name="variant"/> — название и «штук в упаковке»; null — просто второй код). Код уже есть у товара — ничего не пишется. Владельца кода вызывающий проверил заранее.</summary>
    public static async Task<InvoiceBindResult> BindAsync(string productId, string code, AlternateBarcodeVariant? variant = null, CancellationToken ct = default)
    {
        code = InvoiceBarcodeRules.Clean(code);
        if (!InvoiceBarcodeRules.IsValidNew(code))
            return new InvoiceBindResult(false, InvoiceBindPlan.Main, InvoiceBarcodeRules.FormatHint(code));
        try
        {
            var card = await ReadCardFullAsync(productId, ct).ConfigureAwait(false);
            if (card is null)
                return new InvoiceBindResult(false, InvoiceBindPlan.Main, T("Карточка товара не прочиталась с сервера — код не записан.", "Товардын карточкасы серверден окулган жок — код жазылган жок.",
                    "The product card could not be read from the server — the code was not written.", "Ürün kartı sunucudan okunamadı — kod yazılmadı.", "Mahsulot kartochkasi serverdan o'qilmadi — kod yozilmadi."));
            var (main, alts) = (card.Barcode, card.Alts);
            if (InvoiceBarcodeRules.Same(main, code) || alts.Any(a => InvoiceBarcodeRules.Same(a.Barcode, code)))
                return new InvoiceBindResult(true, InvoiceBindPlan.Already, "", alts);
            InvoiceBindPlan plan;
            if (string.IsNullOrWhiteSpace(main) && variant is null)
            {
                plan = InvoiceBindPlan.Main;
                var (error, owner) = await SaveAsync(productId, new Dictionary<string, object?> { ["barcode"] = code }, ct).ConfigureAwait(false);
                if (error is not null)
                    return new InvoiceBindResult(false, plan, error, Duplicate: owner);
            }
            else
            {
                // 2026-11-21 (ответ Умара): доп. код добавляется ОДНИМ запросом (POST products/<id>/barcodes/, If-Match — версия карточки), остальные коды сервер не трогает;
                // раньше уходил весь список целиком (PATCH alternate_barcodes) — чужие коды, добавленные на другой кассе за это время, терялись.
                plan = InvoiceBindPlan.Alt;
                var add = variant ?? new AlternateBarcodeVariant { Barcode = code };
                var written = await AddOneAsync(productId, add, card.Version, ct).ConfigureAwait(false);
                if (!written.Ok)
                    return new InvoiceBindResult(false, plan, written.Message, Duplicate: written.Duplicate);
                alts = InvoiceBarcodeRules.MergeVariants(alts, add);
            }
            ApplyToCache(productId, plan == InvoiceBindPlan.Main ? code : null, plan == InvoiceBindPlan.Alt ? alts : null);
            PosLogger.Log($"Накладная: товару {productId} записан штрихкод {PurchaseReceivingService.MaskCode(code)} ({(plan == InvoiceBindPlan.Main ? "основной" : "дополнительный")}).", "STOCK");
            return new InvoiceBindResult(true, plan, "", alts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Накладная: штрихкод товару {productId} не записан ({ex.Message}).", "WARNING");
            return new InvoiceBindResult(false, InvoiceBindPlan.Main, T("Сервер не сохранил штрихкод: ", "Сервер штрихкодду сактаган жок: ", "The server didn't save the barcode: ", "Sunucu barkodu kaydetmedi: ", "Server shtrix-kodni saqlamadi: ") + ex.Message);
        }
    }

    /// <summary>Записать доп. штрихкоды товара (окно «Доп. штрихкоды»: добавили, удалили, поправили название/упаковку). Основной код не трогается.
    /// 2026-11-21 (ответ Умара): если известен прежний список (<paramref name="original"/>, прочитанный при открытии окна) и менялся только состав кодов — добавленные уходят POST, убранные — DELETE
    /// (по одному, остальные коды сервер не трогает; <paramref name="version"/> — If-Match). Поправили название/«штук в упаковке» у уже записанного кода — заменяется весь набор (PATCH целиком), как раньше.</summary>
    public static async Task<InvoiceBindResult> SaveAlternatesAsync(string productId, IReadOnlyList<AlternateBarcodeVariant> variants, CancellationToken ct = default,
        IReadOnlyList<AlternateBarcodeVariant>? original = null, string? version = null)
    {
        try
        {
            if (original is not null && TryDiff(original, variants, out var added, out var removed))
            {
                var ver = version;
                foreach (var gone in removed)
                {
                    var res = await RemoveOneAsync(productId, gone.Barcode, ver, ct).ConfigureAwait(false);
                    if (!res.Ok)
                        return await FailedPartialAsync(productId, res, ct).ConfigureAwait(false);
                    ver = null; // версия карточки после первой записи сменилась — дальше без If-Match (каждый запрос меняет один код)
                }
                foreach (var add in added)
                {
                    var res = await AddOneAsync(productId, add, ver, ct).ConfigureAwait(false);
                    if (!res.Ok)
                        return await FailedPartialAsync(productId, res, ct).ConfigureAwait(false);
                    ver = null;
                }
            }
            else
            {
                var (error, owner) = await SaveAsync(productId, new Dictionary<string, object?> { ["alternate_barcodes"] = InvoiceBarcodeRules.ToServerList(variants) }, ct).ConfigureAwait(false);
                if (error is not null)
                    return new InvoiceBindResult(false, InvoiceBindPlan.Alt, error, Duplicate: owner);
            }
            ApplyToCache(productId, null, variants.ToList());
            PosLogger.Log($"Накладная: товару {productId} записан список доп. штрихкодов ({variants.Count}).", "STOCK");
            return new InvoiceBindResult(true, InvoiceBindPlan.Alt, "", variants.ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Накладная: доп. штрихкоды товара {productId} не записаны ({ex.Message}).", "WARNING");
            return new InvoiceBindResult(false, InvoiceBindPlan.Alt, T("Сервер не сохранил штрихкоды: ", "Сервер штрихкоддорду сактаган жок: ", "The server didn't save the barcodes: ", "Sunucu barkodları kaydetmedi: ", "Server shtrix-kodlarni saqlamadi: ") + ex.Message);
        }
    }

    /// <summary>Состав кодов изменился только добавлением/удалением (названия и «штук в упаковке» у оставшихся те же) — тогда годятся POST/DELETE по одному. false — есть правка существующего кода.</summary>
    internal static bool TryDiff(IReadOnlyList<AlternateBarcodeVariant> original, IReadOnlyList<AlternateBarcodeVariant> now, out List<AlternateBarcodeVariant> added, out List<AlternateBarcodeVariant> removed)
    {
        added = now.Where(n => !string.IsNullOrWhiteSpace(n.Barcode) && !original.Any(o => InvoiceBarcodeRules.Same(o.Barcode, n.Barcode))).ToList();
        removed = original.Where(o => !string.IsNullOrWhiteSpace(o.Barcode) && !now.Any(n => InvoiceBarcodeRules.Same(o.Barcode, n.Barcode))).ToList();
        foreach (var n in now.Where(n => !string.IsNullOrWhiteSpace(n.Barcode)))
            if (original.FirstOrDefault(o => InvoiceBarcodeRules.Same(o.Barcode, n.Barcode)) is { } o
                && (!string.Equals((o.Name ?? "").Trim(), (n.Name ?? "").Trim(), StringComparison.Ordinal) || InvoiceBarcodeRules.ServerQuantity(o.Quantity) != InvoiceBarcodeRules.ServerQuantity(n.Quantity)))
                return false;
        return true;
    }

    /// <summary>Часть кодов записана, на одном сервер отказал: каталог перечитывается (в памяти — то, что реально на сервере), окну — сообщение и, если код занят, владелец.</summary>
    private static async Task<InvoiceBindResult> FailedPartialAsync(string productId, BarcodeWriteOutcome res, CancellationToken ct)
    {
        if (SaveOverride is null && AddOverride is null && RemoveOverride is null)
        {
            try { await CatalogCacheService.RefreshProductsAsync([productId]).ConfigureAwait(false); }
            catch (Exception ex) { PosLogger.Log($"Накладная: каталог после частичной записи кодов не обновлён ({ex.Message}).", "WARNING"); }
        }
        return new InvoiceBindResult(false, InvoiceBindPlan.Alt, res.Message, Duplicate: res.Duplicate);
    }

    private static Task<BarcodeWriteOutcome> AddOneAsync(string productId, AlternateBarcodeVariant variant, string? version, CancellationToken ct) =>
        AddOverride is { } add ? add(productId, variant, version, ct) : ProductBarcodeWriter.AddAsync(PosApp.CatalogApi, productId, variant, version, ct);

    private static Task<BarcodeWriteOutcome> RemoveOneAsync(string productId, string barcode, string? version, CancellationToken ct) =>
        RemoveOverride is { } remove ? remove(productId, barcode, version, ct) : ProductBarcodeWriter.RemoveAsync(PosApp.CatalogApi, productId, barcode, version, ct);

    private static async Task<(string? Error, ServerBarcodeOwner? Duplicate)> SaveAsync(string productId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        if (SaveOverride is { } custom)
            return (await custom(productId, fields, ct).ConfigureAwait(false), null);
        var r = await ProductActions.UpdateFieldsAsync(productId, fields, T("штрихкод", "штрих-код", "barcode", "barkod", "shtrix-kod"), null, ct).ConfigureAwait(false);
        return r.Ok ? (null, null) : (r.Message, r.Duplicate);
    }

    /// <summary>Каталог в памяти сразу знает новый код (следующий скан найдёт товар, проверка владельца увидит код); база и сервер догонят обновлением карточки.</summary>
    private static void ApplyToCache(string productId, string? main, List<AlternateBarcodeVariant>? alts)
    {
        if (SaveOverride is not null || AddOverride is not null || RemoveOverride is not null)
            return;
        try
        {
            if (ProductActions.Find(productId) is { } tile)
            {
                if (main is not null)
                    tile.Barcode = main;
                if (alts is not null)
                    tile.AlternateBarcodeVariants = alts.ToList();
            }
            _ = CatalogCacheService.RefreshProductsAsync([productId]);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Накладная: каталог после записи штрихкода не обновлён ({ex.Message}).", "WARNING");
        }
    }
}
