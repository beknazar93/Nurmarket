import 'package:flutter/material.dart';

import '../l10n/app_localizations.dart';
import 'api/api_client.dart';
import 'db/database.dart';

/// 2026-10-31 (владелец: «режим магазина тоже добавь в мобилку»): сфера магазина, как «Сфера магазина» кассы
/// (MarketSpheres / SphereCatalog). Три базовые сферы живут на сервере (`market_sphere` в api/users/company/: grocery / clothing /
/// services), точная сфера «Хозтовары» (hardware) — в общих настройках компании (документ nurmarket-kassa, ключ MarketSphereDetail)
/// и на сервер как market_sphere не уходит: туда пишется базовая grocery — ровно как делает касса.
/// 2026-11-22 (владелец: «в мобилке нет сферы Базар и Автозапчасти»): добавлены «Базар» (bazaar) и «Автозапчасти» (autoparts) — тоже точные сферы
/// на базовой grocery (MarketSphereDetail = bazaar / bazaar_retail / bazaar_wholesale / autoparts, как BazaarProfile / AutoPartsProfile кассы).
/// Выбора сферы в мобилке НЕТ: её задаёт владелец в кассе/CRM, телефон только читает с сервера (ShopModeSync.syncFromServer).
enum ShopMode { grocery, clothing, services, hardware, bazaar, autoparts }

extension ShopModeX on ShopMode {
  String get code => name;

  /// Что уходит на сервер в market_sphere.
  String get baseSphere => isBase ? name : 'grocery';

  /// Базовая сфера сервера (market_sphere): grocery / clothing / services. Остальные — точные сферы поверх grocery.
  bool get isBase => this == ShopMode.grocery || this == ShopMode.clothing || this == ShopMode.services;

  /// Размеры и цвета (варианты товара): выбор при продаже, надпись на плитке, скан штрихкода размера.
  bool get variants => this == ShopMode.clothing;

  /// «Продукты»: весы и весовые товары (кнопка весов в продаже). В одежде и услугах её нет.
  bool get weighing => this == ShopMode.grocery || this == ShopMode.hardware || this == ShopMode.bazaar;

  /// Цвет режима: значок и рамка в шапке главной, в профиле и на плитке товара. Брендовый жёлтый остаётся за главными кнопками.
  Color get accent => switch (this) {
    ShopMode.grocery => const Color(0xFF2E9E5B),
    ShopMode.clothing => const Color(0xFF8E5BE8),
    ShopMode.services => const Color(0xFF1AA6B7),
    ShopMode.hardware => const Color(0xFFF59E0B),
    ShopMode.bazaar => const Color(0xFFEA580C), // как AccentHex «Базара» в кассе
    ShopMode.autoparts => const Color(0xFF0EA5E9), // как AccentHex «Автозапчастей» в кассе
  };

  /// Подсказка в строке поиска продажи под режим.
  String searchHint(AppLocalizations l) => switch (this) {
    ShopMode.grocery => l.shopSearchGrocery,
    ShopMode.clothing => l.shopSearchClothing,
    ShopMode.services => l.shopSearchServices,
    ShopMode.hardware => l.shopSearchHardware,
    ShopMode.bazaar => l.shopSearchBazaar,
    ShopMode.autoparts => l.shopSearchAutoparts,
  };

  /// Пустое состояние списка товаров под режим.
  String emptyText(AppLocalizations l) => switch (this) {
    ShopMode.grocery => l.shopEmptyGrocery,
    ShopMode.clothing => l.shopEmptyClothing,
    ShopMode.services => l.shopEmptyServices,
    ShopMode.hardware => l.shopEmptyHardware,
    ShopMode.bazaar => l.shopEmptyBazaar,
    ShopMode.autoparts => l.shopEmptyAutoparts,
  };

  IconData get icon => switch (this) {
    ShopMode.grocery => Icons.shopping_basket_rounded,
    ShopMode.clothing => Icons.checkroom_rounded,
    ShopMode.services => Icons.content_cut_rounded,
    ShopMode.hardware => Icons.hardware_rounded,
    ShopMode.bazaar => Icons.storefront_rounded,
    ShopMode.autoparts => Icons.directions_car_rounded,
  };

  String title(AppLocalizations l) => switch (this) {
    ShopMode.grocery => l.shopModeGrocery,
    ShopMode.clothing => l.shopModeClothing,
    ShopMode.services => l.shopModeServices,
    ShopMode.hardware => l.shopModeHardware,
    ShopMode.bazaar => l.shopModeBazaar,
    ShopMode.autoparts => l.shopModeAutoparts,
  };

