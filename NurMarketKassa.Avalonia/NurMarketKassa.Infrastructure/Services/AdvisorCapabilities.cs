using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-30, владелец: «Нур Советник: в настройках поставить тумблер (находить штрихкоды) и прочие его возможности, чтобы
/// можно было выключать и включать какую-то часть». Что делает возможность: читает данные, меняет данные (всегда через подтверждение)
/// или отправляет что-то наружу (ИИ-провайдеру, в интернет, в мессенджер).</summary>
public enum AdvisorRisk { Reads, Changes, SendsOut }

/// <summary>Одна возможность Нур Советника: id, группа, название и пояснение на 5 языках (ru, ky, en, tr, uz), риск, значение по умолчанию
/// (всегда «как было до тумблеров» — включено), нужно ли подтверждение владельца и право пользователя (<see cref="AiAccess"/>: Products,
/// Clients, Staff, Settings; null — без права).</summary>
public sealed record AdvisorCapability(string Id, string Group, AdvisorRisk Risk, bool DefaultOn, bool NeedsConfirm, string? Permission,
    string[] Title, string[] Hint)
{
    public string Name => Pick(Title);
    public string Description => Pick(Hint);

    private static string Pick(string[] v) => Tr.T(v[0], v[1], v[2], v[3], v[4]);
}

public sealed record AdvisorGroup(string Id, string[] Title, string[] Hint)
{
    public string Name => Tr.T(Title[0], Title[1], Title[2], Title[3], Title[4]);
    public string Description => Tr.T(Hint[0], Hint[1], Hint[2], Hint[3], Hint[4]);
}

/// <summary>2026-10-30: реестр и выключатели возможностей Нур Советника на десктопе. Хранение — одно общее поле компании
/// <see cref="UserPreferences.AdvisorCapabilitiesJson"/> (JSON «id → true/false», только отличия от значений по умолчанию): меняет
/// владелец или администратор, облако (SettingsCloudSync, CompanyKeys) раздаёт на все кассы, локальная копия работает без связи.
/// Выключенная возможность: (1) не показывается в окне (кнопки, подсказки, карточки), (2) не попадает в инструкцию ИИ и в данные,
/// которые уходят ИИ-провайдеру, (3) не вызывается из кода (разбор «ТОВАР:» / «НАПОМНИТЬ:» / «БОТ:», переходы, внешние запросы).
/// Пока ничего не менялось, все возможности включены — поведение прежнее.</summary>
public static class AdvisorCapabilities
{
    public const string Master = "advisor.enabled";

    private static string[] L(string ru, string ky, string en, string tr, string uz) => new[] { ru, ky, en, tr, uz };

    public static readonly IReadOnlyList<AdvisorGroup> Groups = new[]
    {
        new AdvisorGroup("read",
            L("Чтение данных и переходы", "Маалыматты окуу жана бөлүмдөргө өтүү", "Reading data and navigation", "Veri okuma ve bölümlere geçiş", "Ma'lumotlarni o'qish va bo'limlarga o'tish"),
            L("Советник отвечает по цифрам магазина и открывает разделы. Данные не меняются.", "Кеңешчи дүкөндүн сандары боюнча жооп берет жана бөлүмдөрдү ачат. Маалымат өзгөрбөйт.",
              "The advisor answers from the shop's figures and opens sections. Nothing is changed.", "Danışman mağaza rakamlarıyla yanıtlar ve bölümleri açar. Hiçbir şey değişmez.",
              "Maslahatchi do'kon raqamlari bo'yicha javob beradi va bo'limlarni ochadi. Ma'lumot o'zgarmaydi.")),
        new AdvisorGroup("act",
            L("Действия с данными (с подтверждением)", "Маалымат менен аракеттер (ырастоо менен)", "Actions on data (with confirmation)", "Veriyle işlemler (onayla)", "Ma'lumot bilan amallar (tasdiq bilan)"),
            L("Советник только предлагает — меняется после вашей кнопки «Выполнить». Исключение — команды сайту.", "Кеңешчи сунуштайт гана — «Аткаруу» баскычынан кийин өзгөрөт. Өзгөчөлүк — сайтка буйруктар.",
              "The advisor only suggests — changes happen after your “Run” button. Website commands are the exception.", "Danışman yalnızca önerir — değişiklik sizin «Uygula» düğmenizden sonra olur. İstisna: site komutları.",
              "Maslahatchi faqat taklif qiladi — o'zgarish sizning «Bajarish» tugmangizdan keyin bo'ladi. Istisno — sayt buyruqlari.")),
        new AdvisorGroup("barcode",
            L("Поиск штрихкодов и справочник", "Штрихкод издөө жана справочник", "Barcode search and directory", "Barkod arama ve rehber", "Shtrix-kod qidirish va ma'lumotnoma"),
            L("Название по штрихкоду из справочника NurCRM (128 тыс. товаров), поиск товара в интернете и запись штрихкода.", "NurCRM справочнигинен штрихкод боюнча аталыш (128 миң товар), интернеттен товар издөө жана штрихкод жазуу.",
              "Name by barcode from the NurCRM directory (128k products), web search for a product and writing a barcode.", "NurCRM rehberinden barkoda göre ad (128 bin ürün), internette ürün arama ve barkod yazma.",
              "NurCRM ma'lumotnomasidan shtrix-kod bo'yicha nom (128 ming mahsulot), internetda qidirish va shtrix-kod yozish.")),
        new AdvisorGroup("invoice",
            L("Накладные и фото", "Накладнойлор жана сүрөттөр", "Invoices and photos", "Faturalar ve fotoğraflar", "Yuk xatlari va rasmlar"),
            L("Фото и PDF накладной уходят ИИ на чтение; фото товаров ищутся в интернете.", "Накладнойдун сүрөтү жана PDF окуу үчүн ИИге кетет; товардын сүрөттөрү интернеттен изделет.",
              "Invoice photos and PDFs go to the AI to be read; product photos are searched on the web.", "Fatura fotoğrafı ve PDF okunması için yapay zekâya gider; ürün fotoğrafları internette aranır.",
              "Yuk xati rasmi va PDF o'qish uchun SIga ketadi; mahsulot rasmlari internetda qidiriladi.")),
        new AdvisorGroup("remind",
            L("Напоминания и рассылки", "Эскертүүлөр жана жөнөтүүлөр", "Reminders and messages", "Hatırlatmalar ve mesajlar", "Eslatmalar va xabarlar"),
            L("Готовые сообщения должникам. Отправляете вы сами — советник ничего не шлёт.", "Карыздарларга даяр билдирүүлөр. Жөнөтүүнү өзүңүз жасайсыз — кеңешчи өзү эч нерсе жөнөтпөйт.",
              "Ready-made messages to debtors. You send them yourself — the advisor sends nothing.", "Borçlulara hazır mesajlar. Siz kendiniz gönderirsiniz — danışman hiçbir şey göndermez.",
              "Qarzdorlarga tayyor xabarlar. Yuborishni o'zingiz qilasiz — maslahatchi o'zi hech narsa yubormaydi.")),
        new AdvisorGroup("proactive",
            L("Проактивные подсказки", "Активдүү кеңештер", "Proactive hints", "Proaktif ipuçları", "Faol maslahatlar"),
            L("Советник сам показывает карточки и готовые вопросы, пока вы ничего не спросили.", "Сиз сурабай эле кеңешчи карточкаларды жана даяр суроолорду көрсөтөт.",
              "The advisor shows cards and ready questions on its own before you ask.", "Danışman siz sormadan kartlar ve hazır sorular gösterir.",
              "Maslahatchi siz so'ramasdan o'zi kartalar va tayyor savollarni ko'rsatadi.")),
        new AdvisorGroup("voice",
            L("Голос", "Үн", "Voice", "Ses", "Ovoz"),
            L("Микрофон и звук разговора уходят в Google Gemini.", "Микрофон жана маектин үнү Google Gemini'ге кетет.", "The microphone and call audio go to Google Gemini.",
              "Mikrofon ve konuşma sesi Google Gemini'ye gider.", "Mikrofon va suhbat ovozi Google Gemini'ga ketadi.")),
        new AdvisorGroup("privacy",
            L("Приватность: какие данные уходят в ИИ", "Купуялык: ИИге кандай маалымат кетет", "Privacy: what data goes to the AI", "Gizlilik: yapay zekâya hangi veriler gider", "Maxfiylik: SIga qanday ma'lumot ketadi"),
            L("Имена и телефоны клиентов и сотрудников в ИИ не уходят никогда (только коды [Д1], [С1]). Здесь — остальное.", "Кардарлардын жана кызматкерлердин аттары менен телефондору ИИге эч качан кетпейт (коддор гана [Д1], [С1]). Бул жерде — калганы.",
              "Customer and staff names and phones never go to the AI (only codes [Д1], [С1]). Here — everything else.", "Müşteri ve çalışan adları ile telefonları yapay zekâya asla gitmez (yalnızca [Д1], [С1] kodları). Burada — gerisi.",
              "Mijoz va xodimlarning ismi va telefoni SIga hech qachon ketmaydi (faqat [Д1], [С1] kodlari). Bu yerda — qolgani.")),
    };

