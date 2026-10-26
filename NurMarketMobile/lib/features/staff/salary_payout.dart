// 2026-10-26 (голос-управление, В7): выплата зарплаты и «Выплачено за период» — перенос правил ПК один к одному
// (SalaryPayoutService кассы, программа владельца). Никаких новых серверных методов: расход POST api/construction/cashflows/
// со статьёй «Зарплата», метка сотрудника и периода «[#<user> дд.мм.гггг–дд.мм.гггг]» в конце названия, ключ выплаты в
// source_id и idempotency_key.
//
// Правила (из ПК, не менять без владельца):
//  - ключ создаётся один раз на выплату; повтор новым POST — только после явного 400 по конкретному полю (запрос не записан);
//  - ответ не дошёл (тайм-аут, обрыв, 5xx) — НЕ повторять: запись «unknown» в журнале телефона — ЗАМОК, новая выплата этому
//    сотруднику закрыта, пока владелец не проверит («Проверить на сервере» — по source_id; «есть / нет на сайте» — решение);
//  - журнал выплат не читается — не платим (без него нет защиты от двойной выплаты);
//  - повтор той же выплаты в течение 10 секунд не проводится (двойное нажатие, дубль вызова инструмента);
//  - «не уверен — не подставляй сумму»: выплаты с сервера прочитаны не полностью → «проверено частично», сумма не подставляется;
//  - «Выплачено» — выплаты, чей ПЕРИОД ЗАРПЛАТЫ целиком внутри периода на экране; пересекается частично — пометкой, не вычитается;
//  - расход подтверждён сразу (approved) только у владельца, у остальных — pending (ждёт подтверждения на сайте);
//  - отмена выплаты с телефона невозможна — только запрос владельцу (cashflows/{id}/cancel-request/), решает владелец на сайте.
// Не перенесено (честно): выплата «из ящика смены» и «из кармана владельца» (парная запись «Вложение владельца») — только
// касса компании. Источник «касса продаж филиала» разрешён с предупреждением, как на ПК.
import 'dart:async';
import 'dart:convert';

import '../../core/api/api_client.dart';
import '../ops/ops_repository.dart' show OpsRepository;

const kSalaryCategoryTitle = 'Зарплата';

// ---------------------------------------------------------------------------------------------------------------------
// Хранилище настроек (журнал выплат, статья «Зарплата», последняя касса) — таблица settings телефона, схема базы не меняется
// ---------------------------------------------------------------------------------------------------------------------

abstract class SettingsPort {
  Future<String?> get(String key);
  Future<void> set(String key, String value);
}

// ---------------------------------------------------------------------------------------------------------------------
// Модели
// ---------------------------------------------------------------------------------------------------------------------

class PayoutCashbox {
  final String id;
  final String name;
  final String role;
  final bool? isConsumption;
  final double? balance;
  const PayoutCashbox(this.id, this.name, this.role, this.isConsumption, [this.balance]);

  /// Касса продаж (pos_main, pos_branch…): расход из неё уменьшает остаток кассы продаж филиала.
  bool get isSales => role.toLowerCase().startsWith('pos');
}

class PayoutRequest {
  final String key;
  final String userId;
  final String employee;
  final DateTime from;
  final DateTime to;
  final double amount;
  final PayoutCashbox cashbox;
  final String comment;

  /// salary | advance
  final String kind;
  const PayoutRequest({
    required this.key,
    required this.userId,
    required this.employee,
    required this.from,
    required this.to,
    required this.amount,
    required this.cashbox,
    this.comment = '',
    this.kind = 'salary',
  });
}

enum PayoutVerdict { done, rejected, unknown }

class PayoutResult {
  final PayoutVerdict verdict;
  final String? flowId;
  final String? status;

  /// Текст сервера либо код вида «#lock» / «#dup» / «#journal» (его переводит тот, кто показывает).
  final String? error;
  final SalaryPayoutRecord? lockRecord;
  const PayoutResult(this.verdict, {this.flowId, this.status, this.error, this.lockRecord});
}

/// Запись журнала выплат телефона. status: sending (ушла на сервер) | ok | unknown (ответ не дошёл — замок) | absent (владелец:
/// «выплаты нет на сайте», замок снят).
class SalaryPayoutRecord {
  String key;
  String userId;
  String employee;
  DateTime periodFrom;
  DateTime periodTo;
  double amount;
  String kind;
  String cashboxId;
  String cashboxName;
  String by;
  DateTime createdAt;
  String status;
  String? flowId;
  String sourceId;
  SalaryPayoutRecord({
    required this.key,
    required this.userId,
    required this.employee,
    required this.periodFrom,
    required this.periodTo,
    required this.amount,
    this.kind = 'salary',
    this.cashboxId = '',
    this.cashboxName = '',
    this.by = '',
    required this.createdAt,
    required this.status,
    this.flowId,
    this.sourceId = '',
  });

  Map<String, dynamic> toJson() => {
    'key': key,
    'user': userId,
    'emp': employee,
    'from': periodFrom.toIso8601String(),
    'to': periodTo.toIso8601String(),
    'amount': amount,
    'kind': kind,
    'box': cashboxId,
    'boxName': cashboxName,
    'by': by,
    'at': createdAt.toIso8601String(),
    'status': status,
    'flow': flowId,
    'sid': sourceId,
  };

