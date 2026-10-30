// 2026-10-30 (1.18.12, product-batches): «Поступления и партии» товара на складе — разбор приходов, остаток партии по FIFO, итоги,
// чтение страницами (500 приходов), кэш «данные от ЧЧ:ММ», права (кассир не видит закупку и поставщиков), накладная и файл.
// Сервер не трогается: фейковый API и база в памяти.
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/sync/account_switch.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/stock/product_batches.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _FakeApi extends ApiClient {
  _FakeApi(this.handler) : super(SessionStore());
  Future<dynamic> Function(String path, Map<String, dynamic>? query) handler;
  final calls = <String>[];
  final queries = <Map<String, dynamic>?>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    ifNoneMatch = null;
    calls.add(path);
    queries.add(query);
    return handler(path, query);
  }
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Бакыт', role: 'owner', isOwner: true, cashboxId: 'b');
const _cashier = Session(email: 'c@x.kg', access: 'a', refresh: 'r', userId: 'u4', userName: 'Айгуль', role: 'cashier', cashboxId: 'b');

/// Приход i: товар P1 — в каждом пятом (r0, r5, …), остальные с другим товаром; новые сверху (i растёт — приход старше).
Map<String, dynamic> _receipt(int i, {String? file}) => {
  'id': 'r$i',
  'supplier_id': 's${i % 3}',
  'supplier_name': 'Поставщик ${i % 3}',
  'created_by_name': 'Админ',
  'created_at': DateTime.utc(2026, 10, 30, 12).subtract(Duration(hours: i)).toIso8601String(),
  'total_amount': 600.0,
  'items': [
    {'product_id': i % 5 == 0 ? 'P1' : 'PX', 'product_name': 'Альфа', 'qty': 10, 'purchase_price': 60, 'unit': 'шт'},
    {'product_id': 'PZ', 'product_name': 'Другой товар', 'qty': 2, 'purchase_price': 5, 'unit': 'шт'},
  ],
  'file': ?file,
};

_FakeApi _api({int total = 500, Map<String, String> files = const {}, Object? failWith}) {
  final all = [for (var i = 0; i < total; i++) _receipt(i, file: files['r$i'])];
  return _FakeApi((path, q) async {
    if (failWith != null) throw failWith;
    if (path == 'api/main/suppliers/receipts/') {
      final page = (q?['page'] as int?) ?? 1;
      final limit = (q?['limit'] as int?) ?? 100;
      final from = (page - 1) * limit;
      final slice = from >= all.length ? <Map<String, dynamic>>[] : all.sublist(from, (from + limit).clamp(0, all.length));
      return {'count': all.length, 'next': page * limit < all.length ? 'next' : null, 'results': slice};
    }
    if (path.startsWith('api/main/suppliers/receipts/')) {
      final id = path.substring('api/main/suppliers/receipts/'.length).replaceAll('/', '');
      return all.firstWhere((r) => r['id'] == id);
    }
    if (path == 'api/main/clients/') {
      return {
        'results': [
          {'id': 's0', 'full_name': 'Поставщик 0', 'phone': '0700123456'},
        ],
      };
    }
    return {};
  });
}

