// 2026-11-05 (волна 0-D, аудит №4 «Мобилка»): проверки исправлений М6-xx — деньги, очередь при простое сервера, ключи операций,
// право «удаление из чека», журнал удалений, тёмная тема, часы телефона, пределы чисел, ревизия, Sunmi, штрихкод картинкой.
// Всё на фейках: база в памяти, фейковый клиент; сервер NurCRM и боевая база не трогаются.
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/print/label_bitmap.dart';
import 'package:nurmarket/core/print/escpos.dart';
import 'package:nurmarket/core/print/printer_service.dart';
import 'package:nurmarket/core/print/sunmi_builtin.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/sync/account_switch.dart';
import 'package:nurmarket/core/sync/sync_service.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/admin/admin_repository.dart';
import 'package:nurmarket/features/admin/admin_screens.dart' show adminRepoProvider;
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/sale/sale_screen.dart';
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
  @override
  Future<dynamic> patch(String path, {Object? body}) => _h('PATCH', path, body, null, null);
  @override
  Future<dynamic> delete(String path) => _h('DELETE', path, null, null, null);
}

Session session(String email, {List<String>? perms}) =>
    Session(email: email, access: 'a', refresh: 'r', userId: 'u1', userName: 'Айбек', cashboxId: 'box1', cashboxName: 'Касса', perms: perms);

Cart cart1() => Cart(lines: [CartLine(productId: 'p1', name: 'Вода', price: 100, qty: 2)]);

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

