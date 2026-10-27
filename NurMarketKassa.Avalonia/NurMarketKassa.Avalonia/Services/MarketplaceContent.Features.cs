using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-1, раздел 5.1–5.13): контент функций. Факты проверены по коду (ссылки — в ТЗ);
// «Не обещать» из ТЗ соблюдено: ничего сверх того, что функция делает сейчас. Слайды объявлены всегда — витрина покажет только те,
// у которых есть файлы в Assets/marketplace/<id>/ (кладут пакеты медиа П-6…П-9).
internal static partial class MarketplaceContent
{
    // 2026-10-27: описание «5.1 Голосовое управление» убрано вместе с функцией (есть ИИ-ассистент).

    // ------------------------------------------------------------------ 5.2 Программа лояльности
    private static void Loyalty(MarketplaceItem i)
    {
        i.IconKey = "gift-outline";
        i.Tagline = T("Баллы за покупки — повод вернуться к вам", "Сатып алуу үчүн упайлар — сизге кайра келүүгө себеп",
            "Points for purchases — a reason to come back", "Alışverişe puan — geri gelmek için bir neden", "Xarid uchun ballar — qaytib kelishga sabab");
        i.KeyFacts =
        [
            T("5 % по умолчанию", "Демейки 5 %", "5% by default", "Varsayılan %5", "Standart 5 %"),
            T("1 балл = 1 сом", "1 упай = 1 сом", "1 point = 1 som", "1 puan = 1 som", "1 ball = 1 so'm"),
            T("Без тумблера", "Которгучсуз", "No switch", "Anahtarsız", "Almashtirgichsiz"),
        ];
        i.Benefits =
        [
            B("account-heart-outline", T("Повод вернуться", "Кайра келүүгө себеп", "A reason to return", "Geri dönmek için neden", "Qaytishga sabab"),
                T("Покупатель видит накопленные баллы и возвращается, чтобы их потратить.",
                  "Сатып алуучу топтогон упайларын көрүп, аларды жумшаганы кайра келет.",
                  "Customers see their points and come back to spend them.",
                  "Müşteri biriken puanını görür ve harcamak için geri gelir.",
                  "Xaridor to'plagan ballarini ko'rib, ularni sarflash uchun qaytib keladi.")),
            B("percent", T("Начисляется само", "Өзү эсептелет", "Earned automatically", "Otomatik kazanılır", "O'zi hisoblanadi"),
                T("По умолчанию 5 % от суммы к оплате после скидок; процент меняется в настройках кассы или берётся из NurCRM.",
                  "Демейки боюнча арзандатуудан кийинки төлөм суммасынын 5 %; пайызды кассанын жөндөөлөрүндө өзгөртүүгө болот же NurCRM'ден алынат.",
                  "By default 5% of the amount due after discounts; the percentage is changed in the till settings or taken from NurCRM.",
                  "Varsayılan olarak indirimlerden sonraki ödenecek tutarın %5'i; yüzde kasa ayarlarında değiştirilir veya NurCRM'den alınır.",
                  "Standart bo'yicha chegirmalardan keyingi to'lov summasining 5 %; foiz kassa sozlamalarida o'zgartiriladi yoki NurCRM'dan olinadi.")),
            B("qrcode-scan", T("Клиент за секунду", "Кардар бир секундда", "Customer in a second", "Saniyede müşteri", "Mijoz bir soniyada"),
                T("Кнопка „Выбрать клиента“ в окне оплаты или скан QR-кода клиента.",
                  "Төлөм терезесиндеги „Кардарды тандоо“ баскычы же кардардын QR-кодун скандоо.",
                  "The “Select customer” button in the payment window or scanning the customer's QR code.",
                  "Ödeme penceresinde «Müşteri seç» düğmesi veya müşterinin QR kodunu okutma.",
                  "To'lov oynasidagi «Mijozni tanlash» tugmasi yoki mijozning QR-kodini skanerlash.")),
            B("shield-check-outline", T("Без ошибок кассира", "Кассир жаңылбайт", "No cashier mistakes", "Kasiyer hatası yok", "Kassir xatosiz"),
                T("Нельзя списать больше баланса или больше суммы чека; при возврате начисленные баллы откатываются.",
                  "Баланстан же чектин суммасынан көп чегерүүгө болбойт; кайтарууда эсептелген упайлар кайтарылат.",
                  "You cannot redeem more than the balance or more than the receipt total; on a return the earned points are rolled back.",
                  "Bakiyeden veya fiş tutarından fazlası harcanamaz; iadede kazanılan puanlar geri alınır.",
                  "Balansdan yoki chek summasidan ko'p ayirib bo'lmaydi; qaytarishda hisoblangan ballar qaytariladi.")),
            B("wifi-off", T("При плохой связи", "Байланыш начар болсо", "Weak connection is fine", "Zayıf bağlantıda da", "Aloqa yomon bo'lsa ham"),
                T("Без интернета касса показывает последний известный баланс, а операции отправит в NurCRM, когда связь появится.",
                  "Интернет жок болсо касса акыркы белгилүү балансты көрсөтөт, операцияларды байланыш пайда болгондо NurCRM'ге жөнөтөт.",
                  "Without internet the till shows the last known balance and sends the operations to NurCRM when the connection returns.",
                  "İnternet yokken kasa bilinen son bakiyeyi gösterir, işlemleri bağlantı gelince NurCRM'e gönderir.",
                  "Internet bo'lmasa kassa oxirgi ma'lum balansni ko'rsatadi, operatsiyalarni aloqa paydo bo'lganda NurCRM'ga yuboradi.")),
        ];
        var p = PriceInBrackets(i);
        i.HowTo =
        [
            T($"Если функция ещё не активирована — активируйте серийным ключом{p}: программа включится сразу.", $"Функция али активдештирилбесе — сериялык ачкыч менен активдештириңиз{p}: программа дароо күйөт.",
                $"If the function is not activated yet, activate it with a serial key{p} — the program turns it on at once.", $"İşlev henüz etkinleştirilmediyse seri anahtarla etkinleştirin{p} — program hemen açılır.",
                $"Funksiya hali faollashtirilmagan bo'lsa, seriya kaliti bilan faollashtiring{p} — dastur darhol yoqiladi."),
            T("Если нужно, поменяйте процент: настройки кассы → „Операции“.", "Керек болсо пайызды өзгөртүңүз: кассанын жөндөөлөрү → „Операциялар“.",
                "If needed, change the percentage: till settings → “Operations”.", "Gerekirse yüzdeyi değiştirin: kasa ayarları → «İşlemler».",
                "Kerak bo'lsa foizni o'zgartiring: kassa sozlamalari → «Operatsiyalar»."),
            T("В окне оплаты нажмите „Выбрать клиента“ или отсканируйте его QR.", "Төлөм терезесинде „Кардарды тандоо“ баскычын басыңыз же анын QR-кодун скандаңыз.",
                "In the payment window press “Select customer” or scan the customer's QR.", "Ödeme penceresinde «Müşteri seç»e basın veya müşterinin QR kodunu okutun.",
                "To'lov oynasida «Mijozni tanlash»ni bosing yoki uning QR-kodini skanerlang."),
            T("Введите, сколько баллов списать, и примите оплату.", "Канча упай чегерүүнү жазып, төлөмдү кабыл алыңыз.",
                "Enter how many points to redeem and accept the payment.", "Kaç puan harcanacağını girin ve ödemeyi alın.",
                "Necha ball ayirishni kiriting va to'lovni qabul qiling."),
        ];
        i.Audience =
        [
            T("Магазины с постоянными покупателями", "Туруктуу сатып алуучулары бар дүкөндөр", "Shops with regular customers", "Düzenli müşterisi olan mağazalar", "Doimiy xaridorlari bor do'konlar"),
            T("Магазины у дома", "Үй жанындагы дүкөндөр", "Neighborhood shops", "Mahalle mağazaları", "Uy yonidagi do'konlar"),
            T("Владельцы, которые ведут базу клиентов", "Кардарлар базасын жүргүзгөн ээлер", "Owners who keep a customer base", "Müşteri tabanı tutan sahipler", "Mijozlar bazasini yuritadigan egalar"),
        ];
        i.PriceTerms = OnceTerms();
        i.Slides =
        [
            S(1, T("Баланс клиента — прямо в окне оплаты", "Кардардын балансы — төлөм терезесинде", "The customer's balance — right in the payment window", "Müşteri bakiyesi — doğrudan ödeme penceresinde", "Mijoz balansi — to'g'ridan-to'g'ri to'lov oynasida")),
            S(2, T("Баллы оплачивают часть чека", "Упайлар чектин бир бөлүгүн төлөйт", "Points pay part of the receipt", "Puanlar fişin bir kısmını öder", "Ballar chekning bir qismini to'laydi")),
            S(3, T("Процент начисления меняется в настройках", "Эсептөө пайызы жөндөөлөрдө өзгөрөт", "The earn percentage is changed in settings", "Kazanım yüzdesi ayarlardan değiştirilir", "Hisoblash foizi sozlamalarda o'zgaradi")),
            S(4, T("Баллы каждого клиента в списке клиентов", "Ар бир кардардын упайлары кардарлар тизмесинде", "Each customer's points in the customer list", "Her müşterinin puanı müşteri listesinde", "Har bir mijozning ballari mijozlar ro'yxatida")),
        ];
        i.Demo = Demo(T("Списание баллов при оплате", "Төлөмдө упайларды чегерүү", "Redeeming points at payment", "Ödemede puan harcama", "To'lovda ballarni ayirish"));
    }

