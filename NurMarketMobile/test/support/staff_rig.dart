// 2026-10-26 (голос-управление, В7): звонок целиком только с инструментами сотрудников — настоящий ApiStaffSource поверх
// сервера в памяти (staff_fakes.dart). Без сети, без микрофона, без боевой базы.
import 'package:nurmarket/core/ai/assistant/action_tools.dart';
import 'package:nurmarket/core/ai/assistant/call_table.dart';
import 'package:nurmarket/core/ai/assistant/staff_tools.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/features/admin/admin_more.dart' show StaffRepository;
import 'package:nurmarket/features/staff/salary_payout.dart';
import 'package:nurmarket/features/staff/salary_repository.dart';
import 'package:nurmarket/features/staff/staff_source.dart';
import 'package:nurmarket/features/staff/tasks_repository.dart';
import 'package:nurmarket/features/staff/timesheet.dart';

import 'staff_fakes.dart';

class _NoHost implements ActionHost {
  @override
  dynamic noSuchMethod(Invocation i) => null;
}

Session sess({bool owner = false, String role = 'cashier', List<String> perms = const [], String id = 'u1', String name = 'Кассир'}) =>
    Session(email: 'shop@test', access: 'x', refresh: '', userId: id, userName: name, role: role, isOwner: owner, perms: perms, cashboxId: 'cb1');

final ownerSession = sess(owner: true, role: 'owner', id: 'owner1', name: 'Владелец', perms: const ['can_view_salary', 'can_edit_salary', 'can_view_employees', Session.viewShifts]);
final cashierSession = sess(id: 'u1', name: 'Айбек', perms: const [Session.sale]);

class StaffRig {
  final StaffServer server = StaffServer();
  late final FakeApi api;
  final MemSettings settings = MemSettings();
  late final SalaryPayoutService payout;
  late final ApiStaffSource source;
  late final TasksRepository tasks;
  final tables = TableStore();
  final speech = UserSpeechLog();
  final reg = ToolRegistry();
  late final ActionKit kit;
  DateTime now = DateTime(2026, 10, 26, 12);
  Session session;
  var _n = 0;

  StaffRig({Session? session, bool owner = true}) : session = session ?? ownerSession {
    api = FakeApi(server.handle);
    tasks = TasksRepository(api, now: () => now);
    payout = SalaryPayoutService(api, settings, scope: 'shop@test', byName: 'Владелец', approvedRightAway: owner, now: () => now);
    source = ApiStaffSource(
      staffRepo: StaffRepository(api),
      tasksRepo: tasks,
      timesheetRepo: TimesheetRepository(api),
      salaryRepo: SalaryRepository(api),
      payoutService: payout,
      session: () => this.session,
    );
    kit = ActionKit(host: _NoHost(), tables: tables, now: () => now);
    registerActionTools(reg, kit); // «отмени последнее» и общие ворота
    registerStaffTools(reg, kit, source, tables: tables);
  }

  void user(String text) {
    now = now.add(const Duration(seconds: 2));
    speech.closeTurn();
    speech.add(text, echoSuspect: false, now: now);
  }

  Future<ToolResult> call(String tool, [Map<String, dynamic> args = const {}, Session? s]) =>
      reg.execute(LiveToolCall('c${++_n}', tool, args), ToolEnv(session: s ?? session, speech: speech, lang: 'ru', now: () => now));

  Future<ToolResult> say(String phrase, String tool, [Map<String, dynamic> args = const {}, Session? s]) {
    user(phrase);
    return call(tool, args, s);
  }
}
