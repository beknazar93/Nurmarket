import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/util/beep.dart';
import '../../l10n/app_localizations.dart';
import '../scan/barcode_lookup.dart' show normalizeCode;

/// 2026-11-26 (владелец: «на складе поиск товара — добавь поиск сканером»): камера читает ОДИН код (штрихкод, QR, этикетка весов)
/// и сразу возвращает его экрану «Склад» — тот ищет товар так же, как продажа (CodeLookup: штрихкод упаковки, размера,
/// весовая этикетка, сервер). Своего разбора кода здесь нет. Закрыть — крестик или «Назад» (вернётся null).
class StockScanPage extends StatefulWidget {
  const StockScanPage({super.key});

  /// Открыть сканер и дождаться кода; null — закрыли без скана.
  static Future<String?> open(BuildContext context) => Navigator.of(context).push<String>(MaterialPageRoute(builder: (_) => const StockScanPage()));

  @override
  State<StockScanPage> createState() => _StockScanPageState();
}

class _StockScanPageState extends State<StockScanPage> {
  final _camera = MobileScannerController(
    formats: const [BarcodeFormat.ean13, BarcodeFormat.ean8, BarcodeFormat.code128, BarcodeFormat.upcA, BarcodeFormat.upcE, BarcodeFormat.itf14, BarcodeFormat.qrCode],
    detectionSpeed: DetectionSpeed.normal,
    returnImage: false,
    autoZoom: true,
  );
  bool _done = false;
  bool _torch = false;

  @override
  void dispose() {
    _camera.dispose();
    super.dispose();
  }

  void _onDetect(BarcodeCapture capture) {
    if (_done) return;
    for (final b in capture.barcodes) {
      final code = normalizeCode(b.rawValue);
      if (code.isEmpty) continue;
      _done = true;
      HapticFeedback.mediumImpact();
      Beep.ok();
      Navigator.of(context).pop(code);
      return;
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = AppLocalizations.of(context)!;
    return Scaffold(
      backgroundColor: Colors.black,
      appBar: AppBar(
        backgroundColor: Colors.black,
        foregroundColor: Colors.white,
        title: Text(l.stockScanBtn),
        actions: [
          IconButton(
            key: const Key('stockScanTorch'),
            iconSize: 28,
            constraints: const BoxConstraints(minWidth: 48, minHeight: 48),
            tooltip: l.torchToggle,
            icon: Icon(_torch ? Icons.flash_on_rounded : Icons.flash_off_rounded),
            onPressed: () async {
              try {
                await _camera.toggleTorch();
                if (mounted) setState(() => _torch = !_torch);
              } catch (_) {}
            },
          ),
        ],
      ),
      body: Stack(
        children: [
          Positioned.fill(
            child: MobileScanner(
              controller: _camera,
              onDetect: _onDetect,
              errorBuilder: (c, e) => Center(
                child: Padding(
                  padding: const EdgeInsets.all(24),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(l.cameraUnavailable, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white70)),
                      const SizedBox(height: 12),
                      FilledButton.icon(
                        style: FilledButton.styleFrom(minimumSize: const Size(48, 48)),
                        onPressed: () async {
                          try {
                            await _camera.stop();
                            await _camera.start();
                          } catch (_) {}
                        },
                        icon: const Icon(Icons.refresh_rounded),
                        label: Text(l.retry),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
          Align(
            alignment: Alignment.bottomCenter,
            child: SafeArea(
              child: Container(
                margin: const EdgeInsets.all(16),
                padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                decoration: BoxDecoration(color: Colors.black54, borderRadius: BorderRadius.circular(12)),
                child: Text(l.stockScanHint, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w600)),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
