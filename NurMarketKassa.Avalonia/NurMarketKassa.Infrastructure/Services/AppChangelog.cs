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
        "Шесть видов кассы на выбор (Настройки → Экран → «Вид кассы»): Классика, Табличная, Карточки, Минимал, Профи и 1С — переключаются сразу, без перезапуска",
        "Табличная: чек таблицей, поле штрихкода сверху, кнопки «Наличные» и «Безналичные» сразу открывают оплату нужным способом",
        "Редактор своих тем (Маркетплейс → Темы → «Редактор тем»): 9 цветов для светлого и тёмного варианта, скругление, шрифт, живой предпросмотр, экспорт и импорт",
        "Обновлённый основной вид: единые значки, выбранная вкладка чека в цвете темы, крупный итог справа; клавиатура и сканер работают во всех видах",
        "Баннер «Доступно обновление» показывает список изменений обычным текстом на языке программы",
    ];

    public static readonly string[] LatestKy =
    [
        "Кассанын алты көрүнүшү (Жөндөөлөр → Экран → «Кассанын көрүнүшү»): Классика, Таблица, Карточкалар, Минимал, Профи жана 1С — кайра ачпастан дароо алмашат",
        "Таблица: чек таблица менен, штрихкод талаасы өйдөдө, «Накталай» жана «Накталай эмес» баскычтары төлөмдү дароо керектүү ыкма менен ачат",
        "Өз темаларыңыздын редактору (Маркетплейс → Темалар → «Темалардын редактору»): жарык жана караңгы вариант үчүн 9 түс, бурчтардын тегеректиги, шрифт, түз алдын ала көрүү, экспорт жана импорт",
        "Жаңыланган негизги көрүнүш: бирдиктүү белгилер, тандалган чек өтмөгү теманын түсүндө, чоң жыйынтык оң жакта; баскычтоп жана сканер бардык көрүнүштөрдө иштейт",
        "«Жаңыртуу бар» тилкеси өзгөрүүлөрдүн тизмесин программанын тилинде жөнөкөй текст менен көрсөтөт",
    ];

    public static readonly string[] LatestEn =
    [
        "Six till layouts to choose from (Settings → Screen → “Till layout”): Classic, Table, Cards, Minimal, Pro and 1C — switch instantly, no restart",
        "Table: the receipt as a table, a barcode field on top, “Cash” and “Card” buttons open payment with that method right away",
        "Your own theme editor (Marketplace → Themes → “Theme editor”): 9 colors for the light and dark variant, corner radius, font size, live preview, export and import",
        "Refreshed main layout: consistent icons, the selected receipt tab in the theme color, a large total on the right; keyboard and scanner work in every layout",
        "The “Update available” banner shows the list of changes as plain text in the program's language",
    ];

    public static readonly string[] LatestTr =
    [
        "Seçilebilir altı kasa görünümü (Ayarlar → Ekran → «Kasa görünümü»): Klasik, Tablo, Kartlar, Minimal, Pro ve 1C — yeniden başlatmadan anında değişir",
        "Tablo: fiş tablo halinde, üstte barkod alanı, «Nakit» ve «Kart» düğmeleri ödemeyi hemen o yöntemle açar",
        "Kendi tema düzenleyiciniz (Pazaryeri → Temalar → «Tema düzenleyici»): açık ve koyu varyant için 9 renk, köşe yuvarlaklığı, yazı boyutu, canlı önizleme, dışa ve içe aktarma",
        "Yenilenen ana görünüm: tutarlı simgeler, seçili fiş sekmesi tema renginde, sağda büyük toplam; klavye ve tarayıcı tüm görünümlerde çalışır",
        "«Güncelleme mevcut» şeridi değişiklik listesini program dilinde düz metin olarak gösterir",
    ];

    public static readonly string[] LatestUz =
    [
        "Kassaning oltita ko'rinishi (Sozlamalar → Ekran → «Kassa ko'rinishi»): Klassika, Jadval, Kartochkalar, Minimal, Pro va 1C — qayta ishga tushirmasdan darhol almashadi",
        "Jadval: chek jadval ko'rinishida, tepada shtrix-kod maydoni, «Naqd» va «Naqdsiz» tugmalari to'lovni darhol kerakli usulda ochadi",
        "O'z mavzularingiz muharriri (Marketpleys → Mavzular → «Mavzular muharriri»): yorug' va qorong'i variant uchun 9 rang, burchak yumaloqligi, shrift, jonli oldindan ko'rish, eksport va import",
        "Yangilangan asosiy ko'rinish: yagona belgilar, tanlangan chek yorlig'i mavzu rangida, o'ngda katta jami; klaviatura va skaner barcha ko'rinishlarda ishlaydi",
        "«Yangilanish mavjud» tasmasi o'zgarishlar ro'yxatini dastur tilida oddiy matn bilan ko'rsatadi",
    ];

    /// <summary>Список на языке интерфейса (2026-09-07).</summary>
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
