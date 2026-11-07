using System;
using System.Globalization;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-11-06, жалоба владельца «дальше 9 тысяч цена не отображается» (Rongta, Штрих-М): предел цены каждой марки
/// весов в одном месте. Цена на весах — целое число «единиц» (сом × 10^знаков), поле конечной ширины: у Штрих-ПРИНТ и у Dahua/Rongta
/// (команда «!0V», поле mprice) — 6 цифр, у Штрих РС-200 (БД товаров, поле 5) — 7 цифр, у Rongta RLS1000 (TCP, 0110) — 8 цифр.
/// При 2 знаках после запятой 6 цифр дают потолок 9999,99 сом. Молча обрезать нельзя (весы напечатали бы неверную цену) —
/// вызывающий проверяет <see cref="Fits"/> и показывает <see cref="DoesNotFitText"/>.</summary>
public static class ScalePriceLimits
{
    /// <summary>Штрих-ПРИНТ: «Цена в МДЕ, 0..999999» (протокол, команды 55h/57h; поле 3 байта, но допустимо по протоколу до 999999).</summary>
    public const long ShtrikhPrintMaxUnits = 999_999;

    /// <summary>Dahua / TM-30F / Rongta напрямую по сети: поле mprice — 6 цифр.</summary>
    public const long DahuaMaxUnits = 999_999;

    /// <summary>Штрих РС-200: поле 5 БД товаров — «0..9999999 или 0..9999.999», 7 знаков вместе с дробной частью.</summary>
    public const long ShtrikhPcMaxUnits = 9_999_999;

    /// <summary>Rongta RLS1000 по TCP (пакет 0110): «Unit price» — 8 цифр, сотые без точки.</summary>
    public const long RongtaRlsMaxUnits = 99_999_999;

    /// <summary>Штрих-ПРИНТ: «положение десятичной точки» весов отделяет 0 или 2 знака.</summary>
    public static int ShtrikhPrintDigits(int decimalPointDigits) => decimalPointDigits >= 2 ? 2 : 0;

    private static decimal Pow10(int decimals) => decimals switch { <= 0 => 1m, 1 => 10m, 2 => 100m, _ => 1000m };

    /// <summary>Самая большая цена в сомах, которая помещается в поле.</summary>
    public static decimal MaxPriceSom(long maxUnits, int decimals) => maxUnits / Pow10(decimals);

    /// <summary>Цена в единицах весов (округление как у протоколов — от нуля); отрицательная — 0.</summary>
    public static decimal Units(decimal priceSom, int decimals) =>
        Math.Round(Math.Max(0m, priceSom) * Pow10(decimals), 0, MidpointRounding.AwayFromZero);

    /// <summary>Помещается ли цена в поле весов без обрезки.</summary>
    public static bool Fits(decimal priceSom, long maxUnits, int decimals) => Units(priceSom, decimals) <= maxUnits;

    public static string FormatMax(long maxUnits, int decimals)
    {
        var max = MaxPriceSom(maxUnits, decimals);
        return max.ToString(decimals == 0 ? "N0" : "N" + Math.Min(decimals, 3).ToString(CultureInfo.InvariantCulture), CultureInfo.GetCultureInfo("ru-RU"));
    }

    /// <summary>Честное сообщение на 5 языках: цена выше предела весов, максимум и что делать.</summary>
    /// <param name="hint">Что менять на весах/в кассе (уже на 5 языках); пусто — без подсказки.</param>
    public static string DoesNotFitText(decimal priceSom, long maxUnits, int decimals, string hint = "")
    {
        var price = priceSom.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
        var max = FormatMax(maxUnits, decimals);
        var text = Tr.T(
            $"цена {price} сом выше предела весов (максимум {max} сом при {decimals} знаках после запятой) — товар не загружен",
            $"баа {price} сом таразанын чегинен жогору (жылдызчадан кийин {decimals} белгиде эң көп {max} сом) — товар жүктөлгөн жок",
            $"the price {price} som is above the scale limit (maximum {max} som with {decimals} decimals) — the product was not loaded",
            $"{price} som fiyatı tartı sınırının üzerinde (virgülden sonra {decimals} hanede en fazla {max} som) — ürün yüklenmedi",
            $"{price} so‘m narxi tarozi chegarasidan yuqori (verguldan keyin {decimals} xonada eng ko‘pi {max} so‘m) — tovar yuklanmadi");
        return string.IsNullOrEmpty(hint) ? text : text + ". " + hint;
    }

    /// <summary>Подсказка «уменьшите число знаков цены на весах» для марок, где это настройка весов.</summary>
    public static string FewerDecimalsHint(string settingNameRu, string settingNameKy, string settingNameEn, string settingNameTr, string settingNameUz) => Tr.T(
        $"Уменьшите на весах «{settingNameRu}» (с каждым знаком меньше предел растёт в 10 раз), затем выберите то же число знаков в кассе и отправьте снова",
        $"Таразада «{settingNameKy}» азайтыңыз (ар бир белгиге чек 10 эсе өсөт), анан кассада ошол санды тандап кайра жөнөтүңүз",
        $"On the scale reduce “{settingNameEn}” (each decimal less raises the limit 10 times), then pick the same number in the till and send again",
        $"Tartıda «{settingNameTr}» değerini azaltın (her hane sınırı 10 kat artırır), sonra kasada aynı sayıyı seçip tekrar gönderin",
        $"Tarozida «{settingNameUz}» ni kamaytiring (har bir xona chegarani 10 baravar oshiradi), keyin kassada shu sonni tanlab qayta yuboring");
}
