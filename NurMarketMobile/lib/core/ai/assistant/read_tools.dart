// 2026-10-26 (голос-управление, В2): инструменты ЧТЕНИЯ для звонка с Нур Советником — остатки, цены, продажи за период,
// долги, смена, поиск товара и клиента, топы, рекомендации по товарам, журналы, плюс управление таблицей на экране
// (раздел 3.3 ТЗ). Ничего не меняют: данные берутся из СУЩЕСТВУЮЩИХ репозиториев мобилки через [ReadSource] (в тестах —
// фейк, без сети). Каждый ответ с цифрами собирает ПРОГРАММА: таблица [CallTable] кладётся в [TableStore] (её покажет панель
// В4), модели уходит коротко (`data` ≤ 1500 символов: id таблицы, колонки, 5 строк, 1–3 главных числа). ИИ таблицу не
// зачитывает, а называет одно-два главных числа из `highlights` (раздел 4.4 ТЗ). Имена должников, клиентов и кассиров в
// Google не уходят — только коды [Д1], [К1], [С1]; имена подставляет экран ([CallTable.names]).
import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;

import 'package:flutter_riverpod/misc.dart' show ProviderListenable;

import '../../../features/admin/admin_repository.dart';
import '../../../features/admin/analytics.dart';
import '../../../features/ops/ops_repository.dart';
import '../../../features/payment/sales_repository.dart';
import '../../../features/sale/cart.dart';
import '../../../features/scan/code_lookup.dart';
import '../../../features/shift/shift_repository.dart';
import '../../api/api_client.dart';
import '../../api/session_store.dart';
import '../../db/database.dart';
import '../../providers.dart';
import '../../util/audit.dart';
import '../../util/product_picker.dart' show isLowStock;
import '../live_tools.dart';
import 'call_table.dart';
import 'recommendations.dart';
import 'tool_registry.dart';

// ---------------------------------------------------------------------------------------------------------------------
// Источник данных
// ---------------------------------------------------------------------------------------------------------------------

typedef CashMove = (DateTime? at, String name, double amount, bool outflow);

/// Откуда инструменты чтения берут данные. Рабочая реализация — [RepoReadSource] (существующие репозитории мобилки),
/// в тестах — фейк. Новых запросов к серверу здесь нет: только то, что мобилка уже умеет читать.
abstract class ReadSource {
  Future<SalesSummary> sales(DateTime from, DateTime to);
  Future<List<ProductSales>> productSales(DateTime from, DateTime to);

  /// Каталог телефона (поиск по названию, штрихкоду, артикулу).
  Future<List<Product>> catalog(String query, {String? category, int limit = 20000});

  /// Товар по штрихкоду: телефон, потом сервер (как сканер). [unavailable] — сервер не ответил.
  Future<({Product? product, bool unavailable})> findByCode(String code);

  /// Закупочная цена товара (карточка товара на сервере); null — не получена.
  Future<double?> purchasePrice(String productId);
  Future<List<Debtor>> debtors();
  Future<List<ClientRow>> clients(String search);
  Future<List<ShiftRow>> shifts();
  Future<Map<String, dynamic>> shiftReport(String shiftId);
  Future<List<CashMove>> cashFlows(String shiftId);
  Future<List<SiteOrder>> orders();

  /// Страница чеков: [shiftId] есть — чеки смены, нет — продажи компании (новые первыми).
  Future<({List<Map<String, dynamic>> rows, bool more})> checks({String? shiftId, int page = 1});
  Future<Map<String, dynamic>> sale(String id);
  Future<List<DeletionRow>> deletions();
  Future<List<AuditEntry>> audit();
  Cart get cart;

  /// Моя открытая смена (id и цифры, известные телефону); null — смена не открыта.
  ShiftInfo? get shift;
}

typedef Reader = T Function<T>(ProviderListenable<T> provider);

/// Рабочий источник: те же репозитории, что у экранов («Сводка», «Склад», «Долги», «Смены», «Чеки»).
class RepoReadSource implements ReadSource {
  final AnalyticsRepository analytics;
  final OpsRepository ops;
  final AdminRepository admin;
  final SalesRepository salesRepo;
  final AppDatabase db;
  final ApiClient api;
  final Cart Function() getCart;
  final ShiftInfo? Function() getShift;

  RepoReadSource({
    required this.analytics,
    required this.ops,
    required this.admin,
    required this.salesRepo,
    required this.db,
    required this.api,
    required this.getCart,
    required this.getShift,
  });

  /// `RepoReadSource.fromReader(ref.read)` — из провайдеров приложения.
  factory RepoReadSource.fromReader(Reader read) {
    final api = read(apiProvider);
    final db = read(databaseProvider);
    return RepoReadSource(
      analytics: read(analyticsRepoProvider),
      ops: read(opsRepoProvider),
      admin: AdminRepository(api, db),
      salesRepo: read(salesRepoProvider),
      db: db,
      api: api,
      getCart: () => read(cartProvider),
      getShift: () => read(shiftProvider).shift,
    );
  }

  @override
  Future<SalesSummary> sales(DateTime from, DateTime to) => analytics.sales(from, to);
  @override
  Future<List<ProductSales>> productSales(DateTime from, DateTime to) => analytics.products(from, to);
  @override
  Future<List<Product>> catalog(String query, {String? category, int limit = 20000}) => db.searchProducts(query, category: category, limit: limit);
  @override
  Future<({Product? product, bool unavailable})> findByCode(String code) async {
    final r = await CodeLookup.find(db, api, code);
    return (product: r.product, unavailable: r.unavailable);
  }

  @override
  Future<double?> purchasePrice(String productId) async {
    try {
      final m = await admin.product(productId);
      return m['purchase_price'] == null ? null : num0(m, 'purchase_price');
    } catch (_) {
      return null;
    }
  }

  @override
  Future<List<Debtor>> debtors() => ops.debtors();
  @override
  Future<List<ClientRow>> clients(String search) => admin.clients(search);
  @override
  Future<List<ShiftRow>> shifts() => admin.shifts();
  @override
  Future<Map<String, dynamic>> shiftReport(String shiftId) => admin.shiftReport(shiftId);
  @override
  Future<List<CashMove>> cashFlows(String shiftId) => ops.shiftCashFlows(shiftId);
  @override
  Future<List<SiteOrder>> orders() => admin.orders();
  @override
  Future<({List<Map<String, dynamic>> rows, bool more})> checks({String? shiftId, int page = 1}) async {
    if (shiftId != null) {
      final (rows, more) = await ops.shiftSales(shiftId, page: page);
      return (rows: rows, more: more);
    }
    final rows = await salesRepo.sales(page: page);
    return (rows: rows, more: rows.length >= 50);
  }

  @override
  Future<Map<String, dynamic>> sale(String id) => salesRepo.sale(id);
  @override
  Future<List<DeletionRow>> deletions() => admin.deletions();
  @override
  Future<List<AuditEntry>> audit() => Audit.read();
  @override
  Cart get cart => getCart();
  @override
  ShiftInfo? get shift => getShift();
}

/// Что нужно инструментам чтения: источник данных и таблицы звонка.
class ReadContext {
  final ReadSource src;
  final TableStore tables;
  ReadContext(this.src, this.tables);
}

