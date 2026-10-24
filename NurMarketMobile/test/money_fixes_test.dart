// 2026-10-22 (мобилка, деньги): проверки исправлений аудита 22.10 — двойная продажа, чужая смена, возврат со скидкой
// и долгом, 401 и очередь, другой аккаунт, двойная приёмка и ревизия. Сервер NurCRM не трогается — всё на фейках.
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/sync/outbox_meta.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/sync/sync_service.dart';
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/features/ops/return_ledger.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body, Map<String, String>? headers, Map<String, dynamic>? query);

/// Сервер в памяти: запросы идут в [handler].
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

Session session(String email, {String userId = 'u1', String cashbox = 'box1'}) =>
    Session(email: email, access: 'a', refresh: 'r', userId: userId, cashboxId: cashbox, cashboxName: 'Касса');

Cart cart1() => Cart(lines: [CartLine(productId: 'p1', name: 'Вода', price: 100, qty: 2)]);

/// Сервер продаж с идемпотентностью: один ключ — одна продажа.
class SalesServer {
  final sales = <String, Map<String, dynamic>>{};
  final bodies = <Map<String, dynamic>>[];

  /// Сколько следующих ответов «потеряется» (сервер провёл, но ответ не дошёл).
  int loseReplies = 0;
  bool offline = false;
  int? failStatus;

  Future<dynamic> handle(String m, String p, Object? b, Map<String, String>? h, Map<String, dynamic>? q) async {
    if (offline) throw ApiException('Нет связи с сервером.');
    if (failStatus != null) throw ApiException('Сессия истекла — войдите снова.', status: failStatus);
    if (m == 'POST' && p == 'api/main/pos/checkout/') {
      final key = h!['Idempotency-Key']!;
      bodies.add((b as Map).cast<String, dynamic>());
      final sale = sales.putIfAbsent(key, () => {'id': 's${sales.length + 1}', 'number': '${sales.length + 1}', 'total': '200.00'});
      if (loseReplies > 0) {
        loseReplies--;
        throw ApiException('Сервер не ответил вовремя.');
      }
      return sale;
    }
    throw ApiException('не ожидали $m $p', status: 404);
  }
}

class FixedSession extends SessionController {
  final Session s;
  FixedSession(this.s);
  @override
  Session? build() => s;
}

