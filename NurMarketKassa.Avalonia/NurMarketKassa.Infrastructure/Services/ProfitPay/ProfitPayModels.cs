using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

// 2026-10-28, владелец: «Схема зарплат от чистой прибыли, от чистой маржи!». Схемы сервера NurCRM (оклад, процент от продаж, за товар —
// market-sale-employee-pay-profiles) расширить с кассы нельзя (сервер не знает новых pay_scheme), поэтому «надбавка от прибыли»
// хранится отдельно — в общих настройках компании NurCRM (см. ProfitPayStore) — и ПРИБАВЛЯЕТСЯ к тому, что насчитал сервер по схеме
// сотрудника. Так получаются и комбинации: оклад + % от маржи, оклад + % от чистой прибыли, процент от продаж + % от прибыли.

/// <summary>От чего считается процент.</summary>
public enum ProfitPayBase
{
    /// <summary>Надбавки нет (схема выключена).</summary>
    None = 0,
    /// <summary>Чистая маржа = выручка − закупочная цена проданного − возвраты − скидки/бонусы.</summary>
    Margin = 1,
    /// <summary>Чистая прибыль = чистая маржа − расходы магазина за период (те же статьи, что в «Прибыли и деньгах»).</summary>
    NetProfit = 2,
}

/// <summary>По чьим цифрам считается.</summary>
public enum ProfitPayScope
{
    /// <summary>По своим продажам: доля сотрудника в продажах × результат магазина.</summary>
    Own = 0,
    /// <summary>По общему результату магазина: сотрудник получает свой процент от всего результата.</summary>
    Shop = 1,
    /// <summary>Общий пул: процент от результата магазина делится между участниками.</summary>
    Pool = 2,
}

/// <summary>Как делить пул.</summary>
public enum ProfitPayShare
{
    /// <summary>По доле продаж сотрудника.</summary>
    Sales = 0,
    /// <summary>По отработанным часам (табель: закрытые смены, часы внутри периода).</summary>
    Hours = 1,
    /// <summary>Поровну.</summary>
    Equal = 2,
}

/// <summary>Округление начисления.</summary>
public enum ProfitPayRounding
{
    /// <summary>До тыйына (0,01 сом).</summary>
    Tyiyn = 0,
    /// <summary>До целого сома.</summary>
    Som = 1,
}

/// <summary>Правило начисления: база, процент, порог, прогрессивная шкала.</summary>
public sealed class ProfitPayRule
{
    [JsonPropertyName("base")] public ProfitPayBase Base { get; set; } = ProfitPayBase.Margin;
    /// <summary>Процент (при прогрессивной шкале — «до предела»).</summary>
    [JsonPropertyName("pct")] public decimal Percent { get; set; }
    /// <summary>Минимальная прибыль для начисления: меньше — начисление 0.</summary>
    [JsonPropertyName("min")] public decimal MinProfit { get; set; }
    /// <summary>true — процент только с суммы сверх порога; false — порог «включает» начисление со всей суммы.</summary>
    [JsonPropertyName("excess")] public bool OnlyExcess { get; set; }
    [JsonPropertyName("prog")] public bool Progressive { get; set; }
    /// <summary>Предел шкалы: до него — Percent, свыше — PercentAbove.</summary>
    [JsonPropertyName("limit")] public decimal Limit { get; set; }
    [JsonPropertyName("above")] public decimal PercentAbove { get; set; }

    public ProfitPayRule Clone() => (ProfitPayRule)MemberwiseClone();
}

/// <summary>Схема одного сотрудника.</summary>
public sealed class ProfitPayScheme
{
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    /// <summary>Имя на момент сохранения (для строки без продаж и для ведомости).</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("scope")] public ProfitPayScope Scope { get; set; } = ProfitPayScope.Own;
    /// <summary>Правило сотрудника; для общего пула берётся правило пула из <see cref="ProfitPayShopSettings.Pool"/>.</summary>
    [JsonPropertyName("rule")] public ProfitPayRule Rule { get; set; } = new();
    /// <summary>ISO 8601 UTC; при слиянии с облаком побеждает более новая запись.</summary>
    [JsonPropertyName("at")] public string? UpdatedAt { get; set; }

    [JsonIgnore] public bool Active => Rule.Base != ProfitPayBase.None;

    public ProfitPayScheme Clone() => new() { UserId = UserId, Name = Name, Scope = Scope, Rule = Rule.Clone(), UpdatedAt = UpdatedAt };
}

