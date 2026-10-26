// 2026-10-26 (голос-управление, В5): экран «Помощник» (/ai/settings, Профиль) — режим голосового управления (раздел 2.3 ТЗ).
// Макет: design/NurMarket-design/screens/Mobile-Call-Settings.html (Г6). Три режима радиокарточками (≥ 72 dp): «Выполнять по
// просьбе» (по умолчанию), «Спрашивать перед деньгами», «Только смотреть». Владелец и администратор задают режим для всех на
// этом телефоне (`assistant.mode`), сотрудник — только для себя (`assistant.mode.<id>`) и только строже общего: действует
// более строгий из двух. Голосом режим не меняется — для этого нет ни одного инструмента (тест assistant_settings_test.dart).
// Чтение/запись настройки — [AssistantModePrefs] (assistant_mode.dart), движок звонка читает его же.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/ai/ai_access.dart';
import '../../core/ai/assistant/ai_journal.dart';
import '../../core/ai/assistant/tool_registry.dart' show AssistantMode;
import '../../core/api/session_store.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import 'assistant_mode.dart';

/// Мелкая метка (макет `.tag`): «по умолчанию», «владелец», счётчик записей.
class AiTag extends StatelessWidget {
  final String text;
  final IconData? icon;
  final bool accent;
  const AiTag(this.text, {super.key, this.icon, this.accent = false});
  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    final soft = pal.dark ? T.darkPanel : T.soft;
    return Container(
      constraints: const BoxConstraints(minHeight: 22),
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: accent ? pal.accentSoft : soft,
        borderRadius: BorderRadius.circular(11),
        border: accent ? null : Border.all(color: pal.border),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[Icon(icon, size: 12, color: pal.text), const SizedBox(width: 4)],
          Flexible(
            child: Text(text, style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, color: accent ? pal.text : pal.text.withValues(alpha: .8), height: 1.2)),
          ),
        ],
      ),
    );
  }
}

/// Плашка-пояснение (макет `.plate`): значок слева, текст, минимум 52 dp. [info] — на жёлтой подложке.
class AiPlate extends StatelessWidget {
  final IconData icon;
  final Widget child;
  final bool info;
  final double minHeight;
  const AiPlate({super.key, required this.icon, required this.child, this.info = false, this.minHeight = 52});
  @override
  Widget build(BuildContext context) {
    final pal = context.pal;
    return Container(
      constraints: BoxConstraints(minHeight: minHeight),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: info ? pal.accentSoft : pal.panel,
        borderRadius: BorderRadius.circular(T.radiusCard),
        border: info ? null : Border.all(color: pal.border),
      ),
      child: Row(
        children: [
          Icon(icon, size: 22, color: pal.text),
          const SizedBox(width: 10),
          Expanded(child: child),
        ],
      ),
    );
  }
}

class AssistantSettingsScreen extends ConsumerStatefulWidget {
  const AssistantSettingsScreen({super.key});
  @override
  ConsumerState<AssistantSettingsScreen> createState() => _AssistantSettingsScreenState();
}

