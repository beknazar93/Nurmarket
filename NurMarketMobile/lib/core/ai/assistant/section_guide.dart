// 2026-11-27 (просьба владельца «дай Нур-советнику доступы ко всем новым функциям и разделам»): описание каждого экрана мобилки
// для советника — что делает раздел (ru, ky, uz, en, tr), чего в телефоне НЕТ (акции, диалоги бота, ремонт — это программа
// владельца и сайт), что нового и как называется каждый инструмент в строке «Использовано: …». Чистая таблица без Flutter и сети.
// Тест test/advisor_tools_test.dart сверяет её с реестром экранов: НОВЫЙ ЭКРАН БЕЗ ОПИСАНИЯ ЗДЕСЬ — КРАСНЫЙ ТЕСТ.
import 'screen_registry.dart';

/// Описание экрана: «что делает» на 5 языках в порядке [kNavLangs] (ru, ky, uz, en, tr).
const kSectionGuide = <String, List<String>>{
  'home': [
    'Главная: быстрые кнопки продажи, смены, склада и управления, цифры дня.',
    'Башкы бет: сатуу, смена, кампа жана башкаруунун ылдам баскычтары, күндүн цифралары.',
    "Bosh sahifa: sotuv, smena, ombor va boshqaruvning tezkor tugmalari, kun raqamlari.",
    'Home: quick buttons for sale, shift, stock and management, today\'s figures.',
    'Ana sayfa: satış, vardiya, depo ve yönetim kısayolları, günün rakamları.',
  ],
  'sale': [
    'Продажа: корзина, поиск и скан товара, скидка, отложить чек, переход к оплате.',
    'Сатуу: себет, товарды издөө жана сканерлөө, арзандатуу, чекти токтотуу, төлөмгө өтүү.',
    "Sotuv: savat, tovarni qidirish va skanerlash, chegirma, chekni kechiktirish, to'lovga o'tish.",
    'Sale: cart, product search and scan, discount, hold a receipt, go to payment.',
    'Satış: sepet, ürün arama ve tarama, indirim, fişi beklemeye alma, ödemeye geçiş.',
  ],
  'scan': [
    'Сканер: камера считывает штрихкод — проверка цены или добавление в продажу.',
    'Сканер: камера штрих-кодду окуйт — баа текшерүү же сатууга кошуу.',
    "Skaner: kamera shtrix-kodni o'qiydi — narxni tekshirish yoki sotuvga qo'shish.",
    'Scanner: the camera reads a barcode — price check or add to the sale.',
    'Tarayıcı: kamera barkodu okur — fiyat sorgulama veya satışa ekleme.',
  ],
  'checks': [
    'Чеки: список продаж с поиском; чек можно открыть, показать QR и ссылку покупателю, оформить возврат.',
    'Чектер: издөөсү бар сатуулар тизмеси; чекти ачып, QR жана шилтемени көрсөтүп, кайтаруу жасаса болот.',
    "Cheklar: qidiruvli sotuvlar ro'yxati; chekni ochish, QR va havolani ko'rsatish, qaytarish rasmiylashtirish mumkin.",
    'Receipts: list of sales with search; open a receipt, show a QR and link to the buyer, make a return.',
    'Fişler: aramalı satış listesi; fiş açılır, alıcıya QR ve bağlantı gösterilir, iade yapılır.',
  ],
  'check': [
    'Чек: состав, суммы, оплата и QR одного чека.',
    'Чек: бир чектин курамы, суммалары, төлөмү жана QR.',
    "Chek: bitta chekning tarkibi, summalari, to'lovi va QR.",
    'Receipt: lines, totals, payment and QR of one receipt.',
    'Fiş: tek bir fişin kalemleri, tutarları, ödemesi ve QR kodu.',
  ],
  'stock': [
    'Склад: товары и остатки, фильтры «мало / нет / в наличии», поиск, карточка товара, скан и поиск по фото.',
    'Кампа: товарлар жана калдыктар, «аз / жок / бар» чыпкалары, издөө, товар картасы, скан жана сүрөт боюнча издөө.',
    "Ombor: tovarlar va qoldiqlar, «kam / yo'q / bor» filtrlari, qidiruv, tovar kartasi, skan va rasm bo'yicha qidiruv.",
    'Stock: products and quantities, filters low / out / in stock, search, product card, scan and photo search.',
    'Depo: ürünler ve stok, az / yok / var filtreleri, arama, ürün kartı, tarama ve fotoğrafla arama.',
  ],
  'stock_receive': [
    'Приход (простой): быстро добавить количество к товару без поставщика.',
    'Кириш (жөнөкөй): товарга жеткирүүчүсүз тез санын кошуу.',
    "Kirim (oddiy): tovarga yetkazib beruvchisiz tezda miqdor qo'shish.",
    'Simple receipt: quickly add quantity to a product without a supplier.',
    'Basit giriş: tedarikçi olmadan ürüne hızlıca miktar ekleme.',
  ],
  'stock_audit': [
    'Ревизия: пересчёт остатков, сверка с учётом и корректировка расхождений.',
    'Ревизия: калдыктарды кайра саноо, эсеп менен салыштыруу жана айырмаларды оңдоо.',
    "Reviziya: qoldiqlarni qayta sanash, hisob bilan solishtirish va farqlarni tuzatish.",
    'Stock count: recount the stock, compare with the books and fix differences.',
    'Sayım: stokları yeniden sayma, kayıtla karşılaştırma ve farkları düzeltme.',
  ],
  'stock_import': [
    'Массовая загрузка: добавить много товаров из файла (Excel / CSV).',
    'Жалпы жүктөө: файлдан (Excel / CSV) көп товар кошуу.',
    "Ommaviy yuklash: fayldan (Excel / CSV) ko'p tovar qo'shish.",
    'Bulk import: add many products from a file (Excel / CSV).',
    'Toplu yükleme: dosyadan (Excel / CSV) çok sayıda ürün ekleme.',
  ],
  'product': [
    'Карточка товара: название, цены, штрихкод, остаток, фото, партии и сроки.',
    'Товар картасы: аталышы, баалары, штрих-код, калдык, сүрөт, партиялар жана мөөнөттөр.',
    "Tovar kartasi: nomi, narxlar, shtrix-kod, qoldiq, rasm, partiyalar va muddatlar.",
    'Product card: name, prices, barcode, quantity, photo, batches and expiry dates.',
    'Ürün kartı: ad, fiyatlar, barkod, stok, fotoğraf, partiler ve son kullanma tarihleri.',
  ],
  'pay': [
    'Оплата: наличные, карта, QR, долг или смешанная оплата одной продажи.',
    'Төлөм: накталай, карта, QR, карыз же аралаш төлөм.',
    "To'lov: naqd, karta, QR, qarz yoki aralash to'lov.",
    'Payment: cash, card, QR, debt or mixed payment of one sale.',
    'Ödeme: nakit, kart, QR, borç veya karma ödeme.',
  ],
  'paid': [
    'Оплачено: итог после оплаты — чек, QR для покупателя, печать.',
    'Төлөндү: төлөмдөн кийинки жыйынтык — чек, сатып алуучуга QR, басып чыгаруу.',
    "To'landi: to'lovdan keyingi natija — chek, xaridor uchun QR, chop etish.",
    'Paid: the result after payment — receipt, QR for the buyer, printing.',
    'Ödendi: ödeme sonrası özet — fiş, alıcı için QR, yazdırma.',
  ],
  'close_shift': [
    'Закрытие смены: итоги смены, пересчёт наличных, Z-отчёт.',
    'Сменаны жабуу: смена жыйынтыгы, накталайды саноо, Z-отчёт.',
    "Smenani yopish: smena yakunlari, naqdni sanash, Z-hisobot.",
    'Close shift: shift totals, cash count, Z report.',
    'Vardiya kapatma: vardiya özeti, nakit sayımı, Z raporu.',
  ],
  'profile': [
    'Профиль: язык, тема, режим магазина, принтер, связь с кассой, настройки помощника и ИИ, выход.',
    'Профиль: тил, тема, дүкөн режими, принтер, кассага байланыш, жардамчы жана ИИ жөндөөлөрү, чыгуу.',
    "Profil: til, mavzu, do'kon rejimi, printer, kassa bilan aloqa, yordamchi va AI sozlamalari, chiqish.",
    'Profile: language, theme, shop mode, printer, till link, assistant and AI settings, sign out.',
    'Profil: dil, tema, mağaza modu, yazıcı, kasa bağlantısı, yardımcı ve YZ ayarları, çıkış.',
  ],
  'ai_log': [
    'Действия ИИ: журнал того, что помощник сделал по вашим просьбам, с отменой.',
    'ИИ аракеттери: жардамчы сиздин өтүнүчүңүз боюнча эмне кылганынын журналы, жокко чыгаруу менен.',
    "AI harakatlari: yordamchi so'rovlaringiz bo'yicha nima qilgani jurnali, bekor qilish bilan.",
    'AI actions: a log of what the assistant did on your requests, with undo.',
    'YZ işlemleri: yardımcının isteklerinize göre yaptıklarının günlüğü, geri alma ile.',
  ],
  'assistant_settings': [
    'Помощник: режим работы ИИ — выполнять по просьбе, спрашивать перед деньгами или только смотреть.',
    'Жардамчы: ИИ иштөө режими — суроо боюнча аткаруу, акчадан мурун сурап алуу же көрүү гана.',
    "Yordamchi: AI ish rejimi — so'rov bo'yicha bajarish, pul oldidan so'rash yoki faqat ko'rish.",
    'Assistant: AI mode — act on request, ask before money, or view only.',
    'Yardımcı: YZ çalışma modu — istek üzerine yap, paradan önce sor veya sadece görüntüle.',
  ],
  'sync': [
    'Синхронизация: очередь неотправленных данных и кнопка «Отправить сейчас».',
    'Шайкештирүү: жөнөтүлө элек маалыматтар кезеги жана «Азыр жөнөтүү» баскычы.',
    "Sinxronlash: yuborilmagan ma'lumotlar navbati va «Hozir yuborish» tugmasi.",
    'Sync: the queue of unsent data and the "Send now" button.',
    'Senkronizasyon: gönderilmemiş verilerin sırası ve «Şimdi gönder» düğmesi.',
  ],
  'cash': [
    'Внесение и изъятие: деньги в кассу и из кассы в течение смены.',
    'Салуу жана алуу: смена учурунда кассага акча салуу жана алуу.',
    "Kirim va chiqim: smena davomida kassaga pul kiritish va chiqarish.",
    'Cash in/out: put money into or take it out of the till during the shift.',
    'Kasa giriş çıkış: vardiya sırasında kasaya para koyma ve alma.',
  ],
  'debts': [
    'Долги клиентов: кто сколько должен, погашение, напоминание в WhatsApp.',
    'Кардарлардын карыздары: ким канча карыз, төлөө, WhatsApp эскертмеси.',
    "Mijozlar qarzlari: kim qancha qarzdor, to'lash, WhatsApp eslatmasi.",
    'Client debts: who owes how much, repayment, a WhatsApp reminder.',
    'Müşteri borçları: kim ne kadar borçlu, tahsilat, WhatsApp hatırlatması.',
  ],
  'held': [
    'Отложенные: чеки, которые поставили на паузу, чтобы вернуться к ним позже.',
    'Кечиктирилген: кийин кайтып келүү үчүн токтотулган чектер.',
    "Kechiktirilgan: keyinroq qaytish uchun to'xtatib qo'yilgan cheklar.",
    'Held checks: receipts put on pause to come back to later.',
    'Bekleyen: daha sonra dönmek için beklemeye alınan fişler.',
  ],
  'search': [
    'Поиск: общий поиск по товарам и клиентам.',
    'Издөө: товарлар жана кардарлар боюнча жалпы издөө.',
    "Qidiruv: tovarlar va mijozlar bo'yicha umumiy qidiruv.",
    'Search: a general search over products and clients.',
    'Arama: ürünlerde ve müşterilerde genel arama.',
  ],
  'rentals': [
    'Прокат: выданные в аренду позиции, сроки и возвраты.',
    'Ижара: ижарага берилген товарлар, мөөнөттөр жана кайтаруулар.',
    "Ijara: ijaraga berilgan pozitsiyalar, muddatlar va qaytarishlar.",
    'Rentals: items given for rent, due dates and returns.',
    'Kiralama: kiraya verilen kalemler, süreler ve iadeler.',
  ],
  'sizes': [
    'Размеры и цвета: остатки одежды и обуви по размерам и цветам.',
    'Өлчөмдөр жана түстөр: кийим жана бут кийимдин өлчөм жана түс боюнча калдыктары.',
    "O'lcham va ranglar: kiyim va poyabzal qoldiqlari o'lcham va rang bo'yicha.",
    'Sizes and colors: clothing and footwear stock by size and color.',
    'Bedenler ve renkler: giyim ve ayakkabı stoğu beden ve renge göre.',
  ],
  'rental_new': [
    'Новый прокат: оформить выдачу позиции в аренду клиенту.',
    'Жаңы ижара: кардарга товарды ижарага берүүнү расмийлештирүү.',
    "Yangi ijara: mijozga pozitsiyani ijaraga berishni rasmiylashtirish.",
    'New rental: register an item rented out to a client.',
    'Yeni kiralama: müşteriye kalem kiralamayı kaydetme.',
  ],
  'admin': [
    'Управление: меню разделов владельца и администратора — аналитика, заказы, клиенты, закупки, сотрудники, настройки.',
    'Башкаруу: ээсинин жана администратордун бөлүмдөрү — аналитика, заказдар, кардарлар, сатып алуулар, кызматкерлер, жөндөөлөр.',
    "Boshqaruv: egasi va administrator bo'limlari — analitika, buyurtmalar, mijozlar, xaridlar, xodimlar, sozlamalar.",
    'Management: the owner and administrator menu — analytics, orders, clients, purchasing, staff, settings.',
    'Yönetim: sahip ve yönetici menüsü — analiz, siparişler, müşteriler, satın alma, personel, ayarlar.',
  ],
  'prices': [
    'Цены товара: смотреть и менять цену продажи, закупки и опта.',
    'Товар баалары: сатуу, сатып алуу жана дүң баасын көрүү жана өзгөртүү.',
    "Tovar narxlari: sotish, xarid va ulgurji narxni ko'rish va o'zgartirish.",
    'Product prices: view and change the sale, purchase and wholesale price.',
    'Ürün fiyatları: satış, alış ve toptan fiyatı görme ve değiştirme.',
  ],
  'shifts': [
    'Смены: история кассовых смен, выручка, сотрудник, расхождения.',
    'Смендер: кассалык смендердин тарыхы, киреше, кызматкер, айырмалар.',
    "Smenalar: kassa smenalari tarixi, tushum, xodim, farqlar.",
    'Shifts: history of cash shifts, revenue, cashier, differences.',
    'Vardiyalar: kasa vardiyaları geçmişi, ciro, kasiyer, farklar.',
  ],
  'orders': [
    'Заказы: заказы с сайта и онлайн, статусы, код выдачи.',
    'Буйрутмалар: сайттан жана онлайн заказдар, статустар, берүү коду.',
    "Buyurtmalar: saytdan va onlayn buyurtmalar, statuslar, berish kodi.",
    'Orders: website and online orders, statuses, pickup code.',
    'Siparişler: web sitesi ve online siparişler, durumlar, teslim kodu.',
  ],
  'pick': [
    'Сборка заказа: собрать позиции заказа по скану.',
    'Заказды жыйноо: заказдын позицияларын скан менен чогултуу.',
    "Buyurtmani yig'ish: buyurtma pozitsiyalarini skan bilan yig'ish.",
    'Order picking: collect the order lines by scanning.',
    'Sipariş toplama: sipariş kalemlerini tarayarak toplama.',
  ],
  'clients': [
    'Клиенты: база клиентов, бонусы, история покупок, долги.',
    'Кардарлар: кардарлар базасы, бонустар, сатып алуулар тарыхы, карыздар.',
    "Mijozlar: mijozlar bazasi, bonuslar, xaridlar tarixi, qarzlar.",
    'Clients: the client base, bonuses, purchase history, debts.',
    'Müşteriler: müşteri tabanı, bonuslar, alışveriş geçmişi, borçlar.',
  ],
  'purchase': [
    'Заказ поставщику: список того, что заканчивается, и сообщение поставщику в WhatsApp.',
    'Жеткирүүчүгө заказ: түгөнүп жаткандардын тизмеси жана жеткирүүчүгө WhatsApp билдирүүсү.',
    "Yetkazib beruvchiga buyurtma: tugayotganlar ro'yxati va yetkazib beruvchiga WhatsApp xabari.",
    'Supplier order: a list of what is running out and a WhatsApp message to the supplier.',
    'Tedarikçi siparişi: azalan ürünlerin listesi ve tedarikçiye WhatsApp mesajı.',
  ],
  'deletions': [
    'Удалено из чека: журнал удалённых позиций — кто, что и когда убрал.',
    'Чектен өчүрүлгөн: өчүрүлгөн позициялар журналы — ким, эмнени жана качан алып салган.',
    "Chekdan o'chirilgan: o'chirilgan pozitsiyalar jurnali — kim, nimani va qachon olib tashlagan.",
    'Deleted items: a log of removed lines — who removed what and when.',
    'Silinenler: silinen kalemlerin günlüğü — kim, neyi, ne zaman sildi.',
  ],
  'receive': [
    'Приёмка с поставщиком: принять товар по накладной — количество, закупка, цена продажи, оплата.',
    'Жеткирүүчүдөн кабыл алуу: товарды накладной боюнча кабыл алуу — саны, сатып алуу баасы, сатуу баасы, төлөм.',
    "Yetkazib beruvchidan qabul: tovarni nakladnoy bo'yicha qabul qilish — miqdor, xarid, sotuv narxi, to'lov.",
    'Receive from supplier: receive goods by invoice — quantity, cost, sale price, payment.',
    'Tedarikçiden mal kabul: faturaya göre mal kabulü — miktar, alış, satış fiyatı, ödeme.',
  ],
  'invoice': [
    'Накладная: загрузить накладную из файла или фото и принять по ней товар.',
    'Накладная: накладнойду файлдан же сүрөттөн жүктөп, товарды кабыл алуу.',
    "Nakladnoy: nakladnoyni fayl yoki rasmdan yuklab, tovarni qabul qilish.",
    'Invoice: load an invoice from a file or photo and receive goods by it.',
    'İrsaliye: irsaliyeyi dosya veya fotoğraftan yükleyip malı kabul etme.',
  ],
  'smartcam': [
    'Умная камера: распознаёт накладную, ценники и товары на полке по фото.',
    'Акылдуу камера: накладнойду, баа жазууларын жана текчедеги товарларды сүрөт боюнча тааныйт.',
    "Aqlli kamera: nakladnoy, narx yorliqlari va javondagi tovarlarni rasm bo'yicha taniydi.",
    'Smart camera: recognizes an invoice, price labels and shelf products from a photo.',
    'Akıllı kamera: fotoğraftan fatura, etiket ve raftaki ürünleri tanır.',
  ],
  'staff': [
    'Сотрудники и права: список сотрудников, их доступы и роли.',
    'Кызматкерлер жана укуктар: кызматкерлердин тизмеси, алардын жеткиликтүүлүгү жана ролдору.',
    "Xodimlar va huquqlar: xodimlar ro'yxati, ularning ruxsatlari va rollari.",
    'Staff and permissions: the staff list, their access and roles.',
    'Personel ve yetkiler: personel listesi, erişimleri ve rolleri.',
  ],
  'payqr': [
    'QR-оплата: реквизиты QR для оплаты по ссылке и QR на чеке.',
    'QR төлөм: төлөм шилтемеси жана чектеги QR үчүн реквизиттер.',
    "QR to'lov: to'lov havolasi va chekdagi QR uchun rekvizitlar.",
    'QR payment: the QR details used for payment links and the QR on a receipt.',
    'QR ödeme: ödeme bağlantısı ve fişteki QR için bilgiler.',
  ],
  'debt_access': [
    'Доступ к долгам: кому из сотрудников разрешено видеть и гасить долги.',
    'Карыздарга жеткиликтүүлүк: кайсы кызматкерлерге карыздарды көрүүгө жана төлөөгө уруксат.',
    "Qarzlarga ruxsat: qaysi xodimlarga qarzlarni ko'rish va yopishga ruxsat.",
    'Debt access: which employees may see and repay debts.',
    'Borç erişimi: hangi çalışanların borçları görüp tahsil edebileceği.',
  ],
  'audit': [
    'Журнал аудита: кто и что менял в приложении — цены, остатки, возвраты.',
    'Аудит журналы: колдонмодо ким эмнени өзгөрткөн — баалар, калдыктар, кайтаруулар.',
    "Audit jurnali: ilovada kim nimani o'zgartirgan — narxlar, qoldiqlar, qaytarishlar.",
    'Audit log: who changed what in the app — prices, stock, returns.',
    'Denetim günlüğü: uygulamada kim neyi değiştirdi — fiyatlar, stok, iadeler.',
  ],
  'notifications': [
    'Уведомления: заказы, долги и товары, которые заканчиваются.',
    'Билдирмелер: заказдар, карыздар жана түгөнүп жаткан товарлар.',
    "Bildirishnomalar: buyurtmalar, qarzlar va tugayotgan tovarlar.",
    'Notifications: orders, debts and products running out.',
    'Bildirimler: siparişler, borçlar ve azalan ürünler.',
  ],
  'quotes': [
    'Коммерческое предложение: собрать предложение клиенту из товаров и отправить PDF.',
    'Коммерциялык сунуш: кардарга товарлардан сунуш түзүп, PDF жөнөтүү.',
    "Tijorat taklifi: mijozga tovarlardan taklif tuzib, PDF yuborish.",
    'Quotes: build an offer for a client from products and send it as a PDF.',
    'Teklifler: ürünlerden müşteriye teklif hazırlayıp PDF gönderme.',
  ],
  'advisor': [
    'Нур Советник: чат и звонок с ИИ — вопросы о продажах, складе, долгах, функциях и открытие разделов.',
    'Нур Кеңешчи: ИИ менен чат жана чалуу — сатуу, кампа, карыз, функциялар жөнүндө суроолор жана бөлүмдөрдү ачуу.',
    "Nur Maslahatchi: AI bilan chat va qo'ng'iroq — sotuv, ombor, qarz, funksiyalar haqida savollar va bo'limlarni ochish.",
    'Nur Advisor: chat and call with the AI — questions about sales, stock, debts, features, and opening sections.',
    'Nur Danışman: YZ ile sohbet ve görüşme — satış, depo, borç, özellikler soruları ve bölüm açma.',
  ],
  'summary': [
    'Сводка: выручка, прибыль, чеки, сравнение периодов, деньги по способам оплаты.',
    'Жыйынтык: киреше, пайда, чектер, мезгилдерди салыштыруу, төлөм ыкмалары боюнча акча.',
    "Xulosa: tushum, foyda, cheklar, davrlarni solishtirish, to'lov usullari bo'yicha pul.",
    'Summary: revenue, profit, checks, period comparison, money by payment method.',
    'Özet: ciro, kâr, fişler, dönem karşılaştırması, ödeme yöntemine göre para.',
  ],
  'abc': [
    'ABC-анализ: какие товары дают основную выручку (диаграмма Парето).',
    'ABC-талдоо: кайсы товарлар негизги кирешени берет (Парето диаграммасы).',
    "ABC-tahlil: qaysi tovarlar asosiy tushumni beradi (Pareto diagrammasi).",
    'ABC analysis: which products bring most of the revenue (Pareto chart).',
    'ABC analizi: hangi ürünler cironun çoğunu getirir (Pareto grafiği).',
  ],
  'esf': [
    'ЭСФ: электронная счёт-фактура — подготовка и выписка.',
    'ЭСФ: электрондук эсеп-фактура — даярдоо жана жазуу.',
    "ESF: elektron hisob-faktura — tayyorlash va yozish.",
    'ESF: the electronic invoice — preparing and issuing it.',
    'ESF: elektronik fatura — hazırlama ve kesme.',
  ],
  'marketplace': [
    'Маркетплейс: функции и тарифы компании — что включено, что можно подключить, цены с сервера, описание и демо каждой функции.',
    'Маркетплейс: компаниянын функциялары жана тарифтери — эмне кошулган, эмнени кошууга болот, сервердин баалары, ар бир функциянын сүрөттөмөсү.',
    "Marketplace: kompaniya funksiyalari va tariflari — nima yoqilgan, nimani ulash mumkin, serverdan narxlar, har bir funksiyaning tavsifi.",
    'Marketplace: company features and plans — what is on, what can be added, server prices, a description and demo of each feature.',
    'Marketplace: şirket özellikleri ve tarifeleri — nelerin açık olduğu, nelerin eklenebileceği, sunucu fiyatları, her özelliğin açıklaması.',
  ],
  'new_product': [
    'Новый товар: завести карточку товара, одежды или услуги.',
    'Жаңы товар: товардын, кийимдин же кызматтын картасын түзүү.',
    "Yangi tovar: tovar, kiyim yoki xizmat kartasini yaratish.",
    'New product: create a card for a product, clothing or service.',
    'Yeni ürün: ürün, giyim veya hizmet kartı oluşturma.',
  ],
  'tasks': [
    'Задачи: поручения сотрудникам и их выполнение.',
    'Тапшырмалар: кызматкерлерге тапшырмалар жана алардын аткарылышы.',
    "Vazifalar: xodimlarga topshiriqlar va ularning bajarilishi.",
    'Tasks: assignments for employees and their completion.',
    'Görevler: çalışanlara görevler ve tamamlanmaları.',
  ],
  'my_tasks': [
    'Мои задачи: поручения, которые поставили лично вам.',
    'Менин тапшырмаларым: сизге жеке коюлган тапшырмалар.',
    "Mening vazifalarim: sizga shaxsan qo'yilgan topshiriqlar.",
    'My tasks: assignments given to you personally.',
    'Görevlerim: size verilen görevler.',
  ],
  'salary': [
    'Зарплата: начисления сотрудникам за период, табель и выплаты.',
    'Айлык: мезгил боюнча кызматкерлерге эсептөөлөр, табель жана төлөмдөр.',
    "Maosh: davr bo'yicha xodimlarga hisoblashlar, tabel va to'lovlar.",
    'Salary: accruals for employees over a period, timesheet and payouts.',
    'Maaş: dönem için çalışan hakedişleri, puantaj ve ödemeler.',
  ],
  'printer': [
    'Принтер: подключить чековый принтер и проверить печать.',
    'Принтер: чек принтерин туташтыруу жана басып чыгарууну текшерүү.',
    "Printer: chek printerini ulash va chop etishni tekshirish.",
    'Printer: connect a receipt printer and test printing.',
    'Yazıcı: fiş yazıcısını bağlama ve yazdırmayı deneme.',
  ],
  'ai_keys': [
    'Настройка ИИ: подключение ключей и язык ответов ИИ.',
    'ИИ жөндөөсү: ачкычтарды туташтыруу жана ИИ жооптору тили.',
    "AI sozlamalari: kalitlarni ulash va AI javoblari tili.",
    'AI settings: connecting keys and the language of AI answers.',
    'Yapay zeka ayarları: anahtar bağlama ve YZ yanıt dili.',
  ],
  'kassa_link': [
    'Связь с кассой: телефон как сканер штрихкодов для кассы на компьютере.',
    'Кассага туташуу: телефон компьютердеги кассага штрих-код сканери катары.',
    "Kassa bilan aloqa: telefon kompyuterdagi kassa uchun shtrix-kod skaneri sifatida.",
    'Till link: the phone as a barcode scanner for the computer till.',
    'Kasa bağlantısı: telefonu bilgisayardaki kasa için barkod tarayıcı yapma.',
  ],
  'gemini_keys': [
    'Ключи Gemini: несколько ключей ИИ и переключение при лимите (сами ключи не показываются).',
    'Gemini ачкычтары: бир нече ИИ ачкычы жана лимитте которуу (ачкычтар өзү көрсөтүлбөйт).',
    "Gemini kalitlari: bir nechta AI kaliti va limitda almashtirish (kalitlarning o'zi ko'rsatilmaydi).",
    'Gemini keys: several AI keys and switching on a limit (the keys themselves are never shown).',
    'Gemini anahtarları: birden çok YZ anahtarı ve limitte geçiş (anahtarların kendisi gösterilmez).',
  ],
  'ai_access': [
    'Доступ к ИИ: каким сотрудникам разрешён ИИ (не больше двух).',
    'ИИге жеткиликтүүлүк: кайсы кызматкерлерге ИИ уруксат (экиден көп эмес).',
    "SI dan foydalanish: qaysi xodimlarga AI ruxsat etilgan (ikkitadan ko'p emas).",
    'AI access: which employees may use the AI (no more than two).',
    'YZ erişimi: hangi çalışanlar YZ kullanabilir (en çok iki).',
  ],
  'tester': [
    'Режим тестировщика: проверка новых функций по коду.',
    'Тестирлөөчү режими: жаңы функцияларды код боюнча текшерүү.',
    "Sinovchi rejimi: yangi funksiyalarni kod bo'yicha tekshirish.",
    'Tester mode: checking new features by a code.',
    'Test modu: yeni özellikleri kodla deneme.',
  ],
};

