import 'dart:convert';

import 'package:flutter/widgets.dart' show BuildContext;

import 'api/api_client.dart';
import 'db/database.dart';
import 'util/ui.dart' show L10nX;

/// 2026-11-05 (решение владельца: «язык в ИИ кроме кыргызского и русского не включай, пока не купят языковой пакет»).
/// Как `LanguagePackGate` кассы: русский и кыргызский доступны всегда, английский/турецкий/узбекский — только если у компании
/// на сервере включён пакет «Язык» (`GET api/users/company/addons/` → `[{code: "kassa-language", active, until}]`, тот же ответ, что читает касса).
/// Нет связи — работает «последнее известное» (кэш в настройках телефона; кэш старше 7 дней доступ не продлевает; кэш чужого аккаунта не действует).
/// Без пакета сохранённый платный язык сбрасывается на русский при запуске и при потере пакета (`PrefsController.load`).
class LanguagePackGate {
  LanguagePackGate._();

  /// Бесплатные языки интерфейса и ИИ.
  static const freeLanguages = {'ru', 'ky'};

  /// Серверный код платной функции (как в кассе: `kassa-` + `language`).
  static const addonCode = 'kassa-language';

  /// 2026-11-05: код пакета в ответе сервера ceba67c (`users/company/addons/` читают все роли).
  static const addonCodeServer = 'language';

  static const _cacheKey = 'addons.language';
  static const _cacheTtl = Duration(days: 7);

  static bool _granted = false;

  /// Пакет «Язык» действует (по серверу или по последнему известному ответу).
  static bool get granted => _granted;

  /// Язык платный (нужен пакет).
  static bool isPremium(String code) => !freeLanguages.contains(code);

  /// Язык можно включить сейчас.
  static bool isAvailable(String code) => !isPremium(code) || _granted;

  /// Язык, который можно использовать: платный без пакета → русский.
  static String effective(String code) => isAvailable(code) ? code : 'ru';

  /// Разобрать ответ сервера: пакет включён, если есть запись [addonCode] с active = true и сроком не в прошлом.
  static bool parseGranted(dynamic data, {DateTime? now}) {
    final today = now ?? DateTime.now();
    // 2026-11-05 (сервер ceba67c, п. 11): код языкового пакета теперь `language` (`{code:"language", active:true}`); прежний `kassa-language` поддержан.
    // Несколько записей — пакет действует, если действует любая.
    for (final m in asList(data)) {
      final code = (str(m, 'code') ?? '').trim().toLowerCase();
      if (code != addonCode && code != addonCodeServer) continue;
      final active = m['active'] == true || '${m['active']}'.toLowerCase() == 'true';
      if (!active) continue;
      final until = DateTime.tryParse(str(m, 'until') ?? '');
      if (until != null && DateTime(until.year, until.month, until.day, 23, 59, 59).isBefore(today)) continue;
      return true;
    }
    return false;
  }

  /// Прочитать последнее известное состояние пакета для аккаунта [owner] (вызывать до чтения языка).
  static Future<void> loadCached(AppDatabase db, String? owner) async {
    _granted = false;
    try {
      final raw = await db.getSetting(_cacheKey);
      if (raw == null || raw.isEmpty) return;
      final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
      // кэш другого аккаунта ничего не открывает; owner == null — запуск приложения (сессия ещё не прочитана), доверяем кэшу единственного входа
      if (owner != null && (m['owner'] ?? '') != owner) return;
      final at = DateTime.tryParse('${m['at']}');
      if (at == null || DateTime.now().difference(at) > _cacheTtl) return;
      _granted = m['granted'] == true;
    } catch (_) {
      // испорченный кэш — как будто пакета нет
    }
  }

  /// Спросить сервер. Возвращает: true/false — свежий ответ; null — не удалось (остаётся последнее известное).
  /// При ошибке (нет связи, 401/404/5xx) ничего не меняется и ничего не показывается.
  static Future<bool?> refresh(ApiClient api, AppDatabase db, String owner) async {
    try {
      final data = await api.get('api/users/company/addons/').timeout(const Duration(seconds: 6));
      final g = parseGranted(data);
      _granted = g;
      await db.setSetting(_cacheKey, jsonEncode({'granted': g, 'owner': owner, 'at': DateTime.now().toIso8601String()}));
      return g;
    } catch (_) {
      return null;
    }
  }

  /// Выход / смена аккаунта: пакет прошлого аккаунта не действует.
  static Future<void> reset(AppDatabase db) async {
    _granted = false;
    try {
      await db.setSetting(_cacheKey, '');
    } catch (_) {}
  }

  /// Только для тестов.
  static void debugSet(bool granted) => _granted = granted;
}

/// Подсказка у заблокированного языка / в ответе ИИ (5 языков, без правки сгенерированных файлов l10n).
String languagePackMissingText(BuildContext c) => c.t5(
  'Языковой пакет не подключён',
  'Тил топтому туташтырылган эмес',
  'Til paketi ulanmagan',
  'Language pack is not connected',
  'Dil paketi bağlı değil',
);
