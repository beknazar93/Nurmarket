import 'dart:async';

import 'package:drift/drift.dart' show Value;

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
  // 2026-10-29 (аудит мобилки, п. 4): очередь продаж, каталог и КП — раздельные циклы со своими замками. Раньше общий флаг:
  // пока каталог качался (на медленной сети — минуты), чеки не уходили, а «Закрыть смену» видела «есть неотправленные».
  Future<void>? _queueRun;
  bool _catalogRunning = false;
  bool _quotesRunning = false;
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

  /// 2026-10-29 (аудит, п. 3): кто держит замок отправки очереди — 'app' (основной изолят) или 'bg' (фоновая задача WorkManager).
  final String lockOwner;

  /// 2026-10-30 (оптимизация, п. 14): есть ли неотправленная очередь этого входа — по этому ставится или снимается фоновая задача
  /// WorkManager (задаёт основной изолят; в самой фоновой задаче не нужен).
  final void Function(bool hasUnsent)? onUnsentChanged;

  SyncService({required this.db, required this.sales, required this.stock, required this.catalog, required this.owner, this.quotes, this.lockOwner = 'app', this.onUnsentChanged});

  /// 2026-10-30 (оптимизация, п. 3, 14): есть ли что отправлять ЭТОМУ входу. Дёшево: без ждущих записей тела не читаются.
  Future<bool> hasOwnWork() async {
    if (!await db.hasPending()) return false;
    final me = owner();
    final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
    return (await db.pending()).any((r) => OutboxMeta.belongsTo(r, me, legacyOwner: legacy));
  }

  /// Приложение сворачивается или только что запустилось: поставить фоновую отправку, если очередь не пуста, иначе снять.
  Future<void> planBackground() async {
    final cb = onUnsentChanged;
    if (cb == null) return;
    try {
      cb(await hasOwnWork());
    } catch (_) {
      // фоновая задача — страховка, сбой не мешает
    }
  }

  bool _started = false;

  /// 2026-10-29 (срочно, «Скорость синхронизации»): после неудачного прохода (нет связи, ошибка сервера) таймер повторяет не чаще
  /// раза в минуту (было каждые 20 секунд — телефон будил радио впустую). Возврат в приложение и появление связи идут мимо этой паузы.
  DateTime? _failedAt;
  static const failedBackoff = Duration(seconds: 60);

  void _tick() {
    final f = _failedAt;
    if (f != null && DateTime.now().difference(f) < failedBackoff) return;
    runOnce();
  }

  void _arm() {
    _timer?.cancel();
    _timer = Timer.periodic(const Duration(seconds: 20), (_) => _tick());
  }

  void start() {
    authPaused = false;
    _started = true;
    _failedAt = null;
    _arm();
    // 2026-10-30 (оптимизация, чистка очереди): старые отправленные записи (старше 30 дней, сверх 500 последних) удаляются
    unawaited(db.purgeOutbox().catchError((_) => 0));
    runOnce();
  }

  void stop() {
    _started = false;
    _timer?.cancel();
  }

  /// Приложение ушло в фон — опрос по таймеру на паузу (очередь в фоне уходит задачей WorkManager). Возврат: [resume].
  void pause() => _timer?.cancel();

  void resume() {
    if (_started) _arm();
  }

  /// Только очередь (чеки, приход, ревизия), без каталога. Если проход уже идёт — дожидается его и делает ещё один
  /// (чек мог встать в очередь уже после старта прохода). Закрытие смены ждёт именно это.
  Future<void> runQueue() async {
    final busy = _queueRun;
    if (busy != null) {
      await busy.catchError((_) {});
      final next = _queueRun; // за время ожидания проход мог начать другой ожидающий
      if (next != null) return next.catchError((_) {});
    }
    final f = _lockedDrain();
    _queueRun = f;
    try {
      await f;
    } finally {
      if (identical(_queueRun, f)) _queueRun = null;
    }
  }

  Future<void> runOnce({bool force = false}) async {
    // Таймер: очередь уже идёт — не ждём и не копим проходы.
    if (_queueRun == null) await runQueue();
    var failed = lastError != null && (await db.pending()).isNotEmpty; // очередь не ушла (нет связи, ошибка сервера)
    if (!_quotesRunning) {
      _quotesRunning = true;
      try {
        await quotes?.call();
      } catch (_) {
        // КП подождут следующего раза — очередь чеков важнее
      } finally {
        _quotesRunning = false;
      }
    }
    // 2026-10-29 (аудит, п. 2): лента изменений дешёвая — спрашиваем раз в минуту; без ленты — полная загрузка раз в 5 минут, как раньше
    final every = catalog.feedActive ? const Duration(seconds: 60) : const Duration(minutes: 5);
    if (!_catalogRunning && !authPaused && (force || DateTime.now().difference(_lastCatalog) > every)) {
      _catalogRunning = true;
      try {
        await catalog.sync();
        _lastCatalog = DateTime.now();
        if (!failed) lastError = null; // связь есть, очередь пуста — старая ошибка не висит на экране «Синхронизация»
      } on ApiException catch (e) {
        lastError = e.message;
        failed = true;
      } catch (e) {
        lastError = '$e';
        failed = true;
      } finally {
        _catalogRunning = false;
      }
    }
    _failedAt = failed ? DateTime.now() : null;
  }

  /// Очередь уходит под общим замком (см. [AppDatabase.tryAcquireQueueLock]): основной изолят и фоновая задача не шлют одновременно.
  /// Замок занят другим — ждём до 20 секунд (закрытие смены ждёт именно это), потом пропускаем проход.
  Future<void> _lockedDrain() async {
    // 2026-10-30 (оптимизация, п. 3): очередь пуста — замок не берём и в базу не пишем (раньше каждые 20 секунд — две записи
    // в settings даже при пустой очереди: ~8 600 записей в сутки при открытом приложении). Замок нужен только при отправке.
    final has = await hasOwnWork();
    onUnsentChanged?.call(has);
    if (!has) return;
    var got = false;
    for (var i = 0; i < 20 && !got; i++) {
      got = await db.tryAcquireQueueLock(lockOwner);
      if (!got) await Future<void>.delayed(const Duration(seconds: 1));
    }
    if (!got) return;
    try {
      await _drainQueue();
    } finally {
      await db.releaseQueueLock(lockOwner);
    }
  }

  Future<void> _drainQueue() async {
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
            await stock.replayAudit(row, fromQueue: true); // 2026-10-29: из очереди ревизия не проводится (см. replayAudit)
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
        // 2026-10-29 (аудит мобилки, п. 1): 409 idempotency_conflict на досылке — продажа с этим ключом у сервера УЖЕ есть:
        // «проведено, сверить», а не «Не принято» (иначе «Отправить снова» даст тот же 409, а «Снять» вернёт остаток зря).
        if (e.status == 409 && row.kind == 'sale' && SalesRepository.isKeyConflict(e.payload)) {
          await db.markSent(row.id);
          final note = ApiText.saleAlreadyPosted;
          await (db.update(db.outbox)..where((o) => o.id.equals(row.id))).write(OutboxCompanion(lastError: Value(note)));
          lastError = note;
          continue;
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
  }
}
