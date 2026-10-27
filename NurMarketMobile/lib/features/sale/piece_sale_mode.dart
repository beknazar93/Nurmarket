import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/db/database.dart';
import '../../core/providers.dart';

/// 2026-10-27 (ревью М-6): переключатель «Продавать поштучно из упаковки» (настройка телефона `pack_checkout_mode`).
/// Штука из пачки уходит в `pos/checkout` полем `sale_package_id` — касса его сама ещё не шлёт, и подтверждения, что боевой сервер
/// его понимает, нет: если нет, 3 сигареты спишут 3 пачки. Старого «многошагового» пути в мобилке нет, поэтому безопасный запасной
/// вариант один — выключить поштучную продажу: товар продаётся только целой упаковкой (как раньше).
///   'pieces' (по умолчанию, текущее поведение) — выбор «Пачка / Штука» включён;
///   'off' — выбор не показывается, продаётся упаковка целиком.
/// Включено по умолчанию, потому что владелец уже пользуется поштучной продажей; после продажи штуки остаток сверяется с сервером
/// ([SalesRepository.verifyPackStock]), расхождение видно жёлтой плашкой на экране «Оплачено» и в журнале.
class PieceSaleMode {
  static const setting = 'pack_checkout_mode';
  static const on = 'pieces';
  static const off = 'off';

  static Future<bool> isOn(AppDatabase db) async {
    try {
      return (await db.getSetting(setting)) != off;
    } catch (_) {
      return true;
    }
  }

  static Future<void> set(AppDatabase db, bool value) => db.setSetting(setting, value ? on : off);
}

/// Состояние переключателя для экрана настроек (читается из базы при первом обращении).
class PieceSaleController extends Notifier<bool> {
  @override
  bool build() {
    Future.microtask(() async {
      final v = await PieceSaleMode.isOn(ref.read(databaseProvider));
      if (v != state) state = v;
    });
    return true;
  }

  Future<void> setOn(bool v) async {
    state = v;
    await PieceSaleMode.set(ref.read(databaseProvider), v);
  }
}

final pieceSaleProvider = NotifierProvider<PieceSaleController, bool>(PieceSaleController.new);
