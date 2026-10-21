import 'package:flutter/material.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../payment/receipt_pdf.dart';
import '../payment/receipt_qr.dart';
import '../profile/printer_settings.dart';
import 'receipt.dart';

/// 13.10: чек на экране — как бумажная лента: белая, с зубчатым нижним краем, пунктирными разделителями, крупным итогом
/// и QR. Цвета «бумажные» и в тёмной теме — так чек выглядит одинаково на экране, в PDF и на принтере.
class ReceiptView extends StatelessWidget {
  final Receipt receipt;
  const ReceiptView({super.key, required this.receipt});

  // 13.10, владелец: «и цвет чека тоже должен меняться», «и QR тоже» — чек и QR в цветах темы
  // (в тёмной — тёмная лента, светлый текст и светлые точки QR).
  static const _red = Color(0xFFB42318);

  @override
  Widget build(BuildContext context) {
    final r = receipt;
    final f = r.fiscal;
    final qrData = f?.checkUrl ?? r.eReceiptUrl;
    final p = context.pal;
    final red = p.dark ? const Color(0xFFF97066) : _red;
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 400),
        child: DecoratedBox(
          decoration: const BoxDecoration(
            boxShadow: [BoxShadow(color: Color(0x22000000), blurRadius: 18, offset: Offset(0, 6))],
          ),
          child: ClipPath(
            clipper: _ZigzagClipper(),
            child: Container(
              color: p.panel,
              padding: const EdgeInsets.fromLTRB(20, 22, 20, 30),
              child: DefaultTextStyle.merge(
                style: TextStyle(color: p.text, fontSize: 13, height: 1.35),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Text(
                      r.shop,
                      textAlign: TextAlign.center,
                      style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
                    ),
                    if (r.inn.isNotEmpty)
                      Text(
                        'ИНН ${r.inn}',
                        textAlign: TextAlign.center,
                        style: TextStyle(color: p.textSoft, fontSize: 12),
                      ),
                    if (r.address.isNotEmpty)
                      Text(
                        r.address,
                        textAlign: TextAlign.center,
                        style: TextStyle(color: p.textSoft, fontSize: 12),
                      ),
                    const SizedBox(height: 12),
                    Center(
                      child: Container(
                        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 5),
                        decoration: BoxDecoration(color: p.window, borderRadius: BorderRadius.circular(999)),
                        child: Text(r.title, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w800, letterSpacing: .6)),
                      ),
                    ),
                    const SizedBox(height: 12),
                    _pair(p, 'Чек №', r.numberText, bold: true),
                    if (r.shift.isNotEmpty) _pair(p, 'Смена №', r.shift),
                    _pair(p, 'Дата и время', r.dateText),
                    if (r.cashier.isNotEmpty) _pair(p, 'Кассир', r.cashier),
                    if (r.cashbox.isNotEmpty) _pair(p, 'Касса', r.cashbox),
                    if (r.client.isNotEmpty) _pair(p, 'Покупатель', r.client),
                    const _Dashed(),
                    for (var i = 0; i < r.lines.length; i++) _line(p, i + 1, r.lines[i]),
                    const _Dashed(),
                    if (r.discount > 0) ...[_pair(p, 'Сумма без скидки', money(r.subtotal)), _pair(p, 'Скидка', '−${money(r.discount)}', color: red)],
                    Padding(
                      padding: const EdgeInsets.symmetric(vertical: 6),
                      child: Row(
                        children: [
                          const Text('ИТОГО', style: TextStyle(fontSize: 20, fontWeight: FontWeight.w800)),
                          const Spacer(),
                          Text('${money(r.total)} сом', style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w800)),
                        ],
                      ),
                    ),
                    _pair(p, r.paymentText, money(r.received ?? r.total)),
                    if (r.change > 0) _pair(p, 'Сдача', money(r.change), bold: true),
                    if (f != null) ...[
                      const _Dashed(),
                      for (final t in f.taxes) _pair(p, t.name, money(t.amount)),
                      if (f.regNumber.isNotEmpty) _pair(p, 'РН ККМ', f.regNumber),
                      if (f.fiscalModule.isNotEmpty) _pair(p, 'ФМ', f.fiscalModule),
                      _pair(p, 'ФД', f.fd),
                      if (f.fpd.isNotEmpty) _pair(p, 'ФПД', f.fpd),
                    ],
                    if (r.offline)
                      Padding(
                        padding: const EdgeInsets.only(top: 8),
                        child: Text(
                          'Чек сохранён без связи — уйдёт на сервер автоматически',
                          textAlign: TextAlign.center,
                          style: TextStyle(color: Colors.orange.shade800, fontSize: 11.5),
                        ),
                      ),
                    if (qrData != null) ...[
                      const SizedBox(height: 14),
                      Center(
                        child: QrImageView(
                          data: qrData,
                          size: 132,
                          // 13.10, владелец: «и QR тоже цвет поменять» — в цветах чека (тёмная тема — светлые точки на тёмном)
                          backgroundColor: p.panel,
                          eyeStyle: QrEyeStyle(eyeShape: QrEyeShape.square, color: p.text),
                          dataModuleStyle: QrDataModuleStyle(dataModuleShape: QrDataModuleShape.square, color: p.text),
                        ),
                      ),
                      Text(
                        f?.checkUrl != null ? 'Проверка чека' : 'Электронный чек (PDF)',
                        textAlign: TextAlign.center,
                        style: TextStyle(color: p.textSoft, fontSize: 11),
                      ),
                    ],
                    const SizedBox(height: 14),
                    const Text(
                      'Спасибо за покупку!',
                      textAlign: TextAlign.center,
                      style: TextStyle(fontWeight: FontWeight.w700),
                    ),
                    Text(
                      'NurMarket',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: p.textSoft, fontSize: 10.5),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _pair(Palette p, String label, String value, {bool bold = false, Color? color}) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 1.5),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: TextStyle(color: p.textSoft)),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            value,
            textAlign: TextAlign.right,
            style: TextStyle(fontWeight: bold ? FontWeight.w800 : FontWeight.w600, color: color ?? p.text),
          ),
        ),
      ],
    ),
  );

  Widget _line(Palette p, int n, ReceiptLine l) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text('$n. ${l.name}', style: const TextStyle(fontWeight: FontWeight.w600)),
        Row(
          children: [
            Text('${qtyText(l.qty)}${l.unit.isEmpty ? '' : ' ${l.unit}'} × ${money(l.price)}', style: TextStyle(color: p.textSoft)),
            const Spacer(),
            Text(money(l.total), style: const TextStyle(fontWeight: FontWeight.w800)),
          ],
        ),
        if (l.discount > 0)
          Align(
            alignment: Alignment.centerRight,
            child: Text('скидка −${money(l.discount)}', style: TextStyle(color: p.dark ? const Color(0xFFF97066) : _red, fontSize: 12)),
          ),
      ],
    ),
  );
}

