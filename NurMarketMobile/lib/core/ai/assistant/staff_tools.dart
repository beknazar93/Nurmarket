// 2026-10-26 (голос-управление, В3а): инструменты сотрудников — список, задачи, табель, зарплата, выплата, схема оплаты
// (раздел 8 ТЗ). ИИ ставит задачи и ведёт их, считает «сколько я должен Айбеку за октябрь», выплачивает по просьбе.
//
// ЧЕСТНО О ТОМ, ЧЕГО В МОБИЛКЕ ЕЩЁ НЕТ (поправка владельца: не выдумывать сетевые методы, а отвечать «недоступно»):
//  - в мобилке есть ТОЛЬКО список сотрудников (`StaffRepository.list`, api/users/employees/). Репозиториев задач
//    (api/main/tasks/), табеля, зарплаты (analytics/market/?tab=salary), выплат (cashflows «Зарплата» по правилам ПК
//    `SalaryPayoutService`) и схемы оплаты (market-sale-employee-pay-profiles) пока нет — их пишет пакет В7;
//  - здесь — порт [StaffSource]: В7 реализует методы и передаёт источник в [registerStaffTools]; инструменты, чьих возможностей
//    у источника нет, объявляются коротко и отвечают «недоступно» (модель не выдумывает и не говорит «сделано»);
//  - штрафы, авансы и премии как записи сервер не хранит (Б-63) — инструмент `staff_fine` честно так и отвечает;
//  - приоритет и напоминание задачи до Б-62 хранятся служебной строкой в описании (кодек ниже).
// Выплата — только по правилам ПК (ключ в source_id, «ответ не дошёл — не повторять», при неполном чтении сумма сама не
// подставляется): это обязанность реализации [StaffSource.payout] (В7); здесь — ворота, права, объект просьбы и отказ повторять.
import 'dart:async';

import '../../../features/admin/admin_more.dart' show StaffRepository;
import '../../api/api_client.dart';
import '../../api/session_store.dart';
import '../live_tools.dart';
import 'action_texts.dart' show am;
import 'action_tools.dart' show ActionKit;
import 'call_table.dart';
import 'intent_gate.dart';
import 'read_tools.dart' show parsePeriod;
import 'screen_registry.dart' show productNameMatchesQuery;
import 'tool_registry.dart';
import 'undo.dart';

// ---------------------------------------------------------------------------------------------------------------------
// Права (В7 перенесёт их в Session; значения — как в разделе 7 ТЗ)
// ---------------------------------------------------------------------------------------------------------------------

// 2026-10-26 (В7): права теперь живут в Session (viewSalary / editSalary / manageTasks), здесь — те же правила под старыми именами.
bool canViewSalary(Session s) => s.viewSalary;
bool canEditSalary(Session s) => s.editSalary;
bool canManageTasks(Session s) => s.manageTasks;

// ---------------------------------------------------------------------------------------------------------------------
// Модели и порт
// ---------------------------------------------------------------------------------------------------------------------

class StaffPerson {
  final String id;
  final String name;
  final String role;
  const StaffPerson(this.id, this.name, this.role);
}

enum TaskState {
  pending('pending'),
  inProgress('in_progress'),
  done('done');

  final String wire;
  const TaskState(this.wire);
  static TaskState parse(String? v) => values.firstWhere((s) => s.wire == v, orElse: () => TaskState.pending);
}

class StaffTask {
  final String id;
  final String title;
  final String description;
  final DateTime? due;
  final TaskState status;
  final String assigneeId;

  /// low | normal | high — до Б-62 из служебной строки описания.
  final String priority;
  final DateTime? remindAt;
  final String report;
  const StaffTask({
    this.id = '',
    required this.title,
    this.description = '',
    this.due,
    this.status = TaskState.pending,
    required this.assigneeId,
    this.priority = 'normal',
    this.remindAt,
    this.report = '',
  });

  StaffTask copyWith({String? title, DateTime? due, TaskState? status, String? assigneeId, String? priority, String? report, bool clearDue = false}) => StaffTask(
    id: id,
    title: title ?? this.title,
    description: description,
    due: clearDue ? null : (due ?? this.due),
    status: status ?? this.status,
    assigneeId: assigneeId ?? this.assigneeId,
    priority: priority ?? this.priority,
    remindAt: remindAt,
    report: report ?? this.report,
  );

  /// Просрочена: срок раньше сегодняшнего дня и не сделана (считает телефон).
  bool overdue(DateTime now) => due != null && status != TaskState.done && DateTime(due!.year, due!.month, due!.day).isBefore(DateTime(now.year, now.month, now.day));
}

// Служебная строка в конце описания (до Б-62): ⟦nm: priority=high; remind=2026-10-27T09:00; report=…⟧. Сайт показывает её как
// текст. Читает и пишет мобилка. Без строки — «обычный» приоритет.
final _metaRe = RegExp('⟦nm:([^⟧]*)⟧');

/// Описание без служебной строки + поля из неё.
({String text, String priority, DateTime? remindAt, String report}) decodeTaskMeta(String description) {
  final m = _metaRe.firstMatch(description);
  var priority = 'normal';
  DateTime? remind;
  var report = '';
  if (m != null) {
    for (final part in m.group(1)!.split(';')) {
      final i = part.indexOf('=');
      if (i < 0) continue;
      final k = part.substring(0, i).trim();
      final v = part.substring(i + 1).trim();
      switch (k) {
        case 'priority':
          if (const {'low', 'normal', 'high'}.contains(v)) priority = v;
        case 'remind':
          remind = DateTime.tryParse(v);
        case 'report':
          report = v;
      }
    }
  }
  return (text: description.replaceAll(_metaRe, '').trim(), priority: priority, remindAt: remind, report: report);
}

String encodeTaskMeta(String text, {String priority = 'normal', DateTime? remindAt, String report = ''}) {
  final parts = [
    if (priority != 'normal') 'priority=$priority',
    if (remindAt != null) 'remind=${remindAt.toIso8601String().substring(0, 16)}',
    if (report.isNotEmpty) 'report=${report.replaceAll(RegExp(r'[;⟧⟦]'), ',')}',
  ];
  final base = text.replaceAll(_metaRe, '').trim();
  return parts.isEmpty ? base : '${base.isEmpty ? '' : '$base\n'}⟦nm: ${parts.join('; ')}⟧';
}

class TimesheetRow {
  final String employeeId;
  final String name;
  final int shifts;
  final int days;
  final double hours;
  final double revenue;
  const TimesheetRow(this.employeeId, this.name, this.shifts, this.days, this.hours, this.revenue);
}

class SalaryRow {
  final String employeeId;
  final String name;
  final String scheme;
  final double base;
  final double percent;
  final double perItem;
  final double accrued;

  /// Выплачено по меткам расходов «Зарплата» (как ПК); null — не читалось.
  final double? paid;

  /// Выплаты прочитаны не полностью: «осталось» неточно, сумма сама не подставляется.
  final bool partial;

  /// 2026-10-26 (В7): оклад в месяц по схеме (base — оклад за период), начисление «за товары» и проценты одной суммой, схема не настроена.
  final double monthly;
  final double bonus;
  final bool noScheme;
  const SalaryRow(
    this.employeeId,
    this.name,
    this.scheme,
    this.base,
    this.percent,
    this.perItem,
    this.accrued, {
    this.paid,
    this.partial = false,
    this.monthly = 0,
    this.bonus = 0,
    this.noScheme = false,
  });

  SalaryRow withPaid(double? paid, bool partial) =>
      SalaryRow(employeeId, name, scheme, base, percent, perItem, accrued, paid: paid, partial: partial, monthly: monthly, bonus: bonus, noScheme: noScheme);

  double? get left => paid == null ? null : accrued - paid!;
}

class PayoutOutcome {
  /// Выплата проведена.
  final bool ok;

  /// Ответ сервера не дошёл: выплата могла пройти, повторять НЕЛЬЗЯ (замок до решения владельца).
  final bool unknown;
  final String cashbox;
  final double? left;
  final String? error;

  /// 2026-10-26 (В7): предупреждение для ответа (касса продаж филиала — расход уменьшит её остаток).
  final String note;
  const PayoutOutcome({this.ok = false, this.unknown = false, this.cashbox = '', this.left, this.error, this.note = ''});
}

class PaySchemeData {
  final String scheme;
  final double base;
  final double percent;
  final double perItem;
  const PaySchemeData(this.scheme, this.base, this.percent, this.perItem);
}

/// Что умеет источник (чего нет — инструмент отвечает «недоступно»).
enum StaffCap { staff, tasks, timesheet, salary, payout, scheme }

