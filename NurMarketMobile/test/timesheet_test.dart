// 2026-10-26 (голос-управление, В7): табель из смен — правила ПК (StaffTimesheetService.Aggregate) на фейках, без сети.
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/staff_tools.dart';
import 'package:nurmarket/features/staff/timesheet.dart';

import 'support/staff_fakes.dart';

ShiftRec _s(String name, String open, String? close, {double revenue = 0, String id = ''}) =>
    ShiftRec(cashierId: id, cashier: name, openedAt: DateTime.parse(open), closedAt: close == null ? null : DateTime.parse(close), revenue: revenue);

void main() {
  group('табель как на ПК', () {
    test('только закрытые смены: открытая (и с закрытием раньше открытия) не считается', () {
      final rows = aggregateTimesheet([
        _s('Айбек', '2026-10-05 09:00', '2026-10-05 18:00', revenue: 1000),
        _s('Айбек', '2026-10-06 09:00', null, revenue: 5000), // открыта
        _s('Айбек', '2026-10-07 09:00', '2026-10-07 08:00'), // закрыта раньше открытия
      ], DateTime(2026, 10, 1), DateTime(2026, 10, 31));
      expect(rows.length, 1);
      expect(rows.single.shifts, 1);
      expect(rows.single.hours, 9);
      expect(rows.single.revenue, 1000);
    });

    test('смена через полночь — день ОТКРЫТИЯ; два открытия в один день — 1 день', () {
      final rows = aggregateTimesheet([
        _s('Нурлан', '2026-10-05 22:00', '2026-10-06 06:00', revenue: 100), // 8 часов, день 05
        _s('Нурлан', '2026-10-05 10:00', '2026-10-05 12:00', revenue: 50), // тот же день 05
        _s('Нурлан', '2026-10-08 09:00', '2026-10-08 10:30'),
      ], DateTime(2026, 10, 1), DateTime(2026, 10, 31));
      final r = rows.single;
      expect(r.shifts, 3);
      expect(r.days, 2);
      expect(r.hours, closeTo(11.5, 1e-9));
      expect(r.revenue, 150);
    });

    test('период — по дню открытия включительно: смена 31.10 вечером попала в октябрь, а 30.09 вечером — нет', () {
      final rows = aggregateTimesheet([
        _s('Айбек', '2026-10-31 20:00', '2026-11-01 04:00'),
        _s('Айбек', '2026-09-30 20:00', '2026-10-01 04:00'),
      ], DateTime(2026, 10, 1), DateTime(2026, 10, 31));
      expect(rows.single.shifts, 1);
      expect(rows.single.hours, 8);
    });

    test('один кассир — одна строка (имя без учёта регистра и пробелов), порядок: больше часов — выше', () {
      final rows = aggregateTimesheet([
        _s('айбек', '2026-10-01 09:00', '2026-10-01 11:00'),
        _s('Айбек ', '2026-10-02 09:00', '2026-10-02 11:00'),
        _s('Мария', '2026-10-03 09:00', '2026-10-03 19:00'),
        _s('', '2026-10-04 09:00', '2026-10-04 10:00'),
      ], null, null);
      expect(rows.map((e) => e.name), ['Мария', 'айбек', '—']);
      expect(rows[1].shifts, 2);
    });

    test('по id кассира: два сотрудника с одним именем — разные строки', () {
      final rows = aggregateTimesheet([
        _s('Айбек', '2026-10-01 09:00', '2026-10-01 11:00', id: 'a'),
        _s('Айбек', '2026-10-02 09:00', '2026-10-02 13:00', id: 'b'),
      ], null, null);
      expect(rows.length, 2);
    });

    test('сотруднику без id подставляется id из списка по имени', () {
      final rows = aggregateTimesheet([_s('Нурлан Б.', '2026-10-01 09:00', '2026-10-01 11:00')], null, null);
      final withIds = withEmployeeIds(rows, const [StaffPerson('u2', 'нурлан  б.', 'кладовщик')]);
      expect(withIds.single.employeeId, 'u2');
    });
  });

  group('смены с сервера: все страницы', () {
    test('shiftsBetween забирает все страницы, а не первую сотню', () async {
      final srv = StaffServer()..forceSize = 2;
      srv.shifts = [
        for (var i = 1; i <= 5; i++)
          {'id': 's$i', 'cashier_display': 'Айбек', 'opened_at': '2026-10-1${i}T12:00:00Z', 'closed_at': '2026-10-1${i}T20:00:00Z', 'sales_total': '1000'},
      ];
      final api = FakeApi(srv.handle);
      final repo = TimesheetRepository(api);
      final all = await repo.shiftsBetween(DateTime(2026, 10, 1), DateTime(2026, 10, 31));
      expect(all.length, 5);
      expect(api.count('GET api/construction/shifts/'), greaterThanOrEqualTo(3));
      final rows = await repo.timesheet(DateTime(2026, 10, 1), DateTime(2026, 10, 31));
      expect(rows.single.shifts, 5);
      expect(rows.single.hours, 40);
      expect(rows.single.revenue, 5000);
    });
  });
}