  String subtitle(AppLocalizations l) => switch (this) {
    ShopMode.grocery => l.shopModeGrocerySub,
    ShopMode.clothing => l.shopModeClothingSub,
    ShopMode.services => l.shopModeServicesSub,
    ShopMode.hardware => l.shopModeHardwareSub,
    ShopMode.bazaar => l.shopModeBazaarSub,
    ShopMode.autoparts => l.shopModeAutopartsSub,
  };

  static ShopMode? parse(String? v) {
    for (final m in ShopMode.values) {
      if (m.name == (v ?? '').trim().toLowerCase()) return m;
    }
    return null;
  }

  /// Точная сфера из MarketSphereDetail (как SphereCatalog.CanonicalId кассы): hardware / hozmag / stroymag — «Хозтовары»,
  /// bazaar / bazaar_retail / bazaar_wholesale — «Базар», autoparts — «Автозапчасти». Незнакомое значение — null (останется базовая).
  static ShopMode? fromDetail(String? detail) => switch ((detail ?? '').trim().toLowerCase()) {
    'hardware' || 'hozmag' || 'stroymag' => ShopMode.hardware,
    'bazaar' || 'bazaar_retail' || 'bazaar_wholesale' => ShopMode.bazaar,
    'autoparts' => ShopMode.autoparts,
    _ => null,
  };

  /// Режим по ответу сервера: базовая сфера + точная (точная совместима только с базовой grocery, как SphereCatalog.Resolve кассы).
  static ShopMode? fromServer(String? base, String? detail) {
    // 2026-11-26 (проверка в эмуляторе): сервер отдаёт точную сферу и прямо в market_sphere (company.market_sphere = 'autoparts'),
    // а не только в MarketSphereDetail поверх grocery — раньше такой ответ отбрасывался и плитка «Режим магазина» была «Не выбран».
    final b = parse(base) ?? fromDetail(base);
    if (b == null) return null;
    if (b == ShopMode.grocery) return fromDetail(detail) ?? ShopMode.grocery;
    return b;
  }
}

/// Хранение и синхронизация режима магазина. Всё лежит в настройках телефона (`settings`) — они стираются при выходе и смене
/// аккаунта вместе с остальными (AccountSwitch.wipeAll), поэтому режим не переходит от одного магазина к другому.
class ShopModeSync {
  static const modeKey = 'shop.mode';
  static const legacyClothingKey = 'shop.clothing'; // его читают разбор весовых этикеток и сканер; 1 — только в режиме «Одежда»
  static const serverSeenKey = 'shop.modeServerSeen';
  static const hintOffKey = 'shop.clothingHintOff';
  static const companyPath = 'api/users/company/';
  static const settingsPath = 'api/main/app-settings/nurmarket-kassa/';
  static const detailKey = 'MarketSphereDetail';

  /// Выбранный режим; null — не выбирали. Старая настройка «Размеры и цвета при продаже» (shop.clothing = 1) — режим «Одежда».
  static Future<ShopMode?> read(AppDatabase db) async {
    final m = ShopModeX.parse(await db.getSetting(modeKey));
    if (m != null) return m;
    return (await db.getSetting(legacyClothingKey)) == '1' ? ShopMode.clothing : null;
  }

  static Future<void> write(AppDatabase db, ShopMode mode) async {
    await db.setSetting(modeKey, mode.code);
    await db.setSetting(legacyClothingKey, mode == ShopMode.clothing ? '1' : '0');
  }

  /// Взять режим с сервера, как MarketSphereSync кассы: на новой установке (режим не выбирали) или когда на сервере сменили
  /// сферу с тех пор, как телефон видел её в прошлый раз. Выбор на уже настроенном телефоне при первом знакомстве с сервером не
  /// затирается; «не задан» (null) локальный выбор не трогает. Возвращает новый режим, если он сменился, иначе null.
  /// [detail] — точная сфера из общих настроек компании (hardware).
  static Future<ShopMode?> applyServer(AppDatabase db, {required String? base, String? detail}) async {
    final target = ShopModeX.fromServer(base, detail);
    if (target == null) return null;
    final seen = await db.getSetting(serverSeenKey);
    final current = await read(db);
    // 2026-11-22: «виденное» — полный серверный режим (с точной сферой), а не базовая сфера: иначе смена hardware → bazaar → autoparts
    // на сервере (база везде grocery) телефон не замечал. Старое значение (базовая сфера) читается как есть.
    if (seen == target.code) {
      // сфера на сервере та же; режим на телефоне не выбирали (или он расходится с явной сферой сервера), а сервер говорит не «Продукты» — берём серверный
      // 2026-11-26 («при смене сферы сфера не меняется»): выбора сферы в телефоне больше нет, поэтому явная сфера сервера (не «Продукты» по умолчанию) всегда главнее старого выбора
      if (target != ShopMode.grocery && current != target) {
        await write(db, target);
        return target;
      }
      return null;
    }
    await db.setSetting(serverSeenKey, target.code);
    // 2026-11-26: защита выбора на настроенном телефоне при первом знакомстве — только против «Продуктов» (значение по умолчанию); явная сфера сервера применяется сразу
    final firstSightOnConfigured = seen == null && current != null && target == ShopMode.grocery;
    if (firstSightOnConfigured || current == target) return null;
    // «Продукты» на сервере — значение по умолчанию любой компании: на телефоне оно не считается выбором (иначе магазин одежды,
    // которому на кассе не меняли сферу, никогда не получил бы подсказку «Включить режим «Одежда»?»)
    if (current == null && target == ShopMode.grocery) return null;
    await write(db, target);
    return target;
  }

