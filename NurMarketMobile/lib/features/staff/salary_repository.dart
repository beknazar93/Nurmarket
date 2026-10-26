// 2026-10-26 (голос-управление, В7): начисление зарплаты и схема оплаты — на существующих методах NurCRM, как у ПК и сайта.
//  - начислено: GET api/main/analytics/market/?tab=salary&period_start&period_end (серверный расчёт, тот же, что на ПК и сайте);
//  - схема оплаты: GET/POST/PATCH api/main/market-sale-employee-pay-profiles/ (SalesApiService.SavePayProfileAsync кассы).
// «Выплачено» и выплата — salary_payout.dart. Штрафов, премий записями и сменной ставки на сервере нет (Б-63).
import '../../core/ai/assistant/staff_tools.dart' show PaySchemeData, SalaryRow;
import '../../core/api/api_client.dart';

const kPaySchemes = ['salary', 'percent', 'salary_plus_percent', 'per_item', 'salary_plus_per_item', 'percent_plus_per_item'];

/// Итоги отчёта (плитки экрана «Зарплата»).
class SalaryCards {
  final double payroll;
  final double base;
  final double bonus;
  final double sales;
  final int employees;
  const SalaryCards(this.payroll, this.base, this.bonus, this.sales, this.employees);
}

class SalaryReport {
  final List<SalaryRow> rows;
  final SalaryCards cards;
  const SalaryReport(this.rows, this.cards);
}

/// Строка отчёта tab=salary (поля — как читает ПК: SalaryWindow.SalaryRow.From).
SalaryRow parseSalaryRow(Map<String, dynamic> r) {
  final name = str(r, 'employee_label') ?? '';
  return SalaryRow(
    str(r, 'user_id') ?? '',
    name.isEmpty ? '—' : name,
    str(r, 'pay_scheme') ?? '',
    num0(r, 'base_prorated'),
    num0(r, 'sales_percent'),
    num0(r, 'per_item_amount'),
    num0(r, 'total'),
    monthly: num0(r, 'monthly_base_salary'),
    bonus: num0(r, 'percent_bonus') + num0(r, 'per_item_bonus'),
    noScheme: (str(r, 'profile_scope') ?? '') == 'none',
  );
}

SalaryReport parseSalaryReport(dynamic data) {
  final m = data is Map ? data.cast<String, dynamic>() : <String, dynamic>{};
  final cards = m['cards'] is Map ? (m['cards'] as Map).cast<String, dynamic>() : <String, dynamic>{};
  final rows = [for (final x in asList(m['rows'])) parseSalaryRow(x)]..sort((a, b) => b.accrued.compareTo(a.accrued));
  return SalaryReport(
    rows,
    SalaryCards(
      num0(cards, 'total_payroll'),
      num0(cards, 'total_base_prorated'),
      num0(cards, 'total_percent_bonus') + num0(cards, 'total_per_item_bonus'),
      num0(cards, 'total_employee_sales'),
      num0(cards, 'employees_with_profile').round(),
    ),
  );
}

class SalaryRepository {
  final ApiClient api;
  SalaryRepository(this.api);

  static String _day(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  Future<SalaryReport> report(DateTime from, DateTime to) async => parseSalaryReport(
    await api.get('api/main/analytics/market/', query: {'tab': 'salary', 'period_start': _day(from), 'period_end': _day(to)}, timeout: const Duration(seconds: 30)),
  );

  // ---- схема оплаты

  Future<({String id, PaySchemeData data})?> _profile(String userId) async {
    final rows = asList(await api.get('api/main/market-sale-employee-pay-profiles/', query: {'user': userId.trim()}));
    // сервер может не соблюсти фильтр: берём только профиль этого сотрудника
    final mine = rows.where((m) => (str(m, 'user') ?? str(m, 'user_id') ?? userId) == userId).toList();
    if (mine.isEmpty) return null;
    final m = mine.first;
    return (
      id: str(m, 'id') ?? '',
      data: PaySchemeData(str(m, 'pay_scheme') ?? 'salary', num0(m, 'monthly_base_salary'), num0(m, 'sales_percent'), num0(m, 'per_item_amount')),
    );
  }

  Future<PaySchemeData?> scheme(String userId) async => (await _profile(userId))?.data;

  /// POST (профиля ещё нет) или PATCH (есть) — как кассы.
  Future<void> setScheme(String userId, PaySchemeData d) async {
    final prof = await _profile(userId);
    final body = {
      'user': userId.trim(),
      'pay_scheme': d.scheme,
      'monthly_base_salary': d.base.toStringAsFixed(2),
      'sales_percent': d.percent.toStringAsFixed(2),
      'per_item_amount': d.perItem.toStringAsFixed(2),
    };
    if (prof == null || prof.id.isEmpty) {
      await api.post('api/main/market-sale-employee-pay-profiles/', body: body);
    } else {
      await api.patch('api/main/market-sale-employee-pay-profiles/${prof.id}/', body: body);
    }
  }
}
