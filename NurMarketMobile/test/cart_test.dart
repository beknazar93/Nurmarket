// Тесты правил чека. Без сети и без боевой базы (ТЗ, раздел 3).
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/sale/cart.dart';

void main() {
  CartLine line(String id, double price, {double qty = 1, double discount = 0, bool weight = false}) =>
      CartLine(productId: id, name: id, price: price, qty: qty, discount: discount, isWeight: weight);

  test('итог: цена × количество', () {
    final c = const Cart().add(line('a', 85, qty: 2)).add(line('b', 39));
    expect(c.total, 209);
    expect(c.count, 2);
  });

  test('тот же товар прибавляется к строке, весовой — новой строкой', () {
    var c = const Cart().add(line('a', 85)).add(line('a', 85));
    expect(c.count, 1);
    expect(c.lines.first.qty, 2);
    c = c.add(line('w', 120, qty: 0.5, weight: true)).add(line('w', 120, qty: 0.3, weight: true));
    expect(c.count, 3);
  });

  test('скидка на чек процентом — от суммы после скидок строк, до копейки', () {
    final c = Cart(lines: [line('a', 100, discount: 10), line('b', 33.33)], orderDiscountPercent: 10);
    // база 123.33, скидка 12.33 → 111.00
    expect(c.orderDiscount, 12.33);
    expect(c.total, 111.00);
  });

  test('скидка на чек суммой не больше остатка', () {
    final c = Cart(lines: [line('a', 50)], orderDiscountSum: 80);
    expect(c.total, 0);
  });

  test('сдача и подсказки купюр', () {
    expect(change(500, 209), 291);
    expect(change(100, 209), 0);
    expect(cashSuggestions(209), [209, 250, 300, 500]);
  });
}