    // ------------------------------------------------------------------ 5.3 Учёт сотрудников
    private static void Timesheet(MarketplaceItem i)
    {
        i.IconKey = "account-clock-outline";
        i.Tagline = T("Кто сколько работал и сколько продал", "Ким канча иштеди жана канча сатты", "Who worked how long and sold how much", "Kim ne kadar çalıştı, ne kadar sattı", "Kim qancha ishladi va qancha sotdi");
        i.KeyFacts =
        [
            T("7 колонок", "7 мамыча", "7 columns", "7 sütun", "7 ustun"),
            T("Любой период", "Каалаган мезгил", "Any period", "Herhangi bir dönem", "Istalgan davr"),
            T("Без ручного ввода", "Колго киргизбей", "No manual entry", "Elle giriş yok", "Qo'lda kiritmasdan"),
        ];
        i.Benefits =
        [
            B("clock-outline", T("Часы без тетрадки", "Дептерсиз сааттар", "Hours without a notebook", "Defter olmadan saatler", "Daftarsiz soatlar"),
                T("Часы считаются по открытию и закрытию смен — записывать ничего не нужно.",
                  "Сааттар сменанын ачылышы жана жабылышы боюнча эсептелет — эч нерсе жазуунун кереги жок.",
                  "Hours are counted from shift opening and closing — nothing to write down.",
                  "Saatler vardiyanın açılış ve kapanışından hesaplanır — hiçbir şey yazmanız gerekmez.",
                  "Soatlar smenaning ochilishi va yopilishidan hisoblanadi — hech narsa yozish shart emas.")),
            B("cash", T("Выручка каждого кассира", "Ар бир кассирдин түшүмү", "Revenue per cashier", "Kasiyer başına ciro", "Har bir kassir tushumi"),
                T("Колонка „Выручка“ рядом с часами — кассиров легко сравнить.",
                  "„Түшүм“ мамычасы сааттардын жанында — кассирлерди салыштыруу оңой.",
                  "A “Revenue” column next to the hours — cashiers are easy to compare.",
                  "Saatlerin yanında «Ciro» sütunu — kasiyerleri karşılaştırmak kolay.",
                  "Soatlar yonida «Tushum» ustuni — kassirlarni solishtirish oson.")),
            B("calendar-range", T("Любой период", "Каалаган мезгил", "Any period", "Herhangi bir dönem", "Istalgan davr"),
                T("Календари „с — по“ и кнопка „Весь период“; таблица обновляется сразу.",
                  "„-дан — -га чейин“ календарлары жана „Бүткүл мезгил“ баскычы; таблица дароо жаңыланат.",
                  "“From — to” calendars and an “All time” button; the table updates at once.",
                  "«Başlangıç — bitiş» takvimleri ve «Tüm dönem» düğmesi; tablo hemen güncellenir.",
                  "«dan — gacha» kalendarlari va «Butun davr» tugmasi; jadval darhol yangilanadi.")),
            B("sort-descending", T("Самые занятые сверху", "Эң көп иштегендер өйдөдө", "Busiest first", "En çok çalışan üstte", "Eng ko'p ishlaganlar yuqorida"),
                T("Список отсортирован по часам; видно среднюю смену.",
                  "Тизме сааттар боюнча иреттелген; орточо смена көрүнөт.",
                  "The list is sorted by hours; the average shift is visible.",
                  "Liste saatlere göre sıralanır; ortalama vardiya görünür.",
                  "Ro'yxat soatlar bo'yicha tartiblangan; o'rtacha smena ko'rinadi.")),
        ];
        i.HowTo =
        [
            ActivateStep(i),
            T("Нажмите „Открыть табель“.", "„Табелди ачуу“ баскычын басыңыз.", "Press “Open timesheet”.", "«Çizelgeyi aç»a basın.", "«Tabelni ochish» tugmasini bosing."),
            T("Выберите период — таблица обновится сама.", "Мезгилди тандаңыз — таблица өзү жаңыланат.", "Choose a period — the table updates by itself.", "Dönemi seçin — tablo kendiliğinden güncellenir.", "Davrni tanlang — jadval o'zi yangilanadi."),
        ];
        i.Audience =
        [
            T("Владелец магазина", "Дүкөндүн ээси", "Shop owner", "Mağaza sahibi", "Do'kon egasi"),
            T("Магазины с 2+ кассирами", "2+ кассири бар дүкөндөр", "Shops with 2+ cashiers", "2+ kasiyeri olan mağazalar", "2+ kassiri bor do'konlar"),
            T("Расчёт смен вручную", "Сменаларды колго эсептегендер", "Counting shifts by hand", "Vardiyaları elle hesaplayanlar", "Smenalarni qo'lda hisoblaydiganlar"),
        ];
        i.PriceTerms = OnceTerms();
        i.Slides =
        [
            S(1, T("Смены, часы и выручка каждого кассира", "Ар бир кассирдин сменалары, сааттары жана түшүмү", "Shifts, hours and revenue of every cashier", "Her kasiyerin vardiyaları, saatleri ve cirosu", "Har bir kassirning smenalari, soatlari va tushumi")),
            S(2, T("Выберите любой период", "Каалаган мезгилди тандаңыз", "Choose any period", "Herhangi bir dönemi seçin", "Istalgan davrni tanlang")),
            S(3, T("Таблица обновляется сразу", "Таблица дароо жаңыланат", "The table updates at once", "Tablo hemen güncellenir", "Jadval darhol yangilanadi")),
            S(4, T("Весь период — одной кнопкой", "Бүткүл мезгил — бир баскыч менен", "All time — with one button", "Tüm dönem — tek düğmeyle", "Butun davr — bitta tugma bilan")),
        ];
        i.Demo = Demo(T("Табель за любой период", "Каалаган мезгилдин табели", "A timesheet for any period", "Herhangi bir dönemin çizelgesi", "Istalgan davr tabeli"));
    }