/// Подключить инструменты чтения к реестру звонка.
void registerReadTools(ToolRegistry registry, ReadContext c) {
  for (final t in buildReadTools(c)) {
    registry.register(t);
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Периоды, формат, мелочи
// ---------------------------------------------------------------------------------------------------------------------

DateTime _day(DateTime d) => DateTime(d.year, d.month, d.day);

/// Период отчёта: с какого дня по какой (включительно).
class Period {
  final DateTime from;
  final DateTime to;
  const Period(this.from, this.to);
  int get days => to.difference(from).inDays + 1;

  /// Прошлый такой же по длине период перед этим.
  Period get previous {
    final pTo = from.subtract(const Duration(days: 1));
    return Period(pTo.subtract(Duration(days: days - 1)), pTo);
  }

  String label(String lang) {
    String f(DateTime d) => '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}';
    return from == to ? f(from) : '${f(from)}–${f(to)}';
  }
}

DateTime? _parseDay(String s, DateTime today) {
  final t = s.trim();
  final iso = RegExp(r'^(\d{4})-(\d{1,2})-(\d{1,2})').firstMatch(t);
  if (iso != null) return DateTime(int.parse(iso.group(1)!), int.parse(iso.group(2)!), int.parse(iso.group(3)!));
  final dm = RegExp(r'^(\d{1,2})[./](\d{1,2})(?:[./](\d{2,4}))?$').firstMatch(t);
  if (dm != null) {
    var y = dm.group(3) == null ? today.year : int.parse(dm.group(3)!);
    if (y < 100) y += 2000;
    return DateTime(y, int.parse(dm.group(2)!), int.parse(dm.group(1)!));
  }
  return null;
}

Period? _keyword(String s, DateTime today) {
  switch (s.trim().toLowerCase()) {
    case 'today' || 'сегодня' || 'бүгүн' || 'bugun' || 'bugün':
      return Period(today, today);
    case 'yesterday' || 'вчера' || 'кечээ' || 'kecha' || 'dün':
      final y = today.subtract(const Duration(days: 1));
      return Period(y, y);
    case 'week' || 'неделя' || 'неделю' || 'апта' || 'hafta':
      return Period(today.subtract(const Duration(days: 6)), today);
    case 'month' || 'месяц' || 'ай' || 'oy' || 'ay':
      return Period(DateTime(today.year, today.month, 1), today);
    case 'last_week':
      return Period(today.subtract(const Duration(days: 13)), today.subtract(const Duration(days: 7)));
    case 'last_month':
      final first = DateTime(today.year, today.month - 1, 1);
      return Period(first, DateTime(today.year, today.month, 0));
  }
  return null;
}

/// Период из `from` и `to` (даты `YYYY-MM-DD` или слова today, yesterday, week, month, last_week, last_month). Нет ни того,
/// ни другого — [fallback]. Перепутанные даты меняются местами; не больше 366 дней.
Period parsePeriod(Map<String, dynamic> a, DateTime now, {String fallback = 'today'}) {
  final today = _day(now);
  final f = '${a['from'] ?? ''}'.trim();
  final t = '${a['to'] ?? ''}'.trim();
  Period? p;
  final kf = f.isEmpty ? null : _keyword(f, today);
  if (kf != null && t.isEmpty) {
    p = kf;
  } else {
    final from = f.isEmpty ? null : (kf?.from ?? _parseDay(f, today));
    final kt = t.isEmpty ? null : _keyword(t, today);
    final to = t.isEmpty ? null : (kt?.to ?? _parseDay(t, today));
    if (from != null || to != null) p = Period(from ?? to!, to ?? from!);
  }
  p ??= _keyword(fallback, today)!;
  var from = p.from;
  var to = p.to;
  if (from.isAfter(to)) (from, to) = (to, from);
  if (to.difference(from).inDays > 365) from = to.subtract(const Duration(days: 365));
  return Period(from, to);
}

String _sp(String s) => s.replaceAll(' ', ' ');
String _clip(String s, [int n = 40]) => s.length > n ? '${s.substring(0, n - 1)}…' : s;

String _money(num v, String lang) => _sp(CallTable.formatCell(v, CellKind.money, lang));
String _qty(num v, String unit, String lang) => _sp(CallTable.formatCell(v, CellKind.qty, lang, unit: unit));
String _pct(num v, String lang) => _sp(CallTable.formatCell(v, CellKind.percent, lang));
String _date(DateTime d, String lang) => CallTable.formatCell(d, CellKind.date, lang);

/// Изменение к прошлому периоду, %; null — сравнивать не с чем.
double? _change(double cur, double prev) => prev > 0.005 ? (cur - prev) / prev * 100 : null;

CallColumn _col(
  String lang,
  String key,
  CellKind kind,
  String ru,
  String ky,
  String uz,
  String en,
  String tr, {
  bool sum = false,
  String? unit,
  bool signed = true,
  bool? numeric,
}) => CallColumn(key, tr5(lang, ru, ky, uz, en, tr), kind, sum: sum, unit: unit, signed: signed, numeric: numeric);

bool _canCost(Session s) => s.canProcure || s.canEditPrices;

Map<String, dynamic> _fit(Map<String, dynamic> d, [int limit = 1400]) {
  // data ≤ 1500 символов (раздел 3.2 ТЗ): при необходимости убираем строки предпросмотра, потом сам предпросмотр.
  int size() => jsonEncode(d).length;
  final table = d['table'];
  if (table is Map<String, dynamic> && table['preview'] is List) {
    var preview = List<Object?>.of(table['preview'] as List);
    while (size() > limit && preview.isNotEmpty) {
      preview = preview.sublist(0, preview.length - 1);
      table['preview'] = preview;
    }
    if (size() > limit) table.remove('totals');
  }
  return d;
}

/// Статус ответа инструмента при сбое чтения.
Future<ToolResult> _safe(ToolEnv env, Future<ToolResult> Function() f) async {
  final lang = env.lang;
  try {
    return await f().timeout(const Duration(seconds: 25));
  } on TimeoutException {
    return ToolResult(
      ToolStatus.offline,
      tr5(
        lang,
        'Сервер отвечает слишком медленно — этих данных у меня пока нет.',
        'Сервер өтө жай жооп берүүдө — бул маалымат азырынча жок.',
        "Server juda sekin javob beryapti — bu ma'lumot hozircha yo'q.",
        'The server is too slow — I do not have this data yet.',
        'Sunucu çok yavaş — bu veri henüz yok.',
      ),
    );
  } on ApiException catch (e) {
    if (e.status == 403) return ToolResult(ToolStatus.denied, toolMsg('denied', lang));
    if (e.isTransient) {
      return ToolResult(
        ToolStatus.offline,
        tr5(
          lang,
          'Нет связи с сервером — этих данных у меня сейчас нет.',
          'Сервер менен байланыш жок — бул маалымат азыр жок.',
          "Server bilan aloqa yo'q — bu ma'lumot hozir yo'q.",
          'No connection to the server — I cannot get this data now.',
          'Sunucu ile bağlantı yok — bu veri şu an alınamıyor.',
        ),
      );
    }
    return ToolResult(ToolStatus.error, toolMsg('error', lang));
  }
}

ToolResult _show(
  ReadContext c,
  ToolEnv env,
  CallTable t,
  String say,
  List<String> highlights, {
  List<Map<String, dynamic>>? options,
  ToolStatus status = ToolStatus.ok,
}) {
  c.tables.add(t);
  final data = _fit({
    'table': t.modelBrief(),
    if (highlights.isNotEmpty) 'highlights': [for (final h in highlights.take(3)) _sp(h)],
  });
  return ToolResult(status, _sp(say), data: data, tableId: t.id, options: options, screen: env.screen.isEmpty ? null : env.screen);
}

ToolResult _none(ToolEnv env, String ru, String ky, String uz, String en, String tr, {ToolStatus status = ToolStatus.notFound}) =>
    ToolResult(status, tr5(env.lang, ru, ky, uz, en, tr), screen: env.screen.isEmpty ? null : env.screen);

ToolResult _denied(ToolEnv env, String ru, String ky, String uz, String en, String tr) =>
    ToolResult(ToolStatus.denied, tr5(env.lang, ru, ky, uz, en, tr));

/// Коды в таблице: [Д1] → имя для экрана. Нумерация внутри одной таблицы.
class _Codes {
  final String letter;
  final Map<String, String> names = {};
  final Map<String, String> _byKey = {};
  _Codes(this.letter);

  String code(String key, String display) {
    final have = _byKey[key];
    if (have != null) return have;
    final code = '[$letter${_byKey.length + 1}]';
    _byKey[key] = code;
    names[code] = display.trim().isEmpty ? code : display.trim();
    return code;
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Описания для модели. По-английски, коротко: все объявления звонка ≤ 8 тыс. символов (раздел 3.4 ТЗ).
// ---------------------------------------------------------------------------------------------------------------------

const _dateDesc = 'YYYY-MM-DD, or today|yesterday|week|month|last_month';
Map<String, dynamic> _from() => sStr(desc: _dateDesc);
Map<String, dynamic> _to() => sStr(desc: 'YYYY-MM-DD (end, optional)');

/// Все инструменты чтения и управления таблицей.
List<AssistantTool> buildReadTools(ReadContext c) {
  bool analytics(Session s) => s.can(Session.viewAnalytics);
  bool products(Session s) => s.can(Session.viewProducts);
  bool clients(Session s) => s.can(Session.viewClients);
  return [
    _salesReport(c, analytics),
    _topProducts(c, analytics),
    _abcReport(c, analytics),
    _stockList(c, products),
    _productInfo(c),
    _checksList(c),
    _checkDetails(c),
    _debtors(c, clients),
    _clientInfo(c, clients),
    _shiftNow(c),
    _shiftsList(c),
    _cashMoves(c),
    _ordersList(c),
    _cartShow(c),
    _deletionsLog(c, analytics),
    _auditLog(c, analytics),
    _recommend(c, analytics),
    _tableControl(c),
    _showTable(c),
  ];
}

// ---------------------------------------------------------------------------------------------------------------------
// Продажи, топы, ABC
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _salesReport(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'sales_report',
    'Sales/revenue for a period, shown as a table. group_by: total (default), day, payment. ru: выручка, продажи; ky: түшүм; uz: tushum; tr: ciro.',
    parameters: sObj({
      'from': _from(),
      'to': _to(),
      'group_by': sStr(values: ['total', 'day', 'payment']),
    }),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Отчёты',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final p = parsePeriod(r.args, env.now());
    final group = '${r.args['group_by'] ?? 'total'}';
    // Прошлый такой же период нужен только для сравнения «к прошлому»; не пришёл — отчёт всё равно есть.
    final both = await Future.wait<Object?>([
      c.src.sales(p.from, p.to),
      if (group == 'total') c.src.sales(p.previous.from, p.previous.to).then<Object?>((v) => v, onError: (_) => null),
    ]);
    final cur = both[0] as SalesSummary;
    final prev = both.length > 1 ? both[1] as SalesSummary? : null;
    final id = c.tables.nextId();
    final sub = p.label(lang);
    final src = 'tab=sales';
    if (group == 'day') {
      final rows = [for (final d in cur.byDay) <Object?>[d.$1, d.$2]];
      final t = CallTable.build(
        id: id,
        title: tr5(lang, 'Продажи по дням', 'Күндөр боюнча сатуу', 'Kunlar bo\'yicha savdo', 'Sales by day', 'Günlere göre satış'),
        subtitle: sub,
        columns: [
          _col(lang, 'date', CellKind.date, 'День', 'Күн', 'Kun', 'Day', 'Gün'),
          _col(lang, 'sum', CellKind.money, 'Выручка', 'Түшүм', 'Tushum', 'Revenue', 'Ciro'),
        ],
        rows: rows,
        totals: {'sum': cur.revenue},
        source: src,
        at: env.now(),
        lang: lang,
        objectRoute: 'summary',
      );
      if (rows.isEmpty) {
        return _show(c, env, t, tr5(lang, 'За $sub продаж нет.', '$sub аралыгында сатуу жок.', '$sub oralig\'ida savdo yo\'q.', 'No sales for $sub.', '$sub için satış yok.'), []);
      }
      final best = cur.byDay.reduce((a, b) => b.$2 > a.$2 ? b : a);
      final say = tr5(
        lang,
        'За $sub выручка ${_money(cur.revenue, lang)}; лучший день — ${_date(best.$1, lang)}, ${_money(best.$2, lang)}.',
        '$sub аралыгында түшүм ${_money(cur.revenue, lang)}; эң жакшы күн — ${_date(best.$1, lang)}, ${_money(best.$2, lang)}.',
        '$sub uchun tushum ${_money(cur.revenue, lang)}; eng yaxshi kun — ${_date(best.$1, lang)}, ${_money(best.$2, lang)}.',
        'Revenue for $sub is ${_money(cur.revenue, lang)}; best day — ${_date(best.$1, lang)}, ${_money(best.$2, lang)}.',
        '$sub için ciro ${_money(cur.revenue, lang)}; en iyi gün — ${_date(best.$1, lang)}, ${_money(best.$2, lang)}.',
      );
      return _show(c, env, t, say, [
        'revenue ${_money(cur.revenue, lang)}',
        'best day ${_date(best.$1, lang)} ${_money(best.$2, lang)}',
        'checks ${cur.checks}',
      ]);
    }
    if (group == 'payment') {
      final total = cur.payments.values.fold(0.0, (a, b) => a + b);
      final entries = cur.payments.entries.toList()..sort((a, b) => b.value.compareTo(a.value));
      final t = CallTable.build(
        id: id,
        title: tr5(lang, 'Продажи по способам оплаты', 'Төлөм ыкмалары боюнча', 'To\'lov usullari bo\'yicha', 'Sales by payment method', 'Ödeme yöntemine göre'),
        subtitle: sub,
        columns: [
          _col(lang, 'pay', CellKind.status, 'Способ', 'Ыкма', 'Usul', 'Method', 'Yöntem'),
          _col(lang, 'sum', CellKind.money, 'Сумма', 'Сумма', 'Summa', 'Amount', 'Tutar', sum: true),
          _col(lang, 'share', CellKind.percent, 'Доля', 'Үлүш', 'Ulush', 'Share', 'Pay', signed: false),
        ],
        rows: [
          for (final e in entries) <Object?>[e.key.isEmpty ? '—' : e.key, e.value, total > 0 ? e.value / total * 100 : 0.0],
        ],
        source: src,
        at: env.now(),
        lang: lang,
      );
      if (entries.isEmpty) {
        return _show(c, env, t, tr5(lang, 'За $sub продаж нет.', '$sub аралыгында сатуу жок.', '$sub oralig\'ida savdo yo\'q.', 'No sales for $sub.', '$sub için satış yok.'), []);
      }
      final top = entries.first;
      final w = CallTable.statusWord(top.key, lang);
      final share = total > 0 ? top.value / total * 100 : 0.0;
      final say = tr5(
        lang,
        'Больше всего — $w: ${_money(top.value, lang)} (${_pct(share, lang).replaceFirst('+', '')}).',
        'Эң көбү — $w: ${_money(top.value, lang)} (${_pct(share, lang).replaceFirst('+', '')}).',
        'Eng ko\'pi — $w: ${_money(top.value, lang)} (${_pct(share, lang).replaceFirst('+', '')}).',
        'Most — $w: ${_money(top.value, lang)} (${_pct(share, lang).replaceFirst('+', '')}).',
        'En çok — $w: ${_money(top.value, lang)} (${_pct(share, lang).replaceFirst('+', '')}).',
      );
      return _show(c, env, t, say, ['top $w ${_money(top.value, lang)}', 'total ${_money(total, lang)}']);
    }
    // total: карточка «поле — значение»
    final change = prev == null ? null : _change(cur.revenue, prev.revenue);
    final fieldCol = _col(lang, 'field', CellKind.text, 'Показатель', 'Көрсөткүч', 'Ko\'rsatkich', 'Metric', 'Gösterge');
    final valueCol = _col(lang, 'value', CellKind.text, 'Значение', 'Мааниси', 'Qiymat', 'Value', 'Değer', numeric: false);
    final t = CallTable.build(
      id: id,
      title: tr5(lang, 'Продажи', 'Сатуулар', 'Savdo', 'Sales', 'Satışlar'),
      subtitle: sub,
      card: true,
      columns: [fieldCol, valueCol],
      rows: [
        [tr5(lang, 'Выручка', 'Түшүм', 'Tushum', 'Revenue', 'Ciro'), Cell(cur.revenue, CellKind.money)],
        [tr5(lang, 'Чеков', 'Чектер', 'Cheklar', 'Checks', 'Fiş sayısı'), Cell(cur.checks, CellKind.qty)],
        [tr5(lang, 'Средний чек', 'Орточо чек', 'O\'rtacha chek', 'Average check', 'Ortalama fiş'), Cell(cur.avg, CellKind.money)],
        [tr5(lang, 'Валовая прибыль', 'Дүң пайда', 'Yalpi foyda', 'Gross profit', 'Brüt kâr'), Cell(cur.profit, CellKind.money)],
        if (change != null) [tr5(lang, 'К прошлому периоду', 'Мурунку мезгилге', 'Oldingi davrga', 'vs previous period', 'Önceki döneme göre'), Cell(change, CellKind.percent)],
      ],
      source: src,
      at: env.now(),
      lang: lang,
      objectRoute: 'summary',
    );
    final cmp = change == null
        ? ''
        : tr5(lang, ' (${_pct(change, lang)} к прошлому периоду)', ' (мурунку мезгилге ${_pct(change, lang)})', ' (oldingi davrga ${_pct(change, lang)})', ' (${_pct(change, lang)} vs previous period)', ' (önceki döneme göre ${_pct(change, lang)})');
    final say = tr5(
      lang,
      'За $sub выручка ${_money(cur.revenue, lang)}, чеков ${cur.checks}, средний чек ${_money(cur.avg, lang)}$cmp.',
      '$sub аралыгында түшүм ${_money(cur.revenue, lang)}, чек ${cur.checks}, орточо чек ${_money(cur.avg, lang)}$cmp.',
      '$sub uchun tushum ${_money(cur.revenue, lang)}, cheklar ${cur.checks}, o\'rtacha chek ${_money(cur.avg, lang)}$cmp.',
      'For $sub revenue ${_money(cur.revenue, lang)}, ${cur.checks} checks, average check ${_money(cur.avg, lang)}$cmp.',
      '$sub için ciro ${_money(cur.revenue, lang)}, ${cur.checks} fiş, ortalama fiş ${_money(cur.avg, lang)}$cmp.',
    );
    return _show(c, env, t, say, [
      'revenue ${_money(cur.revenue, lang)}',
      'checks ${cur.checks}',
      if (change != null) 'vs previous ${_pct(change, lang)}' else 'profit ${_money(cur.profit, lang)}',
    ]);
  }),
);

AssistantTool _topProducts(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'top_products',
    'Best or worst selling products for a period as a table. metric: revenue (default), qty, profit; order: desc (default) or asc; limit up to 50. ru: топ товаров, что лучше продаётся.',
    parameters: sObj({
      'from': _from(),
      'to': _to(),
      'metric': sStr(values: ['revenue', 'qty', 'profit']),
      'order': sStr(values: ['desc', 'asc']),
      'limit': sInt(),
    }),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Отчёты',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final p = parsePeriod(r.args, env.now(), fallback: 'week');
    final metric = const {'revenue', 'qty', 'profit'}.contains(r.args['metric']) ? '${r.args['metric']}' : 'revenue';
    final asc = r.args['order'] == 'asc';
    final limit = ((r.args['limit'] is num ? (r.args['limit'] as num).toInt() : 10)).clamp(1, 50);
    final sold = await c.src.productSales(p.from, p.to);
    double val(ProductSales s) => switch (metric) {
      'qty' => s.qty,
      'profit' => s.profit,
      _ => s.revenue,
    };
    final list = sold.where((s) => s.qty > 0 || s.revenue > 0).toList()..sort((a, b) => asc ? val(a).compareTo(val(b)) : val(b).compareTo(val(a)));
    final title = asc
        ? tr5(lang, 'Хуже всего продаётся', 'Эң начар сатылат', 'Eng yomon sotiladi', 'Worst sellers', 'En az satanlar')
        : tr5(lang, 'Лучшие товары', 'Эң мыкты товарлар', 'Eng yaxshi mahsulotlar', 'Top products', 'En iyi ürünler');
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: title,
      subtitle: p.label(lang),
      columns: [
        _col(lang, 'name', CellKind.text, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'),
        _col(lang, 'qty', CellKind.qty, 'Продано', 'Сатылды', 'Sotildi', 'Sold', 'Satılan', sum: true),
        _col(lang, 'revenue', CellKind.money, 'Выручка', 'Түшүм', 'Tushum', 'Revenue', 'Ciro', sum: true),
        _col(lang, 'profit', CellKind.money, 'Прибыль', 'Пайда', 'Foyda', 'Profit', 'Kâr', sum: true),
      ],
      rows: [
        for (final s in list) <Object?>[s.name, s.qty, s.revenue, s.profit],
      ],
      sortKey: metric,
      desc: !asc,
      source: 'tab=products',
      at: env.now(),
      lang: lang,
      shown: limit,
    );
    if (list.isEmpty) {
      return _show(c, env, t, tr5(lang, 'За ${p.label(lang)} продаж по товарам нет.', '${p.label(lang)} аралыгында товар сатуулары жок.', '${p.label(lang)} uchun mahsulot savdosi yo\'q.', 'No product sales for ${p.label(lang)}.', '${p.label(lang)} için ürün satışı yok.'), []);
    }
    final first = list.first;
    final firstVal = metric == 'qty' ? _qty(first.qty, '', lang) : _money(val(first), lang);
    final name = _clip(first.name);
    final say = asc
        ? tr5(lang, 'Хуже всего — $name: $firstVal.', 'Эң начар — $name: $firstVal.', 'Eng yomoni — $name: $firstVal.', 'Worst — $name: $firstVal.', 'En kötüsü — $name: $firstVal.')
        : tr5(lang, 'Лучший — $name: $firstVal.', 'Эң мыктысы — $name: $firstVal.', 'Eng yaxshisi — $name: $firstVal.', 'Best — $name: $firstVal.', 'En iyisi — $name: $firstVal.');
    final total = list.fold(0.0, (a, s) => a + val(s));
    final topN = list.take(limit).fold(0.0, (a, s) => a + val(s));
    return _show(c, env, t, say, [
      '$name $firstVal',
      if (total > 0) 'top$limit share ${_pct(topN / total * 100, lang).replaceFirst('+', '')}',
    ]);
  }),
);

