// 2026-10-26 (голос-управление, В7): экраны «Задачи» (/admin/tasks — руководитель) и «Мои задачи» (/tasks/my — любой сотрудник).
// Макет: design/NurMarket-design/screens/Mobile-Call-Staff.html (состояния tasks и my). Данные — api/main/tasks/
// (tasks_repository.dart); приоритет и напоминание — служебная строка в описании до Б-62. Фоновых уведомлений нет —
// напоминание показывается плашкой при открытии приложения (главная и «Мои задачи»).
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/assistant/staff_tools.dart' show StaffPerson, StaffTask, TaskState;
import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../admin/admin_more.dart' show staffRepoProvider;
import 'staff_providers.dart';
import 'tasks_repository.dart';

String _two(int v) => v.toString().padLeft(2, '0');
String _dm(DateTime d) => '${_two(d.day)}.${_two(d.month)}';
String _dmHm(DateTime d) => '${_dm(d)} ${_two(d.hour)}:${_two(d.minute)}';

String taskStatusName(BuildContext c, TaskState s) => switch (s) {
  TaskState.pending => c.l.stfStNew,
  TaskState.inProgress => c.l.stfStWork,
  TaskState.done => c.l.stfStDone,
};

/// Карточка задачи (макет: флажок приоритета, исполнитель, срок, чип статуса; просрочена — красным).
class TaskCard extends StatelessWidget {
  final StaffTask task;
  final String? assignee;
  final bool overdue;
  final VoidCallback? onTap;
  final Widget? actions;
  const TaskCard({super.key, required this.task, this.assignee, required this.overdue, this.onTap, this.actions});

  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    final l = context.l;
    final high = task.priority == 'high';
    final done = task.status == TaskState.done;
    final dueText = task.due == null ? l.stfNoDue : (overdue ? '${l.stfOverdue} · ${_dm(task.due!)}' : _dm(task.due!));
    final chipBg = switch (task.status) {
      TaskState.pending => p.border.withValues(alpha: .5),
      TaskState.inProgress => p.accentSoft,
      TaskState.done => p.successSoft,
    };
    final chipFg = switch (task.status) {
      TaskState.pending => p.textSoft,
      TaskState.inProgress => p.warn,
      TaskState.done => p.success,
    };
    return Panel(
      onTap: onTap,
      padding: const EdgeInsets.fromLTRB(12, 12, 12, 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.flag_rounded, size: 20, color: high ? p.danger : p.textSoft.withValues(alpha: .6), semanticLabel: high ? l.stfUrgent : null),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  (high ? kHighMark : '') + task.title,
                  maxLines: 3,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(fontWeight: FontWeight.w800, decoration: done ? TextDecoration.lineThrough : null, color: done ? p.textSoft : null),
                ),
                const SizedBox(height: 6),
                Wrap(
                  spacing: 12,
                  runSpacing: 4,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    if (assignee != null && assignee!.isNotEmpty) _Meta(Icons.person_outline_rounded, assignee!, p.textSoft),
                    _Meta(Icons.event_rounded, dueText, overdue ? p.danger : p.textSoft, bold: overdue),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                      decoration: BoxDecoration(color: chipBg, borderRadius: BorderRadius.circular(8)),
                      child: Text(taskStatusName(context, task.status), style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: chipFg)),
                    ),
                  ],
                ),
                if (actions != null) ...[const SizedBox(height: 8), actions!],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _Meta extends StatelessWidget {
  final IconData icon;
  final String text;
  final Color color;
  final bool bold;
  const _Meta(this.icon, this.text, this.color, {this.bold = false});
  @override
  Widget build(BuildContext context) => Row(
    mainAxisSize: MainAxisSize.min,
    children: [
      Icon(icon, size: 15, color: color),
      const SizedBox(width: 4),
      Flexible(
        child: Text(text, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12.5, color: color, fontWeight: bold ? FontWeight.w700 : FontWeight.w500)),
      ),
    ],
  );
}