    // ------------------------------------------------------------------ 5.4 Весы (только «Старт»)
    private static void Scales(MarketplaceItem i)
    {
        i.IconKey = "scale";
        i.Tagline = T("Цены с кассы — сразу на весы с этикетками", "Кассадагы баалар — дароо этикеткалуу таразага",
            "Till prices straight to label scales", "Kasa fiyatları doğrudan etiketli teraziye", "Kassa narxlari — to'g'ridan-to'g'ri yorliqli taroziga");
        i.KeyFacts =
        [
            T("4 марки весов", "Таразанын 4 маркасы", "4 scale brands", "4 terazi markası", "4 ta tarozi markasi"),
            T("PLU из каталога", "PLU каталогдон", "PLU from the catalog", "Katalogdan PLU", "Katalogdan PLU"),
            T("Инструкция с анимацией", "Анимациялуу нускама", "Animated guide", "Animasyonlu kılavuz", "Animatsiyali yo'riqnoma"),
        ];
        i.Benefits =
        [
            B("database-arrow-right-outline", T("Без ручного ввода в весы", "Таразага колго киргизбей", "No manual entry", "Elle giriş yok", "Qo'lda kiritmasdan"),
                T("Названия и цены берутся из ваших товаров — не нужно вбивать их в весы.",
                  "Аттары жана баалары товарларыңыздан алынат — таразага терип отуруунун кереги жок.",
                  "Names and prices come from your products — no need to type them into the scales.",
                  "Adlar ve fiyatlar ürünlerinizden alınır — teraziye elle girmenize gerek yok.",
                  "Nomlar va narxlar mahsulotlaringizdan olinadi — tarozige terib o'tirish shart emas.")),
            B("scale", T("Несколько весов", "Бир нече тараза", "Several scales", "Birden çok terazi", "Bir nechta tarozi"),
                T("У каждых весов свои кнопки „Настроить…“ и „Отправить товары“.",
                  "Ар бир таразанын өз „Жөндөө…“ жана „Товарларды жөнөтүү“ баскычтары бар.",
                  "Each scale has its own “Configure…” and “Send goods” buttons.",
                  "Her terazinin kendi «Ayarla…» ve «Ürünleri gönder» düğmeleri vardır.",
                  "Har bir tarozining o'z «Sozlash…» va «Tovarlarni yuborish» tugmalari bor.")),
            B("play-box-outline", T("Подключение по шагам", "Кадам менен туташтыруу", "Step-by-step setup", "Adım adım bağlantı", "Bosqichma-bosqich ulash"),
                T("„Инструкция подключения“ показывает с анимацией, как подключить весы вашей марки.",
                  "„Туташтыруу нускамасы“ таразаңызды кантип туташтырууну анимация менен көрсөтөт.",
                  "The “Connection guide” shows with an animation how to connect your brand of scale.",
                  "«Bağlantı kılavuzu» terazinizin markasının animasyonla nasıl bağlanacağını gösterir.",
                  "«Ulanish yo'riqnomasi» tarozingiz markasini animatsiya bilan qanday ulashni ko'rsatadi.")),
            B("lan", T("Штрих — по сети или через NurCRM", "Штрих — тармак же NurCRM аркылуу", "Shtrikh: network or NurCRM", "Shtrikh: ağ veya NurCRM", "Shtrix: tarmoq yoki NurCRM"),
                T("Товары уходят на весы Штрих напрямую по сети или через сервер NurCRM.",
                  "Товарлар Штрих таразасына түз тармак аркылуу же NurCRM сервери аркылуу кетет.",
                  "Products go to Shtrikh scales directly over the network or through the NurCRM server.",
                  "Ürünler Shtrikh terazilerine doğrudan ağ üzerinden veya NurCRM sunucusu aracılığıyla gider.",
                  "Mahsulotlar Shtrix tarozilariga to'g'ridan-to'g'ri tarmoq orqali yoki NurCRM serveri orqali ketadi.")),
        ];
        var fee = TariffGate.PackActivationFee;
        var month = TariffGate.PackMonthlyFee;
        i.HowTo =
        [
            T($"Подключите ({fee} сом + {month} сом/мес; на „Стандарте“ входит в тариф).", $"Туташтырыңыз ({fee} сом + айына {month} сом; „Стандартта“ тарифке кирет).",
                $"Connect it ({fee} som + {month} som/mo; included in the “Standard” plan).", $"Bağlayın ({fee} som + aylık {month} som; «Standart»'ta tarifeye dahildir).",
                $"Ulang ({fee} so'm + oyiga {month} so'm; «Standart»da tarifga kiradi)."),
            T("Настройки → Весы → „Весы с этикетками“ → „+ Добавить весы“.", "Жөндөөлөр → Тараза → „Этикеткалуу тараза“ → „+ Тараза кошуу“.",
                "Settings → Scales → “Label scales” → “+ Add a scale”.", "Ayarlar → Tartılar → «Etiketli tartı» → «+ Tartı ekle».",
                "Sozlamalar → Tarozilar → «Yorliqli tarozi» → «+ Tarozi qo'shish»."),
            T("„Отправить товары“ → отметьте товары → „Отправить на весы“.", "„Товарларды жөнөтүү“ → товарларды белгилеңиз → „Таразага жиберүү“.",
                "“Send goods” → tick the products → “Send to the scale”.", "«Ürünleri gönder» → ürünleri işaretleyin → «Tartıya gönder».",
                "«Tovarlarni yuborish» → mahsulotlarni belgilang → «Taroziga yuborish»."),
        ];
        i.Audience =
        [
            T("Мясо, овощи, сыр на развес", "Эт, жашылча, сыр салмагы менен", "Meat, vegetables and cheese by weight", "Gramajla et, sebze, peynir", "Vaznli go'sht, sabzavot, pishloq"),
            T("Весы Штрих, Rongta, TM-30F", "Штрих, Rongta, TM-30F таразалары", "Shtrikh, Rongta, TM-30F scales", "Shtrikh, Rongta, TM-30F terazileri", "Shtrix, Rongta, TM-30F tarozilari"),
        ];
        i.PriceTerms = TariffTerms();
        i.Slides =
        [
            S(1, T("Все весы магазина в одном списке", "Дүкөндүн бардык таразалары бир тизмеде", "All the shop's scales in one list", "Mağazanın tüm terazileri tek listede", "Do'konning barcha tarozilari bitta ro'yxatda")),
            S(2, T("Отметьте товары — и на весы", "Товарларды белгилеңиз — жана таразага", "Tick the products — and send to the scale", "Ürünleri işaretleyin — ve teraziye", "Mahsulotlarni belgilang — va taroziga")),
            S(3, T("Инструкция подключения с анимацией", "Анимациялуу туташтыруу нускамасы", "A connection guide with animation", "Animasyonlu bağlantı kılavuzu", "Animatsiyali ulanish yo'riqnomasi")),
            S(4, T("Марка и адрес весов", "Таразанын маркасы жана дареги", "Scale brand and address", "Terazi markası ve adresi", "Tarozi markasi va manzili")),
        ];
        i.Demo = Demo(T("Отправка товаров на весы", "Товарларды таразага жөнөтүү", "Sending products to the scale", "Ürünleri teraziye gönderme", "Mahsulotlarni taroziga yuborish"));
    }

    // ------------------------------------------------------------------ 5.5 Аналитика склада
    private static void Analytics(MarketplaceItem i)
    {
        i.IconKey = "chart-box-outline";
        i.Tagline = T("Сколько денег лежит на складе и где", "Кампада канча акча жатат жана кайда", "How much money sits in stock, and where", "Depoda ne kadar para var ve nerede", "Omborda qancha pul yotibdi va qayerda");
        i.KeyFacts =
        [
            T("5 показателей", "5 көрсөткүч", "5 metrics", "5 gösterge", "5 ko'rsatkich"),
            T("Топ-10", "Топ-10", "Top 10", "İlk 10", "Top-10"),
            T("Без ожидания сервера", "Серверди күтпөй", "No waiting for the server", "Sunucu beklemeden", "Serverni kutmasdan"),
        ];
        i.Benefits =
        [
            B("cash-multiple", T("Деньги в остатке", "Калдыктагы акча", "Money in stock", "Stoktaki para", "Qoldiqdagi pul"),
                T("Две суммы: остаток по закупочной и по продажной цене.",
                  "Эки сумма: калдык сатып алуу баасы жана сатуу баасы боюнча.",
                  "Two totals: stock at the purchase price and at the sale price.",
                  "İki tutar: alış fiyatı ve satış fiyatıyla stok.",
                  "Ikki summa: qoldiq xarid narxi va sotuv narxi bo'yicha.")),
            B("package-variant-remove", T("Что закончилось", "Эмне түгөндү", "What ran out", "Tükenenler", "Nima tugadi"),
                T("Плитки „Нет в наличии“ и „Низкий остаток“ — видно, что пора заказать.",
                  "„Жок“ жана „Аз калды“ плиткалары — эмнеге буйрутма берүү керектиги көрүнөт.",
                  "The “Out of stock” and “Low stock” tiles show what it is time to order.",
                  "«Stokta yok» ve «Düşük stok» kutucukları neyin sipariş edilmesi gerektiğini gösterir.",
                  "«Mavjud emas» va «Qoldiq kam» plitkalari nimani buyurtma qilish vaqti kelganini ko'rsatadi.")),
            B("chart-bar", T("Где заморожены деньги", "Акча кайда тоңуп калды", "Where money is frozen", "Para nerede bağlı", "Pul qayerda muzlagan"),
                T("Топ-10 товаров по стоимости остатка — полосами и таблицей.",
                  "Калдыктын наркы боюнча топ-10 товар — тилкелер жана таблица менен.",
                  "The top 10 products by stock value — as bars and a table.",
                  "Stok değerine göre ilk 10 ürün — çubuklar ve tablo olarak.",
                  "Qoldiq qiymati bo'yicha top-10 mahsulot — chiziqlar va jadval ko'rinishida.")),
            B("shape-outline", T("По категориям", "Категориялар боюнча", "By category", "Kategoriye göre", "Kategoriyalar bo'yicha"),
                T("Сколько остатка в каждой категории, включая „Без категории“.",
                  "Ар бир категорияда канча калдык бар, „Категориясыз“ да кошо.",
                  "How much stock is in each category, including “No category”.",
                  "Her kategoride ne kadar stok olduğu, «Kategorisiz» dahil.",
                  "Har bir kategoriyada qancha qoldiq borligi, «Kategoriyasiz» ham.")),
            B("flash-outline", T("Сразу", "Дароо", "Instant", "Anında", "Darhol"),
                T("Считается по каталогу на этой кассе, без запросов к серверу.",
                  "Ушул кассадагы каталог боюнча эсептелет, серверге суроосуз.",
                  "Calculated from the catalog on this till, with no requests to the server.",
                  "Bu kasadaki katalogdan hesaplanır, sunucuya istek göndermeden.",
                  "Shu kassadagi katalog bo'yicha hisoblanadi, serverga so'rovsiz.")),
        ];
        i.HowTo =
        [
            ActivateStep(i),
            T("Нажмите „Открыть аналитику склада“ (или Склад → вкладка „Аналитика“).", "„Склад аналитикасын ачуу“ баскычын басыңыз (же Кампа → „Аналитика“ өтмөгү).",
                "Press “Open warehouse analytics” (or Warehouse → the “Analytics” tab).", "«Depo analitiğini aç»a basın (veya Depo → «Analiz» sekmesi).",
                "«Ombor analitikasini ochish» tugmasini bosing (yoki Ombor → «Analitika» bo'limi)."),
        ];
        i.Audience =
        [
            T("Владелец", "Ээси", "Owner", "Sahibi", "Egasi"),
            T("Закупщик", "Сатып алуучу (закупщик)", "Buyer", "Satın almacı", "Xaridor (zakupshik)"),
            T("Магазины с большим ассортиментом", "Ассортименти чоң дүкөндөр", "Shops with a large range", "Geniş ürün yelpazeli mağazalar", "Assortimenti katta do'konlar"),
        ];
        i.PriceTerms = OnceTerms();
        i.Slides =
        [
            S(1, T("Пять главных цифр склада", "Кампанын беш негизги саны", "The five key warehouse figures", "Deponun beş ana rakamı", "Ombor uchun beshta asosiy raqam")),
            S(2, T("Топ-10 товаров по стоимости остатка", "Калдыктын наркы боюнча топ-10 товар", "Top 10 products by stock value", "Stok değerine göre ilk 10 ürün", "Qoldiq qiymati bo'yicha top-10 mahsulot")),
            S(3, T("Остаток по категориям", "Категориялар боюнча калдык", "Stock by category", "Kategoriye göre stok", "Kategoriyalar bo'yicha qoldiq")),
            S(4, T("Всё на одной вкладке склада", "Баары кампанын бир өтмөгүндө", "Everything on one warehouse tab", "Hepsi deponun tek sekmesinde", "Hammasi ombor bo'limining bitta sahifasida")),
        ];
        i.Demo = Demo(T("Остатки склада за минуту", "Кампа калдыктары бир мүнөттө", "Warehouse stock in a minute", "Depo stoku bir dakikada", "Ombor qoldiqlari bir daqiqada"));
    }

