using System;
using System.Collections.Generic;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-22 (просьба владельца: «иконки и описание у всех новых функций Маркетплейса»): единое место для общих карточек «Модуль» —
// значок (ключ MarketplaceIcons), цвет плитки, короткая фраза, «Что делает», «Как включить / где найти», «Для кого».
// ПРАВИЛО: каждая новая функция, модуль, тема или вид Маркетплейса ОБЯЗАНА иметь свой значок и своё описание на 5 языках (Tr.T).
// Новый модуль сервера = новая строка в Specs ниже (+ значок в MarketplaceIcons.Paths, если нужного нет). Без неё карточка получает запасной
// значок «плюс в квадрате» и шаблонный текст, а тест MarketplaceModuleContentTests красный: он перебирает ModuleIds и известные коды сервера.
// Тексты — только то, что программа делает сейчас (сверено по коду и ТЗ «функции-модули»); цену карточка берёт с сервера, здесь её нет.
public static class MarketplaceModuleContent
{
    /// <summary>Тексты страницы модуля. Short — строка под названием; What — «Что делает» (с примером); Steps — «Как включить / где найти»; Audience — «Для кого».</summary>
    public sealed record Text(string Short, string What, string[] Steps, string[] Audience);

    private sealed record Spec(string Icon, string Color, Func<Text> Make);

    /// <summary>Значок для модуля без описания в Specs (не «пазл»): плюс в квадрате — «добавленная функция».</summary>
    public const string FallbackIcon = "plus-box-outline";

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // ── общие куски ──
    private static string Enable() => T("Нажмите «Включить» на этой странице — программа перестроится сразу.", "Ушул беттеги «Күйгүзүү» баскычын басыңыз — программа дароо кайра курулат.",
        "Press “Turn on” on this page — the program rebuilds at once.", "Bu sayfada «Aç»a basın — program hemen yeniden kurulur.", "Shu sahifada «Yoqish» tugmasini bosing — dastur darhol qayta quriladi.");

    private static string Grocery() => T("Продуктовые магазины", "Азык-түлүк дүкөндөрү", "Grocery shops", "Market ve bakkallar", "Oziq-ovqat do'konlari");
    private static string Hardware() => T("Хозтовары и стройматериалы", "Чарба товарлары жана курулуш материалдары", "Hardware and building materials", "Hırdavat ve inşaat malzemeleri", "Xo'jalik tovarlari va qurilish materiallari");
    private static string Clothing() => T("Магазины одежды и обуви", "Кийим жана бут кийим дүкөндөрү", "Clothing and footwear shops", "Giyim ve ayakkabı mağazaları", "Kiyim va poyabzal do'konlari");
    private static string Electronics() => T("Электроника и бытовая техника", "Электроника жана тиричилик техникасы", "Electronics and home appliances", "Elektronik ve ev aletleri", "Elektronika va maishiy texnika");
    private static string Parts() => T("Автозапчасти", "Автозапчасттар", "Auto parts", "Oto yedek parça", "Avtoehtiyot qismlar");
    private static string Stationery() => T("Канцтовары", "Кеңсе товарлары", "Stationery", "Kırtasiye", "Kantstovarlar");
    private static string Wholesale() => T("Оптовая торговля", "Дүң соода", "Wholesale", "Toptan satış", "Ulgurji savdo");

