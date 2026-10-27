// 2026-10-27 (расследование «ИИ очень долго думает»): журнал замеров ИИ по этапам — `ai_perf.log` в папке приложения (последние
// 200 строк) и кольцо в памяти. Видно, где уходит время: разбор намерения, сводка магазина (контекст), выбор ключа и модели,
// ответ ИИ, открытие экрана. В журнал попадают ТОЛЬКО название события, миллисекунды и короткие метки (путь «нав./ИИ», ключ экрана,
// имя модели) — без текста вопросов и ответов, без ключей Gemini и без данных магазина.
import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';

import '../../util/audit.dart';
import 'ai_journal.dart';

class AiPerfEntry {
  final DateTime at;
  final String kind;
  final Map<String, int> ms;
  final Map<String, String> tags;
  const AiPerfEntry(this.at, this.kind, this.ms, this.tags);

  /// Одна строка журнала: «2026-10-27T10:00:00 chat.llm ctx=640ms ask=2100ms total=2800ms model=gemini-3.5-flash-lite».
  String get line => [
    at.toIso8601String().split('.').first,
    kind,
    for (final e in ms.entries) '${e.key}=${e.value}ms',
    for (final e in tags.entries) '${e.key}=${e.value}',
  ].join(' ');
}

/// Секундомер этапов одного ответа: `mark('ctx')` — сколько прошло с прошлой метки; `finish()` пишет итог.
class AiPerfTrace {
  final String kind;
  final Map<String, String> tags = {};
  final Map<String, int> _ms = {};
  final Stopwatch _all = Stopwatch()..start();
  int _last = 0;
  AiPerfTrace(this.kind);

  /// Закрыть этап [stage]: время от прошлой метки (или от начала).
  void mark(String stage) {
    final now = _all.elapsedMilliseconds;
    _ms[stage] = now - _last;
    _last = now;
  }

  void tag(String key, String value) => tags[key] = value;

  /// Записать готовое значение этапа (мс), если время измерено снаружи.
  void ms(String stage, int value) => _ms[stage] = value;

  /// Записать итог (`total`) в журнал; повторный вызов ничего не делает.
  void finish([String? outcome]) {
    if (_done) return;
    _done = true;
    _ms['total'] = _all.elapsedMilliseconds;
    if (outcome != null) tags['result'] = outcome;
    AiPerf.record(kind, _ms, tags);
  }

  bool _done = false;
}

class AiPerf {
  static const maxLines = 200;
  static final List<AiPerfEntry> recent = [];

  /// Только для тестов: папка вместо папки приложения.
  static Directory? testDirectory;

  /// Дублировать строки в консоль отладки (в тестах выключается).
  static bool echo = true;

  /// Выключить запись на диск (тесты без папки приложения).
  static bool toDisk = true;

  static void record(String kind, Map<String, int> ms, [Map<String, String> tags = const {}]) {
    final e = AiPerfEntry(DateTime.now(), kind, Map.of(ms), Map.of(tags));
    recent.add(e);
    while (recent.length > 100) {
      recent.removeAt(0);
    }
    if (kDebugMode && echo) debugPrint('[ai_perf] ${e.line}');
    if (toDisk) unawaited(_append(e.line));
  }

  static Future<void> _chain = Future.value();

  static Future<void> _append(String line) {
    final r = _chain.then((_) async {
      try {
        final dir = testDirectory ?? AiJournal.testDirectory ?? Audit.testDirectory ?? await getApplicationDocumentsDirectory();
        final f = File('${dir.path}/ai_perf.log');
        final old = await f.exists() ? await f.readAsLines() : <String>[];
        final lines = [...old, line];
        await f.writeAsString('${(lines.length > maxLines ? lines.sublist(lines.length - maxLines) : lines).join('\n')}\n', flush: true);
      } catch (_) {
        // журнал замеров не должен мешать ответу
      }
    });
    _chain = r.then((_) {}, onError: (_) {});
    return r;
  }

  /// Для тестов: дождаться записи на диск.
  @visibleForTesting
  static Future<void> flush() => _chain;

  static void clearForTests() => recent.clear();
}
