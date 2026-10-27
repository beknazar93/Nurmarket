// 2026-10-26 (голос-управление, В0): ворота намерения — действие выполняется, только если в речи ПОЛЬЗОВАТЕЛЯ (расшифровка
// микрофона) есть слово-просьба этого действия, а названные числа сказаны вслух (раздел 2.5 г ТЗ).
// 2026-10-26 (голос-управление, В3а): полный словарь слов-намерений (раздел 3.6 ТЗ, пять языков), сравнение по началу слова
// с «сворачиванием» ң/ө/ү/ş/ğ/ı и апострофов (ASR путает буквы), объекты просьбы (номер чека, имя сотрудника, клиента:
// без них «верни деньги» → уточнение, раздел 2.5 г п. 5), разбор чисел словами подставляется снаружи (lang.dart spokenNumbers).
import '../live_tools.dart';

/// Что видят ворота: последняя реплика пользователя и, если шло уточнение, реплика с исходной просьбой.
class GateSpeech {
  final UserTurn? current;

  /// Реплика, в которой была просьба до уточняющего вопроса («поставь цену Кола» → «какую? — вторую»).
  final String? earlier;
  final DateTime now;
  const GateSpeech(this.current, this.earlier, this.now);
}

/// Итог проверки ворот: null — пропустить; иначе готовый ответ инструмента (not_requested или clarify) без выполнения.
class GateVerdict {
  final String status; // 'not_requested' | 'clarify'
  final String reasonKey; // ключ сообщения для say (см. tool_registry.dart)
  final Map<String, String> params;
  const GateVerdict(this.status, this.reasonKey, [this.params = const {}]);
}

/// Одно требование ворот к вызову: слово-просьба, числа и объекты из аргументов, которые пользователь обязан был сказать сам.
/// У инструмента с несколькими действиями (cart_edit: add / remove / discount…) требований несколько, по полям аргументов.
class GateReq {
  /// Основы слов-просьбы (любого из пяти языков); пусто — слово не требуется (проверяются только числа и объекты).
  final List<String> words;

  /// Имена числовых аргументов (цена, количество, сумма, номер чека).
  final List<String> numeric;

  /// Имена текстовых аргументов-объектов (клиент, сотрудник, товар): их слово должно прозвучать в речи.
  final List<String> objects;

  /// Готовые числа, которые тоже обязаны прозвучать (из вложенных списков: количество в строках прихода, фактический остаток).
  /// Ключ — любая метка для сообщения.
  final Map<String, num> values;
  const GateReq({this.words = const [], this.numeric = const [], this.objects = const [], this.values = const {}});
}

abstract class IntentGate {
  const IntentGate();

  /// [intentWords] — основы слов-просьбы инструмента (любого из пяти языков), [numeric] — числа из аргументов, которые ИИ
  /// передал и которые пользователь обязан был сказать сам, [objects] — названия объектов (клиент, сотрудник), которые
  /// тоже должны прозвучать в речи.
  GateVerdict? check({
    required List<String> intentWords,
    required Map<String, num> numeric,
    required GateSpeech speech,
    List<String> objects = const [],
  });
}

/// Нормализация речи: нижний регистр, «ё»→«е», все апострофы → «'».
String normSpeech(String s) => s.toLowerCase().replaceAll('ё', 'е').replaceAll(RegExp('[’ʻʼ`]'), "'");

const _foldMap = {
  'ё': 'е', 'ң': 'н', 'ө': 'о', 'ү': 'у', 'ӊ': 'н', 'ә': 'а', 'қ': 'к', 'ғ': 'г', 'ҳ': 'х', 'ў': 'у',
  'ı': 'i', 'ş': 's', 'ğ': 'g', 'ç': 'c', 'ö': 'o', 'ü': 'u', 'ä': 'a', 'â': 'a', 'î': 'i', 'û': 'u',
};

