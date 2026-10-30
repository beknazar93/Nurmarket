import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../api/api_client.dart';
import '../catalog/catalog_matcher.dart';
import '../catalog/product_matcher.dart' show autoGap, autoScore;
import '../db/database.dart';
import '../providers.dart';
import 'ai_access.dart';
import 'ai_image.dart';
import 'assistant/ai_journal.dart';
import 'cam_aliases.dart';
import 'gemini_keys.dart';
import 'scale_reading.dart';

/// Что увидела камера на снимке.
class SeenItem {
  final String name;
  final String brand;
  final String size;
  final String barcode;
  final int count;
  final double labelPrice;

  /// 2026-10-27: название по-русски, как назвал бы продавец (ИИ отвечает name_ru); пусто — ИИ его не дал.
  final String nameRu;
  const SeenItem({required this.name, this.brand = '', this.size = '', this.barcode = '', this.count = 1, this.labelPrice = 0, this.nameRu = ''});
  String get title => [brand, name, size].where((x) => x.trim().isNotEmpty).join(' ');

  /// 2026-10-27: то же по-русски («Спрайт 1 л» вместо «Sprite 1 L») — второй вариант для сверки с каталогом.
  String get titleRu => nameRu.trim().isEmpty ? '' : [brand, nameRu, size].where((x) => x.trim().isNotEmpty).join(' ');
}

/// Увиденное + лучшие совпадения в каталоге (score 0..1; ≥ 0.99 — штрихкод совпал).
class SeenMatch {
  final SeenItem seen;
  final List<(Product, double)> candidates;
  const SeenMatch(this.seen, this.candidates);
  Product? get best => candidates.isNotEmpty && candidates.first.$2 >= 0.5 ? candidates.first.$1 : null;

  /// 2026-10-27: товар, выбранный БЕЗ вопроса: лучший ≥ 0,85 и отрыв от второго ≥ 0,15 (иначе выбирает человек). Остаток на
  /// складе уходит только такому товару — «похоже» ([best]) для денег не годится.
  Product? get picked {
    if (candidates.isEmpty || candidates.first.$2 < autoScore) return null;
    if (candidates.length > 1 && candidates.first.$2 - candidates[1].$2 < autoGap) return null;
    return candidates.first.$1;
  }
}

/// 2026-10-27: чем закончилась попытка ИИ — для понятного текста вместо общего «не ответил».
enum VisionIssue { quota, keyRejected, timeout }

class VisionException implements Exception {
  final String message;
  final bool noKey;

  /// 2026-10-27: причина (лимит / Google отклонил ключ / долго не отвечает) и момент, когда можно пробовать снова.
  final VisionIssue? issue;
  final DateTime? until;
  const VisionException(this.message, {this.noKey = false, this.issue, this.until});

  /// Нужна кнопка «Настройка ИИ»: ключа нет, его отклонил Google или кончился лимит (поможет второй ключ).
  bool get showSettings => noKey || issue == VisionIssue.quota || issue == VisionIssue.keyRejected;
  @override
  String toString() => message;
}

/// 2026-10-27: что случилось с последней попыткой чата (код, модель, хвост ключа) — для журнала и экрана «Ключи Gemini».
class _ChatFail {
  /// Общий срок вышел — фоновая работа этого вопроса больше не шлёт запросов в Google (лимит общий с кассой и ботом).
  bool cancelled = false;

  /// 2026-10-27: короткий вопрос без сводки магазина — первой модели даём меньше времени.
  bool fast = false;
  VisionIssue? issue;
  DateTime? until;
  String? model;
  Object? code;
  String? keyTail;
  String? detail;
}

/// 2026-10-26 (владелец: ИИ — не больше 2 сотрудникам магазина): у этого сотрудника нет права на ИИ. [message] уже на языке
/// интерфейса («ИИ доступен только 2 сотрудникам магазина: …. Обратитесь к владельцу»); наследует VisionException, поэтому
/// старые `on VisionException` показывают его как есть, а не «нет ключа». [names] — кому ИИ разрешён.
class AiAccessDenied extends VisionException {
  final List<String> names;
  const AiAccessDenied(super.message, {this.names = const []});
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
  static String? _workingModel;
  final Dio _http;

  /// 2026-10-29 (полная изоляция аккаунтов): секреты ИИ в защищённом хранилище телефона — вход другим аккаунтом их стирает.
  static const accountSecretKeys = [_keyName, _sharedKeyName, _badKeysName, _extraName, 'ai.groq', 'ai.openrouter'];

  /// 2026-10-29: вошёл другой аккаунт — всё запомненное в памяти (ключи, модель, правило ИИ, «не подошло») забывается.
  static void forgetAccount() {
    _workingModel = null;
    _thinkStep.clear();
    _list = const [];
    _cloudList = const [];
    GeminiKeyPool.reset();
    _cachedFor = null;
    _cachedAt = DateTime(2000);
    _noKeyUntil = DateTime(2000);
    _bad = null; // перечитать из хранилища телефона (как после перезапуска)
    _groq = null;
    _openRouter = null;
    _fallbackLoaded = false;
    lastChatError = null;
    lastChatModel = null;
    AiAccessRepository.resetForTest();
  }

  /// 2026-10-22 (советник мобилки): сброс запомненных модели и шагов «размышлений» — для тестов.
  @visibleForTesting
  static void resetForTest() => forgetAccount();

  /// 2026-10-27: прошло 10 минут — запомненный список ключей надо перечитать (для тестов; без выхода из программы).
  @visibleForTesting
  static void expireCacheForTest() {
    _cachedAt = DateTime(2000);
    _noKeyUntil = DateTime(2000);
  }

