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
        "Исправлено: при выборе вида кассы «Профи» или «Карточки» появлялась «Ошибка в программе»",
        "Создание товара сканом, как на сайте: из базы NurCRM сразу подставляются название, категория и бренд; поиск быстрее, найденное запоминается",
        "Продажа в убыток: у товара в чеке «убыток N сом», если со скидкой он дешевле закупки; предупреждение кассиру; раздел владельца «Продажи в убыток»",
        "Документы из чека на A4: товарный чек и накладная — в «Истории чеков» и в «Продажах»",
        "Опт: кнопка «Опт» у товара в чеке и «Опт на весь чек» в «Ещё» (по оптовой цене из карточки)",
        "Предоплата при продаже в долг: выбор «Наличные / Безнал» (включается в настройках)",
        "Сканер находит товар с 12-значным кодом, если в каталоге он записан с нулём впереди; название нового товара — сразу из базы NurCRM",
        "Приёмка: «Убрать» работает и при открытой правке ячейки; поле количества — не больше 5 знаков, скан в нём не превращается в количество",
        "Телеграм-бот: кнопки в стиле Telegram, видно, работает ли ИИ на сервере; бот понимает «какие одежды есть», «прокат джинсов»",
    ];

    public static readonly string[] LatestKy =
    [
        "Оңдолду: «Профи» же «Карточкалар» касса көрүнүшүн тандаганда «Программада ката» чыгып жатты",
        "Товарды скан менен түзүү, сайттагыдай: NurCRM базасынан аталышы, категориясы жана бренди дароо толот; издөө тезирээк, табылганы эстеп калат",
        "Зыян менен сатуу: арзандатуу менен товар сатып алуу баасынан арзан болсо, чекте «зыян N сом»; кассирге эскертүү; ээсинин «Зыян менен сатуулар» бөлүмү",
        "Чектен A4 документтер: товардык чек жана накладной — «Чектердин тарыхында» жана «Сатууларда»",
        "Дүң: чектеги товардын «Дүң» баскычы жана «Дагы» ичинде «Бүт чекке дүң» (карточкадагы дүң баа боюнча)",
        "Карызга сатууда алдын ала төлөм: «Накталай / Накталай эмес» тандоо (жөндөөлөрдөн күйгүзүлөт)",
        "Сканер каталогдо алдында нөлү менен жазылган 12 орундуу коддуу товарды табат; жаңы товардын аталышы — дароо NurCRM базасынан",
        "Кабыл алуу: уячаны оңдоо ачык болсо да «Алып салуу» иштейт; сан талаасы — 5 белгиден ашпайт, скан санга айланбайт",
        "Телеграм-бот: Telegram стилиндеги баскычтар, серверде ЖИ иштеп жатабы — көрүнөт; бот «кандай кийим бар», «джинсы прокаты» дегенди түшүнөт",
    ];

    public static readonly string[] LatestEn =
    [
        "Fixed: choosing the “Pro” or “Cards” till view showed “An error occurred in the program”",
        "Creating a product by scan, as on the website: the name, category and brand come from the NurCRM base right away; faster search, found items are remembered",
        "Selling at a loss: a “loss N som” mark on the item when the discount takes it below cost; a warning for the cashier; owner section “Sales at a loss”",
        "A4 documents from a receipt: sales receipt and waybill — in “Receipt history” and “Sales”",
        "Wholesale: a “Wholesale” button on a receipt line and “Wholesale for receipt” in “More” (wholesale price from the product card)",
        "Prepayment on credit sales: choose “Cash / Cashless” (turned on in settings)",
        "The scanner finds a product with a 12-digit code stored with a leading zero; a new product’s name comes from the NurCRM base right away",
        "Receiving: “Remove” works even while a cell is being edited; the quantity field takes up to 5 characters, a scan there doesn’t become a quantity",
        "Telegram bot: Telegram-style buttons, you can see whether the AI works on the server; the bot understands “what clothes do you have”, “jeans rental”",
    ];

    public static readonly string[] LatestTr =
    [
        "Düzeltildi: «Profi» veya «Kartlar» kasa görünümü seçilince «Programda hata» çıkıyordu",
        "Web sitesindeki gibi okutarak ürün oluşturma: ad, kategori ve marka hemen NurCRM tabanından gelir; arama daha hızlı, bulunanlar hatırlanır",
        "Zararına satış: indirimle ürün alış fiyatının altına düşerse fişte «zarar N som»; kasiyere uyarı; işletme sahibi bölümü «Zararına satışlar»",
        "Fişten A4 belgeler: satış fişi ve irsaliye — «Fiş geçmişi» ve «Satışlar» içinde",
        "Toptan: fiş satırında «Toptan» düğmesi ve «Diğer» içinde «Tüm fişe toptan» (karttaki toptan fiyatla)",
        "Veresiye satışta ön ödeme: «Nakit / Nakitsiz» seçimi (ayarlardan açılır)",
        "Tarayıcı, katalogda başında sıfırla kayıtlı 12 haneli kodlu ürünü bulur; yeni ürünün adı hemen NurCRM tabanından gelir",
        "Mal kabul: hücre düzenlenirken de «Kaldır» çalışır; miktar alanı en fazla 5 karakter, orada okutulan barkod miktar olmaz",
        "Telegram botu: Telegram tarzı düğmeler, yapay zekânın sunucuda çalışıp çalışmadığı görünür; bot «hangi giysiler var», «kot kiralama» sorularını anlar",
    ];

    public static readonly string[] LatestUz =
    [
        "Tuzatildi: «Profi» yoki «Kartochkalar» kassa ko'rinishi tanlanganda «Dasturda xato» chiqardi",
        "Saytdagidek skan orqali mahsulot yaratish: nomi, kategoriyasi va brendi darhol NurCRM bazasidan keladi; qidiruv tezroq, topilganlar eslab qolinadi",
        "Zarariga sotish: chegirma bilan mahsulot xarid narxidan arzon bo'lsa, chekda «zarar N so'm»; kassirga ogohlantirish; egasi bo'limi «Zarariga sotuvlar»",
        "Chekdan A4 hujjatlar: tovar cheki va yuk xati — «Cheklar tarixi» va «Sotuvlar»da",
        "Ulgurji: chek qatorida «Ulgurji» tugmasi va «Yana»da «Butun chekka ulgurji» (kartadagi ulgurji narx bo'yicha)",
        "Qarzga sotishda oldindan to'lov: «Naqd / Naqdsiz» tanlovi (sozlamalarda yoqiladi)",
        "Skaner katalogda oldida nol bilan yozilgan 12 xonali kodli mahsulotni topadi; yangi mahsulot nomi — darhol NurCRM bazasidan",
        "Qabul qilish: katak tahrirlanayotganda ham «Olib tashlash» ishlaydi; miqdor maydoni — 5 belgidan oshmaydi, u yerdagi skan miqdorga aylanmaydi",
        "Telegram bot: Telegram uslubidagi tugmalar, serverda SI ishlayaptimi — ko'rinadi; bot «qanday kiyimlar bor», «jinsi prokati»ni tushunadi",
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
