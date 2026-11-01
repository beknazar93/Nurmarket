import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/ops/return_ledger.dart';

// 2026-11-01 (аудит 10 из 10, М-02): ответ сервера c8958b4 — quantity после возврата не уменьшается,
// «сколько можно вернуть» — returnable_qty, «уже возвращено» — returned_qty. Чистая проверка разбора, без сети и базы.
void main() {
  test('новый сервер: returnable_qty главнее quantity', () {
    final l = SaleLineInfo.fromJson({'id': 'a', 'name': 'Сок', 'quantity': 5, 'returned_qty': 2, 'returnable_qty': 3, 'unit_price': 100});
    expect(l.qty, 5);
    expect(l.refundable, 3);
  });

  test('новый сервер: строка возвращена целиком — вернуть нечего', () {
    final l = SaleLineInfo.fromJson({'id': 'a', 'name': 'Сок', 'quantity': 5, 'returned_qty': 5, 'returnable_qty': 0, 'unit_price': 100});
    expect(l.refundable, 0);
  });

  test('нет returnable_qty: считается по returned_qty, затем по старым полям', () {
    expect(SaleLineInfo.fromJson({'id': 'a', 'quantity': 5, 'returned_qty': 1, 'unit_price': 10}).refundable, 4);
    expect(SaleLineInfo.fromJson({'id': 'a', 'quantity': 5, 'quantity_refunded': 2, 'unit_price': 10}).refundable, 3);
    expect(SaleLineInfo.fromJson({'id': 'a', 'quantity': 5, 'unit_price': 10}).refundable, 5);
  });
}
