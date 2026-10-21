// 18.10: акции товаров на телефоне — те же правила, что у кассы (PromotionRules.LineDiscount), проверенные на сервере 28.09.
import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';

void main() {
  const adygene = [PromoRule(minAmount: 32, percent: 15)];

  test('«Адыгене 1л» 50 сом, акция 15 % от 32 сом → 42,50 (продажа 1136 на сервере)', () {
    final line = CartLine(productId: 'a', name: 'Адыгене 1л', price: 50, promo: adygene);
    expect(line.discount, 7.5);
    expect(line.total, 42.5);
  });

  test('сумма строки меньше порога — акция не действует, ручная скидка тоже', () {
    final line = CartLine(productId: 'a', name: 'x', price: 30, promo: adygene, discount: 5);
    expect(line.discount, 0);
    expect(line.total, 30);
    line.discount = 10; // ручная скидка строке с акцией не ставится
    expect(line.discount, 0);
  });

  test('promo_quantity — предел штук: 30 сом × 11 при пределе 10 и 45 % → 135,00', () {
    final line = CartLine(productId: 'b', name: 'y', price: 30, qty: 11, promo: const [PromoRule(minAmount: 50, percent: 45, quantity: 10)]);
    expect(line.discount, 135);
    expect(line.total, 195);
  });

  test('из подходящих ступеней — с наибольшим «от суммы»', () {
    final rules = PromoRule.parse('[{"position":0,"min_amount":"0","discount_percent":"5"},{"position":1,"min_amount":"1000","discount_percent":"10"}]');
    expect(CartLine(productId: 'c', name: 'z', price: 100, qty: 5, promo: rules).discount, 25); // 500 → 5 %
    expect(CartLine(productId: 'c', name: 'z', price: 100, qty: 10, promo: rules).discount, 100); // 1000 → 10 %
  });

  test('скидка на чек — от суммы после акции: 42,50 × 10 % = 4,25', () {
    final cart = Cart(
      lines: [CartLine(productId: 'a', name: 'Адыгене 1л', price: 50, promo: adygene)],
      orderDiscountPercent: 10,
    );
    expect(cart.orderDiscount, 4.25);
    expect(cart.total, 38.25);
  });

  test('в запрос продажи строка с акцией уходит без ручной скидки', () {
    final cart = Cart(
      lines: [
        CartLine(productId: 'a', name: 'Адыгене 1л', price: 50, promo: adygene),
        CartLine(productId: 'b', name: 'Хлеб', price: 30, discount: 5),
      ],
    );
    final body = SalesRepository.buildBody(cart: cart, shiftId: 's', method: PayMethod.cash, received: 100);
    final items = (body['items'] as List).cast<Map<String, dynamic>>();
    expect(items[0].containsKey('discount'), isFalse);
    expect(items[1]['discount'], '5.00');
  });

  test('правила из API: строки, без процента — не ступень, мусор — без акции', () {
    expect(
      PromoRule.parse([
        {'min_amount': '10', 'discount_percent': '0'},
      ]),
      isEmpty,
    );
    expect(PromoRule.parse('не json'), isEmpty);
    expect(PromoRule.parse(null), isEmpty);
    expect(PromoRule.encodeFromApi([]), '');
    final enc = PromoRule.encodeFromApi([
      {'position': 1, 'min_amount': '32.00', 'discount_percent': '15.00', 'promo_quantity': 3},
    ]);
    final back = PromoRule.parse(enc).single;
    expect([back.minAmount, back.percent, back.quantity], [32, 15, 3]);
  });

  test('10 000 случайных строк: скидка не больше суммы, итог не меньше нуля, совпадает с независимым расчётом', () {
    final rnd = Random(1810);
    var bad = 0;
    for (var i = 0; i < 10000; i++) {
      final price = (rnd.nextInt(100000) + 1) / 100;
      final qty = rnd.nextBool() ? (rnd.nextInt(20) + 1).toDouble() : (rnd.nextInt(5000) + 1) / 1000;
      final rules = [
        for (var k = 0; k < rnd.nextInt(3) + 1; k++)
          PromoRule(
            minAmount: rnd.nextInt(2000).toDouble(),
            percent: (rnd.nextInt(60) + 1).toDouble(),
            quantity: rnd.nextBool() ? (rnd.nextInt(10) + 1).toDouble() : null,
            position: k,
          ),
      ];
      final line = CartLine(productId: 'p', name: 'p', price: price, qty: qty, promo: rules);
      final gross = roundMoney(qty * price);
      // независимый расчёт
      PromoRule? best;
      for (final r in rules) {
        if (gross >= r.minAmount - 1e-9 && (best == null || r.minAmount > best.minAmount)) best = r;
      }
      double expected = 0;
      if (best != null) {
        final dq = best.quantity != null ? min(qty, best.quantity!) : qty;
        expected = min(gross, roundMoney(roundMoney(dq * price) * best.percent / 100));
      }
      if ((line.discount - expected).abs() > 0.001 || line.total < 0 || line.discount > gross + 1e-9) bad++;
    }
    expect(bad, 0);
  });
}
