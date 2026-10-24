/// 13.10: разбор штрихкода со сканера — как в кассе (WeightBarcodeParser, LocalCartService.FindByEmbeddedCode).
/// Камера читает один и тот же товар по-разному: UPC-A — 12 цифр, а в базе он записан EAN-13 с ведущим нулём (и наоборот);
/// весовая этикетка с весов (2X…) несёт код товара и вес — её штрихкода нет ни у одного товара.
/// 2026-10-22 (аудит П7): как эталон ПК ProductCodeLookup.cs — очистка кода, GTIN-14, UPC-E, русская раскладка
/// ручного сканера, правила весовых этикеток компании (раскладка, режим, префиксы «сумма» и «вес»).
library;

import 'dart:convert';

/// Код со сканера без мусора: управляющие символы (GS у кодов GS1, перевод строки ручного сканера), пробелы по краям,
/// пробелы внутри цифрового кода («4870 2025 21261» — так вставляют из накладной). Как ProductCodeLookup.Normalize.
String normalizeCode(String? raw) {
  if (raw == null) return '';
  final cleaned = raw.replaceAll(RegExp(r'[\x00-\x1F\x7F]'), '').trim();
  final noSpaces = cleaned.replaceAll(' ', '');
  return noSpaces.isNotEmpty && RegExp(r'^\d+$').hasMatch(noSpaces) ? noSpaces : cleaned;
}

/// UPC-A 12 ↔ EAN-13 с нулём впереди.
String? _alternate(String c) {
  if (RegExp(r'^\d{12}$').hasMatch(c)) return '0$c';
  if (RegExp(r'^0\d{12}$').hasMatch(c)) return c.substring(1);
  return null;
}

/// UPC-E (8 цифр, система 0 или 1) → UPC-A (12 цифр): камера отдаёт мелкую упаковку восемью цифрами,
/// а в базе код записан полностью.
String? upcEToUpcA(String c) {
  if (!RegExp(r'^[01]\d{7}$').hasMatch(c)) return null;
  final s = c[0], d = c.substring(1, 7), check = c[7];
  final body = switch (d[5]) {
    '0' || '1' || '2' => '${d.substring(0, 2)}${d[5]}0000${d.substring(2, 5)}',
    '3' => '${d.substring(0, 3)}00000${d.substring(3, 5)}',
    '4' => '${d.substring(0, 4)}00000${d[4]}',
    _ => '${d.substring(0, 5)}0000${d[5]}',
  };
  return '$s$body$check';
}

const _ru = 'йцукенгшщзфывапролдячсмить';
const _en = 'qwertyuiopasdfghjklzxcvbnm';

/// Код, набранный ручным сканером-клавиатурой в русской раскладке («ФИ123» вместо «AB123»); null — русских букв нет.
String? swapRussianLayout(String code) {
  if (!code.toLowerCase().split('').any(_ru.contains)) return null;
  return code.split('').map((ch) {
    final i = _ru.indexOf(ch.toLowerCase());
    if (i < 0) return ch;
    return ch == ch.toLowerCase() ? _en[i] : _en[i].toUpperCase();
  }).join();
}

/// Все написания одного штрихкода: как есть, UPC-A ↔ EAN-13, GTIN-14 с нулём → EAN-13, UPC-E → UPC-A, латиница
/// вместо русской раскладки. Порядок важен: сначала как пришёл.
List<String> barcodeVariants(String code) {
  final c = normalizeCode(code);
  final v = <String>[];
  void add(String? x) {
    if (x != null && x.isNotEmpty && !v.contains(x)) v.add(x);
  }

  add(c);
  add(_alternate(c));
  if (RegExp(r'^0\d{13}$').hasMatch(c)) {
    add(c.substring(1));
    add(_alternate(c.substring(1)));
  }
  final upcA = upcEToUpcA(c);
  if (upcA != null) {
    add(upcA);
    add('0$upcA');
  }
  final latin = swapRussianLayout(c);
  if (latin != null) {
    add(latin);
    add(_alternate(latin));
  }
  return v;
}

/// 2026-10-22 (аудит П7): правила весовых этикеток компании, как WeightBarcodeParser кассы. Раскладка и режим — из
/// настроек компании NurCRM (api/users/settings/company/: scale_barcode_layout, scale_barcode_mode,
/// scale_barcode_amount_unit); префиксы «сумма» и «вес» — общая настройка телефонов компании (ScaleLabelPrefixes в
/// api/main/app-settings/nurmarket-mobile/). Пример владельца: весы Rongta с префиксом 21 печатают СУММУ — без правила
/// телефон читал 40,00 сом как 0,400 кг.
class ScaleRules {
  /// plu — PLU 5 цифр, code — код 6 цифр (Rongta), пусто — пробовать обе.
  final String layout;

  /// auto (25 — сумма, остальные — вес), weight, amount.
  final String mode;

  /// tiyin — сумма × 100, som — сумма как есть.
  final String amountUnit;
  final Set<String> amountPrefixes;
  final Set<String> weightPrefixes;

