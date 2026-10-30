import 'dart:async' show unawaited;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show SystemUiOverlayStyle;
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'core/api/api_client.dart' show ApiClient, ApiText;
import 'core/api/session_store.dart';
import 'core/sync/background_sync.dart';
import 'core/util/access.dart';
import 'core/util/ui.dart' show statusBarStyle;
import 'core/providers.dart';
import 'core/theme/theme.dart';
import 'features/admin/admin_more.dart';
import 'features/payment/payment_qr.dart';
import 'features/payment/debt_access.dart';
import 'features/admin/advisor.dart';
import 'features/admin/ai_access_ui.dart';
import 'features/admin/analytics.dart';
import 'features/admin/admin_receive.dart';
import 'features/admin/invoice_import.dart';
import 'features/admin/admin_repository.dart';
import 'features/admin/admin_screens.dart';
import 'features/admin/new_item.dart';
import 'features/admin/quotes.dart';
import 'features/auth/login_screen.dart';
import 'features/checks/checks_screen.dart';
import 'features/home/home_screen.dart';
import 'features/home/search_screen.dart';
import 'features/home/shell.dart';
import 'features/ops/ops_screens.dart';
import 'features/payment/payment_screen.dart';
import 'features/profile/profile_screen.dart';
import 'features/rentals/rentals.dart';
import 'features/sale/sale_screen.dart';
import 'features/scan/scan_screen.dart';
import 'features/scan/smart_camera.dart';
import 'features/shift/close_shift_screen.dart';
import 'features/stock/bulk_import.dart';
import 'features/stock/stock_screens.dart';
import 'l10n/app_localizations.dart';
import 'core/util/audit.dart';
import 'features/admin/audit_screen.dart';
import 'core/ai/assistant/read_tools.dart' show Reader;
import 'features/assistant/ai_log_screen.dart';
import 'features/assistant/assistant_settings.dart';
import 'features/assistant/call_controller.dart';
import 'features/assistant/call_overlay.dart';
import 'features/staff/salary_screen.dart';
import 'features/staff/tasks_screen.dart';
import 'features/tester/tester_screen.dart';

