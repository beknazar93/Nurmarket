// 2026-10-29 (владелец: «в мобилке сделай инфографику лучше»): данные инфографики — сводка с прошлым периодом, пропущенные дни,
// доли, остатки, раскладка ящика смены. Сервер не трогается — репозиторий фейковый; выручку сервер считает сам (paid + partially_returned).
import 'package:flutter/material.dart' show DateUtils;
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/ui/infographic/infographic_data.dart';
import 'package:nurmarket/features/admin/analytics.dart';
import 'package:nurmarket/features/shift/shift_repository.dart';

class _Offline extends ApiClient {
  _Offline() : super(SessionStore());
}

class _Repo extends AnalyticsRepository {
  final List<(DateTime, DateTime)> calls = [];
  final bool failPrev;
  _Repo({this.failPrev = false}) : super(_Offline());
  @override
  Future<SalesSummary> sales(DateTime from, DateTime to) async {
    calls.add((from, to));
    final today = DateUtils.dateOnly(DateTime.now());
    final isPrev = to.isBefore(today);
    if (isPrev && failPrev) throw ApiException('нет связи');
    return SalesSummary(
      isPrev ? 1000 : 1200,
      isPrev ? 10 : 12,
      100,
      isPrev ? 300 : 360,
      [(DateUtils.dateOnly(from), isPrev ? 1000 : 1200)],
      const {'cash': 700, 'card': 500},
    );
  }

  @override
  Future<List<ProductSales>> products(DateTime from, DateTime to) async => const [ProductSales('Вода', 5, 100, 10)];
}

class _ZeroRepo extends AnalyticsRepository {
  _ZeroRepo() : super(_Offline());
  @override
  Future<SalesSummary> sales(DateTime from, DateTime to) async => const SalesSummary(0, 0, 0, 0, [], {});
  @override
  Future<List<ProductSales>> products(DateTime from, DateTime to) async => const [];
}

