import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/providers.dart';
import '../../core/sync/account_switch.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/util/ui.dart';

/// 2026-10-29 (владелец: «Ничего не должно остаться от старого аккаунта!!»): окно перед выходом и сменой аккаунта.
/// Пока в очереди есть неотправленные продажи или операции, выйти нельзя: либо «Отправить сейчас» (ждёт пустую очередь), либо —
/// если связи нет — «Выгрузить в файл и выйти», либо «Выйти БЕЗ отправки» (продажи будут потеряны; нужно ввести слово «УДАЛИТЬ»).
/// Возвращает true, когда выходить можно (очередь пуста, выгружена в файл или удаление подтверждено): дальше всё стирается.
///
/// [foreignOwner] — экран входа: вводят логин, не совпадающий с прошлым, а в очереди остались продажи прошлого аккаунта. Отправить
/// их без его входа нельзя, остаётся выгрузить или удалить (или вернуться и войти прежним аккаунтом).
Future<bool> confirmQueueResolved(BuildContext context, WidgetRef ref, {String? foreignOwner}) async {
  final db = ref.read(databaseProvider);
  final QueueSummary q = foreignOwner == null
      ? await AccountSwitch.queueSummary(db, owner: OutboxMeta.ownerOf(ref.read(sessionProvider)))
      : await AccountSwitch.foreignQueue(db, foreignOwner);
  if (q.isEmpty) return true;
  if (!context.mounted) return false;
  final ok = await showDialog<bool>(
    context: context,
    barrierDismissible: false,
    builder: (c) => _QueueGuardDialog(first: q, foreignOwner: foreignOwner),
  );
  return ok == true;
}

class _QueueGuardDialog extends ConsumerStatefulWidget {
  final QueueSummary first;
  final String? foreignOwner;
  const _QueueGuardDialog({required this.first, this.foreignOwner});
  @override
  ConsumerState<_QueueGuardDialog> createState() => _QueueGuardDialogState();
}

class _QueueGuardDialogState extends ConsumerState<_QueueGuardDialog> {
  late QueueSummary _q = widget.first;
  bool _busy = false;
  String? _message;

  String get _me => OutboxMeta.ownerOf(ref.read(sessionProvider));
  Future<QueueSummary> _recount() {
    final db = ref.read(databaseProvider);
    return widget.foreignOwner == null ? AccountSwitch.queueSummary(db, owner: _me) : AccountSwitch.foreignQueue(db, widget.foreignOwner!);
  }

  Future<void> _send() async {
    final l = context.l;
    setState(() {
      _busy = true;
      _message = null;
    });
    final sync = ref.read(syncProvider);
    final db = ref.read(databaseProvider);
    try {
      // не принятые сервером записи — повторить; затем очередь по порядку до конца
      final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
      for (final r in await db.select(db.outbox).get()) {
        if (r.status == 'failed' && OutboxMeta.belongsTo(r, _me, legacyOwner: legacy)) await db.retryFailed(r.id);
      }
      sync.authPaused = false;
      await sync.runQueue();
    } catch (_) {
      // остаток и причину покажем ниже
    }
    final left = await _recount();
    if (!mounted) return;
    if (left.isEmpty) {
      Navigator.pop(context, true);
      return;
    }
    final offline = ref.read(apiProvider).link.value.offline;
    final err = sync.lastError;
    setState(() {
      _busy = false;
      _q = left;
      _message = offline || err == null ? l.guardWaitConnection : l.guardSendLeft(left.count, err);
    });
  }

  Future<void> _export() async {
    final l = context.l;
    final go = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.guardExport),
        content: Text(l.guardExportWarn),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          TextButton(onPressed: () => Navigator.pop(c, true), child: Text(l.guardExport)),
        ],
      ),
    );
    if (go != true || !mounted) return;
    setState(() => _busy = true);
    try {
      final owner = widget.foreignOwner == null ? _me : (_q.owners.isNotEmpty ? _q.owners.first : 'account');
      final file = await AccountSwitch.exportQueue(ref.read(databaseProvider), owner: owner);
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        barrierDismissible: false,
        builder: (c) => AlertDialog(
          content: Text(l.guardExported(file.path)),
          actions: [TextButton(onPressed: () => Navigator.pop(c), child: Text(l.done))],
        ),
      );
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _message = l.guardExportFailed('$e');
      });
    }
  }

  Future<void> _discard() async {
    final ok = await showDialog<bool>(context: context, builder: (d) => const _DiscardDialog());
    if (ok == true && mounted) Navigator.pop(context, true);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final foreign = widget.foreignOwner != null;
    return PopScope(
      canPop: false,
      child: AlertDialog(
        title: Row(children: [Icon(Icons.cloud_off_rounded, color: pal.warn), const SizedBox(width: 8), Expanded(child: Text(l.logout))]),
        content: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(foreign ? l.guardForeign(_q.owners.join(', '), _q.count, money(_q.sum)) : l.guardUnsent(_q.count, money(_q.sum))),
              if (_busy) ...[
                const SizedBox(height: 12),
                const LinearProgressIndicator(),
                const SizedBox(height: 6),
                Text(l.guardSending, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
              ],
              if (_message != null) ...[
                const SizedBox(height: 12),
                Text(_message!, style: TextStyle(color: pal.warn, fontSize: 13)),
              ],
            ],
          ),
        ),
        actionsOverflowDirection: VerticalDirection.down,
        actionsOverflowButtonSpacing: 4,
        actions: [
          if (!foreign) FilledButton(onPressed: _busy ? null : _send, child: Text(l.logoutSendNow)),
          OutlinedButton(onPressed: _busy ? null : _export, child: Text(l.guardExport)),
          TextButton(
            onPressed: _busy ? null : _discard,
            child: Text(l.guardDiscard, style: TextStyle(color: _busy ? null : pal.danger)),
          ),
          TextButton(onPressed: _busy ? null : () => Navigator.pop(context, false), child: Text(l.cancel)),
        ],
      ),
    );
  }
}

/// Подтверждение удаления: кнопка включается только после ввода слова (на языке интерфейса или «УДАЛИТЬ»).
class _DiscardDialog extends StatefulWidget {
  const _DiscardDialog();
  @override
  State<_DiscardDialog> createState() => _DiscardDialogState();
}

class _DiscardDialogState extends State<_DiscardDialog> {
  final _c = TextEditingController();

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final word = l.guardDiscardWord;
    final typed = _c.text.trim().toUpperCase();
    final match = typed == word.toUpperCase() || typed == 'УДАЛИТЬ';
    return AlertDialog(
      title: Text(l.guardDiscard, style: TextStyle(color: pal.danger)),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l.guardDiscardWarn(word)),
          const SizedBox(height: 12),
          TextField(
            controller: _c,
            autofocus: true,
            autocorrect: false,
            enableSuggestions: false,
            decoration: InputDecoration(labelText: l.guardDiscardField),
            onChanged: (_) => setState(() {}),
          ),
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(context, false), child: Text(l.cancel)),
        TextButton(
          onPressed: match ? () => Navigator.pop(context, true) : null,
          child: Text(l.guardDiscard, style: TextStyle(color: match ? pal.danger : null)),
        ),
      ],
    );
  }
}
