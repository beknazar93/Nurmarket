import 'dart:io' show Platform;

import 'package:uuid/uuid.dart';

import '../db/database.dart';
import 'server_capabilities.dart';

/// 2026-11-05 (сервер ceba67c, признак `checkout_branch`): поля `device_key`, `device_name`, `branch` в теле pos/checkout/ — сервер помнит,
/// с какого устройства и филиала продан чек. `device_key` — случайный стабильный номер установки (8–64 знака: латиница, цифры, `_`, `-`),
/// без личных данных; выходом из аккаунта не стирается (см. AccountSwitch._keepKeys). Поля шлются ТОЛЬКО при признаке; без него тело как раньше.
class CheckoutDevice {
  CheckoutDevice._();

  static const keySetting = 'device.key';

  /// Филиал сотрудника из профиля (`users/profile/`), если сервер его отдал; пусто — поле `branch` не отправляется.
  static String branch = '';

  static final _valid = RegExp(r'^[A-Za-z0-9_-]{8,64}$');

  static bool isValidKey(String? k) => k != null && _valid.hasMatch(k);

  /// Номер установки: читается из настроек телефона, при первом обращении создаётся (`mob-` + 32 hex-знака).
  static Future<String> key(AppDatabase db) async {
    try {
      final saved = await db.getSetting(keySetting);
      if (isValidKey(saved)) return saved!;
      final id = 'mob-${const Uuid().v4().replaceAll('-', '')}';
      await db.setSetting(keySetting, id);
      return id;
    } catch (_) {
      return 'mob-unknown00'; // база недоступна — поле не должно ломать продажу
    }
  }

  static final _uuid = RegExp(r'^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$');
  static final _digits = RegExp(r'^[0-9]{1,18}$');

  /// Похоже на id филиала (uuid или число), а не на название.
  static bool looksLikeId(String v) => _uuid.hasMatch(v.trim()) || _digits.hasMatch(v.trim());

  /// Филиал из ответа профиля: `branch` / `branch_id` (строка, число или объект с id) у пользователя или в корне.
  static String branchOf(Map<String, dynamic> profile, Map<String, dynamic> user) {
    for (final m in [user, profile]) {
      for (final k in ['branch', 'branch_id']) {
        final v = m[k];
        final id = v is Map ? v['id'] : v;
        // 2026-11-06 (аудит №5, М7-14): сервер ждёт id филиала. Если профиль отдал название («Центральный»), оно не уходит в чек: иначе
        // каждый чек получил бы 400, а офлайн-чеки легли бы в «Не принято».
        if (id != null && looksLikeId('$id')) return '$id';
      }
    }
    return '';
  }

  /// Дополнить тело чека. Без признака `checkout_branch` — тело не меняется.
  static Future<Map<String, dynamic>> apply(Map<String, dynamic> body, AppDatabase db) async {
    if (!ServerCapabilities.has(ServerCapabilities.checkoutBranch)) return body;
    return {
      ...body,
      'device_key': await key(db),
      'device_name': 'NurMarket mobile (${Platform.operatingSystem})',
      if (branch.isNotEmpty) 'branch': branch,
    };
  }
}
