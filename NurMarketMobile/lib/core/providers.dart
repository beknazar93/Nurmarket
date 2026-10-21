import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../features/admin/quotes_store.dart';
import '../features/ops/ops_repository.dart';
import '../features/payment/sales_repository.dart';
import '../features/sale/cart.dart';
import '../features/sale/catalog_repository.dart';
import '../features/shift/shift_repository.dart';
import '../features/stock/stock_repository.dart';
import 'api/api_client.dart';
import 'api/session_store.dart';
import 'db/database.dart';
import 'sync/sync_service.dart';
import 'util/audit.dart';

final databaseProvider = Provider<AppDatabase>((ref) => throw UnimplementedError('задаётся в main'));
final sessionStoreProvider = Provider<SessionStore>((ref) => SessionStore());
final apiProvider = Provider<ApiClient>((ref) => ApiClient(ref.watch(sessionStoreProvider)));

final catalogRepoProvider = Provider((ref) => CatalogRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));
final shiftRepoProvider = Provider((ref) => ShiftRepository(ref.watch(apiProvider)));
final salesRepoProvider = Provider((ref) => SalesRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));
final stockRepoProvider = Provider((ref) => StockRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));
final opsRepoProvider = Provider((ref) => OpsRepository(ref.watch(apiProvider), ref.watch(databaseProvider)));
final syncProvider = Provider(
  (ref) => SyncService(
    db: ref.watch(databaseProvider),
    sales: ref.watch(salesRepoProvider),
    stock: ref.watch(stockRepoProvider),
    catalog: ref.watch(catalogRepoProvider),
    // 2026-10-21: КП и счета без связи уходят в общий список фоном (только если есть неотправленные).
    quotes: () => ref.read(quotesStoreProvider).sync(onlyIfDirty: true),
  ),
);

final pendingCountProvider = StreamProvider<int>((ref) => ref.watch(databaseProvider).pendingCount());

// ------------------------------------------------------------------ сессия

class SessionController extends Notifier<Session?> {
  @override
  Session? build() => null;

  ApiClient get _api => ref.read(apiProvider);
  SessionStore get _store => ref.read(sessionStoreProvider);

  Future<bool> restore() async {
    final s = await _store.load();
    if (s == null) return false;
    _api.attach(s);
    state = s;
    // Реквизиты для чека у входов до 13.10 не сохранены — подтягиваем в фоне, вход не ждёт.
    if (s.companyInn.isEmpty && s.companyAddress.isEmpty) unawaited(_refreshCompany());
    // 13.10: доступы сотрудника — при каждом запуске (владелец мог их поменять на сайте), вход не ждёт.
    unawaited(refreshProfile());
    return true;
  }

  /// Роль и доступы (can_view_*) из api/users/profile/. Без связи — остаются прежние.
  Future<void> refreshProfile() async {
    try {
      final profile = await _api.get('api/users/profile/');
      final cur = state;
      if (cur == null || profile is! Map) return;
      final user = (profile['user'] is Map ? profile['user'] : profile) as Map;
      final u = user.cast<String, dynamic>();
      final s = cur.copyWith(
        role: str(u, 'role_display') ?? str(u, 'role') ?? cur.role,
        isOwner: u['is_owner'] == true || str(u, 'role') == 'owner',
        perms: Session.extractPerms(profile),
      );
      await _store.save(s);
      state = s;
    } catch (_) {
      // без связи — в следующий запуск
    }
  }

  Future<void> _refreshCompany() async {
    try {
      final company = (await _api.get('api/users/company/')) as Map<String, dynamic>;
      final cur = state;
      if (cur == null) return;
      final s = cur.copyWith(
        companyName: str(company, 'name') ?? cur.companyName,
        companyInn: str(company, 'inn') ?? '',
        companyAddress: str(company, 'address') ?? '',
      );
      await _store.save(s);
      state = s;
    } catch (_) {
      // без связи — в следующий запуск
    }
  }

