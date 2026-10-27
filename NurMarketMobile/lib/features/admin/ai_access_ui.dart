// 2026-10-26 (владелец: «максимум 2 сотрудника имеют права использовать ИИ»): экран и плашки «Доступ к ИИ».
// Модель, хранение и проверка — core/ai/ai_access.dart; здесь только то, что видит человек:
//  - [AiAccessPanel] — плашка с объяснением лимита, счётчик «2 из 2» и выбор сотрудников (его ставят вверху «Настройки ИИ»);
//  - [AiAccessScreen] — тот же блок отдельным экраном (Профиль → «Доступ к ИИ» у владельца);
//  - [AiAccessBanner] — «Рекомендуем ограничить ИИ двумя сотрудниками» с кнопкой «Настроить» (только владельцу и пока список не задан);
//  - [showAiNoticeOnce] — короткое «Понятно» при первом открытии ИИ сотрудником с правом.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/ai_access.dart';
import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/audit.dart';
import '../../core/util/ui.dart';
import 'admin_more.dart';

/// Строка списка: человек + должность + пометки.
class _Person {
  final AiAccessUser user;
  final String role;
  final bool owner;
  const _Person(this.user, this.role, {this.owner = false});
}

/// Экран «Доступ к ИИ» (Профиль → «Доступ к ИИ»; владелец и администратор).
class AiAccessScreen extends StatelessWidget {
  const AiAccessScreen({super.key});
  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: ScreenTitle(context.l.aiAccTitle),
    body: ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 24), children: const [AiAccessPanel()]),
  );
}

/// Блок настройки: объяснение лимита, счётчик, сотрудники, «Сохранить». Сотруднику без права менять — только статус и список.
class AiAccessPanel extends ConsumerStatefulWidget {
  const AiAccessPanel({super.key});
  @override
  ConsumerState<AiAccessPanel> createState() => _AiAccessPanelState();
}

class _AiAccessPanelState extends ConsumerState<AiAccessPanel> {
  AiAccessPolicy? _policy;
  AiPicker _picker = AiPicker();
  List<_Person> _people = const [];
  bool _staffFailed = false;
  bool _busy = false;
  bool _dirty = false;
  String _warn = '';

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final session = ref.read(sessionProvider);
    final policy = await ref.read(aiAccessRepoProvider).load(force: true);
    List<Employee> staff = const [];
    var failed = false;
    if (AiAccess.canManage(session)) {
      try {
        staff = await ref.read(staffRepoProvider).list();
      } on ApiException {
        failed = true;
      } catch (_) {
        failed = true;
      }
    }
    if (!mounted) return;
    final people = <_Person>[];
    void add(_Person p) {
      if (p.user.id.isEmpty || people.any((x) => x.user == p.user)) return;
      people.add(p);
    }