class _AssistantSettingsScreenState extends ConsumerState<AssistantSettingsScreen> {
  AssistantMode? _shared;
  AssistantMode? _own;
  int _logCount = 0;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final s = ref.read(sessionProvider);
    final db = ref.read(databaseProvider);
    final shared = await AssistantModePrefs.loadShared(db);
    final own = await AssistantModePrefs.loadOwn(db, s?.userId ?? '');
    final n = (await AiJournal().read(limit: AiJournal.maxRecords, userId: _seeAll(s) ? null : (s?.userId ?? ''))).length;
    if (!mounted) return;
    setState(() {
      _shared = shared;
      _own = own;
      _logCount = n;
    });
  }

  /// Владелец и администратор видят всё и меняют режим для всех на телефоне; сотрудник — только свой, и строже общего.
  static bool _seeAll(Session? s) => s != null && s.isAdmin;

  /// Что действует на самом деле (так же читает движок звонка): более строгий из общего и своего.
  AssistantMode _effective() => AssistantMode.stricter(_shared!, _own!);

  Future<void> _pick(AssistantMode m, Session? s) async {
    if (_saving || s == null) return;
    final manager = _seeAll(s);
    // сотрудник: мягче общего нельзя; без своего id сохранить «для себя» негде
    if (!manager && (AssistantMode.stricter(_shared!, m) != m || s.userId.isEmpty)) return;
    setState(() => _saving = true);
    try {
      final db = ref.read(databaseProvider);
      if (manager) {
        // для всех на телефоне; свой режим менеджера выравниваем с ним, иначе движок взял бы более строгий из старых двух
        await AssistantModePrefs.save(db, m);
        if (s.userId.isNotEmpty) await AssistantModePrefs.save(db, m, userId: s.userId);
      } else {
        await AssistantModePrefs.save(db, m, userId: s.userId);
      }
      if (!mounted) return;
      setState(() {
        if (manager) {
          _shared = m;
          _own = m;
        } else {
          _own = m;
        }
      });
      toast(context, context.l.aiModeSaved);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  String _name(AppLocalizations l, AssistantMode m) => switch (m) {
    AssistantMode.act => l.aiModeDo,
    AssistantMode.askMoney => l.aiModeAskMoney,
    AssistantMode.viewOnly => l.aiModeWatch,
  };

  String _hint(AppLocalizations l, AssistantMode m) => switch (m) {
    AssistantMode.act => l.aiModeDoHint,
    AssistantMode.askMoney => l.aiModeAskHint,
    AssistantMode.viewOnly => l.aiModeWatchHint,
  };

  IconData _icon(AssistantMode m) => switch (m) {
    AssistantMode.act => Icons.bolt_rounded,
    AssistantMode.askMoney => Icons.shield_outlined,
    AssistantMode.viewOnly => Icons.visibility_outlined,
  };

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final s = ref.watch(sessionProvider);
    final policy = ref.watch(aiPolicyProvider).value;
    if (_shared == null || _own == null) {
      return Scaffold(
        appBar: ScreenTitle(l.assistantTitle),
        body: const Center(child: CircularProgressIndicator()),
      );
    }
    final manager = _seeAll(s);
    final shared = _shared!;
    final selected = _effective();
    final role = manager ? (s!.isOwner ? l.aiAccOwnerTag : s.role) : [s?.userName ?? '', s?.role ?? ''].where((e) => e.isNotEmpty).join(' · ');
    final subtitle = manager ? '${l.assistantSub} · $role' : role;
    // порядок как в макете: по просьбе, перед деньгами, только смотреть
    const order = [AssistantMode.act, AssistantMode.askMoney, AssistantMode.viewOnly];
    final ownerSet = !manager && shared != AssistantMode.defaultMode;
    return Scaffold(
      appBar: ScreenTitle(l.assistantTitle, subtitle: subtitle.isEmpty ? null : subtitle),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 4, 16, 16),
        children: [
          if (ownerSet) ...[
            AiPlate(
              icon: Icons.lock_outline_rounded,
              info: true,
              child: Text.rich(
                TextSpan(
                  style: TextStyle(fontSize: 13, height: 1.3, color: pal.text),
                  children: [
                    TextSpan(text: '${l.aiModeOwnerSet} '),
                    TextSpan(
                      text: _name(l, shared),
                      style: const TextStyle(fontWeight: FontWeight.w700),
                    ),
                    const TextSpan(text: '\n'),
                    TextSpan(
                      text: l.aiModeStricterSelf,
                      style: TextStyle(fontSize: 12, color: pal.textSoft),
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 8),
          ],
          for (final m in order) ...[
            _ModeCard(
              icon: _icon(m),
              title: _name(l, m),
              hint: _hint(l, m),
              on: m == selected,
              disabled: !manager && AssistantMode.stricter(shared, m) != m,
              isDefault: m == AssistantMode.defaultMode,
              ownerTag: ownerSet && m == shared && selected == shared,
              onTap: () => _pick(m, s),
            ),
            const SizedBox(height: 8),
          ],
          if (selected == AssistantMode.askMoney) ...[
            Panel(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(l.aiModeAskWhatTitle, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700)),
                  const SizedBox(height: 4),
                  Text(
                    [l.aiWhatPay, l.aiWhatReturn, l.aiWhatReceive, l.aiWhatWriteOff, l.aiWhatCount, l.aiWhatCloseShift, l.aiWhatSalary, l.aiWhatDebt, l.aiWhatDelete, l.aiWhatPrice].join(' · '),
                    style: TextStyle(fontSize: 12, height: 1.3, color: pal.textSoft),
                  ),
                  const SizedBox(height: 4),
                  Text(l.aiModeAskNote, style: TextStyle(fontSize: 12, height: 1.3, color: pal.textSoft)),
                ],
              ),
            ),
            const SizedBox(height: 8),
          ],
          AiPlate(
            icon: Icons.mic_off_outlined,
            minHeight: 44,
            child: Text(l.aiModeNoVoice, style: TextStyle(fontSize: 12, height: 1.3, color: pal.text.withValues(alpha: .8))),
          ),
          const SizedBox(height: 16),
          _LogLink(count: _logCount, onTap: () => context.push('/ai/log')),
          const SizedBox(height: 8),
          // строка статуса доступа к ИИ (ограничение «не больше 2 сотрудников») — та же, что в профиле
          if (policy != null)
            Padding(
              padding: const EdgeInsets.only(bottom: 4),
              child: Text(AiAccess.statusText(l, s, policy), textAlign: TextAlign.center, style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, height: 1.3, color: pal.text)),
            ),
          Text(l.aiModeFooter, textAlign: TextAlign.center, style: TextStyle(fontSize: 12, height: 1.3, color: pal.textSoft)),
        ],
      ),
    );
  }
}

