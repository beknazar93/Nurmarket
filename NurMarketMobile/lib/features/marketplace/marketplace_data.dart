import 'dart:convert';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';

/// 2026-11-27 (Маркетплейс мобилки): данные сервера о модулях и тарифах — те же ответы и те же вызовы, что у кассы (CompanyModules, PublicModules, TariffApi):
/// GET public/modules/ (цены модулей), GET users/company/modules/ (что включено), PUT users/company/modules/ {enable|disable} с If-Match,
/// DELETE users/company/tariff/modules/{id}/, POST users/company/features/trial/, GET public/tariffs/, POST users/company/tariff/quote/ и …/custom/.
/// Цены только серверные: ответа нет — «Цена уточняется», своих цен здесь нет (решение 21.11.2026).

String? _s(Object? v) {
  final t = v is String ? v : (v is num ? '$v' : null);
  return t == null || t.trim().isEmpty ? null : t.trim();
}

double? _n(Object? v) => v is num ? v.toDouble() : double.tryParse((_s(v) ?? '').replaceAll(',', '.'));

List<String> _strs(Object? v) => v is List
    ? [
        for (final x in v)
          if (x is String && x.trim().isNotEmpty) x.trim(),
      ]
    : <String>[];

/// Название вида {"ru":…, "en":…} или строка → карта по языкам.
Map<String, String> _names(Object? v) {
  if (v is String && v.trim().isNotEmpty) return {'ru': v.trim()};
  if (v is! Map) return {};
  return {
    for (final e in v.entries)
      if (e.value is String && (e.value as String).trim().isNotEmpty) '${e.key}': (e.value as String).trim(),
  };
}

/// Единое имя профиля сервера: hardware → stroymag, bazaar_retail / bazaar_wholesale → bazaar (как CompanyModules.CanonProfile кассы).
String canonProfile(String? id) {
  final v = (id ?? '').trim().toLowerCase();
  return switch (v) {
    'hardware' => 'stroymag',
    'bazaar_retail' || 'bazaar_wholesale' => 'bazaar',
    _ => v,
  };
}

/// Модуль из GET public/modules/: цена в месяц, группа (сфера), профили, где он входит бесплатно, пробные дни.
class MpServerModule {
  final String id;
  final Map<String, String> names;
  final String group;
  final double? monthly;
  final int trialDays;
  final List<String> defaultIn;
  final String? description;
  const MpServerModule(this.id, this.names, this.group, this.monthly, this.trialDays, this.defaultIn, {this.description});

  static List<MpServerModule> parseList(Object? root) {
    if (root is! Map || root['modules'] is! List) return const [];
    return [
      for (final e in root['modules'] as List)
        if (e is Map && _s(e['id']) != null)
          MpServerModule(
            _s(e['id'])!,
            _names(e['name'] ?? e['names']),
            _s(e['group']) ?? '',
            _n(e['monthly']),
            (_n(e['trial_days']) ?? 0).toInt(),
            _strs(e['default_in']),
            description: _names(e['description'] ?? e['summary']).values.firstOrNull,
          ),
    ];
  }

  static String currencyOf(Object? root) => root is Map ? (_s(root['currency']) ?? 'KGS') : 'KGS';
}

/// Строка items[] ответа компании: включён ли модуль и откуда (default / added / removed / off / trial).
class MpItem {
  final String id;
  final bool enabled;
  final String source;
  final DateTime? trialUntil;
  const MpItem(this.id, this.enabled, this.source, this.trialUntil);
  bool get isTrial => source == 'trial';
}

/// Разобранный ответ GET/PUT users/company/modules/: профиль, версия (для If-Match), items[], catalog[], profiles[], modules[].
class MpCompany {
  final String profile;
  final String version;
  final Map<String, MpItem> items;
  final Map<String, Map<String, String>> catalogNames;
  final Map<String, List<String>> catalogDefaultIn;
  final Map<String, Map<String, String>> profileNames;
  final Map<String, List<String>> profileModules;
  final Set<String> enabled;
  const MpCompany({
    this.profile = '',
    this.version = '',
    this.items = const {},
    this.catalogNames = const {},
    this.catalogDefaultIn = const {},
    this.profileNames = const {},
    this.profileModules = const {},
    this.enabled = const {},
  });

