import 'package:drift/drift.dart' hide Column;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../../l10n/app_localizations.dart';
import '../receipt/receipt.dart';
import 'admin_screens.dart';
import 'staff_form.dart';
import 'waybill_pdf.dart';

/// Сотрудник и его доступы (поля can_* — как в «Сотрудниках» сайта и кассы).
// 2026-10-28 (сотрудники): кроме id/имени/роли/доступов хранится весь ответ сервера [raw] — неизвестные поля не теряются, а
// форма показывает только те, что сервер вернул (feature-detect). Прежний конструктор (4 позиционных поля) не изменился.
class Employee {
  final String id;
  final String name;
  final String role;
  final Map<String, bool> rights;
  final Map<String, dynamic> raw;
  const Employee(this.id, this.name, this.role, this.rights, {this.raw = const {}});

  /// Разбор строки списка / ответа создания. Любые пустые и неполные поля — безопасны.
  factory Employee.fromJson(Map<String, dynamic> m) {
    final first = (str(m, 'first_name') ?? '').trim();
    final last = (str(m, 'last_name') ?? '').trim();
    final joined = [first, last].where((x) => x.isNotEmpty).join(' ');
    return Employee(
      str(m, 'id') ?? str(m, 'user_id') ?? '',
      joined.isNotEmpty ? joined : (str(m, 'full_name') ?? str(m, 'email') ?? ''),
      str(m, 'role_display') ?? str(m, 'custom_role_name') ?? str(m, 'role') ?? '',
      {
        for (final k in staffRights.keys) k: m[k] == true,
        // допуск к кассе (Б-16) — только если сервер уже отдаёт
        for (final k in staffRightsOptional.keys) if (m.containsKey(k)) k: m[k] == true,
      },
      raw: Map.unmodifiable(m),
    );
  }

  String field(String key) => (str(raw, key) ?? '').trim();
  String get firstName => field('first_name');
  String get lastName => field('last_name');
  String get email => field('email');
  String get phone => field('phone_number').isNotEmpty ? field('phone_number') : field('phone');
  String get trackNumber => field('track_number');

  /// Номер роли (custom_role) — для выбора должности в форме.
  String get roleId => field('custom_role');
  bool has(String key) => raw.containsKey(key);

  /// Ключ признака «работает», который вернул сервер (is_active / active); null — сервер признака не отдаёт.
  String? get activeKey => raw['is_active'] is bool ? 'is_active' : (raw['active'] is bool ? 'active' : null);
  bool? get active => activeKey == null ? null : raw[activeKey] as bool;
  bool get dismissed => active == false;
  bool get isOwnerRole => raw['is_owner'] == true || field('role').toLowerCase() == 'owner';
}

/// Т13: права, которые макет показывает на телефоне, и поля сервера за ними.
/// 2026-10-22 (мобилка, админ, П9): «Продажа» — can_view_sale, как на сайте и ПК (было can_view_cashier — это «Интерфейс
/// кассира»); добавлены «Интерфейс кассира», «Заказы с сайта» (can_view_orders), «Закупки» и «Поставщики».
const staffRights = <String, String>{
  'can_view_sale': 'rightSale',
  'can_view_cashier': 'rightCashier',
  'can_view_market_employee_return': 'rightReturn',
  'can_view_market_discount': 'rightDiscount',
  'can_view_market_edit_price': 'rightEditPrice',
  'can_view_market_delete_cart_item': 'rightDeleteLine',
  'can_view_products': 'rightStock',
  'can_view_analytics': 'rightReports',
  'can_view_shifts': 'rightShifts',
  'can_view_clients': 'rightClients',
  'can_view_orders': 'rightOrders',
  'can_view_market_procurement': 'rightProcurement',
  'can_view_market_supplier': 'rightSuppliers',
  'can_view_settings': 'rightSettings',
  // 2026-10-28 (сотрудники): флаги зарплаты и сотрудников из схемы сервера (EmployeeUpdate) — раньше менялись только на сайте.
  'can_view_salary': 'rightSalaryView',
  'can_edit_salary': 'rightSalaryEdit',
  'can_view_employees': 'rightEmployeesView',
  'can_edit_employees': 'rightEmployeesEdit',
};

/// 2026-10-28 (сотрудники): допуск к работе на кассе (Б-16) — показывается и правится, только когда сервер уже отдаёт поле.
const staffRightsOptional = <String, String>{'can_use_pos': 'rightUsePos'};

