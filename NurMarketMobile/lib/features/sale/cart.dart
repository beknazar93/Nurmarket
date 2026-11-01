/// Чек на телефоне: собирается локально и уходит на сервер одним запросом при оплате (ТЗ, раздел 4).
/// Правила итога — как у сервера для POST api/main/pos/checkout/ (QuickCheckoutBody.ComputeTotal кассы):
/// Σ(цена × кол-во − скидка строки), затем скидка на чек — процент от суммы после скидок строк, сумма — не больше остатка.
library;

import 'dart:convert';

/// 2026-11-01 (аудит 10 из 10, М-01): HALF_UP до 0,01 (как ROUND_HALF_UP сервера). Поправка 1e-7 убирает ошибку double на границе
/// («1,005 × 100 = 100,4999…» округлялось вниз).
double roundMoney(double v) => v.isFinite ? (v * 100 + (v >= 0 ? 1e-7 : -1e-7)).roundToDouble() / 100 : 0;

// 2026-11-01 (М-01, ответ Умара 01.11): сервер складывает строки БЕЗ округления и округляет только итог (ROUND_HALF_UP до 0,01).
// Расчёт идёт в целых единицах (BigInt — без переполнения): количество ×10^6, цена ×10^8 → сумма строки в 10^-14 сома (1 копейка = 10^12).
// Входная точность сохраняется (цена упаковки 100/3 = 33,33333333, вес с 6 знаками). Накопления double нет; один раз HALF_UP при показе
// и отправке. Касса считает так же (делается параллельно).
const int _qDigits = 6;
const int _pDigits = 8;
final BigInt _unitsPerCent = BigInt.from(10).pow(_qDigits + _pDigits - 2); // 10^12

/// double → целое с [digits] знаками после запятой (через десятичную запись, без ошибки умножения double).
BigInt _scaled(double v, int digits) {
  if (!v.isFinite) return BigInt.zero;
  final c = v.abs() > 1e12 ? (v < 0 ? -1e12 : 1e12) : v; // защита от экспоненциальной записи
  return BigInt.parse(c.toStringAsFixed(digits).replaceAll('.', ''));
}

/// Сумма строки (количество × цена) в единицах, без округления.
BigInt lineUnits(double qty, double price) => _scaled(qty, _qDigits) * _scaled(price, _pDigits);

/// 2026-11-01 (живая сверка NBS, чеки 1979/1980): весовая строка округляется ВНИЗ до 0,01 (0,343 кг × 66 = 22,638 → 22,63),
/// обычная — не округляется. [weight] — строка весового товара (единица кг/г/л/м или флаг товара).
BigInt lineUnitsFor(double qty, double price, {bool weight = false}) {
  final u = lineUnits(qty, price);
  return weight ? (u ~/ _unitsPerCent) * _unitsPerCent : u;
}

/// Деньги (double с копейками) → единицы.
BigInt moneyUnits(double m) => _scaled(m, 2) * _unitsPerCent;

/// Единицы → сомы, HALF_UP до 0,01 (для отрицательных — симметрично).
double unitsToMoney(BigInt u) {
  final neg = u.isNegative;
  final a = neg ? -u : u;
  final cents = ((a + _unitsPerCent ~/ BigInt.two) ~/ _unitsPerCent).toInt();
  return (neg ? -cents : cents) / 100;
}

/// Доля процента от единиц, HALF_UP до единицы.
BigInt _percentOf(BigInt units, double percent) => (units * _scaled(percent, 2) + BigInt.from(5000)) ~/ BigInt.from(10000);

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

/// Скидка строки по акции так, как её посчитает сервер (в единицах, без округления строки — М-01).
BigInt promoDiscountUnits(List<PromoRule> rules, double qty, double price, {bool weight = false}) {
  final gross = lineUnitsFor(qty, price, weight: weight);
  if (gross <= BigInt.zero) return BigInt.zero;
  PromoRule? best;
  for (final r in rules) {
    if (gross + BigInt.one < moneyUnitsExact(r.minAmount)) continue;
    if (best == null || r.minAmount > best.minAmount) best = r;
  }
  if (best == null) return BigInt.zero;
  final pct = best.percent > 100 ? 100.0 : best.percent;
  final q = best.quantity;
  final discountedQty = q != null && q > 0 && q < qty ? q : qty;
  final d = _percentOf(lineUnitsFor(discountedQty, price, weight: weight), pct);
  return d.isNegative ? BigInt.zero : (d > gross ? gross : d);
}

/// Порог «от суммы» акции в единицах (без округления до копеек).
BigInt moneyUnitsExact(double m) => _scaled(m, _qDigits + _pDigits);