    // владелец — первым (его в списке сотрудников NurCRM может не быть)
    if (session != null && session.isOwner) add(_Person(AiAccessUser(session.userId, session.userName), session.role, owner: true));
    final sorted = [...staff]..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
    for (final e in sorted) {
      add(_Person(AiAccessUser(e.id, e.name.isEmpty ? e.role : e.name), e.role, owner: _looksOwner(e.role)));
    }
    // выбранные раньше, которых сейчас нет в списке (уволены / список не получен) — не теряем
    for (final u in policy.users ?? const <AiAccessUser>[]) {
      add(_Person(u, ''));
    }
    // начальный выбор: сохранённый список, а пока его нет — владелец (как один из двух)
    var initial = policy.isSet ? policy.users! : AiAccess.defaultUsers(session);
    if (!policy.isSet && initial.isEmpty) {
      initial = [for (final p in people) if (p.owner) p.user].take(AiAccess.limit).toList();
    }
    setState(() {
      _policy = policy;
      _people = people;
      _staffFailed = failed;
      _picker = AiPicker(initial);
      _dirty = !policy.isSet && initial.isNotEmpty; // по умолчанию подставлен владелец — ещё не сохранено
    });
  }

  static bool _looksOwner(String role) {
    final r = role.toLowerCase();
    return r.contains('owner') || r.contains('владел');
  }

  void _toggle(_Person p) {
    final l = context.l;
    final r = _picker.toggle(p.user);
    setState(() {
      if (r == AiPick.full) {
        // не молчаливый отказ: объясняем и на экране, и всплывающим сообщением
        _warn = l.aiAccLimitReached(AiAccess.limit);
      } else {
        _warn = '';
        _dirty = true;
      }
    });
    if (r == AiPick.full) toast(context, l.aiAccLimitReached(AiAccess.limit), error: true);
  }

  Future<void> _save() async {
    final l = context.l;
    final session = ref.read(sessionProvider);
    setState(() => _busy = true);
    try {
      final policy = AiAccessPolicy.set(_picker.chosen, by: session?.userName ?? '', at: DateTime.now().toUtc().toIso8601String());
      await ref.read(aiAccessRepoProvider).save(policy);
      ref.invalidate(aiPolicyProvider);
      Audit.log('settings', '${l.aiAccTitle}: ${policy.users!.map((u) => u.name).join(', ')}');
      if (!mounted) return;
      setState(() {
        _policy = policy;
        _dirty = false;
      });
      // 2026-10-27: владелец убрал себя из списка — предупреждаем: ему ИИ всё равно доступен (владелец и администратор — всегда)
      final self = session?.isAdmin ?? false;
      toast(context, self && !policy.contains(session!.userId) ? '${l.aiAccSaved}. ${l.aiAccOwnerNote}' : l.aiAccSaved);
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
    final session = ref.watch(sessionProvider);
    if (_policy == null) return const Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator()));
    final manage = AiAccess.canManage(session);
    final chosen = _picker.chosen.length;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // плашка: почему лимит и сколько занято
        Panel(
          color: pal.dark ? null : T.warnSoft,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(Icons.info_outline_rounded, color: pal.warn, size: 20),
                  const SizedBox(width: 8),
                  Expanded(child: Text(l.aiAccSection, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15))),
                  Container(
                    key: const Key('aiAccCounter'),
                    padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                    decoration: BoxDecoration(color: chosen >= AiAccess.limit ? T.accent : pal.window, borderRadius: BorderRadius.circular(20)),
                    child: Text(
                      l.aiAccCounter(chosen, AiAccess.limit),
                      style: TextStyle(fontWeight: FontWeight.w800, fontSize: 13, color: chosen >= AiAccess.limit ? T.navy : null),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              Text(l.aiAccLimitInfo(AiAccess.limit), style: TextStyle(color: pal.textSoft, fontSize: 12.5, height: 1.35)),
              // 2026-10-27 (аудит мобилки, С-6): «1 из 2» при несохранённом списке путало с «ИИ доступен всем» — поясняем, что это черновик,
              // и что владельцу и администратору ИИ доступен всегда.
              Padding(
                key: const Key('aiAccAlways'),
                padding: const EdgeInsets.only(top: 8),
                child: Text(!_policy!.isSet && manage ? l.aiAccDraftNote : l.aiAccAlways, style: TextStyle(color: pal.textSoft, fontSize: 12.5, height: 1.35)),
              ),
              if (!_policy!.isSet && manage)
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Text(l.aiAccRecommend, style: TextStyle(color: pal.warn, fontSize: 12.5, fontWeight: FontWeight.w700)),
                ),
            ],
          ),
        ),
        const SizedBox(height: 10),
        if (_warn.isNotEmpty)
          Padding(
            key: const Key('aiAccWarn'),
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(_warn, style: const TextStyle(color: T.danger, fontWeight: FontWeight.w700, fontSize: 12.5)),
          ),
        if (!manage)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(l.aiAccManagerOnly, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
          ),
        if (_staffFailed)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(l.aiAccStaffNotLoaded, style: const TextStyle(color: T.danger, fontSize: 12.5)),
          ),
        if (manage && chosen == 0)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(l.aiAccNobody, style: TextStyle(color: pal.warn, fontSize: 12.5, fontWeight: FontWeight.w700)),
          ),
        for (final p in _people)
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: Panel(
              padding: EdgeInsets.zero,
              child: CheckboxListTile(
                key: Key('aiAccRow_${p.user.id}'),
                value: _picker.has(p.user),
                onChanged: manage ? (_) => _toggle(p) : null,
                title: Text(
                  [
                    p.user.name.isEmpty ? '—' : p.user.name,
                    if (p.owner) l.aiAccOwnerTag,
                    if (isSelfUser(session?.userId, p.user.id)) l.aiAccYouTag,
                  ].join(' · '),
                  style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 14.5),
                ),
                subtitle: p.role.isEmpty ? null : Text(p.role, style: TextStyle(color: pal.textSoft, fontSize: 12)),
                controlAffinity: ListTileControlAffinity.trailing,
              ),
            ),
          ),
        if (manage) ...[
          const SizedBox(height: 8),
          FilledButton(
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
            onPressed: _busy || !(_dirty || !_policy!.isSet) ? null : _save,
            child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.save),
          ),
        ],
      ],
    );
  }

  static bool isSelfUser(String? a, String b) => a != null && a.isNotEmpty && a.toLowerCase() == b.toLowerCase();
}

