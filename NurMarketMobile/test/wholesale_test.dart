import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/sale/cart.dart';

/// 13.10, «продажа оптом и в розницу»: строка переходит на оптовую цену и обратно, скидка не больше суммы строки.
void main() {
  test('строка ↔ опт', () {
    final line = CartLine(productId: 'p', name: 'Макфа', price: 85, qty: 10, wholesalePrice: 70);
    expect(line.canWholesale, isTrue);
    line.setWholesale(true);
    expect(line.price, 70);
    expect(line.total, 700);
    line.setWholesale(false);
    expect(line.price, 85);
    expect(line.total, 850);
  });

  test('без оптовой цены опт не включается', () {
    final line = CartLine(productId: 'p', name: 'Mars', price: 39);
    line.setWholesale(true);
    expect(line.wholesale, isFalse);
    expect(line.price, 39);
  });

  test('скидка не больше новой суммы строки', () {
    final line = CartLine(productId: 'p', name: 'X', price: 100, qty: 1, wholesalePrice: 60, discount: 80);
    line.setWholesale(true);
    expect(line.discount, 60);
    expect(line.total, 0);
  });

  test('опт и розница одного товара — разные строки', () {
    final a = CartLine(productId: 'p', name: 'X', price: 100, wholesalePrice: 80);
    final b = CartLine(productId: 'p', name: 'X', price: 100, wholesalePrice: 80)..setWholesale(true);
    final cart = const Cart().add(a).add(b);
    expect(cart.lines.length, 2);
    expect(cart.total, 180);
  });

  test('один товар разных размеров — разные строки (С5)', () {
    final a = CartLine(productId: 'p', name: 'Платье (44)', price: 1500, variantId: 'v44');
    final b = CartLine(productId: 'p', name: 'Платье (46)', price: 1500, variantId: 'v46');
    final c = CartLine(productId: 'p', name: 'Платье (44)', price: 1500, variantId: 'v44');
    final cart = const Cart().add(a).add(b).add(c);
    expect(cart.lines.length, 2);
    expect(cart.lines.first.qty, 2);
  });
}
