import 'dart:io';

import 'package:flutter/services.dart';
import 'package:intl/intl.dart';
import 'package:path_provider/path_provider.dart';
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;
import 'package:share_plus/share_plus.dart';

import '../../core/util/ui.dart';
import '../receipt/receipt.dart';

/// 13.10, макет Д5 «Телефон — накладная»: по проведённому чеку — товарный чек, расходная накладная или счёт на оплату,
/// A4 PDF (продавец с ИНН и адресом, покупатель, основание, таблица №/товар/ед./кол-во/цена/сумма, итог, сумма прописью,
/// подписи). Тот же бланк, что «Печать и экспорт» на десктопе (DocForms).
enum DocKind { salesReceipt, waybill, invoice, quote }

String docTitle(DocKind k) => switch (k) {
  DocKind.salesReceipt => 'ТОВАРНЫЙ ЧЕК',
  DocKind.waybill => 'РАСХОДНАЯ НАКЛАДНАЯ',
  DocKind.invoice => 'СЧЁТ НА ОПЛАТУ',
  DocKind.quote => 'КОММЕРЧЕСКОЕ ПРЕДЛОЖЕНИЕ',
};

/// basis — «Основание» (по умолчанию — продажа по чеку); note — строка под итогом (срок оплаты, срок действия КП).
/// 2026-10-21, проверка КП и счетов: бланк был одной страницей (pw.Page) — у КП на 30+ строк таблица, итог и подписи
/// обрезались без предупреждения. Теперь страниц сколько нужно (pw.MultiPage, внизу «стр. N из M»); цены и суммы
/// с разрядами («12 500,00», как итог); у КП нет строки «Основание»; [requisites] — реквизиты магазина для счёта (банк,
/// р/с, БИК, телефон) под названием; [paid] — оплачено по счёту и остаток к оплате.
Future<List<int>> docPdfBytes(Receipt r, DocKind kind, {String? basis, String? note, String requisites = '', double paid = 0}) async {
  final regular = pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Regular.ttf'));
  final bold = pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Bold.ttf'));
  final doc = pw.Document(title: '${docTitle(kind)} ${r.number}', author: r.shop, creator: 'NurMarket');
  final m2 = NumberFormat('#,##0.00', 'ru');
  String d(DateTime t) => '${t.day.toString().padLeft(2, '0')}.${t.month.toString().padLeft(2, '0')}.${t.year}';
  pw.Widget cell(String t, {bool b = false, pw.TextAlign a = pw.TextAlign.left}) => pw.Padding(
    padding: const pw.EdgeInsets.symmetric(horizontal: 4, vertical: 3),
    child: pw.Text(
      t,
      textAlign: a,
      style: pw.TextStyle(fontSize: 9, fontWeight: b ? pw.FontWeight.bold : pw.FontWeight.normal),
    ),
  );
  pw.Widget right(String t, {double size = 10, bool b = false}) => pw.Align(
    alignment: pw.Alignment.centerRight,
    child: pw.Text(
      t,
      textAlign: pw.TextAlign.right,
      style: pw.TextStyle(fontSize: size, fontWeight: b ? pw.FontWeight.bold : pw.FontWeight.normal),
    ),
  );
  final subtotal = r.lines.fold<double>(0, (s, l) => s + l.total);
  final left = (r.total - paid).clamp(0, double.infinity).toDouble();
  final (signLeft, signRight) = switch (kind) {
    DocKind.invoice || DocKind.quote => ('Руководитель', 'Бухгалтер'),
    DocKind.salesReceipt => ('Продавец', 'Покупатель'),
    DocKind.waybill => ('Отпустил', 'Получил'),
  };
  final shopLines = [
    [if (r.address.isNotEmpty) r.address, if (r.inn.isNotEmpty) 'ИНН ${r.inn}'].join(' · '),
    ...requisites.split('\n').map((x) => x.trim()).where((x) => x.isNotEmpty),
  ].where((x) => x.isNotEmpty);
  doc.addPage(
    pw.MultiPage(
      pageFormat: PdfPageFormat.a4,
      margin: const pw.EdgeInsets.all(36),
      theme: pw.ThemeData.withFont(base: regular, bold: bold),
      crossAxisAlignment: pw.CrossAxisAlignment.stretch,
      footer: (c) => pw.Padding(
        padding: const pw.EdgeInsets.only(top: 8),
        child: pw.Row(
          children: [
            pw.Expanded(child: pw.Text('Сформировано в NurMarket', style: const pw.TextStyle(fontSize: 8, color: PdfColors.grey700))),
            if (c.pagesCount > 1)
              pw.Text('${r.number} · стр. ${c.pageNumber} из ${c.pagesCount}', style: const pw.TextStyle(fontSize: 8, color: PdfColors.grey700)),
          ],
        ),
      ),
      build: (c) => [
        pw.Row(
          crossAxisAlignment: pw.CrossAxisAlignment.start,
          children: [
            pw.Expanded(
              child: pw.Column(
                crossAxisAlignment: pw.CrossAxisAlignment.start,
                children: [
                  pw.Text(r.shop, style: pw.TextStyle(fontSize: 13, fontWeight: pw.FontWeight.bold)),
                  for (final x in shopLines) pw.Text(x, style: const pw.TextStyle(fontSize: 9)),
                ],
              ),
            ),
            pw.SizedBox(width: 12),
            pw.Text(
              basis != null ? 'от ${d(r.at)}' : 'от ${d(r.at)}\n${r.cashbox.isEmpty ? '' : '${r.cashbox} · '}чек ${r.number}',
              textAlign: pw.TextAlign.right,
              style: const pw.TextStyle(fontSize: 9),
            ),
          ],
        ),
        pw.SizedBox(height: 14),
        pw.Center(
          child: pw.Text('${docTitle(kind)} № ${r.number}', style: pw.TextStyle(fontSize: 15, fontWeight: pw.FontWeight.bold)),
        ),
        pw.SizedBox(height: 10),
        pw.Text(
          'Поставщик: ${r.shop}${r.inn.isEmpty ? '' : ', ИНН ${r.inn}'}${r.address.isEmpty ? '' : ', ${r.address}'}',
          style: const pw.TextStyle(fontSize: 10),
        ),
        pw.Text('Покупатель: ${r.client.isEmpty ? '______________________________' : r.client}', style: const pw.TextStyle(fontSize: 10)),
        if (kind != DocKind.quote) pw.Text('Основание: ${basis ?? 'продажа по чеку ${r.number}'}', style: const pw.TextStyle(fontSize: 10)),
        pw.SizedBox(height: 8),
        pw.Table(
          border: pw.TableBorder.all(width: .6),
          columnWidths: const {
            0: pw.FixedColumnWidth(24),
            1: pw.FlexColumnWidth(),
            2: pw.FixedColumnWidth(34),
            3: pw.FixedColumnWidth(48),
            4: pw.FixedColumnWidth(66),
            5: pw.FixedColumnWidth(74),
          },
          children: [
            pw.TableRow(
              repeat: true,
              decoration: const pw.BoxDecoration(color: PdfColor.fromInt(0xFFEEEEEE)),
              children: [
                cell('№', b: true),
                cell('Товар', b: true),
                cell('Ед.', b: true),
                cell('Кол-во', b: true, a: pw.TextAlign.right),
                cell('Цена', b: true, a: pw.TextAlign.right),
                cell('Сумма', b: true, a: pw.TextAlign.right),
              ],
            ),
            for (var i = 0; i < r.lines.length; i++)
              pw.TableRow(
                children: [
                  cell('${i + 1}'),
                  cell(r.lines[i].name),
                  cell(r.lines[i].unit),
                  cell(qtyText(r.lines[i].qty), a: pw.TextAlign.right),
                  cell(m2.format(r.lines[i].price), a: pw.TextAlign.right),
                  cell(m2.format(r.lines[i].total), a: pw.TextAlign.right),
                ],
              ),
          ],
        ),
        pw.SizedBox(height: 6),
        if (subtotal - r.total > 0.005) ...[
          right('Сумма без скидки: ${m2.format(subtotal)}'),
          right('Скидка: −${m2.format(subtotal - r.total)}'),
        ],
        right('Итого: ${money(r.total)} сом', size: 12, b: true),
        if (paid > 0.005) ...[
          right('Оплачено: ${money(paid)} сом'),
          right('К оплате: ${money(left)} сом', b: true),
        ],
        pw.SizedBox(height: 6),
        pw.Text('Всего наименований ${r.lines.length}, на сумму: ${amountInWords(r.total)}', style: const pw.TextStyle(fontSize: 10)),
        if (note != null)
          pw.Padding(
            padding: const pw.EdgeInsets.only(top: 6),
            child: pw.Text(note, style: pw.TextStyle(fontSize: 10, fontWeight: pw.FontWeight.bold)),
          ),
        pw.SizedBox(height: 30),
        pw.Row(
          children: [
            pw.Expanded(
              child: pw.Text(
                '$signLeft ____________ ${kind == DocKind.invoice || kind == DocKind.quote ? '' : r.cashier}',
                style: const pw.TextStyle(fontSize: 10),
              ),
            ),
            pw.Expanded(child: pw.Text('$signRight ____________', style: const pw.TextStyle(fontSize: 10))),
          ],
        ),
        pw.SizedBox(height: 20),
        pw.Text('М.П.', style: const pw.TextStyle(fontSize: 9)),
      ],
    ),
  );
  return doc.save();
}

