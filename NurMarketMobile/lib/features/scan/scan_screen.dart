import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../sale/sale_screen.dart';
import 'scan_animations.dart';
import 'smart_camera.dart';
import 'weigh_camera.dart';

// 2026-10-20: weigh — весовой товар умной камерой (вес с табло весов).
enum ScanMode { sale, price, receive, look, weigh }

/// Сканер камерой (Т3): EAN-13/8, Code128, QR; непрерывный режим; фонарик.
/// В режиме «Приёмка», открытом из экрана приёмки, возвращает штрихкоды через [Navigator.pop].
class ScanScreen extends ConsumerStatefulWidget {
  final ScanMode initial;
  final bool returnCodes;
  const ScanScreen({super.key, this.initial = ScanMode.sale, this.returnCodes = false});
  @override
  ConsumerState<ScanScreen> createState() => _ScanScreenState();
}

class _ScanScreenState extends ConsumerState<ScanScreen> with WidgetsBindingObserver {
  final _camera = MobileScannerController(
    formats: const [BarcodeFormat.ean13, BarcodeFormat.ean8, BarcodeFormat.code128, BarcodeFormat.upcA, BarcodeFormat.upcE, BarcodeFormat.qrCode],
    // 13.10, телефон владельца: при noDuplicates тот же штрихкод второй раз не приходил, пока камера не увидит другой, —
    // две пачки Mars подряд не добавлялись. Повтор отсекаем сами (_onDetect: тот же код не чаще раза в 1,5 с).
    detectionSpeed: DetectionSpeed.normal,
  );
  late ScanMode _mode = widget.initial;
  Product? _found;
  String? _lastCode;
  DateTime _lastAt = DateTime.fromMillisecondsSinceEpoch(0);
  double _qty = 1;
  bool _continuous = true;
  bool _torch = false;
  final _received = <String>[];
  final _frame = GlobalKey<ScanFrameState>();
  final _popups = GlobalKey<ScanPopupsState>();

