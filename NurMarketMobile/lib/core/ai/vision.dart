import 'dart:convert';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../api/api_client.dart';
import '../db/database.dart';
import '../providers.dart';
import 'scale_reading.dart';

/// Что увидела камера на снимке.
class SeenItem {
  final String name;
  final String brand;
  final String size;
  final String barcode;
  final int count;
  final double labelPrice;
  const SeenItem({required this.name, this.brand = '', this.size = '', this.barcode = '', this.count = 1, this.labelPrice = 0});
  String get title => [brand, name, size].where((x) => x.trim().isNotEmpty).join(' ');
}

/// Увиденное + лучшие совпадения в каталоге (score 0..1; ≥ 0.99 — штрихкод совпал).
class SeenMatch {
  final SeenItem seen;
  final List<(Product, double)> candidates;
  const SeenMatch(this.seen, this.candidates);
  Product? get best => candidates.isNotEmpty && candidates.first.$2 >= 0.5 ? candidates.first.$1 : null;
}

class VisionException implements Exception {
  final String message;
  final bool noKey;
  const VisionException(this.message, {this.noKey = false});
  @override
  String toString() => message;
}

/// 13.10, доски Т9–Т10 «Умная камера» и вопрос владельца «умная камера при продаже в телефоне где?»: снимок → Gemini
/// (тот же ключ и те же модели, что у Нур Советника на кассе — TelegramAiChat: секрет TelegramAiKey из личных настроек
/// NurCRM api/users/app-settings/…; на телефоне его можно ввести и самому) → список товаров на снимке → совпадения
/// с каталогом телефона (штрихкод, затем слова названия). Ключ хранится в защищённом хранилище телефона.
class VisionService {
  final ApiClient api;
  final AppDatabase db;
  // 2026-10-22 (советник мобилки): [http] — для тестов на фейках (без сети).
  VisionService(this.api, this.db, {Dio? http})
    : _http = http ?? Dio(BaseOptions(connectTimeout: const Duration(seconds: 15), receiveTimeout: const Duration(seconds: 60)));

  static const _models = ['gemini-3.5-flash-lite', 'gemini-flash-lite-latest', 'gemini-3.1-flash-lite', 'gemini-flash-latest', 'gemini-3.5-flash'];
  static const _storage = FlutterSecureStorage();
  static const _keyName = 'ai.gemini';
  static String? _cached;
  static String? _workingModel;
  final Dio _http;

  /// 2026-10-22 (советник мобилки): сброс запомненных модели и шагов «размышлений» — для тестов.
  @visibleForTesting
  static void resetForTest() {
    _workingModel = null;
    _thinkStep.clear();
    _cached = null;
    _cachedFor = null;
    _cachedAt = DateTime(2000);
    _noKeyUntil = DateTime(2000);
    _bad = null; // перечитать из хранилища телефона (как после перезапуска)
    _groq = null;
    _openRouter = null;
    _fallbackLoaded = false;
  }

  // 2026-10-22 (советник мобилки, аудит С-1…С-4): где искать ключ и что уже известно.
  static const _sharedKeyName = 'ai.gemini.shared';
  static const _badKeysName = 'ai.gemini.bad';
  static DateTime _cachedAt = DateTime(2000);
  static DateTime _noKeyUntil = DateTime(2000);
  static String? _cachedFor;
  static Set<String>? _bad;

  /// Отпечаток ключа (сам ключ в список «не подошёл» не пишется).
  static String _fp(String k) => sha256.convert(utf8.encode(k.trim())).toString().substring(0, 16);

  Future<Set<String>> _badKeys() async {
    if (_bad != null) return _bad!;
    try {
      final raw = await _storage.read(key: _badKeysName);
      final list = raw == null ? const [] : jsonDecode(raw);
      return _bad = {for (final x in list is List ? list : const []) x.toString()};
    } catch (_) {
      return _bad = <String>{};
    }
  }

  Future<void> _saveBad() async {
    try {
      final list = _bad!.toList();
      await _storage.write(key: _badKeysName, value: jsonEncode(list.length > 20 ? list.sublist(list.length - 20) : list));
    } catch (_) {
      // хранилище телефона недоступно — помним до перезапуска
    }
  }

  /// 2026-10-22 (аудит С-2): ключ не подошёл (Google ответил «API key not valid» и т. п.) — запоминаем его отпечаток и больше
  /// не берём, пока ключ в облаке не сменится (новый ключ — новый отпечаток) или ключ не вставят вручную заново.
  Future<void> markBadKey(String k) async {
    if (k.trim().isEmpty) return;
    (await _badKeys()).add(_fp(k));
    await _saveBad();
    if (_cached == k.trim()) _cached = null;
    _cachedAt = DateTime(2000);
    _noKeyUntil = DateTime(2000);
  }

  Future<void> _unmarkBad(String k) async {
    if ((await _badKeys()).remove(_fp(k))) await _saveBad();
  }

  Future<String?> _read(String name) async {
    try {
      final v = await _storage.read(key: name);
      return v == null || v.trim().isEmpty ? null : v.trim();
    } catch (_) {
      return null; // хранилище телефона не читается (ключи Android сброшены) — как будто ключа нет, без падения
    }
  }

