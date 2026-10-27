/// 2026-10-27 (владелец: «на фото существующий товар не находит», звонок «Асу» → ничего, «Приёмка» не знает свои товары):
/// ОДИН модуль сопоставления «как назвали» → товар каталога магазина. Чистый Dart (без UI и базы): умная камера, накладная,
/// поиск в видеозвонке и «Приёмка» зовут его вместо своих правил. Расследование: docs/Отчеты/Расследование мобилка — умная
/// камера, видеозвонок, удаление 2026-10-27.md, раздел 1.3.
///
/// Что делает:
///  1. объём/вес/фасовка отдельно от слов («1л», «1 л», «1000 мл», «литр», «0,5», «полтора»);
///  2. одна азбука для обеих сторон: латиница ↔ кириллица («Asu» = «Асу», «Adygene» = «Адыгене»), «скелет» слова
///     (й/и, ы/и, э/е, двойные буквы), сокращения («н/г» = негазированная), небольшой словарь синонимов (water = вода);
///  3. вес слова по каталогу магазина (IDF): бренд и вкус весят больше, «вода», «сок» — меньше;
///  4. поиск по ВСЕМУ каталогу в памяти (а не по первым 5 буквам и не 40 штук по алфавиту), топ-N с оценкой 0..1;
///  5. автовыбор — только если лучший ≥ [autoScore] и отрыв от второго ≥ [autoGap] ([ProductMatcher.confident]);
///     иначе решает человек (кнопки «Это …?»).
library;

import 'dart:math' as math;

import '../../features/scan/barcode_lookup.dart';

/// Минимальная оценка, с которой товар выбирается без вопроса.
const double autoScore = 0.85;

/// Минимальный отрыв лучшего от второго для автовыбора.
const double autoGap = 0.15;

/// Товар каталога для сопоставления. Строится из карточки мобилки адаптером ([matchProductOf] в catalog_matcher.dart).
class MatchProduct {
  final String id;
  final String name;
  final String barcode;
  final List<String> altBarcodes;
  final String sku;
  final String brand;
  final String category;

  /// Как этот товар называли ИИ и люди («обучение»): совпадение с алиасом — оценка 1,0.
  final List<String> aliases;
  const MatchProduct({
    required this.id,
    required this.name,
    this.barcode = '',
    this.altBarcodes = const [],
    this.sku = '',
    this.brand = '',
    this.category = '',
    this.aliases = const [],
  });
  MatchProduct withAliases(List<String> a) => MatchProduct(
    id: id,
    name: name,
    barcode: barcode,
    altBarcodes: altBarcodes,
    sku: sku,
    brand: brand,
    category: category,
    aliases: a,
  );
  @override
  String toString() => 'MatchProduct($id, $name)';
}

/// Один кандидат: товар, оценка 0..1 и почему (barcode / alias / name / volume / volume_mismatch).
class MatchCandidate {
  final MatchProduct product;
  final double score;
  final List<String> reasons;
  const MatchCandidate(this.product, this.score, this.reasons);
  @override
  String toString() => '${product.name} ${score.toStringAsFixed(2)} $reasons';
}

/// Объём или вес, приведённые к мл или г: kind 'v' — объём, 'm' — масса.
class MatchVolume {
  final double amount;
  final String kind;
  const MatchVolume(this.amount, this.kind);
  bool sameKind(MatchVolume o) => kind == o.kind;
  bool close(MatchVolume o) => (amount - o.amount).abs() <= math.max(amount, o.amount) * 0.03;
  @override
  String toString() => '$amount$kind';
}

/// Нормализованный текст: слова (в одной азбуке) и объём.
class NormalizedText {
  final List<String> tokens;
  final MatchVolume? volume;
  const NormalizedText(this.tokens, this.volume);
}

// ---------------------------------------------------------------------------------------------------------------------
// Азбука
// ---------------------------------------------------------------------------------------------------------------------

const _vowelsLat = 'aeiouy';

