// 2026-10-26 (голос-управление, В4): нижняя панель таблицы во время звонка (раздел 4 ТЗ, макет Mobile-Call-Table). Три высоты:
// свёрнута 96 dp (ручка, название, «Итого»), половина (по умолчанию), весь экран. Новая таблица заменяет прежнюю («‹ 2/4 ›»),
// «Закрепить» — новая открывается вкладкой рядом. Первая колонка закреплена при горизонтальной прокрутке, шапка — при
// вертикальной, «Итого» прилипает снизу. Нажатие на шапку колонки — сортировка, на строку — открыть объект. Цифры — из
// программы (CallTable), числа с табличными цифрами, минус — красным. Размер текста 1.0 / 1.3 / 1.6 («Крупнее»).
import 'package:flutter/material.dart';

import '../../core/ai/assistant/call_table.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import 'call_controller.dart';

enum PanelExtent { min, half, full }

class _PanelColors {
  final Palette pal;
  _PanelColors(this.pal);
  Color get soft => pal.dark ? const Color(0xFF333333) : T.soft;
  Color get total => pal.dark ? const Color(0xFF3A2D10) : T.accentSoft;
  Color get zebra => pal.dark ? const Color(0xFF303030) : T.soft;
}

class CallTablePanel extends StatefulWidget {
  final AssistantCallController c;

  /// Начальная высота — для тестов и снимков.
  final PanelExtent initial;
  const CallTablePanel({super.key, required this.c, this.initial = PanelExtent.half});

  @override
  State<CallTablePanel> createState() => _CallTablePanelState();
}

class _CallTablePanelState extends State<CallTablePanel> {
  late PanelExtent _extent = widget.initial;
  double? _drag; // высота во время перетаскивания
  TableStore get _store => widget.c.tables;

  double _heightOf(PanelExtent e, double screenH, double top, double bottom) {
    switch (e) {
      case PanelExtent.min:
        return 96 + bottom;
      case PanelExtent.half:
        return screenH * (screenH <= 760 ? .54 : .52);
      case PanelExtent.full:
        return screenH - top;
    }
  }

  void _snap(double h, double velocity, double screenH, double top, double bottom) {
    // ближайшая из трёх высот; быстрый жест — к следующей в его сторону
    final hs = {for (final e in PanelExtent.values) e: _heightOf(e, screenH, top, bottom)};
    PanelExtent best = _extent;
    var d = double.infinity;
    final target = h - velocity * .12;
    for (final e in PanelExtent.values) {
      final dd = (hs[e]! - target).abs();
      if (dd < d) {
        d = dd;
        best = e;
      }
    }
    setState(() {
      _extent = best;
      _drag = null;
    });
  }

  @override
  Widget build(BuildContext context) {
    final mq = MediaQuery.of(context);
    return LayoutBuilder(
      builder: (context, box) {
        final screenH = box.maxHeight;
        final top = mq.padding.top;
        final bottom = mq.padding.bottom;
        final h = (_drag ?? _heightOf(_extent, screenH, top, bottom)).clamp(_heightOf(PanelExtent.min, screenH, top, bottom), _heightOf(PanelExtent.full, screenH, top, bottom));
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (widget.c.panelInset.value != h) widget.c.panelInset.value = h;
        });
        final pal = context.pal;
        final cur = _store.current;
        if (cur == null || _store.isClosed) return const SizedBox.shrink();
        return Stack(
          children: [
            if (_extent != PanelExtent.min)
              Positioned.fill(
                child: GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onTap: () => setState(() => _extent = PanelExtent.min),
                  child: ColoredBox(color: pal.dark ? const Color(0x99000000) : const Color(0x73191919)),
                ),
              ),
            Positioned(
              left: 0,
              right: 0,
              bottom: 0,
              child: AnimatedContainer(
                duration: _drag == null ? const Duration(milliseconds: 260) : Duration.zero,
                curve: const Cubic(.2, .8, .2, 1),
                height: h,
                decoration: BoxDecoration(
                  color: pal.panel,
                  borderRadius: const BorderRadius.vertical(top: Radius.circular(20)),
                  boxShadow: const [BoxShadow(color: Color(0x38000000), blurRadius: 28, offset: Offset(0, -8))],
                ),
                clipBehavior: Clip.antiAlias,
                child: Material(
                  color: Colors.transparent,
                  child: _PanelBody(
                    c: widget.c,
                    table: cur,
                    extent: _extent,
                    bottomInset: bottom,
                    onToggle: () => setState(() => _extent = _extent == PanelExtent.full ? PanelExtent.half : PanelExtent.full),
                    onMin: () => setState(() => _extent = PanelExtent.min),
                    onExpand: () => setState(() => _extent = _extent == PanelExtent.min ? PanelExtent.half : _extent),
                    onDrag: (dy) => setState(() => _drag = ((_drag ?? h) - dy)),
                    onDragEnd: (v) => _snap(_drag ?? h, v, screenH, top, bottom),
                  ),
                ),
              ),
            ),
          ],
        );
      },
    );
  }
}

