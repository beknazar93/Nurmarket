import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../admin/admin_repository.dart' show DeletionRow;
import '../admin/admin_screens.dart' show adminRepoProvider;
import '../sale/cart.dart' show roundMoney;
import 'ops_docs.dart';
import 'ops_repository.dart';
import 'return_ledger.dart';
import 'tape_doc.dart';
import '../../core/util/audit.dart';
import '../../core/ai/assistant/ai_handles.dart';
import '../../core/ai/assistant/screen_registry.dart' show productNameMatchesQuery;
import '../../core/ai/assistant/tool_registry.dart' show ToolStatus;

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
    // 2026-10-22 (мобилка, деньги): изъятие не больше, чем должно быть в ящике (по данным сервера).
    if (_withdrawal && shift.expectedCash > 0 && amount > shift.expectedCash + 0.004) {
      toast(context, l.cashOutTooMuch(money(shift.expectedCash)), error: true);
      return;
    }
    if (_busy) return;
    setState(() => _busy = true);
    try {
      // 2026-10-22: ключ (source_id) живёт до успеха — повтор после обрыва не внесёт деньги дважды.
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
      // 2026-10-26 (живой аудит, П5): каждый денежный шаг оставляет документ — ордер внесения/изъятия с печатью и PDF.
      final doc = cashOrderDoc(
        l,
        ref.read(sessionProvider),
        withdrawal: _withdrawal,
        amount: amount,
        reason: _reason.text.trim(),
        drawerAfter: ref.read(shiftProvider).shift?.expectedCash,
      );
      await Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => TapeDocScreen(doc: doc, title: doc.title)));
      if (mounted) context.pop();
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

  /// 2026-10-26 (живой аудит, П5): список взят из памяти телефона (нет связи) — рядом «Повторить».
  bool _stale = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final repo = ref.read(opsRepoProvider);
    final owner = OutboxMeta.ownerOf(ref.read(sessionProvider));
    try {
      final rows = await repo.debtors();
      if (mounted) {
        setState(() {
          _rows = rows;
          _error = null;
          _stale = false;
        });
      }
      await repo.cacheDebtors(owner, rows);
    } on ApiException catch (e) {
      final saved = _rows ?? await repo.cachedDebtors(owner);
      if (mounted) {
        setState(() {
          _error = e.message;
          if (saved != null) {
            _rows = saved;
            _stale = true;
          }
        });
      }
    }
  }

  /// 2026-10-22 (мобилка, деньги): оплата долга идёт — второе нажатие не начинает вторую оплату.
  bool _paying = false;

  Future<void> _pay(Debtor d) async {
    if (_paying) return;
    final l = context.l;
    final amount = TextEditingController(text: d.debt.toStringAsFixed(d.debt == d.debt.roundToDouble() ? 0 : 2));
    var method = 'cash';
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => StatefulBuilder(
        builder: (c, set) {
          // 2026-10-22 (мобилка, деньги): переплата не обрезается молча — к зачёту долг, остальное — сдача (наличными).
          final got = parseNum(amount.text) ?? 0;
          final toDebt = got > d.debt ? d.debt : got;
          final change = method == 'cash' && got > d.debt ? roundMoney(got - d.debt) : 0.0;
          return AlertDialog(
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
                  decoration: InputDecoration(labelText: method == 'cash' ? l.received : l.amount),
                  onChanged: (_) => set(() {}),
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
                if (got > 0) ...[
                  const SizedBox(height: 10),
                  Text(l.payDebtToDebt(money(toDebt)), style: const TextStyle(fontWeight: FontWeight.w700)),
                  if (change > 0)
                    Text(
                      '${l.change}: ${money(change)} ${l.som}',
                      style: const TextStyle(fontWeight: FontWeight.w800, color: T.success, fontSize: 16),
                    ),
                  if (method != 'cash' && got > d.debt + 0.004) Text(l.payDebtOverCard, style: const TextStyle(color: T.danger, fontSize: 12.5)),
                ],
              ],
            ),
            actions: [
              TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
              FilledButton(onPressed: method != 'cash' && got > d.debt + 0.004 ? null : () => Navigator.pop(c, true), child: Text(l.payDebt)),
            ],
          );
        },
      ),
    );
    final got = parseNum(amount.text);
    if (ok != true || got == null || got <= 0 || !mounted) return;
    final sum = got > d.debt ? d.debt : got;
    final change = method == 'cash' && got > d.debt ? roundMoney(got - d.debt) : 0.0;
    setState(() => _paying = true);
    try {
      // Ключ повтора — в репозитории, живёт до успеха: повтор после обрыва не проведёт деньги дважды.
      await ref.read(opsRepoProvider).payDebt(clientId: d.clientId, amount: sum, method: method, shiftId: ref.read(shiftProvider).shift?.id);
      Audit.log('cash', '${l.payDebtDone} · ${d.name}', amount: sum);
      ref.read(shiftProvider.notifier).refresh();
      if (!mounted) return;
      toast(context, '${l.payDebtDone}: ${money(sum)} ${l.som}${change > 0 ? ' · ${l.change}: ${money(change)} ${l.som}' : ''}');
      // 2026-10-26 (живой аудит, П5): после оплаты долга — квитанция «Принято N, остаток M» с печатью, PDF и отправкой.
      final left = debtLeftAfter(d.debt, sum);
      final doc = debtReceiptDoc(
        l,
        ref.read(sessionProvider),
        clientName: d.name,
        phone: d.phone,
        debtBefore: d.debt,
        accepted: sum,
        method: method,
        change: change,
      );
      await Navigator.of(context).push(
        MaterialPageRoute<void>(
          builder: (_) => TapeDocScreen(
            doc: doc,
            title: l.debtReceiptTitle,
            subtitle: d.name,
            banner: _AcceptedBanner(l.debtReceiptAcceptedLine('${money(sum)} ${l.som}', '${money(left)} ${l.som}')),
          ),
        ),
      );
      if (!mounted) return;
      setState(() => _rows = null);
      await _load();
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _paying = false);
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
      body: _rows == null && _error != null
          ? _RetryBox(
              message: '${l.debtsLoadFailed}: $_error',
              onRetry: () {
                setState(() => _error = null);
                _load();
              },
            )
          : _rows == null
          ? const Center(child: CircularProgressIndicator())
          : RefreshIndicator(
              onRefresh: _load,
              child: ListView(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
                children: [
                  if (_paying) const LinearProgressIndicator(),
                  if (_stale)
                    Padding(
                      padding: const EdgeInsets.only(bottom: 8),
                      child: _RetryBox(message: l.listCachedNote, onRetry: _load, compact: true),
                    ),
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
                                  // 2026-10-26 (живой аудит, П5): удаление отложенного чека — в журнал удалений, как строки чека.
                                  final who = ref.read(sessionProvider)?.userName ?? '';
                                  final name = '${l.heldChecks}: ${h.note.isEmpty ? TimeOfDay.fromDateTime(h.at).format(context) : h.note}';
                                  final qty = h.cart.lines.fold<double>(0, (a, x) => a + x.qty);
                                  await ref.read(adminRepoProvider).logDeletion(DeletionRow(DateTime.now(), name, qty, h.cart.total, l.delete, who));
                                  Audit.log('delete', '$name · ${money(h.cart.total)} ${l.som}', amount: h.cart.total);
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
/// 2026-10-22 (мобилка, деньги, К-3): сумма — как окно возврата кассы (ReturnSaleDialog + SaleReturnLedger): доля
/// скидки на чек, уже возвращённое (и по строкам, и по сумме), потолок = оплачено; продажа «в долг» уменьшает долг,
/// а не выдаётся наличными; сумма сервера меньше расчёта — берём серверную. Ключ повтора живёт до успеха.
class ReturnScreen extends ConsumerStatefulWidget {
  final String saleId;
  final String number;
  final Map<String, dynamic> sale;
  const ReturnScreen({super.key, required this.saleId, required this.number, required this.sale});
  @override
  ConsumerState<ReturnScreen> createState() => _ReturnScreenState();
}

class _ReturnScreenState extends ConsumerState<ReturnScreen> {
  late final List<SaleLineInfo> _lines;
  late final List<double> _pick;
  LedgerEntry? _ledger;
  Future<double?>? _debtLeft;
  bool _busy = false;
  bool _ready = false;

  /// 2026-10-26 (живой аудит, П5): причина возврата (необязательно). Сервер её в возврате не принимает (как и у кассы ПК:
  /// тело — только ключ и касса), поэтому она пишется в журнал телефона и в документ возврата.
  final _reason = TextEditingController();

  // 2026-10-26 (голос-управление, В3б): «ручка» для Нур Советника — выбирает строки и нажимает ТУ ЖЕ `_submit` без диалога
  // «Вернуть?» (просьба в звонке = одобрение). Сумма, журнал возвратов и ключ операции — прежние. Итог — через `_aiSink`.
  final _aiSink = OutcomeSink();
  late final AiScreenHandles _aiHandles = ref.read(aiHandlesProvider);
  late final AiHandle _aiHandle = AiHandle(fill: _aiFill, run: _aiRun);
  final _aiReady = Completer<void>();
  bool _aiWhole = true;

  /// Сумма, которую сервер уже вернул (задаётся сразу после `returnSale`): если экран упадёт позже, деньги всё равно возвращены.
  double? _aiPosted;

  @override
  void dispose() {
    _aiHandles.unregister('return', _aiHandle);
    _reason.dispose();
    super.dispose();
  }

  Future<HandleOutcome?> _aiFill(Map<String, dynamic> a) async {
    try {
      await _aiReady.future.timeout(const Duration(seconds: 8));
    } on TimeoutException {
      return HandleOutcome.errorKey('h.screenNotReady');
    }
    if (!mounted) return HandleOutcome.errorKey('h.screenNotReady');
    final lines = a['lines'];
    final whole = a['whole'] == true || lines is! List || lines.isEmpty;
    final picks = List<double>.filled(_lines.length, 0);
    if (!whole) {
      for (final raw in lines) {
        if (raw is! Map) continue;
        final query = '${raw['query'] ?? ''}'.trim();
        final hits = [
          for (var i = 0; i < _lines.length; i++)
            if (productNameMatchesQuery(_lines[i].name, query)) i,
        ];
        if (hits.isEmpty) return HandleOutcome.notFoundKey('h.lineNone', {'name': query});
        var i = hits.first;
        if (hits.length > 1) {
          final exact = aiMatchByName(hits, query, (h) => _lines[h].name);
          if (exact.length != 1) {
            return HandleOutcome(ToolStatus.ambiguous, askKey: 'h.lineMany', askParams: {'list': hits.take(5).map((h) => _lines[h].name).join('; ')});
          }
          i = exact.first;
        }
        final want = raw['qty'] is num ? (raw['qty'] as num).toDouble() : parseNum('${raw['qty'] ?? ''}');
        final qty = want ?? _lines[i].refundable;
        if (qty <= 0) return HandleOutcome.clarifyKey('h.retNoPick');
        if (picks[i] + qty > _lines[i].refundable + 1e-6) {
          return HandleOutcome.clarifyKey('h.retQty', {'name': _lines[i].name, 'maxQty': _lines[i].refundable - picks[i]});
        }
        picks[i] += qty;
      }
      if (picks.every((p) => p <= 0)) return HandleOutcome.clarifyKey('h.retNoPick');
    }
    final reason = '${a['reason'] ?? ''}'.trim();
    setState(() {
      _aiWhole = whole;
      for (var i = 0; i < _pick.length; i++) {
        _pick[i] = picks[i];
      }
      if (reason.isNotEmpty) _reason.text = reason;
    });
    return null;
  }

  Future<HandleOutcome> _aiRun() => _aiSink.run(
    () => _submit(whole: _aiWhole, ask: false),
    afterEnd: () => _aiPosted == null ? null : HandleOutcome.ok({'amount': _aiPosted}),
  );

  String? get _method => str(widget.sale, 'payment_method');
  ReturnLedger get _journal => ReturnLedger(ref.read(databaseProvider));

  @override
  void initState() {
    super.initState();
    _lines = saleLines(widget.sale);
    _pick = List.filled(_lines.length, 0);
    _aiHandles.register('return', _aiHandle);
    WidgetsBinding.instance.addPostFrameCallback((_) => _prepare());
  }

  Future<void> _prepare() async {
    LedgerEntry? led;
    try {
      led = await _journal.rememberFromSale(widget.saleId, widget.sale);
    } catch (_) {
      led = null;
    }
    // Остаток долга — заранее, до возврата (как StartServerDebtLeftLookup кассы).
    final isDebt = (led != null && led.debt > 0.005) || (_method ?? '').toLowerCase() == 'debt';
    if (isDebt) _debtLeft = ref.read(opsRepoProvider).saleDebtLeft(widget.sale);
    if (!mounted) return;
    setState(() {
      _ledger = led;
      _ready = true;
    });
    if (!_aiReady.isCompleted) _aiReady.complete();
  }

  List<(int, double)> _picked({required bool whole}) => [
    for (var i = 0; i < _lines.length; i++)
      if (whole ? _lines[i].refundable > 0 : _pick[i] > 0) (i, whole ? _lines[i].refundable : _pick[i]),
  ];

  double _amount(List<(int, double)> picks) => refundTotal(_ledger, _lines, picks);

  String _splitText(BuildContext context, double amount, RefundSplit split) {
    final l = context.l;
    final b = StringBuffer(l.returnAmount(money(amount)));
    final parts = <String>[
      if (split.cash > 0.005) l.returnInCash(money(split.cash)),
      if (split.nonCash > 0.005) l.returnToCard(money(split.nonCash)),
    ];
    final give = !split.splitKnown ? '${money(split.money)} ${l.som}' : (parts.isEmpty ? '${money(0)} ${l.som}' : parts.join(' · '));
    b.write('\n${l.returnGive(give)}');
    if (split.debtReduced > 0.005) b.write('\n${split.debtUnknown ? l.returnDebtUnknown(money(split.debtReduced)) : l.returnDebtReduced(money(split.debtReduced))}');
    if (split.nonCash > 0.005 && split.splitKnown) b.write('\n${l.returnViaBank}');
    return b.toString();
  }

  Future<void> _submit({required bool whole, bool ask = true}) async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await _submitCore(whole: whole, ask: ask);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _submitCore({required bool whole, bool ask = true}) async {
    final l = context.l;
    final picks = _picked(whole: whole);
    if (picks.isEmpty) {
      if (whole) toast(context, l.returnNothingLeft, error: true);
      _aiSink.report(whole ? HandleOutcome.errorKey('h.retNothing') : HandleOutcome.clarifyKey('h.retNoPick'));
      return;
    }
    final amount = _amount(picks);
    final led = _ledger;
    if (led != null && led.total > 0 && led.remaining <= 0.004) {
      toast(context, l.returnNothingLeft, error: true);
      _aiSink.report(HandleOutcome.errorKey('h.retNothing'));
      return;
    }
    final debtLeft = await _debtLeft;
    if (!mounted) return;
    final preview = computeRefundSplit(led, _method, amount, serverDebtLeft: debtLeft);
    // 2026-10-26 (голос-управление, В3б): от ИИ диалога «Вернуть?» нет — просьба в звонке и есть одобрение
    final ok = !ask
        ? true
        : await showDialog<bool>(
            context: context,
            builder: (c) => AlertDialog(
              title: Text('${l.returnTitle} №${widget.number}'),
              content: Text(_splitText(context, amount, preview)),
              actions: [
                TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
                FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(whole ? l.returnAll : l.returnSelected)),
              ],
            ),
          );
    if (ok != true || !mounted) return;
    // Весь чек без прошлых возвратов — как раньше одним запросом; иначе строками (остатки строк).
    final untouched = _lines.every((x) => x.refundable >= x.qty - 1e-6);
    final send = whole && untouched ? const <(String, double)>[] : [for (final (i, q) in picks) (_lines[i].id, q)];
    try {
      await ref.read(opsRepoProvider).returnSale(widget.saleId, send);
      _aiPosted = amount;
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
      // ответ не дошёл — возврат мог пройти; повтор пойдёт тем же ключом (OpsRepository._withKey) и второй раз не проведётся
      _aiSink.report(OpsRepository.outcomeUnknown(e) ? const HandleOutcome(ToolStatus.error, unknown: true) : HandleOutcome.error(e.message));
      return;
    }
    // Что записал сервер: меньше расчёта — берём его (денег ушло меньше); выдано по движениям денег — его цифра.
    final seen = {for (final r in led?.refunds ?? const <LedgerRefund>[]) ?r.serverDocId};
    final figures = await ref.read(opsRepoProvider).serverReturnFigures(widget.saleId, seen);
    var finalAmount = amount;
    if (figures.doc != null && figures.doc! < amount - 0.009) finalAmount = roundMoney(figures.doc!);
    final split = computeRefundSplit(led, _method, finalAmount, cashOut: figures.cashOut, serverDebtLeft: debtLeft);
    if (led != null) {
      await _journal.addRefund(
        widget.saleId,
        LedgerRefund(
          at: DateTime.now(),
          amount: finalAmount,
          cash: split.cash,
          nonCash: split.nonCash,
          debtReduced: split.debtReduced,
          serverAmount: figures.doc,
          serverDocId: figures.docId,
        ),
      );
    }
    final reason = _reason.text.trim();
    Audit.log(
      'return',
      '${l.auditReturn} №${widget.number} · ${whole ? l.returnAll : l.returnSelected}${reason.isEmpty ? '' : ' · $reason'}',
      amount: -split.money,
    );
    ref.read(shiftProvider.notifier).refresh();
    _aiSink.report(HandleOutcome.ok({'amount': finalAmount}));
    if (!mounted) return;
    // 2026-10-26 (живой аудит, П5): каждый денежный шаг оставляет документ — документ возврата с печатью, PDF и отправкой.
    final doc = returnDoc(
      l,
      ref.read(sessionProvider),
      number: widget.number,
      items: [for (final (i, q) in picks) (_lines[i].name, q)],
      splitLines: _splitText(context, finalAmount, split).split('\n'),
      reason: reason,
    );
    await Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => TapeDocScreen(doc: doc, title: l.returnDone, subtitle: '${l.check} №${widget.number}')));
    if (mounted) context.pop(true);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final picks = _picked(whole: false);
    final sum = _ready ? _amount(picks) : 0.0;
    final led = _ledger;
    return Scaffold(
      appBar: ScreenTitle(l.returnTitle, subtitle: '${l.check} №${widget.number}'),
      body: !_ready
          ? const Center(child: CircularProgressIndicator())
          : ListView(
              padding: EdgeInsets.fromLTRB(16, 16, 16, 24 + MediaQuery.paddingOf(context).bottom),
              children: [
                if (led != null && led.total > 0)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 10),
                    child: Text(l.returnPaidInfo(money(led.total), money(led.refunded)), style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
                  ),
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
                                if (_lines[i].refundable < _lines[i].qty - 1e-6)
                                  Text(
                                    l.returnedQty(qtyText(_lines[i].qty - _lines[i].refundable)),
                                    style: const TextStyle(fontSize: 12, color: T.warn, fontWeight: FontWeight.w700),
                                  ),
                              ],
                            ),
                          ),
                          IconButton.outlined(
                            onPressed: _pick[i] <= 0 ? null : () => setState(() => _pick[i] = (_pick[i] - 1).clamp(0, _lines[i].refundable).toDouble()),
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
                            onPressed: _pick[i] >= _lines[i].refundable - 1e-6
                                ? null
                                : () => setState(() {
                                    // весовая строка — остаток целиком, штучная — по одной
                                    final left = _lines[i].refundable - _pick[i];
                                    _pick[i] = (_pick[i] + (left < 1 ? left : 1)).clamp(0, _lines[i].refundable).toDouble();
                                  }),
                            icon: Icon(Icons.add, color: context.pal.onAction),
                          ),
                        ],
                      ),
                    ),
                  ),
                const SizedBox(height: 8),
                FieldLabel(l.returnReasonTitle),
                Wrap(
                  spacing: 6,
                  children: [
                    for (final r in [l.returnReasonDefect, l.returnReasonNotFit, l.returnReasonCashier])
                      ChoiceChip(label: Text(r), selected: _reason.text == r, onSelected: (_) => setState(() => _reason.text = r)),
                  ],
                ),
                const SizedBox(height: 6),
                TextField(controller: _reason, onChanged: (_) => setState(() {})),
                const SizedBox(height: 12),
                Text('${l.returnToCustomer}: ${money(sum)} ${l.som}', style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
                const SizedBox(height: 12),
                FilledButton(onPressed: _busy || sum <= 0 ? null : () => _submit(whole: false), child: Text(l.returnSelected)),
                const SizedBox(height: 8),
                OutlinedButton(onPressed: _busy || _lines.every((x) => x.refundable <= 0) ? null : () => _submit(whole: true), child: Text(l.returnAll)),
              ],
            ),
    );
  }
}

