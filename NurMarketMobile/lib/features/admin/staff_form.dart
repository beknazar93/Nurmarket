// 2026-10-28 (сотрудники): карточка сотрудника — личные данные, должность, доступы; создание, правка, увольнение/восстановление,
// сброс пароля. Поля берутся только те, что сервер вернул (feature-detect по ответу api/users/employees/), правка уходит PATCH-ом
// только изменённых полей — чужие данные и неизвестные поля сервера не затираются.
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_client.dart';
import '../../core/api/session_store.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import 'admin_more.dart';

final _emailRe = RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s.]{2,}$');

/// Почта: полностью, с «@» и доменом. Пустая — «заполните» (проверяет вызывающий).
bool isValidStaffEmail(String s) => _emailRe.hasMatch(s.trim());

/// Телефон: разрешены цифры, пробелы, скобки, дефис и «+» в начале; цифр от 9 до 15. Пустой телефон — допустим.
bool isValidStaffPhone(String s) {
  final t = s.trim();
  if (t.isEmpty) return true;
  if (!RegExp(r'^\+?[\d\s()\-]+$').hasMatch(t)) return false;
  final n = t.replaceAll(RegExp(r'\D'), '').length;
  return n >= 9 && n <= 15;
}

/// Телефон для сервера: «+» (если был) и цифры, без пробелов и скобок.
String normalizeStaffPhone(String s) {
  final t = s.trim();
  if (t.isEmpty) return '';
  return (t.startsWith('+') ? '+' : '') + t.replaceAll(RegExp(r'\D'), '');
}

/// Дата для сервера: ГГГГ-ММ-ДД (из значения сервера берётся только дата, без времени).
String? staffIsoDate(Object? v) {
  final s = '${v ?? ''}'.trim();
  if (s.length < 10) return null;
  final d = DateTime.tryParse(s.substring(0, 10));
  if (d == null) return null;
  final iso = '${d.year.toString().padLeft(4, '0')}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
  return iso == s.substring(0, 10) ? iso : null; // 2026-13-45 парсер «переносит» на другой год — это не дата
}

String _showDate(String iso) {
  final d = DateTime.tryParse(iso);
  return d == null ? iso : '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}.${d.year}';
}

/// Дополнительные поля карточки: показываются, только если сервер их вернул (или вернул у других сотрудников — при создании).
const _extraText = ['middle_name', 'address', 'comment'];
const _extraDate = ['birth_date', 'hire_date'];

class StaffFormScreen extends ConsumerStatefulWidget {
  /// null — новый сотрудник.
  final Employee? employee;

  /// Какие поля сервер вообще отдаёт (по списку) — для формы создания.
  final Set<String> knownKeys;
  const StaffFormScreen({super.key, this.employee, this.knownKeys = const {}});

  @override
  ConsumerState<StaffFormScreen> createState() => _StaffFormScreenState();
}

