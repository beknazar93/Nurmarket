using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-11, ответ Умара (сервер NurCRM): пределы денежных полей. Единое место для кассы и программы владельца — окна и сервисы берут числа ОТСЮДА.
/// • цена продажи (price) и оптовая (wholesale_price) товара — до 9 999 999,999 сом, 3 знака после запятой (выше сервер отвечает ошибкой);
/// • цена строки в чеке (unit_price) — до 9 999 999 999,99;
/// • закупочная цена — до 9 999 999 999,9999, 4 знака.
/// Блокируем только выше пределов сервера. «Подозрительно большая цена» (больше <see cref="SuspiciousPrice"/>) — красное предупреждение, проводить можно.
/// </summary>
public static class ServerPriceLimits
{
    /// <summary>Цена продажи и оптовая цена товара, сом (3 знака после запятой).</summary>
    public const decimal SaleMax = 9_999_999.999m;

    /// <summary>Цена строки чека (unit_price), сом (2 знака).</summary>
    public const decimal LineUnitPriceMax = 9_999_999_999.99m;

    /// <summary>Закупочная цена, сом (4 знака после запятой).</summary>
    public const decimal PurchaseMax = 9_999_999_999.9999m;

    /// <summary>Знаков после запятой, которые хранит сервер.</summary>
    public const int SaleDecimals = 3;
    public const int PurchaseDecimals = 4;

    /// <summary>Цена больше этой — красная пометка «подозрительно большая» (штрихкод в ячейке, лишние нули), но НЕ запрет.</summary>
    public const double SuspiciousPrice = 1_000_000;

    /// <summary>Число не помещается в предел (NaN, бесконечность и огромные значения — тоже «больше»). Ровно предел — помещается.</summary>
    private static bool Exceeds(double value, decimal max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e15)
            return true;
        return (decimal)value > max;
    }

    public static bool SaleExceeds(double value) => Exceeds(value, SaleMax);

    public static bool PurchaseExceeds(double value) => Exceeds(value, PurchaseMax);

    public static bool LineUnitPriceExceeds(double value) => Exceeds(value, LineUnitPriceMax);

    private static string Fmt(decimal value, int decimals)
    {
        // «9 999 999,999» — пробелы разрядов и запятая, как видит кассир (не зависит от языка интерфейса: числа в тексте одинаковые на всех языках).
        var nfi = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = ",", NumberGroupSizes = [3] };
        return value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), nfi);
    }

    public static string SaleMaxText => Fmt(SaleMax, SaleDecimals);

    public static string PurchaseMaxText => Fmt(PurchaseMax, PurchaseDecimals);

    /// <summary>«Цена не может быть больше 9 999 999,999 сом» — цена продажи / оптовая.</summary>
    public static string SaleTooBigText => Tr.T(
        $"Цена не может быть больше {SaleMaxText} сом",
        $"Баа {SaleMaxText} сомдон ашпашы керек",
        $"The price cannot be more than {SaleMaxText} som",
        $"Fiyat {SaleMaxText} somdan fazla olamaz",
        $"Narx {SaleMaxText} so'mdan oshmasligi kerak");

    /// <summary>То же для цены закупки.</summary>
    public static string PurchaseTooBigText => Tr.T(
        $"Закупочная цена не может быть больше {PurchaseMaxText} сом",
        $"Сатып алуу баасы {PurchaseMaxText} сомдон ашпашы керек",
        $"The purchase price cannot be more than {PurchaseMaxText} som",
        $"Alış fiyatı {PurchaseMaxText} somdan fazla olamaz",
        $"Xarid narxi {PurchaseMaxText} so'mdan oshmasligi kerak");

    /// <summary>Красное предупреждение (не запрет): цена больше <see cref="SuspiciousPrice"/>.</summary>
    public static string SuspiciousPriceText => Tr.T(
        "Подозрительно большая цена (больше 1 000 000) — проверьте: не штрихкод ли это?",
        "Баасы шектен тышкары чоң (1 000 000дон ашык) — текшериңиз: штрихкод эмеспи?",
        "Suspiciously big price (over 1,000,000) — check it: is it a barcode?",
        "Şüpheli derecede büyük fiyat (1.000.000'dan fazla) — kontrol edin: barkod olmasın?",
        "Shubhali darajada katta narx (1 000 000 dan ortiq) — tekshiring: shtrix-kod emasmi?");

    /// <summary>Первая проблема с ценами товара (продажа / опт / закупка) или null. Для проверок перед сохранением и отправкой.</summary>
    public static string? ProductPriceProblem(double? sale, double? wholesale, double? purchase)
    {
        if (sale is { } s && SaleExceeds(s))
            return SaleTooBigText;
        if (wholesale is { } w && SaleExceeds(w))
            return SaleTooBigText;
        if (purchase is { } p && PurchaseExceeds(p))
            return PurchaseTooBigText;
        return null;
    }
}
