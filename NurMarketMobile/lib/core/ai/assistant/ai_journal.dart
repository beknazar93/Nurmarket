// 2026-10-26 (голос-управление, В3а): журнал действий ИИ — `ai_actions.jsonl` на телефоне (до 2000 записей) и строка в журнал
// аудита вида `ai` (раздел 2.6 ТЗ). Пишется: время, пользователь, что сказал (фраза), инструмент, аргументы, итог, как отменить,
// режим. НЕ пишется: ключи, звук, телефоны и длинные цифровые последовательности (маскируются по значению, а не только по
// имени ключа). Сам `audit.dart` не правится — `'ai'` в список видов для фильтра добавит В5. На сервер журнал не уходит (Б-65).
import 'dart:convert';
import 'dart:io';

import 'package:path_provider/path_provider.dart';

import '../../util/audit.dart';
import 'tool_registry.dart' show ToolJournal;

class AiActionRecord {
  final DateTime at;
  final String user;
  final String tool;
  final String callId;

  /// Фраза пользователя (последняя реплика), цифры телефона замаскированы.
  final String said;
  final Map<String, dynamic> args;
  final String status;
  final String say;
  final double? amount;
  final bool reversible;

  /// Как отменить: старые значения, id документа, снимок (то, что положил инструмент). Без ключей и телефонов.
  final Map<String, dynamic> undo;
  final String mode;
  final String? model;
  const AiActionRecord({
    required this.at,
    required this.user,
    required this.tool,
    required this.callId,
    required this.said,
    required this.args,
    required this.status,
    required this.say,
    this.amount,
    this.reversible = false,
    this.undo = const {},
    this.mode = 'act',
    this.model,
  });

  Map<String, dynamic> toJson() => {
    'at': at.toIso8601String(),
    'user': user,
    'tool': tool,
    'call': callId,
    'said': said,
    'args': args,
    'status': status,
    'say': say,
    if (amount != null) 'amount': amount,
    'reversible': reversible,
    if (undo.isNotEmpty) 'undo': undo,
    'mode': mode,
    'model': ?model,
  };

  static AiActionRecord? fromLine(String line) {
    try {
      final m = jsonDecode(line) as Map<String, dynamic>;
      return AiActionRecord(
        at: DateTime.parse(m['at'] as String),
        user: '${m['user'] ?? ''}',
        tool: '${m['tool'] ?? ''}',
        callId: '${m['call'] ?? ''}',
        said: '${m['said'] ?? ''}',
        args: (m['args'] as Map?)?.cast<String, dynamic>() ?? const {},
        status: '${m['status'] ?? ''}',
        say: '${m['say'] ?? ''}',
        amount: (m['amount'] as num?)?.toDouble(),
        reversible: m['reversible'] == true,
        undo: (m['undo'] as Map?)?.cast<String, dynamic>() ?? const {},
        mode: '${m['mode'] ?? 'act'}',
        model: m['model'] as String?,
      );
    } catch (_) {
      return null;
    }
  }
}

class AiJournal {
  static const maxRecords = 2000;

  /// Только для тестов: папка вместо папки приложения (по умолчанию — та же, что у `Audit`).
  static Directory? testDirectory;

  static Future<File> _file() async =>
      File('${(testDirectory ?? Audit.testDirectory ?? await getApplicationDocumentsDirectory()).path}/ai_actions.jsonl');

  static Future<void> _chain = Future.value();

  /// Только для тестов: у каждого теста свои «поддельные» часы, и очередь записи с прошлого теста могла остаться незавершённой
  /// (следующее чтение журнала встало бы за ней). Сбрасываем очередь.
  static void resetQueueForTests() => _chain = Future.value();
  static Future<T> _serial<T>(Future<T> Function() f) {
    final r = _chain.then((_) => f());
    _chain = r.then((_) {}, onError: (_) {});
    return r;
  }

  int _written = 0;

  /// Записать действие. Сбой записи действию не мешает. Параллельно — строка в аудит вида `ai`.
  Future<void> record(AiActionRecord r) async {
    final safe = AiActionRecord(
      at: r.at,
      user: r.user,
      tool: r.tool,
      callId: r.callId,
      said: ToolJournal.maskDigits(r.said),
      args: ToolJournal.redact(r.args),
      status: r.status,
      say: ToolJournal.maskDigits(r.say),
      amount: r.amount,
      reversible: r.reversible,
      undo: ToolJournal.redact(r.undo),
      mode: r.mode,
      model: r.model,
    );
    try {
      await _serial(() async {
        final f = await _file();
        await f.writeAsString('${jsonEncode(safe.toJson())}\n', mode: FileMode.append, flush: true);
        if (++_written % 100 == 0) await _trim(f);
      });
    } catch (_) {
      // журнал не записался — действие важнее
    }
    try {
      await Audit.log('ai', '${safe.tool} · ${safe.say}', amount: safe.amount);
    } catch (_) {}
  }

  static Future<void> _trim(File f) async {
    final lines = await f.readAsLines();
    if (lines.length <= maxRecords) return;
    final tmp = File('${f.path}.tmp');
    await tmp.writeAsString('${lines.sublist(lines.length - maxRecords).join('\n')}\n', flush: true);
    await tmp.rename(f.path);
  }

  /// Новые сверху. [userId] — только записи этого сотрудника (сотрудник видит свои, владелец и администратор — все).
  Future<List<AiActionRecord>> read({int limit = 200, String? userId}) async {
    try {
      return await _serial(() async {
        final f = await _file();
        if (!await f.exists()) return <AiActionRecord>[];
        await _trim(f);
        final lines = await f.readAsLines();
        final out = <AiActionRecord>[];
        for (final l in lines.reversed) {
          final r = AiActionRecord.fromLine(l);
          if (r == null || (userId != null && r.user != userId)) continue;
          out.add(r);
          if (out.length >= limit) break;
        }
        return out;
      });
    } catch (_) {
      return [];
    }
  }
}
