import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../theme/tokens.dart';
import '../../util/ui.dart';
import 'chart_logic.dart';

// 2026-10-29 (владелец: «в мобилке сделай инфографику лучше»; спека Fable: design/Specs-1029/03-mobile-infographics.html,
// docs/Дизайн/Спеки 2026-10-29.md, раздел 3). Общие виджеты: KpiTile, DeltaText, Sparkline, PeriodBar, BarChartCard, DonutCard,
// TopBars, InfoProgressBar, состояния «пусто / загрузка / ошибка», «Как посчитано». Цвета — Palette и T.chartColors без
// изменений; сетка 16 / 12 / 14, радиусы 16 / 12 / 16, касания не меньше 44 dp. Логика — chart_logic.dart (без отрисовки).

const _kGap = 12.0;
const _kPad = 14.0;

TextStyle _num(double size, FontWeight w, [Color? c]) => TextStyle(fontSize: size, fontWeight: w, color: c, fontFeatures: const [FontFeature.tabularFigures()]);

/// Деньги для плитки: значение до 99 999 — целиком, дальше — «312,4 тыс», «1,2 млн».
String kpiMoney(BuildContext context, double v) => compactNumber(v, CompactWords.of(context.l), fullBelow: 100000);

// ================================================================== сравнение и дельта

/// Состояние сравнения для плитки. [on] == false → «— н/д»; включено, но прошлого нет → «— сравнения нет».
/// [inverse] — рост плох (долги, возвраты). [vs] — «к прошлой неделе».
class KpiCompare {
  final bool on;
  final DeltaInfo? delta;
  final String vs;
  final bool inverse;
  const KpiCompare({required this.on, this.delta, this.vs = '', this.inverse = false});
}

Color deltaColor(BuildContext context, DeltaInfo d, {bool inverse = false}) {
  final pal = context.pal;
  return switch (d.isGood(inverse: inverse)) {
    true => pal.success,
    false => pal.danger,
    null => pal.textSoft,
  };
}

/// Строка дельты: «▲ +12 %  к прошлой неделе» (знак + стрелка + цвет), «— н/д» без сравнения.
class DeltaRow extends StatelessWidget {
  final KpiCompare compare;
  final Color? softColor;
  const DeltaRow(this.compare, {super.key, this.softColor});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final soft = softColor ?? pal.textSoft;
    final d = compare.delta;
    if (!compare.on) return Text(l.infNoDelta, maxLines: 1, style: _num(12, FontWeight.w600, soft));
    if (d == null || d.kind == DeltaKind.none) return Text(l.infNoCompare, maxLines: 2, style: _num(12, FontWeight.w600, soft));
    final c = d.kind == DeltaKind.same ? soft : deltaColor(context, d, inverse: compare.inverse);
    // «▲ +12 %» и «к прошлой неделе» — в строку, а если не помещается (узкая плитка, длинный язык), вторая часть переносится ниже
    return Wrap(
      crossAxisAlignment: WrapCrossAlignment.center,
      spacing: 6,
      children: [
        Text(d.text(freshWord: l.infDeltaNew), maxLines: 1, style: _num(12, FontWeight.w600, c)),
        if (compare.vs.isNotEmpty) Text(compare.vs, maxLines: 2, style: _num(12, FontWeight.w500, soft)),
      ],
    );
  }
}

// ================================================================== мини-линия

/// Мини-линия тренда 64×28 (цвет категории, 2 px). 0 точек — пусто, 1 точка — точка, ровный ряд — линия по центру.
class Sparkline extends StatelessWidget {
  final List<double> values;
  final Color color;
  final double width;
  final double height;
  const Sparkline(this.values, {super.key, required this.color, this.width = 64, this.height = 28});

  @override
  Widget build(BuildContext context) {
    final clean = [for (final v in values) if (!v.isNaN && !v.isInfinite) v];
    if (clean.isEmpty) return SizedBox(width: width, height: height);
    return RepaintBoundary(
      child: ExcludeSemantics(
        child: CustomPaint(size: Size(width, height), painter: _SparkPainter(clean, color)),
      ),
    );
  }
}

class _SparkPainter extends CustomPainter {
  final List<double> v;
  final Color color;
  _SparkPainter(this.v, this.color);

  @override
  void paint(Canvas canvas, Size size) {
    const pad = 3.0;
    final lo = v.reduce(math.min), hi = v.reduce(math.max);
    final span = hi - lo;
    Offset at(int i) {
      final x = v.length == 1 ? size.width - pad : pad + (size.width - 2 * pad) * i / (v.length - 1);
      final y = span < 1e-9 ? size.height / 2 : size.height - pad - (size.height - 2 * pad) * (v[i] - lo) / span;
      return Offset(x, y);
    }

    final paint = Paint()
      ..color = color
      ..strokeWidth = 2
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round
      ..strokeJoin = StrokeJoin.round;
    if (v.length > 1) {
      final path = Path()..moveTo(at(0).dx, at(0).dy);
      for (var i = 1; i < v.length; i++) {
        path.lineTo(at(i).dx, at(i).dy);
      }
      canvas.drawPath(path, paint);
    }
    canvas.drawCircle(at(v.length - 1), 2.6, Paint()..color = color);
  }

  @override
  bool shouldRepaint(covariant _SparkPainter old) => old.color != color || old.v.length != v.length || !_same(old.v, v);

  static bool _same(List<double> a, List<double> b) {
    for (var i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }
}

// ================================================================== KPI-плитка

/// KPI-плитка: подпись 12, значение 22 ExtraBold + «сом» 13, дельта 12, мини-линия справа. [hero] — на всю ширину,
/// тёмная (heroBg), значение 32. [value] == null — грузится (скелет той же высоты); [failed] — «нет связи» и «Повторить».
class KpiTile extends StatelessWidget {
  final String label;
  final String? value;
  final String? unit;
  final KpiCompare? compare;
  final List<double>? spark;
  final Color? sparkColor;
  final Color? valueColor;
  final bool hero;
  final bool failed;
  final VoidCallback? onTap;
  final VoidCallback? onRetry;

