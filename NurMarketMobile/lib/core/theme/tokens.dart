import 'package:flutter/material.dart';

/// Токены темы NurMarket (ТЗ мобильного приложения, раздел 2; доска «Элементы интерфейса»).
class T {
  static const window = Color(0xFFF4F5F7);
  static const panel = Color(0xFFFFFFFF);
  static const soft = Color(0xFFF9FAFB);
  static const border = Color(0xFFE5E7EB);
  static const borderStrong = Color(0xFFD0D5DD);
  static const text = Color(0xFF111827);
  static const textSoft = Color(0xFF667085);
  static const accent = Color(0xFFFFD600);
  static const accentSoft = Color(0xFFFFF7CC);
  static const navy = Color(0xFF0B2A3C);
  static const success = Color(0xFF0E7A3A);
  static const successSoft = Color(0xFFE7F6EC);
  static const danger = Color(0xFFC0362C);
  static const dangerSoft = Color(0xFFFDECEA);
  static const warn = Color(0xFFB54708);
  static const warnSoft = Color(0xFFFEF3E2);

  // Тёмная тема
  static const darkWindow = Color(0xFF0A1A24);
  static const darkPanel = Color(0xFF0F2230);
  static const darkBorder = Color(0xFF1E3647);
  static const darkText = Color(0xFFEAF0F4);
  static const darkTextSoft = Color(0xFF9DB0BE);

  static const radiusCard = 16.0;
  static const radiusButton = 12.0;
  static const radiusCta = 16.0;
  static const ctaHeight = 56.0;
  static const fieldHeight = 48.0;
}

/// Цвета, которые зависят от светлой или тёмной темы.
class Palette {
  final bool dark;
  const Palette(this.dark);

  Color get window => dark ? T.darkWindow : T.window;
  Color get panel => dark ? T.darkPanel : T.panel;
  Color get border => dark ? T.darkBorder : T.border;
  Color get text => dark ? T.darkText : T.text;
  Color get textSoft => dark ? T.darkTextSoft : T.textSoft;
  Color get price => dark ? T.accent : T.navy;

  /// 13.10, владелец: «кнопка плюс сливается с фоном» — главные кнопки (+, сканер) в тёмной теме жёлтые с тёмным значком.
  Color get action => dark ? T.accent : T.navy;
  Color get onAction => dark ? T.navy : Colors.white;

  /// 13.10, владелец (снимок тёмной темы: «текст просроченного не отображается»): светлые подложки предупреждений
  /// в тёмной теме — приглушённые, иначе светлый текст на них не читается.
  Color get dangerSoft => dark ? const Color(0x40F04438) : T.dangerSoft;
  Color get warnSoft => dark ? const Color(0x40F79009) : T.warnSoft;
  Color get successSoft => dark ? const Color(0x4012B76A) : T.successSoft;
  Color get accentSoft => dark ? const Color(0x33FFD600) : T.accentSoft;

  static Palette of(BuildContext context) => Palette(Theme.of(context).brightness == Brightness.dark);
}
