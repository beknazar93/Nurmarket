namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (живой прогон 3, P3-51): тексты «Создать штрихкоды» / «Создать QR» в «Размерах и цветах», когда новых кодов нет.
/// Строка без размера и цвета кода не получает — это не «код уже есть», а «сначала введите размер».</summary>
public static class SizeCodeHints
{
    /// <summary>Что сказать, если новых кодов не создано. <paramref name="sizedRows"/> — строки, где введён размер или цвет.</summary>
    public static string NothingCreated(bool qr, int sizedRows)
    {
        if (sizedRows <= 0)
            return NeedSize();
        return qr
            ? Tr.T("Коды есть у всех размеров. Чтобы напечатать их QR-кодом, выберите «Формат кода: QR».", "Бардык өлчөмдөрдө код бар. QR менен басуу үчүн «Код форматы: QR» тандаңыз.",
                "All sizes already have codes. To print them as QR, choose “Code format: QR”.", "Tüm bedenlerin kodu var. QR olarak basmak için «Kod biçimi: QR» seçin.", "Barcha o'lchamlarda kod bor. QR sifatida chop etish uchun «Kod formati: QR»ni tanlang.")
            : Tr.T("Штрихкоды есть у всех размеров.", "Бардык өлчөмдөрдө штрихкод бар.", "All sizes already have barcodes.", "Tüm bedenlerin barkodu var.", "Barcha o'lchamlarda shtrix-kod bor.");
    }

    public static string NeedSize() => Tr.T("Сначала введите размер (или цвет) в строке — коду нужна строка с размером.",
        "Алгач сапка өлчөмдү (же түстү) жазыңыз — код үчүн өлчөмү бар сап керек.",
        "Enter a size (or color) in the row first — a code needs a row with a size.",
        "Önce satıra beden (veya renk) girin — kod için bedenli bir satır gerekir.",
        "Avval qatorga o'lcham (yoki rang) kiriting — kod uchun o'lchamli qator kerak.");

    /// <summary>Добавка к сообщению «создано N», если часть строк пропущена из-за пустого размера.</summary>
    public static string SkippedNote(int emptyRows) => emptyRows <= 0 ? "" : " " + Tr.T(
        $"Строк без размера пропущено: {emptyRows} — введите размер.",
        $"Өлчөмсүз саптар өткөрүлдү: {emptyRows} — өлчөмдү жазыңыз.",
        $"Rows without a size skipped: {emptyRows} — enter a size.",
        $"Bedensiz atlanan satır: {emptyRows} — beden girin.",
        $"O'lchamsiz qatorlar o'tkazib yuborildi: {emptyRows} — o'lcham kiriting.");
}
