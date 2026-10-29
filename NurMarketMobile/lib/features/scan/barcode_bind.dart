import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import 'barcode_lookup.dart';
import 'cam_label.dart';
import 'code_lookup.dart';
import 'live_shot.dart';
import '../../core/util/beep.dart';
import 'scan_animations.dart';

/// Чем закончился шаг «Штрихкод»: [code] null — пропустили (или «Оставить» у другого товара); [takenFrom] — «Заменить»:
/// штрихкод сначала снять с этого товара.
typedef BindResult = ({String? code, Product? takenFrom});

/// Кому принадлежит код: товар (или null) и «сервер не ответил» (тогда привязываем без проверки).
typedef BindLookup = Future<({Product? product, bool unavailable})> Function(String code);

enum BindPhase { scan, checking, confirm }

/// 2026-10-22, владелец: «умная камера после обучения выходит сканер, но он тупо ничего не говорит после фоток — выведи
/// надпись „А теперь отсканируйте штрихкод, если есть“, если нет — „Пропустить“». Причина: после фото обучения
/// открывался общий сканер приёмки (ScanScreen receive + returnCodes) — подсказка «Отсканировано: 0», после скана
/// только всплывашка «+1 · код», сканер не закрывался и ждал «Готово (1)», а без штрихкода выйти можно было только
/// крестиком; что будет с кодом — не говорилось, код чужого товара привязывался молча. Теперь отдельный шаг привязки:
/// шаги «1 Фото ✓ → 2 Штрихкод → 3 Готово», крупная подсказка, «Пропустить» и «Ввести вручную»; после скана — код и
/// что с ним будет («… будет привязан к «Товар»»), код другого товара — вопрос «Заменить / Оставить / Отмена».
class BarcodeBindPage extends ConsumerStatefulWidget {
  /// Название товара («» — новый товар, название ещё не введено).
  final String productName;

  /// Товар уже есть в NurCRM — тогда можно «Заменить» (забрать код у другого товара); null — товар ещё не создан.
  final String? productId;

  /// Тесты: поиск владельца кода и картинка вместо камеры.
  final BindLookup? lookup;
  final Widget? preview;

  const BarcodeBindPage({super.key, this.productName = '', this.productId, this.lookup, this.preview});

  static Future<BindResult?> open(BuildContext context, {String productName = '', String? productId}) =>
      Navigator.of(context).push<BindResult>(MaterialPageRoute(builder: (_) => BarcodeBindPage(productName: productName, productId: productId)));

  @override
  ConsumerState<BarcodeBindPage> createState() => BarcodeBindPageState();
}

class BarcodeBindPageState extends ConsumerState<BarcodeBindPage> {
  MobileScannerController? _camera;
  final _frame = GlobalKey<ScanFrameState>();
  BindPhase _phase = BindPhase.scan;
  String? _code;
  Product? _takenFrom;
  String _message = '';

  /// «Отмена» в вопросе о чужом коде: тот же код, ещё видный в кадре, 2 с не спрашиваем снова.
  String? _ignore;
  DateTime _ignoreUntil = DateTime(2000);

  BindPhase get phase => _phase;

  @override
  void initState() {
    super.initState();
    if (widget.preview == null) {
      _camera = MobileScannerController(
        // товарные штрихкоды (QR — не штрихкод товара)
        formats: const [BarcodeFormat.ean13, BarcodeFormat.ean8, BarcodeFormat.code128, BarcodeFormat.upcA, BarcodeFormat.upcE, BarcodeFormat.itf14],
        detectionSpeed: DetectionSpeed.normal,
        autoZoom: true,
      );
    }
  }

  @override
  void dispose() {
    _camera?.dispose();
    super.dispose();
  }

  Future<({Product? product, bool unavailable})> _lookup(String code) async {
    final custom = widget.lookup;
    if (custom != null) return custom(code);
    final r = await CodeLookup.find(ref.read(databaseProvider), ref.read(apiProvider), code, allowWeightLabel: false);
    return (product: r.product, unavailable: r.unavailable);
  }

