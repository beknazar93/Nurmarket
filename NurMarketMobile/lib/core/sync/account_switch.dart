import 'dart:convert';
import 'dart:io';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:path_provider/path_provider.dart';

import '../../features/sale/catalog_repository.dart';
import '../../features/scan/code_lookup.dart';
import '../ai/cam_aliases.dart';
import '../ai/vision.dart';
import '../db/database.dart';
import '../link/kassa_link.dart';
import '../util/audit.dart';
import '../util/photo_cache.dart';
import 'outbox_meta.dart';

/// 2026-10-29 (срочно, владелец: «Ничего не должно остаться от старого аккаунта!!»): выход и смена аккаунта стирают с телефона
/// ВСЁ — очередь, ключи идемпотентности, товары, клиентов, настройки (тема, язык, принтер, режим тестировщика, id устройства),
/// ключи ИИ, токены, журналы и файлы. Исключение одно: служебная метка миграции поиска (`search.keyV`, без данных аккаунта).
///
/// Деньги: пока в очереди есть неотправленные продажи или операции, выйти нельзя (окно [AccountSwitch.queueSummary] в
/// `logout_guard.dart`): их нужно отправить, либо выгрузить в файл ([exportQueue]), либо подтвердить удаление словом «УДАЛИТЬ».
class AccountSwitch {
  /// Аккаунт последнего входа (хранится в настройках телефона до выхода).
  static const lastOwnerKey = 'session.lastOwner';

  /// Не стирается: метка «ключи поиска приведены к новому виду» (миграция базы), данных аккаунта в ней нет.
  static const _keepKeys = {'search.keyV'};

  /// Секреты телефона, привязанные к аккаунту: ключи ИИ (Gemini, Groq, OpenRouter), сопряжение с кассой (PhoneLink), вход.
  static const secretKeys = [...VisionService.accountSecretKeys, 'kassa.link.pairing', 'kassa.link.phone', 'kassa.link.seq', 'nurmarket.session'];

  /// Только для тестов: папка вместо папки приложения (вход идёт через SessionController, параметров у него нет).
  static Directory? testDirectory;

  /// Чьи товары лежат в базе — по области каталога («логин|магазин|сервер»); пусто — товаров нет или область неизвестна.
  static Future<String> catalogOwner(AppDatabase db) async {
    final scope = (await db.getSetting(CatalogRepository.scopeKey)) ?? '';
    final i = scope.indexOf('|');
    return (i < 0 ? scope : scope.substring(0, i)).trim().toLowerCase();
  }

  /// Вызывать при входе аккаунтом [owner] (логин в нижнем регистре). true — на телефоне были данные другого аккаунта (выход не
  /// делали: истёк вход, обновление) — они стёрты целиком, вместе с очередью (окно «выгрузить или удалить» показал экран входа).
  static Future<bool> prepare(AppDatabase db, String owner, {Directory? directory, Future<void> Function(String key)? deleteSecret}) async {
    if (owner.isEmpty) return false;
    final last = ((await db.getSetting(lastOwnerKey)) ?? '').trim().toLowerCase();
    final holder = await catalogOwner(db);
    final foreign = (last.isNotEmpty && last != owner) || (holder.isNotEmpty && holder != owner);
    if (!foreign) return false;
    await wipeAll(db, directory: directory, deleteSecret: deleteSecret);
    return true;
  }

  /// Неотправленное в очереди: ждёт отправки или не принято сервером ([pending], [failed]); [sum] — сумма продаж.
  static Future<QueueSummary> queueSummary(AppDatabase db, {String? owner}) async {
    final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
    var pending = 0, failed = 0;
    var sum = 0.0;
    final owners = <String>{};
    for (final r in await db.select(db.outbox).get()) {
      if (r.status != 'pending' && r.status != 'failed') continue;
      final who = OutboxMeta.ownerOfRow(r) ?? legacy ?? '';
      if (owner != null && who.isNotEmpty && who != owner) continue;
      if (r.status == 'pending') {
        pending++;
      } else {
        failed++;
      }
      if (r.kind == 'sale') sum += r.total;
      if (who.isNotEmpty) owners.add(who);
    }
    return QueueSummary(pending: pending, failed: failed, sum: sum, owners: owners);
  }

  /// Неотправленное в очереди ДРУГИХ аккаунтов, чем [owner] (экран входа: вводят чужой логин).
  static Future<QueueSummary> foreignQueue(AppDatabase db, String owner) async {
    final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
    var pending = 0, failed = 0;
    var sum = 0.0;
    final owners = <String>{};
    for (final r in await db.select(db.outbox).get()) {
      if (r.status != 'pending' && r.status != 'failed') continue;
      final who = OutboxMeta.ownerOfRow(r) ?? legacy ?? '';
      if (who.isEmpty || who == owner) continue;
      if (r.status == 'pending') {
        pending++;
      } else {
        failed++;
      }
      if (r.kind == 'sale') sum += r.total;
      owners.add(who);
    }
    return QueueSummary(pending: pending, failed: failed, sum: sum, owners: owners);
  }

