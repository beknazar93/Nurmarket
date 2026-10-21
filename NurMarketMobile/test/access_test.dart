import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/session_store.dart';

// 13.10: доступы сотрудника — флаги can_view_* профиля, как у кассы (PermissionService).
void main() {
  final cashierPerms = Session.extractPerms({
    'user': {'role': 'cashier', 'can_view_cashier': true, 'can_view_market_discount': false, 'can_view_products': true},
  });
  final cashier = Session(email: 'k', access: '', refresh: '', role: 'cashier', perms: cashierPerms);
  final owner = Session(email: 'o', access: '', refresh: '', role: 'owner', isOwner: true, perms: const []);

  test('cashier gets only flags set on the site', () {
    expect(cashier.can(Session.viewProducts), isTrue);
    expect(cashier.can(Session.discount), isFalse);
    expect(cashier.canRoute('/stock'), isTrue);
    expect(cashier.canRoute('/admin/staff'), isFalse);
    expect(cashier.showAdmin, isFalse);
    expect(cashier.canRoute('/debts'), isTrue);
  });
  test('owner always sees owner sections, settings only by flag', () {
    expect(owner.can(Session.viewAnalytics), isTrue);
    expect(owner.can(Session.viewClients), isTrue);
    expect(owner.can(Session.viewSettings), isFalse);
    expect(owner.showAdmin, isTrue);
  });
  test('old session without perms falls back to role', () {
    final old = Session(email: 'a', access: '', refresh: '', role: 'Администратор');
    expect(old.canRoute('/admin/staff'), isTrue);
    final oldCashier = Session(email: 'c', access: '', refresh: '', role: 'Кассир');
    expect(oldCashier.canRoute('/admin/staff'), isFalse);
    expect(oldCashier.canRoute('/stock'), isTrue);
  });
}
