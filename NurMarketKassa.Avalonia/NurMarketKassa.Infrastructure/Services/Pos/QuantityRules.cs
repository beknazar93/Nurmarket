using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-10-26 (ТЗ «Хозмаг и стройматериалы», ХМ2): правило количества штучного товара — шаг, минимум, знаки.</summary>
public sealed record QuantityRule(string UnitCode, decimal Step, decimal Min, int Decimals);

/// <summary>
/// 2026-10-26 (ХМ2): дробное количество штучного товара (2,5 м кабеля) только для магазина «Хозтовары и стройматериалы»
/// (<see cref="MarketSpheres.IsHardware"/>) и только для единиц, которые <see cref="HardwareProfile"/> считает дробными
/// (м, м², м³, л, т). Единица «кг» остаётся прежним весовым путём. Во всех остальных случаях (продукты, одежда, услуги,
/// «шт», неизвестная единица, флаг выключен) методы возвращают null / прежнее значение — поведение не меняется.
/// Это ДЕНЬГИ: количество уходит в тело продажи (qty/quantity), офлайн-очередь и возвраты.
/// </summary>
public static class QuantityRules
{
    /// <summary>Метка строки чека: «количество этой строки дробное по правилу единицы». Ставится при добавлении товара,
    /// по ней отправка на сервер (в том числе из офлайн-очереди после смены сферы) не округляет метры до целого.</summary>
    public const string FractionalField = "frac_qty";

    /// <summary>Главный флаг функции. Сервер принимает дробное quantity у is_weight=false (проверено записью, см. отчёт ХМ2 /
    /// ТЗ бэкенду Б-52). Выключен — касса ведёт себя как раньше даже в «Хозтоварах». Менять только после проверки сервера.</summary>
    public static bool ServerAcceptsFractional { get; set; } = false;

    /// <summary>Функция действует сейчас: флаг включён и магазин — «Хозтовары и стройматериалы».</summary>
    public static bool Active => ServerAcceptsFractional && MarketSpheres.IsHardware;

    /// <summary>Правило по тексту единицы («м», «пог.м», «м2», «л»…) или null: не хозмаг, флаг выкл, единица не дробная
    /// или «кг» (весовой путь).</summary>
    public static QuantityRule? ForUnit(string? unitText)
    {
        if (!Active)
            return null;
        var unit = HardwareProfile.FindUnit(unitText);
        if (unit is null || !unit.Fractional || unit.Code == "kg" || unit.Step <= 0m)
            return null;
        return new QuantityRule(unit.Code, unit.Step, unit.MinQty ?? unit.Step, unit.Decimals);
    }

    /// <summary>Правило для товара каталога: весовой товар (is_weight / «кг») — всегда null.</summary>
    public static QuantityRule? ForTile(CatalogProductTileVm? tile) =>
        tile is null || tile.MustWeigh ? null : ForUnit(tile.Unit);

    /// <summary>Правило для строки чека (снимок): метка <see cref="FractionalField"/>, иначе единица строки или товара
    /// каталога. Весовая строка и поштучная из упаковки (sale_package_id) — null.</summary>
    public static QuantityRule? ForLine(JsonElement it)
    {
        if (!Active || it.ValueKind != JsonValueKind.Object)
            return null;
        if (CartDisplayHelper.LineMustWeigh(it) || !string.IsNullOrWhiteSpace(CartDisplayHelper.SalePackageId(it)))
            return null;
        return ForUnit(LineUnitText(it));
    }

    /// <summary>То же для строки в виде JsonObject (редактор снимка чека).</summary>
    public static QuantityRule? ForLine(JsonObject line)
    {
        if (!Active)
            return null;
        using var doc = JsonDocument.Parse(line.ToJsonString());
        return ForLine(doc.RootElement);
    }

    /// <summary>Строка помечена как дробная (при добавлении) ИЛИ сейчас подпадает под правило. Для отправки на сервер.</summary>
    public static bool IsFractionalLine(JsonElement it) =>
        it.ValueKind == JsonValueKind.Object
        && ((it.TryGetProperty(FractionalField, out var mark) && mark.ValueKind == JsonValueKind.True)
            || ForLine(it) is not null);

