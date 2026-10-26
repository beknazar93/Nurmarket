// 2026-10-27, владелец: «в мобилке дополнительного ключа нет — добавь!». Экран «Ключи Gemini»: до 10 ключей из разных аккаунтов
// Google — как окно «Ключи Gemini» на кассе и в программе владельца. Ключи используются по порядку: первый рабочий; когда у
// ключа кончается лимит, ИИ сам берёт следующий (vision.dart). Список общий с кассой (секреты TelegramAiKey + TelegramAiKeysExtra).
// Менять список могут владелец и администратор с правом настроек; сотрудник с правом на ИИ видит ключи скрытыми и только пользуется.
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/ai/ai_access.dart';
import '../../core/ai/gemini_keys.dart';
import '../../core/ai/vision.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/audit.dart';
import '../../core/util/ui.dart';

class GeminiKeysScreen extends ConsumerStatefulWidget {
  const GeminiKeysScreen({super.key});
  @override
  ConsumerState<GeminiKeysScreen> createState() => _GeminiKeysScreenState();
}

class _Row {
  GeminiKeyEntry entry;
  final TextEditingController label;
  bool shown = false;
  bool checking = false;
  String? note; // результат «Проверить» (ошибка), пусто — без пометки
  _Row(this.entry) : label = TextEditingController(text: entry.label);
  GeminiKeyEntry get current => entry.copyWith(label: label.text);
}

class _GeminiKeysScreenState extends ConsumerState<GeminiKeysScreen> {
  final List<_Row> _rows = [];
  final _newKey = TextEditingController();
  final _newLabel = TextEditingController();
  bool _loaded = false;
  bool _saving = false;
  bool _dirty = false;
  String? _addError;

