import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show rootBundle;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';
import 'package:pdf/pdf.dart';
import 'package:pdf/widgets.dart' as pw;
import 'package:share_plus/share_plus.dart';

import '../../core/api/session_store.dart';
import '../../core/print/escpos.dart';
import '../../core/print/printer_service.dart';
import '../../core/util/ui.dart';
import '../profile/printer_settings.dart';

/// 2026-10-26 (живой аудит, П5/П6): «каждый денежный шаг оставляет документ». Простой документ-«лента» из строк: квитанция
/// об оплате долга, ордер внесения/изъятия, возврат, X- и Z-отчёт смены. Один документ — четыре вида: экран ([TapeView]),
/// PDF (лента 80 мм), текст для мессенджера и ESC/POS для термопринтера (кириллица и «ү/ө/ң» — как у чека, см. escpos.dart).
enum TapeKind { heading, row, total, note, rule }

enum TapeTone { normal, good, bad, warn, soft }

class TapeItem {
  final TapeKind kind;
  final String text;
  final String value;
  final TapeTone tone;
  const TapeItem._(this.kind, this.text, this.value, this.tone);
  const TapeItem.heading(String text) : this._(TapeKind.heading, text, '', TapeTone.normal);
  const TapeItem.row(String label, String value, {TapeTone tone = TapeTone.normal}) : this._(TapeKind.row, label, value, tone);
  const TapeItem.total(String label, String value, {TapeTone tone = TapeTone.normal}) : this._(TapeKind.total, label, value, tone);
  const TapeItem.note(String text, {TapeTone tone = TapeTone.soft}) : this._(TapeKind.note, text, '', tone);
  const TapeItem.rule() : this._(TapeKind.rule, '', '', TapeTone.normal);
}

/// Дата и время в одной записи для документов: 26.10.2026 14:05.
String tapeDate(DateTime d) =>
    '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}.${d.year} ${d.hour.toString().padLeft(2, '0')}:${d.minute.toString().padLeft(2, '0')}';

class TapeDoc {
  final String shop;
  final String inn;
  final String address;
  final String title;
  final String fileName;

  /// Шапка: «Дата: …», «Кассир: …».
  final List<(String, String)> meta;
  final List<TapeItem> items;
  const TapeDoc({
    required this.shop,
    this.inn = '',
    this.address = '',
    required this.title,
    required this.fileName,
    this.meta = const [],
    required this.items,
  });

  /// Реквизиты продавца — из сессии (как у чека).
  factory TapeDoc.forSession(
    Session? s, {
    required String title,
    required String fileName,
    List<(String, String)> meta = const [],
    required List<TapeItem> items,
  }) => TapeDoc(
    shop: (s?.companyName.isNotEmpty ?? false) ? s!.companyName : 'NurCRM',
    inn: s?.companyInn ?? '',
    address: s?.companyAddress ?? '',
    title: title,
    fileName: fileName,
    meta: [
      for (final m in meta)
        if (m.$2.trim().isNotEmpty) m,
    ],
    items: items,
  );

  /// Текст для мессенджера.
  String toText() {
    const dash = '--------------------------------';
    final b = StringBuffer()..writeln(shop);
    if (inn.isNotEmpty) b.writeln('ИНН $inn');
    if (address.isNotEmpty) b.writeln(address);
    b
      ..writeln(title)
      ..writeln(dash);
    for (final (k, v) in meta) {
      b.writeln('$k: $v');
    }
    b.writeln(dash);
    for (final it in items) {
      switch (it.kind) {
        case TapeKind.heading:
          b.writeln(it.text);
        case TapeKind.row || TapeKind.total:
          b.writeln(it.value.isEmpty ? it.text : '${it.text}: ${it.value}');
        case TapeKind.note:
          b.writeln(it.text);
        case TapeKind.rule:
          b.writeln(dash);
      }
    }
    return b.toString();
  }