  static SalaryPayoutRecord? fromJson(Object? j) {
    if (j is! Map) return null;
    final from = DateTime.tryParse('${j['from']}');
    final to = DateTime.tryParse('${j['to']}');
    final at = DateTime.tryParse('${j['at']}');
    if (from == null || to == null || at == null) return null;
    return SalaryPayoutRecord(
      key: '${j['key'] ?? ''}',
      userId: '${j['user'] ?? ''}',
      employee: '${j['emp'] ?? ''}',
      periodFrom: from,
      periodTo: to,
      amount: (j['amount'] as num?)?.toDouble() ?? 0,
      kind: '${j['kind'] ?? 'salary'}',
      cashboxId: '${j['box'] ?? ''}',
      cashboxName: '${j['boxName'] ?? ''}',
      by: '${j['by'] ?? ''}',
      createdAt: at,
      status: '${j['status'] ?? ''}',
      flowId: j['flow'] as String?,
      sourceId: '${j['sid'] ?? ''}',
    );
  }
}

/// Насколько полно прочитаны выплаты с сервера. full — видны все выплаты (в т. ч. без статьи); partial — только расходы статьи
/// «Зарплата» или есть непривязанные; none — ничего не прочитано (только журнал телефона).
enum PaidCoverage { none, partial, full }

class PartialPaid {
  final DateTime from;
  final DateTime to;
  final double amount;
  const PartialPaid(this.from, this.to, this.amount);
}

class UserPaid {
  final double paid;
  final bool byName;
  final List<PartialPaid> partials;
  const UserPaid(this.paid, this.byName, this.partials);
}

class PaidResult {
  final Map<String, UserPaid> users;
  final PaidCoverage coverage;
  final double unassignedSum;
  final int unassignedCount;
  final bool journalUnavailable;
  const PaidResult(this.users, this.coverage, this.unassignedSum, this.unassignedCount, {this.journalUnavailable = false});
}

/// Почему сумме к выплате нельзя доверять (null у [Trust.reason] — доверять можно).
enum TrustReason { timeout, journal, notChecked, unassigned, partial, partialNotes, byName }

class Trust {
  final bool sure;
  final TrustReason? reason;
  const Trust(this.sure, [this.reason]);
}

/// Одно правило «доверять ли «Выплачено» и подставлять ли сумму» — для таблицы, окна выплаты, голоса и пересчёта.
Trust trustPaid(PaidResult? result, UserPaid? info, {bool timedOut = false}) {
  if (timedOut) return const Trust(false, TrustReason.timeout);
  if (result != null && result.journalUnavailable) return const Trust(false, TrustReason.journal);
  if (result == null || result.coverage == PaidCoverage.none) return const Trust(false, TrustReason.notChecked);
  if (result.unassignedCount > 0) return const Trust(false, TrustReason.unassigned);
  if (result.coverage == PaidCoverage.partial) return const Trust(false, TrustReason.partial);
  if (info != null && info.partials.isNotEmpty) return const Trust(false, TrustReason.partialNotes);
  if (info != null && info.byName) return const Trust(false, TrustReason.byName);
  return const Trust(true);
}

// ---------------------------------------------------------------------------------------------------------------------
// Разбор названий и дат
// ---------------------------------------------------------------------------------------------------------------------

final _sourceIdRx = RegExp(r'^salary:(?<u>[^:]+):(?<f>\d{8})-(?<t>\d{8}):(?<k>.+)$');
final _nameTagRx = RegExp(r'\[#(?<u>[^\s\]]+) (?<f>\d{2}\.\d{2}\.\d{4})[–-](?<t>\d{2}\.\d{2}\.\d{4})\]\s*$');
final _parenPeriodRx = RegExp(r'\((?<f>\d{2}\.\d{2}\.\d{4})(?:[–-](?<t>\d{2}\.\d{2}\.\d{4}))?\)');

String _two(int v) => v.toString().padLeft(2, '0');
String _d(DateTime d) => '${_two(d.day)}.${_two(d.month)}.${d.year}';
String _ymd(DateTime d) => '${d.year}${_two(d.month)}${_two(d.day)}';
DateTime _only(DateTime d) => DateTime(d.year, d.month, d.day);

DateTime? _parseDmy(String s) {
  final p = s.split('.');
  if (p.length != 3) return null;
  final d = int.tryParse(p[0]);
  final m = int.tryParse(p[1]);
  final y = int.tryParse(p[2]);
  if (d == null || m == null || y == null) return null;
  return DateTime(y, m, d);
}

DateTime? _parseYmd(String s) {
  if (s.length != 8) return null;
  final y = int.tryParse(s.substring(0, 4));
  final m = int.tryParse(s.substring(4, 6));
  final d = int.tryParse(s.substring(6, 8));
  if (y == null || m == null || d == null) return null;
  return DateTime(y, m, d);
}

/// Период в названии расхода: «01.10.2026–19.10.2026» (один день — одна дата).
String payoutPeriodText(DateTime from, DateTime to) => _only(from) == _only(to) ? _d(from) : '${_d(from)}–${_d(to)}';

/// Название расхода на сайте — всегда по-русски (это запись NurCRM). Аванс тоже содержит слово «Зарплата» — иначе поиск
/// «Зарплата» его не найдёт, и «Выплачено» его потеряет.
String payoutFlowName(String employee, DateTime from, DateTime to, {String kind = 'salary'}) =>
    '${kind == 'advance' ? 'Зарплата (аванс)' : 'Зарплата'}: ${employee.trim()} (${payoutPeriodText(from, to)})';

