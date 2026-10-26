// 2026-10-26 (голос-управление, В2): инструменты ЧТЕНИЯ звонка на фейках — без сети, без микрофона, без боевой базы.
// Проверяем: права роли (при объявлении и при вызове), цифры собирает программа и кладёт в таблицу, `data` ≤ 1500 символов,
// имена должников и кассиров в Google не уходят (только коды), сортировка / фильтр / «ещё» голосом, рекомендации, отказ сервера.
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/call_table.dart';
import 'package:nurmarket/core/ai/assistant/read_tools.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/admin_repository.dart';
import 'package:nurmarket/features/admin/analytics.dart';
import 'package:nurmarket/features/ops/ops_repository.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';

final _now = DateTime(2026, 10, 26, 15, 0);
const nb = ' ';

Product _p(String id, String name, {double price = 100, double qty = 10, String unit = 'шт', double min = 0, bool service = false, String category = '', String? barcode}) => Product(
  id: id,
  name: name,
  price: price,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: min,
  quantity: qty,
  unit: unit,
  isWeight: false,
  isService: service,
  isFavorite: false,
  altBarcodes: '',
  category: category,
  searchKey: name.toLowerCase(),
  barcode: barcode,
);

class FakeSource implements ReadSource {
  final calls = <String>[];
  Object? fail;

  SalesSummary Function(DateTime from, DateTime to) salesFn = (f, t) => SalesSummary(48200, 63, 765, 12000, [(DateTime(2026, 10, 24), 20000), (DateTime(2026, 10, 25), 8200), (DateTime(2026, 10, 26), 20000)], {'cash': 30000, 'card': 15000, 'debt': 3200});
  List<ProductSales> Function(DateTime from, DateTime to) productSalesFn = (f, t) => const [];
  List<Product> products = [];
  List<Debtor> debtorList = [];
  List<ClientRow> clientList = [];
  List<ShiftRow> shiftList = [];
  List<SiteOrder> orderList = [];
  List<DeletionRow> deletionList = [];
  List<AuditEntry> auditList = [];
  List<Map<String, dynamic>> companyChecks = [];
  List<Map<String, dynamic>> shiftChecks = [];
  Map<String, dynamic> saleBody = {};
  Map<String, dynamic> report = {};
  List<CashMove> moves = [];
  double? purchase;
  @override
  Cart cart = const Cart();
  @override
  ShiftInfo? shift;

  void _maybeFail() {
    if (fail != null) throw fail!;
  }

  @override
  Future<SalesSummary> sales(DateTime from, DateTime to) async {
    calls.add('sales');
    _maybeFail();
    return salesFn(from, to);
  }

  @override
  Future<List<ProductSales>> productSales(DateTime from, DateTime to) async {
    calls.add('productSales');
    _maybeFail();
    return productSalesFn(from, to);
  }

  @override
  Future<List<Product>> catalog(String query, {String? category, int limit = 20000}) async {
    calls.add('catalog');
    _maybeFail();
    final words = query.toLowerCase().split(RegExp(r'\s+')).where((w) => w.isNotEmpty);
    return [
      for (final p in products)
        if (words.every(p.searchKey.contains) && (category == null || p.category == category)) p,
    ].take(limit).toList();
  }

  @override
  Future<({Product? product, bool unavailable})> findByCode(String code) async {
    calls.add('findByCode');
    return (product: products.where((p) => p.barcode == code).firstOrNull, unavailable: false);
  }

  @override
  Future<double?> purchasePrice(String productId) async {
    calls.add('purchasePrice');
    return purchase;
  }

  @override
  Future<List<Debtor>> debtors() async {
    calls.add('debtors');
    _maybeFail();
    return debtorList;
  }

  @override
  Future<List<ClientRow>> clients(String search) async {
    calls.add('clients');
    return clientList;
  }

  @override
  Future<List<ShiftRow>> shifts() async {
    calls.add('shifts');
    return shiftList;
  }

  @override
  Future<Map<String, dynamic>> shiftReport(String shiftId) async {
    calls.add('shiftReport');
    _maybeFail();
    return report;
  }

  @override
  Future<List<CashMove>> cashFlows(String shiftId) async {
    calls.add('cashFlows:$shiftId');
    return moves;
  }

  @override
  Future<List<SiteOrder>> orders() async {
    calls.add('orders');
    return orderList;
  }

  @override
  Future<({List<Map<String, dynamic>> rows, bool more})> checks({String? shiftId, int page = 1}) async {
    calls.add('checks:${shiftId ?? 'company'}:$page');
    return (rows: page == 1 ? (shiftId != null ? shiftChecks : companyChecks) : <Map<String, dynamic>>[], more: false);
  }

  @override
  Future<Map<String, dynamic>> sale(String id) async {
    calls.add('sale:$id');
    return saleBody;
  }

  @override
  Future<List<DeletionRow>> deletions() async => deletionList;
  @override
  Future<List<AuditEntry>> audit() async => auditList;
}

Session _session({List<String> perms = const [], bool owner = false, String role = 'cashier'}) =>
    Session(email: 'u', access: '', refresh: '', role: role, isOwner: owner, perms: perms, userName: 'Кассир');

final _owner = _session(owner: true, role: 'owner', perms: const ['can_view_products', 'can_view_analytics', 'can_view_clients', 'can_view_orders', 'can_view_shifts']);
final _cashier = _session(perms: const ['can_view_products']);
final _bare = _session();

class Rig {
  final src = FakeSource();
  final tables = TableStore();
  late final ToolRegistry registry;
  var _n = 0;
  Rig() {
    registry = ToolRegistry.withBuiltins();
    registerReadTools(registry, ReadContext(src, tables));
  }

