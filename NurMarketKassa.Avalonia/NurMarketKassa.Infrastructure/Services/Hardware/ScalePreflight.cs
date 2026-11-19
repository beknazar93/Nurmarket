using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Что не так в строке списка перед выгрузкой на весы.</summary>
public enum ScalePreflightKind
{
    /// <summary>Нет PLU / номера ячейки — «Исправить»: присвоить свободный.</summary>
    NoPlu,
    /// <summary>Номер больше таблицы весов (у Rongta и TM-30F — 4000, у Штрих-ПРИНТ — размер таблицы весов) — «Исправить»: свободный номер.</summary>
    PluTooLong,
    /// <summary>«Код в ШК» не число — «Исправить»: свободный код.</summary>
    NoCode,
    /// <summary>Код длиннее поля штрихкода весов — «Исправить»: свободный код.</summary>
    CodeTooLong,
    /// <summary>Цена 0 — весы такой товар не примут (Paloma365 тоже пропускает) — без «Исправить», цена правится в карточке.</summary>
    PriceZero,
    /// <summary>Цена выше поля цены весов (ScalePriceLimits).</summary>
    PriceTooHigh,
    /// <summary>Отмечено товаров больше таблицы весов — «Исправить»: снять отметки с лишних.</summary>
    OverLimit,
    /// <summary>Сумма этикетки при 1 кг выше потолка типа штрихкода (Rongta тип 02 и др.; у типа 07 потолка нет) — «Исправить»: тип 07.</summary>
    AmountOverCeiling,
    /// <summary>Название с символами, которые весы не печатают (заменятся) — предупреждение.</summary>
    NameChars,
    /// <summary>Один номер PLU у двух товаров — «Исправить»: второму свободный номер.</summary>
    DuplicatePlu,
}

public enum ScalePreflightSeverity { Warning, Error }

/// <summary>Одно замечание. <see cref="ProductId"/> null — замечание ко всему списку.</summary>
public sealed record ScalePreflightIssue(ScalePreflightKind Kind, ScalePreflightSeverity Severity, string? ProductId, string Name, string Detail)
{
    /// <summary>У строки есть кнопка «Исправить» (сама правит или ведёт в нужное окно).</summary>
    public bool CanFix => Kind is ScalePreflightKind.NoPlu or ScalePreflightKind.PluTooLong or ScalePreflightKind.NoCode
        or ScalePreflightKind.CodeTooLong or ScalePreflightKind.OverLimit or ScalePreflightKind.AmountOverCeiling or ScalePreflightKind.DuplicatePlu;

    /// <summary>Товар с таким замечанием не отправляется (автоотправка его пропускает; ручная — как раньше).</summary>
    public bool Blocks => Severity == ScalePreflightSeverity.Error && ProductId is not null;
}

public sealed record ScalePreflightRow(string Id, string Name, decimal PriceSom, int? Plu, long? Code, string? CodeText);

/// <param name="MaxPlu">Наибольший номер ячейки.</param>
/// <param name="MaxCode">Наибольший «Код в ШК».</param>
/// <param name="MaxPriceUnits">Поле цены весов (ScalePriceLimits), в единицах цены.</param>
/// <param name="PriceDecimals">Знаков после запятой в цене на весах.</param>
/// <param name="MaxProducts">Сколько товаров вмещают весы за раз; 0 — предел неизвестен.</param>
/// <param name="AmountCeilingSom">Потолок суммы этикетки (сом); 0 — потолка нет (тип по весу, у Rongta тип 07).</param>
/// <param name="NameHasUnprintable">Есть ли в названии символы, которые весы не печатают; null — не проверяем.</param>
public sealed record ScalePreflightContext(int MaxPlu, long MaxCode, long MaxPriceUnits, int PriceDecimals, int MaxProducts,
    decimal AmountCeilingSom = 0m, Func<string, bool>? NameHasUnprintable = null);

