import 'dart:convert';

import 'package:flutter/foundation.dart' show ValueNotifier;

import '../mode/plan_info.dart';
import '../../features/sale/cart.dart' show ServerRounding;
import '../db/database.dart';
import 'api_client.dart';

/// 2026-11-05 (сервер ceba67c): признаки возможностей сервера — `GET api/main/capabilities/` (как ServerCapabilities кассы).
/// Список отдаётся только ПОСЛЕ входа (до входа — 401), поэтому его просят после авторизации и при смене аккаунта. Новое поведение включается
/// только при наличии признака; список не получен (нет связи, 401, 5xx) — признаков «нет», всё идёт как раньше, а сбой НЕ запоминается как
/// «признаков нет»: прошлый список того же аккаунта сохраняется, следующий запрос повторяется. Список переживает перезапуск (кэш в настройках
/// телефона, привязан к аккаунту), чтобы офлайн-старт не менял правила расчёта.
class ServerCapabilities {
  ServerCapabilities._();

  // имена признаков (ответ Умара 05.11.2026)
  static const weightLineTotalFloor = 'weight_line_total_floor';
  static const checkoutBranch = 'checkout_branch';
  static const checkoutLinesMerged = 'checkout_lines_merged';
  static const checkoutBelowCostWarning = 'checkout_below_cost_warning';
  static const receiptVersionRequired = 'receipt_version_required';
  static const rentalIdempotency = 'rental_idempotency';

  static const _cacheKey = 'server.features';
  static const _cacheTtl = Duration(days: 14);

  static Set<String>? _features;
  static String? _owner;

  /// 2026-11-20 (тариф «Мини», E1): план компании из того же ответа `capabilities` (объект `plan`). Нет плана / не получен — [PlanInfo.none]
  /// («как сегодня»). Кэш живёт отдельно (PlanStore, без срока годности): офлайн действует последний известный план.
  static final ValueNotifier<PlanInfo> planNotifier = ValueNotifier(PlanInfo.none);
  static PlanInfo get plan => planNotifier.value;

  /// Копия списка признаков (для ModeProfile).
  static Set<String> get featureSet => {...?_features};

  /// Когда последний раз спрашивали сервер (для «обновить при открытии не чаще раза в N часов»).
  static DateTime? _lastRefresh;

  /// Обновить признаки и план, если прошло больше [maxAge] с прошлого успешного запроса (возврат в приложение, открытие «Ещё»).
  static Future<bool> refreshIfStale(ApiClient api, AppDatabase db, String owner, {Duration maxAge = const Duration(hours: 6)}) async {
    final at = _lastRefresh;
    if (at != null && DateTime.now().difference(at) < maxAge && _owner == owner) return true;
    return refresh(api, db, owner);
  }

  /// 2026-11-06 (аудит №5, М7-08): шаг весов компании получен. false — признак округления есть, а `users/company/` не ответил: шаг «копейка»
  /// взят по умолчанию и НЕ закрепляется на 14 дней (кэш помечается, запрос повторяется на следующем проходе очереди).
  static bool _stepKnown = true;

  /// Нужно повторить запрос (признаки есть, шаг весов не получен).
  static bool get stepUnknown => _features != null && !_stepKnown;

  /// Признак есть у ЭТОГО аккаунта (список уже получен). Нет списка — false.
  static bool has(String name) => _features?.contains(name) ?? false;

  /// Список признаков получен (с сервера или из кэша этого аккаунта).
  static bool get known => _features != null;

  /// Разбор ответа: массив строк, объект «имя → true» или вложенный `features` / `capabilities` (так же разбирает касса).
  static Set<String> parse(dynamic d) {
    final out = <String>{};
    void walk(dynamic v, int depth) {
      if (v is List) {
        for (final x in v) {
          if (x is String && x.isNotEmpty) out.add(x);
          if (x is Map && depth < 2) walk(x, depth + 1);
        }
      } else if (v is Map) {
        for (final e in v.entries) {
          final k = '${e.key}';
          if ((k == 'features' || k == 'capabilities') && (e.value is Map || e.value is List) && depth < 2) {
            walk(e.value, depth + 1);
          } else if (e.value == true || '${e.value}'.toLowerCase() == 'true') {
            out.add(k);
          }
        }
      }
    }

    walk(d, 0);
    return out;
  }

  /// Шаг весов в копейках по `scale_barcode_amount_unit` компании: tiyin = 0,01 → 1, som = 1 → 100. Непонятное значение — tiyin.
  static int stepCentsOf(Object? unit) => '${unit ?? ''}'.trim().toLowerCase() == 'som' ? 100 : 1;

