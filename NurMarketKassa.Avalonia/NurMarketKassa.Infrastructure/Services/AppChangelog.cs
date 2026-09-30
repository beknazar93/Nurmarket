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
        "Программа владельца на новых компьютерах: ярлык «NurMarket Владелец» создаётся надёжно, без Windows Script Host; если при установке не получилось — касса создаст его при запуске. В релизе есть и отдельный установщик программы владельца",
        "Финансы → «Возвраты»: настоящие возвраты — полные и частичные, с номером чека, составом и точной суммой; двойной щелчок открывает чек (раньше там был журнал удалений из корзины, частичные возвраты не показывались)",
        "Плитки «Финансов» (выручка, возвраты, наличные, безнал, средний чек, чеки) нажимаются — подробно, как в Z-отчёте: чеки, товары, итог",
        "Новая тема «Кыргыз»: красный и золото флага, кыргызские орнаменты (кочкор мүйүз, ит куйрук, тумар, суу, бадам) в окнах, корзине, каталоге, на кнопках и экране покупателя",
        "Голосовое управление: слово «касса» срабатывает только в начале фразы — разговоры у кассы больше не добавляют товары в чек",
        "Настройки на узком экране: строки в несколько колонок встают столбиком; окно «Весы»: колонки и фильтры не обрезаются на всех языках",
    ];

    public static readonly string[] LatestKy =
    [
        "Ээсинин программасы жаңы компьютерлерде: «NurMarket Владелец» энбелгиси ишенимдүү түзүлөт; орнотууда болбосо — касса ишке кирерде түзөт. Релизде ээсинин программасынын өзүнчө орнотуучусу да бар",
        "Каржы → «Кайтаруулар»: чыныгы кайтаруулар — толук жана жарым-жартылай, чектин номери, курамы жана так суммасы менен; эки жолу басса чек ачылат",
        "«Каржынын» плиткалары басылат — Z-отчёттогудай толук: чектер, товарлар, жыйынтык",
        "Жаңы «Кыргыз» темасы: желектин кызыл жана алтын түсү, кыргыз оюулары (кочкор мүйүз, ит куйрук, тумар, суу, бадам) терезелерде, себетте, каталогдо, баскычтарда жана сатып алуучунун экранында",
        "Үн менен башкаруу: «касса» сөзү фразанын башында гана иштейт — кассанын жанындагы сүйлөшүү чекке товар кошпойт",
        "Тар экрандагы жөндөөлөр мамыча болуп тизилет; «Таразалар» терезесинде тилкелер жана чыпкалар бардык тилдерде кесилбейт",
    ];

    public static readonly string[] LatestEn =
    [
        "Owner app on new computers: the “NurMarket Владелец” shortcut is created reliably, without Windows Script Host; if it fails during installation, the till creates it at startup. The release also has a separate owner app installer",
        "Finance → “Returns”: real returns — full and partial, with receipt number, items and exact amount; double-click opens the receipt",
        "Finance tiles (revenue, returns, cash, cashless, average receipt, receipts) are clickable — detailed like the Z-report: receipts, products, total",
        "New “Kyrgyz” theme: the red and gold of the flag, Kyrgyz ornaments in windows, cart, catalog, buttons and on the customer display",
        "Voice control: the word “касса” works only at the start of a phrase — talk near the till no longer adds goods",
        "Settings on a narrow screen stack vertically; in the “Scales” window columns and filters are not cut off in any language",
    ];

    public static readonly string[] LatestTr =
    [
        "Yeni bilgisayarlarda sahip programı: «NurMarket Владелец» kısayolu Windows Script Host olmadan güvenilir şekilde oluşturulur; kurulumda olmazsa kasa açılışta oluşturur. Sürümde ayrıca ayrı bir sahip programı yükleyicisi var",
        "Finans → «İadeler»: gerçek iadeler — tam ve kısmi, fiş numarası, içerik ve kesin tutarla; çift tıklama fişi açar",
        "Finans kutucukları (ciro, iadeler, nakit, nakitsiz, ortalama fiş, fişler) tıklanabilir — Z raporundaki gibi ayrıntılı",
        "Yeni «Kırgız» teması: bayrağın kırmızısı ve altını, pencerelerde, sepette, katalogda, düğmelerde ve müşteri ekranında Kırgız süslemeleri",
        "Sesli kontrol: «касса» kelimesi yalnızca cümlenin başında çalışır — kasa yanındaki konuşmalar artık ürün eklemiyor",
        "Dar ekranda ayarlar alt alta dizilir; «Teraziler» penceresinde sütunlar ve filtreler hiçbir dilde kesilmez",
    ];

    public static readonly string[] LatestUz =
    [
        "Yangi kompyuterlarda egasining dasturi: «NurMarket Владелец» yorlig‘i Windows Script Host’siz ishonchli yaratiladi; o‘rnatishda bo‘lmasa kassa ishga tushganda yaratadi. Relizda egasi dasturining alohida o‘rnatuvchisi ham bor",
        "Moliya → «Qaytarishlar»: haqiqiy qaytarishlar — to‘liq va qisman, chek raqami, tarkibi va aniq summasi bilan; ikki marta bosish chekni ochadi",
        "«Moliya» plitkalari bosiladi — Z-hisobotdagidek batafsil: cheklar, mahsulotlar, jami",
        "Yangi «Qirg‘iz» mavzusi: bayroqning qizil va oltin ranglari, oynalarda, savatda, katalogda, tugmalarda va xaridor ekranida qirg‘iz naqshlari",
        "Ovozli boshqaruv: «касса» so‘zi faqat ibora boshida ishlaydi — kassa yonidagi suhbatlar endi tovar qo‘shmaydi",
        "Tor ekranda sozlamalar ustma-ust joylashadi; «Tarozilar» oynasida ustunlar va filtrlar hech bir tilda kesilmaydi",
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
