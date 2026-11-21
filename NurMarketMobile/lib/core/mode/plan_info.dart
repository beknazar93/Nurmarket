import 'dart:convert';

import '../db/database.dart';

/// 2026-11-20 (тариф «Мини», этап E1): тариф компании из `GET api/main/capabilities/` → объект `plan`
/// `{code, names{ru,ky,…}, limits{max_staff, max_devices, max_cashboxes}, allowed_clients[], upgrade_plan}` (формат Умара, 20.11).
/// Принцип: нет объекта `plan` (старый сервер, нет тарифа — `plan: null`, нет связи) — режим «как сегодня»; незнакомый код плана
/// ничего не открывает и ничего не режет. Продажу тариф не блокирует никогда.
class PlanLimits {
  /// null — без лимита (так сервер отдаёт у тарифов без ограничения).
  final int? maxStaff;
  final int? maxDevices;
  final int? maxCashboxes;
  const PlanLimits({this.maxStaff, this.maxDevices, this.maxCashboxes});

  static int? _n(Object? v) => v is num ? v.toInt() : int.tryParse('${v ?? ''}');

  static PlanLimits parse(Object? raw) {
    if (raw is! Map) return const PlanLimits();
    return PlanLimits(maxStaff: _n(raw['max_staff']), maxDevices: _n(raw['max_devices']), maxCashboxes: _n(raw['max_cashboxes']));
  }

  Map<String, dynamic> toJson() => {'max_staff': maxStaff, 'max_devices': maxDevices, 'max_cashboxes': maxCashboxes};
}

class PlanInfo {
  /// Код плана в нижнем регистре: mini / start / standard / custom или любой другой (тогда [isKnown] = false). '' — плана нет.
  final String code;

  /// Названия плана по языкам (`names` сервера): {ru: 'Мини', …}.
  final Map<String, String> names;
  final PlanLimits limits;

  /// С каких клиентов вход разрешён: `mobile`, `pos`, `web`, `owner`… Пусто — не указано.
  final Set<String> allowedClients;

  /// Код тарифа для апгрейда (`upgrade_plan`: «start»).
  final String upgradePlan;

  /// Список признаков внутри плана (если сервер его кладёт в `plan.features`).
  final Set<String> features;

  /// Объект `plan` в ответе был и не пустой.
  final bool present;

  const PlanInfo({
    this.code = '',
    this.names = const {},
    this.limits = const PlanLimits(),
    this.allowedClients = const {},
    this.upgradePlan = '',
    this.features = const {},
    this.present = false,
  });

  /// Плана нет — «как сегодня».
  static const none = PlanInfo();

  static const knownCodes = {'mini', 'start', 'standard', 'custom'};

  bool get isKnown => knownCodes.contains(code);
  bool get isMini => code == 'mini';

  /// Название плана на языке [lang]; нет — русское, нет — код.
  String nameFor(String lang) => names[lang] ?? names['ru'] ?? (code.isEmpty ? '' : code);

  /// Разбор ответа `capabilities`: `plan` лежит в корне или внутри `capabilities`. Строка вместо объекта — это код плана.
  static PlanInfo parse(dynamic data) {
    Object? raw;
    if (data is Map) {
      raw = data['plan'];
      if (raw == null && data['capabilities'] is Map) raw = (data['capabilities'] as Map)['plan'];
    }
    if (raw is String && raw.trim().isNotEmpty) return PlanInfo(code: raw.trim().toLowerCase(), present: true);
    if (raw is! Map) return none;
    final code = '${raw['code'] ?? raw['plan_code'] ?? ''}'.trim().toLowerCase();
    if (code.isEmpty && raw.isEmpty) return none;
    Set<String> strs(Object? v) => v is List
        ? {
            for (final x in v)
              if (x is String && x.isNotEmpty) x.trim().toLowerCase(),
          }
        : <String>{};
    final names = <String, String>{};
    final n = raw['names'] ?? raw['name'];
    if (n is Map) {
      for (final e in n.entries) {
        if (e.value is String && (e.value as String).isNotEmpty) names['${e.key}'] = e.value as String;
      }
    } else if (n is String && n.isNotEmpty) {
      names['ru'] = n;
    }
    return PlanInfo(
      code: code,
      names: names,
      limits: PlanLimits.parse(raw['limits']),
      allowedClients: strs(raw['allowed_clients']),
      upgradePlan: '${raw['upgrade_plan'] ?? ''}'.trim().toLowerCase(),
      features: strs(raw['features']),
      present: true,
    );
  }

  Map<String, dynamic> toJson() => {
    'code': code,
    'names': names,
    'limits': limits.toJson(),
    'allowed_clients': allowedClients.toList()..sort(),
    'upgrade_plan': upgradePlan,
    'features': features.toList()..sort(),
  };

  @override
  bool operator ==(Object other) => other is PlanInfo && jsonEncode(other.toJson()) == jsonEncode(toJson()) && other.present == present;

  @override
  int get hashCode => Object.hash(jsonEncode(toJson()), present);
}

/// Кэш плана в настройках телефона (не в схеме базы), привязан к аккаунту. Срока годности нет: офлайн действует последний известный план.
class PlanStore {
  static const key = 'plan.info';

  static Future<PlanInfo> load(AppDatabase db, String owner) async {
    try {
      final raw = await db.getSetting(key);
      if (raw == null || raw.isEmpty) return PlanInfo.none;
      final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
      if (m['owner'] != owner || m['plan'] is! Map) return PlanInfo.none;
      return PlanInfo.parse({'plan': m['plan']});
    } catch (_) {
      return PlanInfo.none; // испорченный кэш — как будто плана нет
    }
  }

  static Future<void> save(AppDatabase db, String owner, PlanInfo plan) async {
    try {
      await db.setSetting(key, plan.present ? jsonEncode({'owner': owner, 'at': DateTime.now().toIso8601String(), 'plan': plan.toJson()}) : '');
    } catch (_) {
      // кэш — удобство
    }
  }
}
