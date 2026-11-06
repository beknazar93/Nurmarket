namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (весы «Штрих РС-200», ТЗ Умара): тексты про штрихкод весов на 5 языках — один источник для чека, сканера и Android.</summary>
public static class ScanMessages
{
    /// <summary>Весовая этикетка с PLU, которого нет в каталоге (вместо «Товар с кодом … не найден»).</summary>
    public static string WeightPluNotFound(string plu) => Tr.T(
        $"Весовой товар с PLU {plu} не найден. Проверьте PLU товара в NurCRM.",
        $"PLU {plu} болгон салмактуу товар табылган жок. NurCRM'де товардын PLU кодун текшериңиз.",
        $"Weighed product with PLU {plu} was not found. Check the product's PLU in NurCRM.",
        $"PLU {plu} olan tartılı ürün bulunamadı. Ürünün PLU kodunu NurCRM'de kontrol edin.",
        $"PLU {plu} bo'lgan tarozi mahsuloti topilmadi. Mahsulotning PLU kodini NurCRM'da tekshiring.");

    /// <summary>Этикетка весов прочитана с ошибкой (контрольная цифра EAN-13 не сходится) — отсканировать ещё раз, на сервер не спрашивать.</summary>
    public static string BadScan => Tr.T(
        "Штрихкод прочитан неверно, отсканируйте ещё раз.",
        "Штрихкод туура эмес окулду, кайра сканерлеңиз.",
        "The barcode was read incorrectly, please scan again.",
        "Barkod yanlış okundu, lütfen tekrar tarayın.",
        "Shtrix-kod noto'g'ri o'qildi, qayta skanerlang.");
}
