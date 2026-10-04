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
        "Одежда с размерами и цветами: продажа проходит одним запросом — быстрее (было 1,3 с, стало 0,8 с), окно размеров открывается сразу, а остаток проданного размера уменьшается сразу после оплаты",
        "Бот магазина знает размеры и цвета: на «какие размеры есть?» сам называет размеры и цвета в наличии",
        "Android: касса и программа владельца открываются и работают на телефонах и кассовых терминалах; экран поворачивается, все окна подстраиваются под экран, прокрутка пальцем",
        "Android: камера телефона — сканер штрихкодов (в чеке, в карточке товара, в приёмке), с анимацией и зелёной вспышкой при удачном скане",
        "Android и Linux: значки на месте (раньше были квадраты); экранная клавиатура больше не выскакивает сама при открытии окна",
    ];

    public static readonly string[] LatestKy =
    [
        "Өлчөмү жана түсү бар кийим: сатуу бир суроо менен өтөт — тезирээк (1,3 с → 0,8 с), өлчөм терезеси дароо ачылат, сатылган өлчөмдүн калдыгы төлөмдөн кийин дароо азаят",
        "Дүкөндүн боту өлчөмдөрдү жана түстөрдү билет: «кандай өлчөмдөр бар?» дегенде бар өлчөмдөрдү жана түстөрдү өзү айтат",
        "Android: касса жана ээсинин программасы телефондордо жана касса терминалдарында ачылат жана иштейт; экран бурулат, бардык терезелер экранга ылайыкташат, манжа менен жылдыруу",
        "Android: телефондун камерасы — штрихкод сканери (чекте, товар карточкасында, кабыл алууда), анимациясы жана ийгиликтүү скандагы жашыл жаркылдоо менен",
        "Android жана Linux: белгилер ордунда (мурда төрт бурчтуктар болчу); терезе ачылганда экран клавиатурасы өзү чыкпайт",
    ];

    public static readonly string[] LatestEn =
    [
        "Clothing with sizes and colors: the sale goes in one request — faster (1.3 s → 0.8 s), the size window opens instantly, and the sold size's stock drops right after payment",
        "The shop bot knows sizes and colors: asked “what sizes are there?” it lists the sizes and colors in stock",
        "Android: the till and the owner program open and work on phones and POS terminals; the screen rotates, every window adapts to the screen, finger scrolling",
        "Android: the phone camera is a barcode scanner (in the receipt, the product card, receiving), with an animation and a green flash on a good scan",
        "Android and Linux: icons are back (they were squares); the on-screen keyboard no longer pops up by itself when a window opens",
    ];

    public static readonly string[] LatestTr =
    [
        "Beden ve renkli giyim: satış tek istekte geçer — daha hızlı (1,3 sn → 0,8 sn), beden penceresi hemen açılır, satılan bedenin stoğu ödemeden hemen sonra düşer",
        "Mağaza botu beden ve renkleri biliyor: «hangi bedenler var?» sorusuna stoktaki beden ve renkleri kendisi söyler",
        "Android: kasa ve işletme sahibi programı telefonlarda ve POS terminallerinde açılıp çalışır; ekran döner, tüm pencereler ekrana uyum sağlar, parmakla kaydırma",
        "Android: telefon kamerası barkod okuyucu (fişte, ürün kartında, mal kabulde), animasyonlu ve başarılı okumada yeşil yanıp sönme ile",
        "Android ve Linux: simgeler yerinde (önceden kareydi); pencere açılınca ekran klavyesi artık kendiliğinden açılmıyor",
    ];

    public static readonly string[] LatestUz =
    [
        "O'lcham va rangli kiyim: sotuv bitta so'rovda o'tadi — tezroq (1,3 s → 0,8 s), o'lcham oynasi darhol ochiladi, sotilgan o'lcham qoldig'i to'lovdan keyin darhol kamayadi",
        "Do'kon boti o'lcham va ranglarni biladi: «qanday o'lchamlar bor?» deganda mavjud o'lcham va ranglarni o'zi aytadi",
        "Android: kassa va ega dasturi telefonlarda va kassa terminallarida ochiladi va ishlaydi; ekran aylanadi, barcha oynalar ekranga moslashadi, barmoq bilan aylantirish",
        "Android: telefon kamerasi — shtrix-kod skaneri (chekda, mahsulot kartasida, qabul qilishda), animatsiya va muvaffaqiyatli skanda yashil chaqnash bilan",
        "Android va Linux: belgilar joyida (avval kvadratlar edi); oyna ochilganda ekran klaviaturasi endi o'zi chiqmaydi",
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
