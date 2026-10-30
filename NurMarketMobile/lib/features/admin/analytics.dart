import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/ui/link_banner.dart';
import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/ui/infographic/chart_logic.dart';
import '../../core/ui/infographic/infographic_data.dart';
import '../../core/ui/infographic/infographics.dart';
import '../../core/util/ui.dart';

/// 13.10, владелец: «ABC-анализ, диаграмма Парето и сводка — в мобилку». Цифры — из отчётов сервера, как у программы
/// владельца на компьютере (FinanceWindow, OwnerShellWindow): GET api/main/analytics/market/?tab=sales (выручка, чеки,
/// средний чек, прибыль, выручка по дням, способы оплаты) и ?tab=products (продажи по товарам: штуки, выручка, закупка).
class SalesSummary {
  final double revenue;
  final int checks;
  final double avg;
  final double profit;
  final List<(DateTime, double)> byDay;
  final Map<String, double> payments;
  const SalesSummary(this.revenue, this.checks, this.avg, this.profit, this.byDay, this.payments);
}

class ProductSales {
  final String name;
  final double qty;
  final double revenue;
  final double purchase;
  const ProductSales(this.name, this.qty, this.revenue, this.purchase);
  double get profit => revenue - qty * purchase;
}

class AnalyticsRepository {
  final ApiClient api;
  AnalyticsRepository(this.api);