/// 2026-10-22 (мобилка, админ, П9): свои права снять нельзя (иначе администратор запирает сам себя «Настройками»):
/// у себя включённые доступы остаются включёнными, включать новые можно.
Map<String, bool> guardSelfRights(Map<String, bool> before, Map<String, bool> after, {required bool self}) {
  if (!self) return after;
  return {for (final k in {...before.keys, ...after.keys}) k: (before[k] ?? false) || (after[k] ?? false)};
}

/// Сотрудник — тот, кто сейчас вошёл: по id пользователя сессии.
bool isSelfEmployee(Employee e, String? userId) => userId != null && userId.isNotEmpty && e.id == userId;

String rightTitle(BuildContext c, String key) => switch (staffRights[key] ?? staffRightsOptional[key]) {
  'rightSale' => c.l.rightSale,
  'rightCashier' => c.l.rightCashier,
  'rightOrders' => c.l.rightOrders,
  'rightProcurement' => c.l.rightProcurement,
  'rightSuppliers' => c.l.rightSuppliers,
  'rightReturn' => c.l.rightReturn,
  'rightDiscount' => c.l.rightDiscount,
  'rightEditPrice' => c.l.rightEditPrice,
  'rightDeleteLine' => c.l.rightDeleteLine,
  'rightStock' => c.l.rightStock,
  'rightReports' => c.l.rightReports,
  'rightShifts' => c.l.rightShifts,
  'rightClients' => c.l.rightClients,
  'rightSalaryView' => c.l.rightSalaryView,
  'rightSalaryEdit' => c.l.rightSalaryEdit,
  'rightEmployeesView' => c.l.rightEmployeesView,
  'rightEmployeesEdit' => c.l.rightEmployeesEdit,
  'rightUsePos' => c.l.rightUsePos,
  _ => c.l.rightSettings,
};

/// Результат создания сотрудника: пароль сервер отдаёт один раз (null — не вернул или ответ потерялся, сотрудник найден в списке).
class CreatedEmployee {
  final Employee employee;
  final String? password;
  final bool recovered;
  const CreatedEmployee(this.employee, this.password, {this.recovered = false});
}

/// Понятный текст ошибки сотрудников по коду ответа (400/403/404/409/429/5xx и обрыв связи).
String staffErrorText(AppLocalizations l, Object e) {
  if (e is! ApiException) return l.apiNetworkError;
  final p = e.payload;
  switch (e.status) {
    case 400:
    case 422:
      if (p is Map && p['email'] != null) return l.empErrEmail;
      if (p is Map && (p['phone_number'] != null || p['phone'] != null)) return l.empPhoneBad;
      return e.message.isNotEmpty ? e.message : l.empErrBad;
    case 403:
      return l.empErr403;
    case 404:
      return l.empErr404;
    case 409:
      return l.empErr409;
    case 429:
      return l.empErr429;
  }
  final s = e.status;
  if (s != null && s >= 500) return l.empErr5xx;
  return e.message; // обрыв связи / тайм-аут — уже на языке приложения (ApiText)
}

class StaffRepository {
  final ApiClient api;
  StaffRepository(this.api);

  static const _base = 'api/users/employees/';
  static const _secretKeys = {'password', 'generated_password', 'temp_password', 'default_password'};

  /// GET api/users/employees/ — подтверждено живым запросом (касса, 21.09).
  /// 2026-10-22 (мобилка, админ, П9): все страницы списка (раньше — только первая).
  // 2026-10-28 (сотрудники): повторы по id отбрасываются (сервер без пагинации отдавал один и тот же список на каждой странице),
  // «страницы закончились» (404 на странице > 1) — не ошибка, потолок 60 страниц × 100 = 6000 сотрудников.
  Future<List<Employee>> list() async {
    final out = <Employee>[];
    final seen = <String>{};
    for (var page = 1; page <= 60; page++) {
      dynamic data;
      try {
        data = await api.get(_base, query: {'page': page, 'page_size': 100});
      } on ApiException catch (e) {
        if (page > 1 && e.status == 404) break;
        rethrow;
      }
      final items = asList(data);
      var fresh = 0;
      for (final m in items) {
        final e = Employee.fromJson(m);
        if (seen.add(e.id.isEmpty ? 'row${out.length}' : e.id)) {
          out.add(e);
          fresh++;
        }
      }
      final hasNext = data is Map ? data['next'] != null : items.length >= 100;
      if (items.isEmpty || fresh == 0 || !hasNext) break;
    }
    return out;
  }