  /// null — это не ответ модулей (нет ни modules[], ни items[]).
  static MpCompany? parse(Object? root) {
    if (root is! Map) return null;
    var r = root;
    if (r['data'] is Map && r['modules'] == null && r['items'] == null) r = r['data'] as Map;
    final hasModules = r['modules'] is List;
    final hasItems = r['items'] is List;
    if (!hasModules && !hasItems) return null;
    final items = <String, MpItem>{};
    if (hasItems) {
      for (final e in r['items'] as List) {
        if (e is! Map || _s(e['id']) == null) continue;
        final id = _s(e['id'])!;
        items[id] = MpItem(id, e['enabled'] == true, (_s(e['source']) ?? '').toLowerCase(), DateTime.tryParse(_s(e['trial_until']) ?? ''));
      }
    }
    final enabled = hasModules ? _strs(r['modules']).toSet() : {for (final i in items.values) if (i.enabled) i.id};
    final catNames = <String, Map<String, String>>{};
    final catIn = <String, List<String>>{};
    if (r['catalog'] is List) {
      for (final e in r['catalog'] as List) {
        if (e is! Map || _s(e['id']) == null) continue;
        catNames[_s(e['id'])!] = _names(e['names'] ?? e['name']);
        catIn[_s(e['id'])!] = _strs(e['default_in']);
      }
    }
    final profNames = <String, Map<String, String>>{};
    final profMods = <String, List<String>>{};
    if (r['profiles'] is List) {
      for (final e in r['profiles'] as List) {
        if (e is! Map || _s(e['id']) == null) continue;
        profNames[_s(e['id'])!] = _names(e['names'] ?? e['name']);
        profMods[_s(e['id'])!] = _strs(e['modules']);
      }
    }
    return MpCompany(
      profile: _s(r['profile']) ?? '',
      version: _s(r['version']) ?? '',
      items: items,
      catalogNames: catNames,
      catalogDefaultIn: catIn,
      profileNames: profNames,
      profileModules: profMods,
      enabled: enabled,
    );
  }

  bool knows(String id) => items.containsKey(id) || catalogNames.containsKey(id);

  bool isOn(String id) {
    final it = items[id];
    if (it != null && it.source == 'removed') return false;
    return enabled.contains(id) || (it?.enabled ?? false);
  }

  /// Модуль входит в профиль сферы бесплатно (catalog.default_in / profiles[].modules; запасной путь — public/modules default_in).
  bool includedIn(String id, {List<String> publicDefaultIn = const []}) {
    if (profile.isEmpty) return false;
    final p = canonProfile(profile);
    if ((catalogDefaultIn[id] ?? const []).any((d) => canonProfile(d) == p)) return true;
    for (final e in profileModules.entries) {
      if (canonProfile(e.key) == p && e.value.contains(id)) return true;
    }
    return publicDefaultIn.any((d) => canonProfile(d) == p);
  }
}

class MpPlan {
  final String code;
  final Map<String, String> names;
  final double? price;
  final double? yearPrice;
  final int trialDays;
  final String? base;
  const MpPlan(this.code, this.names, this.price, this.yearPrice, this.trialDays, this.base);
}

class MpPack {
  final String code;
  final Map<String, String> names;
  final Map<String, String> description;
  final double activationFee;
  final double monthlyFee;
  final List<String> includedIn;
  const MpPack(this.code, this.names, this.description, this.activationFee, this.monthlyFee, this.includedIn);
}

class MpTariffs {
  final List<MpPlan> plans;
  final List<MpPack> packs;
  const MpTariffs(this.plans, this.packs);
  static const none = MpTariffs([], []);

  static MpTariffs parse(Object? root) {
    if (root is! Map) return none;
    return MpTariffs(
      [
        if (root['plans'] is List)
          for (final e in root['plans'] as List)
            if (e is Map && _s(e['code']) != null)
              MpPlan(_s(e['code'])!, _names(e['name']), _n(e['price']), e['year'] is Map ? _n((e['year'] as Map)['price']) : null, (_n(e['trial_days']) ?? 0).toInt(), _s(e['base'])),
      ],
      [
        if (root['packs'] is List)
          for (final e in root['packs'] as List)
            if (e is Map && _s(e['code']) != null)
              MpPack(_s(e['code'])!, _names(e['name']), _names(e['description']), _n(e['activation_fee']) ?? 0, _n(e['monthly_fee']) ?? 0, _strs(e['included_in'])),
      ],
    );
  }
}