  static String _d(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  Future<Map<String, dynamic>> _report(String tab, DateTime from, DateTime to) async {
    final data = await api.get(
      'api/main/analytics/market/',
      query: {
        'tab': tab,
        'period_start': _d(from),
        'period_end': _d(to),
        // 2026-10-22 (мобилка, деньги): сервер во вкладке «Товары» отдаёт только 20 строк top_by_revenue — ABC считался
        // по 20 товарам. Как касса (SalesApiService.ReportCache) и сайт: limit — все проданные товары периода.
        if (tab == 'products') 'limit': '10000',
      },
      timeout: const Duration(seconds: 40),
    );
    return data is Map ? data.cast<String, dynamic>() : const {};
  }

  Future<SalesSummary> sales(DateTime from, DateTime to) async {
    final r = await _report('sales', from, to);
    final cards = (r['cards'] is Map ? r['cards'] as Map : const {}).cast<String, dynamic>();
    final charts = (r['charts'] is Map ? r['charts'] as Map : const {}).cast<String, dynamic>();
    final revenue = num0(cards, 'revenue');
    final checks = num0(cards, 'transactions').round();
    final byDay = <(DateTime, double)>[
      for (final p in asList(charts['sales_dynamics']))
        if (DateTime.tryParse(str(p, 'date') ?? '') case final d?) (d, num0(p, 'value')),
    ]..sort((a, b) => a.$1.compareTo(b.$1));
    final payments = <String, double>{};
    for (final m in asList(charts['payment_methods'])) {
      final key = (str(m, 'method') ?? '').toLowerCase();
      payments[key] = (payments[key] ?? 0) + num0(m, 'total');
    }
    return SalesSummary(
      revenue,
      checks,
      num0(cards, 'avg_check') > 0 ? num0(cards, 'avg_check') : (checks > 0 ? revenue / checks : 0),
      num0(cards, 'gross_profit'),
      byDay,
      payments,
    );
  }

  Future<List<ProductSales>> products(DateTime from, DateTime to) async {
    final r = await _report('products', from, to);
    final tables = (r['tables'] is Map ? r['tables'] as Map : const {}).cast<String, dynamic>();
    return [
      for (final row in asList(tables['top_by_revenue']))
        if ((str(row, 'name') ?? '').isNotEmpty) ProductSales(str(row, 'name')!, num0(row, 'qty_sold'), num0(row, 'revenue'), num0(row, 'purchase_price')),
    ];
  }
}

final analyticsRepoProvider = Provider((ref) => AnalyticsRepository(ref.watch(apiProvider)));

(DateTime, DateTime) periodRange(String period) {
  final today = DateUtils.dateOnly(DateTime.now());
  return switch (period) {
    'week' => (today.subtract(const Duration(days: 6)), today),
    'month' => (DateTime(today.year, today.month, 1), today),
    _ => (today, today),
  };
}

Widget periodChips(BuildContext context, String value, ValueChanged<String> onChanged) {
  final l = context.l;
  return SegmentedButton<String>(
    segments: [
      ButtonSegment(value: 'today', label: Text(l.periodToday)),
      ButtonSegment(value: 'week', label: Text(l.periodWeek)),
      ButtonSegment(value: 'month', label: Text(l.periodMonth)),
    ],
    selected: {value},
    onSelectionChanged: (s) => onChanged(s.first),
  );
}

// 2026-10-22 (мобилка, админ, П14): названия способов оплаты — на языке приложения (было только по-русски).
String paymentName(BuildContext context, String key) => switch (key) {
  'cash' => context.l.cash,
  'card' => context.l.card,
  'transfer' || 'qr' || 'mbank' => context.l.payTransferQr,
  'debt' => context.l.debt,
  'mixed' => context.l.mixed,
  _ => key.isEmpty ? '—' : key,
};

/// 2026-10-22 (мобилка, админ, П14): отчёт не загрузился — понятный текст на языке приложения и «Повторить»
/// (было сырое сообщение исключения красным). Подробность сервера — мелко ниже, для поддержки.
Widget reportError(BuildContext context, Object? error, VoidCallback retry) => Padding(
  padding: const EdgeInsets.all(24),
  child: Column(
    children: [
      Icon(Icons.cloud_off_rounded, size: 40, color: context.pal.textSoft),
      const SizedBox(height: 8),
      Text(
        context.l.reportLoadFailed,
        textAlign: TextAlign.center,
        style: const TextStyle(fontWeight: FontWeight.w700),
      ),
      if (error is ApiException && error.message.isNotEmpty)
        Padding(
          padding: const EdgeInsets.only(top: 4),
          child: Text(
            error.message,
            textAlign: TextAlign.center,
            style: TextStyle(fontSize: 11.5, color: context.pal.textSoft),
          ),
        ),
      const SizedBox(height: 12),
      FilledButton.icon(onPressed: retry, icon: const Icon(Icons.refresh_rounded), label: Text(context.l.retry)),
    ],
  ),
);

// ================================================================== Сводка

/// 2026-10-29 (владелец: «инфографику лучше», спека Fable 03-mobile-infographics): сводка на общих компонентах — период и
/// «Сравнить», плитки с дельтой и мини-линией, выручка по дням (касание — карточка), кольцо способов оплаты, прибыль,
/// топ-5. Цифры те же, что у программы владельца: отчёт сервера (выручка = оплаченные + с частичным возвратом).
class SummaryScreen extends ConsumerStatefulWidget {
  const SummaryScreen({super.key});
  @override
  ConsumerState<SummaryScreen> createState() => _SummaryScreenState();
}

class _SummaryScreenState extends ConsumerState<SummaryScreen> {
  String _period = 'today';
  (DateTime, DateTime)? _custom;
  bool _compare = false;
  Future<SummaryBundle>? _future;
  SummaryBundle? _last;

  @override
  void initState() {
    super.initState();
    _load();
  }

  void _load() {
    final repo = ref.read(analyticsRepoProvider);
    final f = loadSummaryBundle(repo, _period, custom: _custom, compare: _compare);
    setState(() {
      _future = f;
    });
    f.then((b) {
      if (mounted && identical(_future, f)) _last = b;
    }, onError: (_) {});
  }

  Future<void> _pickPeriod(String v) async {
    if (v == 'custom') {
      final now = DateUtils.dateOnly(DateTime.now());
      final r = await showDateRangePicker(
        context: context,
        firstDate: DateTime(now.year - 3),
        lastDate: now,
        initialDateRange: _custom == null ? null : DateTimeRange(start: _custom!.$1, end: _custom!.$2),
        helpText: context.l.infRangePick,
      );
      if (r == null || !mounted) return;
      _custom = (r.start, r.end);
    }
    _period = v;
    _load();
  }