class _PanelBody extends StatelessWidget {
  final AssistantCallController c;
  final CallTable table;
  final PanelExtent extent;
  final double bottomInset;
  final VoidCallback onToggle;
  final VoidCallback onMin;
  final VoidCallback onExpand;
  final void Function(double dy) onDrag;
  final void Function(double velocity) onDragEnd;
  const _PanelBody({
    required this.c,
    required this.table,
    required this.extent,
    required this.bottomInset,
    required this.onToggle,
    required this.onMin,
    required this.onExpand,
    required this.onDrag,
    required this.onDragEnd,
  });

  TableStore get store => c.tables;

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final colors = _PanelColors(pal);
    final min = extent == PanelExtent.min;
    final full = extent == PanelExtent.full;
    final t = table;
    final subtitle = [if (t.subtitle != null && t.subtitle!.isNotEmpty) t.subtitle!, if (t.viewNote().isNotEmpty) t.viewNote()].join(' · ');
    final pager = store.count > 1
        ? Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              _Ib(icon: Icons.chevron_left_rounded, label: l.tblPrev, size: 40, onTap: () => store.step(-1)),
              Text('${store.position}/${store.count}', style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: pal.textSoft, fontFeatures: const [FontFeature.tabularFigures()])),
              _Ib(icon: Icons.chevron_right_rounded, label: l.tblNext, size: 40, onTap: () => store.step(1)),
            ],
          )
        : null;
    final tot = t.totalsRow();
    return Column(
      children: [
        // --- ручка и заголовок (тянется вверх-вниз)
        GestureDetector(
          behavior: HitTestBehavior.opaque,
          onVerticalDragUpdate: (d) => onDrag(d.delta.dy),
          onVerticalDragEnd: (d) => onDragEnd(d.velocity.pixelsPerSecond.dy),
          onTap: min ? onExpand : null,
          child: Column(
            children: [
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Container(width: 32, height: 4, decoration: BoxDecoration(color: T.borderStrong, borderRadius: BorderRadius.circular(2))),
              ),
              Padding(
                padding: EdgeInsets.fromLTRB(full ? 4 : 16, 2, 8, 0),
                child: ConstrainedBox(
                  constraints: const BoxConstraints(minHeight: 48),
                  child: Row(
                    children: [
                      if (full) _Ib(icon: Icons.keyboard_arrow_down_rounded, label: l.tblCollapse, size: 48, iconSize: 24, onTap: onToggle),
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Text(t.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800, height: 1.15)),
                            if (subtitle.isNotEmpty) Text(subtitle, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: pal.textSoft, fontWeight: FontWeight.w600)),
                          ],
                        ),
                      ),
                      if (!min && !full && pager != null) pager,
                      if (full)
                        ListenableBuilder(
                          listenable: c,
                          builder: (_, _) => Padding(
                            padding: const EdgeInsets.symmetric(horizontal: 4),
                            child: Text(
                              _mmss(c.elapsed),
                              style: TextStyle(fontSize: 14, fontWeight: FontWeight.w700, color: pal.textSoft, fontFeatures: const [FontFeature.tabularFigures()]),
                            ),
                          ),
                        ),
                      if (full)
                        _Ib(
                          icon: Icons.call_end_rounded,
                          label: l.callEnd,
                          size: 48,
                          iconSize: 20,
                          bg: T.danger,
                          fg: Colors.white,
                          round: true,
                          onTap: c.endCall,
                        ),
                      _Ib(icon: Icons.close_rounded, label: l.tblClose, size: 48, onTap: store.close),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
        if (min)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
            child: Row(
              children: [
                if (tot != null) ...[
                  Text(tot.firstWhere((e) => e.isNotEmpty, orElse: () => ''), style: TextStyle(fontSize: 13, color: pal.textSoft)),
                  const SizedBox(width: 8),
                  Flexible(child: Text(_totalMain(t), style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w800), overflow: TextOverflow.ellipsis)),
                  const SizedBox(width: 8),
                ],
                Text('· ${l.tblRowsN(t.totalRows)}', style: TextStyle(fontSize: 13, color: pal.textSoft)),
              ],
            ),
          )
        else ...[
          if (store.pinnedIds.isNotEmpty) _Tabs(c: c),
          if (!t.card && t.viewCount > 0 || t.filter != null && !t.card)
            _Acts(c: c, table: t, pager: full ? pager : null),
          Expanded(
            child: t.card
                ? _CardBody(c: c, table: t)
                : t.viewCount == 0
                ? _EmptyBody(c: c, table: t)
                : _TableGrid(c: c, table: t, colors: colors),
          ),
          _Footer(c: c, table: t, bottomInset: bottomInset),
        ],
      ],
    );
  }

  static String _mmss(Duration d) => '${d.inMinutes.toString().padLeft(2, '0')}:${(d.inSeconds % 60).toString().padLeft(2, '0')}';

  /// Главная цифра «Итого» для свёрнутой панели: первая числовая колонка с итогом.
  static String _totalMain(CallTable t) {
    for (var i = 0; i < t.columns.length; i++) {
      if (t.columns[i].numeric) {
        final s = t.totalText(i);
        if (s != null) return s;
      }
    }
    return '';
  }
}

