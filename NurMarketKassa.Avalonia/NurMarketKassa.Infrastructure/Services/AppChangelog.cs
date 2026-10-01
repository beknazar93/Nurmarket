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
        "Чековый принтер: «Символов в строке» и ширина графики для 58 и 80 мм — текст и картинки больше не уходят за край чека",
        "Принтер этикеток: размер наклейки берётся из шаблона, зазор и сдвиг настраиваются — наклейки не пропускаются и печатаются на месте",
        "Денежный ящик открывается при каждой оплате наличными; весы: товары на горячих клавишах больше не слетают",
        "Права сотрудников как на сайте NurCRM: «Склад», «Аналитика», «Клиенты» и «Заказы» видны только тем, кому их выдали",
        "Калькуляция: кнопка «Обновить» — всегда свежие цены и остатки; «Подробнее» с описанием товара видно и на плитках с фото",
        "Продажа в долг с предоплатой: наличные сразу попадают в кассу и отчёт смены",
        "Программа владельца: раздел «Прибыль и деньги» — прибыль, движение денег и сверка за период",
        "Новое: подсказка «С этим часто берут» над итогом чека (включается в Настройки → Экран)",
        "Телеграм-бот и ИИ могут работать на сервере NurCRM — бот отвечает круглые сутки, даже когда компьютер выключен (программа один раз спросит; кнопка — Настройки → Операции → «Подключить бота»)",
    ];

    public static readonly string[] LatestKy =
    [
        "Чек принтери: 58 жана 80 мм үчүн «Саптагы белгилер» жана графиканын туурасы — текст менен сүрөттөр чектин четинен чыкпайт",
        "Этикетка принтери: чаптаманын өлчөмү шаблондон алынат, аралык жана жылыш жөндөлөт — чаптамалар өткөрүлбөйт жана ордуна басылат",
        "Акча кутусу накталай төлөгөн сайын ачылат; таразалар: ыкчам баскычтардагы товарлар мындан ары түшүп калбайт",
        "Кызматкерлердин укуктары NurCRM сайтындагыдай: «Кампа», «Аналитика», «Кардарлар» жана «Заказдар» берилгендерге гана көрүнөт",
        "Калькуляция: «Жаңыртуу» баскычы — баалар жана калдыктар дайыма жаңы; товардын сүрөттөмөсү бар «Толугураак» сүрөттүү плиткаларда да көрүнөт",
        "Алдын ала төлөм менен карызга сатуу: накталай дароо кассага жана смена отчётуна түшөт",
        "Ээсинин программасы: «Пайда жана акча» бөлүмү — мезгил ичиндеги пайда, акчанын кыймылы жана салыштыруу",
        "Жаңы: чектин жыйынтыгынын үстүндө «Муну менен көп алышат» кеңеши (Жөндөөлөр → Экран бөлүмүндө күйгүзүлөт)",
        "Телеграм-бот жана ЖИ NurCRM серверинде иштей алат — компьютер өчүк болсо да бот күнү-түнү жооп берет (программа бир жолу сурайт; баскыч — Жөндөөлөр → Операциялар → «Ботту туташтыруу»)",
    ];

    public static readonly string[] LatestEn =
    [
        "Receipt printer: “Characters per line” and graphics width for 58 and 80 mm — text and images no longer run off the receipt",
        "Label printer: label size comes from the template, gap and offset are adjustable — labels are no longer skipped and print in place",
        "The cash drawer opens on every cash payment; scales: products assigned to hotkeys no longer drop off",
        "Employee permissions match the NurCRM website: “Warehouse”, “Analytics”, “Clients” and “Orders” are visible only to those granted them",
        "Costing: “Refresh” button — always fresh prices and stock; “Details” with the product description is visible on tiles with photos too",
        "Sale on credit with a down payment: the cash goes straight into the till and the shift report",
        "Owner app: “Profit and money” section — profit, cash flow and reconciliation for a period",
        "New: “Often bought with this” suggestion above the receipt total (turn on in Settings → Screen)",
        "The Telegram bot and AI can run on the NurCRM server — the bot answers around the clock, even with the computer off (the app asks once; button — Settings → Operations → “Connect bot”)",
    ];

    public static readonly string[] LatestTr =
    [
        "Fiş yazıcısı: 58 ve 80 mm için «Satırdaki karakter sayısı» ve grafik genişliği — metin ve resimler artık fişin kenarından taşmıyor",
        "Etiket yazıcısı: etiket boyutu şablondan alınır, boşluk ve kaydırma ayarlanır — etiketler atlanmaz ve yerine basılır",
        "Para çekmecesi her nakit ödemede açılır; teraziler: kısayol tuşlarına atanan ürünler artık düşmüyor",
        "Çalışan yetkileri NurCRM sitesindeki gibi: «Depo», «Analitik», «Müşteriler» ve «Siparişler» yalnızca yetki verilenlere görünür",
        "Maliyet: «Yenile» düğmesi — fiyatlar ve stoklar hep güncel; ürün açıklamalı «Ayrıntılar» fotoğraflı kartlarda da görünür",
        "Ön ödemeli veresiye satış: nakit hemen kasaya ve vardiya raporuna geçer",
        "Sahip programı: «Kâr ve para» bölümü — dönem için kâr, nakit akışı ve mutabakat",
        "Yeni: fiş toplamının üstünde «Bununla sık alınanlar» önerisi (Ayarlar → Ekran'dan açılır)",
        "Telegram botu ve yapay zekâ NurCRM sunucusunda çalışabilir — bilgisayar kapalıyken bile bot günün her saati yanıt verir (program bir kez sorar; düğme — Ayarlar → İşlemler → «Botu bağla»)",
    ];

    public static readonly string[] LatestUz =
    [
        "Chek printeri: 58 va 80 mm uchun «Qatordagi belgilar» va grafika kengligi — matn va rasmlar chek chetidan chiqmaydi",
        "Yorliq printeri: yorliq o'lchami shablondan olinadi, oraliq va siljish sozlanadi — yorliqlar o'tkazib yuborilmaydi va joyida chop etiladi",
        "Pul qutisi har bir naqd to'lovda ochiladi; tarozilar: tezkor tugmalarga biriktirilgan mahsulotlar endi tushib qolmaydi",
        "Xodimlar huquqlari NurCRM saytidagidek: «Ombor», «Tahlil», «Mijozlar» va «Buyurtmalar» faqat ruxsat berilganlarga ko'rinadi",
        "Kalkulyatsiya: «Yangilash» tugmasi — narxlar va qoldiqlar doim yangi; mahsulot tavsifli «Batafsil» rasmli kartalarda ham ko'rinadi",
        "Oldindan to'lov bilan nasiyaga sotish: naqd pul darhol kassaga va smena hisobotiga tushadi",
        "Egasi dasturi: «Foyda va pul» bo'limi — davr uchun foyda, pul harakati va solishtirish",
        "Yangi: chek jamining ustida «Bu bilan ko'p olishadi» maslahati (Sozlamalar → Ekran bo'limida yoqiladi)",
        "Telegram bot va SI NurCRM serverida ishlashi mumkin — kompyuter o'chiq bo'lsa ham bot kecha-kunduz javob beradi (dastur bir marta so'raydi; tugma — Sozlamalar → Operatsiyalar → «Botni ulash»)",
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