  static String _dm(DateTime x) => '${x.day.toString().padLeft(2, '0')}.${x.month.toString().padLeft(2, '0')}';

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final customLabel = _period == 'custom' && _custom != null ? '${_dm(_custom!.$1)}–${_dm(_custom!.$2)}' : null;
    return Scaffold(
      appBar: ScreenTitle(l.summaryTitle),
      body: RefreshIndicator(
        onRefresh: () async => _load(),
        child: FutureBuilder<SummaryBundle>(
          future: _future,
          builder: (c, snap) {
            const gap = SizedBox(height: 12);
            final children = <Widget>[
              PeriodBar(
                period: _period,
                onPeriod: _pickPeriod,
                compare: _compare,
                onCompare: (v) {
                  _compare = v;
                  _load();
                },
                customLabel: customLabel,
              ),
              gap,
            ];
            if (snap.connectionState != ConnectionState.done) {
              children.addAll([
                KpiTile(label: l.kpiRevenue, hero: true),
                gap,
                InfoCardSkeleton(title: l.revenueByDay),
                gap,
                InfoCardSkeleton(title: l.paymentMethods),
              ]);
            } else if (snap.hasError && _last == null) {
              final e = snap.error;
              children.add(InfoError(onRetry: _load, lastData: _last?.updatedAt, detail: e is ApiException ? e.message : null));
            } else {
              // 2026-10-29 (аудит мобилки, п. 5): ошибка при обновлении — остаются последние цифры и плашка «данные от ЧЧ:ММ»
              final b = snap.hasError ? _last! : snap.data!;
              if (snap.hasError) {
                children.addAll([
                  LinkBanner(force: true, dataAt: b.updatedAt, detail: snap.error is ApiException ? (snap.error as ApiException).message : null),
                  gap,
                ]);
              }
              final s = b.current;
              final vs = versusLabel(l, _period);
              final p = b.previous;
              KpiCompare cmp(double cur, double? prev) => KpiCompare(on: _compare, delta: _compare && p != null ? DeltaInfo.of(cur, prev) : null, vs: vs);
              final days = b.chartDays;
              final (best, _) = RevenueDayBars.marks(days);
              final chartTotal = days.fold<double>(0, (a, d) => a + d.$2);
              final bars = chartTotal <= 0 && s.revenue <= 0
                  ? <ChartBar>[]
                  : [for (final d in days) ChartBar(days.length > 14 ? '${d.$1.day}' : _dm(d.$1), _dm(d.$1), d.$2)];
              final slices = [for (final e in s.payments.entries) Slice(e.key, paymentName(context, e.key), e.value)];
              final topRows = (b.products.toList()..sort((x, y) => y.revenue.compareTo(x.revenue))).where((x) => x.revenue > 0).take(5).toList();
              final topColors = ChartColors.assign([for (final x in topRows) x.name]);
              final range = b.from == b.to ? _dm(b.from) : '${_dm(b.from)} – ${_dm(b.to)}';
              children.addAll([
                Align(
                  alignment: Alignment.centerRight,
                  child: Text('$range · ${l.infUpdatedAt(TimeOfDay.fromDateTime(b.updatedAt).format(context))}', style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
                ),
                const SizedBox(height: 6),
                KpiTile(
                  label: l.kpiRevenue,
                  value: kpiMoney(context, s.revenue),
                  unit: l.som,
                  hero: true,
                  how: l.infHowRevenue,
                  compare: cmp(s.revenue, p?.revenue),
                  spark: [for (final d in days) d.$2],
                  sparkColor: T.accent,
                ),
                gap,
                KpiGrid([
                  KpiTile(label: l.kpiChecks, value: '${s.checks}', how: l.infHowChecks, compare: cmp(s.checks.toDouble(), p?.checks.toDouble())),
                  KpiTile(label: l.kpiAvg, value: kpiMoney(context, s.avg), unit: l.som, how: l.infHowAvg, compare: cmp(s.avg, p?.avg)),
                  KpiTile(label: l.kpiProfit, value: kpiMoney(context, s.profit), unit: l.som, how: l.infHowProfit, compare: cmp(s.profit, p?.profit)),
                ]),
                gap,
                BarChartCard(
                  title: l.revenueByDay,
                  subtitle: l.som,
                  semanticsName: l.revenueByDay,
                  unit: l.som,
                  bars: bars,
                  previous: _compare ? b.previousAligned : null,
                  best: best,
                  how: l.infHowRevenue,
                ),
                gap,
                DonutCard(title: l.paymentMethods, subtitle: range, semanticsName: l.paymentMethods, unit: l.som, slices: slices, how: l.infHowPayments),
                if (s.revenue > 0) ...[gap, _ProfitCard(b)],
                gap,
                InfoCard(
                  title: l.topProducts,
                  subtitle: l.som,
                  how: l.infHowTop,
                  child: topRows.isEmpty
                      ? InfoEmpty(title: l.noSalesPeriod, hint: l.infNoDataHint)
                      : TopBars(rows: [for (var i = 0; i < topRows.length; i++) TopBarRow(topRows[i].name, topRows[i].revenue, topColors[i])], unit: l.som),
                ),
                gap,
                InfoNote(l.infHoursNote),
                gap,
                OutlinedButton.icon(
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                  onPressed: () => context.push('/admin/abc'),
                  icon: const Icon(Icons.bar_chart_rounded),
                  label: Text(l.abcTitle),
                ),
              ]);
            }
            return ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 24), children: children);
          },
        ),
      ),
    );
  }
}