/// 2026-10-26 (живой аудит, П5): ошибка загрузки с «Повторить». [compact] — полоса над списком, когда показан последний
/// сохранённый список; иначе — по центру экрана вместо списка.
class _RetryBox extends StatelessWidget {
  final String message;
  final VoidCallback onRetry;
  final bool compact;
  const _RetryBox({required this.message, required this.onRetry, this.compact = false});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final retry = OutlinedButton.icon(
      style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)),
      onPressed: onRetry,
      icon: const Icon(Icons.refresh_rounded),
      label: Text(l.retry),
    );
    if (compact) {
      return Container(
        padding: const EdgeInsets.fromLTRB(12, 8, 8, 8),
        decoration: BoxDecoration(color: pal.warnSoft, borderRadius: BorderRadius.circular(12)),
        child: Row(
          children: [
            Icon(Icons.wifi_off_rounded, color: pal.warn, size: 18),
            const SizedBox(width: 8),
            Expanded(child: Text(message, style: TextStyle(color: pal.warn, fontSize: 12.5))),
            TextButton(onPressed: onRetry, child: Text(l.retry)),
          ],
        ),
      );
    }
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(message, textAlign: TextAlign.center, style: TextStyle(color: pal.danger)),
            const SizedBox(height: 12),
            retry,
          ],
        ),
      ),
    );
  }
}

/// Крупная строка «Принято N, остаток M» над квитанцией.
class _AcceptedBanner extends StatelessWidget {
  final String text;
  const _AcceptedBanner(this.text);

  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(color: pal.successSoft, borderRadius: BorderRadius.circular(14)),
      child: Row(
        children: [
          Icon(Icons.check_circle_rounded, color: pal.success),
          const SizedBox(width: 10),
          Expanded(child: Text(text, style: TextStyle(color: pal.success, fontWeight: FontWeight.w800, fontSize: 16))),
        ],
      ),
    );
  }
}
