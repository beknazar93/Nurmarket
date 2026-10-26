// 2026-10-26 (голос-управление, В7): экран «Зарплата» (/admin/salary). Макет: design/NurMarket-design/screens/Mobile-Call-Staff.html
// (состояния salary и payout). Начислено — серверный расчёт tab=salary (как ПК и сайт); «Выплачено» — расходы «Зарплата» с меткой
// [#user период] (salary_payout.dart, правила ПК); «Выплатить» — расход через SalaryPayoutService: ключ в source_id, «ответ не дошёл» —
// замок до проверки, повтор за 10 секунд не проводится, при «проверено частично» сумма сама не подставляется. Отмена выплаты —
// только запросом владельцу (cancel-request), кнопки «Отменить» нет.
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/assistant/staff_tools.dart' show PaySchemeData, SalaryRow;
import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../core/util/audit.dart';
import '../ops/ops_repository.dart' show OpsRepository;
import 'salary_payout.dart';
import 'salary_repository.dart';
import 'staff_providers.dart';

String _two(int v) => v.toString().padLeft(2, '0');
String _dm(DateTime d) => '${_two(d.day)}.${_two(d.month)}';
DateTime _day(DateTime d) => DateTime(d.year, d.month, d.day);

String schemeName(BuildContext c, String code) {
  final l = c.l;
  return switch (code) {
    'salary' => l.stfSchemeSalary,
    'percent' => l.stfSchemePercent,
    'salary_plus_percent' => l.stfSchemeSalaryPercent,
    'per_item' => l.stfSchemePerItem,
    'salary_plus_per_item' => l.stfSchemeSalaryItem,
    'percent_plus_per_item' => l.stfSchemePercentItem,
    _ => '—',
  };
}

/// Почему сумме к выплате нельзя доверять — простыми словами.
String trustText(BuildContext c, TrustReason r, {PaidResult? paid}) {
  final l = c.l;
  return switch (r) {
    TrustReason.timeout => l.stfTrustTimeout,
    TrustReason.journal => l.stfTrustJournal,
    TrustReason.notChecked => l.stfTrustNotChecked,
    TrustReason.unassigned => l.stfTrustUnassigned(money(paid?.unassignedSum ?? 0), paid?.unassignedCount ?? 0),
    TrustReason.partial => l.stfTrustPartial,
    TrustReason.partialNotes => l.stfTrustPartialNotes,
    TrustReason.byName => l.stfTrustByName,
  };
}

/// Код отказа выплаты («#lock» …) → текст.
String payErrorText(BuildContext c, String? e, {SalaryPayoutRecord? lock}) {
  final l = c.l;
  if (e == null || e.isEmpty) return l.stfPayFailed;
  if (!e.startsWith('#')) return '${l.stfPayFailed} $e';
  return switch (e) {
    '#lock' => lock == null ? l.stfPayLockShort : l.stfPayLock(money(lock.amount), '${_dm(lock.createdAt)} ${_two(lock.createdAt.hour)}:${_two(lock.createdAt.minute)}', lock.cashboxName),
    '#dup' => l.stfPayDup,
    '#journal' => l.stfPayJournal,
    '#nocash' => l.stfPayNoCash,
    _ => l.stfPayFailed,
  };
}

enum _Preset { month, lastMonth, week, custom }

class _Vm {
  final SalaryRow row;
  final UserPaid? info;
  final Trust trust;
  final SalaryPayoutRecord? lock;
  _Vm(this.row, this.info, this.trust, this.lock);
  double get paid => info?.paid ?? 0;
  double get left => row.accrued - paid > 0 ? row.accrued - paid : 0;
}

class SalaryScreen extends ConsumerStatefulWidget {
  /// today | week | month — из адреса (голос: «открой зарплату за неделю»).
  final String? period;
  const SalaryScreen({super.key, this.period});
  @override
  ConsumerState<SalaryScreen> createState() => _SalaryScreenState();
}

class _SalaryScreenState extends ConsumerState<SalaryScreen> {
  late DateTime _from;
  late DateTime _to;
  _Preset _preset = _Preset.month;
  SalaryReport? _report;
  PaidResult? _paid;
  bool _timedOut = false;
  bool _paidLoading = false;
  bool _denied = false;
  bool _failed = false;
  Map<String, SalaryPayoutRecord?> _locks = {};
  int _gen = 0;

  @override
  void initState() {
    super.initState();
    final today = _day(DateTime.now());
    switch (widget.period) {
      case 'today':
        _from = today;
        _to = today;
        _preset = _Preset.custom;
      case 'week':
        _from = today.subtract(const Duration(days: 6));
        _to = today;
        _preset = _Preset.week;
      default:
        _from = DateTime(today.year, today.month, 1);
        _to = today;
    }
    _load();
  }