AssistantTool _abcReport(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'abc_report',
    'ABC analysis of products for a period (A = 80% of the metric, B = next 15%, C = rest) as a table. metric: revenue (default), profit, qty.',
    parameters: sObj({
      'from': _from(),
      'to': _to(),
      'metric': sStr(values: ['revenue', 'profit', 'qty']),
    }),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Отчёты',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final p = parsePeriod(r.args, env.now(), fallback: 'month');
    final metric = const {'revenue', 'profit', 'qty'}.contains(r.args['metric']) ? '${r.args['metric']}' : 'revenue';
    final sold = await c.src.productSales(p.from, p.to);
    // Как AbcScreen: A — пока накоплено до 80 %, B — до 95 %, остальное C (товар, перешедший порог, остаётся в своей группе).
    double val(ProductSales s) => switch (metric) {
      'profit' => math.max(0, s.profit),
      'qty' => s.qty,
      _ => s.revenue,
    };
    final list = sold.where((s) => val(s) > 0).toList()..sort((a, b) => val(b).compareTo(val(a)));
    final total = list.fold(0.0, (a, s) => a + val(s));
    final rows = <List<Object?>>[];
    final counts = {'A': 0, 'B': 0, 'C': 0};
    var acc = 0.0;
    for (final s in list) {
      final before = total <= 0 ? 0.0 : acc / total;
      acc += val(s);
      final g = before < .8 ? 'A' : (before < .95 ? 'B' : 'C');
      counts[g] = counts[g]! + 1;
      rows.add([s.name, val(s), total <= 0 ? 0.0 : val(s) / total * 100, total <= 0 ? 0.0 : acc / total * 100, g]);
    }
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'ABC-анализ', 'ABC-талдоо', 'ABC-tahlil', 'ABC analysis', 'ABC analizi'),
      subtitle: p.label(lang),
      columns: [
        _col(lang, 'name', CellKind.text, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'),
        metric == 'qty'
            ? _col(lang, 'value', CellKind.qty, 'Продано', 'Сатылды', 'Sotildi', 'Sold', 'Satılan', sum: true)
            : _col(lang, 'value', CellKind.money, metric == 'profit' ? 'Прибыль' : 'Выручка', metric == 'profit' ? 'Пайда' : 'Түшүм', metric == 'profit' ? 'Foyda' : 'Tushum', metric == 'profit' ? 'Profit' : 'Revenue', metric == 'profit' ? 'Kâr' : 'Ciro', sum: true),
        _col(lang, 'share', CellKind.percent, 'Доля', 'Үлүш', 'Ulush', 'Share', 'Pay', signed: false),
        _col(lang, 'cum', CellKind.percent, 'Накоплено', 'Топтолду', 'Jamlangan', 'Cumulative', 'Birikimli', signed: false),
        _col(lang, 'group', CellKind.text, 'Группа', 'Топ', 'Guruh', 'Group', 'Grup'),
      ],
      rows: rows,
      source: 'tab=products',
      at: env.now(),
      lang: lang,
      objectRoute: 'abc',
    );
    if (rows.isEmpty) {
      return _show(c, env, t, tr5(lang, 'За ${p.label(lang)} продаж по товарам нет.', '${p.label(lang)} аралыгында товар сатуулары жок.', '${p.label(lang)} uchun mahsulot savdosi yo\'q.', 'No product sales for ${p.label(lang)}.', '${p.label(lang)} için ürün satışı yok.'), []);
    }
    final a = counts['A']!;
    final say = tr5(
      lang,
      'Группа A — $a товаров дают 80 % результата; B — ${counts['B']}, C — ${counts['C']}.',
      'A тобу — $a товар 80 % натыйжаны берет; B — ${counts['B']}, C — ${counts['C']}.',
      'A guruhi — $a ta mahsulot natijaning 80 % ini beradi; B — ${counts['B']}, C — ${counts['C']}.',
      'Group A — $a products give 80% of the result; B — ${counts['B']}, C — ${counts['C']}.',
      'A grubu — $a ürün sonucun %80 ini veriyor; B — ${counts['B']}, C — ${counts['C']}.',
    );
    return _show(c, env, t, say, ['A: $a', 'B: ${counts['B']}', 'C: ${counts['C']}']);
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Склад и товар
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _stockList(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'stock_list',
    'List of products by stock level as a table. filter: low (running out), out (zero), in (in stock), all. Optional query text and category. ru: остатки, что заканчивается; ky: калдык; uz: qoldiq; tr: stok.',
    parameters: sObj({
      'filter': sStr(values: ['low', 'out', 'in', 'all']),
      'query': sStr(),
      'category': sStr(),
      'limit': sInt(),
    }),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Склад',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final filter = const {'low', 'out', 'in', 'all'}.contains(r.args['filter']) ? '${r.args['filter']}' : 'all';
    final query = '${r.args['query'] ?? ''}'.trim();
    final category = '${r.args['category'] ?? ''}'.trim();
    final limit = (r.args['limit'] is num ? (r.args['limit'] as num).toInt() : CallTable.pageSize).clamp(1, 500);
    final all = await c.src.catalog(query, category: category.isEmpty ? null : category);
    bool match(Product p) => switch (filter) {
      'low' => p.quantity > 0 && isLowStock(p.quantity, p.minQuantity),
      'out' => p.quantity <= 0,
      'in' => p.quantity > 0,
      _ => true,
    };
    final list = all.where((p) => !p.isService && match(p)).toList();
    if (filter == 'low') list.sort((a, b) => a.quantity.compareTo(b.quantity));
    final title = switch (filter) {
      'low' => tr5(lang, 'Мало осталось', 'Аз калды', 'Kam qoldi', 'Running low', 'Azalanlar'),
      'out' => tr5(lang, 'Закончились', 'Бүттү', 'Tugadi', 'Out of stock', 'Bitenler'),
      'in' => tr5(lang, 'В наличии', 'Бар', 'Mavjud', 'In stock', 'Stokta'),
      _ => tr5(lang, 'Склад', 'Склад', 'Ombor', 'Stock', 'Depo'),
    };
    final subtitle = [if (query.isNotEmpty) '«${_clip(query, 24)}»', if (category.isNotEmpty) _clip(category, 24)].join(' · ');
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: title,
      subtitle: subtitle.isEmpty ? null : subtitle,
      columns: [
        _col(lang, 'name', CellKind.text, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'),
        _col(lang, 'qty', CellKind.qty, 'Остаток', 'Калдык', 'Qoldiq', 'Stock', 'Stok'),
        _col(lang, 'price', CellKind.money, 'Цена', 'Баасы', 'Narxi', 'Price', 'Fiyat'),
        _col(lang, 'category', CellKind.text, 'Категория', 'Категория', 'Kategoriya', 'Category', 'Kategori'),
      ],
      rows: [
        for (final p in list) <Object?>[p.name, Cell(p.quantity, CellKind.qty, unit: p.unit), p.price, p.category.isEmpty ? null : p.category],
      ],
      refs: [for (final p in list) p.id],
      sortKey: filter == 'low' ? 'qty' : null,
      source: 'phone',
      at: env.now(),
      lang: lang,
      objectRoute: 'product',
      shown: limit,
    );
    if (list.isEmpty) {
      return _show(
        c,
        env,
        t,
        switch (filter) {
          'low' => tr5(lang, 'Ничего не заканчивается.', 'Эч нерсе бүтүп жаткан жок.', 'Hech narsa tugamayapti.', 'Nothing is running low.', 'Azalan ürün yok.'),
          'out' => tr5(lang, 'Закончившихся товаров нет.', 'Бүткөн товар жок.', 'Tugagan mahsulot yo\'q.', 'Nothing is out of stock.', 'Biten ürün yok.'),
          _ => tr5(lang, 'Ничего не нашла.', 'Эч нерсе табылган жок.', 'Hech narsa topilmadi.', 'Nothing found.', 'Hiçbir şey bulunamadı.'),
        },
        [],
      );
    }
    final f = list.first;
    final fn = _clip(f.name);
    final say = switch (filter) {
      'low' => tr5(
        lang,
        'Мало осталось ${list.length} товаров; меньше всего — $fn: ${_qty(f.quantity, f.unit, lang)}.',
        'Аз калган ${list.length} товар; эң азы — $fn: ${_qty(f.quantity, f.unit, lang)}.',
        'Kam qolgan ${list.length} ta mahsulot; eng kami — $fn: ${_qty(f.quantity, f.unit, lang)}.',
        '${list.length} products are running low; lowest — $fn: ${_qty(f.quantity, f.unit, lang)}.',
        '${list.length} ürün azaldı; en az — $fn: ${_qty(f.quantity, f.unit, lang)}.',
      ),
      'out' => tr5(lang, 'Закончилось ${list.length} товаров.', 'Бүттү: ${list.length} товар.', 'Tugadi: ${list.length} ta mahsulot.', '${list.length} products are out of stock.', '${list.length} ürün bitti.'),
      _ => tr5(lang, 'Нашла ${list.length} товаров.', '${list.length} товар табылды.', '${list.length} ta mahsulot topildi.', 'Found ${list.length} products.', '${list.length} ürün bulundu.'),
    };
    return _show(c, env, t, say, ['count ${list.length}', if (filter == 'low') '$fn ${_qty(f.quantity, f.unit, lang)}']);
  }),
);

