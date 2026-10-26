import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/util/barcode_generator.dart';
import 'package:nurmarket/features/scan/barcode_lookup.dart';

// 2026-10-26 (требование владельца): генератор своего штрихкода — EAN-13, не с 0, не весовой, без повторов. Только фейки, без сети.
void main() {
  test('1000 кодов: 13 цифр, не с 0 и не с 2, контрольная верна, на весовую этикетку не похожи', () {
    final r = Random(42);
    final seen = <String>{};
    for (var i = 0; i < 1000; i++) {
      final c = BarcodeGenerator.create(r);
      expect(c.length, 13, reason: c);
      expect(RegExp(r'^\d{13}$').hasMatch(c), isTrue, reason: c);
      expect(c[0], isNot('0'), reason: c);
      expect(c[0], isNot('2'), reason: c);
      expect(validEan13(c), isTrue, reason: c);
      expect(parseWeightLabel(c, rules: const ScaleRules()), isNull, reason: c);
      // и при любых префиксах «вес/сумма» компании: весовой разбор требует первую цифру 2
      expect(parseWeightLabel(c, rules: const ScaleRules(weightPrefixes: {'20', '21', '29'}, amountPrefixes: {'22', '25'})), isNull, reason: c);
      seen.add(c);
    }
    expect(seen.length, greaterThan(995));
  });

  test('системный Random.secure тоже даёт верные коды', () {
    for (var i = 0; i < 200; i++) {
      final c = BarcodeGenerator.create();
      expect(c[0], isNot('0'));
      expect(c[0], isNot('2'));
      expect(validEan13(c), isTrue);
    }
  });

  test('первые цифры — все допустимые 1,3..9, запрещённых нет', () {
    final r = Random(7);
    final firsts = {for (var i = 0; i < 2000; i++) BarcodeGenerator.create(r)[0]};
    expect(firsts, {'1', '3', '4', '5', '6', '7', '8', '9'});
  });

  test('1000 кодов с занятыми: повторов с занятыми нет', () async {
    final r = Random(1);
    final taken = <String>{for (var i = 0; i < 300; i++) BarcodeGenerator.create(r)};
    final made = <String>{};
    for (var i = 0; i < 1000; i++) {
      final c = await BarcodeGenerator.createFree((code) async => taken.contains(code) || made.contains(code), random: r);
      expect(taken.contains(c), isFalse, reason: c);
      expect(made.add(c), isTrue, reason: c);
      expect(c[0], isNot('0'));
      expect(validEan13(c), isTrue);
    }
  });

  test('код занят — генерируется заново, не больше 10 проверок', () async {
    var calls = 0;
    final c = await BarcodeGenerator.createFree((code) async {
      calls++;
      return true; // всё занято
    }, random: Random(3));
    expect(calls, BarcodeGenerator.maxAttempts);
    expect(validEan13(c), isTrue);
  });

  test('первый код занят, второй свободен — отдан второй', () async {
    final r = Random(5);
    final first = BarcodeGenerator.create(Random(5));
    final c = await BarcodeGenerator.createFree((code) async => code == first, random: r);
    expect(c, isNot(first));
  });

  test('нет связи (проверка упала) — код отдаётся без проверки', () async {
    final c = await BarcodeGenerator.createFree((code) async => throw Exception('нет связи'), random: Random(9));
    expect(c.length, 13);
    expect(validEan13(c), isTrue);
  });
}
