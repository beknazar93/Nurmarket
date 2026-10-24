import 'package:drift/drift.dart';
import 'package:drift_flutter/drift_flutter.dart';

import '../../features/scan/barcode_lookup.dart';

part 'database.g.dart';

/// Каталог и остатки — чтобы продавать без интернета (ТЗ, раздел 8).
class Products extends Table {
  TextColumn get id => text()();
  TextColumn get name => text()();
  RealColumn get price => real().withDefault(const Constant(0))();

  /// 13.10, владелец: «продажа оптом и в розницу» — оптовая цена товара (wholesale_price сервера); 0 — нет опта.
  RealColumn get wholesalePrice => real().withDefault(const Constant(0))();
  TextColumn get barcode => text().nullable()();

  /// Доп. штрихкоды через перевод строки.
  TextColumn get altBarcodes => text().withDefault(const Constant(''))();
  TextColumn get sku => text().nullable()();

  /// 13.10: PLU весов — по нему касса находит товар весовой этикетки (2X…).
  TextColumn get plu => text().nullable()();
  TextColumn get unit => text().withDefault(const Constant('шт'))();
  BoolColumn get isWeight => boolean().withDefault(const Constant(false))();
  BoolColumn get isService => boolean().withDefault(const Constant(false))();
  BoolColumn get isFavorite => boolean().withDefault(const Constant(false))();
  RealColumn get quantity => real().withDefault(const Constant(0))();
  TextColumn get category => text().withDefault(const Constant(''))();
  TextColumn get imageUrl => text().nullable()();

  /// 13.10, владелец: «в мобилке тормозит и не видит все данные». Название, артикул, штрихкод и категория в нижнем
  /// регистре — для поиска: lower() SQLite меняет только латиницу, и «мак» не находил «Макфа».
  TextColumn get searchKey => text().withDefault(const Constant(''))();

  /// 18.10: акция товара (promotion_rules сервера) — JSON-массив ступеней; пусто — акции нет. Скидку по акции сервер
  /// считает сам и ручную скидку строки у такого товара не принимает — телефон считает так же (cart.dart, promoDiscount).
  TextColumn get promoRules => text().withDefault(const Constant(''))();

  /// 2026-10-22 (мобилка, админ): минимальный остаток товара (minimum_quantity сервера). 0 — не задан: «заканчивается»
  /// по общему порогу < 10, как на ПК. Задан — товар заканчивается, когда остаток дошёл до минимума.
  RealColumn get minQuantity => real().withDefault(const Constant(0))();

  @override
  Set<Column> get primaryKey => {id};
}

/// Очередь: продажа, приёмка, ревизия. id — ключ идемпотентности: повтор не создаёт дубль на сервере.
class Outbox extends Table {
  TextColumn get id => text()();

  /// sale | receive | audit
  TextColumn get kind => text()();
  TextColumn get body => text()();
  DateTimeColumn get createdAt => dateTime()();

  /// pending | sent | failed
  TextColumn get status => text().withDefault(const Constant('pending'))();
  IntColumn get attempts => integer().withDefault(const Constant(0))();
  TextColumn get lastError => text().nullable()();
  RealColumn get total => real().withDefault(const Constant(0))();

  /// Короткое описание для списка чеков: «Mars, Кока-Кола…».
  TextColumn get summary => text().withDefault(const Constant(''))();
  TextColumn get paymentMethod => text().withDefault(const Constant(''))();
  TextColumn get serverId => text().nullable()();
  TextColumn get serverNumber => text().nullable()();
  TextColumn get shiftId => text().nullable()();

  /// Состав для показа на телефоне: [[название, кол-во, цена, сумма], …] — серверу не отправляется.
  TextColumn get display => text().withDefault(const Constant('[]'))();

  @override
  Set<Column> get primaryKey => {id};
}

class Settings extends Table {
  TextColumn get key => text()();
  TextColumn get value => text()();

  @override
  Set<Column> get primaryKey => {key};
}

