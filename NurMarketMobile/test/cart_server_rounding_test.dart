import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/sale/cart.dart';

// 2026-11-01 (аудит 10 из 10, М-01; ответ Умара 01.11; опыты на сервере, чеки 1971–1988): строки считаются точно, затем
// подытог = FLOOR(Σ весовых строк, 0,01) + HALF_UP(Σ остальных, 0,01); скидка чека в процентах = HALF_UP((подытог − скидки строк) × p / 100);
// итог = подытог − скидки строк − скидка чека. Чистая арифметика, без сети и базы.
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

  test('чек 1981: вниз округляется СУММА весовых строк: 4,875 + 22,638 = 27,513 → 27,51 (а не 27,50)', () {
    expect(Cart(lines: [w(0.375, 13), w(0.343, 66)]).total, 27.51);
  });

  test('чек 1984: весовая 22,638 + штучная 5,00 → 22,63 + 5,00 = 27,63', () {
    expect(Cart(lines: [w(0.343, 66), n(1, 5)]).total, 27.63);
  });

  test('чек 1985: весовая 22,638 + «Доп. услуга» 4,472 (не весовая) → 22,63 + 4,47 = 27,10', () {
    final extra = CartLine(productId: 'x', name: 'Доп. услуга', price: 13, qty: 0.344, custom: true);
    expect(Cart(lines: [w(0.343, 66), extra]).total, 27.10);
  });

  test('«Алма»: единица кг, но is_weight=false на сервере (weightFlag=false) → не весовая: 2,295 × 45 = 103,275 → 103,28 (HALF_UP)', () {
    final alma = CartLine(productId: 'alma', name: 'Алма', price: 45, qty: 2.295, isWeight: true, weightFlag: false);
    expect(Cart(lines: [alma]).total, 103.28);
  });

  test('is_weight=true на сервере (weightFlag=true) даёт весовое округление вниз, даже если телефон считает строку невесовой', () {
    final line = CartLine(productId: 'w2', name: 'Весовой', price: 66, qty: 0.343, weightFlag: true);
    expect(Cart(lines: [line]).total, 22.63);
  });

  test('чек 1987: весовая 0,341 × 66 = 22,506 → 22,50', () {
    expect(Cart(lines: [w(0.341, 66)]).total, 22.50);
  });

  test('чек 1986: весовая 22,638, скидка строки 0,07, скидка чека 15 % → подытог 22,63, скидка 3,45 (0,07 + 3,38), итог 19,18', () {
    final line = CartLine(productId: 'w', name: 'Весовой', price: 66, qty: 0.343, isWeight: true, discount: 0.07);
    final cart = Cart(lines: [line], orderDiscountPercent: 15);
    expect(cart.subtotal, 22.63);
    expect(cart.lineDiscounts, 0.07);
    expect(cart.orderDiscount, 3.38);
    expect(cart.lineDiscounts + cart.orderDiscount, closeTo(3.45, 1e-9));
    expect(cart.total, 19.18);
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

  test('цена 100/3: на сервер уходит `33.33` (два знака), поэтому 3 × 33,33 = 99,99 — телефон считает то же, что получит сервер', () {
    final price = 100 / 3;
    expect(Cart(lines: [CartLine(productId: 'p', name: 'Штука', price: price, qty: 3)]).total, 99.99);
  });

  test('вес считается с тремя знаками, как уходит на сервер: 0,333333 → 0,333; обычная 0,333 × 3 = 0,999 → 1,00; весовая — вниз 0,99', () {
    expect(Cart(lines: [n(0.333333, 3)]).total, 1.0);
    expect(Cart(lines: [w(0.333333, 3)]).total, 0.99);
  });

  test('М2-01: два взвешивания одного товара по одной цене (0,375 × 3,80 дважды) — как одна позиция 0,750: весовой итог 2,85', () {
    expect(Cart(lines: [w(0.375, 3.80), w(0.375, 3.80)]).total, 2.85);
  });

  test('М2-01: акция «от 100 сом −10 %» на сумме двух взвешиваний по 60 сом: порог по сумме группы 120 → скидка 12,00, итог 108,00', () {
    const promo = [PromoRule(minAmount: 100, percent: 10)];
    CartLine weigh() => CartLine(productId: 'ap', name: 'Яблоки', price: 120, qty: 0.5, isWeight: true, promo: promo);
    final cart = Cart(lines: [weigh(), weigh()]);
    expect(cart.lineDiscounts, 12.0);
    expect(cart.total, 108.0);
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