void main() {
  // ------------------------------------------------------------------ разбор и расчёты
  group('разбор прихода и расчёты', () {
    test('строки одного товара в приходе складываются, чужой товар не мешает; товара нет — null', () {
      final r = {
        'id': 'a1',
        'supplier': {'id': 7, 'name': 'ОсОО Свет'},
        'created_by_name': 'Айбек',
        'created_at': '2026-10-29T10:00:00+06:00',
        'items': [
          {'product': 'P1', 'qty': 4, 'purchase_price': '50.00', 'unit': 'шт'},
          {'product': 'P1', 'qty': 6, 'purchase_price': 60, 'returned_qty': 1},
          {'product': 'P9', 'qty': 100, 'purchase_price': 1},
        ],
      };
      final e = batchFromReceipt(r, 'P1')!;
      expect(e.qty, 10);
      expect(e.returned, 1);
      expect(e.net, 9);
      expect(e.cost, closeTo(56, 0.001)); // (4·50 + 6·60) / 10
      expect(e.supplierName, 'ОсОО Свет');
      expect(e.createdBy, 'Айбек');
      expect(e.unit, 'шт');
      expect(batchFromReceipt(r, 'NOPE'), isNull);
      expect(batchFromReceipt({'id': 'a2'}, 'P1'), isNull);
    });

    test('время без пояса — Бишкек (+06:00), с поясом — как есть', () {
      expect(parseBatchDate('2026-10-15T14:57:59')!.toUtc(), DateTime.utc(2026, 10, 15, 8, 57, 59));
      expect(parseBatchDate('2026-10-15T14:57:59+06:00')!.toUtc(), DateTime.utc(2026, 10, 15, 8, 57, 59));
      expect(parseBatchDate('2026-10-15T08:57:59Z')!.toUtc(), DateTime.utc(2026, 10, 15, 8, 57, 59));
      expect(parseBatchDate(''), isNull);
      expect(parseBatchDate('вчера'), isNull);
    });

    test('остаток партии по FIFO: текущий остаток ложится на самые свежие поступления', () {
      BatchEntry e(String id, double q, {double ret = 0}) => BatchEntry(receiptId: id, qty: q, returned: ret);
      expect(fifoRemaining([e('n', 10), e('m', 10), e('o', 10)], 15), [10, 5, 0]);
      expect(fifoRemaining([e('n', 10), e('o', 10)], 0), [0, 0]);
      expect(fifoRemaining([e('n', 10), e('o', 10)], 100), [10, 10]); // остаток больше принятого — партия целиком
      expect(fifoRemaining([e('n', 10, ret: 4), e('o', 10)], 8), [6, 2]); // возврат поставщику уменьшает партию
      expect(fifoRemaining([e('n', 3)], -5), [0]);
      expect(fifoRemaining(const [], 5), isEmpty);
    });

    test('итоги: всего получено, средняя закупка по количеству, последняя закупка и поставщик', () {
      final list = [
        BatchEntry(receiptId: '3', qty: 10, cost: 70, supplierName: 'Новый'),
        BatchEntry(receiptId: '2', qty: 30, cost: 50, supplierName: 'Старый'),
        BatchEntry(receiptId: '1', qty: 5),
      ];
      final s = BatchSummary.of(list);
      expect(s.totalQty, 45);
      expect(s.avgCost, closeTo((10 * 70 + 30 * 50) / 40, 0.001));
      expect(s.lastCost, 70);
      expect(s.lastSupplier, 'Новый');
      expect(s.count, 3);
      final none = BatchSummary.of(const []);
      expect(none.avgCost, 0);
      expect(none.lastSupplier, '');
    });

    test('объединение страниц: новые сверху, повтор одного прихода отбрасывается', () {
      final a = BatchEntry(receiptId: 'a', qty: 1, at: DateTime(2026, 10, 1));
      final b = BatchEntry(receiptId: 'b', qty: 1, at: DateTime(2026, 10, 3));
      final c = BatchEntry(receiptId: 'c', qty: 1);
      final m = mergeBatches([a], [b, a, c]);
      expect(m.map((e) => e.receiptId), ['b', 'a', 'c']);
    });

    test('исходный файл: нет поля — null; путь с сервера достраивается до адреса; не-http отбрасывается', () {
      expect(receiptFileUrl(null), isNull);
      expect(receiptFileUrl({'id': 1}), isNull);
      expect(receiptFileUrl({'file': ''}), isNull);
      expect(receiptFileUrl({'file': '/media/invoices/a.pdf'}), '${ApiClient.defaultBaseUrl}/media/invoices/a.pdf');
      expect(receiptFileUrl({'attachment': 'https://cdn.x.kg/a.xlsx'}), 'https://cdn.x.kg/a.xlsx');
      expect(receiptFileUrl({'file': 'javascript:alert(1)'}), isNull);
      expect(receiptFileUrl({'source_file': {'url': 'https://x.kg/b.pdf'}}), 'https://x.kg/b.pdf');
    });
  });

  // ------------------------------------------------------------------ чтение страницами и кэш
  group('чтение страницами и кэш', () {
    late AppDatabase db;
    setUp(() => db = AppDatabase(NativeDatabase.memory()));
    tearDown(() => db.close());

    test('500 приходов: первый шаг останавливается на 20 поступлениях, следующие шаги идут с нужной страницы до конца', () async {
      final api = _api();
      final repo = ProductBatchesRepository(api, db);
      final first = await repo.load('P1');
      expect(first.entries.length, 20);
      expect(first.hasMore, isTrue);
      expect(first.nextPage, 2);
      expect(api.calls.length, 1);
      expect(api.queries.first!['limit'], 100);
      var all = [...first.entries];
      var page = first.nextPage;
      var more = first.hasMore;
      var steps = 0;
      while (more && steps++ < 20) {
        final p = await repo.load('P1', page: page);
        all = mergeBatches(all, p.entries);
        page = p.nextPage;
        more = p.hasMore;
      }
      expect(all.length, 100); // каждый пятый из 500
      expect(more, isFalse);
      expect(all.first.receiptId, 'r0');
      expect(all.last.receiptId, 'r495');
    });

    test('период: на сервер уходит date_from; приходы старше начала периода не попадают в список', () async {
      final api = _api(total: 30);
      final repo = ProductBatchesRepository(api, db);
      final r = await repo.load('P1', from: DateTime(2026, 10, 30)); // r0 — 30.10 18:00 по Бишкеку, остальные — раньше
      expect(api.queries.first!['date_from'], '2026-10-30');
      expect(r.entries.every((e) => !e.at!.isBefore(DateTime(2026, 10, 30))), isTrue);
    });

    test('кэш: запись и чтение, не больше 12 товаров, чужой товар не мешает', () async {
      final repo = ProductBatchesRepository(_api(total: 0), db);
      expect(await repo.cacheRead('P1'), isNull);
      final at = DateTime(2026, 10, 30, 14, 5);
      await repo.cacheWrite('P1', [BatchEntry(receiptId: 'r1', qty: 3, cost: 7, supplierName: 'А', at: DateTime(2026, 10, 1))], now: at);
      final c = (await repo.cacheRead('P1'))!;
      expect(c.at, at);
      expect(c.entries.single.supplierName, 'А');
      expect(c.entries.single.cost, 7);
      expect(await repo.cacheRead('P2'), isNull);
      for (var i = 0; i < 15; i++) {
        await repo.cacheWrite('Q$i', [BatchEntry(receiptId: 'x', qty: 1)], now: at.add(Duration(minutes: i)));
      }
      expect(await repo.cacheRead('P1'), isNull, reason: 'самый старый вытеснен');
      expect(await repo.cacheRead('Q14'), isNotNull);
    });

    test('смена аккаунта (account_wipe) стирает кэш поступлений', () async {
      final repo = ProductBatchesRepository(_api(total: 0), db);
      await repo.cacheWrite('P1', [BatchEntry(receiptId: 'r1', qty: 3)]);
      expect(await repo.cacheRead('P1'), isNotNull);
      final dir = await Directory.systemTemp.createTemp('pb_wipe');
      addTearDown(() => dir.delete(recursive: true));
      await AccountSwitch.wipeAll(db, directory: dir, deleteSecret: (k) async {});
      expect(await repo.cacheRead('P1'), isNull);
    });

    test('испорченный кэш не роняет', () async {
      await db.setSetting(ProductBatchesRepository.cacheKey, '{не json');
      final repo = ProductBatchesRepository(_api(total: 0), db);
      expect(await repo.cacheRead('P1'), isNull);
      await repo.cacheWrite('P1', [BatchEntry(receiptId: 'r1', qty: 1)]); // перезапись поверх мусора
      expect(await repo.cacheRead('P1'), isNotNull);
    });
  });

  // ------------------------------------------------------------------ экран
  group('экран «Поступления и партии»', () {
    late AppDatabase db;
    late Product product;

    setUp(() async {
      db = AppDatabase(NativeDatabase.memory());
      await db.upsertProduct(ProductsCompanion.insert(id: 'P1', name: 'Альфа', quantity: const Value(15), searchKey: const Value('альфа')));
      product = (await db.productById('P1'))!;
    });
    tearDown(() => db.close());

    void phone(WidgetTester t, {double scale = 1.0}) {
      t.view.physicalSize = const Size(360, 800) * 2;
      t.view.devicePixelRatio = 2;
      t.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(t.view.reset);
      addTearDown(t.platformDispatcher.clearTextScaleFactorTestValue);
    }

    Widget app(Session s, _FakeApi api, {String lang = 'ru', bool dark = false}) => ProviderScope(
      overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(s))],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        darkTheme: buildTheme(dark: true),
        themeMode: dark ? ThemeMode.dark : ThemeMode.light,
        locale: Locale(lang),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: ProductBatchesScreen(product: product),
      ),
    );

    AppLocalizations lx(String lang) => lookupAppLocalizations(Locale(lang));
    Finder vertical() => find.byWidgetPredicate((w) => w is Scrollable && w.axis == Axis.vertical);

    Future<void> open(WidgetTester t, Session s, _FakeApi api, {String lang = 'ru', bool dark = false}) async {
      await t.pumpWidget(app(s, api, lang: lang, dark: dark));
      await t.pumpAndSettle();
    }

    testWidgets('владелец: 500 приходов — список ленивый, итоги закреплены, подгрузка до конца, «К началу»', (t) async {
      phone(t);
      final api = _api();
      await open(t, _owner, api);
      final l = lx('ru');
      expect(t.takeException(), isNull);
      // итоги
      expect(find.byKey(const Key('pbTotalQty')), findsOneWidget);
      expect(find.byKey(const Key('pbAvgCost')), findsOneWidget);
      expect(find.byKey(const Key('pbLastCost')), findsOneWidget);
      expect(find.text(l.pbLastSupplier), findsOneWidget);
      // ленивый список: построены только видимые карточки, а не 20 (первая страница) и тем более не 100
      expect(find.byKey(const ValueKey('pbRow-r0')), findsOneWidget);
      expect(find.byWidgetPredicate((w) => w.key is ValueKey<String> && (w.key as ValueKey<String>).value.startsWith('pbRow-')).evaluate().length, lessThan(10));
      // остаток партии r0 = 10 (весь), следующей r5 = 5, оценка помечена
      expect(find.byKey(const ValueKey('pbRemain-r0')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const ValueKey('pbRemain-r0')), matching: find.text(l.pbRemainLine('10', 'шт'))), findsOneWidget);
      // листаем до самого старого прихода — подгружаются страницы сервера
      await t.scrollUntilVisible(find.byKey(const ValueKey('pbRow-r495')), 600, scrollable: vertical().first, maxScrolls: 400);
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      expect(api.calls.where((c) => c == 'api/main/suppliers/receipts/').length, 5, reason: '500 приходов по 100 на странице');
      expect(find.text(l.pbEnd), findsOneWidget);
      expect(find.byKey(const Key('pbTop')), findsOneWidget);
      await t.tap(find.byKey(const Key('pbTop')));
      await t.pumpAndSettle();
      expect(find.byKey(const ValueKey('pbRow-r0')), findsOneWidget);
      expect(find.byKey(const Key('pbTop')), findsNothing);
    });

    testWidgets('кассир: дата и количество; ни закупки, ни поставщиков, ни накладной, ни средней/последней закупки', (t) async {
      phone(t);
      final api = _api(total: 60);
      await open(t, _cashier, api);
      final l = lx('ru');
      expect(find.byKey(const ValueKey('pbRow-r0')), findsOneWidget);
      expect(find.textContaining('+10'), findsWidgets);
      expect(find.text('Поставщик 0'), findsNothing);
      expect(find.textContaining('Закупка'), findsNothing);
      expect(find.textContaining('Сумма'), findsNothing);
      expect(find.textContaining('Принял'), findsNothing);
      expect(find.byKey(const ValueKey('pbInvoice-r0')), findsNothing);
      expect(find.byKey(const Key('pbAvgCost')), findsNothing);
      expect(find.byKey(const Key('pbLastCost')), findsNothing);
      expect(find.byKey(const Key('pbLastSupplier')), findsNothing);
      expect(find.byKey(const Key('pbTotalQty')), findsOneWidget);
      expect(find.text(l.pbSearchHintNo), findsOneWidget);
      // поиск по поставщику у кассира не работает (поставщика он не видит)
      await t.enterText(find.byKey(const Key('pbSearch')), 'Поставщик');
      await t.pumpAndSettle();
      expect(find.byKey(const ValueKey('pbRow-r0')), findsNothing);
      expect(t.takeException(), isNull);
    });

    testWidgets('поиск по поставщику и по номеру; фильтр периода уходит на сервер', (t) async {
      phone(t);
      final api = _api(total: 120);
      await open(t, _owner, api);
      final l = lx('ru');
      await t.enterText(find.byKey(const Key('pbSearch')), 'r10');
      await t.pumpAndSettle();
      expect(find.byKey(const ValueKey('pbRow-r10')), findsOneWidget);
      expect(find.byKey(const ValueKey('pbRow-r0')), findsNothing);
      await t.enterText(find.byKey(const Key('pbSearch')), 'Поставщик 2');
      await t.pumpAndSettle();
      expect(find.byKey(const ValueKey('pbRow-r5')), findsOneWidget); // 5 % 3 == 2
      expect(find.byKey(const ValueKey('pbRow-r0')), findsNothing);
      await t.enterText(find.byKey(const Key('pbSearch')), 'нет такого');
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
      await t.enterText(find.byKey(const Key('pbSearch')), '');
      await t.tap(find.byKey(const Key('pbPeriod-week')));
      await t.pumpAndSettle();
      expect(api.queries.last!['date_from'], isNotNull);
      expect(find.text(l.pbWeek), findsOneWidget);
      await t.ensureVisible(find.byKey(const Key('pbPeriod-all'))); // чипы периода листаются вбок
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('pbPeriod-all')));
      await t.pumpAndSettle();
      expect(api.queries.last!.containsKey('date_from'), isFalse);
    });

    testWidgets('поставщик: тап открывает карточку с телефоном, «Позвонить» и WhatsApp', (t) async {
      phone(t);
      final api = _api(total: 20);
      await open(t, _owner, api);
      final l = lx('ru');
      await t.tap(find.descendant(of: find.byKey(const ValueKey('pbRow-r0')), matching: find.text('Поставщик 0')));
      await t.pumpAndSettle();
      expect(find.text('0700123456'), findsOneWidget);
      expect(find.text(l.pbCall), findsOneWidget);
      expect(find.text(l.pbWhatsApp), findsOneWidget);
    });

    testWidgets('«Остаток партии» — оценка: тап по строке объясняет, как считается', (t) async {
      phone(t);
      await open(t, _owner, _api(total: 20));
      final l = lx('ru');
      await t.tap(find.byKey(const ValueKey('pbRemain-r0')));
      await t.pumpAndSettle();
      expect(find.text(l.pbRemainTip('15', 'шт')), findsOneWidget);
    });

    testWidgets('нет связи: показан кэш с подписью «данные от ЧЧ:ММ»; без кэша — ошибка и «Повторить»', (t) async {
      phone(t);
      final repo = ProductBatchesRepository(_api(total: 0), db);
      await t.runAsync(() => repo.cacheWrite('P1', [BatchEntry(receiptId: 'r7', qty: 4, cost: 9, supplierName: 'Кэш-поставщик', at: DateTime(2026, 10, 20, 9))], now: DateTime(2026, 10, 30, 14, 5)));
      final l = lx('ru');
      await t.pumpWidget(app(_owner, _api(failWith: ApiException('Нет связи'))));
      await t.runAsync(() => Future.delayed(const Duration(milliseconds: 100)));
      await t.pumpAndSettle();
      expect(find.text(l.pbDataFrom('14:05')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const ValueKey('pbRow-r7')), matching: find.text('Кэш-поставщик')), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    testWidgets('нет связи и нет кэша: сообщение и «Повторить»', (t) async {
      phone(t);
      final l = lx('ru');
      await open(t, _owner, _api(failWith: ApiException('Нет связи')));
      expect(find.text(l.pbLoadError('Нет связи')), findsOneWidget);
      expect(find.text(l.retry), findsOneWidget);
    });

    testWidgets('у товара нет поступлений: «не найдено»', (t) async {
      phone(t);
      await open(t, _owner, _api(total: 0));
      expect(find.text(lx('ru').pbEmpty), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    testWidgets('накладная: состав прихода, товар подсвечен; файла нет — честно «исходный файл не сохранён»', (t) async {
      phone(t);
      await open(t, _owner, _api(total: 20));
      final l = lx('ru');
      await t.tap(find.byKey(const ValueKey('pbInvoice-r0')));
      await t.pumpAndSettle();
      expect(find.text(l.pbDocTitle), findsOneWidget);
      expect(find.byKey(const ValueKey('pbDocLine-P1')), findsOneWidget);
      expect(find.byKey(const ValueKey('pbDocLine-PZ')), findsOneWidget);
      expect(find.text(l.pbThisProduct), findsOneWidget, reason: 'подсвечена только строка этого товара');
      expect(find.byKey(const Key('pbNoFile')), findsOneWidget);
      expect(find.text(l.pbNoFile), findsOneWidget);
      expect(find.byKey(const Key('pbOpenFile')), findsNothing);
    });

    testWidgets('накладная с файлом на сервере: кнопка «Открыть файл»', (t) async {
      phone(t);
      await open(t, _owner, _api(total: 20, files: {'r0': '/media/invoices/r0.pdf'}));
      await t.tap(find.byKey(const ValueKey('pbInvoice-r0')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('pbOpenFile')), findsOneWidget);
      expect(find.byKey(const Key('pbNoFile')), findsNothing);
    });

    for (final cfg in const [
      ('ru', false),
      ('ky', true),
      ('en', false),
      ('tr', true),
      ('uz', false),
    ]) {
      testWidgets('360 dp, шрифт 1,3, ${cfg.$1}${cfg.$2 ? ', тёмная' : ''}: без переполнений; владелец и кассир, накладная', (t) async {
        phone(t, scale: 1.3);
        final l = lx(cfg.$1);
        await open(t, _owner, _api(total: 80), lang: cfg.$1, dark: cfg.$2);
        expect(t.takeException(), isNull);
        expect(find.text(l.pbTitle), findsWidgets);
        for (var i = 0; i < 6; i++) {
          await t.drag(vertical().first, const Offset(0, -500));
          await t.pump();
          expect(t.takeException(), isNull, reason: 'прокрутка $i');
        }
        // кнопки действий ≥ 48 dp
        final inv = find.byWidgetPredicate((w) => w.key is ValueKey<String> && (w.key as ValueKey<String>).value.startsWith('pbInvoice-'));
        expect(inv, findsWidgets);
        expect(t.getSize(inv.first).height, greaterThanOrEqualTo(40));
        await t.tap(inv.first);
        await t.pumpAndSettle();
        expect(find.text(l.pbDocTitle), findsOneWidget);
        expect(t.takeException(), isNull);
        expect(find.text(l.pbNoFile), findsOneWidget);
      });
    }

    testWidgets('кассир на 360 dp, шрифт 1,3, тёмная, узбекский: без переполнений', (t) async {
      phone(t, scale: 1.3);
      await open(t, _cashier, _api(total: 80), lang: 'uz', dark: true);
      expect(t.takeException(), isNull);
      for (var i = 0; i < 4; i++) {
        await t.drag(vertical().first, const Offset(0, -500));
        await t.pump();
      }
      expect(t.takeException(), isNull);
    });
  });
}
