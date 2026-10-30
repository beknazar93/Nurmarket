import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../features/admin/quotes_store.dart';
import '../features/ops/ops_repository.dart';
import '../features/payment/sales_repository.dart';
import '../features/sale/cart.dart';
import '../features/sale/catalog_repository.dart';
import '../features/shift/shift_repository.dart';
import '../features/stock/product_packages.dart' show PiecePack;
import '../features/stock/stock_repository.dart';
import 'api/api_client.dart';
import 'api/session_store.dart';
import 'db/database.dart';
import 'ai/ai_access.dart';
import 'ai/vision.dart';
import 'sync/account_reset.dart';
import 'sync/background_sync.dart';
import 'sync/account_switch.dart';
import 'sync/outbox_meta.dart';
import 'sync/sync_service.dart';
import 'util/audit.dart';
import '../features/payment/debt_access.dart';
import '../features/payment/payment_qr.dart';
import '../features/scan/code_lookup.dart';
import '../core/ai/cam_aliases.dart';

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
    // 2026-10-22 (мобилка, деньги, К-6): очередь отправляет только записи аккаунта текущего входа.
    owner: () => OutboxMeta.ownerOf(ref.read(apiProvider).session),
    // 2026-10-21: КП и счета без связи уходят в общий список фоном (только если есть неотправленные).
    quotes: () => ref.read(quotesStoreProvider).sync(onlyIfDirty: true),
    // 2026-10-30 (оптимизация, п. 14): фоновая отправка (Android) — разовая задача только пока есть неотправленное
    onUnsentChanged: (has) => has ? BackgroundSync.schedule() : BackgroundSync.cancel(),
  ),
);

/// 2026-10-29 (полная изоляция аккаунтов): считаются только неотправленные записи ВХОДА (аккаунта), а не всего телефона —
/// записи другого аккаунта (если остались после истёкшего входа) под этим входом не показываются и не отправляются.
final pendingCountProvider = StreamProvider<int>((ref) {
  final db = ref.watch(databaseProvider);
  final me = OutboxMeta.ownerOf(ref.watch(sessionProvider));
  // 2026-10-30 (оптимизация, п. 1): раньше — вся таблица очереди (с телами чеков) на каждую её запись; теперь только ждущие
  // записи (индекс по статусу), а изменения без смены числа дальше не идут (distinct).
  return db.watchPendingRows().asyncMap((rows) async {
    if (rows.isEmpty) return 0;
    final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
    return rows.where((r) => OutboxMeta.belongsTo(r, me, legacyOwner: legacy)).length;
  }).distinct();
});

// ------------------------------------------------------------------ загрузка данных аккаунта

/// 2026-10-29 (полная изоляция аккаунтов, уточнение владельца): после входа с сервера подтягиваются данные ЭТОГО аккаунта —
/// профиль и права, компания, доступ к ИИ и его ключи, правила компании, должники, смена. Пока идёт — «Загружаем данные
/// аккаунта…» (экран входа и главная), не вышло — понятная ошибка с «Повторить».
class AccountLoadState {
  final bool loading;
  final int done;
  final int total;

  /// Хоть один шаг не загрузился (нет связи, ошибка сервера) — на главной плашка «Повторить».
  final bool failed;
  const AccountLoadState({this.loading = false, this.done = 0, this.total = 0, this.failed = false});
}

class AccountLoadController extends Notifier<AccountLoadState> {
  @override
  AccountLoadState build() => const AccountLoadState();
  void set(AccountLoadState s) => state = s;
}

