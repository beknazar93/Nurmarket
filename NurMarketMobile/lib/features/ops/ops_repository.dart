import 'dart:convert';
import 'dart:math';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../sale/cart.dart';

/// 13.10, владелец: «все функции из старой кассы — и в мобилке». Операции кассы, которых не было на телефоне:
/// внесение и изъятие, долги клиентов и их оплата, возврат по чеку, отложенные чеки. Запросы — те же, что у кассы
/// (ShiftCashFlowSync, ClientDebtsApiService, SalesApiService.PosReturnSaleAsync).
class Debtor {
  final String clientId;
  final String name;
  final String phone;
  final double debt;
  final int salesCount;
  const Debtor(this.clientId, this.name, this.phone, this.debt, this.salesCount);
}

class OpsRepository {
  final ApiClient api;
  final AppDatabase db;
  OpsRepository(this.api, this.db);

  static String newKey() {
    final r = Random.secure();
    String hex(int n) => List.generate(n, (_) => r.nextInt(16).toRadixString(16)).join();
    return '${hex(8)}-${hex(4)}-4${hex(3)}-${(8 + r.nextInt(4)).toRadixString(16)}${hex(3)}-${hex(12)}';
  }

  /// 2026-10-22 (мобилка, деньги): ключ операции живёт до успеха — как PendingIdempotencyKey кассы. Раньше source_id и
  /// ключ возврата были новыми при каждом вызове: повтор после обрыва = двойное внесение или двойной возврат. Пока исход
  /// прошлой попытки с той же подписью (касса, сумма, причина…) неизвестен, повтор идёт тем же ключом; хранится в базе
  /// телефона 10 минут (как ReturnSaleDialog.PendingReturn кассы).
  static const _pendingPrefix = 'ops.pending.';
  static const _pendingLife = Duration(minutes: 10);

  /// Последний [pendingKey] вернул прежний ключ (это повтор неудачной попытки).
  bool lastKeyReused = false;

  Future<String> pendingKey(String op, String signature) async {
    final raw = await db.getSetting('$_pendingPrefix$op');
    if (raw != null && raw.isNotEmpty) {
      try {
        final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
        final at = DateTime.tryParse(m['at'] as String? ?? '');
        if (m['sig'] == signature && m['key'] is String && at != null && DateTime.now().difference(at) < _pendingLife) {
          lastKeyReused = true;
          return m['key'] as String;
        }
      } catch (_) {
        // новый ключ
      }
    }
    lastKeyReused = false;
    final key = newKey();
    await db.setSetting('$_pendingPrefix$op', jsonEncode({'key': key, 'sig': signature, 'at': DateTime.now().toIso8601String()}));
    return key;
  }

  /// Операция прошла (или сервер отказал по существу) — следующая будет новой.
  Future<void> forgetKey(String op) => db.setSetting('$_pendingPrefix$op', '');

  /// Исход неизвестен (обрыв, тайм-аут, 5xx) — ключ оставить для повтора.
  static bool outcomeUnknown(Object e) => e is! ApiException || e.status == null || e.status == 408 || e.status == 429 || e.status! >= 500;

  Future<T> _withKey<T>(String op, String signature, Future<T> Function(String key) run) async {
    final key = await pendingKey(op, signature);
    try {
      final r = await run(key);
      await forgetKey(op);
      return r;
    } catch (e) {
      if (!outcomeUnknown(e)) await forgetKey(op);
      rethrow;
    }
  }

  /// Внесение (income, shift_drawer_inflow) или изъятие (expense, shift_drawer_outflow) — деньги ящика смены.
  /// source_id — ключ операции: перед повтором проверяем ?source_id= (как касса) — запись уже есть, второй раз не шлём.
  Future<void> cashFlow({required String shiftId, required String cashboxId, required bool withdrawal, required double amount, required String reason}) =>
      _withKey('cashflow', '$shiftId|$withdrawal|${amount.toStringAsFixed(2)}|$reason', (key) async {
        if (lastKeyReused && await _cashFlowExists(key)) return;
        await api.post(
          'api/construction/cashflows/',
          body: {
            'cashbox': cashboxId,
            'shift': shiftId,
            'type': withdrawal ? 'expense' : 'income',
            'name': '${withdrawal ? 'Изъятие' : 'Внесение'}${reason.isEmpty ? '' : ': $reason'}',
            'amount': amount.toStringAsFixed(2),
            'source_kind': withdrawal ? 'shift_drawer_outflow' : 'shift_drawer_inflow',
            'source_id': key,
            'payment_method': 'cash',
          },
        );
      });

