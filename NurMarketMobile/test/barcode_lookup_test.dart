import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/scan/barcode_lookup.dart';

void main() {
  test('UPC-A и EAN-13 с нулём — один товар', () {
    expect(barcodeVariants('012345678905'), containsAll(['012345678905', '0012345678905']));
    expect(barcodeVariants('0012345678905'), contains('012345678905'));
    expect(barcodeVariants('4600000000000'), ['4600000000000']);
  });

  test('весовая этикетка: PLU 12, 1,234 кг', () {
    // 22 00012 01234 + контрольная
    const body = '220001201234';
    var sum = 0;
    for (var i = 0; i < 12; i++) {
      sum += int.parse(body[i]) * (i.isEven ? 1 : 3);
    }
    final code = '$body${(10 - sum % 10) % 10}';
    final label = parseWeightLabel(code)!;
    expect(label.isAmount, isFalse);
    expect(label.candidates.first.code, '00012');
    expect(label.candidates.first.value, closeTo(1.234, 1e-9));
    expect(sameShortCode('12', label.candidates.first.code), isTrue);
  });

  test('неверная контрольная цифра и обычный штрихкод — не весовые', () {
    expect(parseWeightLabel('2200012012341'), isNull); // верная контрольная для 220001201234 — 5
    expect(parseWeightLabel('4600000000000'), isNull);
    expect(parseWeightLabel('22000120123'), isNull);
  });
}