final accountLoadProvider = NotifierProvider<AccountLoadController, AccountLoadState>(AccountLoadController.new);

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
    // 2026-10-22 (мобилка, деньги, К-6): записи очереди без владельца (до обновления) — этого, сохранённого входа.
    await _claimLegacyQueue(s);
    unawaited(ref.read(cartProvider.notifier).restoreDraft()); // 2026-10-26 (П3, В-1): незавершённый чек после запуска
    // Реквизиты для чека у входов до 13.10 не сохранены — подтягиваем в фоне, вход не ждёт.
    if (s.companyInn.isEmpty && s.companyAddress.isEmpty) unawaited(_refreshCompany());
    // 13.10: доступы сотрудника — при каждом запуске (владелец мог их поменять на сайте), вход не ждёт.
    unawaited(refreshProfile());
    return true;
  }

  /// 2026-10-28 (сотрудники): «Имя Фамилия» из профиля; пусто — null (прежнее имя остаётся).
  static String? _nameOf(Map<String, dynamic> u) {
    final n = [str(u, 'first_name'), str(u, 'last_name')].whereType<String>().join(' ').trim();
    return n.isNotEmpty ? n : str(u, 'full_name');
  }

  /// Роль и доступы (can_view_*) из api/users/profile/. Без связи — остаются прежние.
  /// true — профиль получен с сервера (2026-10-29: нужен шагу «Загружаем данные аккаунта»).
  Future<bool> refreshProfile() async {
    try {
      final profile = await _api.get('api/users/profile/');
      final cur = state;
      if (cur == null || profile is! Map) return false;
      final user = (profile['user'] is Map ? profile['user'] : profile) as Map;
      final u = user.cast<String, dynamic>();
      final s = cur.copyWith(
        // 2026-10-27: номер пользователя подгружается, если при входе профиль не загрузился (нужен для списка «Доступ к ИИ»)
        userId: cur.userId.isEmpty ? (str(u, 'id') ?? '') : cur.userId,
        // 2026-10-28 (сотрудники): имя в шапке/чеках обновляется, если владелец поменял его на сайте или в карточке сотрудника
        userName: _nameOf(u) ?? cur.userName,
        role: str(u, 'role_display') ?? str(u, 'role') ?? cur.role,
        isOwner: u['is_owner'] == true || str(u, 'role') == 'owner',
        perms: Session.extractPerms(profile),
      );
      // 2026-10-30 (оптимизация, п. 9): профиль не изменился — ни записи в защищённое хранилище (Keystore, 30–150 мс), ни нового
      // Session (он перестраивал ~20 наблюдателей в первые кадры каждого запуска)
      if (_sameProfile(cur, s)) return true;
      await _store.save(s);
      state = s;
      return true;
    } catch (_) {
      return false; // без связи — в следующий запуск
    }
  }

  static bool _sameProfile(Session a, Session b) {
    if (a.userId != b.userId || a.userName != b.userName || a.role != b.role || a.isOwner != b.isOwner) return false;
    final x = a.perms, y = b.perms;
    if (x == null || y == null) return x == y;
    return x.length == y.length && Set.of(x).containsAll(y);
  }

  Future<bool> _refreshCompany() async {
    try {
      final company = (await _api.get('api/users/company/')) as Map<String, dynamic>;
      final cur = state;
      if (cur == null) return false;
      final s = cur.copyWith(
        companyName: str(company, 'name') ?? cur.companyName,
        companyInn: str(company, 'inn') ?? '',
        companyAddress: str(company, 'address') ?? '',
      );
      await _store.save(s);
      state = s;
      return true;
    } catch (_) {
      return false; // без связи — в следующий запуск
    }
  }

  /// Вход: токены → профиль (имя, роль) → компания. Тот же логин, что на кассе и в вебе.
  Future<void> login(String email, String password) async {
    await _api.login(email, password);
    var s = _api.session!;
    _profileLoaded = false;
    _companyLoaded = false;
    String? serverLang; // 2026-10-29: язык интерфейса аккаунта с сервера — для первого входа
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
      _profileLoaded = true;
      final lang = (str(u, 'language') ?? str(u, 'lang') ?? '').trim().toLowerCase();
      if (const {'ru', 'ky', 'en', 'tr', 'uz'}.contains(lang)) serverLang = lang;
    } on ApiException {
      // Профиль не загрузился — входим с логином вместо имени.
    }
    try {
      final company = (await _api.get('api/users/company/')) as Map<String, dynamic>;
      s = s.copyWith(companyName: str(company, 'name') ?? '', companyInn: str(company, 'inn') ?? '', companyAddress: str(company, 'address') ?? '');
      _companyLoaded = true;
    } on ApiException {
      // не критично
    }
    _api.attach(s);
    await _store.save(s);
    final db = ref.read(databaseProvider);
    final owner = OutboxMeta.ownerOf(s);
    // 2026-10-22 (мобилка, деньги): вошли другим аккаунтом — чек и смена прошлого входа не переходят к новому
    // (после истечения сессии тем же логином набранный чек остаётся).
    final last = await db.getSetting(_lastOwnerKey);
    // 2026-10-29 (срочно): другой аккаунт — товары, остатки и настройки каталога прошлого аккаунта стираются, репозиторий каталога
    // пересоздаётся (курсор ленты, замок загрузки), фоновая задача очереди перепривязывается к новому входу (app.dart).
    // 2026-10-29 («ничего не должно остаться»): на телефоне остались данные другого аккаунта (выход не делали) — стираются целиком.
    var wiped = false;
    if (await AccountSwitch.prepare(db, owner)) {
      wiped = true;
      resetAccountProviders(ref);
    }
    // язык интерфейса нового аккаунта — с сервера (если прислал и пользователь сам не выбирал на экране входа)
    if (serverLang != null && (await db.getSetting('ui.lang') ?? '').isEmpty) await db.setSetting('ui.lang', serverLang);
    if (wiped || serverLang != null) await ref.read(prefsProvider.notifier).load();
    if (last != null && last.isNotEmpty && last != owner) {
      ref.read(cartProvider.notifier).clear();
      ref.read(shiftProvider.notifier).reset();
    }
    await db.setSetting(_lastOwnerKey, owner);
    await _claimLegacyQueue(s);
    state = s;
    unawaited(ref.read(cartProvider.notifier).restoreDraft()); // 2026-10-26 (П3, В-1): вошли после истечения сессии — чек на месте
    await loadAccountData();
  }

  bool _profileLoaded = false;
  bool _companyLoaded = false;

  /// 2026-10-29 (полная изоляция аккаунтов, уточнение владельца): подтянуть с сервера данные ЭТОГО аккаунта. Шаги идут по
  /// очереди с общим прогрессом [accountLoadProvider]; каждый ограничен по времени, сбой одного не мешает остальным. Каталог
  /// и клиенты грузятся фоном (SyncService.start при входе) — здесь только то, без чего экраны покажут чужое или пустое.
  /// Вызывается при входе и по «Повторить» на главной.
  Future<void> loadAccountData() async {
    final load = ref.read(accountLoadProvider.notifier);
    final api = ref.read(apiProvider);
    final db = ref.read(databaseProvider);
    final steps = <Future<bool> Function()>[
      () async => _profileLoaded || await refreshProfile(),
      () async => _companyLoaded || await _refreshCompany(),
      () async {
        // доступ к ИИ (кому разрешён, лимит) и ключи ИИ своего аккаунта
        await ref.read(aiAccessRepoProvider).load(force: true);
        final ok = AiAccessRepository.lastLoadFromServer;
        try {
          await ref.read(visionProvider).key();
        } catch (_) {
          // нет права на ИИ или ключа нет — это тоже загруженный ответ
        }
        return ok;
      },
      () async {
        // настройки компании: правила весов, долги, QR оплаты, названия камеры
        await ScaleRulesLoader.ensure(db, api);
        await CamAliases.ensure(db, api, force: true);
        await ref.read(debtAccessRepoProvider).load();
        await ref.read(payQrRepoProvider).load();
        return true;
      },
      () async {
        await ref.read(opsRepoProvider).debtors();
        return true;
      },
      () async {
        if (ref.read(sessionProvider)?.cashboxId == null) return true; // касса ещё не выбрана — смену прочитает главная
        await ref.read(shiftProvider.notifier).refresh();
        return ref.read(shiftProvider).error == null;
      },
    ];
    var failed = false;
    load.set(AccountLoadState(loading: true, total: steps.length));
    for (var i = 0; i < steps.length; i++) {
      try {
        if (!await steps[i]().timeout(const Duration(seconds: 20))) failed = true;
      } catch (e) {
        // нет прав на раздел (403) — не сбой загрузки; обрыв связи и ошибка сервера — сбой, на главной «Повторить»
        if (e is! ApiException || e.status == null || e.status! >= 500) failed = true;
      }
      if (state == null) break; // вышли во время загрузки
      load.set(AccountLoadState(loading: true, done: i + 1, total: steps.length, failed: failed));
    }
    load.set(AccountLoadState(done: steps.length, total: steps.length, failed: failed));
  }

  static const _lastOwnerKey = AccountSwitch.lastOwnerKey;

  /// Записи очереди до 22.10 без владельца — аккаунта, который первым вошёл после обновления (до него там был он же).
  Future<void> _claimLegacyQueue(Session s) async {
    final db = ref.read(databaseProvider);
    final cur = await db.getSetting(OutboxMeta.legacyOwnerSetting);
    if (cur == null || cur.isEmpty) await db.setSetting(OutboxMeta.legacyOwnerSetting, OutboxMeta.ownerOf(s));
    if ((await db.getSetting(_lastOwnerKey) ?? '').isEmpty) await db.setSetting(_lastOwnerKey, OutboxMeta.ownerOf(s));
  }

  Future<void> chooseCashbox(Cashbox box) async {
    final s = state!.copyWith(cashboxId: box.id, cashboxName: box.name);
    _api.attach(s);
    await _store.save(s);
    state = s;
  }

  /// 2026-10-29 («Ничего не должно остаться от старого аккаунта!!»): выход стирает с телефона ВСЁ — очередь, настройки, товары,
  /// ключи, журналы, файлы. Вызывать только после окна «Есть неотправленные продажи» (logout_guard.dart): к этому моменту очередь
  /// отправлена, выгружена в файл или удаление подтверждено словом.
  Future<void> logout() async {
    ref.read(syncProvider).stop();
    await _store.clear();
    _api.attach(null);
    ref.read(cartProvider.notifier).clear();
    await ref.read(cartProvider.notifier).flush();
    ref.read(shiftProvider.notifier).reset();
    ref.read(accountLoadProvider.notifier).set(const AccountLoadState());
    state = null;
    await AccountSwitch.wipeAll(ref.read(databaseProvider));
    resetAccountProviders(ref);
    await ref.read(prefsProvider.notifier).load(); // тема и язык — по умолчанию
  }

  /// 2026-10-22 (мобилка, деньги, К-5): сессия истекла (refresh-токен не обновился) — на вход, но набранный чек
  /// остаётся (после входа тем же логином он на месте), очередь ждёт входа и не уходит в «не принято».
  Future<void> expire() async {
    await _store.clear();
    _api.attach(null);
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

  /// 2026-10-22 (мобилка, деньги, К-1): на этой кассе открыта смена другого кассира — своей нет, откройте свою.
  final bool foreignShift;

  /// Смена взята из памяти телефона (нет связи) — итоги не обновлены.
  final bool cached;

  /// 2026-10-29 (аудит мобилки, п. 5): когда итоги смены в последний раз пришли с сервера. null — итогов нет вовсе (смена
  /// только из памяти телефона после перезапуска без связи): главная показывает «—», а не «0 сом · 0 чеков».
  final DateTime? fetchedAt;
  const ShiftState({this.shift, this.loading = false, this.error, this.foreignShift = false, this.cached = false, this.fetchedAt});
  bool get isOpen => shift != null;
}