  void _setPreset(_Preset p) {
    final today = _day(DateTime.now());
    setState(() {
      _preset = p;
      switch (p) {
        case _Preset.month:
          _from = DateTime(today.year, today.month, 1);
          _to = today;
        case _Preset.lastMonth:
          final first = DateTime(today.year, today.month, 1);
          _from = DateTime(first.year, first.month - 1, 1);
          _to = first.subtract(const Duration(days: 1));
        case _Preset.week:
          _from = today.subtract(const Duration(days: 6));
          _to = today;
        case _Preset.custom:
          break;
      }
    });
    _load();
  }

  Future<void> _pickRange() async {
    final today = _day(DateTime.now());
    final r = await showDateRangePicker(
      context: context,
      firstDate: DateTime(today.year - 3),
      lastDate: today,
      initialDateRange: DateTimeRange(start: _from, end: _to),
    );
    if (r == null) return;
    setState(() {
      _preset = _Preset.custom;
      _from = _day(r.start);
      _to = _day(r.end);
    });
    _load();
  }

  Future<void> _load() async {
    final gen = ++_gen;
    setState(() {
      _report = null;
      _paid = null;
      _timedOut = false;
      _denied = false;
      _failed = false;
      _paidLoading = false;
      _locks = {};
    });
    final svc = ref.read(salaryPayoutProvider);
    svc.forgetCache();
    SalaryReport rep;
    try {
      rep = await ref.read(salaryRepoProvider).report(_from, _to);
    } on ApiException catch (e) {
      if (!mounted || gen != _gen) return;
      setState(() {
        _denied = e.status == 401 || e.status == 403;
        _failed = !_denied;
      });
      return;
    }
    if (!mounted || gen != _gen) return;
    setState(() {
      _report = rep;
      _paidLoading = rep.rows.isNotEmpty;
    });
    if (rep.rows.isEmpty) return;
    PaidResult? paid;
    var timedOut = false;
    try {
      paid = await svc.paidForPeriod([for (final r in rep.rows) (userId: r.employeeId, name: r.name)], _from, _to).timeout(const Duration(seconds: 60));
    } catch (_) {
      timedOut = true;
    }
    final locks = <String, SalaryPayoutRecord?>{};
    for (final r in rep.rows) {
      locks[r.employeeId] = (await svc.lockFor(r.employeeId)).lock;
    }
    if (!mounted || gen != _gen) return;
    setState(() {
      _paid = paid;
      _timedOut = timedOut;
      _paidLoading = false;
      _locks = locks;
    });
  }

  List<_Vm> get _vms => [
    for (final r in _report?.rows ?? const <SalaryRow>[]) _Vm(r, _paid?.users[r.employeeId], _paidLoading ? const Trust(false, TrustReason.notChecked) : trustPaid(_paid, _paid?.users[r.employeeId], timedOut: _timedOut), _locks[r.employeeId]),
  ];

