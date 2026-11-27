import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/util/numeric_focus.dart';

/// 2026-11-26 (владелец: «0 не убирается автоматически при создании товара в количестве!»): общий NumericFocus — фокус выделяет «0» целиком
/// (касание и программно), цифра заменяет ноль, «0,5» набирается как есть, пустое поле при уходе снова «0», обычный текст не затрагивается.
void main() {
  setUp(NumericFocus.install);
  tearDown(NumericFocus.uninstall);

  Widget app(TextEditingController qty, TextEditingController price, TextEditingController name) => MaterialApp(
        home: Scaffold(
          body: Column(children: [
            TextField(key: const Key('qty'), controller: qty, keyboardType: const TextInputType.numberWithOptions(decimal: true)),
            TextField(key: const Key('price'), controller: price, keyboardType: TextInputType.number),
            TextField(key: const Key('name'), controller: name),
          ]),
        ),
      );

  testWidgets('касание поля с «0» выделяет ноль; «5» даёт «5», «0,5» — «0,5»', (tester) async {
    final qty = TextEditingController(text: '0'), price = TextEditingController(), name = TextEditingController(text: '0');
    await tester.pumpWidget(app(qty, price, name));
    await tester.tap(find.byKey(const Key('qty')));
    await tester.pumpAndSettle();
    expect(qty.selection.start, 0);
    expect(qty.selection.end, 1);
    tester.testTextInput.enterText('5'); // выделенный «0» заменён вводом
    await tester.pump();
    expect(qty.text, '5');

    qty.text = '0';
    await tester.tap(find.byKey(const Key('price')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('qty')));
    await tester.pumpAndSettle();
    expect(qty.selection.end - qty.selection.start, 1);
    tester.testTextInput.enterText('0,5');
    await tester.pump();
    expect(qty.text, '0,5');
  });

  testWidgets('пустое поле, в которое вошли с «0», при уходе снова «0»; число в поле цены выделяется целиком', (tester) async {
    final qty = TextEditingController(text: '0'), price = TextEditingController(text: '150'), name = TextEditingController(text: 'Молоко');
    await tester.pumpWidget(app(qty, price, name));
    await tester.tap(find.byKey(const Key('qty')));
    await tester.pumpAndSettle();
    tester.testTextInput.enterText('');
    await tester.pump();
    expect(qty.text, '');
    await tester.tap(find.byKey(const Key('price')));
    await tester.pumpAndSettle();
    expect(qty.text, '0', reason: 'пустое количество при уходе — «0»');
    expect(price.selection.end - price.selection.start, 3, reason: 'число в поле цены выделено целиком');
  });

  testWidgets('обычное текстовое поле выделением не затрагивается', (tester) async {
    final qty = TextEditingController(text: '0'), price = TextEditingController(), name = TextEditingController(text: '0');
    await tester.pumpWidget(app(qty, price, name));
    await tester.tap(find.byKey(const Key('name')));
    await tester.pumpAndSettle();
    expect(name.selection.isCollapsed, isTrue);
  });

  test('isZeroText', () {
    for (final t in ['0', '0,00', '0.0', ' 00 ']) {
      expect(NumericFocus.isZeroText(t), isTrue, reason: t);
    }
    for (final t in ['0,5', '10', '', null]) {
      expect(NumericFocus.isZeroText(t), isFalse, reason: '$t');
    }
  });
}
