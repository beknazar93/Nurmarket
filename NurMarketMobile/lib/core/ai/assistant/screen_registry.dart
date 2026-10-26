// 2026-10-26 (голос-управление, В1): реестр экранов мобилки для голосовой навигации — ключ, маршрут go_router, право,
// заголовки и синонимы на 5 языках (ru/ky/uz/en/tr), параметры (filter / query / period / tab) и разбор слов пользователя
// (запасной путь «открой …» без инструментов, перенос идеи NavKeywords/NavigateByWords кассы ПК: основа слова — 5 букв,
// вес названия 2, синонима 1). Файл без Flutter и без сети. Права берутся у самой сессии (Session.canRoute) — второй
// копии таблицы прав здесь нет; сверку «реестр ↔ маршруты app.dart» делает test/nav_tools_test.dart.
import '../../api/session_store.dart';

const kNavLangs = ['ru', 'ky', 'uz', 'en', 'tr'];

// ---------------------------------------------------------------------------------------------------------------------
// Нормализация речи для сравнения слов
// ---------------------------------------------------------------------------------------------------------------------

const _fold = {
  'ё': 'е', 'ң': 'н', 'ө': 'о', 'ү': 'у', 'ӊ': 'н', // русская и кыргызская кириллица (расшифровка путает ң/н, ө/о, ү/у)
  'ı': 'i', 'ş': 's', 'ğ': 'g', 'ç': 'c', 'ö': 'o', 'ü': 'u', 'ä': 'a', 'â': 'a', 'î': 'i', 'û': 'u', // турецкий, узбекский
};

/// Для сравнения слов: нижний регистр, диакритика убрана (ң→н, ө→о, ү→у, ş→s, ğ→g, ı→i …), апострофы удалены
/// («o'ch» = «och», «ko'rsat» = «korsat»).
String foldText(String s) {
  final b = StringBuffer();
  for (final r in s.toLowerCase().runes) {
    final ch = String.fromCharCode(r);
    if (r == 0x307 || r == 0x2bb || r == 0x2bc || r == 0x2019 || r == 0x60 || r == 0x27) continue; // точка İ и апострофы
    b.write(_fold[ch] ?? ch);
  }
  return b.toString();
}

final _splitRe = RegExp(r'[^\p{L}\p{N}]+', unicode: true);

List<String> foldTokens(String s) => foldText(s).split(_splitRe).where((w) => w.isNotEmpty).toList();

/// Слово речи [t] подходит слову-образцу [p]: короткие образцы (≤ 3 букв) — только целиком, длинные — по основе в 5 букв.
bool tokenMatches(String t, String p) {
  if (p.length <= 3) return t == p;
  final stem = p.length > 5 ? p.substring(0, 5) : p;
  return t.startsWith(stem);
}

// ---------------------------------------------------------------------------------------------------------------------
// Описание экрана
// ---------------------------------------------------------------------------------------------------------------------

/// Параметр экрана. [arg] — имя в инструменте (filter / query / period / tab), [url] — имя в адресе маршрута.
/// [live] = true, если сам экран уже читает этот параметр из адреса (app.dart). Пока false — адрес с параметром открывается,
/// но экран его игнорирует, и программа честно говорит об этом (правки экранов — В4/В3б, флаг переключается здесь).
class ParamDef {
  final String arg;
  final String url;
  final Set<String>? values;
  final bool live;
  const ParamDef(this.arg, this.url, {this.values, this.live = false});
}

class _Phrase {
  final List<String> tokens;
  final int weight;
  const _Phrase(this.tokens, this.weight);
}

class ScreenDef {
  /// Значение open_screen.screen.
  final String key;

  /// Маршрут go_router; null — экран вне маршрутов (открывается через [aux]).
  final String? route;
  final String? aux;

  /// Маршрут уже есть в app.dart (false — запланирован пакетами В5/В7: в реестре, но не открывается).
  final bool exists;

  /// Голос может открыть: false — экран требует данных (чек, заказ), оплачено/вход и т.п.
  final bool voiceOpen;

  /// Заголовки: ru, ky, uz, en, tr.
  final List<String> titles;
  final List<ParamDef> params;
  final List<_Phrase> _phrases;

