// 2026-10-30 (видеоуроки мобилки, первая волна): кадры экранов NurCRM Mobile на телефоне 360x780 dp, плотность 3 (1080x2340 px),
// светлая тема, ru. Настоящие экраны приложения на фейках: база в памяти, фейковый сервер, без сети, камеры, микрофона и боевой базы.
// Данные выдуманные (магазин «Нур»). Рядом с каждым PNG — JSON с прямоугольниками нужных элементов (для подсветки касаний).
// Без --dart-define=SNAP_DIR=<папка> тест пропускается, в репозиторий ничего не пишется.
// Запуск: flutter test test/lesson_frames_test.dart --dart-define=SNAP_DIR=<папка> [--plain-name "M01"] --concurrency=2
import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:drift/drift.dart' show Value, driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
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
import 'package:nurmarket/features/auth/login_screen.dart';
import 'package:nurmarket/features/home/home_screen.dart';
import 'package:nurmarket/features/home/shell.dart';
import 'package:nurmarket/features/payment/payment_screen.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/sale_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/features/staff/staff_providers.dart' show myTasksProvider;
import 'package:nurmarket/core/catalog/global_products.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/features/admin/new_item.dart';
import 'package:nurmarket/features/stock/product_batches.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/features/ops/ops_screens.dart';
import 'package:nurmarket/features/shift/close_shift_screen.dart';
import 'package:nurmarket/features/admin/admin_screens.dart';
import 'package:nurmarket/features/admin/analytics.dart';
import 'package:nurmarket/features/profile/profile_screen.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/scan/scan_animations.dart';
import 'package:nurmarket/features/scan/scan_screen.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/call_rig.dart' show allPerms;

const _dir = String.fromEnvironment('SNAP_DIR');

// ---------------------------------------------------------------------------------------------------------------- общее

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
  final root = Platform.environment['FLUTTER_ROOT'] ?? 'C:/Users/User/flutter';
  final fonts = Directory('$root/bin/cache/artifacts/material_fonts');
  Future<void> load(String family, List<String> files) async {
    final l = FontLoader(family);
    var any = false;
    for (final f in files) {
      final file = File('${fonts.path}/$f');
      if (file.existsSync()) {
        any = true;
        l.addFont(Future.value(ByteData.sublistView(file.readAsBytesSync())));
      }
    }
    if (any) await l.load();
  }

  await load('MaterialIcons', ['materialicons-regular.otf']);
  await load('Roboto', ['roboto-regular.ttf', 'roboto-medium.ttf', 'roboto-bold.ttf', 'roboto-black.ttf', 'roboto-light.ttf']);
}

void _phone(WidgetTester t, {double keyboard = 0}) {
  t.view.physicalSize = const Size(1080, 2340);
  t.view.devicePixelRatio = 3;
  t.view.padding = const FakeViewPadding(top: 84);
  t.view.viewPadding = const FakeViewPadding(top: 84);
  if (keyboard > 0) t.view.viewInsets = FakeViewPadding(bottom: keyboard * 3);
  t.platformDispatcher.textScaleFactorTestValue = 1.0;
  final prev = FlutterError.onError;
  FlutterError.onError = (d) {
    if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
    prev?.call(d);
  };
  addTearDown(() {
    FlutterError.onError = prev;
    t.view.reset();
    t.platformDispatcher.clearTextScaleFactorTestValue();
  });
}

const _delegates = <LocalizationsDelegate<dynamic>>[
  AppLocalizations.delegate,
  GlobalMaterialLocalizations.delegate,
  GlobalWidgetsLocalizations.delegate,
  GlobalCupertinoLocalizations.delegate,
];

void _fixDefaultFont(RenderObject ro) {
  if (ro is RenderParagraph) {
    final s = ro.text;
    if (s is TextSpan && s.style?.fontFamily == null) {
      ro.text = TextSpan(text: s.text, children: s.children, style: (s.style ?? const TextStyle()).copyWith(fontFamily: 'Roboto'), semanticsLabel: s.semanticsLabel);
    }
  }
  ro.visitChildren(_fixDefaultFont);
}

/// Снимок: PNG + JSON {"targets": {имя: [x, y, w, h]}} в пикселях снимка.
class Snapper {
  final WidgetTester t;
  final GlobalKey key;
  final String prefix;
  Snapper(this.t, this.key, this.prefix);