  /// Все ключи, которые сервер вернул хоть у одного сотрудника (какие поля показывать в форме создания).
  static Set<String> keysOf(Iterable<Employee> list) => {for (final e in list) ...e.raw.keys};

  /// Должности компании (api/users/roles/). Нет доступа / нет адреса — пустой список, форма работает без выбора должности.
  Future<List<({String id, String name})>> roles() async {
    for (final path in const ['api/users/roles/', 'api/users/company/roles/', 'api/main/roles/']) {
      try {
        final rows = asList(await api.get(path));
        return [
          for (final m in rows)
            if ((str(m, 'id') ?? '').isNotEmpty) (id: str(m, 'id')!, name: str(m, 'name') ?? str(m, 'title') ?? str(m, 'id')!),
        ];
      } on ApiException catch (e) {
        if (e.status == 404 || e.status == 405) continue;
        return const [];
      }
    }
    return const [];
  }

  /// POST api/users/employees/create/ (как у кассы; запасной адрес — сам список). Если ответ не дошёл (обрыв, 5xx), сотрудник мог
  /// создаться: ищем его в списке по почте и не создаём второй раз.
  Future<CreatedEmployee> create(Map<String, dynamic> body) async {
    ApiException? last;
    for (final path in const ['api/users/employees/create/', _base]) {
      try {
        return await _parseCreated(await api.post(path, body: body), body);
      } on ApiException catch (e) {
        if (e.status == 404 || e.status == 405) {
          last = e;
          continue;
        }
        if (e.isTransient && e.status != 429 && e.status != 409) {
          final found = await _findByEmail('${body['email'] ?? ''}');
          if (found != null) return CreatedEmployee(found, null, recovered: true);
        }
        rethrow;
      }
    }
    throw last ?? ApiException('not found', status: 404);
  }

  Future<CreatedEmployee> _parseCreated(dynamic data, Map<String, dynamic> sent) async {
    final top = data is Map ? data.cast<String, dynamic>() : <String, dynamic>{};
    var inner = top;
    for (final k in const ['user', 'employee', 'data']) {
      if (top[k] is Map) {
        inner = (top[k] as Map).cast<String, dynamic>();
        break;
      }
    }
    String? password;
    for (final src in [top, inner]) {
      for (final k in _secretKeys) {
        final v = src[k];
        if (v is String && v.isNotEmpty) password ??= v;
      }
    }
    // пароль в карточке не храним
    final clean = {...inner}..removeWhere((k, _) => _secretKeys.contains(k));
    var e = Employee.fromJson({...sent, ...clean});
    if (e.id.isEmpty) {
      final id = str(top, 'id') ?? str(top, 'user_id') ?? str(top, 'employee_id') ?? '';
      if (id.isNotEmpty) {
        e = Employee.fromJson({...sent, ...clean, 'id': id});
      } else {
        e = await _findByEmail('${sent['email'] ?? ''}') ?? e;
      }
    }
    return CreatedEmployee(e, password);
  }

  Future<Employee?> _findByEmail(String email) async {
    if (email.trim().isEmpty) return null;
    try {
      for (final e in await list()) {
        if (e.email.toLowerCase() == email.trim().toLowerCase()) return e;
      }
    } catch (_) {
      // список не прочитан — сообщим обычной ошибкой
    }
    return null;
  }

  /// PATCH только переданных полей (как UpdateEmployeeAccessAsync кассы — тот же перебор адресов). Пустой набор — запроса нет.
  Future<void> update(String id, Map<String, dynamic> fields) async {
    if (fields.isEmpty) return;
    ApiException? last;
    for (final path in ['api/users/employees/$id/', 'api/users/employees/$id/update/', 'api/users/employees/update/$id/']) {
      try {
        await api.patch(path, body: fields);
        return;
      } on ApiException catch (e) {
        if (e.status != 404 && e.status != 405) rethrow;
        last = e;
      }
    }
    throw last ?? ApiException('not found', status: 404);
  }

  /// Изменить доступы: PATCH только can_* (прежнее имя метода).
  Future<void> save(String id, Map<String, bool> rights) => update(id, rights);