  /// Прочитать сферу компании с сервера (без записи в базу): (base, detail) для [applyServer]. Нет связи/ошибка — null.
  /// 2026-11-26 («при смене сферы сфера не меняется»): сервер отдаёт готовый профиль компании `market_profile` (grocery, clothing, hozmag,
  /// stroymag, bazaar_retail, bazaar_wholesale, autoparts, electronics …). У «Хозмага» и «Строймага» `market_sphere` = null — раньше телефон
  /// такой ответ не понимал и сферу не менял. Профиль главнее; нет его (старый сервер) — прежний путь через market_sphere + MarketSphereDetail.
  static Future<({String? base, String? detail})?> fetchServer(ApiClient api, {Duration timeout = const Duration(seconds: 6)}) async {
    try {
      var company = await api.get(companyPath, timeout: timeout);
      if (company is Map && company['data'] is Map) company = company['data'];
      if (company is! Map) return null;
      final profile = company['market_profile'] is String ? (company['market_profile'] as String).trim().toLowerCase() : '';
      if (profile.isNotEmpty && profile != 'grocery') {
        // профиль без своего режима в телефоне (electronics …) — обычные «Продукты», а не прежний режим другой сферы
        return (base: fromServerKnown(profile) ? profile : 'grocery', detail: null);
      }
      final base = company['market_sphere'] is String ? company['market_sphere'] as String : null;
      String? detail;
      if ((base ?? '').trim().toLowerCase() == 'grocery' || profile == 'grocery') {
        // точная сфера (hardware / bazaar / autoparts) лежит в общих настройках компании, её выбирает владелец в кассе
        try {
          final doc = await api.get(settingsPath, timeout: timeout);
          final st = doc is Map && doc['settings'] is Map ? doc['settings'] as Map : null;
          final d = st?[detailKey];
          if (d is String) detail = d;
        } catch (_) {
          // 2026-11-22: общих настроек не прочитать — НЕ применяем «голую» grocery (иначе Базар/Автозапчасти/Хозтовары на миг слетали бы в «Продукты»)
          return null;
        }
      }
      return (base: base ?? (profile == 'grocery' ? 'grocery' : null), detail: detail);
    } catch (_) {
      return null;
    }
  }

  /// Знает ли телефон такой профиль сервера (есть свой режим).
  static bool fromServerKnown(String profile) => ShopModeX.fromServer(profile, null) != null;

  /// Прочитать сферу компании с сервера и применить. Нет связи/ошибка — тихо ничего.
  static Future<ShopMode?> syncFromServer(AppDatabase db, ApiClient api, {Duration timeout = const Duration(seconds: 6)}) async {
    final r = await fetchServer(api, timeout: timeout);
    if (r == null) return null;
    return applyServer(db, base: r.base, detail: r.detail);
  }
}

/// Через сколько дней кончается срок годности («ГГГГ-ММ-ДД»); null — срока нет. Отрицательное — уже истёк.
int? daysToExpiry(String? iso, {DateTime? now}) {
  if (iso == null || iso.length < 8) return null;
  final d = DateTime.tryParse(iso);
  if (d == null) return null;
  final n = now ?? DateTime.now();
  return DateTime(d.year, d.month, d.day).difference(DateTime(n.year, n.month, n.day)).inDays;
}

/// «05.11.2026».
String expiryText(String iso) {
  final d = DateTime.tryParse(iso);
  if (d == null) return iso;
  String two(int x) => x.toString().padLeft(2, '0');
  return '${two(d.day)}.${two(d.month)}.${d.year}';
}

/// «05.11» — коротко, для значка на плитке.
String expiryShort(String iso) {
  final d = DateTime.tryParse(iso);
  if (d == null) return iso;
  String two(int x) => x.toString().padLeft(2, '0');
  return '${two(d.day)}.${two(d.month)}';
}

/// Штучная единица (шт, pcs …): у «Хозтоваров» остальные единицы (м, м², кг, упаковка) показываются у количества и на плитке.
bool isPieceUnit(String unit) => const {'', 'шт', 'шт.', 'pcs', 'pc', 'piece', 'adet', 'dona', 'даана'}.contains(unit.trim().toLowerCase());
