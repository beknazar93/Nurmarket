import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:uuid/uuid.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/sync/outbox_meta.dart';

class StockLine {
  final String productId;
  final String name;
  final double before;
  double qty;
  StockLine({required this.productId, required this.name, required this.before, this.qty = 1});

  Map<String, dynamic> toJson() => {'productId': productId, 'name': name, 'qty': qty, 'done': false};
}

/// 2026-10-22 (мобилка, деньги, К-7): исход прибавки остатка неясен — прошлый PATCH остался без ответа, а остаток
/// с тех пор изменился. Повторно не прибавляем (как PendingStockPatch кассы), запись — «Не принято» с подсказкой.
class StockUnclearException extends ApiException {
  StockUnclearException(super.message) : super(status: 409);
  @override
  bool get isTransient => false;
}

/// Склад кассира (Т7, Т16, Т17): приёмка сканером без цен и ревизия.
class StockRepository {
  final ApiClient api;
  final AppDatabase db;
  StockRepository(this.api, this.db);

  /// 2026-10-22 (мобилка, деньги, К-7): записи, которые сейчас отправляются. Экран и фоновая очередь брали одну запись
  /// одновременно — остаток прибавлялся дважды. Второй вызов теперь ждёт первый и видит запись уже отправленной.
  final _inFlight = <String, Future<void>>{};

  Future<void> _once(String id, Future<void> Function() run) async {
    final busy = _inFlight[id];
    if (busy != null) {
      await busy.catchError((_) {});
      return;
    }
    final f = run();
    _inFlight[id] = f;
    try {
      await f;
    } finally {
      _inFlight.remove(id);
    }
  }

  Future<OutboxData?> _row(String id) => (db.select(db.outbox)..where((o) => o.id.equals(id))).getSingleOrNull();

  Future<void> _saveBody(OutboxData row, Map<String, dynamic> body) =>
      (db.update(db.outbox)..where((o) => o.id.equals(row.id))).write(OutboxCompanion(body: Value(jsonEncode(body))));

  Map<String, dynamic> _meta() => {'owner': OutboxMeta.ownerOf(api.session), 'cashbox': api.session?.cashboxId, 'at': DateTime.now().toIso8601String()};

  /// Приёмка без поставщика — как у кассы и сайта: остаток пишется в сам товар, свежий с сервера + принятое.
  /// Строки отмечаются по одной, поэтому повтор после обрыва не прибавит принятое дважды.
  Future<bool> postReceiving(List<StockLine> lines) async {
    final key = const Uuid().v4();
    final body = {
      'lines': lines.map((l) => l.toJson()).toList(),
      OutboxMeta.key: _meta(),
    };
    await db.enqueue(
      OutboxCompanion.insert(
        id: key,
        kind: 'receive',
        body: jsonEncode(body),
        createdAt: DateTime.now(),
        total: Value(lines.fold<double>(0, (s, l) => s + l.qty)),
        summary: Value(lines.take(3).map((l) => l.name).join(', ')),
      ),
    );
    for (final l in lines) {
      await db.adjustStock(l.productId, l.qty);
    }
    final row = await _row(key);
    if (row == null || row.status != 'pending') return true;
    try {
      await replayReceiving(row);
      // запись могла отправлять фоновая очередь — смотрим, чем кончилось
      return (await _row(key))?.status == 'sent';
    } on ApiException catch (e) {
      if (!e.isTransient) {
        await db.markAttempt(key, e.message, failed: true);
        rethrow;
      }
      await db.markAttempt(key, e.message);
      return false; // уйдёт при связи
    }
  }

  /// 2026-10-22 (мобилка, деньги, К-7): у строки запоминаем остаток ДО прибавки (pre) и записываемый (target), как
  /// PendingStockPatch кассы. Повтор после потерянного ответа: свежий = target — PATCH дошёл, не прибавляем; = pre — не
  /// дошёл, пишем target; иначе исход не понять — НЕ прибавляем, строка «нужна проверка».
  Future<void> replayReceiving(OutboxData row) => _once(row.id, () async {
    final fresh = await _row(row.id);
    if (fresh == null || fresh.status == 'sent') return;
    final body = (jsonDecode(fresh.body) as Map).cast<String, dynamic>();
    final lines = (body['lines'] as List).cast<Map>().map((e) => e.cast<String, dynamic>()).toList();
    body['lines'] = lines;
    final unclear = <String>[];
    for (final l in lines) {
      if (l['done'] == true) continue;
      final id = l['productId'] as String;
      final current = ((await api.get('api/main/products/$id/')) as Map).cast<String, dynamic>();
      final stock = num0(current, 'quantity');
      final qty = (l['qty'] as num).toDouble();
      var pre = stock;
      final pending = l['target'];
      if (pending is num) {
        final prevPre = (l['pre'] as num?)?.toDouble() ?? stock;
        if ((stock - pending.toDouble()).abs() < 0.0005) {
          // прошлый PATCH дошёл — ответ потерялся
          l['done'] = true;
          await db.setStock(id, stock);
          await _saveBody(fresh, body);
          continue;
        }
        if ((stock - prevPre).abs() >= 0.0005) {
          unclear.add('${l['name'] ?? id}: ${_fmt(prevPre)} → ${_fmt(pending.toDouble())}, сейчас ${_fmt(stock)}');
          continue;
        }
        pre = prevPre;
      }
      final updated = ((pre + qty) * 1000).round() / 1000;
      l['pre'] = pre;
      l['target'] = updated;
      await _saveBody(fresh, body); // до PATCH: потеря ответа не приведёт ко второй прибавке
      try {
        await api.patch('api/main/products/$id/', body: {'quantity': updated});
      } on ApiException catch (e) {
        if (!e.isTransient) {
          // отказ по существу — остаток не записан
          l.remove('target');
          l.remove('pre');
          await _saveBody(fresh, body);
        }
        rethrow;
      }
      await db.setStock(id, updated);
      l['done'] = true;
      l.remove('target');
      await _saveBody(fresh, body);
    }
    if (unclear.isNotEmpty) {
      throw StockUnclearException('Ответ сервера потерялся, а остаток с тех пор изменился — не прибавлено, проверьте остаток: ${unclear.join('; ')}');
    }
    await db.markSent(row.id);
  });

