// 2026-11-20 (тариф «Мини», Простой режим): экран продажи (плитки, «+» «−», «Оплатить N сом» одним нажатием, «До 50», «Получено» со сдачей, долг),
// навигация из 3 вкладок + «Ещё», «Приход» одним сканом, «Новый товар», «Итоги дня», предупреждения; текст не обрезается и кнопки не налезают друг
// на друга на 360×640 и 411×891. Сервер и боевая база не трогаются: база в памяти, фейковый клиент (без связи чек уходит в очередь).
import 'dart:convert';

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
import 'package:nurmarket/core/sync/outbox_meta.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/ui.dart' show money;
import 'package:nurmarket/features/home/shell.dart';
import 'package:nurmarket/features/payment/payment_screen.dart';
import 'package:nurmarket/features/payment/quick_pay_preset.dart';
import 'package:nurmarket/features/payment/sales_repository.dart' show PayMethod;
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/features/simple/simple_receive_screen.dart';
import 'package:nurmarket/features/simple/simple_routes.dart';
import 'package:nurmarket/features/simple/simple_shell.dart';
import 'package:nurmarket/features/simple/quick_product.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _Api extends ApiClient {
  final posts = <String, Object?>{};
  final patches = <String, Object?>{};

  /// Что сервер отвечает на `capabilities` (тариф); null — ответ без плана.
  Map<String, dynamic>? capsPlan;
  _Api() : super(SessionStore());

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    if (path == 'api/main/capabilities/') return {'features': <String, dynamic>{}, 'plan': capsPlan};
    if (path == 'api/main/clients/debtors/') {
      return {
        'results': [
          {'client': 'c1', 'full_name': 'Асель', 'phone': '0700123456', 'debt_total': '1250.00', 'sales_count': 2},
        ],
      };
    }
    return {'results': <dynamic>[]};
  }

  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async {
    if (path == 'api/main/pos/checkout/') throw ApiException('Нет связи с сервером.'); // без связи чек встаёт в очередь
    posts[path] = body;
    if (path == 'api/main/clients/') return {'id': 'c9', 'full_name': (body as Map)['full_name'], 'phone': body['phone']};
    if (path == 'api/main/products/create-manual/') return {'id': 'n1'};
    return <String, dynamic>{};
  }

  @override
  Future<dynamic> patch(String path, {Object? body}) async {
    patches[path] = body;
    return <String, dynamic>{};
  }
}

class _Fixed extends SessionController {
  final Session s;
  _Fixed(this.s);
  @override
  Session? build() => s;
}

class _OpenShift extends ShiftController {
  final ShiftInfo info;
  _OpenShift([this.info = const ShiftInfo(id: 'sh1')]);
  @override
  ShiftState build() => ShiftState(shift: info);
  @override
  Future<void> refresh() async {} // тест держит свою смену
}

class _SimpleOn extends SimpleModeController {
  @override
  bool build() => true;
  @override
  Future<void> load() async {}
}

String _jwt(DateTime exp) {
  String b(Object o) => base64Url.encode(utf8.encode(jsonEncode(o))).replaceAll('=', '');
  return '${b({'alg': 'HS256'})}.${b({'exp': exp.millisecondsSinceEpoch ~/ 1000})}.sig';
}

Session _owner({String refresh = 'r'}) => Session(
  email: 'o@x.kg',
  access: 'a',
  refresh: refresh,
  userId: 'u',
  userName: 'Айгерим',
  role: 'owner',
  isOwner: true,
  companyName: 'Нур',
  cashboxId: 'b',
  cashboxName: 'Касса 1',
);

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

List<String> _clipped() {
  final out = <String>[];
  for (final e in find.byType(RichText).evaluate()) {
    final r = e.renderObject;
    if (r is RenderParagraph && r.didExceedMaxLines) out.add(r.text.toPlainText());
  }
  return out;
}

/// testWidgets + разборка дерева в конце: потоки drift закрываются отложенным таймером, без паузы тест ругается на «Timer is still pending».
void tw(String name, Future<void> Function(WidgetTester t) body) => testWidgets(name, (t) async {
  await body(t);
  await t.pumpWidget(const SizedBox());
  await t.pump(const Duration(milliseconds: 50));
});