/// Выручка → себестоимость → прибыль одной полосой (расходов магазина в отчёте сервера нет — честная заметка под ней).
class _ProfitCard extends StatelessWidget {
  final SummaryBundle b;
  const _ProfitCard(this.b);
  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final rev = b.current.revenue;
    final profit = b.current.profit;
    final cost = b.cost;
    final fr = rev <= 0 ? const <double>[] : [cost / rev, math.max(0, profit) / rev];
    Widget line(Color c, String label, double v, {bool bold = false}) => ConstrainedBox(
      constraints: const BoxConstraints(minHeight: 36),
      child: Row(
        children: [
          Container(
            width: 12,
            height: 12,
            decoration: BoxDecoration(color: c, borderRadius: BorderRadius.circular(4)),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(label, style: TextStyle(fontSize: 13, fontWeight: bold ? FontWeight.w700 : FontWeight.w500)),
          ),
          Text('${money(v)} ${l.som}', style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, fontFeatures: [FontFeature.tabularFigures()])),
        ],
      ),
    );
    final margin = b.marginPercent;
    return InfoCard(
      title: l.kpiProfit,
      subtitle: margin == null ? null : l.infMargin(margin.toStringAsFixed(1).replaceAll('.', ',')),
      how: l.infHowProfit,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            container: true,
            label: '${l.infRevenueCostProfit}: ${l.kpiRevenue} ${money(rev)} ${l.som}, ${l.infCost} ${money(cost)}, ${l.kpiProfit} ${money(profit)}',
            child: SplitBar(fractions: fr, colors: [T.chartColors[7], pal.success]),
          ),
          const SizedBox(height: 6),
          line(T.chartColors[1], l.kpiRevenue, rev, bold: true),
          line(T.chartColors[7], l.infCost, cost),
          line(pal.success, l.kpiProfit, profit, bold: true),
          const SizedBox(height: 4),
          InfoNote(l.infExpensesNote),
        ],
      ),
    );
  }
}

