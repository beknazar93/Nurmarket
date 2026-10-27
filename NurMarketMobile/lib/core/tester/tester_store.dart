import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:uuid/uuid.dart';

import '../api/api_client.dart';
import '../db/database.dart';
import '../providers.dart';
import 'tester_checklist.dart';

/// 2026-10-27: отметки тестировщика. Хранятся на устройстве (таблица настроек drift, схема не менялась) и, если у
/// пользователя есть права, — в общих настройках компании (см. [AppSettingsTesterSync]) для счётчика «на скольких устройствах».
enum TesterStatus {
  ok,
  issue;

  static TesterStatus? parse(Object? v) => switch ('$v') {
    'ok' => ok,
    'issue' => issue,
    _ => null,
  };
}

class TesterMark {
  final TesterStatus status;
  final DateTime ts;
  final String comment;

  /// Путь к необязательному скриншоту на этом устройстве (на сервер не уходит).
  final String? shot;

  /// Отметка уже записана в общий документ.
  final bool synced;

  const TesterMark({required this.status, required this.ts, this.comment = '', this.shot, this.synced = false});

  TesterMark copyWith({String? comment, String? shot, bool clearShot = false, bool? synced}) =>
      TesterMark(status: status, ts: ts, comment: comment ?? this.comment, shot: clearShot ? null : (shot ?? this.shot), synced: synced ?? this.synced);

  Map<String, dynamic> toJson() => {
    's': status.name,
    't': ts.toUtc().toIso8601String(),
    if (comment.isNotEmpty) 'c': comment,
    if (shot != null) 'p': shot,
    if (synced) 'y': 1,
  };

  static TesterMark? fromJson(Object? m) {
    if (m is! Map) return null;
    final s = TesterStatus.parse(m['s']);
    if (s == null) return null;
    return TesterMark(
      status: s,
      ts: DateTime.tryParse('${m['t'] ?? ''}')?.toLocal() ?? DateTime.fromMillisecondsSinceEpoch(0),
      comment: '${m['c'] ?? ''}',
      shot: m['p'] is String && (m['p'] as String).isNotEmpty ? m['p'] as String : null,
      synced: m['y'] == 1,
    );
  }
}

/// Локальное хранилище: отметки, id и имя устройства.
class TesterStore {
  TesterStore(this.db);
  final AppDatabase db;

  static const marksKey = 'tester.marks';
  static const deviceIdKey = 'tester.deviceId';
  static const deviceNameKey = 'tester.deviceName';

  Future<Map<String, TesterMark>> loadMarks() async {
    try {
      final raw = await db.getSetting(marksKey);
      if (raw == null || raw.isEmpty) return {};
      final m = jsonDecode(raw);
      if (m is! Map) return {};
      final out = <String, TesterMark>{};
      for (final e in m.entries) {
        final mark = TesterMark.fromJson(e.value);
        if (mark != null) out['${e.key}'] = mark;
      }
      return out;
    } catch (_) {
      return {}; // испорченная запись — начинаем с чистого списка
    }
  }

  Future<void> saveMarks(Map<String, TesterMark> marks) async {
    try {
      await db.setSetting(marksKey, jsonEncode({for (final e in marks.entries) e.key: e.value.toJson()}));
    } catch (_) {
      // не удалось записать — отметка останется до закрытия экрана
    }
  }

  /// Короткий id устройства: хэш случайного кода, созданного при первом открытии (не IMEI и не серийный номер).
  Future<String> deviceId() async {
    try {
      final saved = await db.getSetting(deviceIdKey);
      if (saved != null && saved.length >= 6) return saved;
      final id = sha256.convert(utf8.encode(const Uuid().v4())).toString().substring(0, 8).toUpperCase();
      await db.setSetting(deviceIdKey, id);
      return id;
    } catch (_) {
      return '00000000';
    }
  }

  static const removedKey = 'tester.removed';

  /// Пункты, с которых отметку сняли, а общий документ ещё не знает об этом.
  Future<Set<String>> loadRemoved() async {
    try {
      final raw = await db.getSetting(removedKey);
      final l = raw == null || raw.isEmpty ? null : jsonDecode(raw);
      return l is List ? {for (final x in l) '$x'} : <String>{};
    } catch (_) {
      return <String>{};
    }
  }

  Future<void> saveRemoved(Set<String> ids) async {
    try {
      await db.setSetting(removedKey, jsonEncode(ids.toList()));
    } catch (_) {}
  }

  Future<String> deviceName() async {
    try {
      return (await db.getSetting(deviceNameKey)) ?? '';
    } catch (_) {
      return '';
    }
  }

  Future<void> setDeviceName(String name) async {
    try {
      await db.setSetting(deviceNameKey, name.trim());
    } catch (_) {}
  }
}

