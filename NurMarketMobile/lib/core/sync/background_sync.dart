import 'dart:io' show Platform;
import 'dart:ui' show DartPluginRegistrant;

import 'package:drift/drift.dart' show driftRuntimeOptions;
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/widgets.dart' show WidgetsFlutterBinding;
import 'package:workmanager/workmanager.dart';

import '../../features/payment/sales_repository.dart';
import '../../features/sale/catalog_repository.dart';
import '../../features/stock/stock_repository.dart';
import '../api/api_client.dart';
import '../api/session_store.dart';
import '../db/database.dart';
import '../util/audit.dart';
import 'outbox_meta.dart';
import 'sync_service.dart';

/// 2026-10-29 (аудит мобилки, п. 3; новая зависимость workmanager разрешена владельцем): отправка очереди продаж в фоне, когда
/// приложение закрыто. Раньше чеки без связи уходили только пока приложение открыто (таймер 20 с живёт, пока жив процесс), и
/// касса с владельцем не видели такие продажи часами.
///
/// Только Android (WorkManager), только при подключённой сети.
/// 2026-10-30 (оптимизация, п. 14): вместо периодики каждые 15 минут (96 запусков движка Flutter в сутки, даже когда отправлять
/// нечего) — РАЗОВАЯ задача, и только если на телефоне есть неотправленная очередь: ставится, когда она появилась (и при
/// сворачивании), отменяется, когда очередь пуста. Начальная задержка [delay] — пока приложение живо, оно само отправит очередь
/// и снимет задачу, не дожидаясь её. Не ушло (нет связи, ошибка сервера) — задача возвращает «повторить» (экспоненциальная пауза).
/// iOS не затронут — там очередь уходит при открытии приложения (вызовы ниже ничего не делают).
///
/// Задача — тот же [SyncService.runQueue], что в приложении: порядок записей, ключ идемпотентности, 401 ставит очередь на
/// паузу, обрыв связи не тратит попытки. От одновременной отправки с основным изолятом защищает замок в базе
/// ([AppDatabase.tryAcquireQueueLock]). Токены берутся из защищённого хранилища; обновлённые токены туда же и пишутся
/// (основной изолят подхватывает их при возврате — [ApiClient.reloadTokens]). Каждый проход с неотправленным в журнале («Синхронизация»).
class BackgroundSync {
  /// Прежняя периодическая задача (до 1.18.12): при первом вызове снимается.
  static const legacyUniqueName = 'nurmarket.queue-sync';
  static const uniqueName = 'nurmarket.queue-sync.once';
  static const taskName = 'nurmarket.queue-sync.task';

  /// Через сколько после постановки задача может сработать, если приложение к тому времени закрыто.
  static const delay = Duration(minutes: 3);

  static bool _inited = false;

  /// null — неизвестно (после запуска приложения), true — задача стоит, false — снята.
  static bool? _scheduled;

  static bool get supported => !kIsWeb && Platform.isAndroid;

  static Future<void> _init() async {
    if (_inited) return;
    await Workmanager().initialize(backgroundDispatcher);
    _inited = true;
    try {
      await Workmanager().cancelByUniqueName(legacyUniqueName); // 2026-10-30: прежней периодики (каждые 15 минут) больше нет
    } catch (_) {
      // её и не было
    }
  }

  /// Поставить разовую задачу (повтор безопасен: уже стоящая остаётся). Сбой не мешает работе приложения.
  static Future<void> schedule() async {
    if (!supported || _scheduled == true) return;
    try {
      await _init();
      await Workmanager().registerOneOffTask(
        uniqueName,
        taskName,
        initialDelay: delay,
        constraints: Constraints(networkType: NetworkType.connected),
        existingWorkPolicy: ExistingWorkPolicy.keep,
        backoffPolicy: BackoffPolicy.exponential,
        backoffPolicyDelay: const Duration(minutes: 1),
      );
      _scheduled = true;
    } catch (e) {
      Audit.log('error', 'WorkManager: задача не поставлена · $e');
    }
  }

  /// 2026-10-29 (срочно, смена аккаунта): новый вход — фоновая задача ставится заново (старая отменяется), чтобы не тянуть
  /// состояние прошлого входа. Сама задача при каждом проходе читает текущий вход и отправляет только его записи очереди.
  static Future<void> rebind() async {
    if (!supported) return;
    try {
      await _init();
      await Workmanager().cancelByUniqueName(uniqueName);
    } catch (_) {
      // задачи и так нет
    }
    _scheduled = false;
    // расписание после новой привязки — по факту: поставит SyncService.planBackground, если очередь не пуста
  }

  /// Выход из аккаунта или истёкший вход — фоновая отправка не нужна (записи остаются и уйдут после следующего входа).
  static Future<void> cancel() async {
    if (!supported || _scheduled == false) return;
    try {
      await _init();
      await Workmanager().cancelByUniqueName(uniqueName);
      _scheduled = false;
    } catch (_) {
      // задачи и так нет
    }
  }

  /// Один фоновый проход. Возвращает false, только если стоит повторить раньше срока (неожиданный сбой).
  @pragma('vm:entry-point')
  static Future<bool> run() async {
    WidgetsFlutterBinding.ensureInitialized();
    DartPluginRegistrant.ensureInitialized();
    driftRuntimeOptions.dontWarnAboutMultipleDatabases = true; // база открыта и в основном изоляте — это намеренно
    final store = SessionStore();
    final session = await store.load();
    if (session == null) return true; // вышли из аккаунта — очереди под этим входом нет
    final db = AppDatabase();
    try {
      // 2026-10-30 (оптимизация, п. 14): считаются только записи этого входа — чужие под ним не уходят и повтор не вызывают
      final me = OutboxMeta.ownerOf(session);
      final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
      Future<int> mine() async => (await db.pending()).where((r) => OutboxMeta.belongsTo(r, me, legacyOwner: legacy)).length;
      final before = await mine();
      if (before == 0) return true;
      final api = ApiClient(store)..attach(session);
      Audit.currentUser = session.userName;
      final svc = SyncService(
        db: db,
        sales: SalesRepository(api, db),
        stock: StockRepository(api, db),
        catalog: CatalogRepository(api, db),
        owner: () => OutboxMeta.ownerOf(api.session),
        lockOwner: 'bg',
      );
      await svc.runQueue();
      final left = await mine();
      await Audit.log(
        'sync',
        'Фон: отправлено ${before - left < 0 ? 0 : before - left} из $before, осталось $left${svc.lastError == null ? '' : ' · ${svc.lastError}'}',
      );
      // не всё ушло (нет связи, ошибка сервера) — повторить позже с растущей паузой; истёкший вход повтором не лечится (нужен вход в приложение)
      return left == 0 || svc.authPaused;
    } catch (e) {
      await Audit.log('error', 'Фон: сбой отправки очереди · $e');
      return false;
    } finally {
      await db.close();
    }
  }
}

/// Точка входа фонового изолята WorkManager (должна быть верхнего уровня).
@pragma('vm:entry-point')
void backgroundDispatcher() {
  Workmanager().executeTask((task, input) => BackgroundSync.run());
}
