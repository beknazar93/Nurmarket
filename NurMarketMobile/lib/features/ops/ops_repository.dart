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

  /// Внесение (income, shift_drawer_inflow) или изъятие (expense, shift_drawer_outflow) — деньги ящика смены.
  /// source_id — ключ операции: повтор после обрыва связи сервер узнаёт (касса проверяет ?source_id=).
  Future<void> cashFlow({required String shiftId, required String cashboxId, required bool withdrawal, required double amount, required String reason}) async {
    await api.post(
      'api/construction/cashflows/',
      body: {
        'cashbox': cashboxId,
        'shift': shiftId,
        'type': withdrawal ? 'expense' : 'income',
        'name': '${withdrawal ? 'Изъятие' : 'Внесение'}${reason.isEmpty ? '' : ': $reason'}',
        'amount': amount.toStringAsFixed(2),
        'source_kind': withdrawal ? 'shift_drawer_outflow' : 'shift_drawer_inflow',
        'source_id': newKey(),
        'payment_method': 'cash',
      },
    );
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

  /// Оплата долга одной суммой: сервер гасит от старых долгов к новым; повтор с тем же ключом не проводит деньги дважды.
  Future<Map<String, dynamic>> payDebt({required String clientId, required double amount, required String method, String? shiftId, required String key}) async {
    final resp = await api.post(
      'api/main/clients/$clientId/pay-debt/',
      body: {'amount': amount.toStringAsFixed(2), 'method': method, if (shiftId != null && !shiftId.startsWith('offline-')) 'shift': shiftId},
      headers: {'Idempotency-Key': key},
    );
    return resp is Map<String, dynamic> ? resp : <String, dynamic>{};
  }

  /// Возврат: весь чек (lines пусто) или строки {sale_item_id, quantity} — тот же запрос, что кнопка «Возврат» сайта.
  Future<Map<String, dynamic>> returnSale(String saleId, List<(String itemId, double qty)> lines) async {
    final resp = await api.post(
      'api/main/pos/sales/$saleId/return/',
      body: {
        'idempotency_key': newKey(),
        'cashbox_role': 'pos_main',
        if (lines.isNotEmpty)
          'items': [
            for (final (id, q) in lines) {'sale_item_id': id, 'quantity': q == q.roundToDouble() ? q.toInt().toString() : q.toStringAsFixed(3)},
          ],
      },
    );
    return resp is Map<String, dynamic> ? resp : <String, dynamic>{};
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
            promo: PromoRule.parse(l['pm']),
          ),
      ],
    ),
  );
}
