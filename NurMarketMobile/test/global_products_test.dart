import 'dart:async';
import 'dart:io';

import 'package:drift/drift.dart' hide isNull, isNotNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/catalog/global_products.dart';
import 'package:nurmarket/features/stock/bulk_import.dart';

// 2026-10-27: встроенный справочник готовых наименований NurCRM — поиск по штрихкоду (EAN/UPC), мусорные коды,
// слияние новой выгрузки, запрос сервера (404 помнится 24 часа, офлайн без ошибок, один запрос за раз).

class _Seed extends GeneratedDatabase {
  _Seed(super.e);
  @override
  Iterable<TableInfo<Table, dynamic>> get allTables => const [];
  @override
  int get schemaVersion => 1;
}

/// Файл справочника как у встроенной базы: products + meta.
Future<void> seed(File f, List<List<String>> rows, {String exportedAt = '2026-10-26', int added = 0}) async {
  final db = _Seed(NativeDatabase(f));
  await db.customStatement('CREATE TABLE products(barcode TEXT PRIMARY KEY, name TEXT NOT NULL, brand TEXT NOT NULL DEFAULT \'\', category TEXT NOT NULL DEFAULT \'\') WITHOUT ROWID');
  await db.customStatement('CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)');
  await db.customStatement("INSERT INTO meta VALUES('exported_at', ?)", [exportedAt]);
  if (added > 0) await db.customStatement("INSERT INTO meta VALUES('added', ?)", ['$added']);
  for (final r in rows) {
    await db.customStatement('INSERT INTO products VALUES(?,?,?,?)', [r[0], r[1], r.length > 2 ? r[2] : '', r.length > 3 ? r[3] : '']);
  }
  await db.close();
}

