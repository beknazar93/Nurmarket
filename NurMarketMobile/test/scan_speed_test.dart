import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/scan/barcode_lookup.dart';

// 2026-10-22 (скорость скана): поиск товара по коду на 50 000 товаров — до (без индексов, по запросу на написание кода)
// и после (индексы, один запрос на все написания); сверка товаров кадра умной камеры — до и после.
String _ean13(int n) {
  final body = n.toString().padLeft(12, '0');
  var sum = 0;
  for (var i = 0; i < 12; i++) {
    sum += int.parse(body[i]) * (i.isEven ? 1 : 3);
  }
  return '$body${(10 - sum % 10) % 10}';
}

void main() {
  late AppDatabase db;
  const total = 50000;

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    await db.productCount(); // открыть базу (индексы создаются в beforeOpen)
    await db.replaceProducts([
      for (var i = 1; i <= total; i++)
        ProductsCompanion.insert(
          id: 'p$i',
          name: 'Товар номер $i ${i % 7 == 0 ? 'молоко' : 'хлеб'}',
          barcode: Value(_ean13(100000 + i)),
          sku: Value('A$i'),
          plu: Value(i <= 999 ? '$i' : null),
          altBarcodes: Value(i % 50 == 0 ? 'ALT$i|12' : ''),
          searchKey: Value('товар номер $i ${i % 7 == 0 ? 'молоко' : 'хлеб'} a$i'),
        ),
    ]);
  });
  tearDown(() => db.close());

  Future<int> avgMicros(Future<void> Function() f, {int runs = 20}) async {
    await f();
    final sw = Stopwatch()..start();
    for (var i = 0; i < runs; i++) {
      await f();
    }
    return sw.elapsedMicroseconds ~/ runs;
  }

  /// Как было до ускорения: по два запроса на каждое написание кода.
  Future<void> oldScan(String code) async {
    for (final v in barcodeVariants(code)) {
      if (await db.productByBarcode(v) != null) return;
    }
  }

  test('скорость поиска по коду на 50 000 товаров: до и после', () async {
    final hit = _ean13(100000 + 31337); // обычный скан: код сразу как в базе
    final upcA = _ean13(100000 + 777).substring(1); // UPC-A без нуля: нужен второй вариант записи
    final miss = _ean13(900000000 + 5); // нет в базе: все варианты и доп. штрихкоды
    final alt = 'ALT25000'; // доп. штрихкод

    // «до»: убрать индексы, искать как раньше
    for (final ix in ['ix_products_barcode', 'ix_products_sku', 'ix_products_plu_int', 'ix_products_sku_int']) {
      await db.customStatement('DROP INDEX IF EXISTS $ix');
    }
    final before = {
      'hit': await avgMicros(() => oldScan(hit), runs: 5),
      'upcA': await avgMicros(() => oldScan(upcA), runs: 5),
      'miss': await avgMicros(() => oldScan(miss), runs: 5),
      'alt': await avgMicros(() => oldScan(alt), runs: 5),
    };
    // «после»: индексы и новый productByScan
    await db.customStatement('CREATE INDEX IF NOT EXISTS ix_products_barcode ON products(barcode)');
    await db.customStatement('CREATE INDEX IF NOT EXISTS ix_products_sku ON products(sku)');
    await db.customStatement('CREATE INDEX IF NOT EXISTS ix_products_plu_int ON products(CAST(plu AS INTEGER))');
    await db.customStatement('CREATE INDEX IF NOT EXISTS ix_products_sku_int ON products(CAST(sku AS INTEGER))');
    final after = {
      'hit': await avgMicros(() => db.productByScan(hit)),
      'upcA': await avgMicros(() => db.productByScan(upcA)),
      'miss': await avgMicros(() => db.productByScan(miss)),
      'alt': await avgMicros(() => db.productByScan(alt)),
    };
    for (final k in before.keys) {
      // ignore: avoid_print
      print('поиск по коду [$k]: было ${(before[k]! / 1000).toStringAsFixed(2)} мс, стало ${(after[k]! / 1000).toStringAsFixed(2)} мс');
    }
    expect(after['hit']!, lessThan(before['hit']!));
    expect(after['miss']!, lessThan(before['miss']!));
    // весовая этикетка по PLU (индекс по выражению)
    final label = _ean13(0).replaceRange(0, 2, '20'); // не используется для проверки значений — только время
    final t = Stopwatch()..start();
    await db.productByScan(label);
    // ignore: avoid_print
    print('весовая/неизвестная этикетка: ${t.elapsedMilliseconds} мс');
  });

  test('результаты поиска прежние: основной код, артикул, UPC-A, доп. штрихкод, приоритет написаний', () async {
    expect((await db.productByScan(_ean13(100000 + 5)))!.product.id, 'p5');
    expect((await db.productByScan('A77'))!.product.id, 'p77');
    expect((await db.productByScan(_ean13(100000 + 9).substring(1)))!.product.id, 'p9'); // UPC-A → EAN-13
    final a = (await db.productByScan('ALT100'))!;
    expect(a.product.id, 'p100');
    expect(a.qty, 12);
    expect(await db.productByScan(_ean13(900000000 + 1)), isNull);
    // первое написание важнее: код есть и в виде 12 цифр у одного товара, и с нулём у другого
    final twelve = _ean13(100000 + 3).substring(1);
    await db.upsertProduct(ProductsCompanion.insert(id: 'x12', name: 'Двенадцать', barcode: Value(twelve)));
    expect((await db.productByScan(twelve))!.product.id, 'x12');
    expect((await db.productByScan(_ean13(100000 + 3)))!.product.id, 'p3');
  });

  test('сверка товаров кадра умной камеры: до и после', () async {
    final vision = VisionService(ApiClient(SessionStore()), db);
    final items = [
      for (final t in ['Товар номер 123 молоко', 'Товар номер 456 хлеб', 'Товар номер 789 молоко'])
        SeenItem(name: t, brand: '', size: '', barcode: '', count: 1),
    ];
    Future<void> oldMatch() async {
      for (final s in items) {
        final raw = s.title.toLowerCase().split(RegExp(r'\s+')).where((w) => w.length >= 3);
        final terms = <String>{for (final w in raw.take(4)) ...{w, VisionService.normWord(w)}};
        for (final w in terms) {
          await db.searchProducts(w.length > 5 ? w.substring(0, 5) : w, limit: 40);
        }
      }
    }

    final before = await avgMicros(oldMatch, runs: 3);
    final after = await avgMicros(() => vision.match(items), runs: 3);
    // ignore: avoid_print
    print('сверка 3 товаров кадра с каталогом: было ${(before / 1000).toStringAsFixed(1)} мс, стало ${(after / 1000).toStringAsFixed(1)} мс');
    final m = await vision.match(items);
    expect(m.length, 3);
    expect(m.first.candidates, isNotEmpty);
  });
}
