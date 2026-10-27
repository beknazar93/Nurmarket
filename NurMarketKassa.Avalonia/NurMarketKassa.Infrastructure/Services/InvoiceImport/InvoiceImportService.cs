using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: вход загрузки электронной накладной. Файл читается один раз: размер (не больше 20 МБ) → тип по содержимому (XML / xlsx / CSV;
// PDF и фото — через ИИ Нур Советника, здесь отказ с подсказкой) → разбор. Исключений наружу не бросает. Журнал — префикс INVOICE_IMPORT, без
// содержимого накладной (только имя файла, формат, число строк).

public sealed class InvoiceLoadResult
{
    public InvoiceDocument? Document { get; init; }
    public string? Error { get; init; }

    /// <summary>PDF или фото: читает ИИ Нур Советника, а не этот разбор.</summary>
    public bool IsImageOrPdf { get; init; }

    /// <summary>Таблица Excel/CSV, из которой получена накладная (для ручного сопоставления столбцов); для XML null.</summary>
    public ProductFileTable? Table { get; init; }

    public int HeaderIndex { get; init; }
    public InvoiceColumnMapping? Mapping { get; init; }

    /// <summary>Строка заголовков не узнана уверенно — показать окно сопоставления столбцов.</summary>
    public bool NeedsMapping { get; init; }

    public string FileHash { get; init; } = "";
    public string FileName { get; init; } = "";
    public bool Ok => Document is not null;
}

public static class InvoiceImportService
{
    /// <summary>Не больше 20 МБ (как у «Массового добавления»); больше — отказ без чтения содержимого.</summary>
    public const long MaxFileBytes = ProductFileReader.MaxFileBytes;

    public static readonly string[] Extensions = [".xlsx", ".csv", ".xml", ".txt", ".tsv"];

    private static readonly string[] ImageExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".heic", ".tif", ".tiff"];

    public static bool IsSupportedExtension(string? path) => Extensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    public static bool IsImageOrPdfExtension(string? path) => ImageExtensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    private static string AiHint => Tr.T(
        "PDF и фото накладной читает ИИ «Нур Советника»: откройте Нур Советник, нажмите «Приложить фото» и выберите файл — он подготовит приход с наценкой (нужен ключ Gemini в настройках). Эта кнопка загружает электронные накладные Excel (.xlsx), CSV и XML.",
        "Накладнойдун PDF жана сүрөтүн «Нур Кеңешчи»нин ЖИси окуйт: Нур Кеңешчини ачып, «Сүрөт тиркөө» басып файлды тандаңыз (Gemini ачкычы керек). Бул баскыч Excel (.xlsx), CSV жана XML электрондук накладнойлорду жүктөйт.",
        "PDF and photo invoices are read by the AI of Nur Advisor: open Nur Advisor, press “Attach photo” and pick the file (a Gemini key is required in settings). This button loads electronic invoices in Excel (.xlsx), CSV and XML.",
        "PDF ve fotoğraf faturaları Nur Danışman'ın yapay zekâsı tarafından okunur: Nur Danışman'ı açın, «Fotoğraf ekle»ye basın ve dosyayı seçin (ayarlarda Gemini anahtarı gerekir). Bu düğme Excel (.xlsx), CSV ve XML e-faturaları yükler.",
        "PDF va foto yuk xatini «Nur Maslahatchi» AI o'qiydi: Nur Maslahatchini oching, «Foto biriktirish»ni bosing va faylni tanlang (Gemini kaliti kerak). Bu tugma Excel (.xlsx), CSV va XML elektron yuk xatlarini yuklaydi.");

    private static InvoiceLoadResult Fail(string error, string fileName = "", string hash = "", bool image = false) =>
        new() { Error = error, FileName = fileName, FileHash = hash, IsImageOrPdf = image };

    /// <summary>Прочитать и разобрать файл (синхронно; вызывать из Task.Run).</summary>
    public static InvoiceLoadResult Load(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return Fail(Tr.T("Файл не найден.", "Файл табылган жок.", "File not found.", "Dosya bulunamadı.", "Fayl topilmadi."), name);
            if (info.Length == 0)
                return Fail(Tr.T("Файл пустой.", "Файл бош.", "The file is empty.", "Dosya boş.", "Fayl bo'sh."), name);
            if (info.Length > MaxFileBytes)
            {
                PosLogger.Log($"INVOICE_IMPORT: файл отклонён, размер {info.Length / 1024 / 1024} МБ больше предела.", "WARNING");
                return Fail(Tr.T("Файл больше 20 МБ — это не похоже на накладную. Проверьте, тот ли файл выбран, или разделите его.",
                    "Файл 20 МБдан чоң — накладнойго окшобойт. Туура файл тандалганын текшериңиз же бөлүңүз.",
                    "The file is larger than 20 MB — it does not look like an invoice. Check that it is the right file or split it.",
                    "Dosya 20 MB'tan büyük — faturaya benzemiyor. Doğru dosyayı seçtiğinizi kontrol edin veya bölün.",
                    "Fayl 20 MB dan katta — yuk xatiga o'xshamaydi. To'g'ri fayl tanlanganini tekshiring yoki bo'ling."), name);
            }
            if (IsImageOrPdfExtension(path))
                return Fail(AiHint, name, image: true);

            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
            }
            var hash = InvoiceImportRegistry.HashOf(bytes);

            if (LooksLikeImageOrPdf(bytes))
                return Fail(AiHint, name, hash, image: true);

            InvoiceLoadResult result;
            if (LooksLikeXml(bytes))
                result = LoadXml(bytes, name, hash);
            else
                result = LoadTable(path, name, hash);

            if (result.Ok)
                PosLogger.Log($"INVOICE_IMPORT: файл {name} прочитан — {result.Document!.Format}, строк {result.Document.Items.Count}, предупреждений {result.Document.Warnings.Count}.", "INVOICE_IMPORT");
            else
                PosLogger.Log($"INVOICE_IMPORT: файл {name} не разобран — {(result.NeedsMapping ? "нужно сопоставить столбцы" : (result.Error ?? "").Split('.')[0])}.", "WARNING");
            return result;
        }
        catch (OutOfMemoryException)
        {
            return Fail(Tr.T("Не хватило памяти для чтения файла.", "Файлды окууга эс жетпей калды.", "Not enough memory to read the file.", "Dosyayı okumak için bellek yetmedi.", "Faylni o'qishga xotira yetmadi."), name);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"INVOICE_IMPORT: файл {name} не прочитан: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return Fail(Tr.T("Не удалось открыть файл: " + ex.Message, "Файлды ачуу мүмкүн болгон жок: " + ex.Message, "Could not open the file: " + ex.Message,
                "Dosya açılamadı: " + ex.Message, "Faylni ochib bo'lmadi: " + ex.Message), name);
        }
    }

    private static bool LooksLikeImageOrPdf(byte[] b) =>
        (b.Length > 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46)                       // %PDF
        || (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)                      // PNG
        || (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)                                      // JPEG
        || (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P'); // WEBP

    /// <summary>XML: первый значащий символ — «&lt;» (после BOM и пробелов), и это не архив xlsx.</summary>
    private static bool LooksLikeXml(byte[] b)
    {
        var i = 0;
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            i = 3;
        else if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)))
        {
            // UTF-16 с BOM: и XML, и «Текст Юникод» из Excel — смотрим на первый значащий символ.
            var text = System.Text.Encoding.Unicode.GetString(b, 2, Math.Min(b.Length - 2, 64)).TrimStart();
            if (b[0] == 0xFE)
                text = System.Text.Encoding.BigEndianUnicode.GetString(b, 2, Math.Min(b.Length - 2, 64)).TrimStart();
            return text.StartsWith('<');
        }
        while (i < b.Length && (b[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            i++;
        return i < b.Length && b[i] == (byte)'<';
    }

    private static InvoiceLoadResult LoadXml(byte[] bytes, string name, string hash)
    {
        var xml = InvoiceXmlParser.Parse(bytes, name);
        if (xml.Document is null)
            return Fail(xml.Error ?? "", name, hash);
        return new InvoiceLoadResult { Document = xml.Document, FileName = name, FileHash = hash };
    }

    private static InvoiceLoadResult LoadTable(string path, string name, string hash)
    {
        var read = ProductFileReader.ReadFile(path);
        if (!read.Ok)
            return Fail(read.Error ?? "", name, hash);
        var table = read.Table!;
        if (table.Rows.Count == 0)
            return Fail(Tr.T("В файле нет данных.", "Файлда маалымат жок.", "The file has no data.", "Dosyada veri yok.", "Faylda ma'lumot yo'q."), name, hash);

        var detection = InvoiceTableParser.Detect(table);
        var mapping = detection.Mapping;
        if (!detection.Recognized || !mapping.IsComplete)
            return new InvoiceLoadResult
            {
                Table = table,
                HeaderIndex = detection.HeaderIndex,
                Mapping = mapping,
                NeedsMapping = true,
                FileName = name,
                FileHash = hash,
                Error = Tr.T("Не удалось определить столбцы накладной автоматически. Укажите, где наименование, количество и цена.",
                    "Накладнойдун мамычаларын автоматтык аныктоо мүмкүн болгон жок. Аталышы, саны жана баасы кайда экенин көрсөтүңүз.",
                    "Could not detect the invoice columns automatically. Point out where the name, quantity and price are.",
                    "Fatura sütunları otomatik belirlenemedi. Ad, miktar ve fiyatın nerede olduğunu belirtin.",
                    "Yuk xati ustunlarini avtomatik aniqlab bo'lmadi. Nomi, miqdori va narxi qayerdaligini ko'rsating."),
            };
        return FromTable(table, detection.HeaderIndex, mapping, name, hash);
    }

    /// <summary>Разобрать таблицу с выбранными строкой заголовков и столбцами (после ручного сопоставления — тоже сюда).</summary>
    public static InvoiceLoadResult FromTable(ProductFileTable table, int headerIndex, InvoiceColumnMapping mapping, string name, string hash)
    {
        var doc = InvoiceTableParser.Parse(table, headerIndex, mapping, name);
        if (doc.Items.Count == 0)
            return new InvoiceLoadResult
            {
                Table = table,
                HeaderIndex = headerIndex,
                Mapping = mapping,
                NeedsMapping = true,
                FileName = name,
                FileHash = hash,
                Error = Tr.T("В таблице не найдено строк товара с этими столбцами. Проверьте строку заголовков и столбцы.",
                    "Таблицада бул мамычалар менен товар саптары табылган жок. Аталыштар сабын жана мамычаларды текшериңиз.",
                    "No product rows were found with these columns. Check the header row and the columns.",
                    "Bu sütunlarla ürün satırı bulunamadı. Başlık satırını ve sütunları kontrol edin.",
                    "Bu ustunlar bilan mahsulot qatorlari topilmadi. Sarlavha qatorini va ustunlarni tekshiring."),
            };
        return new InvoiceLoadResult { Document = doc, Table = table, HeaderIndex = headerIndex, Mapping = mapping, FileName = name, FileHash = hash };
    }
}
