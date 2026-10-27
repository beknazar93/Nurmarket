// 2026-10-27 (М4): поштучная продажа из упаковки — модель (как packages кассы), цена штуки, остаток «12 уп. + 5 шт», тело PATCH
// карточки (packages_input), строка чека (sale_package_id), тело продажи, очередь и пересчёт остатка. Сервер NurCRM не трогается:
// подставной API и база в памяти.
import 'dart:convert';
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/widgets.dart' show Locale;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/sync/outbox_meta.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/features/payment/sales_repository.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';
import 'package:nurmarket/features/stock/product_edit_repository.dart';
import 'package:nurmarket/features/stock/product_packages.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

/// Упаковка в виде, как сервер отдаёт в packages (проверено кассой: ProductCatalogMapper).
Map<String, dynamic> _serverPack({String id = 'pk1', String name = 'Пачка', num qty = 20, String price = '12.00'}) => {
  'id': id,
  'name': name,
  'quantity_in_package': qty,
  'unit': 'шт',
  'piece_unit_price': price,
};

typedef _H = Future<dynamic> Function(String m, String p, Object? b, Map<String, String>? h);

class _Api extends ApiClient {
  _Api(this.handler) : super(SessionStore()) {
    attach(const Session(email: 'a@b.kg', access: 'a', refresh: 'r', userId: 'u1', cashboxId: 'box1', cashboxName: 'Касса'));
  }
  final _H handler;
  final calls = <String>[];
  final bodies = <Object?>[];
  Future<dynamic> _h(String m, String p, Object? b, [Map<String, String>? h]) {
    calls.add('$m $p');
    bodies.add(b);
    return handler(m, p, b, h);
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => _h('GET', path, null);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => _h('POST', path, body, headers);
  @override
  Future<dynamic> patch(String path, {Object? body}) => _h('PATCH', path, body);
  @override
  Future<dynamic> delete(String path) => _h('DELETE', path, null);
}

Product _prod({double quantity = 10, double price = 200}) => Product(
  id: 'p1',
  name: 'Винстон',
  price: price,
  wholesalePrice: 0,
  barcode: '4700000000011',
  altBarcodes: '',
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  quantity: quantity,
  category: '',
  searchKey: 'винстон',
  promoRules: '',
  minQuantity: 0,
);

const _pack = PiecePack(id: 'pk1', name: 'Пачка', qty: 20, piecePrice: 12);

ProductDraft _draft({PiecePack? pack, bool locked = false, String unit = 'шт'}) =>
    ProductDraft(name: 'Винстон', unit: unit, category: '', barcode: '', alt: const [], price: 200, purchase: 150, wholesale: 0, minQty: null, pack: pack, packLocked: locked);

void main() {
  late AppDatabase db;
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_pack_'));
  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  test('тексты упаковок есть на 5 языках и не совпадают с русским (кроме общих слов)', () {
    final ru = lookupAppLocalizations(const Locale('ru'));
    for (final code in ['ky', 'en', 'tr', 'uz']) {
      final l = lookupAppLocalizations(Locale(code));
      for (final (a, b) in [
        (ru.pkSwitch, l.pkSwitch),
        (ru.pkTitle, l.pkTitle),
        (ru.pkQtyBad, l.pkQtyBad),
        (ru.pkChoiceTitle, l.pkChoiceTitle),
        (ru.pkWeightBlocked, l.pkWeightBlocked),
        (ru.pkMulti, l.pkMulti),
      ]) {
        expect(b, isNotEmpty);
        expect(b, isNot(a), reason: '$code переведён');
      }
      expect(l.pkMismatch('20', '12', '240', '200'), contains('240'));
      expect(l.pkLinePiece('Пачка'), contains('Пачка'));
    }
    expect(ru.pkMarkup(33), contains('33'));
  });

  group('модель упаковки (packages кассы)', () {
    test('читается как у кассы: штук > 0 и цена штуки > 0, комплект — нет', () {
      final all = PiecePack.parseAll([
        _serverPack(),
        {'id': 'x', 'quantity_in_package': 0, 'piece_unit_price': '5'},
        {'id': 'y', 'quantity_in_package': 10, 'piece_unit_price': '0'},
        'мусор',
      ]);
      expect(all.length, 1);
      expect(all.single.id, 'pk1');
      expect(all.single.qty, 20);
      expect(all.single.piecePrice, 12);
      expect(PiecePack.first(null), isNull);
      expect(PiecePack.first(<dynamic>[]), isNull);
    });

    test('packages_input — тот же вид, что BuildProductBody кассы', () {
      expect(_pack.toInput('шт'), {'name': 'Пачка', 'quantity_in_package': 20, 'unit': 'шт', 'piece_unit_price': '12.00'});
      expect(const PiecePack(qty: 6, piecePrice: 20.5).toInput('упак'), {'name': 'упаковка', 'quantity_in_package': 6, 'unit': 'упак', 'piece_unit_price': '20.50'});
    });

    test('цена штуки сама = цена упаковки ÷ штук, до копейки', () {
      expect(autoPiecePrice(200, 20), 10);
      expect(autoPiecePrice(250, 12), 20.83);
      expect(autoPiecePrice(0, 12), 0);
      expect(autoPiecePrice(200, 0), 0);
    });

    test('проверки: штук целое ≥ 2, цена штуки есть, расхождение > 5 % — вопрос', () {
      expect(packProblem(packPrice: 200, qty: 0, piece: 10), PackProblem.qtyBad);
      expect(packProblem(packPrice: 200, qty: 1, piece: 10), PackProblem.qtyBad);
      expect(packProblem(packPrice: 200, qty: 2.5, piece: 10), PackProblem.qtyBad);
      expect(packProblem(packPrice: 200, qty: 20, piece: 0), PackProblem.pieceBad);
      expect(packProblem(packPrice: 200, qty: 20, piece: 10), isNull);
      expect(packProblem(packPrice: 200, qty: 20, piece: 12), PackProblem.mismatch, reason: '12 × 20 = 240 против 200 — 20 % выше');
    });

    test('расхождение: 4 % — тихо, 6 % — вопрос (в обе стороны)', () {
      expect(packProblem(packPrice: 100, qty: 10, piece: 10.4), isNull);
      expect(packProblem(packPrice: 100, qty: 10, piece: 10.6), PackProblem.mismatch);
      expect(packProblem(packPrice: 100, qty: 10, piece: 9.4), PackProblem.mismatch);
    });

    test('остаток в упаковках → «12 уп. + 5 шт»', () {
      String s(double v, PiecePack? p) => stockWithPack((x) => x.toStringAsFixed(x == x.roundToDouble() ? 0 : 2), v, p, packWord: 'уп.', pcs: 'шт', unit: 'шт');
      expect(s(12.25, _pack), '12 уп. + 5 шт');
      expect(s(12, _pack), '12 уп.');
      expect(s(0.25, _pack), '5 шт');
      expect(s(245 / 20, _pack), '12 уп. + 5 шт');
      expect(s(245, null), '245 шт', reason: 'без упаковки — как раньше');
      expect(s(11.7, _pack), '11 уп. + 14 шт');
      expect(s(11.703, _pack), '11.70 уп.', reason: 'не складывается в целые штуки — дробные пачки');
      expect(splitStock(-1, 20), isNull);
      expect(stockInPieces(12.25, _pack), closeTo(245, 1e-9));
    });

    test('упаковки товаров в настройках телефона: запись и чтение', () async {
      expect(await PackageStore.read(db), isEmpty);
      await PackageStore.put(db, 'p1', _pack);
      await PackageStore.put(db, 'p2', const PiecePack(qty: 6, piecePrice: 20));
      final m = await PackageStore.read(db);
      expect(m['p1']!.id, 'pk1');
      expect(m['p2']!.qty, 6);
      await PackageStore.put(db, 'p2', null);
      expect((await PackageStore.read(db)).keys, ['p1']);
      await db.setSetting(PackageStore.setting, '{не json');
      expect(await PackageStore.read(db), isEmpty, reason: 'испорченная запись — упаковок нет, продажа целиком работает');
    });
  });

  group('карточка товара: packages_input', () {
    test('включили поштучную продажу — packages_input одной упаковкой', () {
      final body = productPatchBody(_draft(), _draft(pack: const PiecePack(name: 'Пачка', qty: 20, piecePrice: 12)), canPrices: true);
      expect(body['packages_input'], [
        {'name': 'Пачка', 'quantity_in_package': 20, 'unit': 'шт', 'piece_unit_price': '12.00'},
      ]);
      expect(body.keys, ['packages_input'], reason: 'остальное не менялось');
    });

    test('не менялось — packages_input не уходит; выключили — пустой список', () {
      expect(productPatchBody(_draft(pack: _pack), _draft(pack: _pack), canPrices: true), isEmpty);
      expect(productPatchBody(_draft(pack: _pack), _draft(), canPrices: true)['packages_input'], isEmpty);
    });

    test('несколько упаковок/комплект, весовой — packages_input не отправляется', () {
      expect(productPatchBody(_draft(pack: _pack, locked: true), _draft(pack: const PiecePack(qty: 6, piecePrice: 20), locked: true), canPrices: true), isEmpty);
      expect(productPatchBody(_draft(unit: 'кг'), _draft(unit: 'кг', pack: _pack), canPrices: true).containsKey('packages_input'), isFalse);
    });

    test('ProductDraft.fromServer читает packages; у комплекта packages — состав, не упаковка', () {
      final d = ProductDraft.fromServer({
        'id': 'p1',
        'name': 'Винстон',
        'price': '200',
        'packages': [_serverPack()],
      }, _prod());
      expect(d.pack!.id, 'pk1');
      expect(d.pack!.qty, 20);
      expect(d.packLocked, isFalse);
      final two = ProductDraft.fromServer({
        'id': 'p1',
        'name': 'В',
        'packages': [_serverPack(), _serverPack(id: 'pk2', qty: 200, price: '1.00')],
      }, _prod());
      expect(two.packLocked, isTrue);
      final bundle = ProductDraft.fromServer({
        'id': 'p1',
        'name': 'Набор',
        'kind': 'bundle',
        'packages': [_serverPack()],
      }, _prod());
      expect(bundle.pack, isNull);
      expect(bundle.packLocked, isTrue);
      final plain = ProductDraft.fromServer({'id': 'p1', 'name': 'В'}, _prod());
      expect(plain.pack, isNull);
    });

    test('сохранение: PATCH с packages_input, id упаковки с сервера — в базе телефона (для продажи штуки)', () async {
      final server = <String, dynamic>{'id': 'p1', 'name': 'Винстон', 'price': '200.00', 'purchase_price': '150.00', 'quantity': '10', 'unit': 'шт', 'packages': <dynamic>[]};
      final api = _Api((m, p, b, h) async {
        if (m == 'GET' && p == 'api/main/products/p1/') return Map<String, dynamic>.from(server);
        if (m == 'PATCH' && p == 'api/main/products/p1/') {
          final inp = ((b as Map)['packages_input'] as List).cast<Map>();
          server['packages'] = [
            for (final x in inp) {..._serverPack(), 'name': x['name'], 'quantity_in_package': x['quantity_in_package'], 'piece_unit_price': x['piece_unit_price']},
          ];
          return Map<String, dynamic>.from(server);
        }
        throw ApiException('нет $m $p', status: 404);
      });
      await db.upsertProduct(ProductsCompanion.insert(id: 'p1', name: 'Винстон', searchKey: const Value('винстон')));
      final repo = ProductEditRepository(api, db, StockRepository(api, db));
      final before = ProductDraft.fromServer(await repo.load('p1'), _prod());
      final r = await repo.save('p1', before, before.copyWith(pack: const PiecePack(name: 'Пачка', qty: 20, piecePrice: 12)), canPrices: true);
      expect(r.changed, isTrue);
      expect(api.calls.where((c) => c.startsWith('PATCH')).length, 1);
      final saved = (await PackageStore.read(db))['p1']!;
      expect(saved.id, 'pk1');
      expect(saved.qty, 20);
      expect(saved.piecePrice, 12);
    });
  });

  group('чек и тело продажи (деньги)', () {
    CartLine whole() => CartLine(productId: 'p1', name: 'Винстон', price: 200, qty: 2, packName: 'Пачка');
    CartLine piece(double qty) => CartLine(productId: 'p1', name: 'Винстон', price: 12, qty: qty, unit: 'шт', salePackageId: 'pk1', perPack: 20, packName: 'Пачка');

    test('штука и целая упаковка одного товара — разные строки; штуки одной упаковки складываются', () {
      final cart = const Cart().add(whole()).add(piece(3)).add(piece(2));
      expect(cart.lines.length, 2);
      expect(cart.lines[0].qty, 2);
      expect(cart.lines[1].qty, 5);
      expect(cart.total, 2 * 200 + 5 * 12);
    });

    test('остаток, который списывает строка: штука — доля упаковки, остальное как раньше', () {
      expect(piece(5).stockQty, 0.25);
      expect(whole().stockQty, 2);
      expect(CartLine(productId: 'w', name: 'Сахар', price: 80, qty: 1.5, isWeight: true).stockQty, 1.5);
    });

    test('обычный чек: тело продажи НЕ изменилось (ни sale_package_id, ни price_override)', () {
      final body = SalesRepository.buildBody(
        cart: Cart(
          lines: [CartLine(productId: 'p1', name: 'Вода', price: 15, qty: 3)],
        ),
        shiftId: 's1',
        method: PayMethod.cash,
      );
      expect((body['items'] as List).single, {'product': 'p1', 'qty': '3', 'price': '15.00'});
      // даже у товара с упаковкой, проданного целиком
      final w = SalesRepository.buildBody(
        cart: Cart(lines: [whole()]),
        shiftId: 's1',
        method: PayMethod.cash,
      );
      expect((w['items'] as List).single, {'product': 'p1', 'qty': '2', 'price': '200.00'});
    });

    test('штука из упаковки: sale_package_id, qty — штуки, price — цена штуки (как строка чека кассы)', () {
      final body = SalesRepository.buildBody(cart: const Cart().add(whole()).add(piece(3)), shiftId: 's1', method: PayMethod.cash, received: 1000);
      final items = (body['items'] as List).cast<Map>();
      expect(items.length, 2, reason: 'упаковка и штуки — разные позиции');
      expect(items[0], {'product': 'p1', 'qty': '2', 'price': '200.00'});
      expect(items[1], {'product': 'p1', 'qty': '3', 'price': '12.00', 'sale_package_id': 'pk1', 'price_override': true});
      expect(body['payment'], {'method': 'cash', 'received': '1000.00'});
    });

    test('штуки целые: дробное количество округляется как у обычного штучного товара', () {
      final body = SalesRepository.buildBody(
        cart: Cart(lines: [piece(2.6)]),
        shiftId: 's1',
        method: PayMethod.cash,
      );
      expect(((body['items'] as List).single as Map)['qty'], '3');
    });

    test('оплата онлайн: на сервер уходит sale_package_id, остаток телефона уменьшается на долю упаковки', () async {
      await db.upsertProduct(ProductsCompanion.insert(id: 'p1', name: 'Винстон', price: const Value(200), quantity: const Value(10), searchKey: const Value('винстон')));
      Map? sent;
      final api = _Api((m, p, b, h) async {
        if (p == 'api/main/pos/checkout/') {
          sent = (b as Map).cast<String, dynamic>();
          return {'id': 's1', 'number': '1', 'total': '60.00'};
        }
        throw ApiException('нет $m $p', status: 404);
      });
      final r = await SalesRepository(api, db).checkout(
        cart: Cart(lines: [piece(5)]),
        shiftId: 'sh1',
        method: PayMethod.cash,
        received: 100,
      );
      expect(r.queued, isFalse);
      expect(((sent!['items'] as List).single as Map)['sale_package_id'], 'pk1');
      expect((await db.productById('p1'))!.quantity, closeTo(9.75, 1e-9), reason: '5 шт из пачки по 20 = 0,25 пачки');
    });

    test('без связи: чек в очереди с той же строкой; снятие из очереди возвращает долю упаковки; досылка — с sale_package_id', () async {
      await db.upsertProduct(ProductsCompanion.insert(id: 'p1', name: 'Винстон', price: const Value(200), quantity: const Value(10), searchKey: const Value('винстон')));
      final offline = _Api((m, p, b, h) async => throw ApiException('Нет связи с сервером.'));
      final r = await SalesRepository(offline, db).checkout(
        cart: Cart(lines: [whole(), piece(5)]),
        shiftId: 'sh1',
        method: PayMethod.cash,
        received: 500,
      );
      expect(r.queued, isTrue);
      expect((await db.productById('p1'))!.quantity, closeTo(10 - 2 - 0.25, 1e-9));
      final row = (await db.pending()).single;
      // в очереди штуки помечены метой «штук в упаковке»; на сервер мета не уходит
      expect(OutboxMeta.read(row.body)!['packs'], {'pk1': 20.0});
      final replay = SalesRepository.replayBody(row);
      expect(replay.containsKey('_meta'), isFalse);
      final items = (replay['items'] as List).cast<Map>();
      expect(items[1]['sale_package_id'], 'pk1');
      expect(items[1]['qty'], '5');
      expect(replay['sold_at'], isNotNull);
      // пересчёт очереди после обновления каталога: сервер ещё не знает про чек → остаток = серверный − чек из очереди
      final syncApi = _Api((m, p, b, h) async {
        if (p == 'api/main/products/list/') {
          return {
            'results': [
              {
                'id': 'p1',
                'name': 'Винстон',
                'price': '200.00',
                'quantity': '10',
                'packages': [_serverPack()],
              },
            ],
          };
        }
        throw ApiException('нет $m $p', status: 404);
      });
      await CatalogRepository(syncApi, db).sync();
      expect((await db.productById('p1'))!.quantity, closeTo(10 - 2 - 0.25, 1e-9), reason: 'очередь поверх каталога: штуки — долей упаковки');
      expect((await PackageStore.read(db))['p1']!.id, 'pk1', reason: 'упаковка из каталога сохранена');
      // «Снять» запись: товар возвращается в остаток
      await OutboxMeta.drop(db, row);
      expect((await db.productById('p1'))!.quantity, closeTo(10, 1e-9));
    });

    test('каталог без поля packages не стирает ранее известные упаковки', () async {
      await PackageStore.put(db, 'p1', _pack);
      final api = _Api(
        (m, p, b, h) async => {
          'results': [
            {'id': 'p1', 'name': 'Винстон', 'price': '200.00', 'quantity': '10'},
          ],
        },
      );
      await CatalogRepository(api, db).sync();
      expect((await PackageStore.read(db)).containsKey('p1'), isTrue);
    });

    test('черновик и отложенный чек не теряют штуки из упаковки', () {
      final held = HeldCart(
        id: 'h',
        at: DateTime(2026, 10, 27),
        note: '',
        cart: Cart(lines: [whole(), piece(4)]),
      );
      final back = HeldCart.fromJson((jsonDecode(jsonEncode(held.toJson())) as Map).cast<String, dynamic>());
      expect(back.cart.lines[1].salePackageId, 'pk1');
      expect(back.cart.lines[1].perPack, 20);
      expect(back.cart.lines[1].packName, 'Пачка');
      expect(back.cart.lines[0].salePackageId, isNull);
      expect(back.cart.lines[0].perPack, 1);
    });

    test('чек с сервера: строка со sale_package_id — «шт», а не единица упаковки', () {
      final r = Receipt.fromSale(
        session: null,
        sale: {
          'items': [
            {'product_name': 'Винстон', 'quantity': '3', 'unit_price': '12.00', 'total': '36.00', 'unit': 'упак', 'sale_package_id': 'pk1'},
            {'product_name': 'Вода', 'quantity': '1', 'unit_price': '15.00', 'total': '15.00', 'unit': 'шт'},
          ],
        },
        number: '7',
        total: 51,
        at: DateTime(2026, 10, 27),
        payment: 'cash',
      );
      expect(r.lines[0].unit, 'шт');
      expect(r.lines[1].unit, 'шт');
    });
  });
}