// ------------------------------------------------------------------------------------------------------ общий документ

/// Чужая (или своя) отметка из общего документа.
class RemoteMark {
  final TesterStatus status;
  final DateTime? ts;
  final String comment;
  final String name;
  const RemoteMark(this.status, this.ts, this.comment, this.name);
}

/// itemId → deviceId → отметка.
typedef RemoteChecks = Map<String, Map<String, RemoteMark>>;

class TesterPull {
  final RemoteChecks checks;

  /// false — общий документ прочитать не удалось.
  final bool available;
  const TesterPull(this.checks, this.available);
}

class TesterChange {
  final String itemId;

  /// null — снять отметку этого устройства.
  final TesterMark? mark;
  const TesterChange(this.itemId, this.mark);
}

enum TesterPush { ok, forbidden, failed, local }

/// Синхронизация отметок между устройствами. Две реализации: [LocalTesterSync] (по умолчанию, без сервера) и
/// [AppSettingsTesterSync] (общий документ компании). Любые сбои — без исключений, результатом.
abstract class TesterSync {
  bool get isShared;
  Future<TesterPull> pull();
  Future<TesterPush> push({required String deviceId, required String deviceName, required List<TesterChange> changes});
}

class LocalTesterSync implements TesterSync {
  const LocalTesterSync();
  @override
  bool get isShared => false;
  @override
  Future<TesterPull> pull() async => const TesterPull({}, false);
  @override
  Future<TesterPush> push({required String deviceId, required String deviceName, required List<TesterChange> changes}) async => TesterPush.local;
}

/// api/main/app-settings/nurmarket-mobile-tests/, ключ Checks — как AiAccess/PaymentQrs, но в отдельном документе.
/// Чтение разрешено всем сотрудникам, запись — владельцу/администратору (остальным сервер вернёт 403 — тогда остаёмся локальными).
class AppSettingsTesterSync implements TesterSync {
  AppSettingsTesterSync(this.api);
  final ApiClient api;

  static const path = 'api/main/app-settings/nurmarket-mobile-tests/';
  static const key = 'Checks';

  @override
  bool get isShared => true;

  Future<Object?> _read() async {
    final data = await api.get(path, timeout: const Duration(seconds: 8)).timeout(const Duration(seconds: 10));
    final settings = data is Map ? data['settings'] : null;
    return settings is Map ? settings[key] : null;
  }

  /// Разбор общего документа; мусор в отдельных записях пропускается.
  static RemoteChecks parse(Object? raw) {
    final out = <String, Map<String, RemoteMark>>{};
    if (raw is! Map) return out;
    for (final item in raw.entries) {
      final devices = item.value;
      if (devices is! Map) continue;
      for (final d in devices.entries) {
        final m = d.value;
        if (m is! Map) continue;
        final st = TesterStatus.parse(m['status']);
        if (st == null) continue;
        (out['${item.key}'] ??= {})['${d.key}'] = RemoteMark(
          st,
          DateTime.tryParse('${m['ts'] ?? ''}')?.toLocal(),
          '${m['comment'] ?? ''}',
          '${m['name'] ?? ''}',
        );
      }
    }
    return out;
  }

  /// Слияние БЕЗ затирания чужих ключей: берём прочитанный документ, меняем только ключи [deviceId] у перечисленных
  /// пунктов (null — удалить свой ключ), остальное остаётся как есть. Пустой пункт после удаления выбрасывается.
  static Map<String, dynamic> merge(Object? current, {required String deviceId, required String deviceName, required List<TesterChange> changes}) {
    final out = <String, dynamic>{};
    if (current is Map) {
      for (final e in current.entries) {
        final v = e.value;
        out['${e.key}'] = v is Map ? Map<String, dynamic>.from(v.map((k, x) => MapEntry('$k', x))) : v;
      }
    }
    for (final c in changes) {
      final cur = out[c.itemId];
      final devices = cur is Map<String, dynamic> ? cur : <String, dynamic>{};
      if (c.mark == null) {
        devices.remove(deviceId);
      } else {
        devices[deviceId] = {
          'status': c.mark!.status.name,
          'ts': c.mark!.ts.toUtc().toIso8601String(),
          if (c.mark!.comment.isNotEmpty) 'comment': c.mark!.comment,
          if (deviceName.isNotEmpty) 'name': deviceName,
        };
      }
      if (devices.isEmpty) {
        out.remove(c.itemId);
      } else {
        out[c.itemId] = devices;
      }
    }
    return out;
  }

