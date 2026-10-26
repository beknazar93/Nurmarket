// 2026-10-26 (живой аудит, П5/П6): квитанция об оплате долга, «Повторить» без связи, разбивка смены (возвраты, оплата долгов,
// внесения/изъятия → «Должно быть в кассе»), X-отчёт и Z-итог. Сервер NurCRM не трогается — всё на фейках.
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/ops/ops_docs.dart';
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/features/ops/ops_screens.dart';
import 'package:nurmarket/features/shift/shift_doc.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';
import 'package:nurmarket/l10n/app_localizations_ru.dart';

typedef _H = Future<dynamic> Function(String method, String path, Map<String, dynamic>? query);

class _Api extends ApiClient {
  _Api(this.h) : super(SessionStore());
  final _H h;
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => h('GET', path, query);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => h('POST', path, null);
}

class _Session extends SessionController {
  @override
  Session? build() => Session(email: 'a@x.kg', access: 'a', refresh: 'r', userId: 'u1', cashboxId: 'box1', cashboxName: 'Касса 2');
}

class _Ops extends OpsRepository {
  _Ops(super.api, super.db);
  bool offline = false;
  final paid = <double>[];
  @override
  Future<List<Debtor>> debtors() async {
    if (offline) throw ApiException('Нет связи с сервером.');
    return [const Debtor('c1', 'Бакыт', '0555', 800, 2)];
  }

  @override
  Future<Map<String, dynamic>> payDebt({required String clientId, required double amount, required String method, String? shiftId}) async {
    paid.add(amount);
    return {};
  }
}

ShiftInfo _shift({double opening = 100, double cash = 500, double debt = 200, double expected = 0}) => ShiftInfo(
  id: 'sh1',
  cashboxId: 'box1',
  cashierId: 'u1',
  openedAt: DateTime(2026, 10, 26, 9),
  openingCash: opening,
  cashSales: cash,
  nonCashSales: 50,
  debtSales: debt,
  salesTotal: cash + 50 + debt,
  expectedCash: expected,
  salesCount: 4,
);

Map<String, dynamic> _flow(String kind, String type, num amount, {String pm = 'cash'}) => {'source_kind': kind, 'type': type, 'amount': '$amount', 'payment_method': pm};

