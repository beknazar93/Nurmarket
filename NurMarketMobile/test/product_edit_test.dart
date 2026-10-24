// 2026-10-22 (мобилка, админ): правка товара со склада — цены вместе с закупкой (сервер пересчитывает цену по наценке),
// двойное «Сохранить», остаток только документом (приход / списание / ревизия). Сервер NurCRM не трогается — фейки.
import 'dart:async';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body);

class FakeApi extends ApiClient {
  FakeApi(this.handler) : super(SessionStore()) {
    attach(Session(email: 'a@b.kg', access: 'a', refresh: 'r', userId: 'u1', cashboxId: 'box1', cashboxName: 'Касса'));
  }
  Handler handler;
  final calls = <String>[];
  final bodies = <Object?>[];

  Future<dynamic> _h(String m, String p, Object? b) {
    calls.add('$m $p');
    bodies.add(b);
    return handler(m, p, b);
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => _h('GET', path, null);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => _h('POST', path, body);
  @override
  Future<dynamic> patch(String path, {Object? body}) => _h('PATCH', path, body);
  @override
  Future<dynamic> delete(String path) => _h('DELETE', path, null);
}

/// Товар на «сервере». Как NurCRM: PATCH purchase_price пересчитывает цену продажи по markup_percent (ТЗ 30.7),
/// если [recalcByMarkup] — даже когда цена пришла в том же запросе.
class ProductServer {
  final p = <String, dynamic>{
    'id': 'p1',
    'name': 'Вода',
    'price': '15.00',
    'purchase_price': '10.00',
    'markup_percent': '1400',
    'wholesale_price': '0',
    'quantity': '7',
    'unit': 'шт',
    'barcode': '4700000000011',
    'alternate_barcodes': [],
  };
  bool recalcByMarkup = false;
  int sessions = 0;
  final applied = <String>[];
  final sessionBodies = <Map>[];
  Completer<void>? slowPatch;

