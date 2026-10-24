import 'dart:convert';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../sale/cart.dart' show roundMoney;

/// 2026-10-22 (мобилка, деньги, К-3): журнал возвратов по чекам — как SaleReturnLedger кассы (эталон ПК).
/// Сервер NurCRM считает возврат по строкам без доли скидки на чек (чек 1000, скидка 10 % → «вернуть» 1000 вместо 900)
/// и после первого частичного возврата убирает скидку из продажи. Телефон запоминает продажу, как её заплатили
/// (при оплате — деньги, при первом открытии в «Возврате» — строки), и каждый свой возврат:
///   • сумма строки — её доля с учётом скидки на чек (Factor = Итог / Сумма строк);
///   • все возвраты чека вместе — не больше оплаченного (Remaining);
///   • продажа «в долг» — сначала уменьшается долг, выдаются только внесённые деньги.
/// Хранится в базе телефона (Settings `returns.ledger`), 45 дней, не больше 1500 продаж.
class LedgerLine {
  final String lineId;
  final String name;
  final double qty;
  final double total; // после скидки строки, до скидки на чек
  const LedgerLine(this.lineId, this.name, this.qty, this.total);

  Map<String, dynamic> toJson() => {'id': lineId, 'n': name, 'q': qty, 't': total};
  static LedgerLine fromJson(Map<String, dynamic> m) =>
      LedgerLine(m['id'] as String? ?? '', m['n'] as String? ?? '', (m['q'] as num?)?.toDouble() ?? 0, (m['t'] as num?)?.toDouble() ?? 0);
}

class LedgerRefund {
  final DateTime at;
  final double amount;
  final double cash;
  final double nonCash;
  final double debtReduced;
  final double? serverAmount;
  final String? serverDocId;
  const LedgerRefund({required this.at, required this.amount, this.cash = 0, this.nonCash = 0, this.debtReduced = 0, this.serverAmount, this.serverDocId});

  Map<String, dynamic> toJson() => {
    'at': at.toIso8601String(),
    'a': amount,
    'c': cash,
    'nc': nonCash,
    'd': debtReduced,
    if (serverAmount != null) 'sa': serverAmount,
    if (serverDocId != null) 'sd': serverDocId,
  };
  static LedgerRefund fromJson(Map<String, dynamic> m) => LedgerRefund(
    at: DateTime.tryParse(m['at'] as String? ?? '') ?? DateTime.now(),
    amount: (m['a'] as num?)?.toDouble() ?? 0,
    cash: (m['c'] as num?)?.toDouble() ?? 0,
    nonCash: (m['nc'] as num?)?.toDouble() ?? 0,
    debtReduced: (m['d'] as num?)?.toDouble() ?? 0,
    serverAmount: (m['sa'] as num?)?.toDouble(),
    serverDocId: m['sd'] as String?,
  );
}

class LedgerEntry {
  final String saleId;
  DateTime recordedAt;
  String method; // cash / transfer / debt / mixed
  double total; // к оплате со всеми скидками — потолок всех возвратов
  double linesTotal; // сумма строк до скидки на чек (0 — неизвестна)
  double paidCash;
  double paidNonCash;
  List<LedgerLine> lines;
  final List<LedgerRefund> refunds;

  LedgerEntry({
    required this.saleId,
    DateTime? recordedAt,
    this.method = '',
    this.total = 0,
    this.linesTotal = 0,
    this.paidCash = 0,
    this.paidNonCash = 0,
    List<LedgerLine>? lines,
    List<LedgerRefund>? refunds,
  }) : recordedAt = recordedAt ?? DateTime.now(),
       lines = lines ?? [],
       refunds = refunds ?? [];

  double get refunded => refunds.fold(0.0, (s, r) => s + r.amount);
  double get remaining => _max0(roundMoney(total - refunded));

  /// Доля скидки на весь чек: стоимость строки × factor = сколько за неё заплатили.
  double get factor => linesTotal > 0.005 && total > 0 ? (total / linesTotal).clamp(0, 1).toDouble() : 1;
  double get debt => _max0(roundMoney(total - paidCash - paidNonCash));
  double get debtLeft => _max0(roundMoney(debt - refunds.fold(0.0, (s, r) => s + r.debtReduced)));
  double get cashLeft => _max0(roundMoney(paidCash - refunds.fold(0.0, (s, r) => s + r.cash)));
  double get nonCashLeft => _max0(roundMoney(paidNonCash - refunds.fold(0.0, (s, r) => s + r.nonCash)));

