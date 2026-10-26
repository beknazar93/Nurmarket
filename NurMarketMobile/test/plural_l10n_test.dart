// 2026-10-26 (живой аудит, П0): склонения счётчиков (ICU plural) и полнота ключей во всех 5 языках.
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

Future<AppLocalizations> _load(String lang) => AppLocalizations.delegate.load(Locale(lang));

void main() {
  test('ru: чеки и позиции склоняются (1/2/5/11/21/22)', () async {
    final l = await _load('ru');
    expect(l.checksCount(1), '1 чек');
    expect(l.checksCount(2), '2 чека');
    expect(l.checksCount(5), '5 чеков');
    expect(l.checksCount(11), '11 чеков');
    expect(l.checksCount(21), '21 чек');
    expect(l.checksCount(22), '22 чека');
    expect(l.positions(1), '1 позиция');
    expect(l.positions(2), '2 позиции');
    expect(l.positions(5), '5 позиций');
    expect(l.positions(21), '21 позиция');
    expect(l.positions(0), '0 позиций');
    expect(l.runningOut(1), 'Заканчивается 1 товар');
    expect(l.runningOut(3), 'Заканчиваются 3 товара');
    expect(l.runningOut(39), 'Заканчиваются 39 товаров');
    expect(l.goodsPieces(21, '5'), '21 товар · 5 шт');
    expect(l.smartUpload(2), 'Загрузить 2 товара на склад');
    expect(l.closeShiftPending(1), contains('1 чек '));
    expect(l.closeShiftPending(3), contains('3 чека'));
    expect(l.closeShiftPending(5), contains('5 чеков'));
    expect(l.logoutPending(1), contains('1 неотправленная запись'));
    expect(l.logoutPending(2), contains('2 неотправленные записи'));
    expect(l.logoutPending(5), contains('5 неотправленных записей'));
    // Никогда «3 чеков», «1 позиций», «2 позиций».
    for (var n = 0; n <= 130; n++) {
      final c = l.checksCount(n);
      if (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14)) expect(c, endsWith('чека'), reason: '$n');
      if (n % 10 == 1 && n % 100 != 11) expect(c, endsWith('чек'), reason: '$n');
      final p = l.positions(n);
      if (n % 10 == 1 && n % 100 != 11) expect(p, endsWith('позиция'), reason: '$n');
    }
  });

  test('uz: «N ta chek» для 1/2/5/21', () async {
    final l = await _load('uz');
    expect(l.checksCount(1), '1 ta chek');
    expect(l.checksCount(2), '2 ta chek');
    expect(l.checksCount(5), '5 ta chek');
    expect(l.checksCount(21), '21 ta chek');
    expect(l.positions(2), '2 ta pozitsiya');
  });

  test('en: one/other', () async {
    final l = await _load('en');
    expect(l.checksCount(1), '1 receipt');
    expect(l.checksCount(2), '2 receipts');
    expect(l.checksCount(21), '21 receipts');
    expect(l.positions(1), '1 item');
    expect(l.positions(5), '5 items');
  });

  test('ky: «Ижара» вместо «Прокат»', () async {
    final l = await _load('ky');
    expect(l.rentals, 'Ижара');
    expect(l.rentalNew, isNot(contains('рокат')));
    expect(l.rentalCreated(5), isNot(contains('рокат')));
    expect(l.rentalLine(5, '2'), isNot(contains('Прокат')));
    expect(l.rentalLateHint(2, '10', '20'), isNot(contains('рокат')));
    expect(l.rentalPenaltyLine(7), isNot(contains('рокат')));
  });

  test('ключи есть во всех 5 языках и с одинаковыми параметрами', () {
    Map<String, dynamic> read(String lang) => jsonDecode(File('lib/l10n/app_$lang.arb').readAsStringSync()) as Map<String, dynamic>;
    final ru = read('ru');
    final keys = ru.keys.where((k) => !k.startsWith('@')).toSet();
    expect(keys.length, greaterThan(1000));
    for (final lang in ['ky', 'en', 'tr', 'uz']) {
      final other = read(lang);
      final ok = other.keys.where((k) => !k.startsWith('@')).toSet();
      expect(keys.difference(ok), isEmpty, reason: '$lang: нет ключей');
      expect(ok.difference(keys), isEmpty, reason: '$lang: лишние ключи');
      for (final k in keys) {
        final a = (ru[k] as String);
        final b = (other[k] as String);
        expect(b.trim(), isNotEmpty, reason: '$lang.$k пусто');
        // Параметры верхнего уровня совпадают с русским (без ICU-слов plural/one/few/many/other).
        Set<String> params(String s) => RegExp(r'\{(\w+)\}').allMatches(s).map((m) => m.group(1)!).toSet();
        expect(params(b), params(a), reason: '$lang.$k: параметры');
      }
    }
  });
}