class ShiftController extends Notifier<ShiftState> {
  @override
  ShiftState build() => const ShiftState();

  void reset() => state = const ShiftState();

  /// 2026-10-22 (мобилка, деньги, К-4): последняя известная смена — с аккаунтом и кассой. Раньше `shift.id` не стирался
  /// при закрытии, и без связи закрытая смена «оживала»: продажи шли в очередь с закрытой сменой, сервер отвечал 400.
  static const cacheKey = 'shift.id';

  String _owner() => OutboxMeta.ownerOf(ref.read(sessionProvider));

  Future<void> _remember(ShiftInfo shift) => ref
      .read(databaseProvider)
      .setSetting(cacheKey, jsonEncode({'id': shift.id, 'owner': _owner(), 'cashbox': shift.cashboxId ?? ref.read(sessionProvider)?.cashboxId}));

  Future<void> _forget() => ref.read(databaseProvider).setSetting(cacheKey, '');

  /// Смена из памяти — только этого аккаунта и этой кассы (старый формат «просто id» не берём: чья она, неизвестно).
  Future<String?> _cached() async {
    final raw = await ref.read(databaseProvider).getSetting(cacheKey);
    if (raw == null || raw.isEmpty || !raw.startsWith('{')) return null;
    try {
      final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
      final box = ref.read(sessionProvider)?.cashboxId;
      if (m['owner'] != _owner() || (box != null && m['cashbox'] != null && m['cashbox'] != box)) return null;
      return m['id'] as String?;
    } catch (_) {
      return null;
    }
  }

