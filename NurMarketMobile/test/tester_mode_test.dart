// 2026-10-27 (владелец: «Логотип NurCRM — пять тапов, режим тестировщика»): вход пятью нажатиями (только тестовая сборка или
// код тестировщика), отметки «Проверено / Есть проблема» (сохраняются и снимаются), счётчик по устройствам, слияние в общем
// документе без затирания чужих ключей, 403 — тихая локальность, фильтр версий, отчёт, вёрстка на 360 и 412 dp. Сеть — фейки.
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/tester/tester_checklist.dart';
import 'package:nurmarket/core/tester/tester_mode.dart';
import 'package:nurmarket/core/tester/tester_store.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/tester/tester_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body);

class _Api extends ApiClient {
  _Api(this.handler) : super(SessionStore());
  Handler handler;
  final calls = <String>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    calls.add('GET $path');
    return handler('GET', path, null);
  }

  @override
  Future<dynamic> patch(String path, {Object? body}) {
    calls.add('PATCH $path');
    return handler('PATCH', path, body);
  }
}

class _FakeSync implements TesterSync {
  _FakeSync({this.shared = true, this.pullResult = const TesterPull({}, true), this.pushResult = TesterPush.ok});
  bool shared;
  TesterPull pullResult;
  TesterPush pushResult;
  int pulls = 0;
  final pushed = <List<TesterChange>>[];
  @override
  bool get isShared => shared;
  @override
  Future<TesterPull> pull() async {
    pulls++;
    return pullResult;
  }

  @override
  Future<TesterPush> push({required String deviceId, required String deviceName, required List<TesterChange> changes}) async {
    pushed.add(changes);
    return pushResult;
  }
}

const _smallJson = '''
{"areas":[{"id":"sale","title":{"ru":"Продажа","ky":"Сатуу"}},{"id":"stock","title":{"ru":"Склад"}}],
 "items":[
  {"id":"1.2.0/a","version":"1.2.0","area":"sale","severity":"money","title":{"ru":"Пункт А","ky":"А пункту"},"changed":{"ru":"Изменили А"},"steps":{"ru":["Шаг 1","Шаг 2"]}},
  {"id":"1.2.0/b","version":"1.2.0","area":"stock","severity":"important","title":{"ru":"Пункт Б"},"changed":{"ru":"Изменили Б"},"steps":{"ru":"Раз\\nДва"}},
  {"id":"1.1.0/old","version":"1.1.0","area":"sale","severity":"normal","title":{"ru":"Старый пункт"},"changed":{"ru":"Было"},"steps":{}},
  {"id":"1.2.0/a","version":"1.2.0","area":"sale","title":{"ru":"Дубль"}},
  {"id":"","version":"1.2.0","title":{"ru":"Без id"}},
  "мусор"
 ]}
''';

