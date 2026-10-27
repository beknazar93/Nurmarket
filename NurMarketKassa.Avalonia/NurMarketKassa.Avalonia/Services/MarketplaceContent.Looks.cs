using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-1, раздел 5.14–5.18): контент пакетов «Стандарта», видов кассы, экрана покупателя,
// тем и редактора тем. LongDescription видов и тем уже есть на 5 языках в KassaLayouts / AccentThemeService — здесь не переписывается.
internal static partial class MarketplaceContent
{
    // ------------------------------------------------------------------ 5.14 Пакеты «Стандарта» на «Старте»

    /// <summary>«Что откроется» для пакета (Features). Нужен каталогу при сборке элемента: Features — init, после Gated() его не поменять.</summary>
    internal static string[] PackFeatures(string slug) => slug switch
    {
        TariffGate.Packs.Ai =>
        [
            T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"),
            T("ИИ в Telegram-боте", "Telegram-боттогу ИИ", "AI in the Telegram bot", "Telegram botunda yapay zekâ", "Telegram-botda SI"),
            T("Поиск фото товаров", "Товардын сүрөтүн издөө", "Product photo search", "Ürün fotoğrafı arama", "Mahsulot rasmini qidirish"),
            T("Ключи ИИ", "ИИ ачкычтары", "AI keys", "Yapay zekâ anahtarları", "SI kalitlari"),
        ],
        TariffGate.Packs.Clients =>
        [
            T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar"),
            "WhatsApp",
            T("Воронка продаж", "Сатуу воронкасы", "Sales funnel", "Satış hunisi", "Savdo voronkasi"),
        ],
        TariffGate.Packs.SalesAnalytics =>
        [
            T("Продажи и финансы", "Сатуулар жана каржы", "Sales and finance", "Satışlar ve finans", "Sotuvlar va moliya"),
            T("Аналитика", "Аналитика", "Analytics", "Analiz", "Analitika"),
            T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC-tahlil"),
            T("Прибыль и деньги", "Пайда жана акча", "Profit and cash", "Kâr ve nakit", "Foyda va pul"),
            T("Продажи в убыток", "Зыян менен сатуулар", "Sales at a loss", "Zararına satışlar", "Zarariga sotuvlar"),
            T("План продаж", "Сатуу планы", "Sales plan", "Satış planı", "Sotuv rejasi"),
            T("Размеры и цвета (для магазинов одежды)", "Өлчөмдөр жана түстөр (кийим дүкөндөрү үчүн)", "Sizes and colors (for clothing shops)", "Bedenler ve renkler (giyim mağazaları için)", "O'lchamlar va ranglar (kiyim do'konlari uchun)"),
        ],
        TariffGate.Packs.Restock =>
        [
            T("Пополнение", "Толуктоо", "Restock", "Stok yenileme", "To'ldirish"),
            T("Сроки годности", "Жарактуулук мөөнөттөрү", "Expiry dates", "Son kullanma tarihleri", "Yaroqlilik muddatlari"),
        ],
        TariffGate.Packs.Salary =>
        [
            T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi"),
        ],
        TariffGate.Packs.Debts =>
        [
            T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari"),
            T("Оплата долга на кассе", "Кассада карыз төлөө", "Debt payment at the till", "Kasada borç ödeme", "Kassada qarz to'lash"),
            T("Отложенные чеки", "Кийинкиге калтырылган чектер", "Parked receipts", "Bekletilen fişler", "Kechiktirilgan cheklar"),
        ],
        TariffGate.Packs.Service =>
        [
            T("NurCRM в программе", "Программада NurCRM", "NurCRM in the program", "Programda NurCRM", "Dasturda NurCRM"),
            T("База знаний", "Билим базасы", "Knowledge base", "Bilgi bankası", "Bilimlar bazasi"),
            T("Тех. поддержка", "Тех. колдоо", "Tech support", "Teknik destek", "Texnik yordam"),
            T("Журнал ошибок", "Каталар журналы", "Error log", "Hata günlüğü", "Xatolar jurnali"),
        ],
        _ => [],
    };

    /// <summary>Общая плашка пакетов: что пакетами не открывается (ТЗ 5.14).</summary>
    internal static string PackNote() => T(
        "Закупки, Поставщики и Филиалы ни одним пакетом не открываются — только переходом на «Стандарт».",
        "Сатып алуулар, Жеткирүүчүлөр жана Филиалдар эч бир пакет менен ачылбайт — «Стандартка» өткөндө гана.",
        "Purchases, Suppliers and Branches are not opened by any pack — only by switching to “Standard”.",
        "Satın almalar, Tedarikçiler ve Şubeler hiçbir paketle açılmaz — yalnızca «Standart»a geçişle açılır.",
        "Xaridlar, Yetkazib beruvchilar va Filiallar hech bir paket bilan ochilmaydi — faqat «Standart»ga o'tish bilan.");