// ---------------------------------------------------------------------------------------------------------------------
// Задачи (руководитель)
// ---------------------------------------------------------------------------------------------------------------------

class TasksScreen extends ConsumerStatefulWidget {
  const TasksScreen({super.key});
  @override
  ConsumerState<TasksScreen> createState() => _TasksScreenState();
}

class _TasksScreenState extends ConsumerState<TasksScreen> {
  List<StaffTask>? _all;
  Map<String, StaffPerson> _people = {};
  TaskFilter _filter = TaskFilter.all;
  bool _failed = false;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load({bool force = false}) async {
    setState(() => _failed = false);
    try {
      final tasks = await ref.read(tasksRepoProvider).list(force: force);
      List<StaffPerson> people = const [];
      try {
        people = [for (final e in await ref.read(staffRepoProvider).list()) StaffPerson(e.id, e.name, e.role, active: !e.dismissed)];
      } on ApiException {
        // имена не пришли — карточки без исполнителя, список задач остаётся
      }
      if (!mounted) return;
      setState(() {
        _all = tasks;
        _people = {for (final p in people) p.id: p};
      });
    } on ApiException {
      if (mounted) setState(() => _failed = _all == null);
      if (mounted && _all != null) toast(context, context.l.stfLoadFail, error: true);
    }
  }

  Future<void> _run(Future<void> Function() f) async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await f();
      await _load(force: true);
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _newTask() async {
    final made = await showModalBottomSheet<StaffTask>(
      context: context,
      isScrollControlled: true,
      useSafeArea: true,
      builder: (_) => _TaskForm(people: _people.values.toList()),
    );
    if (made == null) return;
    await _run(() async {
      await ref.read(tasksRepoProvider).create(made, key: _key());
      if (mounted) toast(context, context.l.stfTaskCreated);
    });
  }

  String _key() => DateTime.now().microsecondsSinceEpoch.toRadixString(16);

