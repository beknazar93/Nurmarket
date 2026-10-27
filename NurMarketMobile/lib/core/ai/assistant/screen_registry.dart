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

  /// 2026-10-27: раздел-родитель для пути в ответе («Склад → Приёмка»): программа называет то, что реально открыла.
  final String? parent;

  /// 2026-10-27: добавочное право экрана вне маршрутов (например «Доступ к ИИ» — владелец или право «Настройки»).
  final bool Function(Session s)? gate;

  ScreenDef._(this.key, this.route, this.aux, this.exists, this.voiceOpen, this.titles, this.params, this._phrases, this.parent, this.gate);

  /// 2026-10-27: фразы экрана (слова и вес: название 2, синоним 1) для локального маршрутизатора (nav_router.dart).
  Iterable<({List<String> tokens, int weight})> get phrases => _phrases.map((p) => (tokens: p.tokens, weight: p.weight));

  /// Название с разделом-родителем: «Склад → Приёмка с поставщиком».
  String fullTitle(String lang) {
    ScreenDef? up;
    if (parent != null) {
      for (final d in _screens) {
        if (d.key == parent) up = d;
      }
    }
    return up == null ? title(lang) : '${up.title(lang)} → ${title(lang)}';
  }

  String title(String lang) {
    final i = kNavLangs.indexOf(lang);
    return titles[i < 0 ? 0 : i];
  }

  /// Право как у самого маршрута (Session.canRoute: redirect → /denied). Экраны вне маршрутов открыты всем.
  bool allowedFor(Session s) => (route == null ? true : s.canRoute(route!)) && (gate?.call(s) ?? true);

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
  String? parent,
  bool Function(Session s)? gate,
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
  // 2026-10-27: разговорные слова и «глубокие цели» (приёмка, накладная, ключи Gemini …) — отдельной таблицей _extraSyn
  for (final p in (_extraSyn[key] ?? '').split('|')) {
    final tokens = foldTokens(p);
    if (tokens.isNotEmpty) phrases.add(_Phrase(tokens, 1));
  }
  return ScreenDef._(key, route, aux, exists, voiceOpen, titles, params, phrases, parent, gate);
}

const _stockFilters = {'low', 'out', 'in', 'all'};
const _periods = {'today', 'week', 'month'};
const _q = ParamDef('query', 'q');

