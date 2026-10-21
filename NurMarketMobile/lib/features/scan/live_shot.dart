import 'dart:async';
import 'dart:typed_data';

import 'package:camera/camera.dart';
import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';
import '../../core/util/ui.dart';

/// 14.10, владелец: «умная камера в виде сканера, а не фото». Видоискатель во весь экран, как у сканера штрихкодов:
/// рамка с уголками и бегущая линия; кадр берётся прямо с видоискателя (без системной камеры) — кнопкой, касанием
/// экрана или сам («Авто»: через 2 с после открытия, когда телефон держат на товаре). Возвращает JPEG.
class LiveShotPage extends StatefulWidget {
  final String title;
  final String hint;
  const LiveShotPage({super.key, required this.title, required this.hint});

  static Future<Uint8List?> open(BuildContext context, {required String title, required String hint}) => Navigator.of(context).push<Uint8List>(
    MaterialPageRoute(
      builder: (_) => LiveShotPage(title: title, hint: hint),
    ),
  );

  @override
  State<LiveShotPage> createState() => _LiveShotPageState();
}

class _LiveShotPageState extends State<LiveShotPage> with TickerProviderStateMixin, WidgetsBindingObserver {
  CameraController? _camera;
  String? _error;
  bool _taking = false;
  bool _auto = true;
  bool _torch = false;
  late final AnimationController _line = AnimationController(vsync: this, duration: const Duration(milliseconds: 1600))..repeat(reverse: true);
  late final AnimationController _countdown = AnimationController(vsync: this, duration: const Duration(milliseconds: 2200));

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _countdown.addStatusListener((s) {
      if (s == AnimationStatus.completed && _auto) _take();
    });
    _start();
  }

  Future<void> _start() async {
    try {
      final cams = await availableCameras();
      final back = cams.firstWhere((c) => c.lensDirection == CameraLensDirection.back, orElse: () => cams.first);
      final c = CameraController(back, ResolutionPreset.veryHigh, enableAudio: false, imageFormatGroup: ImageFormatGroup.jpeg);
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
      if (_auto) _countdown.forward(from: 0);
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
    } else if (state == AppLifecycleState.resumed && _camera == null) {
      _start();
    }
  }

  Future<void> _take() async {
    final c = _camera;
    if (c == null || _taking || !c.value.isInitialized) return;
    setState(() => _taking = true);
    _countdown.stop();
    try {
      final file = await c.takePicture();
      final bytes = await file.readAsBytes();
      if (mounted) Navigator.of(context).pop(bytes);
    } catch (e) {
      if (mounted) {
        toast(context, '$e', error: true);
        setState(() => _taking = false);
      }
    }
  }

  Future<void> _focusAndTake(TapUpDetails d, BoxConstraints box) async {
    final c = _camera;
    if (c == null) return;
    try {
      await c.setFocusPoint(Offset(d.localPosition.dx / box.maxWidth, d.localPosition.dy / box.maxHeight));
    } catch (_) {
      // фокус по точке не у всех камер
    }
    await _take();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _line.dispose();
    _countdown.dispose();
    _camera?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final c = _camera;
    return Scaffold(
      backgroundColor: Colors.black,
      body: LayoutBuilder(
        builder: (context, box) {
          final frame = Rect.fromCenter(center: Offset(box.maxWidth / 2, box.maxHeight * .44), width: box.maxWidth * .84, height: box.maxHeight * .46);
          return Stack(
            fit: StackFit.expand,
            children: [
              if (c != null && c.value.isInitialized)
                GestureDetector(
                  onTapUp: (d) => _focusAndTake(d, box),
                  child: FittedBox(
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
              // затемнение вокруг рамки, уголки и бегущая линия — как у сканера штрихкодов
              IgnorePointer(
                child: AnimatedBuilder(
                  animation: _line,
                  builder: (_, _) => CustomPaint(painter: _FramePainter(frame, _line.value, _taking)),
                ),
              ),
              SafeArea(
                child: Padding(
                  padding: const EdgeInsets.fromLTRB(12, 8, 12, 0),
                  child: Row(
                    children: [
                      IconButton.filledTonal(onPressed: () => Navigator.of(context).pop(), icon: const Icon(Icons.arrow_back_rounded)),
                      const SizedBox(width: 10),
                      Expanded(
                        child: Text(
                          widget.title,
                          style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 18),
                        ),
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
                left: 0,
                right: 0,
                top: frame.bottom + 14,
                child: Text(
                  _taking ? l.recognizing : widget.hint,
                  textAlign: TextAlign.center,
                  style: const TextStyle(color: Colors.white, fontSize: 14, fontWeight: FontWeight.w600),
                ),
              ),
              Positioned(
                left: 0,
                right: 0,
                bottom: 28 + MediaQuery.paddingOf(context).bottom,
                child: Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    SizedBox(
                      width: 96,
                      child: FilterChip(
                        label: Text(l.liveAuto),
                        selected: _auto,
                        onSelected: (v) {
                          setState(() => _auto = v);
                          if (v && c != null) {
                            _countdown.forward(from: 0);
                          } else {
                            _countdown.stop();
                          }
                        },
                      ),
                    ),
                    const SizedBox(width: 18),
                    GestureDetector(
                      onTap: _take,
                      child: AnimatedBuilder(
                        animation: _countdown,
                        builder: (_, _) => SizedBox(
                          width: 84,
                          height: 84,
                          child: Stack(
                            alignment: Alignment.center,
                            children: [
                              SizedBox(
                                width: 84,
                                height: 84,
                                child: CircularProgressIndicator(
                                  value: _auto && !_taking ? _countdown.value : (_taking ? null : 0),
                                  strokeWidth: 5,
                                  color: T.accent,
                                  backgroundColor: Colors.white24,
                                ),
                              ),
                              Container(
                                width: 66,
                                height: 66,
                                decoration: const BoxDecoration(color: T.accent, shape: BoxShape.circle),
                                child: Icon(_taking ? Icons.hourglass_top_rounded : Icons.center_focus_strong_rounded, color: T.navy, size: 32),
                              ),
                            ],
                          ),
                        ),
                      ),
                    ),
                    const SizedBox(width: 114),
                  ],
                ),
              ),
            ],
          );
        },
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
    if (!busy) {
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
