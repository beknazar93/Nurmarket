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
        "Склад: «Ревизия» — пересчёт сканером (скан → факт), акты, выгрузка в Excel, CSV и PDF; «Приёмка»: скан → строка, наценка, подорожание, кнопка «＋ поставщик»",
        "Оприходование в фоне: окно не ждёт сервер (плохой интернет — «операция выполняется в фоне»), новое окно «Приходы в фоне», повторы без дублей",
        "Нур Советник: накладная по штрихкодам, кнопка «Сгенерировать» штрихкод; ввод в полях больше не теряет символы",
        "Генератор штрихкода в карточке товара и закупке: код не начинается с 0 и 2 и не повторяет коды каталога",
        "Telegram-бот: «Группа заказов» — заказы приходят владельцу, в группу или в оба места",
        "Редактор этикеток: можно печатать только штрихкод, только название или только цену; размер текста не ограничен",
        "Скан в открытой карточке товара больше не срабатывает в окне «Склад»; поле «Штрихкод» в карточке шире",
    ];

    public static readonly string[] LatestKy =
    [
        "Кампа: «Ревизия» — сканер менен кайра эсептөө (скан → факт), актылар, Excel, CSV жана PDFке чыгаруу; «Кабыл алуу»: скан → сап, кошумча баа, кымбаттоо, «＋ жеткирүүчү» баскычы",
        "Фондо кирешелөө: терезе серверди күтпөйт (интернет начар болсо — «операция фондо аткарылууда»), «Фондогу кирешелер» жаңы терезеси, кайталоолор дубликатсыз",
        "Нур Кеңешчи: штрихкод боюнча накладная, штрихкодду «Түзүү» баскычы; талааларга киргизүү символдорду жоготпойт",
        "Товардын карточкасында жана сатып алууда штрихкод генератору: код 0 жана 2 менен башталбайт, каталогдогу коддорду кайталабайт",
        "Telegram-бот: «Буйрутмалар тобу» — буйрутмалар ээсине, топко же экөөнө тең келет",
        "Этикетка редактору: штрихкодду гана, атын гана же баасын гана басууга болот; текст өлчөмү чектелбейт",
        "Товардын ачык карточкасындагы скан «Кампа» терезесинде иштебейт; карточкадагы «Штрихкод» талаасы кеңирээк",
    ];

    public static readonly string[] LatestEn =
    [
        "Stock: “Revision” — counting with a scanner (scan → actual), acts, export to Excel, CSV and PDF; “Receiving”: scan → row, markup, price rise, the “＋ supplier” button",
        "Receiving in the background: the window does not wait for the server (bad internet — “the operation runs in the background”), a new “Receipts in the background” window, retries without duplicates",
        "Nur Advisor: an invoice by barcodes, a “Generate” barcode button; typing in fields no longer loses characters",
        "Barcode generator in the product card and purchasing: the code never starts with 0 or 2 and never repeats catalog codes",
        "Telegram bot: “Orders group” — orders go to the owner, to the group, or to both",
        "Label editor: you can print only the barcode, only the name or only the price; the text size is not limited",
        "A scan in an open product card no longer triggers in the “Stock” window; the “Barcode” field in the card is wider",
    ];

    public static readonly string[] LatestTr =
    [
        "Depo: «Sayım» — tarayıcıyla sayım (tarama → fiili), tutanaklar, Excel, CSV ve PDF dışa aktarma; «Mal kabul»: tarama → satır, kâr oranı, zam, «＋ tedarikçi» düğmesi",
        "Arka planda mal girişi: pencere sunucuyu beklemez (kötü internette «işlem arka planda yürütülüyor»), yeni «Arka plandaki girişler» penceresi, tekrarlar çift kayıtsız",
        "Nur Danışman: barkodlarla fatura, «Oluştur» barkod düğmesi; alanlara yazarken karakterler artık kaybolmaz",
        "Ürün kartında ve satın almada barkod üretici: kod 0 ve 2 ile başlamaz, katalog kodlarını tekrarlamaz",
        "Telegram botu: «Sipariş grubu» — siparişler sahibe, gruba veya ikisine gelir",
        "Etiket düzenleyici: yalnızca barkod, yalnızca ad veya yalnızca fiyat yazdırılabilir; yazı boyutu sınırsız",
        "Açık ürün kartındaki tarama artık «Depo» penceresinde çalışmaz; kartta «Barkod» alanı daha geniş",
    ];

    public static readonly string[] LatestUz =
    [
        "Ombor: «Taftish» — skaner bilan qayta hisoblash (skan → haqiqiy), dalolatnomalar, Excel, CSV va PDFga chiqarish; «Qabul»: skan → qator, ustama, qimmatlash, «＋ yetkazib beruvchi» tugmasi",
        "Fonda kirim: oyna serverni kutmaydi (yomon internetda «amal fonda bajarilmoqda»), yangi «Fondagi kirimlar» oynasi, takrorlar dublikatsiz",
        "Nur Maslahatchi: shtrix-kodlar bo'yicha hisob-faktura, shtrix-kodni «Yaratish» tugmasi; maydonlarga yozishda belgilar endi yo'qolmaydi",
        "Mahsulot kartasida va xaridda shtrix-kod generatori: kod 0 va 2 bilan boshlanmaydi, katalogdagi kodlarni takrorlamaydi",
        "Telegram-bot: «Buyurtmalar guruhi» — buyurtmalar egasiga, guruhga yoki ikkalasiga keladi",
        "Yorliq muharriri: faqat shtrix-kodni, faqat nomni yoki faqat narxni chop etish mumkin; matn o'lchami cheklanmagan",
        "Ochiq mahsulot kartasidagi skan endi «Ombor» oynasida ishlamaydi; kartadagi «Shtrix-kod» maydoni kengroq",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-24: Android 1.17.60 (тестовая) — то же, что на Windows в 1.17.60, без пунктов только для компьютера (воронка, окна и панель задач, горячие клавиши, импорт) и без программы владельца.
    // 2026-10-21: Android 1.17.59 (тестовая) — то же, что на Windows в 1.17.59, без пунктов только для компьютера.
    // 2026-10-11: Android 1.17.58 (после 1.17.53 — всё, что вышло на Windows в 1.17.54–1.17.58).
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "Генератор штрихкода в карточке товара и закупке: код не начинается с 0 и 2 и не повторяет коды каталога",
        "Скан в открытой карточке товара больше не срабатывает в окне «Склад»; поле «Штрихкод» в карточке шире",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Товардын карточкасында жана сатып алууда штрихкод генератору: код 0 жана 2 менен башталбайт, каталогдогу коддорду кайталабайт",
        "Товардын ачык карточкасындагы скан «Кампа» терезесинде иштебейт; карточкадагы «Штрихкод» талаасы кеңирээк",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Barcode generator in the product card and purchasing: the code never starts with 0 or 2 and never repeats catalog codes",
        "A scan in an open product card no longer triggers in the “Stock” window; the “Barcode” field in the card is wider",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Ürün kartında ve satın almada barkod üretici: kod 0 ve 2 ile başlamaz, katalog kodlarını tekrarlamaz",
        "Açık ürün kartındaki tarama artık «Depo» penceresinde çalışmaz; kartta «Barkod» alanı daha geniş",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Mahsulot kartasida va xaridda shtrix-kod generatori: kod 0 va 2 bilan boshlanmaydi, katalogdagi kodlarni takrorlamaydi",
        "Ochiq mahsulot kartasidagi skan endi «Ombor» oynasida ishlamaydi; kartadagi «Shtrix-kod» maydoni kengroq",
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
