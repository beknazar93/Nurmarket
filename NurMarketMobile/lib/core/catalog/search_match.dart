// 2026-10-29 (аварийный аудит мобилки, п. 10): единый поиск по тексту — перенос логики кассы
// (NurMarketKassa.Core/Application/SearchMatch.cs, 1.17.64). Раньше в телефоне «елка» не находила «ёлка», «ASU» не находило
// «асу», а запрос в чужой раскладке или транслитом давал пустой список.
//  • normalize: нижний регистр, ё→е, кыргызские буквы (ү ө ң …) → ближайшие русские, пробелы схлопнуты, невидимые символы убраны;
//  • слова запроса — в любом порядке, каждое — подстрока ключа («негаз асу» находит «Асу негазированная 1л»);
//  • если по запросу как набран ничего нет — вторая попытка: «не та раскладка» (ASU ↔ ФЫГ) и транслит (Asu ↔ Асу).
// Ключ поиска товара (products.search_key) хранится уже нормализованным и считается один раз при записи в базу.

class SearchMatch {
  static const _enKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
  static const _ruKeys = 'йцукенгшщзхъфывапролджэячсмитьбюё';

  static final Map<String, String> _enToRu = _build(_enKeys, _ruKeys);
  static final Map<String, String> _ruToEn = _build(_ruKeys, _enKeys);

  static Map<String, String> _build(String from, String to) {
    final f = from.runes.map(String.fromCharCode).toList();
    final t = to.runes.map(String.fromCharCode).toList();
    return {for (var i = 0; i < f.length && i < t.length; i++) f[i]: t[i]};
  }

  /// Те же замены, что в SQL-миграции ключей (database.dart): ё→е, ү/ұ→у, ө→о, ң→н, қ→к, ғ→г, һ→х.
  static const _fold = {'ё': 'е', 'ң': 'н', 'ө': 'о', 'ү': 'у', 'ұ': 'у', 'қ': 'к', 'ғ': 'г', 'һ': 'х', 'ı': 'i', '’': "'", 'ʼ': "'", '`': "'"};

  static final RegExp _invisible = RegExp('[​‌‍﻿­]');
  static final RegExp _spaces = RegExp(r'\s+');

  /// Нижний регистр, ё→е, кыргызские буквы → русские, пробелы схлопнуты, края обрезаны.
  static String normalize(String? text) {
    if (text == null || text.isEmpty) return '';
    final lower = text.replaceAll(_invisible, '').toLowerCase();
    final sb = StringBuffer();
    for (final r in lower.runes) {
      final c = String.fromCharCode(r);
      sb.write(_fold[c] ?? c);
    }
    return sb.toString().replaceAll(_spaces, ' ').trim();
  }

  /// Ключ товара/строки для поиска: поля через пробел, нормализованные. Считать один раз и хранить.
  static String key(Iterable<String?> fields) => normalize(fields.where((f) => f != null && f.isNotEmpty).join(' '));

  static String swapLayout(String token, {required bool fromLatin}) {
    final map = fromLatin ? _enToRu : _ruToEn;
    final sb = StringBuffer();
    for (final r in token.runes) {
      final c = String.fromCharCode(r);
      sb.write(map[c] ?? c);
    }
    return sb.toString();
  }

  static const _latToCyrDigraphs = <(String, String)>[
    ('sch', 'щ'), ('shch', 'щ'), ('zh', 'ж'), ('kh', 'х'), ('ts', 'ц'), ('ch', 'ч'), ('sh', 'ш'),
    ('yu', 'ю'), ('ya', 'я'), ('yo', 'е'), ('ye', 'е'), ('ph', 'ф'),
  ];

  static const _latToCyr = {
    'a': 'а', 'b': 'б', 'c': 'к', 'd': 'д', 'e': 'е', 'f': 'ф', 'g': 'г', 'h': 'х', 'i': 'и', 'j': 'ж', 'k': 'к', 'l': 'л', 'm': 'м',
    'n': 'н', 'o': 'о', 'p': 'п', 'q': 'к', 'r': 'р', 's': 'с', 't': 'т', 'u': 'у', 'v': 'в', 'w': 'в', 'x': 'кс', 'y': 'ы', 'z': 'з',
  };

