// 2026-10-27, владелец: «в мобилке дополнительного ключа нет — добавь!». Список ключей Google Gemini из разных аккаунтов —
// как GeminiKeyPool кассы и программы владельца (бесплатный лимит Gemini считается на проект/аккаунт, а ключей можно до 10).
//
// ФОРМАТ ХРАНЕНИЯ (тот же, что на кассе — общий через секреты личных настроек NurCRM, api/users/app-settings/…/ → secrets):
//   • основной ключ — как раньше, секрет TelegramAiKey (его читают старые версии мобилки, касса, бот): ничего не ломается;
//   • остальные — секрет TelegramAiKeysExtra, строка JSON:
//     {"v":1,"mainLabel":"Аккаунт 1","mainEnabled":true,"extra":[{"label":"Аккаунт 2","key":"AIza…","enabled":true}]}.
//   Порядок списка: основной, затем extra по порядку. Не больше [GeminiKeys.maxKeys]; повторы и пустые отбрасываются.
// На телефоне то же самое лежит в защищённом хранилище: «ai.gemini» (основной) и «ai.gemini.extra» (строка JSON).
//
// Состояние «исчерпан до …» и «недействителен» (в [GeminiKeyPool]) хранится только в памяти: после перезапуска ключ пробуют
// заново (один лишний запрос). Квота Gemini считается по модели, поэтому «исчерпан» — на пару (ключ, модель).
import 'dart:convert';

/// Один ключ из списка.
class GeminiKeyEntry {
  final String label;
  final String key;
  final bool enabled;
  const GeminiKeyEntry({
    this.label = '',
    required this.key,
    this.enabled = true,
  });

  GeminiKeyEntry copyWith({String? label, String? key, bool? enabled}) =>
      GeminiKeyEntry(
        label: label ?? this.label,
        key: key ?? this.key,
        enabled: enabled ?? this.enabled,
      );

  /// Для журнала и показа: сам ключ никуда не пишется — только последние 4 символа.
  String get tail => GeminiKeys.tail(key);

  @override
  bool operator ==(Object other) =>
      other is GeminiKeyEntry &&
      other.label == label &&
      other.key == key &&
      other.enabled == enabled;
  @override
  int get hashCode => Object.hash(label, key, enabled);
}

class GeminiKeys {
  static const maxKeys = 10;

  /// Ключ Google не содержит пробелов и переводов строк; при вставке они часто прилипают (перенос строки, пробел с краю, неразрывный
  /// пробел, невидимые символы, кавычки вокруг). 2026-10-27 (владелец: «смена ключа не работает»): раньше обрезались только края.
  static String clean(Object? raw) {
    if (raw == null) return '';
    return raw
        .toString()
        .replaceAll(RegExp('[\\s ​-‏   ⁠﻿"\'“”‘’«»`]'), '')
        .replaceAll(RegExp(r'[\x00-\x1F\x7F]'), '');
  }

  /// Последние 4 символа (короткий ключ — «****»).
  static String tail(String key) =>
      key.length <= 4 ? '****' : key.substring(key.length - 4);

  /// Привести список к виду для хранения: ключи очищены, пустые и повторы отброшены, не больше [maxKeys], метки обрезаны.
  static List<GeminiKeyEntry> normalize(Iterable<GeminiKeyEntry> src) {
    final out = <GeminiKeyEntry>[];
    for (final e in src) {
      final k = clean(e.key);
      if (k.isEmpty || out.any((x) => x.key == k)) continue;
      out.add(
        GeminiKeyEntry(label: e.label.replaceAll(RegExp('[*•]'), '').trim(), key: k, enabled: e.enabled),
      );
      if (out.length >= maxKeys) break;
    }
    return out;
  }

