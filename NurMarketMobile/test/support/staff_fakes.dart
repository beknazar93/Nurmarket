// 2026-10-26 (голос-управление, В7): фейки для тестов задач, табеля и зарплаты — сервер NurCRM в памяти, без сети.
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/features/staff/salary_payout.dart';

typedef Handler = Future<dynamic> Function(String method, String path, Object? body, Map<String, dynamic>? query, Map<String, String>? headers);

class FakeApi extends ApiClient {
  FakeApi(this.handler) : super(SessionStore());
  Handler handler;
  final log = <String>[];

  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) {
    log.add('GET $path');
    return handler('GET', path, null, query, null);
  }

  @override
  Future<dynamic> post(String path, {Object? body, Map<String, String>? headers, Duration? timeout}) {
    log.add('POST $path');
    return handler('POST', path, body, null, headers);
  }

  @override
  Future<dynamic> patch(String path, {Object? body}) {
    log.add('PATCH $path');
    return handler('PATCH', path, body, null, null);
  }

  @override
  Future<dynamic> delete(String path) {
    log.add('DELETE $path');
    return handler('DELETE', path, null, null, null);
  }

  int count(String prefix) => log.where((l) => l.startsWith(prefix)).length;
}

class MemSettings implements SettingsPort {
  final data = <String, String>{};

  /// Журнал «не читается» (диск занят).
  bool failRead = false;
  @override
  Future<String?> get(String key) async {
    if (failRead) throw StateError('disk busy');
    return data[key];
  }

  @override
  Future<void> set(String key, String value) async => data[key] = value;
}

Map<String, dynamic> asMap(Object? b) => b is Map ? b.cast<String, dynamic>() : <String, dynamic>{};

/// Сервер NurCRM: задачи, сотрудники, смены, схема оплаты, отчёт зарплаты, кассы, статьи и движения денег.
class StaffServer {
  // задачи
  final tasks = <Map<String, dynamic>>[];
  int _taskN = 0;
  final taskPatches = <Map<String, dynamic>>[];
  final taskPosts = <Map<String, dynamic>>[];

  /// Сколько раз сервер отвергнет due_date в виде даты (ждёт дату со временем).
  bool dueNeedsTime = false;

  // сотрудники
  final employees = <Map<String, dynamic>>[
    {'id': 'u1', 'first_name': 'Айбек', 'last_name': 'Т.', 'role_display': 'кассир'},
    {'id': 'u2', 'first_name': 'Нурлан', 'last_name': 'Б.', 'role_display': 'кладовщик'},
    {'id': 'u3', 'first_name': 'Айгерим', 'last_name': 'С.', 'role_display': 'кассир'},
  ];

  // смены
  var shifts = <Map<String, dynamic>>[];

  // зарплата
  var salaryRows = <Map<String, dynamic>>[];
  final profiles = <Map<String, dynamic>>[];

  // деньги
  final cashboxes = <Map<String, dynamic>>[
    {'id': 'cb1', 'name': 'Основная', 'role': 'expense_variable', 'is_consumption': true, 'current_balance': '50000'},
    {'id': 'cb2', 'name': 'Касса филиала', 'role': 'pos_branch', 'is_consumption': false, 'current_balance': '90000'},
  ];
  final categories = <Map<String, dynamic>>[
    {'id': 'cat1', 'title': 'Зарплата', 'slug': 'salary'},
  ];
  final flows = <Map<String, dynamic>>[];
  final flowPosts = <Map<String, dynamic>>[];
  final cancelRequests = <String>[];
  int _flowN = 0;

  /// Сколько следующих POST расхода «потеряют ответ»: сервер записал, ответ не дошёл.
  int loseFlowResponse = 0;

  /// Поле, которое сервер отвергнет в расходе (400 по этому полю), пока оно есть в теле.
  String? rejectField;

  /// Сервер НЕ фильтрует ?search= (отдаёт всё подряд) — значит поиск по названию недоступен.
  bool searchBroken = false;
  bool categoryFilterBroken = false;

  /// Чтение движений недоступно (нет связи): выплаты не прочитать совсем.
  bool flowsDown = false;

  String addTask(String title, String assignee, {String? due, String status = 'pending', String description = ''}) {
    final id = 't${++_taskN}';
    tasks.add({'id': id, 'title': title, 'description': description, 'due_date': due, 'status': status, 'assigned_to': assignee});
    return id;
  }

  String addFlow({required String name, required double amount, String? sourceId, String? category, String status = 'approved', String? createdAt}) {
    final id = 'f${++_flowN}';
    flows.add({
      'id': id,
      'type': 'expense',
      'name': name,
      'amount': amount.toStringAsFixed(2),
      'source_id': sourceId ?? '',
      'category': category,
      'status': status,
      'created_at': createdAt ?? '2026-10-20T10:00:00+06:00',
    });
    return id;
  }

  /// Сервер отдаёт страницами по столько записей, сколько бы ни просили (проверка «все страницы»).
  int? forceSize;

