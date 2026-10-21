import 'dart:io';
import 'dart:math';
import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:drift/native.dart';
import 'package:flutter/painting.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/product_refs.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/product_picker.dart';
import 'package:nurmarket/features/payment/payment_qr.dart';

// 14.10, владелец: «все новые функции — стресс-тест». Журнал аудита, тема по времени, фильтры товаров, QR с суммой
// (совпадение с десктопом), эталоны умной камеры. Без телефона и без сервера.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('журнал аудита: 6000 записей подряд и вперемешку — порядок, обрезка до 5000, кириллица', () async {
    final dir = await Directory.systemTemp.createTemp('audit');
    Audit.testDirectory = dir;
    Audit.currentUser = 'Кассир «Тест»';
    final sw = Stopwatch()..start();
    await Future.wait([for (var i = 0; i < 6000; i++) Audit.log(Audit.kinds[i % Audit.kinds.length], 'Действие №$i "кавычки"\nперенос', amount: i.toDouble())]);
    final written = sw.elapsedMilliseconds;
    final all = await Audit.read();
    expect(all.length, 5000);
    expect(all.first.text, startsWith('Действие №5999'));
    expect(all.first.user, 'Кассир «Тест»');
    expect(all.first.amount, 5999);
    for (var i = 1; i < all.length; i++) {
      expect(all[i].at.isAfter(all[i - 1].at), isFalse);
    }
    final again = await Audit.read(); // файл уже обрезан
    expect(again.length, 5000);
    // испорченная строка в файле не мешает чтению
    await File('${dir.path}/audit.jsonl').writeAsString('{не json\n', mode: FileMode.append);
    await Audit.log('sale', 'после порчи');
    expect((await Audit.read()).first.text, 'после порчи');
    // ignore: avoid_print
    print('журнал: 6000 записей за $written мс');
    await dir.delete(recursive: true);
  });

  test('тема «По времени»: все минуты суток, интервал через полночь и днём', () {
    const night = Prefs(auto: true, darkFrom: 19 * 60, darkTo: 7 * 60);
    const day = Prefs(auto: true, darkFrom: 9 * 60, darkTo: 17 * 60);
    for (var m = 0; m < 24 * 60; m++) {
      final t = DateTime(2026, 10, 14, m ~/ 60, m % 60);
      expect(night.darkAt(t), m >= 19 * 60 || m < 7 * 60, reason: 'ночь, минута $m');
      expect(day.darkAt(t), m >= 9 * 60 && m < 17 * 60, reason: 'день, минута $m');
    }
    const same = Prefs(auto: true, darkFrom: 600, darkTo: 600);
    expect(same.darkAt(DateTime(2026, 1, 1, 10)), isFalse);
  });

  test('фильтры товаров: 20 000 товаров, каждый фильтр — ровно своё', () {
    final rnd = Random(5);
    final products = [
      for (var i = 0; i < 20000; i++)
        Product(
          id: '$i',
          name: 'p$i',
          price: 10,
          wholesalePrice: 0,
          promoRules: '',
          quantity: (rnd.nextInt(12) - 3).toDouble(),
          unit: 'шт',
          isWeight: false,
          isService: i % 50 == 0,
          isFavorite: false,
          altBarcodes: '',
          category: '',
          searchKey: 'p$i',
        ),
    ];
    final inStock = products.where((p) => stockFilterMatch(p, StockFilter.inStock)).length;
    final low = products.where((p) => stockFilterMatch(p, StockFilter.low)).length;
    final out = products.where((p) => stockFilterMatch(p, StockFilter.out)).length;
    expect(inStock, products.where((p) => p.isService || p.quantity > 0).length);
    expect(low, products.where((p) => !p.isService && p.quantity > 0 && p.quantity <= 3).length);
    expect(out, products.where((p) => !p.isService && p.quantity <= 0).length);
    expect(products.where((p) => stockFilterMatch(p, StockFilter.all)).length, 20000);
  });

  test('QR с суммой: телефон собирает ровно то же, что касса (1000 сумм из стресс-теста десктопа)', () {
    final f = File('${Directory.systemTemp.path}/nm_stress/elqr_cs.tsv');
    if (!f.existsSync()) {
      markTestSkipped('нет файла десктопа — сначала стресс-тест десктопа');
      return;
    }
    final lines = f.readAsLinesSync();
    final link = lines.first;
    var n = 0;
    for (final l in lines.skip(1).where((x) => x.isNotEmpty)) {
      final parts = l.split('\t');
      expect(Elqr.buildWithAmount(link, double.parse(parts[0])), parts[1], reason: 'сумма ${parts[0]}');
      n++;
    }
    expect(n, 1000);
  });

  testWidgets('эталоны умной камеры: 40 товаров по 3 снимка, перезапись, загрузка', (tester) async {
    await tester.runAsync(() async {
      final dir = await Directory.systemTemp.createTemp('refs');
      ProductRefs.testDirectory = dir;
      final db = AppDatabase(NativeDatabase.memory());
      Future<Uint8List> png(int seed) async {
        final rec = ui.PictureRecorder();
        Canvas(rec).drawRect(const Rect.fromLTWH(0, 0, 800, 600), Paint()..color = Color(0xFF000000 | (seed * 7919 & 0xFFFFFF)));
        final img = await rec.endRecording().toImage(800, 600);
        return (await img.toByteData(format: ui.ImageByteFormat.png))!.buffer.asUint8List();
      }

      final shot = await png(1);
      for (var i = 0; i < 40; i++) {
        await ProductRefs.save(db, 'p$i', [shot, shot, shot]);
      }
      expect((await ProductRefs.trainedIds(db)).length, 40);
      final refs = await ProductRefs.load(db, 'p7');
      expect(refs.length, 3);
      final codec = await ui.instantiateImageCodec(refs.first);
      expect((await codec.getNextFrame()).image.width, 384); // уменьшено до 384 px
      // повторное обучение заменяет снимки, а не добавляет
      await ProductRefs.save(db, 'p7', [shot]);
      expect((await ProductRefs.load(db, 'p7')).length, 1);
      expect(Directory('${dir.path}/refs').listSync().length, 40 * 3 - 2);
      await db.close();
      await dir.delete(recursive: true);
    });
  });
}