/// Латиница → кириллица по звучанию (грубо — для сравнения, не для показа). Двухбуквенные сочетания — первыми;
/// «y» после согласной — «ы» (Adygene → адыгене), в начале слова и после гласной — «й» (yogurt → йогурт); «c» перед e/i/y — «с».
String latinToCyr(String s) {
  const two = {
    'sh': 'ш', 'ch': 'ч', 'zh': 'ж', 'kh': 'х', 'ts': 'ц', 'ya': 'я', 'yu': 'ю', 'yo': 'е', 'ck': 'к', 'ph': 'ф', 'ee': 'и', 'oo': 'у', 'th': 'т',
    'qu': 'кв',
  };
  const one = {
    'a': 'а', 'b': 'б', 'd': 'д', 'e': 'е', 'f': 'ф', 'g': 'г', 'h': 'х', 'i': 'и', 'j': 'дж', 'k': 'к', 'l': 'л', 'm': 'м',
    'n': 'н', 'o': 'о', 'p': 'п', 'q': 'к', 'r': 'р', 's': 'с', 't': 'т', 'u': 'у', 'v': 'в', 'w': 'в', 'x': 'кс', 'z': 'з',
  };
  final out = StringBuffer();
  var i = 0;
  while (i < s.length) {
    final ch = s[i];
    if (!RegExp('[a-z]').hasMatch(ch)) {
      out.write(ch);
      i++;
      continue;
    }
    final pair = i + 1 < s.length ? s.substring(i, i + 2) : '';
    if (pair.length == 2 && two.containsKey(pair)) {
      out.write(two[pair]);
      i += 2;
      continue;
    }
    if (ch == 'y') {
      final prev = i > 0 ? s[i - 1] : '';
      out.write(i == 0 || _vowelsLat.contains(prev) || !RegExp('[a-z]').hasMatch(prev) ? 'й' : 'ы');
    } else if (ch == 'c') {
      final next = i + 1 < s.length ? s[i + 1] : '';
      out.write('eiy'.contains(next) && next.isNotEmpty ? 'с' : 'к');
    } else {
      out.write(one[ch] ?? ch);
    }
    i++;
  }
  return out.toString();
}

/// «Скелет» слова: латиница → кириллица, ё→е, й/ы→и, э→е, щ→ш, ь и ъ убраны, двойные буквы — одна.
String skeleton(String word) {
  var t = latinToCyr(word.toLowerCase().replaceAll('ё', 'е'));
  t = t.replaceAll('й', 'и').replaceAll('ы', 'и').replaceAll('э', 'е').replaceAll('щ', 'ш').replaceAll(RegExp('[ьъ]'), '');
  return t.replaceAllMapped(RegExp(r'([^\d])\1+'), (m) => m[1]!);
}

// ---------------------------------------------------------------------------------------------------------------------
// Синонимы (ключи — слова как написаны; приводятся к «скелету» один раз)
// ---------------------------------------------------------------------------------------------------------------------

const _synRaw = <String, String>{
  // вода и вкусы: ИИ отвечает на английском/русском, карточки — как придумал магазин
  'water': 'вода', 'suv': 'вода', 'суу': 'вода', 'su': 'вода',
  'still': 'негаз', 'flat': 'негаз', 'gazsiz': 'негаз', 'газсыз': 'негаз',
  'sparkling': 'газ', 'carbonated': 'газ', 'gazli': 'газ', 'газдуу': 'газ',
  'mineral': 'минерал',
  'orange': 'апельс', 'apelsin': 'апельс', 'апельсин': 'апельс',
  'strawberry': 'клуб', 'клубника': 'клуб', 'qulupnay': 'клуб', 'cilek': 'клуб', 'çilek': 'клуб',
  'apple': 'яблок', 'яблоко': 'яблок', 'olma': 'яблок', 'elma': 'яблок', 'алма': 'яблок',
  'cherry': 'вишн', 'вишня': 'вишн',
  'peach': 'персик', 'shaftoli': 'персик', 'seftali': 'персик',
  'lemon': 'лимон', 'limon': 'лимон',
  'grape': 'виноград', 'grapes': 'виноград',
  'banana': 'банан', 'mango': 'манго', 'pineapple': 'ананас', 'raspberry': 'малин', 'малина': 'малин', 'blueberry': 'черник',
  'juice': 'сок', 'sok': 'сок', 'шире': 'сок',
  'milk': 'молок', 'молоко': 'молок', 'sut': 'молок', 'süt': 'молок', 'сут': 'молок',
  'tea': 'чай', 'chay': 'чай', 'cay': 'чай', 'çay': 'чай',
  'coffee': 'кофе', 'kahve': 'кофе', 'qahva': 'кофе',
  'chocolate': 'шоколад', 'choco': 'шоколад', 'shokolad': 'шоколад',
  'sugar': 'сахар', 'шекер': 'сахар', 'shakar': 'сахар',
  'bread': 'хлеб', 'нан': 'хлеб',
  'cigarettes': 'сигарет', 'cigarette': 'сигарет', 'sigara': 'сигарет',
  // бренды, которые транслит не переводит
  'sprite': 'спрайт', 'schweppes': 'швепс', 'lays': 'лейс', "lay's": 'лейс', 'pringles': 'принглс', 'nescafe': 'нескафе',
  'bonaqua': 'бонаква', 'adrenaline': 'адреналин', 'redbull': 'редбул',
};

