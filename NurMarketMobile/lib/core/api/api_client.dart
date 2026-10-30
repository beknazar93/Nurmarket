import 'dart:async';
import 'dart:math';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../../l10n/app_localizations.dart';
import 'session_store.dart';

/// 2026-10-26 (живой аудит, П4): тексты ошибок сети — на языке приложения. У ApiClient нет контекста, поэтому язык берётся
/// из [l] (его выставляет MaterialApp.builder в app.dart); пока он не задан (тесты, самый ранний запуск) — по-русски.
class ApiText {
  static AppLocalizations? l;
  static String get noToken => l?.apiNoToken ?? 'Сервер не вернул токен.';
  static String get sessionExpired => l?.apiSessionExpired ?? 'Сессия истекла — войдите снова.';
  static String get timeout => l?.apiTimeout ?? 'Сервер не ответил вовремя.';
  static String get noConnection => l?.apiNoConnection ?? 'Нет связи с сервером.';
  static String serverError(int code) => l?.apiServerError('$code') ?? 'Ошибка сервера $code.';
  static String get networkError => l?.apiNetworkError ?? 'Ошибка сети.';
  // 2026-10-29 (аудит мобилки, мелкое): страница входа Wi-Fi вместо ответа сервера; ответ не того вида
  static String get wifiPortal => l?.apiWifiPortal ?? 'Откройте страницу входа Wi-Fi (магазинная сеть просит войти) и повторите.';
  static String get badResponse => l?.apiBadResponse ?? 'Сервер ответил непонятно. Повторите позже.';
  // 2026-10-29 (аудит мобилки, пп. 1, 6)
  static String get saleAlreadyPosted => l?.apiSaleAlreadyPosted ?? 'Сервер уже провёл этот чек (ключ совпал) — сверьте его в разделе «Чеки».';
  static String get auditStale => l?.apiAuditStale ?? 'Ревизия не проведена вовремя — остатки могли измениться. Пересчитайте заново (акт остался черновиком).';
}

/// Ошибка сервера с понятным текстом для кассира.
class ApiException implements Exception {
  final int? status;
  final String message;
  final dynamic payload;
  ApiException(this.message, {this.status, this.payload});

  /// Сбой связи или сервера: запрос мог дойти — повтор только тем же ключом (как PosCheckoutService.Quick кассы).
  bool get isTransient => status == null || status == 408 || status == 409 || status == 423 || status == 425 || status == 429 || (status! >= 500);

  @override
  String toString() => message;
}

/// 2026-10-29 (аудит мобилки, п. 5): состояние связи для плашки «Нет связи · данные от ЧЧ:ММ». [offline] — последний запрос
/// не дошёл до сервера (обрыв, тайм-аут, страница входа Wi-Fi); любой ответ сервера, кроме 5xx, считается «связь есть».
/// [lastOk] — когда сервер отвечал в последний раз.
class LinkStatus {
  final bool offline;
  final DateTime? lastOk;
  const LinkStatus({this.offline = false, this.lastOk});
}

/// Клиент NurCRM (ТЗ, раздел 1: dio): JWT и его обновление, повтор при 429 с паузой, журнал без токенов.
class ApiClient {
  static const defaultBaseUrl = 'https://app.nurcrm.kg';

  final Dio _dio;
  final SessionStore _store;
  Session? _session;
  Future<bool>? _refreshing;

  /// Сессия истекла и не обновилась — приложение показывает вход.
  final _expired = StreamController<void>.broadcast();
  Stream<void> get onExpired => _expired.stream;

  /// 2026-10-29 (срочно, ETag ленты): условный запрос. Вызывающий ставит [ifNoneMatch] прямо перед `get` — запрос уходит с
  /// If-None-Match, и значение сразу забывается (только для этого запроса). Cloudflare приписывает ETag префикс `W/`, а сервер
  /// с `W/` не сравнивает, поэтому префикс снимается. Ответ 304 — [notModified]; ETag ответа остаётся в [lastEtag].
  String? ifNoneMatch;
  String? lastEtag;
  static const notModified = _NotModified();
  static String stripWeak(String etag) => etag.startsWith('W/') ? etag.substring(2) : etag;

  /// Состояние связи (обновляется каждым запросом); экраны и SyncService слушают его.
  final ValueNotifier<LinkStatus> link = ValueNotifier(const LinkStatus());

  void _linkOk() {
    final cur = link.value;
    final now = DateTime.now();
    // чаще раза в 20 секунд время не обновляем — слушатели не перестраиваются на каждый запрос
    if (cur.offline || cur.lastOk == null || now.difference(cur.lastOk!) > const Duration(seconds: 20)) link.value = LinkStatus(lastOk: now);
  }

  void _linkDown() {
    if (!link.value.offline) link.value = LinkStatus(offline: true, lastOk: link.value.lastOk);
  }