  bool get _canEdit => AiAccess.canManage(ref.read(sessionProvider));

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    for (final r in _rows) {
      r.label.dispose();
    }
    _newKey.dispose();
    _newLabel.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    List<GeminiKeyEntry> list;
    try {
      list = await ref.read(visionProvider).geminiEntries(refresh: true);
    } on VisionException catch (e) {
      if (mounted) toast(context, e.message, error: true);
      list = const [];
    }
    if (!mounted) return;
    setState(() {
      _rows
        ..clear()
        ..addAll(list.map(_Row.new));
      _dirty = false;
      _loaded = true;
    });
  }

  List<GeminiKeyEntry> get _entries => [for (final r in _rows) r.current];

  void _touch() => setState(() => _dirty = true);

  void _add() {
    final l = context.l;
    final k = GeminiKeys.clean(_newKey.text);
    if (k.isEmpty) return;
    if (_rows.length >= GeminiKeys.maxKeys) {
      setState(() => _addError = l.gkMax);
      return;
    }
    if (_rows.any((r) => r.entry.key == k)) {
      setState(() => _addError = l.gkDuplicate);
      return;
    }
    final label = _newLabel.text.trim().isEmpty
        ? l.gkLabelDefault(_rows.length + 1)
        : _newLabel.text.trim();
    setState(() {
      _rows.add(_Row(GeminiKeyEntry(label: label, key: k)));
      _newKey.clear();
      _newLabel.clear();
      _addError = null;
      _dirty = true;
    });
  }

  void _move(int i, int d) {
    final j = i + d;
    if (j < 0 || j >= _rows.length) return;
    setState(() {
      final r = _rows.removeAt(i);
      _rows.insert(j, r);
      _dirty = true;
    });
  }

  Future<void> _delete(int i) async {
    final l = context.l;
    final r = _rows[i];
    final name = r.label.text.trim().isEmpty
        ? l.gkLabelDefault(i + 1)
        : r.label.text.trim();
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        content: Text(l.gkDeleteAsk(name, r.entry.tail)),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(c, false),
            child: Text(l.cancel),
          ),
          TextButton(
            onPressed: () => Navigator.pop(c, true),
            child: Text(l.gkDelete, style: TextStyle(color: T.danger)),
          ),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    setState(() {
      _rows.removeAt(i).label.dispose();
      _dirty = true;
    });
  }

  Future<void> _check(_Row r) async {
    final l = context.l;
    setState(() {
      r.checking = true;
      r.note = null;
    });
    final res = await ref.read(visionProvider).checkGeminiKey(r.entry.key);
    if (!mounted) return;
    setState(() {
      r.checking = false;
      r.note = switch (res.state) {
        GeminiKeyState.working => '',
        GeminiKeyState.exhausted => null,
        GeminiKeyState.invalid =>
          res.error == null || res.error!.isEmpty
              ? l.gkStateInvalid
              : res.error,
        _ => l.gkCheckFail(res.error ?? ''),
      };
    });
  }

  Future<void> _save() async {
    final l = context.l;
    setState(() => _saving = true);
    try {
      final cloud = await ref.read(visionProvider).saveGeminiKeys(_entries);
      if (!mounted) return;
      Audit.log(
        'settings',
        l.gkTitle,
      ); // в журнал — только название, без ключей
      toast(
        context,
        cloud ? l.aiKeysSaved : l.aiKeysSavedPhoneOnly,
        error: !cloud,
      );
      await _load();
    } on VisionException catch (e) {
      if (mounted) toast(context, e.message, error: true);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  String _mask(String k) => k.length <= 8
      ? '••••'
      : '${k.substring(0, 4)}…${k.substring(k.length - 4)}';

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final vision = ref.read(visionProvider);
    final edit = _canEdit;
    final (ok, total) = vision.geminiCounts(_entries);
    return Scaffold(
      appBar: ScreenTitle(l.gkTitle),
      body: !_loaded
          ? const Center(child: CircularProgressIndicator())
          : ListView(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
              children: [
                Text(
                  l.gkHint,
                  style: TextStyle(color: pal.textSoft, fontSize: 12.5),
                ),
                const SizedBox(height: 8),
                // честное предупреждение — коротко
                Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: pal.accentSoft,
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Icon(
                        Icons.warning_amber_rounded,
                        size: 20,
                        color: pal.action,
                      ),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(
                          l.gkWarn,
                          style: const TextStyle(fontSize: 12.5),
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(height: 12),
                if (!edit) ...[
                  Text(
                    l.gkViewOnly,
                    style: TextStyle(color: pal.textSoft, fontSize: 12.5),
                  ),
                  const SizedBox(height: 8),
                ],
                Text(
                  total == 0 ? l.aiNoKeyShort : l.gkWorkingCount(ok, total),
                  style: TextStyle(
                    fontWeight: FontWeight.w800,
                    fontSize: 15,
                    color: total == 0
                        ? pal.textSoft
                        : (ok == 0 ? T.danger : T.success),
                  ),
                ),
                const SizedBox(height: 8),
                if (_rows.isEmpty)
                  Text(l.gkEmpty, style: TextStyle(color: pal.textSoft)),
                for (var i = 0; i < _rows.length; i++) ...[
                  _rowCard(i, edit),
                  const SizedBox(height: 8),
                ],
                if (edit) ...[
                  const SizedBox(height: 4),
                  Panel(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Text(
                          l.gkAdd,
                          style: const TextStyle(
                            fontWeight: FontWeight.w800,
                            fontSize: 15,
                          ),
                        ),
                        const SizedBox(height: 8),
                        TextField(
                          controller: _newKey,
                          obscureText: true,
                          autocorrect: false,
                          enableSuggestions: false,
                          decoration: InputDecoration(
                            hintText: l.gkPasteKey,
                            isDense: true,
                          ),
                          onChanged: (_) => setState(() => _addError = null),
                        ),
                        const SizedBox(height: 8),
                        TextField(
                          controller: _newLabel,
                          decoration: InputDecoration(
                            hintText: l.gkLabelHint,
                            isDense: true,
                          ),
                        ),
                        if (_addError != null)
                          Padding(
                            padding: const EdgeInsets.only(top: 6),
                            child: Text(
                              _addError!,
                              style: TextStyle(
                                color: T.danger,
                                fontWeight: FontWeight.w600,
                                fontSize: 12.5,
                              ),
                            ),
                          ),
                        const SizedBox(height: 8),
                        Wrap(
                          spacing: 6,
                          children: [
                            ActionChip(
                              avatar: const Icon(
                                Icons.open_in_new_rounded,
                                size: 16,
                              ),
                              label: Text(l.aiGetKey),
                              onPressed: () => launchUrl(
                                Uri.parse('https://aistudio.google.com/apikey'),
                                mode: LaunchMode.externalApplication,
                              ),
                            ),
                            ActionChip(
                              avatar: const Icon(Icons.add_rounded, size: 16),
                              label: Text(l.gkAddBtn),
                              onPressed: _newKey.text.trim().isEmpty
                                  ? null
                                  : _add,
                            ),
                          ],
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 12),
                  FilledButton(
                    style: FilledButton.styleFrom(
                      minimumSize: const Size.fromHeight(52),
                    ),
                    onPressed: _saving || !_dirty ? null : _save,
                    child: _saving
                        ? const SizedBox(
                            width: 22,
                            height: 22,
                            child: CircularProgressIndicator(strokeWidth: 2.5),
                          )
                        : Text(l.save),
                  ),
                ],
              ],
            ),
    );
  }

  Widget _rowCard(int i, bool edit) {
    final l = context.l;
    final pal = context.pal;
    final r = _rows[i];
    final vision = ref.read(visionProvider);
    final (state, until) = vision.geminiStateOf(r.current);
    final (text, color) = switch (state) {
      GeminiKeyState.working => (l.gkStateWorking, T.success),
      GeminiKeyState.exhausted => (
        l.gkStateExhausted(until == null ? '' : VisionService.hm(until)),
        T.danger,
      ),
      GeminiKeyState.invalid => (l.gkStateInvalid, T.danger),
      GeminiKeyState.disabled => (l.gkStateDisabled, pal.textSoft),
      GeminiKeyState.unchecked => (l.gkStateUnchecked, pal.textSoft),
    };
    return Panel(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Text(
                '${i + 1}.',
                style: const TextStyle(
                  fontWeight: FontWeight.w800,
                  fontSize: 15,
                ),
              ),
              const SizedBox(width: 6),
              Expanded(
                child: edit
                    ? TextField(
                        controller: r.label,
                        decoration: InputDecoration(
                          hintText: l.gkLabelDefault(i + 1),
                          isDense: true,
                          border: InputBorder.none,
                        ),
                        style: const TextStyle(
                          fontWeight: FontWeight.w800,
                          fontSize: 15,
                        ),
                        onChanged: (_) => _touch(),
                      )
                    : Text(
                        r.label.text.isEmpty
                            ? l.gkLabelDefault(i + 1)
                            : r.label.text,
                        style: const TextStyle(
                          fontWeight: FontWeight.w800,
                          fontSize: 15,
                        ),
                      ),
              ),
              Text(
                text,
                style: TextStyle(
                  color: color,
                  fontWeight: FontWeight.w700,
                  fontSize: 12.5,
                ),
              ),
            ],
          ),
          const SizedBox(height: 4),
          // ключ целиком видно только тому, кто его меняет, и только по кнопке «Показать»
          Text(
            r.shown && edit ? r.entry.key : _mask(r.entry.key),
            style: TextStyle(
              color: pal.textSoft,
              fontSize: 13,
              fontFamily: 'monospace',
            ),
          ),
          if (r.note != null)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text(
                r.note!.isEmpty ? l.gkCheckOk : r.note!,
                style: TextStyle(
                  color: r.note!.isEmpty ? T.success : T.danger,
                  fontWeight: FontWeight.w600,
                  fontSize: 12.5,
                ),
              ),
            ),
          const SizedBox(height: 4),
          Wrap(
            spacing: 4,
            runSpacing: 2,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              ActionChip(
                avatar: r.checking
                    ? const SizedBox(
                        width: 14,
                        height: 14,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.bolt_rounded, size: 16),
                label: Text(l.aiTest),
                onPressed: r.checking ? null : () => _check(r),
              ),
              if (edit) ...[
                ActionChip(
                  avatar: Icon(
                    r.shown
                        ? Icons.visibility_off_rounded
                        : Icons.visibility_rounded,
                    size: 16,
                  ),
                  label: Text(r.shown ? l.gkHide : l.gkShow),
                  onPressed: () => setState(() => r.shown = !r.shown),
                ),
                IconButton(
                  tooltip: l.gkMoveUp,
                  visualDensity: VisualDensity.compact,
                  icon: const Icon(Icons.arrow_upward_rounded, size: 20),
                  onPressed: i == 0 ? null : () => _move(i, -1),
                ),
                IconButton(
                  tooltip: l.gkMoveDown,
                  visualDensity: VisualDensity.compact,
                  icon: const Icon(Icons.arrow_downward_rounded, size: 20),
                  onPressed: i == _rows.length - 1 ? null : () => _move(i, 1),
                ),
                IconButton(
                  tooltip: l.gkDelete,
                  visualDensity: VisualDensity.compact,
                  icon: Icon(
                    Icons.delete_outline_rounded,
                    size: 20,
                    color: T.danger,
                  ),
                  onPressed: () => _delete(i),
                ),
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(l.gkEnabled, style: const TextStyle(fontSize: 13)),
                    Switch(
                      value: r.entry.enabled,
                      onChanged: (v) => setState(() {
                        r.entry = r.entry.copyWith(enabled: v);
                        _dirty = true;
                      }),
                    ),
                  ],
                ),
              ],
            ],
          ),
        ],
      ),
    );
  }
}