void main() {
  final today = DateUtils.dateOnly(DateTime.now());

  group('период сводки', () {
    test('сегодня / неделя / месяц как раньше, свой — по порядку дат', () {
      expect(summaryRange('today'), (today, today));
      expect(summaryRange('week').$1, today.subtract(const Duration(days: 6)));
      expect(summaryRange('month').$1, DateTime(today.year, today.month, 1));
      final c = summaryRange('custom', custom: (DateTime(2026, 10, 20, 15), DateTime(2026, 10, 10)));
      expect(c, (DateTime(2026, 10, 10), DateTime(2026, 10, 20)));
      expect(summaryRange('custom'), (today, today), reason: 'свой без дат — сегодня');
    });

    test('пропущенные дни заполняются нулями, дубли дня складываются', () {
      final d = fillDays([(DateTime(2026, 10, 21), 50), (DateTime(2026, 10, 21, 12), 25), (DateTime(2026, 10, 23), 10)], DateTime(2026, 10, 20), DateTime(2026, 10, 24));
      expect(d.length, 5);
      expect([for (final x in d) x.$2], [0, 75, 0, 10, 0]);
    });

    test('слишком длинный диапазон обрезается до последних дней; пустой — пусто', () {
      expect(fillDays(const [], DateTime(2000), DateTime(2026, 10, 29)).length, 400);
      expect(fillDays(const [], DateTime(2026, 10, 5), DateTime(2026, 10, 1)), isEmpty);
      expect(fillDays(const [], DateTime(2026, 10, 5), DateTime(2026, 10, 5)).length, 1);
    });
  });

  group('загрузка сводки', () {
    test('без сравнения — один отчёт за период, прошлого нет', () async {
      final repo = _Repo();
      final b = await loadSummaryBundle(repo, 'month');
      expect(b.previous, isNull);
      expect(b.previousAligned, isNull);
      expect(b.current.revenue, 1200);
      expect(repo.calls.length, 1);
      expect(b.cost, 840);
      expect(b.marginPercent, closeTo(30, 1e-9));
    });

    test('за «сегодня» диаграмма — неделя (7 дней без пропусков), цифры — за сегодня', () async {
      final b = await loadSummaryBundle(_Repo(), 'today');
      expect(b.from, today);
      expect(b.chartDays.length, 7);
      expect(b.chartDays.last.$1, today);
    });

    test('со сравнением: прошлый период запрошен и выровнен по дням', () async {
      final repo = _Repo();
      final b = await loadSummaryBundle(repo, 'week', compare: true);
      expect(b.previous!.revenue, 1000);
      expect(b.prevTo, b.from.subtract(const Duration(days: 1)));
      expect(b.previousAligned!.length, 7);
      expect(b.previousAligned!.first, 1000);
    });

    test('если прошлый период не загрузился — сравнения нет, ошибки нет', () async {
      final b = await loadSummaryBundle(_Repo(failPrev: true), 'week', compare: true);
      expect(b.previous, isNull);
      expect(b.prevFrom, isNull);
      expect(b.current.revenue, 1200);
    });

    test('нулевая выручка: рентабельности нет, себестоимость 0', () async {
      final b = await loadSummaryBundle(_ZeroRepo(), 'month');
      expect(b.marginPercent, isNull);
      expect(b.cost, 0);
    });
  });

  group('доли', () {
    test('топ: доля от суммы ВСЕХ, полоса от лучшего, мусор отброшен', () {
      final r = topShares([('a', 50), ('b', 25), ('c', 25), ('z', 0), ('n', double.nan)], n: 2);
      expect(r.length, 2);
      expect(r[0].share, 0.5);
      expect(r[0].relToTop, 1);
      expect(r[1].share, 0.25);
      expect(r[1].relToTop, 0.5);
      expect(topShares(const []), isEmpty);
      expect(topShares([('a', -1)]), isEmpty);
    });
    test('концентрация долга', () {
      expect(concentration([100, 50, 30, 20]), closeTo(180 / 200, 1e-9));
      expect(concentration(const []), 0);
      expect(concentration([0, 0]), 0);
      expect(concentration([100], n: 3), 1);
    });
  });

  group('остатки', () {
    test('классы как на плитке «Заканчивается»', () {
      expect(stockLevel(quantity: 0, minQuantity: 0, isService: false), StockLevel.out);
      expect(stockLevel(quantity: -2, minQuantity: 0, isService: false), StockLevel.out);
      expect(stockLevel(quantity: 9.9, minQuantity: 0, isService: false), StockLevel.low);
      expect(stockLevel(quantity: 10, minQuantity: 0, isService: false), StockLevel.ok);
      expect(stockLevel(quantity: 20, minQuantity: 20, isService: false), StockLevel.low, reason: 'минимум товара');
      expect(stockLevel(quantity: 21, minQuantity: 20, isService: false), StockLevel.ok);
      expect(stockLevel(quantity: 0, minQuantity: 5, isService: true), StockLevel.service);
    });
    test('счётчики; услуги не считаются; «заканчивается» = нет + мало', () {
      final h = StockHealth.of([
        (quantity: 0.0, minQuantity: 0.0, isService: false),
        (quantity: 3.0, minQuantity: 0.0, isService: false),
        (quantity: 50.0, minQuantity: 0.0, isService: false),
        (quantity: 0.0, minQuantity: 0.0, isService: true),
      ]);
      expect((h.out, h.low, h.ok, h.total, h.runningOut), (1, 1, 1, 3, 2));
      expect(StockHealth.of(const []).total, 0);
    });
  });

  group('смена', () {
    test('доли продаж; отрицательное и ноль не ломают', () {
      expect(const SalesSplit(50, 30, 20).fractions, [0.5, 0.3, 0.2]);
      expect(const SalesSplit(0, 0, 0).fractions, isEmpty);
      expect(SalesSplit.of(const ShiftBreakdown(cashSales: -5, nonCashSales: 10)).fractions, [0, 1, 0]);
    });
    test('части ящика: знаки и сумма совпадают с расчётом смены и сервером', () {
      const b = ShiftBreakdown(
        opening: 1000,
        cashSales: 5000,
        debtPaymentsCash: 200,
        inflow: 300,
        outflow: 100,
        returnsCash: 400,
        returnsTotal: 400,
        returnsCount: 1,
        serverExpected: 6000,
      );
      final steps = drawerSteps(b);
      expect([for (final s in steps) s.signed], [1000, 5000, 200, 300, -400, -100]);
      expect(drawerStepsSum(steps), 6000);
      expect(drawerStepsSum(steps), b.calcExpected);
      expect(b.matches, isTrue);
    });
    test('неизвестные части не заменяются нулём и не входят в сумму', () {
      const b = ShiftBreakdown(opening: 100, cashSales: 50, serverExpected: 150);
      final steps = drawerSteps(b);
      expect(steps.where((s) => !s.known).length, 4);
      expect(drawerStepsSum(steps), 150);
    });
  });
}
