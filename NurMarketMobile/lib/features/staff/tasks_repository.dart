// 2026-10-26 (голос-управление, В7): задачи сотрудникам на существующем api/main/tasks/ (ТЗ, раздел 8.1). Своих полей сервер не
// даёт (Б-62): приоритет и напоминание живут служебной строкой ⟦nm: …⟧ в конце описания (кодек — staff_tools.dart), для
// высокого приоритета в заголовок добавляется «❗ » (сайт видит приоритет без разбора строки). Денег здесь нет.
import '../../core/ai/assistant/staff_tools.dart' show StaffTask, TaskState, decodeTaskMeta, encodeTaskMeta;
import '../../core/api/api_client.dart';

const kHighMark = '❗ ';

class TasksRepository {
  final ApiClient api;
  final DateTime Function() now;
  TasksRepository(this.api, {DateTime Function()? now}) : now = now ?? DateTime.now;

  // Кэш списка 60 с (ТЗ 8.1); любая запись его сбрасывает.
  List<StaffTask>? _cache;
  DateTime? _cachedAt;

  void forget() {
    _cache = null;
    _cachedAt = null;
  }

  /// Все страницы GET tasks/ (не больше 20, как getAllPages ТЗ). Фильтры по исполнителю и статусу — на телефоне.
  Future<List<StaffTask>> list({bool force = false}) async {
    final c = _cache;
    final at = _cachedAt;
    if (!force && c != null && at != null && now().difference(at) < const Duration(seconds: 60)) return c;
    final rows = await getAllPages(api, 'api/main/tasks/', pageSize: 100, maxPages: 20);
    final out = [for (final m in rows) parse(m)];
    _cache = out;
    _cachedAt = now();
    return out;
  }

  /// Задача из ответа сервера: приоритет и напоминание — из служебной строки; «❗» в заголовке без строки (правка на сайте) —
  /// тоже высокий приоритет.
  static StaffTask parse(Map<String, dynamic> m) {
    final meta = decodeTaskMeta(str(m, 'description') ?? '');
    var title = str(m, 'title') ?? '';
    var priority = meta.priority;
    if (title.startsWith('❗')) {
      title = title.substring(1).trimLeft();
      if (priority == 'normal') priority = 'high';
    }
    return StaffTask(
      id: str(m, 'id') ?? '',
      title: title,
      description: meta.text,
      due: _date(str(m, 'due_date')),
      status: TaskState.parse(str(m, 'status')),
      assigneeId: str(m, 'assigned_to') ?? '',
      priority: priority,
      remindAt: meta.remindAt,
      report: meta.report,
    );
  }

  static DateTime? _date(String? v) {
    if (v == null) return null;
    final d = DateTime.tryParse(v);
    if (d == null) return null;
    // дата без времени — как есть; со временем и зоной — местное время
    return v.length <= 10 ? d : d.toLocal();
  }

  static String _day(DateTime d) => '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  /// Тело POST/PATCH: заголовок с «❗» для высокого приоритета, описание со служебной строкой.
  static Map<String, dynamic> body(StaffTask t, {bool full = true, bool dateTime = false}) {
    final high = t.priority == 'high';
    final title = high ? '$kHighMark${t.title}' : t.title;
    final due = t.due;
    return {
      'title': title,
      'description': encodeTaskMeta(t.description, priority: t.priority, remindAt: t.remindAt, report: t.report),
      'due_date': due == null ? null : (dateTime ? '${_day(due)}T23:59:00' : _day(due)),
      'status': t.status.wire,
      if (t.assigneeId.isNotEmpty) 'assigned_to': t.assigneeId,
    };
  }

  /// Создать: POST tasks/. Ключ повтора уходит заголовком (сервер его пока не читает — Б-62, п. 6); без связи ответ мог не
  /// дойти, поэтому при сбое связи не повторяем молча (кто вызвал — тот и решает).
  Future<StaffTask> create(StaffTask draft, {String? key}) async {
    final headers = key == null ? null : {'Idempotency-Key': key};
    dynamic r;
    try {
      r = await api.post('api/main/tasks/', body: body(draft), headers: headers);
    } on ApiException catch (e) {
      // срок — дата со временем (DateTimeField сервера): один повтор в другом виде, только после явного отказа по этому полю
      if (e.status == 400 && draft.due != null && _dueRejected(e)) {
        r = await api.post('api/main/tasks/', body: body(draft, dateTime: true), headers: headers);
      } else {
        rethrow;
      }
    }
    forget();
    final m = r is Map ? r.cast<String, dynamic>() : <String, dynamic>{};
    final made = parse(m);
    return made.id.isEmpty ? StaffTask(id: '', title: draft.title, assigneeId: draft.assigneeId, due: draft.due, priority: draft.priority) : made;
  }