@DriftDatabase(tables: [Products, Outbox, Settings])
class AppDatabase extends _$AppDatabase {
  AppDatabase([QueryExecutor? executor]) : super(executor ?? driftDatabase(name: 'nurmarket'));

  @override
  int get schemaVersion => 6;

  @override
  MigrationStrategy get migration => MigrationStrategy(
    // 2026-10-22 (скорость скана): индексы по штрихкоду, артикулу и PLU — раньше каждый скан читал все товары подряд
    // (на 50 000 товаров — десятки миллисекунд на каждое написание кода). IF NOT EXISTS: и новая база, и старая.
    beforeOpen: (details) async {
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_barcode ON products(barcode)');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_sku ON products(sku)');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_plu_int ON products(CAST(plu AS INTEGER))');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_sku_int ON products(CAST(sku AS INTEGER))');
    },
    onUpgrade: (m, from, to) async {
      // 13.10: PLU для весовых этикеток; каталог заполнит его при ближайшей синхронизации.
      if (from < 2) await m.addColumn(products, products.plu);
      // 13.10: оптовая цена — для «Розница / Опт» в продаже.
      if (from < 3) await m.addColumn(products, products.wholesalePrice);
      // 13.10: поле поиска; до ближайшей синхронизации каталога — из названия как есть.
      if (from < 4) {
        await m.addColumn(products, products.searchKey);
        await customStatement("UPDATE products SET search_key = lower(name || ' ' || coalesce(sku, '') || ' ' || coalesce(barcode, ''))");
      }
      // 18.10: акции товаров; каталог заполнит при ближайшей синхронизации.
      if (from < 5) await m.addColumn(products, products.promoRules);
      // 2026-10-22 (мобилка, админ): минимальный остаток товара; у старых товаров 0 (общий порог), каталог заполнит.
      if (from < 6) await m.addColumn(products, products.minQuantity);
    },
  );

  // ------------------------------------------------------------------ каталог

  Future<void> replaceProducts(List<ProductsCompanion> rows) => transaction(() async {
    await delete(products).go();
    await batch((b) => b.insertAll(products, rows));
  });

  Future<int> productCount() async => (await (selectOnly(products)..addColumns([products.id.count()])).getSingle()).read(products.id.count()) ?? 0;

  /// Поиск по названию, штрихкоду и артикулу.
  Future<List<Product>> searchProducts(String query, {String? category, bool favorites = false, int limit = 200}) {
    final q = query.trim().toLowerCase();
    final s = select(products);
    s.where((p) {
      Expression<bool> e = const Constant(true);
      if (q.isNotEmpty) {
        // каждое слово запроса — в любом месте названия («кола 2л» найдёт «Кока-Кола (ПЭТ) 2л»)
        for (final w in q.split(RegExp(r'\s+')).where((w) => w.isNotEmpty)) {
          e = e & p.searchKey.like('%$w%');
        }
        e = e | p.barcode.equals(query.trim()) | p.altBarcodes.like('%${query.trim()}%');
      }
      if (category != null && category.isNotEmpty) e = e & p.category.equals(category);
      if (favorites) e = e & p.isFavorite.equals(true);
      return e;
    });
    s.orderBy([(p) => OrderingTerm(expression: p.searchKey)]);
    s.limit(limit);
    return s.get();
  }

  /// 13.10: товар по скану: штрихкод в любом написании (UPC-A ↔ EAN-13), потом весовая этикетка (PLU или артикул).
  /// [qty] — вес в кг или количество по сумме этикетки; null — обычный штучный скан.
  /// 2026-10-22 (аудит П7, как ProductCodeLookup.cs ПК): очистка кода, GTIN-14, UPC-E, русская раскладка; весовая этикетка
  /// по правилам компании (префикс 21 у Rongta — сумма), в «Одежде» — только с явным префиксом; [allowWeightLabel] = false
  /// — приёмка (этикетка весов — вес одной покупки, а не товар поставки). Этикетка ищется одним запросом по коду,
  /// а не чтением всех товаров с PLU.
  Future<({Product product, double? qty})?> productByScan(String code, {bool allowWeightLabel = true}) async {
    final variants = barcodeVariants(code);
    // 2026-10-22 (скорость скана): все написания кода — двумя запросами по индексу, а не по два запроса на каждое написание
    final byVariant = await productsByBarcodes(variants);
    for (final v in variants) {
      final p = byVariant[v];
      if (p != null) return (product: p, qty: packQuantity(p, v));
    }
    if (!allowWeightLabel || variants.isEmpty) return null;
    final label = parseWeightLabel(variants.first, clothing: (await getSetting('shop.clothing')) == '1');
    if (label == null) return null;
    for (final c in label.candidates) {
      final n = int.tryParse(c.code) ?? 0;
      if (n <= 0) continue;
      final rows = await customSelect(
        'SELECT * FROM products WHERE (plu IS NOT NULL AND CAST(plu AS INTEGER) = ?1) OR (sku IS NOT NULL AND CAST(sku AS INTEGER) = ?1) LIMIT 20',
        variables: [Variable.withInt(n)],
        readsFrom: {products},
      ).map((r) => products.map(r.data)).get();
      final p = rows.where((r) => sameShortCode(r.plu, c.code)).firstOrNull ?? rows.where((r) => sameShortCode(r.sku, c.code)).firstOrNull;
      if (p == null) continue;
      final qty = label.isAmount ? (p.price > 0 ? c.value / p.price : null) : c.value;
      if (qty == null) continue;
      return (product: p, qty: double.parse(qty.toStringAsFixed(3)));
    }
    return null;
  }

  /// 2026-10-22 (скорость скана): товары по нескольким написаниям кода. Первое написание (то, что прочла камера) почти
  /// всегда находится одним запросом по индексу; иначе — точные совпадения и доп. штрихкоды всех написаний параллельно.
  /// Порядок, как раньше: по написаниям, у каждого — сначала основной код/артикул, потом доп. штрихкоды.
  Future<Map<String, Product>> productsByBarcodes(List<String> codes) async {
    final cs = [for (final c in codes) if (c.trim().isNotEmpty) c.trim()];
    if (cs.isEmpty) return const {};
    final first = await (select(products)..where((p) => p.barcode.equals(cs.first) | p.sku.equals(cs.first))..limit(1)).getSingleOrNull();
    if (first != null) return {cs.first: first};
    final rest = cs.skip(1).toList();
    final exactF = rest.isEmpty
        ? Future.value(<Product>[])
        : (select(products)..where((p) => p.barcode.isIn(rest) | p.sku.isIn(rest))).get();
    final altF = customSelect(
      "SELECT * FROM products WHERE alt_barcodes != '' AND (${List.generate(cs.length, (i) => 'instr(alt_barcodes, ?${i + 1}) > 0').join(' OR ')})",
      variables: [for (final c in cs) Variable.withString(c)],
      readsFrom: {products},
    ).map((r) => products.map(r.data)).get();
    final exact = await exactF, alt = await altF;
    final out = <String, Product>{};
    for (final c in cs) {
      final e = exact.where((p) => p.barcode == c || p.sku == c).firstOrNull;
      if (e != null) {
        out[c] = e;
        continue;
      }
      final a = alt.where((p) => p.altBarcodes.split('\n').any((line) => line.split('|').first == c)).firstOrNull;
      if (a != null) out[c] = a;
    }
    return out;
  }

  Future<Product?> productByBarcode(String code) async {
    final c = code.trim();
    if (c.isEmpty) return null;
    final exact =
        await (select(products)
              ..where((p) => p.barcode.equals(c) | p.sku.equals(c))
              ..limit(1))
            .getSingleOrNull();
    if (exact != null) return exact;
    // 2026-10-22: instr вместо LIKE — «_» и «%» в коде Code128 больше не работают как шаблон
    final alt = await customSelect(
      'SELECT * FROM products WHERE instr(alt_barcodes, ?1) > 0',
      variables: [Variable.withString(c)],
      readsFrom: {products},
    ).map((r) => products.map(r.data)).get();
    for (final p in alt) {
      if (p.altBarcodes.split('\n').any((line) => line.split('|').first == c)) return p;
    }
    return null;
  }

  /// 2026-10-22 (аудит С-4): товар, найденный на сервере по штрихкоду, — в базу телефона сразу (не ждать каталога).
  Future<void> upsertProduct(ProductsCompanion row) => into(products).insertOnConflictUpdate(row);

  /// 13.10: штрихкод упаковки (доп. штрихкод с количеством) — сколько штук добавить; null — обычный штрихкод.
  static double? packQuantity(Product p, String code) {
    for (final line in p.altBarcodes.split('\n')) {
      final parts = line.split('|');
      if (parts.first == code.trim() && parts.length > 1) {
        final q = double.tryParse(parts[1]) ?? 0;
        if (q > 1) return q;
      }
    }
    return null;
  }

  /// 13.10: товар удалён на сервере — убираем и с телефона, не дожидаясь синхронизации каталога.
  Future<void> deleteProduct(String id) => (delete(products)..where((p) => p.id.equals(id))).go();

  Future<Product?> productById(String id) => (select(products)..where((p) => p.id.equals(id))).getSingleOrNull();

  Future<List<String>> categories() async {
    final rows = await (selectOnly(products, distinct: true)..addColumns([products.category])).get();
    return rows.map((r) => r.read(products.category) ?? '').where((c) => c.isNotEmpty).toList()..sort();
  }

  /// Остаток после продажи или приёмки — сразу, не дожидаясь синхронизации каталога.
  Future<void> adjustStock(String productId, double delta) => customUpdate(
    'UPDATE products SET quantity = quantity + ? WHERE id = ?',
    variables: [Variable.withReal(delta), Variable.withString(productId)],
    updates: {products},
  );

  Future<void> setStock(String productId, double quantity) =>
      (update(products)..where((p) => p.id.equals(productId))).write(ProductsCompanion(quantity: Value(quantity)));

  // ------------------------------------------------------------------ очередь

  Future<void> enqueue(OutboxCompanion row) => into(outbox).insertOnConflictUpdate(row);

  Future<List<OutboxData>> pending() =>
      (select(outbox)
            ..where((o) => o.status.equals('pending'))
            ..orderBy([(o) => OrderingTerm(expression: o.createdAt)]))
          .get();

  Stream<int> pendingCount() {
    final c = outbox.id.count();
    return (selectOnly(outbox)
          ..addColumns([c])
          ..where(outbox.status.equals('pending')))
        .map((r) => r.read(c) ?? 0)
        .watchSingle();
  }

  Stream<List<OutboxData>> watchOutbox({String? kind}) {
    final s = select(outbox)..orderBy([(o) => OrderingTerm(expression: o.createdAt, mode: OrderingMode.desc)]);
    if (kind != null) s.where((o) => o.kind.equals(kind));
    return s.watch();
  }

  Future<void> markSent(String id, {String? serverId, String? serverNumber}) => (update(outbox)..where((o) => o.id.equals(id))).write(
    OutboxCompanion(status: const Value('sent'), serverId: Value(serverId), serverNumber: Value(serverNumber), lastError: const Value(null)),
  );

  Future<void> markAttempt(String id, String error, {bool failed = false}) => customUpdate(
    'UPDATE outbox SET attempts = attempts + 1, last_error = ?, status = ? WHERE id = ?',
    variables: [Variable.withString(error), Variable.withString(failed ? 'failed' : 'pending'), Variable.withString(id)],
    updates: {outbox},
  );

  Future<void> retryFailed(String id) => (update(outbox)..where((o) => o.id.equals(id))).write(const OutboxCompanion(status: Value('pending')));

  // ------------------------------------------------------------------ настройки

  Future<String?> getSetting(String key) async => (await (select(settings)..where((s) => s.key.equals(key))).getSingleOrNull())?.value;

  Future<void> setSetting(String key, String value) => into(settings).insertOnConflictUpdate(SettingsCompanion.insert(key: key, value: value));
}