  Future<void> _open(StaffTask t) async {
    final act = await showModalBottomSheet<_TaskAct>(
      context: context,
      useSafeArea: true,
      builder: (_) => _TaskActions(task: t, assignee: _people[t.assigneeId]?.name),
    );
    if (act == null || !mounted) return;
    final repo = ref.read(tasksRepoProvider);
    switch (act) {
      case _TaskAct.progress:
        await _run(() => repo.update(t.copyWith(status: TaskState.inProgress)));
      case _TaskAct.done:
        await _run(() => repo.update(t.copyWith(status: TaskState.done)));
      case _TaskAct.reopen:
        await _run(() => repo.update(t.copyWith(status: TaskState.pending)));
      case _TaskAct.due:
        final d = await showDatePicker(
          context: context,
          initialDate: t.due ?? DateTime.now(),
          firstDate: DateTime.now().subtract(const Duration(days: 365)),
          lastDate: DateTime.now().add(const Duration(days: 365 * 3)),
        );
        if (d != null) await _run(() => repo.update(t.copyWith(due: d)));
      case _TaskAct.assignee:
        final who = await showModalBottomSheet<StaffPerson>(
          context: context,
          useSafeArea: true,
          // 2026-10-28: уволенных исполнителями не предлагаем (текущий остаётся в списке)
builder: (_) => _PeoplePicker(people: _people.values.where((p) => p.active || p.id == t.assigneeId).toList(), current: t.assigneeId),
        );
        if (who != null) await _run(() => repo.update(t.copyWith(assigneeId: who.id)));
      case _TaskAct.delete:
        final ok = await showDialog<bool>(
          context: context,
          builder: (c) => AlertDialog(
            title: Text(context.l.stfDeleteAsk(t.title)),
            actions: [
              TextButton(onPressed: () => Navigator.pop(c, false), child: Text(context.l.cancel)),
              FilledButton(style: FilledButton.styleFrom(backgroundColor: T.danger, minimumSize: const Size(110, 48)), onPressed: () => Navigator.pop(c, true), child: Text(context.l.stfDelete)),
            ],
          ),
        );
        if (ok == true) await _run(() => repo.delete(t.id));
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = context.pal;
    final me = ref.watch(sessionProvider)?.userId ?? '';
    final now = DateTime.now();
    final all = _all;
    final late = all?.where((t) => t.overdue(now)).length ?? 0;
    final shown = all == null ? const <StaffTask>[] : filterTasks(all, _filter, myId: me, now: now);
    String chip(TaskFilter f) => switch (f) {
      TaskFilter.all => l.filterAll,
      TaskFilter.overdue => late > 0 ? '${l.stfFilterOverdue} · $late' : l.stfFilterOverdue,
      TaskFilter.mine => l.stfMyTasksTitle,
      TaskFilter.done => l.stfFilterDone,
    };
    return Scaffold(
      appBar: ScreenTitle(
        l.stfTasksTitle,
        subtitle: all == null ? null : l.stfTasksSub(all.length, '$late'),
        actions: [
          IconButton.filled(
            tooltip: l.stfNewTask,
            style: IconButton.styleFrom(minimumSize: const Size(48, 48), backgroundColor: p.action, foregroundColor: p.onAction),
            onPressed: _busy ? null : _newTask,
            icon: const Icon(Icons.add_rounded, size: 26),
          ),
        ],
      ),
      body: all == null
          ? (_failed
                ? Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(l.stfLoadFail, style: TextStyle(color: p.textSoft)),
                        const SizedBox(height: 8),
                        FilledButton(onPressed: () => _load(force: true), child: Text(l.retry)),
                      ],
                    ),
                  )
                : const Center(child: CircularProgressIndicator()))
          : Column(
              children: [
                SizedBox(
                  height: 48,
                  child: ListView(
                    scrollDirection: Axis.horizontal,
                    padding: const EdgeInsets.symmetric(horizontal: 16),
                    children: [
                      for (final f in TaskFilter.values)
                        Padding(
                          padding: const EdgeInsets.only(right: 6),
                          child: ChoiceChip(label: Text(chip(f)), selected: _filter == f, onSelected: (_) => setState(() => _filter = f)),
                        ),
                    ],
                  ),
                ),
                Expanded(
                  child: RefreshIndicator(
                    onRefresh: () => _load(force: true),
                    child: shown.isEmpty
                        ? ListView(children: [Padding(padding: const EdgeInsets.all(40), child: Center(child: Text(l.stfNoTasks, style: TextStyle(color: p.textSoft))))])
                        : ListView.separated(
                            padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                            itemCount: shown.length + 1,
                            separatorBuilder: (_, _) => const SizedBox(height: 8),
                            itemBuilder: (c, i) {
                              if (i == shown.length) {
                                return Padding(
                                  padding: const EdgeInsets.only(top: 8),
                                  child: Text(l.stfPriorityHint, textAlign: TextAlign.center, style: TextStyle(fontSize: 11.5, color: p.textSoft)),
                                );
                              }
                              final t = shown[i];
                              return TaskCard(task: t, assignee: _people[t.assigneeId]?.name, overdue: t.overdue(now), onTap: _busy ? null : () => _open(t));
                            },
                          ),
                  ),
                ),
              ],
            ),
    );
  }
}

enum _TaskAct { progress, done, reopen, due, assignee, delete }