/// 2026-10-29: «Выручка по дням» — обёртка над [BarChartCard] для старых вызовов и тестов. [marks] и [baseColor] оставлены:
/// лучший день считается так же, как раньше (минимум два дня с продажами, значения не одинаковые).
class RevenueDayBars extends StatelessWidget {
  final List<(DateTime, double)> days;

  /// «Сегодня» — для проверки в тестах; в приложении текущая дата.
  final DateTime? today;
  const RevenueDayBars(this.days, {super.key, this.today});

  /// Номера столбцов: лучшие (все с наибольшей выручкой) и самые слабые (с наименьшей, но не нулевой).
  static (Set<int>, Set<int>) marks(List<(DateTime, double)> days) {
    final sold = [
      for (var i = 0; i < days.length; i++)
        if (days[i].$2 > 0) i,
    ];
    if (sold.length < 2) return ({}, {});
    final hi = sold.map((i) => days[i].$2).reduce(math.max);
    final lo = sold.map((i) => days[i].$2).reduce(math.min);
    if (hi == lo) return ({}, {});
    return (
      {
        for (final i in sold)
          if (days[i].$2 == hi) i,
      },
      sold.length >= 3
          ? {
              for (final i in sold)
                if (days[i].$2 == lo) i,
            }
          : <int>{},
    );
  }

  /// Цвет обычного столбца старого вида (оставлен для тестов и других экранов).
  static Color baseColor(bool dark) => dark ? const Color(0xFF9AA3B2) : T.navy;

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    String dd(DateTime x) => '${x.day.toString().padLeft(2, '0')}.${x.month.toString().padLeft(2, '0')}';
    final (best, _) = marks(days);
    return BarChartCard(
      title: l.revenueByDay,
      subtitle: l.som,
      semanticsName: l.revenueByDay,
      unit: l.som,
      bars: [for (final d in days) ChartBar(days.length > 14 ? '${d.$1.day}' : dd(d.$1), dd(d.$1), d.$2)],
      best: best,
      legendExtra: best.isEmpty
          ? null
          : Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 10,
                  height: 10,
                  decoration: BoxDecoration(color: T.accent, borderRadius: BorderRadius.circular(3)),
                ),
                const SizedBox(width: 6),
                Text(l.chartBestDay, style: TextStyle(fontSize: 11.5, color: context.pal.textSoft)),
              ],
            ),
    );
  }
}

// ================================================================== ABC и Парето

class AbcScreen extends ConsumerStatefulWidget {
  const AbcScreen({super.key});
  @override
  ConsumerState<AbcScreen> createState() => _AbcScreenState();
}

class _AbcScreenState extends ConsumerState<AbcScreen> {
  String _period = 'month';
  String _metric = 'revenue';
  Future<List<ProductSales>>? _future;

  @override
  void initState() {
    super.initState();
    _load();
  }

  void _load() {
    final (from, to) = periodRange(_period);
    setState(() => _future = ref.read(analyticsRepoProvider).products(from, to));
  }

