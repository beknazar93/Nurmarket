// 2026-11-05 (ревью волн 0–3, мобилка): проверки исправлений М-1…М-6 — журнал без владельца, счёт 5xx в базе, возврат со старым ключом,
// «Округление» от серверного итога. Всё на фейках: база в памяти, фейковый клиент; сервер NurCRM и боевая база не трогаются.
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/print/sunmi_builtin.dart';
import 'package:nurmarket/core/sync/sync_service.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body, Map<String, String>? headers, Map<String, dynamic>? query);

class FakeApi extends ApiClient {
  FakeApi(this.handler) : super(SessionStore());
  Handler handler;
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => handler('GET', path, null, null, query);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => handler('POST', path, body, headers, null);
}

Session session(String email) =>
    Session(email: email, access: 'a', refresh: 'r', userId: 'u1', userName: 'Айбек', cashboxId: 'box1', cashboxName: 'Касса');

Cart cart1() => Cart(lines: [CartLine(productId: 'p1', name: 'Вода', price: 100, qty: 2)]);

void main() {
  late AppDatabase db;
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_rev03_'));
  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    Audit.resetForTests();
  });
  tearDown(() => db.close());

  test('М-1: владелец журнала неизвестен — видны только записи без владельца, чужие удаления не показываются', () async {
    Audit.currentUser = 'Айбек';
    Audit.currentOwner = 'a@x.kg';
    await Audit.log('delete', 'Вода × 2', amount: 170);
    Audit.currentOwner = '';
    await Audit.log('sale', 'Продажа без владельца');
    expect((await Audit.read()).map((e) => e.kind), ['sale'], reason: 'запись a@x.kg при пустом владельце не показывается');
    Audit.currentOwner = 'a@x.kg';
    expect((await Audit.read()).map((e) => e.kind).toSet(), {'delete', 'sale'});
    await Audit.wipeAll(directory: Audit.testDirectory);
  });

  group('М-2/М-3 счёт ответов 5xx хранится в базе', () {
    SyncService newSync(FakeApi api) =>
        SyncService(db: db, sales: SalesRepository(api, db), stock: StockRepository(api, db), catalog: CatalogRepository(api, db), owner: () => 'a@x.kg');

    Future<void> queueSale() async {
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('Нет связи с сервером.'))..attach(session('a@x.kg'));
      await SalesRepository(api, db).checkout(cart: cart1(), shiftId: 'sh1', method: PayMethod.cash);
    }

    test('«чистая» 500 считается через перезапуски: новый SyncService на каждый проход не обнуляет счёт', () async {
      await queueSale();
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('Ошибка сервера 500.', status: 500))..attach(session('a@x.kg'));
      for (var i = 0; i < SyncService.maxServer500Attempts - 1; i++) {
        await newSync(api).runQueue(); // каждый раз «после перезапуска»
      }
      expect((await db.select(db.outbox).get()).single.status, 'pending');
      await newSync(api).runQueue();
      final row = (await db.select(db.outbox).get()).single;
      expect(row.status, 'failed', reason: 'один плохой чек не держит очередь сутками');
      expect(await db.getSetting(SyncService.tallyKey(row.id)), anyOf(isNull, isEmpty), reason: 'счёт убран');
    });

    test('чек старше суток (лежал офлайн) при первой же 502 не уходит в «Не принято»', () async {
      await queueSale();
      final old = DateTime.now().subtract(const Duration(hours: 30));
      await (db.update(db.outbox)).write(OutboxCompanion(createdAt: Value(old)));
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('Ошибка сервера 502.', status: 502))..attach(session('a@x.kg'));
      await newSync(api).runQueue();
      expect((await db.select(db.outbox).get()).single.status, 'pending');
    });

    test('сдаёмся только через 24 ч от ПЕРВОЙ ошибки сервера', () async {
      await queueSale();
      final id = (await db.select(db.outbox).get()).single.id;
      final now = DateTime.now();
      await db.setSetting(
        SyncService.tallyKey(id),
        SyncService.formatTally(0, now.subtract(const Duration(hours: 25)), now.subtract(const Duration(minutes: 10))),
      );
      final api = FakeApi((m, p, b, h, q) async => throw ApiException('Ошибка сервера 503.', status: 503))..attach(session('a@x.kg'));
      await newSync(api).runQueue();
      expect((await db.select(db.outbox).get()).single.status, 'failed');
    });

    test('разбор счёта: мусор и устаревший счёт начинаются заново', () {
      final now = DateTime(2026, 11, 5, 12);
      expect(SyncService.parseTally(null, now).n500, 0);
      expect(SyncService.parseTally('xx', now).n500, 0);
      final old = SyncService.formatTally(5, now.subtract(const Duration(days: 3)), now.subtract(const Duration(hours: 5)));
      expect(SyncService.parseTally(old, now).n500, 0, reason: 'пауза в ошибках больше serverTallyGap');
      final fresh = SyncService.formatTally(5, now.subtract(const Duration(hours: 3)), now.subtract(const Duration(minutes: 5)));
      expect(SyncService.parseTally(fresh, now).n500, 5);
    });
  });

  group('М-4 возврат со старым ключом', () {
    Future<List<String>> run({required bool serverHasReturn}) async {
      final keys = <String>[];
      final api = FakeApi((m, p, b, h, q) async {
        if (m == 'GET' && p == 'api/main/pos/returns/') {
          return {
            'results': [
              if (serverHasReturn) {'id': 'r1', 'sale': 'sA', 'amount': '100', 'created_at': DateTime.now().toUtc().toIso8601String()},
            ],
          };
        }
        keys.add((b as Map)['idempotency_key'] as String);
        return {'ok': true};
      });
      final ops = OpsRepository(api, db);
      // ключ прошлой попытки, исход которой неизвестен, 3 часа назад
      await db.setSetting(
        'ops.pending.return',
        jsonEncode([
          {'key': 'old-key', 'sig': 'sA|i1:1', 'at': DateTime.now().subtract(const Duration(hours: 3)).toIso8601String()},
        ]),
      );
      await ops.returnSale('sA', [('i1', 1)]);
      return keys;
    }

    test('на сервере есть документ возврата после ключа — прошлая попытка прошла, новый возврат идёт НОВЫМ ключом', () async {
      expect((await run(serverHasReturn: true)).single, isNot('old-key'));
    });

    test('документа нет — прошлая попытка не прошла, повтор тем же ключом', () async {
      expect((await run(serverHasReturn: false)).single, 'old-key');
    });
  });

  test('М-9: ответ «error» плагина Sunmi — сбой, «ok»/«OK»/пусто — принято', () {
    SunmiBuiltIn.checkReply('ok');
    SunmiBuiltIn.checkReply('OK');
    SunmiBuiltIn.checkReply(null);
    SunmiBuiltIn.checkReply(true);
    expect(() => SunmiBuiltIn.checkReply('error'), throwsStateError);
    expect(() => SunmiBuiltIn.checkReply('No text provided to print'), throwsStateError);
  });

  test('М-5: «Округление» считается от серверного итога', () {
    final cart = Cart(lines: [
      CartLine(productId: 'a', name: 'А', price: 13, qty: 0.375, weightFlag: true),
      CartLine(productId: 'b', name: 'Б', price: 66, qty: 0.343, weightFlag: true),
    ]);
    final own = Receipt.fromCart(session: session('a@x.kg'), cart: cart, number: '1', payment: 'cash', received: 30);
    expect(own.weightRounding, 0.01);
    final srv = Receipt.fromCart(session: session('a@x.kg'), cart: cart, number: '1', payment: 'cash', received: 30, serverTotal: 27.50);
    expect(srv.total, 27.50);
    expect(srv.weightRounding, 0.02, reason: 'бумага: 27,52 − 0 − 27,50');
    expect(srv.subtotal - srv.discount - srv.weightRounding, closeTo(srv.total, 0.0001));
  });
}