    // Цвета — из палитры карточек Маркетплейса; соседние в списке различаются, текст на плитке подбирает GlyphColorOn (контраст не хуже 4,5).
    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.OrdinalIgnoreCase)
    {
        [ModuleIds.ExpiryBatches] = new("calendar-range", "#D97706", () => new(
            T("Срок годности и партии: предупреждение о просрочке", "Жарактуулук мөөнөтү жана партиялар: мөөнөтү өткөнү тууралуу эскертүү",
              "Expiry dates and batches: a warning about expired goods", "Son kullanma tarihi ve partiler: süresi geçenler için uyarı", "Yaroqlilik muddati va partiyalar: muddati o'tganlar haqida ogohlantirish"),
            T("Касса помнит срок годности и партию каждого прихода. Если товар просрочен, при добавлении в чек она спросит «Продать всё равно?» (или продаст только после PIN владельца), а в окне «Партии» у товара видны остатки по срокам. Пример: у йогурта срок до 25.11 — после этой даты кассир увидит предупреждение до продажи.",
              "Касса ар бир келген партиянын жарактуулук мөөнөтүн жана партиясын эстейт. Товардын мөөнөтү өтүп кетсе, чекке кошкондо «Баары бир сатабызбы?» деп сурайт (же ээсинин PIN-кодунан кийин гана сатат), ал эми товардагы «Партиялар» терезесинде мөөнөттөр боюнча калдыктар көрүнөт. Мисал: йогурттун мөөнөтү 25.11 чейин — ал күндөн кийин кассир сатуудан мурун эскертүү көрөт.",
              "The till remembers the expiry date and batch of every delivery. If an item is expired, it asks “Sell anyway?” when it is added to the receipt (or sells only after the owner's PIN), and the product's “Batches” window shows stock by date. Example: a yoghurt that expires on 25.11 — after that date the cashier sees a warning before the sale.",
              "Kasa her girişin son kullanma tarihini ve partisini hatırlar. Ürünün süresi geçmişse fişe eklerken «Yine de satılsın mı?» diye sorar (veya yalnızca sahibin PIN'iyle satar); üründeki «Partiler» penceresi tarihlere göre stoğu gösterir. Örnek: son kullanma tarihi 25.11 olan yoğurt — bu tarihten sonra kasiyer satıştan önce uyarı görür.",
              "Kassa har bir kirimning yaroqlilik muddati va partiyasini eslab qoladi. Tovarning muddati o'tgan bo'lsa, chekka qo'shganda «Baribir sotamizmi?» deb so'raydi (yoki faqat egasining PIN-kodidan keyin sotadi), tovardagi «Partiyalar» oynasida muddatlar bo'yicha qoldiq ko'rinadi. Misol: yogurtning muddati 25.11 gacha — shu sanadan keyin kassir sotishdan oldin ogohlantirish ko'radi."),
            [Enable(), T("При приёмке товара укажите срок годности и партию — они появятся в окне «Партии» у товара.", "Товарды кабыл алууда жарактуулук мөөнөтүн жана партияны көрсөтүңүз — алар товардагы «Партиялар» терезесинде көрүнөт.",
                "When receiving goods, enter the expiry date and batch — they appear in the product's “Batches” window.", "Mal kabulünde son kullanma tarihini ve partiyi girin — ürünün «Partiler» penceresinde görünür.", "Tovarni qabul qilishda yaroqlilik muddati va partiyani kiriting — ular tovardagi «Partiyalar» oynasida ko'rinadi.")],
            [Grocery(), T("Любой товар с датой годности", "Жарактуулук мөөнөтү бар ар кандай товар", "Any product with an expiry date", "Son kullanma tarihi olan her ürün", "Yaroqlilik muddati bor har qanday tovar")])),

        [ModuleIds.QuickItems] = new("flash-outline", "#EA580C", () => new(
            T("Быстрые товары: самые ходовые — одной кнопкой", "Ыкчам товарлар: эң көп сатылгандар — бир баскыч менен",
              "Quick products: your best sellers on one button", "Hızlı ürünler: en çok satılanlar tek düğmede", "Tezkor mahsulotlar: eng ko'p sotiladiganlari bitta tugmada"),
            T("Когда чек пуст, на экране кассы показаны ваши ходовые товары крупными кнопками: нажал — товар в чеке, без поиска и штрихкода. Пример: хлеб, пакет и вода — кассир пробивает их одним касанием.",
              "Чек бош болгондо кассанын экранында эң көп сатылган товарлар чоң баскычтар менен көрүнөт: бастыңыз — товар чекте, издөөсүз жана штрихкодсуз. Мисал: нан, пакет жана суу — кассир аларды бир тийүү менен өткөрөт.",
              "While the receipt is empty, the till screen shows your best sellers as big buttons: tap — the item is in the receipt, no search or barcode. Example: bread, a bag and water — the cashier rings them up with one tap.",
              "Fiş boşken kasa ekranında en çok satan ürünleriniz büyük düğmelerle gösterilir: dokunun — ürün fişte, arama ve barkod olmadan. Örnek: ekmek, poşet ve su — kasiyer bunları tek dokunuşla okutur.",
              "Chek bo'sh bo'lganda kassa ekranida eng ko'p sotiladigan tovarlar katta tugmalar bilan ko'rinadi: bosdingiz — tovar chekda, qidirish va shtrixkodsiz. Misol: non, paket va suv — kassir ularni bir bosishda o'tkazadi."),
            [Enable(), T("Блок можно скрыть: Настройки → Экран → «Быстрые товары в пустом чеке».", "Блокту жашырууга болот: Жөндөөлөр → Экран → «Бош чектеги ыкчам товарлар».",
                "The block can be hidden: Settings → Screen → “Quick products in an empty receipt”.", "Blok gizlenebilir: Ayarlar → Ekran → «Boş fişte hızlı ürünler».", "Blokni yashirish mumkin: Sozlamalar → Ekran → «Bo'sh chekda tezkor mahsulotlar».")],
            [Grocery(), Stationery()])),

        [ModuleIds.FractionalQty] = new("ruler-square", "#0284C7", () => new(
            T("Дробное количество: 2,5 м, 0,5 л", "Бөлчөк сан: 2,5 м, 0,5 л", "Fractional quantity: 2.5 m, 0.5 l", "Kesirli miktar: 2,5 m, 0,5 l", "Kasr miqdor: 2,5 m, 0,5 l"),
            T("Товар можно продавать не только целыми штуками, но и метрами, м², м³, литрами и тоннами: количество вводится дробным. Минимум и шаг касса берёт по единице товара; килограммы остаются обычным весовым путём. Пример: 2,5 м кабеля или 3,5 л краски — одной строкой чека.",
              "Товарды бүтүн даана менен гана эмес, метр, м², м³, литр жана тонна менен да сатууга болот: санды бөлчөк кылып киргизесиз. Минимумду жана кадамды касса товардын бирдигине жараша алат; килограмм мурдагыдай таразы жолу менен жүрөт. Мисал: 2,5 м кабель же 3,5 л боёк — чектин бир сабында.",
              "A product can be sold not only in whole pieces but also in metres, m², m³, litres and tonnes: the quantity is entered as a fraction. The till takes the minimum and step from the product's unit; kilograms keep the usual weighing path. Example: 2.5 m of cable or 3.5 l of paint — on one receipt line.",
              "Ürün yalnızca tam adetle değil, metre, m², m³, litre ve ton ile de satılabilir: miktar kesirli girilir. Asgari miktarı ve adımı kasa ürünün birimine göre alır; kilogram her zamanki tartı yolunda kalır. Örnek: 2,5 m kablo veya 3,5 l boya — fişte tek satırda.",
              "Tovarni butun donagina emas, metr, m², m³, litr va tonna bilan ham sotish mumkin: miqdor kasr qilib kiritiladi. Minimum va qadamni kassa tovar birligiga qarab oladi; kilogramm avvalgidek tarozi yo'lida qoladi. Misol: 2,5 m kabel yoki 3,5 l bo'yoq — chekning bitta qatorida."),
            [Enable(), T("У товара укажите единицу «м», «м²», «м³», «л» или «т» — в чеке количество вводится дробным.", "Товарга «м», «м²», «м³», «л» же «т» бирдигин көрсөтүңүз — чекте сан бөлчөк болуп киргизилет.",
                "Set the product's unit to “m”, “m²”, “m³”, “l” or “t” — the quantity is then entered as a fraction in the receipt.", "Ürünün birimini «m», «m²», «m³», «l» veya «t» yapın — fişte miktar kesirli girilir.", "Tovar birligini «m», «m²», «m³», «l» yoki «t» qilib belgilang — chekda miqdor kasr qilib kiritiladi.")],
            [Hardware()])),

        [ModuleIds.Packages] = new("package-variant-closed", "#7C3AED", () => new(
            T("Упаковки: коробка или мешок = N штук", "Таңгактар: куту же баштык = N даана", "Packages: a box or a bag = N pieces", "Ambalajlar: kutu veya çuval = N adet", "Qadoqlar: quti yoki qop = N dona"),
            T("Позволяет вести товар упаковками: коробка саморезов ×200, мешок цемента. Штрихкод коробки при сканировании добавляет в чек сразу все штуки одной строкой, повторный скан — ещё одну коробку. Пример: отсканировали коробку ×200 — в чеке 200 шт.",
              "Товарды таңгак менен жүргүзүүгө мүмкүндүк берет: ×200 өзүн-өзү кесүүчү бурама куту, цемент баштыгы. Коробканын штрихкодун сканерлесе чекке бардык даана бир сапта кошулат, кайра сканерлесе — дагы бир куту. Мисал: ×200 кутуну сканерледик — чекте 200 даана.",
              "Lets you keep a product in packages: a box of screws ×200, a bag of cement. Scanning the box barcode adds all the pieces to the receipt on one line; scanning again adds one more box. Example: you scan a ×200 box — the receipt shows 200 pcs.",
              "Ürünü ambalajlarla yürütmenizi sağlar: ×200 vidalık kutu, çimento çuvalı. Kutu barkodu okutulunca tüm adetler fişe tek satırda eklenir, tekrar okutmak bir kutu daha ekler. Örnek: ×200 kutuyu okuttunuz — fişte 200 adet.",
              "Tovarni qadoqlar bilan yuritish imkonini beradi: ×200 shurup qutisi, sement qopi. Quti shtrixkodini skanerlasangiz, chekka barcha donalar bitta qatorda qo'shiladi, qayta skanerlash — yana bitta quti. Misol: ×200 qutini skanerladik — chekda 200 dona."),
            [Enable(), T("У товара добавьте штрихкод коробки с количеством («Кол-во в упаковке») и включите галочку «Скан коробки добавляет все штуки».", "Товарга коробканын штрихкодун саны менен кошуңуз («Таңгактагы саны») жана «Коробканы сканерлөө бардык даананы кошот» белгисин күйгүзүңүз.",
                "Add the box barcode with its quantity (“Quantity in a package”) to the product and turn on “Scanning a box adds all the pieces”.", "Ürüne kutu barkodunu miktarıyla («Ambalajdaki miktar») ekleyin ve «Kutu okutma tüm adetleri ekler» seçeneğini açın.", "Tovarga quti shtrixkodini miqdori bilan qo'shing («Qadoqdagi miqdor») va «Qutini skanerlash barcha donalarni qo'shadi» belgisini yoqing.")],
            [Hardware(), Stationery()])),

        [ModuleIds.ClientPrice] = new("sale", "#DB2777", () => new(
            T("Цена по клиенту: отмеченному клиенту — оптовая цена", "Кардар боюнча баа: белгиленген кардарга — дүң баа", "Price by client: a marked client gets the wholesale price", "Müşteriye özel fiyat: işaretli müşteriye toptan fiyat", "Mijoz bo'yicha narx: belgilangan mijozga — ulgurji narx"),
            T("Отметьте постоянного клиента — прораба или мастера: в его чеке все товары с оптовой ценой идут по опту, остальные — по рознице. В чеке видна плашка «Цена прораба». Пример: прорабу плитка по 480 вместо 520.",
              "Туруктуу кардарды белгилеңиз — прорабды же устаны: анын чегинде дүң баасы бар бардык товарлар дүң баада, калгандары чекене баада болот. Чекте «Прорабдын баасы» жазуусу көрүнөт. Мисал: прорабга плитка 520 эмес, 480.",
              "Mark a regular client — a foreman or a mechanic: in his receipt every product that has a wholesale price goes at wholesale, the rest at retail. The receipt shows a “Foreman price” badge. Example: tiles at 480 instead of 520 for a foreman.",
              "Düzenli müşteriyi işaretleyin — usta veya tamirci: onun fişinde toptan fiyatı olan tüm ürünler toptan, diğerleri perakende gider. Fişte «Usta fiyatı» etiketi görünür. Örnek: ustaya fayans 520 yerine 480.",
              "Doimiy mijozni belgilang — prorab yoki usta: uning chekida ulgurji narxi bor barcha tovarlar ulgurji narxda, qolganlari chakana narxda bo'ladi. Chekda «Prorab narxi» belgisi ko'rinadi. Misol: prorabga plitka 520 o'rniga 480."),
            [Enable(), T("Откройте карточку клиента и включите «Покупает по оптовой цене (прораб)»; оптовую цену задайте в карточке товара.", "Кардардын карточкасын ачып «Дүң баада сатып алат (прораб)» күйгүзүңүз; дүң баасын товардын карточкасында коюңуз.",
                "Open the client's card and turn on “Buys at the wholesale price (foreman)”; set the wholesale price in the product card.", "Müşteri kartını açıp «Toptan fiyatla alır (usta)» seçeneğini açın; toptan fiyatı ürün kartında girin.", "Mijoz kartasini ochib «Ulgurji narxda oladi (prorab)» ni yoqing; ulgurji narxni tovar kartasida kiriting.")],
            [Hardware(), Parts(), Stationery()])),

        [ModuleIds.PriceLevels] = new("layers-outline", "#4F46E5", () => new(
            T("Уровни цен: розница, мастер, опт", "Баа деңгээлдери: чекене, уста, дүң", "Price levels: retail, mechanic, wholesale", "Fiyat seviyeleri: perakende, usta, toptan", "Narx darajalari: chakana, usta, ulgurji"),
            T("Несколько цен на один товар: розничная и для постоянных клиентов — «прораб», «СТО / мастер», «опт». Клиенту отмечается его уровень, и в чеке товар идёт по цене этого уровня. Пример: мастеру СТО деталь по 900 вместо 1 100.",
              "Бир товарга бир нече баа: чекене жана туруктуу кардарларга — «прораб», «СТО / уста», «дүң». Кардарга анын деңгээли белгиленет, чекте товар ошол деңгээлдин баасы менен кетет. Мисал: СТО устасына тетик 1 100 эмес, 900.",
              "Several prices for one product: retail and for regular clients — “foreman”, “garage / mechanic”, “wholesale”. The client is marked with his level, and the product goes into the receipt at that level's price. Example: a part at 900 instead of 1,100 for a garage mechanic.",
              "Bir ürüne birden çok fiyat: perakende ve düzenli müşteriler için — «usta», «servis / usta», «toptan». Müşteriye seviyesi işaretlenir, fişte ürün o seviyenin fiyatıyla gider. Örnek: servis ustasına parça 1.100 yerine 900.",
              "Bitta tovarga bir nechta narx: chakana va doimiy mijozlar uchun — «prorab», «STO / usta», «ulgurji». Mijozga uning darajasi belgilanadi, chekda tovar shu daraja narxida ketadi. Misol: STO ustasiga ehtiyot qism 1 100 o'rniga 900."),
            [Enable(), T("Цены уровней задайте в карточке товара, а уровень клиента — в карточке клиента.", "Деңгээлдердин баасын товардын карточкасында, кардардын деңгээлин кардардын карточкасында коюңуз.",
                "Set the level prices in the product card and the client's level in the client card.", "Seviye fiyatlarını ürün kartında, müşterinin seviyesini müşteri kartında belirleyin.", "Daraja narxlarini tovar kartasida, mijoz darajasini mijoz kartasida belgilang.")],
            [Hardware(), Parts(), Wholesale()])),

        [ModuleIds.MaterialCalc] = new("vector-square", "#0D9488", () => new(
            T("Калькулятор «Сколько нужно?» для отделки и ремонта", "Тегиздөө жана оңдоо үчүн «Канча керек?» эсептегичи", "“How much do I need?” calculator for finishing and repairs", "Tadilat için «Ne kadar gerekir?» hesaplayıcısı", "Pardozlash va ta'mirlash uchun «Qancha kerak?» hisoblagichi"),
            T("Вы вводите размеры, а касса считает, сколько взять плитки, ламината, краски, обоев или сухой смеси — с запасом. Кнопка «В чек» кладёт нужное количество в чек. Пример: комната 4 × 5 м — сколько упаковок ламината.",
              "Сиз өлчөмдөрдү киргизесиз, касса болсо канча плитка, ламинат, боёк, обой же кургак аралашма алуу керектигин запас менен эсептейт. «Чекке» баскычы керектүү санды чекке салат. Мисал: бөлмө 4 × 5 м — канча таңгак ламинат.",
              "You enter the dimensions and the till works out how much tile, laminate, paint, wallpaper or dry mix to take — with a reserve. The “To receipt” button puts the needed quantity into the receipt. Example: a 4 × 5 m room — how many packs of laminate.",
              "Ölçüleri girersiniz, kasa ne kadar fayans, laminat, boya, duvar kâğıdı veya kuru harç alınacağını yedekle birlikte hesaplar. «Fişe» düğmesi gereken miktarı fişe koyar. Örnek: 4 × 5 m oda — kaç paket laminat.",
              "Siz o'lchamlarni kiritasiz, kassa esa qancha plitka, laminat, bo'yoq, oboy yoki quruq aralashma olish kerakligini zaxira bilan hisoblaydi. «Chekka» tugmasi kerakli miqdorni chekka qo'yadi. Misol: xona 4 × 5 m — nechta qadoq laminat."),
            [Enable(), T("Откройте карточку товара (значок «i») и нажмите «Сколько нужно?».", "Товардын карточкасын (белги «i») ачып «Канча керек?» баскычын басыңыз.",
                "Open the product card (the “i” icon) and press “How much do I need?”.", "Ürün kartını («i» simgesi) açıp «Ne kadar gerekir?» düğmesine basın.", "Tovar kartasini («i» belgisi) ochib «Qancha kerak?» tugmasini bosing.")],
            [Hardware()])),

        [ModuleIds.Serials] = new("pound", "#475569", () => new(
            T("Серийные номера и IMEI в чеке", "Чектеги сериялык номерлер жана IMEI", "Serial numbers and IMEI in the receipt", "Fişte seri numaraları ve IMEI", "Chekdagi seriya raqamlari va IMEI"),
            T("Для телефонов, ноутбуков и техники кассир при продаже вводит или сканирует серийный номер или IMEI — он записывается в строку чека. Пример: продали телефон — в его строке чека стоит IMEI.",
              "Телефондор, ноутбуктар жана техника үчүн кассир сатууда сериялык номерди же IMEI киргизет же сканерлейт — ал чектин сабына жазылат. Мисал: телефон сатылды — анын чек сабында IMEI турат.",
              "For phones, laptops and appliances the cashier enters or scans the serial number or IMEI at the sale — it is written to the receipt line. Example: a phone is sold — its receipt line carries the IMEI.",
              "Telefon, dizüstü ve cihazlarda kasiyer satışta seri numarasını veya IMEI'yi girer ya da okutur — fiş satırına yazılır. Örnek: telefon satıldı — fiş satırında IMEI durur.",
              "Telefon, noutbuk va texnika uchun kassir sotishda seriya raqami yoki IMEI ni kiritadi yoki skanerlaydi — u chek qatoriga yoziladi. Misol: telefon sotildi — uning chek qatorida IMEI turadi."),
            [Enable(), T("Номер спрашивается у товаров с отметкой «серийный номер» и у категорий вроде телефонов и ноутбуков.", "Номер «сериялык номер» белгиси бар товарлардан жана телефон, ноутбук сыяктуу категориялардан суралат.",
                "The number is asked for products marked “serial number” and for categories such as phones and laptops.", "Numara, «seri numarası» işaretli ürünlerde ve telefon, dizüstü gibi kategorilerde sorulur.", "Raqam «seriya raqami» belgisi bor tovarlardan va telefon, noutbuk kabi toifalardan so'raladi.")],
            [Electronics()])),

        [ModuleIds.Warranty] = new("shield-check-outline", "#16A34A", () => new(
            T("Гарантия: срок в чеке и проверка при возврате", "Кепилдик: чектеги мөөнөтү жана кайтарууда текшерүү", "Warranty: the term in the receipt and a check on return", "Garanti: fişte süre ve iadede kontrol", "Kafolat: chekdagi muddat va qaytarishda tekshiruv"),
            T("Касса знает срок гарантии товара (у техники по умолчанию 12 месяцев) и пишет его в чек. Если покупатель возвращает вещь с причиной «Гарантия», срок проверяется — сразу видно, действует ли гарантия. Пример: телефон куплен 8 месяцев назад — гарантия ещё действует.",
              "Касса товардын кепилдик мөөнөтүн билет (техникада демейки 12 ай) жана аны чекке жазат. Сатып алуучу буюмду «Кепилдик» себеби менен кайтарса, мөөнөт текшерилет — кепилдик иштеп жатканы дароо көрүнөт. Мисал: телефон 8 ай мурун сатылган — кепилдик дагы иштейт.",
              "The till knows the product's warranty term (12 months by default for appliances) and writes it to the receipt. If a customer returns an item with the reason “Warranty”, the term is checked — you see at once whether the warranty is valid. Example: a phone bought 8 months ago — still under warranty.",
              "Kasa ürünün garanti süresini bilir (cihazlarda varsayılan 12 ay) ve fişe yazar. Müşteri ürünü «Garanti» nedeniyle iade ederse süre kontrol edilir — garantinin geçerli olup olmadığı hemen görünür. Örnek: 8 ay önce alınan telefon — garanti hâlâ geçerli.",
              "Kassa tovarning kafolat muddatini biladi (texnikada odatda 12 oy) va uni chekka yozadi. Xaridor buyumni «Kafolat» sababi bilan qaytarsa, muddat tekshiriladi — kafolat amal qilishi darhol ko'rinadi. Misol: telefon 8 oy oldin sotilgan — kafolat hali amal qiladi."),
            [Enable(), T("Срок в месяцах — в карточке товара; не задан — берётся по категории. При возврате выберите причину «Гарантия».", "Айлар менен мөөнөт — товардын карточкасында; коюлбаса категория боюнча алынат. Кайтарууда «Кепилдик» себебин тандаңыз.",
                "The term in months is in the product card; if not set it comes from the category. On a return choose the reason “Warranty”.", "Ay cinsinden süre ürün kartındadır; girilmezse kategoriden alınır. İadede «Garanti» nedenini seçin.", "Oylardagi muddat tovar kartasida; belgilanmagan bo'lsa toifadan olinadi. Qaytarishda «Kafolat» sababini tanlang.")],
            [Electronics(), Parts()])),

        [ModuleIds.PartNumbers] = new("format-list-numbered", "#DC2626", () => new(
            T("Каталожный номер и OEM: поиск детали по номеру", "Каталог номери жана OEM: тетикти номер боюнча издөө", "Catalogue number and OEM: find a part by number", "Katalog numarası ve OEM: parçayı numarayla bulun", "Katalog raqami va OEM: ehtiyot qismni raqam bo'yicha qidirish"),
            T("У детали записывается каталожный номер и OEM-номер, а искать её можно по номеру, в том числе «бренд + номер». Пример: клиент называет номер фильтра — касса находит деталь, даже если в названии номера нет.",
              "Тетикке каталог номери жана OEM номери жазылат, аны номер боюнча да, «бренд + номер» боюнча да издөөгө болот. Мисал: кардар чыпканын номерин айтат — аталышында номер жок болсо да, касса тетикти табат.",
              "A part gets a catalogue number and an OEM number, and it can be found by number, including “brand + number”. Example: a client names a filter number — the till finds the part even if the name has no number.",
              "Parçaya katalog numarası ve OEM numarası yazılır, parça numarayla, «marka + numara» ile de aranabilir. Örnek: müşteri filtrenin numarasını söyler — adında numara olmasa da kasa parçayı bulur.",
              "Ehtiyot qismga katalog raqami va OEM raqami yoziladi, uni raqam bo'yicha, jumladan «brend + raqam» bo'yicha qidirish mumkin. Misol: mijoz filtr raqamini aytadi — nomida raqam bo'lmasa ham, kassa qismni topadi."),
            [Enable(), T("Заполните «Каталожный номер» в карточке детали, затем ищите по номеру в строке поиска.", "Тетиктин карточкасында «Каталог номерин» толтуруңуз, андан соң издөө сабында номер боюнча издеңиз.",
                "Fill in “Catalogue number” in the part card, then search by number in the search bar.", "Parça kartında «Katalog numarası»nı doldurun, sonra arama çubuğunda numarayla arayın.", "Qism kartasida «Katalog raqami» ni to'ldiring, so'ng qidiruv qatorida raqam bo'yicha qidiring.")],
            [Parts()])),

        [ModuleIds.CrossAnalogs] = new("swap-horizontal", "#0891B2", () => new(
            T("Аналоги и кроссы: замена, если детали нет", "Аналогдор жана кросстор: тетик жок болсо алмаштыруу", "Analogues and cross-references: a substitute when the part is out", "Muadiller ve çaprazlar: parça yoksa alternatif", "Analoglar va krosslar: qism bo'lmasa almashtirish"),
            T("У детали можно указать аналоги и кроссы — подходящие замены других брендов. В карточке детали кнопка «Аналоги» покажет их, чтобы предложить замену, если нужной детали нет. Пример: нет оригинального фильтра — кассир сразу открывает аналоги.",
              "Тетикке аналогдорду жана кросстарды — башка бренддердин ылайыктуу алмашмаларын — көрсөтүүгө болот. Тетиктин карточкасындагы «Аналогдор» баскычы алардын тизмесин көрсөтөт, керектүү тетик жок болсо алмаштырууну сунуштоо үчүн. Мисал: түп нускадагы чыпка жок — кассир аналогдорду дароо ачат.",
              "A part can list analogues and cross-references — suitable substitutes from other brands. The “Analogues” button in the part card shows them so you can offer a replacement when the needed part is out. Example: no original filter — the cashier opens the analogues at once.",
              "Parçaya muadiller ve çaprazlar — diğer markalardan uygun alternatifler — yazılabilir. Parça kartındaki «Muadiller» düğmesi, gereken parça yoksa alternatif önermek için bunları gösterir. Örnek: orijinal filtre yok — kasiyer muadilleri hemen açar.",
              "Ehtiyot qismga analoglar va krosslar — boshqa brendlarning mos almashtiruvchilari — ko'rsatilishi mumkin. Qism kartasidagi «Analoglar» tugmasi kerakli qism bo'lmasa almashtirishni taklif qilish uchun ularni ko'rsatadi. Misol: original filtr yo'q — kassir analoglarni darhol ochadi."),
            [Enable(), T("В карточке детали добавьте аналоги; кнопка «Аналоги» откроет их список.", "Тетиктин карточкасына аналогдорду кошуңуз; «Аналогдор» баскычы тизмесин ачат.",
                "Add analogues in the part card; the “Analogues” button opens their list.", "Parça kartına muadilleri ekleyin; «Muadiller» düğmesi listesini açar.", "Qism kartasiga analoglarni qo'shing; «Analoglar» tugmasi ro'yxatni ochadi.")],
            [Parts()])),

        [ModuleIds.CustomerOrders] = new("clipboard-check-outline", "#9333EA", () => new(
            T("Заказ под клиента с предоплатой", "Кардарга заказ алдын ала төлөм менен", "A customer order with a prepayment", "Ön ödemeli müşteri siparişi", "Mijoz uchun oldindan to'lovli buyurtma"),
            T("Когда нужной детали нет, вы записываете заказ под клиента с предоплатой, а когда деталь пришла — оформляете выдачу. Предоплата и отмена заказа по PIN под контролем владельца. Пример: клиент оставил предоплату за генератор — деталь привезут через два дня.",
              "Керектүү тетик жок болгондо кардарга алдын ала төлөм менен заказ жазасыз, тетик келгенде — берүүнү расмийлейсиз. Алдын ала төлөм жана заказды PIN менен жокко чыгаруу ээсинин көзөмөлүндө. Мисал: кардар генератор үчүн алдын ала төлөдү — тетикти эки күндөн кийин алып келишет.",
              "When the needed part is out, you record a customer order with a prepayment and, when the part arrives, hand it over. The prepayment and cancelling an order by PIN are under the owner's control. Example: a client leaves a prepayment for an alternator — the part arrives in two days.",
              "Gereken parça yokken müşteri için ön ödemeli sipariş yazarsınız, parça gelince teslimi yaparsınız. Ön ödeme ve siparişin PIN'le iptali sahibin kontrolündedir. Örnek: müşteri alternatör için ön ödeme bıraktı — parça iki güne gelir.",
              "Kerakli qism bo'lmaganda mijoz uchun oldindan to'lovli buyurtma yozasiz, qism kelganda — topshirishni rasmiylashtirasiz. Oldindan to'lov va buyurtmani PIN bilan bekor qilish egasi nazoratida. Misol: mijoz generator uchun oldindan to'lov qoldirdi — qism ikki kunda keltiriladi."),
            [Enable(), T("Заказ создаётся в окне «Заказ под клиента»; выдача оформляется, когда деталь пришла.", "Заказ «Кардарга заказ» терезесинде түзүлөт; тетик келгенде берүү расмийленет.",
                "The order is created in the “Customer order” window; hand-over is recorded when the part arrives.", "Sipariş «Müşteri siparişi» penceresinde oluşturulur; parça gelince teslim işlenir.", "Buyurtma «Mijoz uchun buyurtma» oynasida yaratiladi; qism kelganda topshirish rasmiylashtiriladi.")],
            [Parts(), Wholesale()])),

        [ModuleIds.ScalesLabels] = new("scale-balance", "#65A30D", () => new(
            T("Весы с этикетками: товары на весы, печать этикеток", "Этикеткалуу таразылар: товарларды таразыга жөнөтүү, этикетка басуу", "Label scales: send products to the scale and print labels", "Etiketli teraziler: ürünleri tartıya gönderme, etiket basma", "Etiketli tarozilar: tovarlarni tarozilarga yuborish, etiketka chop etish"),
            T("Товары с кодом (PLU) отправляются на весы с этикетками — Штрих, Rongta, CAS, Масса-К, TM-30F. Весы печатают этикетку, а касса читает её штрихкод. Пример: взвесили сыр, наклеили этикетку — касса сама берёт товар и вес.",
              "PLU коду бар товарлар этикеткалуу таразыга жөнөтүлөт — Штрих, Rongta, CAS, Масса-К, TM-30F. Таразы этикетка басат, касса анын штрихкодун окуйт. Мисал: быштакты таразаладык, этикетканы чаптадык — касса товарды жана салмакты өзү алат.",
              "Products with a PLU code are sent to label scales — Shtrikh, Rongta, CAS, Massa-K, TM-30F. The scale prints a label and the till reads its barcode. Example: cheese is weighed, the label is stuck on — the till takes the product and weight by itself.",
              "PLU kodlu ürünler etiketli teraziye gönderilir — Shtrikh, Rongta, CAS, Massa-K, TM-30F. Terazi etiket basar, kasa barkodunu okur. Örnek: peynir tartıldı, etiket yapıştırıldı — kasa ürünü ve ağırlığı kendisi alır.",
              "PLU kodli tovarlar etiketli tarozilarga yuboriladi — Shtrix, Rongta, CAS, Massa-K, TM-30F. Tarozi etiketka chop etadi, kassa uning shtrixkodini o'qiydi. Misol: pishloq tortildi, etiketka yopishtirildi — kassa tovar va vaznni o'zi oladi."),
            [Enable(), T("Откройте раздел «Весы», подключите весы и отправьте товары; в карточке товара задайте код PLU.", "«Таразылар» бөлүмүн ачып, таразыны туташтырыңыз жана товарларды жөнөтүңүз; товардын карточкасында PLU кодун коюңуз.",
                "Open the “Scales” section, connect the scale and send the products; set the PLU code in the product card.", "«Teraziler» bölümünü açın, tartıyı bağlayın ve ürünleri gönderin; ürün kartında PLU kodunu girin.", "«Tarozilar» bo'limini ochib, tarozini ulang va tovarlarni yuboring; tovar kartasida PLU kodini kiriting.")],
            [Grocery()])),

        [ModuleIds.Consultant] = new("account-voice", "#C026D3", () => new(
            T("Консультант: кто помог покупателю и его процент", "Консультант: сатып алуучуга ким жардам берди жана анын пайызы", "Consultant: who helped the customer and their percentage", "Danışman: müşteriye kim yardım etti ve yüzdesi", "Maslahatchi: xaridorga kim yordam berdi va uning foizi"),
            T("В окне оплаты появляется блок «Консультант»: кассир выбирает, кто помог покупателю, и процент ему с продажи. Продажи по консультантам видны в отчёте по зарплате. Пример: Айгуль помогла выбрать пальто — ей идёт процент с этой продажи.",
              "Төлөм терезесинде «Консультант» блогу пайда болот: кассир сатып алуучуга ким жардам бергенин жана ага сатуудан канча пайыз экенин тандайт. Консультанттар боюнча сатуулар эмгек акы отчётунда көрүнөт. Мисал: Айгүл пальто тандоого жардам берди — ушул сатуудан ага пайыз берилет.",
              "A “Consultant” block appears in the payment window: the cashier picks who helped the customer and the percentage they get from the sale. Sales by consultant show in the salary report. Example: Aigul helped choose a coat — she gets a percentage of that sale.",
              "Ödeme penceresinde «Danışman» bloğu çıkar: kasiyer müşteriye kimin yardım ettiğini ve satıştan ona düşen yüzdeyi seçer. Danışmanlara göre satışlar maaş raporunda görünür. Örnek: Aygül palto seçmeye yardım etti — bu satıştan ona yüzde verilir.",
              "To'lov oynasida «Maslahatchi» bloki paydo bo'ladi: kassir xaridorga kim yordam bergani va unga sotuvdan qancha foiz tegishli ekanini tanlaydi. Maslahatchilar bo'yicha sotuvlar ish haqi hisobotida ko'rinadi. Misol: Aygul palto tanlashga yordam berdi — shu sotuvdan unga foiz beriladi."),
            [Enable(), T("В окне оплаты выберите консультанта в блоке «Консультант»; процент берётся из профиля его выплат.", "Төлөм терезесиндеги «Консультант» блогунан консультантты тандаңыз; пайыз анын төлөм профилинен алынат.",
                "In the payment window pick the consultant in the “Consultant” block; the percentage comes from their payout profile.", "Ödeme penceresinde «Danışman» bloğundan danışmanı seçin; yüzde onun ödeme profilinden alınır.", "To'lov oynasida «Maslahatchi» blokida maslahatchini tanlang; foiz uning to'lov profilidan olinadi.")],
            [Clothing()])),

        [ModuleIds.Exchange] = new("sync", "#2563EB", () => new(
            T("Обмен товара: вернул одно — взял другое", "Товарды алмаштыруу: бирин кайтарды — экинчисин алды", "Exchange: return one item, take another", "Ürün değişimi: birini iade et, diğerini al", "Tovar almashtirish: birini qaytardi — boshqasini oldi"),
            T("Покупатель возвращает вещь и берёт другую, а касса сама считает разницу — доплатить или вернуть деньги. Пример: платье не подошло по размеру — меняем на другое, доплата 300 сом.",
              "Сатып алуучу буюмду кайтарып, башкасын алат, ал эми касса айырманы өзү эсептейт — кошумча төлөө же акчаны кайтаруу. Мисал: көйнөк өлчөмү туура келбеди — башкасына алмаштырабыз, кошумча төлөм 300 сом.",
              "The customer returns an item and takes another, and the till works out the difference — to pay extra or to refund. Example: a dress is the wrong size — it is swapped for another, 300 som extra to pay.",
              "Müşteri ürünü iade edip başkasını alır, kasa farkı kendisi hesaplar — ek ödeme veya para iadesi. Örnek: elbise beden uymadı — başkasıyla değiştirilir, 300 som fark ödenir.",
              "Xaridor buyumni qaytarib, boshqasini oladi, kassa esa farqni o'zi hisoblaydi — qo'shimcha to'lash yoki pulni qaytarish. Misol: ko'ylak o'lchami to'g'ri kelmadi — boshqasiga almashtiramiz, qo'shimcha to'lov 300 so'm."),
            [Enable(), T("Откройте возврат продажи и нажмите «Обменять».", "Сатууну кайтарууну ачып, «Алмаштыруу» баскычын басыңыз.",
                "Open the sale return and press “Exchange”.", "Satış iadesini açıp «Değiştir»e basın.", "Sotuvni qaytarishni ochib, «Almashtirish» tugmasini bosing.")],
            [Clothing(), Electronics()])),

        [ModuleIds.Variants] = new("shape-outline", "#E11D48", () => new(
            T("Размеры и цвета: один товар — много вариантов", "Өлчөмдөр жана түстөр: бир товар — көп вариант", "Sizes and colours: one product, many variants", "Beden ve renkler: tek ürün, çok varyant", "O'lchamlar va ranglar: bitta tovar — ko'p variant"),
            T("Один товар ведётся с размерами и цветами: у каждого варианта свой штрихкод и остаток, а в чеке размер можно сменить. Пример: куртка M, L и XL — одна карточка, а не три товара.",
              "Бир товар өлчөмдөрү жана түстөрү менен жүргүзүлөт: ар бир варианттын өз штрихкоду жана калдыгы бар, ал эми чекте өлчөмдү алмаштырууга болот. Мисал: M, L жана XL куртка — үч товар эмес, бир карточка.",
              "One product is kept with sizes and colours: each variant has its own barcode and stock, and the size can be changed in the receipt. Example: a jacket in M, L and XL — one card, not three products.",
              "Tek ürün beden ve renklerle tutulur: her varyantın kendi barkodu ve stoğu vardır, fişte beden değiştirilebilir. Örnek: M, L ve XL ceket — üç ürün değil, tek kart.",
              "Bitta tovar o'lchamlar va ranglar bilan yuritiladi: har bir variantning o'z shtrixkodi va qoldig'i bor, chekda o'lchamni almashtirish mumkin. Misol: M, L va XL kurtka — uchta tovar emas, bitta karta."),
            [Enable(), T("В карточке товара добавьте размеры и цвета — у товара появится блок вариантов.", "Товардын карточкасына өлчөмдөрдү жана түстөрдү кошуңуз — товарда варианттар блогу пайда болот.",
                "Add sizes and colours in the product card — a block of variants appears.", "Ürün kartına beden ve renk ekleyin — üründe varyant bloğu görünür.", "Tovar kartasiga o'lchamlar va ranglarni qo'shing — tovarda variantlar bloki paydo bo'ladi.")],
            [Clothing()])),

        ["condition"] = new("star-half-full", "#CA8A04", () => new(
            T("Состояние детали: новая или Б/У", "Тетиктин абалы: жаңы же Б/У", "Part condition: new or used", "Parça durumu: yeni veya ikinci el", "Qism holati: yangi yoki B/U"),
            T("В карточке детали отмечается состояние — новая или Б/У, чтобы кассир и покупатель видели это сразу. Пример: фара с разборки помечена «Б/У» и не путается с новой.",
              "Тетиктин карточкасында абалы — жаңы же Б/У — белгиленет, кассир да, сатып алуучу да муну дароо көрөт. Мисал: чачылган машинадан алынган фара «Б/У» деп белгиленет жана жаңысы менен чаташпайт.",
              "The part card records its condition — new or used — so the cashier and the buyer see it at once. Example: a headlight from a dismantled car is marked “used” and is not mixed up with a new one.",
              "Parça kartında durumu — yeni veya ikinci el — işaretlenir, kasiyer ve alıcı bunu hemen görür. Örnek: sökülmüş araçtan gelen far «ikinci el» olarak işaretlenir, yenisiyle karışmaz.",
              "Qism kartasida holati — yangi yoki B/U — belgilanadi, kassir ham, xaridor ham buni darhol ko'radi. Misol: demontajdan olingan faralar «B/U» deb belgilanadi va yangisi bilan adashmaydi."),
            [Enable(), T("Состояние выбирается в карточке детали.", "Абал тетиктин карточкасында тандалат.", "The condition is chosen in the part card.", "Durum parça kartında seçilir.", "Holat qism kartasida tanlanadi.")],
            [Parts()])),

        ["storage_cell"] = new("view-grid-outline", "#78716C", () => new(
            T("Ячейка склада: где лежит деталь", "Кампанын уячасы: тетик кайда жатат", "Storage cell: where the part is", "Depo gözü: parça nerede duruyor", "Ombor katagi: qism qayerda yotadi"),
            T("У детали записывается ячейка склада — стеллаж и полка, где она лежит. Кассир быстро находит деталь, не обходя склад. Пример: «Стеллаж 3, полка B» — подсказка прямо в карточке.",
              "Тетикке кампанын уячасы — ал жаткан стеллаж жана текче — жазылат. Кассир кампаны кыдырбай эле тетикти тез табат. Мисал: «3-стеллаж, B текче» — түздөн-түз карточкада эскертме.",
              "A part gets a storage cell — the rack and shelf where it lies. The cashier finds the part quickly without walking the warehouse. Example: “Rack 3, shelf B” — a hint right in the card.",
              "Parçaya depo gözü — durduğu raf ve bölme — yazılır. Kasiyer depoyu dolaşmadan parçayı çabuk bulur. Örnek: «Raf 3, bölme B» — doğrudan kartta ipucu.",
              "Qismga ombor katagi — yotgan javon va tokcha — yoziladi. Kassir omborni aylanib chiqmasdan qismni tez topadi. Misol: «3-javon, B tokcha» — to'g'ridan-to'g'ri kartada eslatma."),
            [Enable(), T("Ячейку укажите в карточке детали — она видна в карточке и при просмотре товара.", "Уячаны тетиктин карточкасында көрсөтүңүз — ал карточкада жана товарды карап жатканда көрүнөт.",
                "Enter the cell in the part card — it shows in the card and when viewing the product.", "Gözü parça kartında girin — kartta ve ürüne bakarken görünür.", "Katakni qism kartasida kiriting — u kartada va tovarni ko'rishda ko'rinadi.")],
            [Parts()])),

        ["sto_price"] = new("percent", "#059669", () => new(
            T("Цена СТО: особая цена для мастеров", "СТО баасы: усталар үчүн өзгөчө баа", "Garage price: a special price for mechanics", "Servis fiyatı: ustalara özel fiyat", "STO narxi: ustalar uchun maxsus narx"),
            T("В карточке детали есть цена СТО (оптовая); клиенту, отмеченному «СТО / мастер», касса сама ставит эту цену. Если цены СТО нет или она выше розницы, остаётся розничная, а кассиру показывается подсказка. Пример: фильтр 900 вместо 1 100.",
              "Тетиктин карточкасында СТО баасы (дүң) бар; «СТО / уста» деп белгиленген кардарга касса ушул бааны өзү коёт. СТО баасы жок болсо же ал чекенеден жогору болсо, чекене баа калат, кассирге эскертүү көрсөтүлөт. Мисал: чыпка 1 100 эмес, 900.",
              "The part card has a garage (wholesale) price; for a client marked “Garage / mechanic” the till sets that price by itself. If there is no garage price or it is above retail, retail stays and the cashier gets a hint. Example: a filter at 900 instead of 1,100.",
              "Parça kartında servis (toptan) fiyatı vardır; «Servis / usta» işaretli müşteriye kasa bu fiyatı kendisi koyar. Servis fiyatı yoksa veya perakendeden yüksekse perakende kalır, kasiyere ipucu gösterilir. Örnek: filtre 1.100 yerine 900.",
              "Qism kartasida STO (ulgurji) narxi bor; «STO / usta» deb belgilangan mijozga kassa shu narxni o'zi qo'yadi. STO narxi bo'lmasa yoki chakanadan yuqori bo'lsa, chakana narx qoladi, kassirga maslahat ko'rsatiladi. Misol: filtr 1 100 o'rniga 900."),
            [Enable(), T("Задайте цену СТО (оптовую) в карточке детали и отметьте клиента «СТО / мастер» в его карточке.", "Тетиктин карточкасында СТО баасын (дүң) коюп, кардарды анын карточкасында «СТО / уста» деп белгилеңиз.",
                "Set the garage (wholesale) price in the part card and mark the client “Garage / mechanic” in the client card.", "Servis (toptan) fiyatını parça kartında girin ve müşteriyi kartında «Servis / usta» olarak işaretleyin.", "STO (ulgurji) narxini qism kartasida belgilang va mijozni uning kartasida «STO / usta» deb belgilang.")],
            [Parts()])),

        ["return_rules"] = new("undo", "#B45309", () => new(
            T("Правила возврата для запчастей", "Тетиктерди кайтаруу эрежелери", "Return rules for parts", "Yedek parça iade kuralları", "Ehtiyot qismlarni qaytarish qoidalari"),
            T("Срок возврата (по умолчанию 14 дней) и запрет «Не принимать установленные детали и электрику» — касса проверяет их сама при возврате, а не кассир по памяти. Пример: деталь куплена 20 дней назад — касса не даст оформить обычный возврат.",
              "Кайтаруу мөөнөтү (демейки 14 күн) жана «Орнотулган тетиктерди жана электриканы кабыл албоо» тыюусу — аларды кайтарууда кассир эстеп эмес, касса өзү текшерет. Мисал: тетик 20 күн мурун сатылган — касса кадимки кайтарууну расмийлетпейт.",
              "The return period (14 days by default) and the ban “Do not accept installed parts and electrics” — the till checks them itself on a return, not the cashier from memory. Example: a part bought 20 days ago — the till will not allow an ordinary return.",
              "İade süresi (varsayılan 14 gün) ve «Takılmış parça ve elektrik kabul edilmez» yasağı — iadede bunları kasiyer hafızadan değil, kasa kendisi kontrol eder. Örnek: 20 gün önce alınan parça — kasa olağan iadeye izin vermez.",
              "Qaytarish muddati (odatda 14 kun) va «O'rnatilgan qismlar va elektrikni qabul qilmaslik» taqiqi — ularni qaytarishda kassir esdan emas, kassaning o'zi tekshiradi. Misol: qism 20 kun oldin sotilgan — kassa oddiy qaytarishga ruxsat bermaydi."),
            [Enable(), T("Срок и запрет настраиваются в настройках сферы «Автозапчасти».", "Мөөнөт жана тыюу «Автозапчасттар» чөйрөсүнүн жөндөөлөрүндө коюлат.",
                "The period and the ban are set in the “Auto parts” sphere settings.", "Süre ve yasak «Oto yedek parça» alanı ayarlarında yapılır.", "Muddat va taqiq «Avtoehtiyot qismlar» sohasi sozlamalarida belgilanadi.")],
            [Parts()])),
    };

    // Палитра запасных цветов для неизвестных модулей сервера (цвет по коду — стабильный, соседи различаются).
    private static readonly string[] FallbackColors = ["#0EA5E9", "#8B5CF6", "#F59E0B", "#10B981", "#EC4899", "#6366F1", "#14B8A6", "#F97316"];

    /// <summary>Коды модулей, у которых есть значок и описание (для проверки).</summary>
    public static IReadOnlyCollection<string> KnownIds => Specs.Keys;

    /// <summary>У модуля есть своё описание, значок и цвет (а не запасные).</summary>
    public static bool Has(string id) => Specs.ContainsKey(id);

    /// <summary>Ключ значка модуля (MarketplaceIcons); неизвестный модуль — запасной «плюс в квадрате», не пазл.</summary>
    public static string IconKeyOf(string id) => Specs.TryGetValue(id, out var s) ? s.Icon : FallbackIcon;

    /// <summary>Цвет плитки модуля (hex); неизвестный — стабильный по коду из запасной палитры.</summary>
    public static string ColorOf(string id)
    {
        if (Specs.TryGetValue(id, out var s))
            return s.Color;
        var hash = 17;
        foreach (var ch in id)
            hash = unchecked(hash * 31 + char.ToLowerInvariant(ch));
        return FallbackColors[(hash & 0x7FFFFFFF) % FallbackColors.Length];
    }

    /// <summary>Тексты страницы модуля на языке программы; null — описания нет (карточка покажет запасной шаблонный текст).</summary>
    public static Text? TextOf(string id) => Specs.TryGetValue(id, out var s) ? s.Make() : null;
}