  Future<void> save(String name, {Map<String, Finder> targets = const {}}) async {
    final rects = <String, List<double>>{};
    // ignore: avoid_print
    print('snap $prefix$name');
    for (final e in targets.entries) {
      final f = e.value;
      var none = true;
      try {
        none = f.evaluate().isEmpty;
      } catch (_) {}
      if (none) {
        // ignore: avoid_print
        print('!! нет цели ${e.key} в кадре $prefix$name');
        continue;
      }
      final r = t.getRect(f.first);
      rects[e.key] = [r.left * 3, r.top * 3, r.width * 3, r.height * 3];
    }
    debugDisableShadows = false;
    _fixDefaultFont(RendererBinding.instance.renderView);
    await t.pump(const Duration(milliseconds: 16));
    await t.runAsync(() async {
      final b = key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
      final img = await b.toImage(pixelRatio: 3);
      final png = await img.toByteData(format: ui.ImageByteFormat.png);
      File('$_dir/$prefix$name.png').writeAsBytesSync(png!.buffer.asUint8List());
      File('$_dir/$prefix$name.json').writeAsStringSync(jsonEncode({'targets': rects}));
    });
    debugDisableShadows = true;
  }
}

Future<void> settle(WidgetTester t, {int n = 6, int ms = 120}) async {
  for (var i = 0; i < n; i++) {
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
    await t.pump(Duration(milliseconds: ms));
  }
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _OpenShift extends ShiftController {
  @override
  ShiftState build() => ShiftState(
    shift: ShiftInfo(id: 's1', cashboxId: 'b', openedAt: DateTime.now().subtract(const Duration(hours: 3)), openingCash: 3000, expectedCash: 9315, salesTotal: 9250, cashSales: 5900, nonCashSales: 3350, debtSales: 0, salesCount: 14),
    fetchedAt: DateTime.now(),
  );
  @override
  Future<void> refresh() async {}
}

class _NoNet extends ApiClient {
  _NoNet() : super(SessionStore());
}

const _cashier = Session(email: 'kassir@nur.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Айнура Т.', role: 'cashier', companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1', perms: allPerms);

const _owner = Session(email: 'ainura@nur.kg', access: 'a', refresh: 'r', userId: 'u1', userName: 'Айнура Т.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1', perms: allPerms);

/// Фейковый сервер: отвечает по пути; всё остальное — пустой ответ.
class _FakeApi extends ApiClient {
  _FakeApi(this.get_, {this.post_}) : super(SessionStore());
  final Future<dynamic> Function(String path, Map<String, dynamic>? q) get_;
  final Future<dynamic> Function(String path, Object? body)? post_;
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => get_(path, query);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async => post_ != null ? post_!(path, body) : <String, dynamic>{};
  @override
  Future<dynamic> patch(String path, {Object? body}) async => <String, dynamic>{};
}

Map<String, dynamic> _receipt(int i, String supplier, double qty, double cost) => {
  'id': 'r$i',
  'supplier_id': 's$i',
  'supplier_name': supplier,
  'created_by_name': 'Айнура Т.',
  'created_at': DateTime.utc(2026, 10, 29, 12).subtract(Duration(days: i * 6)).toIso8601String(),
  'total_amount': qty * cost,
  'items': [
    {'product_id': 'a', 'product_name': 'Адыгене 1,5 л', 'qty': qty, 'purchase_price': cost, 'unit': 'шт'},
    {'product_id': 'zz', 'product_name': 'Другой товар', 'qty': 2, 'purchase_price': 5, 'unit': 'шт'},
  ],
};

_FakeApi _batchApi({Object? failWith}) {
  final all = [_receipt(0, 'ОсОО Нур-Трейд', 24, 42), _receipt(1, 'ИП Бекболот', 36, 40), _receipt(2, 'ОсОО Нур-Трейд', 48, 38)];
  return _FakeApi((path, q) async {
    if (failWith != null) throw failWith;
    if (path == 'api/main/suppliers/receipts/') return {'count': all.length, 'next': null, 'results': all};
    if (path.startsWith('api/main/suppliers/receipts/')) {
      final id = path.substring('api/main/suppliers/receipts/'.length).replaceAll('/', '');
      return all.firstWhere((r) => r['id'] == id);
    }
    if (path == 'api/main/clients/') return {'results': <dynamic>[]};
    return {};
  });
}

Future<void> _catalogBuy(AppDatabase db) async {
  const buy = {'a': 42.0, 'w': 190.0, 'u': 48.0, 'v': 26.0, 'h': 14.0, 'n': 21.0, 'k': 70.0, 's': 51.0};
  for (final e in buy.entries) {
    await (db.update(db.products)..where((p) => p.id.equals(e.key))).write(ProductsCompanion(purchasePrice: Value(e.value)));
  }
}


DateTime get _today => DateUtils.dateOnly(DateTime.now());

/// Фейковые отчёты сервера: выручка за день 48 750 сом, 63 чека, прибыль 11 900; прошлый день — чуть меньше.
class _Repo extends AnalyticsRepository {
  _Repo() : super(ApiClient(SessionStore()));
  static const _week = [41200.0, 38900.0, 52300.0, 44100.0, 58600.0, 43100.0, 48750.0];
  @override
  Future<SalesSummary> sales(DateTime from, DateTime to) async {
    final today = _today;
    final days = [for (var i = 0; i < 7; i++) (today.subtract(Duration(days: 6 - i)), _week[i])];
    if (from == to && from != today) {
      return SalesSummary(43100, 58, 743, 10400, [(from, 43100)], const {'cash': 27000, 'card': 11800, 'transfer': 4300});
    }
    return SalesSummary(48750, 63, 774, 11900, from == to ? [(today, 48750)] : days, const {'cash': 29100, 'card': 14200, 'transfer': 5450});
  }

  @override
  Future<List<ProductSales>> products(DateTime from, DateTime to) async => const [
    ProductSales('Winston Blue', 62, 9800, 120),
    ProductSales('Адыгене 1,5 л', 130, 7150, 38),
    ProductSales('ASU', 118, 6100, 33),
    ProductSales('Вода питьевая 1,5 л', 124, 4340, 22),
    ProductSales('Хлеб белый', 150, 3000, 14),
  ];
}

/// Каталог магазина «Нур».
Future<void> _catalog(AppDatabase db) => db.replaceProducts([
  ProductsCompanion.insert(id: 'a', name: 'Адыгене 1,5 л', price: const Value(55), quantity: const Value(24), barcode: const Value('4870001234567'), category: const Value('Вода'), searchKey: const Value('адыгене 1,5 л 4870001234567')),
  ProductsCompanion.insert(id: 'w', name: 'Winston Blue', price: const Value(205), quantity: const Value(18), barcode: const Value('4870009990011'), category: const Value('Сигареты'), searchKey: const Value('winston blue 4870009990011')),
  ProductsCompanion.insert(id: 'u', name: 'ASU', price: const Value(60), quantity: const Value(30), barcode: const Value('4870005550022'), category: const Value('Напитки'), searchKey: const Value('asu 4870005550022')),
  ProductsCompanion.insert(id: 'v', name: 'Вода питьевая 1,5 л', price: const Value(35), quantity: const Value(40), barcode: const Value('4870003330033'), category: const Value('Вода'), searchKey: const Value('вода питьевая 1,5 л 4870003330033')),
  ProductsCompanion.insert(id: 'h', name: 'Хлеб белый', price: const Value(20), quantity: const Value(4), barcode: const Value('4870002220044'), category: const Value('Хлеб'), searchKey: const Value('хлеб белый 4870002220044')),
  ProductsCompanion.insert(id: 'n', name: 'Нан', price: const Value(30), quantity: const Value(40), barcode: const Value('4870001110055'), category: const Value('Хлеб'), searchKey: const Value('нан 4870001110055')),
  ProductsCompanion.insert(id: 'k', name: 'Кант 1 кг', price: const Value(78), quantity: const Value(60), barcode: const Value('4870001110066'), category: const Value('Бакалея'), searchKey: const Value('кант 1 кг 4870001110066')),
  ProductsCompanion.insert(id: 's', name: 'Сүт 1 л', price: const Value(65), quantity: const Value(6), barcode: const Value('4870001110077'), category: const Value('Молочные'), searchKey: const Value('сүт 1 л 4870001110077')),
]);

Finder _tab(String label) => find.descendant(of: find.byType(CashierShell), matching: find.text(label)).last;

GoRouter _router(String initial, {Map<String, WidgetBuilder> extra = const {}}) => GoRouter(
  initialLocation: initial,
  routes: [
    ShellRoute(
      builder: (c, s, child) => CashierShell(location: s.matchedLocation, child: child),
      routes: [
        GoRoute(path: '/home', builder: (_, _) => const HomeScreen()),
        GoRoute(path: '/sale', builder: (_, _) => const SaleScreen()),
        GoRoute(path: '/scan', builder: (_, _) => const SizedBox()),
        GoRoute(path: '/checks', builder: (_, _) => const SizedBox()),
        GoRoute(path: '/stock', builder: (_, _) => const SizedBox()),
      ],
    ),
    GoRoute(path: '/login', builder: (_, _) => const LoginScreen()),
    GoRoute(path: '/pay', builder: (_, _) => const PaymentScreen()),
    for (final e in extra.entries) GoRoute(path: e.key, builder: (c, _) => e.value(c)),
  ],
);

Widget _appRouter(GlobalKey shot, GoRouter r, List<Override> overrides) => ProviderScope(
  overrides: overrides,
  child: RepaintBoundary(
    key: shot,
    child: MaterialApp.router(
      debugShowCheckedModeBanner: false,
      routerConfig: r,
      theme: buildTheme(dark: false),
      locale: const Locale('ru'),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: _delegates,
    ),
  ),
);

Widget _appHome(GlobalKey shot, Widget home, List<Override> overrides) => ProviderScope(
  overrides: overrides,
  child: RepaintBoundary(
    key: shot,
    child: MaterialApp(
      debugShowCheckedModeBanner: false,
      theme: buildTheme(dark: false),
      locale: const Locale('ru'),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: _delegates,
      home: home,
    ),
  ),
);

List<Override> _ov(AppDatabase db, {Session s = _cashier, ApiClient? api}) => [
  databaseProvider.overrideWithValue(db),
  apiProvider.overrideWithValue(api ?? _NoNet()),
  sessionProvider.overrideWith(() => _Fixed(s)),
  shiftProvider.overrideWith(_OpenShift.new),
  pendingCountProvider.overrideWith((ref) => Stream.value(0)),
  myTasksProvider.overrideWith((ref) async => []),
];

void main() {
  final shot = GlobalKey();
  late AppDatabase db;

  setUpAll(() {
    driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
    if (_dir.isNotEmpty) Directory(_dir).createSync(recursive: true);
  });

  Future<AppDatabase> freshDb(WidgetTester t) async {
    final d = AppDatabase(NativeDatabase.memory());
    await t.runAsync(() => _catalog(d));
    return d;
  }

  ProviderContainer cont(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(Scaffold).first));

  // ===================================================================================================== M-01
  testWidgets('M01 вход и первая продажа', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    db = await freshDb(t);
    final l = lookupAppLocalizations(const Locale('ru'));

    // --- вход: пустой экран и заполненный
    await t.pumpWidget(_appHome(shot, const LoginScreen(), [databaseProvider.overrideWithValue(db)]));
    await settle(t, n: 3);
    final s = Snapper(t, shot, 'm01-');
    await s.save('login-empty', targets: {'login': find.byType(TextField).first, 'password': find.byType(TextField).last, 'button': find.widgetWithText(FilledButton, l.signIn)});
    await t.enterText(find.byType(TextField).first, 'kassir@nur.kg');
    await t.enterText(find.byType(TextField).last, '12345678');
    await settle(t, n: 2);
    await s.save('login-filled', targets: {'login': find.byType(TextField).first, 'password': find.byType(TextField).last, 'button': find.widgetWithText(FilledButton, l.signIn)});
    await t.pumpWidget(const SizedBox());

    // --- главная
    final r = _router('/home');
    await t.pumpWidget(_appRouter(shot, r, _ov(db)));
    await settle(t, n: 8);
    await s.save('home', targets: {'tab_sale': _tab(l.tabSale), 'tab_scan': _tab(l.tabScan), 'tab_stock': _tab(l.tabStock), 'tab_checks': _tab(l.tabChecks)});

    // --- продажа: каталог
    await t.tap(_tab(l.tabSale));
    await settle(t, n: 6);
    await s.save('sale-catalog', targets: {'search': find.byType(TextField).first, 'tile_adigene': find.text('Адыгене 1,5 л')});

    // --- товар в чек касанием плитки
    await t.tap(find.text('Адыгене 1,5 л'));
    await settle(t, n: 4);
    await s.save('sale-added', targets: {'pay': find.byType(FilledButton).last, 'tile_adigene': find.text('Адыгене 1,5 л')});
    // второй: Winston Blue
    await t.tap(find.text('Winston Blue'));
    await settle(t, n: 4);
    await t.tap(find.text('ASU'));
    await settle(t, n: 4);
    await s.save('sale-3items', targets: {'pay': find.byType(FilledButton).last});

    // --- вкладка «Чек»
    await t.tap(find.byIcon(Icons.receipt_long_rounded).first);
    await settle(t, n: 6);
    await s.save('cart', targets: {'row_adigene': find.byType(Dismissible).first, 'plus': find.byIcon(Icons.add).first, 'pay': find.byType(FilledButton).last});
    await t.tap(find.byIcon(Icons.add).first);
    await settle(t, n: 3);
    await s.save('cart-qty2', targets: {'pct': find.byIcon(Icons.percent_rounded).first, 'plus': find.byIcon(Icons.add).first, 'row_adigene': find.byType(Dismissible).first, 'pay': find.byType(FilledButton).last});
    // значок «%» — скидка на строку
    await t.tap(find.byIcon(Icons.percent_rounded).first);
    await settle(t, n: 6);
    await s.save('cart-discount-sheet', targets: {'chip10': find.text('10 %')});
    await t.tap(find.text('10 %'));
    await settle(t, n: 6);
    await s.save('cart-discounted', targets: {'pay': find.byType(FilledButton).last});

    // --- оплата
    await t.tap(find.byType(FilledButton).last);
    await settle(t, n: 8);
    await s.save('pay-cash', targets: {'cash': find.text(l.cash), 'card': find.text(l.card), 'received': find.byType(TextField).last, 'confirm': find.widgetWithText(FilledButton, 'Провести оплату')});
    await t.enterText(find.byType(TextField).last, '500');
    await settle(t, n: 3);
    await s.save('pay-cash-received', targets: {'received': find.byType(TextField).last, 'change': find.text(l.change), 'confirm': find.widgetWithText(FilledButton, 'Провести оплату')});
    await t.tap(find.text(l.card));
    await settle(t, n: 4);
    await s.save('pay-card', targets: {'card': find.text(l.card)});
    await t.tap(find.text(l.cash));
    await settle(t, n: 3);
    await t.enterText(find.byType(TextField).last, '500');
    await settle(t, n: 3);
    final cart = cont(t).read(cartProvider);
    await t.pumpWidget(const SizedBox());

    // --- чек
    final receipt = Receipt.fromCart(session: _cashier, cart: cart, number: '1642', payment: 'cash', received: 500);
    final info = PaidInfo(CheckoutResult(queued: false, key: 'k', number: '1642', total: cart.total), 500 - cart.total, receipt);
    await t.pumpWidget(_appHome(shot, PaidScreen(info: info), _ov(db)));
    await settle(t, n: 6);
    await s.save('paid', targets: {'qr': find.text('QR'), 'pdf': find.text('PDF'), 'new': find.text(l.newCheck)});
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ===================================================================================================== M-03
  testWidgets('M03 склад: товар, партии, приёмка, ревизия', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    db = await freshDb(t);
    await t.runAsync(() => _catalogBuy(db));
    final l = lookupAppLocalizations(const Locale('ru'));
    final s = Snapper(t, shot, 'm03-');
    List<Override> ovr([ApiClient? api]) => [..._ov(db, s: _owner, api: api), globalProductsProvider.overrideWithValue(GlobalProducts(openFile: () async => throw StateError('нет')))];

    // --- склад
    await t.pumpWidget(_appHome(shot, const Scaffold(body: StockScreen()), ovr(_batchApi())));
    await settle(t, n: 8);
    await s.save('stock', targets: {'receive': find.textContaining('Приёмка'), 'audit': find.text('Ревизия'), 'search': find.byType(TextField).first, 'clock': find.byIcon(Icons.history_rounded)});
    await t.pumpWidget(const SizedBox());

    // --- партии
    final p = (await t.runAsync(() => db.productById('a')))!;
    await t.pumpWidget(_appHome(shot, ProductBatchesScreen(product: p), ovr(_batchApi())));
    await settle(t, n: 10);
    await s.save('batches', targets: {});
    await t.pumpWidget(const SizedBox());

    // --- новый товар
    final api = _FakeApi((path, q) async => path.contains('suppliers') || path.contains('clients') ? <dynamic>[] : {'results': <dynamic>[]});
    await t.pumpWidget(_appHome(shot, const NewItemScreen(), ovr(api)));
    await settle(t, n: 8);
    await s.save('new-empty', targets: {'barcode': find.byType(TextField).at(0), 'name': find.byType(TextField).at(1)});
    await t.enterText(find.byType(TextField).at(0), '48700abc11122');
    await settle(t, n: 3);
    await s.save('new-barcode', targets: {'barcode': find.byType(TextField).at(0)});
    await t.enterText(find.byType(TextField).at(1), 'Печенье «Нур» 200 г');
    await settle(t, n: 3);
    await s.save('new-name', targets: {'name': find.byType(TextField).at(1)});
    await t.enterText(find.byType(TextField).at(4), '30');
    await t.enterText(find.byType(TextField).at(5), '40');
    await t.enterText(find.byType(TextField).at(8), '50');
    await settle(t, n: 3);
    await s.save('new-prices', targets: {'cost': find.byType(TextField).at(4), 'price': find.byType(TextField).at(5), 'qty': find.byType(TextField).at(8)});
    await t.drag(find.byWidgetPredicate((w) => w is Scrollable && w.axis == Axis.vertical).first, const Offset(0, -420));
    await settle(t, n: 4);
    await s.save('new-unit', targets: {'unit_kg': find.text('кг'), 'unit_sht': find.text('шт')});
    await t.tap(find.text('кг'));
    await settle(t, n: 3);
    await s.save('new-unit-kg', targets: {});
    await t.pumpWidget(const SizedBox());

    // --- приёмка с наценкой
    final lines = [
      ReceiveLine(productId: 'a', name: 'Адыгене 1,5 л', qty: 24, cost: 42, price: 55, oldPrice: 55, oldCost: 40, before: 24),
      ReceiveLine(productId: 'u', name: 'ASU', qty: 30, cost: 48, price: 60, oldPrice: 60, oldCost: 48, before: 30),
      ReceiveLine(productId: 'v', name: 'Вода питьевая 1,5 л', qty: 40, cost: 26, price: 35, oldPrice: 35, oldCost: 26, before: 40),
      ReceiveLine(productId: 'h', name: 'Хлеб белый', qty: 20, cost: 14, price: 20, oldPrice: 20, oldCost: 14, before: 4),
    ];
    final rapi = _FakeApi((path, q) async => path == 'api/main/clients/' ? {'results': [{'id': 's1', 'full_name': 'ОсОО Нур-Трейд'}]} : {'purchase_price': '42', 'quantity': 0});
    await t.pumpWidget(_appHome(shot, AdminReceiveScreen(initialLines: lines, initialSupplierId: 's1'), ovr(rapi)));
    await settle(t, n: 10);
    await s.save('receive', targets: {});
    await t.pumpWidget(const SizedBox());

    // --- ревизия
    await t.pumpWidget(_appHome(shot, const AuditScreen(), ovr()));
    await settle(t, n: 6);
    await s.save('audit-start', targets: {'all': find.text(l.allStock)});
    await t.tap(find.text(l.allStock));
    await settle(t, n: 6);
    Future<void> count(String name, String v) async {
      final row = find.ancestor(of: find.text(name), matching: find.byType(Row)).first;
      await t.enterText(find.descendant(of: row, matching: find.byType(TextField)), v);
      await t.pump();
    }
    await s.save('audit-list', targets: {});
    await count('Адыгене 1,5 л', '21');
    await count('ASU', '27');
    await count('Winston Blue', '17');
    await settle(t, n: 3);
    await s.save('audit-counted', targets: {});
    await t.drag(find.byWidgetPredicate((w) => w is Scrollable && w.axis == Axis.vertical).first, const Offset(0, -2000));
    await settle(t, n: 4);
    await s.save('audit-total', targets: {});
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ===================================================================================================== M-06
  testWidgets('M06 смена и Z-отчёт', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    db = await freshDb(t);
    final l = lookupAppLocalizations(const Locale('ru'));
    final s = Snapper(t, shot, 'm06-');
    final api = _FakeApi((path, q) async {
      if (path.endsWith('/report/')) {
        return {'sales_total': '9250', 'expected_cash': '9315', 'cash_sales_total': '5900', 'noncash_sales_total': '3350', 'debt_sales_total': '0', 'opening_cash': '3000', 'returns_total': '85', 'returns_count': 1, 'debt_payments_cash': '0'};
      }
      if (path == 'api/construction/cashflows/') {
        final at = DateTime.now().subtract(const Duration(hours: 1)).toUtc().toIso8601String();
        return {
          'next': null,
          'results': [
            {'source_kind': 'shift_drawer_in', 'type': 'income', 'amount': '1000', 'payment_method': 'cash', 'created_at': at, 'name': 'Размен'},
            {'source_kind': 'shift_drawer_out', 'type': 'expense', 'amount': '500', 'payment_method': 'cash', 'created_at': at, 'name': 'Хлеб у поставщика'},
            {'source_kind': 'pos_sale_return', 'type': 'expense', 'amount': '85', 'payment_method': 'cash', 'created_at': at},
          ],
        };
      }
      if (path == 'api/main/pos/returns/') return {'results': <dynamic>[], 'next': null};
      return {'results': <dynamic>[], 'next': null};
    });
    final ovr = _ov(db, api: api);

    // --- главная: карточка смены
    await t.pumpWidget(_appRouter(shot, _router('/home'), ovr));
    await settle(t, n: 8);
    await s.save('home', targets: {'close': find.text(l.closeShift).first});
    await t.pumpWidget(const SizedBox());

    // --- внесение
    await t.pumpWidget(_appHome(shot, const CashOpScreen(), ovr));
    await settle(t, n: 8);
    await s.save('cashop', targets: {'amount': find.byType(TextField).first});
    await t.enterText(find.byType(TextField).first, '1000');
    await t.enterText(find.byType(TextField).last, 'Размен');
    await settle(t, n: 3);
    await s.save('cashop-filled', targets: {'amount': find.byType(TextField).first, 'reason': find.byType(TextField).last});
    await t.pumpWidget(const SizedBox());

    // --- закрытие смены
    await t.pumpWidget(_appRouter(shot, _router('/close-shift', extra: {'/close-shift': (_) => const CloseShiftScreen()}), ovr));
    await settle(t, n: 12);
    await s.save('close-top', targets: {'field': find.byType(TextField).last});
    await t.enterText(find.byType(TextField).last, '9315');
    await settle(t, n: 3);
    await s.save('close-counted', targets: {'field': find.byType(TextField).last, 'close': find.widgetWithText(FilledButton, l.closeShift)});
    await t.drag(find.byWidgetPredicate((w) => w is Scrollable && w.axis == Axis.vertical).first, const Offset(0, -700));
    await settle(t, n: 5);
    await s.save('close-mid', targets: {'close': find.widgetWithText(FilledButton, l.closeShift)});
    await t.tap(find.widgetWithText(FilledButton, l.closeShift));
    for (var i = 0; i < 10; i++) {
      await t.pump(const Duration(milliseconds: 100));
    }
    await s.save('close-confirm', targets: {});
    await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.widgetWithText(FilledButton, l.closeShift)));
    for (var i = 0; i < 30; i++) {
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 30)));
      await t.pump(const Duration(milliseconds: 100));
    }
    await s.save('z-top', targets: {});
    await t.drag(find.byWidgetPredicate((w) => w is Scrollable && w.axis == Axis.vertical).first, const Offset(0, -900));
    await settle(t, n: 5);
    await s.save('z-tape', targets: {});
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ===================================================================================================== M-02
  testWidgets('M02 сканер: режимы, весовой, не найден', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    db = await freshDb(t);
    await t.runAsync(() => db.upsertProduct(ProductsCompanion.insert(id: 'ap', name: 'Яблоки красные', price: const Value(120), quantity: const Value(30), unit: const Value('кг'), isWeight: const Value(true), plu: const Value('00012'), searchKey: const Value('яблоки красные'))));
    final l = lookupAppLocalizations(const Locale('ru'));
    final s = Snapper(t, shot, 'm02-');
    ScanMode mode = ScanMode.sale;
    Widget cam(ScanMode m) => Scaffold(
      backgroundColor: const Color(0xFF071824),
      body: Stack(fit: StackFit.expand, children: [
        const DecoratedBox(decoration: BoxDecoration(gradient: RadialGradient(center: Alignment(0, -.2), radius: 1.1, colors: [Color(0xFF2A4256), Color(0xFF14232F), Color(0xFF071824)])), child: SizedBox.expand()),
        const Align(alignment: Alignment(0, -.1), child: ScanFrame()),
        Align(alignment: Alignment.topCenter, child: SafeArea(child: Padding(padding: const EdgeInsets.all(14), child: Row(children: [Expanded(child: ScanModeSelect(mode: m, onSelect: (_) {}, onKassa: () {}))])))),
      ]),
    );
    await t.pumpWidget(_appHome(shot, cam(mode), _ov(db)));
    await settle(t, n: 4);
    await s.save('cam', targets: {'mode': find.byType(ScanModeSelect)});
    await t.tap(find.byType(ScanModeSelect));
    await settle(t, n: 6);
    await s.save('modes', targets: {'weigh': find.text('Весы'), 'look': find.text('По виду'), 'kassa': find.text('Сканер для кассы'), 'receive': find.text('Приёмка'), 'price': find.text('Узнать цену')});
    await t.pumpWidget(const SizedBox());

    // --- весовая этикетка и неизвестный штрихкод в продаже
    await t.pumpWidget(_appRouter(shot, _router('/sale'), _ov(db, s: _owner, api: _FakeApi((path, q) async => {'results': <dynamic>[], 'next': null}))));
    await settle(t, n: 6);
    Future<void> scan(String code) async {
      await t.enterText(find.byType(TextField).first, code);
      await t.testTextInput.receiveAction(TextInputAction.search);
      await settle(t, n: 4);
    }
    await s.save('sale-empty', targets: {'search': find.byType(TextField).first});
    await scan('2000012005003');
    await s.save('weight-scanned', targets: {});
    await scan('9990000000017');
    await settle(t, n: 8);
    await s.save('notfound', targets: {'create': find.text(l.createProductWithCode)});
    await t.pumpWidget(const SizedBox());

    // --- «Добавить товар» с этим штрихкодом
    final api = _FakeApi((path, q) async => <dynamic>[]);
    await t.pumpWidget(_appHome(shot, const NewItemScreen(barcode: '9990000000017'), [..._ov(db, s: _owner, api: api), globalProductsProvider.overrideWithValue(GlobalProducts(openFile: () async => throw StateError('нет')))]));
    await settle(t, n: 8);
    await s.save('add-with-code', targets: {'barcode': find.byType(TextField).at(0), 'name': find.byType(TextField).at(1)});
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ===================================================================================================== M-08
  testWidgets('M08 управление', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    db = await freshDb(t);
    final l = lookupAppLocalizations(const Locale('ru'));
    final s = Snapper(t, shot, 'm08-');
    final api = _FakeApi((path, q) async {
      if (path == 'api/main/clients/debtors/') {
        return {
          'next': null,
          'results': [
            {'client': 'c1', 'full_name': 'Асель Ж.', 'phone': '0700 123 456', 'debt_total': '1250.00', 'sales_count': 3},
            {'client': 'c2', 'full_name': 'Бакыт Т.', 'phone': '0555 000 111', 'debt_total': '840.00', 'sales_count': 2},
            {'client': 'c3', 'full_name': 'Магазин «Алтын»', 'phone': '0777 222 333', 'debt_total': '395.00', 'sales_count': 1},
          ],
        };
      }
      return {'results': <dynamic>[], 'next': null};
    });
    await t.pumpWidget(_appHome(shot, const AdminScreen(), _ov(db, s: _owner, api: api)));
    await settle(t, n: 8);
    await s.save('admin', targets: {});
    await t.pumpWidget(const SizedBox());

    await t.pumpWidget(_appHome(shot, const SummaryScreen(), [..._ov(db, s: _owner, api: api), analyticsRepoProvider.overrideWithValue(_Repo())]));
    await settle(t, n: 8);
    await s.save('summary', targets: {'compare': find.text(l.infCompare)});
    if (find.text(l.infCompare).evaluate().isNotEmpty) {
      await t.tap(find.text(l.infCompare).first);
      await settle(t, n: 6);
    }
    await s.save('summary-compare', targets: {});
    await t.drag(find.byType(ListView).first, const Offset(0, -900));
    await settle(t, n: 5);
    await s.save('summary-charts', targets: {});
    await t.pumpWidget(const SizedBox());

    await t.pumpWidget(_appHome(shot, const DebtsScreen(), _ov(db, s: _owner, api: api)));
    await settle(t, n: 10);
    await s.save('debts', targets: {});
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);

  // ===================================================================================================== M-09
  testWidgets('M09 без интернета', (t) async {
    await t.runAsync(_fonts);
    _phone(t);
    db = await freshDb(t);
    final l = lookupAppLocalizations(const Locale('ru'));
    final s = Snapper(t, shot, 'm09-');
    // 1) продажа проведена без связи
    final cartLines = Cart(lines: [CartLine(productId: 'a', name: 'Адыгене 1,5 л', price: 55, qty: 2), CartLine(productId: 'n', name: 'Нан', price: 30, qty: 1)]);
    final receipt = Receipt.fromCart(session: _cashier, cart: cartLines, number: '', payment: 'cash', received: 200);
    final info = PaidInfo(CheckoutResult(queued: true, key: 'k1', number: null, total: cartLines.total), 200 - cartLines.total, receipt);
    final dead = _FakeApi((path, q) async => throw ApiException('Нет связи с сервером.'));
    await t.pumpWidget(_appHome(shot, PaidScreen(info: info), _ov(db, api: dead)));
    await settle(t, n: 6);
    await s.save('paid-queued', targets: {});
    await t.pumpWidget(const SizedBox());

    // 2) очередь чеков
    await t.runAsync(() async {
      for (var i = 0; i < 2; i++) {
        await db.enqueue(OutboxCompanion.insert(
          id: 'q$i',
          kind: 'sale',
          body: jsonEncode({'_meta': {'owner': 'kassir@nur.kg', 'at': DateTime.now().toIso8601String()}, 'items': <dynamic>[]}),
          createdAt: DateTime.now().subtract(Duration(minutes: 7 - i * 3)),
          total: Value(140.0 + i * 85),
          summary: Value(i == 0 ? 'Адыгене 1,5 л, Нан' : 'Winston Blue'),
          paymentMethod: const Value('cash'),
        ));
      }
    });
    await t.pumpWidget(_appHome(shot, const SyncScreen(), _ov(db, api: dead)));
    await settle(t, n: 8);
    await s.save('queue', targets: {'send': find.text('Отправить сейчас'), 'row1': find.text('Winston Blue')});
    await t.pumpWidget(const SizedBox());

    // 3) список должников из памяти телефона: «список от ЧЧ:ММ»
    await t.runAsync(() async {
      final repo = ProductBatchesRepository(dead, db);
      await repo.cacheWrite('a', [BatchEntry(receiptId: 'r7', qty: 24, cost: 42, supplierName: 'ОсОО Нур-Трейд', at: DateTime(2026, 10, 27, 9))], now: DateTime(2026, 10, 30, 14, 5));
    });
    final p = (await t.runAsync(() => db.productById('a')))!;
    await t.pumpWidget(_appHome(shot, ProductBatchesScreen(product: p), _ov(db, s: _owner, api: dead)));
    await settle(t, n: 10);
    await s.save('cached', targets: {'from': find.text(l.pbDataFrom('14:05'))});
    await t.pumpWidget(const SizedBox());
  }, skip: _dir.isEmpty);
}