  /// Убрать сотрудника, когда сервер не отдаёт признак «работает»: DELETE и проверка, что id пропал из списка (как у кассы:
  /// сервер бывает отвечает 2xx, но запись остаётся).
  Future<void> remove(String id) async {
    var done = false;
    ApiException? last;
    for (final path in ['api/users/employees/$id/', 'api/users/employees/$id/delete/', 'api/users/employees/delete/$id/']) {
      try {
        await api.delete(path);
        done = true;
        break;
      } on ApiException catch (e) {
        if (e.status != 404 && e.status != 405) rethrow;
        last = e;
      }
    }
    if (!done) throw last ?? ApiException('not found', status: 404);
    final still = (await list()).any((e) => e.id == id);
    if (still) throw ApiException(ApiText.l?.empDismissStill ?? 'Сервер принял запрос, но сотрудник остался в списке.', status: 409);
  }

  /// POST users/employees/{id}/reset-password/ → новый пароль (показывается один раз). Адреса ещё нет — 404.
  Future<String> resetPassword(String id) async {
    final data = await api.post('api/users/employees/$id/reset-password/', body: const <String, dynamic>{});
    final p = data is Map ? data['password'] : null;
    if (p is String && p.isNotEmpty) return p;
    throw ApiException('no password', status: 404);
  }
}

final staffRepoProvider = Provider((ref) => StaffRepository(ref.watch(apiProvider)));

enum _StaffFilter { all, active, dismissed }

/// Т13: сотрудники и права. 2026-10-28: поиск, «Работают / Уволены», карточка сотрудника с личными данными (создание,
/// правка, увольнение), все страницы списка.
class StaffScreen extends ConsumerStatefulWidget {
  const StaffScreen({super.key});
  @override
  ConsumerState<StaffScreen> createState() => _StaffScreenState();
}

