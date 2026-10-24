import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Сессия кассира: токены и выбранная касса — в защищённом хранилище (ТЗ, раздел 1: flutter_secure_storage).
class Session {
  final String email;
  final String access;
  final String refresh;
  final String userId;
  final String userName;
  final String role;
  final String companyName;

  /// 13.10: реквизиты продавца для чека (ИНН, адрес) — из api/users/company/.
  final String companyInn;
  final String companyAddress;
  final bool isOwner;
  final String? cashboxId;
  final String? cashboxName;

  /// 13.10, владелец: «доступы в мобилке работают?» — доступы сотрудника с сервера (флаги can_view_* профиля, как у кассы
  /// PermissionService). null — профиль ещё не загружался (вход до 13.10): тогда по-старому, по роли.
  final List<String>? perms;

  const Session({
    required this.email,
    required this.access,
    required this.refresh,
    this.userId = '',
    this.userName = '',
    this.role = '',
    this.companyName = '',
    this.companyInn = '',
    this.companyAddress = '',
    this.isOwner = false,
    this.cashboxId,
    this.cashboxName,
    this.perms,
  });

  static const viewProducts = 'can_view_products';
  static const viewAnalytics = 'can_view_analytics';
  static const viewClients = 'can_view_clients';
  static const viewOrders = 'can_view_orders';
  static const viewShifts = 'can_view_shifts';
  static const viewSettings = 'can_view_settings';
  static const discount = 'can_view_market_discount';
  static const deleteLine = 'can_view_market_delete_cart_item';
  static const employeeReturn = 'can_view_market_employee_return';
  // 2026-10-22 (мобилка, админ, П9): отдельные права, как PosPermissions кассы. «Продажа» на сайте — can_view_sale
  // (can_view_cashier — «Интерфейс кассира»); цена, закупки и поставщики — свои флаги, а не общий «Склад».
  static const sale = 'can_view_sale';
  static const cashier = 'can_view_cashier';
  static const editPrice = 'can_view_market_edit_price';
  static const procurement = 'can_view_market_procurement';
  static const supplier = 'can_view_market_supplier';

  /// Разделы, которые сайт владельцу открывает всегда (PosPermissions.OwnerSections кассы).
  static const _ownerSections = {viewProducts, viewAnalytics, viewClients, viewOrders};

  /// Право как у кассы: флаг с сервера; владельцу — его разделы; продажи/долги — всем. Роль сама прав не даёт.
  bool can(String p) {
    if (p == 'can_view_sales') return true;
    final list = perms;
    if (list == null) {
      // профиль с доступами ещё не загружен — как было до 13.10: администратор всё, кассир — продажа и склад
      const cashier = {viewProducts, discount, deleteLine, employeeReturn};
      return isAdmin || cashier.contains(p);
    }
    if (list.contains(p)) return true;
    return _ownerSections.contains(p) && (isOwner || role.toLowerCase() == 'owner');
  }

  /// 2026-10-22 (мобилка, админ, П9): менять цену продажи и закупки. Владелец и администратор — всегда (карточка
  /// товара программы владельца на ПК право цены не проверяет), сотрудник — только с флагом «Изменение цены».
  bool get canEditPrices => isAdmin || can(editPrice);

  /// 2026-10-22 (мобилка, админ, П9): закупочные цены, приёмка с ценами и заказ поставщику. Флаги закупок и поставщиков
  /// сервер ставит по тарифу (комментарий PermissionService кассы), поэтому владельца и администратора не запираем,
  /// а сотруднику нужен «Закупки» или «Поставщики» — раньше хватало одного «Склада».
  bool get canProcure => isAdmin || can(procurement) || can(supplier);

  /// Разделы, которым кроме [routePermission] нужны закупки (см. [canProcure]).
  static const _procureRoutes = {'/admin/receive', '/admin/purchase'};

  /// Какой доступ нужен для раздела (как разделы десктопа: AdminWindow, меню кассы). null — открыт всем.
  static String? routePermission(String loc) {
    if (loc == '/stock' || loc.startsWith('/stock/')) return viewProducts;
    switch (loc) {
      case '/admin/product':
      case '/admin/receive':
      case '/admin/smartcam':
      case '/admin/new-product':
      case '/admin/purchase':
        return viewProducts;
      case '/admin/shifts':
        return viewShifts;
      case '/admin/orders':
      case '/admin/pick':
        return viewOrders;
      case '/admin/clients':
      case '/admin/quotes':
      case '/rentals':
      case '/rentals/new':
        return viewClients;
      case '/admin/deletions':
      case '/admin/audit':
      case '/admin/advisor':
      case '/admin/summary':
      case '/admin/abc':
        return viewAnalytics;
      case '/admin/staff':
      case '/admin/payqr':
      case '/admin/esf':
      case '/admin/debt-access':
        return viewSettings;
    }
    return null;
  }

