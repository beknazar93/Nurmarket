import 'dart:convert';

import 'package:drift/drift.dart' show Value;

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/sync/outbox_meta.dart';
import '../stock/product_packages.dart';
import 'cart.dart' show PromoRule;

/// Каталог: страницы api/main/products/list/ по 300 → локальная база (ТЗ, раздел 8: обновление раз в 5 минут).
class CatalogRepository {
  final ApiClient api;
  final AppDatabase db;
  CatalogRepository(this.api, this.db);

  static const _pageSize = 300;

  Future<int> sync() async {
    final rows = <ProductsCompanion>[];
    final seen = <String>{};
    final noMin = <String>{};
    // 2026-10-27 (М4): поштучная продажа из упаковки (packages) по товарам; listHasPackages — список вообще отдаёт это поле
    final packs = <String, PiecePack>{};
    var listHasPackages = false;
    for (var page = 1; page <= 100; page++) {
      final data = await api.get('api/main/products/list/', query: {'page': page, 'page_size': _pageSize}, timeout: const Duration(seconds: 40));
      final items = asList(data);
      for (final p in items) {
        final row = _map(p);
        if (row != null && seen.add(row.id.value)) {
          rows.add(row);
          if (p.containsKey('packages')) {
            listHasPackages = true;
            // комплект (bundle): packages — его состав, а не штуки из упаковки
            final pk = str(p, 'kind') == 'bundle' ? null : PiecePack.first(p['packages']);
            if (pk != null) packs[row.id.value] = pk;
          }
          // 2026-10-22 (мобилка, админ): список каталога может не отдавать minimum_quantity — тогда прежний минимум
          // товара не сбрасываем (ниже подставляем из базы телефона).
          if (!p.containsKey('minimum_quantity') && !p.containsKey('min_quantity')) noMin.add(row.id.value);
        }
      }
      // Страница после последней бывает с ответом 500 — дальше не идём, если пришло меньше страницы или нет next.
      final hasNext = data is Map ? data['next'] != null : items.length >= _pageSize;
      if (items.length < _pageSize || !hasNext) break;
    }
    if (rows.isNotEmpty) {
      if (noMin.isNotEmpty) {
        final old = {for (final p in await db.select(db.products).get()) p.id: p.minQuantity};
        for (var i = 0; i < rows.length; i++) {
          final keep = old[rows[i].id.value];
          if (keep != null && keep > 0 && noMin.contains(rows[i].id.value)) rows[i] = rows[i].copyWith(minQuantity: Value(keep));
        }
      }
      await db.replaceProducts(rows);
      if (listHasPackages) await PackageStore.replaceAll(db, packs);
      await _reapplyPending();
      await db.setSetting('catalog.syncedAt', DateTime.now().toIso8601String());
    }
    return rows.length;
  }

  /// 2026-10-22 (сканер, аудит С-4): товар с сервера в базу телефона по той же раскладке, что и каталог.
  static ProductsCompanion? mapProduct(Map<String, dynamic> p) => _map(p);

  /// 2026-10-22 (мобилка, деньги): каталог с сервера ещё не знает о продажах и приходах из очереди телефона — остатки
  /// «откатывались» до досылки (продал 5 без связи — через 5 минут остаток снова прежний). Не отправленное — поверх.
  Future<void> _reapplyPending() async {
    for (final row in await db.pending()) {
      try {
        final body = (jsonDecode(row.body) as Map).cast<String, dynamic>();
        if (row.kind == 'sale') {
          final meta = OutboxMeta.read(row.body);
          for (final it in asList(body['items'])) {
            final id = str(it, 'product');
            final q = OutboxMeta.stockQty(it, meta); // 2026-10-27 (М4): штуки из упаковки — доля упаковки
            if (id != null && q > 0) await db.adjustStock(id, -q);
          }
        } else if (row.kind == 'receive') {
          for (final l in asList(body['lines'])) {
            final id = str(l, 'productId');
            final q = num0(l, 'qty');
            if (id != null && q > 0 && l['done'] != true) await db.adjustStock(id, q);
          }
        }
      } catch (_) {
        // повреждённая запись — без поправки остатка
      }
    }
  }

