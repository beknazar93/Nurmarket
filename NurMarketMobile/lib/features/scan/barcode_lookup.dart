/// 13.10: разбор штрихкода со сканера — как в кассе (WeightBarcodeParser, LocalCartService.FindByEmbeddedCode).
/// Камера читает один и тот же товар по-разному: UPC-A — 12 цифр, а в базе он записан EAN-13 с ведущим нулём (и наоборот);
/// весовая этикетка с весов (2X…) несёт код товара и вес — её штрихкода нет ни у одного товара.
library;

/// Все написания одного штрихкода: как есть, UPC-A ↔ EAN-13 с нулём впереди.
List<String> barcodeVariants(String code) {
  final c = code.trim();
  final v = <String>{c};
  if (RegExp(r'^\d{12}$').hasMatch(c)) v.add('0$c');
  if (RegExp(r'^0\d{12}$').hasMatch(c)) v.add(c.substring(1));
  return v.toList();
}

/// Весовая этикетка: код товара (PLU или артикул) и вес в кг либо сумма в сомах.
class WeightLabel {
  /// Коды-кандидаты: раскладка «plu» (5 цифр, по умолчанию у весов Штрих-М) и «code» (6 цифр, весы Rongta).
  final List<({String code, double value})> candidates;
  final bool isAmount;
  const WeightLabel(this.candidates, this.isAmount);
}

/// EAN-13 на «2», верная контрольная цифра. Префикс 25 — сумма в тыйынах, остальные — вес в граммах (как в кассе по умолчанию).
WeightLabel? parseWeightLabel(String barcode) {
  final c = barcode.trim();
  if (!RegExp(r'^2\d{12}$').hasMatch(c) || !_validEan13(c)) return null;
  final isAmount = c.startsWith('25');
  double value(String raw) {
    final n = int.parse(raw);
    return isAmount ? n / 100 : n / 1000;
  }

  final plu = (code: c.substring(2, 7), value: value(c.substring(7, 12)));
  final code = (code: c.substring(2, 8), value: value(c.substring(8, 12)));
  final list = [if (plu.value > 0) plu, if (code.value > 0) code];
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

bool _validEan13(String c) {
  var sum = 0;
  for (var i = 0; i < 12; i++) {
    sum += (c.codeUnitAt(i) - 48) * (i.isEven ? 1 : 3);
  }
  return (10 - sum % 10) % 10 == c.codeUnitAt(12) - 48;
}