  /// Разобрать основной ключ и строку TelegramAiKeysExtra. Битый JSON — только основной ключ (как касса).
  static List<GeminiKeyEntry> parse(String? mainKey, Object? extraJson) {
    var mainLabel = '';
    var mainEnabled = true;
    List? extra;
    final raw = extraJson is Map || extraJson is List
        ? jsonEncode(extraJson)
        : extraJson?.toString().trim();
    if (raw != null && raw.isNotEmpty) {
      try {
        final root = jsonDecode(raw);
        if (root is Map) {
          mainLabel = root['mainLabel']?.toString() ?? '';
          mainEnabled = root['mainEnabled'] != false;
          extra = root['extra'] is List ? root['extra'] as List : null;
        }
      } catch (_) {
        // дополнительный список не разобран — остаётся основной ключ
      }
    }
    final src = <GeminiKeyEntry>[];
    final main = clean(mainKey);
    if (main.isNotEmpty) {
      src.add(GeminiKeyEntry(label: mainLabel, key: main, enabled: mainEnabled));
    }
    for (final e in extra ?? const []) {
      if (e is! Map) continue;
      src.add(
        GeminiKeyEntry(
          label: e['label']?.toString() ?? '',
          key: clean(e['key']),
          enabled: e['enabled'] != false,
        ),
      );
    }
    return normalize(src);
  }

  /// Список → (основной ключ, строка TelegramAiKeysExtra). Пусто — оба null. Один ключ без метки и включённый — extra null
  /// (ровно как раньше: в облаке только TelegramAiKey).
  static (String?, String?) encode(List<GeminiKeyEntry> list) {
    final clean = normalize(list);
    if (clean.isEmpty) return (null, null);
    if (clean.length == 1 && clean.first.label.isEmpty && clean.first.enabled) {
      return (clean.first.key, null);
    }
    return (
      clean.first.key,
      jsonEncode({
        'v': 1,
        'mainLabel': clean.first.label,
        'mainEnabled': clean.first.enabled,
        'extra': [
          for (final e in clean.skip(1))
            {'label': e.label, 'key': e.key, 'enabled': e.enabled},
        ],
      }),
    );
  }
}

/// Состояние ключа для экрана.
enum GeminiKeyState { working, exhausted, invalid, disabled, unchecked }

/// Состояние ключей в памяти и выбор «первый рабочий» (как GeminiKeyPool кассы).
class GeminiKeyPool {
  /// Через сколько пробовать «недействительный» ключ снова.
  static const invalidRetry = Duration(minutes: 30);

  /// Часы — подменяются в тестах.
  static DateTime Function() clock = DateTime.now;

  static final Map<String, DateTime> _blocked = {}; // "ключ\nмодель" -> до
  static final Map<String, DateTime> _invalid = {}; // ключ -> до
  static final Set<String> _verified = {};

  static String _bk(String key, String model) => '$key\n$model';

  static void reset() {
    clock = DateTime.now;
    _blocked.clear();
    _invalid.clear();
    _verified.clear();
  }

  /// Забыть отметки одного ключа («Проверить» прошла, ключ заново вставили).
  static void clear(String key) {
    _invalid.remove(key);
    _blocked.removeWhere((k, _) => k.startsWith('$key\n'));
  }

  static bool isInvalid(String key) {
    final u = _invalid[key];
    return u != null && u.isAfter(clock());
  }

  /// Когда ключ снова будет пробоваться после «недействителен» (null — не помечен).
  static DateTime? invalidUntil(String key) {
    final u = _invalid[key];
    return u != null && u.isAfter(clock()) ? u.toLocal() : null;
  }

  /// До какого времени ключ исчерпан для модели (null — не исчерпан).
  static DateTime? blockedUntil(String key, String model) {
    final now = clock();
    DateTime? best;
    for (final m in [model, '*']) {
      final u = _blocked[_bk(key, m)];
      if (u != null && u.isAfter(now) && (best == null || u.isAfter(best))) {
        best = u;
      }
    }
    return best?.toLocal();
  }

  static bool isBlocked(String key, String model) {
    final now = clock();
    final u = _blocked[_bk(key, model)];
    if (u != null && u.isAfter(now)) return true;
    final all =
        _blocked[_bk(
          key,
          '*',
        )]; // лимит, пойманный проверкой ключа (без модели)
    return all != null && all.isAfter(now);
  }

