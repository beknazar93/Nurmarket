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
        "Прокат и аренда (одежда, услуги): выдача вещи с залогом деньгами или паспортом, возврат со штрафом, просрочки — меню кассы → «Прокат»",
        "Размеры и цвета: новое окно выбора — остаток под каждым размером, цвет с кружком, акционная цена со скидкой",
        "Скидка на размер видна в чеке и в корзине: «было 1900 (−16%)»",
        "Окно оплаты: иконки способов оплаты и ровные кнопки",
        "Аптека и ветаптека: если товар закончился — замена с тем же действующим веществом",
        "Телеграм-бот: знает размеры и цвета одежды, предлагает замену, отвечает про прокат («кто не вернул», «залоги»)",
        "Программа владельца: раздел «Прокат», размеры в «Размерах и цветах» по порядку",
    ];

    public static readonly string[] LatestKy =
    [
        "Прокат жана ижара (кийим, кызматтар): буюмду акча же паспорт күрөөсү менен берүү, айып менен кайтаруу, мөөнөтү өткөндөр — касса менюсу → «Прокат»",
        "Өлчөмдөр жана түстөр: жаңы тандоо терезеси — ар бир өлчөмдүн астында калдык, түс тегерек менен, акциялык баа арзандатуу менен",
        "Өлчөмгө арзандатуу чекте жана себетте көрүнөт: «1900 болгон (−16%)»",
        "Төлөм терезеси: төлөм ыкмаларынын сүрөтчөлөрү жана тегиз баскычтар",
        "Дарыкана жана ветдарыкана: товар түгөнсө — ошол эле таасир этүүчү заты бар алмаштыруу",
        "Телеграм-бот: кийимдин өлчөмдөрүн жана түстөрүн билет, алмаштыруу сунуштайт, прокат боюнча жооп берет",
        "Ээсинин программасы: «Прокат» бөлүмү, «Өлчөмдөр жана түстөрдө» өлчөмдөр тартиби менен",
    ];

    public static readonly string[] LatestEn =
    [
        "Rentals (clothing, services): rent out items with a cash or passport deposit, take back with a penalty, overdue list — till menu → “Rentals”",
        "Sizes and colors: new picker — stock under each size, color swatches, promo price with discount",
        "Size discount is shown in the receipt and the cart: “was 1900 (−16%)”",
        "Payment window: payment method icons and even buttons",
        "Pharmacy and vet pharmacy: if a product is out of stock — a replacement with the same active ingredient",
        "Telegram bot: knows clothing sizes and colors, suggests replacements, answers about rentals (“who hasn't returned”, “deposits”)",
        "Owner app: “Rentals” section, sizes in “Sizes and colors” in order",
    ];

    public static readonly string[] LatestTr =
    [
        "Kiralama (giyim, hizmetler): ürünü nakit veya pasaport depozitosuyla kiralama, cezalı iade, gecikenler — kasa menüsü → «Kiralama»",
        "Beden ve renkler: yeni seçim penceresi — her bedenin altında stok, renk dairesi, indirimli kampanya fiyatı",
        "Beden indirimi fişte ve sepette görünür: «önce 1900 (−16%)»",
        "Ödeme penceresi: ödeme yöntemi simgeleri ve düzgün düğmeler",
        "Eczane ve veteriner eczanesi: ürün bittiyse — aynı etken maddeli muadil",
        "Telegram botu: giyim beden ve renklerini bilir, muadil önerir, kiralama hakkında yanıt verir",
        "Sahip programı: «Kiralama» bölümü, «Bedenler ve renkler»de bedenler sıralı",
    ];

    public static readonly string[] LatestUz =
    [
        "Prokat va ijara (kiyim, xizmatlar): buyumni naqd pul yoki pasport garovi bilan berish, jarima bilan qaytarish, muddati o'tganlar — kassa menyusi → «Prokat»",
        "O'lcham va ranglar: yangi tanlash oynasi — har bir o'lcham ostida qoldiq, rang doirachasi, chegirmali aksiya narxi",
        "O'lchamga chegirma chekda va savatda ko'rinadi: «1900 edi (−16%)»",
        "To'lov oynasi: to'lov usullari belgilari va tekis tugmalar",
        "Dorixona va veterinariya dorixonasi: mahsulot tugasa — bir xil ta'sir etuvchi moddali almashtirish",
        "Telegram bot: kiyim o'lcham va ranglarini biladi, almashtirish taklif qiladi, prokat haqida javob beradi",
        "Egasi dasturi: «Prokat» bo'limi, «O'lcham va ranglar»da o'lchamlar tartib bilan",
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