  @override
  Future<TesterPull> pull() async {
    try {
      return TesterPull(parse(await _read()), true);
    } on ApiException catch (e) {
      if (e.status == 404) return const TesterPull({}, true); // документа ещё нет — счётчики пустые
      return const TesterPull({}, false);
    } catch (_) {
      return const TesterPull({}, false);
    }
  }

  @override
  Future<TesterPush> push({required String deviceId, required String deviceName, required List<TesterChange> changes}) async {
    if (changes.isEmpty) return TesterPush.ok;
    try {
      Object? current;
      try {
        current = await _read();
      } on ApiException catch (e) {
        if (e.status != 404) rethrow;
      }
      await api.patch(
        path,
        body: {key: merge(current, deviceId: deviceId, deviceName: deviceName, changes: changes)},
      );
      return TesterPush.ok;
    } on ApiException catch (e) {
      return e.status == 403 ? TesterPush.forbidden : TesterPush.failed;
    } catch (_) {
      return TesterPush.failed;
    }
  }
}

/// Со входом — общий документ компании (запишется только если хватает прав), без входа — локально.
final testerSyncProvider = Provider<TesterSync>((ref) {
  final s = ref.watch(sessionProvider);
  return s == null ? const LocalTesterSync() : AppSettingsTesterSync(ref.watch(apiProvider));
});

final testerStoreProvider = Provider<TesterStore>((ref) => TesterStore(ref.watch(databaseProvider)));

// ------------------------------------------------------------------------------------------------------ счётчик и отчёт

class TesterCount {
  final int ok;
  final int issue;
  const TesterCount(this.ok, this.issue);
  bool get none => ok == 0 && issue == 0;
}

/// На скольких устройствах проверено / есть проблема. Своё устройство берётся из локальной отметки [mine] (она новее общей),
/// остальные — из общего документа [remote]; без документа считается только это устройство.
TesterCount testerCount({required Map<String, RemoteMark>? remote, required TesterMark? mine, required String deviceId}) {
  final status = <String, TesterStatus>{};
  remote?.forEach((id, m) => status[id] = m.status);
  if (mine == null) {
    status.remove(deviceId);
  } else {
    status[deviceId] = mine.status;
  }
  var ok = 0, issue = 0;
  for (final s in status.values) {
    s == TesterStatus.ok ? ok++ : issue++;
  }
  return TesterCount(ok, issue);
}

/// Текст отчёта для владельца (всегда по-русски).
String testerReport({
  required String version,
  required int build,
  required bool testBuild,
  required String deviceLine,
  required List<(TesterArea, List<TesterItem>)> groups,
  required Map<String, TesterMark> marks,
  required DateTime now,
}) {
  String two(int n) => n.toString().padLeft(2, '0');
  String when(DateTime d) => '${two(d.day)}.${two(d.month)}.${d.year} ${two(d.hour)}:${two(d.minute)}';
  final all = [for (final g in groups) ...g.$2];
  final ok = all.where((i) => marks[i.id]?.status == TesterStatus.ok).toList();
  final bad = all.where((i) => marks[i.id]?.status == TesterStatus.issue).toList();
  final todo = all.where((i) => marks[i.id] == null).toList();
  String area(TesterItem i) {
    for (final g in groups) {
      if (g.$2.contains(i)) return g.$1.name('ru');
    }
    return i.area;
  }

  final b = StringBuffer()
    ..writeln('Отчёт тестировщика NurMarket Mobile')
    ..writeln('Версия: $version (сборка $build)${testBuild ? ', тестовая' : ''}')
    ..writeln('Устройство: $deviceLine')
    ..writeln('Дата: ${when(now)}')
    ..writeln('Итого: проверено ${ok.length} из ${all.length}, проблем ${bad.length}, не проверено ${todo.length}');
  if (bad.isNotEmpty) {
    b
      ..writeln()
      ..writeln('ПРОБЛЕМЫ (${bad.length})');
    for (final i in bad) {
      final m = marks[i.id]!;
      b.writeln(
        '- [${area(i)}] ${i.titleIn('ru')} (${when(m.ts)}): ${m.comment.isEmpty ? 'без комментария' : m.comment}${m.shot != null ? ' [есть скриншот]' : ''}',
      );
    }
  }
  if (ok.isNotEmpty) {
    b
      ..writeln()
      ..writeln('ПРОВЕРЕНО (${ok.length})');
    for (final i in ok) {
      b.writeln('- [${area(i)}] ${i.titleIn('ru')} (${when(marks[i.id]!.ts)})');
    }
  }
  if (todo.isNotEmpty) {
    b
      ..writeln()
      ..writeln('НЕ ПРОВЕРЕНО (${todo.length})');
    for (final i in todo) {
      b.writeln('- [${area(i)}] ${i.titleIn('ru')}');
    }
  }
  return b.toString().trimRight();
}
