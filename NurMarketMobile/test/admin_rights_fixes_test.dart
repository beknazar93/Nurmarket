// 2026-10-22 (мобилка, админ): проверки пакетов П9 (права), П11 (экраны администратора), П14 (аналитика) — на фейках,
// сервер NurCRM не трогается.
import 'package:drift/native.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/util/access.dart';
import 'package:nurmarket/core/util/product_picker.dart';
import 'package:nurmarket/features/admin/admin_more.dart';
import 'package:nurmarket/features/admin/admin_repository.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Map<String, dynamic>? query);

class FakeApi extends ApiClient {
  FakeApi(this.handler) : super(SessionStore());
  Handler handler;
  final calls = <String>[];

  Future<dynamic> _h(String m, String p, Map<String, dynamic>? q) {
    calls.add('$m $p ${q ?? ''}');
    return handler(m, p, q);
  }

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) => _h('GET', path, query);
  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) => _h('POST', path, null);
  @override
  Future<dynamic> patch(String path, {Object? body}) => _h('PATCH', path, null);
  @override
  Future<dynamic> delete(String path) => _h('DELETE', path, null);
}

Session emp(List<String> perms, {String role = 'cashier'}) => Session(email: 'e', access: '', refresh: '', role: role, perms: perms);

void main() {
  group('П9 права', () {
    test('цена, закупки и поставщики — отдельные права сотрудника; владелец не запирается', () {
      final stockOnly = emp([Session.viewProducts]);
      expect(stockOnly.canRoute('/admin/product'), isTrue);
      expect(stockOnly.canEditPrices, isFalse);
      expect(stockOnly.canProcure, isFalse);
      expect(stockOnly.canRoute('/admin/receive'), isFalse, reason: 'приёмка с ценами — нужны закупки');
      expect(stockOnly.canRoute('/admin/purchase'), isFalse);
      expect(stockOnly.canRoute('/stock/receive'), isTrue, reason: 'приёмка кассира без цен — по «Складу»');

      final buyer = emp([Session.viewProducts, Session.procurement, Session.editPrice]);
      expect(buyer.canEditPrices, isTrue);
      expect(buyer.canRoute('/admin/receive'), isTrue);
      expect(emp([Session.viewProducts, Session.supplier]).canRoute('/admin/purchase'), isTrue);
      expect(emp([Session.procurement]).canRoute('/admin/receive'), isFalse, reason: 'и «Склад» тоже нужен');

      final owner = Session(email: 'o', access: '', refresh: '', role: 'owner', isOwner: true, perms: const []);
      expect(owner.canEditPrices, isTrue);
      expect(owner.canRoute('/admin/receive'), isTrue);
    });

    test('уведомления — только тем, кому открыто «Управление»', () {
      expect(emp([Session.viewProducts]).canRoute('/admin/notifications'), isFalse);
      expect(emp([Session.viewAnalytics]).canRoute('/admin/notifications'), isTrue);
      expect(Session(email: 'a', access: '', refresh: '', role: 'admin').canRoute('/admin/notifications'), isTrue);
    });

    test('«Продажа» в сотрудниках — can_view_sale, есть заказы, закупки и поставщики', () {
      expect(staffRights['can_view_sale'], 'rightSale');
      expect(staffRights['can_view_cashier'], 'rightCashier');
      expect(staffRights.containsKey('can_view_orders'), isTrue);
      expect(staffRights.containsKey('can_view_market_procurement'), isTrue);
      expect(staffRights.containsKey('can_view_market_supplier'), isTrue);
    });

    test('свои права снять нельзя, добавить можно; у других — как выбрано', () {
      final before = {'can_view_settings': true, 'can_view_products': true, 'can_view_orders': false};
      final after = {'can_view_settings': false, 'can_view_products': true, 'can_view_orders': true};
      expect(guardSelfRights(before, after, self: true), {'can_view_settings': true, 'can_view_products': true, 'can_view_orders': true});
      expect(guardSelfRights(before, after, self: false), after);
      const e = Employee('7', 'Айбек', 'admin', {});
      expect(isSelfEmployee(e, '7'), isTrue);
      expect(isSelfEmployee(e, '8'), isFalse);
      expect(isSelfEmployee(e, ''), isFalse);
    });

    test('сотрудники — все страницы списка', () async {
      final api = FakeApi((m, p, q) async {
        final page = q?['page'] as int? ?? 1;
        return {
          'results': [
            for (var i = 0; i < (page < 3 ? 100 : 5); i++) {'id': '$page-$i', 'first_name': 'N$i', 'can_view_sale': true},
          ],
          'next': page < 3 ? 'x' : null,
        };
      });
      final list = await StaffRepository(api).list();
      expect(list.length, 205);
      expect(list.first.rights['can_view_sale'], isTrue);
    });

    test('маршрут без данных перехода не падает', () {
      expect(withExtra<String>(null, (v) => Text(v)), isA<MissingDataScreen>());
      expect(withExtra<String>('ok', (v) => Text(v)), isA<Text>());
    });
  });

  group('П11 экраны администратора', () {
    late AppDatabase db;
    setUp(() => db = AppDatabase(NativeDatabase.memory()));
    tearDown(() => db.close());

    test('выручка дня — с сервера, как «Сводка», а не сумма 10 страниц продаж', () async {
      final api = FakeApi((m, p, q) async {
        if (p == 'api/main/analytics/market/') {
          expect(q?['tab'], 'sales');
          expect(q?['period_start'], q?['period_end']);
          return {
            'cards': {'revenue': '12345.50', 'transactions': 17},
          };
        }
        fail('продажи по страницам не нужны: $p');
      });
      final t = await AdminRepository(api, db).today();
      expect(t.revenue, 12345.5);
      expect(t.checks, 17);
    });

    test('старый сервер без отчёта (404) — выручка по продажам, как раньше; без связи — ошибка, не ноль', () async {
      final now = DateTime.now().toUtc().toIso8601String();
      final api = FakeApi((m, p, q) async {
        if (p == 'api/main/analytics/market/') throw ApiException('nf', status: 404);
        return {
          'results': [
            {'created_at': now, 'total': '100', 'status': 'paid'},
            {'created_at': now, 'total': '50', 'status': 'canceled'},
          ],
          'next': null,
        };
      });
      final t = await AdminRepository(api, db).today();
      expect(t.revenue, 100);
      expect(t.checks, 1);
      final offline = FakeApi((m, p, q) async => throw ApiException('нет связи'));
      expect(() => AdminRepository(offline, db).today(), throwsA(isA<ApiException>()));
    });

    test('сборка: упаковка и вес не больше заказанного, чужой товар — нет, недобор считается', () {
      const items = [SiteOrderItem('a', 'Вода', 6, 50), SiteOrderItem('w', 'Картофель', 2.5, 60), SiteOrderItem('a', 'Вода (ещё)', 2, 50)];
      final picked = <int, double>{};
      expect(pickShortage(items, picked), 10.5);
      // штрихкод упаковки ×12 — строка 1 заполнена до 6, не до 12
      var r = pickScanned(items, picked, 'a', 12)!;
      expect(r, (0, 6.0));
      picked[r.$1] = r.$2;
      // следующий скан той же воды — во вторую строку
      r = pickScanned(items, picked, 'a', null)!;
      expect(r, (2, 1.0));
      picked[r.$1] = r.$2;
      // весовая этикетка 1,25 кг — вес, а не «+1»
      r = pickScanned(items, picked, 'w', 1.25)!;
      expect(r, (1, 1.25));
      picked[r.$1] = r.$2;
      expect(pickScanned(items, picked, 'zzz', null), isNull);
      expect(pickShortage(items, picked), 2.25);
      picked[1] = 2.5;
      picked[2] = 2;
      expect(pickScanned(items, picked, 'a', null), isNull, reason: 'всё собрано — больше не отмечается');
      expect(pickShortage(items, picked), 0);
    });

    test('код выдачи — один на заказ и сохраняется', () async {
      final repo = AdminRepository(FakeApi((m, p, q) async => null), db);
      expect(await repo.savedPickupCode('o1'), isNull);
      final c1 = await repo.pickupCode('o1');
      expect(c1, matches(RegExp(r'^\d{4}$')));
      expect(await repo.pickupCode('o1'), c1);
      expect(await repo.savedPickupCode('o1'), c1);
    });

    test('клиенты постранично: первая страница 50, «Ещё» пока есть next, поставщики скрыты', () async {
      final api = FakeApi((m, p, q) async {
        expect(q?['page_size'], AdminRepository.clientsPageSize);
        final page = q?['page'] as int;
        return {
          'results': [
            {'id': 'c$page', 'full_name': 'Клиент $page'},
            {'id': 's$page', 'full_name': 'Поставщик', 'type': 'supplier'},
          ],
          'next': page < 2 ? 'x' : null,
        };
      });
      final repo = AdminRepository(api, db);
      final p1 = await repo.clientsPage('', 1);
      expect(p1.rows.map((c) => c.id), ['c1']);
      expect(p1.more, isTrue);
      final p2 = await repo.clientsPage('', 2);
      expect(p2.more, isFalse);
      expect(api.calls.length, 2, reason: 'при открытии — одна страница, не 30');
    });

    test('порог «заканчивается» как на ПК — меньше 10', () {
      expect(lowStockLimit, 10);
    });
  });
}