  void _onDetect(BarcodeCapture capture) {
    for (final b in capture.barcodes) {
      final code = normalizeCode(b.rawValue);
      if (code.isNotEmpty) {
        onCode(code);
        return;
      }
    }
  }

  /// Код со сканера или введённый вручную.
  Future<void> onCode(String raw) async {
    final code = normalizeCode(raw);
    if (code.isEmpty || _phase != BindPhase.scan) return;
    if (code == _ignore && DateTime.now().isBefore(_ignoreUntil)) return;
    final l = context.l;
    HapticFeedback.mediumImpact();
    Beep.ok(); // 2026-10-22 (мобилка, админ): бип через Android
    _frame.currentState?.success();
    setState(() {
      _phase = BindPhase.checking;
      _code = code;
      _takenFrom = null;
    });
    final hit = await _lookup(code);
    if (!mounted) return;
    final other = hit.product;
    final name = widget.productName.trim();
    if (other == null || (widget.productId != null && other.id == widget.productId)) {
      setState(() {
        _phase = BindPhase.confirm;
        _message = other != null
            ? l.bindAlreadyOwn(code, other.name)
            : name.isEmpty
            ? l.bindWillLinkNew(code)
            : l.bindWillLink(code, name);
      });
      return;
    }
    // код уже у другого товара — предупредить вибрацией и звуком и спросить
    HapticFeedback.heavyImpact();
    Beep.error();
    _frame.currentState?.fail();
    // «Заменить» — только у существующего товара и только если это основной штрихкод другого товара (доп. штрихкод
    // упаковки так не снять)
    final canReplace = widget.productId != null && barcodeVariants(code).contains(normalizeCode(other.barcode));
    final choice = await showDialog<String>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(c.l.bindTakenTitle),
        content: Text(canReplace ? c.l.bindTakenAsk(code, other.name) : c.l.bindTakenAskNew(code, other.name)),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c), child: Text(c.l.cancel)),
          TextButton(onPressed: () => Navigator.pop(c, 'keep'), child: Text(c.l.bindKeep)),
          if (canReplace) FilledButton(onPressed: () => Navigator.pop(c, 'replace'), child: Text(c.l.bindReplace)),
        ],
      ),
    );
    if (!mounted) return;
    switch (choice) {
      case 'keep':
        Navigator.of(context).pop<BindResult>((code: null, takenFrom: null));
      case 'replace':
        setState(() {
          _takenFrom = other;
          _phase = BindPhase.confirm;
          _message = l.bindWillLink(code, name.isEmpty ? '…' : name);
        });
      default:
        _rescan();
    }
  }

  void _rescan() {
    _ignore = _code;
    _ignoreUntil = DateTime.now().add(const Duration(seconds: 2));
    setState(() {
      _phase = BindPhase.scan;
      _code = null;
      _takenFrom = null;
      _message = '';
    });
  }

  void _skip() => Navigator.of(context).pop<BindResult>((code: null, takenFrom: null));

  void _done() => Navigator.of(context).pop<BindResult>((code: _code, takenFrom: _takenFrom));

  Future<void> _manual() async {
    final l = context.l;
    final ctrl = TextEditingController();
    final text = await showDialog<String>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(c.l.bindManual),
        content: TextField(
          controller: ctrl,
          autofocus: true,
          keyboardType: TextInputType.number,
          inputFormatters: barcodeFormatters, // 2026-10-29: только цифры
          style: const TextStyle(fontSize: 22, fontWeight: FontWeight.w700, letterSpacing: 1.5),
          decoration: InputDecoration(labelText: l.barcodeLabel, hintText: l.bindManualHint),
          onSubmitted: (v) => Navigator.pop(c, v),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c), child: Text(c.l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(c, ctrl.text), child: Text(c.l.done)),
        ],
      ),
    );
    // ctrl не освобождаем сразу: поле ещё живо, пока окно уезжает (dispose здесь ронял анимацию закрытия)
    if (text != null && text.trim().isNotEmpty && mounted) await onCode(text);
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final camera = _camera;
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _skip();
      },
      child: Scaffold(
        backgroundColor: const Color(0xFF071824),
        body: Stack(
          children: [
            Positioned.fill(
              child: widget.preview ??
                  MobileScanner(
                    controller: camera!,
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
                              onPressed: () async {
                                try {
                                  await camera.stop();
                                  await camera.start();
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
            Center(child: ScanFrame(key: _frame)),
            Align(
              alignment: Alignment.topCenter,
              child: SafeArea(
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Row(
                        children: [
                          IconButton.filledTonal(onPressed: _skip, icon: const Icon(Icons.close_rounded)),
                          const SizedBox(width: 8),
                          Expanded(
                            child: ScanSteps(labels: [l.stepPhoto, l.stepBarcode, l.done], current: _phase == BindPhase.confirm ? 2 : 1),
                          ),
                          const SizedBox(width: 48),
                        ],
                      ),
                      if (_phase != BindPhase.confirm) ...[
                        const SizedBox(height: 12),
                        Container(
                          padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
                          decoration: BoxDecoration(color: const Color(0xCC0B1F33), borderRadius: BorderRadius.circular(18)),
                          child: Column(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              Text(
                                l.bindTitle,
                                textAlign: TextAlign.center,
                                style: const TextStyle(color: Colors.white, fontSize: 21, fontWeight: FontWeight.w800, height: 1.25),
                              ),
                              const SizedBox(height: 6),
                              Text(l.bindSub, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white70, fontSize: 15)),
                            ],
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
              ),
            ),
            Align(alignment: Alignment.bottomCenter, child: _bottom(context)),
          ],
        ),
      ),
    );
  }

  Widget _bottom(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final pad = EdgeInsets.fromLTRB(16, 0, 16, 16 + MediaQuery.paddingOf(context).bottom);
    if (_phase == BindPhase.scan) {
      return Padding(
        padding: pad,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            FilledButton.icon(
              style: FilledButton.styleFrom(
                backgroundColor: T.accent,
                foregroundColor: T.navy,
                minimumSize: const Size.fromHeight(60),
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(30)),
                textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
              ),
              onPressed: _skip,
              icon: const Icon(Icons.skip_next_rounded, size: 26),
              label: Text(l.bindSkip),
            ),
            const SizedBox(height: 10),
            OutlinedButton.icon(
              style: OutlinedButton.styleFrom(
                foregroundColor: Colors.white,
                // 2026-10-22 (подпись «Умная камера»): тёмная подложка — кнопка видна на светлом кадре
                backgroundColor: camShade,
                side: const BorderSide(color: Colors.white70, width: 1.5),
                minimumSize: const Size.fromHeight(52),
                shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(26)),
                textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700),
              ),
              onPressed: _manual,
              icon: const Icon(Icons.keyboard_alt_outlined),
              label: Text(l.bindManual),
            ),
          ],
        ),
      );
    }
    return Container(
      decoration: BoxDecoration(color: pal.panel, borderRadius: const BorderRadius.vertical(top: Radius.circular(24))),
      padding: EdgeInsets.fromLTRB(16, 18, 16, 16 + MediaQuery.paddingOf(context).bottom),
      child: _phase == BindPhase.checking
          ? Row(
              children: [
                const SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 3)),
                const SizedBox(width: 14),
                Expanded(child: Text(l.bindChecking, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700))),
              ],
            )
          : Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    const Icon(Icons.check_circle_rounded, color: T.success, size: 30),
                    const SizedBox(width: 12),
                    Expanded(child: Text(_message, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800, height: 1.3))),
                  ],
                ),
                const SizedBox(height: 16),
                Row(
                  children: [
                    Expanded(
                      child: OutlinedButton(
                        style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(T.ctaHeight)),
                        onPressed: _rescan,
                        child: Text(l.bindRescan, textAlign: TextAlign.center),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: FilledButton(
                        style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(T.ctaHeight), textStyle: const TextStyle(fontSize: 17, fontWeight: FontWeight.w800)),
                        onPressed: _done,
                        child: Text(l.done),
                      ),
                    ),
                  ],
                ),
              ],
            ),
    );
  }
}
