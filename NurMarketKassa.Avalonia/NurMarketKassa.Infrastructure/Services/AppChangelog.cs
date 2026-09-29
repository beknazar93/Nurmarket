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
        "Товары с акцией NurCRM: сумма в окне оплаты теперь та же, что проводит сервер — раньше окно показывало 50,00, а продажа проходила на 42,50",
        "Если сервер всё же провёл продажу на другую сумму, касса сразу предупреждает об этом",
        "«Финансы», «Продажи», «Аналитика»: выручка, чеки, оплаты, возвраты, прибыль и топ товаров — ровно как на сайте NurCRM; плитки появляются за доли секунды",
        "Большие магазины: окна аналитики больше не зависают на длинном периоде (месяц с 12 000 чеков — было до 40 с), список догружается в фоне",
        "Настройки → Весы переделаны: вкладки «Весы на кассе», «Весы с этикетками», «Штрих-код: вес / сумма», мастер «Настроить по этикетке», несколько весов с категориями",
        "Отправка на весы: клавиши быстрого доступа Штрих-ПРИНТ, фильтр по категории, результат по каждому товару",
        "Настройки — вкладками сверху; вид кассы выбирается в Маркетплейсе → «Виды кассы»; база знаний: 30+ новых статей про весы",
        "Телеграм-бот: длинные отчёты приходят частями, ответы и напоминания должникам не теряются при нагрузке, первая команда после включения кассы больше не пропадает",
    ];

    public static readonly string[] LatestKy =
    [
        "NurCRM акциясы бар товарлар: төлөм терезесиндеги сумма эми сервер өткөргөн суммага барабар — мурун терезе 50,00 көрсөтүп, сатуу 42,50 болуп өтчү",
        "Эгер сервер сатууну башка суммага өткөрсө, касса дароо эскертет",
        "«Каржы», «Сатуулар», «Аналитика»: түшүм, чектер, төлөмдөр, кайтаруулар, пайда жана топ товарлар — NurCRM сайтындагыдай; плиткалар секунданын ичинде чыгат",
        "Чоң дүкөндөр: аналитика терезелери узун мезгилде катып калбайт (12 000 чектүү ай — 40 секундга чейин болчу), тизме фондо жүктөлөт",
        "Жөндөөлөр → Таразалар жаңыланды: «Кассадагы тараза», «Этикеткалуу тараза», «Штрих-код: салмак / сумма» өтмөктөрү, «Этикетка боюнча жөндөө» устасы, категориялары менен бир нече тараза",
        "Таразага жөнөтүү: Штрих-ПРИНТ ыкчам баскычтары, категория боюнча чыпка, ар бир товардын жыйынтыгы",
        "Жөндөөлөр — өйдөдө өтмөктөр менен; кассанын түрү Маркетплейсте → «Кассанын түрлөрү»; билим базасы: таразалар жөнүндө 30дан ашык жаңы макала",
        "Телеграм-бот: узун отчёттор бөлүктөп келет, жооптор жана карыздарга эскертмелер жүктөмдө жоголбойт, касса күйгөндөн кийинки биринчи буйрук эми жоголбойт",
    ];

    public static readonly string[] LatestEn =
    [
        "NurCRM promo products: the amount in the payment window is now the same the server charges — before, the window showed 50.00 and the sale went through at 42.50",
        "If the server still records the sale with a different amount, the till warns at once",
        "“Finance”, “Sales”, “Analytics”: revenue, receipts, payments, returns, profit and top products exactly as on the NurCRM website; the tiles appear in a fraction of a second",
        "Large stores: analytics windows no longer freeze on long periods (a month with 12,000 receipts took up to 40 s), the list loads in the background",
        "Settings → Scales redesigned: tabs “Till scales”, “Label scales”, “Barcode: weight / total”, the “Set up from a label” wizard, several scales with categories",
        "Sending to scales: Shtrikh-PRINT hot keys, category filter, result for each product",
        "Settings as tabs at the top; the till layout is chosen in Marketplace → “Till layouts”; knowledge base: 30+ new articles on scales",
        "Telegram bot: long reports arrive in parts, replies and debt reminders are not lost under load, the first command after the till starts is no longer skipped",
    ];

    public static readonly string[] LatestTr =
    [
        "NurCRM kampanyalı ürünler: ödeme penceresindeki tutar artık sunucunun aldığıyla aynı — önce pencere 50,00 gösteriyor, satış 42,50 olarak geçiyordu",
        "Sunucu satışı yine de farklı bir tutarla kaydederse kasa hemen uyarır",
        "«Finans», «Satışlar», «Analitik»: ciro, fişler, ödemeler, iadeler, kâr ve en çok satanlar NurCRM sitesindeki gibi; kutucuklar bir saniyeden kısa sürede gelir",
        "Büyük mağazalar: analitik pencereleri uzun dönemde artık donmuyor (12.000 fişli ay 40 sn’ye kadar sürüyordu), liste arka planda yüklenir",
        "Ayarlar → Teraziler yenilendi: «Kasa terazisi», «Etiketli terazi», «Barkod: ağırlık / tutar» sekmeleri, «Etikete göre ayarla» sihirbazı, kategorili birden çok terazi",
        "Teraziye gönderme: Shtrikh-PRINT kısayol tuşları, kategori filtresi, her ürün için sonuç",
        "Ayarlar üstte sekmeler halinde; kasa görünümü Market → «Kasa görünümleri» içinden seçilir; bilgi bankası: teraziler hakkında 30’dan fazla yeni makale",
        "Telegram botu: uzun raporlar parça parça gelir, yanıtlar ve borç hatırlatmaları yoğunlukta kaybolmaz, kasa açıldıktan sonraki ilk komut artık atlanmaz",
    ];

    public static readonly string[] LatestUz =
    [
        "NurCRM aksiyali mahsulotlar: to'lov oynasidagi summa endi server o'tkazgan summa bilan bir xil — oldin oyna 50,00 ko'rsatib, savdo 42,50 bo'lib o'tardi",
        "Agar server savdoni baribir boshqa summa bilan o'tkazsa, kassa darhol ogohlantiradi",
        "«Moliya», «Savdolar», «Tahlil»: tushum, cheklar, to'lovlar, qaytarishlar, foyda va top mahsulotlar — NurCRM saytidagidek; plitkalar bir soniyadan kam vaqtda chiqadi",
        "Katta do'konlar: tahlil oynalari uzun davrda endi qotib qolmaydi (12 000 chekli oy — 40 soniyagacha edi), ro'yxat fonda yuklanadi",
        "Sozlamalar → Tarozilar yangilandi: «Kassadagi tarozi», «Yorliqli tarozi», «Shtrix-kod: vazn / summa» varaqlari, «Yorliq bo'yicha sozlash» ustasi, toifalari bilan bir nechta tarozi",
        "Taroziga yuborish: Shtrix-PRINT tezkor tugmalari, toifa bo'yicha filtr, har bir mahsulot natijasi",
        "Sozlamalar — tepada varaqlar bilan; kassa ko'rinishi Marketpleysda → «Kassa ko'rinishlari»; bilim bazasi: tarozilar haqida 30 dan ortiq yangi maqola",
        "Telegram-bot: uzun hisobotlar qismlarga bo'lib keladi, javoblar va qarzdorlarga eslatmalar yuklamada yo'qolmaydi, kassa yoqilgandan keyingi birinchi buyruq endi o'tkazib yuborilmaydi",
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