/// Маршруты (ТЗ, раздел 3: go_router — переходы и по ссылкам из пуш-уведомлений).
/// Этап 1 — экраны кассира; экраны администратора (Т8–Т14, Т20–Т26) — этап 2.
GoRouter buildRouter(Reader read, {required bool signedIn, Listenable? refresh}) => GoRouter(
  initialLocation: signedIn ? '/home' : '/login',
  observers: [AuditNavObserver()],
  // 2026-10-22 (мобилка, админ, П9): права и касса сессии поменялись (доступы загрузились, владелец снял право, сессия
  // истекла) — redirect проверяется заново сразу, а не при следующем переходе.
  refreshListenable: refresh,
  redirect: (context, state) {
    final s = read(sessionProvider);
    final loc = state.matchedLocation;
    // 2026-10-27: «Режим тестировщика» открывается и до входа (5 нажатий на логотип на экране входа)
    if (s == null && loc != '/login' && loc != '/tester') return '/login';
    if (s != null && s.cashboxId == null && loc != '/cashbox' && loc != '/login') return '/cashbox';
    // 13.10, владелец: «доступы в мобилке работают?» — раздел, закрытый сотруднику на сайте, не открывается и по ссылке.
    if (s != null && !s.canRoute(loc)) return '/denied';
    return null;
  },
  routes: [
    GoRoute(path: '/login', builder: (_, _) => const LoginScreen()),
    GoRoute(path: '/cashbox', builder: (_, _) => const CashboxScreen()),
    GoRoute(path: '/denied', builder: (_, _) => const AccessDeniedScreen()),
    ShellRoute(
      builder: (c, state, child) => CashierShell(location: state.matchedLocation, child: child),
      routes: [
        GoRoute(path: '/home', builder: (_, _) => const HomeScreen()),
        GoRoute(
          path: '/sale',
          builder: (_, _) => const SafeArea(bottom: false, child: SaleScreen()),
        ),
        GoRoute(path: '/checks', builder: (_, _) => const ChecksScreen()),
        GoRoute(
          path: '/stock',
          builder: (_, s) => StockScreen(filter: s.uri.queryParameters['filter']),
        ),
      ],
    ),
    GoRoute(
      path: '/scan',
      builder: (_, s) => ScanScreen(initial: s.uri.queryParameters['mode'] == 'price' ? ScanMode.price : null),
    ),
    GoRoute(path: '/pay', builder: (_, _) => const PaymentScreen()),
    GoRoute(
      path: '/paid',
      // 2026-10-22 (мобилка, админ, П9): без extra (восстановление после выгрузки) — экран «данные не сохранились».
      builder: (_, s) => withExtra<PaidInfo>(s.extra, (v) => PaidScreen(info: v)),
    ),
    GoRoute(
      path: '/checks/detail',
      builder: (_, s) => withExtra<CheckRow>(s.extra, (v) => CheckDetailScreen(row: v)),
    ),
    GoRoute(path: '/stock/receive', builder: (_, _) => const ReceiveScreen()),
    GoRoute(path: '/stock/audit', builder: (_, _) => const AuditScreen()),
    // 13.10, владелец: массовая загрузка товаров на складе (Excel, CSV, вставка).
    GoRoute(path: '/stock/import', builder: (_, _) => const BulkImportScreen()),
    GoRoute(path: '/close-shift', builder: (_, _) => const CloseShiftScreen()),
    GoRoute(path: '/profile', builder: (_, _) => const ProfileScreen()),
    GoRoute(path: '/sync', builder: (_, _) => const SyncScreen()),
    // 2026-10-27, владелец: «Режим тестировщика» — список функций версии с отметками «проверено / проблема» (5 нажатий на логотип)
    GoRoute(path: '/tester', builder: (_, _) => const TesterScreen()),
    // 2026-10-26 (голос-управление, В5): режим помощника и журнал действий ИИ (открыты всем по роли, права — в самих экранах).
    GoRoute(path: '/ai/settings', builder: (_, _) => const AssistantSettingsScreen()),
    GoRoute(path: '/ai/log', builder: (_, _) => const AiLogScreen()),
    // 13.10: функции старой кассы на телефоне — внесение/изъятие, долги, отложенные чеки.
    GoRoute(path: '/cash', builder: (_, _) => const CashOpScreen()),
    GoRoute(path: '/debts', builder: (_, _) => const DebtsScreen()),
    GoRoute(path: '/held', builder: (_, _) => const HeldScreen()),
    // 13.10, этап 2 — администратор на телефоне (макеты Т8, Т11–Т14, Т21–Т22, Т26, СЗ5, Л5, У3).
    GoRoute(path: '/search', builder: (_, _) => const SearchScreen()),
    // 13.10, макет С8: прокат на телефоне.
    GoRoute(path: '/rentals', builder: (_, _) => const RentalsScreen()),
    GoRoute(path: '/rentals/new', builder: (_, _) => const NewRentalScreen()),
    GoRoute(path: '/admin', builder: (_, _) => const AdminScreen()),
    GoRoute(path: '/admin/product', builder: (_, _) => const ProductPriceScreen()),
    GoRoute(path: '/admin/shifts', builder: (_, _) => const ShiftsScreen()),
    GoRoute(path: '/admin/orders', builder: (_, _) => const OrdersScreen()),
    GoRoute(
      path: '/admin/pick',
      builder: (_, s) => withExtra<SiteOrder>(s.extra, (v) => PickScreen(order: v)),
    ),
    GoRoute(path: '/admin/clients', builder: (_, _) => const ClientsScreen()),
    GoRoute(path: '/admin/purchase', builder: (_, _) => const SupplierOrderScreen()),
    GoRoute(path: '/admin/deletions', builder: (_, _) => const DeletionsScreen()),
    GoRoute(
      path: '/admin/receive',
      builder: (_, s) => s.extra is ReceiveHandoff
          ? AdminReceiveScreen(
              initialLines: (s.extra as ReceiveHandoff).lines,
              initialSupplierId: (s.extra as ReceiveHandoff).supplierId,
              onPosted: (s.extra as ReceiveHandoff).onPosted,
              initialInvoiceKeys: (s.extra as ReceiveHandoff).invoiceKeys,
            )
          : AdminReceiveScreen(initialLines: s.extra is List<ReceiveLine> ? s.extra as List<ReceiveLine> : null),
    ),
    // 2026-10-27: «Загрузить накладную» — электронная накладная поставщика (Excel / CSV / XML) → строки прихода
    GoRoute(path: '/admin/invoice', builder: (_, s) => InvoiceImportScreen(forReceive: s.extra == true, initialFile: s.extra is InvoiceFileArg ? s.extra as InvoiceFileArg : null)),
    // 13.10, доски Т9–Т10: умная камера — снимок полки, накладной, этикеток → приёмка.
    // 2026-10-27: ?mode=invoice — «Сфотографировать накладную» открывает режим «Накладная», а не «Полка»
    GoRoute(path: '/admin/smartcam', builder: (_, s) => SmartCameraScreen(initialMode: s.uri.queryParameters['mode'])),
    GoRoute(path: '/admin/staff', builder: (_, _) => const StaffScreen()),
    // 14.10, владелец: QR для оплаты, как на десктопе.
    GoRoute(path: '/admin/payqr', builder: (_, _) => const PayQrSettingsScreen()),
    GoRoute(path: '/admin/debt-access', builder: (_, _) => const DebtAccessScreen()),
    // 14.10, владелец: журнал аудита.
    GoRoute(path: '/admin/audit', builder: (_, _) => const AuditLogScreen()),
    // 2026-10-26 (голос-управление, В7): задачи сотрудникам, мои задачи, зарплата.
    GoRoute(path: '/admin/tasks', builder: (_, _) => const TasksScreen()),
    GoRoute(path: '/tasks/my', builder: (_, _) => const MyTasksScreen()),
    GoRoute(path: '/admin/salary', builder: (_, s) => SalaryScreen(period: s.uri.queryParameters['period'])),
    GoRoute(path: '/admin/notifications', builder: (_, _) => const NotificationsScreen()),
    // 13.10, владелец: «коммерческое предложение и ЭСФ в мобилку добавь».
    GoRoute(path: '/admin/quotes', builder: (_, _) => const QuotesScreen()),
    // 13.10, владелец: Нур Советник, сводка, ABC-анализ с диаграммой Парето.
    GoRoute(path: '/admin/advisor', builder: (_, _) => const AiAccessGate(child: AdvisorScreen())),
    GoRoute(path: '/admin/summary', builder: (_, _) => const SummaryScreen()),
    GoRoute(path: '/admin/abc', builder: (_, _) => const AbcScreen()),
    GoRoute(path: '/admin/esf', builder: (_, _) => const EsfScreen()),
    // 13.10, макеты Н3, Н7, Н8: новая позиция — товар, одежда, услуга (?kind=clothes|service).
    GoRoute(
      path: '/admin/new-product',
      builder: (_, s) => NewItemScreen(kind: s.uri.queryParameters['kind'] ?? 'product', barcode: s.uri.queryParameters['barcode']),
    ),
  ],
);

