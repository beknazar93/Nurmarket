// 2026-10-30 (оптимизация мобилки по отчёту «Оптимизация мобилки (30.10.2026)»): проверки быстрых исправлений №1–10 и структурных
// №12–15 и замеры «до/после» на базе из 15 000 товаров (ПК; на телефоне в 3–8 раз медленнее). Сервер не трогается.
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:drift/drift.dart' hide isNull;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image/image.dart' as img;

import 'package:nurmarket/core/ai/ai_image.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/sync/outbox_meta.dart';
import 'package:nurmarket/core/sync/sync_service.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/photo_cache.dart';
import 'package:nurmarket/core/util/product_picker.dart';
import 'package:nurmarket/features/checks/checks_screen.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _CountDb extends AppDatabase {
  _CountDb(super.e);
  int watches = 0;
  @override
  Stream<List<OutboxData>> watchOutbox({String? kind, bool hideSent = false, bool hideDropped = false, int? limit}) {
    watches++;
    return super.watchOutbox(kind: kind, hideSent: hideSent, hideDropped: hideDropped, limit: limit);
  }
}

class _Offline extends ApiClient {
  _Offline() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => throw ApiException('Нет связи с сервером.');
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async => throw ApiException('Нет связи с сервером.');
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _me = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');

Future<void> _fillProducts(AppDatabase db, int n) async {
  const names = ['Кока-Кола', 'Фанта', 'Спрайт', 'Макфа вермишель', 'Молоко', 'Хлеб', 'Сахар', 'Масло подсолнечное', 'Чай чёрный', 'Печенье'];
  await db.batch((b) {
    b.insertAll(db.products, [
      for (var i = 0; i < n; i++)
        ProductsCompanion.insert(
          id: 'p$i',
          name: '${names[i % names.length]} $i',
          price: Value(50.0 + i % 300),
          quantity: Value((i % 40).toDouble()),
          category: Value('Кат ${i % 25}'),
          barcode: Value('4600${(100000 + i)}'),
          searchKey: Value('${names[i % names.length]} $i 4600${100000 + i} кат ${i % 25}'.toLowerCase()),
        ),
    ]);
  });
}

Future<int> _ms(Future<Object?> Function() f, {int times = 5}) async {
  await f(); // прогрев
  final sw = Stopwatch()..start();
  for (var i = 0; i < times; i++) {
    await f();
  }
  return sw.elapsedMilliseconds ~/ times;
}

void main() {
  group('замеры: 15 000 товаров', () {
    late AppDatabase db;
    setUp(() async {
      db = AppDatabase(NativeDatabase.memory());
      await _fillProducts(db, 15000);
    });
    tearDown(() => db.close());

    test('п. 13: окно из 401 товара вместо всех 15 000 на каждую букву', () async {
      final oldAll = await _ms(() => db.searchProducts('', limit: 20000));
      final newWin = await _ms(() => db.searchProducts('', limit: 401));
      final oldQ = await _ms(() => db.searchProducts('кола', limit: 20000));
      final newQ = await _ms(() => db.searchProducts('кола', limit: 401));
      // ignore: avoid_print
      print('ЗАМЕР п.13  пустой запрос: было 20000 строк = $oldAll мс, стало окно 401 = $newWin мс; «кола»: было $oldQ мс, стало $newQ мс');
      expect(newWin, lessThan(oldAll));
      final all = await db.searchProducts('', limit: 20000);
      final win = await db.searchProducts('', limit: 401);
      expect(all.length, 15000);
      expect(win.length, 401);
      expect(win.map((p) => p.id).toList(), all.take(401).map((p) => p.id).toList(), reason: 'окно — начало того же упорядоченного списка');
      // растущее окно не теряет и не дублирует товары
      final w2 = await db.searchProducts('', limit: 801);
      expect(w2.take(401).map((p) => p.id).toList(), win.map((p) => p.id).toList());
      expect(w2.map((p) => p.id).toSet().length, 801);
    });

    test('п. 13: фильтр остатка в SQL даёт то же, что фильтр в Dart', () async {
      final all = await db.searchProducts('', limit: 20000);
      final low = await db.searchProducts('', limit: 20000, extra: (p) => p.isService.equals(false) & p.quantity.isBiggerThanValue(0) & lowStockSql(p));
      expect(low.map((p) => p.id).toSet(), all.where((p) => !p.isService && p.quantity > 0 && isLowStock(p.quantity, p.minQuantity)).map((p) => p.id).toSet());
      final out = await db.searchProducts('', limit: 20000, extra: (p) => p.quantity.isSmallerOrEqualValue(0));
      expect(out.length, all.where((p) => p.quantity <= 0).length);
    });

    test('п. 4: «заканчиваются» — COUNT(*) вместо чтения всех строк', () async {
      final old = await _ms(() async => (await (db.select(db.products)..where((p) => lowStockSql(p) & p.isService.equals(false))).get()).length);
      final cnt = await _ms(() => countLowStock(db));
      // ignore: avoid_print
      print('ЗАМЕР п.4   «заканчиваются»: было чтение строк $old мс, стало COUNT $cnt мс');
      final rows = await (db.select(db.products)..where((p) => lowStockSql(p) & p.isService.equals(false))).get();
      expect(await countLowStock(db), rows.length);
      expect(rows.length, greaterThan(1000));
    });

    test('п. 10: минимум и закупка — только нужные поля, не вся таблица объектов', () async {
      final old = await _ms(() async => {for (final p in await db.select(db.products).get()) p.id: p.minQuantity});
      final nw = await _ms(() => db.keptFieldsById());
      // ignore: avoid_print
      print('ЗАМЕР п.10  чтение полей при полной загрузке (раз): было объекты Product $old мс, стало 3 колонки $nw мс (в коде было 2 раза)');
      final m = await db.keptFieldsById();
      expect(m.length, 15000);
      await db.upsertProduct(ProductsCompanion.insert(id: 'p1', name: 'X', minQuantity: const Value(7), purchasePrice: const Value(33.5)));
      final m2 = await db.keptFieldsById();
      expect(m2['p1']!.minQuantity, 7);
      expect(m2['p1']!.purchasePrice, 33.5);
      expect(m2['p2']!.purchasePrice, isNull);
    });
  });

  group('очередь outbox', () {
    late AppDatabase db;
    final body = jsonEncode({'items': List.generate(8, (i) => {'product': 'p$i', 'qty': 1, 'price': 100}), '_meta': {'owner': 'o@x.kg', 'at': 'x'}, 'pad': 'я' * 1500});
    setUp(() async {
      db = AppDatabase(NativeDatabase.memory());
      final now = DateTime.now();
      await db.batch((b) {
        b.insertAll(db.outbox, [
          for (var i = 0; i < 3000; i++)
            OutboxCompanion.insert(
              id: 'o$i',
              kind: 'sale',
              body: body,
              createdAt: now.subtract(Duration(days: i ~/ 50)), // 60 дней
              status: Value(i < 3 ? 'pending' : 'sent'),
              summary: const Value('Mars, Кока-Кола…'),
            ),
        ]);
      });
    });
    tearDown(() => db.close());

    test('п. 1: счётчик неотправленных читает только ждущие записи', () async {
      final oldMs = await _ms(() async => (await db.watchOutbox().first).where((r) => r.status == 'pending').length, times: 3);
      final newMs = await _ms(() async => (await db.watchPendingRows().first).length, times: 3);
      // ignore: avoid_print
      print('ЗАМЕР п.1   счётчик при 3000 записях в очереди: было вся таблица $oldMs мс, стало только ждущие $newMs мс на каждую запись очереди');
      expect((await db.watchPendingRows().first).length, 3);
      expect(await db.hasPending(), isTrue);
    });

    test('п. 2: список очереди для экрана — без отправленных и не больше limit (в SQL)', () async {
      final rows = await db.watchOutbox(kind: 'sale', hideSent: true, hideDropped: true, limit: 200).first;
      expect(rows.length, 3);
      expect(rows.every((r) => r.status != 'sent'), isTrue);
      final sync = await db.watchOutbox(hideDropped: true, limit: 150).first;
      expect(sync.length, 150);
      final t = sync.first.createdAt;
      expect(sync.every((r) => !r.createdAt.isAfter(t)), isTrue, reason: 'новые сверху');
    });

    test('чистка: отправленные старше 30 дней и сверх 500 удаляются, ждущие и «не принято» — никогда', () async {
      await db.markAttempt('o0', 'ошибка', failed: true); // «не принято» — старая, но должна остаться
      await (db.update(db.outbox)..where((o) => o.id.equals('o1'))).write(const OutboxCompanion(status: Value('dropped')));
      final removed = await db.purgeOutbox(keepDays: 30, keepRows: 500);
      final left = await db.select(db.outbox).get();
      // ignore: avoid_print
      print('ЗАМЕР чистка очереди: было 3000 записей, удалено $removed, осталось ${left.length}');
      expect(left.where((r) => r.status == 'pending').length, 1, reason: 'o0 стал «не принято», o1 снят — ждёт только o2');
      expect(left.any((r) => r.id == 'o0' && r.status == 'failed'), isTrue);
      expect(left.where((r) => r.status == 'sent' || r.status == 'dropped').length, lessThanOrEqualTo(500));
      expect(left.length, lessThan(600));
      // всё оставшееся отправленное — самое свежее
      final sentIds = left.where((r) => r.status == 'sent').map((r) => r.createdAt).toList();
      expect(sentIds.every((d) => DateTime.now().difference(d).inDays <= 30), isTrue);
    });

    test('п. 3: пустая очередь — тик не берёт замок и не пишет в базу', () async {
      await db.delete(db.outbox).go();
      final api = _Offline()..attach(_me);
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'o@x.kg');
      var writes = 0;
      final sub = db.tableUpdates(TableUpdateQuery.onTable(db.settings)).listen((_) => writes++);
      for (var i = 0; i < 5; i++) {
        await sync.runQueue();
      }
      await Future<void>.delayed(const Duration(milliseconds: 50));
      await sub.cancel();
      expect(await db.getSetting(AppDatabase.queueLockKey), isNull, reason: 'замок не создавался');
      expect(writes, 0, reason: 'раньше — 2 записи в settings на каждый тик (замок взять/отпустить)');
    });

    test('п. 3: очередь только чужого аккаунта — тоже без замка', () async {
      await db.delete(db.outbox).go();
      await db.enqueue(OutboxCompanion.insert(id: 'x', kind: 'sale', body: jsonEncode({'_meta': {'owner': 'other@x.kg'}}), createdAt: DateTime.now()));
      final api = _Offline()..attach(_me);
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'o@x.kg');
      await sync.runQueue();
      expect(await db.getSetting(AppDatabase.queueLockKey), isNull);
      expect(await sync.hasOwnWork(), isFalse);
    });

    test('п. 14: SyncService сообщает, есть ли неотправленное (по этому ставится/снимается задача WorkManager)', () async {
      await db.delete(db.outbox).go();
      final api = _Offline()..attach(_me);
      final states = <bool>[];
      final sync = SyncService(
        db: db,
        sales: SalesRepository(api, db),
        stock: StockRepository(api, db),
        catalog: CatalogRepository(api, db),
        owner: () => 'o@x.kg',
        onUnsentChanged: states.add,
      );
      await sync.runQueue();
      expect(states, [false]);
      await db.enqueue(OutboxCompanion.insert(id: 'mine', kind: 'sale', body: jsonEncode({OutboxMeta.key: {'owner': 'o@x.kg'}}), createdAt: DateTime.now()));
      await sync.planBackground();
      expect(states.last, isTrue);
    });
  });

  group('п. 2: экран «Чеки» подписывается на очередь один раз', () {
    testWidgets('поиск по буквам не пересоздаёт поток очереди', (t) async {
      t.view.physicalSize = const Size(720, 1280);
      t.view.devicePixelRatio = 2.0;
      addTearDown(t.view.resetPhysicalSize);
      addTearDown(t.view.resetDevicePixelRatio);
      final prev = FlutterError.onError;
      FlutterError.onError = (d) {
        if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
        prev?.call(d);
      };
      addTearDown(() => FlutterError.onError = prev);
      final db = _CountDb(NativeDatabase.memory());
      addTearDown(db.close);
      await t.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(_Offline()), sessionProvider.overrideWith(() => _Fixed(_me))],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            home: const Scaffold(body: ChecksScreen()),
          ),
        ),
      );
      await t.pump(const Duration(milliseconds: 200));
      expect(db.watches, 1);
      final field = find.byType(TextField);
      expect(field, findsOneWidget);
      for (final s in ['м', 'ма', 'мар', 'марс', 'марс 1']) {
        await t.enterText(field, s);
        await t.pump();
      }
      // ignore: avoid_print
      print('ЗАМЕР п.2   подписок на очередь за 5 букв поиска в «Чеках»: было 6 (по одной на каждую перерисовку), стало ${db.watches}');
      expect(db.watches, 1, reason: 'раньше stream создавался в build() — на каждую букву заново');
      await t.pumpWidget(const SizedBox());
      await t.pump(const Duration(milliseconds: 50));
    });
  });

  group('п. 13: склад берёт товары окном', () {
    testWidgets('первый экран — 400 товаров, прокрутка подгружает следующие, поиск и фильтр остатка работают в SQL', (t) async {
      t.view.physicalSize = const Size(720, 1280);
      t.view.devicePixelRatio = 2.0;
      addTearDown(t.view.resetPhysicalSize);
      addTearDown(t.view.resetDevicePixelRatio);
      final prev = FlutterError.onError;
      FlutterError.onError = (d) {
        if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
        prev?.call(d);
      };
      addTearDown(() => FlutterError.onError = prev);
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);
      await db.batch((b) {
        b.insertAll(db.products, [
          for (var i = 0; i < 1000; i++)
            ProductsCompanion.insert(id: 'p$i', name: 'Товар ${i.toString().padLeft(4, '0')}', quantity: Value(i % 2 == 0 ? 50.0 : 3.0), searchKey: Value('товар ${i.toString().padLeft(4, '0')}')),
        ]);
      });
      await t.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(_Offline()), sessionProvider.overrideWith(() => _Fixed(_me))],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            home: const Scaffold(body: StockScreen()),
          ),
        ),
      );
      await t.pumpAndSettle();
      // ListView.separated: детей = 2 × строк − 1
      final list = find.byWidgetPredicate((w) => w is ListView && w.childrenDelegate is SliverChildBuilderDelegate);
      int items() => (((t.widget(list.last) as ListView).childrenDelegate as SliverChildBuilderDelegate).estimatedChildCount! + 1) ~/ 2;
      expect(items(), 401, reason: '400 товаров окна + строка подсказки; раньше читались все 1000 сразу');
      for (var i = 0; i < 20 && items() == 401; i++) {
        await t.drag(list.last, const Offset(0, -2500));
        await t.pumpAndSettle();
      }
      // ignore: avoid_print
      print('ЗАМЕР п.13  склад, 1000 товаров: первый экран ${401 - 1}, после прокрутки в тестовом окне ${items() - 1}');
      expect(items(), greaterThan(401), reason: 'дошли до конца окна — подгрузилась следующая страница');
      // поиск — в SQL: ровно один товар
      await t.enterText(find.byType(TextField).first, 'товар 0777');
      await t.pump(const Duration(milliseconds: 400));
      await t.pumpAndSettle();
      expect(items(), 2);
      await t.pumpWidget(const SizedBox());
      await t.pump(const Duration(milliseconds: 50));
    });
  });

  group('п. 8: журнал аудита', () {
    late Directory dir;
    setUp(() {
      dir = Directory.systemTemp.createTempSync('audit1030');
      Audit.testDirectory = dir;
      Audit.resetForTests();
    });
    tearDown(() {
      Audit.testDirectory = null;
      dir.deleteSync(recursive: true);
    });

    test('записи не теряются без fsync на каждую пачку; syncToDisk не падает', () async {
      for (var i = 0; i < 300; i++) {
        Audit.log('cart', 'строка $i');
      }
      await Audit.log('cart', 'последняя');
      await Audit.syncToDisk();
      final lines = File('${dir.path}/audit.jsonl').readAsLinesSync();
      expect(lines.length, 301);
      expect(lines.last, contains('последняя'));
    });

    test('обрезка при записи: файл > 5000 строк ужимается до 5000 без открытия журнала', () async {
      final f = File('${dir.path}/audit.jsonl');
      final line = jsonEncode({'at': DateTime.now().toIso8601String(), 'user': 'u', 'kind': 'cart', 'text': 'x'});
      f.writeAsStringSync('${List.filled(6200, line).join('\n')}\n');
      await Audit.log('cart', 'новая');
      final n = f.readAsLinesSync().length;
      expect(n, lessThanOrEqualTo(5001));
      expect(f.readAsLinesSync().last, contains('новая'));
    });
  });

  group('п. 5: снимок для ИИ', () {
    test('большой JPEG уменьшается до 1280 px и становится заметно меньше; маленький не трогается', () async {
      final big = img.Image(width: 3000, height: 2250);
      for (var y = 0; y < big.height; y += 1) {
        for (var x = 0; x < big.width; x += 1) {
          big.setPixelRgb(x, y, (x * 7 + y) % 256, (x + y * 3) % 256, (x * y) % 256);
        }
      }
      final jpg = Uint8List.fromList(img.encodeJpg(big, quality: 92));
      final sw = Stopwatch()..start();
      final small = await shrinkForAi(jpg);
      final ms = sw.elapsedMilliseconds;
      final d = img.decodeJpg(small)!;
      // ignore: avoid_print
      print('ЗАМЕР п.5   снимок 3000×2250: было ${jpg.length ~/ 1024} КБ, стало ${small.length ~/ 1024} КБ (${d.width}×${d.height}), уменьшение за $ms мс (в изоляте)');
      expect(d.width, 1280);
      expect(small.length, lessThan(jpg.length ~/ 3));
      expect(jpegSize(jpg), (3000, 2250));
      final tiny = Uint8List.fromList(img.encodeJpg(img.Image(width: 200, height: 100), quality: 80));
      expect(identical(await shrinkForAi(tiny), tiny), isTrue);
      // снимок камеры в пределах 1280 px: заголовок прочитан, картинка не разбирается вовсе
      final hd = Uint8List.fromList(img.encodeJpg(img.Image(width: 1280, height: 720), quality: 80));
      final sw2 = Stopwatch()..start();
      expect(identical(await shrinkForAi(hd), hd), isTrue);
      expect(sw2.elapsedMilliseconds, lessThan(50));
      final notImage = Uint8List.fromList(List.filled(300 * 1024, 7));
      expect(identical(await shrinkForAi(notImage), notImage), isTrue, reason: 'не картинка — как есть, без падения');
    });
  });

  group('п. 12: дисковый кэш фото', () {
    test('trim: лишнее сверх лимита удаляется, старые первыми', () async {
      final dir = Directory.systemTemp.createTempSync('photos1030');
      addTearDown(() => dir.deleteSync(recursive: true));
      final base = DateTime.now().subtract(const Duration(days: 10));
      for (var i = 0; i < 10; i++) {
        final f = File('${dir.path}/f$i.jpg')..writeAsBytesSync(List.filled(1000, i));
        f.setLastModifiedSync(base.add(Duration(hours: i)));
      }
      final removed = await PhotoCache.trim(force: true, dir: dir, limit: 5000);
      final left = dir.listSync().map((e) => e.uri.pathSegments.last).toSet();
      expect(removed, 6, reason: 'до 80 % лимита: 4000 из 10 000 байт');
      expect(left, {'f6.jpg', 'f7.jpg', 'f8.jpg', 'f9.jpg'});
    });
  });
}
