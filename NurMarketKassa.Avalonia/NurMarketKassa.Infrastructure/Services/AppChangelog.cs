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
        "Весы TM-30F и другие: сумма из этикетки больше не читается как вес — «Алма» 0,220 кг × 60 = 13,20 было в чеке 1,320 кг = 79,20, теперь 0,220 кг = 13,20",
        "При первом скане этикетки с префиксом 21–24 или 26–29 касса один раз спросит: «на этикетке СУММА или ВЕС?» и покажет оба варианта в деньгах — ответ запоминается",
        "Оплата быстрее: продажа проводится одним запросом к серверу (0,3–0,4 с вместо 1,5–1,9 с), без двойных чеков при повторе",
        "Смешанная оплата уходит на сервер с разбивкой на наличные и безнал",
        "Отчёт смены и Z-отчёт — с сервера (смешанная раздельно, возвраты, внесения); внесение входит в сумму смены; постоянный номер чека, как на сайте",
        "«Оплата долга»: погасить одной суммой — долг из десятков взносов за 1 секунду вместо 15",
        "Весы TM-30F (Dahua): прямая отправка товаров на весы из кассы, настройки и формат штрихкода",
        "Поиск весов в сети проверяет и чужие подсети (заводские адреса весов); Rongta: код товара в штрихкоде больше не нулевой",
        "Исправлено: первый чек после «Открыть смену» уходил офлайн; при сбое сервера (502) понятное сообщение и без двойной продажи",
    ];

    public static readonly string[] LatestKy =
    [
        "TM-30F жана башка таразалар: этикеткадагы сумма салмак катары окулбайт — «Алма» 0,220 кг × 60 = 13,20 чекте 1,320 кг = 79,20 болчу, эми 0,220 кг = 13,20",
        "21–24 же 26–29 префикстүү этикетканы биринчи сканерлегенде касса бир жолу сурайт: «этикеткада СУММА же САЛМАК?» жана эки вариантты акча менен көрсөтөт — жооп эсте калат",
        "Төлөм тезирээк: сатуу серверге бир суроо менен өтөт (1,5–1,9 с ордуна 0,3–0,4 с), кайталаганда эки чек түзүлбөйт",
        "Аралаш төлөм серверге накталай жана накталай эмес болуп бөлүнүп кетет",
        "Сменанын отчёту жана Z-отчёт серверден; салуу сменанын суммасына кирет; чектин туруктуу номери сайттагыдай",
        "«Карызды төлөө»: бир сумма менен жабуу — ондогон төлөмдөн турган карыз 15 секунданын ордуна 1 секундада",
        "TM-30F (Dahua) таразалары: товарларды кассадан таразага түз жөнөтүү, жөндөөлөр жана штрих-код форматы",
        "Тармактан таразаларды издөө башка подсеттерди да текшерет; Rongta: штрих-коддогу товардын коду эми нөл эмес",
        "Оңдолду: «Сменаны ачуудан» кийинки биринчи чек офлайн кетчү; сервер иштебей калса (502) түшүнүктүү билдирүү жана кош сатуу жок",
    ];

    public static readonly string[] LatestEn =
    [
        "TM-30F and other scales: the total from the label is no longer read as weight — “Alma” 0.220 kg × 60 = 13.20 was 1.320 kg = 79.20 in the receipt, now 0.220 kg = 13.20",
        "On the first scan of a label with prefix 21–24 or 26–29 the till asks once: “TOTAL or WEIGHT on the label?” and shows both options in money — the answer is remembered",
        "Faster payment: a sale is made with one server request (0.3–0.4 s instead of 1.5–1.9 s), no duplicate receipts on retry",
        "Mixed payment is sent to the server split into cash and card",
        "Shift and Z reports from the server; cash deposits count in the shift total; permanent receipt number as on the website",
        "“Debt payment”: pay with one sum — a debt of dozens of instalments in 1 second instead of 15",
        "TM-30F (Dahua) scales: direct product upload from the till, settings and barcode format",
        "Scale search on the network also checks other subnets (factory scale addresses); Rongta: product code in the barcode is no longer zero",
        "Fixed: the first receipt after “Open shift” went offline; a server failure (502) shows a clear message and makes no duplicate sale",
    ];

    public static readonly string[] LatestTr =
    [
        "TM-30F ve diğer teraziler: etiketteki tutar artık ağırlık olarak okunmuyor — «Alma» 0,220 kg × 60 = 13,20 fişte 1,320 kg = 79,20 idi, şimdi 0,220 kg = 13,20",
        "21–24 veya 26–29 önekli etiketin ilk taramasında kasa bir kez sorar: «etikette TUTAR mı AĞIRLIK mı?» ve iki seçeneği parayla gösterir — cevap hatırlanır",
        "Daha hızlı ödeme: satış sunucuya tek istekle yapılır (1,5–1,9 sn yerine 0,3–0,4 sn), tekrarda çift fiş olmaz",
        "Karışık ödeme sunucuya nakit ve kart olarak ayrılmış gider",
        "Vardiya ve Z raporu sunucudan; para girişi vardiya toplamına dahil; sitedeki gibi kalıcı fiş numarası",
        "«Borç ödeme»: tek tutarla kapat — onlarca taksitlik borç 15 saniye yerine 1 saniyede",
        "TM-30F (Dahua) teraziler: ürünleri kasadan teraziye doğrudan gönderme, ayarlar ve barkod biçimi",
        "Ağda terazi arama başka alt ağları da kontrol eder; Rongta: barkoddaki ürün kodu artık sıfır değil",
        "Düzeltildi: «Vardiya aç» sonrası ilk fiş çevrimdışına gidiyordu; sunucu hatasında (502) anlaşılır mesaj ve çift satış yok",
    ];

    public static readonly string[] LatestUz =
    [
        "TM-30F va boshqa tarozilar: yorliqdagi summa endi vazn deb o'qilmaydi — «Alma» 0,220 kg × 60 = 13,20 chekda 1,320 kg = 79,20 edi, endi 0,220 kg = 13,20",
        "21–24 yoki 26–29 prefiksli yorliqni birinchi skanerlashda kassa bir marta so'raydi: «yorliqda SUMMA yoki VAZN?» va ikkala variantni pulda ko'rsatadi — javob eslab qolinadi",
        "Tezroq to'lov: savdo serverga bitta so'rov bilan o'tadi (1,5–1,9 s o'rniga 0,3–0,4 s), qaytarilganda ikki chek bo'lmaydi",
        "Aralash to'lov serverga naqd va karta bo'yicha ajratilib yuboriladi",
        "Smena va Z hisobot serverdan; kiritish smena summasiga kiradi; saytdagidek doimiy chek raqami",
        "«Qarz to'lovi»: bitta summa bilan yopish — o'nlab to'lovli qarz 15 soniya o'rniga 1 soniyada",
        "TM-30F (Dahua) tarozilari: mahsulotlarni kassadan taroziga to'g'ridan-to'g'ri yuborish, sozlamalar va shtrix-kod formati",
        "Tarmoqda tarozilarni qidirish boshqa quyi tarmoqlarni ham tekshiradi; Rongta: shtrix-koddagi mahsulot kodi endi nol emas",
        "Tuzatildi: «Smenani ochish»dan keyingi birinchi chek oflayn ketardi; server xatosida (502) tushunarli xabar va ikki savdo yo'q",
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
