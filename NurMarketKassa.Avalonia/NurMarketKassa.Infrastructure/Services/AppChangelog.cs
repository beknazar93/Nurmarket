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
        "Аналитика обновляется сама, в реальном времени. Окна «Продажи», «Финансы» и «ABC-анализ» раньше показывали данные на момент открытия и обновлялись только по кнопке: кассир пробивал чек, а владелец смотрел на открытое окно и видел прежние цифры. Теперь любая запись, влияющая на отчёты — продажа, возврат, списание, расход, оплата долга, внесение и изъятие наличных — обновляет открытые окна сама, через полторы секунды после операции",
        "Весы Штрих-Принт: клавиши на панели не работали. Программа записывала товар под ЕГО СОБСТВЕННЫМ номером из каталога — у «Айфона» это, например, 10007. А клавиши вызывают ПЛУ по номеру и по положению: клавиша 1 — ПЛУ 1, клавиша 2 — ПЛУ 2. До номера 10007 ни одна клавиша не дотягивалась, и панель выглядела нерабочей, хотя выгрузка формально проходила. Теперь номера назначаются подряд: порядок товаров в списке и есть порядок клавиш. После выгрузки показывается, какой товар на какой клавише, а полная раскладка пишется в журнал — по ней удобно подписывать панель",
        "Предпросмотр чека наконец выглядит как чек: моноширинный шрифт на белой «бумаге», шапка «Контрольно-кассовый чек», магазин, ИНН и адрес по настройкам, номер чека, позиции с выравниванием суммы вправо. Раньше это был список с точками вида «• товар — 1 × 95,00 = 95.00», который невозможно было сверить с бумажным чеком",
        "Предпросмотр и повторная печать теперь строятся одним кодом — раньше это были два разных формата, и на экране было одно, а на бумаге другое",
        "Исправлено в чеке: «Сумма 6 114,70 / Скидка −15,00 / ИТОГО 6 114,70» — скидка была, а итог от неё не менялся. Причина: когда сервер отклоняет скидку суммой, касса раскладывает её по ценам позиций, и сумма строк уже идёт со скидкой. Теперь «Сумма» показывается до скидки, и арифметика на чеке сходится",
        "Банки: в настройках снова три основных, а кнопка «Добавить банк» открывает выбор из всех 26 банков Кыргызстана по реестру НБКР (или можно вписать своё название). Добавленный банк сразу появляется при оплате",
        "Календарь выбора дат оформлен под стиль программы: раньше выпадающая панель приходила из системной темы и выглядела серо-голубой плашкой поверх окна, а выбранный день терялся. Теперь фон совпадает с карточкой, выбранный день выделен, сегодняшний обведён, дни соседних месяцев приглушены",
    ];

    public static readonly string[] LatestKy =
    [
        "Аналитика өзү жаңыланат. «Сатуулар», «Каржы» жана «ABC-анализ» терезелери мурда ачылган учурдагы маалыматты көрсөтчү жана «Жаңылоо» баскычы менен гана жаңырчу. Эми отчётторго таасир этүүчү ар бир жазуу — сатуу, кайтаруу, эсептен чыгаруу, чыгым, карызды төлөө, акча салуу жана алуу — ачык терезелерди өзү жаңылайт",
        "Штрих-Принт таразалары: панелдеги баскычтар иштебей жаткан. Программа товарды ӨЗҮНҮН номери менен жазчу (мисалы 10007), ал эми баскычтар ПЛУну номери жана орду боюнча чакырат: 1-баскыч — 1-ПЛУ. Эми номерлер катары менен берилет, тизмедеги тартип — баскычтардын тартиби. Жүктөөдөн кийин кайсы товар кайсы баскычта экени көрсөтүлөт",
        "Чектин алдын ала көрүнүшү эми чекке окшош: ак «кагазда» моношириналуу шрифт, «Контролдук-кассалык чек» башы, дүкөн, чектин номери, суммалар оңго тегизделген",
        "Алдын ала көрүнүш жана кайра басып чыгаруу эми бир код менен түзүлөт — мурда экранда бир, кагазда башка болчу",
        "Чектеги ката оңдолду: арзандатуу бар эле, бирок жыйынтык өзгөрчү эмес. Эми «Сумма» арзандатууга чейин көрсөтүлөт жана эсеп туура чыгат",
        "Банктар: жөндөөлөрдө кайрадан үчөө, ал эми «Банк кошуу» баскычы Кыргызстандын 26 банкынын тизмесин ачат",
        "Күндөрдү тандоо календары программанын стилине келтирилди: фону карточка менен дал келет, тандалган күн белгиленет",
    ];

    public static readonly string[] LatestEn =
    [
        "Analytics now refresh themselves, in real time. The Sales, Finance and ABC windows used to show the data from the moment they were opened and only refreshed on a button press: the cashier rang up a sale and the owner, looking at an open window, still saw the old numbers. Now anything that affects the reports - a sale, return, write-off, expense, debt payment, cash in or out - refreshes the open windows by itself, about a second and a half after the operation",
        "Shtrikh-Print scales: the keys on the panel did nothing. The app wrote each product under ITS OWN catalogue number - 10007 for an iPhone, say - while the keys address a PLU by its number and position: key 1 is PLU 1, key 2 is PLU 2. Nothing could reach number 10007, so the panel looked dead even though the upload reported success. Numbers are now assigned sequentially: the order in the list is the order of the keys. After the upload the app shows which product sits on which key, and the full layout goes to the log so the panel can be labelled",
        "The receipt preview finally looks like a receipt: monospace on white paper, a proper header, shop name, tax number and address from settings, receipt number and right-aligned amounts. It used to be a bulleted list that could not be checked against the printed slip",
        "Preview and reprint are now built by the same code - they used to be two different formats",
        "Fixed on the receipt: Subtotal 6 114.70 / Discount -15.00 / TOTAL 6 114.70 - the discount was there but the total never changed. When the server rejects a cash discount the till spreads it across the line prices, so the lines are already net. Subtotal is now shown before the discount and the arithmetic adds up",
        "Banks: Settings list the three main ones again, and Add bank opens a picker with all 26 Kyrgyz banks from the NBKR register",
        "The date picker calendar now matches the app style instead of arriving as a grey-blue slab from the system theme",
    ];

    public static readonly string[] LatestTr =
    [
        "Analitik artık kendiliğinden, gerçek zamanlı yenileniyor. Satışlar, Finans ve ABC pencereleri önceden yalnızca açıldıkları andaki verileri gösteriyordu. Artık raporları etkileyen her kayıt - satış, iade, zayiat, gider, borç ödemesi, kasa giriş ve çıkışı - açık pencereleri kendisi yeniliyor",
        "Shtrikh-Print teraziler: paneldeki tuşlar çalışmıyordu. Uygulama her ürünü KENDİ katalog numarasıyla yazıyordu (örneğin 10007), oysa tuşlar PLU'yu numarasına ve konumuna göre çağırır: 1. tuş 1. PLU. Artık numaralar sırayla atanıyor, listedeki sıra tuşların sırasıdır",
        "Fiş önizlemesi sonunda fiş gibi görünüyor: beyaz «kağıt» üzerinde eşaralıklı yazı tipi, başlık, mağaza, fiş numarası ve sağa hizalı tutarlar",
        "Önizleme ve yeniden yazdırma artık aynı kodla üretiliyor",
        "Fişte düzeltildi: indirim vardı ama toplam değişmiyordu. Artık «Ara toplam» indirimden önce gösteriliyor ve hesap tutuyor",
        "Bankalar: Ayarlar'da yine üç ana banka, «Banka ekle» ise Kırgızistan'ın 26 bankasının listesini açıyor",
        "Tarih seçme takvimi artık uygulamanın stiline uyuyor",
    ];

    public static readonly string[] LatestUz =
    [
        "Tahlil endi o'zi, real vaqtda yangilanadi. «Sotuvlar», «Moliya» va «ABC» oynalari ilgari faqat ochilgan paytdagi ma'lumotni ko'rsatardi. Endi hisobotlarga ta'sir qiluvchi har bir yozuv - sotuv, qaytarish, hisobdan chiqarish, xarajat, qarz to'lovi, kassaga pul kiritish va olish - ochiq oynalarni o'zi yangilaydi",
        "Shtrix-Print tarozilari: paneldagi tugmalar ishlamasdi. Dastur mahsulotni O'Z katalog raqami bilan yozardi (masalan 10007), tugmalar esa PLUni raqami va o'rni bo'yicha chaqiradi: 1-tugma — 1-PLU. Endi raqamlar ketma-ket beriladi, ro'yxatdagi tartib — tugmalar tartibi",
        "Chekning oldindan ko'rinishi nihoyat chekka o'xshaydi: oq «qog'oz»da monoshirin shrift, sarlavha, do'kon, chek raqami va o'ngga tekislangan summalar",
        "Oldindan ko'rish va qayta chop etish endi bitta kod bilan tuziladi",
        "Chekda tuzatildi: chegirma bor edi, lekin jami o'zgarmasdi. Endi «Summa» chegirmadan oldin ko'rsatiladi va hisob to'g'ri chiqadi",
        "Banklar: sozlamalarda yana uchta asosiy bank, «Bank qo'shish» esa Qirg'izistonning 26 banki ro'yxatini ochadi",
        "Sana tanlash taqvimi endi dastur uslubiga mos keladi",
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