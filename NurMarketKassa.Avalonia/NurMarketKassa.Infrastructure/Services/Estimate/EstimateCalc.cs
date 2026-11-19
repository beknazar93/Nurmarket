using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>2026-11-19 («Смета», ТЗ 3): материал, который считает калькулятор по площади. Краска, грунт, шпаклёвка, штукатурка, клей и затирка берут расход из таблицы норм
/// (<see cref="HardwareCalcSettings"/> — её правит владелец в «Нормах расхода»); плитка, ламинат, гипсокартон — по размеру упаковки/листа; обои — по рулону и раппорту.</summary>
public enum EstimateMaterial
{
    Paint,
    Primer,
    Tile,
    Laminate,
    Wallpaper,
    Drywall,
    Putty,
    Plaster,
    Adhesive,
    Grout,
}

/// <summary>Откуда берётся площадь: пол/потолок (длина × ширина), стены (периметр × высота) или введена сразу.</summary>
public enum EstimateSurface
{
    Floor,
    Walls,
    Direct,
}

/// <summary>Исходные данные расчёта. Нули в <see cref="Layers"/>/<see cref="PerM2"/> — взять из таблицы норм.</summary>
public sealed record EstimateCalcInput
{
    public EstimateMaterial Material { get; init; }
    public EstimateSurface Surface { get; init; }
    public decimal Length { get; init; }
    public decimal Width { get; init; }
    public decimal Height { get; init; }
    /// <summary>Площадь, если <see cref="Surface"/> = Direct.</summary>
    public decimal Area { get; init; }
    /// <summary>Проёмы (двери, окна), м² — вычитаются из площади.</summary>
    public decimal Openings { get; init; }
    public decimal ReservePercent { get; init; }
    /// <summary>Размер упаковки: м² (плитка, ламинат, лист гипсокартона), л (краска, грунт), кг (смеси).</summary>
    public decimal PackSize { get; init; }
    public decimal Layers { get; init; }
    public decimal PerM2 { get; init; }
    public decimal RollLength { get; init; }
    public decimal RollWidth { get; init; }
    public decimal Rapport { get; init; }
}

/// <summary>Результат: «сырой» расчёт <see cref="ConsumptionCalculator"/>, площадь, метка упаковки и текст формулы с допущениями (показывается под результатом).</summary>
public sealed record EstimateCalcResult(EstimateCalcInput Input, CalcResult Raw, decimal Area, string PackWord, string Formula)
{
    public bool IsValid => Raw.IsValid;
}

/// <summary>
/// 2026-11-19 («Смета», ТЗ 3): калькуляторы расхода по площади для сметы. Формулы — только <see cref="ConsumptionCalculator"/> (одни и те же, что в карточке товара «Сколько нужно?»):
/// площадь − проёмы → × слои × норма → + запас → округление ВВЕРХ до целых упаковок. Здесь добавлены: площадь по размерам комнаты (пол, стены), гипсокартон (лист 2,5 × 1,2 м = 3 м²),
/// ламинат/линолеум (м² в упаковке), понятные тексты формулы на 5 языках и превращение результата в строку сметы (с привязкой к товару). Денег здесь нет — цену строки ставит смета.
/// </summary>
public static class EstimateCalc
{
    /// <summary>Лист гипсокартона 2,5 × 1,2 м, м².</summary>
    public const decimal DrywallSheetArea = 3.0m;

    public static string Key(EstimateMaterial m) => m.ToString().ToLowerInvariant();