  Future<bool> _cashFlowExists(String sourceId) async {
    try {
      final rows = asList(await api.get('api/construction/cashflows/', query: {'source_id': sourceId}));
      return rows.any((r) => str(r, 'source_id') == sourceId);
    } on ApiException catch (e) {
      if (e.status == null) rethrow; // нет связи — и запись не отправится
      return false;
    }
  }

  /// Журнал денег смены (Т25): GET api/construction/cashflows/?shift= — внесения и изъятия этой смены.
  Future<List<(DateTime? at, String name, double amount, bool outflow)>> shiftCashFlows(String shiftId) async {
    final data = {
      'results': await getAllPages(api, 'api/construction/cashflows/', query: {'shift': shiftId}, pageSize: 200, maxPages: 10),
    };
    final out = <(DateTime?, String, double, bool)>[];
    for (final m in asList(data)) {
      final kind = (str(m, 'source_kind') ?? '').toLowerCase();
      if (!kind.startsWith('shift_drawer')) continue; // только внесения/изъятия, без продаж
      final type = (str(m, 'type') ?? '').toLowerCase();
      out.add((
        DateTime.tryParse(str(m, 'created_at') ?? '')?.toLocal(),
        str(m, 'name') ?? '',
        num0(m, 'amount'),
        type == 'expense' || kind.endsWith('outflow'),
      ));
    }
    out.sort((a, b) => (b.$1 ?? DateTime(2000)).compareTo(a.$1 ?? DateTime(2000)));
    return out;
  }

  /// Должники: GET api/main/clients/debtors/ постранично.
  Future<List<Debtor>> debtors() async {
    final out = <Debtor>[];
    for (var page = 1; page <= 20; page++) {
      final data = await api.get('api/main/clients/debtors/', query: {'min_amount': '0', if (page > 1) 'page': '$page'});
      for (final row in asList(data)) {
        final id = str(row, 'client') ?? str(row, 'id');
        if (id == null) continue;
        out.add(
          Debtor(
            id,
            str(row, 'full_name') ?? str(row, 'client_name') ?? '',
            str(row, 'phone') ?? '',
            num0(row, 'debt_total'),
            num0(row, 'sales_count').round(),
          ),
        );
      }
      if (data is! Map || data['next'] is! String) break;
    }
    out.sort((a, b) => b.debt.compareTo(a.debt));
    return out;
  }

  /// 2026-10-26 (живой аудит, П5): последний загруженный список должников — показывается с «Повторить», когда связи нет.
  /// Хранится в настройках телефона (не в схеме базы); [owner] — чужой список другому аккаунту не показываем.
  static const _debtorsCacheKey = 'debtors.cache';

  Future<void> cacheDebtors(String owner, List<Debtor> rows) => db.setSetting(
    _debtorsCacheKey,
    jsonEncode({
      'owner': owner,
      'rows': [
        for (final d in rows) {'id': d.clientId, 'n': d.name, 'p': d.phone, 'd': d.debt, 'c': d.salesCount},
      ],
    }),
  );

  Future<List<Debtor>?> cachedDebtors(String owner) async {
    final raw = await db.getSetting(_debtorsCacheKey);
    if (raw == null || raw.isEmpty) return null;
    try {
      final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
      if (m['owner'] != owner) return null;
      return [
        for (final r in (m['rows'] as List).cast<Map<String, dynamic>>())
          Debtor(r['id'] as String, r['n'] as String? ?? '', r['p'] as String? ?? '', (r['d'] as num?)?.toDouble() ?? 0, (r['c'] as num?)?.toInt() ?? 0),
      ];
    } catch (_) {
      return null;
    }
  }

  /// Оплата долга одной суммой: сервер гасит от старых долгов к новым; повтор с тем же ключом не проводит деньги дважды.
  /// 2026-10-22 (мобилка, деньги): ключ живёт до успеха (раньше — только пока открыт диалог: повтор после ошибки = новый).
  Future<Map<String, dynamic>> payDebt({required String clientId, required double amount, required String method, String? shiftId}) =>
      _withKey('paydebt', '$clientId|${amount.toStringAsFixed(2)}|$method', (key) async {
        final resp = await api.post(
          'api/main/clients/$clientId/pay-debt/',
          body: {'amount': amount.toStringAsFixed(2), 'method': method, if (shiftId != null && !shiftId.startsWith('offline-')) 'shift': shiftId},
          headers: {'Idempotency-Key': key},
        );
        return resp is Map<String, dynamic> ? resp : <String, dynamic>{};
      });

