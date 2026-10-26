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
  static const kinds = ['sale', 'cart', 'return', 'discount', 'delete', 'price', 'product', 'receive', 'cash', 'shift', 'settings', 'login', 'open', 'ai'];

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

  static Future<void> _flush() async {
    try {
      await Future<void>.delayed(Duration.zero); // дать накопиться записям этого же такта
      final f = await _file();
      while (_pending.isNotEmpty) {
        final batch = _pending.join();
        _pending.clear();
        try {
          await f.writeAsString(batch, mode: FileMode.append, flush: true);
        } catch (_) {
          // журнал не записался — продажа важнее
        }
      }
    } finally {
      _flushing = null;
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
