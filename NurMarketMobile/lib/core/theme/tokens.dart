import 'package:flutter/material.dart';

/// Токены темы NurMarket (ТЗ мобильного приложения, раздел 2; доска «Элементы интерфейса»).
/// 2026-10-22, брендбук NUR CRM: Golden Light #FFB208 (акцент), Deep Night #191919 (чёрный; имя `navy` оставлено, чтобы не править
/// десятки экранов — теперь это тёмно-серый брендбука), серая шкала #3D3D3D … #EEE. Текст на жёлтом — только `navy` (9,7:1);
/// белый на жёлтом — 1,8:1, жёлтый текст на белом запрещён. Оранжевый Orange Flame #F77B14 — [brandOrange]; Wood Brown — [brandBrown].
class T {
  static const window = Color(0xFFF4F4F4);
  static const panel = Color(0xFFFFFFFF);
  static const soft = Color(0xFFF9FAFB);
  static const border = Color(0xFFE2E2E2);
  static const borderStrong = Color(0xFFCCCCCC);
  static const text = Color(0xFF191919);
  static const textSoft = Color(0xFF666666);
  static const accent = Color(0xFFFFB208);
  static const accentSoft = Color(0xFFFFF1DB);
  static const navy = Color(0xFF191919);
  static const brandOrange = Color(0xFFF77B14);
  static const brandYellow2 = Color(0xFFF9D919);
  static const brandBrown = Color(0xFF7C460B);
  static const success = Color(0xFF0E7A3A);
  static const successSoft = Color(0xFFE7F6EC);
  static const danger = Color(0xFFC0362C);
  static const dangerSoft = Color(0xFFFDECEA);
  static const warn = Color(0xFFB54708);
  static const warnSoft = Color(0xFFFEF3E2);

  // Тёмная тема
  static const darkWindow = Color(0xFF191919);
  static const darkPanel = Color(0xFF242424);
  static const darkBorder = Color(0xFF3D3D3D);
  static const darkText = Color(0xFFF5F5F5);
  static const darkTextSoft = Color(0xFFAAAAAA);

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
  Color get accentSoft => dark ? const Color(0x33FFB208) : T.accentSoft;

  static Palette of(BuildContext context) => Palette(Theme.of(context).brightness == Brightness.dark);
}