  /// Возврат: весь чек (lines пусто) или строки {sale_item_id, quantity} — тот же запрос, что кнопка «Возврат» сайта.
  /// 2026-10-22 (мобилка, деньги): ключ повтора живёт до успеха (раньше новый при каждой попытке — двойной возврат).
  Future<Map<String, dynamic>> returnSale(String saleId, List<(String itemId, double qty)> lines) {
    String q(double v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(3);
    final sig = '$saleId|${lines.map((l) => '${l.$1}:${q(l.$2)}').join(',')}';
    return _withKey('return', sig, (key) async {
      final resp = await api.post(
        'api/main/pos/sales/$saleId/return/',
        body: {
          'idempotency_key': key,
          'cashbox_role': 'pos_main',
          if (lines.isNotEmpty)
            'items': [
              for (final (id, v) in lines) {'sale_item_id': id, 'quantity': q(v)},
            ],
        },
      );
      return resp is Map<String, dynamic> ? resp : <String, dynamic>{};
    });
  }

  /// 2026-10-22 (мобилка, деньги): текущий остаток долга чека «в долг» с сервера (remaining_debt сделки, как
  /// ReturnSaleDialog кассы) — возврат не уменьшает долг, которого уже нет (клиент мог погасить). null — не получен.
  Future<double?> saleDebtLeft(Map<String, dynamic> sale) async {
    double? own;
    for (final k in ['debt_remaining', 'remaining_debt', 'debt_amount']) {
      if (sale[k] != null) {
        own = num0(sale, k);
        break;
      }
    }
    final dealId = str(sale, 'deal_id');
    final clientId = str(sale, 'client');
    if (dealId == null) return own == null ? null : (own < 0 ? 0 : own);
    if (clientId == null) return null;
    try {
      final deal = await api.get('api/main/clients/$clientId/deals/$dealId/', timeout: const Duration(seconds: 8));
      if (deal is Map && deal['remaining_debt'] != null) {
        final v = num0(deal.cast<String, dynamic>(), 'remaining_debt');
        return v < 0 ? 0 : v;
      }
    } catch (_) {
      // остаток неизвестен — кассиру предупреждение
    }
    return null;
  }

  /// 2026-10-22 (мобилка, деньги): что записал сервер по последнему возврату чека — сумма документа (api/main/pos/returns/
  /// ?sale=) и выданные деньги (cashflows source_kind=pos_sale_return), как ServerReturnFiguresAsync кассы.
  Future<({double? doc, String? docId, double? cashOut})> serverReturnFigures(String saleId, Set<String> seenDocs) async {
    try {
      final rows = asList(await api.get('api/main/pos/returns/', query: {'sale': saleId}, timeout: const Duration(seconds: 8)));
      final mine = rows.where((r) => str(r, 'sale') == saleId && num0(r, 'amount') > 0 && !seenDocs.contains(str(r, 'id'))).toList()
        ..sort((a, b) => (str(b, 'created_at') ?? '').compareTo(str(a, 'created_at') ?? ''));
      if (mine.isEmpty) return (doc: null, docId: null, cashOut: null);
      final newest = mine.first;
      final at = DateTime.tryParse(str(newest, 'created_at') ?? '');
      double? cashOut;
      if (at != null) {
        final flows = asList(
          await api.get('api/construction/cashflows/', query: {'source_kind': 'pos_sale_return', 'source_id': saleId}, timeout: const Duration(seconds: 8)),
        );
        for (final f in flows) {
          final fAt = DateTime.tryParse(str(f, 'created_at') ?? '');
          if (str(f, 'source_id') != saleId || str(f, 'source_kind') != 'pos_sale_return' || fAt == null) continue;
          if (fAt.difference(at).inSeconds.abs() > 15) continue;
          cashOut = (cashOut ?? 0) + num0(f, 'amount');
        }
      }
      return (doc: num0(newest, 'amount'), docId: str(newest, 'id'), cashOut: cashOut);
    } catch (_) {
      return (doc: null, docId: null, cashOut: null);
    }
  }

  /// 2026-10-22 (мобилка, деньги): продажи смены — фильтр на сервере (?shift=) и страницы («Ещё»), а не первая страница
  /// продаж всей компании.
  Future<(List<Map<String, dynamic>>, bool)> shiftSales(String? shiftId, {int page = 1}) async {
    final data = await api.get('api/main/pos/sales/', query: {'page': page, if (shiftId != null && !shiftId.startsWith('offline-')) 'shift': shiftId});
    final rows = asList(data).where((m) => shiftId == null || str(m, 'shift') == null || str(m, 'shift') == shiftId).toList();
    final more = data is Map && data['next'] != null;
    return (rows, more);
  }

  // ------------------------------------------------------------------ отложенные чеки (на телефоне)

  static const _heldKey = 'held.carts';

  Future<List<HeldCart>> held() async {
    final raw = await db.getSetting(_heldKey);
    if (raw == null || raw.isEmpty) return [];
    try {
      return [for (final m in (jsonDecode(raw) as List).cast<Map<String, dynamic>>()) HeldCart.fromJson(m)];
    } catch (_) {
      return [];
    }
  }

  Future<void> hold(Cart cart, String note) async {
    final list = await held();
    list.insert(0, HeldCart(id: newKey(), at: DateTime.now(), note: note, cart: cart));
    await db.setSetting(_heldKey, jsonEncode([for (final h in list) h.toJson()]));
  }

  Future<void> removeHeld(String id) async {
    final list = (await held())..removeWhere((h) => h.id == id);
    await db.setSetting(_heldKey, jsonEncode([for (final h in list) h.toJson()]));
  }
}

class HeldCart {
  final String id;
  final DateTime at;
  final String note;
  final Cart cart;
  const HeldCart({required this.id, required this.at, required this.note, required this.cart});

