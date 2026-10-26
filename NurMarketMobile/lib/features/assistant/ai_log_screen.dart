// 2026-10-26 (голос-управление, В5): экран «Действия ИИ» (/ai/log, Профиль) — журнал `ai_actions.jsonl` по дням (раздел 2.6 ТЗ).
// Макет: design/NurMarket-design/screens/Mobile-Call-Log.html (Г5). Строка: значок группы, время, сказанная фраза курсивом,
// итог жирным и справа «Отменить» / «нельзя отменить» / «отмена — запросом владельцу» / «отменено 14:07». Фильтры: Все, Только
// деньги, Отменённые; сотрудник видит только свои записи, владелец и администратор — все записи телефона.
// «Отменить» вне звонка работает только там, где способ отмены сохранён в записи (journal_undo.dart); остальное — честная подпись.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/ai/ai_access.dart';
import '../../core/ai/assistant/action_tools.dart' show ActionHost, RepoActionHost;
import '../../core/ai/assistant/ai_journal.dart';
import '../../core/ai/assistant/journal_undo.dart';
import '../../core/ai/assistant/undo.dart' show UndoStatus;
import '../../core/api/api_client.dart' show ApiException;
import '../../core/api/session_store.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import 'assistant_settings.dart' show AiPlate;

/// Журнал действий ИИ (файл на телефоне). В тестах подменяется.
final aiJournalProvider = Provider<AiJournal>((ref) => AiJournal());

/// Исполнитель «Отменить» из журнала: те же репозитории, что у инструментов звонка. В тестах подменяется фейком.
final journalUndoerProvider = Provider<JournalUndoer>((ref) {
  final ActionHost host = RepoActionHost.fromReader(ref.read, productSales: (_, _) async => const []);
  return JournalUndoer(host);
});

enum _Filter { all, money, undone }

class AiLogScreen extends ConsumerStatefulWidget {
  const AiLogScreen({super.key});
  @override
  ConsumerState<AiLogScreen> createState() => _AiLogScreenState();
}

class _AiLogScreenState extends ConsumerState<AiLogScreen> {
  List<AiActionRecord>? _all;
  _Filter _filter = _Filter.all;
  DateTime? _day;
  final _busy = Set<AiActionRecord>.identity();

  @override
  void initState() {
    super.initState();
    _load();
  }

  static bool _seeAll(Session? s) => s != null && s.isAdmin;

  Future<void> _load() async {
    final s = ref.read(sessionProvider);
    final list = await ref.read(aiJournalProvider).read(limit: AiJournal.maxRecords, userId: _seeAll(s) ? null : (s?.userId ?? ''));
    if (mounted) setState(() => _all = list);
  }

  static String _two(int v) => v.toString().padLeft(2, '0');
  static String _hm(DateTime d) => '${_two(d.hour)}:${_two(d.minute)}';
  static bool _sameDay(DateTime a, DateTime b) => a.year == b.year && a.month == b.month && a.day == b.day;

  String _dayTitle(AppLocalizations l, DateTime d) {
    final now = DateTime.now();
    final dm = '${_two(d.day)}.${_two(d.month)}';
    if (_sameDay(d, now)) return '${l.dayToday} · $dm';
    if (_sameDay(d, now.subtract(const Duration(days: 1)))) return '${l.dayYesterday} · $dm';
    return '$dm.${d.year}';
  }

  /// Короткое название для тостов: первое предложение итога.
  static String _label(AiActionRecord r) {
    final t = r.say.split(RegExp(r'(?<=[.!?])\s')).first.trim();
    final text = t.isEmpty ? r.tool : t;
    return text.length > 60 ? '${text.substring(0, 59)}…' : text;
  }

  IconData _groupIcon(AiLogGroup g) => switch (g) {
    AiLogGroup.nav => Icons.explore_outlined,
    AiLogGroup.cart => Icons.shopping_cart_outlined,
    AiLogGroup.money => Icons.payments_outlined,
    AiLogGroup.product => Icons.inventory_2_outlined,
    AiLogGroup.task => Icons.checklist_rounded,
    AiLogGroup.salary => Icons.account_circle_outlined,
  };

  String _groupName(AppLocalizations l, AiLogGroup g) => switch (g) {
    AiLogGroup.nav => l.aiGroupNav,
    AiLogGroup.cart => l.aiGroupCart,
    AiLogGroup.money => l.aiGroupMoney,
    AiLogGroup.product => l.aiGroupProduct,
    AiLogGroup.task => l.aiGroupTask,
    AiLogGroup.salary => l.aiGroupSalary,
  };

