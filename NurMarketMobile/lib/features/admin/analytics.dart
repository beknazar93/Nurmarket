import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
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
      Text(context.l.reportLoadFailed, textAlign: TextAlign.center, style: const TextStyle(fontWeight: FontWeight.w700)),
      if (error is ApiException && error.message.isNotEmpty)
        Padding(
          padding: const EdgeInsets.only(top: 4),
          child: Text(error.message, textAlign: TextAlign.center, style: TextStyle(fontSize: 11.5, color: context.pal.textSoft)),
        ),
      const SizedBox(height: 12),
      FilledButton.icon(onPressed: retry, icon: const Icon(Icons.refresh_rounded), label: Text(context.l.retry)),
    ],
  ),
);

// ================================================================== Сводка

class SummaryScreen extends ConsumerStatefulWidget {
  const SummaryScreen({super.key});
  @override
  ConsumerState<SummaryScreen> createState() => _SummaryScreenState();
}

class _SummaryScreenState extends ConsumerState<SummaryScreen> {
  String _period = 'today';
  Future<(SalesSummary, List<ProductSales>)>? _future;

  @override
  void initState() {
    super.initState();
    _load();
  }

  void _load() {
    final (from, to) = periodRange(_period == 'today' ? 'week' : _period); // по дням — хотя бы неделя
    final (pf, pt) = periodRange(_period);
    final repo = ref.read(analyticsRepoProvider);
    setState(() {
      _future = () async {
        final s = await repo.sales(pf, pt);
        final chart = _period == 'today' ? await repo.sales(from, to) : s;
        final products = await repo.products(pf, pt);
        return (SalesSummary(s.revenue, s.checks, s.avg, s.profit, chart.byDay, s.payments), products);
      }();
    });
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Scaffold(
      appBar: ScreenTitle(l.summaryTitle),
      body: RefreshIndicator(
        onRefresh: () async => _load(),
        child: FutureBuilder<(SalesSummary, List<ProductSales>)>(
          future: _future,
          builder: (c, snap) {
            final children = <Widget>[
              periodChips(context, _period, (v) {
                _period = v;
                _load();
              }),
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
              final (s, products) = snap.data!;
              Widget kpi(String title, String value, {Color? color}) => Expanded(
                child: Panel(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(title, style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
                      Text(
                        value,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(fontSize: 17, fontWeight: FontWeight.w800, color: color),
                      ),
                    ],
                  ),
                ),
              );
              children.addAll([
                const SizedBox(height: 10),
                Panel(
                  color: T.navy,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(l.kpiRevenue, style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12.5)),
                      Text(
                        '${money(s.revenue)} ${l.som}',
                        style: const TextStyle(color: T.accent, fontSize: 28, fontWeight: FontWeight.w800),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 8),
                Row(
                  children: [
                    kpi(l.kpiChecks, '${s.checks}'),
                    const SizedBox(width: 6),
                    kpi(l.kpiAvg, money(s.avg)),
                    const SizedBox(width: 6),
                    kpi(l.kpiProfit, money(s.profit), color: T.success),
                  ],
                ),
                FieldLabel(l.revenueByDay),
                Panel(
                  child: SizedBox(
                    height: 150,
                    child: s.byDay.isEmpty
                        ? Center(
                            child: Text(l.noSalesPeriod, style: TextStyle(color: pal.textSoft)),
                          )
                        : _DayBars(s.byDay),
                  ),
                ),
                if (s.payments.isNotEmpty) ...[
                  FieldLabel(l.paymentMethods),
                  Panel(
                    child: Column(
                      children: [
                        for (final e in s.payments.entries.toList()..sort((a, b) => b.value.compareTo(a.value)))
                          Padding(
                            padding: const EdgeInsets.symmetric(vertical: 4),
                            child: Row(
                              children: [
                                SizedBox(width: 110, child: Text(paymentName(context, e.key))),
                                Expanded(
                                  child: ClipRRect(
                                    borderRadius: BorderRadius.circular(6),
                                    child: LinearProgressIndicator(
                                      value: s.revenue <= 0 ? 0 : (e.value / s.payments.values.fold(0.0, (a, b) => a + b)).clamp(0, 1).toDouble(),
                                      minHeight: 10,
                                      color: T.navy,
                                      backgroundColor: pal.border,
                                    ),
                                  ),
                                ),
                                const SizedBox(width: 8),
                                SizedBox(
                                  width: 90,
                                  child: Text(
                                    money(e.value),
                                    textAlign: TextAlign.right,
                                    style: const TextStyle(fontWeight: FontWeight.w700),
                                  ),
                                ),
                              ],
                            ),
                          ),
                      ],
                    ),
                  ),
                ],
                FieldLabel(l.topProducts),
                if (products.isEmpty) Text(l.noSalesPeriod, style: TextStyle(color: pal.textSoft)),
                for (final p in (products.toList()..sort((a, b) => b.revenue.compareTo(a.revenue))).take(10))
                  Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: Panel(
                      child: Row(
                        children: [
                          Expanded(
                            child: Text(
                              p.name,
                              maxLines: 2,
                              overflow: TextOverflow.ellipsis,
                              style: const TextStyle(fontWeight: FontWeight.w700),
                            ),
                          ),
                          Text('${qtyText(p.qty)} · ', style: TextStyle(color: pal.textSoft)),
                          Text(money(p.revenue), style: const TextStyle(fontWeight: FontWeight.w800)),
                        ],
                      ),
                    ),
                  ),
                const SizedBox(height: 8),
                OutlinedButton.icon(onPressed: () => context.push('/admin/abc'), icon: const Icon(Icons.bar_chart_rounded), label: Text(l.abcTitle)),
              ]);
            }
            return ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 24), children: children);
          },
        ),
      ),
    );
  }
}

class _DayBars extends StatelessWidget {
  final List<(DateTime, double)> days;
  const _DayBars(this.days);
  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final max = days.map((d) => d.$2).fold(0.0, math.max);
    return Row(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        for (final (d, v) in days)
          Expanded(
            child: Tooltip(
              message: '${d.day}.${d.month.toString().padLeft(2, '0')} · ${money(v)}',
              child: Column(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  Container(
                    height: max <= 0 ? 2 : math.max(2, 120 * v / max),
                    margin: const EdgeInsets.symmetric(horizontal: 2),
                    decoration: BoxDecoration(color: pal.dark ? T.accent : T.navy, borderRadius: BorderRadius.circular(4)),
                  ),
                  const SizedBox(height: 4),
                  Text('${d.day}', style: TextStyle(fontSize: 9.5, color: pal.textSoft)),
                ],
              ),
            ),
          ),
      ],
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

            Color groupColor(String g) => g == 'A' ? (pal.dark ? T.accent : T.navy) : (g == 'B' ? const Color(0xFFF2B300) : pal.textSoft);
            // 2026-10-21, владелец: жёлтая буква «B» на белом не читалась — у текста в светлой теме тёмно-жёлтый (контраст ≈ 8:1),
            // столбцы графика остаются жёлтыми.
            Color groupText(String g) => g == 'B' && !pal.dark ? const Color(0xFF7A5A00) : groupColor(g);
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