  Future<void> refresh() async {
    final s = ref.read(sessionProvider);
    if (s == null) return;
    state = ShiftState(shift: state.shift, loading: true, foreignShift: state.foreignShift, cached: state.cached, fetchedAt: state.fetchedAt);
    try {
      final found = await ref.read(shiftRepoProvider).findShift(s.cashboxId, cashierId: s.userId);
      var shift = found.shift;
      // Своя смена на другой кассе — переходим на её кассу (как FindCashierShiftOnOtherCashbox кассы).
      if (shift != null && found.movedCashbox && shift.cashboxId != null) {
        await ref.read(sessionProvider.notifier).chooseCashbox(Cashbox(shift.cashboxId!, shift.cashboxName ?? s.cashboxName ?? ''));
      }
      state = ShiftState(shift: shift, foreignShift: shift == null && found.foreignOnThisCashbox, fetchedAt: DateTime.now());
      if (shift != null) {
        await _remember(shift);
      } else {
        await _forget();
      }
    } on ApiException catch (e) {
      // Без связи — работаем по последней известной СВОЕЙ смене (продажи уйдут в очередь с ней и временем продажи).
      final cached = state.shift == null ? await _cached() : null;
      state = ShiftState(
        shift: state.shift ?? (cached != null ? ShiftInfo(id: cached, cashboxId: s.cashboxId) : null),
        error: e.message,
        foreignShift: state.foreignShift,
        cached: true,
        fetchedAt: state.fetchedAt,
      );
    }
  }