  Map<String, dynamic> toJson() => {
    'id': saleId,
    'at': recordedAt.toIso8601String(),
    'm': method,
    't': total,
    'lt': linesTotal,
    'pc': paidCash,
    'pn': paidNonCash,
    'l': [for (final l in lines) l.toJson()],
    'r': [for (final r in refunds) r.toJson()],
  };

  static LedgerEntry fromJson(Map<String, dynamic> m) => LedgerEntry(
    saleId: m['id'] as String? ?? '',
    recordedAt: DateTime.tryParse(m['at'] as String? ?? ''),
    method: m['m'] as String? ?? '',
    total: (m['t'] as num?)?.toDouble() ?? 0,
    linesTotal: (m['lt'] as num?)?.toDouble() ?? 0,
    paidCash: (m['pc'] as num?)?.toDouble() ?? 0,
    paidNonCash: (m['pn'] as num?)?.toDouble() ?? 0,
    lines: [for (final l in (m['l'] as List? ?? const [])) LedgerLine.fromJson((l as Map).cast<String, dynamic>())],
    refunds: [for (final r in (m['r'] as List? ?? const [])) LedgerRefund.fromJson((r as Map).cast<String, dynamic>())],
  );
}

double _max0(double v) => v < 0 ? 0 : v;

/// Чем вернуть [amount] — как ComputeRefundSplit кассы (ReturnSaleDialog). Продажа «в долг»: сначала уменьшается долг
/// (не больше остатка долга с сервера [serverDebtLeft] и по журналу), выдаётся только внесённое; [cashOut] — сколько
/// сервер сам выдал по движениям денег (тогда его цифра). Чистая функция — проверяется тестами.
class RefundSplit {
  final double cash;
  final double nonCash;
  final double debtReduced;

  /// false — как заплатили, телефон не знает (чек до возвратов не видел, смешанная): показывается одна сумма.
  final bool splitKnown;

  /// Продажа «в долг», а остаток долга с сервера не получен — кассиру предупреждение проверить долг.
  final bool debtUnknown;
  const RefundSplit(this.cash, this.nonCash, this.debtReduced, {this.splitKnown = true, this.debtUnknown = false});
  double get money => roundMoney(cash + nonCash);
}

RefundSplit computeRefundSplit(LedgerEntry? ledger, String? fallbackMethod, double amount, {double? cashOut, double? serverDebtLeft}) {
  var splitKnown = true;
  var debtUnknown = false;
  final method = ((ledger?.method.isNotEmpty ?? false) ? ledger!.method : (fallbackMethod ?? '')).trim().toLowerCase();
  final isDebtSale = (ledger != null && ledger.debt > 0.005) || method == 'debt';
  if (isDebtSale) {
    double money;
    if (cashOut != null) {
      money = amount < _max0(cashOut) ? amount : _max0(cashOut);
    } else if (serverDebtLeft != null) {
      final debtLeft = ledger != null ? (_max0(serverDebtLeft) < ledger.debtLeft ? _max0(serverDebtLeft) : ledger.debtLeft) : _max0(serverDebtLeft);
      money = _max0(amount - (amount < debtLeft ? amount : debtLeft));
    } else {
      debtUnknown = true;
      money = ledger != null ? _max0(amount - (amount < ledger.debtLeft ? amount : ledger.debtLeft)) : 0;
    }
    money = roundMoney(money);
    final debtReduced = roundMoney(amount - money);
    final cashShare = ledger?.cashLeft ?? money;
    final nonCashShare = ledger?.nonCashLeft ?? 0;
    final prepaid = ledger != null ? (money < cashShare + nonCashShare ? money : cashShare + nonCashShare) : money;
    if (money - prepaid > 0.005) splitKnown = false;
    var cash = cashShare + nonCashShare > 0 ? roundMoney(prepaid * cashShare / (cashShare + nonCashShare)) : prepaid;
    cash = roundMoney(cash + money - prepaid);
    return RefundSplit(cash, roundMoney(money - cash), debtReduced, splitKnown: splitKnown, debtUnknown: debtUnknown);
  }
  switch (method) {
    case 'cash':
      return RefundSplit(amount, 0, 0);
    case 'mixed' || 'split' when ledger != null && ledger.cashLeft + ledger.nonCashLeft > 0:
      final c = roundMoney(amount * ledger.cashLeft / (ledger.cashLeft + ledger.nonCashLeft));
      final cash = c < amount ? c : amount;
      return RefundSplit(cash, roundMoney(amount - cash), 0);
    case 'mixed' || 'split' || '':
      return RefundSplit(amount, 0, 0, splitKnown: false);
    default:
      return RefundSplit(0, amount, 0);
  }
}

