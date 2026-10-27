import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:path_provider/path_provider.dart';
import 'package:share_plus/share_plus.dart';

import '../../core/theme/tokens.dart';
import '../../core/tester/tester_checklist.dart';
import '../../core/tester/tester_mode.dart';
import '../../core/tester/tester_store.dart';
import '../../core/util/ui.dart';

/// 2026-10-27, владелец: «в режиме тестировщика — вся информация, что добавлено и что проверено; для каждого устройства
/// просто нажимаешь то, что проверил; под функцией — счётчик, на скольких устройствах проверено». Экран открывается пятью
/// быстрыми нажатиями на логотип (см. [TesterTap]); список функций — данные assets/data/tester_checklist.json.
class TesterScreen extends ConsumerStatefulWidget {
  const TesterScreen({
    super.key,
    this.debugChecklist,
    this.debugSync,
    this.pollEvery = const Duration(seconds: 60),
    this.pickShot,
    this.currentVersion = AppBuild.version,
    this.now,
  });

  /// Подмена для тестов: список, синхронизация, выбор скриншота, текущая версия, часы.
  final TesterChecklist? debugChecklist;
  final TesterSync? debugSync;
  final Duration pollEvery;
  final Future<String?> Function()? pickShot;
  final String currentVersion;
  final DateTime Function()? now;

  @override
  ConsumerState<TesterScreen> createState() => _TesterScreenState();
}

class _TesterScreenState extends ConsumerState<TesterScreen> {
  TesterChecklist? _list;
  Map<String, TesterMark> _marks = {};
  Set<String> _removed = {};
  RemoteChecks _remote = {};
  bool _readable = false; // общий документ прочитан
  bool _writeBlocked = false; // сервер ответил 403 на запись — остаёмся локальными до следующего открытия экрана
  String _deviceId = '';
  String _deviceName = '';
  String? _version;
  Timer? _timer;
  bool _pulling = false;
  bool _pushing = false;
  bool _disposed = false;

  TesterStore get _store => ref.read(testerStoreProvider);
  TesterSync get _sync => widget.debugSync ?? ref.read(testerSyncProvider);
  DateTime _now() => (widget.now ?? DateTime.now)();

  @override
  void initState() {
    super.initState();
    unawaited(_init());
  }

  @override
  void dispose() {
    _disposed = true;
    _timer?.cancel();
    super.dispose();
  }

  Future<void> _init() async {
    final store = _store;
    final list = widget.debugChecklist ?? await TesterChecklist.load();
    final marks = await store.loadMarks();
    final removed = await store.loadRemoved();
    final id = await store.deviceId();
    final name = await store.deviceName();
    if (_disposed) return;
    setState(() {
      _list = list;
      _marks = marks;
      _removed = removed;
      _deviceId = id;
      _deviceName = name;
      _version = list.defaultVersion(widget.currentVersion);
    });
    await _pull();
    await _pushPending();
    // опрос общего счётчика — не чаще раза в 60 с, пока экран открыт
    if (widget.pollEvery > Duration.zero) {
      _timer = Timer.periodic(widget.pollEvery, (_) {
        unawaited(_pull());
      });
    }
  }

  Future<void> _pull() async {
    if (_pulling || _disposed) return;
    _pulling = true;
    try {
      final p = await _sync.pull();
      if (_disposed) return;
      setState(() {
        _remote = p.checks;
        _readable = p.available;
      });
    } catch (_) {
      if (!_disposed) setState(() => _readable = false);
    } finally {
      _pulling = false;
    }
  }

  /// Отправить в общий документ всё, что ещё не отправлено (новые отметки и снятые). Ошибки — тихо.
  Future<void> _pushPending() async {
    if (_pushing || _disposed || !_sync.isShared || _writeBlocked) return;
    final changes = <TesterChange>[
      for (final e in _marks.entries)
        if (!e.value.synced) TesterChange(e.key, e.value),
      for (final id in _removed)
        if (!_marks.containsKey(id)) TesterChange(id, null),
    ];
    if (changes.isEmpty) return;
    _pushing = true;
    try {
      final r = await _sync.push(deviceId: _deviceId, deviceName: _deviceName, changes: changes);
      if (_disposed) return;
      if (r == TesterPush.ok) {
        setState(() {
          for (final c in changes) {
            final m = _marks[c.itemId];
            if (c.mark != null && m != null && m.status == c.mark!.status && m.ts == c.mark!.ts) _marks[c.itemId] = m.copyWith(synced: true);
          }
          _removed = {};
        });
        await _store.saveMarks(_marks);
        await _store.saveRemoved(_removed);
        await _pull();
      } else if (r == TesterPush.forbidden) {
        setState(() => _writeBlocked = true);
      }
    } finally {
      _pushing = false;
    }
  }