  ScreenDef._(this.key, this.route, this.aux, this.exists, this.voiceOpen, this.titles, this.params, this._phrases);

  String title(String lang) {
    final i = kNavLangs.indexOf(lang);
    return titles[i < 0 ? 0 : i];
  }

  /// Право как у самого маршрута (Session.canRoute: redirect → /denied). Экраны вне маршрутов открыты всем.
  bool allowedFor(Session s) => route == null ? true : s.canRoute(route!);

  /// Путь для сравнения с адресом (без параметров).
  String get path => route ?? 'aux:$aux';
}

ScreenDef _def(
  String key,
  String? route,
  List<String> titles,
  List<String> syn, {
  String? aux,
  bool exists = true,
  bool voiceOpen = true,
  List<ParamDef> params = const [],
}) {
  assert(titles.length == 5 && syn.length == 5);
  final phrases = <_Phrase>[];
  for (final t in titles) {
    final tokens = foldTokens(t.replaceAll(RegExp(r'\(.*?\)'), ''));
    if (tokens.isNotEmpty) phrases.add(_Phrase(tokens, 2));
  }
  for (final line in syn) {
    for (final p in line.split('|')) {
      final tokens = foldTokens(p);
      if (tokens.isNotEmpty) phrases.add(_Phrase(tokens, 1));
    }
  }
  return ScreenDef._(key, route, aux, exists, voiceOpen, titles, params, phrases);
}

const _stockFilters = {'low', 'out', 'in', 'all'};
const _periods = {'today', 'week', 'month'};
const _q = ParamDef('query', 'q');

