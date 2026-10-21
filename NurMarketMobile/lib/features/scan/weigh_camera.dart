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
import '../sale/sale_screen.dart';
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
  const WeighResult({this.seenName = '', this.candidates = const [], this.kg, this.stable = false, this.confidence = 0, this.error, this.noKey = false});

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
  static const _maxShots = 10;
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

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _start();
  }

  Future<void> _start() async {
    try {
      final cams = await availableCameras();
      final back = cams.firstWhere((c) => c.lensDirection == CameraLensDirection.back, orElse: () => cams.first);
      // high (≈720p) — цифры табло читаются, а кадр уходит в ИИ быстро
      final c = CameraController(back, ResolutionPreset.high, enableAudio: false, imageFormatGroup: ImageFormatGroup.jpeg);
      await c.initialize();
      try {
        await c.setFocusMode(FocusMode.auto);
      } catch (_) {
        // не все камеры умеют
      }
      if (!mounted) {
        await c.dispose();
        return;
      }
      setState(() => _camera = c);
      await Future<void>.delayed(const Duration(milliseconds: 1200)); // кассир наводит камеру
      if (mounted) _run();
    } catch (e) {
      if (mounted) setState(() => _error = '$e');
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    final c = _camera;
    if (state == AppLifecycleState.inactive && c != null) {
      _camera = null;
      c.dispose();
    } else if (state == AppLifecycleState.resumed && _camera == null && !_closed) {
      _start();
    }
  }

  @override
  void dispose() {
    _closed = true;
    WidgetsBinding.instance.removeObserver(this);
    _camera?.dispose();
    super.dispose();
  }

  void _finish(WeighResult r) {
    if (_closed || !mounted) return;
    _closed = true;
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
      final ids = {for (final e in exact) e.candidates.first.$1.id};
      return [for (final e in exact) e.candidates.first, ..._candidates.where((c) => !ids.contains(c.$1.id))];
    } catch (_) {
      return _candidates;
    }
  }

  Future<void> _run() async {
    if (_running) return;
    _running = true;
    final l = context.l;
    final vision = ref.read(visionProvider);
    final db = ref.read(databaseProvider);
    // весовые товары магазина — ИИ называет товар так же, как в каталоге
    final names = [for (final p in await db.searchProducts('', limit: 3000)) if (p.isWeight) p.name];
    try {
      for (var shot = 0; shot < _maxShots && !_closed; shot++) {
        if (mounted) setState(() => _status = _seen.isEmpty ? l.weighReading : l.weighWaiting);
        final jpeg = await _snap();
        if (jpeg == null || _closed) return;
        final first = _seen.isEmpty;
        final r = await vision.readScale(jpeg, withProduct: first, names: names);
        if (_closed) return;
        if (first && r.product.isNotEmpty) {
          _seen = r.product;
          _confidence = r.confidence;
          await _matchProduct(r.product, jpeg);
          // штучный товар — вес не нужен, сразу дальше
          final best = _candidates.isNotEmpty && _candidates.first.$2 >= 0.5 ? _candidates.first.$1 : null;
          if (best != null && !best.isWeight) {
            _finish(WeighResult(seenName: _seen, candidates: await _withExact(), stable: true, confidence: _confidence));
            return;
          }
        } else if (!first && r.confidence > 0) {
          _confidence = (_confidence + r.confidence) / 2;
        }
        final stable = _stab.add(r.weightKg);
        if (mounted) {
          setState(() {
            _lastKg = r.weightKg ?? _lastKg;
            _status = !r.hasWeight ? l.weighNoDisplay : (r.isZero ? l.weighZero : l.weighWaiting);
          });
        }
        if (stable != null && _seen.isNotEmpty) {
          _finish(WeighResult(seenName: _seen, candidates: await _withExact(), kg: stable, stable: true, confidence: _confidence));
          return;
        }
        await Future<void>.delayed(const Duration(milliseconds: 700));
      }
      // вес так и не установился — кассир проверит прочитанное
      _finish(WeighResult(seenName: _seen, candidates: await _withExact(), kg: _lastKg, confidence: _confidence));
    } on VisionException catch (e) {
      _finish(WeighResult(seenName: _seen, candidates: _candidates, kg: _lastKg, error: e.noKey ? l.aiKeyNeeded : e.message, noKey: e.noKey));
    } catch (e) {
      _finish(WeighResult(seenName: _seen, candidates: _candidates, kg: _lastKg, error: '$e'));
    } finally {
      _running = false;
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final c = _camera;
    return Scaffold(
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
                      child: Text(_error!, style: const TextStyle(color: Colors.white70)),
                    )
                  : const CircularProgressIndicator(color: T.accent),
            ),
          SafeArea(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
              child: Row(
                children: [
                  IconButton.filledTonal(onPressed: () => Navigator.of(context).pop(), icon: const Icon(Icons.arrow_back_rounded)),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(l.weighTitle, style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 18)),
                  ),
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
          ),
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
                    _status.isEmpty ? l.weighHint : _status,
                    textAlign: TextAlign.center,
                    style: const TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.w600),
                  ),
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
                ],
              ),
            ),
          ),
        ],
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
  if (await ref.read(visionProvider).key() == null) {
    if (!context.mounted) return;
    toast(context, l.weighNoAi, error: true);
    await _manual(context, ref);
    return;
  }
  if (!context.mounted) return;
  final r = await WeighCameraPage.open(context);
  if (r == null || !context.mounted) return;
  if (r.error != null) {
    toast(context, '${l.weighNoAi}\n${r.error}', error: true);
    await _manual(context, ref, kg: r.kg);
    return;
  }
  final best = r.best;
  // уверенно: товар узнан (эталон или совпадение названия ≥ 0,75), ИИ уверен, вес устойчив и разумен
  final sure = best != null &&
      r.candidates.first.$2 >= 0.75 &&
      r.confidence >= 0.75 &&
      (!best.isWeight || (r.stable && checkWeight(r.kg) == WeightCheck.ok));
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
