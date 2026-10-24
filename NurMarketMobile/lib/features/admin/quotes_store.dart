import 'dart:async';
import 'dart:convert';
import 'dart:math' show max;

import 'package:flutter/material.dart' show DateUtils;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_client.dart';
import '../../core/api/session_store.dart';
import '../../core/db/database.dart';
import '../../core/providers.dart';
import '../../core/util/ui.dart' show parseNum;
import '../sale/cart.dart' show roundMoney;

/// 13.10, владелец: «коммерческое предложение и ЭСФ в мобилку добавь» (доски КП1–КП2, СЧ1–СЧ2). Модель КП и счёта.
/// 2026-10-21, владелец: «хранить на сервере, документы должны быть и в программе, и в телефоне». Документы общие с
/// программой владельца на ПК (ClientDocsStore.cs) — общие настройки компании NurCRM api/main/app-settings/nurmarket-mobile/,
/// ключ «quotes». Формат согласован с ПК: у документа поле updatedAt (ISO 8601 UTC, ставится при каждом изменении),
/// удаление — «надгробие» {id, deleted: true, updatedAt} (хранится 90 дней), незнакомые поля (ПК: kind, clientId, vat, note…;
/// у строки pr0, dp, f) телефон сохраняет как есть.
class QuoteLine {
  final String productId;
  final String name;
  final String unit;
  double qty;
  double _price;

  /// 2026-10-21: незнакомые поля строки (у ПК: pr0 — цена до скидки, dp — скидка %, f — свободная строка).
  final Map<String, dynamic> extra;
  QuoteLine({required this.productId, required this.name, this.unit = 'шт', this.qty = 1, required double price, Map<String, dynamic>? extra})
    // ignore: prefer_initializing_formals
    : _price = price,
      extra = extra ?? {};

  double get price => _price;

  /// 2026-10-21: цену поменяли на телефоне — скидка строки ПК (pr0, dp) больше не про эту цену, иначе ПК покажет старую скидку.
  set price(double v) {
    if ((v - _price).abs() > 0.00001) {
      extra
        ..remove('pr0')
        ..remove('dp');
    }
    _price = v;
  }

  double get sum => roundMoney(qty * price);

  /// Копия строки со всеми полями (и незнакомыми).
  QuoteLine copy() => QuoteLine(productId: productId, name: name, unit: unit, qty: qty, price: _price, extra: _deepCopy(extra));

  static const _keys = {'p', 'n', 'u', 'q', 'pr'};
  Map<String, dynamic> toJson() => {..._deepCopy(extra), 'p': productId, 'n': name, 'u': unit, 'q': qty, 'pr': price};
  static QuoteLine fromJson(Map<String, dynamic> j) => QuoteLine(
    productId: _s(j['p']),
    name: _s(j['n']),
    unit: j['u'] == null ? 'шт' : _s(j['u']),
    qty: _n(j['q']) ?? 1,
    price: _n(j['pr']) ?? 0,
    extra: _deepCopy(j)..removeWhere((k, _) => _keys.contains(k)),
  );
}

/// 2026-10-21, владелец: «на Enter при добавлении товара в КП и счёт он удаляет или дублирует». Товар, который уже есть
/// в документе, прибавляет количество, а не вторую строку (раньше «Из текущего чека» дважды — все строки вдвое).
/// [anyPrice] — выбор из каталога: прибавить к строке этого товара, даже если цену в ней поменяли вручную.
void addQuoteLine(List<QuoteLine> lines, QuoteLine add, {bool anyPrice = false}) {
  final same = add.productId.isEmpty
      ? null
      : lines.where((x) => x.productId == add.productId && x.unit == add.unit && (anyPrice || (x.price - add.price).abs() < 0.005)).firstOrNull;
  if (same == null) {
    lines.add(add);
  } else {
    same.qty = double.parse((same.qty + add.qty).toStringAsFixed(3));
  }
}

/// 2026-10-21: скидка документа — сумма («150») или процент («10%»), не больше суммы строк, до тыйына.
double parseDiscount(String text, double subtotal) {
  final t = text.trim();
  if (t.isEmpty) return 0;
  final pct = t.endsWith('%');
  final v = parseNum(pct ? t.substring(0, t.length - 1) : t) ?? 0;
  final sum = pct ? subtotal * v / 100 : v;
  return roundMoney(sum.clamp(0, max(subtotal, 0)).toDouble());
}

/// Сумма строк: каждая строка округлена до тыйына, как в PDF (итог документа и таблица совпадают).
double linesSubtotal(Iterable<QuoteLine> lines) => roundMoney(lines.fold(0.0, (s, l) => s + l.sum));

class QuoteDoc {
  final String id;
  String number;
  final bool isQuote;
  String client;
  String phone;
  String inn;

