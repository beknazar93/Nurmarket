// 2026-10-29 (срочно, владелец: «Ничего не должно остаться от старого аккаунта!!»): выход и смена аккаунта. В очереди A есть
// продажа → выход блокируется окном; после отправки (подмена очереди) / выгрузки в файл / «УДАЛИТЬ» выход стирает с телефона ВСЁ
// (база, настройки, очередь, секреты, файлы, журнал) — следов A нет. Сервер — фейк, база в памяти, файлы — во временной папке.
import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
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
import 'package:nurmarket/core/sync/account_switch.dart';
import 'package:nurmarket/core/sync/outbox_meta.dart';
import 'package:nurmarket/core/sync/sync_service.dart';
import 'package:nurmarket/core/tester/tester_store.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/profile/logout_guard.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _MemStore extends SessionStore {
  Session? saved;
  @override
  Future<Session?> load() async => saved;
  @override
  Future<void> save(Session s) async => saved = s;
  @override
  Future<void> clear() async => saved = null;
}

/// Сервер двух компаний: у каждого логина свой профиль, своё правило ИИ и свои должники.
class _Server extends ApiClient {
  _Server(super.store);
  bool offline = false;

  static const _users = {
    'a@shop.kg': {'id': 'ua', 'name': 'Анна', 'ai': 'ua', 'role': 'owner', 'lang': 'ky'},
    'b@shop.kg': {'id': 'ub', 'name': 'Борис', 'ai': 'ub', 'role': 'cashier', 'lang': 'en'},
  };

  Map<String, String> get _me => _users[session!.email.toLowerCase()]!;