AssistantTool _productInfo(ReadContext c) => AssistantTool(
  decl: ToolDecl(
    'product_info',
    'Price (and stock / purchase price if the user is allowed) of one product, by name or barcode. Several matches -> ambiguous: ask which, then call again with pick = its number. ru: сколько стоит, есть ли; ky: баасы канча; uz: narxi qancha; tr: fiyatı ne kadar.',
    parameters: sObj({
      'query': sStr(),
      'barcode': sStr(),
      'pick': sInt(desc: '1-based option number after ambiguous'),
    }),
  ),
  group: ToolGroup.read,
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final s = env.session;
    final query = '${r.args['query'] ?? ''}'.trim();
    final barcode = '${r.args['barcode'] ?? ''}'.trim();
    if (query.isEmpty && barcode.isEmpty) {
      return ToolResult(ToolStatus.clarify, tr5(lang, 'Какой товар?', 'Кайсы товар?', 'Qaysi mahsulot?', 'Which product?', 'Hangi ürün?'));
    }
    Product? product;
    if (barcode.isNotEmpty) {
      final f = await c.src.findByCode(barcode);
      if (f.unavailable && f.product == null) {
        return ToolResult(ToolStatus.offline, tr5(lang, 'Сервер не ответил — по штрихкоду не нашла.', 'Сервер жооп берген жок — штрихкод боюнча табылган жок.', 'Server javob bermadi — shtrixkod bo\'yicha topilmadi.', 'The server did not answer — barcode not found.', 'Sunucu yanıt vermedi — barkod bulunamadı.'));
      }
      product = f.product;
    } else {
      final cands = await c.src.catalog(query, limit: 30);
      final exact = cands.where((p) => p.name.trim().toLowerCase() == query.toLowerCase()).toList();
      final pick = r.args['pick'] is num ? (r.args['pick'] as num).toInt() : null;
      if (pick != null && pick >= 1 && pick <= cands.length) {
        product = cands[pick - 1];
      } else if (cands.length == 1) {
        product = cands.first;
      } else if (exact.length == 1) {
        product = exact.first;
      } else if (cands.length > 1) {
        final showStock = s.can(Session.viewProducts);
        final t = CallTable.build(
          id: c.tables.nextId(),
          title: tr5(lang, 'Какой товар?', 'Кайсы товар?', 'Qaysi mahsulot?', 'Which product?', 'Hangi ürün?'),
          subtitle: '«${_clip(query, 24)}»',
          columns: [
            _col(lang, 'n', CellKind.text, '№', '№', '№', '#', 'No', numeric: false),
            _col(lang, 'name', CellKind.text, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'),
            _col(lang, 'price', CellKind.money, 'Цена', 'Баасы', 'Narxi', 'Price', 'Fiyat'),
            if (showStock) _col(lang, 'qty', CellKind.qty, 'Остаток', 'Калдык', 'Qoldiq', 'Stock', 'Stok'),
          ],
          rows: [
            for (var i = 0; i < cands.length; i++)
              <Object?>[
                '${i + 1}',
                cands[i].name,
                cands[i].price,
                if (showStock) Cell(cands[i].quantity, CellKind.qty, unit: cands[i].unit),
              ],
          ],
          refs: [for (final p in cands) p.id],
          source: 'phone',
          at: env.now(),
          lang: lang,
          objectRoute: 'product',
        );
        final top = cands.take(5).toList();
        final listed = [for (var i = 0; i < top.length; i++) '${i + 1} — ${_clip(top[i].name, 30)}, ${_money(top[i].price, lang)}'].join('; ');
        final say = tr5(
          lang,
          'Нашла несколько: $listed. Какой — скажите номер.',
          'Бир нече табылды: $listed. Кайсы — номерин айтыңыз.',
          'Bir nechta topildi: $listed. Qaysi — raqamini ayting.',
          'Several found: $listed. Which one — say the number.',
          'Birkaç tane buldum: $listed. Hangisi — numarasını söyleyin.',
        );
        return _show(
          c,
          env,
          t,
          say,
          [],
          status: ToolStatus.ambiguous,
          options: [
            for (var i = 0; i < top.length; i++) {'n': i + 1, 'title': _clip(top[i].name, 40), 'price': top[i].price},
          ],
        );
      }
    }
    if (product == null) {
      return _none(env, 'Такого товара не нашла.', 'Андай товар табылган жок.', 'Bunday mahsulot topilmadi.', 'No such product found.', 'Böyle bir ürün bulunamadı.');
    }
    final p = product;
    final seeStock = s.can(Session.viewProducts);
    final seeCost = _canCost(s);
    final purchase = seeCost ? await c.src.purchasePrice(p.id) : null;
    final markup = purchase != null && purchase > 0 ? (p.price - purchase) / purchase * 100 : null;
    String label(String ru, String ky, String uz, String en, String tr) => tr5(lang, ru, ky, uz, en, tr);
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: _clip(p.name, 60),
      card: true,
      columns: [
        _col(lang, 'field', CellKind.text, 'Поле', 'Талаа', 'Maydon', 'Field', 'Alan'),
        _col(lang, 'value', CellKind.text, 'Значение', 'Мааниси', 'Qiymat', 'Value', 'Değer', numeric: false),
      ],
      rows: [
        [label('Цена', 'Баасы', 'Narxi', 'Price', 'Fiyat'), Cell(p.price, CellKind.money)],
        if (p.wholesalePrice > 0) [label('Опт', 'Дүң', 'Ulgurji', 'Wholesale', 'Toptan'), Cell(p.wholesalePrice, CellKind.money)],
        if (seeStock && !p.isService) [label('Остаток', 'Калдык', 'Qoldiq', 'Stock', 'Stok'), Cell(p.quantity, CellKind.qty, unit: p.unit)],
        if (seeStock && p.minQuantity > 0) [label('Мин. остаток', 'Мин. калдык', 'Min. qoldiq', 'Min. stock', 'Min. stok'), Cell(p.minQuantity, CellKind.qty, unit: p.unit)],
        if (purchase != null) [label('Закупка', 'Закупка', 'Xarid narxi', 'Purchase', 'Alış'), Cell(purchase, CellKind.money)],
        if (markup != null) [label('Наценка', 'Үстөк', 'Ustama', 'Markup', 'Kâr oranı'), Cell(markup, CellKind.percent)],
        if ((p.barcode ?? '').isNotEmpty) [label('Штрихкод', 'Штрихкод', 'Shtrixkod', 'Barcode', 'Barkod'), Cell(p.barcode, CellKind.text)],
        if (p.category.isNotEmpty) [label('Категория', 'Категория', 'Kategoriya', 'Category', 'Kategori'), Cell(p.category, CellKind.text)],
      ],
      source: 'phone',
      at: env.now(),
      lang: lang,
      objectRoute: 'product',
    );
    final n = _clip(p.name);
    final stockPart = seeStock && !p.isService
        ? tr5(lang, ', остаток ${_qty(p.quantity, p.unit, lang)}', ', калдык ${_qty(p.quantity, p.unit, lang)}', ', qoldiq ${_qty(p.quantity, p.unit, lang)}', ', in stock ${_qty(p.quantity, p.unit, lang)}', ', stok ${_qty(p.quantity, p.unit, lang)}')
        : '';
    final say = tr5(
      lang,
      '$n — ${_money(p.price, lang)}$stockPart.',
      '$n — ${_money(p.price, lang)}$stockPart.',
      '$n — ${_money(p.price, lang)}$stockPart.',
      '$n — ${_money(p.price, lang)}$stockPart.',
      '$n — ${_money(p.price, lang)}$stockPart.',
    );
    return _show(c, env, t, say, ['price ${_money(p.price, lang)}', if (seeStock && !p.isService) 'stock ${_qty(p.quantity, p.unit, lang)}']);
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Чеки
// ---------------------------------------------------------------------------------------------------------------------

bool _voided(String status) {
  final s = status.toLowerCase();
  return s.contains('cancel') || s.contains('return') || s.contains('refund');
}

/// Чеки: с правом «Отчёты» — продажи компании за период, без него — только чеки своей смены (как экран «Чеки»).
Future<({List<Map<String, dynamic>> rows, bool wholeCompany})> _loadChecks(ReadContext c, ToolEnv env, Period? p) async {
  final whole = env.session.can(Session.viewAnalytics);
  final shiftId = c.src.shift?.id;
  if (!whole) {
    if (shiftId == null) return (rows: <Map<String, dynamic>>[], wholeCompany: false);
    final out = <Map<String, dynamic>>[];
    for (var page = 1; page <= 5; page++) {
      final res = await c.src.checks(shiftId: shiftId, page: page);
      out.addAll(res.rows);
      if (!res.more || res.rows.isEmpty) break;
    }
    return (rows: out, wholeCompany: false);
  }
  final out = <Map<String, dynamic>>[];
  for (var page = 1; page <= 5; page++) {
    final res = await c.src.checks(page: page);
    out.addAll(res.rows);
    if (res.rows.isEmpty || !res.more) break;
    // Продажи идут от новых к старым: дошли до дня раньше начала периода — дальше не нужно.
    final oldest = res.rows.map((m) => DateTime.tryParse(str(m, 'created_at') ?? '')?.toLocal()).whereType<DateTime>().fold<DateTime?>(null, (a, d) => a == null || d.isBefore(a) ? d : a);
    if (p != null && oldest != null && oldest.isBefore(p.from)) break;
  }
  return (rows: out, wholeCompany: true);
}

AssistantTool _checksList(ReadContext c) => AssistantTool(
  decl: ToolDecl(
    'checks_list',
    'List of sale receipts (checks) as a table, newest first. Optional period, payment (cash|card|debt|transfer|mixed), min_total, limit. Without the Reports right only the current shift. ru: чеки, продажи за смену.',
    parameters: sObj({
      'from': _from(),
      'to': _to(),
      'payment': sStr(),
      'min_total': sNum(),
      'limit': sInt(),
    }),
  ),
  group: ToolGroup.read,
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final hasPeriod = '${r.args['from'] ?? ''}'.isNotEmpty || '${r.args['to'] ?? ''}'.isNotEmpty;
    final p = hasPeriod ? parsePeriod(r.args, env.now()) : null;
    final load = await _loadChecks(c, env, p);
    final payment = '${r.args['payment'] ?? ''}'.trim().toLowerCase();
    final minTotal = r.args['min_total'] is num ? (r.args['min_total'] as num).toDouble() : null;
    final limit = (r.args['limit'] is num ? (r.args['limit'] as num).toInt() : CallTable.pageSize).clamp(1, 500);
    final rows = <Map<String, dynamic>>[];
    for (final m in load.rows) {
      final at = DateTime.tryParse(str(m, 'created_at') ?? '')?.toLocal();
      if (p != null && at != null && (_day(at).isBefore(p.from) || _day(at).isAfter(p.to))) continue;
      if (payment.isNotEmpty && !(str(m, 'payment_method') ?? '').toLowerCase().contains(payment)) continue;
      if (minTotal != null && num0(m, 'total') < minTotal) continue;
      rows.add(m);
    }
    final live = rows.where((m) => !_voided(str(m, 'status') ?? '')).toList();
    final sum = live.fold(0.0, (a, m) => a + num0(m, 'total'));
    final scope = load.wholeCompany ? (p?.label(lang) ?? '') : tr5(lang, 'эта смена', 'ушул смена', 'shu smena', 'this shift', 'bu vardiya');
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Чеки', 'Чектер', 'Cheklar', 'Checks', 'Fişler'),
      subtitle: scope.isEmpty ? null : scope,
      columns: [
        _col(lang, 'number', CellKind.text, '№', '№', '№', '#', 'No', numeric: false),
        _col(lang, 'date', CellKind.date, 'Дата', 'Күнү', 'Sana', 'Date', 'Tarih'),
        _col(lang, 'time', CellKind.time, 'Время', 'Убакыт', 'Vaqt', 'Time', 'Saat'),
        _col(lang, 'pay', CellKind.status, 'Оплата', 'Төлөм', 'To\'lov', 'Payment', 'Ödeme'),
        _col(lang, 'sum', CellKind.money, 'Сумма', 'Сумма', 'Summa', 'Amount', 'Tutar', sum: true),
        _col(lang, 'status', CellKind.status, 'Статус', 'Статус', 'Holat', 'Status', 'Durum'),
      ],
      rows: [
        for (final m in rows)
          <Object?>[
            str(m, 'number') ?? str(m, 'receipt_number') ?? '',
            DateTime.tryParse(str(m, 'created_at') ?? '')?.toLocal(),
            DateTime.tryParse(str(m, 'created_at') ?? '')?.toLocal(),
            str(m, 'payment_method') ?? '',
            num0(m, 'total'),
            _voided(str(m, 'status') ?? '') ? str(m, 'status') : null,
          ],
      ],
      refs: [for (final m in rows) str(m, 'id')],
      totals: {'sum': sum},
      source: 'phone',
      at: env.now(),
      lang: lang,
      objectRoute: 'check',
      shown: limit,
      note: load.wholeCompany || rows.isNotEmpty
          ? null
          : tr5(lang, 'Нет права «Отчёты»: видны только чеки текущей смены.', '«Отчёттор» укугу жок: ушул смена гана көрүнөт.', '«Hisobotlar» huquqi yo\'q: faqat joriy smena.', 'No Reports right: current shift only.', '«Raporlar» yetkisi yok: yalnızca geçerli vardiya.'),
    );
    if (rows.isEmpty) {
      return _show(c, env, t, tr5(lang, 'Чеков не нашла.', 'Чек табылган жок.', 'Chek topilmadi.', 'No checks found.', 'Fiş bulunamadı.'), []);
    }
    final say = tr5(
      lang,
      'Нашла ${rows.length} чеков на ${_money(sum, lang)}.',
      '${rows.length} чек табылды, жалпы ${_money(sum, lang)}.',
      '${rows.length} ta chek topildi, jami ${_money(sum, lang)}.',
      'Found ${rows.length} checks totalling ${_money(sum, lang)}.',
      '${rows.length} fiş bulundu, toplam ${_money(sum, lang)}.',
    );
    return _show(c, env, t, say, ['checks ${rows.length}', 'sum ${_money(sum, lang)}']);
  }),
);

AssistantTool _checkDetails(ReadContext c) => AssistantTool(
  decl: ToolDecl(
    'check_details',
    'Contents of one receipt (check): lines and total. Give number, or last = true for the latest check. ru: что в чеке номер, последний чек.',
    parameters: sObj({
      'number': sStr(),
      'last': sBool(),
    }),
  ),
  group: ToolGroup.read,
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final number = '${r.args['number'] ?? ''}'.trim().replaceFirst(RegExp(r'^[№#]\s*'), '');
    if (number.isEmpty && r.args['last'] != true) {
      return ToolResult(ToolStatus.clarify, tr5(lang, 'Какой номер чека?', 'Чектин номери кайсы?', 'Chek raqami qaysi?', 'Which check number?', 'Hangi fiş numarası?'));
    }
    final load = await _loadChecks(c, env, null);
    Map<String, dynamic>? row;
    if (number.isEmpty) {
      row = load.rows.isEmpty ? null : load.rows.first;
    } else {
      for (final m in load.rows) {
        if ((str(m, 'number') ?? str(m, 'receipt_number')) == number) {
          row = m;
          break;
        }
      }
    }
    final id = row == null ? null : str(row, 'id');
    if (row == null || id == null) {
      return _none(env, 'Такого чека не нашла.', 'Андай чек табылган жок.', 'Bunday chek topilmadi.', 'No such check found.', 'Böyle bir fiş bulunamadı.');
    }
    final sale = await c.src.sale(id);
    dynamic items = sale['items'] ?? sale['lines'];
    if (items == null && sale['cart'] is Map) items = (sale['cart'] as Map)['items'];
    final lines = asList(items);
    final total = num0(sale, 'total') > 0 ? num0(sale, 'total') : num0(row, 'total');
    final at = DateTime.tryParse(str(row, 'created_at') ?? '')?.toLocal();
    final no = str(row, 'number') ?? str(row, 'receipt_number') ?? number;
    final pay = str(row, 'payment_method') ?? '';
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: '${tr5(lang, 'Чек', 'Чек', 'Chek', 'Check', 'Fiş')} №$no',
      subtitle: [
        if (at != null) '${_date(at, lang)} ${CallTable.formatCell(at, CellKind.time, lang)}',
        if (pay.isNotEmpty) CallTable.statusWord(pay, lang),
      ].join(' · '),
      columns: [
        _col(lang, 'name', CellKind.text, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'),
        _col(lang, 'qty', CellKind.qty, 'Кол-во', 'Саны', 'Soni', 'Qty', 'Adet'),
        _col(lang, 'price', CellKind.money, 'Цена', 'Баасы', 'Narxi', 'Price', 'Fiyat'),
        _col(lang, 'sum', CellKind.money, 'Сумма', 'Сумма', 'Summa', 'Amount', 'Tutar', sum: true),
      ],
      rows: [
        for (final m in lines)
          <Object?>[
            str(m, 'product_name') ?? str(m, 'name') ?? str(m, 'title') ?? '',
            num0(m, 'quantity') > 0 ? num0(m, 'quantity') : num0(m, 'qty'),
            num0(m, 'unit_price') > 0 ? num0(m, 'unit_price') : num0(m, 'price'),
            num0(m, 'total') > 0 ? num0(m, 'total') : num0(m, 'line_total'),
          ],
      ],
      totals: {'sum': total},
      source: 'phone',
      at: env.now(),
      lang: lang,
      objectRoute: 'check',
    );
    final say = tr5(
      lang,
      'Чек $no: ${lines.length} позиций на ${_money(total, lang)}${pay.isEmpty ? '' : ', ${CallTable.statusWord(pay, lang)}'}.',
      '$no чек: ${lines.length} позиция, ${_money(total, lang)}${pay.isEmpty ? '' : ', ${CallTable.statusWord(pay, lang)}'}.',
      '$no chek: ${lines.length} ta pozitsiya, ${_money(total, lang)}${pay.isEmpty ? '' : ', ${CallTable.statusWord(pay, lang)}'}.',
      'Check $no: ${lines.length} lines, ${_money(total, lang)}${pay.isEmpty ? '' : ', ${CallTable.statusWord(pay, lang)}'}.',
      '$no numaralı fiş: ${lines.length} kalem, ${_money(total, lang)}${pay.isEmpty ? '' : ', ${CallTable.statusWord(pay, lang)}'}.',
    );
    return _show(c, env, t, say, ['total ${_money(total, lang)}', 'lines ${lines.length}']);
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Клиенты и долги (имена — только на экране, в Google коды)
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _debtors(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'debtors',
    'Clients who owe money, as a table (names stay on the screen; never say codes like [Д1] aloud, say only the count and the total). Optional min_amount, limit. ru: кто мне должен, должники; ky: карызы барлар; uz: qarzdorlar; tr: borçlular.',
    parameters: sObj({'min_amount': sNum(), 'limit': sInt()}),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Клиенты',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final minAmount = r.args['min_amount'] is num ? (r.args['min_amount'] as num).toDouble() : 0.0;
    final limit = (r.args['limit'] is num ? (r.args['limit'] as num).toInt() : CallTable.pageSize).clamp(1, 500);
    // Как советник: коды по месту в списке по убыванию долга ([Д1] — самый большой долг).
    final all = (await c.src.debtors()).where((d) => d.debt > 0.004).toList()..sort((a, b) => b.debt.compareTo(a.debt));
    final codes = _Codes('Д');
    final coded = [
      for (final d in all)
        (
          d,
          codes.code(
            d.clientId,
            d.name.trim().isEmpty ? '' : '${d.name.trim()}${d.phone.trim().isEmpty ? '' : ' (${tr5(lang, 'тел.', 'тел.', 'tel.', 'tel.', 'tel.')} ${d.phone.trim()})'}',
          ),
        ),
    ];
    final list = coded.where((e) => e.$1.debt >= minAmount).toList();
    final total = list.fold(0.0, (a, e) => a + e.$1.debt);
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Долги клиентов', 'Кардарлардын карыздары', 'Mijozlar qarzi', 'Client debts', 'Müşteri borçları'),
      subtitle: minAmount > 0 ? '> ${_money(minAmount, lang)}' : null,
      columns: [
        _col(lang, 'client', CellKind.code, 'Должник', 'Карызкор', 'Qarzdor', 'Debtor', 'Borçlu'),
        _col(lang, 'debt', CellKind.money, 'Долг', 'Карыз', 'Qarz', 'Debt', 'Borç', sum: true),
        _col(lang, 'checks', CellKind.qty, 'Чеков в долг', 'Карызга чек', 'Qarz cheklar', 'Credit checks', 'Borç fişleri'),
      ],
      rows: [
        for (final e in list) <Object?>[e.$2, e.$1.debt, e.$1.salesCount],
      ],
      refs: [for (final e in list) e.$1.clientId],
      sortKey: 'debt',
      desc: true,
      names: codes.names,
      source: 'phone',
      at: env.now(),
      lang: lang,
      objectRoute: 'debts',
      shown: limit,
    );
    if (list.isEmpty) {
      return _show(c, env, t, tr5(lang, 'Непогашенных долгов нет.', 'Төлөнбөгөн карыз жок.', 'To\'lanmagan qarz yo\'q.', 'No unpaid debts.', 'Ödenmemiş borç yok.'), []);
    }
    final say = tr5(
      lang,
      'Должников ${list.length}, всего ${_money(total, lang)}.',
      'Карызкор ${list.length}, жалпысы ${_money(total, lang)}.',
      'Qarzdorlar ${list.length} ta, jami ${_money(total, lang)}.',
      '${list.length} debtors, ${_money(total, lang)} in total.',
      '${list.length} borçlu, toplam ${_money(total, lang)}.',
    );
    return _show(c, env, t, say, ['debtors ${list.length}', 'total ${_money(total, lang)}', 'biggest ${_money(list.first.$1.debt, lang)}']);
  }),
);

