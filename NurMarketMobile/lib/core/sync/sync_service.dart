import 'dart:async';

import '../../features/payment/sales_repository.dart';
import '../../features/sale/catalog_repository.dart';
import '../../features/stock/stock_repository.dart';
import '../api/api_client.dart';
import '../db/database.dart';
import 'outbox_meta.dart';

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

  /// 2026-10-22 (мобилка, деньги, К-6): аккаунт текущего входа — отправляются только его записи.
  final String Function() owner;

  /// Последняя ошибка связи — для экрана «Синхронизация».
  String? lastError;

  /// 2026-10-22 (мобилка, деньги, К-5): сессия истекла (401) — очередь ждёт нового входа, записи остаются «ждут связи».
  bool authPaused = false;

  /// 2026-10-22 (мобилка, деньги): ответ сервера 5xx/409/… или сбой разбора ответа — столько попыток подряд, потом запись
  /// уходит в «Не принято» с причиной (повтор — кнопкой, тем же ключом), а очередь идёт дальше. Раньше постоянный 500
  /// останавливал всю очередь навсегда. Обрыв связи (нет ответа) попытки не тратит — очередь просто ждёт.
  static const maxServerAttempts = 3;

  SyncService({required this.db, required this.sales, required this.stock, required this.catalog, required this.owner, this.quotes});

  void start() {
    authPaused = false;
    _timer?.cancel();
    _timer = Timer.periodic(const Duration(seconds: 20), (_) => runOnce());
    runOnce();
  }

  void stop() => _timer?.cancel();

  Future<void> runOnce({bool force = false}) async {
    if (_running) return;
    _running = true;
    try {
      final me = owner();
      final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
      for (final row in await db.pending()) {
        // К-6: чек другого аккаунта (вход сменили) — не отправляем под этим токеном, ждёт входа своим логином.
        if (!OutboxMeta.belongsTo(row, me, legacyOwner: legacy)) continue;
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
          if (e.status == 401) {
            // К-5: истёкшая сессия — не отказ по существу. Запись остаётся в очереди, досылка — после входа.
            await db.markAttempt(row.id, e.message, failed: false);
            authPaused = true;
            break;
          }
          if (e.status == null) {
            // нет связи — порядок не нарушаем, ждём
            await db.markAttempt(row.id, e.message, failed: false);
            break;
          }
          if (e.isTransient) {
            final giveUp = row.attempts + 1 >= maxServerAttempts;
            await db.markAttempt(row.id, e.message, failed: giveUp);
            if (giveUp) continue;
            break;
          }
          // Отказ по существу — в «Не принято» с причиной.
          await db.markAttempt(row.id, e.message, failed: true);
        } catch (e) {
          // 2026-10-22: любая другая ошибка (ответ не того вида и т. п.) больше не ломает очередь.
          lastError = '$e';
          final giveUp = row.attempts + 1 >= maxServerAttempts;
          await db.markAttempt(row.id, '$e', failed: giveUp);
          if (!giveUp) break;
        }
      }
      try {
        await quotes?.call();
      } catch (_) {
        // КП подождут следующего раза — очередь чеков важнее
      }
      if (!authPaused && (force || DateTime.now().difference(_lastCatalog) > const Duration(minutes: 5))) {
        try {
          await catalog.sync();
          _lastCatalog = DateTime.now();
        } on ApiException catch (e) {
          lastError = e.message;
        } catch (e) {
          lastError = '$e';
        }
      }
    } finally {
      _running = false;
    }
  }
}