  Future<ToolResult> call(String name, [Map<String, dynamic> args = const {}, Session? s, String lang = 'ru']) =>
      registry.execute(LiveToolCall('c${++_n}', name, args), ToolEnv(session: s ?? _owner, speech: UserSpeechLog(), lang: lang, now: () => _now));

  /// Что услышит Google: ответ инструмента целиком, как уходит в toolResponse.
  String wire(ToolResult r) => jsonEncode(r.toJson());
}

int _dataLen(ToolResult r) => jsonEncode(r.toJson()['data'] ?? const {}).length;

void main() {
  late Rig rig;
  setUp(() => rig = Rig());

  group('права роли', () {
    test('объявляются только доступные: кассир со «Склад» не получает отчёты, долги, клиентов, рекомендации', () {
      final names = rig.registry.forSession(_cashier).map((t) => t.name).toSet();
      expect(names, containsAll(['stock_list', 'product_info', 'checks_list', 'check_details', 'shift_now', 'cash_moves', 'cart_show', 'table_control', 'show_table']));
      expect(names, isNot(contains('sales_report')));
      expect(names, isNot(contains('top_products')));
      expect(names, isNot(contains('abc_report')));
      expect(names, isNot(contains('debtors')));
      expect(names, isNot(contains('client_info')));
      expect(names, isNot(contains('shifts_list')));
      expect(names, isNot(contains('orders_list')));
      expect(names, isNot(contains('deletions_log')));
      expect(names, isNot(contains('audit_log')));
      expect(names, isNot(contains('recommend')));
    });
    test('без «Склад» нет stock_list; владелец видит всё', () {
      expect(rig.registry.forSession(_bare).map((t) => t.name), isNot(contains('stock_list')));
      final all = rig.registry.forSession(_owner).map((t) => t.name).toSet();
      expect(all, containsAll(['sales_report', 'top_products', 'abc_report', 'stock_list', 'debtors', 'client_info', 'shifts_list', 'orders_list', 'recommend', 'deletions_log', 'audit_log']));
    });
    test('при вызове право проверяется ещё раз: отчёт без права — denied, к серверу не ходили', () async {
      final r = await rig.call('sales_report', {'from': 'today'}, _cashier);
      expect(r.status, ToolStatus.denied);
      expect(rig.src.calls, isEmpty);
      expect((await rig.call('debtors', const {}, _cashier)).status, ToolStatus.denied);
      expect((await rig.call('stock_list', const {}, _bare)).status, ToolStatus.denied);
      expect(rig.tables.count, 0);
    });
    test('объявления для модели компактные (весь набор чтения в пределах бюджета)', () {
      final size = ToolDecl.sizeOf(rig.registry.declarations(_owner));
      expect(size, lessThan(8500), reason: 'ориентир ТЗ: все инструменты ≤ 8 тыс.; чтение — основная часть, фактически $size');
    });
  });

  group('продажи и отчёты', () {
    test('итог за день: карточка в таблице, главное число в say, data короткая и без обрезки', () async {
      final r = await rig.call('sales_report', {'from': 'today'});
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('48 200 сом'));
      expect(r.say, contains('63'));
      expect(r.tableId, 't1');
      final t = rig.tables.current!;
      expect(t.card, isTrue);
      expect(t.title, 'Продажи');
      expect(t.subtitle, '26.10');
      expect(_dataLen(r), lessThanOrEqualTo(1500));
      expect(r.toJson()['data'], isNot(contains('truncated')));
      expect((r.data!['highlights'] as List).length, lessThanOrEqualTo(3));
    });
    test('сравнение с прошлым периодом считает программа', () async {
      rig.src.salesFn = (f, t) => f == DateTime(2026, 10, 25) ? SalesSummary(40000, 50, 800, 9000, const [], const {}) : SalesSummary(48200, 63, 765, 12000, const [], const {});
      final r = await rig.call('sales_report', {'from': '2026-10-26', 'to': '2026-10-26'});
      expect(r.say, contains('+21 %'));
    });
    test('прошлый период недоступен — отчёт всё равно есть', () async {
      var n = 0;
      rig.src.salesFn = (f, t) => ++n == 1 ? SalesSummary(100, 1, 100, 10, const [], const {}) : throw ApiException('x');
      final r = await rig.call('sales_report', {'from': 'today'});
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('100 сом'));
    });
    test('по дням: таблица с «Итого», лучший день называется в say', () async {
      final r = await rig.call('sales_report', {'from': 'week', 'group_by': 'day'});
      expect(r.status, ToolStatus.ok);
      final t = rig.tables.current!;
      expect(t.rows.length, 3);
      expect(t.totalText(1), '48${nb}200$nbсом');
      expect(r.say, contains('лучший день'));
      expect(r.say, contains('20 000 сом'));
      expect(t.subtitle, '20.10–26.10');
    });
    test('по способам оплаты: статусы словами, доли', () async {
      final r = await rig.call('sales_report', {'from': 'today', 'group_by': 'payment'});
      final t = rig.tables.current!;
      expect(t.cellText(0, 0), 'наличные');
      expect(t.cellText(0, 2), '62$nb%'); // 30000 / 48200
      expect(r.say, contains('наличные'));
    });
    test('язык ответа: кыргызский, английский', () async {
      final ky = await rig.call('sales_report', {'from': 'today'}, null, 'ky');
      expect(ky.say, contains('түшүм'));
      expect(rig.tables.current!.title, 'Сатуулар');
      final en = await rig.call('sales_report', {'from': 'today'}, null, 'en');
      expect(en.say, contains('revenue'));
      expect(en.say, contains('48,200 som'));
    });
    test('период: ключевые слова и перепутанные даты', () {
      final w = parsePeriod({'from': 'week'}, _now);
      expect((w.from, w.to), (DateTime(2026, 10, 20), DateTime(2026, 10, 26)));
      final m = parsePeriod({'from': 'month'}, _now);
      expect(m.from, DateTime(2026, 10, 1));
      final lm = parsePeriod({'from': 'last_month'}, _now);
      expect((lm.from, lm.to), (DateTime(2026, 9, 1), DateTime(2026, 9, 30)));
      final y = parsePeriod({'from': 'yesterday'}, _now);
      expect(y.from, DateTime(2026, 10, 25));
      final swapped = parsePeriod({'from': '2026-10-20', 'to': '2026-10-10'}, _now);
      expect((swapped.from, swapped.to), (DateTime(2026, 10, 10), DateTime(2026, 10, 20)));
      final one = parsePeriod({'from': '2026-10-05'}, _now);
      expect(one.from, one.to);
      final dm = parsePeriod({'from': '5.10'}, _now);
      expect(dm.from, DateTime(2026, 10, 5));
      expect(parsePeriod(const {}, _now).from, DateTime(2026, 10, 26));
      expect(parsePeriod({'from': '2020-01-01', 'to': '2026-10-26'}, _now).days, 366);
      expect(parsePeriod({'from': 'week'}, _now).previous.to, DateTime(2026, 10, 19));
    });
    test('нет связи — offline, не выдумывает цифры; сервер медленный/отказ — тоже честно', () async {
      rig.src.fail = ApiException('Нет связи', status: null);
      final r = await rig.call('sales_report', {'from': 'today'});
      expect(r.status, ToolStatus.offline);
      expect(r.say, contains('Нет связи'));
      expect(rig.tables.count, 0);
      rig.src.fail = ApiException('нет доступа', status: 403);
      expect((await rig.call('sales_report', {'from': 'today'})).status, ToolStatus.denied);
      rig.src.fail = ApiException('плохо', status: 400);
      expect((await rig.call('sales_report', {'from': 'today'})).status, ToolStatus.error);
    });
  });

  group('топы и ABC', () {
    setUp(() {
      rig.src.productSalesFn = (f, t) => const [
        ProductSales('Кола', 120, 7200, 30),
        ProductSales('Марс', 30, 1500, 30),
        ProductSales('Вода', 300, 6000, 12),
        ProductSales('Хлеб', 80, 2400, 20),
        ProductSales('Соль', 4, 80, 25), // продаётся в убыток
      ];
    });
    test('топ по выручке: первая строка, лимит видимых, остальное — «ещё»', () async {
      final r = await rig.call('top_products', {'from': 'week', 'limit': 2});
      final t = rig.tables.current!;
      expect(t.visibleIndices.length, 2);
      expect(t.rows.length, 5);
      expect(t.hasMore, isTrue);
      expect(t.rows[t.visibleIndices.first][0], 'Кола');
      expect(r.say, contains('Кола'));
      expect(r.say, contains('7 200 сом'));
      final more = await rig.call('table_control', {'action': 'more'});
      expect(more.status, ToolStatus.ok);
      expect(rig.tables.current!.visibleIndices.length, 5);
    });
    test('худшие по количеству и прибыль', () async {
      await rig.call('top_products', {'from': 'week', 'metric': 'qty', 'order': 'asc'});
      var t = rig.tables.current!;
      expect(t.rows[t.visibleIndices.first][0], 'Соль');
      expect(t.title, 'Хуже всего продаётся');
      await rig.call('top_products', {'from': 'week', 'metric': 'profit'});
      t = rig.tables.current!;
      expect(t.rows[t.visibleIndices.first][0], 'Кола'); // 7200 - 120*30 = 3600; Вода 2400
      expect(t.rows[t.visibleIndices.last][0], 'Соль'); // отрицательная прибыль — красным
      expect(t.isNegative(t.visibleIndices.last, 3), isTrue);
    });
    test('ABC: группы A/B/C считаются как на экране «ABC-анализ»', () async {
      final r = await rig.call('abc_report', {'from': 'month'});
      final t = rig.tables.current!;
      expect(t.rows.first[0], 'Кола');
      expect(t.rows.first[4], 'A');
      expect(r.say, contains('A'));
      // доли и накопление
      final total = 7200 + 1500 + 6000 + 2400 + 80;
      expect(t.rows.first[2], closeTo(7200 / total * 100, 1e-9));
      expect(t.rows.last[3], closeTo(100, 1e-9));
    });
    test('нет продаж — честный ответ и пустая таблица', () async {
      rig.src.productSalesFn = (f, t) => const [];
      final r = await rig.call('top_products', {'from': 'today'});
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('нет'));
    });
  });

  group('склад и товар', () {
    setUp(() {
      rig.src.products = [
        _p('1', 'Кола 0,5 л', price: 60, qty: 4, category: 'Напитки', barcode: '111'),
        _p('2', 'Кола 1,5 л', price: 120, qty: 0, category: 'Напитки', barcode: '222'),
        _p('3', 'Хлеб', price: 30, qty: 50, min: 10),
        _p('4', 'Мука', price: 90, qty: 8, min: 5), // минимум 5: 8 — ещё много
        _p('5', 'Доставка', price: 200, qty: 0, service: true),
        _p('6', 'Сахар', price: 80, qty: 3, min: 5),
      ];
    });
    test('мало осталось: порог товара учитывается, услуги не считаются, сначала самое малое', () async {
      final r = await rig.call('stock_list', {'filter': 'low'});
      final t = rig.tables.current!;
      final names = [for (final i in t.visibleIndices) t.rows[i][0]];
      expect(names, ['Сахар', 'Кола 0,5 л']);
      expect(r.say, contains('Мало осталось 2'));
      expect(r.say, contains('Сахар'));
      expect(t.cellText(t.visibleIndices.first, 1), '3$nbшт');
      expect(t.refs[t.visibleIndices.first], '6');
      expect(t.objectRoute, 'product');
    });
    test('закончились и поиск по слову', () async {
      expect((await rig.call('stock_list', {'filter': 'out'})).say, contains('Закончилось 1'));
      await rig.call('stock_list', {'query': 'кола'});
      expect(rig.tables.current!.rows.length, 2);
      final none = await rig.call('stock_list', {'filter': 'low', 'query': 'нет такого'});
      expect(none.say, contains('Ничего не заканчивается'));
    });
    test('цена одного товара: единственное совпадение — карточка; остаток только с правом «Склад»', () async {
      final withStock = await rig.call('product_info', {'query': 'хлеб'}, _cashier);
      expect(withStock.say, contains('Хлеб'));
      expect(withStock.say, contains('30 сом'));
      expect(withStock.say, contains('остаток 50 шт'));
      final t1 = rig.tables.current!;
      expect(t1.card, isTrue);
      final noStock = await rig.call('product_info', {'query': 'хлеб'}, _bare);
      expect(noStock.say, contains('30 сом'));
      expect(noStock.say, isNot(contains('остаток')));
      expect(noStock.say, isNot(contains('50')));
      final t2 = rig.tables.current!;
      expect(t2.formattedRows(all: true).expand((e) => e).join(' '), isNot(contains('Остаток')));
    });
    test('закупка и наценка — только с правом цены/закупок; без права сервер даже не спрашивают', () async {
      rig.src.purchase = 20;
      final plain = await rig.call('product_info', {'query': 'хлеб'}, _cashier);
      expect(rig.src.calls, isNot(contains('purchasePrice')));
      expect(rig.tables.current!.formattedRows(all: true).expand((e) => e).join(' '), isNot(contains('Закупка')));
      expect(rig.wire(plain), isNot(contains('Закупка')));
      final admin = _session(role: 'admin', perms: const ['can_view_products']);
      await rig.call('product_info', {'query': 'хлеб'}, admin);
      expect(rig.src.calls, contains('purchasePrice'));
      final rows = rig.tables.current!.formattedRows(all: true);
      expect(rows.any((r) => r[0] == 'Закупка' && r[1] == '20$nbсом'), isTrue);
      expect(rows.any((r) => r[0] == 'Наценка' && r[1] == '+50$nb%'), isTrue);
    });
    test('две «Колы» — ambiguous с номерами; «вторую» (pick: 2) — цена 1,5 л', () async {
      final r = await rig.call('product_info', {'query': 'кола'});
      expect(r.status, ToolStatus.ambiguous);
      expect(r.options!.length, 2);
      expect(r.options!.first['n'], 1);
      expect(r.say, contains('1 — Кола 0,5 л'));
      expect(rig.tables.current!.title, 'Какой товар?');
      final second = await rig.call('product_info', {'query': 'кола', 'pick': 2});
      expect(second.status, ToolStatus.ok);
      expect(second.say, contains('Кола 1,5 л'));
      expect(second.say, contains('120 сом'));
    });
    test('точное название выигрывает, штрихкод ищет по коду, нет товара — not_found', () async {
      rig.src.products = [...rig.src.products, _p('7', 'Кола', price: 55)];
      final exact = await rig.call('product_info', {'query': 'кола'});
      expect(exact.status, ToolStatus.ok);
      expect(exact.say, contains('55 сом'));
      final bar = await rig.call('product_info', {'barcode': '222'});
      expect(bar.say, contains('Кола 1,5 л'));
      expect((await rig.call('product_info', {'query': 'абракадабра'})).status, ToolStatus.notFound);
      expect((await rig.call('product_info')).status, ToolStatus.clarify);
    });
  });

  group('должники, клиенты, сотрудники: имена не уходят в Google', () {
    setUp(() {
      rig.src.debtorList = [
        const Debtor('c2', 'Бакыт Сейитов', '0700222333', 1500, 1),
        const Debtor('c1', 'Асан Иванов', '0555111222', 5200, 3),
        const Debtor('c3', '', '', 300, 1),
      ];
    });
    test('таблица с кодами по месту в списке; say без имён; имена только на экране', () async {
      final r = await rig.call('debtors');
      expect(r.status, ToolStatus.ok);
      expect(r.say, 'Должников 3, всего 7 000 сом.');
      final wire = rig.wire(r);
      for (final secret in ['Асан', 'Бакыт', 'Иванов', 'Сейитов', '0555', '0700']) {
        expect(wire, isNot(contains(secret)), reason: secret);
      }
      expect(wire, contains('[Д1]'));
      final t = rig.tables.current!;
      expect(t.rows.first[0], '[Д1]'); // самый большой долг
      expect(t.cellText(0, 0), contains('Асан Иванов'));
      expect(t.cellText(0, 0), contains('0555111222'));
      expect(t.cellText(2, 0), '[Д3]'); // без имени — остаётся кодом
      expect(t.totalText(1), '7${nb}000$nbсом');
    });
    test('минимальная сумма и лимит; коды не меняются от фильтра', () async {
      await rig.call('debtors', {'min_amount': 1000});
      final t = rig.tables.current!;
      expect(t.rows.length, 2);
      expect(t.rows.last[0], '[Д2]');
    });
    test('нет должников — честно; нет права «Клиенты» — denied', () async {
      rig.src.debtorList = [];
      expect((await rig.call('debtors')).say, 'Непогашенных долгов нет.');
      expect((await rig.call('debtors', const {}, _cashier)).status, ToolStatus.denied);
    });
    test('триста должников с длинными именами: data всё равно ≤ 1500 и без обрезки', () async {
      rig.src.debtorList = [for (var i = 0; i < 300; i++) Debtor('c$i', 'Очень длинное имя клиента номер $i Фамилия', '0555$i', 100.0 + i, 2)];
      final r = await rig.call('debtors');
      expect(_dataLen(r), lessThanOrEqualTo(1500));
      expect(rig.wire(r), isNot(contains('truncated')));
      expect(rig.tables.current!.rows.length, 300);
      expect(rig.tables.current!.visibleIndices.length, 20);
    });
    test('клиент по имени: коды [К…], имя и телефон на экране', () async {
      rig.src.clientList = const [ClientRow('k1', 'Нурлан Абдыев', '0777000111', 800, 150)];
      final r = await rig.call('client_info', {'query': 'нурлан'});
      expect(r.say, 'Долг 800 сом, бонусы 150 сом.');
      expect(rig.wire(r), isNot(contains('Нурлан')));
      expect(rig.wire(r), isNot(contains('0777')));
      expect(rig.tables.current!.cellText(0, 0), contains('Нурлан Абдыев'));
      rig.src.clientList = [];
      expect((await rig.call('client_info', {'query': 'нет'})).status, ToolStatus.notFound);
      expect((await rig.call('client_info', const {})).status, ToolStatus.clarify);
    });
    test('смены: кассиры кодами [С…]; неполные данные (ровно 100 смен) помечены', () async {
      rig.src.shiftList = [
        for (var i = 0; i < 100; i++)
          ShiftRow('s$i', i.isEven ? 'Айбек Токтогулов' : 'Нурлан Абдыев', 'Касса 1', DateTime(2026, 10, 26).subtract(Duration(days: i ~/ 4)), i == 0 ? null : DateTime(2026, 10, 26, 22), 1000, 800, 10),
      ];
      final r = await rig.call('shifts_list', {'from': 'month'});
      expect(rig.wire(r), isNot(contains('Айбек')));
      expect(rig.wire(r), isNot(contains('Нурлан')));
      expect(rig.wire(r), contains('[С1]'));
      final t = rig.tables.current!;
      expect(t.note, contains('100 смен'));
      expect(r.say, contains('могут быть неполными'));
      expect(t.cellText(0, 1), 'Айбек Токтогулов');
      expect((await rig.call('shifts_list', const {}, _cashier)).status, ToolStatus.denied);
    });
    test('заказы: покупатели кодами', () async {
      rig.src.orderList = [
        SiteOrder('o1', '1001', 'Гульнара', '0555999888', 'new', 2500, DateTime(2026, 10, 26, 10), false, const []),
        SiteOrder('o2', '1000', 'Чынара', '', 'done', 900, DateTime(2026, 10, 25, 10), false, const []),
      ];
      final r = await rig.call('orders_list');
      expect(r.say, 'Заказов 2 на 3 400 сом, новых 1.');
      expect(rig.wire(r), isNot(contains('Гульнара')));
      expect((await rig.call('orders_list', {'status': 'done'})).say, startsWith('Заказов 1'));
      expect(rig.tables.current!.cellText(0, 2), 'сделано');
    });
  });

  group('чеки', () {
    setUp(() {
      rig.src.shift = const ShiftInfo(id: 'sh1');
      rig.src.companyChecks = [
        {'id': 's9', 'number': '1050', 'total': 900, 'created_at': '2026-10-26T09:00:00', 'payment_method': 'card', 'status': 'completed'},
        {'id': 's8', 'number': '1049', 'total': 400, 'created_at': '2026-10-25T09:00:00', 'payment_method': 'cash', 'status': 'returned'},
        {'id': 's7', 'number': '1043', 'total': 1250, 'created_at': '2026-10-25T08:00:00', 'payment_method': 'cash', 'status': 'completed'},
      ];
      rig.src.shiftChecks = [
        {'id': 's9', 'number': '1050', 'total': 900, 'created_at': '2026-10-26T09:00:00', 'payment_method': 'card', 'status': 'completed'},
      ];
      rig.src.saleBody = {
        'total': 1250,
        'items': [
          {'product_name': 'Кола', 'quantity': 2, 'unit_price': 60, 'total': 120},
          {'product_name': 'Хлеб', 'quantity': 1, 'unit_price': 30, 'total': 30},
        ],
      };
    });
    test('с правом «Отчёты» — продажи компании; возвращённый чек не входит в сумму', () async {
      final r = await rig.call('checks_list', {'from': '2026-10-25', 'to': '2026-10-26'});
      expect(rig.src.calls, contains('checks:company:1'));
      expect(rig.tables.current!.rows.length, 3);
      expect(r.say, 'Нашла 3 чеков на 2 150 сом.'); // 900 + 1250, возврат 400 не считается
    });
    test('фильтры: оплата, минимальная сумма', () async {
      await rig.call('checks_list', {'from': '2026-10-25', 'to': '2026-10-26', 'payment': 'cash', 'min_total': 1000});
      expect(rig.tables.current!.rows.length, 1);
      expect(rig.tables.current!.rows.first[0], '1043');
    });
    test('без «Отчётов» — только чеки своей смены, всей компании не видит', () async {
      final r = await rig.call('checks_list', const {}, _cashier);
      expect(rig.src.calls, contains('checks:sh1:1'));
      expect(rig.src.calls, isNot(contains('checks:company:1')));
      expect(rig.tables.current!.rows.length, 1);
      expect(r.status, ToolStatus.ok);
      rig.src.shift = null;
      expect((await rig.call('checks_list', const {}, _cashier)).say, 'Чеков не нашла.');
    });
    test('содержимое чека: строки и итог; «последний чек»; нет такого — not_found', () async {
      final r = await rig.call('check_details', {'number': '№1043'});
      expect(r.say, contains('Чек 1043: 2 позиций на 1 250 сом'));
      final t = rig.tables.current!;
      expect(t.rows.length, 2);
      expect(t.cellText(0, 3), '120$nbсом');
      expect(t.totalText(3), '1${nb}250$nbсом');
      final last = await rig.call('check_details', {'last': true});
      expect(last.say, contains('1050'));
      expect((await rig.call('check_details', {'number': '9999'})).status, ToolStatus.notFound);
      expect((await rig.call('check_details')).status, ToolStatus.clarify);
    });
  });

  group('смена, касса, корзина, журналы', () {
    test('смена не открыта — ok без таблицы; открыта — карточка по отчёту сервера', () async {
      expect((await rig.call('shift_now')).say, 'Смена сейчас не открыта.');
      expect(rig.tables.count, 0);
      rig.src.shift = ShiftInfo(id: 'sh1', openedAt: DateTime(2026, 10, 26, 9, 12), salesTotal: 1000, expectedCash: 1500);
      rig.src.report = {
        'sales_total': 4800,
        'sales_count': 12,
        'cash_sales': 3000,
        'noncash_sales': 1800,
        'by_payment': {'debt': 200},
        'expected_cash': 5100,
        'opening_cash': 500,
      };
      final r = await rig.call('shift_now');
      expect(r.say, 'Смена открыта: выручка 4 800 сом, чеков 12, в кассе должно быть 5 100 сом.');
      final rows = rig.tables.current!.formattedRows(all: true);
      expect(rows.any((e) => e[0] == 'Должно быть в кассе' && e[1] == '5${nb}100$nbсом'), isTrue);
      expect(rows.any((e) => e[0] == 'Открыта с' && e[1] == '09:12'), isTrue);
      expect(rig.tables.current!.note, isNull);
    });
    test('сервер не отдал отчёт смены — цифры телефона и пометка', () async {
      rig.src.shift = ShiftInfo(id: 'sh1', salesTotal: 1000, expectedCash: 1500, salesCount: 4);
      rig.src.fail = ApiException('нет связи');
      final r = await rig.call('shift_now');
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('1 000 сом'));
      expect(rig.tables.current!.note, contains('Цифры телефона'));
    });
    test('внесения и изъятия своей смены; чужую — только с правом «Смены»', () async {
      rig.src.shift = const ShiftInfo(id: 'sh1');
      rig.src.moves = [
        (DateTime(2026, 10, 26, 10), 'Внесение: размен', 1000.0, false),
        (DateTime(2026, 10, 26, 12), 'Изъятие: инкассация', 300.0, true),
      ];
      final r = await rig.call('cash_moves', const {}, _cashier);
      expect(r.say, 'Внесено 1 000 сом, изъято 300 сом.');
      expect(rig.tables.current!.rows.last[3], -300.0);
      expect(rig.tables.current!.isNegative(1, 3), isTrue);
      expect((await rig.call('cash_moves', {'shift': 'other'}, _cashier)).status, ToolStatus.denied);
      expect((await rig.call('cash_moves', {'shift': 'other'})).status, ToolStatus.ok);
      expect(rig.src.calls, contains('cashFlows:other'));
    });
    test('корзина: строки, скидка, итог программы', () async {
      expect((await rig.call('cart_show')).say, 'Чек пустой.');
      rig.src.cart = Cart(
        lines: [CartLine(productId: 'p1', name: 'Кола', price: 60, qty: 2), CartLine(productId: 'p2', name: 'Хлеб', price: 30, discount: 5)],
        orderDiscountSum: 10,
      );
      final r = await rig.call('cart_show', const {}, _bare);
      expect(r.say, 'В чеке 2 позиций, итого 135 сом.');
      final t = rig.tables.current!;
      expect(t.cellText(1, 3), '-5$nbсом');
      expect(t.totalText(4), '135$nbсом');
      expect(t.note, contains('10 сом'));
    });
    test('удалённое из чека и журнал аудита: кассиры кодами, фильтр по виду', () async {
      rig.src.deletionList = [DeletionRow(DateTime(2026, 10, 26, 11), 'Кола', 1, 60, 'ошибся', 'Асель')];
      final d = await rig.call('deletions_log', {'from': 'week'});
      expect(d.say, 'Удалено 1 строк на 60 сом.');
      expect(rig.wire(d), isNot(contains('Асель')));
      rig.src.auditList = [
        AuditEntry(DateTime(2026, 10, 26, 11), 'Асель', 'price', 'Цена Кола 60 → 65', null),
        AuditEntry(DateTime(2026, 10, 26, 12), 'Асель', 'sale', 'Продажа', 120),
      ];
      final a = await rig.call('audit_log', {'from': 'today', 'kind': 'price'});
      expect(a.say, 'В журнале 1 записей.');
      expect(rig.wire(a), isNot(contains('Асель')));
      expect(Audit.kinds, contains('price'));
    });
  });

  group('рекомендации', () {
    setUp(() {
      rig.src.products = [
        _p('1', 'Кола', price: 60, qty: 0),
        _p('2', 'Вода', price: 30, qty: 5),
        _p('3', 'Хлеб', price: 30, qty: 100),
        _p('4', 'Чай', price: 200, qty: 4),
        _p('5', 'Шампунь', price: 700, qty: 10),
        _p('6', 'Сахар', price: 110, qty: 20),
        _p('7', 'Мука', price: 150, qty: 20),
        _p('8', 'Соль', price: 40, qty: 20),
        _p('9', 'Доставка', price: 100, qty: 0, service: true),
      ];
      rig.src.productSalesFn = (f, t) => const [
        ProductSales('Кола', 30, 1800, 40),
        ProductSales('Вода', 60, 1800, 12),
        ProductSales('Хлеб', 30, 900, 20),
        ProductSales('Сахар', 40, 4000, 100),
        ProductSales('Мука', 40, 6000, 100),
        ProductSales('Соль', 10, 400, 50),
      ];
    });
    test('что заказать: закончилось и хватит меньше 7 дней; сколько заказать на 14 дней', () async {
      final r = await rig.call('recommend', {'kind': 'reorder'});
      final t = rig.tables.current!;
      expect([for (final row in t.rows) row[0]], ['Кола', 'Вода']);
      expect(t.cellText(0, 4), '14$nbшт'); // 1 в день * 14 - 0
      expect(t.cellText(1, 4), '23$nbшт'); // 2 в день * 14 - 5
      expect(r.say, contains('Заказать нужно 2 товаров, закончилось 1'));
      expect(r.say, contains('Кола'));
    });
    test('неликвид: остаток есть, продаж за 60 дней нет; деньги — только с правом на цены', () async {
      final r = await rig.call('recommend', {'kind': 'dead_stock'}, _session(role: 'admin', perms: const ['can_view_products', 'can_view_analytics']));
      var t = rig.tables.current!;
      expect([for (final row in t.rows) row[0]], ['Шампунь', 'Чай']);
      expect(t.rows.first[2], 7000.0);
      expect(r.say, contains('заморожено около 7 800 сом'));
      expect(r.say, contains('Шампунь'));
      final plain = await rig.call('recommend', {'kind': 'dead_stock'}, _session(perms: const ['can_view_products', 'can_view_analytics']));
      t = rig.tables.current!;
      expect(t.columns.length, 2);
      expect(plain.say, isNot(contains('сом')));
      expect(rig.wire(plain), isNot(contains('7000')));
    });
    test('низкая наценка: ниже 20 % к закупке, цена с 20 % округлена вверх, продажа ниже закупки видна', () async {
      final r = await rig.call('recommend', {'kind': 'low_markup'}, _session(role: 'admin', perms: const ['can_view_analytics']));
      final t = rig.tables.current!;
      expect([for (final row in t.rows) row[0]], ['Соль', 'Сахар']); // Соль: закупка 50, цена 40 → -20 %; Сахар: 100 → 110 = +10 %
      expect(t.rows.first[3], closeTo(-20, 1e-9));
      expect(t.rows.first[4], 60.0);
      expect(r.say, contains('ниже закупки из них: 1'));
    });
    test('что продвигать: высокая наценка и рост продаж', () async {
      rig.src.productSalesFn = (f, t) => f == DateTime(2026, 10, 13)
          ? const [ProductSales('Кола', 30, 3000, 20), ProductSales('Вода', 5, 400, 30), ProductSales('Хлеб', 5, 150, 20)]
          : const [ProductSales('Кола', 10, 1000, 20), ProductSales('Вода', 5, 400, 30), ProductSales('Хлеб', 20, 600, 20)];
      final r = await rig.call('recommend', {'kind': 'promote'}, _session(role: 'admin', perms: const ['can_view_analytics']));
      final t = rig.tables.current!;
      expect(t.rows.first[0], 'Кола'); // цена 60 при закупке 20 = +200 %, рост 200 %
      expect(r.say, contains('Кола'));
    });
    test('без права на закупки наценку не показывает; без «Склад» — остатки', () async {
      final noCost = await rig.call('recommend', {'kind': 'low_markup'}, _session(perms: const ['can_view_analytics', 'can_view_products']));
      expect(noCost.status, ToolStatus.denied);
      expect(rig.src.calls, isNot(contains('productSales')));
      final noStock = await rig.call('recommend', {'kind': 'reorder'}, _session(perms: const ['can_view_analytics']));
      expect(noStock.status, ToolStatus.denied);
    });
    test('сопутствующие и заказ поставщику: в мобилке нет чтения — честно говорит', () async {
      for (final k in ['companions', 'supplier_order']) {
        final r = await rig.call('recommend', {'kind': k});
        expect(r.status, ToolStatus.error);
        expect(r.say, contains('недоступна'));
      }
      expect((await rig.call('recommend', {'kind': 'что-то'})).status, ToolStatus.clarify);
    });
    test('нечего заказать — так и говорит', () async {
      rig.src.productSalesFn = (f, t) => const [ProductSales('Хлеб', 30, 900, 20)];
      final r = await rig.call('recommend', {'kind': 'reorder'});
      expect(r.say, contains('Заказывать пока нечего'));
    });
  });

  group('управление таблицей голосом', () {
    setUp(() async {
      rig.src.debtorList = [
        const Debtor('a', 'Асан', '', 500, 1),
        const Debtor('b', 'Бакыт', '', 5200, 2),
        const Debtor('c', 'Чолпон', '', 1500, 3),
        const Debtor('d', 'Дастан', '', 80, 1),
      ];
      await rig.call('debtors');
    });
    test('«отсортируй по сумме» — по убыванию, первая строка называется без кода', () async {
      final r = await rig.call('table_control', {'action': 'sort', 'column': 'сумма'});
      expect(r.status, ToolStatus.ok);
      expect(r.say, contains('Долг'));
      expect(r.say, contains('сначала большие'));
      expect(r.say, contains('5 200 сом'));
      expect(r.say, isNot(contains('[Д')));
      expect(r.tableId, 't1');
      expect(rig.tables.current!.sortKey, 'debt');
    });
    test('по возрастанию и по алфавиту; неизвестная колонка — переспрос со списком', () async {
      await rig.call('table_control', {'action': 'sort', 'column': 'долг', 'order': 'asc'});
      final t = rig.tables.current!;
      expect(t.rows[t.visibleIndices.first][1], 80.0);
      final bad = await rig.call('table_control', {'action': 'sort', 'column': 'цвет'});
      expect(bad.status, ToolStatus.clarify);
      expect(bad.say, contains('Должник'));
    });
    test('«только где долг больше 1000» и снять фильтр', () async {
      final r = await rig.call('table_control', {'action': 'filter', 'column': 'долг', 'value': '>1000'});
      expect(r.say, contains('Оставила 2 из 4'));
      expect(rig.tables.current!.viewCount, 2);
      final off = await rig.call('table_control', {'action': 'filter', 'value': ''});
      expect(off.say, contains('Фильтр снят'));
      expect(rig.tables.current!.viewCount, 4);
      expect((await rig.call('table_control', {'action': 'filter', 'column': 'долг', 'value': 'много'})).status, ToolStatus.clarify);
    });
    test('«покажи ещё», когда больше нечего показывать', () async {
      expect((await rig.call('table_control', {'action': 'more'})).say, 'Это все строки.');
    });
    test('закрепить, открепить, размер, закрыть и вернуть «покажи таблицу»', () async {
      await rig.call('table_control', {'action': 'pin'});
      expect(rig.tables.isPinned('t1'), isTrue);
      await rig.call('table_control', {'action': 'unpin'});
      expect(rig.tables.isPinned('t1'), isFalse);
      await rig.call('table_control', {'action': 'bigger'});
      expect(rig.tables.textScale, 1.3);
      await rig.call('table_control', {'action': 'smaller'});
      expect(rig.tables.textScale, 1.0);
      await rig.call('table_control', {'action': 'close'});
      expect(rig.tables.isClosed, isTrue);
      final back = await rig.call('show_table', {'table_id': 't1'});
      expect(back.status, ToolStatus.ok);
      expect(rig.tables.isClosed, isFalse);
      expect((await rig.call('show_table', {'table_id': 'zzz'})).status, ToolStatus.notFound);
      expect((await rig.call('show_table')).status, ToolStatus.ok);
    });
    test('экспорт: без экрана честно «не получится», с обработчиком — делает', () async {
      expect((await rig.call('table_control', {'action': 'export_pdf'})).status, ToolStatus.error);
      final got = <String>[];
      rig.tables.onExport = (a, t) async {
        got.add('$a:${t.id}');
        return true;
      };
      for (final a in ['export_chat', 'export_pdf', 'share']) {
        expect((await rig.call('table_control', {'action': a})).status, ToolStatus.ok);
      }
      expect(got, ['export_chat:t1', 'export_pdf:t1', 'share:t1']);
    });
    test('управление указанной таблицей по id; нет таблиц — not_found', () async {
      await rig.call('sales_report', {'from': 'today', 'group_by': 'day'});
      expect(rig.tables.current!.id, 't2');
      final r = await rig.call('table_control', {'action': 'sort', 'column': 'долг', 'table_id': 't1', 'order': 'asc'});
      expect(r.tableId, 't1');
      expect(rig.tables.byId('t1')!.sortKey, 'debt');
      final empty = Rig();
      expect((await empty.call('table_control', {'action': 'more'})).status, ToolStatus.notFound);
    });
    test('карточку не сортируют — понятный ответ', () async {
      await rig.call('sales_report', {'from': 'today'});
      final r = await rig.call('table_control', {'action': 'sort', 'column': 'значение'});
      expect(r.status, ToolStatus.clarify);
      expect(r.say, contains('Карточку'));
    });
  });

  group('общее', () {
    test('чтение не требует слов-просьбы и не попадает под режим «только смотреть»', () async {
      final r = await rig.registry.execute(
        LiveToolCall('x', 'stock_list', const {}),
        ToolEnv(session: _owner, speech: UserSpeechLog(), mode: AssistantMode.viewOnly, now: () => _now),
      );
      expect(r.status, ToolStatus.ok);
    });
    test('все ответы укладываются в 1500 символов data, в журнале вызовов нет данных магазина', () async {
      rig.src.products = [for (var i = 0; i < 200; i++) _p('$i', 'Товар с довольно длинным названием номер $i', qty: i.toDouble())];
      final r = await rig.call('stock_list', {'filter': 'all'});
      expect(_dataLen(r), lessThanOrEqualTo(1500));
      expect(rig.registry.journal.entries.last.tool, 'stock_list');
    });
    test('язык по умолчанию — русский; незнакомый язык — русский', () async {
      final r = await rig.call('cart_show', const {}, null, 'xx');
      expect(r.say, 'Чек пустой.');
    });
    test('каждый инструмент чтения зарегистрирован один раз и не пересекается с реестром каркаса', () {
      final names = rig.registry.names.toList();
      expect(names.toSet().length, names.length);
      expect(names, contains('app_status'));
      expect(buildReadTools(ReadContext(rig.src, rig.tables)).map((t) => t.name).toSet().length, 19);
    });
  });
}
