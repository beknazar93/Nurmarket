// 2026-11-20 (владелец: «если у товара в накладной нет штрихкода — при нажатии на товар сразу при добавлении сканировать штрихкод
// товара и привязать его»). Чистая логика привязки (основной/дополнительный код, конфликты, дрожание камеры), запись кода товару через
// очередь (офлайн), сканер «по очереди» и сценарий на экране умной камеры: строка без кода → скан → привязка. Камеры нет: вместо неё
// подставляется картинка, коды подаются в `onCode`, как со сканера.
import 'dart:async';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/sync/sync_service.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/scan/barcode_bind.dart';
import 'package:nurmarket/features/scan/invoice_bind.dart';
import 'package:nurmarket/features/scan/smart_camera.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'admin_stock_fixes_test.dart' show FakeApi, StockServer;

Product _p(String id, String name, {String? barcode, String alt = ''}) => Product(
  id: id,
  name: name,
  price: 100,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: 5,
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: alt,
  category: '',
  searchKey: name.toLowerCase(),
  barcode: barcode,
);

const _owner = Session(email: 'a@x.kg', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);
const _code1 = '4600123456789';
const _code2 = '4601234567895';

void main() {
  group('логика привязки', () {
    test('куда писать код: основным, если у товара его нет; дополнительным, если основной уже есть; ничего — если код уже у товара', () {
      expect(planBarcodeBind(_p('a', 'Вода'), _code1), BindPlan.main);
      expect(planBarcodeBind(_p('a', 'Вода', barcode: ''), _code1), BindPlan.main);
      expect(planBarcodeBind(_p('a', 'Вода', barcode: '111'), _code1), BindPlan.alt);
      expect(planBarcodeBind(_p('a', 'Вода', barcode: _code1), _code1), BindPlan.already);
      expect(planBarcodeBind(_p('a', 'Вода', barcode: '111', alt: '$_code1|12|Коробка'), _code1), BindPlan.already, reason: 'код упаковки у товара уже есть');
      expect(planBarcodeBind(_p('a', 'Вода', barcode: '0$_code1'), _code1), BindPlan.already, reason: 'ведущие нули — тот же код');
    });

    test('есть ли у товара код и нужен ли он строке накладной', () {
      expect(productHasCode(_p('a', 'Вода')), isFalse);
      expect(productHasCode(_p('a', 'Вода', barcode: '  ')), isFalse);
      expect(productHasCode(_p('a', 'Вода', barcode: _code1)), isTrue);
      expect(productHasCode(_p('a', 'Вода', alt: '$_code1|6|Блок')), isTrue);
      expect(rowNeedsCode(product: _p('a', 'Вода'), seenCodeValid: false), isTrue);
      expect(rowNeedsCode(product: _p('a', 'Вода', barcode: _code1), seenCodeValid: false), isFalse);
      expect(rowNeedsCode(product: null, seenCodeValid: false), isTrue, reason: 'новый товар без кода в накладной');
      expect(rowNeedsCode(product: null, seenCodeValid: true), isFalse, reason: 'код с верной контрольной цифрой прочитан из накладной');
      expect(rowNeedsCode(product: null, seenCodeValid: false, boundCode: _code1), isFalse, reason: 'код привязан сканером');
    });

    test('конфликты: код у другого товара каталога, у своего товара, у другой строки накладной (она проверяется раньше каталога)', () {
      final mine = _p('a', 'Вода'), other = _p('b', 'Сок', barcode: _code1);
      expect(checkBindConflict().kind, BindConflictKind.none);
      expect(checkBindConflict(catalogOwner: other, rowProductId: 'a').kind, BindConflictKind.catalog);
      expect(checkBindConflict(catalogOwner: other, rowProductId: 'a').product?.name, 'Сок');
      expect(checkBindConflict(catalogOwner: other).kind, BindConflictKind.catalog, reason: 'новая строка: товар уже есть в каталоге');
      expect(checkBindConflict(catalogOwner: mine, rowProductId: 'a').kind, BindConflictKind.own);
      final d = checkBindConflict(catalogOwner: other, rowProductId: 'a', draftRowName: 'Лимонад');
      expect(d.kind, BindConflictKind.draft);
      expect(d.rowName, 'Лимонад');
    });

    test('дрожание камеры: тот же код в течение 1,5 с игнорируется, пока он в кадре — тоже; другой код проходит сразу', () {
      var t = DateTime(2026, 11, 20, 12);
      final d = CodeDebounce(clock: () => t);
      expect(d.accept(_code1), isTrue);
      t = t.add(const Duration(milliseconds: 300));
      expect(d.accept(_code1), isFalse);
      t = t.add(const Duration(milliseconds: 1000));
      expect(d.accept(_code1), isFalse, reason: 'код всё ещё в кадре: отсчёт идёт от последнего кадра');
      expect(d.accept(_code2), isTrue);
      t = t.add(const Duration(milliseconds: 1600));
      expect(d.accept(_code2), isTrue, reason: 'камеру отводили больше 1,5 с — код снова новый');
    });
  });

  group('запись кода товару (очередь)', () {
    late AppDatabase db;
    late StockRepository repo;
    late StockServer s;
    late FakeApi api;
    var offline = false;
    final patches = <Object?>[];

    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      s = StockServer();
      offline = false;
      patches.clear();
      api = FakeApi((m, p, b, q) async {
        if (offline) throw ApiException('Нет связи');
        if (m == 'PATCH') patches.add(b);
        return s.handle(m, p, b, q);
      })..attach(_owner);
      repo = StockRepository(api, db);
    });
    tearDown(() => db.close());

    Future<String> addProduct(String name, {String barcode = ''}) async {
      final id = s.add(name, barcode: barcode, qty: 3);
      await db.upsertProduct(ProductsCompanion.insert(id: id, name: name, barcode: Value(barcode.isEmpty ? null : barcode), quantity: const Value(3), searchKey: Value(name.toLowerCase())));
      return id;
    }

    Future<void> settle() async {
      for (var i = 0; i < 20; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 10));
      }
    }

    test('со связью: у товара нет кода — PATCH barcode, запись «отправлена», на телефоне код сразу', () async {
      final id = await addProduct('Вода');
      expect(await repo.queueBarcodeBind((await db.productById(id))!, _code1), isTrue);
      await settle();
      expect(patches, [
        {'barcode': _code1},
      ]);
      expect(s.products[id]!['barcode'], isNotNull);
      final row = (await db.select(db.outbox).get()).single;
      expect(row.kind, 'barcode');
      expect(row.status, 'sent');
      expect((await db.productById(id))!.barcode, _code1);
      expect((await db.productByScan(_code1))?.product.id, id, reason: 'сканер находит товар по новому коду');
    });

    test('без связи: запись ждёт в очереди, код на телефоне уже есть; повторный скан того же кода не создаёт второй записи', () async {
      final id = await addProduct('Вода');
      offline = true;
      final p = (await db.productById(id))!;
      expect(await repo.queueBarcodeBind(p, _code1), isTrue);
      await settle();
      var rows = await db.select(db.outbox).get();
      expect(rows.single.status, 'pending');
      expect(rows.single.summary, contains('Вода'));
      expect((await db.productById(id))!.barcode, _code1);
      // тот же код тому же товару ещё раз (дрожание, повторный скан): запись одна
      expect(await repo.queueBarcodeBind(p, _code1), isTrue);
      await settle();
      rows = await db.select(db.outbox).get();
      expect(rows.length, 1);
      // связь появилась — очередь SyncService отправляет код
      offline = false;
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: repo, catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      await sync.runOnce();
      expect((await db.select(db.outbox).get()).single.status, 'sent');
      expect(patches, [
        {'barcode': _code1},
      ]);
    });

    test('у товара уже есть основной код: новый уходит дополнительным, старые дополнительные сохраняются', () async {
      final id = await addProduct('Сок', barcode: '2000000000015');
      s.products[id]!['alternate_barcodes'] = [
        {'barcode': '2000000000022', 'name': 'Блок', 'quantity': 6},
      ];
      expect(await repo.queueBarcodeBind((await db.productById(id))!, _code1), isTrue);
      await settle();
      expect(patches.single, {
        'alternate_barcodes': [
          {'barcode': '2000000000022', 'name': 'Блок', 'quantity': 6},
          {'barcode': _code1, 'name': ''},
        ],
      });
      expect((await db.productById(id))!.altBarcodes, contains(_code1));
    });

    test('код уже есть у товара на сервере: ничего не пишем, запись отправлена; код у самого товара на телефоне — писать нечего', () async {
      final id = await addProduct('Вода');
      s.products[id]!['barcode'] = _code1; // на сервере код уже есть (кто-то добавил с кассы), на телефоне ещё нет
      expect(await repo.queueBarcodeBind((await db.productById(id))!, _code1), isTrue);
      await settle();
      expect(patches, isEmpty);
      expect((await db.select(db.outbox).get()).single.status, 'sent');
      expect(await repo.queueBarcodeBind((await db.productById(id))!, _code1), isFalse, reason: 'на телефоне код уже записан — повтор не нужен');
    });

    test('сервер отказал (код занят другим товаром): запись «Не принято» с причиной, повтор после отказа записывает заново', () async {
      final id = await addProduct('Вода');
      var reject = true;
      api.handler = (m, p, b, q) async {
        if (m == 'PATCH' && reject) throw ApiException('Штрихкод уже используется', status: 400);
        return s.handle(m, p, b, q);
      };
      await repo.queueBarcodeBind((await db.productById(id))!, _code1);
      await settle();
      var row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'failed');
      expect(row.lastError, contains('уже используется'));
      expect((await db.productById(id))!.barcode, isNull, reason: 'отказ сервера — на телефоне кода у товара нет');
      reject = false;
      await repo.queueBarcodeBind((await db.productById(id))!, _code1);
      await settle();
      row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'sent');
    });
  });

  group('сканер «по очереди» (BarcodeBindPage)', () {
    late List<(int, BindResult)> done;
    late List<Object?> popped;

    setUp(() {
      done = [];
      popped = [];
    });
    tearDown(() {
      BarcodeBindPage.testLookup = null;
      BarcodeBindPage.testPreview = null;
    });

    Widget app(BarcodeBindPage page, {double scale = 1.0, Locale locale = const Locale('ru')}) => ProviderScope(
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: locale,
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        builder: (c, child) => MediaQuery(data: MediaQuery.of(c).copyWith(textScaler: TextScaler.linear(scale)), child: child!),
        home: Builder(
          builder: (c) => Scaffold(
            body: Center(child: ElevatedButton(onPressed: () async => popped.add(await Navigator.of(c).push<BindResult>(MaterialPageRoute(builder: (_) => page))), child: const Text('open'))),
          ),
        ),
      ),
    );

    BarcodeBindPage queue(List<BindRow> rows, {Product? Function(String)? owner, String? Function(String, int)? draft}) => BarcodeBindPage(
      rows: rows,
      preview: const ColoredBox(color: Colors.black),
      lookup: (code) async => (product: owner?.call(code), unavailable: false),
      onRowDone: (i, r) async => done.add((i, r)),
      draftOwner: draft,
      allowReplace: false,
      offerUseOwner: true,
    );

    Future<void> open(WidgetTester t, BarcodeBindPage p, {Size size = const Size(360, 640), double scale = 1.0, Locale locale = const Locale('ru')}) async {
      t.view.physicalSize = size;
      t.view.devicePixelRatio = 1;
      addTearDown(t.view.reset);
      await t.pumpWidget(app(p, scale: scale, locale: locale));
      await t.tap(find.text('open'));
      await t.pump();
      await t.pump(const Duration(milliseconds: 400));
    }

    BarcodeBindPageState state(WidgetTester t) => t.state<BarcodeBindPageState>(find.byType(BarcodeBindPage));

    Future<void> next(WidgetTester t) async {
      await t.pump();
      await t.pump(const Duration(milliseconds: 400));
    }

    const rows = [BindRow('Вода «Нур» 1 л'), BindRow('Сок яблочный 0,2 л', 'p2'), BindRow('Печенье овсяное')];

    testWidgets('заголовок «Отсканируйте штрихкод товара», название строки и «Товар 1 из 3»', (t) async {
      await open(t, queue(rows));
      expect(find.text('Отсканируйте штрихкод товара'), findsOneWidget);
      expect(find.byKey(const Key('bind_row_name')), findsOneWidget);
      expect(find.text('Вода «Нур» 1 л'), findsOneWidget);
      expect(find.text('Товар 1 из 3'), findsOneWidget);
      expect(find.text('Пропустить'), findsOneWidget);
      expect(find.text('Ввести вручную'), findsOneWidget);
    });

    testWidgets('скан → «Готово» → итог по строке уходит наружу, камера переходит к следующей; после последней — закрывается', (t) async {
      await open(t, queue(rows));
      await state(t).onCode(_code1);
      await t.pump();
      expect(state(t).phase, BindPhase.confirm);
      expect(find.text('Штрихкод $_code1 будет привязан к «Вода «Нур» 1 л»'), findsOneWidget);
      await t.tap(find.widgetWithText(FilledButton, 'Готово'));
      await next(t);
      expect(done.map((e) => (e.$1, e.$2.code)), [(0, _code1)]);
      expect(state(t).row, 1);
      expect(state(t).phase, BindPhase.scan);
      expect(find.text('Товар 2 из 3'), findsOneWidget);
      expect(find.text('Сок яблочный 0,2 л'), findsOneWidget);
      // «Пропустить» — следующая строка без записи
      await t.tap(find.text('Пропустить'));
      await next(t);
      expect(state(t).row, 2);
      expect(done.length, 1);
      // последняя строка: второй код, «Готово» — страница закрывается
      await state(t).onCode(_code2);
      await t.pump();
      await t.tap(find.widgetWithText(FilledButton, 'Готово'));
      await next(t);
      expect(done.map((e) => (e.$1, e.$2.code)), [(0, _code1), (2, _code2)]);
      expect(find.byType(BarcodeBindPage), findsNothing);
      expect(popped.length, 1);
    });

    testWidgets('«Пропустить» на последней строке и крестик закрывают очередь; крестик не теряет уже привязанное', (t) async {
      await open(t, queue(rows));
      await state(t).onCode(_code1);
      await t.pump();
      await t.tap(find.widgetWithText(FilledButton, 'Готово'));
      await next(t);
      await t.tap(find.byKey(const Key('bind_close')));
      await next(t);
      expect(find.byType(BarcodeBindPage), findsNothing);
      expect(done.length, 1, reason: 'первая строка уже записана');
      expect(popped.single, isNull, reason: 'крестик — выход без результата');
    });

    testWidgets('тот же код, что у другой строки этой накладной: объяснение «уже у строки …», код не записывается, можно сканировать другой', (t) async {
      await open(t, queue(rows, draft: (code, i) => code == _code1 && i == 1 ? 'Вода «Нур» 1 л' : null));
      await state(t).onCode(_code1);
      await t.pump();
      await t.tap(find.widgetWithText(FilledButton, 'Готово'));
      await next(t);
      expect(state(t).row, 1);
      // сразу после привязки тот же код (он ещё в кадре) молча игнорируется 1,5 с — это и есть защита от дрожания
      unawaited(state(t).onCode(_code1));
      await next(t);
      expect(find.textContaining('уже у строки'), findsNothing);
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 1600)));
      unawaited(state(t).onCode(_code1)); // onCode ждёт закрытия окна с объяснением
      await next(t);
      expect(find.textContaining('уже у строки «Вода «Нур» 1 л» этой накладной'), findsOneWidget);
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.byType(TextButton)));
      await next(t);
      expect(state(t).phase, BindPhase.scan, reason: 'после объяснения камера снова ждёт код');
      expect(done.length, 1);
      await state(t).onCode(_code2);
      await t.pump();
      expect(state(t).phase, BindPhase.confirm);
    });

    testWidgets('код уже у товара каталога: видно название, «Выбрать «…»» делает строку этим товаром, код не пишется', (t) async {
      final other = _p('o1', 'Сок «Добрый» 1 л', barcode: _code1);
      await open(t, queue(rows, owner: (c) => c == _code1 ? other : null));
      unawaited(state(t).onCode(_code1));
      await next(t);
      expect(find.textContaining('уже у товара «Сок «Добрый» 1 л» в каталоге'), findsOneWidget);
      expect(find.text('Оставить'), findsOneWidget);
      expect(find.text('Отмена'), findsOneWidget);
      await t.tap(find.text('Выбрать «Сок «Добрый» 1 л»'));
      await next(t);
      expect(done.single.$2.useOwner?.id, 'o1');
      expect(done.single.$2.code, isNull);
      expect(state(t).row, 1, reason: 'камера перешла к следующей строке');
    });

    testWidgets('«Оставить» у конфликта — строка без кода, переход дальше; «Отмена» — снова ждём код', (t) async {
      final other = _p('o1', 'Сок «Добрый» 1 л', barcode: _code1);
      await open(t, queue(rows, owner: (c) => c == _code1 ? other : null));
      unawaited(state(t).onCode(_code1));
      await next(t);
      await t.tap(find.text('Отмена'));
      await next(t);
      expect(state(t).row, 0);
      expect(state(t).phase, BindPhase.scan);
      await state(t).onCode(_code2); // другой код, без конфликта, чтобы не упереться в окно 2 с после «Отмены»
      await t.pump();
      expect(state(t).phase, BindPhase.confirm);
      await t.tap(find.text('Сканировать заново'));
      await next(t);
      unawaited(state(t).onCode(_code1));
      await next(t);
      await t.tap(find.text('Оставить'));
      await next(t);
      expect(done, isEmpty);
      expect(state(t).row, 1);
    });

    for (final size in const [Size(360, 640), Size(411, 891)]) {
      for (final lang in const ['ru', 'ky', 'uz']) {
        testWidgets('${size.width.toInt()}×${size.height.toInt()}, $lang, шрифт 1,3: заголовок и кнопки целы, кнопки ≥ 48', (t) async {
          await open(t, queue(const [BindRow('Напиток газированный «Сэмпл» лимон-лайм 1,5 л')]), size: size, scale: 1.3, locale: Locale(lang));
          expect(t.takeException(), isNull);
          final l = lookupAppLocalizations(Locale(lang));
          expect(find.text(l.bindRowTitle), findsOneWidget);
          expect(find.text(l.bindRowOf(1, 1)), findsOneWidget);
          for (final label in [l.bindSkip, l.bindManual]) {
            final f = find.text(label);
            expect(f, findsOneWidget, reason: label);
            final r = t.getRect(find.ancestor(of: f, matching: find.byWidgetPredicate((w) => w is ButtonStyleButton)));
            expect(r.height, greaterThanOrEqualTo(48), reason: label);
            expect(r.bottom, lessThanOrEqualTo(size.height), reason: '$label ниже экрана');
            expect(r.left, greaterThanOrEqualTo(0));
            expect(r.right, lessThanOrEqualTo(size.width));
          }
          expect(t.getRect(find.byKey(const Key('bind_close'))).height, greaterThanOrEqualTo(48));
          // подпись заголовка и название целиком в пределах экрана и не накладываются на кнопки
          final title = t.getRect(find.text(l.bindRowTitle));
          expect(title.right, lessThanOrEqualTo(size.width));
          final skipTop = t.getRect(find.ancestor(of: find.text(l.bindSkip), matching: find.byWidgetPredicate((w) => w is ButtonStyleButton))).top;
          expect(t.getRect(find.byKey(const Key('bind_row_name'))).bottom, lessThan(skipTop));
        });
      }
    }
  });

  group('экран умной камеры: строка без штрихкода → скан → привязка', () {
    late AppDatabase db;
    late StockServer s;
    late FakeApi api;
    late List<String> log;
    late List<Object?> patches;
    late List<Map<String, dynamic>> created;

    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      s = StockServer();
      log = [];
      patches = [];
      created = [];
      api = FakeApi((m, p, b, q) async {
        log.add('$m $p');
        if (m == 'PATCH') patches.add(b);
        if (m == 'POST' && p == 'api/main/products/create-manual/') created.add((b as Map).cast<String, dynamic>());
        return s.handle(m, p, b, q);
      })..attach(_owner);
      BarcodeBindPage.testPreview = const ColoredBox(color: Colors.black);
      BarcodeBindPage.testLookup = (code) async => (product: null, unavailable: false);
    });
    tearDown(() async {
      BarcodeBindPage.testLookup = null;
      BarcodeBindPage.testPreview = null;
      await db.close();
    });

    Future<void> pumpScreen(WidgetTester tester, List<SeenMatch> matches, {String mode = 'invoice', Size size = const Size(411, 891), double scale = 1.0, Locale locale = const Locale('ru')}) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api)],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: locale,
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            builder: (c, child) => MediaQuery(data: MediaQuery.of(c).copyWith(textScaler: TextScaler.linear(scale)), child: child!),
            home: SmartCameraScreen(debugMatches: matches, debugMode: mode),
          ),
        ),
      );
      await tester.pump();
    }

    Future<void> settle(WidgetTester tester) async {
      for (var i = 0; i < 12; i++) {
        await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
        await tester.pump();
      }
    }

    BarcodeBindPageState bind(WidgetTester t) => t.state<BarcodeBindPageState>(find.byType(BarcodeBindPage));
    Finder bindButtons() => find.byWidgetPredicate((w) => w.key is ValueKey && '${(w.key as ValueKey).value}'.startsWith('camBind_'));

    Future<String> addKnown(String name) async {
      final id = s.add(name, qty: 3);
      await db.upsertProduct(ProductsCompanion.insert(id: id, name: name, quantity: const Value(3), searchKey: Value(name.toLowerCase())));
      return id;
    }

    testWidgets('существующий товар без кода: нажатие на товар открывает сканер; код привязан, строка помечена, запись ушла на сервер', (tester) async {
      final id = await addKnown('Вода «Нур» 1 л');
      final p = (await db.productById(id))!;
      await pumpScreen(tester, [SeenMatch(SeenItem(name: p.name, count: 2, labelPrice: 40), [(p, 1.0)])]);
      expect(find.byKey(const Key('cam_bind_queue')), findsOneWidget, reason: 'в шапке — «Привязать по очереди» с числом строк');
      expect(find.text('1'), findsWidgets);
      expect(bindButtons(), findsOneWidget);
      // нажатие на сам товар (название) — сразу сканер
      await tester.tap(find.text('Вода «Нур» 1 л'));
      await settle(tester);
      expect(find.byType(BarcodeBindPage), findsOneWidget);
      expect(find.text('Отсканируйте штрихкод товара'), findsOneWidget);
      expect(find.text('Товар 1 из 1'), findsOneWidget);
      await bind(tester).onCode(_code1);
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Готово'));
      await settle(tester);
      expect(find.byType(BarcodeBindPage), findsNothing, reason: 'это была последняя строка без кода — камера закрылась');
      expect(find.text('Штрихкод $_code1'), findsOneWidget);
      expect(bindButtons(), findsNothing);
      expect(find.byKey(const Key('cam_bind_queue')), findsNothing);
      expect(find.text('Штрихкоды привязаны: 1'), findsOneWidget);
      expect(patches, [
        {'barcode': _code1},
      ]);
      expect((await db.productById(id))!.barcode, _code1);
      final row = (await db.select(db.outbox).get()).single;
      expect((row.kind, row.status), ('barcode', 'sent'));
    });

    testWidgets('новый товар: привязанный код уходит в карточку при создании (create-manual), отдельной записи нет', (tester) async {
      await pumpScreen(tester, [SeenMatch(const SeenItem(name: 'Печенье овсяное', count: 3, labelPrice: 40), const [])]);
      await tester.tap(bindButtons());
      await settle(tester);
      expect(find.text('Печенье овсяное'), findsWidgets);
      await bind(tester).onCode(_code2);
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Готово'));
      await settle(tester);
      expect(find.text('Штрихкод $_code2'), findsOneWidget);
      expect(await db.select(db.outbox).get(), isEmpty, reason: 'у нового товара код едет в создании, а не отдельной записью');
      await tester.tap(find.widgetWithText(FilledButton, 'Загрузить 1 товар на склад'));
      await settle(tester);
      expect(created.single['barcode'], _code2);
      expect(created.single['name'], 'Печенье овсяное');
    });

    testWidgets('«Привязать по очереди»: две строки; тот же код во второй строке — объяснение, не дубль; «Пропустить» — без кода', (tester) async {
      final id = await addKnown('Вода «Нур» 1 л');
      final p = (await db.productById(id))!;
      await pumpScreen(tester, [
        SeenMatch(SeenItem(name: p.name, count: 2, labelPrice: 40), [(p, 1.0)]),
        SeenMatch(const SeenItem(name: 'Печенье овсяное', count: 3, labelPrice: 40), const []),
      ]);
      expect(find.text('2'), findsWidgets, reason: 'число строк без кода — значком на кнопке');
      await tester.tap(find.byKey(const Key('cam_bind_queue')));
      await settle(tester);
      expect(find.text('Товар 1 из 2'), findsOneWidget);
      await bind(tester).onCode(_code1);
      await tester.pump();
      await tester.tap(find.widgetWithText(FilledButton, 'Готово'));
      await settle(tester);
      expect(find.text('Товар 2 из 2'), findsOneWidget);
      expect(find.text('Печенье овсяное'), findsWidgets);
      // тот же код у второй строки: «уже у строки …», запись не создаётся
      unawaited(bind(tester).onCode(_code1));
      await settle(tester); // 12 × 40 мс — меньше 1,5 с: тот же код в кадре игнорируется
      expect(find.textContaining('уже у строки'), findsNothing);
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 1200)));
      unawaited(bind(tester).onCode(_code1));
      await settle(tester);
      expect(find.textContaining('уже у строки «Вода «Нур» 1 л» этой накладной'), findsOneWidget);
      await tester.tap(find.descendant(of: find.byType(AlertDialog), matching: find.byType(TextButton)));
      await settle(tester);
      // «Пропустить» — вторая строка остаётся без кода, очередь закрывается
      await tester.tap(find.text('Пропустить'));
      await settle(tester);
      expect(find.byType(BarcodeBindPage), findsNothing);
      expect(find.text('Штрихкод $_code1'), findsOneWidget);
      expect(bindButtons(), findsOneWidget, reason: 'вторая строка по-прежнему без кода');
      expect((await db.select(db.outbox).get()).length, 1);
      expect(find.text('Штрихкоды привязаны: 1'), findsOneWidget);
    });

    testWidgets('код уже у другого товара каталога: «Выбрать» — строка становится этим товаром («В базе»), новый товар не создаётся', (tester) async {
      final other = await addKnown('Сок «Добрый» 1 л');
      await db.upsertProduct(ProductsCompanion.insert(id: other, name: 'Сок «Добрый» 1 л', barcode: const Value(_code1), quantity: const Value(3), searchKey: const Value('сок добрый 1 л')));
      final owner = (await db.productById(other))!;
      BarcodeBindPage.testLookup = (code) async => (product: code == _code1 ? owner : null, unavailable: false);
      await pumpScreen(tester, [SeenMatch(const SeenItem(name: 'Сок добрый', count: 1, labelPrice: 40), const [])]);
      expect(find.textContaining('НОВЫХ'), findsOneWidget);
      await tester.tap(bindButtons());
      await settle(tester);
      unawaited(bind(tester).onCode(_code1));
      await settle(tester);
      expect(find.textContaining('уже у товара «Сок «Добрый» 1 л» в каталоге'), findsOneWidget);
      await tester.tap(find.text('Выбрать «Сок «Добрый» 1 л»'));
      await settle(tester);
      expect(find.byType(BarcodeBindPage), findsNothing);
      expect(find.textContaining('НОВЫХ'), findsNothing);
      expect(find.textContaining('В БАЗЕ'), findsOneWidget);
      expect(find.text('Сок «Добрый» 1 л'), findsOneWidget);
      expect(patches, isEmpty, reason: 'код у товара уже есть — на сервер ничего не пишем');
    });

    testWidgets('в «Полке» и «Этикетках» кнопок привязки нет (только накладная)', (tester) async {
      await pumpScreen(tester, [SeenMatch(const SeenItem(name: 'Печенье овсяное', count: 3, labelPrice: 40), const [])], mode: 'shelf');
      expect(bindButtons(), findsNothing);
      expect(find.byKey(const Key('cam_bind_queue')), findsNothing);
    });

    for (final size in const [Size(360, 640), Size(411, 891)]) {
      for (final lang in const ['ru', 'ky', 'uz']) {
        for (final known in [true, false]) {
          testWidgets('${size.width.toInt()}×${size.height.toInt()}, $lang, шрифт 1,3, ${known ? 'товар из базы' : 'новый товар'}: кнопка привязки без переполнения, высота ≥ 48', (tester) async {
            final id = await addKnown('Вода «Нур» 1 л');
            final p = (await db.productById(id))!;
            await pumpScreen(
              tester,
              [known ? SeenMatch(SeenItem(name: p.name, count: 2, labelPrice: 40), [(p, 1.0)]) : SeenMatch(const SeenItem(name: 'Печенье овсяное сдобное 400 г', count: 3, labelPrice: 40), const [])],
              size: size,
              scale: 1.3,
              locale: Locale(lang),
            );
            expect(tester.takeException(), isNull);
            final l = lookupAppLocalizations(Locale(lang));
            final q = find.byKey(const Key('cam_bind_queue'));
            expect(q, findsOneWidget);
            expect(tester.getRect(q).height, greaterThanOrEqualTo(48));
            expect(tester.getRect(q).width, greaterThanOrEqualTo(48));
            expect(tester.getRect(q).right, lessThanOrEqualTo(size.width));
            await tester.scrollUntilVisible(bindButtons(), 150, scrollable: find.descendant(of: find.byType(ListView), matching: find.byType(Scrollable)).first);
            final btn = bindButtons();
            expect(btn, findsOneWidget);
            final r = tester.getRect(btn);
            expect(r.height, greaterThanOrEqualTo(48));
            expect(r.left, greaterThanOrEqualTo(0));
            expect(r.right, lessThanOrEqualTo(size.width));
            // подпись кнопки целиком: две строки не вылезают за кнопку, переполнения нет
            final text = find.descendant(of: btn, matching: find.byType(Text));
            expect(text, findsOneWidget);
            expect(tester.getRect(text).height, lessThanOrEqualTo(r.height));
            expect(tester.getRect(text).right, lessThanOrEqualTo(r.right));
            expect(find.textContaining(l.camBindBtn), findsWidgets);
            expect(tester.takeException(), isNull);
          });
        }
      }
    }
  });

  group('окно «Узнаю товар…»: этапы и действия через 20 с', () {
    Future<ValueNotifier<RecognizeStage>> pumpWait(
      WidgetTester t, {
      required DateTime Function() now,
      Size size = const Size(360, 640),
      double scale = 1.0,
      Locale locale = const Locale('ru'),
      bool invoice = true,
      VoidCallback? onCancel,
      VoidCallback? onRetry,
      VoidCallback? onManual,
      VoidCallback? onCloser,
    }) async {
      t.view.physicalSize = size;
      t.view.devicePixelRatio = 1;
      addTearDown(t.view.reset);
      final stage = ValueNotifier(RecognizeStage.compress);
      addTearDown(stage.dispose);
      await t.pumpWidget(
        MaterialApp(
          theme: buildTheme(dark: false),
          locale: locale,
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          builder: (c, child) => MediaQuery(data: MediaQuery.of(c).copyWith(textScaler: TextScaler.linear(scale)), child: child!),
          home: Scaffold(
            body: RecognizeWait(
              invoice: invoice,
              stage: stage,
              now: now,
              onCancel: onCancel ?? () {},
              onRetry: onRetry ?? () {},
              onManual: onManual,
              onCloser: onCloser,
            ),
          ),
        ),
      );
      await t.pump();
      return stage;
    }

    testWidgets('этапы по порядку: «Сжимаю фото…» → «Читаю накладную…» → «Ищу товары в каталоге…», шаг n из 3', (t) async {
      final stage = await pumpWait(t, now: () => DateTime(2026, 11, 20, 12));
      expect(find.text('Сжимаю фото…'), findsOneWidget);
      expect(find.text('Шаг 1 из 3'), findsOneWidget);
      stage.value = RecognizeStage.read;
      await t.pump();
      expect(find.text('Читаю накладную…'), findsOneWidget);
      expect(find.text('Шаг 2 из 3'), findsOneWidget);
      stage.value = RecognizeStage.match;
      await t.pump();
      expect(find.text('Ищу товары в каталоге…'), findsOneWidget);
      expect(find.text('Шаг 3 из 3'), findsOneWidget);
      expect(find.text('Отмена'), findsOneWidget);
    });

    testWidgets('не накладная: на втором этапе прежнее «Узнаю товар…»', (t) async {
      final stage = await pumpWait(t, now: () => DateTime(2026, 11, 20, 12), invoice: false);
      stage.value = RecognizeStage.read;
      await t.pump();
      expect(find.text('Узнаю товар…'), findsOneWidget);
    });

    testWidgets('до 20 с — только «Отмена»; через 20 с — «Повторить», «Ввести вручную», «Сфотографировать ближе» и «Отмена» работают', (t) async {
      var clock = DateTime(2026, 11, 20, 12);
      final calls = <String>[];
      await pumpWait(
        t,
        now: () => clock,
        onCancel: () => calls.add('cancel'),
        onRetry: () => calls.add('retry'),
        onManual: () => calls.add('manual'),
        onCloser: () => calls.add('closer'),
      );
      clock = clock.add(const Duration(seconds: 10));
      await t.pump(const Duration(seconds: 10));
      expect(find.text('Повторить'), findsNothing);
      expect(find.textContaining('ИИ думает дольше обычного'), findsOneWidget, reason: 'после 6 с — честное пояснение');
      clock = clock.add(const Duration(seconds: 12));
      await t.pump(const Duration(seconds: 12));
      expect(find.textContaining('Прошло 22 с'), findsOneWidget);
      expect(find.text('Читается долго. Подождите или выберите действие:'), findsOneWidget);
      for (final label in ['Повторить', 'Ввести вручную', 'Сфотографировать ближе', 'Отмена']) {
        final f = find.text(label);
        expect(f, findsOneWidget, reason: label);
        expect(t.getRect(find.ancestor(of: f, matching: find.byWidgetPredicate((w) => w is ButtonStyleButton))).height, greaterThanOrEqualTo(48), reason: label);
        await t.tap(f);
        await t.pump();
      }
      expect(calls, ['retry', 'manual', 'closer', 'cancel']);
    });

    testWidgets('без «Ввести вручную» и «ближе» (галерея, файл): через 20 с — «Повторить» и «Отмена»', (t) async {
      var clock = DateTime(2026, 11, 20, 12);
      await pumpWait(t, now: () => clock);
      clock = clock.add(const Duration(seconds: 25));
      await t.pump(const Duration(seconds: 25));
      expect(find.text('Повторить'), findsOneWidget);
      expect(find.text('Ввести вручную'), findsNothing);
      expect(find.text('Сфотографировать ближе'), findsNothing);
    });

    for (final size in const [Size(360, 640), Size(411, 891)]) {
      for (final lang in const ['ru', 'ky', 'uz']) {
        testWidgets('${size.width.toInt()}×${size.height.toInt()}, $lang, шрифт 1,3, через 25 с: все четыре кнопки на экране, ≥ 48, без переполнения', (t) async {
          var clock = DateTime(2026, 11, 20, 12);
          await pumpWait(t, now: () => clock, size: size, scale: 1.3, locale: Locale(lang), onManual: () {}, onCloser: () {});
          clock = clock.add(const Duration(seconds: 25));
          await t.pump(const Duration(seconds: 25));
          expect(t.takeException(), isNull);
          final l = lookupAppLocalizations(Locale(lang));
          for (final label in [l.retry, l.recManual, l.recRetakeCloser, l.cancel]) {
            final f = find.text(label);
            expect(f, findsOneWidget, reason: label);
            await t.ensureVisible(f);
            await t.pump();
            final r = t.getRect(find.ancestor(of: f, matching: find.byWidgetPredicate((w) => w is ButtonStyleButton)));
            expect(r.height, greaterThanOrEqualTo(47.99), reason: label); // 48 dp с точностью до дроби
            expect(r.left, greaterThanOrEqualTo(0), reason: label);
            expect(r.right, lessThanOrEqualTo(size.width), reason: label);
          }
        });
      }
    }
  });
}