Future<void> shareDocPdf(Receipt r, DocKind kind, {String? basis, String? note, String requisites = '', double paid = 0}) async {
  final dir = await getTemporaryDirectory();
  final name =
      '${switch (kind) {
        DocKind.salesReceipt => 'tovarny-chek',
        DocKind.waybill => 'nakladnaya',
        DocKind.invoice => 'schet',
        DocKind.quote => 'kp',
      }}-${r.number.replaceAll(RegExp(r'[^\w-]'), '')}';
  final file = File('${dir.path}/$name.pdf');
  await file.writeAsBytes(await docPdfBytes(r, kind, basis: basis, note: note, requisites: requisites, paid: paid));
  await SharePlus.instance.share(
    ShareParams(
      files: [XFile(file.path, mimeType: 'application/pdf', name: '$name.pdf')],
      text: '${r.shop} · ${docTitle(kind).toLowerCase()} № ${r.number}',
    ),
  );
}

const _units = ['', 'один', 'два', 'три', 'четыре', 'пять', 'шесть', 'семь', 'восемь', 'девять'];
const _unitsF = ['', 'одна', 'две', 'три', 'четыре', 'пять', 'шесть', 'семь', 'восемь', 'девять'];
const _teens = ['десять', 'одиннадцать', 'двенадцать', 'тринадцать', 'четырнадцать', 'пятнадцать', 'шестнадцать', 'семнадцать', 'восемнадцать', 'девятнадцать'];
const _tens = ['', '', 'двадцать', 'тридцать', 'сорок', 'пятьдесят', 'шестьдесят', 'семьдесят', 'восемьдесят', 'девяносто'];
const _hundreds = ['', 'сто', 'двести', 'триста', 'четыреста', 'пятьсот', 'шестьсот', 'семьсот', 'восемьсот', 'девятьсот'];

