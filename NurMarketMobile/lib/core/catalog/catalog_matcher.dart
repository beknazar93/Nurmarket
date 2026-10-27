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
  final Map<String, Product> byId;
  final String stamp;
  _Entry(this.matcher, this.byId, this.stamp);
}

/// Матчер по каталогу телефона. Индекс строится один раз и сам перестраивается, когда каталог обновился
/// (`catalog.syncedAt`), изменилось число товаров или алиасы ([CamAliases.version]). Весь каталог — в памяти (~30 000
/// товаров × несколько слов — десятки миллисекунд), без `LIKE` и лимита по алфавиту.
class CatalogMatcher {
  static final _cache = Expando<_Entry>('catalogMatcher');
  static final _building = Expando<Future<_Entry>>('catalogMatcherBuilding');

  static Future<_Entry> _entry(AppDatabase db) async {
    await CamAliases.ensureLocal(db);
    final synced = (await db.getSetting('catalog.syncedAt')) ?? '';
    final count = await db.productCount();
    final stamp = '$synced|$count|${CamAliases.version}';
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
    final all = await db.select(db.products).get();
    final aliases = CamAliases.current;
    final e = _Entry(
      ProductMatcher([for (final p in all) matchProductOf(p, aliases: aliases[p.id] ?? const [])]),
      {for (final p in all) p.id: p},
      stamp,
    );
    _cache[db] = e;
    return e;
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
    return [
      for (final c in e.matcher.match(query, brand: brand, volume: volume, barcode: barcode, top: top, minScore: minScore, strict: strict, requireVolume: requireVolume))
        if (e.byId[c.product.id] case final p?) ProductHit(p, c.score, c.reasons),
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
