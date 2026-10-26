import 'dart:async';

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
import 'barcode_lookup.dart';
import 'code_lookup.dart';
import 'live_shot.dart';
import 'shot_flow.dart';
import '../../core/util/beep.dart';
import 'scan_animations.dart';
import 'cam_label.dart';
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
    // 2026-10-22 (аудит С-7): + ITF (коробки GTIN-14 в приёмке)
    formats: const [BarcodeFormat.ean13, BarcodeFormat.ean8, BarcodeFormat.code128, BarcodeFormat.upcA, BarcodeFormat.upcE, BarcodeFormat.itf14, BarcodeFormat.qrCode],
    // 13.10, телефон владельца: при noDuplicates тот же штрихкод второй раз не приходил, пока камера не увидит другой, —
    // две пачки Mars подряд не добавлялись. Повтор отсекаем сами (_isNewScan).
    detectionSpeed: DetectionSpeed.normal,
    // 2026-10-22 (скорость скана): пауза между кадрами с кодом 100 мс вместо 250 — следующий товар в кассу быстрее;
    // картинка кадра в Dart не возвращается (returnImage: false — по умолчанию, указано явно), анализ — в стандартном
    // разрешении плагина (не 4K).
    detectionTimeoutMs: 100,
    returnImage: false,
    // 2026-10-22 (аудит С-8): мелкий код — камера сама приближает (Android)
    autoZoom: true,
  );
  late ScanMode _mode = widget.initial;
  Product? _found;

  /// 2026-10-22 (аудит С-1): повтор скана — по каждому коду отдельно ([ScanRepeat]).
  late final _repeat = ScanRepeat(receiving: widget.returnCodes);

  /// Идёт разбор кода (окно размера одежды, вопрос «добавить товар?») — новые коды не берём (аудит С-3).
  bool _processing = false;
  double _qty = 1;
  bool _continuous = true;
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
    // 2026-10-22 (скорость скана): список камер — заранее, пока сканер включается: переход в умную камеру без паузы
    unawaited(CameraGate.warmUp());
    // правила весовых этикеток компании (префикс «сумма» у Rongta и т. п.)
    unawaited(ScaleRulesLoader.ensure(ref.read(databaseProvider), ref.read(apiProvider)).catchError((_) {}));
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
      default:
        break;
    }
  }

  /// Код ближе всех к центру кадра (рамка — в центре): соседний товар на прилавке не считывается.
  static String? _nearestCode(BarcodeCapture capture) {
    String? best;
    var bestD = double.infinity;
    final w = capture.size.width, h = capture.size.height;
    for (final b in capture.barcodes) {
      final code = normalizeCode(b.rawValue);
      if (code.isEmpty) continue;
      var d = 0.0;
      if (b.corners.isNotEmpty && w > 0 && h > 0) {
        final cx = b.corners.map((o) => o.dx).reduce((a, c) => a + c) / b.corners.length / w - .5;
        final cy = b.corners.map((o) => o.dy).reduce((a, c) => a + c) / b.corners.length / h - .5;
        d = cx * cx + cy * cy;
      }
      if (d < bestD) {
        bestD = d;
        best = code;
      }
    }
    return best;
  }

  Future<void> _onDetect(BarcodeCapture capture) async {
    final code = _nearestCode(capture);
    if (code == null) return;
    final now = DateTime.now();
    _repeat.receiving = widget.returnCodes || _mode != ScanMode.sale;
    if (!_repeat.isNew(code, now) || _processing || _asking) return;
    _repeat.counted(code, now);
    _processing = true;
    try {
      await _handleCode(code);
    } finally {
      _processing = false;
      // пока шёл разбор (окно размера), код был в кадре — «ушёл из кадра» считаем от конца разбора
      _repeat.seen(code, DateTime.now());
    }
  }

  Future<void> _handleCode(String code) async {
    HapticFeedback.mediumImpact();
    // 2026-10-22 (мобилка, админ): бип через Android (слышен и при выключенных звуках касаний), было SystemSound.click.
    Beep.ok();
    final db = ref.read(databaseProvider);

    if (widget.returnCodes) {
      _received.add(code);
      _frame.currentState?.success();
      // приёмка: этикетка весов — не товар поставки
      final hit = await db.productByScan(code, allowWeightLabel: false);
      final p = hit?.product;
      // 2026-10-26 (живой аудит, П7): штрихкод упаковки прибавляет всю упаковку — в подсказке то же число, что запишет приёмка
      _popups.currentState?.add(p == null ? '+1 · $code' : '+${qtyText(hit!.qty ?? 1)} ${p.name}', color: p == null ? T.warn : T.success);
      if (mounted) setState(() {});
      return;
    }
    // 2026-10-22 (аудит С-4): незнакомый код — сначала сервер (2 с), как касса; без связи — не предлагать дубль
    final hit = await CodeLookup.find(db, ref.read(apiProvider), code);
    if (!mounted) return;
    final p = hit.product;
    if (p == null) {
      _frame.currentState?.fail();
      HapticFeedback.heavyImpact();
      Beep.error();
      if (hit.unavailable) {
        toast(context, context.l.notFoundOffline(code), error: true);
      } else {
        await _offerNewProduct(code);
      }
      return;
    }
    _frame.currentState?.success();
    setState(() {
      _found = p;
      _foundQty = hit.qty;
      _qty = hit.qty ?? 1;
    });
    if (_mode == ScanMode.sale && _continuous && (!p.isWeight || hit.qty != null)) {
      // Весовая этикетка несёт вес — добавляем сразу, без вопроса «сколько кг».
      await addProduct(context, ref, p, qty: hit.qty);
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
    _repeat.seen(code, DateTime.now()); // тот же штрихкод сразу после возврата не спрашиваем снова
    if (mounted) await _camera.start();
  }

  /// 2026-10-22 (аудит П7): какие префиксы весовых этикеток — сумма, какие — вес (у владельца весы Rongta с префиксом 21
  /// печатают сумму). Общая настройка всех телефонов компании; менять может тот, у кого есть право на товары.
  Future<void> _scalePrefixes() async {
    final s = ref.read(sessionProvider);
    if (s == null || !s.canRoute('/admin/new-product')) return;
    final l = context.l;
    final r = ScaleRules.current;
    final amount = TextEditingController(text: (r.amountPrefixes.toList()..sort()).join(' '));
    final weight = TextEditingController(text: (r.weightPrefixes.toList()..sort()).join(' '));
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.scaleLabelsTitle),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(l.scaleLabelsHint, style: TextStyle(color: c.pal.textSoft, fontSize: 13)),
            const SizedBox(height: 8),
            TextField(controller: amount, keyboardType: TextInputType.number, decoration: InputDecoration(labelText: l.scaleAmountPrefixes, hintText: '21 25')),
            TextField(controller: weight, keyboardType: TextInputType.number, decoration: InputDecoration(labelText: l.scaleWeightPrefixes, hintText: '20 22')),
          ],
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.save)),
        ],
      ),
    );
    if (ok != true || !mounted) return;
    final a = ScaleRules.parsePrefixes(amount.text);
    final w = ScaleRules.parsePrefixes(weight.text)..removeAll(a);
    try {
      await ScaleRulesLoader.savePrefixes(ref.read(databaseProvider), ref.read(apiProvider), a, w);
      if (mounted) toast(context, l.saved);
    } catch (e) {
      if (mounted) toast(context, '$e', error: true);
    }
  }

  /// 2026-10-22: камеру сканера отдать другому экрану (умная камера, весы, сканер приёмки): сначала дождаться остановки
  /// сканера, по возвращении — дождаться, пока умная камера отпустит камеру ([CameraGate]), и только тогда включить сканер.
  Future<void> _handOver(Future<Object?> Function() open) async {
    if (_handedOver) return;
    _handedOver = true;
    try {
      await _camera.stop();
    } catch (_) {}
    if (!mounted) return;
    try {
      await open();
    } finally {
      await CameraGate.idle();
      _handedOver = false;
      if (mounted) await _camera.start();
    }
  }

  /// 13.10, владелец: «в сканере при нажатии назад приложение должно вернуться в продажу, а не выходить из программы».
  /// Сканер открывается вкладкой (go) — истории нет, и системная «Назад» закрывала приложение. Теперь: открыт из продажи
  /// (push) — просто закрываем; вкладкой — в продажу; сканер приёмки — как раньше, с отсканированными кодами.
  /// 2026-10-26 (живой аудит, П8, С-9): вкладкой — на «Главную», как «Назад» на любой вкладке (CashierShell); из продажи
  /// кассир возвращается кнопкой «Чек · N» под карточкой товара.
  void _back() {
    if (widget.returnCodes) {
      Navigator.pop(context, _received);
    } else if (context.canPop()) {
      context.pop();
    } else {
      context.go('/home');
    }
  }

  /// 2026-10-22 (скорость скана): число позиций в чеке читает только нижняя панель, а не весь экран с камерой —
  /// добавление товара больше не перерисовывает видоискатель, рамку и верхнюю панель.
  Widget _sheetWithCount(BuildContext context, Product p) =>
      Consumer(builder: (context, ref, _) => _sheet(context, p, ref.watch(cartProvider).count));

  @override
  Widget build(BuildContext context) {
    final l = context.l;
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
              child: LayoutBuilder(
                builder: (context, box) => MobileScanner(
                  controller: _camera,
                  onDetect: _onDetect,
                  // 2026-10-22 (аудит С-2): чёрный экран — MobileScanner сам включал камеру при возврате в приложение, мимо
                  // _handedOver (камеру в это время держала умная камера или весы). Жизненным циклом управляет этот экран.
                  useAppLifecycleState: false,
                  // 2026-10-22 (аудит С-1): читается только рамка (с запасом), а не весь кадр
                  scanWindow: Rect.fromCenter(center: box.biggest.center(Offset.zero), width: 330, height: 230),
                  errorBuilder: (c, e) => Center(
                    child: Padding(
                      padding: const EdgeInsets.all(24),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(
                            l.cameraUnavailable,
                            textAlign: TextAlign.center,
                            style: const TextStyle(color: Colors.white70),
                          ),
                          const SizedBox(height: 12),
                          // 2026-10-22 (аудит С-6): после «Разрешить» в настройках — включить снова, без переустановки
                          FilledButton.icon(
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
            ),
            // Рамка: бегущий луч, вспышка при скане; над ней — всплывающие «+1 товар»
            Center(child: ScanFrame(key: _frame)),
            Align(
              alignment: const Alignment(0, -.42),
              child: ScanPopups(key: _popups),
            ),
            Align(
              alignment: const Alignment(0, .28),
              // 2026-10-22: сканер кодов (доп. штрихкод, приёмка) — пока ничего не отсканировано, сказать, что делать и что дальше
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 24),
                // 2026-10-22 (подпись «Умная камера»): подсказка сканера — на тёмной подложке, читается на светлом кадре
                child: CamLabel(
                  widget.returnCodes ? (_received.isEmpty ? l.scanCodesHint : l.scanReceiveHint(_received.length)) : l.scanHint,
                  size: 15,
                  weight: FontWeight.w700,
                  align: TextAlign.center,
                  maxLines: 3,
                ),
              ),
            ),
            // Верхняя панель — только сверху и по высоте кнопок, что бы ни было внутри.
            Align(
              alignment: Alignment.topCenter,
              child: SafeArea(
                child: Padding(
                  padding: const EdgeInsets.all(14),
                  child: Row(
                    children: [
                      _Round(icon: Icons.close_rounded, label: l.a11yBack, onTap: _back),
                      const SizedBox(width: 10),
                      if (!widget.returnCodes)
                        Expanded(
                          child: ScanModeBar(
                            mode: _mode,
                            // 2026-10-22 (аудит П7): долгое нажатие на «Весы» — префиксы этикеток весов компании
                            onLongPress: (m) {
                              if (m == ScanMode.weigh) _scalePrefixes();
                            },
                            onSelect: (m) {
                              // 13.10, владелец: «умная камера при продаже где?» — «По виду»: снимок товара без штрихкода,
                              // ИИ узнаёт его и находит в каталоге; камеру сканера на время снимка отдаём системной камере.
                              // 2026-10-20, владелец: «ставить картошку… умной камерой сканирует вместе с показаниями весов».
                              // 2026-10-22: «умная камера после сканера очень долго думает» и чёрный экран — сканер не
                              // ждал, пока остановится, а умная камера уже включалась на ту же камеру. Теперь _handOver.
                              if (m == ScanMode.weigh) {
                                _handOver(() => weighForSale(context, ref));
                              } else if (m == ScanMode.look) {
                                _handOver(() => recognizeForSale(context, ref));
                              } else if (m == ScanMode.receive) {
                                // Вторая камера (сканер приёмки) не откроется, пока работает эта — останавливаем и включаем по возвращении.
                                _handOver(() => context.push('/stock/receive'));
                              } else {
                                setState(() => _mode = m);
                              }
                            },
                          ),
                        )
                      else
                        const Spacer(),
                      const SizedBox(width: 10),
                      // 2026-10-22 (аудит С-9): состояние фонарика — от камеры; нет вспышки — кнопки нет
                      ValueListenableBuilder(
                        valueListenable: _camera,
                        builder: (context, v, _) => v.torchState == TorchState.unavailable
                            ? const SizedBox(width: 46)
                            : _Round(
                                icon: v.torchState == TorchState.on ? Icons.flash_on_rounded : Icons.flash_off_rounded,
                                label: l.torchToggle,
                                onTap: () async {
                                  try {
                                    await _camera.toggleTorch();
                                  } catch (_) {}
                                },
                              ),
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
                  child: _found == null ? const SizedBox.shrink() : KeyedSubtree(key: ValueKey(_found!.id), child: _sheetWithCount(context, _found!)),
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

/// 2026-10-26 (живой аудит, П8, Н-2): полоса режимов сканера. Один размер шрифта у всех подписей (раньше FittedBox сжимал
/// каждую по-своему), касание не меньше 48 × 48 dp; на узком экране (360 dp) полоса прокручивается по горизонтали, выбранный
/// режим подводится в видимую часть.
class ScanModeBar extends StatelessWidget {
  final ScanMode mode;
  final void Function(ScanMode mode) onSelect;
  final void Function(ScanMode mode)? onLongPress;
  const ScanModeBar({super.key, required this.mode, required this.onSelect, this.onLongPress});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Container(
      padding: const EdgeInsets.all(4),
      decoration: BoxDecoration(color: camShade, borderRadius: BorderRadius.circular(14)),
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(
          children: [
            for (final (m, label) in [
              (ScanMode.sale, l.toCheck),
              (ScanMode.look, l.byLook),
              (ScanMode.weigh, l.weighShort),
              (ScanMode.price, l.checkPrice),
              (ScanMode.receive, l.receiving),
            ])
              Builder(
                builder: (itemContext) => Semantics(
                  button: true,
                  selected: mode == m,
                  label: label,
                  excludeSemantics: true,
                  child: GestureDetector(
                    behavior: HitTestBehavior.opaque,
                    onLongPress: onLongPress == null ? null : () => onLongPress!(m),
                    onTap: () {
                      onSelect(m);
                      if (itemContext.mounted) Scrollable.ensureVisible(itemContext, alignment: .5, duration: const Duration(milliseconds: 180));
                    },
                    child: Container(
                      constraints: const BoxConstraints(minHeight: 48, minWidth: 48),
                      padding: const EdgeInsets.symmetric(horizontal: 14),
                      decoration: BoxDecoration(color: mode == m ? T.accent : Colors.transparent, borderRadius: BorderRadius.circular(10)),
                      // не alignment у Container: он растягивал плашку на всю высоту экрана поверх камеры (13.10)
                      child: Center(
                        widthFactor: 1,
                        heightFactor: 1,
                        child: Text(
                          label,
                          maxLines: 1,
                          softWrap: false,
                          textAlign: TextAlign.center,
                          style: TextStyle(fontSize: 14, fontWeight: FontWeight.w700, color: mode == m ? T.navy : Colors.white),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _Round extends StatelessWidget {
  final IconData icon;
  final VoidCallback onTap;
  final String label;
  const _Round({required this.icon, required this.onTap, required this.label});
  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    label: label,
    excludeSemantics: true,
    child: Tooltip(
      message: label,
      child: Material(
        color: camShade,
        shape: const CircleBorder(),
        child: InkWell(
          customBorder: const CircleBorder(),
          onTap: onTap,
          child: SizedBox(width: 48, height: 48, child: Icon(icon, color: Colors.white)),
        ),
      ),
    ),
  );
}