    // ------------------------------------------------------------------ 5.6 Редактор ценников
    private static void PriceTag(MarketplaceItem i)
    {
        i.IconKey = "tag-text-outline";
        i.Tagline = T("Ценник за минуту — 6 готовых шаблонов", "Баа белгиси бир мүнөттө — 6 даяр шаблон", "A price tag in a minute — 6 templates", "Bir dakikada etiket — 6 hazır şablon", "Bir daqiqada narx yorlig'i — 6 shablon");
        i.KeyFacts =
        [
            T("6 шаблонов", "6 шаблон", "6 templates", "6 şablon", "6 shablon"),
            T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Ko'rinish"),
            T("Термо или A4", "Термо же A4", "Thermal or A4", "Termal veya A4", "Termo yoki A4"),
        ];
        i.Benefits =
        [
            B("view-grid-outline", T("Готовые шаблоны", "Даяр шаблондор", "Ready templates", "Hazır şablonlar", "Tayyor shablonlar"),
                T("Размеры по умолчанию от 40×20 до 60×40 мм — выбрать и печатать.",
                  "Демейки өлчөмдөр 40×20 мм ден 60×40 мм ге чейин — тандап, басып чыгаруу.",
                  "Default sizes from 40×20 to 60×40 mm — pick one and print.",
                  "Varsayılan boyutlar 40×20 ile 60×40 mm arasında — seçin ve yazdırın.",
                  "Standart o'lchamlar 40×20 dan 60×40 mm gacha — tanlang va chop eting.")),
            B("sale", T("Акции видно сразу", "Акция дароо көрүнөт", "Promotions stand out", "Kampanya hemen görünür", "Aksiya darhol ko'rinadi"),
                T("В акционных шаблонах — старая цена и скидка.",
                  "Акциялык шаблондордо — эски баа жана арзандатуу.",
                  "The promo templates show the old price and the discount.",
                  "Promosyon şablonlarında eski fiyat ve indirim görünür.",
                  "Aksiya shablonlarida eski narx va chegirma ko'rsatiladi.")),
            B("eye-outline", T("Видно до печати", "Басуудан мурун көрүнөт", "See before printing", "Baskıdan önce görün", "Chop etishdan oldin ko'ring"),
                T("Живой предпросмотр и предупреждение, если со штрих-кодом что-то не так.",
                  "Түз алдын ала көрүү жана штрих-коддо ката болсо эскертүү.",
                  "A live preview and a warning if something is wrong with the barcode.",
                  "Canlı önizleme ve barkodda bir sorun varsa uyarı.",
                  "Jonli ko'rinish va shtrix-kodda xato bo'lsa ogohlantirish.")),
            B("printer-outline", T("Термопринтер или A4", "Термопринтер же A4", "Thermal or A4", "Termal veya A4", "Termo yoki A4"),
                T("Можно напечатать несколько копий.",
                  "Бир нече нуска басып чыгарууга болот.",
                  "You can print several copies.",
                  "Birkaç kopya yazdırılabilir.",
                  "Bir nechta nusxa chop etish mumkin.")),
        ];
        i.HowTo =
        [
            ActivateStep(i),
            T("Склад → Товары → „⋯“ в строке товара → „Ценник“.", "Кампа → Товарлар → товардын сабындагы „⋯“ → „Баа белгиси“.",
                "Warehouse → Products → “⋯” in the product row → “Price tag”.", "Depo → Ürünler → ürün satırındaki «⋯» → «Fiyat etiketi».",
                "Ombor → Mahsulotlar → mahsulot qatoridagi «⋯» → «Narx yorlig'i»."),
            T("Выберите шаблон и нажмите „Печать“.", "Шаблонду тандап, „Басып чыгаруу“ баскычын басыңыз.",
                "Choose a template and press “Print”.", "Şablonu seçin ve «Yazdır»a basın.", "Shablonni tanlang va «Chop etish»ni bosing."),
        ];
        i.Audience =
        [
            T("Продуктовые магазины", "Азык-түлүк дүкөндөрү", "Grocery shops", "Market mağazaları", "Oziq-ovqat do'konlari"),
            T("Магазины с акциями", "Акциялары бар дүкөндөр", "Shops with promotions", "Kampanyalı mağazalar", "Aksiyalari bor do'konlar"),
            T("Кто клеит ценники каждый день", "Күн сайын баа белгисин чапкандар", "Those who stick price tags every day", "Her gün etiket yapıştıranlar", "Har kuni narx yorlig'i yopishtiradiganlar"),
        ];
        i.PriceTerms = OnceTerms();
        i.Slides =
        [
            S(1, T("Ценник с предпросмотром", "Алдын ала көрүнгөн баа белгиси", "A price tag with a preview", "Önizlemeli etiket", "Ko'rinishli narx yorlig'i")),
            S(2, T("Акционный: старая цена и скидка", "Акциялык: эски баа жана арзандатуу", "Promo: old price and discount", "Promosyon: eski fiyat ve indirim", "Aksiya: eski narx va chegirma")),
            S(3, T("QR-код со штрих-кодом товара", "Товардын штрих-коду менен QR-код", "A QR code with the product barcode", "Ürün barkodlu QR kodu", "Mahsulot shtrix-kodi bilan QR-kod")),
            S(4, T("6 готовых шаблонов и свой", "6 даяр шаблон жана өзүңүздүкү", "6 ready templates and your own", "6 hazır şablon ve kendi şablonunuz", "6 tayyor shablon va o'zingizniki")),
            S(5, T("Термопринтер или A4, несколько копий", "Термопринтер же A4, бир нече нуска", "Thermal printer or A4, several copies", "Termal yazıcı veya A4, birkaç kopya", "Termoprinter yoki A4, bir nechta nusxa")),
        ];
        i.Demo = Demo(T("Ценник за минуту", "Баа белгиси бир мүнөттө", "A price tag in a minute", "Bir dakikada etiket", "Bir daqiqada narx yorlig'i"));
    }

