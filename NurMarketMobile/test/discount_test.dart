import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/sale/cart.dart';

/// 13.10, «скидку в соме тоже, а не только процент»: скидка на чек суммой и процентом.
void main() {
  final cart = const Cart().add(CartLine(productId: 'a', name: 'A', price: 100, qty: 2)).add(CartLine(productId: 'b', name: 'B', price: 50));

  test('скидка суммой', () {
    final c = cart.copyWith(orderDiscountSum: 30);
    expect(c.orderDiscount, 30);
    expect(c.total, 220);
  });

  test('скидка суммой не больше чека', () {
    final c = cart.copyWith(orderDiscountSum: 999);
    expect(c.total, 0);
  });

  test('скидка процентом', () {
    final c = cart.copyWith(orderDiscountPercent: 10);
    expect(c.orderDiscount, 25);
    expect(c.total, 225);
  });
}
