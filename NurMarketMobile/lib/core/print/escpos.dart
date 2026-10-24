/// 2026-10-20, владелец: «в мобилке исправь подключение к принтерам». Чек на термопринтер — команды ESC/POS
/// (Xprinter, GOOJPRT, Sunmi, HPRT и др.): 58 мм — 32 знака в строке, 80 мм — 48 (шрифт A).
/// Кириллица — однобайтовой таблицей CP866 (ESC t 17) или CP1251 (ESC t 46); без «FS .» многие китайские
/// принтеры печатают русские буквы иероглифами, поэтому он посылается сразу после ESC @.
/// Модуль без Flutter — проверяется юнит-тестами (test/escpos_test.dart).
library;

import 'dart:typed_data';

import '../../features/receipt/receipt.dart';
import '../util/ui.dart' show money, qtyText;

/// Кодировка кириллицы на принтере.
enum PrinterCodepage { cp866, cp1251 }

/// Номер таблицы для ESC t по умолчанию (Epson и большинство китайских принтеров).
int defaultCodeTable(PrinterCodepage c) => c == PrinterCodepage.cp866 ? 17 : 46;

/// Знаков в строке: 58 мм — 32, 80 мм — 48.
int lineWidth(int paperMm) => paperMm >= 80 ? 48 : 32;

// Буквы, которых нет ни в CP866, ни в CP1251: кыргызские, турецкие, узбекские, типографские знаки.
const _fallback = <String, String>{
  'Ң': 'Н', 'ң': 'н', 'Ө': 'О', 'ө': 'о', 'Ү': 'У', 'ү': 'у',
  'Ş': 'S', 'ş': 's', 'Ğ': 'G', 'ğ': 'g', 'İ': 'I', 'ı': 'i', 'Ç': 'C', 'ç': 'c', 'Ö': 'O', 'ö': 'o', 'Ü': 'U', 'ü': 'u',
  'ʻ': "'", 'ʼ': "'", '‘': "'", '’': "'", '“': '"', '”': '"', '×': 'x', '−': '-', '–': '-', '—': '-', '…': '...',
  ' ': ' ', ' ': ' ', '✓': 'v', '₽': 'р', 'Ә': 'А', 'ә': 'а', 'Қ': 'К', 'қ': 'к', 'Ғ': 'Г', 'ғ': 'г', 'Ҳ': 'Х', 'ҳ': 'х',
  'Ў': 'У', 'ў': 'у', 'Ұ': 'У', 'ұ': 'у', 'Һ': 'Х', 'һ': 'х',
};

/// Один символ в байт таблицы; null — символа в таблице нет.
int? _cp866(int r) {
  if (r < 0x80) return r;
  if (r >= 0x0410 && r <= 0x042F) return 0x80 + (r - 0x0410); // А–Я
  if (r >= 0x0430 && r <= 0x043F) return 0xA0 + (r - 0x0430); // а–п
  if (r >= 0x0440 && r <= 0x044F) return 0xE0 + (r - 0x0440); // р–я
  return switch (r) {
    0x0401 => 0xF0, // Ё
    0x0451 => 0xF1, // ё
    0x00B0 => 0xF8, // °
    0x00B7 => 0xFA, // ·
    0x2116 => 0xFC, // №
    _ => null,
  };
}

int? _cp1251(int r) {
  if (r < 0x80) return r;
  if (r >= 0x0410 && r <= 0x044F) return 0xC0 + (r - 0x0410); // А–я
  return switch (r) {
    0x0401 => 0xA8, // Ё
    0x0451 => 0xB8, // ё
    0x2116 => 0xB9, // №
    0x00B7 => 0xB7, // ·
    0x00B0 => 0xB0, // °
    0x00AB => 0xAB, // «
    0x00BB => 0xBB, // »
    _ => null,
  };
}

