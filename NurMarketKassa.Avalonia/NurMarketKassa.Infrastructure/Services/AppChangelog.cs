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
        "ИИ-советник меняет товары после вашего «Выполнить»: приход, списание, остаток, цены, срок годности, описание, страна, бренд, категория, штрихкод, фото; сам находит сведения о товаре в интернете и показывает на складе, что изменил. То же — в Telegram-боте и голосом",
        "Накладная по фото: 📎 в ИИ-советнике — советник читает товары и закупку, ставит цены с наценкой не ниже 20 % и оприходует на склад; у новых товаров штрихкод — сканером",
        "Склад: плитки сводки с фильтрами, фильтр «Наличие», срок годности в строке, кнопка «±» — приход, списание, точное количество",
        "Программа владельца: поиск раздела (Ctrl+K), группы меню сворачиваются, в «Сводке» — «к вчера на это время»; ИИ знает список сотрудников, их зарплату, продажи и табель (смены, часы)",
        "Отложенный чек больше не пропадает при закрытии смены; после досылки офлайн-продаж касса пишет, что очередь отправлена",
        "Режим «Одежда и обувь»: обмен по чеку, срок обмена и брак, штрихкод размера, приёмка сеткой «цвет × размер», отчёт «Размеры и цвета». В «Продуктах» всё как раньше",
        "Перемещение в филиал со склада — на сервере NurCRM, с отменой; раздел «Филиалы»",
        "Долг со сроком или рассрочкой, прокат с датами и штрафом в чеке; бонусы покупателей и настройки аккаунта — на сервере NurCRM, общие для всех касс",
        "«Финансы» и «Продажи» у владельца снова загружаются (было «Нет связи с сервером»)",
    ];

    public static readonly string[] LatestKy =
    [
        "ИИ-кеңешчи товарларды сиздин «Аткаруу» баскычыңыздан кийин өзгөртөт: кириш, эсептен чыгаруу, калдык, баалар, жарамдуулук мөөнөтү, сүрөттөмө, өлкө, бренд, категория, штрихкод, сүрөт; товар тууралуу маалыматты интернеттен өзү табат жана эмнени өзгөрткөнүн кампада көрсөтөт. Ошол эле — Telegram-ботто жана үн менен",
        "Накладнойду сүрөт менен: ИИ-кеңешчидеги 📎 — кеңешчи товарларды жана сатып алуу баасын окуйт, баасын 20 %дан кем эмес үстөк менен коюп, кампага кириштейт; жаңы товарлардын штрихкоду — сканер менен",
        "Кампа: чыпкалуу жыйынтык плиткалары, «Бар болушу» чыпкасы, сапта жарамдуулук мөөнөтү, «±» баскычы — кириш, эсептен чыгаруу, так саны",
        "Ээсинин программасы: бөлүмдү издөө (Ctrl+K), меню топтору жыйналат, «Жыйынтыкта» — «кечээ ушул убакка салыштырмалуу»; ИИ кызматкерлердин тизмесин, эмгек акысын, сатууларын жана табелин (сменалар, сааттар) билет",
        "Калтырылган чек сменаны жапканда жоголбойт; офлайн сатуулар жөнөтүлгөндөн кийин касса кезек жөнөтүлдү деп жазат",
        "«Кийим жана бут кийим» режими: чек боюнча алмаштыруу, алмаштыруу мөөнөтү жана брак, өлчөмдүн штрихкоду, «түс × өлчөм» торчосу менен кабыл алуу, «Өлчөмдөр жана түстөр» отчету. «Азык-түлүктө» баары мурункудай",
        "Кампадан филиалга жылдыруу — NurCRM серверинде, жокко чыгаруу менен; «Филиалдар» бөлүмү",
        "Мөөнөтү же бөлүп төлөөсү бар карыз, чекте даталары жана айыбы менен прокат; сатып алуучулардын бонустары жана аккаунттун жөндөөлөрү — NurCRM серверинде, бардык кассаларга жалпы",
        "Ээсинин программасындагы «Финансы» жана «Сатуулар» кайра жүктөлөт (мурда «Сервер менен байланыш жок» болчу)",
    ];

    public static readonly string[] LatestEn =
    [
        "The AI advisor changes products after you press “Do it”: receipt, write-off, stock, prices, expiry date, description, country, brand, category, barcode, photo; it finds product details on the web itself and shows in the warehouse what it changed. The same works in the Telegram bot and by voice",
        "Invoice by photo: 📎 in the AI advisor — the advisor reads the items and purchase prices, sets prices with a markup of at least 20% and receives them into stock; barcodes of new products — with the scanner",
        "Warehouse: summary tiles with filters, a “Stock” filter, expiry date in the row, a “±” button — receive, write off, exact quantity",
        "Owner program: section search (Ctrl+K), collapsible menu groups, “vs yesterday at this time” in “Overview”; the AI knows the staff list, their salaries, sales and timesheet (shifts, hours)",
        "A held receipt is no longer lost when the shift is closed; after offline sales are sent the till says the queue has been sent",
        "“Clothing and shoes” mode: exchange by receipt, exchange period and defects, size barcodes, receiving by a “color × size” grid, the “Sizes and colors” report. In “Groceries” everything stays as before",
        "Transfer to a branch from the warehouse — on the NurCRM server, with cancel; a “Branches” section",
        "Debt with a due date or installments, rentals with dates and a late fee on the receipt; customer bonuses and account settings are on the NurCRM server, shared by all tills",
        "“Finance” and “Sales” in the owner program load again (they showed “No connection to the server”)",
    ];

    public static readonly string[] LatestTr =
    [
        "Yapay zekâ danışmanı ürünleri sizin «Uygula» onayınızdan sonra değiştirir: giriş, düşüm, stok, fiyatlar, son kullanma tarihi, açıklama, ülke, marka, kategori, barkod, fotoğraf; ürün bilgisini internette kendisi bulur ve neyi değiştirdiğini depoda gösterir. Aynısı Telegram botunda ve sesle",
        "Fotoğraftan fatura: yapay zekâ danışmanında 📎 — danışman ürünleri ve alış fiyatlarını okur, fiyatları en az %20 kâr payıyla koyar ve stoğa alır; yeni ürünlerin barkodu — okuyucuyla",
        "Depo: filtreli özet kutucukları, «Stok» filtresi, satırda son kullanma tarihi, «±» düğmesi — giriş, düşüm, tam miktar",
        "Sahip programı: bölüm arama (Ctrl+K), menü grupları daraltılır, «Özet»te «dün bu saate göre»; yapay zekâ çalışan listesini, maaşlarını, satışlarını ve puantajını (vardiyalar, saatler) bilir",
        "Bekleyen fiş vardiya kapatılınca artık kaybolmaz; çevrimdışı satışlar gönderilince kasa sıranın gönderildiğini yazar",
        "«Giyim ve ayakkabı» modu: fişle değişim, değişim süresi ve kusurlu ürün, beden barkodu, «renk × beden» tablosuyla mal kabul, «Bedenler ve renkler» raporu. «Gıda» modunda her şey eskisi gibi",
        "Depodan şubeye transfer — NurCRM sunucusunda, iptal edilebilir; «Şubeler» bölümü",
        "Vadeli veya taksitli borç, fişte tarihleri ve gecikme cezasıyla kiralama; müşteri bonusları ve hesap ayarları NurCRM sunucusunda, tüm kasalarda ortak",
        "Sahip programındaki «Finans» ve «Satışlar» yeniden yükleniyor (önce «Sunucuyla bağlantı yok» gösteriyordu)",
    ];

    public static readonly string[] LatestUz =
    [
        "SI maslahatchi mahsulotlarni sizning «Bajarish» tasdig'ingizdan keyin o'zgartiradi: kirim, hisobdan chiqarish, qoldiq, narxlar, yaroqlilik muddati, tavsif, mamlakat, brend, kategoriya, shtrix-kod, rasm; mahsulot haqidagi ma'lumotni internetdan o'zi topadi va nimani o'zgartirganini omborda ko'rsatadi. Xuddi shunday — Telegram-botda va ovoz bilan",
        "Yuk xati rasm orqali: SI maslahatchidagi 📎 — maslahatchi mahsulotlar va xarid narxini o'qiydi, narxni kamida 20 % ustama bilan qo'yadi va omborga kirim qiladi; yangi mahsulotlar shtrix-kodi — skaner bilan",
        "Ombor: filtrli xulosa plitkalari, «Mavjudlik» filtri, qatorda yaroqlilik muddati, «±» tugmasi — kirim, hisobdan chiqarish, aniq miqdor",
        "Ega dasturi: bo'limni qidirish (Ctrl+K), menyu guruhlari yig'iladi, «Umumiy ko'rinish»da «kechagi shu vaqtga nisbatan»; SI xodimlar ro'yxati, ish haqi, sotuvlari va tabelini (smenalar, soatlar) biladi",
        "Kutishdagi chek smena yopilganda endi yo'qolmaydi; oflayn sotuvlar yuborilgach kassa navbat yuborilganini yozadi",
        "«Kiyim va poyabzal» rejimi: chek bo'yicha almashtirish, almashtirish muddati va nuqsonli tovar, o'lcham shtrix-kodi, «rang × o'lcham» jadvali bilan qabul, «O'lchamlar va ranglar» hisoboti. «Oziq-ovqat»da hammasi avvalgidek",
        "Ombordan filialga ko'chirish — NurCRM serverida, bekor qilish bilan; «Filiallar» bo'limi",
        "Muddatli yoki bo'lib to'lanadigan qarz, chekda sanalar va jarima bilan ijara; xaridorlar bonuslari va akkaunt sozlamalari — NurCRM serverida, barcha kassalar uchun umumiy",
        "Ega dasturidagi «Moliya» va «Sotuvlar» yana yuklanadi (avval «Server bilan aloqa yo'q» chiqardi)",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "Обновление прямо из программы: «Настройки → Обновления → Проверить обновления» находит новую версию, «Обновить» скачивает и открывает установку",
        "Долги клиентов подробно: раздел «Долги клиентов», карточка в «Сводке» и вкладка «Долги» в «Аналитике», напоминание в WhatsApp",
        "Возврат работает на тарифе «Старт»; функции «Стандарта» можно подключить на «Старте» по отдельности, в «Аккаунте» — сравнение тарифов",
        "«Настройки → Экран → Разделы меню»: ненужные разделы можно скрыть; при закупке дороже продажи — красное предупреждение",
        "Чековый принтер 80 мм на 42 символа (XP-80 и похожие); покупатель с QR из приложения NurCRM находится сам",
        "ИИ-советник: новый вид, история разговоров, «Стоп», поиск в интернете; меню программы владельца открывает раздел с первого нажатия",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Программадан эле жаңыртуу: «Жөндөөлөр → Жаңыртуулар → Жаңыртууларды текшерүү» жаңы версияны табат, «Жаңыртуу» жүктөп, орнотууну ачат",
        "Кардарлардын карыздары толук: «Кардарлардын карыздары» бөлүмү, «Жыйынтыктагы» карточка жана «Талдоодогу» «Карыздар» өтмөгү, WhatsApp'та эскертүү",
        "Кайтаруу «Старт» тарифинде иштейт; «Стандарттын» функцияларын «Стартта» өзүнчө кошууга болот, «Аккаунтта» — тарифтерди салыштыруу",
        "«Жөндөөлөр → Экран → Меню бөлүмдөрү»: керексиз бөлүмдөрдү жашырса болот; сатып алуу баасы сатуудан кымбат болсо — кызыл эскертүү",
        "80 мм, 42 белгилүү чек принтери (XP-80 ж.б.); NurCRM тиркемесиндеги QR менен кардар өзү табылат",
        "ИИ-кеңешчи: жаңы көрүнүш, маектердин тарыхы, «Токтотуу», интернеттен издөө; ээсинин программасынын менюсу бөлүмдү биринчи басуудан ачат",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Update right from the app: “Settings → Updates → Check for updates” finds a new version, “Update” downloads it and opens the installer",
        "Customer debts in detail: the “Customer debts” section, a card in “Overview” and a “Debts” tab in “Analytics”, WhatsApp reminders",
        "Returns work on the “Start” plan; “Standard” features can be added to “Start” one by one, “Account” compares the plans",
        "“Settings → Screen → Menu sections”: hide sections you don't need; a red warning when the purchase price is above the selling price",
        "80 mm receipt printers with 42 characters (XP-80 and similar); a customer with a QR from the NurCRM app is found automatically",
        "AI advisor: new look, chat history, “Stop”, web search; the owner app menu opens a section on the first tap",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Programdan doğrudan güncelleme: «Ayarlar → Güncellemeler → Güncellemeleri kontrol et» yeni sürümü bulur, «Güncelle» indirir ve yükleyiciyi açar",
        "Müşteri borçları ayrıntılı: «Müşteri borçları» bölümü, «Özet»te kart ve «Analiz»de «Borçlar» sekmesi, WhatsApp hatırlatması",
        "İade «Start» tarifesinde çalışır; «Standart» özellikleri «Start»a tek tek eklenebilir, «Hesap»ta tarife karşılaştırması",
        "«Ayarlar → Ekran → Menü bölümleri»: gereksiz bölümler gizlenebilir; alış fiyatı satıştan yüksekse kırmızı uyarı",
        "80 mm, 42 karakterlik fiş yazıcıları (XP-80 ve benzerleri); NurCRM uygulamasındaki QR ile müşteri kendiliğinden bulunur",
        "Yapay zekâ danışmanı: yeni görünüm, sohbet geçmişi, «Durdur», internet araması; sahip programının menüsü bölümü ilk dokunuşta açar",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Dasturning o'zidan yangilash: «Sozlamalar → Yangilanishlar → Yangilanishlarni tekshirish» yangi versiyani topadi, «Yangilash» yuklab olib, o'rnatishni ochadi",
        "Mijozlar qarzlari batafsil: «Mijozlar qarzlari» bo'limi, «Umumiy ko'rinish»da kartochka va «Analitika»da «Qarzlar» yorlig'i, WhatsApp'da eslatish",
        "Qaytarish «Start» tarifida ishlaydi; «Standart» funksiyalarini «Start»ga alohida ulash mumkin, «Akkaunt»da tariflarni solishtirish",
        "«Sozlamalar → Ekran → Menyu bo'limlari»: keraksiz bo'limlarni yashirish mumkin; xarid narxi sotuvdan qimmat bo'lsa — qizil ogohlantirish",
        "80 mm, 42 belgili chek printerlari (XP-80 va shunga o'xshash); NurCRM ilovasidagi QR bilan mijoz o'zi topiladi",
        "SI-maslahatchi: yangi ko'rinish, suhbatlar tarixi, «To'xtatish», internetda qidirish; egasi dasturi menyusi bo'limni birinchi bosishda ochadi",
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