  static void _apply({int? stepCents, bool stepKnown = true}) {
    _stepKnown = stepKnown;
    ServerRounding.weightLineFloor = has(weightLineTotalFloor);
    if (stepCents != null) ServerRounding.weightStepCents = stepCents;
  }

  /// Выход / смена аккаунта: признаки прошлого аккаунта не действуют.
  static Future<void> reset(AppDatabase? db) async {
    _features = null;
    _owner = null;
    _stepKnown = true;
    _lastRefresh = null;
    planNotifier.value = PlanInfo.none;
    ServerRounding.reset();
    try {
      await db?.setSetting(PlanStore.key, '');
      await db?.setSetting(_cacheKey, '');
    } catch (_) {}
  }

  /// Прочитать последний известный список для аккаунта [owner] (вызывать до первого расчёта чека).
  static Future<void> loadCached(AppDatabase db, String owner) async {
    planNotifier.value = await PlanStore.load(db, owner); // 2026-11-20: план — без срока годности, отдельно от списка признаков
    try {
      final raw = await db.getSetting(_cacheKey);
      if (raw == null || raw.isEmpty) return _forgetIfOther(owner);
      final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
      final at = DateTime.tryParse('${m['at']}');
      if ((m['owner'] ?? '') != owner || at == null || DateTime.now().difference(at) > _cacheTtl || m['features'] is! List) return _forgetIfOther(owner);
      _features = {for (final x in m['features'] as List) '$x'};
      _owner = owner;
      _apply(stepCents: (m['step'] as num?)?.toInt() ?? 1, stepKnown: m['stepKnown'] != false);
    } catch (_) {
      _forgetIfOther(owner); // испорченный кэш — как будто признаков нет
    }
  }

  static void _forgetIfOther(String owner) {
    if (_owner != owner) {
      _features = null;
      ServerRounding.reset();
    }
  }

  /// Спросить сервер. true — список получен; false — не удалось (прошлый список того же аккаунта остаётся, ничего не запоминается).
  static Future<bool> refresh(ApiClient api, AppDatabase db, String owner) async {
    try {
      final data = await api.get('api/main/capabilities/', timeout: const Duration(seconds: 8));
      final set = parse(data);
      if (set.isEmpty && data is! Map && data is! List) return false; // ответ не того вида — это сбой, а не «признаков нет»
      final prevStepKnown = _owner == owner && _features != null && _stepKnown; // шаг того же аккаунта был известен
      if (_owner != owner) ServerRounding.reset(); // список другого аккаунта не должен влиять
      _features = set;
      _owner = owner;
      _lastRefresh = DateTime.now();
      // 2026-11-20 (тариф «Мини»): план из того же ответа; нет объекта plan (старый сервер, plan: null) — «как сегодня»
      final plan = PlanInfo.parse(data);
      planNotifier.value = plan;
      await PlanStore.save(db, owner, plan);
      var step = ServerRounding.weightStepCents;
      var stepKnown = true;
      if (set.contains(weightLineTotalFloor)) {
        // шаг весов — настройка компании
        try {
          final company = await api.get('api/users/company/', timeout: const Duration(seconds: 8));
          if (company is Map) {
            step = stepCentsOf(company['scale_barcode_amount_unit']);
          } else {
            stepKnown = prevStepKnown;
          }
        } catch (_) {
          stepKnown = prevStepKnown; // не получили: прежний шаг того же аккаунта (если был известен), иначе «копейка» без закрепления
        }
        if (!stepKnown) step = 1;
      }
      _apply(stepCents: step, stepKnown: stepKnown);
      try {
        await db.setSetting(_cacheKey, jsonEncode({'owner': owner, 'at': DateTime.now().toIso8601String(), 'features': set.toList()..sort(), 'step': step, if (!stepKnown) 'stepKnown': false}));
      } catch (_) {
        // кэш — удобство
      }
      return stepKnown;
    } catch (_) {
      return false; // нет связи, 401, 5xx: прошлый список остаётся, повтор при следующем входе/запуске
    }
  }

  /// Только для тестов.
  static void debugSet(Iterable<String>? features, {String owner = 'test', int stepCents = 1, PlanInfo? plan}) {
    planNotifier.value = plan ?? PlanInfo.none;
    _lastRefresh = null;
    _features = features?.toSet();
    _owner = features == null ? null : owner;
    ServerRounding.reset();
    if (features != null) _apply(stepCents: stepCents);
  }
}