  /// 2026-10-21: компания покупателя (ОсОО, ИП) — в PDF перед именем контакта.
  String company;
  List<QuoteLine> lines;
  double discount;
  DateTime until;
  String status; // КП: draft | sent | accepted | declined; счёт: issued | partial | paid (ПК ещё draft | sent)
  double paid;
  final DateTime createdAt;
  String basedOn;
  List<String> history;

  /// 2026-10-21: время последнего изменения, ISO 8601 UTC ('' — у старых документов; они «старше» любых с полем).
  String updatedAt;

  /// 2026-10-21: незнакомые поля документа (ПК: kind, clientId, address, note, vat, template, lang, by…) — не теряются.
  final Map<String, dynamic> extra;
  QuoteDoc({
    required this.id,
    required this.number,
    required this.isQuote,
    this.client = '',
    this.phone = '',
    this.inn = '',
    this.company = '',
    List<QuoteLine>? lines,
    this.discount = 0,
    required this.until,
    required this.status,
    this.paid = 0,
    required this.createdAt,
    this.basedOn = '',
    List<String>? history,
    this.updatedAt = '',
    Map<String, dynamic>? extra,
  }) : lines = lines ?? [],
       history = history ?? [],
       extra = extra ?? {};

  double get subtotal => linesSubtotal(lines);
  double get total => roundMoney((subtotal - discount).clamp(0, double.infinity).toDouble());
  double get left => roundMoney((total - paid).clamp(0, double.infinity).toDouble());

  /// 2026-10-22 (мобилка, админ): П13 — накладная программы владельца (quote: false, kind: "waybill", как ClientDoc.Kind ПК).
  /// Раньше телефон видел её как счёт: «не оплачен», «просрочен», «Принять оплату».
  bool get isWaybill => (extra['kind']?.toString() ?? '').toLowerCase() == 'waybill';

  /// Счёт на оплату: не КП и не накладная.
  bool get isInvoice => !isQuote && !isWaybill;

  /// 2026-10-22 (мобилка, админ): П13 — НДС «в том числе», % (поле vat общего списка; 0 — без НДС).
  double get vatPercent {
    final v = _n(extra['vat']);
    return v == null || v <= 0 ? 0 : v;
  }

  bool get overdue => isInvoice && status != 'paid' && until.isBefore(DateUtils.dateOnly(DateTime.now()));

  /// 2026-10-21: КП без ответа после срока действия — «Срок истёк» (серым: это не долг).
  bool get expired => isQuote && (status == 'draft' || status == 'sent') && until.isBefore(DateUtils.dateOnly(DateTime.now()));

  /// Кому: компания и контакт — для списка и PDF.
  String get buyer => [company, client].where((x) => x.trim().isNotEmpty).join(' · ');

  bool matches(String q) {
    final s = q.trim().toLowerCase();
    if (s.isEmpty) return true;
    final digits = s.replaceAll(RegExp(r'\D'), '');
    return [number, client, company, inn].any((x) => x.toLowerCase().contains(s)) ||
        (digits.length >= 3 && phone.replaceAll(RegExp(r'\D'), '').contains(digits));
  }

  static const _keys = {
    'id',
    'number',
    'quote',
    'client',
    'phone',
    'inn',
    'company',
    'lines',
    'discount',
    'until',
    'status',
    'paid',
    'created',
    'basedOn',
    'history',
    'updatedAt',
  };

  Map<String, dynamic> toJson() => {
    ..._deepCopy(extra),
    'id': id,
    'number': number,
    'quote': isQuote,
    'client': client,
    'phone': phone,
    'inn': inn,
    'company': company,
    'lines': lines.map((l) => l.toJson()).toList(),
    'discount': discount,
    'until': until.toIso8601String(),
    'status': status,
    'paid': paid,
    'created': createdAt.toIso8601String(),
    'basedOn': basedOn,
    'history': history,
    if (updatedAt.isNotEmpty) 'updatedAt': updatedAt,
  };

  static QuoteDoc fromJson(Map<String, dynamic> j) {
    // 2026-10-21: без даты — не «сейчас» (иначе каждое чтение давало бы новый документ и лишнюю отправку).
    // Документы пишут и ПК, и другие телефоны — чтение терпит число вместо строки и т. п.
    final created = DateTime.tryParse(_s(j['created'])) ?? DateTime.tryParse(_s(j['updatedAt']))?.toLocal() ?? DateTime(2026);
    return QuoteDoc(
      id: _s(j['id']),
      number: _s(j['number']),
      isQuote: j['quote'] is bool ? j['quote'] as bool : true,
      client: _s(j['client']),
      phone: _s(j['phone']),
      inn: _s(j['inn']),
      company: _s(j['company']),
      lines: [
        for (final e in (j['lines'] is List ? j['lines'] as List : const []))
          if (e is Map) QuoteLine.fromJson(e.cast<String, dynamic>()),
      ],
      discount: _n(j['discount']) ?? 0,
      until: DateTime.tryParse(_s(j['until'])) ?? DateUtils.dateOnly(created),
      status: j['status'] == null ? 'draft' : _s(j['status']),
      paid: _n(j['paid']) ?? 0,
      createdAt: created,
      basedOn: _s(j['basedOn']),
      history: [for (final e in (j['history'] is List ? j['history'] as List : const [])) e.toString()],
      updatedAt: _s(j['updatedAt']),
      extra: _deepCopy(j)..removeWhere((k, _) => _keys.contains(k)),
    );
  }
}

