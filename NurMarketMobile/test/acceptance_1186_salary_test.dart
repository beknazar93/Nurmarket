// 2026-10-26 (приёмка 1.18.6): «Зарплата» — суммы и заголовки не рвутся посреди числа при шрифте 1,3 на 360 dp (В-3н, ru и ky),
// замок «выплата без ответа» виден в списке: значок, подпись и «Проверить» вместо «Выплатить» (С-5н), в окне выплаты касса выбрана
// заранее, если она единственная, а без выбора — «Выберите кассу», а не «не нашла кассу» (С-4н). Сервер — в памяти, без сети.
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart' show money;
import 'package:nurmarket/features/staff/salary_payout.dart' show PayoutCashbox;
import 'package:nurmarket/features/staff/salary_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/staff_fakes.dart';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'owner1', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  late AppDatabase db;
  late StaffServer srv;
  late FakeApi api;
  setUpAll(() async {
    await _fonts();
    Audit.testDirectory = Directory.systemTemp.createTempSync('nm_salary1186_');
  });
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    srv = StaffServer();
    api = FakeApi(srv.handle);
  });
  tearDown(() => db.close());

  Widget app(String lang) => ProviderScope(
    overrides: [databaseProvider.overrideWithValue(db), pendingCountProvider.overrideWith((ref) => Stream.value(0)), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(_owner))],
    child: MaterialApp(
      theme: buildTheme(dark: false),
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: const SalaryScreen(),
    ),
  );

  void phone(WidgetTester t, {double scale = 1.0}) {
    t.view.physicalSize = const Size(720, 1280); // 360×640 dp
    t.view.devicePixelRatio = 2.0;
    t.platformDispatcher.textScaleFactorTestValue = scale;
    final prev = FlutterError.onError;
    FlutterError.onError = (d) {
      if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
      prev?.call(d);
    };
    addTearDown(() {
      FlutterError.onError = prev;
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
      t.platformDispatcher.clearTextScaleFactorTestValue();
    });
  }

  void rows() => srv.salaryRows = [
    {'user_id': 'u1', 'employee_label': 'Айбек Тологонов', 'pay_scheme': 'salary', 'monthly_base_salary': 30000, 'sales_percent': 0, 'per_item_amount': 0, 'base_prorated': 30000, 'total': 30000, 'percent_bonus': 0, 'per_item_bonus': 0, 'profile_scope': 'employee'},
    {'user_id': 'u2', 'employee_label': 'Нурлан Б.', 'pay_scheme': 'percent', 'monthly_base_salary': 0, 'sales_percent': 2, 'per_item_amount': 0, 'base_prorated': 0, 'total': 20500, 'percent_bonus': 20500, 'per_item_bonus': 0, 'profile_scope': 'employee'},
  ];

  for (final lang in ['ky', 'ru']) {
    testWidgets('В-3н $lang, 360 dp, шрифт 1,3: суммы и заголовки таблицы — одной строкой, число не рвётся', (t) async {
      phone(t, scale: 1.3);
      rows();
      await t.pumpWidget(app(lang));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      final l = lookupAppLocalizations(Locale(lang));
      // высота одной строки текста при этом шрифте — «0» рядом в той же таблице
      for (final sum in [money(30000), money(20500)]) {
        final f = find.text(sum);
        expect(f, findsWidgets, reason: sum);
        for (var i = 0; i < f.evaluate().length; i++) {
          final r = t.getRect(f.at(i));
          expect(r.height, lessThan(32), reason: 'сумма $sum — одна строка, а не «30 00 / 0»');
          expect(r.right, lessThanOrEqualTo(360));
        }
      }
      for (final head in [l.stfColEmployee, l.stfAccrued, l.stfLeft]) {
        final f = find.text(head);
        expect(f, findsWidgets, reason: head);
        expect(t.getRect(f.first).height, lessThan(30), reason: 'заголовок «$head» не переносится по буквам');
      }
      expect(find.text(l.stfPayBtn), findsNWidgets(2));
      for (final b in find.widgetWithText(FilledButton, l.stfPayBtn).evaluate()) {
        expect(b.size!.height, greaterThanOrEqualTo(48));
      }
    });
  }

  group('С-5н: замок в списке', () {
    testWidgets('ответ не дошёл → после закрытия окна в строке сотрудника значок, «выплата без ответа» и «Проверить» вместо «Выплатить»', (t) async {
      phone(t);
      rows();
      srv.loseFlowResponse = 1;
      await t.pumpWidget(app('ru'));
      await t.pumpAndSettle();
      await t.tap(find.text('Выплатить').first);
      await t.pumpAndSettle();
      await t.tap(find.widgetWithText(FilledButton, 'Выплатить').last);
      await t.pumpAndSettle();
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.widgetWithText(FilledButton, 'Выплатить')));
      await t.pumpAndSettle();
      expect(find.textContaining('могла пройти'), findsOneWidget);
      expect(srv.flowPosts.length, 1);
      // окно закрывают, не дожидаясь «готово»
      await t.tapAt(const Offset(180, 30));
      await t.pumpAndSettle();
      expect(find.textContaining('могла пройти'), findsNothing, reason: 'окно выплаты закрыто');
      expect(find.text('выплата без ответа'), findsOneWidget);
      expect(find.byIcon(Icons.lock_outline_rounded), findsOneWidget);
      expect(find.widgetWithText(OutlinedButton, 'Проверить'), findsOneWidget);
      expect(find.text('Выплатить'), findsOneWidget, reason: 'у второго сотрудника «Выплатить» осталась, у закрытого замком — нет');
      expect(t.getSize(find.widgetWithText(OutlinedButton, 'Проверить')).height, greaterThanOrEqualTo(48));
      // «Проверить» открывает карточку с проверкой на сервере
      await t.tap(find.widgetWithText(OutlinedButton, 'Проверить'));
      await t.pumpAndSettle();
      expect(find.text('Проверить на сервере'), findsOneWidget);
    });
  });

  group('С-4н: касса в окне выплаты', () {
    testWidgets('единственная касса компании (без ролей) выбрана заранее — «Выплатить» доходит до подтверждения с её названием', (t) async {
      phone(t);
      rows();
      srv.cashboxes
        ..clear()
        ..add({'id': 'c1', 'name': 'Касса 1 (зал)', 'current_balance': '12000'});
      await t.pumpWidget(app('ru'));
      await t.pumpAndSettle();
      await t.tap(find.text('Выплатить').first);
      await t.pumpAndSettle();
      expect(find.textContaining('Касса 1 (зал)'), findsWidgets, reason: 'касса выбрана в списке');
      await t.tap(find.widgetWithText(FilledButton, 'Выплатить').last);
      await t.pumpAndSettle();
      expect(find.textContaining('Выплатить ${money(30000)} сом сотруднику Айбек Тологонов из кассы «Касса 1 (зал)»'), findsOneWidget);
      expect(find.textContaining('Не нашла кассу'), findsNothing);
    });

    testWidgets('несколько касс без ролей: заранее не выбрана; без выбора — «Выберите кассу», а не «Не нашла кассу…»', (t) async {
      phone(t);
      rows();
      srv.cashboxes
        ..clear()
        ..addAll([
          {'id': 'c1', 'name': 'Касса А'},
          {'id': 'c2', 'name': 'Касса Б'},
        ]);
      await t.pumpWidget(app('ru'));
      await t.pumpAndSettle();
      await t.tap(find.text('Выплатить').first);
      await t.pumpAndSettle();
      await t.tap(find.widgetWithText(FilledButton, 'Выплатить').last);
      await t.pumpAndSettle();
      expect(find.text('Выберите кассу'), findsWidgets);
      expect(find.textContaining('Не нашла кассу'), findsNothing);
      expect(srv.flowPosts, isEmpty);
      // выбрал сам — идёт дальше
      await t.ensureVisible(find.byType(DropdownButton<PayoutCashbox>));
      await t.tap(find.byType(DropdownButton<PayoutCashbox>));
      await t.pumpAndSettle();
      await t.tap(find.text('Касса Б').last);
      await t.pumpAndSettle();
      await t.tap(find.widgetWithText(FilledButton, 'Выплатить').last);
      await t.pumpAndSettle();
      expect(find.textContaining('из кассы «Касса Б»'), findsOneWidget);
    });

    testWidgets('у компании касс нет совсем — прежнее сообщение «выплатите на сайте»', (t) async {
      phone(t);
      rows();
      srv.cashboxes.clear();
      await t.pumpWidget(app('ru'));
      await t.pumpAndSettle();
      await t.tap(find.text('Выплатить').first);
      await t.pumpAndSettle();
      expect(find.textContaining('Не нашла кассу'), findsWidgets);
    });
  });
}