String _nameTag(String userId, DateTime from, DateTime to) => ' [#${userId.trim()} ${_d(from)}–${_d(to)}]';

String payoutSourceId(String userId, DateTime from, DateTime to, String key) => 'salary:${userId.trim()}:${_ymd(from)}-${_ymd(to)}:$key';

bool _overlaps(DateTime af, DateTime at, DateTime bf, DateTime bt) => !_only(af).isAfter(_only(bt)) && !_only(at).isBefore(_only(bf));
bool _inside(DateTime af, DateTime at, DateTime bf, DateTime bt) => !_only(af).isBefore(_only(bf)) && !_only(at).isAfter(_only(bt));

class _PayoutId {
  final String userId;
  final DateTime from;
  final DateTime to;
  final String? key;
  const _PayoutId(this.userId, this.from, this.to, this.key);
}

_PayoutId? _parseSourceId(String sid) {
  final m = _sourceIdRx.firstMatch(sid);
  if (m == null) return null;
  final f = _parseYmd(m.namedGroup('f')!);
  final t = _parseYmd(m.namedGroup('t')!);
  if (f == null || t == null) return null;
  return _PayoutId(m.namedGroup('u')!, f, t, m.namedGroup('k'));
}

_PayoutId? _parseNameTag(String name) {
  final m = _nameTagRx.firstMatch(name);
  if (m == null) return null;
  final f = _parseDmy(m.namedGroup('f')!);
  final t = _parseDmy(m.namedGroup('t')!);
  if (f == null || t == null) return null;
  return _PayoutId(m.namedGroup('u')!, f, t, null);
}

(DateTime, DateTime)? _parenPeriod(String name) {
  final m = _parenPeriodRx.firstMatch(name);
  if (m == null) return null;
  final f = _parseDmy(m.namedGroup('f')!);
  if (f == null) return null;
  final ts = m.namedGroup('t');
  final t = ts == null ? f : _parseDmy(ts);
  if (t == null) return null;
  return (f, t);
}

class _ServerFlow {
  final String id;
  final String sourceId;
  final String name;
  final double amount;
  final bool rejected;
  final DateTime? createdAt;
  final DateTime? date;
  final String? category;
  const _ServerFlow(this.id, this.sourceId, this.name, this.amount, this.rejected, this.createdAt, this.date, this.category);
}

class _ServerRead {
  final List<_ServerFlow> flows;
  final PaidCoverage coverage;
  final bool searchFull;
  final String? categoryId;
  const _ServerRead(this.flows, this.coverage, this.searchFull, this.categoryId);
}

class _Filtered {
  final List<_ServerFlow> flows;
  final bool complete;
  const _Filtered(this.flows, this.complete);
}

class _Tally {
  double paid = 0;
  bool byName = false;
  final List<PartialPaid> partials = [];
}

// ---------------------------------------------------------------------------------------------------------------------
// Сервис
// ---------------------------------------------------------------------------------------------------------------------

class SalaryPayoutService {
  final ApiClient api;
  final SettingsPort settings;

  /// Чей журнал: вход (логин) — на одном телефоне разные магазины не смешиваются.
  final String scope;
  final String byName;
  final bool approvedRightAway;
  final DateTime Function() now;
  SalaryPayoutService(this.api, this.settings, {required this.scope, this.byName = '', this.approvedRightAway = false, DateTime Function()? now}) : now = now ?? DateTime.now;

  String get _journalKey => 'salary.journal.v1.$scope';
  String get _catKey => 'salary.category.$scope';
  String get _boxKey => 'salary.cashbox.$scope';

  // ---- журнал

  Future<void> _gate = Future.value();

  /// Очередь: изменения журнала идут по одному (чтение-запись целиком).
  Future<T> _locked<T>(Future<T> Function() f) {
    final c = Completer<T>();
    final prev = _gate;
    _gate = c.future.then<void>((_) {}, onError: (_) {});
    prev.then((_) async {
      try {
        c.complete(await f());
      } catch (e, st) {
        c.completeError(e, st);
      }
    });
    return c.future;
  }

  bool _broken = false;

  /// null — журнал не читается (это НЕ пустой журнал). Повреждённый журнал сохраняется рядом и закрывает выплаты.
  Future<List<SalaryPayoutRecord>?> _load() async {
    if (_broken) return null;
    final String? raw;
    try {
      raw = await settings.get(_journalKey);
    } catch (_) {
      return null;
    }
    if (raw == null || raw.isEmpty) return [];
    try {
      final j = jsonDecode(raw);
      if (j is! List) throw const FormatException('not a list');
      return [for (final x in j) ?SalaryPayoutRecord.fromJson(x)];
    } catch (_) {
      _broken = true;
      try {
        await settings.set('$_journalKey.bad', raw);
      } catch (_) {}
      return null;
    }
  }

  Future<bool> _change(void Function(List<SalaryPayoutRecord>) change) => _locked(() async {
    final list = await _load();
    if (list == null) return false;
    change(list);
    try {
      await settings.set(_journalKey, jsonEncode([for (final r in list.take(300)) r.toJson()]));
      return true;
    } catch (_) {
      return false;
    }
  });

  Future<bool> _upsert(SalaryPayoutRecord r) => _change((l) {
    l.removeWhere((x) => x.key == r.key);
    l.insert(0, r);
  });

