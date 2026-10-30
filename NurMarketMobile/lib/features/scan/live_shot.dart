import 'dart:async';

import 'package:camera/camera.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';
import 'cam_label.dart';
import 'shot_flow.dart';

/// 2026-10-22, владелец: «умная камера при обучении фоткает только 1 фото и потом чёрный экран». Причина: плагин камеры
/// на Android (camera_android_camerax) — одна общая камера на всё приложение: у него один Preview и один
/// ProcessCameraProvider. Обучение открывало новую страницу видоискателя на каждый снимок, и новая камера включалась,
/// пока старая страница ещё уезжала с экрана; когда старая страница закрывалась, её dispose() освобождал общий Preview
/// и делал unbindAll() — превью новой страницы гасло (чёрный экран). Так же сканер штрихкодов включался раньше, чем
/// умная камера отпускала камеру. Теперь камера одна за раз: следующий открывает её только после того, как прежний
/// контроллер освобождён ([CameraGate]), а снимки обучения делаются на одной странице с одной камерой.
class CameraGate {
  static final _active = <CameraController>{};
  static Future<void> _disposing = Future<void>.value();
  static Completer<void>? _freed;
  static CameraDescription? _back;
  // 2026-10-26 (видеозвонок): передняя камера запоминается рядом с задней; null — её нет (или список ещё не читали).
  static CameraDescription? _front;
  static bool _lensesRead = false;
  static int _lensCount = 0;

  /// Дождаться, пока камеру никто не держит (не дольше [timeout] — чтобы зависшая камера не заперла приложение).
  static Future<void> idle({Duration timeout = const Duration(seconds: 3)}) async {
    final end = DateTime.now().add(timeout);
    while (_active.isNotEmpty) {
      final left = end.difference(DateTime.now());
      if (left <= Duration.zero) break;
      final f = _freed ??= Completer<void>();
      try {
        await f.future.timeout(left);
      } on TimeoutException {
        break;
      }
    }
    try {
      await _disposing.timeout(timeout);
    } on TimeoutException {
      // освобождение затянулось — дальше всё равно пробуем открыть
    }
  }

  /// Задняя камера; список камер запоминаем — availableCameras() при каждом открытии стоил лишние 100–300 мс.
  static Future<CameraDescription> _backCamera() async {
    final known = _back;
    if (known != null) return known;
    final cams = await _readLenses();
    if (cams.isEmpty) throw CameraException('noCamera', 'no camera');
    return _back = cams.firstWhere((c) => c.lensDirection == CameraLensDirection.back, orElse: () => cams.first);
  }

  static Future<List<CameraDescription>> _readLenses() async {
    final cams = await availableCameras();
    _lensesRead = true;
    _lensCount = cams.length;
    _front = cams.cast<CameraDescription?>().firstWhere((c) => c!.lensDirection == CameraLensDirection.front, orElse: () => null);
    return cams;
  }

  /// 2026-10-26 (видеозвонок): камера нужной стороны. Задняя — как раньше; передней нет — noCamera (молча на заднюю не уходим).
  static Future<CameraDescription> _camera(CameraLensDirection lens) async {
    if (lens != CameraLensDirection.front) return _backCamera();
    if (!_lensesRead) await _readLenses();
    final f = _front;
    if (f == null) throw CameraException('noCamera', 'no front camera');
    return f;
  }

  /// 2026-10-26 (видеозвонок): есть ли камера этой стороны (для кнопки «Другая камера»). Нет камер вообще — false.
  static Future<bool> hasLens(CameraLensDirection lens) async {
    try {
      if (!_lensesRead) await _readLenses();
      return lens == CameraLensDirection.front ? _front != null : _lensCount > 0;
    } catch (_) {
      return false;
    }
  }

  /// 2026-10-22 (скорость скана): узнать камеры заранее (сканер штрихкодов вызывает при открытии) — умная камера
  /// потом не тратит на это 100–300 мс.
  static Future<void> warmUp() async {
    try {
      await _backCamera();
    } catch (_) {}
  }

