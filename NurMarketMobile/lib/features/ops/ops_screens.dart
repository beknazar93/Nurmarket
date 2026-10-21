import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import 'ops_repository.dart';
import '../../core/util/audit.dart';

/// 13.10: «Внесение / изъятие» — деньги в ящик смены и из него, как в кассе (construction/cashflows/).
class CashOpScreen extends ConsumerStatefulWidget {
  const CashOpScreen({super.key});
  @override
  ConsumerState<CashOpScreen> createState() => _CashOpScreenState();
}

class _CashOpScreenState extends ConsumerState<CashOpScreen> {
  bool _withdrawal = false;
  final _amount = TextEditingController();
  final _reason = TextEditingController();
  bool _busy = false;
  List<(DateTime?, String, double, bool)>? _journal;

  @override
  void initState() {
    super.initState();
    _loadJournal();
  }

  /// 13.10, макет Т25: внесения и изъятия этой смены — под формой.
  Future<void> _loadJournal() async {
    final shift = ref.read(shiftProvider).shift;
    if (shift == null) return;
    try {
      final rows = await ref.read(opsRepoProvider).shiftCashFlows(shift.id);
      if (mounted) setState(() => _journal = rows);
    } on ApiException {
      // без связи — журнал не показываем
    }
  }

  Future<void> _submit() async {
    final l = context.l;
    final amount = parseNum(_amount.text);
    final shift = ref.read(shiftProvider).shift;
    if (amount == null || amount <= 0) return;
    if (shift == null || shift.cashboxId == null) {
      toast(context, l.openShiftFirst, error: true);
      return;
    }
    setState(() => _busy = true);
    try {
      await ref
          .read(opsRepoProvider)
          .cashFlow(shiftId: shift.id, cashboxId: shift.cashboxId!, withdrawal: _withdrawal, amount: amount, reason: _reason.text.trim());
      await ref.read(shiftProvider.notifier).refresh();
      if (!mounted) return;
      Audit.log(
        'cash',
        '${_withdrawal ? l.cashOut : l.cashIn}${_reason.text.trim().isEmpty ? '' : ' · ${_reason.text.trim()}'}',
        amount: _withdrawal ? -amount : amount,
      );
      toast(context, '${l.cashOpDone}: ${_withdrawal ? '−' : '+'}${money(amount)} ${l.som}');
      context.pop();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final shift = ref.watch(shiftProvider).shift;
    return Scaffold(
      appBar: ScreenTitle(l.cashInOut, subtitle: shift == null ? l.shiftClosed : '${l.shouldBeInDrawer}: ${money(shift.expectedCash)} ${l.som}'),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          SegmentedButton<bool>(
            segments: [
              ButtonSegment(value: false, label: Text(l.cashIn)),
              ButtonSegment(value: true, label: Text(l.cashOut)),
            ],
            selected: {_withdrawal},
            onSelectionChanged: (s) => setState(() => _withdrawal = s.first),
          ),
          const SizedBox(height: 16),
          FieldLabel(l.amount),
          TextField(
            controller: _amount,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            style: const TextStyle(fontSize: 24, fontWeight: FontWeight.w800),
            autofocus: true,
          ),
          const SizedBox(height: 16),
          FieldLabel(l.reason),
          Wrap(
            spacing: 6,
            children: [
              for (final r in [l.reasonChange, l.reasonCollection, l.reasonExpense, l.reasonOther])
                ActionChip(label: Text(r), onPressed: () => setState(() => _reason.text = r)),
            ],
          ),
          const SizedBox(height: 8),
          TextField(controller: _reason),
          const SizedBox(height: 24),
          FilledButton(onPressed: _busy ? null : _submit, child: Text(_withdrawal ? l.cashOut : l.cashIn)),
          if (_journal != null) ...[
            const SizedBox(height: 20),
            FieldLabel(l.cashJournal),
            if (_journal!.isEmpty) Text(l.noCashOps, style: TextStyle(color: context.pal.textSoft)),
            for (final (at, name, amount, outflow) in _journal!)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 6),
                child: Row(
                  children: [
                    Expanded(
                      child: Text(name, style: const TextStyle(fontWeight: FontWeight.w600)),
                    ),
                    if (at != null) Text('${TimeOfDay.fromDateTime(at).format(context)}  ', style: TextStyle(color: context.pal.textSoft, fontSize: 12)),
                    Text(
                      '${outflow ? '−' : '+'}${money(amount)} ${l.som}',
                      style: TextStyle(fontWeight: FontWeight.w800, color: outflow ? T.danger : T.success),
                    ),
                  ],
                ),
              ),
          ],
        ],
      ),
    );
  }
}

