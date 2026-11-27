namespace NurMarketKassa.Services;

/// <summary>2026-11-26 («создай то, чего у нас нет», услуга «Заполним вашу базу»): магазин просит поддержку перенести и ввести товары под ключ — из Excel, из фото накладных
/// или из 1С. Форма собирает название магазина, телефон, что есть у магазина и ссылку на файлы (облако: Google Drive, Яндекс.Диск…), а здесь из неё делается готовое сообщение
/// и ссылка wa.me на чат поддержки (тот же номер, что везде: <see cref="MarketSphereLock.SupportPhone"/>). Автоотправки нет: открывается WhatsApp с текстом,
/// «Отправить» нажимает владелец сам.</summary>
public static class FillBaseRequest
{
    [Flags]
    public enum Sources
    {
        None = 0,
        Excel = 1,
        InvoicePhotos = 2,
        OneC = 4,
        Other = 8,
    }

    public sealed record Form(string Shop, string Phone, Sources Sources, string FileLink, string Comment);

    public static string SourceTitle(Sources source) => source switch
    {
        Sources.Excel => Tr.T("Excel / CSV файл", "Excel / CSV файл", "Excel / CSV file", "Excel / CSV dosyası", "Excel / CSV fayl"),
        Sources.InvoicePhotos => Tr.T("Фото накладных", "Накладныйлардын сүрөттөрү", "Photos of invoices", "Fatura fotoğrafları", "Hujjat suratlari"),
        Sources.OneC => Tr.T("База 1С", "1С базасы", "1C database", "1C veritabanı", "1C bazasi"),
        _ => Tr.T("Другое", "Башка", "Other", "Diğer", "Boshqa"),
    };