    /// <summary>Текст количества НЕвесовой строки для тела запроса на сервер. Без дробной строки — ровно прежнее
    /// <c>Math.Round(qty, 0)</c>. Дробная — до 3 знаков без хвостовых нулей («2.5»; у целого числа точки нет).</summary>
    public static string PieceText(JsonElement it, double qty) =>
        IsFractionalLine(it)
            ? qty.ToString("0.###", CultureInfo.InvariantCulture)
            : Math.Round(qty, 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>Привести количество к шагу и минимуму единицы: к ближайшему кратному шагу (половина вверх), до 3 знаков
    /// (как вес), не меньше минимума. Нечисло и ≤ 0 возвращает как есть — проверяет вызывающий. Считает в decimal,
    /// чтобы 0,1 + 0,2 не давало 0,30000000000000004 в теле запроса.</summary>
    public static double Normalize(double qty, QuantityRule rule)
    {
        if (!double.IsFinite(qty) || qty <= 0 || qty > 1e9)
            return qty;
        var value = (decimal)qty;
        var snapped = Math.Round(value / rule.Step, 0, MidpointRounding.AwayFromZero) * rule.Step;
        snapped = Math.Round(snapped, 3, MidpointRounding.AwayFromZero);
        if (snapped < rule.Min)
            snapped = rule.Min;
        return (double)snapped;
    }

    /// <summary>Количество при слиянии с уже лежащей в чеке строкой того же товара: для дробной единицы — сумма, приведённая
    /// к шагу, и строка получает метку; иначе прежняя сумма <c>oldQty + qty</c> без изменений.</summary>
    public static double MergeQuantity(JsonObject line, CatalogProductTileVm product, string? salePackageId, double oldQty, double qty)
    {
        if (!string.IsNullOrWhiteSpace(salePackageId) || ForTile(product) is not { } rule)
            return oldQty + qty;
        line[FractionalField] = true;
        return Normalize(oldQty + qty, rule);
    }

    /// <summary>Пометить новую строку чека как дробную (если товар подпадает под правило). Поштучная продажа из упаковки
    /// (sale_package_id) не помечается: там штуки целые.</summary>
    public static void StampLine(JsonObject line, CatalogProductTileVm product, string? salePackageId)
    {
        if (!string.IsNullOrWhiteSpace(salePackageId) || ForTile(product) is null)
            return;
        line[FractionalField] = true;
        line["unit"] = product.Unit;
    }

    /// <summary>Сумма строки = количество × цена, до копейки, половина вверх. В «Хозтоварах» (функция включена) считается в decimal,
    /// как на сервере: 0,1 × 41,05 = 4,105 → 4,11, а в double выходило 4,10 (двоичная погрешность, расхождение в копейку с чеком
    /// сервера). Везде кроме «Хозтоваров» — прежний расчёт в double без изменений.</summary>
    public static double Gross(double qty, double price) =>
        Active && double.IsFinite(qty) && double.IsFinite(price) && Math.Abs(qty) < 1e9 && Math.Abs(price) < 1e9
            ? (double)Math.Round((decimal)qty * (decimal)price, 2, MidpointRounding.AwayFromZero)
            : Math.Round(qty * price, 2, MidpointRounding.AwayFromZero);

    /// <summary>Подсказка «минимум» для кассира, 5 языков, например «Минимум — 1 м.».</summary>
    public static string MinimumMessage(QuantityRule rule, string unitText) =>
        Tr.T($"Минимум — {rule.Min.ToString("0.###", CultureInfo.InvariantCulture)} {unitText}.",
            $"Эң азы — {rule.Min.ToString("0.###", CultureInfo.InvariantCulture)} {unitText}.",
            $"Minimum is {rule.Min.ToString("0.###", CultureInfo.InvariantCulture)} {unitText}.",
            $"En az {rule.Min.ToString("0.###", CultureInfo.InvariantCulture)} {unitText}.",
            $"Minimal — {rule.Min.ToString("0.###", CultureInfo.InvariantCulture)} {unitText}.");

    private static string? LineUnitText(JsonElement it)
    {
        if (it.TryGetProperty("unit", out var unit) && unit.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(unit.GetString()))
            return unit.GetString();
        foreach (var key in new[] { "product", "product_snapshot" })
        {
            if (it.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("unit", out var pu) && pu.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pu.GetString()))
                return pu.GetString();
        }

        var productId = CartDisplayHelper.TryProductId(it);
        if (string.IsNullOrEmpty(productId))
            return null;
        try
        {
            return LocalProductRepository.Instance.TryGetTileById(productId)?.Unit;
        }
        catch (Exception)
        {
            // Каталог недоступен — единица неизвестна, строка считается целой (как раньше), а не ломает правку количества.
            return null;
        }
    }
}