  static String _fmt(double v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(3);

  /// 2026-10-22 (мобилка, деньги, К-10): ревизия кассира проводится, как на ПК (WarehouseViewModel): акт
  /// инвентаризации (POST api/main/inventory/sessions/) и сразу проведение (POST …/{id}/apply/, allow_negative: false) —
  /// сервер сам считает разницу с остатком. Раньше уходил только черновик «администратору», а провести его было негде.
  /// Только при связи: пересчёт, проведённый часами позже, стёр бы продажи между пересчётом и отправкой.
  /// id созданного акта сохраняется в записи — повтор после обрыва не создаёт второй акт и не проводит его дважды.
  /// Возврат: true — проведено; false — акт создан, проведение ждёт связи (уйдёт само).
  Future<bool> applyAudit(String note, Map<String, double> counted) async {
    final key = const Uuid().v4();
    final body = {
      'note': note,
      'items': counted.entries.map((e) => {'product_id': e.key, 'quantity_fact': e.value.toStringAsFixed(2)}).toList(),
      OutboxMeta.key: {..._meta(), 'apply': true},
    };
    await db.enqueue(
      OutboxCompanion.insert(
        id: key,
        kind: 'audit',
        body: jsonEncode(body),
        createdAt: DateTime.now(),
        total: Value(counted.length.toDouble()),
        summary: Value(note),
      ),
    );
    final row = (await _row(key))!;
    try {
      await replayAudit(row);
      if ((await _row(key))?.status != 'sent') return false;
      for (final e in counted.entries) {
        await db.setStock(e.key, e.value);
      }
      return true;
    } on ApiException catch (e) {
      final now = await _row(key);
      final created = now != null && OutboxMeta.read(now.body)?['session'] is String;
      if (e.isTransient && created) {
        await db.markAttempt(key, e.message);
        return false; // акт есть, проведение — при связи
      }
      // Акт не создан (нет связи или отказ) — запись убираем, пересчёт остаётся на экране.
      await (db.delete(db.outbox)..where((o) => o.id.equals(key))).go();
      rethrow;
    }
  }

  Future<void> replayAudit(OutboxData row) => _once(row.id, () async {
    final fresh = await _row(row.id);
    if (fresh == null || fresh.status == 'sent') return;
    final raw = (jsonDecode(fresh.body) as Map).cast<String, dynamic>();
    final meta = (raw[OutboxMeta.key] is Map ? (raw[OutboxMeta.key] as Map).cast<String, dynamic>() : <String, dynamic>{});
    var sessionId = meta['session'] as String?;
    if (sessionId == null) {
      final resp = await api.post('api/main/inventory/sessions/', body: OutboxMeta.strip(raw));
      sessionId = resp is Map ? resp['id']?.toString() : null;
      if (sessionId == null || sessionId.isEmpty) throw ApiException('Сервер не вернул номер акта ревизии.', status: 502);
      meta['session'] = sessionId;
      raw[OutboxMeta.key] = meta;
      await _saveBody(fresh, raw);
    }
    // Запись до 22.10 — только черновик (как было).
    if (meta['apply'] == true) {
      try {
        meta['applyTried'] = true;
        raw[OutboxMeta.key] = meta;
        await _saveBody(fresh, raw);
        await api.post('api/main/inventory/sessions/$sessionId/apply/', body: {'allow_negative': false});
      } on ApiException catch (e) {
        // Прошлое проведение могло дойти без ответа — сервер отвечает «уже проведён»: проверяем статус акта.
        if (e.isTransient || !(await _sessionApplied(sessionId))) rethrow;
      }
    }
    await db.markSent(row.id, serverId: sessionId);
  });

  Future<bool> _sessionApplied(String id) async {
    try {
      final s = await api.get('api/main/inventory/sessions/$id/');
      if (s is! Map) return false;
      final st = '${s['status'] ?? ''}'.toLowerCase();
      return s['is_applied'] == true || s['applied_at'] != null || const {'applied', 'done', 'completed', 'closed', 'posted'}.contains(st);
    } catch (_) {
      return false;
    }
  }
}