  Future<void> _pickDay() async {
    final now = DateTime.now();
    final d = await showDatePicker(context: context, initialDate: _day ?? now, firstDate: DateTime(now.year - 2), lastDate: now);
    if (d != null && mounted) setState(() => _day = d);
  }

  Future<void> _undo(AiActionRecord r) async {
    final s = ref.read(sessionProvider);
    if (s == null || _busy.contains(r)) return;
    final l = context.l;
    setState(() => _busy.add(r));
    String? message;
    var error = false;
    try {
      final undoer = ref.read(journalUndoerProvider);
      final out = await undoer.undo(r, s);
      if (out.status == UndoStatus.ok) {
        message = out.partial ? l.aiLogUndoPartial(out.done, out.total) : l.aiLogUndoneToast(_label(r));
        await undoer.markUndone(ref.read(aiJournalProvider), r, s, message);
        await _load();
      } else {
        error = true;
        message = out.status == UndoStatus.conflict
            ? l.aiLogUndoConflict
            : out.shiftClosed
            ? l.aiLogUndoShift
            : l.aiLogUndoFailed(_label(r));
      }
    } on ApiException {
      error = true;
      message = l.aiLogUndoFailed(_label(r));
    } catch (_) {
      error = true;
      message = l.aiLogUndoFailed(_label(r));
    } finally {
      if (mounted) setState(() => _busy.remove(r));
    }
    if (mounted) toast(context, message, error: error);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final s = ref.watch(sessionProvider);
    final policy = ref.watch(aiPolicyProvider).value;
    final all = _all;
    final manager = _seeAll(s);
    final subtitle = manager ? '${l.aiLogSubAll} · ${s!.isOwner ? l.aiAccOwnerTag : s.role}' : [s?.userName ?? '', s?.role ?? ''].where((e) => e.isNotEmpty).join(' · ');
    final undone = all == null ? const <AiActionRecord, DateTime>{} : JournalUndo.undoneMap(all);
    final rows = <AiActionRecord>[
      for (final r in all ?? const <AiActionRecord>[])
        if ((_day == null || _sameDay(r.at, _day!)) &&
            switch (_filter) {
              _Filter.all => true,
              _Filter.money => JournalUndo.isMoney(r),
              _Filter.undone => undone.containsKey(r),
            })
          r,
    ];
    final canCall = s != null && s.canRoute('/admin/advisor') && (policy == null || AiAccess.canUse(s, policy));
    return Scaffold(
      appBar: ScreenTitle(
        l.aiLogTitle,
        subtitle: subtitle.isEmpty ? null : subtitle,
        actions: [IconButton(tooltip: l.aiLogByDate, constraints: const BoxConstraints(minWidth: 48, minHeight: 48), onPressed: _pickDay, icon: const Icon(Icons.calendar_today_outlined, size: 22))],
      ),
      body: all == null
          ? const Center(child: CircularProgressIndicator())
          : Column(
              children: [
                SizedBox(
                  height: 56,
                  child: ListView(
                    scrollDirection: Axis.horizontal,
                    padding: const EdgeInsets.fromLTRB(16, 4, 16, 4),
                    children: [
                      _Chip(label: l.filterAll, on: _filter == _Filter.all, onTap: () => setState(() => _filter = _Filter.all)),
                      _Chip(label: l.aiLogMoneyOnly, icon: Icons.payments_outlined, on: _filter == _Filter.money, onTap: () => setState(() => _filter = _Filter.money)),
                      _Chip(label: l.aiLogUndone, on: _filter == _Filter.undone, onTap: () => setState(() => _filter = _Filter.undone)),
                      if (_day != null)
                        _Chip(label: '${_two(_day!.day)}.${_two(_day!.month)}', icon: Icons.calendar_today_outlined, trailing: Icons.close_rounded, on: true, onTap: () => setState(() => _day = null)),
                    ],
                  ),
                ),
                if (!manager)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                    child: AiPlate(icon: Icons.account_circle_outlined, info: true, minHeight: 44, child: Text(l.aiLogMineOnly, style: const TextStyle(fontSize: 13, height: 1.3))),
                  ),
                Expanded(
                  child: rows.isEmpty
                      ? _Empty(filtered: all.isNotEmpty, canCall: canCall, onCall: () => context.push('/admin/advisor'))
                      : ListView.builder(
                          padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
                          itemCount: rows.length + 1,
                          itemBuilder: (c, i) {
                            if (i == rows.length) {
                              return Padding(
                                padding: const EdgeInsets.only(top: 8),
                                child: Text(l.aiLogFooter, textAlign: TextAlign.center, style: TextStyle(fontSize: 12, height: 1.3, color: pal.textSoft)),
                              );
                            }
                            final r = rows[i];
                            final header = i == 0 || !_sameDay(rows[i - 1].at, r.at);
                            return Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              children: [
                                if (header)
                                  Padding(
                                    padding: const EdgeInsets.fromLTRB(2, 6, 2, 8),
                                    child: Text(_dayTitle(l, r.at), style: TextStyle(fontSize: 12, fontWeight: FontWeight.w800, letterSpacing: .2, color: pal.textSoft)),
                                  ),
                                Padding(
                                  padding: const EdgeInsets.only(bottom: 8),
                                  child: _LogRow(
                                    record: r,
                                    info: JournalUndo.classify(r, s, undone),
                                    mine: r.user == (s?.userId ?? ''),
                                    who: _who(r, policy),
                                    time: _hm(r.at),
                                    group: JournalUndo.groupOf(r),
                                    groupIcon: _groupIcon(JournalUndo.groupOf(r)),
                                    groupName: _groupName(l, JournalUndo.groupOf(r)),
                                    busy: _busy.contains(r),
                                    hm: _hm,
                                    onUndo: () => _undo(r),
                                  ),
                                ),
                              ],
                            );
                          },
                        ),
                ),
              ],
            ),
    );
  }

  /// Имя автора чужой записи (владелец смотрит записи кассира): в журнале лежит id, имя берём из списка «Доступ к ИИ», если он там есть.
  String _who(AiActionRecord r, AiAccessPolicy? policy) {
    for (final u in policy?.users ?? const <AiAccessUser>[]) {
      if (u.sameId(r.user) && u.name.trim().isNotEmpty) return u.name.trim();
    }
    return '';
  }
}