class MpQuoteModule {
  final String id;
  final Map<String, String> names;
  final double monthly;
  const MpQuoteModule(this.id, this.names, this.monthly);
}

/// Расчёт «Своё» (POST users/company/tariff/quote/). Суммы — как прислал сервер, сами не считаем.
class MpQuote {
  final double? base;
  final double? totalMonth;
  final double? totalYear;
  final double? yearDiscount;
  final int? yearPaidMonths;
  final double? totalOnce;
  final List<MpQuoteModule> extra;
  final List<MpQuoteModule> included;
  final List<String> removed;
  const MpQuote({this.base, this.totalMonth, this.totalYear, this.yearDiscount, this.yearPaidMonths, this.totalOnce, this.extra = const [], this.included = const [], this.removed = const []});

  static List<MpQuoteModule> _mods(Object? v) => [
    if (v is List)
      for (final e in v)
        if (e is Map && _s(e['id']) != null) MpQuoteModule(_s(e['id'])!, _names(e['names'] ?? e['name']), _n(e['monthly']) ?? 0),
  ];

  static MpQuote parse(Object? root) {
    if (root is! Map) return const MpQuote();
    return MpQuote(
      base: _n(root['base']) ?? _n(root['base_price']),
      totalMonth: _n(root['total_month']) ?? _n(root['monthly']),
      totalYear: _n(root['total_year']),
      yearDiscount: _n(root['year_discount']),
      yearPaidMonths: _n(root['year_paid_months'])?.toInt(),
      totalOnce: _n(root['total_once']) ?? _n(root['once']),
      extra: _mods(root['extra']),
      included: _mods(root['included']),
      removed: _strs(root['removed']),
    );
  }
}

/// Итог заявки «Своё».
class MpOrder {
  final String id;
  final String status;
  final String? payUrl;
  const MpOrder(this.id, this.status, this.payUrl);
}

/// Итог действия с модулем (включить / выключить / проба): коды как у ModuleChangeStatus кассы.
enum MpChange { ok, forbidden, noConnection, unknownModule, conflict, trialUsed, alreadyActive, unsupported, failed }

class MpChangeResult {
  final MpChange status;
  final String? detail;
  final MpCompany? company;
  const MpChangeResult(this.status, {this.detail, this.company});
  bool get ok => status == MpChange.ok;
}

/// Всё, что экран знает о сервере: ответы (или кэш), признак «без связи».
class MpSnapshot {
  final List<MpServerModule> modules;
  final String currency;
  final MpCompany? company;
  final MpTariffs tariffs;
  final bool offline;
  final DateTime? at;
  const MpSnapshot({this.modules = const [], this.currency = 'KGS', this.company, this.tariffs = MpTariffs.none, this.offline = false, this.at});
}

/// Работа с сервером и кэш последнего ответа (в настройках телефона, по аккаунту).
class MarketplaceRepository {
  final ApiClient _api;
  final AppDatabase? _db;
  final String owner;
  MarketplaceRepository(this._api, this._db, this.owner);

  static const cacheKey = 'marketplace.cache';
  static const _timeout = Duration(seconds: 20);