/// Реестр экранов (раздел 7 ТЗ). Порядок — как в таблице ТЗ.
final List<ScreenDef> _screens = [
  _def('home', '/home', ['Главная', 'Башкы бет', 'Bosh sahifa', 'Home', 'Ana sayfa'], [
    'главная|начало|старт',
    'башкы|үй',
    'bosh|uy',
    'home|main screen',
    'ana sayfa|ana ekran|anasayfa',
  ]),
  _def('sale', '/sale', ['Продажа', 'Сатуу', 'Sotuv', 'Sale', 'Satış'], [
    'продажа|касса|корзина|пробить',
    'сатуу|касса|себет',
    'sotuv|kassa|savat',
    'sale|cashier|register|cart',
    'satis|kasa|sepet',
  ]),
  _def('scan', '/scan', ['Сканер', 'Сканер', 'Skaner', 'Scanner', 'Tarayıcı'], [
    'сканер|скан|штрихкод|штрих код',
    'сканер|штрих',
    'skaner|shtrix|shtrixkod',
    'scanner|scan|barcode',
    'tarayici|tara|barkod',
  ], params: [
    ParamDef('tab', 'mode', values: {'sale', 'price'}, live: true),
  ]),
  _def('checks', '/checks', ['Чеки', 'Чектер', 'Cheklar', 'Receipts', 'Fişler'], [
    'чеки|история чеков|история продаж',
    'чектер|чек тарыхы',
    'cheklar|chek',
    'checks|receipts|sales history',
    'fisler|fis|makbuz',
  ], params: [
    _q,
  ]),
  _def('check', '/checks/detail', ['Чек', 'Чек', 'Chek', 'Receipt', 'Fiş'], ['', '', '', '', ''], voiceOpen: false),
  _def('stock', '/stock', ['Склад', 'Кампа', 'Ombor', 'Warehouse', 'Depo'], [
    'склад|остатки|товары|наличие|ассортимент',
    'кампа|склад|калдык|товарлар',
    'ombor|qoldiq|tovarlar',
    'stock|warehouse|inventory|products',
    'depo|stok|urunler',
  ], params: [
    ParamDef('filter', 'filter', values: _stockFilters, live: true),
    _q,
  ]),
  _def('stock_receive', '/stock/receive', ['Приход (простой)', 'Кириш (жөнөкөй)', 'Kirim (oddiy)', 'Simple receipt', 'Basit giriş'], [
    'приход|оприходовать|оприходование|простой приход',
    'кириш|кирим|кабыл алуу',
    'kirim|qabul',
    'receive|goods in|simple receive',
    'mal girisi|giris',
  ]),
  _def('stock_audit', '/stock/audit', ['Ревизия', 'Ревизия', 'Reviziya', 'Stock count', 'Sayım'], [
    'ревизия|инвентаризация|пересчет|пересчитать',
    'ревизия|санап чыгуу|саноо',
    'reviziya|inventarizatsiya|sanash',
    'stocktake|stock count|inventory count|recount',
    'sayim|envanter',
  ]),
  _def('stock_import', '/stock/import', ['Массовая загрузка', 'Жалпы жүктөө', 'Ommaviy yuklash', 'Bulk import', 'Toplu yükleme'], [
    'массовая загрузка|импорт|загрузка товаров|загрузить из excel',
    'жалпы жуктоо|импорт',
    'ommaviy yuklash|import',
    'bulk import|import|upload products',
    'toplu yukleme|ice aktar',
  ]),
  // Карточка товара — вне маршрутов: открывается инструментом open_product (openProductEditor), не open_screen.
  _def('product', null, ['Карточка товара', 'Товар картасы', 'Tovar kartasi', 'Product card', 'Ürün kartı'], ['', '', '', '', ''], aux: 'product', voiceOpen: false),
  _def('pay', '/pay', ['Оплата', 'Төлөм', "To'lov", 'Payment', 'Ödeme'], [
    'оплата|оплатить',
    'толом|толоо',
    'tolov',
    'payment|pay screen',
    'odeme',
  ]),
  _def('paid', '/paid', ['Оплачено', 'Төлөндү', "To'landi", 'Paid', 'Ödendi'], ['', '', '', '', ''], voiceOpen: false),
  _def('close_shift', '/close-shift', ['Закрытие смены', 'Сменаны жабуу', 'Smenani yopish', 'Close shift', 'Vardiya kapatma'], [
    'закрытие смены|закрыть смену|закрой смену|z отчет',
    'сменаны жабуу|смена жабуу|сменаны жап',
    'smenani yopish|smena yopish|smenani yop',
    'close shift|end of day|end shift',
    'vardiya kapat|vardiyayi kapat|vardiya kapatma',
  ]),
  _def('profile', '/profile', ['Профиль', 'Профиль', 'Profil', 'Profile', 'Profil'], [
    'профиль|мой профиль|аккаунт|тема|язык',
    'профиль|жондоолор|тил',
    'profil|sozlamalar|til',
    'profile|settings|account|theme|language',
    'profil|ayarlar|hesap|dil',
  ]),
  _def('ai_log', '/ai/log', ['Действия ИИ', 'ИИ аракеттери', 'AI harakatlari', 'AI actions', 'Yapay zeka işlemleri'], [
    'действия ии|журнал ии',
    'ии аракеттери',
    'ai harakatlari',
    'ai actions|ai log',
    'yapay zeka islemleri',
  ]), // 2026-10-26 (В5): экран готов (/ai/log), exists: true
  _def('assistant_settings', '/ai/settings', ['Помощник', 'Жардамчы', 'Yordamchi', 'Assistant', 'Yardımcı'], [
    'помощник|режим помощника',
    'жардамчы',
    'yordamchi',
    'assistant|assistant mode',
    'yardimci',
  ]), // 2026-10-26 (В5): экран готов (/ai/settings), exists: true
  _def('sync', '/sync', ['Синхронизация', 'Шайкештирүү', 'Sinxronlash', 'Sync', 'Senkronizasyon'], [
    'синхронизация|синхронизировать|обновить данные|очередь',
    'шайкештируу|синхрондоо',
    'sinxronlash|sinxronizatsiya',
    'sync|synchronization',
    'senkronizasyon|senkron',
  ]),
  _def('cash', '/cash', ['Внесение и изъятие', 'Салуу жана алуу', 'Kirim va chiqim', 'Cash in/out', 'Kasa giriş çıkış'], [
    'внесение|изъятие|внести деньги|изъять деньги|движение денег',
    'акча салуу|акча алуу',
    'kassaga kirim|kassadan chiqim|pul kiritish',
    'cash in|cash out|cash operations|cash drawer',
    'kasaya para|kasadan para|para girisi',
  ]),
  _def('debts', '/debts', ['Долги клиентов', 'Кардарлардын карыздары', 'Mijozlar qarzlari', 'Client debts', 'Müşteri borçları'], [
    'долги|должники|долг',
    'карыздар|карыз',
    'qarzlar|qarz',
    'debts|debtors|debt',
    'borclar|borc',
  ]),
  _def('held', '/held', ['Отложенные', 'Кечиктирилген', 'Kechiktirilgan', 'Held checks', 'Bekleyen'], [
    'отложенные|отложенные чеки',
    'кечиктирилген|токтотулган чектер',
    'kechiktirilgan|saqlangan cheklar',
    'held|held checks|parked|on hold',
    'bekleyen|askiya alinan',
  ]),
  _def('search', '/search', ['Поиск', 'Издөө', 'Qidiruv', 'Search', 'Arama'], [
    'поиск|искать',
    'издоо|табуу',
    'qidiruv|qidirish',
    'search|find',
    'arama|ara',
  ], params: [
    _q,
  ]),
  _def('rentals', '/rentals', ['Прокат', 'Ижара', 'Ijara', 'Rentals', 'Kiralama'], [
    'прокат|аренда',
    'ижара',
    'ijara|prokat',
    'rental|rentals|hire',
    'kiralama|kiralik',
  ]),
  _def('rental_new', '/rentals/new', ['Новый прокат', 'Жаңы ижара', 'Yangi ijara', 'New rental', 'Yeni kiralama'], [
    'новый прокат|новая аренда|выдать в прокат',
    'жаны ижара',
    'yangi ijara',
    'new rental',
    'yeni kiralama',
  ]),
  _def('admin', '/admin', ['Управление', 'Башкаруу', 'Boshqaruv', 'Management', 'Yönetim'], [
    'управление|админка|администрирование',
    'башкаруу|башкарма',
    'boshqaruv|admin',
    'admin|management|manage',
    'yonetim',
  ]),
  _def('prices', '/admin/product', ['Цены товара', 'Товар баалары', 'Tovar narxlari', 'Product prices', 'Ürün fiyatları'], [
    'цены|цена|ценники|переоценка|изменение цен',
    'баалар|баа',
    'narxlar|narx',
    'prices|price',
    'fiyatlar|fiyat',
  ]),
  _def('shifts', '/admin/shifts', ['Смены', 'Смендер', 'Smenalar', 'Shifts', 'Vardiyalar'], [
    'смены|история смен|табель',
    'смендер',
    'smenalar',
    'shifts',
    'vardiyalar',
  ]),
  _def('orders', '/admin/orders', ['Заказы', 'Буйрутмалар', 'Buyurtmalar', 'Orders', 'Siparişler'], [
    'заказы|заказ|онлайн заказы',
    'буйрутмалар|заказдар',
    'buyurtmalar|zakazlar',
    'orders',
    'siparisler|siparis',
  ], params: [
    _q,
  ]),
  _def('pick', '/admin/pick', ['Сборка заказа', 'Заказды жыйноо', 'Buyurtmani yig\'ish', 'Order picking', 'Sipariş toplama'], ['', '', '', '', ''], voiceOpen: false),
  _def('clients', '/admin/clients', ['Клиенты', 'Кардарлар', 'Mijozlar', 'Clients', 'Müşteriler'], [
    'клиенты|клиент|покупатели|бонусы',
    'кардарлар|кардар|бонус',
    'mijozlar|mijoz|xaridorlar',
    'clients|customers',
    'musteriler|musteri',
  ], params: [
    _q,
  ]),
  _def('purchase', '/admin/purchase', ['Заказ поставщику', 'Жеткирүүчүгө заказ', 'Yetkazib beruvchiga buyurtma', 'Supplier order', 'Tedarikçi siparişi'], [
    'заказ поставщику|закупка|закупки|поставщики',
    'жеткируучуго заказ|закупка',
    'yetkazib beruvchiga buyurtma|xarid',
    'supplier order|purchase|purchasing|suppliers',
    'tedarikci siparisi|satin alma',
  ]),
  _def('deletions', '/admin/deletions', ['Удалено из чека', 'Чектен өчүрүлгөн', 'Chekdan o\'chirilgan', 'Deleted items', 'Silinenler'], [
    'удалено из чека|удаленные|удаления',
    'чектен очурулгон',
    'chekdan ochirilgan|ochirilgan',
    'deletions|deleted items|voided',
    'silinenler',
  ]),
  _def('receive', '/admin/receive', ['Приёмка с поставщиком', 'Жеткирүүчүдөн кабыл алуу', 'Yetkazib beruvchidan qabul', 'Receive from supplier', 'Tedarikçiden mal kabul'], [
    'приемка|приемка товара|накладная прихода',
    'кабыл алуу|жеткируучудон кабыл алуу',
    'qabul|yetkazib beruvchidan qabul',
    'goods receipt|receive from supplier|receiving',
    'tedarikciden mal kabul|mal kabul',
  ]),
  _def('invoice', '/admin/invoice', ['Накладная', 'Накладная', 'Hisob-faktura', 'Invoice', 'İrsaliye'], [
    'накладная',
    'накладная',
    'nakladnoy|faktura',
    'invoice|waybill',
    'irsaliye',
  ], exists: false),
  _def('smartcam', '/admin/smartcam', ['Умная камера', 'Акылдуу камера', 'Aqlli kamera', 'Smart camera', 'Akıllı kamera'], [
    'умная камера|камера|фото полки',
    'акылдуу камера',
    'aqlli kamera',
    'smart camera|camera',
    'akilli kamera|kamera',
  ]),
  _def('staff', '/admin/staff', ['Сотрудники и права', 'Кызматкерлер жана укуктар', 'Xodimlar va huquqlar', 'Staff and permissions', 'Personel ve yetkiler'], [
    'сотрудники|права|доступы|персонал|работники',
    'кызматкерлер|укуктар',
    'xodimlar|huquqlar',
    'staff|employees|permissions',
    'personel|calisanlar|yetkiler',
  ]),
  _def('payqr', '/admin/payqr', ['QR-оплата', 'QR төлөм', "QR to'lov", 'QR payment', 'QR ödeme'], [
    'qr|кьюар|оплата по qr',
    'qr толом',
    'qr tolov',
    'qr payment|qr pay',
    'qr odeme',
  ]),
  _def('debt_access', '/admin/debt-access', ['Доступ к долгам', 'Карыздарга жеткиликтүүлүк', 'Qarzlarga ruxsat', 'Debt access', 'Borç erişimi'], [
    'доступ к долгам',
    'карыздарга жеткиликтуулук',
    'qarzlarga ruxsat',
    'debt access',
    'borc erisimi',
  ]),
  _def('audit', '/admin/audit', ['Журнал аудита', 'Аудит журналы', 'Audit jurnali', 'Audit log', 'Denetim günlüğü'], [
    'журнал|аудит|журнал аудита|история действий',
    'аудит журналы|журнал',
    'audit jurnali|jurnal',
    'audit log|audit|journal|log',
    'denetim gunlugu|gunluk|denetim',
  ]),
  _def('notifications', '/admin/notifications', ['Уведомления', 'Билдирмелер', 'Bildirishnomalar', 'Notifications', 'Bildirimler'], [
    'уведомления|оповещения',
    'билдирмелер',
    'bildirishnomalar',
    'notifications|alerts',
    'bildirimler',
  ]),
  _def('quotes', '/admin/quotes', ['Коммерческое предложение', 'Коммерциялык сунуш', 'Tijorat taklifi', 'Quotes', 'Teklifler'], [
    'кп|коммерческое предложение|предложения',
    'коммерциялык сунуш|кп',
    'tijorat taklifi|kp',
    'quote|quotes|proposal|quotation',
    'teklif|fiyat teklifi',
  ]),
  _def('advisor', '/admin/advisor', ['Нур Советник', 'Нур Кеңешчи', 'Nur Maslahatchi', 'Nur Advisor', 'Nur Danışman'], [
    'советник|нур советник|ассистент|чат',
    'кенешчи|нур кенешчи',
    'maslahatchi|nur maslahatchi',
    'advisor|nur advisor|assistant|chat',
    'danisman|nur danisman',
  ]),
  _def('summary', '/admin/summary', ['Сводка', 'Жыйынтык', 'Xulosa', 'Summary', 'Özet'], [
    'сводка|итоги|выручка|финансы|статистика|отчет|дашборд',
    'жыйынтык|киреше|отчет|финансы',
    'xulosa|hisobot|tushum|moliya',
    'summary|dashboard|revenue|report|finances|statistics',
    'ozet|rapor|gelir|finans|istatistik',
  ], params: [
    ParamDef('period', 'period', values: _periods),
  ]),
  _def('abc', '/admin/abc', ['ABC-анализ', 'ABC-талдоо', 'ABC-tahlil', 'ABC analysis', 'ABC analizi'], [
    'abc|абц|абс анализ|анализ товаров|парето',
    'abc талдоо|парето',
    'abc tahlil|pareto',
    'abc analysis|abc|pareto',
    'abc analizi|pareto',
  ]),
  _def('esf', '/admin/esf', ['ЭСФ', 'ЭСФ', 'ESF', 'ESF', 'ESF'], [
    'эсф|электронный счет фактура|счет фактура',
    'эсф',
    'esf',
    'esf|e invoice|electronic invoice',
    'esf|e fatura',
  ]),
  _def('new_product', '/admin/new-product', ['Новый товар', 'Жаңы товар', 'Yangi tovar', 'New product', 'Yeni ürün'], [
    'новый товар|добавить товар|создать товар|новая позиция',
    'жаны товар|товар кошуу',
    'yangi tovar|tovar qoshish',
    'new product|add product|create product',
    'yeni urun|urun ekle',
  ], params: [
    ParamDef('tab', 'kind', values: {'product', 'clothes', 'service'}, live: true),
  ]),
  _def('tasks', '/admin/tasks', ['Задачи', 'Тапшырмалар', 'Vazifalar', 'Tasks', 'Görevler'], [
    'задачи|задача',
    'тапшырмалар|тапшырма',
    'vazifalar|vazifa',
    'tasks|task',
    'gorevler|gorev',
  ]),
  _def('my_tasks', '/tasks/my', ['Мои задачи', 'Менин тапшырмаларым', 'Mening vazifalarim', 'My tasks', 'Görevlerim'], [
    'мои задачи',
    'менин тапшырмаларым',
    'mening vazifalarim',
    'my tasks',
    'gorevlerim',
  ]),
  _def('salary', '/admin/salary', ['Зарплата', 'Айлык', 'Maosh', 'Salary', 'Maaş'], [
    'зарплата|зарплаты|оклад',
    'айлык|айлыктар',
    'maosh|oylik',
    'salary|payroll',
    'maas|maaslar',
  ], params: [
    ParamDef('period', 'period', values: {'today', 'week', 'month'}, live: true),
  ]),
  // Экраны вне маршрутов (Navigator.push из профиля): открываются через NavHost.openAux.
  _def('printer', null, ['Принтер', 'Принтер', 'Printer', 'Printer', 'Yazıcı'], [
    'принтер|печать|чековый принтер',
    'принтер',
    'printer',
    'printer|printing',
    'yazici',
  ], aux: 'printer'),
  _def('ai_keys', null, ['Настройка ИИ', 'ИИ жөндөөсү', 'AI sozlamalari', 'AI settings', 'Yapay zeka ayarları'], [
    'настройка ии|настройки ии|ключ ии|нейросеть',
    'ии жондоосу',
    'ai sozlamalari',
    'ai settings|ai keys',
    'yapay zeka ayarlari',
  ], aux: 'ai_keys'),
];

