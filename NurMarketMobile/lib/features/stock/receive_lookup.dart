import 'package:drift/drift.dart' show StringExpressionOperators;

import '../../core/api/api_client.dart';
import '../../core/catalog/catalog_matcher.dart';
import '../../core/catalog/global_products.dart';
import '../../core/catalog/product_matcher.dart';
import '../../core/db/database.dart';
import '../scan/barcode_lookup.dart';
import '../scan/code_lookup.dart';

/// 2026-10-27 (владелец: «Приёмка» показывает известные товары как «Неизвестный штрихкод»; расследование, раздел 6):
/// что нашлось по отсканированному коду в приёмке.
enum ReceiveKind {
  /// Товар магазина (на телефоне или на сервере): строка приёмки.
  found,

  /// У товара магазина в карточке тот же код, но с ошибкой в контрольной цифре — предложить исправить.
  typo,

  /// Кода нет у магазина, в справочнике NurCRM он есть, и по названию справочника есть ваш товар — предложить привязать код.
  similar,

  /// Кода нет у магазина, но он есть в справочнике NurCRM — предложить завести товар.
  global,

  /// Нигде не найден.
  none,
}

class ReceiveFind {
  final ReceiveKind kind;
  final String code;

  /// found / typo / similar — товар магазина.
  final Product? product;

  /// found — сколько штук прибавить (штрихкод упаковки — вся упаковка).
  final double qty;

  /// similar / global — товар справочника NurCRM.
  final GlobalProduct? global;
  final double score;
  const ReceiveFind(this.kind, this.code, {this.product, this.qty = 1, this.global, this.score = 0});
}

/// Лучший «ваш» товар для названия из справочника должен быть не хуже этой оценки (строгая: «ASU Негаз 1л» не равен «ASU Негаз 1л персик»).
const receiveSimilarMin = 0.9;

/// Найти код: телефон → опечатка в контрольной цифре карточки (быстро, без сети) → сервер ([CodeLookup], 2 с) →
/// справочник NurCRM ([GlobalProducts], локально и сервер) + ваш похожий товар по названию справочника ([CatalogMatcher]).
/// [network] = false — только телефон и локальный справочник (восстановление черновика без связи). Любая ошибка — «не найден».
Future<ReceiveFind> resolveReceiveCode(AppDatabase db, ApiClient api, GlobalProducts? globals, String raw, {bool network = true}) async {
  final code = normalizeCode(raw);
  try {
    final local = await db.productByScan(code, allowWeightLabel: false);
    if (local != null) return ReceiveFind(ReceiveKind.found, code, product: local.product, qty: local.qty ?? 1);
    final typo = await findCheckDigitTypo(db, code);
    if (typo != null) return ReceiveFind(ReceiveKind.typo, code, product: typo);
    if (network) {
      final hit = await CodeLookup.findOnServer(db, api, code);
      if (hit.product != null) return ReceiveFind(ReceiveKind.found, code, product: hit.product, qty: hit.qty ?? 1);
    }
    final g = globals == null ? null : await globals.find(code, network: network);
    if (g != null) {
      final hits = await CatalogMatcher.find(db, '${g.brand} ${g.name}'.trim(), top: 3, strict: true);
      if (hits.isNotEmpty && hits.first.score >= receiveSimilarMin) {
        return ReceiveFind(ReceiveKind.similar, code, product: hits.first.product, global: g, score: hits.first.score);
      }
      return ReceiveFind(ReceiveKind.global, code, global: g);
    }
  } catch (_) {
    // сбой базы/сети — как «не найден»: кассир увидит честный текст и сможет завести товар
  }
  return ReceiveFind(ReceiveKind.none, code);
}

/// Товар магазина, у которого основной штрихкод отличается от [code] только последней цифрой и записан с ошибкой
/// контрольной цифры (61 из 343 кодов тестового магазина NBS такие: сканер их не найдёт никогда).
Future<Product?> findCheckDigitTypo(AppDatabase db, String code) async {
  if (!isValidEan(code)) return null;
  final prefix = code.substring(0, code.length - 1);
  final rows = await (db.select(db.products)..where((p) => p.barcode.like('$prefix%'))).get();
  for (final p in rows) {
    if (isCheckDigitTypo((p.barcode ?? '').trim(), code)) return p;
  }
  return null;
}