  Future<bool> _remove(String key) => _change((l) => l.removeWhere((x) => x.key == key));

  Future<List<SalaryPayoutRecord>> journal() async => await _load() ?? [];

  /// Журнал читается? (false — выплаты закрыты)
  Future<bool> journalReadable() async => await _load() != null;

  /// Замок сотрудника: прошлая выплата без ответа сервера. readable=false — журнал не прочитался, замок неизвестен.
  Future<({bool readable, SalaryPayoutRecord? lock})> lockFor(String userId) async {
    final all = await _load();
    if (all == null) return (readable: false, lock: null);
    final locked = all.where((r) => (r.status == 'unknown' || r.status == 'sending') && r.userId == userId).toList()
      ..sort((a, b) => b.createdAt.compareTo(a.createdAt));
    return (readable: true, lock: locked.firstOrNull);
  }

  // ---- кассы и статья «Зарплата»

  Future<List<PayoutCashbox>> cashboxes() async {
    final rows = await getAllPages(api, 'api/construction/cashboxes/', pageSize: 100, maxPages: 5);
    final out = <PayoutCashbox>[];
    for (final m in rows) {
      final id = str(m, 'id') ?? str(m, 'uuid');
      if (id == null || id.isEmpty) continue;
      final cons = m['is_consumption'];
      double? bal;
      for (final f in const ['current_balance', 'balance']) {
        if (bal == null && m[f] != null) bal = num0(m, f);
      }
      out.add(PayoutCashbox(id, str(m, 'name') ?? str(m, 'title') ?? id, str(m, 'role') ?? str(m, 'cashbox_role') ?? '', cons is bool ? cons : null, bal));
    }
    return out;
  }

  /// Касса по умолчанию — касса расходов, как у сайта (is_consumption / expense_variable / «Прочие расходы»). Прошлый выбор —
  /// только если касс расходов нет и это не касса продаж. Иначе первая не pos_*; ролей нет — -1 (выбирает человек).
  static int defaultCashboxIndex(List<PayoutCashbox> list, String? savedId) {
    int idx(bool Function(PayoutCashbox) f) => list.indexWhere(f);
    var i = idx((b) => b.isConsumption == true);
    if (i < 0) i = idx((b) => b.role.toLowerCase() == 'expense_variable');
    if (i < 0) i = idx((b) => b.name.toLowerCase().contains('прочие расход'));
    if (i < 0) i = idx((b) => savedId != null && savedId.isNotEmpty && b.id == savedId && !b.isSales);
    if (i < 0) i = idx((b) => b.role.isNotEmpty && !b.isSales);
    return i;
  }

  Future<String?> savedCashboxId() async {
    try {
      return await settings.get(_boxKey);
    } catch (_) {
      return null;
    }
  }

  Future<void> rememberCashbox(PayoutCashbox b) async {
    try {
      await settings.set(_boxKey, b.id);
    } catch (_) {}
  }

  /// Касса для выплаты без выбора человеком (голос): по умолчанию как на ПК; null — подходящей нет.
  Future<PayoutCashbox?> defaultCashbox() async {
    final list = await cashboxes();
    final i = defaultCashboxIndex(list, await savedCashboxId());
    if (i >= 0) return list[i];
    // Единственная касса компании (даже если это касса продаж) — другой нет; ответ предупредит, что её остаток уменьшился.
    // Несколько касс и ни одной расходной — не угадываем: выплата на сайте или в программе владельца.
    return list.length == 1 ? list.first : null;
  }

  String? _catMem;
  DateTime? _noCatUntil;

  /// id статьи «Зарплата» (slug salary или название; несколько — самая старая). Нет и [create] — создаётся один раз. null —
  /// справочника нет / создать не дали: выплата тогда идёт БЕЗ статьи (так на ПК).
  Future<String?> categoryId({required bool create}) async {
    if (_catMem != null) return _catMem;
    try {
      final stored = await settings.get(_catKey);
      if (stored != null && stored.isNotEmpty) return _catMem = stored;
    } catch (_) {}
    if (!create && _noCatUntil != null && now().isBefore(_noCatUntil!)) return null;
    final rows = <Map<String, dynamic>>[];
    try {
      for (var page = 1; page <= 10; page++) {
        final data = await api.get('api/construction/cashflow-categories/', query: {'page_size': '500', 'page': '$page'}, timeout: const Duration(seconds: 15));
        rows.addAll(asList(data));
        if (!(data is Map && data['next'] is String)) break;
      }
    } catch (_) {
      return null;
    }
    String? bySlug;
    final byTitle = <(String, DateTime?, int)>[];
    for (var i = 0; i < rows.length; i++) {
      final x = rows[i];
      final id = str(x, 'id') ?? str(x, 'uuid');
      if (id == null || id.isEmpty) continue;
      final title = (str(x, 'title') ?? str(x, 'name') ?? '').trim().toLowerCase();
      final slug = str(x, 'slug') ?? '';
      if (slug == 'salary' || slug == 'salaries' || slug == 'zarplata') {
        bySlug ??= id;
      } else if (title == 'зарплата' || title == 'зарплаты' || title == 'заработная плата') {
        byTitle.add((id, DateTime.tryParse(str(x, 'created_at') ?? ''), i));
      }
    }
    byTitle.sort((a, b) {
      final c = (a.$2 ?? DateTime(9999)).compareTo(b.$2 ?? DateTime(9999));
      return c != 0 ? c : a.$3.compareTo(b.$3);
    });
    var found = bySlug ?? byTitle.firstOrNull?.$1;
    if (found == null && create) {
      try {
        final r = await api.post('api/construction/cashflow-categories/', body: {'title': kSalaryCategoryTitle}, timeout: const Duration(seconds: 15));
        found = r is Map ? (str(r.cast<String, dynamic>(), 'id') ?? str(r.cast<String, dynamic>(), 'uuid')) : null;
      } catch (_) {
        found = null;
      }
    }
    if (found != null && found.isNotEmpty) {
      _catMem = found;
      _noCatUntil = null;
      try {
        await settings.set(_catKey, found);
      } catch (_) {}
    } else if (!create) {
      _noCatUntil = now().add(const Duration(minutes: 10));
    }
    return found;
  }