class _TaskActions extends StatelessWidget {
  final StaffTask task;
  final String? assignee;
  const _TaskActions({required this.task, this.assignee});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    Widget tile(IconData icon, String text, _TaskAct a, {bool danger = false}) => ListTile(
      minTileHeight: 52,
      leading: Icon(icon, color: danger ? T.danger : null),
      title: Text(text, style: TextStyle(color: danger ? T.danger : null, fontWeight: FontWeight.w600)),
      onTap: () => Navigator.pop(context, a),
    );
    return SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 8, 20, 4),
            child: Align(
              alignment: Alignment.centerLeft,
              child: Text(task.title, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
            ),
          ),
          if (task.status != TaskState.inProgress && task.status != TaskState.done) tile(Icons.play_arrow_rounded, l.stfToWork, _TaskAct.progress),
          if (task.status != TaskState.done) tile(Icons.check_circle_outline_rounded, l.stfMarkDone, _TaskAct.done),
          if (task.status == TaskState.done) tile(Icons.replay_rounded, l.stfReopen, _TaskAct.reopen),
          tile(Icons.event_rounded, l.stfEditDue, _TaskAct.due),
          tile(Icons.swap_horiz_rounded, l.stfEditAssignee, _TaskAct.assignee),
          tile(Icons.delete_outline_rounded, l.stfDelete, _TaskAct.delete, danger: true),
          const SizedBox(height: 8),
        ],
      ),
    );
  }
}

class _PeoplePicker extends StatelessWidget {
  final List<StaffPerson> people;
  final String? current;
  const _PeoplePicker({required this.people, this.current});
  @override
  Widget build(BuildContext context) => SafeArea(
    child: ConstrainedBox(
      constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * .7),
      child: ListView(
        shrinkWrap: true,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 12, 20, 4),
            child: Text(context.l.stfAssignee, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17)),
          ),
          for (final p in people)
            ListTile(
              minTileHeight: 52,
              leading: const Icon(Icons.person_outline_rounded),
              title: Text(p.name),
              subtitle: p.role.isEmpty ? null : Text(p.role),
              trailing: p.id == current ? const Icon(Icons.check_rounded) : null,
              onTap: () => Navigator.pop(context, p),
            ),
        ],
      ),
    ),
  );
}

/// Новая задача: что сделать, кому, срок, срочность, напоминание.
class _TaskForm extends StatefulWidget {
  final List<StaffPerson> people;
  const _TaskForm({required this.people});
  @override
  State<_TaskForm> createState() => _TaskFormState();
}

class _TaskFormState extends State<_TaskForm> {
  final _title = TextEditingController();
  final _note = TextEditingController();
  StaffPerson? _who;
  DateTime? _due;
  DateTime? _remind;
  bool _urgent = false;
  String? _err;

  Future<void> _pickDue() async {
    final d = await showDatePicker(
      context: context,
      initialDate: _due ?? DateTime.now(),
      firstDate: DateTime.now().subtract(const Duration(days: 1)),
      lastDate: DateTime.now().add(const Duration(days: 365 * 3)),
    );
    if (d != null) setState(() => _due = d);
  }

  Future<void> _pickRemind() async {
    final d = await showDatePicker(
      context: context,
      initialDate: _remind ?? _due ?? DateTime.now(),
      firstDate: DateTime.now().subtract(const Duration(days: 1)),
      lastDate: DateTime.now().add(const Duration(days: 365 * 3)),
    );
    if (d == null || !mounted) return;
    final t = await showTimePicker(context: context, initialTime: const TimeOfDay(hour: 9, minute: 0));
    if (t != null) setState(() => _remind = DateTime(d.year, d.month, d.day, t.hour, t.minute));
  }

