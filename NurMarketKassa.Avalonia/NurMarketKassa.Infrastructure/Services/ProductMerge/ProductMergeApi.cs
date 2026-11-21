using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services.ProductMerge;

/// <summary>Чем кончился запрос серверного слияния/отката.</summary>
public enum ServerMergeKind
{
    /// <summary>Выполнено (или, для dry_run, «можно объединить»).</summary>
    Ok,
    /// <summary>409 merge_not_supported: сервер не объединяет эти товары (причины — в <see cref="ServerMergeResponse.Reasons"/>).</summary>
    NotSupported,
    /// <summary>409 undo_blocked: откат невозможен (код отдан другому товару или у главного не хватает остатка).</summary>
    UndoBlocked,
    /// <summary>404/405 на POST main/products/merge/ — старый сервер без серверного слияния: работает запасной ручной путь.</summary>
    Unavailable,
    /// <summary>404 на откате: слияние не найдено или прошло 30 суток.</summary>
    NotFound,
}

/// <summary>Ответ сервера на слияние/откат (контракт Умара 21.11: {merge_id, moved_barcodes, moved_qty, stock_document, archived}).</summary>
public sealed record ServerMergeResponse(ServerMergeKind Kind)
{
    public string? MergeId { get; init; }
    public int MovedBarcodes { get; init; }
    public double MovedQty { get; init; }
    public string? StockDocument { get; init; }

    /// <summary>id товаров, отправленных в архив, если сервер их перечислил; null — перечня нет (считаем, что в архив ушли все источники).</summary>
    public IReadOnlyList<string>? ArchivedIds { get; init; }

    /// <summary>Причины отказа (merge_not_supported / undo_blocked), как их прислал сервер.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    public string Message { get; init; } = "";

    /// <summary>Повтор с тем же Idempotency-Key: сервер вернул прежний результат, второго слияния не было.</summary>
    public bool Replayed { get; init; }

    public static ServerMergeResponse Unavailable { get; } = new(ServerMergeKind.Unavailable);
}

/// <summary>2026-11-21 (ответ Умара 21.11 02:02, «всё в бою»): серверное слияние товаров. POST main/products/merge/ {target, sources:[id…], variant_names:{id:"Название"}, dry_run?};
/// Idempotency-Key обязателен, кроме dry_run. Откат — POST main/products/merge/&lt;merge_id&gt;/undo/ (30 суток). Одна транзакция на сервере: коды источников → доп. коды цели,
/// остаток — дельтой документом склада count_fix с причиной regrade, партии сроков годности → цели, источник в архив. НЕ объединяет (409 merge_not_supported + причины): товары с
/// вариантами (размер/цвет), с партиями-оттенками, с упаковками sale_units, с разными единицами. Поддержку не угадываем по признакам компании: пробуем dry_run, 404/405 = старый сервер,
/// результат запоминается на сессию программы (<see cref="Supported"/>).</summary>
public sealed class ProductMergeApi(NurMarketApiClient api)
{
    /// <summary>Запомненный на сессию итог пробы: true — слияние есть; false — 404/405 (старый сервер); null — ещё не пробовали.</summary>
    public static bool? Supported { get; private set; }

    /// <summary>Только для проверок: забыть итог пробы.</summary>
    public static void ResetSupportCache() => Supported = null;

    private const string Path = "api/main/products/merge/";