/// Откуда инструменты берут сотрудников, задачи, табель и зарплату. Реализация задач, табеля, зарплаты и выплат — пакет В7
/// (`tasks_repository.dart`, `timesheet.dart`, `salary_repository.dart`, `salary_payout.dart`); здесь — договорённость.
abstract class StaffSource {
  Set<StaffCap> get caps;
  Future<List<StaffPerson>> staff();
  Future<List<StaffTask>> tasks();
  Future<StaffTask> createTask(StaffTask draft);
  Future<StaffTask> updateTask(StaffTask updated);
  Future<void> deleteTask(String id);
  Future<List<TimesheetRow>> timesheet(DateTime from, DateTime to);
  Future<List<SalaryRow>> salary(DateTime from, DateTime to);

  /// Выплата по правилам ПК (SalaryPayoutService): расход со статьёй «Зарплата», метка периода, ключ в source_id, замок при
  /// неизвестном исходе. [kind] — salary | advance.
  Future<PayoutOutcome> payout({required SalaryRow row, required double amount, required String kind, required DateTime from, required DateTime to, required String method});
  Future<PaySchemeData?> scheme(String employeeId);
  Future<void> setScheme(String employeeId, PaySchemeData data);
}

/// То, что есть в мобилке сейчас: только список сотрудников (api/users/employees/). Остальное — «недоступно» до В7.
class RepoStaffSource implements StaffSource {
  final StaffRepository repo;
  RepoStaffSource(this.repo);

  @override
  Set<StaffCap> get caps => const {StaffCap.staff};

  @override
  Future<List<StaffPerson>> staff() async => [for (final e in await repo.list()) StaffPerson(e.id, e.name, e.role)];

  Never _no() => throw UnsupportedError('Не реализовано в мобилке (пакет В7)');
  @override
  Future<List<StaffTask>> tasks() => _no();
  @override
  Future<StaffTask> createTask(StaffTask draft) => _no();
  @override
  Future<StaffTask> updateTask(StaffTask updated) => _no();
  @override
  Future<void> deleteTask(String id) => _no();
  @override
  Future<List<TimesheetRow>> timesheet(DateTime from, DateTime to) => _no();
  @override
  Future<List<SalaryRow>> salary(DateTime from, DateTime to) => _no();
  @override
  Future<PayoutOutcome> payout({required SalaryRow row, required double amount, required String kind, required DateTime from, required DateTime to, required String method}) => _no();
  @override
  Future<PaySchemeData?> scheme(String employeeId) => _no();
  @override
  Future<void> setScheme(String employeeId, PaySchemeData data) => _no();
}

// ---------------------------------------------------------------------------------------------------------------------
// Тексты
// ---------------------------------------------------------------------------------------------------------------------

