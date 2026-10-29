import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../../core/catalog/global_products.dart';
import '../../core/db/database.dart';
import '../../core/link/kassa_link.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../sale/sale_screen.dart';
import 'barcode_lookup.dart';
import 'code_lookup.dart';
import 'live_shot.dart';
import 'shot_flow.dart';
import '../../core/util/beep.dart';
import 'scan_animations.dart';
import 'cam_label.dart';
import 'kassa_scanner_mode.dart';
import '../profile/kassa_link_screen.dart';
import 'smart_camera.dart';
import 'weigh_camera.dart';

// 2026-10-20: weigh — весовой товар умной камерой (вес с табло весов).
enum ScanMode { sale, price, receive, look, weigh }

/// Сканер камерой (Т3): EAN-13/8, Code128, QR; непрерывный режим; фонарик.
/// В режиме «Приёмка», открытом из экрана приёмки, возвращает штрихкоды через [Navigator.pop].
class ScanScreen extends ConsumerStatefulWidget {
  /// 2026-10-29: null — взять последний режим, запомненный на телефоне; задан явно (приёмка, ссылка `?mode=price`) — он и берётся.
  final ScanMode? initial;
  final bool returnCodes;
  const ScanScreen({super.key, this.initial, this.returnCodes = false});
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
  late ScanMode _mode = widget.initial ?? ScanMode.sale;
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

