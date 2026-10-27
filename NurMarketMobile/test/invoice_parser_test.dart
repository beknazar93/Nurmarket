import 'dart:convert';
import 'dart:io';
import 'dart:isolate';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/invoice/invoice_parser.dart';

// 2026-10-27: разбор электронных накладных (Excel / CSV / XML / CommerceML), числа, коды, даты, битые и опасные файлы.
// Только чистый разбор — без сети, базы и экрана. Образцы — test/fixtures/invoices/ (вымышленные, кроме ТОРГ-12 владельца).
Uint8List _file(String name) => Uint8List.fromList(File('test/fixtures/invoices/$name').readAsBytesSync());
Uint8List _text(String s) => Uint8List.fromList(utf8.encode(s));

/// Минимальный .xlsx из строк таблицы (общие строки, номера ячеек A1, B1…).
Uint8List _xlsx(List<List<String>> rows, {bool inline = false, int startRow = 1}) {
  final shared = <String>[];
  String ref(int c, int r) {
    var s = '';
    var n = c + 1;
    while (n > 0) {
      s = String.fromCharCode(65 + (n - 1) % 26) + s;
      n = (n - 1) ~/ 26;
    }
    return '$s$r';
  }

  final sb = StringBuffer('<worksheet><sheetData>');
  for (var i = 0; i < rows.length; i++) {
    final r = i + startRow;
    sb.write('<row r="$r">');
    for (var c = 0; c < rows[i].length; c++) {
      final v = rows[i][c];
      if (v.isEmpty) continue;
      final num = double.tryParse(v) != null && !v.startsWith('0');
      if (num) {
        sb.write('<c r="${ref(c, r)}"><v>$v</v></c>');
      } else if (inline) {
        sb.write('<c r="${ref(c, r)}" t="inlineStr"><is><t>${_esc(v)}</t></is></c>');
      } else {
        shared.add(v);
        sb.write('<c r="${ref(c, r)}" t="s"><v>${shared.length - 1}</v></c>');
      }
    }
    sb.write('</row>');
  }
  sb.write('</sheetData></worksheet>');
  final zip = Archive()
    ..addFile(ArchiveFile.string('xl/sharedStrings.xml', '<sst>${[for (final s in shared) '<si><t>${_esc(s)}</t></si>'].join()}</sst>'))
    ..addFile(ArchiveFile.string('xl/worksheets/sheet1.xml', sb.toString()));
  return Uint8List.fromList(ZipEncoder().encode(zip));
}

String _esc(String s) => s.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

