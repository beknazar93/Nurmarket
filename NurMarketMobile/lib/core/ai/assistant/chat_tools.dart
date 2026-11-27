// 2026-11-27 (просьба владельца «дай Нур-советнику доступы ко всем новым функциям и разделам … полный доступ»): сборка инструментов
// для ЧАТА советника из тех же частей, что у звонка: навигация (open_screen …), чтение (продажи, чеки, склад, долги, смены, заказы …) и
// изучение (explain_section, marketplace_info, tariff_info, app_settings_info, suppliers_info, whats_new). ТОЛЬКО ЧТЕНИЕ И НАВИГАЦИЯ:
// инструменты-действий (продажа, возврат, оплата, удаление, цены, настройки, включение модулей, сообщения покупателям) сюда не
// регистрируются, а ещё и режим вызова — «только смотреть», так что даже будущий забытый action-инструмент ответит view_only.
// Права роли — как у самих инструментов (allowed) и экранов (Session.canRoute); результат одного вызова ≤ 1500 знаков данных.

import '../../api/session_store.dart';
import '../chat_toolkit.dart';
import '../live_tools.dart';
import 'call_table.dart' show TableStore;
import 'knowledge_tools.dart';
import 'nav_tools.dart';
import 'read_tools.dart';
import 'tool_registry.dart';

/// Инструменты звонка, которые нужны только панели таблиц звонка и в чате не имеют смысла.
const _callOnlyTools = {'table_control', 'show_table'};

/// Реестр инструментов чата. [nav] null — раздел открыть нечем (тогда open_* не объявляются, остальное работает).
ToolRegistry buildChatRegistry({required NavHost? nav, required ReadSource reads, required KnowledgeSource knowledge}) {
  final reg = ToolRegistry(hideActionsInViewOnly: true);
  if (nav != null) registerNavTools(reg, nav);
  final tables = TableStore(); // таблицы чата не показываются: ответ модели собирает слова из data
  for (final t in buildReadTools(ReadContext(reads, tables))) {
    if (!_callOnlyTools.contains(t.name)) reg.register(t);
  }
  registerKnowledgeTools(reg, knowledge);
  return reg;
}

/// Набор для одного ответа чата: объявления по правам [session], исполнитель через реестр (права и режим проверяются ещё раз на каждом вызове).
/// [lastAsk] — вопрос пользователя: навигация сверяет выбор модели со словами человека (как в звонке).
ChatToolkit buildChatToolkit({
  required ToolRegistry registry,
  required Session Function() session,
  required String lang,
  required String lastAsk,
  String Function()? screen,
  String fallbackSystem = '',
  int maxCalls = 4,
  Duration callTimeout = const Duration(seconds: 5),
}) {
  final speech = UserSpeechLog()..add(lastAsk);
  final decls = registry.declarations(session(), AssistantMode.viewOnly);
  return ChatToolkit(
    declarations: decls,
    fallbackSystem: fallbackSystem,
    maxCalls: maxCalls,
    callTimeout: callTimeout,
    run: (name, args) async {
      final env = ToolEnv(session: session(), speech: speech, mode: AssistantMode.viewOnly, lang: lang, screen: screen?.call() ?? '');
      final res = await registry.execute(LiveToolCall('chat${DateTime.now().microsecondsSinceEpoch}', name, args), env);
      return res.toJson();
    },
  );
}
