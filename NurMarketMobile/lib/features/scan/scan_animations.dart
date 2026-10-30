import 'package:flutter/material.dart';

import '../../core/theme/tokens.dart';

/// Рамка сканера (Т3): жёлтые уголки, бегущая полоса-луч; удачный скан — рамка вспыхивает зелёным и «пружинит»,
/// неизвестный штрихкод — красным с покачиванием.
class ScanFrame extends StatefulWidget {
  final double width;
  final double height;
  const ScanFrame({super.key, this.width = 270, this.height = 170});
  @override
  State<ScanFrame> createState() => ScanFrameState();
}

class ScanFrameState extends State<ScanFrame> with TickerProviderStateMixin {
  late final AnimationController _laser = AnimationController(vsync: this, duration: const Duration(milliseconds: 1700))..repeat(reverse: true);
  late final AnimationController _hit = AnimationController(vsync: this, duration: const Duration(milliseconds: 520));
  Color _hitColor = T.success;
  bool _shake = false;

  void success() => _play(T.success, shake: false);
  void fail() => _play(T.danger, shake: true);

  void _play(Color c, {required bool shake}) {
    setState(() {
      _hitColor = c;
      _shake = shake;
    });
    _hit.forward(from: 0);
  }

  @override
  void dispose() {
    _laser.dispose();
    _hit.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // 2026-10-30 (оптимизация, п. 7): RepaintBoundary — луч перерисовывает только рамку, а не весь экран с камерой (60 к/с)
    return RepaintBoundary(
      child: AnimatedBuilder(
      animation: Listenable.merge([_laser, _hit]),
      builder: (context, _) {
        final h = _hit.value; // 0 → 1
        final glow = h == 0 ? 0.0 : (1 - h); // вспышка гаснет
        final pulse = 1 + 0.07 * Curves.easeOutBack.transform((h < .4 ? h / .4 : (1 - h) / .6).clamp(0, 1));
        final dx = _shake ? 10 * (1 - h) * (h * 28).remainder(2) * ((h * 14).floor().isEven ? 1 : -1) : 0.0;
        final color = Color.lerp(T.accent, _hitColor, glow)!;
        final y = 12 + Curves.easeInOut.transform(_laser.value) * (widget.height - 24);
        return Transform.translate(
          offset: Offset(dx, 0),
          child: Transform.scale(
            scale: pulse,
            child: SizedBox(
              width: widget.width,
              height: widget.height,
              child: Stack(
                children: [
                  // заливка вспышки
                  Positioned.fill(
                    child: DecoratedBox(
                      decoration: BoxDecoration(
                        color: _hitColor.withValues(alpha: .22 * glow),
                        borderRadius: BorderRadius.circular(18),
                      ),
                    ),
                  ),
                  Positioned.fill(
                    child: CustomPaint(painter: _CornersPainter(color: color)),
                  ),
                  // полоса-луч со свечением; при удачном скане замирает по центру
                  // 2026-10-30 (оптимизация, п. 7): сдвиг через Transform.translate — меняется только рисование, без пересчёта компоновки
                  Positioned(
                    left: 18,
                    right: 18,
                    top: 0,
                    child: Transform.translate(
                      offset: Offset(0, glow > 0 ? widget.height / 2 - 1 : y - 1),
                      child: Container(
                      height: 2.5,
                      decoration: BoxDecoration(
                        color: color,
                        borderRadius: BorderRadius.circular(2),
                        boxShadow: [BoxShadow(color: color.withValues(alpha: .7), blurRadius: 12, spreadRadius: 1)],
                      ),
                    ),
                    ),
                  ),
                  if (glow > 0)
                    Center(
                      child: Opacity(
                        opacity: glow,
                        child: Icon(_shake ? Icons.close_rounded : Icons.check_rounded, size: 56 * pulse, color: Colors.white),
                      ),
                    ),
                ],
              ),
            ),
          ),
        );
      },
    ),
    );
  }
}

class _CornersPainter extends CustomPainter {
  final Color color;
  _CornersPainter({required this.color});
  @override
  void paint(Canvas canvas, Size s) {
    final p = Paint()
      ..color = color
      ..strokeWidth = 4
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round;
    const len = 34.0;
    const r = 16.0;
    void corner(Offset o, double sx, double sy) {
      final path = Path()
        ..moveTo(o.dx, o.dy + sy * len)
        ..lineTo(o.dx, o.dy + sy * r)
        ..quadraticBezierTo(o.dx, o.dy, o.dx + sx * r, o.dy)
        ..lineTo(o.dx + sx * len, o.dy);
      canvas.drawPath(path, p);
    }

    corner(const Offset(2, 2), 1, 1);
    corner(Offset(s.width - 2, 2), -1, 1);
    corner(Offset(2, s.height - 2), 1, -1);
    corner(Offset(s.width - 2, s.height - 2), -1, -1);
  }

  @override
  bool shouldRepaint(_CornersPainter old) => old.color != color;
}

/// Всплывающие надписи над рамкой: «+1 Mars» поднимается и тает за 1 с; несколько сканов подряд — столбиком.
class ScanPopups extends StatefulWidget {
  const ScanPopups({super.key});
  @override
  State<ScanPopups> createState() => ScanPopupsState();
}

class ScanPopupsState extends State<ScanPopups> with TickerProviderStateMixin {
  final _items = <(int, String, Color, AnimationController)>[];
  int _seq = 0;

  void add(String text, {Color color = T.success}) {
    final c = AnimationController(vsync: this, duration: const Duration(milliseconds: 1100));
    final id = _seq++;
    setState(() => _items.add((id, text, color, c)));
    c.forward().whenComplete(() {
      if (!mounted) return;
      setState(() => _items.removeWhere((e) => e.$1 == id));
      c.dispose();
    });
  }

  @override
  void dispose() {
    for (final e in _items) {
      e.$4.dispose();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return IgnorePointer(
      child: SizedBox(
        height: 140,
        child: Stack(
          alignment: Alignment.bottomCenter,
          children: [
            for (final (id, text, color, c) in _items)
              AnimatedBuilder(
                key: ValueKey(id),
                animation: c,
                builder: (context, child) {
                  final t = Curves.easeOut.transform(c.value);
                  return Positioned(
                    bottom: 8 + 90 * t,
                    child: Opacity(
                      opacity: (c.value < .15 ? c.value / .15 : 1 - ((c.value - .15) / .85)).clamp(0, 1),
                      child: Transform.scale(scale: .85 + .15 * Curves.easeOutBack.transform((c.value * 3).clamp(0, 1)), child: child),
                    ),
                  );
                },
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
                  decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(20)),
                  child: Text(
                    text,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 14),
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

/// Нажатие с «вдавливанием» — плитки товаров и большие кнопки.
class Pressable extends StatefulWidget {
  final Widget child;
  final VoidCallback? onTap;
  const Pressable({super.key, required this.child, this.onTap});
  @override
  State<Pressable> createState() => _PressableState();
}

class _PressableState extends State<Pressable> {
  bool _down = false;
  @override
  Widget build(BuildContext context) => GestureDetector(
    onTapDown: (_) => setState(() => _down = true),
    onTapCancel: () => setState(() => _down = false),
    onTapUp: (_) => setState(() => _down = false),
    onTap: widget.onTap,
    child: AnimatedScale(scale: _down ? .94 : 1, duration: const Duration(milliseconds: 110), curve: Curves.easeOut, child: widget.child),
  );
}