String _s(Object? v) => v == null ? '' : v.toString();
double? _n(Object? v) => v is num ? v.toDouble() : (v is String ? parseNum(v) : null);

Map<String, dynamic> _deepCopy(Map m) => (jsonDecode(jsonEncode(m)) as Map).cast<String, dynamic>();

// ───────────────────────────── общий список (сервер) ─────────────────────────────

/// 2026-10-21: доступ к общим настройкам компании. В тестах — фейк (сервер NurCRM не трогается).
abstract class QuotesCloud {
  /// Чьи документы: компания входа ('' — не вошли, синхронизации нет).
  String get scope;

  /// Общие настройки компании («settings» ответа GET). Сбой связи/сервера — [ApiException].
  Future<Map<String, dynamic>> fetch();

  /// PATCH только ключа «quotes»: сервер сливает верхний уровень настроек (проверено на ПК 05.10, SettingsCloudSync),
  /// остальные ключи (QR оплаты, DebtAccess, waybills, docsProfile) не меняются.
  Future<void> patchQuotes(List<Map<String, dynamic>> quotes);
}

class ApiQuotesCloud implements QuotesCloud {
  static const path = 'api/main/app-settings/nurmarket-mobile/';
  final ApiClient api;
  final Session? Function() session;
  ApiQuotesCloud(this.api, this.session);

  @override
  String get scope {
    final s = session();
    return s == null || s.access.isEmpty ? '' : s.companyName.trim().toLowerCase();
  }

  @override
  Future<Map<String, dynamic>> fetch() async {
    final data = await api.get(path, timeout: const Duration(seconds: 10));
    if (data is Map && data['settings'] is Map) return (data['settings'] as Map).cast<String, dynamic>();
    if (data is Map && data.containsKey('settings') && data['settings'] == null) return {};
    throw ApiException('Неожиданный ответ сервера (общие настройки).');
  }

  @override
  Future<void> patchQuotes(List<Map<String, dynamic>> quotes) => api.patch(path, body: {'quotes': quotes});
}

enum QuotesSyncState { localOnly, syncing, synced, pending, offline, forbidden }

class QuotesSyncResult {
  final QuotesSyncState state;

  /// Сколько документов ещё не на сервере.
  final int pending;

  /// Документы, которые позже изменили на ПК/другом телефоне — показана их версия (номера).
  final List<String> lost;

  /// Новые документы, чей номер уже заняли на другом устройстве: (было, стало).
  final List<(String, String)> renumbered;
  final String? error;
  const QuotesSyncResult(this.state, {this.pending = 0, this.lost = const [], this.renumbered = const [], this.error});
}

/// Результат слияния: список для сервера и что из своего ушло.
class QuotesMerge {
  final List<Map<String, dynamic>> list;

  /// Свои изменения, которые победили: id → updatedAt.
  final Map<String, String> sent;

  /// Свои изменения, которые проиграли (позже меняли в другом месте), и уже совпадающие с сервером.
  final Set<String> lost;
  final Set<String> same;
  final List<(String, String)> renumbered;
  QuotesMerge(this.list, this.sent, this.lost, this.same, this.renumbered);
}

bool isTombstone(Map m) => m['deleted'] == true;
DateTime? quoteUpdatedAt(Map m) => DateTime.tryParse(m['updatedAt']?.toString() ?? '')?.toUtc();

/// Свежее время изменения: ISO 8601 UTC до миллисекунд («2026-10-21T05:15:30.123Z»).
String quoteStamp([DateTime? t]) => DateTime.fromMillisecondsSinceEpoch((t ?? DateTime.now()).millisecondsSinceEpoch, isUtc: true).toIso8601String();

/// Старое поле ПК «updated»: местное время без зоны, «2026-10-21T10:15:30.123» (ClientDoc.FormatDate).
String pcLocalStamp(DateTime t) {
  String two(int v) => v.toString().padLeft(2, '0');
  return '${t.year}-${two(t.month)}-${two(t.day)}T${two(t.hour)}:${two(t.minute)}:${two(t.second)}.${t.millisecond.toString().padLeft(3, '0')}';
}

