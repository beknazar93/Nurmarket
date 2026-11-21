// 2026-11-21 (ответ сервера, Умар): `quantity` в alternate_barcodes — целое > 0 или без поля (0 даёт ошибку); привязка кода через
// POST products/{id}/barcodes/ с If-Match (409 version_conflict — один повтор, 404/405 — прежний PATCH), 409 barcode_duplicate с
// товаром-владельцем, comment прихода с разбивкой по вариантам, бренд нового товара. Сервер — заглушка, сети и боевой базы нет.
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/features/scan/invoice_bind.dart';
import 'package:nurmarket/features/stock/stock_repository.dart';

import 'admin_stock_fixes_test.dart' show FakeApi;

const _owner = Session(email: 'a@x.kg', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);
const _code = '4600123456789';

class _Req {
  final String method, path;
  final Object? body;
  final Map<String, String>? headers;
  _Req(this.method, this.path, this.body, this.headers);
}

/// Заглушка NurCRM: карточка товара p1 с версией, POST /barcodes/ с If-Match, управляемые отказы.
class _Server {
  final reqs = <_Req>[];
  Map<String, dynamic> card = {'id': 'p1', 'name': 'Сок', 'barcode': '2000000000015', 'version': 'v1', 'alternate_barcodes': <Object>[]};

  /// Ошибки на очередные POST /barcodes/ (по порядку); пусто — успех.
  final postErrors = <ApiException>[];
  ApiException? patchError;
  int getCount = 0;

  List<_Req> get posts => reqs.where((r) => r.method == 'POST' && r.path.endsWith('/barcodes/')).toList();
  List<_Req> get patches => reqs.where((r) => r.method == 'PATCH').toList();

  Future<dynamic> handle(String m, String p, Object? b, Map<String, dynamic>? q, [Map<String, String>? h]) async {
    reqs.add(_Req(m, p, b, h));
    if (m == 'GET' && p == 'api/main/products/p1/') {
      getCount++;
      return Map<String, dynamic>.from(card);
    }
    if (m == 'POST' && p == 'api/main/products/p1/barcodes/') {
      if (postErrors.isNotEmpty) throw postErrors.removeAt(0);
      final m2 = (b as Map).cast<String, dynamic>();
      (card['alternate_barcodes'] as List).add(m2);
      card['version'] = 'v${getCount + 10}';
      return {'ok': true};
    }
    if (m == 'PATCH' && p == 'api/main/products/p1/') {
      if (patchError != null) throw patchError!;
      return card;
    }
    throw ApiException('не ожидали $m $p', status: 404);
  }
}

ApiException _conflict(String code, [Map<String, dynamic>? data]) => ApiException('Конфликт', status: 409, payload: {'code': code, 'data': ?data});