    private static void Pack(MarketplaceItem i)
    {
        var slug = i.Id["pack:".Length..];
        i.PriceTerms = PackTerms();
        i.HowTo =
        [
            T("Нажмите «Активировать» и введите серийный ключ.", "«Активдештирүү» баскычын басып, сериялык ачкычты киргизиңиз.",
                "Press “Activate” and enter the serial key.", "«Etkinleştir»e basıp seri anahtarı girin.", "«Faollashtirish» tugmasini bosib, seriya kalitini kiriting."),
            T("Разделы появятся в меню программы сразу.", "Бөлүмдөр программанын менюсунда дароо пайда болот.",
                "The sections appear in the program menu at once.", "Bölümler program menüsünde hemen görünür.", "Bo'limlar dastur menyusida darhol paydo bo'ladi."),
        ];
        switch (slug)
        {
            case TariffGate.Packs.Ai:
                i.IconKey = "robot-outline";
                i.Tagline = T("Советы, ИИ в боте и поиск фото товаров", "Кеңештер, боттогу ИИ жана товардын сүрөтүн издөө",
                    "Advice, AI in the bot and product photo search", "Tavsiyeler, bottaki yapay zekâ ve ürün fotoğrafı arama", "Maslahatlar, botdagi SI va mahsulot rasmini qidirish");
                i.Benefits =
                [
                    B("robot-outline", T("Советы по продажам и закупкам", "Сатуу жана сатып алуу боюнча кеңештер", "Sales and purchasing advice", "Satış ve satın alma tavsiyeleri", "Sotuv va xarid bo'yicha maslahatlar"),
                        T("Нур Советник даёт советы по продажам и закупкам прямо в программе владельца.",
                          "Нур Кеңешчи ээсинин программасында эле сатуу жана сатып алуу боюнча кеңештерди берет.",
                          "Nur Advisor gives sales and purchasing advice right in the owner app.",
                          "Nur Danışman, sahip programında satış ve satın alma tavsiyeleri verir.",
                          "Nur Maslahatchi egasi dasturida sotuv va xarid bo'yicha maslahat beradi.")),
                    B("image-outline", T("Фото товара из интернета", "Товардын сүрөтү интернеттен", "Product photos from the internet", "İnternetten ürün fotoğrafı", "Mahsulot rasmi internetdan"),
                        T("Поиск фото товаров и поиск в интернете — прямо из программы.",
                          "Товардын сүрөтүн жана интернеттен издөө — программанын өзүнөн.",
                          "Product photo search and web search — right from the program.",
                          "Ürün fotoğrafı ve internet araması — doğrudan programdan.",
                          "Mahsulot rasmini va internetdan qidirish — to'g'ridan-to'g'ri dasturdan.")),
                ];
                break;
            case TariffGate.Packs.Clients:
                i.IconKey = "account-group-outline";
                i.Tagline = T("Клиенты, воронка и переписка в WhatsApp", "Кардарлар, воронка жана WhatsApp'та жазышуу",
                    "Customers, funnel and WhatsApp chats", "Müşteriler, huni ve WhatsApp yazışmaları", "Mijozlar, voronka va WhatsApp yozishmalari");
                i.Benefits =
                [
                    B("account-group-outline", T("База клиентов с долгами и скидками", "Карыздары жана арзандатуулары бар кардарлар базасы", "A customer base with debts and discounts", "Borç ve indirimli müşteri tabanı", "Qarz va chegirmalar bilan mijozlar bazasi"),
                        T("Клиенты хранятся в базе вместе с их долгами и скидками.",
                          "Кардарлар карыздары жана арзандатуулары менен бирге базада сакталат.",
                          "Customers are kept in the base together with their debts and discounts.",
                          "Müşteriler borçları ve indirimleriyle birlikte tabanda tutulur.",
                          "Mijozlar qarzlari va chegirmalari bilan birga bazada saqlanadi.")),
                    B("message-text-outline", T("Переписка в WhatsApp из программы", "Программадан WhatsApp'та жазышуу", "WhatsApp chats from the program", "Programdan WhatsApp yazışması", "Dasturdan WhatsApp'da yozishish"),
                        T("Клиентам можно писать в WhatsApp прямо из программы; в пакет входит и воронка продаж.",
                          "Кардарларга WhatsApp'ка программанын өзүнөн жазууга болот; пакетке сатуу воронкасы да кирет.",
                          "You can write to customers in WhatsApp right from the program; the sales funnel is part of the pack.",
                          "Müşterilere WhatsApp'tan doğrudan programdan yazabilirsiniz; pakete satış hunisi de dahildir.",
                          "Mijozlarga WhatsApp'da to'g'ridan-to'g'ri dasturdan yozish mumkin; paketga savdo voronkasi ham kiradi.")),
                ];
                break;
            case TariffGate.Packs.SalesAnalytics:
                i.IconKey = "chart-line";
                i.Tagline = T("Прибыль, ABC-анализ и продажи в убыток", "Пайда, ABC-анализ жана зыян менен сатуулар",
                    "Profit, ABC analysis and sales at a loss", "Kâr, ABC analizi ve zararına satışlar", "Foyda, ABC-tahlil va zarariga sotuvlar");
                i.Benefits =
                [
                    B("chart-line", T("Видно, что приносит прибыль", "Эмне пайда алып келерин көрөсүз", "See what brings profit", "Neyin kâr getirdiğini görün", "Nima foyda keltirishini ko'rasiz"),
                        T("Прибыль и деньги, ABC-анализ и аналитика продаж собраны в одном пакете.",
                          "Пайда жана акча, ABC-анализ жана сатуулардын аналитикасы бир пакетте чогултулган.",
                          "Profit and cash, ABC analysis and sales analytics are gathered in one pack.",
                          "Kâr ve nakit, ABC analizi ve satış analizi tek pakette toplanmıştır.",
                          "Foyda va pul, ABC-tahlil va sotuv analitikasi bitta paketda jamlangan.")),
                    B("cash-multiple", T("Продажи в убыток — отдельным списком", "Зыян менен сатуулар — өзүнчө тизме", "Sales at a loss — as a separate list", "Zararına satışlar — ayrı bir liste", "Zarariga sotuvlar — alohida ro'yxat"),
                        T("Продажи в убыток собраны в отдельном разделе.",
                          "Зыян менен сатуулар өзүнчө бөлүмгө чогултулган.",
                          "Sales at a loss are gathered in a separate section.",
                          "Zararına satışlar ayrı bir bölümde toplanır.",
                          "Zarariga sotuvlar alohida bo'limda jamlangan.")),
                ];
                break;
            case TariffGate.Packs.Restock:
                i.IconKey = "package-variant-closed";
                i.Tagline = T("Что пора заказать и что скоро испортится", "Эмнеге буйрутма берүү керек жана эмненин мөөнөтү бүтүп баратат",
                    "What to reorder and what is about to expire", "Neyin sipariş edileceği ve neyin süresinin dolmak üzere olduğu", "Nimani buyurtma qilish va nimaning muddati tugayapti");
                i.Benefits =
                [
                    B("cart-arrow-down", T("Что пора заказать", "Эмнеге буйрутма берүү керек", "What to reorder", "Sipariş edilecekler", "Nimani buyurtma qilish kerak"),
                        T("Раздел показывает товары, которые пора заказать.",
                          "Бөлүм буйрутма берүү керек болгон товарларды көрсөтөт.",
                          "The section shows the products that are due for reordering.",
                          "Bölüm sipariş edilmesi gereken ürünleri gösterir.",
                          "Bo'lim buyurtma qilish vaqti kelgan mahsulotlarni ko'rsatadi.")),
                    B("clock-outline", T("Что скоро испортится", "Эмненин мөөнөтү бүтүп баратат", "What is about to expire", "Neyin süresi dolmak üzere", "Nimaning muddati tugayapti"),
                        T("Видно, у каких товаров заканчивается срок годности.",
                          "Кайсы товарлардын жарактуулук мөөнөтү бүтүп баратканы көрүнөт.",
                          "You can see which products are nearing their expiry date.",
                          "Hangi ürünlerin son kullanma tarihinin yaklaştığı görünür.",
                          "Qaysi mahsulotlarning yaroqlilik muddati tugayotgani ko'rinadi.")),
                ];
                break;
            case TariffGate.Packs.Salary:
                i.IconKey = "cash-multiple";
                i.Tagline = T("Зарплата по продажам и сменам", "Сатуу жана сменалар боюнча эмгек акы",
                    "Salaries by sales and shifts", "Satış ve vardiyalara göre maaş", "Sotuv va smenalar bo'yicha ish haqi");
                i.Benefits =
                [
                    B("cash-multiple", T("Зарплата по продажам и сменам", "Сатуу жана сменалар боюнча эмгек акы", "Salaries by sales and shifts", "Satış ve vardiyalara göre maaş", "Sotuv va smenalar bo'yicha ish haqi"),
                        T("Зарплата сотрудников считается по их продажам и сменам.",
                          "Кызматкерлердин эмгек акысы алардын сатуулары жана сменалары боюнча эсептелет.",
                          "Staff salaries are calculated from their sales and shifts.",
                          "Personel maaşı satışlarına ve vardiyalarına göre hesaplanır.",
                          "Xodimlar ish haqi ularning sotuvlari va smenalari bo'yicha hisoblanadi.")),
                ];
                break;
            case TariffGate.Packs.Debts:
                i.IconKey = "book-account-outline";
                i.Tagline = T("Долги клиентов и отложенные чеки", "Кардарлардын карыздары жана кийинкиге калтырылган чектер",
                    "Customer debts and parked receipts", "Müşteri borçları ve bekletilen fişler", "Mijozlar qarzlari va kechiktirilgan cheklar");
                i.Benefits =
                [
                    B("book-account-outline", T("Долги под контролем", "Карыздар көзөмөлдө", "Debts under control", "Borçlar kontrol altında", "Qarzlar nazoratda"),
                        T("Долги покупателей можно оплатить прямо на кассе.",
                          "Сатып алуучулардын карыздарын түз кассада төлөсө болот.",
                          "Customers' debts can be paid right at the till.",
                          "Müşteri borçları doğrudan kasada ödenebilir.",
                          "Xaridorlar qarzini to'g'ridan-to'g'ri kassada to'lash mumkin.")),
                    B("receipt-text-outline", T("Отложить чек и вернуться к нему", "Чекти кийинкиге калтырып, кайра кайтуу", "Park a receipt and return to it", "Fişi bekletip sonra geri dönün", "Chekni kechiktirib, unga qaytish"),
                        T("Чек можно отложить и вернуться к нему позже.",
                          "Чекти кийинкиге калтырып, кийинчерээк ага кайтууга болот.",
                          "A receipt can be parked and returned to later.",
                          "Fiş bekletilebilir ve daha sonra geri dönülebilir.",
                          "Chekni kechiktirib, keyinroq unga qaytish mumkin.")),
                ];
                break;
            case TariffGate.Packs.Service:
                i.IconKey = "lifebuoy";
                i.Tagline = T("NurCRM, база знаний и поддержка в программе", "Программада NurCRM, билим базасы жана колдоо",
                    "NurCRM, knowledge base and support in the program", "Programda NurCRM, bilgi bankası ve destek", "Dasturda NurCRM, bilimlar bazasi va yordam");
                i.Benefits =
                [
                    B("lifebuoy", T("Помощь без звонка", "Чалбай эле жардам", "Help without a call", "Aramadan yardım", "Qo'ng'iroqsiz yordam"),
                        T("Сайт NurCRM внутри программы, удалённая тех. поддержка и журнал ошибок.",
                          "Программанын ичинде NurCRM сайты, алыстан тех. колдоо жана каталар журналы.",
                          "The NurCRM site inside the program, remote tech support and an error log.",
                          "Program içinde NurCRM sitesi, uzaktan teknik destek ve hata günlüğü.",
                          "Dastur ichida NurCRM sayti, masofaviy texnik yordam va xatolar jurnali.")),
                    B("play-box-outline", T("Обучение со снимками", "Сүрөттөр менен окутуу", "Learning with screenshots", "Ekran görüntüleriyle eğitim", "Rasmlar bilan o'qitish"),
                        T("База знаний открывается прямо в программе.",
                          "Билим базасы программанын өзүндө ачылат.",
                          "The knowledge base opens right in the program.",
                          "Bilgi bankası doğrudan programda açılır.",
                          "Bilimlar bazasi to'g'ridan-to'g'ri dasturda ochiladi.")),
                ];
                break;
        }
    }