class _StaffScreenState extends ConsumerState<StaffScreen> {
  List<Employee>? _rows;
  String? _error;
  final _query = TextEditingController();
  _StaffFilter _filter = _StaffFilter.all;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final rows = await ref.read(staffRepoProvider).list();
      if (mounted) {
        setState(() {
          _rows = rows;
          _error = null;
        });
      }
    } catch (e) {
      if (!mounted) return;
      final l = context.l;
      final msg = e is ApiException && e.status == 403 ? l.empListDenied : staffErrorText(l, e);
      if (_rows == null) {
        setState(() => _error = msg);
      } else {
        toast(context, msg, error: true); // список уже был на экране — не стираем его из-за сбоя обновления
      }
    }
  }

  Future<void> _open(Employee? e) async {
    final saved = await Navigator.of(context).push<bool>(
      MaterialPageRoute(builder: (_) => StaffFormScreen(employee: e, knownKeys: StaffRepository.keysOf(_rows ?? const []))),
    );
    if (saved == true && mounted) await _load();
  }

  static String _digits(String s) => s.replaceAll(RegExp(r'\D'), '');

  List<Employee> _visible() {
    final q = _query.text.trim().toLowerCase();
    final qd = _digits(q);
    return [
      for (final e in _rows ?? const <Employee>[])
        if (switch (_filter) {
              _StaffFilter.all => true,
              _StaffFilter.active => !e.dismissed,
              _StaffFilter.dismissed => e.dismissed,
            } &&
            (q.isEmpty ||
                e.name.toLowerCase().contains(q) ||
                e.email.toLowerCase().contains(q) ||
                e.role.toLowerCase().contains(q) ||
                (qd.length >= 3 && _digits(e.phone).contains(qd))))
          e,
    ];
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    final s = ref.watch(sessionProvider);
    final rows = _rows;
    final hasDismissed = rows?.any((e) => e.dismissed) ?? false;
    final list = rows == null ? const <Employee>[] : _visible();
    return Scaffold(
      appBar: ScreenTitle(l.staff, subtitle: rows == null ? null : l.empCount(rows.length)),
      floatingActionButton: rows != null && (s?.canEditStaff ?? false)
          ? FloatingActionButton.extended(onPressed: () => _open(null), icon: const Icon(Icons.person_add_alt_1_rounded), label: Text(l.empAdd))
          : null,
      body: rows == null
          ? Center(
              child: _error == null
                  ? const CircularProgressIndicator()
                  : Padding(
                      padding: const EdgeInsets.all(24),
                      child: Column(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(_error!, textAlign: TextAlign.center),
                          const SizedBox(height: 12),
                          OutlinedButton(
                            onPressed: () {
                              setState(() => _error = null);
                              _load();
                            },
                            child: Text(l.retry),
                          ),
                        ],
                      ),
                    ),
            )
          : Column(
              children: [
                Padding(
                  padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
                  child: TextField(
                    key: const Key('staffSearch'),
                    controller: _query,
                    onChanged: (_) => setState(() {}),
                    textInputAction: TextInputAction.search,
                    decoration: InputDecoration(
                      hintText: l.empSearch,
                      prefixIcon: const Icon(Icons.search_rounded),
                      suffixIcon: _query.text.isEmpty
                          ? null
                          : IconButton(
                              icon: const Icon(Icons.close_rounded),
                              onPressed: () => setState(_query.clear),
                            ),
                    ),
                  ),
                ),
                if (hasDismissed)
                  Padding(
                    padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
                    child: Align(
                      alignment: Alignment.centerLeft,
                      child: Wrap(
                        spacing: 8,
                        runSpacing: 4,
                        children: [
                          for (final f in _StaffFilter.values)
                            ChoiceChip(
                              key: Key('staffFilter_${f.name}'),
                              label: Text(switch (f) {
                                _StaffFilter.all => l.all,
                                _StaffFilter.active => l.empFilterActive,
                                _StaffFilter.dismissed => l.empFilterDismissed,
                              }),
                              selected: _filter == f,
                              onSelected: (_) => setState(() => _filter = f),
                            ),
                        ],
                      ),
                    ),
                  ),
                Expanded(
                  child: RefreshIndicator(
                    onRefresh: _load,
                    child: list.isEmpty
                        ? ListView(children: [Padding(padding: const EdgeInsets.all(32), child: Center(child: Text(l.empNothingFound)))])
                        : ListView.separated(
                            padding: const EdgeInsets.fromLTRB(16, 4, 16, 96),
                            itemCount: list.length,
                            separatorBuilder: (_, _) => const SizedBox(height: 8),
                            itemBuilder: (c, i) {
                              final e = list[i];
                              final on = e.rights.values.where((v) => v).length;
                              final soft = c.pal.textSoft;
                              return Panel(
                                onTap: () => _open(e),
                                child: Row(
                                  children: [
                                    CircleAvatar(
                                      backgroundColor: e.dismissed ? soft : T.navy,
                                      child: Text(
                                        initials(e.name).toUpperCase(),
                                        style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800),
                                      ),
                                    ),
                                    const SizedBox(width: 12),
                                    Expanded(
                                      child: Column(
                                        crossAxisAlignment: CrossAxisAlignment.start,
                                        children: [
                                          Text(
                                            e.name.isEmpty ? '—' : e.name,
                                            maxLines: 1,
                                            overflow: TextOverflow.ellipsis,
                                            style: TextStyle(
                                              fontWeight: FontWeight.w700,
                                              color: e.dismissed ? soft : null,
                                              decoration: e.dismissed ? TextDecoration.lineThrough : null,
                                            ),
                                          ),
                                          Text(
                                            [
                                              if (e.role.isNotEmpty) e.role,
                                              '$on/${e.rights.length}',
                                              if (e.dismissed) l.empDismissedTag,
                                              if (isSelfEmployee(e, s?.userId)) l.staffYou,
                                            ].join(' · '),
                                            maxLines: 2,
                                            overflow: TextOverflow.ellipsis,
                                            style: TextStyle(fontSize: 12, color: soft),
                                          ),
                                          if (e.phone.isNotEmpty)
                                            Text(e.phone, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 12, color: soft)),
                                        ],
                                      ),
                                    ),
                                    const Icon(Icons.chevron_right_rounded),
                                  ],
                                ),
                              );
                            },
                          ),
                  ),
                ),
              ],
            ),
    );
  }
}

/// Т24: уведомления администратора — собраны из данных сервера и телефона: новые заказы с сайта, что заканчивается,
/// большие долги, неотправленные записи телефона. Нажатие — переход в нужный раздел.
class NotificationsScreen extends ConsumerStatefulWidget {
  const NotificationsScreen({super.key});
  @override
  ConsumerState<NotificationsScreen> createState() => _NotificationsScreenState();
}