  /// Текст «Как посчитано» — рядом с подписью появляется значок ⓘ.
  final String? how;
  final String? note;
  const KpiTile({
    super.key,
    required this.label,
    this.value,
    this.unit,
    this.compare,
    this.spark,
    this.sparkColor,
    this.valueColor,
    this.hero = false,
    this.failed = false,
    this.onTap,
    this.onRetry,
    this.how,
    this.note,
  });

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final bg = hero ? pal.heroBg : pal.panel;
    final onBg = hero ? Colors.white : pal.text;
    final soft = hero ? const Color(0xB8FFFFFF) : pal.textSoft;
    final valueC = valueColor ?? (hero ? Colors.white : pal.price);
    final sem = [
      label,
      if (failed) l.adminNoLink else if (value != null) '$value ${unit ?? ''}'.trim(),
      if (compare != null && compare!.on && compare!.delta != null) compare!.delta!.semantics(l, compare!.vs),
      ?note,
    ].join('. ');
    final showSpark = !failed && value != null && spark != null && spark!.length > 1;
    return Semantics(
      container: true,
      button: onTap != null,
      label: sem,
      child: ExcludeSemantics(
        child: Material(
          color: bg,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(T.radiusCard),
            side: BorderSide(color: hero ? pal.heroBorder : pal.border),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: ConstrainedBox(
              constraints: BoxConstraints(minHeight: hero ? 120 : 104),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: _kPad, vertical: 12),
                child: Stack(
                  children: [
                    Padding(
                      padding: EdgeInsets.only(right: showSpark ? (hero ? 118 : 70) : 0),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Row(
                            children: [
                              Flexible(child: Text(label, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w500, color: soft))),
                              if (how case final h?) HowCalcButton(h, color: soft, compact: true),
                            ],
                          ),
                          const SizedBox(height: 2),
                          if (failed)
                            _FailedBit(onRetry: onRetry, color: onBg)
                          else if (value == null)
                            InfoSkeleton(height: hero ? 38 : 28, width: 110)
                          else
                            FittedBox(
                              fit: BoxFit.scaleDown,
                              alignment: Alignment.centerLeft,
                              child: Row(
                                crossAxisAlignment: CrossAxisAlignment.baseline,
                                textBaseline: TextBaseline.alphabetic,
                                mainAxisSize: MainAxisSize.min,
                                children: [
                                  Text(value!, maxLines: 1, softWrap: false, style: _num(hero ? 32 : 22, FontWeight.w800, valueC)),
                                  if (unit != null && unit!.isNotEmpty) ...[
                                    const SizedBox(width: 4),
                                    Text(unit!, maxLines: 1, softWrap: false, style: TextStyle(fontSize: hero ? 14 : 13, fontWeight: FontWeight.w600, color: soft)),
                                  ],
                                ],
                              ),
                            ),
                          if (compare != null && !failed) ...[
                            const SizedBox(height: 4),
                            DeltaRow(compare!, softColor: soft),
                          ],
                          if (note != null) ...[
                            const SizedBox(height: 2),
                            Text(note!, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11.5, color: soft)),
                          ],
                        ],
                      ),
                    ),
                    if (showSpark)
                      Positioned(
                        right: 0,
                        top: hero ? 6 : 2,
                        child: Sparkline(spark!, color: sparkColor ?? T.chartColors[0], width: hero ? 110 : 64, height: hero ? 44 : 28),
                      ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _FailedBit extends StatelessWidget {
  final VoidCallback? onRetry;
  final Color color;
  const _FailedBit({this.onRetry, required this.color});
  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Wrap(
      crossAxisAlignment: WrapCrossAlignment.center,
      spacing: 8,
      children: [
        Text(l.adminNoLink, style: TextStyle(fontSize: 14, fontWeight: FontWeight.w700, color: color)),
        if (onRetry != null)
          InkWell(
            onTap: onRetry,
            borderRadius: BorderRadius.circular(8),
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 44, minWidth: 44),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.refresh_rounded, size: 16, color: color),
                  const SizedBox(width: 4),
                  Text(l.retry, maxLines: 1, style: TextStyle(fontSize: 13, fontWeight: FontWeight.w800, color: color)),
                ],
              ),
            ),
          ),
      ],
    );
  }
}

/// Плитки в две колонки одной высоты (по 12 между ними); нечётная последняя — на половину ширины.
class KpiGrid extends StatelessWidget {
  final List<Widget> tiles;
  const KpiGrid(this.tiles, {super.key});
  @override
  Widget build(BuildContext context) {
    final rows = <Widget>[];
    for (var i = 0; i < tiles.length; i += 2) {
      final b = i + 1 < tiles.length ? tiles[i + 1] : const SizedBox.shrink();
      rows.add(
        IntrinsicHeight(
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Expanded(child: tiles[i]),
              const SizedBox(width: _kGap),
              Expanded(child: b),
            ],
          ),
        ),
      );
      if (i + 2 < tiles.length) rows.add(const SizedBox(height: _kGap));
    }
    return Column(children: rows);
  }
}

// ================================================================== период и «Сравнить»

