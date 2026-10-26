import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/ai/assistant/ai_handles.dart';
import '../../core/ai/assistant/tool_registry.dart' show ToolStatus;
import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../ops/ops_repository.dart' show OpsRepository;
import '../ops/tape_doc.dart';
import 'shift_doc.dart';
import 'shift_repository.dart';

/// Закрыть смену (Т18): итоги смены, «должно быть в кассе», пересчёт наличных.
/// Неотправленные чеки сначала уходят на сервер — иначе их не будет в отчёте смены.
/// 2026-10-26 (живой аудит, П6): разбивка как у ПК — продажи нал/безнал/долг, возвраты, оплата долгов и предоплата, внесения и
/// изъятия, «Должно быть в кассе» со сверкой с сервером; X-отчёт и Z-итог — отдельные экраны-«ленты» с печатью и PDF.
class CloseShiftScreen extends ConsumerStatefulWidget {
  const CloseShiftScreen({super.key});
  @override
  ConsumerState<CloseShiftScreen> createState() => _CloseShiftScreenState();
}

class _CloseShiftScreenState extends ConsumerState<CloseShiftScreen> {
  final _cash = TextEditingController();
  bool _busy = false;

  /// 2026-10-26 (П6): разбивка смены — продажи, возвраты, оплата долгов, внесения/изъятия (см. [ShiftBreakdown]).
  ShiftBreakdown? _bd;
  bool _bdLoading = false;

  // 2026-10-26 (голос-управление, В3б): «ручка» для Нур Советника — вписывает насчитанные наличные и нажимает ТУ ЖЕ `_close`
  // (сначала уходят чеки из очереди, потом закрытие) без диалога «Закрыть смену?» — просьба в звонке и есть одобрение.
  final _aiSink = OutcomeSink();
  late final AiScreenHandles _aiHandles = ref.read(aiHandlesProvider);
  late final AiHandle _aiHandle = AiHandle(fill: _aiFill, run: () => _aiSink.run(() => _close(ask: false)));

  Future<HandleOutcome?> _aiFill(Map<String, dynamic> a) async {
    if (ref.read(shiftProvider).shift == null) return HandleOutcome.errorKey('noShift');
    final counted = a['counted_cash'] is num ? (a['counted_cash'] as num).toDouble() : parseNum('${a['counted_cash'] ?? ''}');
    // сумма обязательна: без пересчёта наличных смену не закрываем (вопрос голосом, а не молчаливый ноль)
    if (counted == null || counted < 0) return HandleOutcome.clarifyKey('needCounted');
    if (!mounted) return HandleOutcome.errorKey('h.screenNotReady');
    setState(() => _cash.text = counted.toStringAsFixed(counted.truncateToDouble() == counted ? 0 : 2));
    return null;
  }

  @override
  void initState() {
    super.initState();
    _aiHandles.register('close_shift', _aiHandle);
    WidgetsBinding.instance.addPostFrameCallback((_) => _reload());
  }

  @override
  void dispose() {
    _aiHandles.unregister('close_shift', _aiHandle);
    _cash.dispose();
    super.dispose();
  }