  /// 2026-10-26 (видеозвонок): [lens] — сторона камеры; по умолчанию задняя, остальные экраны вызывают как раньше.
  static Future<CameraController> open(ResolutionPreset preset, {CameraLensDirection lens = CameraLensDirection.back}) async {
    await idle();
    final c = CameraController(await _camera(lens), preset, enableAudio: false, imageFormatGroup: ImageFormatGroup.jpeg);
    _active.add(c);
    try {
      await c.initialize();
    } catch (_) {
      release(c);
      rethrow;
    }
    try {
      await c.setFocusMode(FocusMode.auto);
    } catch (_) {
      // не все камеры умеют
    }
    return c;
  }

  /// Отпустить камеру (повторный вызов ничего не делает). Освобождение идёт по очереди, следующий ждёт в [idle].
  static void release(CameraController c) {
    if (!_active.remove(c)) return;
    _disposing = _disposing.then((_) => c.dispose()).onError((_, _) {});
    if (_active.isEmpty) {
      final f = _freed;
      _freed = null;
      f?.complete();
    }
  }
}

/// Результат распознавания под видоискателем (продажа «По виду»); null — убрать.
typedef ShowPanel = void Function(WidgetBuilder? panel);

/// 14.10, владелец: «умная камера в виде сканера, а не фото». Видоискатель во весь экран, как у сканера штрихкодов:
/// рамка с уголками и бегущая линия; кадр берётся прямо с видоискателя. Возвращает JPEG.
/// 2026-10-22, владелец: «умный сканер при нажатии сам сразу сканирует — добавь кнопку сканера». Самоснимок через
/// 2 секунды («Авто») и снимок касанием экрана убраны: открывается превью, снимок — только крупной кнопкой
/// «Сканировать» (касание экрана — фокус). Три вида страницы:
/// [open] — один кадр и назад (полка, накладная, этикетки);
/// [openMany] — обучение: несколько кадров подряд на одной камере, «Фото 2 из 5», «Переснять», «Готово»;
/// [openLive] — продажа: страница остаётся открытой, кадр за кадром, результат — панелью над кнопкой.
/// Непрерывный режим (удержание) не делаем: каждый кадр — запрос к ИИ (1–3 с и платный лимит), а один и тот же товар
/// добавлялся бы в чек несколько раз; повторное нажатие «Сканировать» быстрее и понятнее.
class LiveShotPage extends StatefulWidget {
  final String title;
  final String hint;
  final int maxShots;
  final int minShots;

  /// Подписи ракурсов для каждого снимка обучения (справа, слева, сверху…).
  final List<String> steps;
  final ResolutionPreset preset;

  /// 2026-10-22, владелец: «после обучения сканер тупо ничего не говорит». Шаги всего пути наверху
  /// («1 Фото → 2 Штрихкод → 3 Готово»; эта страница — первый) и строка «что будет дальше» под подсказкой.
  final List<String> stages;
  final String? next;

  /// Продажа: что делать с кадром, пока страница открыта (распознать и показать список).
  final Future<void> Function(Uint8List jpeg, ShowPanel show, Stopwatch sincePress)? onShot;

  const LiveShotPage({
    super.key,
    required this.title,
    required this.hint,
    this.maxShots = 1,
    this.minShots = 1,
    this.steps = const [],
    this.preset = ResolutionPreset.high, // 2026-10-30 (оптимизация, п. 5): было veryHigh (до 1–3 МБ на снимок); для ИИ хватает 1280×720
    this.onShot,
    this.stages = const [],
    this.next,
  });

  static Future<Uint8List?> open(BuildContext context, {required String title, required String hint, String? next, ResolutionPreset preset = ResolutionPreset.high}) async {
    final r = await Navigator.of(context).push<Object?>(
      MaterialPageRoute(
        builder: (_) => LiveShotPage(title: title, hint: hint, next: next, preset: preset),
      ),
    );
    return r is Uint8List ? r : null;
  }

