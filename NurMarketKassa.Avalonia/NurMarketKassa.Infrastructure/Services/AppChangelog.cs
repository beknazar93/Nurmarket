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
        "ИИ-советник теперь «Нур Советник»: сам загружает данные с сервера и показывает таблицей — «чеки за вчера», «продажи за 6.10», «чеки за неделю»; в звонке таблица появляется в чате, итог — голосом",
        "Фото товара — сразу из интернета и точнее: подпись фото сверяется с названием товара, магазины первыми; в чате индикатор поиска; фото со склада к ответам больше не подставляются",
        "Разделы программы открываются только по команде «открой»; понимает кыргызские команды («кампаны ач»)",
        "Редактор сайта: голосом или текстом «поставь тему Ала-Тоо», «опубликуй сайт»; «Смотреть сайт» открывает market.nurcrm.kg — там видно новое оформление",
        "Новый товар: штрихкод по названию из открытой базы barcode-list.ru — несколько вариантов с названием из базы, сверяете и нажимаете нужный",
        "Исправлено: под ответом ИИ больше нет чужих ссылок «Найдено в интернете»",
    ];

    public static readonly string[] LatestKy =
    [
        "ИИ-кеңешчи эми «Нур Кеңешчи»: маалыматты серверден өзү жүктөп, таблица менен көрсөтөт — «кечээки чектер», «6.10 сатуулар», «жумалык чектер»; чалууда таблица чатта чыгат, жыйынтыгы — үн менен",
        "Товардын сүрөтү — дароо интернеттен жана тагыраак: сүрөттүн жазуусу товардын аты менен салыштырылат, дүкөндөр биринчи; чатта издөө белгиси; кампадагы сүрөттөр жоопко кошулбайт",
        "Программанын бөлүмдөрү «ач» деген буйрук менен гана ачылат; кыргызча буйруктарды түшүнөт («кампаны ач»)",
        "Сайттын редактору: үн же текст менен «Ала-Тоо темасын кой», «сайтты жарыяла»; «Сайтты көрүү» market.nurcrm.kg ачат — жаңы жасалгалоо ошол жерде көрүнөт",
        "Жаңы товар: аталышы боюнча barcode-list.ru ачык базасынан штрихкод — базадагы аталышы менен бир нече вариант, салыштырып керектүүсүн басасыз",
        "Оңдолду: ИИнин жообунун астында башка «Интернеттен табылды» шилтемелери чыкпайт",
    ];

    public static readonly string[] LatestEn =
    [
        "The AI advisor is now “Nur Advisor”: it loads data from the server itself and shows tables — “receipts for yesterday”, “sales on 6.10”, “receipts for the week”; in a call the table appears in the chat and the total is spoken",
        "Product photos — straight from the web and more accurate: the photo caption is checked against the product name, shops first; a search indicator in the chat; warehouse photos are no longer added to answers",
        "Program sections open only on an “open” command; Kyrgyz commands are understood (“кампаны ач”)",
        "Website editor: by voice or text “apply the Ala-Too theme”, “publish the website”; “View website” opens market.nurcrm.kg, where the new design is visible",
        "New product: barcode by name from the open barcode-list.ru database — a few options with the database name; you check and press the right one",
        "Fixed: unrelated “Found online” links no longer appear under AI answers",
    ];

    public static readonly string[] LatestTr =
    [
        "Yapay zekâ danışmanı artık «Nur Danışman»: verileri sunucudan kendisi yükler ve tablo olarak gösterir — «dünün fişleri», «6.10 satışları», «haftanın fişleri»; görüşmede tablo sohbette çıkar, özet sesle söylenir",
        "Ürün fotoğrafı — doğrudan internetten ve daha doğru: fotoğraf açıklaması ürün adıyla karşılaştırılır, mağazalar önce; sohbette arama göstergesi; depo fotoğrafları yanıtlara artık eklenmez",
        "Program bölümleri yalnızca «aç» komutuyla açılır; Kırgızca komutlar anlaşılır («кампаны ач»)",
        "Site düzenleyici: sesle veya yazıyla «Ala-Too temasını koy», «siteyi yayınla»; «Siteyi gör» market.nurcrm.kg'yi açar — yeni tasarım orada görünür",
        "Yeni ürün: ada göre açık barcode-list.ru veritabanından barkod — veritabanındaki adıyla birkaç seçenek; karşılaştırıp doğru olana basarsınız",
        "Düzeltildi: yapay zekâ yanıtlarının altında ilgisiz «İnternette bulundu» bağlantıları artık çıkmıyor",
    ];

    public static readonly string[] LatestUz =
    [
        "SI maslahatchi endi «Nur Maslahatchi»: ma'lumotlarni serverdan o'zi yuklaydi va jadval qilib ko'rsatadi — «kechagi cheklar», «6.10 sotuvlari», «haftalik cheklar»; qo'ng'iroqda jadval chatda chiqadi, yakuni — ovozda",
        "Mahsulot rasmi — to'g'ridan-to'g'ri internetdan va aniqroq: rasm izohi mahsulot nomi bilan solishtiriladi, do'konlar birinchi; chatda qidiruv belgisi; ombordagi rasmlar javoblarga endi qo'shilmaydi",
        "Dastur bo'limlari faqat «och» buyrug'i bilan ochiladi; qirg'izcha buyruqlarni tushunadi («кампаны ач»)",
        "Sayt muharriri: ovoz yoki matn bilan «Ala-Too mavzusini qo'y», «saytni e'lon qil»; «Saytni ko'rish» market.nurcrm.kg ni ochadi — yangi bezak o'sha yerda ko'rinadi",
        "Yangi mahsulot: nomi bo'yicha ochiq barcode-list.ru bazasidan shtrix-kod — bazadagi nomi bilan bir nechta variant; solishtirib keraklisini bosasiz",
        "Tuzatildi: SI javobi ostida begona «Internetda topildi» havolalari endi chiqmaydi",
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
