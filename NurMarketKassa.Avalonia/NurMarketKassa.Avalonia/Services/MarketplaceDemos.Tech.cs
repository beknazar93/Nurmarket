using System.Collections.Generic;
using static NurMarketKassa.AvaloniaHost.Services.MockDsl;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-26: демонстрации работы и «Что получите» — одежда, электроника, автозапчасти (часть 1). Правила: 3–5 шагов; подпись шага «что нажать / что видно / результат» на 5 языках (T);
// кадр помещается в рамку 640×340 (≈5 строк или полей; тест MarketplaceDemosTests измеряет высоту на всех языках); выдуманные имена и суммы, без реальных данных;
// содержание — только то, что функция делает по тексту её карточки (MarketplaceModuleContent) и коду.
public static partial class MarketplaceDemos
{
    private static void AddGroupB(Dictionary<string, MarketplaceDemoEntry> d)
    {
        // Образец (остальные записи группы — по нему): поиск детали по каталожному номеру → найдена, есть аналоги.
        d["part_numbers"] = new(() =>
        [
            Step(T("1. Клиент называет номер фильтра — вводите его в строку поиска", "1. Кардар чыпканын номерин айтат — издөө сабына киргизесиз", "1. The client names a filter number — type it into the search bar", "1. Müşteri filtre numarasını söyler — arama çubuğuna yazarsınız", "1. Mijoz filtr raqamini aytadi — qidiruv qatoriga kiritasiz"),
                T("Касса", "Касса", "Till", "Kasa", "Kassa"),
                Input(T("Поиск", "Издөө", "Search", "Ara", "Qidiruv"), "04152-YZZA1", hot: true),
                Note(T("В названии детали этого номера нет — он записан в карточке", "Тетиктин атында бул номер жок — ал карточкада жазылган", "The part name has no such number — it is stored in the card", "Parça adında bu numara yok — kartta kayıtlı", "Qism nomida bu raqam yo'q — u kartada yozilgan"))),
            Step(T("2. Касса находит деталь по каталожному номеру или OEM", "2. Касса тетикти каталог номери же OEM боюнча табат", "2. The till finds the part by catalogue number or OEM", "2. Kasa parçayı katalog numarası veya OEM ile bulur", "2. Kassa qismni katalog raqami yoki OEM bo'yicha topadi"),
                T("Касса", "Касса", "Till", "Kasa", "Kassa"),
                Input(T("Поиск", "Издөө", "Search", "Ara", "Qidiruv"), "04152-YZZA1"),
                Row(T("Фильтр масляный Toyota", "Toyota май чыпкасы", "Toyota oil filter", "Toyota yağ filtresi", "Toyota moy filtri"), "650", MockTone.Normal, true, "04152-YZZA1 · OEM"),
                Row(T("Фильтр масляный Hyundai", "Hyundai май чыпкасы", "Hyundai oil filter", "Hyundai yağ filtresi", "Hyundai moy filtri"), "590", MockTone.Normal, false, "26300-35505")),
            Step(T("3. Можно искать и «бренд + номер»", "3. «Бренд + номер» боюнча да издөөгө болот", "3. “Brand + number” works too", "3. «Marka + numara» ile de aranabilir", "3. «Brend + raqam» bilan ham qidirish mumkin"),
                T("Касса", "Касса", "Till", "Kasa", "Kassa"),
                Input(T("Поиск", "Издөө", "Search", "Ara", "Qidiruv"), "Toyota 04152", true),
                Row(T("Фильтр масляный Toyota", "Toyota май чыпкасы", "Toyota oil filter", "Toyota yağ filtresi", "Toyota moy filtri"), "650", MockTone.Normal, true, "04152-YZZA1"),
                Row(T("Колодки передние Toyota", "Toyota алдыңкы колодкалары", "Toyota front pads", "Toyota ön balatalar", "Toyota old kolodkalari"), "2 400", MockTone.Normal, false, "04465-02220")),
            Step(T("4. Деталь в чеке — продажа за несколько секунд", "4. Тетик чекте — сатуу бир нече секундда", "4. The part is in the receipt — a sale in a few seconds", "4. Parça fişte — birkaç saniyede satış", "4. Qism chekda — bir necha soniyada sotuv"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Фильтр масляный Toyota", "Toyota май чыпкасы", "Toyota oil filter", "Toyota yağ filtresi", "Toyota moy filtri"), "650", MockTone.Normal, false, "1 × 650"),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "650", true),
                Btn(T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"), true)),
        ],
        () =>
        [
            Gain("magnify", T("Находит по номеру", "Номер боюнча табат", "Finds by number", "Numarayla bulur", "Raqam bo'yicha topadi"),
                T("Деталь находится по каталожному номеру или OEM, даже если в названии его нет.", "Тетик аталышында жок болсо да, каталог номери же OEM боюнча табылат.", "A part is found by catalogue number or OEM even if the name has none.", "Parça, adında olmasa bile katalog numarası veya OEM ile bulunur.", "Qism nomida bo'lmasa ham, katalog raqami yoki OEM bo'yicha topiladi.")),
            Gain("clock-outline", T("Продажа быстрее", "Сатуу тезирээк", "Faster sales", "Daha hızlı satış", "Tezroq sotuv"),
                T("Кассир не листает список: вводит номер — и деталь уже в чеке.", "Кассир тизмени барактабайт: номерди киргизет — тетик чекте.", "The cashier does not scroll a list: types the number and the part is in the receipt.", "Kasiyer liste kaydırmaz: numarayı yazar, parça fişe gelir.", "Kassir ro'yxatni varaqlamaydi: raqamni kiritadi — qism chekda.")),
            Gain("tag-text-outline", T("«Бренд + номер»", "«Бренд + номер»", "“Brand + number”", "«Marka + numara»", "«Brend + raqam»"),
                T("Поиск понимает и такую запись: «Toyota 04152».", "Издөө мындай жазууну да түшүнөт: «Toyota 04152».", "Search understands this form too: “Toyota 04152”.", "Arama bu yazımı da anlar: «Toyota 04152».", "Qidiruv bunday yozuvni ham tushunadi: «Toyota 04152».")),
        ]);

        // ── Размеры и цвета ──
        d["variants"] = new(() =>
        [
            Step(T("1. В карточке товара добавьте размеры и цвета", "1. Товардын карточкасына өлчөмдөрдү жана түстөрдү кошуңуз", "1. In the product card, add sizes and colours", "1. Ürün kartına beden ve renkleri ekleyin", "1. Tovar kartasiga o'lchamlar va ranglarni qo'shing"),
                T("Карточка товара", "Товардын карточкасы", "Product card", "Ürün kartı", "Tovar kartasi"),
                Input(T("Название", "Аталышы", "Name", "Ad", "Nomi"), T("Куртка зимняя", "Кышкы куртка", "Winter jacket", "Kışlık mont", "Qishki kurtka")),
                Chips(-1, "S", "M", "L", "XL"),
                Chips(-1, T("Чёрный", "Кара", "Black", "Siyah", "Qora"), T("Синий", "Көк", "Blue", "Mavi", "Ko'k")),
                Btn(T("Добавить варианты", "Варианттарды кошуу", "Add variants", "Varyant ekle", "Variantlarni qo'shish"), true)),
            Step(T("2. У каждого варианта свой штрихкод и свой остаток", "2. Ар бир варианттын өз штрихкоду жана өз калдыгы бар", "2. Each variant has its own barcode and its own stock", "2. Her varyantın kendi barkodu ve kendi stoku var", "2. Har bir variantning o'z shtrixkodi va o'z qoldig'i bor"),
                T("Куртка зимняя", "Кышкы куртка", "Winter jacket", "Kışlık mont", "Qishki kurtka"),
                Row(T("M · чёрный", "M · кара", "M · black", "M · siyah", "M · qora"), T("12 шт", "12 дана", "12 pcs", "12 adet", "12 dona"), MockTone.Normal, false, "2000011"),
                Row(T("L · чёрный", "L · кара", "L · black", "L · siyah", "L · qora"), T("5 шт", "5 дана", "5 pcs", "5 adet", "5 dona"), MockTone.Normal, false, "2000012"),
                Row(T("XL · чёрный", "XL · кара", "XL · black", "XL · siyah", "XL · qora"), T("0 шт", "0 дана", "0 pcs", "0 adet", "0 dona"), MockTone.Bad, false, "2000013")),
            Step(T("3. В чеке размер можно сменить, не убирая строку", "3. Чекте саптын өзүн алып салбай өлчөмдү алмаштырса болот", "3. In the receipt the size can be changed without removing the line", "3. Fişte satırı silmeden beden değiştirilebilir", "3. Chekda qatorni o'chirmasdan o'lchamni almashtirish mumkin"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Куртка зимняя · L", "Кышкы куртка · L", "Winter jacket · L", "Kışlık mont · L", "Qishki kurtka · L"), "4 500", MockTone.Normal, true, T("Размер можно сменить", "Өлчөмдү алмаштырса болот", "The size can be changed", "Beden değiştirilebilir", "O'lchamni almashtirish mumkin")),
                Chips(2, "S", "M", "L", "XL"),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "4 500")),
            Step(T("4. Результат: одна карточка вместо трёх товаров", "4. Натыйжа: үч товардын ордуна бир карточка", "4. Result: one card instead of three products", "4. Sonuç: üç ürün yerine tek kart", "4. Natija: uchta tovar o'rniga bitta karta"),
                T("Товары", "Товарлар", "Products", "Ürünler", "Tovarlar"),
                Row(T("Куртка зимняя", "Кышкы куртка", "Winter jacket", "Kışlık mont", "Qishki kurtka"), T("17 шт", "17 дана", "17 pcs", "17 adet", "17 dona"), MockTone.Normal, true, "M · L · XL"),
                Note(T("Куртка M, L и XL — одна карточка, а не три товара", "M, L жана XL курткасы — үч товар эмес, бир карточка", "A jacket in M, L and XL is one card, not three products", "M, L ve XL mont — üç ürün değil, tek kart", "M, L va XL kurtka — uchta tovar emas, bitta karta"), MockTone.Good)),
        ],
        () =>
        [
            Gain("shape-outline", T("Одна карточка — все варианты", "Бир карточка — бардык вариант", "One card, all variants", "Tek kart, tüm varyantlar", "Bitta karta — barcha variantlar"),
                T("Куртка M, L и XL — один товар, а не три отдельных.", "M, L жана XL курткасы — үч өзүнчө эмес, бир товар.", "A jacket in M, L and XL is one product, not three separate ones.", "M, L ve XL mont — üç ayrı değil, tek ürün.", "M, L va XL kurtka — uchta alohida emas, bitta tovar.")),
            Gain("barcode", T("Свой штрихкод и остаток", "Өз штрихкоду жана калдыгы", "Own barcode and stock", "Kendi barkodu ve stoku", "O'z shtrixkodi va qoldig'i"),
                T("У каждого размера и цвета свой штрихкод и свой остаток.", "Ар бир өлчөмдүн жана түстүн өз штрихкоду жана өз калдыгы бар.", "Each size and colour has its own barcode and its own stock.", "Her beden ve rengin kendi barkodu ve stoku var.", "Har bir o'lcham va rangning o'z shtrixkodi va o'z qoldig'i bor.")),
            Gain("sync", T("Смена размера в чеке", "Чекте өлчөмдү алмаштыруу", "Size change in the receipt", "Fişte beden değişimi", "Chekda o'lchamni almashtirish"),
                T("Покупатель передумал — размер меняется прямо в строке чека.", "Сатып алуучу оюн өзгөрттү — өлчөм чектин сабында алмашат.", "The customer changed their mind — the size changes right in the receipt line.", "Müşteri fikrini değiştirdi — beden doğrudan fiş satırında değişir.", "Xaridor fikrini o'zgartirdi — o'lcham chek qatorida o'zgaradi.")),
        ]);

        // ── Консультант ──
        d["consultant"] = new(() =>
        [
            Step(T("1. В окне оплаты есть блок «Консультант»", "1. Төлөм терезесинде «Консультант» блогу бар", "1. The payment window has a “Consultant” block", "1. Ödeme penceresinde «Danışman» bloğu var", "1. To'lov oynasida «Maslahatchi» bloki bor"),
                T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"),
                Total(T("К оплате", "Төлөнө турган", "To pay", "Ödenecek", "To'lanadi"), "9 800"),
                Row(T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"), T("не выбран", "тандалган жок", "not chosen", "seçilmedi", "tanlanmagan"), MockTone.Warn, true),
                Btn(T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"), false)),
            Step(T("2. Выберите, кто помог покупателю", "2. Сатып алуучуга ким жардам бергенин тандаңыз", "2. Choose who helped the customer", "2. Müşteriye kimin yardım ettiğini seçin", "2. Xaridorga kim yordam berganini tanlang"),
                T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"),
                Row(T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"), T("Айгуль", "Айгүл", "Aigul", "Aigül", "Aygul"), MockTone.Normal, true),
                Chips(0, T("Айгуль", "Айгүл", "Aigul", "Aigül", "Aygul"), T("Бакыт", "Бакыт", "Bakyt", "Bakıt", "Baqit"), T("Эрлан", "Эрлан", "Erlan", "Erlan", "Erlan"))),
            Step(T("3. Процент ей идёт с этой продажи — из профиля выплат", "3. Пайыз ушул сатуудан ага — төлөм профилинен", "3. Her percentage of this sale comes from her payout profile", "3. Bu satıştan payı ödeme profilinden gelir", "3. Foiz shu sotuvdan unga — to'lov profilidan"),
                T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"),
                Row(T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"), T("Айгуль", "Айгүл", "Aigul", "Aigül", "Aygul")),
                Row(T("Процент с продажи", "Сатуудан пайыз", "Percentage of the sale", "Satıştan yüzde", "Sotuvdan foiz"), "3 %", MockTone.Good, true, T("из профиля выплат", "төлөм профилинен", "from the payout profile", "ödeme profilinden", "to'lov profilidan")),
                Total(T("К оплате", "Төлөнө турган", "To pay", "Ödenecek", "To'lanadi"), "9 800")),
            Step(T("4. Продажи по консультантам видны в отчёте по зарплате", "4. Консультанттар боюнча сатуулар айлык отчётто көрүнөт", "4. Sales by consultant are in the salary report", "4. Danışmanlara göre satışlar maaş raporunda görünür", "4. Maslahatchilar bo'yicha sotuvlar ish haqi hisobotida ko'rinadi"),
                T("Отчёт по зарплате", "Айлык отчёт", "Salary report", "Maaş raporu", "Ish haqi hisoboti"),
                Row(T("Айгуль", "Айгүл", "Aigul", "Aigül", "Aygul"), "1 480", MockTone.Good, true, T("12 продаж · 3 %", "12 сатуу · 3 %", "12 sales · 3 %", "12 satış · 3 %", "12 sotuv · 3 %")),
                Row(T("Бакыт", "Бакыт", "Bakyt", "Bakıt", "Baqit"), "920", MockTone.Normal, false, T("8 продаж · 3 %", "8 сатуу · 3 %", "8 sales · 3 %", "8 satış · 3 %", "8 sotuv · 3 %"))),
        ],
        () =>
        [
            Gain("account-voice", T("Кто помог покупателю", "Сатып алуучуга ким жардам берди", "Who helped the customer", "Müşteriye kim yardım etti", "Xaridorga kim yordam berdi"),
                T("В окне оплаты выбираете консультанта — продажа записана на него.", "Төлөм терезесинде консультантты тандайсыз — сатуу ага жазылат.", "You pick the consultant in the payment window — the sale is credited to them.", "Ödeme penceresinde danışmanı seçersiniz — satış ona yazılır.", "To'lov oynasida maslahatchini tanlaysiz — sotuv unga yoziladi.")),
            Gain("percent", T("Процент без ручного счёта", "Кол менен эсептебей пайыз", "Percentage without manual maths", "Elle hesap yapmadan yüzde", "Qo'lda hisoblamasdan foiz"),
                T("Процент с продажи берётся из профиля выплат консультанта.", "Сатуудан пайыз консультанттын төлөм профилинен алынат.", "The percentage of the sale comes from the consultant's payout profile.", "Satıştan yüzde danışmanın ödeme profilinden alınır.", "Sotuvdan foiz maslahatchining to'lov profilidan olinadi.")),
            Gain("chart-bar", T("Отчёт по консультантам", "Консультанттар боюнча отчёт", "Report by consultant", "Danışman raporu", "Maslahatchilar hisoboti"),
                T("Продажи по консультантам видны в отчёте по зарплате.", "Консультанттар боюнча сатуулар айлык отчётто көрүнөт.", "Sales by consultant are visible in the salary report.", "Danışmanlara göre satışlar maaş raporunda görünür.", "Maslahatchilar bo'yicha sotuvlar ish haqi hisobotida ko'rinadi.")),
        ]);

        // ── Обмен ──
        d["exchange"] = new(() =>
        [
            Step(T("1. Откройте возврат продажи и нажмите «Обменять»", "1. Сатууну кайтарууну ачып, «Алмаштыруу» баскычын басыңыз", "1. Open the sale return and press “Exchange”", "1. Satış iadesini açıp «Değiştir»e basın", "1. Sotuvni qaytarishni ochib, «Almashtirish» ni bosing"),
                T("Возврат продажи", "Сатууну кайтаруу", "Sale return", "Satış iadesi", "Sotuvni qaytarish"),
                Row(T("Платье синее, 44", "Көк көйнөк, 44", "Blue dress, 44", "Mavi elbise, 44", "Ko'k ko'ylak, 44"), "2 500", MockTone.Normal, false, T("Чек № 1048", "Чек № 1048", "Receipt No. 1048", "Fiş No. 1048", "Chek № 1048")),
                Btn(T("Обменять", "Алмаштыруу", "Exchange", "Değiştir", "Almashtirish"), true),
                Btn(T("Вернуть деньги", "Акчаны кайтаруу", "Refund money", "Parayı iade et", "Pulni qaytarish"), false, false)),
            Step(T("2. Выберите, что покупатель берёт взамен", "2. Сатып алуучу эмнени алмаштырып алаарын тандаңыз", "2. Choose what the customer takes instead", "2. Müşterinin karşılığında ne alacağını seçin", "2. Xaridor evaziga nima olishini tanlang"),
                T("Обмен", "Алмаштыруу", "Exchange", "Değişim", "Almashtirish"),
                Row(T("Возврат: платье синее, 44", "Кайтаруу: көк көйнөк, 44", "Return: blue dress, 44", "İade: mavi elbise, 44", "Qaytarish: ko'k ko'ylak, 44"), "−2 500", MockTone.Bad),
                Row(T("Взамен: платье синее, 46", "Ордуна: көк көйнөк, 46", "Instead: blue dress, 46", "Karşılığında: mavi elbise, 46", "Evaziga: ko'k ko'ylak, 46"), "2 800", MockTone.Normal, true)),
            Step(T("3. Касса сама считает разницу — доплатить или вернуть", "3. Касса айырманы өзү эсептейт — кошумча төлөө же кайтаруу", "3. The till works out the difference — top up or refund", "3. Kasa farkı kendisi hesaplar — ek ödeme veya iade", "3. Kassa farqni o'zi hisoblaydi — qo'shimcha to'lov yoki qaytarish"),
                T("Обмен", "Алмаштыруу", "Exchange", "Değişim", "Almashtirish"),
                Row(T("Возврат: платье синее, 44", "Кайтаруу: көк көйнөк, 44", "Return: blue dress, 44", "İade: mavi elbise, 44", "Qaytarish: ko'k ko'ylak, 44"), "−2 500", MockTone.Bad),
                Row(T("Взамен: платье синее, 46", "Ордуна: көк көйнөк, 46", "Instead: blue dress, 46", "Karşılığında: mavi elbise, 46", "Evaziga: ko'k ko'ylak, 46"), "2 800"),
                Total(T("Доплатить", "Кошумча төлөө", "Top up", "Ek ödeme", "Qo'shimcha to'lash"), "300", true)),
            Step(T("4. Результат: обмен оформлен, разница учтена в кассе", "4. Натыйжа: алмаштыруу тариздетилди, айырма кассада эсепке алынды", "4. Result: the exchange is done and the difference is in the till", "4. Sonuç: değişim tamamlandı, fark kasada işlendi", "4. Natija: almashtirish rasmiylashtirildi, farq kassada hisobga olindi"),
                T("Обмен", "Алмаштыруу", "Exchange", "Değişim", "Almashtirish"),
                Note(T("Обмен оформлен: платье 46 выдано", "Алмаштыруу тариздетилди: 46-көйнөк берилди", "Exchange done: dress 46 handed over", "Değişim tamamlandı: 46 elbise verildi", "Almashtirish rasmiylashtirildi: 46-ko'ylak berildi"), MockTone.Good),
                Total(T("Принято доплаты", "Кошумча төлөм алынды", "Top-up received", "Alınan ek ödeme", "Qabul qilingan qo'shimcha to'lov"), "300")),
        ],
        () =>
        [
            Gain("sync", T("Обмен за минуту", "Алмаштыруу бир мүнөттө", "An exchange in a minute", "Bir dakikada değişim", "Bir daqiqada almashtirish"),
                T("Вернули одно, взяли другое — всё в одном окне возврата.", "Бирин кайтарды, экинчисин алды — баары сатууну кайтаруунун бир терезесинде.", "Return one item, take another — all in one return window.", "Birini iade et, diğerini al — hepsi tek iade penceresinde.", "Birini qaytardi, boshqasini oldi — hammasi qaytarishning bitta oynasida.")),
            Gain("cash", T("Разница считается сама", "Айырма өзү эсептелет", "The difference is calculated", "Fark kendiliğinden hesaplanır", "Farq o'zi hisoblanadi"),
                T("Было 2 500, взяли за 2 800 — касса покажет доплату 300.", "2 500 болчу, 2 800гө алышты — касса 300 кошумча төлөөнү көрсөтөт.", "It was 2,500, they took one for 2,800 — the till shows a top-up of 300.", "2 500 idi, 2 800'lük aldılar — kasa 300 ek ödeme gösterir.", "2 500 edi, 2 800 lik oldi — kassa 300 qo'shimcha to'lovni ko'rsatadi.")),
            Gain("check-circle-outline", T("Без счёта в уме", "Эсептебей эле", "No mental arithmetic", "Kafadan hesap yok", "Esda hisoblamasdan"),
                T("Кассир не считает разницу в уме — меньше ошибок и споров.", "Кассир айырманы эсинде эсептебейт — ката жана талаш азыраак.", "The cashier does not work out the difference in their head — fewer errors and disputes.", "Kasiyer farkı kafadan hesaplamaz — daha az hata ve anlaşmazlık.", "Kassir farqni esda hisoblamaydi — xato va nizolar kamroq.")),
        ]);

        // ── Серийные номера ──
        d["serials"] = new(() =>
        [
            Step(T("1. Добавьте телефон или ноутбук в чек", "1. Телефонду же ноутбукту чекке кошуңуз", "1. Add a phone or laptop to the receipt", "1. Telefonu veya dizüstü bilgisayarı fişe ekleyin", "1. Telefon yoki noutbukni chekka qo'shing"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Смартфон X12, 128 ГБ", "Смартфон X12, 128 ГБ", "Smartphone X12, 128 GB", "Akıllı telefon X12, 128 GB", "Smartfon X12, 128 GB"), "24 900", MockTone.Normal, true),
                Note(T("У товаров с отметкой «серийный номер» касса спросит номер", "«Сериялык номер» белгиси бар товарлардан касса номерди сурайт", "For products marked “serial number” the till asks for the number", "«Seri numarası» işaretli ürünlerde kasa numarayı sorar", "«Seriya raqami» belgisi bor tovarlarda kassa raqamni so'raydi"), MockTone.Accent)),
            Step(T("2. Введите или отсканируйте серийный номер или IMEI", "2. Сериялык номерди же IMEI киргизиңиз же сканерлеңиз", "2. Type or scan the serial number or IMEI", "2. Seri numarasını veya IMEI'yi yazın ya da okutun", "2. Seriya raqami yoki IMEI ni kiriting yoki skanerlang"),
                T("Серийный номер", "Сериялык номер", "Serial number", "Seri numarası", "Seriya raqami"),
                Input(T("Серийный номер или IMEI", "Сериялык номер же IMEI", "Serial number or IMEI", "Seri numarası veya IMEI", "Seriya raqami yoki IMEI"), "356 789 012 345 678", true),
                Btn(T("Добавить в чек", "Чекке кошуу", "Add to receipt", "Fişe ekle", "Chekka qo'shish"), true)),
            Step(T("3. Результат: номер записан в строку чека", "3. Натыйжа: номер чектин сабына жазылды", "3. Result: the number is stored in the receipt line", "3. Sonuç: numara fiş satırına yazıldı", "3. Natija: raqam chek qatoriga yozildi"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Смартфон X12, 128 ГБ", "Смартфон X12, 128 ГБ", "Smartphone X12, 128 GB", "Akıllı telefon X12, 128 GB", "Smartfon X12, 128 GB"), "24 900", MockTone.Normal, true, "IMEI 356 789 012 345 678"),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "24 900")),
        ],
        () =>
        [
            Gain("pound", T("Номер в строке чека", "Номер чектин сабында", "Number in the receipt line", "Numara fiş satırında", "Raqam chek qatorida"),
                T("Серийный номер или IMEI записывается в строку чека при продаже.", "Сериялык номер же IMEI сатууда чектин сабына жазылат.", "The serial number or IMEI is stored in the receipt line at the sale.", "Seri numarası veya IMEI satışta fiş satırına yazılır.", "Seriya raqami yoki IMEI sotuvda chek qatoriga yoziladi.")),
            Gain("barcode-scan", T("Ввод или скан", "Киргизүү же сканерлөө", "Type or scan", "Yazın ya da okutun", "Kiritish yoki skanerlash"),
                T("Номер можно набрать вручную или отсканировать сканером.", "Номерди кол менен терсе же сканер менен окуса болот.", "The number can be typed by hand or read with a scanner.", "Numara elle yazılabilir veya tarayıcıyla okutulabilir.", "Raqamni qo'lda terish yoki skaner bilan o'qish mumkin.")),
            Gain("shield-check-outline", T("Видно, что продано", "Эмне сатылганы көрүнөт", "You can see what was sold", "Neyin satıldığı görünür", "Nima sotilgani ko'rinadi"),
                T("В чеке указан именно тот аппарат, который получил покупатель.", "Чекте сатып алуучу алган түзмөк так көрсөтүлөт.", "The receipt shows exactly the device the customer received.", "Fişte müşterinin aldığı cihaz tam olarak belirtilir.", "Chekda xaridor olgan qurilma aniq ko'rsatiladi.")),
        ]);

        // ── Гарантия ──
        d["warranty"] = new(() =>
        [
            Step(T("1. Срок гарантии указан в карточке товара", "1. Кепилдик мөөнөтү товардын карточкасында көрсөтүлгөн", "1. The warranty term is set in the product card", "1. Garanti süresi ürün kartında belirtilir", "1. Kafolat muddati tovar kartasida ko'rsatiladi"),
                T("Карточка товара", "Товардын карточкасы", "Product card", "Ürün kartı", "Tovar kartasi"),
                Row(T("Ноутбук Nova 15", "Nova 15 ноутбугу", "Laptop Nova 15", "Dizüstü Nova 15", "Noutbuk Nova 15"), "46 000"),
                Input(T("Гарантия, месяцев", "Кепилдик, ай", "Warranty, months", "Garanti, ay", "Kafolat, oy"), "12", true),
                Note(T("Не задан — берётся по категории (у техники 12 месяцев)", "Коюлбаса — категория боюнча алынат (техникада 12 ай)", "If not set, the category's term is used (12 months for appliances)", "Girilmezse kategoriye göre alınır (cihazlarda 12 ay)", "Kiritilmasa — toifa bo'yicha olinadi (texnikada 12 oy)"))),
            Step(T("2. Срок гарантии печатается в чеке", "2. Кепилдик мөөнөтү чекте басылат", "2. The warranty term is printed in the receipt", "2. Garanti süresi fişe basılır", "2. Kafolat muddati chekda chop etiladi"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Ноутбук Nova 15", "Nova 15 ноутбугу", "Laptop Nova 15", "Dizüstü Nova 15", "Noutbuk Nova 15"), "46 000", MockTone.Normal, true, T("Гарантия: 12 мес.", "Кепилдик: 12 ай", "Warranty: 12 months", "Garanti: 12 ay", "Kafolat: 12 oy")),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "46 000")),
            Step(T("3. При возврате выберите причину «Гарантия»", "3. Кайтарууда «Кепилдик» себебин тандаңыз", "3. On a return choose the reason “Warranty”", "3. İadede «Garanti» nedenini seçin", "3. Qaytarishda «Kafolat» sababini tanlang"),
                T("Возврат продажи", "Сатууну кайтаруу", "Sale return", "Satış iadesi", "Sotuvni qaytarish"),
                Row(T("Смартфон X12", "Смартфон X12", "Smartphone X12", "Akıllı telefon X12", "Smartfon X12"), "24 900", MockTone.Normal, false, T("Куплен 8 мес. назад", "8 ай мурун сатып алынган", "Bought 8 months ago", "8 ay önce alındı", "8 oy oldin sotib olingan")),
                Row(T("Причина возврата", "Кайтаруу себеби", "Return reason", "İade nedeni", "Qaytarish sababi"), T("Гарантия", "Кепилдик", "Warranty", "Garanti", "Kafolat"), MockTone.Accent, true)),
            Step(T("4. Касса сразу показывает, действует ли гарантия", "4. Касса кепилдик иштеп жатканын дароо көрсөтөт", "4. The till shows at once whether the warranty is valid", "4. Kasa garantinin geçerli olup olmadığını hemen gösterir", "4. Kassa kafolat amal qilishini darhol ko'rsatadi"),
                T("Возврат продажи", "Сатууну кайтаруу", "Sale return", "Satış iadesi", "Sotuvni qaytarish"),
                Note(T("Гарантия действует: прошло 8 из 12 месяцев", "Кепилдик иштейт: 12 айдын 8и өттү", "The warranty is valid: 8 of 12 months have passed", "Garanti geçerli: 12 aydan 8'i geçti", "Kafolat amal qiladi: 12 oydan 8 tasi o'tdi"), MockTone.Good),
                Row(T("Осталось", "Калды", "Left", "Kalan", "Qoldi"), T("4 мес.", "4 ай", "4 months", "4 ay", "4 oy"), MockTone.Good, true)),
        ],
        () =>
        [
            Gain("shield-check-outline", T("Гарантия в чеке", "Кепилдик чекте", "Warranty in the receipt", "Fişte garanti", "Chekda kafolat"),
                T("Срок гарантии печатается в чеке — клиент видит его сразу.", "Кепилдик мөөнөтү чекте басылат — кардар аны дароо көрөт.", "The warranty term is printed in the receipt — the customer sees it at once.", "Garanti süresi fişe basılır — müşteri hemen görür.", "Kafolat muddati chekda chop etiladi — mijoz uni darhol ko'radi.")),
            Gain("calendar-range", T("Срок по умолчанию", "Демейки мөөнөт", "A default term", "Varsayılan süre", "Odatdagi muddat"),
                T("У техники — 12 месяцев; другой срок задаётся в карточке товара.", "Техникада — 12 ай; башка мөөнөт товардын карточкасында коюлат.", "12 months for appliances; another term is set in the product card.", "Cihazlarda 12 ay; başka süre ürün kartında girilir.", "Texnikada — 12 oy; boshqa muddat tovar kartasida belgilanadi.")),
            Gain("check-circle-outline", T("Проверка при возврате", "Кайтарууда текшерүү", "A check on return", "İadede kontrol", "Qaytarishda tekshiruv"),
                T("Причина «Гарантия» — касса сама сверяет срок с датой покупки.", "«Кепилдик» себеби — касса мөөнөттү сатып алуу датасы менен өзү салыштырат.", "With the reason “Warranty” the till checks the term against the purchase date.", "«Garanti» nedeninde kasa süreyi satın alma tarihiyle kendisi karşılaştırır.", "«Kafolat» sababi — kassa muddatni sotib olish sanasi bilan o'zi solishtiradi.")),
        ]);

        // ── Кроссы и аналоги ──
        d["cross_analogs"] = new(() =>
        [
            Step(T("1. В карточке детали запишите аналоги и кроссы", "1. Тетиктин карточкасына аналогдорду жана кросстарды жазыңыз", "1. In the part card, enter analogues and cross-references", "1. Parça kartına muadilleri ve çaprazları yazın", "1. Qism kartasiga analoglar va krosslarni yozing"),
                T("Карточка детали", "Тетиктин карточкасы", "Part card", "Parça kartı", "Qism kartasi"),
                Row(T("Фильтр масляный Toyota", "Toyota май чыпкасы", "Toyota oil filter", "Toyota yağ filtresi", "Toyota moy filtri"), "650", MockTone.Normal, false, "04152-YZZA1"),
                Input(T("Аналог (бренд и номер)", "Аналог (бренд жана номер)", "Analogue (brand and number)", "Muadil (marka ve numara)", "Analog (brend va raqam)"), "Mann W 712/94", true),
                Btn(T("Добавить аналог", "Аналог кошуу", "Add analogue", "Muadil ekle", "Analog qo'shish"), true)),
            Step(T("2. Нужной детали нет в наличии", "2. Керектүү тетик жок", "2. The needed part is out of stock", "2. Gereken parça stokta yok", "2. Kerakli qism omborda yo'q"),
                T("Касса", "Касса", "Till", "Kasa", "Kassa"),
                Row(T("Фильтр масляный Toyota", "Toyota май чыпкасы", "Toyota oil filter", "Toyota yağ filtresi", "Toyota moy filtri"), T("0 шт", "0 дана", "0 pcs", "0 adet", "0 dona"), MockTone.Bad, false, "04152-YZZA1"),
                Btn(T("Аналоги", "Аналогдор", "Analogues", "Muadiller", "Analoglar"), true)),
            Step(T("3. Кнопка «Аналоги» показывает подходящие замены", "3. «Аналогдор» баскычы ылайыктуу алмашмаларды көрсөтөт", "3. The “Analogues” button shows suitable substitutes", "3. «Muadiller» düğmesi uygun alternatifleri gösterir", "3. «Analoglar» tugmasi mos almashtiruvchilarni ko'rsatadi"),
                T("Аналоги", "Аналогдор", "Analogues", "Muadiller", "Analoglar"),
                Row("Mann", "540", MockTone.Normal, true, "W 712/94"),
                Row("Hyundai", "590", MockTone.Normal, false, "26300-35505"),
                Row("Bosch", "610", MockTone.Normal, false, "0 451 103 336")),
            Step(T("4. Результат: клиент берёт замену — продажа не потеряна", "4. Натыйжа: кардар алмашманы алат — сатуу жоголгон жок", "4. Result: the client takes the substitute — the sale is kept", "4. Sonuç: müşteri alternatifi alır — satış kaybolmaz", "4. Natija: mijoz almashtiruvchini oladi — sotuv yo'qolmadi"),
                T("Чек", "Чек", "Receipt", "Fiş", "Chek"),
                Row(T("Фильтр масляный Mann", "Mann май чыпкасы", "Mann oil filter", "Mann yağ filtresi", "Mann moy filtri"), "540", MockTone.Normal, false, "W 712/94"),
                Total(T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), "540"),
                Btn(T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"), true)),
        ],
        () =>
        [
            Gain("swap-horizontal", T("Замена всегда под рукой", "Алмашма дайыма жанында", "A substitute at hand", "Alternatif hep elinizde", "Almashtiruvchi doim qo'lingizda"),
                T("Кнопка «Аналоги» в карточке детали показывает подходящие замены.", "Тетиктин карточкасындагы «Аналогдор» баскычы ылайыктуу алмашмаларды көрсөтөт.", "The “Analogues” button in the part card shows suitable substitutes.", "Parça kartındaki «Muadiller» düğmesi uygun alternatifleri gösterir.", "Qism kartasidagi «Analoglar» tugmasi mos almashtiruvchilarni ko'rsatadi.")),
            Gain("clock-outline", T("Продажа не теряется", "Сатуу жоголбойт", "The sale is not lost", "Satış kaybolmaz", "Sotuv yo'qolmaydi"),
                T("Нет оригинала — кассир сразу предлагает аналог, а не отпускает клиента.", "Түп нуска жок — кассир кардарды жибербей, аналогду дароо сунуштайт.", "No original — the cashier offers an analogue at once instead of letting the client go.", "Orijinal yok — kasiyer müşteriyi göndermeden hemen muadil önerir.", "Original yo'q — kassir mijozni jo'natmay, darhol analog taklif qiladi.")),
            Gain("tag-text-outline", T("Бренды рядом", "Бренддер жанаша", "Brands side by side", "Markalar yan yana", "Brendlar yonma-yon"),
                T("У детали записываются кроссы и аналоги других брендов.", "Тетикке башка бренддердин кросстору жана аналогдору жазылат.", "A part stores the cross-references and analogues of other brands.", "Parçaya diğer markaların çaprazları ve muadilleri yazılır.", "Qismga boshqa brendlarning krosslari va analoglari yoziladi.")),
        ]);

        // ── Ремонт ──
        d["repair"] = new(() =>
        [
            Step(T("1. В меню кассы откройте «Ремонт и сервис» и нажмите «Новый приём»", "1. Касса менюсунан «Оңдоо жана тейлөө» ачып, «Жаңы кабыл алуу» басыңыз", "1. In the till menu open “Repair and service” and press “New intake”", "1. Kasa menüsünde «Tamir ve servis»i açıp «Yeni kabul»e basın", "1. Kassa menyusida «Ta'mirlash va servis» ni ochib, «Yangi qabul» ni bosing"),
                T("Ремонт и сервис", "Оңдоо жана тейлөө", "Repair and service", "Tamir ve servis", "Ta'mirlash va servis"),
                Btn(T("Новый приём", "Жаңы кабыл алуу", "New intake", "Yeni kabul", "Yangi qabul"), true),
                Row("Р-0001 · " + T("Смартфон X12", "Смартфон X12", "Smartphone X12", "Akıllı telefon X12", "Smartfon X12"), T("В работе", "Иште", "In progress", "İşlemde", "Jarayonda"), MockTone.Warn),
                Row("Р-0002 · " + T("Ноутбук Nova 15", "Nova 15 ноутбугу", "Laptop Nova 15", "Dizüstü Nova 15", "Noutbuk Nova 15"), T("Готово", "Даяр", "Ready", "Hazır", "Tayyor"), MockTone.Good)),
            Step(T("2. Впишите клиента, устройство и неисправность", "2. Кардарды, түзмөктү жана бузулууну жазыңыз", "2. Enter the customer, the device and the fault", "2. Müşteriyi, cihazı ve arızayı yazın", "2. Mijoz, qurilma va nosozlikni yozing"),
                T("Новый приём", "Жаңы кабыл алуу", "New intake", "Yeni kabul", "Yangi qabul"),
                Input(T("Клиент и телефон", "Кардар жана телефон", "Customer and phone", "Müşteri ve telefon", "Mijoz va telefon"), T("Асан · 0700 123 456", "Асан · 0700 123 456", "Asan · 0700 123 456", "Asan · 0700 123 456", "Asan · 0700 123 456")),
                Input(T("Устройство и IMEI", "Түзмөк жана IMEI", "Device and IMEI", "Cihaz ve IMEI", "Qurilma va IMEI"), "X12 · 356 789 012 345 678"),
                Input(T("Неисправность", "Бузулуу", "Fault", "Arıza", "Nosozlik"), T("Не включается", "Күйбөйт", "Does not turn on", "Açılmıyor", "Yonmaydi"), true)),
            Step(T("3. Печатается квитанция Р-0001; предоплата идёт в кассу", "3. Р-0001 квитанциясы басылат; алдын ала төлөм кассага салынат", "3. Receipt R-0001 is printed; the prepayment goes into the till", "3. R-0001 makbuzu basılır; ön ödeme kasaya girer", "3. R-0001 kvitansiyasi chop etiladi; oldindan to'lov kassaga kiritiladi"),
                T("Р-0001", "Р-0001", "R-0001", "R-0001", "R-0001"),
                Note(T("Квитанция Р-0001 напечатана, клиент и мастер расписались", "Р-0001 квитанциясы басылды, кардар жана уста кол койду", "Receipt R-0001 printed, customer and technician signed", "R-0001 makbuzu basıldı, müşteri ve usta imzaladı", "R-0001 kvitansiyasi chop etildi, mijoz va usta imzo chekdi"), MockTone.Good),
                Row(T("Предоплата", "Алдын ала төлөм", "Prepayment", "Ön ödeme", "Oldindan to'lov"), "500", MockTone.Normal, true, T("внесена в кассу", "кассага салынды", "put into the till", "kasaya girildi", "kassaga kiritildi")),
                Row(T("Срок готовности", "Даяр болуу мөөнөтү", "Due date", "Hazır olma tarihi", "Tayyor bo'lish muddati"), "28.11")),
            Step(T("4. Готово — нажмите «Написать клиенту»: откроется WhatsApp с текстом", "4. Даяр — «Кардарга жазуу» басыңыз: WhatsApp тексти менен ачылат", "4. Ready — press “Message the customer”: WhatsApp opens with the text", "4. Hazır — «Müşteriye yaz»a basın: WhatsApp metinle açılır", "4. Tayyor — «Mijozga yozish» ni bosing: WhatsApp matn bilan ochiladi"),
                T("Р-0001", "Р-0001", "R-0001", "R-0001", "R-0001"),
                Row(T("Статус", "Статусу", "Status", "Durum", "Status"), T("Готово", "Даяр", "Ready", "Hazır", "Tayyor"), MockTone.Good),
                Btn(T("Написать клиенту", "Кардарга жазуу", "Message the customer", "Müşteriye yaz", "Mijozga yozish"), true),
                Note(T("Сообщение уйдёт, только когда вы сами нажмёте «Отправить»", "Билдирүү сиз өзүңүз «Жөнөтүү» басканда гана кетет", "The message is sent only when you press Send yourself", "Mesaj yalnızca siz Gönder'e bastığınızda gider", "Xabar faqat siz «Yuborish» ni o'zingiz bosganingizda ketadi"))),
            Step(T("5. При выдаче — оплата работ и акт; предоплата засчитывается", "5. Берүүдө — иштин төлөмү жана акт; алдын ала төлөм эсепке алынат", "5. At hand-over — payment for the work and an act; the prepayment is counted", "5. Teslimde — iş ücreti ve tutanak; ön ödeme düşülür", "5. Topshirishda — ish to'lovi va dalolatnoma; oldindan to'lov hisobga olinadi"),
                T("Выдача", "Берүү", "Hand-over", "Teslim", "Topshirish"),
                Row(T("Работы", "Иштер", "Work", "İşçilik", "Ishlar"), "1 200"),
                Row(T("Предоплата", "Алдын ала төлөм", "Prepayment", "Ön ödeme", "Oldindan to'lov"), "−500", MockTone.Good),
                Total(T("К оплате", "Төлөнө турган", "To pay", "Ödenecek", "To'lanadi"), "700", true)),
        ]);

        // ── Trade-in ──
        d["trade_in"] = new(() =>
        [
            Step(T("1. В меню кассы откройте «Trade-in / скупка» и нажмите «Новый выкуп»", "1. Касса менюсунан «Trade-in / сатып алуу» ачып, «Жаңы сатып алуу» басыңыз", "1. In the till menu open “Trade-in / buy-back” and press “New buy-back”", "1. Kasa menüsünde «Trade-in / alım»ı açıp «Yeni alım»a basın", "1. Kassa menyusida «Trade-in / sotib olish» ni ochib, «Yangi sotib olish» ni bosing"),
                T("Trade-in / скупка", "Trade-in / сатып алуу", "Trade-in / buy-back", "Trade-in / alım", "Trade-in / sotib olish"),
                Btn(T("Новый выкуп", "Жаңы сатып алуу", "New buy-back", "Yeni alım", "Yangi sotib olish"), true),
                Row(T("Смартфон X12", "Смартфон X12", "Smartphone X12", "Akıllı telefon X12", "Smartfon X12"), T("Выкуплен", "Сатып алынды", "Bought", "Alındı", "Sotib olindi"), MockTone.Warn, false, T("Айбек · 0555 123 456", "Айбек · 0555 123 456", "Aibek · 0555 123 456", "Aybek · 0555 123 456", "Aybek · 0555 123 456")),
                Row(T("Ноутбук Nova 15", "Nova 15 ноутбугу", "Laptop Nova 15", "Dizüstü Nova 15", "Noutbuk Nova 15"), T("На складе", "Кампада", "In stock", "Stokta", "Omborda"), MockTone.Good)),
            Step(T("2. Впишите данные продавца и устройства", "2. Сатуучунун жана түзмөктүн маалыматын жазыңыз", "2. Enter the seller's and the device's details", "2. Satıcının ve cihazın bilgilerini yazın", "2. Sotuvchi va qurilma ma'lumotlarini yozing"),
                T("Новый выкуп", "Жаңы сатып алуу", "New buy-back", "Yeni alım", "Yangi sotib olish"),
                Input(T("Продавец и телефон", "Сатуучу жана телефон", "Seller and phone", "Satıcı ve telefon", "Sotuvchi va telefon"), T("Айбек · 0555 123 456", "Айбек · 0555 123 456", "Aibek · 0555 123 456", "Aybek · 0555 123 456", "Aybek · 0555 123 456")),
                Input(T("Устройство и IMEI", "Түзмөк жана IMEI", "Device and IMEI", "Cihaz ve IMEI", "Qurilma va IMEI"), "X12 · 356 789 012 345 678"),
                Input(T("Состояние и комплектация", "Абалы жана комплектация", "Condition and included items", "Durum ve teslim edilenler", "Holati va komplektatsiya"), T("Хорошее, с коробкой", "Жакшы, куту менен", "Good, with the box", "İyi, kutusuyla", "Yaxshi, quti bilan"), true)),
            Step(T("3. Укажите сумму выкупа и выберите: выплата или зачёт", "3. Сатып алуу суммасын көрсөтүп, тандаңыз: төлөө же эсептөө", "3. Enter the buy-back amount and choose: payout or credit", "3. Alım tutarını girin ve seçin: ödeme veya mahsup", "3. Sotib olish summasini kiriting va tanlang: to'lov yoki hisobga olish"),
                T("Расчёт", "Эсептешүү", "Settlement", "Hesaplama", "Hisob-kitob"),
                Total(T("Сумма выкупа", "Сатып алуу суммасы", "Buy-back amount", "Alım tutarı", "Sotib olish summasi"), "9 000", true),
                Chips(0, T("Выплата из кассы", "Кассадан төлөө", "Payout from the till", "Kasadan ödeme", "Kassadan to'lov"), T("Зачёт в чеке покупки", "Сатып алуу чегинде эсептөө", "Credit in a purchase", "Alışveriş fişinde mahsup", "Xarid chekida hisobga olish"))),
            Step(T("4. Печатается акт; принятое идёт на склад как «Б/у …»", "4. Акт басылат; кабыл алынганы кампага «Б/у …» катары кирет", "4. The act is printed; the item goes into stock as “Used …”", "4. Tutanak basılır; alınan ürün depoya «İkinci el …» olarak girer", "4. Dalolatnoma chop etiladi; qabul qilingani omborga «B/u …» sifatida kiradi"),
                T("Акт приёма", "Кабыл алуу актысы", "Acceptance act", "Kabul tutanağı", "Qabul dalolatnomasi"),
                Note(T("Акт напечатан, выплата записана в кассе", "Акт басылды, төлөм кассада жазылды", "The act is printed, the payout is recorded in the till", "Tutanak basıldı, ödeme kasada kaydedildi", "Dalolatnoma chop etildi, to'lov kassada yozildi"), MockTone.Good),
                Row(T("Б/у Смартфон X12", "Б/у Смартфон X12", "Used Smartphone X12", "İkinci el Akıllı telefon X12", "B/u Smartfon X12"), "9 000", MockTone.Normal, true, T("штрихкод — серийный номер", "штрихкод — сериялык номер", "barcode — the serial number", "barkod — seri numarası", "shtrixkod — seriya raqami"))),
        ]);

        // 2026-11-26 (просьба координатора): «Статус ремонта на витрине» — сервер модуль и признак repair_status_public пока не присылает («Скоро»); сценарий — по заданию.
        d[ModuleIds.RepairStatusSite] = new(() =>
        [
            Step(T("1. Принимаете в ремонт «Samsung A54», предоплата 500 сом", "1. «Samsung A54» оңдоого кабыл аласыз, алдын ала төлөм 500 сом", "1. You accept a “Samsung A54” for repair, prepayment 500 som", "1. «Samsung A54» tamire alırsınız, ön ödeme 500 som", "1. «Samsung A54» ni ta'mirga qabul qilasiz, oldindan to'lov 500 so'm"),
                T("Приём в ремонт", "Оңдоого кабыл алуу", "Repair intake", "Tamire kabul", "Ta'mirga qabul"),
                Input(T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma"), "Samsung A54"),
                Input(T("Предоплата, сом", "Алдын ала төлөм, сом", "Prepayment, som", "Ön ödeme, som", "Oldindan to'lov, so'm"), "500", true),
                Btn(T("Принять", "Кабыл алуу", "Accept", "Kabul et", "Qabul qilish"), true)),
            Step(T("2. На квитанции — код Р-0001 и ссылка на витрину", "2. Квитанцияда — Р-0001 коду жана витринага шилтеме", "2. The receipt has the code Р-0001 and a storefront link", "2. Makbuzda Р-0001 kodu ve vitrin bağlantısı var", "2. Kvitansiyada Р-0001 kodi va vitrinaga havola bor"),
                T("Квитанция", "Квитанция", "Receipt", "Makbuz", "Kvitansiya"),
                Row("Samsung A54", "Р-0001", MockTone.Accent, true, T("Принят, предоплата 500 сом", "Кабыл алынды, алдын ала төлөм 500 сом", "Accepted, prepayment 500 som", "Kabul edildi, ön ödeme 500 som", "Qabul qilindi, oldindan to'lov 500 so'm")),
                Row(T("Ссылка", "Шилтеме", "Link", "Bağlantı", "Havola"), "/catalog/shop/repair"),
                Note(T("Клиент проверяет ремонт сам: звонить в магазин не нужно", "Кардар оңдоону өзү текшерет: дүкөнгө чалуунун кереги жок", "The customer checks the repair alone: no need to call the shop", "Müşteri tamiri kendisi kontrol eder: mağazayı aramaya gerek yok", "Mijoz ta'mirni o'zi tekshiradi: do'konga qo'ng'iroq qilish shart emas"))),
            Step(T("3. Клиент вводит код и 4 цифры телефона — видит «В работе, срок 30.11»", "3. Кардар кодду жана телефондун 4 цифрасын киргизет — «Иште, мөөнөтү 30.11» көрөт", "3. The customer enters the code and 4 phone digits and sees “In progress, due 30.11”", "3. Müşteri kodu ve telefonun 4 hanesini girer, «Devam ediyor, süre 30.11» görür", "3. Mijoz kod va telefonning 4 raqamini kiritadi — «Jarayonda, muddat 30.11» ni ko'radi"),
                T("Витрина: статус ремонта", "Витрина: оңдоонун абалы", "Storefront: repair status", "Vitrin: tamir durumu", "Vitrina: ta'mir holati"),
                Input(T("Код квитанции", "Квитанция коду", "Receipt code", "Makbuz kodu", "Kvitansiya kodi"), "Р-0001"),
                Input(T("Последние 4 цифры телефона", "Телефондун акыркы 4 цифрасы", "Last 4 digits of the phone", "Telefonun son 4 hanesi", "Telefonning oxirgi 4 raqami"), "4821", true),
                Row(T("Статус", "Абалы", "Status", "Durum", "Holat"), T("В работе", "Иште", "In progress", "Devam ediyor", "Jarayonda"), MockTone.Warn, true),
                Row(T("Срок", "Мөөнөтү", "Due date", "Süre", "Muddat"), "30.11")),
            Step(T("4. Мастер жмёт «Готово» — клиент получает сообщение в Telegram", "4. Уста «Даяр» басат — кардар Telegram'да билдирме алат", "4. The technician presses “Done” and the customer gets a Telegram message", "4. Usta «Hazır»a basar, müşteri Telegram'da mesaj alır", "4. Usta «Tayyor» ni bosadi — mijoz Telegram'da xabar oladi"),
                T("Ремонт", "Оңдоо", "Repair", "Tamir", "Ta'mir"),
                Row("Samsung A54", "Р-0001", MockTone.Normal, false, T("Было: в работе", "Мурда: иште", "Was: in progress", "Önceki: devam ediyor", "Oldin: jarayonda")),
                Btn(T("Готово", "Даяр", "Done", "Hazır", "Tayyor"), true),
                Note(T("Telegram клиенту: «Ремонт Samsung A54 готов, ждём вас»", "Кардарга Telegram: «Samsung A54 оңдолуп бүттү, күтөбүз»", "Telegram to the customer: “Your Samsung A54 repair is ready”", "Müşteriye Telegram: «Samsung A54 tamiri hazır, sizi bekliyoruz»", "Mijozga Telegram: «Samsung A54 ta'miri tayyor, sizni kutamiz»"), MockTone.Good)),
            Step(T("5. На странице — «Готов, к доплате 1 200 сом»", "5. Баракта — «Даяр, кошумча төлөм 1 200 сом»", "5. The page shows “Ready, 1 200 som left to pay”", "5. Sayfada «Hazır, ödenecek 1 200 som» yazar", "5. Sahifada — «Tayyor, qo'shimcha to'lov 1 200 so'm»"),
                T("Витрина: статус ремонта", "Витрина: оңдоонун абалы", "Storefront: repair status", "Vitrin: tamir durumu", "Vitrina: ta'mir holati"),
                Row(T("Статус", "Абалы", "Status", "Durum", "Holat"), T("Готов", "Даяр", "Ready", "Hazır", "Tayyor"), MockTone.Good, true),
                Row(T("К доплате", "Кошумча төлөм", "Left to pay", "Ödenecek", "Qo'shimcha to'lov"), T("1 200 сом", "1 200 сом", "1 200 som", "1 200 som", "1 200 so'm"), MockTone.Normal, true),
                Row(T("Срок", "Мөөнөтү", "Due date", "Süre", "Muddat"), "30.11")),
        ],
        () =>
        [
            Gain("cellphone-message", T("Клиент не звонит", "Кардар чалбайт", "No calls from customers", "Müşteri aramaz", "Mijoz qo'ng'iroq qilmaydi"),
                T("Клиент сам смотрит статус на вашей витрине по коду квитанции.", "Кардар абалын витринаңыздан квитанциянын коду боюнча өзү көрөт.", "The customer checks the status on your storefront by the receipt code.", "Müşteri durumu vitrininizden makbuz koduyla kendisi görür.", "Mijoz holatni vitrinangizdan kvitansiya kodi bo'yicha o'zi ko'radi.")),
            Gain("send-outline", T("Сообщение в Telegram", "Telegram'да билдирме", "A Telegram message", "Telegram mesajı", "Telegram xabari"),
                T("Когда ремонт готов, клиент сразу получает сообщение в Telegram.", "Оңдоо бүткөндө кардар Telegram'да дароо билдирме алат.", "When the repair is ready, the customer gets a Telegram message at once.", "Tamir hazır olunca müşteri hemen Telegram'da mesaj alır.", "Ta'mir tayyor bo'lganda mijoz darhol Telegram'da xabar oladi.")),
            Gain("clock-outline", T("Срок и сумма видны", "Мөөнөт жана сумма көрүнөт", "Due date and amount shown", "Süre ve tutar görünür", "Muddat va summa ko'rinadi"),
                T("Видно: принят, в работе, готов, выдан, срок и сумма к оплате.", "Көрүнөт: кабыл алынды, иште, даяр, берилди, мөөнөт жана төлөнүүчү сумма.", "Shown: accepted, in progress, ready, handed over, the due date and the amount to pay.", "Görünür: kabul edildi, devam ediyor, hazır, teslim edildi, süre ve ödenecek tutar.", "Ko'rinadi: qabul qilindi, jarayonda, tayyor, berildi, muddat va to'lanadigan summa.")),
        ]);
    }
}
