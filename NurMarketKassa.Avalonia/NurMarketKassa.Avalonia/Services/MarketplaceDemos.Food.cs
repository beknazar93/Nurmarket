using System.Collections.Generic;
using static NurMarketKassa.AvaloniaHost.Services.MockDsl;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26: демонстрации работы и «Что получите» — функции продуктов, хозтоваров и строймага. Правила: 3–5 шагов; подпись шага «что нажать / что видно / результат» на 5 языках (T);
// кадр помещается в рамку 640×340 (≈5 строк или полей; тест MarketplaceDemosTests измеряет высоту на всех языках); выдуманные имена и суммы, без реальных данных;
// содержание — только то, что функция делает по тексту её карточки (MarketplaceModuleContent) и коду.
public static partial class MarketplaceDemos
{
    private static string FoodTill() => T("Касса", "Касса", "Till", "Kasa", "Kassa");

    private static string FoodTotal() => T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami");

    private static string FoodSom() => T("сом", "сом", "som", "som", "so'm");

    private static string FoodKg() => T("кг", "кг", "kg", "kg", "kg");

    private static string FoodCard() => T("Карточка товара", "Товардын карточкасы", "Product card", "Ürün kartı", "Tovar kartasi");

    private static string FoodLaminate() => T("Ламинат 33 кл.", "Ламинат 33 кл.", "Laminate 33 cl.", "Laminat 33 sın.", "Laminat 33 sin.");