  Future<void> _persist() async {
    await _store.saveMarks(_marks);
    await _store.saveRemoved(_removed);
    unawaited(_pushPending());
  }

  void _remove(String id) {
    setState(() {
      _marks = {..._marks}..remove(id);
      _removed = {..._removed, id};
    });
    unawaited(_persist());
  }

  void _toggleOk(TesterItem item) {
    final cur = _marks[item.id];
    if (cur?.status == TesterStatus.ok) return _remove(item.id);
    setState(() {
      _marks = {..._marks, item.id: TesterMark(status: TesterStatus.ok, ts: _now())};
      _removed = {..._removed}..remove(item.id);
    });
    unawaited(_persist());
  }

  Future<void> _toggleIssue(TesterItem item) async {
    final cur = _marks[item.id];
    if (cur?.status == TesterStatus.issue) return _remove(item.id);
    final r = await _askIssue(initial: '', shot: null);
    if (r == null || !mounted) return;
    setState(() {
      _marks = {..._marks, item.id: TesterMark(status: TesterStatus.issue, ts: _now(), comment: r.$1, shot: r.$2)};
      _removed = {..._removed}..remove(item.id);
    });
    unawaited(_persist());
  }

  Future<void> _editComment(TesterItem item) async {
    final cur = _marks[item.id];
    if (cur == null || cur.status != TesterStatus.issue) return;
    final r = await _askIssue(initial: cur.comment, shot: cur.shot);
    if (r == null || !mounted) return;
    setState(() => _marks = {..._marks, item.id: TesterMark(status: TesterStatus.issue, ts: cur.ts, comment: r.$1, shot: r.$2)});
    unawaited(_persist());
  }

