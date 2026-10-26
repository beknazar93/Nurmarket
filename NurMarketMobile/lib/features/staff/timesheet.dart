// 2026-10-26 (голос-управление, В7): табель из смен — перенос StaffTimesheetService.Aggregate кассы (ПК): только ЗАКРЫТЫЕ смены,
// день — по дате ОТКРЫТИЯ (смена через полночь — день открытия), часы — сумма, открытая смена не считается (она ещё не знает
// своей длины). Данные — все страницы api/construction/shifts/ за период (старый AdminRepository.shifts() берёт только 100).
import '../../core/ai/assistant/staff_tools.dart' show StaffPerson, TimesheetRow;
import '../../core/api/api_client.dart';

/// Смена из ответа сервера (только то, что нужно табелю).
class ShiftRec {
  final String id;

  /// id кассира у сервера (если отдан) и его отображаемое имя.
  final String cashierId;
  final String cashier;
  final DateTime? openedAt;
  final DateTime? closedAt;
  final double revenue;
  const ShiftRec({this.id = '', this.cashierId = '', required this.cashier, this.openedAt, this.closedAt, this.revenue = 0});
}

/// Табель за период [from]..[to] (даты включительно, по дню открытия). Порядок как на ПК: больше часов — выше.
/// Один кассир — одна строка (по id, а без id — по имени без учёта регистра). Пустое имя — «—».
List<TimesheetRow> aggregateTimesheet(Iterable<ShiftRec> shifts, DateTime? from, DateTime? to) {
  final groups = <String, List<ShiftRec>>{};
  for (final s in shifts) {
    final o = s.openedAt;
    final c = s.closedAt;
    if (o == null || c == null || !c.isAfter(o)) continue;
    if (from != null && DateTime(o.year, o.month, o.day).isBefore(DateTime(from.year, from.month, from.day))) continue;
    if (to != null && DateTime(o.year, o.month, o.day).isAfter(DateTime(to.year, to.month, to.day))) continue;
    final name = s.cashier.trim().isEmpty ? '—' : s.cashier.trim();
    final key = s.cashierId.isNotEmpty ? 'id:${s.cashierId}' : 'n:${name.toLowerCase()}';
    (groups[key] ??= []).add(s);
  }
  final rows = <TimesheetRow>[];
  for (final g in groups.values) {
    final days = <int>{for (final s in g) DateTime(s.openedAt!.year, s.openedAt!.month, s.openedAt!.day).millisecondsSinceEpoch};
    var minutes = 0;
    for (final s in g) {
      minutes += s.closedAt!.difference(s.openedAt!).inMinutes;
    }
    final first = g.first;
    final name = first.cashier.trim().isEmpty ? '—' : first.cashier.trim();
    rows.add(TimesheetRow(first.cashierId, name, g.length, days.length, minutes / 60.0, g.fold(0.0, (a, s) => a + s.revenue)));
  }
  rows.sort((a, b) => b.hours.compareTo(a.hours));
  return rows;
}

/// Сотруднику без id в смене подставляется id по совпадению имени (смены часто отдают только отображаемое имя кассира).
List<TimesheetRow> withEmployeeIds(List<TimesheetRow> rows, List<StaffPerson> staff) {
  String norm(String s) => s.toLowerCase().replaceAll(RegExp(r'\s+'), ' ').trim();
  final byName = <String, StaffPerson>{};
  for (final p in staff) {
    byName.putIfAbsent(norm(p.name), () => p);
  }
  return [
    for (final r in rows)
      r.employeeId.isNotEmpty ? r : TimesheetRow(byName[norm(r.name)]?.id ?? '', byName[norm(r.name)]?.name ?? r.name, r.shifts, r.days, r.hours, r.revenue),
  ];
}

class TimesheetRepository {
  final ApiClient api;
  TimesheetRepository(this.api);

  /// Все смены периода: страницы api/construction/shifts/ (до 30 по 200), потом отбор по дню открытия на телефоне.
  Future<List<ShiftRec>> shiftsBetween(DateTime from, DateTime to) async {
    final rows = await getAllPages(api, 'api/construction/shifts/', pageSize: 200, maxPages: 30);
    return [
      for (final m in rows)
        ShiftRec(
          id: str(m, 'id') ?? '',
          cashierId: str(m, 'cashier') ?? str(m, 'cashier_id') ?? str(m, 'user') ?? str(m, 'opened_by') ?? '',
          cashier: str(m, 'cashier_display') ?? str(m, 'cashier_name') ?? str(m, 'user_display') ?? '',
          openedAt: DateTime.tryParse(str(m, 'opened_at') ?? '')?.toLocal(),
          closedAt: DateTime.tryParse(str(m, 'closed_at') ?? '')?.toLocal(),
          revenue: _first(m, ['sales_total', 'total_sales']),
        ),
    ];
  }

  static double _first(Map<String, dynamic> m, List<String> keys) {
    for (final k in keys) {
      if (m[k] != null) return num0(m, k);
    }
    return 0;
  }

  Future<List<TimesheetRow>> timesheet(DateTime from, DateTime to) async => aggregateTimesheet(await shiftsBetween(from, to), from, to);
}
