using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-13 (ответ Умара 13.11, п. 4, «Строймаркет»): единица продажи товара <c>sale_units</c> — упаковка для м²:
/// <c>{"name": "Упаковка", "unit_code": "pack", "factor": 1.44, "barcode", "price", "wholesale_price"}</c>. <c>factor</c> — сколько базовых единиц (м²) в упаковке.
/// В строке чека касса шлёт <c>sale_unit</c> и количество В УПАКОВКАХ, сервер списывает <c>qty × factor</c>; касса считает остаток так же
/// (<see cref="CartDisplayHelper.LineQuantityInStockUnits"/>). Колонок в pos_local.db для единиц нет (схему не меняем), поэтому касса запоминает их по id товара
/// в файле <c>product-sale-units.json</c> (как <see cref="ProductSerialRules"/>): <see cref="Remember"/> вызывает разбор карточки
/// (<see cref="ProductCatalogMapper.TryTile(System.Text.Json.JsonElement, string)"/> — products/list, products/changes, карточка), файл пишется после загрузки каталога.
/// Старый сервер поля не присылает — ничего не запоминается, всё работает как раньше.
/// </summary>
public static class ProductSaleUnits
{
    /// <summary>Одна единица продажи товара. Деньги — decimal.</summary>
    public sealed class Unit
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string UnitCode { get; set; } = "";
        /// <summary>Сколько базовых единиц товара в одной единице продажи (1,44 м² в упаковке).</summary>
        public decimal Factor { get; set; }
        public string? Barcode { get; set; }
        public decimal? Price { get; set; }
        public decimal? WholesalePrice { get; set; }

        /// <summary>Что уходит на сервер в поле <c>sale_unit</c>: id единицы, а если сервер его не прислал — её код (<c>pack</c>).</summary>
        public string Key => !string.IsNullOrWhiteSpace(Id) ? Id!.Trim() : UnitCode.Trim();

        /// <summary>Подпись для чека и экрана: «Упаковка 1,44 м²» (без единицы товара — просто «Упаковка ×1,44»).</summary>
        public string Label(string? baseUnit)
        {
            var name = string.IsNullOrWhiteSpace(Name) ? Tr.T("Упаковка", "Таңгак", "Pack", "Paket", "Qadoq") : Name.Trim();
            var f = Factor.ToString("0.####", CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(baseUnit) ? $"{name} ×{f}" : $"{name} {f} {baseUnit.Trim()}";
        }
    }

    /// <summary>Префикс «условного id упаковки», под которым единица продажи видна в <c>CartItem.SalePackageId</c> (проекция строки): так строка в упаковках не получает
    /// дробный шаг, а пределы остатка считаются через <see cref="Unit.Factor"/>. В JSON строки и в тело запроса этот префикс НЕ попадает (там <c>sale_unit</c>).</summary>
    public const string LinePrefix = "sale_unit:";

    /// <summary>Предел множителя (защита от опечатки в карточке).</summary>
    public const decimal MaxFactor = 1_000_000m;

