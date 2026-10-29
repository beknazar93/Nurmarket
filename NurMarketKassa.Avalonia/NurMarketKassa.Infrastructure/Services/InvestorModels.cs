using System.Globalization;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29: запись общего документа «StartupCosts» с идентификатором и меткой времени — по ним две программы
/// сливают данные по записям (побеждает более свежая; удаление — «надгробие»).</summary>
public abstract class StampedRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Время последнего изменения (UTC): по нему при слиянии побеждает более свежая запись.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Удалена («надгробие»): не показывается, хранится 90 дней, чтобы удаление дошло до другой программы.</summary>
    public bool Deleted { get; set; }
    /// <summary>Незнакомые поля записи (их добавила другая версия программы) — уходят обратно без изменений.</summary>
    internal JsonObject? Extra { get; set; }

    internal abstract StampedRecord CloneRecord();
}

/// <summary>Этап выплат инвестору: доля от базы и сколько он действует.</summary>
public sealed class PayStage
{
    /// <summary>Доля от базы, % (дробная допускается).</summary>
    public decimal Pct { get; set; }
    /// <summary>«return» — пока инвестор не получит Val % от своего вложения; «months» — Val месяцев; «forever» — бессрочно.</summary>
    public string Cond { get; set; } = "forever";
    public decimal Val { get; set; }

    public PayStage Clone() => (PayStage)MemberwiseClone();
}

/// <summary>Условия инвестора: от чего считается доля, этапы выплат, правило убыточного месяца, период и ограничения, особенности сферы.</summary>
public sealed class InvestorTerms
{
    /// <summary>Ключ шаблона сферы (<see cref="InvestorTemplates"/>); пусто — условия правили вручную.</summary>
    public string Template { get; set; } = "";
    /// <summary>«revenue» — выручка, «gross» — валовая прибыль, «net» — чистая прибыль (<see cref="NetProfitCalculator.BaseFor"/>).</summary>
    public string Base { get; set; } = "net";
    /// <summary>Статьи расходов, которые при базе «net» НЕ вычитаются (ключи <see cref="NetProfitCalculator.OpexKeys"/>).</summary>
    public List<string> ExcludedExpenses { get; set; } = new();
    public List<PayStage> Stages { get; set; } = new();
    /// <summary>«carry» — убыток переносится и сначала покрывается; «none» — убыточный месяц просто без выплаты; «share» — инвестор участвует в убытке.</summary>
    public string LossRule { get; set; } = "carry";
    /// <summary>«month» — каждый месяц; «quarter» — раз в квартал; «year» — раз в год; «manual» — вручную. Расчёт идёт по месяцам, период — подсказка договору.</summary>
    public string Period { get; set; } = "month";
    /// <summary>Минимальная выплата в месяц (если есть база), сом; 0 — нет.</summary>
    public decimal MinMonthly { get; set; }
    /// <summary>Налог, удерживаемый с выплат, %.</summary>
    public decimal TaxPct { get; set; }
    /// <summary>Резервный фонд: % положительной базы откладывается до распределения.</summary>
    public decimal ReservePct { get; set; }
    /// <summary>Засчитывать товарный остаток (по закупочной цене) в окупаемость бизнеса.</summary>
    public bool StockCredit { get; set; }
    /// <summary>Ликвидность остатка, %: какую часть остатка реально вернуть при распродаже.</summary>
    public decimal StockLiquidityPct { get; set; } = 70m;
    /// <summary>Номер версии условий (растёт при каждом сохранении).</summary>
    public int Version { get; set; } = 1;
    internal JsonObject? Extra { get; set; }

    public InvestorTerms Clone()
    {
        var c = (InvestorTerms)MemberwiseClone();
        c.ExcludedExpenses = ExcludedExpenses.ToList();
        c.Stages = Stages.Select(s => s.Clone()).ToList();
        return c;
    }
}

