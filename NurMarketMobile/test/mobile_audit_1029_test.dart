// 2026-10-29 (аудит мобилки): тело первой попытки = тело досылки (sold_at, cashbox), 409 по ключу = «проведено, сверить»,
// 409 price_changed (строгая цена), ключ переживает другую сумму «получено», замок загрузки каталога, очередь отдельно от каталога,
// сверка цен перед оплатой. Сервер NurCRM не трогается — всё на фейках. dotnet test не используется.
import 'dart:async';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/sync/sync_service.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';

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

Session session() => Session(email: 'a@x.kg', access: 'a', refresh: 'r', userId: 'u1', cashboxId: 'box1', cashboxName: 'Касса');

Cart cart1({double price = 100}) => Cart(lines: [CartLine(productId: 'p1', name: 'Вода', price: price, qty: 2)]);

/// Сервер продаж: один ключ — одно тело; другое тело под тем же ключом — 409 idempotency_conflict (как строгий сервер).
class StrictServer {
  final sales = <String, Map<String, dynamic>>{};
  final keyBodies = <String, String>{};
  final bodies = <Map<String, dynamic>>[];
  final headersSeen = <Map<String, String>>[];
  int loseReplies = 0;
  bool offline = false;

  /// Сколько следующих запросов отвечать 409 price_changed.
  int priceChanged = 0;
  List<Map<String, dynamic>> mismatch = const [];