    private static readonly ConcurrentDictionary<string, Unit[]> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (string ProductId, Unit Unit)> ByBarcode = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileGate = new();
    private static volatile bool _loaded;
    private static volatile bool _dirty;

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "product-sale-units.json");

    /// <summary>Запомнить <c>sale_units</c> карточки. Поля нет — ничего не меняется; поле есть (даже пустой список) — заменяет прежний список товара.</summary>
    public static void Remember(string? productId, JsonElement product)
    {
        if (string.IsNullOrWhiteSpace(productId) || product.ValueKind != JsonValueKind.Object
            || !product.TryGetProperty("sale_units", out var array) || array.ValueKind != JsonValueKind.Array)
            return;
        EnsureLoaded();
        var parsed = new List<Unit>();
        foreach (var el in array.EnumerateArray())
            if (Parse(el) is { } unit)
                parsed.Add(unit);
        Store(productId.Trim(), parsed.ToArray());
    }

    /// <summary>Только для проверок и локальной правки карточки: записать список единиц товара.</summary>
    public static void Set(string? productId, IEnumerable<Unit> units)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        EnsureLoaded();
        Store(productId.Trim(), units.Where(u => u.Factor > 0m).ToArray());
    }

    /// <summary>Единицы продажи товара (пусто — товар только в базовой единице).</summary>
    public static IReadOnlyList<Unit> For(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return Array.Empty<Unit>();
        EnsureLoaded();
        return Known.TryGetValue(productId.Trim(), out var units) ? units : Array.Empty<Unit>();
    }

    public static bool Has(string? productId) => For(productId).Count > 0;

    /// <summary>Единица товара по ключу из строки (<c>sale_unit</c>: id или код).</summary>
    public static Unit? Find(string? productId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var k = key.Trim();
        var units = For(productId);
        return units.FirstOrDefault(u => string.Equals(u.Key, k, StringComparison.OrdinalIgnoreCase))
               ?? units.FirstOrDefault(u => string.Equals(u.UnitCode?.Trim(), k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Скан штрихкода упаковки: товар и единица, чей <c>barcode</c> равен коду. null — такого кода у единиц нет.</summary>
    public static (string ProductId, Unit Unit)? FindByBarcode(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return null;
        EnsureLoaded();
        return ByBarcode.TryGetValue(barcode.Trim(), out var hit) ? hit : null;
    }

    /// <summary>Только для проверок: забыть всё (память; файл не трогает).</summary>
    public static void ResetForTests()
    {
        Known.Clear();
        ByBarcode.Clear();
        _loaded = false;
        _dirty = false;
    }

    public static void SaveIfDirty()
    {
        if (!_dirty)
            return;
        try
        {
            lock (FileGate)
            {
                _dirty = false;
                var snapshot = Known.ToDictionary(kv => kv.Key, kv => kv.Value);
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Единицы продажи товаров: не удалось сохранить на диск: {ex.Message}", "WARNING");
        }
    }

    // ── разбор строки/JSON строки чека ──

    /// <summary>Ключ единицы продажи строки чека (поле <c>sale_unit</c>: строка — id/код, объект — его <c>id</c> или <c>unit_code</c>); null — строка в базовой единице.</summary>
    public static string? LineKey(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty("sale_unit", out var el))
            return null;
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return string.IsNullOrWhiteSpace(el.GetString()) ? null : el.GetString()!.Trim();
            case JsonValueKind.Number:
                return el.GetRawText();
            case JsonValueKind.Object:
                foreach (var name in new[] { "id", "unit_code", "code" })
                    if (el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
                        && (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()) is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s))
                        return s.Trim();
                return null;
            default:
                return null;
        }
    }

    /// <summary>Сколько базовых единиц в единице продажи строки: <c>sale_unit_factor</c> строки (кассой), множитель из объекта <c>sale_unit</c> (ответ сервера по продаже),
    /// иначе — из карточки товара по ключу. Строка в базовой единице — 1.</summary>
    public static decimal LineFactor(JsonElement line, string? productId)
    {
        var key = LineKey(line);
        if (key is null)
            return 1m;
        if (line.TryGetProperty("sale_unit_factor", out var own) && ReadDecimal(own) is { } f0 && f0 > 0m && f0 <= MaxFactor)
            return f0;
        if (line.TryGetProperty("sale_unit", out var obj) && obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty("factor", out var f1) && ReadDecimal(f1) is { } f2 && f2 > 0m && f2 <= MaxFactor)
            return f2;
        return Find(productId, key) is { Factor: > 0m } unit ? unit.Factor : 1m;
    }

    // ── внутреннее ──

    private static void Store(string id, Unit[] units)
    {
        if (Known.TryGetValue(id, out var old) && SameUnits(old, units))
            return;
        if (old is not null)
            foreach (var u in old)
                if (!string.IsNullOrWhiteSpace(u.Barcode) && ByBarcode.TryGetValue(u.Barcode.Trim(), out var hit) && string.Equals(hit.ProductId, id, StringComparison.OrdinalIgnoreCase))
                    ByBarcode.TryRemove(u.Barcode.Trim(), out _);
        if (units.Length == 0)
            Known.TryRemove(id, out _);
        else
            Known[id] = units;
        foreach (var u in units)
            if (!string.IsNullOrWhiteSpace(u.Barcode))
                ByBarcode[u.Barcode.Trim()] = (id, u);
        _dirty = true;
    }

    private static bool SameUnits(Unit[] a, Unit[] b) =>
        a.Length == b.Length && JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    private static Unit? Parse(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty("factor", out var fEl) || ReadDecimal(fEl) is not { } factor || factor <= 0m || factor > MaxFactor)
            return null;
        var unit = new Unit
        {
            Id = ReadText(el, "id"),
            Name = ReadText(el, "name") ?? "",
            UnitCode = ReadText(el, "unit_code") ?? "",
            Factor = factor,
            Barcode = ReadText(el, "barcode"),
            Price = el.TryGetProperty("price", out var p) ? ReadDecimal(p) : null,
            WholesalePrice = el.TryGetProperty("wholesale_price", out var w) ? ReadDecimal(w) : null,
        };
        // Без id и без кода единицу сервер по полю sale_unit не найдёт — такую не запоминаем (продажа ушла бы без единицы и списала бы упаковку как штуку).
        return unit.Key.Length == 0 ? null : unit;
    }

    private static string? ReadText(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
            return null;
        var s = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static decimal? ReadDecimal(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetDecimal(out var d) => d,
        JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
        _ => null,
    };

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (FileGate)
        {
            if (_loaded)
                return;
            try
            {
                var path = FilePath;
                if (File.Exists(path)
                    && AtomicFile.ReadAllText(path) is { Length: > 0 } text
                    && JsonSerializer.Deserialize<Dictionary<string, Unit[]>>(text) is { } stored)
                    foreach (var (id, units) in stored)
                        if (units is { Length: > 0 } && !Known.ContainsKey(id))
                        {
                            Known[id] = units;
                            foreach (var u in units)
                                if (!string.IsNullOrWhiteSpace(u.Barcode))
                                    ByBarcode.TryAdd(u.Barcode.Trim(), (id, u));
                        }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                PosLogger.Log($"Единицы продажи товаров: файл не прочитан ({ex.Message}) — ждём каталог с сервера.", "WARNING");
            }
            _loaded = true;
        }
    }
}