  /// Строки под ширину ленты принтера.
  List<PrintLine> toPrintLines(int width) {
    final dash = PrintLine('-' * width);
    final out = <PrintLine>[];
    for (final s in wrapText(shop, width)) {
      out.add(PrintLine(s, bold: true, center: true));
    }
    if (inn.isNotEmpty) out.add(PrintLine('ИНН $inn', center: true));
    if (address.isNotEmpty) out.addAll(wrapText(address, width).map((s) => PrintLine(s, center: true)));
    out.add(dash);
    out.addAll(wrapText(title, width).map((s) => PrintLine(s, bold: true, center: true)));
    for (final (k, v) in meta) {
      out.addAll(wrapText('$k: $v', width).map(PrintLine.new));
    }
    out.add(dash);
    for (final it in items) {
      switch (it.kind) {
        case TapeKind.heading:
          out.addAll(wrapText(it.text, width).map((s) => PrintLine(s, bold: true)));
        case TapeKind.row:
          out.addAll(twoColumns(it.text, it.value, width).map(PrintLine.new));
        case TapeKind.total:
          out.addAll(twoColumns(it.text, it.value, width).map((s) => PrintLine(s, bold: true)));
        case TapeKind.note:
          out.addAll(wrapText(it.text, width).map(PrintLine.new));
        case TapeKind.rule:
          out.add(dash);
      }
    }
    return out;
  }

  Uint8List toEscPos({int paperMm = 58, PrinterCodepage codepage = PrinterCodepage.cp866, int? codeTable}) {
    final p = EscPos(codepage, codeTable: codeTable);
    for (final l in toPrintLines(lineWidth(paperMm))) {
      p.line(l);
    }
    p.feedAndCut();
    return p.bytes();
  }

  /// PDF — лента 80 мм, шрифт Montserrat (знает кириллицу и «ү/ө/ң»).
  Future<List<int>> pdfBytes() async {
    final regular = pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Regular.ttf'));
    final bold = pw.Font.ttf(await rootBundle.load('assets/fonts/Montserrat-Bold.ttf'));
    const ink = PdfColor.fromInt(0xFF15202B);
    const soft = PdfColor.fromInt(0xFF667085);
    const green = PdfColor.fromInt(0xFF0E7A3A);
    const red = PdfColor.fromInt(0xFFB42318);
    const amber = PdfColor.fromInt(0xFF9A5B00);
    PdfColor color(TapeTone t) => switch (t) {
      TapeTone.good => green,
      TapeTone.bad => red,
      TapeTone.warn => amber,
      TapeTone.soft => soft,
      TapeTone.normal => ink,
    };
    pw.Widget pair(String label, String value, {bool strong = false, PdfColor c = ink, double size = 8}) => pw.Padding(
      padding: const pw.EdgeInsets.symmetric(vertical: 1.5),
      child: pw.Row(
        crossAxisAlignment: pw.CrossAxisAlignment.start,
        children: [
          pw.Expanded(
            child: pw.Text(
              label,
              style: pw.TextStyle(fontSize: size, color: strong ? c : soft, fontWeight: strong ? pw.FontWeight.bold : pw.FontWeight.normal),
            ),
          ),
          pw.SizedBox(width: 6),
          pw.Text(
            value,
            textAlign: pw.TextAlign.right,
            style: pw.TextStyle(fontSize: size, fontWeight: strong ? pw.FontWeight.bold : pw.FontWeight.normal, color: c),
          ),
        ],
      ),
    );
    pw.Widget dashed() => pw.Padding(
      padding: const pw.EdgeInsets.symmetric(vertical: 4),
      child: pw.Divider(thickness: .5, color: PdfColors.grey500, borderStyle: pw.BorderStyle.dashed),
    );
    pw.Widget centered(String t, {double size = 8, bool strong = false, PdfColor c = ink}) => pw.Text(
      t,
      textAlign: pw.TextAlign.center,
      style: pw.TextStyle(fontSize: size, color: c, fontWeight: strong ? pw.FontWeight.bold : pw.FontWeight.normal),
    );
    final doc = pw.Document(title: fileName, author: shop, creator: 'NurCRM');
    doc.addPage(
      pw.Page(
        pageFormat: PdfPageFormat.roll80.copyWith(marginLeft: 10, marginRight: 10, marginTop: 12, marginBottom: 12),
        theme: pw.ThemeData.withFont(base: regular, bold: bold),
        build: (_) => pw.Column(
          crossAxisAlignment: pw.CrossAxisAlignment.stretch,
          children: [
            centered(shop, size: 12, strong: true),
            if (inn.isNotEmpty) centered('ИНН $inn', c: soft),
            if (address.isNotEmpty) centered(address, c: soft),
            dashed(),
            centered(title, size: 10, strong: true),
            pw.SizedBox(height: 4),
            for (final (k, v) in meta) pair(k, v),
            dashed(),
            for (final it in items)
              switch (it.kind) {
                TapeKind.heading => pw.Padding(
                  padding: const pw.EdgeInsets.only(top: 4, bottom: 2),
                  child: pw.Text(it.text, style: pw.TextStyle(fontSize: 8.5, fontWeight: pw.FontWeight.bold, color: ink)),
                ),
                TapeKind.row => pair(it.text, it.value, c: color(it.tone)),
                TapeKind.total => pair(it.text, it.value, strong: true, c: color(it.tone), size: 10),
                TapeKind.note => pw.Padding(
                  padding: const pw.EdgeInsets.symmetric(vertical: 1.5),
                  child: pw.Text(it.text, style: pw.TextStyle(fontSize: 8, color: color(it.tone))),
                ),
                TapeKind.rule => dashed(),
              },
          ],
        ),
      ),
    );
    return doc.save();
  }