  Future<void> _openRow(_Vm vm) async {
    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _RowSheet(vm: vm, paid: _paid, from: _from, to: _to, loading: _paidLoading, onChanged: _load),
    );
  }

  Future<void> _pay(_Vm vm) async {
    final done = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _PaySheet(vm: vm, paid: _paid, from: _from, to: _to),
    );
    if (done == true) {
      _load();
    } else {
      // 2026-10-26 (приёмка 1.18.6, С-5н): выплата «без ответа» ставит замок, а лист закрывают без «готово» — список не
      // перечитывался, и замок в нём не было видно. Замки лежат на телефоне, поэтому читаем только их.
      await _refreshLocks();
    }
  }

  Future<void> _refreshLocks() async {
    final svc = ref.read(salaryPayoutProvider);
    final locks = <String, SalaryPayoutRecord?>{};
    for (final r in _report?.rows ?? const <SalaryRow>[]) {
      locks[r.employeeId] = (await svc.lockFor(r.employeeId)).lock;
    }
    if (mounted) setState(() => _locks = locks);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = context.pal;
    final s = ref.watch(sessionProvider);
    final canPay = s?.editSalary ?? false;
    final vms = _vms;
    final accrued = vms.fold<double>(0, (a, v) => a + v.row.accrued);
    final paid = vms.fold<double>(0, (a, v) => a + v.paid);
    final left = vms.fold<double>(0, (a, v) => a + v.left);
    final firstUnsure = vms.where((v) => !v.trust.sure).firstOrNull;
    final chipLabel = '${_dm(_from)}–${_dm(_to)}';
    return Scaffold(
      appBar: ScreenTitle(l.stfSalaryTitle, subtitle: _report == null ? null : [s?.companyName ?? '', l.stfSalarySub(vms.length)].where((x) => x.isNotEmpty).join(' · ')),
      body: Column(
        children: [
          SizedBox(
            height: 48,
            child: ListView(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: 16),
              children: [
                for (final (pr, label) in [(_Preset.month, l.stfPeriodMonth), (_Preset.lastMonth, l.stfPeriodLast), (_Preset.week, l.stfPeriodWeek)])
                  Padding(padding: const EdgeInsets.only(right: 6), child: ChoiceChip(label: Text(label), selected: _preset == pr, onSelected: (_) => _setPreset(pr))),
                Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: ChoiceChip(avatar: const Icon(Icons.calendar_today_rounded, size: 16), label: Text(chipLabel), selected: _preset == _Preset.custom, onSelected: (_) => _pickRange()),
                ),
              ],
            ),
          ),
          Expanded(
            child: _denied
                ? Center(child: Padding(padding: const EdgeInsets.all(24), child: Text(l.stfSalaryDenied, textAlign: TextAlign.center, style: TextStyle(color: p.textSoft))))
                : _failed
                ? Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [Text(l.stfSalaryFail, style: TextStyle(color: p.textSoft)), const SizedBox(height: 8), FilledButton(onPressed: _load, child: Text(l.retry))],
                    ),
                  )
                : _report == null
                ? const Center(child: CircularProgressIndicator())
                : RefreshIndicator(
                    onRefresh: _load,
                    child: ListView(
                      padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                      children: [
                        Row(
                          children: [
                            Expanded(child: _Kpi(l.stfAccrued, money(accrued))),
                            const SizedBox(width: 8),
                            Expanded(child: _Kpi(l.stfPaid, _paidLoading ? '…' : money(paid))),
                            const SizedBox(width: 8),
                            Expanded(child: _Kpi(l.stfLeft, _paidLoading ? '…' : money(left), strong: true)),
                          ],
                        ),
                        const SizedBox(height: 10),
                        if (_paidLoading)
                          Padding(padding: const EdgeInsets.only(bottom: 10), child: Text(l.stfChecking, style: TextStyle(color: p.textSoft, fontSize: 12.5)))
                        else if (firstUnsure != null)
                          Container(
                            margin: const EdgeInsets.only(bottom: 10),
                            constraints: const BoxConstraints(minHeight: 44),
                            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                            decoration: BoxDecoration(color: p.warnSoft, borderRadius: BorderRadius.circular(12)),
                            child: Row(
                              children: [
                                Icon(Icons.warning_amber_rounded, size: 20, color: p.warn),
                                const SizedBox(width: 10),
                                Expanded(child: Text('${l.stfPartialPlate} ${trustText(context, firstUnsure.trust.reason!, paid: _paid)}', style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13))),
                              ],
                            ),
                          ),
                        if (vms.isEmpty)
                          Padding(padding: const EdgeInsets.all(32), child: Center(child: Text(l.stfSalaryEmpty, textAlign: TextAlign.center, style: TextStyle(color: p.textSoft))))
                        else
                          Panel(
                            padding: EdgeInsets.zero,
                            child: Column(
                              children: [
                                Padding(
                                  padding: const EdgeInsets.fromLTRB(12, 10, 8, 6),
                                  child: Row(
                                    children: [
                                      // 2026-10-26 (приёмка 1.18.6, В-3н): заголовки в одну строку, при нехватке места сжимаются —
                                      // слово не рвётся посреди («Кызматк/ер»)
                                      Expanded(flex: 5, child: _OneLine(l.stfColEmployee, style: _head(p), align: Alignment.centerLeft)),
                                      Expanded(flex: 4, child: _OneLine(l.stfAccrued, style: _head(p), align: Alignment.centerRight)),
                                      Expanded(flex: 4, child: _OneLine(l.stfLeft, style: _head(p), align: Alignment.centerRight)),
                                      SizedBox(width: canPay ? 96 : 8),
                                    ],
                                  ),
                                ),
                                const Divider(height: 1),
                                for (final vm in vms) ...[
                                  InkWell(
                                    onTap: () => _openRow(vm),
                                    child: Padding(
                                      padding: const EdgeInsets.fromLTRB(12, 6, 8, 6),
                                      child: Row(
                                        children: [
                                          Expanded(
                                            flex: 5,
                                            child: Column(
                                              crossAxisAlignment: CrossAxisAlignment.start,
                                              children: [
                                                Text(vm.row.name, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w700)),
                                                // 2026-10-26 (приёмка 1.18.6, С-5н): замок «выплата без ответа» — значок и подпись в самой строке
                                                if (vm.lock != null)
                                                  Padding(
                                                    padding: const EdgeInsets.only(top: 2),
                                                    child: Row(
                                                      crossAxisAlignment: CrossAxisAlignment.start,
                                                      children: [
                                                        Icon(Icons.lock_outline_rounded, size: 15, color: p.danger),
                                                        const SizedBox(width: 3),
                                                        Expanded(child: Text(l.stfLockedTag, style: TextStyle(fontSize: 11.5, color: p.danger, fontWeight: FontWeight.w700))),
                                                      ],
                                                    ),
                                                  ),
                                              ],
                                            ),
                                          ),
                                          // суммы — всегда одной строкой (при большом шрифте уменьшаются, но число не рвётся «30 00 / 0»)
                                          Expanded(flex: 4, child: _OneLine(money(vm.row.accrued), style: const TextStyle(fontFeatures: [FontFeature.tabularFigures()]), align: Alignment.centerRight)),
                                          Expanded(
                                            flex: 4,
                                            child: _paidLoading
                                                ? _OneLine('…', style: TextStyle(color: p.textSoft), align: Alignment.centerRight)
                                                : _OneLine(vm.left > 0.004 ? money(vm.left) : '0', style: TextStyle(fontWeight: vm.left > 0.004 ? FontWeight.w800 : FontWeight.w500, color: vm.left > 0.004 ? null : p.success), align: Alignment.centerRight),
                                          ),
                                          if (canPay)
                                            SizedBox(
                                              width: 96,
                                              child: vm.lock != null
                                                  // замок: вместо «Выплатить» — «Проверить» (открывает карточку с проверкой на сервере)
                                                  ? Align(
                                                      alignment: Alignment.centerRight,
                                                      child: OutlinedButton(
                                                        style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48), padding: const EdgeInsets.symmetric(horizontal: 8)),
                                                        onPressed: () => _openRow(vm),
                                                        child: _OneLine(l.stfLockCheckShort, align: Alignment.center),
                                                      ),
                                                    )
                                                  : (vm.left > 0.004 || !vm.trust.sure) && !_paidLoading && vm.row.accrued > 0
                                                  ? Align(
                                                      alignment: Alignment.centerRight,
                                                      child: FilledButton.tonal(
                                                        style: FilledButton.styleFrom(minimumSize: const Size(0, 48), padding: const EdgeInsets.symmetric(horizontal: 8)),
                                                        onPressed: () => _pay(vm),
                                                        child: _OneLine(l.stfPayBtn, align: Alignment.center),
                                                      ),
                                                    )
                                                  : null,
                                            )
                                          else
                                            const SizedBox(width: 8),
                                        ],
                                      ),
                                    ),
                                  ),
                                  const Divider(height: 1),
                                ],
                              ],
                            ),
                          ),
                        const SizedBox(height: 10),
                        Text(l.stfSalaryHint, textAlign: TextAlign.center, style: TextStyle(fontSize: 11.5, color: p.textSoft)),
                      ],
                    ),
                  ),
          ),
        ],
      ),
    );
  }

  TextStyle _head(Palette p) => TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: p.textSoft);
}