// ---------------------------------------------------------------------------------------------------------------------
// Реестр: поиск, права, разбор параметров
// ---------------------------------------------------------------------------------------------------------------------

/// Совпадение экрана со словами пользователя.
class ScreenMatch {
  final ScreenDef def;
  final int score;
  const ScreenMatch(this.def, this.score);
}

/// Результат запасного пути: экран и параметры, найденные в той же фразе.
class NavWords {
  final ScreenDef def;
  final Map<String, String> args;
  const NavWords(this.def, this.args);
}

class ScreenRegistry {
  const ScreenRegistry();

  /// Маршруты app.dart, которых в реестре нарочно нет: вход, выбор кассы, отказ в доступе (ИИ их не открывает).
  static const excludedRoutes = {'/login', '/cashbox', '/denied'};

  List<ScreenDef> get all => _screens;

  ScreenDef? byKey(String key) {
    final k = key.trim().toLowerCase().replaceAll(RegExp(r'[\s\-]+'), '_');
    for (final d in _screens) {
      if (d.key == k) return d;
    }
    return null;
  }

  /// Маршруты всех существующих экранов (для сверки с app.dart).
  Set<String> get routes => {
    for (final d in _screens)
      if (d.exists && d.route != null) d.route!,
  };

  /// Экраны, которые эта сессия может открыть голосом (право роли + экран открывается голосом + маршрут существует).
  List<ScreenDef> openable(Session s) => [
    for (final d in _screens)
      if (d.exists && d.voiceOpen && d.allowedFor(s)) d,
  ];

