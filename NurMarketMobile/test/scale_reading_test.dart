import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/scale_reading.dart';
import 'package:nurmarket/features/sale/cart.dart';

// 2026-10-20: весовой товар умной камерой — разбор табло, устойчивость веса и сумма (картошка 40 сом/кг × 0,5 = 20).
void main() {
  test('вес из текста табло — все форматы', () {
    expect(parseWeightText('0.500'), 0.5);
    expect(parseWeightText('0,500'), 0.5);
    expect(parseWeightText('500 g'), 0.5);
    expect(parseWeightText('500 г'), 0.5);
    expect(parseWeightText('500гр'), 0.5);
    expect(parseWeightText('0.50 kg'), 0.5);
    expect(parseWeightText('1,250 кг'), 1.25);
    expect(parseWeightText('500'), 0.5); // целое без единицы ≥ 50 — граммы
    expect(parseWeightText('2'), 2.0); // маленькое целое — килограммы
    expect(parseWeightText('0.000'), 0.0);
    expect(parseWeightText(''), isNull);
    expect(parseWeightText('---'), isNull);
  });

  test('ответ ИИ строго JSON — числом, строкой, в граммах, в ```json```', () {
    var r = parseScaleJson('{"product":"Картофель","weight_kg":0.5,"unit":"kg","confidence":0.92}');
    expect(r.product, 'Картофель');
    expect(r.weightKg, 0.5);
    expect(r.confidence, closeTo(0.92, 1e-9));

    r = parseScaleJson('```json\n{"product":"Лук","weight_kg":"0,500","unit":"kg","confidence":0.8}\n```');
    expect(r.weightKg, 0.5);

    r = parseScaleJson('{"product":"Лук","weight_kg":"500 g","unit":"g","confidence":0.8}');
    expect(r.weightKg, 0.5);

    // ИИ не перевёл граммы в кг
    r = parseScaleJson('{"product":"Яблоки","weight_kg":500,"unit":"g","confidence":0.7}');
    expect(r.weightKg, 0.5);
    expect(r.unit, 'g');

    // перевёл правильно, но unit g
    r = parseScaleJson('{"product":"Яблоки","weight_kg":0.5,"unit":"g","confidence":0.7}');
    expect(r.weightKg, 0.5);

    // уверенность процентами
    r = parseScaleJson('{"product":"Морковь","weight_kg":"0.50 kg","unit":"kg","confidence":85}');
    expect(r.weightKg, 0.5);
    expect(r.confidence, closeTo(0.85, 1e-9));

    // табло не видно / ноль / мусор
    expect(parseScaleJson('{"product":"","weight_kg":null,"unit":"kg","confidence":0}').hasWeight, isFalse);
    expect(parseScaleJson('{"product":"","weight_kg":0,"unit":"kg","confidence":0.9}').isZero, isTrue);
    expect(parseScaleJson('не знаю').hasWeight, isFalse);
  });

  test('устойчивость — точные случаи', () {
    final s = WeightStabilizer();
    expect(s.add(0.31), isNull);
    expect(s.add(0.52), isNull);
    expect(s.add(0.499), isNull); // 0.52 → 0.499: 21 г — серия заново
    expect(s.add(0.501), 0.5); // 0.499 и 0.501 — в пределах 5 г, среднее 0,5
    s.reset();
    expect(s.add(0.5), isNull);
    expect(s.add(0), isNull); // сняли товар
    expect(s.add(0.5), isNull); // счёт заново
    expect(s.add(0.504), 0.502);

    final three = WeightStabilizer(need: 3);
    expect(three.add(1.0), isNull);
    expect(three.add(1.002), isNull);
    expect(three.add(1.001), 1.001);
  });

  test('сумма весовой строки: картошка 40 сом/кг × 0,5 кг = 20 сом, до тыйынов', () {
    expect(weightTotal(40, 0.5), 20);
    expect(weightTotal(40, 1), 40);
    expect(weightTotal(85.5, 0.333), 28.47); // 28.4715 → 28.47
    expect(weightTotal(120, 1.255), 150.6);
    // в чеке — так же, как считает касса
    final cart = const Cart().add(CartLine(productId: 'p', name: 'Картошка', price: 40, qty: 0.5, isWeight: true, unit: 'кг'));
    expect(cart.total, 20);
    expect(kgText(0.5), '0,500');
  });

  test('проверка веса: 0 — положите товар, > 50 кг — переспросить', () {
    expect(checkWeight(0.5), WeightCheck.ok);
    expect(checkWeight(0), WeightCheck.zero);
    expect(checkWeight(null), WeightCheck.zero);
    expect(checkWeight(-0.2), WeightCheck.negative);
    expect(checkWeight(51), WeightCheck.tooHeavy);
    expect(checkWeight(50), WeightCheck.ok);
  });
}