/// Для СРАВНЕНИЯ слов речи со словарём: нижний регистр, диакритика убрана (ң→н, ө→о, ü→u, ş→s…), апострофы выброшены
/// («to'la» = «tola», «qo'sh» = «qosh»). И речь, и словарь сворачиваются одинаково.
String foldSpeech(String s) {
  final b = StringBuffer();
  for (final r in s.toLowerCase().runes) {
    if (r == 0x307 || r == 0x2bb || r == 0x2bc || r == 0x2019 || r == 0x60 || r == 0x27) continue;
    final ch = String.fromCharCode(r);
    b.write(_foldMap[ch] ?? ch);
  }
  return b.toString();
}

final _wordSplit = RegExp(r'[^\p{L}\p{N}]+', unicode: true);

List<String> speechWords(String text) => foldSpeech(text).split(_wordSplit).where((w) => w.isNotEmpty).toList();

/// Слово-просьба [phrase] есть в речи: каждое слово фразы — начало какого-то слова речи (в любом порядке: «закрой смену» =
/// «смену закрой»). Одно слово — тоже по началу слова: «цен» находит «цену», но не «процент».
bool speechHasPhrase(List<String> words, String phrase) {
  final toks = speechWords(phrase);
  if (toks.isEmpty) return false;
  // Короткие слова (до 2 букв: «до», «by») — только целиком, иначе «к» нашлось бы в любом слове.
  return toks.every((t) => t.length < 3 ? words.contains(t) : words.any((w) => w.startsWith(t)));
}

bool speechHasAny(String text, Iterable<String> phrases) {
  final words = speechWords(text);
  return phrases.any((p) => speechHasPhrase(words, p));
}

/// Имя объекта (клиент, сотрудник, товар) прозвучало: хотя бы одно слово имени (от 3 букв) совпадает с началом слова речи,
/// допускается другое окончание («Айбек» ↔ «Айбеку», «Кола» ↔ «Колу»).
bool speechHasName(String text, String name) {
  final words = speechWords(text);
  for (final t in speechWords(name)) {
    if (t.length < 3) continue;
    final need = t.length - 2 < 3 ? 3 : t.length - 2;
    for (final w in words) {
      if (w.length < need) continue;
      var k = 0;
      while (k < t.length && k < w.length && t[k] == w[k]) {
        k++;
      }
      if (k >= need) return true;
    }
  }
  return false;
}

/// Имя из списка (сотрудник, клиент, должник, задача) подходит запросу ИИ с учётом падежа: каждое слово запроса (от 3 букв)
/// совпадает с началом какого-то слова имени почти целиком («Нурлану» ↔ «Нурлан», «полку» ↔ «полка», «Асана» ↔ «Асан»).
/// Слово короче трёх букв пропускается. Пустой запрос ничему не подходит.
bool looseNameMatch(String name, String query) {
  final q = speechWords(query).where((w) => w.length >= 3).toList();
  if (q.isEmpty) return false;
  final nt = speechWords(name);
  return q.every((w) {
    for (final n in nt) {
      var k = 0;
      while (k < w.length && k < n.length && w[k] == n[k]) {
        k++;
      }
      // Окончание запроса может отличаться на пару букв («Асана» / «Асан»), а длинное «Асанбек» коротким «Асан» не считается.
      final need = w.length - 2 < 3 ? 3 : w.length - 2;
      if (k >= need) return true;
    }
    return false;
  });
}

/// Числа, названные цифрами: «120», «1 250» (и как 1250, и как 1 и 250), «1,5», «0.5». Слова («сто двадцать») — В2 `spokenNumbers`.
Set<double> extractDigitNumbers(String text) {
  final out = <double>{};
  final re = RegExp(r'\d+(?:[   ]\d{3})*(?:[.,]\d+)?');
  for (final m in re.allMatches(text)) {
    final raw = m.group(0)!;
    final whole = double.tryParse(raw.replaceAll(RegExp(r'[   ]'), '').replaceAll(',', '.'));
    if (whole != null) out.add(whole);
    for (final part in raw.split(RegExp(r'[   ]'))) {
      final v = double.tryParse(part.replaceAll(',', '.'));
      if (v != null) out.add(v);
    }
  }
  return out;
}

