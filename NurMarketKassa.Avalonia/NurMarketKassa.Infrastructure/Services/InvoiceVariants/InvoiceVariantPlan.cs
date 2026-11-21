using System.Globalization;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>Одна позиция внутри объединённого товара: вкус/цвет/размер, свой штрихкод, своё количество и закупка (количество каждого варианта хранится отдельно).
/// <paramref name="Name"/> пусто — «обычный» товар без вкуса (он же основной штрихкод).</summary>
public sealed record VariantPart(string Name, string Barcode, double Qty, double? Purchase, string InvoiceName);

/// <summary>2026-11-20 (просьба владельца: «в один товар и доп. штрихкоды»: «Асу» и «Асу со вкусом клубники» — один товар): группировка вариантов при загрузке накладной через ИИ.
/// ИИ-разбор строки даёт base (название без вкуса/цвета/размера), variant, brand (ProductActionPlan.Step.Base/Variant/Brand). Строки с одним base + brand + единицей + ценой (допуск),
/// но разным variant и штрихкодом — ОДИН товар: основной штрихкод первого варианта, остальные — доп. штрихкоды с названием варианта (alternate_barcodes: barcode + name + quantity, как карточка
/// товара), количество — сумма, у каждого варианта своё. Товар уже есть в каталоге (base + brand) — новый вкус добавляется ему доп. штрихкодом, а не новым товаром. Цены разные — автоматически
/// не объединяем, окно спрашивает «Объединить? цены разные». Класс без окон и сети (каталог — через подменяемые поиски): план живёт в карточке, правки владельца (разъединить, объединить с…,
/// название варианта) меняют его, ToSteps() даёт шаги для закупки. Ничего не пишется, пока владелец не подтвердит карточку.</summary>
public sealed class InvoiceVariantPlan
{
    /// <summary>Допуск цен «одна цена»: до 1 % (и не меньше 0,01 сома) — копеечные расхождения округления не считаются разными ценами.</summary>
    public const double PriceTolerancePercent = 1.0;

    public sealed class Member
    {
        public required ProductActionPlan.Step Step { get; set; }
        /// <summary>Вкус/цвет/размер, правится владельцем. Пусто — товар без варианта.</summary>
        public string Variant { get; set; } = "";
        public string Barcode => (Step.Barcode ?? "").Trim();
        public double Qty => Step.Qty ?? 0;
        public string InvoiceName => string.IsNullOrWhiteSpace(Step.InvoiceName) ? Step.Product.Title : Step.InvoiceName!;
        /// <summary>Цена, по которой сравниваем «одна цена»: закупка, а если её нет — розничная.</summary>
        public double? CompareValue => Step.Purchase is > 0 ? Step.Purchase : Step.Price is > 0 ? Step.Price : null;
    }

    public sealed class Entry
    {
        /// <summary>Не накладная (open, set_*, stock_*, photo…) — проходит как есть.</summary>
        public ProductActionPlan.Step? Passthrough { get; init; }
        public List<Member> Members { get; } = [];
        /// <summary>Название товара без варианта (общее у всех вариантов).</summary>
        public string Base { get; set; } = "";
        public string Brand { get; set; } = "";
        /// <summary>Товар каталога, к которому добавляются варианты (null — создаётся новый).</summary>
        public CatalogProductTileVm? Existing { get; set; }
        /// <summary>Основной штрихкод, введённый владельцем в карточке (null — по умолчанию код первого варианта с кодом).</summary>
        public string? MainCode { get; set; }
        /// <summary>Замечания для строки (штрихкод не прочитан, код у другого товара).</summary>
        public List<string> Notes { get; } = [];

        public bool IsPassthrough => Passthrough is not null;
        /// <summary>Объединённый товар: два и больше варианта, или вкус добавляется существующему товару.</summary>
        public bool IsGroup => Passthrough is null && (Members.Count >= 2 || (Existing is not null && Members.Any(m => m.Variant.Length > 0)));
        public string Key => Passthrough is not null || Base.Length == 0 ? "" : Norm(Base) + "|" + Norm(Brand) + "|" + Norm(Unit);
        public string Unit => Members.Count == 0 ? "" : UnitOf(Members[0].Step);
        public double TotalQty => Members.Sum(m => m.Qty);
        /// <summary>Заголовок строки для показа: у группы — общее название, у одиночной строки — название из накладной.</summary>
        public string Title => IsGroup ? Base : Members.Count > 0 ? Members[0].InvoiceName : Passthrough?.Product.Title ?? "";
    }

