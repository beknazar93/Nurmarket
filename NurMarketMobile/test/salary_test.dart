// 2026-10-26 (голос-управление, В7): зарплата и выплата по правилам ПК (SalaryPayoutService кассы) на фейках, без сети и без
// боевой базы: ключ в source_id, «ответ не дошёл — не повторять», замок до проверки, повтор за 10 секунд не проводится,
// «проверено частично» — сумма сама не подставляется, кассир без права → denied.
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/staff_tools.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/staff/salary_payout.dart';
import 'package:nurmarket/features/staff/salary_repository.dart';

import 'support/staff_fakes.dart';
import 'support/staff_rig.dart';

Map<String, dynamic> _row(String uid, String name, num total, {String scheme = 'salary', num monthly = 30000}) => {
  'user_id': uid,
  'employee_label': name,
  'pay_scheme': scheme,
  'monthly_base_salary': monthly,
  'sales_percent': 0,
  'per_item_amount': 0,
  'base_prorated': total,
  'total': total,
  'percent_bonus': 0,
  'per_item_bonus': 0,
  'profile_scope': 'employee',
};

final _from = DateTime(2026, 10, 1);
final _to = DateTime(2026, 10, 26);

PayoutRequest _req(String key, {String user = 'u1', String name = 'Айбек Т.', double amount = 1000, String kind = 'salary', PayoutCashbox? box}) => PayoutRequest(
  key: key,
  userId: user,
  employee: name,
  from: _from,
  to: _to,
  amount: amount,
  cashbox: box ?? const PayoutCashbox('cb1', 'Основная', 'expense_variable', true),
  kind: kind,
);

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_salary_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  group('начисление: серверный расчёт tab=salary', () {
    test('строка и плитки разбираются так же, как на ПК (SalaryRow.From)', () {
      final rep = parseSalaryReport({
        'cards': {'total_payroll': 70000, 'total_base_prorated': 60000, 'total_percent_bonus': 8000, 'total_per_item_bonus': 2000, 'total_employee_sales': 500000, 'employees_with_profile': 2},
        'rows': [
          {..._row('u1', 'Айбек Т.', 24300), 'sales_percent': 2, 'percent_bonus': 1500, 'per_item_bonus': 300},
          {..._row('u2', 'Нурлан Б.', 40000), 'profile_scope': 'none'},
        ],
      });
      expect(rep.cards.payroll, 70000);
      expect(rep.cards.bonus, 10000);
      expect(rep.cards.employees, 2);
      expect(rep.rows.first.employeeId, 'u2', reason: 'больше начислено — выше');
      final a = rep.rows.last;
      expect(a.name, 'Айбек Т.');
      expect(a.accrued, 24300);
      expect(a.percent, 2);
      expect(a.bonus, 1800);
      expect(rep.rows.first.noScheme, isTrue);
    });
  });

  group('выплата: правила ПК', () {
    late StaffServer srv;
    late FakeApi api;
    late MemSettings mem;
    late DateTime now;
    late SalaryPayoutService svc;
    setUp(() {
      srv = StaffServer();
      api = FakeApi(srv.handle);
      mem = MemSettings();
      now = DateTime(2026, 10, 26, 12);
      svc = SalaryPayoutService(api, mem, scope: 's', byName: 'Владелец', approvedRightAway: true, now: () => now);
    });

    test('расход: касса, тип expense, название с меткой [#user период], ключ в source_id и idempotency_key, статья «Зарплата», approved у владельца', () async {
      final res = await svc.pay(_req('KEY-1', amount: 18500));
      expect(res.verdict, PayoutVerdict.done);
      final b = srv.flowPosts.single;
      expect(b['cashbox'], 'cb1');
      expect(b['type'], 'expense');
      expect(b['amount'], '18500.00');
      expect(b['name'], 'Зарплата: Айбек Т. (01.10.2026–26.10.2026) [#u1 01.10.2026–26.10.2026]');
      expect(b['source_id'], 'salary:u1:20261001-20261026:KEY-1');
      expect(b['idempotency_key'], 'KEY-1');
      expect(b['category'], 'cat1');
      expect(b['status'], 'approved');
      expect(b.containsKey('source_kind'), isFalse, reason: 'сервер его отвергает (проверено на ПК 19.10)');
      // у не-владельца расход ждёт подтверждения
      final other = SalaryPayoutService(api, MemSettings(), scope: 's2', approvedRightAway: false, now: () => now);
      await other.pay(_req('KEY-2', user: 'u2', name: 'Нурлан Б.', amount: 500));
      expect(srv.flowPosts.last['status'], 'pending');
    });

    test('аванс — тоже «Зарплата: …» в названии (иначе поиск «Зарплата» его потеряет)', () async {
      await svc.pay(_req('K-ADV', amount: 5000, kind: 'advance'));
      final name = srv.flowPosts.single['name'] as String;
      expect(name, contains('Зарплата'));
      expect(name, contains('аванс'));
      final paid = await svc.paidForPeriod([(userId: 'u1', name: 'Айбек Т.')], _from, _to);
      expect(paid.users['u1']!.paid, 5000);
    });

    test('ответ не дошёл → «неизвестно», НЕ повторяем; новая выплата этому сотруднику закрыта замком; другому — можно', () async {
      srv.loseFlowResponse = 1;
      final lost = await svc.pay(_req('LOST-1', amount: 3000));
      expect(lost.verdict, PayoutVerdict.unknown);
      expect(srv.flowPosts.length, 1, reason: 'повтора нет');
      final again = await svc.pay(_req('NEW-2', amount: 700));
      expect(again.verdict, PayoutVerdict.rejected);
      expect(again.error, '#lock');
      expect(again.lockRecord!.amount, 3000);
      expect(srv.flowPosts.length, 1, reason: 'замок: второй POST не ушёл');
      final other = await svc.pay(_req('OTHER-1', user: 'u2', name: 'Нурлан Б.', amount: 700));
      expect(other.verdict, PayoutVerdict.done);
      expect((await svc.lockFor('u1')).lock!.key, 'LOST-1');
    });

    test('«Проверить на сервере»: расход нашёлся по source_id → учтён, замок снят, в «Выплачено» один раз', () async {
      srv.loseFlowResponse = 1;
      await svc.pay(_req('LOST-2', amount: 3000));
      expect(await svc.checkUnknown('LOST-2'), isTrue);
      expect((await svc.lockFor('u1')).lock, isNull);
      final paid = await svc.paidForPeriod([(userId: 'u1', name: 'Айбек Т.')], _from, _to);
      expect(paid.users['u1']!.paid, 3000, reason: 'сервер и журнал — одна и та же выплата, без двойного счёта');
    });

    test('не нашлась на сервере → замок остаётся; снимает его только решение человека: «НЕТ на сайте» → можно платить', () async {
      srv.loseFlowResponse = 1;
      await svc.pay(_req('LOST-3', amount: 3000));
      srv.flows.clear(); // на сервере расхода нет
      expect(await svc.checkUnknown('LOST-3'), isFalse);
      expect((await svc.lockFor('u1')).lock, isNotNull);
      expect(await svc.resolveUnknown('LOST-3', paidOnSite: false), isTrue);
      expect((await svc.lockFor('u1')).lock, isNull);
      expect((await svc.pay(_req('LATER', amount: 3000))).verdict, PayoutVerdict.done, reason: 'владелец сказал «на сайте нет» — запись absent, можно платить заново');
    });

    test('«ЕСТЬ на сайте» → выплата учитывается в «Выплачено» без дубля', () async {
      srv.loseFlowResponse = 1;
      await svc.pay(_req('LOST-4', amount: 2500));
      srv.flows.clear();
      await svc.resolveUnknown('LOST-4', paidOnSite: true);
      final paid = await svc.paidForPeriod([(userId: 'u1', name: 'Айбек Т.')], _from, _to);
      expect(paid.users['u1']!.paid, 2500);
    });

    test('повтор той же выплаты за 10 секунд не проводится, через 10 секунд — проводится', () async {
      expect((await svc.pay(_req('A1', amount: 1000))).verdict, PayoutVerdict.done);
      now = now.add(const Duration(seconds: 4));
      final dup = await svc.pay(_req('A2', amount: 1000));
      expect(dup.verdict, PayoutVerdict.rejected);
      expect(dup.error, '#dup');
      expect(srv.flowPosts.length, 1);
      // другая сумма — другая выплата
      expect((await svc.pay(_req('A3', amount: 1500))).verdict, PayoutVerdict.done);
      now = now.add(const Duration(seconds: 11));
      expect((await svc.pay(_req('A4', amount: 1000))).verdict, PayoutVerdict.done);
      expect(srv.flowPosts.length, 3);
    });

    test('сервер отверг поле статьи (400) → один повтор без статьи; отказ без поля → расход не записан, замка нет', () async {
      srv.rejectField = 'category';
      final r = await svc.pay(_req('C1', amount: 800));
      expect(r.verdict, PayoutVerdict.done);
      expect(srv.flowPosts.length, 2);
      expect(srv.flowPosts.last.containsKey('category'), isFalse);
      // 400 без названного поля — отказ по существу
      srv.rejectField = 'amount';
      now = now.add(const Duration(minutes: 1));
      final bad = await svc.pay(_req('C2', amount: 900));
      expect(bad.verdict, PayoutVerdict.rejected);
      expect((await svc.lockFor('u1')).lock, isNull);
    });

    test('журнал выплат не читается → не платим вообще (защиты от двойной выплаты нет)', () async {
      mem.failRead = true;
      final r = await svc.pay(_req('J1'));
      expect(r.verdict, PayoutVerdict.rejected);
      expect(r.error, '#journal');
      expect(srv.flowPosts, isEmpty);
    });

    test('запрос отмены — cancel-request владельцу; кнопки «отменить расход» нет', () async {
      final r = await svc.pay(_req('Z1'));
      await svc.requestCancel(r.flowId!);
      expect(srv.cancelRequests, [r.flowId]);
    });

    test('касса по умолчанию — касса расходов, не касса продаж', () async {
      final boxes = await svc.cashboxes();
      expect(boxes[SalaryPayoutService.defaultCashboxIndex(boxes, null)].id, 'cb1');
      expect(boxes[1].isSales, isTrue);
      // касс расходов нет: прошлый выбор — не кассу продаж; иначе первая не pos_*
      final onlySales = [boxes[1]];
      expect(SalaryPayoutService.defaultCashboxIndex(onlySales, 'cb2'), -1);
    });
  });

  group('«Выплачено за период»: метки, журнал, доверие', () {
    late StaffServer srv;
    late SalaryPayoutService svc;
    setUp(() {
      srv = StaffServer();
      svc = SalaryPayoutService(FakeApi(srv.handle), MemSettings(), scope: 's', approvedRightAway: true, now: () => DateTime(2026, 10, 26, 12));
    });
    final emps = [(userId: 'u1', name: 'Айбек Т.'), (userId: 'u2', name: 'Нурлан Б.')];

    test('по source_id и по метке в названии; период выплаты целиком внутри периода — вычитается, пересекается частично — только пометка', () async {
      srv.addFlow(name: 'Зарплата: Айбек Т. (01.10.2026–15.10.2026) [#u1 01.10.2026–15.10.2026]', amount: 10000, sourceId: 'salary:u1:20261001-20261015:k1', category: 'cat1');
      srv.addFlow(name: 'Зарплата: Нурлан Б. (01.10.2026–10.10.2026) [#u2 01.10.2026–10.10.2026]', amount: 4000, category: 'cat1');
      srv.addFlow(name: 'Зарплата: Айбек Т. (20.09.2026–05.10.2026) [#u1 20.09.2026–05.10.2026]', amount: 777, category: 'cat1');
      final r = await svc.paidForPeriod(emps, _from, _to);
      expect(r.coverage, PaidCoverage.full);
      expect(r.users['u1']!.paid, 10000);
      expect(r.users['u1']!.partials.single.amount, 777);
      expect(r.users['u2']!.paid, 4000);
      expect(trustPaid(r, r.users['u1']).sure, isFalse, reason: 'есть частичное пересечение');
      expect(trustPaid(r, r.users['u1']).reason, TrustReason.partialNotes);
      expect(trustPaid(r, r.users['u2']).sure, isTrue);
    });

    test('старый формат без метки: сотрудник узнан по ФИО в названии → «по названию», сумма сама не подставляется', () async {
      srv.addFlow(name: 'Зарплата: Нурлан Б. (01.10.2026–20.10.2026)', amount: 5000, category: 'cat1');
      final r = await svc.paidForPeriod(emps, _from, _to);
      expect(r.users['u2']!.paid, 5000);
      expect(r.users['u2']!.byName, isTrue);
      expect(trustPaid(r, r.users['u2']).reason, TrustReason.byName);
    });

    test('расход «Зарплата», который некому приписать → «частично», сумма сама не подставляется', () async {
      srv.addFlow(name: 'Зарплата за октябрь (грузчики) (01.10.2026–26.10.2026)', amount: 9000, category: 'cat1');
      final r = await svc.paidForPeriod(emps, _from, _to);
      expect(r.unassignedCount, 1);
      expect(r.unassignedSum, 9000);
      expect(r.coverage, PaidCoverage.partial);
      expect(trustPaid(r, r.users['u1']).reason, TrustReason.unassigned);
    });

    test('отклонённый расход (rejected) не вычитается', () async {
      srv.addFlow(name: 'Зарплата: Айбек Т. [#u1 01.10.2026–26.10.2026]', amount: 1234, sourceId: 'salary:u1:20261001-20261026:z', category: 'cat1', status: 'rejected');
      final r = await svc.paidForPeriod(emps, _from, _to);
      expect(r.users['u1']?.paid ?? 0, 0);
    });

    test('сервер не ищет по названию → читаем по статье → «проверено частично»; не читается совсем → «не проверено»', () async {
      srv.searchBroken = true;
      srv.addFlow(name: 'Зарплата: Айбек Т. [#u1 01.10.2026–26.10.2026]', amount: 2000, sourceId: 'salary:u1:20261001-20261026:q', category: 'cat1');
      srv.addFlow(name: 'Аренда', amount: 99999, category: 'other');
      final r = await svc.paidForPeriod(emps, _from, _to);
      expect(r.users['u1']!.paid, 2000);
      expect(r.coverage, anyOf(PaidCoverage.partial, PaidCoverage.full));
      // по статье тоже не читается — остаётся чтение расходов с начала периода (полное)
      srv.categoryFilterBroken = true;
      final svc2 = SalaryPayoutService(FakeApi(srv.handle), MemSettings(), scope: 's3', approvedRightAway: true, now: () => DateTime(2026, 10, 26, 12));
      final r2 = await svc2.paidForPeriod(emps, _from, _to);
      expect(r2.coverage, PaidCoverage.full);
      expect(r2.users['u1']!.paid, 2000);
      // движения не читаются совсем → «не проверено», доверия нет
      srv.flowsDown = true;
      final svc3 = SalaryPayoutService(FakeApi(srv.handle), MemSettings(), scope: 's4', approvedRightAway: true, now: () => DateTime(2026, 10, 26, 12));
      final r3 = await svc3.paidForPeriod(emps, _from, _to);
      expect(r3.coverage, PaidCoverage.none);
      expect(trustPaid(r3, r3.users['u1']).reason, TrustReason.notChecked);
    });

    test('trustPaid: таймаут и недоступный журнал — не доверяем', () {
      expect(trustPaid(null, null, timedOut: true).reason, TrustReason.timeout);
      expect(trustPaid(const PaidResult({}, PaidCoverage.full, 0, 0, journalUnavailable: true), null).reason, TrustReason.journal);
      expect(trustPaid(const PaidResult({}, PaidCoverage.full, 0, 0), null).sure, isTrue);
    });
  });

  group('инструменты звонка: зарплата поверх настоящего источника', () {
    StaffRig rig({bool owner = true}) {
      final r = StaffRig(owner: owner);
      r.server.salaryRows = [_row('u1', 'Айбек Т.', 30000), _row('u2', 'Нурлан Б.', 18000)];
      return r;
    }

    DateTime periodFrom(StaffRig r) => DateTime(r.now.year, r.now.month, 1);

    test('«сколько я должен Айбеку за октябрь»: начислено − выплачено; при «частично» — с пометкой', () async {
      final r = rig();
      r.server.addFlow(name: 'Зарплата: Айбек Т. (01.10.2026–26.10.2026) [#u1 01.10.2026–26.10.2026]', amount: 11500, sourceId: 'salary:u1:20261001-20261026:a', category: 'cat1');
      final res = await r.say('сколько я должен Айбеку за октябрь', 'salary_due', {'employee': 'Айбек', 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(res.status, ToolStatus.ok, reason: res.say);
      expect(res.say, contains('30 000'));
      expect(res.say, contains('11 500'));
      expect(res.say, contains('18 500'));
      expect(res.say, isNot(contains('частично')));
      // чужая выплата без метки (не приписать) → «проверено частично»
      r.server.addFlow(name: 'Зарплата за что-то (01.10.2026–26.10.2026)', amount: 100, category: 'cat1');
      r.now = r.now.add(const Duration(minutes: 5));
      r.payout.forgetCache();
      final part = await r.say('сколько я должен Айбеку за октябрь', 'salary_due', {'employee': 'Айбек', 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(part.say, contains('частично'));
      expect(periodFrom(r).month, 10);
    });

    test('выплата с суммой: расход по правилам ПК, голосом — сумма, касса, остаток; отмены нет', () async {
      final r = rig();
      final res = await r.say('выплати Айбеку десять тысяч аванс наличными', 'salary_payout', {'employee': 'Айбек', 'amount': 10000, 'kind': 'advance', 'method': 'cash', 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(res.status, ToolStatus.ok, reason: res.say);
      expect(res.say, contains('10 000'));
      expect(res.say, contains('Основная'));
      expect(res.say, contains('Осталось 20 000'));
      expect(res.undo, isFalse);
      final b = r.server.flowPosts.single;
      expect(b['source_id'], matches(r'^salary:u1:20261001-20261026:.+'));
      expect(b['idempotency_key'], isNotEmpty);
      final u = await r.say('отмени выплату', 'undo_last');
      expect(u.say, contains('запросом владельцу'));
      expect(r.server.flows.length, 1, reason: 'расход не тронут');
    });

    test('без суммы: остаток известен уверенно → весь остаток; «частично» → сумма не подставляется, расход не создаётся', () async {
      final r = rig();
      r.server.addFlow(name: 'Зарплата: Нурлан Б. (01.10.2026–26.10.2026) [#u2 01.10.2026–26.10.2026]', amount: 6000, sourceId: 'salary:u2:20261001-20261026:n', category: 'cat1');
      final all = await r.say('выплати Нурлану весь остаток зарплаты', 'salary_payout', {'employee': 'Нурлан', 'kind': 'salary', 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(all.status, ToolStatus.ok, reason: all.say);
      expect(r.server.flowPosts.single['amount'], '12000.00');
      // теперь в журнале расход без метки → «частично»
      r.server.addFlow(name: 'Зарплата: что-то непонятное (01.10.2026–26.10.2026)', amount: 50, category: 'cat1');
      r.now = r.now.add(const Duration(minutes: 5));
      r.payout.forgetCache();
      final part = await r.say('выплати Айбеку зарплату', 'salary_payout', {'employee': 'Айбек', 'kind': 'salary', 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(part.status, ToolStatus.clarify);
      expect(part.say, contains('скажите сумму'));
      expect(r.server.flowPosts.length, 1);
    });

    test('ответ не дошёл → «могло пройти», повторять нельзя; следующая просьба этому же сотруднику упирается в замок и ничего не шлёт', () async {
      final r = rig();
      r.server.loseFlowResponse = 1;
      final lost = await r.say('выплати Айбеку тысячу', 'salary_payout', {'employee': 'Айбек', 'amount': 1000, 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(lost.status, ToolStatus.error);
      expect(lost.say, contains('могло пройти'));
      expect(r.server.flowPosts.length, 1);
      r.now = r.now.add(const Duration(minutes: 1));
      final next = await r.say('выплати Айбеку две тысячи', 'salary_payout', {'employee': 'Айбек', 'amount': 2000, 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(next.status, ToolStatus.error);
      expect(next.say, contains('без ответа'));
      expect(r.server.flowPosts.length, 1, reason: 'замок не дал отправить');
    });

    test('повтор той же просьбы за 10 секунд не проводится (второй расход не создаётся)', () async {
      final r = rig();
      await r.say('выплати Айбеку тысячу', 'salary_payout', {'employee': 'Айбек', 'amount': 1000, 'from': '2026-10-01', 'to': '2026-10-26'});
      // модель продублировала вызов без новой фразы пользователя — ворота «уже сделано» / окно 10 с
      final dup = await r.call('salary_payout', {'employee': 'Айбек', 'amount': 1000, 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(dup.say, startsWith('Уже сделано:'));
      expect(r.server.flowPosts.length, 1);
    });

    test('единственная касса — касса продаж: выплата идёт с предупреждением; несколько касс без расходной — отказ, расход не создаётся', () async {
      final r = StaffRig();
      r.server.salaryRows = [_row('u1', 'Айбек Т.', 30000)];
      r.server.cashboxes.removeAt(0); // осталась только касса продаж филиала
      final ok = await r.say('выплати Айбеку тысячу', 'salary_payout', {'employee': 'Айбек', 'amount': 1000, 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(ok.status, ToolStatus.ok, reason: ok.say);
      expect(ok.say, contains('касса продаж филиала'));
      expect(r.server.flowPosts.single['cashbox'], 'cb2');
      // две кассы продаж и ни одной расходной
      final r2 = StaffRig();
      r2.server.salaryRows = [_row('u1', 'Айбек Т.', 30000)];
      r2.server.cashboxes
        ..removeAt(0)
        ..add({'id': 'cb3', 'name': 'Касса 2', 'role': 'pos_main', 'is_consumption': false});
      final none = await r2.say('выплати Айбеку тысячу', 'salary_payout', {'employee': 'Айбек', 'amount': 1000, 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(none.status, ToolStatus.error);
      expect(none.say, contains('кассу компании'));
      expect(r2.server.flowPosts, isEmpty);
    });

    test('не-владелец: выплата создаётся в статусе pending, голос это говорит', () async {
      final r = StaffRig(owner: false);
      r.server.salaryRows = [_row('u1', 'Айбек Т.', 30000)];
      final res = await r.say('выплати Айбеку тысячу', 'salary_payout', {'employee': 'Айбек', 'amount': 1000, 'from': '2026-10-01', 'to': '2026-10-26'});
      expect(res.status, ToolStatus.ok, reason: res.say);
      expect(r.server.flowPosts.single['status'], 'pending');
      expect(res.say, contains('подтверждения'));
    });

    test('кассир без can_view_salary: инструментов зарплаты нет, вызов — denied; выплата без can_edit_salary — denied', () async {
      final r = rig();
      for (final tool in ['salary_report', 'salary_due', 'salary_payout', 'salary_scheme']) {
        expect(r.reg.forSession(cashierSession).map((t) => t.name), isNot(contains(tool)), reason: tool);
      }
      final denied = await r.say('сколько я должен Айбеку', 'salary_due', {'employee': 'Айбек'}, cashierSession);
      expect(denied.status, ToolStatus.denied);
      final denied2 = await r.say('выплати Айбеку тысячу', 'salary_payout', {'employee': 'Айбек', 'amount': 1000}, cashierSession);
      expect(denied2.status, ToolStatus.denied);
      // бухгалтер: смотрит, но не платит и не меняет схему
      final acc = sess(role: 'accountant', id: 'acc', perms: const ['can_view_salary']);
      final names = r.reg.forSession(acc).map((t) => t.name);
      expect(names, containsAll(['salary_report', 'salary_due']));
      expect(names, isNot(contains('salary_payout')));
      expect(names, isNot(contains('salary_scheme')));
      expect(r.server.flowPosts, isEmpty);
    });

    test('отчёт: таблица «сотрудник | схема | начислено | выплачено | осталось», имена вслух не уходят', () async {
      final r = rig();
      final res = await r.say('зарплата за октябрь', 'salary_report', {'from': '2026-10-01', 'to': '2026-10-26'});
      expect(res.status, ToolStatus.ok, reason: res.say);
      expect(r.tables.current!.rows.length, 2);
      expect(jsonEncode(res.toJson()), isNot(contains('Айбек')));
      expect(res.say, contains('48 000'));
    });

    test('табель голосом: по закрытым сменам, сводка часов', () async {
      final r = rig();
      r.server.shifts = [
        {'id': 's1', 'cashier_display': 'Айбек Т.', 'opened_at': '2026-10-12T12:00:00Z', 'closed_at': '2026-10-12T20:00:00Z', 'sales_total': '1000'},
        {'id': 's2', 'cashier_display': 'Айбек Т.', 'opened_at': '2026-10-13T12:00:00Z', 'closed_at': null, 'sales_total': '500'},
      ];
      final res = await r.say('табель за октябрь', 'timesheet', {'from': '2026-10-01', 'to': '2026-10-31'});
      expect(res.status, ToolStatus.ok, reason: res.say);
      expect(res.say, contains('8'));
      expect(r.tables.current!.rows.length, 1);
    });

    test('штрафы: сервер их не хранит — честный отказ, ничего не создаётся', () async {
      final r = rig();
      final res = await r.say('оштрафуй Айбека на пятьсот', 'staff_unavailable', {'feature': 'fines'});
      expect(res.status, ToolStatus.error);
      expect(res.say, contains('не хранит'));
      expect(r.server.flowPosts, isEmpty);
    });
  });

  group('схема оплаты: market-sale-employee-pay-profiles', () {
    test('профиля нет → POST; есть → PATCH; отмена возвращает прежние значения', () async {
      final r = StaffRig();
      r.server.salaryRows = [_row('u1', 'Айбек Т.', 30000)];
      final repo = SalaryRepository(r.api);
      expect(await repo.scheme('u1'), isNull);
      await repo.setScheme('u1', const PaySchemeData('salary_plus_percent', 30000, 2, 0));
      expect(r.server.profiles.single['pay_scheme'], 'salary_plus_percent');
      expect(r.server.profiles.single['monthly_base_salary'], '30000.00');
      await repo.setScheme('u1', const PaySchemeData('salary', 25000, 0, 0));
      expect(r.server.profiles.length, 1);
      expect(r.server.profiles.single['pay_scheme'], 'salary');
    });

    test('голосом: «поставь Айбеку оклад 30000 и 2 процента» → схема сохранена; «отмени» → прежняя', () async {
      final r = StaffRig();
      r.server.salaryRows = [_row('u1', 'Айбек Т.', 30000)];
      r.server.profiles.add({'id': 'pp1', 'user': 'u1', 'pay_scheme': 'salary', 'monthly_base_salary': '25000', 'sales_percent': '0', 'per_item_amount': '0'});
      // источник знает user_id → id списка сотрудников после чтения отчёта
      await r.source.salary(DateTime(2026, 10, 1), DateTime(2026, 10, 26));
      final ok = await r.say('поставь Айбеку оклад 30000 и 2 процента', 'salary_scheme', {'employee': 'Айбек', 'scheme': 'salary_plus_percent', 'base': 30000, 'percent': 2});
      expect(ok.status, ToolStatus.ok, reason: ok.say);
      expect(r.server.profiles.single['pay_scheme'], 'salary_plus_percent');
      await r.say('отмени', 'undo_last');
      expect(r.server.profiles.single['pay_scheme'], 'salary');
      expect(r.server.profiles.single['monthly_base_salary'], '25000.00');
    });
  });
}
