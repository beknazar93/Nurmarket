using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-10-27, жалоба владельца (снимок чата Нур Советника): «ИИ неправильно открывает разделы и вкладки, очень долго думает».
/// Причина: «открой …» разбирала нейросеть (в её списке не было «Приёмки», она называла «Прибыль и деньги»), а программа открывала
/// раздел по первым 5 буквам слов (при ничьей — первый в меню) и ответ «Открыт раздел» писала, не проверив, что открылось.
/// Здесь — локальный маршрутизатор без нейросети: словарь всех разделов и вкладок программы владельца (ru, ky, uz, en, tr), нечёткое
/// сопоставление (падежи, опечатки, «э-э», «мне», «раздел», «вкладку»), при ничьей — до 4 вариантов, а не угадывание. Чистая логика
/// без Avalonia: открывает раздел OwnerShellWindow (OpenNavTarget) и проверяет результат; ответ владельцу строится ИЗ ФАКТА открытия.</summary>
public static class AdvisorNavRouter
{
    /// <summary>Пункт назначения: раздел меню (<see cref="Section"/> — ключ OwnerShellWindow), вкладка внутри него и/или фильтр.</summary>
    public sealed class NavTarget
    {
        public required string Id { get; init; }
        public required string Section { get; init; }
        public string? Tab { get; init; }
        public string? Filter { get; init; }
        public NavTarget? Parent { get; init; }
        /// <summary>Фильтр списка («Заканчивается», «Нет в наличии») — может быть второй частью составной просьбы.</summary>
        public bool IsFilter { get; init; }
        public required string[] Names { get; init; }   // ru, ky, en, tr, uz
        internal string Synonyms { get; set; } = "";
        private string[][]? _phrases;
        /// <summary>Фразы словаря как списки свёрнутых слов (без слов-мусора) — считаются при первом разборе.</summary>
        internal string[][] Phrases => _phrases ??= ParsePhrases(Synonyms);
        public int Depth => Parent is null ? 0 : 1;

        public string OwnTitle => Tr.T(Names[0], Names[1], Names[2], Names[3], Names[4]);

        /// <summary>«Склад → Приёмка» — как человек видит путь в программе.</summary>
        public string Title => Parent is null ? OwnTitle : Parent.Title + " → " + OwnTitle;

        public string TitleRu => Parent is null ? Names[0] : Parent.TitleRu + " → " + Names[0];
    }

    public enum RouteKind
    {
        /// <summary>Это не команда перехода — отвечает обычный путь (нейросеть).</summary>
        NotNav,
        /// <summary>Один понятный пункт назначения.</summary>
        Single,
        /// <summary>Ничья: несколько подходящих пунктов — показать кнопки (до 4), не угадывать.</summary>
        Ambiguous,
        /// <summary>Есть команда «открой …», но такого раздела в программе нет.</summary>
        Unknown,
    }

    /// <param name="Targets">Single — один пункт; Ambiguous — варианты (до 4).</param>
    /// <param name="Companion">Вторая часть составной просьбы: фильтр списка («закончились») к открытому разделу.</param>
    /// <param name="Residual">Слова, которые маршрутизатор не объяснил (название товара, вопрос о цифрах).</param>
    /// <param name="Strong">Явная команда «открой / перейди / зайди / ач / och / aç».</param>
    public sealed record Route(RouteKind Kind, IReadOnlyList<NavTarget> Targets, NavTarget? Companion, IReadOnlyList<string> Residual, bool Strong)
    {
        public NavTarget? Target => Kind == RouteKind.Single ? Targets[0] : null;
        public static readonly Route None = new(RouteKind.NotNav, Array.Empty<NavTarget>(), null, Array.Empty<string>(), false);
    }

    // ── Словарь ────────────────────────────────────────────────────────────────────────────────────────────────

    private static NavTarget S(string id, string section, string ru, string ky, string en, string tr, string uz, string synonyms,
        string? tab = null, string? filter = null, NavTarget? parent = null, bool isFilter = false) =>
        new() { Id = id, Section = section, Tab = tab, Filter = filter, Parent = parent, IsFilter = isFilter, Names = new[] { ru, ky, en, tr, uz }, Synonyms = synonyms };

