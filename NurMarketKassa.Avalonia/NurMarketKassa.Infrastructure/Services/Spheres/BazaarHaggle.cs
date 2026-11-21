using System.Globalization;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>Результат расчёта торга.</summary>
public enum HaggleOutcome
{
    /// <summary>Скидка посчитана (<see cref="HaggleResult.Discount"/> &gt; 0).</summary>
    Ok,
    /// <summary>Сумма уже круглая или равна договорённой — скидки нет.</summary>
    NoChange,
    /// <summary>«Договорились за …» больше суммы: торг только вниз.</summary>
    CannotRaise,
    /// <summary>Договорились за 0 или меньше / сумма не число.</summary>
    InvalidAmount,
    /// <summary>Округление «до N» увело бы сумму в ноль (сумма меньше шага).</summary>
    BelowStep,
}

/// <summary>Итог торга: сумма до, сумма после и скидка суммой (сом, две цифры).</summary>
public readonly record struct HaggleResult(HaggleOutcome Outcome, decimal Total, decimal Agreed, decimal Discount)
{
    public bool IsOk => Outcome == HaggleOutcome.Ok;

    /// <summary>Скидка в процентах от суммы до торга (для порога PIN).</summary>
    public double Percent => Total > 0m ? (double)(Discount / Total * 100m) : 0;
}

/// <summary>Что делать со скидкой торга по порогам (ТЗ Контейнер-базар 2.3): до порога PIN — свободно; выше порога PIN — нужен PIN руководителя;
/// выше жёсткого предела — только владелец / администратор.</summary>
public enum HaggleGuardLevel
{
    Free,
    NeedsPin,
    SeniorOnly,
}

/// <summary>
/// 2026-11-21 (сфера «Базар», модуль <c>haggle</c>, ТЗ Контейнер-базар 2.3): торг. «До 10 / 50 / 100» — сумма округляется ВНИЗ до шага (в пользу покупателя, вверх нельзя),
/// «Договорились за …» — итог называет продавец, скидка = сумма − итог. Скидка уходит в чек обычной скидкой (строки чека не меняются; скидка на строку — суммой);
/// причина «торг» хранится в чеке (<see cref="ReasonField"/>) и в журнале. Чистая логика на decimal, без экрана — проверяется тестами.
/// </summary>
public static class BazaarHaggle
{
    /// <summary>Шаги округления «До N».</summary>
    public static readonly IReadOnlyList<int> Steps = new[] { 10, 50, 100 };

    /// <summary>Поле чека (снимок корзины): скидка поставлена торгом. Только для кассы — на сервер не уходит (тело продажи собирается по отдельным полям).</summary>
    public const string ReasonField = "discount_reason";

    /// <summary>Значение <see cref="ReasonField"/> для торга.</summary>
    public const string ReasonValue = "haggle";

    /// <summary>Порог, выше которого скидка торга требует PIN руководителя (процент от суммы; ТЗ 2.3, вопрос 9 — рекомендация 5 %).</summary>
    public const double DefaultPinPercent = 5;

    /// <summary>Жёсткий предел: выше — только владелец / администратор (ТЗ 2.3 — 10 %).</summary>
    public const double DefaultHardPercent = 10;

    /// <summary>Итог после округления вниз до шага. Сумма меньше шага — <see cref="HaggleOutcome.BelowStep"/>.</summary>
    public static HaggleResult RoundDown(decimal total, int step)
    {
        total = Math.Round(total, 2, MidpointRounding.ToZero);
        if (total <= 0m || step <= 0)
            return new HaggleResult(HaggleOutcome.InvalidAmount, total, total, 0m);
        var agreed = Math.Floor(total / step) * step;
        if (agreed <= 0m)
            return new HaggleResult(HaggleOutcome.BelowStep, total, total, 0m);
        if (agreed >= total)
            return new HaggleResult(HaggleOutcome.NoChange, total, total, 0m);
        return new HaggleResult(HaggleOutcome.Ok, total, agreed, total - agreed);
    }

    /// <summary>2026-11-21: сумма позиции «цена × количество», округлённая вниз до копейки (раньше считалась в окне — правило «денег во view нет»).</summary>
    public static decimal LineGross(double unitPrice, double quantity)
        => Math.Round((decimal)(unitPrice * quantity), 2, MidpointRounding.ToZero);

    /// <summary>«Договорились за …»: скидка = сумма − договорённый итог. Итог выше суммы нельзя (торг только вниз).</summary>
    public static HaggleResult Agreed(decimal total, decimal agreed)
    {
        total = Math.Round(total, 2, MidpointRounding.ToZero);
        agreed = Math.Round(agreed, 2, MidpointRounding.ToZero);
        if (total <= 0m || agreed <= 0m)
            return new HaggleResult(HaggleOutcome.InvalidAmount, total, agreed, 0m);
        if (agreed > total)
            return new HaggleResult(HaggleOutcome.CannotRaise, total, agreed, 0m);
        if (agreed == total)
            return new HaggleResult(HaggleOutcome.NoChange, total, agreed, 0m);
        return new HaggleResult(HaggleOutcome.Ok, total, agreed, total - agreed);
    }

    /// <summary>Разбор введённого «Договорились за»: пробелы и запятая допускаются; не число — null.</summary>
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var clean = text.Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0m && v < 1_000_000_000m ? v : null;
    }

    /// <summary>Уровень защиты по проценту скидки. Пороги — из настроек «Базара» (раздел <c>bazaar</c>, <c>haggle</c>: <c>pinPercent</c>, <c>hardPercent</c>) или умолчания.</summary>
    public static HaggleGuardLevel GuardLevel(double percent, double? pinPercent = null, double? hardPercent = null)
    {
        var pin = pinPercent ?? PinPercent;
        var hard = Math.Max(pin, hardPercent ?? HardPercent);
        if (percent > hard + 1e-9)
            return HaggleGuardLevel.SeniorOnly;
        if (percent > pin + 1e-9)
            return HaggleGuardLevel.NeedsPin;
        return HaggleGuardLevel.Free;
    }

    public static double PinPercent => ReadPercent("pinPercent") ?? DefaultPinPercent;

    public static double HardPercent => ReadPercent("hardPercent") ?? DefaultHardPercent;

    private static double? ReadPercent(string key)
    {
        var section = SphereSettings.Section(UserPreferences.Instance.SphereSettingsJson, "bazaar");
        return section?["haggle"] is JsonObject h && h[key] is JsonValue v && v.TryGetValue<double>(out var d) && d >= 0 && d <= 100 ? d : null;
    }

    /// <summary>Записать пороги торга (проценты; жёсткий не меньше порога PIN). false — не поместилось в лимит настроек или значения не годятся.</summary>
    public static bool SetPercents(double pin, double hard)
    {
        if (!double.IsFinite(pin) || !double.IsFinite(hard) || pin < 0 || hard > 100 || hard < pin)
            return false;
        var section = SphereSettings.Section(UserPreferences.Instance.SphereSettingsJson, "bazaar") ?? new JsonObject();
        section["haggle"] = new JsonObject { ["pinPercent"] = pin, ["hardPercent"] = hard };
        return SphereSettings.TrySetSection("bazaar", section);
    }

    /// <summary>Подпись скидки торга для строки чека и журнала.</summary>
    public static string ReasonText => Tr.T("торг", "соодалашуу", "haggling", "pazarlık", "savdolashuv");
}
