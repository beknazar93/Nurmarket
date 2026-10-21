import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';

void main() {
  test('отложенный чек сохраняется и возвращается без потерь', () {
    final cart = Cart(
      lines: [
        CartLine(productId: 'p1', name: 'Макароны', price: 85, qty: 2, discount: 10),
        CartLine(productId: 'p2', name: 'Яблоки', price: 120, qty: 1.25, isWeight: true, unit: 'кг'),
        CartLine(productId: 'custom-1', name: 'Доставка', price: 200, custom: true),
      ],
      orderDiscountPercent: 5,
    );
    final h = HeldCart(id: 'x', at: DateTime(2026, 10, 13, 12), note: 'Азамат', cart: cart);
    final back = HeldCart.fromJson(h.toJson());
    expect(back.note, 'Азамат');
    expect(back.cart.lines.length, 3);
    expect(back.cart.lines[1].isWeight, isTrue);
    expect(back.cart.lines[2].custom, isTrue);
    expect(back.cart.total, cart.total);
  });

  test('ключ операции — UUID v4', () {
    final k = OpsRepository.newKey();
    expect(RegExp(r'^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$').hasMatch(k), isTrue);
    expect(OpsRepository.newKey(), isNot(k));
  });
}