  /// Экраны по словам, лучшие первыми. Балл: слов фразы × вес (название 2, синоним 1); фраза засчитывается, только если
  /// каждое её слово есть в речи (по основе в 5 букв, см. [tokenMatches]). Порядок слов не важен.
  List<ScreenMatch> find(String text, {bool onlyOpenable = true}) {
    final words = foldTokens(text);
    if (words.isEmpty) return const [];
    final out = <ScreenMatch>[];
    for (final d in _screens) {
      if (!d.exists || (onlyOpenable && !d.voiceOpen)) continue;
      var best = 0;
      for (final ph in d._phrases) {
        if (ph.tokens.every((p) => words.any((w) => tokenMatches(w, p)))) {
          final sc = ph.tokens.length * ph.weight;
          if (sc > best) best = sc;
        }
      }
      if (best > 0) out.add(ScreenMatch(d, best));
    }
    out.sort((a, b) => b.score.compareTo(a.score));
    return out;
  }

  /// Название экрана, как его сказала модель (`screen` инструмента): ключ реестра, иначе однозначное совпадение по словам.
  ScreenDef? resolve(String raw) {
    final byk = byKey(raw);
    if (byk != null) return byk;
    // Сначала среди экранов, которые голос открывает (иначе «чеки» путались бы с экраном «Чек»), потом среди всех.
    for (final only in const [true, false]) {
      final m = find(raw, onlyOpenable: only);
      if (m.isEmpty) continue;
      if (m.length > 1 && m[0].score == m[1].score) return null; // неоднозначно — пусть переспросит
      return m.first.def;
    }
    return null;
  }

