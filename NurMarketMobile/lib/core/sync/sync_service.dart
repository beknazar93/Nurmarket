import 'dart:async';

import '../../features/payment/sales_repository.dart';
import '../../features/sale/catalog_repository.dart';
import '../../features/stock/stock_repository.dart';
import '../api/api_client.dart';
import '../db/database.dart';

/// Досылка очереди по порядку при связи и обновление каталога раз в 5 минут (ТЗ, раздел 8).
class SyncService {
  final AppDatabase db;
  final SalesRepository sales;
  final StockRepository stock;
  final CatalogRepository catalog;
  Timer? _timer;
  bool _running = false;
  DateTime _lastCatalog = DateTime.fromMillisecondsSinceEpoch(0);

  /// 2026-10-21: досылка КП и счетов, изменённых без связи, в общий список с программой владельца (quotes_store.dart).
  final Future<void> Function()? quotes;

  /// Последняя ошибка связи — для экрана «Синхронизация».
  String? lastError;

  SyncService({required this.db, required this.sales, required this.stock, required this.catalog, this.quotes});

  void start() {
    _timer?.cancel();
    _timer = Timer.periodic(const Duration(seconds: 20), (_) => runOnce());
    runOnce();
  }

  void stop() => _timer?.cancel();

  Future<void> runOnce({bool force = false}) async {
    if (_running) return;
    _running = true;
    try {
      for (final row in await db.pending()) {
        try {
          switch (row.kind) {
            case 'sale':
              final resp = await sales.replay(row);
              await db.markSent(row.id, serverId: str(resp, 'id'), serverNumber: str(resp, 'number'));
            case 'receive':
              await stock.replayReceiving(row);
            case 'audit':
              await stock.replayAudit(row);
          }
          lastError = null;
        } on ApiException catch (e) {
          lastError = e.message;
          // Отказ по существу — в «Не отправлено» с причиной; сбой связи — остановка до следующего раза, порядок не нарушаем.
          await db.markAttempt(row.id, e.message, failed: !e.isTransient);
          if (e.isTransient) break;
        }
      }
      try {
        await quotes?.call();
      } catch (_) {
        // КП подождут следующего раза — очередь чеков важнее
      }
      if (force || DateTime.now().difference(_lastCatalog) > const Duration(minutes: 5)) {
        try {
          await catalog.sync();
          _lastCatalog = DateTime.now();
        } on ApiException catch (e) {
          lastError = e.message;
        }
      }
    } finally {
      _running = false;
    }
  }
}
