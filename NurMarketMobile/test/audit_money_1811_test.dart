// 2026-10-30 (1.18.11): ревизия в деньгах, как на кассе — закупка товара в базе телефона (миграция 6 → 7), загрузка закупки из каталога
// и ленты (без затирания), расчёт итогов (недостача / излишек / по продаже / без закупки / топ-10), закупка и суммы только
// владельцу и администратору. Сервер не трогается: фейковый API и база в памяти.
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/stock/audit_money.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
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
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    ifNoneMatch = null;
    return handler(path, query);
  }
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Бакыт', role: 'owner', isOwner: true, cashboxId: 'b', cashboxName: 'Касса 1');
const _admin = Session(email: 'a@x.kg', access: 'a', refresh: 'r', userId: 'u3', userName: 'Админ', role: 'Администратор', cashboxId: 'b');
const _cashier = Session(email: 'c@x.kg', access: 'a', refresh: 'r', userId: 'u4', userName: 'Айгуль', role: 'cashier', cashboxId: 'b');

AuditLine _line(String id, double expected, double counted, double sale, double? buy) =>
    AuditLine(id: id, name: id, expected: expected, counted: counted, salePrice: sale, purchase: buy);

void main() {
  // ------------------------------------------------------------------ база: миграция
  group('миграция базы 6 → 7: закупка товара', () {
    test('база версии 6 открывается новой схемой: данные целы, закупка null, потом сохраняется', () async {
      final db = AppDatabase(
        NativeDatabase.memory(
          setup: (raw) {
            raw.execute('''CREATE TABLE products (
              id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, price REAL NOT NULL DEFAULT 0, wholesale_price REAL NOT NULL DEFAULT 0,
              barcode TEXT NULL, alt_barcodes TEXT NOT NULL DEFAULT '', sku TEXT NULL, plu TEXT NULL, unit TEXT NOT NULL DEFAULT 'шт',
              is_weight INTEGER NOT NULL DEFAULT 0, is_service INTEGER NOT NULL DEFAULT 0, is_favorite INTEGER NOT NULL DEFAULT 0,
              quantity REAL NOT NULL DEFAULT 0, category TEXT NOT NULL DEFAULT '', image_url TEXT NULL,
              search_key TEXT NOT NULL DEFAULT '', promo_rules TEXT NOT NULL DEFAULT '', min_quantity REAL NOT NULL DEFAULT 0)''');
            raw.execute('CREATE TABLE outbox (id TEXT NOT NULL PRIMARY KEY, kind TEXT NOT NULL, body TEXT NOT NULL, created_at INTEGER NOT NULL, '
                "status TEXT NOT NULL DEFAULT 'pending', attempts INTEGER NOT NULL DEFAULT 0, last_error TEXT NULL, total REAL NOT NULL DEFAULT 0, "
                "summary TEXT NOT NULL DEFAULT '', payment_method TEXT NOT NULL DEFAULT '', server_id TEXT NULL, server_number TEXT NULL, "
                "shift_id TEXT NULL, display TEXT NOT NULL DEFAULT '[]')");
            raw.execute('CREATE TABLE settings (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL)');
            raw.execute("INSERT INTO settings (key, value) VALUES ('shop.name', 'Мой магазин')");
            raw.execute("INSERT INTO products (id, name, price, quantity, min_quantity, barcode) VALUES ('old1', 'Старый товар', 12.5, 4, 3, '4600000000001')");
            raw.execute('PRAGMA user_version = 6');
          },
        ),
      );
      final p = await db.productById('old1');
      expect(p, isNotNull);
      expect(p!.name, 'Старый товар');
      expect(p.price, 12.5);
      expect(p.quantity, 4);
      expect(p.minQuantity, 3);
      expect(p.barcode, '4600000000001');
      expect(p.purchasePrice, isNull, reason: 'закупка неизвестна до ближайшей загрузки каталога');
      expect(await db.getSetting('shop.name'), 'Мой магазин');
      await db.setStock('old1', 9);
      await (db.update(db.products)..where((x) => x.id.equals('old1'))).write(const ProductsCompanion(purchasePrice: Value(7.5)));
      expect((await db.productById('old1'))!.purchasePrice, 7.5);
      expect((await db.productById('old1'))!.quantity, 9);
      expect(db.schemaVersion, 7);
      await db.close();
    });

    test('новая база: колонка есть, по умолчанию null', () async {
      final db = AppDatabase(NativeDatabase.memory());
      await db.upsertProduct(ProductsCompanion.insert(id: 'n1', name: 'Новый'));
      expect((await db.productById('n1'))!.purchasePrice, isNull);
      await db.close();
    });
  });

  // ------------------------------------------------------------------ каталог и лента
  group('закупка из каталога и ленты (урок Б-1: нет поля — не затираем)', () {
    late AppDatabase db;
    late List<Map<String, dynamic>> list;
    late Future<dynamic> Function(Map<String, dynamic>? q) feed;
    late _FakeApi api;
    var version = 1;

    setUp(() {
      version = 1;
      db = AppDatabase(NativeDatabase.memory());
      list = [
        {'id': 'p1', 'name': 'Вода', 'price': 100, 'quantity': 5, 'purchase_price': '60.00'},
        {'id': 'p2', 'name': 'Хлеб', 'price': 50, 'quantity': 5},
      ];
      feed = (q) async => {'cursor': 'c1', 'changes': [], 'deleted': [], 'has_more': false};
      api = _FakeApi((path, q) async {
        if (path == 'api/main/products/list/') return {'results': list, 'next': null};
        if (path == 'api/main/products/catalog-version/') return {'catalog_version': version};
        if (path == 'api/main/products/changes/') return feed(q);
        throw ApiException('нет', status: 404);
      });
    });
    tearDown(() => db.close());

    test('mapProduct: purchase_price попадает в базу, нет поля или null — не трогаем', () {
      expect(CatalogRepository.mapProduct({'id': 'x', 'name': 'Х', 'purchase_price': '12.50'})!.purchasePrice.value, 12.5);
      expect(CatalogRepository.mapProduct({'id': 'y', 'name': 'У'})!.purchasePrice.present, isFalse);
      expect(CatalogRepository.mapProduct({'id': 'z', 'name': 'З', 'purchase_price': null})!.purchasePrice.present, isFalse);
    });

    test('полная загрузка записывает закупку; следующая полная загрузка без поля её не стирает', () async {
      final repo = CatalogRepository(api, db);
      await repo.sync();
      expect((await db.productById('p1'))!.purchasePrice, 60);
      expect((await db.productById('p2'))!.purchasePrice, isNull);
      // сервер (например, для кассира) закупку не прислал — прежняя остаётся
      list = [
        {'id': 'p1', 'name': 'Вода', 'price': 110, 'quantity': 5},
        {'id': 'p2', 'name': 'Хлеб', 'price': 50, 'quantity': 5},
      ];
      await repo.sync(full: true);
      final p1 = (await db.productById('p1'))!;
      expect(p1.price, 110, reason: 'остальное обновилось');
      expect(p1.purchasePrice, 60, reason: 'закупки в списке нет — прежняя не затёрта');
      // закупка пришла новая — обновилась
      list = [
        {'id': 'p1', 'name': 'Вода', 'price': 110, 'quantity': 5, 'purchase_price': '65.00'},
        {'id': 'p2', 'name': 'Хлеб', 'price': 50, 'quantity': 5},
      ];
      await repo.sync(full: true);
      expect((await db.productById('p1'))!.purchasePrice, 65);
    });

    test('лента: карточка без закупки и запись без названия её не затирают; присланная закупка применяется', () async {
      final repo = CatalogRepository(api, db);
      await repo.sync();
      feed = (q) async => {
        'cursor': 'c2',
        'changes': [
          {'id': 'p1', 'name': 'Вода', 'price': 105}, // сокращённая карточка без закупки
          {'id': 'p2', 'price': 55, 'purchase_price': '30.00'}, // без названия, закупка пришла
        ],
        'deleted': [],
        'has_more': false,
      };
      await repo.sync();
      expect((await db.productById('p1'))!.purchasePrice, 60, reason: 'карточка без закупки не затирает');
      expect((await db.productById('p1'))!.price, 105);
      expect((await db.productById('p2'))!.purchasePrice, 30);
      feed = (q) async => {
        'cursor': 'c3',
        'changes': [
          {'id': 'p2', 'quantity': 3}, // только остаток
        ],
        'deleted': [],
        'has_more': false,
      };
      await repo.sync();
      expect((await db.productById('p2'))!.purchasePrice, 30, reason: 'запись без закупки её не трогает');
      expect((await db.productById('p2'))!.quantity, 3);
    });
  });

  // ------------------------------------------------------------------ расчёт итогов
  group('итоги ревизии в деньгах', () {
    // A: учёт 10, факт 7, продажа 100, закупка 60 → −3 шт: −180 по закупке, −300 по продаже
    // B: учёт 5, факт 3, продажа 50, закупки нет → −2 шт: по закупке 0, по продаже −100 (помечается «*»)
    // C: учёт 2, факт 4, продажа 30, закупка 10 → +2 шт: +20 по закупке, +60 по продаже
    // D: учёт 8, факт 8 — совпало
    final lines = [_line('A', 10, 7, 100, 60), _line('B', 5, 3, 50, null), _line('C', 2, 4, 30, 10), _line('D', 8, 8, 20, 5)];

    test('недостача, излишек, по закупке и по продаже, чистый результат, товары без закупки', () {
      final t = AuditTotals.of(lines);
      expect(t.counted, 4);
      expect(t.withDifference, 3);
      expect(t.shortageQty, 5);
      expect(t.shortageAtCost, 180);
      expect(t.shortageAtSale, 400);
      expect(t.surplusQty, 2);
      expect(t.surplusAtCost, 20);
      expect(t.surplusAtSale, 60);
      expect(t.netAtCost, -160);
      expect(t.netAtSale, -340);
      expect(t.noCostCount, 1);
      expect(t.shortageNoCostAtSale, 100);
      expect(t.surplusNoCostAtSale, 0);
    });

    test('сумма строки: по закупке; закупки нет — по цене продажи с пометкой; ни того ни другого — нет суммы', () {
      expect(lines[0].money, (amount: -180.0, fallback: false));
      expect(lines[1].money, (amount: -100.0, fallback: true));
      expect(lines[2].money, (amount: 20.0, fallback: false));
      expect(lines[3].money, (amount: 0.0, fallback: false));
      expect(_line('E', 3, 1, 0, null).money, isNull);
      expect(_line('F', 3, 1, 10, 0).hasPurchase, isFalse, reason: 'закупка 0 — как её отсутствие');
      expect(auditAmountText(lines[0], 'сом'), '−180 сом');
      expect(auditAmountText(lines[1], 'сом'), '−100 сом*');
      expect(auditAmountText(lines[2], 'сом'), '+20 сом');
    });

    test('топ-10 недостач: сначала самые дорогие, по закупке; без закупки — по цене продажи; излишки и совпадения не входят', () {
      final many = [
        for (var i = 1; i <= 12; i++) _line('P$i', 10, 10.0 - i, 10, 1.0 * i), // недостача i шт × закупка i → −i²
        _line('S', 1, 5, 99, 99), // излишек
        _line('OK', 3, 3, 10, 10),
        _line('NoBuy', 9, 0, 40, null), // −9 × 40 = −360 по продаже
      ];
      final top = AuditTotals.topShortages(many);
      expect(top.length, 10);
      expect(top.first.id, 'NoBuy'); // −360 (по продаже) глубже P12 (−144)
      expect(top[1].id, 'P12');
      expect(top.map((r) => r.id), isNot(contains('S')));
      expect(top.map((r) => r.id), isNot(contains('OK')));
      expect(top.map((r) => r.id), isNot(contains('P1'))); // самые мелкие в десятку не вошли
      expect(AuditTotals.topShortages([_line('S', 1, 5, 9, 9)]), isEmpty);
    });

    test('нет расхождений — итоги нулевые', () {
      final t = AuditTotals.of([_line('D', 8, 8, 20, 5)]);
      expect(t.withDifference, 0);
      expect(t.netAtCost, 0);
      expect(t.noCostCount, 0);
    });
  });

  // ------------------------------------------------------------------ права
  group('закупка и суммы только тем, кому они доступны', () {
    test('владелец, администратор, «Настройки» — видят; кассир — нет', () {
      expect(_owner.canSeePurchase, isTrue);
      expect(_admin.canSeePurchase, isTrue);
      expect(_cashier.canSeePurchase, isFalse);
      expect(const Session(email: 'x', access: 'a', refresh: 'r', role: 'cashier', perms: [Session.viewProducts]).canSeePurchase, isFalse);
      expect(const Session(email: 'x', access: 'a', refresh: 'r', role: 'cashier', perms: [Session.viewProducts, Session.viewSettings]).canSeePurchase, isTrue);
    });
  });

  // ------------------------------------------------------------------ экран
  group('экран ревизии', () {
    late AppDatabase db;

    Widget app(Session s, {String lang = 'ru', bool dark = false, List<Override> extra = const []}) => ProviderScope(
      overrides: [databaseProvider.overrideWithValue(db), sessionProvider.overrideWith(() => _Fixed(s)), ...extra],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        darkTheme: buildTheme(dark: true),
        themeMode: dark ? ThemeMode.dark : ThemeMode.light,
        locale: Locale(lang),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: const AuditScreen(),
      ),
    );

    setUp(() async {
      db = AppDatabase(NativeDatabase.memory());
      Future<void> add(String id, String name, double qty, double price, double? buy) => db.upsertProduct(
        ProductsCompanion.insert(
          id: id,
          name: name,
          quantity: Value(qty),
          price: Value(price),
          purchasePrice: Value(buy),
          searchKey: Value(name.toLowerCase()),
        ),
      );
      await add('A', 'Альфа', 10, 100, 60);
      await add('B', 'Браво', 5, 50, null);
      await add('C', 'Чарли', 2, 30, 10);
    });
    tearDown(() => db.close());

    Future<void> startAndCount(WidgetTester t, Session s, {String lang = 'ru', bool dark = false, Size size = const Size(412, 915), double scale = 1.0}) async {
      t.view.physicalSize = size * 2;
      t.view.devicePixelRatio = 2;
      t.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(t.view.reset);
      addTearDown(t.platformDispatcher.clearTextScaleFactorTestValue);
      await t.pumpWidget(app(s, lang: lang, dark: dark));
      await t.pumpAndSettle();
      await t.tap(find.text(lx(lang).allStock));
      await t.pumpAndSettle();
      // строки списка строятся лениво — на низком экране последняя за краем: ищем поле по строке, прокручивая список
      for (final (name, count) in [('Альфа', '7'), ('Браво', '3'), ('Чарли', '4')]) {
        await t.scrollUntilVisible(find.text(name), 60, scrollable: find.byWidgetPredicate((w) => w is Scrollable && w.axis == Axis.vertical).first);
        await t.pump();
        final row = find.ancestor(of: find.text(name), matching: find.byType(Row)).first;
        await t.enterText(find.descendant(of: row, matching: find.byType(TextField)), count);
        await t.pump();
      }
    }

    testWidgets('владелец: на карточках закупка и сумма разницы, внизу итоги', (t) async {
      await startAndCount(t, _owner);
      final l = lx('ru');
      expect(find.byKey(const ValueKey('rev-money-A')), findsOneWidget);
      expect(find.textContaining('${l.revCostLine('60')} · ${l.revDiffAmount('−180 сом')}', findRichText: true), findsOneWidget);
      expect(find.textContaining('${l.revNoCost} · ${l.revDiffAmount('−100 сом*')}', findRichText: true), findsOneWidget);
      expect(find.textContaining(l.revDiffAmount('+20 сом'), findRichText: true), findsOneWidget);
      expect(find.text(l.revShortageLine('5', '180', '400')), findsOneWidget);
      expect(find.text(l.revSurplusLine('2', '20', '60')), findsOneWidget);
      expect(find.text(l.revNetLine('−160')), findsOneWidget);
      expect(find.text(l.revNoCostWarn(1)), findsOneWidget);
      expect(find.text(l.revStarNote), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    testWidgets('администратор (по роли) видит то же', (t) async {
      await startAndCount(t, _admin);
      expect(find.byKey(const ValueKey('rev-shortage')), findsOneWidget);
      expect(find.byKey(const ValueKey('rev-money-A')), findsOneWidget);
    });

    testWidgets('кассир: ни закупки, ни сумм — экран как раньше', (t) async {
      await startAndCount(t, _cashier);
      final l = lx('ru');
      expect(find.byKey(const ValueKey('rev-money-A')), findsNothing);
      expect(find.byKey(const ValueKey('rev-shortage')), findsNothing);
      expect(find.byKey(const ValueKey('rev-net')), findsNothing);
      expect(find.textContaining(l.revNoCost, findRichText: true), findsNothing);
      expect(find.textContaining('сом', findRichText: true), findsNothing, reason: 'кассиру денег по расхождению не показываем');
      // окно завершения кассира — прежнее, без денег и списка
      await t.tap(find.widgetWithText(FilledButton, l.auditApply));
      await t.pumpAndSettle();
      expect(find.text(l.auditApplyAsk(3, 3)), findsOneWidget);
      expect(find.byKey(const ValueKey('rev-list')), findsNothing);
      expect(find.byKey(const ValueKey('rev-net')), findsNothing);
      await t.tap(find.text(l.cancel));
      await t.pumpAndSettle();
    });

    testWidgets('окно завершения владельца: деньги, топ недостач со звёздочкой, переключение на все расхождения', (t) async {
      await startAndCount(t, _owner);
      final l = lx('ru');
      await t.tap(find.widgetWithText(FilledButton, l.auditApply));
      await t.pumpAndSettle();
      expect(find.text(l.auditApplyAsk(3, 3)), findsOneWidget);
      // в окне свои итоги (ещё и под ним на экране) — берём те, что внутри диалога
      final dialog = find.byType(AlertDialog);
      expect(find.descendant(of: dialog, matching: find.byKey(const ValueKey('rev-net'))), findsOneWidget);
      expect(find.descendant(of: dialog, matching: find.byKey(const ValueKey('rev-nocost-tip'))), findsOneWidget);
      expect(find.descendant(of: dialog, matching: find.text(l.revTopShortages(2))), findsOneWidget);
      expect(find.descendant(of: dialog, matching: find.text('−180 сом')), findsOneWidget);
      expect(find.descendant(of: dialog, matching: find.text('−100 сом*')), findsOneWidget);
      expect(find.descendant(of: dialog, matching: find.text('+20 сом')), findsNothing, reason: 'в топе только недостачи');
      expect(find.descendant(of: dialog, matching: find.byKey(const ValueKey('rev-star'))), findsOneWidget);
      await t.tap(find.byKey(const ValueKey('rev-all')));
      await t.pumpAndSettle();
      expect(find.descendant(of: dialog, matching: find.text('+20 сом')), findsOneWidget);
      await t.tap(find.text(l.cancel));
      await t.pumpAndSettle();
      expect(t.takeException(), isNull);
    });

    for (final cfg in [('ky', const Size(360, 640), 1.3, false), ('uz', const Size(360, 640), 1.3, true), ('tr', const Size(360, 800), 1.0, false), ('en', const Size(412, 915), 1.0, true)]) {
      testWidgets('${cfg.$1}, ${cfg.$2.width.toInt()}×${cfg.$2.height.toInt()}, шрифт ${cfg.$3}${cfg.$4 ? ', тёмная' : ''}: без переполнений, итоги и окно на месте', (t) async {
        await startAndCount(t, _owner, lang: cfg.$1, dark: cfg.$4, size: cfg.$2, scale: cfg.$3);
        final l = lx(cfg.$1);
        expect(t.takeException(), isNull);
        expect(find.text(l.revNetLine('−160')), findsOneWidget);
        await t.tap(find.widgetWithText(FilledButton, l.auditApply));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
        expect(find.byKey(const ValueKey('rev-list')), findsOneWidget);
        await t.tap(find.text(l.cancel));
        await t.pumpAndSettle();
        expect(t.takeException(), isNull);
      });
    }
  });
}

AppLocalizations lx(String lang) => lookupAppLocalizations(Locale(lang));
