import 'dart:math' as math;

import 'package:flutter/material.dart' show DateUtils;

import '../../../features/admin/analytics.dart';
import '../../../features/shift/shift_repository.dart';
import '../../util/product_picker.dart' show isLowStock;
import 'chart_logic.dart';

// 2026-10-29 (владелец: «в мобилке сделай инфографику лучше»): ДАННЫЕ инфографики без отрисовки. Источники и формулы те же,
// что у программы владельца на компьютере: отчёт сервера api/main/analytics/market/ (выручка = оплаченные + с частичным
// возвратом — считает сервер, телефон её не пересчитывает), «Должно быть в кассе» — число сервера. Здесь только раскладка
// готовых чисел по долям, периодам и классам. Репозитории продаж, смены и синхронизации не затронуты.

// ------------------------------------------------------------------ период сводки

/// Режим периода сводки: 'today' | 'week' | 'month' | 'custom'. Для 'custom' нужен [custom].
(DateTime, DateTime) summaryRange(String period, {(DateTime, DateTime)? custom}) {
  if (period == 'custom' && custom != null) {
    final a = DateUtils.dateOnly(custom.$1), b = DateUtils.dateOnly(custom.$2);
    return a.isAfter(b) ? (b, a) : (a, b);
  }
  return periodRange(period == 'custom' ? 'today' : period);
}

/// Каждый день диапазона [from]…[to] (включительно) — пропущенные дни (сервер не присылает дни без продаж) получают 0.
/// Больше [maxDays] дней не строим (защита от случайного «с 2000 года»): берутся последние [maxDays].
List<(DateTime, double)> fillDays(List<(DateTime, double)> byDay, DateTime from, DateTime to, {int maxDays = 400}) {
  var f = DateUtils.dateOnly(from);
  final t = DateUtils.dateOnly(to);
  if (t.isBefore(f)) return const [];
  final span = t.difference(f).inDays + 1;
  if (span > maxDays) f = DateTime(t.year, t.month, t.day - maxDays + 1);
  final sums = <DateTime, double>{};
  for (final p in byDay) {
    final k = DateUtils.dateOnly(p.$1);
    sums[k] = (sums[k] ?? 0) + p.$2;
  }
  final out = <(DateTime, double)>[];
  for (var d = f; !d.isAfter(t); d = DateTime(d.year, d.month, d.day + 1)) {
    out.add((d, sums[d] ?? 0));
  }
  return out;
}

/// Всё, что нужно экрану «Сводка» за один раз: текущий период, прошлый (если просили сравнить), товары, время обновления.
class SummaryBundle {
  final String period;
  final DateTime from;
  final DateTime to;
  final SalesSummary current;

  /// Выручка по дням для диаграммы (за «сегодня» — неделя, чтобы было с чем сравнить), без пропусков.
  final List<(DateTime, double)> chartDays;
  final SalesSummary? previous;
  final DateTime? prevFrom;
  final DateTime? prevTo;
  final List<ProductSales> products;
  final DateTime updatedAt;
  const SummaryBundle({
    required this.period,
    required this.from,
    required this.to,
    required this.current,
    required this.chartDays,
    required this.products,
    required this.updatedAt,
    this.previous,
    this.prevFrom,
    this.prevTo,
  });

  /// Прошлая выручка по дням, выровненная по номеру дня (первый день к первому): для призрачных столбцов «к прошлому периоду».
  /// null, если сравнения нет или режим «сегодня» (там диаграмма — неделя, а сравнение — один вчерашний день).
  List<double>? get previousAligned {
    if (previous == null || prevFrom == null || prevTo == null || period == 'today') return null;
    final days = fillDays(previous!.byDay, prevFrom!, prevTo!);
    return [for (final d in days) d.$2];
  }

  /// Себестоимость проданного = выручка − валовая прибыль сервера (не меньше 0).
  double get cost => math.max(0, current.revenue - current.profit);

  /// Рентабельность, % от выручки; null при нулевой выручке.
  double? get marginPercent => current.revenue > 0 ? current.profit / current.revenue * 100 : null;
}

/// Загрузка сводки теми же запросами, что и раньше [SummaryScreen], плюс прошлый период при [compare]. Ошибка сервера
/// пробрасывается как есть; если упал только запрос прошлого периода — сравнение просто не показывается.
Future<SummaryBundle> loadSummaryBundle(
  AnalyticsRepository repo,
  String period, {
  (DateTime, DateTime)? custom,
  bool compare = false,
  DateTime? now,
}) async {
  final (pf, pt) = summaryRange(period, custom: custom);
  final s = await repo.sales(pf, pt);
  // по дням — хотя бы неделя (как было): за «сегодня» диаграмма показывает последние 7 дней
  final (cf, ct) = period == 'today' ? periodRange('week') : (pf, pt);
  final chart = period == 'today' ? await repo.sales(cf, ct) : s;
  final products = await repo.products(pf, pt);
  SalesSummary? prev;
  ({DateTime from, DateTime to})? pr;
  if (compare) {
    pr = previousPeriod(period, pf, pt);
    try {
      prev = await repo.sales(pr.from, pr.to);
    } catch (_) {
      prev = null; // прошлый период не загрузился — сравнение молча не показываем
    }
  }
  return SummaryBundle(
    period: period,
    from: pf,
    to: pt,
    current: SalesSummary(s.revenue, s.checks, s.avg, s.profit, s.byDay, s.payments),
    chartDays: fillDays(chart.byDay, cf, ct),
    products: products,
    updatedAt: now ?? DateTime.now(),
    previous: prev,
    prevFrom: prev == null ? null : pr!.from,
    prevTo: prev == null ? null : pr!.to,
  );
}