/// 13.10: «Долги клиентов» — список должников и «Принять оплату долга» (clients/{id}/pay-debt/, ключ повтора).
class DebtsScreen extends ConsumerStatefulWidget {
  const DebtsScreen({super.key});
  @override
  ConsumerState<DebtsScreen> createState() => _DebtsScreenState();
}

class _DebtsScreenState extends ConsumerState<DebtsScreen> {
  List<Debtor>? _rows;
  String? _error;
  final _q = TextEditingController();

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final rows = await ref.read(opsRepoProvider).debtors();
      if (mounted) setState(() => _rows = rows);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    }
  }

  Future<void> _pay(Debtor d) async {
    final l = context.l;
    final amount = TextEditingController(text: d.debt.toStringAsFixed(d.debt == d.debt.roundToDouble() ? 0 : 2));
    var method = 'cash';
    final key = OpsRepository.newKey(); // один ключ на попытку: повтор после обрыва не проведёт деньги дважды
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => AlertDialog(
          title: Text(d.name),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('${l.debt}: ${money(d.debt)} ${l.som}', style: const TextStyle(fontWeight: FontWeight.w700)),
              const SizedBox(height: 12),
              TextField(
                controller: amount,
                keyboardType: const TextInputType.numberWithOptions(decimal: true),
                decoration: InputDecoration(labelText: l.amount),
              ),
              const SizedBox(height: 12),
              SegmentedButton<String>(
                segments: [
                  ButtonSegment(value: 'cash', label: Text(l.cash)),
                  ButtonSegment(value: 'transfer', label: Text(l.card)),
                ],
                selected: {method},
                onSelectionChanged: (s) => set(() => method = s.first),
              ),
            ],
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
            FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.payDebt)),
          ],
        ),
      ),
    );
    final sum = parseNum(amount.text);
    if (ok != true || sum == null || sum <= 0 || !mounted) return;
    try {
      await ref
          .read(opsRepoProvider)
          .payDebt(clientId: d.clientId, amount: sum > d.debt ? d.debt : sum, method: method, shiftId: ref.read(shiftProvider).shift?.id, key: key);
      ref.read(shiftProvider.notifier).refresh();
      if (!mounted) return;
      toast(context, '${l.payDebtDone}: ${money(sum)} ${l.som}');
      setState(() => _rows = null);
      await _load();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final q = _q.text.trim().toLowerCase();
    final rows = (_rows ?? []).where((d) => q.isEmpty || d.name.toLowerCase().contains(q) || d.phone.contains(q)).toList();
    final total = (_rows ?? []).fold<double>(0, (s, d) => s + d.debt);
    return Scaffold(
      appBar: ScreenTitle(l.debts, subtitle: _rows == null ? null : '${l.debtorsTotal}: ${money(total)} ${l.som}'),
      body: _error != null
          ? Center(
              child: Text(_error!, style: const TextStyle(color: T.danger)),
            )
          : _rows == null
          ? const Center(child: CircularProgressIndicator())
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
                children: [
                  TextField(
                    controller: _q,
                    onChanged: (_) => setState(() {}),
                    decoration: InputDecoration(prefixIcon: const Icon(Icons.search), hintText: l.clientSearch),
                  ),
                  const SizedBox(height: 8),
                  if (rows.isEmpty)
                    Padding(
                      padding: const EdgeInsets.all(24),
                      child: Center(
                        child: Text(l.noDebts, style: TextStyle(color: pal.textSoft)),
                      ),
                    ),
                  for (final d in rows)
                    Padding(
                      padding: const EdgeInsets.only(bottom: 6),
                      child: Panel(
                        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                        child: InkWell(
                          onTap: () => _pay(d),
                          child: Row(
                            children: [
                              Expanded(
                                child: Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Text(d.name, style: const TextStyle(fontWeight: FontWeight.w700)),
                                    Text(
                                      [d.phone, '${d.salesCount} ${l.check.toLowerCase()}'].where((x) => x.isNotEmpty).join(' · '),
                                      style: TextStyle(fontSize: 12, color: pal.textSoft),
                                    ),
                                  ],
                                ),
                              ),
                              Text(
                                '${money(d.debt)} ${l.som}',
                                style: const TextStyle(fontWeight: FontWeight.w800, color: T.danger),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                ],
              ),
            ),
    );
  }
}

/// 13.10: «Отложенные чеки» — хранятся на телефоне; вернуть в чек или удалить.
class HeldScreen extends ConsumerStatefulWidget {
  const HeldScreen({super.key});
  @override
  ConsumerState<HeldScreen> createState() => _HeldScreenState();
}

class _HeldScreenState extends ConsumerState<HeldScreen> {
  List<HeldCart>? _rows;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final rows = await ref.read(opsRepoProvider).held();
    if (mounted) setState(() => _rows = rows);
  }

  Future<void> _restore(HeldCart h) async {
    final l = context.l;
    if (!ref.read(cartProvider).isEmpty) {
      toast(context, l.checkBusy, error: true);
      return;
    }
    ref.read(cartProvider.notifier).restore(h.cart);
    await ref.read(opsRepoProvider).removeHeld(h.id);
    if (mounted) context.go('/sale');
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Scaffold(
      appBar: ScreenTitle(l.heldChecks),
      body: _rows == null
          ? const Center(child: CircularProgressIndicator())
          : _rows!.isEmpty
          ? Center(
              child: Text(l.heldEmpty, style: TextStyle(color: pal.textSoft)),
            )
          : ListView(
              padding: const EdgeInsets.all(16),
              children: [
                for (final h in _rows!)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 8),
                    child: Panel(
                      padding: const EdgeInsets.all(12),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              Expanded(
                                child: Text(
                                  h.note.isEmpty ? '${l.check} · ${TimeOfDay.fromDateTime(h.at).format(context)}' : h.note,
                                  style: const TextStyle(fontWeight: FontWeight.w800),
                                ),
                              ),
                              Text('${money(h.cart.total)} ${l.som}', style: const TextStyle(fontWeight: FontWeight.w800)),
                            ],
                          ),
                          const SizedBox(height: 4),
                          Text(
                            h.cart.lines.map((x) => x.name).take(4).join(', '),
                            maxLines: 2,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(fontSize: 12, color: pal.textSoft),
                          ),
                          const SizedBox(height: 8),
                          Row(
                            children: [
                              Expanded(
                                child: FilledButton(onPressed: () => _restore(h), child: Text(l.restoreCheck)),
                              ),
                              const SizedBox(width: 8),
                              OutlinedButton(
                                onPressed: () async {
                                  await ref.read(opsRepoProvider).removeHeld(h.id);
                                  await _load();
                                },
                                child: Text(l.delete),
                              ),
                            ],
                          ),
                        ],
                      ),
                    ),
                  ),
              ],
            ),
    );
  }
}

