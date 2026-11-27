using System.Collections.Generic;
using static NurMarketKassa.AvaloniaHost.Services.MockDsl;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26: демонстрации работы и «Что получите» — пакеты «Стандарта». Правила: 3–5 шагов; подпись шага «что нажать / что видно / результат» на 5 языках (T);
// кадр помещается в рамку 640×340 (≈5 строк или полей; тест MarketplaceDemosTests измеряет высоту на всех языках); выдуманные имена и суммы, без реальных данных;
// содержание — только то, что функция делает по тексту её карточки (MarketplaceModuleContent) и коду.
public static partial class MarketplaceDemos
{
    private static void AddGroupD(Dictionary<string, MarketplaceDemoEntry> d)
    {
        // ── ИИ-советник и ИИ в боте ──
        d["pack:ai"] = new(() =>
        [
            Step(T("1. Откройте «Нур Советник» и задайте вопрос по продажам или закупкам", "1. «Нур Кеңешчини» ачып, сатуу же сатып алуу боюнча суроо бериңиз", "1. Open “Nur Advisor” and ask about sales or purchasing", "1. «Nur Danışman»ı açın ve satış veya alım hakkında sorun", "1. «Nur Maslahatchi»ni oching va sotuv yoki xarid haqida so'rang"),
                T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"),
                Input(T("Вопрос", "Суроо", "Question", "Soru", "Savol"), T("Что докупить на этой неделе?", "Бул жумада эмне кошумча алуу керек?", "What to restock this week?", "Bu hafta ne alınmalı?", "Shu hafta nimani qo'shimcha olish kerak?"), hot: true),
                Btn(T("Спросить", "Суроо", "Ask", "Sor", "So'rash"), true)),
            Step(T("2. Советник смотрит продажи и остатки и отвечает по делу", "2. Кеңешчи сатууларды жана калдыктарды карап, так жооп берет", "2. The advisor looks at sales and stock and answers to the point", "2. Danışman satışlara ve stoğa bakıp net cevap verir", "2. Maslahatchi sotuv va qoldiqlarni ko'rib, aniq javob beradi"),
                T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"),
                Row(T("Молоко 1 л", "Сүт 1 л", "Milk 1 l", "Süt 1 l", "Sut 1 l"), T("Заказать 30", "30 буйрутма", "Order 30", "30 sipariş", "30 buyurtma"), MockTone.Warn, true, T("Осталось 4 шт., уходит 10 в день", "4 даана калды, күнүнө 10 кетет", "4 left, 10 sell a day", "4 kaldı, günde 10 gidiyor", "4 dona qoldi, kuniga 10 ta ketadi")),
                Row(T("Сахар 1 кг", "Кант 1 кг", "Sugar 1 kg", "Şeker 1 kg", "Shakar 1 kg"), T("Хватит", "Жетет", "Enough", "Yeter", "Yetadi"), MockTone.Good, false, T("На 12 дней", "12 күнгө", "For 12 days", "12 güne", "12 kunga"))),
            Step(T("3. Тот же ИИ отвечает владельцу в Telegram-боте", "3. Ошол эле ИИ Telegram-боттогу ээсине жооп берет", "3. The same AI answers the owner in the Telegram bot", "3. Aynı yapay zekâ sahibe Telegram botunda cevap verir", "3. Xuddi shu SI egasiga Telegram-botda javob beradi"),
                T("Telegram-бот", "Telegram-бот", "Telegram bot", "Telegram botu", "Telegram-bot"),
                Input(T("Сообщение", "Билдирүү", "Message", "Mesaj", "Xabar"), T("Сколько продали сегодня?", "Бүгүн канча сатылды?", "How much did we sell today?", "Bugün ne kadar sattık?", "Bugun qancha sotdik?"), true),
                Note(T("Сегодня: 42 чека на 38 500 сом", "Бүгүн: 42 чек, 38 500 сом", "Today: 42 receipts, 38,500 som", "Bugün: 42 fiş, 38.500 som", "Bugun: 42 chek, 38 500 so'm"), MockTone.Good)),
            Step(T("4. В карточке товара «Найти фото» ищет картинку в интернете", "4. Товардын карточкасындагы «Сүрөт табуу» интернеттен сүрөт издейт", "4. In the product card “Find photo” searches the web for a picture", "4. Ürün kartında «Fotoğraf bul» internette resim arar", "4. Mahsulot kartasida «Rasm topish» internetdan rasm qidiradi"),
                T("Карточка товара", "Товардын карточкасы", "Product card", "Ürün kartı", "Mahsulot kartasi"),
                Input(T("Название", "Аталышы", "Name", "Ad", "Nomi"), T("Чай чёрный 100 г", "Кара чай 100 г", "Black tea 100 g", "Siyah çay 100 g", "Qora choy 100 g")),
                Btn(T("Найти фото", "Сүрөт табуу", "Find photo", "Fotoğraf bul", "Rasm topish"), true),
                Note(T("Фото найдено — товар с картинкой", "Сүрөт табылды — товар сүрөттүү болду", "Photo found — the product has a picture", "Fotoğraf bulundu — ürün resimli", "Rasm topildi — mahsulot rasmli"), MockTone.Good)),
        ],
        () =>
        [
            Gain("robot-outline", T("Совет вместо догадок", "Божомолдун ордуна кеңеш", "Advice, not guesses", "Tahmin yerine tavsiye", "Taxmin o'rniga maslahat"),
                T("Нур Советник подсказывает, что докупить и что продаётся лучше всего.", "Нур Кеңешчи эмнени кошумча алууну жана эмне жакшы сатыларын айтат.", "Nur Advisor tells you what to restock and what sells best.", "Nur Danışman neyin alınacağını ve en iyi neyin satıldığını söyler.", "Nur Maslahatchi nimani olish va nima yaxshi sotilishini aytadi.")),
            Gain("send-outline", T("Ответы в Telegram", "Telegram'да жооптор", "Answers in Telegram", "Telegram'da cevaplar", "Telegram'da javoblar"),
                T("Спросите бота — ИИ ответит про продажи, не заходя в программу.", "Боттон сураңыз — ИИ программага кирбей эле сатуулар тууралуу жооп берет.", "Ask the bot — the AI answers about sales without opening the program.", "Bota sorun — yapay zekâ programı açmadan satışları anlatır.", "Botdan so'rang — SI dasturga kirmasdan sotuvlar haqida javob beradi.")),
            Gain("image-outline", T("Фото товаров быстрее", "Товар сүрөттөрү тезирээк", "Product photos faster", "Ürün fotoğrafları daha hızlı", "Mahsulot rasmlari tezroq"),
                T("Картинку для карточки находит поиск, а не фотосессия.", "Карточка үчүн сүрөттү сүрөткө тартуу эмес, издөө табат.", "Search finds the picture for the card — no photo shoot needed.", "Kart için resmi fotoğraf çekimi değil arama bulur.", "Karta uchun rasmni suratga olish emas, qidiruv topadi.")),
        ],
        () => T("Нур Советник смотрит ваши продажи и остатки и подсказывает, что докупить и что идёт лучше всего; тот же ИИ отвечает в Telegram-боте, а в карточке товара ищет фото. Пример: спрашиваете «Что докупить на этой неделе?» — видите, что молока осталось 4 шт., а уходит по 10 в день.",
            "Нур Кеңешчи сатууларыңызды жана калдыктарды карап, эмнени кошумча алууну жана эмне жакшы сатыларын айтат; ошол эле ИИ Telegram-ботто жооп берип, товардын карточкасында сүрөт издейт. Мисал: «Бул жумада эмне кошумча алуу керек?» деп сурайсыз — сүт 4 даана калганын, күнүнө 10 даана кетерин көрөсүз.",
            "Nur Advisor looks at your sales and stock and suggests what to restock and what sells best; the same AI answers in the Telegram bot and finds a photo in the product card. Example: you ask “What to restock this week?” and see that only 4 packs of milk are left while 10 sell a day.",
            "Nur Danışman satışlarınıza ve stoğunuza bakıp neyin alınacağını ve en iyi neyin sattığını söyler; aynı yapay zekâ Telegram botunda cevap verir ve ürün kartında fotoğraf arar. Örnek: «Bu hafta ne alınmalı?» diye sorarsınız — sütten 4 adet kaldığını, günde 10 adet gittiğini görürsünüz.",
            "Nur Maslahatchi sotuv va qoldiqlaringizni ko'rib, nimani olish va nima yaxshi sotilishini aytadi; xuddi shu SI Telegram-botda javob beradi va mahsulot kartasida rasm qidiradi. Misol: «Shu hafta nimani qo'shimcha olish kerak?» deb so'raysiz — sut 4 dona qolganini, kuniga 10 ta ketishini ko'rasiz."),
        () => [T("Владельцу, который решает, что закупить", "Эмнени сатып алууну чечкен ээсине", "Owners deciding what to buy", "Neyin alınacağına karar veren sahiplere", "Nimani sotib olishni hal qiladigan egalarga"),
               T("Магазинам с большим числом товаров", "Товары көп дүкөндөргө", "Shops with many products", "Çok ürünlü mağazalara", "Tovarlari ko'p do'konlarga")]);

        // ── Клиенты, воронка и WhatsApp ──
        d["pack:clients"] = new(() =>
        [
            Step(T("1. Откройте «Клиенты»: покупатели с долгами и скидками в одном списке", "1. «Кардарларды» ачыңыз: карыздары жана арзандатуулары бар сатып алуучулар бир тизмеде", "1. Open “Customers”: buyers with debts and discounts in one list", "1. «Müşteriler»i açın: borçlu ve indirimli alıcılar tek listede", "1. «Mijozlar»ni oching: qarz va chegirmali xaridorlar bitta ro'yxatda"),
                T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar"),
                Row(T("Айбек Сыдыков", "Айбек Сыдыков", "Aibek Sydykov", "Aybek Sıdıkov", "Aybek Sidiqov"), T("Долг 1 200", "Карыз 1 200", "Debt 1,200", "Borç 1.200", "Qarz 1 200"), MockTone.Bad, true, T("Скидка 5 %", "Арзандатуу 5 %", "Discount 5%", "İndirim %5", "Chegirma 5 %")),
                Row(T("Мээрим Токтогулова", "Мээрим Токтогулова", "Meerim Toktogulova", "Meyrim Toktogulova", "Meyrim Toktog'ulova"), T("Долгов нет", "Карыз жок", "No debt", "Borç yok", "Qarz yo'q"), MockTone.Good, false, T("Скидка 10 %", "Арзандатуу 10 %", "Discount 10%", "İndirim %10", "Chegirma 10 %")),
                Row(T("Нурлан Эсенов", "Нурлан Эсенов", "Nurlan Esenov", "Nurlan Esenov", "Nurlan Esenov"), T("Долгов нет", "Карыз жок", "No debt", "Borç yok", "Qarz yo'q"), MockTone.Good)),
            Step(T("2. Откройте карточку клиента — виден долг и скидка", "2. Кардардын карточкасын ачыңыз — карызы жана арзандатуусу көрүнөт", "2. Open the customer card — the debt and discount are visible", "2. Müşteri kartını açın — borç ve indirim görünür", "2. Mijoz kartasini oching — qarz va chegirma ko'rinadi"),
                T("Айбек Сыдыков", "Айбек Сыдыков", "Aibek Sydykov", "Aybek Sıdıkov", "Aybek Sidiqov"),
                Input(T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), "0700 123 456"),
                Row(T("Долг", "Карыз", "Debt", "Borç", "Qarz"), "1 200", MockTone.Bad),
                Row(T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma"), "5 %", MockTone.Good, true)),
            Step(T("3. Воронка продаж: сделки идут по этапам", "3. Сатуу воронкасы: келишимдер этап менен жүрөт", "3. Sales funnel: deals move through stages", "3. Satış hunisi: anlaşmalar aşama aşama ilerler", "3. Savdo voronkasi: kelishuvlar bosqichma-bosqich boradi"),
                T("Воронка продаж", "Сатуу воронкасы", "Sales funnel", "Satış hunisi", "Savdo voronkasi"),
                Chips(1, T("Новые", "Жаңылар", "New", "Yeni", "Yangi"), T("В работе", "Иште", "In progress", "Süreçte", "Jarayonda"), T("Куплено", "Сатылды", "Bought", "Satın alındı", "Sotib olindi")),
                Row(T("Заказ плитки — Мээрим", "Плитка буйрутмасы — Мээрим", "Tile order — Meerim", "Fayans siparişi — Meyrim", "Plitka buyurtmasi — Meyrim"), "24 000", MockTone.Normal, true),
                Row(T("Краска — Нурлан", "Боёк — Нурлан", "Paint — Nurlan", "Boya — Nurlan", "Bo'yoq — Nurlan"), "6 500")),
            Step(T("4. Напишите клиенту в WhatsApp прямо из программы", "4. Кардарга WhatsApp'ка түз эле программадан жазыңыз", "4. Message the customer on WhatsApp right from the app", "4. Müşteriye WhatsApp'tan doğrudan programdan yazın", "4. Mijozga WhatsApp'da to'g'ridan-to'g'ri dasturdan yozing"),
                "WhatsApp",
                Input(T("Сообщение", "Билдирүү", "Message", "Mesaj", "Xabar"), T("Ваш заказ готов, ждём вас", "Буйрутмаңыз даяр, сизди күтөбүз", "Your order is ready, we await you", "Siparişiniz hazır, sizi bekliyoruz", "Buyurtmangiz tayyor, sizni kutamiz"), true),
                Btn(T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"), true)),
        ],
        () =>
        [
            Gain("account-group-outline", T("Все клиенты в одном месте", "Бардык кардарлар бир жерде", "All customers in one place", "Tüm müşteriler tek yerde", "Barcha mijozlar bir joyda"),
                T("База с долгами и скидками: кассир сразу видит, кто перед ним.", "Карыздары жана арзандатуулары бар база: кассир кимдин алдында экенин дароо көрөт.", "A base with debts and discounts: the cashier sees at once who is at the till.", "Borç ve indirimli taban: kasiyer karşısındakini hemen görür.", "Qarz va chegirmali baza: kassir qarshisida kim borligini darhol ko'radi.")),
            Gain("chart-line", T("Воронка продаж", "Сатуу воронкасы", "A sales funnel", "Satış hunisi", "Savdo voronkasi"),
                T("Видно, какие сделки новые, в работе и завершены — ничего не теряется.", "Кайсы келишимдер жаңы, иште жана бүткөнү көрүнөт — эч нерсе жоголбойт.", "See which deals are new, in progress and done — nothing gets lost.", "Hangi anlaşmaların yeni, süreçte ve bitmiş olduğu görünür — hiçbiri kaybolmaz.", "Qaysi kelishuvlar yangi, jarayonda va tugaganligi ko'rinadi — hech narsa yo'qolmaydi.")),
            Gain("message-text-outline", T("WhatsApp из программы", "Программадан WhatsApp", "WhatsApp from the app", "Programdan WhatsApp", "Dasturdan WhatsApp"),
                T("Пишите клиенту, не переключаясь между окнами и телефоном.", "Терезелер менен телефондун ортосунда которулбай, кардарга жазыңыз.", "Message the customer without switching between windows and the phone.", "Pencereler ve telefon arasında geçmeden müşteriye yazın.", "Oynalar va telefon orasida almashmasdan mijozga yozing.")),
        ],
        () => T("База клиентов хранит телефоны, долги и скидки, воронка продаж ведёт сделки по этапам, а клиенту можно написать в WhatsApp прямо из программы. Пример: у Айбека долг 1 200 и скидка 5 % — кассир видит это сразу, а сделку на 24 000 вы переводите из «Новых» в «В работе».",
            "Кардарлардын базасында телефондору, карыздары жана арзандатуулары сакталат, сатуу воронкасы келишимдерди этап менен алып жүрөт, ал эми кардарга WhatsApp'ка түз эле программадан жазса болот. Мисал: Айбектин карызы 1 200, арзандатуусу 5 % — кассир муну дароо көрөт, 24 000 сомдук келишимди «Жаңылардан» «Иште» этабына өткөрөсүз.",
            "The customer base keeps phones, debts and discounts, the sales funnel moves deals through stages, and you can message a customer on WhatsApp right from the program. Example: Aibek owes 1,200 and has a 5% discount — the cashier sees it at once, and you move a 24,000 deal from “New” to “In progress”.",
            "Müşteri tabanı telefonları, borçları ve indirimleri tutar, satış hunisi anlaşmaları aşama aşama yürütür, müşteriye de WhatsApp'tan doğrudan programdan yazılabilir. Örnek: Aybek'in 1.200 borcu ve %5 indirimi var — kasiyer hemen görür, 24.000'lik anlaşmayı «Yeni»den «Süreçte»ye taşırsınız.",
            "Mijozlar bazasi telefonlar, qarzlar va chegirmalarni saqlaydi, savdo voronkasi kelishuvlarni bosqichma-bosqich yuritadi, mijozga esa WhatsApp'da to'g'ridan-to'g'ri dasturdan yozish mumkin. Misol: Aybekning qarzi 1 200, chegirmasi 5 % — kassir buni darhol ko'radi, 24 000 lik kelishuvni «Yangi» dan «Jarayonda» ga o'tkazasiz."),
        () => [T("Магазинам с постоянными покупателями", "Туруктуу сатып алуучулары бар дүкөндөргө", "Shops with regular customers", "Düzenli müşterisi olan mağazalara", "Doimiy xaridorli do'konlarga"),
               T("Тем, кто продаёт в долг", "Карызга сатуучуларга", "Those who sell on credit", "Veresiye satış yapanlara", "Qarzga sotadiganlarga")]);

        // ── Продажи и аналитика ──
        d["pack:salesanalytics"] = new(() =>
        [
            Step(T("1. Откройте «Продажи»: итоги дня и чеки за период", "1. «Сатууларды» ачыңыз: күндүн жыйынтыгы жана мезгилдеги чектер", "1. Open “Sales”: day totals and receipts for the period", "1. «Satışlar»ı açın: gün toplamları ve dönemdeki fişler", "1. «Sotuvlar»ni oching: kun jami va davrdagi cheklar"),
                T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"),
                Chips(0, T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun"), T("Неделя", "Жума", "Week", "Hafta", "Hafta"), T("Месяц", "Ай", "Month", "Ay", "Oy")),
                Row(T("Чеков", "Чектер", "Receipts", "Fişler", "Cheklar"), "42"),
                Row(T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek"), "917"),
                Total(T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), "38 500", true)),
            Step(T("2. ABC-анализ показывает, какие товары приносят основные деньги", "2. ABC-талдоо кайсы товарлар негизги акча алып келерин көрсөтөт", "2. ABC analysis shows which products bring the main money", "2. ABC analizi hangi ürünlerin ana geliri getirdiğini gösterir", "2. ABC-tahlil qaysi mahsulotlar asosiy pul keltirishini ko'rsatadi"),
                T("ABC-анализ", "ABC-талдоо", "ABC analysis", "ABC analizi", "ABC-tahlil"),
                Row(T("Сахар 1 кг", "Кант 1 кг", "Sugar 1 kg", "Şeker 1 kg", "Shakar 1 kg"), "A · 18 %", MockTone.Good, true),
                Row(T("Масло 1 л", "Май 1 л", "Oil 1 l", "Yağ 1 l", "Moy 1 l"), "A · 14 %", MockTone.Good),
                Row(T("Чай 100 г", "Чай 100 г", "Tea 100 g", "Çay 100 g", "Choy 100 g"), "B · 6 %"),
                Row(T("Спички", "Ширеңке", "Matches", "Kibrit", "Gugurt"), "C · 1 %", MockTone.Warn)),
            Step(T("3. «Прибыль и деньги»: сколько заработано и что в кассе", "3. «Пайда жана акча»: канча тапканыңыз жана кассада эмне бар", "3. “Profit and cash”: what you earned and what is in the till", "3. «Kâr ve nakit»: ne kazandığınız ve kasada ne olduğu", "3. «Foyda va pul»: qancha topganingiz va kassada nima borligi"),
                T("Прибыль и деньги", "Пайда жана акча", "Profit and cash", "Kâr ve nakit", "Foyda va pul"),
                Row(T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), "250 000"),
                Row(T("Себестоимость", "Өздүк нарк", "Cost", "Maliyet", "Tannarx"), "170 000"),
                Total(T("Прибыль", "Пайда", "Profit", "Kâr", "Foyda"), "80 000", true)),
            Step(T("4. Продажи в убыток подсвечены, а прогресс плана виден сразу", "4. Зыян менен сатуулар белгиленет, план жүрүшү дароо көрүнөт", "4. Sales at a loss are highlighted and plan progress is visible", "4. Zararına satışlar vurgulanır, plan ilerlemesi hemen görünür", "4. Zarariga sotuvlar ajratiladi, reja borishi darhol ko'rinadi"),
                T("Контроль продаж", "Сатууну көзөмөлдөө", "Sales control", "Satış kontrolü", "Sotuvni nazorat"),
                Note(T("Продано ниже себестоимости: Масло 1 л, убыток 12", "Өздүк нарктан төмөн сатылды: Май 1 л, зыян 12", "Sold below cost: Oil 1 l, loss 12", "Maliyetin altında satıldı: Yağ 1 l, zarar 12", "Tannarxdan past sotildi: Moy 1 l, zarar 12"), MockTone.Bad),
                Row(T("План продаж на месяц", "Айлык сатуу планы", "Monthly sales plan", "Aylık satış planı", "Oylik sotuv rejasi"), "62 %", MockTone.Accent, true, T("155 000 из 250 000", "250 000 ичинен 155 000", "155,000 of 250,000", "250.000'in 155.000'i", "250 000 dan 155 000"))),
        ],
        () =>
        [
            Gain("chart-bar", T("Цифры за минуту", "Сандар бир мүнөттө", "Numbers in a minute", "Rakamlar bir dakikada", "Raqamlar bir daqiqada"),
                T("Выручка, чеки и средний чек по дням, неделям и месяцам.", "Күн, жума жана ай боюнча түшүм, чектер жана орточо чек.", "Revenue, receipts and average receipt by day, week and month.", "Gün, hafta ve ay bazında ciro, fiş ve ortalama fiş.", "Kun, hafta va oy bo'yicha tushum, cheklar va o'rtacha chek.")),
            Gain("chart-box-outline", T("Видно, что кормит магазин", "Дүкөндү эмне багарын көрөсүз", "See what feeds the shop", "Mağazayı neyin beslediğini görün", "Do'konni nima boqishini ko'ring"),
                T("ABC-анализ и прибыль показывают главные товары и реальный заработок.", "ABC-талдоо жана пайда негизги товарларды жана чыныгы кирешени көрсөтөт.", "ABC analysis and profit show the main products and real earnings.", "ABC analizi ve kâr ana ürünleri ve gerçek kazancı gösterir.", "ABC-tahlil va foyda asosiy mahsulotlar va haqiqiy daromadni ko'rsatadi.")),
            Gain("percent", T("Убытки не пропадают", "Зыяндар көрүнбөй калбайт", "Losses do not slip by", "Zararlar gözden kaçmaz", "Zararlar ko'zdan qochmaydi"),
                T("Продажи ниже себестоимости подсвечены, а план продаж показывает прогресс.", "Өздүк нарктан төмөн сатуулар белгиленет, сатуу планы жүрүшүн көрсөтөт.", "Sales below cost are highlighted and the sales plan shows progress.", "Maliyet altı satışlar vurgulanır, satış planı ilerlemeyi gösterir.", "Tannarxdan past sotuvlar ajratiladi, sotuv rejasi borishni ko'rsatadi.")),
        ],
        () => T("Разделы продаж, финансов и аналитики показывают выручку, чеки и средний чек, ABC-анализ товаров, прибыль и деньги, продажи ниже себестоимости и выполнение плана продаж. Пример: выручка за месяц 250 000, себестоимость 170 000 — прибыль 80 000, а масло продано в убыток на 12.",
            "Сатуу, каржы жана аналитика бөлүмдөрү түшүмдү, чектерди жана орточо чекти, товарлардын ABC-талдоосун, пайда менен акчаны, өздүк нарктан төмөн сатууларды жана сатуу планынын аткарылышын көрсөтөт. Мисал: айдагы түшүм 250 000, өздүк нарк 170 000 — пайда 80 000, ал эми май 12 зыян менен сатылган.",
            "The sales, finance and analytics sections show revenue, receipts and average receipt, ABC analysis of products, profit and cash, sales below cost and progress against the sales plan. Example: monthly revenue 250,000 and cost 170,000 give a profit of 80,000, and oil was sold at a loss of 12.",
            "Satış, finans ve analiz bölümleri ciroyu, fişleri ve ortalama fişi, ürünlerin ABC analizini, kâr ve nakdi, maliyet altı satışları ve satış planının ilerlemesini gösterir. Örnek: aylık ciro 250.000, maliyet 170.000 — kâr 80.000, yağ ise 12 zararla satılmış.",
            "Sotuv, moliya va analitika bo'limlari tushum, cheklar va o'rtacha chekni, mahsulotlarning ABC-tahlilini, foyda va pulni, tannarxdan past sotuvlarni va sotuv rejasining bajarilishini ko'rsatadi. Misol: oylik tushum 250 000, tannarx 170 000 — foyda 80 000, moy esa 12 zarar bilan sotilgan."),
        () => [T("Владельцам, которые хотят видеть цифры", "Сандарды көргүсү келген ээлерге", "Owners who want to see the numbers", "Rakamları görmek isteyen sahiplere", "Raqamlarni ko'rmoqchi egalarga"),
               T("Магазинам с планом продаж", "Сатуу планы бар дүкөндөргө", "Shops with a sales plan", "Satış planı olan mağazalara", "Sotuv rejasi bor do'konlarga")]);

        // ── Пополнение и сроки годности ──
        d["pack:restock"] = new(() =>
        [
            Step(T("1. Откройте «Пополнение»: что пора заказать", "1. «Толуктоону» ачыңыз: эмнеге буйрутма берүү керек", "1. Open “Restock”: what is time to order", "1. «Stok yenileme»yi açın: neyin sipariş zamanı geldi", "1. «To'ldirish»ni oching: nimani buyurtma qilish vaqti keldi"),
                T("Пополнение", "Толуктоо", "Restock", "Stok yenileme", "To'ldirish"),
                Row(T("Молоко 1 л", "Сүт 1 л", "Milk 1 l", "Süt 1 l", "Sut 1 l"), T("Заказать 30", "30 буйрутма", "Order 30", "30 sipariş", "30 buyurtma"), MockTone.Warn, true, T("Осталось 4 шт.", "4 даана калды", "4 left", "4 adet kaldı", "4 dona qoldi")),
                Row(T("Хлеб белый", "Ак нан", "White bread", "Beyaz ekmek", "Oq non"), T("Заказать 50", "50 буйрутма", "Order 50", "50 sipariş", "50 buyurtma"), MockTone.Warn, false, T("Осталось 6 шт.", "6 даана калды", "6 left", "6 adet kaldı", "6 dona qoldi")),
                Row(T("Сахар 1 кг", "Кант 1 кг", "Sugar 1 kg", "Şeker 1 kg", "Shakar 1 kg"), T("Хватит", "Жетет", "Enough", "Yeter", "Yetadi"), MockTone.Good)),
            Step(T("2. Отметьте нужные товары — количество подставится само", "2. Керектүү товарларды белгилеңиз — саны өзү коюлат", "2. Tick the products you need — the quantity fills in itself", "2. İhtiyaç duyulan ürünleri işaretleyin — miktar kendiliğinden gelir", "2. Kerakli mahsulotlarni belgilang — miqdor o'zi qo'yiladi"),
                T("Пополнение", "Толуктоо", "Restock", "Stok yenileme", "To'ldirish"),
                Row("✓ " + T("Молоко 1 л", "Сүт 1 л", "Milk 1 l", "Süt 1 l", "Sut 1 l"), "30", MockTone.Accent, true),
                Row("✓ " + T("Хлеб белый", "Ак нан", "White bread", "Beyaz ekmek", "Oq non"), "50", MockTone.Accent, true),
                Btn(T("Сформировать заказ", "Буйрутма түзүү", "Create the order", "Sipariş oluştur", "Buyurtma tuzish"), true)),
            Step(T("3. «Сроки годности» показывают, что скоро испортится", "3. «Жарактуулук мөөнөттөрү» эмне жакында бузуларын көрсөтөт", "3. “Expiry dates” show what will soon go off", "3. «Son kullanma tarihleri» neyin yakında bozulacağını gösterir", "3. «Yaroqlilik muddatlari» nima tez orada buzilishini ko'rsatadi"),
                T("Сроки годности", "Жарактуулук мөөнөттөрү", "Expiry dates", "Son kullanma tarihleri", "Yaroqlilik muddatlari"),
                Row(T("Йогурт 200 г", "Йогурт 200 г", "Yogurt 200 g", "Yoğurt 200 g", "Yogurt 200 g"), T("до 02.12", "02.12 чейин", "until 02.12", "02.12'ye kadar", "02.12 gacha"), MockTone.Warn, true, T("12 шт.", "12 даана", "12 pcs", "12 adet", "12 dona")),
                Row(T("Сыр 300 г", "Быштак 300 г", "Cheese 300 g", "Peynir 300 g", "Pishloq 300 g"), T("просрочен", "мөөнөтү өттү", "expired", "süresi geçti", "muddati o'tgan"), MockTone.Bad, false, T("3 шт.", "3 даана", "3 pcs", "3 adet", "3 dona")),
                Row(T("Кефир 1 л", "Кефир 1 л", "Kefir 1 l", "Kefir 1 l", "Kefir 1 l"), T("до 20.12", "20.12 чейин", "until 20.12", "20.12'ye kadar", "20.12 gacha"), MockTone.Good)),
            Step(T("4. Результат: заказ собран, а скоропорт не залёживается", "4. Натыйжа: буйрутма түзүлдү, тез бузулган товар жатып калбайт", "4. Result: the order is ready and perishables do not sit", "4. Sonuç: sipariş hazır, çabuk bozulanlar bekleyip kalmaz", "4. Natija: buyurtma tayyor, tez buziladiganlar yotib qolmaydi"),
                T("Пополнение", "Толуктоо", "Restock", "Stok yenileme", "To'ldirish"),
                Note(T("Заказ готов: 2 позиции, 80 штук", "Буйрутма даяр: 2 позиция, 80 даана", "Order ready: 2 items, 80 pieces", "Sipariş hazır: 2 kalem, 80 adet", "Buyurtma tayyor: 2 pozitsiya, 80 dona"), MockTone.Good),
                Note(T("Йогурт: скидка поможет продать до 02.12", "Йогурт: арзандатуу 02.12 чейин сатууга жардам берет", "Yogurt: a discount helps sell it by 02.12", "Yoğurt: indirim 02.12'ye kadar satmaya yardım eder", "Yogurt: chegirma 02.12 gacha sotishga yordam beradi"), MockTone.Warn)),
        ],
        () =>
        [
            Gain("history", T("Не уходите в ноль", "Нөлгө чейин калтырбаңыз", "Never run to zero", "Sıfıra düşürmeyin", "Nolga tushirmang"),
                T("Список «пора заказать» собирается по остаткам — полки не пустеют.", "«Буйрутма берүү убактысы» тизмеси калдыктар боюнча түзүлөт — текчелер бош калбайт.", "The “time to order” list is built from stock, so shelves do not run empty.", "«Sipariş zamanı» listesi stoğa göre oluşur — raflar boş kalmaz.", "«Buyurtma vaqti» ro'yxati qoldiqlar bo'yicha tuziladi — javonlar bo'sh qolmaydi.")),
            Gain("calendar-range", T("Видно сроки годности", "Жарактуулук мөөнөтү көрүнөт", "Expiry dates in view", "Son kullanma tarihleri görünür", "Yaroqlilik muddatlari ko'rinadi"),
                T("Товары с близким сроком вынесены в отдельный список.", "Мөөнөтү жакындаган товарлар өзүнчө тизмеге чыгарылган.", "Products near their expiry date are listed separately.", "Son kullanma tarihi yaklaşan ürünler ayrı listede.", "Muddati yaqinlashgan mahsulotlar alohida ro'yxatda.")),
            Gain("cart-arrow-down", T("Заказ без блокнота", "Блокнотсуз буйрутма", "An order without a notebook", "Defter olmadan sipariş", "Daftarsiz buyurtma"),
                T("Отметили нужное — заказ поставщику готов, без ручных записей.", "Керектүүнү белгилеңиз — жеткирүүчүгө буйрутма даяр, кол менен жазбай.", "Tick what you need and the supplier order is ready, no hand notes.", "Gerekeni işaretleyin — tedarikçi siparişi hazır, elle not yok.", "Kerakli narsani belgilang — yetkazib beruvchiga buyurtma tayyor, qo'lda yozmasdan.")),
        ],
        () => T("«Пополнение» собирает список товаров, которые пора заказать, а «Сроки годности» показывает, у каких товаров срок скоро закончится. Пример: молока осталось 4 шт. — программа предлагает заказать 30; йогурт 12 шт. годен до 02.12, а 3 шт. сыра уже просрочены.",
            "«Толуктоо» буйрутма берүү керек товарлардын тизмесин чогултат, ал эми «Жарактуулук мөөнөттөрү» кайсы товарлардын мөөнөтү жакында бүтөрүн көрсөтөт. Мисал: сүт 4 даана калды — программа 30 буйрутма берүүнү сунуштайт; йогурт 12 даана 02.12 чейин жарактуу, ал эми 3 даана быштактын мөөнөтү өттү.",
            "“Restock” collects a list of products that are due for ordering, and “Expiry dates” shows which products are about to expire. Example: only 4 packs of milk are left — the program suggests ordering 30; 12 yogurts are good until 02.12, while 3 cheeses are already expired.",
            "«Stok yenileme» sipariş zamanı gelen ürünlerin listesini toplar, «Son kullanma tarihleri» hangi ürünlerin süresinin dolmak üzere olduğunu gösterir. Örnek: sütten 4 adet kaldı — program 30 sipariş etmeyi önerir; 12 yoğurt 02.12'ye kadar geçerli, 3 peynirin süresi ise geçmiş.",
            "«To'ldirish» buyurtma qilish vaqti kelgan mahsulotlar ro'yxatini yig'adi, «Yaroqlilik muddatlari» esa qaysi mahsulotlarning muddati tugayotganini ko'rsatadi. Misol: sut 4 dona qoldi — dastur 30 ta buyurtma qilishni taklif qiladi; 12 ta yogurt 02.12 gacha yaroqli, 3 ta pishloqning muddati o'tgan."),
        () => [T("Продуктовым магазинам и аптекам", "Азык-түлүк дүкөндөрүнө жана дарыканаларга", "Grocery shops and pharmacies", "Market ve eczanelere", "Oziq-ovqat do'konlari va dorixonalarga"),
               T("Тем, у кого товар быстро заканчивается", "Товары тез бүтүп калгандарга", "Those whose stock runs out fast", "Ürünü çabuk biten işletmelere", "Mahsuloti tez tugaydiganlarga")]);

        // ── Зарплата сотрудников ──
        d["pack:salary"] = new(() =>
        [
            Step(T("1. Откройте «Зарплата» и выберите период", "1. «Эмгек акыны» ачып, мезгилди тандаңыз", "1. Open “Salaries” and choose the period", "1. «Maaşlar»ı açın ve dönemi seçin", "1. «Ish haqi»ni oching va davrni tanlang"),
                T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi"),
                Chips(1, T("Неделя", "Жума", "Week", "Hafta", "Hafta"), T("Месяц", "Ай", "Month", "Ay", "Oy")),
                Row(T("Айжан Касымова", "Айжан Касымова", "Aizhan Kasymova", "Ayjan Kasımova", "Ayjon Qosimova"), "", MockTone.Normal, true, T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir")),
                Row(T("Тимур Алымкулов", "Тимур Алымкулов", "Timur Alymkulov", "Timur Alımkulov", "Timur Alimqulov"), "", MockTone.Normal, false, T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"))),
            Step(T("2. Программа берёт смены сотрудника из журнала смен", "2. Программа кызматкердин сменаларын смена журналынан алат", "2. The program takes the employee's shifts from the shift log", "2. Program çalışanın vardiyalarını vardiya kaydından alır", "2. Dastur xodimning smenalarini smena jurnalidan oladi"),
                T("Айжан Касымова", "Айжан Касымова", "Aizhan Kasymova", "Ayjan Kasımova", "Ayjon Qosimova"),
                Row(T("Смены за месяц", "Айдагы сменалар", "Shifts this month", "Aydaki vardiyalar", "Oydagi smenalar"), "22", MockTone.Normal, true),
                Row(T("Часов отработано", "Иштелген саат", "Hours worked", "Çalışılan saat", "Ishlangan soat"), "176")),
            Step(T("3. И её продажи за тот же период", "3. Жана ошол эле мезгилдеги сатуулары", "3. And her sales for the same period", "3. Ve aynı dönemdeki satışları", "3. Va shu davrdagi sotuvlari"),
                T("Айжан Касымова", "Айжан Касымова", "Aizhan Kasymova", "Ayjan Kasımova", "Ayjon Qosimova"),
                Row(T("Смены за месяц", "Айдагы сменалар", "Shifts this month", "Aydaki vardiyalar", "Oydagi smenalar"), "22"),
                Row(T("Продажи за месяц", "Айдагы сатуулар", "Sales this month", "Aylık satışlar", "Oylik sotuvlar"), "480 000", MockTone.Normal, true),
                Row(T("Чеков пробито", "Чек чыгарылды", "Receipts rung up", "Kesilen fiş", "Chiqarilgan chek"), "615")),
            Step(T("4. Итог: зарплата посчитана по сменам и продажам, без калькулятора", "4. Жыйынтык: эмгек акы сменалар жана сатуулар боюнча эсептелди, калькуляторсуз", "4. Result: the salary is calculated from shifts and sales, no calculator", "4. Sonuç: maaş vardiya ve satışlardan hesaplandı, hesap makinesi yok", "4. Natija: ish haqi smena va sotuvlar bo'yicha hisoblandi, kalkulyatorsiz"),
                T("Зарплата", "Эмгек акы", "Salaries", "Maaşlar", "Ish haqi"),
                Total(T("Айжан Касымова — итого", "Айжан Касымова — жыйынтыгы", "Aizhan Kasymova — total", "Ayjan Kasımova — toplam", "Ayjon Qosimova — jami"), "28 400", true),
                Note(T("Расчёт готов — сверьте и выплатите", "Эсеп даяр — салыштырып, төлөңүз", "The calculation is ready — check and pay", "Hesap hazır — kontrol edip ödeyin", "Hisob tayyor — tekshirib, to'lang"), MockTone.Good)),
        ],
        () =>
        [
            Gain("account-clock-outline", T("Смены считаются сами", "Сменалар өзү эсептелет", "Shifts count themselves", "Vardiyalar kendiliğinden sayılır", "Smenalar o'zi sanaladi"),
                T("Программа знает, кто и сколько смен отработал.", "Программа ким канча смена иштегенин билет.", "The program knows who worked how many shifts.", "Program kimin kaç vardiya çalıştığını bilir.", "Dastur kim nechta smena ishlaganini biladi.")),
            Gain("cash", T("Зарплата по продажам", "Сатуулар боюнча эмгек акы", "Pay by sales", "Satışa göre maaş", "Sotuv bo'yicha ish haqi"),
                T("Продажи сотрудника учитываются при расчёте, а не считаются вручную.", "Кызматкердин сатуулары эсепке алынат, кол менен эсептелбейт.", "The employee's sales go into the calculation, not counted by hand.", "Çalışanın satışları hesaba katılır, elle sayılmaz.", "Xodimning sotuvlari hisobga olinadi, qo'lda sanalmaydi.")),
            Gain("check-circle-outline", T("Меньше споров", "Талаштар азыраак", "Fewer disputes", "Daha az anlaşmazlık", "Kamroq tortishuv"),
                T("Любую цифру можно проверить по сменам и чекам.", "Ар бир санды сменалар жана чектер боюнча текшерүүгө болот.", "Every figure can be checked against shifts and receipts.", "Her rakam vardiya ve fişlerle kontrol edilebilir.", "Har bir raqamni smena va cheklar bo'yicha tekshirish mumkin.")),
        ],
        () => T("Раздел «Зарплата» считает оплату сотрудников по их сменам и продажам за выбранный период — вручную считать не нужно. Пример: кассир Айжан за месяц отработала 22 смены и продала на 480 000 — программа показывает итог к выплате 28 400.",
            "«Эмгек акы» бөлүмү кызматкерлердин төлөмүн тандалган мезгилдеги сменалары жана сатуулары боюнча эсептейт — кол менен эсептөөнүн кереги жок. Мисал: кассир Айжан айга 22 смена иштеп, 480 000 сомдук сатса — программа төлөнө турган жыйынтыкты 28 400 деп көрсөтөт.",
            "The “Salaries” section calculates staff pay from their shifts and sales for the chosen period, so you do not count by hand. Example: cashier Aizhan worked 22 shifts in a month and sold 480,000 — the program shows a total to pay of 28,400.",
            "«Maaşlar» bölümü personelin ödemesini seçilen dönemdeki vardiya ve satışlarına göre hesaplar — elle saymaya gerek yok. Örnek: kasiyer Ayjan ayda 22 vardiya çalışıp 480.000'lik satış yaptı — program ödenecek toplamı 28.400 olarak gösterir.",
            "«Ish haqi» bo'limi xodimlar to'lovini tanlangan davrdagi smena va sotuvlari bo'yicha hisoblaydi — qo'lda sanash shart emas. Misol: kassir Ayjon oyiga 22 smena ishlab, 480 000 lik sotgan bo'lsa — dastur to'lanadigan jamini 28 400 deb ko'rsatadi."),
        () => [T("Магазинам с несколькими кассирами", "Бир нече кассири бар дүкөндөргө", "Shops with several cashiers", "Birkaç kasiyeri olan mağazalara", "Bir nechta kassirli do'konlarga"),
               T("Владельцам, считающим зарплату вручную", "Эмгек акыны кол менен эсептеген ээлерге", "Owners who count pay by hand", "Maaşı elle hesaplayan sahiplere", "Ish haqini qo'lda hisoblaydigan egalarga")]);

        // ── Долги и отложенные чеки ──
        d["pack:debts"] = new(() =>
        [
            Step(T("1. Покупатель в долг — в списке видно, сколько он должен", "1. Сатып алуучу карызга — тизмеде канча карыз экени көрүнөт", "1. A customer on credit — the list shows how much they owe", "1. Veresiye alıcı — listede ne kadar borçlu olduğu görünür", "1. Qarzga olgan xaridor — ro'yxatda qancha qarzi ko'rinadi"),
                T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari"),
                Row(T("Айбек Сыдыков", "Айбек Сыдыков", "Aibek Sydykov", "Aybek Sıdıkov", "Aybek Sidiqov"), T("Долг 1 200", "Карыз 1 200", "Debt 1,200", "Borç 1.200", "Qarz 1 200"), MockTone.Bad, true),
                Row(T("Гульмира Осмонова", "Гульмира Осмонова", "Gulmira Osmonova", "Gülmira Osmonova", "Gulmira Osmonova"), T("Долг 350", "Карыз 350", "Debt 350", "Borç 350", "Qarz 350"), MockTone.Warn)),
            Step(T("2. Клиент пришёл отдать долг — примите оплату прямо на кассе", "2. Кардар карызын төлөөгө келди — төлөмдү кассада эле алыңыз", "2. The customer came to pay — take the payment at the till", "2. Müşteri borcunu ödemeye geldi — ödemeyi kasada alın", "2. Mijoz qarzini to'lashga keldi — to'lovni kassada qabul qiling"),
                T("Оплата долга", "Карызды төлөө", "Debt payment", "Borç ödemesi", "Qarzni to'lash"),
                Input(T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), "500", true),
                Chips(0, T("Наличные", "Накта акча", "Cash", "Nakit", "Naqd"), T("Карта", "Карта", "Card", "Kart", "Karta")),
                Btn(T("Принять оплату", "Төлөмдү алуу", "Accept payment", "Ödemeyi al", "To'lovni qabul qilish"), true)),
            Step(T("3. Долг уменьшился — остаток записан сразу", "3. Карыз азайды — калдык дароо жазылды", "3. The debt went down — the balance is recorded at once", "3. Borç azaldı — kalan hemen kaydedildi", "3. Qarz kamaydi — qoldiq darhol yozildi"),
                T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari"),
                Note(T("Принято 500. Осталось 700", "500 алынды. 700 калды", "500 accepted. 700 left", "500 alındı. 700 kaldı", "500 qabul qilindi. 700 qoldi"), MockTone.Good),
                Row(T("Айбек Сыдыков", "Айбек Сыдыков", "Aibek Sydykov", "Aybek Sıdıkov", "Aybek Sidiqov"), T("Долг 700", "Карыз 700", "Debt 700", "Borç 700", "Qarz 700"), MockTone.Warn, true)),
            Step(T("4. Покупатель ушёл за деньгами — чек можно отложить и вернуться", "4. Сатып алуучу акча үчүн кетти — чекти кийинкиге калтырып, кайтып келсе болот", "4. The customer went for money — park the receipt and come back", "4. Alıcı para almaya gitti — fişi bekletip geri dönebilirsiniz", "4. Xaridor pul uchun ketdi — chekni kechiktirib, qaytib kelish mumkin"),
                T("Отложенные чеки", "Кийинкиге калтырылган чектер", "Parked receipts", "Bekletilen fişler", "Kechiktirilgan cheklar"),
                Row(T("Чек №15 · 3 товара", "№15 чек · 3 товар", "Receipt #15 · 3 items", "Fiş №15 · 3 ürün", "№15 chek · 3 mahsulot"), "840", MockTone.Normal, true),
                Row(T("Чек №14 · 1 товар", "№14 чек · 1 товар", "Receipt #14 · 1 item", "Fiş №14 · 1 ürün", "№14 chek · 1 mahsulot"), "120"),
                Btn(T("Вернуться к чеку", "Чекке кайтуу", "Back to the receipt", "Fişe dön", "Chekka qaytish"), true)),
        ],
        () =>
        [
            Gain("book-account-outline", T("Долги под контролем", "Карыздар көзөмөлдө", "Debts under control", "Borçlar kontrolde", "Qarzlar nazoratda"),
                T("Видно, кто и сколько должен, — без тетрадки и памяти.", "Ким канча карыз экени көрүнөт — дептерсиз жана эстен тайбай.", "You see who owes how much — no notebook, no memory.", "Kimin ne kadar borçlu olduğu görünür — defter ve hafıza gerekmez.", "Kim qancha qarzdorligi ko'rinadi — daftarsiz va esdan emas.")),
            Gain("cash-multiple", T("Оплата долга на кассе", "Карызды кассада төлөө", "Debt paid at the till", "Borç kasada ödenir", "Qarz kassada to'lanadi"),
                T("Клиент платит прямо на кассе, остаток обновляется сам.", "Кардар түз эле кассада төлөйт, калдык өзү жаңыланат.", "The customer pays at the till and the balance updates itself.", "Müşteri doğrudan kasada öder, kalan kendiliğinden güncellenir.", "Mijoz to'g'ridan-to'g'ri kassada to'laydi, qoldiq o'zi yangilanadi.")),
            Gain("clock-outline", T("Очередь не стоит", "Кезек күтпөйт", "No queue standing", "Kuyruk beklemez", "Navbat kutmaydi"),
                T("Отложили чек — обслужили следующего, потом вернулись.", "Чекти калтырып — кийинкисине кызмат кылып, кийин кайтып келдиңиз.", "Park the receipt, serve the next customer, then come back.", "Fişi beklettiniz — sıradakine baktınız, sonra döndünüz.", "Chekni kechiktirdingiz — keyingisiga xizmat qildingiz, keyin qaytdingiz.")),
        ],
        () => T("Клиент, купивший в долг, остаётся в списке долгов, а оплату можно принять прямо на кассе; чек можно отложить и вернуться к нему позже. Пример: долг Айбека 1 200 — он приносит 500, остаётся 700, а недооформленный чек №15 на 840 ждёт в отложенных.",
            "Карызга сатып алган кардар карыздар тизмесинде калат, ал эми төлөмдү түз эле кассада алса болот; чекти кийинкиге калтырып, кийин кайтып келсе болот. Мисал: Айбектин карызы 1 200 — ал 500 алып келет, 700 калат, ал эми аякталбаган №15 чек 840 кийинкиге калтырылгандарда күтөт.",
            "A customer who bought on credit stays in the debt list, you can take the payment right at the till, and a receipt can be parked and resumed later. Example: Aibek owes 1,200 — he brings 500, 700 remains, and the unfinished receipt #15 for 840 waits among the parked ones.",
            "Veresiye alan müşteri borç listesinde kalır, ödeme doğrudan kasada alınabilir; fiş bekletilip sonra devam edilebilir. Örnek: Aybek'in borcu 1.200 — 500 getirir, 700 kalır, tamamlanmamış 840'lık 15 numaralı fiş bekletilenlerde durur.",
            "Qarzga olgan xaridor qarzlar ro'yxatida qoladi, to'lovni to'g'ridan-to'g'ri kassada qabul qilish mumkin; chekni kechiktirib, keyin davom ettirish mumkin. Misol: Aybekning qarzi 1 200 — u 500 olib keladi, 700 qoladi, tugallanmagan 840 lik №15 chek kechiktirilganlarda kutadi."),
        () => [T("Магазинам, которые продают в долг", "Карызга сатуучу дүкөндөргө", "Shops that sell on credit", "Veresiye satış yapan mağazalara", "Qarzga sotadigan do'konlarga"),
               T("Кассам с очередью", "Кезеги бар кассаларга", "Tills with queues", "Kuyruklu kasalara", "Navbatli kassalarga")]);

        // ── NurCRM, база знаний и поддержка ──
        d["pack:service"] = new(() =>
        [
            Step(T("1. Откройте «NurCRM»: сайт работает прямо внутри программы", "1. «NurCRM» ачыңыз: сайт программанын ичинде эле иштейт", "1. Open “NurCRM”: the site works right inside the program", "1. «NurCRM»i açın: site doğrudan programın içinde çalışır", "1. «NurCRM»ni oching: sayt dastur ichida ishlaydi"),
                "NurCRM",
                Btn(T("Открыть NurCRM", "NurCRM ачуу", "Open NurCRM", "NurCRM'i aç", "NurCRM'ni ochish"), true),
                Note(T("Сайт открыт в окне программы, браузер не нужен", "Сайт программанын терезесинде ачык, браузер керек эмес", "The site is open in the program window, no browser needed", "Site program penceresinde açık, tarayıcı gerekmez", "Sayt dastur oynasida ochiq, brauzer kerak emas"), MockTone.Good)),
            Step(T("2. В «Базе знаний» ищите ответ по слову", "2. «Билим базасында» жоопту сөз боюнча издеңиз", "2. In the “Knowledge base” search for an answer by a word", "2. «Bilgi bankası»nda cevabı bir sözcükle arayın", "2. «Bilimlar bazasi»da javobni so'z bo'yicha qidiring"),
                T("База знаний", "Билим базасы", "Knowledge base", "Bilgi bankası", "Bilimlar bazasi"),
                Input(T("Поиск", "Издөө", "Search", "Ara", "Qidiruv"), T("закрыть смену", "сменаны жабуу", "close a shift", "vardiya kapatma", "smenani yopish"), true),
                Row(T("Как закрыть смену", "Сменаны кантип жабуу", "How to close a shift", "Vardiya nasıl kapatılır", "Smenani qanday yopish"), "", MockTone.Normal, true, T("Статья", "Макала", "Article", "Makale", "Maqola"))),
            Step(T("3. Не нашли ответ — позовите техподдержку, она подключится удалённо", "3. Жооп табылбады — тех колдоону чакырыңыз, ал алыстан кошулат", "3. No answer found — call tech support, it connects remotely", "3. Cevap bulunamadı — teknik desteği çağırın, uzaktan bağlanır", "3. Javob topilmadi — texnik yordamni chaqiring, u masofadan ulanadi"),
                T("Тех. поддержка", "Тех. колдоо", "Tech support", "Teknik destek", "Texnik yordam"),
                Btn(T("Позвать поддержку", "Колдоону чакыруу", "Call support", "Desteği çağır", "Yordamni chaqirish"), true),
                Note(T("Специалист подключится к программе удалённо", "Адис программага алыстан кошулат", "A specialist connects to the program remotely", "Uzman programa uzaktan bağlanır", "Mutaxassis dasturga masofadan ulanadi"), MockTone.Good)),
            Step(T("4. «Журнал ошибок» хранит сбои — по нему проще найти причину", "4. «Каталар журналы» ката-кемчиликтерди сактайт — себебин табуу оңой", "4. The “Error log” keeps failures — the cause is easier to find", "4. «Hata günlüğü» arızaları saklar — nedeni bulmak kolaylaşır", "4. «Xatolar jurnali» nosozliklarni saqlaydi — sababini topish osonroq"),
                T("Журнал ошибок", "Каталар журналы", "Error log", "Hata günlüğü", "Xatolar jurnali"),
                Row(T("Принтер не отвечает", "Принтер жооп бербейт", "Printer not responding", "Yazıcı yanıt vermiyor", "Printer javob bermayapti"), "14:32", MockTone.Bad, true),
                Row(T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q"), "11:05", MockTone.Warn)),
        ],
        () =>
        [
            Gain("store-outline", T("NurCRM под рукой", "NurCRM колдо", "NurCRM at hand", "NurCRM elinizin altında", "NurCRM qo'l ostida"),
                T("Сайт NurCRM открывается внутри программы — без браузера и паролей.", "NurCRM сайты программанын ичинде ачылат — браузерсиз жана сырсөздөрсүз.", "The NurCRM site opens inside the program — no browser, no extra logins.", "NurCRM sitesi programın içinde açılır — tarayıcı ve ek giriş yok.", "NurCRM sayti dastur ichida ochiladi — brauzer va qo'shimcha parollarsiz.")),
            Gain("book-account-outline", T("Ответ без звонка", "Чалбай эле жооп", "An answer without a call", "Aramadan cevap", "Qo'ng'iroqsiz javob"),
                T("База знаний отвечает на частые вопросы — найдите нужную статью.", "Билим базасы көп берилүүчү суроолорго жооп берет — керектүү макаланы табыңыз.", "The knowledge base answers common questions — find the right article.", "Bilgi bankası sık sorulan sorulara cevap verir — doğru makaleyi bulun.", "Bilimlar bazasi tez-tez so'raladigan savollarga javob beradi — kerakli maqolani toping.")),
            Gain("lifebuoy", T("Помощь на расстоянии", "Алыстан жардам", "Help from a distance", "Uzaktan yardım", "Masofadan yordam"),
                T("Техподдержка подключается удалённо, а журнал ошибок ускоряет разбор.", "Тех колдоо алыстан кошулат, каталар журналы териштирүүнү тездетет.", "Tech support connects remotely and the error log speeds up diagnosis.", "Teknik destek uzaktan bağlanır, hata günlüğü incelemeyi hızlandırır.", "Texnik yordam masofadan ulanadi, xatolar jurnali tahlilni tezlashtiradi.")),
        ],
        () => T("Сайт NurCRM открывается внутри программы, «База знаний» отвечает на частые вопросы, техподдержка подключается удалённо, а «Журнал ошибок» хранит сбои для разбора. Пример: принтер перестал печатать в 14:32 — вы находите статью, а если не помогло, зовёте специалиста, который видит запись в журнале.",
            "NurCRM сайты программанын ичинде ачылат, «Билим базасы» көп берилүүчү суроолорго жооп берет, тех колдоо алыстан кошулат, ал эми «Каталар журналы» ката-кемчиликтерди териштирүү үчүн сактайт. Мисал: принтер 14:32 басууну токтотту — макаланы табасыз, жардам бербесе, журналдагы жазууну көргөн адисти чакырасыз.",
            "The NurCRM site opens inside the program, the “Knowledge base” answers common questions, tech support connects remotely, and the “Error log” keeps failures for diagnosis. Example: the printer stopped printing at 14:32 — you find an article, and if it does not help, you call a specialist who sees the log entry.",
            "NurCRM sitesi programın içinde açılır, «Bilgi bankası» sık sorulan sorulara cevap verir, teknik destek uzaktan bağlanır, «Hata günlüğü» arızaları inceleme için saklar. Örnek: yazıcı 14:32'de yazdırmayı bıraktı — makaleyi bulursunuz, yardımcı olmazsa günlükteki kaydı gören uzmanı çağırırsınız.",
            "NurCRM sayti dastur ichida ochiladi, «Bilimlar bazasi» tez-tez so'raladigan savollarga javob beradi, texnik yordam masofadan ulanadi, «Xatolar jurnali» nosozliklarni tahlil uchun saqlaydi. Misol: printer 14:32 da chop etishni to'xtatdi — maqolani topasiz, yordam bermasa, jurnaldagi yozuvni ko'radigan mutaxassisni chaqirasiz."),
        () => [T("Тем, кто работает без своего IT-специалиста", "Өз IT адиси жок иштегендерге", "Those without their own IT person", "Kendi BT uzmanı olmayanlara", "O'z IT mutaxassisi yo'qlarga"),
               T("Новым кассирам, которым нужны подсказки", "Кеңеш керек болгон жаңы кассирлерге", "New cashiers who need hints", "İpucu isteyen yeni kasiyerlere", "Maslahat kerak yangi kassirlarga")]);
    }
}