  /// Запасной путь (модель без инструментов, раздел 3.7 ТЗ): «склад ач», «open warehouse». Открывает ТОЛЬКО при явном
  /// «открой / ач / och / open / aç» (правило владельца ПК, 07.10). Нет слова-просьбы или два экрана с равным баллом — null.
  NavWords? navigateByWords(String text) {
    if (!hasOpenVerb(text)) return null;
    final m = find(text);
    if (m.isEmpty) return null;
    if (m.length > 1 && m[0].score == m[1].score) return null;
    final d = m.first.def;
    final args = <String, String>{};
    final words = foldTokens(text);
    for (final p in d.params) {
      String? v;
      if (p.arg == 'filter') v = stockFilterFromWords(words);
      if (p.arg == 'period') v = periodFromWords(words);
      if (v != null) args[p.arg] = v;
    }
    return NavWords(d, args);
  }
}

/// Слово-просьба открыть экран (5 языков). Без неё запасной путь ничего не открывает.
bool hasOpenVerb(String text) {
  final words = foldTokens(text);
  const exact = {
    // ru
    'открой', 'открыть', 'открывай', 'покажи', 'показать', 'перейди', 'зайди',
    // ky
    'ач', 'ачып', 'ачкыла', 'ачыныз', 'ачсан', 'ачсанбы', 'аччы', 'корсот', 'корсотчу',
    // uz
    'och', 'ochib', 'ochgin', 'oching', 'ochsang', 'korsat', 'korsating',
    // en
    'open', 'show',
    // tr
    'ac', 'acar', 'acin', 'acarmisin', 'goster', 'git',
  };
  for (final w in words) {
    if (exact.contains(w)) return true;
  }
  // «go to», «take me to»
  for (var i = 0; i + 1 < words.length; i++) {
    if (words[i] == 'go' && words[i + 1] == 'to') return true;
  }
  return false;
}

