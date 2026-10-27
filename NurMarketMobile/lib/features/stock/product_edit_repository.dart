import 'package:drift/drift.dart' show Value;

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../sale/catalog_repository.dart';
import 'product_packages.dart';
import 'stock_repository.dart';

// 2026-10-22 (мобилка, админ): владелец — «в мобилке нельзя редактировать товары на складе». Карточка товара со склада:
// название, цены, единица, категория, штрихкоды, минимальный остаток. Остаток — НЕ правкой числа, а документом
// (приход / списание / ревизия с причиной), как ProductActions.ChangeStockAsync кассы: свежий остаток с сервера →
// итог → акт инвентаризации и сразу проведение. Так продажи других касс между чтением и записью не перетираются числом.

/// Поля карточки, которые меняет склад. Остатка здесь нет — он меняется только документом.
class ProductDraft {
  final String name;
  final String unit;
  final String category;
  final String barcode;

  /// Доп. штрихкоды в записи телефона: «код» или «код|кол-во|название» (упаковка), как Products.altBarcodes.
  final List<String> alt;
  final double price;
  final double purchase;
  final double wholesale;

  /// null — сервер минимум не отдал и его не вписали: не отправляем (как MinimumQuantity кассы).
  final double? minQty;

  /// 2026-10-27 (М4): поштучная продажа из упаковки (packages сервера); null — её нет.
  final PiecePack? pack;

  /// 2026-10-27 (М4): у товара несколько упаковок или это комплект — упаковки здесь не правятся и `packages_input` не уходит.
  final bool packLocked;

  const ProductDraft({
    required this.name,
    required this.unit,
    required this.category,
    required this.barcode,
    required this.alt,
    required this.price,
    required this.purchase,
    required this.wholesale,
    required this.minQty,
    this.pack,
    this.packLocked = false,
  });

  /// Карточка с сервера (GET products/{id}/); чего нет в ответе — из базы телефона.
  factory ProductDraft.fromServer(Map<String, dynamic> m, Product local) {
    final mapped = CatalogRepository.mapProduct(m);
    final altText = mapped?.altBarcodes.present == true ? mapped!.altBarcodes.value : local.altBarcodes;
    final rawMin = m['minimum_quantity'] ?? m['min_quantity'];
    return ProductDraft(
      name: (str(m, 'name') ?? local.name).trim(),
      unit: (mapped?.unit.present == true ? mapped!.unit.value : local.unit).trim(),
      category: mapped?.category.present == true ? mapped!.category.value : local.category,
      barcode: (str(m, 'barcode') ?? local.barcode ?? '').trim(),
      alt: altText.split('\n').where((s) => s.trim().isNotEmpty).toList(),
      price: m.containsKey('price') ? num0(m, 'price') : local.price,
      purchase: num0(m, 'purchase_price'),
      wholesale: m.containsKey('wholesale_price') ? num0(m, 'wholesale_price') : local.wholesalePrice,
      minQty: rawMin == null ? (local.minQuantity > 0 ? local.minQuantity : null) : num0(m, m.containsKey('minimum_quantity') ? 'minimum_quantity' : 'min_quantity'),
      // 2026-10-27 (М4): у комплекта (bundle) packages — состав набора, не поштучная продажа: не трогаем
      pack: str(m, 'kind') == 'bundle' ? null : PiecePack.first(m['packages']),
      packLocked: str(m, 'kind') == 'bundle' || PiecePack.parseAll(m['packages']).length > 1,
    );
  }

  ProductDraft copyWith({
    String? name,
    String? unit,
    String? category,
    String? barcode,
    List<String>? alt,
    double? price,
    double? purchase,
    double? wholesale,
    double? minQty,
    PiecePack? pack,
    bool clearPack = false,
  }) => ProductDraft(
    name: name ?? this.name,
    unit: unit ?? this.unit,
    category: category ?? this.category,
    barcode: barcode ?? this.barcode,
    alt: alt ?? this.alt,
    price: price ?? this.price,
    purchase: purchase ?? this.purchase,
    wholesale: wholesale ?? this.wholesale,
    minQty: minQty ?? this.minQty,
    pack: clearPack ? null : (pack ?? this.pack),
    packLocked: packLocked,
  );

  static String altCode(String line) => line.split('|').first.trim();

  /// Все штрихкоды карточки (основной + доп.) — для проверки дублей.
  List<String> get allCodes => [if (barcode.trim().isNotEmpty) barcode.trim(), ...alt.map(altCode).where((c) => c.isNotEmpty)];
}

bool _moneyDiff(double a, double b) => (a - b).abs() > 0.004;

bool isWeightUnit(String unit) => const {'кг', 'kg', 'г', 'g'}.contains(unit.trim().toLowerCase());

