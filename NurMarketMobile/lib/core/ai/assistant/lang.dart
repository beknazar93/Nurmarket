// 2026-10-26 (голос-управление, В2): язык реплики и числа, названные словами. detectLang выбирает язык ответа программы
// (раздел 3.2 ТЗ); spokenNumbers разбирает «сто двадцать», «бир жүз жыйырма», «bir yuz yigirma» для ворот намерения
// (IntentGate: число из аргумента ИИ должно быть в речи пользователя, раздел 3.6 ТЗ). Чистые функции, без сети.
import 'intent_gate.dart' show extractDigitNumbers, normSpeech;

/// Язык реплики: кириллица с ң/ө/ү — ky; латиница с o'/g'/q — uz; ı/ş/ğ (и ç/ö/ü без o'/g') — tr; иначе латиница — en;
/// иначе кириллица — ru. Нет букв — [fallback] (язык интерфейса).
String detectLang(String text, {String fallback = 'ru'}) {
  final t = text.toLowerCase();
  final cyr = RegExp('[а-яёәөүңіқғһ]').allMatches(t).length;
  final lat = RegExp('[a-zçğıöşüʻ]').allMatches(t).length;
  if (cyr == 0 && lat == 0) return fallback;
  if (cyr >= lat) {
    return RegExp('[ңөү]').hasMatch(t) ? 'ky' : 'ru';
  }
  if (RegExp('[ıİşğ]').hasMatch(t)) return 'tr';
  if (RegExp("[og][ʻ’`']").hasMatch(t) || RegExp(r'\bq[a-z]').hasMatch(t) || RegExp(r'(oʻ|gʻ)').hasMatch(t)) return 'uz';
  if (RegExp('[çöü]').hasMatch(t)) return 'tr';
  return 'en';
}

// ---------------------------------------------------------------------------------------------------------------------
// Числа словами. Слова из пяти языков в одном словаре: разные языки почти не пересекаются, а где пересекаются
// («бир» = 1 и в ky, и в uz/tr), значение одно.
// ---------------------------------------------------------------------------------------------------------------------

const _units = <String, int>{
  // ru
  'ноль': 0, 'ноля': 0, 'один': 1, 'одна': 1, 'одну': 1, 'одно': 1, 'два': 2, 'две': 2, 'три': 3, 'четыре': 4, 'пять': 5,
  'шесть': 6, 'семь': 7, 'восемь': 8, 'девять': 9, 'десять': 10, 'одиннадцать': 11, 'двенадцать': 12, 'тринадцать': 13,
  'четырнадцать': 14, 'пятнадцать': 15, 'шестнадцать': 16, 'семнадцать': 17, 'восемнадцать': 18, 'девятнадцать': 19,
  'двадцать': 20, 'тридцать': 30, 'сорок': 40, 'пятьдесят': 50, 'шестьдесят': 60, 'семьдесят': 70, 'восемьдесят': 80,
  'девяносто': 90, 'сто': 100, 'двести': 200, 'триста': 300, 'четыреста': 400, 'пятьсот': 500, 'шестьсот': 600,
  'семьсот': 700, 'восемьсот': 800, 'девятьсот': 900,
  // 2026-10-26 (В3а): косвенные падежи — «до двадцати процентов», «двух бутылок», «десяти тысяч», «ста»: без них ворота не
  // находили число в живой речи (Gemini чаще пишет цифры, но не всегда).
  'одного': 1, 'одной': 1, 'двух': 2, 'трех': 3, 'трёх': 3, 'четырех': 4, 'четырёх': 4, 'пяти': 5, 'шести': 6, 'семи': 7,
  'восьми': 8, 'девяти': 9, 'десяти': 10, 'одиннадцати': 11, 'двенадцати': 12, 'тринадцати': 13, 'четырнадцати': 14,
  'пятнадцати': 15, 'шестнадцати': 16, 'семнадцати': 17, 'восемнадцати': 18, 'девятнадцати': 19, 'двадцати': 20,
  'тридцати': 30, 'сорока': 40, 'пятидесяти': 50, 'шестидесяти': 60, 'семидесяти': 70, 'восьмидесяти': 80, 'девяноста': 90,
  'ста': 100, 'двухсот': 200, 'трехсот': 300, 'трёхсот': 300, 'четырехсот': 400, 'четырёхсот': 400, 'пятисот': 500,
  'шестисот': 600, 'семисот': 700, 'восьмисот': 800, 'девятисот': 900,
  // ky
  'нөл': 0, 'бир': 1, 'эки': 2, 'үч': 3, 'төрт': 4, 'беш': 5, 'алты': 6, 'жети': 7, 'сегиз': 8, 'тогуз': 9,
  'жыйырма': 20, 'отуз': 30, 'кырк': 40, 'элүү': 50, 'алтымыш': 60, 'жетмиш': 70, 'сексен': 80, 'токсон': 90,
  // uz (латиница)
  'nol': 0, 'ikki': 2, 'uch': 3, "to'rt": 4, 'tort': 4, 'besh': 5, 'olti': 6, 'yetti': 7, 'sakkiz': 8, "to'qqiz": 9,
  'toqqiz': 9, "o'n": 10, 'yigirma': 20, "o'ttiz": 30, 'ottiz': 30, 'qirq': 40, 'ellik': 50, 'oltmish': 60,
  'yetmish': 70, 'sakson': 80, "to'qson": 90, 'toqson': 90,
  // en
  'zero': 0, 'one': 1, 'two': 2, 'three': 3, 'four': 4, 'five': 5, 'six': 6, 'seven': 7, 'eight': 8, 'nine': 9, 'ten': 10,
  'eleven': 11, 'twelve': 12, 'thirteen': 13, 'fourteen': 14, 'fifteen': 15, 'sixteen': 16, 'seventeen': 17,
  'eighteen': 18, 'nineteen': 19, 'twenty': 20, 'thirty': 30, 'forty': 40, 'fifty': 50, 'sixty': 60, 'seventy': 70,
  'eighty': 80, 'ninety': 90,
  // tr
  'sıfır': 0, 'iki': 2, 'üç': 3, 'dört': 4, 'beş': 5, 'altı': 6, 'yedi': 7, 'sekiz': 8, 'dokuz': 9, 'yirmi': 20,
  'otuz': 30, 'kırk': 40, 'elli': 50, 'altmış': 60, 'yetmiş': 70, 'seksen': 80, 'doksan': 90,
};

