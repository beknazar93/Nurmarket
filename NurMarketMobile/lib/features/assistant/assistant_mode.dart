// 2026-10-26 (голос-управление, В4): настройка «режим помощника» (раздел 2.3 ТЗ). settings: `assistant.mode` — общий на телефоне,
// `assistant.mode.<userId>` — у сотрудника; действует БОЛЕЕ СТРОГИЙ из двух. По умолчанию — «Выполнять по просьбе» (act).
// Голосом режим не меняется (инструмента нет). Экран «Помощник» (/ai/settings) — пакет В5, он пользуется этим же классом.
import '../../core/ai/assistant/tool_registry.dart' show AssistantMode;
import '../../core/db/database.dart';

class AssistantModePrefs {
  static const sharedKey = 'assistant.mode';
  static String userKey(String userId) => 'assistant.mode.$userId';

  /// Режим сотрудника: строже из общего и своего.
  static Future<AssistantMode> load(AppDatabase db, String userId) async {
    try {
      final shared = AssistantMode.parse(await db.getSetting(sharedKey));
      final own = userId.isEmpty ? AssistantMode.defaultMode : AssistantMode.parse(await db.getSetting(userKey(userId)));
      return AssistantMode.stricter(shared, own);
    } catch (_) {
      return AssistantMode.defaultMode;
    }
  }

  /// 2026-10-26 (В5): общий режим телефона (его задаёт владелец или администратор) — экран «Помощник» показывает его сотруднику.
  static Future<AssistantMode> loadShared(AppDatabase db) async {
    try {
      return AssistantMode.parse(await db.getSetting(sharedKey));
    } catch (_) {
      return AssistantMode.defaultMode;
    }
  }

  /// 2026-10-26 (В5): собственный режим сотрудника (без общего) — для экрана «Помощник».
  static Future<AssistantMode> loadOwn(AppDatabase db, String userId) async {
    try {
      return userId.isEmpty ? AssistantMode.defaultMode : AssistantMode.parse(await db.getSetting(userKey(userId)));
    } catch (_) {
      return AssistantMode.defaultMode;
    }
  }

  /// [userId] null — общий режим телефона (владелец или администратор), иначе — свой режим сотрудника.
  static Future<void> save(AppDatabase db, AssistantMode mode, {String? userId}) =>
      db.setSetting(userId == null ? sharedKey : userKey(userId), mode.wire);
}
