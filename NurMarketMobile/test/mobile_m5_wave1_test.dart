import 'dart:typed_data';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/print/printer_service.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/features/sale/cart.dart';
import 'package:nurmarket/features/scan/barcode_lookup.dart';
import 'package:nurmarket/features/scan/smart_camera.dart' show saleConfident;
import 'package:nurmarket/features/scan/scale_label_flow.dart';
import 'package:nurmarket/features/scan/shot_flow.dart';

// 2026-11-05 (мобилка, М5-01, М5-11…М5-16): двойной чек по Bluetooth, поиск весовой этикетки только среди весовых товаров,
// «код товара», повтор той же этикетки. Только чистая логика и база в памяти (боевая база не нужна).
String _ean13(String body12) {
  var sum = 0;
  for (var i = 0; i < 12; i++) {
    sum += int.parse(body12[i]) * (i.isEven ? 1 : 3);
  }
  return '$body12${(10 - sum % 10) % 10}';
}

void main() {
  tearDown(() => ScaleRules.current = const ScaleRules());

  group('М5-01 двойной чек по Bluetooth', () {
    const c = PrinterConfig(kind: PrinterKind.bluetooth, address: 'AA:BB:CC:DD:EE:FF');

    test('нет ответа Android в срок (stalled) — второго btPrint нет', () async {
      final s = PrinterService(null)..retryDelay = Duration.zero;
      var calls = 0;
      s.sendOverride = (_, _) async {
        calls++;
        throw const PrinterException(PrinterProblem.stalled, '', true);
      };
      await expectLater(s.send(c, Uint8List(3)), throwsA(isA<PrinterException>().having((e) => e.problem, 'problem', PrinterProblem.stalled)));
      expect(calls, 1);
    });

    test('stalled даже с sent = false повтора не получает', () async {
      final s = PrinterService(null)..retryDelay = Duration.zero;
      var calls = 0;
      s.sendOverride = (_, _) async {
        calls++;
        throw const PrinterException(PrinterProblem.stalled);
      };
      await expectLater(s.send(c, Uint8List(3)), throwsA(isA<PrinterException>()));
      expect(calls, 1);
    });

    test('подтверждённый отказ подключения (timeout, байты не ушли) — один повтор', () async {
      final s = PrinterService(null)..retryDelay = Duration.zero;
      var calls = 0;
      s.sendOverride = (_, _) async {
        calls++;
        if (calls == 1) throw const PrinterException(PrinterProblem.timeout);
      };
      await s.send(c, Uint8List(3));
      expect(calls, 2);
    });

    test('срок Dart больше срока Android на подключение (15 с)', () {
      expect(PrinterService.btTimeout(0), greaterThan(const Duration(seconds: 30)));
      expect(PrinterService.btTimeout(100000), greaterThan(PrinterService.btTimeout(1000)));
    });
  });

  group('М5-11/14/15/16 весовая этикетка', () {
    late AppDatabase db;
    setUp(() => db = AppDatabase(NativeDatabase.memory()));
    tearDown(() => db.close());

    test('PLU 12 у штучной «Жвачки» (артикул 12) не мешает: этикетка находит весовой «Картофель»', () async {
      await db.replaceProducts([
        ProductsCompanion.insert(id: 'gum', name: 'Жвачка', price: const Value(30), sku: const Value('12')),
        ProductsCompanion.insert(id: 'pot', name: 'Картофель', price: const Value(40), plu: const Value('12'), isWeight: const Value(true)),
      ]);
      final label = _ean13('200001200734'); // PLU 00012, 0,734 кг
      final hit = await db.productByScan(label);
      expect(hit!.product.id, 'pot');
      expect(hit.qty, 0.734);
    });

    test('этикетка штучного товара — не находит его молча; с includePiece — признак weighed = false', () async {
      await db.replaceProducts([ProductsCompanion.insert(id: 'gum', name: 'Жвачка', price: const Value(30), plu: const Value('12'))]);
      final label = _ean13('200001200734');
      expect(await db.productByScan(label), isNull);
      expect((await db.weightLabelHits(label)), isEmpty);
      final piece = await db.weightLabelHits(label, includePiece: true);
      expect(piece.single.product.id, 'gum');
      expect(piece.single.weighed, isFalse);
    });

    test('неизвестный PLU — пусто, а это всё же весовая этикетка (сервер не нужен)', () async {
      final label = _ean13('200999000500');
      expect(parseWeightLabel(label), isNotNull);
      expect(await db.productByScan(label), isNull);
      expect(await db.weightLabelHits(label, includePiece: true), isEmpty);
    });

    test('«по сумме», цена 0 — товар найден, вес не известен (впишут руками)', () async {
      ScaleRules.current = const ScaleRules(amountPrefixes: {'22'});
      await db.replaceProducts([ProductsCompanion.insert(id: 'pot', name: 'Картофель', plu: const Value('12'), isWeight: const Value(true))]);
      final hit = await db.productByScan(_ean13('220001200500'));
      expect(hit!.product.id, 'pot');
      expect(hit.qty, isNull);
    });

    test('«код товара» находится, даже когда заполнен артикул (раскладка по коду)', () async {
      ScaleRules.current = const ScaleRules(layout: 'code');
      await db.replaceProducts([
        ProductsCompanion.insert(id: 'ap', name: 'Яблоки', price: const Value(100), sku: const Value('0073'), productCode: const Value('0082'), isWeight: const Value(true)),
      ]);
      final hit = await db.productByScan(_ean13('210000824000')); // код 000082, 4,000 кг
      expect(hit!.product.id, 'ap');
      expect(hit.qty, 4);
    });

    test('весовой признак сервера (isWeightFlag) тоже считается весовым', () async {
      await db.replaceProducts([ProductsCompanion.insert(id: 'x', name: 'Орехи', plu: const Value('7'), isWeightFlag: const Value(true))]);
      expect((await db.productByScan(_ean13('200000700250')))!.product.id, 'x');
    });

    test('раскладка не задана и два прочтения нашли разные товары — в списке оба (сканер даёт выбрать)', () async {
      await db.replaceProducts([
        ProductsCompanion.insert(id: 'a', name: 'А', price: const Value(10), plu: const Value('12'), isWeight: const Value(true)),
        ProductsCompanion.insert(id: 'b', name: 'Б', price: const Value(10), sku: const Value('000123'), isWeight: const Value(true)),
      ]);
      // 21 | 00012 | 34000 | ... читается и как PLU 00012 (34 кг), и как код 000123 (4,000 кг)
      final label = _ean13('210001234000');
      final hits = await db.weightLabelHits(label);
      expect(hits.map((h) => h.product.id), ['a', 'b']);
      expect(hits.first.qty, 34);
      expect(hits.last.qty, 4);
      // раскладка задана — гадания нет
      ScaleRules.current = const ScaleRules(layout: 'plu');
      expect((await db.weightLabelHits(label)).map((h) => h.product.id), ['a']);
    });

    test('схема 10: колонка product_code есть, версия схемы 10', () async {
      expect(db.schemaVersion, 10);
      await db.replaceProducts([ProductsCompanion.insert(id: 'p', name: 'П', productCode: const Value('55'))]);
      expect((await db.productById('p'))!.productCode, '55');
    });
  });

  group('М4-14 «Округление весовых», М5-26 уверенный ответ ИИ', () {
    test('сумма весовых строк округляется вниз: 4,875 + 22,638 = 27,513 → 27,51, округление 0,003', () {
      final cart = Cart(lines: [
        // 2026-11-05: количество весового — граммы (3 знака): 0,375 кг × 13 = 4,875; 0,343 кг × 66 = 22,638
        CartLine(productId: 'a', name: 'А', price: 13, qty: 0.375, weightFlag: true),
        CartLine(productId: 'b', name: 'Б', price: 66, qty: 0.343, weightFlag: true),
      ]);
      expect(cart.subtotal, 27.51);
      expect(cart.weightRounding, closeTo(0.003, 1e-9));
      expect(const Cart().weightRounding, 0);
    });

    test('ИИ уверен только если у каждой позиции есть автовыбор', () {
      final p = Product(
        id: '1', name: 'Кола', price: 10, wholesalePrice: 0, altBarcodes: '', unit: 'шт', isWeight: false, isService: false, isFavorite: false,
        quantity: 1, category: '', searchKey: '', promoRules: '', minQuantity: 0,
      );
      expect(saleConfident([SeenMatch(const SeenItem(name: 'Кола'), [(p, 0.95)])]), isTrue);
      expect(saleConfident([SeenMatch(const SeenItem(name: 'Кола'), [(p, 0.95), (p, 0.9)])]), isFalse);
      expect(saleConfident([SeenMatch(const SeenItem(name: 'Кола'), [(p, 0.6)])]), isFalse);
      expect(saleConfident(const []), isFalse);
    });
  });

  group('М5-13 повтор той же этикетки', () {
    test('память этикеток: код в чеке помнится, пустой чек забывает', () {
      final m = ScaleLabelMemory()..add('2000012007345');
      m.syncWithCart(cartEmpty: false);
      expect(m.contains('2000012007345'), isTrue);
      m.syncWithCart(cartEmpty: true);
      expect(m.contains('2000012007345'), isFalse);
    });

    test('этикетка из чека в кадре 5 с — не «новый скан» (ждёт ухода из кадра)', () {
      final r = ScanRepeat(receiving: true);
      final t0 = DateTime(2026, 11, 5, 10);
      expect(r.isNew('X', t0), isTrue);
      r.counted('X', t0);
      for (var ms = 100; ms <= 5000; ms += 100) {
        expect(r.isNew('X', t0.add(Duration(milliseconds: ms))), isFalse, reason: '$ms мс');
      }
      // ушла из кадра и вернулась
      expect(r.isNew('X', t0.add(const Duration(seconds: 6))), isTrue);
    });

    test('штучный товар — как раньше: тот же код снова через 1,5 с', () {
      final r = ScanRepeat();
      final t0 = DateTime(2026, 11, 5, 10);
      expect(r.isNew('Y', t0), isTrue);
      r.counted('Y', t0);
      // 2026-11-05: код держат в кадре — кадры идут каждые 100 мс (разрыв ≥ 0,5 с считался бы «ушёл из кадра»).
      for (var ms = 100; ms <= 1400; ms += 100) {
        expect(r.isNew('Y', t0.add(Duration(milliseconds: ms))), isFalse, reason: '$ms мс');
      }
      expect(r.isNew('Y', t0.add(const Duration(milliseconds: 1600))), isTrue);
    });
  });
}