  /// Вход: токены → профиль (имя, роль) → компания. Тот же логин, что на кассе и в вебе.
  Future<void> login(String email, String password) async {
    await _api.login(email, password);
    var s = _api.session!;
    try {
      final profile = (await _api.get('api/users/profile/')) as Map<String, dynamic>;
      final user = (profile['user'] is Map ? profile['user'] : profile) as Map;
      final u = user.cast<String, dynamic>();
      final name = [str(u, 'first_name'), str(u, 'last_name')].whereType<String>().join(' ').trim();
      s = s.copyWith(
        userId: str(u, 'id') ?? '',
        userName: name.isNotEmpty ? name : (str(u, 'full_name') ?? s.email),
        role: str(u, 'role_display') ?? str(u, 'role') ?? '',
        isOwner: u['is_owner'] == true || str(u, 'role') == 'owner',
        perms: Session.extractPerms(profile),
      );
    } on ApiException {
      // Профиль не загрузился — входим с логином вместо имени.
    }
    try {
      final company = (await _api.get('api/users/company/')) as Map<String, dynamic>;
      s = s.copyWith(companyName: str(company, 'name') ?? '', companyInn: str(company, 'inn') ?? '', companyAddress: str(company, 'address') ?? '');
    } on ApiException {
      // не критично
    }
    _api.attach(s);
    await _store.save(s);
    state = s;
  }

  Future<void> chooseCashbox(Cashbox box) async {
    final s = state!.copyWith(cashboxId: box.id, cashboxName: box.name);
    _api.attach(s);
    await _store.save(s);
    state = s;
  }

  Future<void> logout() async {
    await _store.clear();
    _api.attach(null);
    ref.read(cartProvider.notifier).clear();
    ref.read(shiftProvider.notifier).reset();
    state = null;
  }
}

final sessionProvider = NotifierProvider<SessionController, Session?>(SessionController.new);

// ------------------------------------------------------------------ смена

class ShiftState {
  final ShiftInfo? shift;
  final bool loading;
  final String? error;
  const ShiftState({this.shift, this.loading = false, this.error});
  bool get isOpen => shift != null;
}

class ShiftController extends Notifier<ShiftState> {
  @override
  ShiftState build() => const ShiftState();

  void reset() => state = const ShiftState();

  Future<void> refresh() async {
    final s = ref.read(sessionProvider);
    if (s == null) return;
    state = ShiftState(shift: state.shift, loading: true);
    try {
      final shift = await ref.read(shiftRepoProvider).openShift(s.cashboxId);
      state = ShiftState(shift: shift);
      if (shift != null) await ref.read(databaseProvider).setSetting('shift.id', shift.id);
    } on ApiException catch (e) {
      // Без связи — работаем по последней известной смене (продажи уйдут в очередь с ней).
      final cached = await ref.read(databaseProvider).getSetting('shift.id');
      state = ShiftState(
        shift: state.shift ?? (cached != null ? ShiftInfo(id: cached) : null),
        error: e.message,
      );
    }
  }

  Future<void> open(double openingCash) async {
    final s = ref.read(sessionProvider)!;
    final shift = await ref.read(shiftRepoProvider).open(s.cashboxId!, openingCash);
    state = ShiftState(shift: shift);
    Audit.log('shift', 'Смена открыта', amount: openingCash);
    await ref.read(databaseProvider).setSetting('shift.id', shift.id);
    await refresh();
  }

  Future<void> close(double? closingCash) async {
    final id = state.shift?.id;
    if (id == null) return;
    await ref.read(shiftRepoProvider).close(id, closingCash);
    Audit.log('shift', 'Смена закрыта', amount: closingCash);
    state = const ShiftState();
  }
}

final shiftProvider = NotifierProvider<ShiftController, ShiftState>(ShiftController.new);

// ------------------------------------------------------------------ чек

class CartController extends Notifier<Cart> {
  @override
  Cart build() => const Cart();