/// 2026-10-27 (расследование «ИИ неправильно открывает разделы»): разговорные слова и «глубокие цели» — дополнение к синонимам
/// в [_screens]. Каждая запись: слова/фразы через «|», по языкам ru, ky, uz, en, tr; все — синонимы веса 1 (название экрана
/// весит 2). Фраза засчитывается, только если все её слова есть в речи. Правило: слово не должно стоять у двух экранов сразу,
/// кроме случаев, когда выбор нужно предложить кнопками (например «табель» — Смены или Зарплата).
const _extraSyn = <String, String>{
  'home': 'домой|на главную|главный экран|главная страница|рабочий стол|стартовая|'
      'башкы экран|баштапкы бет|башкы бетке|башкы бетти|'
      'bosh ekran|asosiy sahifa|bosh sahifaga|'
      'home screen|main page|start page|homepage|'
      'ana ekran|baslangic|ana sayfaya',
  'sale': 'продажи|новая продажа|экран продажи|пробить чек|начать продажу|продавать|кассовый режим|'
      'жаңы сатуу|сатуу экраны|сатуу бөлүмү|сатуу жасоо|'
      'sotish|yangi sotuv|sotuv ekrani|savdo|'
      'new sale|sales screen|checkout|point of sale|pos|selling|'
      'yeni satis|satis ekrani|satis yap|satislar',
  'scan': 'сканировать|сканер штрихкодов|считать штрихкод|проверка цены|сканер цен|проверить цену|'
      'сканерлоо|штрих код окуу|баа текшерүү|'
      'skanerlash|shtrix kod oqish|narx tekshirish|'
      'barcode scanner|price check|scan barcode|check price|'
      'barkod okuyucu|tarama|fiyat sorgula|fiyat kontrol',
  'checks': 'чек|список чеков|кассовые чеки|квитанции|продажи за день|журнал чеков|'
      'чектер тизмеси|сатуулар тарыхы|квитанциялар|'
      'cheklar royxati|sotuvlar tarixi|kvitansiyalar|cheklar tarixi|'
      'receipt|receipts list|transactions|sales log|check list|'
      'fis listesi|fis gecmisi|satis gecmisi|fisleri|fisi',
  'stock': 'кладовая|список товаров|каталог|номенклатура|запасы|складской учет|товарные остатки|остаток товара|'
      'товар тизмеси|каталог|запас|кампадагы товарлар|'
      'mahsulotlar|katalog|ombor hisobi|tovar royxati|'
      'catalog|stock list|items|goods|inventory list|stock levels|'
      'urun listesi|katalog|stok listesi|stoklar|depom',
  'stock_receive': 'простой приход|быстрый приход|приход товара|принять приход|'
      'жөнөкөй кирим|кирим жасоо|тез кирим|'
      'oddiy kirim|tovar kirimi|tez kirim|'
      'simple receipt|quick receive|receive stock|stock in|'
      'basit giris|hizli giris|mal girisi yap',
  'stock_audit': 'ревизию|сверка остатков|подсчет остатков|пересчет склада|провести ревизию|инвентарь|'
      'калдыктарды саноо|ревизия жүргүзүү|санап чыгуу|'
      'qayta sanash|qoldiqlarni sanash|reviziya otkazish|'
      'stock audit|inventory check|count stock|stock taking|stocktaking|'
      'stok sayimi|sayim yap|envanter sayimi|sayim',
  'stock_import': 'импорт товаров|загрузить товары|загрузить из файла|массовое добавление|добавить много товаров|загрузка из эксель|импорт из excel|'
      'товар жүктөө|топтом жүктөө|excel жүктөө|'
      'tovar yuklash|excel dan yuklash|ommaviy qoshish|'
      'import products|bulk upload|bulk add|import from excel|upload from excel|'
      'urun yukle|excelden yukle|toplu ekleme|toplu urun',
  'close_shift': 'конец смены|завершить смену|закрытие кассы|закончить смену|'
      'сменаны бүтүрүү|'
      'smenani tugatish|'
      'finish shift|close register|shift closing|'
      'gun sonu|vardiyayi bitir',
  'profile': 'настройки|мои настройки|настройки приложения|смена языка|сменить тему|кабинет|личный кабинет|'
      'жөндөөлөр|тема|тил тандоо|жеке кабинет|'
      'sozlash|mening profilim|shaxsiy kabinet|tilni almashtirish|'
      'my profile|app settings|my settings|change language|change theme|'
      'ayarlarim|hesabim|tema|dil degistir|kisisel',
  'ai_log': 'что делал ии|история действий ии|лог ии|действия помощника|журнал помощника|'
      'ии эмне кылды|ии тарыхы|'
      'ai nima qildi|ai tarixi|ai amallari|'
      'what did ai do|ai history|ai journal|assistant actions|'
      'yapay zeka gecmisi|yz islemleri|yz gunlugu',
  'assistant_settings': 'помощника|настройки помощника|режим ии|права ии|настройка помощника|режим работы ии|'
      'жардамчы режими|жардамчынын жөндөөлөрү|'
      'yordamchi rejimi|yordamchi sozlamalari|'
      'assistant settings|ai mode|helper|'
      'yardimci modu|yardimci ayarlari|yz modu',
  'sync': 'синхронизацию|отправка данных|очередь отправки|'
      'шайкештирүү|'
      'sinxronlash|malumotlarni yuborish|'
      'sync now|sync data|outbox|'
      'senkronlama|veri gonder',
  'cash': 'положить деньги|взять деньги|инкассация|деньги в кассе|'
      'акча кассага|'
      'kassaga pul|kassadan pul|'
      'put money in|take money out|'
      'kasaya para koy|kasadan para al',
  'debts': 'кто должен|задолженность|долги клиентов|список долгов|'
      'карызы барлар|'
      'qarzdorlar|'
      'who owes|receivables|'
      'borclular|alacaklar',
  'held': 'отложенные чеки|отложить чек|отложенное|парковка чеков|отложенные продажи|'
      'токтотулган сатуулар|'
      'saqlangan sotuvlar|'
      'parked sales|held sales|'
      'bekleyen satislar|askidaki satislar',
  'rentals': 'взять в аренду|выдача в прокат|прокатные позиции|список проката|'
      'ижарага берүү|'
      'ijaraga berish|'
      'rent|rentals list|'
      'kiralamalar|kirala',
  'rental_new': 'оформить прокат|выдать прокат|создать прокат|'
      'ижара түзүү|'
      'ijara rasmiylashtirish|'
      'create rental|start rental|'
      'kiralama olustur',
  'admin': 'админ панель|панель управления|меню управления|'
      'башкаруу панели|админ|'
      'boshqaruv paneli|'
      'admin panel|control panel|'
      'yonetim paneli|admin paneli',
  'prices': 'изменить цену|поменять цену|цены товаров|установить цену|наценка|'
      'баа өзгөртүү|'
      'narx ozgartirish|'
      'change price|set price|markup|'
      'fiyat degistir|fiyat guncelle',
  'shifts': 'кассовые смены|рабочие смены|мои смены|табель|'
      'смена тарыхы|иш убактысы|'
      'smenalar tarixi|jadval|tabel|'
      'shift history|timesheet|work shifts|'
      'vardiya gecmisi|puantaj',
  'orders': 'заказы с сайта|интернет заказы|интернет магазин|'
      'сайттан заказдар|'
      'buyurtma|zakaz|'
      'site orders|web orders|online orders|'
      'online siparis',
  'clients': 'база клиентов|мои клиенты|контрагенты|'
      'кардарлар базасы|'
      'mijozlar bazasi|'
      'customer list|my customers|'
      'musteri listesi|musterilerim',
  'purchase': 'заказать у поставщика|заявка поставщику|закупить|'
      'сатып алуу|жеткирүүчүлөр|'
      'taminotchi|taminotchiga buyurtma|xaridlar|'
      'purchase order|order from supplier|procurement|'
      'tedarikciler|tedarikci siparis',
  'deletions': 'удаления|удаленные позиции|отмененные позиции|'
      'өчүрүлгөндөр|'
      'ochirilganlar|'
      'deleted lines|voided items|'
      'silinen satirlar',
  'receive': 'приемка|приемка от поставщика|принять от поставщика|приход от поставщика|поступление|поступление товара|принять накладную|принять поставку|'
      'кабыл алуу|жеткирүүчүдөн кабыл алуу|товар кабыл алуу|'
      'qabul|tovar qabul|qabul qilish|postavshikdan qabul|nakladnoy qabul|'
      'receive goods|incoming goods|goods receipt|receiving|supplier delivery|'
      'mal kabulu|teslim alma|tedarikci teslimati',
  'invoice': 'загрузить накладную|накладную загрузить|загрузка накладной|электронная накладная|накладная из файла|накладная excel|'
      'накладная жүктөө|накладнойду жүктөө|'
      'nakladnoy yuklash|nakladnoyni yuklash|hisob faktura|'
      'upload invoice|import invoice|load invoice|supplier invoice|'
      'fatura yukle|irsaliye yukle|irsaliye yukleme',
  'smartcam': 'сфотографировать накладную|фото накладной|накладная по фото|снять накладную|сканировать накладную|камера накладная|камера для накладной|сфотографировать полку|'
      'накладнаяны сүрөткө тартуу|текче сүрөтү|'
      'nakladnoyni suratga olish|javon surati|'
      'photo of invoice|scan invoice|photograph invoice|shelf photo|take photo of invoice|'
      'irsaliye fotografi|irsaliyeyi cek|raf fotografi',
  'staff': 'сотрудники и права|управление сотрудниками|кадры|команда|мои сотрудники|продавцы|кассиры|'
      'ишчилер|кызматкер|'
      'ishchilar|xodim|hodimlar|'
      'team|workers|users|'
      'calisan|ekip',
  'payqr': 'оплата по qr|реквизиты qr|настройка qr|'
      'qr төлөм жөндөө|'
      'qr tolov sozlash|'
      'qr settings|qr setup|'
      'qr ayarlari',
  'audit': 'журнал аудита|история действий|кто что делал|журнал действий|'
      'аракеттер журналы|'
      'harakatlar jurnali|'
      'activity log|who did what|'
      'islem gunlugu',
  'notifications': 'колокольчик|'
      'коңгуроо|'
      'qongiroq|'
      'bell|notification center|'
      'bildirim merkezi',
  'quotes': 'счет клиенту|предложение клиенту|коммерческое|'
      'кардарга сунуш|'
      'mijozga taklif|'
      'offer to client|price offer|'
      'musteri teklifi',
  'advisor': 'ии чат|чат с ии|консультант|чат с советником|открыть чат|'
      'ии чат|кеңешчи|'
      'ai chat|maslahatchi chat|'
      'ai chat|ai advisor|chat with advisor|'
      'yz sohbet|yapay zeka sohbeti|sohbet',
  'summary': 'аналитика|прибыль|прибыль и деньги|показатели|итоги дня|финансовый отчет|отчет по продажам|'
      'аналитика|пайда|статистика|'
      'analitika|foyda|statistika|'
      'analytics|profit|overview|sales report|financial report|'
      'analiz|kar|genel bakis|satis raporu',
  'abc': 'abc анализ|abc-анализ|анализ abc|топ товаров|'
      'abc талдоо|'
      'abc tahlili|'
      'abc report|top products|'
      'abc raporu',
  'esf': 'электронная счет фактура|выписать эсф|'
      'эсф жазуу|'
      'esf yozish|'
      'issue esf|e invoice kg|'
      'esf olustur',
  'new_product': 'добавить позицию|завести товар|карточка нового товара|новая карточка|добавление товара|создание товара|'
      'товар түзүү|жаңы товар кошуу|'
      'tovar yaratish|yangi mahsulot|'
      'add item|new item|create item|'
      'urun olustur|yeni stok',
  'tasks': 'задания|поручения|список задач|планировщик|'
      'милдеттер|'
      'topshiriqlar|'
      'to do|todo|assignments|'
      'yapilacaklar|isler',
  'salary': 'расчет зарплаты|выплата зарплаты|табель|начисления|'
      'айлык эсептөө|'
      'oylik hisoblash|'
      'payroll|wages|paycheck|'
      'maas hesabi|bordro',
  'printer': 'настройки принтера|подключить принтер|принтер чеков|настроить принтер|'
      'принтер жөндөө|'
      'printer sozlamalari|'
      'printer settings|connect printer|receipt printer|'
      'yazici ayarlari|yazici baglan',
  'ai_keys': 'ключи ии|ключ ии|подключить ии|настроить ии|ключ нейросети|модели ии|'
      'ии ачкычы|ии жөндөө|'
      'ai kaliti|ai sozlash|'
      'ai keys|ai key|set up ai|ai providers|'
      'yz ayarlari|yapay zeka anahtari',
};


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
    'кириш|кирим',
    'kirim',
    'receive|goods in|simple receive',
    'mal girisi|giris',
  ], parent: 'stock'),
  _def('stock_audit', '/stock/audit', ['Ревизия', 'Ревизия', 'Reviziya', 'Stock count', 'Sayım'], [
    'ревизия|инвентаризация|пересчет|пересчитать',
    'ревизия|санап чыгуу|саноо',
    'reviziya|inventarizatsiya|sanash',
    'stocktake|stock count|inventory count|recount',
    'sayim|envanter',
  ], parent: 'stock'),
  _def('stock_import', '/stock/import', ['Массовая загрузка', 'Жалпы жүктөө', 'Ommaviy yuklash', 'Bulk import', 'Toplu yükleme'], [
    'массовая загрузка|импорт|загрузка товаров|загрузить из excel',
    'жалпы жуктоо|импорт',
    'ommaviy yuklash|import',
    'bulk import|import|upload products',
    'toplu yukleme|ice aktar',
  ], parent: 'stock'),
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
  ], parent: 'profile'), // 2026-10-26 (В5): экран готов (/ai/log), exists: true
  _def('assistant_settings', '/ai/settings', ['Помощник', 'Жардамчы', 'Yordamchi', 'Assistant', 'Yardımcı'], [
    'помощник|режим помощника',
    'жардамчы',
    'yordamchi',
    'assistant|assistant mode',
    'yardimci',
  ], parent: 'profile'), // 2026-10-26 (В5): экран готов (/ai/settings), exists: true
  _def('sync', '/sync', ['Синхронизация', 'Шайкештирүү', 'Sinxronlash', 'Sync', 'Senkronizasyon'], [
    'синхронизация|синхронизировать|обновить данные|очередь',
    'шайкештируу|синхрондоо',
    'sinxronlash|sinxronizatsiya',
    'sync|synchronization',
    'senkronizasyon|senkron',
  ], parent: 'profile'),
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
  ], parent: 'rentals'),
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
  ], parent: 'stock'),
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
  ], parent: 'stock'),
  _def('invoice', '/admin/invoice', ['Накладная', 'Накладная', 'Hisob-faktura', 'Invoice', 'İrsaliye'], [
    'накладная',
    'накладная',
    'nakladnoy|faktura',
    'invoice|waybill',
    'irsaliye',
  ], parent: 'stock'), // 2026-10-27: маршрут /admin/invoice появился (загрузка накладной) — экран настоящий
  _def('smartcam', '/admin/smartcam', ['Умная камера', 'Акылдуу камера', 'Aqlli kamera', 'Smart camera', 'Akıllı kamera'], [
    'умная камера|камера|фото полки',
    'акылдуу камера',
    'aqlli kamera',
    'smart camera|camera',
    'akilli kamera|kamera',
  ], parent: 'stock', params: [
    ParamDef('tab', 'mode', values: {'shelf', 'invoice', 'labels'}, live: true), // 2026-10-27: /admin/smartcam?mode= — SmartCameraScreen.initialMode
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
  ], parent: 'stock'),
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
  ], aux: 'printer', parent: 'profile'),
  _def('ai_keys', null, ['Настройка ИИ', 'ИИ жөндөөсү', 'AI sozlamalari', 'AI settings', 'Yapay zeka ayarları'], [
    'настройка ии|настройки ии|ключ ии|нейросеть',
    'ии жондоосу',
    'ai sozlamalari',
    'ai settings|ai keys',
    'yapay zeka ayarlari',
  ], aux: 'ai_keys', parent: 'profile'),
  // 2026-10-27: экраны профиля и «глубокие цели» (раздел «Связь с кассой», «Ключи Gemini», «Доступ к ИИ», «Режим тестировщика»)
  _def('kassa_link', null, ['Связь с кассой', 'Кассага туташуу', 'Kassa bilan aloqa', 'Till link', 'Kasa bağlantısı'], [
    'связь с кассой|сопряжение с кассой|подключить к кассе|подключение к кассе|сканер для кассы|телефон как сканер|привязать к кассе',
    'кассага туташуу|кассага байланыш|кассага туташтыруу|телефон сканер',
    'kassa bilan aloqa|kassaga ulash|kassaga ulanish|telefon skaner',
    'till link|link with register|register link|connect to register|connect to till|phone as scanner|pair with till|pair with register',
    'kasa baglantisi|kasaya baglan|kasaya bagla|telefonu tarayici yap|kasa eslestirme',
  ], aux: 'kassa_link', parent: 'profile'),
  _def('gemini_keys', null, ['Ключи Gemini', 'Gemini ачкычтары', 'Gemini kalitlari', 'Gemini keys', 'Gemini anahtarları'], [
    'ключи gemini|ключ gemini|ключи джемини|ключи гемини|несколько ключей|ключи|gemini|джемини|гемини',
    'gemini ачкычтары|gemini ачкыч|ачкычтар|ачкыч',
    'gemini kalitlari|gemini kaliti|kalitlar|kalit',
    'gemini keys|gemini key|keys|api keys|google keys',
    'gemini anahtarlari|gemini anahtari|anahtarlar|anahtar',
  ], aux: 'gemini_keys', parent: 'profile'),
  _def('ai_access', null, ['Доступ к ИИ', 'ИИге жеткиликтүүлүк', 'SI dan foydalanish', 'AI access', 'YZ erişimi'], [
    'доступ к ии|кому можно ии|кому разрешен ии|доступ ии|разрешить ии сотрудникам',
    'иидин жеткиликтуулугу|ии кимге мумкун',
    'si dan foydalanish|ai dan foydalanish|kimga ai mumkin',
    'ai access|who can use ai|ai permissions|ai users',
    'yz erisimi|yapay zeka erisimi|yz kimler',
  ], aux: 'ai_access', parent: 'profile', gate: (s) => s.isOwner || s.can(Session.viewSettings)),
  _def('tester', '/tester', ['Режим тестировщика', 'Тестирлөөчү режими', 'Sinovchi rejimi', 'Tester mode', 'Test modu'], [
    'режим тестировщика|тестировщик|тестовый режим|проверка функций|тестирование|режим проверки',
    'тестирлоочу режими|тестирлоочу|сынак режими',
    'sinovchi rejimi|sinovchi|test rejimi',
    'tester mode|tester|test mode|qa mode',
    'test modu|test kullanicisi|tester modu',
  ], parent: 'profile'),
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
  static const excludedRoutes = {'/login', '/cashbox', '/denied'}; // 2026-10-27: '/tester' теперь в реестре (экран «Режим тестировщика»)

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
