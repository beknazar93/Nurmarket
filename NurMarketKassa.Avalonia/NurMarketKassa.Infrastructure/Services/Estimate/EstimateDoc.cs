using System.Globalization;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-19 («Смета» — калькулятор для строймаркета, ТЗ 19.11.2026): статус сметы. «Принята» ставится автоматически после «Добавить всё в чек» (только касса).</summary>
public enum EstimateStatus
{
    Draft,
    Sent,
    Accepted,
}

/// <summary>Уровень цены сметы: розница или «прораб» (оптовая цена товара; нет оптовой — розница).</summary>
public enum EstimateLevel
{
    Retail,
    Foreman,
}

/// <summary>Раздел сметы для группировки позиций («Стены», «Пол», «Работа»).</summary>
public sealed class EstimateSection
{
    public string Id { get; set; } = EstimateRules.NewId();
    public string Name { get; set; } = "";
}

/// <summary>Одна позиция сметы. Цена, закупка и остаток — НА МОМЕНТ составления (смета хранит цены; «Пересчитать цены» обновляет их из каталога).
/// <see cref="Qty"/> — нужное количество без запаса; запас строки — <see cref="ReservePercent"/> (null — запас всей сметы).</summary>
public sealed class EstimateLine
{
    public string Id { get; set; } = EstimateRules.NewId();
    /// <summary>Раздел сметы (пусто — без раздела).</summary>
    public string? SectionId { get; set; }
    /// <summary>Товар каталога; пусто — своя позиция (работа, доставка, услуга).</summary>
    public string? ProductId { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Qty { get; set; } = 1m;
    /// <summary>Цена за единицу до скидки строки.</summary>
    public decimal Price { get; set; }
    /// <summary>Закупочная цена за единицу на момент сметы (0 — неизвестна, у своей позиции — по желанию). Кассиру не показывается.</summary>
    public decimal Purchase { get; set; }
    public decimal DiscountPercent { get; set; }
    /// <summary>Запас строки, %: null — запас сметы. Расчёт материала уже включает запас и кладёт сюда 0.</summary>
    public decimal? ReservePercent { get; set; }
    /// <summary>Остаток товара на момент сметы (null — неизвестен); нужен, чтобы при переносе в чек показать, что изменилось.</summary>
    public decimal? StockAtTime { get; set; }
    /// <summary>Формула расчёта материала («Площадь 14,4 м² + запас 10 % → 10 уп.»), если строка получена калькулятором.</summary>
    public string? CalcNote { get; set; }

    [JsonIgnore] public bool IsCustom => string.IsNullOrWhiteSpace(ProductId);

    public EstimateLine Clone() => (EstimateLine)MemberwiseClone();
}

/// <summary>Смета: шапка, разделы, позиции. Хранится локально (<see cref="EstimateStore"/>), на сервер не уходит.</summary>
public sealed class EstimateDoc
{
    public string Id { get; set; } = EstimateRules.NewId();
    public int Number { get; set; }
    /// <summary>Название / объект («Ремонт комнаты 18 м²»).</summary>
    public string Title { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string? ClientId { get; set; }
    public string Phone { get; set; } = "";
    /// <summary>Адрес объекта.</summary>
    public string Address { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Цены на момент составления (по ним считается «действует до» и «Пересчитать»).</summary>
    public DateTime PricesAt { get; set; }
    /// <summary>Действует до (по умолчанию 7 дней).</summary>
    public DateTime ValidUntil { get; set; }
    public EstimateStatus Status { get; set; }
    public EstimateLevel Level { get; set; }
    /// <summary>Запас всей сметы, %: для строк без своего запаса. По умолчанию 0 — запас кладёт калькулятор материала (10 %) или владелец по строке/всей смете.</summary>
    public decimal ReservePercent { get; set; }
    /// <summary>Общая скидка на смету, %.</summary>
    public decimal DiscountPercent { get; set; }
    public List<EstimateSection> Sections { get; set; } = new();
    public List<EstimateLine> Lines { get; set; } = new();
}

/// <summary>Что лежит в файле хранилища: сметы, счётчик номеров и запомненные привязки «материал → товар».</summary>
public sealed class EstimateData
{
    public int LastNumber { get; set; }
    public List<EstimateDoc> Estimates { get; set; } = new();
    /// <summary>Привязки калькулятора: ключ материала (paint, tile…) → id товара каталога (запоминается при выборе).</summary>
    public Dictionary<string, string> Links { get; set; } = new();
}

/// <summary>Товар каталога в виде, нужном смете (цены — decimal; разбор карточки — <see cref="EstimateRules.FromProduct"/>).</summary>
public sealed record EstimateCatalogInfo(string Id, string Name, string Unit, decimal Retail, decimal Wholesale, decimal Purchase, decimal Stock, bool IsService);

/// <summary>
/// 2026-11-19: правила сметы — ЧИСТАЯ логика без окон и сети: запас, суммы строк, скидки, итог, себестоимость и маржа, срок «действует до», пересчёт цен по каталогу,
/// копия сметы. Деньги — decimal, округление до копеек «от нуля вверх» (как RepairRules.Money). Запас округляется ВВЕРХ: до целого у штучных единиц (упаковка, шт, лист, рулон),
/// до сотых у дробных (м², л, кг, м) — недобор материала хуже перебора (ТЗ). Окна ничего не считают сами (проверка check-money-in-views).
/// </summary>
public static class EstimateRules
{
    public const int DefaultValidDays = 7;
    public const decimal MaxQty = 1_000_000m;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static string Code(int number) => "С-" + number.ToString("0000", CultureInfo.InvariantCulture);

    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Число из поля: пробелы, запятая или точка; пусто — null; не число / отрицательное / слишком большое — null.</summary>
    public static decimal? TryParseNumber(string? text, decimal max = 100_000_000m)
    {
        var t = (text ?? "").Replace(" ", "").Replace(" ", "").Replace(',', '.').Trim();
        if (t.Length == 0)
            return null;
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= max ? v : null;
    }

    /// <summary>Единица продаётся только целыми (упаковка, шт, лист, рулон, мешок…)? Неизвестная единица — целыми.</summary>
    public static bool IsWholeUnit(string? unit) => HardwareProfile.FindUnit(unit)?.Fractional != true;

    /// <summary>Нужное количество с запасом: <c>Qty × (1 + запас)</c>, вверх до целого (штучные) или до сотых (дробные). Запас вне 0…100 — 0.</summary>
    public static decimal EffectiveQty(EstimateLine line, EstimateDoc doc)
    {
        var qty = Math.Max(0m, line.Qty);
        var reserve = line.ReservePercent ?? doc.ReservePercent;
        if (reserve is <= 0m or > 100m)
            return qty;
        var withReserve = qty * (1m + reserve / 100m);
        // 6 знаков отсекают «шум» деления: 10 × 1,1 не должно стать 11,0000001 → 12.
        return IsWholeUnit(line.Unit)
            ? Math.Ceiling(Math.Round(withReserve, 6, MidpointRounding.AwayFromZero))
            : Math.Ceiling(Math.Round(withReserve * 100m, 6, MidpointRounding.AwayFromZero)) / 100m;
    }

    /// <summary>Цена строки после её скидки, за единицу.</summary>
    public static decimal NetPrice(EstimateLine line) => Money(line.Price * (1m - Math.Clamp(line.DiscountPercent, 0m, 100m) / 100m));

    /// <summary>Сумма строки: количество с запасом × цена после скидки строки.</summary>
    public static decimal LineSum(EstimateLine line, EstimateDoc doc) => Money(EffectiveQty(line, doc) * NetPrice(line));

    public static decimal Subtotal(EstimateDoc doc) => Money(doc.Lines.Sum(l => LineSum(l, doc)));

    /// <summary>Сумма общей скидки сметы.</summary>
    public static decimal DiscountAmount(EstimateDoc doc) => Money(Subtotal(doc) * Math.Clamp(doc.DiscountPercent, 0m, 100m) / 100m);

    public static decimal Total(EstimateDoc doc) => Money(Subtotal(doc) - DiscountAmount(doc));

    public static decimal SectionTotal(EstimateDoc doc, string? sectionId) =>
        Money(doc.Lines.Where(l => string.IsNullOrWhiteSpace(l.SectionId) ? string.IsNullOrWhiteSpace(sectionId) : l.SectionId == sectionId).Sum(l => LineSum(l, doc)));

    /// <summary>Себестоимость сметы (количество с запасом × закупка). Только для владельца и сотрудников с правом на закупку.</summary>
    public static decimal Cost(EstimateDoc doc) => Money(doc.Lines.Sum(l => EffectiveQty(l, doc) * l.Purchase));

    /// <summary>Маржа сметы = итог − себестоимость.</summary>
    public static decimal Margin(EstimateDoc doc) => Money(Total(doc) - Cost(doc));

    /// <summary>Маржа в % от итога (0, если итог нулевой).</summary>
    public static decimal MarginPercent(EstimateDoc doc)
    {
        var total = Total(doc);
        return total <= 0m ? 0m : Math.Round(Margin(doc) / total * 100m, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>У всех ли строк-товаров известна закупка (иначе маржа неполная).</summary>
    public static bool CostKnown(EstimateDoc doc) => doc.Lines.Where(l => !l.IsCustom).All(l => l.Purchase > 0m);

    /// <summary>Срок «действует до» прошёл, а смета не принята — «Цены могли измениться».</summary>
    public static bool IsExpired(EstimateDoc doc, DateTime today) => doc.Status != EstimateStatus.Accepted && doc.ValidUntil.Date < today.Date;

    /// <summary>Новая смета: номер, даты, срок 7 дней, уровень «розница».</summary>
    public static EstimateDoc NewDoc(int number, DateTime now) => new()
    {
        Number = number,
        CreatedAt = now,
        UpdatedAt = now,
        PricesAt = now,
        ValidUntil = now.Date.AddDays(DefaultValidDays),
    };

    /// <summary>Копия сметы: новый id и номер, статус «Черновик», даты от сегодняшнего дня (цены остаются прежними до «Пересчитать»).</summary>
    public static EstimateDoc Duplicate(EstimateDoc source, int number, DateTime now, string titleSuffix)
    {
        var sectionMap = source.Sections.ToDictionary(s => s.Id, _ => NewId());
        var copy = NewDoc(number, now);
        copy.Title = string.IsNullOrWhiteSpace(source.Title) ? titleSuffix.Trim() : source.Title.Trim() + " " + titleSuffix;
        copy.ClientName = source.ClientName;
        copy.ClientId = source.ClientId;
        copy.Phone = source.Phone;
        copy.Address = source.Address;
        copy.Note = source.Note;
        copy.Level = source.Level;
        copy.ReservePercent = source.ReservePercent;
        copy.DiscountPercent = source.DiscountPercent;
        copy.PricesAt = source.PricesAt;
        copy.Sections = source.Sections.Select(s => new EstimateSection { Id = sectionMap[s.Id], Name = s.Name }).ToList();
        copy.Lines = source.Lines.Select(l =>
        {
            var c = l.Clone();
            c.Id = NewId();
            c.SectionId = l.SectionId is { } sid && sectionMap.TryGetValue(sid, out var mapped) ? mapped : null;
            return c;
        }).ToList();
        return copy;
    }

    // ───────────────────────── цены из каталога ─────────────────────────

    /// <summary>Карточка товара → то, что нужно смете. Цена — как в каталоге (PriceValue), оптовая — «Прораб».</summary>
    public static EstimateCatalogInfo FromProduct(NurMarketKassa.Models.Pos.CatalogProductTileVm p) =>
        new(p.Id, p.Title, p.Unit ?? "", Money((decimal)p.PriceValue), Money((decimal)p.WholesalePrice), (decimal)p.PurchasePrice, (decimal)p.Quantity, p.IsService);

    /// <summary>Цена товара на уровне сметы: «прораб» — оптовая, если задана; иначе розница.</summary>
    public static decimal PriceFor(EstimateCatalogInfo info, EstimateLevel level) =>
        level == EstimateLevel.Foreman && info.Wholesale > 0m ? info.Wholesale : info.Retail;

    /// <summary>Заполнить строку из товара (цена по уровню сметы, закупка, остаток, единица, название).</summary>
    public static void FillFromCatalog(EstimateLine line, EstimateCatalogInfo info, EstimateLevel level)
    {
        line.ProductId = info.Id;
        line.Name = info.Name;
        line.Unit = string.IsNullOrWhiteSpace(info.Unit) ? line.Unit : info.Unit;
        line.Price = PriceFor(info, level);
        line.Purchase = info.Purchase;
        line.StockAtTime = info.IsService ? null : info.Stock;
    }

    /// <summary>Одно изменение при пересчёте цен.</summary>
    public sealed record PriceChange(EstimateLine Line, decimal OldPrice, decimal NewPrice, bool Missing);

    /// <summary>«Пересчитать цены»: цены, закупка и остаток строк-товаров берутся из каталога (<paramref name="find"/>), дата цен обновляется. Товар, которого в каталоге уже нет,
    /// остаётся со старой ценой и попадает в результат с <c>Missing</c>. Своих позиций не касается. Возвращает только те строки, где цена изменилась или товар пропал.</summary>
    public static List<PriceChange> RefreshPrices(EstimateDoc doc, Func<string, EstimateCatalogInfo?> find, DateTime now)
    {
        var changes = new List<PriceChange>();
        foreach (var line in doc.Lines.Where(l => !l.IsCustom))
        {
            var info = find(line.ProductId!);
            if (info is null)
            {
                changes.Add(new PriceChange(line, line.Price, line.Price, true));
                continue;
            }
            var oldPrice = line.Price;
            line.Price = PriceFor(info, doc.Level);
            line.Purchase = info.Purchase;
            line.StockAtTime = info.IsService ? null : info.Stock;
            if (oldPrice != line.Price)
                changes.Add(new PriceChange(line, oldPrice, line.Price, false));
        }
        doc.PricesAt = now;
        doc.UpdatedAt = now;
        return changes;
    }

    /// <summary>Сменить уровень цены сметы и пересчитать цены строк-товаров. Каталога нет (товар пропал) — строка не меняется.</summary>
    public static void SetLevel(EstimateDoc doc, EstimateLevel level, Func<string, EstimateCatalogInfo?> find, DateTime now)
    {
        if (doc.Level == level)
            return;
        doc.Level = level;
        RefreshPrices(doc, find, now);
    }
}
