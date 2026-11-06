import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';

// 2026-11-06 (жалоба «Орук маркет»: десктоп молча ставил «закупка + 20 %», 30 → 36): приход не шлёт и не пишет цену продажи,
// если её не задали или не меняли в этом приходе. Сеть и сервер подменены.
class _Api extends ApiClient {
  _Api() : super(SessionStore());
  final patches = <Map<String, dynamic>>[];
  final posts = <String>[];
  double stock = 3;

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async => {'id': 'p1', 'quantity': stock, 'purchase_price': 30};

  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) async {
    posts.add(path);
    return {};
  }

  @override
  Future<dynamic> patch(String path, {Object? body}) async {
    patches.add({'path': path, ...(body as Map).cast<String, dynamic>()});
    return {};
  }
}

void main() {
  late AppDatabase db;
  late _Api api;
  late AdminReceiveRepository repo;

  Future<double> localPrice() async => (await db.customSelect("SELECT price FROM products WHERE id = 'p1'").getSingle()).read<double>('price');

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    api = _Api();
    repo = AdminReceiveRepository(api, db);
  });
  tearDown(() async => db.close());

  Future<void> product(double price) => db.upsertProduct(ProductsCompanion.insert(id: 'p1', name: 'Мука', price: Value(price), quantity: const Value(3), searchKey: const Value('мука')));

  ReceiveLine line({required double oldPrice, required double price, double cost = 30}) =>
      ReceiveLine(productId: 'p1', name: 'Мука', before: 3, oldCost: 30, oldPrice: oldPrice, cost: cost, price: price, qty: 2);

  test('priceChanged: пустая цена и прежняя — не изменение; новая заданная — изменение; новый товар — не считается', () {
    expect(line(oldPrice: 0, price: 0).priceChanged, isFalse, reason: 'товар без цены, закупка 100 → цена не ставится сама');
    expect(line(oldPrice: 50, price: 50).priceChanged, isFalse, reason: 'существующая цена не затирается');
    expect(line(oldPrice: 50, price: 0).priceChanged, isFalse, reason: 'очищенное поле не обнуляет цену товара');
    expect(line(oldPrice: 50, price: 60).priceChanged, isTrue);
    expect(line(oldPrice: 0, price: 45).priceChanged, isTrue, reason: 'цену задали руками в этом приходе');
    expect(ReceiveLine(productId: '', name: 'Новый', price: 10, isNew: true).priceChanged, isFalse);
  });

  test('товар без цены + закупка 100 (приход от поставщика): запрос цены не уходит, цена на телефоне не меняется', () async {
    await product(0);
    final (n, errors) = await repo.post([line(oldPrice: 0, price: 0, cost: 100)], 's1', false);
    expect(n, 1);
    expect(errors, isEmpty);
    expect(api.posts.single, 'api/main/suppliers/s1/receipt/');
    expect(api.patches, isEmpty, reason: '«оставить без цены» — PATCH цены не шлём');
    expect(await localPrice(), 0);
  });

  test('товар без цены, без поставщика: PATCH несёт закупку и остаток, но не цену', () async {
    await product(0);
    final (n, errors) = await repo.post([line(oldPrice: 0, price: 0, cost: 100)], null, false);
    expect(n, 1);
    expect(errors, isEmpty);
    final body = api.patches.single;
    expect(body.containsKey('price'), isFalse);
    expect(body['purchase_price'], isNotNull);
    expect(body['quantity'], 5.0);
    expect(await localPrice(), 0);
  });

  test('существующая цена не затирается: цену не меняли в этом приходе — PATCH без price (и с поставщиком, и без)', () async {
    await product(50);
    await repo.post([line(oldPrice: 50, price: 50)], 's1', false);
    expect(api.patches, isEmpty);
    await repo.post([line(oldPrice: 50, price: 50)..posted = false], null, false);
    expect(api.patches.single.containsKey('price'), isFalse);
    expect(await localPrice(), 50);
  });

  test('цену изменили в этом приходе — уходит ровно один PATCH цены и пишется на телефон', () async {
    await product(50);
    await repo.post([line(oldPrice: 50, price: 65)], 's1', false);
    expect(api.patches.single['price'], '65.00');
    expect(await localPrice(), 65);
  });
}
