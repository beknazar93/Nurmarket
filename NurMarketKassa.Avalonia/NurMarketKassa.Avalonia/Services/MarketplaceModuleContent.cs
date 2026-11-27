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
public static partial class MarketplaceModuleContent // 2026-11-26: partial — однострочные описания для всех модулей сервера лежат в MarketplaceModuleContent.Lines.cs
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
    private static string RepairShops() => T("Мастерские и сервисные центры", "Устакана жана тейлөө борборлору", "Repair shops and service centres", "Tamir atölyeleri ve servis merkezleri", "Ustaxonalar va servis markazlari");
    private static string Parts() => T("Автозапчасти", "Автозапчасттар", "Auto parts", "Oto yedek parça", "Avtoehtiyot qismlar");
    private static string Stationery() => T("Канцтовары", "Кеңсе товарлары", "Stationery", "Kırtasiye", "Kantstovarlar");
    private static string Wholesale() => T("Оптовая торговля", "Дүң соода", "Wholesale", "Toptan satış", "Ulgurji savdo");
    // 2026-11-26: сфера «Базар» (модули category_tiles, volume_price, haggle, currencies, invoice_cargo) — раньше у них не было записи и карточка получала шаблон.
    private static string Bazaar() => T("Базар и контейнеры (розница и опт)", "Базар жана контейнерлер (чекене жана дүң)", "Bazaar and containers (retail and wholesale)", "Pazar ve konteynerler (perakende ve toptan)", "Bozor va konteynerlar (chakana va ulgurji)");
    private static string BazaarWholesale() => T("Оптовый базар", "Дүң базар", "Wholesale bazaar", "Toptan pazar", "Ulgurji bozor");

    // Цвета — из палитры карточек Маркетплейса; соседние в списке различаются, текст на плитке подбирает GlyphColorOn (контраст не хуже 4,5).
    // 2026-11-26: Specs = CoreSpecs + функции без модуля сервера (MarketplaceModuleContent.Features.cs)
    private static readonly Dictionary<string, Spec> CoreSpecs = new(StringComparer.OrdinalIgnoreCase)
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

        // 2026-11-26 (просьба владельца: функция для мясных магазинов): «Выход мяса при приходе» — вес «с костью» и выход % при приходе весового товара. Включается локально (сервер модуль пока не знает), бесплатно.
        [ModuleIds.CarcassYield] = new("meat-cleaver", "#9F1239", () => new(
            T("Выход мяса: купили тушу — в продажу пойдёт меньше, вес и цена пересчитаются сами", "Эттин чыгымы: союшту сатып алдыңыз — сатууга азыраак чыгат, салмак жана баа өзү эсептелет",
              "Meat yield: you buy a carcass — less goes on sale, the weight and cost recalculate themselves", "Et verimi: karkas aldınız — satışa daha azı çıkar, ağırlık ve maliyet kendiliğinden hesaplanır", "Go'sht chiqishi: tushani sotib oldingiz — sotuvga kamroq chiqadi, vazn va tannarx o'zi hisoblanadi"),
            T("При приходе весового товара (кг) вы вписываете, сколько купили «с костью», и выход в процентах — или сразу вес, который пойдёт в продажу. На склад приходит вес для продажи, а поставщику вы платите всю сумму закупки, поэтому себестоимость килограмма в продаже пересчитывается сама. Пример: 300 кг туши по 400 сом/кг, выход 75 % → в продаже 225 кг, отходы 75 кг, к оплате поставщику 120 000 сом, себестоимость 533,33 сом/кг. В примечании прихода остаётся «Брутто 300 кг, выход 75 %, отходы 75 кг», а выход запоминается для следующего прихода.",
              "Салмактуу товар (кг) келгенде «сөөгү менен» канча сатып алганыңызды жана чыгымды пайыз менен жазасыз — же сатууга кете турган салмакты түз эле. Кампага сатууга кете турган салмак келет, ал эми жеткирүүчүгө сатып алуунун бүт суммасын төлөйсүз, ошондуктан сатыктагы килограммдын өздүк баасы өзү кайра эсептелет. Мисал: 300 кг союш, килосу 400 сомдон, чыгым 75 % → сатууда 225 кг, калдык 75 кг, жеткирүүчүгө төлөө 120 000 сом, өздүк баа 533,33 сом/кг. Кирешенин эскертмесинде «Брутто 300 кг, чыгым 75 %, калдык 75 кг» калат, ал эми чыгым кийинки кирешеге эсте калат.",
              "When you receive a weighed product (kg), you enter how much you bought “with bones” and the yield in percent — or the weight that goes on sale directly. The stock receives the weight for sale, while you still pay the supplier the whole purchase amount, so the cost of a kilogram on sale is recalculated by itself. Example: 300 kg of carcass at 400 som/kg, yield 75 % → 225 kg on sale, 75 kg of waste, 120,000 som to pay the supplier, cost 533.33 som/kg. The delivery note keeps “Gross 300 kg, yield 75 %, waste 75 kg”, and the yield is remembered for the next delivery.",
              "Tartılı ürün (kg) girişinde «kemikli» ne kadar aldığınızı ve verimi yüzde olarak yazarsınız — ya da doğrudan satışa gidecek ağırlığı. Depoya satış ağırlığı girer, tedarikçiye ise alışın tüm tutarını ödersiniz; bu yüzden satıştaki kilogramın maliyeti kendiliğinden yeniden hesaplanır. Örnek: kilosu 400 som olan 300 kg karkas, verim %75 → satışta 225 kg, fire 75 kg, tedarikçiye ödenecek 120.000 som, maliyet 533,33 som/kg. Giriş notunda «Brüt 300 kg, verim %75, fire 75 kg» kalır, verim de bir sonraki giriş için hatırlanır.",
              "Tarozida o'lchanadigan tovar (kg) kirimida «suyagi bilan» qancha sotib olganingizni va chiqishni foizda yozasiz — yoki to'g'ridan-to'g'ri sotuvga ketadigan vaznni. Omborga sotuv vazni kiradi, yetkazib beruvchiga esa xaridning butun summasini to'laysiz, shuning uchun sotuvdagi kilogramm tannarxi o'zi qayta hisoblanadi. Misol: kilosi 400 so'mdan 300 kg tusha, chiqish 75 % → sotuvda 225 kg, chiqindi 75 kg, yetkazib beruvchiga 120 000 so'm, tannarx 533,33 so'm/kg. Kirim izohida «Brutto 300 kg, chiqish 75 %, chiqindi 75 kg» qoladi, chiqish esa keyingi kirim uchun eslab qolinadi."),
            [Enable(), T("Склад → Приёмка: у весового товара (кг) нажмите «Выход мяса», впишите «Куплено» и «Выход, %» — в строке сразу видно, сколько пойдёт в продажу.", "Кампа → Кабыл алуу: салмактуу товарда (кг) «Эттин чыгымы» баскычын басып, «Сатып алынды» жана «Чыгым, %» жазыңыз — саптан сатууга канча кете турганы дароо көрүнөт.",
                "Warehouse → Receiving: on a weighed product (kg) press “Meat yield”, enter “Bought” and “Yield, %” — the row shows at once how much goes on sale.", "Depo → Mal kabul: tartılı üründe (kg) «Et verimi»ne basın, «Alınan» ve «Verim, %» yazın — satırda satışa ne kadar gideceği hemen görünür.", "Ombor → Qabul qilish: tarozidagi tovarda (kg) «Go'sht chiqishi» tugmasini bosing, «Sotib olindi» va «Chiqish, %» kiriting — qatorda sotuvga qancha ketishi darrov ko'rinadi.")],
            [T("Мясные магазины и лавки", "Эт дүкөндөрү жана эт сатуучулар", "Butcher shops", "Kasap dükkânları", "Go'sht do'konlari"),
             T("Продуктовые магазины с весовым товаром", "Салмак менен сатылуучу товары бар азык-түлүк дүкөндөрү", "Grocery shops with weighed goods", "Tartılı ürün satan marketler", "Tarozida o'lchanadigan tovarli oziq-ovqat do'konlari")])),

        [ModuleIds.QuickItems] = new("flash-outline", "#EA580C", () => new(
            T("Быстрые товары: самые ходовые — одной кнопкой", "Ыкчам товарлар: эң көп сатылгандар — бир баскыч менен",
              "Quick products: your best sellers on one button", "Hızlı ürünler: en çok satılanlar tek düğmede", "Tezkor mahsulotlar: eng ko'p sotiladiganlari bitta tugmada"),
            T("Когда чек пуст, на экране кассы показаны ваши ходовые товары крупными кнопками: нажал — товар в чеке, без поиска и штрихкода. Пример: хлеб за 35, пакет за 5 и вода за 40 — три касания, и чек на 80 сом готов, без поиска.",
              "Чек бош болгондо кассанын экранында эң көп сатылган товарлар чоң баскычтар менен көрүнөт: бастыңыз — товар чекте, издөөсүз жана штрихкодсуз. Мисал: нан 35, пакет 5 жана суу 40 — үч тийүү, 80 сомдук чек даяр, издөөсүз.",
              "While the receipt is empty, the till screen shows your best sellers as big buttons: tap — the item is in the receipt, no search or barcode. Example: bread for 35, a bag for 5 and water for 40 — three taps and an 80 som receipt is ready, no search.",
              "Fiş boşken kasa ekranında en çok satan ürünleriniz büyük düğmelerle gösterilir: dokunun — ürün fişte, arama ve barkod olmadan. Örnek: ekmek 35, poşet 5 ve su 40 — üç dokunuş, 80 somluk fiş hazır, arama yok.",
              "Chek bo'sh bo'lganda kassa ekranida eng ko'p sotiladigan tovarlar katta tugmalar bilan ko'rinadi: bosdingiz — tovar chekda, qidirish va shtrixkodsiz. Misol: non 35, paket 5 va suv 40 — uch bosish, 80 so'mlik chek tayyor, qidirishsiz."),
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
            T("Для телефонов, ноутбуков и техники кассир при продаже вводит или сканирует серийный номер или IMEI — он записывается в строку чека. Пример: продали телефон за 18 500 — в его строке чека стоит 15-значный IMEI.",
              "Телефондор, ноутбуктар жана техника үчүн кассир сатууда сериялык номерди же IMEI киргизет же сканерлейт — ал чектин сабына жазылат. Мисал: 18 500 сомдук телефон — чек сабында 15 орундуу IMEI.",
              "For phones, laptops and appliances the cashier enters or scans the serial number or IMEI at the sale — it is written to the receipt line. Example: a phone is sold for 18 500 — its receipt line carries the 15-digit IMEI.",
              "Telefon, dizüstü ve cihazlarda kasiyer satışta seri numarasını veya IMEI'yi girer ya da okutur — fiş satırına yazılır. Örnek: telefon 18 500 somdan satıldı — fiş satırında 15 haneli IMEI durur.",
              "Telefon, noutbuk va texnika uchun kassir sotishda seriya raqami yoki IMEI ni kiritadi yoki skanerlaydi — u chek qatoriga yoziladi. Misol: telefon 18 500 so'mga sotildi — uning chek qatorida 15 xonali IMEI turadi."),
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
            T("У детали записывается каталожный номер и OEM-номер, а искать её можно по номеру, в том числе «бренд + номер». Пример: клиент называет номер фильтра 04152-YZZA1 — касса находит деталь за секунду, даже если в названии номера нет.",
              "Тетикке каталог номери жана OEM номери жазылат, аны номер боюнча да, «бренд + номер» боюнча да издөөгө болот. Мисал: кардар чыпканын 04152-YZZA1 номерин айтат — аталышында номер жок болсо да, касса тетикти бир секундда табат.",
              "A part gets a catalogue number and an OEM number, and it can be found by number, including “brand + number”. Example: a client names the filter number 04152-YZZA1 — the till finds the part in a second even if the name has no number.",
              "Parçaya katalog numarası ve OEM numarası yazılır, parça numarayla, «marka + numara» ile de aranabilir. Örnek: müşteri 04152-YZZA1 filtre numarasını söyler — adında numara olmasa da kasa parçayı bir saniyede bulur.",
              "Ehtiyot qismga katalog raqami va OEM raqami yoziladi, uni raqam bo'yicha, jumladan «brend + raqam» bo'yicha qidirish mumkin. Misol: mijoz 04152-YZZA1 filtr raqamini aytadi — nomida raqam bo'lmasa ham, kassa qismni bir soniyada topadi."),
            [Enable(), T("Заполните «Каталожный номер» в карточке детали, затем ищите по номеру в строке поиска.", "Тетиктин карточкасында «Каталог номерин» толтуруңуз, андан соң издөө сабында номер боюнча издеңиз.",
                "Fill in “Catalogue number” in the part card, then search by number in the search bar.", "Parça kartında «Katalog numarası»nı doldurun, sonra arama çubuğunda numarayla arayın.", "Qism kartasida «Katalog raqami» ni to'ldiring, so'ng qidiruv qatorida raqam bo'yicha qidiring.")],
            [Parts()])),

        [ModuleIds.CrossAnalogs] = new("swap-horizontal", "#0891B2", () => new(
            T("Аналоги и кроссы: замена, если детали нет", "Аналогдор жана кросстор: тетик жок болсо алмаштыруу", "Analogues and cross-references: a substitute when the part is out", "Muadiller ve çaprazlar: parça yoksa alternatif", "Analoglar va krosslar: qism bo'lmasa almashtirish"),
            T("У детали можно указать аналоги и кроссы — подходящие замены других брендов. В карточке детали кнопка «Аналоги» покажет их, чтобы предложить замену, если нужной детали нет. Пример: нет оригинального фильтра за 650 — кассир сразу открывает аналоги и предлагает замену за 590.",
              "Тетикке аналогдорду жана кросстарды — башка бренддердин ылайыктуу алмашмаларын — көрсөтүүгө болот. Тетиктин карточкасындагы «Аналогдор» баскычы алардын тизмесин көрсөтөт, керектүү тетик жок болсо алмаштырууну сунуштоо үчүн. Мисал: 650 сомдук түп нускадагы чыпка жок — кассир аналогдорду дароо ачып, 590 сомдук алмашманы сунуштайт.",
              "A part can list analogues and cross-references — suitable substitutes from other brands. The “Analogues” button in the part card shows them so you can offer a replacement when the needed part is out. Example: no original filter for 650 — the cashier opens the analogues at once and offers a substitute for 590.",
              "Parçaya muadiller ve çaprazlar — diğer markalardan uygun alternatifler — yazılabilir. Parça kartındaki «Muadiller» düğmesi, gereken parça yoksa alternatif önermek için bunları gösterir. Örnek: 650 somluk orijinal filtre yok — kasiyer muadilleri hemen açıp 590 somluk alternatif önerir.",
              "Ehtiyot qismga analoglar va krosslar — boshqa brendlarning mos almashtiruvchilari — ko'rsatilishi mumkin. Qism kartasidagi «Analoglar» tugmasi kerakli qism bo'lmasa almashtirishni taklif qilish uchun ularni ko'rsatadi. Misol: 650 so'mlik original filtr yo'q — kassir analoglarni darhol ochib, 590 so'mlik almashtiruvchini taklif qiladi."),
            [Enable(), T("В карточке детали добавьте аналоги; кнопка «Аналоги» откроет их список.", "Тетиктин карточкасына аналогдорду кошуңуз; «Аналогдор» баскычы тизмесин ачат.",
                "Add analogues in the part card; the “Analogues” button opens their list.", "Parça kartına muadilleri ekleyin; «Muadiller» düğmesi listesini açar.", "Qism kartasiga analoglarni qo'shing; «Analoglar» tugmasi ro'yxatni ochadi.")],
            [Parts()])),

        [ModuleIds.CustomerOrders] = new("clipboard-check-outline", "#9333EA", () => new(
            T("Заказ под клиента с предоплатой", "Кардарга заказ алдын ала төлөм менен", "A customer order with a prepayment", "Ön ödemeli müşteri siparişi", "Mijoz uchun oldindan to'lovli buyurtma"),
            T("Когда нужной детали нет, вы записываете заказ под клиента с предоплатой, а когда деталь пришла — оформляете выдачу. Предоплата и отмена заказа по PIN под контролем владельца. Пример: клиент оставил предоплату 3 000 за генератор — деталь привезут через 2 дня.",
              "Керектүү тетик жок болгондо кардарга алдын ала төлөм менен заказ жазасыз, тетик келгенде — берүүнү расмийлейсиз. Алдын ала төлөм жана заказды PIN менен жокко чыгаруу ээсинин көзөмөлүндө. Мисал: кардар генератор үчүн 3 000 сом алдын ала төлөдү — тетикти 2 күндөн кийин алып келишет.",
              "When the needed part is out, you record a customer order with a prepayment and, when the part arrives, hand it over. The prepayment and cancelling an order by PIN are under the owner's control. Example: a client leaves a prepayment of 3 000 for an alternator — the part arrives in 2 days.",
              "Gereken parça yokken müşteri için ön ödemeli sipariş yazarsınız, parça gelince teslimi yaparsınız. Ön ödeme ve siparişin PIN'le iptali sahibin kontrolündedir. Örnek: müşteri alternatör için 3 000 som ön ödeme bıraktı — parça 2 güne gelir.",
              "Kerakli qism bo'lmaganda mijoz uchun oldindan to'lovli buyurtma yozasiz, qism kelganda — topshirishni rasmiylashtirasiz. Oldindan to'lov va buyurtmani PIN bilan bekor qilish egasi nazoratida. Misol: mijoz generator uchun 3 000 so'm oldindan to'lov qoldirdi — qism 2 kunda keltiriladi."),
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
            T("В окне оплаты появляется блок «Консультант»: кассир выбирает, кто помог покупателю, и процент ему с продажи. Продажи по консультантам видны в отчёте по зарплате. Пример: Айгуль помогла выбрать пальто за 6 000 — ей идёт 5 % с этой продажи, то есть 300.",
              "Төлөм терезесинде «Консультант» блогу пайда болот: кассир сатып алуучуга ким жардам бергенин жана ага сатуудан канча пайыз экенин тандайт. Консультанттар боюнча сатуулар эмгек акы отчётунда көрүнөт. Мисал: Айгүл 6 000 сомдук пальто тандоого жардам берди — ушул сатуудан ага 5 %, башкача айтканда 300 берилет.",
              "A “Consultant” block appears in the payment window: the cashier picks who helped the customer and the percentage they get from the sale. Sales by consultant show in the salary report. Example: Aigul helped choose a coat for 6 000 — she gets 5 % of that sale, that is 300.",
              "Ödeme penceresinde «Danışman» bloğu çıkar: kasiyer müşteriye kimin yardım ettiğini ve satıştan ona düşen yüzdeyi seçer. Danışmanlara göre satışlar maaş raporunda görünür. Örnek: Aygül 6 000 somluk palto seçmeye yardım etti — bu satıştan ona %5, yani 300 verilir.",
              "To'lov oynasida «Maslahatchi» bloki paydo bo'ladi: kassir xaridorga kim yordam bergani va unga sotuvdan qancha foiz tegishli ekanini tanlaydi. Maslahatchilar bo'yicha sotuvlar ish haqi hisobotida ko'rinadi. Misol: Aygul 6 000 so'mlik palto tanlashga yordam berdi — shu sotuvdan unga 5 %, ya'ni 300 beriladi."),
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
            T("Один товар ведётся с размерами и цветами: у каждого варианта свой штрихкод и остаток, а в чеке размер можно сменить. Пример: куртка в трёх размерах (M, L, XL) и двух цветах — одна карточка с 6 вариантами, а не 6 товаров.",
              "Бир товар өлчөмдөрү жана түстөрү менен жүргүзүлөт: ар бир варианттын өз штрихкоду жана калдыгы бар, ал эми чекте өлчөмдү алмаштырууга болот. Мисал: үч өлчөмдөгү (M, L, XL) жана эки түстөгү куртка — 6 товар эмес, 6 варианты бар бир карточка.",
              "One product is kept with sizes and colours: each variant has its own barcode and stock, and the size can be changed in the receipt. Example: a jacket in three sizes (M, L, XL) and two colours — one card with 6 variants, not 6 products.",
              "Tek ürün beden ve renklerle tutulur: her varyantın kendi barkodu ve stoğu vardır, fişte beden değiştirilebilir. Örnek: üç bedenli (M, L, XL) ve iki renkli ceket — 6 ürün değil, 6 varyantlı tek kart.",
              "Bitta tovar o'lchamlar va ranglar bilan yuritiladi: har bir variantning o'z shtrixkodi va qoldig'i bor, chekda o'lchamni almashtirish mumkin. Misol: uch o'lchamli (M, L, XL) va ikki rangli kurtka — 6 ta tovar emas, 6 variantli bitta karta."),
            [Enable(), T("В карточке товара добавьте размеры и цвета — у товара появится блок вариантов.", "Товардын карточкасына өлчөмдөрдү жана түстөрдү кошуңуз — товарда варианттар блогу пайда болот.",
                "Add sizes and colours in the product card — a block of variants appears.", "Ürün kartına beden ve renk ekleyin — üründe varyant bloğu görünür.", "Tovar kartasiga o'lchamlar va ranglarni qo'shing — tovarda variantlar bloki paydo bo'ladi.")],
            [Clothing()])),

        ["condition"] = new("star-half-full", "#CA8A04", () => new(
            T("Состояние детали: новая или Б/У", "Тетиктин абалы: жаңы же Б/У", "Part condition: new or used", "Parça durumu: yeni veya ikinci el", "Qism holati: yangi yoki B/U"),
            T("В карточке детали отмечается состояние — новая или Б/У, чтобы кассир и покупатель видели это сразу. Пример: фара с разборки за 1 500 помечена «Б/У» и не путается с новой за 4 200.",
              "Тетиктин карточкасында абалы — жаңы же Б/У — белгиленет, кассир да, сатып алуучу да муну дароо көрөт. Мисал: чачылган машинадан алынган 1 500 сомдук фара «Б/У» деп белгиленет жана 4 200 сомдук жаңысы менен чаташпайт.",
              "The part card records its condition — new or used — so the cashier and the buyer see it at once. Example: a headlight from a dismantled car for 1 500 is marked “used” and is not mixed up with a new one for 4 200.",
              "Parça kartında durumu — yeni veya ikinci el — işaretlenir, kasiyer ve alıcı bunu hemen görür. Örnek: sökülmüş araçtan gelen 1 500 somluk far «ikinci el» olarak işaretlenir, 4 200 somluk yenisiyle karışmaz.",
              "Qism kartasida holati — yangi yoki B/U — belgilanadi, kassir ham, xaridor ham buni darhol ko'radi. Misol: demontajdan olingan 1 500 so'mlik fara «B/U» deb belgilanadi va 4 200 so'mlik yangisi bilan adashmaydi."),
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

        // ── 2026-11-26: модули сферы «Базар» (BazaarModules): тексты сверены по коду кассы и ТЗ «Контейнер-базар» ──
        [BazaarModules.CategoryTiles] = new("view-dashboard-outline", "#0EA5E9", () => new(
            T("Плитки категорий: крупные кнопки вместо длинного списка", "Категория плиткалары: узун тизмеден көрө чоң баскычтар",
              "Category tiles: big buttons instead of a long list", "Kategori kutuları: uzun liste yerine büyük düğmeler", "Kategoriya plitkalari: uzun ro'yxat o'rniga katta tugmalar"),
            T("На главном экране кассы вместо длинного списка товаров — крупные плитки категорий: фото или цвет, название и число товаров. Один тап по плитке — виден список товаров этой категории, тап по товару кладёт его в чек, «← Все категории» возвращает к плиткам. Поиск и группы F1–F12 работают поверх всего каталога. Пример: на базаре продавец касается «Обувь», затем нужной пары — без поиска.",
              "Кассанын башкы экранында товарлардын узун тизмеси ордуна — категориялардын чоң плиткалары: сүрөт же түс, аталышы жана товарлардын саны. Плиткага бир тийүү — ошол категориянын товарлары көрүнөт, товарга тийсеңиз чекке түшөт, «← Бардык категориялар» плиткаларга кайтарат. Издөө жана F1–F12 топтору бүт каталог боюнча иштейт. Мисал: базарда сатуучу «Бут кийим» дегенге, андан кийин керектүү жупка тийет — издебей эле.",
              "On the till's main screen, instead of a long list of products, you see big category tiles: a photo or colour, a name and the number of products. One tap on a tile shows that category's products, a tap on a product puts it in the receipt, “← All categories” goes back to the tiles. Search and the F1–F12 groups work over the whole catalogue. Example: at a bazaar the seller taps “Shoes” and then the right pair — no searching.",
              "Kasanın ana ekranında uzun ürün listesi yerine büyük kategori kutuları görünür: fotoğraf veya renk, ad ve ürün sayısı. Kutuya tek dokunuş o kategorinin ürünlerini açar, ürüne dokunmak onu fişe koyar, «← Tüm kategoriler» kutulara döndürür. Arama ve F1–F12 grupları tüm katalog üzerinde çalışır. Örnek: pazarda satıcı «Ayakkabı»ya, sonra doğru çifte dokunur — aramadan.",
              "Kassaning asosiy ekranida tovarlarning uzun ro'yxati o'rniga kategoriyalarning katta plitkalari ko'rinadi: rasm yoki rang, nom va tovarlar soni. Plitkaga bir bosish — shu kategoriya tovarlari ochiladi, tovarga bosish uni chekka qo'shadi, «← Barcha kategoriyalar» plitkalarga qaytaradi. Qidiruv va F1–F12 guruhlari butun katalog bo'yicha ishlaydi. Misol: bozorda sotuvchi «Poyabzal»ga, so'ng kerakli juftga bosadi — qidirmasdan."),
            [Enable(), T("Фото плитки — фото первого товара категории; без фото плитка получает цвет категории.", "Плитканын сүрөтү — категориянын биринчи товарынын сүрөтү; сүрөтсүз плитка категориянын түсүн алат.",
                "The tile photo is the photo of the category's first product; without a photo the tile gets the category colour.", "Kutu fotoğrafı kategorideki ilk ürünün fotoğrafıdır; fotoğraf yoksa kutu kategori rengini alır.", "Plitka rasmi — kategoriyaning birinchi tovari rasmi; rasm bo'lmasa plitka kategoriya rangini oladi.")],
            [Bazaar(), T("Любой магазин с большим числом категорий", "Категориялары көп ар кандай дүкөн", "Any shop with many categories", "Çok kategorili her mağaza", "Kategoriyalari ko'p har qanday do'kon")])),

        [BazaarModules.VolumePrice] = new("tag-multiple-outline", "#EC4899", () => new(
            T("Цена по количеству: чем больше берут — тем дешевле", "Санына жараша баа: канчалык көп алса — ошончолук арзан",
              "Price by quantity: the more they take, the cheaper", "Miktara göre fiyat: ne kadar çok alırsa o kadar ucuz", "Miqdorga qarab narx: ko'p olsa — arzonroq"),
            T("Для товара задаются ступени «от N штук — цена». Когда кассир набирает количество, цена строки сама меняется по подходящей ступени, а под строкой видна подпись «Опт от 10 — 450». Цену, которую кассир ввёл вручную, касса не трогает. Пример: пара обуви 1 200, от 3 пар — 1 100, от 10 — 1 000.",
              "Товарга «N даанадан — баа» баскычтары коюлат. Кассир санды терип жатканда саптын баасы ылайыктуу баскыч боюнча өзү өзгөрөт, ал эми саптын астында «Дүң, мин. 10 — 450» жазуусу көрүнөт. Кассир кол менен киргизген баасын касса өзгөртпөйт. Мисал: бир жуп бут кийим 1 200, 3 жуптан — 1 100, 10 жуптан — 1 000.",
              "A product gets steps “from N pieces — price”. When the cashier types the quantity, the line price changes by itself to the matching step, and a label like “Bulk from 10 — 450” appears under the line. A price the cashier typed by hand is left alone. Example: a pair of shoes 1,200, from 3 pairs 1,100, from 10 pairs 1,000.",
              "Ürüne «N adetten — fiyat» basamakları girilir. Kasiyer miktarı girdiğinde satır fiyatı uygun basamağa göre kendiliğinden değişir, satırın altında «Toptan, en az 10 — 450» yazısı görünür. Kasiyerin elle girdiği fiyata kasa dokunmaz. Örnek: bir çift ayakkabı 1.200, 3 çiftten 1.100, 10 çiftten 1.000.",
              "Tovarga «N dona dan — narx» pog'onalari kiritiladi. Kassir miqdorni terganda qator narxi mos pog'onaga qarab o'zi o'zgaradi, qator ostida «Ulgurji, kamida 10 — 450» yozuvi ko'rinadi. Kassir qo'lda kiritgan narxga kassa tegmaydi. Misol: bir juft poyabzal 1 200, 3 juftdan — 1 100, 10 juftdan — 1 000."),
            [Enable(), T("В карточке товара появится блок «Цена по количеству» — задайте ступени «от N штук — цена».", "Товардын карточкасында «Санына жараша баа» блогу пайда болот — «N даанадан — баа» баскычтарын коюңуз.",
                "A “Price by quantity” block appears in the product card — set the steps “from N pieces — price”.", "Ürün kartında «Miktara göre fiyat» bloğu görünür — «N adetten — fiyat» basamaklarını girin.", "Tovar kartasida «Miqdorga qarab narx» bloki paydo bo'ladi — «N dona dan — narx» pog'onalarini kiriting.")],
            [Bazaar(), Wholesale()])),

        [BazaarModules.Haggle] = new("message-text-outline", "#F97316", () => new(
            T("Торг в одно касание: «До 10 / 50 / 100» и «Договорились за …»", "Бир тийүүдө соодалашуу: «10 / 50 / 100 чейин» жана «Макулдашылган сумма …»",
              "Haggling in one tap: “To 10 / 50 / 100” and “Agreed for …”", "Tek dokunuşla pazarlık: «10 / 50 / 100'e kadar» ve «Anlaşılan tutar …»", "Bir bosishda savdolashuv: «10 / 50 / 100 gacha» va «Kelishilgan summa …»"),
            T("Покупатель торгуется — кассир нажимает «Торг» у строки или у всего чека. «До 10 / 50 / 100» округляет сумму вниз до шага, в пользу покупателя; «Договорились за …» принимает любую итоговую сумму ниже. Скидка до порога — свободно, выше порога нужен PIN руководителя, выше предела — только владелец. Пример: чек 1 340 — «До 50» даёт 1 300.",
              "Сатып алуучу соодалашат — кассир саптагы же бүт чектеги «Соодалашуу» баскычын басат. «10 / 50 / 100 чейин» суммасын төмөн карай тегеректейт, сатып алуучунун пайдасына; «Макулдашылган сумма …» төмөнкү каалаган жыйынтык сумманы кабыл алат. Чекке чейинки арзандатуу — эркин, чектен жогору жетекчинин PIN коду керек, чектен ашса — ээси гана. Мисал: чек 1 340 — «50 чейин» 1 300 берет.",
              "The customer haggles — the cashier presses “Haggle” on a line or on the whole receipt. “To 10 / 50 / 100” rounds the amount down to the step, in the buyer's favour; “Agreed for …” accepts any lower final amount. A discount up to the threshold is free, above it a manager PIN is needed, above the limit only the owner. Example: a receipt of 1,340 — “To 50” gives 1,300.",
              "Müşteri pazarlık eder — kasiyer satırda veya tüm fişte «Pazarlık»a basar. «10 / 50 / 100'e kadar» tutarı basamağa kadar aşağı yuvarlar, alıcı lehine; «Anlaşılan tutar …» daha düşük herhangi bir son tutarı kabul eder. Eşiğe kadar indirim serbesttir, eşiğin üstünde yönetici PIN'i, sınırın üstünde yalnızca sahip gerekir. Örnek: 1.340'lık fiş — «50'ye kadar» 1.300 verir.",
              "Xaridor savdolashadi — kassir qatorda yoki butun chekda «Savdolashuv»ni bosadi. «10 / 50 / 100 gacha» summani pog'onagacha pastga yaxlitlaydi, xaridor foydasiga; «Kelishilgan summa …» pastroq istalgan yakuniy summani qabul qiladi. Chegaragacha chegirma — erkin, chegaradan yuqorisiga rahbar PIN kodi, limitdan yuqorisiga faqat egasi. Misol: chek 1 340 — «50 gacha» 1 300 beradi."),
            [Enable(), T("В чеке нажмите «Торг»; пороги PIN и предела — Настройки → Операции → Сфера.", "Чекте «Соодалашуу» баскычын басыңыз; PIN жана чек чектери — Жөндөөлөр → Операциялар → Чөйрө.",
                "Press “Haggle” in the receipt; the PIN and limit thresholds are in Settings → Operations → Sphere.", "Fişte «Pazarlık»a basın; PIN ve sınır eşikleri Ayarlar → İşlemler → Alan bölümünde.", "Chekda «Savdolashuv»ni bosing; PIN va limit chegaralari — Sozlamalar → Operatsiyalar → Soha.")],
            [Bazaar()])),

        [BazaarModules.Currencies] = new("currency-usd", "#10B981", () => new(
            T("Валюты: оплата в USD, RUB и CNY по курсу компании", "Валюталар: компаниянын курсу боюнча USD, RUB жана CNY менен төлөм",
              "Currencies: payment in USD, RUB and CNY at the company's rate", "Dövizler: şirketin kuruyla USD, RUB ve CNY ile ödeme", "Valyutalar: kompaniya kursi bo'yicha USD, RUB va CNY da to'lov"),
            T("В окне оплаты наличными появляется блок «Принято в валюте»: кассир выбирает USD, RUB или CNY (те, у которых задан курс), вводит сумму в валюте, а касса пересчитывает её в сомы по курсу компании. Сдача считается в сомах. Пример: покупатель даёт 50 $ — касса сама покажет, сколько это в сомах и какая сдача.",
              "Накта акча менен төлөө терезесинде «Валютада алынды» блогу пайда болот: кассир USD, RUB же CNY тандайт (курсу коюлгандарды), валютадагы сумманы киргизет, ал эми касса аны компаниянын курсу боюнча сомго которот. Кайтарым сом менен эсептелет. Мисал: сатып алуучу 50 $ берет — касса сомдо канча экенин жана кайтарымын өзү көрсөтөт.",
              "The cash payment window gets a “Received in currency” block: the cashier picks USD, RUB or CNY (those that have a rate), types the amount in that currency, and the till converts it to som at the company's rate. Change is worked out in som. Example: the customer hands over $50 — the till shows how much that is in som and what the change is.",
              "Nakit ödeme penceresinde «Dövizle alınan» bloğu çıkar: kasiyer USD, RUB veya CNY seçer (kuru girilmiş olanlar), döviz tutarını girer, kasa bunu şirketin kuruyla soma çevirir. Para üstü somla hesaplanır. Örnek: müşteri 50 $ verir — kasa bunun somda ne kadar olduğunu ve para üstünü kendisi gösterir.",
              "Naqd to'lov oynasida «Valyutada qabul qilindi» bloki paydo bo'ladi: kassir USD, RUB yoki CNY ni tanlaydi (kursi belgilanganlarini), valyutadagi summani kiritadi, kassa esa uni kompaniya kursi bo'yicha so'mga o'giradi. Qaytim so'mda hisoblanadi. Misol: xaridor 50 $ beradi — kassa so'mda qancha ekanini va qaytimini o'zi ko'rsatadi."),
            [Enable(), T("Курсы задаются в окне «Курсы валют» (Настройки → Операции → Сфера).", "Курстар «Валюта курстары» терезесинде коюлат (Жөндөөлөр → Операциялар → Чөйрө).",
                "The rates are set in the “Exchange rates” window (Settings → Operations → Sphere).", "Kurlar «Döviz kurları» penceresinde girilir (Ayarlar → İşlemler → Alan).", "Kurslar «Valyuta kurslari» oynasida belgilanadi (Sozlamalar → Operatsiyalar → Soha).")],
            [Bazaar()])),

        [BazaarModules.InvoiceCargo] = new("file-document-outline", "#6366F1", () => new(
            T("Накладная A4 и «карго» для оптовых отгрузок", "A4 накладная жана дүң жөнөтүүлөр үчүн «карго»", "A4 invoice and “cargo” for wholesale shipments", "Toptan sevkiyat için A4 irsaliye ve «kargo»", "Ulgurji jo'natmalar uchun A4 hisob-faktura va «kargo»"),
            T("Накладная оптовому покупателю печатается с колонками «Мест» и «Вес брутто, кг» по каждой строке и блоком «Карго» под итогами: всего мест, брутто, мешков, перевозчик, город или адрес получателя, трек-номер. Данные хранятся в самой накладной. Пример: отгрузка в Бишкек, 40 мешков и 3 места — на бумаге видно, сколько мешков и какой перевозчик.",
              "Дүң сатып алуучуга накладная ар бир сап боюнча «Орун» жана «Брутто салмак, кг» тилкелери менен жана жыйынтыктын астындагы «Карго» блогу менен басылат: жалпы орун, брутто, баштык, ташуучу, алуучунун шаары же дареги, трек-номер. Маалымат накладныйдын өзүндө сакталат. Мисал: Бишкекке жөнөтүү, 40 баштык жана 3 орун — кагазда канча баштык жана кайсы ташуучу экени көрүнөт.",
              "An invoice for a wholesale buyer is printed with “Places” and “Gross weight, kg” columns on every line and a “Cargo” block under the totals: total places, gross, bags, carrier, the recipient's city or address, tracking number. The data is kept in the invoice itself. Example: a shipment to Bishkek, 40 bags and 3 places — the paper shows how many bags and which carrier.",
              "Toptan alıcıya irsaliye her satırda «Yer» ve «Brüt ağırlık, kg» sütunları ve toplamların altında «Kargo» bloğuyla basılır: toplam yer, brüt, çuval, taşıyıcı, alıcının şehri veya adresi, takip numarası. Veriler irsaliyenin kendisinde saklanır. Örnek: Bişkek'e sevkiyat, 40 çuval ve 3 yer — kâğıtta kaç çuval ve hangi taşıyıcı olduğu görünür.",
              "Ulgurji xaridorga hisob-faktura har bir qatorda «Joy» va «Brutto og'irlik, kg» ustunlari va jami ostida «Kargo» bloki bilan chop etiladi: jami joy, brutto, qop, tashuvchi, oluvchining shahri yoki manzili, trek-raqam. Ma'lumot hisob-fakturaning o'zida saqlanadi. Misol: Bishkekka jo'natma, 40 qop va 3 joy — qog'ozda nechta qop va qaysi tashuvchi ekani ko'rinadi."),
            [Enable(), T("В редакторе накладной включите галочку «Карго» — появятся места, вес и данные перевозчика.", "Накладныйдын редакторунда «Карго» белгисин күйгүзүңүз — орундар, салмак жана ташуучунун маалыматы пайда болот.",
                "In the invoice editor tick “Cargo” — places, weight and carrier details appear.", "İrsaliye düzenleyicide «Kargo»yu işaretleyin — yerler, ağırlık ve taşıyıcı bilgileri görünür.", "Hisob-faktura muharririda «Kargo» belgisini yoqing — joylar, og'irlik va tashuvchi ma'lumotlari paydo bo'ladi.")],
            [BazaarWholesale(), Wholesale()])),
        // 2026-11-26 (просьба координатора): «Статус ремонта на витрине» — сервер модуль и признак repair_status_public пока не присылает («Скоро: ждём сервер NurCRM»).
        [ModuleIds.RepairStatusSite] = new("cellphone-message", "#C2410C", () => new(
            T("Статус ремонта на витрине: клиент проверяет сам и получает сообщение, когда готово", "Витринада оңдоонун абалы: кардар өзү текшерет жана даяр болгондо билдирме алат", "Repair status on the storefront: the customer checks it alone and gets a message when it is ready", "Vitrinde tamir durumu: müşteri kendisi kontrol eder, hazır olunca mesaj alır", "Vitrinada ta'mir holati: mijoz o'zi tekshiradi va tayyor bo'lganda xabar oladi"),
            T("Клиент вводит номер квитанции и последние 4 цифры телефона на вашей витрине и видит: принят, в работе, готов, выдан, срок и сумму к оплате. Когда ремонт готов, клиент получает сообщение в Telegram. Пример: Samsung A54 принят с предоплатой 500 сом, клиент по коду Р-0001 видит «В работе, срок 30.11», а потом «Готов, к доплате 1 200 сом» — и не звонит в магазин.",
              "Кардар витринаңызда квитанциянын номерин жана телефондун акыркы 4 цифрасын киргизет да, көрөт: кабыл алынды, иште, даяр, берилди, мөөнөт жана төлөнүүчү сумма. Оңдоо бүткөндө кардар Telegram'да билдирме алат. Мисал: Samsung A54 500 сом алдын ала төлөм менен кабыл алынды, кардар Р-0001 коду боюнча «Иште, мөөнөтү 30.11», андан соң «Даяр, кошумча төлөм 1 200 сом» деп көрөт — дүкөнгө чалбайт.",
              "The customer enters the receipt number and the last 4 digits of the phone on your storefront and sees: accepted, in progress, ready, handed over, the due date and the amount to pay. When the repair is ready, the customer gets a Telegram message. Example: a Samsung A54 is accepted with a prepayment of 500 som, by the code Р-0001 the customer sees “In progress, due 30.11” and then “Ready, 1 200 som left to pay” — and does not call the shop.",
              "Müşteri vitrininizde makbuz numarasını ve telefonun son 4 hanesini girer ve şunu görür: kabul edildi, devam ediyor, hazır, teslim edildi, süre ve ödenecek tutar. Tamir hazır olunca müşteri Telegram'da mesaj alır. Örnek: Samsung A54 500 som ön ödemeyle kabul edilir, müşteri Р-0001 koduyla «Devam ediyor, süre 30.11», sonra «Hazır, ödenecek 1 200 som» görür — mağazayı aramaz.",
              "Mijoz vitrinangizda kvitansiya raqamini va telefonning oxirgi 4 raqamini kiritadi va ko'radi: qabul qilindi, jarayonda, tayyor, berildi, muddat va to'lanadigan summa. Ta'mir tayyor bo'lganda mijoz Telegram'da xabar oladi. Misol: Samsung A54 500 so'm oldindan to'lov bilan qabul qilindi, mijoz Р-0001 kodi bo'yicha «Jarayonda, muddat 30.11», keyin «Tayyor, qo'shimcha to'lov 1 200 so'm» ni ko'radi — do'konga qo'ng'iroq qilmaydi."),
            [T("Нужны включённый модуль «Ремонт» и онлайн-витрина вашего магазина.", "«Оңдоо» модулу жана дүкөнүңүздүн онлайн-витринасы күйгүзүлгөн болушу керек.", "The “Repair” module and your shop's online storefront must be on.", "«Tamir» modülü ve mağazanızın çevrimiçi vitrini açık olmalıdır.", "«Ta'mir» moduli va do'koningizning onlayn vitrinasi yoqilgan bo'lishi kerak."),
             T("Принимайте устройства в «Ремонте» как обычно: на квитанции будет код и ссылка на витрину.", "Түзмөктөрдү «Оңдоодо» адаттагыдай кабыл алыңыз: квитанцияда код жана витринага шилтеме болот.", "Accept devices in “Repair” as usual: the receipt carries the code and the storefront link.", "Cihazları «Tamir»de her zamanki gibi kabul edin: makbuzda kod ve vitrin bağlantısı olur.", "Qurilmalarni «Ta'mir» da odatdagidek qabul qiling: kvitansiyada kod va vitrinaga havola bo'ladi."),
             T("Пока сервер NurCRM не прислал эту функцию, карточка помечена «Скоро».", "NurCRM сервери бул функцияны жибергенче карточкада «Жакында» деп белгиленет.", "Until the NurCRM server sends this function, the card is marked “Coming soon”.", "NurCRM sunucusu bu işlevi gönderene kadar kart «Yakında» olarak işaretlidir.", "NurCRM serveri bu funksiyani yuborguncha kartochka «Tez orada» deb belgilanadi.")],
            [RepairShops(), Electronics()])),
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
