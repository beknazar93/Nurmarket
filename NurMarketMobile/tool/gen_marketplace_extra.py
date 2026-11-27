# 2026-11-27: ручные тексты Маркетплейса мобилки для модулей сервера, у которых в кассе пока нет записи «что делает» (Specs/Lines в MarketplaceModuleContent*.cs).
# На 27.11.2026 это модули сферы «Автозапчасти». Тексты написаны по демонстрации и «Что получите» этих же модулей в кассе (MarketplaceDemos.*).
# Когда в кассе появится запись, она главнее: генератор берёт отсюда только недостающие поля (gen_marketplace_content.py, EXTRA).
def _t(ru, ky, en, tr, uz):
    return dict(ru=ru, ky=ky, en=en, tr=tr, uz=uz)


_ENABLE = _t('Включите модуль на этой странице.', 'Модулду ушул беттен күйгүзүңүз.', 'Turn the module on on this page.', 'Modülü bu sayfadan açın.', 'Modulni shu sahifadan yoqing.')
_PARTS = _t('Автозапчасти', 'Автозапчасттар', 'Auto parts', 'Oto yedek parça', 'Avtoehtiyot qismlar')

MODULES = {
    'condition': dict(
        icon='star-half-full', color='#0F766E',
        line=_t('Отмечает деталь «Новая» или «Б/У» — видно в списке и в чеке.', 'Тетикти «Жаңы» же «Колдонулган» деп белгилейт — тизмеде жана чекте көрүнөт.',
                'Marks a part as “New” or “Used” — shown in the list and on the receipt.', 'Parçayı «Yeni» veya «İkinci el» olarak işaretler — listede ve fişte görünür.',
                "Detalni «Yangi» yoki «B/U» deb belgilaydi — ro'yxatda va chekda ko'rinadi."),
        short=_t('Новая или Б/У: состояние детали записано в карточке', 'Жаңы же колдонулган: тетиктин абалы карточкада жазылган',
                 'New or used: the condition of a part is written in its card', 'Yeni veya ikinci el: parçanın durumu kartında yazılı', "Yangi yoki b/u: detalning holati kartasida yozilgan"),
        what=_t('В карточке детали вы отмечаете состояние: новая или Б/У. Оно видно кассиру в списке, а покупателю — в чеке. Пример: фара с разборки не путается с новой — покупатель заранее знает, что берёт, поэтому меньше споров и возвратов.',
                'Тетиктин карточкасында абалын белгилейсиз: жаңы же колдонулган. Ал кассирге тизмеде, сатып алуучуга чекте көрүнөт. Мисал: талкалоодон алынган фара жаңысы менен аралашпайт — сатып алуучу эмнени алганын алдын ала билет, ошондуктан талаш жана кайтаруу азыраак.',
                'In the part card you mark its condition: new or used. The cashier sees it in the list and the customer sees it on the receipt. Example: a headlight from a dismantled car is never mixed up with a new one — the customer knows in advance what they are buying, so there are fewer disputes and returns.',
                'Parça kartında durumunu işaretlersiniz: yeni veya ikinci el. Kasiyer bunu listede, müşteri fişte görür. Örnek: söküm aracından çıkan far yenisiyle karışmaz — müşteri ne aldığını önceden bilir, bu yüzden anlaşmazlık ve iade azalır.',
                "Detal kartasida holatini belgilaysiz: yangi yoki b/u. Kassir buni ro'yxatda, xaridor chekda ko'radi. Misol: demontajdan olingan faralar yangisi bilan adashmaydi — xaridor nima olayotganini oldindan biladi, shuning uchun nizo va qaytarishlar kamayadi."),
        steps=[_ENABLE,
               _t('В карточке детали выберите состояние: «Новая» или «Б/У».', 'Тетиктин карточкасында абалын тандаңыз: «Жаңы» же «Колдонулган».', 'In the part card choose the condition: “New” or “Used”.', 'Parça kartında durumu seçin: «Yeni» veya «İkinci el».', "Detal kartasida holatni tanlang: «Yangi» yoki «B/U».")],
        audience=[_PARTS,
                  _t('Магазины с деталями с разборки', 'Талкалоодон алынган тетиктери бар дүкөндөр', 'Shops selling parts from dismantled cars', 'Söküm parça satan mağazalar', "Demontaj detallari bor do'konlar")]),
    'storage_cell': dict(
        icon='view-grid-outline', color='#4F46E5',
        line=_t('Ячейка хранения: стеллаж и полка рядом с деталью — склад обходить не надо.', 'Сактоо уяжасы: тетиктин жанында текче — кампаны кыдырбайсыз.',
                'Storage cell: the rack and shelf next to the part — no walking around the warehouse.', 'Depolama gözü: parçanın yanında raf bilgisi — depoyu dolaşmaya gerek yok.',
                "Saqlash katagi: detal yonida stellaj va javon — omborni aylanib yurish shart emas."),
        short=_t('Знаете, где лежит каждая деталь: стеллаж и полка', 'Ар бир тетик кайда жатканын билесиз: текче жана текчедеги орду',
                 'You know where every part lies: rack and shelf', 'Her parçanın nerede durduğunu bilirsiniz: raf ve göz', "Har bir detal qayerda yotganini bilasiz: stellaj va javon"),
        what=_t('У детали записана ячейка: стеллаж и полка. Кассир ищет деталь и сразу видит, куда идти, — склад обходить не надо. Если деталь переложили, ячейку меняют в карточке. Пример: «Стеллаж 3, полка B» рядом с фильтром в списке.',
                'Тетикке уяча жазылат: текче жана анын орду. Кассир тетикти издеп, кайда барарын дароо көрөт — кампаны кыдыруунун кереги жок. Тетик башка жерге коюлса, уяча карточкада өзгөртүлөт. Мисал: тизмеде чыпканын жанында «Текче 3, B».',
                'A part has a storage cell written down: rack and shelf. The cashier searches for the part and sees where to go at once — no need to walk around the warehouse. If the part is moved, the cell is changed in its card. Example: “Rack 3, shelf B” next to a filter in the list.',
                'Parçaya bir göz yazılır: raf ve bölme. Kasiyer parçayı arar ve nereye gideceğini hemen görür — depoyu dolaşmak gerekmez. Parça taşındıysa göz kartta değiştirilir. Örnek: listede filtrenin yanında «Raf 3, bölme B».',
                "Detalga katak yoziladi: stellaj va javon. Kassir detalni qidiradi va qayerga borishni darrov ko'radi — omborni aylanib chiqish shart emas. Detal ko'chirilsa, katak kartada o'zgartiriladi. Misol: ro'yxatda filtr yonida «Stellaj 3, javon B»."),
        steps=[_ENABLE,
               _t('В карточке детали впишите ячейку: стеллаж и полку.', 'Тетиктин карточкасына уячаны жазыңыз: текче жана анын орду.', 'In the part card enter the cell: rack and shelf.', 'Parça kartına gözü yazın: raf ve bölme.', "Detal kartasiga katakni yozing: stellaj va javon.")],
        audience=[_PARTS,
                  _t('Склады с большим числом деталей', 'Тетиги көп склады бар дүкөндөр', 'Warehouses with many parts', 'Çok parçalı depolar', "Detali ko'p omborlar")]),
    'sto_price': dict(
        icon='percent', color='#B45309',
        line=_t('Мастеру и СТО касса сама ставит оптовую цену — без ручного пересчёта.', 'Устага жана СТО-го касса дүң баанын өзү коёт — кол менен кайра эсептебейсиз.',
                'The till sets the wholesale price for mechanics and service stations — no manual recalculation.', 'Kasa usta ve servise toptan fiyatı kendisi uygular — elle hesap yok.',
                "Usta va STO uchun kassa ulgurji narxni o'zi qo'yadi — qo'lda qayta hisoblash yo'q."),
        short=_t('Цена СТО: мастерам — своя оптовая цена автоматически', 'СТО баасы: усталарга — өз дүң баасы автоматтык түрдө',
                 'Service-station price: mechanics get their wholesale price automatically', 'Servis fiyatı: ustalara kendi toptan fiyatları otomatik', "STO narxi: ustalarga — o'z ulgurji narxi avtomatik"),
        what=_t('В карточке детали указывается цена СТО (оптовая). Когда в чеке выбран клиент с отметкой «СТО / мастер», касса сама ставит эту цену. Пример: 900 вместо 1 100. Если цены СТО нет или она выше розничной — остаётся розничная, а кассир видит подсказку.',
                'Тетиктин карточкасында СТО баасы (дүң) көрсөтүлөт. Чекте «СТО / уста» белгиси бар кардар тандалганда, касса бул баа өзү коёт. Мисал: 1 100 ордуна 900. СТО баасы жок болсо же чекене баадан жогору болсо — чекене баа калат, кассир кеңеш көрөт.',
                'The part card holds a service-station (wholesale) price. When a customer marked “STO / mechanic” is chosen in the receipt, the till applies that price itself. Example: 900 instead of 1,100. If there is no such price or it is above retail, the retail price stays and the cashier sees a hint.',
                'Parça kartında servis (toptan) fiyatı yer alır. Fişte «Servis / usta» işaretli müşteri seçilince kasa bu fiyatı kendisi uygular. Örnek: 1.100 yerine 900. Servis fiyatı yoksa veya perakendeden yüksekse perakende fiyat kalır, kasiyer ipucu görür.',
                "Detal kartasida STO (ulgurji) narxi ko'rsatiladi. Chekda «STO / usta» belgisi bor mijoz tanlansa, kassa bu narxni o'zi qo'yadi. Misol: 1 100 o'rniga 900. STO narxi yo'q yoki chakanadan baland bo'lsa — chakana narx qoladi, kassir maslahat ko'radi."),
        steps=[_ENABLE,
               _t('В карточке детали укажите цену СТО, а у клиента отметьте «СТО / мастер».', 'Тетиктин карточкасында СТО баасын көрсөтүп, кардарда «СТО / уста» деп белгилеңиз.', 'In the part card enter the service-station price and mark the customer “STO / mechanic”.', 'Parça kartına servis fiyatını girin, müşteriyi «Servis / usta» olarak işaretleyin.', "Detal kartasida STO narxini kiriting, mijozni «STO / usta» deb belgilang.")],
        audience=[_PARTS,
                  _t('Магазины, которые продают мастерам и СТО', 'Усталарга жана СТО-го сатган дүкөндөр', 'Shops selling to mechanics and service stations', 'Usta ve servislere satan mağazalar', "Usta va STO'ga sotadigan do'konlar")]),
    'return_rules': dict(
        icon='undo', color='#BE123C',
        line=_t('Срок возврата и запрет на установленные детали — проверяет касса.', 'Кайтаруу мөөнөтүн жана орнотулган тетиктерге тыюуну касса текшерет.',
                'The till checks the return term and the ban on installed parts.', 'İade süresini ve takılmış parça yasağını kasa kontrol eder.',
                "Qaytarish muddati va o'rnatilgan detallarga taqiqni kassa tekshiradi."),
        short=_t('Правила возврата: срок и запрет на установленные детали', 'Кайтаруу эрежелери: мөөнөт жана орнотулган тетиктерге тыюу',
                 'Return rules: the term and a ban on installed parts', 'İade kuralları: süre ve takılmış parça yasağı', "Qaytarish qoidalari: muddat va o'rnatilgan detallarga taqiq"),
        what=_t('В настройках сферы задаются срок возврата (по умолчанию 14 дней) и запрет на возврат установленных деталей и электрики. Кассир ничего не считает по памяти: касса сама проверяет срок и не даёт оформить обычный возврат вне правил. Пример: деталь куплена 5 дней назад — возврат разрешён, 20 дней назад — нет.',
                'Сфераны орнотууларында кайтаруу мөөнөтү (демейки 14 күн) жана орнотулган тетиктерди жана электриканы кайтарууга тыюу коюлат. Кассир эч нерсени эстеп эсептебейт: касса мөөнөттү өзү текшерет жана эрежеден тышкары кадимки кайтарууга жол бербейт. Мисал: тетик 5 күн мурун сатылган — кайтарууга болот, 20 күн мурун — болбойт.',
                'The sphere settings hold the return term (14 days by default) and a ban on returning installed parts and electrical items. The cashier does not count from memory: the till checks the term itself and will not allow an ordinary return outside the rules. Example: a part bought 5 days ago can be returned, 20 days ago cannot.',
                'Alan ayarlarında iade süresi (varsayılan 14 gün) ile takılmış parça ve elektrik ürünlerinin iade yasağı belirlenir. Kasiyer hafızayla saymaz: kasa süreyi kendisi kontrol eder ve kurallar dışında olağan iadeye izin vermez. Örnek: 5 gün önce alınan parça iade edilebilir, 20 gün önce alınan edilemez.',
                "Soha sozlamalarida qaytarish muddati (odatda 14 kun) va o'rnatilgan detallar hamda elektrikani qaytarishga taqiq belgilanadi. Kassir xotiradan hisoblamaydi: kassa muddatni o'zi tekshiradi va qoidadan tashqari oddiy qaytarishga yo'l qo'ymaydi. Misol: detal 5 kun oldin olingan — qaytarish mumkin, 20 kun oldin — mumkin emas."),
        steps=[_ENABLE,
               _t('В настройках сферы задайте срок возврата и запрет на установленные детали.', 'Сфераны орнотууларында кайтаруу мөөнөтүн жана орнотулган тетиктерге тыюуну коюңуз.', 'In the sphere settings set the return term and the ban on installed parts.', 'Alan ayarlarında iade süresini ve takılmış parça yasağını belirleyin.', "Soha sozlamalarida qaytarish muddati va o'rnatilgan detallarga taqiqni belgilang.")],
        audience=[_PARTS,
                  _t('Магазины, где возвраты бывают спорными', 'Кайтаруу боюнча талаш болгон дүкөндөр', 'Shops where returns are often disputed', 'İadelerin tartışmalı olduğu mağazalar', "Qaytarishlar bo'yicha nizo bo'ladigan do'konlar")]),
}


