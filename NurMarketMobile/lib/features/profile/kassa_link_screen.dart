import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/link/kassa_link.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../scan/cam_label.dart';

// 2026-10-27: экран «Связь с кассой» (М-П1): сопряжение с кассой по QR, состояние связи, смена на кассе, «Проверить связь»,
// «Отключить». Сама связь — lib/core/link/kassa_link.dart (протокол v1); экран только показывает её и зовёт.

/// 2026-10-27 (аудит мобилки, Н-3): подпись для TalkBack из частей — без «….» на стыке («Проверка связи…. Касса» читалось с паузой).
String a11yJoin(Iterable<String> parts) => parts.where((p) => p.trim().isNotEmpty).map((p) => p.trim().replaceAll(RegExp(r'[.…]+$'), '')).join('. ');

/// Текст, значок и цвет состояния связи — общие для этого экрана и режима «Сканер для кассы».
extension KassaStatusUi on KassaLinkStatus {
  String text(AppLocalizations l) => switch (this) {
    KassaLinkStatus.unpaired => l.klStUnpaired,
    KassaLinkStatus.unknown => l.klStUnknown,
    KassaLinkStatus.online => l.klStOnline,
    KassaLinkStatus.notFound => l.klStNotFound,
    KassaLinkStatus.revoked => l.klStRevoked,
    KassaLinkStatus.badTime => l.klStBadTime,
    KassaLinkStatus.disabled => l.klStDisabled,
    KassaLinkStatus.rePair => l.klStRePair,
  };

  /// 2026-10-27 (аудит мобилки, С-3/С-4): что делать при отказе кассы или «касса не найдена» (null — подсказка не нужна).
  String? help(AppLocalizations l) => switch (this) {
    KassaLinkStatus.notFound => l.klHelpNotFound,
    KassaLinkStatus.disabled => l.klHelpDisabled,
    KassaLinkStatus.revoked || KassaLinkStatus.rePair => l.klHelpRePair,
    KassaLinkStatus.badTime => l.klHelpBadTime,
    _ => null,
  };

  IconData get icon => switch (this) {
    KassaLinkStatus.online => Icons.wifi_rounded,
    KassaLinkStatus.unknown => Icons.wifi_find_rounded,
    KassaLinkStatus.badTime => Icons.schedule_rounded,
    _ => Icons.wifi_off_rounded,
  };

  /// Цвет: зелёный — на связи, жёлтый — ещё не проверено, красный — нет связи или отказ.
  Color color(Palette pal) => switch (this) {
    KassaLinkStatus.online => pal.success,
    KassaLinkStatus.unknown || KassaLinkStatus.unpaired => pal.warn,
    _ => pal.danger,
  };

  Color softColor(Palette pal) => switch (this) {
    KassaLinkStatus.online => pal.successSoft,
    KassaLinkStatus.unknown || KassaLinkStatus.unpaired => pal.warnSoft,
    _ => pal.dangerSoft,
  };
}

class KassaLinkScreen extends ConsumerStatefulWidget {
  /// [link] — только для тестов (по умолчанию общая связь приложения).
  final KassaLink? link;
  const KassaLinkScreen({super.key, this.link});
  @override
  ConsumerState<KassaLinkScreen> createState() => _KassaLinkScreenState();
}

