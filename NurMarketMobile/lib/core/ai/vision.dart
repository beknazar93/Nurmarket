import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
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
  VisionService(this.api, this.db);

  static const _models = ['gemini-3.5-flash-lite', 'gemini-flash-lite-latest', 'gemini-3.1-flash-lite', 'gemini-flash-latest', 'gemini-3.5-flash'];
  static const _storage = FlutterSecureStorage();
  static const _keyName = 'ai.gemini';
  static String? _cached;
  static String? _workingModel;
  final _http = Dio(BaseOptions(connectTimeout: const Duration(seconds: 15), receiveTimeout: const Duration(seconds: 60)));

  Future<String?> key() async {
    if (_cached != null) return _cached;
    final local = await _storage.read(key: _keyName);
    if (local != null && local.isNotEmpty) return _cached = local;
    for (final app in ['nurmarket-kassa', 'nurmarket-owner']) {
      try {
        final data = await api.get('api/users/app-settings/$app/', timeout: const Duration(seconds: 10));
        if (data is Map && data['settings'] is Map) {
          final secrets = (data['settings'] as Map)['secrets'];
          if (secrets is Map) _takeFallbackKeys(secrets);
          final k = secrets is Map ? secrets['TelegramAiKey']?.toString() : null;
          if (k != null && k.trim().isNotEmpty) return _cached = k.trim();
        }
      } on ApiException {
        // нет настроек этой программы — пробуем следующую
      }
    }
    return null;
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
    if (_fallbackLoaded) return;
    // 14.10: ключи, введённые на телефоне («Настройка ИИ»), — первыми.
    _groq ??= await _storage.read(key: 'ai.groq');
    _openRouter ??= await _storage.read(key: 'ai.openrouter');
    for (final app in ['nurmarket-kassa', 'nurmarket-owner']) {
      try {
        final data = await api.get('api/users/app-settings/$app/', timeout: const Duration(seconds: 10));
        if (data is Map && data['settings'] is Map && (data['settings'] as Map)['secrets'] is Map) {
          _takeFallbackKeys((data['settings'] as Map)['secrets'] as Map);
        }
      } on ApiException {
        // нет настроек этой программы
      }
    }
    _fallbackLoaded = true;
  }

  /// Запасной ответ: OpenAI-совместимый чат Groq (openai/gpt-oss-120b), затем OpenRouter (openrouter/free) — как AiProviders кассы.
  Future<String?> _fallbackChat(String system, List<({bool user, String text})> history, List<String> errors) async {
    await _loadFallbackKeys();
    final messages = [
      {'role': 'system', 'content': system},
      for (final m in history) {'role': m.user ? 'user' : 'assistant', 'content': m.text},
    ];
    for (final (name, url, model, k) in [
      ('Groq', 'https://api.groq.com/openai/v1/chat/completions', 'openai/gpt-oss-120b', _groq),
      ('OpenRouter', 'https://openrouter.ai/api/v1/chat/completions', 'openrouter/free', _openRouter),
    ]) {
      if (k == null) continue;
      try {
        final r = await _http.post<Map<String, dynamic>>(
          url,
          data: {'model': model, 'messages': messages, 'temperature': 0.4, 'max_tokens': 1500},
          options: Options(headers: {'Authorization': 'Bearer $k'}, validateStatus: (_) => true),
        );
        final data = r.data ?? const {};
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
    await _loadFallbackKeys();
    return _groq != null || _openRouter != null;
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
          final r = await _http.post<Map<String, dynamic>>(
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
          final e = r.data?['error'];
          last = (e is Map ? e['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
          if (r.statusCode == 400 || r.statusCode == 401 || r.statusCode == 403) return last;
        }
        return last;
      }
      final (url, model) = provider == 'groq'
          ? ('https://api.groq.com/openai/v1/chat/completions', 'openai/gpt-oss-120b')
          : ('https://openrouter.ai/api/v1/chat/completions', 'openrouter/free');
      final r = await _http.post<Map<String, dynamic>>(
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
      final e = r.data?['error'];
      return (e is Map ? e['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
    } on DioException catch (e) {
      return e.message ?? 'нет связи';
    }
  }

  Future<void> saveKey(String value) async {
    _cached = value.trim().isEmpty ? null : value.trim();
    if (_cached == null) {
      await _storage.delete(key: _keyName);
    } else {
      await _storage.write(key: _keyName, value: _cached);
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
      for (final png in refs[i].$2) {
        parts.add({
          'inline_data': {'mime_type': 'image/png', 'data': base64Encode(png)},
        });
      }
    }
    final body = {
      'contents': [
        {'role': 'user', 'parts': parts},
      ],
      'generationConfig': {'temperature': 0.0, 'responseMimeType': 'application/json', 'maxOutputTokens': 512},
    };
    for (final model in [?_workingModel, ..._models.where((m) => m != _workingModel)]) {
      try {
        final r = await _http.post<Map<String, dynamic>>(
          'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
          data: body,
          options: Options(headers: {'x-goog-api-key': k}, validateStatus: (_) => true),
        );
        if ((r.statusCode ?? 0) >= 400) continue;
        final text = _text(r.data ?? const {});
        if (text == null) continue;
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
        // следующая модель
      }
    }
    return const [];
  }

  /// 2026-10-20, владелец: «умной камерой сканирует вместе с показаниями весов». Один кадр: товар на весах и вес
  /// с табло (тем же ключом Gemini). [withProduct] = false — только вес (повторные кадры, пока вес устанавливается).
  /// [names] — весовые товары каталога: ИИ называет товар так же, как в магазине, и совпадение находится точнее.
  Future<ScaleReading> readScale(Uint8List jpeg, {bool withProduct = true, List<String> names = const []}) async {
    final k = await key();
    if (k == null) throw const VisionException('no key', noKey: true);
    final task = StringBuffer()
      ..write('На фото — торговые весы магазина и товар на них. ')
      ..write(withProduct ? '1) Какой товар лежит на весах (как называют в магазине: «Картофель», «Лук репчатый», «Яблоки»). ' : '')
      ..write('${withProduct ? '2' : '1'}) Прочитай ВЕС (окно «МАССА»/«ВЕС»/«WEIGHT», кг) на табло весов. ')
      ..write('Не путай с окнами «ЦЕНА» и «СТОИМОСТЬ». На табло 0 или прочерки — weight_kg: 0. Табло не видно или не читается — weight_kg: null. ')
      ..write('Табло в граммах (например 500 g) — переведи в кг (0.5) и unit: "g".');
    if (withProduct && names.isNotEmpty) task.write(' Если товар есть в этом списке — назови его точно так: ${names.take(60).join('; ')}.');
    final body = {
      'systemInstruction': {
        'parts': [
          {
            'text':
                'Ты читаешь табло весов и узнаёшь товар по фото. Отвечай ТОЛЬКО JSON-объектом {"product": строка, "weight_kg": число или null, "unit": "kg" или "g", "confidence": число 0..1}. Не выдумывай: не уверен — снижай confidence.',
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
      'generationConfig': {'temperature': 0.0, 'responseMimeType': 'application/json', 'maxOutputTokens': 1024},
    };
    String? lastError;
    for (final model in [?_workingModel, ..._models.where((m) => m != _workingModel)]) {
      try {
        final r = await _http.post<Map<String, dynamic>>(
          'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
          data: body,
          options: Options(headers: {'x-goog-api-key': k}, validateStatus: (_) => true),
        );
        final data = r.data ?? const {};
        if ((r.statusCode ?? 0) >= 400) {
          lastError = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
          if (r.statusCode == 400 && (lastError.contains('API key') || lastError.contains('API_KEY'))) throw VisionException(lastError, noKey: true);
          continue;
        }
        final text = _text(data);
        if (text == null) {
          lastError = 'пустой ответ';
          continue;
        }
        _workingModel = model;
        return parseScaleJson(text);
      } on DioException catch (e) {
        lastError = e.message;
      }
    }
    throw VisionException(lastError ?? 'ИИ недоступен');
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
    final body = {
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
      'generationConfig': {'temperature': 0.1, 'responseMimeType': 'application/json', 'maxOutputTokens': 4096},
    };
    final models = [?_workingModel, ..._models.where((m) => m != _workingModel)];
    String? lastError;
    for (final model in models) {
      try {
        final r = await _http.post<Map<String, dynamic>>(
          'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
          data: body,
          options: Options(headers: {'x-goog-api-key': k}, validateStatus: (_) => true),
        );
        final data = r.data ?? const {};
        if ((r.statusCode ?? 0) >= 400) {
          lastError = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
          if (r.statusCode == 400 && (lastError.contains('API key') || lastError.contains('API_KEY'))) {
            throw VisionException(lastError, noKey: true);
          }
          continue; // перегружена или нет у ключа — следующая модель, как у советника
        }
        final text = _text(data);
        if (text == null) {
          lastError = 'пустой ответ';
          continue;
        }
        _workingModel = model;
        return parseSeen(text);
      } on DioException catch (e) {
        lastError = e.message;
      }
    }
    throw VisionException(lastError ?? 'ИИ недоступен');
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
    if (k == null) {
      final errors = <String>[];
      final alt = await _fallbackChat(system, history, errors);
      if (alt != null) return alt;
      throw VisionException(errors.isEmpty ? 'no key' : errors.join('\n'), noKey: errors.isEmpty);
    }
    final body = {
      'systemInstruction': {
        'parts': [
          {'text': system},
        ],
      },
      'contents': [
        for (final m in history)
          {
            'role': m.user ? 'user' : 'model',
            'parts': [
              {'text': m.text},
            ],
          },
      ],
      'generationConfig': {'temperature': 0.4, 'maxOutputTokens': 1500},
    };
    String? lastError;
    for (final model in [?_workingModel, ..._models.where((m) => m != _workingModel)]) {
      try {
        final r = await _http.post<Map<String, dynamic>>(
          'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
          data: body,
          options: Options(headers: {'x-goog-api-key': k}, validateStatus: (_) => true),
        );
        final data = r.data ?? const {};
        if ((r.statusCode ?? 0) >= 400) {
          lastError = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
          if (r.statusCode == 400 && (lastError.contains('API key') || lastError.contains('API_KEY'))) throw VisionException(lastError, noKey: true);
          continue;
        }
        final text = _text(data);
        if (text != null) {
          _workingModel = model;
          return text.trim();
        }
      } on DioException catch (e) {
        lastError = e.message;
      }
    }
    final errors = <String>['Gemini: ${lastError ?? 'нет ответа'}'];
    final alt = await _fallbackChat(system, history, errors);
    if (alt != null) return alt;
    throw VisionException(errors.join('\n'));
  }

  static String? _text(Map<String, dynamic> data) {
    final c = data['candidates'];
    if (c is! List || c.isEmpty) return null;
    final parts = ((c.first as Map)['content'] as Map?)?['parts'];
    if (parts is! List) return null;
    final t = parts.map((p) => (p as Map)['text']?.toString() ?? '').join();
    return t.trim().isEmpty ? null : t;
  }

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

  static Set<String> _words(String s) => s.toLowerCase().replaceAll(RegExp(r'[«»"().,/\\-]'), ' ').split(RegExp(r'\s+')).where((w) => w.length >= 2).toSet();

  /// Совпадения в каталоге: штрихкод — точно; иначе доля общих слов названия (бренд, название, объём).
  Future<List<SeenMatch>> match(List<SeenItem> items) async {
    final out = <SeenMatch>[];
    for (final s in items) {
      final found = <String, (Product, double)>{};
      if (s.barcode.length >= 8) {
        final p = await db.productByBarcode(s.barcode);
        if (p != null) found[p.id] = (p, 1.0);
      }
      final want = _words(s.title);
      for (final w in want.where((w) => w.length >= 3).take(4)) {
        for (final p in await db.searchProducts(w, limit: 40)) {
          final have = _words(p.name);
          if (have.isEmpty) continue;
          final common = want.intersection(have).length;
          final score = common / want.length.clamp(1, 99) * 0.6 + common / have.length * 0.4;
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