  /// 2026-10-27: режим «Сканер для кассы» — штрихкоды уходят на кассу по Wi-Fi, а не в чек телефона.
  bool _kassa = false;
  KassaScannerController? _kassaCtl;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    // 2026-10-22 (скорость скана): список камер — заранее, пока сканер включается: переход в умную камеру без паузы
    unawaited(CameraGate.warmUp());
    // правила весовых этикеток компании (префикс «сумма» у Rongta и т. п.)
    unawaited(ScaleRulesLoader.ensure(ref.read(databaseProvider), ref.read(apiProvider)).catchError((_) {}));
    if (widget.initial == null && !widget.returnCodes) unawaited(_restoreMode());
  }

  /// 2026-10-29: режим сканера запоминается между запусками (продажа, цена, «Сканер для кассы»). Умная камера, весы и приёмка
  /// открывают отдельный экран и режим сканера не меняют — их не запоминаем. Ссылка/приёмка с явным режимом сюда не заходят.
  bool _modeTouched = false;

  Future<void> _restoreMode() async {
    final saved = await ScanModeMemory.load(ref.read(databaseProvider));
    if (!mounted || _modeTouched || saved == null) return;
    if (saved == ScanModeMemory.kassa) {
      if (KassaLink.supported && !_kassa) _toggleKassa();
    } else if (saved == ScanMode.price.name) {
      setState(() => _mode = ScanMode.price);
    }
  }

  void _remember(String value) {
    if (widget.returnCodes) return;
    unawaited(ScanModeMemory.save(ref.read(databaseProvider), value));
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _kassaCtl?.dispose();
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
    if (_kassa) {
      _kassaCtl?.onCode(code); // 2026-10-27: режим «Сканер для кассы» — код сразу на кассу, свой антидребезг
      return;
    }
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

  /// 2026-10-27: включить/выключить режим «Сканер для кассы». Связь не настроена — панель предлагает настроить.
  void _toggleKassa() {
    if (_kassa) {
      _kassaCtl?.dispose();
      setState(() {
        _kassa = false;
        _kassaCtl = null;
      });
      _remember(_mode.name);
      return;
    }
    final ctl = KassaScannerController(
      userName: () => ref.read(sessionProvider)?.userName ?? '',
      onFlash: (ok) => ok ? _frame.currentState?.success() : _frame.currentState?.fail(),
    );
    setState(() {
      _kassa = true;
      _found = null; // карточка товара телефона в этом режиме не нужна
      _kassaCtl = ctl;
    });
    unawaited(ctl.start());
    _remember(ScanModeMemory.kassa);
  }

  /// Из панели «не настроено»: экран «Связь с кассой»; камера на это время отдана экрану QR.
  Future<void> _setupKassa() => _handOver(() => Navigator.of(context).push<void>(MaterialPageRoute(builder: (_) => const KassaLinkScreen())));

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
    // 2026-10-27: код есть в справочнике готовых наименований NurCRM — подсказка (ждём сервер не больше 2 с)
    final known = await ref.read(globalProductsProvider).find(code).timeout(const Duration(seconds: 2), onTimeout: () => null);
    if (!mounted) return;
    final add = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        title: Text(l.notFoundTitle),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(l.notFoundAsk(code)),
            if (known != null) ...[
              const SizedBox(height: 10),
              Text(l.globalFoundAsk(known.name), style: const TextStyle(color: T.success, fontWeight: FontWeight.w700)),
              Text(l.globalSource, style: TextStyle(color: c.pal.textSoft, fontSize: 12)),
            ],
          ],
        ),
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
            if (!_kassa)
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
                  // 2026-10-29: полоса режимов и отдельная кнопка «Сканер для кассы» заменены одним селектом «Режим: …» на всю
                  // ширину (нижний лист со списком); кнопки «назад» и фонарик — отдельным рядом над ним.
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Row(
                        children: [
                          _Round(icon: Icons.close_rounded, label: l.a11yBack, onTap: _back),
                          const Spacer(),
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
                      if (!widget.returnCodes) ...[
                        const SizedBox(height: 8),
                        ScanModeSelect(
                          mode: _mode,
                          kassa: _kassa,
                          // 2026-10-27: «Сканер для кассы» — только в мобилке (Android/iOS), не в сканере приёмки
                          showKassa: KassaLink.supported,
                          // 2026-10-22 (аудит П7): долгое нажатие на «Весы» — префиксы этикеток весов компании
                          onLongPress: (m) {
                            if (m == ScanMode.weigh) _scalePrefixes();
                          },
                          onKassa: () {
                            _modeTouched = true;
                            if (!_kassa) _toggleKassa();
                          },
                          onSelect: (m) {
                            _modeTouched = true;
                            // 2026-10-29: выбор любого другого режима выключает «Сканер для кассы» (раньше — повторным нажатием кнопки)
                            if (_kassa) _toggleKassa();
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
                              _remember(m.name);
                            }
                          },
                        ),
                      ],
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
            if (_kassa && _kassaCtl != null)
              Align(
                alignment: Alignment.bottomCenter,
                child: KassaScannerPanel(controller: _kassaCtl!, onSetup: _setupKassa),
              ),
            if (!widget.returnCodes && !_kassa)
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

/// 2026-10-29: последний режим сканера (продажа, цена, «Сканер для кассы») — в настройках базы телефона.
class ScanModeMemory {
  static const key = 'scan.mode';
  static const kassa = 'kassa';
  static Future<String?> load(AppDatabase db) async {
    try {
      return await db.getSetting(key);
    } catch (_) {
      return null;
    }
  }

  static Future<void> save(AppDatabase db, String value) async {
    try {
      await db.setSetting(key, value);
    } catch (_) {}
  }
}

/// Пункт списка режимов: пять режимов продажи и «Сканер для кассы».
enum ScanChoice {
  sale(ScanMode.sale, Icons.shopping_cart_checkout_rounded),
  look(ScanMode.look, Icons.image_search_rounded),
  weigh(ScanMode.weigh, Icons.scale_rounded),
  price(ScanMode.price, Icons.price_check_rounded),
  receive(ScanMode.receive, Icons.move_to_inbox_rounded),
  kassa(null, Icons.point_of_sale_rounded);

  final ScanMode? mode;
  final IconData icon;
  const ScanChoice(this.mode, this.icon);

  String label(AppLocalizations l) => switch (this) {
    sale => l.toCheck,
    look => l.byLook,
    weigh => l.weighShort,
    price => l.checkPrice,
    receive => l.receiving,
    kassa => l.klScannerMode,
  };

  String sub(AppLocalizations l) => switch (this) {
    sale => l.scanModeSaleSub,
    look => l.scanModeLookSub,
    weigh => l.scanModeWeighSub,
    price => l.scanTileSub,
    receive => l.scanModeReceiveSub,
    kassa => l.scanModeKassaSub,
  };
}

/// 2026-10-29: селект режима сканера вместо полосы вкладок и отдельной кнопки «Сканер для кассы»: поле «Режим: Весы ▾» на всю
/// ширину (не ниже 48 dp, длинная подпись переносится, не обрезается) и нижний лист со списком — значок, название, описание,
/// отметка выбранного. «Сканер для кассы» — шестой пункт списка (если [showKassa]); поле показывает его, пока он включён.
class ScanModeSelect extends StatelessWidget {
  final ScanMode mode;
  final bool kassa;
  final bool showKassa;
  final void Function(ScanMode mode) onSelect;
  final VoidCallback onKassa;
  final void Function(ScanMode mode)? onLongPress;
  const ScanModeSelect({super.key, required this.mode, required this.onSelect, required this.onKassa, this.kassa = false, this.showKassa = true, this.onLongPress});

  ScanChoice get _current => kassa ? ScanChoice.kassa : ScanChoice.values.firstWhere((c) => c.mode == mode);

  Future<void> _open(BuildContext context) async {
    final picked = await showModalBottomSheet<ScanChoice>(
      context: context,
      showDragHandle: true,
      isScrollControlled: true,
      backgroundColor: context.pal.panel,
      builder: (c) => _ScanModeSheet(
        current: _current,
        showKassa: showKassa,
        onLongPress: onLongPress == null
            ? null
            : (m) {
                Navigator.pop(c);
                onLongPress!(m);
              },
      ),
    );
    if (picked == null || !context.mounted) return;
    if (picked == ScanChoice.kassa) {
      onKassa();
    } else {
      onSelect(picked.mode!);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final cur = _current;
    final fg = kassa ? T.navy : Colors.white;
    final name = cur.label(l);
    return Semantics(
      button: true,
      label: l.scanModeA11y(name),
      excludeSemantics: true,
      child: Material(
        color: kassa ? T.accent : camShade,
        borderRadius: BorderRadius.circular(14),
        child: InkWell(
          borderRadius: BorderRadius.circular(14),
          onTap: () => _open(context),
          child: ConstrainedBox(
            constraints: const BoxConstraints(minHeight: 48),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
              child: Row(
                children: [
                  Icon(cur.icon, size: 20, color: fg),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text.rich(
                      TextSpan(
                        children: [
                          TextSpan(text: '${l.scanModeLabel}: ', style: TextStyle(fontWeight: FontWeight.w500, color: fg.withValues(alpha: .8))),
                          TextSpan(text: name, style: TextStyle(fontWeight: FontWeight.w800, color: fg)),
                        ],
                      ),
                      style: const TextStyle(fontSize: 14),
                    ),
                  ),
                  const SizedBox(width: 6),
                  Icon(Icons.keyboard_arrow_down_rounded, size: 24, color: fg),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _ScanModeSheet extends StatelessWidget {
  final ScanChoice current;
  final bool showKassa;
  final void Function(ScanMode mode)? onLongPress;
  const _ScanModeSheet({required this.current, required this.showKassa, this.onLongPress});

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final pal = context.pal;
    final items = [for (final c in ScanChoice.values) if (c != ScanChoice.kassa || showKassa) c];
    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(12, 0, 12, 12),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(8, 0, 8, 8),
              child: Text(l.scanModeTitle, style: TextStyle(fontSize: 18, fontWeight: FontWeight.w800, color: pal.text)),
            ),
            for (final c in items)
              Semantics(
                button: true,
                selected: c == current,
                label: '${c.label(l)}. ${c.sub(l)}',
                excludeSemantics: true,
                child: Material(
                  color: c == current ? pal.accentSoft : Colors.transparent,
                  borderRadius: BorderRadius.circular(14),
                  child: InkWell(
                    borderRadius: BorderRadius.circular(14),
                    onTap: () => Navigator.pop(context, c),
                    onLongPress: c.mode == null || onLongPress == null ? null : () => onLongPress!(c.mode!),
                    child: ConstrainedBox(
                      constraints: const BoxConstraints(minHeight: 56),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
                        child: Row(
                          children: [
                            Icon(c.icon, size: 24, color: pal.price),
                            const SizedBox(width: 14),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(c.label(l), style: TextStyle(fontSize: 15, fontWeight: FontWeight.w800, color: pal.text)),
                                  Text(c.sub(l), style: TextStyle(fontSize: 13, color: pal.textSoft)),
                                ],
                              ),
                            ),
                            if (c == current) ...[
                              const SizedBox(width: 8),
                              Icon(Icons.check_rounded, size: 24, color: pal.price),
                            ],
                          ],
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