  /// Обучение: до [max] снимков подряд, «Готово» — с [min] снимков. null — назад без сохранения.
  static Future<List<Uint8List>?> openMany(
    BuildContext context, {
    required String title,
    required String hint,
    required List<String> steps,
    int min = 1,
    int max = 5,
    List<String> stages = const [],
    String? next,
  }) async {
    final r = await Navigator.of(context).push<Object?>(
      MaterialPageRoute(
        // 2026-10-22 (аудит У-4): 640×480 — снимок и есть лёгкий эталон (JPEG 40–80 КБ, без перекодирования)
        builder: (_) => LiveShotPage(title: title, hint: hint, steps: steps, minShots: min, maxShots: max, preset: ResolutionPreset.medium, stages: stages, next: next),
      ),
    );
    return r is List<Uint8List> ? r : null;
  }

  /// Продажа «По виду»: камера открыта, пока кассир не закроет страницу.
  static Future<void> openLive(BuildContext context, {required String title, required String hint, String? next, required Future<void> Function(Uint8List, ShowPanel, Stopwatch) onShot}) =>
      Navigator.of(context).push<Object?>(
        MaterialPageRoute(
          // 720p вместо 1080p: кадр в 2–3 раза легче — быстрее снимок и отправка в ИИ, товар узнаётся так же
          builder: (_) => LiveShotPage(title: title, hint: hint, next: next, maxShots: 0, preset: ResolutionPreset.high, onShot: onShot),
        ),
      );

  @override
  State<LiveShotPage> createState() => _LiveShotPageState();
}

class _LiveShotPageState extends State<LiveShotPage> with TickerProviderStateMixin, WidgetsBindingObserver {
  CameraController? _camera;
  String? _error;
  bool _torch = false;
  bool _starting = false;
  bool _closing = false;

  /// Последний кадр — виден, пока страница закрывается (камера уже отпущена).
  Uint8List? _frozen;
  WidgetBuilder? _panel;
  late final ShotFlow _flow = ShotFlow(maxShots: widget.maxShots, minShots: widget.minShots, keep: widget.maxShots > 1);
  // 2026-10-22 (аудит У-12): бегущая линия — только пока идёт скан (раньше крутилась всё время и тратила батарею)
  late final AnimationController _line = AnimationController(vsync: this, duration: const Duration(milliseconds: 1600));

  void _syncLine() {
    if (_flow.busy && !_line.isAnimating) {
      _line.repeat(reverse: true);
    } else if (!_flow.busy && _line.isAnimating) {
      _line.stop();
    }
  }

  @override
  void setState(VoidCallback fn) {
    super.setState(fn);
    _syncLine();
  }
  late final AnimationController _flash = AnimationController(vsync: this, duration: const Duration(milliseconds: 260));

