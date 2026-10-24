import 'dart:convert';
import 'dart:math' as math;

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';

/// 13.10, владелец: «после десктопа продолжи мобилку» — этап 2, администратор на телефоне (макеты Т8, Т11–Т14, Т21–Т22,
/// Т26, СЗ5, Л5, У3). Запросы — те же, что у кассы и программы владельца: продажи, смены, товары, заказы витрины, бонусы.
class TodayStats {
  final double revenue;
  final int checks;
  const TodayStats(this.revenue, this.checks);
}

class ShiftRow {
  final String id;
  final String cashier;
  final String cashbox;
  final DateTime? openedAt;
  final DateTime? closedAt;
  final double sales;
  final double cash;
  final int checks;
  const ShiftRow(this.id, this.cashier, this.cashbox, this.openedAt, this.closedAt, this.sales, this.cash, this.checks);
  bool get isOpen => closedAt == null;
}

class SiteOrderItem {
  final String productId;
  final String name;
  final double qty;
  final double price;
  const SiteOrderItem(this.productId, this.name, this.qty, this.price);
}

class SiteOrder {
  final String id;
  final String number;
  final String customer;
  final String phone;
  final String status;
  final double total;
  final DateTime? createdAt;
  final bool stockReserved;
  final List<SiteOrderItem> items;
  const SiteOrder(this.id, this.number, this.customer, this.phone, this.status, this.total, this.createdAt, this.stockReserved, this.items);
  bool get finished => status == 'done' || status == 'canceled' || status == 'completed';
}

class ClientRow {
  final String id;
  final String name;
  final String phone;
  final double debt;
  final double bonus;
  const ClientRow(this.id, this.name, this.phone, this.debt, this.bonus);
}

/// Удаление из чека на телефоне (доска У3): что, сколько, почему — журнал на телефоне.
class DeletionRow {
  final DateTime at;
  final String name;
  final double qty;
  final double sum;
  final String reason;
  final String cashier;
  const DeletionRow(this.at, this.name, this.qty, this.sum, this.reason, this.cashier);

  Map<String, dynamic> toJson() => {'at': at.toIso8601String(), 'n': name, 'q': qty, 's': sum, 'r': reason, 'c': cashier};
  static DeletionRow fromJson(Map<String, dynamic> m) => DeletionRow(
    DateTime.tryParse(m['at'] as String? ?? '') ?? DateTime.now(),
    m['n'] as String? ?? '',
    (m['q'] as num?)?.toDouble() ?? 0,
    (m['s'] as num?)?.toDouble() ?? 0,
    m['r'] as String? ?? '',
    m['c'] as String? ?? '',
  );
}

/// 2026-10-22 (мобилка, админ, П11): сборка заказа — скан товара [productId] отмечает первую недобранную строку.
/// [qty] — штук упаковки или вес весовой этикетки (null — 1). Больше заказанного не отмечается.
/// Возврат: (индекс строки, новое собранное количество) или null — товара нет в заказе или он уже собран.
(int, double)? pickScanned(List<SiteOrderItem> items, Map<int, double> picked, String productId, double? qty) {
  for (var i = 0; i < items.length; i++) {
    final have = picked[i] ?? 0;
    if (items[i].productId != productId || have >= items[i].qty - 1e-9) continue;
    final next = math.min(items[i].qty, have + (qty != null && qty > 0 ? qty : 1));
    return (i, double.parse(next.toStringAsFixed(3)));
  }
  return null;
}

/// 2026-10-22 (мобилка, админ, П11): сколько не хватает до полного заказа (0 — собран целиком).
double pickShortage(List<SiteOrderItem> items, Map<int, double> picked) {
  var short = 0.0;
  for (var i = 0; i < items.length; i++) {
    short += math.max(0, items[i].qty - (picked[i] ?? 0));
  }
  return double.parse(short.toStringAsFixed(3));
}

class AdminRepository {
  final ApiClient api;
  final AppDatabase db;
  AdminRepository(this.api, this.db);