  Map<String, dynamic> toJson() => {
    'id': id,
    'at': at.toIso8601String(),
    'note': note,
    'pct': cart.orderDiscountPercent,
    'sum': cart.orderDiscountSum,
    'lines': [
      for (final l in cart.lines)
        {
          'p': l.productId,
          'n': l.name,
          'pr': l.price,
          'q': l.qty,
          'w': l.isWeight,
          'u': l.unit,
          'c': l.custom,
          'd': l.discount,
          'rp': l.retailPrice,
          'wp': l.wholesalePrice,
          'ws': l.wholesale,
          'v': l.variantId,
          // 2026-10-27 (М4): штука из упаковки — id упаковки, штук в упаковке, название (черновик чека и отложенные не теряют их)
          if (l.salePackageId != null) 'sp': l.salePackageId,
          if (l.perPack > 1) 'pp': l.perPack,
          if (l.packName != null) 'pn': l.packName,
          if (l.hasPromo)
            'pm': [
              for (final r in l.promo) {'position': r.position, 'min_amount': r.minAmount, 'discount_percent': r.percent, if (r.quantity != null) 'promo_quantity': r.quantity},
            ],
        },
    ],
  };

  static HeldCart fromJson(Map<String, dynamic> m) => HeldCart(
    id: m['id'] as String,
    at: DateTime.tryParse(m['at'] as String? ?? '') ?? DateTime.now(),
    note: m['note'] as String? ?? '',
    cart: Cart(
      orderDiscountPercent: (m['pct'] as num?)?.toDouble() ?? 0,
      orderDiscountSum: (m['sum'] as num?)?.toDouble() ?? 0,
      lines: [
        for (final l in (m['lines'] as List).cast<Map<String, dynamic>>())
          CartLine(
            productId: l['p'] as String,
            name: l['n'] as String,
            price: (l['pr'] as num).toDouble(),
            qty: (l['q'] as num).toDouble(),
            isWeight: l['w'] as bool? ?? false,
            unit: l['u'] as String? ?? 'шт',
            custom: l['c'] as bool? ?? false,
            discount: (l['d'] as num?)?.toDouble() ?? 0,
            retailPrice: (l['rp'] as num?)?.toDouble(),
            wholesalePrice: (l['wp'] as num?)?.toDouble() ?? 0,
            wholesale: l['ws'] as bool? ?? false,
            variantId: l['v'] as String?,
            salePackageId: l['sp'] as String?,
            perPack: (l['pp'] as num?)?.toDouble() ?? 1,
            packName: l['pn'] as String?,
            promo: PromoRule.parse(l['pm']),
          ),
      ],
    ),
  );
}
