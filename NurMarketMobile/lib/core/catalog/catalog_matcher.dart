import 'dart:isolate';

import 'package:flutter/foundation.dart' show visibleForTesting;

import '../ai/cam_aliases.dart';
import '../db/database.dart';
import 'product_matcher.dart';

/// 2026-10-27: карточка товара мобилки → [MatchProduct] для [ProductMatcher] (одна функция-переходник на всё приложение).
MatchProduct matchProductOf(Product p, {List<String> aliases = const []}) => MatchProduct(
  id: p.id,
  name: p.name,
  barcode: p.barcode ?? '',
  altBarcodes: [
    for (final b in p.altBarcodes.split('\n'))
      if (b.trim().isNotEmpty) b.trim(),
  ],
  sku: p.sku ?? '',
  category: p.category,
  aliases: aliases,
);

/// Товар каталога телефона, оценка и причины.
class ProductHit {
  final Product product;
  final double score;
  final List<String> reasons;
  const ProductHit(this.product, this.score, this.reasons);
  @override
  String toString() => '${product.name} ${score.toStringAsFixed(2)}';
}

class _Entry {
  final ProductMatcher matcher;
  final String stamp;
  _Entry(this.matcher, this.stamp);
}

/// Матчер по каталогу телефона. Индекс строится один раз и сам перестраивается, когда изменились названия/штрихкоды/артикулы
/// товаров (`catalog.namesAt`), число товаров или алиасы ([CamAliases.version]). Весь каталог — в памяти (~30 000
/// товаров × несколько слов), без `LIKE` и лимита по алфавиту.
/// 2026-11-05 (М5-25): индекс строится В ОТДЕЛЬНОМ ИЗОЛЯТЕ из лёгких записей (раньше — на главном потоке из полных карточек, и после
/// каждой продажи/ленты, менявшей остатки, первый снимок замораживал экран на 0,3–1,5 с). Остатки и цены в индекс не входят,
/// поэтому продажа его больше не сбрасывает.
class CatalogMatcher {
  /// Растёт при смене аккаунта: индекс прошлого магазина не должен пережить вход в другой (вместе с AccountReset).
  static int _epoch = 0;

  static void forgetAccount() => _epoch++;

  /// Для проверок: строить индекс в том же потоке (в тестах с поддельным временем изолят не нужен).
  @visibleForTesting
  static bool buildInline = false;

  static final _cache = Expando<_Entry>('catalogMatcher');
  static final _building = Expando<Future<_Entry>>('catalogMatcherBuilding');

  static Future<_Entry> _entry(AppDatabase db) async {
    await CamAliases.ensureLocal(db);
    // 2026-11-05 (М5-25): раньше — `catalog.syncedAt`, он менялся от любой ленты (остатки после каждой продажи); теперь — `catalog.namesAt`,
    // его ставит загрузка каталога, только когда изменились названия, штрихкоды, артикулы, категории или состав товаров
    final names = (await db.getSetting('catalog.namesAt')) ?? '';
    final count = await db.productCount();
    final stamp = '$_epoch|$names|$count|${CamAliases.version}';
    final cached = _cache[db];
    if (cached != null && cached.stamp == stamp) return cached;
    final pending = _building[db];
    if (pending != null) {
      final e = await pending;
      if (e.stamp == stamp) return e;
    }
    final f = _build(db, stamp);
    _building[db] = f;
    try {
      return await f;
    } finally {
      if (identical(_building[db], f)) _building[db] = null;
    }
  }

  static Future<_Entry> _build(AppDatabase db, String stamp) async {
    // только поля, нужные индексу (без фото, цен, остатков, акций …) — лёгкие записи
    final rows = await db.customSelect('SELECT id, name, barcode, alt_barcodes, sku, category FROM products', readsFrom: {db.products}).get();
    final aliases = CamAliases.current;
    final list = [
      for (final r in rows)
        MatchProduct(
          id: r.read<String>('id'),
          name: r.read<String>('name'),
          barcode: r.read<String?>('barcode') ?? '',
          altBarcodes: [
            for (final b in (r.read<String?>('alt_barcodes') ?? '').split('\n'))
              if (b.trim().isNotEmpty) b.trim(),
          ],
          sku: r.read<String?>('sku') ?? '',
          category: r.read<String?>('category') ?? '',
          aliases: aliases[r.read<String>('id')] ?? const [],
        ),
    ];
    ProductMatcher matcher;
    if (buildInline || list.length < 400) {
      matcher = ProductMatcher(list); // маленький каталог — изолят дороже самого построения
    } else {
      try {
        matcher = await Isolate.run(() => ProductMatcher(list));
      } catch (_) {
        matcher = ProductMatcher(list); // изолят не запустился — по-старому, в этом потоке
      }
    }
    final e = _Entry(matcher, stamp);
    _cache[db] = e;
    return e;
  }

  /// Карточки найденных товаров по id (одним запросом); индекс карточек целиком не держит.
  static Future<Map<String, Product>> _byIds(AppDatabase db, Iterable<String> ids) async {
    final list = ids.toSet().toList();
    if (list.isEmpty) return const {};
    final rows = await (db.select(db.products)..where((p) => p.id.isIn(list))).get();
    return {for (final p in rows) p.id: p};
  }

  /// Построить индекс заранее (экран открыт, кассир наводит камеру): ошибки не важны.
  static Future<void> warm(AppDatabase db) async {
    try {
      await _entry(db);
    } catch (_) {}
  }

  /// Матчер по текущему каталогу.
  static Future<ProductMatcher> of(AppDatabase db) async => (await _entry(db)).matcher;

  /// Лучшие [top] товаров каталога для [query]: товар, оценка 0..1, причины. Пустой каталог или запрос — пустой список.
  static Future<List<ProductHit>> find(
    AppDatabase db,
    String query, {
    String? brand,
    String? volume,
    String? barcode,
    int top = 5,
    double minScore = 0.3,
    bool strict = false,
    bool requireVolume = false,
  }) async {
    final e = await _entry(db);
    final found = e.matcher.match(query, brand: brand, volume: volume, barcode: barcode, top: top, minScore: minScore, strict: strict, requireVolume: requireVolume);
    final byId = await _byIds(db, [for (final c in found) c.product.id]);
    return [
      for (final c in found)
        if (byId[c.product.id] case final p?) ProductHit(p, c.score, c.reasons),
    ];
  }

  /// Автовыбор по правилу «≥ 0,85 и отрыв ≥ 0,15» ([ProductMatcher.confident]) среди [hits].
  static ProductHit? confident(List<ProductHit> hits) {
    if (hits.isEmpty) return null;
    final best = hits.first;
    if (best.score < autoScore) return null;
    if (hits.length > 1 && best.score - hits[1].score < autoGap) return null;
    return best;
  }
}