    public static readonly IReadOnlyList<AdvisorCapability> All = new[]
    {
        // ── Главный выключатель ──────────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability(Master, "main", AdvisorRisk.Reads, true, false, null,
            L("Нур Советник включён", "Нур Кеңешчи күйгүзүлгөн", "Nur Advisor is on", "Nur Danışman açık", "Nur Maslahatchi yoqilgan"),
            L("Выключено: пункт меню скрыт, окно отвечает «выключено владельцем», ИИ-ответы в Telegram-боте владельцу не идут, утренний отчёт не собирается, в ИИ ничего не уходит.",
              "Өчүк: меню пункту жашырылат, терезе «ээси өчүрдү» деп жооп берет, ээсине Telegram-боттогу ИИ жооптору келбейт, эртең мененки отчёт чогултулбайт, ИИге эч нерсе кетпейт.",
              "Off: the menu item is hidden, the window answers “turned off by the owner”, AI replies in the owner's Telegram bot stop, the morning report is not built, nothing goes to the AI.",
              "Kapalı: menü öğesi gizlenir, pencere «sahip kapattı» der, sahibe Telegram botunda yapay zekâ yanıtı gelmez, sabah raporu hazırlanmaz, yapay zekâya hiçbir şey gitmez.",
              "O'chiq: menyu bandi yashiriladi, oyna «egasi o'chirdi» deb javob beradi, egasiga Telegram-botda SI javoblari kelmaydi, ertalabki hisobot yig'ilmaydi, SIga hech narsa ketmaydi.")),