    // ------------------------------------------------------------------ 5.15 Виды кассы
    private static void Layout(MarketplaceItem i)
    {
        i.IconKey = "view-dashboard-outline";
        i.Tagline = i.ShortDescription;
        i.KeyFacts =
        [
            T("Без перезапуска", "Кайра ачпай", "No restart", "Yeniden başlatmadan", "Qayta ishga tushirmasdan"),
            T("Экран покупателя тоже", "Сатып алуучунун экраны да", "Customer display too", "Müşteri ekranı da", "Xaridor ekrani ham"),
        ];
        i.HowTo =
        [
            T("Нажмите «Применить вид» — касса перестроится сразу.", "«Көрүнүштү колдонуу» баскычын басыңыз — касса дароо өзгөрөт.",
                "Press “Apply layout” — the till rearranges at once.", "«Görünümü uygula»ya basın — kasa hemen yeniden düzenlenir.", "«Ko'rinishni qo'llash» tugmasini bosing — kassa darhol qayta quriladi."),
            T("Вернуть прежний — здесь или в Настройки → Экран.", "Мурункусуна кайтуу — ушул жерден же Жөндөөлөр → Экран.",
                "To go back, use this page or Settings → Screen.", "Eskisine dönmek için burası veya Ayarlar → Ekran.", "Avvalgisiga qaytish — shu yerda yoki Sozlamalar → Ekran."),
        ];
        i.Slides =
        [
            S(1, LightVariant()),
            S(2, DarkVariant()),
            S(3, T("Экран покупателя в этом виде", "Ушул көрүнүштөгү сатып алуучунун экраны", "The customer display in this layout", "Bu görünümde müşteri ekranı", "Shu ko'rinishdagi xaridor ekrani")),
        ];

        switch (i.Id["layout:".Length..])
        {
            case "standard":
                i.Benefits =
                [
                    B("view-grid-outline", T("Привычно", "Көнүмүш", "Familiar", "Alışıldık", "Odatiy"),
                        T("С этим видом касса ставится; каталог плитками слева, чек справа.",
                          "Касса ушул көрүнүш менен орнотулат; сол жакта плиткалар, оң жакта чек.",
                          "The till is installed with this layout; catalog tiles on the left, receipt on the right.",
                          "Kasa bu görünümle kurulur; solda katalog karoları, sağda fiş.",
                          "Kassa shu ko'rinish bilan o'rnatiladi; chapda plitkalar, o'ngda chek.")),
                    B("cursor-move", T("Ширина колонок меняется", "Тилкелердин туурасы өзгөрөт", "Column widths change", "Sütun genişlikleri değişir", "Ustunlar kengligi o'zgaradi"),
                        T("Подстройте каталог и чек под свой экран.",
                          "Каталог менен чекти экраныңызга ылайыктаңыз.",
                          "Adjust the catalog and receipt to your screen.",
                          "Katalog ve fişi ekranınıza göre ayarlayın.",
                          "Katalog va chekni ekraningizga moslang.")),
                ];
                i.Audience = [T("Любой магазин", "Каалаган дүкөн", "Any shop", "Her mağaza", "Istalgan do'kon")];
                break;
            case "table":
                i.Benefits =
                [
                    B("barcode-scan", T("Для клавиатуры и сканера", "Баскычтоп жана сканер үчүн", "For keyboard and scanner", "Klavye ve okuyucu için", "Klaviatura va skaner uchun"),
                        T("Чек плотной таблицей и поле штрихкода сверху.",
                          "Чек тыгыз таблица, өйдө жакта штрихкод талаасы.",
                          "A dense receipt table and a barcode field on top.",
                          "Sık fiş tablosu ve üstte barkod alanı.",
                          "Zich chek jadvali va tepada shtrix-kod maydoni.")),
                    B("palette-outline", T("Цветные кнопки оплаты", "Түстүү төлөм баскычтары", "Colored payment buttons", "Renkli ödeme düğmeleri", "Rangli to'lov tugmalari"),
                        T("Быстрые товары и оплата справа.",
                          "Тез товарлар жана төлөм оң жакта.",
                          "Quick products and payment on the right.",
                          "Hızlı ürünler ve ödeme sağda.",
                          "Tezkor mahsulotlar va to'lov o'ngda.")),
                ];
                i.Audience = [T("Супермаркет, поток покупателей", "Супермаркет, сатып алуучулар агымы", "Supermarket, heavy customer flow", "Süpermarket, yoğun müşteri akışı", "Supermarket, xaridorlar oqimi")];
                break;
            case "cards":
                i.Benefits =
                [
                    B("view-grid-outline", T("Крупные карточки", "Чоң карточкалар", "Large cards", "Büyük kartlar", "Katta kartochkalar"),
                        T("Категории чипами, товары карточками по центру.",
                          "Категориялар чип менен, товарлар ортодо карточка түрүндө.",
                          "Categories as chips, products as cards in the center.",
                          "Kategoriler çip olarak, ürünler ortada kart olarak.",
                          "Kategoriyalar chip ko'rinishida, mahsulotlar o'rtada kartochka sifatida.")),
                    B("gesture-tap", T("Большая зелёная «Оплатить»", "Чоң жашыл «Төлөө»", "A big green “Pay” button", "Büyük yeşil «Öde» düğmesi", "Katta yashil «To'lash» tugmasi"),
                        T("Чек справа с кнопками +/−.",
                          "Оң жакта +/− баскычтары бар чек.",
                          "The receipt on the right with +/− buttons.",
                          "Sağda +/− düğmeli fiş.",
                          "O'ngda +/− tugmali chek.")),
                ];
                i.Audience = [T("Сенсорный экран, кафе", "Сенсордук экран, кафе", "Touch screen, cafe", "Dokunmatik ekran, kafe", "Sensorli ekran, kafe")];
                break;
            case "minimal":
                i.Benefits =
                [
                    B("eye-outline", T("Ничего лишнего", "Ашыкча эч нерсе жок", "Nothing extra", "Fazlalık yok", "Ortiqcha narsa yo'q"),
                        T("Много воздуха и крупные цветные плитки.",
                          "Көп бош орун жана чоң түстүү плиткалар.",
                          "Lots of space and large colored tiles.",
                          "Bol boşluk ve büyük renkli karolar.",
                          "Ko'p bo'sh joy va katta rangli plitkalar.")),
                    B("cash", T("Огромная «Оплатить» с суммой", "Суммасы менен чоң «Төлөө»", "A huge “Pay” button with the total", "Tutarlı dev «Öde» düğmesi", "Summali katta «To'lash» tugmasi"),
                        T("Сумма видна издалека.",
                          "Сумма алыстан көрүнөт.",
                          "The total is visible from afar.",
                          "Tutar uzaktan görünür.",
                          "Summa uzoqdan ko'rinadi.")),
                ];
                i.Audience = [T("Небольшой ассортимент", "Ассортименти аз", "A small range", "Küçük ürün yelpazesi", "Assortimenti kam")];
                break;
            case "pro":
                i.Benefits =
                [
                    B("magnify", T("Крупный поиск", "Чоң издөө", "Large search", "Büyük arama", "Katta qidiruv"),
                        T("Список результатов и боковая панель разделов.",
                          "Натыйжалар тизмеси жана бөлүмдөрдүн каптал панели.",
                          "A results list and a side panel of sections.",
                          "Sonuç listesi ve bölümlerin yan paneli.",
                          "Natijalar ro'yxati va bo'limlarning yon paneli.")),
                    B("account-group-outline", T("Блок покупателя в чеке", "Чекте сатып алуучу блогу", "A customer block in the receipt", "Fişte müşteri bloğu", "Chekda xaridor bloki"),
                        T("Покупатель и итоги — справа.",
                          "Сатып алуучу жана жыйынтык — оң жакта.",
                          "The customer and totals are on the right.",
                          "Müşteri ve toplamlar sağda.",
                          "Xaridor va yakunlar — o'ngda.")),
                ];
                i.Audience = [T("Большой ассортимент", "Ассортименти чоң", "A large range", "Geniş ürün yelpazesi", "Assortimenti katta")];
                break;
            case "onec":
                i.Benefits =
                [
                    B("barcode-scan", T("Как в 1С", "1С'теги сыяктуу", "Like in 1C", "1C'deki gibi", "1C dagidek"),
                        T("Строка сканера, крупная активная строка чека, итоги справа.",
                          "Сканер сабы, чектин чоң активдүү сабы, оң жакта жыйынтык.",
                          "A scanner line, a large active receipt row, totals on the right.",
                          "Okuyucu satırı, büyük etkin fiş satırı, sağda toplamlar.",
                          "Skaner qatori, chekning katta faol qatori, o'ngda yakunlar.")),
                    B("check", T("Только сканер и избранное", "Сканер жана тандалмалар гана", "Scanner and favorites only", "Yalnızca okuyucu ve favoriler", "Faqat skaner va sevimlilar"),
                        T("Товар добавляется сканером или из избранного.",
                          "Товар сканер же тандалмалар аркылуу кошулат.",
                          "A product is added by scanner or from favorites.",
                          "Ürün okuyucuyla veya favorilerden eklenir.",
                          "Mahsulot skaner yoki sevimlilar orqali qo'shiladi.")),
                ];
                i.Audience = [T("Кто привык к 1С", "1С'ке көнгөндөр", "Those used to 1C", "1C'ye alışkın olanlar", "1C ga ko'nikkanlar")];
                break;
        }
    }