  Future<ShiftBreakdown?> _reload() async {
    await ref.read(shiftProvider.notifier).refresh();
    final shift = ref.read(shiftProvider).shift;
    if (shift == null || !mounted) return null;
    setState(() => _bdLoading = true);
    try {
      final bd = await ref.read(shiftRepoProvider).breakdown(shift);
      if (mounted) setState(() => _bd = bd);
      return bd;
    } finally {
      if (mounted) setState(() => _bdLoading = false);
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

  Future<void> _close({bool ask = true}) async {
    final l = context.l;
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await ref.read(syncProvider).runOnce();
      final (pending, failed) = await _queueCounts();
      if (pending > 0) {
        if (mounted) toast(context, l.closeShiftPending(pending), error: true);
        _aiSink.report(HandleOutcome.error(l.closeShiftPending(pending)));
        return;
      }
      if (failed > 0) {
        // Не принятые сервером чеки не попадут в отчёт смены — сначала разобрать их.
        if (mounted) toast(context, l.closeShiftFailed(failed), error: true);
        _aiSink.report(HandleOutcome.error(l.closeShiftFailed(failed)));
        return;
      }
      if (!mounted) return;
      // 2026-10-26 (П6): свежие итоги прямо перед закрытием — по ним же строится Z-итог (после закрытия смены их уже не взять).
      final bd = await _reload() ?? _bd;
      if (!mounted) return;
      final shift = ref.read(shiftProvider).shift;
      final counted = parseNum(_cash.text);
      final ok = !ask
          ? true
          : await showDialog<bool>(
              context: context,
              builder: (c) => AlertDialog(
                title: Text(l.closeShiftAsk),
                content: Text(
                  [
                    '${l.revenue}: ${money(shift?.salesTotal ?? 0)} ${l.som}',
                    '${l.shouldBeInDrawer}: ${money(bd?.serverExpected ?? shift?.expectedCash ?? 0)} ${l.som}',
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
      _aiSink.report(HandleOutcome.ok({'counted': counted ?? 0.0}));
      if (!mounted) return;
      toast(context, l.shiftClosedOk);
      // Z-итог — документ закрытой смены с печатью (смена уже закрыта: «Назад» и «Готово» ведут на главную).
      // 2026-10-26 (приёмка 1.18.6, Б-1): раньше документ подменял этот экран через pushReplacement, а «Готово» вызывало
      // c.go('/home') — go_router уже считал адрес «/home» и ничего не менял, а PopScope(canPop: false) глушил «Назад»:
      // кассир оставался заперт. Теперь документ открывается ПОВЕРХ этого экрана, «Готово» и «Назад» просто закрывают его
      // (явный pop), а после закрытия мы сами уходим на «Главную» — этот экран остаётся под документом и убирается вместе с ним.
      if (shift != null && bd != null) {
        final doc = shiftTapeDoc(l, ref.read(sessionProvider), shift, bd, z: true, counted: counted, closedAt: DateTime.now());
        final router = GoRouter.of(context);
        await Navigator.of(context).push(
          MaterialPageRoute<void>(
            builder: (c) => TapeDocScreen(doc: doc, title: l.zReport, subtitle: l.shiftClosed, lockBack: true, onDone: () => Navigator.of(c).pop()),
          ),
        );
        router.go('/home');
        return;
      }
      context.go('/home');
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
      // ответ на закрытие не дошёл — смена могла закрыться: ИИ не говорит «закрыла», а просит проверить
      _aiSink.report(OpsRepository.outcomeUnknown(e) ? const HandleOutcome(ToolStatus.error, unknown: true) : HandleOutcome.error(e.message));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  /// X-отчёт — итоги смены без закрытия (свежие с сервера), как на кассе ПК; отдельный экран-«лента» с печатью и PDF.
  Future<void> _xReport() async {
    final l = context.l;
    final bd = await _reload() ?? _bd;
    if (!mounted) return;
    final s = ref.read(shiftProvider).shift;
    if (s == null || bd == null) return;
    final (pending, failed) = await _queueCounts();
    if (!mounted) return;
    final doc = shiftTapeDoc(l, ref.read(sessionProvider), s, bd, pending: pending, failed: failed);
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => TapeDocScreen(
          doc: doc,
          title: l.xReport,
          subtitle: s.openedAt == null ? null : l.sinceChecks(TimeOfDay.fromDateTime(s.openedAt!).format(context), s.salesCount),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final shift = ref.watch(shiftProvider).shift;
    final opened = shift?.openedAt;
    final kb = MediaQuery.viewInsetsOf(context).bottom > 0;
    // Пока разбивка не пришла — прежние итоги из самой смены (без выдуманных строк).
    final bd = _bd ?? (shift == null ? null : ShiftBreakdown.build(shift: shift, partial: false));
    final expected = bd?.serverExpected ?? shift?.expectedCash ?? 0;
    final closeButton = FilledButton(
      onPressed: _busy || shift == null ? null : _close,
      child: _busy ? SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5, color: T.navy)) : Text(l.closeShift),
    );
    return Scaffold(
      appBar: ScreenTitle(
        l.closeShift,
        subtitle: opened == null ? null : l.sinceChecks(TimeOfDay.fromDateTime(opened).format(context), shift?.salesCount ?? 0),
      ),
      body: ListView(
        keyboardDismissBehavior: ScrollViewKeyboardDismissBehavior.onDrag,
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
        children: [
          if (_bdLoading) const Padding(padding: EdgeInsets.only(bottom: 8), child: LinearProgressIndicator()),
          // 2026-10-26 (приёмка 1.18.6, С-3н): поле «Пересчитано в кассе» было ниже сгиба (на 411×731 dp его не видно без прокрутки,
          // а закреплённая кнопка закрывала смену без пересчёта) — теперь оно стоит первым, итоги смены ниже.
          Container(
            padding: const EdgeInsets.all(16),
            decoration: BoxDecoration(
              color: pal.heroBg,
              borderRadius: BorderRadius.circular(18),
              border: Border.all(color: pal.heroBorder),
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(l.shouldBeInDrawer, style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 13)),
                    ),
                    Text('${money(expected)} ${l.som}', style: const TextStyle(color: T.accent, fontWeight: FontWeight.w800)),
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
                  textInputAction: TextInputAction.done,
                  // Поле прокручивается над клавиатурой вместе с подсказкой и кнопкой под ним.
                  scrollPadding: const EdgeInsets.fromLTRB(20, 20, 20, 220),
                  style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800, color: T.text),
                  decoration: InputDecoration(hintText: l.howMuchInDrawer, fillColor: Colors.white),
                  onChanged: (_) => setState(() {}),
                ),
                const SizedBox(height: 8),
                if (parseNum(_cash.text) case final v? when shift != null && (v - expected).abs() > 0.004)
                  Text(l.cashDiff(money(v - expected)), style: const TextStyle(color: T.accent, fontSize: 12.5, fontWeight: FontWeight.w700))
                else
                  Text(l.cashDiffHint, style: const TextStyle(color: Color(0xFFC7D2DB), fontSize: 12)),
              ],
            ),
          ),
          const SizedBox(height: 12),
          if (bd != null) ...[
            Panel(padding: const EdgeInsets.all(16), child: TapeItemsList(shiftSalesItems(l, bd))),
            const SizedBox(height: 8),
            Panel(padding: const EdgeInsets.all(16), child: TapeItemsList(shiftCashItems(l, bd))),
          ],
          const SizedBox(height: 8),
          OutlinedButton.icon(
            onPressed: _busy || shift == null ? null : _xReport,
            icon: const Icon(Icons.receipt_long_outlined),
            label: Text(l.xReport),
          ),
          // С клавиатурой кнопка внизу экрана не видна — она идёт в самом списке, под полем пересчёта.
          if (kb) ...[const SizedBox(height: 12), closeButton],
        ],
      ),
      bottomNavigationBar: kb
          ? null
          : SafeArea(
              child: Padding(padding: const EdgeInsets.fromLTRB(16, 6, 16, 12), child: closeButton),
            ),
    );
  }
}