  @override
  Future<Map<String, dynamic>> login(String email, String password) async {
    if (offline) throw ApiException('Нет связи', status: null);
    attach(Session(email: email.trim(), access: 'tok-$email', refresh: 'r'));
    return {};
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    if (offline) throw ApiException('Нет связи с сервером.');
    final me = _me;
    if (path == 'api/users/profile/') {
      return {'user': {'id': me['id'], 'first_name': me['name'], 'role': me['role'], 'is_owner': me['role'] == 'owner', 'language': me['lang']}};
    }
    if (path == 'api/users/company/') return {'name': 'Магазин ${me['name']}', 'inn': '1${me['id']}', 'address': 'ул. ${me['name']}'};
    if (path == 'api/main/app-settings/nurmarket-mobile/') {
      return {
        'settings': {
          'AiAccess': {'users': [{'id': me['ai'], 'name': me['name']}], 'limit': 2},
        },
      };
    }
    if (path == 'api/main/clients/debtors/') {
      return {'results': [{'client': 'c-${me['id']}', 'full_name': 'Должник ${me['name']}', 'debt_total': 100}]};
    }
    throw ApiException('нет', status: 404);
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late AppDatabase db;
  late Directory dir;
  late _MemStore store;
  late _Server api;
  late ProviderContainer c;
  var online = true;

  setUp(() async {
    FlutterSecureStorage.setMockInitialValues({});
    online = true;
    db = AppDatabase(NativeDatabase.memory());
    dir = await Directory.systemTemp.createTemp('acct1810');
    Audit.testDirectory = dir;
    AccountSwitch.testDirectory = dir;
    store = _MemStore();
    api = _Server(store);
    AiAccessRepository.resetForTest();
    c = ProviderContainer(overrides: [databaseProvider.overrideWithValue(db), sessionStoreProvider.overrideWithValue(store), apiProvider.overrideWithValue(api)]);
  });
  tearDown(() async {
    c.dispose();
    await db.close();
    Audit.testDirectory = null;
    AccountSwitch.testDirectory = null;
    if (await dir.exists()) await dir.delete(recursive: true);
  });

  Future<void> file(String name, String text) async {
    final f = File('${dir.path}/$name');
    await f.parent.create(recursive: true);
    await f.writeAsString(text);
  }

  Future<void> loginAs(String email) => c.read(sessionProvider.notifier).login(email, 'pw');

  Future<void> sale(String id, String owner, {double total = 250, String status = 'pending'}) => db.enqueue(
    OutboxCompanion.insert(
      id: id,
      kind: 'sale',
      body: jsonEncode(OutboxMeta.attach({'items': [{'product': 'a1', 'qty': 2}]}, owner: owner)),
      createdAt: DateTime.now(),
      total: Value(total),
      status: Value(status),
    ),
  );

  /// Всё, что A оставил бы на телефоне: данные приложения, секреты, файлы.
  Future<void> seedAccountA() async {
    await db.upsertProduct(ProductsCompanion.insert(id: 'a1', name: 'Яблоко'));
    for (final e in {
      'ai.access': '{"users":[{"id":"ua","name":"Анна"}]}',
      'debt.rules': '[1]',
      'debtors.cache': '[{"id":"c-ua"}]',
      'held.carts': '[{"id":"h1"}]',
      'cart.draft': '{"owner":"a@shop.kg"}',
      'quotes': '[{"n":1}]',
      'shop.clothing': '1',
      'shift.id': '{"id":"7","owner":"a@shop.kg"}',
      'refs.index': '{"a1":["x"]}',
      'cam.aliases': '{"a1":["ябл"]}',
      'ui.lang': 'ky',
      'ui.dark': '1',
      'tester.code': 'CODE-A',
      'tester.marks': '{"x":1}',
      'printer.config': '{"port":"192.168.0.5","width":58}',
      'checkout.pendingKey': '{"key":"k1"}',
      'ops.pending.refund': '{"key":"k2"}',
    }.entries) {
      await db.setSetting(e.key, e.value);
    }
    await db.setSetting(TesterStore.deviceIdKey, 'AAAA1111');
    await file('ai_actions.jsonl', '{"tool":"x"}\n');
    await file('ai_last_error.json', '{}');
    await file('ai_perf.log', 'x\n');
    await file('refs/a1_0.jpg', 'jpg');
    await file('audit.jsonl', '{"u":"Анна"}\n');
    await file('audit.a@shop.kg.jsonl', '{"u":"Анна"}\n');
    const secure = FlutterSecureStorage();
    for (final k in AccountSwitch.secretKeys) {
      await secure.write(key: k, value: 'секрет-A');
    }
    await secure.write(key: 'something.else', value: 'tail');
  }

  /// Следов A нет: ни очереди, ни товаров, ни настроек (кроме метки миграции поиска), ни секретов, ни файлов.
  Future<void> expectNoTraceOfA({File? allowedFile}) async {
    expect(await db.select(db.outbox).get(), isEmpty, reason: 'очередь');
    expect(await db.productCount(), 0);
    final left = (await db.select(db.settings).get()).where((s) => s.key != 'search.keyV').toList();
    expect(left, isEmpty, reason: 'настройки: ${left.map((e) => e.key)}');
    expect(await const FlutterSecureStorage().readAll(), isEmpty, reason: 'защищённое хранилище');
    final files = [for (final e in dir.listSync(recursive: true)) if (e is File && e.path != allowedFile?.path) e.path];
    expect(files, isEmpty, reason: 'файлы: $files');
    expect(Audit.currentUser, '');
    expect(c.read(sessionProvider), isNull);
    expect(store.saved, isNull);
  }

  group('выход: очередь A блокирует, потом стирается всё', () {
    testWidgets('в очереди A есть продажа → окно блокирует выход; отправка пустит; «УДАЛИТЬ» нужно вводить; файл не создаётся', (t) async {
      t.view.physicalSize = const Size(1080, 2200);
      t.view.devicePixelRatio = 2.6;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
      });
      final l = lookupAppLocalizations(const Locale('ru'));
      late FakeSyncProbe probe;
      await t.runAsync(() async {
        await sale('s1', 'a@shop.kg', total: 250);
        await sale('s2', 'a@shop.kg', total: 100);
      });
      probe = FakeSyncProbe();
      Future<bool>? result;
      Widget app() => ProviderScope(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(api),
          sessionProvider.overrideWith(() => _FixedSession(Session(email: 'a@shop.kg', access: 'a', refresh: 'r'))),
          syncProvider.overrideWith((ref) => probe.make(db, api, () => online)),
        ],
        child: MaterialApp(
          theme: buildTheme(dark: false),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: Builder(
            builder: (ctx) => Consumer(
              builder: (ctx2, ref, _) => Scaffold(body: TextButton(onPressed: () => result = confirmQueueResolved(ctx, ref), child: const Text('выйти'))),
            ),
          ),
        ),
      );
      Future<void> settle() async {
        for (var i = 0; i < 12; i++) {
          await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
          await t.pump(const Duration(milliseconds: 50));
        }
      }

      await t.pumpWidget(app());
      await t.tap(find.text('выйти'));
      await settle();
      // окно: сумма и количество, кнопки; обычного «Выйти» нет
      expect(find.textContaining('2 на 350'), findsOneWidget);
      expect(find.text(l.logoutSendNow), findsOneWidget);
      expect(find.text(l.guardExport), findsOneWidget);
      expect(find.text(l.guardDiscard), findsOneWidget);
      var done = false;
      unawaited(result!.then((_) => done = true));
      expect(done, isFalse, reason: 'выход заблокирован');

      // нет связи: отправка не проходит, окно остаётся и просит дождаться
      online = false;
      await t.tap(find.text(l.logoutSendNow));
      await settle();
      expect(find.textContaining(l.guardSendLeft(2, 'Нет связи с сервером.')), findsOneWidget);
      expect(done, isFalse);

      // «Выйти БЕЗ отправки»: без слова кнопка не работает
      await t.tap(find.text(l.guardDiscard));
      await settle();
      expect(find.text(l.guardDiscardField), findsOneWidget);
      final confirm = find.widgetWithText(TextButton, l.guardDiscard).last;
      expect(t.widget<TextButton>(confirm).onPressed, isNull);
      await t.enterText(find.byType(TextField), 'УДА');
      await t.pump();
      expect(t.widget<TextButton>(confirm).onPressed, isNull);
      await t.tap(find.text(l.cancel).last); // отмена вложенного окна
      await settle();
      expect(done, isFalse);
      expect(dir.listSync(recursive: true), isEmpty, reason: 'файл выгрузки не создан');

      // связь появилась: «Отправить сейчас» отправляет всё и пускает
      online = true;
      await t.tap(find.text(l.logoutSendNow));
      await settle();
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      await t.pump();
      expect(await result, isTrue);
      expect(probe.runs, greaterThanOrEqualTo(2));
      expect((await t.runAsync(() => db.pending()))!, isEmpty);
    });

