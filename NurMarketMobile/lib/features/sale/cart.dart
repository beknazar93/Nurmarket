/// Чек на телефоне: собирается локально и уходит на сервер одним запросом при оплате (ТЗ, раздел 4).
/// Правила итога — как у сервера для POST api/main/pos/checkout/ (QuickCheckoutBody.ComputeTotal кассы):
/// Σ(цена × кол-во − скидка строки), затем скидка на чек — процент от суммы после скидок строк, сумма — не больше остатка.
library;

import 'dart:convert';

double roundMoney(double v) => v.isFinite ? (v * 100).roundToDouble() / 100 : 0;

/// 18.10, денежный баг телефона: «Акции» товара NurCRM (promotion_rules) сервер применяет к строке САМ, а телефон о них не
/// знал — показывал покупателю, считал сдачу и печатал чек на сумму без скидки. Правила — как у кассы (PromotionRules.LineDiscount,
/// проверены на тестовом аккаунте 28.09): из ступеней, у которых «от суммы» ≤ сумма строки, берётся ступень с наибольшим
/// «от суммы»; скидка = процент от суммы, не больше чем на promo_quantity штук; ручную скидку строки у такого товара сервер
/// игнорирует; скидка на чек — от суммы после акции.
class PromoRule {
  final double minAmount;
  final double percent;
  final double? quantity;
  final int position;

  const PromoRule({required this.minAmount, required this.percent, this.quantity, this.position = 0});

  static double _n(Object? v) => v is num ? v.toDouble() : double.tryParse('${v ?? ''}'.replaceAll(',', '.')) ?? 0;

  /// Ступени из promotion_rules сервера (JSON-строка базы телефона или список из API); без процента — не ступень.
  static List<PromoRule> parse(Object? raw) {
    Object? data = raw;
    if (raw is String) {
      if (raw.trim().isEmpty) return const [];
      try {
        data = jsonDecode(raw);
      } catch (_) {
        return const [];
      }
    }
    if (data is! List) return const [];
    final out = <PromoRule>[];
    for (final r in data) {
      if (r is! Map) continue;
      final pct = _n(r['discount_percent']);
      if (!(pct > 0)) continue;
      final q = _n(r['promo_quantity']);
      out.add(PromoRule(minAmount: _n(r['min_amount']), percent: pct, quantity: q > 0 ? q : null, position: _n(r['position']).toInt()));
    }
    out.sort((a, b) => a.position.compareTo(b.position));
    return out;
  }

  /// Для колонки базы: '' — акции нет.
  static String encodeFromApi(Object? raw) {
    final rules = parse(raw);
    if (rules.isEmpty) return '';
    return jsonEncode([
      for (final r in rules)
        {'position': r.position, 'min_amount': r.minAmount, 'discount_percent': r.percent, if (r.quantity != null) 'promo_quantity': r.quantity},
    ]);
  }
}

/// Скидка строки по акции так, как её посчитает сервер.
double promoDiscount(List<PromoRule> rules, double qty, double price) {
  final gross = roundMoney(qty * price);
  if (!(gross > 0)) return 0;
  PromoRule? best;
  for (final r in rules) {
    if (gross + 1e-9 < r.minAmount) continue;
    if (best == null || r.minAmount > best.minAmount) best = r;
  }
  if (best == null) return 0;
  final pct = best.percent > 100 ? 100.0 : best.percent;
  final q = best.quantity;
  final discountedQty = q != null && q > 0 && q < qty ? q : qty;
  final d = roundMoney(roundMoney(discountedQty * price) * pct / 100);
  return d < 0 ? 0 : (d > gross ? gross : d);
}

class CartLine {
  final String productId;
  final String name;

  /// Цена строки сейчас: розничная или оптовая (13.10, «Розница / Опт»).
  double price;
  final bool isWeight;
  final String unit;
  final bool custom;
  double qty;

  /// Скидка кассира на строку. У товара с акцией не действует — там скидку считает сервер (см. [discount]).
  double _manualDiscount;

  /// 18.10: ступени акции товара; пусто — акции нет.
  final List<PromoRule> promo;

  /// Розничная и оптовая цены товара; wholesalePrice = 0 — опта у товара нет.
  final double retailPrice;
  final double wholesalePrice;
  bool wholesale;

  /// 13.10, макет С5: вариант товара (размер/цвет) — uuid варианта NurCRM; сервер списывает и товар, и размер.
  final String? variantId;