    private static void AddGroupA(Dictionary<string, MarketplaceDemoEntry> d)
    {
        // ── Весы с этикетками ──
        d["scales_labels"] = new(() =>
        [
            Step(T("1. Отправьте товары с кодом PLU на весы", "1. PLU коду бар товарларды таразыга жөнөтүңүз", "1. Send the products with a PLU code to the scale", "1. PLU kodlu ürünleri tartıya gönderin", "1. PLU kodli tovarlarni tarozilarga yuboring"),
                T("Весы", "Таразылар", "Scales", "Teraziler", "Tarozilar"),
                Row(T("Сыр «Голландский»", "«Голландиялык» быштак", "Dutch cheese", "Hollanda peyniri", "«Gollandiya» pishlog'i"), "PLU 12"),
                Row(T("Колбаса докторская", "Докторская колбаса", "Doctor's sausage", "Doktor sucuğu", "Doktorskaya kolbasa"), "PLU 15"),
                Btn(T("Отправить на весы", "Таразыга жөнөтүү", "Send to the scale", "Tartıya gönder", "Tarozilarga yuborish"), true)),
            Step(T("2. Взвесили сыр — весы печатают этикетку со штрихкодом", "2. Быштакты таразаладык — таразы штрихкоду бар этикетка басат", "2. The cheese is weighed — the scale prints a barcode label", "2. Peynir tartıldı — terazi barkodlu etiket basar", "2. Pishloq tortildi — tarozi shtrixkodli etiketka chop etadi"),
                T("Весы", "Таразылар", "Scales", "Teraziler", "Tarozilar"),
                Row(T("Сыр «Голландский»", "«Голландиялык» быштак", "Dutch cheese", "Hollanda peyniri", "«Gollandiya» pishlog'i"), "0,420 " + FoodKg(), MockTone.Normal, false, "PLU 12"),
                Row(T("Цена за кг", "1 кг баасы", "Price per kg", "Kg fiyatı", "1 kg narxi"), "620"),
                Total(T("Сумма на этикетке", "Этикеткадагы сумма", "Amount on the label", "Etiketteki tutar", "Etiketkadagi summa"), "260,40", true)),
            Step(T("3. В кассе отсканируйте этикетку", "3. Кассада этикетканы сканерлеңиз", "3. At the till, scan the label", "3. Kasada etiketi okutun", "3. Kassada etiketkani skanerlang"),
                FoodTill(),
                Input(T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrixkod"), "2012004026040", true),
                Note(T("Касса читает код товара и вес прямо из штрихкода", "Касса товардын кодун жана салмакты штрихкоддон түз окуйт", "The till reads the product code and weight right from the barcode", "Kasa ürün kodunu ve ağırlığı doğrudan barkoddan okur", "Kassa tovar kodi va vaznni to'g'ridan-to'g'ri shtrixkoddan o'qiydi"))),
            Step(T("4. Товар и вес уже в чеке — вводить ничего не нужно", "4. Товар жана салмак чекте — эч нерсе киргизбейсиз", "4. The product and weight are in the receipt — nothing to type", "4. Ürün ve ağırlık fişte — bir şey yazmanız gerekmez", "4. Tovar va vazn chekda — hech narsa kiritish shart emas"),
                FoodTill(),
                Row(T("Сыр «Голландский»", "«Голландиялык» быштак", "Dutch cheese", "Hollanda peyniri", "«Gollandiya» pishlog'i"), "260,40", MockTone.Normal, true, "0,420 " + FoodKg() + " × 620"),
                Total(FoodTotal(), "260,40")),
        ],
        () =>
        [
            Gain("scale-balance", T("Весы и касса вместе", "Таразы жана касса бирге", "Scale and till together", "Terazi ve kasa birlikte", "Tarozi va kassa birga"),
                T("Товары с PLU уходят на весы, а этикетка с весов читается в кассе.", "PLU товарлар таразыга кетет, таразынын этикеткасы кассада окулат.", "Products with PLU go to the scale and its label is read at the till.", "PLU'lu ürünler tartıya gider, terazi etiketi kasada okunur.", "PLU tovarlar tarozilarga ketadi, tarozi etiketkasi kassada o'qiladi.")),
            Gain("barcode-scan", T("Без ввода веса", "Салмак киргизүүсүз", "No typing the weight", "Ağırlık yazmadan", "Vaznni kiritmasdan"),
                T("Касса сама берёт товар и вес из штрихкода — кассир ничего не набирает.", "Касса товарды жана салмакты штрихкоддон өзү алат — кассир эч нерсе терпейт.", "The till takes the product and weight from the barcode — the cashier types nothing.", "Kasa ürünü ve ağırlığı barkoddan alır — kasiyer bir şey yazmaz.", "Kassa tovar va vaznni shtrixkoddan oladi — kassir hech narsa kiritmaydi.")),
            Gain("printer-outline", T("Популярные модели весов", "Белгилүү таразы моделдери", "Popular scale models", "Yaygın terazi modelleri", "Mashhur tarozi modellari"),
                T("Работает со Штрих, Rongta, CAS, Масса-К и TM-30F.", "Штрих, Rongta, CAS, Масса-К жана TM-30F менен иштейт.", "Works with Shtrikh, Rongta, CAS, Massa-K and TM-30F.", "Shtrikh, Rongta, CAS, Massa-K ve TM-30F ile çalışır.", "Shtrix, Rongta, CAS, Massa-K va TM-30F bilan ishlaydi.")),
        ]);

        // ── Сроки годности и партии ──
        d["expiry_batches"] = new(() =>
        [
            Step(T("1. При приёмке укажите срок годности и партию", "1. Кабыл алууда жарактуулук мөөнөтүн жана партияны көрсөтүңүз", "1. When receiving goods, enter the expiry date and batch", "1. Mal kabulünde son kullanma tarihini ve partiyi girin", "1. Qabul qilishda yaroqlilik muddati va partiyani kiriting"),
                T("Приёмка", "Кабыл алуу", "Receiving", "Mal kabul", "Qabul qilish"),
                Row(T("Йогурт клубничный", "Кулпунай йогурт", "Strawberry yogurt", "Çilekli yoğurt", "Qulupnayli yogurt"), "24 " + T("шт", "даана", "pcs", "adet", "dona")),
                Input(T("Срок годности", "Жарактуулук мөөнөтү", "Expiry date", "Son kullanma tarihi", "Yaroqlilik muddati"), "25.11.2026", true),
                Input(T("Партия", "Партия", "Batch", "Parti", "Partiya"), "П-0412")),
            Step(T("2. В окне «Партии» у товара видны остатки по срокам", "2. Товардагы «Партиялар» терезесинде мөөнөттөр боюнча калдыктар көрүнөт", "2. The product's “Batches” window shows stock by expiry date", "2. Ürünün «Partiler» penceresinde süreye göre stok görünür", "2. Tovarning «Partiyalar» oynasida muddatlar bo'yicha qoldiq ko'rinadi"),
                T("Партии: йогурт", "Партиялар: йогурт", "Batches: yogurt", "Partiler: yoğurt", "Partiyalar: yogurt"),
                Row("П-0398 · " + T("до", "чейин", "until", "şu tarihe kadar", "gacha") + " 18.11", "6 " + T("шт", "даана", "pcs", "adet", "dona"), MockTone.Bad, true),
                Row("П-0412 · " + T("до", "чейин", "until", "şu tarihe kadar", "gacha") + " 25.11", "24 " + T("шт", "даана", "pcs", "adet", "dona"), MockTone.Warn),
                Row("П-0420 · " + T("до", "чейин", "until", "şu tarihe kadar", "gacha") + " 09.12", "30 " + T("шт", "даана", "pcs", "adet", "dona"), MockTone.Good)),
            Step(T("3. Просрочено — касса предупреждает до продажи", "3. Мөөнөтү өттү — касса сатуудан мурун эскертет", "3. Expired — the till warns before the sale", "3. Süresi geçmiş — kasa satıştan önce uyarır", "3. Muddati o'tgan — kassa sotishdan oldin ogohlantiradi"),
                FoodTill(),
                Row(T("Йогурт клубничный", "Кулпунай йогурт", "Strawberry yogurt", "Çilekli yoğurt", "Qulupnayli yogurt"), "85", MockTone.Normal, false, T("Партия П-0398", "П-0398 партиясы", "Batch П-0398", "Parti П-0398", "П-0398 partiyasi")),
                Note(T("Срок истёк 18.11. Продать всё равно?", "Мөөнөтү 18.11 өттү. Баары бир сатабызбы?", "Expired on 18.11. Sell anyway?", "Süresi 18.11'de doldu. Yine de satılsın mı?", "Muddati 18.11 da tugagan. Baribir sotamizmi?"), MockTone.Bad),
                Btn(T("Продать всё равно", "Баары бир сатуу", "Sell anyway", "Yine de sat", "Baribir sotish"), true, false)),
            Step(T("4. Свежая партия продаётся без вопросов", "4. Жаңы партия суроосуз сатылат", "4. A fresh batch sells without questions", "4. Taze parti sorusuz satılır", "4. Yangi partiya savolsiz sotiladi"),
                FoodTill(),
                Row(T("Йогурт клубничный", "Кулпунай йогурт", "Strawberry yogurt", "Çilekli yoğurt", "Qulupnayli yogurt"), "85", MockTone.Normal, true, T("Партия П-0420 · до 09.12", "П-0420 партиясы · 09.12 чейин", "Batch П-0420 · until 09.12", "Parti П-0420 · 09.12'ye kadar", "П-0420 partiyasi · 09.12 gacha")),
                Note(T("Срок в порядке — товар добавлен в чек", "Мөөнөтү жайында — товар чекке кошулду", "The date is fine — the item is added to the receipt", "Tarih uygun — ürün fişe eklendi", "Muddat joyida — tovar chekka qo'shildi"), MockTone.Good),
                Total(FoodTotal(), "85")),
        ],
        () =>
        [
            Gain("calendar-range", T("Помнит срок каждого прихода", "Ар бир келген мөөнөттү эстейт", "Remembers each delivery's date", "Her girişin tarihini hatırlar", "Har bir kirim muddatini eslaydi"),
                T("Срок годности и партия записываются при приёмке и видны у товара.", "Жарактуулук мөөнөтү жана партия кабыл алууда жазылат, товардан көрүнөт.", "Expiry date and batch are saved on receiving and shown on the product.", "Son kullanma tarihi ve parti mal kabulde kaydedilir, üründe görünür.", "Yaroqlilik muddati va partiya qabul qilishda yoziladi, tovarda ko'rinadi.")),
            Gain("eye-check-outline", T("Предупреждение о просрочке", "Мөөнөтү өткөнү тууралуу эскертүү", "Warning about expired goods", "Süresi geçenler için uyarı", "Muddati o'tganlar haqida ogohlantirish"),
                T("Просроченный товар не продадут по ошибке: касса спросит «Продать всё равно?».", "Мөөнөтү өткөн товар жаңылыштык менен сатылбайт: касса «Баары бир сатабызбы?» деп сурайт.", "Expired goods are not sold by mistake: the till asks “Sell anyway?”.", "Süresi geçen ürün yanlışlıkla satılmaz: kasa «Yine de satılsın mı?» diye sorar.", "Muddati o'tgan tovar xato sotilmaydi: kassa «Baribir sotamizmi?» deb so'raydi.")),
            Gain("history", T("Остатки по партиям", "Партиялар боюнча калдык", "Stock by batch", "Partilere göre stok", "Partiyalar bo'yicha qoldiq"),
                T("В окне «Партии» видно, что и до какой даты лежит на складе.", "«Партиялар» терезесинде эмне жана кайсы датага чейин кампада жатканы көрүнөт.", "The “Batches” window shows what is in stock and until when.", "«Partiler» penceresinde neyin ne zamana kadar depoda olduğu görünür.", "«Partiyalar» oynasida nima va qaysi sanagacha omborda turgani ko'rinadi.")),
        ]);

        // ── Быстрые товары ──
        d["quick_items"] = new(() =>
        [
            Step(T("1. Чек пуст — вверху крупные кнопки ходовых товаров", "1. Чек бош — жогорку жагында эң көп сатылган товарлардын чоң баскычтары", "1. The receipt is empty — big buttons of best sellers on top", "1. Fiş boş — üstte çok satan ürünlerin büyük düğmeleri", "1. Chek bo'sh — tepada ko'p sotiladigan tovarlarning katta tugmalari"),
                FoodTill(),
                Tiles(-1, T("Хлеб", "Нан", "Bread", "Ekmek", "Non"), T("Пакет", "Пакет", "Bag", "Poşet", "Paket"), T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l"), T("Яйца", "Жумуртка", "Eggs", "Yumurta", "Tuxum")),
                Note(T("Чек пуст — добавьте товар одной кнопкой", "Чек бош — товарды бир баскыч менен кошуңуз", "The receipt is empty — add an item with one button", "Fiş boş — ürünü tek düğmeyle ekleyin", "Chek bo'sh — tovarni bitta tugma bilan qo'shing"))),
            Step(T("2. Нажали «Хлеб» — товар сразу в чеке", "2. «Нан» баскычын бастыңыз — товар чекте", "2. Tap “Bread” — the item is in the receipt at once", "2. «Ekmek»e dokundunuz — ürün hemen fişte", "2. «Non» ni bosdingiz — tovar darhol chekda"),
                FoodTill(),
                Tiles(0, T("Хлеб", "Нан", "Bread", "Ekmek", "Non"), T("Пакет", "Пакет", "Bag", "Poşet", "Paket"), T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l"), T("Яйца", "Жумуртка", "Eggs", "Yumurta", "Tuxum")),
                Row(T("Хлеб", "Нан", "Bread", "Ekmek", "Non"), "35", MockTone.Normal, true, "1 × 35")),
            Step(T("3. Ещё касания — вода и пакет, без поиска и штрихкода", "3. Дагы тийүү — суу жана пакет, издөөсүз жана штрихкодсуз", "3. More taps — water and a bag, no search or barcode", "3. Birkaç dokunuş daha — su ve poşet, arama ve barkod yok", "3. Yana bosish — suv va paket, qidirish va shtrixkodsiz"),
                FoodTill(),
                Tiles(2, T("Хлеб", "Нан", "Bread", "Ekmek", "Non"), T("Пакет", "Пакет", "Bag", "Poşet", "Paket"), T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l"), T("Яйца", "Жумуртка", "Eggs", "Yumurta", "Tuxum")),
                Row(T("Хлеб", "Нан", "Bread", "Ekmek", "Non"), "35"),
                Row(T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l"), "45", MockTone.Normal, true)),
            Step(T("4. Чек готов за несколько секунд", "4. Чек бир нече секундда даяр", "4. The receipt is ready in a few seconds", "4. Fiş birkaç saniyede hazır", "4. Chek bir necha soniyada tayyor"),
                FoodTill(),
                Row(T("Хлеб", "Нан", "Bread", "Ekmek", "Non"), "35"),
                Row(T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l"), "45"),
                Row(T("Пакет", "Пакет", "Bag", "Poşet", "Paket"), "5"),
                Total(FoodTotal(), "85", true)),
        ],
        () =>
        [
            Gain("flash-outline", T("Товар одним касанием", "Товар бир тийүү менен", "An item in one tap", "Ürün tek dokunuşla", "Tovar bitta bosishda"),
                T("Ходовые товары — крупными кнопками на пустом чеке.", "Эң көп сатылган товарлар — бош чекте чоң баскычтар менен.", "Best sellers as big buttons on an empty receipt.", "Çok satan ürünler boş fişte büyük düğmelerle.", "Ko'p sotiladigan tovarlar bo'sh chekda katta tugmalar bilan.")),
            Gain("gesture-tap", T("Без поиска и штрихкода", "Издөөсүз жана штрихкодсуз", "No search or barcode", "Arama ve barkod yok", "Qidirish va shtrixkodsiz"),
                T("Хлеб, воду и пакет пробивают прямо с экрана — быстрее очередь.", "Нанды, сууну жана пакетти түз экрандан тандайт — кезек тезирээк өтөт.", "Bread, water and bags are rung up right from the screen — a faster queue.", "Ekmek, su ve poşet doğrudan ekrandan okutulur — kuyruk hızlanır.", "Non, suv va paket to'g'ridan-to'g'ri ekrandan tanlanadi — navbat tezlashadi.")),
            Gain("eye-outline", T("Блок можно скрыть", "Блокту жашырууга болот", "The block can be hidden", "Blok gizlenebilir", "Blokni yashirish mumkin"),
                T("Блок можно выключить в настройках экрана, если он не нужен.", "Керек болбосо, блокту экран жөндөөлөрүндө өчүрүүгө болот.", "If you do not need the block, switch it off in the screen settings.", "Gerekmiyorsa blok ekran ayarlarında kapatılabilir.", "Keraksiz bo'lsa, blokni ekran sozlamalarida o'chirish mumkin.")),
        ]);

        // ── Выход мяса при приходе ──
        d["carcass_yield"] = new(() =>
        [
            Step(T("1. В приёмке у весового товара нажмите «Выход мяса»", "1. Кабыл алууда салмактуу товарда «Эттин чыгымы» баскычын басыңыз", "1. In receiving, press “Meat yield” on a weighed item", "1. Mal kabulünde tartılı üründe «Et verimi»ne basın", "1. Qabul qilishda tarozida o'lchanadigan tovarda «Go'sht chiqishi» ni bosing"),
                T("Приёмка", "Кабыл алуу", "Receiving", "Mal kabul", "Qabul qilish"),
                Row(T("Говядина (туша)", "Уй эти (союш)", "Beef (carcass)", "Sığır eti (karkas)", "Mol go'shti (tusha)"), FoodKg()),
                Btn(T("Выход мяса", "Эттин чыгымы", "Meat yield", "Et verimi", "Go'sht chiqishi"), true)),
            Step(T("2. Впишите, сколько купили «с костью», и выход в %", "2. «Сөөгү менен» канча сатып алганыңызды жана чыгымды % менен жазыңыз", "2. Enter how much you bought “with bone” and the yield in %", "2. «Kemikli» ne kadar aldığınızı ve verimi % olarak yazın", "2. «Suyagi bilan» qancha sotib olganingizni va chiqishni % da yozing"),
                T("Выход мяса", "Эттин чыгымы", "Meat yield", "Et verimi", "Go'sht chiqishi"),
                Input(T("Куплено, кг", "Сатып алынды, кг", "Bought, kg", "Alınan, kg", "Sotib olindi, kg"), "300"),
                Input(T("Выход, %", "Чыгым, %", "Yield, %", "Verim, %", "Chiqish, %"), "75", true),
                Input(T("Цена закупки за кг", "Сатып алуу баасы, 1 кг", "Purchase price per kg", "Kg alış fiyatı", "1 kg xarid narxi"), "400")),
            Step(T("3. В строке сразу видно, сколько пойдёт в продажу", "3. Саптан сатууга канча кетерин дароо көрөсүз", "3. The line shows at once how much goes on sale", "3. Satırda satışa ne kadar çıkacağı hemen görünür", "3. Qatorda sotuvga qancha chiqishi darhol ko'rinadi"),
                T("Выход мяса", "Эттин чыгымы", "Meat yield", "Et verimi", "Go'sht chiqishi"),
                Row(T("В продажу", "Сатууга", "For sale", "Satışa", "Sotuvga"), "225 " + FoodKg(), MockTone.Good, true),
                Row(T("Отходы", "Калдык", "Waste", "Fire", "Chiqindi"), "75 " + FoodKg()),
                Row(T("К оплате поставщику", "Жабдуучуга төлөө", "To pay the supplier", "Tedarikçiye ödenecek", "Yetkazib beruvchiga to'lov"), "120 000 " + FoodSom()),
                Row(T("Себестоимость", "Өздүк нарк", "Cost price", "Maliyet", "Tannarx"), "533,33 " + FoodSom(), MockTone.Warn)),
            Step(T("4. Приход записан: на складе 225 кг, цена кг пересчитана", "4. Келген товар жазылды: кампада 225 кг, 1 кг баасы кайра эсептелди", "4. The delivery is saved: 225 kg in stock, the cost per kg is recalculated", "4. Giriş kaydedildi: depoda 225 kg, kg maliyeti yeniden hesaplandı", "4. Kirim yozildi: omborda 225 kg, 1 kg tannarxi qayta hisoblandi"),
                T("Склад", "Кампа", "Stock", "Depo", "Ombor"),
                Note(T("Брутто 300 кг, выход 75 %, отходы 75 кг", "Брутто 300 кг, чыгым 75 %, калдык 75 кг", "Gross 300 kg, yield 75 %, waste 75 kg", "Brüt 300 kg, verim 75 %, fire 75 kg", "Brutto 300 kg, chiqish 75 %, chiqindi 75 kg"), MockTone.Good),
                Row(T("На складе", "Кампада", "In stock", "Depoda", "Omborda"), "225 " + FoodKg()),
                Row(T("Себестоимость", "Өздүк нарк", "Cost price", "Maliyet", "Tannarx"), "533,33 " + FoodSom(), MockTone.Normal, true)),
        ],
        () =>
        [
            Gain("meat-cleaver", T("Вес для продажи — сам", "Сатуу салмагы — өзү", "Sale weight by itself", "Satış ağırlığı kendiliğinden", "Sotuv vazni o'zi"),
                T("Вписали выход в % — на склад идёт вес, который реально продаётся.", "Чыгымды % менен жаздыңыз — кампага чындап сатылуучу салмак келет.", "Enter the yield in % — the stock gets the weight that really sells.", "Verimi % yazın — depoya gerçekten satılacak ağırlık girer.", "Chiqishni % da yozdingiz — omborga haqiqatan sotiladigan vazn kiradi.")),
            Gain("calculator-variant-outline", T("Себестоимость кг верная", "1 кг өздүк нарк так", "The cost per kg is right", "Kg maliyeti doğru", "1 kg tannarxi to'g'ri"),
                T("Поставщику платите всю сумму, а цена килограмма в продаже пересчитывается сама.", "Жабдуучуга бүт сумманы төлөйсүз, сатуудагы 1 кг баасы өзү кайра эсептелет.", "You pay the supplier in full and the cost per kg on sale is recalculated.", "Tedarikçiye tüm tutarı ödersiniz, satıştaki kg maliyeti yeniden hesaplanır.", "Yetkazib beruvchiga to'liq summani to'laysiz, sotuvdagi 1 kg tannarxi o'zi qayta hisoblanadi.")),
            Gain("history", T("Выход запоминается", "Чыгым эсте калат", "The yield is remembered", "Verim hatırlanır", "Chiqish eslab qolinadi"),
                T("В примечании прихода остаются брутто, выход и отходы; процент подставится в следующий раз.", "Келген товардын эскертмесинде брутто, чыгым жана калдык калат; пайыз кийинки жолу коюлат.", "The delivery note keeps gross, yield and waste; the percentage is prefilled next time.", "Giriş notunda brüt, verim ve fire kalır; yüzde bir sonraki sefer hazır gelir.", "Kirim izohida brutto, chiqish va chiqindi qoladi; foiz keyingi safar o'zi qo'yiladi.")),
        ]);

        // ── Дробное количество ──
        d["fractional_qty"] = new(() =>
        [
            Step(T("1. У товара укажите единицу: м, м², м³, л или т", "1. Товарга бирдикти көрсөтүңүз: м, м², м³, л же т", "1. Set the product's unit: m, m², m³, l or t", "1. Ürüne birim girin: m, m², m³, l veya t", "1. Tovarga birlikni kiriting: m, m², m³, l yoki t"),
                FoodCard(),
                Input(T("Название", "Аталышы", "Name", "Ad", "Nomi"), T("Кабель ВВГ 3×2,5", "ВВГ 3×2,5 кабели", "VVG 3×2.5 cable", "VVG 3×2,5 kablo", "VVG 3×2,5 kabeli")),
                Chips(1, T("шт", "даана", "pcs", "adet", "dona"), "m", "m²", "m³", T("л", "л", "l", "l", "l"), T("т", "т", "t", "t", "t")),
                Input(T("Цена за метр", "1 метрдин баасы", "Price per metre", "Metre fiyatı", "1 metr narxi"), "85")),
            Step(T("2. В чеке количество вводится дробным: 2,5", "2. Чекте сан бөлчөк болуп киргизилет: 2,5", "2. In the receipt the quantity is typed as a fraction: 2.5", "2. Fişte miktar kesirli yazılır: 2,5", "2. Chekda miqdor kasr bilan kiritiladi: 2,5"),
                FoodTill(),
                Input(T("Количество, м", "Саны, м", "Quantity, m", "Miktar, m", "Miqdor, m"), "2,5", true),
                Row(T("Кабель ВВГ 3×2,5", "ВВГ 3×2,5 кабели", "VVG 3×2.5 cable", "VVG 3×2,5 kablo", "VVG 3×2,5 kabeli"), "212,50", MockTone.Normal, true, "2,5 m × 85")),
            Step(T("3. Краска — в литрах, и тоже дробно", "3. Боёк — литр менен, ал да бөлчөк", "3. Paint is in litres, also fractional", "3. Boya litreyle, o da kesirli", "3. Bo'yoq litrda, u ham kasr"),
                FoodTill(),
                Input(T("Количество, л", "Саны, л", "Quantity, l", "Miktar, l", "Miqdor, l"), "3,5", true),
                Row(T("Краска белая", "Ак боёк", "White paint", "Beyaz boya", "Oq bo'yoq"), "875", MockTone.Normal, true, "3,5 l × 250")),
            Step(T("4. Обе позиции — одним чеком", "4. Эки позиция тең — бир чекте", "4. Both lines go in one receipt", "4. İki kalem tek fişte", "4. Ikkala pozitsiya bitta chekda"),
                FoodTill(),
                Row(T("Кабель ВВГ 3×2,5", "ВВГ 3×2,5 кабели", "VVG 3×2.5 cable", "VVG 3×2,5 kablo", "VVG 3×2,5 kabeli"), "212,50", MockTone.Normal, false, "2,5 m"),
                Row(T("Краска белая", "Ак боёк", "White paint", "Beyaz boya", "Oq bo'yoq"), "875", MockTone.Normal, false, "3,5 l"),
                Total(FoodTotal(), "1 087,50", true)),
        ],
        () =>
        [
            Gain("ruler-square", T("Метры, литры, тонны", "Метр, литр, тонна", "Metres, litres, tonnes", "Metre, litre, ton", "Metr, litr, tonna"),
                T("Товар продаётся не только штуками, но и в м, м², м³, л и т.", "Товар даана менен гана эмес, м, м², м³, л жана т менен да сатылат.", "A product sells not only by pieces but also in m, m², m³, l and t.", "Ürün yalnızca adetle değil, m, m², m³, l ve t ile de satılır.", "Tovar faqat dona bilan emas, m, m², m³, l va t bilan ham sotiladi.")),
            Gain("counter", T("Количество — дробное", "Саны — бөлчөк", "A fractional quantity", "Kesirli miktar", "Miqdor — kasr"),
                T("2,5 м кабеля или 3,5 л краски — одной строкой чека.", "2,5 м кабель же 3,5 л боёк — чектин бир сабы менен.", "2.5 m of cable or 3.5 l of paint — one receipt line.", "2,5 m kablo veya 3,5 l boya — fişin tek satırı.", "2,5 m kabel yoki 3,5 l bo'yoq — chekning bitta qatori.")),
            Gain("cash", T("Сумма считается сама", "Сумма өзү эсептелет", "The amount calculates itself", "Tutar kendiliğinden hesaplanır", "Summa o'zi hisoblanadi"),
                T("Касса сама умножает количество на цену — без калькулятора.", "Касса санды баага өзү көбөйтөт — эсептегичсиз.", "The till multiplies quantity by price itself — no calculator.", "Kasa miktarı fiyatla kendisi çarpar — hesap makinesi gerekmez.", "Kassa miqdorni narxga o'zi ko'paytiradi — kalkulyatorsiz.")),
        ]);

        // ── Упаковки ──
        d["packages"] = new(() =>
        [
            Step(T("1. В карточке товара задайте штрихкод коробки и количество", "1. Товардын карточкасында куттун штрихкодун жана санын коюңуз", "1. In the product card set the box barcode and quantity", "1. Ürün kartında kutu barkodunu ve adedi girin", "1. Tovar kartasida quti shtrixkodi va sonini kiriting"),
                FoodCard(),
                Input(T("Штрихкод коробки", "Куттун штрихкоду", "Box barcode", "Kutu barkodu", "Quti shtrixkodi"), "4601234567890"),
                Input(T("Кол-во в упаковке", "Таңгактагы саны", "Pieces per package", "Paketteki adet", "Qadoqdagi soni"), "200", true),
                Toggle(T("Скан коробки добавляет все штуки", "Куту сканери бардык даананы кошот", "Scanning a box adds all pieces", "Kutu okutmak tüm adetleri ekler", "Qutini skanerlash barcha donalarni qo'shadi"), true)),
            Step(T("2. В кассе отсканируйте коробку", "2. Кассада кутуну сканерлеңиз", "2. At the till, scan the box", "2. Kasada kutuyu okutun", "2. Kassada qutini skanerlang"),
                FoodTill(),
                Input(T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrixkod"), "4601234567890", true),
                Note(T("Это коробка: в ней 200 штук саморезов", "Бул куту: ичинде 200 даана шуруп бар", "This is a box: it holds 200 screws", "Bu bir kutu: içinde 200 vida var", "Bu quti: ichida 200 dona shurup bor"))),
            Step(T("3. В чеке сразу все штуки — одной строкой", "3. Чекте бардык даана бирден — бир сапта", "3. The receipt gets all pieces at once, in one line", "3. Fişe tüm adetler tek satırda gelir", "3. Chekda barcha donalar birdan — bitta qatorda"),
                FoodTill(),
                Row(T("Саморез 3,5×35", "Шуруп 3,5×35", "Screw 3.5×35", "Vida 3,5×35", "Shurup 3,5×35"), "1 200", MockTone.Normal, true, "200 × 6"),
                Total(FoodTotal(), "1 200")),
            Step(T("4. Повторный скан — ещё одна коробка", "4. Кайра сканерлөө — дагы бир куту", "4. Scan again — one more box", "4. Tekrar okutmak — bir kutu daha", "4. Qayta skanerlash — yana bitta quti"),
                FoodTill(),
                Row(T("Саморез 3,5×35", "Шуруп 3,5×35", "Screw 3.5×35", "Vida 3,5×35", "Shurup 3,5×35"), "2 400", MockTone.Normal, true, T("400 шт (2 коробки)", "400 даана (2 куту)", "400 pcs (2 boxes)", "400 adet (2 kutu)", "400 dona (2 quti)")),
                Total(FoodTotal(), "2 400")),
        ],
        () =>
        [
            Gain("package-variant-closed", T("Коробка = N штук", "Куту = N даана", "A box = N pieces", "Kutu = N adet", "Quti = N dona"),
                T("Коробку саморезов ×200 или мешок цемента ведёте одним товаром.", "×200 шуруп кутусун же цемент баштыгын бир товар катары жүргүзөсүз.", "A box of screws ×200 or a bag of cement is kept as one product.", "×200 vida kutusu veya çimento çuvalı tek ürün olarak tutulur.", "×200 shurup qutisi yoki sement qopini bitta tovar sifatida yuritasiz.")),
            Gain("barcode-scan", T("Один скан — все штуки", "Бир сканер — бардык даана", "One scan — all pieces", "Tek okutma — tüm adetler", "Bitta skan — barcha dona"),
                T("Скан коробки кладёт в чек сразу всё количество одной строкой.", "Куту сканери чекке бүт санды бир сап менен салат.", "Scanning a box puts the whole quantity in the receipt in one line.", "Kutuyu okutmak tüm adedi fişe tek satırda ekler.", "Qutini skanerlash butun sonni chekka bitta qatorda qo'shadi.")),
            Gain("counter", T("Повторный скан — ещё коробка", "Кайра сканерлөө — дагы куту", "A repeat scan — another box", "Tekrar okutma — bir kutu daha", "Qayta skan — yana quti"),
                T("Второй скан прибавляет ещё одну коробку — считать в уме не нужно.", "Экинчи сканер дагы бир куту кошот — оюңуздан эсептебейсиз.", "A second scan adds another box — no counting in your head.", "İkinci okutma bir kutu daha ekler — kafadan saymak gerekmez.", "Ikkinchi skan yana bitta quti qo'shadi — ongda hisoblash shart emas.")),
        ]);

        // ── Цена по клиенту ──
        d["client_price"] = new(() =>
        [
            Step(T("1. В карточке клиента включите «Покупает по оптовой цене»", "1. Кардардын карточкасында «Дүң баада сатып алат» күйгүзүңүз", "1. In the client card turn on “Buys at the wholesale price”", "1. Müşteri kartında «Toptan fiyattan alır»ı açın", "1. Mijoz kartasida «Ulgurji narxda sotib oladi» ni yoqing"),
                T("Карточка клиента", "Кардардын карточкасы", "Client card", "Müşteri kartı", "Mijoz kartasi"),
                Input(T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), T("Прораб Асан", "Прораб Асан", "Foreman Asan", "Usta Asan", "Prorab Asan")),
                Toggle(T("Покупает по оптовой цене (прораб)", "Дүң баада сатып алат (прораб)", "Buys at the wholesale price (foreman)", "Toptan fiyattan alır (usta)", "Ulgurji narxda sotib oladi (prorab)"), true, true)),
            Step(T("2. У товара задайте оптовую цену", "2. Товарга дүң баасын коюңуз", "2. Set the product's wholesale price", "2. Ürüne toptan fiyat girin", "2. Tovarga ulgurji narxni kiriting"),
                FoodCard(),
                Row(T("Плитка 30×30", "Плитка 30×30", "Tile 30×30", "Fayans 30×30", "Plitka 30×30"), "520", MockTone.Normal, false, T("Розница", "Чекене", "Retail", "Perakende", "Chakana")),
                Input(T("Оптовая цена", "Дүң баасы", "Wholesale price", "Toptan fiyat", "Ulgurji narx"), "480", true)),
            Step(T("3. Выбран этот клиент — товары идут по оптовой цене", "3. Бул кардар тандалды — товарлар дүң баада кетет", "3. This client is chosen — items go at the wholesale price", "3. Bu müşteri seçildi — ürünler toptan fiyatla gider", "3. Shu mijoz tanlandi — tovarlar ulgurji narxda ketadi"),
                FoodTill(),
                Note(T("Цена прораба", "Прораб баасы", "Foreman price", "Usta fiyatı", "Prorab narxi"), MockTone.Accent),
                Row(T("Плитка 30×30", "Плитка 30×30", "Tile 30×30", "Fayans 30×30", "Plitka 30×30"), "480", MockTone.Normal, true, T("было 520", "520 болчу", "was 520", "520'ydi", "520 edi")),
                Total(FoodTotal(), "480")),
            Step(T("4. Товар без оптовой цены остаётся по рознице", "4. Дүң баасы жок товар чекене баада калат", "4. An item without a wholesale price stays at retail", "4. Toptan fiyatı olmayan ürün perakende kalır", "4. Ulgurji narxi yo'q tovar chakana narxda qoladi"),
                FoodTill(),
                Row(T("Плитка 30×30", "Плитка 30×30", "Tile 30×30", "Fayans 30×30", "Plitka 30×30"), "480", MockTone.Normal, false, T("оптовая цена", "дүң баасы", "wholesale price", "toptan fiyat", "ulgurji narx")),
                Row(T("Клей для плитки", "Плитка желими", "Tile adhesive", "Fayans yapıştırıcısı", "Plitka yelimi"), "310", MockTone.Normal, false, T("розница (опта нет)", "чекене (дүң жок)", "retail (no wholesale)", "perakende (toptan yok)", "chakana (ulgurji yo'q)")),
                Total(FoodTotal(), "790", true)),
        ],
        () =>
        [
            Gain("sale", T("Оптовая цена — сама", "Дүң баа — өзү", "The wholesale price by itself", "Toptan fiyat kendiliğinden", "Ulgurji narx o'zi"),
                T("Отмеченному клиенту (прорабу, мастеру) касса даёт опт автоматически.", "Белгиленген кардарга (прораб, уста) касса дүң бааны автоматтык берет.", "A marked client (foreman, mechanic) gets wholesale automatically.", "İşaretli müşteriye (usta) kasa toptan fiyatı otomatik uygular.", "Belgilangan mijozga (prorab, usta) kassa ulgurji narxni avtomatik beradi.")),
            Gain("tag-text-outline", T("Плашка «Цена прораба»", "«Прораб баасы» белгиси", "A “Foreman price” label", "«Usta fiyatı» etiketi", "«Prorab narxi» yozuvi"),
                T("В чеке видно, что цена особая, — вопросов у клиента не возникает.", "Чекте баа өзгөчө экени көрүнөт — кардарда суроо болбойт.", "The receipt shows the price is special — no questions from the client.", "Fişte fiyatın özel olduğu görünür — müşterinin sorusu kalmaz.", "Chekda narx alohida ekani ko'rinadi — mijozda savol qolmaydi.")),
            Gain("account-group-outline", T("Остальные — по рознице", "Калгандар — чекене баада", "Everyone else at retail", "Diğerleri perakende", "Qolganlar — chakana"),
                T("Товары без оптовой цены и прочие клиенты идут по обычной рознице.", "Дүң баасы жок товарлар жана башка кардарлар кадимки чекене баада кетет.", "Items without a wholesale price and other clients go at the usual retail.", "Toptan fiyatı olmayan ürünler ve diğer müşteriler normal perakende fiyatta gider.", "Ulgurji narxi yo'q tovarlar va boshqa mijozlar odatdagi chakana narxda ketadi.")),
        ]);

        // ── Уровни цен ──
        d["price_levels"] = new(() =>
        [
            Step(T("1. У товара задайте цены по уровням", "1. Товарга деңгээлдер боюнча баа коюңуз", "1. Set the product's prices by level", "1. Ürüne seviyelere göre fiyat girin", "1. Tovarga darajalar bo'yicha narx kiriting"),
                T("Тормозные колодки", "Тормоз колодкалары", "Brake pads", "Fren balataları", "Tormoz kolodkalari"),
                Row(T("Розница", "Чекене", "Retail", "Perakende", "Chakana"), "1 100"),
                Row(T("Прораб", "Прораб", "Foreman", "Usta (inşaat)", "Prorab"), "1 000"),
                Row(T("СТО / мастер", "СТО / уста", "Service / mechanic", "Servis / usta", "STO / usta"), "900", MockTone.Normal, true),
                Row(T("Опт", "Дүң", "Wholesale", "Toptan", "Ulgurji"), "800")),
            Step(T("2. В карточке клиента отметьте его уровень", "2. Кардардын карточкасында анын деңгээлин белгилеңиз", "2. In the client card mark the client's level", "2. Müşteri kartında seviyesini işaretleyin", "2. Mijoz kartasida uning darajasini belgilang"),
                T("Карточка клиента", "Кардардын карточкасы", "Client card", "Müşteri kartı", "Mijoz kartasi"),
                Input(T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), T("СТО «Азамат»", "«Азамат» СТО", "“Azamat” service", "«Azamat» servis", "«Azamat» STO")),
                Chips(2, T("Розница", "Чекене", "Retail", "Perakende", "Chakana"), T("Прораб", "Прораб", "Foreman", "Usta (inşaat)", "Prorab"), T("СТО / мастер", "СТО / уста", "Service / mechanic", "Servis / usta", "STO / usta"), T("Опт", "Дүң", "Wholesale", "Toptan", "Ulgurji"))),
            Step(T("3. В чеке товар идёт по цене уровня клиента", "3. Чекте товар кардардын деңгээлинин баасы менен кетет", "3. In the receipt the item goes at the client's level price", "3. Fişte ürün müşterinin seviye fiyatıyla gider", "3. Chekda tovar mijoz darajasi narxida ketadi"),
                FoodTill(),
                Note(T("Уровень: СТО / мастер", "Деңгээл: СТО / уста", "Level: service / mechanic", "Seviye: servis / usta", "Daraja: STO / usta"), MockTone.Accent),
                Row(T("Тормозные колодки", "Тормоз колодкалары", "Brake pads", "Fren balataları", "Tormoz kolodkalari"), "900", MockTone.Normal, true, T("розница 1 100", "чекене 1 100", "retail 1 100", "perakende 1 100", "chakana 1 100")),
                Total(FoodTotal(), "900")),
            Step(T("4. Другой клиент — другой уровень и другая цена", "4. Башка кардар — башка деңгээл жана башка баа", "4. Another client — another level and price", "4. Başka müşteri — başka seviye ve fiyat", "4. Boshqa mijoz — boshqa daraja va narx"),
                FoodTill(),
                Note(T("Уровень: Опт", "Деңгээл: Дүң", "Level: wholesale", "Seviye: toptan", "Daraja: ulgurji"), MockTone.Accent),
                Row(T("Тормозные колодки", "Тормоз колодкалары", "Brake pads", "Fren balataları", "Tormoz kolodkalari"), "800", MockTone.Normal, true, T("розница 1 100", "чекене 1 100", "retail 1 100", "perakende 1 100", "chakana 1 100")),
                Total(FoodTotal(), "800")),
        ],
        () =>
        [
            Gain("layers-outline", T("Несколько цен на товар", "Бир товарга бир нече баа", "Several prices per product", "Bir ürüne birkaç fiyat", "Bitta tovarga bir necha narx"),
                T("Розница, «прораб», «СТО / мастер» и «опт» — у одного товара.", "Чекене, «прораб», «СТО / уста» жана «дүң» — бир товарда.", "Retail, “foreman”, “service / mechanic” and “wholesale” on one product.", "Perakende, «usta», «servis / usta» ve «toptan» tek üründe.", "Chakana, «prorab», «STO / usta» va «ulgurji» — bitta tovarda.")),
            Gain("account-group-outline", T("Уровень у клиента", "Кардарда деңгээл", "A level on the client", "Müşteride seviye", "Mijozda daraja"),
                T("Клиенту отмечается его уровень один раз — дальше цены подставляются сами.", "Кардарга деңгээли бир жолу белгиленет — андан ары баа өзү коюлат.", "A client's level is marked once — prices are filled in afterwards by themselves.", "Müşterinin seviyesi bir kez işaretlenir — fiyatlar sonra kendiliğinden gelir.", "Mijozga uning darajasi bir marta belgilanadi — keyin narxlar o'zi qo'yiladi.")),
            Gain("sale", T("Без споров о цене", "Баа боюнча талашсыз", "No arguments about price", "Fiyat tartışması yok", "Narx bo'yicha bahssiz"),
                T("Кассир не вспоминает скидки: чек сам считает по цене уровня.", "Кассир арзандатууларды эстебейт: чек деңгээлдин баасы менен өзү эсептейт.", "The cashier does not recall discounts: the receipt uses the level price.", "Kasiyer indirimleri hatırlamaz: fiş seviye fiyatıyla hesaplanır.", "Kassir chegirmalarni eslamaydi: chek daraja narxi bilan o'zi hisoblaydi.")),
        ]);

        // ── Калькулятор материалов ──
        d["material_calc"] = new(() =>
        [
            Step(T("1. В карточке товара нажмите «Сколько нужно?»", "1. Товардын карточкасында «Канча керек?» баскычын басыңыз", "1. In the product card press “How much do I need?”", "1. Ürün kartında «Ne kadar gerekir?»e basın", "1. Tovar kartasida «Qancha kerak?» ni bosing"),
                FoodCard(),
                Row(FoodLaminate(), "1 800", MockTone.Normal, false, T("упаковка 2,2 м²", "таңгак 2,2 м²", "pack 2.2 m²", "paket 2,2 m²", "qadoq 2,2 m²")),
                Btn(T("Сколько нужно?", "Канча керек?", "How much do I need?", "Ne kadar gerekir?", "Qancha kerak?"), true)),
            Step(T("2. Введите размеры комнаты", "2. Бөлмөнүн өлчөмдөрүн киргизиңиз", "2. Enter the room dimensions", "2. Oda ölçülerini girin", "2. Xona o'lchamlarini kiriting"),
                T("Сколько нужно?", "Канча керек?", "How much do I need?", "Ne kadar gerekir?", "Qancha kerak?"),
                Input(T("Длина, м", "Узундугу, м", "Length, m", "Uzunluk, m", "Uzunligi, m"), "5"),
                Input(T("Ширина, м", "Туурасы, м", "Width, m", "Genişlik, m", "Kengligi, m"), "4", true)),
            Step(T("3. Касса считает с запасом — до целых упаковок", "3. Касса запас менен эсептейт — бүтүн таңгакка чейин", "3. The till counts with an allowance, up to whole packs", "3. Kasa fire payıyla, tam paketlere kadar hesaplar", "3. Kassa zaxira bilan butun qadoqqacha hisoblaydi"),
                T("Сколько нужно?", "Канча керек?", "How much do I need?", "Ne kadar gerekir?", "Qancha kerak?"),
                Row(T("Площадь", "Аянт", "Area", "Alan", "Maydon"), "20 m²"),
                Row(T("С запасом 10 %", "10 % запас менен", "With a 10 % allowance", "%10 fire payıyla", "10 % zaxira bilan"), "22 m²"),
                Row(T("Нужно упаковок", "Керектүү таңгак", "Packs needed", "Gereken paket", "Kerakli qadoq"), "10", MockTone.Good, true)),
            Step(T("4. «В чек» — нужное количество уже в чеке", "4. «Чекке» — керектүү сан чекте", "4. “To receipt” — the needed quantity is already in the receipt", "4. «Fişe» — gereken miktar fişte", "4. «Chekka» — kerakli miqdor chekda"),
                FoodTill(),
                Row(FoodLaminate(), "18 000", MockTone.Normal, true, T("10 уп. × 1 800", "10 таңгак × 1 800", "10 packs × 1 800", "10 paket × 1 800", "10 qadoq × 1 800")),
                Total(FoodTotal(), "18 000")),
        ],
        () =>
        [
            Gain("vector-square", T("Считает за покупателя", "Сатып алуучунун ордуна эсептейт", "Counts for the buyer", "Alıcı yerine hesaplar", "Xaridor o'rniga hisoblaydi"),
                T("Вводите размеры комнаты — касса считает плитку, ламинат, краску, обои.", "Бөлмөнүн өлчөмүн киргизесиз — касса плитка, ламинат, боёк, обойду эсептейт.", "Enter the room size — the till counts tiles, laminate, paint, wallpaper.", "Oda ölçüsünü girin — kasa fayans, laminat, boya, duvar kağıdını hesaplar.", "Xona o'lchamini kiritasiz — kassa plitka, laminat, bo'yoq, oboyni hisoblaydi.")),
            Gain("package-variant-closed", T("С запасом и целыми упаковками", "Запас менен бүтүн таңгак", "With allowance, whole packs", "Fire payı ve tam paket", "Zaxira va butun qadoq"),
                T("Результат — с запасом и до целых упаковок, без недобора.", "Жыйынтык — запас менен жана бүтүн таңгакка чейин, жетпей калбайт.", "The result has an allowance and whole packs, so you never fall short.", "Sonuç fire payı ve tam paketlerle gelir, eksik kalmaz.", "Natija — zaxira bilan va butun qadoqqacha, yetmay qolmaydi.")),
            Gain("cart-arrow-down", T("В чек одной кнопкой", "Чекке бир баскыч менен", "To the receipt in one tap", "Fişe tek düğmeyle", "Chekka bitta tugma bilan"),
                T("Кнопка «В чек» кладёт нужное количество товара в чек.", "«Чекке» баскычы керектүү санды чекке салат.", "The “To receipt” button puts the needed quantity into the receipt.", "«Fişe» düğmesi gereken miktarı fişe koyar.", "«Chekka» tugmasi kerakli miqdorni chekka soladi.")),
        ]);

        // ── Смета (карточка со своей страницей — «Что получите» уже есть) ──
        d["estimate"] = new(() =>
        [
            Step(T("1. «Новая смета» → «Рассчитать материал»", "1. «Жаңы смета» → «Материалды эсептөө»", "1. “New estimate” → “Calculate material”", "1. «Yeni keşif» → «Malzemeyi hesapla»", "1. «Yangi smeta» → «Materialni hisoblash»"),
                T("Смета", "Смета", "Estimate", "Keşif", "Smeta"),
                Input(T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), T("Бакыт", "Бакыт", "Bakyt", "Bakıt", "Baqit")),
                Btn(T("Рассчитать материал", "Материалды эсептөө", "Calculate material", "Malzemeyi hesapla", "Materialni hisoblash"), true)),
            Step(T("2. Введите размеры комнаты и выберите материал", "2. Бөлмөнүн өлчөмдөрүн киргизип, материалды тандаңыз", "2. Enter the room size and choose the material", "2. Oda ölçülerini girin ve malzemeyi seçin", "2. Xona o'lchamlarini kiriting va materialni tanlang"),
                T("Калькулятор", "Эсептегич", "Calculator", "Hesaplayıcı", "Kalkulyator"),
                Chips(2, T("Краска", "Боёк", "Paint", "Boya", "Bo'yoq"), T("Плитка", "Плитка", "Tiles", "Fayans", "Plitka"), T("Ламинат", "Ламинат", "Laminate", "Laminat", "Laminat"), T("Обои", "Обой", "Wallpaper", "Duvar kağıdı", "Oboy")),
                Input(T("Длина × ширина, м", "Узундугу × туурасы, м", "Length × width, m", "Uzunluk × genişlik, m", "Uzunligi × kengligi, m"), "4 × 5", true),
                Input(T("Запас на подрезку, %", "Кесүүгө запас, %", "Cutting allowance, %", "Kesim fire payı, %", "Kesish zaxirasi, %"), "10")),
            Step(T("3. Касса считает целые упаковки, формула видна", "3. Касса бүтүн таңгактарды эсептейт, формула көрүнөт", "3. The till counts whole packs, the formula is shown", "3. Kasa tam paketleri hesaplar, formül görünür", "3. Kassa butun qadoqlarni hisoblaydi, formula ko'rinadi"),
                T("Калькулятор", "Эсептегич", "Calculator", "Hesaplayıcı", "Kalkulyator"),
                Row(T("Площадь", "Аянт", "Area", "Alan", "Maydon"), "20 m²"),
                Row(T("С запасом 10 %", "10 % запас менен", "With a 10 % allowance", "%10 fire payıyla", "10 % zaxira bilan"), "22 m²", MockTone.Normal, false, "22 ÷ 2,2"),
                Row(FoodLaminate(), "10", MockTone.Good, true, T("упаковок", "таңгак", "packs", "paket", "qadoq")),
                Total(T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), "18 000")),
            Step(T("4. Распечатайте или отправьте смету клиенту", "4. Сметаны басып чыгарыңыз же кардарга жөнөтүңүз", "4. Print the estimate or send it to the client", "4. Keşfi yazdırın veya müşteriye gönderin", "4. Smetani chop eting yoki mijozga yuboring"),
                T("Смета № 1", "Смета № 1", "Estimate № 1", "Keşif № 1", "Smeta № 1"),
                Row(FoodLaminate(), "18 000", MockTone.Normal, false, T("10 уп. × 1 800", "10 таңгак × 1 800", "10 packs × 1 800", "10 paket × 1 800", "10 qadoq × 1 800")),
                Btn(T("Печать A4", "A4 басуу", "Print A4", "A4 yazdır", "A4 chop etish"), false, false),
                Btn(T("Отправить клиенту", "Кардарга жөнөтүү", "Send to the client", "Müşteriye gönder", "Mijozga yuborish"), true)),
            Step(T("5. Клиент согласен — «Добавить всё в чек»", "5. Кардар макул — «Баарын чекке кошуу»", "5. The client agrees — “Add all to the receipt”", "5. Müşteri kabul etti — «Hepsini fişe ekle»", "5. Mijoz rozi — «Hammasini chekka qo'shish»"),
                FoodTill(),
                Row(FoodLaminate(), "18 000", MockTone.Normal, false, T("10 уп. × 1 800", "10 таңгак × 1 800", "10 packs × 1 800", "10 paket × 1 800", "10 qadoq × 1 800")),
                Note(T("Смета принята, позиции в чеке", "Смета кабыл алынды, позициялар чекте", "The estimate is accepted, the lines are in the receipt", "Keşif kabul edildi, kalemler fişte", "Smeta qabul qilindi, pozitsiyalar chekda"), MockTone.Good),
                Btn(T("Добавить всё в чек", "Баарын чекке кошуу", "Add all to the receipt", "Hepsini fişe ekle", "Hammasini chekka qo'shish"), true)),
        ]);

        // ── Прокат (карточка со своей страницей — «Что получите» уже есть) ──
        d["rental"] = new(() =>
        [
            Step(T("1. «Новый прокат»: клиент, вещь, срок", "1. «Жаңы прокат»: кардар, буюм, мөөнөт", "1. “New rental”: client, item, term", "1. «Yeni kiralama»: müşteri, eşya, süre", "1. «Yangi prokat»: mijoz, buyum, muddat"),
                T("Новый прокат", "Жаңы прокат", "New rental", "Yeni kiralama", "Yangi prokat"),
                Input(T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), T("Эрлан", "Эрлан", "Erlan", "Erlan", "Erlan")),
                Input(T("Вещь", "Буюм", "Item", "Eşya", "Buyum"), T("Перфоратор", "Перфоратор", "Rotary hammer", "Kırıcı matkap", "Perforator")),
                Input(T("Срок, дней", "Мөөнөт, күн", "Term, days", "Süre, gün", "Muddat, kun"), "3", true)),
            Step(T("2. Укажите сумму за день и залог", "2. Күнүнө сумманы жана күрөөнү көрсөтүңүз", "2. Set the daily rate and the deposit", "2. Günlük ücreti ve depozitoyu girin", "2. Kunlik summa va garovni kiriting"),
                T("Новый прокат", "Жаңы прокат", "New rental", "Yeni kiralama", "Yangi prokat"),
                Row(T("Сумма за день", "Күнүнө сумма", "Daily rate", "Günlük ücret", "Kunlik summa"), "400"),
                Row(T("Залог (документ)", "Күрөө (документ)", "Deposit (document)", "Depozito (belge)", "Garov (hujjat)"), T("паспорт", "паспорт", "passport", "pasaport", "pasport"), MockTone.Normal, true),
                Total(T("К оплате за 3 дня", "3 күнгө төлөө", "To pay for 3 days", "3 gün için ödenecek", "3 kun uchun to'lov"), "1 200")),
            Step(T("3. В меню значок: сегодня, завтра, просрочено", "3. Менюда белги: бүгүн, эртең, мөөнөтү өттү", "3. A badge in the menu: today, tomorrow, overdue", "3. Menüde rozet: bugün, yarın, gecikmiş", "3. Menyuda belgi: bugun, ertaga, muddati o'tgan"),
                T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat"),
                Row(T("Перфоратор · Эрлан", "Перфоратор · Эрлан", "Rotary hammer · Erlan", "Kırıcı matkap · Erlan", "Perforator · Erlan"), T("сегодня", "бүгүн", "today", "bugün", "bugun"), MockTone.Warn),
                Row(T("Болгарка · Нурлан", "Болгарка · Нурлан", "Angle grinder · Nurlan", "Taşlama · Nurlan", "Bolgarka · Nurlan"), T("просрочено", "мөөнөтү өттү", "overdue", "gecikmiş", "muddati o'tgan"), MockTone.Bad, true),
                Row(T("Лестница · Айжан", "Тепкич · Айжан", "Ladder · Aizhan", "Merdiven · Aycan", "Narvon · Ayjon"), T("завтра", "эртең", "tomorrow", "yarın", "ertaga"))),
            Step(T("4. При возврате — состояние вещи и штраф за просрочку", "4. Кайтарууда — буюмдун абалы жана кечиктирүү айыбы", "4. On return — the item's condition and the late penalty", "4. İadede — eşyanın durumu ve gecikme cezası", "4. Qaytarishda — buyumning holati va kechikish jarimasi"),
                T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                Row(T("Состояние вещи", "Буюмдун абалы", "Item condition", "Eşya durumu", "Buyum holati"), T("хорошее", "жакшы", "good", "iyi", "yaxshi"), MockTone.Good),
                Row(T("Штраф за просрочку", "Кечиктирүү айыбы", "Late penalty", "Gecikme cezası", "Kechikish jarimasi"), "200", MockTone.Bad, true),
                Note(T("Вещь снова на складе", "Буюм кайра кампада", "The item is back in stock", "Eşya yeniden depoda", "Buyum yana omborda"), MockTone.Good)),
        ]);
    }
}