/// 2026-10-26 (приёмка 1.18.6, В-3н): текст ровно в одну строку — не помещается, так сжимается целиком (FittedBox), а не рвётся
/// посреди числа или слова. Для сумм и заголовков узкой таблицы.
class _OneLine extends StatelessWidget {
  final String text;
  final TextStyle? style;
  final Alignment align;
  const _OneLine(this.text, {this.style, this.align = Alignment.centerLeft});
  @override
  Widget build(BuildContext context) => FittedBox(
    fit: BoxFit.scaleDown,
    alignment: align,
    child: Text(text, maxLines: 1, softWrap: false, style: style),
  );
}

class _Kpi extends StatelessWidget {
  final String label;
  final String value;
  final bool strong;
  const _Kpi(this.label, this.value, {this.strong = false});
  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    return Panel(
      padding: const EdgeInsets.fromLTRB(10, 10, 10, 10),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 11.5, color: p.textSoft)),
          const SizedBox(height: 2),
          FittedBox(
            fit: BoxFit.scaleDown,
            alignment: Alignment.centerLeft,
            child: Text(value, style: TextStyle(fontSize: 17, fontWeight: FontWeight.w800, color: strong ? p.price : null)),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Строка сотрудника: подробности, замок, схема, недавние выплаты
// ---------------------------------------------------------------------------------------------------------------------

class _RowSheet extends ConsumerStatefulWidget {
  final _Vm vm;
  final PaidResult? paid;
  final DateTime from;
  final DateTime to;
  final bool loading;
  final VoidCallback onChanged;
  const _RowSheet({required this.vm, required this.paid, required this.from, required this.to, required this.loading, required this.onChanged});
  @override
  ConsumerState<_RowSheet> createState() => _RowSheetState();
}

class _RowSheetState extends ConsumerState<_RowSheet> {
  SalaryPayoutRecord? _lock;
  List<SalaryPayoutRecord> _recent = [];
  bool _busy = false;
  String? _msg;

  @override
  void initState() {
    super.initState();
    _lock = widget.vm.lock;
    _refresh();
  }

  Future<void> _refresh() async {
    final svc = ref.read(salaryPayoutProvider);
    final all = await svc.journal();
    final lock = (await svc.lockFor(widget.vm.row.employeeId)).lock;
    if (!mounted) return;
    setState(() {
      _lock = lock;
      _recent = all.where((r) => r.userId == widget.vm.row.employeeId && r.status == 'ok').take(5).toList();
    });
  }

  Future<void> _guard(Future<void> Function() f) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _msg = null;
    });
    try {
      await f();
    } on ApiException catch (e) {
      if (mounted) setState(() => _msg = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _check() => _guard(() async {
    final found = await ref.read(salaryPayoutProvider).checkUnknown(_lock!.key);
    widget.onChanged();
    await _refresh();
    if (mounted) setState(() => _msg = found ? context.l.stfLockFound : context.l.stfLockNotFound);
  });

  Future<void> _resolve(bool onSite) => _guard(() async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(onSite ? c.l.stfLockYesAsk : c.l.stfLockNoAsk),
        content: Text(c.l.stfLockResolveHint),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(c.l.cancel)),
          FilledButton(style: FilledButton.styleFrom(minimumSize: const Size(110, 48)), onPressed: () => Navigator.pop(c, true), child: Text(c.l.stfConfirm)),
        ],
      ),
    );
    if (ok != true) return;
    await ref.read(salaryPayoutProvider).resolveUnknown(_lock!.key, paidOnSite: onSite);
    widget.onChanged();
    await _refresh();
  });

  Future<void> _cancelRequest(SalaryPayoutRecord r) => _guard(() async {
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(c.l.stfCancelAsk(money(r.amount))),
        content: Text(c.l.stfCancelHint),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(c.l.cancel)),
          FilledButton(style: FilledButton.styleFrom(minimumSize: const Size(110, 48)), onPressed: () => Navigator.pop(c, true), child: Text(c.l.stfCancelSend)),
        ],
      ),
    );
    if (ok != true || r.flowId == null) return;
    await ref.read(salaryPayoutProvider).requestCancel(r.flowId!);
    if (mounted) setState(() => _msg = context.l.stfCancelSent);
  });

  Future<void> _scheme() async {
    final saved = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _SchemeSheet(row: widget.vm.row),
    );
    if (saved == true) {
      widget.onChanged();
      if (mounted) Navigator.pop(context);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = context.pal;
    final vm = widget.vm;
    final r = vm.row;
    final canEdit = ref.watch(sessionProvider)?.editSalary ?? false;
    final lines = <String>[
      if (r.noScheme) l.stfNoScheme else schemeName(context, r.scheme),
      if (r.monthly > 0) '${l.stfMonthly}: ${money(r.monthly)}',
      if (r.percent > 0) '${l.stfPercent}: ${money(r.percent)} %',
      if (r.perItem > 0) '${l.stfPerItem}: ${money(r.perItem)}',
      if (r.bonus > 0) '${l.stfBonus}: ${money(r.bonus)}',
    ];
    return SafeArea(
        child: ConstrainedBox(
          constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * .85),
          child: ListView(
            shrinkWrap: true,
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 16),
            children: [
              Text(r.name, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
              Text('${_dm(widget.from)}–${_dm(widget.to)}', style: TextStyle(color: p.textSoft, fontSize: 12.5)),
              const SizedBox(height: 10),
              for (final t in lines) Text(t, style: TextStyle(color: p.textSoft)),
              const SizedBox(height: 10),
              Row(
                children: [
                  Expanded(child: _Kpi(l.stfAccrued, money(r.accrued))),
                  const SizedBox(width: 8),
                  Expanded(child: _Kpi(l.stfPaid, widget.loading ? '…' : money(vm.paid))),
                  const SizedBox(width: 8),
                  Expanded(child: _Kpi(l.stfLeft, widget.loading ? '…' : money(vm.left), strong: true)),
                ],
              ),
              if (!vm.trust.sure && !widget.loading) ...[
                const SizedBox(height: 8),
                Text('${l.stfPartialPlate} ${trustText(context, vm.trust.reason!, paid: widget.paid)}', style: TextStyle(color: p.warn, fontSize: 12.5, fontWeight: FontWeight.w600)),
              ],
              if (vm.info != null)
                for (final x in vm.info!.partials)
                  Padding(padding: const EdgeInsets.only(top: 6), child: Text(l.stfPartialNote(money(x.amount), '${_dm(x.from)}–${_dm(x.to)}'), style: TextStyle(color: p.textSoft, fontSize: 12.5))),
              if (_lock != null) ...[
                const SizedBox(height: 12),
                Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(color: p.dangerSoft, borderRadius: BorderRadius.circular(12)),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(payErrorText(context, '#lock', lock: _lock), style: const TextStyle(fontWeight: FontWeight.w600)),
                      const SizedBox(height: 8),
                      Wrap(
                        spacing: 8,
                        runSpacing: 8,
                        children: [
                          FilledButton(style: FilledButton.styleFrom(minimumSize: const Size(0, 48)), onPressed: _busy ? null : _check, child: Text(l.stfLockCheck)),
                          if (canEdit) OutlinedButton(style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)), onPressed: _busy ? null : () => _resolve(true), child: Text(l.stfLockYes)),
                          if (canEdit) OutlinedButton(style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)), onPressed: _busy ? null : () => _resolve(false), child: Text(l.stfLockNo)),
                        ],
                      ),
                    ],
                  ),
                ),
              ],
              if (_msg != null) Padding(padding: const EdgeInsets.only(top: 10), child: Text(_msg!, style: const TextStyle(fontWeight: FontWeight.w600))),
              if (_recent.isNotEmpty) ...[
                const SizedBox(height: 14),
                Text(l.stfRecentPayouts, style: const TextStyle(fontWeight: FontWeight.w800)),
                for (final x in _recent)
                  ListTile(
                    contentPadding: EdgeInsets.zero,
                    dense: true,
                    title: Text('${money(x.amount)} ${l.som} · ${x.kind == 'advance' ? l.stfKindAdvance : l.stfKindSalary}'),
                    subtitle: Text('${_dm(x.createdAt)} ${_two(x.createdAt.hour)}:${_two(x.createdAt.minute)} · ${x.cashboxName}'),
                    trailing: canEdit && x.flowId != null ? TextButton(onPressed: _busy ? null : () => _cancelRequest(x), child: Text(l.stfCancelBtn)) : null,
                  ),
              ],
              const SizedBox(height: 14),
              if (canEdit)
                OutlinedButton.icon(
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                  onPressed: _busy ? null : _scheme,
                  icon: const Icon(Icons.tune_rounded, size: 20),
                  label: Text(l.stfSchemeBtn),
                ),
              const SizedBox(height: 8),
              TextButton(onPressed: () => Navigator.pop(context), child: Text(l.done)),
            ],
          ),
        ),
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Выплатить
// ---------------------------------------------------------------------------------------------------------------------

