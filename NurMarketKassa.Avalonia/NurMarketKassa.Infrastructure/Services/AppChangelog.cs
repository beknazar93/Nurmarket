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
        "Телеграм-бот: бот, подключённый в программе владельца, теперь отвечает на команды — касса подхватывает его настройки сама",
        "Боту можно писать обычными словами: «сколько заработали сегодня», «кто должен», «цена кола» — без интернета и бесплатно",
        "ИИ в боте (бесплатный ключ Google Gemini): свободно общается с владельцем, а покупателям отвечает как продавец-консультант — только товары, цены и наличие",
        "«Сводка» владельца: карточка «Заканчивается на складе»; окно «Пополнение и сроки» больше не выдаёт ошибку",
    ];

    public static readonly string[] LatestKy =
    [
        "Телеграм-бот: ээсинин программасында туташтырылган бот эми буйруктарга жооп берет — касса анын жөндөөлөрүн өзү алат",
        "Ботко кадимки сөздөр менен жазса болот: «бүгүн канча түшүм», «ким карыз», «кола баасы» — интернетсиз жана акысыз",
        "Боттогу ЖИ (Google Gemini акысыз ачкычы): ээси менен эркин баарлашат, ал эми сатып алуучуларга сатуучу-кеңешчи катары жооп берет — товарлар, баалар жана бар-жогу гана",
        "Ээсинин «Жыйынтыгы»: «Кампада түгөнүп баратат» карточкасы; «Толуктоо жана мөөнөттөр» терезеси мындан ары ката бербейт",
    ];

    public static readonly string[] LatestEn =
    [
        "Telegram bot: a bot connected in the owner app now answers commands — the till picks up its settings automatically",
        "You can write to the bot in plain words: “how much did we make today”, “who owes”, “price of cola” — offline and free",
        "AI in the bot (free Google Gemini key): chats freely with the owner and answers customers as a shop assistant — only products, prices and availability",
        "Owner “Overview”: a “Running low in stock” card; the “Restock & expiry” window no longer shows an error",
    ];

    public static readonly string[] LatestTr =
    [
        "Telegram botu: sahip programında bağlanan bot artık komutlara yanıt veriyor — kasa ayarlarını kendisi alıyor",
        "Bota sade cümlelerle yazılabilir: «bugün ne kadar kazandık», «kim borçlu», «kola fiyatı» — internetsiz ve ücretsiz",
        "Bottaki YZ (ücretsiz Google Gemini anahtarı): sahiple serbestçe sohbet eder, müşterilere satış danışmanı olarak yanıt verir — yalnızca ürünler, fiyatlar ve stok",
        "Sahip «Özet»: «Stokta azalanlar» kartı; «Stok yenileme ve SKT» penceresi artık hata vermiyor",
    ];

    public static readonly string[] LatestUz =
    [
        "Telegram bot: egasi dasturida ulangan bot endi buyruqlarga javob beradi — kassa uning sozlamalarini o‘zi oladi",
        "Botga oddiy so‘zlar bilan yozish mumkin: «bugun qancha ishladik», «kim qarzdor», «kola narxi» — internetsiz va bepul",
        "Botdagi SI (bepul Google Gemini kaliti): egasi bilan erkin suhbatlashadi, xaridorlarga esa sotuvchi-maslahatchi sifatida javob beradi — faqat mahsulotlar, narxlar va mavjudlik",
        "Egasining «Umumiy ko‘rinish»i: «Omborda tugayapti» kartasi; «To'ldirish va muddatlar» oynasi endi xato bermaydi",
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