  Map<String, dynamic> page(List<Map<String, dynamic>> all, Map<String, dynamic>? q, {int defaultSize = 100}) {
    final size = forceSize ?? int.tryParse('${q?['page_size'] ?? ''}') ?? defaultSize;
    final pageNo = int.tryParse('${q?['page'] ?? '1'}') ?? 1;
    final from = (pageNo - 1) * size;
    final slice = from >= all.length ? <Map<String, dynamic>>[] : all.sublist(from, (from + size).clamp(0, all.length));
    return {'count': all.length, 'next': from + size < all.length ? 'next' : null, 'results': slice};
  }

  Future<dynamic> handle(String m, String p, Object? b, Map<String, dynamic>? q, Map<String, String>? h) async {
    final body = asMap(b);
    // ---- задачи
    if (p == 'api/main/tasks/') {
      if (m == 'GET') return page(tasks, q);
      if (m == 'POST') {
        taskPosts.add(body);
        if (dueNeedsTime && body['due_date'] is String && !(body['due_date'] as String).contains('T')) {
          throw ApiException('due_date: Datetime has wrong format', status: 400, payload: {'due_date': ['Datetime has wrong format']});
        }
        final id = 't${++_taskN}';
        final t = {...body, 'id': id};
        tasks.add(t);
        return t;
      }
    }
    final tm = RegExp(r'^api/main/tasks/(t\d+)/$').firstMatch(p);
    if (tm != null) {
      final i = tasks.indexWhere((t) => t['id'] == tm.group(1));
      if (m == 'PATCH') {
        taskPatches.add(body);
        tasks[i] = {...tasks[i], ...body};
        return tasks[i];
      }
      if (m == 'DELETE') {
        tasks.removeAt(i);
        return null;
      }
    }
    // ---- сотрудники
    if (p == 'api/users/employees/') return page(employees, q);
    // ---- смены
    if (m == 'GET' && p == 'api/construction/shifts/') return page(shifts, q);
    // ---- зарплата
    if (m == 'GET' && p == 'api/main/analytics/market/' && q?['tab'] == 'salary') {
      return {
        'cards': {'total_payroll': 70000, 'total_base_prorated': 60000, 'total_percent_bonus': 10000, 'total_per_item_bonus': 0, 'total_employee_sales': 500000, 'employees_with_profile': salaryRows.length},
        'rows': salaryRows,
      };
    }
    if (p == 'api/main/market-sale-employee-pay-profiles/') {
      if (m == 'GET') return {'results': profiles.where((x) => '${x['user']}' == '${q?['user']}').toList()};
      if (m == 'POST') {
        final x = {...body, 'id': 'pp${profiles.length + 1}'};
        profiles.add(x);
        return x;
      }
    }
    final pm = RegExp(r'^api/main/market-sale-employee-pay-profiles/(pp\d+)/$').firstMatch(p);
    if (pm != null && m == 'PATCH') {
      final i = profiles.indexWhere((x) => x['id'] == pm.group(1));
      profiles[i] = {...profiles[i], ...body};
      return profiles[i];
    }
    // ---- деньги
    if (m == 'GET' && p == 'api/construction/cashboxes/') return page(cashboxes, q);
    if (p == 'api/construction/cashflow-categories/') {
      if (m == 'GET') return page(categories, q);
      if (m == 'POST') {
        final c = {'id': 'cat${categories.length + 1}', 'title': body['title']};
        categories.add(c);
        return c;
      }
    }
    if (p == 'api/construction/cashflows/') {
      if (m == 'POST') {
        flowPosts.add(body);
        if (rejectField != null && body.containsKey(rejectField)) {
          throw ApiException('$rejectField: неверное значение', status: 400, payload: {rejectField!: ['неверное значение']});
        }
        // идемпотентность по source_id: тот же source_id — тот же расход
        final sid = '${body['source_id'] ?? ''}';
        final same = sid.isEmpty ? null : flows.where((f) => f['source_id'] == sid).firstOrNull;
        final flow = same ?? {...body, 'id': 'f${++_flowN}', 'created_at': DateTime.now().toIso8601String()};
        if (same == null) flows.add(flow);
        if (loseFlowResponse > 0) {
          loseFlowResponse--;
          throw ApiException('Сервер не ответил вовремя.');
        }
        return flow;
      }
      if (m == 'GET') {
        if (flowsDown) throw ApiException('Нет связи с сервером.');
        var rows = flows.toList();
        if (q?['source_id'] != null) rows = rows.where((f) => f['source_id'] == q!['source_id']).toList();
        if (q?['category'] != null && !categoryFilterBroken) rows = rows.where((f) => '${f['category']}' == '${q!['category']}').toList();
        if (q?['search'] != null && !searchBroken) {
          final s = '${q!['search']}'.toLowerCase();
          rows = rows.where((f) => '${f['name']}'.toLowerCase().contains(s) || '${f['category_title'] ?? ''}'.toLowerCase().contains(s)).toList();
        }
        if (q?['type'] != null && q!['search'] == null) rows = rows.where((f) => f['type'] == q['type']).toList();
        return page(rows, q);
      }
    }
    final cm = RegExp(r'^api/construction/cashflows/(f\d+)/cancel-request/$').firstMatch(p);
    if (cm != null && m == 'POST') {
      cancelRequests.add(cm.group(1)!);
      return {'ok': true};
    }
    throw StateError('Нет в фейке: $m $p $q');
  }
}