/// Скидка строки по акции, сомы (HALF_UP до 0,01 — для показа).
double promoDiscount(List<PromoRule> rules, double qty, double price) => unitsToMoney(promoDiscountUnits(rules, qty, price));

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
  // 2026-10-29 (аудит мобилки, п. 8): не final — сверка цен с сервером перед оплатой обновляет цены строки.
  double retailPrice;
  double wholesalePrice;
  bool wholesale;

  /// 13.10, макет С5: вариант товара (размер/цвет) — uuid варианта NurCRM; сервер списывает и товар, и размер.
  final String? variantId;

  /// 2026-10-27 (М4): штука из упаковки — id упаковки (в чек уходит как sale_package_id, цена строки — цена штуки, количество — штуки).
  /// null — обычная строка (целая упаковка / штучный товар).
  final String? salePackageId;

  /// Штук в упаковке у строки-штуки: остаток товара хранится в упаковках, продажа списывает qty / perPack. У обычной строки 1.
  final double perPack;

  /// Название упаковки (Пачка) у товара с поштучной опцией: подпись строки «Пачка» / «шт. из «Пачка»».
  final String? packName;

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
    this.salePackageId,
    this.perPack = 1,
    this.packName,
    this.promo = const [],
  }) : retailPrice = retailPrice ?? price,
       _manualDiscount = discount;

  bool get hasPromo => promo.isNotEmpty;

  bool get isPiece => salePackageId != null;

  /// Сколько списать с остатка товара (в его единицах): для штуки из упаковки — доля упаковки, для остальных — qty.
  double get stockQty => perPack > 1 ? qty / perPack : qty;

  /// Действующая скидка строки: по акции (как посчитает сервер) или ручная скидка кассира.
  /// 2026-11-01 (контрольное ревью): ручная скидка ограничена суммой строки везде — строка не уходит в минус и не уменьшает другие.
  double get discount => hasPromo ? unitsToMoney(promoDiscountUnits(promo, qty, price, weight: isWeight)) : (_manualDiscount <= 0 ? 0 : (_manualDiscount > gross ? gross : _manualDiscount));

  /// 2026-11-01 (М-01): сумма и скидка строки в единицах — для точного итога чека без округления строк.
  BigInt get grossUnits => lineUnitsFor(qty, price, weight: isWeight);
  BigInt get discountUnits {
    if (hasPromo) return promoDiscountUnits(promo, qty, price, weight: isWeight);
    final d = moneyUnits(_manualDiscount < 0 ? 0 : _manualDiscount);
    final g = grossUnits;
    return d > g ? g : d;
  }

  /// Ручная скидка не больше суммы строки (после уменьшения количества или смены цены): хранимое значение тоже обрезается.
  void clampDiscount() {
    if (!hasPromo && _manualDiscount > gross) _manualDiscount = gross;
  }

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
    clampDiscount();
  }

  double get gross => unitsToMoney(grossUnits);
  double get total => unitsToMoney(grossUnits - discountUnits);
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

  // 2026-11-01 (М-01): суммы — в целых единицах без накопления double и без округления строк; округление HALF_UP — один раз на показ.
  BigInt get _subtotalUnits => lines.fold(BigInt.zero, (s, l) => s + l.grossUnits);
  BigInt get _lineDiscountUnits => lines.fold(BigInt.zero, (s, l) => s + l.discountUnits);
  BigInt get _baseUnits {
    final b = _subtotalUnits - _lineDiscountUnits;
    return b.isNegative ? BigInt.zero : b;
  }

  /// Подытог, округлённый один раз HALF_UP до 0,01 (в единицах): от него считается процент скидки чека (живая сверка NBS, чек 1977).
  BigInt get _baseRoundedUnits => moneyUnits(unitsToMoney(_baseUnits));

  BigInt get _orderDiscountUnits {
    final base = _baseRoundedUnits;
    if (orderDiscountPercent > 0) return _percentOf(base, orderDiscountPercent.clamp(0, 100).toDouble());
    if (orderDiscountSum > 0) {
      final s = moneyUnits(orderDiscountSum);
      return s > base ? base : s;
    }
    return BigInt.zero;
  }

  double get subtotal => unitsToMoney(_subtotalUnits);
  double get lineDiscounts => unitsToMoney(_lineDiscountUnits);
  double get orderDiscount => unitsToMoney(_orderDiscountUnits);

  /// 2026-11-01 (сверка с 1932 чеками NBS и живая сверка): подытог округляется один раз HALF_UP, процент скидки берётся от ОКРУГЛЁННОГО
  /// подытога, сумма скидки округляется отдельно HALF_UP, итог = подытог − скидка (17,00 − 2,5 % → скидка 0,43 → 16,57; чек 1977: 1,43 × 50 % → 0,72).
  double get total {
    final t = (unitsToMoney(_baseUnits) * 100).round() - (orderDiscount * 100).round();
    return t < 0 ? 0 : t / 100;
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
      (l) =>
          !l.isWeight &&
          !l.custom &&
          l.productId == line.productId &&
          l.wholesale == line.wholesale &&
          l.variantId == line.variantId &&
          l.salePackageId == line.salePackageId, // 2026-10-27 (М4): штуки и целые упаковки — разные строки
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
      list[index].clampDiscount();
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