AssistantTool _clientInfo(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'client_info',
    'Find a client by name or phone: debt and bonus balance, as a table (names stay on the screen).',
    parameters: sObj({'query': sStr()}, required: ['query']),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Клиенты',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final q = '${r.args['query'] ?? ''}'.trim();
    if (q.isEmpty) return ToolResult(ToolStatus.clarify, tr5(lang, 'Какого клиента?', 'Кайсы кардар?', 'Qaysi mijoz?', 'Which client?', 'Hangi müşteri?'));
    final found = (await c.src.clients(q)).take(50).toList();
    if (found.isEmpty) {
      return _none(env, 'Такого клиента не нашла.', 'Андай кардар табылган жок.', 'Bunday mijoz topilmadi.', 'No such client found.', 'Böyle bir müşteri bulunamadı.');
    }
    final codes = _Codes('К');
    final rows = [
      for (final m in found)
        <Object?>[
          codes.code(m.id, m.name.trim().isEmpty ? m.phone : '${m.name.trim()}${m.phone.trim().isEmpty ? '' : ' (${m.phone.trim()})'}'),
          m.debt,
          m.bonus,
        ],
    ];
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Клиенты', 'Кардарлар', 'Mijozlar', 'Clients', 'Müşteriler'),
      columns: [
        _col(lang, 'client', CellKind.code, 'Клиент', 'Кардар', 'Mijoz', 'Client', 'Müşteri'),
        _col(lang, 'debt', CellKind.money, 'Долг', 'Карыз', 'Qarz', 'Debt', 'Borç'),
        _col(lang, 'bonus', CellKind.money, 'Бонусы', 'Бонус', 'Bonus', 'Bonus', 'Bonus'),
      ],
      rows: rows,
      refs: [for (final m in found) m.id],
      names: codes.names,
      source: 'phone',
      at: env.now(),
      lang: lang,
      objectRoute: 'clients',
    );
    final String say;
    if (found.length == 1) {
      final m = found.first;
      say = tr5(
        lang,
        'Долг ${_money(m.debt, lang)}, бонусы ${_money(m.bonus, lang)}.',
        'Карызы ${_money(m.debt, lang)}, бонус ${_money(m.bonus, lang)}.',
        'Qarzi ${_money(m.debt, lang)}, bonus ${_money(m.bonus, lang)}.',
        'Debt ${_money(m.debt, lang)}, bonus ${_money(m.bonus, lang)}.',
        'Borç ${_money(m.debt, lang)}, bonus ${_money(m.bonus, lang)}.',
      );
    } else {
      say = tr5(
        lang,
        'Нашла ${found.length} клиентов, список на экране.',
        '${found.length} кардар табылды, тизмеси экранда.',
        '${found.length} ta mijoz topildi, ro\'yxat ekranda.',
        'Found ${found.length} clients, the list is on the screen.',
        '${found.length} müşteri bulundu, liste ekranda.',
      );
    }
    return _show(c, env, t, say, found.length == 1 ? ['debt ${_money(found.first.debt, lang)}', 'bonus ${_money(found.first.bonus, lang)}'] : ['clients ${found.length}']);
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Смена, деньги кассы, заказы, корзина, журналы
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _shiftNow(ReadContext c) => AssistantTool(
  decl: const ToolDecl(
    'shift_now',
    'My current open shift: revenue, cash, non-cash, debt sales, expected cash in the drawer. ru: как смена, сколько в кассе; ky: смена кандай; uz: smena qanday; tr: vardiya nasıl.',
  ),
  group: ToolGroup.read,
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final sh = c.src.shift;
    if (sh == null) {
      return _none(env, 'Смена сейчас не открыта.', 'Смена азыр ачык эмес.', 'Smena hozir ochiq emas.', 'No shift is open now.', 'Şu an açık vardiya yok.', status: ToolStatus.ok);
    }
    Map<String, dynamic> m = const {};
    if (!sh.id.startsWith('offline-')) {
      try {
        m = await c.src.shiftReport(sh.id);
      } on ApiException catch (e) {
        if (!e.isTransient) rethrow;
      }
    }
    double? n(String k) => m[k] == null ? null : num0(m, k);
    final pay = m['by_payment'] is Map ? (m['by_payment'] as Map).cast<String, dynamic>() : const <String, dynamic>{};
    double pv(String k) => pay[k] == null ? 0 : num0(pay, k);
    final revenue = n('sales_total') ?? sh.salesTotal;
    final cash = n('cash_sales') ?? (pay.isNotEmpty ? pv('cash') + pv('mixed_cash') : sh.cashSales);
    final nonCash = n('noncash_sales') ?? sh.nonCashSales;
    final debt = pay.isNotEmpty ? pv('debt') : sh.debtSales;
    final expected = n('expected_cash') ?? sh.expectedCash;
    final checks = n('sales_count')?.round() ?? sh.salesCount;
    String label(String ru, String ky, String uz, String en, String tr) => tr5(lang, ru, ky, uz, en, tr);
    final partial = m.isEmpty;
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Моя смена', 'Менин сменам', 'Mening smenam', 'My shift', 'Vardiyam'),
      subtitle: sh.openedAt == null ? null : '${_date(sh.openedAt!, lang)} ${CallTable.formatCell(sh.openedAt, CellKind.time, lang)}',
      card: true,
      columns: [
        _col(lang, 'field', CellKind.text, 'Поле', 'Талаа', 'Maydon', 'Field', 'Alan'),
        _col(lang, 'value', CellKind.text, 'Значение', 'Мааниси', 'Qiymat', 'Value', 'Değer', numeric: false),
      ],
      rows: [
        [label('Открыта с', 'Ачылган', 'Ochilgan', 'Opened at', 'Açılış'), if (sh.openedAt != null) Cell(sh.openedAt, CellKind.time) else '—'],
        [label('Касса при открытии', 'Ачылышындагы касса', 'Ochilishdagi kassa', 'Opening cash', 'Açılış nakdi'), Cell(n('opening_cash') ?? sh.openingCash, CellKind.money)],
        [label('Выручка', 'Түшүм', 'Tushum', 'Revenue', 'Ciro'), Cell(revenue, CellKind.money)],
        [label('Чеков', 'Чектер', 'Cheklar', 'Checks', 'Fiş sayısı'), Cell(checks, CellKind.qty)],
        [label('Наличные', 'Накталай', 'Naqd', 'Cash', 'Nakit'), Cell(cash, CellKind.money)],
        [label('Безнал', 'Накталайсыз', 'Naqdsiz', 'Non-cash', 'Nakitsiz'), Cell(nonCash, CellKind.money)],
        [label('В долг', 'Карызга', 'Qarzga', 'On credit', 'Borç'), Cell(debt, CellKind.money)],
        if ((n('returns_total') ?? 0).abs() > 0.004) [label('Возвраты', 'Кайтаруулар', 'Qaytarishlar', 'Returns', 'İadeler'), Cell(n('returns_total'), CellKind.money)],
        if ((n('deposits') ?? 0) > 0.004) [label('Внесено', 'Салынды', 'Kiritildi', 'Cash in', 'Giriş'), Cell(n('deposits'), CellKind.money)],
        if ((n('withdrawals') ?? 0) > 0.004) [label('Изъято', 'Алынды', 'Olindi', 'Cash out', 'Çıkış'), Cell(n('withdrawals'), CellKind.money)],
        [label('Должно быть в кассе', 'Кассада болуш керек', 'Kassada bo\'lishi kerak', 'Expected cash', 'Kasada olması gereken'), Cell(expected, CellKind.money)],
      ],
      source: 'shift',
      at: env.now(),
      lang: lang,
      note: partial
          ? tr5(lang, 'Цифры телефона: отчёт смены с сервера не получен.', 'Телефондун цифралары: сервердин отчету алынган жок.', 'Telefon raqamlari: serverdan hisobot olinmadi.', 'Phone figures: the server shift report was not received.', 'Telefon rakamları: sunucu vardiya raporu alınamadı.')
          : null,
    );
    final say = tr5(
      lang,
      'Смена открыта: выручка ${_money(revenue, lang)}, чеков $checks, в кассе должно быть ${_money(expected, lang)}.',
      'Смена ачык: түшүм ${_money(revenue, lang)}, чек $checks, кассада ${_money(expected, lang)} болуш керек.',
      'Smena ochiq: tushum ${_money(revenue, lang)}, cheklar $checks, kassada ${_money(expected, lang)} bo\'lishi kerak.',
      'Shift is open: revenue ${_money(revenue, lang)}, $checks checks, expected cash ${_money(expected, lang)}.',
      'Vardiya açık: ciro ${_money(revenue, lang)}, $checks fiş, kasada ${_money(expected, lang)} olmalı.',
    );
    return _show(c, env, t, say, ['revenue ${_money(revenue, lang)}', 'expected cash ${_money(expected, lang)}', 'checks $checks']);
  }),
);

AssistantTool _shiftsList(ReadContext c) => AssistantTool(
  decl: ToolDecl(
    'shifts_list',
    'Shifts of all cashiers for a period as a table (cashier names stay on the screen).',
    parameters: sObj({'from': _from(), 'to': _to()}),
  ),
  group: ToolGroup.read,
  allowed: (s) => s.can(Session.viewShifts),
  needs: 'Смены',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final p = parsePeriod(r.args, env.now(), fallback: 'week');
    final all = await c.src.shifts();
    final list = [
      for (final s in all)
        if (s.openedAt != null && !_day(s.openedAt!).isBefore(p.from) && !_day(s.openedAt!).isAfter(p.to)) s,
    ];
    // Репозиторий отдаёт только первые 100 смен без листания: если самая старая из них новее начала периода, данные неполные.
    final oldest = all.map((s) => s.openedAt).whereType<DateTime>().fold<DateTime?>(null, (a, d) => a == null || d.isBefore(a) ? d : a);
    final partial = all.length >= 100 && oldest != null && _day(oldest).isAfter(p.from);
    final codes = _Codes('С');
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Смены', 'Смендер', 'Smenalar', 'Shifts', 'Vardiyalar'),
      subtitle: p.label(lang),
      columns: [
        _col(lang, 'date', CellKind.date, 'Открыта', 'Ачылган', 'Ochilgan', 'Opened', 'Açılış'),
        _col(lang, 'cashier', CellKind.code, 'Кассир', 'Кассир', 'Kassir', 'Cashier', 'Kasiyer'),
        _col(lang, 'status', CellKind.status, 'Статус', 'Статус', 'Holat', 'Status', 'Durum'),
        _col(lang, 'sum', CellKind.money, 'Продажи', 'Сатуу', 'Savdo', 'Sales', 'Satış', sum: true),
        _col(lang, 'checks', CellKind.qty, 'Чеков', 'Чек', 'Chek', 'Checks', 'Fiş', sum: true),
      ],
      rows: [
        for (final s in list)
          <Object?>[s.openedAt, s.cashier.trim().isEmpty ? '—' : codes.code(s.cashier.trim().toLowerCase(), s.cashier), s.isOpen ? 'open' : 'closed', s.sales, s.checks],
      ],
      refs: [for (final s in list) s.id],
      names: codes.names,
      source: 'shifts',
      at: env.now(),
      lang: lang,
      note: partial
          ? tr5(lang, 'Сервер отдал только последние 100 смен — более ранних здесь может не быть.', 'Сервер акыркы 100 сменаны гана берди — мурункулары жок болушу мүмкүн.', 'Server faqat oxirgi 100 smenani berdi — eskilari bo\'lmasligi mumkin.', 'The server returned only the latest 100 shifts — older ones may be missing.', 'Sunucu yalnızca son 100 vardiyayı verdi — eskiler eksik olabilir.')
          : null,
      objectRoute: 'shifts',
    );
    if (list.isEmpty) {
      return _show(c, env, t, tr5(lang, 'За ${p.label(lang)} смен не нашла.', '${p.label(lang)} аралыгында смена табылган жок.', '${p.label(lang)} uchun smena topilmadi.', 'No shifts for ${p.label(lang)}.', '${p.label(lang)} için vardiya bulunamadı.'), []);
    }
    final total = list.fold(0.0, (a, s) => a + s.sales);
    final say = tr5(
      lang,
      'За ${p.label(lang)} смен ${list.length}, продажи ${_money(total, lang)}${partial ? ' (данные могут быть неполными)' : ''}.',
      '${p.label(lang)} аралыгында ${list.length} смена, сатуу ${_money(total, lang)}${partial ? ' (маалымат толук эмес болушу мүмкүн)' : ''}.',
      '${p.label(lang)} uchun ${list.length} ta smena, savdo ${_money(total, lang)}${partial ? " (ma'lumot to'liq bo'lmasligi mumkin)" : ''}.',
      '${list.length} shifts for ${p.label(lang)}, sales ${_money(total, lang)}${partial ? ' (data may be incomplete)' : ''}.',
      '${p.label(lang)} için ${list.length} vardiya, satış ${_money(total, lang)}${partial ? ' (veri eksik olabilir)' : ''}.',
    );
    return _show(c, env, t, say, ['shifts ${list.length}', 'sales ${_money(total, lang)}']);
  }),
);