  static String _day(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  /// 2026-10-22 (мобилка, админ, П11): выручка и чеки дня — с сервера, тем же отчётом, что «Сводка» и программа
  /// владельца (GET api/main/analytics/market/?tab=sales за сегодня). Раньше телефон складывал 10 страниц продаж сам —
  /// число не совпадало со «Сводкой» (и терялись продажи дальше 10-й страницы). Старый сервер без отчёта — по-старому.
  Future<TodayStats> today() async {
    final d = _day(DateTime.now());
    try {
      final r = await api.get(
        'api/main/analytics/market/',
        query: {'tab': 'sales', 'period_start': d, 'period_end': d},
        timeout: const Duration(seconds: 20),
      );
      final cards = r is Map ? r['cards'] : null;
      if (cards is Map && cards['revenue'] != null) {
        final c = cards.cast<String, dynamic>();
        return TodayStats(num0(c, 'revenue'), num0(c, 'transactions').round());
      }
    } on ApiException catch (e) {
      if (e.isTransient) rethrow;
      // 400/403/404 — отчёта нет: считаем по продажам, как раньше
    }
    return _todayFromSales();
  }

  /// Выручка и чеки за сегодня: продажи сервера по страницам, пока не дошли до вчерашних (запасной путь).
  Future<TodayStats> _todayFromSales() async {
    final start = DateTime.now();
    final day = DateTime(start.year, start.month, start.day);
    var revenue = 0.0;
    var checks = 0;
    for (var page = 1; page <= 10; page++) {
      final data = await api.get('api/main/pos/sales/', query: {'page': page});
      final rows = asList(data);
      var older = false;
      for (final m in rows) {
        final at = DateTime.tryParse(str(m, 'created_at') ?? '')?.toLocal();
        if (at == null) continue;
        if (at.isBefore(day)) {
          older = true;
          continue;
        }
        final status = (str(m, 'status') ?? '').toLowerCase();
        if (status.contains('cancel') || status.contains('return') || status.contains('refund')) continue;
        revenue += num0(m, 'total');
        checks++;
      }
      if (older || rows.isEmpty || data is! Map || data['next'] is! String) break;
    }
    return TodayStats(revenue, checks);
  }

  /// Смены (api/construction/shifts/) — свежие сверху.
  Future<List<ShiftRow>> shifts() async {
    final rows = asList(await api.get('api/construction/shifts/', query: {'page': 1, 'page_size': 100}));
    final out = [
      for (final m in rows)
        ShiftRow(
          str(m, 'id') ?? '',
          str(m, 'cashier_display') ?? str(m, 'cashier_name') ?? str(m, 'user_display') ?? '',
          str(m, 'cashbox_name') ?? str(m, 'cashbox_display') ?? '',
          DateTime.tryParse(str(m, 'opened_at') ?? '')?.toLocal(),
          DateTime.tryParse(str(m, 'closed_at') ?? '')?.toLocal(),
          _first(m, ['sales_total', 'total_sales']),
          _first(m, ['expected_cash', 'current_cash', 'cash_balance', 'closing_cash']),
          num0(m, 'sales_count').round(),
        ),
    ];
    out.sort((a, b) => (b.openedAt ?? DateTime(2000)).compareTo(a.openedAt ?? DateTime(2000)));
    return out;
  }

  /// 14.10, владелец: подробности смены — GET shifts/{id}/report/ (как окно смены кассы, ServerShiftReport);
  /// старый сервер без отчёта — сама смена shifts/{id}/.
  Future<Map<String, dynamic>> shiftReport(String id) async {
    try {
      final r = await api.get('api/construction/shifts/$id/report/');
      if (r is Map && r['sales_total'] != null) return r.cast<String, dynamic>();
    } on ApiException catch (e) {
      if (e.status != 404) rethrow;
    }
    return ((await api.get('api/construction/shifts/$id/')) as Map).cast<String, dynamic>();
  }

  static double _first(Map<String, dynamic> m, List<String> keys) {
    for (final k in keys) {
      if (m[k] != null) return num0(m, k);
    }
    return 0;
  }

  /// Цена продажи и закупки товара — PATCH api/main/products/{id}/ (как карточка товара на кассе).
  Future<void> setPrices(String productId, {double? price, double? purchase, double? wholesale, double? quantity, String? category, String? brand}) async {
    await api.patch(
      'api/main/products/$productId/',
      body: {
        if (price != null) 'price': price.toStringAsFixed(2),
        if (purchase != null) 'purchase_price': purchase.toStringAsFixed(2),
        // 13.10, владелец: «не хватает оптовой цены» — wholesale_price, как в карточке товара кассы.
        if (wholesale != null) 'wholesale_price': wholesale.toStringAsFixed(2),
        // 14.10, владелец: остаток, категория и бренд — поля как у кассы (BuildProductBody / TrySetProductStockAsync).
        if (quantity != null) 'quantity': quantity.toStringAsFixed(3),
        if (category != null) 'category_name': category.trim(),
        if (brand != null) 'brand_name': brand.trim(),
      },
    );
  }

  Future<Map<String, dynamic>> product(String id) async => ((await api.get('api/main/products/$id/')) as Map).cast<String, dynamic>();

  /// Заказы витрины и бота (api/main/showcase/orders/).
  Future<List<SiteOrder>> orders() async {
    final data = {'results': await getAllPages(api, 'api/main/showcase/orders/', pageSize: 100, maxPages: 5)};
    return [for (final m in asList(data)) _order(m)];
  }

  Future<SiteOrder> setOrderStatus(String id, String status) async {
    final data = await api.patch('api/main/showcase/orders/$id/', body: {'status': status});
    if (data is Map && data['id'] != null) return _order(data.cast<String, dynamic>());
    return _order(((await api.get('api/main/showcase/orders/$id/')) as Map).cast<String, dynamic>());
  }

  SiteOrder _order(Map<String, dynamic> m) {
    final items = <SiteOrderItem>[];
    final raw = m['items'];
    if (raw is List) {
      for (final it in raw.whereType<Map>()) {
        final i = it.cast<String, dynamic>();
        final product = i['product'];
        final pid = product is Map ? (product['id']?.toString() ?? '') : (str(i, 'product') ?? '');
        final qty = num0(i, 'quantity') == 0 ? num0(i, 'qty') : num0(i, 'quantity');
        items.add(SiteOrderItem(pid, str(i, 'product_name') ?? str(i, 'name') ?? str(i, 'title') ?? '', qty, num0(i, 'price')));
      }
    }
    final total = num0(m, 'total');
    return SiteOrder(
      str(m, 'id') ?? '',
      str(m, 'order_number') ?? str(m, 'number') ?? '',
      str(m, 'customer_name') ?? '',
      str(m, 'phone') ?? str(m, 'customer_phone') ?? '',
      (str(m, 'status') ?? 'new').toLowerCase(),
      total == 0 ? items.fold(0.0, (s, i) => s + i.qty * i.price) : total,
      DateTime.tryParse(str(m, 'created_at') ?? '')?.toLocal(),
      m['stock_reserved'] == true,
      items,
    );
  }

  /// Клиенты (поиск) с долгом и бонусами.
  Future<List<ClientRow>> clients(String search) async {
    final rows = await getAllPages(
      api,
      'api/main/clients/',
      query: {if (search.trim().isNotEmpty) 'search': search.trim()},
      maxPages: search.trim().isEmpty ? 30 : 2,
    );
    return _clientRows(rows);
  }

  /// 2026-10-22 (мобилка, админ, П11): клиенты постранично — экран открывается с первой страницы (50), «Ещё» догружает.
  /// Раньше при открытии грузилось до 6000 клиентов подряд. more — у сервера есть следующая страница.
  static const clientsPageSize = 50;
  Future<({List<ClientRow> rows, bool more})> clientsPage(String search, int page) async {
    final data = await api.get(
      'api/main/clients/',
      query: {if (search.trim().isNotEmpty) 'search': search.trim(), 'page': page, 'page_size': clientsPageSize},
    );
    final items = asList(data);
    final more = data is Map ? data['next'] != null : items.length >= clientsPageSize;
    return (rows: _clientRows(items), more: more && items.isNotEmpty);
  }

  List<ClientRow> _clientRows(List<Map<String, dynamic>> rows) {
    return [
      for (final m in rows.where((m) => !const {'supplier', 'suppliers'}.contains(str(m, 'type'))))
        ClientRow(
          str(m, 'id') ?? '',
          str(m, 'full_name') ?? str(m, 'name') ?? '',
          str(m, 'phone') ?? '',
          num0(m, 'debt_total') == 0 ? num0(m, 'debt') : num0(m, 'debt_total'),
          num0(m, 'bonus_balance'),
        ),
    ];
  }

  /// 13.10: удалить товар — DELETE products/{id}/ (запасной адрес — products/list/{id}/), как DeleteProductAsync кассы.
  Future<void> deleteProduct(String id) async {
    ApiException? last;
    for (final path in ['api/main/products/$id/', 'api/main/products/list/$id/']) {
      try {
        await api.delete(path);
        await db.deleteProduct(id);
        return;
      } on ApiException catch (e) {
        if (e.status != 404 && e.status != 405) rethrow;
        last = e;
      }
    }
    throw last ?? ApiException('Товар не удалён');
  }

  /// Ручное начисление бонусов (POST api/main/clients/{id}/bonus/ {delta, reason: manual}) — тот же запрос, что у кассы.
  Future<void> addBonus(String clientId, double delta, String note) async {
    await api.post('api/main/clients/$clientId/bonus/', body: {'delta': delta, 'reason': 'manual', 'note': note});
  }

  /// 2026-10-22 (мобилка, админ, П11): код выдачи заказа — один на заказ и хранится на телефоне. Раньше при каждом
  /// «Заказ собран» придумывался новый и нигде не сохранялся: покупателю приходил один код, на выдаче проверить было нечем.
  Future<String> pickupCode(String orderId) async {
    final key = 'pick.code.$orderId';
    final saved = await db.getSetting(key);
    if (saved != null && saved.isNotEmpty) return saved;
    final code = (1000 + math.Random.secure().nextInt(9000)).toString();
    await db.setSetting(key, code);
    return code;
  }

  /// Сохранённый код выдачи или null (заказ ещё не собирали на этом телефоне).
  Future<String?> savedPickupCode(String orderId) async {
    final v = await db.getSetting('pick.code.$orderId');
    return v == null || v.isEmpty ? null : v;
  }

  // ------------------------------------------------------------------ журнал удалений (на телефоне)

  static const _delKey = 'deletions.log';

  Future<List<DeletionRow>> deletions() async {
    final raw = await db.getSetting(_delKey);
    if (raw == null || raw.isEmpty) return [];
    try {
      return [for (final m in (jsonDecode(raw) as List).cast<Map<String, dynamic>>()) DeletionRow.fromJson(m)];
    } catch (_) {
      return [];
    }
  }

  Future<void> logDeletion(DeletionRow row) async {
    final list = await deletions();
    list.insert(0, row);
    if (list.length > 1000) list.removeRange(1000, list.length);
    await db.setSetting(_delKey, jsonEncode([for (final r in list) r.toJson()]));
  }
}