void main() {
  group('числа', () {
    test('пробелы, неразрывные пробелы, запятая и точка, тысячи, знаки, хвосты', () {
      final cases = <String, double?>{
        '1234': 1234,
        '1 234,56': 1234.56,
        '1 234,56': 1234.56,
        '1 234.5': 1234.5,
        '1,234.56': 1234.56,
        '1.234,56': 1234.56,
        '1.234.567,8': 1234567.8,
        '12,5': 12.5,
        '12.5': 12.5,
        '0,333': 0.333,
        '(12,5)': -12.5,
        '−3': -3,
        '-3,5': -3.5,
        '+7': 7,
        '12 %': 12,
        '5 шт': 5,
        '45000 руб.': 45000,
        "1'234": 1234,
        '4.87E+12': 4870000000000,
        '': null,
        '   ': null,
        'abc': null,
        '—': null,
        '27.10.2026': null,
        'Без НДС': null,
      };
      cases.forEach((input, expected) {
        final v = parseInvoiceNumber(input);
        if (expected == null) {
          expect(v, isNull, reason: '«$input»');
        } else {
          expect(v, closeTo(expected, 1e-6), reason: '«$input»');
        }
      });
    });
  });

  group('коды', () {
    test('нормализация: научная запись, .0, апостроф, пробелы и дефисы, не цифровой код остаётся как есть', () {
      expect(normalizeInvoiceCode('4870200221261'), '4870200221261');
      expect(normalizeInvoiceCode('4870200221261.0'), '4870200221261');
      expect(normalizeInvoiceCode('4,870200221261E+12'), '4870200221261');
      expect(normalizeInvoiceCode("'4870200221261"), '4870200221261');
      expect(normalizeInvoiceCode('4870 2002 21261'), '4870200221261');
      expect(normalizeInvoiceCode('487-0200-221261'), '4870200221261');
      expect(normalizeInvoiceCode('AB-12'), 'AB-12');
      expect(normalizeInvoiceCode(null), '');
      expect(isBarcodeLike('4870200221261'), isTrue);
      expect(isBarcodeLike('123'), isFalse);
      expect(isBarcodeLike('AB1234567'), isFalse);
    });
    test('варианты: UPC-A ↔ EAN-13, GTIN-14, потерянные нули Excel', () {
      expect(invoiceCodeVariants('012345678905'), containsAll(['012345678905', '0012345678905']));
      expect(invoiceCodeVariants('0012345678905'), contains('012345678905'));
      // EAN-13 «0…» в Excel стал 12 цифрами, а «00…» — 11
      expect(invoiceCodeVariants('12345678905'), containsAll(['012345678905', '0012345678905']));
      expect(invoiceCodeVariants('4870200221261').first, '4870200221261');
      expect(invoiceCodeVariants(''), isEmpty);
    });
  });

  group('даты', () {
    test('форматы и названия месяцев ru / ky / uz / en', () {
      final d = DateTime(2026, 10, 27);
      for (final s in ['27.10.2026', '27/10/2026', '27-10-26', '2026-10-27', '2026-10-27T10:30:00', '20261027', '27 октября 2026 г.', '27 Октябрь 2026', '27 oktabr 2026', 'October 27, 2026', '27 Oct 2026', 'от 27.10.2026']) {
        expect(parseInvoiceDate(s), d, reason: s);
      }
      expect(parseInvoiceDate('1 мая 2026'), DateTime(2026, 5, 1));
      expect(parseInvoiceDate('3 марта 2026'), DateTime(2026, 3, 3));
      expect(parseInvoiceDate('45966'), DateTime(2025, 11, 5), reason: 'серийный номер Excel');
      expect(parseInvoiceDate('31.02.2026'), isNull);
      expect(parseInvoiceDate('привет'), isNull);
      expect(parseInvoiceDate(''), isNull);
    });
    test('единицы измерения под приход', () {
      expect(normalizeInvoiceUnit('шт.'), 'шт');
      expect(normalizeInvoiceUnit('Штука'), 'шт');
      expect(normalizeInvoiceUnit('КГ'), 'кг');
      expect(normalizeInvoiceUnit('литр'), 'л');
      expect(normalizeInvoiceUnit('упаковка'), 'упак');
      expect(normalizeInvoiceUnit(''), 'шт');
      expect(normalizeInvoiceUnit('метр'), 'метр');
      expect(normalizeInvoiceUnit('очень длинная единица'), 'шт');
    });
  });

  group('CSV', () {
    test('русская накладная: шапка, поставщик с ИНН, покупатель не путается, строки, итог, валюта', () {
      final inv = InvoiceParser.parse(_file('csv_ru.csv'), fileName: 'csv_ru.csv');
      expect(inv.format, InvoiceFormat.csv);
      expect(inv.number, 'Р-0457');
      expect(inv.date, DateTime(2026, 10, 12));
      expect(inv.supplierName, 'ОсОО "Бишкек Трейд"');
      expect(inv.supplierInn, '01234567890123', reason: 'не ИНН покупателя 22222222222222');
      expect(inv.currency, 'KGS');
      expect(inv.lines.length, 4);
      final a = inv.lines[0];
      expect(a.name, 'Вода "Нур" негазированная 1,5 л');
      expect(a.barcode, '4870200221261');
      expect(a.article, 'A-100');
      expect(a.qty, 24);
      expect(a.cost, 42.5);
      expect(a.sum, 1020);
      expect(inv.lines[2].barcode, isEmpty);
      expect(inv.lines[2].unit, 'кг');
      expect(inv.lines[3].valid, isFalse, reason: 'количество 0');
      expect(inv.lines[3].issue, InvoiceLineIssue.noQty);
      expect(inv.linesTotal(), closeTo(5582, 0.001));
      expect(inv.fileTotal, 5582);
    });
    test('кодировка windows-1251 (Excel «CSV» на русской Windows) читается так же', () {
      final a = InvoiceParser.parse(_file('csv_ru.csv'));
      final b = InvoiceParser.parse(_file('csv_ru_cp1251.csv'));
      expect(b.supplierName, a.supplierName);
      expect(b.lines.map((l) => l.name).toList(), a.lines.map((l) => l.name).toList());
      expect(b.lines[3].name, 'Чай чёрный 100 пак');
      expect(b.fileTotal, 5582);
    });
    test('кыргызские заголовки и месяц/дата в строке «Накладной № 77, 05.10.2026-ж.»', () {
      final inv = InvoiceParser.parse(_file('csv_ky.csv'));
      expect(inv.number, '77');
      expect(inv.date, DateTime(2026, 10, 5));
      expect(inv.supplierName, 'ОсОО «Ала-Тоо Азык»');
      expect(inv.supplierInn, '03030303030303');
      expect(inv.lines.length, 2);
      expect(inv.lines[0].name, 'Нан "Бишкек" 500 г');
      expect(inv.lines[0].qty, 100);
      expect(inv.lines[0].cost, 28.5);
      expect(inv.lines[1].unit, 'л');
      expect(inv.fileTotal, 6570);
      expect(inv.linesTotal(), 6570);
    });
    test('узбекские заголовки, запятая-разделитель, числа в кавычках', () {
      final inv = InvoiceParser.parse(_file('csv_uz.csv'));
      expect(inv.number, 'UZ-5');
      expect(inv.date, DateTime(2026, 10, 3));
      expect(inv.supplierName, '"Samarqand Oziq-ovqat" MChJ');
      expect(inv.supplierInn, '305123456');
      expect(inv.lines.length, 2);
      expect(inv.lines[0].cost, 12500.5);
      expect(inv.lines[0].sum, 500020);
      expect(inv.fileTotal, 745020);
    });
    test('без шапки над таблицей, табуляция, колонки в любом порядке, цена выводится из суммы', () {
      final inv = InvoiceParser.parse(_text('Количество\tСумма\tНаименование\n10\t500\tГвозди\n2\t80\tБолты'));
      expect(inv.lines.length, 2);
      expect(inv.lines[0].cost, 50);
      expect(inv.lines[1].cost, 40);
      expect(inv.number, isEmpty);
      expect(inv.supplierName, isEmpty);
    });
    test('многострочная ячейка в кавычках и «""» внутри', () {
      final inv = InvoiceParser.parse(_text('Наименование;Кол-во;Цена\n"Стол ""Офис""\nбелый";2;1000'));
      expect(inv.lines.single.name, 'Стол "Офис"\nбелый');
      expect(inv.lines.single.qty, 2);
    });
    test('таблица найдена не в первой строке (глубже 20 строк шапки) и итог посреди файла не мешает', () {
      final head = List.generate(30, (i) => 'Реквизит $i;значение');
      final inv = InvoiceParser.parse(_text('${head.join('\n')}\nНаименование;Кол-во;Цена\nА;1;10\nИтого;1;10\nБ;2;5'));
      expect(inv.lines.map((l) => l.name).toList(), ['А', 'Б']);
    });
    test('строка нумерации столбцов «1 2 3 4» пропускается', () {
      final inv = InvoiceParser.parse(_text('Наименование;Кол-во;Цена\n1;2;3\nТовар;5;10'));
      expect(inv.lines.map((l) => l.name).toList(), ['Товар']);
    });
    test('5000 строк читаются быстро, 5001 — ошибка «слишком много строк»', () {
      String make(int n) => 'Наименование;Кол-во;Цена\n${List.generate(n, (i) => 'Товар $i;1;10').join('\n')}';
      final sw = Stopwatch()..start();
      expect(InvoiceParser.parse(_text(make(5000))).lines.length, 5000);
      expect(sw.elapsed.inSeconds, lessThan(10));
      expect(() => InvoiceParser.parse(_text(make(5001))), throwsA(isA<InvoiceParseException>().having((e) => e.error, 'error', InvoiceError.tooMany)));
    });
  });

  group('Excel', () {
    test('ТОРГ-12 владельца разобран в test/invoice_torg12_test.dart; здесь — собственные xlsx', () {
      final inv = InvoiceParser.parse(
        _xlsx([
          ['Поставщик: ООО «Ромашка»'],
          ['ИНН: 7701234567'],
          [],
          ['Штрихкод', 'Наименование', 'Кол-во', 'Цена', 'Сумма'],
          ['4870200221261', 'Вода', '12', '40,5', '486'],
          ['4.87020022127E+12', 'Сок', '6', '90', '540'],
        ]),
      );
      expect(inv.format, InvoiceFormat.xlsx);
      expect(inv.supplierName, 'ООО «Ромашка»');
      expect(inv.supplierInn, '7701234567');
      expect(inv.lines.length, 2);
      expect(inv.lines[0].barcode, '4870200221261');
      expect(inv.lines[1].barcode, '4870200221270', reason: 'научная запись числа из ячейки Excel');
    });
    test('inline-строки и пустые строки между данными: номер строки совпадает с Excel', () {
      final inv = InvoiceParser.parse(
        _xlsx([
          ['Наименование', 'Количество', 'Цена'],
          [],
          ['Гвозди', '5', '10'],
        ], inline: true, startRow: 3),
      );
      expect(inv.lines.single.name, 'Гвозди');
      expect(inv.lines.single.row, 5);
    });
    test('НДС: цена без НДС, цена с НДС, ставка; переключатель берёт цену с НДС', () {
      final inv = InvoiceParser.parse(
        _xlsx([
          ['Наименование', 'Кол-во', 'Цена без НДС', 'Ставка НДС', 'Цена с НДС', 'Сумма без НДС', 'Всего с НДС'],
          ['Товар', '10', '100', '12', '112', '1000', '1120'],
        ]),
      );
      final l = inv.lines.single;
      expect(l.cost, 100);
      expect(l.costGross, 112);
      expect(l.vat, 12);
      expect(l.unitCost(), 100);
      expect(l.unitCost(withVat: true), 112);
      expect(l.lineSum(), 1000);
      expect(l.lineSum(withVat: true), 1120);
      expect(inv.hasVat, isTrue);
    });
    test('цена с НДС выводится из суммы с НДС или по ставке', () {
      final byGross = InvoiceParser.parse(_xlsx([['Наименование', 'Кол-во', 'Цена', 'Сумма', 'Всего с НДС'], ['Т', '4', '100', '400', '448']])).lines.single;
      expect(byGross.unitCost(withVat: true), 112);
      final byRate = InvoiceParser.parse(_xlsx([['Наименование', 'Кол-во', 'Цена', 'НДС'], ['Т', '4', '100', '20']])).lines.single;
      expect(byRate.unitCost(withVat: true), 120);
    });
    test('«Код» — штрихкод, если похож на EAN, иначе артикул; «Код по ОКЕИ» — не штрихкод', () {
      final a = InvoiceParser.parse(_xlsx([['Код', 'Наименование', 'Кол-во', 'Цена'], ['4870200221261', 'Вода', '1', '10']])).lines.single;
      expect(a.barcode, '4870200221261');
      final b = InvoiceParser.parse(_xlsx([['Код', 'Наименование', 'Кол-во', 'Цена'], ['00123', 'Вода', '1', '10']])).lines.single;
      expect(b.barcode, isEmpty);
      expect(b.article, '00123');
      final c = InvoiceParser.parse(_xlsx([['Наименование', 'Код по ОКЕИ', 'Кол-во', 'Цена'], ['Вода', '796', '1', '10']])).lines.single;
      expect(c.barcode, isEmpty);
      expect(c.unit, 'шт');
    });
    test('розничная цена из файла запоминается', () {
      final l = InvoiceParser.parse(_xlsx([['Наименование', 'Кол-во', 'Цена закупки', 'Розничная цена'], ['Т', '1', '80', '120']])).lines.single;
      expect(l.cost, 80);
      expect(l.retail, 120);
    });
    test('расхождение итогов: итог файла против суммы строк', () {
      final inv = InvoiceParser.parse(
        _xlsx([
          ['Наименование', 'Кол-во', 'Цена', 'Сумма'],
          ['А', '2', '100', '200'],
          ['Б', '1', '50', '50'],
          ['Итого', '', '', '300'],
        ]),
      );
      expect(inv.linesTotal(), 250);
      expect(inv.fileTotal, 300);
      expect((inv.linesTotal() - inv.fileTotal!).abs(), 50);
    });
    test('сумма прописью: «Триста одна тысяча пятьсот рублей 50 копеек»', () {
      final inv = InvoiceParser.parse(
        _xlsx([
          ['Наименование', 'Кол-во', 'Цена'],
          ['А', '1', '301500'],
          ['Итого отпущено на сумму: Триста одна тысяча пятьсот рублей 50 копеек'],
        ], inline: true),
      );
      expect(inv.fileTotal, 301500.5);
      expect(inv.currency, 'RUB');
    });
  });

  group('XML', () {
    test('электронная счёт-фактура произвольной структуры', () {
      final inv = InvoiceParser.parse(_file('esf_generic.xml'));
      expect(inv.format, InvoiceFormat.xml);
      expect(inv.number, 'ЭСФ-2026-0099');
      expect(inv.date, DateTime(2026, 10, 20));
      expect(inv.currency, 'KGS');
      expect(inv.supplierName, 'ОсОО «Ала-Тоо Опт»');
      expect(inv.supplierInn, '02345678901234', reason: 'ИНН продавца, не покупателя');
      expect(inv.lines.length, 2);
      expect(inv.lines[0].name, 'Мука высший сорт 50 кг');
      expect(inv.lines[0].barcode, '4870001234567');
      expect(inv.lines[0].qty, 10);
      expect(inv.lines[0].cost, 2150);
      expect(inv.lines[0].sum, 21500);
      expect(inv.lines[0].vat, 12);
      expect(inv.lines[1].cost, 118.5);
      expect(inv.fileTotal, 25766);
      expect(inv.linesTotal(), 25766);
    });
    test('CommerceML: продавец по роли, единица из атрибута, ставка НДС не подменяет название', () {
      final inv = InvoiceParser.parse(_file('commerceml.xml'));
      expect(inv.number, '00045');
      expect(inv.date, DateTime(2026, 10, 21));
      expect(inv.supplierName, 'ОсОО Поставка Юг');
      expect(inv.supplierInn, '01234567890123');
      expect(inv.currency, 'KGS');
      expect(inv.lines.length, 2);
      expect(inv.lines[0].name, 'Сахар 1 кг', reason: '«Наименование» ставки налога («НДС») глубже и не берётся');
      expect(inv.lines[0].article, 'A1');
      expect(inv.lines[0].barcode, '4870009876543');
      expect(inv.lines[0].unit, 'Штука');
      expect(inv.lines[0].cost, 85);
      expect(inv.lines[0].qty, 50);
      expect(inv.lines[1].unit, 'Килограмм');
      expect(inv.fileTotal, 6850);
      expect(inv.linesTotal(), 6850);
    });
    test('строки в атрибутах: <Row barcode="…" name="…" qty="…" price="…"/>', () {
      final inv = InvoiceParser.parse(
        _text('<Invoice Number="15" Date="05.10.2026"><Rows><Row barcode="4870200221261" name="Вода" qty="3" price="40"/><Row barcode="4870200221278" name="Сок" qty="2" price="90.5"/></Rows></Invoice>'),
      );
      expect(inv.number, '15');
      expect(inv.date, DateTime(2026, 10, 5));
      expect(inv.lines.length, 2);
      expect(inv.lines[1].cost, 90.5);
    });
    test('XML в windows-1251 с объявленной кодировкой', () {
      final src = '<?xml version="1.0" encoding="windows-1251"?><Накладная><Номер>7</Номер><Товары><Товар><Наименование>Хлеб</Наименование><Количество>3</Количество><Цена>25</Цена></Товар></Товары></Накладная>';
      final bytes = Uint8List.fromList(latin1CyrillicEncode(src));
      final inv = InvoiceParser.parse(bytes);
      expect(inv.lines.single.name, 'Хлеб');
      expect(inv.number, '7');
    });
    test('нет таблицы товаров в XML — понятная ошибка', () {
      expect(() => InvoiceParser.parse(_text('<a><b>1</b><c>текст</c></a>')), throwsA(isA<InvoiceParseException>().having((e) => e.error, 'error', InvoiceError.noTable)));
    });
  });

  group('безопасность и битые файлы', () {
    InvoiceError errOf(Uint8List b) {
      try {
        InvoiceParser.parse(b);
      } on InvoiceParseException catch (e) {
        return e.error;
      }
      fail('ожидалась ошибка');
    }

    test('XXE: DOCTYPE и внешние сущности отклоняются, файл с диска не читается', () {
      const xxe = '<?xml version="1.0"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><invoice><item><name>&xxe;</name><quantity>1</quantity><price>5</price></item></invoice>';
      expect(errOf(_text(xxe)), InvoiceError.unsafeXml);
      expect(errOf(_text('<?xml version="1.0"?><!DOCTYPE x SYSTEM "http://evil.example/x.dtd"><a/>')), InvoiceError.unsafeXml);
    });
    test('«миллиард смешков» (вложенные сущности) отклоняется', () {
      const bomb = '<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"><!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">]><lolz>&lol3;</lolz>';
      expect(errOf(_text(bomb)), InvoiceError.unsafeXml);
    });
    test('XXE внутри xlsx (sharedStrings с DOCTYPE) отклоняется', () {
      final zip = Archive()
        ..addFile(ArchiveFile.string('xl/sharedStrings.xml', '<?xml version="1.0"?><!DOCTYPE s [<!ENTITY x SYSTEM "file:///etc/passwd">]><sst><si><t>&x;</t></si></sst>'))
        ..addFile(ArchiveFile.string('xl/worksheets/sheet1.xml', '<worksheet><sheetData/></worksheet>'));
      expect(errOf(Uint8List.fromList(ZipEncoder().encode(zip))), InvoiceError.unsafeXml);
    });
    test('слишком глубокая вложенность XML — ошибка, а не падение', () {
      final deep = '${'<a>' * 5000}x${'</a>' * 5000}';
      expect(errOf(_text(deep)), InvoiceError.broken);
    });
    test('файл больше 10 МБ — «слишком большой», разбор даже не начинается', () {
      expect(errOf(Uint8List(InvoiceParser.maxBytes + 1)), InvoiceError.tooBig);
    });
    test('пустой файл, PDF, фото, старый .xls', () {
      expect(errOf(Uint8List(0)), InvoiceError.empty);
      expect(errOf(Uint8List.fromList([0x25, 0x50, 0x44, 0x46, 0x2D, 0x31])), InvoiceError.pdf);
      expect(errOf(Uint8List.fromList([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A])), InvoiceError.image);
      expect(errOf(Uint8List.fromList([0xFF, 0xD8, 0xFF, 0xE0, 0, 0])), InvoiceError.image);
      expect(errOf(Uint8List.fromList([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1])), InvoiceError.xls);
    });
    test('повреждённый zip, zip без листов, обрезанный XML, текст не про накладную', () {
      expect(errOf(Uint8List.fromList([0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5])), InvoiceError.broken);
      expect(errOf(Uint8List.fromList(ZipEncoder().encode(Archive()..addFile(ArchiveFile.string('a.txt', 'x'))))), InvoiceError.broken);
      expect(errOf(_text('<invoice><item><name>Вода</name>')), InvoiceError.broken);
      expect(errOf(_text('Привет, это просто письмо без таблицы товаров.')), InvoiceError.noTable);
      expect(errOf(_text('Наименование;Кол-во;Цена')), InvoiceError.noTable, reason: 'только заголовок, строк товаров нет');
    });
    test('в отдельном изоляте (как на экране) ошибка приходит тем же типом, результат — тот же', () async {
      await expectLater(Isolate.run(() => InvoiceParser.parse(_text('<!DOCTYPE a><a/>'))), throwsA(isA<InvoiceParseException>().having((e) => e.error, 'error', InvoiceError.unsafeXml)));
      final inv = await Isolate.run(() => InvoiceParser.parse(_file('csv_ru.csv')));
      expect(inv.lines.length, 4);
    });
    test('случайные байты не роняют разбор (любое исключение — только InvoiceParseException)', () {
      final r = [for (var i = 0; i < 2000; i++) (i * 7919 + 13) % 256];
      for (var n = 1; n < 60; n++) {
        try {
          InvoiceParser.parse(Uint8List.fromList(r.skip(n * 3).take(300).toList()));
        } on InvoiceParseException {
          // ожидаемо
        }
      }
    });
  });
}

/// Кодировка windows-1251 для теста (только буквы русского алфавита и ASCII).
List<int> latin1CyrillicEncode(String s) {
  final out = <int>[];
  for (final c in s.runes) {
    if (c < 0x80) {
      out.add(c);
    } else if (c >= 0x410 && c <= 0x44F) {
      out.add(0xC0 + (c - 0x410));
    } else if (c == 0x401) {
      out.add(0xA8);
    } else if (c == 0x451) {
      out.add(0xB8);
    } else {
      out.add(0x3F);
    }
  }
  return out;
}
