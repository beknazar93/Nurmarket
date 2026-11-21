import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/server_capabilities.dart';
import '../providers.dart';
import 'mode_profile.dart';
import 'plan_info.dart';

/// 2026-11-20 (тариф «Мини», E1): провайдеры режима. Профиль считается при событиях (смена плана, роли, переключателя), а не на каждый кадр.

/// Переключатель «Простой режим (для базара)» в настройках приложения — по умолчанию выключен. Хранится в настройках телефона
/// (`mode.simple`, схема базы не меняется); стирается при выходе вместе с остальными настройками аккаунта.
class SimpleModeController extends Notifier<bool> {
  static const key = 'mode.simple';

  @override
  bool build() => false;

  /// Прочитать сохранённое значение (main.dart ждёт его до первого кадра, чтобы не мигала «обычная» оболочка).
  Future<void> load() async {
    try {
      final v = (await ref.read(databaseProvider).getSetting(key)) == '1';
      if (v != state) state = v;
    } catch (_) {
      // настройка недоступна — остаётся «как сегодня»
    }
  }

  Future<void> set(bool v) async {
    state = v;
    await ref.read(databaseProvider).setSetting(key, v ? '1' : '0');
  }
}

final simpleModeProvider = NotifierProvider<SimpleModeController, bool>(SimpleModeController.new);

/// Текущий тариф (из `ServerCapabilities.planNotifier`: ответ сервера или кэш).
class PlanInfoController extends Notifier<PlanInfo> {
  @override
  PlanInfo build() {
    final n = ServerCapabilities.planNotifier;
    void on() => state = n.value;
    n.addListener(on);
    ref.onDispose(() => n.removeListener(on));
    return n.value;
  }
}

final planInfoProvider = NotifierProvider<PlanInfoController, PlanInfo>(PlanInfoController.new);

/// Профиль режима для всех экранов. Сфера берётся из режима магазина (`Prefs.shop`), признаки — из `capabilities`.
final modeProfileProvider = Provider<ModeProfile>((ref) {
  final plan = ref.watch(planInfoProvider);
  final manual = ref.watch(simpleModeProvider);
  final role = ref.watch(sessionProvider.select((s) => s?.role ?? ''));
  final isOwner = ref.watch(sessionProvider.select((s) => s?.isOwner ?? false));
  final prefs = ref.watch(prefsProvider.select((p) => p.shopChosen ? p.shop.name : ''));
  return ModeProfile.resolve(sphere: prefs, plan: plan, role: isOwner ? 'owner' : role, features: ServerCapabilities.featureSet, simpleManual: manual);
});