  /// Ключ для запроса к модели: первый включённый, не недействительный и не исчерпанный для этой модели. [retired] — ключи, которые
  /// пользователь заменил или Google не принял насовсем (не берём). null — свободных нет.
  static String? pick(
    String model,
    List<GeminiKeyEntry> list, {
    bool Function(String key)? retired,
  }) {
    for (final e in list) {
      if (!e.enabled ||
          (retired?.call(e.key) ?? false) ||
          isInvalid(e.key) ||
          isBlocked(e.key, model)) {
        continue;
      }
      return e.key;
    }
    return null;
  }

  /// Ключ «вообще»: первый включённый, не недействительный, у которого свободна хоть одна из [models]. Если все исчерпаны — первый
  /// включённый не недействительный (запрос всё равно стоит сделать: лимит мог освободиться, а ошибку Google объяснит).
  static String? pickAny(
    List<GeminiKeyEntry> list,
    Iterable<String> models, {
    bool Function(String key)? retired,
  }) {
    bool usable(GeminiKeyEntry e) =>
        e.enabled && !(retired?.call(e.key) ?? false) && !isInvalid(e.key);
    for (final e in list) {
      if (usable(e) && models.any((m) => !isBlocked(e.key, m))) return e.key;
    }
    for (final e in list) {
      if (usable(e)) return e.key;
    }
    return null;
  }

  /// Разобрать неудачный ответ Google. true — ключ теперь помечен (лимит или недействителен): запрос стоит повторить со следующим
  /// ключом. Ответы других видов (5xx, 404 модели, 400 запроса) ничего не меняют.
  static bool reportFailure(String key, String model, int status, String body) {
    if (key.isEmpty) return false;
    if (isQuota(status, body)) {
      _blocked[_bk(key, model)] = quotaUntil(body, clock());
      return true;
    }
    if (isInvalidKey(status, body)) {
      _invalid[key] = clock().add(invalidRetry);
      return true;
    }
    return false;
  }

  /// Звонок (Live): отказ по лимиту / ключу при открытии соединения — те же отметки, что у обычных запросов.
  static void markQuota(String key, String model, String body) =>
      _blocked[_bk(key, model)] = quotaUntil(body, clock());

  static void markInvalid(String key) =>
      _invalid[key] = clock().add(invalidRetry);

  static void reportSuccess(String key) {
    _invalid.remove(key);
    _verified.add(key);
  }

  static bool isVerified(String key) => _verified.contains(key);

  static bool isQuota(int status, String body) {
    final b = body.toLowerCase();
    return status == 429 ||
        b.contains('resource_exhausted') ||
        (status == 403 && b.contains('quota'));
  }

  /// 2026-10-27 (расследование «ИИ не отвечает»): ключ считается недействительным ТОЛЬКО при явной причине от Google — 401 или
  /// 400/403 с текстом «API key not valid / expired / not found», API_KEY_INVALID, leaked, suspended, либо PERMISSION_DENIED
  /// именно по ключу. Любой другой 403 («The caller does not have permission» и т. п.) раньше выключал ключ на 30 минут, и чат
  /// отвечал «добавьте ключ», хотя ключ рабочий; теперь такая ошибка ключ не помечает — пробуется следующая пара ключ + модель.
  static bool isInvalidKey(int status, String body) {
    if (status == 401) return true;
    if (status != 400 && status != 403) return false;
    return isInvalidKeyText(body);
  }

  /// Текст (тело ответа Google или причина закрытия звонка) явно говорит, что виноват ключ.
  static bool isInvalidKeyText(String text) {
    final b = text.toLowerCase();
    return b.contains('api_key_invalid') ||
        b.contains('api key not valid') ||
        b.contains('api key expired') ||
        b.contains('leaked') ||
        b.contains('consumer_suspended') ||
        b.contains('consumer_invalid') ||
        b.contains('has been suspended') ||
        b.contains('api key not found') ||
        (b.contains('permission_denied') &&
            (b.contains('api key') || b.contains('api_key')));
  }

