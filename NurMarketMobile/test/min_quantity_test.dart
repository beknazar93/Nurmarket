// 2026-10-22 (мобилка, админ): минимальный остаток товара — миграция базы телефона 5 → 6 и «Заканчивается».
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/util/product_picker.dart';
import 'package:nurmarket/features/sale/catalog_repository.dart';

void main() {
  test('миграция со старой базы (версия 5): минимум добавлен нулём, данные целы', () async {
    final db = AppDatabase(
      NativeDatabase.memory(
        setup: (raw) {
          raw.execute('''CREATE TABLE products (
            id TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, price REAL NOT NULL DEFAULT 0, wholesale_price REAL NOT NULL DEFAULT 0,
            barcode TEXT NULL, alt_barcodes TEXT NOT NULL DEFAULT '', sku TEXT NULL, plu TEXT NULL, unit TEXT NOT NULL DEFAULT 'шт',
            is_weight INTEGER NOT NULL DEFAULT 0, is_service INTEGER NOT NULL DEFAULT 0, is_favorite INTEGER NOT NULL DEFAULT 0,
            quantity REAL NOT NULL DEFAULT 0, category TEXT NOT NULL DEFAULT '', image_url TEXT NULL,
            search_key TEXT NOT NULL DEFAULT '', promo_rules TEXT NOT NULL DEFAULT '')''');
          raw.execute("INSERT INTO products (id, name, price, quantity) VALUES ('old1', 'Старый товар', 12.5, 4)");
          raw.execute('PRAGMA user_version = 5');
        },
      ),
    );
    final p = await (db.select(db.products)..where((x) => x.id.equals('old1'))).getSingle();
    expect(p.name, 'Старый товар');
    expect(p.price, 12.5);
    expect(p.quantity, 4);
    expect(p.minQuantity, 0, reason: 'у старых товаров минимум не задан — общий порог');
    expect(isLowStock(p.quantity, p.minQuantity), isTrue); // 4 < 10
    await db.close();
  });

  test('«Заканчивается»: без минимума < 10, с минимумом — до минимума включительно', () {
    expect(isLowStock(9, 0), isTrue);
    expect(isLowStock(10, 0), isFalse);
    expect(isLowStock(10, 20), isTrue);
    expect(isLowStock(20, 20), isTrue);
    expect(isLowStock(21, 20), isFalse);
    expect(isLowStock(3, 2), isFalse);
  });

  test('запрос «заканчивается» к базе учитывает минимум товара', () async {
    final db = AppDatabase(NativeDatabase.memory());
    Future<void> add(String id, double q, double min) =>
        db.upsertProduct(ProductsCompanion.insert(id: id, name: id, quantity: Value(q), minQuantity: Value(min)));
    await add('a', 5, 0); // порог 10 → заканчивается
    await add('b', 15, 0); // нет
    await add('c', 15, 20); // минимум 20 → заканчивается
    await add('d', 3, 2); // минимум 2 → нет
    final low = await (db.select(db.products)..where((p) => lowStockSql(p))).get();
    expect(low.map((p) => p.id).toSet(), {'a', 'c'});
    await db.close();
  });

  test('каталог сервера: minimum_quantity попадает в базу, нет поля — 0', () {
    final withMin = CatalogRepository.mapProduct({'id': 'x', 'name': 'Х', 'minimum_quantity': '12.000'})!;
    expect(withMin.minQuantity.value, 12);
    final without = CatalogRepository.mapProduct({'id': 'y', 'name': 'У'})!;
    expect(without.minQuantity.value, 0);
  });
}
