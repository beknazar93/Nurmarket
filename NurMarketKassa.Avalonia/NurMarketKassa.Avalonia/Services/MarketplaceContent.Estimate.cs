namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-19 («Смета», ТЗ 19.11.2026): контент страницы «подробно» для модуля «Смета» (Маркетплейс). Только тексты на 5 языках.
internal static partial class MarketplaceContent
{
    private static void Estimate(MarketplaceItem i)
    {
        i.IconKey = "calculator-variant-outline";
        i.Tagline = T("Посчитайте ремонт за 2 минуты: сколько брать и на какую сумму", "Оңдоону 2 мүнөттө эсептеңиз: канча алуу керек жана кандай сумма", "Work out a renovation in 2 minutes: what to buy and for how much", "Tadilatı 2 dakikada hesaplayın: ne almalı ve kaç para", "Ta'mirni 2 daqiqada hisoblang: nima olish kerak va qancha pul");
        i.KeyFacts =
        [
            T("Расход по площади", "Аянт боюнча сарптоо", "Consumption by area", "Alana göre tüketim", "Maydon bo'yicha sarf"),
            T("Печать и WhatsApp", "Басуу жана WhatsApp", "Print and WhatsApp", "Yazdırma ve WhatsApp", "Chop etish va WhatsApp"),
            T("В чек одним нажатием", "Чекке бир басуу менен", "To the receipt in one tap", "Tek dokunuşla fişe", "Bir bosishda chekka"),
        ];
        i.Benefits =
        [
            B("calculator-variant-outline", T("Калькулятор материала", "Материал калькулятору", "Material calculator", "Malzeme hesaplayıcı", "Material kalkulyatori"),
                T("Краска, грунт, плитка, ламинат, обои, гипсокартон, шпаклёвка, штукатурка, клей. Размеры комнаты, проёмы вычитаются, запас на подрезку, округление вверх до целых упаковок. Под результатом — формула.",
                  "Боёк, грунт, плитка, ламинат, обои, гипсокартон, шпаклёвка, штукатурка, желим. Бөлмөнүн өлчөмдөрү, тешиктер кемитилет, кесүүгө запас, бүтүн таңгакка чейин өйдө тегеректөө. Жыйынтыктын астында — формула.",
                  "Paint, primer, tiles, laminate, wallpaper, drywall, putty, plaster, adhesive. Room dimensions, openings subtracted, an allowance for cutting, rounding up to whole packs. The formula is shown under the result.",
                  "Boya, astar, fayans, laminat, duvar kağıdı, alçıpan, macun, sıva, yapıştırıcı. Oda ölçüleri, boşluklar düşülür, kesim için fire payı, tam pakete yukarı yuvarlama. Sonucun altında formül görünür.",
                  "Bo'yoq, grunt, plitka, laminat, oboy, gipsokarton, shpatlyovka, suvoq, yelim. Xona o'lchamlari, teshiklar ayiriladi, kesish uchun zaxira, butun qadoqqa yuqoriga yaxlitlash. Natija ostida — formula.")),
            B("file-document-outline", T("Смета для клиента", "Кардар үчүн смета", "An estimate for the customer", "Müşteri için keşif", "Mijoz uchun smeta"),
                T("Разделы («Стены», «Пол», «Работа»), свои позиции, цена розница или «прораб», скидка по строке и общая, срок «действует до». Печать A4 и на ленту, текст для WhatsApp или Telegram.",
                  "Бөлүмдөр («Дубалдар», «Пол», «Иш»), өз позициялар, чекене же «прораб» баасы, сап жана жалпы арзандатуу, «чейин жарактуу» мөөнөтү. A4 жана лентага басуу, WhatsApp же Telegram үчүн текст.",
                  "Sections (“Walls”, “Floor”, “Work”), custom lines, retail or “foreman” price, line and overall discount, a “valid until” date. Print on A4 or the receipt tape, text for WhatsApp or Telegram.",
                  "Bölümler («Duvarlar», «Zemin», «İş»), özel kalemler, perakende veya «usta» fiyatı, satır ve genel indirim, «geçerlilik» tarihi. A4 ve fiş şeridine baskı, WhatsApp veya Telegram için metin.",
                  "Bo'limlar («Devorlar», «Pol», «Ish»), o'z pozitsiyalar, chakana yoki «prorab» narxi, qator va umumiy chegirma, «amal qiladi» muddati. A4 va lentaga chop etish, WhatsApp yoki Telegram uchun matn.")),
            B("cart-arrow-down", T("В чек одним нажатием", "Чекке бир басуу менен", "To the receipt in one tap", "Tek dokunuşla fişe", "Bir bosishda chekka"),
                T("«Добавить всё в чек» (в кассе): по текущим ценам; если цена или остаток изменились с момента сметы — покажем что именно. Работа и доставка уходят строкой «Доп. услуга». После этого смета — «Принята».",
                  "«Баарын чекке кошуу» (кассада): азыркы баа боюнча; смета түзүлгөндөн бери баа же калдык өзгөрсө — так эмне экенин көрсөтөбүз. Иш жана жеткирүү «Кошумча кызмат» сабы менен кетет. Андан кийин смета — «Кабыл алынды».",
                  "“Add all to the receipt” (at the till): at current prices; if a price or stock changed since the estimate, we show exactly what. Work and delivery go as an “Extra service” line. Then the estimate is “Accepted”.",
                  "«Hepsini fişe ekle» (kasada): güncel fiyatlarla; keşiften beri fiyat veya stok değiştiyse tam olarak neyin değiştiğini gösteririz. İş ve teslimat «Ek hizmet» satırı olarak gider. Sonra keşif «Kabul edildi» olur.",
                  "«Hammasini chekka qo'shish» (kassada): hozirgi narxlar bo'yicha; smeta tuzilgandan beri narx yoki qoldiq o'zgargan bo'lsa — aynan nima ekanini ko'rsatamiz. Ish va yetkazib berish «Qo'shimcha xizmat» qatori bilan ketadi. Shundan so'ng smeta — «Qabul qilindi».")),
            B("lock-outline", T("Закупка — только владельцу", "Сатып алуу баасы — ээсине гана", "Purchase prices — owner only", "Alış fiyatı — yalnızca sahibe", "Xarid narxi — faqat egasiga"),
                T("Кассир видит цены продажи. Себестоимость и маржа по смете — у владельца и у сотрудников с правом на закупку; в печати и в тексте клиенту их нет.",
                  "Кассир сатуу баасын көрөт. Смета боюнча өздүк нарк жана маржа — ээсинде жана сатып алуу укугу бар кызматкерлерде; кардарга басууда жана текстте алар жок.",
                  "The cashier sees selling prices. Cost and margin of the estimate are for the owner and staff with the purchase right; they are never in the printout or the customer text.",
                  "Kasiyer satış fiyatlarını görür. Keşfin maliyeti ve marjı sahibe ve alış hakkı olan personele aittir; müşteriye basılı çıktıda ve metinde yoktur.",
                  "Kassir sotuv narxlarini ko'radi. Smeta bo'yicha tannarx va marja — egasida va xarid huquqi bor xodimlarda; mijozga chop etishda va matnda ular yo'q.")),
        ];
        i.HowTo =
        [
            T("Нажмите «Смета» в меню кассы (☰ или «Ещё») или в программе владельца: Товары → Смета.", "Касса менюсунан (☰ же «Дагы») «Смета» басыңыз же ээсинин программасында: Товарлар → Смета.", "Press “Estimate” in the till menu (☰ or “More”) or in the owner's program: Products → Estimate.", "Kasa menüsünde (☰ veya «Daha fazla») «Keşif»e basın veya sahip programında: Ürünler → Keşif.", "Kassa menyusida (☰ yoki «Yana») «Smeta» ni bosing yoki egasining dasturida: Mahsulotlar → Smeta."),
            T("«Новая смета» → «Рассчитать материал»: введите размеры комнаты, выберите материал и товар.", "«Жаңы смета» → «Материалды эсептөө»: бөлмөнүн өлчөмдөрүн киргизиңиз, материалды жана товарды тандаңыз.", "“New estimate” → “Calculate material”: enter the room size, pick the material and the product.", "«Yeni keşif» → «Malzeme hesapla»: oda ölçülerini girin, malzemeyi ve ürünü seçin.", "«Yangi smeta» → «Materialni hisoblash»: xona o'lchamlarini kiriting, material va tovarni tanlang."),
            T("Распечатайте или отправьте клиенту. Когда клиент решит — «Добавить всё в чек».", "Басып чыгарыңыз же кардарга жөнөтүңүз. Кардар чечим кабыл алганда — «Баарын чекке кошуу».", "Print it or send it to the customer. When the customer decides — “Add all to the receipt”.", "Yazdırın veya müşteriye gönderin. Müşteri karar verince — «Hepsini fişe ekle».", "Chop eting yoki mijozga yuboring. Mijoz qaror qilganda — «Hammasini chekka qo'shish»."),
        ];
        i.Audience =
        [
            T("Строительные магазины и базы, хозтовары", "Курулуш дүкөндөрү жана базалар, чарба буюмдары", "Building-material shops and depots, hardware stores", "Yapı market ve depoları, hırdavatçılar", "Qurilish do'konlari va bazalari, xo'jalik mollari"),
            T("Продавцы, которые считают ремонт за клиента", "Кардар үчүн оңдоону эсептеген сатуучулар", "Sellers who price a renovation for the customer", "Müşteri için tadilat hesaplayan satıcılar", "Mijoz uchun ta'mirni hisoblaydigan sotuvchilar"),
        ];
        i.PriceTerms = MarketplaceModules.Terms(i.Id); // 2026-11-21: цена — с сервера (public/modules), прежнее «Бесплатно» убрано
    }
}