AssistantTool _cashMoves(ReadContext c) => AssistantTool(
  decl: ToolDecl(
    'cash_moves',
    'Cash in / cash out of the drawer during my current shift (or another shift id if the user may view shifts), as a table.',
    parameters: sObj({'shift': sStr(desc: 'shift id, optional')}),
  ),
  group: ToolGroup.read,
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final own = c.src.shift?.id;
    final asked = '${r.args['shift'] ?? ''}'.trim();
    final id = asked.isEmpty ? own : asked;
    if (id == null) {
      return _none(env, 'Смена сейчас не открыта.', 'Смена азыр ачык эмес.', 'Smena hozir ochiq emas.', 'No shift is open now.', 'Şu an açık vardiya yok.', status: ToolStatus.ok);
    }
    // Чужая смена — только с правом «Смены».
    if (id != own && !env.session.can(Session.viewShifts)) {
      return _denied(env, 'Нет права смотреть чужие смены.', 'Бөтөн смендерди көрүүгө укук жок.', 'Boshqa smenalarni ko\'rishga huquq yo\'q.', 'No right to view other shifts.', 'Başka vardiyaları görme yetkisi yok.');
    }
    if (id.startsWith('offline-')) {
      return _none(env, 'Смена ещё не передана на сервер — движений денег нет.', 'Смена серверге жиберилген эмес — акча кыймылы жок.', 'Smena serverga yuborilmagan — pul harakati yo\'q.', 'The shift is not sent to the server yet — no cash moves.', 'Vardiya henüz sunucuya gönderilmedi — para hareketi yok.');
    }
    final moves = await c.src.cashFlows(id);
    final inSum = moves.where((m) => !m.$4).fold(0.0, (a, m) => a + m.$3);
    final outSum = moves.where((m) => m.$4).fold(0.0, (a, m) => a + m.$3);
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Внесения и изъятия', 'Киргизүү жана алуу', 'Kirim va chiqim', 'Cash in and out', 'Kasa giriş ve çıkışları'),
      columns: [
        _col(lang, 'time', CellKind.time, 'Время', 'Убакыт', 'Vaqt', 'Time', 'Saat'),
        _col(lang, 'dir', CellKind.status, 'Вид', 'Түрү', 'Turi', 'Type', 'Tür'),
        _col(lang, 'text', CellKind.text, 'Причина', 'Себеби', 'Sababi', 'Reason', 'Neden'),
        _col(lang, 'sum', CellKind.money, 'Сумма', 'Сумма', 'Summa', 'Amount', 'Tutar'),
      ],
      rows: [
        for (final m in moves) <Object?>[m.$1, m.$4 ? 'out' : 'in', m.$2, m.$4 ? -m.$3 : m.$3],
      ],
      totals: {'sum': inSum - outSum},
      source: 'shift',
      at: env.now(),
      lang: lang,
    );
    if (moves.isEmpty) {
      return _show(c, env, t, tr5(lang, 'Внесений и изъятий не было.', 'Киргизүү же алуу болгон эмес.', 'Kirim yoki chiqim bo\'lmagan.', 'No cash in or out.', 'Giriş veya çıkış olmadı.'), []);
    }
    final say = tr5(
      lang,
      'Внесено ${_money(inSum, lang)}, изъято ${_money(outSum, lang)}.',
      'Салынды ${_money(inSum, lang)}, алынды ${_money(outSum, lang)}.',
      'Kiritildi ${_money(inSum, lang)}, olindi ${_money(outSum, lang)}.',
      'Cash in ${_money(inSum, lang)}, cash out ${_money(outSum, lang)}.',
      'Giriş ${_money(inSum, lang)}, çıkış ${_money(outSum, lang)}.',
    );
    return _show(c, env, t, say, ['in ${_money(inSum, lang)}', 'out ${_money(outSum, lang)}']);
  }),
);

AssistantTool _ordersList(ReadContext c) => AssistantTool(
  decl: ToolDecl(
    'orders_list',
    'Online/showcase orders as a table. Optional status (new, processing, done, canceled). ru: заказы; ky: буйрутмалар; uz: buyurtmalar; tr: siparişler.',
    parameters: sObj({'status': sStr()}),
  ),
  group: ToolGroup.read,
  allowed: (s) => s.can(Session.viewOrders),
  needs: 'Заказы',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final status = '${r.args['status'] ?? ''}'.trim().toLowerCase();
    final all = await c.src.orders();
    final list = status.isEmpty ? all : all.where((o) => o.status == status).toList();
    list.sort((a, b) => (b.createdAt ?? DateTime(2000)).compareTo(a.createdAt ?? DateTime(2000)));
    final codes = _Codes('К');
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Заказы', 'Буйрутмалар', 'Buyurtmalar', 'Orders', 'Siparişler'),
      subtitle: status.isEmpty ? null : CallTable.statusWord(status, lang),
      columns: [
        _col(lang, 'number', CellKind.text, '№', '№', '№', '#', 'No', numeric: false),
        _col(lang, 'client', CellKind.code, 'Клиент', 'Кардар', 'Mijoz', 'Client', 'Müşteri'),
        _col(lang, 'status', CellKind.status, 'Статус', 'Статус', 'Holat', 'Status', 'Durum'),
        _col(lang, 'sum', CellKind.money, 'Сумма', 'Сумма', 'Summa', 'Amount', 'Tutar', sum: true),
        _col(lang, 'date', CellKind.date, 'Дата', 'Күнү', 'Sana', 'Date', 'Tarih'),
      ],
      rows: [
        for (final o in list)
          <Object?>[
            o.number,
            o.customer.trim().isEmpty ? '—' : codes.code(o.id, '${o.customer.trim()}${o.phone.trim().isEmpty ? '' : ' (${o.phone.trim()})'}'),
            o.status,
            o.total,
            o.createdAt,
          ],
      ],
      refs: [for (final o in list) o.id],
      names: codes.names,
      source: 'orders',
      at: env.now(),
      lang: lang,
      objectRoute: 'orders',
    );
    if (list.isEmpty) {
      return _show(c, env, t, tr5(lang, 'Заказов нет.', 'Буйрутма жок.', 'Buyurtmalar yo\'q.', 'No orders.', 'Sipariş yok.'), []);
    }
    final fresh = list.where((o) => o.status == 'new').length;
    final total = list.fold(0.0, (a, o) => a + o.total);
    final say = tr5(
      lang,
      'Заказов ${list.length} на ${_money(total, lang)}, новых $fresh.',
      'Буйрутма ${list.length}, жалпы ${_money(total, lang)}, жаңысы $fresh.',
      'Buyurtmalar ${list.length} ta, jami ${_money(total, lang)}, yangisi $fresh.',
      '${list.length} orders totalling ${_money(total, lang)}, $fresh new.',
      '${list.length} sipariş, toplam ${_money(total, lang)}, yeni $fresh.',
    );
    return _show(c, env, t, say, ['orders ${list.length}', 'new $fresh']);
  }),
);

AssistantTool _cartShow(ReadContext c) => AssistantTool(
  decl: const ToolDecl('cart_show', 'What is in the current sale cart now, with the total. ru: что в чеке, сколько в корзине.'),
  group: ToolGroup.read,
  run: (r) async {
    final env = r.env;
    final lang = env.lang;
    final cart = c.src.cart;
    if (cart.isEmpty) {
      return _none(env, 'Чек пустой.', 'Чек бош.', 'Chek bo\'sh.', 'The cart is empty.', 'Sepet boş.', status: ToolStatus.ok);
    }
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Текущий чек', 'Учурдагы чек', 'Joriy chek', 'Current cart', 'Geçerli sepet'),
      columns: [
        _col(lang, 'name', CellKind.text, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'),
        _col(lang, 'qty', CellKind.qty, 'Кол-во', 'Саны', 'Soni', 'Qty', 'Adet'),
        _col(lang, 'price', CellKind.money, 'Цена', 'Баасы', 'Narxi', 'Price', 'Fiyat'),
        _col(lang, 'discount', CellKind.money, 'Скидка', 'Арзандатуу', 'Chegirma', 'Discount', 'İndirim'),
        _col(lang, 'sum', CellKind.money, 'Сумма', 'Сумма', 'Summa', 'Amount', 'Tutar'),
      ],
      rows: [
        for (final l in cart.lines) <Object?>[l.name, Cell(l.qty, CellKind.qty, unit: l.unit), l.price, l.discount > 0 ? -l.discount : null, l.total],
      ],
      totals: {'sum': cart.total},
      note: cart.orderDiscount > 0
          ? tr5(lang, 'Скидка на весь чек: ${_money(cart.orderDiscount, lang)}', 'Бүт чекке арзандатуу: ${_money(cart.orderDiscount, lang)}', 'Butun chekka chegirma: ${_money(cart.orderDiscount, lang)}', 'Order discount: ${_money(cart.orderDiscount, lang)}', 'Tüm fişe indirim: ${_money(cart.orderDiscount, lang)}')
          : null,
      source: 'cart',
      at: env.now(),
      lang: lang,
      objectRoute: 'sale',
    );
    final say = tr5(
      lang,
      'В чеке ${cart.count} позиций, итого ${_money(cart.total, lang)}.',
      'Чекте ${cart.count} позиция, жалпы ${_money(cart.total, lang)}.',
      'Chekda ${cart.count} ta pozitsiya, jami ${_money(cart.total, lang)}.',
      '${cart.count} lines in the cart, total ${_money(cart.total, lang)}.',
      'Sepette ${cart.count} kalem var, toplam ${_money(cart.total, lang)}.',
    );
    return _show(c, env, t, say, ['total ${_money(cart.total, lang)}', 'lines ${cart.count}']);
  },
);

AssistantTool _deletionsLog(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'deletions_log',
    'Items removed from receipts on this phone (what, how many, why), as a table. Optional period.',
    parameters: sObj({'from': _from(), 'to': _to()}),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Отчёты',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final p = parsePeriod(r.args, env.now(), fallback: 'week');
    final list = [
      for (final d in await c.src.deletions())
        if (!_day(d.at).isBefore(p.from) && !_day(d.at).isAfter(p.to)) d,
    ];
    final codes = _Codes('С');
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Удалено из чека', 'Чектен өчүрүлгөн', 'Chekdan o\'chirilgan', 'Removed from checks', 'Fişten silinenler'),
      subtitle: p.label(lang),
      columns: [
        _col(lang, 'date', CellKind.date, 'Дата', 'Күнү', 'Sana', 'Date', 'Tarih'),
        _col(lang, 'time', CellKind.time, 'Время', 'Убакыт', 'Vaqt', 'Time', 'Saat'),
        _col(lang, 'name', CellKind.text, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün'),
        _col(lang, 'qty', CellKind.qty, 'Кол-во', 'Саны', 'Soni', 'Qty', 'Adet'),
        _col(lang, 'sum', CellKind.money, 'Сумма', 'Сумма', 'Summa', 'Amount', 'Tutar', sum: true),
        _col(lang, 'reason', CellKind.text, 'Причина', 'Себеби', 'Sababi', 'Reason', 'Neden'),
        _col(lang, 'cashier', CellKind.code, 'Кассир', 'Кассир', 'Kassir', 'Cashier', 'Kasiyer'),
      ],
      rows: [
        for (final d in list)
          <Object?>[d.at, d.at, d.name, d.qty, d.sum, d.reason, d.cashier.trim().isEmpty ? '—' : codes.code(d.cashier.trim().toLowerCase(), d.cashier)],
      ],
      names: codes.names,
      source: 'deletions.log',
      at: env.now(),
      lang: lang,
      objectRoute: 'deletions',
    );
    if (list.isEmpty) {
      return _show(c, env, t, tr5(lang, 'За ${p.label(lang)} удалений из чека не было.', '${p.label(lang)} аралыгында чектен өчүрүү болгон эмес.', '${p.label(lang)} uchun chekdan o\'chirish bo\'lmagan.', 'Nothing was removed from checks for ${p.label(lang)}.', '${p.label(lang)} için fişten silme olmadı.'), []);
    }
    final total = list.fold(0.0, (a, d) => a + d.sum);
    final say = tr5(
      lang,
      'Удалено ${list.length} строк на ${_money(total, lang)}.',
      '${list.length} саптар өчүрүлдү, жалпы ${_money(total, lang)}.',
      '${list.length} ta qator o\'chirildi, jami ${_money(total, lang)}.',
      '${list.length} lines removed, ${_money(total, lang)} in total.',
      '${list.length} satır silindi, toplam ${_money(total, lang)}.',
    );
    return _show(c, env, t, say, ['removed ${list.length}', 'sum ${_money(total, lang)}']);
  }),
);

