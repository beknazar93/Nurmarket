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
        "Денежный ящик открывается после каждой продажи наличными — сразу после печати чека; чек больше не обрывается на середине",
        "Нур Советник: накладная на нескольких фото или PDF (до 10 страниц) читается как одна; «Отредактировать» — таблица: название в накладной и в чеке, количество, закупка, продажа",
        "Склад, приёмка: товары выбранного поставщика с последней закупкой; ✕ у строки и «Очистить список»; поле «Принято» можно стереть; подсказки закрываются по Esc и ✕. Ревизия: «Очистить список» и ✕ у строки",
        "Окно товара: вкладки «Основное» (с горячей клавишей и оптовой ценой), «Доп. штрих-коды» (с артикулом, штрихкод можно исправить ✎), «Упаковка» с минимальным остатком (хранится на сервере), «Описание» с категорией и брендом",
        "Смена: сумма при открытии и закрытии не может быть отрицательной, при закрытии нужна фактическая сумма наличных (0, если их нет)",
        "Исправлено: двойное нажатие в возврате; при обрыве связи скан неизвестного штрихкода не предлагает создать дубль товара; пустое перемещение не остаётся в списке; «Обновить» на складе показывает загрузку",
    ];

    public static readonly string[] LatestKy =
    [
        "Акча кутусу накталай ар бир сатуудан кийин ачылат — чек басылгандан кийин дароо; чек ортосунан үзүлбөйт",
        "Нур Кеңешчи: бир нече сүрөттөгү же PDFтеги (10 баракка чейин) накладной бир накладной катары окулат; «Оңдоо» — таблица: накладнойдогу жана чектеги аталыш, саны, сатып алуу, сатуу",
        "Кампа, кабыл алуу: тандалган жеткирүүчүнүн товарлары акыркы баасы менен; саптагы ✕ жана «Тизмени тазалоо»; «Кабыл алынды» талаасын өчүрсө болот; сунуштар Esc жана ✕ менен жабылат. Ревизия: «Тизмени тазалоо» жана саптагы ✕",
        "Товардын терезеси: «Негизги» (ысык баскыч жана дүң баа менен), «Кошумча штрихкоддор» (артикул менен, штрихкодду ✎ менен оңдосо болот), минималдуу калдык менен «Таңгак» (серверде сакталат), категория жана бренд менен «Сүрөттөмө»",
        "Смена: ачууда жана жабууда сумма терс боло албайт, жабууда нак акчанын чыныгы суммасы керек (жок болсо 0)",
        "Оңдолду: кайтарууда эки жолу басуу; байланыш үзүлгөндө белгисиз штрихкодду сканерлөө товардын дублун түзүүнү сунуштабайт; бош жылдыруу тизмеде калбайт; кампадагы «Жаңыртуу» жүктөөнү көрсөтөт",
    ];

    public static readonly string[] LatestEn =
    [
        "The cash drawer opens after every cash sale — right after the receipt is printed; the receipt is no longer cut off in the middle",
        "Nur Advisor: an invoice on several photos or PDFs (up to 10 pages) is read as one; “Edit” — a table: name on the invoice and on the receipt, quantity, purchase and selling price",
        "Warehouse, receiving: products of the chosen supplier with the last purchase price; ✕ on a row and “Clear list”; the “Received” field can be erased; suggestions close with Esc and ✕. Stocktake: “Clear list” and ✕ on a row",
        "Product window: “Main” (with hotkey and wholesale price), “Extra barcodes” (with article, a barcode can be corrected with ✎), “Packaging” with minimum stock (kept on the server), “Description” with category and brand",
        "Shift: the amount at opening and closing cannot be negative; closing needs the counted cash (0 if there is none)",
        "Fixed: double tap in returns; when the connection drops, scanning an unknown barcode no longer offers to create a duplicate product; an empty transfer no longer stays in the list; “Refresh” in the warehouse shows loading",
    ];

    public static readonly string[] LatestTr =
    [
        "Para çekmecesi her nakit satıştan sonra açılır — fiş basıldıktan hemen sonra; fiş artık ortasında kesilmez",
        "Nur Danışman: birkaç fotoğraf veya PDF'teki (10 sayfaya kadar) fatura tek fatura olarak okunur; «Düzenle» — tablo: faturadaki ve fişteki ad, miktar, alış, satış",
        "Depo, mal kabul: seçilen tedarikçinin ürünleri son alış fiyatıyla; satırda ✕ ve «Listeyi temizle»; «Kabul edilen» alanı silinebilir; öneriler Esc ve ✕ ile kapanır. Sayım: «Listeyi temizle» ve satırda ✕",
        "Ürün penceresi: «Ana» (kısayol tuşu ve toptan fiyatla), «Ek barkodlar» (stok koduyla, barkod ✎ ile düzeltilebilir), minimum stoklu «Ambalaj» (sunucuda saklanır), kategori ve markalı «Açıklama»",
        "Vardiya: açılış ve kapanış tutarı negatif olamaz; kapanışta sayılan nakit gerekir (yoksa 0)",
        "Düzeltildi: iadede çift dokunma; bağlantı kesilince bilinmeyen barkodu okutmak ürün kopyası oluşturmayı önermez; boş transfer listede kalmaz; depoda «Yenile» yüklemeyi gösterir",
    ];

    public static readonly string[] LatestUz =
    [
        "Pul qutisi har bir naqd sotuvdan keyin ochiladi — chek bosilgandan keyin darhol; chek endi o'rtasida uzilmaydi",
        "Nur Maslahatchi: bir nechta rasm yoki PDFdagi (10 sahifagacha) nakladnoy bitta nakladnoy sifatida o'qiladi; «Tahrirlash» — jadval: nakladnoydagi va chekdagi nom, miqdor, xarid, sotuv",
        "Ombor, qabul qilish: tanlangan yetkazib beruvchining mahsulotlari oxirgi xarid narxi bilan; qatordagi ✕ va «Ro'yxatni tozalash»; «Qabul qilindi» maydonini o'chirish mumkin; takliflar Esc va ✕ bilan yopiladi. Reviziya: «Ro'yxatni tozalash» va qatordagi ✕",
        "Mahsulot oynasi: «Asosiy» (tezkor tugma va ulgurji narx bilan), «Qo'shimcha shtrix-kodlar» (artikul bilan, shtrix-kodni ✎ bilan tuzatish mumkin), minimal qoldiqli «Qadoq» (serverda saqlanadi), toifa va brendli «Tavsif»",
        "Smena: ochish va yopishda summa manfiy bo'lishi mumkin emas; yopishda sanalgan naqd pul kerak (bo'lmasa 0)",
        "Tuzatildi: qaytarishda ikki marta bosish; aloqa uzilganda noma'lum shtrix-kodni skanerlash mahsulot nusxasini yaratishni taklif qilmaydi; bo'sh ko'chirish ro'yxatda qolmaydi; ombordagi «Yangilash» yuklanishni ko'rsatadi",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-11: Android 1.17.58 (после 1.17.53 — всё, что вышло на Windows в 1.17.54–1.17.58).
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "ИИ-советник теперь «Нур Советник»: сам загружает данные и показывает таблицей (чеки за день или неделю, сотрудники, табель, зарплата, должники); разделы открывает по команде «открой»",
        "Нур Советник работает с товарами: приход, списание, цены, фото из интернета; накладная по фото — на нескольких страницах, с наценкой не ниже 20 % и редактором «Отредактировать»",
        "Новый склад: плитки остатков, кнопка «±», фото в строке; приёмка с товарами поставщика, ✕ у строки и «Очистить список»; ревизия — «Очистить список»",
        "Окно товара: вкладки «Основное», «Доп. штрих-коды» (исправление ✎), «Упаковка» с минимальным остатком, «Описание»; новый товар — штрихкод по названию из barcode-list.ru для сверки",
        "Редактор сайта: темы и публикация; «Смотреть сайт» открывает market.nurcrm.kg",
        "Денежный ящик открывается после печати чека каждый раз; смена не принимает отрицательную сумму; «Финансы» не пишут «Нет связи» при большом числе чеков",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "ИИ-кеңешчи эми «Нур Кеңешчи»: маалыматты өзү жүктөп, таблица менен көрсөтөт (күндүк же жумалык чектер, кызматкерлер, табель, эмгек акы, карыздар); бөлүмдөрдү «ач» буйругу менен ачат",
        "Нур Кеңешчи товарлар менен иштейт: кирим, чыгым, баалар, интернеттен сүрөт; сүрөт боюнча накладной — бир нече баракта, 20 %дан кем эмес үстөк баа жана «Оңдоо» редактору менен",
        "Жаңы кампа: калдыктардын плиткалары, «±» баскычы, саптагы сүрөт; жеткирүүчүнүн товарлары менен кабыл алуу, саптагы ✕ жана «Тизмени тазалоо»; ревизия — «Тизмени тазалоо»",
        "Товардын терезеси: «Негизги», «Кошумча штрихкоддор» (✎ менен оңдоо), минималдуу калдык менен «Таңгак», «Сүрөттөмө»; жаңы товар — салыштыруу үчүн barcode-list.ru'дан аталышы боюнча штрихкод",
        "Сайттын редактору: темалар жана жарыялоо; «Сайтты көрүү» market.nurcrm.kg ачат",
        "Акча кутусу ар дайым чек басылгандан кийин ачылат; смена терс сумманы кабыл албайт; чек көп болгондо «Финансылар» «Байланыш жок» деп жазбайт",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "The AI advisor is now “Nur Advisor”: it loads data itself and shows tables (receipts for a day or week, staff, timesheet, salary, debtors); it opens sections on an “open” command",
        "Nur Advisor works with products: receiving, write-offs, prices, photos from the web; an invoice by photo — on several pages, with a markup of at least 20 % and an “Edit” table",
        "New warehouse: stock tiles, the “±” button, a photo in the row; receiving with the supplier's products, ✕ on a row and “Clear list”; stocktake — “Clear list”",
        "Product window: “Main”, “Extra barcodes” (edit with ✎), “Packaging” with minimum stock, “Description”; a new product gets barcode suggestions by name from barcode-list.ru to check",
        "Website editor: themes and publishing; “View website” opens market.nurcrm.kg",
        "The cash drawer opens after the receipt is printed, every time; a shift does not accept a negative amount; “Finance” no longer says “No connection” with many receipts",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Yapay zekâ danışmanı artık «Nur Danışman»: verileri kendisi yükler ve tablo olarak gösterir (günün veya haftanın fişleri, personel, puantaj, maaş, borçlular); bölümleri «aç» komutuyla açar",
        "Nur Danışman ürünlerle çalışır: mal kabul, düşüm, fiyatlar, internetten fotoğraf; fotoğraftan fatura — birkaç sayfa, en az %20 kâr payı ve «Düzenle» tablosuyla",
        "Yeni depo: stok kutucukları, «±» düğmesi, satırda fotoğraf; tedarikçinin ürünleriyle mal kabul, satırda ✕ ve «Listeyi temizle»; sayım — «Listeyi temizle»",
        "Ürün penceresi: «Ana», «Ek barkodlar» (✎ ile düzeltme), minimum stoklu «Ambalaj», «Açıklama»; yeni ürün — karşılaştırmak için barcode-list.ru'dan ada göre barkod",
        "Site düzenleyici: temalar ve yayınlama; «Siteyi gör» market.nurcrm.kg'yi açar",
        "Para çekmecesi her seferinde fiş basıldıktan sonra açılır; vardiya negatif tutarı kabul etmez; çok fiş olduğunda «Finans» «Bağlantı yok» demez",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "SI maslahatchi endi «Nur Maslahatchi»: ma'lumotlarni o'zi yuklaydi va jadval qilib ko'rsatadi (kunlik yoki haftalik cheklar, xodimlar, tabel, ish haqi, qarzdorlar); bo'limlarni «och» buyrug'i bilan ochadi",
        "Nur Maslahatchi mahsulotlar bilan ishlaydi: kirim, hisobdan chiqarish, narxlar, internetdan rasm; rasm bo'yicha nakladnoy — bir nechta sahifada, kamida 20 % ustama va «Tahrirlash» jadvali bilan",
        "Yangi ombor: qoldiq plitkalari, «±» tugmasi, qatorda rasm; yetkazib beruvchi mahsulotlari bilan qabul, qatordagi ✕ va «Ro'yxatni tozalash»; reviziya — «Ro'yxatni tozalash»",
        "Mahsulot oynasi: «Asosiy», «Qo'shimcha shtrix-kodlar» (✎ bilan tuzatish), minimal qoldiqli «Qadoq», «Tavsif»; yangi mahsulot — solishtirish uchun barcode-list.ru dan nomi bo'yicha shtrix-kod",
        "Sayt muharriri: mavzular va e'lon qilish; «Saytni ko'rish» market.nurcrm.kg ni ochadi",
        "Pul qutisi har safar chek bosilgandan keyin ochiladi; smena manfiy summani qabul qilmaydi; cheklar ko'p bo'lsa «Moliya» «Aloqa yo'q» demaydi",
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
