import 'dart:convert';

import 'dart:async';

import 'package:drift/drift.dart' show TableUpdate, TableUpdateQuery, Value, Variable;

import '../api/session_store.dart';
import '../db/database.dart';

/// 2026-10-22 (мобилка, деньги): служебные поля записи очереди — кто её создал (аккаунт), на какой кассе и когда.
/// Хранятся в теле записи под ключом `_meta` (схема базы не меняется) и серверу не отправляются (stripMeta).
/// Аудит, К-6: чеки прошлого аккаунта уходили под новым входом — теперь очередь отправляет только записи
/// текущего аккаунта; чужие видны на экране «Синхронизация» и ждут входа своим логином.
class OutboxMeta {
  static const key = '_meta';

  /// Настройка: аккаунт, которому принадлежат записи очереди без `_meta` (созданные до 22.10).
  static const legacyOwnerSetting = 'outbox.legacyOwner';

  /// Аккаунт — логин (почта/телефон) в нижнем регистре: у NurCRM логин один на пользователя, пользователь — в одной компании.
  static String ownerOf(Session? s) => s == null ? '' : s.email.trim().toLowerCase();

  static Map<String, dynamic> attach(Map<String, dynamic> body, {required String owner, String? cashboxId, DateTime? at, Map<String, double>? packs}) => {
    ...body,
    key: {
      'owner': owner,
      if (cashboxId != null && cashboxId.isNotEmpty) 'cashbox': cashboxId,
      'at': (at ?? DateTime.now()).toIso8601String(),
      // 2026-10-27 (М4): штук в упаковке по sale_package_id — остаток телефона для штучных строк (серверу не уходит, см. strip)
      if (packs != null && packs.isNotEmpty) 'packs': packs,
    },
  };

  /// 2026-10-27 (М4): сколько строка чека списывает с остатка товара (в упаковках): штуки из упаковки (sale_package_id) — qty ÷ штук
  /// в упаковке по метке `packs` записи, остальные строки — qty как раньше.
  static double stockQty(Map item, Map<String, dynamic>? meta) {
    final q = double.tryParse('${item['qty'] ?? ''}') ?? 0;
    final pid = item['sale_package_id']?.toString();
    if (pid == null || pid.isEmpty) return q;
    final packs = meta?['packs'];
    final per = packs is Map ? double.tryParse('${packs[pid] ?? ''}') ?? 0 : 0;
    return per > 1 ? q / per : q;
  }

  /// Тело для сервера — без служебных полей.
  static Map<String, dynamic> strip(Map<String, dynamic> body) => {
    for (final e in body.entries)
      if (e.key != key) e.key: e.value,
  };

  static Map<String, dynamic>? read(String rawBody) {
    try {
      final m = jsonDecode(rawBody);
      if (m is Map && m[key] is Map) return (m[key] as Map).cast<String, dynamic>();
    } catch (_) {
      // повреждённое тело — без служебных полей
    }
    return null;
  }

  /// Чей это чек/приход. null — запись до 22.10 (без `_meta`).
  static String? ownerOfRow(OutboxData row) {
    final o = read(row.body)?['owner'];
    return o is String && o.isNotEmpty ? o : null;
  }

  /// 2026-10-22 (мобилка, деньги): «Снять» не принятую сервером запись — она больше не отправляется и не мешает
  /// закрыть смену (статус dropped, запись остаётся в базе для истории). У чека товар возвращается в остаток телефона.
  static const dropped = 'dropped';

  static Future<void> drop(AppDatabase db, OutboxData row) async {
    if (row.kind == 'sale' && row.status != 'sent') {
      try {
        final body = (jsonDecode(row.body) as Map).cast<String, dynamic>();
        final meta = read(row.body);
        for (final it in (body['items'] as List? ?? const [])) {
          if (it is! Map) continue;
          final id = it['product']?.toString();
          final q = stockQty(it, meta);
          if (id != null && q > 0) await db.adjustStock(id, q);
          // 2026-10-31 (одежда): снятый чек возвращает и остаток размера
          final vid = it['variant']?.toString();
          if (vid != null && vid.isNotEmpty && q > 0) await db.adjustVariantStock(vid, q);
        }
      } catch (_) {
        // без поправки остатка
      }
    }
    await (db.update(db.outbox)..where((o) => o.id.equals(row.id))).write(const OutboxCompanion(status: Value(dropped)));
  }

  /// Запись можно отправлять под этим входом. Старая запись без владельца — аккаунта, который был на телефоне при
  /// обновлении (legacyOwner); не известен — по-старому, под текущим входом.
  static bool belongsTo(OutboxData row, String owner, {String? legacyOwner}) {
    if (owner.isEmpty) return false;
    final o = ownerOfRow(row) ?? ((legacyOwner == null || legacyOwner.isEmpty) ? owner : legacyOwner);
    return o == owner;
  }
}

