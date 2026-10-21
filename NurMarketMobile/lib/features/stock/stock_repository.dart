import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:uuid/uuid.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';

class StockLine {
  final String productId;
  final String name;
  final double before;
  double qty;
  StockLine({required this.productId, required this.name, required this.before, this.qty = 1});

  Map<String, dynamic> toJson() => {'productId': productId, 'name': name, 'qty': qty, 'done': false};
}

/// Склад кассира (Т7, Т16, Т17): приёмка сканером без цен и ревизия.
class StockRepository {
  final ApiClient api;
  final AppDatabase db;
  StockRepository(this.api, this.db);

  /// Приёмка без поставщика — как у кассы и сайта: остаток пишется в сам товар, свежий с сервера + принятое.
  /// Строки отмечаются по одной, поэтому повтор после обрыва не прибавит принятое дважды.
  Future<bool> postReceiving(List<StockLine> lines) async {
    final key = const Uuid().v4();
    final body = {'lines': lines.map((l) => l.toJson()).toList()};
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
    final row = (await db.pending()).where((r) => r.id == key).firstOrNull;
    if (row == null) return true;
    try {
      await replayReceiving(row);
      return true;
    } on ApiException catch (e) {
      if (!e.isTransient) rethrow;
      return false; // уйдёт при связи
    }
  }

  Future<void> replayReceiving(OutboxData row) async {
    final body = (jsonDecode(row.body) as Map).cast<String, dynamic>();
    final lines = (body['lines'] as List).cast<Map>().map((e) => e.cast<String, dynamic>()).toList();
    for (final l in lines) {
      if (l['done'] == true) continue;
      final id = l['productId'] as String;
      final current = ((await api.get('api/main/products/$id/')) as Map).cast<String, dynamic>();
      final stock = num0(current, 'quantity');
      final qty = (l['qty'] as num).toDouble();
      final updated = ((stock + qty) * 1000).round() / 1000;
      await api.patch('api/main/products/$id/', body: {'quantity': updated});
      await db.setStock(id, updated);
      l['done'] = true;
      await db.enqueue(OutboxCompanion(id: Value(row.id), kind: Value(row.kind), body: Value(jsonEncode({'lines': lines})), createdAt: Value(row.createdAt)));
    }
    await db.markSent(row.id);
  }

  /// Ревизия кассира — «Отправить администратору»: сессия инвентаризации без применения
  /// (POST api/main/inventory/sessions/); остатки меняет администратор после проверки.
  Future<bool> sendAudit(String note, Map<String, double> counted) async {
    final key = const Uuid().v4();
    final body = {
      'note': note,
      'items': counted.entries.map((e) => {'product_id': e.key, 'quantity_fact': e.value.toStringAsFixed(2)}).toList(),
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
    final row = (await db.pending()).where((r) => r.id == key).firstOrNull;
    if (row == null) return true;
    try {
      await replayAudit(row);
      return true;
    } on ApiException catch (e) {
      if (!e.isTransient) rethrow;
      return false;
    }
  }

  Future<void> replayAudit(OutboxData row) async {
    final resp = await api.post('api/main/inventory/sessions/', body: jsonDecode(row.body));
    await db.markSent(row.id, serverId: resp is Map ? resp['id']?.toString() : null);
  }
}
