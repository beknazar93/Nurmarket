// 2026-11-08 (жалоба владельца): «на складе товар в литрах (3 л), пришло 10 л, а приёмка ведёт строку как штучную». Единица берётся
// из карточки товара (л, кг, м…), количество вводится в ней и ПРИБАВЛЯЕТСЯ к остатку (3 + 10 = 13 л), дробные допустимы; новый товар из
// накладной ИИ сохраняет единицу и дробное количество. Склад NurCRM — в памяти (StockServer), сети и боевой базы нет.
import 'dart:async';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/catalog/global_products.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/units.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/features/scan/smart_camera.dart';
import 'package:nurmarket/features/stock/receive_units.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'admin_stock_fixes_test.dart' show FakeApi, StockServer, stockOf;

Product _p(String id, String name, double qty, {String unit = 'л'}) => Product(
  id: id,
  name: name,
  price: 100,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: qty,
  unit: unit,
  isWeight: unit == 'кг',
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: name.toLowerCase(),
);

const _admin = Session(email: 'a', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _NoGlobals extends GlobalProducts {
  _NoGlobals() : super(openFile: () async => throw UnimplementedError());
  @override
  Future<GlobalProduct?> find(String raw, {bool network = true}) async => null;
}

void main() {
  group('единица и прибавка к остатку — чистые функции', () {
    test('3 л + 10 л = 13 л, дробные тоже', () {
      expect(stockAfter(3, 10), 13);
      expect(stockAfter(3.5, 10.25), 13.75);
      expect(stockAfter(0.1, 0.2), 0.3, reason: 'шум double убран до 3 знаков');
    });

    test('подписи: «3 + 10 = 13 л», итог по единицам, пустая единица — «шт»', () {
      expect(receiveSumText(3, 10, 'л'), '3 + 10 = 13 л');
      expect(receiveSumText(3, 2.5, 'кг'), '3 + 2,5 = 5,5 кг');
      expect(qtyWithUnit(10, 'л'), '10 л');
      expect(qtyWithUnit(1, ''), '1 шт');
      expect(receiveTotalsText([(unit: 'л', qty: 10), (unit: 'шт', qty: 5), (unit: 'л', qty: 2.5)]), '12,5 л · 5 шт');
      expect(receiveAllPieces(['шт', '', null]), isTrue);
      expect(receiveAllPieces(['шт', 'л']), isFalse);
      expect(unitOrPcs('  '), 'шт');
      expect(unitOrPcs(' л '), 'л');
    });
  });

  group('накладная ИИ: дробное количество и единица не теряются', () {
    test('«2,5 кг» не округляется до 3, «0,5 л» не становится 1', () {
      final items = VisionService.parseSeen('[{"name":"Сахар","count":2.5,"unit":"кг"},{"name":"Молоко","count":"0,5","unit":"л"},{"name":"Вода","count":3}]');
      expect(items[0].amount, 2.5);
      expect(items[0].unit, 'кг');
      expect(items[1].amount, 0.5);
      expect(items[1].unit, 'л');
      expect(items[2].amount, 3);
      expect(items[2].qty, isNull, reason: 'целое — прежний путь через count');
      expect(items[2].unit, '');
    });

    test('новая строка камеры: единица «л» сохраняется, «кг» — весовой, по умолчанию «шт»', () {
      ReceiveLine mk(String unit) => camSyncLine(null, product: null, name: 'Масло', barcode: '', qty: 2.5, cost: 100, price: 130, unit: unit);
      expect(mk('л').unit, 'л');
      expect(mk('л').weight, isFalse);
      expect(mk('кг').weight, isTrue);
      expect(camSyncLine(null, product: null, name: 'Масло', barcode: '', qty: 1, cost: 1, price: 2).unit, 'шт');
      // сменили единицу у ещё не отправленной позиции — строка заменяется новой (а не остаётся со старой единицей)
      final old = mk('шт');
      final again = camSyncLine(old, product: null, name: 'Масло', barcode: '', qty: 2.5, cost: 100, price: 130, unit: 'л');
      expect(identical(old, again), isFalse);
      expect(again.unit, 'л');
    });

    test('товар из базы: единица строки — из карточки (литры), не «шт»', () {
      final line = camSyncLine(null, product: _p('p1', 'Масло', 3), name: 'Масло', barcode: '', qty: 10, cost: 0, price: 100);
      expect(line.unit, 'л');
      expect(line.before, 3);
    });
  });

  group('умная камера: товар в литрах', () {
    late AppDatabase db;
    late StockServer s;
    late FakeApi api;
    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      s = StockServer();
      api = FakeApi((m, p, b, q) => s.handle(m, p, b, q))..attach(_admin);
    });
    tearDown(() => db.close());

    Future<void> pump(WidgetTester tester, List<SeenMatch> matches) async {
      tester.view.physicalSize = const Size(900, 3000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api)],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            home: SmartCameraScreen(debugMatches: matches, debugMode: 'invoice'),
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

    testWidgets('на складе 3 л, принято 10 л: на экране «10 л» и «3 + 10 = 13 л», после загрузки на сервере 13', (tester) async {
      final id = s.add('Масло', qty: 3);
      await pump(tester, [
        SeenMatch(const SeenItem(name: 'Масло', count: 10), [(_p(id, 'Масло', 3), 1.0)]),
      ]);
      expect(find.text('10 л'), findsOneWidget, reason: 'количество с единицей карточки, не голое «10»');
      expect(find.text('3 + 10 = 13 л'), findsOneWidget);
      await tester.tap(find.byType(FilledButton));
      await settle(tester);
      expect(stockOf(s, id), 13);
    });

    testWidgets('дробное количество из накладной: 2,5 л прибавляется к 3 л → 5,5 л', (tester) async {
      final id = s.add('Масло', qty: 3);
      await pump(tester, [
        SeenMatch(const SeenItem(name: 'Масло', count: 3, qty: 2.5), [(_p(id, 'Масло', 3), 1.0)]),
      ]);
      expect(find.text('2,5 л'), findsOneWidget);
      expect(find.text('3 + 2,5 = 5,5 л'), findsOneWidget);
      await tester.tap(find.byType(FilledButton));
      await settle(tester);
      expect(stockOf(s, id), 5.5);
    });

    testWidgets('ввод дробного числа в литрах принимается, в штуках — нет', (tester) async {
      final a = s.add('Масло', qty: 3), b = s.add('Вода', qty: 3);
      await pump(tester, [
        SeenMatch(const SeenItem(name: 'Масло', count: 1), [(_p(a, 'Масло', 3), 1.0)]),
        SeenMatch(const SeenItem(name: 'Вода', count: 1), [(_p(b, 'Вода', 3, unit: 'шт'), 1.0)]),
      ]);
      await tester.tap(find.text('1 л'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), '10,5');
      await tester.tap(find.text('Готово'));
      await tester.pumpAndSettle();
      expect(find.text('10,5 л'), findsOneWidget);
      expect(find.text('3 + 10,5 = 13,5 л'), findsOneWidget);

      await tester.tap(find.text('1 шт'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), '2,5');
      await tester.tap(find.text('Готово'));
      await tester.pumpAndSettle();
      expect(find.text('1 шт'), findsOneWidget, reason: 'дробное «шт» отклонено, число прежнее');
    });
  });

  group('«Приёмка» со сканером: единица и прибавка', () {
    late AppDatabase db;
    late StockServer s;
    late FakeApi api;
    setUp(() {
      db = AppDatabase(NativeDatabase.memory());
      s = StockServer();
      api = FakeApi((m, p, b, q) async {
        // сервер с mass-incoming: прибавляет к актуальному остатку (как NurCRM)
        if (m == 'POST' && p == 'api/main/products/mass-incoming/') {
          for (final it in ((b as Map)['items'] as List).cast<Map>()) {
            final x = s.products[it['product_id']]!;
            x['quantity'] = (x['quantity'] as num).toDouble() + double.parse('${it['quantity']}');
          }
          return {'ok': true};
        }
        return s.handle(m, p, b, q);
      })..attach(_admin);
    });
    tearDown(() => db.close());

    testWidgets('скан литрового товара: «было 3 л», ввод 10 → «3 + 10 = 13 л», «Провести» → на сервере 13', (tester) async {
      final id = s.add('Масло', qty: 3, barcode: '4600000000017');
      await db.upsertProduct(ProductsCompanion.insert(id: id, name: 'Масло', barcode: const Value('4600000000017'), quantity: const Value(3), unit: const Value('л'), searchKey: const Value('масло')));
      tester.view.physicalSize = const Size(360, 2000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final router = GoRouter(
        routes: [
          GoRoute(path: '/', builder: (_, _) => const Scaffold()),
          GoRoute(path: '/r', builder: (_, _) => ReceiveScreen(debugScan: () async => ['4600000000017'])),
        ],
      );
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            sessionProvider.overrideWith(() => _Fixed(_admin)),
            globalProductsProvider.overrideWithValue(_NoGlobals()),
          ],
          child: MaterialApp.router(
            routerConfig: router,
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          ),
        ),
      );
      await tester.pump();
      unawaited(router.push('/r'));
      await tester.pumpAndSettle();
      Future<void> settle() async {
        for (var i = 0; i < 20; i++) {
          await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
          await tester.pump();
        }
      }

      await tester.tap(find.text('Сканировать товар'));
      await settle();
      expect(find.text('1 л'), findsOneWidget, reason: 'скан — +1 в единице карточки');
      expect(find.textContaining('было 3 л'), findsOneWidget);

      await tester.tap(find.text('1 л'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField), '10');
      await tester.tap(find.text('Готово'));
      await tester.pumpAndSettle();
      expect(find.text('10 л'), findsOneWidget);
      expect(find.textContaining('3 + 10 = 13 л'), findsOneWidget);
      expect(find.text('Провести приход · 10 л'), findsOneWidget, reason: 'итог в литрах, а не «Провести · 10 шт»');

      await tester.tap(find.textContaining('Провести'));
      await settle();
      expect(stockOf(s, id), 13);
    });

    test('StockRepository.postReceiving: дробное количество в литрах уходит как есть (3 + 2,5 = 5,5)', () async {
      final id = s.add('Масло', qty: 3);
      final repo = StockRepository(api, db);
      await repo.postReceiving([StockLine(productId: id, name: 'Масло', before: 3, qty: 2.5, unit: 'л')]);
      expect(stockOf(s, id), 5.5);
    });
  });

  group('приход администратора: единица в строке', () {
    testWidgets('строка в литрах: «10 л», «На складе: 3 л → 3 + 10 = 13 л»; дробное ввод принят, в «шт» нет', (tester) async {
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);
      final s = StockServer();
      final api = FakeApi((m, p, b, q) => s.handle(m, p, b, q))..attach(_admin);
      tester.view.physicalSize = const Size(900, 3000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), globalProductsProvider.overrideWithValue(_NoGlobals())],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            home: AdminReceiveScreen(
              initialLines: [
                ReceiveLine(productId: 'p1', name: 'Масло', unit: 'л', before: 3, qty: 10, cost: 50, price: 70),
                ReceiveLine(productId: 'p2', name: 'Вода', unit: 'шт', before: 3, qty: 2, cost: 50, price: 70),
              ],
            ),
          ),
        ),
      );
      await tester.pump();
      expect(find.text('10 л'), findsOneWidget);
      expect(find.text('На складе: 3 л → 3 + 10 = 13 л'), findsOneWidget);
      expect(find.text('2 шт'), findsOneWidget);

      await tester.tap(find.text('10 л'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField).last, '2,5');
      await tester.tap(find.text('Готово'));
      await tester.pumpAndSettle();
      expect(find.text('2,5 л'), findsOneWidget);

      await tester.tap(find.text('2 шт'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextField).last, '1,5');
      await tester.tap(find.text('Готово'));
      await tester.pumpAndSettle();
      expect(find.text('2 шт'), findsOneWidget, reason: 'дробное «шт» отклонено');
    });
  });
}