  /// Выгрузить неотправленное в файл `unsent-<логин>-<дата>.json` (запасной выход, когда нет связи). Файл содержит данные чеков
  /// и остаётся на телефоне — это единственное, что не стирается при выходе. Возвращает файл. [directory] — для тестов.
  static Future<File> exportQueue(AppDatabase db, {required String owner, Directory? directory, DateTime? now}) async {
    final at = now ?? DateTime.now();
    final rows = [
      for (final r in await db.select(db.outbox).get())
        if (r.status == 'pending' || r.status == 'failed') r.toJson(),
    ];
    final base = directory ?? testDirectory ?? await _exportDirectory();
    final dir = Directory('${base.path}${Platform.pathSeparator}NurMarket');
    await dir.create(recursive: true);
    String two(int v) => v.toString().padLeft(2, '0');
    final safe = owner.replaceAll(RegExp(r'[^a-z0-9._@-]'), '_');
    final f = File('${dir.path}${Platform.pathSeparator}unsent-$safe-${at.year}${two(at.month)}${two(at.day)}-${two(at.hour)}${two(at.minute)}.json');
    await f.writeAsString(
      const JsonEncoder.withIndent('  ').convert({'app': 'NurCRM Mobile', 'kind': 'unsent-queue', 'owner': owner, 'exportedAt': at.toIso8601String(), 'rows': rows}),
      flush: true,
    );
    return f;
  }

  /// Папка для файла выгрузки: на Android — «Documents» приложения (видна в файловом менеджере), иначе документы приложения.
  static Future<Directory> _exportDirectory() async {
    try {
      if (Platform.isAndroid) {
        final dirs = await getExternalStorageDirectories(type: StorageDirectory.documents);
        if (dirs != null && dirs.isNotEmpty) return dirs.first;
      }
    } catch (_) {
      // внешнего хранилища нет — документы приложения
    }
    return getApplicationDocumentsDirectory();
  }

  /// Стереть ВСЁ, что принадлежит аккаунту: очередь, настройки, товары, секреты, файлы, журналы, кэши в памяти.
  /// Файл выгрузки очереди (`NurMarket/unsent-*.json`) не трогается.
  static Future<void> wipeAll(AppDatabase db, {Directory? directory, Future<void> Function(String key)? deleteSecret}) async {
    await db.transaction(() async {
      await db.delete(db.outbox).go();
      await db.delete(db.products).go();
      await (db.delete(db.settings)..where((s) => s.key.isNotIn(_keepKeys))).go();
    });
    await _wipeSecrets(deleteSecret);
    await _wipeFiles(directory);
    await _forgetMemory();
  }

  static Future<void> _wipeSecrets(Future<void> Function(String key)? deleteSecret) async {
    const storage = FlutterSecureStorage();
    for (final k in secretKeys) {
      try {
        await (deleteSecret != null ? deleteSecret(k) : storage.delete(key: k));
      } catch (_) {
        // хранилище недоступно (в тесте без платформы) — запись и так не прочитается
      }
    }
    try {
      if (deleteSecret == null) await storage.deleteAll(); // и всё остальное в защищённом хранилище приложения
    } catch (_) {}
  }

  /// Файлы приложения: «Действия ИИ», последняя ошибка и замеры ИИ, эталонные снимки камеры, журнал аудита, временные PDF/CSV/фото.
  static Future<void> _wipeFiles(Directory? directory) async {
    try {
      final dir = directory ?? testDirectory ?? await getApplicationDocumentsDirectory();
      for (final name in const ['ai_actions.jsonl', 'ai_last_error.json', 'ai_perf.log']) {
        final f = File('${dir.path}/$name');
        if (await f.exists()) await f.delete();
      }
      final refs = Directory('${dir.path}/refs');
      if (await refs.exists()) await refs.delete(recursive: true);
      await Audit.wipeAll(directory: dir);
    } catch (_) {
      // файл занят — старое содержимое перезапишется новыми записями
    }
    if (directory == null && testDirectory == null) {
      try {
        final tmp = await getTemporaryDirectory();
        await for (final e in tmp.list()) {
          final n = e.path.toLowerCase();
          if (e is File && (n.endsWith('.pdf') || n.endsWith('.csv') || n.endsWith('.jpg') || n.endsWith('.png'))) await e.delete();
        }
      } catch (_) {}
      await PhotoCache.wipe(); // 2026-10-30 (оптимизация, п. 12): дисковый кэш фото товаров прошлого аккаунта
      try {
        final support = await getApplicationSupportDirectory();
        final shots = Directory('${support.path}/tester_shots'); // снимки экрана режима тестировщика
        if (await shots.exists()) await shots.delete(recursive: true);
      } catch (_) {}
    }
  }

  /// Всё запомненное в памяти приложения (ключи ИИ, правила, алиасы камеры, сопряжение с кассой).
  static Future<void> _forgetMemory() async {
    VisionService.forgetAccount();
    CamAliases.forgetAccount();
    ScaleRulesLoader.forgetAccount();
    try {
      await KassaLink.instance.forgetAccount();
    } catch (_) {}
  }
}

/// Итог по очереди: сколько ждёт отправки, сколько не принято сервером, на какую сумму продаж, чьи записи.
class QueueSummary {
  final int pending;
  final int failed;
  final double sum;
  final Set<String> owners;
  const QueueSummary({required this.pending, required this.failed, required this.sum, required this.owners});
  int get count => pending + failed;
  bool get isEmpty => count == 0;
}
