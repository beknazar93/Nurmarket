using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-27, ответ сервера (Умар) на «цена продажи меняется сама»: контракт цен NurCRM.
/// • Б-06: PATCH purchase_price не меняет price; пересчёт — только "recalc_price": true; в ответе PATCH/PUT есть price_changed.
/// • Б-03: в ответе pos/checkout есть price_mismatch: [{index, product_id, name, sent, current}]; с заголовком
///   X-NurMarket-Strict-Price: 1 чек с устаревшей ценой не проводится (409, code "price_changed"); "price_override": true в строке
///   — цена изменена кассиром намеренно.
/// • Б-46: лента изменений каталога (см. CatalogFeedSync).
/// На боевом сервере это может быть ещё не выложено, поэтому всё здесь — «если поле пришло»: нет поля / нет адреса / обычный ответ —
/// прежнее поведение кассы без ошибок.
/// Настройки — необязательный файл server_prices.json в папке данных программы (у кассы и владельца свои):
/// {"strict_price": true, "catalog_feed": true, "feed_interval_sec": 8}. Файла нет или он не читается — значения по умолчанию.
/// </summary>
public static class ServerPriceContract
{
    public const string StrictPriceHeader = "X-NurMarket-Strict-Price";

    /// <summary>Путь к файлу настроек. Стенд подменяет, чтобы не трогать настоящий профиль.</summary>
    public static string SettingsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        "server_prices.json");

    private static readonly object Gate = new();
    private static bool _loaded;
    private static bool _strict = true;
    private static bool _feed = true;
    private static int _feedSeconds = 8;

    /// <summary>Слать заголовок строгой проверки цены на pos/checkout (по умолчанию да). Старый сервер заголовок игнорирует.</summary>
    public static bool StrictPriceEnabled
    {
        get { Load(); return _strict; }
        set { Load(); _strict = value; }
    }

    /// <summary>Опрос ленты изменений каталога (по умолчанию да; нет адреса на сервере — касса сама откатывается на полную загрузку).</summary>
    public static bool CatalogFeedEnabled
    {
        get { Load(); return _feed; }
        set { Load(); _feed = value; }
    }

    /// <summary>Период опроса ленты, секунд (по ответу сервера — раз в 5–10 с; здесь 5–60).</summary>
    public static int FeedIntervalSeconds
    {
        get { Load(); return _feedSeconds; }
        set { Load(); _feedSeconds = Math.Clamp(value, 5, 60); }
    }

    private static void Load()
    {
        lock (Gate)
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                if (!File.Exists(SettingsPath))
                    return;
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return;
                if (doc.RootElement.TryGetProperty("strict_price", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _strict = s.GetBoolean();
                if (doc.RootElement.TryGetProperty("catalog_feed", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _feed = f.GetBoolean();
                if (doc.RootElement.TryGetProperty("feed_interval_sec", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var sec))
                    _feedSeconds = Math.Clamp(sec, 5, 60);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"PRICE_SYNC: server_prices.json не прочитан ({ex.Message}) — настройки по умолчанию.", "PRICE_SYNC");
            }
        }
    }

    // ---------------------------------------------------------------- price_changed (Б-06)

    /// <summary>Флаг price_changed из ответа PATCH/PUT/прихода: true/false — сервер сказал, менялась ли цена продажи; null — поля нет
    /// (старый сервер). Ищем на верхнем уровне и в product/data/sale (сервер мог вложить товар).</summary>
    public static bool? PriceChangedOf(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
            return null;
        if (ReadBool(response, "price_changed") is { } direct)
            return direct;
        foreach (var key in new[] { "product", "data", "result" })
            if (response.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.Object && ReadBool(nested, "price_changed") is { } inner)
                return inner;
        // Приход: price_changed мог прийти по строкам — «менялась» если хоть одна true, «не менялась» если есть строки и все false.
        foreach (var key in new[] { "items", "results" })
        {
            if (!response.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            bool? any = null;
            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || ReadBool(row, "price_changed") is not { } b)
                    continue;
                if (b)
                    return true;
                any = false;
            }

            if (any is not null)
                return any;
        }

        return null;
    }

    private static bool? ReadBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) ? el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;

    /// <summary>Сервер хотя бы раз прислал price_changed в этом запуске — значит, Б-06 на нём выложен (PATCH закупки цену не меняет).</summary>
    public static bool ServerKeepsSalePrice { get; private set; }

    /// <summary>Запоминает, что сервер умеет price_changed (вызывать с любым ответом PATCH/PUT/прихода).</summary>
    public static void Observe(JsonElement response)
    {
        if (PriceChangedOf(response) is not null && !ServerKeepsSalePrice)
        {
            ServerKeepsSalePrice = true;
            PosLogger.Log("PRICE_SYNC: сервер присылает price_changed — «возврат цены» после прихода больше не нужен.", "PRICE_SYNC");
        }
    }

    // ---------------------------------------------------------------- price_mismatch / 409 price_changed (Б-03)

    /// <summary>409 с кодом price_changed — строгая проверка цены отказала в продаже. Обычный 409 «запрос с этим ключом ещё выполняется» — нет.</summary>
    public static bool IsPriceChanged(ApiException ex)
    {
        if (ex.StatusCode != 409 || ex.Payload is not { } payload)
            return false;
        try
        {
            return payload.GetRawText().Contains("price_changed", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>price_mismatch из тела ответа/ошибки: на верхнем уровне или внутри detail/error/errors/data. Пусто — поля нет.</summary>
    public static List<ServerPriceMismatch> ParseMismatch(JsonElement? body)
    {
        var list = new List<ServerPriceMismatch>();
        if (body is not { ValueKind: JsonValueKind.Object } root)
            return list;
        var arr = FindArray(root, "price_mismatch", 0);
        if (arr is not { ValueKind: JsonValueKind.Array })
            return list;
        var n = 0;
        foreach (var row in arr.Value.EnumerateArray())
        {
            n++;
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            var id = ReadText(row, "product_id") ?? ReadText(row, "product") ?? ReadText(row, "id") ?? "";
            var index = row.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number && ix.TryGetInt32(out var iv) ? iv : n - 1;
            list.Add(new ServerPriceMismatch(
                index,
                id,
                ReadText(row, "name") ?? id,
                ReadMoney(row, "sent") ?? 0,
                ReadMoney(row, "current") ?? 0));
        }

        return list;
    }

    private static JsonElement? FindArray(JsonElement obj, string name, int depth)
    {
        if (obj.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.Array)
            return direct;
        if (depth >= 2)
            return null;
        foreach (var prop in obj.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Object && FindArray(prop.Value, name, depth + 1) is { } found)
                return found;
        return null;
    }

    private static string? ReadText(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el)
            ? el.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(el.GetString()) ? null : el.GetString()!.Trim(),
                JsonValueKind.Number => el.GetRawText(),
                _ => null,
            }
            : null;

    private static double? ReadMoney(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
            return d;
        if (el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x))
            return x;
        return null;
    }

    private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Строка для журнала PRICE_SYNC: «Хлеб (id) отправлено 205, сейчас 195».</summary>
    public static string Describe(IEnumerable<ServerPriceMismatch> mismatches) =>
        string.Join("; ", mismatches.Select(m => $"«{m.Name}» ({m.ProductId}) отправлено {Fmt(m.Sent)}, сейчас {Fmt(m.Current)}"));

    /// <summary>Тост после проведённой продажи: сервер заметил расхождение цены (до трёх позиций, остальные — «и ещё N»).</summary>
    public static string MismatchToast(IReadOnlyList<ServerPriceMismatch> mismatches)
    {
        var shown = string.Join("; ", mismatches.Take(3).Select(m => $"{m.Name} {Fmt(m.Sent)} → {Fmt(m.Current)}"));
        var more = mismatches.Count > 3 ? mismatches.Count - 3 : 0;
        var tail = more > 0 ? $" +{more}" : "";
        return Tr.T(
            $"Продажа проведена, но цена в чеке отличалась от текущей: {shown}{tail} сом. Проверьте цены в каталоге.",
            $"Сатуу өттү, бирок чектеги баа азыркыдан айырмалуу болчу: {shown}{tail} сом. Каталогдогу баааларды текшериңиз.",
            $"The sale went through, but the receipt price differed from the current one: {shown}{tail} som. Check the prices in the catalog.",
            $"Satış tamamlandı ancak fişteki fiyat güncel fiyattan farklıydı: {shown}{tail} som. Katalogdaki fiyatları kontrol edin.",
            $"Sotuv o'tkazildi, lekin chekdagi narx joriy narxdan farq qildi: {shown}{tail} so'm. Katalogdagi narxlarni tekshiring.");
    }
}