  void _submit() {
    final l = context.l;
    if (_title.text.trim().isEmpty) return setState(() => _err = l.stfNeedTitle);
    if (_who == null) return setState(() => _err = l.stfNeedAssignee);
    Navigator.pop(
      context,
      StaffTask(
        title: _title.text.trim(),
        description: _note.text.trim(),
        due: _due,
        assigneeId: _who!.id,
        priority: _urgent ? 'high' : 'normal',
        remindAt: _remind,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = context.pal;
    return Padding(
      padding: EdgeInsets.fromLTRB(20, 8, 20, MediaQuery.viewInsetsOf(context).bottom + 16),
      child: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l.stfNewTask, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
            const SizedBox(height: 12),
            FieldLabel(l.stfTaskField, upper: false),
            TextField(controller: _title, autofocus: true, maxLines: 2, minLines: 1, textInputAction: TextInputAction.next, decoration: InputDecoration(hintText: l.stfTaskHint)),
            const SizedBox(height: 8),
            FieldLabel(l.stfAssignee, upper: false),
            DropdownButtonFormField<StaffPerson>(
              initialValue: _who,
              isExpanded: true,
              decoration: InputDecoration(hintText: l.stfAssigneeHint),
              items: [for (final x in widget.people.where((p) => p.active)) DropdownMenuItem(value: x, child: Text(x.name, overflow: TextOverflow.ellipsis))],
              onChanged: (v) => setState(() => _who = v),
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: OutlinedButton.icon(
                    style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)),
                    onPressed: _pickDue,
                    icon: const Icon(Icons.event_rounded, size: 18),
                    label: Text(_due == null ? l.stfDue : '${l.stfDue}: ${_dm(_due!)}', overflow: TextOverflow.ellipsis),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: OutlinedButton.icon(
                    style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)),
                    onPressed: _pickRemind,
                    icon: const Icon(Icons.alarm_rounded, size: 18),
                    label: Text(_remind == null ? l.stfRemind : _dmHm(_remind!), overflow: TextOverflow.ellipsis),
                  ),
                ),
              ],
            ),
            SwitchListTile(
              contentPadding: EdgeInsets.zero,
              title: Text(l.stfUrgent, style: const TextStyle(fontWeight: FontWeight.w600)),
              value: _urgent,
              onChanged: (v) => setState(() => _urgent = v),
            ),
            if (_err != null) Padding(padding: const EdgeInsets.only(bottom: 8), child: Text(_err!, style: TextStyle(color: p.danger))),
            FilledButton(style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(T.ctaHeight)), onPressed: _submit, child: Text(l.stfCreate)),
          ],
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Мои задачи (любой сотрудник)
// ---------------------------------------------------------------------------------------------------------------------

class MyTasksScreen extends ConsumerStatefulWidget {
  const MyTasksScreen({super.key});
  @override
  ConsumerState<MyTasksScreen> createState() => _MyTasksScreenState();
}

class _MyTasksScreenState extends ConsumerState<MyTasksScreen> {
  List<StaffTask>? _mine;
  bool _failed = false;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load({bool force = false}) async {
    final me = ref.read(sessionProvider)?.userId ?? '';
    try {
      final all = await ref.read(tasksRepoProvider).list(force: force);
      if (mounted) {
        setState(() {
          _mine = all.where((t) => t.assigneeId == me).toList();
          _failed = false;
        });
      }
    } on ApiException {
      if (mounted) setState(() => _failed = _mine == null);
    }
  }