    /// <summary>id как число, если это число (сервер ждёт id товара), иначе строкой.</summary>
    private static object IdValue(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : id;

    public async Task<ServerMergeResponse> MergeAsync(string targetId, IReadOnlyList<string> sourceIds, IReadOnlyDictionary<string, string> variantNames, bool dryRun, string? idempotencyKey, CancellationToken ct)
    {
        if (Supported == false)
            return ServerMergeResponse.Unavailable;
        var body = new Dictionary<string, object?>
        {
            ["target"] = IdValue(targetId),
            ["sources"] = sourceIds.Select(IdValue).ToArray(),
        };
        var names = variantNames.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.Ordinal);
        if (names.Count > 0)
            body["variant_names"] = names;
        if (dryRun)
            body["dry_run"] = true;
        try
        {
            JsonElement data;
            if (dryRun)
                data = await api.RequestAsync(HttpMethod.Post, Path, body, null, ct).ConfigureAwait(false);
            else
            {
                if (string.IsNullOrWhiteSpace(idempotencyKey))
                    throw new ArgumentException("Idempotency-Key обязателен (кроме dry_run).", nameof(idempotencyKey));
                data = await api.PostWithIdempotencyKeyAsync(Path, body, idempotencyKey, ct).ConfigureAwait(false);
            }
            Supported = true;
            return Parse(data, ServerMergeKind.Ok);
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 405)
        {
            // 404 с JSON-телом ({"detail":"Not found."}) может значить «нет такого товара», а не «нет адреса»: такой ответ в память сессии не пишем (405 и 404 без JSON — адреса нет)
            if (ex.StatusCode == 405 || ex.Payload is not { ValueKind: JsonValueKind.Object })
                Supported = false;
            PosLogger.Log($"Слияние товаров: сервер не знает POST {Path} ({ex.StatusCode}) — запасной ручной путь.", "INFO");
            return ServerMergeResponse.Unavailable;
        }
        catch (ApiException ex) when (ex.StatusCode == 409 && ServerErrorCodes.Is(ex, "merge_not_supported"))
        {
            Supported = true;
            return Parse(ex.Payload ?? default, ServerMergeKind.NotSupported) with { Message = ex.Message };
        }
    }

    public async Task<ServerMergeResponse> UndoAsync(string mergeId, CancellationToken ct)
    {
        var path = Path + Uri.EscapeDataString(mergeId.Trim()) + "/undo/";
        try
        {
            // ключ — по номеру слияния: повтор отката после обрыва сервер не выполнит второй раз
            var data = await api.PostWithIdempotencyKeyAsync(path, new Dictionary<string, object?>(), "merge-undo-" + mergeId.Trim(), ct).ConfigureAwait(false);
            return Parse(data, ServerMergeKind.Ok) with { MergeId = mergeId };
        }
        catch (ApiException ex) when (ex.StatusCode == 409 && ServerErrorCodes.Is(ex, "undo_blocked"))
        {
            return Parse(ex.Payload ?? default, ServerMergeKind.UndoBlocked) with { Message = ex.Message };
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 410)
        {
            return new ServerMergeResponse(ServerMergeKind.NotFound) { Message = ex.Message };
        }
    }

    // ───────────── разбор ответа ─────────────

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Object => Str(v, "id") ?? Str(v, "number"),
            _ => null,
        } : null;

    /// <summary>Число или длина массива («moved_barcodes» может быть и числом, и списком кодов).</summary>
    private static double CountOrNumber(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.GetArrayLength(),
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : 0,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
    }

    internal static ServerMergeResponse Parse(JsonElement e, ServerMergeKind kind)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return new ServerMergeResponse(kind);
        List<string>? archived = null;
        if (e.TryGetProperty("archived", out var arch) && arch.ValueKind == JsonValueKind.Array)
        {
            archived = [];
            foreach (var a in arch.EnumerateArray())
                if ((a.ValueKind == JsonValueKind.Object ? Str(a, "id") : a.ValueKind is JsonValueKind.String or JsonValueKind.Number ? a.ToString() : null) is { Length: > 0 } id)
                    archived.Add(id);
        }
        var reasons = new List<string>();
        if (e.TryGetProperty("reasons", out var rs))
            CollectReasons(rs, reasons);
        return new ServerMergeResponse(kind)
        {
            MergeId = Str(e, "merge_id") ?? Str(e, "id"),
            MovedBarcodes = (int)CountOrNumber(e, "moved_barcodes"),
            MovedQty = CountOrNumber(e, "moved_qty"),
            StockDocument = Str(e, "stock_document"),
            ArchivedIds = archived,
            Reasons = reasons,
            Replayed = e.TryGetProperty("replayed", out var rp) && rp.ValueKind == JsonValueKind.True,
        };
    }

    /// <summary>Причины могут прийти списком строк, списком объектов ({code, message, product…}) или словарём «товар → причины»: берём всё, что читается как текст.</summary>
    private static void CollectReasons(JsonElement e, List<string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                if (e.GetString() is { Length: > 0 } s && !into.Contains(s))
                    into.Add(s);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray())
                    CollectReasons(x, into);
                break;
            case JsonValueKind.Object:
                var text = Str(e, "code") ?? Str(e, "reason") ?? Str(e, "message") ?? Str(e, "detail");
                if (text is { Length: > 0 })
                {
                    if (!into.Contains(text))
                        into.Add(text);
                }
                else
                    foreach (var p in e.EnumerateObject())
                        CollectReasons(p.Value, into);
                break;
        }
    }
}