/// Переключатель периода: Сегодня / Неделя / Месяц / Свой (сегмент 44, кнопки 38) и тумблер «Сравнить» 44.
/// [period]: 'today' | 'week' | 'month' | 'custom'. Тумблер выключен → нет дельт и серых столбцов.
class PeriodBar extends StatelessWidget {
  final String period;
  final ValueChanged<String> onPeriod;
  final bool compare;
  final ValueChanged<bool> onCompare;
  final String? customLabel;
  const PeriodBar({super.key, required this.period, required this.onPeriod, required this.compare, required this.onCompare, this.customLabel});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    Widget seg(String id, String text) {
      final on = period == id;
      return Expanded(
        child: Semantics(
          button: true,
          selected: on,
          label: text,
          excludeSemantics: true,
          child: InkWell(
            onTap: () => onPeriod(id),
            borderRadius: BorderRadius.circular(9),
            child: Container(
              constraints: const BoxConstraints(minHeight: 38),
              alignment: Alignment.center,
              padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 6),
              decoration: BoxDecoration(color: on ? pal.action : Colors.transparent, borderRadius: BorderRadius.circular(9)),
              child: Text(
                text,
                maxLines: 2,
                textAlign: TextAlign.center,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(fontSize: 13, fontWeight: on ? FontWeight.w700 : FontWeight.w600, color: on ? pal.onAction : pal.textSoft),
              ),
            ),
          ),
        ),
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Container(
          constraints: const BoxConstraints(minHeight: 44),
          padding: const EdgeInsets.all(3),
          decoration: BoxDecoration(color: pal.panel, border: Border.all(color: pal.border), borderRadius: BorderRadius.circular(T.radiusButton)),
          child: Row(children: [seg('today', l.periodToday), seg('week', l.periodWeek), seg('month', l.periodMonth), seg('custom', customLabel ?? l.infPeriodCustom)]),
        ),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: Semantics(
            toggled: compare,
            label: l.infCompare,
            excludeSemantics: true,
            child: InkWell(
              onTap: () => onCompare(!compare),
              borderRadius: BorderRadius.circular(T.radiusButton),
              child: Container(
                constraints: const BoxConstraints(minHeight: 44),
                padding: const EdgeInsets.symmetric(horizontal: 12),
                decoration: BoxDecoration(
                  color: compare ? pal.accentSoft : pal.panel,
                  border: Border.all(color: compare ? T.accent : pal.border),
                  borderRadius: BorderRadius.circular(T.radiusButton),
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Container(
                      width: 30,
                      height: 18,
                      decoration: BoxDecoration(color: compare ? T.accent : pal.borderStrong, borderRadius: BorderRadius.circular(9)),
                      child: AnimatedAlign(
                        duration: const Duration(milliseconds: 120),
                        alignment: compare ? Alignment.centerRight : Alignment.centerLeft,
                        child: Container(
                          width: 14,
                          height: 14,
                          margin: const EdgeInsets.symmetric(horizontal: 2),
                          decoration: const BoxDecoration(color: Colors.white, shape: BoxShape.circle),
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Flexible(child: Text(l.infCompare, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600))),
                  ],
                ),
              ),
            ),
          ),
        ),
      ],
    );
  }
}

// ================================================================== карточка-контейнер

/// Белая карточка секции: заголовок 16, подзаголовок 12, справа ⓘ «Как посчитано» или своя ссылка.
class InfoCard extends StatelessWidget {
  final String title;
  final String? subtitle;
  final String? how;
  final Widget? action;
  final Widget child;
  const InfoCard({super.key, required this.title, this.subtitle, this.how, this.action, required this.child});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Container(
      padding: const EdgeInsets.all(_kPad),
      decoration: BoxDecoration(color: pal.panel, border: Border.all(color: pal.border), borderRadius: BorderRadius.circular(T.radiusCard)),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(
                child: Semantics(
                  header: true,
                  child: Text.rich(
                    TextSpan(
                      children: [
                        TextSpan(text: title, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
                        if (subtitle != null) TextSpan(text: '  $subtitle', style: TextStyle(fontSize: 12, fontWeight: FontWeight.w500, color: pal.textSoft)),
                      ],
                    ),
                  ),
                ),
              ),
              ?action,
              if (how case final h?) HowCalcButton(h),
            ],
          ),
          const SizedBox(height: 10),
          child,
        ],
      ),
    );
  }
}

/// ⓘ «Как посчитано»: касание 44 dp, текст открывается внизу экрана.
class HowCalcButton extends StatelessWidget {
  final String text;
  final Color? color;
  final bool compact;
  const HowCalcButton(this.text, {super.key, this.color, this.compact = false});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return IconButton(
      tooltip: l.infHowCalc,
      constraints: BoxConstraints(minWidth: compact ? 32 : 44, minHeight: compact ? 32 : 44),
      padding: EdgeInsets.zero,
      iconSize: compact ? 16 : 20,
      color: color ?? context.pal.textSoft,
      icon: const Icon(Icons.info_outline_rounded),
      onPressed: () => showModalBottomSheet<void>(
        context: context,
        showDragHandle: true,
        builder: (c) => SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 20),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(l.infHowTitle, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700)),
                const SizedBox(height: 8),
                Text(text, style: const TextStyle(fontSize: 14, height: 1.4)),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

// ================================================================== столбцы

class ChartBar {
  /// Подпись под столбцом (короткая: «29», «пн»).
  final String label;

  /// Название в карточке касания («Среда, 29 окт»).
  final String title;
  final double value;
  final Color? color;
  const ChartBar(this.label, this.title, this.value, {this.color});
}

/// Столбчатая диаграмма h 150: значение над столбцом 11 Bold (без единицы), ось 11, лучший/выбранный — акцент #FFB208 с
/// кольцом, «прошлый период» — тонкий серый столбик рядом. Касание или проведение пальцем — карточка под диаграммой.
/// Столбцов много — подписи значений и оси прореживаются; отрицательные уходят вниз от нулевой линии.
class BarChartCard extends StatefulWidget {
  final String title;
  final String? subtitle;
  final String semanticsName;
  final String unit;
  final List<ChartBar> bars;
  final List<double>? previous;
  final Set<int> best;
  final double height;
  final String? how;
  final Widget? legendExtra;
  const BarChartCard({
    super.key,
    required this.title,
    required this.semanticsName,
    required this.unit,
    required this.bars,
    this.subtitle,
    this.previous,
    this.best = const {},
    this.height = 150,
    this.how,
    this.legendExtra,
  });

