import 'dart:convert';

import '../api/api_client.dart';
import '../db/database.dart';

/// 2026-10-27 (владелец: «на фото существующий товар не находит»; расследование, раздел 4): «как назвал ИИ → какой это товар».
/// Кассир выбрал вариант «Это «…»?» — название, которым ИИ назвал упаковку, запоминается алиасом товара, и в следующий раз
/// матчер ([ProductMatcher]) даёт этому названию 1,0. Алиасы общие для компании: ключ `CamAliases` в
/// api/main/app-settings/nurmarket-mobile/ (тот же механизм, что AiAccess: PATCH меняет только свой ключ), копия на телефоне —
/// настройка `cam.aliases` (работает без связи). Запись на сервер — чтение, слияние, запись: чужие алиасы не затираются,
/// при споре о порядке выигрывает последний. Нет права записи (401/403) или нет связи — алиасы остаются на телефоне.
class CamAliases {
  static const path = 'api/main/app-settings/nurmarket-mobile/';
  static const key = 'CamAliases';
  static const localKey = 'cam.aliases';

  /// 2026-10-27 (ревью М-3): отмена запомненного названия. Удалённое нельзя просто стереть — с сервера или другого телефона оно
  /// вернулось бы слиянием; поэтому удаления хранятся отметками («удалено») рядом: на телефоне и на сервере (ключ `CamAliasesRemoved`).
  /// Повторное «запомнить» того же названия снимает отметку.
  static const removedKey = 'CamAliasesRemoved';
  static const localRemovedKey = 'cam.aliases.removed';
  static const maxRemovedPerProduct = 20;

  /// Не больше названий на товар и товаров в общем списке (размер настройки ≤ ~200 КБ).
  static const maxPerProduct = 5;
  static const maxProducts = 3000;

  /// Алиасы в памяти: id товара → названия (старые первыми). Читает матчер.
  static Map<String, List<String>> current = {};

  /// Отметки «удалено»: id товара → названия, которые нельзя возвращать слиянием.
  static Map<String, List<String>> removed = {};

  /// Растёт при каждом изменении — кэш матчера по нему понимает, что пора перестроиться.
  static int version = 0;

  static bool _loaded = false;
  static DateTime _syncedAt = DateTime(2000);
  static bool _writeDenied = false;

  /// 2026-10-29 (полная изоляция аккаунтов): названия, которые ИИ камеры выучил в магазине прошлого аккаунта, не переходят к новому.
  static void forgetAccount() => resetForTest();

  static void resetForTest() {
    current = {};
    removed = {};
    version = 0;
    _loaded = false;
    _syncedAt = DateTime(2000);
    _writeDenied = false;
  }

  /// Название в одном виде: нижний регистр, пробелы по одному, не длиннее 120 знаков.
  static String normalize(String s) {
    final t = s.toLowerCase().replaceAll(RegExp(r'\s+'), ' ').trim();
    return t.length > 120 ? t.substring(0, 120).trim() : t;
  }

  /// Разбор значения настройки (Map или строка JSON): мусор и пустое отбрасываются.
  static Map<String, List<String>> parse(Object? raw, {int limit = maxPerProduct}) {
    try {
      final v = raw is String ? (raw.trim().isEmpty ? null : jsonDecode(raw)) : raw;
      if (v is! Map) return {};
      final out = <String, List<String>>{};
      for (final e in v.entries) {
        final id = '${e.key}'.trim();
        if (id.isEmpty || e.value is! List) continue;
        final list = <String>[];
        for (final a in e.value as List) {
          final n = normalize('$a');
          if (n.length >= 3 && !list.contains(n)) list.add(n);
        }
        if (list.isNotEmpty) out[id] = list.length > limit ? list.sublist(list.length - limit) : list;
      }
      return out;
    } catch (_) {
      return {};
    }
  }

  /// Слияние без затирания: [older] — то, что было, [newer] — свежее. Названия объединяются, повторы убираются,
  /// самые свежие — в конце; на товар остаётся не больше [maxPerProduct] последних, в списке — не больше [maxProducts] товаров.
  static Map<String, List<String>> merge(Map<String, List<String>> older, Map<String, List<String>> newer, {int limit = maxPerProduct}) {
    final out = <String, List<String>>{
      for (final e in older.entries) e.key: [...e.value],
    };
    for (final e in newer.entries) {
      final list = out.putIfAbsent(e.key, () => []);
      for (final a in e.value) {
        list.remove(a);
        list.add(a);
      }
      if (list.length > limit) out[e.key] = list.sublist(list.length - limit);
    }
    if (out.length > maxProducts) {
      final keep = out.keys.toList().sublist(out.length - maxProducts);
      return {for (final k in keep) k: out[k]!};
    }
    return out;
  }

  static bool _same(Map<String, List<String>> a, Map<String, List<String>> b) => jsonEncode(a) == jsonEncode(b);

  static void _set(Map<String, List<String>> v) {
    if (_same(current, v)) return;
    current = v;
    version++;
  }

  /// [src] без названий, отмеченных «удалено» в [gone].
  static Map<String, List<String>> subtract(Map<String, List<String>> src, Map<String, List<String>> gone) {
    final out = <String, List<String>>{};
    for (final e in src.entries) {
      final g = gone[e.key];
      final list = g == null ? e.value : e.value.where((a) => !g.contains(a)).toList();
      if (list.isNotEmpty) out[e.key] = list;
    }
    return out;
  }