  Future<File> buildPdf() async {
    final dir = await getTemporaryDirectory();
    final safe = fileName.replaceAll(RegExp(r'[^\wА-Яа-яЁё№ .-]'), '_');
    final file = File('${dir.path}/$safe.pdf');
    await file.writeAsBytes(await pdfBytes());
    return file;
  }

  Future<void> sharePdf() async {
    final file = await buildPdf();
    await SharePlus.instance.share(ShareParams(files: [XFile(file.path, mimeType: 'application/pdf', name: '$fileName.pdf')], text: '$shop · $fileName', subject: fileName));
  }

  Future<void> shareText() => SharePlus.instance.share(ShareParams(text: toText(), subject: title));
}

/// Печать документа на принтер чеков — как printReceiptUi: принтер не настроен — подсказка, ошибка — понятным текстом.
Future<void> printTapeUi(BuildContext context, TapeDoc doc) async {
  final l = context.l;
  final service = ProviderScope.containerOf(context, listen: false).read(printerProvider);
  final c = await service.load();
  if (!c.ready) {
    if (context.mounted) toast(context, l.prnErrNotConfigured, error: true);
    return;
  }
  try {
    await service.send(c, doc.toEscPos(paperMm: c.paperMm, codepage: c.codepage, codeTable: c.codeTable));
    if (context.mounted) toast(context, l.prnSent);
  } on PrinterException catch (e) {
    if (context.mounted) toast(context, printerProblemText(l, e), error: true);
  }
}

Color _toneColor(BuildContext context, TapeTone t) {
  final p = context.pal;
  return switch (t) {
    TapeTone.good => p.success,
    TapeTone.bad => p.danger,
    TapeTone.warn => p.warn,
    TapeTone.soft => p.textSoft,
    TapeTone.normal => p.text,
  };
}

/// Строки документа (без шапки продавца) — и на ленте, и в карточках экрана «Закрыть смену».
class TapeItemsList extends StatelessWidget {
  final List<TapeItem> items;
  const TapeItemsList(this.items, {super.key});

  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final it in items)
          switch (it.kind) {
            TapeKind.heading => Padding(
              padding: const EdgeInsets.only(top: 6, bottom: 4),
              child: Text(
                it.text.toUpperCase(),
                style: TextStyle(fontSize: 11, fontWeight: FontWeight.w700, letterSpacing: .4, color: p.textSoft),
              ),
            ),
            TapeKind.rule => const Divider(height: 18),
            TapeKind.note => Padding(
              padding: const EdgeInsets.symmetric(vertical: 3),
              child: Text(it.text, style: TextStyle(fontSize: 12.5, color: _toneColor(context, it.tone))),
            ),
            TapeKind.row || TapeKind.total => Padding(
              padding: const EdgeInsets.symmetric(vertical: 4),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Text(
                      it.text,
                      style: TextStyle(
                        fontSize: it.kind == TapeKind.total ? 16 : 14,
                        fontWeight: it.kind == TapeKind.total ? FontWeight.w800 : FontWeight.w500,
                        color: it.kind == TapeKind.total && it.tone == TapeTone.normal ? p.text : (it.tone == TapeTone.normal ? p.textSoft : _toneColor(context, it.tone)),
                      ),
                    ),
                  ),
                  const SizedBox(width: 10),
                  Text(
                    it.value,
                    textAlign: TextAlign.right,
                    style: TextStyle(fontSize: it.kind == TapeKind.total ? 16 : 14, fontWeight: FontWeight.w800, color: _toneColor(context, it.tone)),
                  ),
                ],
              ),
            ),
          },
      ],
    );
  }
}