  /// Окно «Что не так?»: комментарий и необязательный скриншот (системный выбор картинки). Возвращает (комментарий, путь).
  Future<(String, String?)?> _askIssue({required String initial, required String? shot}) {
    final l = context.l;
    final c = TextEditingController(text: initial);
    String? path = shot;
    return showDialog<(String, String?)>(
      context: context,
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setD) => AlertDialog(
          scrollable: true,
          title: Text(l.tsIssueTitle),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextField(
                controller: c,
                autofocus: true,
                minLines: 3,
                maxLines: 6,
                textInputAction: TextInputAction.newline,
                decoration: InputDecoration(hintText: l.tsIssueHint),
              ),
              const SizedBox(height: 12),
              if (path == null)
                OutlinedButton.icon(
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                  onPressed: () async {
                    final p = await (widget.pickShot ?? _pickShot)();
                    if (p != null) setD(() => path = p);
                  },
                  icon: const Icon(Icons.add_photo_alternate_outlined),
                  label: Text(l.tsAttachShot),
                )
              else
                Row(
                  children: [
                    const Icon(Icons.image_outlined, size: 20),
                    const SizedBox(width: 8),
                    Expanded(child: Text(l.tsShotAttached)),
                    IconButton(tooltip: l.tsShotRemove, onPressed: () => setD(() => path = null), icon: const Icon(Icons.close_rounded)),
                  ],
                ),
            ],
          ),
          actions: [
            TextButton(onPressed: () => Navigator.pop(ctx), child: Text(l.cancel)),
            TextButton(onPressed: () => Navigator.pop(ctx, (c.text.trim(), path)), child: Text(l.save)),
          ],
        ),
      ),
    );
  }

  /// Системный выбор картинки из галереи (image_picker уже подключён); копия — в папку приложения, чтобы файл не пропал.
  Future<String?> _pickShot() async {
    try {
      final f = await ImagePicker().pickImage(source: ImageSource.gallery, maxWidth: 1600, maxHeight: 1600, imageQuality: 80);
      if (f == null) return null;
      try {
        final dir = Directory('${(await getApplicationSupportDirectory()).path}/tester_shots');
        await dir.create(recursive: true);
        final copy = await File(f.path).copy('${dir.path}/${DateTime.now().millisecondsSinceEpoch}.jpg');
        return copy.path;
      } catch (_) {
        return f.path;
      }
    } catch (_) {
      return null; // выбор картинки недоступен — остаётся только комментарий
    }
  }

  Future<void> _rename() async {
    final l = context.l;
    final c = TextEditingController(text: _deviceName);
    final name = await showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l.tsDeviceNameTitle),
        content: TextField(
          controller: c,
          autofocus: true,
          maxLength: 40,
          decoration: InputDecoration(hintText: l.tsDeviceNameHint),
          onSubmitted: (v) => Navigator.pop(ctx, v),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx), child: Text(l.cancel)),
          TextButton(onPressed: () => Navigator.pop(ctx, c.text), child: Text(l.save)),
        ],
      ),
    );
    // контроллер не освобождаем: окно ещё гаснет и использует его
    if (name == null || !mounted) return;
    await _store.setDeviceName(name);
    if (mounted) setState(() => _deviceName = name.trim());
  }

  // ------------------------------------------------------------------------------------------------------ отчёт

  String _osLine() {
    final v = Platform.operatingSystemVersion.split('#').first.trim();
    final os = Platform.operatingSystem.isEmpty ? '' : '${Platform.operatingSystem[0].toUpperCase()}${Platform.operatingSystem.substring(1)}';
    final s = '$os · $v';
    return s.length > 52 ? '${s.substring(0, 52)}…' : s;
  }

  String _screenLine(BuildContext context) {
    final mq = MediaQuery.of(context);
    final scale = mq.textScaler.scale(10) / 10;
    return context.l.tsScreenLine(
      mq.size.width.round().toString(),
      mq.size.height.round().toString(),
      scale.toStringAsFixed(1),
      Localizations.localeOf(context).languageCode,
    );
  }

  String _report(BuildContext context) {
    final list = _list!;
    final items = list.forVersion(_version ?? '');
    final mq = MediaQuery.of(context);
    final scale = (mq.textScaler.scale(10) / 10).toStringAsFixed(1);
    final line =
        '${_deviceName.isEmpty ? '' : '$_deviceName, '}${_osLine()}, id $_deviceId, экран ${mq.size.width.round()}×${mq.size.height.round()} dp, шрифт ×$scale, язык ${Localizations.localeOf(context).languageCode}';
    return testerReport(
      version: _version ?? AppBuild.version,
      build: _version == AppBuild.version ? AppBuild.build : 0,
      testBuild: AppBuild.isTestBuild,
      deviceLine: line,
      groups: list.grouped(items),
      marks: _marks,
      now: _now(),
    );
  }

  Future<void> _copy(BuildContext context) async {
    final text = _report(context);
    await Clipboard.setData(ClipboardData(text: text));
    if (context.mounted) toast(context, context.l.tsCopied);
  }

  Future<void> _share(BuildContext context) async {
    final text = _report(context);
    final ids = {for (final i in _list!.forVersion(_version ?? '')) i.id};
    final files = <XFile>[
      for (final e in _marks.entries)
        if (ids.contains(e.key) && e.value.status == TesterStatus.issue && e.value.shot != null && File(e.value.shot!).existsSync()) XFile(e.value.shot!),
    ];
    try {
      await SharePlus.instance.share(ShareParams(text: text, subject: context.l.tsTitle, files: files.isEmpty ? null : files));
    } catch (_) {
      if (context.mounted) await _copy(context); // системное «Поделиться» недоступно — буфер обмена
    }
  }

  // ------------------------------------------------------------------------------------------------------ вид

  String _when(DateTime d) {
    String two(int n) => n.toString().padLeft(2, '0');
    return '${two(d.day)}.${two(d.month)} ${two(d.hour)}:${two(d.minute)}';
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final list = _list;
    final lang = Localizations.localeOf(context).languageCode;
    final actions = [IconButton(tooltip: l.tsRefresh, onPressed: () => unawaited(_pull()), icon: const Icon(Icons.refresh_rounded))];
    if (list == null) {
      return Scaffold(
        appBar: ScreenTitle(l.tsTitle, actions: actions),
        body: const Center(child: CircularProgressIndicator()),
      );
    }
    final version = _version ?? widget.currentVersion;
    final items = list.forVersion(version);
    final done = items.where((i) => _marks[i.id]?.status == TesterStatus.ok).length;
    final bad = items.where((i) => _marks[i.id]?.status == TesterStatus.issue).length;
    final shared = _sync.isShared && _readable && !_writeBlocked;

    final rows = <Widget>[
      _header(context),
      if (list.versions.length > 1) _versionChips(context, list),
      if (!shared) _banner(context),
      if (items.isEmpty)
        Padding(
          padding: const EdgeInsets.all(24),
          child: Center(
            child: Text(l.tsEmpty, style: TextStyle(color: pal.textSoft)),
          ),
        ),
      for (final g in list.grouped(items)) ...[_areaHeader(context, g.$1.name(lang), g.$2), for (final it in g.$2) _card(context, it, lang)],
      const SizedBox(height: 24),
    ];
    return Scaffold(
      appBar: ScreenTitle(l.tsTitle, subtitle: l.tsBuildLine(AppBuild.version, '${AppBuild.build}'), actions: actions),
      // итог «проверено X из Y» закреплён над списком — виден при прокрутке
      body: SafeArea(
        top: false,
        child: Column(
          children: [
            _summaryBar(context, items.length, done, bad),
            Expanded(
              child: ListView(padding: const EdgeInsets.fromLTRB(16, 4, 16, 8), children: rows),
            ),
          ],
        ),
      ),
    );
  }

  Widget _header(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final name = _deviceName.trim();
    return Container(
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: pal.panel,
        borderRadius: BorderRadius.circular(T.radiusCard),
        border: Border.all(color: pal.border),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 4,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text(l.tsBuildLine(AppBuild.version, '${AppBuild.build}'), style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800)),
              if (AppBuild.isTestBuild)
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                  decoration: BoxDecoration(color: pal.accentSoft, borderRadius: BorderRadius.circular(8)),
                  child: Text(
                    l.tsTestBuild,
                    style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: pal.dark ? T.accent : T.warn),
                  ),
                ),
            ],
          ),
          const SizedBox(height: 8),
          // имя устройства правится нажатием на строку (так шапка короче и первая функция видна сразу)
          InkWell(
            onTap: _rename,
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 48),
              child: Row(
                children: [
                  Expanded(
                    child: Semantics(
                      button: true,
                      hint: l.tsRename,
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text('${l.tsDevice}: ${name.isEmpty ? _osLine() : name}', style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600)),
                          if (name.isNotEmpty) Text(_osLine(), style: TextStyle(fontSize: 12, color: pal.textSoft)),
                          Text(l.tsDeviceId(_deviceId), style: TextStyle(fontSize: 12, color: pal.textSoft)),
                          Text(_screenLine(context), style: TextStyle(fontSize: 12, color: pal.textSoft)),
                        ],
                      ),
                    ),
                  ),
                  Tooltip(
                    message: l.tsRename,
                    child: Icon(Icons.edit_outlined, size: 18, color: pal.textSoft),
                  ),
                ],
              ),
            ),
          ),
          const Divider(height: 16),
          Row(
            children: [
              Expanded(
                child: FilledButton.icon(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                  onPressed: _list == null ? null : () => _copy(context),
                  icon: const Icon(Icons.copy_rounded, size: 20),
                  label: Text(l.tsCopyReport, textAlign: TextAlign.center),
                ),
              ),
              const SizedBox(width: 8),
              Tooltip(
                message: l.tsShareReport,
                child: OutlinedButton(
                  style: OutlinedButton.styleFrom(minimumSize: const Size(52, 48), padding: EdgeInsets.zero),
                  onPressed: _list == null ? null : () => _share(context),
                  child: Semantics(label: l.tsShareReport, child: const Icon(Icons.ios_share_rounded, size: 22)),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _summaryBar(BuildContext context, int total, int done, int bad) {
    final pal = context.pal;
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 10),
      decoration: BoxDecoration(
        color: pal.window,
        border: Border(bottom: BorderSide(color: pal.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Semantics(
            liveRegion: true,
            child: Text(context.l.tsSummary('$done', '$total', '$bad'), style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800)),
          ),
          const SizedBox(height: 6),
          ClipRRect(
            borderRadius: BorderRadius.circular(4),
            child: LinearProgressIndicator(value: total == 0 ? 0 : done / total, minHeight: 8, color: pal.success, backgroundColor: pal.panel),
          ),
        ],
      ),
    );
  }

  Widget _versionChips(BuildContext context, TesterChecklist list) {
    final l = context.l;
    final pal = context.pal;
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Wrap(
        spacing: 8,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text('${l.tsVersionFilter}:', style: TextStyle(fontSize: 13, color: pal.textSoft)),
          for (final v in list.versions)
            Semantics(
              button: true,
              selected: v == _version,
              label: v == widget.currentVersion ? l.tsVersionCurrent(v) : v,
              excludeSemantics: true,
              child: Material(
                color: v == _version ? T.accent : pal.panel,
                shape: StadiumBorder(side: BorderSide(color: v == _version ? T.accent : pal.border)),
                child: InkWell(
                  customBorder: const StadiumBorder(),
                  onTap: () => setState(() => _version = v),
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(minHeight: 48, minWidth: 48),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 14),
                      child: Center(
                        widthFactor: 1,
                        child: Text(
                          v == widget.currentVersion ? l.tsVersionCurrent(v) : v,
                          style: TextStyle(fontSize: 13.5, fontWeight: FontWeight.w700, color: v == _version ? T.navy : pal.text),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }

  Widget _banner(BuildContext context) {
    final pal = context.pal;
    return Container(
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(color: pal.warnSoft, borderRadius: BorderRadius.circular(12)),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.cloud_off_rounded, size: 20, color: pal.warn),
          const SizedBox(width: 10),
          Expanded(
            child: Text(context.l.tsSyncOff, style: TextStyle(fontSize: 13, color: pal.text)),
          ),
        ],
      ),
    );
  }

  Widget _areaHeader(BuildContext context, String name, List<TesterItem> of) {
    final pal = context.pal;
    final done = of.where((i) => _marks[i.id]?.status == TesterStatus.ok).length;
    return Padding(
      padding: const EdgeInsets.fromLTRB(2, 12, 2, 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Semantics(
              header: true,
              child: Text(name, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
            ),
          ),
          const SizedBox(width: 8),
          Text(context.l.tsAreaProgress('$done', '${of.length}'), style: TextStyle(fontSize: 13, color: pal.textSoft)),
        ],
      ),
    );
  }

  (IconData, String, Color) _sev(BuildContext context, TesterSeverity s) {
    final l = context.l;
    final pal = context.pal;
    return switch (s) {
      TesterSeverity.money => (Icons.circle, l.tsSevMoney, pal.danger),
      TesterSeverity.important => (Icons.circle, l.tsSevImportant, pal.warn),
      TesterSeverity.normal => (Icons.circle_outlined, l.tsSevNormal, pal.textSoft),
    };
  }

  String _countText(BuildContext context, TesterItem item) {
    final l = context.l;
    final mine = _marks[item.id];
    final shared = _sync.isShared && _readable;
    if (!shared) {
      return switch (mine?.status) {
        TesterStatus.ok => l.tsCountLocalOk,
        TesterStatus.issue => l.tsCountLocalIssue,
        null => l.tsCountNone,
      };
    }
    final c = testerCount(remote: _remote[item.id], mine: mine, deviceId: _deviceId);
    return c.none ? l.tsCountNone : l.tsCountShared('${c.ok}', '${c.issue}');
  }

  Widget _card(BuildContext context, TesterItem item, String lang) {
    final l = context.l;
    final pal = context.pal;
    final mark = _marks[item.id];
    final sev = _sev(context, item.severity);
    final steps = item.stepsIn(lang);
    final isOk = mark?.status == TesterStatus.ok;
    final isBad = mark?.status == TesterStatus.issue;
    final status = isOk ? l.tsCheckedAt(_when(mark!.ts)) : (isBad ? l.tsIssueAt(_when(mark!.ts)) : l.tsNotChecked);
    final statusColor = isOk ? pal.success : (isBad ? pal.danger : pal.textSoft);

    ButtonStyle style(bool on, Color color, Color soft) => OutlinedButton.styleFrom(
      minimumSize: const Size(0, 48),
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
      foregroundColor: on ? color : pal.text,
      backgroundColor: on ? soft : null,
      side: BorderSide(color: on ? color : pal.border, width: on ? 2 : 1),
    );
    final textStyle = const TextStyle(fontSize: 13.5, fontWeight: FontWeight.w700);

    // Material (а не Container с цветом): ExpansionTile внутри рисует отклик нажатия на ближайшем Material
    return Padding(
      key: ValueKey('tester-${item.id}'),
      padding: const EdgeInsets.only(bottom: 12),
      child: Material(
        color: pal.panel,
        clipBehavior: Clip.antiAlias,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(T.radiusCard),
          side: BorderSide(color: isBad ? pal.danger : (isOk ? pal.success : pal.border)),
        ),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(14, 12, 14, 8),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Icon(sev.$1, size: 12, color: sev.$3),
                  const SizedBox(width: 6),
                  Flexible(
                    child: Text(
                      sev.$2,
                      style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: sev.$3),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 6),
              Text(item.titleIn(lang), style: const TextStyle(fontSize: 15.5, fontWeight: FontWeight.w800, height: 1.25)),
              const SizedBox(height: 8),
              Text(
                l.tsChanged,
                style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: pal.textSoft),
              ),
              const SizedBox(height: 2),
              Text(item.changedIn(lang), style: const TextStyle(fontSize: 13.5, height: 1.35)),
              if (steps.isNotEmpty)
                Theme(
                  data: Theme.of(context).copyWith(dividerColor: Colors.transparent),
                  child: ExpansionTile(
                    tilePadding: EdgeInsets.zero,
                    childrenPadding: const EdgeInsets.only(bottom: 8),
                    expandedCrossAxisAlignment: CrossAxisAlignment.start,
                    title: Text(l.tsHowTo, style: const TextStyle(fontSize: 13.5, fontWeight: FontWeight.w700)),
                    children: [
                      for (var i = 0; i < steps.length; i++)
                        Padding(
                          padding: const EdgeInsets.only(bottom: 6),
                          child: Row(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              SizedBox(
                                width: 24,
                                child: Text(
                                  '${i + 1}.',
                                  style: TextStyle(fontSize: 13.5, color: pal.textSoft, fontWeight: FontWeight.w700),
                                ),
                              ),
                              Expanded(child: Text(steps[i], style: const TextStyle(fontSize: 13.5, height: 1.35))),
                            ],
                          ),
                        ),
                    ],
                  ),
                )
              else
                const SizedBox(height: 4),
              Text(
                status,
                style: TextStyle(fontSize: 12.5, fontWeight: FontWeight.w700, color: statusColor),
              ),
              if (isBad)
                InkWell(
                  onTap: () => unawaited(_editComment(item)),
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(minHeight: 48),
                    child: Align(
                      alignment: AlignmentDirectional.centerStart,
                      child: Semantics(
                        button: true,
                        hint: l.tsEditComment,
                        child: Text(
                          '${mark!.comment.isEmpty ? l.tsNoComment : mark.comment}${mark.shot != null ? '  ·  ${l.tsShotAttached}' : ''}',
                          style: TextStyle(fontSize: 13.5, color: pal.text, decoration: TextDecoration.underline, decorationColor: pal.textSoft),
                        ),
                      ),
                    ),
                  ),
                ),
              const SizedBox(height: 8),
              IntrinsicHeight(
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Expanded(
                      child: Semantics(
                        button: true,
                        selected: isOk,
                        hint: isOk ? l.tsUndoHint : null,
                        child: OutlinedButton.icon(
                          key: ValueKey('ok-${item.id}'),
                          style: style(isOk, pal.success, pal.successSoft),
                          onPressed: () => _toggleOk(item),
                          icon: Icon(isOk ? Icons.check_circle_rounded : Icons.check_circle_outline_rounded, size: 20),
                          label: Text(l.tsMarkOk, textAlign: TextAlign.center, style: textStyle),
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Semantics(
                        button: true,
                        selected: isBad,
                        hint: isBad ? l.tsUndoHint : null,
                        child: OutlinedButton.icon(
                          key: ValueKey('bad-${item.id}'),
                          style: style(isBad, pal.danger, pal.dangerSoft),
                          onPressed: () => unawaited(_toggleIssue(item)),
                          icon: Icon(isBad ? Icons.warning_rounded : Icons.warning_amber_rounded, size: 20),
                          label: Text(l.tsMarkIssue, textAlign: TextAlign.center, style: textStyle),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Icon(Icons.devices_rounded, size: 16, color: pal.textSoft),
                    const SizedBox(width: 6),
                    Expanded(
                      child: Text(_countText(context, item), style: TextStyle(fontSize: 12.5, color: pal.textSoft)),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
