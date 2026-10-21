import 'dart:math';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'dart:typed_data';

import 'package:nurmarket/features/payment/payment_qr.dart';
import 'package:nurmarket/features/stock/bulk_import.dart';
import 'package:nurmarket/features/sale/cart.dart';

// 14.10, владелец: «проведи стресс-тест мобилки». Телефон не подключён — нагрузка на логику: большой каталог,
// длинные чеки со случайными скидками и количествами, QR с суммой (ELQR).
void main() {
  test('20 000 товаров: поиск быстрый и находит всё', () async {
    final db = AppDatabase(NativeDatabase.memory());
    final rnd = Random(1);
    const words = ['молоко', 'хлеб', 'сок', 'вода', 'кола', 'чай', 'кофе', 'сыр', 'масло', 'сахар'];
    await db.replaceProducts([
      for (var i = 0; i < 20000; i++)
        () {
          final name = '${words[rnd.nextInt(words.length)]} ${words[rnd.nextInt(words.length)]} $i';
          return ProductsCompanion.insert(
            id: '$i',
            name: name,
            barcode: Value('46${i.toString().padLeft(11, '0')}'),
            price: Value(1 + rnd.nextInt(5000).toDouble()),
            quantity: Value(rnd.nextInt(50).toDouble()),
            searchKey: Value('$name 46${i.toString().padLeft(11, '0')}'),
          );
        }(),
    ]);
    final sw = Stopwatch()..start();
    for (var k = 0; k < 50; k++) {
      await db.searchProducts(words[k % words.length], limit: 200);
    }
    final perSearch = sw.elapsedMilliseconds / 50;
    expect(perSearch, lessThan(150), reason: 'поиск по 20 000 товаров: $perSearch мс');
    expect((await db.searchProducts('4600000012345')).single.id, '12345');
    expect((await db.searchProducts('', limit: 20000)).length, 20000);
    await db.close();
  });

  test('500 случайных чеков: итог никогда не отрицательный и сходится со строками', () {
    final rnd = Random(7);
    for (var n = 0; n < 500; n++) {
      var cart = const Cart();
      final lines = 1 + rnd.nextInt(300);
      for (var i = 0; i < lines; i++) {
        final weight = rnd.nextInt(5) == 0;
        cart = cart.add(
          CartLine(
            productId: 'p${rnd.nextInt(80)}',
            name: 'Товар',
            price: roundMoney(rnd.nextDouble() * 3000),
            qty: weight ? roundMoney(rnd.nextDouble() * 5) + 0.01 : 1 + rnd.nextInt(5).toDouble(),
            isWeight: weight,
            wholesalePrice: rnd.nextBool() ? roundMoney(rnd.nextDouble() * 2000) : 0,
          ),
        );
      }
      // случайные скидки строк и смена количества (вниз — скидка не должна увести строку в минус)
      for (var i = 0; i < cart.lines.length; i++) {
        if (rnd.nextInt(3) == 0) cart.lines[i].discount = roundMoney(cart.lines[i].gross * rnd.nextDouble());
        if (rnd.nextInt(4) == 0) cart = cart.setQty(i, cart.lines[i].isWeight ? 0.05 : 1);
        if (i < cart.lines.length && rnd.nextInt(5) == 0) cart.lines[i].setWholesale(true);
      }
      cart = cart.copyWith(orderDiscountPercent: rnd.nextInt(3) == 0 ? rnd.nextInt(120).toDouble() : 0, orderDiscountSum: rnd.nextInt(3) == 0 ? rnd.nextDouble() * 1e6 : 0);
      for (final l in cart.lines) {
        expect(l.total, greaterThanOrEqualTo(0), reason: 'строка ушла в минус: ${l.gross} − ${l.discount}');
      }
      expect(cart.total, greaterThanOrEqualTo(0));
      final sumLines = cart.lines.fold(0.0, (s, l) => roundMoney(s + l.total));
      expect((cart.total - roundMoney(sumLines - cart.orderDiscount)).abs(), lessThan(0.011));
      expect(cart.orderDiscount, lessThanOrEqualTo(sumLines + 0.001));
    }
  });

  group('ELQR: QR с суммой чека', () {
    // Синтетический QR в формате ELQR (как у MBank): 00 формат, 26 получатель, 53 валюта, 59 имя, 63 контрольная сумма.
    String sample() {
      final fields = [('00', '01'), ('01', '11'), ('26', '0012p2p.mbank.kg0110123456789'), ('52', '0000'), ('53', '417'), ('59', 'NUR TEST'), ('60', 'BISHKEK')];
      final body = fields.map((f) => '${f.$1}${f.$2.length.toString().padLeft(2, '0')}${f.$2}').join();
      return 'https://app.mbank.kg/qr/#${Uri.encodeComponent('${body}6304${Elqr.checksum(body)}')}';
    }

    test('сумма в тег 54 перед тегом 59, контрольная сумма пересчитана', () {
      final link = Elqr.buildWithAmount(sample(), 200)!;
      expect(link.startsWith('https://app.mbank.kg/qr/#'), isTrue);
      final payload = Uri.decodeComponent(link.substring(link.indexOf('#') + 1));
      final f = Elqr.parse(payload);
      final tags = [for (final x in f) x.$1];
      expect(f.firstWhere((x) => x.$1 == '54').$2, '20000');
      expect(tags.indexOf('54'), lessThan(tags.indexOf('59')));
      expect(tags.last, '63');
      final body = payload.substring(0, payload.length - 8);
      expect(f.last.$2, Elqr.checksum(body));
    });

    test('1000 случайных сумм: всегда валидный QR, повторная сборка не дублирует сумму', () {
      final rnd = Random(3);
      for (var i = 0; i < 1000; i++) {
        final amount = roundMoney(0.01 + rnd.nextDouble() * 200000);
        final once = Elqr.buildWithAmount(sample(), amount)!;
        final twice = Elqr.buildWithAmount(once, amount)!;
        expect(twice, once);
        final f = Elqr.parse(Uri.decodeComponent(once.substring(once.indexOf('#') + 1)));
        expect(f.where((x) => x.$1 == '54').length, 1);
        expect(int.parse(f.firstWhere((x) => x.$1 == '54').$2), (amount * 100).round());
      }
    });

    test('не ELQR и нулевая сумма — показываем QR как есть', () {
      expect(Elqr.buildWithAmount('https://example.com/pay', 100), isNull);
      expect(Elqr.buildWithAmount(sample(), 0), isNull);
      final q = PayQr(id: '1', bank: 'X', payload: 'https://example.com/pay');
      expect(q.forAmount(100), 'https://example.com/pay');
    });

    test('кому показывать QR', () {
      final all = PayQr(id: '1', bank: 'MBank', payload: 'x');
      final one = PayQr(id: '2', bank: 'Optima', payload: 'y', cashiers: ['u1']);
      expect(all.visibleTo('u9'), isTrue);
      expect(one.visibleTo('u1'), isTrue);
      expect(one.visibleTo('u2'), isFalse);
      expect(PayQr.fromJson(one.toJson()).cashiers, ['u1']);
    });
  });

  test('массовая загрузка: мусор вместо файла не роняет программу', () {
    final rnd = Random(11);
    final alphabet = 'abcАБВ0123456789;,". -${String.fromCharCodes([9, 10, 13])}';
    for (var i = 0; i < 300; i++) {
      final text = String.fromCharCodes([for (var k = 0; k < rnd.nextInt(2000); k++) alphabet.codeUnitAt(rnd.nextInt(alphabet.length))]);
      expect(() => BulkParser.parse(BulkParser.readText(text)), returnsNormally);
      final bytes = Uint8List.fromList([for (var k = 0; k < rnd.nextInt(500); k++) rnd.nextInt(256)]);
      expect(() => BulkParser.readXlsx(bytes), returnsNormally);
    }
  });
}