/// Фильтр-чип (макет `.chip`): от 48 dp, выбранный — тёмный (в тёмной теме — жёлтый).
class _Chip extends StatelessWidget {
  final String label;
  final IconData? icon;
  final IconData? trailing;
  final bool on;
  final VoidCallback onTap;
  const _Chip({required this.label, required this.on, required this.onTap, this.icon, this.trailing});
  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final bg = on ? (pal.dark ? T.accent : T.navy) : pal.panel;
    final fg = on ? (pal.dark ? T.navy : Colors.white) : pal.text;
    return Padding(
      padding: const EdgeInsets.only(right: 8),
      child: Semantics(
        button: true,
        selected: on,
        child: Material(
          color: bg,
          shape: StadiumBorder(side: BorderSide(color: on ? bg : pal.border)),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 48),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 14),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (icon != null) ...[Icon(icon, size: 16, color: fg), const SizedBox(width: 6)],
                    Text(label, style: TextStyle(fontSize: 13, fontWeight: on ? FontWeight.w700 : FontWeight.w600, color: fg)),
                    if (trailing != null) ...[const SizedBox(width: 6), Icon(trailing, size: 16, color: fg)],
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

class _LogRow extends StatelessWidget {
  final AiActionRecord record;
  final JournalUndoInfo info;
  final bool mine;
  final String who;
  final String time;
  final AiLogGroup group;
  final IconData groupIcon;
  final String groupName;
  final bool busy;
  final String Function(DateTime) hm;
  final VoidCallback onUndo;
  const _LogRow({
    required this.record,
    required this.info,
    required this.mine,
    required this.who,
    required this.time,
    required this.group,
    required this.groupIcon,
    required this.groupName,
    required this.busy,
    required this.hm,
    required this.onUndo,
  });

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final money = group == AiLogGroup.money || group == AiLogGroup.salary;
    final soft = pal.dark ? T.darkPanel : T.soft;
    final big = MediaQuery.textScalerOf(context).scale(1) > 1.15;
    Widget noText(IconData icon, String text) => ConstrainedBox(
      constraints: BoxConstraints(maxWidth: big ? 120 : 96),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.only(top: 1),
            child: Icon(icon, size: 14, color: pal.textSoft),
          ),
          const SizedBox(width: 4),
          Flexible(
            child: Text(text, textAlign: TextAlign.right, style: TextStyle(fontSize: 11.5, height: 1.2, fontWeight: FontWeight.w600, color: pal.textSoft)),
          ),
        ],
      ),
    );
    final Widget right = switch (info.kind) {
      JournalUndoKind.available => Semantics(
        button: true,
        child: InkWell(
          onTap: busy ? null : onUndo,
          borderRadius: BorderRadius.circular(T.radiusButton),
          child: ConstrainedBox(
            // макет: кнопка 40 dp в строке, хитбокс 48
            constraints: BoxConstraints(minHeight: 48, maxWidth: big ? 140 : 120),
            child: Center(
              child: Container(
                constraints: const BoxConstraints(minHeight: 40),
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: pal.panel,
                  borderRadius: BorderRadius.circular(T.radiusButton),
                  border: Border.all(color: T.borderStrong, width: 1.5),
                ),
                child: busy
                    ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                    : Text(l.aiUndo, textAlign: TextAlign.center, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, height: 1.15)),
              ),
            ),
          ),
        ),
      ),
      JournalUndoKind.irreversible => noText(Icons.lock_outline_rounded, info.writeOffHint ? l.aiLogCantUndoWriteOff : l.aiCantUndo),
      JournalUndoKind.ownerRequest => noText(Icons.schedule_rounded, l.aiUndoRequest),
      JournalUndoKind.expired => noText(Icons.lock_clock_outlined, l.aiLogUndoGone),
      JournalUndoKind.undone => noText(Icons.check_rounded, l.aiLogUndoneAt(hm(info.undoneAt!))),
      JournalUndoKind.none => const SizedBox.shrink(),
    };
    final said = record.said.trim();
    return Container(
      padding: const EdgeInsets.fromLTRB(12, 10, 8, 10),
      decoration: BoxDecoration(color: pal.panel, borderRadius: BorderRadius.circular(T.radiusCard), border: Border.all(color: pal.border)),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Semantics(
            label: groupName,
            child: Container(
              width: 36,
              height: 36,
              decoration: BoxDecoration(
                color: money ? pal.accentSoft : soft,
                borderRadius: BorderRadius.circular(10),
                border: money ? null : Border.all(color: pal.border),
              ),
              child: Icon(groupIcon, size: 20, color: money ? pal.text : pal.text.withValues(alpha: .8)),
            ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  mine || who.isEmpty ? time : '$time · $who',
                  style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: pal.textSoft, fontFeatures: const [FontFeature.tabularFigures()]),
                ),
                if (said.isNotEmpty) ...[
                  const SizedBox(height: 3),
                  Text('«$said»', style: TextStyle(fontSize: 12.5, height: 1.3, fontStyle: FontStyle.italic, color: pal.textSoft)),
                ],
                const SizedBox(height: 3),
                Text(record.say, style: const TextStyle(fontSize: 14, height: 1.3, fontWeight: FontWeight.w700)),
              ],
            ),
          ),
          if (info.kind != JournalUndoKind.none) ...[const SizedBox(width: 10), right],
        ],
      ),
    );
  }
}