  static ProductsCompanion? _map(Map<String, dynamic> p) {
    final id = str(p, 'id');
    final name = str(p, 'name') ?? str(p, 'title') ?? str(p, 'display_name');
    if (id == null || name == null) return null;
    final unit = (str(p, 'unit') ?? str(p, 'unit_display') ?? 'шт').trim();
    final u = unit.toLowerCase();
    final isWeight =
        _truthy(p, ['is_weight', 'is_wait', 'is_weigh', 'is_weight_product', 'sale_as_weight', 'sells_by_weight']) ||
        u == 'кг' ||
        u == 'kg' ||
        u == 'г' ||
        u == 'g';
    final alt = <String>[];
    final altRaw = p['alternate_barcodes'];
    if (altRaw is List) {
      for (final a in altRaw) {
        final code = a is Map ? (a['barcode'] ?? a['code'])?.toString() : a?.toString();
        if (code == null || code.trim().isEmpty) continue;
        // 13.10: доп. штрихкод упаковки (коробка, блок) — с количеством, как у кассы: «код|кол-во|название»
        final q = a is Map ? (num.tryParse('${a['quantity'] ?? ''}') ?? 0) : 0;
        alt.add(q > 1 ? '${code.trim()}|${q.toString()}|${a is Map ? (a['name'] ?? '') : ''}' : code.trim());
      }
    }
    final cat = p['category'];
    final category = cat is Map ? (cat['name']?.toString() ?? '') : (str(p, 'category_name') ?? str(p, 'category') ?? '');
    final image = productImageUrl(p);
    return ProductsCompanion.insert(
      id: id,
      name: name.trim(),
      price: Value(num0(p, 'price')),
      wholesalePrice: Value(num0(p, 'wholesale_price')),
      barcode: Value(str(p, 'barcode') ?? str(p, 'ean')),
      altBarcodes: Value(alt.join('\n')),
      sku: Value(str(p, 'sku') ?? str(p, 'article') ?? str(p, 'code')),
      plu: Value(str(p, 'plu')),
      unit: Value(unit.isEmpty ? 'шт' : unit),
      isWeight: Value(isWeight),
      isService: Value(_truthy(p, ['is_service']) || str(p, 'kind') == 'service'),
      isFavorite: Value(_truthy(p, ['is_favorite'])),
      quantity: Value(num0(p, 'quantity')),
      category: Value(category),
      imageUrl: Value(image),
      searchKey: Value([name, str(p, 'sku') ?? str(p, 'article') ?? '', str(p, 'barcode') ?? '', category, str(p, 'brand_name') ?? ''].join(' ').toLowerCase()),
      promoRules: Value(PromoRule.encodeFromApi(p['promotion_rules'])),
      minQuantity: Value(num0(p, 'minimum_quantity') > 0 ? num0(p, 'minimum_quantity') : num0(p, 'min_quantity')),
    );
  }

  /// 13.10, владелец: «почему в мобилке не отображаются фотки товаров?» — сервер отдаёт фото не строкой в image_url, а
  /// списком images (объекты с адресом) или вложенным объектом; искали только строки верхнего уровня, фото не находилось.
  /// Правила — как у кассы (ProductImageUrl.TryGet): все поля-кандидаты, относительный адрес — от адреса сервера,
  /// лучший кандидат — jpg/png выше webp (webp Flutter тоже показывает, но берётся, только если другого нет).
  static String? productImageUrl(Map<String, dynamic> p) {
    final candidates = <String>[];
    void add(Object? v) {
      if (v is! String) return;
      var u = v.trim();
      if (u.isEmpty) return;
      if (u.startsWith('//')) {
        u = 'https:$u';
      } else if (!u.startsWith('http://') && !u.startsWith('https://')) {
        u = '${ApiClient.defaultBaseUrl}/${u.replaceFirst(RegExp(r'^/+'), '')}';
      }
      if (!candidates.any((c) => c.toLowerCase() == u.toLowerCase())) candidates.add(u);
    }

    const urlKeys = ['url', 'src', 'image', 'file', 'path', 'thumbnail', 'full', 'download_url', 'href'];
    void addFrom(Object? v) {
      if (v is String) add(v);
      if (v is Map) {
        for (final k in urlKeys) {
          add(v[k]);
        }
      }
    }

    for (final k in [
      'primary_image_url',
      'image_url',
      'thumbnail_url',
      'photo_url',
      'picture_url',
      'main_image_url',
      'cover_url',
      'preview_url',
      'image_path',
      'media_url',
    ]) {
      add(p[k]);
    }
    for (final k in ['image', 'photo', 'thumbnail', 'picture', 'cover', 'img', 'primary_image', 'main_image', 'cover_image', 'image_data', 'photo_data']) {
      addFrom(p[k]);
    }
    for (final k in ['images', 'photos', 'gallery', 'media', 'attachments']) {
      final arr = p[k];
      if (arr is List) {
        for (final it in arr.take(8)) {
          addFrom(it);
        }
      }
    }
    if (candidates.isEmpty) return null;
    int score(String url) {
      final raw = url.toLowerCase();
      var s = raw.contains('thumb') ? 15 : 0;
      if (raw.contains('.jpg') || raw.contains('.jpeg')) {
        s += 100;
      } else if (raw.contains('.png')) {
        s += 90;
      } else if (raw.contains('.gif')) {
        s += 70;
      } else if (raw.contains('.webp')) {
        s -= 50;
      } else {
        s += 40;
      }
      return s;
    }

    candidates.sort((x, y) => score(y).compareTo(score(x)));
    return candidates.first;
  }

  static bool _truthy(Map<String, dynamic> p, List<String> keys) {
    for (final k in keys) {
      final v = p[k];
      if (v == true || v == 1 || v == 'true' || v == '1') return true;
    }
    return false;
  }

  /// 13.10, макет С5: варианты товара (размер, цвет, остаток, цена) — GET api/main/products/{id}/variants/, как у кассы.
  Future<List<ProductVariant>> variants(String productId) async {
    final data = await api.get('api/main/products/$productId/variants/', timeout: const Duration(seconds: 4));
    return [
      for (final m in asList(data))
        ProductVariant(
          str(m, 'id') ?? '',
          str(m, 'size') ?? '',
          str(m, 'color') ?? '',
          num0(m, 'quantity'),
          m['price'] == null ? null : num0(m, 'price'),
          m['is_active'] != false,
        ),
    ].where((v) => v.id.isNotEmpty).toList();
  }
}

class ProductVariant {
  final String id;
  final String size;
  final String color;
  final double quantity;
  final double? price;
  final bool active;
  const ProductVariant(this.id, this.size, this.color, this.quantity, this.price, this.active);
  String get label => [size, color].where((x) => x.isNotEmpty).join(', ');
}
