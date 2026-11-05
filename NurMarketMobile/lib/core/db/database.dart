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

  /// 2026-10-31 (режим «Продукты»): срок годности товара (expiration_date сервера), «ГГГГ-ММ-ДД». null — не указан.
  TextColumn get expiresOn => text().nullable()();

  /// 2026-11-01 (живая сверка NBS, правило округления весовых): чистый признак `is_weight` товара с сервера, БЕЗ учёта единицы кг/г
  /// (в [isWeight] он смешан с единицей). null — сервер признак не прислал (старые строки до ближайшей загрузки каталога): тогда весовым
  /// считается по [isWeight]. Нужен только для округления подытога чека (cart.dart); продажа по весу по-прежнему идёт по [isWeight].
  BoolColumn get isWeightFlag => boolean().nullable()();

  /// 2026-11-05 (М5-16, решение владельца: колонка разрешена): «Код товара» сервера (`code`) отдельно от артикула. Раньше код склеивался
  /// с артикулом (`sku = sku ?? article ?? code`) и терялся, когда заполнен артикул — весы, запрограммированные «кодом товара»,
  /// не находили товар (на кассе исправлено 21.09). null — нет кода или каталог ещё не перезагружен после обновления.
  /// Откат: убрать колонку из кода — SQLite колонку не мешает (см. миграцию схемы 9→10).
  TextColumn get productCode => text().nullable()();

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

/// 2026-10-31 (одежда в мобилке): варианты товара — размер и цвет со своим остатком, ценой и штрихкодом (GET products/{id}/variants/).
/// Хранятся на телефоне, чтобы выбор размера, скан штрихкода размера и остаток по размеру работали без связи.
@DataClassName('VariantRow')
class ProductVariants extends Table {
  TextColumn get id => text()();
  TextColumn get productId => text()();
  TextColumn get size => text().withDefault(const Constant(''))();
  TextColumn get color => text().withDefault(const Constant(''))();
  TextColumn get barcode => text().nullable()();
  RealColumn get quantity => real().withDefault(const Constant(0))();

  /// null — цена размера не задана, берётся цена товара.
  RealColumn get price => real().nullable()();
  BoolColumn get isActive => boolean().withDefault(const Constant(true))();

  @override
  Set<Column> get primaryKey => {id};
}

/// 2026-10-31: когда варианты товара последний раз спрашивали у сервера и сколько их нашлось (0 — вариантов нет:
/// повторно такой товар не спрашиваем сутки). Список товаров сервера признака «есть варианты» не отдаёт.
class VariantChecks extends Table {
  TextColumn get productId => text()();
  IntColumn get checkedAt => integer()(); // миллисекунды с 1970
  IntColumn get total => integer().withDefault(const Constant(0))();

  @override
  Set<Column> get primaryKey => {productId};
}

class Settings extends Table {
  TextColumn get key => text()();
  TextColumn get value => text()();

  @override
  Set<Column> get primaryKey => {key};
}

/// 2026-10-31: условие «у товара есть активные размеры» для списков (склад, «Размеры и цвета»).
Expression<bool> productHasVariants() =>
    const CustomExpression<bool>('products.id IN (SELECT product_id FROM product_variants WHERE is_active = 1)');

@DriftDatabase(tables: [Products, Outbox, Settings, ProductVariants, VariantChecks])
class AppDatabase extends _$AppDatabase {
  AppDatabase([QueryExecutor? executor]) : super(executor ?? driftDatabase(name: 'nurmarket'));

  @override
  int get schemaVersion => 10;