// Слова, которые ничего не значат для выбора товара.
const _stop = <String>{
  'со', 'с', 'и', 'в', 'на', 'из', 'для', 'по', 'the', 'and', 'with', 'of', 'a', 'вкусом', 'вкус', 'taste', 'flavor', 'flavour', 'flavored',
  'питьевая', 'питьевой', 'drinking', 'natural', 'new', 'новый', 'новинка', 'шт', 'pcs', 'pack', 'упаковка', 'уп',
};

/// Словоформы к одной основе: негазированная → негаз, газированная → газ, апельсиновый → апельс и т. д.
const _stems = <String, String>{
  'негаз': 'негаз', 'газир': 'газ', 'апельсин': 'апельс', 'клубни': 'клуб', 'яблоч': 'яблок', 'яблок': 'яблок', 'молоч': 'молок', 'молок': 'молок',
  'шоколад': 'шоколад', 'минерал': 'минерал', 'сигарет': 'сигарет',
};

final Map<String, String> _syn = {for (final e in _synRaw.entries) skeleton(e.key): skeleton(e.value)};
final List<(String, String)> _stemList = [for (final e in _stems.entries) (skeleton(e.key), skeleton(e.value))];
final Set<String> _stopSk = {for (final w in _stop) skeleton(w)};

String _canon(String sk) {
  final s = _syn[sk];
  if (s != null) return s;
  for (final (prefix, to) in _stemList) {
    if (sk.startsWith(prefix)) return to;
  }
  return sk;
}

// ---------------------------------------------------------------------------------------------------------------------
// Объём
// ---------------------------------------------------------------------------------------------------------------------

final _volRe = RegExp(
  r'(?<![\d.,])(\d+(?:[.,]\d+)?)\s*(мл|ml|литр\w*|liter\w*|litre\w*|litr\w*|ltr|кг|kg|грамм\w*|гр|г|gr|g|л|l)(?![a-zа-яё])',
  caseSensitive: false,
);
final _halfRe = RegExp(r'пол\s*-?\s*литр\w*|полулитр\w*|pol\s*-?\s*litr\w*', caseSensitive: false);
final _oneHalfRe = RegExp(r'полтора|полторы|polto?ra', caseSensitive: false);
final _literWordRe = RegExp(r'(?<![a-zа-яё])(литр\w*|liter|litre|litr)(?![a-zа-яё])', caseSensitive: false);
final _bareDecimalRe = RegExp(r'(?<![\d.,])(0[.,]\d+|[1-3][.,]\d+)(?![\d.,])');