  /// Загрузить всё. Каждая часть независима: что не пришло — берётся из кэша того же аккаунта; не пришло ничего — полностью кэш и offline = true.
  Future<MpSnapshot> load() async {
    final cached = await _readCache();
    Object? rawModules, rawCompany, rawTariffs;
    var fails = 0;
    Future<Object?> tryGet(String path) async {
      try {
        return await _api.get(path, timeout: _timeout);
      } catch (_) {
        fails++;
        return null;
      }
    }

    final res = await Future.wait([tryGet('api/public/modules/'), tryGet('api/users/company/modules/'), tryGet('api/public/tariffs/')]);
    rawModules = res[0];
    rawCompany = res[1];
    rawTariffs = res[2];
    final modules = MpServerModule.parseList(rawModules);
    final company = MpCompany.parse(rawCompany);
    final tariffs = MpTariffs.parse(rawTariffs);
    final gotModules = modules.isNotEmpty;
    final gotCompany = company != null;
    final gotTariffs = tariffs.plans.isNotEmpty || tariffs.packs.isNotEmpty;
    if (gotModules || gotCompany || gotTariffs) {
      // что-то пришло: свежее — в кэш, недостающее — из кэша
      final merged = <String, Object?>{
        'modules': gotModules ? rawModules : cached?['modules'],
        'company': gotCompany ? rawCompany : cached?['company'],
        'tariffs': gotTariffs ? rawTariffs : cached?['tariffs'],
      };
      await _writeCache(merged);
      final m = gotModules ? modules : MpServerModule.parseList(cached?['modules']);
      return MpSnapshot(
        modules: m,
        currency: MpServerModule.currencyOf(gotModules ? rawModules : cached?['modules']),
        company: gotCompany ? company : MpCompany.parse(cached?['company']),
        tariffs: gotTariffs ? tariffs : MpTariffs.parse(cached?['tariffs']),
        offline: fails > 0 && !(gotModules && gotCompany && gotTariffs),
        at: DateTime.now(),
      );
    }
    if (cached != null) {
      return MpSnapshot(
        modules: MpServerModule.parseList(cached['modules']),
        currency: MpServerModule.currencyOf(cached['modules']),
        company: MpCompany.parse(cached['company']),
        tariffs: MpTariffs.parse(cached['tariffs']),
        offline: true,
        at: DateTime.tryParse('${cached['at'] ?? ''}'),
      );
    }
    return const MpSnapshot(offline: true);
  }

  /// 2026-11-27 (доступы советника): последний сохранённый ответ без обращения к серверу (офлайн); null — кэша нет.
  /// Нужен, чтобы советник в чате не ждал медленную сеть: инструмент отвечает за 5 с и берёт сохранённое.
  Future<MpSnapshot?> cachedSnapshot() async {
    final cached = await _readCache();
    if (cached == null) return null;
    return MpSnapshot(
      modules: MpServerModule.parseList(cached['modules']),
      currency: MpServerModule.currencyOf(cached['modules']),
      company: MpCompany.parse(cached['company']),
      tariffs: MpTariffs.parse(cached['tariffs']),
      offline: true,
      at: DateTime.tryParse('${cached['at'] ?? ''}'),
    );
  }

  Future<Map<String, dynamic>?> _readCache() async {
    try {
      final raw = await _db?.getSetting(cacheKey);
      if (raw == null || raw.isEmpty) return null;
      final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
      return m['owner'] == owner ? m : null; // кэш чужого аккаунта не действует
    } catch (_) {
      return null;
    }
  }

  Future<void> _writeCache(Map<String, Object?> parts) async {
    try {
      await _db?.setSetting(cacheKey, jsonEncode({'owner': owner, 'at': DateTime.now().toIso8601String(), ...parts}));
    } catch (_) {
      // кэш — удобство
    }
  }

  /// Переписать в кэше только состояние компании (после включения / выключения).
  Future<void> rememberCompany(Object? rawCompany) async {
    final c = await _readCache();
    await _writeCache({'modules': c?['modules'], 'company': rawCompany, 'tariffs': c?['tariffs']});
  }

  // ───────────────────────── изменение модулей ─────────────────────────

  /// Включить: PUT users/company/modules/ {enable:[id]}. Выключить: DELETE users/company/tariff/modules/{id}/ (нет такого адреса — PUT {disable:[id]}).
  /// If-Match — версия из последнего ответа; 409 version_conflict — один повтор (версия берётся из свежего ответа [company]).
  Future<MpChangeResult> change(String id, {required bool enable, required MpCompany? company}) async {
    final r = enable ? await _send('PUT', 'api/users/company/modules/', {'enable': [id]}, company?.version) : await _send('DELETE', 'api/users/company/tariff/modules/${Uri.encodeComponent(id)}/', null, company?.version, disabling: true);
    if (!enable && r.status == MpChange.unsupported) return _send('PUT', 'api/users/company/modules/', {'disable': [id]}, company?.version, disabling: true);
    return r;
  }