  Future<void> _forgetCategory() async {
    _catMem = null;
    try {
      await settings.set(_catKey, '');
    } catch (_) {}
  }

  // ---- выплата

  static const dupWindow = Duration(seconds: 10);

  Future<PayoutResult> pay(PayoutRequest req) async {
    final from = _only(req.from);
    final to = _only(req.to);
    final lock = await lockFor(req.userId);
    if (!lock.readable) return const PayoutResult(PayoutVerdict.rejected, error: '#journal');
    if (lock.lock != null) return PayoutResult(PayoutVerdict.rejected, error: '#lock', lockRecord: lock.lock);

    // Повтор той же выплаты за 10 секунд (двойное нажатие, повторный вызов) не проводим.
    final amount = (req.amount * 100).round() / 100;
    final all = await _load();
    if (all == null) return const PayoutResult(PayoutVerdict.rejected, error: '#journal');
    final t = now();
    final dup = all.any(
      (r) =>
          r.userId == req.userId &&
          r.kind == req.kind &&
          (r.amount - amount).abs() < 0.005 &&
          _only(r.periodFrom) == from &&
          _only(r.periodTo) == to &&
          r.status != 'absent' &&
          t.difference(r.createdAt).abs() < dupWindow,
    );
    if (dup) return const PayoutResult(PayoutVerdict.rejected, error: '#dup');

    final tag = _nameTag(req.userId, from, to);
    final baseName = payoutFlowName(req.employee, from, to, kind: req.kind) + (req.comment.trim().isEmpty ? '' : ' — ${req.comment.trim()}');
    // Метка — всегда и целиком: обрезается только начало названия.
    final name = (baseName.length > 255 - tag.length ? baseName.substring(0, 255 - tag.length) : baseName) + tag;
    final record = SalaryPayoutRecord(
      key: req.key,
      userId: req.userId,
      employee: req.employee.trim(),
      periodFrom: from,
      periodTo: to,
      amount: amount,
      kind: req.kind,
      cashboxId: req.cashbox.id,
      cashboxName: req.cashbox.name,
      by: byName,
      createdAt: t,
      status: 'sending',
      sourceId: payoutSourceId(req.userId, from, to, req.key),
    );
    // Без записи «ушла на сервер» защиты от двойной выплаты нет — не отправляем.
    if (!await _upsert(record)) return const PayoutResult(PayoutVerdict.rejected, error: '#journal');

    String? catId;
    try {
      catId = await categoryId(create: true).timeout(const Duration(seconds: 15), onTimeout: () => null);
    } catch (_) {
      catId = null;
    }

    final body = <String, dynamic>{
      'cashbox': req.cashbox.id,
      'type': 'expense',
      'name': name,
      'amount': amount.toStringAsFixed(2),
      'status': approvedRightAway ? 'approved' : 'pending',
      'source_id': record.sourceId,
      'idempotency_key': req.key,
      if (catId != null && catId.isNotEmpty) 'category': catId,
    };

    // Повтор — только после явного 400 по одному из полей (запрос не записан): category, source_id (два раза).
    for (var attempt = 0; attempt < 4; attempt++) {
      try {
        final r = await api.post('api/construction/cashflows/', body: body, timeout: const Duration(seconds: 45));
        final m = r is Map ? r.cast<String, dynamic>() : <String, dynamic>{};
        record.status = 'ok';
        record.flowId = str(m, 'id') ?? str(m, 'uuid');
        await _upsert(record);
        forgetCache();
        return PayoutResult(PayoutVerdict.done, flowId: record.flowId, status: str(m, 'status'));
      } on ApiException catch (e) {
        final field = e.status == 400 ? _rejectedField(e, body) : null;
        if (field != null) {
          if (field == 'source_id' && body['source_id'] != req.key) {
            // «salary:…» не принят — ключ как есть; сотрудник и период остаются в метке названия.
            record.sourceId = req.key;
            await _upsert(record);
            body['source_id'] = req.key;
            continue;
          }
          body.remove(field);
          if (field == 'category') await _forgetCategory();
          if (field == 'source_id') {
            record.sourceId = _noSourceId;
            await _upsert(record);
          }
          continue;
        }
        if (OpsRepository.outcomeUnknown(e)) return _unknown(record, '$e');
        await _remove(req.key);
        return PayoutResult(PayoutVerdict.rejected, error: e.message);
      } catch (e) {
        // не ApiException (обрыв сокета, тайм-аут) — исход неизвестен
        return _unknown(record, '$e');
      }
    }
    await _remove(req.key);
    return const PayoutResult(PayoutVerdict.rejected, error: 'Сервер отверг поля расхода.');
  }