// ---------------------------------------------------------------------------------------------------------------------
// Разбор параметров из слов
// ---------------------------------------------------------------------------------------------------------------------

String? _firstStem(List<String> words, List<String> stems, {Set<String> exactOnly = const {}}) {
  for (final w in words) {
    if (exactOnly.contains(w)) return w;
    for (final s in stems) {
      if (w.startsWith(s)) return s;
    }
  }
  return null;
}

/// Фильтр склада из свободной речи («что заканчивается», «эмне тугонуп жатат»): low / out / in.
String? stockFilterFromWords(List<String> words) {
  if (_firstStem(words, const ['закончил', 'законч', 'нулев', 'tugadi', 'bitti', 'калбаган'], exactOnly: const {'out', 'нет'}) != null) {
    return 'out';
  }
  if (_firstStem(words, const [
        'мало', 'заканчив', 'кончает', 'тугон', 'tugab', 'tugay', 'azal', 'bitiy', 'running', 'low', 'kam', 'дефицит',
      ]) !=
      null) {
    return 'low';
  }
  return null;
}

/// Период из свободной речи: today / week / month. null — не сказан.
String? periodFromWords(List<String> words) {
  if (_firstStem(words, const ['сегодн', 'бугун', 'bugun', 'today'], exactOnly: const {}) != null) return 'today';
  if (_firstStem(words, const ['недел', 'жума', 'hafta', 'week']) != null) return 'week';
  if (_firstStem(words, const ['месяц', 'month'], exactOnly: const {'ай', 'oy', 'ay'}) != null) return 'month';
  return null;
}

