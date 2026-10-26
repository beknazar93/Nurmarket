// 2026-10-26 (владелец): «и в мобилке предупреждение об ограничении использования ИИ: максимум 2 сотрудника имеют права
// использовать ИИ». Причина: бесплатный лимит Gemini общий на весь проект (запросы Flash-Lite — 15 в минуту и 500 в день,
// Live — 65 тыс. токенов в минуту), его хватает не больше чем двум сотрудникам магазина.
//
// Кому разрешён ИИ — список до [AiAccess.limit] сотрудников, его задаёт владелец (или администратор с правом «Настройки»).
// Хранится в общих настройках компании NurCRM — api/main/app-settings/nurmarket-mobile/, ключ AiAccess (рядом с DebtAccess,
// PaymentQrs, quotes): их читает любой телефон компании, писать могут владелец и администратор. Копия — на телефоне (ai.access),
// чтобы правило действовало и без связи. Нового серверного метода и изменения схемы базы не нужно.
//
// ВАЖНО: проверка только на стороне приложения. Сервер список не навязывает (ключи Gemini лежат в тех же настройках, и любой
// телефон с ключом технически может обратиться к Google) — это защита от случайного расхода общего лимита, не от злого умысла.
//
// Одна точка входа: VisionService.key() / fallbackKeys() / hasChatKey() (vision.dart) зовут [AiAccessRepository] и без права
// бросают AiAccessDenied (vision.dart; наследует VisionException). Через них идут чат Советника, звонок (LiveCall получает ключ там же), умная камера, накладная по
// фото, весы по виду и описание товара.
import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/app_localizations.dart';
import '../api/api_client.dart';
import '../api/session_store.dart';
import '../db/database.dart';
import '../providers.dart';

/// Сотрудник в списке ИИ: id пользователя NurCRM и имя (имя хранится, чтобы сообщение «доступен только …» работало и у
/// кассира, которому список сотрудников недоступен).
class AiAccessUser {
  final String id;
  final String name;
  const AiAccessUser(this.id, this.name);

  bool sameId(String other) => id.isNotEmpty && other.isNotEmpty && id.toLowerCase() == other.toLowerCase();

  Map<String, dynamic> toJson() => {'id': id, 'name': name};

  @override
  bool operator ==(Object other) => other is AiAccessUser && other.id.toLowerCase() == id.toLowerCase();
  @override
  int get hashCode => id.toLowerCase().hashCode;
}

/// Правило: [users] == null — список не задан (старые магазины: ИИ у всех), иначе — только эти люди (пустой список = ИИ выключен).
class AiAccessPolicy {
  final List<AiAccessUser>? users;
  final String updatedBy;
  final String updatedAt;
  const AiAccessPolicy._(this.users, this.updatedBy, this.updatedAt);

  const AiAccessPolicy.unset() : this._(null, '', '');

  AiAccessPolicy.set(List<AiAccessUser> users, {String by = '', String at = ''})
    : this._(List.unmodifiable(_clean(users)), by, at);

  bool get isSet => users != null;

  /// Не больше [AiAccess.limit], без повторов и пустых id.
  static List<AiAccessUser> _clean(Iterable<AiAccessUser> src) {
    final out = <AiAccessUser>[];
    for (final u in src) {
      if (u.id.trim().isEmpty || out.contains(u)) continue;
      out.add(u);
      if (out.length >= AiAccess.limit) break;
    }
    return out;
  }

  bool contains(String userId) => users?.any((u) => u.sameId(userId)) ?? false;

  Map<String, dynamic> toJson() => {
    'users': [for (final u in users ?? const <AiAccessUser>[]) u.toJson()],
    'limit': AiAccess.limit,
    if (updatedBy.isNotEmpty) 'by': updatedBy,
    if (updatedAt.isNotEmpty) 'updatedAt': updatedAt,
  };

  /// Любой мусор в настройках — «список не задан» (ИИ как раньше), а не «ИИ выключен».
  static AiAccessPolicy fromJson(Object? raw) {
    try {
      var v = raw;
      if (v is String && v.trim().isNotEmpty) v = jsonDecode(v);
      if (v is! Map || v['users'] is! List) return const AiAccessPolicy.unset();
      final users = <AiAccessUser>[];
      for (final e in v['users'] as List) {
        if (e is Map) {
          final id = '${e['id'] ?? ''}'.trim();
          if (id.isNotEmpty) users.add(AiAccessUser(id, '${e['name'] ?? ''}'.trim()));
        } else if (e is String && e.trim().isNotEmpty) {
          users.add(AiAccessUser(e.trim(), ''));
        }
      }
      return AiAccessPolicy._(List.unmodifiable(_clean(users)), '${v['by'] ?? ''}', '${v['updatedAt'] ?? ''}');
    } catch (_) {
      return const AiAccessPolicy.unset();
    }
  }
}

/// Итог выбора в экране настройки.
enum AiPick { added, removed, full }

/// Выбор сотрудников в экране «Доступ к ИИ»: не больше [AiAccess.limit]; третьего выбрать нельзя — возвращается [AiPick.full],
/// чтобы экран объяснил причину (а не молча проигнорировал нажатие).
class AiPicker {
  final List<AiAccessUser> chosen;
  AiPicker([Iterable<AiAccessUser> initial = const []]) : chosen = [...AiAccessPolicy.set(initial.toList()).users!];

  bool has(AiAccessUser u) => chosen.contains(u);
  bool get isFull => chosen.length >= AiAccess.limit;