  double _value(ProductSales p) => switch (_metric) {
    'profit' => math.max(0, p.profit),
    'qty' => p.qty,
    _ => p.revenue,
  };

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Scaffold(
      appBar: ScreenTitle(l.abcTitle),
      body: FutureBuilder<List<ProductSales>>(
        future: _future,
        builder: (c, snap) {
          final children = <Widget>[
            periodChips(context, _period, (v) {
              _period = v;
              _load();
            }),
            const SizedBox(height: 6),
            SegmentedButton<String>(
              segments: [
                ButtonSegment(value: 'revenue', label: Text(l.abcByRevenue)),
                ButtonSegment(value: 'profit', label: Text(l.abcByProfit)),
                ButtonSegment(value: 'qty', label: Text(l.abcByQty)),
              ],
              selected: {_metric},
              onSelectionChanged: (s) => setState(() => _metric = s.first),
            ),
          ];
          if (snap.connectionState != ConnectionState.done) {
            children.add(
              const Padding(
                padding: EdgeInsets.all(40),
                child: Center(child: CircularProgressIndicator()),
              ),
            );
          } else if (snap.hasError) {
            children.add(reportError(context, snap.error, _load));
          } else {
            final rows = snap.data!.where((p) => _value(p) > 0).toList()..sort((a, b) => _value(b).compareTo(_value(a)));
            final total = rows.fold(0.0, (s, p) => s + _value(p));
            final groups = <String>[];
            final cumulative = <double>[];
            var acc = 0.0;
            for (final p in rows) {
              final before = total <= 0 ? 0 : acc / total;
              acc += _value(p);
              cumulative.add(total <= 0 ? 0 : acc / total);
              // A — пока накоплено до 80 %, B — до 95 %, остальное — C (товар, на котором перешли порог, остаётся в группе)
              groups.add(before < .8 ? 'A' : (before < .95 ? 'B' : 'C'));
            }
            (int, double) stat(String g) {
              var n = 0;
              var sum = 0.0;
              for (var i = 0; i < rows.length; i++) {
                if (groups[i] == g) {
                  n++;
                  sum += _value(rows[i]);
                }
              }
              return (n, total <= 0 ? 0 : sum / total * 100);
            }

            // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах» — группы ABC как в кассе (BarChartRenderer.AbcColor): A зелёный,
            // B жёлтый, C синий (было тёмный/жёлтый/серый). Столбцы ≥ 3:1 на белом и тёмной панели; для текста — отдельные оттенки.
            Color groupColor(String g) => g == 'A' ? const Color(0xFF16A34A) : (g == 'B' ? const Color(0xFFF2B300) : const Color(0xFF3B82F6));
            // 2026-10-21, владелец: жёлтая буква «B» на белом не читалась — у текста в светлой теме тёмно-жёлтый (контраст ≈ 8:1),
            // столбцы графика остаются жёлтыми. 2026-10-26: так же для A и C — текстом тёмные (светлая тема) или светлые (тёмная) оттенки, ≥ 4,5:1.
            Color groupText(String g) => switch (g) {
              'A' => pal.dark ? const Color(0xFF4ADE80) : const Color(0xFF0E7A3A),
              'B' => pal.dark ? const Color(0xFFF2B300) : const Color(0xFF7A5A00),
              _ => pal.dark ? const Color(0xFF60A5FA) : const Color(0xFF1D4ED8),
            };
            Widget tile(String g, String title) {
              final (n, share) = stat(g);
              return Expanded(
                child: Panel(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        g,
                        style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800, color: groupText(g)),
                      ),
                      Text(title, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 12)),
                      Text(l.abcGroupNote(n, share.toStringAsFixed(1)), style: TextStyle(color: pal.textSoft, fontSize: 11.5)),
                    ],
                  ),
                ),
              );
            }

            if (rows.isEmpty) {
              children.add(
                Padding(
                  padding: const EdgeInsets.all(24),
                  child: Text(
                    l.noSalesPeriod,
                    textAlign: TextAlign.center,
                    style: TextStyle(color: pal.textSoft),
                  ),
                ),
              );
            } else {
              children.addAll([
                const SizedBox(height: 10),
                Row(children: [tile('A', l.abcA), const SizedBox(width: 6), tile('B', l.abcB), const SizedBox(width: 6), tile('C', l.abcC)]),
                FieldLabel(l.paretoTitle),
                Panel(
                  child: SizedBox(
                    height: 210,
                    child: CustomPaint(
                      painter: _ParetoPainter(
                        values: [for (final p in rows.take(40)) _value(p)],
                        cumulative: cumulative.take(40).toList(),
                        groups: groups.take(40).toList(),
                        colorOf: groupColor,
                        line: pal.dark ? Colors.white : const Color(0xFFB42318),
                        grid: pal.border,
                        text: pal.textSoft,
                      ),
                      size: Size.infinite,
                    ),
                  ),
                ),
                Padding(
                  padding: const EdgeInsets.only(top: 4),
                  child: Text(l.paretoHint, style: TextStyle(color: pal.textSoft, fontSize: 12)),
                ),
                const SizedBox(height: 8),
                for (var i = 0; i < rows.length; i++)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: Panel(
                      child: Row(
                        children: [
                          Container(
                            width: 26,
                            height: 26,
                            alignment: Alignment.center,
                            decoration: BoxDecoration(color: groupColor(groups[i]).withValues(alpha: .18), borderRadius: BorderRadius.circular(8)),
                            child: Text(
                              groups[i],
                              style: TextStyle(fontWeight: FontWeight.w800, color: groupText(groups[i])),
                            ),
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Text(
                              rows[i].name,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: const TextStyle(fontWeight: FontWeight.w600),
                            ),
                          ),
                          Column(
                            crossAxisAlignment: CrossAxisAlignment.end,
                            children: [
                              Text(_metric == 'qty' ? qtyText(_value(rows[i])) : money(_value(rows[i])), style: const TextStyle(fontWeight: FontWeight.w800)),
                              Text('${(cumulative[i] * 100).toStringAsFixed(1)} %', style: TextStyle(color: pal.textSoft, fontSize: 11)),
                            ],
                          ),
                        ],
                      ),
                    ),
                  ),
              ]);
            }
          }
          return RefreshIndicator(
            onRefresh: () async => _load(),
            child: ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 24), children: children),
          );
        },
      ),
    );
  }
}

