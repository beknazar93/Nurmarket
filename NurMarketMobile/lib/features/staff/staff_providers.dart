// 2026-10-26 (голос-управление, В7): провайдеры сотрудников, задач, табеля и зарплаты. Отдельный файл — общий providers.dart
// не трогаем (им пользуются остальные пакеты).
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/ai/assistant/staff_tools.dart' show StaffSource, StaffTask;
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../admin/admin_more.dart' show staffRepoProvider;
import 'salary_payout.dart';
import 'salary_repository.dart';
import 'staff_source.dart';
import 'tasks_repository.dart';
import 'timesheet.dart';

/// Настройки телефона (таблица settings) как хранилище журнала выплат.
class DbSettingsPort implements SettingsPort {
  final AppDatabase db;
  DbSettingsPort(this.db);
  @override
  Future<String?> get(String key) => db.getSetting(key);
  @override
  Future<void> set(String key, String value) => db.setSetting(key, value);
}

final tasksRepoProvider = Provider((ref) => TasksRepository(ref.watch(apiProvider)));
final timesheetRepoProvider = Provider((ref) => TimesheetRepository(ref.watch(apiProvider)));
final salaryRepoProvider = Provider((ref) => SalaryRepository(ref.watch(apiProvider)));

/// Один сервис выплат на вход: журнал выплат и очередь записи общие для экрана «Зарплата» и голоса. Пересоздаётся только при
/// смене учётной записи (а не при каждом обновлении токена).
final salaryPayoutProvider = Provider((ref) {
  final who = ref.watch(sessionProvider.select((s) => s == null ? '' : '${s.email}|${s.userName}|${s.isOwner}|${s.role}'));
  final s = ref.read(sessionProvider);
  final owner = who.isNotEmpty && (s?.isOwner == true || (s?.role ?? '').toLowerCase() == 'owner');
  return SalaryPayoutService(
    ref.watch(apiProvider),
    DbSettingsPort(ref.watch(databaseProvider)),
    scope: s?.email ?? '',
    byName: s?.userName ?? '',
    approvedRightAway: owner,
  );
});

final staffSourceProvider = Provider<StaffSource>(
  (ref) => ApiStaffSource(
    staffRepo: ref.watch(staffRepoProvider),
    tasksRepo: ref.watch(tasksRepoProvider),
    timesheetRepo: ref.watch(timesheetRepoProvider),
    salaryRepo: ref.watch(salaryRepoProvider),
    payoutService: ref.watch(salaryPayoutProvider),
    session: () => ref.read(sessionProvider),
  ),
);

/// Мои задачи для плашки на главной и экрана «Мои задачи». Ошибка связи и вход без токена — пусто, без шума на главной.
final myTasksProvider = FutureProvider.autoDispose<List<StaffTask>>((ref) async {
  final s = ref.read(sessionProvider);
  if (s == null || s.access.isEmpty || s.userId.isEmpty) return const [];
  try {
    final all = await ref.read(tasksRepoProvider).list();
    return all.where((t) => t.assigneeId == s.userId).toList();
  } catch (_) {
    return const [];
  }
});
