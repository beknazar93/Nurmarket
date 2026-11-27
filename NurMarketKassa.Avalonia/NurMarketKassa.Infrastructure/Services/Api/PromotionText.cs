using System.Globalization;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-27 (раздел «Акции» программы владельца): акция понятными словами на 5 языках — вид («Купи 2 — получи 1», «−15 %»…), период, дни и часы, статус
/// и живой пример расчёта для мастера («Вода 100 сом × 3 = 300 → 200, третья бесплатно»). Без окон — проверяется unit-тестами.</summary>
public static class PromotionText
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Som => T("сом", "сом", "som", "som", "so'm");

    /// <summary>Число без лишних нулей: 100 → «100», 79.5 → «79,5», 1234.56 → «1 234,56».</summary>
    public static string Num(decimal v) => v == decimal.Truncate(v) ? v.ToString("#,0", Ru) : v.ToString("#,0.##", Ru);

    public static string Money(decimal v) => Num(v) + " " + Som;

    public static string KindTitle(string kind) => kind switch
    {
        Promotion.KindBuyNGetM => T("Купи N — получи M", "N сатып ал — M ал", "Buy N, get M", "N al, M kazan", "N ta ol — M ta ol"),
        Promotion.KindPercent => T("Скидка в процентах", "Пайыздык арзандатуу", "Percent discount", "Yüzde indirim", "Foizli chegirma"),
        Promotion.KindAmount => T("Скидка суммой", "Сумма менен арзандатуу", "Amount discount", "Tutar indirimi", "Summali chegirma"),
        Promotion.KindFixedPrice => T("Цена по акции", "Акциялык баа", "Promo price", "Kampanya fiyatı", "Aksiya narxi"),
        _ => kind,
    };

    /// <summary>Вид акции одной строкой для списка: «Купи 2 — получи 1», «−15 %», «−20 сом за штуку», «Цена по акции 79 сом».</summary>
    public static string KindLabel(Promotion p) => p.Kind switch
    {
        Promotion.KindBuyNGetM when p.GetDiscountPercent >= 100 => T($"Купи {p.Buy} — получи {p.Get}", $"{p.Buy} сатып ал — {p.Get} ал", $"Buy {p.Buy}, get {p.Get}",
            $"{p.Buy} al, {p.Get} kazan", $"{p.Buy} ta ol — {p.Get} ta ol"),
        Promotion.KindBuyNGetM => T($"Купи {p.Buy} — ещё {p.Get} со скидкой {Num(p.GetDiscountPercent)} %", $"{p.Buy} сатып ал — дагы {p.Get} {Num(p.GetDiscountPercent)} % арзан",
            $"Buy {p.Buy}, {p.Get} more at {Num(p.GetDiscountPercent)} % off", $"{p.Buy} al, {p.Get} tane daha %{Num(p.GetDiscountPercent)} indirimli",
            $"{p.Buy} ta ol — yana {p.Get} ta {Num(p.GetDiscountPercent)} % chegirma bilan"),
        Promotion.KindPercent => "−" + Num(p.Percent) + " %"
            + (p.MinAmount is > 0 ? T($" от {Money(p.MinAmount.Value)} в строке", $" сапта {Money(p.MinAmount.Value)} баштап", $" from {Money(p.MinAmount.Value)} per line",
                $" satırda {Money(p.MinAmount.Value)} üzeri", $" qatorda {Money(p.MinAmount.Value)} dan") : "")
            + (p.MaxQty is > 0 ? T($", не больше {Num(p.MaxQty.Value)} шт.", $", {Num(p.MaxQty.Value)} даанадан ашык эмес", $", up to {Num(p.MaxQty.Value)} pcs",
                $", en fazla {Num(p.MaxQty.Value)} adet", $", ko'pi bilan {Num(p.MaxQty.Value)} dona") : ""),
        Promotion.KindAmount when p.Per == "line" => "−" + Money(p.Amount) + T(" на строку", " сапка", " per line", " satır başına", " qatorga"),
        Promotion.KindAmount => "−" + Money(p.Amount) + T(" за штуку", " даанасына", " per item", " adet başına", " donasiga"),
        Promotion.KindFixedPrice => T("Цена по акции ", "Акциялык баа ", "Promo price ", "Kampanya fiyatı ", "Aksiya narxi ") + Money(p.Price),
        _ => p.Kind,
    };

    public static string StatusLabel(Promotion p, DateTimeOffset now) => p.Status switch
    {
        Promotion.StatusDraft => T("Черновик", "Каралама", "Draft", "Taslak", "Qoralama"),
        Promotion.StatusPaused => T("На паузе", "Тыныгууда", "Paused", "Duraklatıldı", "To'xtatilgan"),
        Promotion.StatusFinished => T("Завершена", "Аяктады", "Finished", "Bitti", "Yakunlangan"),
        Promotion.StatusActive when p.IsScheduled(now) => T("Запланирована", "Пландалган", "Scheduled", "Planlandı", "Rejalashtirilgan"),
        Promotion.StatusActive when p.EndsAt is { } e && e <= now => T("Срок истёк", "Мөөнөтү бүттү", "Expired", "Süresi doldu", "Muddati tugagan"),
        Promotion.StatusActive => T("Действует", "Иштеп жатат", "Active", "Aktif", "Amalda"),
        _ => p.Status,
    };

    /// <summary>Период: «с 01.12.2026 по 31.12.2026», «с 01.12.2026, бессрочно», «бессрочно».</summary>
    public static string Period(Promotion p)
    {
        string D(DateTimeOffset d) => PromotionsApi.Local(d).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        if (p.StartsAt is { } s && p.EndsAt is { } e)
            return T($"с {D(s)} по {D(e)}", $"{D(s)} – {D(e)}", $"{D(s)} – {D(e)}", $"{D(s)} – {D(e)}", $"{D(s)} – {D(e)}");
        if (p.StartsAt is { } s2)
            return T($"с {D(s2)}, бессрочно", $"{D(s2)} баштап, мөөнөтсүз", $"from {D(s2)}, no end date", $"{D(s2)} itibaren, süresiz", $"{D(s2)} dan, muddatsiz");
        if (p.EndsAt is { } e2)
            return T($"по {D(e2)}", $"{D(e2)} чейин", $"until {D(e2)}", $"{D(e2)} tarihine kadar", $"{D(e2)} gacha");
        return T("бессрочно", "мөөнөтсүз", "no end date", "süresiz", "muddatsiz");
    }

    public static string DayShort(int day) => day switch
    {
        1 => T("Пн", "Дш", "Mon", "Pzt", "Du"),
        2 => T("Вт", "Шш", "Tue", "Sal", "Se"),
        3 => T("Ср", "Шр", "Wed", "Çar", "Cho"),
        4 => T("Чт", "Бш", "Thu", "Per", "Pa"),
        5 => T("Пт", "Жм", "Fri", "Cum", "Ju"),
        6 => T("Сб", "Иш", "Sat", "Cmt", "Sha"),
        7 => T("Вс", "Жк", "Sun", "Paz", "Ya"),
        _ => "",
    };

    /// <summary>Дни и часы: «Пн, Пт · 10:00–14:00», «каждый день», «22:00–02:00 (через полночь)».</summary>
    public static string DaysHours(Promotion p)
    {
        var days = p.Weekdays.Count is 0 or 7
            ? T("каждый день", "күн сайын", "every day", "her gün", "har kuni")
            : string.Join(", ", p.Weekdays.OrderBy(d => d).Select(DayShort));
        if (string.IsNullOrWhiteSpace(p.TimeFrom) && string.IsNullOrWhiteSpace(p.TimeTo))
            return days;
        var from = string.IsNullOrWhiteSpace(p.TimeFrom) ? "00:00" : p.TimeFrom!;
        var to = string.IsNullOrWhiteSpace(p.TimeTo) ? "24:00" : p.TimeTo!;
        var night = TimeSpan.TryParse(from, CultureInfo.InvariantCulture, out var f) && TimeSpan.TryParse(to, CultureInfo.InvariantCulture, out var t) && t < f
            ? T(" (через полночь)", " (түн ортосу аркылуу)", " (past midnight)", " (gece yarısını geçer)", " (yarim tun orqali)")
            : "";
        return days + " · " + from + "–" + to + night;
    }

    /// <summary>Сколько сработала: «Сработала: 12 чеков, скидка 1 500 сом» (null — отчёта нет).</summary>
    public static string Usage(PromotionReportRow? row)
    {
        if (row == null || row.Receipts == 0 && row.Discount == 0)
            return T("Ещё не срабатывала", "Азырынча иштеген жок", "Not used yet", "Henüz kullanılmadı", "Hali ishlamagan");
        return T($"Сработала: {row.Receipts} чек., скидка {Money(row.Discount)}", $"Иштеди: {row.Receipts} чек, арзандатуу {Money(row.Discount)}",
            $"Used: {row.Receipts} receipts, discount {Money(row.Discount)}", $"Kullanıldı: {row.Receipts} fiş, indirim {Money(row.Discount)}",
            $"Ishladi: {row.Receipts} chek, chegirma {Money(row.Discount)}");
    }

    /// <summary>Живой пример расчёта для шага «Вид»: вода по 100 сом. Число штук — блок N+M для «Купи N — получи M», иначе 3.</summary>
    public static string Example(Promotion p)
    {
        const decimal price = 100m;
        var water = T("Вода", "Суу", "Water", "Su", "Suv");
        switch (p.Kind)
        {
            case Promotion.KindBuyNGetM:
            {
                if (p.Buy < 1 || p.Get < 1)
                    return T("Укажите N и M — пример появится здесь.", "N жана M жазыңыз — мисал бул жерде чыгат.", "Enter N and M — the example appears here.", "N ve M girin — örnek burada görünür.", "N va M ni kiriting — misol shu yerda chiqadi.");
                var qty = p.Buy + p.Get;
                var pct = Math.Clamp(p.GetDiscountPercent, 0m, 100m);
                var sum = price * qty;
                var total = sum - Math.Round(price * p.Get * pct / 100m, 2, MidpointRounding.AwayFromZero);
                var tail = pct >= 100m
                    ? p.Get == 1
                        ? T($"{Ordinal(qty)} бесплатно", $"{qty}-си бекер", $"the {OrdinalEn(qty)} one free", $"{qty}. ücretsiz", $"{qty}-si bepul")
                        : T($"{p.Get} шт. бесплатно", $"{p.Get} даана бекер", $"{p.Get} free", $"{p.Get} adet ücretsiz", $"{p.Get} dona bepul")
                    : T($"{p.Get} шт. со скидкой {Num(pct)} %", $"{p.Get} даана {Num(pct)} % арзан", $"{p.Get} at {Num(pct)} % off", $"{p.Get} adet %{Num(pct)} indirimli", $"{p.Get} dona {Num(pct)} % chegirma bilan");
                return $"{water} {Money(price)} × {qty} = {Num(sum)} → {Num(total)}, {tail}";
            }
            case Promotion.KindPercent:
            {
                var pct = Math.Clamp(p.Percent, 0m, 100m);
                var sum = price * 3;
                var units = p.MaxQty is > 0 and < 3 ? p.MaxQty.Value : 3m;
                var disc = Math.Round(price * units * pct / 100m, 2, MidpointRounding.AwayFromZero);
                if (p.MinAmount is { } min && min > sum)
                    return $"{water} {Money(price)} × 3 = {Num(sum)} — " + T($"скидки нет: строка меньше {Money(min)}", $"арзандатуу жок: сап {Money(min)} кем", $"no discount: line below {Money(min)}",
                        $"indirim yok: satır {Money(min)} altında", $"chegirma yo'q: qator {Money(min)} dan kam");
                return $"{water} {Money(price)} × 3 = {Num(sum)} → {Num(sum - disc)} (−{Num(pct)} %)";
            }
            case Promotion.KindAmount:
            {
                var sum = price * 3;
                var disc = p.Per == "line" ? Math.Min(p.Amount, sum) : Math.Min(p.Amount, price) * 3;
                return $"{water} {Money(price)} × 3 = {Num(sum)} → {Num(sum - disc)} (−{Num(disc)})";
            }
            case Promotion.KindFixedPrice:
            {
                var sum = price * 3;
                var unit = Math.Min(p.Price, price);
                return $"{water} {Money(price)} → {Money(unit)}; × 3 = {Num(sum)} → {Num(unit * 3)}";
            }
        }
        return "";
    }

    private static string Ordinal(int n) => n switch
    {
        2 => "вторая", 3 => "третья", 4 => "четвёртая", 5 => "пятая", 6 => "шестая", 7 => "седьмая", 8 => "восьмая", 9 => "девятая", 10 => "десятая",
        _ => n + "-я",
    };

    private static string OrdinalEn(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => n + "th" };

    /// <summary>Подпись предупреждения preview/checkout понятными словами.</summary>
    public static string Warning(PromotionWarning w, string productName)
    {
        var who = productName.Length > 0 ? productName + ": " : "";
        return w.Code switch
        {
            "promotion_capped_by_cost" => who + T("скидка урезана до закупки", "арзандатуу сатып алуу баасына чейин кыскарды", "discount capped at cost", "indirim alış fiyatına indirildi", "chegirma xarid narxigacha qisqardi")
                + (w.PurchasePrice is { } pp ? T($" (закупка {Money(pp)}", $" (сатып алуу {Money(pp)}", $" (cost {Money(pp)}", $" (alış {Money(pp)}", $" (xarid {Money(pp)}")
                    + (w.Cut is { } cut ? T($", срезано {Money(cut)})", $", кыскарды {Money(cut)})", $", cut {Money(cut)})", $", kesilen {Money(cut)})", $", qisqardi {Money(cut)})") : ")") : "")
                + T(". Чтобы продавать ниже закупки, владелец может включить это на шаге «Условия».", ". Сатып алуудан төмөн сатуу үчүн ээси «Шарттар» кадамында күйгүзө алат.",
                    ". To sell below cost, the owner can enable it at step “Conditions”.", ". Alışın altında satmak için sahip bunu «Koşullar» adımında açabilir.",
                    ". Xariddan past sotish uchun egasi buni «Shartlar» qadamida yoqishi mumkin."),
            "discount_clamped" => who + T("скидка не может быть больше суммы строки — урезана", "арзандатуу сап суммасынан ашпайт — кыскарды", "discount can't exceed the line total — capped",
                "indirim satır tutarını aşamaz — kısıldı", "chegirma qator summasidan oshmaydi — qisqardi"),
            "promotion_limit_reached" => who + T("лимит акции исчерпан", "акциянын лимити бүттү", "promotion limit reached", "kampanya limiti doldu", "aksiya limiti tugadi"),
            _ => who + (w.Detail.Length > 0 ? w.Detail : w.Code),
        };
    }
}