  static bool _dueRejected(ApiException e) {
    final p = e.payload;
    return (p is Map && p.containsKey('due_date')) || e.message.contains('due_date');
  }

  /// Изменить: PATCH tasks/{id}/ всеми полями задачи (отмена изменения шлёт прежние значения тем же способом).
  /// [statusOnly] — исполнитель (не руководитель): только статус и отчёт в описании, чужие поля не трогаем (Б-62: 403 иначе).
  Future<StaffTask> update(StaffTask t, {bool statusOnly = false}) async {
    Map<String, dynamic> pick(bool dt) {
      final b = body(t, dateTime: dt);
      return statusOnly ? {'status': b['status'], if (t.report.isNotEmpty) 'description': b['description']} : b;
    }

    dynamic r;
    try {
      r = await api.patch('api/main/tasks/${t.id}/', body: pick(false));
    } on ApiException catch (e) {
      if (!statusOnly && e.status == 400 && t.due != null && _dueRejected(e)) {
        r = await api.patch('api/main/tasks/${t.id}/', body: pick(true));
      } else {
        rethrow;
      }
    }
    forget();
    return r is Map ? parse(r.cast<String, dynamic>()) : t;
  }

  Future<void> delete(String id) async {
    await api.delete('api/main/tasks/$id/');
    forget();
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Чистые функции для экранов и плашки на главной
// ---------------------------------------------------------------------------------------------------------------------

/// Фильтры экрана «Задачи».
enum TaskFilter { all, overdue, mine, done }

List<StaffTask> filterTasks(List<StaffTask> all, TaskFilter f, {required String myId, required DateTime now}) {
  final r = switch (f) {
    TaskFilter.all => all.toList(),
    TaskFilter.overdue => all.where((t) => t.overdue(now)).toList(),
    TaskFilter.mine => all.where((t) => t.assigneeId == myId).toList(),
    TaskFilter.done => all.where((t) => t.status == TaskState.done).toList(),
  };
  r.sort(compareTasks(now));
  return r;
}

/// Порядок: просроченные, затем по сроку (без срока — в конце), сделанные — после несделанных.
int Function(StaffTask, StaffTask) compareTasks(DateTime now) => (a, b) {
  final da = a.status == TaskState.done ? 1 : 0;
  final db = b.status == TaskState.done ? 1 : 0;
  if (da != db) return da - db;
  final oa = a.overdue(now) ? 0 : 1;
  final ob = b.overdue(now) ? 0 : 1;
  if (oa != ob) return oa - ob;
  final c = (a.due ?? DateTime(2100)).compareTo(b.due ?? DateTime(2100));
  return c != 0 ? c : a.title.compareTo(b.title);
};

/// Плашка на главной: «Задач на сегодня: N · просрочено M» — задачи исполнителя [myId] со сроком сегодня (не сделанные) и просроченные.
({int today, int overdue}) todaySummary(List<StaffTask> all, String myId, DateTime now) {
  final mine = all.where((t) => t.assigneeId == myId && t.status != TaskState.done);
  final day = DateTime(now.year, now.month, now.day);
  var today = 0;
  var late = 0;
  for (final t in mine) {
    final d = t.due;
    if (d == null) continue;
    final dd = DateTime(d.year, d.month, d.day);
    if (dd == day) today++;
    if (dd.isBefore(day)) late++;
  }
  return (today: today, overdue: late);
}

/// Напоминание: срок напоминания наступил, задача не сделана (показывается при открытии приложения, фоновых уведомлений нет).
List<StaffTask> dueReminders(List<StaffTask> all, String myId, DateTime now) =>
    all.where((t) => t.assigneeId == myId && t.status != TaskState.done && t.remindAt != null && !t.remindAt!.isAfter(now)).toList();
