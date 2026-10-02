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
        "Прокат прямо из оплаты: кнопка «Оформить как прокат (аренда)» в окне оплаты — вещи и цена переносятся сами, оплата любым способом, в чеке строка «Прокат №N»",
        "Возврат по номеру с чека: «Прокат» → поле «№ проката с чека» → Enter — прокат закрывается",
        "Напоминания о сроке проката: в программе владельца — число у «Прокат» и карточка «вернуть сегодня / завтра / просрочено», владельцу в Телеграм — сообщение; в списке проката — оранжевая отметка",
        "В окне проката: список клиентов открывается сразу, можно добавить нового клиента; в истории покупок клиента — его прокаты",
        "Пустой чек закрывается сам при переходе на другой чек; новый пустой чек не создаётся, если текущий и так пустой",
    ];

    public static readonly string[] LatestKy =
    [
        "Прокат төлөмдөн түз: төлөм терезесинде «Прокат (ижара) катары тариздөө» баскычы — буюмдар жана баа өзү өтөт, каалаган жол менен төлөө, чекте «Прокат №N» сабы",
        "Чектеги номер боюнча кайтаруу: «Прокат» → «Чектеги прокат №» талаасы → Enter — прокат жабылат",
        "Прокаттын мөөнөтү жөнүндө эскертүү: ээсинин программасында «Прокаттын» жанында сан жана «бүгүн / эртең кайтаруу / мөөнөтү өттү» карточкасы, ээсине Телеграмга билдирүү; прокат тизмесинде кызгылт сары белги",
        "Прокат терезесинде: кардарлар тизмеси дароо ачылат, жаңы кардар кошсо болот; кардардын сатып алуулар тарыхында анын прокаттары",
        "Бош чек башка чекке өткөндө өзү жабылат; учурдагы чек бош болсо, жаңы бош чек түзүлбөйт",
    ];

    public static readonly string[] LatestEn =
    [
        "Rental right from payment: “Make it a rental” button in the payment window — items and price carry over, any payment method, the receipt shows “Rental #N”",
        "Take back by the receipt number: “Rentals” → “Rental # from receipt” field → Enter — the rental is closed",
        "Rental due reminders: in the owner app — a count next to “Rentals” and a “due today / tomorrow / overdue” card, a Telegram message to the owner; an orange mark in the rental list",
        "In the rental window: the client list opens at once, a new client can be added; the client's purchase history shows their rentals",
        "An empty receipt closes itself when you switch to another one; no new empty receipt if the current one is already empty",
    ];

    public static readonly string[] LatestTr =
    [
        "Ödemeden doğrudan kiralama: ödeme penceresinde «Kiralama olarak düzenle» düğmesi — ürünler ve fiyat kendiliğinden aktarılır, her ödeme yöntemi, fişte «Kiralama №N» satırı",
        "Fiş numarasıyla iade: «Kiralama» → «Fişteki kiralama №» alanı → Enter — kiralama kapanır",
        "Kiralama süresi hatırlatmaları: işletme sahibi programında «Kiralama» yanında sayı ve «bugün / yarın iade / gecikmiş» kartı, sahibine Telegram mesajı; kiralama listesinde turuncu işaret",
        "Kiralama penceresinde: müşteri listesi hemen açılır, yeni müşteri eklenebilir; müşterinin satın alma geçmişinde kiralamaları",
        "Boş fiş başka fişe geçince kendiliğinden kapanır; mevcut fiş zaten boşsa yeni boş fiş açılmaz",
    ];

    public static readonly string[] LatestUz =
    [
        "To'lovdan to'g'ridan-to'g'ri prokat: to'lov oynasida «Prokat (ijara) sifatida rasmiylashtirish» tugmasi — buyumlar va narx o'zi o'tadi, istalgan to'lov usuli, chekda «Prokat №N» qatori",
        "Chekdagi raqam bo'yicha qaytarish: «Prokat» → «Chekdagi prokat №» maydoni → Enter — prokat yopiladi",
        "Prokat muddati eslatmalari: egasi dasturida «Prokat» yonida son va «bugun / ertaga qaytarish / muddati o'tgan» kartasi, egasiga Telegramda xabar; prokat ro'yxatida to'q sariq belgi",
        "Prokat oynasida: mijozlar ro'yxati darhol ochiladi, yangi mijoz qo'shish mumkin; mijozning xaridlar tarixida uning prokatlari",
        "Bo'sh chek boshqa chekka o'tganda o'zi yopiladi; joriy chek bo'sh bo'lsa, yangi bo'sh chek ochilmaydi",
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