    // ------------------------------------------------------------------ 5.7 Массовая печать ценников
    private static void BulkTag(MarketplaceItem i)
    {
        i.IconKey = "tag-multiple-outline";
        i.Tagline = T("Ценники на все товары — за один проход", "Бардык товарлардын баа белгилери — бир жолу", "Tags for all products in one pass", "Tüm ürünlere tek seferde etiket", "Barcha mahsulotlarga bir yo'la yorliq");
        i.KeyFacts =
        [
            T("До 48 на листе A4", "A4 барагында 48ге чейин", "Up to 48 per A4 sheet", "A4 sayfada 48'e kadar", "A4 varaqda 48 tagacha"),
            T("Поиск и сканер", "Издөө жана сканер", "Search and scanner", "Arama ve okuyucu", "Qidiruv va skaner"),
            T("Помнит настройки", "Жөндөөлөрдү эстейт", "Remembers settings", "Ayarları hatırlar", "Sozlamalarni eslaydi"),
        ];
        i.Benefits =
        [
            B("checkbox-multiple-marked-outline", T("Один проход вместо десятков", "Ондогон жолу эмес — бир жолу", "One pass, not dozens", "Onlarca değil, tek seferde", "O'nlab emas, bir yo'la"),
                T("„Выбрать все“ по найденным и счётчик „Выбрано N из M“.",
                  "Табылгандар боюнча „Баарын тандоо“ жана „M ичинен N тандалды“ эсептегичи.",
                  "“Select all” for the found items and a “Selected N of M” counter.",
                  "Bulunanlar için «Tümünü seç» ve «M içinden N seçildi» sayacı.",
                  "Topilganlar bo'yicha «Hammasini tanlash» va «M dan N tanlandi» hisoblagichi.")),
            B("barcode-scan", T("Найти сканером", "Сканер менен табуу", "Find with a scanner", "Okuyucuyla bulun", "Skaner bilan toping"),
                T("Поиск по названию и штрих-коду; от 4 символов находит и по доп. штрихкоду или артикулу.",
                  "Аты жана штрих-коду боюнча издөө; 4 белгиден баштап кошумча штрих-код же артикул боюнча да табат.",
                  "Search by name and barcode; from 4 characters it also finds by an extra barcode or SKU.",
                  "Ad ve barkoda göre arama; 4 karakterden itibaren ek barkod veya stok koduyla da bulur.",
                  "Nom va shtrix-kod bo'yicha qidiruv; 4 belgidan boshlab qo'shimcha shtrix-kod yoki artikul bo'yicha ham topadi.")),
            B("file-document-outline", T("Экономия бумаги", "Кагаз үнөмдөлөт", "Saves paper", "Kâğıt tasarrufu", "Qog'oz tejaladi"),
                T("На листе A4: 48 ценников 40×20 мм, 32 — 40×30, 24 — 50×30, 18 — 60×40.",
                  "A4 барагына: 40×20 мм — 48, 40×30 — 32, 50×30 — 24, 60×40 — 18 баа белгиси.",
                  "On an A4 sheet: 48 tags of 40×20 mm, 32 of 40×30, 24 of 50×30, 18 of 60×40.",
                  "A4 sayfaya: 40×20 mm 48, 40×30 32, 50×30 24, 60×40 18 etiket.",
                  "A4 varaqqa: 40×20 mm 48 ta, 40×30 — 32, 50×30 — 24, 60×40 — 18 ta yorliq.")),
            B("ruler-square", T("Свой размер и копии", "Өз өлчөмү жана нускалар", "Your size and copies", "Kendi boyut ve kopya", "O'z o'lchami va nusxalar"),
                T("Ширина 15–150 мм, высота 10–150 мм, до 20 копий каждого.",
                  "Туурасы 15–150 мм, бийиктиги 10–150 мм, ар биринен 20 нускага чейин.",
                  "Width 15–150 mm, height 10–150 mm, up to 20 copies of each.",
                  "Genişlik 15–150 mm, yükseklik 10–150 mm, her birinden 20 kopyaya kadar.",
                  "Kengligi 15–150 mm, balandligi 10–150 mm, har biridan 20 nusxagacha.")),
            B("history", T("Помнит настройки", "Жөндөөлөрдү эстейт", "Remembers settings", "Ayarları hatırlar", "Sozlamalarni eslaydi"),
                T("Последний шаблон, размер и принтер сохраняются.",
                  "Акыркы шаблон, өлчөм жана принтер сакталат.",
                  "The last template, size and printer are saved.",
                  "Son şablon, boyut ve yazıcı kaydedilir.",
                  "Oxirgi shablon, o'lcham va printer saqlanadi.")),
        ];
        i.HowTo =
        [
            ActivateStep(i),
            T("Склад → Товары → кнопка „Массовая печать ценников“ (или „Открыть массовую печать“ здесь).",
              "Кампа → Товарлар → „Баа белгилерин массалык басып чыгаруу“ баскычы (же бул жерден „Топтоп басууну ачуу“).",
              "Warehouse → Products → the “Bulk print price tags” button (or “Open bulk printing” here).",
              "Depo → Ürünler → «Toplu fiyat etiketi yazdır» düğmesi (veya buradan «Toplu baskıyı aç»).",
              "Ombor → Mahsulotlar → «Narx yorliqlarini ommaviy chop etish» tugmasi (yoki shu yerdan «Ommaviy chop etishni ochish»)."),
            T("Отметьте товары, выберите шаблон и размер → „Печать“.", "Товарларды белгилеп, шаблон жана өлчөм тандаңыз → „Басып чыгаруу“.",
                "Tick the products, choose a template and size → “Print”.", "Ürünleri işaretleyin, şablon ve boyut seçin → «Yazdır».",
                "Mahsulotlarni belgilang, shablon va o'lchamni tanlang → «Chop etish»."),
        ];
        i.Audience =
        [
            T("Переоценка всего магазина", "Бүт дүкөндү кайра баалоо", "Repricing the whole shop", "Tüm mağazanın yeniden fiyatlandırması", "Butun do'konni qayta baholash"),
            T("Открытие магазина", "Дүкөн ачылганда", "Opening a shop", "Mağaza açılışı", "Do'kon ochilishi"),
            T("Новая поставка", "Жаңы жүк келгенде", "A new delivery", "Yeni sevkiyat", "Yangi yetkazib berish"),
        ];
        i.PriceTerms = OnceTerms();
        i.Slides =
        [
            S(1, T("Отметьте товары — напечатайте все сразу", "Товарларды белгилеңиз — баарын бирден басыңыз", "Tick the products — print them all at once", "Ürünleri işaretleyin — hepsini birden yazdırın", "Mahsulotlarni belgilang — hammasini birdaniga chop eting")),
            S(2, T("Поиск по названию и штрих-коду", "Аты жана штрих-коду боюнча издөө", "Search by name and barcode", "Ad ve barkoda göre arama", "Nom va shtrix-kod bo'yicha qidiruv")),
            S(3, T("Размер, копии, A4 или термопринтер", "Өлчөм, нускалар, A4 же термопринтер", "Size, copies, A4 or thermal printer", "Boyut, kopyalar, A4 veya termal yazıcı", "O'lcham, nusxalar, A4 yoki termoprinter")),
            S(4, T("Лист A4: 48 ценников 40×20 мм", "A4 барагы: 40×20 мм 48 баа белгиси", "An A4 sheet: 48 tags of 40×20 mm", "A4 sayfa: 40×20 mm 48 etiket", "A4 varaq: 40×20 mm 48 ta yorliq")),
        ];
        i.Demo = Demo(T("Много ценников за раз", "Бир жолу көп баа белгиси", "Many price tags at once", "Tek seferde çok etiket", "Bir yo'la ko'p narx yorlig'i"));
    }

    // ------------------------------------------------------------------ 5.8 Редактор этикетки
    private static void LabelEditor(MarketplaceItem i)
    {
        i.IconKey = "label-outline";
        i.Tagline = T("Своя этикетка — как вам удобно", "Өз этикеткаңыз — өзүңүзгө ыңгайлуу", "Your own label, your way", "Kendi etiketiniz, istediğiniz gibi", "O'z yorlig'ingiz — o'zingizga qulay");
        i.KeyFacts =
        [
            T("Перетаскивание", "Сүйрөө", "Drag and drop", "Sürükleme", "Sudrash"),
            T("EAN-13, Code 128, QR", "EAN-13, Code 128, QR", "EAN-13, Code 128, QR", "EAN-13, Code 128, QR", "EAN-13, Code 128, QR"),
            T("Пробная печать", "Сынамык басуу", "Test print", "Deneme baskısı", "Sinov chop etish"),
        ];
        i.Benefits =
        [
            B("cursor-move", T("Как вам нужно", "Сизге кандай керек болсо", "Exactly as you need", "Tam istediğiniz gibi", "Sizga qanday kerak bo'lsa"),
                T("Элементы перетаскиваются, размер — за уголок; рамка не даёт вывести их за край.",
                  "Элементтер сүйрөлөт, өлчөмү — бурчунан; алкак аларды четинен чыгарбайт.",
                  "Elements are dragged, resized by the corner; the frame keeps them from going past the edge.",
                  "Öğeler sürüklenir, boyut köşeden değiştirilir; çerçeve onların kenardan taşmasını önler.",
                  "Elementlar sudraladi, o'lchami burchakdan o'zgartiriladi; ramka ularni chetdan chiqarmaydi.")),
            B("ruler-square", T("Готовые размеры", "Даяр өлчөмдөр", "Ready sizes", "Hazır boyutlar", "Tayyor o'lchamlar"),
                T("39×29, 58×40, 40×30 мм или свой: 15–150 × 10–150 мм.",
                  "39×29, 58×40, 40×30 мм же өзүңүздүкү: 15–150 × 10–150 мм.",
                  "39×29, 58×40, 40×30 mm or your own: 15–150 × 10–150 mm.",
                  "39×29, 58×40, 40×30 mm veya kendi boyutunuz: 15–150 × 10–150 mm.",
                  "39×29, 58×40, 40×30 mm yoki o'zingizniki: 15–150 × 10–150 mm.")),
            B("barcode", T("Любой штрих-код", "Каалаган штрих-код", "Any barcode", "Her tür barkod", "Istalgan shtrix-kod"),
                T("Авто, EAN-13, Code 128 или QR; 7 шрифтов; валюта сом, $, ₽, so'm, ₸, ₺.",
                  "Авто, EAN-13, Code 128 же QR; 7 шрифт; валюта сом, $, ₽, so'm, ₸, ₺.",
                  "Auto, EAN-13, Code 128 or QR; 7 fonts; currency som, $, ₽, so'm, ₸, ₺.",
                  "Otomatik, EAN-13, Code 128 veya QR; 7 yazı tipi; para birimi som, $, ₽, so'm, ₸, ₺.",
                  "Avto, EAN-13, Code 128 yoki QR; 7 ta shrift; valyuta som, $, ₽, so'm, ₸, ₺.")),
            B("printer-settings", T("Калибровка принтера", "Принтерди тууралоо", "Printer calibration", "Yazıcı kalibrasyonu", "Printerni sozlash"),
                T("Зазор, сдвиг, режим без драйвера (ESC/POS или TSPL) и пробная печать.",
                  "Аралык, жылдыруу, драйверсиз режим (ESC/POS же TSPL) жана сынамык басуу.",
                  "Gap, offset, driverless mode (ESC/POS or TSPL) and a test print.",
                  "Boşluk, kaydırma, sürücüsüz mod (ESC/POS veya TSPL) ve deneme baskısı.",
                  "Oraliq, siljitish, drayversiz rejim (ESC/POS yoki TSPL) va sinov chop etish.")),
            B("hanger", T("Размер и цвет", "Өлчөм жана түс", "Size and color", "Beden ve renk", "O'lcham va rang"),
                T("Для магазинов одежды — отдельный элемент „Размер и цвет“.",
                  "Кийим дүкөндөрү үчүн — „Өлчөм жана түс“ элементи.",
                  "For clothing shops — a separate “Size and color” element.",
                  "Giyim mağazaları için — ayrı bir «Beden ve renk» öğesi.",
                  "Kiyim do'konlari uchun — alohida «O'lcham va rang» elementi.")),
        ];
        i.HowTo =
        [
            ActivateStep(i),
            T("„Открыть редактор“.", "„Редакторду ачуу“.", "Press “Open editor”.", "«Düzenleyiciyi aç».", "«Muharrirni ochish»."),
            T("Расставьте элементы → „Пробная печать“ → „Сохранить“.", "Элементтерди жайгаштырыңыз → „Сынамык басуу“ → „Сактоо“.",
                "Arrange the elements → “Test print” → “Save”.", "Öğeleri yerleştirin → «Deneme baskısı» → «Kaydet».", "Elementlarni joylashtiring → «Sinov chop etish» → «Saqlash»."),
        ];
        i.Audience =
        [
            T("Свой принтер этикеток", "Өз этикетка принтери", "Your own label printer", "Kendi etiket yazıcınız", "O'z yorliq printeri"),
            T("Магазины одежды", "Кийим дүкөндөрү", "Clothing shops", "Giyim mağazaları", "Kiyim do'konlari"),
            T("Фасовка и развес", "Таңгактоо жана салмак", "Packing and weighing", "Paketleme ve tartım", "Qadoqlash va tortish"),
        ];
        i.PriceTerms = OnceTerms();
        i.Slides =
        [
            S(1, T("Этикетка из готовых элементов", "Даяр элементтерден этикетка", "A label from ready elements", "Hazır öğelerden etiket", "Tayyor elementlardan yorliq")),
            S(2, T("Перетаскивайте и меняйте размер", "Сүйрөңүз жана өлчөмүн өзгөртүңүз", "Drag and resize", "Sürükleyin ve boyutunu değiştirin", "Sudrang va o'lchamini o'zgartiring")),
            S(3, T("Готовые размеры или свой", "Даяр өлчөмдөр же өзүңүздүкү", "Ready sizes or your own", "Hazır boyutlar veya kendi boyutunuz", "Tayyor o'lchamlar yoki o'zingizniki")),
            S(4, T("EAN-13, Code 128 или QR", "EAN-13, Code 128 же QR", "EAN-13, Code 128 or QR", "EAN-13, Code 128 veya QR", "EAN-13, Code 128 yoki QR")),
            S(5, T("Калибровка и пробная печать", "Тууралоо жана сынамык басуу", "Calibration and test print", "Kalibrasyon ve deneme baskısı", "Sozlash va sinov chop etish")),
        ];
        i.Demo = Demo(T("Своя этикетка за минуту", "Өз этикеткаңыз бир мүнөттө", "Your own label in a minute", "Bir dakikada kendi etiketiniz", "Bir daqiqada o'z yorlig'ingiz"));
    }