  Future<dynamic> handle(String m, String p, Object? b, Map<String, String>? h, Map<String, dynamic>? q) async {
    if (offline) throw ApiException('Нет связи с сервером.');
    if (m == 'POST' && p == 'api/main/pos/checkout/') {
      final key = h!['Idempotency-Key']!;
      final body = (b as Map).cast<String, dynamic>();
      bodies.add(body);
      headersSeen.add(h);
      if (priceChanged > 0 && !sales.containsKey(key)) {
        priceChanged--;
        throw ApiException('Цена изменилась', status: 409, payload: {'code': 'price_changed', 'price_mismatch': mismatch});
      }
      final text = body.toString();
      final known = keyBodies[key];
      if (known != null && known != text) {
        throw ApiException('Idempotency-Key уже использован с другим телом', status: 409, payload: {'code': 'idempotency_conflict'});
      }
      keyBodies[key] = text;
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

void main() {
  late AppDatabase db;
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_1029_'));
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([ProductsCompanion.insert(id: 'p1', name: 'Вода', price: const Value(100), quantity: const Value(10))]);
  });
  tearDown(() => db.close());

  SyncService syncOf(FakeApi api) =>
      SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');

  group('п. 1: тело первой попытки = тело досылки', () {
    test('тайм-аут онлайн → досылка: тот же ключ и то же тело (sold_at, cashbox), строгий сервер не отвечает 409', () async {
      final server = StrictServer()..loseReplies = 1;
      final api = FakeApi(server.handle)..attach(session());
      final repo = SalesRepository(api, db);
      final r = await repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200);
      expect(r.queued, isTrue);
      expect(server.bodies.first['sold_at'], isA<String>(), reason: 'sold_at уже в первой попытке');
      expect(server.bodies.first['cashbox'], 'box1');
      await syncOf(api).runOnce();
      expect(server.bodies.length, 2);
      expect(server.bodies[1].toString(), server.bodies[0].toString(), reason: 'досылка = первая попытка');
      expect(server.sales.length, 1);
      expect((await db.pending()), isEmpty);
      expect((await db.select(db.outbox).get()).single.status, 'sent');
    });

    test('409 idempotency_conflict на досылке = «проведено, сверить»: запись отправлена, не «Не принято»', () async {
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('conflict', status: 409, payload: {'code': 'idempotency_conflict'}))
        ..attach(session());
      final offline = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session());
      await SalesRepository(offline, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200);
      expect((await db.pending()).length, 1);
      final sync = syncOf(api);
      await sync.runOnce();
      final row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'sent', reason: 'не failed: «Отправить снова» дало бы тот же 409');
      expect(row.lastError, isNotEmpty, reason: 'остаётся пометка «сверьте в Чеки»');
      expect(sync.lastError, isNotEmpty);
    });

    test('обычный 409 (запрос ещё выполняется) по-прежнему повторяется и не помечается проведённым', () async {
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('Запрос выполняется', status: 409, payload: {'detail': 'in progress'}))
        ..attach(session());
      final offline = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session());
      await SalesRepository(offline, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200);
      await syncOf(api).runOnce();
      expect((await db.select(db.outbox).get()).single.status, 'pending');
    });

    test('409 idempotency_conflict при первой оплате: результат «сверить», остаток списан, ключ забыт', () async {
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('conflict', status: 409, payload: {'code': 'idempotency_conflict'}))
        ..attach(session());
      final repo = SalesRepository(api, db);
      final r = await repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200);
      expect(r.reconcile, isTrue);
      expect(r.queued, isFalse);
      expect((await db.productById('p1'))!.quantity, 8);
      expect(await db.getSetting(SalesRepository.pendingKeySetting), '');
      expect((await db.select(db.outbox).get()).single.status, 'sent');
    });
  });

  group('п. 8: цена — строгая проверка и сверка перед оплатой', () {
    test('онлайн-оплата уходит с X-NurMarket-Strict-Price: 1', () async {
      final server = StrictServer();
      final api = FakeApi(server.handle)..attach(session());
      await SalesRepository(api, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200);
      expect(server.headersSeen.single['X-NurMarket-Strict-Price'], '1');
    });

    test('409 price_changed: продажа не проведена и не в очереди, ключ забыт, цены сервера в исключении', () async {
      final server = StrictServer()
        ..priceChanged = 1
        ..mismatch = [
          {'index': 0, 'product_id': 'p1', 'name': 'Вода', 'sent': 205, 'current': 195},
        ];
      final api = FakeApi(server.handle)..attach(session());
      final repo = SalesRepository(api, db);
      PriceChangedException? err;
      try {
        await repo.checkout(cart: cart1(price: 205), shiftId: 'sh1', method: PayMethod.cash, received: 400);
      } on PriceChangedException catch (e) {
        err = e;
      }
      expect(err, isNotNull);
      expect(err!.changes.single.oldPrice, 205);
      expect(err.changes.single.newPrice, 195);
      expect(await db.select(db.outbox).get(), isEmpty, reason: 'в очередь не ставим: продажи нет');
      expect((await db.productById('p1'))!.quantity, 10, reason: 'остаток не тронут');
      expect(await db.getSetting(SalesRepository.pendingKeySetting), '');
      // после обновления цены — обычная продажа
      final r = await repo.checkout(cart: cart1(price: 195), shiftId: 'sh1', method: PayMethod.cash, received: 400);
      expect(r.queued, isFalse);
      expect(server.sales.length, 1);
    });

    test('409 price_changed на ПОВТОР ключа (прошлая отправка без ответа) не считается «не проведено»', () async {
      final server = StrictServer()
        ..loseReplies = 1
        ..priceChanged = 0;
      final api = FakeApi(server.handle)..attach(session());
      final repo = SalesRepository(api, db);
      const client = Client('c1', 'Бакыт', '0555');
      await expectLater(repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client), throwsA(isA<CheckoutUnknownException>()));
      server.priceChanged = 1; // сервер для уже виденного ключа ответил бы повтором, но допустим странный 409
      server.sales.clear();
      await expectLater(repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client), throwsA(isNot(isA<PriceChangedException>())));
    });

    test('checkPrices: цена на сервере другая → «205 → 195»; розница/опт по типу строки', () async {
      final api = FakeApi((m, p, b, h, q) async => {'id': 'p1', 'price': '195.00', 'wholesale_price': '180.00'})..attach(session());
      final c = await SalesRepository(api, db).checkPrices(cart1(price: 205));
      expect(c.offline, isFalse);
      expect(c.changes.single.oldPrice, 205);
      expect(c.changes.single.newPrice, 195);
      expect(c.changes.single.wholesale, 180);
      // та же цена — без изменений
      final same = await SalesRepository(api, db).checkPrices(cart1(price: 195));
      expect(same.changes, isEmpty);
    });

    test('checkPrices без связи: offline = true (предупреждение «цена может быть устаревшей»)', () async {
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session());
      final c = await SalesRepository(api, db).checkPrices(cart1());
      expect(c.offline, isTrue);
      expect(c.changes, isEmpty);
    });

    test('applyPriceChanges обновляет цену строки и итог чека', () {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      final n = container.read(cartProvider.notifier);
      n.restore(cart1(price: 205));
      n.applyPriceChanges([const PriceChange('p1', 'Вода', 205, 195, retail: 195, wholesale: 0)]);
      expect(container.read(cartProvider).lines.single.price, 195);
      expect(container.read(cartProvider).total, 390);
    });

    test('hasLiveAttempt: после оборванной оплаты этого же чека — true (цены не сверяем и не меняем)', () async {
      final server = StrictServer()..loseReplies = 1;
      final api = FakeApi(server.handle)..attach(session());
      final repo = SalesRepository(api, db);
      const client = Client('c1', 'Бакыт', '0555');
      expect(await repo.hasLiveAttempt(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client), isFalse);
      await expectLater(repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client), throwsA(isA<CheckoutUnknownException>()));
      expect(await repo.hasLiveAttempt(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, client: client), isTrue);
      expect(await repo.hasLiveAttempt(cart: cart1(price: 90), shiftId: 'sh1', method: PayMethod.debt, client: client), isFalse);
    });
  });

  group('п. 9: ключ после обрыва оплаты', () {
    test('бонусы (только онлайн): обрыв → другая сумма «получено» → тот же ключ, одна продажа, то же тело', () async {
      final server = StrictServer()..loseReplies = 1;
      final api = FakeApi(server.handle)..attach(session());
      final repo = SalesRepository(api, db);
      await expectLater(
        repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200, requireOnline: true),
        throwsA(isA<CheckoutUnknownException>()),
      );
      final r = await repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 300, requireOnline: true);
      expect(r.queued, isFalse);
      expect(server.sales.length, 1, reason: 'раньше другое «получено» давало новый ключ и вторую продажу');
      expect(server.bodies[1].toString(), server.bodies[0].toString(), reason: 'повтор — то же тело, иначе 409 по ключу');
    });

    test('в долг: другая внесённая сумма — это другая продажа (новый ключ)', () async {
      final server = StrictServer()..loseReplies = 1;
      final api = FakeApi(server.handle)..attach(session());
      final repo = SalesRepository(api, db);
      const client = Client('c1', 'Бакыт', '0555');
      await expectLater(repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, received: 50, client: client), throwsA(isA<CheckoutUnknownException>()));
      await repo.checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.debt, received: 100, client: client);
      expect(server.sales.length, 2);
    });
  });

  group('п. 4: очередь отдельно от каталога, замок каталога', () {
    FakeApi catalogApi(void Function() onList, {Future<void> Function()? gate}) => FakeApi((m, p, b, h, q) async {
      if (m == 'GET' && p == 'api/main/products/list/') {
        onList();
        if (gate != null) await gate();
        await Future<void>.delayed(const Duration(milliseconds: 10));
        return {
          'results': [
            {'id': 'p1', 'name': 'Вода', 'price': '100', 'quantity': 10},
          ],
          'next': null,
        };
      }
      throw ApiException('не ожидали $m $p', status: 404);
    })..attach(session());

    test('две пересёкшиеся загрузки каталога не вычитают неотправленную продажу дважды', () async {
      final offline = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session());
      await SalesRepository(offline, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200);
      expect((await db.productById('p1'))!.quantity, 8);
      var lists = 0;
      final api = catalogApi(() => lists++);
      final repo = CatalogRepository(api, db);
      await Future.wait([repo.sync(), repo.sync(), repo.sync()]);
      expect((await db.productById('p1'))!.quantity, 8, reason: '10 минус неотправленные 2, а не 6');
      expect(lists, 2, reason: 'идущая загрузка + одна общая следующая для всех ожидающих');
    });

    test('загрузки идут по одной (одновременно на сервер не уходят две)', () async {
      var active = 0, maxActive = 0;
      final api = FakeApi((m, p, b, h, q) async {
        active++;
        if (active > maxActive) maxActive = active;
        await Future<void>.delayed(const Duration(milliseconds: 10));
        active--;
        return {'results': <Map<String, dynamic>>[], 'next': null};
      })..attach(session());
      final repo = CatalogRepository(api, db);
      await Future.wait([repo.sync(), repo.sync()]);
      expect(maxActive, 1);
    });

    test('runQueue не ждёт каталог: чек уходит, пока каталог ещё качается; «Закрыть смену» видит пустую очередь', () async {
      final gate = Completer<void>();
      final server = StrictServer();
      final api = FakeApi((m, p, b, h, q) async {
        if (m == 'GET' && p == 'api/main/products/list/') {
          await gate.future; // каталог «качается» долго
          return {'results': <Map<String, dynamic>>[], 'next': null};
        }
        return server.handle(m, p, b, h, q);
      })..attach(session());
      final sync = SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');
      server.offline = true;
      await SalesRepository(api, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200); // в очередь
      server.offline = false;
      final full = sync.runOnce(force: true); // очередь уйдёт, затем встанет на каталоге
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(await db.pending(), isEmpty);
      // новый чек встал в очередь, пока каталог качается; таймерный проход его не ждёт — закрытие смены ждёт и отправляет
      server.offline = true;
      await SalesRepository(api, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash, received: 200);
      server.offline = false;
      expect((await db.pending()).length, 1);
      await sync.runQueue().timeout(const Duration(seconds: 2));
      expect(await db.pending(), isEmpty, reason: 'очередь не заперта каталогом');
      gate.complete();
      await full;
    });
  });
}