/// Тело PATCH api/main/products/{id}/ — только изменённые поля.
/// Цены: на сервере NurCRM PATCH purchase_price пересчитывает цену продажи через markup_percent (ТЗ 30.7: закупка 11 →
/// цена 165 вместо 15). Поэтому при правке любой из двух цен уходят обе и наценка, посчитанная из них, — как
/// BuildProductBody кассы; ответ всё равно проверяется (см. [ProductEditRepository.save]).
Map<String, dynamic> productPatchBody(ProductDraft before, ProductDraft after, {required bool canPrices}) {
  final body = <String, dynamic>{};
  if (after.name.trim() != before.name.trim()) body['name'] = after.name.trim();
  if (after.unit.trim() != before.unit.trim()) {
    body['unit'] = after.unit.trim();
    // «кг» делает товар весовым, как ProductEditDialog кассы (is_weight уходит вместе с единицей).
    body['is_weight'] = isWeightUnit(after.unit);
  }
  if (after.category.trim() != before.category.trim()) body['category_name'] = after.category.trim();
  if (after.barcode.trim() != before.barcode.trim()) body['barcode'] = after.barcode.trim().isEmpty ? null : after.barcode.trim();
  if (after.alt.join('\n') != before.alt.join('\n')) {
    body['alternate_barcodes'] = [
      for (final line in after.alt)
        if (ProductDraft.altCode(line).isNotEmpty)
          {
            'barcode': ProductDraft.altCode(line),
            'name': line.split('|').length > 2 ? line.split('|').sublist(2).join('|') : '',
            'quantity': line.split('|').length > 1 ? (num.tryParse(line.split('|')[1]) ?? 0) : 0,
          },
    ];
  }
  if (canPrices) {
    if (_moneyDiff(after.price, before.price) || _moneyDiff(after.purchase, before.purchase)) {
      body['price'] = after.price.toStringAsFixed(2);
      body['purchase_price'] = after.purchase.toStringAsFixed(2);
      body['markup_percent'] = (after.purchase > 0 ? (after.price - after.purchase) / after.purchase * 100 : 0).toStringAsFixed(2);
    }
    if (_moneyDiff(after.wholesale, before.wholesale)) body['wholesale_price'] = after.wholesale.toStringAsFixed(2);
  }
  final m = after.minQty;
  if (m != null && (before.minQty == null || (m - before.minQty!).abs() > 0.0005)) body['minimum_quantity'] = _qty(m);
  // 2026-10-27 (М4): поштучная продажа — packages_input полной заменой, как BuildProductBody кассы; пустой список — снять.
  // Не шлём, если упаковка не менялась, у товара несколько упаковок/комплект, или товар весовой (поштучно нельзя).
  if (!before.packLocked && !after.packLocked && !isWeightUnit(after.unit)) {
    final a = after.pack, b = before.pack;
    final changed = (a == null) != (b == null) || (a != null && !a.sameAs(b));
    if (changed) body['packages_input'] = a == null ? <Object>[] : [a.toInput(after.unit.trim())];
  }
  return body;
}

String _qty(double v) {
  final s = v.toStringAsFixed(3);
  return s.contains('.') ? s.replaceFirst(RegExp(r'\.?0+$'), '') : s;
}

class ProductSaveResult {
  final bool changed;

  /// Сервер пересчитал цену продажи по наценке — цена возвращена вторым PATCH.
  final bool priceRestored;
  final Map<String, dynamic>? server;
  const ProductSaveResult({required this.changed, this.priceRestored = false, this.server});
}

/// Какой документ меняет остаток.
enum StockDoc { income, writeOff, recount }

class StockChangeResult {
  final double from;
  final double to;

  /// false — акт создан, проведение уйдёт при связи (очередь, как ревизия кассира).
  final bool applied;
  final bool unchanged;
  const StockChangeResult({required this.from, required this.to, required this.applied, this.unchanged = false});
}

/// Списать больше остатка нельзя (как ChangeStockAsync кассы).
class StockBelowZeroException extends ApiException {
  final double current;
  StockBelowZeroException(this.current) : super('below zero', status: 400);
  @override
  bool get isTransient => false;
}

class ProductEditRepository {
  final ApiClient api;
  final AppDatabase db;
  final StockRepository stock;
  ProductEditRepository(this.api, this.db, this.stock);

  /// Двойное нажатие «Сохранить» / «Провести»: второй вызов получает тот же результат, второго запроса нет.
  final _inFlight = <String, Future<Object?>>{};

  Future<T> _once<T>(String key, Future<T> Function() run) {
    final busy = _inFlight[key];
    if (busy != null) return busy.then((v) => v as T);
    final f = run();
    _inFlight[key] = f;
    // Блок, а не «=> remove»: remove вернул бы сам f, и whenComplete ждал бы его ошибку — она ушла бы необработанной.
    f.then((_) {}, onError: (_) {}).whenComplete(() {
      _inFlight.remove(key);
    });
    return f;
  }

