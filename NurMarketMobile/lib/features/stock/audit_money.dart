// 2026-10-30 (владелец: «в ревизии мобилки показать потери в деньгах, как на кассе»): расчёт и показ денежных итогов ревизии.
// Образец логики — RevisionSession.GetTotals / RevisionRowView.DiffMoney / MoneyLines кассы. Закупка и суммы — только тем,
// кому закупка доступна (Session.canSeePurchase: владелец, администратор, «Настройки»); кассиру экран ревизии прежний.
import 'package:flutter/material.dart';

import '../../core/db/database.dart';
import '../../core/util/ui.dart';

double _r2(double v) => (v * 100).roundToDouble() / 100;
double _r3(double v) => (v * 1000).roundToDouble() / 1000;

/// Одна посчитанная позиция ревизии: учёт по программе, факт, цены.
class AuditLine {
  final String id;
  final String name;
  final String unit;
  final double expected;
  final double counted;
  final double salePrice;

  /// null или 0 — закупка неизвестна: сумма разницы считается по цене продажи (с пометкой «*»).
  final double? purchase;

  const AuditLine({required this.id, required this.name, this.unit = '', required this.expected, required this.counted, required this.salePrice, this.purchase});

  factory AuditLine.of(Product p, double counted) =>
      AuditLine(id: p.id, name: p.name, unit: p.unit, expected: p.quantity, counted: counted, salePrice: p.price, purchase: p.purchasePrice);

  bool get hasPurchase => (purchase ?? 0) > 0.0001;
  double get diff => counted - expected;
  bool get hasDiff => diff.abs() > 0.0001;

  /// Деньги по расхождению: разница × закупка; закупки нет — разница × цена продажи ([fallback] = true, пометка «*»).
  /// Расхождения нет — сумма 0. null — нет ни закупки, ни цены продажи: считать нечем.
  ({double amount, bool fallback})? get money {
    if (diff.abs() <= 0.0005) return (amount: 0, fallback: false);
    if (hasPurchase) return (amount: _r2(diff * purchase!), fallback: false);
    if (salePrice > 0.0001) return (amount: _r2(diff * salePrice), fallback: true);
    return null;
  }
}

/// Итоги ревизии в деньгах (недостача, излишек — по закупке и по продаже; товары без закупки считаются отдельно).
class AuditTotals {
  final int counted;
  final int withDifference;
  final double shortageQty, shortageAtSale, shortageAtCost;
  final double surplusQty, surplusAtSale, surplusAtCost;

  /// «Не хватает закупки у N товаров»: позиции с расхождением без закупки; их разница по цене продажи в суммы «по закупке» не вошла.
  final int noCostCount;
  final double shortageNoCostAtSale, surplusNoCostAtSale;

  const AuditTotals({
    this.counted = 0,
    this.withDifference = 0,
    this.shortageQty = 0,
    this.shortageAtSale = 0,
    this.shortageAtCost = 0,
    this.surplusQty = 0,
    this.surplusAtSale = 0,
    this.surplusAtCost = 0,
    this.noCostCount = 0,
    this.shortageNoCostAtSale = 0,
    this.surplusNoCostAtSale = 0,
  });

  double get netAtCost => _r2(surplusAtCost - shortageAtCost);
  double get netAtSale => _r2(surplusAtSale - shortageAtSale);

  /// Итоги по посчитанным позициям [lines] (не посчитанные в список не входят).
  factory AuditTotals.of(Iterable<AuditLine> lines) {
    var counted = 0, withDiff = 0, noCost = 0;
    double shQ = 0, shS = 0, shC = 0, suQ = 0, suS = 0, suC = 0, shN = 0, suN = 0;
    for (final r in lines) {
      counted++;
      if (!r.hasDiff) continue;
      withDiff++;
      final d = r.diff;
      final cost = r.hasPurchase ? r.purchase! : 0.0;
      if (!r.hasPurchase) noCost++;
      if (d < 0) {
        shQ += -d;
        shS += -d * r.salePrice;
        shC += -d * cost;
        if (!r.hasPurchase) shN += -d * r.salePrice;
      } else {
        suQ += d;
        suS += d * r.salePrice;
        suC += d * cost;
        if (!r.hasPurchase) suN += d * r.salePrice;
      }
    }
    return AuditTotals(
      counted: counted,
      withDifference: withDiff,
      shortageQty: _r3(shQ),
      shortageAtSale: _r2(shS),
      shortageAtCost: _r2(shC),
      surplusQty: _r3(suQ),
      surplusAtSale: _r2(suS),
      surplusAtCost: _r2(suC),
      noCostCount: noCost,
      shortageNoCostAtSale: _r2(shN),
      surplusNoCostAtSale: _r2(suN),
    );
  }

  /// Топ-[n] самых дорогих недостач (по закупке; закупки нет — по цене продажи): сначала самые большие потери.
  static List<AuditLine> topShortages(Iterable<AuditLine> lines, {int n = 10}) {
    final list = [for (final r in lines) if (r.hasDiff && r.diff < 0) (r, r.money?.amount ?? 0.0)];
    list.sort((a, b) => a.$2.compareTo(b.$2));
    return [for (final e in list.take(n)) e.$1];
  }
}

/// «−600», «+1 250», «0» — сумма со знаком (минус настоящий, как на кассе).
String signedMoney(double v) => v > 0.004 ? '+${money(v)}' : (v < -0.004 ? '−${money(-v)}' : '0');