/// 2026-10-26 (голос-управление, В4): роутер приложения живёт в провайдере (раньше — приватное поле State в app.dart): к нему
/// обращается звонок с Нур Советником (`push` разделов, «назад», открытие чека), пока его экран свёрнут в плашку.
/// Перепроверка доступа (`refreshListenable`) — только когда меняется то, от чего зависит redirect (вход/выход, касса, роль, доступы).
final appRouterProvider = Provider<GoRouter>((ref) {
  final refresh = ValueNotifier<int>(0);
  String routeKey(Session? s) => s == null ? '' : '${s.cashboxId}|${s.role}|${s.isOwner}|${s.perms?.join(',')}';
  // 2026-10-22 (мобилка, админ, П9): refreshListenable роутера — только когда меняется то, от чего зависит redirect
  // (вход/выход, касса, роль, доступы), а не при каждом обновлении токена.
  ref.listen<Session?>(sessionProvider, (prev, next) {
    if (routeKey(prev) != routeKey(next)) refresh.value++;
  });
  // вход сохранён (его читает main до runApp) — сразу на главную, иначе на вход
  final router = buildRouter(ref.read, signedIn: ref.read(sessionProvider) != null, refresh: refresh);
  ref.onDispose(() {
    router.dispose();
    refresh.dispose();
  });
  return router;
});

class NurMarketApp extends ConsumerStatefulWidget {
  final bool signedIn;
  const NurMarketApp({super.key, required this.signedIn});
  @override
  ConsumerState<NurMarketApp> createState() => _NurMarketAppState();
}

class _NurMarketAppState extends ConsumerState<NurMarketApp> {
  late final GoRouter _router = ref.read(appRouterProvider);

  /// 2026-10-29 (аудит мобилки, п. 3): вернулись в приложение — очередь продаж уходит сразу, а не по таймеру через ≤ 20 с.
  late final AppLifecycleListener _lifecycle = AppLifecycleListener(onResume: _onResume, onHide: _onHide);

  /// Приложение скрыто — опрос по таймеру на паузу (срочно, «Скорость синхронизации»).
  void _onHide() {
    ref.read(syncProvider).pause();
    unawaited(ref.read(syncProvider).planBackground()); // 2026-10-30 (оптимизация, п. 14): сворачиваемся — есть неотправленное, значит фоновая задача нужна
    unawaited(Audit.syncToDisk()); // 2026-10-30 (оптимизация, п. 8): журнал действий — на диск (fsync) при сворачивании
  }
  late final ApiClient _api = ref.read(apiProvider);
  bool _wasOffline = false;

  Future<void> _onResume() async {
    if (ref.read(sessionProvider) == null) return;
    // фоновая задача могла обновить токены, пока приложение было закрыто или свёрнуто
    await ref.read(apiProvider).reloadTokens();
    ref.read(syncProvider).resume();
    unawaited(ref.read(syncProvider).runOnce());
  }

