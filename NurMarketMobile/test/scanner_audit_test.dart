import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/scale_reading.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/scan/barcode_lookup.dart';
import 'package:nurmarket/features/scan/shot_flow.dart';

// 2026-10-22 (аудит мобилки, сканеры и умные камеры): поиск по коду как на ПК, повтор скана, весы, совпадение по названию.
String _ean13(String body12) {
  var sum = 0;
  for (var i = 0; i < 12; i++) {
    sum += int.parse(body12[i]) * (i.isEven ? 1 : 3);
  }
  return '$body12${(10 - sum % 10) % 10}';
}

void main() {
  tearDown(() => ScaleRules.current = const ScaleRules());

  test('очистка кода: GS, перевод строки, пробелы внутри цифр', () {
    expect(normalizeCode('\u001d4870202521261\n'), '4870202521261');
    expect(normalizeCode(' 4870 2025 21261 '), '4870202521261');
    expect(normalizeCode('AB 12'), 'AB 12'); // не цифровой — пробелы внутри остаются
  });

  test('написания кода: GTIN-14, UPC-E, русская раскладка', () {
    expect(barcodeVariants('04600000000003'), containsAll(['4600000000003']));
    // UPC-E 04252614 → UPC-A 042100005264
    expect(upcEToUpcA('04252614'), '042100005264');
    expect(barcodeVariants('04252614'), containsAll(['042100005264', '0042100005264']));
    expect(swapRussianLayout('ФИ123'), 'AB123');
    expect(barcodeVariants('ФИ123'), contains('AB123'));
    expect(swapRussianLayout('12345'), isNull);
  });

  test('весовая этикетка: префикс 21 = сумма по правилу компании (Rongta владельца)', () {
    final code = _ean13('210001204000'); // PLU 00012, 04000 → 40,00 сом или 4,000 кг
    expect(parseWeightLabel(code)!.isAmount, isFalse); // по умолчанию 21 — вес
    ScaleRules.current = const ScaleRules(amountPrefixes: {'21'});
    final l = parseWeightLabel(code)!;
    expect(l.isAmount, isTrue);
    expect(l.candidates.first.value, closeTo(40.0, 1e-9));
    // сумма «как есть» и раскладка Rongta (код 6 цифр)
    ScaleRules.current = const ScaleRules(amountPrefixes: {'21'}, amountUnit: 'som', layout: 'code');
    final r = parseWeightLabel(code)!;
    expect(r.candidates.single.code, '000120');
    expect(r.candidates.single.value, 4000);
  });

  test('одежда: этикетка весов — только с явным префиксом', () {
    final code = _ean13('220001201234');
    expect(parseWeightLabel(code, clothing: true), isNull);
    ScaleRules.current = const ScaleRules(weightPrefixes: {'22'});
    expect(parseWeightLabel(code, clothing: true)!.candidates.first.value, closeTo(1.234, 1e-9));
  });

  test('правила весов: из JSON и обратно, мусорные префиксы отброшены', () {
    final r = ScaleRules.fromJson('{"layout":"PLU","mode":"auto","amount":["21","x","35"],"weight":"20, 22"}');
    expect(r.layout, 'plu');
    expect(r.amountPrefixes, {'21'});
    expect(r.weightPrefixes, {'20', '22'});
    expect(ScaleRules.fromJson(r.toJson()).amountPrefixes, {'21'});
    expect(ScaleRules.fromJson('мусор').mode, 'auto');
  });

  test('повтор скана: два кода в кадре не крутятся; приёмка не теряет одинаковые товары', () {
    final t0 = DateTime(2026, 10, 22, 12);
    DateTime at(int ms) => t0.add(Duration(milliseconds: ms));
    final sale = ScanRepeat();
    expect(sale.isNew('A', at(0)), isTrue);
    sale.counted('A', at(0));
    // A держат в кадре: каждые 250 мс — не новый, через 1,5 с — снова (две пачки подряд)
    for (var ms = 250; ms < 1500; ms += 250) {
      expect(sale.isNew('A', at(ms)), isFalse, reason: '$ms мс');
    }
    expect(sale.isNew('A', at(1500)), isTrue);

    final rec = ScanRepeat(receiving: true);
    expect(rec.isNew('A', at(0)), isTrue);
    rec.counted('A', at(0));
    for (var ms = 250; ms <= 3000; ms += 250) {
      expect(rec.isNew('A', at(ms)), isFalse, reason: 'приёмка: тот же товар в кадре не удваивается');
    }
    // убрали из кадра на 0,6 с и поднесли следующий такой же — засчитан (раньше терялся, если быстрее 1,5 с)
    expect(rec.isNew('A', at(3600)), isTrue);
  });

  test('весы: display и единица — 15 g это 15 граммов, а не 15 кг', () {
    expect(parseScaleJson('{"product":"Изюм","display":"15","unit":"g","confidence":0.9}').weightKg, 0.015);
    expect(parseScaleJson('{"product":"Лук","display":"0,500","unit":"kg","confidence":0.9}').weightKg, 0.5);
    expect(parseScaleJson('{"product":"Лук","display":"1.250","unit":"kg","total":"50,00","confidence":0.9}').total, 50);
    // старый ответ: 15 при unit g — граммы
    expect(parseScaleJson('{"product":"Изюм","weight_kg":15,"unit":"g","confidence":0.9}').weightKg, 0.015);
    expect(parseScaleJson('{"product":"Лук","display":null,"unit":"kg","confidence":0}').hasWeight, isFalse);
  });

  test('весы: «Картофель» 1,0 и «Картофель молодой» 0,8 — спросить; стоимость сверяется', () {
    expect(clearWinner([1.0, 0.8]), isFalse);
    expect(clearWinner([1.0, 0.6]), isTrue);
    expect(clearWinner([0.9]), isTrue);
    expect(clearWinner([]), isFalse);
    expect(totalAgrees(20, 40, 0.5), isTrue);
    expect(totalAgrees(40, 40, 0.5), isFalse);
    expect(totalAgrees(null, 40, 0.5), isNull);
  });

  test('совпадение по названию: латиница ↔ кириллица, разный объём — штраф', () {
    expect(VisionService.nameScore('Coca-Cola 1л', 'Кока-Кола 1 л'), greaterThan(0.5));
    expect(VisionService.nameScore('Кока-Кола 1л', 'Кока-Кола 2л'), lessThanOrEqualTo(0.4));
    expect(VisionService.nameScore('Кока-Кола 500 мл', 'Кока-Кола 0,5 л'), greaterThan(0.5));
    expect(VisionService.validEan('4870202521261'), isTrue);
    expect(VisionService.validEan('4870202521262'), isFalse);
    expect(VisionService.validEan('123'), isFalse);
  });

  test('число с неразрывным пробелом', () {
    expect(parseNum('1 250,50'), 1250.5);
    expect(parseNum('1 000'), 1000);
  });

  // весовая этикетка ищется одним запросом по коду (PLU «12» = «00012»); «_» в коде Code128 — не шаблон LIKE
  test('поиск по коду в базе телефона: этикетка весов, доп. штрихкод с «_», приёмка без этикеток', () async {
    final db = AppDatabase(NativeDatabase.memory());
    await db.replaceProducts([
      ProductsCompanion.insert(id: 'w', name: 'Картофель', price: const Value(40), plu: const Value('12'), isWeight: const Value(true)),
      ProductsCompanion.insert(id: 'a', name: 'Болт', altBarcodes: const Value('AB_12')),
      ProductsCompanion.insert(id: 'b', name: 'Гайка', altBarcodes: const Value('ABX12')),
    ]);
    final label = _ean13('220001200500'); // PLU 00012, 0,500 кг
    final hit = await db.productByScan(label);
    expect(hit!.product.id, 'w');
    expect(hit.qty, 0.5);
    expect(await db.productByScan(label, allowWeightLabel: false), isNull);
    ScaleRules.current = const ScaleRules(amountPrefixes: {'22'});
    expect((await db.productByScan(label))!.qty, closeTo(5.0 / 40, 1e-3)); // 5,00 сом ÷ 40 сом/кг
    expect((await db.productByScan('AB_12'))!.product.id, 'a');
    expect((await db.productByScan('ABX12'))!.product.id, 'b');
    await db.close();
  });
}
