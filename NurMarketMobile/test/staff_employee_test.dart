// 2026-10-28 (сотрудники): модель и клиент api/users/employees/, карточка сотрудника (создание, правка, увольнение), список >100
// с поиском, ошибки 400/403/404/409/429/500, права по ролям, «Доступ к ИИ» с большим составом. Сервер NurCRM — только фейки.
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/ai_access.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/admin_more.dart';
import 'package:nurmarket/features/admin/ai_access_ui.dart';
import 'package:nurmarket/features/admin/staff_form.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef Srv = Future<dynamic> Function(String method, String path, Object? body, Map<String, dynamic>? query);

class _Api extends ApiClient {
  _Api(this.srv, [Session? s]) : super(SessionStore()) {
    if (s != null) attach(s);
  }
  Srv srv;
  final log = <String>[];
  final bodies = <String, Object?>{};

  Future<dynamic> _h(String m, String p, Object? b, Map<String, dynamic>? q) {
    log.add('$m $p');
    bodies['$m $p'] = b;
    return srv(m, p, b, q);
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => _h('GET', path, null, query);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => _h('POST', path, body, null);
  @override
  Future<dynamic> patch(String path, {Object? body}) => _h('PATCH', path, body, null);
  @override
  Future<dynamic> delete(String path) => _h('DELETE', path, null, null);

  int count(String prefix) => log.where((x) => x.startsWith(prefix)).length;
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _admin = Session(email: 'a@x', access: 'a', refresh: 'r', userId: 'adm', userName: 'Админ', role: 'admin', perms: []);
const _owner = Session(email: 'o@x', access: 'a', refresh: 'r', userId: 'own', userName: 'Бакыт', role: 'owner', isOwner: true, perms: []);
const _cashier = Session(email: 'c@x', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айбек', role: 'cashier', perms: [Session.sale]);
const _settingsMgr = Session(email: 's@x', access: 'a', refresh: 'r', userId: 'u9', userName: 'Старший', role: 'cashier', perms: [Session.viewSettings]);

Map<String, dynamic> _emp(String id, String first, String last, {bool? active, Map<String, dynamic> more = const {}}) => {
  'id': id,
  'first_name': first,
  'last_name': last,
  'email': '$id@shop.kg',
  'phone_number': '+996555000${id.padLeft(3, '0')}',
  'track_number': '',
  'custom_role': 'r1',
  'role_display': 'Кассир',
  'can_view_sale': true,
  'is_active': ?active,
  ...more,
};

AppLocalizations _l(String lang) => lookupAppLocalizations(Locale(lang));

void main() {
  late AppDatabase db;
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
    AiAccessRepository.resetForTest();
  });
  tearDown(() async {
    ApiText.l = null;
    await db.close();
  });

  // ------------------------------------------------------------------------------------------------ модель
  group('модель Employee', () {
    test('пустые и неполные поля не роняют: имя из полного имени или почты, роль пустая, доступы выключены', () {
      final e = Employee.fromJson({'id': 5, 'email': 'x@y.kg'});
      expect(e.id, '5');
      expect(e.name, 'x@y.kg');
      expect(e.role, '');
      expect(e.phone, '');
      expect(e.active, isNull);
      expect(e.dismissed, isFalse);
      expect(e.rights['can_view_sale'], isFalse);
      expect(e.rights.containsKey('can_use_pos'), isFalse, reason: 'поля нет в ответе — не показываем');
      final f = Employee.fromJson({'id': 'a', 'full_name': 'Айгерим С.', 'first_name': null, 'last_name': '', 'phone': '0555'});
      expect(f.name, 'Айгерим С.');
      expect(f.phone, '0555');
      expect(Employee.fromJson(const {}).id, '');
    });

    test('все поля сервера сохраняются в raw (неизвестные не теряются); is_active и can_use_pos определяются по ответу', () {
      final e = Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: false, more: {'future_field': {'a': 1}, 'can_use_pos': true, 'custom_role': {'id': 12, 'name': 'Кладовщик'}}));
      expect(e.raw['future_field'], {'a': 1});
      expect(e.dismissed, isTrue);
      expect(e.activeKey, 'is_active');
      expect(e.rights['can_use_pos'], isTrue);
      expect(e.roleId, '12', reason: 'custom_role может прийти объектом');
      expect(Employee.fromJson({'id': '1', 'active': true}).activeKey, 'active');
    });
  });

  group('проверка полей', () {
    test('почта', () {
      expect(isValidStaffEmail('aibek@mail.kg'), isTrue);
      for (final bad in ['', 'aibek', 'aibek@', 'aibek@mail', '@mail.kg', 'a b@mail.kg', 'a@mail.k']) {
        expect(isValidStaffEmail(bad), isFalse, reason: bad);
      }
    });
    test('телефон: пустой можно, 9–15 цифр, буквы нельзя; для сервера — «+» и цифры', () {
      expect(isValidStaffPhone(''), isTrue);
      expect(isValidStaffPhone('+996 (555) 123-456'), isTrue);
      expect(isValidStaffPhone('0555123456'), isTrue);
      expect(isValidStaffPhone('12345'), isFalse);
      expect(isValidStaffPhone('+996 555 12a 456'), isFalse);
      expect(isValidStaffPhone('1234567890123456'), isFalse);
      expect(normalizeStaffPhone('+996 (555) 123-456'), '+996555123456');
      expect(normalizeStaffPhone('  '), '');
    });
    test('дата: берётся только число, мусор — null', () {
      expect(staffIsoDate('2026-10-28T09:00:00Z'), '2026-10-28');
      expect(staffIsoDate('28.10.2026'), isNull);
      expect(staffIsoDate(null), isNull);
      expect(staffIsoDate('2026-13-45'), isNull);
    });
  });

  // ------------------------------------------------------------------------------------------------ клиент
  group('StaffRepository', () {
    test('список из 230 сотрудников: все страницы, без повторов', () async {
      final api = _Api((m, p, b, q) async {
        final page = q?['page'] as int? ?? 1;
        final from = (page - 1) * 100;
        final n = (230 - from).clamp(0, 100);
        return {'results': [for (var i = 0; i < n; i++) _emp('${from + i}', 'N${from + i}', '')], 'next': from + n < 230 ? 'x' : null};
      });
      final list = await StaffRepository(api).list();
      expect(list.length, 230);
      expect({for (final e in list) e.id}.length, 230);
      expect(api.count('GET'), 3);
    });

    test('сервер без страниц отдаёт один и тот же список на любой странице — дублей нет, запросов немного', () async {
      final api = _Api((m, p, b, q) async => [for (var i = 0; i < 120; i++) _emp('$i', 'N$i', '')]);
      final list = await StaffRepository(api).list();
      expect(list.length, 120);
      expect(api.count('GET'), 2, reason: 'вторая страница ничего нового не дала — стоп');
    });

    test('404 на странице после первой — это «страницы кончились», а не ошибка', () async {
      final api = _Api((m, p, b, q) async {
        if ((q?['page'] as int? ?? 1) > 1) throw ApiException('Invalid page.', status: 404);
        return {'results': [for (var i = 0; i < 100; i++) _emp('$i', 'N', '')], 'next': 'x'};
      });
      expect((await StaffRepository(api).list()).length, 100);
    });

    test('создание: 404 первого адреса — второй адрес; пароль и id из ответа; пароль не остаётся в карточке', () async {
      final api = _Api((m, p, b, q) async {
        if (p == 'api/users/employees/create/') throw ApiException('nf', status: 404);
        return {'user': {'id': 41, 'email': 'new@shop.kg', 'first_name': 'Новый'}, 'password': 'Zx9-secret'};
      });
      final r = await StaffRepository(api).create({'email': 'new@shop.kg', 'first_name': 'Новый'});
      expect(api.log, ['POST api/users/employees/create/', 'POST api/users/employees/']);
      expect(r.employee.id, '41');
      expect(r.password, 'Zx9-secret');
      expect(r.employee.raw.containsKey('password'), isFalse);
    });

    test('создание: id нет в ответе — ищем по почте в списке', () async {
      final api = _Api((m, p, b, q) async {
        if (m == 'POST') return {'password': 'p1'};
        return [_emp('77', 'Новый', '', more: {'email': 'new@shop.kg'})];
      });
      final r = await StaffRepository(api).create({'email': 'NEW@shop.kg', 'first_name': 'Новый'});
      expect(r.employee.id, '77');
    });

    test('создание: ответ не дошёл (500), сотрудник уже создан — находим и не создаём второй раз', () async {
      var posts = 0;
      final api = _Api((m, p, b, q) async {
        if (m == 'POST') {
          posts++;
          throw ApiException('boom', status: 503);
        }
        return [_emp('9', 'Новый', '', more: {'email': 'new@shop.kg'})];
      });
      final r = await StaffRepository(api).create({'email': 'new@shop.kg', 'first_name': 'Новый'});
      expect(r.recovered, isTrue);
      expect(r.employee.id, '9');
      expect(posts, 1);
    });

    test('создание: 400 и 429 — без повторов и поиска, ошибка уходит наверх', () async {
      for (final code in [400, 429]) {
        final api = _Api((m, p, b, q) async => throw ApiException('x', status: code));
        await expectLater(StaffRepository(api).create({'email': 'a@b.kg'}), throwsA(isA<ApiException>().having((e) => e.status, 'status', code)));
        expect(api.count('GET'), 0);
      }
    });

    test('правка: PATCH только переданных полей; пустой набор — запроса нет; 404/405 — следующий адрес', () async {
      final api = _Api((m, p, b, q) async {
        if (p == 'api/users/employees/7/') throw ApiException('nf', status: 405);
        return {};
      });
      final repo = StaffRepository(api);
      await repo.update('7', {});
      expect(api.log, isEmpty);
      await repo.update('7', {'first_name': 'Нурлан'});
      expect(api.log, ['PATCH api/users/employees/7/', 'PATCH api/users/employees/7/update/']);
      expect(api.bodies['PATCH api/users/employees/7/update/'], {'first_name': 'Нурлан'});
    });

    test('удаление без признака «работает»: сервер ответил 2xx, но запись осталась — понятная ошибка 409', () async {
      final api = _Api((m, p, b, q) async => m == 'GET' ? [_emp('7', 'Нурлан', '')] : {});
      await expectLater(StaffRepository(api).remove('7'), throwsA(isA<ApiException>().having((e) => e.status, 'status', 409)));
    });

    test('сброс пароля: пароль из ответа; нет адреса (404) и пустой ответ — 404 для экрана', () async {
      expect(await StaffRepository(_Api((m, p, b, q) async => {'password': 'N3w'})).resetPassword('7'), 'N3w');
      await expectLater(StaffRepository(_Api((m, p, b, q) async => {})).resetPassword('7'), throwsA(isA<ApiException>()));
    });

    test('должности: первый адрес 404 — второй; 403 — пустой список без ошибки', () async {
      final api = _Api((m, p, b, q) async {
        if (p == 'api/users/roles/') throw ApiException('nf', status: 404);
        return [
          {'id': 3, 'name': 'Кладовщик'},
          {'name': 'без id'},
        ];
      });
      final roles = await StaffRepository(api).roles();
      expect(roles.map((r) => r.name), ['Кладовщик']);
      expect(await StaffRepository(_Api((m, p, b, q) async => throw ApiException('no', status: 403))).roles(), isEmpty);
    });

    test('тексты ошибок: 403, 404, 409, 429, 500, обрыв связи, почта занята — на всех языках', () {
      for (final lang in ['ru', 'ky', 'uz', 'en', 'tr']) {
        final l = _l(lang);
        expect(staffErrorText(l, ApiException('x', status: 403)), l.empErr403);
        expect(staffErrorText(l, ApiException('x', status: 404)), l.empErr404);
        expect(staffErrorText(l, ApiException('x', status: 409)), l.empErr409);
        expect(staffErrorText(l, ApiException('x', status: 429)), l.empErr429);
        expect(staffErrorText(l, ApiException('x', status: 500)), l.empErr5xx);
        expect(staffErrorText(l, ApiException('x', status: 503)), l.empErr5xx);
        expect(staffErrorText(l, ApiException('x', status: 400, payload: {'email': ['занято']})), l.empErrEmail);
        expect(staffErrorText(l, ApiException('first_name: обязательное', status: 400)), 'first_name: обязательное');
        expect(staffErrorText(l, ApiException('', status: 400)), l.empErrBad);
        expect(staffErrorText(l, ApiException('Нет связи с сервером.')), 'Нет связи с сервером.');
        expect(staffErrorText(l, StateError('boom')), l.apiNetworkError);
      }
    });
  });

  // ------------------------------------------------------------------------------------------------ права
  group('права по ролям', () {
    test('правка карточки — администратор/владелец или can_edit_employees; «Настройки» дают только доступы; список — по can_view_employees', () {
      expect(_admin.canEditStaff, isTrue);
      expect(_owner.canEditStaff, isTrue);
      expect(_cashier.canEditStaff, isFalse);
      expect(_settingsMgr.canEditStaff, isFalse);
      const editor = Session(email: 'e@x', access: 'a', refresh: 'r', role: 'cashier', perms: [Session.editEmployees]);
      expect(editor.canEditStaff, isTrue);
      expect(_cashier.canRoute('/admin/staff'), isFalse);
      expect(_settingsMgr.canRoute('/admin/staff'), isTrue);
      const viewer = Session(email: 'v@x', access: 'a', refresh: 'r', role: 'cashier', perms: [Session.viewEmployees]);
      expect(viewer.canRoute('/admin/staff'), isTrue, reason: 'сервер отдаёт список по can_view_employees');
      expect(viewer.canEditStaff, isFalse);
    });

    test('кассир не видит чужую зарплату и экран «Зарплата»; задачи — только свои', () {
      expect(_cashier.viewSalary, isFalse);
      expect(_cashier.editSalary, isFalse);
      expect(_cashier.canRoute('/admin/salary'), isFalse);
      expect(_cashier.canRoute('/admin/tasks'), isFalse);
      expect(_cashier.canRoute('/tasks/my'), isTrue);
      expect(_cashier.canRoute('/admin/audit'), isFalse);
      expect(_owner.canRoute('/admin/salary'), isTrue);
    });

    test('новые права зарплаты и сотрудников — в списке доступов; can_use_pos только по ответу сервера', () {
      for (final k in ['can_view_salary', 'can_edit_salary', 'can_view_employees', 'can_edit_employees']) {
        expect(staffRights.containsKey(k), isTrue, reason: k);
      }
      expect(staffRights.containsKey('can_use_pos'), isFalse);
      expect(staffRightsOptional.containsKey('can_use_pos'), isTrue);
    });
  });

  // ------------------------------------------------------------------------------------------------ экраны
  Widget app(Widget home, _Api api, Session s, {String lang = 'ru', bool dark = false}) => ProviderScope(
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

  void phone(WidgetTester t, {double scale = 1.0, Size logical = const Size(360, 800)}) {
    t.view.physicalSize = logical * 2;
    t.view.devicePixelRatio = 2.0;
    t.platformDispatcher.textScaleFactorTestValue = scale;
    addTearDown(() {
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
      t.platformDispatcher.clearTextScaleFactorTestValue();
    });
  }

  /// Хозяин формы: кнопка открывает карточку и запоминает результат (true — сохранено).
  Widget host(Employee? e, Set<String> known, List<Object?> results) => Builder(
    builder: (c) => Scaffold(
      body: Center(
        child: ElevatedButton(
          onPressed: () async => results.add(await Navigator.of(c).push<bool>(MaterialPageRoute(builder: (_) => StaffFormScreen(employee: e, knownKeys: known)))),
          child: const Text('open'),
        ),
      ),
    ),
  );

  Future<void> openForm(WidgetTester t) async {
    await t.tap(find.text('open'));
    await t.pumpAndSettle();
  }

  Future<void> tapKey(WidgetTester t, String key) async {
    final f = find.byKey(Key(key));
    if (f.evaluate().isEmpty) await t.scrollUntilVisible(f, 300, scrollable: find.byType(Scrollable).first);
    await t.ensureVisible(f);
    await t.pumpAndSettle();
    await t.tap(f);
    await t.pumpAndSettle();
  }

  Srv okServer({List<Map<String, dynamic>>? rows}) => (m, p, b, q) async {
    if (p.startsWith('api/users/roles')) {
      return [
        {'id': 'r1', 'name': 'Кассир'},
        {'id': 'r2', 'name': 'Кладовщик'},
      ];
    }
    if (m == 'GET') return rows ?? [_emp('7', 'Нурлан', 'Б.', active: true)];
    return {};
  };

  group('список сотрудников', () {
    testWidgets('130 сотрудников: счётчик, поиск по имени/почте/телефону, «Уволены» только когда они есть; FAB только у права править', (t) async {
      phone(t);
      final rows = [for (var i = 0; i < 130; i++) _emp('$i', 'Сотр$i', 'Ф', active: i != 3)];
      final api = _Api(okServer(rows: rows));
      await t.pumpWidget(app(const StaffScreen(), api, _admin));
      await t.pumpAndSettle();
      final l = _l('ru');
      expect(t.takeException(), isNull);
      expect(find.text(l.empCount(130)), findsOneWidget);
      expect(find.byType(FloatingActionButton), findsOneWidget);
      await t.enterText(find.byKey(const Key('staffSearch')), 'сотр127');
      await t.pumpAndSettle();
      expect(find.textContaining('Сотр127'), findsOneWidget);
      expect(find.textContaining('Сотр5 '), findsNothing);
      await t.enterText(find.byKey(const Key('staffSearch')), '127@shop');
      await t.pumpAndSettle();
      expect(find.textContaining('Сотр127'), findsOneWidget);
      await t.enterText(find.byKey(const Key('staffSearch')), '555000128'); // телефон
      await t.pumpAndSettle();
      expect(find.textContaining('Сотр128'), findsOneWidget);
      await t.enterText(find.byKey(const Key('staffSearch')), 'нет такого');
      await t.pumpAndSettle();
      expect(find.text(l.empNothingFound), findsOneWidget);
      await t.enterText(find.byKey(const Key('staffSearch')), '');
      await t.tap(find.byKey(const Key('staffFilter_dismissed')));
      await t.pumpAndSettle();
      expect(find.textContaining('Сотр3 Ф'), findsOneWidget);
      expect(find.textContaining('Сотр0 '), findsNothing);
    });

    testWidgets('сотрудник с одним «Настройки» видит список, но без кнопки «Добавить»; кассир без права — 403 с объяснением и «Повторить»', (t) async {
      phone(t);
      await t.pumpWidget(app(const StaffScreen(), _Api(okServer()), _settingsMgr));
      await t.pumpAndSettle();
      expect(find.byType(FloatingActionButton), findsNothing);
      final api = _Api((m, p, b, q) async => throw ApiException('detail', status: 403));
      await t.pumpWidget(const SizedBox());
      await t.pumpWidget(app(const StaffScreen(), api, _cashier));
      await t.pumpAndSettle();
      expect(find.text(_l('ru').empListDenied), findsOneWidget);
      expect(find.text(_l('ru').retry), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    testWidgets('обрыв связи при первой загрузке — сообщение и «Повторить», а не вечный индикатор', (t) async {
      phone(t);
      final api = _Api((m, p, b, q) async => throw StateError('socket'));
      await t.pumpWidget(app(const StaffScreen(), api, _admin));
      await t.pumpAndSettle();
      expect(find.text(_l('ru').apiNetworkError), findsOneWidget);
    });
  });

  group('карточка сотрудника', () {
    testWidgets('правка имени уходит PATCH-ом только с first_name; поля сервера, которых нет в форме, не трогаются', (t) async {
      phone(t);
      final api = _Api(okServer());
      final results = <Object?>[];
      final e = Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true, more: {'future_field': 'keep'}));
      await t.pumpWidget(app(host(e, const {}, results), api, _admin));
      await openForm(t);
      await t.enterText(find.byKey(const Key('staffFirst')), 'Нурлан-бек');
      await tapKey(t, 'staffSave');
      expect(api.bodies['PATCH api/users/employees/7/'], {'first_name': 'Нурлан-бек'});
      expect(results, [true]);
    });

    testWidgets('ничего не менял — запроса нет, подсказка «Ничего не изменено»', (t) async {
      phone(t);
      final api = _Api(okServer());
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, []), api, _admin));
      await openForm(t);
      await tapKey(t, 'staffSave');
      expect(api.count('PATCH'), 0);
      expect(find.text(_l('ru').empNothingChanged), findsOneWidget);
    });

    testWidgets('переключили одно право — в PATCH только это право; телефон приводится к «+цифры»', (t) async {
      phone(t);
      final api = _Api(okServer());
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, []), api, _owner));
      await openForm(t);
      await t.enterText(find.byKey(const Key('staffPhone')), '+996 (700) 111-222');
      await tapKey(t, 'staffRight_can_view_salary');
      await tapKey(t, 'staffSave');
      expect(api.bodies['PATCH api/users/employees/7/'], {'phone_number': '+996700111222', 'can_view_salary': true});
    });

    testWidgets('проверка полей: пустое имя, неверный телефон — ошибка у поля, запроса нет', (t) async {
      phone(t);
      final api = _Api(okServer());
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, []), api, _admin));
      await openForm(t);
      await t.enterText(find.byKey(const Key('staffFirst')), '   ');
      await t.enterText(find.byKey(const Key('staffPhone')), '12');
      await tapKey(t, 'staffSave');
      expect(find.text(_l('ru').empRequired), findsOneWidget);
      expect(find.text(_l('ru').empPhoneBad), findsOneWidget);
      expect(api.count('PATCH'), 0);
    });

    testWidgets('создание: пустая/кривая почта — ошибки; верные данные — POST со всеми полями и доступами, пароль показывается один раз', (t) async {
      phone(t);
      final api = _Api((m, p, b, q) async {
        if (p.startsWith('api/users/roles')) return [{'id': 'r2', 'name': 'Кладовщик'}];
        if (m == 'POST') return {'id': 55, 'password': 'Pw-123'};
        return [];
      });
      final results = <Object?>[];
      await t.pumpWidget(app(host(null, const {'middle_name', 'hire_date', 'can_use_pos'}, results), api, _admin));
      await openForm(t);
      final l = _l('ru');
      await tapKey(t, 'staffSave');
      expect(find.text(l.empRequired), findsWidgets);
      expect(api.count('POST'), 0);
      await t.enterText(find.byKey(const Key('staffFirst')), 'Айгерим');
      await t.enterText(find.byKey(const Key('staffLast')), 'Сыдыкова');
      await t.enterText(find.byKey(const Key('staffEmail')), 'aigerim@');
      await tapKey(t, 'staffSave');
      expect(find.text(l.empEmailBad), findsOneWidget);
      await t.enterText(find.byKey(const Key('staffEmail')), 'aigerim@shop.kg');
      await t.enterText(find.byKey(const Key('staffPhone')), '0555 123 456');
      await t.tap(find.byKey(const Key('staffRole')));
      await t.pumpAndSettle();
      await t.tap(find.text('Кладовщик').last);
      await t.pumpAndSettle();
      await tapKey(t, 'staffRight_can_view_sale');
      await tapKey(t, 'staffSave');
      final body = api.bodies['POST api/users/employees/create/'] as Map;
      expect(body['email'], 'aigerim@shop.kg');
      expect(body['first_name'], 'Айгерим');
      expect(body['last_name'], 'Сыдыкова');
      expect(body['phone_number'], '0555123456');
      expect(body['custom_role'], 'r2');
      expect(body['can_view_sale'], true);
      expect(body['can_view_orders'], false);
      expect(body['can_use_pos'], true, reason: 'допуск к кассе по умолчанию включён (Б-16)');
      expect(body.containsKey('middle_name'), isFalse, reason: 'пустое необязательное поле не шлём');
      expect(find.text('Pw-123'), findsOneWidget);
      expect(find.textContaining('aigerim@shop.kg'), findsWidgets);
      await t.tap(find.text(l.done));
      await t.pumpAndSettle();
      expect(results, [true]);
    });

    testWidgets('ошибки сервера при сохранении — понятные сообщения, экран остаётся (403, 404, 409, 429, 500, обрыв)', (t) async {
      phone(t);
      final l = _l('ru');
      for (final c in <(ApiException, String)>[
        (ApiException('x', status: 403), l.empErr403),
        (ApiException('x', status: 404), l.empErr404),
        (ApiException('x', status: 409), l.empErr409),
        (ApiException('x', status: 429), l.empErr429),
        (ApiException('x', status: 500), l.empErr5xx),
        (ApiException('Нет связи с сервером.'), 'Нет связи с сервером.'),
      ]) {
        final api = _Api((m, p, b, q) async {
          if (p.startsWith('api/users/roles')) return [];
          throw c.$1;
        });
        await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, []), api, _admin));
        await openForm(t);
        await t.enterText(find.byKey(const Key('staffFirst')), 'Другое');
        await tapKey(t, 'staffSave');
        expect(find.text(c.$2), findsOneWidget, reason: '${c.$1.status}');
        expect(find.byKey(const Key('staffSave')), findsOneWidget, reason: 'карточка осталась');
        await t.pumpWidget(const SizedBox());
      }
    });

    testWidgets('двойное нажатие «Сохранить» не создаёт двух сотрудников', (t) async {
      phone(t);
      final api = _Api((m, p, b, q) async {
        if (p.startsWith('api/users/roles')) return [];
        if (m == 'POST') {
          await Future<void>.delayed(const Duration(milliseconds: 300));
          return {'id': 5};
        }
        return [];
      });
      await t.pumpWidget(app(host(null, const {}, []), api, _admin));
      await openForm(t);
      await t.enterText(find.byKey(const Key('staffFirst')), 'Айгерим');
      await t.enterText(find.byKey(const Key('staffEmail')), 'a@shop.kg');
      final save = find.byKey(const Key('staffSave'));
      await t.tap(save);
      await t.pump(const Duration(milliseconds: 50));
      await t.tap(save, warnIfMissed: false);
      await t.pumpAndSettle();
      expect(api.count('POST'), 1);
    });

    testWidgets('увольнение: с признаком is_active — PATCH is_active=false после подтверждения; восстановление — true', (t) async {
      phone(t);
      final l = _l('ru');
      final api = _Api(okServer());
      final results = <Object?>[];
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, results), api, _admin));
      await openForm(t);
      await tapKey(t, 'staffDismiss');
      expect(find.text(l.empDismissAsk('Нурлан Б.')), findsOneWidget);
      await t.tap(find.text(l.empDismiss).last);
      await t.pumpAndSettle();
      expect(api.bodies['PATCH api/users/employees/7/'], {'is_active': false});
      expect(results, [true]);

      final api2 = _Api(okServer());
      await t.pumpWidget(const SizedBox());
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: false)), const {}, []), api2, _admin));
      await openForm(t);
      await tapKey(t, 'staffRestore');
      await t.tap(find.text(l.empRestore).last);
      await t.pumpAndSettle();
      expect(api2.bodies['PATCH api/users/employees/7/'], {'is_active': true});
    });

    testWidgets('«Отмена» в подтверждении увольнения — ничего не уходит; себя и владельца уволить нельзя', (t) async {
      phone(t);
      final l = _l('ru');
      final api = _Api(okServer());
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, []), api, _admin));
      await openForm(t);
      await tapKey(t, 'staffDismiss');
      await t.tap(find.text(l.cancel));
      await t.pumpAndSettle();
      expect(api.count('PATCH'), 0);
      expect(api.count('DELETE'), 0);
      // себя
      await t.pumpWidget(const SizedBox());
      await t.pumpWidget(app(host(Employee.fromJson(_emp('adm', 'Админ', '', active: true)), const {}, []), api, _admin));
      await openForm(t);
      expect(find.byKey(const Key('staffDismiss')), findsNothing);
      // владелец
      await t.pumpWidget(const SizedBox());
      await t.pumpWidget(app(host(Employee.fromJson(_emp('o1', 'Хозяин', '', active: true, more: {'is_owner': true})), const {}, []), api, _admin));
      await openForm(t);
      expect(find.byKey(const Key('staffDismiss')), findsNothing);
    });

    testWidgets('сброс пароля: нет адреса на сервере — «пока не поддерживает», а не «сотрудник не найден»', (t) async {
      phone(t);
      final api = _Api((m, p, b, q) async {
        if (p.startsWith('api/users/roles')) return [];
        throw ApiException('nf', status: 404);
      });
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, []), api, _admin));
      await openForm(t);
      await tapKey(t, 'staffResetPwd');
      await t.tap(find.text(_l('ru').empResetPwd).last);
      await t.pumpAndSettle();
      expect(find.text(_l('ru').empFeatureMissing), findsOneWidget);
    });

    testWidgets('кассир без прав: карточка только для просмотра — нет кнопки «Сохранить», поля и переключатели выключены', (t) async {
      phone(t);
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, []), _Api(okServer()), _cashier));
      await openForm(t);
      expect(find.byKey(const Key('staffSave')), findsNothing);
      expect(t.widget<TextFormField>(find.byKey(const Key('staffFirst'))).enabled, isFalse);
      expect(find.text(_l('ru').rightsReadOnly), findsOneWidget);
      expect(t.widget<SwitchListTile>(find.byKey(const Key('staffRight_can_view_sale'))).onChanged, isNull);
    });

    testWidgets('«Настройки» без права править сотрудников: доступы можно менять, личные данные — нет', (t) async {
      phone(t);
      final api = _Api(okServer());
      await t.pumpWidget(app(host(Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true)), const {}, []), api, _settingsMgr));
      await openForm(t);
      expect(t.widget<TextFormField>(find.byKey(const Key('staffFirst'))).enabled, isFalse);
      await tapKey(t, 'staffRight_can_view_orders');
      await tapKey(t, 'staffSave');
      expect(api.bodies['PATCH api/users/employees/7/'], {'can_view_orders': true});
    });

    testWidgets('свои включённые права выключить нельзя', (t) async {
      phone(t);
      await t.pumpWidget(app(host(Employee.fromJson(_emp('adm', 'Админ', '', active: true)), const {}, []), _Api(okServer()), _admin));
      await openForm(t);
      await t.scrollUntilVisible(find.byKey(const Key('staffRight_can_view_sale')), 300, scrollable: find.byType(Scrollable).first);
      expect(t.widget<SwitchListTile>(find.byKey(const Key('staffRight_can_view_sale'))).onChanged, isNull);
      await t.scrollUntilVisible(find.byKey(const Key('staffRight_can_view_orders')), 300, scrollable: find.byType(Scrollable).first);
      expect(t.widget<SwitchListTile>(find.byKey(const Key('staffRight_can_view_orders'))).onChanged, isNotNull);
    });

    testWidgets('необязательные поля: показываются только если сервер их вернул (отчество, дата найма), дата — выбором и очищается', (t) async {
      phone(t);
      final api = _Api(okServer());
      final withExtras = Employee.fromJson(_emp('7', 'Нурлан', 'Б.', active: true, more: {'middle_name': 'Асанович', 'hire_date': '2025-03-01T00:00:00Z'}));
      await t.pumpWidget(app(host(withExtras, const {}, []), api, _admin));
      await openForm(t);
      expect(find.text('Асанович'), findsOneWidget);
      expect(find.text('01.03.2025'), findsOneWidget);
      expect(find.text(_l('ru').empBirth), findsNothing, reason: 'сервер даты рождения не отдаёт');
      await t.tap(find.byTooltip(_l('ru').empClearDate));
      await t.pumpAndSettle();
      await tapKey(t, 'staffSave');
      expect(api.bodies['PATCH api/users/employees/7/'], {'hire_date': null});
    });

    for (final cfg in [('ky', true), ('uz', false), ('tr', true), ('en', false)]) {
      for (final scale in [1.0, 1.3]) {
        testWidgets('вёрстка 360 dp, ${cfg.$1}, шрифт $scale${cfg.$2 ? ', тёмная' : ''}: список и карточка без переполнения', (t) async {
          phone(t, scale: scale, logical: const Size(360, 640));
          final rows = [
            _emp('7', 'Нурлан-Бекболот уулу', 'Абдурахмановичев', active: true, more: {'middle_name': 'Асанович', 'hire_date': '2025-03-01', 'birth_date': '1990-01-02', 'address': 'г. Бишкек, ул. Очень-Длинная-Улица-Названия 1', 'comment': 'x', 'can_use_pos': true}),
            _emp('8', 'Уволенный', 'Сотрудник', active: false),
          ];
          final api = _Api(okServer(rows: rows));
          await t.pumpWidget(app(const StaffScreen(), api, _owner, lang: cfg.$1, dark: cfg.$2));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull);
          await t.tap(find.byType(FloatingActionButton));
          await t.pumpAndSettle();
          expect(t.takeException(), isNull, reason: 'форма создания');
          Navigator.of(t.element(find.byType(Scaffold).last)).pop();
          await t.pumpAndSettle();
          await t.tap(find.textContaining('Нурлан-Бекболот').first);
          await t.pumpAndSettle();
          expect(t.takeException(), isNull, reason: 'карточка');
          await t.scrollUntilVisible(find.byKey(const Key('staffDismiss')), 300, scrollable: find.byType(Scrollable).first);
          await t.pumpAndSettle();
          expect(t.takeException(), isNull, reason: 'низ карточки');
          expect(t.getSize(find.byKey(const Key('staffSave'))).height, greaterThanOrEqualTo(48));
        });
      }
    }
  });

  // ------------------------------------------------------------------------------------------------ ИИ и большой состав
  group('«Доступ к ИИ» и большой состав', () {
    test('150 сотрудников: из любого сохранённого списка остаются первые двое; выбор третьего отклоняется', () {
      final big = AiAccessPolicy.fromJson({'users': [for (var i = 0; i < 150; i++) {'id': 'u$i', 'name': 'N$i'}]});
      expect(big.users!.length, 2);
      final picker = AiPicker();
      expect(picker.toggle(const AiAccessUser('u1', 'A')), AiPick.added);
      expect(picker.toggle(const AiAccessUser('u2', 'B')), AiPick.added);
      expect(picker.toggle(const AiAccessUser('u3', 'C')), AiPick.full);
      expect(picker.toggle(const AiAccessUser('U1', 'A')), AiPick.removed, reason: 'id без учёта регистра');
    });

    testWidgets('большой состав: поиск, уволенные не предлагаются (выбранный остаётся), сохранение — ровно двое', (t) async {
      phone(t, logical: const Size(360, 900));
      final patches = <Object?>[];
      final rows = [for (var i = 0; i < 120; i++) _emp('u$i', 'Сотр$i', 'Ф', active: i != 7)];
      final api = _Api((m, p, b, q) async {
        if (m == 'PATCH') {
          patches.add(b);
          return {};
        }
        if (p == AiAccessRepository.path) {
          return {
            'settings': {
              'AiAccess': {
                'users': [
                  {'id': 'u7', 'name': 'Сотр7 Ф'},
                ],
              },
            },
          };
        }
        if (p == 'api/users/employees/') return {'results': rows.sublist(0, (q?['page'] == 1) ? 100 : 120).skip((q?['page'] == 1) ? 0 : 100).toList(), 'next': q?['page'] == 1 ? 'x' : null};
        throw ApiException('нет $p', status: 404);
      }, _owner);
      await t.pumpWidget(app(Scaffold(body: SingleChildScrollView(child: AiAccessPanel())), api, _owner));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('aiAccSearch')), findsOneWidget);
      // уволенный u7 уже выбран — остаётся в списке и отмечен
      await t.enterText(find.byKey(const Key('aiAccSearch')), 'сотр110');
      await t.pumpAndSettle();
      expect(find.byKey(const Key('aiAccRow_u110')), findsOneWidget);
      await t.tap(find.byKey(const Key('aiAccRow_u110')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('aiAccRow_u7')), findsOneWidget, reason: 'выбранный виден при любом поиске');
      await t.tap(find.byType(FilledButton));
      await t.pumpAndSettle();
      final users = ((patches.single as Map)['AiAccess'] as Map)['users'] as List;
      expect(users.length, 2);
    });
  });
}