const _t = <String, List<String>>{
  'unav.tasks': [
    'Задачи сотрудникам в телефоне пока недоступны. Это появится после обновления — сейчас поставьте задачу на сайте.',
    'Кызматкерлерге тапшырмалар телефондо азырынча жеткиликсиз. Жаңыртуудан кийин чыгат — азыр сайттан коюңуз.',
    "Xodimlarga vazifalar telefonda hozircha mavjud emas. Yangilanishdan keyin chiqadi — hozir saytda qo'ying.",
    'Tasks for staff are not available in the phone app yet. They will come with an update — for now create the task on the site.',
    'Çalışanlara görev telefonda henüz yok. Güncellemeyle gelecek — şimdilik siteden verin.',
  ],
  'unav.timesheet': [
    'Табель в телефоне пока недоступен.',
    'Табель телефондо азырынча жеткиликсиз.',
    'Tabel telefonda hozircha mavjud emas.',
    'The timesheet is not available in the phone app yet.',
    'Puantaj telefonda henüz yok.',
  ],
  'unav.salary': [
    'Зарплата в телефоне пока недоступна: расчёт смотрите на сайте или в программе владельца.',
    'Эмгек акы телефондо азырынча жеткиликсиз: эсепти сайттан же ээсинин программасынан караңыз.',
    "Maosh telefonda hozircha mavjud emas: hisobni saytda yoki egasi dasturida ko'ring.",
    'Salary is not available in the phone app yet: see the calculation on the site or in the owner program.',
    'Maaş telefonda henüz yok: hesaplamayı sitede veya sahip programında görün.',
  ],
  'unav.payout': [
    'Выплата зарплаты с телефона пока недоступна: выплатите в программе владельца или на сайте.',
    'Айлыкты телефондон төлөө азырынча жеткиликсиз: ээсинин программасында же сайтта төлөңүз.',
    "Maoshni telefondan to'lash hozircha mavjud emas: egasi dasturida yoki saytda to'lang.",
    'Paying salary from the phone is not available yet: pay it in the owner program or on the site.',
    'Maaş ödemesi telefondan henüz yapılamıyor: sahip programında veya sitede ödeyin.',
  ],
  'unav.scheme': [
    'Смена схемы оплаты в телефоне пока недоступна: измените на сайте или в программе владельца.',
    'Төлөм схемасын телефондо азырынча өзгөртүүгө болбойт: сайттан же ээсинин программасынан өзгөртүңүз.',
    "To'lov sxemasini telefonda hozircha o'zgartirib bo'lmaydi: saytda yoki egasi dasturida o'zgartiring.",
    'The pay scheme cannot be changed from the phone yet: change it on the site or in the owner program.',
    'Ödeme şeması telefondan henüz değiştirilemiyor: sitede veya sahip programında değiştirin.',
  ],
  'fineNo': [
    'Штрафы и премии сервер пока не хранит — я ничего не создала.',
    'Айып пулдарды жана сыйлыктарды сервер азырынча сактабайт — мен эч нерсе түзгөн жокмун.',
    "Jarima va mukofotlarni server hozircha saqlamaydi — men hech narsa yaratmadim.",
    'The server does not store fines and bonuses yet — I created nothing.',
    'Sunucu ceza ve primleri henüz saklamıyor — hiçbir şey oluşturmadım.',
  ],
  'needStaff': [
    'Какому сотруднику? Назовите имя.',
    'Кайсы кызматкерге? Атын айтыңыз.',
    'Qaysi xodimga? Ismini ayting.',
    'Which employee? Say the name.',
    'Hangi çalışan? Adını söyleyin.',
  ],
  'staffNone': [
    'Сотрудника с таким именем не нашла.',
    'Мындай аттагы кызматкер табылган жок.',
    'Bunday ismli xodim topilmadi.',
    'No employee with that name found.',
    'Bu isimde çalışan bulunamadı.',
  ],
  'staffMany': [
    'Сотрудников с таким именем несколько: {list}. Какой — скажите номер.',
    'Мындай аттагы кызматкер бир нече: {list}. Кайсынысы — номерин айтыңыз.',
    'Bunday ismli xodim bir nechta: {list}. Qaysi biri — raqamini ayting.',
    'Several employees match: {list}. Which one — say the number.',
    'Bu isimde birkaç çalışan var: {list}. Hangisi — numarasını söyleyin.',
  ],
  'staffSay': [
    'Сотрудников: {n}. Список на экране.',
    'Кызматкерлер: {n}. Тизме экранда.',
    "Xodimlar: {n} ta. Ro'yxat ekranda.",
    '{n} employees. The list is on the screen.',
    '{n} çalışan. Liste ekranda.',
  ],
  'needTitle': [
    'Что за задача? Скажите коротко.',
    'Кандай тапшырма? Кыскача айтыңыз.',
    'Qanday vazifa? Qisqacha ayting.',
    'What is the task? Say it briefly.',
    'Görev ne? Kısaca söyleyin.',
  ],
  'taskCreated': [
    'Поставила задачу «{title}» сотруднику {name}{due}{prio}.',
    '«{title}» тапшырмасын {name} кызматкерине койдум{due}{prio}.',
    "«{title}» vazifasini {name} xodimiga qo'ydim{due}{prio}.",
    'Created the task "{title}" for {name}{due}{prio}.',
    '"{title}" görevi {name} için oluşturuldu{due}{prio}.',
  ],
  'dueWord': [', срок {date}', ', мөөнөтү {date}', ', muddati {date}', ', due {date}', ', son tarih {date}'],
  'prioHigh': [', срочная', ', шашылыш', ', shoshilinch', ', urgent', ', acil'],
  'taskNone': [
    'Такую задачу не нашла.',
    'Мындай тапшырма табылган жок.',
    'Bunday vazifa topilmadi.',
    'No such task found.',
    'Böyle bir görev bulunamadı.',
  ],
  'taskMany': [
    'Подходящих задач несколько: {list}. Какая — скажите номер.',
    'Ылайыктуу тапшырма бир нече: {list}. Кайсынысы — номерин айтыңыз.',
    'Mos vazifa bir nechta: {list}. Qaysi biri — raqamini ayting.',
    'Several tasks match: {list}. Which one — say the number.',
    'Birkaç uygun görev var: {list}. Hangisi — numarasını söyleyin.',
  ],
  'taskForeign': [
    'Чужую задачу менять нельзя — только свою.',
    'Башка кызматкердин тапшырмасын өзгөртүүгө болбойт — өзүңкүн гана.',
    "Boshqa xodimning vazifasini o'zgartirib bo'lmaydi — faqat o'zingiznikini.",
    "You cannot change someone else's task — only your own.",
    'Başkasının görevini değiştiremezsiniz — yalnızca kendi görevinizi.',
  ],
  'taskUpdated': [
    'Задача «{title}»: {what}.',
    '«{title}» тапшырмасы: {what}.',
    "«{title}» vazifasi: {what}.",
    'Task "{title}": {what}.',
    '"{title}" görevi: {what}.',
  ],
  'ts.pending': ['новая', 'жаңы', 'yangi', 'new', 'yeni'],
  'ts.in_progress': ['в работе', 'иште', 'jarayonda', 'in progress', 'devam ediyor'],
  'ts.done': ['сделана', 'аткарылды', 'bajarildi', 'done', 'tamamlandı'],
  'what.due': ['срок {date}', 'мөөнөтү {date}', 'muddati {date}', 'due {date}', 'son tarih {date}'],
  'what.assignee': ['теперь у {name}', 'эми {name}де', 'endi {name}da', 'now with {name}', 'artık {name} üzerinde'],
  'what.priority': ['срочная', 'шашылыш', 'shoshilinch', 'urgent', 'acil'],
  'tasksNone': ['Задач нет.', 'Тапшырма жок.', "Vazifalar yo'q.", 'No tasks.', 'Görev yok.'],
  'tasksSay': [
    'Задач {n}, просрочено {late}.',
    'Тапшырма {n}, мөөнөтү өткөн {late}.',
    "Vazifalar {n} ta, muddati o'tgan {late} ta.",
    '{n} tasks, {late} overdue.',
    '{n} görev, {late} gecikmiş.',
  ],
  'reportSay': [
    'Просрочено задач: {late}. Больше всего у одного сотрудника — {worst}. Таблица на экране.',
    'Мөөнөтү өткөн тапшырма: {late}. Көбү бир кызматкерде — {worst}. Таблица экранда.',
    "Muddati o'tgan vazifalar: {late} ta. Ko'pi bitta xodimda — {worst} ta. Jadval ekranda.",
    'Overdue tasks: {late}. The most for one employee — {worst}. The table is on the screen.',
    'Geciken görev: {late}. Tek çalışanda en çok — {worst}. Tablo ekranda.',
  ],
  'reportNone': [
    'Просроченных задач нет.',
    'Мөөнөтү өткөн тапшырма жок.',
    "Muddati o'tgan vazifa yo'q.",
    'No overdue tasks.',
    'Geciken görev yok.',
  ],
  'tsSay': [
    'Табель за {period}: сотрудников {n}, всего {hours} ч.',
    '{period} үчүн табель: кызматкер {n}, бардыгы {hours} саат.',
    "{period} uchun tabel: xodimlar {n} ta, jami {hours} soat.",
    'Timesheet for {period}: {n} employees, {hours} h in total.',
    '{period} puantajı: {n} çalışan, toplam {hours} saat.',
  ],
  'tsNone': [
    'За {period} закрытых смен нет.',
    '{period} үчүн жабылган смена жок.',
    "{period} uchun yopilgan smena yo'q.",
    'No closed shifts for {period}.',
    '{period} için kapanmış vardiya yok.',
  ],
  'salarySay': [
    'За {period} начислено {accrued}, выплачено {paid}, осталось {left}.{partial}',
    '{period} үчүн эсептелди {accrued}, төлөндү {paid}, калды {left}.{partial}',
    "{period} uchun hisoblandi {accrued}, to'landi {paid}, qoldi {left}.{partial}",
    'For {period}: accrued {accrued}, paid {paid}, left {left}.{partial}',
    '{period} için tahakkuk {accrued}, ödenen {paid}, kalan {left}.{partial}',
  ],
  'dueSay': [
    '{name}: начислено {accrued}, выплачено {paid}, осталось {left}.{partial}',
    '{name}: эсептелди {accrued}, төлөндү {paid}, калды {left}.{partial}',
    "{name}: hisoblandi {accrued}, to'landi {paid}, qoldi {left}.{partial}",
    '{name}: accrued {accrued}, paid {paid}, left {left}.{partial}',
    '{name}: tahakkuk {accrued}, ödenen {paid}, kalan {left}.{partial}',
  ],
  'dueUnknown': [
    '{name}: начислено {accrued}. Выплаты прочитать не удалось — остаток точно не знаю.',
    '{name}: эсептелди {accrued}. Төлөмдөрдү окуй албадым — калганын так билбейм.',
    "{name}: hisoblandi {accrued}. To'lovlarni o'qiy olmadim — qoldig'ini aniq bilmayman.",
    '{name}: accrued {accrued}. I could not read the payouts — I do not know the remainder exactly.',
    '{name}: tahakkuk {accrued}. Ödemeleri okuyamadım — kalanı tam bilmiyorum.',
  ],
  'partial': [
    ' Проверено частично — цифры могут быть неточными.',
    ' Жарым-жартылай текшерилди — сандар так эмес болушу мүмкүн.',
    " Qisman tekshirildi — raqamlar noaniq bo'lishi mumkin.",
    ' Checked partially — the figures may be inexact.',
    ' Kısmen kontrol edildi — rakamlar kesin olmayabilir.',
  ],
  'salaryNone': [
    'За {period} начислений нет.',
    '{period} үчүн эсептөө жок.',
    "{period} uchun hisob-kitob yo'q.",
    'No accruals for {period}.',
    '{period} için tahakkuk yok.',
  ],
  'payoutDone': [
    'Выплатила {name}: {amount} ({kind}, {method}){cashbox}. Осталось {left}.',
    '{name}ге төлөдүм: {amount} ({kind}, {method}){cashbox}. Калды {left}.',
    "{name}ga to'ladim: {amount} ({kind}, {method}){cashbox}. Qoldi {left}.",
    'Paid {name}: {amount} ({kind}, {method}){cashbox}. Left {left}.',
    '{name} için ödendi: {amount} ({kind}, {method}){cashbox}. Kalan {left}.',
  ],
  'payoutAmountNeeded': [
    'Остаток посчитан не полностью — скажите сумму выплаты.',
    'Калды толук эсептелген жок — төлөм суммасын айтыңыз.',
    "Qoldiq to'liq hisoblanmagan — to'lov summasini ayting.",
    'The remainder is not fully computed — say the payout amount.',
    'Kalan tam hesaplanmadı — ödeme tutarını söyleyin.',
  ],
  'payoutNothing': [
    'К выплате ничего не осталось.',
    'Төлөөгө эч нерсе калган жок.',
    "To'lashga hech narsa qolmadi.",
    'Nothing is left to pay.',
    'Ödenecek bir şey kalmadı.',
  ],
  'kind.salary': ['зарплата', 'айлык', 'maosh', 'salary', 'maaş'],
  'kind.advance': ['аванс', 'аванс', 'avans', 'advance', 'avans'],
  'meth.cash': ['наличными', 'накталай', 'naqd', 'cash', 'nakit'],
  'meth.transfer': ['переводом', 'которуу менен', "o'tkazma bilan", 'by transfer', 'havale ile'],
  'cashboxWord': [', касса «{name}»', ', касса «{name}»', ', kassa «{name}»', ', cash register "{name}"', ', kasa "{name}"'],
  'schemeDone': [
    'Схема оплаты {name}: {what}.',
    '{name} үчүн төлөм схемасы: {what}.',
    "{name} uchun to'lov sxemasi: {what}.",
    'Pay scheme of {name}: {what}.',
    '{name} ödeme şeması: {what}.',
  ],
  // 2026-10-26 (В7): выплата ушла из кассы продаж филиала (правило ПК: предупредить, касса расходов не найдена).
  'salesBoxWarn': [
    ' Это касса продаж филиала — её остаток уменьшился.',
    ' Бул филиалдын сатуу кассасы — анын калдыгы азайды.',
    " Bu filialning sotuv kassasi — uning qoldig'i kamaydi.",
    ' This is the branch sales cash register — its balance went down.',
    ' Bu şubenin satış kasası — bakiyesi azaldı.',
  ],
  // 2026-10-26 (В7): отказы выплаты до отправки (код в PayoutOutcome.error начинается с «#»); ошибку сервера говорим как есть.
  'err.lock': [
    'Прошлая выплата этому сотруднику осталась без ответа сервера — новую не отправляю, пока владелец не проверит её на экране «Зарплата».',
    'Бул кызматкерге мурунку төлөм сервердин жообусуз калды — ээси «Эмгек акы» экранында текшергенче жаңысын жөнөтпөйм.',
    "Bu xodimga oldingi to'lov serverdan javobsiz qoldi — egasi «Maosh» ekranida tekshirmaguncha yangisini yubormayman.",
    'The previous payout to this employee got no server reply — I will not send a new one until the owner checks it on the Salary screen.',
    'Bu çalışana önceki ödeme sunucudan yanıt almadı — sahip Maaş ekranında kontrol edene kadar yenisini göndermem.',
  ],
  'err.dup': [
    'Такая же выплата только что уже прошла — повтор не провожу.',
    'Дал ушундай төлөм азыр эле өттү — кайталабайм.',
    "Xuddi shunday to'lov hozirgina o'tdi — takrorlamayman.",
    'The same payout just went through — I am not repeating it.',
    'Aynı ödeme az önce yapıldı — tekrarlamıyorum.',
  ],
  'err.journal': [
    'Журнал выплат на телефоне не читается — без него защиты от двойной выплаты нет, не плачу.',
    'Телефондогу төлөмдөр журналы окулбайт — аныксыз кош төлөмдөн коргоо жок, төлөбөйм.',
    "Telefondagi to'lovlar jurnali o'qilmaydi — usiz ikki marta to'lashdan himoya yo'q, to'lamayman.",
    'The payout log on the phone cannot be read — without it there is no protection from a double payout, I do not pay.',
    'Telefondaki ödeme günlüğü okunamıyor — onsuz çift ödeme koruması yok, ödemiyorum.',
  ],
  'err.nocash': [
    'Не нашла кассу компании для выплаты — выплатите на сайте или в программе владельца.',
    'Төлөм үчүн компаниянын кассасын таппадым — сайтта же ээсинин программасында төлөңүз.',
    "To'lov uchun kompaniya kassasini topmadim — saytda yoki egasi dasturida to'lang.",
    'I could not find a company cash register for the payout — pay on the site or in the owner program.',
    'Ödeme için şirket kasası bulunamadı — sitede veya sahip programında ödeyin.',
  ],
  // 2026-10-26 (В7): расход создан не владельцем — сайт держит его «в ожидании» до подтверждения.
  'pendingNote': [
    ' Расход ждёт подтверждения владельца на сайте.',
    ' Чыгым сайтта ээсинин ырастоосун күтүп турат.',
    " Xarajat saytda egasining tasdig'ini kutmoqda.",
    ' The expense is waiting for the owner to confirm it on the site.',
    ' Gider, sahibin sitede onaylamasını bekliyor.',
  ],
  'scheme.salary': ['оклад', 'айлык', 'maosh', 'salary', 'sabit maaş'],
  'scheme.percent': ['процент от продаж', 'сатуудан пайыз', 'sotuvdan foiz', 'sales percentage', 'satış yüzdesi'],
  'scheme.salary_plus_percent': ['оклад + процент', 'айлык + пайыз', 'maosh + foiz', 'salary + percentage', 'maaş + yüzde'],
  'scheme.per_item': ['за каждый товар', 'ар бир товар үчүн', 'har bir mahsulot uchun', 'per item', 'ürün başına'],
  'scheme.salary_plus_per_item': ['оклад + за товар', 'айлык + товар үчүн', 'maosh + mahsulot uchun', 'salary + per item', 'maaş + ürün başına'],
  'scheme.percent_plus_per_item': ['процент + за товар', 'пайыз + товар үчүн', 'foiz + mahsulot uchun', 'percentage + per item', 'yüzde + ürün başına'],
  'needScheme': [
    'Какая схема: оклад, процент или за штуку? Назовите схему и числа.',
    'Кандай схема: айлык, пайыз же даанасына? Схеманы жана сандарды айтыңыз.',
    "Qanday sxema: maosh, foiz yoki donasiga? Sxema va sonlarni ayting.",
    'Which scheme: salary, percent or per item? Say the scheme and the numbers.',
    'Hangi şema: maaş, yüzde veya adet başına? Şemayı ve sayıları söyleyin.',
  ],
};