// ---------------------------------------------------------------------------------------------------------------------

class _Ib extends StatelessWidget {
  final IconData icon;
  final String label;
  final double size;
  final double iconSize;
  final VoidCallback onTap;
  final Color? bg;
  final Color? fg;
  final bool round;
  const _Ib({required this.icon, required this.label, required this.onTap, this.size = 48, this.iconSize = 22, this.bg, this.fg, this.round = false});

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    label: label,
    excludeSemantics: true,
    child: InkResponse(
      onTap: onTap,
      radius: size / 2,
      child: Container(
        width: size,
        height: size,
        alignment: Alignment.center,
        decoration: bg == null ? null : BoxDecoration(color: bg, shape: round ? BoxShape.circle : BoxShape.rectangle, borderRadius: round ? null : BorderRadius.circular(T.radiusButton)),
        child: Icon(icon, size: iconSize, color: fg ?? context.pal.text),
      ),
    ),
  );
}

class _Tabs extends StatelessWidget {
  final AssistantCallController c;
  const _Tabs({required this.c});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final store = c.tables;
    final cur = store.current;
    final tabs = [
      ...store.history.where((t) => store.isPinned(t.id)),
      if (cur != null && !store.isPinned(cur.id)) cur,
    ];
    return SizedBox(
      height: 46,
      child: ListView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 6),
        children: [
          for (final t in tabs)
            Padding(
              padding: const EdgeInsets.only(right: 6),
              child: GestureDetector(
                onTap: () => store.show(t.id),
                child: Container(
                  constraints: const BoxConstraints(minHeight: 40),
                  padding: const EdgeInsets.symmetric(horizontal: 14),
                  alignment: Alignment.center,
                  decoration: BoxDecoration(
                    color: t.id == cur?.id ? (pal.dark ? T.accent : T.navy) : pal.panel,
                    borderRadius: BorderRadius.circular(999),
                    border: Border.all(color: t.id == cur?.id ? Colors.transparent : pal.border),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      if (store.isPinned(t.id)) Padding(padding: const EdgeInsets.only(right: 4), child: Icon(Icons.push_pin_rounded, size: 14, color: t.id == cur?.id ? (pal.dark ? T.navy : Colors.white) : pal.text)),
                      Text(
                        '${t.title}${store.isPinned(t.id) ? ' · ${t.totalRows}' : ''}',
                        style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, color: t.id == cur?.id ? (pal.dark ? T.navy : Colors.white) : pal.text),
                      ),
                    ],
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

class _Acts extends StatelessWidget {
  final AssistantCallController c;
  final CallTable table;
  final Widget? pager;
  const _Acts({required this.c, required this.table, this.pager});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final store = c.tables;
    final bigger = store.textScale < 1.6;
    Widget btn(IconData icon, String text, VoidCallback onTap, {bool on = false}) => Padding(
      padding: const EdgeInsets.only(right: 6),
      child: OutlinedButton.icon(
        style: OutlinedButton.styleFrom(
          minimumSize: const Size(0, 48),
          padding: const EdgeInsets.symmetric(horizontal: 12),
          side: BorderSide(color: on ? T.accent : pal.border),
          backgroundColor: on ? pal.accentSoft : null,
          foregroundColor: pal.text,
          textStyle: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700),
        ),
        onPressed: onTap,
        icon: Icon(icon, size: 18),
        label: Text(text),
      ),
    );
    final sorted = table.sortKey == null ? null : table.columns.where((x) => x.key == table.sortKey).firstOrNull;
    return SizedBox(
      height: 56,
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 4),
        child: Row(
          children: [
          if (pager != null) Padding(padding: const EdgeInsets.only(right: 4), child: Center(child: pager)),
          btn(Icons.format_size_rounded, bigger ? l.tblBigger : l.tblSmaller, () => store.resize(bigger)),
          if (sorted != null) btn(table.desc ? Icons.arrow_downward_rounded : Icons.arrow_upward_rounded, sorted.title, () {}, on: true),
          if (table.filter != null) btn(Icons.filter_alt_off_rounded, l.tblClearFilter, () => store.replace(table.withoutFilter()), on: true),
          btn(Icons.chat_bubble_outline_rounded, l.tblToChat, () => c.exportTable('export_chat', table)),
          btn(Icons.picture_as_pdf_outlined, l.tblPdf, () => c.exportTable('export_pdf', table)),
          btn(Icons.ios_share_rounded, l.tblShare, () => c.exportTable('share', table)),
          btn(Icons.push_pin_outlined, store.isPinned(table.id) ? l.tblPinned : l.tblPin, () => store.isPinned(table.id) ? store.unpin(table.id) : store.pin(table.id), on: store.isPinned(table.id)),
          ],
        ),
      ),
    );
  }
}