/// «Рекомендуем ограничить ИИ двумя сотрудниками» — только владельцу/администратору и пока список не задан.
class AiAccessBanner extends ConsumerWidget {
  const AiAccessBanner({super.key});
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final policy = ref.watch(aiPolicyProvider).value;
    final session = ref.watch(sessionProvider);
    if (policy == null || !AiAccess.shouldRecommend(session, policy)) return const SizedBox.shrink();
    final l = context.l;
    final pal = context.pal;
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Panel(
        key: const Key('aiAccBanner'),
        color: pal.dark ? null : T.warnSoft,
        child: Row(
          children: [
            Icon(Icons.warning_amber_rounded, color: pal.warn),
            const SizedBox(width: 10),
            Expanded(child: Text(l.aiAccRecommend, style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13))),
            const SizedBox(width: 8),
            TextButton(
              onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const AiAccessScreen())),
              child: Text(l.aiAccConfigure),
            ),
          ],
        ),
      ),
    );
  }
}

/// Один раз на сотрудника и телефон: короткое пояснение про общий лимит — для тех, у кого право на ИИ есть. Звать при первом
/// открытии Нур Советника, звонка и умной камеры; без права на ИИ ничего не показывает (там своё сообщение об отказе).
Future<void> showAiNoticeOnce(BuildContext context, WidgetRef ref) async {
  final session = ref.read(sessionProvider);
  if (session == null || session.userId.isEmpty) return;
  final db = ref.read(databaseProvider);
  final mark = 'ai.notice.${session.userId}';
  try {
    final policy = await ref.read(aiAccessRepoProvider).load();
    if (!AiAccess.canUse(session, policy)) return;
    if (await db.getSetting(mark) == '1') return;
    await db.setSetting(mark, '1'); // помечаем сразу: «один раз» даже если окно закроют жестом
  } catch (_) {
    return; // хранилище недоступно — лучше не показывать, чем показывать каждый раз
  }
  if (!context.mounted) return;
  final l = context.l;
  await showDialog<void>(
    context: context,
    builder: (c) => AlertDialog(
      icon: const Icon(Icons.auto_awesome_rounded),
      title: Text(l.aiAccNoticeTitle),
      content: Text(l.aiAccNoticeBody(AiAccess.limit)),
      actions: [FilledButton(onPressed: () => Navigator.pop(c), child: Text(l.aiAccGotIt))],
    ),
  );
}

/// Экран ИИ целиком (чат Советника, звонок) для сотрудника без права: вместо экрана — «ИИ доступен только 2 сотрудникам магазина:
/// …. Обратитесь к владельцу». С правом (или пока правило грузится / не загрузилось) показывает [child].
/// Владельцу и администратору в отказе даётся кнопка «Настроить».
class AiAccessGate extends ConsumerWidget {
  final Widget child;
  const AiAccessGate({super.key, required this.child});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final session = ref.watch(sessionProvider);
    final policy = ref.watch(aiPolicyProvider);
    return policy.when(
      loading: () => const Scaffold(body: Center(child: CircularProgressIndicator())),
      error: (_, _) => child, // правило не прочиталось — решает проверка в VisionService.key()
      data: (p) {
        if (AiAccess.canUse(session, p)) return child;
        final l = context.l;
        final pal = context.pal;
        return Scaffold(
          appBar: ScreenTitle(l.aiAccTitle),
          body: Center(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(Icons.lock_outline_rounded, size: 40, color: pal.textSoft),
                  const SizedBox(height: 12),
                  Text(
                    AiAccess.deniedText(l, p),
                    key: const Key('aiAccDenied'),
                    textAlign: TextAlign.center,
                    style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 15, height: 1.35),
                  ),
                  if (AiAccess.canManage(session)) ...[
                    const SizedBox(height: 16),
                    FilledButton(
                      onPressed: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const AiAccessScreen())),
                      child: Text(l.aiAccConfigure),
                    ),
                  ],
                ],
              ),
            ),
          ),
        );
      },
    );
  }
}
