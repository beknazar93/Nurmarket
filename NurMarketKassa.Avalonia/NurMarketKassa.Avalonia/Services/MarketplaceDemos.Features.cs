using System.Collections.Generic;
using static NurMarketKassa.AvaloniaHost.Services.MockDsl;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26 (просьба владельца: «все новые функции в Маркетплейс — с описанием и с гиф, как работает»): демонстрации работы (3–4 шага «что нажать → что видно») и «Что получите»
// для функций без модуля сервера (MarketplaceFeatureCards): бот-продавец, торг бота, ЭСФ, онлайн-ККМ, рассылки в WhatsApp, экспорт в 1С, «заполним базу»,
// QR магазина, QR на сумму чека, электронный чек, сканер на складе. Правила те же, что у остальных демо: кадр в рамку 640×340, выдуманные имена и суммы, только то, что функция делает по коду.
// Из этих же кадров собираются настоящие GIF-файлы (тест MarketplaceGifExportTests → docs/Отчеты/img-маркетплейс-гиф/<код>.gif).
public static partial class MarketplaceDemos
{
    private static void AddGroupE(Dictionary<string, MarketplaceDemoEntry> d)
    {
        // ── Бот-продавец ──
        d[MarketplaceFeatureCards.BotSeller] = new(() =>
        [
            Step(T("1. Покупатель пишет вашему боту в Telegram", "1. Сатып алуучу Telegram'дагы ботуңузга жазат", "1. A customer writes to your Telegram bot", "1. Müşteri Telegram botunuza yazar", "1. Xaridor Telegram'dagi botingizga yozadi"),
                "Telegram",
                Input(T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor"), T("Есть чайник до 2 000 сом?", "2 000 сомго чейин чайник барбы?", "Do you have a kettle under 2,000 som?", "2 000 somdan ucuz çaydanlık var mı?", "2 000 so'mgacha choynak bormi?"), true)),
            Step(T("2. Бот отвечает по вашему каталогу и предлагает товар", "2. Бот каталогуңуз боюнча жооп берип, товар сунуштайт", "2. The bot answers from your catalog and offers a product", "2. Bot kataloğunuza göre yanıtlar ve ürün önerir", "2. Bot katalogingiz bo'yicha javob berib, tovar taklif qiladi"),
                "Telegram",
                Row(T("Чайник Bosch 1,7 л", "Bosch чайниги 1,7 л", "Bosch kettle 1.7 l", "Bosch çaydanlık 1,7 l", "Bosch choynak 1,7 l"), "1 800", MockTone.Good, true, T("в наличии: 6 шт", "бар: 6 дана", "in stock: 6 pcs", "stokta: 6 adet", "mavjud: 6 dona")),
                Note(T("Бот: «Есть, 1 800 сом. Оформить заказ?»", "Бот: «Бар, 1 800 сом. Заказ тариздейбизби?»", "Bot: “Yes, 1,800 som. Place the order?”", "Bot: «Var, 1 800 som. Sipariş verelim mi?»", "Bot: «Bor, 1 800 so'm. Buyurtma beraymi?»"), MockTone.Accent)),
            Step(T("3. Покупатель согласен — заказ приходит вам готовым", "3. Сатып алуучу макул — заказ сизге даяр келет", "3. The customer agrees — the order reaches you ready", "3. Müşteri kabul eder — sipariş size hazır gelir", "3. Xaridor rozi — buyurtma sizga tayyor keladi"),
                T("Заказы", "Заказдар", "Orders", "Siparişler", "Buyurtmalar"),
                Row(T("Заказ №27 · Telegram-бот", "№27 заказ · Telegram-бот", "Order 27 · Telegram bot", "Sipariş 27 · Telegram botu", "27-buyurtma · Telegram-bot"), "1 800", MockTone.Normal, true, T("Чайник Bosch 1,7 л × 1", "Bosch чайниги 1,7 л × 1", "Bosch kettle 1.7 l × 1", "Bosch çaydanlık 1,7 l × 1", "Bosch choynak 1,7 l × 1")),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "1 800")),
            Step(T("4. Покупатель просит человека — бот зовёт вас", "4. Сатып алуучу адамды суранат — бот сизди чакырат", "4. The customer asks for a human — the bot calls you", "4. Müşteri insan ister — bot sizi çağırır", "4. Xaridor odamni so'raydi — bot sizni chaqiradi"),
                T("Диалоги бота", "Боттун диалогдору", "Bot dialogs", "Bot konuşmaları", "Bot suhbatlari"),
                Row(T("Покупатель просит продавца", "Сатып алуучу сатуучуну суранат", "A customer asks for a seller", "Müşteri satıcı istiyor", "Xaridor sotuvchini so'rayapti"), "", MockTone.Warn, true),
                Btn(T("Взять диалог", "Диалогду алуу", "Take the dialog", "Konuşmayı al", "Suhbatni olish"), true)),
        ],
        () =>
        [
            Gain("robot-outline", T("Отвечает сам", "Өзү жооп берет", "Answers by itself", "Kendisi yanıtlar", "O'zi javob beradi"),
                T("Бот отвечает по вашему каталогу и правилам магазина, в том числе ночью.", "Бот каталогуңуз жана дүкөн эрежелери боюнча жооп берет, түнкүсүн да.", "The bot answers from your catalog and shop rules, at night too.", "Bot kataloğunuza ve mağaza kurallarına göre yanıtlar, geceleri de.", "Bot katalogingiz va do'kon qoidalari bo'yicha javob beradi, tunda ham.")),
            Gain("cart-arrow-down", T("Берёт заказы", "Заказ алат", "Takes orders", "Sipariş alır", "Buyurtma oladi"),
                T("Оформляет заказ прямо в Telegram — он приходит вам готовым.", "Заказды түз Telegram'да тариздейт — ал сизге даяр келет.", "Places the order right in Telegram — it reaches you ready.", "Siparişi doğrudan Telegram'da oluşturur — size hazır gelir.", "Buyurtmani to'g'ridan-to'g'ri Telegram'da rasmiylashtiradi — sizga tayyor keladi.")),
            Gain("account-voice", T("Зовёт вас вовремя", "Сизди өз убагында чакырат", "Calls you in time", "Sizi zamanında çağırır", "Sizni o'z vaqtida chaqiradi"),
                T("Если покупатель просит человека, диалог переходит к вам, а потом возвращается боту.", "Сатып алуучу адамды суранса, диалог сизге өтөт, анан ботко кайтат.", "If the customer asks for a human, the dialog passes to you and then back to the bot.", "Müşteri insan isterse konuşma size geçer, sonra bota döner.", "Xaridor odamni so'rasa, suhbat sizga o'tadi, keyin botga qaytadi.")),
        ]);

        // ── Торг бота ──
        d[MarketplaceFeatureCards.BotBargain] = new(() =>
        [
            Step(T("1. В «Боте-продавце» включите торг и задайте лимит скидки", "1. «Бот-сатуучуда» соодалашууну күйгүзүп, арзандатуу лимитин коюңуз", "1. In the “Seller bot” turn bargaining on and set the discount limit", "1. «Satıcı bot»ta pazarlığı açın ve indirim sınırını belirleyin", "1. «Bot-sotuvchi»da savdolashuvni yoqib, chegirma limitini belgilang"),
                T("Бот-продавец: Торг", "Бот-сатуучу: Соодалашуу", "Seller bot: Bargaining", "Satıcı bot: Pazarlık", "Bot-sotuvchi: Savdolashuv"),
                Toggle(T("Торг включён", "Соодалашуу күйүк", "Bargaining is on", "Pazarlık açık", "Savdolashish yoqilgan"), true, true),
                Input(T("Лимит скидки, %", "Арзандатуу лимити, %", "Discount limit, %", "İndirim sınırı, %", "Chegirma limiti, %"), "10", true),
                Input(T("Не ниже закупки + N %", "Сатып алуудан төмөн эмес + N %", "Not below purchase + N %", "Alıştan aşağı değil + N %", "Xariddan past emas + N %"), "5")),
            Step(T("2. Для товара — своё правило и минимальная цена", "2. Товар үчүн — өз эреже жана минималдуу баа", "2. For a product — its own rule and a minimum price", "2. Ürün için — kendi kuralı ve en düşük fiyat", "2. Tovar uchun — o'z qoidasi va minimal narx"),
                T("Правила торга по товарам", "Товарлар боюнча соода эрежелери", "Bargaining rules by product", "Ürüne göre pazarlık kuralları", "Tovarlar bo'yicha savdolashuv qoidalari"),
                Row(T("Чайник Bosch 1,7 л", "Bosch чайниги 1,7 л", "Bosch kettle 1.7 l", "Bosch çaydanlık 1,7 l", "Bosch choynak 1,7 l"), "1 800", MockTone.Normal, false, T("закупка 1 300 · пол цены 1 620", "сатып алуу 1 300 · баа чеги 1 620", "purchase 1,300 · price floor 1,620", "alış 1 300 · taban fiyat 1 620", "xarid 1 300 · narx chegarasi 1 620")),
                Toggle(T("Торг разрешён", "Соодалашууга уруксат", "Bargaining allowed", "Pazarlığa izin var", "Savdolashishga ruxsat"), true, true),
                Input(T("Минимальная цена", "Минималдуу баа", "Minimum price", "En düşük fiyat", "Minimal narx"), "1 620")),
            Step(T("3. «Проверить торг»: бот уступает, но не ниже пола", "3. «Соодону текшерүү»: бот жеңилдик берет, бирок чектен төмөн эмес", "3. “Check bargaining”: the bot yields, but not below the floor", "3. «Pazarlığı kontrol et»: bot taviz verir ama tabanın altına inmez", "3. «Savdolashuvni tekshirish»: bot yon beradi, lekin chegaradan past emas"),
                T("Проверить торг", "Соодону текшерүү", "Check bargaining", "Pazarlığı kontrol et", "Savdolashuvni tekshirish"),
                Input(T("Предложение покупателя", "Сатып алуучунун сунушу", "Customer's offer", "Müşterinin teklifi", "Xaridor taklifi"), "1 400", true),
                Note(T("Бот: «Могу уступить до 1 700 сом, ниже не получится»", "Бот: «1 700 сомго чейин жеңилдик берем, андан төмөн болбойт»", "Bot: “I can go down to 1,700 som, not lower”", "Bot: «1 700 soma kadar inebilirim, daha aşağı olmaz»", "Bot: «1 700 so'mgacha yon beraman, undan past bo'lmaydi»"), MockTone.Good),
                Row(T("Пол цены (видите только вы)", "Баа чеги (сиз гана көрөсүз)", "Price floor (only you see it)", "Taban fiyat (yalnız siz görürsünüz)", "Narx chegarasi (faqat siz ko'rasiz)"), "1 620", MockTone.Warn)),
            Step(T("4. В заказе — цена по торгу, цена в списке и скидка", "4. Заказда — соода баасы, тизмедеги баа жана арзандатуу", "4. The order shows the bargained price, list price and discount", "4. Siparişte pazarlık fiyatı, liste fiyatı ve indirim görünür", "4. Buyurtmada — kelishilgan narx, ro'yxatdagi narx va chegirma"),
                T("Заказы", "Заказдар", "Orders", "Siparişler", "Buyurtmalar"),
                Row(T("Чайник Bosch 1,7 л", "Bosch чайниги 1,7 л", "Bosch kettle 1.7 l", "Bosch çaydanlık 1,7 l", "Bosch choynak 1,7 l"), "1 700", MockTone.Good, true, T("Цена по торгу · в списке 1 800 · скидка 6 %", "Соода баасы · тизмеде 1 800 · арзандатуу 6 %", "Bargained price · list 1,800 · discount 6 %", "Pazarlık fiyatı · listede 1 800 · indirim %6", "Kelishilgan narx · ro'yxatda 1 800 · chegirma 6 %")),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "1 700")),
        ],
        () =>
        [
            Gain("tag-text-outline", T("Не ниже вашего пола", "Сиздин чектен төмөн эмес", "Never below your floor", "Tabanınızın altına inmez", "Chegarangizdan past emas"),
                T("Закупка и пол цены видны только вам: покупатель и бот их не раскрывают.", "Сатып алуу баасы жана баа чеги сизге гана көрүнөт: сатып алуучу жана бот аларды ачпайт.", "Purchase cost and the floor are visible only to you: the buyer and the bot never reveal them.", "Alış fiyatı ve taban yalnız size görünür: müşteri ve bot bunları açıklamaz.", "Xarid narxi va narx chegarasi faqat sizga ko'rinadi: xaridor va bot ularni ochmaydi.")),
            Gain("eye-check-outline", T("Проверка без покупателя", "Сатып алуучусуз текшерүү", "Test without a customer", "Müşterisiz deneme", "Xaridorsiz tekshirish"),
                T("Кнопка «Проверить торг» разыгрывает диалог, а журнал оферт хранит итоги.", "«Соодону текшерүү» баскычы диалогду ойнойт, оферталар журналы жыйынтыкты сактайт.", "The “Check bargaining” button plays a dialog and the offers journal keeps the results.", "«Pazarlığı kontrol et» düğmesi konuşmayı oynatır, teklif günlüğü sonuçları saklar.", "«Savdolashuvni tekshirish» tugmasi suhbatni o'ynaydi, takliflar jurnali natijalarni saqlaydi.")),
            Gain("receipt-text-outline", T("Цена в заказе", "Заказдагы баа", "Price in the order", "Siparişteki fiyat", "Buyurtmadagi narx"),
                T("В заказе видны цена по торгу, цена в списке и размер скидки.", "Заказда соода баасы, тизмедеги баа жана арзандатуунун өлчөмү көрүнөт.", "The order shows the bargained price, the list price and the discount size.", "Siparişte pazarlık fiyatı, liste fiyatı ve indirim miktarı görünür.", "Buyurtmada kelishilgan narx, ro'yxatdagi narx va chegirma miqdori ko'rinadi.")),
        ]);

        // ── ЭСФ ──
        d[MarketplaceFeatureCards.Esf] = new(() =>
        [
            Step(T("1. При приёме товара впишите номер ЭСФ поставщика", "1. Товар кабыл алганда жеткирүүчүнүн ЭСФ номерин жазыңыз", "1. When receiving goods, enter the supplier's ESF number", "1. Mal kabulünde tedarikçinin ESF numarasını yazın", "1. Tovar qabul qilishda yetkazib beruvchining ESF raqamini yozing"),
                T("Закупки · Приём товара", "Сатып алуулар · Товар кабыл алуу", "Purchases · Receiving goods", "Satın alma · Mal kabul", "Xaridlar · Tovar qabul qilish"),
                Input(T("№ ЭСФ поставщика", "Жеткирүүчүнүн ЭСФ №", "Supplier ESF no.", "Tedarikçi ESF no.", "Yetkazib beruvchi ESF №"), "0123456789", true),
                Input(T("Накладная поставщика", "Жеткирүүчүнүн накладныйы", "Supplier invoice", "Tedarikçi irsaliyesi", "Yetkazib beruvchi nakladnoyi"), "45"),
                Btn(T("Принять товар", "Товарды кабыл алуу", "Receive goods", "Malı kabul et", "Tovarni qabul qilish"), true)),
            Step(T("2. Приход сохранён вместе с номером ЭСФ", "2. Киреше ЭСФ номери менен бирге сакталды", "2. The receipt is saved with the ESF number", "2. Giriş ESF numarasıyla birlikte kaydedildi", "2. Kirim ESF raqami bilan birga saqlandi"),
                T("Закупки · Приходы", "Сатып алуулар · Кирешелер", "Purchases · Receipts", "Satın alma · Girişler", "Xaridlar · Kirimlar"),
                Row(T("Приход ПР-000123 · ИП Асанов", "ПР-000123 киреше · ИП Асанов", "Receipt PR-000123 · Asanov", "Giriş PR-000123 · Asanov", "PR-000123 kirim · Asanov"), "12 400", MockTone.Normal, true, T("ЭСФ №: 0123456789", "ЭСФ №: 0123456789", "ESF no.: 0123456789", "ESF no.: 0123456789", "ESF №: 0123456789")),
                Note(T("Номер сохранён вместе с приходом", "Номер киреше менен бирге сакталды", "The number is saved with the receipt", "Numara girişle birlikte kaydedildi", "Raqam kirim bilan birga saqlandi"), MockTone.Good)),
            Step(T("3. Позже приход находится по номеру или по его концу", "3. Кийин киреше номер же анын аягы боюнча табылат", "3. Later the receipt is found by the number or its end", "3. Sonra giriş numara veya sonuyla bulunur", "3. Keyin kirim raqam yoki uning oxiri bo'yicha topiladi"),
                T("Закупки · Приходы", "Сатып алуулар · Кирешелер", "Purchases · Receipts", "Satın alma · Girişler", "Xaridlar · Kirimlar"),
                Input(T("Поиск", "Издөө", "Search", "Ara", "Qidiruv"), "6789", true),
                Row(T("Приход ПР-000123 · ИП Асанов", "ПР-000123 киреше · ИП Асанов", "Receipt PR-000123 · Asanov", "Giriş PR-000123 · Asanov", "PR-000123 kirim · Asanov"), "12 400", MockTone.Good, true, T("ЭСФ №: 0123456789", "ЭСФ №: 0123456789", "ESF no.: 0123456789", "ESF no.: 0123456789", "ESF №: 0123456789"))),
        ],
        () =>
        [
            Gain("file-check-outline", T("Номер у прихода", "Кирешеде номер", "The number with the receipt", "Girişte numara", "Kirimda raqam"),
                T("Номер ЭСФ поставщика хранится вместе с приходом — сверка с бухгалтерией проще.", "Жеткирүүчүнүн ЭСФ номери киреше менен бирге сакталат — эсепчи менен салыштыруу жеңилирээк.", "The supplier's ESF number is stored with the receipt — matching with accounting is easier.", "Tedarikçinin ESF numarası girişle birlikte saklanır — muhasebeyle eşleştirme kolaylaşır.", "Yetkazib beruvchining ESF raqami kirim bilan birga saqlanadi — buxgalteriya bilan solishtirish osonroq.")),
            Gain("magnify", T("Поиск за секунду", "Бир секундда издөө", "Search in a second", "Bir saniyede arama", "Bir soniyada qidiruv"),
                T("Приход находится по номеру целиком или от трёх знаков — хоть по последним цифрам.", "Киреше номердин бардыгы же үч белгиден баштап табылат — акыркы сандары боюнча да.", "A receipt is found by the whole number or from three characters — even by the last digits.", "Giriş numaranın tamamıyla ya da üç karakterden itibaren bulunur — son rakamlarla da.", "Kirim raqamning to'liq o'zi yoki uch belgidan boshlab topiladi — oxirgi raqamlari bo'yicha ham.")),
            Gain("shield-check-outline", T("Ничего не уходит в ГНС", "ГНСке эч нерсе кетпейт", "Nothing goes to the STS", "GNS'ye bir şey gitmez", "GNSga hech narsa ketmaydi"),
                T("Касса не отправляет ЭСФ налоговой: номер нужен вам для учёта и сверки.", "Касса ЭСФти салык кызматына жөнөтпөйт: номер сизге эсеп жана салыштыруу үчүн керек.", "The till does not send ESF to the tax service: the number is for your records and matching.", "Kasa ESF'yi vergi idaresine göndermez: numara kayıt ve eşleştirme içindir.", "Kassa ESFni soliq xizmatiga yubormaydi: raqam hisob va solishtirish uchun kerak.")),
        ]);

        // ── Онлайн-ККМ ──
        d[MarketplaceFeatureCards.Ekassa] = new(() =>
        [
            Step(T("1. В «Налогах и ККМ» видно режим, состояние ККМ и ставки", "1. «Салыктар жана ККМде» режим, ККМ абалы жана ставкалар көрүнөт", "1. “Taxes and cash register” shows the mode, the register state and rates", "1. «Vergiler ve ÖKC»de mod, ÖKC durumu ve oranlar görünür", "1. «Soliqlar va KKM»da rejim, KKM holati va stavkalar ko'rinadi"),
                T("Настройки · Налоги и ККМ", "Жөндөөлөр · Салыктар жана ККМ", "Settings · Taxes and cash register", "Ayarlar · Vergiler ve ÖKC", "Sozlamalar · Soliqlar va KKM"),
                Row(T("Режим", "Режим", "Mode", "Mod", "Rejim"), T("Чеки пробивает касса", "Чектерди касса бекитет", "The till fiscalizes", "Fişleri kasa keser", "Cheklarni kassa uradi"), MockTone.Good),
                Row(T("ККМ", "ККМ", "Cash register", "ÖKC", "KKM"), T("Смена открыта", "Смена ачык", "Shift is open", "Vardiya açık", "Smena ochiq"), MockTone.Good),
                Row(T("Ставка НДС", "КНС ставкасы", "VAT rate", "KDV oranı", "QQS stavkasi"), "12 %")),
            Step(T("2. Кассир продаёт как обычно и нажимает «Оплатить»", "2. Кассир адаттагыдай сатат жана «Төлөө» баскычын басат", "2. The cashier sells as usual and presses “Pay”", "2. Kasiyer her zamanki gibi satar ve «Öde»ye basar", "2. Kassir odatdagidek sotadi va «To'lash»ni bosadi"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Чайник Bosch 1,7 л", "Bosch чайниги 1,7 л", "Bosch kettle 1.7 l", "Bosch çaydanlık 1,7 l", "Bosch choynak 1,7 l"), "1 800", MockTone.Normal, false, "1 × 1 800"),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "1 800"),
                Btn(T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"), true)),
            Step(T("3. Чек уходит в eKassa и получает фискальный номер", "3. Чек eKassa'га кетип, фискалдык номер алат", "3. The receipt goes to eKassa and gets a fiscal number", "3. Fiş eKassa'ya gider ve mali numara alır", "3. Chek eKassa'ga ketib, fiskal raqam oladi"),
                T("Фискализация", "Фискалдаштыруу", "Fiscalization", "Mali işlem", "Fiskallashtirish"),
                Note(T("Чек передаётся в eKassa…", "Чек eKassa'га берилүүдө…", "The receipt is being sent to eKassa…", "Fiş eKassa'ya gönderiliyor…", "Chek eKassa'ga yuborilmoqda…"), MockTone.Warn),
                Row(T("Фискальный номер", "Фискалдык номер", "Fiscal number", "Mali numara", "Fiskal raqam"), "№ 0042", MockTone.Good, true)),
            Step(T("4. На чеке — фискальный QR для проверки покупателем", "4. Чекте — сатып алуучу текшерүү үчүн фискалдык QR", "4. The receipt carries a fiscal QR for the buyer to verify", "4. Fişte müşterinin doğrulaması için mali QR var", "4. Chekda — xaridor tekshirishi uchun fiskal QR"),
                T("Фискальный чек", "Фискалдык чек", "Fiscal receipt", "Mali fiş", "Fiskal chek"),
                Row(T("Чайник Bosch 1,7 л", "Bosch чайниги 1,7 л", "Bosch kettle 1.7 l", "Bosch çaydanlık 1,7 l", "Bosch choynak 1,7 l"), "1 800"),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "1 800"),
                Note(T("Внизу чека — фискальный QR и номер ККМ", "Чектин ылдый жагында — фискалдык QR жана ККМ номери", "At the bottom — the fiscal QR and the register number", "Fişin altında — mali QR ve ÖKC numarası", "Chek pastida — fiskal QR va KKM raqami"), MockTone.Good)),
        ],
        () =>
        [
            Gain("printer-pos", T("Чек по закону", "Мыйзам боюнча чек", "A receipt by the law", "Yasaya uygun fiş", "Qonun bo'yicha chek"),
                T("Каждый чек получает фискальный номер и QR — как требует налоговая.", "Ар бир чек салык кызматы талап кылгандай фискалдык номер жана QR алат.", "Every receipt gets a fiscal number and QR — as the tax service requires.", "Her fiş vergi idaresinin istediği gibi mali numara ve QR alır.", "Har bir chek soliq xizmati talab qilganidek fiskal raqam va QR oladi.")),
            Gain("sync", T("Касса или сервер", "Касса же сервер", "The till or the server", "Kasa veya sunucu", "Kassa yoki server"),
                T("Режим выбирает NurCRM: пробивает касса или сервер — тогда отдельная ККМ не нужна.", "Режимди NurCRM тандайт: касса же сервер бекитет — анда өзүнчө ККМ керек эмес.", "NurCRM picks the mode: the till or the server fiscalizes — then no separate register is needed.", "Modu NurCRM seçer: kasa veya sunucu keser — o zaman ayrı ÖKC gerekmez.", "Rejimni NurCRM tanlaydi: kassa yoki server uradi — unda alohida KKM kerak emas.")),
            Gain("qrcode", T("Фискальный QR на чеке", "Чекте фискалдык QR", "A fiscal QR on the receipt", "Fişte mali QR", "Chekda fiskal QR"),
                T("Покупатель проверяет чек по QR; ставки НДС и НсП видны в «Налогах и ККМ».", "Сатып алуучу чекти QR менен текшерет; КНС жана СНС ставкалары «Салыктар жана ККМде» көрүнөт.", "The buyer verifies the receipt by QR; VAT and sales-tax rates are in “Taxes and cash register”.", "Müşteri fişi QR ile doğrular; KDV ve satış vergisi oranları «Vergiler ve ÖKC»de görünür.", "Xaridor chekni QR bilan tekshiradi; QQS va savdo solig'i stavkalari «Soliqlar va KKM»da ko'rinadi.")),
        ]);

        // ── Рассылки в WhatsApp ──
        d[MarketplaceFeatureCards.ClientMailing] = new(() =>
        [
            Step(T("1. Выберите, кому писать: сегмент клиентов", "1. Кимге жазарыңызды тандаңыз: кардарлар сегменти", "1. Choose whom to write to: a customer segment", "1. Kime yazacağınızı seçin: müşteri segmenti", "1. Kimga yozishni tanlang: mijozlar segmenti"),
                T("Клиенты · Рассылка", "Кардарлар · Жөнөтмө", "Customers · Mailing", "Müşteriler · Toplu mesaj", "Mijozlar · Xabarnoma"),
                Chips(0, T("Должники", "Карызкорлор", "Debtors", "Borçlular", "Qarzdorlar"), T("Не покупали 30 дн.", "30 күн албагандар", "No purchase 30 d.", "30 gün almayan", "30 kun olmagan"), "VIP", T("День рождения", "Туулган күн", "Birthday", "Doğum günü", "Tug'ilgan kun")),
                Row(T("Получателей", "Алуучулар", "Recipients", "Alıcılar", "Oluvchilar"), "18", MockTone.Good, true)),
            Step(T("2. Напишите текст: имя и сумма долга подставятся сами", "2. Текст жазыңыз: аты жана карыздын суммасы өзү коюлат", "2. Write the text: the name and the debt fill in by themselves", "2. Metni yazın: ad ve borç tutarı kendiliğinden eklenir", "2. Matnni yozing: ism va qarz summasi o'zi qo'yiladi"),
                T("Клиенты · Рассылка", "Кардарлар · Жөнөтмө", "Customers · Mailing", "Müşteriler · Toplu mesaj", "Mijozlar · Xabarnoma"),
                Input(T("Текст", "Текст", "Text", "Metin", "Matn"), T("{имя}, напоминаем о долге {долг} сом", "{имя}, {долг} сом карызыңызды эскертебиз", "{name}, a reminder of your {debt} som debt", "{name}, {debt} som borcunuzu hatırlatırız", "{name}, {debt} so'm qarzingizni eslatamiz"), true),
                Btn(T("Открыть WhatsApp по очереди", "WhatsAppты кезеги менен ачуу", "Open WhatsApp one by one", "WhatsApp'ı sırayla aç", "WhatsApp'ni navbat bilan ochish"), true)),
            Step(T("3. Открывается один чат с готовым текстом — «Отправить» нажимаете вы", "3. Даяр текст менен бир чат ачылат — «Жөнөтүүнү» өзүңүз басасыз", "3. One chat opens with the text ready — you press Send", "3. Hazır metinle tek sohbet açılır — «Gönder»e siz basarsınız", "3. Tayyor matn bilan bitta chat ochiladi — «Yuborish»ni o'zingiz bosasiz"),
                "WhatsApp",
                Note(T("Азамат Т.: Азамат, напоминаем о долге 1 200 сом", "Азамат Т.: Азамат, 1 200 сом карызыңызды эскертебиз", "Azamat T.: Azamat, a reminder of your 1,200 som debt", "Azamat T.: Azamat, 1 200 som borcunuzu hatırlatırız", "Azamat T.: Azamat, 1 200 so'm qarzingizni eslatamiz"), MockTone.Good),
                Row(T("Журнал: чат открыт", "Журнал: чат ачылды", "Journal: chat opened", "Günlük: sohbet açıldı", "Jurnal: chat ochildi"), "1 / 18", MockTone.Normal, true)),
        ],
        () =>
        [
            Gain("account-group-outline", T("Сегменты клиентов", "Кардарлар сегменттери", "Customer segments", "Müşteri segmentleri", "Mijozlar segmentlari"),
                T("Должники, «давно не покупали», VIP, именинники или все с телефоном — в один клик.", "Карызкорлор, «көптөн бери албагандар», VIP, туулган күнү барлар же телефону барлардын баары — бир чыкылдатууда.", "Debtors, “not bought for long”, VIPs, birthdays or everyone with a phone — in one click.", "Borçlular, «uzun süredir almayanlar», VIP, doğum günü olanlar ya da telefonlu herkes — tek tıkla.", "Qarzdorlar, «uzoq xarid qilmaganlar», VIP, tug'ilgan kunlilar yoki telefoni borlarning hammasi — bir bosishda.")),
            Gain("shield-check-outline", T("Защита от спама", "Спамдан коргоо", "Spam protection", "Spam koruması", "Spamdan himoya"),
                T("«Не беспокоить», дневной лимит чатов и тихие часы берегут клиентов и ваш номер.", "«Тынчтандырбоо», күнүмдүк чат лимити жана тынч саат кардарларды жана номериңизди сактайт.", "“Do not disturb”, a daily chat limit and quiet hours protect customers and your number.", "«Rahatsız etme», günlük sohbet sınırı ve sessiz saatler müşterileri ve numaranızı korur.", "«Bezovta qilmang», kunlik chat limiti va sokin soatlar mijozlarni va raqamingizni asraydi.")),
            Gain("history", T("Журнал рассылки", "Жөнөтмө журналы", "Mailing journal", "Toplu mesaj günlüğü", "Xabarnoma jurnali"),
                T("Видно, кому и когда открыт чат; отметку можно снять.", "Кимге жана качан чат ачылганы көрүнөт; белгини алып салууга болот.", "You see to whom and when a chat was opened; the mark can be removed.", "Kime ve ne zaman sohbet açıldığı görünür; işaret kaldırılabilir.", "Kimga va qachon chat ochilgani ko'rinadi; belgini olib tashlash mumkin.")),
        ]);

        // ── Экспорт в 1С ──
        d[MarketplaceFeatureCards.OneCExport] = new(() =>
        [
            Step(T("1. Выберите период и что выгрузить", "1. Мезгилди жана эмнени чыгарууну тандаңыз", "1. Choose the period and what to export", "1. Dönemi ve neyi aktaracağınızı seçin", "1. Davrni va nimani chiqarishni tanlang"),
                T("Экспорт в 1С", "1Ске экспорт", "Export to 1C", "1C'ye aktarım", "1C ga eksport"),
                Input(T("Период", "Мезгил", "Period", "Dönem", "Davr"), "01.10.2026 — 31.10.2026", true),
                Chips(2, T("Номенклатура", "Номенклатура", "Nomenclature", "Ürün listesi", "Nomenklatura"), T("Остатки", "Калдыктар", "Stock", "Stok", "Qoldiqlar"), T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"), T("Приходы", "Кирешелер", "Receipts", "Girişler", "Kirimlar"))),
            Step(T("2. Выберите формат и нажмите «Выгрузить»", "2. Форматты тандап, «Чыгарууну» басыңыз", "2. Choose the format and press “Export”", "2. Biçimi seçin ve «Aktar»a basın", "2. Formatni tanlang va «Chiqarish»ni bosing"),
                T("Экспорт в 1С", "1Ске экспорт", "Export to 1C", "1C'ye aktarım", "1C ga eksport"),
                Chips(0, "Excel", "CSV", "CommerceML"),
                Btn(T("Выгрузить для 1С", "1С үчүн чыгаруу", "Export for 1C", "1C için aktar", "1C uchun chiqarish"), true)),
            Step(T("3. Файл сохранён, суммы совпадают с отчётом", "3. Файл сакталды, суммалар отчёт менен дал келет", "3. The file is saved, the amounts match the report", "3. Dosya kaydedildi, tutarlar raporla eşleşir", "3. Fayl saqlandi, summalar hisobot bilan mos keladi"),
                T("Экспорт в 1С", "1Ске экспорт", "Export to 1C", "1C'ye aktarım", "1C ga eksport"),
                Note(T("Файл сохранён: 1c-sales-2026-10.xlsx", "Файл сакталды: 1c-sales-2026-10.xlsx", "File saved: 1c-sales-2026-10.xlsx", "Dosya kaydedildi: 1c-sales-2026-10.xlsx", "Fayl saqlandi: 1c-sales-2026-10.xlsx"), MockTone.Good),
                Row(T("Продажи в файле", "Файлдагы сатуулар", "Sales in the file", "Dosyadaki satışlar", "Fayldagi sotuvlar"), "412 650", MockTone.Normal, true),
                Row(T("Продажи в отчёте", "Отчёттогу сатуулар", "Sales in the report", "Rapordaki satışlar", "Hisobotdagi sotuvlar"), "412 650", MockTone.Good)),
        ],
        () =>
        [
            Gain("file-excel-outline", T("Excel, CSV, CommerceML", "Excel, CSV, CommerceML", "Excel, CSV, CommerceML", "Excel, CSV, CommerceML", "Excel, CSV, CommerceML"),
                T("Три формата: таблица для 1С, CSV с разделителем «;» и каталог с остатками для обмена.", "Үч формат: 1С үчүн таблица, «;» бөлгүчү бар CSV жана алмашуу үчүн калдыктары бар каталог.", "Three formats: a table for 1C, CSV with a “;” separator and a catalog with stock for exchange.", "Üç biçim: 1C için tablo, «;» ayraçlı CSV ve değişim için stoklu katalog.", "Uch format: 1C uchun jadval, «;» ajratgichli CSV va almashuv uchun qoldiqli katalog.")),
            Gain("check-circle-outline", T("Суммы сходятся", "Суммалар дал келет", "Amounts match", "Tutarlar eşleşir", "Summalar mos keladi"),
                T("Деньги в файле совпадают с отчётами кассы до тыйына.", "Файлдагы акча кассанын отчёттору менен тыйынына чейин дал келет.", "The money in the file matches the till reports to the last tyiyn.", "Dosyadaki para kasa raporlarıyla kuruşu kuruşuna eşleşir.", "Fayldagi pul kassa hisobotlari bilan tiyinigacha mos keladi.")),
            Gain("calendar-range", T("За любой период", "Каалаган мезгилге", "For any period", "Her dönem için", "Istalgan davr uchun"),
                T("Выбираете даты — выгрузка собирается за них: месяц, квартал или год.", "Күндөрдү тандайсыз — чыгаруу алар үчүн жыйналат: ай, чейрек же жыл.", "You pick the dates — the export is built for them: a month, a quarter or a year.", "Tarihleri seçersiniz — aktarım onlar için hazırlanır: ay, çeyrek veya yıl.", "Sanalarni tanlaysiz — chiqarish ular uchun yig'iladi: oy, chorak yoki yil.")),
        ]);

        // ── «Загрузим вашу базу под ключ» ──
        d[MarketplaceFeatureCards.FillBase] = new(() =>
        [
            Step(T("1. Расскажите, что у вас есть: Excel, фото накладных или 1С", "1. Эмнеңиз бар экенин айтыңыз: Excel, накладный сүрөттөрү же 1С", "1. Tell what you have: Excel, invoice photos or 1C", "1. Elinizde ne olduğunu yazın: Excel, fatura fotoğrafları veya 1C", "1. Sizda nima borligini yozing: Excel, hisob-faktura suratlari yoki 1C"),
                T("Загрузим вашу базу", "Базаңызды жүктөйбүз", "We load your catalog", "Ürün tabanınızı yükleriz", "Bazangizni yuklaymiz"),
                Input(T("Магазин", "Дүкөн", "Shop", "Mağaza", "Do'kon"), T("Мой магазин", "Менин дүкөнүм", "My shop", "Mağazam", "Mening do'konim")),
                Input(T("Что есть", "Эмне бар", "What you have", "Elinizde ne var", "Nima bor"), "Excel · 1С", true),
                Input(T("Ссылка на файлы", "Файлдарга шилтеме", "Link to files", "Dosya bağlantısı", "Fayllarga havola"), "drive.google.com/…")),
            Step(T("2. Нажмите кнопку — откроется WhatsApp поддержки с готовым текстом", "2. Баскычты басыңыз — даяр текст менен колдоонун WhatsApp'ы ачылат", "2. Press the button — support WhatsApp opens with the text ready", "2. Düğmeye basın — destek WhatsApp'ı hazır metinle açılır", "2. Tugmani bosing — tayyor matn bilan qo'llab-quvvatlash WhatsApp'i ochiladi"),
                T("Загрузим вашу базу", "Базаңызды жүктөйбүз", "We load your catalog", "Ürün tabanınızı yükleriz", "Bazangizni yuklaymiz"),
                Btn(T("Открыть WhatsApp поддержки", "Колдоонун WhatsApp'ын ачуу", "Open support WhatsApp", "Destek WhatsApp'ını aç", "Qo'llab-quvvatlash WhatsApp'ini ochish"), true),
                Note(T("«Отправить» нажимаете вы — автоотправки нет", "«Жөнөтүүнү» өзүңүз басасыз — автожөнөтүү жок", "You press Send — nothing is sent automatically", "«Gönder»e siz basarsınız — otomatik gönderim yok", "«Yuborish»ni o'zingiz bosasiz — avtomatik yuborish yo'q"))),
            Step(T("3. Поддержка называет цену и срок, затем вводит товары", "3. Колдоо баасын жана мөөнөтүн айтып, анан товарларды киргизет", "3. Support names the price and the time, then enters the products", "3. Destek fiyatı ve süreyi söyler, sonra ürünleri girer", "3. Qo'llab-quvvatlash narx va muddatni aytadi, keyin tovarlarni kiritadi"),
                T("Товары", "Товарлар", "Products", "Ürünler", "Tovarlar"),
                Row(T("Товаров в вашем Excel", "Excel'иңиздеги товарлар", "Products in your Excel", "Excel'inizdeki ürünler", "Excel'ingizdagi tovarlar"), "1 500"),
                Row(T("Загружено в кассу", "Кассага жүктөлдү", "Loaded into the till", "Kasaya yüklendi", "Kassaga yuklandi"), "1 500", MockTone.Good, true)),
        ],
        () =>
        [
            Gain("database-arrow-right-outline", T("Перенос под ключ", "Ачкыч менен көчүрүү", "Turnkey transfer", "Anahtar teslim aktarım", "Kalit topshiriq ko'chirish"),
                T("Вы присылаете файлы — поддержка переносит и вводит товары за вас.", "Сиз файлдарды жөнөтөсүз — колдоо товарларды сиздин ордуңузга көчүрүп киргизет.", "You send the files — support moves and enters the products for you.", "Dosyaları siz gönderirsiniz — destek ürünleri sizin yerinize taşıyıp girer.", "Siz fayllarni yuborasiz — qo'llab-quvvatlash tovarlarni siz uchun ko'chirib kiritadi.")),
            Gain("image-outline", T("Excel, фото и 1С", "Excel, сүрөт жана 1С", "Excel, photos and 1C", "Excel, fotoğraf ve 1C", "Excel, surat va 1C"),
                T("Принимаем таблицы, фото накладных и выгрузки из 1С — что есть у магазина.", "Таблицаларды, накладный сүрөттөрүн жана 1Сттен чыгарууларды кабыл алабыз — дүкөндө эмне бар.", "We accept tables, invoice photos and 1C exports — whatever the shop has.", "Tabloları, fatura fotoğraflarını ve 1C çıktılarını kabul ederiz — mağazada ne varsa.", "Jadvallar, hisob-faktura suratlari va 1C dan chiqarilgan fayllarni qabul qilamiz — do'konda nima bo'lsa.")),
            Gain("lifebuoy", T("Цену называет поддержка", "Баасын колдоо айтат", "Support names the price", "Fiyatı destek söyler", "Narxni qo'llab-quvvatlash aytadi"),
                T("Сумма и срок — до начала работы, без сюрпризов; отправляете сообщение вы сами.", "Сумма жана мөөнөт — иш башталганга чейин, күтүүсүз жагдайсыз; билдирүүнү өзүңүз жөнөтөсүз.", "The sum and the time come before the work, no surprises; you send the message yourself.", "Tutar ve süre iş başlamadan önce, sürprizsiz; mesajı kendiniz gönderirsiniz.", "Summa va muddat — ish boshlanishidan oldin, kutilmagan holatlarsiz; xabarni o'zingiz yuborasiz.")),
        ]);

        // ── QR магазина на чеке и ценнике ──
        d[MarketplaceFeatureCards.ShopQr] = new(() =>
        [
            Step(T("1. В «Печати» включите «QR витрины»", "1. «Басып чыгарууда» «Витрина QR» күйгүзүңүз", "1. In “Printing” turn on “Storefront QR”", "1. «Yazdırma»da «Vitrin QR'ı»nı açın", "1. «Chop etish»da «Vitrina QR»ni yoqing"),
                T("Настройки · Печать", "Жөндөөлөр · Басып чыгаруу", "Settings · Printing", "Ayarlar · Yazdırma", "Sozlamalar · Chop etish"),
                Toggle(T("QR со ссылкой на электронный чек", "Электрондук чекке шилтемеси бар QR", "QR with a link to the e-receipt", "E-fiş bağlantılı QR", "Elektron chek havolali QR"), true),
                Toggle(T("QR витрины «Заказать онлайн»", "Витрина QR «Онлайн заказ»", "Storefront QR “Order online”", "Vitrin QR'ı «Çevrimiçi sipariş»", "Vitrina QR «Onlayn buyurtma»"), true, true)),
            Step(T("2. На чеке появляется второй QR с подписью «Заказать онлайн»", "2. Чекте «Онлайн заказ» деген жазуусу бар экинчи QR пайда болот", "2. A second QR captioned “Order online” appears on the receipt", "2. Fişte «Çevrimiçi sipariş» yazılı ikinci QR çıkar", "2. Chekda «Onlayn buyurtma» yozuvli ikkinchi QR paydo bo'ladi"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Чайник Bosch 1,7 л", "Bosch чайниги 1,7 л", "Bosch kettle 1.7 l", "Bosch çaydanlık 1,7 l", "Bosch choynak 1,7 l"), "1 250"),
                Note(T("Внизу чека два QR: электронный чек и «Заказать онлайн»", "Чектин ылдый жагында эки QR: электрондук чек жана «Онлайн заказ»", "Two QRs at the bottom: the e-receipt and “Order online”", "Fişin altında iki QR: e-fiş ve «Çevrimiçi sipariş»", "Chek pastida ikkita QR: elektron chek va «Onlayn buyurtma»"), MockTone.Good)),
            Step(T("3. Такой же QR можно добавить на ценник в редакторе", "3. Ушундай QRду редактордо баа белгисине кошууга болот", "3. The same QR can be added to a price tag in the editor", "3. Aynı QR düzenleyicide fiyat etiketine eklenebilir", "3. Xuddi shu QRni muharrirda narx belgisiga qo'shish mumkin"),
                T("Ценники и штрихкоды", "Баа белгилери жана штрихкоддор", "Price tags and barcodes", "Etiketler ve barkodlar", "Narx belgilari va shtrixkodlar"),
                Row(T("Ценник · Чайник Bosch", "Баа белгиси · Bosch чайниги", "Price tag · Bosch kettle", "Etiket · Bosch çaydanlık", "Narx belgisi · Bosch choynak"), "1 800"),
                Row(T("QR магазина", "Дүкөндүн QRу", "Shop QR", "Mağaza QR'ı", "Do'kon QRi"), "market.nurcrm.kg/catalog/…", MockTone.Good, true)),
        ],
        () =>
        [
            Gain("qrcode", T("Заказ прямо с чека", "Заказ түздөн-түз чектен", "Order straight from the receipt", "Doğrudan fişten sipariş", "To'g'ridan-to'g'ri chekdan buyurtma"),
                T("Покупатель наводит камеру и попадает на витрину вашего магазина.", "Сатып алуучу камераны багыттап, дүкөнүңүздүн витринасына түшөт.", "The customer points the camera and lands on your shop storefront.", "Müşteri kamerayı tutar ve mağazanızın vitrinine girer.", "Xaridor kamerani yo'naltirib, do'koningiz vitrinasiga tushadi.")),
            Gain("tag-multiple-outline", T("На ценнике тоже", "Баа белгисинде да", "On the price tag too", "Etikette de", "Narx belgisida ham"),
                T("Тот же QR ставится на ценник в редакторе ценников.", "Ошол эле QR баа белгилеринин редакторунда баа белгисине коюлат.", "The same QR is placed on the tag in the price-tag editor.", "Aynı QR etiket düzenleyicide etikete konur.", "Xuddi shu QR narx belgilari muharririda narx belgisiga qo'yiladi.")),
            Gain("eye-outline", T("Нет витрины — нет QR", "Витрина жок — QR жок", "No storefront — no QR", "Vitrin yok — QR yok", "Vitrina yo'q — QR yo'q"),
                T("Пока нет адреса витрины, QR не печатается: пустого квадрата на чеке не будет.", "Витринанын дареги жок болсо, QR басылбайт: чекте бош чарчы болбойт.", "While there is no storefront address, no QR is printed: no empty square on the receipt.", "Vitrin adresi yokken QR basılmaz: fişte boş kare olmaz.", "Vitrina manzili bo'lmaganda QR chop etilmaydi: chekda bo'sh kvadrat bo'lmaydi.")),
        ]);

        // ── QR на сумму чека ──
        d[MarketplaceFeatureCards.PaymentQr] = new(() =>
        [
            Step(T("1. В «Операциях» загрузите QR банка и включите QR с суммой", "1. «Операцияларда» банктын QRун жүктөп, суммасы бар QRду күйгүзүңүз", "1. In “Operations” upload the bank QR and turn on the amount QR", "1. «İşlemler»de banka QR'ını yükleyin ve tutarlı QR'ı açın", "1. «Operatsiyalar»da bank QRini yuklang va summali QRni yoqing"),
                T("Настройки · Операции", "Жөндөөлөр · Операциялар", "Settings · Operations", "Ayarlar · İşlemler", "Sozlamalar · Operatsiyalar"),
                Toggle(T("Показывать покупателю QR с суммой чека", "Сатып алуучуга чектин суммасы бар QRду көрсөтүү", "Show the customer a QR with the receipt amount", "Müşteriye fiş tutarlı QR göster", "Xaridorga chek summasi bilan QRni ko'rsatish"), true, true),
                Note(T("Самопроверка: QR вашего банка подходит", "Өзүн-өзү текшерүү: банкыңыздын QRу ылайык", "Self-check: your bank's QR fits", "Kendi kendine kontrol: bankanızın QR'ı uygun", "O'z-o'zini tekshirish: bankingiz QRi mos"), MockTone.Good)),
            Step(T("2. На экране покупателя — QR, в который уже вписана сумма", "2. Сатып алуучунун экранында — суммасы жазылган QR", "2. The customer screen shows a QR with the amount already filled in", "2. Müşteri ekranında tutarı yazılı QR görünür", "2. Xaridor ekranida — summasi yozilgan QR"),
                T("Экран покупателя", "Сатып алуучунун экраны", "Customer screen", "Müşteri ekranı", "Xaridor ekrani"),
                Total(T("К оплате", "Төлөнүүчү сумма", "To pay", "Ödenecek", "To'lanadi"), "1 250", true),
                Note(T("QR с суммой 1 250 — наведите камеру банка", "1 250 суммасы бар QR — банктын камерасын багыттаңыз", "QR with the amount 1,250 — point the bank app camera", "1 250 tutarlı QR — banka uygulamasının kamerasını tutun", "1 250 summali QR — bank ilovasi kamerasini yo'naltiring"))),
            Step(T("3. Покупатель сканирует — сумма уже в приложении банка", "3. Сатып алуучу сканерлейт — сумма банк тиркемесинде даяр", "3. The customer scans — the amount is already in the bank app", "3. Müşteri tarar — tutar banka uygulamasında hazır", "3. Xaridor skanerlaydi — summa bank ilovasida tayyor"),
                T("Приложение банка", "Банктын тиркемеси", "Bank app", "Banka uygulaması", "Bank ilovasi"),
                Row(T("Перевод в магазин", "Дүкөнгө которуу", "Transfer to the shop", "Mağazaya transfer", "Do'konga o'tkazma"), "1 250", MockTone.Good, true),
                Note(T("Сумму вводить не нужно", "Сумманы киргизүүнүн кереги жок", "No need to type the amount", "Tutarı yazmaya gerek yok", "Summani kiritish shart emas"), MockTone.Good)),
        ],
        () =>
        [
            Gain("qrcode-scan", T("Сумма уже в QR", "Сумма QRда даяр", "The amount is in the QR", "Tutar QR'da hazır", "Summa QRda tayyor"),
                T("Покупателю не нужно набирать сумму: приложение банка подставит её само.", "Сатып алуучуга сумманы терүүнүн кереги жок: банк тиркемеси аны өзү коёт.", "The customer need not type the amount: the bank app fills it in.", "Müşterinin tutarı yazması gerekmez: banka uygulaması kendisi ekler.", "Xaridor summani terishi shart emas: bank ilovasi uni o'zi qo'yadi.")),
            Gain("shield-check-outline", T("Самопроверка QR", "QRду өзүн-өзү текшерүү", "QR self-check", "QR kendi kendine kontrol", "QRni o'z-o'zini tekshirish"),
                T("Если ваш банк не подходит, касса покажет обычный QR — оплата не сломается.", "Банкыңыз ылайык болбосо, касса кадимки QRду көрсөтөт — төлөм бузулбайт.", "If your bank does not fit, the till shows the plain QR — payment does not break.", "Bankanız uymazsa kasa sıradan QR gösterir — ödeme bozulmaz.", "Bankingiz mos kelmasa, kassa oddiy QRni ko'rsatadi — to'lov buzilmaydi.")),
            Gain("clock-outline", T("Быстрее на кассе", "Кассада тезирээк", "Faster at the till", "Kasada daha hızlı", "Kassada tezroq"),
                T("Меньше ошибок и ожидания: покупатель платит за секунды.", "Ката жана күтүү азыраак: сатып алуучу бир нече секундда төлөйт.", "Fewer mistakes and less waiting: the customer pays in seconds.", "Daha az hata ve bekleme: müşteri saniyeler içinde öder.", "Xato va kutish kamroq: xaridor soniyalarda to'laydi.")),
        ]);

        // ── Красивый электронный чек по QR ──
        d[MarketplaceFeatureCards.ReceiptLink] = new(() =>
        [
            Step(T("1. В «Печати» переключатель «QR со ссылкой на электронный чек» включён", "1. «Басып чыгарууда» «Электрондук чекке шилтемеси бар QR» которгучу күйүк", "1. In “Printing” the “QR with a link to the e-receipt” switch is on", "1. «Yazdırma»da «E-fiş bağlantılı QR» anahtarı açık", "1. «Chop etish»da «Elektron chek havolali QR» o'tkazgichi yoqilgan"),
                T("Настройки · Печать", "Жөндөөлөр · Басып чыгаруу", "Settings · Printing", "Ayarlar · Yazdırma", "Sozlamalar · Chop etish"),
                Toggle(T("QR со ссылкой на электронный чек", "Электрондук чекке шилтемеси бар QR", "QR with a link to the e-receipt", "E-fiş bağlantılı QR", "Elektron chek havolali QR"), true, true)),
            Step(T("2. Покупатель оплатил — на бумажном чеке печатается QR", "2. Сатып алуучу төлөдү — кагаз чекте QR басылат", "2. The customer paid — a QR is printed on the paper receipt", "2. Müşteri ödedi — kağıt fişe QR basılır", "2. Xaridor to'ladi — qog'oz chekda QR chop etiladi"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Чайник Bosch 1,7 л", "Bosch чайниги 1,7 л", "Bosch kettle 1.7 l", "Bosch çaydanlık 1,7 l", "Bosch choynak 1,7 l"), "1 250"),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "1 250"),
                Note(T("Внизу чека — QR со ссылкой на электронный чек", "Чектин ылдый жагында — электрондук чекке шилтемеси бар QR", "At the bottom — a QR with a link to the e-receipt", "Fişin altında — e-fiş bağlantılı QR", "Chek pastida — elektron chek havolali QR"), MockTone.Accent)),
            Step(T("3. Покупатель наводит камеру — на телефоне красивая страница чека", "3. Сатып алуучу камераны багыттайт — телефондо чектин кооз барагы", "3. The customer points the camera — a nice receipt page opens on the phone", "3. Müşteri kamerayı tutar — telefonda güzel fiş sayfası açılır", "3. Xaridor kamerani yo'naltiradi — telefonda chekning chiroyli sahifasi"),
                T("Телефон покупателя", "Сатып алуучунун телефону", "The buyer's phone", "Müşterinin telefonu", "Xaridorning telefoni"),
                Row(T("Чек № 0042 · Мой магазин", "№ 0042 чек · Менин дүкөнүм", "Receipt 0042 · My shop", "Fiş 0042 · Mağazam", "0042-chek · Mening do'konim"), "1 250", MockTone.Good, true),
                Row(T("Чайник Bosch 1,7 л · 1 шт", "Bosch чайниги 1,7 л · 1 дана", "Bosch kettle 1.7 l · 1 pc", "Bosch çaydanlık 1,7 l · 1 adet", "Bosch choynak 1,7 l · 1 dona"), "1 250")),
        ],
        () =>
        [
            Gain("qrcode", T("Чек в телефоне", "Чек телефондо", "The receipt in the phone", "Fiş telefonda", "Chek telefonda"),
                T("Покупатель наводит камеру и сохраняет чек у себя — бумагу можно не брать.", "Сатып алуучу камераны багыттап, чекти өзүндө сактайт — кагазды албаса да болот.", "The customer points the camera and keeps the receipt — no paper needed.", "Müşteri kamerayı tutar ve fişi kendinde saklar — kağıda gerek yok.", "Xaridor kamerani yo'naltirib, chekni o'zida saqlaydi — qog'ozni olmasa ham bo'ladi.")),
            Gain("receipt-text-outline", T("Красиво, а не текстом", "Текст эмес, кооз", "Neat, not plain text", "Düz metin değil, güzel", "Matn emas, chiroyli"),
                T("Вместо кусочка текста открывается страница: товары, суммы и магазин.", "Текст үзүндүсүнүн ордуна барак ачылат: товарлар, суммалар жана дүкөн.", "Instead of a scrap of text a page opens: items, amounts and the shop.", "Bir parça metin yerine sayfa açılır: ürünler, tutarlar ve mağaza.", "Matn parchasi o'rniga sahifa ochiladi: tovarlar, summalar va do'kon.")),
            Gain("history", T("Кнопка «QR» в истории", "Тарыхтагы «QR» баскычы", "The “QR” button in history", "Geçmişteki «QR» düğmesi", "Tarixdagi «QR» tugmasi"),
                T("Чек не напечатан — откройте продажу в истории и покажите QR покупателю.", "Чек басылган эмес — тарыхтан сатууну ачып, QRду сатып алуучуга көрсөтүңүз.", "No printed receipt — open the sale in the history and show the QR to the customer.", "Fiş basılmadıysa — geçmişten satışı açın ve QR'ı müşteriye gösterin.", "Chek chop etilmagan — tarixdan sotuvni ochib, QRni xaridorga ko'rsating.")),
        ]);

        // ── Сканер на складе (мобильное приложение) ──
        d[MarketplaceFeatureCards.StockScan] = new(() =>
        [
            Step(T("1. На экране «Склад» нажмите «Сканер штрихкода»", "1. «Кампа» экранында «Штрихкод сканери» баскычын басыңыз", "1. On the “Stock” screen press “Barcode scanner”", "1. «Depo» ekranında «Barkod tarayıcı»ya basın", "1. «Ombor» ekranida «Shtrixkod skaneri»ni bosing"),
                T("Склад", "Кампа", "Stock", "Depo", "Ombor"),
                Input(T("Поиск товара", "Товар издөө", "Product search", "Ürün ara", "Tovar qidirish"), T("Название или штрихкод", "Аталышы же штрихкод", "Name or barcode", "Ad veya barkod", "Nomi yoki shtrixkod")),
                Btn(T("Сканер штрихкода", "Штрихкод сканери", "Barcode scanner", "Barkod tarayıcı", "Shtrixkod skaneri"), true)),
            Step(T("2. Наведите камеру на штрихкод, QR или этикетку весов", "2. Камераны штрихкодго, QRго же тараза этикеткасына багыттаңыз", "2. Point the camera at a barcode, QR or scale label", "2. Kamerayı barkoda, QR'a veya terazi etiketine tutun", "2. Kamerani shtrixkod, QR yoki tarozi yorlig'iga yo'naltiring"),
                T("Камера", "Камера", "Camera", "Kamera", "Kamera"),
                Note(T("Код прочитан: 4600000012345", "Код окулду: 4600000012345", "Code read: 4600000012345", "Kod okundu: 4600000012345", "Kod o'qildi: 4600000012345"), MockTone.Good)),
            Step(T("3. Товар найден — как в продаже, с остатком", "3. Товар табылды — сатуудагыдай, калдыгы менен", "3. The product is found — as in a sale, with its stock", "3. Ürün bulundu — satıştaki gibi, stoğuyla", "3. Tovar topildi — sotuvdagidek, qoldig'i bilan"),
                T("Склад", "Кампа", "Stock", "Depo", "Ombor"),
                Row(T("Чайник Bosch 1,7 л", "Bosch чайниги 1,7 л", "Bosch kettle 1.7 l", "Bosch çaydanlık 1,7 l", "Bosch choynak 1,7 l"), T("24 шт", "24 дана", "24 pcs", "24 adet", "24 dona"), MockTone.Good, true, "4600000012345"),
                Row(T("Цена", "Баасы", "Price", "Fiyat", "Narxi"), "1 800")),
        ],
        () =>
        [
            Gain("barcode-scan", T("Любой код", "Каалаган код", "Any code", "Her kod", "Istalgan kod"),
                T("Читает штрихкод товара, упаковки и размера, QR и этикетку весов.", "Товардын, таңгактын жана өлчөмдүн штрихкодун, QR жана тараза этикеткасын окуйт.", "Reads a product, pack and size barcode, a QR and a scale label.", "Ürün, paket ve beden barkodunu, QR'ı ve terazi etiketini okur.", "Tovar, qadoq va o'lcham shtrixkodini, QR va tarozi yorlig'ini o'qiydi.")),
            Gain("magnify", T("Поиск как в продаже", "Сатуудагыдай издөө", "Search as in a sale", "Satıştaki gibi arama", "Sotuvdagidek qidiruv"),
                T("Не нашли у себя — код остаётся в поиске, и приложение спросит сервер.", "Өзүңүздө табылбаса — код издөөдө калат, тиркеме серверден сурайт.", "Not found locally — the code stays in the search and the app asks the server.", "Kendinizde bulunmazsa kod aramada kalır ve uygulama sunucuya sorar.", "O'zingizda topilmasa — kod qidiruvda qoladi va ilova serverdan so'raydi.")),
            Gain("clock-outline", T("Быстрее, чем вводить", "Киргизгенден тезирээк", "Faster than typing", "Yazmaktan hızlı", "Yozishdan tezroq"),
                T("Остаток у полки — за секунду, без ручного ввода названия.", "Текче жанындагы калдык — бир секундда, аталышты кол менен киргизбей.", "The stock at the shelf in a second, without typing the name.", "Raf başındaki stok bir saniyede, adı elle yazmadan.", "Javon yonidagi qoldiq — bir soniyada, nomini qo'lda kiritmasdan.")),
        ]);

        // ── Конструктор акций (2026-11-27: модуль promotions; пример из ТЗ — вода 100 × 3 = 300 → 200, третья бесплатно) ──
        d[MarketplaceFeatureCards.Promotions] = new(() =>
        [
            Step(T("1. В программе владельца создайте акцию «2+1»", "1. Ээсинин программасында «2+1» акциясын түзүңүз", "1. Create a “2+1” promotion in the owner's program", "1. Sahibin programında «2+1» kampanyası oluşturun", "1. Egasining dasturida «2+1» aksiyasini yarating"),
                T("Акции · Новая акция", "Акциялар · Жаңы акция", "Promotions · New promotion", "Kampanyalar · Yeni kampanya", "Aksiyalar · Yangi aksiya"),
                Input(T("Тип", "Түрү", "Type", "Tür", "Turi"), T("Купи 2 — третий бесплатно", "2ни ал — үчүнчүсү бекер", "Buy 2 — the third is free", "2 al — üçüncüsü bedava", "2 ta ol — uchinchisi bepul"), true),
                Input(T("Товары", "Товарлар", "Products", "Ürünler", "Tovarlar"), T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l")),
                Input(T("Когда", "Качан", "When", "Ne zaman", "Qachon"), T("Будни 9:00–12:00", "Иш күндөрү 9:00–12:00", "Weekdays 9:00–12:00", "Hafta içi 9:00–12:00", "Ish kunlari 9:00–12:00"))),
            Step(T("2. Две бутылки в чеке — касса подсказывает покупателю", "2. Чекте эки бөтөлкө — касса сатып алуучуга айтат", "2. Two bottles in the receipt — the till gives a hint", "2. Fişte iki şişe — kasa ipucu verir", "2. Chekda ikki shisha — kassa maslahat beradi"),
                T("Касса · Чек", "Касса · Чек", "Till · Receipt", "Kasa · Fiş", "Kassa · Chek"),
                Row(T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l"), "200", MockTone.Normal, false, "100 × 2"),
                Note(T("Добавьте ещё 1 — будет бесплатно", "Дагы 1 кошуңуз — бекер болот", "Add 1 more — it will be free", "1 tane daha ekleyin — bedava olacak", "Yana 1 ta qo'shing — bepul bo'ladi"), MockTone.Accent)),
            Step(T("3. Третья бутылка — бесплатно: 300 → 200 сом", "3. Үчүнчү бөтөлкө — бекер: 300 → 200 сом", "3. The third bottle is free: 300 → 200 som", "3. Üçüncü şişe bedava: 300 → 200 som", "3. Uchinchi shisha bepul: 300 → 200 so'm"),
                T("Касса · Чек", "Касса · Чек", "Till · Receipt", "Kasa · Fiş", "Kassa · Chek"),
                Row(T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l"), "300", MockTone.Normal, false, "100 × 3"),
                Row(T("Акция «2+1»", "«2+1» акциясы", "“2+1” promotion", "«2+1» kampanyası", "«2+1» aksiyasi"), "−100", MockTone.Good, true, T("третья бесплатно", "үчүнчүсү бекер", "the third is free", "üçüncüsü bedava", "uchinchisi bepul")),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "200", true)),
            Step(T("4. Нет интернета — касса считает сама и потом сверяет с сервером", "4. Интернет жок — касса өзү эсептейт, анан сервер менен салыштырат", "4. No internet — the till calculates itself and checks with the server later", "4. İnternet yok — kasa kendisi hesaplar, sonra sunucuyla karşılaştırır", "4. Internet yo'q — kassa o'zi hisoblaydi, keyin server bilan solishtiradi"),
                T("Касса", "Касса", "Till", "Kasa", "Kassa"),
                Note(T("Нет сети — акция всё равно работает", "Тармак жок — акция баары бир иштейт", "Offline — the promotion still works", "Bağlantı yok — kampanya yine de çalışır", "Tarmoq yo'q — aksiya baribir ishlaydi"), MockTone.Warn),
                Row(T("Итог сверен с сервером", "Жыйынтык сервер менен салыштырылды", "Total checked with the server", "Toplam sunucuyla karşılaştırıldı", "Yakun server bilan solishtirildi"), "200", MockTone.Good, true)),
        ],
        () =>
        [
            Gain("ticket-percent-outline", T("Любые акции", "Каалаган акциялар", "Any promotion", "Her türlü kampanya", "Istalgan aksiya"),
                T("2+1, скидка % или суммой, цена по акции — на товары, категории и бренды.", "2+1, % же сумма менен арзандатуу, акциялык баа — товарларга, категорияларга жана бренддерге.", "2+1, % or amount off, a promo price — for products, categories and brands.", "2+1, % veya tutar indirimi, kampanya fiyatı — ürünlere, kategorilere ve markalara.", "2+1, % yoki summa bilan chegirma, aksiya narxi — tovarlar, kategoriyalar va brendlarga.")),
            Gain("wifi-off", T("Работает без интернета", "Интернетсиз иштейт", "Works offline", "İnternetsiz çalışır", "Internetsiz ishlaydi"),
                T("Касса получает акции заранее и считает сама; итог сверяется с сервером.", "Касса акцияларды алдын ала алып, өзү эсептейт; жыйынтык сервер менен салыштырылат.", "The till gets promotions in advance and calculates itself; the total is checked with the server.", "Kasa kampanyaları önceden alır ve kendisi hesaplar; toplam sunucuyla karşılaştırılır.", "Kassa aksiyalarni oldindan olib, o'zi hisoblaydi; yakun server bilan solishtiriladi.")),
            Gain("chart-bar", T("Отчёт по акциям", "Акциялар боюнча отчёт", "Promotions report", "Kampanya raporu", "Aksiyalar hisoboti"),
                T("Сколько чеков, штук, скидки и выручки дала каждая акция.", "Ар бир акция канча чек, даана, арзандатуу жана түшкөн акча бергени.", "How many receipts, units, discount and revenue each promotion brought.", "Her kampanyanın getirdiği fiş, adet, indirim ve ciro.", "Har bir aksiya qancha chek, dona, chegirma va tushum bergani.")),
        ]);
    }
}