    // ------------------------------------------------------------------ 5.9 Отчёты в Word и Excel
    private static void Export(MarketplaceItem i)
    {
        i.IconKey = "file-export-outline";
        i.Tagline = T("Отчёт за период — одним файлом, без Office", "Мезгилдин отчёту — бир файл, Office'сиз",
            "A period report in one file, no Office", "Dönem raporu tek dosyada, Office olmadan", "Davr hisoboti — bitta faylda, Office'siz");
        i.KeyFacts =
        [
            T("Word с графиками", "Графиктүү Word", "Word with charts", "Grafikli Word", "Grafikli Word"),
            T("Excel: все чеки", "Excel: бардык чектер", "Excel: all receipts", "Excel: tüm fişler", "Excel: barcha cheklar"),
            T("Без Office", "Office'сиз", "No Office", "Office'siz", "Office'siz"),
        ];
        i.Benefits =
        [
            B("file-word-outline", T("Отчёт, который не стыдно отправить", "Жөнөтүүгө уят эмес отчёт", "A report worth sending", "Göndermeye değer rapor", "Yuborishga arziydigan hisobot"),
                T("5 разделов с графиками: итоги, выручка по дням, топ товаров, ABC-анализ, что пора заказать.",
                  "Графиктери бар 5 бөлүм: жыйынтыктар, күндөр боюнча түшүм, топ товарлар, ABC-анализ, эмнеге буйрутма берүү керек.",
                  "5 sections with charts: totals, revenue by day, top products, ABC analysis, what to reorder.",
                  "Grafikli 5 bölüm: toplamlar, günlük ciro, en çok satanlar, ABC analizi, sipariş edilecekler.",
                  "Grafikli 5 bo'lim: yakunlar, kunlik tushum, top mahsulotlar, ABC-tahlil, nimani buyurtma qilish kerak.")),
            B("file-excel-outline", T("Все чеки в Excel", "Бардык чектер Excel'де", "All receipts in Excel", "Tüm fişler Excel'de", "Barcha cheklar Excel'da"),
                T("Листы „Чеки“ и „Товары“ — для сверки и бухгалтерии.",
                  "„Чектер“ жана „Товарлар“ барактары — салыштыруу жана бухгалтерия үчүн.",
                  "“Receipts” and “Products” sheets — for reconciliation and accounting.",
                  "«Fişler» ve «Ürünler» sayfaları — mutabakat ve muhasebe için.",
                  "«Cheklar» va «Mahsulotlar» varaqlari — solishtirish va buxgalteriya uchun.")),
            B("file-check-outline", T("Без Microsoft Office", "Microsoft Office'сиз", "No Microsoft Office", "Microsoft Office gerekmez", "Microsoft Office'siz"),
                T("Файл собирается кассой; открыть можно где угодно.",
                  "Файлды касса түзөт; каалаган жерде ачса болот.",
                  "The file is built by the till; you can open it anywhere.",
                  "Dosyayı kasa oluşturur; her yerde açılabilir.",
                  "Faylni kassa tayyorlaydi; istalgan joyda ochish mumkin.")),
            B("cart-arrow-down", T("Что пора заказать", "Эмнеге буйрутма берүү керек", "What to reorder", "Sipariş edilecekler", "Nimani buyurtma qilish"),
                T("Отдельный раздел отчёта Word.",
                  "Word отчётунун өзүнчө бөлүмү.",
                  "A separate section of the Word report.",
                  "Word raporunun ayrı bir bölümü.",
                  "Word hisobotining alohida bo'limi.")),
        ];
        i.HowTo =
        [
            SubscribeStep(),
            T("„Отчёты“ → „Финансы“ → выберите период.", "„Отчёттор“ → „Каржы“ → мезгилди тандаңыз.", "“Reports” → “Finance” → choose a period.", "«Raporlar» → «Finans» → dönemi seçin.", "«Hisobotlar» → «Moliya» → davrni tanlang."),
            T("Нажмите „Word“ или „Excel“ в верхней строке.", "Жогорку саптагы „Word“ же „Excel“ баскычын басыңыз.", "Press “Word” or “Excel” in the top row.", "Üst satırdaki «Word» veya «Excel»e basın.", "Yuqori qatordagi «Word» yoki «Excel» tugmasini bosing."),
        ];
        i.Audience =
        [
            T("Владелец, которому нужен отчёт", "Отчёт керек болгон ээси", "An owner who needs a report", "Rapora ihtiyacı olan sahip", "Hisobot kerak bo'lgan ega"),
            T("Бухгалтерия и сверка чеков", "Бухгалтерия жана чектерди салыштыруу", "Accounting and receipt reconciliation", "Muhasebe ve fiş mutabakatı", "Buxgalteriya va cheklarni solishtirish"),
        ];
        i.PriceTerms = TariffTerms();
        i.Slides =
        [
            S(1, T("Кнопки „Word“ и „Excel“ в „Отчётах“", "„Отчёттордогу“ „Word“ жана „Excel“ баскычтары", "The “Word” and “Excel” buttons in “Reports”", "«Raporlar»daki «Word» ve «Excel» düğmeleri", "«Hisobotlar»dagi «Word» va «Excel» tugmalari")),
            S(2, T("Итоги периода и структура", "Мезгилдин жыйынтыгы жана түзүмү", "Period totals and breakdown", "Dönem toplamları ve yapısı", "Davr yakunlari va tarkibi")),
            S(3, T("Выручка по дням", "Күндөр боюнча түшүм", "Revenue by day", "Günlük ciro", "Kunlik tushum")),
            S(4, T("Топ товаров и ABC-анализ", "Топ товарлар жана ABC-анализ", "Top products and ABC analysis", "En çok satanlar ve ABC analizi", "Top mahsulotlar va ABC-tahlil")),
            S(5, T("Excel: все чеки за период", "Excel: мезгилдеги бардык чектер", "Excel: all receipts for the period", "Excel: dönemdeki tüm fişler", "Excel: davrdagi barcha cheklar")),
        ];
        i.Demo = Demo(T("Отчёт за месяц в Word", "Айлык отчёт Word'до", "A monthly report in Word", "Aylık rapor Word'de", "Oylik hisobot Word'da"));
    }