    // ------------------------------------------------------------------ 5.16 Экран покупателя
    private static void CustomerDisplay(MarketplaceItem i)
    {
        i.IconKey = "monitor-account";
        i.Tagline = T("Покупатель видит каждую позицию и сумму", "Сатып алуучу ар бир позицияны жана сумманы көрөт",
            "Customers see every item and the total", "Müşteri her kalemi ve toplamı görür", "Xaridor har bir pozitsiya va summani ko'radi");
        i.KeyFacts =
        [
            T("Бесплатно", "Акысыз", "Free", "Ücretsiz", "Bepul"),
            T("QR-код оплаты", "Төлөмдүн QR-коду", "Payment QR code", "Ödeme QR kodu", "To'lov QR-kodi"),
            T("Предпросмотр без монитора", "Мониторсуз алдын ала көрүү", "Preview without a monitor", "Monitörsüz önizleme", "Monitorsiz ko'rinish"),
        ];
        i.Benefits =
        [
            B("eye-check-outline", T("Меньше вопросов на кассе", "Кассада суроо азыраак", "Fewer questions at the till", "Kasada daha az soru", "Kassada savol kamroq"),
                T("Покупатель сам видит, что пробито и сколько платить.",
                  "Сатып алуучу эмне урулганын жана канча төлөөрүн өзү көрөт.",
                  "Customers see for themselves what has been rung up and how much to pay.",
                  "Müşteri neyin vurulduğunu ve ne kadar ödeyeceğini kendisi görür.",
                  "Xaridor nima urilganini va qancha to'lashini o'zi ko'radi.")),
            B("format-text", T("Свои надписи", "Өз жазууларыңыз", "Your own captions", "Kendi yazılarınız", "O'z yozuvlaringiz"),
                T("Надпись сверху, приветствие, строка под ним и текст после оплаты.",
                  "Өйдөдөгү жазуу, саламдашуу, анын астындагы сап жана төлөмдөн кийинки текст.",
                  "A caption on top, a greeting, a line under it and the text after payment.",
                  "Üstteki yazı, karşılama, altındaki satır ve ödemeden sonraki metin.",
                  "Tepadagi yozuv, salomlashuv, uning ostidagi qator va to'lovdan keyingi matn.")),
            B("palette-outline", T("Цвета магазина", "Дүкөндүн түстөрү", "Store colors", "Mağaza renkleri", "Do'kon ranglari"),
                T("Как тема кассы или свои: фон, акцент, текст; светлый или тёмный.",
                  "Кассанын темасындай же өзүңүздүкү: фон, акцент, текст; ачык же караңгы.",
                  "Like the till theme or your own: background, accent, text; light or dark.",
                  "Kasa temasındaki gibi veya kendi renkleriniz: arka plan, vurgu, metin; açık veya koyu.",
                  "Kassa mavzusidagidek yoki o'zingizniki: fon, urg'u, matn; yorug' yoki qorong'i.")),
            B("qrcode", T("QR-код оплаты", "Төлөмдүн QR-коду", "Payment QR", "Ödeme QR'ı", "To'lov QR-kodi"),
                T("Можно показать покупателю QR для оплаты.",
                  "Сатып алуучуга төлөм үчүн QR көрсөтсө болот.",
                  "You can show the customer a QR code for payment.",
                  "Müşteriye ödeme için QR gösterilebilir.",
                  "Xaridorga to'lov uchun QR ko'rsatish mumkin.")),
            B("image-outline", T("Логотип и реклама", "Логотип жана жарнама", "Logo and ads", "Logo ve reklam", "Logotip va reklama"),
                T("Логотип и рекламный блок — в Настройки → Монитор.",
                  "Логотип жана жарнама блогу — Жөндөөлөр → Монитор.",
                  "The logo and the ad block are in Settings → Monitor.",
                  "Logo ve reklam bloğu — Ayarlar → Monitör.",
                  "Logotip va reklama bloki — Sozlamalar → Monitor.")),
        ];
        i.HowTo =
        [
            T("Подключите второй монитор (предпросмотр работает и без него).", "Экинчи мониторду туташтырыңыз (алдын ала көрүү ансыз да иштейт).",
                "Connect a second monitor (the preview works without one).", "İkinci monitörü bağlayın (önizleme onsuz da çalışır).", "Ikkinchi monitorni ulang (ko'rinish usiz ham ishlaydi)."),
            T("«Открыть редактор экрана покупателя».", "«Сатып алуучунун экраны редакторун ачуу».",
                "Press “Open the customer display editor”.", "«Müşteri ekranı düzenleyicisini aç».", "«Xaridor ekrani muharririni ochish»."),
            T("Настройте и сохраните; Отмена/Esc ничего не меняет.", "Жөндөп, сактаңыз; Жокко чыгаруу/Esc эч нерсени өзгөртпөйт.",
                "Configure and save; Cancel/Esc changes nothing.", "Ayarlayıp kaydedin; İptal/Esc hiçbir şeyi değiştirmez.", "Sozlab saqlang; Bekor qilish/Esc hech narsani o'zgartirmaydi."),
        ];
        i.Audience =
        [
            T("Магазины со вторым монитором", "Экинчи монитору бар дүкөндөр", "Shops with a second monitor", "İkinci monitörlü mağazalar", "Ikkinchi monitorli do'konlar"),
            T("Кафе и фастфуд", "Кафе жана фастфуд", "Cafes and fast food", "Kafe ve fast food", "Kafe va fastfud"),
        ];
        i.Slides =
        [
            S(1, T("Список покупок и сумма", "Сатып алуулар тизмеси жана сумма", "The item list and the total", "Alışveriş listesi ve toplam", "Xaridlar ro'yxati va summa")),
            S(2, T("Приветствие между покупателями", "Сатып алуучулардын ортосунда саламдашуу", "A greeting between customers", "Müşteriler arasında karşılama", "Xaridorlar orasida salomlashuv")),
            S(3, T("Экран после оплаты", "Төлөмдөн кийинки экран", "The screen after payment", "Ödemeden sonra ekran", "To'lovdan keyingi ekran")),
            S(4, T("Редактор: надписи, цвета, что показывать", "Редактор: жазуулар, түстөр, эмнени көрсөтүү", "Editor: captions, colors, what to show", "Düzenleyici: yazılar, renkler, neyin gösterileceği", "Muharrir: yozuvlar, ranglar, nimani ko'rsatish")),
            S(5, T("Экран меняется вместе с видом кассы", "Экран кассанын көрүнүшү менен бирге өзгөрөт", "The screen changes with the till layout", "Ekran kasa görünümüyle birlikte değişir", "Ekran kassa ko'rinishi bilan birga o'zgaradi")),
        ];
        i.Demo = Demo(T("Настройка экрана покупателя", "Сатып алуучунун экранын жөндөө", "Setting up the customer display", "Müşteri ekranı ayarı", "Xaridor ekranini sozlash"));
    }

