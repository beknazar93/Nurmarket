// 2026-10-22 (мобилка, админ): проверки пакетов П8 (приёмка с ценами), П10 (новый товар), П12 (массовая загрузка) —
// повтор после потерянного ответа не задваивает остаток, приход и товар. Сервер NurCRM не трогается — всё на фейках.
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/admin/admin_receive.dart';
import 'package:nurmarket/features/stock/bulk_import.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body, Map<String, dynamic>? query);

class FakeApi extends ApiClient {
  FakeApi(this.handler) : super(SessionStore());
  Handler handler;
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => handler('GET', path, null, query);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => handler('POST', path, body, null);
  @override
  Future<dynamic> patch(String path, {Object? body}) => handler('PATCH', path, body, null);
  @override
  Future<dynamic> delete(String path) => handler('DELETE', path, null, null);
}

/// Склад NurCRM в памяти: товары, приходы поставщику (идемпотентные по idempotency_key), «потерянные» ответы.
class StockServer {
  final products = <String, Map<String, dynamic>>{};
  final receipts = <String, Map<String, dynamic>>{};
  var _n = 0;

  /// Сколько следующих ответов на эту операцию «потеряется» (сервер выполнил, ответ не дошёл).
  int loseCreate = 0, losePatch = 0, loseReceipt = 0;

  /// Название товара, создание которого сервер отклоняет (400).
  String? rejectName;
  int? receiptStatus;

  String add(String name, {String barcode = '', double qty = 0, bool weight = false, int? plu}) {
    final id = 'p${++_n}';
    products[id] = {'id': id, 'name': name, 'barcode': barcode, 'quantity': qty, 'is_weight': weight, 'plu': plu};
    return id;
  }

  int get created => products.length;

  Future<dynamic> handle(String m, String p, Object? b, Map<String, dynamic>? q) async {
    final body = b is Map ? b.cast<String, dynamic>() : <String, dynamic>{};
    if (m == 'POST' && p == 'api/main/products/create-manual/') {
      if (body['name'] == rejectName) throw ApiException('Неверные данные', status: 400);
      final id = add(body['name'] as String, barcode: (body['barcode'] as String?) ?? '', weight: body['is_weight'] == true, plu: body['plu'] as int?);
      if (loseCreate > 0) {
        loseCreate--;
        throw ApiException('Сервер не ответил вовремя.');
      }
      return {'id': id};
    }
    if (m == 'GET' && p == 'api/main/products/list/') {
      if (q?['is_weight'] == 'true') return {'count': products.values.where((x) => x['is_weight'] == true).length, 'results': []};
      final s = '${q?['search'] ?? ''}'.toLowerCase();
      return {
        'results': [
          for (final x in products.values)
            if ('${x['barcode']}' == s || '${x['name']}'.toLowerCase().contains(s)) x,
        ],
      };
    }
    final pm = RegExp(r'^api/main/products/(p\d+)/$').firstMatch(p);
    if (pm != null) {
      final x = products[pm.group(1)]!;
      if (m == 'GET') return Map<String, dynamic>.from(x);
      if (m == 'PATCH') {
        if (body.containsKey('quantity')) x['quantity'] = (body['quantity'] as num).toDouble();
        if (body.containsKey('price')) x['price'] = body['price'];
        if (losePatch > 0) {
          losePatch--;
          throw ApiException('Сервер не ответил вовремя.');
        }
        return x;
      }
    }
    final rm = RegExp(r'^api/main/suppliers/(\w+)/receipt/$').firstMatch(p);
    if (m == 'POST' && rm != null) {
      if (receiptStatus != null) throw ApiException('Ошибка', status: receiptStatus);
      final key = body['idempotency_key'] as String;
      if (!receipts.containsKey(key)) {
        receipts[key] = body;
        for (final it in (body['items'] as List).cast<Map>()) {
          final x = products[it['product']]!;
          x['quantity'] = (x['quantity'] as num) + (it['qty'] as num);
        }
      }
      if (loseReceipt > 0) {
        loseReceipt--;
        throw ApiException('Сервер не ответил вовремя.');
      }
      return {'id': 'r${receipts.length}'};
    }
    throw ApiException('не ожидали $m $p', status: 404);
  }
}

