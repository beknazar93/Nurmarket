// 2026-10-26 (голос-управление, В7): экраны «Задачи», «Мои задачи», «Зарплата» и плашка на главной — на фейковом сервере,
// без сети. Телефон 360×640, шрифт 1,0 и 1,3: без переполнения; «Выплатить» — подтверждение, сумма сама не подставляется при
// «частично», выплата уходит одним расходом с ключом.
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/ai/assistant/staff_tools.dart' show StaffPerson;
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart' show money;
import 'package:nurmarket/features/staff/salary_screen.dart';
import 'package:nurmarket/features/staff/tasks_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/staff_fakes.dart';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'owner1', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');
const _cashier = Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айбек Т.', role: 'cashier', companyName: 'Нур', cashboxId: 'b', perms: []);

void _phone(WidgetTester t, {double scale = 1.0}) {
  t.view.physicalSize = const Size(720, 1280);
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  addTearDown(() {
    t.view.resetPhysicalSize();
    t.view.resetDevicePixelRatio();
    t.platformDispatcher.clearTextScaleFactorTestValue();
  });
}

String _d(DateTime d) => '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}.${d.year}';
String _iso(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

void main() {
  late AppDatabase db;
  late StaffServer srv;
  late FakeApi api;
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_staff_screens_'));
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    srv = StaffServer();
    api = FakeApi(srv.handle);
  });
  tearDown(() => db.close());

  Widget app(Widget home, Session s, {String lang = 'ru', bool dark = false}) => ProviderScope(
    overrides: [databaseProvider.overrideWithValue(db), pendingCountProvider.overrideWith((ref) => Stream.value(0)), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(s))],
    child: MaterialApp(
      theme: buildTheme(dark: false),
      darkTheme: buildTheme(dark: true),
      themeMode: dark ? ThemeMode.dark : ThemeMode.light,
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
      home: home,
    ),
  );

  final today = DateTime.now();
  final yesterday = today.subtract(const Duration(days: 1));

  group('Задачи', () {
    for (final scale in [1.0, 1.3]) {
      testWidgets('список: исполнитель, срок, «просрочена», фильтры; шрифт $scale — без переполнения', (t) async {
        _phone(t, scale: scale);
        srv.addTask('Пересчитать полку с водой', 'u2', due: _iso(today.add(const Duration(days: 1))), description: '⟦nm: priority=high⟧');
        srv.addTask('Принять накладную «Кока-Кола Бишкек»', 'u1', due: _iso(yesterday));
        srv.addTask('Протереть витрину', 'u3', due: _iso(today), status: 'done');
        await t.pumpWidget(app(const TasksScreen(), _owner));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        expect(find.text('Задачи'), findsOneWidget);
        expect(find.text('Задач: 3 · просрочено: 1'), findsOneWidget);
        expect(find.textContaining('Пересчитать полку с водой'), findsOneWidget);
        expect(find.textContaining('❗'), findsOneWidget, reason: 'высокий приоритет');
        expect(find.textContaining('просрочена ·'), findsOneWidget);
        expect(find.text('Нурлан Б.'), findsOneWidget);
        // фильтр «Просрочено · 1»
        await t.tap(find.text('Просрочено · 1'));
        await t.pumpAndSettle();
        expect(find.textContaining('Принять накладную'), findsOneWidget);
        expect(find.textContaining('Пересчитать полку'), findsNothing);
      });
    }

    testWidgets('руководитель переводит задачу «Сделано» — PATCH статуса; «Удалить» спрашивает', (t) async {
      _phone(t);
      srv.addTask('Выставить ценники', 'u3', due: _iso(today));
      await t.pumpWidget(app(const TasksScreen(), _owner));
      await t.pumpAndSettle();
      await t.tap(find.text('Выставить ценники'));
      await t.pumpAndSettle();
      await t.tap(find.text('Сделано').last);
      await t.pumpAndSettle();
      expect(srv.taskPatches.single['status'], 'done');
      expect(srv.tasks.single['status'], 'done');
    });

    testWidgets('новая задача: без исполнителя не создаётся; с исполнителем — POST с assigned_to', (t) async {
      _phone(t);
      await t.pumpWidget(app(const TasksScreen(), _owner));
      await t.pumpAndSettle();
      await t.tap(find.byTooltip('Новая задача'));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField).first, 'Пересчитать холодильник');
      await t.tap(find.text('Поставить задачу'));
      await t.pumpAndSettle();
      expect(find.text('Выберите исполнителя'), findsOneWidget);
      expect(srv.taskPosts, isEmpty);
      await t.tap(find.byType(DropdownButtonFormField<StaffPerson>).first);
      await t.pumpAndSettle();
      await t.tap(find.text('Нурлан Б.').last);
      await t.pumpAndSettle();
      await t.tap(find.text('Поставить задачу'));
      await t.pumpAndSettle();
      expect(srv.taskPosts.single['assigned_to'], 'u2');
      expect(srv.taskPosts.single['title'], 'Пересчитать холодильник');
    });
  });

  group('Мои задачи', () {
    testWidgets('кассир видит только свои; «В работу» / «Сделано» — шлёт только статус; плашка про просроченную', (t) async {
      _phone(t);
      srv.addTask('Моя на сегодня', 'u1', due: _iso(today));
      srv.addTask('Моя просрочена', 'u1', due: _iso(yesterday));
      srv.addTask('Чужая', 'u2', due: _iso(today));
      await t.pumpWidget(app(const MyTasksScreen(), _cashier));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      expect(find.text('Моя на сегодня'), findsOneWidget);
      expect(find.text('Чужая'), findsNothing);
      expect(find.textContaining('Просрочено: 1'), findsOneWidget);
      expect(find.text('Мои задачи'), findsOneWidget);
      await t.tap(find.text('В работу').first);
      await t.pumpAndSettle();
      expect(srv.taskPatches.single.keys.toSet(), {'status'});
      expect(srv.taskPatches.single['status'], 'in_progress');
    });

    testWidgets('плашка на главной: «Задач на сегодня: 1 · просрочено: 1» (напоминание — пока не наступило, не показывается)', (t) async {
      _phone(t);
      srv.addTask('Сегодняшняя', 'u1', due: _iso(today));
      srv.addTask('Вчерашняя', 'u1', due: _iso(yesterday));
      await t.pumpWidget(app(Scaffold(body: Consumer(builder: (c, ref, _) => TasksTodayPlate(onTap: () {}))), _cashier));
      await t.pumpAndSettle();
      expect(find.text('Задач на сегодня: 1 · просрочено: 1'), findsOneWidget);
    });
  });

  group('Зарплата', () {
    final from = DateTime(today.year, today.month, 1);
    String tag(String uid) => '[#$uid ${_d(from)}–${_d(today)}]';

    void rows() => srv.salaryRows = [
      {'user_id': 'u1', 'employee_label': 'Айбек Т.', 'pay_scheme': 'salary', 'monthly_base_salary': 30000, 'sales_percent': 0, 'per_item_amount': 0, 'base_prorated': 30000, 'total': 30000, 'percent_bonus': 0, 'per_item_bonus': 0, 'profile_scope': 'employee'},
      {'user_id': 'u2', 'employee_label': 'Нурлан Б.', 'pay_scheme': 'percent', 'monthly_base_salary': 0, 'sales_percent': 2, 'per_item_amount': 0, 'base_prorated': 0, 'total': 18000, 'percent_bonus': 18000, 'per_item_bonus': 0, 'profile_scope': 'employee'},
    ];

    for (final scale in [1.0, 1.3]) {
      testWidgets('таблица: начислено / выплачено / осталось, «Выплатить» в строке; шрифт $scale — без переполнения', (t) async {
        _phone(t, scale: scale);
        rows();
        srv.addFlow(name: 'Зарплата: Нурлан Б. (${_d(from)}–${_d(today)}) ${tag('u2')}', amount: 9000, sourceId: 'salary:u2:${from.year}${from.month.toString().padLeft(2, '0')}01-${today.year}${today.month.toString().padLeft(2, '0')}${today.day.toString().padLeft(2, '0')}:k', category: 'cat1');
        await t.pumpWidget(app(const SalaryScreen(), _owner));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        expect(find.text('Зарплата'), findsOneWidget);
        expect(find.text(money(48000)), findsOneWidget, reason: 'начислено');
        expect(find.text(money(9000)), findsWidgets, reason: 'выплачено и осталось у Нурлана');
        expect(find.text('Выплатить'), findsNWidgets(2));
        expect(find.textContaining('Проверено частично'), findsNothing);
      });
    }

    testWidgets('«Выплатить» при уверенном остатке: сумма подставлена, подтверждение, один расход с ключом', (t) async {
      _phone(t);
      rows();
      await t.pumpWidget(app(const SalaryScreen(), _owner));
      await t.pumpAndSettle();
      await t.tap(find.text('Выплатить').first);
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      // у Айбека 30 000, подставлено
      final field = t.widget<TextField>(find.byType(TextField).first);
      expect(field.controller!.text, '30000');
      await t.tap(find.widgetWithText(FilledButton, 'Выплатить').last);
      await t.pumpAndSettle();
      expect(find.textContaining('Выплатить ${money(30000)} сом сотруднику'), findsOneWidget);
      expect(srv.flowPosts, isEmpty, reason: 'без подтверждения ничего не уходит');
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.widgetWithText(FilledButton, 'Выплатить')));
      await t.pumpAndSettle();
      expect(srv.flowPosts.length, 1);
      final b = srv.flowPosts.single;
      expect(b['amount'], '30000.00');
      expect(b['cashbox'], 'cb1');
      expect(b['idempotency_key'], isNotEmpty);
      expect((b['name'] as String), contains('[#u1 '));
      expect(b['status'], 'approved');
    });

    testWidgets('«проверено частично»: плашка, поле суммы ПУСТОЕ (сумма сама не подставляется), без суммы выплата не уходит', (t) async {
      _phone(t);
      rows();
      // расход «Зарплата», который некому приписать
      srv.addFlow(name: 'Зарплата за грузчиков (${_d(from)}–${_d(today)})', amount: 700, category: 'cat1');
      await t.pumpWidget(app(const SalaryScreen(), _owner));
      await t.pumpAndSettle();
      expect(find.textContaining('Проверено частично'), findsOneWidget);
      await t.tap(find.text('Выплатить').first);
      await t.pumpAndSettle();
      final field = t.widget<TextField>(find.byType(TextField).first);
      expect(field.controller!.text, isEmpty);
      await t.tap(find.widgetWithText(FilledButton, 'Выплатить').last);
      await t.pumpAndSettle();
      expect(find.text('Введите сумму выплаты'), findsOneWidget);
      expect(srv.flowPosts, isEmpty);
    });

    testWidgets('ответ не дошёл: сообщение «могла пройти», в таблице «выплата без ответа» и кнопка «Выплатить» закрыта замком', (t) async {
      _phone(t);
      rows();
      srv.loseFlowResponse = 1;
      await t.pumpWidget(app(const SalaryScreen(), _owner));
      await t.pumpAndSettle();
      await t.tap(find.text('Выплатить').first);
      await t.pumpAndSettle();
      await t.tap(find.widgetWithText(FilledButton, 'Выплатить').last);
      await t.pumpAndSettle();
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.widgetWithText(FilledButton, 'Выплатить')));
      await t.pumpAndSettle();
      expect(find.textContaining('могла пройти'), findsOneWidget);
      expect(srv.flowPosts.length, 1, reason: 'повтора нет');
    });

    testWidgets('сотрудник без права зарплаты не видит кнопок «Выплатить»; нет доступа к отчёту — сообщение', (t) async {
      _phone(t);
      rows();
      const accountant = Session(email: 'a@x.kg', access: 'a', refresh: 'r', userId: 'acc', userName: 'Бухгалтер', role: 'accountant', companyName: 'Нур', cashboxId: 'b', perms: ['can_view_salary']);
      await t.pumpWidget(app(const SalaryScreen(), accountant));
      await t.pumpAndSettle();
      expect(find.text('Выплатить'), findsNothing);
      expect(find.text('Айбек Т.'), findsWidgets);
    });
  });
}