  Future<MpChangeResult> _send(String method, String path, Object? body, String? version, {bool disabling = false}) async {
    var v = version;
    for (var attempt = 0; attempt < 2; attempt++) {
      try {
        final data = await _api.send(method, path, body: body, headers: v == null || v.isEmpty ? null : {'If-Match': v}, timeout: _timeout);
        final c = MpCompany.parse(data);
        if (c != null) await rememberCompany(data);
        return MpChangeResult(MpChange.ok, company: c);
      } on ApiException catch (e) {
        final raw = '${e.payload ?? ''} ${e.message}'.toLowerCase();
        if (e.status == 409 && raw.contains('version_conflict')) {
          final fresh = await _freshVersion();
          if (attempt == 0 && fresh != null) {
            v = fresh;
            continue;
          }
          return const MpChangeResult(MpChange.conflict);
        }
        if (e.status == 409 && raw.contains('module_not_enabled') && disabling) return const MpChangeResult(MpChange.ok); // уже выключен — цель достигнута
        return MpChangeResult(_classify(e, raw), detail: e.message);
      }
    }
    return const MpChangeResult(MpChange.conflict);
  }

  Future<String?> _freshVersion() async {
    try {
      final c = MpCompany.parse(await _api.get('api/users/company/modules/', timeout: _timeout));
      return c?.version;
    } catch (_) {
      return null;
    }
  }

  static MpChange _classify(ApiException e, String raw) {
    if (e.status == 403 || raw.contains('permission_denied')) return MpChange.forbidden;
    if (raw.contains('module_unknown') || raw.contains('profile_unknown')) return MpChange.unknownModule;
    if (e.status == 404 || e.status == 405 || e.status == 501) return MpChange.unsupported;
    if (e.status == null || e.status == 408 || e.status == 429 || (e.status ?? 0) >= 500) return MpChange.noConnection;
    return MpChange.failed;
  }

  /// POST users/company/features/trial/ {code}: пробный период модуля. 409 trial_used / already_active — особые итоги.
  Future<MpChangeResult> startTrial(String id) async {
    try {
      await _api.send('POST', 'api/users/company/features/trial/', body: {'code': id}, timeout: _timeout);
      return const MpChangeResult(MpChange.ok);
    } on ApiException catch (e) {
      final raw = '${e.payload ?? ''} ${e.message}'.toLowerCase();
      if (e.status == 409 && raw.contains('trial_used')) return const MpChangeResult(MpChange.trialUsed);
      if (e.status == 409 && raw.contains('already_active')) return const MpChangeResult(MpChange.alreadyActive);
      return MpChangeResult(_classify(e, raw), detail: e.message);
    }
  }

  /// Свежий ответ компании после действия (пробный период ответ модулей не возвращает).
  Future<MpCompany?> reloadCompany() async {
    try {
      final data = await _api.get('api/users/company/modules/', timeout: _timeout);
      final c = MpCompany.parse(data);
      if (c != null) await rememberCompany(data);
      return c;
    } catch (_) {
      return null;
    }
  }

  // ───────────────────────── тариф «Своё» ─────────────────────────

  static Map<String, Object?> quoteBody(List<String> packs, List<String>? modules, String period) => {
    'packs': packs,
    'period': period == 'year' ? 'year' : 'month',
    'modules': ?modules,
  };

  Future<MpQuote> quote(List<String> packs, List<String>? modules, String period) async =>
      MpQuote.parse(await _api.post('api/users/company/tariff/quote/', body: quoteBody(packs, modules, period), timeout: _timeout));

  /// Заявка (только владелец; сервер записывает её, дальше — менеджер). Повтор с тем же ключом сервер отдаёт тем же ответом — заявка не задваивается.
  Future<MpOrder> submitCustom(List<String> packs, List<String>? modules, String period, String idempotencyKey) async {
    final data = await _api.post('api/users/company/tariff/custom/', body: quoteBody(packs, modules, period), headers: {'Idempotency-Key': idempotencyKey}, timeout: _timeout);
    final m = data is Map ? data : const {};
    return MpOrder(_s(m['order_id']) ?? _s(m['id']) ?? '', _s(m['status']) ?? 'pending', _s(m['pay_url']));
  }
}
