import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/features/payment/receipt_pdf.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/receipt/receipt_view.dart';

const _session = Session(email: 'a@b.kg', access: '', refresh: '', userName: 'Нур Тест', companyName: 'NBS Маркет', companyInn: '01234567891011', companyAddress: 'г. Бишкек, ул. Киевская 95');

Map<String, dynamic> _sale({bool fiscal = false}) => {
  'created_at': '2026-10-13T09:15:00+06:00',
  'shift': {'number': '42'},
  'cashier_name': 'Нур Тест',
  'cash_received': 2000,
  'items': [
    {'product_name': 'Макароны «Макфа» спагетти 450г', 'quantity': 2, 'unit_price': 85, 'total': 170},
    {'product_name': 'Кока Кола 2 л', 'quantity': 1, 'unit_price': 170, 'total': 150},
    {'product_name': 'Яблоки', 'quantity': 1.25, 'unit': 'кг', 'unit_price': 120, 'total': 150},
  ],
  if (fiscal) 'fiscal': {'fd': '1234', 'fpd': '987654321', 'reg_number': '0000123456', 'fiscal_module': 'FM0001', 'taxes': [{'name': 'НДС 12%', 'amount': 50.36}, {'name': 'НсП 2%', 'amount': 9.4}]},
  'e_receipt': {'token': 'AbCdEf123'},
};

void main() {
  test('без ФД — «Товарный чек», с ФД — кассовый с реквизитами ККМ', () {
    final plain = Receipt.fromSale(session: _session, sale: _sale(), number: '1744', total: 470, at: null, payment: 'cash');
    expect(plain.title, 'ТОВАРНЫЙ ЧЕК');
    expect(plain.numberText, '001744');
    expect(plain.shift, '42');
    expect(plain.discount, 20);
    expect(plain.change, 1530);
    expect(plain.eReceiptUrl, 'https://check.nurcrm.kg/AbCdEf123.pdf');
    final f = Receipt.fromSale(session: _session, sale: _sale(fiscal: true), number: '1744', total: 470, at: null, payment: 'cash');
    expect(f.title, contains('КАССОВЫЙ ЧЕК'));
    final text = f.toText();
    for (final s in ['ИНН 01234567891011', 'РН ККМ: 0000123456', 'ФМ: FM0001', 'ФД: 1234', 'ФПД: 987654321', 'НДС 12%', 'ИТОГО: 470']) {
      expect(text, contains(s));
    }
  });

  testWidgets('лента и PDF — в файлы для просмотра', (tester) async {
    for (final w in ['Regular', 'SemiBold', 'Bold', 'ExtraBold']) {
      final loader = FontLoader('Montserrat')..addFont(rootBundle.load('assets/fonts/Montserrat-$w.ttf'));
      await loader.load();
    }
    final out = Platform.environment['RECEIPT_OUT'];
    final r = Receipt.fromSale(session: _session, sale: _sale(fiscal: true), number: '1744', total: 470, at: null, payment: 'cash');
    tester.view.physicalSize = const Size(440, 1180);
    tester.view.devicePixelRatio = 1;
    final key = GlobalKey();
    await tester.pumpWidget(
      MaterialApp(
        theme: ThemeData(fontFamily: 'Montserrat'),
        home: Scaffold(
          backgroundColor: const Color(0xFFE9ECF1),
          body: SingleChildScrollView(
            padding: const EdgeInsets.all(16),
            child: RepaintBoundary(key: key, child: ReceiptView(receipt: r)),
          ),
        ),
      ),
    );
    if (out == null) return;
    await expectLater(find.byKey(key), matchesGoldenFile('goldens/receipt.png'));
    await tester.runAsync(() async => File('$out/receipt.pdf').writeAsBytes(await receiptPdfBytes(r)));
  });
}
