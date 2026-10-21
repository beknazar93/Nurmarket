import 'dart:async';
import 'dart:math';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import 'session_store.dart';

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
    final map = data as Map<String, dynamic>;
    final access = map['access'] as String? ?? '';
    final refresh = map['refresh'] as String? ?? '';
    if (access.isEmpty) throw ApiException('Сервер не вернул токен.');
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
    for (var attempt = 1; ; attempt++) {
      try {
        final resp = await _dio.request(
          path,
          data: body,
          queryParameters: query,
          options: Options(method: method, headers: headers, receiveTimeout: timeout, sendTimeout: timeout),
        );
        return resp.data;
      } on DioException catch (e) {
        final code = e.response?.statusCode;
        if (kDebugMode) debugPrint('API ✗ $method $path → ${code ?? e.type.name}: ${e.error ?? e.message}');
        if (auth && code == 401 && !refreshed) {
          refreshed = true;
          if (await _refresh()) continue;
          _expired.add(null);
          throw ApiException('Сессия истекла — войдите снова.', status: 401);
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
        return 'Сервер не ответил вовремя.';
      case DioExceptionType.connectionError:
        return 'Нет связи с сервером.';
      default:
        return e.response?.statusCode != null ? 'Ошибка сервера ${e.response!.statusCode}.' : (e.message ?? 'Ошибка сети.');
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