  /// До какого момента ключ исчерпан. Суточный лимит (в ответе PerDay) — до полуночи по тихоокеанскому времени (так Google сбрасывает
  /// сутки); иначе retryDelay из ответа (10 с … сутки) или 60 с.
  static DateTime quotaUntil(String body, DateTime now) {
    if (RegExp(r'PerDay|per day|daily', caseSensitive: false).hasMatch(body)) {
      return nextPacificMidnight(now);
    }
    var delay = const Duration(seconds: 60);
    final m = RegExp(r'"retryDelay"\s*:\s*"([0-9.]+)s"').firstMatch(body);
    final s = m == null ? null : double.tryParse(m.group(1)!);
    if (s != null) delay = Duration(seconds: (s.ceil() + 1).clamp(10, 86400));
    return now.add(delay);
  }

  /// Смещение тихоокеанского времени от UTC: −7 ч летом (со 2-го воскресенья марта 2:00 до 1-го воскресенья ноября 2:00), иначе −8 ч.
  static Duration pacificOffset(DateTime utc) {
    final u = utc.toUtc();
    DateTime nthSunday(int month, int n) {
      var d = DateTime.utc(u.year, month, 1);
      while (d.weekday != DateTime.sunday) {
        d = d.add(const Duration(days: 1));
      }
      return d.add(Duration(days: 7 * (n - 1)));
    }

    final start = nthSunday(
      3,
      2,
    ).add(const Duration(hours: 10)); // 2:00 PST = 10:00 UTC
    final end = nthSunday(
      11,
      1,
    ).add(const Duration(hours: 9)); // 2:00 PDT = 09:00 UTC
    return !u.isBefore(start) && u.isBefore(end)
        ? const Duration(hours: -7)
        : const Duration(hours: -8);
  }

  /// Следующая полночь по тихоокеанскому времени (момент по часам телефона).
  static DateTime nextPacificMidnight(DateTime now) {
    final u = now.toUtc();
    final off = pacificOffset(u);
    final local = u.add(off);
    final midnightLocal = DateTime.utc(local.year, local.month, local.day + 1);
    final result = midnightLocal.subtract(
      pacificOffset(midnightLocal.subtract(off)),
    );
    return result.toLocal();
  }

  /// Состояние ключа для экрана и момент, до которого он исчерпан (по самой поздней модели).
  static (GeminiKeyState, DateTime?) stateOf(
    GeminiKeyEntry e, {
    bool retired = false,
  }) {
    if (!e.enabled) return (GeminiKeyState.disabled, null);
    if (retired || isInvalid(e.key)) return (GeminiKeyState.invalid, null);
    final now = clock();
    DateTime? latest;
    _blocked.forEach((k, until) {
      if (until.isAfter(now) &&
          k.startsWith('${e.key}\n') &&
          (latest == null || until.isAfter(latest!))) {
        latest = until;
      }
    });
    if (latest != null) return (GeminiKeyState.exhausted, latest!.toLocal());
    return (
      isVerified(e.key) ? GeminiKeyState.working : GeminiKeyState.unchecked,
      null,
    );
  }

  /// «Рабочих ключей: N из M»: N — включённые, не исчерпанные и не недействительные.
  static (int, int) counts(
    List<GeminiKeyEntry> list, {
    bool Function(String key)? retired,
  }) {
    var ok = 0;
    for (final e in list) {
      final s = stateOf(e, retired: retired?.call(e.key) ?? false).$1;
      if (s == GeminiKeyState.working || s == GeminiKeyState.unchecked) ok++;
    }
    return (ok, list.length);
  }

  /// Время ближайшего освобождения для сообщения «все ключи исчерпаны»; null — никто не блокировался.
  static DateTime? nextFree(List<GeminiKeyEntry> list, String model) {
    final now = clock();
    DateTime? best;
    for (final e in list.where((e) => e.enabled)) {
      for (final m in [model, '*']) {
        final u = _blocked[_bk(e.key, m)];
        if (u != null && u.isAfter(now) && (best == null || u.isBefore(best))) {
          best = u;
        }
      }
    }
    return best?.toLocal();
  }
}
