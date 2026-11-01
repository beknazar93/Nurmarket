// 2026-10-31 (владелец: «режимы должны отличаться в интерфейсе»): режим магазина виден и ощущается — плашка на главной, набор плиток,
// подсказка поиска и пустое состояние, значки на плитках товаров, фильтры склада, блок в карточке товара, плашка приёмки; смена режима
// применяется сразу. Снимки по режимам: flutter test test/shop_modes_ui_test.dart --dart-define=SNAP_DIR=<папка> (без неё снимки
// пропускаются, в репозиторий ничего не пишется). Сервер не трогается: база в памяти, фейковый клиент.
import 'dart:io';
import 'dart:ui' as ui;

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/shop_mode.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/features/home/home_screen.dart';
import 'package:nurmarket/features/sale/sale_screen.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/features/stock/mode_product_info.dart';
import 'package:nurmarket/features/stock/sizes_screen.dart';
import 'package:nurmarket/features/stock/stock_screens.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _dir = String.fromEnvironment('SNAP_DIR');

class _Offline extends ApiClient {
  _Offline() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => throw ApiException('Нет связи с сервером.');
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async => throw ApiException('Нет связи с сервером.');
  @override
  Future<dynamic> patch(String path, {Object? body}) async => throw ApiException('Нет связи с сервером.');
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _OpenShift extends ShiftController {
  @override
  ShiftState build() => const ShiftState(shift: ShiftInfo(id: 'sh1'));
}

const _owner = Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u2', userName: 'Бакыт О.', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
  final root = Platform.environment['FLUTTER_ROOT'] ?? 'C:/Users/User/flutter';
  final icons = File('$root/bin/cache/artifacts/material_fonts/materialicons-regular.otf');
  if (icons.existsSync()) {
    await (FontLoader('MaterialIcons')..addFont(Future.value(ByteData.sublistView(icons.readAsBytesSync())))).load();
  }
}

String _in(int days) {
  final d = DateTime.now().add(Duration(days: days));
  String two(int x) => x.toString().padLeft(2, '0');
  return '${d.year}-${two(d.month)}-${two(d.day)}';
}

