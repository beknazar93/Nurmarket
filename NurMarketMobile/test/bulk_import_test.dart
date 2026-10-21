import 'dart:convert';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/features/stock/bulk_import.dart';

// 13.10: массовая загрузка — заголовки в любом порядке, разделители ; и табуляция, xlsx с общими строками.
void main() {
  test('csv with headers in any order', () {
    final rows = BulkParser.parse(BulkParser.readText('Штрихкод;Название;Закупка;Цена продажи;Количество\n4870001;"Сок ""Gracio"" 1л";80,5;110;12\n;Без цены;;;'));
    expect(rows.length, 2);
    expect(rows.first.name, 'Сок "Gracio" 1л');
    expect(rows.first.barcode, '4870001');
    expect(rows.first.cost, 80.5);
    expect(rows.first.price, 110);
    expect(rows.first.qty, 12);
    expect(rows.last.valid, isFalse);
  });
  test('tab pasted from Excel without header uses template order', () {
    final rows = BulkParser.parse(BulkParser.readText('Mars\t4600\t65\t48\t24\tшт\tСладости'));
    expect(rows.single.category, 'Сладости');
    expect(rows.single.cost, 48);
  });
  test('xlsx with shared strings', () {
    final zip = Archive()
      ..addFile(ArchiveFile.string('xl/sharedStrings.xml', '<sst><si><t>Название</t></si><si><t>Цена</t></si><si><t>Вода</t></si></sst>'))
      ..addFile(ArchiveFile.string('xl/worksheets/sheet1.xml',
          '<worksheet><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row><row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>62</v></c></row></sheetData></worksheet>'));
    final bytes = Uint8List.fromList(ZipEncoder().encode(zip));
    final rows = BulkParser.parse(BulkParser.readXlsx(bytes));
    expect(rows.single.name, 'Вода');
    expect(rows.single.price, 62);
    expect(utf8.decode(utf8.encode('ok')), 'ok');
  });
}
