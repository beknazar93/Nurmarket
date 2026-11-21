import 'plan_info.dart';

/// 2026-11-20 (тариф «Мини», этап E1; ТЗ «Мобилка — многофункциональная», раздел 3): профиль режима приложения — ОДНА точка, откуда
/// экраны узнают, что показывать. Чистая функция [ModeProfile.resolve]: сфера × тариф × роль × признаки сервера × экран.
/// Правила безопасности: право даёт сервер, клиент только прячет; продажу, очередь и введённое не блокируем никогда;
/// нет плана / незнакомый план = «как сегодня» (ничего не прячем и ничего нового не открываем).

/// Крупные «семьи» экранов (по адресу маршрута, см. [ModeScreen.ofRoute]).
enum ModeScreen { home, sale, receive, totals, more, scan, checks, stock, admin, rentals, ai, other }

/// Вкладки нижней панели.
enum ModeTab { home, sale, scan, checks, stock, receive, totals, more }

enum ScreenAccess { open, hidden }

/// Откуда взят режим: «как сегодня», переключатель в настройках или тариф.
enum ModeSource { today, manual, plan }

extension ModeScreenX on ModeScreen {
  /// Адрес → семья экранов. `/admin/new-product` и `/admin/product` в Простом режиме остаются открытыми (быстрая карточка, цена) —
  /// это решает [ModeProfile.hidesRoute], а не эта функция.
  static ModeScreen ofRoute(String loc) {
    final p = loc.split('?').first;
    if (p == '/home') return ModeScreen.home;
    if (p == '/sale') return ModeScreen.sale;
    if (p == '/in') return ModeScreen.receive;
    if (p == '/totals') return ModeScreen.totals;
    if (p == '/more') return ModeScreen.more;
    if (p == '/scan') return ModeScreen.scan;
    if (p == '/checks' || p.startsWith('/checks/')) return ModeScreen.checks;
    if (p == '/stock' || p.startsWith('/stock/')) return ModeScreen.stock;
    if (p == '/admin' || p.startsWith('/admin/')) return ModeScreen.admin;
    if (p == '/rentals' || p.startsWith('/rentals/')) return ModeScreen.rentals;
    if (p == '/ai' || p.startsWith('/ai/')) return ModeScreen.ai;
    return ModeScreen.other;
  }
}

class ModeProfile {
  /// Простой режим («Мини»): 3 вкладки + «Ещё», всё остальное скрыто.
  final bool simple;
  final ModeSource source;
  final PlanInfo plan;

  /// Сфера магазина как строка (grocery / clothing / services / hardware…); пусто — не известна.
  final String sphere;
  final bool isAdmin;
  final Set<String> features;
  final List<ModeTab> tabs;

  /// Доступ к экрану, который спрашивали в [resolve] (`screen`).
  final ScreenAccess access;

  /// Незнакомый код плана: режим «как сегодня».
  final bool unknownPlan;

  const ModeProfile._({
    required this.simple,
    required this.source,
    required this.plan,
    required this.sphere,
    required this.isAdmin,
    required this.features,
    required this.tabs,
    required this.access,
    required this.unknownPlan,
  });

  static const todayTabs = [ModeTab.home, ModeTab.sale, ModeTab.scan, ModeTab.checks, ModeTab.stock];
  static const simpleTabs = [ModeTab.sale, ModeTab.receive, ModeTab.totals, ModeTab.more];

  /// Единственная точка решения. [simpleManual] — переключатель «Простой режим (для базара)» в настройках (по умолчанию выключен).
  static ModeProfile resolve({String? sphere, PlanInfo? plan, String role = '', Set<String> features = const {}, ModeScreen screen = ModeScreen.other, bool simpleManual = false}) {
    final p = plan ?? PlanInfo.none;
    final planSimple = p.present && p.isMini;
    final unknown = p.present && p.code.isNotEmpty && !p.isKnown;
    final simple = planSimple || simpleManual;
    final r = role.toLowerCase();
    final admin = r.contains('owner') || r.contains('admin') || r.contains('владел') || r.contains('админ');
    final prof = ModeProfile._(
      simple: simple,
      source: planSimple ? ModeSource.plan : (simpleManual ? ModeSource.manual : ModeSource.today),
      plan: p,
      sphere: (sphere ?? '').trim().toLowerCase(),
      isAdmin: admin,
      features: features,
      tabs: simple ? simpleTabs : todayTabs,
      access: ScreenAccess.open,
      unknownPlan: unknown,
    );
    return prof._withAccess(prof.accessTo(screen));
  }

  ModeProfile _withAccess(ScreenAccess a) =>
      ModeProfile._(simple: simple, source: source, plan: plan, sphere: sphere, isAdmin: isAdmin, features: features, tabs: tabs, access: a, unknownPlan: unknownPlan);

  /// Куда вести на «домой».
  String get homeRoute => simple ? '/sale' : '/home';

  bool has(String feature) => features.contains(feature);

  /// Мини: сотрудников нет (max_staff = 1 — только владелец), «Добавить сотрудника» не показываем.
  bool get staffHidden => simple && plan.present && plan.limits.maxStaff != null && plan.limits.maxStaff! <= 1;

  /// Простой режим по переключателю или тарифу «Мини» не даёт выйти в «обычный» (переключатель работает только вручную).
  bool get canLeaveSimple => simple && source == ModeSource.manual;

  ScreenAccess accessTo(ModeScreen s) {
    if (simple) {
      return switch (s) {
        ModeScreen.home || ModeScreen.admin || ModeScreen.rentals || ModeScreen.ai => ScreenAccess.hidden,
        _ => ScreenAccess.open,
      };
    }
    // обычный режим: три вкладки Простого режима («Приход», «Итоги», «Ещё») не существуют
    return switch (s) {
      ModeScreen.receive || ModeScreen.totals || ModeScreen.more => ScreenAccess.hidden,
      _ => ScreenAccess.open,
    };
  }

  /// Закрыт ли адрес в этом режиме (для редиректа роутера). В Простом режиме остаются открытыми быстрая карточка товара и цена.
  bool hidesRoute(String loc) {
    final path = loc.split('?').first;
    if (simple) {
      if (path == '/admin/new-product' || path == '/admin/product') return false;
      if (path == '/stock/receive' || path == '/stock/audit' || path == '/stock/import' || path == '/sizes' || path == '/search' || path == '/tasks/my') return true;
    }
    return accessTo(ModeScreenX.ofRoute(path)) == ScreenAccess.hidden;
  }
}