class _KassaLinkScreenState extends ConsumerState<KassaLinkScreen> {
  late final link = widget.link ?? KassaLink.instance;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    link.deviceName = ref.read(sessionProvider)?.userName ?? '';
    unawaited(_open());
  }

  Future<void> _open() async {
    await link.ensureLoaded();
    // 2026-10-27: при входе на экран связь проверяется сама (без поиска по подсети — он по кнопке)
    if (mounted && link.isPaired) await link.check(discover: false);
  }

  Future<void> _scanQr() async {
    final raw = await Navigator.of(context).push<String>(MaterialPageRoute(builder: (_) => const KassaQrScanPage()));
    if (raw != null && mounted) await _pair(raw);
  }

  Future<void> _manual() async {
    final l = context.l;
    final c = TextEditingController();
    final text = await showDialog<String>(
      context: context,
      builder: (d) => AlertDialog(
        title: Text(l.klManual),
        content: TextField(
          controller: c,
          autofocus: true,
          minLines: 1,
          maxLines: 4,
          keyboardType: TextInputType.url,
          decoration: InputDecoration(hintText: l.klManualHint),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(d), child: Text(l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(d, c.text), child: Text(l.klConnect)),
        ],
      ),
    );
    if (text != null && text.trim().isNotEmpty && mounted) await _pair(text);
  }

  Future<void> _pair(String raw) async {
    final l = context.l;
    setState(() => _busy = true);
    try {
      final st = await link.pairFromQr(raw);
      if (!mounted) return;
      if (st == KassaLinkStatus.online) {
        toast(context, link.kassaName.isEmpty ? l.klConnectedPlain : l.klConnected(link.kassaName));
      } else {
        toast(context, st.text(l), error: true);
      }
    } on KassaQrException catch (e) {
      if (mounted) toast(context, e.reason == 'version' ? l.klQrVersion : l.klBadQr, error: true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _disconnect() async {
    final l = context.l;
    final name = link.kassaName;
    final ok = await showDialog<bool>(
      context: context,
      builder: (d) => AlertDialog(
        content: Text(l.klDisconnectAsk(name)),
        actions: [
          TextButton(onPressed: () => Navigator.pop(d, false), child: Text(l.cancel)),
          TextButton(
            onPressed: () => Navigator.pop(d, true),
            child: Text(l.klDisconnect, style: TextStyle(color: d.pal.danger)),
          ),
        ],
      ),
    );
    if (ok != true) return;
    await link.disconnect();
    if (mounted) toast(context, l.klDisconnected);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    const btn = Size.fromHeight(T.ctaHeight);
    return Scaffold(
      appBar: ScreenTitle(l.klTitle),
      body: ListenableBuilder(
        listenable: link,
        builder: (context, _) {
          final st = link.status;
          final paired = link.isPaired;
          final busy = _busy || link.checking;
          final pairBlock = Panel(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(l.klScanQrHint, style: TextStyle(fontSize: 13.5, color: pal.textSoft)),
                const SizedBox(height: 12),
                if (paired)
                  OutlinedButton.icon(
                    style: OutlinedButton.styleFrom(minimumSize: btn),
                    onPressed: _busy ? null : _scanQr,
                    icon: const Icon(Icons.qr_code_scanner_rounded),
                    label: Text(l.klScanQr),
                  )
                else
                  FilledButton.icon(
                    style: FilledButton.styleFrom(minimumSize: btn),
                    onPressed: _busy ? null : _scanQr,
                    icon: const Icon(Icons.qr_code_scanner_rounded),
                    label: Text(l.klScanQr),
                  ),
                const SizedBox(height: 4),
                // 2026-10-27: вставить текст QR вручную (проверка на эмуляторе, где камерой QR не показать)
                TextButton(
                  style: TextButton.styleFrom(minimumSize: const Size.fromHeight(48)),
                  onPressed: _busy ? null : _manual,
                  child: Text(l.klManual),
                ),
              ],
            ),
          );
          return ListView(
            padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
            children: [
              Semantics(
                container: true,
                liveRegion: true,
                label: a11yJoin([busy ? (link.discovering ? l.klSearching : l.klChecking) : st.text(l), if (paired && link.kassaName.isNotEmpty) l.klKassaName(link.kassaName)]),
                child: ExcludeSemantics(
                  child: Panel(
                    child: Row(
                      children: [
                        Container(
                          width: 56,
                          height: 56,
                          decoration: BoxDecoration(
                            shape: BoxShape.circle,
                            color: st.softColor(pal),
                            border: Border.all(color: st.color(pal), width: 3),
                          ),
                          child: busy
                              ? const Padding(padding: EdgeInsets.all(15), child: CircularProgressIndicator(strokeWidth: 3))
                              : Icon(st.icon, color: st.color(pal), size: 28),
                        ),
                        const SizedBox(width: 14),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                busy ? (link.discovering ? l.klSearching : l.klChecking) : st.text(l),
                                style: TextStyle(fontWeight: FontWeight.w800, fontSize: 16, color: busy ? null : st.color(pal)),
                              ),
                              if (paired && link.kassaName.isNotEmpty) Text(l.klKassaName(link.kassaName), style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14)),
                              if (paired && link.shiftOpen != null) Text(link.shiftOpen! ? l.klShiftOpen : l.klShiftClosed, style: TextStyle(fontSize: 12.5, color: pal.textSoft)),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
              // 2026-10-27 (аудит мобилки, С-3): что проверить, когда касса не найдена или отказывает
              if (paired && !busy && st.help(l) != null) ...[
                const SizedBox(height: 12),
                Panel(
                  key: const Key('klHelp'),
                  child: Semantics(liveRegion: true, child: Text(st.help(l)!, style: const TextStyle(fontSize: 14, height: 1.35))),
                ),
              ],
              const SizedBox(height: 12),
              if (!paired) pairBlock,
              if (paired) ...[
                FilledButton.icon(
                  style: FilledButton.styleFrom(minimumSize: btn),
                  onPressed: busy ? null : () => link.check(),
                  icon: const Icon(Icons.sync_rounded),
                  label: Text(l.klCheckNow),
                ),
                const SizedBox(height: 10),
                OutlinedButton(
                  style: OutlinedButton.styleFrom(minimumSize: btn, foregroundColor: pal.danger),
                  onPressed: _busy ? null : _disconnect,
                  child: Text(l.klDisconnect),
                ),
                const SizedBox(height: 12),
                pairBlock,
              ],
            ],
          );
        },
      ),
    );
  }
}

/// Сканер QR кассы: закрывается сам, как только прочитал QR «nurmarket-link:…» (возвращает его текст).
class KassaQrScanPage extends StatefulWidget {
  const KassaQrScanPage({super.key});
  @override
  State<KassaQrScanPage> createState() => _KassaQrScanPageState();
}

class _KassaQrScanPageState extends State<KassaQrScanPage> {
  final _camera = MobileScannerController(formats: const [BarcodeFormat.qrCode], returnImage: false);
  bool _done = false;
  bool _wrong = false;
  Timer? _wrongTimer;

  @override
  void dispose() {
    _wrongTimer?.cancel();
    _camera.dispose();
    super.dispose();
  }

  void _onDetect(BarcodeCapture capture) {
    if (_done) return;
    for (final b in capture.barcodes) {
      final raw = b.rawValue;
      if (raw == null) continue;
      if (raw.trim().toLowerCase().startsWith('nurmarket-link:')) {
        _done = true;
        Navigator.of(context).pop(raw);
        return;
      }
    }
    // чужой QR (не кассы) — подсказка на 2 секунды, камера продолжает искать
    if (!_wrong && capture.barcodes.any((b) => b.rawValue != null)) {
      setState(() => _wrong = true);
      _wrongTimer?.cancel();
      _wrongTimer = Timer(const Duration(seconds: 2), () {
        if (mounted) setState(() => _wrong = false);
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      backgroundColor: const Color(0xFF071824),
      body: Stack(
        children: [
          Positioned.fill(
            child: MobileScanner(
              controller: _camera,
              onDetect: _onDetect,
              errorBuilder: (c, e) => Center(
                child: Padding(
                  padding: const EdgeInsets.all(24),
                  child: Text(
                    l.cameraUnavailable,
                    textAlign: TextAlign.center,
                    style: const TextStyle(color: Colors.white70),
                  ),
                ),
              ),
            ),
          ),
          Align(
            alignment: Alignment.topCenter,
            child: SafeArea(
              child: Padding(
                padding: const EdgeInsets.all(14),
                child: Row(
                  children: [
                    Semantics(
                      button: true,
                      label: l.a11yBack,
                      excludeSemantics: true,
                      child: Material(
                        color: camShade,
                        shape: const CircleBorder(),
                        child: InkWell(
                          customBorder: const CircleBorder(),
                          onTap: () => Navigator.of(context).maybePop(),
                          child: const SizedBox(width: 48, height: 48, child: Icon(Icons.close_rounded, color: Colors.white)),
                        ),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Expanded(child: CamLabel(l.klScanQrTitle, size: 14, weight: FontWeight.w700, maxLines: 3)),
                  ],
                ),
              ),
            ),
          ),
          if (_wrong)
            Align(
              alignment: const Alignment(0, .5),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 24),
                child: Semantics(liveRegion: true, child: CamLabel(l.klNotKassaQr, size: 15, align: TextAlign.center, maxLines: 3)),
              ),
            ),
        ],
      ),
    );
  }
}