# Названия модулей на 5 языках (сервер в public/modules отдаёт только ru и en; в кассе для ky/tr/uz берётся то, что пришло, иначе русское).
# Приложение берёт название так: язык сервера → это поле → русское. Новый модуль сервера = строка здесь (и в кассе: Specs/Lines).
NAMES = {
    'scales_labels': _t('Весы с этикетками', 'Этикеткалуу таразылар', 'Scales and labels', 'Etiketli teraziler', 'Etiketli tarozilar'),
    'expiry_batches': _t('Сроки годности и партии', 'Жарактуулук мөөнөтү жана партиялар', 'Expiry dates and batches', 'Son kullanma tarihi ve partiler', 'Yaroqlilik muddati va partiyalar'),
    'quick_items': _t('Быстрые товары', 'Ыкчам товарлар', 'Quick items', 'Hızlı ürünler', 'Tezkor mahsulotlar'),
    'fractional_qty': _t('Дробное количество', 'Бөлчөк сан', 'Fractional quantity', 'Kesirli miktar', 'Kasr miqdor'),
    'packages': _t('Упаковки', 'Таңгактар', 'Packages', 'Ambalajlar', 'Qadoqlar'),
    'client_price': _t('Цена по клиенту', 'Кардар боюнча баа', 'Client price', 'Müşteriye özel fiyat', "Mijoz bo'yicha narx"),
    'price_levels': _t('Уровни цен', 'Баа деңгээлдери', 'Price levels', 'Fiyat seviyeleri', 'Narx darajalari'),
    'estimate': _t('Смета', 'Смета', 'Estimate', 'Keşif', 'Smeta'),
    'material_calc': _t('Калькулятор материалов', 'Материал калькулятору', 'Material calculator', 'Malzeme hesaplayıcı', 'Material kalkulyatori'),
    'rental': _t('Прокат', 'Прокат', 'Rental', 'Kiralama', 'Prokat'),
    'variants': _t('Размеры и цвета', 'Өлчөмдөр жана түстөр', 'Sizes and colours', 'Beden ve renkler', "O'lchamlar va ranglar"),
    'consultant': _t('Консультант', 'Консультант', 'Consultant', 'Danışman', 'Maslahatchi'),
    'exchange': _t('Обмен', 'Алмаштыруу', 'Exchange', 'Değişim', 'Almashtirish'),
    'serials': _t('Серийные номера', 'Сериялык номерлер', 'Serial numbers', 'Seri numaraları', 'Seriya raqamlari'),
    'warranty': _t('Гарантия', 'Кепилдик', 'Warranty', 'Garanti', 'Kafolat'),
    'repair': _t('Ремонт', 'Оңдоо', 'Repair', 'Tamir', "Ta'mir"),
    'trade_in': _t('Trade-in', 'Trade-in', 'Trade-in', 'Trade-in', 'Trade-in'),
    'part_numbers': _t('Каталожный номер и OEM', 'Каталог номери жана OEM', 'Part number and OEM', 'Katalog numarası ve OEM', 'Katalog raqami va OEM'),
    'cross_analogs': _t('Кроссы и аналоги', 'Кросстор жана аналогдор', 'Cross references and analogs', 'Çaprazlar ve muadiller', 'Krosslar va analoglar'),
    'condition': _t('Состояние (новое / б/у)', 'Абалы (жаңы / колдонулган)', 'Condition (new / used)', 'Durum (yeni / ikinci el)', 'Holati (yangi / b/u)'),
    'storage_cell': _t('Ячейка хранения', 'Сактоо уяжасы', 'Storage cell', 'Depolama gözü', 'Saqlash katagi'),
    'sto_price': _t('Цена СТО', 'СТО баасы', 'Service-station price', 'Servis fiyatı', 'STO narxi'),
    'return_rules': _t('Правила возврата', 'Кайтаруу эрежелери', 'Return rules', 'İade kuralları', 'Qaytarish qoidalari'),
    'customer_orders': _t('Заказ под клиента', 'Кардарга буйрутма', 'Customer orders', 'Müşteri siparişi', 'Mijozga buyurtma'),
    'category_tiles': _t('Быстрые плитки категорий', 'Категориялардын ыкчам плиткалары', 'Category tiles', 'Hızlı kategori kutuları', 'Kategoriyalarning tezkor plitkalari'),
    'volume_price': _t('Цена по количеству', 'Санына жараша баа', 'Volume price', 'Miktara göre fiyat', 'Miqdorga qarab narx'),
    'haggle': _t('Торг', 'Соодалашуу', 'Haggling', 'Pazarlık', 'Savdolashuv'),
    'currencies': _t('Курсы валют', 'Валюта курстары', 'Currencies', 'Döviz kurları', 'Valyuta kurslari'),
    'invoice_cargo': _t('Накладная и карго (опт)', 'Накладной жана карго (дүң)', 'Invoice and cargo (wholesale)', 'İrsaliye ve kargo (toptan)', 'Nakladnoy va kargo (ulgurji)'),
    'repair_status_site': _t('Статус ремонта на витрине', 'Витринада оңдоонун абалы', 'Repair status on the storefront', 'Vitrinde tamir durumu', "Vitrinada ta'mir holati"),
}