  /// 2026-10-29 (аудит, п. 3): фоновая отправка очереди (WorkManager) могла обновить токены в защищённом хранилище, пока
  /// приложение было закрыто или свёрнуто, — при возврате берём свежие (тот же аккаунт), иначе старый refresh-токен не подойдёт.
  Future<void> reloadTokens() async {
    final cur = _session;
    if (cur == null) return;
    final stored = await _store.load();
    if (stored != null && stored.email == cur.email && (stored.access != cur.access || stored.refresh != cur.refresh)) {
      _session = cur.copyWith(access: stored.access, refresh: stored.refresh);
    }
  }

  ApiClient(this._store, {String baseUrl = defaultBaseUrl})
    : _dio = Dio(
        BaseOptions(
          // Со слешем в конце: иначе dio склеивает «app.nurcrm.kg» + «api/…» в «app.nurcrm.kgapi/…» (найдено на телефоне 13.10).
          baseUrl: baseUrl.endsWith('/') ? baseUrl : '$baseUrl/',
          connectTimeout: const Duration(seconds: 10),
          receiveTimeout: const Duration(seconds: 30),
          contentType: 'application/json',
          responseType: ResponseType.json,
        ),
      ) {
    _dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (o, h) {
          final token = _session?.access;
          if (token != null && token.isNotEmpty && !o.path.contains('auth/login') && !o.path.contains('auth/refresh')) {
            o.headers['Authorization'] = 'Bearer $token';
          }
          if (kDebugMode) debugPrint('API → ${o.method} ${o.path}');
          h.next(o);
        },
      ),
    );
  }

  Session? get session => _session;

  void attach(Session? s) => _session = s;

  // ------------------------------------------------------------------ вход

  /// POST api/users/auth/login/ {email, password} → {access, refresh, …}.
  Future<Map<String, dynamic>> login(String email, String password) async {
    final data = await _send('POST', 'api/users/auth/login/', body: {'email': email.trim(), 'password': password}, auth: false);
    // 2026-10-29 (аудит, мелкое): ответ не JSON-объект — понятная ошибка, а не TypeError без сообщения
    if (data is! Map<String, dynamic>) throw ApiException(ApiText.badResponse);
    final map = data;
    final access = map['access'] as String? ?? '';
    final refresh = map['refresh'] as String? ?? '';
    if (access.isEmpty) throw ApiException(ApiText.noToken);
    _session = Session(email: email.trim(), access: access, refresh: refresh);
    return map;
  }

  Future<bool> _refresh() {
    return _refreshing ??= () async {
      try {
        final s = _session;
        if (s == null || s.refresh.isEmpty) return false;
        final resp = await _dio.post('api/users/auth/refresh/', data: {'refresh': s.refresh});
        final map = resp.data as Map<String, dynamic>;
        final access = map['access'] as String?;
        if (access == null || access.isEmpty) return false;
        _session = s.copyWith(access: access, refresh: map['refresh'] as String? ?? s.refresh);
        await _store.save(_session!);
        return true;
      } on DioException catch (e) {
        // Сбой сервера — не «сессия истекла»: сохранённый вход не стираем (как NurMarketApiClient кассы, 2026-09-29).
        final code = e.response?.statusCode;
        if (code == null || code >= 500 || code == 408 || code == 429) throw ApiException(_describe(e), status: code);
        return false;
      } finally {
        _refreshing = null;
      }
    }();
  }

  // ------------------------------------------------------------------ запросы

  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => _send('GET', path, query: query, timeout: timeout);

  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) =>
      _send('POST', path, body: body, headers: headers, timeout: timeout);

  Future<dynamic> patch(String path, {Object? body}) => _send('PATCH', path, body: body);

  Future<dynamic> delete(String path) => _send('DELETE', path);

  Future<dynamic> _send(
    String method,
    String path, {
    Object? body,
    Map<String, dynamic>? query,
    Map<String, String>? headers,
    Duration? timeout,
    bool auth = true,
  }) async {
    var refreshed = false;
    final cond = ifNoneMatch;
    ifNoneMatch = null;
    final sendHeaders = cond == null || cond.isEmpty ? headers : {...?headers, 'If-None-Match': stripWeak(cond)};
    for (var attempt = 1; ; attempt++) {
      try {
        final resp = await _dio.request(
          path,
          data: body,
          queryParameters: query,
          options: Options(
            method: method,
            headers: sendHeaders,
            receiveTimeout: timeout,
            sendTimeout: timeout,
            validateStatus: cond == null ? null : (c) => c != null && ((c >= 200 && c < 300) || c == 304),
          ),
        );
        if (cond != null) {
          lastEtag = resp.headers.value('etag');
          if (resp.statusCode == 304) {
            _linkOk();
            return notModified;
          }
        }
        // 2026-10-29 (аудит, мелкое): страница входа Wi-Fi (HTML вместо JSON) — запрос до сервера не дошёл: «нет связи» с
        // понятной подсказкой, а не TypeError (`as Map`) в экране. status null — запись в очереди ждёт, повтор тем же ключом.
        final answer = resp.data;
        if (answer is String && _looksLikeHtml(answer, resp.headers.value('content-type'))) {
          _linkDown();
          throw ApiException(ApiText.wifiPortal);
        }
        _linkOk();
        return answer;
      } on DioException catch (e) {
        final code = e.response?.statusCode;
        // 5xx — сервер болен, но связь есть: «нет связи» не пишем; без ответа вовсе — связи нет
        if (code == null) {
          _linkDown();
        } else if (code < 500) {
          _linkOk();
        }
        if (kDebugMode) debugPrint('API ✗ $method $path → ${code ?? e.type.name}: ${e.error ?? e.message}');
        if (auth && code == 401 && !refreshed) {
          refreshed = true;
          if (await _refresh()) continue;
          _expired.add(null);
          throw ApiException(ApiText.sessionExpired, status: 401);
        }
        // 429 — сервер просит подождать: пауза (Retry-After или нарастающая) и повтор, не больше трёх раз.
        if (code == 429 && attempt < 3) {
          final retryAfter = int.tryParse(e.response?.headers.value('retry-after') ?? '');
          await Future.delayed(Duration(milliseconds: retryAfter != null ? retryAfter * 1000 : 600 * attempt + Random().nextInt(300)));
          continue;
        }
        throw ApiException(_describe(e), status: code, payload: e.response?.data);
      }
    }
  }

  static bool _looksLikeHtml(String body, String? contentType) {
    final head = body.trimLeft();
    if (!head.startsWith('<')) return false; // текст чека и т. п. — не страница
    if (contentType != null && contentType.toLowerCase().contains('text/html')) return true;
    final lower = head.substring(0, head.length < 64 ? head.length : 64).toLowerCase();
    return lower.startsWith('<!doctype html') || lower.startsWith('<html');
  }

  static String _describe(DioException e) {
    final data = e.response?.data;
    if (data is Map) {
      for (final key in ['detail', 'error', 'message', 'non_field_errors']) {
        final v = data[key];
        if (v is String && v.isNotEmpty) return v;
        if (v is List && v.isNotEmpty) return v.join(' ');
      }
      final parts = <String>[];
      data.forEach((k, v) => parts.add('$k: ${v is List ? v.join(' ') : v}'));
      if (parts.isNotEmpty) return parts.take(3).join('; ');
    }
    switch (e.type) {
      case DioExceptionType.connectionTimeout:
      case DioExceptionType.receiveTimeout:
      case DioExceptionType.sendTimeout:
        return ApiText.timeout;
      case DioExceptionType.connectionError:
        return ApiText.noConnection;
      default:
        return e.response?.statusCode != null ? ApiText.serverError(e.response!.statusCode!) : (e.message ?? ApiText.networkError);
    }
  }
}

