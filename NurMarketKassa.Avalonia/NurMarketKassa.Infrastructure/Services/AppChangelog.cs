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
        "Весы Штрих-ПРИНТ: новое окно настроек (Настройки → Весы → «Настройки весов…») — клавиатура, этикетка, штрих-код, тексты, часы — как в тест-драйвере Штрих-М",
        "При отправке на весы можно поправить код в штрих-коде у каждого товара и сразу видно пример этикетки, например 2000001003923",
        "Касса предупреждает, если формат штрих-кода на весах не совпадёт с настройкой компании и товар не найдётся при скане",
        "Виды кассы появились в Маркетплейсе (вкладка «Виды кассы»)",
        "Экран покупателя меняется вместе с видом кассы; добавлен редактор экрана покупателя",
        "Исправлено: короткий пароль весов и номер товара в штрих-коде при прямой отправке на весы",
    ];

    public static readonly string[] LatestKy =
    [
        "Штрих-ПРИНТ таразасы: жаңы жөндөөлөр терезеси (Жөндөөлөр → Таразалар → «Таразанын жөндөөлөрү…») — баскычтар, этикетка, штрих-код, тексттер, саат — Штрих-М тест-драйвериндегидей",
        "Таразага жөнөтүүдө ар бир товардын штрих-коддогу кодун оңдоого болот жана этикетканын үлгүсү дароо көрүнөт, мисалы 2000001003923",
        "Таразадагы штрих-коддун форматы компаниянын жөндөөсү менен дал келбесе жана товар сканерде табылбаса, касса эскертет",
        "Кассанын көрүнүштөрү Маркетплейске кошулду («Кассанын көрүнүштөрү» өтмөгү)",
        "Сатып алуучунун экраны кассанын көрүнүшү менен кошо өзгөрөт; сатып алуучунун экранынын редактору кошулду",
        "Оңдолду: таразанын кыска сырсөзү жана таразага түз жөнөтүүдө штрих-коддогу товардын номери",
    ];

    public static readonly string[] LatestEn =
    [
        "Shtrikh-PRINT scales: a new settings window (Settings → Scales → “Scale settings…”) — keyboard, label, barcode, texts, clock — like the Shtrikh-M test driver",
        "When sending to scales you can edit the code inside each product's barcode and see a sample label right away, e.g. 2000001003923",
        "The till warns you if the barcode format on the scales does not match the company setting and the product would not be found when scanned",
        "Till layouts are now in the Marketplace (“Till layouts” tab)",
        "The customer display changes together with the till layout; a customer display editor was added",
        "Fixed: short scale password and the product number in the barcode when sending directly to scales",
    ];

    public static readonly string[] LatestTr =
    [
        "Shtrikh-PRINT teraziler: yeni ayarlar penceresi (Ayarlar → Teraziler → «Terazi ayarları…») — tuş takımı, etiket, barkod, metinler, saat — Shtrikh-M test sürücüsündeki gibi",
        "Teraziye gönderirken her ürünün barkodundaki kodu düzenleyebilir ve örnek etiketi hemen görebilirsiniz, örneğin 2000001003923",
        "Terazideki barkod biçimi şirket ayarıyla uyuşmazsa ve ürün taramada bulunamayacaksa kasa uyarır",
        "Kasa görünümleri artık Pazaryeri'nde («Kasa görünümleri» sekmesi)",
        "Müşteri ekranı kasa görünümüyle birlikte değişir; müşteri ekranı düzenleyicisi eklendi",
        "Düzeltildi: kısa terazi şifresi ve teraziye doğrudan gönderimde barkoddaki ürün numarası",
    ];

    public static readonly string[] LatestUz =
    [
        "Shtrix-PRINT tarozilari: yangi sozlamalar oynasi (Sozlamalar → Tarozilar → «Tarozi sozlamalari…») — klaviatura, yorliq, shtrix-kod, matnlar, soat — Shtrix-M test drayveridagidek",
        "Taroziga yuborishda har bir mahsulotning shtrix-kodidagi kodni tahrirlash mumkin va yorliq namunasi darhol ko'rinadi, masalan 2000001003923",
        "Tarozidagi shtrix-kod formati kompaniya sozlamasiga mos kelmasa va mahsulot skanerda topilmasa, kassa ogohlantiradi",
        "Kassa ko'rinishlari Marketpleysga qo'shildi («Kassa ko'rinishlari» yorlig'i)",
        "Xaridor ekrani kassa ko'rinishi bilan birga o'zgaradi; xaridor ekrani muharriri qo'shildi",
        "Tuzatildi: tarozining qisqa paroli va taroziga to'g'ridan-to'g'ri yuborishda shtrix-koddagi mahsulot raqami",
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