class _PaySheet extends ConsumerStatefulWidget {
  final _Vm vm;
  final PaidResult? paid;
  final DateTime from;
  final DateTime to;
  const _PaySheet({required this.vm, required this.paid, required this.from, required this.to});
  @override
  ConsumerState<_PaySheet> createState() => _PaySheetState();
}

class _PaySheetState extends ConsumerState<_PaySheet> {
  final _amount = TextEditingController();
  final _comment = TextEditingController();
  List<PayoutCashbox>? _boxes;
  PayoutCashbox? _box;
  String _kind = 'salary';
  bool _busy = false;
  String? _msg;
  bool _warn = true;

  @override
  void initState() {
    super.initState();
    // Сумма подставляется ТОЛЬКО когда остаток известен уверенно; «проверено частично» — поле пустое (правило ПК).
    if (widget.vm.trust.sure && widget.vm.left > 0.004) _amount.text = widget.vm.left.toStringAsFixed(2).replaceAll(RegExp(r'\.?0+$'), '');
    _loadBoxes();
  }

  Future<void> _loadBoxes() async {
    final svc = ref.read(salaryPayoutProvider);
    try {
      final list = await svc.cashboxes();
      final i = SalaryPayoutService.defaultCashboxIndex(list, await svc.savedCashboxId());
      if (!mounted) return;
      setState(() {
        _boxes = list;
        // 2026-10-26 (приёмка 1.18.6, С-4н): расходной кассы нет, но касса у компании одна — берём её (предупреждение «касса
        // продаж» покажется ниже и в подтверждении). Несколько касс и выбрать не из чего — человек выбирает сам.
        _box = i >= 0 ? list[i] : (list.length == 1 ? list.first : null);
      });
    } on ApiException catch (e) {
      if (mounted) {
        setState(() {
          _boxes = const [];
          _msg = e.message;
        });
      }
    }
  }