    public static string Title(EstimateMaterial m) => m switch
    {
        EstimateMaterial.Paint => Tr.T("Краска", "Боёк", "Paint", "Boya", "Bo'yoq"),
        EstimateMaterial.Primer => Tr.T("Грунтовка", "Грунтовка", "Primer", "Astar", "Grunt"),
        EstimateMaterial.Tile => Tr.T("Плитка", "Плитка", "Tiles", "Fayans", "Plitka"),
        EstimateMaterial.Laminate => Tr.T("Ламинат / линолеум", "Ламинат / линолеум", "Laminate / linoleum", "Laminat / linolyum", "Laminat / linoleum"),
        EstimateMaterial.Wallpaper => Tr.T("Обои", "Обои", "Wallpaper", "Duvar kağıdı", "Oboy"),
        EstimateMaterial.Drywall => Tr.T("Гипсокартон", "Гипсокартон", "Drywall", "Alçıpan", "Gipsokarton"),
        EstimateMaterial.Putty => Tr.T("Шпаклёвка", "Шпаклёвка", "Putty", "Macun", "Shpatlyovka"),
        EstimateMaterial.Plaster => Tr.T("Штукатурка", "Штукатурка", "Plaster", "Sıva", "Suvoq"),
        EstimateMaterial.Adhesive => Tr.T("Клей плиточный", "Плитка желими", "Tile adhesive", "Fayans yapıştırıcı", "Plitka yelimi"),
        _ => Tr.T("Затирка", "Затирка", "Grout", "Derz dolgu", "Zatirka"),
    };

    /// <summary>Ключ типовой нормы расхода для материала (paint, primer…); у плитки, ламината, обоев и гипсокартона нормы расхода нет — null.</summary>
    public static string? NormKey(EstimateMaterial m) => m switch
    {
        EstimateMaterial.Paint => "paint",
        EstimateMaterial.Primer => "primer",
        EstimateMaterial.Putty => "putty",
        EstimateMaterial.Plaster => "plaster",
        EstimateMaterial.Adhesive => "adhesive",
        EstimateMaterial.Grout => "grout",
        _ => null,
    };

    public static CalcNorm? NormFor(EstimateMaterial m) =>
        NormKey(m) is { } key ? HardwareCalcSettings.Current.Norms.FirstOrDefault(n => n.Key == key) ?? HardwareCalcSettings.CreateDefault().Norms.FirstOrDefault(n => n.Key == key) : null;

    /// <summary>Типовые значения полей для материала: поверхность, упаковка, запас, слои и расход (из таблицы норм). Пользователь меняет всё в окне.</summary>
    public static EstimateCalcInput Defaults(EstimateMaterial m)
    {
        var norm = NormFor(m);
        var reserve = m == EstimateMaterial.Wallpaper ? 0m : HardwareCalcSettings.Current.ReservePercent; // у обоев запас уже в целых рулонах (ConsumptionCalculator.Wallpaper)
        return new EstimateCalcInput
        {
            Material = m,
            Surface = m is EstimateMaterial.Tile or EstimateMaterial.Laminate or EstimateMaterial.Adhesive or EstimateMaterial.Grout ? EstimateSurface.Floor : EstimateSurface.Walls,
            ReservePercent = reserve,
            Layers = norm?.Layers ?? 0m,
            PerM2 = norm?.PerM2 ?? 0m,
            Height = 2.7m,
            PackSize = m switch
            {
                EstimateMaterial.Paint => 10m,
                EstimateMaterial.Primer => 10m,
                EstimateMaterial.Tile => 1.44m,
                EstimateMaterial.Laminate => 2.2m,
                EstimateMaterial.Drywall => DrywallSheetArea,
                EstimateMaterial.Putty or EstimateMaterial.Plaster or EstimateMaterial.Adhesive => 25m,
                EstimateMaterial.Grout => 5m,
                _ => 0m,
            },
            RollLength = m == EstimateMaterial.Wallpaper ? 10.05m : 0m,
            RollWidth = m == EstimateMaterial.Wallpaper ? 0.53m : 0m,
        };
    }

    /// <summary>Площадь поверхности, м²: пол — длина × ширина, стены — периметр × высота, иначе введённая.</summary>
    public static decimal SurfaceArea(EstimateCalcInput i) => i.Surface switch
    {
        EstimateSurface.Floor => ConsumptionCalculator.Area(i.Length, i.Width),
        EstimateSurface.Walls => ConsumptionCalculator.Perimeter(i.Length, i.Width) * i.Height,
        _ => i.Area,
    };