  bool get _many => widget.maxShots > 1;
  bool get _live => widget.onShot != null;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _start();
  }

  Future<void> _start() async {
    if (_starting || _closing) return;
    _starting = true;
    try {
      final c = await CameraGate.open(widget.preset);
      if (!mounted || _closing) {
        CameraGate.release(c);
        return;
      }
      if (_torch) {
        try {
          await c.setFlashMode(FlashMode.torch);
        } catch (_) {}
      }
      setState(() {
        _camera = c;
        _error = null;
      });
    } catch (e) {
      // 2026-10-22 (аудит У-9): понятный текст вместо сырого исключения камеры
      if (mounted) setState(() => _error = context.l.cameraUnavailable);
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
    // 2026-10-22: при первом запуске окно «Разрешить камеру» тоже делает приложение неактивным — пока камера
    // открывается (_starting), вторую не запускаем: два контроллера на одну камеру гасили превью.
    switch (state) {
      case AppLifecycleState.inactive:
      case AppLifecycleState.paused:
      case AppLifecycleState.hidden:
        if (_camera != null && !_flow.busy) {
          _stopCamera();
          if (mounted) setState(() {});
        }
      case AppLifecycleState.resumed:
        if (_camera == null && !_starting && !_closing) _start();
      case AppLifecycleState.detached:
        break;
    }
  }

  /// Закрыть страницу: камеру отпускаем сразу, до анимации ухода, — следующий экран (сканер, обучение) получит её целой.
  void _close([Object? result]) {
    if (_closing) return;
    _closing = true;
    _stopCamera();
    if (mounted) {
      setState(() {});
      Navigator.of(context).pop(result);
    }
  }

  Future<void> _take() async {
    final c = _camera;
    if (c == null || !c.value.isInitialized || c.value.isTakingPicture || !_flow.begin()) return;
    final sincePress = Stopwatch()..start();
    setState(() {});
    HapticFeedback.lightImpact();
    Uint8List bytes;
    try {
      final file = await c.takePicture();
      bytes = await file.readAsBytes();
    } catch (e) {
      _flow.failed();
      if (mounted) {
        toast(context, context.l.cameraShotFailed, error: true);
        setState(() {});
      }
      return;
    }
    if (!mounted) return;
    _flash.forward(from: 0);
    if (!_many && !_live) {
      _frozen = bytes;
      _close(bytes);
      return;
    }
    _flow.taken(bytes, waitResult: _live);
    if (_many) {
      setState(() {});
      if (_flow.full) {
        _frozen = bytes;
        _close(List<Uint8List>.of(_flow.shots));
      }
      return;
    }
    // продажа: камера остаётся включённой, пока ИИ узнаёт товар
    setState(() {});
    var shown = false;
    try {
      await widget.onShot!(bytes, (b) {
        shown = b != null;
        if (mounted && !_closing) setState(() => _panel = b);
      }, sincePress);
    } catch (e) {
      if (mounted) toast(context, '$e', error: true);
    }
    if (!mounted) return;
    setState(() => _flow.showResult(hasResult: shown));
  }

  Future<void> _focus(TapUpDetails d, BoxConstraints box) async {
    final c = _camera;
    if (c == null) return;
    try {
      await c.setFocusPoint(Offset(d.localPosition.dx / box.maxWidth, d.localPosition.dy / box.maxHeight));
    } catch (_) {
      // фокус по точке не у всех камер
    }
  }

  @override
  void dispose() {
    _closing = true;
    WidgetsBinding.instance.removeObserver(this);
    _line.dispose();
    _flash.dispose();
    _stopCamera();
    super.dispose();
  }

  String _title(BuildContext context) {
    if (!_many) return widget.title;
    final n = _flow.current;
    final step = n <= widget.steps.length ? widget.steps[n - 1] : '';
    return [context.l.teachPhotoOf(n, widget.maxShots), if (step.isNotEmpty) step].join(' · ');
  }

  String _hint(BuildContext context) {
    final l = context.l;
    if (_flow.busy) return l.scanBusy;
    if (_live && _flow.phase == ShotPhase.result) return l.scanNext;
    if (_many && _flow.shots.length >= 3 && _flow.canFinish) return l.teachCanFinish;
    return widget.hint;
  }

  @override
  Widget build(BuildContext context) {
    final c = _camera;
    final frozen = _frozen;
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _close();
      },
      child: Scaffold(
        backgroundColor: Colors.black,
        body: LayoutBuilder(
          builder: (context, box) {
            final frame = Rect.fromCenter(
              center: Offset(box.maxWidth / 2, box.maxHeight * (_panel != null ? .3 : .42)),
              width: box.maxWidth * .84,
              height: box.maxHeight * (_panel != null ? .3 : .42),
            );
            return Stack(
              fit: StackFit.expand,
              children: [
                if (c != null && c.value.isInitialized)
                  GestureDetector(
                    onTapUp: (d) => _focus(d, box),
                    child: FittedBox(
                      fit: BoxFit.cover,
                      child: SizedBox(
                        width: c.value.previewSize?.height ?? box.maxWidth,
                        height: c.value.previewSize?.width ?? box.maxHeight,
                        child: CameraPreview(c),
                      ),
                    ),
                  )
                else if (frozen != null)
                  Image.memory(frozen, fit: BoxFit.cover, gaplessPlayback: true)
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
                                FilledButton.icon(onPressed: _start, icon: const Icon(Icons.refresh_rounded), label: Text(context.l.retry)),
                              ],
                            ),
                          )
                        : const CircularProgressIndicator(color: T.accent),
                  ),
                // затемнение вокруг рамки, уголки и бегущая линия — как у сканера штрихкодов
                IgnorePointer(
                  child: AnimatedBuilder(
                    animation: _line,
                    builder: (_, _) => CustomPaint(painter: _FramePainter(frame, _line.value, _flow.busy)),
                  ),
                ),
                // вспышка — кадр сделан
                IgnorePointer(
                  child: AnimatedBuilder(
                    animation: _flash,
                    builder: (_, _) => ColoredBox(color: Colors.white.withValues(alpha: _flash.isAnimating ? (1 - _flash.value) * .55 : 0)),
                  ),
                ),
                // 2026-10-22: верхняя панель — сверху (в Stack с fit: expand она растягивалась и вставала посреди экрана)
                Align(alignment: Alignment.topCenter, child: SafeArea(
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Row(
                          children: [
                            IconButton.filledTonal(onPressed: () => _close(), icon: const Icon(Icons.arrow_back_rounded)),
                            const SizedBox(width: 10),
                            // 2026-10-22 (подпись «Умная камера»): тёмная пилюля с иконкой вместо белого текста прямо на кадре
                            Expanded(child: Align(alignment: Alignment.centerLeft, child: CamLabel(_title(context), icon: Icons.photo_camera_rounded))),
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
                        if (widget.stages.isNotEmpty)
                          Padding(padding: const EdgeInsets.only(top: 8), child: ScanSteps(labels: widget.stages, current: 0)),
                      ],
                    ),
                  ),
                )),
                Positioned(
                  left: 16,
                  right: 16,
                  top: frame.bottom + 12,
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      // 2026-10-22 (подпись «Умная камера»): подсказка на тёмной подложке, а не тень на светлом кадре
                      CamLabel(_hint(context), size: 15, weight: FontWeight.w700, align: TextAlign.center, maxLines: 3),
                      // 2026-10-22: что будет после снимка — пока ничего не снято и нет списка
                      if (widget.next != null && _flow.phase == ShotPhase.aim && _panel == null && _flow.shots.isEmpty) ...[
                        const SizedBox(height: 4),
                        CamLabel(widget.next!, size: 14, weight: FontWeight.w600, align: TextAlign.center, maxLines: 3, dim: true),
                      ],
                    ],
                  ),
                ),
                Positioned(left: 0, right: 0, bottom: 0, child: _bottom(context)),
              ],
            );
          },
        ),
      ),
    );
  }

  Widget _bottom(BuildContext context) {
    final l = context.l;
    final panel = _panel;
    final ready = _camera != null && !_closing;
    return SafeArea(
      top: false,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 20),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (panel != null)
              Container(
                constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * .36),
                margin: const EdgeInsets.only(bottom: 12),
                decoration: BoxDecoration(color: context.pal.panel, borderRadius: BorderRadius.circular(20)),
                child: Stack(
                  children: [
                    Builder(builder: panel),
                    Positioned(
                      right: 2,
                      top: 2,
                      child: IconButton(icon: const Icon(Icons.close_rounded), onPressed: () => setState(() => _panel = null)),
                    ),
                  ],
                ),
              ),
            if (_many && _flow.shots.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: SizedBox(
                  height: 56,
                  child: Row(
                    children: [
                      for (final s in _flow.shots)
                        Padding(
                          padding: const EdgeInsets.only(right: 8),
                          child: ClipRRect(
                            borderRadius: BorderRadius.circular(10),
                            child: Image.memory(s, width: 56, height: 56, fit: BoxFit.cover, cacheWidth: 112, gaplessPlayback: true),
                          ),
                        ),
                      const Spacer(),
                      TextButton.icon(
                        style: TextButton.styleFrom(foregroundColor: Colors.white),
                        onPressed: _flow.busy ? null : () => setState(_flow.removeLast),
                        icon: const Icon(Icons.undo_rounded),
                        label: Text(l.teachRetake),
                      ),
                    ],
                  ),
                ),
              ),
            Row(
              children: [
                Expanded(
                  child: FilledButton.icon(
                    style: FilledButton.styleFrom(
                      backgroundColor: T.accent,
                      foregroundColor: T.navy,
                      disabledBackgroundColor: T.accent.withValues(alpha: .55),
                      disabledForegroundColor: T.navy,
                      minimumSize: const Size.fromHeight(64),
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(32)),
                      textStyle: const TextStyle(fontSize: 18, fontWeight: FontWeight.w800),
                    ),
                    onPressed: ready && _flow.canShoot ? _take : null,
                    icon: _flow.busy
                        ? const SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 3, color: T.navy))
                        : const Icon(Icons.center_focus_strong_rounded, size: 28),
                    label: Text(_flow.busy ? l.scanBusy : l.scanShoot),
                  ),
                ),
                if (_many && _flow.canFinish) ...[
                  const SizedBox(width: 10),
                  FilledButton(
                    style: FilledButton.styleFrom(
                      backgroundColor: Colors.white,
                      foregroundColor: T.navy,
                      minimumSize: const Size(0, 64),
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(32)),
                      textStyle: const TextStyle(fontSize: 16, fontWeight: FontWeight.w800),
                    ),
                    onPressed: () => _close(List<Uint8List>.of(_flow.shots)),
                    child: Text('${l.done} (${_flow.shots.length})'),
                  ),
                ],
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// 2026-10-22, владелец: «после обучения сканер тупо ничего не говорит». Шаги пути камеры наверху:
/// «1 Фото → 2 Штрихкод → 3 Готово» — сделанные зелёные с ✓, текущий жёлтый.
class ScanSteps extends StatelessWidget {
  final List<String> labels;

