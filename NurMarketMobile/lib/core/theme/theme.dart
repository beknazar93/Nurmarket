import 'package:flutter/cupertino.dart' show CupertinoPageTransitionsBuilder;
import 'package:flutter/material.dart';

import 'tokens.dart';

/// Тема приложения: Montserrat, жёлтая главная кнопка с тёмно-синим текстом, карточки 16, кнопки 12.
ThemeData buildTheme({required bool dark}) {
  final p = Palette(dark);
  final scheme = ColorScheme(
    brightness: dark ? Brightness.dark : Brightness.light,
    primary: T.accent,
    onPrimary: T.navy,
    secondary: T.navy,
    onSecondary: Colors.white,
    error: T.danger,
    onError: Colors.white,
    surface: p.panel,
    onSurface: p.text,
  );
  final base = ThemeData(useMaterial3: true, colorScheme: scheme, fontFamily: 'Montserrat');
  // 2026-10-21, владелец: «жёлтый текст на белом плохо читается — сделать тёмным во всём приложении». Material 3 красит
  // цветом primary (жёлтый, контраст с белым ≈ 1,4:1) ТЕКСТ текстовых кнопок («Отмена», «ОК» в календаре), подпись
  // активного поля, вкладку, выбранную строку, значки чипов, сегодняшнюю дату и курсор. В светлой теме это теперь цвет
  // основного текста; жёлтый остаётся фоном главных кнопок, индикаторами (галочка, переключатель, полоса, вкладка) и
  // рамкой в тёмной теме. В тёмной теме жёлтый текст на тёмно-синем — контраст ≈ 11:1, его оставляем.
  final ink = dark ? T.accent : p.text;
  return base.copyWith(
    scaffoldBackgroundColor: p.window,
    textButtonTheme: TextButtonThemeData(
      style: TextButton.styleFrom(
        foregroundColor: ink,
        textStyle: const TextStyle(fontFamily: 'Montserrat', fontSize: 14, fontWeight: FontWeight.w700),
      ),
    ),
    textSelectionTheme: TextSelectionThemeData(
      cursorColor: dark ? T.accent : T.navy,
      selectionColor: T.accent.withValues(alpha: dark ? .35 : .45),
      selectionHandleColor: dark ? T.accent : T.navy,
    ),
    tabBarTheme: TabBarThemeData(labelColor: ink, unselectedLabelColor: p.textSoft, indicatorColor: T.accent),
    listTileTheme: ListTileThemeData(selectedColor: ink),
    expansionTileTheme: ExpansionTileThemeData(iconColor: ink, textColor: p.text, collapsedIconColor: p.textSoft),
    datePickerTheme: DatePickerThemeData(
      todayForegroundColor: WidgetStateProperty.resolveWith(
        (s) => s.contains(WidgetState.selected) ? T.navy : (s.contains(WidgetState.disabled) ? p.textSoft : ink),
      ),
      todayBorder: const BorderSide(color: T.accent, width: 1.5),
    ),
    timePickerTheme: TimePickerThemeData(
      dayPeriodTextColor: WidgetStateColor.resolveWith((s) => s.contains(WidgetState.selected) ? ink : p.textSoft),
    ),
    pageTransitionsTheme: const PageTransitionsTheme(
      builders: {TargetPlatform.android: ZoomPageTransitionsBuilder(), TargetPlatform.iOS: CupertinoPageTransitionsBuilder()},
    ),
    appBarTheme: AppBarTheme(
      backgroundColor: p.window,
      foregroundColor: p.text,
      elevation: 0,
      scrolledUnderElevation: 0,
      titleTextStyle: TextStyle(fontFamily: 'Montserrat', fontSize: 20, fontWeight: FontWeight.w800, color: p.text),
    ),
    cardTheme: CardThemeData(
      color: p.panel,
      elevation: 0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(T.radiusCard),
        side: BorderSide(color: p.border),
      ),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        backgroundColor: T.accent,
        foregroundColor: T.navy,
        // 13.10, владелец (снимок тёмной темы): неактивная «Оплатить» светилась кремовым пятном — в тёмной теме тёмная.
        disabledBackgroundColor: dark ? T.darkBorder : T.accentSoft,
        disabledForegroundColor: dark ? T.darkTextSoft : T.navy.withValues(alpha: .45),
        minimumSize: const Size.fromHeight(T.ctaHeight),
        textStyle: const TextStyle(fontFamily: 'Montserrat', fontSize: 16, fontWeight: FontWeight.w800),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(T.radiusCta)),
      ),
    ),
    outlinedButtonTheme: OutlinedButtonThemeData(
      style: OutlinedButton.styleFrom(
        foregroundColor: p.text,
        backgroundColor: p.panel,
        minimumSize: const Size(0, T.fieldHeight),
        side: BorderSide(color: dark ? T.darkBorder : T.borderStrong, width: 1.5),
        textStyle: const TextStyle(fontFamily: 'Montserrat', fontSize: 14, fontWeight: FontWeight.w700),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(T.radiusButton)),
      ),
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: p.panel,
      isDense: true,
      contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
      hintStyle: TextStyle(color: p.textSoft, fontWeight: FontWeight.w400),
      // 2026-10-21: подпись активного поля была жёлтой на белом — теперь тёмная (ошибка — красная, как раньше).
      floatingLabelStyle: WidgetStateTextStyle.resolveWith((s) {
        if (s.contains(WidgetState.disabled)) return TextStyle(color: p.textSoft);
        if (s.contains(WidgetState.error)) return const TextStyle(color: T.danger);
        if (s.contains(WidgetState.focused)) return TextStyle(color: ink, fontWeight: FontWeight.w600);
        return TextStyle(color: p.text);
      }),
      border: OutlineInputBorder(
        borderRadius: BorderRadius.circular(T.radiusButton),
        borderSide: BorderSide(color: p.border),
      ),
      enabledBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(T.radiusButton),
        borderSide: BorderSide(color: p.border),
      ),
      focusedBorder: OutlineInputBorder(
        borderRadius: BorderRadius.circular(T.radiusButton),
        borderSide: BorderSide(color: dark ? T.accent : T.navy, width: 2),
      ),
    ),
    chipTheme: ChipThemeData(
      backgroundColor: p.panel,
      selectedColor: T.navy,
      // 14.10, владелец (снимок «Одежда»): у выбранных размеров текст сливался с тёмно-синим фоном — у выбранного чипа белый.
      labelStyle: TextStyle(
        fontFamily: 'Montserrat',
        fontWeight: FontWeight.w600,
        fontSize: 13,
        color: WidgetStateColor.resolveWith((s) => s.contains(WidgetState.selected) ? Colors.white : p.text),
      ),
      checkmarkColor: Colors.white,
      // 2026-10-21: значки чипов («+ Добавить товар», сканер) были жёлтыми на белом — тёмные.
      iconTheme: IconThemeData(color: ink, size: 18),
      secondaryLabelStyle: const TextStyle(fontFamily: 'Montserrat', fontWeight: FontWeight.w700, fontSize: 13, color: Colors.white),
      side: BorderSide(color: p.border),
      shape: const StadiumBorder(),
      showCheckmark: false,
    ),
    navigationBarTheme: NavigationBarThemeData(
      backgroundColor: p.panel,
      indicatorColor: T.accent,
      height: 68,
      labelTextStyle: WidgetStateProperty.resolveWith(
        (s) =>
            TextStyle(fontFamily: 'Montserrat', fontSize: 12, fontWeight: s.contains(WidgetState.selected) ? FontWeight.w800 : FontWeight.w500, color: p.text),
      ),
      iconTheme: WidgetStateProperty.resolveWith((s) => IconThemeData(color: s.contains(WidgetState.selected) ? T.navy : p.textSoft)),
    ),
    dividerTheme: DividerThemeData(color: p.border, thickness: 1, space: 1),
    // 13.10, владелец: «на тёмной теме не видна надпись, которая высвечивается снизу» — текст уведомления всегда белый,
    // а в тёмной теме подложка светлее фона, чтобы уведомление не сливалось с экраном.
    snackBarTheme: SnackBarThemeData(
      behavior: SnackBarBehavior.floating,
      backgroundColor: dark ? const Color(0xFF24435A) : T.navy,
      contentTextStyle: const TextStyle(color: Colors.white, fontFamily: 'Montserrat', fontSize: 14, fontWeight: FontWeight.w600),
      actionTextColor: T.accent,
    ),
  );
}
