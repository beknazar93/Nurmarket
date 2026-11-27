// 2026-11-26 (проверка мобилки 1.18.23 в эмуляторе): экран входа на тарифе «Мини» (device_key, 409 → «Выйти там и войти здесь?»),
// сфера «Автозапчасти» прямо из market_sphere, дробное количество в метрах/литрах (2,5 м × 50 = 125), итог смены без двойного вычитания возврата.
import 'package:drift/drift.dart' show driftRuntimeOptions;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/shop_mode.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/auth/login_screen.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

class _MemStore extends SessionStore {
  Session? saved;
  @override
  Future<Session?> load() async => saved;
  @override
  Future<void> save(Session s) async => saved = s;
  @override
  Future<void> clear() async => saved = null;
}

/// Сервер тарифа «Мини»: вход принимает только с device_key; слот телефона занят, пока не пришёл replace_device.
class _MiniApi extends ApiClient {
  _MiniApi(super.store);
  String mode = 'slot'; // slot — 409 до replace_device; nokey — 400 device_key_required; ok — пускает
  final replaceCalls = <bool>[];
  final keys = <String?>[];

  @override
  Future<Map<String, dynamic>> login(String email, String password, {String? deviceKey, String? deviceName, bool replaceDevice = false}) async {
    replaceCalls.add(replaceDevice);
    keys.add(deviceKey);
    if (mode == 'nokey') {
      throw ApiException('На тарифе Мини вход только с одного телефона: приложение должно передать device_key', status: 400, payload: {'code': 'device_key_required'});
    }
    if (mode == 'slot' && !replaceDevice) {
      throw ApiException('Слот занят', status: 409, payload: {'code': 'device_slot_taken', 'slot': 'mobile', 'device': {'name': 'Телефон Айбека'}});
    }
    attach(Session(email: email.trim(), access: 'tok', refresh: 'r'));
    return {};
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => throw ApiException('нет', status: 404);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  final ru = lookupAppLocalizations(const Locale('ru'));

  group('экран входа: слоты устройств', () {
    late AppDatabase db;
    late _MiniApi api;

    setUp(() {
      driftRuntimeOptions.dontWarnAboutMultipleDatabases = true;
      FlutterSecureStorage.setMockInitialValues({});
      db = AppDatabase(NativeDatabase.memory());
      api = _MiniApi(_MemStore());
    });
    tearDown(() => db.close());

    Future<void> pumpLogin(WidgetTester t) async {
      t.view.physicalSize = const Size(1080, 1920);
      t.view.devicePixelRatio = 420 / 160;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
      });
      final router = GoRouter(routes: [
        GoRoute(path: '/', builder: (_, _) => const LoginScreen()),
        GoRoute(path: '/cashbox', builder: (_, _) => const Scaffold(body: Text('КАССА ОТКРЫТА'))),
      ]);
      await t.pumpWidget(
        ProviderScope(
          overrides: [databaseProvider.overrideWithValue(db), sessionStoreProvider.overrideWithValue(_MemStore()), apiProvider.overrideWithValue(api)],
          child: MaterialApp.router(
            routerConfig: router,
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          ),
        ),
      );
      await t.pump();
      await t.enterText(find.byKey(const Key('loginEmail')), 'mini@nur.kg');
      await t.enterText(find.byKey(const Key('loginPassword')), 'pw');
      await t.pump();
    }

    testWidgets('409 device_slot_taken: вопрос с именем другого телефона; «Выйти там и войти здесь» — повтор с replace_device, вход выполнен', (t) async {
      await pumpLogin(t);
      await t.tap(find.widgetWithText(FilledButton, ru.signIn));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 300)));
      await t.pump();
      expect(find.text(ru.deviceSlotTitle), findsOneWidget);
      expect(find.textContaining('Телефон Айбека'), findsOneWidget);
      expect(find.text(ru.loginWrong), findsNothing, reason: 'не «неверный логин или пароль»');
      expect(api.replaceCalls, [false]);
      await t.tap(find.text(ru.deviceSlotConfirm));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 500)));
      await t.pumpAndSettle();
      expect(api.replaceCalls, [false, true]);
      expect(api.keys.every((k) => k != null && RegExp(r'^[A-Za-z0-9_-]{8,64}$').hasMatch(k)), isTrue, reason: 'device_key уходит всегда');
      expect(api.keys.toSet().length, 1, reason: 'оба раза один и тот же ключ установки');
      expect(find.text('КАССА ОТКРЫТА'), findsOneWidget);
    });

    testWidgets('отказ в вопросе: повтор не отправляется, остаёмся на экране входа без ошибки', (t) async {
      await pumpLogin(t);
      await t.tap(find.widgetWithText(FilledButton, ru.signIn));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 300)));
      await t.pump();
      await t.tap(find.text(ru.cancel));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
      await t.pumpAndSettle();
      expect(api.replaceCalls, [false]);
      expect(find.text('КАССА ОТКРЫТА'), findsNothing);
      expect(find.text(ru.loginWrong), findsNothing);
    });

    testWidgets('400 device_key_required: свой понятный текст, а не «Неверный логин или пароль»', (t) async {
      api.mode = 'nokey';
      await pumpLogin(t);
      await t.tap(find.widgetWithText(FilledButton, ru.signIn));
      await t.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 300)));
      await t.pump();
      expect(find.text(ru.loginDeviceKeyRequired), findsOneWidget);
      expect(find.text(ru.loginWrong), findsNothing);
    });
  });

  group('сфера магазина с сервера', () {
    test('company.market_sphere = autoparts принимается напрямую (а не «Не выбран»)', () async {
      final db = AppDatabase(NativeDatabase.memory());
      addTearDown(db.close);
      expect(ShopModeX.fromServer('autoparts', null), ShopMode.autoparts);
      expect(await ShopModeSync.applyServer(db, base: 'autoparts'), ShopMode.autoparts);
      expect(await ShopModeSync.read(db), ShopMode.autoparts);
      // базовые сферы работают как раньше
      expect(ShopModeX.fromServer('clothing', null), ShopMode.clothing);
      expect(ShopModeX.fromServer('grocery', 'bazaar'), ShopMode.bazaar);
    });
  });

  group('количество в строке чека', () {
    CartLine line(String unit, {bool weight = false, String? pack, double price = 50, double qty = 1}) =>
        CartLine(productId: 'p', name: 'Кабель', price: price, qty: qty, unit: unit, isWeight: weight, salePackageId: pack);

    test('2,5 м × 50 = 125, а не 3 м × 50 = 150', () {
      final l = line('м', price: 50);
      final q = l.enteredQty(2.5);
      expect(q, 2.5);
      l.qty = q;
      expect(l.gross, 125);
      expect(const Cart().add(l).total, 125);
    });

    test('дробные единицы (м, л, мл, г) принимают дробное; штуки, упаковки, пары и штука из упаковки — только целое', () {
      expect(line('л').enteredQty(1.5), 1.5);
      expect(line('мл').enteredQty(250.5), 250.5);
      expect(line('г').enteredQty(0.5), 0.5);
      expect(line('шт').enteredQty(2.5), 3);
      expect(line('').enteredQty(2.4), 2, reason: 'пустая единица — штуки');
      expect(line('упак').enteredQty(2.5), 3);
      expect(line('пара').enteredQty(1.5), 2);
      expect(line('м', pack: 'pk1').enteredQty(2.5), 3, reason: 'штука из упаковки — целая');
      expect(line('шт', weight: true).enteredQty(0.35), 0.35, reason: 'весовой — как раньше');
    });

    test('признак дробного количества у строки', () {
      expect(line('м').fractionalQty, isTrue);
      expect(line('шт').fractionalQty, isFalse);
    });
  });

  group('итог смены: возврат наличными вычитается один раз', () {
    test('продажа 150 наличными и её возврат: «Наличные» 150, «Возвраты наличными» 150, в ящике ровно начальная сумма, расчёт сходится с сервером', () {
      const shift = ShiftInfo(id: 's1', openingCash: 1000, cashSales: 150, salesTotal: 150, expectedCash: 1000, salesCount: 1);
      final b = ShiftBreakdown.build(
        shift: shift,
        // сервер: cash_sales — валовые (чек считается продажей смены), возврат — отдельным движением денег; expected_cash = 1000 + 150 − 150
        report: {'expected_cash': '1000', 'cash_sales': '150', 'debt_payments_cash': '0', 'returns_total': '150', 'returns_count': 1},
        flows: [
          {'source_kind': 'pos_sale_return', 'type': 'expense', 'amount': '150', 'payment_method': 'cash'},
        ],
        returns: (total: 150, count: 1),
      );
      expect(b.cashSales, 150);
      expect(b.returnsCash, 150);
      expect(b.calcExpected, 1000);
      expect(b.matches, isTrue);
    });
  });
}