class _StaffFormScreenState extends ConsumerState<StaffFormScreen> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _first, _last, _email, _phone, _track;
  final Map<String, TextEditingController> _extra = {};
  final Map<String, String> _dates = {}; // ключ → ГГГГ-ММ-ДД ('' — не задана)
  late final Map<String, bool> _rights;
  List<({String id, String name})> _roles = const [];
  String _roleId = '';
  bool _busy = false;
  bool _dirty = false;

  Employee? get _e => widget.employee;
  bool get _isNew => _e == null;

  /// Нужное поле показываем, если сервер его вернул у этого сотрудника (при создании — у кого-то из списка).
  bool _shows(String key) => _isNew ? widget.knownKeys.contains(key) : _e!.has(key);

  String get _phoneKey => (_e?.has('phone_number') ?? true) || !(_e?.has('phone') ?? false) ? 'phone_number' : 'phone';

  @override
  void initState() {
    super.initState();
    final e = _e;
    _first = TextEditingController(text: e?.firstName ?? '');
    _last = TextEditingController(text: e?.lastName ?? '');
    _email = TextEditingController(text: e?.email ?? '');
    _phone = TextEditingController(text: e?.phone ?? '');
    _track = TextEditingController(text: e?.trackNumber ?? '');
    for (final k in _extraText) {
      _extra[k] = TextEditingController(text: e?.field(k) ?? '');
    }
    for (final k in _extraDate) {
      _dates[k] = staffIsoDate(e?.raw[k]) ?? '';
    }
    _rights = e != null
        ? Map<String, bool>.from(e.rights)
        : {
            for (final k in staffRights.keys) k: false,
            for (final k in staffRightsOptional.keys) if (widget.knownKeys.contains(k)) k: true, // can_use_pos у всех true (Б-16)
          };
    _roleId = e?.roleId ?? '';
    _loadRoles();
  }

  @override
  void dispose() {
    for (final c in [_first, _last, _email, _phone, _track, ..._extra.values]) {
      c.dispose();
    }
    super.dispose();
  }

  Future<void> _loadRoles() async {
    try {
      final roles = await ref.read(staffRepoProvider).roles();
      if (mounted) setState(() => _roles = roles);
    } catch (_) {
      // без списка должностей форма работает: должность просто не выбирается
    }
  }

  bool get _canPersonal => ref.read(sessionProvider)?.canEditStaff ?? false;
  bool get _canRights {
    final s = ref.read(sessionProvider);
    return s != null && (s.isAdmin || s.can(Session.viewSettings));
  }

  bool get _isSelf => !_isNew && isSelfEmployee(_e!, ref.read(sessionProvider)?.userId);

  String _label(AppLocalizations l, String key) => switch (key) {
    'middle_name' => l.empMiddleName,
    'address' => l.empAddress,
    'comment' => l.empComment,
    'birth_date' => l.empBirth,
    _ => l.empHire,
  };

  Future<void> _pickDate(String key) async {
    final now = DateTime.now();
    final cur = DateTime.tryParse(_dates[key] ?? '') ?? now;
    final d = await showDatePicker(
      context: context,
      initialDate: cur,
      firstDate: DateTime(1930),
      lastDate: DateTime(now.year + 1, 12, 31),
    );
    if (d != null && mounted) {
      setState(() {
        _dates[key] = staffIsoDate(d.toIso8601String())!;
        _dirty = true;
      });
    }
  }

  // ---------------------------------------------------------------- сохранение

  Map<String, dynamic> _createBody() {
    final body = <String, dynamic>{
      'email': _email.text.trim(),
      'first_name': _first.text.trim(),
      'last_name': _last.text.trim(),
      'phone_number': normalizeStaffPhone(_phone.text),
      'track_number': _track.text.trim(),
      if (_roleId.isNotEmpty) 'custom_role': _roleId,
    };
    for (final k in _extraText) {
      if (_shows(k) && _extra[k]!.text.trim().isNotEmpty) body[k] = _extra[k]!.text.trim();
    }
    for (final k in _extraDate) {
      if (_shows(k) && (_dates[k] ?? '').isNotEmpty) body[k] = _dates[k];
    }
    body.addAll(_rights);
    return body;
  }

  /// Только изменённые поля: чужие и неизвестные поля сервера в запрос не попадают.
  Map<String, dynamic> _changes() {
    final e = _e!;
    final out = <String, dynamic>{};
    if (_canPersonal) {
      void text(String key, String now, String before) {
        if (now.trim() != before.trim()) out[key] = now.trim();
      }

      if (_first.text.trim() != e.firstName) out['first_name'] = _first.text.trim();
      if (e.has('last_name') || _last.text.trim().isNotEmpty) text('last_name', _last.text, e.lastName);
      if (_shows(_phoneKey) || _phone.text.trim().isNotEmpty) {
        final now = normalizeStaffPhone(_phone.text);
        if (now != normalizeStaffPhone(e.phone)) out[_phoneKey] = now;
      }
      if (e.has('track_number')) text('track_number', _track.text, e.trackNumber);
      for (final k in _extraText) {
        if (e.has(k)) text(k, _extra[k]!.text, e.field(k));
      }
      for (final k in _extraDate) {
        if (!e.has(k)) continue;
        final now = _dates[k] ?? '';
        if (now != (staffIsoDate(e.raw[k]) ?? '')) out[k] = now.isEmpty ? null : now;
      }
      if (_roleId.isNotEmpty && _roleId != e.roleId) out['custom_role'] = _roleId;
    }
    if (_canRights) {
      final next = guardSelfRights(e.rights, _rights, self: _isSelf);
      for (final k in next.keys) {
        if ((e.rights[k] ?? false) != next[k]) out[k] = next[k];
      }
    }
    return out;
  }

  Future<void> _save() async {
    if (_busy) return;
    if (!(_formKey.currentState?.validate() ?? false)) return;
    final l = context.l;
    final repo = ref.read(staffRepoProvider);
    setState(() => _busy = true);
    try {
      if (_isNew) {
        final r = await repo.create(_createBody());
        if (!mounted) return;
        toast(context, l.empCreated);
        setState(() => _busy = false); // крутилка не должна вертеться под окном с паролем
        if (r.password != null) await _showPassword(r.employee.email.isNotEmpty ? r.employee.email : _email.text.trim(), r.password!);
        if (mounted) Navigator.pop(context, true);
      } else {
        final diff = _changes();
        if (diff.isEmpty) {
          toast(context, l.empNothingChanged);
          return;
        }
        await repo.update(_e!.id, diff);
        if (!mounted) return;
        if (_isSelf) ref.read(sessionProvider.notifier).refreshProfile(); // своё имя / доступы изменились
        toast(context, l.empSaved);
        Navigator.pop(context, true);
      }
    } catch (e) {
      if (mounted) toast(context, staffErrorText(l, e), error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _showPassword(String login, String password) async {
    final l = context.l;
    await showDialog<void>(
      context: context,
      barrierDismissible: false,
      builder: (c) => AlertDialog(
        title: Text(l.empPasswordTitle),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(l.empPasswordNote(login)),
            const SizedBox(height: 12),
            SelectableText(password, key: const Key('staffPassword'), style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () async {
              await Clipboard.setData(ClipboardData(text: password));
              if (c.mounted) toast(c, l.empCopied);
            },
            child: Text(l.empCopy),
          ),
          FilledButton(onPressed: () => Navigator.pop(c), child: Text(l.done)),
        ],
      ),
    );
  }

  Future<bool> _confirm(String text, String ok) async {
    final l = context.l;
    return await showDialog<bool>(
          context: context,
          builder: (c) => AlertDialog(
            content: Text(text),
            actions: [
              TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
              FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(ok)),
            ],
          ),
        ) ??
        false;
  }

  /// Уволить: если сервер отдаёт «работает» — снимаем флаг (запись и продажи остаются), иначе DELETE с проверкой по списку.
  Future<void> _setDismissed(bool dismiss) async {
    if (_busy) return;
    final l = context.l;
    final e = _e!;
    if (!await _confirm(dismiss ? l.empDismissAsk(e.name) : l.empRestoreAsk(e.name), dismiss ? l.empDismiss : l.empRestore)) return;
    setState(() => _busy = true);
    try {
      final repo = ref.read(staffRepoProvider);
      final key = e.activeKey;
      if (key != null) {
        await repo.update(e.id, {key: !dismiss});
      } else if (dismiss) {
        await repo.remove(e.id);
      }
      if (!mounted) return;
      toast(context, dismiss ? l.empDismissedDone : l.empRestoredDone);
      Navigator.pop(context, true);
    } catch (x) {
      if (mounted) toast(context, staffErrorText(l, x), error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _resetPassword() async {
    if (_busy) return;
    final l = context.l;
    final e = _e!;
    if (!await _confirm(l.empResetAsk(e.name), l.empResetPwd)) return;
    setState(() => _busy = true);
    try {
      final pwd = await ref.read(staffRepoProvider).resetPassword(e.id);
      if (mounted) await _showPassword(e.email, pwd);
    } catch (x) {
      // адрес сброса пароля у сервера ещё не появился (Б-16) — 404 здесь значит «нет такой функции», а не «нет сотрудника»
      if (mounted) toast(context, x is ApiException && x.status == 404 ? l.empFeatureMissing : staffErrorText(l, x), error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  // ---------------------------------------------------------------- вид

  Widget _field(TextEditingController c, String label, {bool enabled = true, TextInputType? type, String? Function(String?)? validator, int? max, TextCapitalization cap = TextCapitalization.none, Key? key}) =>
      Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: TextFormField(
          key: key,
          controller: c,
          enabled: enabled && !_busy,
          keyboardType: type,
          textCapitalization: cap,
          maxLength: max,
          maxLengthEnforcement: MaxLengthEnforcement.enforced,
          validator: validator,
          autovalidateMode: AutovalidateMode.onUserInteraction,
          onChanged: (_) => _dirty = true,
          decoration: InputDecoration(labelText: label, counterText: ''),
        ),
      );

  Widget _dateField(String key, String label, bool enabled) {
    final iso = _dates[key] ?? '';
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: InkWell(
        key: Key('staffDate_$key'),
        onTap: enabled && !_busy ? () => _pickDate(key) : null,
        child: InputDecorator(
          decoration: InputDecoration(
            labelText: label,
            enabled: enabled,
            suffixIcon: iso.isEmpty || !enabled
                ? const Icon(Icons.calendar_today_outlined, size: 20)
                : IconButton(
                    tooltip: context.l.empClearDate,
                    icon: const Icon(Icons.close_rounded),
                    onPressed: () => setState(() {
                      _dates[key] = '';
                      _dirty = true;
                    }),
                  ),
          ),
          child: Text(iso.isEmpty ? ' ' : _showDate(iso)),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final e = _e;
    final canPersonal = _canPersonal;
    final canRights = _canRights;
    final self = _isSelf;
    final roleItems = [
      for (final r in _roles) DropdownMenuItem(value: r.id, child: Text(r.name, overflow: TextOverflow.ellipsis)),
      // должность сотрудника, которой нет в списке (список не загрузился) — не теряем
      if (_roleId.isNotEmpty && !_roles.any((r) => r.id == _roleId)) DropdownMenuItem(value: _roleId, child: Text(e?.role.isNotEmpty == true ? e!.role : _roleId, overflow: TextOverflow.ellipsis)),
    ];
    final canDismiss = !_isNew && canPersonal && !self && !e!.isOwnerRole && (!e.dismissed || e.activeKey != null);
    return LeaveGuard(
      ask: () => _dirty && !_busy,
      child: Scaffold(
        appBar: ScreenTitle(_isNew ? l.empAdd : l.empCard, subtitle: _isNew ? null : e!.name),
        bottomNavigationBar: canPersonal || canRights
            ? SafeArea(
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(16, 6, 16, 10),
                  child: FilledButton(
                    key: const Key('staffSave'),
                    style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                    onPressed: _busy ? null : _save,
                    child: _busy ? const SizedBox(width: 22, height: 22, child: CircularProgressIndicator(strokeWidth: 2.5)) : Text(l.save),
                  ),
                ),
              )
            : null,
        body: Form(
          key: _formKey,
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
            children: [
              FieldLabel(l.empSectionData),
              _field(_first, l.empFirstName, enabled: canPersonal, cap: TextCapitalization.words, max: 100, key: const Key('staffFirst'), validator: (v) => (v ?? '').trim().isEmpty ? l.empRequired : null),
              _field(_last, l.empLastName, enabled: canPersonal, cap: TextCapitalization.words, max: 100, key: const Key('staffLast')),
              if (_shows('middle_name')) _field(_extra['middle_name']!, l.empMiddleName, enabled: canPersonal, cap: TextCapitalization.words, max: 100),
              _field(
                _email,
                l.empEmail,
                enabled: canPersonal && _isNew, // логин существующего сотрудника здесь не меняем
                type: TextInputType.emailAddress,
                max: 254,
                key: const Key('staffEmail'),
                validator: _isNew ? (v) => (v ?? '').trim().isEmpty ? l.empRequired : (isValidStaffEmail(v!) ? null : l.empEmailBad) : null,
              ),
              if (_isNew || _e!.has('phone_number') || _e!.has('phone'))
                _field(_phone, l.empPhone, enabled: canPersonal, type: TextInputType.phone, max: 32, key: const Key('staffPhone'), validator: (v) => isValidStaffPhone(v ?? '') ? null : l.empPhoneBad),
              if (_isNew || _e!.has('track_number')) _field(_track, l.empTrack, enabled: canPersonal, max: 64, key: const Key('staffTrack')),
              if (roleItems.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(bottom: 10),
                  child: DropdownButtonFormField<String>(
                    key: const Key('staffRole'),
                    initialValue: roleItems.any((i) => i.value == _roleId) ? _roleId : null,
                    isExpanded: true,
                    decoration: InputDecoration(labelText: l.empRole),
                    items: roleItems,
                    onChanged: canPersonal && !_busy
                        ? (v) => setState(() {
                            _roleId = v ?? '';
                            _dirty = true;
                          })
                        : null,
                  ),
                )
              else if (!_isNew && e!.role.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(bottom: 10),
                  child: InputDecorator(decoration: InputDecoration(labelText: l.empRole, enabled: false), child: Text(e.role)),
                ),
              for (final k in _extraDate)
                if (_shows(k)) _dateField(k, _label(l, k), canPersonal),
              for (final k in const ['address', 'comment'])
                if (_shows(k)) _field(_extra[k]!, _label(l, k), enabled: canPersonal, max: 255),
              const SizedBox(height: 6),
              FieldLabel(l.empSectionRights),
              if (self)
                Padding(
                  padding: const EdgeInsets.only(bottom: 6),
                  child: Text(l.rightSelfLocked, style: const TextStyle(color: T.warn, fontSize: 12.5, fontWeight: FontWeight.w600)),
                ),
              if (!canRights)
                Padding(
                  padding: const EdgeInsets.only(bottom: 6),
                  child: Text(l.rightsReadOnly, style: TextStyle(color: pal.textSoft, fontSize: 12.5)),
                ),
              for (final k in _rights.keys)
                SwitchListTile(
                  key: Key('staffRight_$k'),
                  contentPadding: EdgeInsets.zero,
                  value: _rights[k] ?? false,
                  title: Text(rightTitle(context, k)),
                  // 2026-10-22 (мобилка, админ, П9): у себя включённое право не выключается.
                  onChanged: !canRights || _busy || (self && (e!.rights[k] ?? false))
                      ? null
                      : (v) => setState(() {
                          _rights[k] = v;
                          _dirty = true;
                        }),
                ),
              if (canDismiss) ...[
                const SizedBox(height: 12),
                if (ref.read(sessionProvider)?.isAdmin ?? false)
                  OutlinedButton.icon(
                    key: const Key('staffResetPwd'),
                    style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                    onPressed: _busy ? null : _resetPassword,
                    icon: const Icon(Icons.key_rounded),
                    label: Text(l.empResetPwd),
                  ),
                const SizedBox(height: 10),
                OutlinedButton.icon(
                  key: Key(e.dismissed ? 'staffRestore' : 'staffDismiss'),
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48), foregroundColor: e.dismissed ? null : T.danger),
                  onPressed: _busy ? null : () => _setDismissed(!e.dismissed),
                  icon: Icon(e.dismissed ? Icons.person_add_alt_1_rounded : Icons.person_off_rounded),
                  label: Text(e.dismissed ? l.empRestore : l.empDismiss),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