  @override
  State<BarChartCard> createState() => _BarChartCardState();
}

class _BarChartCardState extends State<BarChartCard> {
  int? _sel;

  @override
  void didUpdateWidget(covariant BarChartCard old) {
    super.didUpdateWidget(old);
    if (_sel != null && _sel! >= widget.bars.length) _sel = null;
  }

  void _pick(double dx, double width) {
    final n = widget.bars.length;
    if (n == 0 || width <= 0) return;
    final i = (dx / width * n).floor().clamp(0, n - 1);
    if (i != _sel) setState(() => _sel = i);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final bars = widget.bars;
    final words = CompactWords.of(l);
    if (bars.isEmpty) {
      return InfoCard(title: widget.title, subtitle: widget.subtitle, how: widget.how, child: InfoEmpty(title: l.infNoData, hint: l.infNoDataHint));
    }
    final prev = widget.previous;
    final desc = describeBars(l, name: widget.semanticsName, labels: [for (final b in bars) b.title], values: [for (final b in bars) b.value], unit: widget.unit);
    return InfoCard(
      title: widget.title,
      subtitle: widget.subtitle,
      how: widget.how,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Semantics(
            container: true,
            label: desc,
            child: ExcludeSemantics(
              child: LayoutBuilder(
                builder: (c, box) => GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onTapDown: (d) => _pick(d.localPosition.dx, box.maxWidth),
                  onHorizontalDragUpdate: (d) => _pick(d.localPosition.dx, box.maxWidth),
                  child: RepaintBoundary(
                    child: CustomPaint(
                      size: Size(box.maxWidth, widget.height),
                      painter: _BarsPainter(
                        bars: bars,
                        previous: prev,
                        best: widget.best,
                        selected: _sel,
                        text: pal.text,
                        textSoft: pal.textSoft,
                        panel: pal.panel,
                        border: pal.border,
                        prevColor: pal.borderStrong,
                        scaler: MediaQuery.textScalerOf(context),
                        family: DefaultTextStyle.of(context).style.fontFamily ?? Theme.of(context).textTheme.bodyMedium?.fontFamily,
                        fmt: (v) => compactNumber(v, words, fullBelow: 1000),
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ),
          const SizedBox(height: 10),
          _BarTip(bars: bars, previous: prev, selected: _sel, unit: widget.unit),
          if (prev != null || widget.legendExtra != null) ...[
            const SizedBox(height: 8),
            Wrap(
              spacing: 14,
              runSpacing: 4,
              children: [
                if (prev != null)
                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Container(width: 10, height: 10, decoration: BoxDecoration(color: pal.borderStrong, borderRadius: BorderRadius.circular(3))),
                      const SizedBox(width: 6),
                      Text(l.infPrevBar, style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
                    ],
                  ),
                ?widget.legendExtra,
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _BarTip extends StatelessWidget {
  final List<ChartBar> bars;
  final List<double>? previous;
  final int? selected;
  final String unit;
  const _BarTip({required this.bars, required this.previous, required this.selected, required this.unit});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final box = BoxDecoration(color: pal.soft, border: Border.all(color: pal.border), borderRadius: BorderRadius.circular(T.radiusButton));
    if (selected == null) {
      return Container(
        constraints: const BoxConstraints(minHeight: 44),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        alignment: Alignment.centerLeft,
        decoration: box,
        child: Text(l.infTapHint, style: TextStyle(fontSize: 12, color: pal.textSoft)),
      );
    }
    final b = bars[selected!];
    final total = bars.fold<double>(0, (a, x) => a + math.max(0, x.value));
    final pv = previous != null && selected! < previous!.length ? previous![selected!] : null;
    final d = pv == null ? null : DeltaInfo.of(b.value, pv);
    return Semantics(
      liveRegion: true,
      container: true,
      label: '${b.title}: ${money(b.value)} $unit',
      child: Container(
        constraints: const BoxConstraints(minHeight: 44),
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: box,
        child: Wrap(
          crossAxisAlignment: WrapCrossAlignment.center,
          spacing: 8,
          runSpacing: 2,
          children: [
            Container(width: 10, height: 10, decoration: BoxDecoration(color: T.accent, borderRadius: BorderRadius.circular(3))),
            Text(b.title, style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w700)),
            Text('${money(b.value)} $unit', style: _num(12, FontWeight.w700)),
            if (total > 0 && b.value > 0) Text(l.infOfPeriod(percentText(b.value, total).replaceAll(' %', '')), style: TextStyle(fontSize: 12, color: pal.textSoft)),
            if (d != null && d.kind != DeltaKind.none) Text(d.text(freshWord: l.infDeltaNew), style: _num(12, FontWeight.w700, deltaColor(context, d))),
          ],
        ),
      ),
    );
  }
}

class _BarsPainter extends CustomPainter {
  final List<ChartBar> bars;
  final List<double>? previous;
  final Set<int> best;
  final int? selected;
  final Color text, textSoft, panel, border, prevColor;
  final TextScaler scaler;
  final String? family;
  final String Function(double) fmt;
  _BarsPainter({
    required this.bars,
    required this.previous,
    required this.best,
    required this.selected,
    required this.text,
    required this.textSoft,
    required this.panel,
    required this.border,
    required this.prevColor,
    required this.scaler,
    required this.family,
    required this.fmt,
  });

  TextPainter _tp(String s, double size, FontWeight w, Color c, {double maxWidth = double.infinity}) =>
      TextPainter(text: TextSpan(text: s, style: TextStyle(fontFamily: family, fontSize: size, fontWeight: w, color: c)), textDirection: TextDirection.ltr, textScaler: scaler, maxLines: 1, ellipsis: '…')
        ..layout(maxWidth: maxWidth);

  @override
  void paint(Canvas canvas, Size size) {
    final n = bars.length;
    if (n == 0) return;
    const labelH = 16.0;
    const topPad = 18.0;
    final plotH = size.height - labelH - topPad - 4;
    final values = [for (final b in bars) b.value.isNaN || b.value.isInfinite ? 0.0 : b.value];
    final prev = previous;
    var hi = 0.0, lo = 0.0;
    for (var i = 0; i < n; i++) {
      hi = math.max(hi, values[i]);
      lo = math.min(lo, values[i]);
      if (prev != null && i < prev.length && !prev[i].isNaN && !prev[i].isInfinite) {
        hi = math.max(hi, prev[i]);
        lo = math.min(lo, prev[i]);
      }
    }
    final range = hi - lo;
    final baseY = range <= 0 ? topPad + plotH : topPad + plotH * hi / range;
    double hOf(double v) => range <= 0 ? 0 : plotH * v.abs() / range;
    final slot = size.width / n;
    final barW = n <= 14 ? math.min(30.0, slot - 6) : math.max(1.0, slot * 0.7);
    final radius = n <= 14 ? const Radius.circular(6) : const Radius.circular(2);
    // подписи значений — только когда помещаются; иначе — у выбранного и лучшего
    var maxLabelW = 0.0;
    if (n <= 14) {
      for (final v in values) {
        maxLabelW = math.max(maxLabelW, _tp(fmt(v), 11, FontWeight.w700, text).width);
      }
    }
    final showAll = n <= 14 && showBarValues(n, slot, maxLabelW);
    final baseColorDefault = T.chartColors[0];
    for (var i = 0; i < n; i++) {
      final cx = slot * i + slot / 2;
      final v = values[i];
      final h = math.max(v == 0 ? 2.0 : 3.0, hOf(v));
      final isSel = selected == i;
      final isBest = best.contains(i);
      final color = isSel || isBest ? T.accent : (bars[i].color ?? baseColorDefault);
      final top = v >= 0 ? baseY - h : baseY;
      final rect = RRect.fromRectAndCorners(
        Rect.fromLTWH(cx - barW / 2, top, barW, h),
        topLeft: v >= 0 ? radius : const Radius.circular(3),
        topRight: v >= 0 ? radius : const Radius.circular(3),
        bottomLeft: v >= 0 ? const Radius.circular(3) : radius,
        bottomRight: v >= 0 ? const Radius.circular(3) : radius,
      );
      if (prev != null && i < prev.length) {
        final pv = prev[i].isNaN || prev[i].isInfinite ? 0.0 : prev[i];
        final ph = math.max(2.0, hOf(pv));
        final pw = n <= 14 ? 10.0 : math.max(1.0, barW * 0.5);
        final pr = RRect.fromRectAndRadius(Rect.fromLTWH(cx + barW / 2 - pw + (n <= 14 ? 0 : 0), pv >= 0 ? baseY - ph : baseY, pw, ph), const Radius.circular(3));
        canvas.drawRRect(pr, Paint()..color = prevColor);
      }
      if (isSel) {
        canvas.drawRRect(rect.inflate(2), Paint()..color = panel);
        canvas.drawRRect(rect.inflate(3), Paint()
          ..color = T.accent
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1);
        canvas.drawRRect(rect.inflate(2), Paint()
          ..color = panel
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2);
      }
      canvas.drawRRect(rect, Paint()..color = color);
      if (showAll || isSel || (isBest && n <= 31)) {
        final t = _tp(fmt(v), 11, FontWeight.w700, text, maxWidth: math.max(slot * 3, 60));
        final y = v >= 0 ? baseY - h - t.height - 2 : baseY + h + 2;
        final x = (cx - t.width / 2).clamp(0.0, size.width - t.width);
        t.paint(canvas, Offset(x, math.max(0, y)));
      }
    }
    canvas.drawLine(Offset(0, baseY), Offset(size.width, baseY), Paint()
      ..color = border
      ..strokeWidth = 1);
    // подписи оси X с прореживанием
    var lw = 0.0;
    for (final b in bars) {
      lw = math.max(lw, _tp(b.label, 11, FontWeight.w500, textSoft).width);
    }
    final step = labelStep(n, slot, lw);
    for (final i in labelIndices(n, step)) {
      final t = _tp(bars[i].label, 11, FontWeight.w500, selected == i ? text : textSoft, maxWidth: math.max(slot * step, 24));
      final x = (slot * i + slot / 2 - t.width / 2).clamp(0.0, size.width - t.width);
      t.paint(canvas, Offset(x, size.height - labelH + 2));
    }
  }

  @override
  bool shouldRepaint(covariant _BarsPainter o) =>
      o.selected != selected || o.bars != bars || o.previous != previous || o.text != text || o.scaler != scaler || o.family != family || o.best != best || o.panel != panel;
}

// ================================================================== кольцо

/// Кольцо 120×120 (толщина 16) и легенда справа: квадрат 12, подпись 13, процент Bold. Касание сектора или строки легенды —
/// в центре значение и название. Цвет сектора — [ChartColors] по ключу; сверх [maxSlices] — «Прочее».
class DonutCard extends StatefulWidget {
  final String title;
  final String? subtitle;
  final String semanticsName;
  final String unit;
  final List<Slice> slices;
  final String? how;
  final int maxSlices;
  const DonutCard({super.key, required this.title, required this.semanticsName, required this.unit, required this.slices, this.subtitle, this.how, this.maxSlices = 6});

  @override
  State<DonutCard> createState() => _DonutCardState();
}

class _DonutCardState extends State<DonutCard> {
  int? _sel;

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final slices = groupSlices(widget.slices, maxSlices: widget.maxSlices, otherLabel: l.infOther);
    if (slices.isEmpty) {
      return InfoCard(title: widget.title, subtitle: widget.subtitle, how: widget.how, child: InfoEmpty(title: l.infNoData, hint: l.infNoDataHint));
    }
    final fr = sliceFractions(slices);
    final pct = wholePercents(fr);
    final colors = ChartColors.assign([for (final s in slices) s.key]);
    final words = CompactWords.of(l);
    final total = slices.fold<double>(0, (a, s) => a + s.value);
    final sel = _sel != null && _sel! < slices.length ? _sel : null;
    final centerBig = compactNumber(sel == null ? total : slices[sel].value, words, fullBelow: 100000);
    final centerSmall = sel == null ? l.infTotalWord : slices[sel].label;
    final ring = Semantics(
      container: true,
      label: describeSlices(l, name: widget.semanticsName, slices: slices),
      child: ExcludeSemantics(
        child: GestureDetector(
          behavior: HitTestBehavior.opaque,
          onTapDown: (d) {
            const c = Offset(60, 60);
            final off = d.localPosition - c;
            if (off.distance < 30 || off.distance > 64) {
              setState(() => _sel = null);
              return;
            }
            var a = math.atan2(off.dy, off.dx) + math.pi / 2;
            if (a < 0) a += 2 * math.pi;
            var acc = 0.0;
            for (var i = 0; i < fr.length; i++) {
              acc += fr[i] * 2 * math.pi;
              if (a <= acc + 1e-9) {
                setState(() => _sel = _sel == i ? null : i);
                return;
              }
            }
          },
          child: SizedBox(
            width: 120,
            height: 120,
            child: Stack(
              alignment: Alignment.center,
              children: [
                RepaintBoundary(child: CustomPaint(size: const Size(120, 120), painter: _DonutPainter(fr, colors, sel, pal.soft, pal.panel))),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 24),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      FittedBox(fit: BoxFit.scaleDown, child: Text(centerBig, maxLines: 1, style: _num(16, FontWeight.w800))),
                      Text(centerSmall, maxLines: 1, overflow: TextOverflow.ellipsis, textAlign: TextAlign.center, style: TextStyle(fontSize: 9, fontWeight: FontWeight.w600, color: pal.textSoft)),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
    final legend = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (var i = 0; i < slices.length; i++)
          InkWell(
            onTap: () => setState(() => _sel = _sel == i ? null : i),
            borderRadius: BorderRadius.circular(8),
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 44),
              child: Row(
                children: [
                  Container(width: 12, height: 12, decoration: BoxDecoration(color: colors[i], borderRadius: BorderRadius.circular(4))),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(slices[i].label, maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 13, fontWeight: sel == i ? FontWeight.w800 : FontWeight.w500)),
                  ),
                  const SizedBox(width: 6),
                  Text('${pct[i]} %', style: _num(13, FontWeight.w700)),
                ],
              ),
            ),
          ),
      ],
    );
    return InfoCard(
      title: widget.title,
      subtitle: widget.subtitle,
      how: widget.how,
      child: LayoutBuilder(
        builder: (c, box) {
          final narrow = box.maxWidth < 300 || MediaQuery.textScalerOf(context).scale(1) > 1.25;
          if (narrow) {
            return Column(children: [ring, const SizedBox(height: 8), legend]);
          }
          return Row(crossAxisAlignment: CrossAxisAlignment.center, children: [ring, const SizedBox(width: _kPad), Expanded(child: legend)]);
        },
      ),
    );
  }
}

class _DonutPainter extends CustomPainter {
  final List<double> fr;
  final List<Color> colors;
  final int? sel;
  final Color track;
  final Color gap;
  _DonutPainter(this.fr, this.colors, this.sel, this.track, this.gap);

  @override
  void paint(Canvas canvas, Size size) {
    const w = 16.0;
    final rect = Rect.fromCircle(center: size.center(Offset.zero), radius: size.width / 2 - w / 2 - 2);
    canvas.drawArc(
      rect,
      0,
      2 * math.pi,
      false,
      Paint()
        ..color = track
        ..style = PaintingStyle.stroke
        ..strokeWidth = w,
    );
    var start = -math.pi / 2;
    final gapAngle = fr.length > 1 ? 0.04 : 0.0;
    for (var i = 0; i < fr.length; i++) {
      final sweep = fr[i] * 2 * math.pi;
      final draw = math.max(0.0, sweep - gapAngle);
      canvas.drawArc(
        rect,
        start + gapAngle / 2,
        draw,
        false,
        Paint()
          ..color = colors[i]
          ..style = PaintingStyle.stroke
          ..strokeWidth = sel == i ? w + 4 : w
          ..strokeCap = StrokeCap.butt,
      );
      start += sweep;
    }
  }

  @override
  bool shouldRepaint(covariant _DonutPainter o) => o.sel != sel || o.fr != fr || o.colors != colors || o.track != track;
}

// ================================================================== топ-N и индикатор

/// Строка рейтинга: номер, название (обрезка …), полоса 8 px относительно первого, сумма Bold.
class TopBarRow {
  final String name;
  final double value;
  final Color color;
  const TopBarRow(this.name, this.value, this.color);
}

class TopBars extends StatelessWidget {
  final List<TopBarRow> rows;
  final String unit;
  const TopBars({super.key, required this.rows, required this.unit});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final top = rows.fold<double>(0, (m, r) => math.max(m, r.value));
    return Column(
      children: [
        for (var i = 0; i < rows.length; i++)
          Semantics(
            container: true,
            label: '${i + 1}. ${rows[i].name}: ${money(rows[i].value)} $unit',
            child: ExcludeSemantics(
              child: ConstrainedBox(
                constraints: const BoxConstraints(minHeight: 36),
                child: Padding(
                  padding: const EdgeInsets.symmetric(vertical: 3),
                  child: Row(
                    children: [
                      SizedBox(width: 20, child: Text('${i + 1}', style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: pal.textSoft))),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(rows[i].name, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w500)),
                            const SizedBox(height: 3),
                            LayoutBuilder(
                              builder: (c, box) => Container(
                                height: 8,
                                width: top <= 0 ? 0 : math.max(3.0, box.maxWidth * (rows[i].value / top).clamp(0.0, 1.0)),
                                decoration: BoxDecoration(color: rows[i].color, borderRadius: BorderRadius.circular(4)),
                              ),
                            ),
                          ],
                        ),
                      ),
                      const SizedBox(width: 8),
                      ConstrainedBox(
                        constraints: const BoxConstraints(maxWidth: 92),
                        child: FittedBox(
                          fit: BoxFit.scaleDown,
                          alignment: Alignment.centerRight,
                          child: Text(money(rows[i].value), maxLines: 1, style: _num(13, FontWeight.w700)),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
      ],
    );
  }
}

/// Полоса 10 px, радиус 5; метка плана 2 px (если [marker] задан). ≥ 100 % — зелёная. [value] и [max] — любые единицы.
class InfoProgressBar extends StatelessWidget {
  final double value;
  final double max;
  final Color? color;
  final double? marker;
  final double height;
  const InfoProgressBar({super.key, required this.value, required this.max, this.color, this.marker, this.height = 10});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final f = max <= 0 || value.isNaN ? 0.0 : (value / max).clamp(0.0, 1.0);
    final done = max > 0 && value >= max;
    final fill = color ?? (done ? pal.success : T.accent);
    return ExcludeSemantics(
      child: LayoutBuilder(
        builder: (c, box) => Container(
          height: height,
          width: box.maxWidth,
          decoration: BoxDecoration(color: pal.soft, border: Border.all(color: pal.border), borderRadius: BorderRadius.circular(height / 2)),
          child: Stack(
            clipBehavior: Clip.none,
            children: [
              ClipRRect(
                borderRadius: BorderRadius.circular(height / 2),
                child: Align(
                  alignment: Alignment.centerLeft,
                  child: FractionallySizedBox(widthFactor: f, child: Container(color: fill)),
                ),
              ),
              if (marker != null && max > 0)
                Positioned(left: box.maxWidth * (marker! / max).clamp(0.0, 1.0) - 1, top: -3, bottom: -3, child: Container(width: 2, color: pal.text)),
            ],
          ),
        ),
      ),
    );
  }
}

/// Составная полоса из долей (наличные / безнал / долг, нет / мало / хватает): сегменты 10 px с зазором 2.
class SplitBar extends StatelessWidget {
  final List<double> fractions;
  final List<Color> colors;
  final double height;
  const SplitBar({super.key, required this.fractions, required this.colors, this.height = 10});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final active = [
      for (var i = 0; i < fractions.length; i++)
        if (fractions[i] > 0.0005) i,
    ];
    return ExcludeSemantics(
      child: ClipRRect(
        borderRadius: BorderRadius.circular(height / 2),
        child: Container(
          height: height,
          color: pal.soft,
          child: Row(
            children: [
              for (var k = 0; k < active.length; k++) ...[
                if (k > 0) const SizedBox(width: 2),
                Expanded(flex: math.max(1, (fractions[active[k]] * 1000).round()), child: Container(color: colors[active[k]])),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

// ================================================================== состояния

/// Пусто: значок 44, заголовок 14 SemiBold и подсказка 13; высота не меньше 150.
class InfoEmpty extends StatelessWidget {
  final String title;
  final String? hint;
  final IconData icon;
  const InfoEmpty({super.key, required this.title, this.hint, this.icon = Icons.bar_chart_rounded});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Semantics(
      container: true,
      label: [title, ?hint].join('. '),
      child: ExcludeSemantics(
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 150),
          child: Center(
            child: Padding(
              padding: const EdgeInsets.symmetric(vertical: 12),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Container(
                    width: 44,
                    height: 44,
                    decoration: BoxDecoration(color: pal.soft, border: Border.all(color: pal.border), borderRadius: BorderRadius.circular(14)),
                    child: Icon(icon, color: pal.textSoft),
                  ),
                  const SizedBox(height: 8),
                  Text(title, textAlign: TextAlign.center, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
                  if (hint != null) ...[
                    const SizedBox(height: 4),
                    Text(hint!, textAlign: TextAlign.center, style: TextStyle(fontSize: 13, color: pal.textSoft)),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Ошибка: dangerSoft, время последних данных (если были) и «Повторить» 44 dp.
class InfoError extends StatelessWidget {
  final VoidCallback onRetry;
  final DateTime? lastData;
  final String? detail;
  const InfoError({super.key, required this.onRetry, this.lastData, this.detail});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final text = lastData == null ? l.infLoadFailedNoData : l.infLoadFailedAt(TimeOfDay.fromDateTime(lastData!).format(context));
    return Container(
      constraints: const BoxConstraints(minHeight: 150),
      width: double.infinity,
      padding: const EdgeInsets.all(_kPad),
      decoration: BoxDecoration(color: pal.dangerSoft, border: Border.all(color: pal.border), borderRadius: BorderRadius.circular(T.radiusCard)),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(Icons.cloud_off_rounded, size: 36, color: pal.danger),
          const SizedBox(height: 6),
          Text(l.infLoadFailedTitle, textAlign: TextAlign.center, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
          const SizedBox(height: 2),
          Text(text, textAlign: TextAlign.center, style: TextStyle(fontSize: 13, color: pal.textSoft)),
          if (detail != null && detail!.isNotEmpty) Text(detail!, textAlign: TextAlign.center, style: TextStyle(fontSize: 11.5, color: pal.textSoft)),
          const SizedBox(height: 6),
          OutlinedButton.icon(
            style: OutlinedButton.styleFrom(minimumSize: const Size(44, 44)),
            onPressed: onRetry,
            icon: const Icon(Icons.refresh_rounded),
            label: Text(l.retry),
          ),
        ],
      ),
    );
  }
}

/// Скелет загрузки: полоса soft→border, 1,4 с, без спиннера. При «убрать анимации» в системе — неподвижный.
class InfoSkeleton extends StatefulWidget {
  final double height;
  final double? width;
  const InfoSkeleton({super.key, this.height = 28, this.width});
  @override
  State<InfoSkeleton> createState() => _InfoSkeletonState();
}

class _InfoSkeletonState extends State<InfoSkeleton> with SingleTickerProviderStateMixin {
  late final AnimationController _c = AnimationController(vsync: this, duration: const Duration(milliseconds: 1400));

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (MediaQuery.disableAnimationsOf(context)) {
      _c.stop();
    } else if (!_c.isAnimating) {
      _c.repeat();
    }
  }

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return ExcludeSemantics(
      child: AnimatedBuilder(
        animation: _c,
        builder: (c, _) => Container(
          height: widget.height,
          width: widget.width,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(8),
            gradient: LinearGradient(
              begin: Alignment(-1 + 2 * _c.value, 0),
              end: Alignment(1 + 2 * _c.value, 0),
              colors: [pal.soft, pal.border, pal.soft],
            ),
          ),
        ),
      ),
    );
  }
}

/// Карточка-скелет секции той же высоты, что и готовая (числа не «прыгают»).
class InfoCardSkeleton extends StatelessWidget {
  final String title;
  final double height;
  const InfoCardSkeleton({super.key, required this.title, this.height = 150});
  @override
  Widget build(BuildContext context) => InfoCard(title: title, child: InfoSkeleton(height: height));
}

/// «Обновлено 14:02» — мелко, справа.
class UpdatedAtLabel extends StatelessWidget {
  final DateTime at;
  const UpdatedAtLabel(this.at, {super.key});
  @override
  Widget build(BuildContext context) => Text(
    context.l.infUpdatedAt(TimeOfDay.fromDateTime(at).format(context)),
    style: TextStyle(fontSize: 11.5, color: context.pal.textSoft),
  );
}

/// Честная заметка: чего в отчёте нет и почему (часы, расходы, давность долга).
class InfoNote extends StatelessWidget {
  final String text;
  const InfoNote(this.text, {super.key});
  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(Icons.info_outline_rounded, size: 16, color: pal.textSoft),
        const SizedBox(width: 8),
        Expanded(child: Text(text, style: TextStyle(fontSize: 12, height: 1.35, color: pal.textSoft))),
      ],
    );
  }
}

// ================================================================== наличные / безнал / долг

/// Продажи по способу оплаты (наличные, безнал, в долг): полоса 10 px и легенда. [rows] — строки ≥ 44 dp с суммой
/// (экран смены), иначе — компактная подпись в одну-две строки (главная). [onDark] — светлый текст на тёмной карточке.
class PaymentSplit extends StatelessWidget {
  final double cash;
  final double nonCash;
  final double debt;
  final bool rows;
  final bool onDark;
  const PaymentSplit({super.key, required this.cash, required this.nonCash, required this.debt, this.rows = false, this.onDark = false});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final v = [math.max(0.0, cash), math.max(0.0, nonCash), math.max(0.0, debt)];
    final total = v.fold<double>(0, (a, b) => a + b);
    if (total <= 0) return rows ? InfoEmpty(title: l.infShiftNoSales, icon: Icons.receipt_long_outlined) : const SizedBox.shrink();
    final names = [l.cash, l.shNonCash, l.shDebt];
    final colors = ChartColors.assign(const ['cash', 'card', 'debt']);
    final pct = wholePercents([for (final x in v) x / total]);
    final textC = onDark ? Colors.white : pal.text;
    final softC = onDark ? const Color(0xB8FFFFFF) : pal.textSoft;
    final shown = [
      for (var i = 0; i < 3; i++)
        if (v[i] > 0) i,
    ];
    final bar = SplitBar(fractions: [for (final x in v) x / total], colors: colors);
    final sem = [for (final i in shown) '${names[i]} ${pct[i]} %, ${money(v[i])}'].join('; ');
    if (!rows) {
      return Semantics(
        container: true,
        label: sem,
        child: ExcludeSemantics(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              bar,
              const SizedBox(height: 8),
              Wrap(
                spacing: 14,
                runSpacing: 4,
                children: [
                  for (final i in shown)
                    Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Container(width: 10, height: 10, decoration: BoxDecoration(color: colors[i], borderRadius: BorderRadius.circular(3))),
                        const SizedBox(width: 6),
                        Text('${names[i]} ${pct[i]} %', style: TextStyle(fontSize: 11.5, color: softC)),
                      ],
                    ),
                ],
              ),
            ],
          ),
        ),
      );
    }
    return Semantics(
      container: true,
      label: sem,
      child: ExcludeSemantics(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            bar,
            const SizedBox(height: 6),
            for (final i in shown)
              ConstrainedBox(
                constraints: const BoxConstraints(minHeight: 44),
                child: Row(
                  children: [
                    Container(width: 12, height: 12, decoration: BoxDecoration(color: colors[i], borderRadius: BorderRadius.circular(4))),
                    const SizedBox(width: 8),
                    Expanded(child: Text(names[i], maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 14, color: textC))),
                    Text('${pct[i]} %', style: _num(12, FontWeight.w600, softC)),
                    const SizedBox(width: 10),
                    Text('${money(v[i])} ${l.som}', style: _num(14, FontWeight.w700, textC)),
                  ],
                ),
              ),
          ],
        ),
      ),
    );
  }
}