  /// Связь появилась (первый ответ сервера после обрыва) — очередь уходит сразу, не дожидаясь таймера.
  void _onLink() {
    final off = _api.link.value.offline;
    if (_wasOffline && !off && ref.read(sessionProvider) != null) unawaited(ref.read(syncProvider).runOnce());
    _wasOffline = off;
  }

  @override
  void dispose() {
    _lifecycle.dispose();
    _api.link.removeListener(_onLink);
    super.dispose();
  }

  @override
  void initState() {
    super.initState();
    _api.link.addListener(_onLink);
    // 2026-10-26 (голос-управление, В4): звонок ИИ ходит по разделам через этот же роутер.
    ref.read(callControllerProvider).attachRouter(_router);
    // Сессия истекла и не обновилась — на вход. 2026-10-22 (мобилка, деньги, К-5): без logout — набранный чек и очередь
    // остаются до входа тем же логином (раньше чек стирался, а очередь уходила в «не принято»).
    ref.read(apiProvider).onExpired.listen((_) async {
      if (ref.read(sessionProvider) == null) return;
      await ref.read(sessionProvider.notifier).expire();
      _router.go('/login');
    });
    if (widget.signedIn) {
      ref.read(syncProvider).start();
      unawaited(ref.read(syncProvider).planBackground()); // 2026-10-30 (оптимизация, п. 14): фоновая отправка — разовой задачей, если очередь не пуста
    }
  }

  @override
  Widget build(BuildContext context) {
    final prefs = ref.watch(prefsProvider);
    ref.listen(sessionProvider, (prev, next) {
      // 14.10: журнал аудита — кто работает; вход и выход.
      Audit.currentUser = next?.userName ?? '';
      if (prev == null && next != null) Audit.log('login', 'Вход · ${next.email}');
      // 2026-10-29: запись «Выход» не пишем — выход стирает журнал, следов аккаунта на телефоне не остаётся
      if (prev == null && next != null) {
        ref.read(syncProvider).start();
        unawaited(BackgroundSync.rebind().then((_) => ref.read(syncProvider).planBackground())); // 2026-10-29 (смена аккаунта): задача очереди — заново под новый вход
      }
      if (next == null) {
        ref.read(syncProvider).stop();
        unawaited(BackgroundSync.cancel());
      }
      // 2026-10-26 (голос-управление, В4): вышли из учётной записи или сменили кассу — звонок с ИИ завершается
      if (prev != null && (next == null || next.cashboxId != prev.cashboxId || next.userId != prev.userId)) {
        ref.read(callControllerProvider).endCall();
      }
    });
    return MaterialApp.router(
      title: 'NurCRM',
      debugShowCheckedModeBanner: false,
      theme: buildTheme(dark: false),
      darkTheme: buildTheme(dark: true),
      themeMode: prefs.themeMode,
      locale: prefs.locale,
      supportedLocales: AppLocalizations.supportedLocales,
      localizationsDelegates: const [
        AppLocalizations.delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      routerConfig: _router,
      // 14.10, владелец: «кнопки многие вниз уходят». Android 15 рисует приложение под полосой жестов, а экраны и нижние
      // окна отступ снизу не делали — нижние кнопки уходили под неё. Один отступ на всё приложение (экраны, окна снизу,
      // диалоги); клавиатуру это не трогает (она — viewInsets, не padding).
      // 2026-10-26 (живой аудит, П1): значки строки состояния — светлые в тёмной теме и тёмные в светлой (раньше время
      // и сеть не было видно); экран с тёмной шапкой в светлой теме оборачивается в LightStatusBar.
      builder: (context, child) {
        // 2026-10-26 (живой аудит, П4): язык для текстов ошибок сети (ApiClient без контекста)
        ApiText.l = AppLocalizations.of(context);
        // 2026-10-26 (голос-управление, В4): звонок с Нур Советником поверх любого раздела — мини-плашка, строка итога,
        // панель таблицы (call_overlay.dart); сам раздел остаётся тем же деревом.
        return AssistantOverlay(
          navigatorKey: _router.routerDelegate.navigatorKey,
          child: AnnotatedRegion<SystemUiOverlayStyle>(
            value: statusBarStyle(dark: Theme.of(context).brightness == Brightness.dark),
            child: ColoredBox(
              color: Theme.of(context).scaffoldBackgroundColor,
              child: SafeArea(top: false, left: false, right: false, child: child ?? const SizedBox.shrink()),
            ),
          ),
        );
      },
    );
  }
}

/// 14.10: журнал аудита — какой экран открыли (адрес экрана).
class AuditNavObserver extends NavigatorObserver {
  @override
  void didPush(Route<dynamic> route, Route<dynamic>? previousRoute) {
    final name = route.settings.name;
    if (name != null && name.startsWith('/')) Audit.log('open', name);
  }
}
