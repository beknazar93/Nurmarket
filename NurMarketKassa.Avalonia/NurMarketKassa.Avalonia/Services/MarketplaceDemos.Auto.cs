﻿using System.Collections.Generic;
using static NurMarketKassa.AvaloniaHost.Services.MockDsl;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26: демонстрации работы и «Что получите» — автозапчасти (часть 2) и сфера «Базар». Правила: 3–5 шагов; подпись шага «что нажать / что видно / результат» на 5 языках (T);
// кадр помещается в рамку 640×340 (≈5 строк или полей; тест MarketplaceDemosTests измеряет высоту на всех языках); выдуманные имена и суммы, без реальных данных;
// содержание — только то, что функция делает по тексту её карточки (MarketplaceModuleContent) и коду.
public static partial class MarketplaceDemos
{
    // ── общие подписи группы C ──
    private static string AuTill() => T("Касса", "Касса", "Till", "Kasa", "Kassa");
    private static string AuReceipt() => T("Чек", "Чек", "Receipt", "Fiş", "Chek");
    private static string AuCard() => T("Карточка товара", "Товардын карточкасы", "Product card", "Ürün kartı", "Tovar kartasi");
    private static string AuSearch() => T("Поиск", "Издөө", "Search", "Ara", "Qidiruv");
    private static string AuTotal() => T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami");
    private static string AuSave() => T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");
    private static string AuSom() => T("сом", "сом", "som", "som", "so'm");

    private static void AddGroupC(Dictionary<string, MarketplaceDemoEntry> d)
    {
        AuCondition(d);
        AuStorageCell(d);
        AuStoPrice(d);
        AuReturnRules(d);
        AuCustomerOrders(d);
        AuCategoryTiles(d);
        AuVolumePrice(d);
        AuHaggle(d);
        AuCurrencies(d);
        AuInvoiceCargo(d);
    }

    // ───────────────────────── Автозапчасти ─────────────────────────

    private static void AuCondition(Dictionary<string, MarketplaceDemoEntry> d) => d["condition"] = new(() =>
    [
        Step(T("1. В карточке детали отмечаете состояние — «Б/У»", "1. Тетиктин карточкасында абалын белгилейсиз — «Б/У»", "1. In the part card you mark the condition — “Used”", "1. Parça kartında durumu işaretlersiniz — «İkinci el»", "1. Qism kartasida holatini belgilaysiz — «B/U»"),
            T("Карточка детали", "Тетиктин карточкасы", "Part card", "Parça kartı", "Qism kartasi"),
            Input(T("Название", "Аталышы", "Name", "Ad", "Nomi"), T("Фара левая Toyota", "Toyota сол фарасы", "Toyota left headlight", "Toyota sol far", "Toyota chap fara")),
            Chips(1, T("Новая", "Жаңы", "New", "Yeni", "Yangi"), T("Б/У", "Б/У", "Used", "İkinci el", "B/U")),
            Btn(AuSave(), true)),
        Step(T("2. В списке сразу видно: новая деталь или Б/У", "2. Тизмеде жаңы же Б/У экени дароо көрүнөт", "2. The list shows at once whether a part is new or used", "2. Listede parçanın yeni mi ikinci el mi olduğu hemen görünür", "2. Ro'yxatda qism yangi yoki B/U ekani darhol ko'rinadi"),
            AuTill(),
            Input(AuSearch(), T("Фара Toyota", "Toyota фарасы", "Toyota headlight", "Toyota far", "Toyota fara")),
            Row(T("Фара левая Toyota", "Toyota сол фарасы", "Toyota left headlight", "Toyota sol far", "Toyota chap fara"), "3 200", MockTone.Normal, false, T("Новая", "Жаңы", "New", "Yeni", "Yangi")),
            Row(T("Фара левая Toyota", "Toyota сол фарасы", "Toyota left headlight", "Toyota sol far", "Toyota chap fara"), "1 400", MockTone.Warn, true, T("Б/У", "Б/У", "Used", "İkinci el", "B/U"))),
        Step(T("3. Деталь с разборки в чеке — покупатель видит «Б/У»", "3. Чекте бузулган унаадан алынган тетик — сатып алуучу «Б/У» деп көрөт", "3. A used part in the receipt — the buyer sees “Used”", "3. Fişteki sökümden parça — alıcı «İkinci el» görür", "3. Chekdagi qism — xaridor «B/U» ni ko'radi"),
            AuReceipt(),
            Row(T("Фара левая Toyota", "Toyota сол фарасы", "Toyota left headlight", "Toyota sol far", "Toyota chap fara"), "1 400", MockTone.Warn, true, T("Б/У", "Б/У", "Used", "İkinci el", "B/U")),
            Total(AuTotal(), "1 400"),
            Note(T("Подержанную деталь не спутать с новой", "Колдонулган тетикти жаңысы менен чаташтырбайсыз", "A used part is not mixed up with a new one", "İkinci el parça yeniyle karışmaz", "B/U qism yangisi bilan adashmaydi"), MockTone.Good)),
    ],
    () =>
    [
        Gain("star-half-full", T("Новая или Б/У", "Жаңы же Б/У", "New or used", "Yeni veya ikinci el", "Yangi yoki B/U"),
            T("Состояние записано в карточке детали — его видно в списке и в чеке.", "Абалы тетиктин карточкасында жазылган — тизмеде жана чекте көрүнөт.", "The condition is stored in the part card — it shows in the list and the receipt.", "Durum parça kartında kayıtlıdır — listede ve fişte görünür.", "Holat qism kartasida yozilgan — ro'yxatda va chekda ko'rinadi.")),
        Gain("eye-outline", T("Без путаницы", "Чаташуусуз", "No mix-ups", "Karışıklık yok", "Adashishsiz"),
            T("Фара с разборки не путается с новой — ни у кассира, ни у покупателя.", "Бузулган унаадан алынган фара жаңысы менен чаташпайт — кассирде да, сатып алуучуда да.", "A headlight from a dismantled car is not confused with a new one by cashier or buyer.", "Sökümden çıkan far, ne kasiyer ne alıcı için yenisiyle karışmaz.", "Qismlardan olingan fara yangisi bilan adashmaydi — kassirda ham, xaridorda ham.")),
        Gain("check-circle-outline", T("Честная продажа", "Чынчыл сатуу", "An honest sale", "Dürüst satış", "Halol sotuv"),
            T("Покупатель заранее знает, что берёт, — меньше споров и возвратов.", "Сатып алуучу эмнени алганын алдын ала билет — талаш жана кайтаруу азыраак.", "The buyer knows in advance what they take — fewer disputes and returns.", "Alıcı ne aldığını önceden bilir — daha az tartışma ve iade.", "Xaridor nima olayotganini oldindan biladi — nizo va qaytarish kamroq.")),
    ]);