class _NotificationsScreenState extends ConsumerState<NotificationsScreen> {
  final List<(IconData, Color, Color, String, String, String)> _items = [];
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final l = context.l;
    final pal = context.pal; // цвета берём до запросов к серверу
    final items = <(IconData, Color, Color, String, String, String)>[];
    try {
      for (final o in (await ref.read(adminRepoProvider).orders()).where((o) => o.status == 'new')) {
        items.add((
          Icons.shopping_bag_outlined,
          pal.successSoft,
          T.success,
          l.notifyNewOrder,
          '№${o.number} · ${money(o.total)} ${l.som} · ${o.customer}',
          '/admin/orders',
        ));
      }
    } on ApiException {
      // без связи
    }
    final db = ref.read(databaseProvider);
    // 2026-10-22 (мобилка, админ, П11): порог «заканчивается» как на ПК — остаток меньше 10 (было ≤ 3).
    final low = await (db.select(db.products)..where((p) => lowStockSql(p) & p.isService.equals(false))).get();
    if (low.isNotEmpty) items.add((Icons.inventory_2_outlined, pal.warnSoft, T.warn, l.notifyRunningOut, l.notifyRunningOutSub(low.length), '/admin/purchase'));
    try {
      for (final d in (await ref.read(opsRepoProvider).debtors()).take(3)) {
        if (d.debt <= 0) continue;
        items.add((Icons.schedule_rounded, pal.dangerSoft, T.danger, l.notifyBigDebt, '${d.name} · ${money(d.debt)} ${l.som}', '/debts'));
      }
    } on ApiException {
      // без связи
    }
    final pending = (await db.pending()).length;
    if (pending > 0) items.add((Icons.cloud_off_rounded, pal.warnSoft, T.warn, l.notifyNotSent, l.waitingToSend(pending), '/sync'));
    // 2026-10-22 (мобилка, админ, П9): только то, что сотруднику можно открыть (заказы — с правом «Заказы» и т. п.).
    final s = ref.read(sessionProvider);
    if (s != null) items.removeWhere((x) => !s.canRoute(x.$6));
    if (mounted) {
      setState(() {
        _items
          ..clear()
          ..addAll(items);
        _loading = false;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      appBar: ScreenTitle(l.notifications),
      body: _loading
          ? const Center(child: CircularProgressIndicator())
          : RefreshIndicator(
              onRefresh: _load,
              child: _items.isEmpty
                  ? ListView(
                      children: [
                        Padding(
                          padding: const EdgeInsets.all(32),
                          child: Center(child: Text(l.noNotifications)),
                        ),
                      ],
                    )
                  : ListView.separated(
                      padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
                      itemCount: _items.length,
                      separatorBuilder: (_, _) => const SizedBox(height: 8),
                      itemBuilder: (c, i) {
                        final (icon, bg, fg, title, sub, route) = _items[i];
                        return Panel(
                          onTap: () => context.push(route),
                          child: Row(
                            children: [
                              Container(
                                width: 40,
                                height: 40,
                                decoration: BoxDecoration(color: bg, borderRadius: BorderRadius.circular(12)),
                                child: Icon(icon, color: fg, size: 20),
                              ),
                              const SizedBox(width: 12),
                              Expanded(
                                child: Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Text(title, style: const TextStyle(fontWeight: FontWeight.w700)),
                                    Text(sub, style: TextStyle(fontSize: 12, color: c.pal.textSoft)),
                                  ],
                                ),
                              ),
                              const Icon(Icons.chevron_right_rounded),
                            ],
                          ),
                        );
                      },
                    ),
            ),
    );
  }
}

/// Д5: выбор документа по чеку — товарный чек, накладная, счёт — и отправка PDF.
Future<void> chooseDocPdf(BuildContext context, Receipt r) async {
  final l = context.l;
  final kind = await showModalBottomSheet<DocKind>(
    context: context,
    builder: (c) => SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          ListTile(
            title: Text(l.documentPdf, style: const TextStyle(fontWeight: FontWeight.w800)),
          ),
          ListTile(leading: const Icon(Icons.receipt_outlined), title: Text(l.docSalesReceipt), onTap: () => Navigator.pop(c, DocKind.salesReceipt)),
          ListTile(leading: const Icon(Icons.description_outlined), title: Text(l.docWaybill), onTap: () => Navigator.pop(c, DocKind.waybill)),
          ListTile(leading: const Icon(Icons.request_quote_outlined), title: Text(l.docInvoice), onTap: () => Navigator.pop(c, DocKind.invoice)),
        ],
      ),
    ),
  );
  if (kind != null) await shareDocPdf(r, kind);
}