class _Dashed extends StatelessWidget {
  const _Dashed();
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 8),
    child: CustomPaint(size: const Size.fromHeight(1), painter: _DashPainter()),
  );
}

class _DashPainter extends CustomPainter {
  @override
  void paint(Canvas canvas, Size size) {
    final p = Paint()
      ..color = const Color(0xFFB0B7C3)
      ..strokeWidth = 1;
    for (double x = 0; x < size.width; x += 7) {
      canvas.drawLine(Offset(x, 0), Offset(x + 3.5, 0), p);
    }
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

/// Нижний край ленты — зубцами, как у отрывного чека.
class _ZigzagClipper extends CustomClipper<Path> {
  @override
  Path getClip(Size size) {
    const tooth = 10.0;
    final path = Path()
      ..moveTo(0, 0)
      ..lineTo(size.width, 0)
      ..lineTo(size.width, size.height - tooth);
    var x = size.width;
    var down = true;
    while (x > 0) {
      x -= tooth;
      path.lineTo(x.clamp(0, size.width), down ? size.height : size.height - tooth);
      down = !down;
    }
    path.close();
    return path;
  }

  @override
  bool shouldReclip(covariant CustomClipper<Path> oldClipper) => false;
}

/// Чек целиком на отдельном экране: лента, «QR-код» и «Отправить PDF».
class ReceiptScreen extends StatelessWidget {
  final Receipt receipt;
  const ReceiptScreen({super.key, required this.receipt});

  static Future<void> show(BuildContext context, Receipt receipt) =>
      Navigator.of(context).push(MaterialPageRoute(builder: (_) => ReceiptScreen(receipt: receipt)));

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      backgroundColor: context.pal.window,
      appBar: ScreenTitle(receipt.fileName, subtitle: receipt.dateText),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
        children: [ReceiptView(receipt: receipt)],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
          child: Row(
            children: [
              // 2026-10-20: печать на принтер чеков.
              IconButton.outlined(
                style: IconButton.styleFrom(minimumSize: const Size(50, 50), backgroundColor: context.pal.panel),
                tooltip: l.prnPrint,
                onPressed: () => printReceiptUi(context, receipt),
                icon: const Icon(Icons.print_outlined),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: OutlinedButton.icon(
                  style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(50), backgroundColor: context.pal.panel),
                  onPressed: () => ReceiptQrScreen.show(context, receipt: receipt, title: receipt.fileName),
                  icon: const Icon(Icons.qr_code_2_rounded),
                  label: Text(l.showQr),
                ),
              ),
              const SizedBox(width: 10),
              Expanded(
                child: FilledButton.icon(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(50)),
                  onPressed: () => shareReceiptPdf(receipt),
                  icon: const Icon(Icons.picture_as_pdf_outlined),
                  label: Text(l.sendPdf),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