    // ------------------------------------------------------------------ 5.17 Темы
    private static void Theme(MarketplaceItem i)
    {
        i.IconKey = "palette-swatch-outline";
        var themeId = i.Id["theme:".Length..];
        i.HowTo =
        [
            T("Нажмите «Применить тему» — программа перекрасится сразу.", "«Теманы колдонуу» баскычын басыңыз — программа дароо өзгөрөт.",
                "Press “Apply theme” — the program changes color at once.", "«Temayı uygula»ya basın — program hemen yeniden renklenir.", "«Mavzuni qo'llash» tugmasini bosing — dastur darhol qayta bo'yaladi."),
            T("Светлый/тёмный вариант — переключатель «Тема» в шапке.", "Ачык/караңгы вариант — үстүндөгү «Тема» которгучу.",
                "Light/dark variant — the “Theme” switch in the header.", "Açık/koyu varyant — üstteki «Tema» anahtarı.", "Yorug'/qorong'i variant — tepadagi «Mavzu» almashtirgichi."),
        ];

        MarketplaceBenefit[]? benefits = null;
        string[]? audience = null;
        switch (themeId)
        {
            case "classic":
                benefits =
                [
                    B("palette-outline", T("Родной вид", "Жердик көрүнүш", "The native look", "Yerleşik görünüm", "Asl ko'rinish"),
                        T("С этой темой касса ставится", "Касса ушул тема менен орнотулат", "The till is installed with this theme", "Kasa bu temayla kurulur", "Kassa shu mavzu bilan o'rnatiladi")),
                    B("eye-outline", T("Жёлтый и синий", "Сары жана көк", "Yellow and blue", "Sarı ve mavi", "Sariq va ko'k"),
                        T("Жёлтый акцент в светлом, синий в тёмном", "Ачыкта сары, караңгыда көк акцент", "A yellow accent in light, blue in dark", "Açıkta sarı, koyuda mavi vurgu", "Yorug'da sariq, qorong'ida ko'k urg'u")),
                ];
                audience = [T("Всем по умолчанию", "Баарына демейки", "Everyone, by default", "Varsayılan olarak herkes için", "Standart bo'yicha hamma uchun")];
                break;
            case "nurmarket":
                benefits =
                [
                    B("palette-outline", T("Фирменный стиль NUR CRM", "NUR CRM фирмалык стили", "NUR CRM brand style", "NUR CRM kurumsal stili", "NUR CRM firma uslubi"),
                        T("Golden Light и Deep Night по брендбуку", "Брендбук боюнча Golden Light жана Deep Night", "Golden Light and Deep Night per the brand book", "Marka kılavuzuna göre Golden Light ve Deep Night", "Brendbuk bo'yicha Golden Light va Deep Night")),
                    B("format-text", T("Montserrat и мягкие углы", "Montserrat жана жумшак бурчтар", "Montserrat and soft corners", "Montserrat ve yumuşak köşeler", "Montserrat va yumshoq burchaklar"),
                        T("Шрифт Montserrat, крупные скругления", "Montserrat шрифти, чоң тегеректөөлөр", "The Montserrat font, large rounding", "Montserrat yazı tipi, büyük yuvarlatmalar", "Montserrat shrifti, katta yumaloqlash")),
                ];
                audience = [T("Кто хочет вид как у сайта NUR CRM", "NUR CRM сайтындай көрүнүш каалагандар", "Those who want the look of the NUR CRM site", "NUR CRM sitesi gibi görünüm isteyenler", "NUR CRM saytidagidek ko'rinish istaganlar")];
                break;
            case "graphite":
                benefits =
                [
                    B("eye-outline", T("Ничего не отвлекает", "Эч нерсе алаксытпайт", "Nothing distracts", "Hiçbir şey dikkat dağıtmaz", "Hech narsa chalg'itmaydi"),
                        T("Строгий монохром без цветных пятен", "Түстүү тактары жок катаал монохром", "A strict monochrome without colored spots", "Renk lekesi olmayan sade monokrom", "Rangli dog'larsiz qat'iy monoxrom")),
                    B("cash", T("Цена читается сразу", "Баа дароо окулат", "The price reads at once", "Fiyat hemen okunur", "Narx darhol o'qiladi"),
                        T("Чёрная цена на серых поверхностях", "Боз беттерде кара баа", "A black price on gray surfaces", "Gri yüzeylerde siyah fiyat", "Kulrang sirtlarda qora narx")),
                ];
                audience = [T("Офисный стиль", "Кеңсе стили", "An office style", "Ofis stili", "Ofis uslubi")];
                break;
            case "sunset":
                benefits =
                [
                    B("palette-outline", T("Тёплая гамма", "Жылуу гамма", "Warm palette", "Sıcak renk gamı", "Iliq gamma"),
                        T("Песочный фон, терракотовый акцент", "Кум түстүү фон, терракота акцент", "A sand background, a terracotta accent", "Kum rengi arka plan, terakota vurgu", "Qum rangli fon, terrakota urg'u")),
                    B("view-grid-outline", T("Самые круглые карточки", "Эң тегерек карточкалар", "The roundest cards", "En yuvarlak kartlar", "Eng yumaloq kartochkalar"),
                        T("Мягкий, неофициальный вид", "Жумшак, расмий эмес көрүнүш", "A soft, informal look", "Yumuşak, resmî olmayan görünüm", "Yumshoq, norasmiy ko'rinish")),
                ];
                audience = [T("Кофейни, цветочные", "Кофейнялар, гүл дүкөндөрү", "Coffee shops, flower shops", "Kafeler, çiçekçiler", "Qahvaxonalar, gul do'konlari")];
                break;
            case "forest":
                benefits =
                [
                    B("palette-outline", T("Спокойный зелёный", "Тынч жашыл", "Calm green", "Sakin yeşil", "Sokin yashil"),
                        T("Мятный фон, тёмно-зелёная цена", "Жалбыз түстүү фон, кара жашыл баа", "A mint background, a dark green price", "Nane rengi arka plan, koyu yeşil fiyat", "Yalpiz rangli fon, to'q yashil narx")),
                ];
                audience = [T("Эко-, фермерские магазины", "Эко-, фермердик дүкөндөр", "Eco and farm shops", "Eko ve çiftlik mağazaları", "Eko va fermer do'konlari")];
                break;
            case "contrast":
                benefits =
                [
                    B("flash-outline", T("Для яркого света", "Жарык жер үчүн", "For bright light", "Parlak ışık için", "Yorqin yorug'lik uchun"),
                        T("Только чёрное, белое и жёлтое", "Кара, ак жана сары гана", "Only black, white and yellow", "Yalnızca siyah, beyaz ve sarı", "Faqat qora, oq va sariq")),
                    B("eye-outline", T("Для слабого зрения", "Начар көрүү үчүн", "For poor eyesight", "Zayıf görme için", "Ko'rishi zaiflar uchun"),
                        T("Увеличенный шрифт, чёрные рамки у всех карточек", "Чоң шрифт, бардык карточкаларда кара чек", "An enlarged font, black borders on all cards", "Büyütülmüş yazı tipi, tüm kartlarda siyah çerçeve", "Kattalashtirilgan shrift, barcha kartochkalarda qora ramka")),
                ];
                audience = [T("Солнечная витрина, возрастные кассиры", "Күн тийген жер, улгайган кассирлер", "A sunny shop window, older cashiers", "Güneş alan vitrin, yaşlı kasiyerler", "Quyosh tushadigan vitrina, keksa kassirlar")];
                break;
            case "terminal":
                benefits =
                [
                    B("format-text", T("Цифры ровной сеткой", "Сандар түз тор менен", "Digits in an even grid", "Rakamlar düzgün ızgarada", "Raqamlar tekis to'rda"),
                        T("Моноширинный шрифт", "Моноширина шрифт", "A monospaced font", "Eş aralıklı yazı tipi", "Monoeni shrift")),
                    B("console-line", T("Ретро-терминал", "Ретро-терминал", "Retro terminal", "Retro terminal", "Retro terminal"),
                        T("Зелёный по тёмному", "Караңгыда жашыл", "Green on dark", "Koyu üzerinde yeşil", "Qorong'ida yashil")),
                ];
                audience = [T("Любители классических касс", "Классикалык кассаларды жакшы көргөндөр", "Fans of classic tills", "Klasik kasa severler", "Klassik kassalar ixlosmandlari")];
                break;
            case "touch":
                benefits =
                [
                    B("gesture-tap", T("Трудно промахнуться", "Жаңылуу кыйын", "Hard to miss", "Iskalamak zor", "Adashish qiyin"),
                        T("Плитки вдвое крупнее, кнопки и поля 52 точки", "Плиткалар эки эсе чоң, баскычтар жана талаалар 52 чекит", "Tiles twice as large, buttons and fields 52 points", "Karolar iki kat büyük, düğmeler ve alanlar 52 nokta", "Plitkalar ikki baravar katta, tugma va maydonlar 52 nuqta")),
                    B("format-text", T("Крупная цена и название", "Чоң баа жана аталыш", "Large price and name", "Büyük fiyat ve ad", "Katta narx va nom"),
                        T("Цена и название товара заметны с первого взгляда.", "Товардын баасы жана аталышы бир караганда эле көрүнөт.", "The price and name of a product stand out at first glance.", "Ürünün fiyatı ve adı ilk bakışta fark edilir.", "Mahsulotning narxi va nomi birinchi qarashdayoq ko'zga tashlanadi.")),
                ];
                audience = [T("Сенсорные моноблоки", "Сенсордук моноблоктор", "Touch-screen all-in-ones", "Dokunmatik hepsi bir arada bilgisayarlar", "Sensorli monobloklar")];
                break;
            case "compact":
                benefits =
                [
                    B("view-grid-outline", T("Вдвое больше товаров на экране", "Экранда эки эсе көп товар", "Twice as many products on screen", "Ekranda iki kat fazla ürün", "Ekranda ikki baravar ko'p mahsulot"),
                        T("Мелкая сетка и низкие элементы", "Майда тор, жапыз элементтер", "A fine grid and low elements", "İnce ızgara ve alçak öğeler", "Mayda to'r va past elementlar")),
                ];
                audience = [T("Большой ассортимент, поиск глазами", "Чоң ассортимент, көз менен издөө", "A large range, searching by eye", "Geniş ürün yelpazesi, gözle arama", "Assortimenti katta, ko'z bilan qidirish")];
                break;
            case "showcase":
                benefits =
                [
                    B("image-outline", T("Касса как витрина", "Касса витрина сыяктуу", "The till as a shop window", "Vitrin gibi kasa", "Vitrinadek kassa"),
                        T("Залитая фиолетовым шапка, крупное фото товара", "Кызгылт көк баш, товардын чоң сүрөтү", "A header filled with violet, a large product photo", "Mor dolgulu başlık, büyük ürün fotoğrafı", "Binafsha to'ldirilgan sarlavha, mahsulotning katta rasmi")),
                ];
                audience = [T("Бутики, магазины с фото товаров", "Бутиктер, товар сүрөттөрү бар дүкөндөр", "Boutiques, shops with product photos", "Butikler, ürün fotoğraflı mağazalar", "Butiklar, mahsulot rasmlari bor do'konlar")];
                break;
            case "kyrgyz":
                benefits =
                [
                    B("palette-outline", T("Национальный стиль", "Улуттук стиль", "National style", "Ulusal stil", "Milliy uslub"),
                        T("Красный и золото флага на фоне шырдака", "Шырдак фонунда желектин кызыл жана алтын түсү", "The red and gold of the flag on a shyrdak background", "Şırdak zemini üzerinde bayrağın kırmızısı ve altını", "Shirdak fonida bayroqning qizil va oltin ranglari")),
                    B("view-grid-outline", T("Орнамент „кочкор мүйүз“", "„Кочкор мүйүз“ орнаменти", "The “ram's horn” ornament", "«Koçkor müyüz» ornamenti", "«Qo'chqor shoxi» ornamenti"),
                        T("В окнах и под шапкой", "Терезелерде жана баш жагында", "In windows and under the header", "Pencerelerde ve başlığın altında", "Oynalarda va sarlavha ostida")),
                    B("eye-outline", T("Тёмный вариант", "Караңгы вариант", "Dark variant", "Koyu varyant", "Qorong'i variant"),
                        T("Золото на бордовом", "Кызыл күрөңдө алтын", "Gold on burgundy", "Bordo üzerinde altın", "Bordoda oltin")),
                ];
                audience = [T("Праздники, национальные магазины", "Майрамдар, улуттук дүкөндөр", "Holidays, national-style shops", "Bayramlar, ulusal mağazalar", "Bayramlar, milliy do'konlar")];
                break;
        }

        if (benefits is null)
        {
            // Своя тема (CustomThemeStore): снимков нет, предпросмотр рисуется кодом (MarketplaceLookPreview.CustomThemeHero).
            i.Tagline = T("Своя тема", "Өз темаңыз", "Your own theme", "Kendi temanız", "O'z mavzuingiz");
            return;
        }

        i.Tagline = i.ShortDescription;
        i.Benefits = benefits;
        i.Audience = audience ?? [];
        i.Slides =
        [
            S(1, LightVariant()),
            S(2, DarkVariant()),
        ];
    }