        // ── Чтение данных ────────────────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability("read.sales", "read", AdvisorRisk.Reads, true, false, null,
            L("Продажи, выручка, сравнение периодов", "Сатуулар, киреше, мезгилдерди салыштыруу", "Sales, revenue, period comparison", "Satışlar, ciro, dönem karşılaştırma", "Savdo, tushum, davrlarni solishtirish"),
            L("Выключено: советник не получает сводку продаж и не отвечает «как прошёл день».", "Өчүк: кеңешчи сатуулардын жыйынтыгын албайт жана «күн кандай өттү» деген суроого жооп бербейт.",
              "Off: the advisor gets no sales summary and cannot say how the day went.", "Kapalı: danışman satış özetini almaz ve «gün nasıl geçti» sorusunu yanıtlamaz.", "O'chiq: maslahatchi savdo xulosasini olmaydi va «kun qanday o'tdi» ga javob bermaydi.")),
        new AdvisorCapability("read.stock", "read", AdvisorRisk.Reads, true, false, "Products",
            L("Склад: остатки, что заканчивается, сроки годности", "Кампа: калдыктар, эмне түгөнүп жатат, жарактуулук мөөнөтү", "Warehouse: stock, running low, expiry dates", "Depo: stok, azalanlar, son kullanma tarihleri", "Ombor: qoldiqlar, tugayotganlar, yaroqlilik muddati"),
            L("Выключено: ИИ не видит остатки, список склада и сроки — на такие вопросы не ответит.", "Өчүк: ИИ калдыктарды, кампанын тизмесин жана мөөнөттөрдү көрбөйт — мындай суроолорго жооп бербейт.",
              "Off: the AI doesn't see stock, the warehouse list or expiry dates and won't answer such questions.", "Kapalı: yapay zekâ stoku, depo listesini ve tarihleri görmez; bu sorulara yanıt vermez.", "O'chiq: SI qoldiq, ombor ro'yxati va muddatlarni ko'rmaydi — bunday savollarga javob bermaydi.")),
        new AdvisorCapability("read.stocklists", "read", AdvisorRisk.Reads, true, false, "Products",
            L("Списки и поиск по складу («найди товары с 0 остатком», «без штрихкода»)", "Кампа боюнча тизмелер жана издөө («калдыгы 0 товарларды тап», «штрихкодсуз»)", "Warehouse lists and search (“find products with 0 stock”, “without a barcode”)", "Depo listeleri ve arama («stoku 0 olan ürünleri bul», «barkodsuz»)", "Ombor bo'yicha ro'yxat va qidiruv («qoldig'i 0 mahsulotlarni top», «shtrix-kodsiz»)"),
            L("Выключено: программа не строит списки склада по просьбе (нулевой и отрицательный остаток, заканчивается, без штрихкода/цены/категории, дубли, дорогие). Списки считаются на этом компьютере, каталог в ИИ не отправляется.", "Өчүк: программа суроо боюнча кампа тизмелерин түзбөйт (нөл жана терс калдык, түгөнүп жатат, штрихкод/баа/категориясыз, кайталанган, кымбат). Тизмелер ушул компьютерде эсептелет, каталог ИИге жөнөтүлбөйт.",
              "Off: the program does not build warehouse lists on request (zero and negative stock, running low, no barcode/price/category, duplicates, expensive). Lists are computed on this computer; the catalog is not sent to the AI.", "Kapalı: program istek üzerine depo listeleri oluşturmaz (sıfır ve negatif stok, azalan, barkod/fiyat/kategori yok, yinelenen, pahalı). Listeler bu bilgisayarda hesaplanır, katalog yapay zekâya gönderilmez.", "O'chiq: dastur so'rov bo'yicha ombor ro'yxatlarini tuzmaydi (nol va manfiy qoldiq, tugayapti, shtrix-kod/narx/kategoriyasiz, takroriy, qimmat). Ro'yxatlar shu kompyuterda hisoblanadi, katalog SIga yuborilmaydi.")),
        new AdvisorCapability("read.debts", "read", AdvisorRisk.Reads, true, false, "Clients",
            L("Долги клиентов (суммы и сроки, должники кодами)", "Кардарлардын карыздары (суммалар жана мөөнөттөр, карыздарлар коддор менен)", "Customer debts (amounts and terms, debtors as codes)", "Müşteri borçları (tutarlar ve süreler, borçlular kodla)", "Mijozlar qarzi (summalar va muddatlar, qarzdorlar kodlar bilan)"),
            L("Выключено: ИИ не получает итоги долгов и список должников; «кто должен» — без ответа.", "Өчүк: ИИ карыздардын жыйынтыгын жана карыздарлардын тизмесин албайт; «ким карыз» — жоопсуз.",
              "Off: the AI gets no debt totals or debtor list; “who owes” stays unanswered.", "Kapalı: yapay zekâ borç toplamlarını ve borçlu listesini almaz; «kim borçlu» yanıtsız kalır.", "O'chiq: SI qarz jami va qarzdorlar ro'yxatini olmaydi; «kim qarzdor» javobsiz qoladi.")),
        new AdvisorCapability("read.analytics", "read", AdvisorRisk.Reads, true, false, null,
            L("ABC-анализ и анализ товаров для акций", "ABC-талдоо жана акциялар үчүн товарларды талдоо", "ABC analysis and product analysis for promotions", "ABC analizi ve kampanyalar için ürün analizi", "ABC-tahlil va aksiyalar uchun mahsulot tahlili"),
            L("Выключено: советы «что продвигать, где теряются деньги» без цифр анализа.", "Өчүк: «эмнени жылдыруу керек, акча кайда жоголот» деген кеңештер талдоо сандарысыз.",
              "Off: advice on what to promote and where money is lost comes without analysis figures.", "Kapalı: «ne öne çıkarılmalı, para nerede kaybolur» önerileri analiz rakamları olmadan verilir.", "O'chiq: «nimani ilgari surish, pul qayerda yo'qoladi» maslahatlari tahlil raqamlarisiz beriladi.")),
        new AdvisorCapability("read.staff", "read", AdvisorRisk.Reads, true, false, "Staff",
            L("Сотрудники, зарплата, табель (имена кодами)", "Кызматкерлер, айлык, табель (аттар коддор менен)", "Staff, salary, timesheet (names as codes)", "Çalışanlar, maaş, puantaj (adlar kodla)", "Xodimlar, ish haqi, tabel (ismlar kodlar bilan)"),
            L("Выключено: ИИ не получает данные о сотрудниках и зарплате.", "Өчүк: ИИ кызматкерлер жана айлык жөнүндө маалымат албайт.", "Off: the AI gets no staff or salary data.", "Kapalı: yapay zekâ çalışan ve maaş verisi almaz.", "O'chiq: SI xodimlar va ish haqi ma'lumotini olmaydi.")),
        new AdvisorCapability("read.bot", "read", AdvisorRisk.Reads, true, false, "Settings",
            L("Телеграм-бот: состояние, обращения покупателей, прокат", "Телеграм-бот: абалы, сатып алуучулардын кайрылуулары, прокат", "Telegram bot: state, customer inquiries, rentals", "Telegram botu: durum, müşteri başvuruları, kiralama", "Telegram-bot: holati, xaridorlar murojaatlari, ijara"),
            L("Выключено: ИИ не видит настройки бота, обращения и прокат.", "Өчүк: ИИ боттун жөндөөлөрүн, кайрылууларды жана прокатты көрбөйт.", "Off: the AI doesn't see bot settings, inquiries and rentals.", "Kapalı: yapay zekâ bot ayarlarını, başvuruları ve kiralamayı görmez.", "O'chiq: SI bot sozlamalari, murojaatlar va ijarani ko'rmaydi.")),
        new AdvisorCapability("nav.open", "read", AdvisorRisk.Reads, true, false, null,
            L("Открывать разделы и товары по просьбе («Открой склад»)", "Суроо боюнча бөлүмдөрдү жана товарларды ачуу («Кампаны ач»)", "Open sections and products on request (“Open the warehouse”)", "İstek üzerine bölüm ve ürün açma («Depoyu aç»)", "So'rov bo'yicha bo'lim va mahsulotlarni ochish («Omborni och»)"),
            L("Выключено: «Открой …» не переходит по разделам, кнопки «Открыть раздел» под ответами скрыты.", "Өчүк: «… ач» бөлүмдөргө өтпөйт, жооптордун астындагы «Бөлүмдү ачуу» баскычтары жашырылат.",
              "Off: “Open …” does not switch sections and the “Open section” buttons under answers are hidden.", "Kapalı: «… aç» bölüm değiştirmez, yanıtların altındaki «Bölümü aç» düğmeleri gizlenir.", "O'chiq: «… och» bo'limlarga o'tmaydi, javoblar ostidagi «Bo'limni ochish» tugmalari yashiriladi.")),

        // ── Действия с данными ───────────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability("act.stock", "act", AdvisorRisk.Changes, true, true, "Products",
            L("Приход, списание, ревизия остатков", "Кирешти, списаниени, калдык ревизиясын жасоо", "Receipts, write-offs, stock counts", "Giriş, düşüm, stok sayımı", "Kirim, hisobdan chiqarish, qoldiq revizyasi"),
            L("Выключено: ИИ не предлагает менять остатки (stock_in, stock_out, stock_set, минимальный остаток, приход по накладной).", "Өчүк: ИИ калдыктарды өзгөртүүнү сунуштабайт (кирешти, списаниени, ревизияны, минималдуу калдыкты).",
              "Off: the AI doesn't suggest changing stock (receipts, write-offs, counts, minimum stock, invoice receipts).", "Kapalı: yapay zekâ stok değişikliği önermez (giriş, düşüm, sayım, asgari stok, fatura girişi).", "O'chiq: SI qoldiqni o'zgartirishni taklif qilmaydi (kirim, hisobdan chiqarish, revizya, minimal qoldiq).")),
        new AdvisorCapability("act.price", "act", AdvisorRisk.Changes, true, true, "Products",
            L("Менять цены продажи и закупки", "Сатуу жана сатып алуу баасын өзгөртүү", "Change sale and purchase prices", "Satış ve alış fiyatlarını değiştirme", "Sotuv va xarid narxlarini o'zgartirish"),
            L("Выключено: «поставь цену …» и наценка через ИИ не работают.", "Өчүк: «баасын койгун …» жана ИИ аркылуу үстөк иштебейт.", "Off: “set the price …” and markup through the AI do not work.", "Kapalı: «fiyatı … yap» ve yapay zekâ ile kâr payı çalışmaz.", "O'chiq: «narxini … qil» va SI orqali ustama ishlamaydi.")),
        new AdvisorCapability("act.card", "act", AdvisorRisk.Changes, true, true, "Products",
            L("Править карточку товара: название, описание, бренд, категория, страна, артикул, срок годности", "Товардын карточкасын оңдоо: аталышы, сүрөттөмө, бренд, категория, өлкө, артикул, мөөнөт", "Edit the product card: name, description, brand, category, country, article, expiry", "Ürün kartını düzenleme: ad, açıklama, marka, kategori, ülke, ürün kodu, son kullanma", "Mahsulot kartasini tahrirlash: nom, tavsif, brend, kategoriya, mamlakat, artikul, muddat"),
            L("Выключено: «заполни карточку», «переименуй», «поставь срок годности» через ИИ не работают.", "Өчүк: ИИ аркылуу «карточканы толтур», «атын өзгөрт», «мөөнөт кой» иштебейт.", "Off: “fill the card”, “rename”, “set the expiry” through the AI do not work.", "Kapalı: yapay zekâ ile «kartı doldur», «yeniden adlandır», «son kullanma ayarla» çalışmaz.", "O'chiq: SI orqali «kartani to'ldir», «nomini o'zgartir», «muddat qo'y» ishlamaydi.")),
        new AdvisorCapability("act.create", "act", AdvisorRisk.Changes, true, true, "Products",
            L("Создавать новые товары", "Жаңы товарларды түзүү", "Create new products", "Yeni ürün oluşturma", "Yangi mahsulotlar yaratish"),
            L("Выключено: ИИ не создаёт товары (в том числе строки накладной, которых нет в каталоге).", "Өчүк: ИИ товар түзбөйт (анын ичинде каталогдо жок накладной саптарын да).", "Off: the AI creates no products (including invoice lines missing from the catalog).", "Kapalı: yapay zekâ ürün oluşturmaz (katalogda olmayan fatura satırları dahil).", "O'chiq: SI mahsulot yaratmaydi (katalogda yo'q yuk xati qatorlari ham).")),
        new AdvisorCapability("act.bot", "act", AdvisorRisk.Changes, true, true, "Settings",
            L("Менять настройки Телеграм-бота и свои ответы бота", "Телеграм-боттун жөндөөлөрүн жана боттун өз жоопторун өзгөртүү", "Change Telegram bot settings and custom bot replies", "Telegram botu ayarlarını ve bot yanıtlarını değiştirme", "Telegram-bot sozlamalari va bot javoblarini o'zgartirish"),
            L("Выключено: «включи ответы голосом», «добавь команду боту» через ИИ не работают.", "Өчүк: ИИ аркылуу «үн менен жоопту күйгүз», «ботко буйрук кош» иштебейт.", "Off: “turn on voice replies”, “add a bot command” through the AI do not work.", "Kapalı: yapay zekâ ile «sesli yanıtı aç», «bota komut ekle» çalışmaz.", "O'chiq: SI orqali «ovozli javobni yoq», «botga buyruq qo'sh» ishlamaydi.")),
        new AdvisorCapability("nav.site", "act", AdvisorRisk.Changes, true, false, null,
            L("Команды сайту: «поставь тему …», «опубликуй сайт»", "Сайтка буйруктар: «теманы кой …», «сайтты жарыяла»", "Website commands: “set theme …”, “publish the website”", "Site komutları: «tema ayarla …», «siteyi yayınla»", "Saytga buyruqlar: «mavzuni qo'y …», «saytni e'lon qil»"),
            L("Выключено: сайт из чата не меняется и не публикуется. Внимание: эти команды выполняются сразу, без кнопки подтверждения.", "Өчүк: сайт чаттан өзгөрбөйт жана жарыяланбайт. Көңүл буруңуз: бул буйруктар ырастоосуз, дароо аткарылат.",
              "Off: the website is not changed or published from the chat. Note: these commands run at once, without a confirm button.", "Kapalı: site sohbetten değiştirilmez ve yayınlanmaz. Dikkat: bu komutlar onay düğmesi olmadan hemen çalışır.", "O'chiq: sayt chatdan o'zgarmaydi va e'lon qilinmaydi. Diqqat: bu buyruqlar tasdiq tugmasisiz darhol bajariladi.")),

        // ── Штрихкоды и справочник ───────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability("bc.directory", "barcode", AdvisorRisk.Reads, true, false, null,
            L("Находить название, бренд и категорию по штрихкоду в справочнике NurCRM", "NurCRM справочнигинен штрихкод боюнча аталышты, брендди жана категорияны табуу", "Find name, brand and category by barcode in the NurCRM directory", "NurCRM rehberinde barkoda göre ad, marka ve kategori bulma", "NurCRM ma'lumotnomasidan shtrix-kod bo'yicha nom, brend va kategoriyani topish"),
            L("Выключено: на «что за товар 4600…» советник отвечает только по вашему складу, справочник (128 тыс. товаров, работает без интернета) не используется.", "Өчүк: «4600… кандай товар» деген суроого кеңешчи сиздин кампа боюнча гана жооп берет, справочник (128 миң товар, интернетсиз иштейт) колдонулбайт.",
              "Off: for “what product is 4600…” the advisor answers only from your warehouse; the directory (128k products, works offline) is not used.", "Kapalı: «4600… hangi ürün» sorusuna danışman yalnızca deponuzdan yanıt verir; rehber (128 bin ürün, çevrimdışı çalışır) kullanılmaz.", "O'chiq: «4600… qanday mahsulot» ga maslahatchi faqat omboringiz bo'yicha javob beradi; ma'lumotnoma (128 ming mahsulot, internetsiz ishlaydi) ishlatilmaydi.")),
        new AdvisorCapability("bc.web", "barcode", AdvisorRisk.SendsOut, true, false, "Products",
            L("Искать товар по названию и штрихкоду в интернете", "Товарды аталышы жана штрихкоду боюнча интернеттен издөө", "Look a product up by name and barcode on the web", "Ürünü ada ve barkoda göre internette arama", "Mahsulotni nomi va shtrix-kodi bo'yicha internetdan qidirish"),
            L("Выключено: название и штрихкод товара не отправляются в Open Food Facts, поиск и Groq; описание, бренд и страну по интернету не найти.", "Өчүк: товардын аталышы жана штрихкоду Open Food Facts, издөөгө жана Groq'ко жөнөтүлбөйт; сүрөттөмө, бренд жана өлкө интернеттен табылбайт.",
              "Off: the product name and barcode are not sent to Open Food Facts, search or Groq; description, brand and country can't be found on the web.", "Kapalı: ürün adı ve barkodu Open Food Facts'e, aramaya ve Groq'a gönderilmez; açıklama, marka ve ülke internetten bulunamaz.", "O'chiq: mahsulot nomi va shtrix-kodi Open Food Facts, qidiruv va Groq'ga yuborilmaydi; tavsif, brend va mamlakat internetdan topilmaydi.")),
        new AdvisorCapability("bc.set", "barcode", AdvisorRisk.Changes, true, true, "Products",
            L("Записывать штрихкод товару через ИИ (в том числе из накладной)", "ИИ аркылуу товарга штрихкод жазуу (накладнойдон да)", "Write a barcode to a product through the AI (including from an invoice)", "Yapay zekâ ile ürüne barkod yazma (faturadan dahil)", "SI orqali mahsulotga shtrix-kod yozish (yuk xatidan ham)"),
            L("Выключено: «поставь штрихкод …» не работает, штрихкод из накладной в новый товар не подставляется (вводится вручную в карточке).", "Өчүк: «штрихкод кой …» иштебейт, накладнойдогу штрихкод жаңы товарга коюлбайт (карточкада кол менен киргизилет).",
              "Off: “set the barcode …” doesn't work, the invoice barcode isn't put on a new product (enter it by hand in the card).", "Kapalı: «barkodu … yap» çalışmaz, faturadaki barkod yeni ürüne yazılmaz (kartta elle girilir).", "O'chiq: «shtrix-kodni … qil» ishlamaydi, yuk xatidagi shtrix-kod yangi mahsulotga qo'yilmaydi (kartada qo'lda kiritiladi).")),
        new AdvisorCapability("web.search", "barcode", AdvisorRisk.SendsOut, true, false, null,
            L("Поиск в интернете по просьбе (цены конкурентов, курсы, новости)", "Суроо боюнча интернеттен издөө (атаандаштардын баасы, курстар, жаңылыктар)", "Web search on request (competitor prices, rates, news)", "İstek üzerine internet araması (rakip fiyatları, kurlar, haberler)", "So'rov bo'yicha internetdan qidirish (raqobatchilar narxi, kurslar, yangiliklar)"),
            L("Выключено: «найди в интернете …» не работает; в Google и Groq уходит только вопрос, без данных магазина.", "Өчүк: «интернеттен тап …» иштебейт; Google жана Groq'ко суроо гана кетет, дүкөндүн маалыматы жок.", "Off: “search the web …” doesn't work; only the question (no shop data) goes to Google and Groq.", "Kapalı: «internette ara …» çalışmaz; Google ve Groq'a yalnızca soru gider, mağaza verisi gitmez.", "O'chiq: «internetdan top …» ishlamaydi; Google va Groq'ga faqat savol ketadi, do'kon ma'lumotisiz.")),

        // ── Накладные и фото ─────────────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability("inv.read", "invoice", AdvisorRisk.SendsOut, true, true, "Products",
            L("Читать накладные: фото и PDF (скрепка, Ctrl+V, перетаскивание)", "Накладнойлорду окуу: сүрөт жана PDF (кыстырма, Ctrl+V, сүйрөп таштоо)", "Read invoices: photos and PDF (paperclip, Ctrl+V, drag and drop)", "Faturaları okuma: fotoğraf ve PDF (ataş, Ctrl+V, sürükle-bırak)", "Yuk xatlarini o'qish: rasm va PDF (skrepka, Ctrl+V, sudrab tashlash)"),
            L("Выключено: скрепка, вставка и перетаскивание в окне советника не работают, фото накладной в ИИ не уходит. Загрузка Excel/CSV/XML в «Складе» не затрагивается.", "Өчүк: кеңешчи терезесинде кыстырма, чаптоо жана сүйрөө иштебейт, накладнойдун сүрөтү ИИге кетпейт. «Кампадагы» Excel/CSV/XML жүктөө өзгөрбөйт.",
              "Off: the paperclip, paste and drag-and-drop in the advisor window don't work and no invoice photo goes to the AI. Excel/CSV/XML loading in Warehouse is unaffected.", "Kapalı: danışman penceresinde ataş, yapıştırma ve sürükleme çalışmaz, fatura fotoğrafı yapay zekâya gitmez. Depodaki Excel/CSV/XML yükleme etkilenmez.", "O'chiq: maslahatchi oynasida skrepka, qo'yish va sudrash ishlamaydi, yuk xati rasmi SIga ketmaydi. «Ombor»dagi Excel/CSV/XML yuklash o'zgarmaydi.")),
        new AdvisorCapability("inv.photo", "invoice", AdvisorRisk.SendsOut, true, true, "Products",
            L("Искать и ставить фото товаров из интернета", "Товарлардын сүрөттөрүн интернеттен издеп коюу", "Find and set product photos from the web", "Ürün fotoğraflarını internette bulup ayarlama", "Mahsulot rasmlarini internetdan topib qo'yish"),
            L("Выключено: «найди фото для …» и «поставь фото 4» не работают, названия товаров для поиска фото наружу не уходят.", "Өчүк: «… үчүн сүрөт тап» жана «4-сүрөттү кой» иштебейт, сүрөт издөө үчүн товардын аттары сыртка кетпейт.", "Off: “find a photo for …” and “set photo 4” don't work; product names are not sent out for photo search.", "Kapalı: «… için fotoğraf bul» ve «4. fotoğrafı koy» çalışmaz; fotoğraf araması için ürün adları dışarı gitmez.", "O'chiq: «… uchun rasm top» va «4-rasmni qo'y» ishlamaydi; rasm qidirish uchun mahsulot nomlari tashqariga ketmaydi.")),

        // ── Напоминания ──────────────────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability("rem.debtors", "remind", AdvisorRisk.SendsOut, true, true, "Clients",
            L("Напоминания должникам (WhatsApp, Telegram) с готовым текстом", "Карыздарларга эскертүүлөр (WhatsApp, Telegram) даяр тексти менен", "Debtor reminders (WhatsApp, Telegram) with ready text", "Borçlulara hazır metinli hatırlatmalar (WhatsApp, Telegram)", "Qarzdorlarga tayyor matnli eslatmalar (WhatsApp, Telegram)"),
            L("Выключено: «напомни должникам» не готовит сообщения, кнопка «Напомнить должникам» под ответом скрыта. Окно «Напоминания должникам» в Telegram-боте остаётся.", "Өчүк: «карыздарларга эскерт» билдирүү даярдабайт, жооптун астындагы «Карыздарларга эскертүү» баскычы жашырылат. Telegram-боттогу эскертүүлөр терезеси сакталат.",
              "Off: “remind debtors” prepares no messages and the “Remind debtors” button under answers is hidden. The reminders window in the Telegram bot stays.", "Kapalı: «borçlulara hatırlat» mesaj hazırlamaz, yanıt altındaki «Borçlulara hatırlat» düğmesi gizlenir. Telegram botundaki hatırlatma penceresi kalır.", "O'chiq: «qarzdorlarga eslat» xabar tayyorlamaydi, javob ostidagi «Qarzdorlarga eslatish» tugmasi yashiriladi. Telegram-botdagi eslatmalar oynasi qoladi.")),

        // ── Проактивные подсказки ────────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability("pro.morning", "proactive", AdvisorRisk.SendsOut, true, false, null,
            L("Утренний отчёт (карточка и сообщение в Telegram)", "Эртең мененки отчёт (карточка жана Telegram'га билдирүү)", "Morning report (card and Telegram message)", "Sabah raporu (kart ve Telegram mesajı)", "Ertalabki hisobot (karta va Telegram xabari)"),
            L("Выключено: отчёт в 9:00 не собирается, цифры вчерашнего дня в ИИ и в Telegram не отправляются.", "Өчүк: саат 9:00дөгү отчёт чогултулбайт, кечээки күндүн сандары ИИге жана Telegram'га жөнөтүлбөйт.", "Off: the 9:00 report isn't built; yesterday's figures are not sent to the AI or Telegram.", "Kapalı: 9:00 raporu hazırlanmaz; dünün rakamları yapay zekâya ve Telegram'a gönderilmez.", "O'chiq: 9:00 hisobot yig'ilmaydi; kechagi raqamlar SIga va Telegram'ga yuborilmaydi.")),
        new AdvisorCapability("pro.remind", "proactive", AdvisorRisk.Reads, true, false, "Clients",
            L("Подсказка «Пора напомнить N должникам»", "«N карыздарга эскертүүгө убакыт болду» кеңеши", "The “Time to remind N debtors” card", "«N borçluya hatırlatma zamanı» kartı", "«N qarzdorga eslatish vaqti» kartasi"),
            L("Выключено: карточка на пустом экране не показывается; напоминания можно открыть вручную.", "Өчүк: бош экрандагы карточка көрсөтүлбөйт; эскертүүлөрдү кол менен ачса болот.", "Off: the card on the empty screen isn't shown; reminders can still be opened by hand.", "Kapalı: boş ekrandaki kart gösterilmez; hatırlatmalar elle açılabilir.", "O'chiq: bo'sh ekrandagi karta ko'rsatilmaydi; eslatmalarni qo'lda ochish mumkin.")),
        new AdvisorCapability("pro.hints", "proactive", AdvisorRisk.Reads, true, false, null,
            L("Готовые вопросы и кнопки под ответами («Заказать у поставщика», «Сделать акцию»)", "Даяр суроолор жана жооптордун астындагы баскычтар («Жеткирүүчүдөн заказ», «Акция жасоо»)", "Ready questions and buttons under answers (“Order from supplier”, “Make a promotion”)", "Hazır sorular ve yanıtların altındaki düğmeler («Tedarikçiden sipariş», «Kampanya yap»)", "Tayyor savollar va javoblar ostidagi tugmalar («Yetkazib beruvchidan buyurtma», «Aksiya qilish»)"),
            L("Выключено: пустой экран без карточек-вопросов, под ответами нет кнопок-предложений (Excel остаётся).", "Өчүк: бош экран суроо карточкаларысыз, жооптордун астында сунуш баскычтары жок (Excel калат).", "Off: the empty screen has no question cards and answers have no suggestion buttons (Excel stays).", "Kapalı: boş ekranda soru kartları, yanıtların altında öneri düğmeleri yoktur (Excel kalır).", "O'chiq: bo'sh ekranda savol kartalari, javoblar ostida taklif tugmalari yo'q (Excel qoladi).")),

        // ── Голос ────────────────────────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability("voice.ask", "voice", AdvisorRisk.SendsOut, true, false, null,
            L("Вопрос голосом (кнопка микрофона)", "Үн менен суроо (микрофон баскычы)", "Ask by voice (microphone button)", "Sesle sorma (mikrofon düğmesi)", "Ovoz bilan so'rash (mikrofon tugmasi)"),
            L("Выключено: кнопка микрофона скрыта, запись голоса в Google не отправляется.", "Өчүк: микрофон баскычы жашырылат, үн жазуусу Google'га жөнөтүлбөйт.", "Off: the microphone button is hidden and no voice recording is sent to Google.", "Kapalı: mikrofon düğmesi gizlenir, ses kaydı Google'a gönderilmez.", "O'chiq: mikrofon tugmasi yashiriladi, ovoz yozuvi Google'ga yuborilmaydi.")),
        new AdvisorCapability("voice.live", "voice", AdvisorRisk.SendsOut, true, false, null,
            L("Разговор голосом, как по телефону", "Телефондогудай үн менен маек", "Voice conversation, like a phone call", "Telefon gibi sesli sohbet", "Telefondagidek ovozli suhbat"),
            L("Выключено: кнопка «Разговор» скрыта; в Google Gemini не уходят звук и данные магазина для звонка.", "Өчүк: «Маек» баскычы жашырылат; Google Gemini'ге үн жана чалуу үчүн дүкөндүн маалыматы кетпейт.", "Off: the “Talk” button is hidden; no audio and shop data for the call go to Google Gemini.", "Kapalı: «Sohbet» düğmesi gizlenir; Google Gemini'ye ses ve görüşme için mağaza verisi gitmez.", "O'chiq: «Suhbat» tugmasi yashiriladi; Google Gemini'ga ovoz va suhbat uchun do'kon ma'lumoti ketmaydi.")),

        // ── Приватность ──────────────────────────────────────────────────────────────────────────────────────────────
        new AdvisorCapability("priv.purchase", "privacy", AdvisorRisk.SendsOut, true, false, null,
            L("Закупочные цены, наценка и прибыль уходят в ИИ", "Сатып алуу баасы, үстөк жана пайда ИИге кетет", "Purchase prices, markup and profit go to the AI", "Alış fiyatı, kâr payı ve kâr yapay zekâya gider", "Xarid narxi, ustama va foyda SIga ketadi"),
            L("Выключено: из склада, карточек товаров и вопросов убирается закупка и наценка, анализ акций и ABC не отправляются. Ответы про прибыль станут беднее.", "Өчүк: кампадан, товар карточкаларынан жана суроолордон сатып алуу баасы менен үстөк алынат, акциялар талдоосу менен ABC жөнөтүлбөйт. Пайда боюнча жооптор начарлайт.",
              "Off: purchase price and markup are removed from the warehouse, product cards and questions; promotion analysis and ABC are not sent. Profit answers get poorer.", "Kapalı: depodan, ürün kartlarından ve sorulardan alış fiyatı ve kâr payı çıkarılır; kampanya analizi ve ABC gönderilmez. Kâr yanıtları zayıflar.", "O'chiq: ombor, mahsulot kartalari va savollardan xarid narxi va ustama olib tashlanadi, aksiya tahlili va ABC yuborilmaydi. Foyda haqidagi javoblar kambag'allashadi.")),
        new AdvisorCapability("priv.receipts", "privacy", AdvisorRisk.SendsOut, true, false, null,
            L("Чеки за период (с кассирами) уходят в ИИ", "Мезгилдеги чектер (кассирлер менен) ИИге кетет", "Receipts for a period (with cashiers) go to the AI", "Dönem fişleri (kasiyerlerle) yapay zekâya gider", "Davr cheklari (kassirlar bilan) SIga ketadi"),
            L("Выключено: ИИ не получает таблицу чеков, ответы «покажи чеки за вчера» не строятся. Итоги продаж остаются.", "Өчүк: ИИ чектердин таблицасын албайт, «кечээки чектерди көрсөт» жообу түзүлбөйт. Сатуулардын жыйынтыгы калат.", "Off: the AI gets no receipts table, “show yesterday's receipts” isn't answered. Sales totals stay.", "Kapalı: yapay zekâ fiş tablosunu almaz, «dünün fişlerini göster» yanıtlanmaz. Satış toplamları kalır.", "O'chiq: SI cheklar jadvalini olmaydi, «kechagi cheklarni ko'rsat» javob berilmaydi. Savdo jami qoladi.")),
        new AdvisorCapability("priv.fallback", "privacy", AdvisorRisk.SendsOut, true, false, null,
            L("Запасные ИИ (Groq, OpenRouter) получают обезличенную сводку", "Запастагы ИИлер (Groq, OpenRouter) жеке маалыматсыз жыйынтык алышат", "Backup AIs (Groq, OpenRouter) receive an anonymised summary", "Yedek yapay zekâlar (Groq, OpenRouter) kişisel olmayan özet alır", "Zaxira SIlar (Groq, OpenRouter) shaxsiy ma'lumotsiz xulosa oladi"),
            L("Выключено: если Gemini не ответил, вопрос не передаётся запасным моделям — советник просто просит повторить позже.", "Өчүк: Gemini жооп бербесе, суроо запастагы моделдерге берилбейт — кеңешчи кийинчерээк кайталоону суранат.", "Off: if Gemini doesn't answer, the question isn't passed to backup models — the advisor just asks to retry later.", "Kapalı: Gemini yanıt vermezse soru yedek modellere iletilmez; danışman daha sonra tekrar denemenizi ister.", "O'chiq: Gemini javob bermasa, savol zaxira modellarga berilmaydi — maslahatchi keyinroq qayta urinishni so'raydi.")),
    };

    private static readonly Dictionary<string, AdvisorCapability> ById = All.ToDictionary(c => c.Id, StringComparer.Ordinal);

    public static AdvisorCapability? Find(string id) => ById.TryGetValue(id, out var c) ? c : null;

    // ── Хранение ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly object Gate = new();
    private static string? _cachedJson;
    private static Dictionary<string, bool> _cached = new();

    /// <summary>Что-то изменили (этой кассой или пришло из облака): окна обновляют вид.</summary>
    public static event Action? Changed;

    /// <summary>Кто меняет (для записи в журнал): подставляет окно настроек — почта владельца/администратора.</summary>
    public static Func<string?>? ActorProvider { get; set; }

    private static Dictionary<string, bool> Overrides()
    {
        string? json;
        try { json = UserPreferences.Instance.AdvisorCapabilitiesJson; }
        catch { json = null; }
        lock (Gate)
        {
            if (string.Equals(json, _cachedJson, StringComparison.Ordinal))
                return _cached;
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                                map[p.Name] = p.Value.GetBoolean();
                }
                catch (JsonException)
                {
                    // испорченное поле = «ничего не менялось» (всё включено, как было)
                }
            }
            _cachedJson = json;
            _cached = map;
            return map;
        }
    }

    /// <summary>Включена ли возможность. Главный выключатель выключен — выключено всё. Неизвестный id не блокируется.</summary>
    public static bool IsOn(string id)
    {
        var map = Overrides();
        if (id != Master && map.TryGetValue(Master, out var masterOn) && !masterOn)
            return false;
        if (map.TryGetValue(id, out var on))
            return on;
        return Find(id)?.DefaultOn ?? true;
    }

    /// <summary>Включена ли возможность саму по себе (без учёта главного выключателя) — для вида списка в настройках.</summary>
    public static bool IsSet(string id) => Overrides().TryGetValue(id, out var on) ? on : Find(id)?.DefaultOn ?? true;

    public static bool MasterOn => IsOn(Master);

    /// <summary>Поменять одну возможность. Пишет в журнал (кто, что, как стало), сохраняет настройки, будит окна.</summary>
    public static void Set(string id, bool on) => SetMany(new[] { (id, on) }, null);

    public static void SetMany(IEnumerable<(string Id, bool On)> changes, string? reason)
    {
        var map = new Dictionary<string, bool>(Overrides(), StringComparer.Ordinal);
        var done = new List<string>();
        foreach (var (id, on) in changes)
        {
            if (Find(id) is not { } cap || IsSet(id) == on)
                continue;
            if (on == cap.DefaultOn)
                map.Remove(id);
            else
                map[id] = on;
            done.Add($"{id}={(on ? "вкл" : "выкл")}");
        }
        if (done.Count == 0)
            return;
        // "{}" а не null: null в облако не уходит, и другие кассы остались бы с прежними запретами
        var json = map.Count == 0 ? "{}" : JsonSerializer.Serialize(map.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));
        var prefs = UserPreferences.Instance;
        prefs.AdvisorCapabilitiesJson = json;
        prefs.SaveToDisk();
        string who;
        try { who = ActorProvider?.Invoke() ?? "—"; }
        catch { who = "—"; }
        PosLogger.Log($"AUDIT Нур Советник: {(reason is null ? "" : reason + ": ")}{string.Join(", ", done)} (меняет: {who}).", "INFO");
        Changed?.Invoke();
    }

    public enum Preset { SafeOn, ReadOnly, AllOff }

    /// <summary>«Включить всё безопасное» — главный выключатель и всё, что читает или меняет данные только с подтверждением; то, что
    /// отправляет наружу (интернет, голос, фото, рассылки, приватность), не трогается. «Только чтение» — читает и переходит по
    /// разделам, ничего не меняет и не отправляет. «Выключить всё» — Нур Советник выключен целиком.</summary>
    public static void ApplyPreset(Preset preset)
    {
        var changes = new List<(string, bool)>();
        foreach (var c in All)
        {
            bool? target = preset switch
            {
                Preset.SafeOn => c.Id == Master || c.Risk == AdvisorRisk.Reads || c.Risk == AdvisorRisk.Changes && c.NeedsConfirm ? true : null,
                Preset.ReadOnly => c.Id == Master || c.Risk == AdvisorRisk.Reads,
                _ => false,
            };
            if (target is { } t)
                changes.Add((c.Id, t));
        }
        SetMany(changes, preset switch { Preset.SafeOn => "«включить всё безопасное»", Preset.ReadOnly => "«только чтение»", _ => "«выключить всё»" });
    }

    /// <summary>Облако принесло новое значение (SettingsCloudSync.Applied) — окна обновляют вид.</summary>
    public static void RaiseChanged() => Changed?.Invoke();

    // ── Принуждение ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Вежливая фраза пользователю, когда просит выключенное.</summary>
    public static string BlockedText(string? id = null)
    {
        var what = id is not null && Find(id) is { } c ? " «" + c.Name + "»" : "";
        return Tr.T($"Эта возможность{what} выключена владельцем (Настройки → Операции → Нур Советник).",
            $"Бул мүмкүнчүлүк{what} ээси тарабынан өчүрүлгөн (Жөндөөлөр → Операциялар → Нур Кеңешчи).",
            $"This feature{what} is turned off by the owner (Settings → Operations → Nur Advisor).",
            $"Bu özellik{what} sahip tarafından kapatıldı (Ayarlar → İşlemler → Nur Danışman).",
            $"Bu imkoniyat{what} egasi tomonidan o'chirilgan (Sozlamalar → Operatsiyalar → Nur Maslahatchi).");
    }

    /// <summary>Возможность, нужная для операции «ТОВАР: {op}» (null — операция не ограничена отдельным тумблером).</summary>
    public static string? ForOp(string op) => op switch
    {
        "stock_in" or "stock_out" or "stock_set" or "set_min" or "receive" => "act.stock",
        "set_price" or "set_purchase" => "act.price",
        "set_expiry" or "set_shelf_life" or "set_description" or "set_country" or "set_name" or "set_brand" or "set_category" or "set_article" => "act.card",
        "set_barcode" => "bc.set",
        "create" => "act.create",
        "open" or "open_section" => "nav.open",
        "photo" => "inv.photo",
        _ => null,
    };

    /// <summary>Операция разрешена? Нет — вернёт id запретившей возможности (для сообщения).</summary>
    public static bool OpAllowed(string op, out string? blockedBy)
    {
        blockedBy = null;
        if (!MasterOn)
        {
            blockedBy = Master;
            return false;
        }
        if (ForOp(op) is { } id && !IsOn(id))
        {
            blockedBy = id;
            return false;
        }
        return true;
    }

    /// <summary>Единая точка для данных, которые уходят ИИ: выключенные части сводки и приватность отсекаются здесь
    /// (AiContextBudget.Compose — и текстовый ответ, и голосовой звонок, и запасной ИИ).</summary>
    public static IEnumerable<AiSection> FilterSections(IEnumerable<AiSection> sections)
    {
        foreach (var s in sections)
            if (SectionAllowed(s.Key))
                yield return s;
    }

    /// <summary>2026-10-30: списки склада по строкам (весь каталог, что заканчивается) разрешены: включены и «Склад», и «Списки склада»
    /// (и сам Нур Советник). Единая проверка для текстового окна, голоса (звонок и вопрос микрофоном) и Telegram-бота владельца.</summary>
    public static bool StockListsAllowed => MasterOn && IsOn("read.stock") && IsOn("read.stocklists");

    public static bool SectionAllowed(string key)
    {
        if (!MasterOn)
            return false;
        return key switch
        {
            "overview" or "periods" or "period_question" or "upsell" => IsOn("read.sales"),
            "receipts" => IsOn("read.sales") && IsOn("priv.receipts"),
            // 2026-10-30 (приёмка 1.17.66): построчные списки склада («warehouse» — все товары, «low_stock» — что заканчивается) ещё и
            // под тумблером «Списки склада»; итоги, сроки и карточки названных товаров — только под «Склад». Проверка здесь одна на
            // текстовое окно, голосовой разговор и Telegram-бот: данные склада до ИИ идут через AiContextBudget.Compose.
            "warehouse" or "low_stock" => StockListsAllowed,
            "warehouse_totals" or "expiry" or "question_products" or "question_cards" => IsOn("read.stock"),
            "analysis" or "abc" => IsOn("read.analytics") && IsOn("priv.purchase"),
            "debts" or "debtors" => IsOn("read.debts"),
            "staff" => IsOn("read.staff"),
            "bot" or "local" => IsOn("read.bot"),
            "research" => IsOn("bc.web"),
            _ => true,
        };
    }

    /// <summary>Все операции с товарами разрешены (инструкция бота остаётся прежним текстом).</summary>
    public static bool AllActionsOn => MasterOn && new[] { "act.stock", "act.price", "act.card", "act.create", "bc.set", "nav.open", "inv.photo" }.All(IsOn);

    /// <summary>Закупку и наценку в ИИ не отправлять (тумблер приватности).</summary>
    public static bool HidePurchase => !IsOn("priv.purchase");

    /// <summary>Правила действий с товарами для инструкции ИИ — только по включённым возможностям (выключенные операции в инструкцию не
    /// попадают). Все включены — полный прежний набор. Пусто — действий с товарами нет вовсе.</summary>
    public static string BuildActionRules()
    {
        var ops = new List<string>();
        var rules = new List<string>();
        if (IsOn("act.stock"))
        {
            ops.Add("stock_in / stock_out / stock_set (\"qty\"; у stock_out \"reason\": \"Просрочка\"/\"Брак\"/\"Порча\"/\"Кража\"/\"Другое\")");
            ops.Add("set_min (\"qty\")");
            ops.Add("receive (\"qty\", \"purchase\", \"price\")");
            rules.Add("«добавь/пришло» — stock_in, «спиши/убери» — stock_out, «ровно N» — stock_set; просрочку — stock_out с reason «Просрочка»");
        }
        if (IsOn("act.price"))
            ops.Add("set_price / set_purchase (\"price\")");
        if (IsOn("act.card"))
        {
            ops.Add("set_expiry (\"date\": \"ГГГГ-ММ-ДД\"), set_shelf_life (\"days\")");
            ops.Add("set_description (\"text\": 2–5 предложений без выдуманных цифр)");
            ops.Add("set_country / set_name / set_brand / set_category / set_article (\"text\")");
        }
        if (IsOn("bc.set"))
            ops.Add("set_barcode (\"text\")");
        if (IsOn("act.create"))
            ops.Add("create (\"name\", \"qty\", \"purchase\", \"price\", \"barcode\"?, \"unit\"?, \"category\"?)");
        if (IsOn("nav.open"))
        {
            ops.Add("open_section (\"section\": id из списка РАЗДЕЛЫ ПРОГРАММЫ ниже, например warehouse.receiving)");
            ops.Add("open (открыть товар на складе)");
        }
        if (IsOn("inv.photo"))
            ops.Add("photo (только по прямой просьбе искать фото в интернете)");
        if (ops.Count == 0)
            return "";
        return "ДЕЙСТВИЯ С ТОВАРАМИ: ничего не меняется до кнопки владельца — пиши «предлагаю», «подтвердите», не пиши, что уже сделал. "
               + "Каждое действие — отдельной строкой в конце ответа: ТОВАР: {\"op\": \"...\", \"product\": \"точное название или id из данных\", ...} — одной строкой JSON. "
               + "op: " + string.Join(", ", ops) + ". " + (rules.Count > 0 ? string.Join("; ", rules) + "; " : "")
               + "название — точно как в данных; товар не найден или неясен — спроси, не угадывай"
               + (IsOn("bc.set") ? "; название и штрихкод без просьбы не меняй" : "")
               + ". Блок «НАЙДЕНО В ИНТЕРНЕТЕ О ТОВАРАХ» — источник описания, страны, бренда; сведений нет — не придумывай. Обрабатывай все перечисленные позиции."
               + DisabledNote();
    }

    /// <summary>Строка для инструкции: что владелец выключил и как отвечать, если просят (чтобы ИИ не обещал недоступное).</summary>
    public static string DisabledNote()
    {
        var off = All.Where(c => c.Id != Master && c.Group is "act" or "invoice" or "remind" or "barcode" or "read" && !IsOn(c.Id)).Select(c => c.Name).ToList();
        return off.Count == 0 ? ""
            : " ВЫКЛЮЧЕНО ВЛАДЕЛЬЦЕМ (не предлагай и не обещай): " + string.Join("; ", off) + ". Если просят — ответь одной фразой: «Эта возможность выключена владельцем (Настройки → Операции → Нур Советник)».";
    }
}