  /// Слияние отметок «удалено»: на товар не больше [maxRemovedPerProduct] последних.
  static Map<String, List<String>> _mergeRemoved(Map<String, List<String>> a, Map<String, List<String>> b) {
    return merge(a, b, limit: maxRemovedPerProduct);
  }

  /// Отметки без названия [n] у товара [productId] (оно запоминается заново).
  static Map<String, List<String>> _withoutMark(Map<String, List<String>> m, String productId, String n) {
    final out = <String, List<String>>{};
    for (final e in m.entries) {
      final list = e.key == productId ? e.value.where((a) => a != n).toList() : e.value;
      if (list.isNotEmpty) out[e.key] = list;
    }
    return out;
  }

  /// Тело записи на сервер: алиасы и (если есть) отметки «удалено».
  static Map<String, dynamic> _body(Map<String, List<String>> v) => {key: v, if (removed.isNotEmpty) removedKey: removed};

  /// Копия с телефона — один раз за запуск (быстро, без сети).
  static Future<void> ensureLocal(AppDatabase db) async {
    if (_loaded) return;
    _loaded = true;
    try {
      removed = _mergeRemoved(removed, parse(await db.getSetting(localRemovedKey), limit: maxRemovedPerProduct));
      _set(subtract(merge(current, parse(await db.getSetting(localKey))), removed));
    } catch (_) {}
  }

  /// Копия с телефона (быстро), потом сервер — не чаще раза в 10 минут: чужие алиасы подтягиваются, свои локальные уходят.
  static Future<void> ensure(AppDatabase db, ApiClient api, {bool force = false}) async {
    await ensureLocal(db);
    if (!force && DateTime.now().difference(_syncedAt) < const Duration(minutes: 10)) return;
    _syncedAt = DateTime.now();
    final r = await _readRemote(api);
    if (r == null) return;
    removed = _mergeRemoved(r.removed, removed);
    final merged = subtract(merge(r.aliases, current), removed);
    _set(merged);
    await _saveLocal(db);
    if (!_same(r.aliases, merged) || !_same(r.removed, removed)) await _writeRemote(api, merged);
  }

  /// Запомнить: название [text], которым ИИ назвал упаковку, — это товар [productId]. true — ушло и на сервер (для всех
  /// телефонов компании), false — осталось на этом телефоне.
  static Future<bool> add(AppDatabase db, ApiClient api, String productId, String text) async {
    final n = normalize(text);
    if (productId.isEmpty || n.length < 3) return false;
    await ensureLocal(db);
    // повторное «запомнить» снимает отметку «удалено» с этого названия
    removed = _withoutMark(removed, productId, n);
    _set(merge(current, {
      productId: [n],
    }));
    await _saveLocal(db);
    final r = await _readRemote(api);
    if (r == null) return false;
    removed = _mergeRemoved(_withoutMark(r.removed, productId, n), removed);
    final merged = subtract(merge(r.aliases, current), removed);
    _set(merged);
    await _saveLocal(db);
    return _writeRemote(api, merged);
  }

  /// 2026-10-27 (ревью М-3): отменить запомненное название ([text]) или все названия товара (без [text]). Ставит отметку
  /// «удалено» и отправляет её на сервер, чтобы название не вернулось слиянием через 10 минут. true — отметка ушла на сервер.
  static Future<bool> remove(AppDatabase db, ApiClient api, String productId, {String? text}) async {
    await ensureLocal(db);
    final have = current[productId] ?? const <String>[];
    final gone = text == null ? have : [normalize(text)];
    if (gone.isEmpty) return false;
    removed = _mergeRemoved(removed, {productId: [...gone]});
    _set(subtract(current, removed));
    await _saveLocal(db);
    final r = await _readRemote(api);
    if (r == null) return false;
    removed = _mergeRemoved(r.removed, removed);
    final merged = subtract(merge(r.aliases, current), removed);
    _set(merged);
    await _saveLocal(db);
    return _writeRemote(api, merged);
  }

  /// Забыть названия товара (товар удалён или «Сбросить обучение»): на телефоне и, если дали [api], на сервере.
  static Future<void> forget(AppDatabase db, String productId, {ApiClient? api}) async {
    if (api != null) {
      await remove(db, api, productId);
      return;
    }
    await ensureLocal(db);
    final have = current[productId];
    if (have == null) return;
    removed = _mergeRemoved(removed, {productId: [...have]});
    _set(subtract(current, removed));
    await _saveLocal(db);
  }

  static Future<void> _saveLocal(AppDatabase db) async {
    try {
      await db.setSetting(localKey, jsonEncode(current));
      await db.setSetting(localRemovedKey, jsonEncode(removed));
    } catch (_) {}
  }

  static Future<({Map<String, List<String>> aliases, Map<String, List<String>> removed})?> _readRemote(ApiClient api) async {
    try {
      final data = await api.get(path, timeout: const Duration(seconds: 5)).timeout(const Duration(seconds: 6));
      final settings = data is Map ? data['settings'] : null;
      return (aliases: parse(settings is Map ? settings[key] : null), removed: parse(settings is Map ? settings[removedKey] : null, limit: maxRemovedPerProduct));
    } catch (_) {
      return null; // нет связи
    }
  }

  static Future<bool> _writeRemote(ApiClient api, Map<String, List<String>> v) async {
    if (_writeDenied) return false;
    try {
      await api.patch(path, body: _body(v));
      return true;
    } on ApiException catch (e) {
      // нет права записи общих настроек — дальше в этом сеансе только на телефоне
      if (e.status == 401 || e.status == 403) _writeDenied = true;
      return false;
    } catch (_) {
      return false;
    }
  }
}