    internal static string[][] ParsePhrases(string s) =>
        s.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Tokens(p).Where(t => !Fillers.Contains(t)).ToArray()).Where(p => p.Length > 0).ToArray();

    private static readonly NavTarget Warehouse = S("warehouse", "warehouse", "Склад", "Кампа", "Warehouse", "Depo", "Ombor",
        "склад|склады|товары|товар|остатки|остаток|кампа|калдык|товарлар|ombor|omborxona|mahsulotlar|mahsulot|qoldiq|qoldiqlar|depo|urunler|urun|stok|warehouse|products|inventory|stock|ostatki",
        tab: "products");

    private static readonly NavTarget Purchases = S("purchases", "purchases", "Закупки", "Сатып алуулар", "Purchases", "Satın alma", "Xaridlar",
        "закупки|закупка|закупок|история закупок|сатып алуу|сатып алуулар|purchases|purchase|procurement|satin alma|satin almalar|xaridlar|xarid|закуп", tab: "history");

    private static readonly NavTarget SalesFinance = S("salesfinance", "salesfinance", "Продажи и финансы", "Сатуулар жана каржы", "Sales & finance", "Satışlar ve finans", "Sotuvlar va moliya",
        "продажи|продажа|финансы|финансов|финанс|чеки|чек|чека|чеков|выручка|касса|сатуу|сатуулар|каржы|чектер|sales|sale|finance|satislar|satis|finans|sotuvlar|sotuv|moliya|receipts|checks|cheklar|fisler|fis",
        tab: "checks");

    private static readonly NavTarget Settings = S("settings", "settings", "Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar",
        "настройки|настройка|настроек|параметры|жондоолор|жондоо|settings|setting|options|ayarlar|ayar|sozlamalar|sozlama", tab: null);

    private static NavTarget Sub(NavTarget parent, string id, string tab, string? filter, string ru, string ky, string en, string tr, string uz, string syn, bool isFilter = false) =>
        S(id, parent.Section, ru, ky, en, tr, uz, syn, tab: tab, filter: filter, parent: parent, isFilter: isFilter);

    private static readonly NavTarget WhReceiving = Sub(Warehouse, "warehouse.receiving", "receiving", null, "Приёмка", "Кабыл алуу", "Receiving", "Mal kabul", "Qabul qilish",
        "приемка|приемку|приемки|принять товар|приход|кабыл алуу|кабыл|qabul|qabul qilish|mal kabul|kabul|receiving|goods receipt|receive goods");
    private static readonly NavTarget WhRevision = Sub(Warehouse, "warehouse.revision", "revision", null, "Ревизия", "Ревизия", "Stocktake", "Sayım", "Inventarizatsiya",
        "ревизия|ревизию|ревизии|инвентаризация|инвентаризацию|пересчет|пересчитать остатки|сверка остатков|stocktake|stocktaking|stock take|inventory check|sayim|stok sayimi|inventarizatsiya|reviziya|revizya");
    private static readonly NavTarget WhWriteOff = Sub(Warehouse, "warehouse.writeoff", "writeoff", null, "Списание", "Эсептен чыгаруу", "Write-off", "Düşüm", "Hisobdan chiqarish",
        "списание|списания|списании|списать|списать товар|эсептен чыгаруу|эсептен чыгар|write off|write-off|writeoff|dusum|stok dusumu|zayiat|hisobdan chiqarish|hisobdan chiqar");
    private static readonly NavTarget WhMovements = Sub(Warehouse, "warehouse.movements", "movements", null, "Перемещение", "Жылдыруу", "Transfers", "Transfer", "Ko'chirish",
        "перемещение|перемещения|перемещении|переместить|перемещать|трансфер|жылдыруу|которуу|transfer|transfers|kochirish|kochirmoq|aktarma|move goods");
    private static readonly NavTarget WhLabels = Sub(Warehouse, "warehouse.labels", "labels", null, "Редактор этикетки", "Этикетка редактору", "Label editor", "Etiket editörü", "Yorliq muharriri",
        "этикетка|этикетки|этикетку|этикеток|ценник|ценники|ценников|редактор этикетки|печать ценников|печать этикеток|label|labels|label editor|price tag|price tags|barcode label|etiket|etiketler|etiket editoru|fiyat etiketi|yorliq|yorliqlar|narx yorligi|баа этикеткасы|этикетка редактору");
    private static readonly NavTarget WhBulk = Sub(Warehouse, "warehouse.bulk", "bulk", null, "Массовое добавление", "Массалык кошуу", "Bulk add", "Toplu ekleme", "Ommaviy qo'shish",
        "массовое добавление|массовая загрузка|массовое добавить|добавить много товаров|массалык кошуу|bulk add|bulk upload|bulk import|toplu ekleme|toplu yukleme|ommaviy qoshish|ommaviy qoshish");
    private static readonly NavTarget WhLow = Sub(Warehouse, "warehouse.low", "products", "low", "Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda",
        "заканчивается|заканчиваются|заканчивающиеся|заканчивающиеся товары|кончается|кончаются|мало осталось|мало на складе|низкий остаток|минимальный остаток|түгөнүп баратат|аз калган|running low|low stock|running out|tukenmek uzere|tukeniyor|azalan|azalmis|tugab bormoqda|tugayapti|kam qolgan", isFilter: true);
    private static readonly NavTarget WhOut = Sub(Warehouse, "warehouse.out", "products", "out", "Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas",
        "нет в наличии|закончились|закончился|закончилось|закончившиеся|закончившихся|кончились|кончился|нулевой остаток|нулевые остатки|нет на складе|распродано|калдыкта жок|түгөндү|түгөнгөн|out of stock|sold out|ran out|stokta yok|tukendi|tukenmis|tukenen|mavjud emas|tugagan|tugadi", isFilter: true);
    private static readonly NavTarget WhExpiry = Sub(Warehouse, "warehouse.expiry", "products", "expiry", "Срок годности", "Жарактуулук мөөнөтү", "Expiry", "Son kullanma", "Yaroqlilik muddati",
        "срок годности|сроки годности|срок хранения|просроченные|просроченные товары|просрочка|просрочено|жарактуулук мөөнөтү|expiry|expired|expiry date|best before|son kullanma|son kullanma tarihi|yaroqlilik muddati|muddati otgan", isFilter: true);

    private static readonly NavTarget PurHistory = Purchases;
    private static readonly NavTarget PurOrders = Sub(Purchases, "purchases.orders", "orders", null, "Заказы поставщикам", "Жеткирүүчүлөргө буйрутмалар", "Supplier orders", "Tedarikçi siparişleri", "Buyurtmalar",
        "заказы поставщикам|заказ поставщику|заказы поставщика|заказать у поставщика|жеткируучулорго буйрутмалар|supplier orders|supplier order|purchase orders|tedarikci siparisleri|tedarikci siparisi|yetkazib beruvchiga buyurtma|buyurtmalar|заказы|заказ");
    private static readonly NavTarget PurReceive = Sub(Purchases, "purchases.receive", "receive", null, "Приём товара", "Товар кабыл алуу", "Receive goods", "Mal girişi", "Tovar qabuli",
        "прием товара от поставщика|принять закупку|принять товар от поставщика|оприходовать|оприходование|приход товара|приход от поставщика|приход|товар кабыл алуу|receive goods|goods in|stock in|mal girisi|mal girisi yap|tovar qabuli|tovar kirimi|kirim");

    private static readonly NavTarget SfReturns = Sub(SalesFinance, "salesfinance.returns", "returns", null, "Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar",
        "возвраты|возврат|возвраты покупателей|возврат товара|кайтаруулар|кайтаруу|returns|return|refunds|refund|iadeler|iade|qaytarishlar|qaytarish");
    private static readonly NavTarget SfHistory = Sub(SalesFinance, "salesfinance.history", "history", null, "История", "Тарых", "History", "Geçmiş", "Tarix",
        "история|истории|история продаж|история чеков|тарыхы|тарых|sales history|history|satis gecmisi|gecmis|sotuvlar tarixi|tarix");
    private static readonly NavTarget SfShifts = Sub(SalesFinance, "salesfinance.shifts", "shifts", null, "Смены", "Смендер", "Shifts", "Vardiyalar", "Smenalar",
        "смены|смена|смену|кассовые смены|смендер|смена|shifts|shift|vardiyalar|vardiya|smenalar|smena");

    private static readonly NavTarget SetScreen = Sub(Settings, "settings.screen", "screen", null, "Экран", "Экран", "Screen", "Ekran", "Ekran",
        "экран|настройки экрана|язык|языка|тема|тему|темы|масштаб|вид меню|оформление|screen|display settings|language|theme|ekran|ekran ayarlari|dil|tema|til|mavzu");
    private static readonly NavTarget SetUpdates = Sub(Settings, "settings.updates", "updates", null, "Обновления", "Жаңыртуулар", "Updates", "Güncellemeler", "Yangilanishlar",
        "обновления|обновление|обновить программу|жаңыртуулар|жаңыртуу|updates|update|guncellemeler|guncelleme|yangilanishlar|yangilanish");
    private static readonly NavTarget SetOperations = Sub(Settings, "settings.operations", "operations", null, "Операции", "Операциялар", "Operations", "İşlemler", "Operatsiyalar",
        "операции|настройки операций|операциялар|operations|operation|islemler|operatsiyalar");
    private static readonly NavTarget SetCustom = Sub(Settings, "settings.customization", "customization", null, "Кастомизация", "Ыңгайлаштыруу", "Customization", "Özelleştirme", "Moslashtirish",
        "кастомизация|персонализация|ынгайлаштыруу|customization|personalization|ozellestirme|moslashtirish");
    private static readonly NavTarget SetAccount = Sub(Settings, "settings.account", "account", null, "Аккаунт", "Аккаунт", "Account", "Hesap", "Hisob",
        "аккаунт|учетная запись|мой аккаунт|account|my account|hesap|hesabim|akkaunt|hisob|hisobim");
    private static readonly NavTarget SetEmployees = Sub(Settings, "settings.employees", "employees", null, "Сотрудники", "Кызматкерлер", "Employees", "Personel", "Xodimlar",
        "права|права доступа|доступ сотрудников|права сотрудников|permissions|access rights|yetkiler|yetki|huquqlar|ruxsatlar|кызматкерлердин укуктары");
    private static readonly NavTarget SetKeys = Sub(Settings, "settings.keys", "keys", null, "Клавиши", "Баскычтар", "Keys", "Tuşlar", "Tugmalar",
        "клавиши|горячие клавиши|баскычтар|hotkeys|shortcuts|keyboard shortcuts|kisayollar|kisayol|tuslar|tez tugmalar|tugmalar");

    private static readonly NavTarget[] All =
    {
        S("overview", "overview", "Сводка", "Жыйынтык", "Overview", "Özet", "Umumiy ko'rinish",
            "сводка|сводку|сводки|главная|главную|главной|главная страница|главный экран|обзор|домой|начало|жыйынтык|башкы бет|башкы|home|overview|dashboard|main page|ozet|ana sayfa|genel bakis|bosh sahifa|umumiy korinish|umumiy"),
        S("aiadvisor", "aiadvisor", "Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi",
            "нур советник|советник|советника|кенешчи|нур кенешчи|ии|чат с ии|ии чат|advisor|ai advisor|ai chat|danisman|nur danisman|maslahatchi|nur maslahatchi"),
        Warehouse, WhReceiving, WhRevision, WhWriteOff, WhMovements, WhLabels, WhBulk, WhLow, WhOut, WhExpiry,
        S("calculator", "calculator", "Калькуляция", "Калькуляция", "Pricing calculator", "Hesaplama", "Kalkulyatsiya",
            "калькуляция|калькуляцию|калькулятор|расчет цены|расчет наценки|pricing calculator|calculator|hesaplama|hesap makinesi|kalkulyatsiya|kalkulyator"),
        S("restock", "restock", "Пополнение и сроки", "Толуктоо жана мөөнөттөр", "Restock & expiry", "Stok yenileme ve SKT", "To'ldirish va muddatlar",
            "пополнение|пополнения|пополнить|пополнение и сроки|сроки|срок|толуктоо|толуктоо жана мөөнөттөр|мөөнөттөр|мөөнөт|restock|replenish|replenishment|restock and expiry|stok yenileme|yenileme|skt|toldirish|toldirish va muddatlar|muddatlar"),
        Purchases, PurOrders, PurReceive,
        S("suppliers", "suppliers", "Поставщики", "Жеткирүүчүлөр", "Suppliers", "Tedarikçiler", "Yetkazib beruvchilar",
            "поставщики|поставщик|поставщиков|поставщику|жеткируучулор|жеткируучу|suppliers|supplier|vendors|tedarikciler|tedarikci|yetkazib beruvchilar|yetkazib beruvchi|taminotchilar"),
        S("supplierreturns", "supplierreturns", "Возвраты поставщикам", "Жеткирүүчүлөргө кайтаруулар", "Returns to suppliers", "Tedarikçiye iadeler", "Yetkazib beruvchilarga qaytarishlar",
            "возвраты поставщикам|возврат поставщику|возвраты поставщику|возврат поставщикам|брак поставщику|жеткируучулорго кайтаруулар|returns to suppliers|supplier returns|tedarikciye iade|tedarikciye iadeler|yetkazib beruvchiga qaytarish"),
        S("branches", "branches", "Филиалы", "Филиалдар", "Branches", "Şubeler", "Filiallar",
            "филиалы|филиал|филиалов|филиалдар|branches|branch|subeler|sube|filiallar|filial"),
        SalesFinance, SfReturns, SfHistory, SfShifts,
        S("debts", "debts", "Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari",
            "долги|долг|должники|долги клиентов|кто мне должен|карыз|карыздар|карызкорлор|debts|debt|debtors|receivables|borclar|borc|musteri borclari|qarzlar|qarz|qarzdorlar"),
        S("rentals", "rentals", "Прокат", "Прокат", "Rentals", "Kiralama", "Prokat",
            "прокат|проката|аренда|аренду|ижара|rentals|rental|rent|kiralama|kira|prokat|ijara"),
        S("analytics", "analytics", "Аналитика", "Талдоо", "Analytics", "Analiz", "Analitika",
            "аналитика|аналитику|аналитики|отчеты|отчет|графики|сезонность|талдоо|analytics|reports|report|analiz|raporlar|rapor|analitika|hisobotlar|hisobot"),
        S("abc", "abc", "ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC-tahlil",
            "abc|авс|abc анализ|авс анализ|abc analizi|abc tahlil|abc analysis|abc-анализ"),
        S("profitcash", "profitcash", "Прибыль и деньги", "Пайда жана акча", "Profit & cash", "Kâr ve nakit", "Foyda va pul",
            "прибыль и деньги|прибыль|прибыли|доход|доходы|деньги|движение денег|пайда жана акча|пайда|киреше|акча|profit and cash|profit|income|cash flow|cash|kar ve nakit|kar|nakit|foyda va pul|foyda|pul"),
        S("losssales", "losssales", "Продажи в убыток", "Зыян менен сатуулар", "Sales at a loss", "Zararına satışlar", "Zarariga sotuvlar",
            "продажи в убыток|убыток|убытки|убыточные продажи|продаю в минус|зыян менен сатуулар|зыян|sales at a loss|loss sales|selling at a loss|zararina satislar|zararina satis|zarar|zarariga sotuvlar|zarar"),
        S("sizesreport", "sizesreport", "Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colours", "Bedenler ve renkler", "O'lchamlar va ranglar",
            "размеры и цвета|размеры|размер|цвета|өлчөмдөр жана түстөр|sizes and colours|sizes|sizes and colors|bedenler ve renkler|bedenler|beden|olchamlar va ranglar|olchamlar"),
        S("siteorders", "siteorders", "Заказы с сайта", "Сайттан заказдар", "Website orders", "Web sitesi siparişleri", "Saytdan buyurtmalar",
            "заказы с сайта|заказы сайта|заказ с сайта|интернет заказы|онлайн заказы|сайттан заказдар|website orders|site orders|online orders|web sitesi siparisleri|web siparisleri|saytdan buyurtmalar|onlayn buyurtmalar|buyurtmalar|buyurtma|заказы|заказ"),
        S("siteeditor", "siteeditor", "Редактор сайта", "Сайттын редактору", "Website editor", "Web sitesi düzenleyici", "Sayt muharriri",
            "редактор сайта|редактировать сайт|сайт редактор|сайттын редактору|website editor|site editor|edit website|web sitesi duzenleyici|site duzenleyici|sayt muharriri|sayt tahrirlash"),
        S("sitesettings", "sitesettings", "Настройки сайта", "Сайттын жөндөөлөрү", "Website settings", "Web sitesi ayarları", "Sayt sozlamalari",
            "настройки сайта|настройка сайта|сайттын жондоолору|website settings|site settings|web sitesi ayarlari|site ayarlari|sayt sozlamalari"),
        S("appshop", "appshop", "Магазин в приложении", "Тиркемедеги дүкөн", "Shop in the app", "Uygulamadaki mağaza", "Ilovadagi do'kon",
            "магазин в приложении|магазин приложение|магазин в телефоне|тиркемедеги дукон|shop in the app|app shop|uygulamadaki magaza|ilovadagi dokon|ilova dokon"),
        S("clients", "clients", "Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar",
            "клиенты|клиент|клиентов|покупатели|покупатель|кардарлар|кардар|customers|customer|clients|client|musteriler|musteri|mijozlar|mijoz"),
        S("telegrambot", "telegrambot", "Телеграм-бот", "Телеграм-бот", "Telegram bot", "Telegram botu", "Telegram bot",
            "телеграм бот|телеграм|телеграм-бот|бот|бота|боту|телеграмм|telegram bot|telegram|bot|telegram botu|botu"),
        S("whatsapp", "whatsapp", "WhatsApp", "WhatsApp", "WhatsApp", "WhatsApp", "WhatsApp",
            "ватсап|вотсап|ватсапп|whatsapp|whats app|watsap"),
        S("funnel", "funnel", "Воронка", "Воронка", "Sales funnel", "Satış hunisi", "Savdo voronkasi",
            "воронка|воронку|воронка продаж|sales funnel|funnel|satis hunisi|huni|savdo voronkasi|voronka"),
        S("salary", "salary", "Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi",
            "зарплата|зарплату|зарплаты|табель|сотрудники|сотрудник|сотрудников|ставки|эмгек акы|эмгек акыны|эмгек акысы|эмгек акысын|айлык|кызматкерлер|кызматкер|salary|payroll|timesheet|employees|staff|maas|maaslar|puantaj|calisanlar|ish haqi|xodimlar|xodim|oylik"),
        S("crm", "crm", "NurCRM", "NurCRM", "NurCRM", "NurCRM", "NurCRM",
            "nurcrm|нурсрм|нур срм|срм|crm|nur crm|сайт нурсрм"),
        S("marketplace", "marketplace", "Маркетплейс", "Маркетплейс", "Marketplace", "Pazar yeri", "Marketpleys",
            "маркетплейс|маркетплейса|магазин функций|купить функцию|marketplace|market place|pazar yeri|marketpleys|marketpleysi"),
        Settings, SetScreen, SetUpdates, SetOperations, SetCustom, SetAccount, SetEmployees, SetKeys,
        S("kb", "kb", "База знаний", "Билим базасы", "Knowledge base", "Bilgi bankası", "Bilimlar bazasi",
            "база знаний|базу знаний|знания|инструкция|инструкции|помощь|справка|билим базасы|жардам|knowledge base|help|manual|guide|bilgi bankasi|yardim|bilimlar bazasi|yordam"),
        S("support", "support", "Тех. поддержка", "Тех колдоо", "Support", "Destek", "Texnik yordam",
            "тех поддержка|техподдержка|поддержка|поддержку|удаленная помощь|помощь|тех колдоо|колдоо|жардам|support|tech support|remote help|help|destek|teknik destek|yardim|texnik yordam|yordam"),
        S("logs", "logs", "Журнал ошибок", "Каталар журналы", "Error log", "Hata günlüğü", "Xatolar jurnali",
            "журнал ошибок|журнал|ошибки|ошибок|каталар журналы|каталар|error log|logs|log|errors|hata gunlugu|hata kayitlari|hatalar|xatolar jurnali|xatolar"),
    };

    /// <summary>Все пункты назначения (для подсказки нейросети, голосового инструмента и стенда).</summary>
    public static IReadOnlyList<NavTarget> Targets => All;

    public static NavTarget? ById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Список для нейросети: «id — Склад → Приёмка». Только эти id принимает инструмент open_section.</summary>
    public static string PromptList() =>
        string.Join("; ", All.Where(t => !t.IsFilter || t.Filter is not null).Select(t => t.Id + " — " + t.TitleRu));

    // ── Разбор фразы ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Нижний регистр и свёртка букв пяти языков: ё→е, ү→у, ө→о, ң→н, ı→i, ş→s, ç→c, ğ→g, ö→o, ü→u, апострофы убираются.</summary>
    public static string Fold(string? text)
    {
        var sb = new StringBuilder((text ?? "").Length);
        foreach (var raw in text ?? "")
        {
            var ch = raw switch { 'İ' => 'i', 'I' => 'i', _ => char.ToLowerInvariant(raw) };
            switch (ch)
            {
                case 'ё': sb.Append('е'); break;
                case 'ү': sb.Append('у'); break;
                case 'ө': sb.Append('о'); break;
                case 'ң': sb.Append('н'); break;
                case 'ı': sb.Append('i'); break;
                case 'ş': sb.Append('s'); break;
                case 'ç': sb.Append('c'); break;
                case 'ğ': sb.Append('g'); break;
                case 'ö': sb.Append('o'); break;
                case 'ü': sb.Append('u'); break;
                case '\'' or '’' or 'ʻ' or 'ʼ' or '`' or '‘': break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<string> Tokens(string? text)
    {
        var folded = Fold(text);
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var letter = i < folded.Length && (char.IsLetterOrDigit(folded[i]));
            if (letter && start < 0)
                start = i;
            else if (!letter && start >= 0)
            {
                yield return folded[start..i];
                start = -1;
            }
        }
    }

    /// <summary>Слова-«мусор»: междометия, вежливость, «раздел», «вкладку» и предлоги пяти языков (после свёртки).</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "э", "ээ", "эээ", "эм", "ммм", "ну", "мне", "нам", "мен", "меня", "пожалуйста", "пжл", "плиз", "давай", "давайте", "ка", "же", "тут", "там", "на", "в", "во", "к", "ко", "по", "из", "для",
        "и", "а", "да", "что", "это", "эту", "этот", "мой", "мою", "мое", "свой", "своё", "раздел", "раздела", "разделе", "разделу", "вкладку", "вкладка", "вкладке", "вкладки", "страницу", "страница",
        "окно", "программа", "программе", "программу", "пункт", "меню", "быстро", "сейчас", "срочно", "сначала", "потом", "затем", "еще", "ещё", "мне", "нужно", "надо", "можно", "могу", "можешь",
        "которые", "который", "которая", "какие", "какой", "все", "всё", "весь", "ли", "бы", "то", "ты", "вы", "ай", "ой", "так", "вот", "значит", "типа", "ладно", "хорошо",
        "please", "the", "a", "an", "to", "my", "me", "section", "tab", "page", "window", "menu", "now", "uh", "um", "er", "and", "then", "can", "you", "i", "of", "in", "on", "for",
        "bolum", "bolumu", "bolumunu", "sekme", "sekmesi", "sekmeyi", "sayfa", "sayfasi", "lutfen", "bana", "hadi", "ya", "ve", "bir", "bu", "su", "simdi", "ben", "benim", "ee",
        "bolim", "bolimi", "bolimini", "bolimga", "bolimiga", "varaq", "varagi", "menga", "iltimos", "ni", "ga", "ning", "va", "endi", "hozir", "bering", "ber", "ayting",
        "бөлүм", "болум", "болумун", "болумго", "барак", "баракты", "бетти", "мага", "сураныч", "ушул", "бизге", "ыйык", "эми", "азыр", "бер", "берчи", "берсеныз", "жана", "ж", "менен",
        "ты", "ка", "эле", "деген", "болумуно", "болумунон", "sekmesini", "sayfasini", "bolumune", "дагы", "бул", "тамаша", "найди", "найти", "мина", "найдите",
    };

    private static readonly string[] StrongPrefix = { "откр", "перей", "перех", "зайд", "зайт", "заход", "open", "navig", "launch", "ochi", "ochs", "ocha", "ochg", "acin", "acsan", "acar", "acip" };
    private static readonly HashSet<string> StrongExact = new(StringComparer.Ordinal)
    {
        "ач", "ачып", "ачкыла", "ачкыл", "ачыныз", "аччы", "ачсаныз", "кир", "кирип", "кириниз", "кирчи", "отуп", "отчу", "go", "goto", "ac", "git", "gec", "gecin", "och", "kir", "kiring", "kirish", "otish", "otib", "switch", "take",
    };
    private static readonly string[] StrongFuzz = { "открой", "откройте", "открыть", "открою", "открывай", "перейди", "перейти", "зайди", "зайти" };
    private static readonly string[] WeakPrefix =
    {
        "покаж", "показ", "хоч", "нужн", "нуж", "дай", "найд", "найт", "поищ", "ищи", "посмотр", "глян", "провер", "вывед", "корсот", "show", "see", "view", "find", "want", "need", "display",
        "goster", "bak", "istiyorum", "istiyor", "korsat", "kormoq", "kerak", "izla", "bilmoq", "ko'rs", "корсо",
    };

    private static bool IsStrong(string token)
    {
        if (StrongExact.Contains(token) || StrongPrefix.Any(token.StartsWith))
            return true;
        return token.Length >= 5 && !IsWeak(token) && !Fillers.Contains(token) && StrongFuzz.Any(v => Math.Abs(v.Length - token.Length) <= 1 && Lev(token, v) <= 1);
    }

    private static bool IsWeak(string token) => WeakPrefix.Any(token.StartsWith);

    /// <summary>«Хочу / нужен / want / istiyorum / kerak» — желание перейти, а не просьба показать данные.</summary>
    private static bool IsWish(string token) => new[] { "хоч", "нужн", "нуж", "want", "need", "istiyor", "kerak", "kormoq" }.Any(token.StartsWith);

    /// <summary>Слово «раздел / вкладка / страница / section / tab / bölüm / bo'lim» — явно говорят про место в программе, а не про данные.</summary>
    private static bool IsPlaceWord(string token) => new[] { "раздел", "вкладк", "страниц", "окно", "section", "tab", "page", "bolum", "sekme", "sayfa", "bolim", "varaq", "барак", "болум" }.Any(token.StartsWith);

    /// <summary>Разделы, где «покажи …» — обычно вопрос о данных (ответ таблицей в чате), а не просьба перейти (решение владельца 07.10: вкладки только по «открой»).</summary>
    private static readonly HashSet<string> DataSections = new() { "warehouse", "debts", "clients", "salesfinance", "salary", "analytics", "profitcash", "abc", "overview", "losssales" };

    // ── Нечёткое сопоставление слов ────────────────────────────────────────────────────────────────────────────

    private static int Lev(string a, string b)
    {
        // Левенштейн с перестановкой соседних букв (Дамерау) — опечатки «приемак», «склда».
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Вес совпадения слова фразы <paramref name="q"/> со словом словаря <paramref name="s"/> (0 — не совпало): точно; общее начало
    /// (падежи: «складе», «приёмку»); опечатка в одну–две буквы у длинных слов.</summary>
    private static int Match(string q, string s)
    {
        if (q == s)
            return Math.Max(2, s.Length);
        var lq = q.Length;
        var ls = s.Length;
        var min = Math.Min(lq, ls);
        if (min < 4)
            return 0;
        var cp = 0;
        while (cp < min && q[cp] == s[cp])
            cp++;
        if (cp >= Math.Max(4, (int)Math.Ceiling(min * 0.75)) && Math.Abs(lq - ls) <= 5)
            return min;
        if (min >= 5)
        {
            var allow = min >= 10 ? 2 : 1;
            if (Math.Abs(lq - ls) <= allow + 1)
            {
                var m = min;
                if (Lev(q[..Math.Min(lq, m + 1)], s[..Math.Min(ls, m + 1)]) <= allow || Lev(q[..m], s[..m]) <= allow)
                    return min - 1;
            }
        }
        return 0;
    }

    private sealed record Hit(NavTarget Target, int Score, HashSet<int> Used);

    private static Hit? Score(NavTarget target, List<string> words)
    {
        Hit? best = null;
        foreach (var phrase in target.Phrases)
        {
            var used = new HashSet<int>();
            var total = 0;
            var ok = true;
            foreach (var s in phrase)
            {
                var bestIdx = -1;
                var bestW = 0;
                for (var i = 0; i < words.Count; i++)
                {
                    if (used.Contains(i))
                        continue;
                    var w = Match(words[i], s);
                    if (w > bestW)
                        (bestIdx, bestW) = (i, w);
                }
                if (bestIdx < 0)
                {
                    ok = false;
                    break;
                }
                used.Add(bestIdx);
                total += bestW;
            }
            if (!ok)
                continue;
            if (phrase.Length > 1)
                total += 1;
            if (best is null || total > best.Score)
                best = new Hit(target, total, used);
        }
        return best;
    }

    /// <summary>Разобрать фразу владельца. Слова-связки и вежливость отбрасываются; без команды (открой/покажи/хочу…) фраза — не переход.</summary>
    public static Route Parse(string? text)
    {
        var tokens = Tokens(text).ToList();
        if (tokens.Count == 0 || tokens.Count > 24)
            return Route.None;
        var strong = tokens.Any(IsStrong);
        var weak = !strong && tokens.Any(IsWeak);
        // Слова команд и мусор не участвуют в поиске раздела.
        var content = new List<string>();
        var contentIdx = new List<int>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (Fillers.Contains(t) || IsStrong(t) || IsWeak(t))
                continue;
            content.Add(t);
            contentIdx.Add(i);
        }
        if (!strong && !weak)
            return Route.None;
        if (content.Count == 0)
            return strong ? new Route(RouteKind.Unknown, Array.Empty<NavTarget>(), null, Array.Empty<string>(), true) : Route.None;

        var hits = All.Select(t => Score(t, content)).Where(h => h is not null).Select(h => h!).ToList();
        var navHits = hits.Where(h => !h.Target.IsFilter).OrderByDescending(h => h.Score).ThenByDescending(h => h.Target.Depth).ToList();
        var filterHit = hits.Where(h => h.Target.IsFilter).OrderByDescending(h => h.Score).FirstOrDefault();

        if (navHits.Count == 0 && filterHit is null)
        {
            var left = content.Where(w => w.Length > 2).ToList();
            // Длинная фраза с «открыть» без раздела («сколько стоит открыть магазин») — вопрос, а не переход.
            return strong && left.Count <= 2 ? new Route(RouteKind.Unknown, Array.Empty<NavTarget>(), null, left, true) : Route.None;
        }

        // Фильтр + нужен вход в «Склад»: «найди закончившиеся товары» → Склад с фильтром; «открой приёмку и найди закончившиеся» →
        // приёмка, а фильтр — вторая часть (Companion): программа покажет список закончившихся рядом.
        NavTarget? companion = null;
        List<Hit> chosen;
        if (filterHit is not null)
        {
            // Фильтр объясняет слова сильнее раздела («сроки годности» — не «Пополнение и сроки») или раздел — сам «Склад».
            var filterPrimary = navHits.Count == 0 || navHits[0].Target.Id == "warehouse"
                                || (filterHit.Used.Overlaps(navHits[0].Used) && filterHit.Score > navHits[0].Score);
            if (filterPrimary)
            {
                var used = new HashSet<int>(filterHit.Used);
                if (navHits.Count > 0 && navHits[0].Target.Id == "warehouse")
                    used.UnionWith(navHits[0].Used);
                chosen = new List<Hit> { new Hit(filterHit.Target, filterHit.Score, used) };
            }
            else
            {
                chosen = navHits;
                // «Открой приёмку и найди закончившиеся»: фильтр — вторая часть, список покажет окно рядом с открытой вкладкой.
                if (navHits[0].Target.Section == "warehouse")
                    companion = filterHit.Target;
            }
        }
        else
            chosen = navHits;

        var top = chosen[0];
        // Два раздела в одной фразе без общих слов («открой склад и скажи выручку») — первым открывается упомянутый раньше, остальное — остаток.
        if (chosen.Count > 1)
        {
            var earlier = chosen.Where(h => h.Score >= 5 && h.Target.Section != top.Target.Section && !h.Used.Overlaps(top.Used) && h.Used.Min() < top.Used.Min())
                .OrderBy(h => h.Used.Min()).FirstOrDefault();
            if (earlier is not null)
                top = earlier;
        }
        // «Приём товара»: слово «товара» объясняет корень «Склад», слово «приём» — вкладку; вкладка точнее корня того же раздела.
        if (top.Target.Depth == 0)
        {
            var deeper = chosen.Where(h => h.Target.Section == top.Target.Section && h.Target.Depth == 1 && h.Score >= 4 && !h.Used.Overlaps(top.Used))
                .OrderByDescending(h => h.Score).FirstOrDefault();
            if (deeper is not null)
                top = deeper;
        }
        // Ничья: другой пункт набрал столько же (±1) на тех же словах — не угадываем (вкладка и её раздел — не ничья).
        var rivals = chosen.Where(h => h.Target.Id != top.Target.Id && h.Score >= top.Score - 1 && h.Used.Overlaps(top.Used)
                                       && (h.Target.Section != top.Target.Section || h.Target.Depth == top.Target.Depth))
            .ToList();
        // Одно слово объясняет и сам раздел, и его вкладку («склад» / «приёмка склад») — вкладка уточняет раздел, это не ничья.
        var explained = new HashSet<int>(top.Used);
        var residualWords = new List<string>();
        for (var i = 0; i < content.Count; i++)
            if (content[i].Length > 2 && !explained.Contains(i) && !(companion is not null && Score(companion, content)?.Used.Contains(i) == true)
                && !(filterHit is not null && filterHit.Used.Contains(i)))
                residualWords.Add(content[i]);
        // Слова, объяснённые другим пунктом того же раздела («склад» при «приёмка»), — тоже не остаток.
        foreach (var h in chosen.Skip(1).Where(h => h.Target.Section == top.Target.Section && !rivals.Contains(h)))
            residualWords = residualWords.Where(w => !h.Used.Select(ix => content[ix]).Contains(w)).ToList();

        if (!strong && residualWords.Count > 0)
            return Route.None; // «покажи долги Иванова» — вопрос о данных, а не переход
        // «покажи клиентов / покажи закончившиеся товары» — просят данные, ответ таблицей; переход — только с «открой», «хочу», «раздел …».
        if (!strong && (DataSections.Contains(top.Target.Id) || top.Target.IsFilter) && !tokens.Any(IsWish) && !tokens.Any(IsPlaceWord))
            return Route.None;
        if (rivals.Count > 0)
        {
            var list = new List<NavTarget> { top.Target };
            list.AddRange(rivals.Select(r => r.Target));
            return new Route(RouteKind.Ambiguous, list.DistinctBy(t => t.Id).Take(4).ToList(), companion, residualWords, strong);
        }
        return new Route(RouteKind.Single, new[] { top.Target }, companion, residualWords, strong);
    }

    /// <summary>Пункт по идентификатору или по свободному названию (из инструмента нейросети): точный id; иначе разбор как «открой …».</summary>
    public static Route Resolve(string? idOrPhrase)
    {
        if (ById(idOrPhrase) is { } exact)
            return new Route(RouteKind.Single, new[] { exact }, null, Array.Empty<string>(), true);
        // Старые ключи разделов, которые знала нейросеть: sales/finance/restock…
        var legacy = Fold(idOrPhrase) switch
        {
            "sales" or "finance" => "salesfinance",
            "restock" => "restock",
            _ => null,
        };
        if (legacy is not null && ById(legacy) is { } legacyTarget)
            return new Route(RouteKind.Single, new[] { legacyTarget }, null, Array.Empty<string>(), true);
        return Parse("открой " + idOrPhrase);
    }

    /// <summary>Фраза владельца — явная команда перехода (для подсказки нейросети: список разделов добавляется только в этом случае).</summary>
    public static bool LooksLikeNav(string? text) => Tokens(text).Any(t => IsStrong(t) || IsWeak(t));
}

