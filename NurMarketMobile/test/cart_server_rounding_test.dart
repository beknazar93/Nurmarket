import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/sale/cart.dart';

// 2026-11-01 (аудит 10 из 10, М-01; ответ Умара 01.11; живая сверка NBS, чеки 1974–1980): модель сервера —
// обычные строки не округляются, весовая строка округляется ВНИЗ до 0,01; подытог — один раз HALF_UP;
// процент скидки чека берётся от ОКРУГЛЁННОГО подытога, сумма скидки округляется отдельно HALF_UP; итог = подытог − скидка.
// Чистая арифметика, без сети и базы.
void main() {
  CartLine n(double qty, double price) => CartLine(productId: 'n', name: 'Обычный', price: price, qty: qty); // не весовой
  CartLine w(double qty, double price) => CartLine(productId: 'w', name: 'Весовой', price: price, qty: qty, isWeight: true);

  test('обычная строка 0,375 × 3,80 = 1,425 → 1,43 (HALF_UP)', () {
    expect(Cart(lines: [n(0.375, 3.80)]).total, 1.43);
  });

  test('чек 1978: две такие строки — итог 2,85, а не 1,43 + 1,43 = 2,86', () {
    expect(Cart(lines: [n(0.375, 3.80), n(0.375, 3.80)]).total, 2.85);
  });

  test('чек 1979: весовая строка 0,343 кг × 66 = 22,638 → ВНИЗ 22,63', () {
    expect(Cart(lines: [w(0.343, 66)]).total, 22.63);
  });

  test('чек 1980: то же со скидкой 10 % → скидка 2,26, итог 20,37', () {
    final cart = Cart(lines: [w(0.343, 66)], orderDiscountPercent: 10);
    expect(cart.orderDiscount, 2.26);
    expect(cart.total, 20.37);
  });

  test('чек 1974: обычная 0,345 × 45 = 15,525 → 15,53, скидка 10 % = 1,55, итог 13,98', () {
    final cart = Cart(lines: [n(0.345, 45)], orderDiscountPercent: 10);
    expect(cart.subtotal, 15.53);
    expect(cart.orderDiscount, 1.55);
    expect(cart.total, 13.98);
  });

  test('чек 1975: 5,00 со скидкой 2,5 % → скидка 0,13 (0,125 HALF_UP), итог 4,87', () {
    final cart = Cart(lines: [n(1, 5)], orderDiscountPercent: 2.5);
    expect(cart.orderDiscount, 0.13);
    expect(cart.total, 4.87);
  });

  test('чек 1356 / 1976: 17,00 со скидкой 2,5 % → скидка 0,43 (0,425 HALF_UP), итог 16,57', () {
    final cart = Cart(lines: [n(1, 17)], orderDiscountPercent: 2.5);
    expect(cart.orderDiscount, 0.43);
    expect(cart.total, 16.57);
  });

  test('чек 1977: 0,375 × 3,80 со скидкой 50 % — процент от округлённого подытога 1,43 → скидка 0,72, итог 0,71', () {
    final cart = Cart(lines: [n(0.375, 3.80)], orderDiscountPercent: 50);
    expect(cart.subtotal, 1.43);
    expect(cart.orderDiscount, 0.72);
    expect(cart.total, 0.71);
  });

  test('roundMoney: 1,005 → 1,01, −1,005 → −1,01', () {
    expect(roundMoney(1.005), 1.01);
    expect(roundMoney(-1.005), -1.01);
  });

  test('скидка 10 % от подытога 1,43: скидка 0,14 отдельно, итог 1,29', () {
    expect(Cart(lines: [n(0.375, 3.80)], orderDiscountPercent: 10).total, 1.29);
  });

  test('цена упаковки 100/3 не теряет точность: 3 × 33,33333333 = 100,00 (а не 99,99)', () {
    final price = 100 / 3;
    expect(Cart(lines: [CartLine(productId: 'p', name: 'Штука', price: price, qty: 3)]).total, 100.0);
    // три отдельные строки по одной штуке: сумма неокруглённых строк 99,99999999 → 100,00
    final three = Cart(lines: [for (var i = 0; i < 3; i++) CartLine(productId: 'p$i', name: 'Штука', price: price, qty: 1)]);
    expect(three.total, 100.0);
  });

  test('вес с 6 знаками: обычная 0,333333 × 3 = 0,999999 → 1,00; весовая — вниз 0,99', () {
    expect(Cart(lines: [n(0.333333, 3)]).total, 1.0);
    expect(Cart(lines: [w(0.333333, 3)]).total, 0.99);
  });

  test('ручная скидка строки больше её суммы: строка не уходит в минус и не уменьшает другую', () {
    final a = CartLine(productId: 'a', name: 'А', price: 100, qty: 1, discount: 150);
    final b = CartLine(productId: 'b', name: 'Б', price: 50, qty: 1);
    final cart = Cart(lines: [a, b]);
    expect(a.discount, 100);
    expect(a.total, 0);
    expect(cart.total, 50);
    expect(cart.lineDiscounts, 100);
  });

  test('уменьшили количество — скидка строки обрезается по новой сумме', () {
    final a = CartLine(productId: 'a', name: 'А', price: 100, qty: 2, discount: 150);
    final cart = Cart(lines: [a]).setQty(0, 1);
    expect(cart.lines.single.discount, 100);
    expect(cart.total, 0);
  });

  test('скидка суммой больше итога обрезается до нуля', () {
    expect(Cart(lines: [n(1, 10)], orderDiscountSum: 50).total, 0);
  });
}
