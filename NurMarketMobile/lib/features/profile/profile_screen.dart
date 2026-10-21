import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../admin/advisor.dart';
import 'printer_settings.dart';

/// Профиль (Т19): касса, язык, тёмная тема, работа без интернета (очередь), выход.
class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  static const languages = {'ru': 'Русский', 'ky': 'Кыргызча', 'en': 'English', 'tr': 'Türkçe', 'uz': "O'zbekcha"};

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l;
    final pal = context.pal;
    final s = ref.watch(sessionProvider);
    final prefs = ref.watch(prefsProvider);
    final pending = ref.watch(pendingCountProvider).value ?? 0;
    Widget item(IconData icon, String title, {String? sub, Widget? trailing, VoidCallback? onTap}) => ListTile(
      leading: Container(
        width: 36,
        height: 36,
        decoration: BoxDecoration(color: pal.window, borderRadius: BorderRadius.circular(10)),
        child: Icon(icon, size: 20),
      ),
      title: Text(title, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14.5)),
      subtitle: sub == null ? null : Text(sub, style: TextStyle(fontSize: 12, color: pal.textSoft)),
      trailing: trailing,
      onTap: onTap,
    );
    return Scaffold(
      appBar: const ScreenTitle(''),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        children: [
          Row(
            children: [
              CircleAvatar(
                radius: 28,
                backgroundColor: T.accent,
                child: Text(
                  initials(s?.userName ?? '?').toUpperCase(),
                  style: const TextStyle(color: T.navy, fontWeight: FontWeight.w800, fontSize: 18),
                ),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(s?.userName ?? '', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                    Text(
                      [s?.companyName, s?.role, s?.cashboxName].whereType<String>().where((e) => e.isNotEmpty).join(' · '),
                      style: TextStyle(fontSize: 12.5, color: pal.textSoft),
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 16),
          Panel(
            padding: EdgeInsets.zero,
            child: Column(
              children: [
                item(
                  Icons.point_of_sale_rounded,
                  l.cashbox,
                  sub: s?.cashboxName,
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/cashbox'),
                ),
                // 2026-10-20, владелец: «исправь подключение к принтерам» — вместо «в следующем обновлении» настройка печати.
                item(
                  Icons.print_outlined,
                  l.receiptPrinter,
                  sub: l.prnSub,
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const PrinterSettingsScreen())),
                ),
                item(Icons.desktop_windows_outlined, l.linkWithKassa, sub: l.linkWithKassaSub),
              ],
            ),
          ),
          const SizedBox(height: 12),
          Panel(
            padding: EdgeInsets.zero,
            child: Column(
              children: [
                item(
                  Icons.language_rounded,
                  l.language,
                  trailing: Text(languages[prefs.locale.languageCode] ?? '', style: TextStyle(color: pal.textSoft)),
                  onTap: () async {
                    final code = await showModalBottomSheet<String>(
                      context: context,
                      builder: (c) => SafeArea(
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            for (final e in languages.entries)
                              ListTile(
                                title: Text(e.value),
                                trailing: e.key == prefs.locale.languageCode ? const Icon(Icons.check_rounded) : null,
                                onTap: () => Navigator.pop(c, e.key),
                              ),
                          ],
                        ),
                      ),
                    );
                    if (code != null) ref.read(prefsProvider.notifier).setLocale(code);
                  },
                ),
                // 13.10, макет С5: магазин одежды — размер и цвет при продаже.
                item(
                  Icons.checkroom_rounded,
                  l.clothingMode,
                  sub: l.clothingModeSub,
                  trailing: Switch(value: prefs.clothing, onChanged: (v) => ref.read(prefsProvider.notifier).setClothing(v)),
                ),
                // 13.10, макет ТМ3: светлая, тёмная или как в системе.
                // 14.10, владелец: «Нур Советник — настройка и добавление ИИ».
                item(
                  Icons.auto_awesome_rounded,
                  l.aiSettings,
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => const AiSettingsScreen())),
                ),
                item(Icons.dark_mode_outlined, l.appearance),
                // 14.10, владелец: «смена темы по времени» — четвёртый вариант и часы тёмной темы.
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                  child: Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                      for (final (v, label) in [('light', l.themeLight), ('dark', l.themeDark), ('system', l.themeSystem), ('auto', l.themeAuto)])
                        ChoiceChip(label: Text(label), selected: prefs.mode == v, onSelected: (_) => ref.read(prefsProvider.notifier).setMode(v)),
                    ],
                  ),
                ),
                if (prefs.auto)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                    child: Row(
                      children: [
                        Text(l.darkFrom),
                        const SizedBox(width: 6),
                        OutlinedButton(
                          onPressed: () async {
                            final t = await showTimePicker(
                              context: context,
                              initialTime: TimeOfDay(hour: prefs.darkFrom ~/ 60, minute: prefs.darkFrom % 60),
                            );
                            if (t != null) await ref.read(prefsProvider.notifier).setDarkHours(from: t.hour * 60 + t.minute);
                          },
                          child: Text(PrefsController.hhmm(prefs.darkFrom)),
                        ),
                        const SizedBox(width: 6),
                        Text(l.darkTo),
                        const SizedBox(width: 6),
                        OutlinedButton(
                          onPressed: () async {
                            final t = await showTimePicker(
                              context: context,
                              initialTime: TimeOfDay(hour: prefs.darkTo ~/ 60, minute: prefs.darkTo % 60),
                            );
                            if (t != null) await ref.read(prefsProvider.notifier).setDarkHours(to: t.hour * 60 + t.minute);
                          },
                          child: Text(PrefsController.hhmm(prefs.darkTo)),
                        ),
                      ],
                    ),
                  ),
                item(
                  Icons.wifi_off_rounded,
                  l.offlineMode,
                  sub: l.waitingToSend(pending),
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/sync'),
                ),
              ],
            ),
          ),
          const SizedBox(height: 24),
          OutlinedButton(
            style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(52), foregroundColor: T.danger),
            onPressed: () async {
              if (pending > 0) {
                final ok = await showDialog<bool>(
                  context: context,
                  builder: (c) => AlertDialog(
                    content: Text(l.logoutPending(pending)),
                    actions: [
                      TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
                      TextButton(
                        onPressed: () => Navigator.pop(c, true),
                        child: Text(l.logout, style: const TextStyle(color: T.danger)),
                      ),
                    ],
                  ),
                );
                if (ok != true) return;
              }
              await ref.read(sessionProvider.notifier).logout();
              if (context.mounted) context.go('/login');
            },
            child: Text(l.logout),
          ),
        ],
      ),
    );
  }
}