    /// <summary>Параметры сборки: объединять ли вообще и подмены поисков (в программе — каталог кассы).</summary>
    public sealed class Options
    {
        public bool Merge { get; init; } = true;
        /// <summary>Бренд из своей команды («бренд — Асу»): подставляется строкам, где ИИ бренд не прочитал.</summary>
        public string? BrandOverride { get; init; }
        public Func<string, string, CatalogProductTileVm?>? FindExisting { get; init; }
        public Func<string, CatalogProductTileVm?>? OwnerOfCode { get; init; }
    }

    private readonly Options _options;
    public List<Entry> Entries { get; } = [];

    private InvoiceVariantPlan(Options options) => _options = options;

    public bool MergeEnabled => _options.Merge;

    // ------------------------------------------------------------------ сборка

    /// <summary>Шаги карточки (после ProductActionPlan.Extract) → план. Объединение выключено — каждая строка остаётся своим шагом, как раньше.</summary>
    public static InvoiceVariantPlan Build(IReadOnlyList<ProductActionPlan.Step> steps, Options? options = null)
    {
        options ??= new Options();
        var plan = new InvoiceVariantPlan(options);
        var slots = new List<(int Pos, Entry Entry)>();
        var byKey = new Dictionary<string, List<(int Pos, Member Member)>>(StringComparer.Ordinal);
        var keyOrder = new List<string>();
        for (var i = 0; i < steps.Count; i++)
        {
            var st = steps[i];
            if (!ProductActionPlan.IsPurchaseStep(st) || !options.Merge)
            {
                slots.Add((i, Single(st, options)));
                continue;
            }
            var brand = BrandOf(st, options);
            var member = new Member { Step = st, Variant = (st.Variant ?? "").Trim() };
            var baseName = (st.Base ?? "").Trim();
            if (baseName.Length == 0)
            {
                slots.Add((i, SingleMember(member, options)));
                continue;
            }
            var key = Norm(baseName) + "|" + Norm(brand) + "|" + Norm(UnitOf(st));
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = [];
                keyOrder.Add(key);
            }
            list.Add((i, member));
        }

