import 'package:flutter/material.dart';

import '../l10n/app_localizations.dart';
import 'api/api_client.dart';
import 'db/database.dart';

/// 2026-10-31 (владелец: «режим магазина тоже добавь в мобилку»): сфера магазина, как «Сфера магазина» кассы
/// (MarketSpheres / SphereCatalog). Три базовые сферы живут на сервере (`market_sphere` в api/users/company/: grocery / clothing /
/// services), точная сфера «Хозтовары» (hardware) — в общих настройках компании (документ nurmarket-kassa, ключ MarketSphereDetail)
/// и на сервер как market_sphere не уходит: туда пишется базовая grocery — ровно как делает касса.
enum ShopMode { grocery, clothing, services, hardware }

extension ShopModeX on ShopMode {
  String get code => name;

  /// Что уходит на сервер в market_sphere.
  String get baseSphere => this == ShopMode.hardware ? 'grocery' : name;

  /// Размеры и цвета (варианты товара): выбор при продаже, надпись на плитке, скан штрихкода размера.
  bool get variants => this == ShopMode.clothing;

  /// «Продукты»: весы и весовые товары (кнопка весов в продаже). В одежде и услугах её нет.
  bool get weighing => this == ShopMode.grocery || this == ShopMode.hardware;

  /// Цвет режима: значок и рамка в шапке главной, в профиле и на плитке товара. Брендовый жёлтый остаётся за главными кнопками.
  Color get accent => switch (this) {
    ShopMode.grocery => const Color(0xFF2E9E5B),
    ShopMode.clothing => const Color(0xFF8E5BE8),
    ShopMode.services => const Color(0xFF1AA6B7),
    ShopMode.hardware => const Color(0xFFF59E0B),
  };

  /// Подсказка в строке поиска продажи под режим.
  String searchHint(AppLocalizations l) => switch (this) {
    ShopMode.grocery => l.shopSearchGrocery,
    ShopMode.clothing => l.shopSearchClothing,
    ShopMode.services => l.shopSearchServices,
    ShopMode.hardware => l.shopSearchHardware,
  };

  /// Пустое состояние списка товаров под режим.
  String emptyText(AppLocalizations l) => switch (this) {
    ShopMode.grocery => l.shopEmptyGrocery,
    ShopMode.clothing => l.shopEmptyClothing,
    ShopMode.services => l.shopEmptyServices,
    ShopMode.hardware => l.shopEmptyHardware,
  };

  IconData get icon => switch (this) {
    ShopMode.grocery => Icons.shopping_basket_rounded,
    ShopMode.clothing => Icons.checkroom_rounded,
    ShopMode.services => Icons.content_cut_rounded,
    ShopMode.hardware => Icons.hardware_rounded,
  };

  String title(AppLocalizations l) => switch (this) {
    ShopMode.grocery => l.shopModeGrocery,
    ShopMode.clothing => l.shopModeClothing,
    ShopMode.services => l.shopModeServices,
    ShopMode.hardware => l.shopModeHardware,
  };

  String subtitle(AppLocalizations l) => switch (this) {
    ShopMode.grocery => l.shopModeGrocerySub,
    ShopMode.clothing => l.shopModeClothingSub,
    ShopMode.services => l.shopModeServicesSub,
    ShopMode.hardware => l.shopModeHardwareSub,
  };

  static ShopMode? parse(String? v) {
    for (final m in ShopMode.values) {
      if (m.name == (v ?? '').trim().toLowerCase()) return m;
    }
    return null;
  }

  /// Режим по ответу сервера: базовая сфера + точная (hardware совместима только с базовой grocery, как SphereCatalog.Resolve кассы).
  static ShopMode? fromServer(String? base, String? detail) {
    final b = parse(base);
    if (b == null || b == ShopMode.hardware) return null;
    if (b == ShopMode.grocery && (detail ?? '').trim().toLowerCase() == 'hardware') return ShopMode.hardware;
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
    final b = ShopModeX.parse(base);
    if (b == null || b == ShopMode.hardware) return null;
    final seen = await db.getSetting(serverSeenKey);
    final current = await read(db);
    final target = ShopModeX.fromServer(base, detail)!;
    if (seen == b.code) {
      // сфера на сервере та же; режим на телефоне не выбирали, а сервер говорит не «Продукты» — берём серверный
      if (current == null && target != ShopMode.grocery) {
        await write(db, target);
        return target;
      }
      return null;
    }
    await db.setSetting(serverSeenKey, b.code);
    final firstSightOnConfigured = seen == null && current != null;
    if (firstSightOnConfigured || current == target) return null;
    // «Продукты» на сервере — значение по умолчанию любой компании: на телефоне оно не считается выбором (иначе магазин одежды,
    // которому на кассе не меняли сферу, никогда не получил бы подсказку «Включить режим «Одежда»?»)
    if (current == null && target == ShopMode.grocery) return null;
    await write(db, target);
    return target;
  }

  /// Прочитать сферу компании с сервера и применить. Нет связи/ошибка — тихо ничего.
  static Future<ShopMode?> syncFromServer(AppDatabase db, ApiClient api, {Duration timeout = const Duration(seconds: 6)}) async {
    try {
      var company = await api.get(companyPath, timeout: timeout);
      if (company is Map && company['data'] is Map) company = company['data'];
      if (company is! Map) return null;
      final base = company['market_sphere'] is String ? company['market_sphere'] as String : null;
      String? detail;
      if ((base ?? '').trim().toLowerCase() == 'grocery') {
        try {
          final doc = await api.get(settingsPath, timeout: timeout);
          final st = doc is Map && doc['settings'] is Map ? doc['settings'] as Map : null;
          final d = st?[detailKey];
          if (d is String) detail = d;
        } catch (_) {
          // общих настроек не прочитать — остаётся базовая сфера
        }
      }
      return await applyServer(db, base: base, detail: detail);
    } catch (_) {
      return null;
    }
  }

  /// Записать выбор на сервер, как делает касса (владелец; у остальных 403 — режим остаётся только на этом телефоне):
  /// market_sphere — базовая сфера; точная (hardware) — в общих настройках компании, только если она есть или нужна.
  /// Возвращает true, если сервер принял; false — режим сохранён только на телефоне.
  static Future<bool> saveToServer(AppDatabase db, ApiClient api, ShopMode mode) async {
    try {
      await api.patch(companyPath, body: {'market_sphere': mode.baseSphere});
      await db.setSetting(serverSeenKey, mode.baseSphere);
      try {
        final doc = await api.get(settingsPath, timeout: const Duration(seconds: 6));
        final st = doc is Map && doc['settings'] is Map ? doc['settings'] as Map : const {};
        final had = st[detailKey] is String && (st[detailKey] as String).trim().isNotEmpty;
        // как MarketSpheres.Set кассы: точную сферу пишем, если выбрали hardware или она уже была (иначе другие кассы вернули бы старую)
        if (mode == ShopMode.hardware || had) await api.patch(settingsPath, body: {detailKey: mode == ShopMode.hardware ? 'hardware' : mode.code});
      } catch (_) {
        // точная сфера не записалась — базовая на сервере уже стоит
      }
      return true;
    } catch (_) {
      return false;
    }
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