/// Синхронизация (О10): что ждёт отправки, что не принято сервером, «Отправить сейчас».
class SyncScreen extends ConsumerStatefulWidget {
  const SyncScreen({super.key});
  @override
  ConsumerState<SyncScreen> createState() => _SyncScreenState();
}

class _SyncScreenState extends ConsumerState<SyncScreen> {
  bool _busy = false;
  String? _catalogAt;

  @override
  void initState() {
    super.initState();
    ref.read(databaseProvider).getSetting('catalog.syncedAt').then((v) => mounted ? setState(() => _catalogAt = v) : null);
  }

  Future<void> _now() async {
    setState(() => _busy = true);
    await ref.read(syncProvider).runOnce(force: true);
    final at = await ref.read(databaseProvider).getSetting('catalog.syncedAt');
    if (mounted) {
      setState(() {
        _busy = false;
        _catalogAt = at;
      });
      final err = ref.read(syncProvider).lastError;
      if (err != null) toast(context, err, error: true);
    }
  }

  String _kind(BuildContext context, String k) => switch (k) {
    'sale' => context.l.kindSale,
    'receive' => context.l.receiving,
    'audit' => context.l.audit,
    _ => k,
  };

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final at = DateTime.tryParse(_catalogAt ?? '');
    return Scaffold(
      appBar: ScreenTitle(
        l.sync,
        subtitle: at == null
            ? null
            : l.catalogUpdated('${at.day.toString().padLeft(2, '0')}.${at.month.toString().padLeft(2, '0')} ${TimeOfDay.fromDateTime(at).format(context)}'),
      ),
      body: StreamBuilder<List<OutboxData>>(
        stream: ref.read(databaseProvider).watchOutbox(),
        builder: (c, snap) {
          final rows = (snap.data ?? []).take(100).toList();
          if (rows.isEmpty) {
            return Center(
              child: Text(l.nothingToSend, style: TextStyle(color: pal.textSoft)),
            );
          }
          return ListView.separated(
            padding: const EdgeInsets.all(16),
            itemCount: rows.length,
            separatorBuilder: (_, _) => const SizedBox(height: 8),
            itemBuilder: (c, i) {
              final r = rows[i];
              final (color, label) = switch (r.status) {
                'sent' => (T.success, l.sent),
                'failed' => (T.danger, l.notAccepted),
                _ => (T.warn, l.waitsForNetwork),
              };
              return Panel(
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            '${_kind(context, r.kind)}${r.serverNumber != null ? ' №${r.serverNumber}' : ''}',
                            style: const TextStyle(fontWeight: FontWeight.w700),
                          ),
                          Text(
                            r.summary,
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(fontSize: 12.5, color: pal.textSoft),
                          ),
                          if (r.status != 'sent' && r.lastError != null) Text(r.lastError!, style: TextStyle(fontSize: 12, color: color)),
                        ],
                      ),
                    ),
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.end,
                      children: [
                        if (r.kind == 'sale') Text('${money(r.total)} ${l.som}', style: const TextStyle(fontWeight: FontWeight.w800)),
                        Text(
                          label,
                          style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w700, color: color),
                        ),
                        if (r.status == 'failed')
                          TextButton(
                            onPressed: () async {
                              await ref.read(databaseProvider).retryFailed(r.id);
                              await _now();
                            },
                            child: Text(l.sendAgain),
                          ),
                      ],
                    ),
                  ],
                ),
              );
            },
          );
        },
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 6, 16, 12),
          child: FilledButton(
            onPressed: _busy ? null : _now,
            child: _busy ? const CircularProgressIndicator(color: T.navy) : Text(l.sendNow),
          ),
        ),
      ),
    );
  }
}
