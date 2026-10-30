import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:path_provider/path_provider.dart';
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;
import 'package:share_plus/share_plus.dart';

import '../../core/api/api_client.dart';
import '../../core/util/ui.dart';
import '../scan/barcode_lookup.dart';

// 2026-10-26 (живой аудит, П7, С-12): у товара с новым (сгенерированным) штрихкодом нет этикетки — значит, его нельзя
// сканировать. Здесь: проверка кода по серверу, PDF этикетки 58 × 40 мм (название, штрихкод, цена) и вопрос «Напечатать
// этикетку?» после сохранения. Печать — через «Поделиться» (PDF уходит в службу печати телефона, мессенджер, почту).

/// Сколько ждём сервер при проверке нового кода: не блокирует ввод — карточка уже открыта, проверка идёт фоном.
const barcodeServerCheckTimeout = Duration(seconds: 2);

/// Название товара, у которого этот код уже есть на сервере (products/list/?barcode=); null — свободен, нет связи
/// или сервер не ответил за 2 с (тогда дубль поймает проверка при сохранении). Совпадение проверяется по самому коду:
/// если сервер фильтр не понял и вернул что попало, чужие товары за «владельца» не считаются.
Future<String?> serverBarcodeOwner(ApiClient api, String code, {String? exceptProductId}) async {
  final c = code.trim();
  if (c.isEmpty) return null;
  try {
    final data = await api
        .get('api/main/products/list/', query: {'barcode': c, 'page': 1, 'page_size': 5}, timeout: barcodeServerCheckTimeout)
        .timeout(barcodeServerCheckTimeout + const Duration(milliseconds: 500));
    for (final p in asList(data)) {
      if (exceptProductId != null && '${p['id']}' == exceptProductId) continue;
      final alt = p['alternate_barcodes'] ?? p['alt_barcodes'] ?? p['barcodes'];
      final codes = <String>{
        '${p['barcode'] ?? ''}'.trim(),
        if (alt is List)
          for (final a in alt) (a is Map ? '${a['barcode'] ?? ''}' : '$a').trim(),
      };
      if (codes.contains(c)) return '${p['name'] ?? c}';
    }
  } catch (_) {
    // нет связи, 2 с вышли, ошибка сервера — не мешаем
  }
  return null;
}

/// Штрихкод этикетки: EAN-13 с верной контрольной цифрой рисуется как EAN-13, остальное (Code128) — как Code128;
/// не ASCII (нельзя в Code128) — QR.
pw.Barcode labelBarcodeFor(String code) {
  if (validEan13(code)) return pw.Barcode.ean13();
  if (code.codeUnits.every((u) => u >= 32 && u < 127)) return pw.Barcode.code128();
  return pw.Barcode.qrCode();
}

/// PDF этикетки 58 × 40 мм: название (до 2 строк), штрихкод с цифрами, цена. Шрифт — Montserrat (кириллица).
Future<List<int>> labelPdfBytes({required String name, required String code, required double price, String currency = ''}) async {
  final regular = pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Regular.ttf'));
  final bold = pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Bold.ttf'));
  final doc = pw.Document(title: name, creator: 'NurCRM');
  doc.addPage(
    pw.Page(
      pageFormat: PdfPageFormat(58 * PdfPageFormat.mm, 40 * PdfPageFormat.mm, marginAll: 2 * PdfPageFormat.mm),
      theme: pw.ThemeData.withFont(base: regular, bold: bold),
      build: (_) => pw.Column(
        crossAxisAlignment: pw.CrossAxisAlignment.stretch,
        children: [
          pw.Text(name, maxLines: 2, style: pw.TextStyle(fontSize: 8, fontWeight: pw.FontWeight.bold)),
          pw.SizedBox(height: 2),
          pw.Expanded(
            child: pw.BarcodeWidget(barcode: labelBarcodeFor(code), data: code, drawText: true, textStyle: pw.TextStyle(font: regular, fontSize: 7)),
          ),
          pw.SizedBox(height: 2),
          if (price > 0)
            pw.Text(
              '${money(price)}${currency.isEmpty ? '' : ' $currency'}',
              textAlign: pw.TextAlign.center,
              style: pw.TextStyle(fontSize: 13, fontWeight: pw.FontWeight.bold),
            ),
        ],
      ),
    ),
  );
  return doc.save();
}

/// Собрать PDF этикетки и отдать в «Поделиться» (печать / сохранить / отправить).
Future<void> shareLabel({required String name, required String code, required double price, String currency = ''}) async {
  final dir = await getTemporaryDirectory();
  final file = File('${dir.path}/label-${code.replaceAll(RegExp(r'[^\w-]'), '')}.pdf');
  await file.writeAsBytes(await labelPdfBytes(name: name, code: code, price: price, currency: currency));
  await SharePlus.instance.share(
    ShareParams(files: [XFile(file.path, mimeType: 'application/pdf', name: 'label-$code.pdf')], text: '$name · $code'),
  );
}

/// «Напечатать этикетку?» — окно после сохранения товара с новым кодом. Одно касание «Напечатать этикетку» собирает PDF.
Future<void> askPrintLabel(BuildContext context, {required String name, required String code, required double price}) async {
  final l = context.l;
  final ok = await showDialog<bool>(
    context: context,
    builder: (c) => AlertDialog(
      title: Text(l.printLabel),
      content: Text(l.printLabelAsk(name, code)),
      actions: [
        TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.notNow)),
        FilledButton.icon(onPressed: () => Navigator.pop(c, true), icon: const Icon(Icons.print_outlined), label: Text(l.printLabel)),
      ],
    ),
  );
  if (ok != true || !context.mounted) return;
  await printLabelNow(context, name: name, code: code, price: price);
}

/// Печать этикетки без вопроса (кнопка в карточке товара).
Future<void> printLabelNow(BuildContext context, {required String name, required String code, required double price}) async {
  final l = context.l;
  try {
    await shareLabel(name: name, code: code, price: price, currency: l.som);
  } catch (e) {
    if (context.mounted) toast(context, '$e', error: true);
  }
}