  /// 2026-10-22 (аудит С-3): личные настройки кассы и программы владельца — одновременно, срок 4 с. null — сервер не ответил
  /// (нет связи); пустая карта — ответил, но ключей нет.
  Future<Map<String, dynamic>?> cloudSecrets() async {
    var answered = false;
    Future<Map?> one(String app) async {
      try {
        final data = await api.get('api/users/app-settings/$app/', timeout: const Duration(seconds: 4)).timeout(const Duration(seconds: 4));
        answered = true;
        final secrets = data is Map && data['settings'] is Map ? (data['settings'] as Map)['secrets'] : null;
        return secrets is Map ? secrets : null;
      } on ApiException catch (e) {
        if (e.status != null) answered = true; // настроек этой программы нет — сервер ответил
        return null;
      } catch (_) {
        return null; // срок вышел или нет связи
      }
    }

    final both = await Future.wait([one('nurmarket-kassa'), one('nurmarket-owner')]);
    if (!answered) return null;
    final out = <String, dynamic>{};
    // С конца: при совпадении остаётся значение кассы. Скрытые значения («****») не берём — как SettingsCloudSync кассы.
    for (final m in both.reversed) {
      if (m == null) continue;
      m.forEach((k, v) {
        final t = v?.toString().trim() ?? '';
        if (t.isNotEmpty && !t.contains('*') && !t.contains('•')) out['$k'] = t;
      });
    }
    return out;
  }

  /// 2026-10-22: другой пользователь вошёл на этом телефоне — запомненное прежнего забываем.
  void _checkUser() {
    final who = api.session?.userId;
    if (who == _cachedFor) return;
    _cachedFor = who;
    _cached = null;
    _cachedAt = DateTime(2000);
    _noKeyUntil = DateTime(2000);
    _groq = null;
    _openRouter = null;
    _fallbackLoaded = false;
  }

  String get _companyTag => '${api.session?.companyInn ?? ''}|${api.session?.companyName ?? ''}';

  Future<String?> key() async {
    _checkUser();
    final now = DateTime.now();
    // 2026-10-22 (аудит С-4): запомненный ключ — 10 минут, потом перечитать (ключ сменили на ПК — телефон подхватит).
    if (_cached != null && now.difference(_cachedAt) < const Duration(minutes: 10)) return _cached;
    // 2026-10-22 (аудит С-3): «ключа нет» помним 5 минут — без повторных ожиданий при каждом открытии камеры.
    if (_cached == null && now.isBefore(_noKeyUntil)) return null;
    final bad = await _badKeys();
    String? good(Object? k) {
      final v = k?.toString().trim();
      return v == null || v.isEmpty || bad.contains(_fp(v)) ? null : v;
    }

    // 2026-10-22 (советник мобилки): порядок ключей.
    // 1) Личные настройки NurCRM (как у кассы и программы владельца). Было: сначала ключ, когда-то вставленный на телефоне, и
    //    облако не читалось вовсе — владелец сменил ключ на ПК (сейчас в облаке ключ вида «AQ.…»), а телефон остался со старым.
    // 2) Вставленный на телефоне вручную (выше «плохого» облачного — аудит С-2).
    // 3) Ключ этого телефона от прежнего входа владельца или администратора той же компании (аудит С-1). Так же на ПК: ключ ИИ
    //    хранится в настройках компьютера (UserPreferences), и кассир на кассе пользуется ключом, который ввёл владелец.
    final cloud = await cloudSecrets();
    if (cloud != null) _takeFallbackKeys(cloud);
    var found = good(cloud?['TelegramAiKey']);
    if (found != null) {
      try {
        final shared = '$_companyTag\n$found';
        if (await _read(_sharedKeyName) != shared) await _storage.write(key: _sharedKeyName, value: shared);
      } catch (_) {
        // не сохранился на телефоне — в этот раз ключ всё равно есть
      }
    }
    found ??= good(await _read(_keyName));
    if (found == null) {
      final shared = await _read(_sharedKeyName);
      final i = shared?.lastIndexOf('\n') ?? -1;
      if (shared != null && i >= 0 && shared.substring(0, i) == _companyTag) found = good(shared.substring(i + 1));
    }
    // Нет связи — прежний ключ этого запуска лучше, чем ничего.
    if (found == null && cloud == null && _cached != null && !bad.contains(_fp(_cached!))) found = _cached;
    _cached = found;
    _cachedAt = now;
    if (found == null) _noKeyUntil = now.add(const Duration(minutes: 5));
    return found;
  }

  /// 14.10, владелец: «в мобилке Нур Советник не работает». На кассе при отказе Gemini (кончился бесплатный лимит,
  /// модель перегружена) советник отвечает через Groq и OpenRouter (AiProviders) — на телефоне был только Gemini, и
  /// советник молчал. Запасные ключи — из тех же личных настроек NurCRM (secrets: GroqApiKey, OpenRouterApiKey).
  static String? _groq;
  static String? _openRouter;
  static bool _fallbackLoaded = false;

  static void _takeFallbackKeys(Map secrets) {
    String? v(String k) => secrets[k]?.toString().trim().isEmpty ?? true ? null : secrets[k].toString().trim();
    _groq ??= v('GroqApiKey');
    _openRouter ??= v('OpenRouterApiKey');
  }

  Future<void> _loadFallbackKeys() async {
    _checkUser();
    if (_fallbackLoaded) return;
    // 2026-10-22 (советник мобилки): облако — первым, как у ключа Gemini (см. key()); ключи телефона — запасные.
    // Облако уже прочитано в key() — второй раз не спрашиваем (аудит С-3: без лишних ожиданий при плохой связи).
    if (_groq == null && _openRouter == null && _cachedAt.year == 2000) {
      final cloud = await cloudSecrets();
      if (cloud != null) _takeFallbackKeys(cloud);
    }
    final g = await _read('ai.groq');
    final o = await _read('ai.openrouter');
    if (g != null) _groq ??= g;
    if (o != null) _openRouter ??= o;
    _fallbackLoaded = true;
  }

  /// 2026-10-22 (советник мобилки): запасные ключи (Groq, OpenRouter) — для проверки «есть ли хоть один ключ» (и тестов).
  Future<({String? groq, String? openRouter})> fallbackKeys() async {
    await _loadFallbackKeys();
    return (groq: _groq, openRouter: _openRouter);
  }

