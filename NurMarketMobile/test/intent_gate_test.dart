// 2026-10-26 (голос-управление, В3а): ворота намерения — словарь слов-просьб на 5 языках (раздел 3.6 ТЗ), числа словами
// (spokenNumbers), объекты просьбы (имя, номер чека), эхо и устаревшая реплика. Чистые функции, без сети и без микрофона.
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/intent_gate.dart';
import 'package:nurmarket/core/ai/assistant/lang.dart';
import 'package:nurmarket/core/ai/live_tools.dart';

final _t0 = DateTime(2026, 10, 26, 12);

GateSpeech _speech(String text, {String? earlier, bool echo = false, DateTime? at, DateTime? now}) {
  final log = UserSpeechLog();
  log.add(text, echoSuspect: echo, now: at ?? _t0);
  return GateSpeech(log.last, earlier, now ?? at ?? _t0);
}

const _gate = BasicIntentGate(numberExtractor: spokenNumbers);

GateVerdict? _check(List<String> words, String text, {Map<String, num> numeric = const {}, List<String> objects = const [], String? earlier}) =>
    _gate.check(intentWords: words, numeric: numeric, speech: _speech(text, earlier: earlier), objects: objects);

void main() {
  group('словарь: слово-просьба на каждом из пяти языков', () {
    // [список, [фразы, которые должны пройти]]
    final cases = <String, (List<String>, List<String>)>{
      'оплата': (Intents.pay, ['оплата наличными', 'пробей чек', 'рассчитай клиента', 'төлө наличными', "to'la naqd", 'hisobla', 'pay cash', 'checkout please', 'öde kartla']),
      'в корзину': (Intents.cartAdd, ['добавь колу', 'положи марс', 'ещё одну', 'кош колу', 'дагы бир', "qo'sh kola", 'yana bitta', 'add cola', 'one more cola', 'ekle']),
      'убрать': (Intents.cartRemove, ['убери сникерс', 'удали из чека', 'алып сал', 'өчүр', 'olib tashla', 'remove snickers', 'çıkar', 'sil']),
      'возврат': (Intents.refund, ['верни деньги за чек', 'возврат по чеку', 'кайтар', 'qaytar', 'refund the receipt', 'return it', 'iade et']),
      'приход': (Intents.receive, ['прими пять ящиков', 'оприходуй воду', 'проведи приход', 'кабыл ал', 'кириш жаса', 'qabul qil', 'kirim', 'receive the water', 'teslim al']),
      'списание': (Intents.writeOff, ['спиши две бутылки', 'эсептен чыгар', 'hisobdan chiqar', 'write off two', 'düş şunu', 'zayi']),
      'ревизия': (Intents.count, ['пересчитай воду', 'ревизия склада', 'остаток стал двенадцать', 'санап чык', 'sanab chiq', 'recount the water', 'sayım yap']),
      'цена': (Intents.price, ['поставь цену 120', 'наценка 20', 'баасын койчу', 'narxni qo\'y', 'set the price', 'fiyatı yap']),
      'закрыть смену': (Intents.shiftClose, ['закрой смену', 'смену закрой', 'закрыть смену пожалуйста', 'сменаны жап', 'smenani yop', 'close shift', 'close the shift', 'vardiyayı kapat']),
      'открыть смену': (Intents.shiftOpen, ['открой смену', 'сменаны ач', 'smenani och', 'open shift', 'vardiyayı aç']),
      'внести в кассу': (Intents.cashIn, ['внеси в кассу тысячу', 'салып кой', 'kassaga kirit', 'cash in 500', 'kasaya koy']),
      'изъять из кассы': (Intents.cashOut, ['изъять пятьсот', 'сними из кассы', 'алып ал', 'kassadan ol', 'cash out 500', 'kasadan al']),
      'долг': (Intents.debtPay, ['Асан погасил долг', 'карызын төлөдү', 'qarzni to\'ladi', 'pay debt', 'borcu öde']),
      'зарплата': (Intents.salary, ['выплати Айбеку аванс', 'зарплату выдай', 'айлык төлөп бер', 'maosh to\'la', 'pay salary', 'maaş öde']),
      'задача': (Intents.taskCreate, ['поставь задачу Нурлану', 'поручи пересчитать', 'тапшырма бер', 'vazifa ber', 'task for him', 'görev ver']),
      'удаление': (Intents.delete, ['удали товар Кола', 'товарды өчүр', 'tovarni o\'chir', 'delete the product', 'ürünü sil']),
      'отмена': (Intents.undo, ['отмени последнее', 'верни как было', 'жокко чыгар', 'bekor qil', 'undo that', 'geri al']),
      'тема': (Intents.theme, ['включи тёмную тему', 'светлая тема', 'караңгы тема', 'tema qorong\'u', 'dark theme', 'koyu tema']),
      'язык': (Intents.language, ['переключи язык', 'говори по-кыргызски', 'тилди өзгөрт', 'til o\'zgartir', 'switch to english', 'dil değiştir']),
    };

    for (final e in cases.entries) {
      test('${e.key}: ${e.value.$2.length} фраз проходят', () {
        for (final phrase in e.value.$2) {
          expect(speechHasAny(phrase, e.value.$1), isTrue, reason: '«$phrase» должна открыть ворота «${e.key}»');
        }
      });
    }

    test('мимо: обычная болтовня, подстрока внутри слова и слова-близнецы не открывают ворота', () {
      expect(speechHasAny('привет, как дела', Intents.pay), isFalse);
      expect(speechHasAny('что это за товар?', Intents.pay), isFalse);
      // «цен» — начало слова, а не подстрока: «процент» цену не просит
      expect(speechHasAny('двадцать процентов', ['цен']), isFalse);
      expect(speechHasAny('поставь цену', ['цен']), isTrue);
      // «финансы» не содержат «нан» (правило владельца ПК), «dark» не находится в «darker»-подстроке слева
      expect(speechHasAny('финансы', ['нан']), isFalse);
      // «пробей» одно = добавить в чек, но НЕ оплата
      expect(speechHasAny('пробей колу', Intents.pay), isFalse);
      expect(speechHasAny('пробей колу', Intents.cartAdd), isTrue);
      expect(speechHasAny('пробей колу в долг', Intents.pay), isTrue);
      expect(speechHasAny('пробей наличными', Intents.pay), isTrue);
      // внести и изъять — разные слова
      expect(speechHasAny('внеси в кассу тысячу', Intents.cashOut), isFalse);
      expect(speechHasAny('изъять тысячу', Intents.cashIn), isFalse);
    });

    test('короткие слова — только целиком, свёртка букв: ө/ү/ң, апострофы, ı/ş/ğ', () {
      expect(speechHasPhrase(speechWords('до завтра'), 'до'), isTrue);
      expect(speechHasPhrase(speechWords('документы'), 'до'), isFalse);
      expect(foldSpeech("to'la O'zbek"), 'tola ozbek');
      expect(foldSpeech('өчүр Көк ңан'), 'очур кок нан');
      expect(foldSpeech('Şişe Ağaç ılık'), 'sise agac ilik');
      // ASR написал «очур» вместо «өчүр»
      expect(speechHasAny('товарды очур', Intents.delete), isTrue);
    });

    test('в словаре нет пустых строк и одиночных слов короче 3 букв (иначе ворота пропускали бы всё)', () {
      final lists = <String, List<String>>{
        'pay': Intents.pay, 'cartAdd': Intents.cartAdd, 'cartRemove': Intents.cartRemove, 'cartQty': Intents.cartQty,
        'cartDiscount': Intents.cartDiscount, 'cartClear': Intents.cartClear, 'hold': Intents.hold, 'restore': Intents.restore,
        'refund': Intents.refund, 'receive': Intents.receive, 'writeOff': Intents.writeOff, 'count': Intents.count,
        'price': Intents.price, 'priceBulk': Intents.priceBulk, 'barcode': Intents.barcode, 'minQty': Intents.minQty,
        'shiftClose': Intents.shiftClose, 'shiftOpen': Intents.shiftOpen, 'cashIn': Intents.cashIn, 'cashOut': Intents.cashOut,
        'debtPay': Intents.debtPay, 'salary': Intents.salary, 'salaryScheme': Intents.salaryScheme, 'taskCreate': Intents.taskCreate,
        'taskDone': Intents.taskDone, 'taskProgress': Intents.taskProgress, 'taskDue': Intents.taskDue,
        'taskAssignee': Intents.taskAssignee, 'taskPriority': Intents.taskPriority, 'delete': Intents.delete, 'undo': Intents.undo,
        'theme': Intents.theme, 'language': Intents.language, 'bonus': Intents.bonus, 'orderStatus': Intents.orderStatus,
        'clientCreate': Intents.clientCreate, 'productCreate': Intents.productCreate,
      };
      for (final e in lists.entries) {
        expect(e.value, isNotEmpty, reason: e.key);
        for (final w in e.value) {
          final toks = speechWords(w);
          expect(toks, isNotEmpty, reason: '${e.key}: «$w»');
          if (toks.length == 1) expect(toks.first.length, greaterThanOrEqualTo(3), reason: '${e.key}: одиночное «$w» слишком короткое');
        }
      }
    });
  });

  group('имена: объект просьбы', () {
    test('склонение и падеж: Айбеку = Айбек, Нурлану = Нурлан, Колу = Кола', () {
      expect(speechHasName('выплати Айбеку десять тысяч', 'Айбек'), isTrue);
      expect(speechHasName('поставь Нурлану задачу', 'Нурлан'), isTrue);
      expect(speechHasName('добавь колу', 'Кола'), isTrue);
      expect(speechHasName('на Асана', 'Асан'), isTrue);
      expect(speechHasName('выплати Нурлану', 'Айбек'), isFalse);
      expect(speechHasName('выплати Айбеку', 'Айбек Токтогулов'), isTrue); // хотя бы одно слово имени
      expect(speechHasName('привет', 'Асан'), isFalse);
    });
  });

  group('ворота: числа словами, объекты, эхо', () {
    test('число цифрами и словами на ru / ky / uz / en / tr', () {
      for (final (phrase, n) in <(String, num)>[
        ('поставь цену 120', 120),
        ('поставь цену сто двадцать', 120),
        ('баасын бир жүз жыйырма кыл', 120),
        ('narxni bir yuz yigirma qil', 120),
        ('set the price to one hundred twenty', 120),
        ('fiyatı yüz yirmi yap', 120),
        ('цену двенадцать тысяч триста', 12300),
        ('цену 1 250', 1250),
        ('цену 1,5', 1.5),
        ('цену полтора', 1.5),
      ]) {
        expect(_check(Intents.price, phrase, numeric: {'price': n}), isNull, reason: phrase);
      }
    });

    test('число не из речи → clarify «правильно?», а не отказ; без слова-просьбы → not_requested', () {
      final v = _check(Intents.price, 'поставь Сникерсу цену 120', numeric: {'price': 150})!;
      expect(v.status, 'clarify');
      expect(v.reasonKey, 'confirmNumber');
      expect(v.params['n'], '150');
      expect(_check(Intents.price, 'привет, как дела 120', numeric: {'price': 120})!.status, 'not_requested');
    });

    test('объект: «выплати» без имени → уточнение; имя в другом падеже проходит; в уточняющем ответе — из прошлой реплики', () {
      final no = _check(Intents.salary, 'выплати десять тысяч аванс', numeric: {'amount': 10000}, objects: ['Айбек'])!;
      expect(no.status, 'clarify');
      expect(no.reasonKey, 'needObject');
      expect(no.params['what'], 'Айбек');
      expect(_check(Intents.salary, 'выплати Айбеку десять тысяч', numeric: {'amount': 10000}, objects: ['Айбек']), isNull);
      // ответ на «кому?» — «Айбеку», слово «выплати» было в прошлой реплике
      expect(_check(Intents.salary, 'Айбеку', numeric: {'amount': 10000}, objects: ['Айбек'], earlier: 'выплати десять тысяч'), isNull);
    });

    test('номер чека числом: сказан — проходит, не сказан — уточнение', () {
      expect(_check(Intents.refund, 'верни деньги за чек 1043', numeric: {'check_number': 1043}), isNull);
      expect(_check(Intents.refund, 'верни деньги за чек', numeric: {'check_number': 1043})!.status, 'clarify');
    });

    test('эхо динамика и устаревшая реплика ворота не открывают', () {
      expect(_gate.check(intentWords: Intents.price, numeric: const {}, speech: _speech('да, поставь цену', echo: true)), isNotNull);
      final old = _speech('поставь цену', now: _t0.add(const Duration(minutes: 5)), at: _t0);
      expect(_gate.check(intentWords: Intents.price, numeric: const {}, speech: old)!.status, 'not_requested');
      expect(_gate.check(intentWords: Intents.price, numeric: const {}, speech: GateSpeech(null, null, _t0))!.status, 'not_requested');
    });

    test('действие без слов, чисел и объектов не проходит (пустое требование)', () {
      expect(_check(const [], 'что угодно')!.status, 'not_requested');
    });
  });

  // 2026-10-27 (ревью М-7): вопрос «как называется…» — не просьба переименовать; просьба-глагол проходит на пяти языках
  group('переименование: вопрос не просьба', () {
    test('вопросы про название не проходят ворота', () {
      for (final q in ['как называется эта вода', 'какое название у этого товара', 'как его имя', 'аталышы кандай', "nomi nima", 'what is the name of it', 'adı ne']) {
        expect(_check(Intents.rename, q)?.status, 'not_requested', reason: q);
      }
    });
    test('просьбы переименовать проходят: ru, ky, uz, en, tr', () {
      for (final q in ['переименуй в Кола Зеро', 'поменяй название на Кола Зеро', 'назови Кола Зеро', 'атын өзгөрт', "nomini o'zgartir", 'rename it', 'change the name', 'adını değiştir']) {
        expect(_check(Intents.rename, q), isNull, reason: q);
      }
    });
  });
}
