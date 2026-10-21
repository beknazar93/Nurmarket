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
        "Возврат учитывает скидку на чек и долг: к выдаче — только то, что реально оплачено; в истории чека виден блок «Возвраты»",
        "Долг: срок возврата (обычно 30 дней) печатается в чеке — «Вернуть до …»; оплата долга — список долгов клиента, Enter и подтверждение «Погасить»",
        "Закрытие смены с разбивкой продаж и возвратов и строкой «Ожидается N»; X-отчёт — без закрытия смены",
        "Поставщики: поставщик в карточке товара, долг поставщику при приёмке и его оплата",
        "Прокат — во всех видах магазина; в программе владельца во вкладке «Клиенты» — КП, счёт и накладная (PDF, Word, печать, WhatsApp)",
        "Нур Советник отвечает быстрее (1–3 секунды) и точнее, показывает «откуда цифры» и таблицу в Excel; утренний отчёт в 9:00 и напоминания должникам",
        "Telegram-бот: владелец подтверждает себя одноразовым кодом, выручка с сервера, быстрые ответы, меню кнопками",
        "Новый вид окон и меню ☰, тёмная тема без жёлтых цен, много мелких исправлений",
    ];

    public static readonly string[] LatestKy =
    [
        "Кайтаруу чектеги арзандатууну жана карызды эске алат: чыныгы төлөнгөнү гана берилет; чектин тарыхында «Кайтаруулар» бөлүгү көрүнөт",
        "Карыз: кайтаруу мөөнөтү (адатта 30 күн) чекте басылат — «… чейин кайтаруу»; карызды төлөө — кардардын карыздарынын тизмеси, Enter жана «Жабуу» ырастоосу",
        "Сменаны жабуу сатуулардын жана кайтаруулардын бөлүнүшү жана «Күтүлөт N» сабы менен; X-отчёт — сменаны жаппай",
        "Жеткирүүчүлөр: товардын карточкасында жеткирүүчү, кабыл алууда жеткирүүчүгө карыз жана аны төлөө",
        "Прокат — дүкөндүн бардык түрлөрүндө; ээсинин программасында «Кардарлар» өтмөгүндө — КП, эсеп жана накладной (PDF, Word, басып чыгаруу, WhatsApp)",
        "Нур Кеңешчи тезирээк (1–3 секунд) жана тагыраак жооп берет, «сандар кайдан» жана Excel таблицасын көрсөтөт; саат 9:00дө эртең мененки отчёт жана карыздарга эскертүүлөр",
        "Telegram-бот: ээси өзүн бир жолку код менен ырастайт, түшкөн акча серверден, тез жооптор, баскычтар менен меню",
        "Терезелердин жана ☰ менюнун жаңы көрүнүшү, баалары сары эмес караңгы тема, көп майда оңдоолор",
    ];

    public static readonly string[] LatestEn =
    [
        "Returns take the receipt discount and debt into account: only what was actually paid is given back; the receipt history shows a “Returns” block",
        "Debt: the due date (usually 30 days) is printed on the receipt — “Return by …”; paying a debt shows the client's debts, Enter and a “Pay off” confirmation",
        "Closing a shift shows sales and returns separately and an “Expected N” line; the X-report works without closing the shift",
        "Suppliers: the supplier in the product card, a supplier debt at receiving and paying it",
        "Rentals in every store type; in the owner app the “Clients” tab has quotes, invoices and waybills (PDF, Word, print, WhatsApp)",
        "Nur Advisor answers faster (1–3 seconds) and more accurately, shows “where the numbers come from” and an Excel table; a morning report at 9:00 and reminders to debtors",
        "Telegram bot: the owner confirms with a one-time code, revenue from the server, fast replies, a button menu",
        "New look of windows and the ☰ menu, a dark theme without yellow prices, many small fixes",
    ];

    public static readonly string[] LatestTr =
    [
        "İade, fiş indirimini ve borcu dikkate alır: yalnızca gerçekten ödenen geri verilir; fiş geçmişinde «İadeler» bölümü görünür",
        "Borç: geri ödeme tarihi (genelde 30 gün) fişe basılır — «… tarihine kadar iade»; borç ödemede müşterinin borçları, Enter ve «Kapat» onayı",
        "Vardiya kapanışı satışları ve iadeleri ayrı ve «Beklenen N» satırıyla gösterir; X raporu vardiyayı kapatmadan çalışır",
        "Tedarikçiler: ürün kartında tedarikçi, mal kabulde tedarikçiye borç ve ödemesi",
        "Kiralama tüm mağaza türlerinde; sahip programında «Müşteriler» sekmesinde teklif, fatura ve irsaliye (PDF, Word, yazdırma, WhatsApp)",
        "Nur Danışman daha hızlı (1–3 saniye) ve daha doğru yanıt verir, «rakamlar nereden» ve Excel tablosu gösterir; 9:00'da sabah raporu ve borçlulara hatırlatmalar",
        "Telegram botu: sahip kendini tek kullanımlık kodla doğrular, ciro sunucudan, hızlı yanıtlar, düğmeli menü",
        "Pencerelerin ve ☰ menüsünün yeni görünümü, sarı fiyatsız koyu tema, birçok küçük düzeltme",
    ];

    public static readonly string[] LatestUz =
    [
        "Qaytarish chekdagi chegirma va qarzni hisobga oladi: faqat haqiqatda to'langani qaytariladi; chek tarixida «Qaytarishlar» bloki ko'rinadi",
        "Qarz: qaytarish muddati (odatda 30 kun) chekda bosiladi — «… gacha qaytarish»; qarzni to'lashda mijozning qarzlari ro'yxati, Enter va «Yopish» tasdig'i",
        "Smenani yopish sotuvlar va qaytarishlarni alohida hamda «Kutilmoqda N» qatori bilan ko'rsatadi; X-hisobot smenani yopmasdan ishlaydi",
        "Yetkazib beruvchilar: mahsulot kartasida yetkazib beruvchi, qabul qilishda yetkazib beruvchiga qarz va uni to'lash",
        "Prokat do'konning barcha turlarida; egasining dasturida «Mijozlar» bo'limida tijorat taklifi, hisob va nakladnoy (PDF, Word, chop etish, WhatsApp)",
        "Nur Maslahatchi tezroq (1–3 soniya) va aniqroq javob beradi, «raqamlar qayerdan» va Excel jadvalini ko'rsatadi; soat 9:00 da ertalabki hisobot va qarzdorlarga eslatmalar",
        "Telegram-bot: egasi o'zini bir martalik kod bilan tasdiqlaydi, tushum serverdan, tezkor javoblar, tugmali menyu",
        "Oynalar va ☰ menyuning yangi ko'rinishi, sariq narxlarsiz qorong'i mavzu, ko'plab mayda tuzatishlar",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-21: Android 1.17.59 (тестовая) — то же, что на Windows в 1.17.59, без пунктов только для компьютера.
    // 2026-10-11: Android 1.17.58 (после 1.17.53 — всё, что вышло на Windows в 1.17.54–1.17.58).
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "Возврат учитывает скидку на чек и долг: к выдаче — только то, что реально оплачено; в истории чека виден блок «Возвраты»",
        "Долг: срок возврата (обычно 30 дней) печатается в чеке — «Вернуть до …»; оплата долга — список долгов клиента и подтверждение «Погасить»",
        "Закрытие смены с разбивкой продаж и возвратов и строкой «Ожидается N»; X-отчёт — без закрытия смены",
        "Поставщики: поставщик в карточке товара, долг поставщику при приёмке и его оплата; прокат — во всех видах магазина",
        "Нур Советник отвечает быстрее (1–3 секунды) и точнее, показывает «откуда цифры»; утренний отчёт в 9:00 и напоминания должникам",
        "Новый вид окон и меню, тёмная тема без жёлтых цен, много мелких исправлений",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Кайтаруу чектеги арзандатууну жана карызды эске алат: чыныгы төлөнгөнү гана берилет; чектин тарыхында «Кайтаруулар» бөлүгү көрүнөт",
        "Карыз: кайтаруу мөөнөтү (адатта 30 күн) чекте басылат — «… чейин кайтаруу»; карызды төлөө — кардардын карыздарынын тизмеси жана «Жабуу» ырастоосу",
        "Сменаны жабуу сатуулардын жана кайтаруулардын бөлүнүшү жана «Күтүлөт N» сабы менен; X-отчёт — сменаны жаппай",
        "Жеткирүүчүлөр: товардын карточкасында жеткирүүчү, кабыл алууда жеткирүүчүгө карыз жана аны төлөө; прокат — дүкөндүн бардык түрлөрүндө",
        "Нур Кеңешчи тезирээк (1–3 секунд) жана тагыраак жооп берет, «сандар кайдан» көрсөтөт; саат 9:00дө эртең мененки отчёт жана карыздарга эскертүүлөр",
        "Терезелердин жана менюнун жаңы көрүнүшү, баалары сары эмес караңгы тема, көп майда оңдоолор",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Returns take the receipt discount and debt into account: only what was actually paid is given back; the receipt history shows a “Returns” block",
        "Debt: the due date (usually 30 days) is printed on the receipt — “Return by …”; paying a debt shows the client's debts and a “Pay off” confirmation",
        "Closing a shift shows sales and returns separately and an “Expected N” line; the X-report works without closing the shift",
        "Suppliers: the supplier in the product card, a supplier debt at receiving and paying it; rentals in every store type",
        "Nur Advisor answers faster (1–3 seconds) and more accurately, shows “where the numbers come from”; a morning report at 9:00 and reminders to debtors",
        "New look of windows and the menu, a dark theme without yellow prices, many small fixes",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "İade, fiş indirimini ve borcu dikkate alır: yalnızca gerçekten ödenen geri verilir; fiş geçmişinde «İadeler» bölümü görünür",
        "Borç: geri ödeme tarihi (genelde 30 gün) fişe basılır — «… tarihine kadar iade»; borç ödemede müşterinin borçları ve «Kapat» onayı",
        "Vardiya kapanışı satışları ve iadeleri ayrı ve «Beklenen N» satırıyla gösterir; X raporu vardiyayı kapatmadan çalışır",
        "Tedarikçiler: ürün kartında tedarikçi, mal kabulde tedarikçiye borç ve ödemesi; kiralama tüm mağaza türlerinde",
        "Nur Danışman daha hızlı (1–3 saniye) ve daha doğru yanıt verir, «rakamlar nereden» gösterir; 9:00'da sabah raporu ve borçlulara hatırlatmalar",
        "Pencerelerin ve menünün yeni görünümü, sarı fiyatsız koyu tema, birçok küçük düzeltme",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Qaytarish chekdagi chegirma va qarzni hisobga oladi: faqat haqiqatda to'langani qaytariladi; chek tarixida «Qaytarishlar» bloki ko'rinadi",
        "Qarz: qaytarish muddati (odatda 30 kun) chekda bosiladi — «… gacha qaytarish»; qarzni to'lashda mijozning qarzlari ro'yxati va «Yopish» tasdig'i",
        "Smenani yopish sotuvlar va qaytarishlarni alohida hamda «Kutilmoqda N» qatori bilan ko'rsatadi; X-hisobot smenani yopmasdan ishlaydi",
        "Yetkazib beruvchilar: mahsulot kartasida yetkazib beruvchi, qabul qilishda yetkazib beruvchiga qarz va uni to'lash; prokat do'konning barcha turlarida",
        "Nur Maslahatchi tezroq (1–3 soniya) va aniqroq javob beradi, «raqamlar qayerdan» ko'rsatadi; soat 9:00 da ertalabki hisobot va qarzdorlarga eslatmalar",
        "Oynalar va menyuning yangi ko'rinishi, sariq narxlarsiz qorong'i mavzu, ko'plab mayda tuzatishlar",
    ];

    public static string[] LatestForCurrentLanguage() =>
        OperatingSystem.IsAndroid()
            ? UserPreferences.Instance.Language switch
            {
                AppLanguage.Kyrgyz => AndroidLatestKy,
                AppLanguage.English => AndroidLatestEn,
                AppLanguage.Turkish => AndroidLatestTr,
                AppLanguage.Uzbek => AndroidLatestUz,
                _ => AndroidLatest,
            }
            : UserPreferences.Instance.Language switch
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