/// <summary>Участник (инвестор): имя, вложение, дата и условия.</summary>
public sealed class InvestorItem : StampedRecord
{
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime Date { get; set; } = DateTime.Today;
    public string Comment { get; set; } = "";
    public InvestorTerms Terms { get; set; } = new();

    public InvestorItem Clone()
    {
        var c = (InvestorItem)MemberwiseClone();
        c.Terms = Terms.Clone();
        return c;
    }

    internal override StampedRecord CloneRecord() => Clone();
}

/// <summary>Отметка «выплачено» за месяц инвестору (Id = «инвестор|гггг-мм»).</summary>
public sealed class PayMark : StampedRecord
{
    public string InvestorId { get; set; } = "";
    /// <summary>Месяц, «гггг-мм».</summary>
    public string Month { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime PaidOn { get; set; } = DateTime.Today;

    public static string KeyOf(string investorId, string month) => investorId + "|" + month;

    public PayMark Clone() => (PayMark)MemberwiseClone();

    internal override StampedRecord CloneRecord() => Clone();
}

/// <summary>Шаблоны условий по сфере магазина (<see cref="SphereCatalog"/>): заполняют этапы, дальше всё правится вручную.</summary>
public static class InvestorTemplates
{
    public sealed record Template(string Key, string Title, List<PayStage> Stages, string? Base);

    private static PayStage S(decimal pct, string cond, decimal val) => new() { Pct = pct, Cond = cond, Val = val };

    /// <summary>Шаблоны для сферы: grocery / clothing — магазин; services — услуги (салон); hardware — хозтовары и стройматериалы.</summary>
    public static IReadOnlyList<Template> For(string? sphereId)
    {
        var share = new Template("share", Tr.T("Доля от прибыли", "Пайдадан үлүш", "Share of profit", "Kârdan pay", "Foydadan ulush"), new() { S(40, "forever", 0) }, null);
        var stepdown = new Template("stepdown", Tr.T("До окупаемости, потом меньше", "Кайтарганга чейин, андан соң азыраак", "Until payback, then less",
            "Geri dönüşe kadar, sonra daha az", "Qaytgunga qadar, keyin kamroq"), new() { S(60, "return", 100), S(30, "forever", 0) }, null);
        var cap = new Template("cap", Tr.T("С потолком 1,5×", "1,5× чек менен", "Capped at 1.5×", "1,5× tavanlı", "1,5× chegara bilan"), new() { S(50, "return", 150) }, null);
        switch (sphereId)
        {
            case MarketSpheres.Services:
                return new[]
                {
                    share,
                    new Template("stepdown", stepdown.Title, new() { S(50, "return", 100), S(25, "forever", 0) }, null),
                    new Template("term24", Tr.T("Доля на 24 месяца", "24 айга үлүш", "Share for 24 months", "24 ay pay", "24 oyga ulush"), new() { S(60, "months", 24) }, null),
                    cap,
                };
            case MarketSpheres.Hardware:
                return new[]
                {
                    new Template("retplus", Tr.T("Возврат + 20% с маржи", "Кайтаруу + маржадан 20%", "Return + 20% of margin", "İade + marjdan %20", "Qaytarish + marjadan 20%"), new() { S(50, "return", 120) }, null),
                    new Template("margin", Tr.T("Фиксированная доля маржи", "Маржанын туруктуу үлүшү", "Fixed share of margin", "Sabit marj payı", "Marjaning qat'iy ulushi"), new() { S(30, "forever", 0) }, "gross"),
                    new Template("stepdown", stepdown.Title, new() { S(60, "return", 100), S(25, "forever", 0) }, null),
                    new Template("term18", Tr.T("На срок 18 месяцев", "18 айга", "For 18 months", "18 ay süreyle", "18 oyga"), new() { S(45, "months", 18) }, null),
                };
            default:
                return new[]
                {
                    share, stepdown,
                    new Template("margin", Tr.T("Доля с наценки (валовой прибыли)", "Кошумча баадан (дүң пайдадан) үлүш", "Share of markup (gross profit)",
                        "Kâr marjından (brüt kârdan) pay", "Ustamadan (yalpi foydadan) ulush"), new() { S(25, "forever", 0) }, "gross"),
                    cap,
                };
        }
    }

