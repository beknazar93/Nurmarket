import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
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

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => ref.read(shiftProvider.notifier).refresh());
  }

  Future<void> _close() async {
    final l = context.l;
    setState(() => _busy = true);
    try {
      await ref.read(syncProvider).runOnce();
      final pending = ref.read(pendingCountProvider).value ?? 0;
      if (pending > 0) {
        if (mounted) toast(context, l.closeShiftPending(pending), error: true);
        return;
      }
      await ref.read(shiftProvider.notifier).close(parseNum(_cash.text));
      if (!mounted) return;
      toast(context, l.shiftClosedOk);
      context.go('/home');
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
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
              ],
            ),
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