String _x(String key, String lang, [Map<String, String> p = const {}]) {
  final l = _t[key];
  if (l == null) return am(key, lang, p);
  var s = switch (lang) {
    'ky' => l[1],
    'uz' => l[2],
    'en' => l[3],
    'tr' => l[4],
    _ => l[0],
  };
  p.forEach((k, v) => s = s.replaceAll('{$k}', v));
  return s;
}

Map<String, List<String>> get staffTextsForTest => _t;

String _schemeLabel(String code, String lang) => _t.containsKey('scheme.$code') ? _x('scheme.$code', lang) : (code.isEmpty ? '—' : code);

String _sp(String s) => s.replaceAll(' ', ' ').replaceAll(' ', ' ');
String _m(num v, String lang) => _sp(CallTable.formatCell(v, CellKind.money, lang));
String _s(Object? v) => v == null ? '' : '$v'.trim();
double? _dbl(Object? v) => v is num ? v.toDouble() : (v is String ? double.tryParse(v.trim().replaceAll(',', '.').replaceAll(RegExp(r'\s'), '')) : null);
int? _int(Object? v) => _dbl(v)?.toInt();
String _clip(String s, [int n = 40]) => s.length > n ? '${s.substring(0, n - 1)}…' : s;
String _day(DateTime d, String lang) => CallTable.formatCell(d, CellKind.date, lang);

ToolResult _unavailable(ToolEnv env, String feature) => ToolResult(
  ToolStatus.error,
  _x('unav.$feature', env.lang),
  data: {
    'unavailable': true,
    'feature': feature,
    // для разработчика: чего не хватает (Б-62 задачи, Б-63 штрафы и сменная ставка, Б-24 выплаты, В7 экраны и репозитории)
    'needs': switch (feature) {
      'tasks' => 'В7 tasks_repository (api/main/tasks/), Б-62',
      'timesheet' => 'В7 timesheet (api/construction/shifts/)',
      'salary' => 'В7 salary_repository (analytics/market/?tab=salary), Б-24',
      'payout' => 'В7 salary_payout (cashflows «Зарплата»), Б-24',
      'scheme' => 'В7 salary_repository (market-sale-employee-pay-profiles)',
      _ => '',
    },
  },
);

// ---------------------------------------------------------------------------------------------------------------------
// Подключение
// ---------------------------------------------------------------------------------------------------------------------

/// Подключить инструменты сотрудников. [kit] — общий стек отмены и журнал (тот же, что у action_tools). Инструмент, чьих
/// возможностей нет у [src], объявляется коротко и отвечает «недоступно».
void registerStaffTools(ToolRegistry registry, ActionKit kit, StaffSource src, {TableStore? tables}) {
  for (final t in buildStaffTools(kit, src, tables: tables ?? kit.tables)) {
    registry.register(t);
  }
}

List<AssistantTool> buildStaffTools(ActionKit kit, StaffSource src, {TableStore? tables}) {
  final b = _StaffTools(kit, src, tables);
  return [
    b.staffList(),
    ?b.tasksList(),
    ?b.tasksReport(),
    ?b.taskCreate(),
    ?b.taskUpdate(),
    ?b.timesheet(),
    ?b.salaryReport(),
    ?b.salaryDue(),
    ?b.salaryPayout(),
    ?b.salaryScheme(),
    b.unavailable(),
  ];
}

class _StaffTools {
  final ActionKit k;
  final StaffSource src;
  final TableStore? tables;
  _StaffTools(this.k, this.src, this.tables);

  bool has(StaffCap c) => src.caps.contains(c);

  Future<ToolResult> guarded(ToolEnv env, Future<ToolResult> Function() f, {bool money = false}) async {
    try {
      return await f();
    } on UnsupportedError {
      return _unavailable(env, 'tasks');
    } on ApiException catch (e) {
      if (e.status == 403) return ToolResult(ToolStatus.denied, toolMsg('denied', env.lang));
      if (e.isTransient) return ToolResult(ToolStatus.offline, am(money ? 'unknownRetry' : 'offline', env.lang));
      return ToolResult(ToolStatus.error, e.message.isEmpty ? toolMsg('error', env.lang) : e.message);
    }
  }