void main() {
  late AppDatabase db;
  final l = AppLocalizationsRu();
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_p56_'));
  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  group('П6 разбивка смены', () {
    test('сумма строк = «Должно быть» сервера', () {
      // 100 + 500 − 30 (возврат нал) + 50 (оплата долга нал) + 20 (внесение) − 10 (изъятие) = 630
      final b = ShiftBreakdown.build(
        shift: _shift(expected: 630),
        report: {'expected_cash': '630.00', 'debt_payments_cash': '50.00', 'debt_prepayments_cash': '0'},
        flows: [_flow('shift_drawer_inflow', 'income', 20), _flow('shift_drawer_outflow', 'expense', 10), _flow('pos_sale_return', 'expense', 30)],
        returns: (total: 30, count: 1),
      );
      expect(b.inflow, 20);
      expect(b.outflow, 10);
      expect(b.returnsCash, 30);
      expect(b.calcExpected, 630);
      expect(b.matches, isTrue);
    });

    test('расхождение с сервером — видно обе цифры и разница', () {
      final b = ShiftBreakdown.build(
        shift: _shift(expected: 640),
        report: {'expected_cash': '640', 'debt_payments_cash': '50'},
        flows: [_flow('shift_drawer_inflow', 'income', 20), _flow('shift_drawer_outflow', 'expense', 10), _flow('pos_sale_return', 'expense', 30)],
        returns: (total: 30, count: 1),
      );
      expect(b.matches, isFalse);
      expect(b.diff, -10);
      final text = shiftTapeDoc(l, null, _shift(expected: 640), b).toText();
      expect(text, contains('Должно быть в кассе: 640 сом'));
      expect(text, contains('Расчёт по строкам: 630 сом'));
      expect(text, contains('расходится с сервером на 10 сом'));
    });

    test('чего сервер не прислал — не выдумывается: расчёта нет, честная пометка', () {
      final b = ShiftBreakdown.build(shift: _shift(expected: 600), report: null, flows: null, returns: null, partial: true);
      expect(b.calcExpected, isNull);
      expect(b.serverExpected, 600);
      final text = shiftTapeDoc(l, null, _shift(expected: 600), b).toText();
      expect(text, contains('Возвраты смены не загружены'));
      expect(text, contains('Должно быть в кассе: 600 сом'));
      expect(text, contains('Часть данных не загрузилась'));
    });

    test('оплата долгов неизвестна — расчёта по строкам нет', () {
      final b = ShiftBreakdown.build(shift: _shift(expected: 600), report: {'expected_cash': '600'}, flows: [], returns: (total: 0, count: 0));
      expect(b.returnsCash, 0);
      expect(b.debtPaymentsCash, isNull);
      expect(b.calcExpected, isNull);
    });

    test('Z-итог и X-отчёт: возвраты, оплата долгов, внесения, пересчёт и разница', () {
      final b = ShiftBreakdown.build(
        shift: _shift(expected: 630),
        report: {'expected_cash': '630', 'debt_payments_cash': '50', 'debt_prepayments_cash': '25'},
        flows: [_flow('shift_drawer_inflow', 'income', 20), _flow('shift_drawer_outflow', 'expense', 10), _flow('pos_sale_return', 'expense', 30)],
        returns: (total: 30, count: 1),
      );
      final z = shiftTapeDoc(l, null, _shift(expected: 630), b, z: true, counted: 620, closedAt: DateTime(2026, 10, 26, 18)).toText();
      expect(z, contains('Z-итог смены'));
      expect(z, contains('Возвраты · 1 возврат: −30 сом'));
      expect(z, contains('Оплата долгов: +50 сом'));
      expect(z, contains('Внесение: +20 сом'));
      expect(z, contains('Изъятие: −10 сом'));
      expect(z, contains('Пересчитано: 620 сом'));
      expect(z, contains('Разница: −10 сом'));
      expect(z, isNot(contains('промежуточный')));
      final x = shiftTapeDoc(l, null, _shift(expected: 630), b).toText();
      expect(x, contains('X-отчёт'));
      expect(x, contains('промежуточный'));
    });

    test('лента печатается на принтер (ESC/POS) в ширину ленты', () {
      final b = ShiftBreakdown.build(shift: _shift(expected: 600));
      final doc = shiftTapeDoc(l, null, _shift(expected: 600), b);
      expect(doc.toEscPos(paperMm: 58).length, greaterThan(100));
      expect(doc.toPrintLines(32).every((x) => x.text.length <= 32), isTrue);
    });

    test('возвраты смены: если поле shift пусто у всех — по кассиру и времени', () async {
      final api = _Api((m, p, q) async {
        expect(p, 'api/main/pos/returns/');
        final now = DateTime.now().toIso8601String();
        return [
          {'id': 'r1', 'amount': '30.00', 'shift': null, 'cashier': 'u1', 'created_at': now},
          {'id': 'r2', 'amount': '70.00', 'shift': null, 'cashier': 'other', 'created_at': now},
        ];
      });
      final s = ShiftInfo(id: 'sh1', cashierId: 'u1', openedAt: DateTime.now().subtract(const Duration(hours: 2)));
      final r = await ShiftRepository(api).returns(s);
      expect(r!.count, 1);
      expect(r.total, 30);
    });

    test('breakdown: нет связи с одним запросом — остальные цифры есть, partial = true', () async {
      final api = _Api((m, p, q) async {
        if (p.endsWith('/report/')) return {'sales_total': '750', 'expected_cash': '600', 'debt_payments_cash': '0'};
        if (p == 'api/construction/cashflows/') {
          return {
            'results': [_flow('shift_drawer_inflow', 'income', 20)],
          };
        }
        throw ApiException('Нет связи с сервером.');
      });
      final b = await ShiftRepository(api).breakdown(_shift(expected: 600));
      expect(b.partial, isTrue);
      expect(b.inflow, 20);
      expect(b.returnsTotal, isNull);
    });
  });

  group('П5 квитанция и «Повторить»', () {
    test('квитанция: принято, остаток; долг погашен — без остатка', () {
      final d = debtReceiptDoc(l, null, clientName: 'Бакыт', debtBefore: 800, accepted: 500, method: 'cash', change: 0);
      final t = d.toText();
      expect(t, contains('Квитанция об оплате долга'));
      expect(t, contains('Принято: 500 сом'));
      expect(t, contains('Останется долга: 300 сом'));
      expect(debtLeftAfter(800, 800), 0);
      expect(debtReceiptDoc(l, null, clientName: 'Б', debtBefore: 800, accepted: 800, method: 'cash').toText(), contains('Долг погашен полностью'));
    });

    Widget app(_Ops ops, ShiftRepository shifts) => ProviderScope(
      overrides: [
        databaseProvider.overrideWithValue(db),
        sessionProvider.overrideWith(_Session.new),
        opsRepoProvider.overrideWithValue(ops),
        shiftRepoProvider.overrideWithValue(shifts),
      ],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: const Locale('ru'),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: const DebtsScreen(),
      ),
    );

    testWidgets('оплата долга → квитанция «Принято N, остаток M»', (t) async {
      final api = _Api((m, p, q) async => []);
      final ops = _Ops(api, db);
      await t.pumpWidget(app(ops, ShiftRepository(api)));
      await t.pumpAndSettle();
      await t.tap(find.text('Бакыт'));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField).last, '500');
      await t.pump();
      await t.tap(find.text('Принять оплату долга').last);
      await t.pumpAndSettle();
      expect(ops.paid, [500]);
      expect(find.text('Квитанция об оплате долга'), findsWidgets);
      expect(find.textContaining('Принято 500 сом, остаток 300 сом'), findsOneWidget);
      await t.scrollUntilVisible(find.text('PDF'), 200, scrollable: find.byType(Scrollable).first);
      expect(find.text('PDF'), findsOneWidget);
    });

    testWidgets('нет связи: «Повторить» и последний сохранённый список', (t) async {
      final api = _Api((m, p, q) async => []);
      final ops = _Ops(api, db);
      await t.pumpWidget(app(ops, ShiftRepository(api)));
      await t.pumpAndSettle();
      expect(find.text('Бакыт'), findsOneWidget);
      // связь пропала: открываем экран заново — список из памяти и «Повторить»
      ops.offline = true;
      await t.pumpWidget(const SizedBox());
      await t.pumpWidget(app(ops, ShiftRepository(api)));
      await t.pumpAndSettle();
      expect(find.text('Бакыт'), findsOneWidget);
      expect(find.text('Повторить'), findsOneWidget);
      // связь вернулась
      ops.offline = false;
      await t.tap(find.text('Повторить'));
      await t.pumpAndSettle();
      expect(find.text('Повторить'), findsNothing);
    });

    testWidgets('нет связи и нет сохранённого списка: сообщение и «Повторить»', (t) async {
      final api = _Api((m, p, q) async => []);
      final ops = _Ops(api, db)..offline = true;
      await t.pumpWidget(app(ops, ShiftRepository(api)));
      await t.pumpAndSettle();
      expect(find.textContaining('Не удалось загрузить долги'), findsOneWidget);
      expect(find.text('Повторить'), findsOneWidget);
    });
  });
}