    public static EstimateCalcResult Run(EstimateCalcInput i)
    {
        var area = SurfaceArea(i);
        var m = i.Material;
        var norm = NormFor(m);
        var layers = i.Layers > 0m ? i.Layers : norm?.Layers ?? 0m;
        var perM2 = i.PerM2 > 0m ? i.PerM2 : norm?.PerM2 ?? 0m;
        CalcResult raw;
        switch (m)
        {
            case EstimateMaterial.Paint:
            case EstimateMaterial.Primer:
                raw = ConsumptionCalculator.Paint(area, i.Openings, layers, perM2, i.ReservePercent, i.PackSize);
                break;
            case EstimateMaterial.Putty:
            case EstimateMaterial.Plaster:
            case EstimateMaterial.Adhesive:
            case EstimateMaterial.Grout:
                raw = ConsumptionCalculator.Mix(area, i.Openings, layers, perM2, i.ReservePercent, i.PackSize);
                break;
            case EstimateMaterial.Wallpaper:
                // Обои считаются по периметру комнаты и высоте стен (проёмы не вычитаются — см. ConsumptionCalculator.Wallpaper).
                raw = ConsumptionCalculator.Wallpaper(ConsumptionCalculator.Perimeter(i.Length, i.Width), i.Height, i.RollLength, i.RollWidth, i.Rapport, i.ReservePercent);
                area = ConsumptionCalculator.Perimeter(i.Length, i.Width) * i.Height;
                break;
            case EstimateMaterial.Laminate:
                raw = ConsumptionCalculator.Tiles(CalcKind.Laminate, area, i.Openings, i.ReservePercent, i.PackSize);
                break;
            default: // Tile, Drywall — лист/плитка считаются по площади упаковки
                raw = ConsumptionCalculator.Tiles(CalcKind.Tile, area, i.Openings, i.ReservePercent, i.PackSize);
                break;
        }
        var withNorm = i with { Layers = layers, PerM2 = perM2 };
        return new EstimateCalcResult(withNorm, raw, area, PackWord(m), raw.IsValid ? Explain(withNorm, raw, area) : ErrorText(raw.Error));
    }

    /// <summary>Единица строки сметы, когда товар не привязан: «уп.», «банок»…</summary>
    public static string PackWord(EstimateMaterial m) => m switch
    {
        EstimateMaterial.Paint or EstimateMaterial.Primer => Tr.T("банка", "банка", "can", "kutu", "banka"),
        EstimateMaterial.Wallpaper => Tr.T("рулон", "түрмөк", "roll", "rulo", "rulon"),
        EstimateMaterial.Drywall => Tr.T("лист", "барак", "sheet", "levha", "list"),
        EstimateMaterial.Putty or EstimateMaterial.Plaster or EstimateMaterial.Adhesive or EstimateMaterial.Grout => Tr.T("мешок", "кап", "bag", "torba", "qop"),
        _ => Tr.T("уп", "таңгак", "pack", "paket", "qadoq"),
    };

    public static string ErrorText(CalcError error) => error switch
    {
        CalcError.AreaMissing => Tr.T("Укажите размеры или площадь больше нуля.", "Өлчөмдөрдү же нөлдөн чоң аянтты көрсөтүңүз.", "Enter the dimensions or an area above zero.", "Ölçüleri veya sıfırdan büyük alanı girin.", "O'lchamlar yoki noldan katta maydonni kiriting."),
        CalcError.OpeningsTooBig => Tr.T("Проёмы не должны быть больше площади.", "Тешиктер аянттан чоң болбошу керек.", "Openings must be smaller than the area.", "Boşluklar alandan küçük olmalı.", "Teshiklar maydondan kichik bo'lishi kerak."),
        CalcError.PackMissing => Tr.T("Укажите размер упаковки.", "Таңгактын өлчөмүн көрсөтүңүз.", "Enter the pack size.", "Paket boyutunu girin.", "Qadoq o'lchamini kiriting."),
        CalcError.NormMissing => Tr.T("Нужны расход на м² и число слоёв (см. «Нормы расхода»).", "1 м² үчүн сарптоо жана катмарлардын саны керек («Сарптоо нормалары»).", "Rate per m² and number of coats are required (see “Consumption rates”).", "m² başına tüketim ve kat sayısı gerekir («Tüketim normları»).", "1 m² uchun sarf va qatlamlar soni kerak («Sarf normalari»)."),
        CalcError.ReserveInvalid => Tr.T("Запас — число от 0 до 100.", "Запас — 0дөн 100гө чейинки сан.", "The allowance is a number from 0 to 100.", "Fire payı 0 ile 100 arasında bir sayıdır.", "Zaxira — 0 dan 100 gacha son."),
        CalcError.RollInvalid => Tr.T("Проверьте рулон: длина, ширина, высота стены (полоса не длиннее рулона).", "Түрмөктү текшериңиз: узундук, туурасы, дубалдын бийиктиги (тилке түрмөктөн узун эмес).", "Check the roll: length, width, wall height (a strip is not longer than the roll).", "Rulo ölçülerini kontrol edin: uzunluk, en, duvar yüksekliği (şerit rulodan uzun olamaz).", "Rulonni tekshiring: uzunligi, eni, devor balandligi (lenta rulondan uzun bo'lmasligi kerak)."),
        CalcError.TooBig => Tr.T("Слишком большое значение — проверьте ввод.", "Маани өтө чоң — киргизүүнү текшериңиз.", "The value is too large — check the input.", "Değer çok büyük — girişi kontrol edin.", "Qiymat juda katta — kiritishni tekshiring."),
        _ => "",
    };

