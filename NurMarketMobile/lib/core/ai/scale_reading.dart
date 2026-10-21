/// 2026-10-20, владелец: «весы к телефону не подключаются — камера смотрит, сама должна распознать весы»;
/// «картошка 1 кг 40 сом — если на весах 500 грамм, то естественно 20 сом». Разбор ответа ИИ о табло весов,
/// ожидание устойчивого веса и расчёт суммы. Без Flutter — проверяется юнит-тестами (test/scale_reading_test.dart).
library;

import 'dart:convert';

import '../../features/sale/cart.dart' show roundMoney;

/// Что ИИ прочитал на кадре: товар на весах и вес с табло.
class ScaleReading {
  /// Название товара («Картофель»); пусто — ИИ не понял или не спрашивали.
  final String product;

  /// Вес в килограммах; null — табло не видно или не читается; 0 — на весах пусто.
  final double? weightKg;

  /// Единица на табло: kg или g.
  final String unit;

  /// Уверенность 0..1.
  final double confidence;

  const ScaleReading({this.product = '', this.weightKg, this.unit = 'kg', this.confidence = 0});

  bool get hasWeight => weightKg != null;
  bool get isZero => weightKg != null && weightKg!.abs() < 0.0005;
}

final _num = RegExp(r'(-?\d+(?:[.,]\d+)?)');

/// Вес из текста табло: «0.500», «0,500», «500 g», «500 г», «500гр», «0.50 kg», «1,250 кг».
/// Без единицы: дробное число — килограммы; целое ≥ 50 — граммы (табло в граммах), иначе килограммы.
double? parseWeightText(String raw, {String unit = ''}) {
  final s = raw.trim().toLowerCase().replaceAll(' ', ' ');
  if (s.isEmpty) return null;
  final m = _num.firstMatch(s.replaceAll(' ', ''));
  if (m == null) return null;
  final text = m.group(1)!;
  final v = double.tryParse(text.replaceAll(',', '.'));
  if (v == null) return null;
  final tail = '${s.substring(s.indexOf(RegExp(r'\d')))} ${unit.toLowerCase()}';
  final kg = RegExp(r'kg|кг|килог').hasMatch(tail);
  // \b в Dart не видит границу у кириллицы — граница задаётся явно
  final g = !kg && RegExp(r'(?:^|[\d\s])(?:g|gr|gram|grams|г|гр|грамм)(?:$|[\s.])').hasMatch(tail);
  if (g) return v / 1000;
  if (kg) return v;
  final fractional = text.contains('.') || text.contains(',');
  if (!fractional && v >= 50) return v / 1000;
  return v;
}

/// Ответ ИИ строго JSON {product, weight_kg, unit, confidence} (бывает в ```json … ``` или с текстом вокруг).
/// weight_kg может прийти строкой («0,500», «500 g»). Если на табло граммы (unit g), а число похоже на граммы
/// (≥ 20) — ИИ не перевёл, делим на 1000.
ScaleReading parseScaleJson(String text) {
  var t = text.trim();
  final a = t.indexOf('{'), b = t.lastIndexOf('}');
  if (a < 0 || b <= a) return const ScaleReading();
  t = t.substring(a, b + 1);
  Object? decoded;
  try {
    decoded = jsonDecode(t);
  } catch (_) {
    return const ScaleReading();
  }
  if (decoded is! Map) return const ScaleReading();
  final unitRaw = (decoded['unit']?.toString() ?? '').trim().toLowerCase();
  final grams = RegExp(r'^(g|г|гр|gr|gram|grams|грамм)$').hasMatch(unitRaw);
  final w = decoded['weight_kg'] ?? decoded['weight'];
  double? kg;
  if (w is num) {
    kg = w.toDouble();
    if (grams && kg >= 20) kg = kg / 1000;
  } else if (w != null && '$w'.trim().isNotEmpty && '$w' != 'null') {
    kg = parseWeightText('$w', unit: grams ? 'g' : '');
  }
  var conf = decoded['confidence'] is num ? (decoded['confidence'] as num).toDouble() : double.tryParse('${decoded['confidence']}'.replaceAll(',', '.')) ?? 0;
  if (conf > 1) conf = conf / 100; // ИИ иногда отвечает процентами
  return ScaleReading(
    product: (decoded['product']?.toString() ?? '').trim(),
    weightKg: kg == null || !kg.isFinite ? null : (kg * 1000).roundToDouble() / 1000,
    unit: grams ? 'g' : 'kg',
    confidence: conf.clamp(0, 1).toDouble(),
  );
}

/// Ожидание устойчивого веса: [need] показаний подряд, отличающихся не больше чем на [tolerance] кг (±5 г).
/// Ноль на табло — «положите товар», счёт начинается заново.
class WeightStabilizer {
  final int need;
  final double tolerance;
  final List<double> _run = [];
  WeightStabilizer({this.need = 2, this.tolerance = 0.005});

  /// Последние подряд одинаковые показания.
  List<double> get run => List.unmodifiable(_run);

  /// Добавить показание; вернуть устойчивый вес (среднее серии) или null — ещё ждём.
  double? add(double? kg) {
    if (kg == null || kg.abs() < 0.0005) {
      _run.clear();
      return null;
    }
    if (_run.isNotEmpty && (kg - _run.last).abs() > tolerance) _run.clear();
    _run.add(kg);
    if (_run.length > need) _run.removeAt(0);
    if (_run.length < need) return null;
    final avg = _run.reduce((a, b) => a + b) / _run.length;
    return (avg * 1000).roundToDouble() / 1000;
  }

  void reset() => _run.clear();
}

/// Сумма весовой строки, как в кассе: цена за кг × вес, до копеек (тыйынов).
double weightTotal(double pricePerKg, double kg) => roundMoney(pricePerKg * kg);

/// Вес для чека: 0,500.
String kgText(double kg) => kg.toStringAsFixed(3).replaceAll('.', ',');

/// Проверка веса перед добавлением в чек.
enum WeightCheck { ok, zero, negative, tooHeavy }

WeightCheck checkWeight(double? kg, {double maxKg = 50}) {
  if (kg == null || kg.abs() < 0.0005) return WeightCheck.zero;
  if (kg < 0) return WeightCheck.negative;
  if (kg > maxKg) return WeightCheck.tooHeavy;
  return WeightCheck.ok;
}