    // ------------------------------------------------------------------ 5.10 Телеграм-бот владельца
    private static void Telegram(MarketplaceItem i)
    {
        i.IconKey = "send-outline";
        i.Tagline = T("Итоги смены и ответы — в вашем Telegram", "Сменанын жыйынтыгы жана жооптор — Telegram'ыңызда",
            "Shift results and answers in your Telegram", "Vardiya özeti ve yanıtlar Telegram'ınızda", "Smena yakuni va javoblar — Telegram'ingizda");
        i.KeyFacts =
        [
            T("Сводка смены", "Смена жыйынтыгы", "Shift summary", "Vardiya özeti", "Smena hisoboti"),
            T("Код из 6 цифр", "6 сандуу код", "6-digit code", "6 haneli kod", "6 xonali kod"),
            T("Команды", "Буйруктар", "Commands", "Komutlar", "Buyruqlar"),
        ];
        i.Benefits =
        [
            B("cellphone-message", T("Смена в кармане", "Смена чөнтөгүңүздө", "The shift in your pocket", "Vardiya cebinizde", "Smena cho'ntagingizda"),
                T("Сводка приходит сама при закрытии смены: недостача или излишек видны сразу.",
                  "Смена жабылганда жыйынтык өзү келет: жетишпестик же ашыкча дароо көрүнөт.",
                  "The summary arrives by itself when the shift closes: a shortage or surplus is visible at once.",
                  "Özet vardiya kapanınca kendiliğinden gelir: eksik veya fazla hemen görünür.",
                  "Smena yopilganda hisobot o'zi keladi: kamomad yoki ortiqcha darhol ko'rinadi.")),
            B("console-line", T("Ответ по команде", "Буйрук боюнча жооп", "Answers on command", "Komutla yanıt", "Buyruq bo'yicha javob"),
                T("/segodnya, /nedelya, /mesyac — выручка; /dolgi, /ostatki, /zakaz, /top и другие (/help).",
                  "/segodnya, /nedelya, /mesyac — түшүм; /dolgi, /ostatki, /zakaz, /top жана башкалар (/help).",
                  "/segodnya, /nedelya, /mesyac — revenue; /dolgi, /ostatki, /zakaz, /top and others (/help).",
                  "/segodnya, /nedelya, /mesyac — ciro; /dolgi, /ostatki, /zakaz, /top ve diğerleri (/help).",
                  "/segodnya, /nedelya, /mesyac — tushum; /dolgi, /ostatki, /zakaz, /top va boshqalar (/help).")),
            B("shield-lock-outline", T("Только вам", "Сизге гана", "Only for you", "Yalnızca size", "Faqat sizga"),
                T("Получатель подтверждается одноразовым кодом из 6 цифр (10 минут, не больше 5 попыток); долги видит только владелец.",
                  "Алуучу 6 сандуу бир жолку код менен ырасталат (10 мүнөт, 5 аракеттен ашпайт); карыздарды ээси гана көрөт.",
                  "The recipient is confirmed with a one-time 6-digit code (10 minutes, at most 5 attempts); only the owner sees debts.",
                  "Alıcı 6 haneli tek kullanımlık kodla doğrulanır (10 dakika, en fazla 5 deneme); borçları yalnızca sahip görür.",
                  "Qabul qiluvchi 6 xonali bir martalik kod bilan tasdiqlanadi (10 daqiqa, 5 tadan ko'p bo'lmagan urinish); qarzlarni faqat ega ko'radi.")),
            B("account-group-outline", T("И для покупателей", "Сатып алуучулар үчүн да", "For customers too", "Müşteriler için de", "Xaridorlar uchun ham"),
                T("Покупатели смотрят каталог и свой долг, кнопки „Позвонить“ и „Адрес“.",
                  "Сатып алуучулар каталогду жана өз карызын көрөт, „Чалуу“ жана „Дарек“ баскычтары.",
                  "Customers view the catalog and their own debt, with “Call” and “Address” buttons.",
                  "Müşteriler kataloğu ve kendi borçlarını görür, «Ara» ve «Adres» düğmeleri vardır.",
                  "Xaridorlar katalogni va o'z qarzini ko'radi, «Qo'ng'iroq» va «Manzil» tugmalari bor.")),
            B("server-outline", T("Работа при выключенной кассе", "Касса өчүк болсо да", "Works with the till off", "Kasa kapalıyken de", "Kassa o'chiq bo'lsa ham"),
                T("Бота можно перенести на сервер — тогда он отвечает, даже когда касса выключена.",
                  "Ботту серверге которсо болот — анда касса өчүк болсо да жооп берет.",
                  "The bot can be moved to the server — then it answers even when the till is off.",
                  "Bot sunucuya taşınabilir — o zaman kasa kapalıyken bile yanıt verir.",
                  "Botni serverga ko'chirish mumkin — shunda u kassa o'chiq bo'lganda ham javob beradi.")),
        ];
        i.HowTo =
        [
            SubscribeStep(),
            T("„Настроить бота“ → создайте бота в BotFather (кнопка откроет его) и вставьте токен → „Проверить токен“.",
              "„Ботту жөндөө“ → BotFather'де бот түзүңүз (баскыч аны ачат) жана токенди коюңуз → „Токенди текшерүү“.",
              "“Set up the bot” → create a bot in BotFather (the button opens it) and paste the token → “Check token”.",
              "«Botu ayarla» → BotFather'da bir bot oluşturun (düğme onu açar) ve token'ı yapıştırın → «Token'ı kontrol et».",
              "«Botni sozlash» → BotFather'da bot yarating (tugma uni ochadi) va tokenni qo'ying → «Tokenni tekshirish»."),
            T("„Определить получателя“ → отправьте боту код из 6 цифр.", "„Алуучуну аныктоо“ → ботко 6 сандуу кодду жөнөтүңүз.",
                "“Detect the recipient” → send the bot the 6-digit code.", "«Alıcıyı belirle» → bota 6 haneli kodu gönderin.", "«Qabul qiluvchini aniqlash» → botga 6 xonali kodni yuboring."),
            T("Включите „Присылать сводку при закрытии смены“ и „Отвечать на команды в Telegram“ → „Отправить пробное сообщение“.", "„Смена жабылганда жыйынтык жөнөтүү“ жана „Telegram'дагы буйруктарга жооп берүү“ белгилерин күйгүзүңүз → „Сыноо билдирүүсүн жөнөтүү“.",
                "Turn on the summary and command options → “Send a test message”.", "Özet ve komut seçeneklerini açın → «Deneme mesajı gönder».", "Hisobot va buyruqlar variantlarini yoqing → «Sinov xabarini yuborish»."),
        ];
        i.Audience =
        [
            T("Владелец не в магазине", "Ээси дүкөндө эмес", "The owner is away from the shop", "Sahibi mağazada değil", "Ega do'konda emas"),
            T("Несколько точек", "Бир нече түйүн", "Several outlets", "Birden çok nokta", "Bir nechta nuqta"),
            T("Контроль недостач", "Жетишпестикти көзөмөлдөө", "Shortage control", "Eksik kontrolü", "Kamomadni nazorat qilish"),
        ];
        i.PriceTerms = TariffTerms();
        i.Slides =
        [
            S(1, T("Настройка за 4 шага", "4 кадамда жөндөө", "Setup in 4 steps", "4 adımda kurulum", "4 qadamda sozlash")),
            S(2, T("Получатель — по одноразовому коду", "Алуучу — бир жолку код менен", "The recipient — by a one-time code", "Alıcı — tek kullanımlık kodla", "Qabul qiluvchi — bir martalik kod bilan")),
            S(3, T("Сводка при закрытии смены", "Смена жабылгандагы жыйынтык", "The summary when a shift closes", "Vardiya kapanırken özet", "Smena yopilgandagi hisobot")),
            S(4, T("Выручка за день по команде", "Буйрук менен күндүк түшүм", "Daily revenue on command", "Komutla günlük ciro", "Buyruq bilan kunlik tushum")),
            S(5, T("Что пора заказать", "Эмнеге буйрутма берүү керек", "What to reorder", "Sipariş edilecekler", "Nimani buyurtma qilish kerak")),
        ];
        i.Demo = Demo(T("Смена закрыта — отчёт уже у вас", "Смена жабылды — отчёт сизде", "The shift is closed — the report is already with you", "Vardiya kapandı — rapor sizde", "Smena yopildi — hisobot sizda"));
    }

