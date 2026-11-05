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
    public static string PieceText(JsonElement it, double qty)
    {
        if (IsFractionalLine(it))
            return qty.ToString("0.###", CultureInfo.InvariantCulture);
        var rounded = Math.Round(qty, 0);
        // 2026-11-01 (живая проверка 1.17.72, О-3): положительное количество не должно уйти на сервер как «0» (0,375 кг у строки, потерявшей признак
        // «весовая»): шлём настоящее значение — итог совпадёт с кассой, а сервер или проверка итога скажут своё слово, а не продадут «ноль».
        if (rounded == 0 && qty > 0)
            return qty.ToString("0.###", CultureInfo.InvariantCulture);
        return rounded.ToString(CultureInfo.InvariantCulture);
    }

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
        // 2026-11-01: decimal для всех магазинов (раньше вне «Хозтоваров» — double: 0,1 × 41,05 давало 4,10 вместо 4,11). Это только ПОКАЗ строки.
        double.IsFinite(qty) && double.IsFinite(price) && Math.Abs(qty) < 1e9 && Math.Abs(price) < 1e9
            ? (double)Math.Round((decimal)qty * (decimal)price, 2, MidpointRounding.AwayFromZero)
            : Math.Round(qty * price, 2, MidpointRounding.AwayFromZero);

    /// <summary>2026-11-01 (ответ Умара, округление): сумма строки как у сервера — количество × цена в decimal БЕЗ округления
    /// (полная точность). Сервер складывает такие строки и округляет только итог чека (HALF_UP до 0,01), поэтому в итог идёт
    /// это значение, а <see cref="Gross"/> (с округлением до копейки) остаётся для ПОКАЗА суммы строки.</summary>
    public static decimal GrossExact(double qty, double price) =>
        double.IsFinite(qty) && double.IsFinite(price) && Math.Abs(qty) < 1e9 && Math.Abs(price) < 1e9
            ? (decimal)qty * (decimal)price
            : 0m;

    /// <summary>2026-11-01 (опыты на сервере NBS, чеки 1971–1988): подытог = ВНИЗ до 0,01 от СУММЫ всех весовых строк (товары с
    /// <c>is_weight</c>) + половина вверх до 0,01 от суммы остальных строк (штучные, «Доп. услуга» — даже с дробным количеством).
    /// 4,875 + 22,638 = 27,513 → 27,51 (а не 27,50: вниз округляется сумма, а не каждая строка); 22,638 + 4,472 («Доп. услуга») → 22,63 + 4,47 = 27,10.</summary>
    public static decimal Subtotal(decimal weighedSum, decimal otherSum) =>
        (weighedSum > 0 ? Math.Floor(weighedSum * 100m) / 100m : RoundMoney(weighedSum)) + RoundMoney(otherSum);

    /// <summary>2026-11-05 (сервер ceba67c, признак weight_line_total_floor): сервер считает <c>line_total</c> КАЖДОЙ весовой строки как FLOOR(цена × вес) до шага весов (минус скидка), а подытог —
    /// как сумму строк (пример: 0,486 × 66 = 32,076 → 32,07). Подмена нужна тестам; по умолчанию — признак из capabilities (нет списка → прежнее правило).</summary>
    public static Func<bool> WeightLineFloorEnabled { get; set; } = () => NurMarketKassa.Services.Api.ServerCapabilities.Has(NurMarketKassa.Services.Api.ServerCapabilities.WeightLineTotalFloor);

    /// <summary>Шаг весов (scale_barcode_amount_unit КОМПАНИИ с сервера): «som» — 1 сом, иначе «tiyin» — 0,01.
    /// 2026-11-06 (аудит №5, Л5-02 / А5-01): берётся ТОЛЬКО серверная единица (<see cref="UserPreferences.ScaleBarcodeAmountUnit"/>), её же применяет сервер при расчёте line_total.
    /// Местный выбор единицы суммы на кассе (ScaleBarcodeAmountUnitLocal) — другое понятие: он нужен только для разбора цифр этикетки и веса из суммы, на деньги чека не влияет.</summary>
    public static Func<decimal> WeightStep { get; set; } = DefaultWeightStep;

    /// <summary>2026-11-06: шаг по серверной единице суммы из настроек (локальный выбор кассы не учитывается). Вынесено отдельно, чтобы тесты могли восстановить правило по умолчанию.</summary>
    internal static decimal DefaultWeightStep() => StepForServerUnit(UserPreferences.Instance.ScaleBarcodeAmountUnit);

    /// <summary>2026-11-06: шаг округления по серверной единице суммы (неизвестное значение = «tiyin», как у <see cref="NurMarketKassa.Core.Application.WeightBarcodeParser.ResolveAmountUnit"/>).</summary>
    public static decimal StepForServerUnit(string? serverUnit) =>
        string.Equals(NurMarketKassa.Core.Application.WeightBarcodeParser.ResolveAmountUnit(null, serverUnit), NurMarketKassa.Core.Application.WeightBarcodeParser.UnitSom, StringComparison.Ordinal) ? 1m : 0.01m;

    /// <summary>Вниз до шага (положительные значения); шаг ≤ 0 — до копейки.</summary>
    public static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0m)
            step = 0.01m;
        return Math.Floor(value / step) * step;
    }

    /// <summary>Подытог чека: весовые строки (точные суммы цена × вес) + сумма остальных. Без признака weight_line_total_floor — прежнее правило (вниз от СУММЫ весовых);
    /// с признаком — каждая весовая строка вниз до шага весов, потом сумма (сумма строк = подытог, как у сервера). Остальные — половина вверх от суммы, как раньше.</summary>
    public static decimal Subtotal(IReadOnlyList<decimal> weighedLines, decimal otherSum)
    {
        if (!WeightLineFloorEnabled())
            return Subtotal(weighedLines.Sum(), otherSum);
        var step = WeightStep();
        var weighed = weighedLines.Sum(x => x > 0 ? FloorToStep(x, step) : RoundMoney(x));
        return weighed + RoundMoney(otherSum);
    }

    /// <summary>2026-11-01: единственная точка округления денег чека — до 0,01, половина вверх (ROUND_HALF_UP сервера).</summary>
    public static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>2026-11-01: double → decimal для денег; нечисло и бесконечность дают 0 (приведение выбросило бы исключение).</summary>
    public static decimal ToMoneyDecimal(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 1e15 ? (decimal)value : 0m;

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