/// <summary>Результат реального открытия раздела программой владельца (OwnerShellWindow.OpenNavTargetAsync): что показано на самом деле.</summary>
/// <param name="Ok">Раздел действительно стал активным.</param>
/// <param name="Shown">Что открыто по факту: «Склад → Приёмка» или только «Склад», если вкладку выбрать не удалось.</param>
/// <param name="Problem">Почему не получилось (нет прав, раздел скрыт, вкладка недоступна) — для честного ответа.</param>
public sealed record NavOutcome(bool Ok, string Shown, string? Problem = null);

/// <summary>Какие части «сводки магазина» нужны вопросу (ленивая сборка вместо 11 частей на каждый вопрос).</summary>
[Flags]
public enum SummaryParts
{
    None = 0,
    Periods = 1,
    Debts = 2,
    Debtors = 4,
    Abc = 8,
    Analysis = 16,
    Bot = 32,
    Upsell = 64,
    All = Periods | Debts | Debtors | Abc | Analysis | Bot | Upsell,
}

public static class AdvisorIntent
{
    /// <summary>Части сводки по смыслу вопроса. Переход по разделам и болтовня — ничего из сети; цифры продаж — периоды; долги — долги;
    /// непонятный длинный вопрос — как раньше, всё.</summary>
    public static SummaryParts PartsFor(string? question)
    {
        var q = AdvisorNavRouter.Fold(question);
        if (q.Length == 0)
            return SummaryParts.None;
        bool Has(params string[] w) => w.Any(q.Contains);
        var parts = SummaryParts.None;
        if (Has("выруч", "прибыл", "продаж", "продал", "оборот", "недел", "вчера", "сегодн", "месяц", "сравн", "чек", "средн", "доход", "касс",
                "сатуу", "киреше", "пайда", "кечээ", "жума", "бугун", "revenue", "profit", "sales", "week", "yesterday", "today", "month", "ciro", "satis", "kar", "savdo", "tushum", "foyda", "bugun", "dun"))
            parts |= SummaryParts.Periods;
        if (Has("долж", "долг", "карыз", "debt", "borc", "qarz"))
            parts |= SummaryParts.Debts | SummaryParts.Debtors;
        if (Has("abc", "авс", "ходов", "лучш", "худш", "топ", "аутсайд", "best", "worst", "top "))
            parts |= SummaryParts.Abc;
        if (Has("залеж", "не продают", "акци", "скидк", "теряю", "мертв", "проблемн", "жоготуп", "promo", "discount", "indirim", "aksiya", "chegirma"))
            parts |= SummaryParts.Analysis;
        if (Has("бот", "телеграм", "сценар", "telegram", "bot"))
            parts |= SummaryParts.Bot;
        if (Has("допродаж", "с этим бер", "рекомендац", "upsell"))
            parts |= SummaryParts.Upsell;
        if (parts != SummaryParts.None)
            return parts;
        // Склад/товары/сроки/зарплата — локальные части и свои запросы вопроса; из сети сводка не нужна.
        if (Has("склад", "товар", "остат", "закуп", "заказ", "срок", "годн", "зарплат", "табел", "сотрудн", "клиент", "цен", "наценк", "фото", "кампа", "ombor", "depo", "stock", "product"))
            return SummaryParts.None;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        // Приветствие, благодарность, «что умеешь» — без данных. Длинный непонятный вопрос — всё, как раньше.
        return words <= 5 ? SummaryParts.None : SummaryParts.All;
    }
}