  Future<void> open(double openingCash) async {
    final s = ref.read(sessionProvider)!;
    final shift = await ref.read(shiftRepoProvider).open(s.cashboxId!, openingCash);
    state = ShiftState(shift: shift);
    Audit.log('shift', 'Смена открыта', amount: openingCash);
    await _remember(shift);
    await refresh();
  }

  Future<void> close(double? closingCash) async {
    final id = state.shift?.id;
    if (id == null) return;
    await ref.read(shiftRepoProvider).close(id, closingCash);
    Audit.log('shift', 'Смена закрыта', amount: closingCash);
    await _forget();
    state = const ShiftState();
  }
}

final shiftProvider = NotifierProvider<ShiftController, ShiftState>(ShiftController.new);

// ------------------------------------------------------------------ чек

class CartController extends Notifier<Cart> {
  @override
  Cart build() {
    // 2026-10-26 (живой аудит, П3, В-1): чек пишется в настройки телефона при каждом изменении — переживает перезапуск
    // приложения (Android выгружает его в фоне). Схема базы не меняется: обычная запись `cart.draft`.
    listenSelf((_, next) => _persist(next));
    return const Cart();
  }

  /// Незавершённый чек: {owner, ws, cart}. Привязан к аккаунту (как очередь, OutboxMeta.ownerOf); пустой чек = '' (стёрт).
  static const draftKey = 'cart.draft';
  Future<void> _writing = Future.value();