/// Объём из строки и сама строка без объёма. «1л», «1 л», «1000 мл», «литр», «полтора литра», «0,5» (голая дробь ≤ 3 — литры).
({MatchVolume? volume, String rest}) extractVolume(String raw) {
  var s = raw.toLowerCase().replaceAll('ё', 'е');
  MatchVolume? v;
  final m = _volRe.firstMatch(s);
  if (m != null) {
    final n = double.tryParse(m.group(1)!.replaceAll(',', '.'));
    if (n != null) {
      final u = m.group(2)!;
      v = switch (u) {
        'мл' || 'ml' => MatchVolume(n, 'v'),
        'кг' || 'kg' => MatchVolume(n * 1000, 'm'),
        'гр' || 'г' || 'gr' || 'g' => MatchVolume(n, 'm'),
        _ when u.startsWith('грамм') => MatchVolume(n, 'm'),
        _ => MatchVolume(n * 1000, 'v'),
      };
      s = s.replaceRange(m.start, m.end, ' ');
    }
  }
  if (v == null) {
    final h = _halfRe.firstMatch(s);
    final o = _oneHalfRe.firstMatch(s);
    if (h != null) {
      v = const MatchVolume(500, 'v');
      s = s.replaceRange(h.start, h.end, ' ');
    } else if (o != null) {
      v = const MatchVolume(1500, 'v');
      s = s.replaceRange(o.start, o.end, ' ');
      s = s.replaceAll(_literWordRe, ' ');
    }
  }
  if (v == null) {
    final d = _bareDecimalRe.firstMatch(s);
    if (d != null) {
      v = MatchVolume(double.parse(d.group(1)!.replaceAll(',', '.')) * 1000, 'v');
      s = s.replaceRange(d.start, d.end, ' ');
    }
  }
  if (v == null) {
    final w = _literWordRe.firstMatch(s);
    if (w != null) {
      v = const MatchVolume(1000, 'v');
      s = s.replaceRange(w.start, w.end, ' ');
    }
  }
  return (volume: v, rest: s);
}

/// Строка → слова в одной азбуке + объём.
NormalizedText normalizeText(String raw) {
  var s = raw.toLowerCase().replaceAll('ё', 'е');
  // сокращения до разбиения по «/» и «.»
  s = s
      .replaceAll(RegExp(r'н\s*/\s*газ\w*\.?|н\s*/\s*г(?![а-я])|б\s*/\s*газ\w*\.?|б\s*/\s*г(?![а-я])'), ' негаз ')
      .replaceAll(RegExp(r'(?<![а-я])газ\.'), ' газ ')
      .replaceAll(RegExp(r'ж\s*/\s*б'), ' жб ')
      .replaceAll(RegExp(r'ст\s*/\s*б'), ' стб ');
  final ex = extractVolume(s);
  final out = <String>[];
  for (final w in RegExp(r"[a-zа-я0-9çüöşğıə]+").allMatches(ex.rest).map((m) => m[0]!)) {
    if (RegExp(r'^\d{9,}$').hasMatch(w)) continue; // штрихкод внутри названия
    var sk = skeleton(w);
    if (sk.isEmpty) continue;
    if (sk.length < 2 && !RegExp(r'^\d$').hasMatch(sk)) continue;
    if (_stopSk.contains(sk)) continue;
    sk = _canon(sk);
    out.add(sk);
  }
  return NormalizedText(out, ex.volume);
}

// ---------------------------------------------------------------------------------------------------------------------
// Сравнение слов
// ---------------------------------------------------------------------------------------------------------------------

