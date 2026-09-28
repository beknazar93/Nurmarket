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
        "Поиск весов в сети: касса находит адреса весов (Настройки → Весы → «Поиск весов в сети…»)",
        "Окна настроек для весов Rongta и TM-30F; для Штрих-ПРИНТ — вкладки «Валюта» (вместо «РУБ» — «СОМ» на этикетке, знак валюты) и «Макет этикетки»",
        "Новые весы TM-30F (JHScale), поиск товаров в окне «Весы», адрес и марка весов больше не сбрасываются",
        "Количество в строке чека вводится с клавиатуры во всех видах кассы",
        "F1–F12: другая клавиша сразу меняет окно товаров; быстрые товары можно отключить (Настройки → Экран)",
        "Код тестера: ясная отметка «принят / неверный» и сразу проверка обновлений; значки в меню не съезжают",
    ];

    public static readonly string[] LatestKy =
    [
        "Тармактан таразаларды издөө: касса таразалардын даректерин табат (Жөндөөлөр → Таразалар → «Тармактан таразаларды издөө…»)",
        "Rongta жана TM-30F таразалары үчүн жөндөө терезелери; Штрих-ПРИНТ үчүн «Валюта» (этикеткада «РУБ» ордуна «СОМ», валюта белгиси) жана «Этикетканын макети» өтмөктөрү",
        "Жаңы TM-30F (JHScale) таразалары, «Таразалар» терезесинде товар издөө, таразанын дареги жана маркасы эми өчпөйт",
        "Чектин сабындагы санды бардык касса көрүнүштөрүндө клавиатурадан киргизсе болот",
        "F1–F12: башка баскыч товарлар терезесин дароо алмаштырат; ыкчам товарларды өчүрсө болот (Жөндөөлөр → Экран)",
        "Тестер коду: «кабыл алынды / туура эмес» деген так белги жана жаңыртууларды дароо текшерүү; менюдагы белгилер жылбайт",
    ];

    public static readonly string[] LatestEn =
    [
        "Scale search on the network: the till finds scale addresses (Settings → Scales → “Find scales on the network…”)",
        "Settings windows for Rongta and TM-30F scales; Shtrikh-PRINT gets “Currency” (“SOM” instead of “RUB” on the label, currency sign) and “Label layout” tabs",
        "New TM-30F (JHScale) scales, product search in the “Scales” window, the scale address and brand are no longer reset",
        "The quantity in a receipt line can be typed in every till layout",
        "F1–F12: another key switches the products window at once; quick products can be turned off (Settings → Screen)",
        "Tester code: a clear “accepted / wrong” mark and an immediate update check; menu icons no longer shift",
    ];

    public static readonly string[] LatestTr =
    [
        "Ağda terazi arama: kasa terazilerin adreslerini bulur (Ayarlar → Teraziler → «Ağda terazi ara…»)",
        "Rongta ve TM-30F teraziler için ayar pencereleri; Shtrikh-PRINT için «Para birimi» (etikette «RUB» yerine «SOM», para işareti) ve «Etiket düzeni» sekmeleri",
        "Yeni TM-30F (JHScale) teraziler, «Teraziler» penceresinde ürün arama, terazi adresi ve markası artık sıfırlanmıyor",
        "Fiş satırındaki miktar tüm kasa görünümlerinde klavyeden girilebilir",
        "F1–F12: başka bir tuş ürün penceresini hemen değiştirir; hızlı ürünler kapatılabilir (Ayarlar → Ekran)",
        "Test kodu: net «kabul edildi / yanlış» işareti ve hemen güncelleme kontrolü; menü simgeleri artık kaymıyor",
    ];

    public static readonly string[] LatestUz =
    [
        "Tarmoqda tarozilarni qidirish: kassa tarozilar manzilini topadi (Sozlamalar → Tarozilar → «Tarmoqda tarozilarni qidirish…»)",
        "Rongta va TM-30F tarozilari uchun sozlash oynalari; Shtrix-PRINT uchun «Valyuta» (yorliqda «RUB» o'rniga «SOM», valyuta belgisi) va «Yorliq maketi» yorliqlari",
        "Yangi TM-30F (JHScale) tarozilari, «Tarozilar» oynasida mahsulot qidirish, tarozi manzili va markasi endi o'chib ketmaydi",
        "Chek qatoridagi miqdorni barcha kassa ko'rinishlarida klaviaturadan kiritish mumkin",
        "F1–F12: boshqa tugma mahsulotlar oynasini darhol almashtiradi; tezkor mahsulotlarni o'chirish mumkin (Sozlamalar → Ekran)",
        "Tester kodi: aniq «qabul qilindi / noto'g'ri» belgisi va darhol yangilanishni tekshirish; menyu belgilari endi siljimaydi",
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
