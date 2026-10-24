import 'package:drift/drift.dart' hide Column;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../core/api/api_client.dart';
import '../../core/providers.dart';
import '../../core/theme/tokens.dart';
import '../../core/util/product_picker.dart';
import '../../core/util/ui.dart';
import '../receipt/receipt.dart';
import 'admin_screens.dart';
import 'waybill_pdf.dart';

/// Сотрудник и его доступы (поля can_view_* — как в «Сотрудниках» сайта и кассы).
class Employee {
  final String id;
  final String name;
  final String role;
  final Map<String, bool> rights;
  const Employee(this.id, this.name, this.role, this.rights);
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
};

/// 2026-10-22 (мобилка, админ, П9): свои права снять нельзя (иначе администратор запирает сам себя «Настройками»):
/// у себя включённые доступы остаются включёнными, включать новые можно.
Map<String, bool> guardSelfRights(Map<String, bool> before, Map<String, bool> after, {required bool self}) {
  if (!self) return after;
  return {for (final k in {...before.keys, ...after.keys}) k: (before[k] ?? false) || (after[k] ?? false)};
}

/// Сотрудник — тот, кто сейчас вошёл: по id пользователя сессии.
bool isSelfEmployee(Employee e, String? userId) => userId != null && userId.isNotEmpty && e.id == userId;

String rightTitle(BuildContext c, String key) => switch (staffRights[key]) {
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
  _ => c.l.rightSettings,
};

class StaffRepository {
  final ApiClient api;
  StaffRepository(this.api);

  /// GET api/users/employees/ — подтверждено живым запросом (касса, 21.09).
  /// 2026-10-22 (мобилка, админ, П9): все страницы списка (раньше — только первая).
  Future<List<Employee>> list() async {
    final rows = await getAllPages(api, 'api/users/employees/', pageSize: 100, maxPages: 20);
    return [
      for (final m in rows)
        Employee(
          str(m, 'id') ?? '',
          [str(m, 'first_name'), str(m, 'last_name')].whereType<String>().where((x) => x.isNotEmpty).join(' ').trim().isNotEmpty
              ? [str(m, 'first_name'), str(m, 'last_name')].whereType<String>().where((x) => x.isNotEmpty).join(' ')
              : (str(m, 'full_name') ?? str(m, 'email') ?? ''),
          str(m, 'role_display') ?? str(m, 'custom_role_name') ?? str(m, 'role') ?? '',
          {for (final k in staffRights.keys) k: m[k] == true},
        ),
    ];
  }

  /// Изменить доступы: PATCH только can_view_* (как UpdateEmployeeAccessAsync кассы — тот же перебор адресов).
  Future<void> save(String id, Map<String, bool> rights) async {
    ApiException? last;
    for (final path in ['api/users/employees/$id/', 'api/users/employees/$id/update/', 'api/users/employees/update/$id/']) {
      try {
        await api.patch(path, body: rights);
        return;
      } on ApiException catch (e) {
        if (e.status != 404 && e.status != 405) rethrow;
        last = e;
      }
    }
    throw last ?? ApiException('not found');
  }
}

final staffRepoProvider = Provider((ref) => StaffRepository(ref.watch(apiProvider)));

/// Т13: сотрудники и права — переключатели доступов, сохранение на сервер.
class StaffScreen extends ConsumerStatefulWidget {
  const StaffScreen({super.key});
  @override
  ConsumerState<StaffScreen> createState() => _StaffScreenState();
}

class _StaffScreenState extends ConsumerState<StaffScreen> {
  List<Employee>? _rows;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    try {
      final rows = await ref.read(staffRepoProvider).list();
      if (mounted) setState(() => _rows = rows);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
    }
  }

  Future<void> _edit(Employee e) async {
    final l = context.l;
    final rights = Map<String, bool>.from(e.rights);
    final self = isSelfEmployee(e, ref.read(sessionProvider)?.userId);
    final ok = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      builder: (c) => StatefulBuilder(
        builder: (c, set) => SafeArea(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 12),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(e.name, style: const TextStyle(fontWeight: FontWeight.w800, fontSize: 18)),
                Text(e.role, style: TextStyle(color: c.pal.textSoft)),
                if (self)
                  Padding(
                    padding: const EdgeInsets.only(top: 6),
                    child: Text(l.rightSelfLocked, style: const TextStyle(color: T.warn, fontSize: 12.5, fontWeight: FontWeight.w600)),
                  ),
                const SizedBox(height: 8),
                Flexible(
                  child: ListView(
                    shrinkWrap: true,
                    children: [
                      for (final k in staffRights.keys)
                        SwitchListTile(
                          value: rights[k] ?? false,
                          title: Text(rightTitle(c, k)),
                          // 2026-10-22 (мобилка, админ, П9): у себя включённое право не выключается.
                          onChanged: self && (e.rights[k] ?? false) ? null : (v) => set(() => rights[k] = v),
                        ),
                    ],
                  ),
                ),
                FilledButton(
                  style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
                  onPressed: () => Navigator.pop(c, true),
                  child: Text(l.save),
                ),
              ],
            ),
          ),
        ),
      ),
    );
    if (ok != true || !mounted) return;
    try {
      await ref.read(staffRepoProvider).save(e.id, guardSelfRights(e.rights, rights, self: self));
      if (!mounted) return;
      toast(context, l.rightsSaved);
      await _load();
    } on ApiException catch (x) {
      if (mounted) toast(context, x.message, error: true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l;
    return Scaffold(
      appBar: ScreenTitle(l.staff),
      body: _rows == null
          ? Center(child: _error == null ? const CircularProgressIndicator() : Text(_error!))
          : ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 4, 16, 24),
              itemCount: _rows!.length,
              separatorBuilder: (_, _) => const SizedBox(height: 8),
              itemBuilder: (c, i) {
                final e = _rows![i];
                final on = e.rights.values.where((v) => v).length;
                return Panel(
                  onTap: () => _edit(e),
                  child: Row(
                    children: [
                      CircleAvatar(
                        backgroundColor: T.navy,
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
                            Text(e.name, style: const TextStyle(fontWeight: FontWeight.w700)),
                            Text(
                              '${e.role} · $on/${staffRights.length}${isSelfEmployee(e, ref.read(sessionProvider)?.userId) ? ' · ${l.staffYou}' : ''}',
                              style: TextStyle(fontSize: 12, color: c.pal.textSoft),
                            ),
                          ],
                        ),
                      ),
                      const Icon(Icons.chevron_right_rounded),
                    ],
                  ),
                );
              },
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
