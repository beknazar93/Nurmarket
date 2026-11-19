using System.Globalization;

namespace NurMarketKassa.Services.Fiscal;

/// <summary>2026-11-14: разбор и показ полей формы «Налоги и ККМ» и поля «Ставка НДС» товара — отдельно от окна, чтобы проверить тестами (деньги и проценты — только decimal).</summary>
public static class FiscalFormRules
{
    /// <summary>Режим словами для кассира и владельца.</summary>
    public static string ModeText(FiscalMode mode) => mode switch
    {
        FiscalMode.Kassa => Tr.T("Чеки пробивает касса (ККМ eKassa)", "Чектерди касса бекитет (eKassa ККМ)", "The kassa fiscalizes receipts (eKassa)", "Fişleri kasa keser (eKassa)", "Cheklarni kassa uradi (eKassa)"),
        FiscalMode.Server => Tr.T("Чеки пробивает сервер NurCRM — касса не пробивает", "Чектерди NurCRM сервери бекитет — касса бекитпейт", "The NurCRM server fiscalizes — the kassa does not",
            "Fişleri NurCRM sunucusu keser — kasa kesmez", "Cheklarni NurCRM serveri uradi — kassa urmaydi"),
        _ => Tr.T("Выключено — чек «Товарный», нефискальный", "Өчүрүлгөн — «Товардык» чек, фискалдык эмес", "Off — a non-fiscal sales receipt", "Kapalı — mali olmayan satış fişi", "O'chirilgan — fiskal bo'lmagan tovar cheki"),
    };

    /// <summary>Ставки «0; 12» → список. Разделители — «;», пробел или перевод строки (запятая — десятичная: «12,5»). Пусто — пустой список. error — понятная причина (null — верно).</summary>
    public static (IReadOnlyList<decimal> Rates, string? Error) ParseRates(string? text)
    {
        var rates = new List<decimal>();
        foreach (var token in (text ?? "").Split(new[] { ';', ' ', '\n', '\r', '\t', '%' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParsePercent(token, out var value))
                return (Array.Empty<decimal>(), Tr.T($"Ставка «{token}» не число от 0 до 100.", $"«{token}» ставкасы 0дөн 100гө чейинки сан эмес.", $"The rate “{token}” is not a number from 0 to 100.",
                    $"«{token}» oranı 0 ile 100 arasında bir sayı değil.", $"«{token}» stavkasi 0 dan 100 gacha son emas."));
            if (!rates.Contains(value))
                rates.Add(value);
        }
        return (rates, null);
    }

    /// <summary>Одна ставка (НсП): пусто — null без ошибки.</summary>
    public static (decimal? Rate, string? Error) ParseOptionalPercent(string? text)
    {
        var t = (text ?? "").Trim().TrimEnd('%').Trim();
        if (t.Length == 0)
            return (null, null);
        return TryParsePercent(t, out var v)
            ? (v, null)
            : (null, Tr.T($"Ставка «{t}» не число от 0 до 100.", $"«{t}» ставкасы 0дөн 100гө чейинки сан эмес.", $"The rate “{t}” is not a number from 0 to 100.",
                $"«{t}» oranı 0 ile 100 arasında bir sayı değil.", $"«{t}» stavkasi 0 dan 100 gacha son emas."));
    }

    public static bool TryParsePercent(string? text, out decimal value)
    {
        value = 0m;
        var t = (text ?? "").Trim().TrimEnd('%').Trim().Replace(',', '.');
        return t.Length > 0 && decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value) && value is >= 0m and <= 100m;
    }

    /// <summary>Список ставок в поле: «0; 12».</summary>
    public static string FormatRates(IEnumerable<decimal> rates) =>
        string.Join("; ", rates.Select(FormatPercent));

    /// <summary>Процент без лишних нулей: 12.00 → «12», 2.50 → «2,5» (запятая — общий формат кассы).</summary>
    public static string FormatPercent(decimal value) => value.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>Ставка НДС товара из поля карточки: пусто — null («ставка компании»). Ошибка — не число или вне 0…100.</summary>
    public static (decimal? Rate, string? Error) ParseProductVat(string? text) => ParseOptionalPercent(text);

    /// <summary>Для JSON (vat_rate товара): строка «12.00» с двумя знаками, точка — как в ответах сервера.</summary>
    public static string ToApi(decimal rate) => rate.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>vat_rate карточки товара с сервера: число, строка «12.00» или null/пусто (= ставка компании).</summary>
    public static decimal? ReadProductVat(System.Text.Json.JsonElement card)
    {
        if (card.ValueKind != System.Text.Json.JsonValueKind.Object || !card.TryGetProperty("vat_rate", out var v))
            return null;
        return FiscalCapabilities.ReadDecimal(v);
    }

    /// <summary>В карточке товара: поле «Ставка НДС» показывается, только когда режим не «выключено» (сервер знает fiscal_kassa).</summary>
    public static bool ProductVatVisible => FiscalSettings.ShowsFiscalUi;
}