class _WidgetApi extends ApiClient {
  _WidgetApi() : super(SessionStore());
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

void main() {
  late AppDatabase db;
  setUpAll(() {
    Audit.testDirectory = Directory.systemTemp.createTempSync('nm_w0d_');
    AccountSwitch.testDirectory = Audit.testDirectory;
  });
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    Audit.resetForTests(); // у виджет-тестов свои «поддельные» часы: недописанная пачка журнала не должна вставать поперёк
  });
  tearDown(() => db.close());

  Future<void> queueSale(String owner) async {
    final api = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session(owner));
    await SalesRepository(api, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash);
  }

  // ---------------------------------------------------------------- М6-05: простой сервера не уводит очередь в «Не принято»
  group('М6-05 очередь при простое сервера', () {
    test('502 на протяжении 20 проходов: ни одна продажа не «Не принято», порядок сохранён, потом уходят обе', () async {
      await queueSale('a@x.kg');
      await queueSale('a@x.kg');
      var down = true;
      final sent = <String>[];
      final api = FakeApi((m, p, b, h, q) async {
        if (p != 'api/main/pos/checkout/') throw ApiException('нет', status: 404);
        if (down) throw ApiException('Ошибка сервера 502.', status: 502);
        sent.add(h!['Idempotency-Key']!);
        return {'id': 's${sent.length}', 'number': '${sent.length}', 'total': '200.00'};
      })..attach(session('a@x.kg'));
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      for (var i = 0; i < 20; i++) {
        await sync.runOnce();
      }
      final rows = await db.select(db.outbox).get();
      expect(rows.where((r) => r.status == 'failed'), isEmpty, reason: '502 — сервер лежит, а не отказ по существу');
      expect(rows.every((r) => r.status == 'pending'), isTrue);
      expect(sync.serverDownSince, isNotNull);
      expect(sync.serverDownWaiting, 2);
      down = false;
      await sync.runOnce();
      await sync.runOnce();
      expect(sent.length, 2);
      expect(await db.pending(), isEmpty);
      expect(sync.serverDownSince, isNull, reason: 'сервер ответил — плашка уходит');
    });

    test('«чистая» 500 после maxServer500Attempts попыток всё же уходит в «Не принято» (ошибка именно этого чека)', () async {
      await queueSale('a@x.kg');
      final api = FakeApi((m, p, b, h, q) async {
        if (p != 'api/main/pos/checkout/') throw ApiException('нет', status: 404);
        throw ApiException('Ошибка сервера 500.', status: 500);
      })..attach(session('a@x.kg'));
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      for (var i = 0; i < SyncService.maxServer500Attempts - 1; i++) {
        await sync.runOnce();
      }
      expect((await db.select(db.outbox).get()).single.status, 'pending');
      await sync.runOnce();
      expect((await db.select(db.outbox).get()).single.status, 'failed');
    });

    test('паузы нарастают: 20 с → 40 с → 80 с … не больше 10 минут', () {
      expect(SyncService.serverDownPause(1), const Duration(seconds: 20));
      expect(SyncService.serverDownPause(2), const Duration(seconds: 40));
      expect(SyncService.serverDownPause(3), const Duration(seconds: 80));
      expect(SyncService.serverDownPause(50), const Duration(minutes: 10));
    });
  });

  // ---------------------------------------------------------------- М6-04: ключи операций не затирают друг друга
  group('М6-04 ключи повтора операций', () {
    test('возврат A без исхода → возврат B → повтор A идёт ТЕМ ЖЕ ключом', () async {
      final keys = <String, List<String>>{};
      var fail = true;
      final api = FakeApi((m, p, b, h, q) async {
        keys.putIfAbsent(p, () => []).add((b as Map)['idempotency_key'] as String);
        if (fail) throw ApiException('Сервер не ответил вовремя.');
        return {'ok': true};
      });
      final ops = OpsRepository(api, db);
      await expectLater(ops.returnSale('sA', [('i1', 1)]), throwsA(isA<ApiException>()));
      await expectLater(ops.returnSale('sB', [('i2', 1)]), throwsA(isA<ApiException>()));
      await expectLater(ops.returnSale('sA', [('i1', 1)]), throwsA(isA<ApiException>()));
      final a = keys['api/main/pos/sales/sA/return/']!;
      expect(a.length, 2);
      expect(a[0], a[1], reason: 'раньше ключ A затирался возвратом B — второй возврат на те же деньги');
      expect(keys['api/main/pos/sales/sB/return/']!.single, isNot(a[0]));
      fail = false;
      await ops.returnSale('sA', [('i1', 1)]);
      await ops.returnSale('sA', [('i1', 1)]);
      final after = keys['api/main/pos/sales/sA/return/']!;
      expect(after[2], a[0], reason: 'успех — ключ тот же, что у неудачных попыток');
      expect(after[3], isNot(a[0]), reason: 'после успеха следующая операция новая');
    });

    test('в ячейке не больше 5 записей; прежний формат (одна запись) читается', () async {
      await db.setSetting('ops.pending.return', jsonEncode({'key': 'old-key', 'sig': 'sX|i:1', 'at': DateTime.now().toIso8601String()}));
      final ops = OpsRepository(FakeApi((m, p, b, h, q) async => {}), db);
      expect(await ops.pendingKey('return', 'sX|i:1'), 'old-key');
      expect(ops.lastKeyReused, isTrue);
      for (var i = 0; i < 8; i++) {
        await ops.pendingKey('return', 'sig$i');
      }
      final list = jsonDecode((await db.getSetting('ops.pending.return'))!) as List;
      expect(list.length, 5);
    });
  });

  // ---------------------------------------------------------------- М6-06: бумажный чек сходится
  group('М6-06 бумага сходится', () {
    test('весовые 4,875 + 22,638: строки 4,88 + 22,64, подытог 27,52, «Округление» 0,01, ИТОГО 27,51', () {
      final cart = Cart(lines: [
        CartLine(productId: 'a', name: 'А', price: 13, qty: 0.375, weightFlag: true),
        CartLine(productId: 'b', name: 'Б', price: 66, qty: 0.343, weightFlag: true),
      ]);
      expect(cart.total, 27.51);
      expect(cart.printedSubtotal, 27.52);
      expect(cart.paperRounding, 0.01);
      final r = Receipt.fromCart(session: session('a@x.kg'), cart: cart, number: '1', payment: 'cash', received: 30);
      expect(r.weightRounding, 0.01);
      expect(r.subtotal, 27.52);
      // подытог напечатанных строк − скидка − округление = ИТОГО
      expect(roundMoney(r.subtotal - r.discount - r.weightRounding), r.total);
      final text = receiptLines(r, 32).map((l) => l.text).join('\n');
      expect(text, contains('Округление'));
      expect(text, contains('-0,01'));
      expect(roundingSigned(0.01), '−0,01');
      expect(roundingSigned(-0.02), '+0,02');
    });

    test('штучные и скидка: округления нет, бумага сходится сама', () {
      final cart = Cart(lines: [CartLine(productId: 'a', name: 'А', price: 12.5, qty: 3)], orderDiscountPercent: 10);
      expect(cart.paperRounding, 0);
      final r = Receipt.fromCart(session: session('a@x.kg'), cart: cart, number: '1', payment: 'cash', received: 50);
      expect(roundMoney(r.subtotal - r.discount - r.weightRounding), r.total);
    });
  });

  // ---------------------------------------------------------------- тела продаж: скидки и бонусы (тесты 5–6 из отчёта)
  group('тело продажи', () {
    test('скидка на чек процентом + бонусы: order_discount_total = скидка корзины, итог корзины сходится с телом', () {
      final base = Cart(lines: [CartLine(productId: 'a', name: 'А', price: 80, qty: 3)], orderDiscountPercent: 10);
      expect(base.orderDiscount, 24);
      // как PaymentScreen._payCart: бонусы 15 сом списываются отдельной скидкой суммой
      final pay = Cart(lines: base.lines, orderDiscountSum: base.orderDiscount + 15);
      final body = SalesRepository.buildBody(cart: pay, shiftId: 's', method: PayMethod.cash, received: 300);
      expect(body['order_discount_total'], '39.00');
      expect(body.containsKey('order_discount_percent'), isFalse);
      expect(pay.total, 240 - 39);
    });

    test('две строки одного товара с ручными скидками + строка с акцией: сумма тела = итог корзины', () {
      final cart = Cart(lines: [
        CartLine(productId: 'a', name: 'А', price: 50, qty: 2)..discount = 10,
        CartLine(productId: 'a', name: 'А', price: 50, qty: 3)..discount = 5,
        CartLine(productId: 'b', name: 'Б', price: 20, qty: 4),
      ]);
      final body = SalesRepository.buildBody(cart: cart, shiftId: 's', method: PayMethod.cash, received: 500);
      var sum = 0.0;
      for (final it in (body['items'] as List).cast<Map<String, dynamic>>()) {
        sum += double.parse(it['qty'] as String) * double.parse(it['price'] as String) - (double.tryParse('${it['discount'] ?? '0'}') ?? 0);
      }
      expect(roundMoney(sum), cart.total);
      expect((body['items'] as List).length, 2, reason: 'строки одного товара по одной цене — одной позицией');
    });
  });

  // ---------------------------------------------------------------- М6-09: ревизия — три знака веса
  test('М6-09 ревизия: в quantity_fact уходят 3 знака (1,235 кг)', () async {
    Map<String, dynamic>? created;
    final api = FakeApi((m, p, b, h, q) async {
      if (m == 'POST' && p == 'api/main/inventory/sessions/') {
        created = (b as Map).cast<String, dynamic>();
        return {'id': 'inv1'};
      }
      if (m == 'POST' && p.endsWith('/apply/')) return {'status': 'applied'};
      if (m == 'GET') return {'id': 'inv1', 'status': 'applied'};
      throw ApiException('нет', status: 404);
    })..attach(session('a@x.kg'));
    await StockRepository(api, db).applyAudit('Ревизия', {'w1': 1.235, 'p2': 7});
    final items = (created!['items'] as List).cast<Map<String, dynamic>>();
    expect(items.firstWhere((e) => e['product_id'] == 'w1')['quantity_fact'], '1.235');
    expect(items.firstWhere((e) => e['product_id'] == 'p2')['quantity_fact'], '7.00', reason: 'целые и сотые — как раньше');
  });

  // ---------------------------------------------------------------- М6-07: часы телефона
  group('М6-07 часы телефона', () {
    tearDown(ServerClock.reset);

    test('заголовок Date сервера → поправка; расхождение больше 5 минут включает плашку и поправляет время продажи', () {
      final phone = DateTime.utc(2025, 1, 1, 12);
      ServerClock.note('Wed, 05 Nov 2026 10:00:00 GMT', phoneNow: phone);
      expect(ServerClock.wrong.value, isTrue);
      final fixed = ServerClock.fix(DateTime(2025, 1, 1, 12));
      expect(fixed.year, 2026);
      expect(fixed.month, 11);
    });

    test('расхождение меньше допуска — время телефона как есть, плашки нет; мусор в заголовке не ломает', () {
      final phone = DateTime.utc(2026, 11, 5, 10, 0, 30);
      ServerClock.note('Thu, 05 Nov 2026 10:00:00 GMT', phoneNow: phone);
      expect(ServerClock.wrong.value, isFalse);
      final t = DateTime(2026, 11, 5, 10);
      expect(ServerClock.fix(t), t);
      ServerClock.note('не дата');
      ServerClock.note(null);
      expect(ServerClock.wrong.value, isFalse);
    });

    test('продажа берёт время сервера: sold_at в досылке — по часам сервера, а не телефона', () async {
      ServerClock.offset = const Duration(days: 400); // телефон отстаёт на 400 суток
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session('a@x.kg'));
      await SalesRepository(api, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash);
      final row = (await db.select(db.outbox).get()).single;
      // время продажи записи очереди = время продажи при досылке (sold_at): по часам сервера, а не телефона
      expect(row.createdAt.isAfter(DateTime.now().add(const Duration(days: 300))), isTrue);
      expect(SalesRepository.replayBody(row)['sold_at'], isA<String>());
    });
  });

  // ---------------------------------------------------------------- М6-16: пределы чисел
  group('М6-16 числовые поля', () {
    TextEditingValue run(NumKind k, String old, String nw) => numberLimit(k).formatEditUpdate(TextEditingValue(text: old), TextEditingValue(text: nw));

    test('штрихкод 13 цифр не принимается целиком в количестве, весе и сумме', () {
      var t = '';
      for (final ch in '4870001159955'.split('')) {
        t = run(NumKind.qty, t, t + ch).text;
      }
      expect(t.length, 6);
      t = '';
      for (final ch in '4870001159955'.split('')) {
        t = run(NumKind.weight, t, t + ch).text;
      }
      expect(t, '487');
      t = '';
      for (final ch in '4870001159955'.split('')) {
        t = run(NumKind.money, t, t + ch).text;
      }
      expect(t.length, 10);
    });

    test('дробная часть — до 3 знаков; запятая и точка принимаются', () {
      expect(run(NumKind.weight, '1,23', '1,235').text, '1,235');
      expect(run(NumKind.weight, '1,235', '1,2356').text, '1,235');
      expect(run(NumKind.qty, '12', '12.5').text, '12.5');
    });

    testWidgets('в окне «Количество» большое число переспрашивается («Точно 5000?»)', (t) async {
      double? result = -1;
      await t.pumpWidget(
        MaterialApp(
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: Builder(
            builder: (c) => Scaffold(body: TextButton(onPressed: () async => result = await askNumber(c, title: 'Кол', kind: NumKind.qty), child: const Text('go'))),
          ),
        ),
      );
      await t.tap(find.text('go'));
      await t.pumpAndSettle();
      await t.enterText(find.byType(TextField), '5000');
      await t.tap(find.text('Готово'));
      await t.pumpAndSettle();
      expect(find.textContaining('Точно 5'), findsOneWidget);
      await t.tap(find.text('Отмена'));
      await t.pumpAndSettle();
      expect(result, isNull);
    });
  });

  // ---------------------------------------------------------------- М6-01/02/23: право «удаление из чека» и журнал
  group('М6-01/02 очистка чека и «−» — право, вопрос, журнал', () {
    final productA = ProductsCompanion.insert(id: 'a', name: 'Вода', price: const Value(85), quantity: const Value(50), barcode: const Value('4870001112223'), searchKey: const Value('вода 4870001112223'));
    final productB = ProductsCompanion.insert(id: 'b', name: 'Сок', price: const Value(140), quantity: const Value(50), barcode: const Value('4870001119999'), searchKey: const Value('сок 4870001119999'));

    setUpAll(_fonts);

    Widget app(Session s) => ProviderScope(
      overrides: [
        databaseProvider.overrideWithValue(db),
        apiProvider.overrideWithValue(_WidgetApi()),
        sessionProvider.overrideWith(() => _Fixed(s)),
        shiftProvider.overrideWith(_OpenShift.new),
      ],
      child: MaterialApp(
        theme: buildTheme(dark: false),
        locale: const Locale('ru'),
        supportedLocales: AppLocalizations.supportedLocales,
        localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
        home: const Scaffold(body: SaleScreen()),
      ),
    );

    void phone(WidgetTester t) {
      t.view.physicalSize = const Size(720, 1280);
      t.view.devicePixelRatio = 2.0;
      addTearDown(() {
        t.view.resetPhysicalSize();
        t.view.resetDevicePixelRatio();
      });
    }

    ProviderContainer of(WidgetTester t) => ProviderScope.containerOf(t.element(find.byType(SaleScreen)));

    Future<void> fill(WidgetTester t) async {
      await db.replaceProducts([productA, productB]);
      await t.pumpWidget(app(_who));
      await t.pumpAndSettle();
      final products = await db.searchProducts('');
      final cart = of(t).read(cartProvider.notifier);
      cart.add(products.firstWhere((p) => p.id == 'a'), qty: 3);
      cart.add(products.firstWhere((p) => p.id == 'b'));
      await t.pumpAndSettle();
    }

    final l = lookupAppLocalizations(const Locale('ru'));

    testWidgets('кассир без права: «Очистить чек» не очищает и говорит «Нет доступа»', (t) async {
      _who = session('c@x.kg', perms: const []);
      phone(t);
      await fill(t);
      await t.tap(find.byIcon(Icons.more_horiz_rounded));
      await t.pumpAndSettle();
      await t.tap(find.text(l.clearCheck));
      await t.pumpAndSettle();
      expect(of(t).read(cartProvider).lines.length, 2, reason: 'чек не тронут');
      expect(find.textContaining(l.noAccess), findsWidgets);
      expect(await of(t).read(adminRepoProvider).deletions(), isEmpty);
    });

    testWidgets('владелец: вопрос → «Очистить» → каждая строка в «Удалено из чека»', (t) async {
      _who = session('o@x.kg', perms: const ['can_view_market_delete_cart_item']);
      phone(t);
      await fill(t);
      await t.tap(find.byIcon(Icons.more_horiz_rounded));
      await t.pumpAndSettle();
      await t.tap(find.text(l.clearCheck));
      await t.pumpAndSettle();
      expect(find.text(l.clearCheckAsk), findsOneWidget);
      await t.tap(find.descendant(of: find.byType(AlertDialog), matching: find.text(l.clearCheck)));
      await t.pumpAndSettle();
      expect(of(t).read(cartProvider).isEmpty, isTrue);
      final log = await of(t).read(adminRepoProvider).deletions();
      expect(log.length, 2);
      expect(log.map((e) => e.name).toSet(), {'Вода', 'Сок'});
      expect(log.every((e) => e.reason == l.clearCheck), isTrue);
    });

    testWidgets('«Отмена» в вопросе — чек остаётся, журнал пуст', (t) async {
      _who = session('o@x.kg', perms: const ['can_view_market_delete_cart_item']);
      phone(t);
      await fill(t);
      await t.tap(find.byIcon(Icons.more_horiz_rounded));
      await t.pumpAndSettle();
      await t.tap(find.text(l.clearCheck));
      await t.pumpAndSettle();
      await t.tap(find.text(l.cancel));
      await t.pumpAndSettle();
      expect(of(t).read(cartProvider).lines.length, 2);
      expect(await of(t).read(adminRepoProvider).deletions(), isEmpty);
    });

    testWidgets('«−» без права не уменьшает; с правом — уменьшает и пишет разницу в журнал', (t) async {
      _who = session('c@x.kg', perms: const []);
      phone(t);
      await fill(t);
      await t.tap(find.byIcon(Icons.receipt_long_rounded));
      await t.pumpAndSettle();
      await t.tap(find.byIcon(Icons.remove).first);
      await t.pumpAndSettle();
      expect(of(t).read(cartProvider).lines.first.qty, 3, reason: 'без права «−» ничего не меняет');
      expect(await of(t).read(adminRepoProvider).deletions(), isEmpty);
    });

    testWidgets('«−» с правом: количество 3 → 2, в журнале «Количество уменьшено» на 1 шт.', (t) async {
      _who = session('o@x.kg', perms: const ['can_view_market_delete_cart_item']);
      phone(t);
      await fill(t);
      await t.tap(find.byIcon(Icons.receipt_long_rounded));
      await t.pumpAndSettle();
      await t.tap(find.byIcon(Icons.remove).first);
      await t.pumpAndSettle();
      expect(of(t).read(cartProvider).lines.first.qty, 2);
      final log = await of(t).read(adminRepoProvider).deletions();
      expect(log.single.qty, 1);
      expect(log.single.sum, 85);
      expect(log.single.reason, l.qtyReduced);
    });
  });

  // ---------------------------------------------------------------- М6-03: журнал удалений привязан к аккаунту и не стирается выходом
  group('М6-03 журнал удалений при выходе', () {
    test('запись остаётся после wipeAll, видна своему аккаунту и не видна чужому', () async {
      final apiA = FakeApi((m, p, b, h, q) async => {})..attach(session('a@x.kg'));
      final admin = AdminRepository(apiA, db);
      await admin.logDeletion(DeletionRow(DateTime(2026, 11, 5), 'Вода', 2, 170, 'Покупатель передумал', 'Айбек'));
      await AccountSwitch.wipeAll(db, directory: Audit.testDirectory, deleteSecret: (_) async {});
      expect((await AdminRepository(apiA, db).deletions()).single.name, 'Вода');
      final apiB = FakeApi((m, p, b, h, q) async => {})..attach(session('b@x.kg'));
      expect(await AdminRepository(apiB, db).deletions(), isEmpty, reason: 'чужой аккаунт чужой журнал не видит');
    });

    test('аудит: записи «delete» с логином остаются после выхода, остальное стирается; чужие не показываются', () async {
      Audit.resetForTests();
      Audit.currentUser = 'Айбек';
      Audit.currentOwner = 'a@x.kg';
      await Audit.log('delete', 'Вода × 2 · передумал', amount: 170);
      await Audit.log('sale', 'Продажа 200');
      await Audit.wipeAll(directory: Audit.testDirectory);
      Audit.currentOwner = 'a@x.kg';
      final mine = await Audit.read();
      expect(mine.map((e) => e.kind), ['delete']);
      Audit.currentOwner = 'b@x.kg';
      expect(await Audit.read(), isEmpty);
      Audit.currentOwner = '';
      await Audit.wipeAll(directory: Audit.testDirectory);
    });
  });

  // ---------------------------------------------------------------- М6-27, М6-31
  test('М6-27 после удаления строки та же весовая этикетка снова добавляется без вопроса', () {
    final lineA = CartLine(productId: 'a', name: 'А', price: 10, qty: 1);
    final lineB = CartLine(productId: 'b', name: 'Б', price: 10, qty: 1);
    final m = ScaleLabelMemory()
      ..add('2000012007345', line: lineA)
      ..add('2000013001239', line: lineB);
    m.syncWithCart(cartEmpty: false, lines: [lineB]);
    expect(m.contains('2000012007345'), isFalse, reason: 'строку удалили — этикетку можно снова');
    expect(m.contains('2000013001239'), isTrue);
    m.syncWithCart(cartEmpty: true);
    expect(m.isEmpty, isTrue);
  });

  test('М6-31 поиск «5» не тянет товары по доп. штрихкоду; 4+ цифры — находит', () async {
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'x', name: 'Чай', searchKey: const Value('чай'), altBarcodes: const Value('5551234567890|1')),
      ProductsCompanion.insert(id: 'y', name: 'Кофе 5', searchKey: const Value('кофе 5')),
    ]);
    expect((await db.searchProducts('5')).map((p) => p.id), ['y']);
    expect((await db.searchProducts('5551234567890')).map((p) => p.id), ['x']);
    expect((await db.searchProducts('50%')).map((p) => p.id), isEmpty, reason: '% — не шаблон');
  });

  // ---------------------------------------------------------------- М6-11/12: тёмная тема
  group('М6-11/12 тёмная тема', () {
    test('выбранный сегмент и чип отличимы от фона окна (≥ 3:1), текст на нём читается (≥ 4,5:1)', () {
      final th = buildTheme(dark: true);
      const p = Palette(true);
      expect(ChartColors.contrast(th.colorScheme.secondaryContainer, p.window), greaterThanOrEqualTo(3));
      expect(ChartColors.contrast(th.colorScheme.secondaryContainer, th.colorScheme.onSecondaryContainer), greaterThanOrEqualTo(4.5));
      expect(ChartColors.contrast(th.chipTheme.selectedColor!, p.window), greaterThanOrEqualTo(3));
      expect(ChartColors.contrast(th.chipTheme.selectedColor!, p.onAction), greaterThanOrEqualTo(4.5));
      expect(ChartColors.contrast(p.action, p.window), greaterThanOrEqualTo(3));
    });

    test('красный, зелёный и жёлтый текст в тёмной теме читаются на панели и окне (≥ 4,5:1)', () {
      const p = Palette(true);
      for (final c in [p.danger, p.success, p.warn]) {
        expect(ChartColors.contrast(c, p.panel), greaterThanOrEqualTo(4.5));
        expect(ChartColors.contrast(c, p.window), greaterThanOrEqualTo(4.5));
      }
    });

    test('в lib нет TextStyle(color: T.danger|T.success|T.warn): текст берёт цвет из context.pal', () {
      final bad = <String>[];
      final re = RegExp(r'\bT\.(danger|success|warn)\b(?!\.)');
      for (final f in Directory('lib').listSync(recursive: true).whereType<File>()) {
        final path = f.path.replaceAll('\\', '/');
        if (!path.endsWith('.dart') || path.endsWith('core/theme/theme.dart') || path.endsWith('core/theme/tokens.dart')) continue;
        final lines = f.readAsLinesSync();
        for (var i = 0; i < lines.length; i++) {
          if (lines[i].contains('TextStyle(') && re.hasMatch(lines[i])) bad.add('$path:${i + 1}');
        }
      }
      expect(bad, isEmpty, reason: 'в тёмной теме T.* даёт контраст 2,2–2,6:1');
    });
  });

  // ---------------------------------------------------------------- Sunmi и штрихкод картинкой
  group('Sunmi: встроенный принтер — только на SUNMI и по настройке', () {
    tearDown(SunmiBuiltIn.resetForTests);

    final fixed = Receipt.fromCart(session: session('a@x.kg'), cart: cart1(), number: '7', payment: 'cash', received: 200);
    Receipt receipt() => fixed;

    test('на SUNMI с включённой настройкой печатает строки чека средствами Sunmi, байты ESC/POS не уходят', () async {
      SunmiBuiltIn.isSunmiOverride = () async => true;
      List<PrintLine>? got;
      var drawerPulses = 0;
      SunmiBuiltIn.printOverride = (lines, {String? qr, bool drawer = false}) async {
        if (lines.isNotEmpty) got = lines;
        if (drawer) drawerPulses++;
      };
      var bytesSent = 0;
      final service = PrinterService(null)..sendOverride = (c, b) async => bytesSent++;
      await service.printReceipt(receipt(), config: const PrinterConfig(sunmi: true, drawer: true), afterSale: true);
      expect(got, isNotNull);
      expect(got!.any((l) => l.text.contains('ИТОГО')), isTrue);
      expect(bytesSent, 0);
      expect(drawerPulses, 1, reason: 'ящик после наличной продажи — один раз, средствами Sunmi');
    });

    test('не SUNMI: настройка не действует — печать идёт обычным путём (ESC/POS)', () async {
      SunmiBuiltIn.isSunmiOverride = () async => false;
      var sunmiCalls = 0;
      SunmiBuiltIn.printOverride = (lines, {String? qr, bool drawer = false}) async => sunmiCalls++;
      Uint8List? bytes;
      final service = PrinterService(null)..sendOverride = (c, b) async => bytes = b;
      await service.printReceipt(receipt(), config: const PrinterConfig(kind: PrinterKind.network, address: '192.168.0.9', sunmi: true));
      expect(sunmiCalls, 0);
      expect(bytes, isNotNull);
    });

    test('настройка выключена (по умолчанию) — даже на SUNMI обычный принтер', () async {
      SunmiBuiltIn.isSunmiOverride = () async => true;
      var sunmiCalls = 0;
      SunmiBuiltIn.printOverride = (lines, {String? qr, bool drawer = false}) async => sunmiCalls++;
      Uint8List? bytes;
      final service = PrinterService(null)..sendOverride = (c, b) async => bytes = b;
      await service.printReceipt(receipt(), config: const PrinterConfig(kind: PrinterKind.network, address: '192.168.0.9'));
      expect(sunmiCalls, 0);
      expect(bytes, isNotNull);
      expect(const PrinterConfig().sunmi, isFalse);
    });

    test('настройка хранится в JSON; готовность принтера — и для Sunmi без адреса', () {
      final c = PrinterConfig.fromJson(jsonEncode(const PrinterConfig(sunmi: true).toJson()));
      expect(c.sunmi, isTrue);
      expect(c.ready, isTrue);
      expect(PrinterConfig.fromJson('{"kind":"network","address":"1.2.3.4"}').sunmi, isFalse);
    });

    test('«Повторить печать» после обрыва не открывает ящик второй раз (М6-28)', () async {
      var pulses = 0;
      var fail = true;
      final service = PrinterService(null)
        ..sendOverride = (c, b) async {
          // в задании есть импульс ящика ESC p
          for (var i = 0; i + 1 < b.length; i++) {
            if (b[i] == 0x1B && b[i + 1] == 0x70) pulses++;
          }
          if (fail) throw const PrinterException(PrinterProblem.stalled, '', true);
        };
      const cfg = PrinterConfig(kind: PrinterKind.network, address: '192.168.0.9', drawer: true);
      await expectLater(service.printReceipt(receipt(), config: cfg, afterSale: true), throwsA(isA<PrinterException>()));
      fail = false;
      await service.printReceipt(receipt(), config: cfg, afterSale: true);
      expect(pulses, 1, reason: 'в первом (оборванном) задании импульс был, во втором — нет');
    });
  });

  group('М5-03: штрихкод картинкой для этикетки (пакет barcode)', () {
    test('EAN-13 рисуется картинкой нужного размера с чёрными полосами; QR/Code128 для прочего текста', () {
      final bmp = renderBarcodeBitmap('4870001159955', widthDots: 200, heightDots: 60)!;
      expect(bmp.width, 200);
      expect(bmp.height, 60);
      expect(bmp.rowBytes, 25);
      expect(bmp.blackCount, greaterThan(500));
      expect(barcodeFor('4870001159955').name, contains('EAN'));
      expect(barcodeFor('ABC-123').name, contains('128'));
      expect(renderBarcodeBitmap('', widthDots: 100, heightDots: 40), isNull);
    });

    test('TSPL: заголовок SIZE/GAP, BITMAP с размером в байтах, данные инвертированы (0 — чёрная точка), PRINT с копиями', () {
      final bmp = renderBarcodeBitmap('4870001159955', widthDots: 64, heightDots: 8)!;
      final out = tsplBitmapLabel(widthMm: 58, heightMm: 40, bitmap: bmp, copies: 3);
      final head = latin1.decode(out.sublist(0, 80), allowInvalid: true);
      expect(head, startsWith('SIZE 58 mm,40 mm'));
      expect(head, contains('BITMAP 0,0,8,8,0,'));
      final tail = latin1.decode(out.sublist(out.length - 14), allowInvalid: true);
      expect(tail, contains('PRINT 3,1'));
      final dataStart = latin1.decode(out, allowInvalid: true).indexOf('BITMAP 0,0,8,8,0,') + 'BITMAP 0,0,8,8,0,'.length;
      for (var i = 0; i < bmp.bytes.length; i++) {
        expect(out[dataStart + i], ~bmp.bytes[i] & 0xFF);
      }
    });
  });
}

Session _who = const Session(email: 'o@x.kg', access: 'a', refresh: 'r');
