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
        "Экранная клавиатура: набор больше не пропадает — кнопка не забирает курсор из поля, а если поле не выбрано, курсор ставится в поиск товара; свёрнутая клавиатура поднимается наверх",
        "«Просмотр смены»: кнопки «Печать» и «Закрыть» всегда видны — длинный список товаров прокручивается, окно стало компактнее",
        "Программа владельца, «Сводка»: новый период «Спец. дата» — любые даты, сравнение с таким же периодом до них",
    ];

    public static readonly string[] LatestKy =
    [
        "Экрандагы баскычтоп: терилген текст жоголбойт — баскыч курсорду талаадан албайт, талаа тандалбаса курсор товар издөөгө коюлат; жыйылган баскычтоп үстүнө чыгат",
        "«Сменаны көрүү»: «Басып чыгаруу» жана «Жабуу» баскычтары дайыма көрүнөт — товарлардын узун тизмеси сыдырылат, терезе жыйнактуу болду",
        "Ээсинин программасы, «Жыйынтык»: жаңы мезгил «Башка дата» — каалаган күндөр, алардан мурунку ушундай мезгил менен салыштыруу",
    ];

    public static readonly string[] LatestEn =
    [
        "On-screen keyboard: typing is no longer lost — the button keeps the cursor in the field, and if no field is selected the cursor goes to product search; a minimized keyboard is brought to the front",
        "“View shift”: the “Print” and “Close” buttons are always visible — a long product list scrolls, the window is more compact",
        "Owner app, “Overview”: new “Custom dates” period — any dates, compared with the same-length period before them",
    ];

    public static readonly string[] LatestTr =
    [
        "Ekran klavyesi: yazılanlar artık kaybolmuyor — düğme imleci alandan almıyor, alan seçili değilse imleç ürün aramasına gidiyor; küçültülmüş klavye öne getiriliyor",
        "«Vardiyayı görüntüle»: «Yazdır» ve «Kapat» düğmeleri her zaman görünür — uzun ürün listesi kaydırılır, pencere daha derli toplu",
        "Sahip programı, «Özet»: yeni «Özel tarih» dönemi — istenen tarihler, öncesindeki aynı uzunlukta dönemle karşılaştırma",
    ];

    public static readonly string[] LatestUz =
    [
        "Ekran klaviaturasi: yozilganlar endi yo‘qolmaydi — tugma kursorni maydondan olmaydi, maydon tanlanmagan bo‘lsa kursor tovar qidiruviga qo‘yiladi; yig‘ilgan klaviatura oldinga chiqariladi",
        "«Smenani ko‘rish»: «Chop etish» va «Yopish» tugmalari doim ko‘rinadi — uzun tovarlar ro‘yxati aylantiriladi, oyna ixchamroq",
        "Egasining dasturi, «Umumiy ko‘rinish»: yangi «Boshqa sana» davri — istalgan sanalar, ulardan oldingi xuddi shunday davr bilan solishtirish",
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
