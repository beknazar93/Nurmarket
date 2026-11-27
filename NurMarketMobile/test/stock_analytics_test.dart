// 2026-11-27 (владелец: «в мобилке аналитика где? Аналитика по складу?»): экран «Аналитика склада». Проверено на подставных данных (сеть и
// боевая база не трогаются): расчёт итогов, пустой склад, обычный склад, нет связи («Остатки на …»), сотрудник без права видеть закупку
// (нет закупки, наценки и поступлений), плитки открывают «Склад» с фильтром, фильтры «Склада» считают то же, что плитки, 5 языков на 360 dp.
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/admin/stock_analytics.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'o1', userName: 'Бакыт', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b');
// сотрудник с аналитикой, но без «Настроек»: закупку не видит
const _analyst = Session(email: 'k@x.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айбек', role: 'cashier', companyName: 'Нур', cashboxId: 'b', perms: [Session.viewAnalytics, Session.viewProducts]);

class _Api extends ApiClient {
  _Api() : super(SessionStore());
}

/// Данные экрана подменены: товары — из базы в памяти (настоящее чтение), сеть — по флагам.
class _Repo extends StockAnalyticsRepository {
  _Repo(AppDatabase db, {this.offline = false, this.receipts = const []}) : super(_Api(), db, CatalogRepository(_Api(), db));
  final bool offline;
  final List<RecentReceipt> receipts;
  int refreshes = 0;
  @override
  Future<void> refreshCatalog() async {
    refreshes++;
    if (offline) throw ApiException('Нет связи с сервером.');
  }

  @override
  Future<List<RecentReceipt>> recentReceipts({int count = 7}) async {
    if (offline) throw ApiException('Нет связи с сервером.');
    return receipts;
  }
}

void _phone(WidgetTester t, {double scale = 1.0}) {
  t.view.physicalSize = const Size(720, 1480);
  t.view.devicePixelRatio = 2.0;
  t.platformDispatcher.textScaleFactorTestValue = scale;
  t.platformDispatcher.accessibilityFeaturesTestValue = const FakeAccessibilityFeatures(disableAnimations: true);
  final prev = FlutterError.onError;
  FlutterError.onError = (d) {
    if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return; // ky нет в Material
    prev?.call(d);
  };
  addTearDown(() {
    FlutterError.onError = prev;
    t.view.resetPhysicalSize();
    t.view.resetDevicePixelRatio();
    t.platformDispatcher.clearTextScaleFactorTestValue();
    t.platformDispatcher.clearAccessibilityFeaturesTestValue();
  });
}

Future<void> _seed(AppDatabase db) => db.replaceProducts([
  // обычный товар с закупкой: 10 шт × 100 = 1000 по продаже, закупка 10 × 60 = 600
  ProductsCompanion.insert(id: 'a', name: 'Чай Ахмад', price: const Value(100), purchasePrice: const Value(60), quantity: const Value(10), category: const Value('Чай'), barcode: const Value('4700000000011'), searchKey: const Value('чай ахмад')),
  // заканчивается (< 10), без закупки
  ProductsCompanion.insert(id: 'b', name: 'Сахар', price: const Value(80), quantity: const Value(4), category: const Value('Бакалея'), barcode: const Value('4700000000028'), searchKey: const Value('сахар')),
  // нет в наличии, без цены
  ProductsCompanion.insert(id: 'c', name: 'Соль', price: const Value(0), quantity: const Value(0), category: const Value('Бакалея'), barcode: const Value('4700000000035'), searchKey: const Value('соль')),
  // в минусе, без штрихкода
  ProductsCompanion.insert(id: 'd', name: 'Мука', price: const Value(50), quantity: const Value(-3), category: const Value(''), searchKey: const Value('мука')),
  // весовой с PLU: штрихкод не нужен, в «единицы» не входит
  ProductsCompanion.insert(id: 'e', name: 'Яблоки', price: const Value(120), purchasePrice: const Value(70), quantity: const Value(25.5), isWeight: const Value(true), unit: const Value('кг'), plu: const Value('15'), category: const Value('Фрукты'), searchKey: const Value('яблоки')),
  // услуга в склад не входит
  ProductsCompanion.insert(id: 's', name: 'Доставка', price: const Value(100), quantity: const Value(0), isService: const Value(true), searchKey: const Value('доставка')),
]);