  /// Запасной ответ: OpenAI-совместимый чат Groq (openai/gpt-oss-120b), затем OpenRouter (openrouter/free) — как AiProviders кассы.
  Future<String?> _fallbackChat(String system, List<({bool user, String text})> history, List<String> errors) async {
    final keys = await fallbackKeys();
    final messages = [
      {'role': 'system', 'content': system},
      for (final m in history) {'role': m.user ? 'user' : 'assistant', 'content': m.text},
    ];
    for (final (name, url, model, k) in [
      ('Groq', 'https://api.groq.com/openai/v1/chat/completions', 'openai/gpt-oss-120b', keys.groq),
      ('OpenRouter', 'https://openrouter.ai/api/v1/chat/completions', 'openrouter/free', keys.openRouter),
    ]) {
      if (k == null) continue;
      try {
        // 2026-10-22 (советник мобилки): ответ не JSON (страница ошибки) больше не роняет советника (было post<Map> → TypeError).
        final r = await _http.post<dynamic>(
          url,
          data: {'model': model, 'messages': messages, 'temperature': 0.4, 'max_tokens': 3000},
          options: Options(
            headers: {'Authorization': 'Bearer $k'},
            validateStatus: (_) => true,
            sendTimeout: const Duration(seconds: 20),
            receiveTimeout: const Duration(seconds: 40),
          ),
        );
        final data = r.data is Map ? (r.data as Map).cast<String, dynamic>() : const <String, dynamic>{};
        if ((r.statusCode ?? 0) >= 400) {
          final e = data['error'];
          errors.add('$name: ${(e is Map ? e['message']?.toString() : null) ?? 'HTTP ${r.statusCode}'}');
          continue;
        }
        final choices = data['choices'];
        final message = choices is List && choices.isNotEmpty ? (choices.first as Map)['message'] : null;
        final text = message is Map ? message['content']?.toString() : null;
        if (text != null && text.trim().isNotEmpty) return text.trim();
        errors.add('$name: пустой ответ');
      } on DioException catch (e) {
        errors.add('$name: ${e.message}');
      }
    }
    return null;
  }

  /// 14.10: для чата советника хватает любого ключа — Gemini, Groq или OpenRouter.
  Future<bool> hasChatKey() async {
    if (await key() != null) return true;
    final keys = await fallbackKeys();
    return keys.groq != null || keys.openRouter != null;
  }

  /// 14.10, «Настройка ИИ»: текущие ключи трёх поставщиков (null — нет).
  Future<({String? gemini, String? groq, String? openRouter})> allKeys() async {
    final g = await key();
    await _loadFallbackKeys();
    return (gemini: g, groq: _groq, openRouter: _openRouter);
  }

  /// Сохранить ключи: на телефоне и в личных настройках NurCRM (secrets — как SettingsCloudSync кассы), чтобы работали
  /// и на кассе, и в программе владельца. null — не менять, пустая строка — убрать ключ.
  Future<void> saveAllKeys({String? gemini, String? groq, String? openRouter}) async {
    Future<void> local(String name, String? v) async {
      if (v == null) return;
      if (v.trim().isEmpty) {
        await _storage.delete(key: name);
      } else {
        await _storage.write(key: name, value: v.trim());
      }
    }

    await local(_keyName, gemini);
    await local('ai.groq', groq);
    await local('ai.openrouter', openRouter);
    if (gemini != null) _cached = gemini.trim().isEmpty ? null : gemini.trim();
    // 2026-10-22 (аудит С-2): ключ вставили заново — он снова в работе.
    if (gemini != null && gemini.trim().isNotEmpty) await _unmarkBad(gemini);
    _cachedAt = DateTime.now();
    _noKeyUntil = DateTime(2000);
    if (groq != null) _groq = groq.trim().isEmpty ? null : groq.trim();
    if (openRouter != null) _openRouter = openRouter.trim().isEmpty ? null : openRouter.trim();
    final secrets = <String, Object?>{
      if (gemini != null) 'TelegramAiKey': gemini.trim().isEmpty ? null : gemini.trim(),
      if (groq != null) 'GroqApiKey': groq.trim().isEmpty ? null : groq.trim(),
      if (openRouter != null) 'OpenRouterApiKey': openRouter.trim().isEmpty ? null : openRouter.trim(),
    };
    if (secrets.isEmpty) return;
    await api.patch('api/users/app-settings/nurmarket-kassa/', body: {'secrets': secrets});
    try {
      await api.patch('api/users/app-settings/nurmarket-owner/', body: {'secrets': secrets});
    } on ApiException {
      // программы владельца у пользователя может не быть — ключи уже на кассе и на телефоне
    }
  }