/// Нормализация параметра инструмента: допустимое значение или null (неизвестное значение отбрасывается, а не ломает вызов).
String? normalizeNavArg(ParamDef p, Object? raw) {
  if (raw == null) return null;
  final v = '$raw'.trim();
  if (v.isEmpty) return null;
  if (p.arg == 'query') return v.length > 80 ? v.substring(0, 80) : v;
  final words = foldTokens(v);
  final low = foldText(v).trim();
  switch (p.arg) {
    case 'filter':
      if (_stockFilters.contains(low)) return low;
      return stockFilterFromWords(words) ?? (_inStock(words) ? 'in' : null);
    case 'period':
      if (_periods.contains(low)) return low;
      return periodFromWords(words);
    default:
      final vals = p.values;
      if (vals == null) return v;
      if (vals.contains(low)) return low;
      return null;
  }
}

bool _inStock(List<String> words) => _firstStem(words, const ['наличи', 'есть', 'bor', 'in stock', 'stokta', 'бар']) != null;

/// Телефон/штрихкод, названный цифрами и пробелами («4 6 0 0 1 2 3»): только цифры, ≥ 6 — штрихкод; иначе null.
String? barcodeFromText(String raw) {
  final d = raw.replaceAll(RegExp(r'[\s\-]'), '');
  return RegExp(r'^\d{6,}$').hasMatch(d) ? d : null;
}

// ---------------------------------------------------------------------------------------------------------------------
// Товар: целые слова названия (не подстрока)
// ---------------------------------------------------------------------------------------------------------------------

/// Название товара [name] есть в речи [text] целыми словами: каждое слово названия — отдельное слово речи. «финансы» не
/// содержит товар «Нан» (правило владельца ПК: подстрокой не искать).
bool productNameInText(String name, String text) {
  final nt = foldTokens(name);
  if (nt.isEmpty) return false;
  final words = foldTokens(text).toSet();
  return nt.every(words.contains);
}

/// Название товара подходит запросу: каждое слово запроса — начало какого-то слова названия («кола» → «Кока-Кола 0,5»,
/// но «нан» ≠ «Финансы»).
bool productNameMatchesQuery(String name, String query) {
  final q = foldTokens(query);
  if (q.isEmpty) return false;
  final nt = foldTokens(name);
  return q.every((w) => nt.any((n) => n.startsWith(w)));
}
