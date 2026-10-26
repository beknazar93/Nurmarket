// 2026-10-26 (голос-управление, В7): задачи сотрудникам — репозиторий на api/main/tasks/, служебная строка ⟦nm: …⟧, фильтры,
// плашка «на сегодня», инструменты звонка поверх настоящего источника. Сервер NurCRM не трогается — всё на фейках.
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/staff_tools.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/staff/tasks_repository.dart';

import 'support/staff_fakes.dart';
import 'support/staff_rig.dart';

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_staff_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  group('репозиторий задач: api/main/tasks/', () {
    test('создание: assigned_to, срок, статус pending; высокий приоритет — «❗» в заголовке и ⟦nm: priority=high⟧ в описании', () async {
      final srv = StaffServer();
      final repo = TasksRepository(FakeApi(srv.handle));
      final made = await repo.create(
        StaffTask(title: 'Пересчитать полку', assigneeId: 'u2', due: DateTime(2026, 10, 27), priority: 'high', remindAt: DateTime(2026, 10, 27, 9), description: 'с водой'),
        key: 'k1',
      );
      final sent = srv.taskPosts.single;
      expect(sent['assigned_to'], 'u2');
      expect(sent['status'], 'pending');
      expect(sent['due_date'], '2026-10-27');
      expect(sent['title'], '❗ Пересчитать полку');
      expect(sent['description'], contains('⟦nm: priority=high; remind=2026-10-27T09:00⟧'));
      expect(sent['description'], startsWith('с водой'));
      // обратно: приоритет и напоминание читаются, заголовок без «❗», описание без служебной строки
      expect(made.priority, 'high');
      expect(made.title, 'Пересчитать полку');
      expect(made.description, 'с водой');
      expect(made.remindAt, DateTime(2026, 10, 27, 9));
    });

    test('без служебной строки — «обычный» приоритет; обычная задача пишется без неё; «❗» в заголовке с сайта = высокий', () async {
      final srv = StaffServer();
      srv.addTask('Протереть витрину', 'u3', due: '2026-10-26');
      srv.addTask('❗ Принять накладную', 'u2');
      final repo = TasksRepository(FakeApi(srv.handle));
      final list = await repo.list();
      expect(list[0].priority, 'normal');
      expect(list[0].remindAt, isNull);
      expect(list[1].priority, 'high');
      expect(list[1].title, 'Принять накладную');
      await repo.create(const StaffTask(title: 'Обычная', assigneeId: 'u3'));
      expect(srv.taskPosts.single['description'], '');
      expect(srv.taskPosts.single['title'], 'Обычная');
    });

    test('чтение — все страницы; кэш 60 с, запись его сбрасывает', () async {
      final srv = StaffServer()..forceSize = 2;
      for (var i = 0; i < 5; i++) {
        srv.addTask('Задача $i', 'u1');
      }
      var now = DateTime(2026, 10, 26, 12);
      final api = FakeApi(srv.handle);
      final repo = TasksRepository(api, now: () => now);
      expect((await repo.list()).length, 5);
      final reads = api.count('GET api/main/tasks/');
      expect(reads, 3);
      await repo.list();
      expect(api.count('GET api/main/tasks/'), reads, reason: 'из кэша');
      now = now.add(const Duration(seconds: 61));
      await repo.list();
      expect(api.count('GET api/main/tasks/'), reads * 2, reason: 'кэш старше 60 с');
      await repo.create(const StaffTask(title: 'Ещё', assigneeId: 'u1'));
      await repo.list();
      expect(api.count('GET api/main/tasks/'), reads * 3, reason: 'запись сбросила кэш');
    });

    test('исполнитель (не руководитель) шлёт только статус — чужие поля не трогает (Б-62: иначе 403)', () async {
      final srv = StaffServer();
      final id = srv.addTask('Выставить ценники', 'u1', due: '2026-10-26');
      final repo = TasksRepository(FakeApi(srv.handle));
      final t = (await repo.list()).single;
      await repo.update(t.copyWith(status: TaskState.done), statusOnly: true);
      expect(srv.taskPatches.single, {'status': 'done'});
      expect(srv.tasks.single['id'], id);
      // руководитель — все поля
      await repo.update(t.copyWith(status: TaskState.inProgress, due: DateTime(2026, 10, 30)));
      expect(srv.taskPatches.last['due_date'], '2026-10-30');
      expect(srv.taskPatches.last['assigned_to'], 'u1');
    });

    test('сервер ждёт срок датой со временем: один повтор после явного 400 по due_date', () async {
      final srv = StaffServer()..dueNeedsTime = true;
      final repo = TasksRepository(FakeApi(srv.handle));
      final made = await repo.create(StaffTask(title: 'С датой', assigneeId: 'u1', due: DateTime(2026, 10, 28)));
      expect(srv.taskPosts.length, 2);
      expect(srv.taskPosts.last['due_date'], '2026-10-28T23:59:00');
      expect(made.title, 'С датой');
    });

    test('просрочена: срок раньше сегодня и не сделана; плашка «на сегодня» и «просрочено»; напоминание наступило', () {
      final now = DateTime(2026, 10, 26, 12);
      final all = [
        StaffTask(id: '1', title: 'Сегодня', assigneeId: 'u1', due: DateTime(2026, 10, 26)),
        StaffTask(id: '2', title: 'Вчера', assigneeId: 'u1', due: DateTime(2026, 10, 25)),
        StaffTask(id: '3', title: 'Вчера, но сделана', assigneeId: 'u1', due: DateTime(2026, 10, 25), status: TaskState.done),
        StaffTask(id: '4', title: 'Чужая', assigneeId: 'u2', due: DateTime(2026, 10, 25)),
        StaffTask(id: '5', title: 'Завтра', assigneeId: 'u1', due: DateTime(2026, 10, 27), remindAt: DateTime(2026, 10, 26, 9)),
        StaffTask(id: '6', title: 'Без срока', assigneeId: 'u1'),
      ];
      final s = todaySummary(all, 'u1', now);
      expect(s.today, 1);
      expect(s.overdue, 1);
      expect(dueReminders(all, 'u1', now).map((t) => t.id), ['5']);
      expect(all[1].overdue(now), isTrue);
      expect(all[2].overdue(now), isFalse);
      expect(filterTasks(all, TaskFilter.overdue, myId: 'u1', now: now).map((t) => t.id), ['2', '4']);
      expect(filterTasks(all, TaskFilter.mine, myId: 'u2', now: now).map((t) => t.id), ['4']);
      expect(filterTasks(all, TaskFilter.done, myId: 'u1', now: now).map((t) => t.id), ['3']);
      // порядок: просроченные, потом по сроку, сделанные в конце
      expect(filterTasks(all, TaskFilter.all, myId: 'u1', now: now).map((t) => t.id), ['2', '4', '1', '5', '6', '3']);
    });
  });

  group('инструменты звонка поверх настоящего источника', () {
    test('«поставь Нурлану задачу…» → задача на сервере с assigned_to; «отмени» — удаляется (DELETE)', () async {
      final r = StaffRig();
      final res = await r.say('поставь Нурлану задачу пересчитать полку с водой до завтра', 'task_create', {'title': 'Пересчитать полку с водой', 'assignee': 'Нурлан', 'due': '2026-10-27'});
      expect(res.status, ToolStatus.ok, reason: res.say);
      expect(res.undo, isTrue);
      expect(r.server.taskPosts.single['assigned_to'], 'u2');
      expect(r.server.tasks.length, 1);
      final u = await r.say('отмени', 'undo_last');
      expect(u.status, ToolStatus.ok, reason: u.say);
      expect(r.server.tasks, isEmpty);
    });

    test('«срочно» → приоритет high и «❗» для сайта; без слова «срочно» ИИ приоритет не ставит', () async {
      final r = StaffRig();
      final bad = await r.say('поставь Нурлану задачу полить цветы', 'task_create', {'title': 'Полить цветы', 'assignee': 'Нурлан', 'priority': 'high'});
      expect(bad.status, isNot(ToolStatus.ok));
      expect(r.server.taskPosts, isEmpty);
      final ok = await r.say('поставь Нурлану срочную задачу полить цветы', 'task_create', {'title': 'Полить цветы', 'assignee': 'Нурлан', 'priority': 'high'});
      expect(ok.status, ToolStatus.ok, reason: ok.say);
      expect(r.server.taskPosts.single['title'], startsWith('❗'));
    });

    test('«кто что не сделал»: таблица по сотрудникам, просроченные считает телефон', () async {
      final r = StaffRig();
      r.server.addTask('А', 'u1', due: '2026-10-20');
      r.server.addTask('Б', 'u1', due: '2026-10-21');
      r.server.addTask('В', 'u1', due: '2026-10-22', status: 'done');
      r.server.addTask('Г', 'u2', due: '2026-10-23');
      r.server.addTask('Д', 'u3', due: '2026-10-30');
      final res = await r.say('кто что не сделал за неделю', 'tasks_report', {'from': '2026-10-19', 'to': '2026-10-31'});
      expect(res.status, ToolStatus.ok, reason: res.say);
      expect(res.say, contains('Просрочено задач: 3'));
      expect(res.say, contains('2'));
      final t = r.tables.current!;
      expect(t.rows.length, 3);
      // имена вслух не уходят
      expect(jsonEncode(res.toJson()), isNot(contains('Айбек')));
    });

    test('«мои задачи»: исполнитель без права управлять видит только свои; чужую задачу менять нельзя', () async {
      final r = StaffRig(session: cashierSession);
      r.server.addTask('Моя', 'u1', due: '2026-10-26');
      final other = r.server.addTask('Чужая', 'u2', due: '2026-10-26');
      final list = await r.say('какие у меня задачи', 'tasks_list', {});
      expect(list.status, ToolStatus.ok, reason: list.say);
      expect(r.tables.current!.rows.length, 1);
      final done = await r.say('моя задача готова', 'task_update', {'task': 'Моя', 'status': 'done'});
      expect(done.status, ToolStatus.ok, reason: done.say);
      expect(r.server.tasks.firstWhere((t) => t['title'] == 'Моя')['status'], 'done');
      // исполнитель отправил ТОЛЬКО статус
      expect(r.server.taskPatches.last.keys.toSet(), {'status'});
      final foreign = await r.say('чужая задача готова', 'task_update', {'task': 'Чужая', 'status': 'done'});
      expect(foreign.status, isNot(ToolStatus.ok));
      expect(r.server.tasks.firstWhere((t) => t['id'] == other)['status'], 'pending');
      // постановка задач кассиру недоступна
      expect(r.reg.forSession(cashierSession).map((t) => t.name), isNot(contains('task_create')));
      final denied = await r.say('поставь Нурлану задачу', 'task_create', {'title': 'x', 'assignee': 'Нурлан'});
      expect(denied.status, ToolStatus.denied);
    });

    test('два сотрудника с похожим именем → «какой — скажите номер», задача не создаётся', () async {
      final r = StaffRig();
      r.server.employees.add({'id': 'u9', 'first_name': 'Айбек', 'last_name': 'К.', 'role_display': 'продавец'});
      r.server.employees.add({'id': 'u8', 'first_name': 'Айбек', 'last_name': 'Т.', 'role_display': 'кассир'});
      final res = await r.say('поручи Айбеку пересчитать полку', 'task_create', {'title': 'Пересчитать полку', 'assignee': 'Айбек'});
      expect(res.status, ToolStatus.ambiguous);
      expect(r.server.taskPosts, isEmpty);
    });

    test('права: поставить задачу может руководитель; Session.manageTasks / viewSalary / editSalary и маршруты', () {
      final owner = ownerSession;
      expect(owner.manageTasks && owner.viewSalary && owner.editSalary, isTrue);
      expect(owner.canRoute('/admin/tasks') && owner.canRoute('/admin/salary') && owner.canRoute('/tasks/my'), isTrue);
      final cashier = cashierSession;
      expect(cashier.manageTasks || cashier.viewSalary || cashier.editSalary, isFalse);
      expect(cashier.canRoute('/tasks/my'), isTrue, reason: 'мои задачи открыты всем');
      expect(cashier.canRoute('/admin/tasks'), isFalse);
      expect(cashier.canRoute('/admin/salary'), isFalse);
      final manager = sess(role: 'manager', perms: const [Session.viewEmployees]);
      expect(manager.manageTasks, isTrue);
      expect(manager.viewSalary, isFalse);
      final accountant = sess(role: 'accountant', perms: const ['can_view_salary']);
      expect(accountant.viewSalary, isTrue);
      expect(accountant.editSalary, isFalse);
      expect(accountant.canRoute('/admin/salary'), isTrue);
    });
  });
}
