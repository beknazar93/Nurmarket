// 2026-11-27 (просьба владельца «дай Нур-советнику доступы ко всем новым функциям и разделам»): инструменты (function calling Gemini)
// для текстового чата Нур Советника. Класс без Flutter и сети: объявления инструментов, исполнитель, предел вызовов на один ответ,
// срок одного вызова и список использованных инструментов для строки «Использовано: …» под ответом.
// Раньше (27.10) в чате инструментов не было вообще; теперь они добавляются, только когда вопрос похож на «открой / объясни / покажи
// раздел» (advisorNeedsTools), — обычные вопросы идут прежним быстрым потоковым путём без объявлений.
import 'dart:async';

/// Набор инструментов одного ответа чата. Создаётся заново на каждый вопрос (счётчики вызовов и список использованных — свои).
class ChatToolkit {
  /// Объявления для Gemini (`tools[0].functionDeclarations`).
  final List<Map<String, dynamic>> declarations;

  /// Исполнитель одного вызова: имя + аргументы → ответ инструмента (status / say / data …).
  final Future<Map<String, dynamic>> Function(String name, Map<String, dynamic> args) run;

  /// Системная инструкция БЕЗ инструментов — для запасных ИИ (Groq, OpenRouter), которые функции не вызывают: иначе они писали бы
  /// «открыла», ничего не открыв.
  final String fallbackSystem;

  /// Не больше этого числа вызовов на один ответ; дальше модели отвечают «лимит» и инструменты в запросе не объявляются.
  final int maxCalls;

  /// Срок одного вызова: не ответил — модель получает «нет данных» и отвечает по тому, что есть (не ждём 25 с).
  final Duration callTimeout;

  ChatToolkit({
    required this.declarations,
    required this.run,
    this.fallbackSystem = '',
    this.maxCalls = 4,
    this.callTimeout = const Duration(seconds: 5),
  });

  /// Какие инструменты реально вызывались (по порядку, без повторов) — для строки «Использовано: …».
  final List<String> used = [];
  int calls = 0;

  bool get exhausted => calls >= maxCalls;

  /// Выполнить вызов модели с защитой: лимит, срок, любое исключение — ответом инструмента, наружу ничего не летит.
  Future<Map<String, dynamic>> execute(String name, Map<String, dynamic> args) async {
    if (exhausted) {
      return {'status': 'error', 'say': 'Лимит вызовов инструментов на этот ответ исчерпан — ответь по тому, что уже есть.'};
    }
    calls++;
    if (!used.contains(name)) used.add(name);
    try {
      return await run(name, args).timeout(callTimeout);
    } on TimeoutException {
      return {'status': 'offline', 'say': 'Данные не успели загрузиться — скажи, что сейчас их нет.'};
    } catch (_) {
      return {'status': 'error', 'say': 'Не получилось выполнить.'};
    }
  }
}