/// Раздел, которого В ТЕЛЕФОНЕ НЕТ: он есть в программе владельца (Windows) или на сайте. Советник не открывает его, а честно говорит,
/// где он находится. [titles] и [words] — по языкам (ru, ky, uz, en, tr); [where] — owner (программа владельца) / site (сайт NurCRM).
class AbsentSection {
  final String key;
  final List<String> titles;
  final List<String> words;
  final String where;
  const AbsentSection(this.key, this.titles, this.words, this.where);
}

const kAbsentSections = <AbsentSection>[
  AbsentSection('promotions', ['Акции', 'Акциялар', 'Aksiyalar', 'Promotions', 'Kampanyalar'], [
    'акции|скидки по акции|акция|промо',
    'акциялар|акция',
    'aksiyalar|aksiya',
    'promotions|promotion|promo|sales offers',
    'kampanyalar|kampanya',
  ], 'owner'),
  AbsentSection('bot_dialogs', ['Диалоги бота', 'Бот диалогдору', 'Bot dialoglari', 'Bot dialogs', 'Bot diyalogları'], [
    'диалоги бота|диалоги с ботом|переписка бота|бот продавец диалоги',
    'бот диалогдору|бот жазышуулары',
    'bot dialoglari|bot yozishmalari',
    'bot dialogs|bot conversations|bot chats',
    'bot diyaloglari|bot konusmalari',
  ], 'owner'),
  AbsentSection('repair_orders', ['Ремонт', 'Оңдоо', "Ta'mirlash", 'Repair', 'Tamir'], [
    'ремонт|заказы на ремонт|мастерская|приемка в ремонт',
    'оңдоо|ремонт',
    'tamirlash|remont',
    'repair|repairs|repair orders|workshop',
    'tamir|onarim|servis',
  ], 'owner'),
  AbsentSection('mailings', ['Рассылки', 'Жөнөтмөлөр', 'Xabarlar yuborish', 'Mailings', 'Toplu mesajlar'], [
    'рассылки|рассылка клиентам|массовые сообщения',
    'жөнөтмөлөр|рассылка',
    'xabarlar yuborish|rassilka',
    'mailings|mailing|bulk messages|newsletter',
    'toplu mesaj|bulten',
  ], 'owner'),
  AbsentSection('onec', ['Выгрузка в 1С', '1С га чыгаруу', "1C ga yuklash", '1C export', '1C dışa aktarım'], [
    'выгрузка в 1с|экспорт в 1с|1с',
    '1с га чыгаруу|1с',
    '1c ga yuklash|1c',
    '1c export|export to 1c|1c',
    '1c aktarim|1c',
  ], 'owner'),
  AbsentSection('site_editor', ['Сайт и витрина', 'Сайт жана витрина', 'Sayt va vitrina', 'Website and storefront', 'Web sitesi ve vitrin'], [
    'настройка сайта|витрина|интернет магазин настройка|сайт магазина',
    'сайт жөндөө|витрина',
    'sayt sozlash|vitrina',
    'website settings|storefront|online shop setup',
    'web sitesi ayari|vitrin',
  ], 'site'),
];

