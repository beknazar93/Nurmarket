import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';

/// 2026-11-11 (сводка Умара): виртуальные строки старых долговых чеков в GET clients/{id}/deals/ — id «sale:…», is_check_debt: true.
/// Мобилка список сделок клиента не показывает; читает deals только для срока долга (PATCH) и остатка чека (detail) — там такие строки пропускаются.
void main() {
  test('виртуальная строка чека распознаётся по is_check_debt и по id «sale:»', () {
    expect(isCheckDebtRow({'id': 'sale:7f1c', 'is_check_debt': true, 'remaining_debt': '120.00'}), isTrue);
    expect(isCheckDebtRow({'id': 'sale:7f1c'}), isTrue);
    expect(isCheckDebtRow({'id': 42, 'is_check_debt': 'true'}), isTrue);
    expect(isCheckDebtRow({'id': 42, 'kind': 'debt', 'remaining_debt': '50'}), isFalse);
    expect(isCheckDebtRow({'id': 'deal-1', 'is_check_debt': false}), isFalse);
    expect(isCheckDebtRow(null), isFalse);
  });

  test('id «sale:…» — не id сделки', () {
    expect(isCheckDebtId('sale:abc'), isTrue);
    expect(isCheckDebtId(' SALE:abc'), isTrue);
    expect(isCheckDebtId('12'), isFalse);
    expect(isCheckDebtId(null), isFalse);
  });
}