class _Footer extends StatelessWidget {
  final AssistantCallController c;
  final CallTable table;
  final double bottomInset;
  const _Footer({required this.c, required this.table, required this.bottomInset});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final t = table;
    final time = '${t.at.hour.toString().padLeft(2, '0')}:${t.at.minute.toString().padLeft(2, '0')}';
    final left = t.remaining;
    return Container(
      decoration: BoxDecoration(color: pal.panel, border: Border(top: BorderSide(color: pal.border))),
      padding: EdgeInsets.fromLTRB(16, 8, 16, 10 + bottomInset),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (t.card && t.objectRoute != null && t.refs.isNotEmpty && t.refs.first != null)
            OutlinedButton(
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
              onPressed: () => c.openTableRow(t, 0),
              child: Text(l.tblOpenObject),
            )
          else if (!t.card && t.viewCount > 0)
            OutlinedButton(
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48), textStyle: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700)),
              onPressed: t.hasMore ? () => c.tables.replace(t.more()) : null,
              child: Text(t.hasMore ? '${l.tblMore(CallTable.pageSize < left ? CallTable.pageSize : left)} · ${l.tblMoreLeft(left)}' : l.tblAllShown),
            ),
          const SizedBox(height: 6),
          Row(
            children: [
              Icon(Icons.info_outline_rounded, size: 14, color: pal.textSoft),
              const SizedBox(width: 6),
              Expanded(child: Text(l.tblSource(t.source.isEmpty ? 'phone' : t.source, time), style: TextStyle(fontSize: 11, color: pal.textSoft), maxLines: 1, overflow: TextOverflow.ellipsis)),
            ],
          ),
          if (t.note != null && t.note!.isNotEmpty)
            Padding(padding: const EdgeInsets.only(top: 4), child: Text(t.note!, style: TextStyle(fontSize: 11, color: pal.warn, fontWeight: FontWeight.w600))),
        ],
      ),
    );
  }
}