/// Сумма разницы строки: «−600 сом» или «−500 сом*» (по цене продажи); null — считать нечем.
String? auditAmountText(AuditLine r, String som) {
  final m = r.money;
  if (m == null) return null;
  return '${signedMoney(m.amount)} $som${m.fallback ? '*' : ''}';
}

/// Денежные итоги ревизии одной строкой на пункт: Недостача / Излишек / Чистый результат / «Не хватает закупки» (как MoneyLines кассы).
/// Пусто, если расхождений нет. Показывать только при Session.canSeePurchase.
class AuditMoneyLines extends StatelessWidget {
  final AuditTotals totals;

  /// В окне завершения — ещё и пояснение к «Не хватает закупки».
  final bool showTip;
  final double fontSize;
  const AuditMoneyLines({super.key, required this.totals, this.showTip = false, this.fontSize = 12.5});

  @override
  Widget build(BuildContext context) {
    final t = totals;
    if (t.withDifference == 0) return const SizedBox.shrink();
    final l = context.l;
    final pal = context.pal;
    TextStyle st(Color c) => TextStyle(fontSize: fontSize, fontWeight: FontWeight.w700, color: c);
    final net = t.netAtCost;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (t.shortageQty > 0)
          Text(l.revShortageLine(qtyText(t.shortageQty), money(t.shortageAtCost), money(t.shortageAtSale)), key: const ValueKey('rev-shortage'), style: st(pal.danger)),
        if (t.surplusQty > 0)
          Text(l.revSurplusLine(qtyText(t.surplusQty), money(t.surplusAtCost), money(t.surplusAtSale)), key: const ValueKey('rev-surplus'), style: st(pal.success)),
        Text(
          l.revNetLine(signedMoney(net)),
          key: const ValueKey('rev-net'),
          style: st(net < -0.004 ? pal.danger : (net > 0.004 ? pal.success : pal.text)),
        ),
        if (t.noCostCount > 0)
          Text(l.revNoCostWarn(t.noCostCount), key: const ValueKey('rev-nocost'), style: st(pal.warn)),
        if (t.noCostCount > 0 && !showTip)
          Text(l.revStarNote, key: const ValueKey('rev-star-footer'), style: TextStyle(fontSize: fontSize - 1, color: pal.textSoft)),
        if (t.noCostCount > 0 && showTip)
          Padding(
            padding: const EdgeInsets.only(top: 2),
            child: Text(
              l.revNoCostTip(money(t.shortageNoCostAtSale), money(t.surplusNoCostAtSale)),
              key: const ValueKey('rev-nocost-tip'),
              style: TextStyle(fontSize: fontSize - 1, color: pal.textSoft),
            ),
          ),
      ],
    );
  }
}

/// Окно «Провести ревизию» для владельца и администратора: денежные итоги и список расхождений — топ-10 недостач или все.
class AuditFinishDetails extends StatefulWidget {
  final List<AuditLine> lines;
  const AuditFinishDetails({super.key, required this.lines});
  @override
  State<AuditFinishDetails> createState() => _AuditFinishDetailsState();
}

class _AuditFinishDetailsState extends State<AuditFinishDetails> {
  bool? _topOnly; // null — по умолчанию: топ, если есть недостачи

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final totals = AuditTotals.of(widget.lines);
    if (totals.withDifference == 0) return const SizedBox.shrink();
    final diffs = [for (final r in widget.lines) if (r.hasDiff) r];
    final top = AuditTotals.topShortages(widget.lines);
    final topOnly = (_topOnly ?? true) && top.isNotEmpty;
    final shown = topOnly ? top : diffs;
    final som = l.som;
    final anyStar = shown.any((r) => r.money?.fallback ?? false);
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        AuditMoneyLines(totals: totals, showTip: true),
        const SizedBox(height: 8),
        Wrap(
          spacing: 6,
          children: [
            if (top.isNotEmpty)
              ChoiceChip(
                key: const ValueKey('rev-top'),
                label: Text(l.revTopShortages(top.length)),
                selected: topOnly,
                labelStyle: TextStyle(color: topOnly ? Colors.white : pal.text, fontWeight: FontWeight.w600, fontSize: 12),
                onSelected: (_) => setState(() => _topOnly = true),
              ),
            ChoiceChip(
              key: const ValueKey('rev-all'),
              label: Text(l.revAllDiffs(diffs.length)),
              selected: !topOnly,
              labelStyle: TextStyle(color: !topOnly ? Colors.white : pal.text, fontWeight: FontWeight.w600, fontSize: 12),
              onSelected: (_) => setState(() => _topOnly = false),
            ),
          ],
        ),
        const SizedBox(height: 6),
        // Column в SingleChildScrollView, а не ListView: AlertDialog меряет содержимое по ширине (IntrinsicWidth), ленивый список так не умеет
        ConstrainedBox(
          constraints: const BoxConstraints(maxHeight: 220),
          child: SingleChildScrollView(
            key: const ValueKey('rev-list'),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                for (final r in shown)
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 3),
                    child: Row(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(r.name, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                              Text(r.diff < 0 ? l.shortBy(qtyText(-r.diff)) : l.extraBy(qtyText(r.diff)), style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
                            ],
                          ),
                        ),
                        if (auditAmountText(r, som) case final amount?)
                          Padding(
                            padding: const EdgeInsets.only(left: 8),
                            child: Text(amount, style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: r.diff < 0 ? pal.danger : pal.success)),
                          ),
                      ],
                    ),
                  ),
              ],
            ),
          ),
        ),
        if (anyStar)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Text(l.revStarNote, key: const ValueKey('rev-star'), style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
          ),
      ],
    );
  }
}