/// [a] новее [b]: документ без updatedAt старше любого с полем.
int _cmpTs(Map a, Map b) {
  final x = quoteUpdatedAt(a), y = quoteUpdatedAt(b);
  if (x == null && y == null) return 0;
  if (x == null) return -1;
  if (y == null) return 1;
  return x.compareTo(y);
}

final _numberRx = RegExp(r'^\s*(КП|СЧ|НК)\s*-\s*0*(\d{1,9})\s*$');

/// «СЧ-0012» → ('СЧ', 12), иначе null (как ClientDoc.NumberIndex на ПК).
(String, int)? parseDocNumber(String? number) {
  final m = _numberRx.firstMatch(number ?? '');
  return m == null ? null : (m.group(1)!, int.parse(m.group(2)!));
}

String formatDocNumber(String prefix, int n) => '$prefix-${n.toString().padLeft(4, '0')}';

/// 2026-10-21: слияние общего списка с изменениями телефона (как договорено с ПК):
/// • по id побеждает больший updatedAt (без поля — старше любого с полем); равные — уже на сервере;
/// • свой документ поверх серверного: {...сервер, ...своё} — поля, которых телефон не знал, остаются; надгробие — целиком;
/// • изменения телефона берутся только у [dirty] (остальное в копии телефона — просто прошлый снимок сервера);
/// • новый документ телефона с номером, который уже занят другим документом того же вида, получает следующий свободный;
/// • надгробия старше 90 дней выбрасываются.
QuotesMerge mergeQuotes(List<Map<String, dynamic>> remote, List<Map<String, dynamic>> local, Set<String> dirty, {DateTime? now, Map<String, int>? counters}) {
  final out = <String, Map<String, dynamic>>{}; // порядок сервера сохраняется
  final noId = <Map<String, dynamic>>[];
  for (final r in remote) {
    final id = r['id']?.toString() ?? '';
    if (id.isEmpty) {
      noId.add(r);
    } else {
      out.putIfAbsent(id, () => r);
    }
  }
  final sent = <String, String>{};
  final lost = <String>{};
  final same = <String>{};
  final fresh = <String>[];
  final added = <String, Map<String, dynamic>>{};
  for (final l in local) {
    final id = l['id']?.toString() ?? '';
    if (id.isEmpty || !dirty.contains(id)) continue;
    final r = out[id];
    if (r == null) {
      if (isTombstone(l)) {
        same.add(id); // удалили то, чего на сервере уже нет
        continue;
      }
      added[id] = _deepCopy(l);
      fresh.add(id);
      sent[id] = l['updatedAt']?.toString() ?? '';
      continue;
    }
    final c = _cmpTs(l, r);
    if (c > 0) {
      out[id] = isTombstone(l) || isTombstone(r) ? _deepCopy(l) : {...r, ..._deepCopy(l)};
      sent[id] = l['updatedAt']?.toString() ?? '';
    } else if (c == 0) {
      same.add(id);
    } else {
      lost.add(id);
    }
  }
  // Номера: новый документ не повторяет занятый (перепроверка перед записью).
  final renumbered = <(String, String)>[];
  if (fresh.isNotEmpty) {
    final used = <String, Set<int>>{};
    final top = <String, int>{...?counters};
    void note(Map m) {
      final p = parseDocNumber(m['number']?.toString());
      if (p == null) return;
      (used[p.$1] ??= {}).add(p.$2);
      top[p.$1] = max(top[p.$1] ?? 0, p.$2);
    }

    for (final m in out.values) {
      note(m);
    }
    for (final id in fresh) {
      final d = added[id]!;
      final p = parseDocNumber(d['number']?.toString());
      if (p != null && (used[p.$1]?.contains(p.$2) ?? false)) {
        final n = (top[p.$1] ?? 0) + 1;
        final old = d['number'].toString();
        d['number'] = formatDocNumber(p.$1, n);
        renumbered.add((old, d['number'] as String));
      }
      note(d);
    }
  }
  final cut = (now ?? DateTime.now()).toUtc().subtract(const Duration(days: 90));
  bool keep(Map m) => !isTombstone(m) || (quoteUpdatedAt(m)?.isAfter(cut) ?? false);
  final list = [
    for (final id in fresh.reversed) added[id]!,
    ...out.values.where(keep),
    ...noId,
  ];
  return QuotesMerge(list, sent, lost, same, renumbered);
}

/// Ключ «quotes» с сервера: массив или строка с JSON-массивом.
List<Map<String, dynamic>> parseRemoteQuotes(Object? node) {
  Object? v = node;
  if (v is String) {
    try {
      v = v.trim().isEmpty ? null : jsonDecode(v);
    } catch (_) {
      v = null;
    }
  }
  return v is List ? [for (final e in v) if (e is Map) e.cast<String, dynamic>()] : [];
}