    /// <summary>Телефон — не меньше 9 цифр (0700 123 456, +996 700 123 456); ссылка на файлы, если указана, — http(s). Пустая строка — всё в порядке.</summary>
    public static string? Validate(Form form)
    {
        if (string.IsNullOrWhiteSpace(form.Shop))
            return Tr.T("Впишите название магазина.", "Дүкөндүн атын жазыңыз.", "Enter the store name.", "Mağaza adını yazın.", "Do'kon nomini yozing.");
        if (Digits(form.Phone).Length < 9)
            return Tr.T("Впишите телефон, по которому с вами связаться (не меньше 9 цифр).", "Сиз менен байланышуучу телефонду жазыңыз (кеминде 9 сан).",
                "Enter a phone number we can reach you on (at least 9 digits).", "Size ulaşabileceğimiz telefonu yazın (en az 9 hane).", "Siz bilan bog'lanish uchun telefonni yozing (kamida 9 raqam).");
        if (form.Sources == Sources.None)
            return Tr.T("Отметьте, что у вас есть: Excel, фото накладных, база 1С или другое.", "Эмнеңиз бар экенин белгилеңиз: Excel, накладныйлардын сүрөттөрү, 1С базасы же башка.",
                "Tick what you have: Excel, photos of invoices, a 1C database or something else.", "Elinizde ne olduğunu işaretleyin: Excel, fatura fotoğrafları, 1C veritabanı veya başka.",
                "Sizda nima borligini belgilang: Excel, hujjat suratlari, 1C bazasi yoki boshqa.");
        var link = form.FileLink.Trim();
        if (link.Length > 0 && !(Uri.TryCreate(link, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            return Tr.T("Ссылка на файлы должна начинаться с https:// (облако: Google Drive, Яндекс.Диск…). Или оставьте поле пустым — файлы пришлёте в чате.",
                "Файлдарга шилтеме https:// менен башталышы керек (булут: Google Drive, Яндекс.Диск…). Же талааны бош калтырыңыз — файлдарды чатка жибересиз.",
                "The link to the files must start with https:// (cloud: Google Drive, Yandex Disk…). Or leave the field empty and send the files in the chat.",
                "Dosya bağlantısı https:// ile başlamalı (bulut: Google Drive, Yandex Disk…). Ya da alanı boş bırakın ve dosyaları sohbetten gönderin.",
                "Fayllarga havola https:// bilan boshlanishi kerak (bulut: Google Drive, Yandex Disk…). Yoki maydonni bo'sh qoldiring — fayllarni chatda yuborasiz.");
        // 2026-11-26 (стресс-тест): кириллица в ссылке wa.me кодируется по 6 знаков на букву — длинный комментарий + ссылка давали адрес > 2000 знаков,
        // который Windows/браузер может не открыть («ничего не произошло»). Просим сократить, а не молча обрезаем.
        if (BuildUrl(form).Length > MaxUrlLength)
            return Tr.T("Сообщение получилось слишком длинным для WhatsApp. Сократите комментарий — подробности можно дописать в чате.",
                "Билдирүү WhatsApp үчүн өтө узун болуп калды. Комментарийди кыскартыңыз — толугураак чатка жазсаңыз болот.",
                "The message is too long for WhatsApp. Shorten the comment — you can add details in the chat.",
                "Mesaj WhatsApp için çok uzun. Yorumu kısaltın — ayrıntıları sohbette yazabilirsiniz.",
                "Xabar WhatsApp uchun juda uzun bo'lib qoldi. Izohni qisqartiring — batafsilini chatda yozishingiz mumkin.");
        return null;
    }

    /// <summary>Предел длины ссылки wa.me (как у рассылки, ClientMailing.MaxUrlLength): длиннее система может её не открыть.</summary>
    public const int MaxUrlLength = 2000;

    /// <summary>Готовый текст сообщения поддержке (на языке программы).</summary>
    public static string BuildText(Form form)
    {
        var sources = string.Join(", ", new[] { Sources.Excel, Sources.InvoicePhotos, Sources.OneC, Sources.Other }
            .Where(s => form.Sources.HasFlag(s)).Select(SourceTitle));
        var lines = new List<string>
        {
            Tr.T("Здравствуйте! Хочу заказать услугу «Загрузим вашу базу товаров под ключ».", "Саламатсызбы! «Товарлар базаңызды ачкычка даяр кылып жүктөйбүз» кызматын заказ кылгым келет.",
                "Hello! I would like to order the “We will load your product database for you” service.", "Merhaba! «Ürün veritabanınızı sizin için yükleyelim» hizmetini sipariş etmek istiyorum.",
                "Assalomu alaykum! «Mahsulotlar bazangizni kalitga tayyor qilib yuklaymiz» xizmatini buyurtma qilmoqchiman."),
            Tr.T("Магазин: ", "Дүкөн: ", "Store: ", "Mağaza: ", "Do'kon: ") + form.Shop.Trim(),
            Tr.T("Телефон: ", "Телефон: ", "Phone: ", "Telefon: ", "Telefon: ") + form.Phone.Trim(),
            Tr.T("Что есть: ", "Эмне бар: ", "What I have: ", "Elimde olan: ", "Menda bor: ") + sources,
        };
        if (form.FileLink.Trim().Length > 0)
            lines.Add(Tr.T("Файлы по ссылке: ", "Файлдар шилтеме боюнча: ", "Files at the link: ", "Dosyalar bağlantıda: ", "Fayllar havolada: ") + form.FileLink.Trim());
        else
            lines.Add(Tr.T("Файлы пришлю в этом чате.", "Файлдарды ушул чатка жиберем.", "I will send the files in this chat.", "Dosyaları bu sohbetten göndereceğim.", "Fayllarni shu chatda yuboraman."));
        if (form.Comment.Trim().Length > 0)
            lines.Add(Tr.T("Комментарий: ", "Комментарий: ", "Comment: ", "Yorum: ", "Izoh: ") + form.Comment.Trim());
        return string.Join("\n", lines);
    }

    /// <summary>https://wa.me/&lt;номер поддержки&gt;?text=&lt;сообщение&gt;. Открывает чат; сообщение уходит, только если владелец сам нажмёт «Отправить».</summary>
    public static string BuildUrl(Form form) =>
        "https://wa.me/" + Digits(MarketSphereLock.SupportPhone) + "?text=" + Uri.EscapeDataString(BuildText(form));

    private static string Digits(string? text) => new((text ?? "").Where(char.IsDigit).ToArray());
}