  /// Проверка ключа одним коротким запросом; null — работает, иначе текст ошибки.
  Future<String?> testKey(String provider, String k) async {
    try {
      if (provider == 'gemini') {
        String? last;
        for (final model in [?_workingModel, ..._models.where((m) => m != _workingModel)]) {
          final r = await _http.post<dynamic>(
            'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
            data: {
              'contents': [
                {
                  'role': 'user',
                  'parts': [
                    {'text': 'Ответь одним словом: ок'},
                  ],
                },
              ],
              'generationConfig': {'maxOutputTokens': 10},
            },
            options: Options(headers: {'x-goog-api-key': k}, validateStatus: (_) => true),
          );
          if ((r.statusCode ?? 0) < 400) return null;
          final e = r.data is Map ? (r.data as Map)['error'] : null; // 2026-10-22: страница ошибки вместо JSON — без падения
          last = (e is Map ? e['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
          if (r.statusCode == 400 || r.statusCode == 401 || r.statusCode == 403) return last;
        }
        return last;
      }
      final (url, model) = provider == 'groq'
          ? ('https://api.groq.com/openai/v1/chat/completions', 'openai/gpt-oss-120b')
          : ('https://openrouter.ai/api/v1/chat/completions', 'openrouter/free');
      final r = await _http.post<dynamic>(
        url,
        data: {
          'model': model,
          'messages': [
            {'role': 'user', 'content': 'Ответь одним словом: ок'},
          ],
          'max_tokens': 20,
        },
        options: Options(headers: {'Authorization': 'Bearer $k'}, validateStatus: (_) => true),
      );
      if ((r.statusCode ?? 0) < 400) return null;
      final e = r.data is Map ? (r.data as Map)['error'] : null;
      return (e is Map ? e['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
    } on DioException catch (e) {
      return e.message ?? 'нет связи';
    }
  }

  Future<void> saveKey(String value) async {
    // 2026-10-22 (аудит С-2): saveKey('') зовут, когда ключ не подошёл (умная камера), — запоминаем его как «плохой»,
    // иначе key() тут же снова брал тот же ключ из облака. Вставленный вручную — снимаем отметку.
    if (value.trim().isEmpty) {
      if (_cached != null) await markBadKey(_cached!);
    } else {
      await _unmarkBad(value);
    }
    _cached = value.trim().isEmpty ? null : value.trim();
    _cachedAt = DateTime.now();
    _noKeyUntil = DateTime(2000);
    try {
      if (_cached == null) {
        await _storage.delete(key: _keyName);
      } else {
        await _storage.write(key: _keyName, value: _cached);
      }
    } catch (_) {
      // хранилище телефона недоступно — ключ работает до перезапуска
    }
  }

  /// mode: sale — что держит покупатель (обычно 1–3 товара); shelf — полка; invoice — накладная; labels — этикетки.
  /// 14.10, владелец: «при скане умной камерой для корзины сразу находит товар точно». Второй шаг после recognize:
  /// кадр + эталонные снимки товаров (3 снимка «справа, слева, сверху» с телефона или фото из NurCRM) — ИИ называет
  /// номера эталонов, которые есть на кадре, и сколько штук. Возвращает (номер эталона, штук).
  Future<List<(int, int)>> identify(Uint8List shot, List<(String name, List<Uint8List> pngs)> refs) async {
    final k = await key();
    if (k == null || refs.isEmpty) return const [];
    final parts = <Map<String, dynamic>>[
      {
        'text':
            'Первое фото — товар(ы) на кассе. Дальше — эталонные снимки товаров магазина с номерами. Найди на первом фото товары, '
            'которые ТОЧНО совпадают с эталоном (упаковка, цвет, надписи, объём). Ответь ТОЛЬКО JSON-массивом [{"n": номер эталона, '
            '"count": сколько штук на первом фото}]. Нет точного совпадения — пустой массив [].',
      },
      {
        'inline_data': {'mime_type': 'image/jpeg', 'data': base64Encode(shot)},
      },
    ];
    for (var i = 0; i < refs.length; i++) {
      parts.add({'text': 'Эталон ${i + 1}: ${refs[i].$1}'});
      for (final img in refs[i].$2) {
        // 2026-10-22: эталоны теперь JPEG (снимок камеры), старые — PNG
        parts.add({
          'inline_data': {'mime_type': imageMime(img), 'data': base64Encode(img)},
        });
      }
    }
    try {
      final text = await _visionAsk(
        k,
        (thinking) => {
          'contents': [
            {'role': 'user', 'parts': parts},
          ],
          'generationConfig': {'temperature': 0.0, 'responseMimeType': 'application/json', 'maxOutputTokens': 512, 'thinkingConfig': ?thinking},
        },
        total: const Duration(seconds: 10),
      );
      var t = text.trim();
      final a = t.indexOf('['), b = t.lastIndexOf(']');
      if (a >= 0 && b > a) t = t.substring(a, b + 1);
      final out = <(int, int)>[];
      for (final e in jsonDecode(t) as List) {
        if (e is! Map) continue;
        final n = e['n'] is num ? (e['n'] as num).toInt() : int.tryParse('${e['n']}');
        final c = e['count'] is num ? (e['count'] as num).toInt() : int.tryParse('${e['count']}') ?? 1;
        if (n != null && n >= 1 && n <= refs.length) out.add((n - 1, c.clamp(1, 999)));
      }
      return out;
    } catch (_) {
      return const [];
    }
  }

  /// JPEG или PNG — по первым байтам.
  static String imageMime(Uint8List b) => b.length > 2 && b[0] == 0xFF && b[1] == 0xD8 ? 'image/jpeg' : 'image/png';

  /// 2026-10-22 (умная камера, аудит У-14/У-15): один запрос умной камеры или весов к Gemini — как у советника: настройка
  /// «размышлений» по модели (без неё Gemini 3 тратил лимит ответа на размышления и отвечал пусто — это был переход к
  /// следующей модели, ещё одно ожидание), общий срок [total] на все модели (раньше: до 60 с на каждую из 5 моделей),
  /// понятные ошибки. Нет ответа — VisionException.
  Future<String> _visionAsk(String k, Map<String, dynamic> Function(Map<String, dynamic>? thinking) body, {Duration total = const Duration(seconds: 12)}) async {
    final models = [?_workingModel, ..._models.where((m) => m != _workingModel)];
    final started = Stopwatch()..start();
    String? lastError;
    for (var mi = 0; mi < models.length; mi++) {
      final model = models[mi];
      final left = total - started.elapsed;
      if (left < const Duration(seconds: 2)) {
        lastError ??= 'Google не ответил вовремя';
        break;
      }
      final step = _thinkStep[model] ?? (model.startsWith('gemini-3') ? 1 : 0);
      try {
        final r = await _http.post<dynamic>(
          'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
          data: body(switch (step) {
            0 => {'thinkingBudget': 0},
            1 => {'thinkingLevel': 'low'},
            _ => null,
          }),
          options: Options(headers: {'x-goog-api-key': k}, validateStatus: (_) => true, sendTimeout: left, receiveTimeout: left),
        );
        final status = r.statusCode ?? 0;
        final data = r.data is Map ? (r.data as Map).cast<String, dynamic>() : const <String, dynamic>{};
        if (status >= 400) {
          final message = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP $status';
          if (message.contains('API key') || message.contains('API_KEY') || ((status == 401 || status == 403) && !message.toLowerCase().contains('model'))) {
            throw VisionException(message, noKey: true);
          }
          if (status == 400 && step < 2) {
            _thinkStep[model] = step + 1;
            mi--;
            continue;
          }
          if (_workingModel == model) _workingModel = null;
          lastError = switch (status) {
            404 => 'модель $model недоступна',
            429 => 'исчерпан бесплатный лимит запросов Google — попробуйте через минуту',
            >= 500 => 'нейросеть Google сейчас перегружена — попробуйте через минуту',
            _ => 'ошибка Google $status: $message',
          };
          continue;
        }
        final text = _text(data);
        if (text != null) {
          _workingModel = model;
          return text;
        }
        lastError = 'нейросеть вернула пустой ответ';
      } on DioException catch (e) {
        if (_workingModel == model) _workingModel = null;
        lastError = switch (e.type) {
          DioExceptionType.connectionTimeout || DioExceptionType.sendTimeout || DioExceptionType.receiveTimeout => 'Google не ответил вовремя',
          DioExceptionType.connectionError => 'нет связи с интернетом',
          _ => e.message ?? 'нет связи с Google',
        };
        if (e.type == DioExceptionType.connectionError) break; // нет интернета — другие модели не помогут
      }
    }
    throw VisionException(lastError ?? 'ИИ недоступен');
  }

  /// 2026-10-20, владелец: «умной камерой сканирует вместе с показаниями весов». Один кадр: товар на весах и вес
  /// с табло (тем же ключом Gemini). [withProduct] = false — только вес (повторные кадры, пока вес устанавливается).
  /// [names] — весовые товары каталога: ИИ называет товар так же, как в магазине, и совпадение находится точнее.
  /// 2026-10-22 (аудит У-2): ИИ переписывает табло как есть (display) и единицу — вес считает телефон
  /// (раньше «15 g» без перевода превращались в 15 кг).
  Future<ScaleReading> readScale(Uint8List jpeg, {bool withProduct = true, List<String> names = const []}) async {
    final k = await key();
    if (k == null) throw const VisionException('no key', noKey: true);
    final task = StringBuffer()
      ..write('На фото — торговые весы магазина и товар на них. ')
      ..write(withProduct ? '1) Какой товар лежит на весах (как называют в магазине: «Картофель», «Лук репчатый», «Яблоки»). ' : '')
      ..write('${withProduct ? '2' : '1'}) Перепиши в display ТОЧНО то, что видно в окне ВЕСА (окно «МАССА»/«ВЕС»/«WEIGHT»), с точкой или запятой, ')
      ..write('и единицу этого окна в unit ("kg" или "g"). Не путай с окнами «ЦЕНА» и «СТОИМОСТЬ». На табло 0 или прочерки — display "0". ')
      ..write('Табло не видно или не читается — display null. Если видно окно СТОИМОСТЬ — перепиши его в total.');
    if (withProduct && names.isNotEmpty) task.write(' Если товар есть в этом списке — назови его точно так: ${names.take(60).join('; ')}.');
    final text = await _visionAsk(
      k,
      (thinking) => {
        'systemInstruction': {
          'parts': [
            {
              'text':
                  'Ты читаешь табло весов и узнаёшь товар по фото. Отвечай ТОЛЬКО JSON-объектом {"product": строка, "display": строка или null, "unit": "kg" или "g", "total": строка или null, "confidence": число 0..1}. Не выдумывай: не уверен — снижай confidence.',
            },
          ],
        },
        'contents': [
          {
            'role': 'user',
            'parts': [
              {'text': task.toString()},
              {
                'inline_data': {'mime_type': 'image/jpeg', 'data': base64Encode(jpeg)},
              },
            ],
          },
        ],
        'generationConfig': {'temperature': 0.0, 'responseMimeType': 'application/json', 'maxOutputTokens': 1024, 'thinkingConfig': ?thinking},
      },
    );
    return parseScaleJson(text);
  }

  Future<List<SeenItem>> recognize(Uint8List jpeg, {String mode = 'sale'}) async {
    final k = await key();
    if (k == null) throw const VisionException('no key', noKey: true);
    final task = switch (mode) {
      'invoice' => 'На фото — накладная поставщика. Перечисли строки товаров: название как в накладной, количество (count), цена за штуку (label_price).',
      'labels' => 'На фото — этикетки или ценники. Перечисли товары: название, штрихкод с этикетки, если читается, цена (label_price).',
      'shelf' =>
        'На фото — полка магазина. Перечисли все разные товары: бренд, название, объём/вес, сколько штук видно (count), штрихкод — только если он читается.',
      _ => 'На фото — товар(ы), которые покупатель кладёт на кассу. Перечисли каждый товар: бренд, название, объём/вес, сколько штук (count), штрихкод — только если он читается.',
    };
    final text = await _visionAsk(
      k,
      (thinking) => {
        'systemInstruction': {
          'parts': [
            {
              'text': 'Ты распознаёшь товары магазина в Кыргызстане по фото. Отвечай ТОЛЬКО JSON-массивом объектов {"name","brand","size","barcode","count","label_price"}. Не выдумывай: неуверен — пиши, что видно, barcode пустой.',
            },
          ],
        },
        'contents': [
          {
            'role': 'user',
            'parts': [
              {'text': task},
              {
                'inline_data': {'mime_type': 'image/jpeg', 'data': base64Encode(jpeg)},
              },
            ],
          },
        ],
        // 2026-10-22: в продаже на снимке 1–3 товара — короткий ответ быстрее; накладная и полка — длиннее
        'generationConfig': {'temperature': 0.1, 'responseMimeType': 'application/json', 'maxOutputTokens': mode == 'sale' ? 1024 : 4096, 'thinkingConfig': ?thinking},
      },
      total: Duration(seconds: mode == 'sale' ? 12 : 25),
    );
    return parseSeen(text);
  }

  /// 13.10, владелец: «описание — вручную или из интернета». Короткое описание товара для сайта и бота: Gemini с поиском
  /// Google (как «Написать с Нур Советником» на кассе), по названию, штрихкоду и фото, если есть. Без поиска, если ключ его не умеет.
  Future<String> describe(String name, String barcode, {Uint8List? photo}) async {
    final k = await key();
    if (k == null) throw const VisionException('no key', noKey: true);
    Map<String, dynamic> body(bool search) => {
      'systemInstruction': {
        'parts': [
          {
            'text': 'Пиши описание товара для интернет-магазина в Кыргызстане: по-русски, 2–4 предложения, факты (состав, объём, назначение, хранение), без рекламных восклицаний и без цены. Не выдумывай — чего не знаешь, не пиши. Ответ — только текст описания.',
          },
        ],
      },
      'contents': [
        {
          'role': 'user',
          'parts': [
            {'text': 'Товар: $name${barcode.isEmpty ? '' : ', штрихкод $barcode'}.'},
            if (photo != null)
              {
                'inline_data': {'mime_type': 'image/jpeg', 'data': base64Encode(photo)},
              },
          ],
        },
      ],
      if (search)
        'tools': [
          {'google_search': <String, dynamic>{}},
        ],
      'generationConfig': {'temperature': 0.3, 'maxOutputTokens': 600},
    };
    String? lastError;
    for (final search in [true, false]) {
      for (final model in [?_workingModel, ..._models.where((m) => m != _workingModel)]) {
        try {
          final r = await _http.post<Map<String, dynamic>>(
            'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
            data: body(search),
            options: Options(headers: {'x-goog-api-key': k}, validateStatus: (_) => true),
          );
          final data = r.data ?? const {};
          if ((r.statusCode ?? 0) >= 400) {
            lastError = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
            if (r.statusCode == 400 && (lastError.contains('API key') || lastError.contains('API_KEY'))) throw VisionException(lastError, noKey: true);
            continue;
          }
          final text = _text(data);
          if (text != null) return text.trim();
        } on DioException catch (e) {
          lastError = e.message;
        }
      }
    }
    throw VisionException(lastError ?? 'ИИ недоступен');
  }

  /// 13.10, Нур Советник на телефоне: разговор (вопросы и ответы по очереди) со сводкой магазина в системной части.
  Future<String> chat(String system, List<({bool user, String text})> history) async {
    final k = await key();
    final errors = <String>[];
    if (k != null) {
      final text = await _geminiChat(k, system, history, errors);
      if (text != null) return text;
    }
    final alt = await _fallbackChat(system, history, errors);
    if (alt != null) return alt;
    throw VisionException(errors.isEmpty ? 'no key' : errors.join('\n'), noKey: errors.isEmpty);
  }

  /// 2026-10-22 (советник мобилки): шаг «размышлений» по моделям, как ThinkingStep в TelegramAiChat кассы:
  /// 0 — thinkingBudget 0 (Gemini 2.x), 1 — thinkingLevel «low» (Gemini 3), 2 — без настройки.
  static final Map<String, int> _thinkStep = {};

  /// 2026-10-22 (советник мобилки): запрос как у советника на ПК (TelegramAiChat, проверен вживую 21.10). Было: лимит ответа
  /// 1500 токенов без настройки «размышлений» — модель тратила лимит на размышления, ответ обрывался или был пустым; ошибка
  /// модели (404) и пустой ответ не различались; страница ошибки вместо JSON роняла советника (TypeError мимо обработчика);
  /// подряд две реплики одной стороны и тексты ошибок уходили в историю. Теперь: maxOutputTokens 4096 + thinkingConfig,
  /// части «thought» пропускаются, у каждой попытки свой срок (первая 25 с, дальше 15 с, всего 50 с), 404/5xx/пусто —
  /// следующая модель, неподходящий ключ — сразу к запасным ИИ (Groq, OpenRouter). null — Gemini не ответил (причина в [errors]).
  Future<String?> _geminiChat(String k, String system, List<({bool user, String text})> history, List<String> errors) async {
    final contents = <Map<String, dynamic>>[];
    for (final m in history.length > 30 ? history.sublist(history.length - 30) : history) {
      final role = m.user ? 'user' : 'model';
      if (m.text.trim().isEmpty) continue;
      if (contents.isEmpty && role != 'user') continue; // разговор начинается с вопроса
      if (contents.isNotEmpty && contents.last['role'] == role) {
        ((contents.last['parts'] as List).first as Map)['text'] = '${((contents.last['parts'] as List).first as Map)['text']}\n\n${m.text}';
        continue;
      }
      contents.add({
        'role': role,
        'parts': [
          {'text': m.text},
        ],
      });
    }
    if (contents.isEmpty) {
      errors.add('Gemini: нет вопроса');
      return null;
    }
    Map<String, dynamic> body(int step) => {
      'systemInstruction': {
        'parts': [
          {'text': system},
        ],
      },
      'contents': contents,
      'generationConfig': {
        'temperature': 0.5,
        'maxOutputTokens': 4096,
        if (step == 0) 'thinkingConfig': {'thinkingBudget': 0},
        if (step == 1) 'thinkingConfig': {'thinkingLevel': 'low'},
      },
    };
    final models = [?_workingModel, ..._models.where((m) => m != _workingModel)];
    final started = Stopwatch()..start();
    String? lastError;
    for (var mi = 0; mi < models.length; mi++) {
      final model = models[mi];
      final left = const Duration(seconds: 50) - started.elapsed;
      if (left < const Duration(seconds: 5)) {
        lastError ??= 'Google не ответил вовремя';
        break;
      }
      final limit = mi == 0 ? const Duration(seconds: 25) : const Duration(seconds: 15);
      final step = _thinkStep[model] ?? (model.startsWith('gemini-3') ? 1 : 0);
      try {
        final r = await _http.post<dynamic>(
          'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
          data: body(step),
          options: Options(
            headers: {'x-goog-api-key': k},
            validateStatus: (_) => true,
            sendTimeout: limit < left ? limit : left,
            receiveTimeout: limit < left ? limit : left,
          ),
        );
        final status = r.statusCode ?? 0;
        final data = r.data is Map ? (r.data as Map).cast<String, dynamic>() : const <String, dynamic>{};
        if (status >= 400) {
          final message = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP $status';
          final badKey =
              message.contains('API key') || message.contains('API_KEY') || ((status == 401 || status == 403) && !message.toLowerCase().contains('model'));
          if (badKey) {
            await markBadKey(k); // 2026-10-22 (аудит С-2): этот ключ больше не берём, пока его не сменят
            errors.add('Gemini: ключ Google не подходит — проверьте его в программе владельца ($message)');
            return null;
          }
          // Модель не принимает настройку «размышлений» (400) — тот же запрос со следующим шагом (как на ПК).
          if (status == 400 && step < 2) {
            _thinkStep[model] = step + 1;
            mi--;
            continue;
          }
          if (_workingModel == model) _workingModel = null;
          lastError = switch (status) {
            404 => 'модель $model недоступна',
            429 => 'исчерпан бесплатный лимит запросов Google — попробуйте через минуту',
            >= 500 => 'нейросеть Google сейчас перегружена — попробуйте через минуту',
            _ => 'ошибка Google $status: $message',
          };
          continue; // следующая модель
        }
        final text = _text(data);
        if (text != null) {
          _workingModel = model;
          return text.trim();
        }
        final c = data['candidates'];
        final reason = c is List && c.isNotEmpty && c.first is Map ? (c.first as Map)['finishReason']?.toString() : null;
        final blocked = data['promptFeedback'] is Map ? (data['promptFeedback'] as Map)['blockReason']?.toString() : null;
        lastError = blocked != null || reason == 'SAFETY' || reason == 'PROHIBITED_CONTENT'
            ? 'Google отказался отвечать на этот вопрос (${blocked ?? reason})'
            : 'нейросеть вернула пустой ответ${reason == null ? '' : ' ($reason)'}';
      } on DioException catch (e) {
        if (_workingModel == model) _workingModel = null;
        lastError = switch (e.type) {
          DioExceptionType.connectionTimeout || DioExceptionType.sendTimeout || DioExceptionType.receiveTimeout => 'Google не ответил вовремя',
          DioExceptionType.connectionError => 'нет связи с Google',
          _ => e.message ?? 'нет связи с Google',
        };
        if (e.type == DioExceptionType.connectionError) break; // нет интернета — другие модели не помогут
      }
    }
    errors.add('Gemini: ${lastError ?? 'нет ответа'}');
    return null;
  }

  static String? _text(Map<String, dynamic> data) {
    final c = data['candidates'];
    if (c is! List || c.isEmpty || c.first is! Map) return null;
    final content = (c.first as Map)['content'];
    final parts = content is Map ? content['parts'] : null;
    if (parts is! List) return null;
    // 2026-10-22 (советник мобилки): «размышления» модели (thought: true) не показываем — как ReadText на ПК.
    final t = parts.whereType<Map>().where((p) => p['thought'] != true).map((p) => p['text']?.toString() ?? '').join();
    return t.trim().isEmpty ? null : t;
  }

  /// 2026-10-22 (советник мобилки): текст ответа Gemini без «размышлений» — для тестов.
  @visibleForTesting
  static String? answerText(Map<String, dynamic> data) => _text(data);

  static List<SeenItem> parseSeen(String text) {
    var t = text.trim();
    final start = t.indexOf('[');
    final end = t.lastIndexOf(']');
    if (start >= 0 && end > start) t = t.substring(start, end + 1);
    final decoded = jsonDecode(t);
    if (decoded is! List) return [];
    return [
      for (final e in decoded)
        if (e is Map && (e['name']?.toString().trim().isNotEmpty ?? false))
          SeenItem(
            name: e['name'].toString().trim(),
            brand: e['brand']?.toString().trim() ?? '',
            size: e['size']?.toString().trim() ?? '',
            barcode: (e['barcode']?.toString() ?? '').replaceAll(RegExp(r'\D'), ''),
            count: (e['count'] is num ? (e['count'] as num).round() : int.tryParse('${e['count']}') ?? 1).clamp(1, 999),
            labelPrice: e['label_price'] is num ? (e['label_price'] as num).toDouble() : double.tryParse('${e['label_price']}'.replaceAll(',', '.')) ?? 0,
          ),
    ];
  }

  // 2026-10-22 (умная камера, аудит У-5): «Coca-Cola» на упаковке и «Кока-Кола» в каталоге не имели общих слов, «Кока-Кола 1л»
  // и «Кока-Кола 2л» считались одним товаром (0,67 > 0,5). Теперь слова сравниваются в одной записи (латиница → кириллица,
  // ё → е), по основе (первые 4 буквы), а разный объём или вес — штраф.
  static const _lat = {
    'sh': 'ш', 'ch': 'ч', 'zh': 'ж', 'kh': 'х', 'ts': 'ц', 'ya': 'я', 'yu': 'ю', 'yo': 'е', //
    'a': 'а', 'b': 'б', 'c': 'к', 'd': 'д', 'e': 'е', 'f': 'ф', 'g': 'г', 'h': 'х', 'i': 'и', 'j': 'дж', 'k': 'к', 'l': 'л', 'm': 'м',
    'n': 'н', 'o': 'о', 'p': 'п', 'q': 'к', 'r': 'р', 's': 'с', 't': 'т', 'u': 'у', 'v': 'в', 'w': 'в', 'x': 'кс', 'y': 'й', 'z': 'з',
  };

  /// Одна запись слова: нижний регистр, ё → е, латиница → кириллица (по звучанию, грубо — для сравнения, не для показа).
  static String normWord(String s) {
    var t = s.toLowerCase().replaceAll('ё', 'е');
    if (!RegExp('[a-z]').hasMatch(t)) return t;
    final out = StringBuffer();
    var i = 0;
    while (i < t.length) {
      final two = i + 1 < t.length ? t.substring(i, i + 2) : '';
      if (_lat.containsKey(two) && two.length == 2) {
        out.write(_lat[two]);
        i += 2;
      } else {
        out.write(_lat[t[i]] ?? t[i]);
        i++;
      }
    }
    return out.toString();
  }

  static Set<String> _words(String s) =>
      s.toLowerCase().replaceAll(RegExp(r'[«»"().,/\\\-+]'), ' ').split(RegExp(r'\s+')).where((w) => w.length >= 2).map(normWord).toSet();

  static bool _sameWord(String a, String b) => a == b || (a.length >= 4 && b.length >= 4 && a.substring(0, 4) == b.substring(0, 4));

  static final _volume = RegExp(r'(\d+(?:[.,]\d+)?)\s*(мл|ml|л|l|литр\w*|кг|kg|гр|г|g|gr)(?![a-zа-я])', caseSensitive: false);

  /// Объём или вес из названия: (число в мл или г, «v» — объём / «m» — масса); null — не указан.
  static (double, String)? volumeOf(String s) {
    final m = _volume.firstMatch(s.toLowerCase());
    if (m == null) return null;
    final v = double.tryParse(m.group(1)!.replaceAll(',', '.'));
    if (v == null) return null;
    return switch (m.group(2)!) {
      'мл' || 'ml' => (v, 'v'),
      'кг' || 'kg' => (v * 1000, 'm'),
      'гр' || 'г' || 'g' || 'gr' => (v, 'm'),
      _ => (v * 1000, 'v'),
    };
  }

  /// Похожесть увиденного и названия товара 0..1.
  static double nameScore(String seen, String product) {
    final want = _words(seen), have = _words(product);
    if (want.isEmpty || have.isEmpty) return 0;
    final common = want.where((w) => have.any((h) => _sameWord(w, h))).length;
    var score = common / want.length * 0.6 + common / have.length * 0.4;
    final a = volumeOf(seen), b = volumeOf(product);
    if (a != null && b != null && a.$2 == b.$2 && (a.$1 - b.$1).abs() > a.$1 * 0.03) score = score.clamp(0, 0.4).toDouble();
    return score;
  }

  /// Контрольная цифра EAN-8, UPC-A, EAN-13 (ИИ мог «прочитать» штрихкод с ошибкой или выдумать его).
  static bool validEan(String c) {
    if (!RegExp(r'^(\d{8}|\d{12}|\d{13})$').hasMatch(c)) return false;
    var sum = 0;
    for (var i = 0; i < c.length - 1; i++) {
      final d = c.codeUnitAt(c.length - 2 - i) - 48;
      sum += i.isEven ? d * 3 : d;
    }
    return (10 - sum % 10) % 10 == c.codeUnitAt(c.length - 1) - 48;
  }

  /// Совпадения в каталоге: штрихкод (с верной контрольной цифрой) — 0,9; иначе похожесть названия (бренд, название, объём).
  Future<List<SeenMatch>> match(List<SeenItem> items) async {
    // 2026-10-22 (скорость скана): база отвечает по очереди (один поток), поэтому параллель не помогает; выигрыш — не искать
    // одно и то же слово дважды (одинаковые товары кадра, слово и его основа) и штрихкод проверять по индексу.
    final memo = <String, Future<List<Product>>>{};
    final out = <SeenMatch>[];
    for (final s in items) {
      final found = <String, (Product, double)>{};
      // 2026-10-22 (аудит У-6): штрихкод от ИИ — подсказка, не «точно»: проверяется цифра, ищется как скан
      if (validEan(s.barcode)) {
        final hit = await db.productByScan(s.barcode);
        if (hit != null) found[hit.product.id] = (hit.product, 0.9);
      }
      final raw = s.title.toLowerCase().replaceAll(RegExp(r'[«»"().,/\\\-+]'), ' ').split(RegExp(r'\s+')).where((w) => w.length >= 3);
      final terms = <String>{for (final w in raw.take(4)) ...{w, normWord(w)}};
      for (final w in terms) {
        final q = w.length > 5 ? w.substring(0, 5) : w;
        for (final p in await memo.putIfAbsent(q, () => db.searchProducts(q, limit: 40))) {
          final score = nameScore(s.title, p.name);
          if (score > (found[p.id]?.$2 ?? 0)) found[p.id] = (p, score);
        }
      }
      final list = found.values.toList()..sort((a, b) => b.$2.compareTo(a.$2));
      out.add(SeenMatch(s, list.take(3).toList()));
    }
    return out;
  }
}

final visionProvider = Provider((ref) => VisionService(ref.watch(apiProvider), ref.watch(databaseProvider)));