  Future<PayoutResult> _unknown(SalaryPayoutRecord record, String why) async {
    record.status = 'unknown';
    await _upsert(record);
    return PayoutResult(PayoutVerdict.unknown, error: why);
  }

  static const _noSourceId = 'none';

  static String? _rejectedField(ApiException e, Map<String, dynamic> body) {
    for (final f in const ['category', 'source_id']) {
      if (!body.containsKey(f)) continue;
      final p = e.payload;
      if (p is Map && p.containsKey(f)) return f;
      if (e.message.toLowerCase().contains(f)) return f;
    }
    return null;
  }

  /// Запрос владельцу на отмену выплаты (сам расход отменяет владелец или бухгалтер на сайте) — POST cashflows/{id}/cancel-request/.
  Future<void> requestCancel(String flowId, {String reason = ''}) async {
    await api.post('api/construction/cashflows/$flowId/cancel-request/', body: {if (reason.isNotEmpty) 'reason': reason});
  }

  // ---- «неизвестная» выплата

  /// Выплата без ответа найдена на сервере (по source_id, потом по метке «[#user период]» и сумме) — отмечена проведённой.
  /// «Не нашлась» ничего не решает: замок остаётся, снимает его только человек ([resolveUnknown]).
  Future<bool> checkUnknown(String key) async {
    final all = await journal();
    final record = all.where((r) => r.key == key).firstOrNull;
    if (record == null) return false;
    if (record.sourceId != _noSourceId) {
      final sid = record.sourceId.isNotEmpty ? record.sourceId : key;
      try {
        final r = await _filtered({'source_id': sid}, (row) => str(row, 'source_id') == sid, 3);
        final hit = r?.flows.where((f) => !f.rejected).firstOrNull;
        if (hit != null) return await _markFound(record, hit.id);
      } catch (_) {}
    }
    try {
      final s = await _searchSalary();
      if (s != null) {
        final taken = {for (final r in all) if (r.key != key && (r.flowId ?? '').isNotEmpty) r.flowId!};
        final hit = s.flows.where((f) => !f.rejected && !taken.contains(f.id) && _sameRecord(f, _parseNameTag(f.name), record)).firstOrNull;
        if (hit != null) return await _markFound(record, hit.id);
      }
    } catch (_) {}
    return false;
  }

  Future<bool> _markFound(SalaryPayoutRecord record, String flowId) async {
    record.status = 'ok';
    record.flowId = flowId;
    forgetCache();
    return _upsert(record);
  }

  /// Решение человека: paidOnSite — «выплата ЕСТЬ на сайте» (учитывается), иначе «НЕТ на сайте» (замок снят).
  Future<bool> resolveUnknown(String key, {required bool paidOnSite}) async {
    final all = await journal();
    final record = all.where((r) => r.key == key).firstOrNull;
    if (record == null) return false;
    record.status = paidOnSite ? 'ok' : 'absent';
    forgetCache();
    return _upsert(record);
  }

  /// Тот же расход, что запись журнала без номера расхода: сотрудник, период и сумма, записан в пределах ±10 минут.
  static bool _sameRecord(_ServerFlow f, _PayoutId? who, SalaryPayoutRecord r) =>
      who != null &&
      who.userId.toLowerCase() == r.userId.toLowerCase() &&
      who.from == _only(r.periodFrom) &&
      who.to == _only(r.periodTo) &&
      (f.amount - r.amount).abs() < 0.005 &&
      (f.createdAt == null || f.createdAt!.difference(r.createdAt).inMinutes.abs() <= 10);

  // ---- чтение расходов с сервера

  /// Страницы движений с фильтром. null — сервер не соблюдает фильтр (в ответе чужие строки); complete=false — страниц больше
  /// [maxPages] (историю целиком не скачиваем).
  Future<_Filtered?> _filtered(Map<String, String> filter, bool Function(Map<String, dynamic>) matches, int maxPages) async {
    final list = <_ServerFlow>[];
    for (var page = 1; page <= maxPages; page++) {
      final data = await api.get('api/construction/cashflows/', query: {...filter, 'page_size': '500', 'page': '$page'}, timeout: const Duration(seconds: 30));
      final rows = asList(data);
      if (rows.any((r) => !matches(r))) return null;
      for (final r in rows) {
        final id = str(r, 'id') ?? str(r, 'uuid');
        if (id == null || id.isEmpty || (str(r, 'type') ?? '').toLowerCase() != 'expense') continue;
        list.add(
          _ServerFlow(
            id,
            str(r, 'source_id') ?? '',
            str(r, 'name') ?? '',
            num0(r, 'amount'),
            (str(r, 'status') ?? '').toLowerCase() == 'rejected',
            _created(r),
            _flowDate(r),
            _categoryOf(r),
          ),
        );
      }
      if (!(data is Map && data['next'] is String)) return _Filtered(list, true);
    }
    return _Filtered(list, false);
  }

  Future<_Filtered?> _searchSalary() => _filtered(
    {'type': 'expense', 'search': kSalaryCategoryTitle},
    (row) =>
        (str(row, 'type') ?? '').toLowerCase() == 'expense' &&
        ((str(row, 'name') ?? '').toLowerCase().contains('зарплат') || (str(row, 'category_title') ?? '').toLowerCase().contains('зарплат')),
    10,
  );

  static DateTime? _created(Map<String, dynamic> r) {
    final t = str(r, 'created_at');
    return t == null ? null : DateTime.tryParse(t)?.toLocal();
  }