    /// <summary>Условия по шаблону (копия этапов).</summary>
    public static InvestorTerms Apply(Template t, InvestorTerms current)
    {
        var c = current.Clone();
        c.Template = t.Key;
        c.Stages = t.Stages.Select(s => s.Clone()).ToList();
        if (t.Base != null)
            c.Base = t.Base;
        return c;
    }

    /// <summary>Условия по умолчанию: первый шаблон сферы «До окупаемости, потом меньше» (или первый).</summary>
    public static InvestorTerms Default(string? sphereId)
    {
        var list = For(sphereId);
        var t = list.FirstOrDefault(x => x.Key == "stepdown") ?? list[0];
        return Apply(t, new InvestorTerms());
    }

    /// <summary>Предупреждения к условиям: доля больше 100 %, пороги этапов не растут, нет этапов.</summary>
    public static List<string> Warnings(InvestorTerms terms)
    {
        var list = new List<string>();
        if (terms.Stages.Count == 0)
            list.Add(Tr.T("Добавьте хотя бы один этап выплат.", "Жок дегенде бир төлөм этабын кошуңуз.", "Add at least one payout stage.", "En az bir ödeme aşaması ekleyin.", "Kamida bitta to'lov bosqichini qo'shing."));
        decimal prev = 0;
        for (var i = 0; i < terms.Stages.Count; i++)
        {
            var st = terms.Stages[i];
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            if (st.Pct > 100m)
                list.Add(Tr.T($"Этап {n}: доля больше 100%.", $"{n}-этап: үлүш 100%дан чоң.", $"Stage {n}: share is over 100%.", $"{n}. aşama: pay %100'den fazla.", $"{n}-bosqich: ulush 100%dan katta."));
            if (st.Cond == "return")
            {
                if (st.Val <= prev)
                    list.Add(Tr.T($"Этап {n}: порог должен быть больше, чем у предыдущего этапа.", $"{n}-этап: босого мурдагыдан чоң болушу керек.",
                        $"Stage {n}: the threshold must be higher than the previous stage's.", $"{n}. aşama: eşik önceki aşamadan büyük olmalı.", $"{n}-bosqich: chegara oldingisidan katta bo'lishi kerak."));
                prev = st.Val;
            }
            if (st.Cond != "forever" && st.Val <= 0m)
                list.Add(Tr.T($"Этап {n}: укажите, сколько он действует.", $"{n}-этап: канча убакыт аракеттенерин көрсөтүңүз.", $"Stage {n}: set how long it lasts.",
                    $"{n}. aşama: ne kadar süreceğini belirtin.", $"{n}-bosqich: qancha amal qilishini ko'rsating."));
            if (st.Cond == "forever" && i < terms.Stages.Count - 1)
                list.Add(Tr.T($"Этап {n}: «бессрочно» должен быть последним.", $"{n}-этап: «чексиз» акыркы болушу керек.", $"Stage {n}: “forever” must be the last stage.",
                    $"{n}. aşama: «süresiz» sonuncu olmalı.", $"{n}-bosqich: «muddatsiz» oxirgisi bo'lishi kerak."));
        }
        if (terms.TaxPct is < 0m or > 100m || terms.ReservePct is < 0m or > 100m)
            list.Add(Tr.T("Проценты — от 0 до 100.", "Пайыздар — 0дөн 100гө чейин.", "Percentages are 0 to 100.", "Yüzdeler 0 ile 100 arasında.", "Foizlar 0 dan 100 gacha."));
        return list;
    }
}
