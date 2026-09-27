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
        "История чеков для кассира (☰ → Работа): чеки смены, за сегодня и вчера, состав чека и «Печать копии»; в «⋮ Ещё» — «Печать последнего чека» и «Списание» товара",
        "«Отложить чек» оставляет чек вкладкой «Отложен ЧЧ:ММ» и сразу открывает новый; после оплаты лишняя пустая вкладка сама закрывается",
        "Клавиатура: стрелками двигается рамка по каталогу, Num + добавляет товар, Num − убавляет или убирает строку, Enter всегда оплачивает; в окне оплаты стрелки переключают способ оплаты",
        "Настройки → «Клавиши»: любое действие можно переназначить на свою клавишу, есть значения по умолчанию",
        "Работа без интернета: кассы и программа владельца обмениваются продажами по локальной сети или внутри одного компьютера — остаток и выручка видны сразу",
        "Программа владельца: новый раздел «Аналитика», ABC-анализ в каждом разделе по смыслу и полный ABC в «Сводке», «Финансы» без второго меню, разделы без повторяющихся заголовков, отчёт смены открывается мгновенно",
        "Выгрузки в Excel и Word — профессиональные отчёты: оглавление, итоги формулами, диаграммы, шапка на каждой странице, печать на A4",
        "Ценники и этикетки: при печати через драйвер принтера больше не обрезается штрих-код и не меняется размер; неверный код не обрывает печать",
        "Квадратные экраны: масштаб больше не увеличивается сам до 160% — касса всегда помещается на экран; крестик закрывает кассу без выхода из учётной записи",
        "История чеков показывает чек как на бумаге, на копии — «ПОВТОРНАЯ ПЕЧАТЬ» и дата; в Z-отчёте видны все скидки смены, отчёт смены у владельца открывается за секунду",
    ];

    public static readonly string[] LatestKy =
    [
        "Кассир үчүн чектердин тарыхы (☰ → Жумуш): сменанын, бүгүнкү жана кечээки чектер, чектин курамы жана «Көчүрмөсүн басып чыгаруу»; «⋮ Дагы» ичинде — «Акыркы чекти басып чыгаруу» жана товарды «Эсептен чыгаруу»",
        "«Чекти калтыруу» чекти «Калтырылган СС:ММ» өтмөгү катары калтырып, дароо жаңы чек ачат; төлөгөндөн кийин ашыкча бош өтмөк өзү жабылат",
        "Баскычтоп: жебелер менен каталогдо алкак жылат, Num + товар кошот, Num − санын азайтат же сапты алып салат, Enter дайыма төлөйт; төлөм терезесинде жебелер төлөм ыкмасын алмаштырат",
        "Жөндөөлөр → «Баскычтар»: каалаган аракетти өз баскычына кайра дайындаса болот, демейки маанилер бар",
        "Интернетсиз иштөө: кассалар жана ээсинин программасы сатууларды жергиликтүү тармак аркылуу же бир компьютердин ичинде алмашат — калдык жана түшкөн акча дароо көрүнөт",
        "Ээсинин программасы: жаңы «Талдоо» бөлүмү, ар бир бөлүмдө маанисине жараша ABC-талдоо жана «Жыйынтыкта» толук ABC, «Финансы» экинчи менюсуз, бөлүмдөр кайталанган аталышсыз, сменанын отчёту заматта ачылат",
        "Excel жана Word'го жүктөө — кесипкөй отчёттор: мазмуну, формула менен жыйынтыктар, диаграммалар, ар бир бетте баш сап, A4 басып чыгаруу",
        "Баа белгилери жана этикеткалар: принтердин драйвери аркылуу басканда штрих-код кесилбейт жана өлчөмү өзгөрбөйт; туура эмес код басып чыгарууну токтотпойт",
        "Чарчы экрандар: масштаб өзүнөн-өзү 160%га чоңойбойт — касса дайыма экранга батат; айкаш белги кассаны каттоо эсебинен чыкпай жабат",
        "Чектердин тарыхы чекти кагаздагыдай көрсөтөт, көчүрмөдө — «ПОВТОРНАЯ ПЕЧАТЬ» жана күнү; Z-отчётто сменанын бардык арзандатуулары көрүнөт, ээсинде сменанын отчёту бир секундда ачылат",
    ];

    public static readonly string[] LatestEn =
    [
        "Receipt history for cashiers (☰ → Work): receipts of the shift, today and yesterday, receipt contents and “Print copy”; “⋮ More” now has “Print last receipt” and product “Write-off”",
        "“Hold receipt” keeps the receipt as a “Held HH:MM” tab and opens a new one right away; after payment the extra empty tab closes by itself",
        "Keyboard: arrow keys move a frame through the catalog, Num + adds the product, Num − decreases or removes the line, Enter always pays; in the payment window the arrow keys switch the payment method",
        "Settings → “Keys”: any action can be reassigned to your own key, with defaults",
        "Working without internet: tills and the owner program exchange sales over the local network or within one computer — stock and revenue are visible right away",
        "Owner program: new “Analytics” section, ABC analysis in each section by meaning and a full ABC in the “Overview”, “Finance” without a second menu, sections without repeated titles, the shift report opens instantly",
        "Excel and Word exports are professional reports: table of contents, totals as formulas, charts, a header on every page, A4 printing",
        "Price tags and labels: printing through a printer driver no longer cuts off the barcode or changes the size; an invalid code no longer stops printing",
        "Square screens: the scale no longer jumps to 160% by itself — the till always fits the screen; the close button closes the till without signing out",
        "Receipt history shows the receipt as on paper, the copy says “REPRINT” with the date; the Z-report shows all discounts of the shift, the owner's shift report opens in a second",
    ];

    public static readonly string[] LatestTr =
    [
        "Kasiyer için fiş geçmişi (☰ → İş): vardiyanın, bugünün ve dünün fişleri, fiş içeriği ve «Kopya yazdır»; «⋮ Daha fazla» menüsünde «Son fişi yazdır» ve ürün «Düşümü»",
        "«Fişi beklet» fişi «Bekleyen SS:DD» sekmesi olarak bırakır ve hemen yeni fiş açar; ödemeden sonra fazladan boş sekme kendiliğinden kapanır",
        "Klavye: ok tuşlarıyla katalogda çerçeve hareket eder, Num + ürünü ekler, Num − miktarı azaltır veya satırı kaldırır, Enter her zaman öder; ödeme penceresinde ok tuşları ödeme yöntemini değiştirir",
        "Ayarlar → «Tuşlar»: her eylem kendi tuşunuza yeniden atanabilir, varsayılan değerler mevcut",
        "İnternetsiz çalışma: kasalar ve sahip programı satışları yerel ağ üzerinden veya tek bilgisayar içinde paylaşır — stok ve ciro hemen görünür",
        "Sahip programı: yeni «Analiz» bölümü, her bölümde anlamına göre ABC analizi ve «Özet»te tam ABC, ikinci menüsüz «Finans», tekrarlanan başlıklar olmadan bölümler, vardiya raporu anında açılır",
        "Excel ve Word dışa aktarımları profesyonel raporlar: içindekiler, formüllü toplamlar, grafikler, her sayfada başlık, A4 yazdırma",
        "Etiketler: yazıcı sürücüsüyle yazdırırken barkod artık kesilmiyor ve boyut değişmiyor; hatalı kod yazdırmayı durdurmuyor",
        "Kare ekranlar: ölçek artık kendiliğinden %160'a çıkmıyor — kasa her zaman ekrana sığar; kapatma düğmesi oturumu kapatmadan kasayı kapatır",
        "Fiş geçmişi fişi kâğıttaki gibi gösterir, kopyada «ПОВТОРНАЯ ПЕЧАТЬ» ve tarih; Z raporu vardiyanın tüm indirimlerini gösterir, sahibin vardiya raporu bir saniyede açılır",
    ];

    public static readonly string[] LatestUz =
    [
        "Kassir uchun cheklar tarixi (☰ → Ish): smenadagi, bugungi va kechagi cheklar, chek tarkibi va «Nusxani chop etish»; «⋮ Yana» menyusida «Oxirgi chekni chop etish» va mahsulotni «Hisobdan chiqarish»",
        "«Chekni kutishga qo'yish» chekni «Kutishda SS:DD» yorlig'i sifatida qoldiradi va darhol yangi chek ochadi; to'lovdan keyin ortiqcha bo'sh yorliq o'zi yopiladi",
        "Klaviatura: strelkalar bilan katalogda ramka suriladi, Num + mahsulot qo'shadi, Num − miqdorni kamaytiradi yoki qatorni olib tashlaydi, Enter doim to'laydi; to'lov oynasida strelkalar to'lov usulini almashtiradi",
        "Sozlamalar → «Tugmalar»: istalgan amalni o'z tugmangizga qayta tayinlash mumkin, standart qiymatlar bor",
        "Internetsiz ishlash: kassalar va ega dasturi sotuvlarni mahalliy tarmoq orqali yoki bitta kompyuter ichida almashadi — qoldiq va tushum darhol ko'rinadi",
        "Ega dasturi: yangi «Tahlil» bo'limi, har bir bo'limda ma'nosiga ko'ra ABC tahlili va «Umumiy»da to'liq ABC, ikkinchi menyusiz «Moliya», takrorlanuvchi sarlavhalarsiz bo'limlar, smena hisoboti bir zumda ochiladi",
        "Excel va Word'ga eksport — professional hisobotlar: mundarija, formulali jamlar, diagrammalar, har sahifada sarlavha, A4 chop etish",
        "Narx yorliqlari va etiketkalar: printer drayveri orqali chop etilganda shtrix-kod endi kesilmaydi va o'lchami o'zgarmaydi; noto'g'ri kod chop etishni to'xtatmaydi",
        "Kvadrat ekranlar: masshtab endi o'z-o'zidan 160% gacha kattalashmaydi — kassa doim ekranga sig'adi; yopish tugmasi hisobdan chiqmasdan kassani yopadi",
        "Cheklar tarixi chekni qog'ozdagidek ko'rsatadi, nusxada — «ПОВТОРНАЯ ПЕЧАТЬ» va sana; Z-hisobotda smenaning barcha chegirmalari ko'rinadi, egadagi smena hisoboti bir soniyada ochiladi",
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
