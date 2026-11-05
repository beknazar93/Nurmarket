import 'dart:convert';
import 'dart:typed_data';

import 'package:barcode/barcode.dart';

/// 2026-11-05 (волна 0-D, М5-03; разрешено владельцем, пакет barcode, Apache-2.0): штрихкод этикетки КАРТИНКОЙ для термопринтера этикеток
/// (TSPL BITMAP). Принтер сам не рисует кыргызские буквы и не всегда читаемо тянет свой BARCODE, а картинка одинакова везде.
/// Чистые функции без экрана и принтера: проверяются юнит-тестом. К экрану печати этикеток подключаются в волне 3 (с принтером в руках).

/// Чёрно-белая картинка: [bytes] — строки по [rowBytes] байт, старший бит слева, бит 1 = ЧЁРНАЯ точка.
class MonoBitmap {
  final int width;
  final int height;
  final Uint8List bytes;
  const MonoBitmap(this.width, this.height, this.bytes);

  int get rowBytes => (width + 7) ~/ 8;

  bool isBlack(int x, int y) => (bytes[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1 == 1;

  /// Сколько чёрных точек (для проверок).
  int get blackCount {
    var n = 0;
    for (final b in bytes) {
      for (var i = 0; i < 8; i++) {
        n += (b >> i) & 1;
      }
    }
    return n;
  }
}

/// Какой штрихкод рисовать по тексту: 13 цифр с верной контрольной цифрой — EAN-13, 8 — EAN-8, иначе Code 128.
Barcode barcodeFor(String data) {
  final d = data.trim();
  if (RegExp(r'^\d{13}$').hasMatch(d) && Barcode.ean13().isValid(d)) return Barcode.ean13();
  if (RegExp(r'^\d{8}$').hasMatch(d) && Barcode.ean8().isValid(d)) return Barcode.ean8();
  return Barcode.code128();
}

/// Нарисовать штрихкод [data] картинкой [widthDots] × [heightDots] точек (без подписи — подпись печатает принтер или другой слой).
/// Пустой текст или текст, который кодировать нельзя, — null (этикетка печатается без штрихкода, а не падает).
MonoBitmap? renderBarcodeBitmap(String data, {required int widthDots, required int heightDots}) {
  final text = data.trim();
  if (text.isEmpty || widthDots <= 0 || heightDots <= 0) return null;
  final rowBytes = (widthDots + 7) ~/ 8;
  final bytes = Uint8List(rowBytes * heightDots);
  try {
    for (final e in barcodeFor(text).make(text, width: widthDots.toDouble(), height: heightDots.toDouble())) {
      if (e is! BarcodeBar || !e.black) continue;
      final x0 = e.left.round().clamp(0, widthDots);
      final x1 = e.right.round().clamp(0, widthDots);
      final y0 = e.top.round().clamp(0, heightDots);
      final y1 = e.bottom.round().clamp(0, heightDots);
      for (var y = y0; y < y1; y++) {
        for (var x = x0; x < x1; x++) {
          bytes[y * rowBytes + (x >> 3)] |= 0x80 >> (x & 7);
        }
      }
    }
  } on BarcodeException {
    return null;
  }
  return MonoBitmap(widthDots, heightDots, bytes);
}

/// Задание TSPL: одна этикетка [widthMm] × [heightMm] с картинкой [bitmap] в точке ([x], [y]) и [copies] копиями.
/// В TSPL у BITMAP бит 0 — чёрная точка, бит 1 — белая, поэтому байты картинки инвертируются. Режим 0 — запись поверх.
/// На реальном принтере (Xprinter/TSC) проверяется в волне 3: читается ли EAN-13 сканером кассы.
Uint8List tsplBitmapLabel({required int widthMm, required int heightMm, required MonoBitmap bitmap, int gapMm = 2, int copies = 1, int x = 0, int y = 0}) {
  final b = BytesBuilder();
  b.add(latin1.encode('SIZE $widthMm mm,$heightMm mm\r\nGAP $gapMm mm,0 mm\r\nDIRECTION 1\r\nCLS\r\n'));
  b.add(latin1.encode('BITMAP $x,$y,${bitmap.rowBytes},${bitmap.height},0,'));
  b.add(Uint8List.fromList([for (final v in bitmap.bytes) ~v & 0xFF]));
  b.add(latin1.encode('\r\nPRINT ${copies < 1 ? 1 : copies},1\r\n'));
  return b.toBytes();
}
