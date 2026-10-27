import 'dart:convert';

import 'package:drift/drift.dart' show Value;

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
