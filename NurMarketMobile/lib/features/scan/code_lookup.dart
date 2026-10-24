import 'dart:async';
import 'dart:convert';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../sale/catalog_repository.dart';
import 'barcode_lookup.dart';

/// 2026-10-22 (аудит С-4, П7): поиск товара по коду как на ПК (ProductCodeLookup.FindAsync): сначала телефон, потом
/// сервер. Раньше незнакомый код сразу шёл в «Добавить товар?» — товар, заведённый на ПК 1–4 минуты назад (каталог
/// телефона обновляется раз в 5 минут), создавался вторым экземпляром.
class CodeLookup {
  /// Ответ сервера ждём не дольше 2 с — как касса.
  static const serverTimeout = Duration(seconds: 2);

  /// product — найден (qty — вес или штуки этикетки); unavailable — сервер не ответил, «не найден» говорить рано.
  static Future<({Product? product, double? qty, bool unavailable})> find(
    AppDatabase db,
    ApiClient api,
    String raw, {
    bool allowWeightLabel = true,
  }) async {
    final local = await db.productByScan(raw, allowWeightLabel: allowWeightLabel);
    if (local != null) return (product: local.product, qty: local.qty, unavailable: false);
    return findOnServer(db, api, raw);
  }

  static Future<({Product? product, double? qty, bool unavailable})> findOnServer(AppDatabase db, ApiClient api, String raw) async {
    final code = normalizeCode(raw);
    if (code.length < 3) return (product: null, qty: null, unavailable: false);
    final forms = barcodeVariants(code).toSet();
    try {
      final data = await api
          .get('api/main/products/list/', query: {'search': code, 'page': 1, 'page_size': 20}, timeout: serverTimeout)
          .timeout(serverTimeout + const Duration(milliseconds: 500));
      for (final p in asList(data)) {
        final alt = p['alternate_barcodes'] ?? p['alt_barcodes'] ?? p['barcodes'];
        final codes = <String>{
          '${p['barcode'] ?? ''}'.trim(),
          if (alt is List)
            for (final a in alt) (a is Map ? '${a['barcode'] ?? ''}' : '$a').trim(),
        };
        if (!codes.any(forms.contains)) continue;
        final row = CatalogRepository.mapProduct(p);
        if (row == null) continue;
        await db.upsertProduct(row);
        final saved = await db.productById(row.id.value);
        if (saved != null) {
          final hit = await db.productByScan(code, allowWeightLabel: false);
          return (product: saved, qty: hit?.product.id == saved.id ? hit?.qty : null, unavailable: false);
        }
      }
      return (product: null, qty: null, unavailable: false);
    } catch (_) {
      // нет связи, сервер не ответил за 2 с или ответил ошибкой
      return (product: null, qty: null, unavailable: true);
    }
  }
}

/// 2026-10-22 (аудит П7): правила весовых этикеток — из настроек компании NurCRM и общей настройки телефонов
/// компании; копия на телефоне (scale.rules), чтобы этикетки читались и без связи.
class ScaleRulesLoader {
  static const _cacheKey = 'scale.rules';
  static const sharedPath = 'api/main/app-settings/nurmarket-mobile/';
  static const sharedKey = 'ScaleLabelPrefixes';
  static DateTime _loadedAt = DateTime(2000);
  static bool _cacheRead = false;

  /// Копия с телефона (быстро), потом сервер — не чаще раза в 10 минут.
  static Future<void> ensure(AppDatabase db, ApiClient api) async {
    if (!_cacheRead) {
      _cacheRead = true;
      final cached = await db.getSetting(_cacheKey);
      if (cached != null && cached.isNotEmpty) ScaleRules.current = ScaleRules.fromJson(cached);
    }
    if (DateTime.now().difference(_loadedAt) < const Duration(minutes: 10)) return;
    _loadedAt = DateTime.now();
    var rules = ScaleRules.current;
    try {
      var company = await api.get('api/users/settings/company/', timeout: const Duration(seconds: 6));
      if (company is Map && company['data'] is Map) company = company['data'];
      if (company is Map) {
        String? str(String k) => company[k] is String && (company[k] as String).trim().isNotEmpty ? (company[k] as String).trim().toLowerCase() : null;
        rules = rules.copyWith(layout: str('scale_barcode_layout'), mode: str('scale_barcode_mode'), amountUnit: str('scale_barcode_amount_unit'));
      }
    } catch (_) {
      // нет связи — остаются прежние правила
    }
    try {
      final shared = await api.get(sharedPath, timeout: const Duration(seconds: 6));
      final settings = shared is Map && shared['settings'] is Map ? shared['settings'] as Map : null;
      final pref = settings?[sharedKey];
      if (pref is Map) {
        rules = rules.copyWith(amountPrefixes: ScaleRules.parsePrefixes(pref['amount']), weightPrefixes: ScaleRules.parsePrefixes(pref['weight']));
      }
    } catch (_) {}
    ScaleRules.current = rules;
    await db.setSetting(_cacheKey, jsonEncode(rules.toJson()));
  }

  /// Записать префиксы для всех телефонов компании (окно «Этикетки весов» сканера).
  static Future<void> savePrefixes(AppDatabase db, ApiClient api, Set<String> amount, Set<String> weight) async {
    await api.patch(sharedPath, body: {
      sharedKey: {'amount': amount.toList()..sort(), 'weight': weight.toList()..sort()},
    });
    ScaleRules.current = ScaleRules.current.copyWith(amountPrefixes: amount, weightPrefixes: weight);
    await db.setSetting(_cacheKey, jsonEncode(ScaleRules.current.toJson()));
  }
}