  static DateTime? _flowDate(Map<String, dynamic> r) {
    final t = str(r, 'date');
    if (t != null) {
      final d = DateTime.tryParse(t);
      if (d != null) return t.length <= 10 ? _only(d) : _only(d.toLocal());
    }
    final c = _created(r);
    return c == null ? null : _only(c);
  }

  static String? _categoryOf(Map<String, dynamic> r) {
    final c = r['category'];
    if (c is Map) return '${c['id'] ?? c['uuid'] ?? ''}'.isEmpty ? null : '${c['id'] ?? c['uuid']}';
    return c == null || '$c'.isEmpty ? null : '$c';
  }

  // Кэш 2 минуты по периоду — для переключения разделов; своя выплата и «Обновить» его сбрасывают.
  ({DateTime from, DateTime to, DateTime at, _ServerRead read})? _cache;
  void forgetCache() => _cache = null;

  /// 1) ?category=<Зарплата> и 2) ?type=expense&search=Зарплата — параллельно; 3) только если 2) не сработал —
  /// ?type=expense&date_from=<начало периода> (не больше 4 страниц). full — сработал 2) или 3); partial — только 1); none — ничего.
  Future<_ServerRead> _serverFlows(DateTime from, DateTime to) async {
    final c = _cache;
    if (c != null && c.from == from && c.to == to && now().difference(c.at) < const Duration(minutes: 2)) return c.read;

    String? catId;
    Future<List<_ServerFlow>?> byCategory() async {
      try {
        catId = await categoryId(create: false);
        if (catId == null || catId!.isEmpty) return null;
        final id = catId!;
        final a = await _filtered({'category': id}, (row) => _categoryOf(row) == id, 10);
        return a != null && a.complete ? a.flows : null;
      } catch (_) {
        return null;
      }
    }

    Future<List<_ServerFlow>?> bySearch() async {
      try {
        final b = await _searchSalary();
        return b != null && b.complete ? b.flows : null;
      } catch (_) {
        return null;
      }
    }

    final results = await Future.wait([byCategory(), bySearch()]);
    final catFlows = results[0];
    final searchFlows = results[1];
    final byId = <String, _ServerFlow>{};
    for (final f in [...?catFlows, ...?searchFlows]) {
      byId[f.id] = f;
    }
    final searchFull = searchFlows != null;
    var full = searchFull;
    if (!full) {
      try {
        final a = await _filtered(
          {'type': 'expense', 'date_from': '${from.year}-${_two(from.month)}-${_two(from.day)}', 'date_to': '${now().year}-${_two(now().month)}-${_two(now().day)}'},
          (row) {
            if ((str(row, 'type') ?? '').toLowerCase() != 'expense') return false;
            final d = _flowDate(row);
            return d == null || !d.isBefore(from);
          },
          4,
        );
        if (a != null && a.complete) {
          full = true;
          for (final f in a.flows) {
            byId[f.id] = f;
          }
        }
      } catch (_) {}
    }
    final coverage = full ? PaidCoverage.full : (catFlows != null ? PaidCoverage.partial : PaidCoverage.none);
    final read = _ServerRead(byId.values.toList(), coverage, searchFull, catId);
    if (coverage != PaidCoverage.none) _cache = (from: from, to: to, at: now(), read: read);
    return read;
  }

