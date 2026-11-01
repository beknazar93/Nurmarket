import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../providers.dart';
import '../theme/tokens.dart';
import '../util/ui.dart';
import 'app_update_service.dart';

/// 2026-11-01 (обновление через GitHub): баннер «Доступна версия X — Обновить», окно «Что нового» с ходом скачивания и
/// окно про разрешение «Установка из неизвестных источников». Не-Android — ничего не показывается.

/// Баннер на главной и в профиле. [hideWhenSelling] — на главной не мешаем, пока в чеке есть позиции (идёт продажа).
class UpdateBanner extends ConsumerWidget {
  final EdgeInsetsGeometry margin;
  final bool hideWhenSelling;
  const UpdateBanner({super.key, this.margin = EdgeInsets.zero, this.hideWhenSelling = false});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (!AppUpdateController.supported) return const SizedBox.shrink();
    final st = ref.watch(appUpdateProvider);
    final rel = st.available;
    if (rel == null || st.snoozed) return const SizedBox.shrink();
    if (hideWhenSelling && !ref.watch(cartProvider).isEmpty) return const SizedBox.shrink();
    final l = context.l;
    final p = context.pal;
    return Padding(
      padding: margin,
      child: Panel(
        color: p.accentSoft,
        padding: const EdgeInsets.fromLTRB(12, 10, 4, 10),
        onTap: () => showUpdateDialog(context),
        child: Row(
          children: [
            const Icon(Icons.system_update_alt_rounded, color: T.accent, size: 22),
            const SizedBox(width: 10),
            Expanded(
              child: Text(l.updAvailable(rel.version), style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 13.5)),
            ),
            TextButton(onPressed: () => showUpdateDialog(context), child: Text(l.updUpdate)),
            IconButton(
              tooltip: l.updLater,
              visualDensity: VisualDensity.compact,
              icon: Icon(Icons.close_rounded, size: 18, color: p.textSoft),
              onPressed: () => ref.read(appUpdateProvider.notifier).snooze(),
            ),
          ],
        ),
      ),
    );
  }
}

/// Подпись под строкой «Версия приложения» в профиле.
String updateRowSubtitle(BuildContext context, UpdateState st) {
  final l = context.l;
  if (st.checking) return l.updChecking;
  final rel = st.available;
  return rel != null ? l.updAvailable(rel.version) : l.updVersionIs(AppUpdateController.currentVersion);
}

/// Кнопка «Проверить обновления»: нашлась версия — окно «Что нового»; нет — короткое сообщение.
Future<void> checkForUpdatesManually(BuildContext context, WidgetRef ref) async {
  final ctl = ref.read(appUpdateProvider.notifier);
  final res = await ctl.check(manual: true);
  if (!context.mounted) return;
  switch (res) {
    case UpdateCheck.found:
      await showUpdateDialog(context);
    case UpdateCheck.upToDate:
      toast(context, context.l.updUpToDate);
    case UpdateCheck.failed:
      toast(context, context.l.updCheckFailed, error: true);
  }
}

Future<void> showUpdateDialog(BuildContext context) => showDialog<void>(context: context, barrierDismissible: false, builder: (_) => const _UpdateDialog());

class _UpdateDialog extends ConsumerWidget {
  const _UpdateDialog();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l = context.l;
    final p = context.pal;
    final st = ref.watch(appUpdateProvider);
    final ctl = ref.read(appUpdateProvider.notifier);
    final rel = st.available;
    final busy = st.progress != null;
    return AlertDialog(
      title: Text(rel == null ? l.updVersionTitle : l.updAvailable(rel.version)),
      content: SizedBox(
        width: double.maxFinite,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(l.updWhatsNew, style: const TextStyle(fontWeight: FontWeight.w700)),
              const SizedBox(height: 6),
              Text(rel == null || rel.notes.isEmpty ? l.updNoNotes : rel.notes, style: TextStyle(fontSize: 13, color: p.textSoft)),
              if (busy) ...[
                const SizedBox(height: 16),
                LinearProgressIndicator(value: st.progress),
                const SizedBox(height: 6),
                Text(l.updDownloading(((st.progress ?? 0) * 100).round()), style: const TextStyle(fontSize: 12.5)),
              ],
            ],
          ),
        ),
      ),
      actions: [
        if (busy)
          TextButton(onPressed: ctl.cancelDownload, child: Text(l.updCancel))
        else ...[
          TextButton(
            onPressed: () {
              ctl.snooze();
              Navigator.pop(context);
            },
            child: Text(l.updLater),
          ),
          FilledButton(
            onPressed: rel == null ? null : () => _go(context, ref),
            child: Text(l.updUpdate),
          ),
        ],
      ],
    );
  }

  Future<void> _go(BuildContext context, WidgetRef ref) async {
    final ctl = ref.read(appUpdateProvider.notifier);
    final nav = Navigator.of(context);
    final host = nav.context;
    final res = await ctl.start();
    if (!context.mounted) return;
    switch (res) {
      case UpdateStart.started:
        nav.pop();
      case UpdateStart.needPermission:
        nav.pop();
        if (host.mounted) await _askPermission(host, ctl);
      case UpdateStart.failed:
        toast(context, context.l.updDownloadFailed, error: true);
      case UpdateStart.cancelled:
        break;
    }
  }

  /// Нет разрешения «Установка из неизвестных источников»: объяснить и открыть настройки приложения.
  static Future<void> _askPermission(BuildContext context, AppUpdateController ctl) async {
    final l = context.l;
    final open = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.updPermTitle),
        content: Text(l.updPermBody),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.updCancel)),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.updOpenSettings)),
        ],
      ),
    );
    if (open == true) await ctl.openInstallSettings();
  }
}
