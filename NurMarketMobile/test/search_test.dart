import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';

// 13.10, «мобилка не видит все данные»: поиск по-русски без учёта регистра и по словам в любом порядке.
void main() {
  test('cyrillic case-insensitive search by words', () async {
    final db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: '1', name: 'Макароны «Макфа» 450г', searchKey: const Value('макароны «макфа» 450г')),
      ProductsCompanion.insert(id: '2', name: 'Кока-Кола (ПЭТ) 2л', barcode: const Value('5449000000996'), searchKey: const Value('кока-кола (пэт) 2л 5449000000996')),
    ]);
    expect((await db.searchProducts('мак')).single.id, '1');
    expect((await db.searchProducts('кола 2л')).single.id, '2');
    expect((await db.searchProducts('5449000000996')).single.id, '2');
    expect((await db.searchProducts('')).length, 2);
    await db.close();
  });

  // 13.10: доп. штрихкод упаковки, как у кассы — скан коробки даёт её количество штук.
  test('pack barcode gives pack quantity', () async {
    final db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: '1', name: 'Mars', barcode: const Value('4600000000001'), altBarcodes: const Value('4600000000002|24|Блок\n4600000000003')),
    ]);
    expect((await db.productByScan('4600000000002'))!.qty, 24);
    expect((await db.productByScan('4600000000003'))!.qty, isNull);
    expect((await db.productByScan('4600000000001'))!.product.id, '1');
    await db.close();
  });
}
