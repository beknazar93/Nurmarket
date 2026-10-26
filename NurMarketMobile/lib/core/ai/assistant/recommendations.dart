// 2026-10-26 (голос-управление, В2): рекомендации по товарам — чистые функции без сети и без экранов (раздел 8.3 ТЗ).
// Считает телефон из данных магазина: каталог (остатки, цены) и отчёт продаж (`tab=products`). Пороги — константы ниже;
// менять их — вопрос владельцу №10 («запас на 14 дней», «неликвид — 60 дней», «мин. наценка 20 %» как на ПК).
import 'dart:math' as math;

import '../../../features/admin/analytics.dart' show ProductSales;

/// Запас, меньше которого товар «заканчивается» (дней продаж).
const recCoverDays = 7;

/// На сколько дней продаж заказывать (как `_storeContext` советника и ПК).
const recOrderDays = 14;

/// Окно продаж для расчёта «в день».
const recSalesDays = 30;

/// Неликвид: остаток есть, а продаж за столько дней нет.
const recDeadDays = 60;

/// Минимальная наценка, % от закупки (ПК: ProductActionPlan, округление вверх).
const recMinMarkupPct = 20.0;

/// «Продвигать»: наценка от этого процента.
const recPromoMarkupPct = 30.0;

/// Товар каталога телефона (то, что нужно рекомендациям; не зависит от базы).
class RecStock {
  final String id;
  final String name;
  final String unit;
  final double qty;
  final double price;
  final double minQty;
  const RecStock(this.id, this.name, this.unit, this.qty, this.price, {this.minQty = 0});
}

String recKey(String name) => name.trim().toLowerCase();

class ReorderRow {
  final RecStock item;

  /// Продаётся штук в день за окно.
  final double perDay;

  /// На сколько дней хватит (0 — закончился).
  final double daysLeft;

  /// Сколько заказать, чтобы хватило на [recOrderDays] дней.
  final int order;
  const ReorderRow(this.item, this.perDay, this.daysLeft, this.order);
}

/// Что заказать: продавался за последние [days] дней, и либо закончился, либо хватит меньше [recCoverDays] дней.
/// Сначала закончившиеся, потом по числу дней запаса. Услуги не считаются.
List<ReorderRow> reorderList(List<RecStock> stock, List<ProductSales> sold, {int days = recSalesDays}) {
  final byName = {for (final p in stock) recKey(p.name): p};
  final out = <ReorderRow>[];
  final seen = <String>{};
  for (final s in sold) {
    final p = byName[recKey(s.name)];
    if (p == null || s.qty <= 0 || !seen.add(p.id)) continue;
    final perDay = s.qty / days;
    final have = p.qty > 0 ? p.qty : 0.0;
    final left = have / perDay;
    if (p.qty > 0 && left >= recCoverDays) continue;
    final order = math.max(0, (perDay * recOrderDays - have).ceil());
    out.add(ReorderRow(p, perDay, p.qty > 0 ? left : 0, order));
  }
  out.sort((a, b) {
    final c = a.daysLeft.compareTo(b.daysLeft);
    return c != 0 ? c : b.perDay.compareTo(a.perDay);
  });
  return out;
}

class DeadRow {
  final RecStock item;

  /// Деньги, замороженные в остатке, по цене продажи (закупки в каталоге телефона нет).
  final double frozen;
  const DeadRow(this.item, this.frozen);
}

/// Неликвид: остаток больше нуля, а за [days] дней продаж нет. Сначала где заморожено больше.
List<DeadRow> deadStock(List<RecStock> stock, List<ProductSales> sold) {
  final sellers = {for (final s in sold) if (s.qty > 0) recKey(s.name)};
  final out = [
    for (final p in stock)
      if (p.qty > 0 && !sellers.contains(recKey(p.name))) DeadRow(p, p.qty * p.price),
  ]..sort((a, b) => b.frozen.compareTo(a.frozen));
  return out;
}

class MarkupRow {
  final RecStock item;
  final double purchase;
  final double price;

  /// Наценка к закупке, %.
  final double markup;

  /// Цена с минимальной наценкой, округлённая вверх.
  final double suggested;
  const MarkupRow(this.item, this.purchase, this.price, this.markup, this.suggested);
}

double suggestedPrice(double purchase, [double minPct = recMinMarkupPct]) => (purchase * (1 + minPct / 100)).ceilToDouble();

/// Низкая наценка: цена ниже закупки × 1,2 (в том числе ниже закупки). Закупка — из отчёта продаж (`purchase_price`), цена —
/// из каталога (нет в каталоге — средняя цена продажи). Сначала самая низкая наценка.
List<MarkupRow> lowMarkup(List<RecStock> stock, List<ProductSales> sold, {double minPct = recMinMarkupPct}) {
  final byName = {for (final p in stock) recKey(p.name): p};
  final out = <MarkupRow>[];
  final seen = <String>{};
  for (final s in sold) {
    if (s.purchase <= 0 || !seen.add(recKey(s.name))) continue;
    final p = byName[recKey(s.name)];
    final price = p != null && p.price > 0 ? p.price : (s.qty > 0 ? s.revenue / s.qty : 0.0);
    if (price <= 0) continue;
    final markup = (price - s.purchase) / s.purchase * 100;
    if (markup >= minPct - 1e-9) continue;
    out.add(MarkupRow(p ?? RecStock('', s.name, 'шт', 0, price), s.purchase, price, markup, suggestedPrice(s.purchase, minPct)));
  }
  out.sort((a, b) => a.markup.compareTo(b.markup));
  return out;
}

class PromoRow {
  final RecStock item;
  final double markup;
  final double qty;

  /// Рост продаж к прошлому такому же окну, %; null — раньше не продавался.
  final double? growth;
  const PromoRow(this.item, this.markup, this.qty, this.growth);
}

/// Что продвигать: наценка от [recPromoMarkupPct] и продажи растут (последние окно к прошлому такому же) либо высокая
/// наценка при спросе не ниже среднего. Сначала где рост больше, потом по числу проданных.
List<PromoRow> promoteList(List<RecStock> stock, List<ProductSales> current, List<ProductSales> previous) {
  final prev = {for (final s in previous) recKey(s.name): s.qty};
  final byName = {for (final p in stock) recKey(p.name): p};
  final sellers = current.where((s) => s.qty > 0).toList();
  if (sellers.isEmpty) return const [];
  final avg = sellers.fold(0.0, (a, s) => a + s.qty) / sellers.length;
  final out = <PromoRow>[];
  for (final s in sellers) {
    if (s.purchase <= 0) continue;
    final p = byName[recKey(s.name)];
    final price = p != null && p.price > 0 ? p.price : s.revenue / s.qty;
    final markup = (price - s.purchase) / s.purchase * 100;
    if (markup < recPromoMarkupPct) continue;
    final before = prev[recKey(s.name)] ?? 0;
    final growth = before > 0 ? (s.qty - before) / before * 100 : null;
    final growing = growth != null && growth > 0;
    if (!growing && s.qty < avg) continue;
    out.add(PromoRow(p ?? RecStock('', s.name, 'шт', 0, price), markup, s.qty, growth));
  }
  out.sort((a, b) {
    final c = (b.growth ?? -1e9).compareTo(a.growth ?? -1e9);
    return c != 0 ? c : b.qty.compareTo(a.qty);
  });
  return out;
}
