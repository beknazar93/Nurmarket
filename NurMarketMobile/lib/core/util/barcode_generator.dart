import 'dart:math';

/// 2026-10-26 (требование владельца): свой штрихкод EAN-13 для нового товара — «Сгенерировать» рядом со сканом.
/// Те же правила, что BarcodeGenerator.cs кассы: первая цифра 1–9 и никогда 0 (ведущий ноль путают с UPC-A и теряют
/// при экспорте); первая цифра не 2 — любой EAN-13 на «2…» с верной контрольной цифрой телефон и касса читают как
/// весовую этикетку (parseWeightLabel / WeightBarcodeParser), и штучный товар продавался бы с неверным весом/ценой;
/// цифры — Random.secure(); контрольная цифра по EAN-13.
class BarcodeGenerator {
  BarcodeGenerator._();

  /// Первые цифры, которых генератор не использует: 0 (по просьбе владельца) и 2 (весовые префиксы 20–29).
  static const forbiddenFirstDigits = {0, 2};

  /// Сколько раз перегенерировать, пока код занят.
  static const maxAttempts = 10;

  static Random _secure() {
    try {
      return Random.secure();
    } on UnsupportedError {
      return Random();
    }
  }

  /// Новый код без проверки уникальности.
  static String create([Random? random]) {
    final r = random ?? _secure();
    final firstDigits = [for (var d = 1; d <= 9; d++) if (!forbiddenFirstDigits.contains(d)) d];
    final digits = <int>[firstDigits[r.nextInt(firstDigits.length)], for (var i = 1; i < 12; i++) r.nextInt(10)];
    return digits.join() + checkDigit(digits).toString();
  }

  /// Контрольная цифра EAN-13 по первым 12 цифрам (веса 1/3).
  static int checkDigit(List<int> first12) {
    var sum = 0;
    for (var i = 0; i < 12; i++) {
      sum += first12[i] * (i.isEven ? 1 : 3);
    }
    return (10 - sum % 10) % 10;
  }

  /// Новый код, который [isTaken] считает свободным; не более [maxAttempts] попыток. Нет связи или проверка
  /// упала — код отдаётся как есть (повтор поймает проверка при сохранении).
  static Future<String> createFree(Future<bool> Function(String code) isTaken, {Random? random, int attempts = maxAttempts}) async {
    final r = random ?? _secure();
    var code = create(r);
    for (var i = 0; i < attempts; i++) {
      try {
        if (!await isTaken(code)) return code;
      } catch (_) {
        return code;
      }
      code = create(r);
    }
    return code;
  }
}
