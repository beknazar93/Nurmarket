import 'dart:async';

/// 2026-11-20 (тариф «Мини»): события лимита тарифа без UI-зависимостей (их шлёт ApiClient, показывает app.dart).

/// Номер поддержки NurCRM (тот же, что в кассе: RemoteSupportHost.SupportPhone; WhatsApp — тот же номер).
const supportWhatsApp = '+996 221 068 796';

class PlanLimitEvent {
  /// plan_limit | plan_limit_exceeded | plan_client_not_allowed | plan_feature_required
  final String code;
  final String limit;
  final int? max;
  final int? current;
  final String upgradePlan;

  /// Текст сервера (`detail`) — показывается как есть.
  final String detail;
  const PlanLimitEvent({required this.code, this.limit = '', this.max, this.current, this.upgradePlan = '', this.detail = ''});

  static const codes = {'plan_limit', 'plan_limit_exceeded', 'plan_client_not_allowed', 'plan_feature_required'};

  /// Ответ сервера → событие; null — это не лимит тарифа.
  static PlanLimitEvent? parse(int? status, dynamic payload) {
    if (status != 403 && status != 409 && status != 402) return null;
    if (payload is! Map) return null;
    var c = '${payload['code'] ?? ''}'.trim().toLowerCase();
    if (c.isEmpty && payload['error'] is Map) c = '${(payload['error'] as Map)['code'] ?? ''}'.trim().toLowerCase();
    if (!codes.contains(c)) return null;
    int? n(Object? v) => v is num ? v.toInt() : int.tryParse('${v ?? ''}');
    final detail = payload['detail'];
    return PlanLimitEvent(
      code: c,
      limit: '${payload['limit'] ?? payload['feature'] ?? ''}',
      max: n(payload['max']),
      current: n(payload['current']),
      upgradePlan: '${payload['upgrade_plan'] ?? ''}'.trim().toLowerCase(),
      detail: detail is String ? detail : '',
    );
  }
}

/// Шина событий лимита: ApiClient сообщает, приложение (app.dart) показывает окно.
class PlanLimitEvents {
  static final _c = StreamController<PlanLimitEvent>.broadcast();
  static Stream<PlanLimitEvent> get stream => _c.stream;
  static void report(int? status, dynamic payload) {
    final e = PlanLimitEvent.parse(status, payload);
    if (e != null) _c.add(e);
  }
}