class _EmptyBody extends StatelessWidget {
  final AssistantCallController c;
  final CallTable table;
  const _EmptyBody({required this.c, required this.table});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Center(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.table_chart_outlined, size: 36, color: pal.textSoft),
            const SizedBox(height: 8),
            Text(l.tblEmpty, style: TextStyle(fontSize: 15, fontWeight: FontWeight.w700, color: pal.text.withValues(alpha: .8))),
            if (table.filter != null) ...[
              const SizedBox(height: 8),
              Text(l.tblEmptyFilter(table.filter!.describe(table)), textAlign: TextAlign.center, style: TextStyle(color: pal.textSoft)),
              const SizedBox(height: 8),
              OutlinedButton(onPressed: () => c.tables.replace(table.withoutFilter()), child: Text(l.tblClearFilter)),
            ],
          ],
        ),
      ),
    );
  }
}

/// Карточка объекта: «поле — значение».
class _CardBody extends StatelessWidget {
  final AssistantCallController c;
  final CallTable table;
  const _CardBody({required this.c, required this.table});

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final scale = c.tables.textScale;
    return ListView(
      padding: EdgeInsets.zero,
      children: [
        for (final r in table.viewIndices)
          Container(
            constraints: BoxConstraints(minHeight: 44 * (scale >= 1.3 ? 1.18 : 1)),
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 6),
            decoration: BoxDecoration(border: Border(bottom: BorderSide(color: pal.border))),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.center,
              children: [
                Expanded(flex: 4, child: Text(table.cellText(r, 0), style: TextStyle(fontSize: 13 * scale, color: pal.textSoft))),
                const SizedBox(width: 12),
                Expanded(
                  flex: 5,
                  child: Text(
                    table.cellText(r, 1),
                    textAlign: TextAlign.right,
                    style: TextStyle(fontSize: 13 * scale, fontWeight: FontWeight.w700, color: table.isNegative(r, 1) ? pal.danger : pal.text, fontFeatures: const [FontFeature.tabularFigures()]),
                  ),
                ),
              ],
            ),
          ),
      ],
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Сетка таблицы
// ---------------------------------------------------------------------------------------------------------------------

class _TableGrid extends StatefulWidget {
  final AssistantCallController c;
  final CallTable table;
  final _PanelColors colors;
  const _TableGrid({required this.c, required this.table, required this.colors});

  @override
  State<_TableGrid> createState() => _TableGridState();
}

class _TableGridState extends State<_TableGrid> {
  final _h = ScrollController();

  @override
  void dispose() {
    _h.dispose();
    super.dispose();
  }