  /// 13.10, «продажа оптом и в розницу»: в режиме «Опт» товар с оптовой ценой сразу идёт по опту.
  bool wholesaleMode = false;

  void add(Product p, {double qty = 1, String? variantId, String? variantLabel, double? variantPrice}) {
    final line = CartLine(
      productId: p.id,
      name: variantLabel == null ? p.name : '${p.name} ($variantLabel)',
      price: variantPrice ?? p.price,
      qty: qty,
      isWeight: p.isWeight,
      unit: p.unit,
      wholesalePrice: variantId == null ? p.wholesalePrice : 0,
      variantId: variantId,
      promo: PromoRule.parse(p.promoRules),
    );
    if (wholesaleMode) line.setWholesale(true);
    state = state.add(line);
    Audit.log('cart', '${line.name} × ${qty == qty.roundToDouble() ? qty.toInt() : qty}', amount: roundMoney(line.price * qty));
  }

  /// Строка ↔ опт.
  void toggleWholesale(int i) {
    final lines = [...state.lines];
    lines[i].setWholesale(!lines[i].wholesale);
    state = state.copyWith(lines: lines);
  }

  /// «Опт на весь чек» / «Розница»: все строки с оптовой ценой — по опту или обратно; новые строки — так же.
  void setWholesaleMode(bool on) {
    wholesaleMode = on;
    final lines = [...state.lines];
    for (final l in lines) {
      if (l.canWholesale) l.setWholesale(on);
    }
    state = state.copyWith(lines: lines);
  }

  void addCustom(String name, double price, double qty) =>
      state = state.add(CartLine(productId: 'custom-${DateTime.now().microsecondsSinceEpoch}', name: name, price: price, qty: qty, custom: true));

  void setQty(int i, double q) => state = state.setQty(i, q);
  void remove(int i) => state = state.remove(i);
  void setLineDiscount(int i, double sum) {
    final lines = [...state.lines];
    lines[i].discount = sum.clamp(0, lines[i].gross).toDouble();
    state = state.copyWith(lines: lines);
  }

  void setOrderDiscount({double percent = 0, double sum = 0}) =>
      state = Cart(lines: state.lines, orderDiscountPercent: percent, orderDiscountSum: percent > 0 ? 0 : sum);

  void clear() {
    if (state.lines.isNotEmpty) Audit.log('cart', 'Чек очищен (${state.lines.length})');
    wholesaleMode = false;
    state = const Cart();
  }

  /// Вернуть отложенный чек (13.10).
  void restore(Cart cart) => state = cart;
}

final cartProvider = NotifierProvider<CartController, Cart>(CartController.new);

// ------------------------------------------------------------------ язык и тема

class Prefs {
  final Locale locale;
  final bool dark;

  /// 13.10, макет ТМ3: «Как в системе» — тема телефона (светлая/тёмная по настройке Android).
  final bool system;

  /// 14.10, владелец: «смена темы по времени» — тёмная с [darkFrom] до [darkTo] (минуты от полуночи), как «авто» кассы.
  final bool auto;
  final int darkFrom;
  final int darkTo;

  /// 13.10, макет С5: магазин одежды — при продаже выбор размера и цвета (как MarketSpheres.IsClothing кассы).
  final bool clothing;
  const Prefs({
    this.locale = const Locale('ru'),
    this.dark = false,
    this.system = false,
    this.auto = false,
    this.darkFrom = 19 * 60,
    this.darkTo = 7 * 60,
    this.clothing = false,
  });

  Prefs copyWith({Locale? locale, bool? dark, bool? system, bool? auto, int? darkFrom, int? darkTo, bool? clothing}) => Prefs(
    locale: locale ?? this.locale,
    dark: dark ?? this.dark,
    system: system ?? this.system,
    auto: auto ?? this.auto,
    darkFrom: darkFrom ?? this.darkFrom,
    darkTo: darkTo ?? this.darkTo,
    clothing: clothing ?? this.clothing,
  );