/// Канонический вид документа для сравнения «изменился ли»: через модель (одинаковые значения по умолчанию), ключи по
/// алфавиту, 62 и 62.0 — одно число, без updatedAt.
String _canon(Map<String, dynamic> m) {
  Object? norm(Object? v) {
    if (v is Map) {
      final keys = v.keys.map((k) => k.toString()).toList()..sort();
      return {for (final k in keys) k: norm(v[k])};
    }
    if (v is List) return v.map(norm).toList();
    if (v is double && v == v.truncateToDouble() && v.abs() < 1e15) return v.toInt();
    return v;
  }

  if (isTombstone(m)) return 'deleted:${m['id']}';
  final j = QuoteDoc.fromJson(m).toJson()..remove('updatedAt');
  return jsonEncode(norm(j));
}

class _Mutex {
  Future<void> _last = Future.value();
  Future<T> run<T>(Future<T> Function() f) {
    final prev = _last;
    final done = Completer<void>();
    _last = done.future;
    return prev.then((_) => f()).whenComplete(done.complete);
  }
}

class QuotesStore {
  final AppDatabase db;
  final QuotesCloud? cloud;

  /// 2026-10-21 (как на ПК): через сколько после удачной записи ещё раз проверить общий список — чужая запись по старой
  /// копии могла лечь уже после нашей проверки. null — без отложенной проверки (тесты без неё).
  final Duration? recheckDelay;
  QuotesStore(this.db, {this.cloud, this.recheckDelay});

  /// Последняя запланированная отложенная проверка (для тестов).
  Future<void>? recheck;

  static const _kList = 'quotes';
  static const _kDirty = 'quotes.dirty';
  static const _kScope = 'quotes.scope';
  static const _kProfile = 'quotes.profile';
  static const _kSyncAt = 'quotes.syncAt';
  static const _kBackup = 'quotes.backup';

  // Одна база — один замок (в приложении и тестах бывает несколько QuotesStore над той же базой).
  static final _localLocks = Expando<_Mutex>();
  static final _syncLocks = Expando<_Mutex>();
  _Mutex get _local => _localLocks[db] ??= _Mutex();
  _Mutex get _syncLock => _syncLocks[db] ??= _Mutex();

  /// Последнее состояние синхронизации (для строки внизу списка).
  static final lastState = Expando<QuotesSyncResult>();
  static final _forbiddenAt = Expando<DateTime>();

  Future<List<Map<String, dynamic>>> _raw() async {
    final raw = await db.getSetting(_kList);
    if (raw == null || raw.isEmpty) return [];
    try {
      return parseRemoteQuotes(jsonDecode(raw));
    } catch (_) {
      return [];
    }
  }

  Future<Set<String>> _dirty() async {
    try {
      final v = jsonDecode(await db.getSetting(_kDirty) ?? '[]');
      return v is List ? v.map((e) => e.toString()).toSet() : {};
    } catch (_) {
      return {};
    }
  }

  Future<void> _write(List<Map<String, dynamic>> list, Set<String> dirty) async {
    await db.setSetting(_kList, jsonEncode(list));
    await db.setSetting(_kDirty, jsonEncode(dirty.toList()));
  }

  /// Документы без удалённых (надгробий).
  Future<List<QuoteDoc>> load() async => [
    for (final m in await _raw())
      if (!isTombstone(m) && (m['id']?.toString() ?? '').isNotEmpty) QuoteDoc.fromJson(m),
  ];

  /// Документы, которые ещё не ушли на сервер (без надгробий).
  Future<Set<String>> unsentIds() async {
    final dirty = await _dirty();
    if (await db.getSetting(_kScope) == null) return {for (final d in await load()) d.id};
    return dirty;
  }

  /// Записать один документ (новый или изменённый) на телефоне; на сервер — [sync]. Изменился — новое updatedAt.
  Future<void> put(QuoteDoc d) => _local.run(() async {
    final list = await _raw();
    final dirty = await _dirty();
    final i = list.indexWhere((m) => m['id']?.toString() == d.id);
    final old = i < 0 ? null : list[i];
    // Перепроверка номера у нового документа: такой номер уже есть у другого документа — следующий свободный.
    if (old == null && list.any((m) => m['id'] != d.id && !isTombstone(m) && m['number'] == d.number)) {
      final p = parseDocNumber(d.number);
      if (p != null) d.number = formatDocNumber(p.$1, await _top(p.$1, list) + 1);
    }
    var json = d.toJson();
    if (old == null || isTombstone(old) || _canon(old) != _canon(json)) {
      d.updatedAt = quoteStamp();
      // ПК пишет и старое поле updated (местное время, для прежних версий программы) — обновляем и его.
      if (d.extra.containsKey('updated')) d.extra['updated'] = pcLocalStamp(DateTime.now());
      json = d.toJson();
      dirty.add(d.id);
    } else {
      return; // ничего не поменялось — не отправлять
    }
    if (i < 0) {
      list.insert(0, json);
    } else {
      list[i] = json;
    }
    await _write(list, dirty);
  });

