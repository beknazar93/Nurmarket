import 'dart:io' show Platform;

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/services.dart';

/// 2026-10-22 (мобилка, админ): П6 — «бип» сканера, как у ручного сканера на ПК. Звук играет Android (ToneGenerator в
/// MainActivity.kt, канал kg.nurcrm.nurmarket/beep) — без новых зависимостей. Системный «щелчок» (SystemSound.click)
/// молчит, если в телефоне выключены звуки касаний, поэтому кассир не слышал, что код считан.
/// Не Android (тесты, ПК) — тихо ничего. Ошибки канала не мешают продаже.
class Beep {
  static const _channel = MethodChannel('kg.nurcrm.nurmarket/beep');

  /// Выключатель (на будущее — настройка «Звук сканера»).
  static bool enabled = true;

  /// Не чаще одного звука за 80 мс: непрерывный скан не превращается в треск.
  static DateTime _last = DateTime(2000);

  /// Код считан, товар найден.
  static Future<void> ok() => _play('ok');

  /// Код считан, но товар не найден / ошибка.
  static Future<void> error() => _play('error');

  static Future<void> _play(String kind) async {
    if (!enabled || kIsWeb || !Platform.isAndroid) return;
    final now = DateTime.now();
    if (now.difference(_last).inMilliseconds < 80) return;
    _last = now;
    try {
      await _channel.invokeMethod<void>('beep', {'kind': kind});
    } catch (_) {
      // нет канала (старая сборка) или звук занят — без звука
    }
  }
}