  AiPick toggle(AiAccessUser u) {
    if (chosen.remove(u)) return AiPick.removed;
    if (isFull) return AiPick.full;
    chosen.add(u);
    return AiPick.added;
  }
}

/// Решение «может ли этот сотрудник пользоваться ИИ».
class AiAccess {
  /// Сколько сотрудников магазина могут пользоваться ИИ (общий бесплатный лимит Gemini на весь проект).
  static const limit = 2;

  /// Владелец и администратор с правом «Настройки» (как DebtAccess) — задают список.
  static bool canManage(Session? s) => s != null && (s.isOwner || s.can(Session.viewSettings));

  /// Главная проверка. Список не задан — можно всем (старые магазины); задан — только тем, кто в нём.
  static bool canUse(Session? s, AiAccessPolicy policy) {
    if (!policy.isSet) return true;
    if (s == null) return false;
    return policy.contains(s.userId);
  }

  /// Владельцу, пока список не задан, показываем плашку «Рекомендуем ограничить».
  static bool shouldRecommend(Session? s, AiAccessPolicy policy) => !policy.isSet && canManage(s);

  /// Список по умолчанию: сам владелец — первым из двух.
  static List<AiAccessUser> defaultUsers(Session? s) =>
      s != null && s.isOwner && s.userId.isNotEmpty ? [AiAccessUser(s.userId, s.userName)] : const [];

  /// Строка статуса «у вас есть доступ / нет доступа / лимит не задан» — для профиля и настроек.
  static String statusText(AppLocalizations l, Session? s, AiAccessPolicy policy) =>
      !policy.isSet ? l.aiAccMineOpen : (canUse(s, policy) ? l.aiAccMineYes : l.aiAccMineNo);

  /// «ИИ доступен только 2 сотрудникам магазина: A, B. Обратитесь к владельцу» — на языке интерфейса ([l]; null — по-русски).
  static String deniedText(AppLocalizations? l, AiAccessPolicy policy) {
    final names = [for (final u in policy.users ?? const <AiAccessUser>[]) u.name.trim().isEmpty ? '—' : u.name.trim()].join(', ');
    if (names.isEmpty) {
      return l?.aiAccDeniedNone ?? 'ИИ в магазине сейчас выключен для всех сотрудников. Обратитесь к владельцу.';
    }
    return l?.aiAccDenied(limit, names) ?? 'ИИ доступен только $limit сотрудникам магазина: $names. Обратитесь к владельцу.';
  }
}

class AiAccessRepository {
  final ApiClient api;
  final AppDatabase db;
  AiAccessRepository(this.api, this.db);

  static const path = 'api/main/app-settings/nurmarket-mobile/';
  static const key = 'AiAccess';
  static const _local = 'ai.access';

  // Запомненное правило — на всё приложение (key() зовут часто). Нет связи — копия телефона и повтор через минуту.
  static AiAccessPolicy? _cached;
  static String? _cachedFor;
  static DateTime _cachedAt = DateTime(2000);
  static Duration _ttl = const Duration(minutes: 5);

  static void resetForTest() {
    _cached = null;
    _cachedFor = null;
    _cachedAt = DateTime(2000);
    _ttl = const Duration(minutes: 5);
  }

  String get _who => '${api.session?.userId ?? ''}|${api.session?.companyInn ?? ''}|${api.session?.companyName ?? ''}';

  /// Сначала сервер (общий список магазина), без связи — копия на телефоне, нет и её — «не задано».
  Future<AiAccessPolicy> load({bool force = false}) async {
    if (!force && _cached != null && _cachedFor == _who && DateTime.now().difference(_cachedAt) < _ttl) return _cached!;
    AiAccessPolicy p;
    var ttl = const Duration(minutes: 5);
    try {
      final data = await api.get(path, timeout: const Duration(seconds: 4)).timeout(const Duration(seconds: 5));
      if (data is! Map) throw const FormatException('not a map');
      final settings = data['settings'];
      p = AiAccessPolicy.fromJson(settings is Map ? settings[key] : null);
      await _saveLocal(p);
    } catch (_) {
      ttl = const Duration(minutes: 1);
      p = await _readLocal();
    }
    _cached = p;
    _cachedFor = _who;
    _cachedAt = DateTime.now();
    _ttl = ttl;
    return p;
  }

  Future<void> save(AiAccessPolicy p) async {
    await api.patch(path, body: {key: p.toJson()});
    await _saveLocal(p);
    _cached = p;
    _cachedFor = _who;
    _cachedAt = DateTime.now();
    _ttl = const Duration(minutes: 5);
  }

  Future<void> _saveLocal(AiAccessPolicy p) async {
    try {
      await db.setSetting(_local, p.isSet ? jsonEncode(p.toJson()) : '');
    } catch (_) {
      // копия не записалась — правило всё равно в памяти
    }
  }

  Future<AiAccessPolicy> _readLocal() async {
    try {
      return AiAccessPolicy.fromJson(await db.getSetting(_local));
    } catch (_) {
      return const AiAccessPolicy.unset();
    }
  }
}

final aiAccessRepoProvider = Provider((ref) => AiAccessRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));

/// Правило для экранов (плашки, профиль). После сохранения — ref.invalidate(aiPolicyProvider).
final aiPolicyProvider = FutureProvider.autoDispose<AiAccessPolicy>((ref) => ref.read(aiAccessRepoProvider).load());