    // ------------------------------------------------------------------ 5.11 Расширенные итоги смены
    private static void ShiftStats(MarketplaceItem i)
    {
        i.IconKey = "receipt-text-outline";
        i.Tagline = T("Куда ушли деньги смены — по полочкам", "Сменанын акчасы кайда кетти — так-так", "Where the shift's money went", "Vardiyanın parası nereye gitti", "Smena puli qayerga ketdi");
        i.KeyFacts =
        [
            T("5 плиток", "5 плитка", "5 tiles", "5 kutucuk", "5 ta plitka"),
            T("X/Z-отчёт", "X/Z-отчёт", "X/Z report", "X/Z raporu", "X/Z-hisobot"),
            T("Подробности по нажатию", "Басканда толугу", "Details on click", "Tıklayınca ayrıntı", "Bosganda tafsilot"),
        ];
        i.Benefits =
        [
            B("view-grid-outline", T("Всё на одном экране", "Баары бир экранда", "All on one screen", "Hepsi tek ekranda", "Hammasi bitta ekranda"),
                T("Возвраты, списания, расход, долги и скидки рядом с выручкой.",
                  "Кайтаруулар, эсептен чыгаруулар, чыгым, карыздар жана арзандатуулар түшүмдүн жанында.",
                  "Returns, write-offs, expenses, debts and discounts next to the revenue.",
                  "İadeler, stok düşümleri, giderler, borçlar ve indirimler cironun yanında.",
                  "Qaytarishlar, hisobdan chiqarishlar, xarajat, qarzlar va chegirmalar tushum yonida.")),
            B("gesture-tap", T("Подробности по нажатию", "Басканда толук маалымат", "Details on click", "Tıklayınca ayrıntı", "Bosganda tafsilot"),
                T("Плитка открывает список операций смены.",
                  "Плитка сменанын операцияларынын тизмесин ачат.",
                  "A tile opens the list of the shift's operations.",
                  "Kutucuk vardiyanın işlem listesini açar.",
                  "Plitka smena operatsiyalari ro'yxatini ochadi.")),
            B("printer-pos", T("В X/Z-отчёте", "X/Z-отчётто", "In the X/Z report", "X/Z raporunda", "X/Z-hisobotda"),
                T("Возвраты, списания, расход и оплата долгов печатаются в отчёте.",
                  "Кайтаруулар, эсептен чыгаруулар, чыгым жана карыз төлөмдөрү отчётто басылат.",
                  "Returns, write-offs, expenses and debt payments are printed in the report.",
                  "İadeler, stok düşümleri, giderler ve borç ödemeleri raporda basılır.",
                  "Qaytarishlar, hisobdan chiqarishlar, xarajat va qarz to'lovlari hisobotda chop etiladi.")),
            B("scale-balance", T("Проще сверить кассу", "Кассаны салыштыруу жеңил", "Easier cash check", "Kasa kontrolü kolay", "Kassani tekshirish oson"),
                T("Видно, почему наличных меньше, чем выручка.",
                  "Накталай эмне үчүн түшүмдөн аз экени көрүнөт.",
                  "You can see why there is less cash than revenue.",
                  "Nakdin neden cirodan az olduğu görünür.",
                  "Naqd pul nega tushumdan kamligi ko'rinadi.")),
        ];
        i.HowTo =
        [
            SubscribeStep(),
            T("Меню кассы → „Смены“ → выберите смену.", "Касса менюсу → „Смена“ → сменаны тандаңыз.", "Till menu → “Shifts” → choose a shift.", "Kasa menüsü → «Vardiyalar» → vardiyayı seçin.", "Kassa menyusi → «Smenalar» → smenani tanlang."),
        ];
        i.Audience =
        [
            T("Владелец, который сверяет кассу", "Кассаны салыштырган ээси", "An owner who reconciles the till", "Kasayı mutabık eden sahip", "Kassani solishtiradigan ega"),
            T("Магазины с возвратами и расходом из кассы", "Кайтаруулары жана кассадан чыгымы бар дүкөндөр", "Shops with returns and till expenses", "İadesi ve kasadan gideri olan mağazalar", "Qaytarish va kassadan xarajati bor do'konlar"),
        ];
        i.PriceTerms = TariffTerms();
        i.Slides =
        [
            S(1, T("Пять плиток под выручкой смены", "Сменанын түшүмүнүн астында беш плитка", "Five tiles under the shift revenue", "Vardiya cirosunun altında beş kutucuk", "Smena tushumi ostida beshta plitka")),
            S(2, T("Нажмите — откроется список", "Басыңыз — тизме ачылат", "Click — the list opens", "Tıklayın — liste açılır", "Bosing — ro'yxat ochiladi")),
            S(3, T("Расход смены списком", "Сменанын чыгымы тизме менен", "The shift's expenses as a list", "Vardiya giderleri liste olarak", "Smena xarajati ro'yxat ko'rinishida")),
            S(4, T("X-отчёт смены", "Сменанын X-отчёту", "The shift's X report", "Vardiya X raporu", "Smena X-hisoboti")),
        ];
        i.Demo = Demo(T("Итоги смены подробно", "Сменанын жыйынтыгы толук", "Shift results in detail", "Vardiya özeti ayrıntılı", "Smena yakuni batafsil"));
    }

    // ------------------------------------------------------------------ 5.12 Языковой пакет
    private static void Language(MarketplaceItem i)
    {
        i.IconKey = "translate";
        i.Tagline = T("Касса на английском, турецком и узбекском", "Касса англис, түрк жана өзбек тилинде",
            "The till in English, Turkish and Uzbek", "Kasa İngilizce, Türkçe ve Özbekçe", "Kassa ingliz, turk va o'zbek tilida");
        i.KeyFacts =
        [
            T("3 языка", "3 тил", "3 languages", "3 dil", "3 ta til"),
            T("Без скачивания", "Жүктөөсүз", "No download", "İndirmesiz", "Yuklab olmasdan"),
            T("Сразу", "Дароо", "Instantly", "Hemen", "Darhol"),
        ];
        i.Benefits =
        [
            B("account-voice", T("Кассир на родном языке", "Кассир эне тилинде", "Cashiers in their language", "Kasiyer kendi dilinde", "Kassir o'z tilida"),
                T("Меньше ошибок у кассиров, которым удобнее английский, турецкий или узбекский.",
                  "Англис, түрк же өзбек тили ыңгайлуу кассирлер азыраак жаңылат.",
                  "Fewer mistakes for cashiers who are more comfortable in English, Turkish or Uzbek.",
                  "İngilizce, Türkçe veya Özbekçe daha rahat olan kasiyerler daha az hata yapar.",
                  "Ingliz, turk yoki o'zbek tili qulay bo'lgan kassirlar kamroq xato qiladi.")),
            B("download-off-outline", T("Ничего не скачивать", "Эч нерсе жүктөбөйсүз", "Nothing to download", "İndirme yok", "Yuklab olish yo'q"),
                T("Словари встроены — язык включается сразу после активации.",
                  "Сөздүктөр орнотулган — тил активдештирүүдөн кийин дароо күйөт.",
                  "Dictionaries are built in — the language turns on right after activation.",
                  "Sözlükler dahili — dil etkinleştirmeden hemen sonra açılır.",
                  "Lug'atlar ichiga o'rnatilgan — til faollashtirilgach darhol yoqiladi.")),
            B("cog-outline", T("Переключение в настройках", "Жөндөөлөрдө которуу", "Switch in settings", "Ayarlardan değiştirin", "Sozlamalarda almashtirish"),
                T("Настройки → Экран → нужный язык.",
                  "Жөндөөлөр → Экран → керектүү тил.",
                  "Settings → Screen → the language you need.",
                  "Ayarlar → Ekran → istediğiniz dil.",
                  "Sozlamalar → Ekran → kerakli til.")),
        ];
        i.HowTo =
        [
            T("Если функция ещё не активирована — активируйте ключом.", "Функция али активдештирилбесе — ачкыч менен активдештириңиз.", "If the function is not activated yet, activate it with a serial key.", "İşlev henüz etkinleştirilmediyse seri anahtarla etkinleştirin.", "Funksiya hali faollashtirilmagan bo'lsa, seriya kaliti bilan faollashtiring."),
            T("Настройки → Экран → выберите язык.", "Жөндөөлөр → Экран → тилди тандаңыз.", "Settings → Screen → choose the language.", "Ayarlar → Ekran → dili seçin.", "Sozlamalar → Ekran → tilni tanlang."),
        ];
        i.Audience =
        [
            T("Кассиры из других стран", "Башка өлкөлөрдөн келген кассирлер", "Cashiers from other countries", "Başka ülkelerden gelen kasiyerler", "Boshqa mamlakatlardan kelgan kassirlar"),
            T("Туристические места", "Туристтик жерлер", "Tourist places", "Turistik yerler", "Turistik joylar"),
        ];
        i.PriceTerms = OnceTerms();
        i.Slides =
        [
            S(1, T("Пять языков в настройках", "Жөндөөлөрдө беш тил", "Five languages in settings", "Ayarlarda beş dil", "Sozlamalarda beshta til")),
            S(2, "English"),
            S(3, "Türkçe"),
            S(4, "O'zbekcha"),
        ];
        i.Demo = Demo(T("Язык меняется на лету", "Тил ошол замат өзгөрөт", "The language changes on the fly", "Dil anında değişir", "Til shu zahoti o'zgaradi"));
    }

    // ------------------------------------------------------------------ 5.13 SMS (скоро): медиа нет, «Чем поможет»/«Как включить» не заполняем
    private static void Sms(MarketplaceItem i)
    {
        i.IconKey = "message-text-outline";
        i.Tagline = T("Скоро: статус заказа и акции по SMS", "Жакында: заказдын статусу жана акциялар SMS менен",
            "Soon: order status and promotions by SMS", "Yakında: SMS ile sipariş durumu ve kampanyalar", "Tez orada: buyurtma holati va aksiyalar SMS orqali");
    }
}
