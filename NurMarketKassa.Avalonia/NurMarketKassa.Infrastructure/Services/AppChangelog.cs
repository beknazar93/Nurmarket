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
        "Карточка товара — вкладками: «Основное», «Категория и коды», «Упаковка», «Размеры и цвета», «Описание», «История»; вид товара — одним переключателем",
        "Прокат в новом виде: счётчики сверху, карточки с цветной полосой статуса, оформление по шагам, залог — двумя плитками",
        "Возврат проката: состояние двумя плитками и сразу видно, сколько вернуть клиенту",
        "Исправлено: у просроченного проката есть кнопка «Принять возврат»; колесо мыши над датой больше не меняет срок; в истории видно, сколько удержано",
        "Подсказка «С этим часто берут» — новый вид; выбранный размер и цвет не теряют выделение под мышкой",
    ];

    public static readonly string[] LatestKy =
    [
        "Товардын карточкасы — өтмөктөр менен: «Негизги», «Категория жана коддор», «Таңгак», «Өлчөмдөр жана түстөр», «Сүрөттөмө», «Тарых»",
        "Прокат жаңы көрүнүштө: жогоруда эсептегичтер, абал тилкеси бар карточкалар, кадам менен тариздөө, күрөө — эки плитка",
        "Прокатты кайтаруу: абалы эки плитка менен жана кардарга канча кайтаруу керектиги дароо көрүнөт",
        "Оңдолду: мөөнөтү өткөн прокатта «Кайтарууну кабыл алуу» баскычы бар; чычкан дөңгөлөгү датаны өзгөртпөйт; тарыхта канча кармалганы көрүнөт",
        "«Муну менен көп алышат» кеңеши — жаңы көрүнүш; тандалган өлчөм жана түс чычкандын астында белгисин жоготпойт",
    ];

    public static readonly string[] LatestEn =
    [
        "Product card with tabs: “Main”, “Category & codes”, “Package”, “Sizes & colors”, “Description”, “History”; product type as one switch",
        "Rentals redesigned: counters on top, cards with a colored status strip, step-by-step form, deposit as two tiles",
        "Rental return: condition as two tiles and the amount to give back shown right away",
        "Fixed: overdue rentals have a “Take back” button; the mouse wheel over a date no longer changes it; history shows the amount withheld",
        "“Often bought with this” suggestion redesigned; the selected size and color keep their highlight under the mouse",
    ];

    public static readonly string[] LatestTr =
    [
        "Ürün kartı sekmeli: «Temel», «Kategori ve kodlar», «Paket», «Bedenler ve renkler», «Açıklama», «Geçmiş»",
        "Kiralama yeni görünümde: üstte sayaçlar, renkli durum şeritli kartlar, adım adım form, depozito iki kutucuk",
        "Kiralama iadesi: durum iki kutucukla ve müşteriye ne kadar iade edileceği hemen görünür",
        "Düzeltildi: gecikmiş kiralamada «İadeyi al» düğmesi var; fare tekerleği tarihi değiştirmiyor; geçmişte kesilen tutar görünür",
        "«Bununla sık alınanlar» önerisi yeni görünümde; seçili beden ve renk fare altında vurgusunu kaybetmiyor",
    ];

    public static readonly string[] LatestUz =
    [
        "Mahsulot kartasi bo'limlar bilan: «Asosiy», «Kategoriya va kodlar», «Qadoq», «O'lcham va ranglar», «Tavsif», «Tarix»",
        "Prokat yangi ko'rinishda: tepada hisoblagichlar, rangli holat chizig'i bilan kartalar, bosqichma-bosqich forma, garov — ikki plitka",
        "Prokatni qaytarish: holati ikki plitka bilan va mijozga qancha qaytarish kerakligi darhol ko'rinadi",
        "Tuzatildi: muddati o'tgan prokatda «Qaytarishni qabul qilish» tugmasi bor; sichqoncha g'ildiragi sanani o'zgartirmaydi; tarixda ushlab qolingan summa ko'rinadi",
        "«Bu bilan ko'p olishadi» maslahati yangi ko'rinishda; tanlangan o'lcham va rang sichqoncha ostida belgisini yo'qotmaydi",
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