/// Ворота по умолчанию: слово-просьба + числа + объекты. Не пропускают реплику-эхо и устаревшую реплику.
/// Числа слов («сто двадцать») разбирает [numberExtractor]; по умолчанию — цифры (в приложении подставляют `spokenNumbers`).
class BasicIntentGate extends IntentGate {
  /// Реплика старше этого срока просьбой не считается (ИИ не должен «доделывать» то, о чём просили давно).
  final Duration ttl;

  /// Как достать числа из речи; В2 подставит разбор слов (`spokenNumbers`).
  final Set<double> Function(String text) numberExtractor;
  const BasicIntentGate({this.ttl = const Duration(minutes: 2), this.numberExtractor = extractDigitNumbers});

  @override
  GateVerdict? check({
    required List<String> intentWords,
    required Map<String, num> numeric,
    required GateSpeech speech,
    List<String> objects = const [],
  }) {
    final cur = speech.current;
    if (cur == null || cur.text.isEmpty) return const GateVerdict('not_requested', 'notRequested');
    // Эхо динамика («да, поставь», сказанное самим ИИ) просьбой не считается.
    if (cur.echoSuspect) return const GateVerdict('not_requested', 'notRequested');
    if (speech.now.difference(cur.updated) > ttl) return const GateVerdict('not_requested', 'notRequested');
    final text = normSpeech(speech.earlier == null ? cur.text : '${speech.earlier} ${cur.text}');
    if (intentWords.isNotEmpty && !speechHasAny(text, intentWords)) {
      return const GateVerdict('not_requested', 'notRequested');
    }
    if (intentWords.isEmpty && numeric.isEmpty && objects.isEmpty) return const GateVerdict('not_requested', 'notRequested');
    // Объект просьбы (кому выплатить, по какому чеку вернуть) должен прозвучать: иначе уточнение, а не действие.
    for (final o in objects) {
      if (o.trim().isEmpty || !speechHasName(text, o)) {
        return GateVerdict('clarify', 'needObject', {'what': o.trim()});
      }
    }
    if (numeric.isNotEmpty) {
      final said = numberExtractor(text);
      for (final e in numeric.entries) {
        final v = e.value.toDouble();
        if (!said.any((x) => (x - v).abs() <= 0.01)) {
          // ИИ передал число, которого пользователь не называл: переспрос «цена 120 — правильно?» — это не просьба разрешения.
          final shown = v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toString();
          return GateVerdict('clarify', 'confirmNumber', {'n': shown});
        }
      }
    }
    return null;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Словарь слов-намерений (раздел 3.6 ТЗ). Основы слов, ru + ky + uz + en + tr вместе: слова разных языков почти не
// пересекаются. Сравнение — по началу слова и в свёрнутом виде (см. foldSpeech), поэтому «өчүр» = «очур», «to'la» = «tola».
// Фраза из нескольких слов — все слова есть в речи, порядок не важен. Правки словаря — только здесь.
// ---------------------------------------------------------------------------------------------------------------------

abstract final class Intents {
  /// Оплата чека. «Пробей» само по себе — добавить в чек, поэтому как оплата считается только с чеком / способом оплаты.
  static const pay = [
    'оплат', 'оплач', 'рассчита', 'расчет', 'проведи чек', 'закрой чек', 'пробей чек', 'пробей наличн', 'пробей карт', 'пробей в долг',
    'пробить чек', 'принял оплату',
    'төлө', 'эсепте', 'төлөт',
    "to'la", 'tola', 'hisobla', "to'lov",
    'pay', 'checkout', 'check out', 'charge',
    'öde', 'ödeme',
  ];

  static const cartAdd = [
    'добав', 'положи', 'положь', 'пробей', 'еще', 'ещё', 'возьми', 'закинь', 'кинь', 'вбей', 'внеси в чек', 'дай',
    'кош', 'дагы', 'бер',
    "qo'sh", 'qosh', 'yana', 'ber', 'kerak',
    'add', 'one more', 'another', 'put', 'give me', 'need',
    'ekle', 'daha', 'ver',
  ];

  static const cartRemove = [
    'убери', 'убрать', 'удали', 'удалить', 'сними', 'выкинь', 'вычеркни',
    'алып сал', 'алып таш', 'өчүр', 'чыгар',
    "olib tashla", 'olib tasha', "o'chir", 'ochir', 'chiqar',
    'remove', 'delete', 'take off', 'take out', 'drop',
    'çıkar', 'sil', 'kaldır',
  ];

  static const cartQty = [
    'колич', 'сделай', 'измени', 'поставь', 'замени', 'штук', 'упаков', 'пусть', 'ровно',
    'санын', 'өзгөрт', 'кыл', 'болсун',
    'soni', 'miqdor', 'qil', "o'zgartir", 'ozgartir',
    'quantity', 'qty', 'change', 'make it', 'set',
    'adet', 'miktar', 'yap', 'değiştir',
  ];

  static const cartDiscount = [
    'скидк', 'скинь', 'уступ', 'процент', 'дисконт', 'сбавь',
    'арзандат', 'жеңилдик', 'арзан', 'пайыз',
    'chegirma', 'skidka', 'foiz',
    'discount', 'percent', 'off',
    'indirim', 'yüzde',
  ];

  static const cartClear = [
    'очист', 'сбрось', 'обнул', 'удали все', 'убери все', 'удали чек', 'сотри',
    'тазала', 'өчүр',
    'tozala', "o'chir", 'ochir',
    'clear', 'empty', 'reset',
    'temizle', 'boşalt', 'sıfırla',
  ];

  static const hold = [
    'отложи', 'отложить', 'придержи', 'отлож',
    'токтот', 'сакта', 'кое тур',
    'kechiktir', 'saqla', 'ushlab tur',
    'hold', 'park', 'put aside',
    'beklet', 'askıya',
  ];

  static const restore = [
    'отложенн', 'достань', 'восстанов', 'продолж', 'вернись к чеку',
    'калыбына', 'кайтар', 'ачыл',
    'tikla', 'qaytar', 'davom',
    'restore', 'resume', 'bring back',
    'geri getir', 'devam',
  ];

  static const refund = [
    'верни', 'возврат', 'вернуть', 'вернем', 'оформи возврат', 'отмени чек', 'отмени продаж',
    'кайтар', 'кайтарым',
    'qaytar', 'vozvrat',
    'refund', 'return', 'give back', 'money back',
    'iade', 'geri öde',
  ];

  static const receive = [
    'прими', 'принять', 'оприходуй', 'оприходовать', 'приход', 'проведи приход', 'принял', 'поступил', 'приемк', 'приёмк',
    'кабыл ал', 'кабыл', 'кириш', 'келди',
    'qabul', 'kirim', 'keldi',
    'receive', 'receiving', 'stock in', 'book in', 'delivery',
    'teslim al', 'kabul', 'giriş',
  ];

  static const writeOff = [
    'спиши', 'списан', 'списать', 'списание', 'выбрось', 'брак',
    'эсептен чыгар', 'эсептен алып', 'чыгаруу', 'списка',
    'hisobdan chiqar', 'spisat', 'yoq qil',
    'write off', 'write-off', 'scrap', 'damaged',
    'düş', 'zayi', 'imha',
  ];

  static const count = [
    'пересчита', 'ревизи', 'остаток стал', 'пересчет', 'инвентар', 'по факту', 'фактическ', 'на самом деле',
    'санап чык', 'кайра сана', 'ревизия',
    'sanab chiq', 'qayta sana', 'revizi',
    'count', 'recount', 'inventory', 'stocktake',
    'sayım', 'envanter',
  ];

  static const price = [
    'цен', 'наценк', 'стоимост', 'прайс', 'поставь', 'установи', 'сделай', 'измени', 'подними', 'снизь', 'продавай',
    'баа', 'баасын', 'үстөк', 'кыл', 'койсун',
    'narx', 'ustama', 'baho', 'qil',
    'price', 'markup', 'cost', 'set', 'make',
    'fiyat', 'kâr', 'marj', 'yap',
  ];

  static const priceBulk = [
    'наценк', 'подними', 'повысь', 'цен', 'сделай', 'поставь', 'процент',
    'үстөк', 'баа', 'пайыз',
    'ustama', 'narx', 'foiz',
    'markup', 'raise', 'price', 'percent',
    'kâr', 'marj', 'fiyat', 'yüzde',
  ];

  static const barcode = [
    'штрихкод', 'штрих код', 'штрих-код', 'баркод',
    'штрихкод', 'штрих код', 'баркод',
    'shtrixkod', 'barkod', 'shtrix kod',
    'barcode', 'bar code',
    'barkod',
  ];

  static const minQty = [
    'минимум', 'минимал', 'мин остат',
    'минимум', 'минималдуу', 'эң аз',
    'minimum', 'eng kam',
    'minimum', 'min stock', 'reorder level',
    'minimum', 'asgari',
  ];

  static const shiftClose = [
    'закр смен', 'закр касс', 'закрытие смен',
    'смен жап', 'смен жаб',
    'smen yop', 'smena yop', 'smenani yop',
    'close shift', 'end shift', 'close the shift',
    'vardiya kapat', 'vardiyayı kapat',
  ];

  static const shiftOpen = [
    'откр смен', 'открытие смен', 'начни смен', 'начать смен',
    'смен ач', 'смен баштоо',
    'smen och', 'smena och', 'smenani och',
    'open shift', 'start shift', 'open the shift',
    'vardiya aç', 'vardiya başlat',
  ];

  static const cashIn = [
    'внеси', 'внести', 'внесение', 'положи в касс', 'положи в ящик', 'положи деньги', 'на размен',
    'салып кой', 'кассага', 'киргиз',
    'kirit', 'kassaga',
    'cash in', 'deposit', 'put in the drawer', 'put in drawer', 'add cash',
    'kasaya koy', 'kasaya ekle', 'kasaya',
  ];

  static const cashOut = [
    'изъять', 'изъят', 'изыми', 'сними из касс', 'выдай из касс', 'возьми из касс', 'забери из касс', 'забери деньги',
    'алып ал', 'кассадан', 'алуу',
    'kassadan', 'olib qo', 'chiqar',
    'cash out', 'withdraw', 'take from the drawer', 'take cash',
    'kasadan al', 'kasadan çek', 'kasadan',
  ];

  static const debtPay = [
    'погаси', 'погаш', 'оплатил долг', 'оплати долг', 'долг', 'вернул долг', 'принял долг', 'заплатил', 'отдал',
    'карыз', 'карызын төлө',
    'qarz', "qarzni to'la",
    'pay debt', 'debt', 'paid', 'pays',
    'borç', 'borcu öde',
  ];

  static const salary = [
    'выплат', 'аванс', 'зарплат', 'заплати', 'отдай', 'выдай', 'заработн', 'оклад',
    'төлөп бер', 'аванс', 'айлык', 'маяна',
    'maosh', 'avans', 'oylik', "to'la", 'tola', 'ber',
    'pay salary', 'advance', 'salary', 'payroll', 'pay',
    'maaş', 'avans', 'öde',
  ];

  static const salaryScheme = [
    'схем', 'оклад', 'процент', 'ставк', 'за штуку', 'за товар', 'установи', 'поставь', 'измени',
    'схема', 'айлык', 'пайыз', 'койсун',
    'sxema', 'maosh', 'foiz', 'stavka',
    'scheme', 'rate', 'base', 'percent', 'set',
    'şema', 'maaş', 'oran', 'yüzde',
  ];

  static const taskCreate = [
    'задач', 'поручи', 'поручение', 'напомни', 'назначь', 'поставь', 'задание', 'пусть', 'передай', 'надо чтобы',
    'тапшырма', 'тапшыр', 'эскерт', 'милдет', 'койсун',
    'vazifa', 'topshiriq', 'eslat', 'tayinla', 'ber',
    'task', 'assign', 'remind', 'todo', 'to-do', 'give him', 'tell him',
    'görev', 'ata', 'hatırlat', 'iş ver',
  ];

  static const taskDone = [
    'сдела', 'выполн', 'готов', 'закончил', 'заверш', 'исполн', 'закрой задач',
    'аткар', 'бүттү', 'бүткөн', 'даяр',
    'bajar', 'tugad', 'tayyor', 'tamomla',
    'done', 'finish', 'complete', 'ready',
    'bitti', 'tamam', 'yaptı', 'tamamla',
  ];

  static const taskProgress = [
    'в работе', 'начал', 'приступ', 'работает', 'делает', 'взял',
    'иштеп', 'баштады', 'башта',
    'boshla', 'ishlayapti', 'ishlay',
    'started', 'working', 'in progress', 'begin',
    'başla', 'çalışıyor',
  ];

  static const taskDue = [
    'срок', 'перенеси', 'до завтра', 'дедлайн', 'крайн',
    'мөөнөт', 'чейин', 'жылдыр',
    'muddat', 'gacha', 'ko\'chir',
    'deadline', 'due', 'postpone',
    'son tarih', 'tarih', 'ertele', 'kadar',
  ];

  static const taskAssignee = [
    'передай', 'назнач', 'переназнач', 'поручи', 'пусть', 'отдай',
    'тапшыр', 'койсун', 'бер',
    'topshir', 'tayinla', 'ber',
    'assign', 'reassign', 'give to', 'hand over',
    'ata', 'devret', 'ver',
  ];

  static const taskPriority = [
    'срочн', 'важн', 'приоритет', 'горит',
    'шашылыш', 'маанилүү', 'приоритет',
    'shoshilinch', 'muhim', 'tezkor',
    'urgent', 'high', 'priority', 'important',
    'acil', 'önemli', 'öncelik',
  ];

  static const delete = [
    'удал товар', 'удали товар', 'удалить товар', 'удал позици',
    'товар өчүр', 'товарды өчүр', 'товар очур',
    'tovar ochir', "tovarni o'chir",
    'delete', 'remove product', 'delete product',
    'ürünü sil', 'ürün sil',
  ];

  static const undo = [
    'отмен', 'откат', 'верни как было', 'как было', 'назад верн', 'отмени',
    'жокко чыгар', 'кайра кой', 'мурункудай',
    'bekor', 'qaytar', 'undo', 'oldingidek',
    'undo', 'cancel', 'revert', 'take back', 'roll back',
    'geri al', 'iptal', 'vazgeç',
  ];

  static const theme = [
    'тем', 'тёмн', 'темн', 'светл', 'режим', 'ночн', 'дневн', 'авто',
    'караңгы', 'тема', 'жарык', 'кечки', 'режим',
    'tema', 'qorong', 'yorug', 'tun', 'rejim',
    'theme', 'dark', 'light', 'night', 'mode',
    'tema', 'karanlık', 'aydınlık', 'koyu', 'gece', 'mod',
  ];

  static const language = [
    'язык', 'по-русски', 'по русски', 'по-кыргызски', 'по кыргызски', 'на кыргыз', 'на русск', 'на англ', 'на турецк', 'на узбек', 'говори',
    'тил', 'кыргызча', 'орусча', 'англисче', 'өзбекче', 'түркчө', 'сүйлө',
    'til', "o'zbekcha", 'ozbekcha', 'ruscha', 'inglizcha', 'qirgizcha', 'turkcha', 'gapir',
    'language', 'english', 'speak', 'switch to', 'russian', 'kyrgyz', 'uzbek', 'turkish',
    'dil', 'türkçe', 'rusça', 'ingilizce', 'konuş',
  ];

  static const bonus = [
    'бонус', 'начисли', 'баллы', 'балл', 'спиши бонус',
    'бонус', 'упай',
    'bonus', 'ball',
    'bonus', 'points', 'loyalty',
    'bonus', 'puan',
  ];

  static const orderStatus = [
    'заказ', 'статус', 'собран', 'готов', 'выдан', 'принят', 'отмени заказ',
    'буйрутма', 'даяр', 'статус',
    'buyurtma', 'tayyor', 'zakaz', 'status',
    'order', 'ready', 'status', 'complete', 'cancel',
    'sipariş', 'hazır', 'durum',
  ];

  static const clientCreate = [
    'создай клиент', 'новый клиент', 'добавь клиент', 'заведи клиент', 'заведи', 'запиши клиент',
    'кардар түз', 'жаңы кардар', 'кардар кош',
    'mijoz yarat', 'yangi mijoz', "mijoz qo'sh",
    'create client', 'new client', 'add client', 'create customer', 'new customer', 'add customer',
    'müşteri ekle', 'yeni müşteri',
  ];

  static const productCreate = [
    'создай товар', 'новый товар', 'добавь товар', 'заведи товар',
    'жаңы товар', 'товар түз', 'товар кош',
    'yangi tovar', 'tovar yarat', "tovar qo'sh",
    'create product', 'new product', 'add product',
    'yeni ürün', 'ürün ekle',
  ];

  // 2026-10-27 (мобилка, видеозвонок, М2): правки товара голосом и мастер «добавить товар» в звонке.
  /// Название товара: «переименуй», «назови», «название …».
  /// 2026-10-27 (ревью М-7): только слова-ПРОСЬБЫ (глагол или «смени название»). Голые существительные «название», «имя», «называется»,
  /// «name» были в списке — вопрос «как называется эта вода?» считался просьбой переименовать.
  static const rename = [
    'переимен', 'назови', 'назвать', 'переназов', 'поменяй назван', 'измени назван', 'смени назван', 'постав назван', 'нов назван', 'нов имя',
    'атын өзгөрт', 'аталышын өзгөрт', 'атоо', 'кайра ата', 'жаңы ат',
    "nomini o'zgartir", 'nomini ozgartir', 'nomini almashtir', 'nomla', 'yangi nom',
    'rename', 'call it', 'change the name', 'change name', 'set the name', 'set name', 'new name',
    'adını değiştir', 'ismini değiştir', 'yeniden adlandır', 'adlandır', 'yeni ad',
  ];

  static const category = [
    'категор', 'раздел', 'групп',
    'категория', 'бөлүм', 'топ',
    "bo'lim", 'kategoriya', 'guruh',
    'category', 'section', 'group',
    'kategori', 'bölüm', 'grup',
  ];

  static const unit = [
    'единиц', 'измерени', 'в штук', 'в килограмм', 'в литр', 'штучн', 'весов', 'на вес',
    'бирдик', 'өлчөм', 'даана', 'килограмм',
    'birlik', "o'lchov", 'dona', 'kilogramm',
    'unit', 'measure', 'by weight', 'pieces',
    'birim', 'ölçü', 'adet', 'kilogram',
  ];

  /// Подтверждение вслух («да») после того, как программа зачитала итог.
  static const wizardConfirm = [
    'да', 'создай', 'создавай', 'создать', 'подтвержд', 'верно', 'правильно', 'давай', 'ага', 'угу', 'хорошо', 'ок', 'окей',
    'ооба', 'туура', 'жарайт', 'түз',
    'ha', 'xa', 'yarat', "to'g'ri", 'togri', 'mayli', 'ok',
    'yes', 'yeah', 'create', 'confirm', 'correct', 'right', 'do it', 'go ahead',
    'evet', 'oluştur', 'doğru', 'tamam', 'onay',
  ];

  /// «Готово / снимай / сзади …» — реплика, после которой мастер снимает кадр.
  static const wizardShot = [
    'готов', 'снима', 'сними', 'снять', 'давай', 'есть', 'вот', 'спереди', 'сзади', 'сбоку', 'передн', 'задн', 'боков', 'сторон', 'штрихкод', 'ещё', 'еще',
    'пересними', 'повтори', 'дальше', 'поехали', 'всё', 'все', 'так',
    'даяр', 'тарт', 'сүрөт', 'алды', 'арты', 'капта', 'дагы', 'болду', 'штрих',
    'tayyor', 'suratga', 'old', 'orqa', 'yon', 'yana', 'shtrix', 'tamom', 'ol',
    'ready', 'done', 'shoot', 'capture', 'take', 'front', 'back', 'side', 'more', 'next', 'go', 'barcode', 'now', 'retake',
    'hazır', 'çek', 'önden', 'arka', 'yan', 'daha', 'tamam', 'barkod',
  ];

  /// «Хватит / достаточно / пропусти» — закупку и остаток не называть, итог сразу.
  static const wizardFinish = [
    'хватит', 'достаточно', 'пропусти', 'пропуск', 'дальше', 'создавай', 'без закупк', 'без остатк',
    'жетет', 'болду', 'өткөр',
    "bo'ldi", 'boldi', 'yetarli', "o'tkaz",
    'enough', 'skip', "that's all", 'thats all', 'done',
    'yeter', 'geç', 'atla',
  ];

  static const noBarcode = [
    'без штрих', 'нет штрих', 'штрихкода нет', 'пропусти штрих', 'пропустить штрих', 'без кода', 'нет кода',
    'штрихкодсуз', 'штрихкод жок', 'кодсуз',
    'shtrixkodsiz', "shtrixkod yo'q", 'shtrixkod yoq', 'kodsiz',
    'no barcode', 'without barcode', 'without a barcode', 'skip barcode', 'no code',
    'barkodsuz', 'barkod yok', 'kodsuz',
  ];

  /// «Отмена» мастера: сбросить черновик товара.
  static const wizardCancel = [
    'отмен', 'отбой', 'сброс', 'не надо', 'не нужно', 'хватит', 'забудь', 'стоп',
    'жокко чыгар', 'болбойт', 'керек эмес', 'токтот',
    'bekor', 'kerak emas', "to'xta", 'toxta',
    'cancel', 'forget it', 'never mind', 'stop', 'abort',
    'iptal', 'vazgeç', 'gerek yok', 'dur',
  ];

  static const wizardTeach = [
    'обуч', 'научи', 'эталон', 'камеру', 'запомни',
    'үйрөт', 'эстеп кал',
    "o'rgat", 'orgat', 'eslab qol',
    'teach', 'train', 'remember',
    'eğit', 'egit', 'hatırla',
  ];

  /// Подтверждение УДАЛЕНИЯ вслух (второй шаг после показа карточки товара).
  static const deleteConfirm = [
    'да', 'удал', 'подтвержд', 'точно', 'уверен',
    'ооба', 'өчүр', 'ырастаймын',
    'ha', 'xa', "o'chir", 'ochir', 'tasdiqlay',
    'yes', 'delete', 'confirm', 'sure', 'remove',
    'evet', 'sil', 'onay', 'eminim',
  ];

  /// «Да» вслух (строгая проверка внутри инструмента: подтверждение удаления и создания товара).
  static const yes = [
    'да', 'ага', 'угу', 'конечно', 'верно', 'правильно', 'давай', 'создай', 'создавай', 'создать', 'удали', 'удаляй', 'удалить', 'подтвержд', 'хорошо', 'ок', 'окей',
    'ооба', 'туура', 'жарайт', 'өчүр',
    'ha', 'xa', 'mayli', "to'g'ri", 'togri', 'yarat', "o'chir", 'ochir',
    'yes', 'yeah', 'ok', 'okay', 'create', 'confirm', 'correct', 'right', 'sure', 'go ahead', 'do it', 'delete',
    'evet', 'tamam', 'doğru', 'onay', 'oluştur', 'sil',
  ];

  /// «Нет» вслух: отменяет любое «да» в той же реплике («нет, не удаляй», «не надо создавать»).
  static const no = [
    'нет', 'не', 'неа', 'отмен', 'стоп', 'подожди', 'погоди', 'стой', 'отбой', 'передума',
    'жок', 'эмес', 'токто', 'болбойт',
    'yoq', "yo'q", 'emas', 'bekor', "to'xta", 'toxta',
    'no', 'not', "don't", 'dont', 'stop', 'wait', 'cancel', 'never',
    'hayır', 'hayir', 'değil', 'iptal', 'dur', 'vazgeç',
  ];
}

/// Пользователь сказал «да» и не сказал «нет» в той же реплике. Для подтверждений с последствиями (удалить, создать товар).
bool saidYes(String text) => speechHasAny(text, Intents.yes) && !speechHasAny(text, Intents.no);
