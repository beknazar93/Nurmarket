// 18.10: «Доступ к долгу» на телефоне — те же правила, что у кассы (DebtAccess.CheckSale): своё правило важнее общего,
// запрет, лимит чеков и сом в день, владелец и право настроек — без ограничений, испорченные данные не роняют продажу.
import 'package:drift/native.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/payment/debt_access.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

void main() {
  late AppDatabase db;
  late DebtAccessRepository repo;
  final l = lookupAppLocalizations(const Locale('ru'));
  Session cashier(String id) => Session(email: 'c@x', access: 'a', refresh: 'r', userId: id, role: 'cashier', perms: const []);

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    repo = DebtAccessRepository(ApiClient(SessionStore()), db);
  });
  tearDown(() => db.close());

  test('без правил — можно', () async {
    expect(await repo.check(l, cashier('u1'), const [], 1000000), isNull);
  });

  test('запрет одному кассиру не трогает другого; своё правило важнее общего', () async {
    final rules = [DebtRule(user: 'u1', mode: DebtRule.deny)];
    expect(await repo.check(l, cashier('u1'), rules, 1), l.debtDenied);
    expect(await repo.check(l, cashier('u2'), rules, 1), isNull);
    final rules2 = [DebtRule(mode: DebtRule.deny), DebtRule(user: 'u2', mode: DebtRule.allow)];
    expect(await repo.check(l, cashier('u2'), rules2, 5), isNull);
    expect(await repo.check(l, cashier('u3'), rules2, 5), isNotNull);
  });

  test('владелец и право настроек — без ограничений', () async {
    final rules = [DebtRule(mode: DebtRule.deny)];
    final owner = Session(email: 'o@x', access: 'a', refresh: 'r', userId: 'o', isOwner: true);
    final admin = Session(email: 'a@x', access: 'a', refresh: 'r', userId: 'a', perms: const [Session.viewSettings]);
    expect(await repo.check(l, owner, rules, 5), isNull);
    expect(await repo.check(l, admin, rules, 5), isNull);
  });

  test('лимит 3 чека в день: 4-й запрещён', () async {
    final rules = [DebtRule(user: 'u3', mode: DebtRule.limit, maxReceipts: 3)];
    for (var i = 0; i < 3; i++) {
      expect(await repo.check(l, cashier('u3'), rules, 100), isNull);
      await repo.recordSale('u3', 100);
    }
    expect(await repo.check(l, cashier('u3'), rules, 100), l.debtLimitReceipts(3, 3));
    expect(await repo.usedToday('u3'), (3, 300.0));
  });

  test('лимит 1000 сом: 600 + 400 можно, 600 + 400,01 нельзя; подсказка кассиру', () async {
    final rules = [DebtRule(user: 'u3', mode: DebtRule.limit, maxSum: 1000)];
    await repo.recordSale('u3', 600);
    expect(await repo.check(l, cashier('u3'), rules, 400), isNull);
    expect(await repo.check(l, cashier('u3'), rules, 400.01), isNotNull);
    expect(await repo.hint(l, cashier('u3'), rules), contains('1'));
    expect(await repo.hint(l, cashier('u9'), rules), '');
  });

  test('правила из настроек компании: странные значения — «можно», без падения', () {
    final rules = DebtAccessRepository.decode('[{"user":5,"mode":"???","max_sum":"abc"},{"user":"u1","mode":"limit","max_receipts":"2","max_sum":"500.5"}]');
    expect(rules[0].mode, DebtRule.allow);
    expect(rules[1].maxReceipts, 2);
    expect(rules[1].maxSum, 500.5);
    expect(DebtAccessRepository.decode('{мусор'), isEmpty);
  });

  test('испорченный счёт дня — день с нуля', () async {
    await db.setSetting('debt.usage', '{мусор');
    expect(await repo.usedToday('u1'), (0, 0.0));
    await repo.recordSale('u1', 50);
    expect(await repo.usedToday('u1'), (1, 50.0));
  });
}