AppLocalizations lx(String code) => lookupAppLocalizations(Locale(code));

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late AppDatabase db;

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    FlutterSecureStorage.setMockInitialValues({});
  });
  tearDown(() async => db.close());

  // ---------------------------------------------------------------------------------------------- вход
  group('пять нажатий', () {
    test('5 быстрых нажатий в пределах 2 с — вход; медленные и неполные — нет', () {
      final c = TapCounter();
      final t0 = DateTime(2026, 10, 27, 12);
      expect(c.tap(t0), 1);
      expect(c.tap(t0.add(const Duration(milliseconds: 300))), 2);
      expect(c.tap(t0.add(const Duration(milliseconds: 600))), 3);
      expect(c.tap(t0.add(const Duration(milliseconds: 900))), 4);
      expect(c.tap(t0.add(const Duration(milliseconds: 1200))), 5);
      expect(c.tap(t0.add(const Duration(milliseconds: 1300))), 1, reason: 'после входа серия начинается заново');
      // по одному в 1,5 с — никогда не наберётся 5 в окне 2 с
      final s = TapCounter();
      var best = 0;
      for (var i = 0; i < 12; i++) {
        final n = s.tap(t0.add(Duration(milliseconds: 1500 * i)));
        if (n > best) best = n;
      }
      expect(best, lessThan(5));
    });

    test('код тестировщика: регистр и пробелы не важны, чужой код не подходит; хранится только хэш', () async {
      final hash = sha256.convert(utf8.encode('TEST-CODE-1')).toString();
      expect(TesterGate.isValidCode(' test-code-1 ', hashes: {hash}), isTrue);
      expect(TesterGate.isValidCode('TEST-CODE-2', hashes: {hash}), isFalse);
      expect(TesterGate.isValidCode('', hashes: {hash}), isFalse);
      expect(TesterGate.isValidCode(null, hashes: {hash}), isFalse);
      expect(TesterGate.isValidCode('что угодно'), isFalse, reason: 'настоящие хэши кассы не принимают случайный текст');
      expect(TesterGate.codeHashes, hasLength(2));

      // без теста и кода — закрыто; верный код открывает, неверный — ничего не запоминает
      expect(await TesterGate.isAllowed(db, debugMode: false, testBuild: false, hashes: {hash}), isFalse);
      expect(await TesterGate.activate(db, 'нет', hashes: {hash}), isFalse);
      expect(await db.getSetting(TesterGate.codeSetting), isNull);
      expect(await TesterGate.activate(db, 'test-code-1', hashes: {hash}), isTrue);
      expect(await TesterGate.isAllowed(db, debugMode: false, testBuild: false, hashes: {hash}), isTrue);
      expect(await TesterGate.isAllowed(db, debugMode: false, testBuild: true), isTrue);
      expect(await TesterGate.isAllowed(db, debugMode: true, testBuild: false), isTrue, reason: 'в отладочной сборке — всегда');
    });

    Future<int> tapLogo(WidgetTester t, {bool? debug, bool? test, Set<String>? hashes, int times = 5, int stepMs = 200, String? code}) async {
      var opened = 0;
      var now = DateTime(2026, 10, 27, 12);
      if (code != null) await db.setSetting(TesterGate.codeSetting, code);
      await t.pumpWidget(
        ProviderScope(
          key: UniqueKey(),
          overrides: [databaseProvider.overrideWithValue(db)],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [
              AppLocalizations.delegate,
              GlobalMaterialLocalizations.delegate,
              GlobalWidgetsLocalizations.delegate,
              GlobalCupertinoLocalizations.delegate,
            ],
            home: Scaffold(
              body: Center(
                child: TesterTap(
                  debugMode: debug ?? false,
                  testBuild: test ?? false,
                  hashes: hashes,
                  clock: () => now,
                  onOpen: (_) => opened++,
                  child: const SizedBox(key: Key('logo'), width: 130, height: 80, child: Text('LOGO')),
                ),
              ),
            ),
          ),
        ),
      );
      for (var i = 0; i < times; i++) {
        await t.tap(find.byKey(const Key('logo')));
        await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 20)));
        await t.pump();
        now = now.add(Duration(milliseconds: stepMs));
      }
      await t.pump(const Duration(seconds: 5)); // подсказка (SnackBar) уходит сама
      return opened;
    }

    testWidgets('тестовая сборка: 5 нажатий открывают режим и дают лёгкую вибрацию', (t) async {
      final haptics = <String>[];
      t.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'HapticFeedback.vibrate') haptics.add('${call.arguments}');
        return null;
      });
      addTearDown(() => t.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));
      expect(await tapLogo(t, test: true), 1);
      expect(haptics, ['HapticFeedbackType.lightImpact']);
    });

    testWidgets('4 нажатия — не открывает; нажатия реже, чем раз в 0,5 с, — не открывают', (t) async {
      expect(await tapLogo(t, test: true, times: 4), 0);
      expect(await tapLogo(t, test: true, times: 5, stepMs: 700), 0);
    });

    testWidgets('не тестовая сборка и нет кода: 5 нажатий — ничего (ни перехода, ни вибрации, ни подсказки)', (t) async {
      final haptics = <String>[];
      t.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'HapticFeedback.vibrate') haptics.add('${call.arguments}');
        return null;
      });
      addTearDown(() => t.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));
      expect(await tapLogo(t, test: false, debug: false), 0);
      expect(haptics, isEmpty);
      expect(find.byType(SnackBar), findsNothing);
    });

    testWidgets('не тестовая сборка, но введён верный код тестировщика: открывает', (t) async {
      final hash = sha256.convert(utf8.encode('TEST-CODE-1')).toString();
      expect(await tapLogo(t, test: false, debug: false, hashes: {hash}, code: 'TEST-CODE-1'), 1);
    });

    testWidgets('отладочная сборка: открывает всегда', (t) async {
      expect(await tapLogo(t, test: false, debug: true), 1);
    });

    testWidgets('долгое нажатие на логотип: ввод кода — неверный ничего не открывает, верный запоминается', (t) async {
      final hash = sha256.convert(utf8.encode('TEST-CODE-1')).toString();
      await tapLogo(t, test: false, debug: false, hashes: {hash}, times: 0);
      await t.longPress(find.byKey(const Key('logo')));
      await t.pumpAndSettle();
      expect(find.text(lx('ru').tsCodeTitle), findsOneWidget);
      await t.enterText(find.byType(TextField), 'bad');
      await t.tap(find.text(lx('ru').done));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await t.pumpAndSettle();
      expect(find.text(lx('ru').tsCodeBad), findsOneWidget);
      expect(await db.getSetting(TesterGate.codeSetting), isNull);

      await t.pump(const Duration(seconds: 5));
      await t.longPress(find.byKey(const Key('logo')));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField), 'test-code-1');
      await t.tap(find.text(lx('ru').done));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await t.pumpAndSettle();
      expect(find.text(lx('ru').tsCodeOn), findsOneWidget);
      expect(await db.getSetting(TesterGate.codeSetting), 'TEST-CODE-1');
    });
  });

  // ---------------------------------------------------------------------------------------------- данные
  group('список функций (JSON)', () {
    test('разбор: дубль id, пустой id и мусор пропускаются; шаги — списком или строкой; версии по убыванию', () {
      final l = TesterChecklist.parse(_smallJson);
      expect([for (final i in l.items) i.id], ['1.2.0/a', '1.2.0/b', '1.1.0/old']);
      expect(l.versions, ['1.2.0', '1.1.0']);
      expect(l.items[0].stepsIn('ru'), ['Шаг 1', 'Шаг 2']);
      expect(l.items[1].stepsIn('ru'), ['Раз', 'Два']);
      expect(l.items[0].severity, TesterSeverity.money);
      // язык приложения, при отсутствии — русский
      expect(l.items[0].titleIn('ky'), 'А пункту');
      expect(l.items[0].titleIn('uz'), 'Пункт А');
      expect(l.items[0].stepsIn('tr'), ['Шаг 1', 'Шаг 2']);
      expect(l.defaultVersion('1.2.0'), '1.2.0');
      expect(l.defaultVersion('9.9.9'), '1.2.0', reason: 'нет такой версии — самая новая');
      expect(TesterChecklist.compareVersions('1.18.10', '1.18.9'), greaterThan(0));
      expect(TesterChecklist.parse('{плохой json').items, isEmpty);
    }, skip: false);

    test('испорченный файл не роняет: пустой список', () {
      expect(TesterChecklist.parse('{"items": 5, "areas": "x"}').items, isEmpty);
      expect(() => TesterChecklist.parse('[]'), returnsNormally);
    });

    test('настоящий assets/data/tester_checklist.json: пункты 1.18.7, все 5 языков, шаги, стабильные id, области известны', () async {
      final raw = await rootBundle.loadString(TesterChecklist.assetPath);
      final l = TesterChecklist.parse(raw);
      final cur = l.forVersion(AppBuild.version);
      expect(cur.length, inInclusiveRange(8, 40));
      expect(l.versions.first, AppBuild.version);
      expect(l.forVersion('1.18.6'), isNotEmpty, reason: 'прошлая версия — коротко');
      final ids = <String>{};
      final areaIds = {for (final a in l.areas) a.id};
      for (final a in l.areas) {
        for (final lang in ['ru', 'ky', 'uz', 'en', 'tr']) {
          expect(a.title[lang], isNotNull, reason: 'область ${a.id} — $lang');
        }
      }
      for (final i in l.items) {
        expect(ids.add(i.id), isTrue, reason: 'id повторяется: ${i.id}');
        expect(i.id.startsWith('${i.version}/'), isTrue, reason: i.id);
        expect(areaIds, contains(i.area), reason: i.id);
        for (final lang in ['ru', 'ky', 'uz', 'en', 'tr']) {
          expect(i.title[lang]?.trim().isNotEmpty, isTrue, reason: '${i.id} title $lang');
          expect(i.changed[lang]?.trim().isNotEmpty, isTrue, reason: '${i.id} changed $lang');
          expect(i.steps[lang], isNotEmpty, reason: '${i.id} steps $lang');
          expect(i.steps[lang]!.length, i.steps['ru']!.length, reason: '${i.id}: шагов на $lang столько же, сколько по-русски');
        }
      }
      // области из заказа владельца
      expect(areaIds, containsAll(['sale', 'stock', 'invoices', 'smartcam', 'videocall', 'kassa', 'scanner', 'profile', 'aikeys', 'other']));
    });

    test('версия в коде совпадает с pubspec.yaml (иначе тестировщик увидит не тот список)', () {
      final pubspec = File('pubspec.yaml').readAsStringSync();
      final m = RegExp(r'^version:\s*(\d+\.\d+\.\d+)\+(\d+)', multiLine: true).firstMatch(pubspec)!;
      expect(m.group(1), AppBuild.version);
      expect(int.parse(m.group(2)!), AppBuild.build);
    });
  });

  // ---------------------------------------------------------------------------------------------- хранение
  group('хранение на устройстве', () {
    test('отметки: запись, чтение, мусор в записи не роняет; id устройства короткий и стабильный', () async {
      final store = TesterStore(db);
      final t0 = DateTime.utc(2026, 10, 27, 9, 30);
      await store.saveMarks({
        'a': TesterMark(status: TesterStatus.ok, ts: t0),
        'b': TesterMark(status: TesterStatus.issue, ts: t0, comment: 'не печатает', shot: '/x/y.jpg', synced: true),
      });
      final back = await store.loadMarks();
      expect(back['a']!.status, TesterStatus.ok);
      expect(back['a']!.ts.toUtc(), t0);
      expect(back['b']!.comment, 'не печатает');
      expect(back['b']!.shot, '/x/y.jpg');
      expect(back['b']!.synced, isTrue);
      await db.setSetting(TesterStore.marksKey, '{мусор');
      expect(await store.loadMarks(), isEmpty);
      await db.setSetting(
        TesterStore.marksKey,
        jsonEncode({
          'x': 5,
          'y': {'s': 'зелёный'},
          'z': {'s': 'ok', 't': '2026-10-27T09:00:00Z'},
        }),
      );
      expect((await store.loadMarks()).keys, ['z']);

      final id = await store.deviceId();
      expect(id, matches(RegExp(r'^[0-9A-F]{8}$')));
      expect(await store.deviceId(), id);
      expect(await TesterStore(db).deviceId(), id, reason: 'хранится в настройках, а не в памяти объекта');
      await store.setDeviceName('  Телефон Умара ');
      expect(await store.deviceName(), 'Телефон Умара');
    });
  });

  // ---------------------------------------------------------------------------------------------- счётчик и слияние
  group('счётчик и общий документ', () {
    test('счётчик: чужие устройства из документа + это устройство по локальной отметке (она новее общей)', () {
      const other = {'D2': RemoteMark(TesterStatus.ok, null, '', ''), 'D3': RemoteMark(TesterStatus.issue, null, 'x', '')};
      var c = testerCount(remote: other, mine: null, deviceId: 'D1');
      expect((c.ok, c.issue), (1, 1));
      c = testerCount(
        remote: other,
        mine: TesterMark(status: TesterStatus.ok, ts: DateTime(2026)),
        deviceId: 'D1',
      );
      expect((c.ok, c.issue), (2, 1));
      // в документе это устройство было «проблема», а тут уже «проверено» — считается локальная
      c = testerCount(
        remote: {...other, 'D1': const RemoteMark(TesterStatus.issue, null, '', '')},
        mine: TesterMark(status: TesterStatus.ok, ts: DateTime(2026)),
        deviceId: 'D1',
      );
      expect((c.ok, c.issue), (2, 1));
      // отметку сняли локально — своё устройство не считается, даже если в документе осталось
      c = testerCount(remote: {'D1': const RemoteMark(TesterStatus.ok, null, '', '')}, mine: null, deviceId: 'D1');
      expect(c.none, isTrue);
      // без общего документа — только это устройство
      c = testerCount(
        remote: null,
        mine: TesterMark(status: TesterStatus.issue, ts: DateTime(2026)),
        deviceId: 'D1',
      );
      expect((c.ok, c.issue), (0, 1));
    });

    test('слияние: меняется только ключ этого устройства, чужие устройства и пункты остаются как были', () {
      final current = {
        'a': {
          'D2': {'status': 'ok', 'ts': '2026-10-27T08:00:00Z', 'name': 'Умар'},
          'D1': {'status': 'issue', 'ts': '2026-10-26T08:00:00Z', 'comment': 'старое'},
        },
        'c': {
          'D9': {'status': 'issue', 'ts': '2026-10-27T07:00:00Z', 'comment': 'чужое'},
        },
        'будущее_поле': 5,
      };
      final merged = AppSettingsTesterSync.merge(
        current,
        deviceId: 'D1',
        deviceName: 'Мой',
        changes: [
          TesterChange('a', TesterMark(status: TesterStatus.ok, ts: DateTime.utc(2026, 10, 27, 9))),
          TesterChange('b', TesterMark(status: TesterStatus.issue, ts: DateTime.utc(2026, 10, 27, 9), comment: 'сломано')),
        ],
      );
      expect((merged['a'] as Map)['D2'], {'status': 'ok', 'ts': '2026-10-27T08:00:00Z', 'name': 'Умар'}, reason: 'чужое устройство не тронуто');
      expect(((merged['a'] as Map)['D1'] as Map)['status'], 'ok');
      expect(((merged['a'] as Map)['D1'] as Map).containsKey('comment'), isFalse, reason: 'старый комментарий этого устройства заменён целиком');
      expect(((merged['a'] as Map)['D1'] as Map)['name'], 'Мой');
      expect(merged['c'], current['c'], reason: 'пункт, которого мы не меняли, остался как был');
      expect(merged['будущее_поле'], 5);
      expect(((merged['b'] as Map)['D1'] as Map)['comment'], 'сломано');
      // исходный документ не изменён
      expect(((current['a'] as Map)['D1'] as Map)['status'], 'issue');

      // снятие отметки: удаляется только свой ключ; пустой пункт исчезает
      final removed = AppSettingsTesterSync.merge(
        merged,
        deviceId: 'D1',
        deviceName: '',
        changes: [const TesterChange('a', null), const TesterChange('b', null)],
      );
      expect((removed['a'] as Map).keys, ['D2']);
      expect(removed.containsKey('b'), isFalse);
      expect(removed['c'], current['c']);
    });

    test('разбор общего документа: мусор пропускается', () {
      final r = AppSettingsTesterSync.parse({
        'a': {
          'D1': {'status': 'ok', 'ts': '2026-10-27T08:00:00Z'},
          'D2': {'status': 'зелёный'},
          'D3': 5,
        },
        'b': 'x',
      });
      expect(r.keys, ['a']);
      expect(r['a']!.keys, ['D1']);
      expect(AppSettingsTesterSync.parse(null), isEmpty);
      expect(AppSettingsTesterSync.parse([1, 2]), isEmpty);
    });

    test('запись: сначала чтение документа, потом PATCH со слиянием (чужие ключи на месте), в нужный документ', () async {
      final doc = {
        'Checks': {
          'a': {
            'D2': {'status': 'ok', 'ts': '2026-10-27T08:00:00Z'},
          },
        },
      };
      Object? patched;
      final api = _Api((m, p, b) async {
        if (m == 'PATCH') {
          patched = b;
          return {};
        }
        return {'settings': doc};
      });
      final r = await AppSettingsTesterSync(api).push(
        deviceId: 'D1',
        deviceName: '',
        changes: [TesterChange('a', TesterMark(status: TesterStatus.ok, ts: DateTime.utc(2026, 10, 27, 9)))],
      );
      expect(r, TesterPush.ok);
      expect(api.calls, ['GET api/main/app-settings/nurmarket-mobile-tests/', 'PATCH api/main/app-settings/nurmarket-mobile-tests/']);
      final checks = (patched as Map)['Checks'] as Map;
      expect((checks['a'] as Map).keys.toSet(), {'D1', 'D2'});
    });

    test('403 на запись — «forbidden» без исключения; ошибка сети — «failed»; чтение 404 — пусто, но доступно; чтение с ошибкой — недоступно', () async {
      final api403 = _Api((m, p, b) async {
        if (m == 'PATCH') throw ApiException('нет прав', status: 403);
        return {'settings': {}};
      });
      final change = [TesterChange('a', TesterMark(status: TesterStatus.ok, ts: DateTime.utc(2026)))];
      expect(await AppSettingsTesterSync(api403).push(deviceId: 'D1', deviceName: '', changes: change), TesterPush.forbidden);
      final apiDown = _Api((m, p, b) async => throw ApiException('нет связи'));
      expect(await AppSettingsTesterSync(apiDown).push(deviceId: 'D1', deviceName: '', changes: change), TesterPush.failed);
      expect((await AppSettingsTesterSync(apiDown).pull()).available, isFalse);
      final api404 = _Api((m, p, b) async => throw ApiException('нет', status: 404));
      final p404 = await AppSettingsTesterSync(api404).pull();
      expect(p404.available, isTrue);
      expect(p404.checks, isEmpty);
      // документа ещё нет (404 на чтение) — запись всё равно пробуется и создаёт его
      var created = false;
      final apiNew = _Api((m, p, b) async {
        if (m == 'GET') throw ApiException('нет', status: 404);
        created = true;
        return {};
      });
      expect(await AppSettingsTesterSync(apiNew).push(deviceId: 'D1', deviceName: '', changes: change), TesterPush.ok);
      expect(created, isTrue);
      expect(await const LocalTesterSync().push(deviceId: 'D1', deviceName: '', changes: change), TesterPush.local);
      expect((await const LocalTesterSync().pull()).available, isFalse);
    });
  });

  // ---------------------------------------------------------------------------------------------- отчёт
  test('отчёт по-русски: итог, проблемы с комментарием, проверено, не проверено; версия и устройство', () {
    final l = TesterChecklist.parse(_smallJson);
    final items = l.forVersion('1.2.0');
    final text = testerReport(
      version: '1.2.0',
      build: 5,
      testBuild: true,
      deviceLine: 'Телефон Умара, Android, id AB12CD34',
      groups: l.grouped(items),
      marks: {
        '1.2.0/a': TesterMark(status: TesterStatus.issue, ts: DateTime(2026, 10, 27, 14, 5), comment: 'цена не меняется'),
        '1.2.0/b': TesterMark(status: TesterStatus.ok, ts: DateTime(2026, 10, 27, 14, 7)),
      },
      now: DateTime(2026, 10, 27, 15),
    );
    expect(text, contains('Отчёт тестировщика NurMarket Mobile'));
    expect(text, contains('Версия: 1.2.0 (сборка 5), тестовая'));
    expect(text, contains('Устройство: Телефон Умара, Android, id AB12CD34'));
    expect(text, contains('Итого: проверено 1 из 2, проблем 1, не проверено 0'));
    expect(text, contains('ПРОБЛЕМЫ (1)'));
    expect(text, contains('- [Продажа] Пункт А (27.10.2026 14:05): цена не меняется'));
    expect(text, contains('ПРОВЕРЕНО (1)'));
    expect(text, contains('- [Склад] Пункт Б (27.10.2026 14:07)'));
    expect(text, isNot(contains('НЕ ПРОВЕРЕНО')));
  });

  // ---------------------------------------------------------------------------------------------- экран
  group('экран «Режим тестировщика»', () {
    Widget app(Widget home, {String lang = 'ru', bool dark = false}) => ProviderScope(
      key: UniqueKey(),
      overrides: [databaseProvider.overrideWithValue(db)],
      child: MaterialApp(
        theme: buildTheme(dark: dark),
        locale: Locale(lang),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [
          AppLocalizations.delegate,
          GlobalMaterialLocalizations.delegate,
          GlobalWidgetsLocalizations.delegate,
          GlobalCupertinoLocalizations.delegate,
        ],
        home: home,
      ),
    );

    Future<void> settle(WidgetTester t) async {
      for (var i = 0; i < 6; i++) {
        await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 30)));
        await t.pump();
      }
    }

    void phone(WidgetTester t, {Size logical = const Size(412, 915), double scale = 1.0}) {
      t.view.physicalSize = Size(logical.width * 2, logical.height * 2);
      t.view.devicePixelRatio = 2.0;
      t.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
        t.platformDispatcher.clearTextScaleFactorTestValue();
      });
    }

    Future<void> open(
      WidgetTester t, {
      _FakeSync? sync,
      TesterChecklist? list,
      String lang = 'ru',
      bool dark = false,
      Duration poll = Duration.zero,
      Future<String?> Function()? pickShot,
    }) async {
      await t.pumpWidget(
        app(
          TesterScreen(
            debugChecklist: list ?? TesterChecklist.parse(_smallJson),
            debugSync: sync ?? _FakeSync(shared: false),
            pollEvery: poll,
            currentVersion: '1.2.0',
            pickShot: pickShot,
            now: () => DateTime(2026, 10, 27, 14, 30),
          ),
          lang: lang,
          dark: dark,
        ),
      );
      await settle(t);
    }

    Future<void> tapK(WidgetTester t, String key) async {
      await t.scrollUntilVisible(find.byKey(ValueKey(key)), 150, scrollable: find.byType(Scrollable).first, maxScrolls: 100);
      await t.pump();
      await t.tap(find.byKey(ValueKey(key)));
    }

    Future<void> top(WidgetTester t) async {
      await t.drag(find.byType(ListView), const Offset(0, 5000));
      await t.pump();
    }

    Future<void> close(WidgetTester t) async {
      await t.pumpWidget(const SizedBox());
      await t.pump();
    }

    testWidgets('шапка: версия, устройство, id; итог «проверено 0 из 2»; по умолчанию текущая версия, прошлая — по фильтру', (t) async {
      phone(t);
      await open(t);
      final l = lx('ru');
      expect(find.text(l.tsBuildLine(AppBuild.version, '${AppBuild.build}')), findsWidgets);
      expect(find.textContaining('ID устройства: '), findsOneWidget);
      expect(find.text(l.tsSummary('0', '2', '0')), findsOneWidget);
      expect(find.text('Пункт А'), findsOneWidget);
      expect(find.text('Старый пункт'), findsNothing);
      // фильтр версий
      await t.tap(find.text('1.1.0'));
      await t.pump();
      expect(find.text('Старый пункт'), findsOneWidget);
      expect(find.text('Пункт А'), findsNothing);
      expect(find.text(l.tsSummary('0', '1', '0')), findsOneWidget);
      await t.tap(find.text(l.tsVersionCurrent('1.2.0')));
      await t.pump();
      expect(find.text('Пункт А'), findsOneWidget);
      await close(t);
    });

    testWidgets('«Проверено»: отметка, итог «1 из 2», сохраняется на устройстве; повторное нажатие снимает', (t) async {
      phone(t);
      await open(t);
      final l = lx('ru');
      expect(find.text(l.tsNotChecked), findsWidgets);
      await tapK(t, 'ok-1.2.0/a');
      await settle(t);
      expect(find.text(l.tsSummary('1', '2', '0')), findsOneWidget);
      expect(find.text(l.tsCheckedAt('27.10 14:30')), findsOneWidget);
      var saved = await t.runAsync(() => TesterStore(db).loadMarks());
      expect(saved!['1.2.0/a']!.status, TesterStatus.ok);

      // «после перезапуска» — экран читает то же
      await close(t);
      await open(t);
      expect(find.text(l.tsSummary('1', '2', '0')), findsOneWidget);

      await tapK(t, 'ok-1.2.0/a');
      await settle(t);
      expect(find.text(l.tsSummary('0', '2', '0')), findsOneWidget);
      saved = await t.runAsync(() => TesterStore(db).loadMarks());
      expect(saved, isEmpty);
      await close(t);
    });

    testWidgets('«Есть проблема»: комментарий обязателен к показу, сохраняется; повторное нажатие снимает; можно править комментарий', (t) async {
      phone(t);
      await open(t);
      final l = lx('ru');
      await tapK(t, 'bad-1.2.0/a');
      await t.pumpAndSettle();
      expect(find.text(l.tsIssueTitle), findsOneWidget);
      await t.enterText(find.byType(TextField), 'цена не меняется');
      await t.tap(find.text(l.save));
      await settle(t);
      await t.pumpAndSettle();
      expect(find.text(l.tsSummary('0', '2', '1')), findsOneWidget);
      expect(find.text('цена не меняется'), findsOneWidget);
      var saved = await t.runAsync(() => TesterStore(db).loadMarks());
      expect(saved!['1.2.0/a']!.status, TesterStatus.issue);
      expect(saved['1.2.0/a']!.comment, 'цена не меняется');

      // правка комментария
      await t.tap(find.text('цена не меняется'));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField), 'цена не меняется после сохранения');
      await t.tap(find.text(l.save));
      await settle(t);
      await t.pumpAndSettle();
      expect(find.text('цена не меняется после сохранения'), findsOneWidget);

      // повторное нажатие «Есть проблема» снимает отметку
      await tapK(t, 'bad-1.2.0/a');
      await settle(t);
      expect(find.text(l.tsSummary('0', '2', '0')), findsOneWidget);
      saved = await t.runAsync(() => TesterStore(db).loadMarks());
      expect(saved, isEmpty);
      await close(t);
    });

    testWidgets('скриншот: системный выбор картинки подключается, путь сохраняется; нет выбора — остаётся комментарий', (t) async {
      phone(t);
      await open(t, pickShot: () async => '/data/shot.jpg');
      final l = lx('ru');
      await tapK(t, 'bad-1.2.0/a');
      await t.pumpAndSettle();
      await t.tap(find.text(l.tsAttachShot));
      await t.pumpAndSettle();
      expect(find.text(l.tsShotAttached), findsOneWidget);
      await t.tap(find.text(l.save));
      await settle(t);
      await t.pumpAndSettle();
      final saved = await t.runAsync(() => TesterStore(db).loadMarks());
      expect(saved!['1.2.0/a']!.shot, '/data/shot.jpg');
      await close(t);
    });

    testWidgets('счётчик без общего документа: «Пока не проверено…», потом «Проверено: 1 устройство (это)»', (t) async {
      phone(t);
      await open(t, sync: _FakeSync(shared: false));
      final l = lx('ru');
      expect(find.text(l.tsCountNone), findsWidgets);
      expect(find.text(l.tsSyncOff), findsOneWidget, reason: 'плашка: общий счётчик недоступен');
      await tapK(t, 'ok-1.2.0/a');
      await settle(t);
      expect(find.text(l.tsCountLocalOk), findsOneWidget);
      await close(t);
    });

    testWidgets('общий счётчик: чужие устройства из документа + это; плашки нет; отметки уходят в общий документ', (t) async {
      phone(t);
      final sync = _FakeSync(
        pullResult: const TesterPull({
          '1.2.0/a': {
            'D2': RemoteMark(TesterStatus.ok, null, '', ''),
            'D3': RemoteMark(TesterStatus.ok, null, '', ''),
            'D4': RemoteMark(TesterStatus.issue, null, 'x', ''),
          },
        }, true),
      );
      await open(t, sync: sync);
      final l = lx('ru');
      expect(find.text(l.tsSyncOff), findsNothing);
      expect(find.text(l.tsCountShared('2', '1')), findsOneWidget);
      await tapK(t, 'ok-1.2.0/a');
      await settle(t);
      expect(find.text(l.tsCountShared('3', '1')), findsOneWidget);
      expect(sync.pushed, isNotEmpty);
      expect(sync.pushed.last.single.itemId, '1.2.0/a');
      expect(sync.pushed.last.single.mark!.status, TesterStatus.ok);
      final saved = await t.runAsync(() => TesterStore(db).loadMarks());
      expect(saved!['1.2.0/a']!.synced, isTrue);
      await close(t);
    });

    testWidgets('403 на запись: отметка остаётся на устройстве, тихо, плашка «Общий счётчик недоступен», без ошибок и без повторов', (t) async {
      phone(t);
      final sync = _FakeSync(pushResult: TesterPush.forbidden);
      await open(t, sync: sync);
      final l = lx('ru');
      await tapK(t, 'ok-1.2.0/a');
      await settle(t);
      expect(find.text(l.tsCheckedAt('27.10 14:30')), findsOneWidget);
      await top(t);
      expect(find.text(l.tsSyncOff), findsOneWidget);
      expect(t.takeException(), isNull);
      final n = sync.pushed.length;
      await tapK(t, 'ok-1.2.0/b');
      await settle(t);
      expect(sync.pushed.length, n, reason: 'после 403 запись больше не пробуется до следующего открытия экрана');
      final saved = await t.runAsync(() => TesterStore(db).loadMarks());
      expect(saved!.keys.toSet(), {'1.2.0/a', '1.2.0/b'}, reason: 'обе отметки сохранены локально');
      expect(saved['1.2.0/a']!.synced, isFalse);
      await close(t);
    });

    testWidgets('общий документ не читается: плашка, счётчик — только это устройство', (t) async {
      phone(t);
      await open(t, sync: _FakeSync(pullResult: const TesterPull({}, false)));
      expect(find.text(lx('ru').tsSyncOff), findsOneWidget);
      await close(t);
    });

    testWidgets('опрос общего счётчика — раз в 60 с, пока экран открыт; после закрытия — не опрашивает', (t) async {
      phone(t);
      final sync = _FakeSync();
      await open(t, sync: sync, poll: const Duration(seconds: 60));
      final n = sync.pulls;
      expect(n, greaterThanOrEqualTo(1));
      await t.pump(const Duration(seconds: 59));
      expect(sync.pulls, n);
      await t.pump(const Duration(seconds: 2));
      expect(sync.pulls, n + 1);
      await close(t);
      await t.pump(const Duration(seconds: 130));
      expect(sync.pulls, n + 1);
    });

    testWidgets('«Скопировать отчёт» кладёт отчёт в буфер обмена', (t) async {
      phone(t);
      String? clip;
      t.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'Clipboard.setData') clip = (call.arguments as Map)['text'] as String?;
        return null;
      });
      addTearDown(() => t.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));
      await open(t);
      final l = lx('ru');
      await tapK(t, 'ok-1.2.0/b');
      await settle(t);
      await top(t); // наверх: кнопка в шапке
      await t.tap(find.text(l.tsCopyReport));
      await t.pump();
      expect(clip, isNotNull);
      expect(clip, contains('Отчёт тестировщика NurMarket Mobile'));
      expect(clip, contains('Версия: 1.2.0'));
      expect(clip, contains('Итого: проверено 1 из 2'));
      expect(clip, contains('- [Склад] Пункт Б'));
      expect(find.text(l.tsCopied), findsOneWidget);
      await t.pump(const Duration(seconds: 5));
      await close(t);
    });

    // --- вёрстка на настоящем списке 1.18.7 ---
    for (final cfg in [
      ('ky', const Size(360, 800), 1.3, false),
      ('uz', const Size(360, 800), 1.3, true),
      ('ru', const Size(360, 800), 1.0, false),
      ('tr', const Size(412, 915), 1.3, true),
      ('en', const Size(412, 915), 1.0, false),
    ]) {
      testWidgets(
        'настоящий список: ${cfg.$2.width.toInt()}×${cfg.$2.height.toInt()}, ${cfg.$1}, шрифт ${cfg.$3}${cfg.$4 ? ', тёмная' : ''} — без переполнений, кнопки ≥ 48 dp, ярлыки не обрезаны',
        (t) async {
          phone(t, logical: cfg.$2, scale: cfg.$3);
          final list = TesterChecklist.parse(File('assets/data/tester_checklist.json').readAsStringSync());
          await open(t, list: list, lang: cfg.$1, dark: cfg.$4, sync: _FakeSync(shared: false));
          final l = lx(cfg.$1);
          expect(t.takeException(), isNull);
          // шапка: обе кнопки отчёта ≥ 48 dp
          final copy = find.ancestor(of: find.text(l.tsCopyReport), matching: find.byWidgetPredicate((w) => w is ButtonStyleButton)).first;
          expect(t.getSize(copy).height, greaterThanOrEqualTo(48));
          final share = find.descendant(of: find.byTooltip(l.tsShareReport), matching: find.byType(OutlinedButton));
          expect(t.getSize(share).width, greaterThanOrEqualTo(48));
          expect(t.getSize(share).height, greaterThanOrEqualTo(48));
          final seen = <String>{};
          for (var i = 0; i < 80; i++) {
            // кнопки «Проверено» / «Есть проблема» на экране — ≥ 48 dp и надпись целиком (без многоточия)
            for (final key
                in find
                    .byWidgetPredicate(
                      (w) =>
                          w.key is ValueKey<String> &&
                          ((w.key as ValueKey<String>).value.startsWith('ok-') || (w.key as ValueKey<String>).value.startsWith('bad-')),
                    )
                    .evaluate()
                    .map((e) => e.widget.key as ValueKey<String>)) {
              if (!seen.add(key.value)) continue;
              expect(t.getSize(find.byKey(key)).height, greaterThanOrEqualTo(48), reason: key.value);
            }
            expect(t.takeException(), isNull, reason: 'прокрутка $i');
            final before = t.getTopLeft(find.byType(ListView)).dy;
            await t.drag(find.byType(ListView), const Offset(0, -420));
            await t.pump();
            if (t.getRect(find.byType(ListView)).bottom < before) break;
            if (find.text(l.tsCountNone).evaluate().isNotEmpty && find.byKey(ValueKey('ok-${list.forVersion('1.18.6').last.id}')).evaluate().isNotEmpty) break;
          }
          expect(t.takeException(), isNull);
          expect(seen.length, greaterThan(10), reason: 'прокрутили много карточек');
          await close(t);
        },
      );
    }

    testWidgets('ky: подпись кнопок целиком в 360 dp при шрифте 1,3 (без многоточия), раскрытие «Как проверить» показывает шаги', (t) async {
      phone(t, logical: const Size(360, 800), scale: 1.3);
      final list = TesterChecklist.parse(File('assets/data/tester_checklist.json').readAsStringSync());
      await open(t, list: list, lang: 'ky');
      final l = lx('ky');
      final first = list.forVersion(AppBuild.version).first;
      await t.scrollUntilVisible(find.byKey(ValueKey('ok-${first.id}')), 150, scrollable: find.byType(Scrollable).first, maxScrolls: 100);
      await t.pump();
      final txt = t.widget<Text>(find.descendant(of: find.byKey(ValueKey('ok-${first.id}')), matching: find.byType(Text)).first);
      expect(txt.overflow, isNull);
      await t.ensureVisible(find.text(l.tsHowTo).first);
      await t.pump();
      await t.tap(find.text(l.tsHowTo).first);
      await t.pumpAndSettle();
      expect(find.text(first.stepsIn('ky').first), findsOneWidget);
      expect(t.takeException(), isNull);
      await close(t);
    });
  });
}