int _lev(String a, String b) {
  var prev = List<int>.generate(b.length + 1, (j) => j);
  for (var i = 1; i <= a.length; i++) {
    final cur = List<int>.filled(b.length + 1, 0);
    cur[0] = i;
    for (var j = 1; j <= b.length; j++) {
      cur[j] = math.min(math.min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
    }
    prev = cur;
  }
  return prev[b.length];
}

/// Слова равны: точно; короткое — начало длинного («клуб» ⊂ «клубника»; не короче 3 букв, не короче половины длинного);
/// длинные (≥ 7 букв) — отличие в одной букве (опечатка ИИ, неточный транслит). Короткие слова нечётко НЕ сравниваются.
bool tokenEq(String a, String b) {
  if (a == b) return true;
  final (s, l) = a.length <= b.length ? (a, b) : (b, a);
  if (RegExp(r'^\d+$').hasMatch(s) || RegExp(r'^\d+$').hasMatch(l)) return false;
  if (s.length >= 3 && l.startsWith(s) && (s.length >= 4 ? s.length * 2 >= l.length : s.length * 2 > l.length)) return true;
  if (s.length >= 7 && (l.length - s.length) <= 1 && _lev(s, l) <= 1) return true;
  if (s.length >= 7 && l.length > s.length && _lev(s, l.substring(0, s.length)) <= 1 && s.length * 2 >= l.length) return true;
  return false;
}

/// Склейка двух соседних слов равна слову другой стороны: только точно или в одной букве (без «начала слова»).
bool _pairEq(String pair, String t) => t.length >= 5 && (pair == t || (t.length >= 7 && (pair.length - t.length).abs() <= 1 && _lev(pair, t) <= 1));

/// Две цифры штрихкода равны с учётом UPC-A ↔ EAN-13 и т. п.
bool _sameCode(String a, String b) {
  if (a.isEmpty || b.isEmpty) return false;
  if (a == b) return true;
  final va = barcodeVariants(a);
  return va.contains(b) || barcodeVariants(b).contains(a);
}

// ---------------------------------------------------------------------------------------------------------------------
// Матчер
// ---------------------------------------------------------------------------------------------------------------------

class _Row {
  final MatchProduct p;
  final NormalizedText name;
  final List<NormalizedText> aliases;
  final Set<String> codes;
  _Row(this.p, this.name, this.aliases, this.codes);
}

class ProductMatcher {
  final List<MatchProduct> catalog;
  late final List<_Row> _rows;
  final Map<String, double> _idf = {};
  // слово каталога → строки, где оно есть (для быстрого отбора кандидатов без обхода всего каталога)
  final Map<String, List<int>> _byToken = {};
  final Map<String, List<int>> _byCode = {};
  late final double _unknownWeight;

  ProductMatcher(this.catalog) {
    _rows = [
      for (final p in catalog)
        _Row(
          p,
          normalizeText('${p.name} ${p.brand}'.trim()),
          [for (final a in p.aliases) normalizeText(a)],
          {
            for (final c in [p.barcode, p.sku, ...p.altBarcodes])
              if (c.trim().isNotEmpty) c.trim(),
          },
        ),
    ];
    final df = <String, int>{};
    for (var i = 0; i < _rows.length; i++) {
      final r = _rows[i];
      for (final t in {...r.name.tokens, for (final a in r.aliases) ...a.tokens}) {
        if (r.name.tokens.contains(t)) df[t] = (df[t] ?? 0) + 1;
        (_byToken[t] ??= []).add(i);
      }
      for (final c in r.codes) {
        (_byCode[c] ??= []).add(i);
      }
    }
    final n = math.max(_rows.length, 2);
    for (final e in df.entries) {
      _idf[e.key] = 1 + math.log(n / e.value);
    }
    _unknownWeight = 1.0;
  }

  // слова-пояснения: ИИ добавляет их к любому ответу («вода минеральная»), на выбор товара они почти не влияют
  static final Set<String> _generic = {for (final w in const ['вода', 'water', 'минерал', 'напиток', 'сигарет', 'товар', 'продукт']) _canon(skeleton(w))};

  double _w(String t) => _generic.contains(t) ? 0.5 : (_idf[t] ?? _unknownWeight);

  /// Лучшие [top] кандидатов по [query] (+ [brand] и [volume], если ИИ отдал их отдельно). [barcode] — точное совпадение.
  /// Оценка 0..1; слабее [minScore] не возвращаются. Сортировка: оценка, потом название (стабильно).
  /// [strict] — «вся карточка», а не «всё сказанное»: короткий запрос («Асу») не получает высокой оценки у длинной карточки
  /// («ASU Негаз 1 л клуб-киви»); нужен для привязки штрихкода, где путать вкусы нельзя.
  /// [requireVolume] (2026-10-27, ревью М-2: камера/накладная по фото — деньги и остаток уходят выбранному товару): если в
  /// запросе нет объёма/веса, а у товара он есть в названии, оценка не выше [autoScore] − 0,01 (причина `volume_missing`) —
  /// автовыбора нет, человек выбирает из вариантов.
  List<MatchCandidate> match(String query, {String? brand, String? volume, String? barcode, int top = 5, double minScore = 0.3, bool strict = false, bool requireVolume = false}) {
    final found = <int, MatchCandidate>{};
    final code = (barcode ?? '').replaceAll(RegExp(r'\D'), '');
    if (code.length >= 6) {
      for (final e in _byCode.entries) {
        if (!_sameCode(e.key, code)) continue;
        for (final i in e.value) {
          found[i] = MatchCandidate(_rows[i].p, 1.0, const ['barcode']);
        }
      }
    }
    final text = [if (brand != null && brand.trim().isNotEmpty) brand.trim(), query.trim(), if (volume != null && volume.trim().isNotEmpty) volume.trim()].join(' ');
    final q = normalizeText(text);
    if (q.tokens.isNotEmpty) {
      // кандидаты: строки, где есть слово, равное слову запроса (или склейка двух соседних слов)
      final qAll = _withPairs(q.tokens);
      final cand = <int>{};
      for (final e in _byToken.entries) {
        if (qAll.any((t) => tokenEq(t, e.key))) cand.addAll(e.value);
      }
      for (final i in cand) {
        if (found.containsKey(i)) continue;
        final r = _rows[i];
        var best = _score(q, r.name, strict: strict);
        var reasons = best.$2;
        var s = best.$1;
        for (final a0 in r.aliases) {
          // 2026-10-27 (ревью М-2): выученное название без объёма («coca-cola», запомнено для «1 л») берёт объём товара —
          // иначе оно подходило бы «0,5 л» с оценкой 1,0 и уводило к товару другого объёма
          final a = a0.volume != null || r.name.volume == null ? a0 : NormalizedText(a0.tokens, r.name.volume);
          final as = _score(q, a, strict: strict);
          if (as.$1 > s) {
            s = as.$1;
            reasons = as.$2;
          }
          if (_sameText(q, a)) {
            s = 1.0;
            final qv = q.volume, av = a.volume;
            reasons = qv != null && av != null ? const ['alias', 'volume'] : const ['alias'];
          }
        }
        // 2026-10-27 (ревью М-2): в запросе объёма нет, у товара есть — для камеры только вариант, не автовыбор
        if (requireVolume && q.volume == null && r.name.volume != null && s >= autoScore) {
          s = autoScore - 0.01;
          reasons = [...reasons.where((x) => x != 'alias'), 'volume_missing'];
        }
        if (s >= minScore) found[i] = MatchCandidate(r.p, s, reasons);
      }
    }
    // точный штрихкод или выученное название (алиас) важнее похожих названий: остальные не выше 0,8 — не «ничья», а выбор
    final sure = found.values.where((c) => c.reasons.contains('barcode') || c.reasons.contains('alias')).toList();
    if (sure.isNotEmpty) {
      final sureVerified = sure.any((c) => c.reasons.contains('barcode') || c.reasons.contains('volume'));
      for (final e in found.entries.toList()) {
        final c = e.value;
        // 2026-10-27 (ревью М-2): кандидата с совпавшим объёмом не срезаем — он не хуже «выученного» с другим/чужим объёмом
        if (!sure.contains(c) && c.score > 0.8 && !(c.reasons.contains('volume') && !sureVerified)) found[e.key] = MatchCandidate(c.product, 0.8, c.reasons);
      }
    }
    final list = found.values.toList()
      ..sort((a, b) {
        final c = b.score.compareTo(a.score);
        return c != 0 ? c : a.product.name.compareTo(b.product.name);
      });
    return list.take(top).toList();
  }

  /// Автовыбор: лучший ≥ [autoScore] и отрыв от второго ≥ [autoGap]; иначе null — выбирает человек.
  static MatchCandidate? confident(List<MatchCandidate> list) {
    if (list.isEmpty) return null;
    final best = list.first;
    if (best.score < autoScore) return null;
    if (list.length > 1 && best.score - list[1].score < autoGap) return null;
    return best;
  }

  static List<String> _withPairs(List<String> t) => [...t, for (var i = 0; i + 1 < t.length; i++) t[i] + t[i + 1]];

  bool _sameText(NormalizedText a, NormalizedText b) {
    if (a.tokens.isEmpty || a.tokens.length != b.tokens.length) return false;
    final x = [...a.tokens]..sort(), y = [...b.tokens]..sort();
    for (var i = 0; i < x.length; i++) {
      if (x[i] != y[i]) return false;
    }
    final va = a.volume, vb = b.volume;
    return va == null || vb == null || (va.sameKind(vb) && va.close(vb));
  }

  /// Оценка пары «запрос — название».
  (double, List<String>) _score(NormalizedText q, NormalizedText n, {bool strict = false}) {
    if (q.tokens.isEmpty || n.tokens.isEmpty) return (0, const []);
    final qHit = List<bool>.filled(q.tokens.length, false);
    final nHit = List<bool>.filled(n.tokens.length, false);
    for (var i = 0; i < n.tokens.length; i++) {
      for (var j = 0; j < q.tokens.length; j++) {
        if (tokenEq(q.tokens[j], n.tokens[i])) {
          nHit[i] = true;
          qHit[j] = true;
        }
      }
    }
    // «кока-кола» ↔ «кокакола»: склейка двух соседних слов одной стороны равна слову другой
    for (var j = 0; j + 1 < q.tokens.length; j++) {
      final pair = q.tokens[j] + q.tokens[j + 1];
      for (var i = 0; i < n.tokens.length; i++) {
        if (_pairEq(pair, n.tokens[i])) {
          nHit[i] = true;
          qHit[j] = true;
          qHit[j + 1] = true;
        }
      }
    }
    for (var i = 0; i + 1 < n.tokens.length; i++) {
      final pair = n.tokens[i] + n.tokens[i + 1];
      for (var j = 0; j < q.tokens.length; j++) {
        if (_pairEq(pair, q.tokens[j])) {
          qHit[j] = true;
          nHit[i] = true;
          nHit[i + 1] = true;
        }
      }
    }
    double sum(List<String> t, List<bool> hit, bool want) {
      var s = 0.0;
      for (var i = 0; i < t.length; i++) {
        if (hit[i] == want) s += _w(t[i]);
      }
      return s;
    }

    final nAll = sum(n.tokens, nHit, true) + sum(n.tokens, nHit, false);
    final qAll = sum(q.tokens, qHit, true) + sum(q.tokens, qHit, false);
    final covP = nAll == 0 ? 0.0 : sum(n.tokens, nHit, true) / nAll;
    final covQ = qAll == 0 ? 0.0 : sum(q.tokens, qHit, true) / qAll;
    if (covP == 0) return (0, const []);
    // карточка покрыта названием — главное (0,65); название покрыто карточкой — 0,35 (ИИ многословен, лишние слова почти не мешают)
    var s = covP * 0.65 + covQ * 0.35;
    // всё сказанное нашлось в карточке («Асу», «Winston», «Fanta Orange 0,5 л»): это ответ, а не догадка
    if (!strict && covQ >= 0.999) s = math.max(s, 0.8 + 0.2 * covP);
    final reasons = <String>['name'];
    final a = q.volume, b = n.volume;
    if (a != null && b != null && a.sameKind(b)) {
      if (a.close(b)) {
        s = math.min(1.0, s + 0.1);
        reasons.add('volume');
      } else {
        s = math.min(s, 0.4);
        reasons.add('volume_mismatch');
      }
    } else if (a != null && b == null) {
      // спросили «Sprite 1 л», а в карточке объёма нет: годится, но хуже карточки с тем же объёмом
      s = math.max(0, s - 0.08);
    }
    return (math.min(s, 1.0), reasons);
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Контрольная цифра штрихкода
// ---------------------------------------------------------------------------------------------------------------------

/// Контрольная цифра EAN-8 / UPC-A / EAN-13 верна.
bool isValidEan(String c) {
  if (!RegExp(r'^(\d{8}|\d{12}|\d{13})$').hasMatch(c)) return false;
  return c.codeUnitAt(c.length - 1) - 48 == eanCheckDigit(c.substring(0, c.length - 1));
}

/// Контрольная цифра для кода без неё (7, 11 или 12 цифр).
int eanCheckDigit(String body) {
  var sum = 0;
  for (var i = 0; i < body.length; i++) {
    final d = body.codeUnitAt(body.length - 1 - i) - 48;
    sum += i.isEven ? d * 3 : d;
  }
  return (10 - sum % 10) % 10;
}

/// [stored] — код, записанный в карточке товара с ошибкой в контрольной цифре, а [scanned] — верный код с той же основой
/// («отличается последней цифрой»). Тогда карточке нужно исправить последнюю цифру.
bool isCheckDigitTypo(String stored, String scanned) {
  if (stored.length != scanned.length || stored.length < 8 || stored == scanned) return false;
  if (!RegExp(r'^\d+$').hasMatch(stored) || !isValidEan(scanned) || isValidEan(stored)) return false;
  return stored.substring(0, stored.length - 1) == scanned.substring(0, scanned.length - 1);
}