    testWidgets('«Выйти БЕЗ отправки» после ввода слова «УДАЛИТЬ» → выход разрешён, файл не создаётся', (t) async {
      t.view.physicalSize = const Size(1080, 2200);
      t.view.devicePixelRatio = 2.6;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
      });
      final l = lookupAppLocalizations(const Locale('ru'));
      await t.runAsync(() => sale('s1', 'a@shop.kg'));
      Future<bool>? result;
      await t.pumpWidget(
        ProviderScope(
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            sessionProvider.overrideWith(() => _FixedSession(Session(email: 'a@shop.kg', access: 'a', refresh: 'r'))),
          ],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            home: Builder(
              builder: (ctx) => Consumer(builder: (ctx2, ref, _) => Scaffold(body: TextButton(onPressed: () => result = confirmQueueResolved(ctx, ref), child: const Text('выйти')))),
            ),
          ),
        ),
      );
      await t.tap(find.text('выйти'));
      for (var i = 0; i < 10; i++) {
        await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
        await t.pump(const Duration(milliseconds: 50));
      }
      await t.tap(find.text(l.guardDiscard));
      await t.pump(const Duration(milliseconds: 300));
      await t.enterText(find.byType(TextField), 'удалить');
      await t.pump();
      final confirm = find.widgetWithText(TextButton, l.guardDiscard).last;
      expect(t.widget<TextButton>(confirm).onPressed, isNotNull);
      await t.tap(confirm);
      await t.pump(const Duration(milliseconds: 300));
      expect(await result, isTrue);
      expect(dir.listSync(recursive: true), isEmpty, reason: 'файл выгрузки не создан');
    });

    test('после отправки: выход стирает ВСЁ — нет ни одного следа A', () async {
      await loginAs('a@shop.kg');
      expect(c.read(sessionProvider)!.userName, 'Анна');
      await seedAccountA();
      await sale('s1', 'a@shop.kg');
      // очередь отправлена (подмена сервера), затем выход
      await db.markSent('s1', serverId: 'srv1', serverNumber: '17');
      await c.read(sessionProvider.notifier).logout();
      await expectNoTraceOfA();
      // устройство нового аккаунта — свой id
      expect(await TesterStore(db).deviceId(), isNot('AAAA1111'));
    });

    test('«выйти без отправки»: очередь с неотправленным стёрта вместе со всем, файл выгрузки не создан', () async {
      await loginAs('a@shop.kg');
      await seedAccountA();
      await sale('s1', 'a@shop.kg');
      await sale('s2', 'a@shop.kg', status: 'failed');
      expect((await AccountSwitch.queueSummary(db, owner: 'a@shop.kg')).count, 2);
      await c.read(sessionProvider.notifier).logout();
      await expectNoTraceOfA();
      expect(Directory('${dir.path}/NurMarket').existsSync(), isFalse);
    });

    test('«выгрузить в файл и выйти»: файл JSON с чеками создан и остаётся единственным, всё остальное стёрто', () async {
      await loginAs('a@shop.kg');
      await seedAccountA();
      await sale('s1', 'a@shop.kg', total: 250);
      await sale('s2', 'a@shop.kg', total: 100);
      await db.markSent('s2');
      final f = await AccountSwitch.exportQueue(db, owner: 'a@shop.kg', now: DateTime(2026, 10, 29, 22, 5));
      expect(f.path.replaceAll('\\', '/'), endsWith('NurMarket/unsent-a@shop.kg-20261029-2205.json'));
      final json = jsonDecode(await f.readAsString()) as Map<String, dynamic>;
      expect(json['owner'], 'a@shop.kg');
      expect((json['rows'] as List).length, 1, reason: 'только неотправленное');
      expect(jsonEncode(json['rows']), contains('s1'));
      await c.read(sessionProvider.notifier).logout();
      await expectNoTraceOfA(allowedFile: f);
      expect(f.existsSync(), isTrue);
    });
  });

  group('вход другого аккаунта без выхода (истёк вход)', () {
    test('чужая очередь видна для окна «выгрузить или удалить»; вход другим логином стирает её и всё остальное', () async {
      await loginAs('a@shop.kg');
      await seedAccountA();
      await sale('s1', 'a@shop.kg', total: 400);
      await c.read(sessionProvider.notifier).expire(); // вход истёк, выхода не было
      final foreign = await AccountSwitch.foreignQueue(db, 'b@shop.kg');
      expect(foreign.count, 1);
      expect(foreign.sum, 400);
      expect(foreign.owners, {'a@shop.kg'});
      expect((await AccountSwitch.foreignQueue(db, 'a@shop.kg')).isEmpty, isTrue, reason: 'тем же логином — окна нет');
      await loginAs('b@shop.kg');
      final s = c.read(sessionProvider)!;
      expect(s.userName, 'Борис');
      expect(s.companyInn, '1ub');
      expect(await db.select(db.outbox).get(), isEmpty, reason: 'очередь A стёрта');
      for (final k in ['ai.access', 'debt.rules', 'held.carts', 'quotes', 'shop.clothing', 'shift.id', 'refs.index', 'cam.aliases', 'tester.code', 'printer.config', 'checkout.pendingKey']) {
        final v = await db.getSetting(k);
        expect(v == null || v.isEmpty || (!v.contains('ua') && !v.contains('a@shop')), isTrue, reason: '$k от A не остался: $v');
      }
      expect(await db.getSetting('ai.access'), contains('ub'));
      expect(await db.getSetting('ui.lang'), 'en', reason: 'язык нового аккаунта — с сервера');
      expect(File('${dir.path}/ai_actions.jsonl').existsSync(), isFalse);
      expect(File('${dir.path}/audit.a@shop.kg.jsonl').existsSync(), isFalse);
      expect(await const FlutterSecureStorage().read(key: 'ai.gemini'), isNull);
      final policy = await c.read(aiAccessRepoProvider).load(force: true);
      expect(policy.contains('ub'), isTrue);
      expect(policy.contains('ua'), isFalse, reason: 'правило ИИ — B, не A');
    });

    test('тот же аккаунт заново: ничего не стирается, очередь на месте', () async {
      await db.setSetting(AccountSwitch.lastOwnerKey, 'a@shop.kg');
      await db.setSetting('ai.access', 'x');
      await sale('s1', 'a@shop.kg');
      expect(await AccountSwitch.prepare(db, 'a@shop.kg', directory: dir, deleteSecret: (k) async {}), isFalse);
      expect(await db.getSetting('ai.access'), 'x');
      expect((await db.pending()).length, 1);
    });

    test('без связи у нового аккаунта: правила ИИ нет — «ещё не загружено», а не «ИИ открыт всем»', () async {
      await loginAs('b@shop.kg');
      await db.delete(db.settings).go();
      api.offline = true;
      AiAccessRepository.resetForTest();
      final none = await c.read(aiAccessRepoProvider).load(force: true);
      expect(none.unloaded, isTrue);
    });
  });
}

/// Подмена очереди: «отправка» помечает записи отправленными, пока [online]; иначе остаётся «нет связи».
class FakeSyncProbe {
  int runs = 0;
  SyncService make(AppDatabase db, ApiClient api, bool Function() online) {
    return _ProbeSync(this, db, api, online);
  }
}

class _ProbeSync extends SyncService {
  _ProbeSync(this.probe, AppDatabase db, ApiClient api, this.online)
    : super(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@shop.kg');
  final FakeSyncProbe probe;
  final bool Function() online;
  String? _err;
  @override
  String? get lastError => _err;
  @override
  Future<void> runQueue() async {
    probe.runs++;
    if (!online()) {
      _err = 'Нет связи с сервером.';
      return;
    }
    _err = null;
    for (final r in await db.pending()) {
      await db.markSent(r.id, serverId: 'srv-${r.id}', serverNumber: '1');
    }
  }
}

class _FixedSession extends SessionController {
  _FixedSession(this.s);
  final Session s;
  @override
  Session? build() => s;
}