/// Строка продажи сервера для возврата: id, сколько продано, сколько ещё можно вернуть, сумма строки.
class SaleLineInfo {
  final String id;
  final String name;
  final double qty;
  final double refundable;
  final double price;
  final double total;
  const SaleLineInfo({required this.id, required this.name, required this.qty, required this.refundable, required this.price, required this.total});

  static double? _num(Map<String, dynamic> m, String k) => m[k] == null ? null : num0(m, k);

  static SaleLineInfo fromJson(Map<String, dynamic> m) {
    final qty = num0(m, 'quantity') > 0 ? num0(m, 'quantity') : num0(m, 'qty');
    // Уже возвращено — как CartDisplayHelper.RefundableQuantity кассы.
    final returned = _num(m, 'quantity_refunded') ?? _num(m, 'returned_quantity') ?? _num(m, 'qty_returned') ?? _num(m, 'refunded_quantity') ?? 0;
    final price = num0(m, 'unit_price') > 0 ? num0(m, 'unit_price') : num0(m, 'price');
    var total = 0.0;
    for (final k in ['line_total', 'line_total_amount', 'line_amount', 'amount', 'total', 'sum', 'total_price', 'subtotal']) {
      if (m[k] != null && num0(m, k) > 0) {
        total = num0(m, k);
        break;
      }
    }
    if (total <= 0) total = roundMoney(qty * price - num0(m, 'discount_total'));
    final fully = m['is_returned'] == true || m['fully_returned'] == true || m['fully_refunded'] == true;
    final left = qty - returned;
    return SaleLineInfo(
      id: str(m, 'id') ?? '',
      name: str(m, 'product_name') ?? str(m, 'name') ?? '',
      qty: qty,
      refundable: fully || left <= 1e-6 ? 0 : left,
      price: price,
      total: total,
    );
  }
}

/// Сумма за [qty] строки: строка как в продаже до возвратов (снимок журнала) × доля скидки на чек (как касса).
double refundLineSum(LedgerEntry? ledger, SaleLineInfo l, double qty) {
  final orig = ledger?.lines.where((x) => x.lineId == l.id).firstOrNull;
  final hasOrig = orig != null && orig.qty > 0;
  final origQty = hasOrig ? orig.qty : l.qty;
  final origTotal = hasOrig ? orig.total : l.total;
  if (origQty <= 0) return 0;
  return roundMoney(origTotal * qty / origQty * (ledger?.factor ?? 1));
}

/// Итог к возврату за выбранное ([picks] — индекс строки и количество): сумма строк, но все возвраты чека вместе —
/// не больше оплаченного; возвращается весь остаток чека — ровно остаток (без копеек округления).
double refundTotal(LedgerEntry? ledger, List<SaleLineInfo> lines, List<(int, double)> picks) {
  var total = picks.fold(0.0, (s, p) => s + refundLineSum(ledger, lines[p.$1], p.$2));
  if (ledger != null && ledger.total > 0) {
    var allLeft = picks.isNotEmpty;
    for (var i = 0; i < lines.length && allLeft; i++) {
      if (lines[i].refundable <= 0) continue;
      allLeft = picks.any((p) => p.$1 == i && (p.$2 - lines[i].refundable).abs() < 1e-6);
    }
    final left = ledger.remaining;
    total = allLeft && left <= total + 0.01 * picks.length ? left : (total < left ? total : left);
  }
  return roundMoney(total);
}