/// Документ на экране — бумажная лента: реквизиты продавца, название, шапка и строки.
class TapeView extends StatelessWidget {
  final TapeDoc doc;
  const TapeView({super.key, required this.doc});

  @override
  Widget build(BuildContext context) {
    final p = context.pal;
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Container(
          padding: const EdgeInsets.fromLTRB(18, 20, 18, 20),
          decoration: BoxDecoration(
            color: p.panel,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: p.border),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(doc.shop, textAlign: TextAlign.center, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
              if (doc.inn.isNotEmpty) Text('ИНН ${doc.inn}', textAlign: TextAlign.center, style: TextStyle(fontSize: 12, color: p.textSoft)),
              if (doc.address.isNotEmpty) Text(doc.address, textAlign: TextAlign.center, style: TextStyle(fontSize: 12, color: p.textSoft)),
              const Divider(height: 22),
              Text(doc.title, textAlign: TextAlign.center, style: const TextStyle(fontSize: 15, fontWeight: FontWeight.w800)),
              const SizedBox(height: 8),
              for (final (k, v) in doc.meta)
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 2),
                  child: Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text('$k  ', style: TextStyle(fontSize: 12.5, color: p.textSoft)),
                      Expanded(
                        child: Text(v, textAlign: TextAlign.right, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600)),
                      ),
                    ],
                  ),
                ),
              const Divider(height: 22),
              TapeItemsList(doc.items),
            ],
          ),
        ),
      ),
    );
  }
}

/// «Печать / PDF / Отправить» — значок и короткая подпись в одну строку.
class TapeActions extends StatelessWidget {
  final TapeDoc doc;
  const TapeActions(this.doc, {super.key});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    Widget btn(IconData icon, String label, VoidCallback onTap) => Expanded(
      child: OutlinedButton(
        style: OutlinedButton.styleFrom(minimumSize: const Size(0, 52), padding: const EdgeInsets.symmetric(horizontal: 6)),
        onPressed: onTap,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 20),
            const SizedBox(height: 2),
            FittedBox(
              fit: BoxFit.scaleDown,
              child: Text(label, maxLines: 1, softWrap: false),
            ),
          ],
        ),
      ),
    );
    return Row(
      children: [
        btn(Icons.print_outlined, l.tapePrint, () => printTapeUi(context, doc)),
        const SizedBox(width: 8),
        btn(Icons.picture_as_pdf_outlined, 'PDF', () => doc.sharePdf()),
        const SizedBox(width: 8),
        btn(Icons.chat_outlined, l.tapeSend, () => doc.shareText()),
      ],
    );
  }
}

/// Отдельный экран документа: [banner] (крупная строка итога), лента, «Печать / PDF / Отправить» и «Готово».
/// [lockBack] — «Назад» тоже закрывает документ через [onDone] (Z-отчёт: смена закрыта, возвращаться в неё нельзя).
class TapeDocScreen extends StatelessWidget {
  final TapeDoc doc;
  final String title;
  final String? subtitle;
  final Widget? banner;
  final VoidCallback? onDone;
  final bool lockBack;
  const TapeDocScreen({super.key, required this.doc, required this.title, this.subtitle, this.banner, this.onDone, this.lockBack = false});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    void done() => onDone != null ? onDone!() : Navigator.of(context).pop();
    final body = Scaffold(
      appBar: ScreenTitle(title, subtitle: subtitle),
      body: ListView(
        padding: EdgeInsets.fromLTRB(16, 8, 16, 24 + MediaQuery.paddingOf(context).bottom),
        children: [
          if (banner != null) ...[banner!, const SizedBox(height: 12)],
          TapeView(doc: doc),
          const SizedBox(height: 14),
          TapeActions(doc),
        ],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 6, 16, 12),
          child: FilledButton(onPressed: done, child: Text(l.done)),
        ),
      ),
    );
    if (!lockBack) return body;
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) done();
      },
      child: body,
    );
  }
}
