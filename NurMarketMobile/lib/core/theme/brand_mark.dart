import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'brand_logo_paths.dart';

/// Вариант цвета знака NUR CRM (брендбук 3.1–3.4).
enum BrandVariant {
  /// На светлом: жёлтые лучи и солнце, чёрная гора.
  light,

  /// На чёрном: белые лучи, жёлтые солнце и гора.
  dark,

  /// На жёлтом: белые лучи, чёрная гора.
  yellow,

  /// Один цвет ([BrandMark.mono]).
  mono,
}

enum BrandLogoKind { logomark, horizontal, vertical, wordmark }

/// Путь из строки M/L/C/Z (формат контуров брендбука, см. brand_logo_paths.dart).
Path parseBrandPath(String d) {
  final path = Path();
  final re = RegExp(r'([MLCZ])|(-?\d+(?:\.\d+)?)');
  final nums = <double>[];
  String? cmd;
  void flush() {
    if (cmd == null) return;
    switch (cmd) {
      case 'M':
        path.moveTo(nums[0], nums[1]);
      case 'L':
        path.lineTo(nums[0], nums[1]);
      case 'C':
        path.cubicTo(nums[0], nums[1], nums[2], nums[3], nums[4], nums[5]);
      case 'Z':
        path.close();
    }
    nums.clear();
  }

  for (final m in re.allMatches(d)) {
    if (m.group(1) != null) {
      flush();
      cmd = m.group(1);
      if (cmd == 'Z') {
        path.close();
        cmd = null;
      }
    } else {
      nums.add(double.parse(m.group(2)!));
    }
  }
  flush();
  return path;
}

final _pathCache = <String, Path>{};
Path _p(String d) => _pathCache.putIfAbsent(d, () => parseBrandPath(d));

/// 2026-10-22, брендбук NUR CRM: точный логотип из векторных контуров PDF (не «на глаз»). Без искажений; минимумы брендбука:
/// знак — 28 px, с текстом — 100 px по ширине. [cyrillic] — вариант «НУР СРМ» (2.3).
class BrandLogo extends StatelessWidget {
  const BrandLogo({super.key, this.width = 120, this.kind = BrandLogoKind.horizontal, this.variant = BrandVariant.light, this.mono, this.cyrillic = false});

  final double width;
  final BrandLogoKind kind;
  final BrandVariant variant;
  final Color? mono;
  final bool cyrillic;

  @override
  Widget build(BuildContext context) {
    final lay = kind == BrandLogoKind.vertical
        ? (cyrillic ? brandVerticalCyr : brandVerticalLat)
        : (cyrillic ? brandHorizontalCyr : brandHorizontalLat);
    final box = switch (kind) {
      BrandLogoKind.logomark => const [0.0, 0.0, 100.0, 100.0],
      BrandLogoKind.wordmark => lay.textBox,
      _ => lay.box,
    };
    final w = math.max(kind == BrandLogoKind.logomark ? 28.0 : 100.0, width);
    final h = w * (box[3] - box[1]) / (box[2] - box[0]);
    return SizedBox(width: w, height: h, child: CustomPaint(painter: BrandLogoPainter(lay, kind, variant, mono, box)));
  }
}

class BrandLogoPainter extends CustomPainter {
  BrandLogoPainter(this.lay, this.kind, this.variant, this.mono, this.box);

  final BrandLogoLayout lay;
  final BrandLogoKind kind;
  final BrandVariant variant;
  final Color? mono;
  final List<double> box;

  @override
  void paint(Canvas canvas, Size size) {
    late Color sun, mountain, text;
    switch (variant) {
      case BrandVariant.dark:
        sun = Colors.white;
        mountain = BrandMark.goldenLight;
        text = Colors.white;
      case BrandVariant.yellow:
        sun = Colors.white;
        mountain = BrandMark.deepNight;
        text = BrandMark.deepNight;
      case BrandVariant.mono:
        sun = mountain = text = mono ?? BrandMark.deepNight;
      case BrandVariant.light:
        sun = BrandMark.goldenLight;
        mountain = BrandMark.deepNight;
        text = BrandMark.deepNight;
    }
    final k = math.min(size.width / (box[2] - box[0]), size.height / (box[3] - box[1]));
    canvas.save();
    canvas.clipRect(Offset.zero & size);
    canvas.scale(k);
    canvas.translate(-box[0], -box[1]);
    if (kind != BrandLogoKind.wordmark) {
      canvas.drawPath(_p(lay.sun), Paint()..color = sun);
      canvas.drawPath(_p(lay.mountain), Paint()..color = mountain);
    }
    if (kind != BrandLogoKind.logomark) {
      for (final t in lay.text) {
        canvas.drawPath(_p(t), Paint()..color = text);
      }
    }
    canvas.restore();
  }

  @override
  bool shouldRepaint(BrandLogoPainter old) => old.variant != variant || old.kind != kind || old.mono != mono;
}

/// Только знак (логомарк): квадрат, не меньше 28 px.
class BrandMark extends StatelessWidget {
  const BrandMark({super.key, this.size = 64, this.variant = BrandVariant.light, this.mono});

  final double size;
  final BrandVariant variant;
  final Color? mono;

  static const goldenLight = Color(0xFFFFB208);
  static const deepNight = Color(0xFF191919);

  @override
  Widget build(BuildContext context) => BrandLogo(width: size, kind: BrandLogoKind.logomark, variant: variant, mono: mono);
}

/// Фирменный паттерн лучей (брендбук 6.1) как мягкий фон пустых состояний: сетка «звёзд», рисуется кодом.
/// Цвет и прозрачность — [color] (6–8 % — «минималистично, без перегруза»).
class BrandPattern extends StatelessWidget {
  const BrandPattern({super.key, required this.color, this.cell = 56});

  final Color color;
  final double cell;

  @override
  Widget build(BuildContext context) => IgnorePointer(child: CustomPaint(painter: _PatternPainter(color, cell), size: Size.infinite));
}

class _PatternPainter extends CustomPainter {
  _PatternPainter(this.color, this.cell);

  final Color color;
  final double cell;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.clipRect(Offset.zero & size);
    final paint = Paint()
      ..color = color
      ..strokeWidth = math.max(1, cell * 0.05);
    for (var y = 0.0; y < size.height; y += cell) {
      for (var x = 0.0; x < size.width; x += cell) {
        final c = Offset(x + cell / 2, y + cell / 2);
        for (var i = 0; i < 16; i++) {
          final a = i * 2 * math.pi / 16;
          canvas.drawLine(c + Offset(math.cos(a), math.sin(a)) * (cell * 0.16), c + Offset(math.cos(a), math.sin(a)) * (cell * 0.42), paint);
        }
      }
    }
  }

  @override
  bool shouldRepaint(_PatternPainter old) => old.color != color || old.cell != cell;
}