/// <summary>Общие для магазина настройки: что вычитать из прибыли, пул, округление.</summary>
public sealed class ProfitPayShopSettings
{
    [JsonPropertyName("subSalaries")] public bool SubtractSalaries { get; set; }
    [JsonPropertyName("subRent")] public bool SubtractRent { get; set; } = true;
    [JsonPropertyName("subTaxes")] public bool SubtractTaxes { get; set; } = true;
    [JsonPropertyName("subUtilities")] public bool SubtractUtilities { get; set; } = true;
    [JsonPropertyName("subOther")] public bool SubtractOther { get; set; } = true;
    /// <summary>Закупки «из ящика» (статья «Закупка товара / оплата поставщику»): по умолчанию не вычитаются — закупка уже входит в
    /// закупочную цену проданного, повторный вычет задвоил бы расход.</summary>
    [JsonPropertyName("subSupplier")] public bool SubtractSupplierCash { get; set; }
    /// <summary>true — вычесть возвраты отдельно (если выручка сервера их не вычитает). По умолчанию выключено: как в «Прибыли и деньгах».</summary>
    [JsonPropertyName("subReturns")] public bool SubtractReturns { get; set; }
    [JsonPropertyName("rounding")] public ProfitPayRounding Rounding { get; set; } = ProfitPayRounding.Tyiyn;
    [JsonPropertyName("pool")] public ProfitPayRule Pool { get; set; } = new();
    [JsonPropertyName("share")] public ProfitPayShare Share { get; set; } = ProfitPayShare.Sales;
    [JsonPropertyName("at")] public string? UpdatedAt { get; set; }

    public bool Subtracts(string key) => key switch
    {
        "salaries" => SubtractSalaries,
        "rent" => SubtractRent,
        "taxes" => SubtractTaxes,
        "utilities" => SubtractUtilities,
        _ => SubtractOther,
    };

    public ProfitPayShopSettings Clone()
    {
        var c = (ProfitPayShopSettings)MemberwiseClone();
        c.Pool = Pool.Clone();
        return c;
    }
}

/// <summary>Документ настроек: общие настройки + схемы по id сотрудника.</summary>
public sealed class ProfitPayDocument
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("shop")] public ProfitPayShopSettings Shop { get; set; } = new();
    [JsonPropertyName("schemes")] public Dictionary<string, ProfitPayScheme> Schemes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Показатели магазина за период для расчёта (суммы в сомах, decimal).</summary>
public sealed class ProfitPayFigures
{
    public decimal Revenue { get; set; }
    public decimal Cogs { get; set; }
    /// <summary>Валовая прибыль сервера (gross_profit).</summary>
    public decimal Gross { get; set; }
    /// <summary>Расходы по статьям: salaries, rent, taxes, utilities, other.</summary>
    public Dictionary<string, decimal> Expenses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>«Чистая прибыль» сервера (operating_profit), если пришла; иначе считается Gross − все расходы.</summary>
    public decimal? ServerNet { get; set; }
    /// <summary>Возвраты за период (учитываются, только если включено «вычесть возвраты отдельно»).</summary>
    public decimal Returns { get; set; }
    /// <summary>Скидки и списанные бонусы, не попавшие в выручку (обычно 0: выручка сервера — по цене после скидки).</summary>
    public decimal Discounts { get; set; }
    /// <summary>Изъятия на закупку из ящика.</summary>
    public decimal SupplierCash { get; set; }
}

/// <summary>Сотрудник в расчёте: его продажи за период и часы по табелю.</summary>
public sealed class ProfitPayEmployee
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Продажи сотрудника (кассиром) за период, сом.</summary>
    public decimal Sales { get; set; }
    /// <summary>Отработанные часы за период по табелю; null — сотрудник в табеле не найден.</summary>
    public decimal? Hours { get; set; }
}

/// <summary>Результат по одному сотруднику с пошаговым пояснением «как посчитано».</summary>
public sealed class ProfitPayLine
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public ProfitPayBase Base { get; set; }
    public ProfitPayScope Scope { get; set; }
    /// <summary>База процента (маржа/прибыль в сомах, «своя» или магазина) — до округления.</summary>
    public decimal BaseValue { get; set; }
    /// <summary>Начислено, сом (после округления, не меньше 0).</summary>
    public decimal Amount { get; set; }
    /// <summary>Начисление обнулено (отрицательная прибыль, порог, нет весов пула…) — причина в Note.</summary>
    public bool Zeroed { get; set; }
    public string Note { get; set; } = "";
    public List<string> Steps { get; set; } = new();
}

/// <summary>Общий результат: цифры магазина и строки сотрудников.</summary>
public sealed class ProfitPayResult
{
    public decimal Revenue { get; set; }
    public decimal Cogs { get; set; }
    public decimal Gross { get; set; }
    public decimal Returns { get; set; }
    public decimal Discounts { get; set; }
    /// <summary>Чистая маржа магазина.</summary>
    public decimal Margin { get; set; }
    /// <summary>Расходы, вычтенные при расчёте чистой прибыли (с названиями).</summary>
    public List<(string Title, decimal Amount)> ExpensesSubtracted { get; set; } = new();
    public List<(string Title, decimal Amount)> ExpensesSkipped { get; set; } = new();
    /// <summary>Чистая прибыль магазина (по настройкам вычетов).</summary>
    public decimal NetProfit { get; set; }
    public decimal PoolAmount { get; set; }
    public decimal PoolUndistributed { get; set; }
    public List<string> ShopSteps { get; set; } = new();
    public List<ProfitPayLine> Lines { get; set; } = new();
}
