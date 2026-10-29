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
        "Поиск везде работает по-новому: слова в любом порядке («негаз асу» находит «Асу негазированная»), «Asu» находит «Асу», неверная раскладка (ASU ↔ ФЫГ), ё = е, кыргызские ү/ө/ң",
        "Склад → Приёмка: таблица по новой схеме — «На складе», «Принимаем», «Закупка», «Наценка %», «Цена продажи», «Закупка изменилась», «Как посчитана цена», «Прибыль с шт.», «Сумма закупки»; сверху панель «Наценка для всех, %»",
        "Карточка товара: новая вкладка «Упаковка»",
        "Добавление товара: единица измерения, цена и количество — в одном окне, с понятным выбором единицы",
        "Штрихкод принимает только цифры (буквы и пробелы из поля убираются)",
        "Лента цен: цена на плитке не откатывается после прихода или правки товара",
        "Синхронизация быстрее: общие настройки компании подхватываются без перезапуска (раз в 30 с), чужие продажи видны за 20 с, в строке состояния — «Синхронизация: время»",
        "Кнопка «Обновить» в окнах разделов — подтянуть свежие данные с сервера сразу",
        "Сотрудники и роли: добавление и правка сотрудника с проверкой полей, понятные сообщения сервера",
        "Программа владельца: «Аналитика бонусов» (начислено, потрачено, сгорело, топ клиентов)",
        "Программа владельца: зарплата от прибыли — расчёт и выплата доли",
        "Программа владельца: «Инвестиции» — инвесторы, условия, расходы на запуск, окупаемость, выплаты, выгрузка в файл",
    ];

    public static readonly string[] LatestKy =
    [
        "Издөө бардык жерде жаңыча иштейт: сөздөр каалаган тартипте («негаз асу» «Асу негазированная» табат), «Asu» «Асу» табат, раскладка туура эмес (ASU ↔ ФЫГ), ё = е, кыргыз ү/ө/ң",
        "Кампа → Кабыл алуу: таблица жаңы схема боюнча — «Кампада», «Кабыл алабыз», «Сатып алуу», «Үстөк %», «Сатуу баасы», «Сатып алуу өзгөрдү», «Баа кантип эсептелди», «Даанасынан киреше», «Сатып алуу суммасы»; жогорку жагында «Баарына үстөк, %» панели",
        "Товар картасы: жаңы «Таңгак» кошумча барагы",
        "Товар кошуу: өлчөө бирдиги, баасы жана саны — бир терезеде, бирдикти түшүнүктүү тандоо менен",
        "Штрихкод тек гана сандарды кабыл алат (тамгалар жана боштуктар талаадан алынып салынат)",
        "Баа лентасы: плиткадагы баа товар кирип келгенден же оңдолгондон кийин кайра кетпейт",
        "Синхрондоо тезирээк: компаниянын жалпы жөндөөлөрү кайра иштетүүсүз алынат (30 с сайын), башкалардын сатуулары 20 с ичинде көрүнөт, абал сабында — «Синхрондоо: убакыт»",
        "Бөлүм терезелериндеги «Жаңыртуу» баскычы — серверден жаңы маалыматты дароо алуу",
        "Кызматкерлер жана ролдор: кызматкерди талааларды текшерүү менен кошуу жана оңдоо, сервердин түшүнүктүү билдирүүлөрү",
        "Ээсинин программасы: «Бонустар аналитикасы» (эсептелди, жумшалды, күйүп кетти, топ кардарлар)",
        "Ээсинин программасы: кирешеден эмгек акы — үлүштү эсептөө жана төлөө",
        "Ээсинин программасы: «Инвестициялар» — инвесторлор, шарттар, ишке киргизүү чыгымдары, кайтарым, төлөмдөр, файлга чыгаруу",
    ];

    public static readonly string[] LatestEn =
    [
        "Search works in a new way everywhere: words in any order (“negaz asu” finds “Asu non-carbonated”), “Asu” finds “Асу”, wrong keyboard layout (ASU ↔ ФЫГ), ё = е, Kyrgyz ү/ө/ң",
        "Warehouse → Receiving: the table follows the new scheme — “In stock”, “Receiving”, “Cost”, “Markup %”, “Sale price”, “Cost changed”, “How the price was calculated”, “Profit per unit”, “Cost total”; a “Markup for all, %” panel on top",
        "Product card: new “Packaging” tab",
        "Adding a product: unit, price and quantity in one window, with a clear unit picker",
        "The barcode field accepts digits only (letters and spaces are removed)",
        "Price feed: a tile price no longer rolls back after receiving or editing a product",
        "Faster sync: shared company settings are picked up without a restart (every 30 s), other tills’ sales show within 20 s, the status bar shows “Sync: time”",
        "“Refresh” button in section windows — pull fresh data from the server right away",
        "Employees and roles: add and edit an employee with field validation, clear server messages",
        "Owner program: “Bonus analytics” (accrued, spent, expired, top customers)",
        "Owner program: salary from profit — calculating and paying out a share",
        "Owner program: “Investments” — investors, terms, startup costs, payback, payouts, export to a file",
    ];

    public static readonly string[] LatestTr =
    [
        "Arama her yerde yeni çalışıyor: kelimeler herhangi bir sırada («negaz asu» «Asu gazsız» bulur), «Asu» «Асу» bulur, yanlış klavye düzeni (ASU ↔ ФЫГ), ё = е, Kırgız ү/ө/ң",
        "Depo → Mal kabul: tablo yeni şemaya göre — «Stokta», «Kabul edilen», «Alış», «Kâr payı %», «Satış fiyatı», «Alış değişti», «Fiyat nasıl hesaplandı», «Adet başı kâr», «Alış toplamı»; üstte «Herkes için kâr payı, %» paneli",
        "Ürün kartı: yeni «Paketleme» sekmesi",
        "Ürün ekleme: birim, fiyat ve miktar tek pencerede, anlaşılır birim seçimiyle",
        "Barkod alanı yalnızca rakam kabul eder (harfler ve boşluklar silinir)",
        "Fiyat akışı: kutucuktaki fiyat mal kabulden veya ürün düzenlemesinden sonra geri dönmez",
        "Daha hızlı eşitleme: şirketin ortak ayarları yeniden başlatmadan alınır (30 sn'de bir), diğer kasaların satışları 20 sn içinde görünür, durum çubuğunda «Eşitleme: saat»",
        "Bölüm pencerelerinde «Yenile» düğmesi — güncel verileri sunucudan hemen çek",
        "Çalışanlar ve roller: alan denetimiyle çalışan ekleme ve düzenleme, anlaşılır sunucu iletileri",
        "Sahip programı: «Bonus analitiği» (tahakkuk, harcanan, yanan, en iyi müşteriler)",
        "Sahip programı: kârdan maaş — payı hesaplama ve ödeme",
        "Sahip programı: «Yatırımlar» — yatırımcılar, koşullar, açılış giderleri, geri dönüş, ödemeler, dosyaya aktarma",
    ];

    public static readonly string[] LatestUz =
    [
        "Qidiruv hamma joyda yangicha ishlaydi: so'zlar istalgan tartibda («negaz asu» «Асу негазированная»ni topadi), «Asu» «Асу»ni topadi, noto'g'ri klaviatura (ASU ↔ ФЫГ), ё = е, qirg'iz ү/ө/ң",
        "Ombor → Qabul qilish: jadval yangi sxema bo'yicha — «Omborda», «Qabul qilamiz», «Xarid», «Ustama %», «Sotuv narxi», «Xarid o'zgardi», «Narx qanday hisoblandi», «Donasiga foyda», «Xarid summasi»; tepada «Hammaga ustama, %» paneli",
        "Mahsulot kartasi: yangi «Qadoq» yorlig'i",
        "Mahsulot qo'shish: o'lchov birligi, narx va miqdor bitta oynada, birlikni tushunarli tanlash bilan",
        "Shtrix-kod maydoni faqat raqamlarni qabul qiladi (harflar va bo'shliqlar olib tashlanadi)",
        "Narxlar lentasi: plitkadagi narx kirimdan yoki mahsulotni tahrirlashdan keyin qaytmaydi",
        "Sinxronlash tezroq: kompaniyaning umumiy sozlamalari qayta ishga tushirmasdan olinadi (30 s da bir), boshqa kassalarning sotuvlari 20 s ichida ko'rinadi, holat satrida «Sinxronlash: vaqt»",
        "Bo'lim oynalarida «Yangilash» tugmasi — yangi ma'lumotni serverdan darrov olish",
        "Xodimlar va rollar: maydonlarni tekshirish bilan xodim qo'shish va tahrirlash, serverning tushunarli xabarlari",
        "Egasi dasturi: «Bonus tahlili» (hisoblandi, sarflandi, kuyib ketdi, eng yaxshi mijozlar)",
        "Egasi dasturi: foydadan ish haqi — ulushni hisoblash va to'lash",
        "Egasi dasturi: «Investitsiyalar» — investorlar, shartlar, ochilish xarajatlari, qoplanish, to'lovlar, faylga chiqarish",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-29: 1.17.64 — списки Latest*/AndroidLatest* заменены на изменения этой версии (поиск, приёмка, упаковка, бонусы, зарплата, инвестиции, синхронизация).
    // 2026-10-27: интеграция итоговой 1.17.63 - добавлены пункты потоков (Советник, окно оплаты, накладные, темы сайта, смены, пачки); в Android - общие пункты оплаты, смен и этикеток.
    // 2026-10-27: Android 1.17.63 (тестовая) — общее с Windows в 1.17.63 (оплата, цены, смены, возвраты, отчёты, сканер, тёмная тема), без пунктов только для компьютера.
    // 2026-10-27: Android 1.17.62 (тестовая) — то же, что на Windows в 1.17.62, без пунктов только для компьютера (печать, второй монитор, удалённая помощь, чеки с телефона, Склад).
    // 2026-10-24: Android 1.17.60 (тестовая) — то же, что на Windows в 1.17.60, без пунктов только для компьютера (воронка, окна и панель задач, горячие клавиши, импорт) и без программы владельца.
    // 2026-10-21: Android 1.17.59 (тестовая) — то же, что на Windows в 1.17.59, без пунктов только для компьютера.
    // 2026-10-11: Android 1.17.58 (после 1.17.53 — всё, что вышло на Windows в 1.17.54–1.17.58).
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "Поиск везде работает по-новому: слова в любом порядке («негаз асу» находит «Асу негазированная»), «Asu» находит «Асу», неверная раскладка (ASU ↔ ФЫГ), ё = е, кыргызские ү/ө/ң",
        "Добавление товара: единица измерения, цена и количество — в одном окне, с понятным выбором единицы",
        "Штрихкод принимает только цифры (буквы и пробелы из поля убираются)",
        "Лента цен: цена на плитке не откатывается после прихода или правки товара",
        "Синхронизация быстрее: общие настройки компании подхватываются без перезапуска (раз в 30 с), чужие продажи видны за 20 с, в строке состояния — «Синхронизация: время»",
        "Кнопка «Обновить» в окнах разделов — подтянуть свежие данные с сервера сразу",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Издөө бардык жерде жаңыча иштейт: сөздөр каалаган тартипте («негаз асу» «Асу негазированная» табат), «Asu» «Асу» табат, раскладка туура эмес (ASU ↔ ФЫГ), ё = е, кыргыз ү/ө/ң",
        "Товар кошуу: өлчөө бирдиги, баасы жана саны — бир терезеде, бирдикти түшүнүктүү тандоо менен",
        "Штрихкод тек гана сандарды кабыл алат (тамгалар жана боштуктар талаадан алынып салынат)",
        "Баа лентасы: плиткадагы баа товар кирип келгенден же оңдолгондон кийин кайра кетпейт",
        "Синхрондоо тезирээк: компаниянын жалпы жөндөөлөрү кайра иштетүүсүз алынат (30 с сайын), башкалардын сатуулары 20 с ичинде көрүнөт, абал сабында — «Синхрондоо: убакыт»",
        "Бөлүм терезелериндеги «Жаңыртуу» баскычы — серверден жаңы маалыматты дароо алуу",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Search works in a new way everywhere: words in any order (“negaz asu” finds “Asu non-carbonated”), “Asu” finds “Асу”, wrong keyboard layout (ASU ↔ ФЫГ), ё = е, Kyrgyz ү/ө/ң",
        "Adding a product: unit, price and quantity in one window, with a clear unit picker",
        "The barcode field accepts digits only (letters and spaces are removed)",
        "Price feed: a tile price no longer rolls back after receiving or editing a product",
        "Faster sync: shared company settings are picked up without a restart (every 30 s), other tills’ sales show within 20 s, the status bar shows “Sync: time”",
        "“Refresh” button in section windows — pull fresh data from the server right away",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Arama her yerde yeni çalışıyor: kelimeler herhangi bir sırada («negaz asu» «Asu gazsız» bulur), «Asu» «Асу» bulur, yanlış klavye düzeni (ASU ↔ ФЫГ), ё = е, Kırgız ү/ө/ң",
        "Ürün ekleme: birim, fiyat ve miktar tek pencerede, anlaşılır birim seçimiyle",
        "Barkod alanı yalnızca rakam kabul eder (harfler ve boşluklar silinir)",
        "Fiyat akışı: kutucuktaki fiyat mal kabulden veya ürün düzenlemesinden sonra geri dönmez",
        "Daha hızlı eşitleme: şirketin ortak ayarları yeniden başlatmadan alınır (30 sn'de bir), diğer kasaların satışları 20 sn içinde görünür, durum çubuğunda «Eşitleme: saat»",
        "Bölüm pencerelerinde «Yenile» düğmesi — güncel verileri sunucudan hemen çek",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Qidiruv hamma joyda yangicha ishlaydi: so'zlar istalgan tartibda («negaz asu» «Асу негазированная»ni topadi), «Asu» «Асу»ni topadi, noto'g'ri klaviatura (ASU ↔ ФЫГ), ё = е, qirg'iz ү/ө/ң",
        "Mahsulot qo'shish: o'lchov birligi, narx va miqdor bitta oynada, birlikni tushunarli tanlash bilan",
        "Shtrix-kod maydoni faqat raqamlarni qabul qiladi (harflar va bo'shliqlar olib tashlanadi)",
        "Narxlar lentasi: plitkadagi narx kirimdan yoki mahsulotni tahrirlashdan keyin qaytmaydi",
        "Sinxronlash tezroq: kompaniyaning umumiy sozlamalari qayta ishga tushirmasdan olinadi (30 s da bir), boshqa kassalarning sotuvlari 20 s ichida ko'rinadi, holat satrida «Sinxronlash: vaqt»",
        "Bo'lim oynalarida «Yangilash» tugmasi — yangi ma'lumotni serverdan darrov olish",
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