  /// Номер текущего шага с 0; все до него — сделаны.
  final int current;
  const ScanSteps({super.key, required this.labels, required this.current});

  @override
  Widget build(BuildContext context) => Row(
    mainAxisAlignment: MainAxisAlignment.center,
    children: [
      for (var i = 0; i < labels.length; i++) ...[
        if (i > 0) const Padding(padding: EdgeInsets.symmetric(horizontal: 3), child: Icon(Icons.arrow_forward_rounded, size: 16, color: Colors.white70)),
        Flexible(child: _step(i)),
      ],
    ],
  );

  Widget _step(int i) {
    final done = i < current;
    final active = i == current;
    final fg = active ? T.navy : Colors.white;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(color: active ? T.accent : (done ? T.success : const Color(0xAD000000)), borderRadius: BorderRadius.circular(20)),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(done ? '✓' : '${i + 1}', style: TextStyle(color: fg, fontWeight: FontWeight.w900, fontSize: 14)),
          const SizedBox(width: 5),
          Flexible(
            child: Text(labels[i], maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(color: fg, fontWeight: FontWeight.w700, fontSize: 14)),
          ),
        ],
      ),
    );
  }
}

class _FramePainter extends CustomPainter {
  final Rect frame;
  final double t;
  final bool busy;
  _FramePainter(this.frame, this.t, this.busy);