// Сто как множитель после цифры: «бир жүз», «bir yuz», «one hundred», «iki yüz». Русское «сто/двести…» — в _units.
const _hundredMul = {'жүз', 'yuz', 'yüz', 'hundred'};
const _thousandMul = {'тысяча', 'тысячи', 'тысяч', 'тысячу', 'миң', 'ming', 'thousand', 'bin', 'тыс'};
const _millionMul = {'миллион', 'миллиона', 'миллионов', 'миллиондон', 'million', 'milyon'};

// «Полтора», «жарым», «yarim»: по ТЗ — 1,5 (в «бир жарым» половинка добавляется к единице).
const _halves = {'полтора', 'полторы', 'жарым', 'yarim', 'yarım', 'buçuk'};

/// Числа, названные в речи: цифрами («120», «1 250», «1,5») и словами (ru, ky, uz, en, tr) — «сто двадцать» = 120,
/// «две тысячи пятьсот» = 2500, «полтора» = 1,5. Пустое множество — чисел нет.
Set<double> spokenNumbers(String text) {
  final out = <double>{...extractDigitNumbers(text)};
  final words = normSpeech(text).split(RegExp(r"[^\p{L}\p{N}',.]+", unicode: true)).where((w) => w.isNotEmpty).toList();
  var total = 0.0;
  var current = 0.0;
  var active = false;
  var lastDigit = false;

  void flush() {
    if (active) out.add(total + current);
    total = 0;
    current = 0;
    active = false;
    lastDigit = false;
  }

  for (var raw in words) {
    final w = raw.replaceAll(RegExp(r'^[,.]+|[,.]+$'), '');
    if (w.isEmpty) {
      flush();
      continue;
    }
    final digit = double.tryParse(w.replaceAll(',', '.'));
    if (digit != null) {
      if (lastDigit) flush();
      current += digit;
      active = true;
      lastDigit = true;
      continue;
    }
    lastDigit = false;
    if (_halves.contains(w)) {
      if (active && current >= 1 && current == current.roundToDouble()) {
        current += .5;
      } else {
        flush();
        out.add(1.5);
      }
      continue;
    }
    if (_hundredMul.contains(w)) {
      current = (current == 0 ? 1 : current) * 100;
      active = true;
      continue;
    }
    if (_thousandMul.contains(w)) {
      // «тысяча» без числа — 1000; «две тысячи» — 2000; «сто двадцать тысяч» — 120 000.
      total += (current == 0 ? 1 : current) * 1000;
      current = 0;
      active = true;
      continue;
    }
    if (_millionMul.contains(w)) {
      total += (current == 0 ? 1 : current) * 1000000;
      current = 0;
      active = true;
      continue;
    }
    final u = _units[w];
    if (u != null) {
      current += u;
      active = true;
      continue;
    }
    flush();
  }
  flush();
  return out;
}
