import 'package:drift/drift.dart';
import 'package:drift_flutter/drift_flutter.dart';

import '../../features/scan/barcode_lookup.dart';
import '../catalog/search_match.dart';

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

  /// 2026-10-30 (ревизия: потери в деньгах, как на кассе): закупочная цена товара (purchase_price сервера). null — неизвестна
  /// (сервер не прислал или у товара не задана): в ревизии сумма разницы тогда считается по цене продажи с пометкой «*».
  /// Показывается только владельцу и администратору (Session.canSeePurchase).
  RealColumn get purchasePrice => real().nullable()();

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
  int get schemaVersion => 7;

  @override
  MigrationStrategy get migration => MigrationStrategy(
    // 2026-10-22 (скорость скана): индексы по штрихкоду, артикулу и PLU — раньше каждый скан читал все товары подряд
    // (на 50 000 товаров — десятки миллисекунд на каждое написание кода). IF NOT EXISTS: и новая база, и старая.
    beforeOpen: (details) async {
      // 2026-10-29 (аудит, п. 3): фоновая отправка очереди открывает ту же базу из другого изолята — занятая база ждёт, а не падает
      await customStatement('PRAGMA busy_timeout = 5000');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_barcode ON products(barcode)');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_sku ON products(sku)');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_plu_int ON products(CAST(plu AS INTEGER))');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_sku_int ON products(CAST(sku AS INTEGER))');
      // 2026-10-30 (оптимизация, п. 1–3): счётчик ждущих, список очереди и чистка выбирают по статусу — без индекса это полный перебор очереди
      try {
        await customStatement('CREATE INDEX IF NOT EXISTS ix_outbox_status ON outbox(status, created_at)');
      } catch (_) {
        // таблицы очереди нет (очень старая/урезанная база) — без индекса запросы только медленнее
      }
      // 2026-10-29 (аудит, п. 10): ключ поиска хранится нормализованным (ё→е, ү→у …, как SearchMatch кассы). Каталог теперь
      // обновляется по ленте и целиком качается редко, поэтому ключи уже лежащих товаров приводим к новому виду один раз
      // здесь (SQL replace — те же замены, что SearchMatch.normalize; схема базы не меняется).
      if (!details.wasCreated) {
        try {
          final done = await customSelect("SELECT value FROM settings WHERE key = 'search.keyV'").get();
          if (done.isEmpty) {
            var expr = 'search_key';
            for (final e in const {'ё': 'е', 'ң': 'н', 'ө': 'о', 'ү': 'у', 'ұ': 'у', 'қ': 'к', 'ғ': 'г', 'һ': 'х'}.entries) {
              expr = "replace($expr, '${e.key}', '${e.value}')";
            }
            await customStatement('UPDATE products SET search_key = $expr');
            await customStatement("INSERT OR REPLACE INTO settings(key, value) VALUES('search.keyV', '2')");
          }
        } catch (_) {
          // не вышло — поиск работает по старым ключам, их обновит полная загрузка каталога
        }
      }
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
      // 2026-10-30 (ревизия в деньгах): закупка товара; у старых товаров null (неизвестна), каталог заполнит при ближайшей загрузке.
      if (from < 7) await m.addColumn(products, products.purchasePrice);
    },
  );

  // ------------------------------------------------------------------ каталог

  Future<void> replaceProducts(List<ProductsCompanion> rows) => transaction(() async {
    await delete(products).go();
    await batch((b) => b.insertAll(products, rows));
  });

  /// 2026-10-29 (аудит, п. 2): применить страницу ленты изменений — новые товары вставить, известные обновить (в [updates]
  /// только присланные поля: Value.absent() запись не трогает), удалённые убрать. Одной транзакцией.
  Future<void> applyCatalogChanges({List<ProductsCompanion> inserts = const [], List<ProductsCompanion> updates = const [], List<String> deletedIds = const []}) =>
      transaction(() async {
        if (inserts.isNotEmpty) await batch((b) => b.insertAll(products, inserts, mode: InsertMode.insertOrReplace));
        for (final u in updates) {
          await (update(products)..where((p) => p.id.equals(u.id.value))).write(u);
        }
        for (var i = 0; i < deletedIds.length; i += 500) {
          await (delete(products)..where((p) => p.id.isIn(deletedIds.skip(i).take(500)))).go();
        }
      });

  /// 2026-10-29 (срочно, владелец: «при смене аккаунта остаются данные старого, включая склад и товары»): вход другим аккаунтом —
  /// товары, остатки и служебные настройки каталога (область, курсор и ETag ленты, версия, упаковки) прошлого аккаунта стираются.
  /// Очередь продаж, клиенты, должники, чеки, настройки аккаунта и ключи ИИ не трогаются (очередь привязана к аккаунту — OutboxMeta).
  Future<void> wipeCatalogForAccountSwitch() => transaction(() async {
    await delete(products).go();
    await (delete(settings)..where((s) => s.key.like('catalog.%'))).go();
  });

  /// Товары по списку id (пачками — SQLite ограничивает число параметров).
  Future<Map<String, Product>> productsByIds(Iterable<String> ids) async {
    final all = ids.toList();
    final out = <String, Product>{};
    for (var i = 0; i < all.length; i += 500) {
      for (final p in await (select(products)..where((x) => x.id.isIn(all.skip(i).take(500)))).get()) {
        out[p.id] = p;
      }
    }
    return out;
  }

  /// 2026-10-30 (оптимизация, п. 10): минимум и закупка всех товаров — только эти поля (для сохранения при полной загрузке каталога).
  Future<Map<String, ({double minQuantity, double? purchasePrice})>> keptFieldsById() async {
    final rows = await customSelect('SELECT id, min_quantity, purchase_price FROM products', readsFrom: {products}).get();
    return {
      for (final r in rows) r.read<String>('id'): (minQuantity: r.read<double>('min_quantity'), purchasePrice: r.readNullable<double>('purchase_price')),
    };
  }

  Future<int> productCount() async => (await (selectOnly(products)..addColumns([products.id.count()])).getSingle()).read(products.id.count()) ?? 0;

  /// Поиск по названию, штрихкоду и артикулу.
  /// 2026-10-29 (аудит мобилки, п. 10): как SearchMatch кассы — ё=е, слова в любом порядке, при пустом результате вторая попытка
  /// с «не той раскладкой» и транслитом (Asu↔Асу, ФЫГ↔ASU). Всё в SQL по готовому ключу (search_key), без чтения всех строк.
  /// 2026-10-30 (оптимизация, п. 13): [extra] — дополнительное условие в SQL (остаток, услуги) — экраны «Продажа» и «Склад» берут
  /// товары окном (LIMIT), а не читают весь каталог на каждую букву поиска.
  Future<List<Product>> searchProducts(String query, {String? category, bool favorites = false, int limit = 200, Expression<bool> Function($ProductsTable p)? extra}) async {
    final sq = SearchQuery(query);
    final strict = await _searchRows(query, sq, fuzzy: false, category: category, favorites: favorites, limit: limit, extra: extra);
    if (strict.isNotEmpty || sq.isEmpty) return strict;
    return _searchRows(query, sq, fuzzy: true, category: category, favorites: favorites, limit: limit, extra: extra);
  }

  Future<List<Product>> _searchRows(String query, SearchQuery sq, {required bool fuzzy, String? category, required bool favorites, required int limit, Expression<bool> Function($ProductsTable p)? extra}) {
    final s = select(products);
    s.where((p) {
      Expression<bool> e = const Constant(true);
      if (!sq.isEmpty) {
        // каждое слово запроса — в любом месте ключа («кола 2л» найдёт «Кока-Кола (ПЭТ) 2л»)
        Expression<bool> text = const Constant(true);
        for (var i = 0; i < sq.tokens.length; i++) {
          Expression<bool> one = p.searchKey.like('%${sq.tokens[i]}%');
          if (fuzzy) {
            for (final v in sq.variants[i]) {
              one = one | p.searchKey.like('%$v%');
            }
          }
          text = text & one;
        }
        e = text | p.barcode.equals(query.trim()) | p.altBarcodes.like('%${query.trim()}%');
      }
      if (category != null && category.isNotEmpty) e = e & p.category.equals(category);
      if (favorites) e = e & p.isFavorite.equals(true);
      if (extra != null) e = e & extra(p);
      return e;
    });
    // 2026-10-30 (оптимизация, п. 13): id вторым ключом — порядок товаров с одинаковым ключом не меняется между запросами
    s.orderBy([(p) => OrderingTerm(expression: p.searchKey), (p) => OrderingTerm(expression: p.id)]);
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

  /// 2026-10-30 (оптимизация, п. 1–2): список очереди для экранов. Отправленные и снятые отсекаются и число строк ограничивается
  /// В SQL — экран не читает всю историю (она растёт с каждым чеком). [hideSent]/[hideDropped] — статусы sent / dropped.
  Stream<List<OutboxData>> watchOutbox({String? kind, bool hideSent = false, bool hideDropped = false, int? limit}) {
    final s = select(outbox)..orderBy([(o) => OrderingTerm(expression: o.createdAt, mode: OrderingMode.desc)]);
    s.where((o) {
      Expression<bool> e = const Constant(true);
      if (kind != null) e = e & o.kind.equals(kind);
      if (hideSent) e = e & o.status.equals('sent').not();
      if (hideDropped) e = e & o.status.equals('dropped').not();
      return e;
    });
    if (limit != null) s.limit(limit);
    return s.watch();
  }

  /// 2026-10-30 (оптимизация, п. 1): только ждущие отправки записи (обычно их 0–3) — для счётчика «неотправленных»; раньше счётчик
  /// перечитывал всю таблицу очереди на каждую её запись. Строки нужны целиком: владельца записи знает только тело (`_meta`).
  Stream<List<OutboxData>> watchPendingRows() =>
      (select(outbox)..where((o) => o.status.equals('pending'))).watch();

  /// 2026-10-30 (оптимизация, п. 3): есть ли что отправлять — без чтения тел и без записи (тик синхронизации каждые 20 с).
  Future<bool> hasPending() async {
    final r = await customSelect("SELECT 1 FROM outbox WHERE status = 'pending' LIMIT 1", readsFrom: {outbox}).get();
    return r.isNotEmpty;
  }

  /// 2026-10-30 (оптимизация, чистка очереди): отправленные и снятые записи старше [keepDays] дней удаляются, а из оставшихся
  /// отправленных хранятся не больше [keepRows] последних. Ждущие и «Не принято» не трогаются никогда. Возвращает число удалённых.
  Future<int> purgeOutbox({int keepDays = 30, int keepRows = 500}) async {
    final cutoff = DateTime.now().subtract(Duration(days: keepDays)).millisecondsSinceEpoch ~/ 1000;
    final a = await customUpdate(
      "DELETE FROM outbox WHERE status IN ('sent', 'dropped') AND created_at < ?",
      variables: [Variable.withInt(cutoff)],
      updates: {outbox},
      updateKind: UpdateKind.delete,
    );
    final b = await customUpdate(
      "DELETE FROM outbox WHERE status IN ('sent', 'dropped') AND id NOT IN "
      "(SELECT id FROM outbox WHERE status IN ('sent', 'dropped') ORDER BY created_at DESC LIMIT ?)",
      variables: [Variable.withInt(keepRows)],
      updates: {outbox},
      updateKind: UpdateKind.delete,
    );
    return a + b;
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

  // ------------------------------------------------------------------ замок очереди

  static const queueLockKey = 'queue.lock';

  /// 2026-10-29 (аудит, п. 3): замок отправки очереди между основным изолятом и фоновой задачей WorkManager (разные изоляты, один
  /// файл базы): одна строка настроек «владелец|до какого времени», захват одним SQL-оператором. Просроченный замок (процесс
  /// убили посреди отправки) забирается сразу. Ключ идемпотентности защищает от дубля и без замка — замок убирает лишние 409.
  Future<bool> tryAcquireQueueLock(String owner, {Duration ttl = const Duration(minutes: 3)}) async {
    final now = DateTime.now().millisecondsSinceEpoch;
    final mine = '$owner|${now + ttl.inMilliseconds}';
    await customStatement(
      "INSERT INTO settings(key, value) VALUES('$queueLockKey', ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value "
      "WHERE value = '' OR CAST(substr(value, instr(value, '|') + 1) AS INTEGER) < ?",
      [mine, now],
    );
    return await getSetting(queueLockKey) == mine;
  }

  Future<void> releaseQueueLock(String owner) =>
      customStatement("UPDATE settings SET value = '' WHERE key = '$queueLockKey' AND value LIKE ?", ['$owner|%']);

  // ------------------------------------------------------------------ настройки

  Future<String?> getSetting(String key) async => (await (select(settings)..where((s) => s.key.equals(key))).getSingleOrNull())?.value;

  Future<void> setSetting(String key, String value) => into(settings).insertOnConflictUpdate(SettingsCompanion.insert(key: key, value: value));
}