  Future<Map<String, dynamic>> load(String id) async => ((await api.get('api/main/products/$id/')) as Map).cast<String, dynamic>();

  /// Штрихкод уже у другого товара в базе телефона — его название (null — свободен).
  Future<String?> barcodeOwner(String productId, String code) async {
    final p = await db.productByBarcode(code);
    return p != null && p.id != productId ? p.name : null;
  }

  Future<ProductSaveResult> save(String id, ProductDraft before, ProductDraft after, {required bool canPrices}) => _once('save:$id', () async {
    final body = productPatchBody(before, after, canPrices: canPrices);
    if (body.isEmpty) return const ProductSaveResult(changed: false);
    final resp = await api.patch('api/main/products/$id/', body: body);
    var server = resp is Map ? resp.cast<String, dynamic>() : null;
    var restored = false;
    if (body.containsKey('price')) {
      // Ответ без цены — проверяем карточкой.
      if (server == null || !server.containsKey('price')) {
        try {
          server = await load(id);
        } on ApiException {
          server = null;
        }
      }
      if (server != null && _moneyDiff(num0(server, 'price'), after.price)) {
        // Сервер пересчитал цену продажи от закупки и наценки — возвращаем введённую (как касса, ТЗ 30.7).
        final fix = await api.patch('api/main/products/$id/', body: {'price': after.price.toStringAsFixed(2)});
        restored = true;
        server = fix is Map && fix.containsKey('price') ? fix.cast<String, dynamic>() : {...server, 'price': after.price};
      }
    }
    // 2026-10-27 (М4): упаковка записана — в базу телефона кладём то, что вернул сервер (id упаковки нужен для продажи штуки);
    // ответ без packages дополняем карточкой.
    if (body.containsKey('packages_input')) {
      var full = server;
      if (full == null || !full.containsKey('packages')) {
        try {
          full = await load(id);
        } on ApiException {
          full = null;
        }
      }
      await PackageStore.put(db, id, full != null && full.containsKey('packages') ? PiecePack.first(full['packages']) : after.pack);
    }
    await _saveLocal(id, server, after, canPrices: canPrices);
    return ProductSaveResult(changed: true, priceRestored: restored, server: server);
  });

  /// База телефона: карточка сервера, но остаток — телефонный (в нём ещё не отправленные продажи и приходы).
  Future<void> _saveLocal(String id, Map<String, dynamic>? server, ProductDraft after, {required bool canPrices}) async {
    final local = await (db.select(db.products)..where((p) => p.id.equals(id))).getSingleOrNull();
    final mapped = server == null ? null : CatalogRepository.mapProduct(server);
    if (mapped != null && server!['name'] != null) {
      // Минимум: в ответе сервера есть — берём его, иначе вписанный в карточке.
      final hasMin = server.containsKey('minimum_quantity') || server.containsKey('min_quantity');
      await db.upsertProduct(
        mapped.copyWith(
          quantity: Value(local?.quantity ?? mapped.quantity.value),
          isFavorite: Value(local?.isFavorite ?? false),
          minQuantity: hasMin ? mapped.minQuantity : Value(after.minQty ?? local?.minQuantity ?? 0),
        ),
      );
      return;
    }
    if (local == null) return;
    await (db.update(db.products)..where((p) => p.id.equals(id))).write(
      ProductsCompanion(
        name: Value(after.name.trim()),
        unit: Value(after.unit.trim()),
        isWeight: Value(isWeightUnit(after.unit)),
        category: Value(after.category.trim()),
        barcode: Value(after.barcode.trim().isEmpty ? null : after.barcode.trim()),
        altBarcodes: Value(after.alt.join('\n')),
        minQuantity: Value(after.minQty ?? local.minQuantity),
        price: canPrices ? Value(after.price) : const Value.absent(),
        wholesalePrice: canPrices ? Value(after.wholesale) : const Value.absent(),
      ),
    );
  }

  /// Остаток документом: свежий остаток с сервера (без кеша) → итог → акт ревизии с причиной и проведение.
  /// Без связи остаток не читается — ничего не меняется (как касса).
  Future<StockChangeResult> changeStock(String id, StockDoc doc, double qty, String note) => _once('stock:$id', () async {
    final fresh = await load(id);
    final current = num0(fresh, 'quantity');
    final raw = switch (doc) {
      StockDoc.income => current + qty,
      StockDoc.writeOff => current - qty,
      StockDoc.recount => qty,
    };
    final target = (raw * 1000).round() / 1000;
    if (target < 0) throw StockBelowZeroException(current);
    if ((target - current).abs() < 0.0005) {
      await db.setStock(id, current);
      return StockChangeResult(from: current, to: current, applied: true, unchanged: true);
    }
    final applied = await stock.applyAudit(note, {id: target});
    return StockChangeResult(from: current, to: target, applied: applied);
  });
}