/// Радиокарточка режима: от 72 dp, значок, заголовок с метками, пояснение; «мягче» — затенена и не нажимается.
class _ModeCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final String hint;
  final bool on;
  final bool disabled;
  final bool isDefault;
  final bool ownerTag;
  final VoidCallback onTap;
  const _ModeCard({
    required this.icon,
    required this.title,
    required this.hint,
    required this.on,
    required this.disabled,
    required this.isDefault,
    required this.ownerTag,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final accent = pal.dark ? T.accent : T.navy;
    final border = on ? accent : pal.border;
    return Semantics(
      inMutuallyExclusiveGroup: true,
      checked: on,
      enabled: !disabled,
      button: true,
      child: Opacity(
        opacity: disabled ? .55 : 1,
        child: Material(
          color: pal.panel,
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(T.radiusCard),
            side: BorderSide(color: border, width: on ? 2.5 : 1.5),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: disabled ? null : onTap,
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 72),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Padding(
                      padding: const EdgeInsets.only(top: 2),
                      child: Container(
                        width: 24,
                        height: 24,
                        decoration: BoxDecoration(shape: BoxShape.circle, border: Border.all(color: on ? accent : T.borderStrong, width: 2)),
                        child: on ? Center(child: Container(width: 12, height: 12, decoration: BoxDecoration(shape: BoxShape.circle, color: accent))) : null,
                      ),
                    ),
                    const SizedBox(width: 12),
                    Padding(
                      padding: const EdgeInsets.only(top: 2),
                      child: Icon(icon, size: 22, color: pal.text.withValues(alpha: .8)),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Wrap(
                            spacing: 8,
                            runSpacing: 4,
                            crossAxisAlignment: WrapCrossAlignment.center,
                            children: [
                              Text(title, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800)),
                              if (isDefault) AiTag(l.aiModeDefault),
                              if (ownerTag) AiTag(l.aiAccOwnerTag, icon: Icons.lock_outline_rounded, accent: true),
                            ],
                          ),
                          const SizedBox(height: 3),
                          Text(hint, style: TextStyle(fontSize: 13, height: 1.35, color: pal.textSoft)),
                          if (disabled) ...[
                            const SizedBox(height: 3),
                            Text(l.aiModeSofterLocked, style: TextStyle(fontSize: 13, height: 1.35, fontWeight: FontWeight.w700, color: pal.warn)),
                          ],
                        ],
                      ),
                    ),
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

/// Кнопка «Действия ИИ ›» со счётчиком записей (макет: `btn full`, значок history, метка с числом, шеврон).
class _LogLink extends StatelessWidget {
  final int count;
  final VoidCallback onTap;
  const _LogLink({required this.count, required this.onTap});
  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    return Material(
      color: pal.panel,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(T.radiusButton),
        side: const BorderSide(color: T.borderStrong, width: 1.5),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 52),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
            child: Row(
              children: [
                Icon(Icons.history_rounded, size: 20, color: pal.text),
                const SizedBox(width: 8),
                Expanded(child: Text(l.aiLogTitle, style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w700))),
                if (count > 0) ...[AiTag('$count'), const SizedBox(width: 8)],
                Icon(Icons.chevron_right_rounded, size: 20, color: pal.text),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
