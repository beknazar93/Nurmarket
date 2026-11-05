import 'dart:async';
import 'dart:io' show Platform;

import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:flutter/services.dart';
import 'package:sunmi_printer_plus/sunmi_printer_plus.dart';

import 'escpos.dart';
import 'printer_service.dart';

/// 2026-11-05 (волна 0-D, М5-05; разрешено владельцем, пакет sunmi_printer_plus, BSD-3): встроенный принтер Sunmi (V2, T2…).
/// Печать идёт средствами самого Sunmi (все буквы, в том числе кыргызские ң ө ү и ş ğ), а не байтами ESC/POS.
///
/// Защита (проверить на устройстве нельзя — пишем осторожно):
///  • путь работает только если Build.MANUFACTURER — SUNMI (Kotlin MainActivity, метод `manufacturer`);
///  • включается отдельной настройкой «Встроенный принтер Sunmi» ([PrinterConfig.sunmi], по умолчанию выключена);
///  • любая ошибка плагина ловится и превращается в [PrinterException], не в падение приложения;
///  • обычные принтеры (Bluetooth, сеть, USB) идут прежним кодом и этих функций не трогают.
class SunmiBuiltIn {
  SunmiBuiltIn._();

  static const _channel = MethodChannel('kg.nurcrm.nurmarket/printer');
  static bool? _isSunmi;

  /// Только для тестов: подменяет определение производителя.
  static Future<bool> Function()? isSunmiOverride;

  /// Только для тестов: вместо вызова плагина (строки чека, импульс ящика).
  static Future<void> Function(List<PrintLine> lines, {String? qr, bool drawer})? printOverride;

  /// Только для тестов: сбросить запомненный ответ.
  static void resetForTests() {
    _isSunmi = null;
    isSunmiOverride = null;
    printOverride = null;
  }

  /// Производитель телефона — SUNMI (регистр не важен). Не Android, ошибка канала или таймаут — false.
  static Future<bool> isSunmi() async {
    final o = isSunmiOverride;
    if (o != null) return o();
    final cached = _isSunmi;
    if (cached != null) return cached;
    if (!Platform.isAndroid) return _isSunmi = false;
    try {
      final m = await _channel.invokeMethod<String>('manufacturer').timeout(const Duration(seconds: 3));
      return _isSunmi = (m ?? '').toLowerCase().contains('sunmi');
    } catch (_) {
      return false; // не запоминаем: следующий раз спросим снова
    }
  }

  /// Строки чека → принтер Sunmi. [qr] — адрес для QR под чеком; [drawer] — открыть ящик после печати.
  static Future<void> printLines(List<PrintLine> lines, {String? qr, bool drawer = false, int fontSize = 24}) async {
    final o = printOverride;
    if (o != null) return o(lines, qr: qr, drawer: drawer);
    var started = false;
    try {
      // 2026-11-05 (ревью волн 0–3, М-9/М-11): плагин при сбое SDK отвечает строкой «error», а не исключением — проверяем ответ каждой команды,
      // иначе кассир видел «напечатано», хотя ничего не вышло. «Начали» ставим после первой принятой команды: сбой на первой строке
      // (принтер не найден) — ничего не вышло, повтор безопасен.
      for (final l in lines) {
        final r = await SunmiPrinter.printText(
          l.text,
          style: SunmiTextStyle(fontSize: l.big ? fontSize + 8 : fontSize, bold: l.bold, align: l.center ? SunmiPrintAlign.CENTER : SunmiPrintAlign.LEFT),
        );
        started = true;
        checkReply(r);
      }
      if (qr != null && qr.isNotEmpty) checkReply(await SunmiPrinter.printQRCode(qr));
      checkReply(await SunmiPrinter.lineWrap(3));
    } catch (e) {
      // часть строк могла уже выйти — повтор напечатал бы чек второй раз, поэтому sent = true, если начали
      throw PrinterException(PrinterProblem.io, '$e', started);
    }
    if (drawer) await openDrawer();
  }

  /// Ответ команды плагина: «ok»/«OK»/null — принято; «error» и текст ошибки — сбой. Выбрасывает [StateError] (его ловит вызывающий).
  @visibleForTesting
  static void checkReply(Object? reply) {
    if (reply == null || reply == true) return;
    final s = '$reply'.trim().toLowerCase();
    if (s.isEmpty || s == 'ok') return;
    throw StateError('Sunmi: $reply');
  }

  /// Импульс денежного ящика через принтер Sunmi.
  static Future<void> openDrawer() async {
    final o = printOverride;
    if (o != null) return o(const [], drawer: true);
    try {
      checkReply(await SunmiDrawer.openDrawer());
    } catch (e) {
      throw PrinterException(PrinterProblem.io, '$e');
    }
  }
}
