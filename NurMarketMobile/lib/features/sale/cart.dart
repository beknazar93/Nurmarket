/// Чек на телефоне: собирается локально и уходит на сервер одним запросом при оплате (ТЗ, раздел 4).
/// Правила итога — как у сервера для POST api/main/pos/checkout/ (QuickCheckoutBody.ComputeTotal кассы):
/// Σ(цена × кол-во − скидка строки), затем скидка на чек — процент от суммы после скидок строк, сумма — не больше остатка.
library;

import 'dart:convert';

/// 2026-11-01 (аудит 10 из 10, М-01): HALF_UP до 0,01 (как ROUND_HALF_UP сервера). Поправка 1e-7 убирает ошибку double на границе
/// («1,005 × 100 = 100,4999…» округлялось вниз).
double roundMoney(double v) => v.isFinite ? (v * 100 + (v >= 0 ? 1e-7 : -1e-7)).roundToDouble() / 100 : 0;

// 2026-11-01 (М-01, ответ Умара 01.11): сервер складывает строки БЕЗ округления и округляет только итог (ROUND_HALF_UP до 0,01).
// Расчёт идёт в целых единицах (BigInt — без переполнения). Точность ровно такая, с какой строки уходят на сервер (SalesRepository.buildBody:
// количество — `toStringAsFixed(3)`, цена — `toStringAsFixed(2)`): сервер умножает ТО, что получил (контрольный аудит, М2-02). Накопления
// double нет; один раз HALF_UP при показе и отправке. Касса считает так же (делается параллельно).
const int _qDigits = 3;
const int _pDigits = 2;

/// 2026-11-02 (аудит №2, М3-03): запас 10^4 в единицах — процент (две цифры после запятой) делится на 10 000 БЕЗ остатка, поэтому скидка по
/// акции считается точно и округляется до копейки один раз (раньше округлялась до единицы, потом ещё раз до копейки).
const int _exactDigits = 4;
final BigInt _exactFactor = BigInt.from(10).pow(_exactDigits);
final BigInt _unitsPerCent = BigInt.from(10).pow(_qDigits + _pDigits - 2 + _exactDigits); // 10^7

/// double → целое с [digits] знаками после запятой (через десятичную запись, без ошибки умножения double).
BigInt _scaled(double v, int digits) {
  if (!v.isFinite) return BigInt.zero;
  final c = v.abs() > 1e12 ? (v < 0 ? -1e12 : 1e12) : v; // защита от экспоненциальной записи
  return BigInt.parse(c.toStringAsFixed(digits).replaceAll('.', ''));
}

/// Сумма строки (количество × цена) в единицах, без округления.
BigInt lineUnits(double qty, double price) => _scaled(qty, _qDigits) * _scaled(price, _pDigits) * _exactFactor;

/// Деньги (double с копейками) → единицы.
BigInt moneyUnits(double m) => _scaled(m, 2) * _unitsPerCent;

/// Единицы → сомы, HALF_UP до 0,01 (для отрицательных — симметрично).
double unitsToMoney(BigInt u) {
  final neg = u.isNegative;
  final a = neg ? -u : u;
  final cents = ((a + _unitsPerCent ~/ BigInt.two) ~/ _unitsPerCent).toInt();
  return (neg ? -cents : cents) / 100;
}

/// Доля процента от единиц — точно (единицы кратны 10^4, см. _exactDigits).
BigInt _percentOf(BigInt units, double percent) => (units * _scaled(percent, 2)) ~/ BigInt.from(10000); // units кратны 10^4 — деление точное

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
BigInt promoDiscountUnits(List<PromoRule> rules, double qty, double price) {
  final gross = lineUnits(qty, price);
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
  final d = _percentOf(lineUnits(discountedQty, price), pct);
  return d.isNegative ? BigInt.zero : (d > gross ? gross : d);
}

/// Порог «от суммы» акции в единицах (без округления до копеек).
BigInt moneyUnitsExact(double m) => _scaled(m, _qDigits + _pDigits) * _exactFactor;

/// Скидка строки по акции, сомы (HALF_UP до 0,01 — для показа).
double promoDiscount(List<PromoRule> rules, double qty, double price) => unitsToMoney(promoDiscountUnits(rules, qty, price));

class CartLine {
  final String productId;
  final String name;

  /// Цена строки сейчас: розничная или оптовая (13.10, «Розница / Опт»).
  double price;
  final bool isWeight;

  /// 2026-11-01 (округление весовых): чистый признак `is_weight` товара с сервера (без единицы кг/г); null — неизвестен, тогда как [isWeight].
  final bool? weightFlag;

  /// Весовая ли строка для ПОДЫТОГА чека (вниз до 0,01 суммой всех весовых строк): по признаку сервера, иначе по [isWeight].
  bool get weighedForRounding => weightFlag ?? isWeight;
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
    this.weightFlag,
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