  @override
  MigrationStrategy get migration => MigrationStrategy(
    // 2026-10-22 (скорость скана): индексы по штрихкоду, артикулу и PLU — раньше каждый скан читал все товары подряд
    // (на 50 000 товаров — десятки миллисекунд на каждое написание кода). IF NOT EXISTS: и новая база, и старая.
    beforeOpen: (details) async {
      // 2026-10-29 (аудит, п. 3): фоновая отправка очереди открывает ту же базу из другого изолята — занятая база ждёт, а не падает
      await customStatement('PRAGMA busy_timeout = 5000');
      // 2026-10-31 (одежда): индексы вариантов — по товару и по штрихкоду размера (скан этикетки размера)
      try {
        await customStatement('CREATE INDEX IF NOT EXISTS ix_variants_product ON product_variants(product_id)');
        await customStatement('CREATE INDEX IF NOT EXISTS ix_variants_barcode ON product_variants(barcode)');
      } catch (_) {
        // таблицы вариантов нет (урезанная база в тесте) — без индексов только медленнее
      }
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_barcode ON products(barcode)');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_sku ON products(sku)');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_plu_int ON products(CAST(plu AS INTEGER))');
      await customStatement('CREATE INDEX IF NOT EXISTS ix_products_sku_int ON products(CAST(sku AS INTEGER))');
      // 2026-11-05 (М5-16): поиск весовой этикетки по «коду товара»; колонки нет в урезанной базе теста — без индекса только медленнее
      try {
        await customStatement('CREATE INDEX IF NOT EXISTS ix_products_pcode_int ON products(CAST(product_code AS INTEGER))');
      } catch (_) {}
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
      // 2026-10-31 (одежда): варианты товара (размер/цвет) и отметки проверки — новые таблицы, прежние данные не трогаются
      if (from < 8) {
        await m.addColumn(products, products.expiresOn);
        await m.createTable(productVariants);
        await m.createTable(variantChecks);
      }
      // 2026-11-01 (округление весовых): чистый признак is_weight товара; у старых товаров null, каталог заполнит при ближайшей загрузке.
      // Идемпотентно: колонка уже есть (повторный запуск миграции, ручная правка) — не добавляем второй раз; данные не трогаются.
      if (from < 9) {
        final cols = await customSelect('PRAGMA table_info(products)').get();
        if (!cols.any((r) => r.read<String>('name') == 'is_weight_flag')) await m.addColumn(products, products.isWeightFlag);
        // 2026-11-02 (аудит №2, М3-05): чистый is_weight есть только в полной загрузке каталога. Сбрасываем время последней полной загрузки,
        // чтобы она прошла при первом же обновлении после этого обновления приложения, а не через недели (ключ catalog.fullAt).
        try {
          await customStatement("DELETE FROM settings WHERE key = 'catalog.fullAt'");
        } catch (_) {
          // настроек ещё нет (очень старая база) — полная загрузка и так будет первой
        }
      }
      // 2026-11-05 (М5-16): «Код товара» отдельной колонкой. Автозаполнение — из каталога: время полной загрузки сбрасываем, чтобы она
      // прошла при первом же обновлении (тогда код придёт у всех товаров). Идемпотентно: колонка уже есть — второй раз не добавляем.
      if (from < 10) {
        final cols = await customSelect('PRAGMA table_info(products)').get();
        if (!cols.any((r) => r.read<String>('name') == 'product_code')) await m.addColumn(products, products.productCode);
        try {
          await customStatement("DELETE FROM settings WHERE key = 'catalog.fullAt'");
        } catch (_) {
          // настроек ещё нет — полная загрузка и так будет первой
        }
      }
    },
  );

  // ------------------------------------------------------------------ каталог

  Future<void> replaceProducts(List<ProductsCompanion> rows) => transaction(() async {
    await delete(products).go();
    await batch((b) => b.insertAll(products, rows));
    await pruneOrphanVariants();
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
          await (delete(productVariants)..where((v) => v.productId.isIn(deletedIds.skip(i).take(500)))).go();
          await (delete(variantChecks)..where((v) => v.productId.isIn(deletedIds.skip(i).take(500)))).go();
        }
      });

  /// 2026-10-29 (срочно, владелец: «при смене аккаунта остаются данные старого, включая склад и товары»): вход другим аккаунтом —
  /// товары, остатки и служебные настройки каталога (область, курсор и ETag ленты, версия, упаковки) прошлого аккаунта стираются.
  /// Очередь продаж, клиенты, должники, чеки, настройки аккаунта и ключи ИИ не трогаются (очередь привязана к аккаунту — OutboxMeta).
  Future<void> wipeCatalogForAccountSwitch() => transaction(() async {
    await delete(products).go();
    await delete(productVariants).go();
    await delete(variantChecks).go();
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

  /// 2026-10-31: id всех товаров на телефоне (полная загрузка каталога убирает те, которых сервер больше не отдаёт).
  Future<List<String>> allProductIds() async => [for (final r in await customSelect('SELECT id FROM products', readsFrom: {products}).get()) r.read<String>('id')];

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

  /// 2026-10-31 (мобилка «не все товары»): условие поиска вынесено — один и тот же фильтр для списка (_searchRows) и для счётчика
  /// «Найдено M» ([countProducts]); счётчик раньше брали из длины окна, а не из базы.
  Expression<bool> _searchWhere($ProductsTable p, String query, SearchQuery sq, {required bool fuzzy, String? category, required bool favorites, Expression<bool> Function($ProductsTable p)? extra}) {
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
  }

  Future<List<Product>> _searchRows(String query, SearchQuery sq, {required bool fuzzy, String? category, required bool favorites, required int limit, Expression<bool> Function($ProductsTable p)? extra}) {
    final s = select(products);
    s.where((p) => _searchWhere(p, query, sq, fuzzy: fuzzy, category: category, favorites: favorites, extra: extra));
    // 2026-10-30 (оптимизация, п. 13): id вторым ключом — порядок товаров с одинаковым ключом не меняется между запросами
    s.orderBy([(p) => OrderingTerm(expression: p.searchKey), (p) => OrderingTerm(expression: p.id)]);
    s.limit(limit);
    return s.get();
  }

  /// 2026-10-31: сколько товаров найдёт [searchProducts] без окна (LIMIT) — для строки «Найдено M из базы телефона». Та же логика:
  /// сначала точный поиск, при нуле — «не та раскладка» и транслит.
  Future<int> countProducts(String query, {String? category, bool favorites = false, Expression<bool> Function($ProductsTable p)? extra}) async {
    final sq = SearchQuery(query);
    Future<int> count(bool fuzzy) async {
      final c = products.id.count();
      final q = selectOnly(products)
        ..addColumns([c])
        ..where(_searchWhere(products, query, sq, fuzzy: fuzzy, category: category, favorites: favorites, extra: extra));
      return (await q.getSingle()).read(c) ?? 0;
    }

    final strict = await count(false);
    if (strict > 0 || sq.isEmpty) return strict;
    return count(true);
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
    // 2026-10-31 (одежда): штрихкод размера (этикетка «Платье — 44, Красный») — товар этого размера; до весовой этикетки
    for (final v in variants) {
      final row = await variantByBarcode(v);
      if (row == null) continue;
      final p = await productById(row.productId);
      if (p != null) return (product: p, qty: null);
    }
    if (!allowWeightLabel || variants.isEmpty) return null;
    // 2026-11-05 (М5-11): только среди ВЕСОВЫХ товаров, как MustWeigh кассы (раньше PLU «12» находил штучную «Жвачку» с артикулом 12)
    final hits = await weightLabelHits(variants.first);
    final h = hits.where((x) => x.weighed).firstOrNull;
    return h == null ? null : (product: h.product, qty: h.qty);
  }

  /// 2026-11-05 (М5-11): весовой ли товар — как MustWeigh кассы. [Product.isWeight] уже включает единицу кг/г, [Product.isWeightFlag] —
  /// чистый признак сервера (null — не пришёл).
  static bool isWeighed(Product p) => p.isWeight || p.isWeightFlag == true;

  /// Весовая этикетка → товары, которые она может означать, в порядке приоритета (как LocalCartService.FindByEmbeddedCode кассы).
  /// Поля поиска: раскладка «plu» (код 5 цифр) — PLU, потом артикул, потом «код товара»; раскладка «code» (6 цифр, Rongta) — наоборот,
  /// артикул/код, потом PLU. Весовые товары идут первыми; штучный с тем же кодом попадает в список только при [includePiece] и с
  /// признаком weighed = false (сканер тогда спрашивает «это весовая этикетка, а товар штучный — добавить?»). Раскладка не задана:
  /// проверяются оба прочтения этикетки — если они нашли РАЗНЫЕ товары, в списке их два (сканер даёт выбрать, а не гадает).
  /// qty: вес в кг; на этикетке «по сумме» — сумма ÷ цена; цена 0 — null (вес впишут руками, как на кассе).
  Future<List<({Product product, double? qty, bool weighed})>> weightLabelHits(String code, {bool includePiece = false}) async {
    final label = parseWeightLabel(code, clothing: (await getSetting('shop.clothing')) == '1');
    if (label == null) return const [];
    final out = <({Product product, double? qty, bool weighed})>[];
    for (final c in label.candidates) {
      final n = int.tryParse(c.code) ?? 0;
      if (n <= 0) continue;
      final rows = await customSelect(
        'SELECT * FROM products WHERE (plu IS NOT NULL AND CAST(plu AS INTEGER) = ?1) OR (sku IS NOT NULL AND CAST(sku AS INTEGER) = ?1) '
        'OR (product_code IS NOT NULL AND CAST(product_code AS INTEGER) = ?1) LIMIT 40',
        variables: [Variable.withInt(n)],
        readsFrom: {products},
      ).map((r) => products.map(r.data)).get();
      // 6 цифр — «код» Rongta (артикул / код товара важнее PLU); 5 цифр — PLU
      final byCode = c.code.length == 6;
      bool match(Product p, int field) => sameShortCode(switch (field) { 0 => p.plu, 1 => p.sku, _ => p.productCode }, c.code);
      Product? pick(bool weighed) {
        for (final field in byCode ? const [1, 2, 0] : const [0, 1, 2]) {
          final p = rows.where((r) => isWeighed(r) == weighed && match(r, field)).firstOrNull;
          if (p != null) return p;
        }
        return null;
      }

      final p = pick(true) ?? (includePiece ? pick(false) : null);
      if (p == null || out.any((h) => h.product.id == p.id)) continue;
      final qty = label.isAmount ? (p.price > 0 ? double.parse((c.value / p.price).toStringAsFixed(3)) : null) : double.parse(c.value.toStringAsFixed(3));
      out.add((product: p, qty: qty, weighed: isWeighed(p)));
    }
    return out;
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

  /// 2026-11-05 (М5-12): PLU, записанный товару со сканера (на сервере уже сохранён) — на телефоне сразу, без ожидания ленты каталога.
  Future<void> setProductPlu(String id, String plu) => (update(products)..where((p) => p.id.equals(id))).write(ProductsCompanion(plu: Value(plu)));

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
  Future<void> deleteProduct(String id) async {
    await (delete(products)..where((p) => p.id.equals(id))).go();
    await (delete(productVariants)..where((v) => v.productId.equals(id))).go();
    await (delete(variantChecks)..where((v) => v.productId.equals(id))).go();
  }

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

  // ------------------------------------------------------------------ варианты товара (одежда)

  /// Варианты товара из базы телефона (по цвету и размеру). Пусто — вариантов нет или ещё не спрашивали (см. [variantCheck]).
  Future<List<VariantRow>> variantsOf(String productId) =>
      (select(productVariants)
            ..where((v) => v.productId.equals(productId))
            ..orderBy([(v) => OrderingTerm(expression: v.color), (v) => OrderingTerm(expression: v.size)]))
          .get();

  /// Варианты нескольких товаров одним запросом (экран «Размеры и цвета»): id товара → его варианты.
  Future<Map<String, List<VariantRow>>> variantsOfMany(Iterable<String> productIds) async {
    final ids = productIds.toList();
    final out = <String, List<VariantRow>>{};
    for (var i = 0; i < ids.length; i += 400) {
      for (final v in await (select(productVariants)..where((x) => x.productId.isIn(ids.skip(i).take(400)))).get()) {
        (out[v.productId] ??= []).add(v);
      }
    }
    return out;
  }

  Future<VariantRow?> variantById(String id) => (select(productVariants)..where((v) => v.id.equals(id))).getSingleOrNull();

  /// Вариант по штрихкоду размера (в любом написании кода) — этикетка размера в скане.
  Future<VariantRow?> variantByBarcode(String code) async {
    final forms = barcodeVariants(code).where((c) => c.trim().isNotEmpty).toList();
    if (forms.isEmpty) return null;
    final rows = await (select(productVariants)..where((v) => v.barcode.isIn(forms))).get();
    for (final f in forms) {
      for (final r in rows) {
        if (r.barcode == f) return r;
      }
    }
    return null;
  }

  /// Когда и с каким числом вариантов товар проверен; null — не проверялся.
  Future<VariantCheck?> variantCheck(String productId) => (select(variantChecks)..where((c) => c.productId.equals(productId))).getSingleOrNull();

  /// Записать свежий список вариантов товара целиком (старые варианты этого товара заменяются) и отметку проверки.
  Future<void> replaceVariants(String productId, List<ProductVariantsCompanion> rows, {DateTime? at}) => transaction(() async {
    await (delete(productVariants)..where((v) => v.productId.equals(productId))).go();
    if (rows.isNotEmpty) await batch((b) => b.insertAll(productVariants, rows, mode: InsertMode.insertOrReplace));
    await into(variantChecks).insertOnConflictUpdate(
      VariantChecksCompanion.insert(productId: productId, checkedAt: (at ?? DateTime.now()).millisecondsSinceEpoch, total: Value(rows.length)),
    );
  });

  /// Один вариант, найденный по штрихкоду на сервере: добавить или обновить, не трогая остальные варианты товара.
  Future<void> upsertVariant(ProductVariantsCompanion row) => into(productVariants).insertOnConflictUpdate(row);

  /// Остаток размера после продажи/снятия чека — сразу, не дожидаясь обновления вариантов с сервера.
  Future<void> adjustVariantStock(String variantId, double delta) => customUpdate(
    'UPDATE product_variants SET quantity = quantity + ? WHERE id = ?',
    variables: [Variable.withReal(delta), Variable.withString(variantId)],
    updates: {productVariants},
  );

  /// Сколько у товаров активных вариантов и сколько штук всего — для надписи на плитке. Только товары с вариантами.
  Future<Map<String, ({int count, double qty})>> variantSummary([Iterable<String>? productIds]) async {
    final ids = productIds?.toList();
    final out = <String, ({int count, double qty})>{};
    Future<void> read(List<String>? chunk) async {
      final rows = await customSelect(
        'SELECT product_id, COUNT(*) AS n, SUM(CASE WHEN quantity > 0 THEN quantity ELSE 0 END) AS q FROM product_variants WHERE is_active = 1'
        '${chunk == null ? '' : ' AND product_id IN (${List.filled(chunk.length, '?').join(',')})'} GROUP BY product_id',
        variables: [if (chunk != null) for (final c in chunk) Variable.withString(c)],
        readsFrom: {productVariants},
      ).get();
      for (final r in rows) {
        out[r.read<String>('product_id')] = (count: r.read<int>('n'), qty: r.read<double?>('q') ?? 0);
      }
    }

    if (ids == null) {
      await read(null);
    } else {
      for (var i = 0; i < ids.length; i += 400) {
        await read(ids.skip(i).take(400).toList());
      }
    }
    return out;
  }

  /// Есть ли на телефоне хоть один вариант — магазин торгует размерами и цветами.
  Future<bool> hasAnyVariants() async {
    final r = await customSelect('SELECT 1 FROM product_variants LIMIT 1', readsFrom: {productVariants}).get();
    return r.isNotEmpty;
  }

  /// Варианты и отметки товаров, которых больше нет в каталоге (полная загрузка заменила список).
  Future<void> pruneOrphanVariants() async {
    await customStatement('DELETE FROM product_variants WHERE product_id NOT IN (SELECT id FROM products)');
    await customStatement('DELETE FROM variant_checks WHERE product_id NOT IN (SELECT id FROM products)');
  }

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

  /// 2026-11-02 (аудит №2, М2-07): замок продлевается после каждой записи очереди — большая очередь идёт дольше 3 минут, и без продления
  /// фоновая задача и приложение начинали бы отправлять одновременно. Продлевается только свой замок.
  Future<void> renewQueueLock(String owner, {Duration ttl = const Duration(minutes: 3)}) => customStatement(
    "UPDATE settings SET value = ? WHERE key = '$queueLockKey' AND value LIKE ?",
    ['$owner|${DateTime.now().millisecondsSinceEpoch + ttl.inMilliseconds}', '$owner|%'],
  );

  Future<void> releaseQueueLock(String owner) =>
      customStatement("UPDATE settings SET value = '' WHERE key = '$queueLockKey' AND value LIKE ?", ['$owner|%']);

  // ------------------------------------------------------------------ настройки

  Future<String?> getSetting(String key) async => (await (select(settings)..where((s) => s.key.equals(key))).getSingleOrNull())?.value;

  Future<void> setSetting(String key, String value) => into(settings).insertOnConflictUpdate(SettingsCompanion.insert(key: key, value: value));
}
