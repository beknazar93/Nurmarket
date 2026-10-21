import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/print/escpos.dart';
import 'package:nurmarket/core/print/printer_service.dart';
import 'package:nurmarket/features/receipt/receipt.dart';

// 2026-10-20: печать чеков ESC/POS — кодировка кириллицы, ширина ленты, построение чека, настройки принтера.
bool _contains(List<int> hay, List<int> needle) {
  for (var i = 0; i + needle.length <= hay.length; i++) {
    var ok = true;
    for (var j = 0; j < needle.length && ok; j++) {
      ok = hay[i + j] == needle[j];
    }
    if (ok) return true;
  }
  return false;
}

Receipt _receipt() => Receipt(
  shop: 'NBS Маркет',
  inn: '01234567891011',
  address: 'г. Бишкек, ул. Киевская 95',
  number: '1744',
  at: DateTime(2026, 10, 20, 9, 15),
  cashier: 'Нур Тест',
  lines: const [
    ReceiptLine(name: 'Картошка', qty: 0.5, unit: 'кг', price: 40, total: 20),
    ReceiptLine(name: 'Макароны «Макфа» спагетти 450г очень длинное название товара', qty: 2, unit: 'шт', price: 85, discount: 10, total: 160),
  ],
  discount: 10,
  total: 180,
  received: 200,
  change: 20,
);

void main() {
  test('CP866 и CP1251: кириллица, Ё, №, кыргызские буквы — без «?»', () {
    expect(encodeText('АЯаяпрЁё', PrinterCodepage.cp866), [0x80, 0x9F, 0xA0, 0xEF, 0xAF, 0xE0, 0xF0, 0xF1]);
    expect(encodeText('АЯаяЁё№', PrinterCodepage.cp1251), [0xC0, 0xDF, 0xE0, 0xFF, 0xA8, 0xB8, 0xB9]);
    expect(encodeText('№', PrinterCodepage.cp866), [0xFC]);
    expect(encodeText('Abc 12', PrinterCodepage.cp866), 'Abc 12'.codeUnits);
    // Ң Ө Ү — нет в таблицах, печатаются как Н О У
    expect(encodeText('Ңөү', PrinterCodepage.cp866), encodeText('Ноу', PrinterCodepage.cp866));
    expect(encodeText('×—', PrinterCodepage.cp1251), 'x-'.codeUnits);
    expect(encodeText('☺', PrinterCodepage.cp866), [0x3F]);
  });

  test('ширина ленты: 58 мм — 32 знака, 80 мм — 48; колонки и перенос', () {
    expect(lineWidth(58), 32);
    expect(lineWidth(80), 48);
    for (final w in [32, 48]) {
      final rows = twoColumns('Очень длинное название товара, которое не влезает в строку', '1 234,5', w);
      expect(rows.every((r) => r.length <= w), isTrue);
      expect(rows.last.length, w);
      expect(rows.last.endsWith('1 234,5'), isTrue);
    }
    expect(wrapText('раз два три четыре', 9), ['раз два', 'три', 'четыре']);
    expect(wrapText('АБВГДЕЁЖЗИЙКЛ', 5), ['АБВГД', 'ЕЁЖЗИ', 'ЙКЛ']);
  });

  test('чек: строки не шире ленты, вес 0,500 кг × 40 = 20, итог и сдача', () {
    for (final mm in [58, 80]) {
      final w = lineWidth(mm);
      final lines = receiptLines(_receipt(), w);
      expect(lines.every((l) => l.text.length <= w), isTrue, reason: '$mm мм');
      final text = lines.map((l) => l.text).join('\n');
      expect(text, contains('0,500 кг x 40'));
      expect(lines.any((l) => l.text.startsWith('ИТОГО') && l.text.endsWith('180 сом') && l.bold), isTrue);
      expect(text, contains('Сдача'));
      expect(text, contains('ИНН 01234567891011'));
      expect(text, contains('ТОВАРНЫЙ ЧЕК'));
    }
  });

  test('байты чека: сброс, выключение китайского режима, таблица ESC t, отрезка', () {
    final b = receiptToEscPos(_receipt(), paperMm: 58, codepage: PrinterCodepage.cp866);
    expect(b.sublist(0, 7), [0x1B, 0x40, 0x1C, 0x2E, 0x1B, 0x74, 17]);
    expect(_contains(b, [0x1D, 0x56, 0x42, 0x00]), isTrue);
    expect(_contains(b, encodeText('ИТОГО', PrinterCodepage.cp866)), isTrue);
    final b2 = receiptToEscPos(_receipt(), codepage: PrinterCodepage.cp1251, codeTable: 73);
    expect(b2.sublist(4, 7), [0x1B, 0x74, 73]);
    expect(_contains(b2, encodeText('ИТОГО', PrinterCodepage.cp1251)), isTrue);
    expect(testPage(paperMm: 80).isNotEmpty, isTrue);
  });

  test('настройки принтера сохраняются и читаются', () {
    const c = PrinterConfig(kind: PrinterKind.network, address: '192.168.1.50', name: 'XP-80', port: 9100, paperMm: 80, codepage: PrinterCodepage.cp1251, codeTable: 46, autoPrint: true);
    final back = PrinterConfig.fromJson(_encode(c));
    expect(back.kind, PrinterKind.network);
    expect(back.address, '192.168.1.50');
    expect(back.paperMm, 80);
    expect(back.codepage, PrinterCodepage.cp1251);
    expect(back.codeTable, 46);
    expect(back.autoPrint, isTrue);
    expect(back.ready, isTrue);
    expect(PrinterConfig.fromJson(null).ready, isFalse);
    expect(PrinterConfig.fromJson('мусор').kind, PrinterKind.none);
  });
}

String _encode(PrinterConfig c) => jsonEncode(c.toJson());