  /// Сотрудник по имени: один — он; несколько — варианты с номерами (имена вслух не уходят, только номера и роли).
  Future<({StaffPerson? p, ToolResult? stop})> person(ToolEnv env, String name, Object? pick) async {
    if (name.isEmpty) return (p: null, stop: ToolResult(ToolStatus.clarify, _x('needStaff', env.lang)));
    final all = await src.staff();
    final hits = all.where((e) => looseNameMatch(e.name, name) || productNameMatchesQuery(e.name, name)).toList();
    if (hits.isEmpty) return (p: null, stop: ToolResult(ToolStatus.notFound, _x('staffNone', env.lang)));
    final n = _int(pick);
    if (n != null && n >= 1 && n <= hits.length) return (p: hits[n - 1], stop: null);
    if (hits.length == 1) return (p: hits.first, stop: null);
    final shown = hits.take(5).toList();
    return (
      p: null,
      stop: ToolResult(
        ToolStatus.ambiguous,
        _x('staffMany', env.lang, {'list': [for (var i = 0; i < shown.length; i++) '${i + 1} — ${shown[i].role}'].join('; ')}),
        options: [
          for (var i = 0; i < shown.length; i++) {'n': i + 1, 'role': shown[i].role},
        ],
      ),
    );
  }

  // ---- список сотрудников
  AssistantTool staffList() => AssistantTool(
    decl: ToolDecl(
      'staff_list',
      'Employees as a table (names stay on screen; never say codes like [С1] aloud). ru: кто работает, сотрудники.',
    ),
    group: ToolGroup.read,
    allowed: (s) => canManageTasks(s) || canViewSalary(s),
    needs: 'Сотрудники',
    run: (r) => guarded(r.env, () async {
      final env = r.env;
      final lang = env.lang;
      final all = await src.staff();
      final names = <String, String>{};
      final rows = <List<Object?>>[];
      for (var i = 0; i < all.length; i++) {
        final code = '[С${i + 1}]';
        names[code] = all[i].name;
        rows.add([code, all[i].role]);
      }
      final t = CallTable.build(
        id: tables?.nextId() ?? 't0',
        title: tr5(lang, 'Сотрудники', 'Кызматкерлер', 'Xodimlar', 'Employees', 'Çalışanlar'),
        columns: [
          CallColumn('who', tr5(lang, 'Сотрудник', 'Кызматкер', 'Xodim', 'Employee', 'Çalışan'), CellKind.code),
          CallColumn('role', tr5(lang, 'Роль', 'Ролу', 'Roli', 'Role', 'Rol'), CellKind.text),
        ],
        rows: rows,
        refs: [for (final e in all) e.id],
        names: names,
        source: 'api/users/employees/',
        at: env.now(),
        lang: lang,
      );
      tables?.add(t);
      return ToolResult(ToolStatus.ok, _x('staffSay', lang, {'n': '${all.length}'}), tableId: t.id, data: {'count': all.length});
    }),
  );

  // ---- задачи
  Future<Map<String, StaffPerson>> peopleById() async => {for (final p in await src.staff()) p.id: p};