/// Строка → байты принтера. Неизвестный символ — «?», чтобы чек не ломался.
Uint8List encodeText(String s, PrinterCodepage cp) {
  final out = <int>[];
  for (final ch in s.runes) {
    var c = String.fromCharCode(ch);
    c = _fallback[c] ?? c;
    for (final r in c.runes) {
      out.add((cp == PrinterCodepage.cp866 ? _cp866(r) : _cp1251(r)) ?? 0x3F);
    }
  }
  return Uint8List.fromList(out);
}

/// Перенос по словам под ширину ленты; длинное слово режется.
List<String> wrapText(String text, int width) {
  final out = <String>[];
  for (final para in text.split('\n')) {
    var line = '';
    for (var w in para.split(RegExp(r'\s+')).where((w) => w.isNotEmpty)) {
      while (w.length > width) {
        if (line.isNotEmpty) {
          out.add(line);
          line = '';
        }
        out.add(w.substring(0, width));
        w = w.substring(width);
      }
      if (line.isEmpty) {
        line = w;
      } else if (line.length + 1 + w.length <= width) {
        line = '$line $w';
      } else {
        out.add(line);
        line = w;
      }
    }
    out.add(line);
  }
  return out;
}

/// Слева текст, справа сумма — ровно [width] знаков; не влезает — текст переносится, сумма на последней строке.
List<String> twoColumns(String left, String right, int width) {
  if (right.length >= width) return [...wrapText(left, width), right.substring(0, width)];
  final room = width - right.length - 1;
  final lines = wrapText(left, room < 1 ? 1 : room);
  final last = lines.removeLast();
  return [...lines, '$last${' ' * (width - last.length - right.length)}$right'];
}

String centerText(String s, int width) => s.length >= width ? s : '${' ' * ((width - s.length) ~/ 2)}$s';

/// Строка чека с оформлением.
class PrintLine {
  final String text;
  final bool bold;
  final bool big;
  final bool center;
  const PrintLine(this.text, {this.bold = false, this.big = false, this.center = false});
}

/// Команды ESC/POS.
class EscPos {
  final PrinterCodepage codepage;
  final int codeTable;
  final _b = BytesBuilder();
  EscPos(this.codepage, {int? codeTable}) : codeTable = codeTable ?? defaultCodeTable(codepage) {
    _b.add([0x1B, 0x40]); // ESC @ — сброс
    _b.add([0x1C, 0x2E]); // FS . — выключить китайский двухбайтовый режим (иначе кириллица — иероглифами)
    _b.add([0x1B, 0x74, this.codeTable]); // ESC t n — таблица символов
  }

  void line(PrintLine l) {
    _b.add([0x1B, 0x61, l.center ? 1 : 0]); // ESC a — выравнивание
    _b.add([0x1B, 0x45, l.bold ? 1 : 0]); // ESC E — жирный
    _b.add([0x1D, 0x21, l.big ? 0x11 : 0x00]); // GS ! — двойная ширина и высота
    _b.add(encodeText(l.text, codepage));
    _b.add([0x0A]);
  }

  void text(String s, {bool bold = false, bool big = false, bool center = false}) => line(PrintLine(s, bold: bold, big: big, center: center));

  /// 2026-10-22 (мобилка, админ): П6 — импульс денежного ящика (ящик подключён к принтеру, разъём DK), как на ПК
  /// (EscPosCommands.WriteOpenCashDrawer + WriteRealtimeDrawerPulse): ESC p m 25 250 и следом DLE DC4 1 m 2 — второй
  /// выполняется сразу, даже пока принтер допечатывает чек. [pin]: 0 — контакт 2, 1 — контакт 5.
  void openDrawer({int pin = 0}) {
    final m = pin == 1 ? 1 : 0;
    _b.add([0x1B, 0x70, m, 25, 250]);
    _b.add([0x10, 0x14, 0x01, m, 2]);
  }