/// <summary>2026-11-13 (решение владельца по сравнению с Paloma365 и Rosta): проверка списка ПЕРЕД выгрузкой на весы одним перечнем — окно «Весы» →
/// «Проверка перед отправкой». Правила без окна и сети, чтобы их проверять тестами. Часть проверок отправка делает и сама (номера и коды в
/// <c>CheckSendBatch</c>, цены в <see cref="ScalePriceLimits"/>), но говорит о них только после нажатия «Отправить» и по одному — здесь всё сразу.</summary>
public static class ScalePreflight
{
    public static List<ScalePreflightIssue> Check(IReadOnlyList<ScalePreflightRow> rows, ScalePreflightContext ctx)
    {
        var issues = new List<ScalePreflightIssue>();
        foreach (var r in rows)
        {
            if (r.Plu is not > 0)
                issues.Add(new(ScalePreflightKind.NoPlu, ScalePreflightSeverity.Error, r.Id, r.Name, ""));
            else if (r.Plu > ctx.MaxPlu)
                issues.Add(new(ScalePreflightKind.PluTooLong, ScalePreflightSeverity.Error, r.Id, r.Name, $"{r.Plu} > {ctx.MaxPlu}"));

            if (r.Code is null && string.IsNullOrWhiteSpace(r.CodeText))
            {
                // пустой «Код в ШК» = номер ячейки (CheckSendBatch) — не замечание
            }
            else if (r.Code is null or <= 0)
                issues.Add(new(ScalePreflightKind.NoCode, ScalePreflightSeverity.Warning, r.Id, r.Name, r.CodeText ?? ""));
            else if (r.Code > ctx.MaxCode)
                issues.Add(new(ScalePreflightKind.CodeTooLong, ScalePreflightSeverity.Error, r.Id, r.Name, $"{r.Code} > {ctx.MaxCode}"));

            if (r.PriceSom <= 0)
                issues.Add(new(ScalePreflightKind.PriceZero, ScalePreflightSeverity.Error, r.Id, r.Name, ""));
            else if (!ScalePriceLimits.Fits(r.PriceSom, ctx.MaxPriceUnits, ctx.PriceDecimals))
                issues.Add(new(ScalePreflightKind.PriceTooHigh, ScalePreflightSeverity.Error, r.Id, r.Name, ScalePriceLimits.FormatMax(ctx.MaxPriceUnits, ctx.PriceDecimals)));
            else if (ctx.AmountCeilingSom > 0 && r.PriceSom > ctx.AmountCeilingSom)
                issues.Add(new(ScalePreflightKind.AmountOverCeiling, ScalePreflightSeverity.Warning, r.Id, r.Name, ctx.AmountCeilingSom.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))));