/// Пусто: «ИИ ещё ничего не делал…» (или «ничего не найдено» под фильтром) и подсказка про звонок.
class _Empty extends StatelessWidget {
  final bool filtered;
  final bool canCall;
  final VoidCallback onCall;
  const _Empty({required this.filtered, required this.canCall, required this.onCall});
  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.auto_awesome_outlined, size: 40, color: pal.textSoft),
            const SizedBox(height: 8),
            Text(filtered ? l.nothingFound : l.aiLogEmpty, textAlign: TextAlign.center, style: TextStyle(fontSize: 14, fontWeight: FontWeight.w700, color: pal.text.withValues(alpha: .8))),
            if (!filtered) ...[
              const SizedBox(height: 8),
              Text(l.aiLogEmptyHint, textAlign: TextAlign.center, style: TextStyle(fontSize: 12, height: 1.3, color: pal.textSoft)),
              if (canCall) ...[
                const SizedBox(height: 14),
                Material(
                  color: pal.accentSoft,
                  shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(T.radiusButton)),
                  clipBehavior: Clip.antiAlias,
                  child: InkWell(
                    onTap: onCall,
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(minHeight: 48),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
                        child: Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Icon(Icons.phone_outlined, size: 18, color: pal.text),
                            const SizedBox(width: 8),
                            Flexible(child: Text(l.aiLogCall, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700))),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
              ],
            ],
          ],
        ),
      ),
    );
  }
}