  /// Удалить документ: надгробие {id, deleted, updatedAt} (номер остаётся — чтобы не выдать его повторно).
  Future<void> remove(String id) => _local.run(() async {
    final list = await _raw();
    final dirty = await _dirty();
    final i = list.indexWhere((m) => m['id']?.toString() == id);
    if (i < 0 || isTombstone(list[i])) return;
    list[i] = {'id': id, 'deleted': true, 'updatedAt': quoteStamp(), if (list[i]['number'] != null) 'number': list[i]['number']};
    dirty.add(id);
    await _write(list, dirty);
  });

  /// Заменить весь список (старые вызовы и тесты): изменённые — как [put], пропавшие — как [remove].
  Future<void> save(List<QuoteDoc> docs) async {
    final ids = {for (final d in docs) d.id};
    for (final m in await _raw()) {
      final id = m['id']?.toString() ?? '';
      if (id.isNotEmpty && !ids.contains(id) && !isTombstone(m)) await remove(id);
    }
    for (final d in docs.reversed) {
      await put(d);
    }
  }

  Future<int> _top(String prefix, List<Map<String, dynamic>> list) async {
    var top = 0;
    for (final m in list) {
      final p = parseDocNumber(m['number']?.toString());
      if (p != null && p.$1 == prefix) top = max(top, p.$2);
    }
    top = max(top, (await _counters())[prefix] ?? 0);
    top = max(top, int.tryParse(await db.getSetting(prefix == 'КП' ? 'quotes.counter.kp' : 'quotes.counter.inv') ?? '') ?? 0);
    return top;
  }

  /// КП-0001 / СЧ-0001 — свой счётчик у каждого вида.
  /// 2026-10-21: номер — больше всех занятых в общем списке (свои, ПК, удалённые), счётчиков ПК (docsProfile.counters kp/inv)
  /// и счётчика телефона. Перед записью на сервер номер нового документа ещё раз сверяется с самым свежим списком.
  Future<String> nextNumber(bool quote) async {
    final key = quote ? 'quotes.counter.kp' : 'quotes.counter.inv';
    final prefix = quote ? 'КП' : 'СЧ';
    final n = await _top(prefix, await _raw()) + 1;
    await db.setSetting(key, '$n');
    return formatDocNumber(prefix, n);
  }

  Future<Map<String, dynamic>?> _profile() async {
    try {
      final v = jsonDecode(await db.getSetting(_kProfile) ?? 'null');
      return v is Map ? v.cast<String, dynamic>() : null;
    } catch (_) {
      return null;
    }
  }

  /// 2026-10-21: реквизиты магазина для счёта (банк, р/с, БИК, телефон) — свои на телефоне; не заданы — из реквизитов
  /// программы владельца (docsProfile: bank, account, bik, phone).
  Future<String> requisites() async {
    final own = await db.getSetting('quotes.requisites') ?? '';
    if (own.trim().isNotEmpty) return own;
    final p = await _profile();
    if (p == null) return '';
    String s(String k) => p[k]?.toString().trim() ?? '';
    return [
      s('bank'),
      if (s('account').isNotEmpty) 'р/с ${s('account')}',
      if (s('bik').isNotEmpty) 'БИК ${s('bik')}',
      if (s('phone').isNotEmpty) 'Тел. ${s('phone')}',
    ].where((x) => x.isNotEmpty).join('\n');
  }

  Future<void> setRequisites(String text) => db.setSetting('quotes.requisites', text.trim());

  /// 2026-10-22 (мобилка, админ): П13 — НДС по умолчанию для новых документов из реквизитов программы владельца
  /// (docsProfile.vat, как ClientDocProfile.VatPercent); 0 — без НДС.
  Future<double> defaultVat() async {
    final v = _n((await _profile())?['vat']);
    return v == null || v <= 0 ? 0 : v;
  }

  /// Время последней удачной синхронизации.
  Future<DateTime?> syncedAt() async => DateTime.tryParse(await db.getSetting(_kSyncAt) ?? '')?.toLocal();