  /// «Управление» на главной: администратор/владелец или сотрудник, которому открыт хоть один раздел управления.
  bool get showAdmin => isAdmin || const [viewAnalytics, viewOrders, viewShifts, viewSettings].any(can);

  bool canRoute(String loc) {
    // 2026-10-22 (мобилка, админ, П9): уведомления — раздел управления (заказы, долги, остатки), раньше открывался всем.
    if (loc == '/admin' || loc == '/admin/notifications') return showAdmin;
    final p = routePermission(loc);
    if (p != null && !can(p)) return false;
    return !_procureRoutes.contains(loc) || canProcure;
  }

  /// Администратор и владелец видят управление; кассир — только продажу и склад (ТЗ, раздел 5).
  bool get isAdmin {
    final r = role.toLowerCase();
    return isOwner || r.contains('owner') || r.contains('admin') || r.contains('владел') || r.contains('админ');
  }

  Session copyWith({
    String? access,
    String? refresh,
    String? userId,
    String? userName,
    String? role,
    String? companyName,
    String? companyInn,
    String? companyAddress,
    bool? isOwner,
    String? cashboxId,
    String? cashboxName,
    List<String>? perms,
  }) => Session(
    email: email,
    access: access ?? this.access,
    refresh: refresh ?? this.refresh,
    userId: userId ?? this.userId,
    userName: userName ?? this.userName,
    role: role ?? this.role,
    companyName: companyName ?? this.companyName,
    companyInn: companyInn ?? this.companyInn,
    companyAddress: companyAddress ?? this.companyAddress,
    isOwner: isOwner ?? this.isOwner,
    cashboxId: cashboxId ?? this.cashboxId,
    cashboxName: cashboxName ?? this.cashboxName,
    perms: perms ?? this.perms,
  );

  Map<String, dynamic> toJson() => {
    'email': email,
    'access': access,
    'refresh': refresh,
    'userId': userId,
    'userName': userName,
    'role': role,
    'companyName': companyName,
    'companyInn': companyInn,
    'companyAddress': companyAddress,
    'isOwner': isOwner,
    'cashboxId': cashboxId,
    'cashboxName': cashboxName,
    'perms': perms,
  };

  static Session fromJson(Map<String, dynamic> j) => Session(
    email: j['email'] as String? ?? '',
    access: j['access'] as String? ?? '',
    refresh: j['refresh'] as String? ?? '',
    userId: j['userId'] as String? ?? '',
    userName: j['userName'] as String? ?? '',
    role: j['role'] as String? ?? '',
    companyName: j['companyName'] as String? ?? '',
    companyInn: j['companyInn'] as String? ?? '',
    companyAddress: j['companyAddress'] as String? ?? '',
    isOwner: j['isOwner'] as bool? ?? false,
    cashboxId: j['cashboxId'] as String?,
    cashboxName: j['cashboxName'] as String?,
    perms: (j['perms'] as List?)?.map((e) => e.toString()).toList(),
  );

  /// Флаги can_* = true и списки permissions из ответа профиля (как ExtractPermissionNames кассы).
  static List<String> extractPerms(Object? payload) {
    final out = <String>{};
    void visit(Object? e, int depth) {
      if (depth > 5) return;
      if (e is List) {
        for (final it in e) {
          if (it is String && it.isNotEmpty) {
            out.add(it);
          } else {
            visit(it, depth + 1);
          }
        }
        return;
      }
      if (e is! Map) return;
      for (final entry in e.entries) {
        final k = entry.key.toString();
        if (k.startsWith('can_') && entry.value == true) {
          out.add(k);
          continue;
        }
        if (const {'permissions', 'user_permissions', 'access_rights', 'rights', 'user', 'profile', 'data', 'cashier'}.contains(k)) {
          visit(entry.value, depth + 1);
        }
      }
    }

    visit(payload, 0);
    return out.toList();
  }
}

class SessionStore {
  static const _key = 'nurmarket.session';
  static const _storage = FlutterSecureStorage();

  Future<Session?> load() async {
    try {
      final raw = await _storage.read(key: _key);
      if (raw == null || raw.isEmpty) return null;
      return Session.fromJson(jsonDecode(raw) as Map<String, dynamic>);
    } catch (_) {
      return null;
    }
  }

  Future<void> save(Session s) => _storage.write(key: _key, value: jsonEncode(s.toJson()));

  Future<void> clear() => _storage.delete(key: _key);
}