  CartLine({
    required this.productId,
    required this.name,
    required this.price,
    this.qty = 1,
    this.isWeight = false,
    this.unit = 'шт',
    this.custom = false,
    double discount = 0,
    double? retailPrice,
    this.wholesalePrice = 0,
    this.wholesale = false,
    this.variantId,
    this.promo = const [],
  }) : retailPrice = retailPrice ?? price,
       _manualDiscount = discount;

  bool get hasPromo => promo.isNotEmpty;

  /// Действующая скидка строки: по акции (как посчитает сервер) или ручная скидка кассира.
  double get discount => hasPromo ? promoDiscount(promo, qty, price) : _manualDiscount;

  /// Ручная скидка; строке с акцией не ставится (сервер её всё равно не примет).
  set discount(double v) {
    if (!hasPromo) _manualDiscount = v;
  }

  bool get canWholesale => wholesalePrice > 0 && !custom;

  /// Строка ↔ оптовая цена (как SetLineWholesale кассы): цена меняется, скидка суммой не больше новой суммы строки.
  void setWholesale(bool on) {
    if (on && !canWholesale) return;
    wholesale = on;
    price = on ? wholesalePrice : retailPrice;
    if (discount > gross) discount = gross;
  }

  double get gross => roundMoney(qty * price);
  double get total => roundMoney(gross - discount);
}

class Cart {
  final List<CartLine> lines;

  /// Скидка на чек: либо процент, либо сумма — серверу уходит только одно поле.
  final double orderDiscountPercent;
  final double orderDiscountSum;

  const Cart({this.lines = const [], this.orderDiscountPercent = 0, this.orderDiscountSum = 0});

  bool get isEmpty => lines.isEmpty;
  int get count => lines.length;
  double get quantity => lines.fold(0, (s, l) => s + (l.isWeight ? 1 : l.qty));

  double get subtotal => lines.fold(0.0, (s, l) => roundMoney(s + l.gross));
  double get lineDiscounts => lines.fold(0.0, (s, l) => roundMoney(s + l.discount));

  double get orderDiscount {
    final base = (subtotal - lineDiscounts).clamp(0, double.infinity).toDouble();
    if (orderDiscountPercent > 0) return roundMoney(base * orderDiscountPercent.clamp(0, 100) / 100);
    if (orderDiscountSum > 0) return orderDiscountSum > base ? base : roundMoney(orderDiscountSum);
    return 0;
  }

  double get total {
    final base = roundMoney(subtotal - lineDiscounts);
    final t = roundMoney(base - orderDiscount);
    return t < 0 ? 0 : t;
  }

  Cart copyWith({List<CartLine>? lines, double? orderDiscountPercent, double? orderDiscountSum}) => Cart(
    lines: lines ?? this.lines,
    orderDiscountPercent: orderDiscountPercent ?? this.orderDiscountPercent,
    orderDiscountSum: orderDiscountSum ?? this.orderDiscountSum,
  );

  /// Тот же товар — прибавляется к строке (весовой — новой строкой: каждое взвешивание отдельно).
  Cart add(CartLine line) {
    final list = [...lines];
    final i = list.indexWhere(
      (l) => !l.isWeight && !l.custom && l.productId == line.productId && l.wholesale == line.wholesale && l.variantId == line.variantId,
    );
    if (i >= 0 && !line.isWeight) {
      list[i].qty += line.qty;
    } else {
      list.add(line);
    }
    return copyWith(lines: list);
  }

  Cart setQty(int index, double qty) {
    final list = [...lines];
    if (qty <= 0) {
      list.removeAt(index);
    } else {
      list[index].qty = qty;
      // 14.10, проверка логики: количество уменьшили — скидка строки не больше новой суммы (иначе строка уходила в минус).
      if (list[index].discount > list[index].gross) list[index].discount = list[index].gross;
    }
    return copyWith(lines: list);
  }

  Cart remove(int index) => copyWith(lines: [...lines]..removeAt(index));
}

/// Сдача и подсказки сумм («Без сдачи», ближайшие круглые купюры).
double change(double received, double total) => received > total ? roundMoney(received - total) : 0;

List<double> cashSuggestions(double total) {
  if (total <= 0) return const [];
  final out = <double>{total};
  for (final step in [50.0, 100.0, 500.0, 1000.0, 5000.0]) {
    final v = (total / step).ceil() * step;
    if (v > total) out.add(v);
    if (out.length >= 4) break;
  }
  return out.toList()..sort();
}
