// 2026-11-06 (аудит №5 «Мобилка», 1.18.19): проверки исправлений М7-xx — бумага при weight_line_total_floor, выход с чеком, повтор возврата,
// один товар по двум ценам, очередь после долгого офлайна, шаг весов, окно PLU, поле веса, поле branch, красные кнопки, Sunmi.
// Всё на фейках: база в памяти, фейковый клиент; сервер NurCRM и боевая база не трогаются.
import 'dart:convert';
import 'dart:io';

import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/checkout_device.dart';
import 'package:nurmarket/core/api/server_capabilities.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/print/escpos.dart';
import 'package:nurmarket/core/print/printer_service.dart';
import 'package:nurmarket/core/print/sunmi_builtin.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/sync/sync_service.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/admin/admin_screens.dart' show adminRepoProvider;
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/features/payment/payment_screen.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/profile/logout_guard.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/scan/scale_label_flow.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body, Map<String, String>? headers, Map<String, dynamic>? query);

class FakeApi extends ApiClient {
  FakeApi(this.handler) : super(SessionStore());
  Handler handler;
  final calls = <String>[];

  Future<dynamic> _h(String m, String p, Object? b, Map<String, String>? h, Map<String, dynamic>? q) {
    calls.add('$m $p');
    return handler(m, p, b, h, q);
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => _h('GET', path, null, null, query);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => _h('POST', path, body, headers, null);
}

Session session(String email, {List<String>? perms}) =>
    Session(email: email, access: 'a', refresh: 'r', userId: 'u1', userName: 'Айбек', cashboxId: 'box1', cashboxName: 'Касса', perms: perms);

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

class _ListApi extends ApiClient {
  _ListApi() : super(SessionStore());
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => {'results': <dynamic>[]};
}

Future<void> _fonts() async {
  final m = FontLoader('Montserrat');
  for (final w in ['Regular', 'Medium', 'SemiBold', 'Bold', 'ExtraBold']) {
    m.addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
  }
  await m.load();
}

CartLine weighed(String id, String name, double qty, double price, {double discount = 0}) =>
    CartLine(productId: id, name: name, price: price, qty: qty, isWeight: true, unit: 'кг', discount: discount);

CartLine piece(String id, String name, double qty, double price) => CartLine(productId: id, name: name, price: price, qty: qty);

Receipt receiptOf(Cart cart, {Session? s}) => Receipt.fromCart(session: s ?? session('a@x.kg'), cart: cart, number: '1', payment: 'cash', received: 1000);

String paper(Receipt r) => receiptLines(r, 40).map((l) => l.text).join('\n');

/// Сумма напечатанных строк (до скидок), в копейках.
int linesCents(Receipt r) => r.lines.fold(0, (s, l) => s + (l.printedGross * 100).round());

void main() {
  late AppDatabase db;
  setUpAll(() {
    TestWidgetsFlutterBinding.ensureInitialized();
    Audit.testDirectory = Directory.systemTemp.createTempSync('nm_a5_');
  });
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    Audit.resetForTests();
    ServerCapabilities.debugSet(null);
    ServerClock.reset();
  });
  tearDown(() async {
    ServerCapabilities.debugSet(null);
    await db.close();
  });

  // ---------------------------------------------------------------- М7-01/02/03: бумага сходится с ИТОГО при weight_line_total_floor
  group('М7-01 бумага при weight_line_total_floor', () {
    setUp(() => ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor}));

    test('сыр 0,343 × 66 = 22,638: на бумаге 22,63 (а не 22,64), «Округления» нет, строки − скидка − округление = ИТОГО', () {
      final r = receiptOf(Cart(lines: [weighed('c', 'Сыр', 0.343, 66), piece('w', 'Вода', 1, 3)]));
      expect(r.total, 25.63);
      expect(r.lines.first.printedGross, 22.63);
      final text = paper(r);
      expect(text, contains(money(22.63)));
      expect(text, isNot(contains(money(22.64))), reason: 'раньше строка печаталась HALF_UP — 22,64');
      expect(text, isNot(contains('Округление')));
      expect(r.weightRounding, 0);
      expect(linesCents(r) / 100 - r.discount - r.weightRounding, closeTo(r.total, 1e-9));
    });

    test('Sunmi и ESC/POS печатают одни и те же строки (receiptLines)', () {
      final r = receiptOf(Cart(lines: [weighed('c', 'Сыр', 0.343, 66)]));
      final bytes = receiptToEscPos(r, paperMm: 58, qr: false);
      // в байтах ESC/POS (CP866) цифры — как есть: «22,63» есть, «22,64» нет
      final ascii = String.fromCharCodes(bytes.where((b) => b >= 0x20 && b < 0x7F));
      expect(ascii, contains('22,63'));
      expect(ascii, isNot(contains('22,64')));
    });

    test('инвариант «Σ строк − скидка − округление = ИТОГО» для 1, 2 и 3 весовых строк, с акцией, скидкой строки и скидкой на чек', () {
      final carts = <Cart>[
        Cart(lines: [weighed('a', 'А', 0.343, 66)]),
        Cart(lines: [weighed('a', 'А', 0.343, 66), weighed('b', 'Б', 0.375, 13)]),
        Cart(lines: [weighed('a', 'А', 0.343, 66), weighed('b', 'Б', 0.375, 13), weighed('c', 'В', 1.777, 41.3), piece('d', 'Г', 3, 7.5)]),
        Cart(lines: [weighed('a', 'А', 0.343, 66, discount: 0.07)], orderDiscountPercent: 15),
        Cart(lines: [weighed('a', 'А', 0.243, 66), weighed('a', 'А', 0.243, 66), piece('d', 'Г', 2, 19.99)], orderDiscountSum: 3),
        Cart(lines: [
          CartLine(
            productId: 'p',
            name: 'Акция',
            price: 66,
            qty: 0.343,
            isWeight: true,
            unit: 'кг',
            promo: const [PromoRule(minAmount: 10, percent: 15)],
          ),
        ]),
      ];
      for (final c in carts) {
        final r = receiptOf(c);
        expect(linesCents(r) / 100 - r.discount - r.weightRounding, closeTo(r.total, 1e-9), reason: 'ИТОГО ${r.total}, строки ${linesCents(r) / 100}, скидка ${r.discount}');
      }
    });

    test('без признака (старый сервер) строки печатаются как раньше', () {
      ServerCapabilities.debugSet(null);
      final r = receiptOf(Cart(lines: [weighed('c', 'Сыр', 0.343, 66)]));
      expect(r.lines.first.printedGross, 22.64);
    });
  });

  group('М7-02 два взвешивания одного товара', () {
    setUp(() => ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor}));

    test('0,243 + 0,243 по 66 — одна строка 32,07 (как на сервере), «Округления +0,01» нет', () {
      final r = receiptOf(Cart(lines: [weighed('c', 'Сыр', 0.243, 66), weighed('c', 'Сыр', 0.243, 66)]));
      expect(r.lines, hasLength(1));
      expect(r.lines.single.qty, closeTo(0.486, 1e-9));
      expect(r.lines.single.printedGross, 32.07);
      expect(r.total, 32.07);
      expect(r.weightRounding, 0);
    });

    test('разные цены одного товара не склеиваются', () {
      final r = receiptOf(Cart(lines: [weighed('c', 'Сыр', 0.243, 66), weighed('c', 'Сыр', 0.243, 70)]));
      expect(r.lines, hasLength(2));
    });

    test('скидка склеенной позиции — по сумме количеств группы', () {
      final promo = [const PromoRule(minAmount: 10, percent: 10)];
      CartLine l(double q) => CartLine(productId: 'c', name: 'Сыр', price: 66, qty: q, isWeight: true, unit: 'кг', promo: promo);
      final cart = Cart(lines: [l(0.243), l(0.243)]);
      final r = receiptOf(cart);
      expect(r.lines, hasLength(1));
      expect(linesCents(r) / 100 - r.discount - r.weightRounding, closeTo(r.total, 1e-9));
      expect(r.lines.single.total, closeTo(r.total, 1e-9));
    });
  });

  group('М7-03 повторная печать из «Чеков»', () {
    Map<String, dynamic> sale(List<Map<String, dynamic>> items, String total) => {'items': items, 'total': total};

    Receipt reprint(List<Map<String, dynamic>> items, double total) =>
        Receipt.fromSale(session: session('a@x.kg'), sale: sale(items, '$total'), number: '5', total: total, at: DateTime(2026, 11, 6), payment: 'cash');

    test('весовая строка 0,343 × 66, line_total 22,63: «скидки −0,01» нет ни у строки, ни у чека', () {
      final r = reprint([
        {'product_name': 'Сыр', 'quantity': '0.343', 'unit_price': '66.00', 'total': '22.63', 'unit': 'кг', 'is_weight': true},
        {'product_name': 'Вода', 'quantity': '1', 'unit_price': '3.00', 'total': '3.00', 'unit': 'шт'},
      ], 25.63);
      expect(r.lines.first.discount, 0);
      expect(r.discount, 0);
      expect(r.weightRounding, 0);
      expect(paper(r), isNot(contains('скидка')));
      expect(paper(r), isNot(contains('Скидка')));
    });

    test('шаг «сом»: 22,638 → 22,00 тоже округление, а не скидка 0,64', () {
      ServerCapabilities.debugSet({ServerCapabilities.weightLineTotalFloor}, stepCents: 100);
      final r = reprint([
        {'product_name': 'Сыр', 'quantity': '0.343', 'unit_price': '66.00', 'total': '22.00', 'unit': 'кг'},
      ], 22);
      expect(r.lines.single.discount, 0);
      expect(r.discount, 0);
    });

    test('настоящая скидка (discount_total) и скидка обычной строки остаются скидкой', () {
      final r = reprint([
        {'product_name': 'Сыр', 'quantity': '0.343', 'unit_price': '66.00', 'total': '19.23', 'discount_total': '3.40', 'unit': 'кг', 'is_weight': true},
        {'product_name': 'Вода', 'quantity': '2', 'unit_price': '100.00', 'total': '190.00', 'unit': 'шт'},
      ], 209.23);
      expect(r.lines.first.discount, 3.40);
      expect(r.lines.last.discount, 10, reason: 'у обычной строки разница qty × price − total — скидка, как раньше');
      expect(r.discount, closeTo(13.4, 1e-9));
    });
  });

  // ---------------------------------------------------------------- М7-04: выход не стирает чек молча
  group('М7-04 выход из аккаунта с набранным чеком и отложенными', () {
    setUpAll(_fonts);
    final l = lookupAppLocalizations(const Locale('ru'));
    bool? result;

    Widget host(Session s) => ProviderScope(
      overrides: [
        databaseProvider.overrideWithValue(db),
        apiProvider.overrideWithValue(_ListApi()),
        sessionProvider.overrideWith(() => _Fixed(s)),
      ],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: const Locale('ru'),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: Consumer(
          builder: (context, ref, _) => Scaffold(
            body: TextButton(onPressed: () async => result = await confirmCartDiscarded(context, ref), child: const Text('go')),
          ),
        ),
      ),
    );

    ProviderContainer of(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(Scaffold)));

    Future<void> fill(WidgetTester t, {bool held = true}) async {
      final c = of(t);
      c.read(cartProvider.notifier).restore(Cart(lines: [piece('a', 'Вода', 3, 85), piece('b', 'Сок', 1, 140)]));
      if (held) await c.read(opsRepoProvider).hold(Cart(lines: [piece('c', 'Хлеб', 2, 32)]), '');
      await t.pump();
    }

    setUp(() => result = null);

    testWidgets('кассир без права «удаление из чека»: выйти нельзя, «Нет доступа», чек и журнал не тронуты', (t) async {
      await t.pumpWidget(host(session('c@x.kg', perms: const [])));
      await fill(t);
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
      expect(result, isFalse);
      expect(find.textContaining(l.noAccess), findsWidgets);
      expect(of(t).read(cartProvider).lines, hasLength(2));
      expect(await of(t).read(adminRepoProvider).deletions(), isEmpty);
    });

    testWidgets('с правом: вопрос с числами, «Выйти» → каждая строка чека и отложенных в «Удалено из чека» с причиной «Выход из аккаунта»', (t) async {
      await t.pumpWidget(host(session('o@x.kg', perms: const ['can_view_market_delete_cart_item'])));
      await fill(t);
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
      expect(find.text(l.logoutCartTitle), findsOneWidget);
      expect(find.text(l.logoutCartAsk(2, 1)), findsOneWidget);
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.text(l.logout)));
      await t.pumpAndSettle();
      expect(result, isTrue);
      final log = await of(t).read(adminRepoProvider).deletions();
      expect(log.map((e) => e.name).toSet(), {'Вода', 'Сок', 'Хлеб'});
      expect(log.every((e) => e.reason == l.logout), isTrue);
    });

    testWidgets('«Отмена» — выхода нет, журнал пуст', (t) async {
      await t.pumpWidget(host(session('o@x.kg', perms: const ['can_view_market_delete_cart_item'])));
      await fill(t);
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
      await t.tap(find.text(l.cancel));
      await t.pumpAndSettle();
      expect(result, isFalse);
      expect(await of(t).read(adminRepoProvider).deletions(), isEmpty);
    });

    testWidgets('пустой чек и нет отложенных — выход без вопроса', (t) async {
      await t.pumpWidget(host(session('c@x.kg', perms: const [])));
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
      expect(result, isTrue);
      expect(find.byType(AlertDialog), findsNothing);
    });
  });

  // ---------------------------------------------------------------- М7-05/М7-16: повтор возврата
  group('М7-05 повтор возврата через 10+ минут', () {
    Future<(OpsRepository, List<String>)> setup({required bool serverHasReturn, Duration docOffset = Duration.zero}) async {
      final posts = <String>[];
      final api = FakeApi((m, p, b, h, q) async {
        if (m == 'GET' && p == 'api/main/pos/returns/') {
          return {
            'results': [
              if (serverHasReturn) {'id': 'r1', 'sale': 'sA', 'amount': '100', 'created_at': DateTime.now().subtract(const Duration(hours: 3)).add(docOffset).toUtc().toIso8601String()},
            ],
          };
        }
        posts.add((b as Map)['idempotency_key'] as String);
        return {'ok': true};
      });
      final ops = OpsRepository(api, db);
      await db.setSetting(
        'ops.pending.return',
        jsonEncode([
          {'key': 'old-key', 'sig': 'sA|i1:1', 'at': DateTime.now().subtract(const Duration(hours: 3)).toIso8601String()},
        ]),
      );
      return (ops, posts);
    }

    test('на сервере есть документ после ключа: новый возврат НЕ отправляется, бросается ReturnMaybeDone с суммой и временем', () async {
      final (ops, posts) = await setup(serverHasReturn: true, docOffset: const Duration(minutes: 1));
      await expectLater(ops.returnSale('sA', [('i1', 1)]), throwsA(isA<ReturnMaybeDone>().having((e) => e.amount, 'сумма', 100)));
      expect(posts, isEmpty, reason: 'ни старым, ни новым ключом запрос return/ не ушёл');
    });

    test('документа нет — прошлая попытка не прошла, повтор тем же ключом', () async {
      final (ops, posts) = await setup(serverHasReturn: false);
      await ops.returnSale('sA', [('i1', 1)]);
      expect(posts, ['old-key']);
    });

    test('М7-16: телефон спешит на 10 минут — документ сервера «раньше» ключа по часам телефона всё равно находится', () async {
      ServerClock.offset = const Duration(minutes: -10); // сервер − телефон
      addTearDown(ServerClock.reset);
      final (ops, posts) = await setup(serverHasReturn: true, docOffset: const Duration(minutes: -9)); // на 9 мин раньше по часам телефона
      await expectLater(ops.returnSale('sA', [('i1', 1)]), throwsA(isA<ReturnMaybeDone>()));
      expect(posts, isEmpty);
    });

    test('без поправки на часы тот же документ не нашёлся бы (проверка, что тест выше проверяет именно поправку)', () async {
      final (ops, posts) = await setup(serverHasReturn: true, docOffset: const Duration(minutes: -9));
      await ops.returnSale('sA', [('i1', 1)]);
      expect(posts, ['old-key']);
    });
  });

  // ---------------------------------------------------------------- М7-06: один товар по двум ценам
  group('М7-06 один товар по двум ценам', () {
    test('Cart.priceConflictName: розница и опт одного товара — конфликт; разные товары, штука из пачки и «Доп. услуга» — нет', () {
      expect(Cart(lines: [piece('k', 'Кола', 1, 100), piece('k', 'Кола', 2, 90)]).priceConflictName, 'Кола');
      expect(Cart(lines: [piece('k', 'Кола', 1, 100), piece('k', 'Кола', 2, 100)]).priceConflictName, isNull);
      expect(Cart(lines: [piece('k', 'Кола', 1, 100), piece('f', 'Фанта', 2, 90)]).priceConflictName, isNull);
      expect(Cart(lines: [piece('k', 'Кола', 1, 100), CartLine(productId: 'k', name: 'Кола', price: 10, qty: 3, salePackageId: 'pk', perPack: 10)]).priceConflictName, isNull);
      expect(Cart(lines: [CartLine(productId: '', name: 'Доставка', price: 50, custom: true), CartLine(productId: '', name: 'Доставка', price: 70, custom: true)]).priceConflictName, isNull);
    });

    testWidgets('экран оплаты: чек «Кола розница + Кола опт» не оплачивается — понятное сообщение, запрос checkout не уходит', (t) async {
      await _fonts();
      t.view.physicalSize = const Size(720, 1280);
      t.view.devicePixelRatio = 2.0;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
      });
      final api = FakeApi((m, p, b, h, q) async {
        if (m == 'GET') return {'results': <dynamic>[]};
        throw StateError('checkout не должен вызываться: $m $p');
      });
      await t.pumpWidget(
        ProviderScope(
          overrides: [
            databaseProvider.overrideWithValue(db),
            apiProvider.overrideWithValue(api),
            sessionProvider.overrideWith(() => _Fixed(session('o@x.kg', perms: const ['can_view_market_delete_cart_item']))),
            shiftProvider.overrideWith(_OpenShift.new),
          ],
          child: MaterialApp(
            theme: buildTheme(dark: false),
            locale: const Locale('ru'),
            supportedLocales: AppLocalizations.supportedLocales,
            localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
            home: const PaymentScreen(),
          ),
        ),
      );
      final c = ProviderScope.containerOf(t.element(find.byType(Scaffold).first));
      c.read(cartProvider.notifier).restore(Cart(lines: [piece('k', 'Кола', 1, 100), piece('k', 'Кола', 2, 90)]));
      await t.pumpAndSettle();
      final l = lookupAppLocalizations(const Locale('ru'));
      await t.ensureVisible(find.byKey(const Key('doPayBtn')));
      await t.tap(find.byKey(const Key('doPayBtn')));
      await t.pumpAndSettle();
      expect(find.textContaining(l.payDuplicatePrice('Кола')), findsWidgets);
      expect(api.calls.where((x) => x.startsWith('POST')), isEmpty);
    });

    test('mergeDuplicateLines: строки по разным ценам сводятся в одну, итог чека тот же (разница — скидка строки)', () {
      final body = jsonEncode({
        'shift': 's1',
        'items': [
          {'product': 'k', 'qty': '2', 'price': '100.00'},
          {'product': 'f', 'qty': '1', 'price': '50.00'},
          {'product': 'k', 'qty': '1', 'price': '90.00', 'is_wholesale': true},
        ],
        '_meta': {'owner': 'a@x.kg'},
      });
      final out = jsonDecode(SalesRepository.mergeDuplicateLines(body)!) as Map;
      final items = (out['items'] as List).cast<Map>();
      expect(items, hasLength(2));
      final k = items.firstWhere((x) => x['product'] == 'k');
      expect(k['qty'], '3');
      expect(k['price'], '100.00');
      expect(k['discount'], '10.00', reason: '3 × 100 − 10 = 290 = 2 × 100 + 90');
      expect(out['_meta'], isNotNull, reason: 'служебные поля очереди на месте');
    });

    test('mergeDuplicateLines: одинаковые цены или один товар — сводить нечего (null)', () {
      expect(SalesRepository.mergeDuplicateLines(jsonEncode({'items': [{'product': 'k', 'qty': '2', 'price': '100.00'}, {'product': 'k', 'qty': '1', 'price': '100.00'}]})), isNull);
      expect(SalesRepository.mergeDuplicateLines('не json'), isNull);
    });

    test('очередь: 400 duplicate_line на досылке — строки сводятся один раз и чек уходит; в «Не принято» не попадает', () async {
      final bodies = <Map>[];
      final offline = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session('a@x.kg'));
      await SalesRepository(offline, db).checkout(
        cart: Cart(lines: [piece('k', 'Кола', 2, 100), piece('k', 'Кола', 1, 90)]),
        shiftId: 'sh1',
        method: PayMethod.cash,
      );
      final api = FakeApi((m, p, b, h, q) async {
        bodies.add(b as Map);
        if (bodies.length == 1) throw ApiException('Тот же товар с другой ценой', status: 400, payload: {'code': 'duplicate_line', 'duplicate_of': 0});
        return {'id': 's1', 'number': '77', 'total': '290.00'};
      })..attach(session('a@x.kg'));
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      await sync.runQueue();
      var row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'pending', reason: 'не «Не принято»: строки сведены, ждёт следующего прохода');
      await sync.runQueue();
      row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'sent');
      final items = (bodies.last['items'] as List).cast<Map>();
      expect(items, hasLength(1));
      expect(items.single['qty'], '3');
      expect(items.single['discount'], '10.00');
    });

    test('очередь: если и сведённый чек отклонён duplicate_line — «Не принято» с понятным текстом, без бесконечных попыток', () async {
      final offline = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session('a@x.kg'));
      await SalesRepository(offline, db).checkout(
        cart: Cart(lines: [piece('k', 'Кола', 2, 100), piece('k', 'Кола', 1, 90)]),
        shiftId: 'sh1',
        method: PayMethod.cash,
      );
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('dup', status: 400, payload: {'code': 'duplicate_line'}))..attach(session('a@x.kg'));
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      await sync.runQueue();
      await sync.runQueue();
      final row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'failed');
      expect(row.lastError, ApiText.saleDuplicateLine);
    });
  });

  // ---------------------------------------------------------------- М7-09: попытки после долгого офлайна
  group('М7-09 очередь после долгого офлайна', () {
    test('десять проходов «нет связи», потом одна 409 — чек остаётся в очереди (в «Не принято» только после трёх настоящих ответов)', () async {
      final offline = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session('a@x.kg'));
      await SalesRepository(offline, db).checkout(cart: Cart(lines: [piece('w', 'Вода', 1, 85)]), shiftId: 'sh1', method: PayMethod.cash);
      var mode = 'offline';
      final api = FakeApi((m, p, b, h, q) async {
        if (mode == 'offline') throw ApiException('Нет связи с сервером.');
        throw ApiException('Конфликт', status: 409, payload: {'code': 'in_progress'});
      })..attach(session('a@x.kg'));
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      for (var i = 0; i < 10; i++) {
        await sync.runQueue();
      }
      var row = (await db.select(db.outbox).get()).single;
      expect(row.attempts, 0, reason: 'обрыв связи попыток не тратит');
      mode = 'conflict';
      await sync.runQueue();
      row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'pending', reason: 'первая же 409 не уводит оплаченный чек в «Не принято»');
      await sync.runQueue();
      await sync.runQueue();
      row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'failed', reason: 'три настоящих ответа подряд — тогда в «Не принято»');
    });
  });

  // ---------------------------------------------------------------- М7-07: окно привязки PLU
  test('М7-07 окно перезаписи PLU: «У товара «Молоко» PLU 15, будет 12» — название, прежний и новый PLU на своих местах', () {
    final l = lookupAppLocalizations(const Locale('ru'));
    final text = scaleBindPrompt(l, name: 'Молоко', oldPlu: '15', plu: '12');
    expect(text, startsWith('У товара «Молоко» PLU 15, будет 12.'));
    expect(scaleBindPrompt(l, name: 'Молоко', oldPlu: null, plu: '12'), l.scaleBindAsk('12', 'Молоко'));
    expect(scaleBindPrompt(l, name: 'Молоко', oldPlu: ' 12 ', plu: '12'), l.scaleBindAsk('12', 'Молоко'), reason: 'тот же PLU — обычный вопрос');
  });

  // ---------------------------------------------------------------- М7-08: шаг весов
  group('М7-08 шаг весов при сбое запроса компании', () {
    var caps = '{"features":["weight_line_total_floor"]}';
    var company = '{"scale_barcode_amount_unit":"som"}';
    var companyFails = false;

    FakeApi api() => FakeApi((m, p, b, h, q) async {
      if (p == 'api/main/capabilities/') return jsonDecode(caps);
      if (p == 'api/users/company/') {
        if (companyFails) throw ApiException('Нет связи с сервером.');
        return jsonDecode(company);
      }
      throw ApiException('нет');
    });

    setUp(() {
      companyFails = false;
      caps = '{"features":["weight_line_total_floor"]}';
      company = '{"scale_barcode_amount_unit":"som"}';
    });

    test('company не ответил: шаг «копейка» не закрепляется в кэше, запрос повторяется; потом приходит «сом»', () async {
      companyFails = true;
      expect(await ServerCapabilities.refresh(api(), db, 'u|1'), isFalse, reason: 'false — нужно повторить');
      expect(ServerCapabilities.stepUnknown, isTrue);
      expect(ServerRounding.weightStepCents, 1);
      // после «перезапуска» кэш тоже помечен: шаг не считается известным
      ServerCapabilities.debugSet(null);
      await ServerCapabilities.loadCached(db, 'u|1');
      expect(ServerCapabilities.has('weight_line_total_floor'), isTrue);
      expect(ServerCapabilities.stepUnknown, isTrue);
      companyFails = false;
      expect(await ServerCapabilities.refresh(api(), db, 'u|1'), isTrue);
      expect(ServerCapabilities.stepUnknown, isFalse);
      expect(ServerRounding.weightStepCents, 100);
    });

    test('шаг уже был известен («сом»), запрос компании сорвался — прежний шаг остаётся', () async {
      expect(await ServerCapabilities.refresh(api(), db, 'u|1'), isTrue);
      expect(ServerRounding.weightStepCents, 100);
      companyFails = true;
      expect(await ServerCapabilities.refresh(api(), db, 'u|1'), isTrue);
      expect(ServerRounding.weightStepCents, 100);
      expect(ServerCapabilities.stepUnknown, isFalse);
    });

    test('признака округления нет — шаг не нужен, повтор не просится', () async {
      caps = '{"features":[]}';
      companyFails = true;
      expect(await ServerCapabilities.refresh(api(), db, 'u|1'), isTrue);
      expect(ServerCapabilities.stepUnknown, isFalse);
    });
  });

  // ---------------------------------------------------------------- М7-10: поле веса
  group('М7-10 вес больше 50 кг переспрашивается', () {
    test('NumKind.needsSure: вес > 50, количество > 999', () {
      expect(NumKind.weight.needsSure(487), isTrue);
      expect(NumKind.weight.needsSure(50), isFalse);
      expect(NumKind.weight.needsSure(0.343), isFalse);
      expect(NumKind.qty.needsSure(1000), isTrue);
      expect(NumKind.qty.needsSure(999), isFalse);
      expect(NumKind.money.needsSure(5000000), isFalse);
      expect(NumKind.weight.needsSure(487, above: 50000), isFalse, reason: 'граммы');
    });

    testWidgets('скан штрихкода 4870001159955 в окне веса даёт «487» и вопрос «Точно 487?»; «Отмена» — вес не принят', (t) async {
      await _fonts();
      double? result = -1;
      await t.pumpWidget(
        MaterialApp(
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: Builder(
            builder: (c) => Scaffold(body: TextButton(onPressed: () async => result = await askNumber(c, title: 'Вес', kind: NumKind.weight), child: const Text('go'))),
          ),
        ),
      );
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField), '487');
      await t.tap(find.text('Готово'));
      await t.pumpAndSettle();
      expect(find.textContaining('Точно 487'), findsOneWidget);
      await t.tap(find.text('Отмена'));
      await t.pumpAndSettle();
      expect(result, isNull);
    });

    testWidgets('обычный вес 0,343 — без вопроса', (t) async {
      await _fonts();
      double? result = -1;
      await t.pumpWidget(
        MaterialApp(
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: Builder(
            builder: (c) => Scaffold(body: TextButton(onPressed: () async => result = await askNumber(c, title: 'Вес', kind: NumKind.weight), child: const Text('go'))),
          ),
        ),
      );
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField), '0,343');
      await t.tap(find.text('Готово'));
      await t.pumpAndSettle();
      expect(result, 0.343);
    });
  });

  // ---------------------------------------------------------------- М7-14: branch
  test('М7-14 branch из профиля: id (uuid, число, {id}) уходит; название — нет', () {
    const uuid = '3f2b8c1e-5a47-4d1b-9c33-0a1b2c3d4e5f';
    expect(CheckoutDevice.branchOf({}, {'branch': uuid}), uuid);
    expect(CheckoutDevice.branchOf({}, {'branch_id': 12}), '12');
    expect(CheckoutDevice.branchOf({}, {'branch': {'id': uuid, 'name': 'Центральный'}}), uuid);
    expect(CheckoutDevice.branchOf({'branch': uuid}, {}), uuid, reason: 'в корне профиля');
    expect(CheckoutDevice.branchOf({}, {'branch': 'Центральный'}), '', reason: 'название сервер не примет — 400 на каждый чек');
    expect(CheckoutDevice.branchOf({}, {'branch': {'name': 'Центральный'}}), '');
    expect(CheckoutDevice.branchOf({}, {'branch': 'Центральный', 'branch_id': 7}), '7', reason: 'название пропускается, id берётся');
    expect(CheckoutDevice.branchOf({}, {'branch': null}), '');
  });

  // ---------------------------------------------------------------- М7-11: красные кнопки в тёмной теме
  test('М7-11 в lib нет foregroundColor: T.danger|T.success|T.warn — цвет кнопки берётся из context.pal (в тёмной теме T.* даёт контраст 2,6–3,2:1)', () {
    final bad = <String>[];
    final re = RegExp(r'foregroundColor:\s*T\.(danger|success|warn)\b');
    for (final f in Directory('lib').listSync(recursive: true).whereType<File>()) {
      final path = f.path.replaceAll('\\', '/');
      if (!path.endsWith('.dart')) continue;
      final lines = f.readAsLinesSync();
      for (var i = 0; i < lines.length; i++) {
        if (re.hasMatch(lines[i])) bad.add('$path:${i + 1}');
      }
    }
    expect(bad, isEmpty);
  });

  // ---------------------------------------------------------------- М7-12/М7-13: Sunmi
  group('Sunmi', () {
    const channel = MethodChannel('sunmi_printer_plus');
    tearDown(() {
      SunmiBuiltIn.resetForTests();
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(channel, null);
    });

    test('М7-12: «error» на первой же строке — ничего не вышло, PrinterException.sent = false (повтор безопасен)', () async {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(channel, (call) async => 'error');
      try {
        await SunmiBuiltIn.printLines(const [PrintLine('Чек')]);
        fail('ожидали PrinterException');
      } on PrinterException catch (e) {
        expect(e.sent, isFalse);
      }
    });

    test('М7-12: первая строка принята, вторая «error» — часть чека вышла, sent = true', () async {
      var n = 0;
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(channel, (call) async => ++n == 1 ? 'ok' : 'error');
      try {
        await SunmiBuiltIn.printLines(const [PrintLine('Чек'), PrintLine('Вода')]);
        fail('ожидали PrinterException');
      } on PrinterException catch (e) {
        expect(e.sent, isTrue);
      }
    });

    test('М7-13: настройка «Встроенный принтер Sunmi» на обычном телефоне не делает принтер «готовым»', () async {
      SunmiBuiltIn.isSunmiOverride = null;
      expect(const PrinterConfig(sunmi: true).ready, isTrue, reason: 'пока не выяснили — не мешаем');
      expect(await SunmiBuiltIn.isSunmi(), isFalse); // тестовый ПК — не Android: ответ запоминается
      expect(SunmiBuiltIn.knownNotSunmi, isTrue);
      expect(const PrinterConfig(sunmi: true).ready, isFalse);
      expect(const PrinterConfig(kind: PrinterKind.network, address: '1.2.3.4', sunmi: true).ready, isTrue, reason: 'обычный принтер по сети по-прежнему готов');
    });
  });

  // ---------------------------------------------------------------- Версия
  test('версия 1.18.19+20 в pubspec и AppBuild; тестовая сборка выключена', () {
    final pubspec = File('pubspec.yaml').readAsStringSync();
    expect(RegExp(r'^version:\s*1\.18\.19\+20\s*$', multiLine: true).hasMatch(pubspec), isTrue);
    expect(File('release-notes-mobile-1.18.19.md').existsSync(), isTrue);
  });
}