    private static void AuStorageCell(Dictionary<string, MarketplaceDemoEntry> d) => d["storage_cell"] = new(() =>
    [
        Step(T("1. В карточке детали пишете ячейку: стеллаж и полка", "1. Тетиктин карточкасына уячаны жазасыз: текче жана текчеси", "1. In the part card you write the cell: rack and shelf", "1. Parça kartına gözü yazarsınız: raf ve bölme", "1. Qism kartasiga katakni yozasiz: stellaj va tokcha"),
            AuCard(),
            Input(T("Название", "Аталышы", "Name", "Ad", "Nomi"), T("Колодки передние", "Алдыңкы колодкалар", "Front brake pads", "Ön balatalar", "Old kolodkalar")),
            Input(T("Ячейка склада", "Кампанын уячасы", "Storage cell", "Depo gözü", "Ombor katagi"), T("Стеллаж 3, полка B", "Текче 3, текче B", "Rack 3, shelf B", "Raf 3, bölme B", "Stellaj 3, tokcha B"), true),
            Btn(AuSave(), true)),
        Step(T("2. Ищете деталь — ячейка видна рядом с ней", "2. Тетикти издейсиз — уяча анын жанында көрүнөт", "2. You search for the part — the cell is shown next to it", "2. Parçayı ararsınız — göz yanında görünür", "2. Qismni qidirasiz — katak uning yonida ko'rinadi"),
            AuTill(),
            Input(AuSearch(), T("Колодки", "Колодкалар", "Brake pads", "Balata", "Kolodka")),
            Row(T("Колодки передние", "Алдыңкы колодкалар", "Front brake pads", "Ön balatalar", "Old kolodkalar"), "2 400", MockTone.Normal, true, T("Стеллаж 3, полка B", "Текче 3, текче B", "Rack 3, shelf B", "Raf 3, bölme B", "Stellaj 3, tokcha B")),
            Row(T("Колодки задние", "Арткы колодкалар", "Rear brake pads", "Arka balatalar", "Orqa kolodkalar"), "1 900", MockTone.Normal, false, T("Стеллаж 3, полка C", "Текче 3, текче C", "Rack 3, shelf C", "Raf 3, bölme C", "Stellaj 3, tokcha C"))),
        Step(T("3. Кассир сразу идёт к нужной полке — склад обходить не надо", "3. Кассир түз эле керектүү текчеге барат — кампаны кыдырбайт", "3. The cashier goes straight to the shelf — no walking the warehouse", "3. Kasiyer doğrudan rafa gider — depoyu dolaşmaz", "3. Kassir to'g'ri tokchaga boradi — omborni aylanmaydi"),
            AuTill(),
            Row(T("Колодки передние", "Алдыңкы колодкалар", "Front brake pads", "Ön balatalar", "Old kolodkalar"), "2 400", MockTone.Normal, true, T("Стеллаж 3, полка B", "Текче 3, текче B", "Rack 3, shelf B", "Raf 3, bölme B", "Stellaj 3, tokcha B")),
            Note(T("Подсказка — прямо в карточке: Стеллаж 3, полка B", "Кеңеш — түз эле карточкада: Текче 3, текче B", "A hint right in the card: Rack 3, shelf B", "İpucu doğrudan kartta: Raf 3, bölme B", "Maslahat — to'g'ridan-to'g'ri kartada: Stellaj 3, tokcha B"), MockTone.Accent)),
        Step(T("4. Деталь переложили — меняете ячейку в карточке", "4. Тетикти башка жерге койдуңуз — карточкада уячаны өзгөртөсүз", "4. The part was moved — you change the cell in the card", "4. Parça taşındı — kartta gözü değiştirirsiniz", "4. Qism ko'chirildi — kartada katakni o'zgartirasiz"),
            AuCard(),
            Input(T("Ячейка склада", "Кампанын уячасы", "Storage cell", "Depo gözü", "Ombor katagi"), T("Стеллаж 5, полка A", "Текче 5, текче A", "Rack 5, shelf A", "Raf 5, bölme A", "Stellaj 5, tokcha A"), true),
            Note(T("Новая ячейка сохранена — поиск покажет её", "Жаңы уяча сакталды — издөө аны көрсөтөт", "The new cell is saved — search will show it", "Yeni göz kaydedildi — arama onu gösterir", "Yangi katak saqlandi — qidiruv uni ko'rsatadi"), MockTone.Good)),
    ],
    () =>
    [
        Gain("view-grid-outline", T("Знаете, где лежит", "Кайда жатканын билесиз", "You know where it lies", "Nerede durduğunu bilirsiniz", "Qayerda yotganini bilasiz"),
            T("У детали записаны стеллаж и полка — подсказка прямо в карточке.", "Тетиктин текчеси жана текче бөлүмү жазылган — кеңеш түз эле карточкада.", "The rack and shelf are recorded — a hint right in the card.", "Raf ve bölme kayıtlıdır — ipucu doğrudan kartta.", "Stellaj va tokcha yozilgan — maslahat to'g'ridan-to'g'ri kartada.")),
        Gain("clock-outline", T("Быстрее к покупателю", "Сатып алуучуга тезирээк", "Faster to the buyer", "Alıcıya daha hızlı", "Xaridorga tezroq"),
            T("Кассир не обходит склад в поисках детали.", "Кассир деталды издеп кампаны кыдырбайт.", "The cashier does not walk the warehouse looking for the part.", "Kasiyer parçayı aramak için depoyu dolaşmaz.", "Kassir qismni izlab omborni aylanmaydi.")),
        Gain("check-circle-outline", T("Порядок на складе", "Кампада тартип", "Order in the warehouse", "Depoda düzen", "Omborda tartib"),
            T("Новый сотрудник находит деталь так же быстро, как опытный.", "Жаңы кызматкер тетикти тажрыйбалуудай тез табат.", "A new employee finds the part as fast as an experienced one.", "Yeni çalışan parçayı deneyimli biri kadar hızlı bulur.", "Yangi xodim qismni tajribali xodimdek tez topadi.")),
    ]);