  /// Камера отдана другому экрану (сканер приёмки) — возврат в приложение её не включает.
  bool _handedOver = false;
  double? _foundQty;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _camera.dispose();
    super.dispose();
  }

  /// 13.10: после сворачивания приложения (звонок, WhatsApp) камера оставалась чёрной — Android отбирает её у фона,
  /// а сканер сам не перезапускается. Останавливаем при уходе и включаем при возврате.
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (_handedOver || !_camera.value.hasCameraPermission) return;
    switch (state) {
      case AppLifecycleState.resumed:
        _camera.start();
      case AppLifecycleState.inactive:
      case AppLifecycleState.paused:
        _camera.stop();
        if (_torch) setState(() => _torch = false);
      default:
        break;
    }
  }

  Future<void> _onDetect(BarcodeCapture capture) async {
    final code = capture.barcodes.firstOrNull?.rawValue?.trim();
    if (code == null || code.isEmpty) return;
    // Тот же код подряд — не чаще раза в 1,5 с: одна пачка под камерой не добавляется пять раз.
    if (code == _lastCode && DateTime.now().difference(_lastAt) < const Duration(milliseconds: 1500)) return;
    _lastCode = code;
    _lastAt = DateTime.now();
    HapticFeedback.mediumImpact();
    SystemSound.play(SystemSoundType.click);

    if (widget.returnCodes) {
      _received.add(code);
      _frame.currentState?.success();
      final p = (await ref.read(databaseProvider).productByScan(code))?.product;
      _popups.currentState?.add(p == null ? '+1 · $code' : '+1 ${p.name}', color: p == null ? T.warn : T.success);
      setState(() {});
      return;
    }
    final hit = await ref.read(databaseProvider).productByScan(code);
    if (!mounted) return;
    final p = hit?.product;
    if (p == null) {
      _frame.currentState?.fail();
      HapticFeedback.heavyImpact();
      await _offerNewProduct(code);
      return;
    }
    _frame.currentState?.success();
    setState(() {
      _found = p;
      _foundQty = hit!.qty;
      _qty = hit.qty ?? 1;
    });
    if (_mode == ScanMode.sale && _continuous && (!p.isWeight || hit!.qty != null)) {
      // Весовая этикетка несёт вес — добавляем сразу, без вопроса «сколько кг».
      await addProduct(context, ref, p, qty: hit!.qty);
      _popups.currentState?.add(hit.qty == null ? '+1 ${p.name}' : '+${qtyText(hit.qty!)} ${p.unit} ${p.name}');
    } else if (_mode == ScanMode.price) {
      _popups.currentState?.add('${money(p.price)} ${context.l.som}', color: T.navy);
    }
  }

  /// 13.10, владелец: «в сканере, если товар не найден, нужно предложить добавить товар». Камера на время вопроса
  /// останавливается (иначе тот же штрихкод спрашивал бы снова); «Добавить» — новая позиция со штрихкодом и названием
  /// из общей базы NurCRM; после сохранения в режиме продажи товар сразу ложится в чек. Без права на склад — как раньше.
  bool _asking = false;

  Future<void> _offerNewProduct(String code) async {
    final l = context.l;
    final s = ref.read(sessionProvider);
    if (s == null || !s.canRoute('/admin/new-product')) {
      toast(context, l.barcodeNotFound(code), error: true);
      return;
    }
    if (_asking) return;
    _asking = true;
    _handedOver = true;
    await _camera.stop();
    if (!mounted) return;
    final add = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.notFoundTitle),
        content: Text(l.notFoundAsk(code)),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.addNewProduct)),
        ],
      ),
    );
    if (add == true && mounted) {
      await context.push('/admin/new-product?barcode=${Uri.encodeComponent(code)}');
      final hit = await ref.read(databaseProvider).productByScan(code);
      if (hit != null && mounted && _mode == ScanMode.sale) {
        await addProduct(context, ref, hit.product, qty: hit.qty);
        _popups.currentState?.add('+1 ${hit.product.name}');
      }
    }
    _asking = false;
    _handedOver = false;
    _lastCode = code;
    _lastAt = DateTime.now(); // тот же штрихкод сразу после возврата не спрашиваем снова
    if (mounted) await _camera.start();
  }

  /// 13.10, владелец: «в сканере при нажатии назад приложение должно вернуться в продажу, а не выходить из программы».
  /// Сканер открывается вкладкой (go) — истории нет, и системная «Назад» закрывала приложение. Теперь: открыт из продажи
  /// (push) — просто закрываем; вкладкой — в продажу; сканер приёмки — как раньше, с отсканированными кодами.
  void _back() {
    if (widget.returnCodes) {
      Navigator.pop(context, _received);
    } else if (context.canPop()) {
      context.pop();
    } else {
      context.go('/sale');
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final cart = ref.watch(cartProvider);
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _back();
      },
      child: Scaffold(
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
            // Рамка: бегущий луч, вспышка при скане; над ней — всплывающие «+1 товар»
            Center(child: ScanFrame(key: _frame)),
            Align(
              alignment: const Alignment(0, -.42),
              child: ScanPopups(key: _popups),
            ),
            Align(
              alignment: const Alignment(0, .28),
              child: Text(widget.returnCodes ? l.scanReceiveHint(_received.length) : l.scanHint, style: const TextStyle(color: Colors.white70, fontSize: 13)),
            ),
            // Верхняя панель — только сверху и по высоте кнопок, что бы ни было внутри.
            Align(
              alignment: Alignment.topCenter,
              child: SafeArea(
                child: Padding(
                  padding: const EdgeInsets.all(14),
                  child: Row(
                    children: [
                      _Round(icon: Icons.close_rounded, onTap: _back),
                      const SizedBox(width: 10),
                      if (!widget.returnCodes)
                        Expanded(
                          child: Container(
                            padding: const EdgeInsets.all(4),
                            decoration: BoxDecoration(color: const Color(0x33FFFFFF), borderRadius: BorderRadius.circular(14)),
                            child: Row(
                              children: [
                                for (final (m, label) in [
                                  (ScanMode.sale, l.toCheck),
                                  (ScanMode.look, l.byLook),
                                  (ScanMode.weigh, l.weighShort),
                                  (ScanMode.price, l.checkPrice),
                                  (ScanMode.receive, l.receiving),
                                ])
                                  Expanded(
                                    child: GestureDetector(
                                      onTap: () {
                                        // 13.10, владелец: «умная камера при продаже где?» — «По виду»: снимок товара без штрихкода,
                                        // ИИ узнаёт его и находит в каталоге; камеру сканера на время снимка отдаём системной камере.
                                        // 2026-10-20, владелец: «ставить картошку… умной камерой сканирует вместе с показаниями весов».
                                        if (m == ScanMode.weigh) {
                                          _handedOver = true;
                                          _camera.stop();
                                          weighForSale(context, ref).whenComplete(() {
                                            _handedOver = false;
                                            if (mounted) _camera.start();
                                          });
                                          return;
                                        }
                                        if (m == ScanMode.look) {
                                          _handedOver = true;
                                          _camera.stop();
                                          recognizeForSale(context, ref).whenComplete(() {
                                            _handedOver = false;
                                            if (mounted) _camera.start();
                                          });
                                          return;
                                        }
                                        if (m == ScanMode.receive) {
                                          // Вторая камера (сканер приёмки) не откроется, пока работает эта — останавливаем и включаем по возвращении.
                                          _handedOver = true;
                                          _camera.stop();
                                          context.push('/stock/receive').then((_) {
                                            _handedOver = false;
                                            if (mounted) _camera.start();
                                          });
                                          return;
                                        }
                                        setState(() => _mode = m);
                                      },
                                      child: Container(
                                        padding: const EdgeInsets.symmetric(vertical: 9),
                                        decoration: BoxDecoration(color: _mode == m ? T.accent : Colors.transparent, borderRadius: BorderRadius.circular(10)),
                                        // 13.10, телефон владельца: alignment растягивал плашку на всю высоту экрана поверх камеры —
                                        // по центру ставим сам текст, высота — по тексту.
                                        child: Text(
                                          label,
                                          maxLines: 1,
                                          textAlign: TextAlign.center,
                                          style: TextStyle(fontSize: 12, fontWeight: FontWeight.w700, color: _mode == m ? T.navy : Colors.white),
                                        ),
                                      ),
                                    ),
                                  ),
                              ],
                            ),
                          ),
                        )
                      else
                        const Spacer(),
                      const SizedBox(width: 10),
                      _Round(
                        icon: _torch ? Icons.flash_on_rounded : Icons.flash_off_rounded,
                        onTap: () async {
                          await _camera.toggleTorch();
                          setState(() => _torch = !_torch);
                        },
                      ),
                    ],
                  ),
                ),
              ),
            ),
            if (widget.returnCodes && _received.isNotEmpty)
              Align(
                alignment: Alignment.bottomCenter,
                child: SafeArea(
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: FilledButton(onPressed: () => Navigator.pop(context, _received), child: Text(l.doneCount(_received.length))),
                  ),
                ),
              ),
            if (!widget.returnCodes)
              Align(
                alignment: Alignment.bottomCenter,
                child: AnimatedSwitcher(
                  duration: const Duration(milliseconds: 280),
                  switchInCurve: Curves.easeOutCubic,
                  transitionBuilder: (child, a) => SlideTransition(
                    position: Tween(begin: const Offset(0, 1), end: Offset.zero).animate(a),
                    child: child,
                  ),
                  child: _found == null ? const SizedBox.shrink() : KeyedSubtree(key: ValueKey(_found!.id), child: _sheet(context, _found!, cart.count)),
                ),
              ),
          ],
        ),
      ),
    );
  }

  Widget _sheet(BuildContext context, Product p, int cartCount) {
    final l = context.l;
    final pal = context.pal;
    return Container(
      decoration: BoxDecoration(
        color: pal.panel,
        borderRadius: const BorderRadius.vertical(top: Radius.circular(24)),
      ),
      padding: EdgeInsets.fromLTRB(16, 10, 16, MediaQuery.paddingOf(context).bottom + 14),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Container(
            width: 40,
            height: 4,
            decoration: BoxDecoration(color: pal.border, borderRadius: BorderRadius.circular(2)),
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Container(
                width: 52,
                height: 52,
                decoration: BoxDecoration(color: tileColor(p.name), borderRadius: BorderRadius.circular(12)),
                alignment: Alignment.center,
                child: Text(
                  initials(p.name),
                  style: const TextStyle(fontWeight: FontWeight.w800, color: T.navy),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      p.name,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 15),
                    ),
                    Text('${p.barcode ?? ''} · ${l.inStock(qtyText(p.quantity), p.unit)}', style: TextStyle(fontSize: 12, color: pal.textSoft)),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Text(
                '${money(p.price)} ${l.som}',
                style: TextStyle(fontSize: 24, fontWeight: FontWeight.w800, color: pal.price),
              ),
              const Spacer(),
              if (_mode == ScanMode.sale && _foundQty != null)
                Text('${qtyText(_foundQty!)} ${p.unit}', style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16)),
              if (_mode == ScanMode.sale && !p.isWeight && _foundQty == null) ...[
                IconButton.outlined(onPressed: () => setState(() => _qty = (_qty - 1).clamp(1, 9999)), icon: const Icon(Icons.remove)),
                SizedBox(
                  width: 36,
                  child: Text(
                    qtyText(_qty),
                    textAlign: TextAlign.center,
                    style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 16),
                  ),
                ),
                IconButton.filled(
                  style: IconButton.styleFrom(backgroundColor: context.pal.action),
                  onPressed: () => setState(() => _qty += 1),
                  icon: Icon(Icons.add, color: context.pal.onAction),
                ),
              ],
            ],
          ),
          if (_mode == ScanMode.sale) ...[
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              controlAffinity: ListTileControlAffinity.leading,
              value: _continuous,
              onChanged: (v) => setState(() => _continuous = v ?? true),
              title: Text(l.scanContinuous, style: const TextStyle(fontSize: 13)),
            ),
            Row(
              children: [
                OutlinedButton(
                  style: OutlinedButton.styleFrom(minimumSize: const Size(80, T.ctaHeight)),
                  onPressed: () => context.go('/sale'),
                  child: Text(l.checkN(cartCount)),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: FilledButton(
                    onPressed: _continuous && (!p.isWeight || _foundQty != null)
                        ? null
                        : () async {
                            await addProduct(context, ref, p, qty: _foundQty ?? (p.isWeight ? null : _qty));
                            if (context.mounted) toast(context, l.addedToCheck);
                          },
                    child: Text(_continuous && (!p.isWeight || _foundQty != null) ? l.addedAutomatically : l.addToCheck),
                  ),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _Round extends StatelessWidget {
  final IconData icon;
  final VoidCallback onTap;
  const _Round({required this.icon, required this.onTap});
  @override
  Widget build(BuildContext context) => Material(
    color: const Color(0x33FFFFFF),
    shape: const CircleBorder(),
    child: InkWell(
      customBorder: const CircleBorder(),
      onTap: onTap,
      child: SizedBox(width: 46, height: 46, child: Icon(icon, color: Colors.white)),
    ),
  );
}
