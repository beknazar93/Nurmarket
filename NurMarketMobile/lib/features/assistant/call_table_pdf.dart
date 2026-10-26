// 2026-10-26 (голос-управление, В4): «PDF» и «Поделиться» для таблицы звонка (раздел 4.2 ТЗ). Все строки вида (после сортировки
// и фильтра), «Итого» внизу, шрифт Montserrat (кириллица). Имена вместо кодов ([Д1] → имя) подставляет сама таблица: PDF
// остаётся на телефоне и уходит туда, куда выберет пользователь, — в Google он не идёт.
import 'dart:io';

import 'package:flutter/services.dart' show rootBundle;
import 'package:path_provider/path_provider.dart';
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;
import 'package:share_plus/share_plus.dart';

import '../../core/ai/assistant/call_table.dart';

/// Байты PDF. [shop] — подпись вверху. Широкая таблица (больше 5 колонок) — альбомная страница.
Future<List<int>> callTablePdfBytes(CallTable t, {String shop = '', pw.Font? regular, pw.Font? bold}) async {
  final reg = regular ?? pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Regular.ttf'));
  final bld = bold ?? pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Bold.ttf'));
  final doc = pw.Document(title: t.title, author: shop, creator: 'NurMarket');
  final wide = t.columns.length > 5;
  pw.Widget cell(String text, {bool b = false, bool right = false, PdfColor? color}) => pw.Padding(
    padding: const pw.EdgeInsets.symmetric(horizontal: 4, vertical: 3),
    child: pw.Text(
      text,
      textAlign: right ? pw.TextAlign.right : pw.TextAlign.left,
      style: pw.TextStyle(fontSize: 9, fontWeight: b ? pw.FontWeight.bold : pw.FontWeight.normal, color: color),
    ),
  );
  final head = [for (final c in t.columns) cell(c.title, b: true, right: c.numeric)];
  final rows = t.formattedRows(all: true);
  final body = <List<pw.Widget>>[];
  final idx = t.viewIndices;
  for (var r = 0; r < rows.length; r++) {
    body.add([
      for (var c = 0; c < t.columns.length; c++)
        cell(rows[r][c], right: t.columns[c].numeric, color: t.isNegative(idx[r], c) ? PdfColors.red800 : null),
    ]);
  }
  final total = t.totalsRow();
  doc.addPage(
    pw.MultiPage(
      pageFormat: wide ? PdfPageFormat.a4.landscape : PdfPageFormat.a4,
      margin: const pw.EdgeInsets.all(32),
      theme: pw.ThemeData.withFont(base: reg, bold: bld),
      footer: (c) => pw.Align(
        alignment: pw.Alignment.centerRight,
        child: pw.Text('${c.pageNumber} / ${c.pagesCount}', style: const pw.TextStyle(fontSize: 8, color: PdfColors.grey700)),
      ),
      build: (_) => [
        if (shop.isNotEmpty) pw.Text(shop, style: const pw.TextStyle(fontSize: 9, color: PdfColors.grey700)),
        pw.Text(t.title, style: pw.TextStyle(fontSize: 15, fontWeight: pw.FontWeight.bold)),
        if (t.subtitle != null && t.subtitle!.isNotEmpty) pw.Text(t.subtitle!, style: const pw.TextStyle(fontSize: 10, color: PdfColors.grey800)),
        pw.SizedBox(height: 8),
        if (t.card)
          pw.TableHelper.fromTextArray(
            data: [for (final r in rows) r],
            cellStyle: const pw.TextStyle(fontSize: 10),
            border: pw.TableBorder.all(color: PdfColors.grey400, width: .5),
          )
        else
          pw.Table(
            border: pw.TableBorder.all(color: PdfColors.grey400, width: .5),
            children: [
              pw.TableRow(decoration: const pw.BoxDecoration(color: PdfColors.grey200), children: head),
              for (final r in body) pw.TableRow(children: r),
              if (total != null)
                pw.TableRow(
                  decoration: const pw.BoxDecoration(color: PdfColors.amber100),
                  children: [for (var c = 0; c < total.length; c++) cell(total[c], b: true, right: t.columns[c].numeric)],
                ),
            ],
          ),
        if (t.note != null && t.note!.isNotEmpty) pw.Padding(padding: const pw.EdgeInsets.only(top: 6), child: pw.Text(t.note!, style: const pw.TextStyle(fontSize: 9))),
      ],
    ),
  );
  return doc.save();
}

/// Сделать PDF во временной папке и открыть «Поделиться». false — не вышло.
Future<bool> shareCallTablePdf(CallTable t, {String shop = ''}) async {
  try {
    final dir = await getTemporaryDirectory();
    final safe = t.title.replaceAll(RegExp(r'[^\wА-Яа-яЁёӘәӨөҮүҢңІіҚқҒғҺһ №.-]'), '_').trim();
    final name = safe.isEmpty ? 'table' : safe;
    final file = File('${dir.path}/$name.pdf');
    await file.writeAsBytes(await callTablePdfBytes(t, shop: shop));
    await SharePlus.instance.share(ShareParams(files: [XFile(file.path, mimeType: 'application/pdf', name: '$name.pdf')], text: t.title, subject: t.title));
    return true;
  } catch (_) {
    return false;
  }
}

/// «Поделиться» текстом (без PDF).
Future<bool> shareCallTableText(CallTable t) async {
  try {
    await SharePlus.instance.share(ShareParams(text: t.toPlainText(), subject: t.title));
    return true;
  } catch (_) {
    return false;
  }
}