void main() {
  late AppDatabase db;
  late _Api api;
  setUpAll(_fonts);
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    api = _Api();
    await db.replaceProducts([
      ProductsCompanion.insert(
        id: 't',
        name: 'Футболка хлопок',
        price: const Value(430),
        quantity: const Value(12),
        barcode: const Value('4870001112223'),
        searchKey: const Value('футболка хлопок 4870001112223'),
      ),
      ProductsCompanion.insert(
        id: 's',
        name: 'Носки',
        price: const Value(120),
        quantity: const Value(30),
        barcode: const Value('4870001119999'),
        isFavorite: const Value(true),
        searchKey: const Value('носки 4870001119999'),
      ),
      ProductsCompanion.insert(
        id: 'r',
        name: 'Ремень кожаный мужской классический очень длинное название',
        price: const Value(650),
        quantity: const Value(3),
        searchKey: const Value('ремень кожаный'),
      ),
    ]);
  });
  tearDown(() async {
    ServerCapabilities.debugSet(null);
    await db.close();
  });

  void phone(WidgetTester t, {double w = 360, double h = 640, double scale = 1.0}) {
    t.view.physicalSize = Size(w * 2, h * 2);
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

  final captured = <Object?>[];

  GoRouter router({String initial = '/sale', bool realPay = false}) => GoRouter(
    initialLocation: initial,
    routes: [
      ShellRoute(
        builder: (c, s, child) => AdaptiveShell(location: s.matchedLocation, child: child),
        routes: [
          GoRoute(
            path: '/home',
            builder: (_, _) => const Center(child: Text('classic home')),
          ),
          GoRoute(
            path: '/sale',
            builder: (_, _) => const SaleEntry(classic: Center(child: Text('classic sale'))),
          ),
          GoRoute(
            path: '/checks',
            builder: (_, _) => const Center(child: Text('checks')),
          ),
          GoRoute(
            path: '/stock',
            builder: (_, _) => const Center(child: Text('stock')),
          ),
          ...simpleShellRoutes(),
        ],
      ),
      GoRoute(
        path: '/pay',
        builder: (_, s) {
          captured.add(s.extra);
          return realPay ? PaymentScreen(preset: s.extra is QuickPayPreset ? s.extra as QuickPayPreset : null) : const Scaffold(body: Center(child: Text('pay stub')));
        },
      ),
      GoRoute(path: '/paid', builder: (_, s) => withExtraPaid(s.extra)),
      GoRoute(
        path: '/scan',
        builder: (_, _) => const Scaffold(body: Text('scan stub')),
      ),
      GoRoute(
        path: '/sync',
        builder: (_, _) => const Scaffold(body: Text('sync stub')),
      ),
      GoRoute(
        path: '/debts',
        builder: (_, _) => const Scaffold(body: Text('debts stub')),
      ),
      GoRoute(
        path: '/close-shift',
        builder: (_, _) => const Scaffold(body: Text('close stub')),
      ),
      GoRoute(
        path: '/profile',
        builder: (_, _) => const Scaffold(body: Text('profile stub')),
      ),
    ],
  );

  Widget app(GoRouter r, {String lang = 'ru', bool dark = false, ShiftInfo? shift, bool simple = true, Session? session}) => ProviderScope(
    overrides: [
      databaseProvider.overrideWithValue(db),
      apiProvider.overrideWithValue(api),
      sessionProvider.overrideWith(() => _Fixed(session ?? _owner())),
      shiftProvider.overrideWith(() => _OpenShift(shift ?? const ShiftInfo(id: 'sh1'))),
      if (simple) simpleModeProvider.overrideWith(_SimpleOn.new),
    ],
    child: MaterialApp.router(
      routerConfig: r,
      theme: buildTheme(dark: false),
      darkTheme: buildTheme(dark: true),
      themeMode: dark ? ThemeMode.dark : ThemeMode.light,
      locale: Locale(lang),
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
    ),
  );

  ProviderContainer of(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(Scaffold).first));

  Future<void> openSale(WidgetTester t, {String lang = 'ru', double w = 360, double h = 640, double scale = 1.0, bool realPay = false, bool dark = false}) async {
    phone(t, w: w, h: h, scale: scale);
    captured.clear();
    await t.pumpWidget(
      app(
        router(realPay: realPay),
        lang: lang,
        dark: dark,
      ),
    );
    await t.pumpAndSettle();
    await t.tap(find.byKey(const Key('simpleChipAll')));
    await t.pumpAndSettle();
  }

  Future<void> tapTile(WidgetTester t, String id) async {
    await t.tap(find.byKey(Key('simpleTile_$id')));
    await t.pumpAndSettle();
  }

  group('оболочка и навигация', () {
    tw('профиль выбирает оболочку: нет плана — прежняя, mini — простая, ручной переключатель — простая', (t) async {
      phone(t);
      await t.pumpWidget(app(router(initial: '/checks'), simple: false));
      await t.pumpAndSettle();
      expect(find.byType(CashierShell), findsOneWidget);
      expect(find.byType(SimpleShell), findsNothing);

      // тариф «Мини» с сервера — без перезапуска
      ServerCapabilities.planNotifier.value = PlanInfo.parse({
        'plan': {
          'code': 'mini',
          'limits': {'max_staff': 1},
          'allowed_clients': ['mobile'],
        },
      });
      await t.pumpAndSettle();
      expect(find.byType(SimpleShell), findsOneWidget);
      expect(find.byType(CashierShell), findsNothing);

      // неизвестный план — как сегодня
      ServerCapabilities.planNotifier.value = PlanInfo.parse({
        'plan': {'code': 'gold'},
      });
      await t.pumpAndSettle();
      expect(find.byType(CashierShell), findsOneWidget);

      // ручной переключатель
      await of(t).read(simpleModeProvider.notifier).set(true);
      await t.pumpAndSettle();
      expect(find.byType(SimpleShell), findsOneWidget);
      expect(await db.getSetting('mode.simple'), '1', reason: 'переключатель сохранён в настройках телефона');
      await of(t).read(simpleModeProvider.notifier).set(false);
      await t.pumpAndSettle();
      expect(find.byType(CashierShell), findsOneWidget);
    });

    for (final size in [(360.0, 640.0), (411.0, 891.0)]) {
      for (final lang in ['ru', 'ky', 'uz']) {
        tw('3 вкладки + «Ещё»: переходы, кнопки ≥ 56, без переполнения (${size.$1.round()}×${size.$2.round()}, $lang)', (t) async {
          phone(t, w: size.$1, h: size.$2);
          await t.pumpWidget(app(router(), lang: lang));
          await t.pumpAndSettle();
          final tabs = [for (var i = 0; i < 4; i++) find.byKey(Key('simpleTab$i'))];
          for (final f in tabs) {
            expect(f, findsOneWidget);
            final r = t.getRect(f);
            expect(r.height, greaterThanOrEqualTo(56), reason: 'кнопка вкладки ≥ 56 px');
            expect(r.width, greaterThanOrEqualTo(56));
          }
          // вкладки не налезают друг на друга
          for (var i = 0; i < 3; i++) {
            expect(t.getRect(tabs[i]).right, lessThanOrEqualTo(t.getRect(tabs[i + 1]).left + 0.5));
          }
          for (final (i, key) in [(1, 'receiveScan'), (2, 'totalsRevenue'), (3, 'moreStock'), (0, 'simpleSearch')]) {
            await t.tap(tabs[i]);
            await t.pumpAndSettle();
            expect(find.byKey(Key(key)), findsOneWidget, reason: 'вкладка $i открывает экран с $key');
            expect(t.takeException(), isNull);
            expect(_clipped().where((s) => !s.contains('Ремень')), isEmpty, reason: 'подписи на вкладке $i не обрезаны');
          }
        });
      }
    }

    tw('в Простом режиме полоса «Не отправлено» видна всегда: «Всё отправлено», когда очередь пуста', (t) async {
      phone(t);
      await t.pumpWidget(app(router()));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('simpleUnsentText')), findsOneWidget);
      expect(find.text('Всё отправлено'), findsOneWidget);
    });

    tw('предупреждения: вход истекает через сутки, чеку 6 суток', (t) async {
      phone(t);
      await db.enqueue(
        OutboxCompanion.insert(
          id: 'old1',
          kind: 'sale',
          body: jsonEncode(OutboxMeta.attach({'a': 1}, owner: 'o@x.kg', cashboxId: 'b')),
          createdAt: DateTime.now().subtract(const Duration(days: 6)),
          total: const Value(100),
        ),
      );
      await t.pumpWidget(app(router(), session: _owner(refresh: _jwt(DateTime.now().add(const Duration(hours: 30))))));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('simpleWarn_loginExpires')), findsOneWidget);
      expect(find.textContaining('Подключитесь к интернету: вход истечёт'), findsOneWidget);
      expect(find.byKey(const Key('simpleWarn_oldSalesSoon')), findsOneWidget);
      expect(t.takeException(), isNull);
      expect(_clipped(), isEmpty);
      // счётчик «Не отправлено 1» виден, тап ведёт в «Синхронизацию»
      expect(find.text('Не отправлено: 1'), findsOneWidget);
      await t.tap(find.byKey(const Key('simpleUnsent')));
      await t.pumpAndSettle();
      expect(find.text('sync stub'), findsOneWidget);
    });
  });

  group('продажа в Простом режиме', () {
    tw('плитки с ценой; «Частые» показывают избранное; «+» и «−» у позиции', (t) async {
      phone(t);
      await t.pumpWidget(app(router()));
      await t.pumpAndSettle();
      // «Частые»: сначала только избранное
      expect(find.byKey(const Key('simpleTile_s')), findsOneWidget);
      expect(find.byKey(const Key('simpleTile_t')), findsNothing);
      await t.tap(find.byKey(const Key('simpleChipAll')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('simpleTile_t')), findsOneWidget);
      expect(find.text('430 сом'), findsOneWidget);
      expect(find.byKey(const Key('simplePayBar')), findsNothing, reason: 'чек пуст — панели оплаты нет');
      await tapTile(t, 't');
      expect(find.byKey(const Key('simplePayBar')), findsOneWidget);
      expect(find.text('Оплатить 430 сом'), findsOneWidget);
      await t.tap(find.byKey(const Key('simplePlus')));
      await t.pumpAndSettle();
      expect(find.text('Оплатить 860 сом'), findsOneWidget);
      expect(find.byKey(const Key('simpleQty')), findsOneWidget);
      await t.tap(find.byKey(const Key('simpleMinus')));
      await t.pumpAndSettle();
      expect(find.text('Оплатить 430 сом'), findsOneWidget);
      // проданный товар попал в «Частые»
      await t.tap(find.byKey(const Key('simpleChipFreq')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('simpleTile_t')), findsOneWidget);
    });

    tw('«Оплатить N сом» — одно нажатие: наличные, без выбора способа', (t) async {
      await openSale(t);
      await tapTile(t, 't');
      await t.tap(find.byKey(const Key('simplePayBtn')));
      await t.pumpAndSettle();
      expect(find.text('pay stub'), findsOneWidget);
      final preset = captured.single as QuickPayPreset;
      expect(preset.method, PayMethod.cash);
      expect(preset.auto, isTrue);
      expect(preset.received, isNull, reason: '«ровно», без сдачи');
    });

    tw('оплата проходит сама: чек в очереди, «Оплачено», кнопки способа не показывались', (t) async {
      await openSale(t, realPay: true);
      await tapTile(t, 't');
      await t.tap(find.byKey(const Key('simplePayBtn')));
      await t.pumpAndSettle();
      expect(find.text('Оплата'), findsNothing, reason: 'экран выбора способа не показан');
      expect(find.byKey(const Key('newCheckBtn')), findsOneWidget, reason: 'дошли до «Оплачено»');
      final rows = await db.pending();
      expect(rows.where((r) => r.kind == 'sale'), hasLength(1), reason: 'без связи чек сохранён в очередь');
      expect(rows.single.paymentMethod, 'cash');
      expect(rows.single.total, 430);
      expect(of(t).read(cartProvider).isEmpty, isTrue);
    });

    tw('«Получено»: быстрые суммы от итога, СДАЧА крупно, оплата с суммой', (t) async {
      await openSale(t, realPay: true);
      await tapTile(t, 't');
      await t.tap(find.byKey(const Key('simpleReceivedBtn')));
      await t.pumpAndSettle();
      for (final v in [430, 500, 1000, 5000, 10000]) {
        expect(find.byKey(Key('recvQuick_$v')), findsOneWidget, reason: 'быстрая сумма $v');
      }
      expect(find.text('Без сдачи'), findsOneWidget);
      expect(find.byKey(const Key('recvOk')), findsOneWidget);
      expect(t.widget<FilledButton>(find.byKey(const Key('recvOk'))).onPressed, isNull, reason: 'пока ничего не получено — «Принять» недоступна');
      await t.tap(find.byKey(const Key('recvQuick_500')));
      await t.pumpAndSettle();
      final change = t.widget<Text>(find.descendant(of: find.byKey(const Key('recvChange')), matching: find.byType(Text)));
      expect(change.data, 'Сдача: 70 сом');
      expect(change.style!.fontSize, greaterThanOrEqualTo(32), reason: 'сдача крупно');
      // своя сумма (системная цифровая клавиатура): мало — не хватает
      await t.enterText(find.byKey(const Key('recvField')), '300');
      await t.pumpAndSettle();
      expect(find.textContaining('Не хватает 130'), findsOneWidget);
      expect(t.widget<FilledButton>(find.byKey(const Key('recvOk'))).onPressed, isNull);
      expect(t.widget<TextField>(find.byKey(const Key('recvField'))).keyboardType, const TextInputType.numberWithOptions(decimal: true));
      await t.tap(find.byKey(const Key('recvQuick_1000')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('recvOk')));
      await t.pumpAndSettle();
      // «Оплачено»: сдача 570 очень крупно
      final big = t.widget<Text>(find.byKey(const Key('paidChange')));
      expect(big.data, 'Сдача: 570 сом');
      expect(big.style!.fontSize, 44);
      final rows = await db.pending();
      expect(rows.single.total, 430);
    });

    tw('«Торг»: «До 10» недоступна при кратной сумме, «До 50» → 400, предупреждение о крупной скидке, «Убрать торг»', (t) async {
      await openSale(t);
      await tapTile(t, 't');
      await t.tap(find.byKey(const Key('simpleHaggleBtn')));
      await t.pumpAndSettle();
      expect(t.widget<FilledButton>(find.byKey(const Key('haggleStep_10'))).onPressed, isNull, reason: '430 уже кратно 10');
      expect(t.widget<FilledButton>(find.byKey(const Key('haggleStep_50'))).onPressed, isNotNull);
      await t.tap(find.byKey(const Key('haggleStep_50')));
      await t.pumpAndSettle();
      // скидка 30 сом = 7 % — больше 5 %: вопрос вместо PIN
      expect(find.byType(AlertDialog), findsOneWidget);
      expect(find.text('Большая скидка'), findsOneWidget);
      await t.tap(find.text('Продолжить'));
      await t.pumpAndSettle();
      expect(find.text('Оплатить 400 сом'), findsOneWidget);
      expect(find.byKey(const Key('simpleHaggled')), findsOneWidget);
      expect(find.text('Торг: −30 сом'), findsOneWidget);
      expect(of(t).read(cartProvider).total, 400);
      // «Договорились за …»: 420 → скидка 10 (2,3 %) — без вопроса
      await t.tap(find.byKey(const Key('simpleHaggleBtn')));
      await t.pumpAndSettle();
      await t.enterText(find.byKey(const Key('haggleField')), '420');
      await t.pumpAndSettle();
      expect(find.text('Скидка: 10 сом (2,33 %)'), findsOneWidget);
      await t.tap(find.byKey(const Key('haggleOk')));
      await t.pumpAndSettle();
      expect(find.byType(AlertDialog), findsNothing);
      expect(find.text('Оплатить 420 сом'), findsOneWidget);
      // сумма не меньше базы — отказ
      await t.tap(find.byKey(const Key('simpleHaggleBtn')));
      await t.pumpAndSettle();
      await t.enterText(find.byKey(const Key('haggleField')), '430');
      await t.pumpAndSettle();
      expect(t.widget<FilledButton>(find.byKey(const Key('haggleOk'))).onPressed, isNull);
      await t.tap(find.byKey(const Key('haggleClear')));
      await t.pumpAndSettle();
      expect(find.text('Оплатить 430 сом'), findsOneWidget);
      expect(find.byKey(const Key('simpleHaggled')), findsNothing);
    });

    tw('«В долг»: имя + телефон (клиент создаётся здесь же) → срок → оплата долгом', (t) async {
      await openSale(t);
      await tapTile(t, 't');
      await t.tap(find.byKey(const Key('simpleOtherBtn')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('otherDebt')));
      await t.pumpAndSettle();
      // без телефона — не проходит
      await t.enterText(find.byKey(const Key('debtName')), 'Бакыт');
      await t.tap(find.byKey(const Key('debtDone')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('debtError')), findsOneWidget);
      await t.enterText(find.byKey(const Key('debtPhone')), '0555 123 456');
      await t.tap(find.byKey(const Key('debtDays_7')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('debtDone')));
      await t.pumpAndSettle();
      expect((api.posts['api/main/clients/'] as Map)['full_name'], 'Бакыт', reason: 'клиент создан на месте');
      expect(find.text('pay stub'), findsOneWidget);
      final preset = captured.single as QuickPayPreset;
      expect(preset.method, PayMethod.debt);
      expect(preset.client?.id, 'c9');
      expect(preset.debtDue, DateUtils.dateOnly(DateTime.now()).add(const Duration(days: 7)));
    });

    tw('«Другая оплата» → карта: экран оплаты открывается без автоматического проведения', (t) async {
      await openSale(t);
      await tapTile(t, 't');
      await t.tap(find.byKey(const Key('simpleOtherBtn')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('otherCard')));
      await t.pumpAndSettle();
      final preset = captured.single as QuickPayPreset;
      expect(preset.method, PayMethod.card);
      expect(preset.auto, isFalse, reason: 'карту сначала подтверждает человек');
    });

    tw('долгое нажатие на «Оплатить» — другой способ оплаты', (t) async {
      await openSale(t);
      await tapTile(t, 't');
      await t.longPress(find.byKey(const Key('simplePayBtn')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('otherCard')), findsOneWidget);
    });

    for (final size in [(360.0, 640.0), (411.0, 891.0)]) {
      for (final scale in [1.0, 1.3]) {
        for (final lang in ['ru', 'ky']) {
          tw('нет обрезания и наложения: ${size.$1.round()}×${size.$2.round()}, шрифт $scale, $lang', (t) async {
            await openSale(t, lang: lang, w: size.$1, h: size.$2, scale: scale);
            await tapTile(t, 't');
            await tapTile(t, 's');
            expect(t.takeException(), isNull);
            // подписи панели оплаты и вкладок не обрезаны (названия товаров в плитках сами сокращаются до 2 строк)
            final bar = find.byKey(const Key('simplePayBar'));
            for (final e in find.descendant(of: bar, matching: find.byType(RichText)).evaluate()) {
              final r = e.renderObject;
              if (r is RenderParagraph) expect(r.didExceedMaxLines, isFalse, reason: r.text.toPlainText());
            }
            expect(find.descendant(of: bar, matching: find.text('Носки')), findsOneWidget);
            // кнопки панели: ≥ 56, внутри экрана, не налезают друг на друга
            final keys = ['simpleMinus', 'simplePlus', 'simpleWholeCheck', 'simpleHaggleBtn', 'simpleReceivedBtn', 'simpleOtherBtn', 'simplePayBtn'];
            final rects = <String, Rect>{for (final k in keys) k: t.getRect(find.byKey(Key(k)))};
            final screen = Offset.zero & Size(size.$1, size.$2);
            for (final e in rects.entries) {
              expect(screen.contains(e.value.topLeft) && screen.contains(e.value.bottomRight - const Offset(0.5, 0.5)), isTrue, reason: '${e.key} внутри экрана: ${e.value}');
              expect(e.value.height, greaterThanOrEqualTo(55.9), reason: '${e.key} ≥ 56');
            }
            for (final a in keys) {
              for (final b in keys) {
                if (a.compareTo(b) >= 0) continue;
                expect(rects[a]!.overlaps(rects[b]!.deflate(0.5)), isFalse, reason: '$a налезает на $b: ${rects[a]} / ${rects[b]}');
              }
            }
            // панель оплаты не залезает на вкладки
            expect(t.getRect(bar).bottom, lessThanOrEqualTo(t.getRect(find.byKey(const Key('simpleTab0'))).top + 0.5));
          });
        }
      }
    }

    tw('тёмная тема: экран продажи без ошибок', (t) async {
      await openSale(t, dark: true);
      await tapTile(t, 't');
      expect(t.takeException(), isNull);
    });
  });

  group('«Новый товар», «Приход», «Итоги»', () {
    tw('быстрая карточка: фото, название, цена, количество — запрос с name+price, свой штрихкод, остаток', (t) async {
      phone(t);
      await t.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(_owner()))],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [
              AppLocalizations.delegate,
              GlobalMaterialLocalizations.delegate,
              GlobalWidgetsLocalizations.delegate,
              GlobalCupertinoLocalizations.delegate,
            ],
            home: Scaffold(body: QuickProductSheet(debugPhoto: () async => Uint8List.fromList([1, 2, 3]))),
          ),
        ),
      );
      await t.pumpAndSettle();
      // поля не налезают друг на друга
      final keys = ['quickPhoto', 'quickName', 'quickPrice', 'quickQty', 'quickSave'];
      final rects = {for (final k in keys) k: t.getRect(find.byKey(Key(k)))};
      for (final a in keys) {
        for (final b in keys) {
          if (a.compareTo(b) >= 0) continue;
          expect(rects[a]!.overlaps(rects[b]!.deflate(0.5)), isFalse, reason: '$a налезает на $b');
        }
      }
      expect(t.getRect(find.byKey(const Key('quickSave'))).height, greaterThanOrEqualTo(56));
      await t.tap(find.byKey(const Key('quickSave')));
      await t.pumpAndSettle();
      expect(api.posts.containsKey('api/main/products/create-manual/'), isFalse, reason: 'пустое название и цена — не отправляется');
      await t.enterText(find.byKey(const Key('quickName')), 'Шапка вязаная');
      await t.enterText(find.byKey(const Key('quickPrice')), '350');
      await t.enterText(find.byKey(const Key('quickQty')), '10');
      await t.tap(find.byKey(const Key('quickSave')));
      await t.pumpAndSettle();
      final body = api.posts['api/main/products/create-manual/'] as Map;
      expect(body['name'], 'Шапка вязаная');
      expect(body['price'], '350.00');
      expect(body['category_name'], isNull);
      expect((body['barcode'] as String?)?.length, 13, reason: 'внутренний штрихкод — как у кнопки «Сгенерировать»');
      expect(api.patches['api/main/products/n1/'], {'quantity': 10.0}, reason: 'остаток — как в обычной карточке');
    });

    tw('«Приход» одним сканом: штрихкод → «Сколько пришло» → кнопка 10 → «Принять» → остаток растёт', (t) async {
      phone(t);
      await t.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api), sessionProvider.overrideWith(() => _Fixed(_owner()))],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [
              AppLocalizations.delegate,
              GlobalMaterialLocalizations.delegate,
              GlobalWidgetsLocalizations.delegate,
              GlobalCupertinoLocalizations.delegate,
            ],
            home: Scaffold(body: SimpleReceiveScreen(debugScan: () async => '4870001119999')),
          ),
        ),
      );
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('receiveScan')));
      await t.pumpAndSettle();
      expect(find.text('Сколько пришло?'), findsOneWidget);
      for (final n in [1, 5, 10, 20, 50, 100]) {
        expect(find.byKey(Key('arrived_$n')), findsOneWidget);
        expect(t.getRect(find.byKey(Key('arrived_$n'))).height, greaterThanOrEqualTo(56));
      }
      expect(t.widget<FilledButton>(find.byKey(const Key('arrivedOk'))).onPressed, isNull);
      await t.tap(find.byKey(const Key('arrived_10')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('arrivedOk')));
      await t.pumpAndSettle();
      expect((await db.productById('s'))!.quantity, 40, reason: '30 + 10');
      expect(find.byKey(const Key('receiveDone')), findsOneWidget);
      expect(find.text('+10 шт'), findsOneWidget);
      expect(t.takeException(), isNull);
    });

    tw('«Итоги дня»: выручка, наличные, «Должны мне» со списком и WhatsApp, «Закрыть день»', (t) async {
      phone(t);
      await t.pumpWidget(
        app(
          router(initial: '/totals'),
          shift: const ShiftInfo(id: 'sh1', salesTotal: 12500, cashSales: 9000, expectedCash: 9500, salesCount: 7),
        ),
      );
      await t.pumpAndSettle();
      expect(find.text('${money(12500)} сом'), findsOneWidget);
      expect(find.text('${money(9500)} сом'), findsOneWidget);
      expect(
        find.descendant(of: find.byKey(const Key('totalsDebts')), matching: find.text('${money(1250)} сом')),
        findsOneWidget,
        reason: '«Должны мне»',
      );
      expect(find.text('7'), findsOneWidget);
      await t.scrollUntilVisible(find.byKey(const Key('debtor_c1')), 100, scrollable: find.byType(Scrollable).first);
      expect(find.byKey(const Key('debtor_c1')), findsOneWidget);
      expect(find.byKey(const Key('totalsCloseDay')), findsOneWidget);
      expect(t.takeException(), isNull);
      expect(_clipped(), isEmpty);
      await t.tap(find.byKey(const Key('totalsCloseDay')));
      await t.pumpAndSettle();
      expect(find.text('close stub'), findsOneWidget);
    });

    tw('«Ещё»: тариф «Мини», кнопка апгрейда открывает окно «Доступно на тарифе «Старт»»; сотрудников нет', (t) async {
      phone(t);
      api.capsPlan = {
        'code': 'mini',
        'names': {'ru': 'Мини'},
        'limits': {'max_staff': 1},
        'upgrade_plan': 'start',
      }; // «Ещё» при открытии обновляет тариф
      ServerCapabilities.debugSet([], plan: PlanInfo.parse({'plan': api.capsPlan}));
      await t.pumpWidget(app(router(initial: '/more'), simple: false));
      await t.pumpAndSettle();
      await t.scrollUntilVisible(find.byKey(const Key('morePlan')), 200, scrollable: find.byType(Scrollable).first);
      expect(find.byKey(const Key('morePlan')), findsOneWidget);
      expect(find.text('Тариф: Мини'), findsOneWidget);
      expect(find.textContaining('сотрудник', findRichText: true), findsNothing);
      await t.ensureVisible(find.byKey(const Key('moreUpgrade')));
      await t.pumpAndSettle();
      await t.tap(find.byKey(const Key('moreUpgrade')));
      await t.pumpAndSettle();
      expect(find.byKey(const Key('planLimitDialog')), findsOneWidget);
      expect(find.text('Доступно на тарифе «Старт»'), findsOneWidget);
      expect(find.byKey(const Key('planLimitGo')), findsOneWidget);
      expect(find.textContaining('+996 221 068 796'), findsOneWidget);
      expect(t.takeException(), isNull);
    });
  });
}

Widget withExtraPaid(Object? extra) {
  final info = extra as PaidInfo;
  return PaidScreen(info: info);
}