List<SaleLineInfo> saleLines(Map<String, dynamic> sale) {
  dynamic items = sale['items'] ?? sale['lines'];
  if (items == null && sale['cart'] is Map) items = (sale['cart'] as Map)['items'];
  return [for (final m in asList(items)) SaleLineInfo.fromJson(m)].where((x) => x.id.isNotEmpty && x.qty > 0).toList();
}

class ReturnLedger {
  final AppDatabase db;
  ReturnLedger(this.db);

  static const settingKey = 'returns.ledger';
  static const _keepFor = Duration(days: 45);
  static const _maxEntries = 1500;

  Future<Map<String, LedgerEntry>> _load() async {
    final raw = await db.getSetting(settingKey);
    if (raw == null || raw.isEmpty) return {};
    try {
      return {
        for (final m in (jsonDecode(raw) as List).cast<Map>())
          if (LedgerEntry.fromJson(m.cast<String, dynamic>()) case final e when e.saleId.isNotEmpty) e.saleId: e,
      };
    } catch (_) {
      return {};
    }
  }

  Future<void> _save(Map<String, LedgerEntry> all) async {
    final cutoff = DateTime.now().subtract(_keepFor);
    final list = all.values.where((e) => e.recordedAt.isAfter(cutoff)).toList()..sort((a, b) => b.recordedAt.compareTo(a.recordedAt));
    await db.setSetting(settingKey, jsonEncode([for (final e in list.take(_maxEntries)) e.toJson()]));
  }

  Future<LedgerEntry?> get(String saleId) async => (await _load())[saleId];

  /// Продажа только что оплачена: деньги — как их приняли на экране оплаты.
  Future<void> rememberPayment({required String saleId, required double total, required String method, required double paidCash, required double paidNonCash, double linesTotal = 0}) async {
    if (saleId.isEmpty || total < 0) return;
    final all = await _load();
    final e = all.putIfAbsent(saleId, () => LedgerEntry(saleId: saleId));
    if (e.refunds.isNotEmpty) return;
    e
      ..total = total
      ..method = method
      ..paidCash = _max0(paidCash)
      ..paidNonCash = _max0(paidNonCash)
      ..recordedAt = DateTime.now();
    if (linesTotal > 0) e.linesTotal = linesTotal;
    await _save(all);
  }

  /// Чек открыт в «Возврате»: по нему ещё не было возвратов — запоминаем строки и итог с сервера. Деньги из оплаты
  /// (если телефон их запомнил) не трогаем; иначе — из полей продажи сервера. Возвращает запись журнала (или null).
  Future<LedgerEntry?> rememberFromSale(String saleId, Map<String, dynamic> sale) async {
    final all = await _load();
    final known = all[saleId];
    final lines = saleLines(sale);
    final untouched = lines.isNotEmpty && lines.every((l) => l.refundable >= l.qty - 1e-6);
    final total = num0(sale, 'total');
    if (!untouched || total <= 0 || (known != null && known.refunds.isNotEmpty)) return known;
    final e = known ?? LedgerEntry(saleId: saleId);
    e.lines = [for (final l in lines) LedgerLine(l.id, l.name, l.qty, l.total)];
    final lt = lines.fold(0.0, (s, l) => s + l.total);
    if (lt > 0) e.linesTotal = roundMoney(lt);
    final knownFromPayment = e.method.isNotEmpty && e.total > 0;
    if (!knownFromPayment) {
      e.total = total;
      e.method = (str(sale, 'payment_method') ?? '').trim().toLowerCase();
      final received = num0(sale, 'cash_received');
      switch (e.method) {
        case 'cash':
          e.paidCash = total;
        case 'mixed' || 'split':
          e.paidCash = received < total ? received : total;
          e.paidNonCash = _max0(total - e.paidCash);
        case 'debt':
          e.paidCash = received < total ? received : total;
        default:
          e.paidNonCash = total;
      }
    }
    all[saleId] = e;
    await _save(all);
    return e;
  }

  Future<void> addRefund(String saleId, LedgerRefund r) async {
    final all = await _load();
    final e = all[saleId];
    if (e == null) return;
    e.refunds.add(r);
    await _save(all);
  }
}