  Future<dynamic> handle(String m, String path, Object? body) async {
    if (m == 'GET' && path == 'api/main/products/p1/') return Map<String, dynamic>.from(p);
    if (m == 'PATCH' && path == 'api/main/products/p1/') {
      if (slowPatch != null) await slowPatch!.future;
      final b = (body as Map).cast<String, dynamic>();
      final hadPurchase = b.containsKey('purchase_price');
      p.addAll(b);
      if (recalcByMarkup && hadPurchase) {
        // старая наценка сервера, а не присланная: закупка 11 → цена 165
        final cost = double.parse('${p['purchase_price']}');
        p['price'] = (cost * 15).toStringAsFixed(2);
      }
      return Map<String, dynamic>.from(p);
    }
    if (m == 'POST' && path == 'api/main/inventory/sessions/') {
      sessionBodies.add(body as Map);
      return {'id': 'inv${++sessions}'};
    }
    if (m == 'POST' && path.endsWith('/apply/')) {
      applied.add(path);
      final items = (sessionBodies.last['items'] as List).cast<Map>();
      p['quantity'] = items.first['quantity_fact'];
      return {'ok': true};
    }
    throw ApiException('нет такого пути $m $path', status: 404);
  }
}

Product local(AppDatabase db) => Product(
  id: 'p1',
  name: 'Вода',
  price: 15,
  wholesalePrice: 0,
  barcode: '4700000000011',
  altBarcodes: '',
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: true,
  quantity: 5, // на телефоне 2 продажи ещё в очереди
  category: '',
  searchKey: 'вода',
  promoRules: '',
  minQuantity: 0,
);

void main() {
  late AppDatabase db;
  late ProductServer server;
  late FakeApi api;
  late ProductEditRepository repo;

  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_pedit_'));
  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    server = ProductServer();
    api = FakeApi(server.handle);
    repo = ProductEditRepository(api, db, StockRepository(api, db));
    await db.upsertProduct(local(db).toCompanion(true).copyWith(sku: const Value(null)));
  });
  tearDown(() => db.close());

  Future<ProductDraft> before() async => ProductDraft.fromServer(await repo.load('p1'), local(db));

  group('тело PATCH', () {
    test('правка только закупки — уходят и цена, и закупка, и наценка из них', () async {
      final b = await before();
      final body = productPatchBody(b, b.copyWith(purchase: 11), canPrices: true);
      expect(body['purchase_price'], '11.00');
      expect(body['price'], '15.00');
      expect(double.parse(body['markup_percent'] as String), closeTo(36.36, 0.01));
      expect(body.containsKey('quantity'), isFalse, reason: 'остаток не правится числом');
    });

    test('без права цены — цены не отправляются', () async {
      final b = await before();
      final body = productPatchBody(b, b.copyWith(price: 20, purchase: 12, name: 'Вода 1 л'), canPrices: false);
      expect(body.keys, ['name']);
    });

    test('кг делает товар весовым, доп. штрихкод упаковки сохраняет количество', () async {
      final b = await before();
      final body = productPatchBody(b, b.copyWith(unit: 'кг', alt: ['111', '222|12|коробка']), canPrices: true);
      expect(body['is_weight'], isTrue);
      expect(body['alternate_barcodes'], [
        {'barcode': '111', 'name': '', 'quantity': 0},
        {'barcode': '222', 'name': 'коробка', 'quantity': 12},
      ]);
    });

    test('ничего не менялось — пустое тело, запроса нет', () async {
      final b = await before();
      final r = await repo.save('p1', b, b, canPrices: true);
      expect(r.changed, isFalse);
      expect(api.calls.where((c) => c.startsWith('PATCH')), isEmpty);
    });
  });

  group('сохранение', () {
    test('одновременная правка цены и закупки: сервер пересчитал цену по наценке — возвращаем вторым PATCH', () async {
      server.recalcByMarkup = true;
      final b = await before();
      final r = await repo.save('p1', b, b.copyWith(price: 16, purchase: 11), canPrices: true);
      expect(r.priceRestored, isTrue);
      expect(server.p['price'], '16.00');
      expect(server.p['purchase_price'], '11.00');
      final patches = api.bodies.whereType<Map>().where((m) => m.containsKey('price')).toList();
      expect(patches.length, 2);
      expect(patches.last, {'price': '16.00'});
      final row = await (db.select(db.products)..where((p) => p.id.equals('p1'))).getSingle();
      expect(row.price, 16);
      expect(row.quantity, 5, reason: 'остаток телефона (с неотправленными продажами) не затирается карточкой сервера');
      expect(row.isFavorite, isTrue);
    });

    test('сервер принял цену как есть — второго PATCH нет', () async {
      final b = await before();
      final r = await repo.save('p1', b, b.copyWith(price: 16, purchase: 11), canPrices: true);
      expect(r.priceRestored, isFalse);
      expect(api.calls.where((c) => c.startsWith('PATCH')).length, 1);
    });

    test('двойное нажатие «Сохранить» — один PATCH', () async {
      server.slowPatch = Completer<void>();
      final b = await before();
      final after = b.copyWith(name: 'Вода 0,5');
      final f1 = repo.save('p1', b, after, canPrices: true);
      final f2 = repo.save('p1', b, after, canPrices: true);
      server.slowPatch!.complete();
      final r = await Future.wait([f1, f2]);
      expect(r.every((x) => x.changed), isTrue);
      expect(api.calls.where((c) => c.startsWith('PATCH')).length, 1);
    });

    test('штрихкод уже у другого товара — виден владелец', () async {
      await db.upsertProduct(
        ProductsCompanion.insert(id: 'p2', name: 'Сок', barcode: const Value('4700000000028')),
      );
      expect(await repo.barcodeOwner('p1', '4700000000028'), 'Сок');
      expect(await repo.barcodeOwner('p1', '4700000000011'), isNull);
    });
  });

  group('остаток документом', () {
    test('приход: итог от свежего остатка сервера (7 + 3 = 10), акт с причиной проводится', () async {
      final r = await repo.changeStock('p1', StockDoc.income, 3, 'Приход: Поставка');
      expect(r.from, 7);
      expect(r.to, 10);
      expect(r.applied, isTrue);
      expect(server.sessionBodies.single['note'], 'Приход: Поставка');
      expect(server.applied.length, 1);
      final row = await (db.select(db.products)..where((p) => p.id.equals('p1'))).getSingle();
      expect(row.quantity, 10);
      expect(api.calls.where((c) => c.startsWith('PATCH')), isEmpty, reason: 'остаток не пишется PATCH quantity');
    });

    test('списание больше остатка — отказ, на сервер ничего', () async {
      await expectLater(repo.changeStock('p1', StockDoc.writeOff, 8, 'Списание: Брак'), throwsA(isA<StockBelowZeroException>()));
      expect(server.sessions, 0);
    });

    test('ревизия: тот же остаток — документ не создаётся', () async {
      final r = await repo.changeStock('p1', StockDoc.recount, 7, 'Ревизия: Пересчёт');
      expect(r.unchanged, isTrue);
      expect(server.sessions, 0);
    });

    test('двойное нажатие «Провести» — один акт', () async {
      final f1 = repo.changeStock('p1', StockDoc.writeOff, 2, 'Списание: Порча');
      final f2 = repo.changeStock('p1', StockDoc.writeOff, 2, 'Списание: Порча');
      final r = await Future.wait([f1, f2]);
      expect(r.first.to, 5);
      expect(server.sessions, 1);
      expect(server.p['quantity'], '5.00');
    });

    test('нет связи — остаток не прочитан, ничего не создано', () async {
      api.handler = (m, p, b) async => throw ApiException('нет связи', status: 0);
      await expectLater(repo.changeStock('p1', StockDoc.income, 1, 'Приход'), throwsA(isA<ApiException>()));
      expect(server.sessions, 0);
      expect(await db.pending(), isEmpty);
    });
  });
}
