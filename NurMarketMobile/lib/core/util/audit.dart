import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

/// 14.10, владелец: «журнал аудита добавь». Кто, когда и что сделал на этом телефоне: продажи, возвраты, скидки,
/// удаление строк, цены, товары, приёмка, внесения/изъятия, смены, настройки. Пишется строками JSON в файл
/// (быстро, без перестройки базы); хранится последние 5000 записей. У сервера NurCRM API журнала нет — журнал этого
/// телефона (как «Удалено из чека»).
class AuditEntry {
  final DateTime at;
  final String user;
  final String kind;
  final String text;
  final double? amount;
  const AuditEntry(this.at, this.user, this.kind, this.text, this.amount);

  Map<String, dynamic> toJson() => {'at': at.toIso8601String(), 'user': user, 'kind': kind, 'text': text, if (amount != null) 'amount': amount};

  static AuditEntry? fromJson(String line) {
    try {
      final m = jsonDecode(line) as Map<String, dynamic>;
      return AuditEntry(DateTime.parse(m['at'] as String), '${m['user'] ?? ''}', '${m['kind'] ?? ''}', '${m['text'] ?? ''}', (m['amount'] as num?)?.toDouble());
    } catch (_) {
      return null;
    }
  }
}

class Audit {
  static const kinds = ['sale', 'cart', 'return', 'discount', 'delete', 'price', 'product', 'receive', 'cash', 'shift', 'settings', 'login', 'open', 'ai', 'sync', 'error'];

  /// Кто сейчас работает (ставит app.dart при входе/выходе) — пишется в каждую запись.
  static String currentUser = '';
  static const _max = 5000;

  /// Только для тестов: папка вместо папки приложения.
  static Directory? testDirectory;

  static Future<File> _file() async => File('${(testDirectory ?? await getApplicationDocumentsDirectory()).path}/audit.jsonl');

  static final List<String> _pending = [];
  static Future<void>? _flushing;

  /// Только для тестов экранов: у каждого теста свои «поддельные» часы, и пачка записи с прошлого теста могла остаться
  /// незавершённой (её таймер умер вместе с тестом) — следующая запись встала бы за ней навсегда.
  static void resetForTests() {
    _pending.clear();
    _flushing = null;
    _chain = Future.value();
    _sinceTrim = 1 << 30;
    _lastSync = DateTime.fromMillisecondsSinceEpoch(0);
  }

  /// Записать действие. Ошибки записи не мешают работе кассы.
  /// 14.10, стресс-тест: раньше каждая запись открывала файл и сбрасывала его на диск — 6000 записей подряд не
  /// успевали за 30 с. Теперь записи копятся и уходят в файл пачкой (одна запись на диск на пачку).
  static Future<void> log(String kind, String text, {double? amount}) {
    _pending.add('${jsonEncode(AuditEntry(DateTime.now(), currentUser, kind, text, amount).toJson())}\n');
    return _flushing ??= _serial(_flush);
  }

  // 2026-10-22 (мобилка, админ): П16 — запись пачки и обрезка файла идут строго по очереди. Раньше read() перезаписывал
  // файл, пока в него дописывалась новая пачка, — записи между чтением и перезаписью пропадали.
  static Future<void> _chain = Future.value();
  static Future<T> _serial<T>(Future<T> Function() f) {
    final r = _chain.then((_) => f());
    _chain = r.then((_) {}, onError: (_) {});
    return r;
  }

  /// 2026-10-30 (оптимизация, п. 8): fsync не на каждую пачку, а не чаще раза в [_syncEvery] и при сворачивании приложения
  /// ([syncToDisk]). Компромисс: запись без fsync уже передана операционной системе (close файла) — падение или закрытие
  /// приложения её не теряет; пропасть могут только последние ≤ 30 секунд при внезапном выключении питания телефона.
  static const _syncEvery = Duration(seconds: 30);
  static DateTime _lastSync = DateTime.fromMillisecondsSinceEpoch(0);

  /// 2026-10-30 (оптимизация, п. 8): обрезка файла до [_max] строк — при записи (раз за запуск и затем каждые 1000 записей), а не
  /// только при открытии экрана журнала: раньше без открытия журнал рос без предела.
  static int _sinceTrim = 1 << 30;

  static Future<void> _flush() async {
    try {
      await Future<void>.delayed(Duration.zero); // дать накопиться записям этого же такта
      final f = await _file();
      while (_pending.isNotEmpty) {
        final batch = _pending.join();
        final n = _pending.length;
        _pending.clear();
        try {
          final due = DateTime.now().difference(_lastSync) >= _syncEvery;
          await f.writeAsString(batch, mode: FileMode.append, flush: due);
          if (due) _lastSync = DateTime.now();
          _sinceTrim += n;
          if (_sinceTrim >= 1000) {
            _sinceTrim = 0;
            await _trim(f);
          }
        } catch (_) {
          // журнал не записался — продажа важнее
        }
      }
    } finally {
      _flushing = null;
    }
  }

  /// Обрезка до последних [_max] строк через временный файл и переименование (обрыв посреди записи не оставляет половинный журнал).
  static Future<void> _trim(File f) async {
    if (!await f.exists()) return;
    final lines = await f.readAsLines();
    if (lines.length <= _max) return;
    final tmp = File('${f.path}.tmp');
    await tmp.writeAsString('${lines.sublist(lines.length - _max).join('\n')}\n', flush: true);
    await tmp.rename(f.path);
  }

  /// 2026-10-30 (оптимизация, п. 8): приложение свёрнуто — всё накопленное сбрасывается на диск (fsync) одним разом.
  static Future<void> syncToDisk() async {
    try {
      await _flushing;
      await _serial(() async {
        final f = await _file();
        if (!await f.exists()) return;
        final raf = await f.open(mode: FileMode.append);
        try {
          await raf.flush();
        } finally {
          await raf.close();
        }
        _lastSync = DateTime.now();
      });
    } catch (_) {
      // не вышло — данные уже у системы
    }
  }

  /// 2026-10-29 («ничего не должно остаться от старого аккаунта»): журнал действий (и файлы `audit.*.jsonl` старых версий) стирается
  /// при выходе и смене аккаунта; накопленные, но не записанные строки сбрасываются.
  static Future<void> wipeAll({Directory? directory}) async {
    await _flushing;
    _pending.clear();
    currentUser = '';
    try {
      await _serial(() async {
        final dir = directory ?? testDirectory ?? await getApplicationDocumentsDirectory();
        await for (final e in dir.list()) {
          final name = e.uri.pathSegments.last;
          if (e is File && name.startsWith('audit') && name.endsWith('.jsonl')) await e.delete();
        }
      });
    } catch (_) {
      // журнал не стёрся — новые записи пойдут поверх
    }
  }

  /// Новые сверху. Лишнее сверх 5000 записей при чтении обрезается в файле.
  static Future<List<AuditEntry>> read() async {
    await _flushing;
    try {
      return await _serial(() async {
        final f = await _file();
        if (!await f.exists()) return <AuditEntry>[];
        final lines = await f.readAsLines();
        // 2026-10-22 (мобилка, админ): П16 — обрезка через временный файл и переименование: обрыв посреди записи
        // (выключили телефон) больше не оставляет пустой или половинный журнал.
        if (lines.length > _max) {
          final tmp = File('${f.path}.tmp');
          await tmp.writeAsString('${lines.sublist(lines.length - _max).join('\n')}\n', flush: true);
          await tmp.rename(f.path);
        }
        return [for (final l in lines.reversed.take(_max)) ?AuditEntry.fromJson(l)];
      });
    } catch (_) {
      return [];
    }
  }
}