  static double _measure(BuildContext context, String text, double size, FontWeight w) {
    final tp = TextPainter(
      text: TextSpan(text: text, style: TextStyle(fontSize: size, fontWeight: w, fontFeatures: const [FontFeature.tabularFigures()])),
      textDirection: TextDirection.ltr,
      textScaler: MediaQuery.textScalerOf(context),
      maxLines: 1,
    )..layout();
    return tp.width;
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(builder: (context, box) => _grid(context, box.maxWidth));

  Widget _grid(BuildContext context, double viewW) {
    final t = widget.table;
    final pal = context.pal;
    final scale = widget.c.tables.textScale;
    final fs = 13 * scale;
    final hs = 12 * scale;
    final sys = MediaQuery.textScalerOf(context).scale(1);
    final rowH = (fs * 1.3 * sys + 22).clamp(44.0, 120.0);
    final idx = t.visibleIndices;
    final cols = t.columns.length;
    final widths = <double>[];
    final tapCol = t.objectRoute != null && t.refs.any((e) => e != null);
    for (var ci = 0; ci < cols; ci++) {
      var w = _measure(context, t.columns[ci].title, hs, FontWeight.w700) + (t.columns[ci].key == t.sortKey ? 18 : 4);
      for (final r in idx) {
        final m = _measure(context, t.cellText(r, ci), fs, ci == 0 ? FontWeight.w600 : FontWeight.w400);
        if (m > w) w = m;
      }
      final tt = t.totalText(ci);
      if (tt != null) {
        final m = _measure(context, tt, fs, FontWeight.w800);
        if (m > w) w = m;
      }
      w += 22; // поля 10 + 10 + запас
      final maxW = ci == 0 ? (scale >= 1.3 || sys >= 1.3 ? 170.0 : 150.0) : 220.0 * scale;
      widths.add(w.clamp(ci == 0 ? 72.0 : 56.0, maxW));
    }
    // 2026-10-26 (приёмка 1.18.6, С-9н): на 360 dp при шрифте 1,3 колонка «Сумма» уходила за правый край — главного числа не
    // видно, пока не прокрутишь вбок. Теперь главная денежная колонка (последняя суммируемая; иначе последняя числовая) при
    // нехватке ширины ЗАКРЕПЛЯЕТСЯ рядом с первой колонкой, а первая не шире, чем остаётся от неё (минус запас под прокрутку).
    int? keyCol;
    for (var ci = cols - 1; ci >= 1 && keyCol == null; ci--) {
      if (t.columns[ci].kind == CellKind.money && t.columns[ci].sum) keyCol = ci;
    }
    for (var ci = cols - 1; ci >= 1 && keyCol == null; ci--) {
      if (t.columns[ci].numeric) keyCol = ci;
    }
    final allW = widths.fold<double>(0, (a, b) => a + b) + (tapCol ? 32 : 0);
    if (keyCol != null && allW > viewW) {
      // главная колонка не шире, чем остаётся рядом с минимальной первой (72) и запасом 40 dp под прокрутку остальных
      final keyMax = viewW - 72 - 40;
      if (widths[keyCol] > keyMax) widths[keyCol] = keyMax < 56 ? 56 : keyMax;
      final room = viewW - widths[keyCol] - 40;
      if (widths[0] > room) widths[0] = room < 72 ? 72 : room;
    }
    final pinKey = keyCol != null && keyCol != 1 && allW > viewW;
    final pinned = <int>[0, if (pinKey) keyCol];
    final scrolled = <int>[for (var ci = 1; ci < cols; ci++) if (!(pinKey && ci == keyCol)) ci];
    final pinnedW = pinned.fold<double>(0, (a, ci) => a + widths[ci]);
    final restW = scrolled.fold<double>(0, (a, ci) => a + widths[ci]) + (tapCol ? 32 : 0);
    final totals = t.totalsRow();
    final soft = widget.colors.soft;

    // число в колонке не обрезается «31 0…»: не поместилось — сжимается целиком, прижато вправо
    Widget oneLineNum(bool numeric, Widget text) => numeric ? FittedBox(fit: BoxFit.scaleDown, alignment: Alignment.centerRight, child: text) : text;

    Widget head(int ci) => InkWell(
      onTap: () {
        final s = t.sortedBy(t.columns[ci].key, toggle: true);
        if (s != null) widget.c.tables.replace(s);
      },
      child: Container(
        width: widths[ci],
        height: 44,
        color: soft,
        padding: const EdgeInsets.symmetric(horizontal: 10),
        alignment: t.columns[ci].numeric ? Alignment.centerRight : Alignment.centerLeft,
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Flexible(
              child: Text(
                t.columns[ci].title,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(fontSize: hs, fontWeight: FontWeight.w700, color: t.columns[ci].key == t.sortKey ? pal.text : pal.textSoft),
              ),
            ),
            if (t.columns[ci].key == t.sortKey) Icon(t.desc ? Icons.arrow_downward_rounded : Icons.arrow_upward_rounded, size: 14, color: pal.text),
          ],
        ),
      ),
    );

