import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:qr_flutter/qr_flutter.dart';
import 'package:share_plus/share_plus.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../receipt/receipt.dart';
import 'receipt_pdf.dart';

/// 2026-11-26 (владелец: «при скане QR на чеке должен открываться красивый чек, а не текстовый список»): ссылка на страницу чека —
/// `share_url` сервера (https://app.nurcrm.kg/r/{token}), как в кассе (ReceiptShareLink). Старые поля e_receipt_* (check.nurcrm.kg/….pdf) больше
/// не читаем. Берётся из ответа pos/checkout/ и pos/sales/{id}/ (в том числе из вложенного `data`); null — ссылки нет.
String? eReceiptUrl(Map<String, dynamic>? sale) {
  if (sale == null) return null;
  final direct = shareUrlOk(str(sale, 'share_url'));
  if (direct != null) return direct;
  final data = sale['data'];
  return data is Map ? shareUrlOk(str(data.cast<String, dynamic>(), 'share_url')) : null;
}

/// В ответе есть ключ share_url (даже null): сервер ссылку знает — null значит «отозвана», новую молча не создаём.
bool hasShareUrlKey(Map<String, dynamic>? sale) => sale != null && sale.containsKey('share_url');

/// Только полноценная https-ссылка без пробелов и не длиннее 300 знаков (короткий QR читается любой камерой и принтером).
String? shareUrlOk(String? raw) {
  final u = raw?.trim() ?? '';
  if (u.isEmpty || u.length > 300 || !u.toLowerCase().startsWith('https://') || RegExp(r'\s').hasMatch(u)) return null;
  return u;
}

/// Чек со ссылкой на красивую страницу: нет ссылки в самом чеке — просим у сервера (POST share-link, повтор безопасен).
/// Офлайн-чек, нет связи или сбой — чек как был (QR появится позже, ничего не падает).
Future<Receipt> receiptWithLink(BuildContext context, Receipt r) async {
  if (r.eReceiptUrl != null || r.offline || (r.saleId ?? '').isEmpty) return r;
  return ProviderScope.containerOf(context, listen: false).read(salesRepoProvider).withShareLink(r);
}

/// Текст для «Поделиться»: «Ваш чек №…» на языке интерфейса и ссылка на страницу чека.
String shareReceiptMessage(AppLocalizations l, Receipt r, String url) => '${l.shareReceiptText(r.numberText)}\n$url';

/// Экран «QR-код чека»: покупатель наводит камеру — открывается красивая страница чека (share_url).
/// Ссылки ещё нет — запрашиваем у сервера; чек без связи — подсказка (QR появится после отправки чека на сервер).
class ReceiptQrScreen extends StatefulWidget {
  final Receipt receipt;
  final String title;
  const ReceiptQrScreen({super.key, required this.receipt, required this.title});

  static Future<void> show(BuildContext context, {required Receipt receipt, required String title}) => Navigator.of(context).push(
    MaterialPageRoute(
      builder: (_) => ReceiptQrScreen(receipt: receipt, title: title),
    ),
  );

  @override
  State<ReceiptQrScreen> createState() => _ReceiptQrScreenState();
}

class _ReceiptQrScreenState extends State<ReceiptQrScreen> {
  late Receipt _r = widget.receipt;
  bool _loading = false;

  String? get url => _r.eReceiptUrl;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _resolve());
  }

  Future<void> _resolve() async {
    if (url != null || _r.offline || (_r.saleId ?? '').isEmpty) return;
    setState(() => _loading = true);
    try {
      final r = await receiptWithLink(context, _r);
      if (mounted) setState(() => _r = r);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final u = url;
    return Scaffold(
      appBar: ScreenTitle(l.receiptQr, subtitle: widget.title),
      body: SafeArea(
        // 2026-11-26: экран прокручивается — с тремя кнопками на невысоком экране и в альбомной ориентации ничего не уходит за край
        child: LayoutBuilder(
          builder: (context, box) => SingleChildScrollView(
            child: ConstrainedBox(
              constraints: BoxConstraints(minHeight: box.maxHeight),
              child: Padding(
                padding: const EdgeInsets.all(20),
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    const SizedBox(height: 12),
                    if (u != null) ...[
                      Container(
                        padding: const EdgeInsets.all(16),
                        decoration: BoxDecoration(
                          color: Colors.white,
                          borderRadius: BorderRadius.circular(20),
                          border: Border.all(color: context.pal.border),
                        ),
                        child: QrImageView(
                          key: const Key('receiptQrImage'),
                          data: u,
                          version: QrVersions.auto,
                          errorCorrectionLevel: QrErrorCorrectLevel.M,
                          size: (MediaQuery.sizeOf(context).width - 72).clamp(160.0, 260.0), // 2026-11-26: не больше 260 — кнопки видны без прокрутки
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
                        l.qrDownloadHint,
                        textAlign: TextAlign.center,
                        style: TextStyle(color: context.pal.textSoft, fontSize: 13),
                      ),
                    ] else if (_loading)
                      const CircularProgressIndicator()
                    else ...[
                      // 2026-11-26: старый QR с текстом чека убран — без ссылки только подсказка (офлайн-чек: QR после синхронизации)
                      Icon(_r.offline ? Icons.cloud_upload_outlined : Icons.link_off_rounded, size: 48, color: context.pal.textSoft),
                      const SizedBox(height: 14),
                      Text(
                        _r.offline ? l.qrOfflineHint : l.qrTextHint,
                        key: const Key('receiptQrNoLink'),
                        textAlign: TextAlign.center,
                        style: TextStyle(color: context.pal.textSoft, fontSize: 14),
                      ),
                      if (!_r.offline && (_r.saleId ?? '').isNotEmpty) ...[
                        const SizedBox(height: 12),
                        OutlinedButton(onPressed: _resolve, child: Text(l.retry)),
                      ],
                    ],
                    const SizedBox(height: 12),
                    if (u != null) ...[
                      FilledButton.icon(
                        key: const Key('receiptOpenLink'),
                        style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                        onPressed: () => launchUrl(Uri.parse(u), mode: LaunchMode.externalApplication),
                        icon: const Icon(Icons.open_in_new_rounded),
                        label: Text(l.openReceiptLink),
                      ),
                      const SizedBox(height: 10),
                      OutlinedButton.icon(
                        key: const Key('receiptShareLink'),
                        style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                        onPressed: () => SharePlus.instance.share(ShareParams(text: shareReceiptMessage(l, _r, u), subject: _r.fileName)),
                        icon: const Icon(Icons.ios_share_rounded),
                        label: Text(l.shareReceiptLink),
                      ),
                      const SizedBox(height: 10),
                    ],
                    // 13.10: PDF-файл клиенту отсюда же.
                    OutlinedButton.icon(
                      style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                      onPressed: () async => shareReceiptPdf(await receiptWithLink(context, _r)),
                      icon: const Icon(Icons.picture_as_pdf_outlined),
                      label: Text(l.sendPdf),
                    ),
                    const SizedBox(height: 10),
                    OutlinedButton(onPressed: () => Navigator.pop(context), child: Text(l.done)),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