/// 2026-11-01 (аудит 10 из 10, М-07): счёт записей очереди этого входа средствами SQL (JSON1), без чтения и разбора тел чеков.
/// Раньше `pendingCountProvider` и `hasOwnWork` на каждую запись очереди разбирали JSON всех ждущих тел в основном изоляте
/// (5000 записей ≈ 6 МБ и ≈ 77 мс за проход, всё это — на каждую отправленную). Правило владельца — как [OutboxMeta.belongsTo].
extension OutboxCounts on AppDatabase {
  static const _sql =
      "SELECT COUNT(*) AS c FROM outbox WHERE status = 'pending' "
      "AND COALESCE(NULLIF(json_extract(body, '\$._meta.owner'), ''), ?) = ?";

  /// Сколько ждущих отправки записей у входа [me]. Записи без `_meta` (до 22.10) принадлежат [legacyOwner] или текущему входу.
  Future<int> pendingCountFor(String me, {String? legacyOwner}) async {
    if (me.isEmpty) return 0;
    final eff = (legacyOwner == null || legacyOwner.isEmpty) ? me : legacyOwner;
    try {
      final r = await customSelect(_sql, variables: [Variable.withString(eff), Variable.withString(me)], readsFrom: {outbox}).getSingle();
      return r.read<int>('c');
    } catch (_) {
      // JSON1 нет или тело повреждено — старый путь (разбор тел)
      return (await pending()).where((r) => OutboxMeta.belongsTo(r, me, legacyOwner: legacyOwner)).length;
    }
  }

  /// То же, но потоком (обновляется при записи в очередь, не чаще раза в 0,7 с — приёмка, Н-10: на каждую отправленную запись
  /// большой очереди счёт не пересчитывается; запасной путь [pendingCountFor] работает и в потоке).
  Stream<int> watchPendingCountFor(String me, {String? legacyOwner}) => _countStream(this, () => pendingCountFor(me, legacyOwner: legacyOwner));

  static const _staleSql =
      "SELECT COUNT(*) AS c FROM outbox WHERE kind = 'sale' AND status IN ('pending', 'failed') AND created_at < ? "
      "AND COALESCE(NULLIF(json_extract(body, '\$._meta.owner'), ''), ?) = ?";

  /// 2026-11-01 (приёмка, Н-9): сколько чеков этого входа ждут отправки или не приняты и старше [age] — по ВСЕЙ очереди, а не по последним
  /// записям на экране (плашка «чеки старше 5 суток»).
  Future<int> staleSalesCountFor(String me, {String? legacyOwner, Duration age = const Duration(days: 5)}) async {
    if (me.isEmpty) return 0;
    final eff = (legacyOwner == null || legacyOwner.isEmpty) ? me : legacyOwner;
    final cutoff = DateTime.now().subtract(age);
    try {
      final r = await customSelect(
        _staleSql,
        variables: [Variable.withInt(cutoff.millisecondsSinceEpoch ~/ 1000), Variable.withString(eff), Variable.withString(me)],
        readsFrom: {outbox},
      ).getSingle();
      return r.read<int>('c');
    } catch (_) {
      final rows = await (select(outbox)..where((o) => o.kind.equals('sale'))..where((o) => o.status.isIn(const ['pending', 'failed']))).get();
      return rows.where((r) => r.createdAt.isBefore(cutoff) && OutboxMeta.belongsTo(r, me, legacyOwner: legacyOwner)).length;
    }
  }

  Stream<int> watchStaleSalesFor(String me, {String? legacyOwner}) => _countStream(this, () => staleSalesCountFor(me, legacyOwner: legacyOwner));
}

/// Поток числа: первое значение сразу, дальше — после записей в очередь, но не чаще раза в 0,7 с (записи за паузу сливаются в один пересчёт).
Stream<int> _countStream(AppDatabase db, Future<int> Function() count) {
  late final StreamController<int> c;
  StreamSubscription<Set<TableUpdate>>? sub;
  Timer? t;
  Future<void> emit() async {
    t = null;
    try {
      final v = await count();
      if (!c.isClosed) c.add(v);
    } catch (e) {
      if (!c.isClosed) c.addError(e);
    }
  }

  c = StreamController<int>(
    onListen: () {
      unawaited(emit());
      sub = db.tableUpdates(TableUpdateQuery.onTable(db.outbox)).listen((_) => t ??= Timer(const Duration(milliseconds: 700), () => unawaited(emit())));
    },
    onCancel: () {
      t?.cancel();
      return sub?.cancel();
    },
  );
  return c.stream;
}