  /// Исполнитель меняет только статус своей задачи (Б-62: остальное — 403).
  Future<void> _set(StaffTask t, TaskState st) async {
    if (_busy) return;
    setState(() => _busy = true);
    try {
      await ref.read(tasksRepoProvider).update(t.copyWith(status: st), statusOnly: true);
      ref.invalidate(myTasksProvider);
      await _load(force: true);
    } on ApiException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final p = context.pal;
    final s = ref.watch(sessionProvider);
    final now = DateTime.now();
    final mine = _mine;
    final sum = mine == null ? (today: 0, overdue: 0) : todaySummary(mine, s?.userId ?? '', now);
    final shown = [...?mine]..sort(compareTasks(now));
    final firstLate = shown.where((t) => t.overdue(now)).firstOrNull;
    return Scaffold(
      appBar: ScreenTitle(l.stfMyTasksTitle, subtitle: mine == null ? null : l.stfMySub(s?.userName ?? '', '${sum.today}', '${sum.overdue}')),
      body: mine == null
          ? (_failed
                ? Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(l.stfLoadFail, style: TextStyle(color: p.textSoft)),
                        const SizedBox(height: 8),
                        FilledButton(onPressed: () => _load(force: true), child: Text(l.retry)),
                      ],
                    ),
                  )
                : const Center(child: CircularProgressIndicator()))
          : RefreshIndicator(
              onRefresh: () => _load(force: true),
              child: ListView(
                padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                children: [
                  if (firstLate != null) ...[
                    _Plate(icon: Icons.warning_amber_rounded, color: p.warn, bg: p.warnSoft, text: l.stfOverduePlate(sum.overdue, firstLate.title)),
                    const SizedBox(height: 8),
                  ],
                  for (final r in dueReminders(mine, s?.userId ?? '', now).take(2)) ...[
                    _Plate(icon: Icons.alarm_rounded, color: p.text, bg: p.accentSoft, text: l.stfRemindPlate(r.title)),
                    const SizedBox(height: 8),
                  ],
                  if (shown.isEmpty) Padding(padding: const EdgeInsets.all(40), child: Center(child: Text(l.stfNoTasks, style: TextStyle(color: p.textSoft)))),
                  for (final t in shown) ...[
                    TaskCard(
                      task: t,
                      overdue: t.overdue(now),
                      actions: t.status == TaskState.done
                          ? null
                          : Wrap(
                              spacing: 8,
                              runSpacing: 8,
                              children: [
                                if (t.status == TaskState.pending)
                                  FilledButton.tonal(
                                    style: FilledButton.styleFrom(minimumSize: const Size(0, 48)),
                                    onPressed: _busy ? null : () => _set(t, TaskState.inProgress),
                                    child: Text(l.stfToWork),
                                  ),
                                FilledButton(
                                  style: FilledButton.styleFrom(minimumSize: const Size(0, 48)),
                                  onPressed: _busy ? null : () => _set(t, TaskState.done),
                                  child: Text(l.stfMarkDone),
                                ),
                              ],
                            ),
                    ),
                    const SizedBox(height: 8),
                  ],
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: Text(l.stfNoBackground, textAlign: TextAlign.center, style: TextStyle(fontSize: 11.5, color: p.textSoft)),
                  ),
                ],
              ),
            ),
    );
  }
}

class _Plate extends StatelessWidget {
  final IconData icon;
  final Color color;
  final Color bg;
  final String text;
  const _Plate({required this.icon, required this.color, required this.bg, required this.text});
  @override
  Widget build(BuildContext context) => Container(
    constraints: const BoxConstraints(minHeight: 44),
    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
    decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(12)),
    child: Row(
      children: [
        Icon(icon, size: 20, color: color),
        const SizedBox(width: 10),
        Expanded(child: Text(text, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13))),
      ],
    ),
  );
}

/// Плашка на главной: «Задач на сегодня: 2 · просрочено 1» (макет Mobile-Call-Staff, состояние home) и напоминание.
/// Тапом — в «Мои задачи». Без связи, без входа или без задач — не показывается.
class TasksTodayPlate extends ConsumerWidget {
  final VoidCallback onTap;
  const TasksTodayPlate({super.key, required this.onTap});
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mine = ref.watch(myTasksProvider).value;
    final me = ref.watch(sessionProvider)?.userId ?? '';
    if (mine == null || mine.isEmpty) return const SizedBox.shrink();
    final now = DateTime.now();
    final sum = todaySummary(mine, me, now);
    final reminders = dueReminders(mine, me, now);
    if (sum.today == 0 && sum.overdue == 0 && reminders.isEmpty) return const SizedBox.shrink();
    final l = context.l;
    final p = context.pal;
    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Panel(
        onTap: onTap,
        color: p.accentSoft,
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
        child: Row(
          children: [
            Icon(Icons.checklist_rounded, size: 24, color: p.text),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(l.stfTodayPlate('${sum.today}', '${sum.overdue}'), style: TextStyle(fontWeight: FontWeight.w800, color: sum.overdue > 0 ? p.danger : p.text)),
                  if (reminders.isNotEmpty) Text(l.stfRemindPlate(reminders.first.title), maxLines: 2, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: p.textSoft)),
                ],
              ),
            ),
            Icon(Icons.chevron_right_rounded, color: p.textSoft),
          ],
        ),
      ),
    );
  }
}
