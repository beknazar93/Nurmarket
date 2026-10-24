import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../../core/api/api_client.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../receipt/receipt.dart';
import 'receipt_pdf.dart';

/// Ссылка на электронный чек из ответа сервера (ТЗ бэкенда, часть 20, раздел 8: https://check.nurcrm.kg/{token}).
/// null — сервер ещё не выдаёт электронные чеки.
/// 13.10, владелец: «QR при скане — скачивался чек в виде PDF»: QR ведёт сразу на PDF (часть 20: `{token}.pdf`),
/// камера покупателя открывает файл, а не страницу.
String? eReceiptUrl(Map<String, dynamic>? sale) {
  final e = sale?['e_receipt'];
  final nested = e is Map<String, dynamic> ? e : null;
  final pdf = str(sale, 'e_receipt_pdf_url') ?? str(nested, 'pdf_url');
  if (pdf != null && pdf.startsWith('http')) return pdf;
  final url = str(sale, 'e_receipt_url') ?? str(nested, 'url') ?? str(sale, 'receipt_url');
  if (url != null && url.startsWith('http')) return url.endsWith('.pdf') ? url : '${url.replaceAll(RegExp(r'/+$'), '')}.pdf';
  final token = str(sale, 'e_receipt_token') ?? str(nested, 'token');
  return token == null ? null : 'https://check.nurcrm.kg/$token.pdf';
}

/// Текст чека для QR без ссылки: укладываем в ~1100 байт (QR с кириллицей больше — плохо читается с экрана телефона).
String compactReceiptText(String full) {
  if (utf8.encode(full).length <= 1100) return full;
  final lines = full.split('\n');
  final head = lines.take(3).toList();
  final tail = lines.where((l) => l.contains(':')).toList().reversed.take(3).toList().reversed;
  final middle = <String>[];
  for (final l in lines.skip(3)) {
    if (utf8.encode([...head, ...middle, l, '…', ...tail].join('\n')).length > 1050) break;
    middle.add(l);
  }
  return [...head, ...middle, '…', ...tail].join('\n');
}

/// Экран «QR-код чека»: покупатель наводит камеру своего телефона — по ссылке открывается и скачивается чек;
/// пока сервер не выдаёт ссылку — камера покажет чек текстом (его можно сохранить), это работает и без интернета.
class ReceiptQrScreen extends StatelessWidget {
  final Receipt receipt;
  final String title;
  const ReceiptQrScreen({super.key, required this.receipt, required this.title});

  String? get url => receipt.eReceiptUrl;
  String get text => receipt.toText();

  static Future<void> show(BuildContext context, {required Receipt receipt, required String title}) => Navigator.of(context).push(
    MaterialPageRoute(
      builder: (_) => ReceiptQrScreen(receipt: receipt, title: title),
    ),
  );

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final data = url ?? compactReceiptText(text);
    return Scaffold(
      appBar: ScreenTitle(l.receiptQr, subtitle: title),
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(20),
          child: Column(
            children: [
              const Spacer(),
              Container(
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(
                  color: Colors.white,
                  borderRadius: BorderRadius.circular(20),
                  border: Border.all(color: context.pal.border),
                ),
                child: QrImageView(
                  data: data,
                  version: QrVersions.auto,
                  errorCorrectionLevel: url != null ? QrErrorCorrectLevel.M : QrErrorCorrectLevel.L,
                  size: MediaQuery.sizeOf(context).width - 72,
                  // 13.10, владелец: QR в цветах темы (тёмная — светлые точки на тёмном)
                  // 2026-10-22 (мобилка): QR всегда тёмный на белом — светлый на тёмном многие сканеры не читают.
                  backgroundColor: Colors.white,
                  eyeStyle: const QrEyeStyle(eyeShape: QrEyeShape.square, color: T.navy),
                  dataModuleStyle: const QrDataModuleStyle(dataModuleShape: QrDataModuleShape.square, color: T.navy),
                ),
              ),
              const SizedBox(height: 18),
              Text(
                l.qrPointCamera,
                textAlign: TextAlign.center,
                style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 17),
              ),
              const SizedBox(height: 6),
              Text(
                url != null ? l.qrDownloadHint : l.qrTextHint,
                textAlign: TextAlign.center,
                style: TextStyle(color: context.pal.textSoft, fontSize: 13),
              ),
              const Spacer(),
              // 13.10: PDF-файл клиенту отсюда же — пока QR без ссылки, это единственный способ отдать PDF.
              OutlinedButton.icon(
                style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                onPressed: () => shareReceiptPdf(receipt),
                icon: const Icon(Icons.picture_as_pdf_outlined),
                label: Text(l.sendPdf),
              ),
              const SizedBox(height: 10),
              FilledButton(onPressed: () => Navigator.pop(context), child: Text(l.done)),
            ],
          ),
        ),
      ),
    );
  }
}