/// Список из ответа: либо массив, либо {results: [...]}.
/// 13.10, владелец: «мобилка не видит все данные» — клиенты, смены, заказы брались только с первой страницы ответа.
/// Все страницы подряд (пока есть next), не больше [maxPages].
Future<List<Map<String, dynamic>>> getAllPages(ApiClient api, String path, {Map<String, dynamic>? query, int pageSize = 200, int maxPages = 30}) async {
  final out = <Map<String, dynamic>>[];
  for (var page = 1; page <= maxPages; page++) {
    final data = await api.get(path, query: {...?query, 'page': page, 'page_size': pageSize});
    final items = asList(data);
    out.addAll(items);
    final hasNext = data is Map ? data['next'] != null : items.length >= pageSize;
    if (items.isEmpty || !hasNext) break;
  }
  return out;
}

List<Map<String, dynamic>> asList(dynamic data) {
  final raw = data is Map ? (data['results'] ?? data['items'] ?? data['data']) : data;
  if (raw is List) return raw.whereType<Map>().map((e) => e.cast<String, dynamic>()).toList();
  return const [];
}

String? str(Map<String, dynamic>? m, String key) {
  final v = m?[key];
  if (v == null) return null;
  if (v is String) return v.isEmpty ? null : v;
  if (v is num || v is bool) return v.toString();
  if (v is Map) return (v['id'] ?? v['name'])?.toString();
  return null;
}

double num0(Map<String, dynamic>? m, String key) {
  final v = m?[key];
  if (v is num) return v.toDouble();
  if (v is String) return double.tryParse(v.replaceAll(',', '.')) ?? 0;
  return 0;
}

/// Ответ 304 на условный запрос (см. [ApiClient.ifNoneMatch]).
class _NotModified {
  const _NotModified();
}