    // ------------------------------------------------------------------ 5.18 Редактор тем
    private static void ThemeEditor(MarketplaceItem i)
    {
        i.IconKey = "palette-swatch-outline";
        i.Tagline = T("Своя тема в цветах вашего магазина", "Дүкөнүңүздүн түстөрүндөгү өз тема",
            "Your own theme in your store's colors", "Mağazanızın renklerinde kendi temanız", "Do'koningiz ranglarida o'z mavzuingiz");
        i.KeyFacts =
        [
            T("9 цветов × 2 варианта", "9 түс × 2 вариант", "9 colors × 2 variants", "9 renk × 2 varyant", "9 rang × 2 variant"),
            T("Файл .nmtheme.json", ".nmtheme.json файлы", ".nmtheme.json file", ".nmtheme.json dosyası", ".nmtheme.json fayli"),
            T("Видно сразу", "Дароо көрүнөт", "Visible at once", "Anında görünür", "Darhol ko'rinadi"),
        ];
        i.Benefits =
        [
            B("store-outline", T("Под фирменный стиль магазина", "Дүкөндүн фирмалык стилине", "Match your brand", "Markanıza uygun", "Brendingizga mos"),
                T("Акцент, фон, панели, текст и цвета успеха/предупреждения/ошибки.",
                  "Акцент, фон, панелдер, текст жана ийгилик/эскертүү/ката түстөрү.",
                  "Accent, background, panels, text and success/warning/error colors.",
                  "Vurgu, arka plan, paneller, metin ve başarı/uyarı/hata renkleri.",
                  "Urg'u, fon, panellar, matn va muvaffaqiyat/ogohlantirish/xato ranglari.")),
            B("eye-outline", T("Видно сразу", "Дароо көрүнөт", "See it live", "Anında görün", "Darhol ko'ring"),
                T("Пока редактор открыт, тема применена ко всей программе; закрыли без «Применить» — вернётся прежняя.",
                  "Редактор ачык турганда тема бүт программага колдонулат; «Колдонуу»сыз жапсаңыз — мурункусу кайтат.",
                  "While the editor is open the theme is applied to the whole program; if you close it without “Apply”, the previous one returns.",
                  "Düzenleyici açıkken tema tüm programa uygulanır; «Uygula» demeden kapatırsanız eskisi geri gelir.",
                  "Muharrir ochiq turganda mavzu butun dasturga qo'llanadi; «Qo'llash»siz yopsangiz — avvalgisi qaytadi.")),
            B("file-export-outline", T("Перенос на другую кассу", "Башка кассага которуу", "Move to another till", "Başka kasaya taşıyın", "Boshqa kassaga ko'chiring"),
                T("Экспорт и импорт файлом .nmtheme.json.",
                  ".nmtheme.json файлы менен экспорт жана импорт.",
                  "Export and import with a .nmtheme.json file.",
                  ".nmtheme.json dosyasıyla dışa ve içe aktarma.",
                  ".nmtheme.json fayli bilan eksport va import.")),
        ];
        i.HowTo =
        [
            T("«Открыть редактор тем».", "«Темалар редакторун ачуу».", "Press “Open the theme editor”.", "«Tema düzenleyiciyi aç».", "«Mavzu muharririni ochish»."),
            T("Выберите основу, поменяйте цвета, скругление и размер шрифта.", "Негизди тандап, түстөрдү, тегеректикти жана шрифттин өлчөмүн өзгөртүңүз.",
                "Choose a base, change the colors, rounding and font size.", "Bir temel seçin; renkleri, yuvarlaklığı ve yazı boyutunu değiştirin.", "Asosni tanlang, ranglarni, yumaloqlikni va shrift o'lchamini o'zgartiring."),
            T("«Применить»; своя тема появится среди тем с пометкой «Своя».", "«Колдонуу»; өз темаңыз темалардын арасында «Өзүңүздүкү» белгиси менен чыгат.",
                "“Apply”; your theme will appear among the themes marked “Custom”.", "«Uygula»; kendi temanız temalar arasında «Özel» etiketiyle görünür.", "«Qo'llash»; o'z mavzuingiz mavzular orasida «O'zingizniki» belgisi bilan paydo bo'ladi."),
        ];
        i.Audience =
        [
            T("Сети и франшизы", "Тармактар жана франшизалар", "Chains and franchises", "Zincirler ve franchise'lar", "Tarmoqlar va franshizalar"),
            T("Магазины со своим фирменным цветом", "Өз фирмалык түсү бар дүкөндөр", "Shops with their own brand color", "Kendi kurumsal rengi olan mağazalar", "O'z firma rangi bor do'konlar"),
        ];
        i.Slides =
        [
            S(1, T("Тема на основе любой встроенной", "Каалаган орнотулган темага негизделген тема", "A theme based on any built-in one", "Herhangi bir yerleşik temaya dayalı tema", "Istalgan o'rnatilgan mavzu asosidagi mavzu")),
            S(2, T("9 цветов для светлого и тёмного", "Ачык жана караңгы үчүн 9 түс", "9 colors for light and dark", "Açık ve koyu için 9 renk", "Yorug' va qorong'i uchun 9 rang")),
            S(3, T("Скругление и размер шрифта", "Тегеректик жана шрифттин өлчөмү", "Rounding and font size", "Yuvarlaklık ve yazı boyutu", "Yumaloqlik va shrift o'lchami")),
            S(4, T("Перенос темы файлом", "Теманы файл менен которуу", "Moving a theme as a file", "Temayı dosyayla taşıma", "Mavzuni fayl bilan ko'chirish")),
        ];
        i.Demo = Demo(T("Своя тема за минуту", "Өз темаңыз бир мүнөттө", "Your own theme in a minute", "Bir dakikada kendi temanız", "Bir daqiqada o'z mavzuingiz"));
    }
}