    Widget cell(int pos, int r, int ci) {
      final neg = t.isNegative(r, ci);
      return GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: tapCol ? () => widget.c.openTableRow(t, r) : null,
        child: Container(
          width: widths[ci],
          height: rowH,
          padding: const EdgeInsets.symmetric(horizontal: 10),
          alignment: t.columns[ci].numeric ? Alignment.centerRight : Alignment.centerLeft,
          decoration: BoxDecoration(
            color: pos.isOdd ? widget.colors.zebra : pal.panel,
            border: Border(bottom: BorderSide(color: pal.border)),
          ),
          child: oneLineNum(
            t.columns[ci].numeric,
            Text(
              t.cellText(r, ci),
              maxLines: 1,
              overflow: t.columns[ci].numeric ? TextOverflow.visible : TextOverflow.ellipsis,
              softWrap: false,
              style: TextStyle(
                fontSize: fs,
                fontWeight: ci == 0 ? FontWeight.w600 : (t.columns[ci].kind == CellKind.money && t.columns[ci].sum ? FontWeight.w700 : FontWeight.w400),
                color: neg ? pal.danger : pal.text,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
          ),
        ),
      );
    }

    Widget tot(int ci) => Container(
      width: widths[ci],
      height: 44,
      color: widget.colors.total,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      alignment: t.columns[ci].numeric ? Alignment.centerRight : Alignment.centerLeft,
      child: oneLineNum(
        t.columns[ci].numeric,
        Text(
          totals![ci],
          maxLines: 1,
          overflow: t.columns[ci].numeric ? TextOverflow.visible : TextOverflow.ellipsis,
          softWrap: false,
          style: TextStyle(fontSize: fs, fontWeight: FontWeight.w800, color: t.isTotalNegative(ci) ? pal.danger : pal.text, fontFeatures: const [FontFeature.tabularFigures()]),
        ),
      ),
    );

    // Шапка и «Итого» не прокручиваются сами, а повторяют смещение тела по горизонтали.
    Widget shifted(Widget child, double height) => SizedBox(
      height: height,
      child: ClipRect(
        child: AnimatedBuilder(
          animation: _h,
          builder: (_, _) {
            final off = _h.hasClients ? _h.offset : 0.0;
            return OverflowBox(
              alignment: Alignment.topLeft,
              minWidth: 0,
              maxWidth: restW + 400,
              child: Transform.translate(offset: Offset(-off, 0), child: child),
            );
          },
        ),
      ),
    );

    return DecoratedBox(
      decoration: BoxDecoration(border: Border(top: BorderSide(color: pal.border))),
      child: Column(
        children: [
          // шапка
          Row(
            children: [
              SizedBox(width: pinnedW, child: Row(children: [for (final ci in pinned) head(ci)])),
              Expanded(
                child: shifted(
                  Row(mainAxisSize: MainAxisSize.min, children: [for (final ci in scrolled) head(ci), if (tapCol) Container(width: 32, height: 44, color: soft)]),
                  44,
                ),
              ),
            ],
          ),
          // тело
          Expanded(
            child: SingleChildScrollView(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Container(
                    decoration: BoxDecoration(border: Border(right: BorderSide(color: pal.border))),
                    child: Column(children: [for (var p = 0; p < idx.length; p++) Row(children: [for (final ci in pinned) cell(p, idx[p], ci)])]),
                  ),
                  Expanded(
                    child: SingleChildScrollView(
                      controller: _h,
                      scrollDirection: Axis.horizontal,
                      child: Column(
                        children: [
                          for (var p = 0; p < idx.length; p++)
                            Row(
                              children: [
                                for (final ci in scrolled) cell(p, idx[p], ci),
                                if (tapCol)
                                  Container(
                                    width: 32,
                                    height: rowH,
                                    alignment: Alignment.centerRight,
                                    padding: const EdgeInsets.only(right: 6),
                                    decoration: BoxDecoration(color: p.isOdd ? widget.colors.zebra : pal.panel, border: Border(bottom: BorderSide(color: pal.border))),
                                    child: Icon(Icons.chevron_right_rounded, size: 16, color: pal.textSoft),
                                  ),
                              ],
                            ),
                        ],
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
          // «Итого»
          if (totals != null)
            Container(
              decoration: BoxDecoration(border: Border(top: BorderSide(color: T.borderStrong))),
              child: Row(
                children: [
                  SizedBox(width: pinnedW, child: Row(children: [for (final ci in pinned) tot(ci)])),
                  Expanded(
                    child: shifted(
                      Row(mainAxisSize: MainAxisSize.min, children: [for (final ci in scrolled) tot(ci), if (tapCol) Container(width: 32, height: 44, color: widget.colors.total)]),
                      44,
                    ),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}
