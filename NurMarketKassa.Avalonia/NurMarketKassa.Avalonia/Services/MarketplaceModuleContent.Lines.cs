using System;
using System.Collections.Generic;
using System.Linq;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26 (замечание владельца: «описание новым функциям что он делает ты не добавил!!!»): ОДНА строка «что делает» для КАЖДОГО модуля сервера (GET public/modules/) —
// для плиток Маркетплейса, диалога «Тариф «Своё»», раздела «Аккаунт → Тариф» и тостов. Полное описание страницы — Specs в MarketplaceModuleContent.cs;
// здесь только короткая фраза без названия модуля (название рядом уже есть). Правило то же: новый модуль сервера = новая строка здесь (+ запись в Specs или OwnCard),
// иначе тест MarketplaceModuleLinesTests красный. Сервер своего описания пока не отдаёт; если начнёт (description / summary) — оно только запасное, наша строка главнее.
public static partial class MarketplaceModuleContent
{
    /// <summary>Значок и цвет модулей со СВОЕЙ карточкой Маркетплейса (Смета, Прокат, Ремонт, Trade-in): те же, что у их страниц (MarketplaceContent.*).</summary>
    private static readonly Dictionary<string, (string Icon, string Color)> OwnCardVisual = new(StringComparer.OrdinalIgnoreCase)
    {
        [ModuleIds.Estimate] = ("calculator-variant-outline", "#D97706"),
        [ModuleIds.Rental] = ("calendar-clock-outline", "#7C3AED"),
        [ModuleIds.Repair] = ("cog-outline", "#EA580C"),
        [ModuleIds.TradeIn] = ("cash-multiple", "#0891B2"),
    };

    /// <summary>Значок для строки списка (диалог «Своё», полоса тарифа): у модуля со своей карточкой — её значок, иначе значок из Specs, иначе запасной.</summary>
    public static string ListIconKey(string id) => OwnCardVisual.TryGetValue(id, out var o) ? o.Icon : IconKeyOf(id);

    /// <summary>Цвет плитки значка для строки списка (см. <see cref="ListIconKey"/>).</summary>
    public static string ListColor(string id) => OwnCardVisual.TryGetValue(id, out var o) ? o.Color : ColorOf(id);

    /// <summary>Есть своя однострочная фраза «что делает» (а не запасная от сервера).</summary>
    public static bool HasLine(string id) => Lines.ContainsKey(id);

    /// <summary>Коды, для которых написана фраза (для проверки полноты по ответу сервера).</summary>
    public static IReadOnlyCollection<string> LineIds => Lines.Keys;

    /// <summary>Одна строка «что делает» на языке программы. Наша главнее; нет — описание сервера (description / summary), если он его прислал; нет и его — null
    /// (вызывающий НЕ показывает пустую строку, а тест красный).</summary>
    public static string? LineOf(string id)
    {
        if (Lines.TryGetValue(id, out var make))
            return make();
        var server = PublicModules.Cached?.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Description;
        return string.IsNullOrWhiteSpace(server) ? null : server.Trim();
    }