// ------------------------------------------------------------------ доли

/// Строка рейтинга с долей: [share] — доля от суммы всех (0…1), [relToTop] — от лучшей строки (для длины полосы).
class ShareRow {
  final String key;
  final double value;
  final double share;
  final double relToTop;
  const ShareRow(this.key, this.value, this.share, this.relToTop);
}

/// Топ-[n] по убыванию значения; доля считается от суммы ВСЕХ положительных, а не только показанных. Нули и мусор отброшены.
List<ShareRow> topShares(List<(String, double)> items, {int n = 10}) {
  final pos = [
    for (final i in items)
      if (!i.$2.isNaN && !i.$2.isInfinite && i.$2 > 0) i,
  ]..sort((a, b) => b.$2.compareTo(a.$2));
  final total = pos.fold<double>(0, (a, b) => a + b.$2);
  if (total <= 0) return const [];
  final top = pos.first.$2;
  return [for (final i in pos.take(n)) ShareRow(i.$1, i.$2, i.$2 / total, i.$2 / top)];
}

/// Концентрация долга: доля первых [n] должников в общей сумме (0…1). Пусто/нули → 0.
double concentration(List<double> values, {int n = 3}) {
  final pos = [
    for (final v in values)
      if (v > 0 && !v.isInfinite && !v.isNaN) v,
  ]..sort((a, b) => b.compareTo(a));
  final total = pos.fold<double>(0, (a, b) => a + b);
  if (total <= 0) return 0;
  return pos.take(n).fold<double>(0, (a, b) => a + b) / total;
}

// ------------------------------------------------------------------ остатки

enum StockLevel { out, low, ok, service }

/// Класс остатка товара — те же пороги, что на плитке «Заканчивается» и в фильтрах склада (isLowStock): услуга — отдельно;
/// остаток ≤ 0 — «нет»; ниже порога/минимума товара — «заканчивается»; иначе — «хватает».
StockLevel stockLevel({required double quantity, required double minQuantity, required bool isService}) {
  if (isService) return StockLevel.service;
  if (quantity <= 0) return StockLevel.out;
  return isLowStock(quantity, minQuantity) ? StockLevel.low : StockLevel.ok;
}

class StockHealth {
  final int out;
  final int low;
  final int ok;
  const StockHealth(this.out, this.low, this.ok);

  int get total => out + low + ok;

  /// Как число на плитке «Заканчивается» главной и «Управления»: нет + заканчивается.
  int get runningOut => out + low;

  static StockHealth of(Iterable<({double quantity, double minQuantity, bool isService})> items) {
    var o = 0, l = 0, k = 0;
    for (final i in items) {
      switch (stockLevel(quantity: i.quantity, minQuantity: i.minQuantity, isService: i.isService)) {
        case StockLevel.out:
          o++;
        case StockLevel.low:
          l++;
        case StockLevel.ok:
          k++;
        case StockLevel.service:
          break;
      }
    }
    return StockHealth(o, l, k);
  }
}

// ------------------------------------------------------------------ смена: продажи и ящик

/// Доли продаж смены по способу оплаты (наличные/безнал/долг) от суммы трёх. Нули → пустой список.
class SalesSplit {
  final double cash;
  final double nonCash;
  final double debt;
  const SalesSplit(this.cash, this.nonCash, this.debt);

  double get total => cash + nonCash + debt;

  static SalesSplit of(ShiftBreakdown b) => SalesSplit(math.max(0, b.cashSales), math.max(0, b.nonCashSales), math.max(0, b.debtSales));

  List<double> get fractions => total <= 0 ? const [] : [cash / total, nonCash / total, debt / total];
}

enum DrawerPart { opening, cashSales, debtPayments, inflow, returnsCash, outflow }

/// Одна часть «Должно быть в кассе»: со знаком (+ приход, − расход). [known] == false — часть не загружена (не выдумываем 0).
class DrawerStep {
  final DrawerPart part;
  final double signed;
  final bool known;
  const DrawerStep(this.part, this.signed, this.known);
}

/// Раскладка «Должно быть в кассе» по частям — те же числа и знаки, что в [ShiftBreakdown.calcExpected] и в ленте X/Z.
List<DrawerStep> drawerSteps(ShiftBreakdown b) => [
  DrawerStep(DrawerPart.opening, b.opening, true),
  DrawerStep(DrawerPart.cashSales, b.cashSales, true),
  DrawerStep(DrawerPart.debtPayments, b.debtPaymentsCash ?? 0, b.debtPaymentsCash != null),
  DrawerStep(DrawerPart.inflow, b.inflow ?? 0, b.inflow != null),
  DrawerStep(DrawerPart.returnsCash, -(b.returnsCash ?? 0), b.returnsCash != null),
  DrawerStep(DrawerPart.outflow, -(b.outflow ?? 0), b.outflow != null),
];

/// Сумма известных частей; сравнивать с числом сервера [ShiftBreakdown.serverExpected] — как [ShiftBreakdown.matches].
double drawerStepsSum(List<DrawerStep> steps) => steps.where((s) => s.known).fold<double>(0, (a, s) => a + s.signed);