  // 2026-10-22 (советник мобилки, аудит С-1…С-4): где искать ключ и что уже известно.
  static const _sharedKeyName = 'ai.gemini.shared';
  static const _badKeysName = 'ai.gemini.bad';
  // 2026-10-27 (дополнительные ключи): остальные ключи списка (строка JSON, формат — gemini_keys.dart).
  static const _extraName = 'ai.gemini.extra';
  // Список ключей Gemini: основной первым. Это и есть «запомненное» на 10 минут (раньше — один _cached).
  static List<GeminiKeyEntry> _list = const [];
  // Что в последний раз ответило облако (нужно, чтобы при замене знать, какие облачные ключи заменены).
  static List<GeminiKeyEntry> _cloudList = const [];
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
      await _storage.write(key: _badKeysName, value: jsonEncode(list.length > 60 ? list.sublist(list.length - 60) : list));
    } catch (_) {
      // хранилище телефона недоступно — помним до перезапуска
    }
  }

  /// 2026-10-22 (аудит С-2): ключ не подошёл (Google ответил «API key not valid» и т. п.) — запоминаем его отпечаток и больше
  /// не берём, пока ключ в облаке не сменится (новый ключ — новый отпечаток) или ключ не вставят вручную заново.
  /// 2026-10-27: в этот же список попадают ключи, которые пользователь ЗАМЕНИЛ или убрал (см. [_retire]): иначе старый ключ из
  /// облака («облако первым») через 10 минут или после перезапуска возвращался и перекрывал замену.
  Future<void> markBadKey(String k) async {
    if (k.trim().isEmpty) return;
    (await _badKeys()).add(_fp(k));
    await _saveBad();
    _cachedAt = DateTime(2000);
    _noKeyUntil = DateTime(2000);
  }

  bool _isBad(String key) => _bad?.contains(_fp(key)) ?? false;

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
        // 2026-10-27: список ключей (TelegramAiKeysExtra) сервер может отдать и объектом, а не строкой JSON
        final t = (v is Map || v is List ? jsonEncode(v) : v?.toString())?.trim() ?? '';
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
    _list = const [];
    _cloudList = const [];
    _cachedAt = DateTime(2000);
    _noKeyUntil = DateTime(2000);
    _groq = null;
    _openRouter = null;
    _fallbackLoaded = false;
  }

  String get _companyTag => '${api.session?.companyInn ?? ''}|${api.session?.companyName ?? ''}';

  /// 2026-10-26 (владелец: ИИ — не больше 2 сотрудникам): правило «кому разрешён ИИ» (общие настройки магазина + копия телефона).
  Future<AiAccessPolicy> accessPolicy({bool force = false}) => AiAccessRepository(api, db).load(force: force);

  /// 2026-10-26: ЕДИНАЯ проверка права на ИИ. Без права — [AiAccessDenied] с понятным текстом (на языке интерфейса). Её делают
  /// key(), fallbackKeys() и hasChatKey(), а через них — чат Советника, звонок, умная камера, накладная по фото, весы по виду,
  /// описание товара: отдельно проверять нигде не нужно (но экран может спросить заранее — звонок, до запроса микрофона).
  Future<void> requireAccess() async {
    final policy = await accessPolicy();
    if (AiAccess.canUse(api.session, policy)) return;
    throw AiAccessDenied(AiAccess.deniedText(ApiText.l, policy), names: [for (final u in policy.users ?? const <AiAccessUser>[]) u.name]);
  }

  /// Ключ Gemini для запросов к ИИ. Без права на ИИ — [AiAccessDenied] (см. [requireAccess]).
  Future<String?> key() async {
    await requireAccess();
    return _loadKey();
  }

  Future<String?> _loadKey() async {
    await _loadList();
    return _firstUsable();
  }

  /// Первый рабочий ключ списка (включённый, не заменённый, не «недействителен», со свободным лимитом хоть у одной модели).
  String? _firstUsable() => GeminiKeyPool.pickAny(_list, [..._models, _liveModel], retired: _isBad);

  /// Имя «модели» для отметок лимита звонка (Live считает квоту отдельно).
  static const _liveModel = 'live';

  /// 2026-10-27: читает список ключей Gemini из облака и телефона и запоминает на 10 минут в [_list].
  Future<void> _loadList() async {
    _checkUser();
    final now = DateTime.now();
    // 2026-10-22 (аудит С-4): запомненный список — 10 минут, потом перечитать (ключ сменили на ПК — телефон подхватит).
    if (_list.isNotEmpty && now.difference(_cachedAt) < const Duration(minutes: 10)) return;
    // 2026-10-22 (аудит С-3): «ключа нет» помним 5 минут — без повторных ожиданий при каждом открытии камеры.
    if (_list.isEmpty && now.isBefore(_noKeyUntil)) return;
    final bad = await _badKeys();

    // 2026-10-22 (советник мобилки): порядок источников.
    // 1) Личные настройки NurCRM (как у кассы и программы владельца). Было: сначала ключ, когда-то вставленный на телефоне, и
    //    облако не читалось вовсе — владелец сменил ключ на ПК (сейчас в облаке ключ вида «AQ.…»), а телефон остался со старым.
    // 2) Вставленный на телефоне вручную.
    // 3) Ключ этого телефона от прежнего входа владельца или администратора той же компании (аудит С-1). Так же на ПК: ключ ИИ
    //    хранится в настройках компьютера (UserPreferences), и кассир на кассе пользуется ключом, который ввёл владелец.
    // 2026-10-27 (владелец: «смена ключа не работает»): ключи, которые пользователь заменил или убрал на телефоне, лежат в списке
    // `bad` (см. _retire) — старый ключ из облака («облако первым») больше не перекрывает замену. Новый ключ с ПК (другой отпечаток)
    // подхватывается как раньше. Основной ключ и дополнительные берутся из ОДНОГО источника — облако или телефон.
    final cloud = await cloudSecrets();
    if (cloud != null) {
      _takeFallbackKeys(cloud);
      _cloudList = GeminiKeys.parse(cloud['TelegramAiKey']?.toString(), cloud['TelegramAiKeysExtra']);
    }
    var list = <GeminiKeyEntry>[];
    final cloudMain = GeminiKeys.clean(cloud?['TelegramAiKey']);
    if (cloudMain.isNotEmpty && !bad.contains(_fp(cloudMain))) {
      list = _cloudList;
      try {
        final shared = '$_companyTag\n$cloudMain';
        if (await _read(_sharedKeyName) != shared) await _storage.write(key: _sharedKeyName, value: shared);
      } catch (_) {
        // не сохранился на телефоне — в этот раз ключ всё равно есть
      }
    }
    if (list.isEmpty) list = GeminiKeys.parse(await _read(_keyName), await _read(_extraName));
    if (list.isEmpty) {
      final shared = await _read(_sharedKeyName);
      final i = shared?.lastIndexOf('\n') ?? -1;
      if (shared != null && i >= 0 && shared.substring(0, i) == _companyTag) {
        final k = GeminiKeys.clean(shared.substring(i + 1));
        if (k.isNotEmpty && !bad.contains(_fp(k))) list = [GeminiKeyEntry(key: k)];
      }
    }
    // Нет связи — прежний список этого запуска лучше, чем ничего.
    if (list.isEmpty && cloud == null && _list.isNotEmpty) list = _list.where((e) => !bad.contains(_fp(e.key))).toList();
    _list = list;
    _cachedAt = now;
    if (list.isEmpty) _noKeyUntil = now.add(const Duration(minutes: 5));
  }

  /// 2026-10-27: пользователь заменил или убрал ключ — старый больше не берём ни из облака, ни из «общего» ключа телефона.
  Future<void> _retire(Iterable<String> keys) async {
    final bad = await _badKeys();
    var changed = false;
    for (final k in keys) {
      if (k.trim().isNotEmpty && bad.add(_fp(k))) changed = true;
    }
    if (changed) await _saveBad();
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
    await requireAccess();
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

  /// 14.10, «Настройка ИИ»: текущие ключи трёх поставщиков (null — нет). gemini — первый рабочий ключ списка.
  Future<({String? gemini, String? groq, String? openRouter})> allKeys() async {
    // 2026-10-26: «Настройку ИИ» открывают и владелец с администратором, которых нет в списке ИИ, — им ключи видны всегда.
    if (!AiAccess.canManage(api.session)) await requireAccess();
    final g = await _loadKey();
    await _loadFallbackKeys();
    return (gemini: g, groq: _groq, openRouter: _openRouter);
  }

  /// 2026-10-27: весь список ключей Gemini (основной первым) для экрана «Ключи Gemini». Право — как у [allKeys]. [refresh] —
  /// перечитать из облака и телефона, не ждать 10 минут.
  Future<List<GeminiKeyEntry>> geminiEntries({bool refresh = false}) async {
    if (!AiAccess.canManage(api.session)) await requireAccess();
    if (refresh) {
      _cachedAt = DateTime(2000);
      _noKeyUntil = DateTime(2000);
    }
    await _loadList();
    return List.of(_list);
  }

  /// Состояние ключа для экрана: рабочий / исчерпан до … / недействителен / выключен / не проверен.
  (GeminiKeyState, DateTime?) geminiStateOf(GeminiKeyEntry e) => GeminiKeyPool.stateOf(e, retired: _isBad(e.key));

  /// «Рабочих ключей: N из M».
  (int, int) geminiCounts(List<GeminiKeyEntry> list) => GeminiKeyPool.counts(list, retired: _isBad);

  /// Отправка секретов в облако (личные настройки NurCRM: касса и программа владельца). Отдельным методом — для тестов на фейках.
  /// Касса обязательна (ошибка — исключение), программы владельца у пользователя может не быть.
  @visibleForTesting
  Future<void> pushSecrets(Map<String, Object?> secrets) async {
    await api.patch('api/users/app-settings/nurmarket-kassa/', body: {'secrets': secrets});
    try {
      await api.patch('api/users/app-settings/nurmarket-owner/', body: {'secrets': secrets});
    } on ApiException {
      // программы владельца у пользователя может не быть — ключи уже на кассе и на телефоне
    }
  }

  /// Сохранить ключи: на телефоне и в личных настройках NurCRM (secrets — как SettingsCloudSync кассы), чтобы работали
  /// и на кассе, и в программе владельца. null — не менять, пустая строка — убрать ключ.
  /// 2026-10-27: gemini — это ОСНОВНОЙ ключ (первый в списке); остальные ключи списка не трогаются. Возвращает true, если
  /// облако приняло ключи; false — на телефоне сохранено и работает, а в облако не ушло (раньше здесь падало исключение уже
  /// ПОСЛЕ записи на телефон, а старый ключ из облака через 10 минут возвращался и перекрывал замену).
  Future<bool> saveAllKeys({String? gemini, String? groq, String? openRouter}) async {
    List<GeminiKeyEntry>? list;
    if (gemini != null) {
      await _loadList();
      final k = GeminiKeys.clean(gemini);
      final rest = _list.skip(1).toList();
      list = k.isEmpty ? rest : [GeminiKeyEntry(label: _list.isEmpty ? '' : _list.first.label, key: k), ...rest];
    }
    return _store(gemini: list, groq: groq, openRouter: openRouter);
  }

  /// 2026-10-27: сохранить весь список ключей Gemini (порядок, метки, «включён»). Пустые и повторы отбрасываются, не больше 10.
  /// Основной ключ уходит как раньше (TelegramAiKey), остальные — TelegramAiKeysExtra. true — облако приняло.
  Future<bool> saveGeminiKeys(List<GeminiKeyEntry> entries) async {
    await _loadList(); // чтобы знать, какие ключи заменены (их нельзя отдать обратно из облака)
    return _store(gemini: entries);
  }

  Future<bool> _store({List<GeminiKeyEntry>? gemini, String? groq, String? openRouter, bool toCloud = true}) async {
    Future<void> local(String name, String? v) async {
      if (v == null) return;
      try {
        if (v.trim().isEmpty) {
          await _storage.delete(key: name);
        } else {
          await _storage.write(key: name, value: v.trim());
        }
      } catch (_) {
        // хранилище телефона недоступно — работает до перезапуска (как saveKey)
      }
    }

    final secrets = <String, Object?>{};
    if (gemini != null) {
      final clean = GeminiKeys.normalize(gemini);
      final (main, extra) = GeminiKeys.encode(clean);
      // Всё, что было в списке или в облаке, а теперь нет, — заменено: старый ключ не вернётся из облака (см. _loadList).
      final keep = {for (final e in clean) e.key};
      final known = <String>{for (final e in [..._list, ..._cloudList]) e.key};
      final localMain = GeminiKeys.clean(await _read(_keyName));
      if (localMain.isNotEmpty) known.add(localMain);
      final shared = await _read(_sharedKeyName);
      if (shared != null) known.add(GeminiKeys.clean(shared.substring(shared.lastIndexOf('\n') + 1)));
      await _retire(known.where((k) => k.isNotEmpty && !keep.contains(k)));
      for (final e in clean) {
        if (_isBad(e.key) || !_list.any((x) => x.key == e.key)) {
          await _unmarkBad(e.key); // ключ вставили заново — он снова в работе
          GeminiKeyPool.clear(e.key);
        }
      }
      await local(_keyName, main ?? '');
      await local(_extraName, extra ?? '');
      _list = clean;
      secrets['TelegramAiKey'] = main;
      secrets['TelegramAiKeysExtra'] = extra;
    }
    await local('ai.groq', groq);
    await local('ai.openrouter', openRouter);
    _cachedAt = DateTime.now();
    _noKeyUntil = DateTime(2000);
    if (groq != null) {
      _groq = groq.trim().isEmpty ? null : groq.trim();
      secrets['GroqApiKey'] = _groq;
    }
    if (openRouter != null) {
      _openRouter = openRouter.trim().isEmpty ? null : openRouter.trim();
      secrets['OpenRouterApiKey'] = _openRouter;
    }
    if (secrets.isEmpty || !toCloud) return true;
    try {
      await pushSecrets(secrets);
      return true;
    } catch (_) {
      return false;
    }
  }

  /// Лёгкая проверка ключа Gemini: GET списка моделей (не тратит лимит генерации). Обновляет состояние ключа: рабочий снимает
  /// отметки «исчерпан» и «недействителен». error — текст для показа (сам ключ из него убран).
  Future<({GeminiKeyState state, DateTime? until, String? error})> checkGeminiKey(String key) async {
    final k = GeminiKeys.clean(key);
    String hide(String m) => k.isEmpty ? m : m.replaceAll(k, '***${GeminiKeys.tail(k)}');
    try {
      final r = await _http.get<dynamic>(
        'https://generativelanguage.googleapis.com/v1beta/models',
        queryParameters: {'pageSize': 1},
        options: Options(headers: {'x-goog-api-key': k}, validateStatus: (_) => true, sendTimeout: const Duration(seconds: 15), receiveTimeout: const Duration(seconds: 15)),
      );
      final status = r.statusCode ?? 0;
      if (status < 400) {
        GeminiKeyPool.clear(k);
        GeminiKeyPool.reportSuccess(k);
        await _unmarkBad(k);
        return (state: GeminiKeyState.working, until: null, error: null);
      }
      final body = r.data is Map || r.data is List ? jsonEncode(r.data) : '${r.data}';
      final e = r.data is Map ? (r.data as Map)['error'] : null;
      final message = hide((e is Map ? e['message']?.toString() : null) ?? 'HTTP $status');
      if (GeminiKeyPool.reportFailure(k, '*', status, body)) {
        final st = GeminiKeyPool.stateOf(GeminiKeyEntry(key: k));
        return (state: st.$1, until: st.$2, error: st.$1 == GeminiKeyState.invalid ? message : null);
      }
      return (state: GeminiKeyState.unchecked, until: null, error: message);
    } on DioException catch (e) {
      return (state: GeminiKeyState.unchecked, until: null, error: e.message ?? 'нет связи');
    } catch (_) {
      return (state: GeminiKeyState.unchecked, until: null, error: 'нет связи');
    }
  }

  /// Проверка ключа; null — работает, иначе текст ошибки. Gemini — лёгкий запрос списка моделей, остальные — короткий чат.
  Future<String?> testKey(String provider, String k) async {
    k = GeminiKeys.clean(k);
    try {
      if (provider == 'gemini') {
        final r = await checkGeminiKey(k);
        if (r.state == GeminiKeyState.working) return null;
        if (r.state == GeminiKeyState.exhausted) return 'исчерпан лимит запросов Google${r.until == null ? '' : ' до ${hm(r.until!)}'}';
        return r.error ?? 'ключ не подходит';
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

  /// Часы:минуты для «исчерпан до …».
  static String hm(DateTime t) => '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

  /// Один ключ (диалог умной камеры «вставьте ключ»). Пустая строка — ключ не подошёл. Вставленный вручную ключ ставится
  /// ПЕРВЫМ и перекрывает прежний, в том числе облачный (раньше старый облачный ключ возвращался через 10 минут).
  Future<void> saveKey(String value) async {
    // 2026-10-22 (аудит С-2): saveKey('') зовут, когда ключ не подошёл (умная камера), — запоминаем его как «плохой»,
    // иначе key() тут же снова брал тот же ключ из облака. Вставленный вручную — снимаем отметку.
    final k = GeminiKeys.clean(value);
    if (k.isEmpty) {
      // не подошёл тот ключ, который Google только что отклонил (в списке он уже помечен «недействителен»), иначе — первый рабочий
      // 2026-10-27: только временная пометка на 30 минут (как у ротации ключей), а не «навсегда»: один отказ Google раньше выключал
      // общий облачный ключ на этом телефоне даже после перезапуска. Ключ не стирается.
      final rejected = [for (final e in _list) if (GeminiKeyPool.isInvalid(e.key)) e.key];
      final cur = rejected.isNotEmpty ? rejected : [?_firstUsable()];
      for (final x in cur) {
        GeminiKeyPool.markInvalid(x);
      }
      _noKeyUntil = DateTime(2000);
      return;
    }
    await _loadList();
    final rest = _list.skip(1).where((e) => e.key != k).toList();
    await _store(gemini: [GeminiKeyEntry(label: _list.isEmpty ? '' : _list.first.label, key: k), ...rest], toCloud: false);
  }

  /// mode: sale — что держит покупатель (обычно 1–3 товара); shelf — полка; invoice — накладная; labels — этикетки.
  /// 14.10, владелец: «при скане умной камерой для корзины сразу находит товар точно». Второй шаг после recognize:
  /// кадр + эталонные снимки товаров (3 снимка «справа, слева, сверху» с телефона или фото из NurCRM) — ИИ называет
  /// номера эталонов, которые есть на кадре, и сколько штук. Возвращает (номер эталона, штук).
  Future<List<(int, int)>> identify(Uint8List shot, List<(String name, List<Uint8List> pngs)> refs) async {
    final k = await key();
    if (k == null || refs.isEmpty) return const [];
    shot = await shrinkForAi(shot); // 2026-10-30 (оптимизация, п. 5): ≤ 1280 px вместо 1–3 МБ с камеры
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

  /// 2026-10-27 (дополнительные ключи): ключ для запроса к [model] — «первый рабочий» из списка (включённый, не заменённый, не
  /// «недействителен», не «исчерпан» для этой модели). [k] — ключ, с которым пришёл вызывающий; если его нет в списке (подставлен
  /// снаружи, например в тестах), ротации нет и используется он. null — свободных ключей для модели нет.
  /// 2026-10-27: [skip] — пары «ключ\nмодель», которым Google ответил отказом без явной причины (403): в этом вопросе их не повторяем.
  String? _keyFor(String model, String k, {Set<String> skip = const {}}) {
    final list = _list;
    if (list.isEmpty || !list.any((e) => e.key == k)) return skip.contains('$k\n$model') ? null : k;
    return GeminiKeyPool.pick(model, list, retired: (key) => _isBad(key) || skip.contains('$key\n$model'));
  }

  /// Ответ Google с ошибкой: лимит (429/RESOURCE_EXHAUSTED/quota) — ключ «исчерпан до …» (минутный — retryDelay или 60 с, суточный —
  /// до 00:00 тихоокеанского времени); «API key not valid», 401, 403 — «недействителен» (снова пробуется через 30 минут).
  /// true — ключ помечен и есть другой: тот же запрос стоит повторить с ним (только чтение/генерация, побочных действий нет).
  bool _rotate(String used, String model, String k, int status, Object? data) {
    final body = data is Map || data is List ? jsonEncode(data) : '$data';
    if (!GeminiKeyPool.reportFailure(used, model, status, body)) return false;
    final next = _keyFor(model, k);
    return next != null && next != used;
  }

  /// Текст, когда для модели не осталось ключей: «исчерпан лимит у всех ключей (N), следующая попытка после HH:mm».
  String _noKeyText(String model) {
    final on = _list.where((e) => e.enabled).length;
    final next = GeminiKeyPool.nextFree(_list, model);
    final when = next == null ? '' : ', следующая попытка после ${hm(next)}';
    return on > 1 ? 'исчерпан бесплатный лимит у всех ключей Gemini ($on)$when' : 'исчерпан бесплатный лимит запросов Google$when';
  }

  /// 2026-10-27: ключи есть, но для модели свободных нет — лимит исчерпан или Google отклонил ключ (повтор через 30 минут).
  /// Записывает причину в [fail] (только если [first]: до этого ничего не случилось) и возвращает текст.
  String _noFreeKey(String model, _ChatFail fail, bool first) {
    DateTime? rejected;
    for (final e in _list.where((e) => e.enabled && !_isBad(e.key))) {
      final u = GeminiKeyPool.invalidUntil(e.key);
      if (u != null && (rejected == null || u.isBefore(rejected))) rejected = u;
    }
    final free = _list.any((e) => e.enabled && !_isBad(e.key) && !GeminiKeyPool.isInvalid(e.key));
    if (rejected != null && !free) {
      if (first) {
        fail.issue = VisionIssue.keyRejected;
        fail.until = rejected;
      }
      return 'Google отклонил ключ, повтор после ${hm(rejected)}';
    }
    if (first) {
      fail.issue = VisionIssue.quota;
      fail.until = GeminiKeyPool.nextFree(_list, model);
    }
    return _noKeyText(model);
  }

  /// 2026-10-27: ключи в списке есть, но все временно отклонены Google (30 минут) — ошибка «Google отклонил ключ, повтор после HH:mm»
  /// с кнопкой «Настройка ИИ»; ключей нет совсем — прежнее «Добавьте ключ ИИ…». Зовёт экран Советника, когда [hasChatKey] = false.
  VisionException noKeyFailure({bool note = true}) {
    DateTime? until;
    for (final e in _list.where((e) => e.enabled && !_isBad(e.key))) {
      final u = GeminiKeyPool.invalidUntil(e.key);
      if (u != null && (until == null || u.isBefore(until))) until = u;
    }
    if (until == null) return const VisionException('no key', noKey: true);
    final fail = _ChatFail()
      ..issue = VisionIssue.keyRejected
      ..until = until
      ..code = 'ключ';
    final e = VisionException('Google отклонил ключ, повтор после ${hm(until)}', issue: VisionIssue.keyRejected, until: until);
    if (note) _noteChatError(fail, e);
    return e;
  }

  /// Последняя ошибка чата Советника в этом запуске (для экрана «Ключи Gemini»; после перезапуска читается из журнала).
  static AiChatError? lastChatError;

  /// 2026-10-27: следующий вопрос чата короткий и без сводки магазина — первой модели даём меньше времени (12 с вместо 25).
  /// Флагом, а не параметром: у [chat] есть подменяющие её фейки в тестах. Сбрасывается самим [chat].
  bool fastChat = false;

  /// 2026-10-27: модель, ответившая на последний вопрос чата (для журнала замеров ai_perf).
  static String? lastChatModel;

  void _noteChatError(_ChatFail fail, VisionException e) {
    final n = AiChatError(
      at: GeminiKeyPool.clock(),
      code: '${fail.code ?? (e.issue == VisionIssue.timeout ? 'таймаут' : '—')}',
      model: fail.model ?? '',
      keyTail: fail.keyTail ?? '',
      text: (fail.detail ?? e.message).replaceAll(RegExp(r'\s+'), ' ').trim(),
    );
    lastChatError = n;
    unawaited(AiJournal.recordChatError(n));
  }

  /// 2026-10-27: звонок (Live) — ключ отказал ДО начала сеанса (лимит или «API key not valid»): помечаем его и отдаём следующий.
  /// null — других ключей нет (или причина не про ключ). Вызывается из LiveCall синхронно.
  String? rotateLiveKey(String failedKey, String reason) {
    if (_list.isEmpty || !_list.any((e) => e.key == failedKey)) return null;
    final r = reason.toLowerCase();
    // 2026-10-27: «недействителен» — только при явной причине от Google (как для обычных запросов), иначе звонок не выключает ключ
    if (GeminiKeyPool.isInvalidKeyText(reason)) {
      GeminiKeyPool.markInvalid(failedKey);
    } else if (['resource_exhausted', 'quota', 'exhausted', 'rate limit', 'rate_limit'].any(r.contains)) {
      GeminiKeyPool.markQuota(failedKey, _liveModel, reason);
    } else {
      return null;
    }
    final next = GeminiKeyPool.pick(_liveModel, _list, retired: _isBad);
    return next != null && next != failedKey ? next : null;
  }

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
      final used = _keyFor(model, k);
      if (used == null) {
        lastError ??= _noKeyText(model); // 2026-10-27: все ключи исчерпаны для этой модели — следующая модель
        continue;
      }
      try {
        final r = await _http.post<dynamic>(
          'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
          data: body(switch (step) {
            0 => {'thinkingBudget': 0},
            1 => {'thinkingLevel': 'low'},
            _ => null,
          }),
          options: Options(headers: {'x-goog-api-key': used}, validateStatus: (_) => true, sendTimeout: left, receiveTimeout: left),
        );
        final status = r.statusCode ?? 0;
        final data = r.data is Map ? (r.data as Map).cast<String, dynamic>() : const <String, dynamic>{};
        if (status >= 400) {
          final message = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP $status';
          if (_rotate(used, model, k, status, r.data)) {
            mi--; // 2026-10-27: ключ исчерпан или недействителен — тот же запрос со следующим ключом
            continue;
          }
          // 2026-10-27: «ключ не подошёл» — только при явной причине от Google (раньше любой 403 без слова model)
          if (GeminiKeyPool.isInvalidKey(status, r.data is Map || r.data is List ? jsonEncode(r.data) : '${r.data}')) {
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
            429 => _noKeyText(model), // 2026-10-27: и при одном ключе — «следующая попытка после HH:mm», а не «через минуту»
            >= 500 => 'нейросеть Google сейчас перегружена — попробуйте через минуту',
            _ => 'ошибка Google $status: $message',
          };
          continue;
        }
        final text = _text(data);
        if (text != null) {
          _workingModel = model;
          GeminiKeyPool.reportSuccess(used);
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
    jpeg = await shrinkForAi(jpeg); // 2026-10-30 (оптимизация, п. 5)
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

  Future<List<SeenItem>> recognize(Uint8List jpeg, {String mode = 'sale', String mime = 'image/jpeg'}) async {
    final k = await key();
    if (k == null) throw const VisionException('no key', noKey: true);
    // 2026-10-30 (оптимизация, п. 5): снимок для ИИ ≤ 1280 px (накладная — 2048 px, мелкий текст); PDF и чужой тип не трогаем
    if (mime == 'image/jpeg') jpeg = await shrinkForAi(jpeg, maxSide: mode == 'invoice' ? 2048 : 1280);
    final task = switch (mode) {
      'invoice' =>
        'На фото (или в файле) — накладная поставщика. Перечисли строки товаров: название как в накладной, количество (count), цена за штуку (label_price), штрихкод или артикул — если для него есть колонка.',
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
              // 2026-10-27: короткое name, name_ru — по-русски (каталог магазина в основном русский/смешанный), без описаний
              'text': 'Ты распознаёшь товары магазина в Кыргызстане по фото. Отвечай ТОЛЬКО JSON-массивом объектов {"name","name_ru","brand","size","barcode","count","label_price"}. name — как написано на упаковке, коротко (вид, вкус), без описаний и рекламных слов; brand — марка как на упаковке; size — объём или вес; name_ru — то же название по-русски, как назвал бы продавец. Не выдумывай: неуверен — пиши, что видно, barcode пустой.',
            },
          ],
        },
        'contents': [
          {
            'role': 'user',
            'parts': [
              {'text': task},
              {
                'inline_data': {'mime_type': mime, 'data': base64Encode(jpeg)},
              },
            ],
          },
        ],
        // 2026-10-22: в продаже на снимке 1–3 товара — короткий ответ быстрее; накладная и полка — длиннее
        'generationConfig': {'temperature': 0.1, 'responseMimeType': 'application/json', 'maxOutputTokens': mode == 'sale' ? 1024 : 4096, 'thinkingConfig': ?thinking},
      },
      total: Duration(seconds: mime == 'application/pdf' ? 40 : (mode == 'sale' ? 12 : 25)),
    );
    return parseSeen(text);
  }

  /// 13.10, владелец: «описание — вручную или из интернета». Короткое описание товара для сайта и бота: Gemini с поиском
  /// Google (как «Написать с Нур Советником» на кассе), по названию, штрихкоду и фото, если есть. Без поиска, если ключ его не умеет.
  Future<String> describe(String name, String barcode, {Uint8List? photo}) async {
    final k = await key();
    if (k == null) throw const VisionException('no key', noKey: true);
    if (photo != null) photo = await shrinkForAi(photo, maxSide: 1024); // 2026-10-30 (оптимизация, п. 5)
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
      final models = [?_workingModel, ..._models.where((m) => m != _workingModel)];
      for (var mi = 0; mi < models.length; mi++) {
        final model = models[mi];
        final used = _keyFor(model, k);
        if (used == null) {
          lastError ??= _noKeyText(model);
          continue;
        }
        try {
          final r = await _http.post<Map<String, dynamic>>(
            'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
            data: body(search),
            options: Options(headers: {'x-goog-api-key': used}, validateStatus: (_) => true),
          );
          final data = r.data ?? const {};
          if ((r.statusCode ?? 0) >= 400) {
            lastError = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP ${r.statusCode}';
            if (_rotate(used, model, k, r.statusCode ?? 0, data)) {
              mi--; // 2026-10-27: следующий ключ списка
              continue;
            }
            if (r.statusCode == 400 && GeminiKeyPool.isInvalidKeyText(lastError)) throw VisionException(lastError, noKey: true);
            continue;
          }
          final text = _text(data);
          if (text != null) {
            GeminiKeyPool.reportSuccess(used);
            return text.trim();
          }
        } on DioException catch (e) {
          lastError = e.message;
        }
      }
    }
    throw VisionException(lastError ?? 'ИИ недоступен');
  }

  /// 13.10, Нур Советник на телефоне: разговор (вопросы и ответы по очереди) со сводкой магазина в системной части.
  /// 2026-10-27: [budget] — общий срок ответа (с ожиданием подключения и запасными ИИ): по его истечении — ошибка «ИИ долго не
  /// отвечает» (VisionIssue.timeout) с кнопкой «Повторить», а не бесконечный индикатор. Последняя ошибка пишется в журнал ИИ.
  Future<String> chat(String system, List<({bool user, String text})> history, {Duration budget = const Duration(seconds: 45)}) async {
    final fail = _ChatFail()..fast = fastChat;
    fastChat = false;
    try {
      return await _chat(system, history, fail, budget).timeout(
        budget,
        onTimeout: () {
          fail.cancelled = true;
          fail.issue = VisionIssue.timeout;
          throw const VisionException('ИИ долго не отвечает…', issue: VisionIssue.timeout);
        },
      );
    } on VisionException catch (e) {
      if (e is! AiAccessDenied) _noteChatError(fail, e);
      rethrow;
    }
  }

  Future<String> _chat(String system, List<({bool user, String text})> history, _ChatFail fail, Duration budget) async {
    final k = await key();
    final errors = <String>[];
    if (k != null) {
      final text = await _geminiChat(k, system, history, errors, fail, budget);
      if (text != null) return text;
    }
    if (fail.cancelled) throw const VisionException('ИИ долго не отвечает…', issue: VisionIssue.timeout);
    final alt = await _fallbackChat(system, history, errors);
    if (alt != null) return alt;
    if (errors.isEmpty) {
      final e = noKeyFailure(note: false);
      if (e.issue != null) fail.code = 'ключ';
      throw e;
    }
    throw VisionException(errors.join('\n'), issue: fail.issue, until: fail.until);
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
  Future<String?> _geminiChat(
    String k,
    String system,
    List<({bool user, String text})> history,
    List<String> errors,
    _ChatFail fail,
    Duration budget,
  ) async {
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
    final denied = <String>{}; // 2026-10-27: пары «ключ\nмодель» с отказом 403 без явной причины — в этом вопросе не повторяем
    for (var mi = 0; mi < models.length; mi++) {
      final model = models[mi];
      if (fail.cancelled) break;
      final left = budget - started.elapsed; // 2026-10-27: было 50 с на Gemini; теперь общий бюджет чата (45 с)
      if (left < const Duration(seconds: 5)) {
        lastError ??= 'Google не ответил вовремя';
        fail.issue ??= VisionIssue.timeout;
        break;
      }
      // 2026-10-27 (расследование «ИИ долго думает»): короткий вопрос без сводки (fast) — первая модель не дольше 12 с, потом запасная
      final limit = mi == 0 ? (fail.fast ? const Duration(seconds: 12) : const Duration(seconds: 25)) : const Duration(seconds: 15);
      final step = _thinkStep[model] ?? (model.startsWith('gemini-3') ? 1 : 0);
      final used = _keyFor(model, k, skip: denied);
      if (used == null) {
        lastError ??= _noFreeKey(model, fail, lastError == null); // 2026-10-27: свободных ключей для модели нет — следующая модель
        continue;
      }
      fail
        ..issue = null
        ..until = null
        ..model = model
        ..code = null
        ..keyTail = GeminiKeys.tail(used)
        ..detail = null;
      final cap = limit < left ? limit : left;
      try {
        final r = await _http
            .post<dynamic>(
              'https://generativelanguage.googleapis.com/v1beta/models/$model:generateContent',
              data: body(step),
              options: Options(headers: {'x-goog-api-key': used}, validateStatus: (_) => true, sendTimeout: cap, receiveTimeout: cap),
            )
            // connectTimeout Dio (15 с) считается сверху — общий срок попытки держим сами
            .timeout(cap + const Duration(seconds: 1));
        final status = r.statusCode ?? 0;
        final data = r.data is Map ? (r.data as Map).cast<String, dynamic>() : const <String, dynamic>{};
        if (status >= 400) {
          final message = (data['error'] is Map ? (data['error'] as Map)['message']?.toString() : null) ?? 'HTTP $status';
          fail
            ..code = status
            ..detail = message;
          if (_rotate(used, model, k, status, r.data)) {
            mi--; // 2026-10-27: ключ исчерпан или недействителен — тот же вопрос со следующим ключом списка
            continue;
          }
          // 2026-10-27: «ключ не подошёл» — только при явной причине от Google (401, API key not valid/expired, leaked, suspended…).
          // Раньше так считался любой 403 без слова model, и после одного отказа чат 30 минут писал «Добавьте ключ ИИ».
          if (GeminiKeyPool.isInvalidKey(status, r.data is Map || r.data is List ? jsonEncode(r.data) : '${r.data}')) {
            // 2026-10-22 (аудит С-2): этот ключ больше не берём, пока его не сменят. 2026-10-27: теперь на 30 минут (GeminiKeyPool),
            // а не навсегда — ротация пробует следующий ключ, а этот снова проверит позже.
            GeminiKeyPool.markInvalid(used);
            fail
              ..issue = VisionIssue.keyRejected
              ..until = GeminiKeyPool.invalidUntil(used);
            errors.add('Gemini: ключ Google не подходит — проверьте его в «Настройка ИИ» ($message)');
            return null;
          }
          // Модель не принимает настройку «размышлений» (400) — тот же запрос со следующим шагом (как на ПК).
          if (status == 400 && step < 2) {
            _thinkStep[model] = step + 1;
            mi--;
            continue;
          }
          if (_workingModel == model) _workingModel = null;
          if (status == 429) {
            fail
              ..issue = VisionIssue.quota
              ..until = GeminiKeyPool.blockedUntil(used, model);
          }
          if (status == 403) {
            // 2026-10-27: отказ без явной причины (не про ключ) — не помечаем ключ; пробуем другой ключ на этой модели, затем другую модель
            denied.add('$used\n$model');
            if (_keyFor(model, k, skip: denied) != null) {
              mi--;
              continue;
            }
          }
          lastError = switch (status) {
            404 => 'модель $model недоступна',
            429 => _noKeyText(model), // 2026-10-27: и при одном ключе — «следующая попытка после HH:mm», а не «через минуту»
            >= 500 => 'нейросеть Google сейчас перегружена — попробуйте через минуту',
            _ => 'ошибка Google $status: $message',
          };
          continue; // следующая модель
        }
        final text = _text(data);
        if (text != null) {
          _workingModel = model;
          lastChatModel = model; // 2026-10-27: для журнала замеров ai_perf (только имя модели, не ключ)
          GeminiKeyPool.reportSuccess(used);
          return text.trim();
        }
        final c = data['candidates'];
        final reason = c is List && c.isNotEmpty && c.first is Map ? (c.first as Map)['finishReason']?.toString() : null;
        final blocked = data['promptFeedback'] is Map ? (data['promptFeedback'] as Map)['blockReason']?.toString() : null;
        lastError = blocked != null || reason == 'SAFETY' || reason == 'PROHIBITED_CONTENT'
            ? 'Google отказался отвечать на этот вопрос (${blocked ?? reason})'
            : 'нейросеть вернула пустой ответ${reason == null ? '' : ' ($reason)'}';
        fail.detail = lastError;
      } on TimeoutException {
        if (_workingModel == model) _workingModel = null;
        lastError = 'Google не ответил вовремя';
        fail
          ..issue = VisionIssue.timeout
          ..detail = lastError;
      } on DioException catch (e) {
        if (_workingModel == model) _workingModel = null;
        lastError = switch (e.type) {
          DioExceptionType.connectionTimeout || DioExceptionType.sendTimeout || DioExceptionType.receiveTimeout => 'Google не ответил вовремя',
          DioExceptionType.connectionError => 'нет связи с Google',
          _ => e.message ?? 'нет связи с Google',
        };
        fail
          ..code = e.type.name
          ..detail = lastError;
        if (e.type == DioExceptionType.connectionTimeout || e.type == DioExceptionType.sendTimeout || e.type == DioExceptionType.receiveTimeout) {
          fail.issue = VisionIssue.timeout;
        }
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
            nameRu: e['name_ru']?.toString().trim() ?? '',
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

  /// Совпадения в каталоге: штрихкод (с верной контрольной цифрой) — 0,97; иначе похожесть названия по ВСЕМУ каталогу телефона.
  /// 2026-10-27 (владелец: «на фото существующий товар не находит»): вместо поиска `LIKE` по первым 5 буквам (40 штук по
  /// алфавиту), «1л» как слова и латиницы без кириллицы — общий матчер [CatalogMatcher]: объём отдельно, одна азбука, веса слов,
  /// алиасы обучения, топ-5. Названия на латинице и по-русски (name_ru) пробуются оба — берётся лучшая оценка товара.
  Future<List<SeenMatch>> match(List<SeenItem> items) async {
    try {
      await CamAliases.ensureLocal(db);
      // общие алиасы компании подтягиваются в фоне — первый кадр не ждёт сервер
      if (api.session != null) CamAliases.ensure(db, api).then((_) {}, onError: (_) {});
    } catch (_) {}
    final out = <SeenMatch>[];
    for (final s in items) {
      final found = <String, (Product, double)>{};
      // 2026-10-22 (аудит У-6): штрихкод от ИИ — подсказка, не «точно»: проверяется цифра, ищется как скан
      if (validEan(s.barcode)) {
        final hit = await db.productByScan(s.barcode);
        if (hit != null) found[hit.product.id] = (hit.product, 0.97);
      }
      for (final q in {s.title, s.titleRu}) {
        if (q.trim().isEmpty) continue;
        // 2026-10-27 (ревью М-2): по фото без объёма в ответе ИИ товар с объёмом в названии — только вариант, не автовыбор
        for (final h in await CatalogMatcher.find(db, q, requireVolume: true)) {
          if (h.score > (found[h.product.id]?.$2 ?? 0)) found[h.product.id] = (h.product, h.score);
        }
      }
      final list = found.values.toList()..sort((a, b) => b.$2.compareTo(a.$2));
      out.add(SeenMatch(s, list.take(5).toList()));
    }
    return out;
  }
}

final visionProvider = Provider((ref) => VisionService(ref.watch(apiProvider), ref.watch(databaseProvider)));
