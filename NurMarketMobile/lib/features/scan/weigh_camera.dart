import 'dart:async';
import 'dart:typed_data';

import 'package:camera/camera.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/scale_reading.dart';
import '../../core/ai/vision.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../admin/ai_access_ui.dart';
import '../sale/sale_screen.dart';
import 'live_shot.dart';
import 'cam_label.dart';
import 'smart_camera.dart';

/// 2026-10-20, владелец: «умная камера должна работать так: ставить картошку или весовой товар, и умной камерой
/// сканирует вместе с показаниями весов, и камера сразу пересчитывает, находит товар, ставит цену и добавляет в чек;
/// картошка 1 кг 40 сом — если на весах 500 грамм, то естественно 20 сом». Весы к телефону не подключаются —
/// вес читается с табло весов на кадре.
///
/// Шаги: кадр → ИИ (Gemini, тот же ключ) называет товар и читает вес → совпадение в каталоге (слова названия и
/// обученные эталоны) → ещё кадры каждые ~0,7 с, пока 2 показания подряд не совпадут (±5 г) → сумма = цена за кг × вес
/// (до тыйынов, как в кассе) → строка в чек. Уверенно — сразу в чек; сомнительно — кассир подтверждает одним касанием.
class WeighResult {
  final String seenName;
  final List<(Product, double)> candidates;
  final double? kg;
  final bool stable;
  final double confidence;

  /// Ошибка ИИ (нет связи, нет ключа) — дальше ручной ввод.
  final String? error;
  final bool noKey;

  /// 2026-10-22 (аудит У-1, У-3): первый кандидат — эталон обучения; стоимость с табло (если ИИ её видел).
  final bool exact;
  final double? total;

  /// 2026-10-22: кассир нажал «Без камеры — выбрать товар и ввести вес».
  final bool manual;
  const WeighResult({
    this.seenName = '',
    this.candidates = const [],
    this.kg,
    this.stable = false,
    this.confidence = 0,
    this.error,
    this.noKey = false,
    this.exact = false,
    this.total,
    this.manual = false,
  });

  Product? get best => candidates.isNotEmpty && candidates.first.$2 >= 0.5 ? candidates.first.$1 : null;
}

class WeighCameraPage extends ConsumerStatefulWidget {
  const WeighCameraPage({super.key});

  static Future<WeighResult?> open(BuildContext context) =>
      Navigator.of(context).push<WeighResult>(MaterialPageRoute(builder: (_) => const WeighCameraPage()));

  @override
  ConsumerState<WeighCameraPage> createState() => _WeighCameraPageState();
}

class _WeighCameraPageState extends ConsumerState<WeighCameraPage> with WidgetsBindingObserver {
  // 2026-10-22 (аудит У-13): не больше 6 платных запросов на одно взвешивание (было 10)
  static const _maxShots = 6;
  CameraController? _camera;
  String? _error;
  bool _torch = false;
  bool _running = false;
  bool _closed = false;
  String _status = '';
  double? _lastKg;
  String _seen = '';
  final _stab = WeightStabilizer();
  List<(Product, double)> _candidates = const [];
  double _confidence = 0;
  Future<List<SeenMatch>>? _exact;
  bool _exactFound = false;
  double? _total;