  Future<void> _go() async {
    final l = context.l;
    final amount = parseNum(_amount.text);
    final box = _box;
    if (_busy || amount == null || amount <= 0 || box == null) {
      // 2026-10-26 (приёмка 1.18.6, С-4н): касса не выбрана — просим выбрать, а не пугаем «не нашла кассу… выплатите на сайте»
      // (так говорим только когда у компании касс нет совсем)
      setState(() => _msg = box == null ? ((_boxes?.isNotEmpty ?? false) ? l.chooseCashbox : l.stfPayNoCash) : l.stfPayNeedAmount);
      return;
    }
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(c.l.stfPayConfirm(money(amount), widget.vm.row.name, box.name)),
        content: box.isSales ? Text(c.l.stfSalesBoxWarn) : null,
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(c.l.cancel)),
          FilledButton(style: FilledButton.styleFrom(minimumSize: const Size(120, 48)), onPressed: () => Navigator.pop(c, true), child: Text(c.l.stfPayBtn)),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    setState(() {
      _busy = true;
      _msg = null;
    });
    final svc = ref.read(salaryPayoutProvider);
    try {
      final res = await svc.pay(
        PayoutRequest(
          key: OpsRepository.newKey(),
          userId: widget.vm.row.employeeId,
          employee: widget.vm.row.name,
          from: widget.from,
          to: widget.to,
          amount: amount,
          cashbox: box,
          comment: _comment.text,
          kind: _kind,
        ),
      );
      if (!mounted) return;
      switch (res.verdict) {
        case PayoutVerdict.done:
          await svc.rememberCashbox(box);
          // 2026-10-26 (В7): журнал аудита телефона — кто и сколько выплатил (деньги из кассы уходят расходом)
          Audit.log('cash', '${_kind == 'advance' ? l.stfKindAdvance : l.stfKindSalary} · ${widget.vm.row.name}', amount: -amount);
          if (!mounted) return;
          toast(context, l.stfPayDone(money(amount), widget.vm.row.name));
          Navigator.pop(context, true);
          return;
        case PayoutVerdict.unknown:
          // ответ не дошёл — выплата могла пройти: повтор закрыт замком, решает проверка в карточке сотрудника
          setState(() => _msg = l.stfPayUnknown(money(amount), box.name));
          return;
        case PayoutVerdict.rejected:
          setState(() => _msg = payErrorText(context, res.error, lock: res.lockRecord));
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = context.pal;
    final vm = widget.vm;
    return Padding(
      padding: EdgeInsets.fromLTRB(20, 8, 20, MediaQuery.viewInsetsOf(context).bottom + 16),
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l.stfPayTitle(vm.row.name), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
            Text('${_dm(widget.from)}–${_dm(widget.to)} · ${l.stfLeft}: ${money(vm.left)} ${l.som}', style: TextStyle(color: p.textSoft, fontSize: 12.5)),
            if (!vm.trust.sure) ...[
              const SizedBox(height: 8),
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(color: p.warnSoft, borderRadius: BorderRadius.circular(12)),
                child: Text('${l.stfPartialPlate} ${trustText(context, vm.trust.reason!, paid: widget.paid)}', style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13)),
              ),
            ],
            const SizedBox(height: 12),
            SegmentedButton<String>(
              segments: [ButtonSegment(value: 'salary', label: Text(l.stfKindSalary)), ButtonSegment(value: 'advance', label: Text(l.stfKindAdvance))],
              selected: {_kind},
              onSelectionChanged: (v) => setState(() => _kind = v.first),
            ),
            const SizedBox(height: 10),
            FieldLabel(l.stfPayAmount, upper: false),
            TextField(
              controller: _amount,
              autofocus: !vm.trust.sure,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              inputFormatters: [FilteringTextInputFormatter.allow(RegExp(r'[0-9., ]'))],
              decoration: InputDecoration(hintText: vm.trust.sure ? null : l.stfPayEnterAmount, suffixText: l.som),
            ),
            const SizedBox(height: 8),
            FieldLabel(l.stfPayBox, upper: false),
            if (_boxes == null)
              const LinearProgressIndicator()
            else if (_boxes!.isEmpty)
              Text(l.stfPayNoCash, style: TextStyle(color: p.danger))
            else
              DropdownButtonFormField<PayoutCashbox>(
                initialValue: _box,
                isExpanded: true,
                hint: Text(l.chooseCashbox),
                items: [for (final b in _boxes!) DropdownMenuItem(value: b, child: Text(b.balance == null ? b.name : '${b.name} · ${money(b.balance!)}', overflow: TextOverflow.ellipsis))],
                onChanged: (v) => setState(() {
                  _box = v;
                  _warn = true;
                  _msg = null;
                }),
              ),
            if (_box != null && _box!.isSales && _warn)
              Padding(padding: const EdgeInsets.only(top: 6), child: Text(l.stfSalesBoxWarn, style: TextStyle(color: p.warn, fontSize: 12.5, fontWeight: FontWeight.w600))),
            const SizedBox(height: 8),
            TextField(controller: _comment, decoration: InputDecoration(hintText: l.stfPayComment)),
            if (_msg != null) Padding(padding: const EdgeInsets.only(top: 10), child: Text(_msg!, style: TextStyle(color: p.danger, fontWeight: FontWeight.w600))),
            const SizedBox(height: 12),
            FilledButton(
              style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(T.ctaHeight)),
              onPressed: _busy ? null : _go,
              child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2)) : Text(l.stfPayBtn),
            ),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Схема оплаты