  /// 2026-10-21: синхронизация с общим списком (как на ПК): свежее чтение → слияние по id (побеждает больший updatedAt,
  /// чужие документы не трогаются, незнакомые поля сохраняются) → PATCH только ключа «quotes» → перечитать: если нашу
  /// запись затёрли (одновременная запись ПК) — слить ещё раз, до 3 попыток. Без связи — всё остаётся на телефоне
  /// с пометкой «не отправлено» и уходит при следующей синхронизации (открытие раздела, «потянуть вниз», фоновая досылка).
  /// [onlyIfDirty] — фоновая досылка: без своих изменений сервер не спрашивается.
  Future<QuotesSyncResult> sync({bool onlyIfDirty = false}) => _syncLock.run(() async {
    final res = await _sync(onlyIfDirty, repair: false);
    lastState[db] = res;
    return res;
  });

  Future<QuotesSyncResult> _sync(bool onlyIfDirty, {required bool repair}) async {
    final c = cloud;
    final scope = c?.scope ?? '';
    if (c == null || scope.isEmpty) return QuotesSyncResult(QuotesSyncState.localOnly, pending: (await unsentIds()).length);
    // Снимок телефона.
    final (local0, dirty0) = await _local.run(() async {
      final stored = await db.getSetting(_kScope);
      if (stored == null) {
        // Документы, созданные до общего списка, — все «не отправлены»; без времени изменения — сейчас.
        final now = quoteStamp();
        final list = [for (final m in await _raw()) m['updatedAt'] == null ? {...m, 'updatedAt': now} : m];
        await _write(list, {...await _dirty(), for (final m in list) m['id']?.toString() ?? ''}..remove(''));
        await db.setSetting(_kScope, scope);
      } else if (stored != scope) {
        // Вошли в другую компанию: документы прошлой ей не отправляем (копия — в quotes.backup).
        await db.setSetting(_kBackup, jsonEncode({'scope': stored, 'quotes': await _raw(), 'dirty': (await _dirty()).toList()}));
        await _write([], {});
        await db.setSetting(_kScope, scope);
        await db.setSetting(_kSyncAt, '');
      }
      return (await _raw(), await _dirty());
    });
    if (onlyIfDirty && dirty0.isEmpty) return QuotesSyncResult(QuotesSyncState.synced);
    // Кассир без прав на общие настройки: фоновая досылка не стучится на сервер чаще раза в 10 минут.
    if (onlyIfDirty && lastState[db]?.state == QuotesSyncState.forbidden && DateTime.now().difference(_forbiddenAt[db] ?? DateTime(2000)).inMinutes < 10) {
      return lastState[db]!;
    }

    List<Map<String, dynamic>> remote;
    try {
      final settings = await c.fetch();
      _forbiddenAt[db] = null;
      remote = parseRemoteQuotes(settings['quotes']);
      if (settings['docsProfile'] is Map) await db.setSetting(_kProfile, jsonEncode(settings['docsProfile']));
    } on ApiException catch (e) {
      return QuotesSyncResult(e.status == 401 || e.status == 403 ? QuotesSyncState.forbidden : QuotesSyncState.offline, pending: dirty0.length, error: e.message);
    }
    final counters = await _counters();
    final confirmed = <String>{};
    final lost = <String>{};
    final renumbered = <(String, String)>[];
    final written = <String, Map<String, dynamic>>{}; // что записали и подтвердили — для отложенной проверки
    ApiException? pushError;
    var work = local0;
    for (var attempt = 0; attempt < 3; attempt++) {
      final m = mergeQuotes(remote, work, dirty0.difference(confirmed), counters: counters);
      confirmed.addAll(m.same);
      lost.addAll(m.lost);
      if (m.renumbered.isNotEmpty) {
        renumbered.addAll(m.renumbered);
        // Новый номер — и в копии телефона, чтобы следующая попытка не меняла его ещё раз.
        work = [
          for (final x in work) m.list.firstWhere((y) => y['id'] == x['id'] && m.sent.containsKey(x['id']), orElse: () => x),
        ];
      }
      if (m.sent.isEmpty) break;
      try {
        await c.patchQuotes(m.list);
        remote = parseRemoteQuotes((await c.fetch())['quotes']);
      } on ApiException catch (e) {
        pushError = e;
        break;
      }
      // Проверка: наши записи на месте (или там ещё новее)? Иначе — затёрли одновременной записью, сливаем снова.
      final byId = {for (final r in remote) r['id']?.toString(): r};
      for (final e in m.sent.entries) {
        final r = byId[e.key];
        final mine = DateTime.tryParse(e.value)?.toUtc();
        final theirs = r == null ? null : quoteUpdatedAt(r);
        final tomb = isTombstone(work.firstWhere((x) => x['id'] == e.key, orElse: () => const {}));
        if ((r == null && tomb) || (theirs != null && mine != null && !theirs.isBefore(mine))) confirmed.add(e.key);
      }
      for (final id in m.sent.keys) {
        if (confirmed.contains(id)) written[id] = m.list.firstWhere((x) => x['id'] == id);
      }
      if (confirmed.containsAll(m.sent.keys)) break;
    }
    // Что показать: сервер + свои неотправленные.
    final view = mergeQuotes(remote, work, dirty0.difference(confirmed).difference(lost), counters: counters).list;
    // Одна отложенная проверка на запись; запись-исправление своей проверки не заводит — без бесконечных повторов.
    final delay = recheckDelay;
    if (!repair && delay != null && written.isNotEmpty) recheck = Future.delayed(delay, () => _recheck(written));
    return _local.run(() async {
      final local1 = await _raw();
      final dirty1 = await _dirty();
      final byId0 = {for (final m in local0) m['id']?.toString(): m};
      // Пока шёл запрос, документ могли изменить ещё раз — эту версию не терять, она остаётся «не отправлено».
      final changed = <String>{};
      for (final m in local1) {
        final id = m['id']?.toString() ?? '';
        if (!dirty1.contains(id)) continue;
        final o = byId0[id];
        if (o == null || m['updatedAt'] != o['updatedAt']) changed.add(id);
      }
      final out = [
        for (final m in local1)
          if (changed.contains(m['id']) && !view.any((v) => v['id'] == m['id'])) m,
        for (final v in view) changed.contains(v['id']) ? local1.firstWhere((m) => m['id'] == v['id']) : v,
      ];
      final dirty = {...dirty0, ...dirty1}.difference(confirmed).difference(lost)..addAll(changed);
      dirty.removeWhere((id) => !out.any((m) => m['id'] == id));
      await _write(out, dirty);
      if (pushError == null && dirty.isEmpty) await db.setSetting(_kSyncAt, DateTime.now().toUtc().toIso8601String());
      final byNum = {for (final m in local0) m['id']?.toString(): m['number']?.toString() ?? ''};
      if (pushError?.status == 401 || pushError?.status == 403) _forbiddenAt[db] = DateTime.now();
      final state = dirty.isEmpty
          ? QuotesSyncState.synced
          : (pushError != null && (pushError.status == 401 || pushError.status == 403)
                ? QuotesSyncState.forbidden
                : (pushError != null ? QuotesSyncState.offline : QuotesSyncState.pending));
      return QuotesSyncResult(state, pending: dirty.length, lost: [for (final id in lost) byNum[id] ?? id], renumbered: renumbered, error: pushError?.message);
    });
  }

