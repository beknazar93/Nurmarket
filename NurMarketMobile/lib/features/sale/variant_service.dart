import 'dart:async';
import 'dart:convert';

import 'package:drift/drift.dart' show Value;

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/sync/outbox_meta.dart';
import 'catalog_repository.dart';

/// Что известно о вариантах (размер/цвет) товара.
class VariantSet {
  /// Все варианты товара (и неактивные — отбор активных делает экран). Пусто: вариантов нет (если [unknown] = false).
  final List<ProductVariant> variants;

  /// Вариантов товара узнать не удалось: на телефоне о нём ничего нет, а сервер не ответил.
  final bool unknown;

  /// Данные взяты с телефона и уже обновляются с сервера: результат придёт сюда (null — обновлять нечего).
  final Future<List<ProductVariant>?>? refresh;
  const VariantSet(this.variants, {this.unknown = false, this.refresh});

  bool get has => variants.isNotEmpty;
  List<ProductVariant> get active => variants.where((v) => v.active).toList();
}

/// 2026-10-31 (владелец: «почему продажа одежды в мобилке нету?»): варианты товара на телефоне. Сервер не отдаёт в списке товаров
/// признака «есть размеры» (в карточке нет ни has_variants, ни variants), поэтому, как касса (ProductVariantCache), телефон
/// спрашивает api/main/products/{id}/variants/ у товара, который открыли, и запоминает ответ в базе:
/// • с вариантами — показывается сразу из базы, а если ответ старше [freshFor], обновляется с сервера в фоне;
/// • без вариантов — повторно спрашивается не раньше, чем через [noVariantsFor];
/// • сервер не ответил, а о товаре ничего нет — [VariantSet.unknown] (экран решает, продавать ли без размера).
/// Остаток размера после продажи (в том числе без связи) уменьшается на телефоне сразу и поверх свежих данных сервера
/// возвращается заново (неотправленные чеки очереди).
class VariantService {
  final ApiClient api;
  final AppDatabase db;
  VariantService(this.api, this.db);

  static const freshFor = Duration(seconds: 30);
  static const noVariantsFor = Duration(hours: 24);
  static const loadTimeout = Duration(seconds: 4);

  final Map<String, Future<List<ProductVariant>?>> _inFlight = {};

  static ProductVariant fromRow(VariantRow r) => ProductVariant(r.id, r.size, r.color, r.quantity, r.price, r.isActive, barcode: r.barcode, productId: r.productId);

  static ProductVariantsCompanion toRow(String productId, ProductVariant v) => ProductVariantsCompanion.insert(
    id: v.id,
    productId: productId,
    size: Value(v.size),
    color: Value(v.color),
    barcode: Value(v.barcode),
    quantity: Value(v.quantity),
    price: Value(v.price),
    isActive: Value(v.active),
  );

  /// Варианты из ответа сервера (список или {results: …}). Строки без id пропускаются.
  static List<ProductVariant> parse(dynamic data, {String? productId}) {
    return [
      for (final m in asList(data))
        if ((str(m, 'id') ?? '').isNotEmpty)
          ProductVariant(
            str(m, 'id')!,
            (str(m, 'size') ?? '').trim(),
            (str(m, 'color') ?? '').trim(),
            num0(m, 'quantity'),
            m['price'] == null || '${m['price']}'.isEmpty ? null : num0(m, 'price'),
            m['is_active'] != false,
            barcode: (str(m, 'barcode') ?? '').trim().isEmpty ? null : str(m, 'barcode')!.trim(),
            productId: str(m, 'product') ?? productId,
          ),
    ];
  }

  /// Что известно о вариантах товара; при необходимости ходит на сервер (не дольше [timeout]).
  Future<VariantSet> forProduct(String productId, {Duration timeout = loadTimeout, bool allowServer = true}) async {
    final check = await db.variantCheck(productId);
    final rows = await db.variantsOf(productId);
    final cached = [for (final r in rows) fromRow(r)];
    if (check != null) {
      final age = DateTime.now().difference(DateTime.fromMillisecondsSinceEpoch(check.checkedAt));
      if (cached.isNotEmpty) {
        return VariantSet(cached, refresh: allowServer && age > freshFor ? _load(productId, timeout: const Duration(seconds: 8)) : null);
      }
      if (age < noVariantsFor || !allowServer) return const VariantSet([]);
      final fresh = await _load(productId, timeout: timeout);
      return fresh == null ? const VariantSet([]) : VariantSet(fresh);
    }
    if (cached.isNotEmpty) return VariantSet(cached, refresh: allowServer ? _load(productId, timeout: const Duration(seconds: 8)) : null);
    if (!allowServer) return const VariantSet([], unknown: true);
    final fresh = await _load(productId, timeout: timeout);
    return fresh == null ? const VariantSet([], unknown: true) : VariantSet(fresh);
  }