/// «Семь тысяч восемьсот восемьдесят шесть сом 00 тыйын» — как CrmDocuments.AmountInWords на десктопе.
String amountInWords(double amount) {
  final rounded = (amount * 100).round() / 100;
  final whole = rounded.floor();
  final tyiyn = ((rounded - whole) * 100).round();
  final parts = <String>[];
  void group(int v, bool fem, String one, String few, String many) {
    final h = v ~/ 100, t = v % 100;
    if (h > 0) parts.add(_hundreds[h]);
    if (t >= 10 && t < 20) {
      parts.add(_teens[t - 10]);
    } else {
      if (t ~/ 10 > 0) parts.add(_tens[t ~/ 10]);
      if (t % 10 > 0) parts.add((fem ? _unitsF : _units)[t % 10]);
    }
    if (one.isNotEmpty) {
      final last = t % 10;
      parts.add(
        t >= 11 && t <= 14
            ? many
            : last == 1
            ? one
            : (last >= 2 && last <= 4)
            ? few
            : many,
      );
    }
  }

  if (whole == 0) parts.add('ноль');
  final b = whole ~/ 1000000000, m = whole ~/ 1000000 % 1000, th = whole ~/ 1000 % 1000, rest = whole % 1000;
  if (b > 0) group(b, false, 'миллиард', 'миллиарда', 'миллиардов');
  if (m > 0) group(m, false, 'миллион', 'миллиона', 'миллионов');
  if (th > 0) group(th, true, 'тысяча', 'тысячи', 'тысяч');
  if (rest > 0) group(rest, false, '', '', '');
  final text = '${parts.where((p) => p.isNotEmpty).join(' ')} сом ${tyiyn.toString().padLeft(2, '0')} тыйын';
  return text[0].toUpperCase() + text.substring(1);
}