  /// Выплачено сотрудникам (ключ — user_id) за период. Чья выплата: source_id → метка в названии → журнал телефона (ид расхода,
  /// ключ, или сотрудник+период+сумма+время ±10 мин) → точное ФИО в названии (пометка «по названию»). Расходы «Зарплата», которые
  /// не приписать никому, — отдельной суммой (покрытие «частично»). Журнал телефона учитывается всегда, без двойного счёта.
  Future<PaidResult> paidForPeriod(Iterable<({String userId, String name})> employees, DateTime from, DateTime to) async {
    from = _only(from);
    to = _only(to);
    final users = {for (final e in employees) if (e.userId.trim().isNotEmpty) e.userId};
    final all = await _load();
    final journal = all ?? <SalaryPayoutRecord>[];
    final byFlowId = <String, SalaryPayoutRecord>{};
    final byKey = <String, SalaryPayoutRecord>{};
    final bySourceId = <String, SalaryPayoutRecord>{};
    final withoutFlowId = <SalaryPayoutRecord>[];
    for (final r in journal) {
      if ((r.flowId ?? '').isNotEmpty) {
        byFlowId.putIfAbsent(r.flowId!.toLowerCase(), () => r);
      } else if (r.status == 'ok' || r.status == 'unknown' || r.status == 'sending') {
        withoutFlowId.add(r);
      }
      if (r.key.isNotEmpty) byKey.putIfAbsent(r.key.toLowerCase(), () => r);
      if (r.sourceId.isNotEmpty && r.sourceId != _noSourceId) bySourceId.putIfAbsent(r.sourceId.toLowerCase(), () => r);
    }
    final names = <({String userId, int length, RegExp rx})>[];
    for (final e in employees) {
      final n = e.name.trim();
      if (n.length < 3 || n == '—') continue;
      names.add((userId: e.userId, length: n.length, rx: RegExp('(?<![\\p{L}\\p{N}])${RegExp.escape(n)}(?![\\p{L}\\p{N}])', caseSensitive: false, unicode: true)));
    }
    final used = <String>{};
    final tallies = <String, _Tally>{};
    final serverIds = <String, _ServerFlow>{};
    var unassignedSum = 0.0;
    var unassignedCount = 0;

    void attribute(String uid, DateTime pFrom, DateTime pTo, double amount, bool byNameFlag) {
      final key = users.firstWhere((u) => u.toLowerCase() == uid.toLowerCase(), orElse: () => '');
      if (key.isEmpty || !_overlaps(pFrom, pTo, from, to)) return;
      final t = tallies.putIfAbsent(key, _Tally.new);
      if (_inside(pFrom, pTo, from, to)) {
        t.paid += amount;
        t.byName = t.byName || byNameFlag;
      } else {
        t.partials.add(PartialPaid(pFrom, pTo, amount));
      }
    }

    SalaryPayoutRecord? free(SalaryPayoutRecord? r) => r != null && !used.contains(r.key) ? r : null;
    SalaryPayoutRecord? matchJournal(_ServerFlow f, _PayoutId? who) {
      return free(byFlowId[f.id.toLowerCase()]) ??
          (who?.key != null ? free(byKey[who!.key!.toLowerCase()]) : null) ??
          (f.sourceId.isNotEmpty ? (free(byKey[f.sourceId.toLowerCase()]) ?? free(bySourceId[f.sourceId.toLowerCase()])) : null) ??
          withoutFlowId.where((r) => !used.contains(r.key) && _sameRecord(f, who, r)).firstOrNull;
    }

    final read = await _serverFlows(from, to);
    for (final f in read.flows) {
      serverIds[f.id] = f;
      if (f.rejected) continue;
      var who = _parseSourceId(f.sourceId) ?? _parseNameTag(f.name);
      final rec = matchJournal(f, who);
      if (rec != null) {
        used.add(rec.key);
        who ??= _PayoutId(rec.userId, _only(rec.periodFrom), _only(rec.periodTo), rec.key);
      }
      if (who != null) {
        attribute(who.userId, who.from, who.to, f.amount, false);
        continue;
      }
      final salaryLike = (read.categoryId != null && read.categoryId!.isNotEmpty && f.category == read.categoryId) || f.name.toLowerCase().contains('зарплат');
      if (!salaryLike) continue;
      // Не приписан ни меткой, ни журналом — точное ФИО сотрудника в названии (старый формат, выплата с другого устройства).
      final hits = names.where((n) => n.rx.hasMatch(f.name)).toList()..sort((a, b) => b.length.compareTo(a.length));
      String? byNameUser;
      if (hits.isNotEmpty && !(hits.length > 1 && hits[1].length == hits[0].length && hits[1].userId != hits[0].userId)) byNameUser = hits[0].userId;
      final period = _parenPeriod(f.name);
      if (byNameUser != null) {
        final (pFrom, pTo) = period ?? (f.date != null ? (f.date!, f.date!) : (DateTime(1), DateTime(1)));
        // Запись «ЕСТЬ на сайте» без номера расхода — та же выплата, что этот расход по ФИО (сотрудник, период, сумма): один раз.
        final manual = withoutFlowId
            .where(
              (r) =>
                  !used.contains(r.key) &&
                  r.status == 'ok' &&
                  r.userId.toLowerCase() == byNameUser!.toLowerCase() &&
                  (r.amount - f.amount).abs() < 0.005 &&
                  (period == null || (_only(r.periodFrom) == pFrom && _only(r.periodTo) == pTo)),
            )
            .firstOrNull;
        if (manual != null) {
          used.add(manual.key);
          attribute(byNameUser, _only(manual.periodFrom), _only(manual.periodTo), f.amount, false);
        } else if (period != null || (f.date != null && !f.date!.isBefore(from) && !f.date!.isAfter(to))) {
          attribute(byNameUser, pFrom, pTo, f.amount, true);
        }
        continue;
      }
      // Чей — не понять: если расход за этот период, не теряем молча.
      final inPeriod = period != null ? _overlaps(period.$1, period.$2, from, to) : (f.date != null && !f.date!.isBefore(from) && !f.date!.isAfter(to));
      if (inPeriod) {
        unassignedSum += f.amount;
        unassignedCount++;
      }
    }

    for (final r in journal) {
      if (r.status != 'ok' || used.contains(r.key)) continue;
      if (!users.any((u) => u.toLowerCase() == r.userId.toLowerCase())) continue;
      final fid = r.flowId;
      if (fid != null && fid.isNotEmpty) {
        // Сервер знает расход как отклонённый — денег не ушло.
        final known = serverIds[fid];
        if (known != null && known.rejected) continue;
        // Полностью прочитанный сервер этот расход должен был показать — его там нет (удалён на сайте).
        final shouldSee = read.coverage == PaidCoverage.full && (read.searchFull || !_only(r.createdAt).isBefore(from));
        if (shouldSee && known == null) continue;
      }
      attribute(r.userId, _only(r.periodFrom), _only(r.periodTo), r.amount, false);
    }

    var coverage = read.coverage;
    if (unassignedCount > 0 && coverage == PaidCoverage.full) coverage = PaidCoverage.partial;
    return PaidResult(
      {for (final kv in tallies.entries) kv.key: UserPaid((kv.value.paid * 100).round() / 100, kv.value.byName, kv.value.partials)},
      coverage,
      (unassignedSum * 100).round() / 100,
      unassignedCount,
      journalUnavailable: all == null,
    );
  }
}
