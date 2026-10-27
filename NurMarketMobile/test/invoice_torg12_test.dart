import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/invoice/invoice_parser.dart';

// 2026-10-27: реальная накладная владельца (Excel «ТОВАРНАЯ НАКЛАДНАЯ (ТОРГ-12)», без штрихкодов, цены в рублях, без НДС).
void main() {
  final bytes = Uint8List.fromList(File('test/fixtures/invoices/owner_torg12.xlsx').readAsBytesSync());

  test('ТОРГ-12 владельца: шапка, 4 позиции, итог прописью, валюта', () {
    final inv = InvoiceParser.parse(bytes, fileName: 'owner_torg12.xlsx');
    expect(inv.format, InvoiceFormat.xlsx);
    expect(inv.number, '104');
    expect(inv.date, DateTime(2026, 10, 27));
    expect(inv.supplierName, 'ООО «ТехноСфера-М»');
    expect(inv.supplierInn, '7725896321', reason: 'ИНН поставщика, а не покупателя (503215468792) и не КПП');
    expect(inv.currency, 'RUB');
    expect(inv.lines.length, 4, reason: 'строка нумерации, итог и подписи — не товары');
    final l = inv.lines;
    expect(l[0].name, 'Ноутбук бизнес-серии 15.6"');
    expect(l[0].qty, 5);
    expect(l[0].cost, 45000);
    expect(l[0].sum, 225000);
    expect(l[0].unit, 'шт.');
    expect(l[0].barcode, isEmpty, reason: '796 — код ОКЕИ, не штрихкод');
    expect(l[0].article, isEmpty);
    expect(l[0].vat, 0);
    expect(l[1].name, 'Монитор 24" IPS Full HD');
    expect(l[1].cost, 12000);
    expect(l[2].name, 'Комплект (мышь + клавиатура) беспроводной');
    expect(l[2].cost, 2500);
    expect(l[3].name, 'Сетевой фильтр 5м черный');
    expect(l[3].cost, 800);
    expect(inv.linesTotal(), 301500);
    expect(inv.fileTotal, 301500, reason: 'сумма прописью «Триста одна тысяча пятьсот рублей 00 копеек»');
    expect(inv.hasVat, isFalse);
    expect(inv.fileHash.length, 64);
  });
}