    private static void AuStoPrice(Dictionary<string, MarketplaceDemoEntry> d) => d["sto_price"] = new(() =>
    [
        Step(T("1. В карточке детали указываете цену СТО (оптовую)", "1. Тетиктин карточкасында СТО баасын (дүң) көрсөтөсүз", "1. In the part card you enter the garage (wholesale) price", "1. Parça kartına servis (toptan) fiyatını girersiniz", "1. Qism kartasida STO (ulgurji) narxini kiritasiz"),
            AuCard(),
            Row(T("Фильтр масляный: розничная цена", "Май чыпкасы: чекене баасы", "Oil filter: retail price", "Yağ filtresi: perakende fiyat", "Moy filtri: chakana narx"), "1 100"),
            Input(T("Цена СТО", "СТО баасы", "Garage price", "Servis fiyatı", "STO narxi"), "900", true),
            Btn(AuSave(), true)),
        Step(T("2. В чеке выбираете клиента, отмеченного «СТО / мастер»", "2. Чекте «СТО / уста» деп белгиленген кардарды тандайсыз", "2. In the receipt you pick a client marked “Garage / mechanic”", "2. Fişte «Servis / usta» işaretli müşteriyi seçersiniz", "2. Chekda «STO / usta» deb belgilangan mijozni tanlaysiz"),
            AuReceipt(),
            Input(T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), T("СТО «Асан»", "СТО «Асан»", "Garage “Asan”", "Servis «Asan»", "STO «Asan»")),
            Chips(1, T("Розница", "Чекене", "Retail", "Perakende", "Chakana"), T("СТО / мастер", "СТО / уста", "Garage / mechanic", "Servis / usta", "STO / usta"))),
        Step(T("3. Касса сама ставит цену СТО: 900 вместо 1 100", "3. Касса СТО баасын өзү коёт: 1 100 ордуна 900", "3. The till sets the garage price by itself: 900 instead of 1,100", "3. Kasa servis fiyatını kendisi koyar: 1 100 yerine 900", "3. Kassa STO narxini o'zi qo'yadi: 1 100 o'rniga 900"),
            AuReceipt(),
            Row(T("Фильтр масляный", "Май чыпкасы", "Oil filter", "Yağ filtresi", "Moy filtri"), "900", MockTone.Good, true, T("Цена СТО (розница 1 100)", "СТО баасы (чекене 1 100)", "Garage price (retail 1,100)", "Servis fiyatı (perakende 1 100)", "STO narxi (chakana 1 100)")),
            Total(AuTotal(), "900")),
        Step(T("4. Нет цены СТО — остаётся розничная, кассир видит подсказку", "4. СТО баасы жок — чекене калат, кассир кеңешти көрөт", "4. No garage price — retail stays, the cashier sees a hint", "4. Servis fiyatı yok — perakende kalır, kasiyer ipucu görür", "4. STO narxi yo'q — chakana qoladi, kassir maslahat ko'radi"),
            AuReceipt(),
            Row(T("Свеча зажигания", "От алдыруу шамы", "Spark plug", "Buji", "Sham"), "450", MockTone.Normal, true, T("Розничная цена", "Чекене баасы", "Retail price", "Perakende fiyat", "Chakana narx")),
            Note(T("Цены СТО нет или она выше розницы — осталась розничная", "СТО баасы жок же чекенеден жогору — чекене калды", "No garage price or it is above retail — retail stays", "Servis fiyatı yok veya perakendeden yüksek — perakende kaldı", "STO narxi yo'q yoki chakanadan yuqori — chakana qoldi"), MockTone.Warn)),
    ],
    () =>
    [
        Gain("percent", T("Цена для мастеров", "Усталар үчүн баа", "A price for mechanics", "Ustalar için fiyat", "Ustalar uchun narx"),
            T("Клиенту «СТО / мастер» касса сама ставит оптовую цену — без ручного пересчёта.", "«СТО / уста» кардарына касса дүң баасын өзү коёт — кол менен кайра эсептебейсиз.", "For a “Garage / mechanic” client the till sets the wholesale price itself.", "«Servis / usta» müşteriye kasa toptan fiyatı kendisi koyar.", "«STO / usta» mijoziga kassa ulgurji narxni o'zi qo'yadi.")),
        Gain("shield-check-outline", T("Без ошибок в цене", "Баада ката жок", "No price mistakes", "Fiyatta hata yok", "Narxda xato yo'q"),
            T("Если цены СТО нет или она выше розницы, остаётся розничная.", "СТО баасы жок же чекенеден жогору болсо, чекене калат.", "If there is no garage price or it is above retail, retail stays.", "Servis fiyatı yoksa veya perakendeden yüksekse perakende kalır.", "STO narxi yo'q yoki chakanadan yuqori bo'lsa, chakana qoladi.")),
        Gain("check-circle-outline", T("Мастера довольны", "Усталар ыраазы", "Mechanics are happy", "Ustalar memnun", "Ustalar mamnun"),
            T("Постоянные мастера всегда получают свою цену — вернутся снова.", "Туруктуу усталар ар дайым өз баасын алат — кайра келишет.", "Regular mechanics always get their price and come back.", "Düzenli ustalar hep kendi fiyatını alır, tekrar gelir.", "Doimiy ustalar doim o'z narxini oladi — yana keladi.")),
    ]);

    private static void AuReturnRules(Dictionary<string, MarketplaceDemoEntry> d) => d["return_rules"] = new(() =>
    [
        Step(T("1. В настройках сферы задаёте срок возврата и запрет на установленные детали", "1. Чөйрө жөндөөлөрүндө кайтаруу мөөнөтүн жана орнотулган тетиктерге тыюуну коёсуз", "1. In the sphere settings you set the return period and the ban on installed parts", "1. Alan ayarlarında iade süresini ve takılmış parça yasağını belirlersiniz", "1. Soha sozlamalarida qaytarish muddati va o'rnatilgan qismlarga taqiqni belgilaysiz"),
            T("Правила возврата", "Кайтаруу эрежелери", "Return rules", "İade kuralları", "Qaytarish qoidalari"),
            Input(T("Срок возврата, дней", "Кайтаруу мөөнөтү, күн", "Return period, days", "İade süresi, gün", "Qaytarish muddati, kun"), "14", true),
            Toggle(T("Не принимать установленные детали и электрику", "Орнотулган тетиктерди жана электриканы кабыл албоо", "Do not accept installed parts and electrics", "Takılmış parça ve elektrik kabul edilmez", "O'rnatilgan qism va elektrikni qabul qilmaslik"), true, true),
            Btn(AuSave(), true)),
        Step(T("2. Возврат в срок: деталь куплена 5 дней назад — касса разрешает", "2. Мөөнөттө кайтаруу: тетик 5 күн мурун сатылган — касса уруксат берет", "2. A return in time: bought 5 days ago — the till allows it", "2. Süresinde iade: 5 gün önce alınmış — kasa izin verir", "2. Muddatida qaytarish: 5 kun oldin sotilgan — kassa ruxsat beradi"),
            T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
            Row(T("Генератор", "Генератор", "Alternator", "Alternatör", "Generator"), "6 500", MockTone.Normal, true, T("Куплен 5 дней назад", "5 күн мурун сатылган", "Bought 5 days ago", "5 gün önce alındı", "5 kun oldin sotilgan")),
            Note(T("Возврат возможен: срок 14 дней не истёк", "Кайтаруу мүмкүн: 14 күндүк мөөнөт өтө элек", "A return is possible: the 14-day period has not passed", "İade mümkün: 14 günlük süre dolmadı", "Qaytarish mumkin: 14 kunlik muddat o'tmagan"), MockTone.Good),
            Btn(T("Оформить возврат", "Кайтарууну тариздөө", "Process the return", "İadeyi işle", "Qaytarishni rasmiylashtirish"))),
        Step(T("3. Деталь куплена 20 дней назад — касса не даст оформить обычный возврат", "3. Тетик 20 күн мурун сатылган — касса кадимки кайтарууга жол бербейт", "3. Bought 20 days ago — the till will not allow an ordinary return", "3. 20 gün önce alınmış — kasa olağan iadeye izin vermez", "3. 20 kun oldin sotilgan — kassa oddiy qaytarishga yo'l qo'ymaydi"),
            T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
            Row(T("Генератор", "Генератор", "Alternator", "Alternatör", "Generator"), "6 500", MockTone.Bad, true, T("Куплен 20 дней назад", "20 күн мурун сатылган", "Bought 20 days ago", "20 gün önce alındı", "20 kun oldin sotilgan")),
            Note(T("Срок возврата 14 дней истёк — обычный возврат не оформить", "14 күндүк кайтаруу мөөнөтү өттү — кадимки кайтарууну тариздөөгө болбойт", "The 14-day return period has passed — no ordinary return", "14 günlük iade süresi doldu — olağan iade yapılamaz", "14 kunlik qaytarish muddati o'tdi — oddiy qaytarish mumkin emas"), MockTone.Bad)),
        Step(T("4. Установленная деталь и электрика — возврат не принимается", "4. Орнотулган тетик жана электрика — кайтаруу кабыл алынбайт", "4. An installed part or electrics — the return is not accepted", "4. Takılmış parça ve elektrik — iade kabul edilmez", "4. O'rnatilgan qism va elektr — qaytarish qabul qilinmaydi"),
            T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
            Row(T("Датчик кислорода", "Кычкылтек датчиги", "Oxygen sensor", "Oksijen sensörü", "Kislorod datchigi"), "3 100", MockTone.Bad, true, T("Установлен на авто", "Унаага орнотулган", "Installed on the car", "Araca takılmış", "Mashinaga o'rnatilgan")),
            Note(T("Установленные детали и электрика не принимаются", "Орнотулган тетиктер жана электрика кабыл алынбайт", "Installed parts and electrics are not accepted", "Takılmış parça ve elektrik kabul edilmez", "O'rnatilgan qism va elektr qabul qilinmaydi"), MockTone.Bad)),
    ],
    () =>
    [
        Gain("undo", T("Срок проверяет касса", "Мөөнөттү касса текшерет", "The till checks the period", "Süreyi kasa kontrol eder", "Muddatni kassa tekshiradi"),
            T("Срок возврата (по умолчанию 14 дней) касса считает сама, а не кассир по памяти.", "Кайтаруу мөөнөтүн (демейки 14 күн) касса өзү эсептейт, кассир эстебестен.", "The till counts the return period (14 days by default), not the cashier from memory.", "İade süresini (varsayılan 14 gün) kasiyer değil kasa sayar.", "Qaytarish muddatini (odatda 14 kun) kassir emas, kassa o'zi hisoblaydi.")),
        Gain("shield-lock-outline", T("Нет спорных возвратов", "Талаштуу кайтаруулар жок", "No disputed returns", "Tartışmalı iade yok", "Bahsli qaytarishlar yo'q"),
            T("Установленные детали и электрика не принимаются — запрет в настройках.", "Орнотулган тетиктер жана электрика кабыл алынбайт — тыюу жөндөөлөрдө.", "Installed parts and electrics are refused — the ban is in the settings.", "Takılmış parça ve elektrik reddedilir — yasak ayarlardadır.", "O'rnatilgan qism va elektr rad etiladi — taqiq sozlamalarda.")),
        Gain("check-circle-outline", T("Одинаково для всех", "Бардыгы үчүн бирдей", "The same for everyone", "Herkes için aynı", "Hamma uchun bir xil"),
            T("Правила действуют на любой кассе и для любого кассира.", "Эрежелер ар бир кассада жана ар бир кассир үчүн иштейт.", "The rules work on every till and for every cashier.", "Kurallar her kasada ve her kasiyer için geçerlidir.", "Qoidalar har bir kassada va har bir kassir uchun amal qiladi.")),
    ]);

    private static void AuCustomerOrders(Dictionary<string, MarketplaceDemoEntry> d) => d["customer_orders"] = new(() =>
    [
        Step(T("1. Нужной детали нет — нажимаете «Заказ под клиента»", "1. Керектүү тетик жок — «Кардарга заказ» баскычын басасыз", "1. The part is out — press “Customer order”", "1. Parça yok — «Müşteri siparişi»ne basarsınız", "1. Kerakli qism yo'q — «Mijoz uchun buyurtma» ni bosasiz"),
            AuTill(),
            Input(AuSearch(), T("Генератор Camry", "Camry генератору", "Camry alternator", "Camry alternatör", "Camry generator")),
            Note(T("Такой детали нет в наличии", "Мындай тетик жок", "This part is not in stock", "Bu parça stokta yok", "Bu qism omborda yo'q"), MockTone.Warn),
            Btn(T("Заказ под клиента", "Кардарга заказ", "Customer order", "Müşteri siparişi", "Mijoz uchun buyurtma"), true)),
        Step(T("2. Записываете клиента, деталь и предоплату", "2. Кардарды, тетикти жана алдын ала төлөмдү жазасыз", "2. You enter the client, the part and the prepayment", "2. Müşteriyi, parçayı ve ön ödemeyi yazarsınız", "2. Mijozni, qismni va oldindan to'lovni yozasiz"),
            T("Заказ под клиента", "Кардарга заказ", "Customer order", "Müşteri siparişi", "Mijoz uchun buyurtma"),
            Input(T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), T("Бакыт", "Бакыт", "Bakyt", "Bakyt", "Bakyt")),
            Input(T("Деталь", "Тетик", "Part", "Parça", "Qism"), T("Генератор Toyota Camry", "Toyota Camry генератору", "Toyota Camry alternator", "Toyota Camry alternatör", "Toyota Camry generatori")),
            Input(T("Предоплата", "Алдын ала төлөм", "Prepayment", "Ön ödeme", "Oldindan to'lov"), "2 000", true)),
        Step(T("3. Заказ в списке: ждёт поставки; отмена — по PIN владельца", "3. Заказ тизмеде: жеткирүүнү күтөт; жокко чыгаруу — ээсинин PIN менен", "3. The order is in the list, waiting for delivery; cancelling needs the owner's PIN", "3. Sipariş listede, teslimat bekliyor; iptal sahibin PIN'iyle", "3. Buyurtma ro'yxatda, yetkazishni kutadi; bekor qilish — egasining PIN-kodi bilan"),
            T("Заказы", "Заказдар", "Orders", "Siparişler", "Buyurtmalar"),
            Row(T("Бакыт · Генератор Camry", "Бакыт · Camry генератору", "Bakyt · Camry alternator", "Bakyt · Camry alternatör", "Bakyt · Camry generatori"), T("Ждёт поставки", "Жеткирүүнү күтөт", "Waiting", "Bekliyor", "Kutmoqda"), MockTone.Warn, true, T("Предоплата 2 000 из 6 500", "Алдын ала төлөм 2 000 / 6 500", "Prepaid 2,000 of 6,500", "Ön ödeme 2 000 / 6 500", "Oldindan to'lov 2 000 / 6 500")),
            Note(T("Предоплату и отмену заказа контролирует владелец", "Алдын ала төлөмдү жана заказды жокко чыгарууну ээси көзөмөлдөйт", "The owner controls the prepayment and cancelling an order", "Ön ödemeyi ve sipariş iptalini sahip denetler", "Oldindan to'lov va buyurtmani bekor qilishni egasi nazorat qiladi"), MockTone.Accent)),
        Step(T("4. Деталь пришла — оформляете выдачу, клиент доплачивает остаток", "4. Тетик келди — берүүнү тариздейсиз, кардар калганын төлөйт", "4. The part has arrived — you hand it over, the client pays the rest", "4. Parça geldi — teslim edersiniz, müşteri kalanı öder", "4. Qism keldi — topshirasiz, mijoz qolganini to'laydi"),
            T("Заказ под клиента", "Кардарга заказ", "Customer order", "Müşteri siparişi", "Mijoz uchun buyurtma"),
            Row(T("Бакыт · Генератор Camry", "Бакыт · Camry генератору", "Bakyt · Camry alternator", "Bakyt · Camry alternatör", "Bakyt · Camry generatori"), T("Деталь пришла", "Тетик келди", "Part arrived", "Parça geldi", "Qism keldi"), MockTone.Good, true),
            Total(T("Доплатить", "Төлөп берүү", "Left to pay", "Kalan tutar", "Qolgan to'lov"), "4 500", true),
            Btn(T("Выдать заказ", "Заказды берүү", "Hand over the order", "Siparişi teslim et", "Buyurtmani topshirish"), true)),
    ],
    () =>
    [
        Gain("clipboard-check-outline", T("Не теряете клиента", "Кардарды жоготпойсуз", "You keep the client", "Müşteriyi kaybetmezsiniz", "Mijozni yo'qotmaysiz"),
            T("Нет детали — записываете заказ, а не отправляете клиента к конкурентам.", "Тетик жок — заказ жазасыз, кардарды атаандаштарга жөнөтпөйсүз.", "No part — you record an order instead of sending the client to competitors.", "Parça yok — müşteriyi rakibe göndermek yerine sipariş yazarsınız.", "Qism yo'q — mijozni raqobatchilarga yubormay, buyurtma yozasiz.")),
        Gain("cash", T("Предоплата в кассе", "Алдын ала төлөм кассада", "A prepayment in the till", "Kasada ön ödeme", "Kassada oldindan to'lov"),
            T("Клиент оставляет предоплату — заказ не повиснет в воздухе.", "Кардар алдын ала төлөм калтырат — заказ абада калбайт.", "The client leaves a prepayment — the order does not hang in the air.", "Müşteri ön ödeme bırakır — sipariş havada kalmaz.", "Mijoz oldindan to'lov qoldiradi — buyurtma havoda qolmaydi.")),
        Gain("shield-lock-outline", T("Контроль владельца", "Ээсинин көзөмөлү", "Owner control", "Sahip denetimi", "Egasi nazorati"),
            T("Отмена заказа и предоплата — под PIN и контролем владельца.", "Заказды жокко чыгаруу жана алдын ала төлөм — PIN жана ээсинин көзөмөлүндө.", "Cancelling an order and the prepayment are under PIN and owner control.", "Sipariş iptali ve ön ödeme PIN ve sahip denetimindedir.", "Buyurtmani bekor qilish va oldindan to'lov — PIN va egasi nazoratida.")),
    ]);

    // ───────────────────────── Базар ─────────────────────────

    private static void AuCategoryTiles(Dictionary<string, MarketplaceDemoEntry> d) => d["category_tiles"] = new(() =>
    [
        Step(T("1. На главном экране — крупные плитки категорий вместо длинного списка", "1. Башкы экранда — узун тизме ордуна категориялардын чоң плиткалары", "1. The main screen shows big category tiles instead of a long list", "1. Ana ekranda uzun liste yerine büyük kategori kutuları var", "1. Asosiy ekranda uzun ro'yxat o'rniga kategoriyalarning katta plitkalari"),
            AuTill(),
            Tiles(-1, T("Обувь · 48", "Бут кийим · 48", "Shoes · 48", "Ayakkabı · 48", "Poyabzal · 48"), T("Одежда · 62", "Кийим · 62", "Clothes · 62", "Giyim · 62", "Kiyim · 62"), T("Сумки · 17", "Сумкалар · 17", "Bags · 17", "Çantalar · 17", "Sumkalar · 17"), T("Игрушки · 31", "Оюнчуктар · 31", "Toys · 31", "Oyuncaklar · 31", "O'yinchoqlar · 31")),
            Note(T("На плитке — название и число товаров", "Плиткада — аталышы жана товарлардын саны", "A tile shows the name and the number of products", "Kutuda ad ve ürün sayısı görünür", "Plitkada — nomi va tovarlar soni"))),
        Step(T("2. Тап по плитке «Обувь» — виден список товаров этой категории", "2. «Бут кийим» плиткасын таптоо — ушул категориянын товарлары көрүнөт", "2. Tap the “Shoes” tile — the products of that category appear", "2. «Ayakkabı» kutusuna dokunun — o kategorinin ürünleri açılır", "2. «Poyabzal» plitkasiga bosing — shu kategoriya tovarlari ko'rinadi"),
            AuTill(),
            Row(T("← Все категории", "← Бардык категориялар", "← All categories", "← Tüm kategoriler", "← Barcha kategoriyalar")),
            Row(T("Кроссовки, 42", "Кроссовкалар, 42", "Sneakers, 42", "Spor ayakkabı, 42", "Krossovkalar, 42"), "1 200"),
            Row(T("Туфли, 40", "Туфли, 40", "Shoes, 40", "Klasik ayakkabı, 40", "Tufli, 40"), "1 500"),
            Row(T("Ботинки, 43", "Ботинкалар, 43", "Boots, 43", "Bot, 43", "Botinkalar, 43"), "2 100")),
        Step(T("3. Тап по товару кладёт его в чек — без поиска и штрихкода", "3. Товарды таптасаңыз чекке түшөт — издөөсүз жана штрихкодсуз", "3. A tap on a product puts it in the receipt — no search or barcode", "3. Ürüne dokunmak onu fişe koyar — arama ve barkod olmadan", "3. Tovarga bosish uni chekka qo'shadi — qidiruv va shtrixkodsiz"),
            AuTill(),
            Row(T("← Все категории", "← Бардык категориялар", "← All categories", "← Tüm kategoriler", "← Barcha kategoriyalar")),
            Row(T("Кроссовки, 42", "Кроссовкалар, 42", "Sneakers, 42", "Spor ayakkabı, 42", "Krossovkalar, 42"), "1 200", MockTone.Normal, true),
            Note(T("В чеке: Кроссовки, 42 — 1 200", "Чекте: Кроссовкалар, 42 — 1 200", "In the receipt: Sneakers, 42 — 1,200", "Fişte: Spor ayakkabı, 42 — 1 200", "Chekda: Krossovkalar, 42 — 1 200"), MockTone.Good)),
        Step(T("4. «← Все категории» возвращает к плиткам", "4. «← Бардык категориялар» плиткаларга кайтарат", "4. “← All categories” returns to the tiles", "4. «← Tüm kategoriler» kutulara döndürür", "4. «← Barcha kategoriyalar» plitkalarga qaytaradi"),
            AuTill(),
            Row(T("← Все категории", "← Бардык категориялар", "← All categories", "← Tüm kategoriler", "← Barcha kategoriyalar"), "", MockTone.Normal, true),
            Tiles(1, T("Обувь · 48", "Бут кийим · 48", "Shoes · 48", "Ayakkabı · 48", "Poyabzal · 48"), T("Одежда · 62", "Кийим · 62", "Clothes · 62", "Giyim · 62", "Kiyim · 62"), T("Сумки · 17", "Сумкалар · 17", "Bags · 17", "Çantalar · 17", "Sumkalar · 17"), T("Игрушки · 31", "Оюнчуктар · 31", "Toys · 31", "Oyuncaklar · 31", "O'yinchoqlar · 31")),
            Note(T("Поиск и группы F1–F12 работают по всему каталогу", "Издөө жана F1–F12 топтору бардык каталогдо иштейт", "Search and the F1–F12 groups work across the whole catalogue", "Arama ve F1–F12 grupları tüm katalogda çalışır", "Qidiruv va F1–F12 guruhlari butun katalogda ishlaydi"))),
    ],
    () =>
    [
        Gain("view-dashboard-outline", T("Товар в два касания", "Товар эки таптоодо", "A product in two taps", "İki dokunuşta ürün", "Tovar ikki bosishda"),
            T("Касаетесь категории, затем товара — и он в чеке, без поиска.", "Категорияны, анан товарды таптайсыз — ал чекте, издөөсүз.", "Tap a category, then a product — it is in the receipt, no search.", "Kategoriye, sonra ürüne dokunursunuz — fişte, aramasız.", "Kategoriyaga, so'ng tovarga bosasiz — u chekda, qidiruvsiz.")),
        Gain("gesture-tap", T("Удобно с экраном", "Сенсордук экранга ыңгайлуу", "Easy on a touch screen", "Dokunmatik ekranda rahat", "Sensorli ekranda qulay"),
            T("Крупные плитки с фото или цветом — не промахнуться даже в спешке.", "Сүрөттүү же түстүү чоң плиткалар — шашылганда да жаңылбайсыз.", "Big tiles with a photo or colour — hard to miss even in a rush.", "Fotoğraflı veya renkli büyük kutular — acelede bile şaşırmazsınız.", "Rasmli yoki rangli katta plitkalar — shoshganda ham adashmaysiz.")),
        Gain("magnify", T("Поиск не пропал", "Издөө жоголгон жок", "Search is still there", "Arama yerinde", "Qidiruv joyida"),
            T("Поиск и группы F1–F12 работают поверх всего каталога.", "Издөө жана F1–F12 топтору бүт каталогдун үстүнөн иштейт.", "Search and the F1–F12 groups work over the whole catalogue.", "Arama ve F1–F12 grupları tüm katalogun üzerinde çalışır.", "Qidiruv va F1–F12 guruhlari butun katalog ustida ishlaydi.")),
    ]);

    private static void AuVolumePrice(Dictionary<string, MarketplaceDemoEntry> d) => d["volume_price"] = new(() =>
    [
        Step(T("1. В карточке товара задаёте ступени «от N штук — цена»", "1. Товардын карточкасында «N даанадан — баа» баскычтарын коёсуз", "1. In the product card you set steps “from N pieces — price”", "1. Ürün kartında «N adetten — fiyat» basamaklarını girersiniz", "1. Tovar kartasida «N dona dan — narx» pog'onalarini kiritasiz"),
            AuCard(),
            Row(T("Пара обуви: розница", "Бир жуп бут кийим: чекене", "A pair of shoes: retail", "Bir çift ayakkabı: perakende", "Bir juft poyabzal: chakana"), "1 200"),
            Row(T("От 3 штук", "3 даанадан", "From 3 pieces", "3 adetten", "3 dona dan"), "1 100", MockTone.Normal, true),
            Row(T("От 10 штук", "10 даанадан", "From 10 pieces", "10 adetten", "10 dona dan"), "1 000", MockTone.Normal, true)),
        Step(T("2. Кассир набирает 3 — цена строки сама станет 1 100", "2. Кассир 3 терет — саптын баасы өзү 1 100 болот", "2. The cashier types 3 — the line price becomes 1,100 by itself", "2. Kasiyer 3 yazar — satır fiyatı kendiliğinden 1 100 olur", "2. Kassir 3 ni teradi — qator narxi o'zi 1 100 bo'ladi"),
            AuReceipt(),
            Input(T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), "3", true),
            Row(T("Пара обуви", "Бир жуп бут кийим", "A pair of shoes", "Bir çift ayakkabı", "Bir juft poyabzal"), "3 300", MockTone.Good, false, T("Опт от 3 — 1 100", "Дүң 3 тен — 1 100", "Bulk from 3 — 1,100", "Toptan 3'ten — 1 100", "Ulgurji 3 dan — 1 100"))),
        Step(T("3. Берут 10 — цена ещё ниже: 1 000 за пару", "3. 10 алышат — баа дагы төмөн: жубуна 1 000", "3. They take 10 — the price drops further: 1,000 a pair", "3. 10 alırlar — fiyat daha da düşer: çifti 1 000", "3. 10 ta olishadi — narx yana pasayadi: juftiga 1 000"),
            AuReceipt(),
            Input(T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"), "10", true),
            Row(T("Пара обуви", "Бир жуп бут кийим", "A pair of shoes", "Bir çift ayakkabı", "Bir juft poyabzal"), "10 000", MockTone.Good, false, T("Опт от 10 — 1 000", "Дүң 10 дан — 1 000", "Bulk from 10 — 1,000", "Toptan 10'dan — 1 000", "Ulgurji 10 dan — 1 000")),
            Total(AuTotal(), "10 000")),
        Step(T("4. Цену, введённую вручную, касса не трогает", "4. Кол менен киргизилген баага касса тийбейт", "4. A price typed by hand is left alone by the till", "4. Elle girilen fiyata kasa dokunmaz", "4. Qo'lda kiritilgan narxga kassa tegmaydi"),
            AuReceipt(),
            Row(T("Пара обуви", "Бир жуп бут кийим", "A pair of shoes", "Bir çift ayakkabı", "Bir juft poyabzal"), "3 450", MockTone.Normal, true, T("Цена 1 150 введена вручную", "Баасы 1 150 кол менен киргизилген", "Price 1,150 typed by hand", "Fiyat 1 150 elle girildi", "Narx 1 150 qo'lda kiritilgan")),
            Note(T("Ступень не применяется: цена задана кассиром", "Баскыч колдонулбайт: баасын кассир койгон", "The step is not applied: the price was set by the cashier", "Basamak uygulanmaz: fiyatı kasiyer belirledi", "Pog'ona qo'llanmaydi: narxni kassir belgilagan"))),
    ],
    () =>
    [
        Gain("tag-multiple-outline", T("Цена меняется сама", "Баа өзү өзгөрөт", "The price changes by itself", "Fiyat kendiliğinden değişir", "Narx o'zi o'zgaradi"),
            T("Кассир набирает количество — касса выбирает нужную ступень.", "Кассир санды терет — касса керектүү баскычты тандайт.", "The cashier types the quantity — the till picks the right step.", "Kasiyer miktarı yazar — kasa doğru basamağı seçer.", "Kassir miqdorni teradi — kassa kerakli pog'onani tanlaydi.")),
        Gain("sale", T("Опт без калькулятора", "Калькуляторсуз дүң", "Bulk without a calculator", "Hesap makinesiz toptan", "Kalkulyatorsiz ulgurji"),
            T("Под строкой видна подпись «Опт от 10 — 1 000» — покупатель видит выгоду.", "Саптын астында «Дүң 10 дан — 1 000» жазуусу — сатып алуучу пайданы көрөт.", "A label “Bulk from 10 — 1,000” under the line shows the buyer the saving.", "Satırın altında «Toptan 10'dan — 1 000» yazısı alıcıya avantajı gösterir.", "Qator ostida «Ulgurji 10 dan — 1 000» yozuvi — xaridor foydani ko'radi.")),
        Gain("lock-outline", T("Ручная цена в силе", "Кол менен киргизилген баа күчүндө", "A manual price stays", "Elle girilen fiyat geçerli", "Qo'lda kiritilgan narx kuchda"),
            T("Цену, которую кассир ввёл сам, касса не пересчитывает.", "Кассир өзү киргизген баа кайра эсептелбейт.", "A price the cashier typed is not recalculated by the till.", "Kasiyerin kendi girdiği fiyatı kasa yeniden hesaplamaz.", "Kassir o'zi kiritgan narx qayta hisoblanmaydi.")),
    ]);

    private static void AuHaggle(Dictionary<string, MarketplaceDemoEntry> d) => d["haggle"] = new(() =>
    [
        Step(T("1. Покупатель торгуется — нажимаете «Торг» у чека", "1. Сатып алуучу соодалашат — чектеги «Соодалашуу» баскычын басасыз", "1. The customer haggles — press “Haggle” on the receipt", "1. Müşteri pazarlık eder — fişte «Pazarlık»a basarsınız", "1. Xaridor savdolashadi — chekdagi «Savdolashuv» ni bosasiz"),
            AuReceipt(),
            Row(T("Куртка", "Куртка", "Jacket", "Mont", "Kurtka"), "1 340"),
            Total(AuTotal(), "1 340"),
            Btn(T("Торг", "Соодалашуу", "Haggle", "Pazarlık", "Savdolashuv"), true, false)),
        Step(T("2. Жмёте «До 50» — сумма округлится вниз до шага", "2. «50 чейин» басасыз — сумма кадамга чейин төмөн тегеректелет", "2. Press “To 50” — the amount rounds down to the step", "2. «50'ye kadar»a basarsınız — tutar basamağa kadar aşağı yuvarlanır", "2. «50 gacha» ni bosasiz — summa pog'onagacha pastga yaxlitlanadi"),
            T("Торг", "Соодалашуу", "Haggle", "Pazarlık", "Savdolashuv"),
            Chips(1, T("До 10", "10 чейин", "To 10", "10'a kadar", "10 gacha"), T("До 50", "50 чейин", "To 50", "50'ye kadar", "50 gacha"), T("До 100", "100 чейин", "To 100", "100'e kadar", "100 gacha")),
            Total(AuTotal(), "1 340")),
        Step(T("3. Итог 1 300: округлено в пользу покупателя", "3. Жыйынтыгы 1 300: сатып алуучунун пайдасына тегеректелди", "3. The total is 1,300: rounded in the buyer's favour", "3. Toplam 1 300: alıcı lehine yuvarlandı", "3. Jami 1 300: xaridor foydasiga yaxlitlandi"),
            T("Торг", "Соодалашуу", "Haggle", "Pazarlık", "Savdolashuv"),
            Total(AuTotal(), "1 300", true),
            Note(T("Было 1 340 — стало 1 300 одним касанием", "1 340 болчу — бир тийүүдө 1 300 болду", "Was 1,340 — now 1,300 in one tap", "1 340 idi — tek dokunuşta 1 300 oldu", "1 340 edi — bir bosishda 1 300 bo'ldi"), MockTone.Good)),
        Step(T("4. Или «Договорились за …» — любая сумма ниже; большая скидка — по PIN", "4. Же «Макул болдук …» — төмөнкү каалаган сумма; чоң арзандатуу — PIN менен", "4. Or “Agreed for …” — any lower amount; a big discount needs a PIN", "4. Veya «Anlaşılan tutar …» — daha düşük herhangi bir tutar; büyük indirim PIN ister", "4. Yoki «Kelishilgan summa …» — pastroq istalgan summa; katta chegirma PIN talab qiladi"),
            T("Торг", "Соодалашуу", "Haggle", "Pazarlık", "Savdolashuv"),
            Input(T("Договорились за", "Макул болдук", "Agreed for", "Anlaşılan tutar", "Kelishilgan summa"), "1 250", true),
            Note(T("Скидка выше порога — нужен PIN руководителя", "Арзандатуу чектен жогору — жетекчинин PIN керек", "The discount is above the threshold — the manager's PIN is needed", "İndirim eşiğin üstünde — yöneticinin PIN'i gerekir", "Chegirma chegaradan yuqori — rahbar PIN-kodi kerak"), MockTone.Warn)),
    ],
    () =>
    [
        Gain("message-text-outline", T("Торг в одно касание", "Бир тийүүдө соодалашуу", "Haggling in one tap", "Tek dokunuşla pazarlık", "Bir bosishda savdolashuv"),
            T("«До 10 / 50 / 100» округляет сумму, «Договорились за …» берёт любую цену ниже.", "«10 / 50 / 100 чейин» сумманы тегеректейт, «Макул болдук …» төмөнкү баасын алат.", "“To 10 / 50 / 100” rounds the amount; “Agreed for …” takes any lower price.", "«10 / 50 / 100'e kadar» tutarı yuvarlar; «Anlaşılan tutar …» daha düşük her fiyatı alır.", "«10 / 50 / 100 gacha» summani yaxlitlaydi; «Kelishilgan summa …» pastroq istalgan narxni oladi.")),
        Gain("shield-lock-outline", T("Скидка под контролем", "Арзандатуу көзөмөлдө", "The discount is controlled", "İndirim denetimde", "Chegirma nazoratda"),
            T("До порога — свободно, выше нужен PIN руководителя, выше предела — владелец.", "Чекке чейин — эркин, андан жогору жетекчинин PIN, лимиттен жогору — ээси.", "Up to the threshold it is free, above it the manager's PIN, above the limit the owner.", "Eşiğe kadar serbest, üstünde yönetici PIN'i, sınırın üstünde sahip.", "Chegaragacha erkin, undan yuqori rahbar PIN-kodi, limitdan yuqori — egasi.")),
        Gain("clock-outline", T("Быстро у прилавка", "Текченин жанында тез", "Quick at the counter", "Tezgahta hızlı", "Peshtaxtada tez"),
            T("Без калькулятора и споров: сумма готова за секунду.", "Калькуляторсуз жана талаш-тартышсыз: сумма бир секундда даяр.", "No calculator and no arguing: the amount is ready in a second.", "Hesap makinesi ve tartışma yok: tutar bir saniyede hazır.", "Kalkulyatorsiz va tortishuvsiz: summa bir soniyada tayyor.")),
    ]);

    private static void AuCurrencies(Dictionary<string, MarketplaceDemoEntry> d) => d["currencies"] = new(() =>
    [
        Step(T("1. В окне оплаты наличными появился блок «Принято в валюте»", "1. Накта төлөө терезесинде «Валютада алынды» блогу пайда болду", "1. The cash payment window shows a “Received in currency” block", "1. Nakit ödeme penceresinde «Dövizle alınan» bloğu çıktı", "1. Naqd to'lov oynasida «Valyutada qabul qilindi» bloki paydo bo'ldi"),
            T("Оплата наличными", "Накта төлөм", "Cash payment", "Nakit ödeme", "Naqd to'lov"),
            Total(T("К оплате", "Төлөнүүчү", "To pay", "Ödenecek", "To'lanadi"), "4 100"),
            Note(T("Принято в валюте: USD, RUB, CNY — по курсу компании", "Валютада алынды: USD, RUB, CNY — компаниянын курсу боюнча", "Received in currency: USD, RUB, CNY — at the company rate", "Dövizle alınan: USD, RUB, CNY — şirket kuruyla", "Valyutada qabul qilindi: USD, RUB, CNY — kompaniya kursi bo'yicha"), MockTone.Accent)),
        Step(T("2. Выбираете валюту, в которой дал деньги покупатель", "2. Сатып алуучу акча берген валютаны тандайсыз", "2. You pick the currency the buyer paid in", "2. Alıcının ödediği dövizi seçersiniz", "2. Xaridor to'lagan valyutani tanlaysiz"),
            T("Оплата наличными", "Накта төлөм", "Cash payment", "Nakit ödeme", "Naqd to'lov"),
            Total(T("К оплате", "Төлөнүүчү", "To pay", "Ödenecek", "To'lanadi"), "4 100"),
            Chips(0, "USD", "RUB", "CNY")),
        Step(T("3. Вводите сумму в валюте — касса пересчитает её в сомы", "3. Валютадагы сумманы киргизесиз — касса аны сомго которот", "3. You type the amount in that currency — the till converts it to som", "3. Dövizdeki tutarı yazarsınız — kasa onu soma çevirir", "3. Valyutadagi summani kiritasiz — kassa uni so'mga o'tkazadi"),
            T("Оплата наличными", "Накта төлөм", "Cash payment", "Nakit ödeme", "Naqd to'lov"),
            Chips(0, "USD", "RUB", "CNY"),
            Input(T("Сумма в валюте, USD", "Валютадагы сумма, USD", "Amount in currency, USD", "Dövizdeki tutar, USD", "Valyutadagi summa, USD"), "50", true),
            Note(T("50 $ × 89,50 = 4 475 сом по курсу компании", "50 $ × 89,50 = 4 475 сом компаниянын курсу боюнча", "50 $ × 89.50 = 4,475 som at the company rate", "50 $ × 89,50 = 4 475 som şirket kuruyla", "50 $ × 89,50 = 4 475 so'm kompaniya kursi bo'yicha"), MockTone.Accent)),
        Step(T("4. Сдача сразу считается в сомах", "4. Кайтарым дароо сом менен эсептелет", "4. The change is counted in som at once", "4. Para üstü hemen somla hesaplanır", "4. Qaytim darhol so'mda hisoblanadi"),
            T("Оплата наличными", "Накта төлөм", "Cash payment", "Nakit ödeme", "Naqd to'lov"),
            Row(T("Принято: 50 USD", "Алынды: 50 USD", "Received: 50 USD", "Alınan: 50 USD", "Qabul qilindi: 50 USD"), "4 475 " + AuSom()),
            Total(T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim"), "375 " + AuSom(), true),
            Btn(T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"), true)),
    ],
    () =>
    [
        Gain("currency-usd", T("Доллары, рубли, юани", "Доллар, рубль, юань", "Dollars, roubles, yuan", "Dolar, ruble, yuan", "Dollar, rubl, yuan"),
            T("Принимаете USD, RUB и CNY — те, у которых задан курс.", "USD, RUB жана CNY кабыл аласыз — курсу коюлгандарын.", "You accept USD, RUB and CNY — those that have a rate.", "USD, RUB ve CNY kabul edersiniz — kuru girilmiş olanları.", "USD, RUB va CNY ni qabul qilasiz — kursi belgilanganlarini.")),
        Gain("calculator-variant-outline", T("Пересчёт без калькулятора", "Калькуляторсуз кайра эсептөө", "Conversion without a calculator", "Hesap makinesiz çevirme", "Kalkulyatorsiz qayta hisoblash"),
            T("Касса сама считает сомы по курсу компании и показывает сдачу.", "Касса сомду компаниянын курсу боюнча өзү эсептейт жана кайтарымды көрсөтөт.", "The till converts to som at the company rate and shows the change.", "Kasa somu şirket kuruyla kendisi hesaplar ve para üstünü gösterir.", "Kassa so'mni kompaniya kursi bo'yicha o'zi hisoblaydi va qaytimni ko'rsatadi.")),
        Gain("cash", T("Сдача в сомах", "Кайтарым сом менен", "Change in som", "Para üstü somla", "Qaytim so'mda"),
            T("Сдача считается в сомах — без путаницы с курсами на сдаче.", "Кайтарым сом менен эсептелет — курстар менен чаташпайсыз.", "The change is counted in som — no mix-ups with rates on the change.", "Para üstü somla hesaplanır — kurlarla karışıklık olmaz.", "Qaytim so'mda hisoblanadi — kurslar bilan adashmaysiz.")),
    ]);

    private static void AuInvoiceCargo(Dictionary<string, MarketplaceDemoEntry> d) => d["invoice_cargo"] = new(() =>
    [
        Step(T("1. В редакторе накладной ставите галочку «Карго»", "1. Накладной редакторунда «Карго» белгисин коёсуз", "1. In the invoice editor you tick “Cargo”", "1. İrsaliye düzenleyicide «Kargo»yu işaretlersiniz", "1. Hisob-faktura muharririda «Kargo» belgisini qo'yasiz"),
            T("Накладная A4", "A4 накладная", "A4 invoice", "A4 irsaliye", "A4 hisob-faktura"),
            Row(T("Мешок риса, 25 кг", "Күрүч баштыгы, 25 кг", "Bag of rice, 25 kg", "Pirinç çuvalı, 25 kg", "Guruch qopi, 25 kg"), "9 600"),
            Toggle(T("Карго", "Карго", "Cargo", "Kargo", "Kargo"), true, true)),
        Step(T("2. Заполняете перевозчика, город получателя и трек-номер", "2. Ташуучуну, алуучунун шаарын жана трек-номерди толтурасыз", "2. You fill in the carrier, the recipient's city and the tracking number", "2. Taşıyıcıyı, alıcının şehrini ve takip numarasını doldurursunuz", "2. Tashuvchini, oluvchi shahrini va trek-raqamni to'ldirasiz"),
            T("Данные карго", "Карго маалыматы", "Cargo details", "Kargo bilgileri", "Kargo ma'lumotlari"),
            Input(T("Перевозчик", "Ташуучу", "Carrier", "Taşıyıcı", "Tashuvchi"), T("Карго-Демо", "Карго-Демо", "Cargo-Demo", "Kargo-Demo", "Kargo-Demo"), true),
            Input(T("Город или адрес получателя", "Алуучунун шаары же дареги", "Recipient's city or address", "Alıcının şehri veya adresi", "Oluvchining shahri yoki manzili"), T("Бишкек", "Бишкек", "Bishkek", "Bişkek", "Bishkek")),
            Input(T("Трек-номер", "Трек-номер", "Tracking number", "Takip numarası", "Trek-raqam"), "KD-0123")),
        Step(T("3. В каждой строке — колонки «Мест» и «Вес брутто, кг»", "3. Ар бир сапта — «Орун» жана «Брутто салмак, кг» тилкелери", "3. Every line gets “Places” and “Gross weight, kg” columns", "3. Her satırda «Yer» ve «Brüt ağırlık, kg» sütunları olur", "3. Har bir qatorda «Joy» va «Brutto og'irlik, kg» ustunlari bor"),
            T("Накладная A4", "A4 накладная", "A4 invoice", "A4 irsaliye", "A4 hisob-faktura"),
            Row(T("Мешок риса, 25 кг", "Күрүч баштыгы, 25 кг", "Bag of rice, 25 kg", "Pirinç çuvalı, 25 kg", "Guruch qopi, 25 kg"), "9 600", MockTone.Normal, true, T("Мест: 10 · Брутто: 260 кг", "Орун: 10 · Брутто: 260 кг", "Places: 10 · Gross: 260 kg", "Yer: 10 · Brüt: 260 kg", "Joy: 10 · Brutto: 260 kg")),
            Row(T("Масло, 5 л", "Май, 5 л", "Oil, 5 l", "Yağ, 5 l", "Yog', 5 l"), "4 200", MockTone.Normal, true, T("Мест: 4 · Брутто: 22 кг", "Орун: 4 · Брутто: 22 кг", "Places: 4 · Gross: 22 kg", "Yer: 4 · Brüt: 22 kg", "Joy: 4 · Brutto: 22 kg")),
            Total(AuTotal(), "13 800")),
        Step(T("4. Печать A4: под итогами — блок «Карго»", "4. A4 басуу: жыйынтыктын астында — «Карго» блогу", "4. A4 print: a “Cargo” block under the totals", "4. A4 baskı: toplamların altında «Kargo» bloğu", "4. A4 chop etish: jami ostida — «Kargo» bloki"),
            T("Печать накладной", "Накладнойду басуу", "Invoice print", "İrsaliye baskısı", "Hisob-fakturani chop etish"),
            Note(T("Карго: мест 14 · брутто 282 кг · Карго-Демо · Бишкек", "Карго: орун 14 · брутто 282 кг · Карго-Демо · Бишкек", "Cargo: places 14 · gross 282 kg · Cargo-Demo · Bishkek", "Kargo: yer 14 · brüt 282 kg · Kargo-Demo · Bişkek", "Kargo: joy 14 · brutto 282 kg · Kargo-Demo · Bishkek"), MockTone.Good),
            Btn(T("Печать A4", "A4 басуу", "Print A4", "A4 yazdır", "A4 chop etish"), true)),
    ],
    () =>
    [
        Gain("file-document-outline", T("Накладная с картой груза", "Жүк маалыматы бар накладная", "An invoice with cargo data", "Kargo bilgili irsaliye", "Yuk ma'lumotli hisob-faktura"),
            T("Места, брутто и мешки — по строкам и итогом в блоке «Карго».", "Орун, брутто жана баштыктар — саптар боюнча жана «Карго» блогунда жыйынтык.", "Places, gross weight and bags — per line and in total in the “Cargo” block.", "Yer, brüt ve çuval — satır başına ve «Kargo» bloğunda toplam.", "Joy, brutto va qoplar — qatorlar bo'yicha va «Kargo» blokida jami.")),
        Gain("package-variant-closed", T("Перевозчик и получатель", "Ташуучу жана алуучу", "Carrier and recipient", "Taşıyıcı ve alıcı", "Tashuvchi va oluvchi"),
            T("Перевозчик, город или адрес получателя и трек-номер — на бумаге.", "Ташуучу, алуучунун шаары же дареги жана трек-номер — кагазда.", "The carrier, the recipient's city or address and the tracking number — on paper.", "Taşıyıcı, alıcının şehri veya adresi ve takip numarası — kağıtta.", "Tashuvchi, oluvchi shahri yoki manzili va trek-raqam — qog'ozda.")),
        Gain("printer-outline", T("Опт в другой город", "Башка шаарга дүң", "Wholesale to another city", "Başka şehre toptan", "Boshqa shaharga ulgurji"),
            T("Данные хранятся в самой накладной — отгрузка оформлена одним документом.", "Маалымат накладныйдын өзүндө сакталат — жөнөтүү бир документ менен тариздөөлөт.", "The data lives in the invoice itself — a shipment is one document.", "Veriler irsaliyenin içinde saklanır — sevkiyat tek belgeyle hazırlanır.", "Ma'lumotlar hisob-fakturaning o'zida saqlanadi — jo'natma bitta hujjat bilan rasmiylashtiriladi.")),
    ]);
}