/// 13.10: «Возврат по чеку» — весь чек или выбранные строки; деньги — покупателю из ящика (сервер учтёт в смене).
class ReturnScreen extends ConsumerStatefulWidget {
  final String saleId;
  final String number;
  final Map<String, dynamic> sale;
  const ReturnScreen({super.key, required this.saleId, required this.number, required this.sale});
  @override
  ConsumerState<ReturnScreen> createState() => _ReturnScreenState();
}

class _ReturnScreenState extends ConsumerState<ReturnScreen> {
  late final List<({String id, String name, double qty, double price, double total})> _lines;
  late final List<double> _pick;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    dynamic items = widget.sale['items'] ?? widget.sale['lines'];
    if (items == null && widget.sale['cart'] is Map) items = (widget.sale['cart'] as Map)['items'];
    _lines = [
      for (final m in asList(items))
        (
          id: str(m, 'id') ?? '',
          name: str(m, 'product_name') ?? str(m, 'name') ?? '',
          qty: num0(m, 'quantity') > 0 ? num0(m, 'quantity') : num0(m, 'qty'),
          price: num0(m, 'unit_price') > 0 ? num0(m, 'unit_price') : num0(m, 'price'),
          total: num0(m, 'total') > 0 ? num0(m, 'total') : num0(m, 'line_total'),
        ),
    ].where((x) => x.id.isNotEmpty && x.qty > 0).toList();
    _pick = List.filled(_lines.length, 0);
  }

  double get _sum {
    var s = 0.0;
    for (var i = 0; i < _lines.length; i++) {
      final unit = _lines[i].qty > 0 ? _lines[i].total / _lines[i].qty : _lines[i].price;
      s += unit * _pick[i];
    }
    return s;
  }

  Future<void> _submit({required bool whole}) async {
    final l = context.l;
    final picked = [
      for (var i = 0; i < _lines.length; i++)
        if (_pick[i] > 0) (_lines[i].id, _pick[i]),
    ];
    if (!whole && picked.isEmpty) return;
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text('${l.returnTitle} №${widget.number}'),
        content: Text('${l.returnToCustomer}: ${money(whole ? _lines.fold<double>(0, (s, x) => s + x.total) : _sum)} ${l.som}'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(whole ? l.returnAll : l.returnSelected)),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    setState(() => _busy = true);
    try {
      await ref.read(opsRepoProvider).returnSale(widget.saleId, whole ? const [] : picked);
      Audit.log('return', '${l.auditReturn} · ${whole ? l.returnAll : l.returnSelected}');
      ref.read(shiftProvider.notifier).refresh();
      if (!mounted) return;
      toast(context, l.returnDone);
      context.pop(true);
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
    return Scaffold(
      appBar: ScreenTitle(l.returnTitle, subtitle: '${l.check} №${widget.number}'),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          for (var i = 0; i < _lines.length; i++)
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Panel(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(_lines[i].name, style: const TextStyle(fontWeight: FontWeight.w700)),
                          Text(
                            '${qtyText(_lines[i].qty)} × ${money(_lines[i].price)} = ${money(_lines[i].total)}',
                            style: TextStyle(fontSize: 12, color: pal.textSoft),
                          ),
                        ],
                      ),
                    ),
                    IconButton.outlined(
                      onPressed: _pick[i] <= 0 ? null : () => setState(() => _pick[i] = (_pick[i] - 1).clamp(0, _lines[i].qty)),
                      icon: const Icon(Icons.remove),
                    ),
                    SizedBox(
                      width: 40,
                      child: Text(
                        qtyText(_pick[i]),
                        textAlign: TextAlign.center,
                        style: const TextStyle(fontWeight: FontWeight.w800),
                      ),
                    ),
                    IconButton.filled(
                      style: IconButton.styleFrom(backgroundColor: context.pal.action),
                      onPressed: _pick[i] >= _lines[i].qty ? null : () => setState(() => _pick[i] = (_pick[i] + 1).clamp(0, _lines[i].qty)),
                      icon: Icon(Icons.add, color: context.pal.onAction),
                    ),
                  ],
                ),
              ),
            ),
          const SizedBox(height: 12),
          Text('${l.returnToCustomer}: ${money(_sum)} ${l.som}', style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
          const SizedBox(height: 12),
          FilledButton(onPressed: _busy || _sum <= 0 ? null : () => _submit(whole: false), child: Text(l.returnSelected)),
          const SizedBox(height: 8),
          OutlinedButton(onPressed: _busy ? null : () => _submit(whole: true), child: Text(l.returnAll)),
        ],
      ),
    );
  }
}