void main() {
  late AppDatabase db;
  final shot = GlobalKey();

  setUpAll(_fonts);
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'milk', name: 'Молоко «Нур» 1 л', price: const Value(95), quantity: const Value(12), searchKey: const Value('молоко'), expiresOn: Value(_in(3))),
      ProductsCompanion.insert(id: 'cheese', name: 'Сыр голландский', price: const Value(640), quantity: const Value(8), unit: const Value('кг'), isWeight: const Value(true), plu: const Value('21'), searchKey: const Value('сыр'), expiresOn: Value(_in(30))),
      ProductsCompanion.insert(id: 'tee', name: 'Футболка', price: const Value(1200), quantity: const Value(14), searchKey: const Value('футболка')),
      ProductsCompanion.insert(id: 'tape', name: 'Лента строительная', price: const Value(40), quantity: const Value(100), unit: const Value('м'), searchKey: const Value('лента')),
    ]);
    await db.replaceVariants('tee', [
      ProductVariantsCompanion.insert(id: 'v1', productId: 'tee', size: const Value('M'), color: const Value('Красный'), quantity: const Value(3)),
      ProductVariantsCompanion.insert(id: 'v2', productId: 'tee', size: const Value('L'), color: const Value('Красный'), quantity: const Value(0)),
      ProductVariantsCompanion.insert(id: 'v3', productId: 'tee', size: const Value('M'), color: const Value('Синий'), quantity: const Value(5)),
    ]);
  });
  tearDown(() => db.close());

  List<Override> overrides() => [
    databaseProvider.overrideWithValue(db),
    pendingCountProvider.overrideWith((ref) => Stream.value(0)),
    apiProvider.overrideWithValue(_Offline()),
    sessionProvider.overrideWith(() => _Fixed(_owner)),
    shiftProvider.overrideWith(_OpenShift.new),
  ];

  Widget app(Widget home, {String lang = 'ru', bool dark = false}) => ProviderScope(
    overrides: overrides(),
    child: RepaintBoundary(
      key: shot,
      child: MaterialApp(
        debugShowCheckedModeBanner: false,
        theme: buildTheme(dark: false),
        darkTheme: buildTheme(dark: true),
        themeMode: dark ? ThemeMode.dark : ThemeMode.light,
        locale: Locale(lang),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: Scaffold(body: home),
      ),
    ),
  );

  void phone(WidgetTester t, {double scale = 1.0}) {
    t.view.physicalSize = const Size(720, 1280); // 360×640 dp
    t.view.devicePixelRatio = 2.0;
    t.platformDispatcher.textScaleFactorTestValue = scale;
    final prev = FlutterError.onError;
    FlutterError.onError = (d) {
      if (d.exceptionAsString().contains('not supported by all of its localization delegates')) return;
      prev?.call(d);
    };
    addTearDown(() {
      FlutterError.onError = prev;
      t.view.resetPhysicalSize();
      t.view.resetDevicePixelRatio();
      t.platformDispatcher.clearTextScaleFactorTestValue();
    });
  }

  ProviderContainer of(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(Scaffold).first));

  Future<void> settle(WidgetTester t) async {
    await t.pump(const Duration(milliseconds: 200));
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 80)));
    await t.pump(const Duration(milliseconds: 400));
  }

  Future<void> setMode(WidgetTester t, ShopMode? m) async {
    final c = of(t);
    if (m == null) return;
    await t.runAsync(() => c.read(prefsProvider.notifier).setShop(m));
    await settle(t);
  }

  Future<void> save(WidgetTester t, String name) async {
    await t.runAsync(() async {
      final b = shot.currentContext!.findRenderObject()! as RenderRepaintBoundary;
      final img = await b.toImage(pixelRatio: 2);
      final png = await img.toByteData(format: ui.ImageByteFormat.png);
      File('$_dir/$name.png').writeAsBytesSync(png!.buffer.asUint8List());
    });
  }

  AppLocalizations ru() => lookupAppLocalizations(const Locale('ru'));

  // ------------------------------------------------------------------ главная
  group('главная: плашка режима и плитки под режим', () {
    final tileKeys = {
      null: <String>[],
      ShopMode.grocery: ['tileWeighed', 'tileExpiry', 'tileReceive'],
      ShopMode.clothing: ['tileSizes', 'tileReceive'],
      ShopMode.hardware: ['tileUnits', 'tileReceive'],
      ShopMode.services: ['tileRentals'],
    };
    const all = ['tileWeighed', 'tileExpiry', 'tileReceive', 'tileSizes', 'tileUnits', 'tileRentals'];

    for (final e in tileKeys.entries) {
      testWidgets('режим ${e.key?.code ?? 'не выбран'}: плитки ${e.value}, остальных режимных плиток нет; Продажа первая; без переполнения', (t) async {
        phone(t);
        await t.pumpWidget(app(const HomeScreen()));
        await settle(t);
        await setMode(t, e.key);
        expect(t.takeException(), isNull);
        for (final k in all) {
          expect(find.byKey(Key(k)), e.value.contains(k) ? findsOneWidget : findsNothing, reason: '$k в режиме ${e.key?.code}');
        }
        expect(find.byKey(const Key('shopModeChip')), findsOneWidget);
        final l = ru();
        expect(find.descendant(of: find.byKey(const Key('shopModeChip')), matching: find.text(e.key == null ? l.shopModeNotChosen : e.key!.title(l))), findsOneWidget);
        expect(find.text(l.tabSale), findsWidgets);
      });
    }

    testWidgets('смена режима применяется сразу, без перезапуска: одежда → продукты → хозтовары', (t) async {
      phone(t);
      await t.pumpWidget(app(const HomeScreen()));
      await settle(t);
      await setMode(t, ShopMode.clothing);
      expect(find.byKey(const Key('tileSizes')), findsOneWidget);
      await setMode(t, ShopMode.grocery);
      expect(find.byKey(const Key('tileSizes')), findsNothing);
      expect(find.byKey(const Key('tileExpiry')), findsOneWidget);
      await setMode(t, ShopMode.hardware);
      expect(find.byKey(const Key('tileExpiry')), findsNothing);
      expect(find.byKey(const Key('tileUnits')), findsOneWidget);
    });

    for (final lang in ['ru', 'ky', 'en', 'tr', 'uz']) {
      for (final dark in [false, true]) {
        testWidgets('все режимы: $lang, тёмная=$dark, шрифт 1,3 — без переполнения', (t) async {
          phone(t, scale: 1.3);
          await t.pumpWidget(app(const HomeScreen(), lang: lang, dark: dark));
          await settle(t);
          for (final m in ShopMode.values) {
            await setMode(t, m);
            expect(t.takeException(), isNull, reason: '$lang ${m.code}');
          }
        });
      }
    }
  });

  // ------------------------------------------------------------------ продажа
  group('продажа: подсказка поиска, значки, пустое состояние, быстрый вес', () {
    testWidgets('подсказка поиска и пустое состояние — под режим', (t) async {
      phone(t);
      await t.pumpWidget(app(const SaleScreen()));
      await settle(t);
      final l = ru();
      expect(find.text(l.searchProduct), findsOneWidget, reason: 'режим не выбран — прежняя подсказка');
      for (final m in ShopMode.values) {
        await setMode(t, m);
        expect(find.text(m.searchHint(l)), findsOneWidget, reason: m.code);
      }
      await setMode(t, ShopMode.clothing);
      await t.enterText(find.byType(TextField).first, 'яяяя');
      await t.pump(const Duration(milliseconds: 500));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 80)));
      await t.pump(const Duration(milliseconds: 400));
      expect(find.byKey(const Key('saleEmpty')), findsOneWidget);
      expect(find.textContaining(ShopMode.clothing.emptyText(l)), findsOneWidget);
    });

    testWidgets('продукты: значок «кг» у весового, «до …» у товара со сроком ≤ 14 дней (красный при ≤ 7); хозтовары: «м»; в одежде значков нет', (t) async {
      phone(t);
      await t.pumpWidget(app(const SaleScreen()));
      await settle(t);
      expect(find.byKey(const Key('modeBadge_milk')), findsNothing, reason: 'режим не выбран — значков нет');
      await setMode(t, ShopMode.grocery);
      expect(find.byKey(const Key('modeBadge_cheese')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('modeBadge_cheese')), matching: find.text('кг')), findsOneWidget);
      expect(find.byKey(const Key('modeBadge_milk')), findsOneWidget);
      final milk = t.widget<Container>(find.byKey(const Key('modeBadge_milk')));
      expect((milk.decoration! as BoxDecoration).color, T.danger);
      expect(find.byKey(const Key('modeBadge_tape')), findsNothing);
      await setMode(t, ShopMode.hardware);
      expect(find.descendant(of: find.byKey(const Key('modeBadge_tape')), matching: find.text('м')), findsOneWidget);
      expect(find.byKey(const Key('modeBadge_milk')), findsNothing);
      await setMode(t, ShopMode.clothing);
      expect(find.byKey(const Key('modeBadge_tape')), findsNothing);
      expect(find.byKey(const Key('modeBadge_cheese')), findsNothing);
    });

    testWidgets('продукты: весовой товар — быстрые кнопки веса; 0,5 кг сразу в чек; срок годности близко — предупреждение', (t) async {
      phone(t);
      await t.pumpWidget(app(const SaleScreen()));
      await settle(t);
      await setMode(t, ShopMode.grocery);
      await t.tap(find.text('Сыр голландский'));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('quickWeight_0.5')), findsOneWidget);
      await t.tap(find.byKey(const Key('quickWeight_0.5')));
      await t.pumpAndSettle();
      final line = of(t).read(cartProvider).lines.single;
      expect(line.qty, 0.5);
      expect(line.isWeight, isTrue);
      // молоко: срок через 3 дня — предупреждение
      await t.tap(find.text('Молоко «Нур» 1 л'));
      await settle(t);
      final l = ru();
      expect(find.textContaining(l.expirySoonToast('Молоко «Нур» 1 л', expiryText(_in(3)))), findsOneWidget);
    });

    testWidgets('хозтовары: у количества в чеке — единица (м)', (t) async {
      phone(t);
      await t.pumpWidget(app(const SaleScreen()));
      await settle(t);
      await setMode(t, ShopMode.hardware);
      await t.tap(find.text('Лента строительная'));
      await settle(t);
      await t.tap(find.text(ru().checkTab(1)));
      await t.pumpAndSettle();
      expect(find.text('1 м'), findsOneWidget);
    });
  });

  // ------------------------------------------------------------------ склад и карточка
  group('склад и карточка товара', () {
    Future<void> openStock(WidgetTester t, String filter) async {
      phone(t);
      await t.pumpWidget(app(StockScreen(key: ValueKey(filter), filter: filter)));
      await settle(t);
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      await t.pump(const Duration(milliseconds: 400));
    }

    testWidgets('фильтры плиток главной: размеры / весовые / срок годности / единицы', (t) async {
      await openStock(t, 'sizes');
      expect(find.text('Футболка'), findsOneWidget);
      expect(find.text('Молоко «Нур» 1 л'), findsNothing);
      expect(find.byKey(const Key('stockModeFilter')), findsOneWidget);
      await openStock(t, 'weight');
      expect(find.text('Сыр голландский'), findsOneWidget);
      expect(find.text('Футболка'), findsNothing);
      await openStock(t, 'expiry');
      expect(find.text('Молоко «Нур» 1 л'), findsOneWidget, reason: 'срок через 3 дня');
      expect(find.text('Сыр голландский'), findsNothing, reason: 'срок через 30 дней — не близко');
      await openStock(t, 'units');
      expect(find.text('Лента строительная'), findsOneWidget);
      expect(find.text('Футболка'), findsNothing);
      expect(find.text('Сыр голландский'), findsNothing, reason: 'весовой — не «единицы»');
    });

    testWidgets('карточка товара: одежда — размеры и цвета; продукты — вес/PLU и срок годности; хозтовары — единица; услуги/не выбран — блока нет', (t) async {
      phone(t);
      final tee = (await db.productById('tee'))!;
      final cheese = (await db.productById('cheese'))!;
      final tape = (await db.productById('tape'))!;
      Widget card(Product p) => app(SingleChildScrollView(child: ModeProductInfo(product: p)));
      await t.pumpWidget(card(tee));
      await settle(t);
      expect(find.byKey(const Key('modeProductInfo')), findsNothing, reason: 'режим не выбран');
      await setMode(t, ShopMode.clothing);
      expect(find.byKey(const Key('modeProductInfo')), findsOneWidget);
      expect(find.text('M · 3'), findsOneWidget);
      expect(find.text('L · 0'), findsOneWidget);
      expect(find.text('M · 5'), findsOneWidget);
      await t.pumpWidget(card(cheese));
      await setMode(t, ShopMode.grocery);
      expect(find.textContaining('21'), findsWidgets, reason: 'PLU');
      expect(find.byKey(const Key('cardExpiry')), findsOneWidget);
      await t.pumpWidget(card(tape));
      await setMode(t, ShopMode.hardware);
      expect(find.text(ru().cardUnitInfo('м')), findsOneWidget);
      await setMode(t, ShopMode.services);
      expect(find.byKey(const Key('modeProductInfo')), findsNothing);
    });

    testWidgets('«Размеры и цвета»: товары с размерами и остатки; пусто — подсказка', (t) async {
      phone(t);
      await t.pumpWidget(app(const SizesScreen()));
      await settle(t);
      expect(find.byKey(const Key('sizesRow_tee')), findsOneWidget);
      expect(find.text('M · 3'), findsOneWidget);
      expect(find.byKey(const Key('sizesRow_milk')), findsNothing);
      await t.enterText(find.byType(TextField).first, 'нет такого');
      await t.pump(const Duration(milliseconds: 500));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 80)));
      await t.pump(const Duration(milliseconds: 400));
      expect(find.byKey(const Key('sizesEmpty')), findsOneWidget);
    });
  });

  // ------------------------------------------------------------------ снимки
  group('снимки по режимам (нужен --dart-define=SNAP_DIR)', () {
    for (final dark in [false, true]) {
      for (final m in <ShopMode?>[null, ...ShopMode.values]) {
        final name = 'home_${m?.code ?? 'none'}${dark ? '_dark' : ''}';
        testWidgets('снимок: главная — $name', (t) async {
          phone(t);
          await t.pumpWidget(app(const HomeScreen(), dark: dark));
          await settle(t);
          await setMode(t, m);
          await save(t, name);
        }, skip: _dir.isEmpty);
        final sale = 'sale_${m?.code ?? 'none'}${dark ? '_dark' : ''}';
        testWidgets('снимок: продажа — $sale', (t) async {
          phone(t);
          await t.pumpWidget(app(const SaleScreen(), dark: dark));
          await settle(t);
          await setMode(t, m);
          if (m == ShopMode.clothing) await t.pump(const Duration(milliseconds: 600));
          await save(t, sale);
        }, skip: _dir.isEmpty);
      }
    }
  });
}