// ---------------------------------------------------------------------------------------------------------------------

class _SchemeSheet extends ConsumerStatefulWidget {
  final SalaryRow row;
  const _SchemeSheet({required this.row});
  @override
  ConsumerState<_SchemeSheet> createState() => _SchemeSheetState();
}

class _SchemeSheetState extends ConsumerState<_SchemeSheet> {
  String _scheme = 'salary';
  final _base = TextEditingController();
  final _percent = TextEditingController();
  final _item = TextEditingController();
  bool _loading = true;
  bool _busy = false;
  String? _msg;

  @override
  void initState() {
    super.initState();
    _load();
  }

  String _n(double v) => v == 0 ? '' : v.toStringAsFixed(2).replaceAll(RegExp(r'\.?0+$'), '');

  Future<void> _load() async {
    PaySchemeData? d;
    try {
      d = await ref.read(salaryRepoProvider).scheme(widget.row.employeeId);
    } on ApiException {
      d = null;
    }
    final r = widget.row;
    if (!mounted) return;
    setState(() {
      _scheme = kPaySchemes.contains(d?.scheme ?? r.scheme) ? (d?.scheme ?? r.scheme) : 'salary';
      _base.text = _n(d?.base ?? r.monthly);
      _percent.text = _n(d?.percent ?? r.percent);
      _item.text = _n(d?.perItem ?? r.perItem);
      _loading = false;
    });
  }