void main() {
  late AppDatabase db;
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_money_'));
  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  // ---------------------------------------------------------------- К-2: одна продажа на попытку оплаты
  group('К-2 оплата: один ключ на попытку', () {
    test('долг: тайм-аут → повтор той же оплаты = одна продажа', () async {
      final server = SalesServer()..loseReplies = 1;
      final api = FakeApi(server.handle)..attach(session('a@x.kg'));
      final repo = SalesRepository(api, db);
      const client = Client('c1', 'Бакыт', '0555');
      await expectLater(
        repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client),
        throwsA(isA<CheckoutUnknownException>()),
      );
      final r = await repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client);
      expect(r.queued, isFalse);
      expect(server.sales.length, 1, reason: 'повтор тем же ключом — сервер второй раз не проводит');
      // следующая (новая) продажа — новый ключ
      await repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client);
      expect(server.sales.length, 2);
    });

    test('двойное нажатие во время медленной оплаты: вторая ждёт первую и берёт её ключ', () async {
      final server = SalesServer()..loseReplies = 1;
      final api = FakeApi(server.handle)..attach(session('a@x.kg'));
      final repo = SalesRepository(api, db);
      const client = Client('c1', 'Бакыт', '0555');
      final first = repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client);
      final second = repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client);
      await expectLater(first, throwsA(isA<CheckoutUnknownException>()));
      await second;
      expect(server.sales.length, 1);
    });

    test('наличные: тайм-аут → в очередь с тем же ключом; досылка не создаёт вторую продажу', () async {
      final server = SalesServer()..loseReplies = 1;
      final api = FakeApi(server.handle)..attach(session('a@x.kg'));
      final repo = SalesRepository(api, db);
      final r = await repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200);
      expect(r.queued, isTrue);
      final sync = SyncService(db: db, sales: repo, stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      await sync.runOnce();
      expect(server.sales.length, 1);
      expect((await db.pending()), isEmpty);
      // досылка — со временем продажи и кассой, без служебных полей
      final replayed = server.bodies.last;
      expect(replayed['sold_at'], isA<String>());
      expect(replayed['cashbox'], 'box1');
      expect(replayed.containsKey(OutboxMeta.key), isFalse);
    });

    test('отказ по существу (400) — ключ забыт, следующая попытка новая', () async {
      final server = SalesServer();
      var reject = true;
      final api = FakeApi((m, p, b, h, q) async {
        if (reject) throw ApiException('Смена не открыта', status: 400);
        return server.handle(m, p, b, h, q);
      })..attach(session('a@x.kg'));
      final repo = SalesRepository(api, db);
      await expectLater(repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash), throwsA(isA<ApiException>()));
      expect(await db.getSetting(SalesRepository.pendingKeySetting), '');
      reject = false;
      await repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash);
      expect(server.sales.length, 1);
    });

    test('скидка на чек суммой не больше итога строк', () {
      final c = Cart(lines: [CartLine(productId: 'p1', name: 'Вода', price: 100, qty: 1)], orderDiscountSum: 500);
      final body = SalesRepository.buildBody(cart: c, shiftId: 's', method: PayMethod.cash);
      expect(body['order_discount_total'], '100.00');
    });
  });

  // ---------------------------------------------------------------- К-1: своя смена
  group('К-1 смена: только своя смена кассира', () {
    final rows = [
      const ShiftInfo(id: 'A', cashboxId: 'box1', cashierId: 'uA'),
      const ShiftInfo(id: 'B', cashboxId: 'box2', cashierId: 'uB'),
    ];
    test('на кассе чужая смена — своей нет, подсказка «откройте свою»', () {
      final r = ShiftRepository.pickShift(rows, cashboxId: 'box1', cashierId: 'uC');
      expect(r.shift?.id, isNull, reason: 'раньше бралась первая открытая смена компании');
      expect(r.foreignOnThisCashbox, isTrue);
    });
    test('своя смена на своей кассе', () {
      expect(ShiftRepository.pickShift(rows, cashboxId: 'box2', cashierId: 'uB').shift?.id, 'B');
    });
    test('своя смена на другой кассе — берётся вместе с кассой', () {
      final r = ShiftRepository.pickShift([rows.first, const ShiftInfo(id: 'B', cashboxId: 'box2', cashierId: 'uB')], cashboxId: 'box3', cashierId: 'uB');
      expect(r.shift?.id, 'B');
      expect(r.movedCashbox, isTrue);
    });
    test('чужая смена на другой кассе — никогда', () {
      expect(ShiftRepository.pickShift(rows, cashboxId: 'box3', cashierId: 'uC').shift, isNull);
    });
  });


  group('К-4 закрытая смена не «оживает» без связи', () {
    test('закрыли → нет связи → смены нет; кэш другого аккаунта не берётся', () async {
      var offline = false;
      var open = true;
      final api = FakeApi((m, p, b, h, q) async {
        if (offline) throw ApiException('Нет связи с сервером.');
        if (m == 'GET' && p == 'api/construction/shifts/') {
          return open ? [{'id': 'A', 'status': 'open', 'cashbox': 'box1', 'cashier': 'u1'}] : [];
        }
        if (m == 'POST' && p.endsWith('/close/')) {
          open = false;
          return {'ok': true};
        }
        throw ApiException('нет', status: 404);
      })..attach(session('a@x.kg'));
      final c = ProviderContainer(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(api),
          sessionProvider.overrideWith(() => FixedSession(session('a@x.kg'))),
        ],
      );
      addTearDown(c.dispose);
      final ctl = c.read(shiftProvider.notifier);
      await ctl.refresh();
      expect(c.read(shiftProvider).shift?.id, 'A');
      // без связи — своя последняя смена из памяти (продажи в очередь с ней)
      ctl.reset();
      offline = true;
      await ctl.refresh();
      expect(c.read(shiftProvider).shift?.id, 'A');
      offline = false;
      await ctl.refresh();
      await ctl.close(100);
      expect(c.read(shiftProvider).isOpen, isFalse);
      offline = true;
      await ctl.refresh();
      expect(c.read(shiftProvider).isOpen, isFalse, reason: 'раньше shift.id не стирался и смена «оживала»');
      // старый формат кэша (просто id) и чужой аккаунт — не берутся
      await db.setSetting('shift.id', 'A');
      await ctl.refresh();
      expect(c.read(shiftProvider).isOpen, isFalse);
      await db.setSetting('shift.id', jsonEncode({'id': 'A', 'owner': 'b@x.kg', 'cashbox': 'box1'}));
      await ctl.refresh();
      expect(c.read(shiftProvider).isOpen, isFalse);
    });

    test('смена другого кассира на этой кассе — подсказка, своей нет', () async {
      final api = FakeApi((m, p, b, h, q) async => [
        {'id': 'X', 'status': 'open', 'cashbox': 'box1', 'cashier': 'u9'},
      ])..attach(session('a@x.kg'));
      final c = ProviderContainer(
        overrides: [
          databaseProvider.overrideWithValue(db),
          apiProvider.overrideWithValue(api),
          sessionProvider.overrideWith(() => FixedSession(session('a@x.kg'))),
        ],
      );
      addTearDown(c.dispose);
      await c.read(shiftProvider.notifier).refresh();
      expect(c.read(shiftProvider).isOpen, isFalse);
      expect(c.read(shiftProvider).foreignShift, isTrue);
    });
  });

  // ---------------------------------------------------------------- К-3: возврат
  group('К-3 возврат: скидка на чек, остаток, долг', () {
    Map<String, dynamic> sale({String method = 'cash', double total = 900, double received = 0, double returnedQty = 0}) => {
      'id': 's1',
      'total': total.toStringAsFixed(2),
      'payment_method': method,
      'cash_received': received.toStringAsFixed(2),
      'items': [
        {'id': 'i1', 'product_name': 'Чай', 'quantity': '2', 'unit_price': '250.00', 'total': '500.00', 'quantity_refunded': '$returnedQty'},
        {'id': 'i2', 'product_name': 'Кофе', 'quantity': '1', 'unit_price': '500.00', 'total': '500.00'},
      ],
    };

    test('чек 1000, скидка на чек 10 % (оплачено 900) — весь чек = 900, не 1000', () async {
      final led = await ReturnLedger(db).rememberFromSale('s1', sale());
      final lines = saleLines(sale());
      expect(led!.factor, closeTo(0.9, 1e-9));
      expect(refundTotal(led, lines, [(0, 2), (1, 1)]), 900);
      expect(refundTotal(led, lines, [(0, 1)]), 225); // 250 × 0,9
    });

    test('уже возвращённое учитывается: сумма всех возвратов ≤ оплаченного', () async {
      final journal = ReturnLedger(db);
      var led = await journal.rememberFromSale('s1', sale());
      await journal.addRefund('s1', LedgerRefund(at: DateTime.now(), amount: 450, cash: 450));
      led = await journal.get('s1');
      expect(led!.remaining, 450);
      // сервер после частичного возврата отдаёт строку с остатком — снимок из журнала остаётся прежним
      final after = saleLines(sale(returnedQty: 2));
      expect(after[0].refundable, 0);
      expect(refundTotal(led, after, [(1, 1)]), 450);
      // повторное открытие не перезаписывает журнал
      final again = await journal.rememberFromSale('s1', sale(returnedQty: 2));
      expect(again!.refunds.length, 1);
    });

    test('продажа в долг: долг уменьшается, выдаётся только внесённое', () async {
      final led = await ReturnLedger(db).rememberFromSale('s1', sale(method: 'debt', total: 1000, received: 300));
      expect(led!.debt, 700);
      final split = computeRefundSplit(led, 'debt', 1000, serverDebtLeft: 700);
      expect(split.debtReduced, 700);
      expect(split.cash, 300);
      expect(split.nonCash, 0);
      // клиент уже погасил долг — тогда выдаётся деньгами
      final paidOff = computeRefundSplit(led, 'debt', 1000, serverDebtLeft: 0);
      expect(paidOff.debtReduced, 0);
      expect(paidOff.money, 1000);
      // остаток долга с сервера неизвестен — предупреждение
      expect(computeRefundSplit(led, 'debt', 500, serverDebtLeft: null).debtUnknown, isTrue);
    });

    test('безнал — на карту, наличные — наличными', () {
      expect(computeRefundSplit(null, 'transfer', 100).nonCash, 100);
      expect(computeRefundSplit(null, 'cash', 100).cash, 100);
    });

    test('ключ возврата живёт до успеха: повтор после обрыва тем же ключом', () async {
      final keys = <String>[];
      var fail = true;
      final api = FakeApi((m, p, b, h, q) async {
        keys.add((b as Map)['idempotency_key'] as String);
        if (fail) throw ApiException('Сервер не ответил вовремя.');
        return {'ok': true};
      });
      final ops = OpsRepository(api, db);
      await expectLater(ops.returnSale('s1', [('i1', 1)]), throwsA(isA<ApiException>()));
      fail = false;
      await ops.returnSale('s1', [('i1', 1)]);
      expect(keys[0], keys[1]);
      await ops.returnSale('s1', [('i1', 1)]);
      expect(keys[2], isNot(keys[1]), reason: 'после успеха — новая операция');
    });

    test('внесение: повтор после потерянного ответа не вносит дважды (?source_id=)', () async {
      final flows = <Map<String, dynamic>>[];
      var lose = true;
      final api = FakeApi((m, p, b, h, q) async {
        if (m == 'GET') return [for (final f in flows) if (f['source_id'] == q?['source_id']) f];
        flows.add((b as Map).cast<String, dynamic>());
        if (lose) {
          lose = false;
          throw ApiException('Сервер не ответил вовремя.');
        }
        return {'id': 'f'};
      });
      final ops = OpsRepository(api, db);
      Future<void> go() => ops.cashFlow(shiftId: 'sh', cashboxId: 'b', withdrawal: false, amount: 500, reason: '');
      await expectLater(go(), throwsA(isA<ApiException>()));
      await go();
      expect(flows.length, 1);
    });
  });

  // ---------------------------------------------------------------- К-5, К-6: очередь
  group('К-5/К-6 очередь', () {
    Future<void> queueSale(String owner) async {
      final server = SalesServer()..offline = true;
      final api = FakeApi(server.handle)..attach(session(owner));
      await SalesRepository(api, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash);
    }

    test('401 — очередь цела (ждёт входа), не «не принято»', () async {
      await queueSale('a@x.kg');
      final server = SalesServer()..failStatus = 401;
      final api = FakeApi(server.handle)..attach(session('a@x.kg'));
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      await sync.runOnce();
      final rows = await db.pending();
      expect(rows.length, 1, reason: 'раньше 401 переводил всю очередь в failed');
      expect(sync.authPaused, isTrue);
      // вход снова — досылка
      server.failStatus = null;
      sync.authPaused = false; // как start() после входа
      await sync.runOnce();
      expect(await db.pending(), isEmpty);
      expect(server.sales.length, 1);
    });

    test('чеки прошлого аккаунта не уходят под новым входом', () async {
      await queueSale('a@x.kg');
      final server = SalesServer();
      final api = FakeApi(server.handle)..attach(session('b@x.kg', userId: 'u2'));
      final syncB = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'b@x.kg');
      await syncB.runOnce();
      expect(server.sales, isEmpty);
      expect((await db.pending()).length, 1);
      final syncA = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      await syncA.runOnce();
      expect(server.sales.length, 1);
    });

    test('постоянный 500 не останавливает очередь навсегда', () async {
      await queueSale('a@x.kg');
      await queueSale('a@x.kg');
      var calls = 0;
      final api = FakeApi((m, p, b, h, q) async {
        if (p != 'api/main/pos/checkout/') throw ApiException('нет', status: 404);
        calls++;
        throw ApiException('Ошибка сервера 500.', status: 500);
      })..attach(session('a@x.kg'));
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      for (var i = 0; i < SyncService.maxServerAttempts * 2 + 1; i++) {
        await sync.runOnce();
      }
      final all = await db.select(db.outbox).get();
      expect(all.every((r) => r.status == 'failed'), isTrue);
      expect(calls, SyncService.maxServerAttempts * 2);
    });

    test('чужая запись видна как чужая', () async {
      await queueSale('a@x.kg');
      final row = (await db.pending()).single;
      expect(OutboxMeta.ownerOfRow(row), 'a@x.kg');
      expect(OutboxMeta.belongsTo(row, 'b@x.kg'), isFalse);
    });
  });

  // ---------------------------------------------------------------- К-7, К-10: склад кассира
  group('К-7/К-10 приёмка и ревизия', () {
    setUp(() async {
      await db.replaceProducts([ProductsCompanion.insert(id: 'p1', name: 'Вода', quantity: const Value(10))]);
    });

    test('экран и фоновая очередь одновременно — остаток прибавлен один раз', () async {
      var stock = 10.0;
      final api = FakeApi((m, p, b, h, q) async {
        await Future<void>.delayed(const Duration(milliseconds: 5));
        if (m == 'GET') return {'id': 'p1', 'quantity': stock};
        if (m == 'PATCH') stock = ((b as Map)['quantity'] as num).toDouble();
        return {'ok': true};
      })..attach(session('a@x.kg'));
      final stockRepo = StockRepository(api, db);
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: stockRepo, catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      final post = stockRepo.postReceiving([StockLine(productId: 'p1', name: 'Вода', before: 10, qty: 20)]);
      await Future<void>.delayed(const Duration(milliseconds: 1));
      await Future.wait([post, sync.runOnce()]);
      await sync.runOnce();
      expect(stock, 30);
    });

    test('ответ на PATCH потерялся — повтор не прибавляет второй раз', () async {
      var stock = 10.0;
      var lose = true;
      final api = FakeApi((m, p, b, h, q) async {
        if (m == 'GET') return {'id': 'p1', 'quantity': stock};
        if (m == 'PATCH') {
          stock = ((b as Map)['quantity'] as num).toDouble();
          if (lose) {
            lose = false;
            throw ApiException('Сервер не ответил вовремя.');
          }
        }
        return {'ok': true};
      })..attach(session('a@x.kg'));
      final stockRepo = StockRepository(api, db);
      final sent = await stockRepo.postReceiving([StockLine(productId: 'p1', name: 'Вода', before: 10, qty: 20)]);
      expect(sent, isFalse);
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: stockRepo, catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      await sync.runOnce();
      expect(stock, 30, reason: 'раньше было 50');
      expect(await db.pending(), isEmpty);
    });

    test('ревизия проводится: акт + apply; потерянный ответ apply — второй акт не создаётся', () async {
      var sessions = 0, applies = 0;
      var applied = false;
      var lose = true;
      final api = FakeApi((m, p, b, h, q) async {
        if (m == 'POST' && p == 'api/main/inventory/sessions/') return {'id': 'inv${++sessions}'};
        if (m == 'POST' && p.endsWith('/apply/')) {
          applies++;
          if (applied) throw ApiException('Акт уже проведён', status: 400);
          applied = true;
          if (lose) {
            lose = false;
            throw ApiException('Сервер не ответил вовремя.');
          }
          return {'ok': true};
        }
        if (m == 'GET' && p.startsWith('api/main/inventory/sessions/')) return {'id': 'inv1', 'status': applied ? 'applied' : 'draft'};
        throw ApiException('нет', status: 404);
      })..attach(session('a@x.kg'));
      final stockRepo = StockRepository(api, db);
      final ok = await stockRepo.applyAudit('Ревизия', {'p1': 7});
      expect(ok, isFalse, reason: 'акт создан, проведение ждёт');
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: stockRepo, catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      await sync.runOnce();
      expect(sessions, 1);
      expect(applies, 2);
      expect(await db.pending(), isEmpty);
    });

    test('ревизия без связи — запись не остаётся в очереди (пересчёт на экране)', () async {
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session('a@x.kg'));
      await expectLater(StockRepository(api, db).applyAudit('Ревизия', {'p1': 7}), throwsA(isA<ApiException>()));
      expect(await db.select(db.outbox).get(), isEmpty);
    });
  });

  test('каталог с сервера не «откатывает» остаток неотправленных продаж', () async {
    final server = SalesServer()..offline = true;
    final api0 = FakeApi(server.handle)..attach(session('a@x.kg'));
    await db.replaceProducts([ProductsCompanion.insert(id: 'p1', name: 'Вода', quantity: const Value(10))]);
    await SalesRepository(api0, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash);
    expect((await db.productById('p1'))!.quantity, 8);
    final api = FakeApi((m, p, b, h, q) async => {
      'results': [
        {'id': 'p1', 'name': 'Вода', 'quantity': 10},
      ],
      'next': null,
    });
    await CatalogRepository(api, db).sync();
    expect((await db.productById('p1'))!.quantity, 8);
  });

  test('replayBody: метка времени с поясом', () {
    final row = OutboxData(
      id: 'k',
      kind: 'sale',
      body: jsonEncode(OutboxMeta.attach({'shift': 's', 'items': []}, owner: 'a', cashboxId: 'b')),
      createdAt: DateTime(2026, 10, 22, 14, 5),
      status: 'pending',
      attempts: 0,
      total: 0,
      summary: '',
      paymentMethod: 'cash',
      display: '[]',
    );
    final body = SalesRepository.replayBody(row);
    expect(body['sold_at'], startsWith('2026-10-22T14:05:00'));
    expect(body['cashbox'], 'b');
  });
}
