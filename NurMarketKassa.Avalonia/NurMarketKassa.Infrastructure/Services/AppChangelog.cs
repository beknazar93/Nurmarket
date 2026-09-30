namespace NurMarketKassa.Services;

/// <summary>Краткое описание изменений ТЕКУЩЕЙ версии — показывается кассиру один раз
/// в окне "Что нового" после того, как касса обновилась (см. UserPreferences.LastSeenAppVersion),
/// и используется как текст релиза при публикации на GitHub.
/// ВАЖНО: перед каждым релизом содержимое <see cref="Latest"/> (и переводов) полностью ЗАМЕНЯЕТСЯ
/// на изменения именно этой версии — не дописывается поверх старых пунктов. История прошлых версий
/// не нужна здесь: она уже есть в самих релизах на GitHub, а этот список — только "что нового прямо
/// сейчас". 2026-09-07: список показывается на языке интерфейса (ru/ky/en/tr/uz, см.
/// <see cref="LatestForCurrentLanguage"/>) — раньше при кыргызском интерфейсе заголовок был
/// кыргызским, а сами пункты русскими.</summary>
public static class AppChangelog
{
    public static readonly string[] Latest =
    [
        "Телеграм-бот считает обращения покупателей: спросите бота «сколько обращений было» — сегодня, за 7 дней и последние вопросы",
        "Заказ через бота: покупатель пишет, что хочет купить, называет имя и телефон — бот оформляет заказ, а владельцу приходит уведомление",
        "Ответы ИИ в боте: списки товаров аккуратно — каждый товар с новой строки, по группам",
        "Программа владельца: раздел «Телеграм-бот» — обращения, покупатели и заказы через бота; бот отвечает и на сообщения, пришедшие во время перезапуска",
    ];

    public static readonly string[] LatestKy =
    [
        "Телеграм-бот сатып алуучулардын кайрылууларын эсептейт: ботко «канча кайрылуу болду» деп жазыңыз — бүгүн, 7 күн жана акыркы суроолор",
        "Бот аркылуу заказ: сатып алуучу эмне сатып алгысы келгенин, атын жана телефонун жазат — бот заказ түзөт, ээсине билдирүү келет",
        "Боттогу ЖИ жооптору: товарлардын тизмеси тыкан — ар бир товар жаңы саптан, топтор боюнча",
        "Ээсинин программасы: «Телеграм-бот» бөлүмү — кайрылуулар, сатып алуучулар жана бот аркылуу заказдар; бот кайра иштетүү учурунда келген билдирүүлөргө да жооп берет",
    ];

    public static readonly string[] LatestEn =
    [
        "The Telegram bot counts customer inquiries: ask it “how many inquiries” — today, last 7 days and the latest questions",
        "Ordering via the bot: a customer says what to buy, gives name and phone — the bot places the order and notifies the owner",
        "AI replies in the bot: product lists are tidy — one product per line, grouped",
        "Owner app: “Telegram bot” section — inquiries, customers and orders via the bot; the bot also answers messages sent during a restart",
    ];

    public static readonly string[] LatestTr =
    [
        "Telegram botu müşteri başvurularını sayar: bota «kaç başvuru oldu» diye sorun — bugün, 7 gün ve son sorular",
        "Bot üzerinden sipariş: müşteri ne almak istediğini, adını ve telefonunu yazar — bot siparişi oluşturur, sahibine bildirim gider",
        "Bottaki YZ yanıtları: ürün listeleri düzenli — her ürün yeni satırda, gruplar halinde",
        "Sahip programı: «Telegram botu» bölümü — başvurular, müşteriler ve bot siparişleri; bot yeniden başlatma sırasında gelen mesajlara da yanıt verir",
    ];

    public static readonly string[] LatestUz =
    [
        "Telegram bot xaridorlar murojaatlarini sanaydi: botdan «qancha murojaat bo'ldi» deb so'rang — bugun, 7 kun va oxirgi savollar",
        "Bot orqali buyurtma: xaridor nima olmoqchiligini, ismi va telefonini yozadi — bot buyurtmani rasmiylashtiradi, egasiga xabar keladi",
        "Botdagi SI javoblari: mahsulotlar ro'yxati tartibli — har bir mahsulot yangi qatorda, guruhlab",
        "Egasi dasturi: «Telegram bot» bo'limi — murojaatlar, xaridorlar va bot orqali buyurtmalar; bot qayta ishga tushirish paytida kelgan xabarlarga ham javob beradi",
    ];

    public static string[] LatestForCurrentLanguage() =>
        UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => LatestKy,
            AppLanguage.English => LatestEn,
            AppLanguage.Turkish => LatestTr,
            AppLanguage.Uzbek => LatestUz,
            _ => Latest,
        };

    public static string LatestAsBulletedText() =>
        string.Join("\n", System.Linq.Enumerable.Select(LatestForCurrentLanguage(), line => "• " + line));
}