  AssistantTool? tasksList() {
    if (!has(StaffCap.tasks)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'tasks_list',
        'Tasks as a table: a manager sees all (filter assignee name, status, overdue), others only their own. ru: задачи Айбека, мои задачи, просроченные.',
        parameters: sObj({
          'assignee': sStr(),
          'status': sStr(values: ['pending', 'in_progress', 'done']),
          'overdue': sBool(),
          'pick': sInt(),
        }),
      ),
      group: ToolGroup.read,
      run: (r) => guarded(r.env, () async {
        final env = r.env;
        final lang = env.lang;
        final manage = canManageTasks(env.session);
        final people = await peopleById();
        var list = await src.tasks();
        if (!manage) {
          list = list.where((t) => t.assigneeId == env.session.userId).toList();
        } else if (_s(r.args['assignee']).isNotEmpty) {
          final pp = await person(env, _s(r.args['assignee']), r.args['pick']);
          if (pp.stop != null) return pp.stop!;
          list = list.where((t) => t.assigneeId == pp.p!.id).toList();
        }
        final st = _s(r.args['status']);
        if (st.isNotEmpty) list = list.where((t) => t.status.wire == st).toList();
        final now = env.now();
        if (r.args['overdue'] == true) list = list.where((t) => t.overdue(now)).toList();
        list.sort((a, b) {
          final c = (a.due ?? DateTime(2100)).compareTo(b.due ?? DateTime(2100));
          return c != 0 ? c : a.title.compareTo(b.title);
        });
        final names = <String, String>{};
        final codeOf = <String, String>{};
        String code(String id) => codeOf.putIfAbsent(id, () {
          final c = '[С${codeOf.length + 1}]';
          names[c] = people[id]?.name ?? c;
          return c;
        });
        final t = CallTable.build(
          id: tables?.nextId() ?? 't0',
          title: tr5(lang, 'Задачи', 'Тапшырмалар', 'Vazifalar', 'Tasks', 'Görevler'),
          columns: [
            CallColumn('n', '№', CellKind.text, numeric: false),
            CallColumn('title', tr5(lang, 'Задача', 'Тапшырма', 'Vazifa', 'Task', 'Görev'), CellKind.text),
            CallColumn('who', tr5(lang, 'Кому', 'Кимге', 'Kimga', 'Assignee', 'Kime'), CellKind.code),
            CallColumn('due', tr5(lang, 'Срок', 'Мөөнөт', 'Muddat', 'Due', 'Son tarih'), CellKind.date),
            CallColumn('st', tr5(lang, 'Статус', 'Статус', 'Holati', 'Status', 'Durum'), CellKind.text, numeric: false),
          ],
          rows: [
            for (var i = 0; i < list.length; i++)
              <Object?>[
                '${i + 1}',
                (list[i].priority == 'high' ? '❗ ' : '') + list[i].title,
                code(list[i].assigneeId),
                list[i].due,
                list[i].overdue(now) ? '⚠ ${_x('ts.${list[i].status.wire}', lang)}' : _x('ts.${list[i].status.wire}', lang),
              ],
          ],
          refs: [for (final x in list) x.id],
          names: names,
          source: 'api/main/tasks/',
          at: now,
          lang: lang,
        );
        tables?.add(t);
        if (list.isEmpty) return ToolResult(ToolStatus.ok, _x('tasksNone', lang), tableId: t.id);
        final late = list.where((x) => x.overdue(now)).length;
        return ToolResult(ToolStatus.ok, _x('tasksSay', lang, {'n': '${list.length}', 'late': '$late'}), tableId: t.id, data: {'count': list.length, 'overdue': late});
      }),
    );
  }

  AssistantTool? tasksReport() {
    if (!has(StaffCap.tasks)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'tasks_report',
        'Who did not do what: per employee total, done, overdue, % on time. ru: кто что не сделал.',
        parameters: sObj({'from': sStr(), 'to': sStr()}),
      ),
      group: ToolGroup.read,
      allowed: canManageTasks,
      needs: 'Сотрудники',
      run: (r) => guarded(r.env, () async {
        final env = r.env;
        final lang = env.lang;
        final now = env.now();
        final p = parsePeriod(r.args, now, fallback: 'week');
        final people = await peopleById();
        final inPeriod = (await src.tasks()).where((t) {
          final d = t.due;
          return d == null || (!DateTime(d.year, d.month, d.day).isBefore(p.from) && !DateTime(d.year, d.month, d.day).isAfter(p.to));
        }).toList();
        final by = <String, List<StaffTask>>{};
        for (final t in inPeriod) {
          (by[t.assigneeId] ??= []).add(t);
        }
        final names = <String, String>{};
        final rows = <List<Object?>>[];
        var late = 0;
        var worst = 0;
        final ids = by.keys.toList()..sort((a, b) => by[b]!.where((t) => t.overdue(now)).length.compareTo(by[a]!.where((t) => t.overdue(now)).length));
        for (var i = 0; i < ids.length; i++) {
          final ts = by[ids[i]]!;
          final done = ts.where((t) => t.status == TaskState.done).length;
          final prog = ts.where((t) => t.status == TaskState.inProgress).length;
          final over = ts.where((t) => t.overdue(now)).length;
          final due = ts.where((t) => t.due != null).length;
          final onTime = due == 0 ? 100.0 : (due - over) / due * 100;
          late += over;
          if (over > worst) worst = over;
          final c = '[С${i + 1}]';
          names[c] = people[ids[i]]?.name ?? c;
          rows.add([c, ts.length, done, prog, over, onTime]);
        }
        final t = CallTable.build(
          id: tables?.nextId() ?? 't0',
          title: tr5(lang, 'Задачи: кто что не сделал', 'Тапшырмалар: ким эмнени аткарган жок', 'Vazifalar: kim nimani bajarmadi', 'Tasks: who did not do what', 'Görevler: kim ne yapmadı'),
          subtitle: p.label(lang),
          columns: [
            CallColumn('who', tr5(lang, 'Сотрудник', 'Кызматкер', 'Xodim', 'Employee', 'Çalışan'), CellKind.code),
            CallColumn('all', tr5(lang, 'Всего', 'Бардыгы', 'Jami', 'Total', 'Toplam'), CellKind.qty, sum: true),
            CallColumn('done', tr5(lang, 'Сделано', 'Аткарылды', 'Bajarildi', 'Done', 'Tamam'), CellKind.qty, sum: true),
            CallColumn('prog', tr5(lang, 'В работе', 'Иште', 'Jarayonda', 'In progress', 'Devam'), CellKind.qty, sum: true),
            CallColumn('late', tr5(lang, 'Просрочено', 'Мөөнөтү өткөн', "Muddati o'tgan", 'Overdue', 'Geciken'), CellKind.qty, sum: true),
            CallColumn('ontime', tr5(lang, 'В срок', 'Мөөнөттө', 'Muddatda', 'On time', 'Zamanında'), CellKind.percent, signed: false),
          ],
          rows: rows,
          names: names,
          sortKey: 'late',
          desc: true,
          source: 'api/main/tasks/',
          at: now,
          lang: lang,
        );
        tables?.add(t);
        if (late == 0) return ToolResult(ToolStatus.ok, _x('reportNone', lang), tableId: t.id);
        return ToolResult(ToolStatus.ok, _x('reportSay', lang, {'late': '$late', 'worst': '$worst'}), tableId: t.id, data: {'overdue': late});
      }),
    );
  }

  AssistantTool? taskCreate() {
    if (!has(StaffCap.tasks)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'task_create',
        'Give a task to an employee (assignee = name as said, due = YYYY-MM-DD; priority high only if the user said urgent). ru: поставь Нурлану задачу пересчитать полку до завтра; ky: тапшырма бер.',
        parameters: sObj({
          'title': sStr(),
          'assignee': sStr(),
          'due': sStr(),
          'priority': sStr(values: ['low', 'normal', 'high']),
          'remind_at': sStr(desc: 'YYYY-MM-DDTHH:MM'),
          'description': sStr(),
          'pick': sInt(),
        }, required: ['title', 'assignee']),
      ),
      group: ToolGroup.action,
      allowed: canManageTasks,
      needs: 'Сотрудники',
      gateFor: (a) => [
        const GateReq(words: Intents.taskCreate, objects: ['assignee']),
        if (_s(a['priority']).isNotEmpty && _s(a['priority']) != 'normal') const GateReq(words: Intents.taskPriority),
      ],
      intentWords: Intents.taskCreate,
      run: (r) => guarded(r.env, () async {
        final env = r.env;
        final lang = env.lang;
        final title = _s(r.args['title']);
        if (title.isEmpty) return ToolResult(ToolStatus.clarify, _x('needTitle', lang));
        final pp = await person(env, _s(r.args['assignee']), r.args['pick']);
        if (pp.stop != null) return pp.stop!;
        final who = pp.p!;
        final due = DateTime.tryParse(_s(r.args['due']));
        final prio = const {'low', 'normal', 'high'}.contains(_s(r.args['priority'])) ? _s(r.args['priority']) : 'normal';
        final draft = StaffTask(
          title: title,
          description: _s(r.args['description']),
          due: due,
          assigneeId: who.id,
          priority: prio,
          remindAt: DateTime.tryParse(_s(r.args['remind_at'])),
        );
        final made = await src.createTask(draft);
        final say = _x('taskCreated', lang, {
          'title': _clip(title, 40),
          'name': _clip(who.name, 30),
          'due': due == null ? '' : _x('dueWord', lang, {'date': _day(due, lang)}),
          'prio': prio == 'high' ? _x('prioHigh', lang) : '',
        });
        await k.commit(
          r,
          tool: 'task_create',
          say: say,
          label: (l) => '${tr5(l, 'задача', 'тапшырма', 'vazifa', 'task', 'görev')} «${_clip(title, 24)}»',
          data: {'task': made.id, 'assignee_code': 'С'},
          // Отмена созданной задачи — DELETE.
          undoFn: (l) async {
            await src.deleteTask(made.id);
            return UndoResult.ok;
          },
        );
        return ToolResult(ToolStatus.ok, say, undo: true, data: {'task': made.id});
      }),
    );
  }

  AssistantTool? taskUpdate() {
    if (!has(StaffCap.tasks)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'task_update',
        'Change a task: status (in_progress | done), due, assignee, priority, note. Managers any task, an employee only their own. ru: задача готова, перенеси на пятницу.',
        parameters: sObj({
          'task': sStr(),
          'pick': sInt(),
          'status': sStr(values: ['in_progress', 'done']),
          'due': sStr(),
          'assignee': sStr(),
          'priority': sStr(values: ['low', 'normal', 'high']),
          'note': sStr(),
        }, required: ['task']),
      ),
      group: ToolGroup.action,
      gateFor: (a) => [
        if (_s(a['status']) == 'done') const GateReq(words: Intents.taskDone),
        if (_s(a['status']) == 'in_progress') const GateReq(words: Intents.taskProgress),
        if (_s(a['due']).isNotEmpty) const GateReq(words: Intents.taskDue),
        if (_s(a['assignee']).isNotEmpty) const GateReq(words: Intents.taskAssignee, objects: ['assignee']),
        if (_s(a['priority']).isNotEmpty) const GateReq(words: Intents.taskPriority),
        if (_s(a['status']).isEmpty && _s(a['due']).isEmpty && _s(a['assignee']).isEmpty && _s(a['priority']).isEmpty) const GateReq(words: Intents.taskDone),
      ],
      intentWords: Intents.taskDone,
      run: (r) => guarded(r.env, () async {
        final env = r.env;
        final lang = env.lang;
        final manage = canManageTasks(env.session);
        final all = await src.tasks();
        final q = _s(r.args['task']);
        final mine = manage ? all : all.where((t) => t.assigneeId == env.session.userId).toList();
        // «№ из таблицы» или слова названия
        final byNum = _int(q);
        StaffTask? task;
        List<StaffTask> hits;
        if (byNum != null && _s(r.args['pick']).isEmpty) {
          hits = [];
          final shown = tables?.current;
          final ref = shown != null && shown.source == 'api/main/tasks/' && byNum >= 1 && byNum <= shown.refs.length ? shown.refs[byNum - 1] : null;
          task = ref == null ? null : all.where((t) => t.id == ref).firstOrNull;
        } else {
          hits = mine.where((t) => t.status != TaskState.done && (looseNameMatch(t.title, q) || productNameMatchesQuery(t.title, q))).toList();
          if (hits.isEmpty) hits = mine.where((t) => looseNameMatch(t.title, q) || productNameMatchesQuery(t.title, q)).toList();
        }
        if (task == null) {
          if (hits.isEmpty) return ToolResult(ToolStatus.notFound, _x('taskNone', lang));
          final n = _int(r.args['pick']);
          if (n != null && n >= 1 && n <= hits.length) {
            task = hits[n - 1];
          } else if (hits.length == 1) {
            task = hits.first;
          } else {
            final shown = hits.take(5).toList();
            return ToolResult(
              ToolStatus.ambiguous,
              _x('taskMany', lang, {'list': [for (var i = 0; i < shown.length; i++) '${i + 1} — ${_clip(shown[i].title, 24)}'].join('; ')}),
              options: [
                for (var i = 0; i < shown.length; i++) {'n': i + 1, 'title': _clip(shown[i].title, 40)},
              ],
            );
          }
        }
        final old = task;
        if (!manage && old.assigneeId != env.session.userId) return ToolResult(ToolStatus.denied, _x('taskForeign', lang));
        // Исполнитель меняет только статус и заметку своей задачи.
        if (!manage && (_s(r.args['due']).isNotEmpty || _s(r.args['assignee']).isNotEmpty || _s(r.args['priority']).isNotEmpty)) {
          return ToolResult(ToolStatus.denied, _x('taskForeign', lang));
        }
        var next = old;
        final what = <String>[];
        final st = _s(r.args['status']);
        if (st.isNotEmpty) {
          next = next.copyWith(status: TaskState.parse(st));
          what.add(_x('ts.$st', lang));
        }
        final due = DateTime.tryParse(_s(r.args['due']));
        if (due != null) {
          next = next.copyWith(due: due);
          what.add(_x('what.due', lang, {'date': _day(due, lang)}));
        }
        if (_s(r.args['assignee']).isNotEmpty) {
          final pp = await person(env, _s(r.args['assignee']), null);
          if (pp.stop != null) return pp.stop!;
          next = next.copyWith(assigneeId: pp.p!.id);
          what.add(_x('what.assignee', lang, {'name': _clip(pp.p!.name, 30)}));
        }
        final prio = _s(r.args['priority']);
        if (const {'low', 'normal', 'high'}.contains(prio)) {
          next = next.copyWith(priority: prio);
          if (prio == 'high') what.add(_x('what.priority', lang));
        }
        final note = _s(r.args['note']);
        if (note.isNotEmpty) next = next.copyWith(report: note);
        if (what.isEmpty && note.isEmpty) return ToolResult(ToolStatus.clarify, am('nothingToChange', lang));
        final saved = await src.updateTask(next);
        final say = _x('taskUpdated', lang, {'title': _clip(old.title, 40), 'what': what.isEmpty ? note : what.join(', ')});
        await k.commit(
          r,
          tool: 'task_update',
          say: say,
          label: (l) => '${tr5(l, 'задача', 'тапшырма', 'vazifa', 'task', 'görev')} «${_clip(old.title, 24)}»',
          data: {'task': old.id, 'from': old.status.wire, 'to': saved.status.wire},
          // Отмена изменения — PATCH прежних полей.
          undoFn: (l) async {
            await src.updateTask(old);
            return UndoResult.ok;
          },
        );
        return ToolResult(ToolStatus.ok, say, undo: true);
      }),
    );
  }

  // ---- табель и зарплата
  AssistantTool? timesheet() {
    bool allowed(Session s) => s.can(Session.viewShifts) || canViewSalary(s);
    if (!has(StaffCap.timesheet)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'timesheet',
        'Timesheet from closed shifts: shifts, days, hours, revenue per employee. ru: табель за октябрь.',
        parameters: sObj({'from': sStr(), 'to': sStr(), 'employee': sStr(), 'pick': sInt()}),
      ),
      group: ToolGroup.read,
      allowed: allowed,
      needs: 'Смены',
      run: (r) => guarded(r.env, () async {
        final env = r.env;
        final lang = env.lang;
        final p = parsePeriod(r.args, env.now(), fallback: 'month');
        var rows = await src.timesheet(p.from, p.to);
        if (_s(r.args['employee']).isNotEmpty) {
          final pp = await person(env, _s(r.args['employee']), r.args['pick']);
          if (pp.stop != null) return pp.stop!;
          rows = rows.where((e) => e.employeeId == pp.p!.id).toList();
        }
        final names = <String, String>{};
        final t = CallTable.build(
          id: tables?.nextId() ?? 't0',
          title: tr5(lang, 'Табель', 'Табель', 'Tabel', 'Timesheet', 'Puantaj'),
          subtitle: p.label(lang),
          columns: [
            CallColumn('who', tr5(lang, 'Сотрудник', 'Кызматкер', 'Xodim', 'Employee', 'Çalışan'), CellKind.code),
            CallColumn('shifts', tr5(lang, 'Смен', 'Смена', 'Smena', 'Shifts', 'Vardiya'), CellKind.qty, sum: true),
            CallColumn('days', tr5(lang, 'Дней', 'Күн', 'Kun', 'Days', 'Gün'), CellKind.qty, sum: true),
            CallColumn('hours', tr5(lang, 'Часов', 'Саат', 'Soat', 'Hours', 'Saat'), CellKind.qty, sum: true),
            CallColumn('rev', tr5(lang, 'Выручка', 'Түшүм', 'Tushum', 'Revenue', 'Ciro'), CellKind.money, sum: true),
          ],
          rows: [
            for (var i = 0; i < rows.length; i++)
              () {
                final c = '[С${i + 1}]';
                names[c] = rows[i].name;
                return <Object?>[c, rows[i].shifts, rows[i].days, rows[i].hours, rows[i].revenue];
              }(),
          ],
          names: names,
          source: 'api/construction/shifts/',
          at: env.now(),
          lang: lang,
        );
        tables?.add(t);
        if (rows.isEmpty) return ToolResult(ToolStatus.ok, _x('tsNone', lang, {'period': p.label(lang)}), tableId: t.id);
        final hours = rows.fold(0.0, (a, e) => a + e.hours);
        return ToolResult(
          ToolStatus.ok,
          _x('tsSay', lang, {'period': p.label(lang), 'n': '${rows.length}', 'hours': hours.toStringAsFixed(hours == hours.roundToDouble() ? 0 : 1)}),
          tableId: t.id,
        );
      }),
    );
  }

  AssistantTool? salaryReport() {
    if (!has(StaffCap.salary)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'salary_report',
        'Salary for a period per employee (scheme, accrued, paid, left) as a table. ru: зарплата за октябрь.',
        parameters: sObj({'from': sStr(), 'to': sStr(), 'employee': sStr(), 'pick': sInt()}),
      ),
      group: ToolGroup.read,
      allowed: canViewSalary,
      needs: 'Зарплата',
      run: (r) => guarded(r.env, () async {
        final env = r.env;
        final lang = env.lang;
        final p = parsePeriod(r.args, env.now(), fallback: 'month');
        var rows = await src.salary(p.from, p.to);
        if (_s(r.args['employee']).isNotEmpty) {
          final pp = await person(env, _s(r.args['employee']), r.args['pick']);
          if (pp.stop != null) return pp.stop!;
          rows = rows.where((e) => e.employeeId == pp.p!.id).toList();
        }
        final names = <String, String>{};
        final partial = rows.any((e) => e.partial || e.paid == null);
        final t = CallTable.build(
          id: tables?.nextId() ?? 't0',
          title: tr5(lang, 'Зарплата', 'Айлык', 'Maosh', 'Salary', 'Maaş'),
          subtitle: p.label(lang),
          columns: [
            CallColumn('who', tr5(lang, 'Сотрудник', 'Кызматкер', 'Xodim', 'Employee', 'Çalışan'), CellKind.code),
            CallColumn('scheme', tr5(lang, 'Схема', 'Схема', 'Sxema', 'Scheme', 'Şema'), CellKind.text, numeric: false),
            CallColumn('acc', tr5(lang, 'Начислено', 'Эсептелди', 'Hisoblandi', 'Accrued', 'Tahakkuk'), CellKind.money, sum: true),
            CallColumn('paid', tr5(lang, 'Выплачено', 'Төлөндү', "To'landi", 'Paid', 'Ödenen'), CellKind.money, sum: true),
            CallColumn('left', tr5(lang, 'Осталось', 'Калды', 'Qoldi', 'Left', 'Kalan'), CellKind.money, sum: true),
          ],
          rows: [
            for (var i = 0; i < rows.length; i++)
              () {
                final c = '[С${i + 1}]';
                names[c] = rows[i].name;
                return <Object?>[c, rows[i].noScheme ? '—' : _schemeLabel(rows[i].scheme, lang), rows[i].accrued, rows[i].paid, rows[i].left];
              }(),
          ],
          names: names,
          note: partial ? _x('partial', lang).trim() : null,
          source: 'analytics/market/?tab=salary',
          at: env.now(),
          lang: lang,
        );
        tables?.add(t);
        if (rows.isEmpty) return ToolResult(ToolStatus.ok, _x('salaryNone', lang, {'period': p.label(lang)}), tableId: t.id);
        final acc = rows.fold(0.0, (a, e) => a + e.accrued);
        final paid = rows.fold(0.0, (a, e) => a + (e.paid ?? 0));
        return ToolResult(
          ToolStatus.ok,
          _x('salarySay', lang, {'period': p.label(lang), 'accrued': _m(acc, lang), 'paid': _m(paid, lang), 'left': _m(acc - paid, lang), 'partial': partial ? _x('partial', lang) : ''}),
          tableId: t.id,
          data: {'partial': partial},
        );
      }),
    );
  }

  AssistantTool? salaryDue() {
    bool allowed(Session s) => canViewSalary(s);
    if (!has(StaffCap.salary)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'salary_due',
        'How much is owed to ONE employee for a period: accrued minus paid. ru: сколько я должен Айбеку за октябрь.',
        parameters: sObj({'employee': sStr(), 'from': sStr(), 'to': sStr(), 'pick': sInt()}, required: ['employee']),
      ),
      group: ToolGroup.read,
      allowed: allowed,
      needs: 'Зарплата',
      run: (r) => guarded(r.env, () async {
        final env = r.env;
        final lang = env.lang;
        final p = parsePeriod(r.args, env.now(), fallback: 'month');
        final pp = await person(env, _s(r.args['employee']), r.args['pick']);
        if (pp.stop != null) return pp.stop!;
        final row = (await src.salary(p.from, p.to)).where((e) => e.employeeId == pp.p!.id).firstOrNull;
        if (row == null) return ToolResult(ToolStatus.ok, _x('salaryNone', lang, {'period': p.label(lang)}));
        if (row.paid == null) {
          return ToolResult(ToolStatus.ok, _x('dueUnknown', lang, {'name': _clip(pp.p!.name, 30), 'accrued': _m(row.accrued, lang)}), data: {'partial': true});
        }
        final partial = row.partial;
        return ToolResult(
          ToolStatus.ok,
          _x('dueSay', lang, {
            'name': _clip(pp.p!.name, 30),
            'accrued': _m(row.accrued, lang),
            'paid': _m(row.paid!, lang),
            'left': _m(row.left!, lang),
            'partial': partial ? _x('partial', lang) : '',
          }),
          data: {'accrued': row.accrued, 'paid': row.paid, 'left': row.left, 'partial': partial},
        );
      }),
    );
  }

  AssistantTool? salaryPayout() {
    if (!has(StaffCap.payout) || !has(StaffCap.salary)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'salary_payout',
        'Pay salary or an advance to ONE employee from the cash register (owner-program rules). amount omitted = the whole remainder, only if known exactly. ru: выплати Айбеку 10000 аванс наличными; ky: төлөп бер.',
        parameters: sObj({
          'employee': sStr(),
          'amount': sNum(),
          'kind': sStr(values: ['salary', 'advance']),
          'from': sStr(),
          'to': sStr(),
          'method': sStr(values: ['cash', 'transfer']),
          'pick': sInt(),
        }, required: ['employee']),
      ),
      group: ToolGroup.money,
      allowed: canEditSalary,
      needs: 'Выплата зарплаты',
      intentWords: Intents.salary,
      numericArgs: const ['amount'],
      objectArgs: const ['employee'],
      run: (r) => guarded(r.env, () => k.serial(() async {
        final env = r.env;
        final lang = env.lang;
        final p = parsePeriod(r.args, env.now(), fallback: 'month');
        final pp = await person(env, _s(r.args['employee']), r.args['pick']);
        if (pp.stop != null) return pp.stop!;
        final who = pp.p!;
        final row = (await src.salary(p.from, p.to)).where((e) => e.employeeId == who.id).firstOrNull;
        if (row == null) return ToolResult(ToolStatus.notFound, _x('salaryNone', lang, {'period': p.label(lang)}));
        var amount = _dbl(r.args['amount']);
        final kind = _s(r.args['kind']) == 'advance' ? 'advance' : 'salary';
        if (amount == null) {
          // Без суммы — только «уверенный» остаток; не уверен — сумма сама не подставляется (правило ПК).
          if (row.paid == null || row.partial) return ToolResult(ToolStatus.clarify, _x('payoutAmountNeeded', lang));
          amount = row.left;
          if (amount == null || amount <= 0.004) return ToolResult(ToolStatus.ok, _x('payoutNothing', lang));
        }
        if (amount <= 0) return ToolResult(ToolStatus.clarify, am('needAmount', lang));
        final method = _s(r.args['method']) == 'transfer' ? 'transfer' : 'cash';
        final o = await src.payout(row: row, amount: amount, kind: kind, from: p.from, to: p.to, method: method);
        if (o.unknown) {
          // Ответ не дошёл: НЕ повторять новым запросом, запись «неизвестно» держит замок в самой реализации.
          return ToolResult(ToolStatus.error, am('unknownOutcome', lang), data: {'unknown_outcome': true});
        }
        if (!o.ok) {
          final e = o.error;
          return ToolResult(ToolStatus.error, e == null ? toolMsg('error', lang) : (e.startsWith('#') ? _x('err.${e.substring(1)}', lang) : e));
        }
        final left = o.left ?? (row.left == null ? null : row.left! - amount);
        final say = _x('payoutDone', lang, {
          'name': _clip(who.name, 30),
          'amount': _m(amount, lang),
          'kind': _x('kind.$kind', lang),
          'method': _x('meth.$method', lang),
          'cashbox': o.cashbox.isEmpty ? '' : _x('cashboxWord', lang, {'name': o.cashbox}),
          'left': left == null ? '—' : _m(left < 0 ? 0 : left, lang),
        }) + (o.note.contains('sales_cashbox') ? _x('salesBoxWarn', lang) : '') + (o.note.contains('pending') ? _x('pendingNote', lang) : '');
        final paidAmount = amount;
        await k.commit(
          r,
          tool: 'salary_payout',
          say: say,
          label: (l) => '${_x('kind.$kind', l)} ${_m(paidAmount, l)}',
          data: {'employee_code': 'С', 'amount': paidAmount, 'kind': kind, 'method': method},
          // Выплату отменяет только владелец или бухгалтер запросом на сайте (cancel-request): ИИ так и говорит.
          whyNot: (l) => am('why.salary', l),
          amount: -paidAmount,
        );
        return ToolResult(ToolStatus.ok, say, undo: false, data: {'amount': paidAmount, 'left': left});
      }, ), money: true),
    );
  }

  AssistantTool? salaryScheme() {
    if (!has(StaffCap.scheme)) return null;
    return AssistantTool(
      decl: ToolDecl(
        'salary_scheme',
        'Set the pay scheme of ONE employee: salary | percent | salary_plus_percent | per_item | salary_plus_per_item | percent_plus_per_item with base, percent, per_item. ru: поставь Айбеку оклад 30000 и 2 процента.',
        parameters: sObj({
          'employee': sStr(),
          'scheme': sStr(values: ['salary', 'percent', 'salary_plus_percent', 'per_item', 'salary_plus_per_item', 'percent_plus_per_item']),
          'base': sNum(),
          'percent': sNum(),
          'per_item': sNum(),
          'pick': sInt(),
        }, required: ['employee', 'scheme']),
      ),
      group: ToolGroup.action,
      allowed: canEditSalary,
      needs: 'Схема оплаты',
      intentWords: Intents.salaryScheme,
      numericArgs: const ['base', 'percent', 'per_item'],
      objectArgs: const ['employee'],
      run: (r) => guarded(r.env, () async {
        final env = r.env;
        final lang = env.lang;
        final pp = await person(env, _s(r.args['employee']), r.args['pick']);
        if (pp.stop != null) return pp.stop!;
        final who = pp.p!;
        final scheme = _s(r.args['scheme']);
        if (scheme.isEmpty) return ToolResult(ToolStatus.clarify, _x('needScheme', lang));
        final old = await src.scheme(who.id);
        final next = PaySchemeData(scheme, _dbl(r.args['base']) ?? old?.base ?? 0, _dbl(r.args['percent']) ?? old?.percent ?? 0, _dbl(r.args['per_item']) ?? old?.perItem ?? 0);
        await src.setScheme(who.id, next);
        final what = [
          scheme,
          if (next.base > 0) _m(next.base, lang),
          if (next.percent > 0) '${next.percent} %',
          if (next.perItem > 0) _m(next.perItem, lang),
        ].join(', ');
        final say = _x('schemeDone', lang, {'name': _clip(who.name, 30), 'what': what});
        await k.commit(
          r,
          tool: 'salary_scheme',
          say: say,
          label: (l) => tr5(l, 'схема оплаты', 'төлөм схемасы', "to'lov sxemasi", 'pay scheme', 'ödeme şeması'),
          data: {'employee_code': 'С', 'scheme': scheme},
          // Отмена — прежние значения.
          undoFn: old == null
              ? null
              : (l) async {
                  await src.setScheme(who.id, old);
                  return UndoResult.ok;
                },
          whyNot: old == null ? (l) => am('why.salary', l) : null,
        );
        return ToolResult(ToolStatus.ok, say, undo: old != null);
      }),
    );
  }

  /// ОДИН короткий инструмент на всё, чего в мобилке ещё нет (задачи, табель, зарплата, выплата, схема оплаты) и что сервер не
  /// хранит (штрафы, премии — Б-63). Модель вызывает его с названием возможности и получает честный ответ: не выдумывает и не
  /// говорит «сделано». Когда В7 даст возможность — она уходит из списка, а полноценные инструменты появляются сами.
  AssistantTool unavailable() {
    final missing = <String>[
      if (!has(StaffCap.tasks)) 'tasks',
      if (!has(StaffCap.timesheet)) 'timesheet',
      if (!has(StaffCap.salary)) 'salary',
      if (!has(StaffCap.payout) || !has(StaffCap.salary)) 'payout',
      if (!has(StaffCap.scheme)) 'scheme',
      'fines',
    ];
    return AssistantTool(
      decl: ToolDecl(
        'staff_unavailable',
        'Call ONCE when the user asks for something the phone app cannot do yet (${missing.join(', ')}); '
        'it returns the honest answer to say. Fines/bonuses for staff are NOT stored on the server. ru: оштрафуй Айбека, поставь задачу, выплати зарплату.',
        parameters: sObj({'feature': sStr(values: missing)}, required: ['feature']),
      ),
      group: ToolGroup.read,
      allowed: (s) => canEditSalary(s) || canManageTasks(s) || canViewSalary(s) || s.can(Session.viewShifts),
      needs: 'Сотрудники',
      run: (r) async {
        final f = _s(r.args['feature']);
        if (f == 'fines' || !missing.contains(f)) {
          return ToolResult(ToolStatus.error, _x('fineNo', r.env.lang), data: {'unavailable': true, 'feature': 'fines', 'needs': 'Б-63'});
        }
        return _unavailable(r.env, f);
      },
    );
  }
}
