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
        "Опт: тумблер «Розничный / Оптовый» над чеком и галочка «Опт» у товара; оптовая цена больше не теряется после перезапуска и после продажи",
        "Скидка в убыток: окно «Продажа в убыток» с кнопкой «Я знаю что делаю» — без подтверждения скидка не применяется",
        "Окно скидки помнит выбранный режим — процент или сом",
        "Чек «в долг»: печатается, как внесена предоплата — «наличными» или «безналом»",
        "Отчёт закрытия смены: блок «Прокат за смену» — выдано, возвращено, залоги, штрафы, на руках",
        "Склад: кнопка «Обновить» — товары и остатки заново с сервера",
        "Новый товар: поле PLU весового товара больше не сдвигает цены вниз",
        "Касса больше не выходит из аккаунта, если сервер NurCRM временно не отвечает",
        "Остаток товара уменьшается сразу после продажи",
    ];

    public static readonly string[] LatestKy =
    [
        "Дүң: чектин үстүндө «Чекене / Дүң» которгуч жана товардагы «Дүң» белгиси; дүң баа кайра жүргүзгөндөн жана сатуудан кийин жоголбойт",
        "Зыянга арзандатуу: «Эмне кылып жатканымды билем» баскычы менен эскертүү — ырастабасаңыз арзандатуу колдонулбайт",
        "Арзандатуу терезеси тандалган режимди эстейт — пайыз же сом",
        "«Карызга» чек: алдын ала төлөм кантип берилгени басылат — накталай же накталай эмес",
        "Сменаны жабуу отчёту: «Сменадагы прокат» — берилди, кайтарылды, күрөөлөр, айыптар, колдо",
        "Кампа: «Жаңылоо» баскычы — товарлар жана калдыктар серверден кайра",
        "Жаңы товар: салмактуу товардын PLU талаасы бааларды ылдый түртпөйт",
        "NurCRM сервери убактылуу жооп бербесе, касса аккаунттан чыгып кетпейт",
        "Товардын калдыгы сатуудан кийин дароо азаят",
    ];

    public static readonly string[] LatestEn =
    [
        "Wholesale: a “Retail / Wholesale” switch above the receipt and a “Wholesale” tick on each item; the wholesale price is no longer lost after a restart or a sale",
        "Discount below cost: a “Selling at a loss” window with an “I know what I'm doing” button — without it the discount is not applied",
        "The discount window remembers the chosen mode — percent or som",
        "Credit receipt: the prepayment method is printed — “cash” or “non-cash”",
        "Shift closing report: a “Rentals this shift” block — issued, returned, deposits, penalties, still out",
        "Warehouse: a “Refresh” button — products and stock reloaded from the server",
        "New product: the PLU field of a weighed product no longer pushes the prices down",
        "The till no longer logs out when the NurCRM server is temporarily down",
        "Stock goes down right after a sale",
    ];

    public static readonly string[] LatestTr =
    [
        "Toptan: fişin üstünde «Perakende / Toptan» anahtarı ve üründe «Toptan» işareti; toptan fiyat yeniden başlatmadan ve satıştan sonra kaybolmuyor",
        "Zararına indirim: «Ne yaptığımı biliyorum» düğmeli uyarı — onay olmadan indirim uygulanmaz",
        "İndirim penceresi seçilen modu hatırlar — yüzde veya som",
        "Veresiye fişi: ön ödemenin nasıl alındığı yazdırılır — «nakit» veya «nakitsiz»",
        "Vardiya kapanış raporu: «Vardiyadaki kiralamalar» — verilen, iade edilen, depozitolar, cezalar, dışarıdakiler",
        "Depo: «Yenile» düğmesi — ürünler ve stoklar sunucudan yeniden",
        "Yeni ürün: tartılı ürünün PLU alanı artık fiyatları aşağı itmiyor",
        "NurCRM sunucusu geçici olarak yanıt vermezse kasa hesaptan çıkmıyor",
        "Ürün stoku satıştan hemen sonra azalır",
    ];

    public static readonly string[] LatestUz =
    [
        "Ulgurji: chek ustida «Chakana / Ulgurji» tugmasi va mahsulotda «Ulgurji» belgisi; ulgurji narx qayta ishga tushirish va sotuvdan keyin yo'qolmaydi",
        "Zarariga chegirma: «Nima qilayotganimni bilaman» tugmali ogohlantirish — tasdiqsiz chegirma qo'llanmaydi",
        "Chegirma oynasi tanlangan rejimni eslab qoladi — foiz yoki so'm",
        "«Qarzga» chek: oldindan to'lov qanday qabul qilingani chop etiladi — «naqd» yoki «naqdsiz»",
        "Smena yopilishi hisoboti: «Smenadagi prokat» — berildi, qaytarildi, garovlar, jarimalar, qo'lda",
        "Ombor: «Yangilash» tugmasi — mahsulotlar va qoldiqlar serverdan qayta",
        "Yangi mahsulot: vaznli mahsulotning PLU maydoni narxlarni pastga surmaydi",
        "NurCRM serveri vaqtincha javob bermasa, kassa akkauntdan chiqib ketmaydi",
        "Mahsulot qoldig'i sotuvdan keyin darhol kamayadi",
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
