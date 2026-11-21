// 2026-11-20 (тариф «Мини», Простой режим): снимки экранов 360×640 для глаз — продажа, чек с торгом, «Получено», «Торг», итоги, приход, «Ещё».
// Без --dart-define=SNAP_DIR=<папка> тест пропускается, в репозиторий ничего не пишется.
import 'dart:io';
import 'dart:ui' as ui;

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/server_capabilities.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/mode/mode_provider.dart';
import 'package:nurmarket/core/mode/plan_info.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/features/simple/simple_routes.dart';
import 'package:nurmarket/features/simple/simple_shell.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

const _dir = String.fromEnvironment('SNAP_DIR');

class _Api extends ApiClient {
  _Api() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    if (path == 'api/main/capabilities/') {
      return {
        'features': <String, dynamic>{},
        'plan': {'code': 'mini', 'names': {'ru': 'Мини'}, 'limits': {'max_staff': 1}, 'upgrade_plan': 'start'},
      };
    }
    if (path == 'api/main/clients/debtors/') {
      return {
        'results': [
          {'client': 'c1', 'full_name': 'Асель', 'phone': '0700123456', 'debt_total': '1250.00', 'sales_count': 2},
          {'client': 'c2', 'full_name': 'Бакыт', 'phone': '0555000111', 'debt_total': '400.00', 'sales_count': 1},
        ],
      };
    }
    return {'results': <dynamic>[]};
  }
}

class _Fixed extends SessionController {
  @override
  Session? build() => const Session(email: 'o@x.kg', access: 'a', refresh: 'r', userId: 'u', userName: 'Айгерим', role: 'owner', isOwner: true, companyName: 'Нур', cashboxId: 'b', cashboxName: 'Касса 1');
}

class _Shift extends ShiftController {
  @override
  ShiftState build() => const ShiftState(shift: ShiftInfo(id: 'sh1', salesTotal: 12500, cashSales: 9000, expectedCash: 9500, salesCount: 7));
  @override
  Future<void> refresh() async {}
}

class _On extends SimpleModeController {
  @override
  bool build() => true;
  @override
  Future<void> load() async {}
}

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

void main() {
  final shot = GlobalKey();
  late AppDatabase db;
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: 't', name: 'Футболка хлопок', price: const Value(430), quantity: const Value(12), isFavorite: const Value(true), searchKey: const Value('футболка')),
      ProductsCompanion.insert(id: 's', name: 'Носки', price: const Value(120), quantity: const Value(30), isFavorite: const Value(true), searchKey: const Value('носки')),
      ProductsCompanion.insert(id: 'r', name: 'Ремень кожаный мужской', price: const Value(650), quantity: const Value(3), isFavorite: const Value(true), searchKey: const Value('ремень')),
      ProductsCompanion.insert(id: 'k', name: 'Кепка бейсболка', price: const Value(300), quantity: const Value(8), isFavorite: const Value(true), searchKey: const Value('кепка')),
      ProductsCompanion.insert(id: 'p', name: 'Перчатки зимние', price: const Value(250), quantity: const Value(8), isFavorite: const Value(true), searchKey: const Value('перчатки')),
      ProductsCompanion.insert(id: 'sh', name: 'Шарф', price: const Value(350), quantity: const Value(8), isFavorite: const Value(true), searchKey: const Value('шарф')),
    ]);
  });
  tearDown(() async {
    ServerCapabilities.debugSet(null);
    await db.close();
  });

  Future<void> save(WidgetTester t, String name) async {
    await t.runAsync(() async {
      final b = shot.currentContext!.findRenderObject()! as RenderRepaintBoundary;
      final img = await b.toImage(pixelRatio: 1);
      final png = await img.toByteData(format: ui.ImageByteFormat.png);
      File('$_dir/$name.png').writeAsBytesSync(png!.buffer.asUint8List());
    });
  }

  testWidgets('снимки Простого режима 360×640', (t) async {
    await t.runAsync(_fonts);
    t.view.physicalSize = const Size(360, 640);
    t.view.devicePixelRatio = 1;
    addTearDown(t.view.reset);
    final router = GoRouter(
      initialLocation: '/sale',
      routes: [
        ShellRoute(
          builder: (c, s, child) => AdaptiveShell(location: s.matchedLocation, child: child),
          routes: [
            GoRoute(path: '/sale', builder: (_, _) => const SaleEntry(classic: SizedBox())),
            ...simpleShellRoutes(),
          ],
        ),
        GoRoute(path: '/sync', builder: (_, _) => const SizedBox()),
      ],
    );
    ServerCapabilities.debugSet([], plan: PlanInfo.parse({'plan': {'code': 'mini', 'names': {'ru': 'Мини'}, 'limits': {'max_staff': 1}, 'upgrade_plan': 'start'}}));
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(_Api()),
          sessionProvider.overrideWith(_Fixed.new),
          shiftProvider.overrideWith(_Shift.new),
          simpleModeProvider.overrideWith(_On.new),
        ],
        child: RepaintBoundary(
          key: shot,
          child: MaterialApp.router(
            routerConfig: router,
            debugShowCheckedModeBanner: false,
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          ),
        ),
      ),
    );
    await t.pumpAndSettle();
    await save(t, 'simple_1_sale_empty');
    final c = ProviderScope.containerOf(t.element(find.byType(Scaffold).first));
    c.read(cartProvider.notifier).add((await db.productById('t'))!);
    c.read(cartProvider.notifier).add((await db.productById('t'))!);
    c.read(cartProvider.notifier).add((await db.productById('s'))!);
    await t.pumpAndSettle();
    await save(t, 'simple_2_sale_cart');
    await t.tap(find.byKey(const Key('simpleReceivedBtn')));
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('recvQuick_1000')));
    await t.pumpAndSettle();
    await save(t, 'simple_3_received');
    Navigator.of(t.element(find.byKey(const Key('recvField')))).pop();
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('simpleHaggleBtn')));
    await t.pumpAndSettle();
    await t.enterText(find.byKey(const Key('haggleField')), '950');
    await t.pumpAndSettle();
    await save(t, 'simple_4_haggle');
    Navigator.of(t.element(find.byKey(const Key('haggleField')))).pop();
    await t.pumpAndSettle();
    router.go('/totals');
    await t.pumpAndSettle();
    await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
    await t.pumpAndSettle();
    await save(t, 'simple_5_totals');
    router.go('/in');
    await t.pumpAndSettle();
    await save(t, 'simple_6_receive');
    router.go('/more');
    await t.pumpAndSettle();
    await save(t, 'simple_7_more');
    await t.pumpWidget(const SizedBox());
    await t.pump(const Duration(milliseconds: 50));
  }, skip: _dir.isEmpty);
}
