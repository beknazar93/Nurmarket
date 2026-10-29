// 2026-10-26 (голос-управление, В7): настоящий источник для инструментов сотрудников (порт StaffSource из staff_tools.dart) —
// сотрудники, задачи (api/main/tasks/), табель (смены), зарплата (tab=salary + «Выплачено» по меткам), выплата по правилам ПК
// и схема оплаты. Все деньги идут через SalaryPayoutService (ключ в source_id, замок при «ответ не дошёл», повтор за 10 с не
// проводится). Что сервер не хранит (штрафы, премии, плановые смены) — честно «недоступно» в самих инструментах.
import '../../core/ai/assistant/staff_tools.dart';
import '../../core/api/session_store.dart';
import '../admin/admin_more.dart' show StaffRepository;
import '../ops/ops_repository.dart' show OpsRepository;
import 'salary_payout.dart';
import 'salary_repository.dart';
import 'tasks_repository.dart';
import 'timesheet.dart';

String _norm(String s) => s.toLowerCase().replaceAll(RegExp(r'\s+'), ' ').trim();

class ApiStaffSource implements StaffSource {
  final StaffRepository staffRepo;
  final TasksRepository tasksRepo;
  final TimesheetRepository timesheetRepo;
  final SalaryRepository salaryRepo;
  final SalaryPayoutService payoutService;
  final Session? Function() session;
  ApiStaffSource({
    required this.staffRepo,
    required this.tasksRepo,
    required this.timesheetRepo,
    required this.salaryRepo,
    required this.payoutService,
    required this.session,
  });

  @override
  Set<StaffCap> get caps => StaffCap.values.toSet();

  List<StaffPerson>? _people;
  DateTime? _peopleAt;

  @override
  Future<List<StaffPerson>> staff() async {
    final p = _people;
    final at = _peopleAt;
    if (p != null && at != null && DateTime.now().difference(at) < const Duration(seconds: 60)) return p;
    final list = [for (final e in await staffRepo.list()) StaffPerson(e.id, e.name, e.role, active: !e.dismissed)];
    _people = list;
    _peopleAt = DateTime.now();
    return list;
  }

  // ---- задачи

  @override
  Future<List<StaffTask>> tasks() => tasksRepo.list();

  @override
  Future<StaffTask> createTask(StaffTask draft) => tasksRepo.create(draft, key: OpsRepository.newKey());

  @override
  Future<StaffTask> updateTask(StaffTask updated) => tasksRepo.update(updated, statusOnly: !(session()?.manageTasks ?? false));

  @override
  Future<void> deleteTask(String id) => tasksRepo.delete(id);

  // ---- табель

  @override
  Future<List<TimesheetRow>> timesheet(DateTime from, DateTime to) async {
    final rows = await timesheetRepo.timesheet(from, to);
    try {
      return withEmployeeIds(rows, await staff());
    } catch (_) {
      return rows; // список сотрудников не получен — табель по именам из смен
    }
  }

  // ---- зарплата

  // id сотрудника в отчёте зарплаты (user_id) → id в списке сотрудников; обратно — при выплате.
  final Map<String, String> _userIdByStaffId = {};

  @override
  Future<List<SalaryRow>> salary(DateTime from, DateTime to) async {
    final report = await salaryRepo.report(from, to);
    List<StaffPerson> people = const [];
    try {
      people = await staff();
    } catch (_) {}
    final ids = {for (final p in people) p.id};
    final byName = {for (final p in people) _norm(p.name): p};

    // сопоставление строк отчёта и списка сотрудников: по id, а если id не совпали — по имени
    final mapped = <SalaryRow>[];
    for (final r in report.rows) {
      var id = r.employeeId;
      if (!ids.contains(id)) {
        final p = byName[_norm(r.name)];
        if (p != null) {
          _userIdByStaffId[p.id] = r.employeeId;
          id = p.id;
        }
      } else {
        _userIdByStaffId[id] = r.employeeId;
      }
      mapped.add(
        SalaryRow(id, r.name, r.scheme, r.base, r.percent, r.perItem, r.accrued, monthly: r.monthly, bonus: r.bonus, noScheme: r.noScheme),
      );
    }

    PaidResult? paid;
    var timedOut = false;
    try {
      paid = await payoutService
          .paidForPeriod([for (final r in report.rows) (userId: r.employeeId, name: r.name)], from, to)
          .timeout(const Duration(seconds: 60));
    } catch (_) {
      timedOut = true;
    }
    return [
      for (var i = 0; i < mapped.length; i++)
        () {
          final src = report.rows[i];
          final info = paid?.users[src.employeeId];
          final t = trustPaid(paid, info, timedOut: timedOut);
          // выплаты не прочитаны совсем (журнал недоступен / время вышло) — «выплачено» неизвестно
          if (paid == null || paid.journalUnavailable) return mapped[i].withPaid(null, true);
          return mapped[i].withPaid(info?.paid ?? 0, !t.sure);
        }(),
    ];
  }

  // ---- выплата

  @override
  Future<PayoutOutcome> payout({
    required SalaryRow row,
    required double amount,
    required String kind,
    required DateTime from,
    required DateTime to,
    required String method,
  }) async {
    final s = session();
    if (s == null || !s.editSalary) return const PayoutOutcome(error: 'denied');
    final box = await payoutService.defaultCashbox();
    if (box == null) return const PayoutOutcome(error: '#nocash');
    final userId = _userIdByStaffId[row.employeeId] ?? row.employeeId;
    final res = await payoutService.pay(
      PayoutRequest(
        key: OpsRepository.newKey(),
        userId: userId,
        employee: row.name,
        from: from,
        to: to,
        amount: amount,
        cashbox: box,
        kind: kind,
      ),
    );
    switch (res.verdict) {
      case PayoutVerdict.unknown:
        return const PayoutOutcome(unknown: true);
      case PayoutVerdict.rejected:
        return PayoutOutcome(error: res.error);
      case PayoutVerdict.done:
        final left = row.left == null ? null : row.left! - amount;
        final notes = [if (box.isSales) 'sales_cashbox', if (!payoutService.approvedRightAway) 'pending'];
        return PayoutOutcome(ok: true, cashbox: box.name, left: left, note: notes.join(','));
    }
  }

  // ---- схема оплаты

  @override
  Future<PaySchemeData?> scheme(String employeeId) => salaryRepo.scheme(_userIdByStaffId[employeeId] ?? employeeId);

  @override
  Future<void> setScheme(String employeeId, PaySchemeData data) => salaryRepo.setScheme(_userIdByStaffId[employeeId] ?? employeeId, data);
}