        foreach (var key in keyOrder)
        {
            var list = byKey[key];
            var members = list.Select(x => x.Member).ToList();
            var baseName = (members[0].Step.Base ?? "").Trim();
            var brand = BrandOf(members[0].Step, options);
            var existing = FindExistingFor(baseName, brand, members, options);
            var hasVariant = members.Any(m => m.Variant.Length > 0);
            // Ни одной строки с вариантом и товара в каталоге нет — это просто повтор позиции, объединением вариантов тут не занимаемся (повторы сводит закупка).
            if (!hasVariant)
            {
                foreach (var (pos, m) in list)
                    slots.Add((pos, SingleMember(m, options)));
                continue;
            }
            // Строки с кодом, который уже у ДРУГОГО товара каталога, — отдельно (их разберёт обычный путь: приход к тому товару).
            var grouped = new List<(int Pos, Member Member)>();
            foreach (var item in list)
            {
                if (item.Member.Barcode.Length > 0 && options.OwnerOfCode?.Invoke(item.Member.Barcode) is { } owner && (existing is null || !SameProduct(owner, existing)))
                    slots.Add((item.Pos, SingleMember(item.Member, options)));
                else
                    grouped.Add(item);
            }
            if (grouped.Count == 0)
                continue;
            // Кластеры по цене: «одна цена» — внутри допуска.
            var clusters = new List<List<(int Pos, Member Member)>>();
            foreach (var item in grouped)
            {
                var placed = clusters.FirstOrDefault(c => PricesClose(c[0].Member.CompareValue, item.Member.CompareValue));
                if (placed is null)
                    clusters.Add([item]);
                else
                    placed.Add(item);
            }
            foreach (var cluster in clusters)
            {
                var entry = NewGroupEntry(baseName, brand, existing, cluster.Select(c => c.Member));
                // Один вариант без пары и без товара в каталоге — не группа (останется самим собой, но с общим именем в подсказке «Объединить?»).
                slots.Add((cluster.Min(c => c.Pos), entry));
            }
        }
        foreach (var (_, e) in slots.OrderBy(s => s.Pos))
        {
            plan.Entries.Add(e);
            plan.Validate(e);
        }
        return plan;
    }

    private static Entry NewGroupEntry(string baseName, string brand, CatalogProductTileVm? existing, IEnumerable<Member> members)
    {
        var entry = new Entry { Base = baseName, Brand = brand, Existing = existing };
        // Обычный товар без вкуса — первым: его код станет основным.
        foreach (var m in members.OrderBy(m => m.Variant.Length == 0 ? 0 : 1))
            entry.Members.Add(m);
        return entry;
    }

    private static Entry Single(ProductActionPlan.Step st, Options options)
    {
        if (!ProductActionPlan.IsPurchaseStep(st))
            return new Entry { Passthrough = st };
        return SingleMember(new Member { Step = st, Variant = (st.Variant ?? "").Trim() }, options);
    }

    private static Entry SingleMember(Member m, Options options)
    {
        var entry = new Entry { Base = (m.Step.Base ?? "").Trim(), Brand = BrandOf(m.Step, options) };
        entry.Members.Add(m);
        return entry;
    }

    private static string BrandOf(ProductActionPlan.Step st, Options options) =>
        (st.Brand ?? "").Trim() is { Length: > 0 } b ? b : (options.BrandOverride ?? "").Trim();

    private static CatalogProductTileVm? FindExistingFor(string baseName, string brand, List<Member> members, Options options)
    {
        // Шаг уже «приход к товару склада» (ИИ назвал товар из каталога) — он и есть существующий.
        if (members.Select(m => m.Step).FirstOrDefault(s => s.Op == "receive" && s.Product.Id.Length > 0) is { } received)
            return received.Product;
        // Товар склада по названию и марке (base + brand). Коды вариантов владельца не определяют: код чужого товара выводит строку из группы (см. Build), но не делает чужой товар «существующим».
        return options.FindExisting?.Invoke(baseName, brand);
    }

    private static bool SameProduct(CatalogProductTileVm a, CatalogProductTileVm b) =>
        a.Id.Length > 0 && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Одна цена: разница не больше допуска (1 %, не меньше 0,01). Нет цены у обеих — одна (нечего сравнивать); у одной — разные.</summary>
    public static bool PricesClose(double? a, double? b)
    {
        if (a is null && b is null)
            return true;
        if (a is null || b is null)
            return false;
        var tol = Math.Max(0.01, Math.Max(Math.Abs(a.Value), Math.Abs(b.Value)) * PriceTolerancePercent / 100);
        return Math.Abs(a.Value - b.Value) <= tol;
    }

    private void Validate(Entry e)
    {
        e.Notes.Clear();
        if (!e.IsGroup)
            return;
        foreach (var m in e.Members)
        {
            if (m.Barcode.Length == 0 && e.Members.Count > 1)
                e.Notes.Add(Tr.T($"у варианта «{DisplayVariant(m)}» нет штрихкода — дополнительный код не создаётся", $"«{DisplayVariant(m)}» вариантынын штрихкоду жок — кошумча код түзүлбөйт",
                    $"variant “{DisplayVariant(m)}” has no barcode — no extra code is created", $"«{DisplayVariant(m)}» varyantının barkodu yok — ek kod oluşturulmaz", $"«{DisplayVariant(m)}» variantining shtrix-kodi yo'q — qo'shimcha kod yaratilmaydi"));
            else if (m.Barcode.Length > 0 && !InvoiceBarcodeRules.IsValidNew(InvoiceBarcodeRules.Clean(m.Barcode)))
                e.Notes.Add(Tr.T($"штрихкод «{m.Barcode}» варианта «{DisplayVariant(m)}» — не 4–14 цифр, как дополнительный не запишется", $"«{DisplayVariant(m)}» вариантынын «{m.Barcode}» штрихкоду — 4–14 цифра эмес, кошумча катары жазылбайт",
                    $"barcode “{m.Barcode}” of variant “{DisplayVariant(m)}” is not 4–14 digits — it will not be saved as an extra code", $"«{DisplayVariant(m)}» varyantının «{m.Barcode}» barkodu 4–14 rakam değil — ek kod olarak yazılmaz", $"«{DisplayVariant(m)}» variantining «{m.Barcode}» shtrix-kodi 4–14 raqam emas — qo'shimcha kod sifatida yozilmaydi"));
        }
    }

    public static string DisplayVariant(Member m) =>
        m.Variant.Length > 0 ? m.Variant : Tr.T("обычный", "кадимки", "plain", "normal", "oddiy");

    // ------------------------------------------------------------------ в шаги

    /// <summary>Шаги для закупки: объединённый товар — один шаг (create — новый товар, receive — приход к существующему) со всеми вариантами в <see cref="ProductActionPlan.Step.Variants"/>;
    /// одиночные строки — как пришли.</summary>
    public List<ProductActionPlan.Step> ToSteps()
    {
        var result = new List<ProductActionPlan.Step>();
        foreach (var e in Entries)
        {
            if (e.Passthrough is { } pass)
            {
                result.Add(pass);
                continue;
            }
            if (!e.IsGroup)
            {
                var m = e.Members[0];
                result.Add(m.Step with { Variants = null });
                continue;
            }
            result.Add(MergedStep(e));
        }
        return result;
    }

    private static ProductActionPlan.Step MergedStep(Entry e)
    {
        var first = e.Members[0].Step;
        double qty = 0;
        double? purchase = null;
        double? price = null;
        double? lineTotal = 0;
        double? confidence = null;
        var unclear = false;
        foreach (var m in e.Members)
        {
            var s = m.Step;
            purchase = s.Purchase is null ? purchase : purchase is null ? s.Purchase : Math.Round((qty * purchase.Value + (s.Qty ?? 0) * s.Purchase.Value) / Math.Max(1e-9, qty + (s.Qty ?? 0)), 4);
            qty += s.Qty ?? 0;
            price ??= s.Price;
            lineTotal = lineTotal is null || s.LineTotal is null ? null : lineTotal + s.LineTotal;
            confidence = s.Confidence is null ? confidence : confidence is null ? s.Confidence : Math.Min(confidence.Value, s.Confidence.Value);
            unclear |= s.Unclear;
        }
        var parts = e.Members.Select(m => new VariantPart(m.Variant, m.Barcode, m.Qty, m.Step.Purchase, m.InvoiceName)).ToList();
        var mainCode = e.Existing is null ? (e.MainCode is { Length: > 0 } typed ? typed : e.Members.Select(m => m.Barcode).FirstOrDefault(c => c.Length > 0)) : null;
        var title = e.Base;
        var existing = e.Existing;
        var priceKept = price;
        double? notLowered = null;
        if (existing is not null)
            (priceKept, notLowered) = ProductActionPlan.KeepCurrentPriceIfLower(price, existing.PriceValue);
        return first with
        {
            Op = existing is null ? "create" : "receive",
            Product = existing ?? new CatalogProductTileVm("", title, $"{price ?? 0:0.00}", false),
            Qty = qty,
            Purchase = purchase,
            Price = existing is null ? price : priceKept,
            PriceNotLowered = notLowered,
            Barcode = mainCode,
            Base = e.Base,
            Brand = e.Brand.Length > 0 ? e.Brand : null,
            Variant = null,
            Variants = parts,
            LineTotal = lineTotal,
            Confidence = confidence,
            Unclear = unclear,
            ExpectedStock = existing?.Quantity,
            InvoiceName = null,
        };
    }

    /// <summary>Доп. штрихкоды для шага: варианты с кодом, кроме основного <paramref name="mainCode"/> (если владелец поменял основной код — прежний основной станет дополнительным).</summary>
    public static List<AlternateBarcodeVariant> ExtraCodes(ProductActionPlan.Step step, string? mainCode) => ExtraCodes(step.Variants, mainCode);

    /// <summary>То же по списку вариантов (окно закупки хранит варианты строки и пересчитывает доп. коды от основного, который владелец мог поменять).</summary>
    public static List<AlternateBarcodeVariant> ExtraCodes(IReadOnlyList<VariantPart>? variants, string? mainCode)
    {
        var result = new List<AlternateBarcodeVariant>();
        if (variants is not { Count: > 0 } parts)
            return result;
        foreach (var p in parts)
        {
            var code = InvoiceBarcodeRules.Clean(p.Barcode);
            if (code.Length == 0 || !InvoiceBarcodeRules.IsValidNew(code) || InvoiceBarcodeRules.Same(code, mainCode) || result.Any(r => InvoiceBarcodeRules.Same(r.Barcode, code)))
                continue;
            result.Add(new AlternateBarcodeVariant { Barcode = code, Name = p.Name.Length > 0 ? VariantTitle(p.Name) : null });
        }
        return result;
    }

    /// <summary>Разбивка прихода по вариантам для примечания: «Асу: Клубничный 10, Лимонный 5». Нет вариантов — пусто.</summary>
    public static string BreakdownNote(ProductActionPlan.Step step)
    {
        if (step.Variants is not { Count: > 1 } parts)
            return "";
        var title = step.Product.Title;
        var items = parts.Select(p => (p.Name.Length > 0 ? VariantTitle(p.Name) : Tr.T("обычный", "кадимки", "plain", "normal", "oddiy")) + " " + p.Qty.ToString("0.###", CultureInfo.InvariantCulture));
        return title + ": " + string.Join(", ", items);
    }

    /// <summary>Название варианта для доп. штрихкода: с заглавной буквы («клубничный» → «Клубничный»).</summary>
    public static string VariantTitle(string? name)
    {
        var t = Regex.Replace(name ?? "", @"\s+", " ").Trim();
        return t.Length == 0 ? "" : char.ToUpper(t[0], CultureInfo.CurrentCulture) + t[1..];
    }

    // ------------------------------------------------------------------ итоги для окна

    /// <summary>«Товаров: N» — товарные позиции карточки (одиночные и объединённые), без служебных шагов.</summary>
    public int ProductCount => Entries.Count(e => !e.IsPassthrough);

    /// <summary>«Вариантов / доп. кодов: M» — сколько вариантов вошло в объединённые товары (дополнительные штрихкоды и добавляемые вкусы).</summary>
    public int VariantCount => Entries.Where(e => e.IsGroup).Sum(e => e.Members.Count);

    /// <summary>Сколько доп. штрихкодов создастся: варианты с годным кодом, кроме основного.</summary>
    public int ExtraCodeCount => Entries.Where(e => e.IsGroup).Sum(e => ExtraCodes(MergedStep(e), MergedStep(e).Barcode).Count);

    // ------------------------------------------------------------------ правки владельца

    /// <summary>Объединённых и «спорных» нет — окно не показывает блок вариантов.</summary>
    public bool HasAnything => Entries.Any(e => e.IsGroup) || Entries.Any(e => PriceConflictFor(e) is not null) || SplitMates().Count > 0;

    /// <summary>«Разъединить» один вариант: он становится отдельным товаром (как в накладной).</summary>
    public void Split(Entry entry, Member member)
    {
        if (!entry.Members.Remove(member))
            return;
        var index = Entries.IndexOf(entry);
        var detached = new Entry { Base = entry.Base, Brand = entry.Brand };
        detached.Members.Add(member);
        // Вариант остаётся отдельным товаром, а не добавлением к существующему.
        if (entry.Members.Count == 0)
            Entries.Remove(entry);
        else
            Validate(entry);
        Entries.Insert(Math.Min(Entries.Count, index + 1), detached);
        Validate(detached);
        if (entry.Members.Count == 1 && entry.Existing is not null && entry.Members[0].Variant.Length == 0)
            entry.Existing = null;
    }

    /// <summary>«Разъединить» всю группу: каждая строка накладной — отдельным товаром.</summary>
    public void SplitAll(Entry entry)
    {
        if (!entry.IsGroup)
            return;
        var index = Entries.IndexOf(entry);
        var members = entry.Members.ToList();
        Entries.Remove(entry);
        foreach (var (m, i) in members.Select((m, i) => (m, i)))
        {
            var e = new Entry { Base = entry.Base, Brand = entry.Brand };
            e.Members.Add(m);
            Entries.Insert(index + i, e);
            Validate(e);
        }
    }

    /// <summary>Другие товарные позиции, с которыми <paramref name="source"/> можно объединить («Объединить с …»): не служебные, та же единица.</summary>
    public List<Entry> MergeTargets(Entry source) =>
        Entries.Where(e => !ReferenceEquals(e, source) && !e.IsPassthrough && e.Members.Count > 0 && UnitsEqual(e, source)).ToList();

    private static bool UnitsEqual(Entry a, Entry b) => Norm(a.Unit) == Norm(b.Unit);

    /// <summary>Объединить <paramref name="source"/> (одиночную позицию или группу) с <paramref name="target"/>. false — нельзя (другая единица или служебная строка).</summary>
    public bool Merge(Entry source, Entry target)
    {
        if (ReferenceEquals(source, target) || source.IsPassthrough || target.IsPassthrough || !UnitsEqual(source, target))
            return false;
        if (target.Base.Length == 0)
            target.Base = CommonPrefixWords(target.Members[0].InvoiceName, source.Members[0].InvoiceName) is { Length: > 0 } common ? common : target.Members[0].InvoiceName;
        if (target.Brand.Length == 0)
            target.Brand = source.Brand;
        foreach (var m in source.Members.ToList())
        {
            if (m.Variant.Length == 0 && !ReferenceEquals(m, target.Members.FirstOrDefault()))
                m.Variant = DeriveVariant(m.InvoiceName, target.Base);
            target.Members.Add(m);
        }
        Entries.Remove(source);
        // У самой целевой позиции без варианта название — обычный товар (основной код), остальные — по порядку накладной.
        var ordered = target.Members.OrderBy(m => m.Variant.Length == 0 ? 0 : 1).ToList();
        target.Members.Clear();
        target.Members.AddRange(ordered);
        if (target.Existing is null && source.Existing is not null)
            target.Existing = source.Existing;
        Validate(target);
        return true;
    }

    /// <summary>Спор «цены разные»: позиция того же товара (base + марка + единица) есть, но с другой ценой, и они не объединены. null — спора нет.</summary>
    public (Entry Other, double? ThisPrice, double? OtherPrice)? PriceConflictFor(Entry e)
    {
        if (e.IsPassthrough || e.Key.Length == 0 || !_options.Merge)
            return null;
        foreach (var other in Entries)
        {
            if (ReferenceEquals(other, e) || other.IsPassthrough || other.Key != e.Key)
                continue;
            // Варианты ищем только если хоть у одной из позиций есть вкус (повтор одной позиции — не спор).
            if (!e.Members.Concat(other.Members).Any(m => m.Variant.Length > 0))
                continue;
            // Цены близки — это не спор «цены разные» (разъединённые владельцем одноценные позиции — см. SplitMates).
            if (PricesClose(e.Members[0].CompareValue, other.Members[0].CompareValue))
                continue;
            return (other, e.Members[0].CompareValue, other.Members[0].CompareValue);
        }
        return null;
    }

    /// <summary>Разъединённые владельцем позиции того же товара (base + марка + единица) с той же ценой — их можно вернуть в группу («Объединить снова»).</summary>
    public List<Entry> SplitMates() => !_options.Merge ? [] :
        Entries.Where(e => !e.IsGroup && !e.IsPassthrough && e.Key.Length > 0 && Entries.Any(o => !ReferenceEquals(o, e) && !o.IsPassthrough && o.Key == e.Key
            && e.Members.Concat(o.Members).Any(m => m.Variant.Length > 0) && PricesClose(e.Members[0].CompareValue, o.Members[0].CompareValue))).ToList();

    /// <summary>«Объединить снова»: разъединённые позиции с той же ценой собираются обратно (по ключу и цене; группа, если она есть, принимает их в себя).</summary>
    public void MergeSplit()
    {
        foreach (var e in SplitMates())
        {
            if (!Entries.Contains(e))
                continue;
            var target = Entries.Where(o => !ReferenceEquals(o, e) && !o.IsPassthrough && o.Key == e.Key && PricesClose(e.Members[0].CompareValue, o.Members[0].CompareValue))
                .OrderBy(o => o.IsGroup ? 0 : 1).ThenBy(o => Entries.IndexOf(o)).FirstOrDefault();
            if (target is null)
                continue;
            Merge(e, target);
        }
    }

    /// <summary>Правка названия варианта; пустое — товар без варианта.</summary>
    public void RenameVariant(Member member, string? text)
    {
        member.Variant = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        foreach (var e in Entries.Where(e => e.Members.Contains(member)))
            Validate(e);
    }

    /// <summary>Коды, которые владелец ввёл в блоке «Штрихкоды товаров» карточки (по номеру шага = номеру позиции в <see cref="ToSteps"/>), — переносятся в план при пересборке карточки.
    /// Пустые не трогают то, что есть. У одиночной позиции код заменяет штрихкод строки, у группы — основной код (прежний основной станет дополнительным).</summary>
    public void ApplyTypedCodes(IReadOnlyDictionary<int, string> codesByIndex)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (!codesByIndex.TryGetValue(i, out var raw) || raw.Trim() is not { Length: > 0 } code)
                continue;
            var e = Entries[i];
            if (e.IsPassthrough || e.Members.Count == 0 || (e.Existing is not null && e.IsGroup) || (!e.IsGroup && e.Members[0].Step.Op == "receive"))
                continue;
            if (e.IsGroup)
                e.MainCode = code;
            else
                e.Members[0].Step = e.Members[0].Step with { Barcode = code };
        }
    }

    // ------------------------------------------------------------------ вспомогательное

    private static string UnitOf(ProductActionPlan.Step st) => ProductUnitNormalizer.IsKilogramWord(st.Unit) ? "кг" : string.IsNullOrWhiteSpace(st.Unit) ? "шт" : st.Unit!.Trim();

    /// <summary>Ключ сравнения: строчные, ё=е, без знаков.</summary>
    public static string Norm(string? text) =>
        Regex.Replace((text ?? "").ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+", " ").Trim();

    /// <summary>Общие первые слова двух названий: «Асу» и «Асу лимон» → «Асу».</summary>
    public static string CommonPrefixWords(string? a, string? b)
    {
        var wa = Regex.Split((a ?? "").Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
        var wb = Regex.Split((b ?? "").Trim(), @"\s+").Where(w => w.Length > 0).ToArray();
        var n = 0;
        while (n < wa.Length && n < wb.Length && Norm(wa[n]) == Norm(wb[n]))
            n++;
        return string.Join(' ', wa.Take(n));
    }

    /// <summary>Вкус из полного названия: убираем общее начало («Асу со вкусом клубники» − «Асу» → «со вкусом клубники»); не вышло — название целиком.</summary>
    public static string DeriveVariant(string? invoiceName, string? baseName)
    {
        var full = Regex.Replace(invoiceName ?? "", @"\s+", " ").Trim();
        var b = Regex.Replace(baseName ?? "", @"\s+", " ").Trim();
        if (b.Length > 0 && full.StartsWith(b, StringComparison.CurrentCultureIgnoreCase) && full.Length > b.Length)
        {
            var rest = full[b.Length..].Trim(' ', '-', '–', '—', ',', '.');
            rest = Regex.Replace(rest, @"^(со\s+вкусом|вкус|с\s+ароматом|аромат|цвет|размер)\s+", "", RegexOptions.IgnoreCase).Trim();
            if (rest.Length > 0)
                return rest;
        }
        return full;
    }

    /// <summary>Поиск товара каталога по base и марке: название равно base (или «марка base»); если у товара указана марка, она должна совпасть. Нет или неоднозначно — null.</summary>
    public static CatalogProductTileVm? FindInCatalog(IEnumerable<CatalogProductTileVm> catalog, string baseName, string brand)
    {
        var b = Norm(baseName);
        if (b.Length == 0)
            return null;
        var withBrand = Norm(brand).Length > 0 && !b.StartsWith(Norm(brand) + " ", StringComparison.Ordinal) && b != Norm(brand) ? Norm(brand) + " " + b : "";
        var hits = catalog.Where(p =>
        {
            var t = Norm(p.Title);
            if (t != b && (withBrand.Length == 0 || t != withBrand))
                return false;
            var pb = Norm(p.Brand);
            return pb.Length == 0 || Norm(brand).Length == 0 || pb == Norm(brand);
        }).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Подмены поисков для программы: каталог кассы (CatalogCacheService.Products). Каталог пуст/недоступен — null (товар считается новым).</summary>
    public static Options ProgramOptions(bool merge, string? brandOverride) => new()
    {
        Merge = merge,
        BrandOverride = brandOverride,
        FindExisting = (b, br) => SafeCatalog() is { } all ? FindInCatalog(all, b, br) : null,
        OwnerOfCode = code => SafeCatalog() is { } all ? InvoiceBarcodeRules.OwnerIn(all, InvoiceBarcodeRules.Clean(code)) : null,
    };

    private static List<CatalogProductTileVm>? SafeCatalog()
    {
        try
        {
            return CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