/// Парето: столбцы по товарам (цвет группы), линия накопленной доли, пунктир 80 % и 95 %.
class _ParetoPainter extends CustomPainter {
  final List<double> values;
  final List<double> cumulative;
  final List<String> groups;
  final Color Function(String) colorOf;
  final Color line;
  final Color grid;
  final Color text;
  _ParetoPainter({
    required this.values,
    required this.cumulative,
    required this.groups,
    required this.colorOf,
    required this.line,
    required this.grid,
    required this.text,
  });

  @override
  void paint(Canvas canvas, Size size) {
    if (values.isEmpty) return;
    const top = 8.0;
    const bottom = 4.0;
    const right = 30.0;
    final h = size.height - top - bottom;
    final w = size.width - right;
    final max = values.reduce(math.max);
    final step = w / values.length;
    for (final level in [.8, .95]) {
      final y = top + h * (1 - level);
      final paint = Paint()
        ..color = grid
        ..strokeWidth = 1;
      for (var x = 0.0; x < w; x += 6) {
        canvas.drawLine(Offset(x, y), Offset(x + 3, y), paint);
      }
      final tp = TextPainter(
        text: TextSpan(
          text: '${(level * 100).round()}%',
          style: TextStyle(color: text, fontSize: 9.5),
        ),
        textDirection: TextDirection.ltr,
      )..layout();
      tp.paint(canvas, Offset(w + 4, y - tp.height / 2));
    }
    for (var i = 0; i < values.length; i++) {
      final bh = max <= 0 ? 0.0 : h * values[i] / max;
      final rect = RRect.fromRectAndRadius(Rect.fromLTWH(i * step + step * .15, top + h - bh, step * .7, bh), const Radius.circular(2));
      canvas.drawRRect(rect, Paint()..color = colorOf(groups[i]));
    }
    final path = Path();
    for (var i = 0; i < cumulative.length; i++) {
      final p = Offset(i * step + step / 2, top + h * (1 - cumulative[i]));
      i == 0 ? path.moveTo(p.dx, p.dy) : path.lineTo(p.dx, p.dy);
    }
    canvas.drawPath(
      path,
      Paint()
        ..color = line
        ..strokeWidth = 2
        ..style = PaintingStyle.stroke,
    );
    for (var i = 0; i < cumulative.length; i++) {
      canvas.drawCircle(Offset(i * step + step / 2, top + h * (1 - cumulative[i])), 2.2, Paint()..color = line);
    }
  }

  @override
  bool shouldRepaint(covariant _ParetoPainter old) => old.values != values || old.line != line;
}