void main() {
  late Directory dir;
  final opened = <GlobalProducts>[];
  setUp(() => dir = Directory.systemTemp.createTempSync('globalprod'));
  tearDown(() async {
    for (final g in opened) {
      await g.close();
    }
    opened.clear();
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  GlobalProducts make({
    required File file,
    Future<dynamic> Function(String)? fetch,
    Future<String?> Function()? version,
    Future<File?> Function()? bundle,
    Future<void> Function()? repair,
    DateTime Function()? clock,
  }) {
    final g = GlobalProducts(
      openFile: () async => file,
      executorFor: NativeDatabase.new,
      bundledVersion: version,
      bundledCopy: bundle,
      fetch: fetch,
      repair: repair,
      clock: clock,
    );
    opened.add(g);
    return g;
  }

  test('мусорные коды: нет цифр, короче 6, длиннее 14', () {
    for (final c in ['!', ':', r'$^)#@', '12345', '123456789012345', 'ABCDEFGH', '']) {
      expect(isJunkGlobalCode(c), isTrue, reason: c);
    }
    for (final c in ['123456', '4600123456789', '12345678901234', r'$45261A-78B00']) {
      expect(isJunkGlobalCode(c), isFalse, reason: c);
    }
    expect(GlobalProducts.variantsOf('!'), isEmpty);
    expect(GlobalProducts.variantsOf('4600 1234 56789'), contains('4600123456789'));
  });

  test('поиск: EAN-13 ↔ UPC-A с ведущим нулём, GTIN-14', () async {
    final f = File('${dir.path}/a.sqlite');
    await seed(f, [
      ['0012345678905', 'Печенье EAN', 'Brand', 'Сладости'], // в базе с нулём
      ['036000291452', 'Салфетки UPC'], // в базе UPC-A 12 цифр
      ['4600123456789', 'Кофе'],
    ]);
    final g = make(file: f);
    final a = await g.find('012345678905'); // камера отдала UPC-A без нуля
    expect(a?.name, 'Печенье EAN');
    expect(a?.brand, 'Brand');
    expect(a?.category, 'Сладости');
    expect((await g.find('0036000291452'))?.name, 'Салфетки UPC'); // EAN-13 с нулём → UPC-A
    expect((await g.find('04600123456789'))?.name, 'Кофе'); // GTIN-14 с нулём → EAN-13
    expect((await g.find(' 4600 1234 56789\n'))?.name, 'Кофе'); // пробелы и перевод строки сканера
    expect(await g.find('4600123456780', network: false), isNull);
  });

  test('мусорные коды в базе не находятся', () async {
    final f = File('${dir.path}/b.sqlite');
    await seed(f, [
      ['!', 'мусор'],
      ['12345', 'коротко'],
      ['ABCDEFGH', 'без цифр'],
    ]);
    var asked = 0;
    final g = make(file: f, fetch: (c) async { asked++; return null; });
    expect(await g.find('!'), isNull);
    expect(await g.find('12345'), isNull);
    expect(await g.find('ABCDEFGH'), isNull);
    expect(asked, 0, reason: 'мусор на сервер не отправляется');
  });

  test('сервер: 200 дописывается в базу, повторно сервер не спрашивается', () async {
    final f = File('${dir.path}/c.sqlite');
    await seed(f, [
      ['4600123456789', 'Кофе'],
    ]);
    var asked = 0;
    final g = make(
      file: f,
      fetch: (c) async {
        asked++;
        return {'id': 'u', 'name': 'Сникерс 50 г', 'barcode': '5000159461122', 'brand': 'Mars', 'category': 'Шоколад'};
      },
    );
    final p = await g.find('5000159461122');
    expect(p?.name, 'Сникерс 50 г');
    expect(p?.brand, 'Mars');
    expect(asked, 1);
    expect((await g.find('5000159461122'))?.name, 'Сникерс 50 г');
    expect((await g.local('05000159461122'))?.name, 'Сникерс 50 г'); // и вариант с нулём без сети
    expect(asked, 1);
    final st = await g.status();
    expect(st?.count, 2);
    expect(st?.added, 1);
    expect(st?.exportedAt, '2026-10-26');
  });

  test('сервер: ответ с вложенными brand/category-объектами', () async {
    final f = File('${dir.path}/c2.sqlite');
    await seed(f, []);
    final g = make(
      file: f,
      fetch: (c) async => {
        'product': {'name': 'Чай', 'brand': {'name': 'Lipton'}, 'category': {'name': 'Напитки'}},
      },
    );
    final p = await g.find('8714100123456');
    expect(p?.name, 'Чай');
    expect(p?.brand, 'Lipton');
    expect(p?.category, 'Напитки');
    expect(p?.barcode, '8714100123456');
  });

  test('сервер: 404 помнится 24 часа', () async {
    final f = File('${dir.path}/d.sqlite');
    await seed(f, []);
    var asked = 0;
    var now = DateTime(2026, 10, 27, 10);
    final g = make(file: f, clock: () => now, fetch: (c) async { asked++; throw ApiException('нет', status: 404); });
    expect(await g.find('5000159461122'), isNull);
    expect(asked, 1);
    expect(await g.find('5000159461122'), isNull);
    now = now.add(const Duration(hours: 23));
    expect(await g.find('5000159461122'), isNull);
    expect(asked, 1, reason: 'за 23 часа сервер не спрашивается');
    now = now.add(const Duration(hours: 2));
    expect(await g.find('5000159461122'), isNull);
    expect(asked, 2, reason: 'через 25 часов спрашиваем снова');
  });

  test('офлайн и сбои сервера: null без ошибок и без запоминания', () async {
    final f = File('${dir.path}/e.sqlite');
    await seed(f, []);
    var asked = 0;
    final g = make(
      file: f,
      fetch: (c) async {
        asked++;
        throw asked == 1 ? ApiException('Нет связи', status: null) : (asked == 2 ? ApiException('Сервер', status: 500) : TimeoutException('t'));
      },
    );
    expect(await g.find('5000159461122'), isNull);
    expect(await g.find('5000159461122'), isNull);
    expect(await g.find('5000159461122'), isNull);
    expect(asked, 3, reason: 'сбой — не «нет в справочнике», каждый раз спрашиваем заново');
  });

  test('нет входа (fetch вернул null) — не запоминается; весовая этикетка на сервер не уходит', () async {
    final f = File('${dir.path}/e2.sqlite');
    await seed(f, []);
    var asked = 0;
    final g = make(file: f, fetch: (c) async { asked++; return null; });
    expect(await g.find('5000159461122'), isNull);
    expect(await g.find('5000159461122'), isNull);
    expect(asked, 2);
    expect(await g.find('2000012300456'), isNull);
    expect(asked, 2);
  });

  test('не больше одного запроса к серверу одновременно', () async {
    final f = File('${dir.path}/f.sqlite');
    await seed(f, []);
    var running = 0, maxRunning = 0, total = 0;
    final g = make(
      file: f,
      fetch: (c) async {
        total++;
        running++;
        if (running > maxRunning) maxRunning = running;
        await Future<void>.delayed(const Duration(milliseconds: 30));
        running--;
        return {'name': 'Товар $c', 'barcode': c};
      },
    );
    final r = await Future.wait([g.find('1000000000011'), g.find('1000000000028'), g.find('1000000000035'), g.find('1000000000011')]);
    expect(r.map((e) => e?.name), ['Товар 1000000000011', 'Товар 1000000000028', 'Товар 1000000000035', 'Товар 1000000000011']);
    expect(maxRunning, 1);
    expect(total, 3, reason: 'повтор того же кода после очереди берётся из базы');
  });

  test('слияние: более новая выгрузка добавляет строки, свои правки и добавленное с сервера не теряются', () async {
    final user = File('${dir.path}/user.sqlite');
    await seed(user, [
      ['4600123456789', 'Кофе (правка с сервера)'],
      ['5000159461122', 'Сникерс с сервера'],
    ], exportedAt: '2026-10-01', added: 1);
    final bundle = File('${dir.path}/bundle.sqlite');
    await seed(bundle, [
      ['4600123456789', 'Кофе старое имя'],
      ['4870202121261', 'Новое наименование'],
      ['4870202121278', 'Ещё новое'],
    ]);
    final g = make(file: user, version: () async => '2026-10-26', bundle: () async => bundle);
    expect((await g.find('4870202121261', network: false))?.name, 'Новое наименование');
    expect((await g.find('4600123456789', network: false))?.name, 'Кофе (правка с сервера)');
    expect((await g.find('5000159461122', network: false))?.name, 'Сникерс с сервера');
    final st = await g.status();
    expect(st?.count, 4);
    expect(st?.exportedAt, '2026-10-26');
    expect(st?.added, 3, reason: '1 с сервера + 2 новых из выгрузки');
    expect(bundle.existsSync(), isFalse, reason: 'временный файл удалён');
  });

  test('слияние не идёт, если версия та же или старее', () async {
    final user = File('${dir.path}/user2.sqlite');
    await seed(user, [
      ['4600123456789', 'Кофе'],
    ]);
    final bundle = File('${dir.path}/bundle2.sqlite');
    await seed(bundle, [
      ['4870202121261', 'Не должно появиться'],
    ]);
    final g = make(file: user, version: () async => '2026-10-26', bundle: () async => bundle);
    expect(await g.find('4870202121261', network: false), isNull);
    expect((await g.status())?.count, 1);
  });

  test('испорченный файл пересоздаётся (repair)', () async {
    final f = File('${dir.path}/g.sqlite');
    await f.writeAsString('это не база данных' * 100);
    final opener = GlobalProducts(
      openFile: () async {
        if (!f.existsSync()) await seed(f, [['4600123456789', 'Кофе']]);
        return f;
      },
      executorFor: NativeDatabase.new,
      repair: () async => f.deleteSync(),
    );
    opened.add(opener);
    expect((await opener.find('4600123456789'))?.name, 'Кофе');
  });

  test('если справочник не открылся совсем — поиск отвечает null, а не падает', () async {
    final g = GlobalProducts(openFile: () async => throw StateError('нет диска'), executorFor: NativeDatabase.new);
    opened.add(g);
    expect(await g.find('4600123456789'), isNull);
    expect(await g.status(), isNull);
  });

  test('массовое добавление: название по штрихкоду из справочника, введённое не затирается', () async {
    final f = File('${dir.path}/h.sqlite');
    await seed(f, [
      ['4600123456789', 'Кофе из справочника', 'Jardin', 'Кофе'],
    ]);
    final g = make(file: f);
    final rows = BulkParser.parse(BulkParser.readText('Штрихкод;Цена продажи;Количество;Категория\n4600123456789;250;3;\n4600123456789;250;3;Свой\n9999999999999;10;1;'));
    expect(rows.length, 3, reason: 'файл без столбца названий распознан с заголовком');
    final (out, filled) = await ImportRow.fillNames(rows, g);
    expect(filled, 2);
    expect(out[0].name, 'Кофе из справочника');
    expect(out[0].category, 'Кофе');
    expect(out[0].valid, isTrue);
    expect(out[1].category, 'Свой');
    expect(out[2].name, '');
    final typed = ImportRow(name: 'Моё имя', barcode: '4600123456789', price: 1);
    expect((await ImportRow.fillNames([typed], g)).$1.single.name, 'Моё имя');
  });
  test('встроенный файл assets/data: открывается, 128 276 строк, версия совпадает, код находится', () async {
    final f = File('${dir.path}/real.sqlite')..writeAsBytesSync(File('assets/data/global-products.sqlite').readAsBytesSync());
    final version = File('assets/data/global-products.version').readAsStringSync().trim();
    final g = make(file: f, version: () async => version);
    final st = await g.status();
    expect(st?.count, 128276);
    expect(st?.exportedAt, version);
    expect(st?.added, 0);
    expect((await g.find('5449000000996', network: false))?.name, isNotEmpty);
    expect(await g.find('!', network: false), isNull);
  });
  test('localIfReady: пока справочник не открыт — null без ожидания; после warmUp находит', () async {
    final f = File('${dir.path}/i.sqlite');
    await seed(f, [
      ['4600123456789', 'Кофе'],
    ]);
    final g = make(file: f);
    expect(await g.localIfReady('4600123456789'), isNull);
    expect((await g.status())?.count, 1); // открыли
    expect((await g.localIfReady('4600123456789'))?.name, 'Кофе');
  });
}