  const ScaleRules({this.layout = '', this.mode = 'auto', this.amountUnit = 'tiyin', this.amountPrefixes = const {}, this.weightPrefixes = const {}});

  /// Правила, загруженные при входе (ScaleRulesLoader); по умолчанию — как касса без настроек.
  static ScaleRules current = const ScaleRules();

  /// Префикс записан явно (в «Одежде» весовая этикетка — только с таким префиксом).
  bool explicit(String prefix) => amountPrefixes.contains(prefix) || weightPrefixes.contains(prefix);

  bool isAmount(String prefix) {
    if (amountPrefixes.contains(prefix)) return true;
    if (weightPrefixes.contains(prefix)) return false;
    if (mode == 'weight') return false;
    if (mode == 'amount') return true;
    return prefix == '25';
  }

  ScaleRules copyWith({String? layout, String? mode, String? amountUnit, Set<String>? amountPrefixes, Set<String>? weightPrefixes}) => ScaleRules(
    layout: layout ?? this.layout,
    mode: mode ?? this.mode,
    amountUnit: amountUnit ?? this.amountUnit,
    amountPrefixes: amountPrefixes ?? this.amountPrefixes,
    weightPrefixes: weightPrefixes ?? this.weightPrefixes,
  );

  Map<String, dynamic> toJson() => {
    'layout': layout,
    'mode': mode,
    'amountUnit': amountUnit,
    'amount': amountPrefixes.toList()..sort(),
    'weight': weightPrefixes.toList()..sort(),
  };

  static Set<String> parsePrefixes(Object? v) {
    final list = v is List ? v.map((e) => '$e') : (v is String ? v.split(RegExp(r'[\s,;]+')) : const <String>[]);
    return {for (final p in list) if (RegExp(r'^2\d$').hasMatch(p.trim())) p.trim()};
  }

  static ScaleRules fromJson(Object? raw) {
    try {
      final m = raw is String ? jsonDecode(raw) : raw;
      if (m is! Map) return const ScaleRules();
      String str(String k, String def) => (m[k]?.toString().trim().toLowerCase() ?? '').isEmpty ? def : m[k].toString().trim().toLowerCase();
      return ScaleRules(
        layout: str('layout', ''),
        mode: str('mode', 'auto'),
        amountUnit: str('amountUnit', 'tiyin'),
        amountPrefixes: parsePrefixes(m['amount']),
        weightPrefixes: parsePrefixes(m['weight']),
      );
    } catch (_) {
      return const ScaleRules();
    }
  }
}

/// Весовая этикетка: код товара (PLU или артикул) и вес в кг либо сумма в сомах.
class WeightLabel {
  /// Коды-кандидаты: раскладка «plu» (5 цифр, по умолчанию у весов Штрих-М) и «code» (6 цифр, весы Rongta).
  final List<({String code, double value})> candidates;
  final bool isAmount;
  const WeightLabel(this.candidates, this.isAmount);
}

/// EAN-13 на «2», верная контрольная цифра. Сумма или вес — по правилам компании ([ScaleRules]).
/// [clothing] — магазин одежды: внутренние штрихкоды тоже на «2», этикетка весов — только с явно записанным префиксом.
WeightLabel? parseWeightLabel(String barcode, {ScaleRules? rules, bool clothing = false}) {
  final r = rules ?? ScaleRules.current;
  final c = normalizeCode(barcode);
  if (!RegExp(r'^2\d{12}$').hasMatch(c) || !validEan13(c)) return null;
  final prefix = c.substring(0, 2);
  if (clothing && !r.explicit(prefix)) return null;
  final isAmount = r.isAmount(prefix);
  double value(String raw) {
    final n = int.parse(raw);
    return isAmount ? (r.amountUnit == 'som' ? n.toDouble() : n / 100) : n / 1000;
  }

  final plu = (code: c.substring(2, 7), value: value(c.substring(7, 12)));
  final code = (code: c.substring(2, 8), value: value(c.substring(8, 12)));
  final list = [
    if (r.layout != 'code' && plu.value > 0) plu,
    if (r.layout != 'plu' && code.value > 0) code,
  ];
  return list.isEmpty ? null : WeightLabel(list, isAmount);
}

/// Совпадает ли короткий код этикетки с PLU или артикулом товара: «00012» = «12».
bool sameShortCode(String? field, String code) {
  if (field == null || field.trim().isEmpty) return false;
  final f = field.trim();
  if (f == code) return true;
  final a = int.tryParse(f), b = int.tryParse(code);
  return a != null && b != null && a == b;
}

bool validEan13(String c) {
  if (!RegExp(r'^\d{13}$').hasMatch(c)) return false;
  var sum = 0;
  for (var i = 0; i < 12; i++) {
    sum += (c.codeUnitAt(i) - 48) * (i.isEven ? 1 : 3);
  }
  return (10 - sum % 10) % 10 == c.codeUnitAt(12) - 48;
}