  /// Загрузить варианты товара с сервера и записать в базу. null — сервер не ответил. Один запрос на товар одновременно.
  Future<List<ProductVariant>?> _load(String productId, {Duration timeout = loadTimeout}) {
    final running = _inFlight[productId];
    if (running != null) return running;
    final f = _loadCore(productId, timeout);
    _inFlight[productId] = f;
    unawaited(f.whenComplete(() => _inFlight.remove(productId)));
    return f;
  }

  Future<List<ProductVariant>?> _loadCore(String productId, Duration timeout) async {
    try {
      final data = await api.get('api/main/products/$productId/variants/', timeout: timeout).timeout(timeout + const Duration(milliseconds: 500));
      final list = parse(data, productId: productId);
      await db.replaceVariants(productId, [for (final v in list) toRow(productId, v)]);
      await _reapplyPending({for (final v in list) v.id});
      return [for (final r in await db.variantsOf(productId)) fromRow(r)];
    } catch (_) {
      return null;
    }
  }

  /// Принудительно обновить варианты товара (после приёмки, правки). null — без связи.
  Future<List<ProductVariant>?> reload(String productId) => _load(productId, timeout: const Duration(seconds: 8));

  /// Сервер уже убавил остаток размеров на время, пока чек лежал в очереди? Нет — свежие данные не знают о неотправленных чеках:
  /// вычитаем их заново (как CatalogRepository._reapplyPending для товаров).
  Future<void> _reapplyPending(Set<String> variantIds) async {
    if (variantIds.isEmpty) return;
    final me = OutboxMeta.ownerOf(api.session);
    final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
    for (final row in await db.pending()) {
      if (row.kind != 'sale') continue;
      if (me.isNotEmpty && !OutboxMeta.belongsTo(row, me, legacyOwner: legacy)) continue;
      try {
        final body = (jsonDecode(row.body) as Map).cast<String, dynamic>();
        for (final it in asList(body['items'])) {
          final vid = str(it, 'variant');
          final q = num0(it, 'qty');
          if (vid != null && q > 0 && variantIds.contains(vid)) await db.adjustVariantStock(vid, -q);
        }
      } catch (_) {
        // повреждённая запись — без поправки
      }
    }
  }

  /// Штрихкод размера, которого нет на телефоне: сервер `GET products/barcode/{код}/` (с 31.10.2026 отдаёт товар и поле `variant`
  /// размера, найденного по его штрихкоду). Найденный товар и размер записываются в базу, остальные размеры товара подтягиваются в
  /// фоне. Возвращает размер (его товар уже в базе) или null. Исключение сети не бросает: [unavailable] = true.
  Future<({VariantRow? variant, bool unavailable})> variantFromServer(String code, {Duration timeout = const Duration(seconds: 2)}) async {
    try {
      final data = await api.get('api/main/products/barcode/${Uri.encodeComponent(code)}/', timeout: timeout).timeout(timeout + const Duration(milliseconds: 500));
      if (data is! Map) return (variant: null, unavailable: false);
      final m = data.cast<String, dynamic>();
      final v = m['variant'];
      if (v is! Map) return (variant: null, unavailable: false);
      final vm = v.cast<String, dynamic>();
      final productId = str(vm, 'product') ?? str(m, 'id');
      final vid = str(vm, 'id');
      if (productId == null || vid == null) return (variant: null, unavailable: false);
      final row = CatalogRepository.mapProduct(m);
      if (row != null) await db.upsertProduct(row);
      final parsed = parse([vm], productId: productId);
      if (parsed.isEmpty) return (variant: null, unavailable: false);
      await db.upsertVariant(toRow(productId, parsed.first));
      unawaited(_load(productId, timeout: const Duration(seconds: 8)));
      return (variant: await db.variantById(vid), unavailable: false);
    } on ApiException catch (e) {
      return (variant: null, unavailable: e.status == null);
    } catch (_) {
      return (variant: null, unavailable: true);
    }
  }
}

/// 2026-10-31 (одежда): название строки продажи с сервера вместе с размером и цветом: «Футболка · M · Красный».
/// Сервер отдаёт их отдельными полями variant_size / variant_color (чек, история, возврат); название, где размер уже есть
/// (строка, проведённая с телефона: «Футболка · M · Красный»), второй раз не дополняется.
String saleItemName(Map<String, dynamic> m) {
  final name = str(m, 'product_name') ?? str(m, 'name') ?? str(m, 'title') ?? '';
  final size = (str(m, 'variant_size') ?? '').trim();
  final color = (str(m, 'variant_color') ?? '').trim();
  final tail = [size, color].where((x) => x.isNotEmpty).join(' · ');
  return tail.isEmpty || name.endsWith(tail) ? name : '$name · $tail';
}
