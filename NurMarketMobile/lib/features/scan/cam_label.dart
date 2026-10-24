import 'package:flutter/material.dart';

// 2026-10-22 (подпись «Умная камера»): подписи поверх живого превью камеры. Раньше это был белый текст без подложки —
// на светлом кадре (стол, стена, бумага) он сливался с фоном и «Умной камеры» не было видно. Теперь тёмная «пилюля»
// (чёрный 68 %) с белым текстом: контраст не зависит от кадра и от темы приложения.
const camShade = Color(0xAD000000);

class CamLabel extends StatelessWidget {
  final String text;
  final IconData? icon;
  final double size;
  final FontWeight weight;
  final int maxLines;
  final TextAlign align;
  final bool dim;
  const CamLabel(
    this.text, {
    super.key,
    this.icon,
    this.size = 16,
    this.weight = FontWeight.w800,
    this.maxLines = 2,
    this.align = TextAlign.start,
    this.dim = false,
  });

  @override
  Widget build(BuildContext context) => Container(
    constraints: const BoxConstraints(minHeight: 48),
    padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
    decoration: BoxDecoration(color: camShade, borderRadius: BorderRadius.circular(24)),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        if (icon != null) ...[Icon(icon, size: 20, color: Colors.white), const SizedBox(width: 8)],
        Flexible(
          child: Text(
            text,
            maxLines: maxLines,
            overflow: TextOverflow.ellipsis,
            textAlign: align,
            style: TextStyle(color: dim ? Colors.white70 : Colors.white, fontSize: size, fontWeight: weight),
          ),
        ),
      ],
    ),
  );
}