  /// Ключ слияния строк в одну позицию чека — тот же, что в SalesRepository.buildBody; null — строка не сливается («Доп. услуга»).
  String? get mergeKey =>
      custom ? null : '$productId|${price.toStringAsFixed(2)}|${variantId ?? ''}${salePackageId == null ? '' : '|$salePackageId'}';

  bool get isPiece => salePackageId != null;

  /// Сколько списать с остатка товара (в его единицах): для штуки из упаковки — доля упаковки, для остальных — qty.
  double get stockQty => perPack > 1 ? qty / perPack : qty;

  /// Действующая скидка строки: по акции (как посчитает сервер) или ручная скидка кассира.
  /// 2026-11-01 (контрольное ревью): ручная скидка ограничена суммой строки везде — строка не уходит в минус и не уменьшает другие.
  double get discount => hasPromo ? unitsToMoney(promoDiscountUnits(promo, qty, price)) : (_manualDiscount <= 0 ? 0 : (_manualDiscount > gross ? gross : _manualDiscount));

  /// 2026-11-01 (М-01): сумма и скидка строки в единицах — для точного итога чека без округления строк.
  BigInt get grossUnits => lineUnits(qty, price);
  BigInt get discountUnits {
    if (hasPromo) return promoDiscountUnits(promo, qty, price);
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
  /// 2026-11-01 (опыты на сервере, чеки 1971–1988): подытог = FLOOR(Σ весовых строк, 0,01) + HALF_UP(Σ остальных строк, 0,01).
  /// Вниз округляется СУММА весовых строк, а не каждая (4,875 + 22,638 = 27,513 → 27,51). Строки считаются точно, без округления.
  /// Весовая — `CartLine.weighedForRounding` (чистый is_weight сервера, иначе isWeight). Результат — в единицах, кратных копейке.
  BigInt get _subtotalUnits {
    var weighed = BigInt.zero, other = BigInt.zero;
    for (final l in lines) {
      if (l.weighedForRounding) {
        weighed += l.grossUnits;
      } else {
        other += l.grossUnits;
      }
    }
    final w = (weighed ~/ _unitsPerCent) * _unitsPerCent;
    final o = ((other + _unitsPerCent ~/ BigInt.two) ~/ _unitsPerCent) * _unitsPerCent;
    return w + o;
  }

  /// 2026-11-01 (контрольный аудит, М2-01): скидки строк считаются по тем же позициям, что уходят на сервер: строки одного товара по одной цене
  /// (ключ [CartLine.mergeKey]) сервер сливает в одну позицию, поэтому порог и лимит акции берутся от СУММЫ количеств группы.
  BigInt get _lineDiscountUnits {
    final groups = <String, List<CartLine>>{};
    var total = BigInt.zero;
    for (final l in lines) {
      final k = l.mergeKey;
      if (k == null) {
        total += l.discountUnits; // «Доп. услуга» всегда отдельная позиция
      } else {
        (groups[k] ??= []).add(l);
      }
    }
    for (final g in groups.values) {
      final first = g.first;
      if (first.hasPromo) {
        total += promoDiscountUnits(first.promo, g.fold(0.0, (s, l) => s + l.qty), first.price);
      } else {
        for (final l in g) {
          total += l.discountUnits;
        }
      }
    }
    return total;
  }

  /// База скидки чека: округлённый подытог минус скидки строк (в копейках) — от неё процент скидки (чек 1986: (22,63 − 0,07) × 15 % = 3,38).
  BigInt get _baseUnits {
    final b = _subtotalUnits - moneyUnits(lineDiscounts);
    return b.isNegative ? BigInt.zero : b;
  }

  BigInt get _orderDiscountUnits {
    final base = _baseUnits;
    if (orderDiscountPercent > 0) return _percentOf(base, orderDiscountPercent.clamp(0, 100).toDouble());
    if (orderDiscountSum > 0) {
      final s = moneyUnits(orderDiscountSum);
      return s > base ? base : s;
    }
    return BigInt.zero;
  }

  double get subtotal => unitsToMoney(_subtotalUnits);

  /// 2026-11-05 (М4-14, как строка «Округление весовых» кассы 1.17.73): сколько округление вниз суммы весовых строк «съело» у покупателя
  /// (4,875 + 22,638 = 27,513 → 27,51: 0,003). 0 — весовых строк нет или они сошлись до копейки.
  double get weightRounding {
    var weighed = BigInt.zero;
    for (final l in lines) {
      if (l.weighedForRounding) weighed += l.grossUnits;
    }
    final floored = (weighed ~/ _unitsPerCent) * _unitsPerCent;
    // 2026-11-05: точная доля копейки (0,003), а не округлённая до копейки — иначе всегда 0; на экране и в печати порог ≥ 0,005 прежний.
    return (weighed - floored).toDouble() / (_unitsPerCent.toDouble() * 100);
  }
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