  static const _cyrToLat = {
    'а': 'a', 'б': 'b', 'в': 'v', 'г': 'g', 'д': 'd', 'е': 'e', 'ж': 'zh', 'з': 'z', 'и': 'i', 'й': 'y', 'к': 'k', 'л': 'l', 'м': 'm',
    'н': 'n', 'о': 'o', 'п': 'p', 'р': 'r', 'с': 's', 'т': 't', 'у': 'u', 'ф': 'f', 'х': 'h', 'ц': 'ts', 'ч': 'ch', 'ш': 'sh', 'щ': 'sch',
    'ъ': '', 'ы': 'y', 'ь': '', 'э': 'e', 'ю': 'yu', 'я': 'ya',
  };

  static String translitLatToCyr(String token, {bool cAsTs = false}) {
    final sb = StringBuffer();
    var i = 0;
    while (i < token.length) {
      var matched = false;
      for (final (lat, cyr) in _latToCyrDigraphs) {
        if (token.startsWith(lat, i)) {
          sb.write(cyr);
          i += lat.length;
          matched = true;
          break;
        }
      }
      if (matched) continue;
      final ch = token[i];
      sb.write(cAsTs && ch == 'c' ? 'ц' : (_latToCyr[ch] ?? ch));
      i++;
    }
    return sb.toString();
  }

  static String translitCyrToLat(String token, {bool vAsW = false}) {
    final sb = StringBuffer();
    for (final r in token.runes) {
      final c = String.fromCharCode(r);
      sb.write(vAsW && c == 'в' ? 'w' : (_cyrToLat[c] ?? c));
    }
    return sb.toString();
  }

  static bool hasLatin(String s) => s.codeUnits.any((c) => c >= 0x61 && c <= 0x7a);
  static bool hasCyrillic(String s) => s.codeUnits.any((c) => c >= 0x430 && c <= 0x44f);
}

/// Подготовленный запрос: слова нормализованы, варианты раскладки/транслита посчитаны один раз на запрос.
class SearchQuery {
  final String normalized;
  final List<String> tokens;
  final List<List<String>> variants;

  SearchQuery._(this.normalized, this.tokens, this.variants);

  factory SearchQuery(String? query) {
    final n = SearchMatch.normalize(query);
    final tokens = n.isEmpty
        ? <String>[]
        : [
            for (final t in n.split(' '))
              if (t.replaceAll(RegExp(r'^[,;:"«»()]+|[,;:"«»()]+$'), '').isNotEmpty) t.replaceAll(RegExp(r'^[,;:"«»()]+|[,;:"«»()]+$'), ''),
          ];
    final variants = <List<String>>[];
    for (final t in tokens) {
      final list = <String>[];
      if (SearchMatch.hasLatin(t)) {
        list.add(SearchMatch.swapLayout(t, fromLatin: true)); // asu -> фыг
        list.add(SearchMatch.translitLatToCyr(t)); // asu -> асу
        if (t.contains('c')) list.add(SearchMatch.translitLatToCyr(t, cAsTs: true)); // cezar -> цезар
      }
      if (SearchMatch.hasCyrillic(t)) {
        list.add(SearchMatch.swapLayout(t, fromLatin: false)); // фыг -> asu
        list.add(SearchMatch.translitCyrToLat(t)); // асу -> asu
        if (t.contains('в')) list.add(SearchMatch.translitCyrToLat(t, vAsW: true)); // воллингер -> wollinger
      }
      variants.add({for (final v in list) if (v.isNotEmpty && v != t) v}.toList());
    }
    return SearchQuery._(n, tokens, variants);
  }

  bool get isEmpty => tokens.isEmpty;

  /// Все слова запроса (в любом порядке) есть в ключе. Ключ уже нормализован ([SearchMatch.key]).
  bool matches(String normalizedKey) => tokens.every(normalizedKey.contains);

  /// То же, но каждое слово можно найти и в «другой раскладке» или транслитом.
  bool matchesFuzzy(String normalizedKey) {
    for (var i = 0; i < tokens.length; i++) {
      if (normalizedKey.contains(tokens[i])) continue;
      if (!variants[i].any(normalizedKey.contains)) return false;
    }
    return true;
  }
}