  /// 2026-10-22 (мобилка, админ): П6 — QR-код командой принтера GS ( k (модель 2, коррекция M), по центру.
  /// Принтеры без QR эти байты пропускают. Длинная строка (> 700 байт) не печатается — QR вышел бы нечитаемым.
  void qr(String data, {int size = 6}) {
    final d = data.codeUnits.where((c) => c < 0x80).toList();
    if (d.isEmpty || d.length > 700) return;
    final n = d.length + 3;
    _b.add([0x1B, 0x61, 1]);
    _b.add([0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00]); // модель 2
    _b.add([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, size.clamp(1, 16)]); // размер точки
    _b.add([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, 0x31]); // коррекция M
    _b.add([0x1D, 0x28, 0x6B, n & 0xFF, n >> 8, 0x31, 0x50, 0x30, ...d]); // данные
    _b.add([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]); // печать
    _b.add([0x0A, 0x1B, 0x61, 0]);
  }

  /// Протяжка и отрезка (у принтера без ножа GS V просто игнорируется).
  void feedAndCut({int lines = 4}) {
    _b.add([0x1B, 0x61, 0, 0x1D, 0x21, 0, 0x1B, 0x45, 0]);
    _b.add([0x1B, 0x64, lines]); // ESC d n
    _b.add([0x1D, 0x56, 0x42, 0x00]); // GS V B 0 — частичная отрезка
  }

  Uint8List bytes() => _b.toBytes();
}

/// Чек строками под ширину ленты — то же содержание, что Receipt.toText (реквизиты по нормам КР).
List<PrintLine> receiptLines(Receipt r, int width) {
  final dash = PrintLine('-' * width);
  final out = <PrintLine>[];
  for (final s in wrapText(r.shop, width)) {
    out.add(PrintLine(s, bold: true, center: true));
  }
  if (r.inn.isNotEmpty) out.add(PrintLine('ИНН ${r.inn}', center: true));
  if (r.address.isNotEmpty) out.addAll(wrapText(r.address, width).map((s) => PrintLine(s, center: true)));
  out.add(dash);
  out.addAll(wrapText(r.title, width).map((s) => PrintLine(s, bold: true, center: true)));
  out.add(PrintLine('Чек № ${r.numberText}${r.shift.isEmpty ? '' : ' · смена № ${r.shift}'}'));
  out.add(PrintLine(r.dateText));
  if (r.cashier.isNotEmpty) out.addAll(wrapText('Кассир: ${r.cashier}', width).map(PrintLine.new));
  if (r.cashbox.isNotEmpty) out.addAll(wrapText('Касса: ${r.cashbox}', width).map(PrintLine.new));
  if (r.client.isNotEmpty) out.addAll(wrapText('Покупатель: ${r.client}', width).map(PrintLine.new));
  out.add(dash);
  for (var i = 0; i < r.lines.length; i++) {
    final l = r.lines[i];
    out.addAll(wrapText('${i + 1}. ${l.name}', width).map(PrintLine.new));
    final qty = l.unit == 'кг' || l.unit == 'kg' ? l.qty.toStringAsFixed(3).replaceAll('.', ',') : qtyText(l.qty);
    out.addAll(twoColumns('  $qty${l.unit.isEmpty ? '' : ' ${l.unit}'} x ${money(l.price)}', money(_gross(l)), width).map(PrintLine.new));
    if (l.discount > 0) out.addAll(twoColumns('  скидка', '-${money(l.discount)}', width).map(PrintLine.new));
  }
  out.add(dash);
  if (r.discount > 0) out.addAll(twoColumns('Скидка', '-${money(r.discount)}', width).map(PrintLine.new));
  out.addAll(twoColumns('ИТОГО', '${money(r.total)} сом', width).map((s) => PrintLine(s, bold: true)));
  // 2026-10-22 (мобилка, деньги): долг — внесено / долг / «Вернуть до».
  for (final (k, v) in r.paymentLines) {
    out.addAll(twoColumns(k, v, width).map(PrintLine.new));
  }
  if (r.change > 0) out.addAll(twoColumns('Сдача', money(r.change), width).map(PrintLine.new));
  final f = r.fiscal;
  if (f != null) {
    out.add(dash);
    for (final t in f.taxes) {
      out.addAll(twoColumns(t.name, money(t.amount), width).map(PrintLine.new));
    }
    if (f.regNumber.isNotEmpty) out.add(PrintLine('РН ККМ: ${f.regNumber}'));
    if (f.fiscalModule.isNotEmpty) out.add(PrintLine('ФМ: ${f.fiscalModule}'));
    out.addAll(wrapText('ФД: ${f.fd}${f.fpd.isEmpty ? '' : '  ФПД: ${f.fpd}'}', width).map(PrintLine.new));
  }
  if (r.offline) out.addAll(wrapText('Чек сохранён без связи — уйдёт на сервер сам', width).map(PrintLine.new));
  out.add(dash);
  out.add(const PrintLine('Спасибо за покупку!', center: true));
  return out;
}

