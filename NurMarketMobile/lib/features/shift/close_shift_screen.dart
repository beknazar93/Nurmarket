import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';

/// Закрыть смену (Т18): итоги смены, «должно быть в кассе», пересчёт наличных.
/// Неотправленные чеки сначала уходят на сервер — иначе их не будет в отчёте смены.
class CloseShiftScreen extends ConsumerStatefulWidget {
  const CloseShiftScreen({super.key});
  @override
  ConsumerState<CloseShiftScreen> createState() => _CloseShiftScreenState();
}

class _CloseShiftScreenState extends ConsumerState<CloseShiftScreen> {
  final _cash = TextEditingController();
  bool _busy = false;

  /// 2026-10-22 (мобилка, деньги): внесения и изъятия смены — в итогах и X-отчёте.
  double? _inflow;
  double? _outflow;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _reload());
  }

  @override
  void dispose() {
    _cash.dispose();
    super.dispose();
  }

  Future<void> _reload() async {
    await ref.read(shiftProvider.notifier).refresh();
    final shift = ref.read(shiftProvider).shift;
    if (shift == null) return;
    try {
      final rows = await ref.read(opsRepoProvider).shiftCashFlows(shift.id);
      if (!mounted) return;
      setState(() {
        _inflow = rows.where((r) => !r.$4).fold<double>(0, (s, r) => s + r.$3);
        _outflow = rows.where((r) => r.$4).fold<double>(0, (s, r) => s + r.$3);
      });
    } on ApiException {
      // без связи — без строк внесений
    }
  }

  /// 2026-10-22 (мобилка, деньги): неотправленное и не принятое ЭТОГО аккаунта — прямо из базы, а не по счётчику,
  /// который мог устареть (закрытие смены по старому числу пропускало только что вставшие в очередь чеки).
  Future<(int pending, int failed)> _queueCounts() async {
    final db = ref.read(databaseProvider);
    final me = OutboxMeta.ownerOf(ref.read(sessionProvider));
    final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
    final rows = await (db.select(db.outbox)..where((o) => o.status.isIn(const ['pending', 'failed']))).get();
    final mine = rows.where((r) => OutboxMeta.belongsTo(r, me, legacyOwner: legacy));
    return (mine.where((r) => r.status == 'pending').length, mine.where((r) => r.status == 'failed' && r.kind == 'sale').length);
  }

  Future<void> _close() async {
    final l = context.l;
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await ref.read(syncProvider).runOnce();
      final (pending, failed) = await _queueCounts();
      if (pending > 0) {
        if (mounted) toast(context, l.closeShiftPending(pending), error: true);
        return;
      }
      if (failed > 0) {
        // Не принятые сервером чеки не попадут в отчёт смены — сначала разобрать их.
        if (mounted) toast(context, l.closeShiftFailed(failed), error: true);
        return;
      }
      if (!mounted) return;
      final shift = ref.read(shiftProvider).shift;
      final counted = parseNum(_cash.text);
      final ok = await showDialog<bool>(
        context: context,
        builder: (c) => AlertDialog(
          title: Text(l.closeShiftAsk),
          content: Text(
            [
              '${l.revenue}: ${money(shift?.salesTotal ?? 0)} ${l.som}',
              '${l.shouldBeInDrawer}: ${money(shift?.expectedCash ?? 0)} ${l.som}',
              if (counted != null) '${l.countedCash}: ${money(counted)} ${l.som}',
              l.closeShiftAskSub,
            ].join('\n'),
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.closeShift)),
          ],
        ),
      );
      if (ok != true) return;
      await ref.read(shiftProvider.notifier).close(counted);
      if (!mounted) return;
      toast(context, l.shiftClosedOk);
      context.go('/home');
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// 2026-10-22 (мобилка, деньги): X-отчёт — итоги смены без закрытия (свежие с сервера), как на кассе ПК.
  Future<void> _xReport() async {
    final l = context.l;
    await _reload();
    if (!mounted) return;
    final s = ref.read(shiftProvider).shift;
    if (s == null) return;
    final (pending, failed) = await _queueCounts();
    if (!mounted) return;
    String row(String k, double v) => '$k: ${money(v)} ${l.som}';
    await showDialog<void>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.xReport),
        content: Text(
          [
            if (s.openedAt != null) l.sinceChecks(TimeOfDay.fromDateTime(s.openedAt!).format(context), s.salesCount),
            row(l.openingCashLabel, s.openingCash),
            row(l.cash, s.cashSales),
            row(l.card, s.nonCashSales),
            if (s.debtSales > 0) row(l.debt, s.debtSales),
            row(l.revenue, s.salesTotal),
            if (_inflow != null) row(l.cashIn, _inflow!),
            if (_outflow != null) row(l.cashOut, _outflow!),
            row(l.shouldBeInDrawer, s.expectedCash),
            if (pending > 0) l.closeShiftPending(pending),
            if (failed > 0) l.closeShiftFailed(failed),
          ].join('\n'),
        ),
        actions: [FilledButton(onPressed: () => Navigator.pop(c), child: Text(l.done))],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final shift = ref.watch(shiftProvider).shift;
    final opened = shift?.openedAt;
    Widget row(String label, double v, {Color? color, bool bold = false}) => Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        children: [
          Text(
            label,
            style: TextStyle(color: bold ? null : pal.textSoft, fontWeight: bold ? FontWeight.w800 : FontWeight.w500, fontSize: bold ? 16 : 14),
          ),
          const Spacer(),
          Text(
            '${money(v)} ${l.som}',
            style: TextStyle(fontWeight: FontWeight.w800, fontSize: bold ? 16 : 14, color: color),
          ),
        ],
      ),
    );
    return Scaffold(
      appBar: ScreenTitle(
        l.closeShift,
        subtitle: opened == null ? null : l.sinceChecks(TimeOfDay.fromDateTime(opened).format(context), shift?.salesCount ?? 0),
      ),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
        children: [
          Panel(
            padding: const EdgeInsets.all(16),
            child: Column(
              children: [
                row(l.cash, shift?.cashSales ?? 0),
                row(l.card, shift?.nonCashSales ?? 0),
                if ((shift?.debtSales ?? 0) > 0) row(l.debt, shift!.debtSales),
                const Divider(height: 18),
                row(l.revenue, shift?.salesTotal ?? 0, bold: true),
                // 2026-10-22: начало смены, внесения и изъятия — откуда «должно быть в кассе».
                if ((shift?.openingCash ?? 0) > 0) row(l.openingCashLabel, shift!.openingCash),
                if ((_inflow ?? 0) > 0) row(l.cashIn, _inflow!),
                if ((_outflow ?? 0) > 0) row(l.cashOut, -_outflow!),
              ],
            ),
          ),
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: _busy || shift == null ? null : _xReport,
            icon: const Icon(Icons.receipt_long_outlined),
            label: Text(l.xReport),
          ),
          const SizedBox(height: 12),
          Container(
            padding: const EdgeInsets.all(16),
            decoration: BoxDecoration(color: T.navy, borderRadius: BorderRadius.circular(18)),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Text(l.shouldBeInDrawer, style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 13)),
                    const Spacer(),
                    Text(
                      '${money(shift?.expectedCash ?? 0)} ${l.som}',
                      style: const TextStyle(color: T.accent, fontWeight: FontWeight.w800),
                    ),
                  ],
                ),
                const SizedBox(height: 10),
                Text(
                  l.recountCash.toUpperCase(),
                  style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w700, letterSpacing: .3),
                ),
                const SizedBox(height: 10),
                TextField(
                  controller: _cash,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true),
                  style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800, color: T.text),
                  decoration: InputDecoration(hintText: l.howMuchInDrawer, fillColor: Colors.white),
                  onChanged: (_) => setState(() {}),
                ),
                const SizedBox(height: 8),
                if (parseNum(_cash.text) case final v? when shift != null && (v - shift.expectedCash).abs() > 0.004)
                  Text(
                    l.cashDiff(money(v - shift.expectedCash)),
                    style: const TextStyle(color: T.accent, fontSize: 12.5, fontWeight: FontWeight.w700),
                  )
                else
                  Text(l.cashDiffHint, style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12)),
              ],
            ),
          ),
        ],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 6, 16, 12),
          child: FilledButton(
            onPressed: _busy || shift == null ? null : _close,
            child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5, color: T.navy)) : Text(l.closeShift),
          ),
        ),
      ),
    );
  }
}