/// Что нового: пункты привязаны к экрану реестра ([screen]) или к Маркетплейсу; тексты по языкам (ru, ky, uz, en, tr).
class NewsItem {
  final String screen;
  final List<String> text;
  const NewsItem(this.screen, this.text);
}

const kWhatsNew = <NewsItem>[
  NewsItem('marketplace', [
    'Появился «Маркетплейс»: функции и тарифы компании, описание и демо каждой функции, цены с сервера (владелец и администратор).',
    '«Маркетплейс» пайда болду: компаниянын функциялары жана тарифтери, ар бир функциянын сүрөттөмөсү жана демосу, сервердин баалары (ээси жана администратор).',
    "«Marketplace» paydo bo'ldi: kompaniya funksiyalari va tariflari, har bir funksiyaning tavsifi va demosi, serverdan narxlar (egasi va administrator).",
    'The "Marketplace" appeared: company features and plans, a description and demo of each feature, server prices (owner and administrator).',
    '«Marketplace» eklendi: şirket özellikleri ve tarifeleri, her özelliğin açıklaması ve demosu, sunucu fiyatları (sahip ve yönetici).',
  ]),
  NewsItem('scan', [
    'Повторный скан того же кода останавливает камеру и спрашивает количество.',
    'Бир эле кодду кайра сканерлегенде камера токтоп, санын сурайт.',
    "Bir xil kodni qayta skanerlash kamerani to'xtatib, miqdorni so'raydi.",
    'Scanning the same code again stops the camera and asks for the quantity.',
    'Aynı kodu tekrar okutmak kamerayı durdurur ve miktarı sorar.',
  ]),
  NewsItem('receive', [
    'Приёмка: литры и килограммы остаются литрами и килограммами, дробное количество из накладной не округляется.',
    'Кабыл алуу: литр жана килограмм литр жана килограмм бойдон калат, накладнойдогу бөлчөк сан тегеректелбейт.',
    "Qabul: litr va kilogramm litr va kilogramm bo'lib qoladi, nakladnoydagi kasr miqdor yaxlitlanmaydi.",
    'Receiving: liters and kilograms stay liters and kilograms, a fractional quantity from the invoice is not rounded.',
    'Mal kabul: litre ve kilogram aynen kalır, faturadaki kesirli miktar yuvarlanmaz.',
  ]),
  NewsItem('debts', [
    'В «Долгах» у каждого должника кнопка WA с готовым напоминанием на языке приложения.',
    '«Карыздар» бөлүмүндө ар бир карызкорго даяр эскертмеси бар WA баскычы бар.',
    "«Qarzlar»da har bir qarzdorda tayyor eslatmali WA tugmasi bor.",
    'In "Debts" each debtor has a WA button with a ready reminder in the app language.',
    '«Borçlar»da her borçluda hazır hatırlatmalı WA düğmesi var.',
  ]),
  NewsItem('stock', [
    'Склад: поиск по каталожному номеру и OEM для «Автозапчастей», сферы «Базар» и «Автозапчасти» определяются по ответу сервера.',
    'Кампа: «Автозапчасттар» үчүн каталог номери жана OEM боюнча издөө, «Базар» жана «Автозапчасттар» сфералары сервердин жообу боюнча аныкталат.',
    "Ombor: «Avtoehtiyot qismlar» uchun katalog raqami va OEM bo'yicha qidiruv, «Bozor» va «Avtoehtiyot qismlar» sohalari server javobiga ko'ra aniqlanadi.",
    'Stock: search by catalog number and OEM for "Auto parts"; the "Bazaar" and "Auto parts" spheres come from the server answer.',
    'Depo: «Oto yedek parça» için katalog numarası ve OEM ile arama; «Pazar» ve «Oto yedek parça» alanları sunucu yanıtından belirlenir.',
  ]),
];