  /// 2026-10-21: отложенная проверка после записи (как на ПК): наш документ пропал без надгробия или на сервере версия
  /// старее нашей — значит, его затёрли записью по старой копии; возвращаем нашу версию (если на телефоне нет новее) и
  /// пишем ещё раз. Без связи — ничего (следующая обычная синхронизация).
  Future<void> _recheck(Map<String, Map<String, dynamic>> written) async {
    final c = cloud;
    if (c == null || c.scope.isEmpty) return;
    try {
      final remote = parseRemoteQuotes((await c.fetch())['quotes']);
      final byId = {for (final r in remote) r['id']?.toString(): r};
      final fix = [
        for (final e in written.entries)
          if (byId[e.key] == null ? !isTombstone(e.value) : _cmpTs(byId[e.key]!, e.value) < 0) e.value,
      ];
      if (fix.isEmpty) return;
      await _local.run(() async {
        final list = await _raw();
        final dirty = await _dirty();
        for (final w in fix) {
          final i = list.indexWhere((m) => m['id'] == w['id']);
          if (i < 0) {
            list.insert(0, w);
          } else if (_cmpTs(list[i], w) <= 0) {
            list[i] = w;
          } else {
            continue; // на телефоне уже новее — уйдёт обычной синхронизацией
          }
          dirty.add(w['id'].toString());
        }
        await _write(list, dirty);
      });
      await _syncLock.run(() async {
        final res = await _sync(false, repair: true);
        lastState[db] = res;
      });
    } on ApiException {
      // нет связи — проверит следующая синхронизация
    }
  }

  Future<Map<String, int>> _counters() async {
    final p = await _profile();
    final c = p?['counters'];
    if (c is! Map) return {};
    return {
      if (c['kp'] is num) 'КП': (c['kp'] as num).toInt(),
      if (c['inv'] is num) 'СЧ': (c['inv'] as num).toInt(),
      if (c['wb'] is num) 'НК': (c['wb'] as num).toInt(),
    };
  }
}

final quotesStoreProvider = Provider(
  (ref) => QuotesStore(
    ref.watch(databaseProvider),
    cloud: ApiQuotesCloud(ref.watch(apiProvider), () => ref.read(sessionProvider)),
    recheckDelay: const Duration(seconds: 4),
  ),
);