  /// 2026-10-29: дождаться записи черновика (выход стирает базу — отложенная запись не должна её воскресить).
  Future<void> flush() => _writing;

  void _persist(Cart cart) {
    try {
      final owner = OutboxMeta.ownerOf(ref.read(sessionProvider));
      if (!cart.isEmpty && owner.isEmpty) return; // сессии нет (истекла) — старый черновик не трогаем
      final value = cart.isEmpty
          ? ''
          : jsonEncode({'owner': owner, 'ws': wholesaleMode, 'cart': HeldCart(id: 'draft', at: DateTime.now(), note: '', cart: cart).toJson()});
      final db = ref.read(databaseProvider);
      // записи идут по очереди, чтобы старое состояние не затёрло новое
      _writing = _writing.then((_) => db.setSetting(draftKey, value)).catchError((_) {});
    } catch (_) {
      // базы нет (например, в тесте без неё) — чек просто остаётся в памяти
    }
  }

  /// Вернуть незавершённый чек после запуска или входа: только своего аккаунта и только в пустой чек.
  Future<void> restoreDraft() async {
    try {
      final raw = await ref.read(databaseProvider).getSetting(draftKey);
      if (raw == null || raw.isEmpty) return;
      final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
      if (m['owner'] != OutboxMeta.ownerOf(ref.read(sessionProvider))) return;
      if (!state.isEmpty) return;
      final held = HeldCart.fromJson((m['cart'] as Map).cast<String, dynamic>());
      if (held.cart.isEmpty) return;
      wholesaleMode = m['ws'] == true;
      state = held.cart;
    } catch (_) {
      // испорченный черновик — игнорируем, продавать это не мешает
    }
  }

  /// 13.10, «продажа оптом и в розницу»: в режиме «Опт» товар с оптовой ценой сразу идёт по опту.
  bool wholesaleMode = false;

  /// [piece] — штука из упаковки (2026-10-27, М4): цена строки — цена штуки, в чек уходит sale_package_id; опт у штуки не действует.
  /// [packName] — название упаковки товара с поштучной опцией (подпись строки).
  void add(Product p, {double qty = 1, String? variantId, String? variantLabel, double? variantPrice, PiecePack? piece, String? packName}) {
    final line = CartLine(
      productId: p.id,
      name: variantLabel == null ? p.name : '${p.name} ($variantLabel)',
      price: piece?.piecePrice ?? variantPrice ?? p.price,
      qty: qty,
      isWeight: p.isWeight,
      unit: piece == null ? p.unit : 'шт',
      wholesalePrice: variantId == null && piece == null ? p.wholesalePrice : 0,
      variantId: variantId,
      salePackageId: piece?.id,
      perPack: piece?.qty ?? 1,
      packName: packName,
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

  /// 2026-10-29 (аудит мобилки, п. 8): цены строк по ответу сервера («Цена изменилась: A → B») — розница и опт обновляются,
  /// цена строки берётся по её типу; скидка строки не больше новой суммы.
  void applyPriceChanges(List<PriceChange> changes) {
    if (changes.isEmpty) return;
    final lines = [...state.lines];
    for (final l in lines) {
      if (l.custom || l.variantId != null || l.salePackageId != null) continue;
      for (final c in changes) {
        if (c.productId != l.productId) continue;
        if (c.retail != null) l.retailPrice = c.retail!;
        if (c.wholesale != null && c.wholesale! > 0) l.wholesalePrice = c.wholesale!;
        if (c.retail == null && c.wholesale == null) {
          if (l.wholesale) {
            l.wholesalePrice = c.newPrice;
          } else {
            l.retailPrice = c.newPrice;
          }
        }
        l.price = l.wholesale && l.wholesalePrice > 0 ? l.wholesalePrice : l.retailPrice;
        if (l.discount > l.gross) l.discount = l.gross;
      }
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
