using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-28, денежный баг продажи №1136: «Акции» товара NurCRM (поле товара promotion_rules,
/// задаются на сайте в карточке товара) сервер применяет к строке чека САМ — и при add-item, и при
/// продаже одним запросом (POST pos/checkout/). Касса о них не знала: кассир видел в окне оплаты
/// 50,00 и взял 50, а сервер провёл «Адыгене 1л» со скидкой по акции 15 % → 42,50 и сдачей 7,50.
///
/// Правила сервера, проверенные на тестовом аккаунте 28.09 (корзины без продажи + продажи 1143, 1144):
/// • правило подходит, если сумма строки (цена × количество) ≥ min_amount; из подходящих берётся
///   правило с наибольшим min_amount (как на сайте в «Интерфейсе кассира»);
/// • скидка = сумма × discount_percent / 100, до копейки; promo_quantity — НЕ порог, а предел:
///   скидка считается не больше чем на promo_quantity штук (30 сом × 11 шт при пределе 10 → 135,00);
/// • у товара с акциями сервер ИГНОРИРУЕТ скидку строки от кассы (discount_total в add-item/PATCH,
///   discount в pos/checkout/) — даже когда ни одно правило не подошло (скидка 5,00 → 0,00);
/// • скидка на чек считается от суммы ПОСЛЕ скидки по акции (42,50 × 10 % = 4,25).
///
/// Правила кладутся прямо в строку чека (поле "promotion_rules" — так же их отдаёт сервер в строках
/// корзины), поэтому итог считается из самого снимка чека одной функцией
/// (CartDisplayHelper.EffectiveLineDiscount) — на экране, в окне оплаты, в теле запроса, в очереди
/// без интернета и в отложенных чеках одинаково. Откуда касса берёт правила — из каталога
/// (ProductCatalogMapper.TryTile запоминает их здесь); запоминаются на диск, чтобы касса, открытая
/// без интернета, считала акции так же.
/// </summary>
public static class PromotionRules
{
    public const string LineField = "promotion_rules";

    /// <summary>id товара → JSON-массив правил ("[]" — у товара акций нет).</summary>
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileGate = new();
    private static volatile bool _loaded;
    private static volatile bool _dirty;

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppMode.DataFolderName,
            "promotion_rules.json");

    /// <summary>Запомнить правила товара из ответа API каталога. Товар без поля
    /// promotion_rules (другой эндпоинт) ничего не меняет.</summary>
    public static void Remember(string? productId, JsonElement product)
    {
        if (string.IsNullOrWhiteSpace(productId)
            || product.ValueKind != JsonValueKind.Object
            || !product.TryGetProperty(LineField, out var rules))
            return;

        EnsureLoaded();
        var text = rules.ValueKind == JsonValueKind.Array && rules.GetArrayLength() > 0 ? rules.GetRawText() : "[]";
        var id = productId.Trim();
        if (Known.TryGetValue(id, out var old) && string.Equals(old, text, StringComparison.Ordinal))
            return;
        Known[id] = text;
        _dirty = true;
    }

    /// <summary>Сохранить на диск после загрузки каталога (если что-то поменялось).</summary>
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
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Акции товаров: не удалось сохранить на диск: {ex.Message}", "WARNING");
        }
    }

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
                    && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } stored)
                {
                    foreach (var (id, text) in stored)
                        Known.TryAdd(id, text);
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Акции товаров: файл не прочитан ({ex.Message}) — ждём каталог с сервера.", "WARNING");
            }

            _loaded = true;
        }
    }

    /// <summary>Проставить строке чека текущие правила товара. true — строка изменилась.
    /// Товар, про который касса ещё ничего не знает, не трогаем.</summary>
    public static bool ApplyToLine(JsonObject line, string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return false;
        EnsureLoaded();
        if (!Known.TryGetValue(productId.Trim(), out var text))
            return false;

        var current = line[LineField] is JsonArray arr && arr.Count > 0 ? arr.ToJsonString() : "[]";
        var next = JsonNode.Parse(text) is JsonArray parsed && parsed.Count > 0 ? parsed.ToJsonString() : "[]";
        if (string.Equals(current, next, StringComparison.Ordinal))
            return false;

        if (next == "[]")
            line.Remove(LineField);
        else
            line[LineField] = JsonNode.Parse(next);
        return true;
    }

    /// <summary>Есть ли у строки хотя бы одно правило акции (тогда скидку строки назначает сервер).</summary>
    public static bool LineHasRules(JsonElement line) =>
        line.ValueKind == JsonValueKind.Object
        && line.TryGetProperty(LineField, out var rules)
        && rules.ValueKind == JsonValueKind.Array
        && rules.EnumerateArray().Any(r => ReadNumber(r, "discount_percent") > 0);

    /// <summary>Скидка строки по акции так, как её посчитает сервер. null — у строки акций нет
    /// (действует обычная скидка кассира); 0 — акции есть, но ни одна не подошла.</summary>
    public static double? LineDiscount(JsonElement line, double quantity, double unitPrice)
    {
        if (!LineHasRules(line))
            return null;

        var gross = Round(quantity * unitPrice);
        if (!(gross > 0))
            return 0;

        JsonElement? best = null;
        double bestMin = 0;
        foreach (var rule in line.GetProperty(LineField).EnumerateArray()
                     .Where(r => ReadNumber(r, "discount_percent") > 0)
                     .OrderBy(r => ReadNumber(r, "position")))
        {
            var min = ReadNumber(rule, "min_amount");
            if (gross + 1e-9 < min)
                continue;
            if (best == null || min > bestMin)
            {
                best = rule;
                bestMin = min;
            }
        }

        if (best is not { } chosen)
            return 0;

        var percent = Math.Min(100, ReadNumber(chosen, "discount_percent"));
        var limit = ReadNumber(chosen, "promo_quantity");
        var discountedQty = limit > 0 ? Math.Min(quantity, limit) : quantity;
        var discount = Round(Round(discountedQty * unitPrice) * percent / 100.0);
        return Math.Clamp(discount, 0, gross);
    }

    private static double Round(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : 0;

    private static double ReadNumber(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(property, out var el)
        && JsonNumericReader.TryToDouble(el, out var v)
        && double.IsFinite(v)
            ? v
            : 0;
}
