import 'dart:io';

import 'package:flutter/services.dart';
import 'package:path_provider/path_provider.dart';
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;
import 'package:share_plus/share_plus.dart';

import '../../core/util/ui.dart';
import '../receipt/receipt.dart';

/// 13.10, владелец: «чек должен в виде PDF отправляться клиенту» и «приведи чек по стандартам Кыргызстана».
/// Лента 80 мм с теми же реквизитами, что ReceiptView: продавец, ИНН, адрес, вид документа, номер, смена, дата, кассир,
/// позиции, скидки, итог, форма оплаты, фискальный блок (если есть) и QR. Шрифт — Montserrat: встроенные шрифты PDF
/// не знают кириллицу.
Future<File> buildReceiptPdf(Receipt r) async {
  final dir = await getTemporaryDirectory();
  final safe = r.fileName.replaceAll(RegExp(r'[^\wА-Яа-яЁё№ .-]'), '_');
  final file = File('${dir.path}/$safe.pdf');
  await file.writeAsBytes(await receiptPdfBytes(r));
  return file;
}

/// Байты PDF — без файловой системы (и для тестов).
Future<List<int>> receiptPdfBytes(Receipt r) async {
  final regular = pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Regular.ttf'));
  final bold = pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Bold.ttf'));
  const ink = PdfColor.fromInt(0xFF15202B);
  const soft = PdfColor.fromInt(0xFF667085);
  const red = PdfColor.fromInt(0xFFB42318);
  final f = r.fiscal;
  final qrData = f?.checkUrl ?? r.eReceiptUrl;

  pw.Widget pair(String label, String value, {bool strong = false, PdfColor? color}) => pw.Padding(
    padding: const pw.EdgeInsets.symmetric(vertical: 1),
    child: pw.Row(
      crossAxisAlignment: pw.CrossAxisAlignment.start,
      children: [
        pw.Text(label, style: const pw.TextStyle(fontSize: 8, color: soft)),
        pw.SizedBox(width: 6),
        pw.Expanded(
          child: pw.Text(
            value,
            textAlign: pw.TextAlign.right,
            style: pw.TextStyle(fontSize: 8, fontWeight: strong ? pw.FontWeight.bold : pw.FontWeight.normal, color: color ?? ink),
          ),
        ),
      ],
    ),
  );
  pw.Widget dashed() => pw.Padding(
    padding: const pw.EdgeInsets.symmetric(vertical: 4),
    child: pw.Divider(thickness: .5, color: PdfColors.grey500, borderStyle: pw.BorderStyle.dashed),
  );

  final doc = pw.Document(title: r.fileName, author: r.shop, creator: 'NurCRM');
  doc.addPage(
    pw.Page(
      pageFormat: PdfPageFormat.roll80.copyWith(marginLeft: 10, marginRight: 10, marginTop: 12, marginBottom: 12),
      theme: pw.ThemeData.withFont(base: regular, bold: bold),
      build: (_) => pw.Column(
        crossAxisAlignment: pw.CrossAxisAlignment.stretch,
        children: [
          pw.Text(
            r.shop,
            textAlign: pw.TextAlign.center,
            style: pw.TextStyle(fontSize: 12, fontWeight: pw.FontWeight.bold, color: ink),
          ),
          if (r.inn.isNotEmpty)
            pw.Text(
              'ИНН ${r.inn}',
              textAlign: pw.TextAlign.center,
              style: const pw.TextStyle(fontSize: 7.5, color: soft),
            ),
          if (r.address.isNotEmpty)
            pw.Text(
              r.address,
              textAlign: pw.TextAlign.center,
              style: const pw.TextStyle(fontSize: 7.5, color: soft),
            ),
          pw.SizedBox(height: 6),
          pw.Center(
            child: pw.Container(
              padding: const pw.EdgeInsets.symmetric(horizontal: 8, vertical: 3),
              decoration: pw.BoxDecoration(color: PdfColors.grey200, borderRadius: pw.BorderRadius.circular(8)),
              child: pw.Text(r.title, style: pw.TextStyle(fontSize: 7.5, fontWeight: pw.FontWeight.bold, letterSpacing: .4)),
            ),
          ),
          pw.SizedBox(height: 6),
          pair('Чек №', r.numberText, strong: true),
          if (r.shift.isNotEmpty) pair('Смена №', r.shift),
          pair('Дата и время', r.dateText),
          if (r.cashier.isNotEmpty) pair('Кассир', r.cashier),
          if (r.cashbox.isNotEmpty) pair('Касса', r.cashbox),
          if (r.client.isNotEmpty) pair('Покупатель', r.client),
          dashed(),
          for (var i = 0; i < r.lines.length; i++)
            pw.Padding(
              padding: const pw.EdgeInsets.symmetric(vertical: 2),
              child: pw.Column(
                crossAxisAlignment: pw.CrossAxisAlignment.stretch,
                children: [
                  pw.Text('${i + 1}. ${r.lines[i].name}', style: pw.TextStyle(fontSize: 8.5, fontWeight: pw.FontWeight.bold)),
                  pw.Row(
                    children: [
                      pw.Expanded(
                        child: pw.Text(
                          '${qtyText(r.lines[i].qty)}${r.lines[i].unit.isEmpty ? '' : ' ${r.lines[i].unit}'} × ${money(r.lines[i].price)}',
                          style: const pw.TextStyle(fontSize: 8, color: soft),
                        ),
                      ),
                      pw.Text(money(r.lines[i].total), style: pw.TextStyle(fontSize: 8.5, fontWeight: pw.FontWeight.bold)),
                    ],
                  ),
                  if (r.lines[i].discount > 0)
                    pw.Text(
                      'скидка −${money(r.lines[i].discount)}',
                      textAlign: pw.TextAlign.right,
                      style: const pw.TextStyle(fontSize: 7.5, color: red),
                    ),
                ],
              ),
            ),
          dashed(),
          if (r.discount > 0) ...[pair('Сумма без скидки', money(r.subtotal)), pair('Скидка', '−${money(r.discount)}', color: red)],
          pw.Padding(
            padding: const pw.EdgeInsets.symmetric(vertical: 3),
            child: pw.Row(
              children: [
                pw.Text('ИТОГО', style: pw.TextStyle(fontSize: 13, fontWeight: pw.FontWeight.bold)),
                pw.Spacer(),
                pw.Text('${money(r.total)} сом', style: pw.TextStyle(fontSize: 13, fontWeight: pw.FontWeight.bold)),
              ],
            ),
          ),
          for (final (k, v) in r.paymentLines) pair(k, v), // 2026-10-22: долг — внесено / долг / срок
          if (r.change > 0) pair('Сдача', money(r.change), strong: true),
          if (f != null) ...[
            dashed(),
            for (final t in f.taxes) pair(t.name, money(t.amount)),
            if (f.regNumber.isNotEmpty) pair('РН ККМ', f.regNumber),
            if (f.fiscalModule.isNotEmpty) pair('ФМ', f.fiscalModule),
            pair('ФД', f.fd),
            if (f.fpd.isNotEmpty) pair('ФПД', f.fpd),
          ],
          if (qrData != null) ...[
            pw.SizedBox(height: 8),
            pw.Center(
              child: pw.BarcodeWidget(barcode: pw.Barcode.qrCode(), data: qrData, width: 96, height: 96, color: ink),
            ),
            pw.SizedBox(height: 3),
            pw.Text(
              f?.checkUrl != null ? 'Проверка чека' : 'Электронный чек',
              textAlign: pw.TextAlign.center,
              style: const pw.TextStyle(fontSize: 7, color: soft),
            ),
          ],
          pw.SizedBox(height: 8),
          pw.Text(
            'Спасибо за покупку!',
            textAlign: pw.TextAlign.center,
            style: pw.TextStyle(fontSize: 9, fontWeight: pw.FontWeight.bold),
          ),
          pw.Text(
            'NurCRM',
            textAlign: pw.TextAlign.center,
            style: const pw.TextStyle(fontSize: 6.5, color: soft),
          ),
        ],
      ),
    ),
  );
  return doc.save();
}

/// Отправить чек клиенту PDF-файлом (WhatsApp, Telegram, почта — что выберет кассир).
Future<void> shareReceiptPdf(Receipt r) async {
  final file = await buildReceiptPdf(r);
  await SharePlus.instance.share(
    ShareParams(
      files: [XFile(file.path, mimeType: 'application/pdf', name: '${r.fileName}.pdf')],
      text: '${r.shop} · ${r.fileName}',
      subject: r.fileName,
    ),
  );
}