  @override
  void paint(Canvas canvas, Size size) {
    final r = RRect.fromRectAndRadius(frame, const Radius.circular(22));
    canvas.drawPath(Path.combine(PathOperation.difference, Path()..addRect(Offset.zero & size), Path()..addRRect(r)), Paint()..color = const Color(0x99000000));
    final corner = Paint()
      ..color = T.accent
      ..strokeWidth = 5
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round;
    const k = 34.0;
    final f = frame;
    for (final (p, dx, dy) in [(f.topLeft, 1.0, 1.0), (f.topRight, -1.0, 1.0), (f.bottomLeft, 1.0, -1.0), (f.bottomRight, -1.0, -1.0)]) {
      canvas.drawLine(p, p + Offset(k * dx, 0), corner);
      canvas.drawLine(p, p + Offset(0, k * dy), corner);
    }
    if (busy) {
      final y = f.top + 12 + (f.height - 24) * t;
      canvas.drawLine(
        Offset(f.left + 18, y),
        Offset(f.right - 18, y),
        Paint()
          ..strokeWidth = 2.5
          ..shader = LinearGradient(colors: [T.accent.withValues(alpha: 0), T.accent, T.accent.withValues(alpha: 0)])
              .createShader(Rect.fromLTWH(f.left, y - 2, f.width, 4)),
      );
    }
  }

  @override
  bool shouldRepaint(_FramePainter old) => old.t != t || old.busy != busy || old.frame != frame;
}