    private static string Q(decimal v) => MoneyText.Weight(v, "0.##");

    private static string UnitOf(EstimateMaterial m) => m is EstimateMaterial.Paint or EstimateMaterial.Primer
        ? Tr.T("л", "л", "l", "l", "l")
        : m is EstimateMaterial.Putty or EstimateMaterial.Plaster or EstimateMaterial.Adhesive or EstimateMaterial.Grout
            ? Tr.T("кг", "кг", "kg", "kg", "kg")
            : "м²";

    /// <summary>Формула и допущения на языке интерфейса: из чего получено количество (показывается под результатом и печатается рядом со строкой сметы).</summary>
    public static string Explain(EstimateCalcInput i, CalcResult r, decimal area)
    {
        var m = i.Material;
        if (m == EstimateMaterial.Wallpaper)
            return Tr.T(
                $"Периметр {Q(ConsumptionCalculator.Perimeter(i.Length, i.Width))} м, высота {Q(i.Height)} м; рулон {Q(i.RollLength)} × {Q(i.RollWidth)} м, раппорт {Q(i.Rapport)} м: полос из рулона {r.StripsPerRoll}, полос по комнате {r.Strips} (с запасом {Q(i.ReservePercent)} %) → {r.Packs} рул. Проёмы не вычитаются.",
                $"Периметр {Q(ConsumptionCalculator.Perimeter(i.Length, i.Width))} м, бийиктиги {Q(i.Height)} м; түрмөк {Q(i.RollLength)} × {Q(i.RollWidth)} м, раппорт {Q(i.Rapport)} м: түрмөктөн тилке {r.StripsPerRoll}, бөлмөгө тилке {r.Strips} (запас {Q(i.ReservePercent)} % менен) → {r.Packs} түрмөк. Тешиктер кемитилбейт.",
                $"Perimeter {Q(ConsumptionCalculator.Perimeter(i.Length, i.Width))} m, height {Q(i.Height)} m; roll {Q(i.RollLength)} × {Q(i.RollWidth)} m, repeat {Q(i.Rapport)} m: strips per roll {r.StripsPerRoll}, strips for the room {r.Strips} (with {Q(i.ReservePercent)} % allowance) → {r.Packs} rolls. Openings are not subtracted.",
                $"Çevre {Q(ConsumptionCalculator.Perimeter(i.Length, i.Width))} m, yükseklik {Q(i.Height)} m; rulo {Q(i.RollLength)} × {Q(i.RollWidth)} m, rapor {Q(i.Rapport)} m: rulodan şerit {r.StripsPerRoll}, oda için şerit {r.Strips} (%{Q(i.ReservePercent)} fire ile) → {r.Packs} rulo. Boşluklar düşülmez.",
                $"Perimetr {Q(ConsumptionCalculator.Perimeter(i.Length, i.Width))} m, balandlik {Q(i.Height)} m; rulon {Q(i.RollLength)} × {Q(i.RollWidth)} m, raport {Q(i.Rapport)} m: rulondan lenta {r.StripsPerRoll}, xona uchun lenta {r.Strips} ({Q(i.ReservePercent)} % zaxira bilan) → {r.Packs} rulon. Teshiklar ayirilmaydi.");

        var u = UnitOf(m);
        var net = area - i.Openings;
        var areaPart = Tr.T(
            $"Площадь {Q(area)} м²" + (i.Openings > 0m ? $" − проёмы {Q(i.Openings)} м² = {Q(net)} м²" : ""),
            $"Аянт {Q(area)} м²" + (i.Openings > 0m ? $" − тешиктер {Q(i.Openings)} м² = {Q(net)} м²" : ""),
            $"Area {Q(area)} m²" + (i.Openings > 0m ? $" − openings {Q(i.Openings)} m² = {Q(net)} m²" : ""),
            $"Alan {Q(area)} m²" + (i.Openings > 0m ? $" − boşluklar {Q(i.Openings)} m² = {Q(net)} m²" : ""),
            $"Maydon {Q(area)} m²" + (i.Openings > 0m ? $" − teshiklar {Q(i.Openings)} m² = {Q(net)} m²" : ""));
        var normPart = m is EstimateMaterial.Tile or EstimateMaterial.Laminate or EstimateMaterial.Drywall
            ? ""
            : Tr.T($" × {Q(i.Layers)} сл. × {Q(i.PerM2)} {u}/м² = {Q(r.Need)} {u}", $" × {Q(i.Layers)} кат. × {Q(i.PerM2)} {u}/м² = {Q(r.Need)} {u}", $" × {Q(i.Layers)} coats × {Q(i.PerM2)} {u}/m² = {Q(r.Need)} {u}", $" × {Q(i.Layers)} kat × {Q(i.PerM2)} {u}/m² = {Q(r.Need)} {u}", $" × {Q(i.Layers)} qatlam × {Q(i.PerM2)} {u}/m² = {Q(r.Need)} {u}");
        return areaPart + normPart + Tr.T(
            $"; запас {Q(i.ReservePercent)} % → {Q(r.WithReserve)} {u}; упаковка {Q(r.PackSize)} {u} → {r.Packs} шт. (всего {Q(r.PackedTotal)} {u}). Округление вверх до целых упаковок.",
            $"; запас {Q(i.ReservePercent)} % → {Q(r.WithReserve)} {u}; таңгак {Q(r.PackSize)} {u} → {r.Packs} даана (бардыгы {Q(r.PackedTotal)} {u}). Бүтүн таңгакка чейин өйдө тегеректөө.",
            $"; allowance {Q(i.ReservePercent)} % → {Q(r.WithReserve)} {u}; pack {Q(r.PackSize)} {u} → {r.Packs} pcs (total {Q(r.PackedTotal)} {u}). Rounded up to whole packs.",
            $"; fire payı %{Q(i.ReservePercent)} → {Q(r.WithReserve)} {u}; paket {Q(r.PackSize)} {u} → {r.Packs} adet (toplam {Q(r.PackedTotal)} {u}). Tam pakete yukarı yuvarlanır.",
            $"; zaxira {Q(i.ReservePercent)} % → {Q(r.WithReserve)} {u}; qadoq {Q(r.PackSize)} {u} → {r.Packs} dona (jami {Q(r.PackedTotal)} {u}). Butun qadoqqa yuqoriga yaxlitlanadi.");
    }

    /// <summary>Превращает результат в строку сметы. С товаром (<paramref name="info"/>): количество — упаковки или единицы товара (<see cref="CalcCartPlanner"/>), цена по уровню сметы;
    /// единица товара не подходит (плитка в «м» и т. п.) — null. Без товара: своя позиция «название — N упаковок», цену вписывают потом. Запас уже в количестве — у строки он 0.</summary>
    public static EstimateLine? ToLine(EstimateCalcResult result, EstimateCatalogInfo? info, EstimateLevel level)
    {
        if (!result.IsValid)
            return null;
        var line = new EstimateLine { ReservePercent = 0m, CalcNote = result.Formula };
        if (info is null)
        {
            line.Name = Title(result.Input.Material);
            line.Unit = result.PackWord;
            line.Qty = result.Raw.Packs;
            return line;
        }
        var price = EstimateRules.PriceFor(info, level);
        var plan = CalcCartPlanner.Plan(result.Raw, info.Unit, price);
        if (!plan.CanAdd)
            return null;
        EstimateRules.FillFromCatalog(line, info, level);
        line.Qty = plan.Quantity;
        return line;
    }
}