/// Название инструмента в строке «Использовано: …» (ru, ky, uz, en, tr). Тест требует название у каждого инструмента советника.
const kToolLabels = <String, List<String>>{
  'open_screen': ['открытие раздела', 'бөлүмдү ачуу', "bo'limni ochish", 'opening a section', 'bölüm açma'],
  'open_product': ['карточка товара', 'товар картасы', 'tovar kartasi', 'product card', 'ürün kartı'],
  'open_check': ['открытие чека', 'чекти ачуу', 'chekni ochish', 'opening a receipt', 'fiş açma'],
  'open_client': ['клиенты', 'кардарлар', 'mijozlar', 'clients', 'müşteriler'],
  'open_order': ['заказы', 'заказдар', 'buyurtmalar', 'orders', 'siparişler'],
  'go_back': ['назад', 'артка', 'orqaga', 'back', 'geri'],
  'go_home': ['главная', 'башкы бет', 'bosh sahifa', 'home', 'ana sayfa'],
  'list_screens': ['список разделов', 'бөлүмдөр тизмеси', "bo'limlar ro'yxati", 'list of sections', 'bölüm listesi'],
  'sales_report': ['продажи', 'сатуулар', 'sotuvlar', 'sales', 'satışlar'],
  'top_products': ['топ товаров', 'топ товарлар', 'top tovarlar', 'top products', 'en çok satanlar'],
  'abc_report': ['ABC-анализ', 'ABC-талдоо', 'ABC-tahlil', 'ABC analysis', 'ABC analizi'],
  'stock_list': ['склад', 'кампа', 'ombor', 'stock', 'depo'],
  'product_info': ['информация о товаре', 'товар маалыматы', "tovar ma'lumoti", 'product info', 'ürün bilgisi'],
  'checks_list': ['чеки', 'чектер', 'cheklar', 'receipts', 'fişler'],
  'check_details': ['чек', 'чек', 'chek', 'receipt', 'fiş'],
  'debtors': ['долги', 'карыздар', 'qarzlar', 'debts', 'borçlar'],
  'client_info': ['клиент', 'кардар', 'mijoz', 'client', 'müşteri'],
  'shift_now': ['текущая смена', 'учурдагы смена', 'joriy smena', 'current shift', 'mevcut vardiya'],
  'shifts_list': ['смены', 'смендер', 'smenalar', 'shifts', 'vardiyalar'],
  'cash_moves': ['движение денег', 'акча кыймылы', 'pul harakati', 'cash movements', 'para hareketi'],
  'orders_list': ['заказы', 'заказдар', 'buyurtmalar', 'orders', 'siparişler'],
  'cart_show': ['корзина', 'себет', 'savat', 'cart', 'sepet'],
  'deletions_log': ['удаления из чеков', 'чектен өчүрүүлөр', "chekdan o'chirishlar", 'deleted lines', 'silinenler'],
  'audit_log': ['журнал аудита', 'аудит журналы', 'audit jurnali', 'audit log', 'denetim günlüğü'],
  'recommend': ['рекомендации', 'сунуштар', 'tavsiyalar', 'recommendations', 'öneriler'],
  'explain_section': ['описание раздела', 'бөлүм сүрөттөмөсү', "bo'lim tavsifi", 'section guide', 'bölüm açıklaması'],
  'marketplace_info': ['Маркетплейс', 'Маркетплейс', 'Marketplace', 'Marketplace', 'Marketplace'],
  'tariff_info': ['тариф', 'тариф', 'tarif', 'plan', 'tarife'],
  'app_settings_info': ['настройки приложения', 'колдонмо жөндөөлөрү', 'ilova sozlamalari', 'app settings', 'uygulama ayarları'],
  'suppliers_info': ['поставщики и приходы', 'жеткирүүчүлөр жана кирим', 'yetkazib beruvchilar va kirim', 'suppliers and receipts', 'tedarikçiler ve girişler'],
  'whats_new': ['что нового', 'эмне жаңы', 'nima yangi', "what's new", 'yenilikler'],
  'app_status': ['состояние приложения', 'колдонмо абалы', 'ilova holati', 'app status', 'uygulama durumu'],
};

/// Название инструмента для строки «Использовано»; неизвестный — его имя как есть (тест не даст забыть описание).
String toolLabel(String tool, String lang) {
  final l = kToolLabels[tool];
  if (l == null) return tool;
  final i = kNavLangs.indexOf(lang);
  return l[i < 0 ? 0 : i];
}

/// Подпись «Использовано: …» (5 языков) из имён инструментов, без повторов, не больше 5 названий.
String usedLine(Iterable<String> tools, String lang) {
  final seen = <String>[];
  for (final t in tools) {
    final s = toolLabel(t, lang);
    if (!seen.contains(s)) seen.add(s);
  }
  if (seen.isEmpty) return '';
  final head = switch (lang) {
    'ky' => 'Колдонулду',
    'uz' => 'Ishlatildi',
    'en' => 'Used',
    'tr' => 'Kullanılan',
    _ => 'Использовано',
  };
  final shown = seen.length > 5 ? [...seen.take(5), '…'] : seen;
  return '$head: ${shown.join(', ')}';
}
