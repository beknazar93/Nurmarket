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
        "Весы Rongta напрямую по сети, без программы RLS1000: товары, цены, названия (кириллица) и PLU уходят прямо в весы; проверено на весах Rongta RLS1100",
        "PLU весовых товаров совпадают с сайтом: товарам без PLU касса назначает его на сайте сама; PLU можно поменять в окне «Весы» — изменится и на сайте",
        "«Кнопки весов…»: нажмите кнопку весов и выберите товар; «Лист кнопок» — печать на A4 и сохранение в Word",
        "Окно «Весы»: только весовые товары, фильтр «Показать» (весовые / все в кг / без PLU / с кнопкой); «Код в ШК», который открыл бы чужой товар, касса заменяет сама, а не останавливает отправку",
        "«Инструкция подключения» у каждых весов (Настройки → Весы): пошаговый план с анимациями для Rongta, Штрих-ПРИНТ, TM-30F и AI весов",
        "Этикетки весов, отправленных из программы владельца, находятся и в кассе на том же компьютере",
        "Товары больше не добавляются в чек сами после входа и после продажи: Enter от сканера не нажимает плитки и кнопки",
        "Окна не выходят за край маленьких и квадратных экранов",
        "Весы Штрих-ПРИНТ: у товара постоянный PLU, клавиши весов идут за товаром при смене номера, «Обновить с сервера» загружает каталог заново",
        "Сервер не отвечает — через 2 секунды продажа уходит в очередь и досылается сама; доп. штрих-код варианта — отдельной строкой; «+ Новый чек» и скан не создают лишний чек; «Напечатать чек» запоминается",
        "Программа владельца: «Заказы с сайта» и «Настройки сайта»",
    ];

    public static readonly string[] LatestKy =
    [
        "Rongta таразасы тармак аркылуу түз, RLS1000 программасысыз: товарлар, баалар, аталыштар (кирилл) жана PLU түз таразага кетет; Rongta RLS1100 таразасында текшерилди",
        "Салмактуу товарлардын PLU'су сайт менен дал келет: PLU'су жок товарларга касса аны сайтта өзү берет; PLU'ну «Таразалар» терезесинде өзгөртсө болот — сайтта да өзгөрөт",
        "«Тараза баскычтары…»: таразанын баскычын басып товарды тандаңыз; «Баскычтар барагы» — A4 басып чыгаруу жана Word'го сактоо",
        "«Таразалар» терезеси: салмактуу товарлар гана, «Көрсөтүү» чыпкасы (салмактуулар / кг'дагылар / PLU'суз / баскычы бар); башка товарды ача турган «ШКдагы кодду» касса өзү алмаштырат, жөнөтүүнү токтотпойт",
        "Ар бир таразада «Туташтыруу нускамасы» (Жөндөөлөр → Таразалар): Rongta, Штрих-ПРИНТ, TM-30F жана AI тараза үчүн анимациялуу кадамдар",
        "Ээсинин программасынан жөнөтүлгөн таразалардын этикеткалары ошол эле компьютердеги кассада да табылат",
        "Кирүүдөн жана сатуудан кийин чекке товарлар өзүнөн-өзү кошулбайт: сканердин Enter'и плиткаларды жана баскычтарды баспайт",
        "Терезелер кичине жана чарчы экрандардын четинен чыкпайт",
        "Штрих-ПРИНТ таразасы: товардын туруктуу PLU'су бар, номер өзгөргөндө тараза баскычтары товар менен кошо көчөт, «Серверден жаңыртуу» каталогду кайра жүктөйт",
        "Сервер жооп бербесе — 2 секунддан кийин сатуу кезекке кетет жана өзү жөнөтүлөт; варианттын кошумча штрих-коду — өзүнчө сап; «+ Жаңы чек» жана скан ашыкча чек түзбөйт; «Чекти басып чыгаруу» эстелет",
        "Ээсинин программасы: «Сайттан заказдар» жана «Сайттын жөндөөлөрү»",
    ];

    public static readonly string[] LatestEn =
    [
        "Rongta scales directly over the network, without RLS1000: goods, prices, names (Cyrillic) and PLUs go straight to the scale; checked on a Rongta RLS1100",
        "Weighed-goods PLUs match the website: the till assigns a PLU on the website to goods without one; a PLU changed in the “Scales” window changes on the website too",
        "“Scale keys…”: press a scale key and choose a product; “Key sheet” — print on A4 or save to Word",
        "“Scales” window: weighed goods only, a “Show” filter (weighed / all in kg / without PLU / with a key); a “Barcode code” that would open another product is replaced by the till instead of stopping the upload",
        "“Connection guide” for every scale (Settings → Scales): step-by-step plan with animations for Rongta, Shtrih-PRINT, TM-30F and AI scales",
        "Labels of a scale loaded from the owner app are found by the till on the same computer",
        "Goods no longer get added to the receipt by themselves after sign-in or after a sale: the scanner’s Enter no longer presses tiles and buttons",
        "Windows no longer go past the edge of small and square screens",
        "Shtrih-PRINT scales: a product keeps its PLU, scale keys follow the product when its number changes, “Refresh from server” reloads the catalog",
        "Server not answering — after 2 seconds the sale goes to the queue and is sent later by itself; a variant’s extra barcode is a separate line; “+ New receipt” and a scan no longer create an extra receipt; “Print receipt” is remembered",
        "Owner app: “Website orders” and “Website settings”",
    ];

    public static readonly string[] LatestTr =
    [
        "Rongta tartılar RLS1000 olmadan doğrudan ağ üzerinden: ürünler, fiyatlar, adlar (Kiril) ve PLU'lar doğrudan tartıya gider; Rongta RLS1100'de denendi",
        "Tartılı ürünlerin PLU'ları siteyle aynı: PLU'su olmayan ürünlere kasa sitede PLU verir; «Tartı» penceresinde değiştirilen PLU sitede de değişir",
        "«Tartı tuşları…»: tartı tuşuna basıp ürün seçin; «Tuş listesi» — A4 yazdırma ve Word'e kaydetme",
        "«Tartı» penceresi: yalnızca tartılı ürünler, «Göster» filtresi (tartılı / kg'daki tümü / PLU'suz / tuşlu); başka ürünü açacak «Barkod kodu»nu kasa gönderimi durdurmadan kendisi değiştirir",
        "Her tartıda «Bağlantı kılavuzu» (Ayarlar → Tartılar): Rongta, Shtrih-PRINT, TM-30F ve AI tartı için animasyonlu adımlar",
        "Sahip programından yüklenen tartıların etiketleri aynı bilgisayardaki kasada da bulunur",
        "Girişten ve satıştan sonra fişe kendiliğinden ürün eklenmiyor: tarayıcının Enter'ı karo ve düğmelere basmıyor",
        "Pencereler küçük ve kare ekranların kenarından taşmıyor",
        "Shtrih-PRINT tartılar: ürünün sabit PLU'su var, numara değişince tartı tuşları ürünü takip eder, «Sunucudan yenile» kataloğu yeniden yükler",
        "Sunucu yanıt vermezse 2 saniye sonra satış kuyruğa gider ve kendiliğinden gönderilir; varyantın ek barkodu ayrı satır; «+ Yeni fiş» ve okutma fazladan fiş açmıyor; «Fişi yazdır» hatırlanıyor",
        "Sahip programı: «Site siparişleri» ve «Site ayarları»",
    ];

    public static readonly string[] LatestUz =
    [
        "Rongta tarozilari RLS1000 dasturisiz to‘g‘ridan-to‘g‘ri tarmoq orqali: tovarlar, narxlar, nomlar (kirill) va PLU to‘g‘ridan-to‘g‘ri taroziga ketadi; Rongta RLS1100 da tekshirildi",
        "Vaznli tovarlar PLU'si sayt bilan bir xil: PLU'si yo‘q tovarlarga kassa saytda PLU beradi; «Tarozi» oynasida o‘zgartirilgan PLU saytda ham o‘zgaradi",
        "«Tarozi tugmalari…»: tarozi tugmasini bosing va tovarni tanlang; «Tugmalar varag‘i» — A4 chop etish va Word'ga saqlash",
        "«Tarozi» oynasi: faqat vaznli tovarlar, «Ko‘rsatish» filtri (vaznli / kg dagi hammasi / PLU'siz / tugmali); boshqa tovarni ochadigan «ShKdagi kod»ni kassa yuborishni to‘xtatmasdan o‘zi almashtiradi",
        "Har bir tarozida «Ulanish yo‘riqnomasi» (Sozlamalar → Tarozilar): Rongta, Shtrix-PRINT, TM-30F va AI tarozi uchun animatsiyali qadamlar",
        "Egasi dasturidan yuklangan tarozi yorliqlari shu kompyuterdagi kassada ham topiladi",
        "Kirishdan va sotuvdan keyin chekka tovarlar o‘z-o‘zidan qo‘shilmaydi: skanerning Enter'i plitkalar va tugmalarni bosmaydi",
        "Oynalar kichik va kvadrat ekranlar chetidan chiqmaydi",
        "Shtrix-PRINT tarozilari: tovarning doimiy PLU'si bor, raqam o‘zgarsa tarozi tugmalari tovar bilan birga ko‘chadi, «Serverdan yangilash» katalogni qayta yuklaydi",
        "Server javob bermasa — 2 soniyadan keyin sotuv navbatga ketadi va o‘zi yuboriladi; variantning qo‘shimcha shtrix-kodi — alohida qator; «+ Yangi chek» va skan ortiqcha chek ochmaydi; «Chekni chop etish» eslab qolinadi",
        "Egasi dasturi: «Saytdan buyurtmalar» va «Sayt sozlamalari»",
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