  bool get _hasBase => _scheme.contains('salary');
  bool get _hasPercent => _scheme.contains('percent');
  bool get _hasItem => _scheme.contains('item');

  Future<void> _save() async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _msg = null;
    });
    try {
      await ref.read(salaryRepoProvider).setScheme(
        widget.row.employeeId,
        PaySchemeData(_scheme, _hasBase ? parseNum(_base.text) ?? 0 : 0, _hasPercent ? parseNum(_percent.text) ?? 0 : 0, _hasItem ? parseNum(_item.text) ?? 0 : 0),
      );
      if (!mounted) return;
      toast(context, context.l.stfSchemeSaved);
      Navigator.pop(context, true);
    } on ApiException catch (e) {
      if (mounted) setState(() => _msg = e.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = context.pal;
    return Padding(
      padding: EdgeInsets.fromLTRB(20, 8, 20, MediaQuery.viewInsetsOf(context).bottom + 16),
      child: SingleChildScrollView(
        child: _loading
            ? const SizedBox(height: 120, child: Center(child: CircularProgressIndicator()))
            : Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(l.stfSchemeTitle(widget.row.name), style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                  const SizedBox(height: 10),
                  DropdownButtonFormField<String>(
                    initialValue: _scheme,
                    isExpanded: true,
                    items: [for (final k in kPaySchemes) DropdownMenuItem(value: k, child: Text(schemeName(context, k), overflow: TextOverflow.ellipsis))],
                    onChanged: (v) => setState(() => _scheme = v ?? 'salary'),
                  ),
                  if (_hasBase) ...[const SizedBox(height: 8), FieldLabel(l.stfMonthly, upper: false), _num(_base, l.som)],
                  if (_hasPercent) ...[const SizedBox(height: 8), FieldLabel(l.stfPercent, upper: false), _num(_percent, '%')],
                  if (_hasItem) ...[const SizedBox(height: 8), FieldLabel(l.stfPerItem, upper: false), _num(_item, l.som)],
                  if (_msg != null) Padding(padding: const EdgeInsets.only(top: 10), child: Text(_msg!, style: TextStyle(color: p.danger, fontWeight: FontWeight.w600))),
                  const SizedBox(height: 14),
                  FilledButton(style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(T.ctaHeight)), onPressed: _busy ? null : _save, child: Text(l.save)),
                ],
              ),
      ),
    );
  }

  Widget _num(TextEditingController c, String suffix) => TextField(
    controller: c,
    keyboardType: const TextInputType.numberWithOptions(decimal: true),
    inputFormatters: [FilteringTextInputFormatter.allow(RegExp(r'[0-9., ]'))],
    decoration: InputDecoration(suffixText: suffix),
  );
}