  String get mode => auto ? 'auto' : (system ? 'system' : (dark ? 'dark' : 'light'));

  /// Сейчас тёмное время (интервал может переходить через полночь: 19:00–07:00).
  bool get darkNow => darkAt(DateTime.now());

  bool darkAt(DateTime now) {
    final m = now.hour * 60 + now.minute;
    return darkFrom <= darkTo ? (m >= darkFrom && m < darkTo) : (m >= darkFrom || m < darkTo);
  }

  ThemeMode get themeMode => auto ? (darkNow ? ThemeMode.dark : ThemeMode.light) : (system ? ThemeMode.system : (dark ? ThemeMode.dark : ThemeMode.light));
}

class PrefsController extends Notifier<Prefs> {
  Timer? _timer;
  bool? _lastDark;

  @override
  Prefs build() {
    // Режим «По времени»: раз в минуту проверяем, не пора ли сменить тему (новый объект — MaterialApp перестроится).
    _timer ??= Timer.periodic(const Duration(minutes: 1), (_) {
      if (!state.auto) return;
      final d = state.darkNow;
      if (d != _lastDark) {
        _lastDark = d;
        state = state.copyWith();
      }
    });
    ref.onDispose(() => _timer?.cancel());
    return const Prefs();
  }

  static int _minutes(String? value, int fallback) {
    final p = value?.split(':');
    if (p == null || p.length != 2) return fallback;
    final h = int.tryParse(p[0]), m = int.tryParse(p[1]);
    return h == null || m == null ? fallback : (h * 60 + m).clamp(0, 24 * 60 - 1);
  }

  static String hhmm(int minutes) => '${(minutes ~/ 60).toString().padLeft(2, '0')}:${(minutes % 60).toString().padLeft(2, '0')}';

  Future<void> load() async {
    final db = ref.read(databaseProvider);
    final dark = await db.getSetting('ui.dark');
    state = Prefs(
      locale: Locale(await db.getSetting('ui.lang') ?? 'ru'),
      dark: dark == '1',
      system: dark == 'system',
      auto: dark == 'auto',
      darkFrom: _minutes(await db.getSetting('ui.darkFrom'), 19 * 60),
      darkTo: _minutes(await db.getSetting('ui.darkTo'), 7 * 60),
      clothing: (await db.getSetting('shop.clothing')) == '1',
    );
    _lastDark = state.darkNow;
  }

  Future<void> setLocale(String code) async {
    await ref.read(databaseProvider).setSetting('ui.lang', code);
    state = state.copyWith(locale: Locale(code));
  }

  Future<void> setDark(bool v) async {
    await ref.read(databaseProvider).setSetting('ui.dark', v ? '1' : '0');
    state = state.copyWith(dark: v, system: false, auto: false);
  }

  Future<void> setClothing(bool v) async {
    await ref.read(databaseProvider).setSetting('shop.clothing', v ? '1' : '0');
    state = state.copyWith(clothing: v);
  }

  /// light / dark / system / auto.
  Future<void> setMode(String mode) async {
    await ref.read(databaseProvider).setSetting('ui.dark', switch (mode) {
      'dark' => '1',
      'system' => 'system',
      'auto' => 'auto',
      _ => '0',
    });
    state = state.copyWith(dark: mode == 'dark', system: mode == 'system', auto: mode == 'auto');
    _lastDark = state.darkNow;
  }

  /// Часы тёмной темы для режима «По времени».
  Future<void> setDarkHours({int? from, int? to}) async {
    final db = ref.read(databaseProvider);
    if (from != null) await db.setSetting('ui.darkFrom', hhmm(from));
    if (to != null) await db.setSetting('ui.darkTo', hhmm(to));
    state = state.copyWith(darkFrom: from, darkTo: to);
    _lastDark = state.darkNow;
  }
}

final prefsProvider = NotifierProvider<PrefsController, Prefs>(PrefsController.new);