double stockOf(StockServer s, String id) => (s.products[id]!['quantity'] as num).toDouble();

Future<void> putLocal(AppDatabase db, String id, String name, {String barcode = '', double qty = 0, String? plu, bool weight = false}) =>
    db.upsertProduct(
      ProductsCompanion.insert(
        id: id,
        name: name,
        barcode: Value(barcode.isEmpty ? null : barcode),
        quantity: Value(qty),
        plu: Value(plu),
        isWeight: Value(weight),
        searchKey: Value(name.toLowerCase()),
      ),
    );

void main() {
  late AppDatabase db;
  setUp(() => db = AppDatabase(NativeDatabase.memory()));
  tearDown(() => db.close());

  group('П8 приёмка без поставщика: остаток не задваивается', () {
    test('ответ на PATCH остатка потерялся → повтор той же строки не прибавляет второй раз', () async {
      final s = StockServer();
      final id = s.add('Вода', qty: 10);
      await putLocal(db, id, 'Вода', qty: 10);
      final repo = AdminReceiveRepository(FakeApi(s.handle), db);
      final line = ReceiveLine(productId: id, name: 'Вода', qty: 5, cost: 30, price: 40);
      s.losePatch = 1;
      final (n1, e1) = await repo.post([line], null, false);
      expect(n1, 0);
      expect(e1, isNotEmpty);
      expect(stockOf(s, id), 15, reason: 'сервер записал, ответ потерялся');
      expect(line.posted, isFalse);
      final (n2, e2) = await repo.post([line], null, false);
      expect(n2, 1);
      expect(e2, isEmpty);
      expect(stockOf(s, id), 15, reason: 'повтор не прибавил второй раз');
      expect(line.posted, isTrue);
      expect((await db.productById(id))!.quantity, 15, reason: 'на телефоне прибавлено один раз');
      // третья попытка (двойное нажатие после успеха) — строка уже проведена
      final (n3, _) = await repo.post([line], null, false);
      expect(n3, 0);
      expect(stockOf(s, id), 15);
    });

    test('без ответа, а остаток потом изменился иначе — не прибавляем, говорим проверить', () async {
      final s = StockServer();
      final id = s.add('Сок', qty: 10);
      final repo = AdminReceiveRepository(FakeApi(s.handle), db);
      final line = ReceiveLine(productId: id, name: 'Сок', qty: 5);
      s.losePatch = 1;
      await repo.post([line], null, false);
      s.products[id]!['quantity'] = 13; // продали 2 на кассе
      final (n, errors) = await repo.post([line], null, false);
      expect(n, 0);
      expect(errors.single, contains('Сок'));
      expect(stockOf(s, id), 13);
    });

    test('отказ сервера по существу — строку можно провести снова', () async {
      final s = StockServer();
      final id = s.add('Чай', qty: 2);
      final api = FakeApi(s.handle);
      final repo = AdminReceiveRepository(api, db);
      final line = ReceiveLine(productId: id, name: 'Чай', qty: 3);
      final real = api.handler;
      api.handler = (m, p, b, q) => m == 'PATCH' ? Future.error(ApiException('Нельзя', status: 400)) : real(m, p, b, q);
      final (n1, _) = await repo.post([line], null, false);
      expect(n1, 0);
      expect(line.pendingTarget, isNull);
      api.handler = real;
      final (n2, _) = await repo.post([line], null, false);
      expect(n2, 1);
      expect(stockOf(s, id), 5);
    });
  });

  group('П8 приход от поставщика: ключ повтора', () {
    test('ответ потерялся → повтор тем же ключом = один приход; следующий приход — новый ключ', () async {
      final s = StockServer();
      final id = s.add('Мука', qty: 0);
      final repo = AdminReceiveRepository(FakeApi(s.handle), db);
      final line = ReceiveLine(productId: id, name: 'Мука', qty: 4, cost: 50, price: 70, oldPrice: 70);
      s.loseReceipt = 1;
      await expectLater(repo.post([line], 'sup1', false), throwsA(isA<ApiException>()));
      final (n, _) = await repo.post([line], 'sup1', false);
      expect(n, 1);
      expect(s.receipts.length, 1, reason: 'сервер не провёл приход второй раз');
      expect(stockOf(s, id), 4);
      final next = ReceiveLine(productId: id, name: 'Мука', qty: 4, cost: 50, price: 70, oldPrice: 70);
      await repo.post([next], 'sup1', false);
      expect(s.receipts.length, 2, reason: 'новый приход того же состава — новый ключ');
      expect(stockOf(s, id), 8);
    });

    test('отказ по существу (400) — ключ забыт, повтор после исправления — новый приход', () async {
      final s = StockServer();
      final id = s.add('Рис');
      final repo = AdminReceiveRepository(FakeApi(s.handle), db);
      final line = ReceiveLine(productId: id, name: 'Рис', qty: 1, cost: 10);
      s.receiptStatus = 400;
      await expectLater(repo.post([line], 'sup1', true), throwsA(isA<ApiException>()));
      expect(await db.getSetting(AdminReceiveRepository.pendingReceiptKeySetting), '');
      s.receiptStatus = null;
      final (n, _) = await repo.post([line], 'sup1', true);
      expect(n, 1);
      expect(s.receipts.length, 1);
    });

    test('ключ один для одного состава, другой — для другого', () async {
      final repo = AdminReceiveRepository(FakeApi(StockServer().handle), db);
      final a = [ReceiveLine(productId: 'p1', name: 'A', qty: 1, cost: 5)];
      final b = [ReceiveLine(productId: 'p1', name: 'A', qty: 2, cost: 5)];
      final k1 = await repo.receiptKey(AdminReceiveRepository.receiptSignature('s', false, a));
      final k2 = await repo.receiptKey(AdminReceiveRepository.receiptSignature('s', false, a));
      final k3 = await repo.receiptKey(AdminReceiveRepository.receiptSignature('s', false, b));
      expect(k1, k2);
      expect(k3, isNot(k1));
    });
  });

  group('П10/П8 новый товар: повтор не создаёт второй', () {
    test('ответ на создание потерялся → повтор находит созданный товар по штрихкоду', () async {
      final s = StockServer()..loseCreate = 1;
      final repo = AdminReceiveRepository(FakeApi(s.handle), db);
      final line = ReceiveLine(productId: '', name: 'Новый сок', barcode: '4870000000017', qty: 6, cost: 40, price: 55, isNew: true);
      final (n1, e1) = await repo.post([line], null, false);
      expect(n1, 0);
      expect(e1, isNotEmpty);
      expect(line.createUnknown, isTrue);
      expect(s.created, 1);
      final (n2, _) = await repo.post([line], null, false);
      expect(n2, 1);
      expect(s.created, 1, reason: 'второй товар не создан');
      expect(stockOf(s, line.productId), 6);
    });

    test('без штрихкода — находит по точному названию', () async {
      final s = StockServer()..loseCreate = 1;
      final repo = AdminReceiveRepository(FakeApi(s.handle), db);
      final line = ReceiveLine(productId: '', name: 'Пирожок', qty: 0, price: 30, isNew: true);
      await repo.post([line], null, false);
      await repo.post([line], null, false);
      expect(s.created, 1);
      expect(line.productId, isNotEmpty);
    });

    test('PLU весового товара: число весовых + 1, занятые пропускаются', () async {
      final s = StockServer();
      s.add('Картофель', weight: true, plu: 1);
      s.add('Лук', weight: true, plu: 2);
      final repo = AdminReceiveRepository(FakeApi(s.handle), db);
      await putLocal(db, 'x1', 'Морковь', plu: '3', weight: true);
      expect(await repo.nextPlu(), 4, reason: 'весовых 2 → 3, но 3 занят');
      expect(await repo.nextPluOr('17'), 17);
    });

    test('создание весового товара шлёт is_weight и plu', () async {
      final s = StockServer();
      final repo = AdminReceiveRepository(FakeApi(s.handle), db);
      final id = await repo.createProduct(ReceiveLine(productId: '', name: 'Яблоки', unit: 'кг', price: 120), isWeight: true, plu: 5);
      expect(s.products[id]!['is_weight'], isTrue);
      expect(s.products[id]!['plu'], 5);
    });
  });

  group('П12 массовая загрузка: повтор не создаёт дубли и не удваивает остатки', () {
    List<ImportRow> file() => BulkParser.parse(
      BulkParser.readText(
        'Название;Штрихкод;Цена продажи;Закупка;Количество\n'
        'Вода;111;50;30;10\n'
        'Сок;222;90;60;5\n'
        'Плохой;333;70;40;3\n'
        'Сок дубль;222;95;60;7\n',
      ),
    );

    test('ошибка в одной строке → повтор загружает только её; остатки и товары не задвоены', () async {
      final s = StockServer()..rejectName = 'Плохой';
      final existingId = s.add('Вода', barcode: '111', qty: 4);
      await putLocal(db, existingId, 'Вода', barcode: '111', qty: 4);
      final up = BulkUploader(AdminReceiveRepository(FakeApi(s.handle), db), FakeApi(s.handle));
      final rows = file();
      await up.prepare(db, rows);
      expect(up.duplicates.length, 1, reason: 'второй «222» в файле пропускается');
      expect(up.existing.length, 1);
      final e1 = await up.upload(rows);
      expect(e1, isNotEmpty);
      expect(s.created, 2, reason: 'Вода была, создан Сок, Плохой отклонён');
      expect(stockOf(s, existingId), 14);
      s.rejectName = null;
      final e2 = await up.upload(rows);
      expect(e2, isEmpty);
      expect(s.created, 3, reason: 'создан только Плохой');
      expect(stockOf(s, existingId), 14, reason: 'остаток Воды не прибавлен второй раз');
      final juice = s.products.values.firstWhere((x) => x['name'] == 'Сок');
      expect(juice['quantity'], 5);
      // третье нажатие — нечего загружать
      expect(rows.where(up.canUpload), isEmpty);
      await up.upload(rows);
      expect(s.created, 3);
      expect(stockOf(s, existingId), 14);
    });

    test('потерянный ответ при создании внутри загрузки — повтор не создаёт второй товар', () async {
      final s = StockServer()..loseCreate = 1;
      final up = BulkUploader(AdminReceiveRepository(FakeApi(s.handle), db), FakeApi(s.handle));
      final rows = BulkParser.parse(BulkParser.readText('Название;Штрихкод;Цена продажи;Количество\nХлеб;444;30;8\n'));
      await up.prepare(db, rows);
      await up.upload(rows);
      await up.upload(rows);
      expect(s.created, 1);
      expect(s.products.values.single['quantity'], 8);
    });

    test('«Задать остаток» заменяет, а не прибавляет', () async {
      final s = StockServer();
      final id = s.add('Вода', barcode: '111', qty: 40);
      await putLocal(db, id, 'Вода', barcode: '111', qty: 40);
      final up = BulkUploader(AdminReceiveRepository(FakeApi(s.handle), db), FakeApi(s.handle))..setStock = true;
      final rows = BulkParser.parse(BulkParser.readText('Название;Штрихкод;Цена продажи;Количество\nВода;111;50;60\n'));
      await up.prepare(db, rows);
      expect(await up.upload(rows), isEmpty);
      expect(stockOf(s, id), 60);
      expect((await db.productById(id))!.quantity, 60);
    });

    test('без штрихкода: товар с тем же названием на складе не создаётся второй раз', () async {
      final s = StockServer();
      final id = s.add('Пирожок', qty: 1);
      await putLocal(db, id, 'Пирожок', qty: 1);
      final up = BulkUploader(AdminReceiveRepository(FakeApi(s.handle), db), FakeApi(s.handle));
      final rows = BulkParser.parse(BulkParser.readText('Название;Цена продажи;Количество\nпирожок;30;2\n'));
      await up.prepare(db, rows);
      await up.upload(rows);
      expect(s.created, 1);
      expect(stockOf(s, id), 3);
    });
  });
}