  /// 2026-10-22 (аудит У-11): весовые товары магазина — один раз за открытие (раньше до 3000 товаров при каждом нажатии).
  late final Future<List<String>> _names = () async {
    try {
      return [for (final p in await ref.read(databaseProvider).searchProducts('', limit: 3000)) if (p.isWeight) p.name];
    } catch (_) {
      return const <String>[];
    }
  }();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _start();
    _names.ignore();
  }

  bool _starting = false;

  /// 2026-10-22, владелец: «умный сканер при нажатии сам сразу сканирует — добавь кнопку». Камера открывается и ждёт:
  /// чтение весов начинается только кнопкой «Сканировать» (раньше — само через 1,2 с после открытия).
  /// Камера — через [CameraGate]: не включается, пока сканер штрихкодов или прошлая умная камера её не отпустили.
  Future<void> _start() async {
    if (_starting || _closed) return;
    _starting = true;
    try {
      // high (≈720p) — цифры табло читаются, а кадр уходит в ИИ быстро
      final c = await CameraGate.open(ResolutionPreset.high);
      if (!mounted || _closed) {
        CameraGate.release(c);
        return;
      }
      setState(() => _camera = c);
    } catch (e) {
      if (mounted) setState(() => _error = context.l.cameraUnavailable); // 2026-10-22 (аудит У-9): без сырого текста
    } finally {
      _starting = false;
    }
  }

  void _stopCamera() {
    final c = _camera;
    if (c == null) return;
    _camera = null;
    CameraGate.release(c);
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    switch (state) {
      case AppLifecycleState.inactive:
      case AppLifecycleState.paused:
      case AppLifecycleState.hidden:
        if (_camera != null && !_running) {
          _stopCamera();
          if (mounted) setState(() {});
        }
      case AppLifecycleState.resumed:
        if (_camera == null && !_starting && !_closed) _start();
      case AppLifecycleState.detached:
        break;
    }
  }

  @override
  void dispose() {
    _closed = true;
    WidgetsBinding.instance.removeObserver(this);
    _stopCamera();
    super.dispose();
  }

  /// Закрыть: камеру отпускаем сразу, чтобы сканер штрихкодов за этим экраном включился без чёрного экрана.
  void _finish(WeighResult? r) {
    if (_closed || !mounted) return;
    _closed = true;
    _stopCamera();
    setState(() {});
    Navigator.of(context).pop(r);
  }

  Future<Uint8List?> _snap() async {
    final c = _camera;
    if (c == null || !c.value.isInitialized) return null;
    final f = await c.takePicture();
    return f.readAsBytes();
  }

  /// Товар из каталога по названию от ИИ; обученные эталоны — в фоне (их ответ точнее).
  Future<void> _matchProduct(String name, Uint8List jpeg) async {
    final vision = ref.read(visionProvider);
    final found = await vision.match([SeenItem(name: name)]);
    _candidates = found.isEmpty ? const [] : found.first.candidates;
    _exact = exactMatches(ref, jpeg, found);
  }

  Future<List<(Product, double)>> _withExact() async {
    final f = _exact;
    if (f == null) return _candidates;
    try {
      final exact = await f.timeout(const Duration(seconds: 6));
      if (exact.isEmpty) return _candidates;
      _exactFound = true;
      final ids = {for (final e in exact) e.candidates.first.$1.id};
      return [for (final e in exact) e.candidates.first, ..._candidates.where((c) => !ids.contains(c.$1.id))];
    } catch (_) {
      return _candidates;
    }
  }

  Future<void> _run() async {
    if (_running || _camera == null) return;
    setState(() => _running = true);
    final l = context.l;
    final vision = ref.read(visionProvider);
    // весовые товары магазина — ИИ называет товар так же, как в каталоге
    final names = await _names;
    var noDisplay = 0;
    // 2026-10-22, владелец: «умная камера очень долго думает». Было: кадр → ответ ИИ → пауза 0,7 с → следующий кадр,
    // а для устойчивого веса нужны 2 ответа подряд (≈ 2 ответа ИИ + 1 с). Теперь следующий кадр снимается и уходит в ИИ,
    // пока читается предыдущий (не больше 2 запросов сразу), — вес готов примерно через один ответ ИИ + 0,5 с.
    final inflight = <Future<ScaleReading>>[];
    final jpegs = <Uint8List>[];
    var shots = 0;
    try {
      if (mounted) setState(() => _status = l.weighReading);
      while (!_closed) {
        while (inflight.length < 2 && shots < _maxShots && !_closed) {
          final jpeg = await _snap();
          if (jpeg == null || _closed) return;
          // товар спрашиваем, пока он не узнан (второй кадр уходит раньше, чем пришёл ответ на первый)
          final f = vision.readScale(jpeg, withProduct: _seen.isEmpty, names: names);
          f.ignore(); // ошибку получим при await ниже; брошенный запрос (экран закрыт) не шумит
          inflight.add(f);
          jpegs.add(jpeg);
          shots++;
          if (inflight.length < 2) await Future<void>.delayed(const Duration(milliseconds: 400));
        }
        if (inflight.isEmpty) break;
        final jpeg = jpegs.removeAt(0);
        final r = await inflight.removeAt(0);
        if (_closed) return;
        final first = _seen.isEmpty;
        if (first && r.product.isNotEmpty) {
          _seen = r.product;
          _confidence = r.confidence;
          await _matchProduct(r.product, jpeg);
          // штучный товар — вес не нужен, сразу дальше
          final best = _candidates.isNotEmpty && _candidates.first.$2 >= 0.5 ? _candidates.first.$1 : null;
          if (best != null && !best.isWeight) {
            final list = await _withExact();
            _finish(WeighResult(seenName: _seen, candidates: list, exact: _exactFound, total: _total, stable: true, confidence: _confidence));
            return;
          }
        } else if (!first && r.confidence > 0) {
          _confidence = (_confidence + r.confidence) / 2;
          // 2026-10-22 (аудит У-3): второй ответ назвал другой товар — не уверены, кассир выберет сам
          if (r.product.isNotEmpty && VisionService.nameScore(r.product, _seen) < 0.5) {
            _confidence = _confidence.clamp(0, 0.5).toDouble();
            final more = await vision.match([SeenItem(name: r.product)]);
            final have = {for (final c in _candidates) c.$1.id};
            _candidates = [..._candidates, for (final m in more) ...m.candidates.where((c) => have.add(c.$1.id))];
          }
        }
        if (r.total != null) _total = r.total;
        // 2026-10-22 (аудит У-13): 3 ответа подряд «табло не видно» — не тратим запросы, подсказываем
        noDisplay = r.hasWeight ? 0 : noDisplay + 1;
        if (noDisplay >= 3) {
          if (mounted) setState(() => _status = l.weighAimDisplay);
          return;
        }
        final stable = _stab.add(r.weightKg);
        if (mounted) {
          setState(() {
            _lastKg = r.weightKg ?? _lastKg;
            _status = !r.hasWeight ? l.weighNoDisplay : (r.isZero ? l.weighZero : l.weighWaiting);
          });
        }
        if (stable != null && _seen.isNotEmpty) {
          final list = await _withExact();
          _finish(WeighResult(seenName: _seen, candidates: list, exact: _exactFound, total: _total, kg: stable, stable: true, confidence: _confidence));
          return;
        }
      }
      // вес так и не установился — кассир проверит прочитанное
      final list = await _withExact();
      _finish(WeighResult(seenName: _seen, candidates: list, exact: _exactFound, total: _total, kg: _lastKg, confidence: _confidence));
    } on VisionException catch (e) {
      _finish(WeighResult(seenName: _seen, candidates: _candidates, kg: _lastKg, error: e.noKey ? l.aiKeyNeeded : e.message, noKey: e.noKey));
    } catch (e) {
      _finish(WeighResult(seenName: _seen, candidates: _candidates, kg: _lastKg, error: '$e'));
    } finally {
      _running = false;
      if (mounted && !_closed) setState(() {});
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final c = _camera;
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _finish(null);
      },
      child: Scaffold(
        backgroundColor: Colors.black,
        body: Stack(
          fit: StackFit.expand,
          children: [
            if (c != null && c.value.isInitialized)
              LayoutBuilder(
                builder: (context, box) => FittedBox(
                  fit: BoxFit.cover,
                  child: SizedBox(
                    width: c.value.previewSize?.height ?? box.maxWidth,
                    height: c.value.previewSize?.width ?? box.maxHeight,
                    child: CameraPreview(c),
                  ),
                ),
              )
            else
              Center(
                child: _error != null
                    ? Padding(
                        padding: const EdgeInsets.all(24),
                        child: Column(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Text(_error!, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white70)),
                            const SizedBox(height: 12),
                            FilledButton.icon(onPressed: _start, icon: const Icon(Icons.refresh_rounded), label: Text(l.retry)),
                          ],
                        ),
                      )
                    : const CircularProgressIndicator(color: T.accent),
              ),
            // 2026-10-22: верхняя панель — сверху (в Stack с fit: expand она растягивалась и вставала посреди экрана)
            Align(alignment: Alignment.topCenter, child: SafeArea(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
                child: Row(
                  children: [
                    IconButton.filledTonal(onPressed: () => _finish(null), icon: const Icon(Icons.arrow_back_rounded)),
                    const SizedBox(width: 10),
                    // 2026-10-22 (подпись «Умная камера»): заголовок на тёмной пилюле — виден на любом кадре
                    Expanded(child: Align(alignment: Alignment.centerLeft, child: CamLabel(l.weighTitle, icon: Icons.scale_rounded))),
                    IconButton.filledTonal(
                      onPressed: c == null
                          ? null
                          : () async {
                              _torch = !_torch;
                              try {
                                await c.setFlashMode(_torch ? FlashMode.torch : FlashMode.off);
                              } catch (_) {}
                              if (mounted) setState(() {});
                            },
                      icon: Icon(_torch ? Icons.flash_on_rounded : Icons.flash_off_rounded),
                    ),
                  ],
                ),
              ),
            )),
            Positioned(
              left: 16,
              right: 16,
              bottom: 24 + MediaQuery.paddingOf(context).bottom,
              child: Container(
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(color: const Color(0xCC0B1F33), borderRadius: BorderRadius.circular(18)),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      _status.isEmpty ? l.weighAim : _status,
                      textAlign: TextAlign.center,
                      style: const TextStyle(color: Colors.white, fontSize: 15, fontWeight: FontWeight.w600),
                    ),
                    // 2026-10-22: что будет после «Сканировать» — пока камера ещё ничего не прочитала
                    if (_status.isEmpty && !_running) ...[
                      const SizedBox(height: 4),
                      Text(l.weighNext, textAlign: TextAlign.center, style: const TextStyle(color: Colors.white70, fontSize: 14)),
                    ],
                    if (_seen.isNotEmpty || _lastKg != null) ...[
                      const SizedBox(height: 8),
                      Text(
                        [if (_seen.isNotEmpty) _seen, if (_lastKg != null) '${kgText(_lastKg!)} ${l.kgUnit}'].join(' · '),
                        textAlign: TextAlign.center,
                        style: const TextStyle(color: T.accent, fontSize: 22, fontWeight: FontWeight.w800),
                      ),
                    ],
                    const SizedBox(height: 8),
                    // точки — сколько показаний подряд совпало
                    Row(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        for (var i = 0; i < _stab.need; i++)
                          Container(
                            width: 10,
                            height: 10,
                            margin: const EdgeInsets.symmetric(horizontal: 4),
                            decoration: BoxDecoration(shape: BoxShape.circle, color: i < _stab.run.length ? T.accent : Colors.white24),
                          ),
                      ],
                    ),
                    const SizedBox(height: 12),
                    FilledButton.icon(
                      style: FilledButton.styleFrom(
                        backgroundColor: T.accent,
                        foregroundColor: T.navy,
                        disabledBackgroundColor: T.accent.withValues(alpha: .55),
                        disabledForegroundColor: T.navy,
                        minimumSize: const Size.fromHeight(60),
                        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(30)),
                        textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
                      ),
                      onPressed: c != null && !_running && !_closed ? _run : null,
                      icon: _running
                          ? const SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 3, color: T.navy))
                          : const Icon(Icons.center_focus_strong_rounded, size: 28),
                      label: Text(_running ? l.scanBusy : l.scanShoot),
                    ),
                    // 2026-10-22: выход без камеры — табло не читается, товар не узнаётся
                    TextButton.icon(
                      style: TextButton.styleFrom(foregroundColor: Colors.white, minimumSize: const Size.fromHeight(48)),
                      onPressed: _closed ? null : () => _finish(WeighResult(kg: _lastKg, manual: true)),
                      icon: const Icon(Icons.edit_note_rounded),
                      label: Text(l.weighManualBtn, textAlign: TextAlign.center),
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Строка для кассира: «Картошка 0,500 кг × 40 = 20 сом».
String weighLineText(BuildContext context, Product p, double kg) =>
    context.l.weighLine(p.name, kgText(kg), money(p.price), money(weightTotal(p.price, kg)));

/// Положить в чек: весовой — вес (до грамма), штучный — 1 шт. Вес > 50 кг или ≤ 0 — переспросить.
Future<bool> _addWeighed(BuildContext context, WidgetRef ref, Product p, double? kg) async {
  final l = context.l;
  if (!p.isWeight) {
    await addProduct(context, ref, p, qty: 1);
    if (context.mounted) toast(context, l.addedToCheckName(p.name));
    return true;
  }
  var w = kg;
  final check = checkWeight(w);
  if (check == WeightCheck.zero || check == WeightCheck.negative) {
    w = await askNumber(context, title: l.weightOf(p.name), hint: l.kg);
    if (w == null || checkWeight(w) == WeightCheck.zero || checkWeight(w) == WeightCheck.negative || !context.mounted) return false;
  }
  if (checkWeight(w) == WeightCheck.tooHeavy) {
    final ok = await showDialog<bool>(
      context: context,
      builder: (c) => AlertDialog(
        content: Text(l.weighTooHeavy(kgText(w!))),
        actions: [
          TextButton(onPressed: () => Navigator.pop(c, false), child: Text(l.cancel)),
          FilledButton(onPressed: () => Navigator.pop(c, true), child: Text(l.addToCheck)),
        ],
      ),
    );
    if (ok != true || !context.mounted) return false;
  }
  final q = (w! * 1000).roundToDouble() / 1000;
  await addProduct(context, ref, p, qty: q);
  if (context.mounted) toast(context, weighLineText(context, p, q));
  return true;
}

/// Ручной путь (нет связи / нет ключа ИИ / товар не узнан): выбрать товар и ввести вес.
Future<void> _manual(BuildContext context, WidgetRef ref, {double? kg}) async {
  final p = await pickProduct(context, noServices: true);
  if (p == null || !context.mounted) return;
  if (p.isWeight && kg != null && checkWeight(kg) == WeightCheck.ok) {
    await _addWeighed(context, ref, p, kg);
  } else {
    await addProduct(context, ref, p);
  }
}

/// Продажа «Весы»: камера → товар и вес → в чек (уверенно — сразу, иначе — подтверждение одним касанием).
Future<void> weighForSale(BuildContext context, WidgetRef ref) async {
  final l = context.l;
  if (!ref.read(shiftProvider).isOpen) {
    toast(context, l.openShiftFirst, error: true);
    return;
  }
  // 2026-10-26 (владелец: ИИ — не больше 2 сотрудникам): без права на ИИ — «доступен только …», вес — вручную; с правом — пояснение раз.
  String? denied;
  String? key;
  try {
    key = await ref.read(visionProvider).key();
  } on AiAccessDenied catch (e) {
    denied = e.message;
  }
  if (key == null) {
    if (!context.mounted) return;
    toast(context, denied ?? l.weighNoAi, error: true);
    await _manual(context, ref);
    return;
  }
  if (!context.mounted) return;
  await showAiNoticeOnce(context, ref);
  if (!context.mounted) return;
  final r = await WeighCameraPage.open(context);
  if (r == null || !context.mounted) return;
  if (r.manual) {
    await _manual(context, ref, kg: r.kg);
    return;
  }
  if (r.error != null) {
    toast(context, '${l.weighNoAi}\n${r.error}', error: true);
    await _manual(context, ref, kg: r.kg);
    return;
  }
  final best = r.best;
  // уверенно: товар узнан (эталон или совпадение названия ≥ 0,75), ИИ уверен, вес устойчив и разумен.
  // 2026-10-22 (аудит У-1, У-2, У-3): и первый кандидат заметно лучше второго («Картофель» и «Картофель молодой» —
  // спросить), вес весового товара не больше 30 кг, стоимость на табло (если видна) сходится с ценой × вес.
  final sure = best != null &&
      r.candidates.first.$2 >= 0.75 &&
      (r.exact || clearWinner([for (final c in r.candidates) c.$2])) &&
      r.confidence >= 0.75 &&
      (!best.isWeight || (r.stable && checkWeight(r.kg, maxKg: 30) == WeightCheck.ok && totalAgrees(r.total, best.price, r.kg) != false));
  if (sure) {
    await _addWeighed(context, ref, best, r.kg);
    return;
  }
  await _confirmSheet(context, ref, r);
}

/// Сомнительное распознавание: прочитанный вес (можно поправить) и 2–3 похожих товара — касание кладёт в чек.
Future<void> _confirmSheet(BuildContext context, WidgetRef ref, WeighResult r) async {
  final l = context.l;
  var kg = r.kg;
  await showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    builder: (c) => StatefulBuilder(
      builder: (c, setSheet) {
        final check = checkWeight(kg);
        return SafeArea(
          child: ConstrainedBox(
            constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(c).height * .8),
            child: ListView(
              shrinkWrap: true,
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 16),
              children: [
                Text(l.weighConfirm, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800)),
                const SizedBox(height: 6),
                Text(l.weighRead(r.seenName.isEmpty ? '—' : r.seenName, kg == null ? '—' : kgText(kg!)), style: TextStyle(color: c.pal.textSoft)),
                if (!r.stable && kg != null) Text(l.weighNotStable, style: const TextStyle(color: T.warn, fontWeight: FontWeight.w600)),
                if (check == WeightCheck.zero) Text(l.weighZero, style: const TextStyle(color: T.warn, fontWeight: FontWeight.w600)),
                if (check == WeightCheck.tooHeavy) Text(l.weighTooHeavy(kgText(kg!)), style: const TextStyle(color: T.warn, fontWeight: FontWeight.w600)),
                const SizedBox(height: 6),
                OutlinedButton.icon(
                  onPressed: () async {
                    final v = await askNumber(c, title: l.weighEditKg, hint: l.kg, initial: kg);
                    if (v != null) setSheet(() => kg = v);
                  },
                  icon: const Icon(Icons.scale_rounded),
                  label: Text(l.weighEditKg),
                ),
                if (r.candidates.isEmpty) Padding(padding: const EdgeInsets.only(top: 8), child: Text(l.notInCatalog, style: const TextStyle(color: T.warn))),
                for (final (p, score) in r.candidates.take(3))
                  ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: ProductThumb(name: p.name, url: p.imageUrl, size: 44),
                    title: Text(p.name, style: TextStyle(fontWeight: score >= .5 ? FontWeight.w800 : FontWeight.w500)),
                    subtitle: Text(
                      p.isWeight && kg != null && checkWeight(kg) != WeightCheck.zero
                          ? weighLineText(c, p, kg!)
                          : '${money(p.price)} ${l.som}${p.isWeight ? ' / ${l.kgUnit}' : ''}',
                    ),
                    trailing: const Icon(Icons.add_shopping_cart_rounded),
                    onTap: () async {
                      Navigator.pop(c);
                      await _addWeighed(context, ref, p, kg);
                    },
                  ),
                TextButton.icon(
                  onPressed: () async {
                    Navigator.pop(c);
                    await _manual(context, ref, kg: kg);
                  },
                  icon: const Icon(Icons.search_rounded),
                  label: Text(l.weighOther),
                ),
              ],
            ),
          ),
        );
      },
    ),
  );
}