    // 2026-11-26: Lines = CoreLines + функции без модуля сервера (MarketplaceModuleContent.Features.cs)
    private static readonly Dictionary<string, Func<string>> CoreLines = new(StringComparer.OrdinalIgnoreCase)
    {
        [ModuleIds.ScalesLabels] = () => T("Товары уходят на весы, весы печатают этикетку — касса сама читает вес.",
            "Товарлар таразыга жөнөтүлөт, таразы этикетка басат — касса салмакты өзү окуйт.",
            "Products go to the scale, the scale prints a label — the till reads the weight itself.",
            "Ürünler tartıya gider, terazi etiket basar — kasa ağırlığı kendisi okur.",
            "Tovarlar tarozilarga yuboriladi, tarozi etiketka chop etadi — kassa vaznni o'zi o'qiydi."),
        [ModuleIds.CarcassYield] = () => T("Купили тушу — вписали выход в %: на склад идёт вес для продажи, себестоимость кг пересчитывается.", // 2026-11-26: «Выход мяса при приходе»
            "Союшту сатып алдыңыз — чыгымды % менен жазасыз: кампага сатуучу салмак келет, килограммдын өздүк баасы кайра эсептелет.",
            "You buy a carcass and enter the yield in %: the stock gets the weight for sale and the cost per kg is recalculated.",
            "Karkas aldınız, verimi % olarak yazarsınız: depoya satış ağırlığı girer, kilogram maliyeti yeniden hesaplanır.",
            "Tushani sotib oldingiz va chiqishni % da yozasiz: omborga sotuv vazni kiradi, kilogramm tannarxi qayta hisoblanadi."),
        [ModuleIds.ExpiryBatches] = () => T("Помнит срок годности и партию каждого прихода, предупреждает о просрочке.",
            "Ар бир келген партиянын жарактуулук мөөнөтүн эстейт, мөөнөтү өткөндө эскертет.",
            "Remembers the expiry date and batch of each delivery and warns about expired goods.",
            "Her girişin son kullanma tarihini ve partisini hatırlar, süresi geçince uyarır.",
            "Har bir kirimning yaroqlilik muddati va partiyasini eslab qoladi, muddati o'tsa ogohlantiradi."),
        [ModuleIds.QuickItems] = () => T("Ходовые товары крупными кнопками на пустом чеке — без поиска и штрихкода.",
            "Эң көп сатылган товарлар бош чекте чоң баскычтар менен — издөөсүз жана штрихкодсуз.",
            "Best sellers as big buttons on an empty receipt — no search or barcode.",
            "Boş fişte en çok satan ürünler büyük düğmelerle — arama ve barkod olmadan.",
            "Bo'sh chekda eng ko'p sotiladigan tovarlar katta tugmalar bilan — qidirish va shtrixkodsiz."),
        [ModuleIds.FractionalQty] = () => T("Продажа метрами, литрами и тоннами: количество можно ввести дробным (2,5 м).",
            "Метр, литр жана тонна менен сатуу: санды бөлчөк киргизсе болот (2,5 м).",
            "Sell by metres, litres and tonnes: the quantity can be fractional (2.5 m).",
            "Metre, litre ve ton ile satış: miktar kesirli girilebilir (2,5 m).",
            "Metr, litr va tonna bilan sotish: miqdorni kasr kiritish mumkin (2,5 m)."),
        [ModuleIds.Packages] = () => T("Коробки и мешки: скан коробки добавляет в чек сразу все штуки.",
            "Куту жана баштыктар: кутуну сканерлесе чекке бардык даана бирдей кошулат.",
            "Boxes and bags: scanning a box adds all its pieces to the receipt at once.",
            "Kutu ve çuvallar: kutuyu okutmak tüm adetleri fişe bir seferde ekler.",
            "Qutilar va qoplar: qutini skanerlash barcha donalarni chekka birdan qo'shadi."),
        [ModuleIds.ClientPrice] = () => T("Постоянному клиенту (прорабу, мастеру) — оптовая цена автоматически.",
            "Туруктуу кардарга (прораб, уста) — дүң баа автоматтык түрдө.",
            "A regular client (foreman, mechanic) gets the wholesale price automatically.",
            "Düzenli müşteriye (usta, tamirci) toptan fiyat otomatik uygulanır.",
            "Doimiy mijozga (prorab, usta) — ulgurji narx avtomatik qo'yiladi."),
        [ModuleIds.PriceLevels] = () => T("Несколько цен на товар: розница, мастер, опт — по уровню клиента.",
            "Бир товарга бир нече баа: чекене, уста, дүң — кардардын деңгээлине жараша.",
            "Several prices per product: retail, mechanic, wholesale — by the client's level.",
            "Ürün başına birkaç fiyat: perakende, usta, toptan — müşteri seviyesine göre.",
            "Bitta tovarga bir nechta narx: chakana, usta, ulgurji — mijoz darajasiga qarab."),
        [ModuleIds.Estimate] = () => T("Считает материал на комнату, собирает смету, печатает и кладёт в чек.",
            "Бөлмөгө материалды эсептейт, смета түзөт, басат жана чекке салат.",
            "Works out the material for a room, builds an estimate, prints it and puts it in the receipt.",
            "Oda için malzemeyi hesaplar, keşif hazırlar, yazdırır ve fişe koyar.",
            "Xona uchun materialni hisoblaydi, smeta tuzadi, chop etadi va chekka qo'yadi."),
        [ModuleIds.MaterialCalc] = () => T("Размеры комнаты → сколько плитки, ламината или краски взять, с запасом.",
            "Бөлмөнүн өлчөмү → канча плитка, ламинат же боёк алуу керек, запас менен.",
            "Room size → how much tile, laminate or paint to take, with a reserve.",
            "Oda ölçüsü → ne kadar fayans, laminat veya boya alınacağı, yedekle birlikte.",
            "Xona o'lchami → qancha plitka, laminat yoki bo'yoq olish kerak, zaxira bilan."),
        [ModuleIds.Rental] = () => T("Выдача вещей и инструмента в прокат: залог, срок, возврат, штраф за просрочку.",
            "Буюм жана аспапты прокатка берүү: күрөө, мөөнөт, кайтаруу, кечиктирүү үчүн айып.",
            "Renting out items and tools: deposit, term, return, late penalty.",
            "Eşya ve alet kiralama: depozito, süre, iade, gecikme cezası.",
            "Buyum va asbobni prokatga berish: garov, muddat, qaytarish, kechikish jarimasi."),
        [ModuleIds.Variants] = () => T("Один товар с размерами и цветами: у каждого варианта свой штрихкод и остаток.",
            "Бир товар өлчөмдөрү жана түстөрү менен: ар бир варианттын өз штрихкоду жана калдыгы бар.",
            "One product with sizes and colours: each variant has its own barcode and stock.",
            "Beden ve renkli tek ürün: her varyantın kendi barkodu ve stoğu var.",
            "O'lcham va rangli bitta tovar: har bir variantning o'z shtrixkodi va qoldig'i bor."),
        [ModuleIds.Consultant] = () => T("Кто помог покупателю и какой процент ему с продажи — в окне оплаты.",
            "Сатып алуучуга ким жардам берди жана ага сатуудан канча пайыз — төлөм терезесинде.",
            "Who helped the customer and what percentage they earn — in the payment window.",
            "Müşteriye kimin yardım ettiği ve satıştan yüzdesi — ödeme penceresinde.",
            "Xaridorga kim yordam bergani va unga sotuvdan qancha foiz — to'lov oynasida."),
        [ModuleIds.Exchange] = () => T("Вернул одно — взял другое: касса сама считает доплату или возврат денег.",
            "Бирин кайтарды — экинчисин алды: касса кошумча төлөмдү же акчаны кайтарууну өзү эсептейт.",
            "Return one item, take another: the till works out the extra payment or the refund.",
            "Birini iade et, diğerini al: kasa ek ödemeyi veya para iadesini hesaplar.",
            "Birini qaytardi — boshqasini oldi: kassa qo'shimcha to'lov yoki pulni qaytarishni hisoblaydi."),
        [ModuleIds.Serials] = () => T("Серийный номер или IMEI записывается в строку чека при продаже.",
            "Сатууда сериялык номер же IMEI чектин сабына жазылат.",
            "The serial number or IMEI is written into the receipt line at the sale.",
            "Satışta seri numarası veya IMEI fiş satırına yazılır.",
            "Sotishda seriya raqami yoki IMEI chek qatoriga yoziladi."),
        [ModuleIds.Warranty] = () => T("Срок гарантии пишется в чек; при возврате по гарантии касса проверяет срок.",
            "Кепилдик мөөнөтү чекке жазылат; кепилдик боюнча кайтарууда касса мөөнөттү текшерет.",
            "The warranty term goes on the receipt; on a warranty return the till checks the term.",
            "Garanti süresi fişe yazılır; garanti iadesinde kasa süreyi kontrol eder.",
            "Kafolat muddati chekka yoziladi; kafolat bo'yicha qaytarishda kassa muddatni tekshiradi."),
        [ModuleIds.Repair] = () => T("Приём в ремонт: квитанция, статусы, выдача, сообщение клиенту «готово».",
            "Оңдоого кабыл алуу: квитанция, статустар, берүү, кардарга «даяр» билдирүүсү.",
            "Repair intake: receipt, statuses, hand-over, a “ready” message to the customer.",
            "Tamire kabul: makbuz, durumlar, teslim, müşteriye «hazır» mesajı.",
            "Ta'mirlashga qabul: kvitansiya, statuslar, topshirish, mijozga «tayyor» xabari."),
        [ModuleIds.TradeIn] = () => T("Приём б/у у клиента: оценка, выплата из кассы или зачёт в покупку.",
            "Кардардан б/у кабыл алуу: баалоо, кассадан төлөө же сатып алууга эсептөө.",
            "Taking used items from customers: appraisal, payout from the till or credit to a purchase.",
            "Müşteriden ikinci el alım: değerleme, kasadan ödeme veya alışverişe mahsup.",
            "Mijozdan b/u qabul qilish: baholash, kassadan to'lov yoki xaridga hisobga olish."),
        [ModuleIds.PartNumbers] = () => T("Поиск детали по каталожному номеру и OEM, в том числе «бренд + номер».",
            "Тетикти каталог номери жана OEM боюнча издөө, анын ичинде «бренд + номер».",
            "Find a part by catalogue or OEM number, including “brand + number”.",
            "Parçayı katalog veya OEM numarasıyla bulma, «marka + numara» dahil.",
            "Qismni katalog va OEM raqami bo'yicha qidirish, jumladan «brend + raqam»."),
        [ModuleIds.CrossAnalogs] = () => T("Аналоги и кроссы других брендов — предложить замену, если детали нет.",
            "Башка бренддердин аналогдору жана кросстору — тетик жок болсо алмаштырууну сунуштоо.",
            "Analogues and cross-references from other brands — offer a substitute when a part is out.",
            "Diğer markaların muadilleri ve çaprazları — parça yoksa alternatif önerin.",
            "Boshqa brendlarning analoglari va krosslari — qism bo'lmasa almashtirishni taklif qilish."),
        ["condition"] = () => T("Отметка «новая» или «Б/У» в карточке детали — видна кассиру и покупателю.",
            "Тетиктин карточкасында «жаңы» же «Б/У» белгиси — кассирге жана сатып алуучуга көрүнөт.",
            "A “new” or “used” mark in the part card — visible to the cashier and the buyer.",
            "Parça kartında «yeni» veya «ikinci el» işareti — kasiyer ve alıcı görür.",
            "Qism kartasida «yangi» yoki «B/U» belgisi — kassir va xaridorga ko'rinadi."),
        ["storage_cell"] = () => T("Ячейка склада у детали: стеллаж и полка — быстрее найти товар.",
            "Тетиктин кампа уячасы: стеллаж жана текче — товарды тезирээк табуу.",
            "A storage cell for each part: rack and shelf — find items faster.",
            "Parçanın depo gözü: raf ve bölme — ürünü daha hızlı bulun.",
            "Qismning ombor katagi: javon va tokcha — tovarni tezroq topish."),
        ["sto_price"] = () => T("Особая цена СТО для мастеров: касса ставит её клиенту «СТО / мастер».",
            "Усталар үчүн өзгөчө СТО баасы: касса аны «СТО / уста» кардарына өзү коёт.",
            "A special garage price for mechanics: the till applies it to a “Garage / mechanic” client.",
            "Ustalara özel servis fiyatı: kasa bunu «Servis / usta» müşterisine uygular.",
            "Ustalar uchun maxsus STO narxi: kassa uni «STO / usta» mijozga qo'yadi."),
        ["return_rules"] = () => T("Срок возврата и запрет на установленные детали — касса проверяет сама.",
            "Кайтаруу мөөнөтү жана орнотулган тетиктерге тыюу — касса өзү текшерет.",
            "The return period and the ban on installed parts — the till checks them itself.",
            "İade süresi ve takılmış parçalara yasak — kasa kendisi kontrol eder.",
            "Qaytarish muddati va o'rnatilgan qismlarga taqiq — kassaning o'zi tekshiradi."),
        [ModuleIds.CustomerOrders] = () => T("Заказ детали под клиента с предоплатой, выдача, когда деталь пришла.",
            "Кардарга тетикти алдын ала төлөм менен заказ кылуу, тетик келгенде берүү.",
            "Order a part for a customer with a prepayment, hand it over when it arrives.",
            "Müşteri için ön ödemeli parça siparişi, parça gelince teslim.",
            "Qismni mijozga oldindan to'lov bilan buyurtma qilish, kelganda topshirish."),
        [BazaarModules.CategoryTiles] = () => T("Главный экран — крупные плитки категорий: один тап, и видны товары.",
            "Башкы экран — категориялардын чоң плиткалары: бир тийүү, товарлар көрүнөт.",
            "The main screen shows big category tiles: one tap and the products appear.",
            "Ana ekranda büyük kategori kutuları: tek dokunuş, ürünler görünür.",
            "Asosiy ekranda kategoriyalarning katta plitkalari: bir bosish — tovarlar ko'rinadi."),
        [BazaarModules.VolumePrice] = () => T("Цена от количества: «от 10 шт — дешевле», подпись «Опт от N» в чеке.",
            "Санына жараша баа: «10 даанадан — арзан», чекте «Дүң, мин. N» жазуусу.",
            "Price by quantity: “from 10 pcs — cheaper”, with a “Bulk from N” label in the receipt.",
            "Miktara göre fiyat: «10 adetten — daha ucuz», fişte «Toptan, en az N» yazısı.",
            "Miqdorga qarab narx: «10 dona dan — arzonroq», chekda «Ulgurji, kamida N» yozuvi."),
        [BazaarModules.Haggle] = () => T("Торг в одно касание: «До 10 / 50 / 100» или «Договорились за …».",
            "Бир тийүүдө соодалашуу: «10 / 50 / 100 чейин» же «Макулдашылган сумма …».",
            "Haggling in one tap: “To 10 / 50 / 100” or “Agreed for …”.",
            "Tek dokunuşla pazarlık: «10 / 50 / 100'e kadar» veya «Anlaşılan tutar …».",
            "Bir bosishda savdolashuv: «10 / 50 / 100 gacha» yoki «Kelishilgan summa …»."),
        [BazaarModules.Currencies] = () => T("Оплата в USD, RUB и CNY по курсу компании — сдача в сомах.",
            "USD, RUB жана CNY менен төлөм компаниянын курсу боюнча — кайтарым сом менен.",
            "Payment in USD, RUB and CNY at the company's rate — change in som.",
            "USD, RUB ve CNY ile ödeme, şirketin kuruyla — para üstü somla.",
            "USD, RUB va CNY da to'lov kompaniya kursi bo'yicha — qaytim so'mda."),
        [BazaarModules.InvoiceCargo] = () => T("Накладная A4 с колонками «Мест» и «Брутто» и блоком «Карго» — для опта.",
            "«Орун» жана «Брутто» тилкелери бар A4 накладная жана «Карго» блогу — дүң соода үчүн.",
            "An A4 invoice with “Places” and “Gross” columns and a “Cargo” block — for wholesale.",
            "«Yer» ve «Brüt» sütunlu A4 irsaliye ve «Kargo» bloğu — toptan satış için.",
            "«Joy» va «Brutto» ustunli A4 hisob-faktura va «Kargo» bloki — ulgurji savdo uchun."),
        [ModuleIds.RepairStatusSite] = () => T("Клиент сам проверяет ремонт на вашей витрине по коду квитанции — звонить в магазин не нужно.", // 2026-11-26: «Статус ремонта на витрине»
            "Кардар оңдоонун абалын витринаңыздан квитанциянын коду боюнча өзү текшерет — дүкөнгө чалуунун кереги жок.",
            "The customer checks the repair on your storefront by the receipt code — no need to call the shop.",
            "Müşteri tamiri vitrininizde makbuz koduyla kendisi kontrol eder — mağazayı aramaya gerek yok.",
            "Mijoz ta'mirni vitrinangizda kvitansiya kodi bo'yicha o'zi tekshiradi — do'konga qo'ng'iroq qilish shart emas."),
    };
}