void main() {
  late AppDatabase db;

  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  group('расчёт', () {
    test('итоги по образцовым данным: позиции, единицы, стоимость, наценка, нет / заканчивается / минус, без цены и кода', () async {
      await _seed(db);
      final s = StockStats.of(await _Repo(db).rows());
      expect(s.positions, 5, reason: 'услуга не считается');
      expect(s.units, 14, reason: '10 + 4; весовые и минус не входят');
      expect(s.retailValue, 10 * 100 + 4 * 80 + 25.5 * 120);
      expect(s.purchaseValue, 10 * 60 + 25.5 * 70);
      expect(s.purchaseMissing, 1, reason: 'у «Сахара» закупки нет');
      expect(s.markup, 10 * 40 + 25.5 * 50);
      expect(s.markupPercent, closeTo((10 * 40 + 25.5 * 50) / (10 * 60 + 25.5 * 70) * 100, 1e-9));
      expect([s.zero, s.low, s.minus, s.noPrice, s.noCode], [1, 1, 1, 1, 1]);
      expect(s.categories.first, ('Фрукты', 25.5 * 120));
      expect(s.top.map((x) => x.$1).toList(), ['Яблоки', 'Чай Ахмад', 'Сахар']);
    });

    test('без закупок наценки нет; пустой склад — нули', () {
      final s = StockStats.of(const [StockRow(name: 'x', price: 10, quantity: 2, barcode: '1')]);
      expect(s.markup, isNull);
      expect(s.hasPurchase, isFalse);
      final e = StockStats.of(const []);
      expect([e.positions, e.units, e.retailValue, e.zero, e.minus], [0, 0, 0, 0, 0]);
    });

    test('топ — не больше 10, по убыванию', () {
      final s = StockStats.of([for (var i = 1; i <= 15; i++) StockRow(name: 'T$i', price: i.toDouble(), quantity: 1, barcode: '$i')]);
      expect(s.top.length, 10);
      expect(s.top.first.$1, 'T15');
    });

    test('приход сервера: номер, поставщик, сумма; пустой ответ не падает', () {
      final r = RecentReceipt.parse({
        'id': 'abcdef123456',
        'number': 'П-7',
        'supplier': {'full_name': 'ОсОО Ала-Тоо'},
        'created_at': '2026-11-20T10:00:00+06:00',
        'items': [
          {'qty': 3, 'purchase_price': 50},
          {'quantity': 2, 'purchase_price': 10},
        ],
      })!;
      expect([r.number, r.supplier, r.qty, r.sum], ['П-7', 'ОсОО Ала-Тоо', 5, 170]);
      expect(r.at, isNotNull);
      expect(RecentReceipt.parse({}), isNull);
    });
  });

  Widget app(Widget home, List<Override> o, {String lang = 'ru', GoRouter? router}) {
    final delegates = const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate];
    return ProviderScope(
      overrides: o,
      child: router != null
          ? MaterialApp.router(routerConfig: router, theme: buildTheme(dark: false), locale: Locale(lang), supportedLocales: AppLocalizations.supportedLocales, localizationsDelegates: delegates)
          : MaterialApp(theme: buildTheme(dark: false), locale: Locale(lang), supportedLocales: AppLocalizations.supportedLocales, localizationsDelegates: delegates, home: home),
    );
  }

  List<Override> over(Session s, _Repo r) => [databaseProvider.overrideWithValue(db), sessionProvider.overrideWith(() => _Fixed(s)), stockAnalyticsRepoProvider.overrideWithValue(r)];

  Future<void> settle(WidgetTester t) async {
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 150)));
    await t.pump();
    await t.pump(const Duration(milliseconds: 300));
  }

  testWidgets('пустой склад: понятная надпись, без ошибок', (t) async {
    _phone(t);
    await t.pumpWidget(app(const StockAnalyticsScreen(), over(_owner, _Repo(db))));
    await settle(t);
    final l = AppLocalizations.of(t.element(find.byType(StockAnalyticsScreen)))!;
    expect(find.text(l.saEmpty), findsOneWidget);
    expect(find.text(l.saEmptyHint), findsOneWidget);
    expect(t.takeException(), isNull);
  });

  testWidgets('владелец: плитки с числами, закупка и наценка, предупреждение о товарах без закупки, поступления', (t) async {
    await _seed(db);
    _phone(t);
    final repo = _Repo(db, receipts: [RecentReceipt(number: 'П-7', supplier: 'ОсОО Ала-Тоо', at: DateTime(2026, 11, 20), qty: 5, sum: 170)]);
    await t.pumpWidget(app(const StockAnalyticsScreen(), over(_owner, repo)));
    await settle(t);
    final l = AppLocalizations.of(t.element(find.byType(StockAnalyticsScreen)))!;
    expect(find.text(l.saPositions), findsOneWidget);
    expect(find.text('5'), findsOneWidget);
    expect(find.text(l.saUnits), findsOneWidget);
    expect(find.text(l.saRetail), findsOneWidget);
    expect(find.text(l.saPurchase), findsOneWidget);
    expect(find.text(l.saMarkup), findsOneWidget);
    expect(find.text(l.saPurchaseMissing(1)), findsOneWidget);
    for (final k in [l.saZero, l.saLow, l.saMinus, l.saNoPrice, l.saNoCode]) {
      await t.scrollUntilVisible(find.text(k), 200, scrollable: find.byType(Scrollable).first);
      expect(find.text(k), findsOneWidget, reason: k);
    }
    await t.scrollUntilVisible(find.text(l.saRecent), 300, scrollable: find.byType(Scrollable).first);
    expect(find.textContaining('П-7'), findsOneWidget);
    expect(repo.refreshes, 1, reason: 'при открытии каталог обновляется с сервера');
    expect(find.text(l.linkOffline), findsNothing);
    expect(t.takeException(), isNull);
  });

  testWidgets('нет связи: цифры телефона остаются, плашка «Нет связи», поступления — «когда появится связь»', (t) async {
    await _seed(db);
    await db.setSetting('catalog.checkedAt', DateTime(2026, 11, 27, 9, 30).toIso8601String());
    _phone(t);
    await t.pumpWidget(app(const StockAnalyticsScreen(), over(_owner, _Repo(db, offline: true))));
    await settle(t);
    final l = AppLocalizations.of(t.element(find.byType(StockAnalyticsScreen)))!;
    expect(find.text(l.linkOffline), findsOneWidget);
    expect(find.textContaining(l.stockAsOf('').trim()), findsWidgets);
    expect(find.text(l.saPositions), findsOneWidget);
    expect(find.text('5'), findsOneWidget);
    await t.scrollUntilVisible(find.text(l.saRecentNoLink), 400, scrollable: find.byType(Scrollable).first);
    expect(find.text(l.saRecentNoLink), findsOneWidget);
    expect(t.takeException(), isNull);
  });

  testWidgets('без права видеть закупку: нет закупки, наценки, предупреждения и поступлений; цена продажи есть', (t) async {
    await _seed(db);
    _phone(t);
    await t.pumpWidget(app(const StockAnalyticsScreen(), over(_analyst, _Repo(db, receipts: [const RecentReceipt(number: 'X', supplier: 'Y', sum: 5)]))));
    await settle(t);
    final l = AppLocalizations.of(t.element(find.byType(StockAnalyticsScreen)))!;
    expect(find.text(l.saRetail), findsOneWidget);
    expect(find.text(l.saPurchase), findsNothing);
    expect(find.text(l.saMarkup), findsNothing);
    expect(find.text(l.saPurchaseMissing(1)), findsNothing);
    await t.drag(find.byType(Scrollable).first, const Offset(0, -3000));
    await t.pump();
    expect(find.text(l.saRecent), findsNothing);
    expect(t.takeException(), isNull);
  });

  testWidgets('плитка «В минусе» открывает «Склад» с фильтром minus', (t) async {
    await _seed(db);
    _phone(t);
    final router = GoRouter(
      routes: [
        GoRoute(path: '/', builder: (_, _) => const StockAnalyticsScreen()),
        GoRoute(path: '/stock', builder: (_, s) => Scaffold(body: Text('СКЛАД ${s.uri.queryParameters['filter']}'))),
      ],
    );
    await t.pumpWidget(app(const SizedBox(), over(_owner, _Repo(db)), router: router));
    await settle(t);
    final l = AppLocalizations.of(t.element(find.byType(StockAnalyticsScreen)))!;
    await t.scrollUntilVisible(find.text(l.saMinus), 200, scrollable: find.byType(Scrollable).first);
    await t.tap(find.text(l.saMinus));
    await t.pumpAndSettle();
    expect(find.text('СКЛАД minus'), findsOneWidget);
  });

  group('«Склад» с фильтрами из аналитики показывает ровно те товары, что посчитаны на плитках', () {
    Future<List<String>> names(WidgetTester t, String filter) async {
      _phone(t);
      await t.pumpWidget(app(Scaffold(body: StockScreen(filter: filter)), [databaseProvider.overrideWithValue(db), sessionProvider.overrideWith(() => _Fixed(_owner)), apiProvider.overrideWithValue(_Api())]));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 150)));
      await t.pump();
      await t.pump(const Duration(milliseconds: 300));
      return [
        for (final n in ['Чай Ахмад', 'Сахар', 'Соль', 'Мука', 'Яблоки', 'Доставка'])
          if (find.text(n).evaluate().isNotEmpty) n,
      ];
    }

    for (final (filter, want) in [('zero', ['Соль']), ('minus', ['Мука']), ('noprice', ['Соль']), ('nocode', ['Мука'])]) {
      testWidgets(filter, (t) async {
        await _seed(db);
        expect(await names(t, filter), want);
      });
    }
  });

  testWidgets('5 языков на 360 dp при шрифте 1,3: без переполнений', (t) async {
    await _seed(db);
    for (final lang in ['ru', 'ky', 'en', 'tr', 'uz']) {
      _phone(t, scale: 1.3);
      t.view.physicalSize = const Size(720, 1480);
      await t.pumpWidget(app(const StockAnalyticsScreen(), over(_owner, _Repo(db, receipts: [RecentReceipt(number: 'П-7', supplier: 'ОсОО Ала-Тоо', at: DateTime(2026, 11, 20), sum: 170)])), lang: lang));
      await settle(t);
      await t.drag(find.byType(Scrollable).first, const Offset(0, -2500));
      await t.pump();
      expect(t.takeException(), isNull, reason: lang);
      await t.pumpWidget(const SizedBox());
    }
  });
}