double _gross(ReceiptLine l) => (l.qty * l.price * 100).roundToDouble() / 100;

/// 2026-10-22 (мобилка, админ): П6 — что зашить в QR на бумаге: проверка фискального чека или электронный чек.
String? receiptQrData(Receipt r) {
  final u = (r.fiscal?.checkUrl ?? r.eReceiptUrl ?? '').trim();
  return u.isEmpty ? null : u;
}

/// Ящик открывается только при наличных (или смешанной) оплате продажи — как TryOpenCashDrawerAfterSale на ПК.
bool receiptNeedsDrawer(Receipt r) => !r.isReturn && (r.payment == 'cash' || r.payment == 'mixed');

/// Готовые байты чека для принтера. [qr] — QR проверки/электронного чека под чеком; [openDrawer] — импульс ящика
/// после отрезки (в том же задании, без сброса принтера — чек не обрывается).
Uint8List receiptToEscPos(
  Receipt r, {
  int paperMm = 58,
  PrinterCodepage codepage = PrinterCodepage.cp866,
  int? codeTable,
  bool qr = true,
  bool openDrawer = false,
  int drawerPin = 0,
}) {
  final p = EscPos(codepage, codeTable: codeTable);
  for (final l in receiptLines(r, lineWidth(paperMm))) {
    p.line(l);
  }
  final data = qr ? receiptQrData(r) : null;
  if (data != null) {
    p.text(r.fiscal?.checkUrl != null ? 'Проверка чека' : 'Электронный чек', center: true);
    p.qr(data, size: paperMm >= 80 ? 7 : 5);
  }
  p.feedAndCut();
  if (openDrawer) p.openDrawer(pin: drawerPin);
  return p.bytes();
}

/// Только импульс ящика (кнопка проверки в настройках): со сбросом ESC @, как на ПК, — принтер без дела.
Uint8List drawerPulse({int pin = 0}) => (BytesBuilder()
      ..add([0x1B, 0x40])
      ..add([0x1B, 0x70, pin == 1 ? 1 : 0, 25, 250]))
    .toBytes();

/// Пробная страница: ширина ленты, кириллица и обе таблицы — по ней видно, что выбрать.
Uint8List testPage({int paperMm = 58, PrinterCodepage codepage = PrinterCodepage.cp866, int? codeTable, String title = 'NurMarket'}) {
  final w = lineWidth(paperMm);
  final p = EscPos(codepage, codeTable: codeTable)
    ..text(title, bold: true, big: true, center: true)
    ..text('Проверка печати', bold: true, center: true)
    ..text('-' * w)
    ..text('Лента $paperMm мм: $w знаков')
    ..text('1234567890' * (w ~/ 10) + '1234567890'.substring(0, w % 10))
    ..text('Таблица: ${codepage == PrinterCodepage.cp866 ? 'CP866' : 'CP1251'} (ESC t ${codeTable ?? defaultCodeTable(codepage)})')
    ..text('АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ')
    ..text('абвгдеёжзийклмнопрстуфхцчшщъыьэюя')
    ..text('№ 0123456789 · сом');
  for (final s in twoColumns('Картошка 0,500 кг x 40', '20', w)) {
    p.text(s);
  }
  p
    ..text('-' * w)
    ..text('Буквы видны — принтер настроен.', center: true)
    ..feedAndCut();
  return p.bytes();
}