            if (ctx.NameHasUnprintable?.Invoke(r.Name) == true)
                issues.Add(new(ScalePreflightKind.NameChars, ScalePreflightSeverity.Warning, r.Id, r.Name, ""));
        }

        foreach (var g in rows.Where(r => r.Plu is > 0).GroupBy(r => r.Plu!.Value).Where(g => g.Count() > 1))
            foreach (var r in g)
                issues.Add(new(ScalePreflightKind.DuplicatePlu, ScalePreflightSeverity.Error, r.Id, r.Name, g.Key.ToString(CultureInfo.InvariantCulture)));

        if (ctx.MaxProducts > 0 && rows.Count > ctx.MaxProducts)
            issues.Add(new(ScalePreflightKind.OverLimit, ScalePreflightSeverity.Error, null, "", $"{rows.Count} > {ctx.MaxProducts}"));
        return issues;
    }

    /// <summary>Короткое название замечания (первая колонка списка).</summary>
    public static string Title(ScalePreflightKind kind) => kind switch
    {
        ScalePreflightKind.NoPlu => Tr.T("Нет PLU", "PLU жок", "No PLU", "PLU yok", "PLU yo‘q"),
        ScalePreflightKind.PluTooLong => Tr.T("PLU больше таблицы весов", "PLU тараза таблицасынан чоң", "PLU above the scale table", "PLU tartı tablosundan büyük", "PLU tarozi jadvalidan katta"),
        ScalePreflightKind.NoCode => Tr.T("«Код в ШК» не число", "«ШКдагы код» сан эмес", "“Barcode code” is not a number", "«Barkod kodu» sayı değil", "«ShKdagi kod» son emas"),
        ScalePreflightKind.CodeTooLong => Tr.T("Код длиннее поля штрихкода", "Код штрих-код талаасынан узун", "Code longer than the barcode field", "Kod barkod alanından uzun", "Kod shtrix-kod maydonidan uzun"),
        ScalePreflightKind.PriceZero => Tr.T("Цена 0", "Баасы 0", "Price 0", "Fiyat 0", "Narxi 0"),
        ScalePreflightKind.PriceTooHigh => Tr.T("Цена выше предела весов", "Баа таразанын чегинен жогору", "Price above the scale limit", "Fiyat tartı sınırının üzerinde", "Narx tarozi chegarasidan yuqori"),
        ScalePreflightKind.OverLimit => Tr.T("Товаров больше, чем вмещают весы", "Товарлар тараза батырганынан көп", "More products than the scale holds", "Tartının alabileceğinden fazla ürün", "Tarozi sig‘diradiganidan ko‘p tovar"),
        ScalePreflightKind.AmountOverCeiling => Tr.T("Сумма выше потолка штрихкода", "Сумма штрих-код чегинен жогору", "Amount above the barcode ceiling", "Tutar barkod sınırının üzerinde", "Summa shtrix-kod chegarasidan yuqori"),
        ScalePreflightKind.NameChars => Tr.T("Символы в названии", "Аталыштагы белгилер", "Characters in the name", "Addaki karakterler", "Nomdagi belgilar"),
        ScalePreflightKind.DuplicatePlu => Tr.T("Один PLU у нескольких товаров", "Бир PLU бир нече товарда", "One PLU on several products", "Birden fazla üründe aynı PLU", "Bir PLU bir nechta tovarda"),
        _ => kind.ToString(),
    };

    /// <summary>Пояснение: что произойдёт и что делать (вторая колонка списка).</summary>
    public static string Describe(ScalePreflightIssue issue)
    {
        var d = issue.Detail;
        return issue.Kind switch
        {
            ScalePreflightKind.NoPlu => Tr.T("У товара нет номера ячейки — на весы он не уйдёт. «Исправить» выдаст свободный номер.",
                "Товардын уяча номери жок — таразага кетпейт. «Оңдоо» бош номер берет.",
                "The product has no slot number — it will not go to the scale. “Fix” gives a free number.",
                "Ürünün hücre numarası yok — tartıya gitmez. «Düzelt» boş bir numara verir.",
                "Tovarning katak raqami yo‘q — taroziga ketmaydi. «Tuzatish» bo‘sh raqam beradi."),
            ScalePreflightKind.PluTooLong => Tr.T($"Номер {d} не помещается в таблицу весов. «Исправить» выдаст свободный номер.",
                $"{d} номери тараза таблицасына батпайт. «Оңдоо» бош номер берет.",
                $"Number {d} does not fit the scale table. “Fix” gives a free number.",
                $"{d} numarası tartı tablosuna sığmıyor. «Düzelt» boş bir numara verir.",
                $"{d} raqami tarozi jadvaliga sig‘maydi. «Tuzatish» bo‘sh raqam beradi."),
            ScalePreflightKind.NoCode => Tr.T($"«Код в ШК» «{d}» не число — весы напечатают номер ячейки. «Исправить» выдаст свободный код.",
                $"«ШКдагы код» «{d}» сан эмес — тараза уяча номерин басат. «Оңдоо» бош код берет.",
                $"“Barcode code” “{d}” is not a number — the scale prints the slot number. “Fix” gives a free code.",
                $"«Barkod kodu» «{d}» sayı değil — tartı hücre numarasını basar. «Düzelt» boş bir kod verir.",
                $"«ShKdagi kod» «{d}» son emas — tarozi katak raqamini chop etadi. «Tuzatish» bo‘sh kod beradi."),
            ScalePreflightKind.CodeTooLong => Tr.T($"Код {d} длиннее поля штрихкода весов. «Исправить» выдаст свободный код.",
                $"Код {d} тараза штрих-код талаасынан узун. «Оңдоо» бош код берет.",
                $"Code {d} is longer than the scale barcode field. “Fix” gives a free code.",
                $"Kod {d} tartı barkod alanından uzun. «Düzelt» boş bir kod verir.",
                $"Kod {d} tarozi shtrix-kod maydonidan uzun. «Tuzatish» bo‘sh kod beradi."),
            ScalePreflightKind.PriceZero => Tr.T("Цена 0 — товар на весы не отправляется. Укажите цену в карточке товара.",
                "Баасы 0 — товар таразага жөнөтүлбөйт. Товар карточкасында баа көрсөтүңүз.",
                "The price is 0 — the product is not sent to the scale. Set the price in the product card.",
                "Fiyat 0 — ürün tartıya gönderilmez. Ürün kartında fiyat girin.",
                "Narxi 0 — tovar taroziga yuborilmaydi. Tovar kartochkasida narxni kiriting."),
            ScalePreflightKind.PriceTooHigh => Tr.T($"Цена выше поля цены весов (максимум {d} сом). Уменьшите число знаков цены на весах — предел вырастет в 10 раз за каждый знак.",
                $"Баа таразанын баа талаасынан жогору (эң көп {d} сом). Таразада баанын белгилерин азайтыңыз — ар бир белги чекти 10 эсе өстүрөт.",
                $"The price is above the scale price field (maximum {d} som). Reduce the price decimals on the scale — each one raises the limit 10 times.",
                $"Fiyat tartının fiyat alanının üzerinde (en fazla {d} som). Tartıda fiyat hanelerini azaltın — her hane sınırı 10 kat artırır.",
                $"Narx tarozining narx maydonidan yuqori (eng ko‘pi {d} so‘m). Tarozida narx xonalarini kamaytiring — har bir xona chegarani 10 baravar oshiradi."),
            ScalePreflightKind.OverLimit => Tr.T($"Отмечено {d}: больше, чем вмещают весы. «Исправить» снимет отметки с лишних.",
                $"Белгиленген {d}: тараза батырганынан көп. «Оңдоо» ашыкчаларынан белгини алат.",
                $"Ticked {d}: more than the scale holds. “Fix” unticks the extra ones.",
                $"İşaretli {d}: tartının alabileceğinden fazla. «Düzelt» fazlalıkların işaretini kaldırır.",
                $"Belgilangan {d}: tarozi sig‘diradiganidan ko‘p. «Tuzatish» ortiqchalarining belgisini oladi."),
            ScalePreflightKind.AmountOverCeiling => Tr.T($"Уже 1 кг стоит больше {d} сом — выше потолка суммы в штрихкоде: весы напечатают этикетку БЕЗ штрихкода. «Исправить» выберет тип 07 (по весу, без потолка).",
                $"1 кг эле {d} сомдон кымбат — штрих-кодтогу сумма чегинен жогору: тараза этикетканы штрих-коддуз басат. «Оңдоо» 07 түрүн тандайт (салмак боюнча, чексиз).",
                $"1 kg already costs more than {d} som — above the amount ceiling in the barcode: the scale prints the label WITHOUT a barcode. “Fix” selects type 07 (by weight, no ceiling).",
                $"1 kg zaten {d} som'dan pahalı — barkoddaki tutar sınırının üzerinde: tartı etiketi barkodsuz basar. «Düzelt» 07 türünü seçer (ağırlığa göre, sınırsız).",
                $"1 kg allaqachon {d} so‘mdan qimmat — shtrix-koddagi summa chegarasidan yuqori: tarozi yorliqni shtrix-kodsiz chop etadi. «Tuzatish» 07 turini tanlaydi (vazn bo‘yicha, chegarasiz)."),
            ScalePreflightKind.NameChars => Tr.T("В названии есть символы, которых весы не печатают (например ң, ө, ү) — они заменятся похожими. Название на сайте можно сократить или написать иначе.",
                "Аталышта тараза басбаган белгилер бар (мисалы ң, ө, ү) — алар окшош белгилерге алмашат. Сайттагы аталышты кыскартсаңыз же башкача жазсаңыз болот.",
                "The name has characters the scale does not print (for example ң, ө, ү) — they are replaced by similar ones. You can shorten or rewrite the name on the website.",
                "Adda tartının basmadığı karakterler var (örneğin ң, ө, ү) — benzerleriyle değiştirilir. Sitedeki adı kısaltabilir veya başka yazabilirsiniz.",
                "Nomda tarozi chop etmaydigan belgilar bor (masalan ң, ө, ү) — ular o‘xshashlari bilan almashtiriladi. Saytdagi nomni qisqartirish yoki boshqacha yozish mumkin."),
            ScalePreflightKind.DuplicatePlu => Tr.T($"PLU {d} стоит у нескольких товаров. «Исправить» выдаст свободные номера всем, кроме первого.",
                $"PLU {d} бир нече товарда турат. «Оңдоо» биринчисинен башкаларына бош номер берет.",
                $"PLU {d} is on several products. “Fix” gives free numbers to all but the first.",
                $"PLU {d} birden fazla üründe. «Düzelt» ilki dışındakilere boş numara verir.",
                $"PLU {d} bir nechta tovarda. «Tuzatish» birinchisidan boshqalarga bo‘sh raqam beradi."),
            _ => d,
        };
    }

    /// <summary>Итог одной строкой для окна «Весы» и журнала.</summary>
    public static string Summary(IReadOnlyCollection<ScalePreflightIssue> issues)
    {
        if (issues.Count == 0)
            return Tr.T("Проверка: замечаний нет — можно отправлять.", "Текшерүү: эскертүү жок — жөнөтсө болот.", "Check: no issues — ready to send.", "Kontrol: sorun yok — gönderilebilir.", "Tekshiruv: izoh yo‘q — yuborish mumkin.");
        var errors = issues.Count(i => i.Severity == ScalePreflightSeverity.Error);
        var warnings = issues.Count - errors;
        return Tr.T($"Проверка: ошибок {errors}, предупреждений {warnings}.", $"Текшерүү: каталар {errors}, эскертүүлөр {warnings}.", $"Check: errors {errors}, warnings {warnings}.", $"Kontrol: hata {errors}, uyarı {warnings}.", $"Tekshiruv: xatolar {errors}, ogohlantirishlar {warnings}.");
    }
}

/// <summary>2026-11-13: печатают ли весы символы названия. Все марки пишут название в Windows-1251 (Rongta — по парам байт, но тоже из неё), поэтому буквы вне
/// 1251 (кыргызские ң, ө, ү, эмодзи и т. п.) заменяются вопросом или похожей буквой. Пробелы и цифры не в счёт.</summary>
public static class ScaleNameChars
{
    private static readonly Encoding Cp1251 = Resolve();

    private static Encoding Resolve()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch (Exception) { /* уже зарегистрирован */ }
        return Encoding.GetEncoding(1251, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    }

    public static bool HasUnprintable(string? name)
    {
        foreach (var c in name ?? "")
        {
            if (char.IsWhiteSpace(c) || c == '?')
                continue;
            var bytes = Cp1251.GetBytes(c.ToString());
            if (bytes.Length == 1 && bytes[0] == (byte)'?')
                return true;
        }
        return false;
    }
}