AssistantTool _auditLog(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'audit_log',
    'Audit journal of this phone (who did what: sales, returns, prices, shifts…) as a table. Optional period and kind.',
    parameters: sObj({'from': _from(), 'to': _to(), 'kind': sStr(values: Audit.kinds)}),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Отчёты',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final p = parsePeriod(r.args, env.now(), fallback: 'week');
    final kind = '${r.args['kind'] ?? ''}'.trim();
    final list = [
      for (final e in await c.src.audit())
        if (!_day(e.at).isBefore(p.from) && !_day(e.at).isAfter(p.to) && (kind.isEmpty || e.kind == kind)) e,
    ];
    final codes = _Codes('С');
    final t = CallTable.build(
      id: c.tables.nextId(),
      title: tr5(lang, 'Журнал аудита', 'Аудит журналы', 'Audit jurnali', 'Audit journal', 'Denetim günlüğü'),
      subtitle: [p.label(lang), if (kind.isNotEmpty) kind].join(' · '),
      columns: [
        _col(lang, 'date', CellKind.date, 'Дата', 'Күнү', 'Sana', 'Date', 'Tarih'),
        _col(lang, 'time', CellKind.time, 'Время', 'Убакыт', 'Vaqt', 'Time', 'Saat'),
        _col(lang, 'user', CellKind.code, 'Кто', 'Ким', 'Kim', 'Who', 'Kim'),
        _col(lang, 'kind', CellKind.text, 'Вид', 'Түрү', 'Turi', 'Kind', 'Tür'),
        _col(lang, 'text', CellKind.text, 'Что', 'Эмне', 'Nima', 'What', 'Ne'),
        _col(lang, 'sum', CellKind.money, 'Сумма', 'Сумма', 'Summa', 'Amount', 'Tutar'),
      ],
      rows: [
        for (final e in list) <Object?>[e.at, e.at, e.user.trim().isEmpty ? '—' : codes.code(e.user.trim().toLowerCase(), e.user), e.kind, e.text, e.amount],
      ],
      names: codes.names,
      source: 'audit.jsonl',
      at: env.now(),
      lang: lang,
      objectRoute: 'audit',
    );
    if (list.isEmpty) {
      return _show(c, env, t, tr5(lang, 'Записей в журнале нет.', 'Журналда жазуу жок.', 'Jurnalda yozuv yo\'q.', 'No journal entries.', 'Günlükte kayıt yok.'), []);
    }
    final say = tr5(lang, 'В журнале ${list.length} записей.', 'Журналда ${list.length} жазуу.', 'Jurnalda ${list.length} ta yozuv.', '${list.length} journal entries.', 'Günlükte ${list.length} kayıt var.');
    return _show(c, env, t, say, ['entries ${list.length}']);
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Рекомендации по товарам (раздел 8.3 ТЗ)
// ---------------------------------------------------------------------------------------------------------------------

AssistantTool _recommend(ReadContext c, bool Function(Session) allowed) => AssistantTool(
  decl: ToolDecl(
    'recommend',
    'Product recommendations as a table: reorder (what to order), promote (what to push), dead_stock (no sales), low_markup (price too low). companions and supplier_order need the server and are not available yet. '
    'ru: что заказать, что продвигать, неликвид, низкая наценка.',
    parameters: sObj({
      'kind': sStr(values: ['reorder', 'promote', 'dead_stock', 'low_markup', 'companions', 'supplier_order']),
      'days': sInt(desc: 'sales window in days'),
      'limit': sInt(),
    }, required: ['kind']),
  ),
  group: ToolGroup.read,
  allowed: allowed,
  needs: 'Отчёты',
  run: (r) => _safe(r.env, () async {
    final env = r.env;
    final lang = env.lang;
    final s = env.session;
    final kind = '${r.args['kind'] ?? ''}';
    final limit = (r.args['limit'] is num ? (r.args['limit'] as num).toInt() : CallTable.pageSize).clamp(1, 500);
    int daysArg(int d) => (r.args['days'] is num ? (r.args['days'] as num).toInt() : d).clamp(1, 365);
    final today = _day(env.now());
    Period window(int days, [int skip = 0]) => Period(today.subtract(Duration(days: days - 1 + skip)), today.subtract(Duration(days: skip)));

    if (kind == 'companions' || kind == 'supplier_order') {
      return ToolResult(
        ToolStatus.error,
        tr5(
          lang,
          'Эта рекомендация пока недоступна в мобильном приложении — смотрите её в программе на компьютере.',
          'Бул сунуш мобилдик тиркемеде азырынча жок — компьютердеги программадан караңыз.',
          'Bu tavsiya mobil ilovada hozircha mavjud emas — kompyuterdagi dasturdan ko\'ring.',
          'This recommendation is not available in the mobile app yet — see it in the desktop program.',
          'Bu öneri mobil uygulamada henüz yok — bilgisayardaki programdan bakın.',
        ),
      );
    }
    if ((kind == 'reorder' || kind == 'dead_stock') && !s.can(Session.viewProducts)) {
      return _denied(env, 'Нет права «Склад» — остатки показать не могу.', '«Склад» укугу жок — калдыкты көрсөтө албайм.', '«Ombor» huquqi yo\'q — qoldiqni ko\'rsata olmayman.', 'No Stock right — I cannot show stock.', '«Depo» yetkisi yok — stoğu gösteremem.');
    }
    if ((kind == 'low_markup' || kind == 'promote') && !_canCost(s)) {
      return _denied(
        env,
        'Нет права на закупочные цены — наценку показать не могу.',
        'Закуп баалары үчүн укук жок — үстөктү көрсөтө албайм.',
        'Xarid narxlari huquqi yo\'q — ustamani ko\'rsata olmayman.',
        'No right to purchase prices — I cannot show markup.',
        'Alış fiyatı yetkisi yok — kâr oranını gösteremem.',
      );
    }
    final catalog = (await c.src.catalog('')).where((p) => !p.isService).toList();
    final stock = [for (final p in catalog) RecStock(p.id, p.name, p.unit, p.quantity, p.price, minQty: p.minQuantity)];
    final id = c.tables.nextId();
    CallColumn name() => _col(lang, 'name', CellKind.text, 'Товар', 'Товар', 'Mahsulot', 'Product', 'Ürün');

    switch (kind) {
      case 'reorder':
        final days = daysArg(recSalesDays);
        final w = window(days);
        final list = reorderList(stock, await c.src.productSales(w.from, w.to), days: days);
        final t = CallTable.build(
          id: id,
          title: tr5(lang, 'Что заказать', 'Эмнени заказ кылуу', 'Nimani buyurtma qilish', 'What to reorder', 'Ne sipariş edilmeli'),
          subtitle: tr5(lang, 'продажи за $days дн.', '$days күндүк сатуу', '$days kunlik savdo', 'sales for $days days', '$days günlük satış'),
          columns: [
            name(),
            _col(lang, 'qty', CellKind.qty, 'Остаток', 'Калдык', 'Qoldiq', 'Stock', 'Stok'),
            _col(lang, 'perday', CellKind.qty, 'В день', 'Күнүнө', 'Kuniga', 'Per day', 'Günlük'),
            _col(lang, 'days', CellKind.qty, 'Хватит дней', 'Канча күнгө', 'Necha kunga', 'Days left', 'Kaç güne yeter', unit: tr5(lang, 'дн', 'күн', 'kun', 'd', 'gün')),
            _col(lang, 'order', CellKind.qty, 'Заказать', 'Заказ', 'Buyurtma', 'Order', 'Sipariş'),
          ],
          rows: [
            for (final e in list) <Object?>[e.item.name, Cell(e.item.qty, CellKind.qty, unit: e.item.unit), Cell(e.perDay, CellKind.qty, unit: e.item.unit), e.daysLeft, Cell(e.order, CellKind.qty, unit: e.item.unit)],
          ],
          refs: [for (final e in list) e.item.id.isEmpty ? null : e.item.id],
          source: 'tab=products',
          at: env.now(),
          lang: lang,
          objectRoute: 'product',
          shown: limit,
        );
        if (list.isEmpty) {
          return _show(c, env, t, tr5(lang, 'Заказывать пока нечего: запасов хватает.', 'Азырынча заказ кылуучу нерсе жок: запас жетиштүү.', 'Hozircha buyurtma qiladigan narsa yo\'q: zaxira yetarli.', 'Nothing to reorder: stock is enough.', 'Sipariş edilecek bir şey yok: stok yeterli.'), []);
        }
        final out = list.where((e) => e.item.qty <= 0).length;
        final f = list.first;
        final fn = _clip(f.item.name);
        final say = tr5(
          lang,
          'Заказать нужно ${list.length} товаров, закончилось $out. Срочнее всего — $fn.',
          '${list.length} товарды заказ кылуу керек, $out бүттү. Эң шашылыш — $fn.',
          '${list.length} ta mahsulotni buyurtma qilish kerak, $out tasi tugagan. Eng shoshilinchi — $fn.',
          '${list.length} products need reordering, $out are out. Most urgent — $fn.',
          '${list.length} ürün sipariş edilmeli, $out tanesi bitti. En acil — $fn.',
        );
        return _show(c, env, t, say, ['reorder ${list.length}', 'out $out', 'most urgent $fn']);

      case 'dead_stock':
        final days = daysArg(recDeadDays);
        final w = window(days);
        final list = deadStock(stock, await c.src.productSales(w.from, w.to));
        final money = _canCost(s);
        final frozen = list.fold(0.0, (a, e) => a + e.frozen);
        final t = CallTable.build(
          id: id,
          title: tr5(lang, 'Неликвид', 'Сатылбай жаткан', 'Sotilmayotgan', 'Dead stock', 'Satılmayanlar'),
          subtitle: tr5(lang, 'нет продаж $days дн.', '$days күн сатуу жок', '$days kun savdo yo\'q', 'no sales for $days days', '$days gün satış yok'),
          columns: [
            name(),
            _col(lang, 'qty', CellKind.qty, 'Остаток', 'Калдык', 'Qoldiq', 'Stock', 'Stok'),
            if (money) _col(lang, 'frozen', CellKind.money, 'Заморожено (по цене)', 'Тоңдурулган (баасы боюнча)', 'Muzlatilgan (narx bo\'yicha)', 'Frozen (at price)', 'Dondurulan (fiyatla)', sum: true),
          ],
          rows: [
            for (final e in list) <Object?>[e.item.name, Cell(e.item.qty, CellKind.qty, unit: e.item.unit), if (money) e.frozen],
          ],
          refs: [for (final e in list) e.item.id.isEmpty ? null : e.item.id],
          sortKey: money ? 'frozen' : 'qty',
          desc: true,
          source: 'tab=products',
          at: env.now(),
          lang: lang,
          objectRoute: 'product',
          shown: limit,
        );
        if (list.isEmpty) {
          return _show(c, env, t, tr5(lang, 'Неликвидных товаров нет.', 'Сатылбай жаткан товар жок.', 'Sotilmayotgan mahsulot yo\'q.', 'No dead stock.', 'Satılmayan ürün yok.'), []);
        }
        final f = list.first;
        final fn = _clip(f.item.name);
        final say = money
            ? tr5(
                lang,
                'Без продаж $days дней ${list.length} товаров, заморожено около ${_money(frozen, lang)}; больше всего — $fn.',
                '$days күн сатылбаган ${list.length} товар, болжол менен ${_money(frozen, lang)} тоңуп турат; эң көбү — $fn.',
                '$days kun sotilmagan ${list.length} ta mahsulot, taxminan ${_money(frozen, lang)} muzlab turibdi; eng ko\'pi — $fn.',
                '${list.length} products had no sales for $days days, about ${_money(frozen, lang)} is frozen; most — $fn.',
                '$days gündür satılmayan ${list.length} ürün var, yaklaşık ${_money(frozen, lang)} donmuş; en çok — $fn.',
              )
            : tr5(
                lang,
                'Без продаж $days дней ${list.length} товаров.',
                '$days күн сатылбаган ${list.length} товар.',
                '$days kun sotilmagan ${list.length} ta mahsulot.',
                '${list.length} products had no sales for $days days.',
                '$days gündür satılmayan ${list.length} ürün var.',
              );
        return _show(c, env, t, say, ['dead ${list.length}', if (money) 'frozen ${_money(frozen, lang)}', 'top $fn']);

      case 'low_markup':
        final days = daysArg(90);
        final w = window(days);
        final list = lowMarkup(stock, await c.src.productSales(w.from, w.to));
        final t = CallTable.build(
          id: id,
          title: tr5(lang, 'Низкая наценка', 'Төмөн үстөк', 'Past ustama', 'Low markup', 'Düşük kâr oranı'),
          subtitle: tr5(lang, 'проданные за $days дн.', '$days күндө сатылган', '$days kunda sotilgan', 'sold in $days days', '$days günde satılan'),
          columns: [
            name(),
            _col(lang, 'purchase', CellKind.money, 'Закупка', 'Закуп', 'Xarid', 'Purchase', 'Alış'),
            _col(lang, 'price', CellKind.money, 'Цена', 'Баасы', 'Narxi', 'Price', 'Fiyat'),
            _col(lang, 'markup', CellKind.percent, 'Наценка', 'Үстөк', 'Ustama', 'Markup', 'Kâr oranı'),
            _col(lang, 'suggested', CellKind.money, 'Цена с ${recMinMarkupPct.toStringAsFixed(0)} %', '${recMinMarkupPct.toStringAsFixed(0)} % менен баа', '${recMinMarkupPct.toStringAsFixed(0)} % bilan narx', 'Price at ${recMinMarkupPct.toStringAsFixed(0)}%', '%${recMinMarkupPct.toStringAsFixed(0)} ile fiyat'),
          ],
          rows: [
            for (final e in list) <Object?>[e.item.name, e.purchase, e.price, e.markup, e.suggested],
          ],
          refs: [for (final e in list) e.item.id.isEmpty ? null : e.item.id],
          sortKey: 'markup',
          source: 'tab=products',
          at: env.now(),
          lang: lang,
          objectRoute: 'product',
          shown: limit,
        );
        if (list.isEmpty) {
          return _show(c, env, t, tr5(lang, 'Товаров с низкой наценкой нет.', 'Үстөгү төмөн товар жок.', 'Ustamasi past mahsulot yo\'q.', 'No low-markup products.', 'Düşük kâr oranlı ürün yok.'), []);
        }
        final below = list.where((e) => e.markup < 0).length;
        final f = list.first;
        final fn = _clip(f.item.name);
        final say = tr5(
          lang,
          'Наценка ниже ${recMinMarkupPct.toStringAsFixed(0)} % у ${list.length} товаров, ниже закупки из них: $below. Хуже всего — $fn.',
          'Үстөк ${recMinMarkupPct.toStringAsFixed(0)} % дан төмөн — ${list.length} товар, алардын $below закуптан төмөн сатылат. Эң начары — $fn.',
          'Ustama ${recMinMarkupPct.toStringAsFixed(0)} % dan past — ${list.length} ta mahsulot, ulardan $below tasi xariddan past narxda. Eng yomoni — $fn.',
          '${list.length} products have markup below ${recMinMarkupPct.toStringAsFixed(0)}%, $below of them sell below cost. Worst — $fn.',
          '${list.length} üründe kâr oranı %${recMinMarkupPct.toStringAsFixed(0)} altında, $below tanesi maliyetin altında satılıyor. En kötüsü — $fn.',
        );
        return _show(c, env, t, say, ['low ${list.length}', 'below cost $below', 'worst $fn ${_pct(f.markup, lang)}']);

      case 'promote':
        final w = daysArg(14);
        final both = await Future.wait<List<ProductSales>>([
          c.src.productSales(window(w).from, window(w).to),
          c.src.productSales(window(w, w).from, window(w, w).to),
        ]);
        final list = promoteList(stock, both[0], both[1]);
        final t = CallTable.build(
          id: id,
          title: tr5(lang, 'Что продвигать', 'Эмнени жылдыруу', 'Nimani targ\'ib qilish', 'What to promote', 'Neyi öne çıkarmalı'),
          subtitle: tr5(lang, 'продажи за $w дн. к прошлым $w', '$w күн мурунку $w күнгө салыштырмалуу', '$w kun, oldingi $w kunga nisbatan', '$w days vs previous $w', '$w gün, önceki $w güne göre'),
          columns: [
            name(),
            _col(lang, 'markup', CellKind.percent, 'Наценка', 'Үстөк', 'Ustama', 'Markup', 'Kâr oranı', signed: false),
            _col(lang, 'qty', CellKind.qty, 'Продано', 'Сатылды', 'Sotildi', 'Sold', 'Satılan'),
            _col(lang, 'growth', CellKind.percent, 'Рост', 'Өсүү', 'O\'sish', 'Growth', 'Büyüme'),
          ],
          rows: [
            for (final e in list) <Object?>[e.item.name, e.markup, Cell(e.qty, CellKind.qty, unit: e.item.unit), e.growth],
          ],
          refs: [for (final e in list) e.item.id.isEmpty ? null : e.item.id],
          source: 'tab=products',
          at: env.now(),
          lang: lang,
          objectRoute: 'product',
          shown: limit,
        );
        if (list.isEmpty) {
          return _show(c, env, t, tr5(lang, 'Подходящих товаров для продвижения не нашла.', 'Жылдырууга ылайыктуу товар табылган жок.', 'Targ\'ib qilishga mos mahsulot topilmadi.', 'No suitable products to promote.', 'Öne çıkarılacak uygun ürün bulunamadı.'), []);
        }
        final f = list.first;
        final fn = _clip(f.item.name);
        final say = tr5(
          lang,
          'Стоит продвигать ${list.length} товаров: высокая наценка и хороший спрос. Первый — $fn, наценка ${_pct(f.markup, lang).replaceFirst('+', '')}.',
          '${list.length} товарды жылдырса болот: үстөгү жогору, суроо-талап жакшы. Биринчиси — $fn, үстөгү ${_pct(f.markup, lang).replaceFirst('+', '')}.',
          '${list.length} ta mahsulotni targ\'ib qilish mumkin: ustamasi yuqori, talab yaxshi. Birinchisi — $fn, ustamasi ${_pct(f.markup, lang).replaceFirst('+', '')}.',
          'Worth promoting ${list.length} products: high markup and good demand. First — $fn, markup ${_pct(f.markup, lang).replaceFirst('+', '')}.',
          '${list.length} ürün öne çıkarılabilir: yüksek kâr oranı ve iyi talep. İlki — $fn, kâr oranı ${_pct(f.markup, lang).replaceFirst('+', '')}.',
        );
        return _show(c, env, t, say, ['promote ${list.length}', 'first $fn']);
    }
    return ToolResult(ToolStatus.clarify, tr5(lang, 'Какую рекомендацию показать: что заказать, что продвигать, неликвид или низкая наценка?', 'Кайсы сунушту көрсөтөйүн: эмнени заказ кылуу, эмнени жылдыруу, сатылбаган же төмөн үстөк?', 'Qaysi tavsiyani ko\'rsatay: nimani buyurtma qilish, nimani targ\'ib qilish, sotilmayotgan yoki past ustama?', 'Which recommendation: what to reorder, what to promote, dead stock or low markup?', 'Hangi öneri: ne sipariş edilmeli, neyi öne çıkarmalı, satılmayanlar veya düşük kâr oranı?'));
  }),
);

// ---------------------------------------------------------------------------------------------------------------------
// Управление таблицей на экране
// ---------------------------------------------------------------------------------------------------------------------

/// Что сказать про первую строку после сортировки: первая колонка (если это не код) и главное число.
String _firstRow(CallTable t) {
  final v = t.visibleIndices;
  if (v.isEmpty) return '';
  final r = v.first;
  final firstCol = t.columns.first;
  final label = firstCol.kind == CellKind.code ? '' : _sp(t.cellText(r, 0, revealNames: false));
  final sk = t.sortKey == null ? -1 : t.columns.indexWhere((x) => x.key == t.sortKey);
  final num = sk > 0 ? _sp(t.cellText(r, sk, revealNames: false)) : '';
  return [if (label.isNotEmpty) _clip(label), if (num.isNotEmpty) num].join(', ');
}

AssistantTool _tableControl(ReadContext c) => AssistantTool(
  decl: ToolDecl(
    'table_control',
    'Control the table on screen. action: sort (column, order asc|desc), filter (column, value like ">1000" or a word; empty value clears), more, pin, unpin, close, bigger, smaller, export_chat, export_pdf, share. '
    'ru: отсортируй по сумме, покажи ещё, только где долг больше 1000, закрепи, крупнее.',
    parameters: sObj({
      'action': sStr(values: ['sort', 'filter', 'more', 'pin', 'unpin', 'close', 'bigger', 'smaller', 'export_chat', 'export_pdf', 'share']),
      'column': sStr(),
      'order': sStr(values: ['asc', 'desc']),
      'value': sStr(),
      'table_id': sStr(),
    }, required: ['action']),
  ),
  group: ToolGroup.table,
  run: (r) async {
    final env = r.env;
    final lang = env.lang;
    final a = '${r.args['action'] ?? ''}';
    final tid = '${r.args['table_id'] ?? ''}'.trim();
    final table = tid.isEmpty ? c.tables.current : c.tables.byId(tid);
    if (table == null) {
      return _none(env, 'Сейчас на экране нет таблицы.', 'Азыр экранда таблица жок.', 'Hozir ekranda jadval yo\'q.', 'There is no table on the screen now.', 'Şu an ekranda tablo yok.');
    }
    ToolResult changed(CallTable n, String say) {
      c.tables.replace(n);
      c.tables.show(n.id);
      final data = _fit({'table': n.modelBrief()});
      return ToolResult(ToolStatus.ok, _sp(say), data: data, tableId: n.id, screen: env.screen.isEmpty ? null : env.screen);
    }

    ToolResult simple(String say) => ToolResult(ToolStatus.ok, say, tableId: table.id, screen: env.screen.isEmpty ? null : env.screen);

    switch (a) {
      case 'sort':
        final col = '${r.args['column'] ?? ''}';
        final order = r.args['order'];
        final n = table.sortedBy(col, desc: order == 'desc' ? true : (order == 'asc' ? false : null));
        if (n == null) {
          return ToolResult(
            ToolStatus.clarify,
            table.card
                ? tr5(lang, 'Карточку нельзя сортировать.', 'Карточканы иргеге болбойт.', 'Kartochkani saralab bo\'lmaydi.', 'A card cannot be sorted.', 'Kart sıralanamaz.')
                : tr5(lang, 'По какой колонке? Есть: ${table.headers.join(', ')}.', 'Кайсы тилке боюнча? Бар: ${table.headers.join(', ')}.', 'Qaysi ustun bo\'yicha? Bor: ${table.headers.join(', ')}.', 'By which column? Available: ${table.headers.join(', ')}.', 'Hangi sütuna göre? Mevcut: ${table.headers.join(', ')}.'),
          );
        }
        final colTitle = n.columns.firstWhere((x) => x.key == n.sortKey).title;
        final first = _firstRow(n);
        return changed(
          n,
          tr5(
            lang,
            'Отсортировала по «$colTitle»${n.desc ? ', сначала большие' : ', по возрастанию'}.${first.isEmpty ? '' : ' Первая — $first.'}',
            '«$colTitle» боюнча иреттедим${n.desc ? ', чоңдору алгач' : ''}.${first.isEmpty ? '' : ' Биринчиси — $first.'}',
            '«$colTitle» bo\'yicha saraladim${n.desc ? ', kattalari birinchi' : ''}.${first.isEmpty ? '' : ' Birinchisi — $first.'}',
            'Sorted by "$colTitle"${n.desc ? ', largest first' : ', ascending'}.${first.isEmpty ? '' : ' First — $first.'}',
            '"$colTitle" sütununa göre sıraladım${n.desc ? ', büyükten küçüğe' : ''}.${first.isEmpty ? '' : ' İlki — $first.'}',
          ),
        );
      case 'filter':
        final value = '${r.args['value'] ?? ''}';
        final n = table.filteredBy('${r.args['column'] ?? ''}', value);
        if (n == null) {
          return ToolResult(ToolStatus.clarify, tr5(lang, 'Не поняла условие. Скажите, например: «только где долг больше 1000».', 'Шартты түшүнгөн жокмун. Мисалы: «карыз 1000 ден көп гана».', 'Shartni tushunmadim. Masalan: «qarz 1000 dan katta».', 'I did not get the condition. Say, for example: "only where debt is over 1000".', 'Koşulu anlamadım. Örneğin: "yalnızca borcu 1000 üzeri".'));
        }
        if (n.filter == null) return changed(n, tr5(lang, 'Фильтр снят, показаны все строки.', 'Чыпка алынды, бардык саптар көрсөтүлдү.', 'Filtr olib tashlandi, barcha qatorlar ko\'rsatildi.', 'Filter cleared, all rows shown.', 'Filtre kaldırıldı, tüm satırlar gösteriliyor.'));
        final left = n.viewCount;
        return changed(
          n,
          tr5(
            lang,
            'Оставила $left из ${n.totalRows} строк: ${n.filter!.describe(n)}.',
            '${n.totalRows} саптан $left калды: ${n.filter!.describe(n)}.',
            '${n.totalRows} qatordan $left tasi qoldi: ${n.filter!.describe(n)}.',
            'Kept $left of ${n.totalRows} rows: ${n.filter!.describe(n)}.',
            '${n.totalRows} satırdan $left kaldı: ${n.filter!.describe(n)}.',
          ),
        );
      case 'more':
        if (!table.hasMore) {
          return simple(tr5(lang, 'Это все строки.', 'Бардык саптар ушул.', 'Hammasi shu.', 'That is all the rows.', 'Tüm satırlar bu kadar.'));
        }
        final n = table.more();
        final shownN = n.visibleIndices.length;
        return changed(
          n,
          tr5(lang, 'Показала ещё: теперь $shownN из ${n.viewCount}.', 'Дагы көрсөттүм: азыр ${n.viewCount} ичинен $shownN.', 'Yana ko\'rsatdim: endi ${n.viewCount} tadan $shownN.', 'Showing more: now $shownN of ${n.viewCount}.', 'Daha fazla gösterdim: şimdi ${n.viewCount} içinden $shownN.'),
        );
      case 'pin':
        c.tables.pin(table.id);
        return simple(tr5(lang, 'Закрепила таблицу.', 'Таблица бекитилди.', 'Jadval mahkamlandi.', 'Table pinned.', 'Tablo sabitlendi.'));
      case 'unpin':
        c.tables.unpin(table.id);
        return simple(tr5(lang, 'Открепила.', 'Бекитүү алынды.', 'Mahkamlash olib tashlandi.', 'Unpinned.', 'Sabitleme kaldırıldı.'));
      case 'close':
        c.tables.close();
        return simple(tr5(lang, 'Закрыла таблицу.', 'Таблица жабылды.', 'Jadval yopildi.', 'Table closed.', 'Tablo kapatıldı.'));
      case 'bigger':
      case 'smaller':
        final scale = c.tables.resize(a == 'bigger');
        return simple(tr5(lang, 'Размер текста $scale.', 'Тексттин өлчөмү $scale.', 'Matn o\'lchami $scale.', 'Text size $scale.', 'Metin boyutu $scale.'));
      case 'export_chat':
      case 'export_pdf':
      case 'share':
        final handler = c.tables.onExport;
        final ok = handler == null ? false : await handler(a, table);
        if (!ok) {
          return ToolResult(ToolStatus.error, tr5(lang, 'Сейчас не получится отправить таблицу.', 'Азыр таблицаны жөнөтүү мүмкүн эмес.', 'Hozir jadvalni yuborib bo\'lmaydi.', 'I cannot send the table right now.', 'Şu an tablo gönderilemiyor.'));
        }
        return simple(switch (a) {
          'export_chat' => tr5(lang, 'Отправила таблицу в чат.', 'Таблица чатка жөнөтүлдү.', 'Jadval chatga yuborildi.', 'Table sent to the chat.', 'Tablo sohbete gönderildi.'),
          'export_pdf' => tr5(lang, 'Сделала PDF.', 'PDF жасалды.', 'PDF tayyor.', 'PDF is ready.', 'PDF hazır.'),
          _ => tr5(lang, 'Открыла «Поделиться».', '«Бөлүшүү» ачылды.', '«Ulashish» ochildi.', 'Share is open.', '«Paylaş» açıldı.'),
        });
    }
    return ToolResult(ToolStatus.clarify, tr5(lang, 'Что сделать с таблицей?', 'Таблица менен эмне кылайын?', 'Jadval bilan nima qilay?', 'What should I do with the table?', 'Tabloyla ne yapayım?'));
  },
);

AssistantTool _showTable(ReadContext c) => AssistantTool(
  decl: ToolDecl(
    'show_table',
    'Show again an earlier table of this call (by table_id, default the latest). ru: покажи ту таблицу ещё раз.',
    parameters: sObj({'table_id': sStr()}),
  ),
  group: ToolGroup.table,
  run: (r) async {
    final env = r.env;
    final lang = env.lang;
    final tid = '${r.args['table_id'] ?? ''}'.trim();
    final t = tid.isEmpty ? (c.tables.history.isEmpty ? null : c.tables.history.last) : c.tables.byId(tid);
    if (t == null || !c.tables.show(t.id)) {
      return _none(env, 'Такой таблицы нет.', 'Андай таблица жок.', 'Bunday jadval yo\'q.', 'No such table.', 'Böyle bir tablo yok.');
    }
    return ToolResult(
      ToolStatus.ok,
      tr5(lang, 'Показала таблицу «${_clip(t.title)}».', '«${_clip(t.title)}» таблицасын көрсөттүм.', '«${_clip(t.title)}» jadvalini ko\'rsatdim.', 'Showing the table "${_clip(t.title)}".', '"${_clip(t.title)}" tablosunu gösterdim.'),
      data: _fit({'table': t.modelBrief()}),
      tableId: t.id,
      screen: env.screen.isEmpty ? null : env.screen,
    );
  },
);