void main() {
  late AppDatabase db;
  late _Server srv;
  late FakeApi api;
  late StockRepository repo;

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    srv = _Server();
    api = _HeadersApi(srv)..attach(_owner);
    repo = StockRepository(api, db);
    await db.upsertProduct(ProductsCompanion.insert(id: 'p1', name: 'Сок', barcode: const Value('2000000000015'), quantity: const Value(3), searchKey: const Value('сок')));
  });
  tearDown(() => db.close());

  Future<void> settle() async {
    for (var i = 0; i < 20; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }
  }

  Future<OutboxData> queue(String code, {String name = ''}) async {
    await repo.queueBarcodeBind((await db.productById('p1'))!, code, name: name);
    await settle();
    return (await db.select(db.outbox).get()).single;
  }

  group('тело доп. штрихкода: quantity — целое > 0 или без поля', () {
    test('нет, ноль и дробное < 0,5 — поля нет; 12 и 12,4 — 12; длины ограничены (имя 255, код 64)', () {
      expect(altBarcodeBody('111'), {'barcode': '111', 'name': ''});
      expect(altBarcodeBody('111', qty: 0), {'barcode': '111', 'name': ''});
      expect(altBarcodeBody('111', qty: 0.4), {'barcode': '111', 'name': ''});
      expect(altBarcodeBody('111', name: ' коробка ', qty: 12), {'barcode': '111', 'name': 'коробка', 'quantity': 12});
      expect(altBarcodeBody('111', qty: 12.4)['quantity'], 12);
      expect((altBarcodeBody('1' * 100, name: 'я' * 300)['barcode'] as String).length, 64);
      expect((altBarcodeBody('1', name: 'я' * 300)['name'] as String).length, 255);
      expect(altBarcodeBody('111').containsKey('quantity'), isFalse);
    });
  });

  group('POST products/{id}/barcodes/', () {
    test('основной код есть: POST одного кода с названием варианта и If-Match версии карточки, без quantity; PATCH не нужен', () async {
      final row = await queue(_code, name: 'клубника');
      expect(srv.posts.length, 1);
      expect(srv.posts.single.body, {'barcode': _code, 'name': 'клубника'});
      expect(srv.posts.single.headers, {'If-Match': 'v1'});
      expect(srv.patches, isEmpty);
      expect(row.status, 'sent');
      expect((await db.productById('p1'))!.altBarcodes, '$_code|0|клубника');
    });

    test('у карточки нет версии: запрос без If-Match', () async {
      srv.card.remove('version');
      await queue(_code);
      expect(srv.posts.single.headers ?? const {}, isEmpty);
    });

    test('основного кода нет: он становится основным — PATCH barcode (POST /barcodes/ только для дополнительных)', () async {
      srv.card['barcode'] = '';
      await db.upsertProduct(ProductsCompanion.insert(id: 'p1', name: 'Сок', quantity: const Value(3), searchKey: const Value('сок')));
      await db.customStatement("UPDATE products SET barcode = NULL WHERE id = 'p1'");
      final row = await queue(_code);
      expect(srv.posts, isEmpty);
      expect(srv.patches.single.body, {'barcode': _code});
      expect(row.status, 'sent');
    });

    test('409 version_conflict: карточка перечитывается, запрос повторяется один раз с новой версией', () async {
      srv.postErrors.add(_conflict('version_conflict'));
      // пока шёл первый запрос, карточку кто-то изменил: после перечитывания версия другая
      final original = srv.handle;
      var reads = 0;
      api.handler = (m, p, b, q) async {
        if (m == 'GET' && p == 'api/main/products/p1/' && ++reads == 2) srv.card['version'] = 'v7';
        return original(m, p, b, q);
      };
      final row = await queue(_code, name: 'лимон');
      expect(srv.posts.length, 2);
      expect(srv.posts[0].headers, {'If-Match': 'v1'});
      expect(srv.posts[1].headers, {'If-Match': 'v7'}, reason: 'повтор — с версией перечитанной карточки');
      expect(row.status, 'sent');
    });

    test('409 version_conflict дважды: второй отказ уходит наружу, запись остаётся ждать (409 «временный»), код на телефоне не откатывается', () async {
      srv.postErrors.addAll([_conflict('version_conflict'), _conflict('version_conflict')]);
      final row = await queue(_code);
      expect(srv.posts.length, 2, reason: 'повтор один');
      expect(row.status, 'pending');
      expect((await db.productById('p1'))!.altBarcodes, contains(_code));
    });

    test('конфликт версии, а после перечитывания код уже в карточке (добавили с кассы): повторного POST нет', () async {
      srv.postErrors.add(_conflict('version_conflict'));
      // перечитанная карточка уже содержит код
      final original = srv.handle;
      var reads = 0;
      api.handler = (m, p, b, q) async {
        if (m == 'GET' && p == 'api/main/products/p1/' && ++reads == 2) {
          srv.card['alternate_barcodes'] = [
            {'barcode': _code, 'name': ''},
          ];
        }
        return original(m, p, b, q);
      };
      final row = await queue(_code);
      expect(srv.posts.length, 1);
      expect(row.status, 'sent');
    });

    for (final status in const [404, 405]) {
      test('сервер без POST /barcodes/ ($status): прежний PATCH alternate_barcodes списком, без quantity 0', () async {
        srv.card['alternate_barcodes'] = [
          {'barcode': '2000000000022', 'name': 'Блок', 'quantity': 6},
          {'barcode': '2000000000039', 'name': 'Пачка', 'quantity': null},
        ];
        srv.postErrors.add(ApiException('нет', status: status));
        final row = await queue(_code, name: 'клубника');
        expect(srv.posts.length, 1);
        expect(srv.patches.single.body, {
          'alternate_barcodes': [
            {'barcode': '2000000000022', 'name': 'Блок', 'quantity': 6},
            {'barcode': '2000000000039', 'name': 'Пачка'},
            {'barcode': _code, 'name': 'клубника'},
          ],
        });
        expect(row.status, 'sent');
      });
    }

    test('код уже в карточке: ни POST, ни PATCH (повтор того же кода)', () async {
      srv.card['alternate_barcodes'] = [
        {'barcode': _code, 'name': 'клубника'},
      ];
      final row = await queue(_code, name: 'клубника');
      expect(srv.posts, isEmpty);
      expect(srv.patches, isEmpty);
      expect(row.status, 'sent');
    });
  });

  group('409 barcode_duplicate', () {
    final dupData = {'product_id': 'p9', 'name': 'Фанта 1 л', 'code': 'barcode_duplicate', 'source': 'alternate', 'barcode': _code};

    test('разбор: товар-владелец, откуда код (основной или дополнительный); другие 409 и коды — не дубль', () {
      final d = BarcodeDuplicate.from(_conflict('barcode_duplicate', dupData))!;
      expect((d.productId, d.name, d.code, d.source), ('p9', 'Фанта 1 л', _code, 'alternate'));
      final nested = BarcodeDuplicate.from(ApiException('x', status: 409, payload: {'error': {'code': 'barcode_duplicate', 'data': dupData}}));
      expect(nested?.name, 'Фанта 1 л');
      expect(BarcodeDuplicate.from(_conflict('version_conflict')), isNull);
      expect(BarcodeDuplicate.from(ApiException('x', status: 400, payload: {'code': 'barcode_duplicate'})), isNull);
      expect(BarcodeDuplicate.from(ApiException('x', status: 409)), isNull);
      expect(BarcodeDuplicate.isVersionConflict(_conflict('version_conflict')), isTrue);
      expect(BarcodeDuplicate.isVersionConflict(_conflict('barcode_duplicate')), isFalse);
    });

    test('при привязке: запись «Не принято» с названием владельца из ответа сервера, код на телефоне откатывается, повтора нет', () async {
      srv.postErrors.add(_conflict('barcode_duplicate', dupData));
      final row = await queue(_code, name: 'клубника');
      expect(srv.posts.length, 1, reason: 'повтор тем же запросом не поможет');
      expect(row.status, 'failed');
      expect(row.lastError, contains('Фанта 1 л'));
      expect(row.lastError, contains(_code));
      expect((await db.productById('p1'))!.altBarcodes, isEmpty);
      expect(await db.productByScan(_code), isNull);
    });

    test('основной код другого товара: тот же разбор (source = product)', () async {
      srv.card['barcode'] = '';
      await db.customStatement("UPDATE products SET barcode = NULL WHERE id = 'p1'");
      srv.patchError = _conflict('barcode_duplicate', {...dupData, 'source': 'product', 'name': 'Сок «Добрый»'});
      final row = await queue(_code);
      expect(row.status, 'failed');
      expect(row.lastError, contains('Сок «Добрый»'));
      expect((await db.productById('p1'))!.barcode, isNull);
    });
  });

  group('приход: comment и бренд', () {
    test('примечание к приходу — разбивка по вариантам, не длиннее 1500 знаков; без вариантов — пусто', () {
      final a = ReceiveLine(productId: 'p1', name: 'Асу', note: 'Обычный 5, клубника 3, лимон 2');
      final b = ReceiveLine(productId: 'p2', name: 'Хлеб');
      expect(receiptComment([a, b]), 'Асу: Обычный 5, клубника 3, лимон 2');
      expect(receiptComment([b]), '');
      expect(receiptComment([ReceiveLine(productId: 'p3', name: 'Х', note: 'я' * 3000)]).length, 1500);
    });

    test('приход с поставщиком: comment уходит в POST suppliers/{id}/receipt/ только когда есть варианты', () async {
      final bodies = <Map<String, dynamic>>[];
      final repo2 = AdminReceiveRepository(
        FakeApi((m, p, b, q) async {
          if (m == 'POST' && p == 'api/main/suppliers/s1/receipt/') {
            bodies.add((b as Map).cast<String, dynamic>());
            return {'id': 'r1'};
          }
          if (m == 'PATCH') return {};
          throw ApiException('не ожидали $m $p', status: 404);
        })..attach(_owner),
        db,
      );
      await repo2.post([ReceiveLine(productId: 'p1', name: 'Асу', qty: 10, cost: 40, price: 85, note: 'Обычный 5, клубника 3, лимон 2')], 's1', true);
      await repo2.post([ReceiveLine(productId: 'p2', name: 'Хлеб', qty: 1, cost: 20, price: 30)], 's1', true);
      expect(bodies[0]['comment'], 'Асу: Обычный 5, клубника 3, лимон 2');
      expect(bodies[1].containsKey('comment'), isFalse);
      expect((bodies[0]['items'] as List).single, {'product': 'p1', 'qty': 10.0, 'purchase_price': 40.0});
    });

    test('новый товар: бренд уходит как brand_name, доп. штрихкоды — без quantity', () async {
      final created = <Map<String, dynamic>>[];
      final repo2 = AdminReceiveRepository(
        FakeApi((m, p, b, q) async {
          if (m == 'POST' && p == 'api/main/products/create-manual/') {
            created.add((b as Map).cast<String, dynamic>());
            return {'id': 'n1'};
          }
          if (m == 'GET') return {'id': 'n1', 'quantity': 0};
          if (m == 'PATCH') return {};
          throw ApiException('не ожидали $m $p', status: 404);
        })..attach(_owner),
        db,
      );
      final line = ReceiveLine(
        productId: '',
        name: 'Асу',
        barcode: _code,
        isNew: true,
        qty: 2,
        cost: 40,
        price: 85,
        brand: 'Асу',
        altBarcodes: [altBarcodeBody('4600123456790', name: 'клубника')],
      );
      await repo2.post([line], null, false);
      expect(created.single['brand_name'], 'Асу');
      expect(created.single['alternate_barcodes'], [
        {'barcode': '4600123456790', 'name': 'клубника'},
      ]);
      expect(created.single['quantity'], 0, reason: 'это остаток самого товара (0 допустим); нули запрещены только в доп. штрихкодах');
    });
  });
}

/// FakeApi, у которого post передаёт заголовки заглушке (If-Match).
class _HeadersApi extends FakeApi {
  final _Server srv;
  _HeadersApi(this.srv) : super((m, p, b, q) async => srv.handle(m, p, b, q));
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => srv.handle('POST', path, body, null, headers);
}
