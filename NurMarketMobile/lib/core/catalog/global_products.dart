import 'dart:async';
import 'dart:io';

import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';

import '../../features/scan/barcode_lookup.dart';
import '../api/api_client.dart';
import '../providers.dart';

/// 2026-10-27 (владелец: «встрой справочник готовых наименований и обновляй из сервера, если появилось новое»):
/// товар из общего справочника NurCRM (GlobalProduct сервера): штрихкод, название, бренд, категория. Фото в справочнике нет.
class GlobalProduct {
  final String barcode;
  final String name;
  final String brand;
  final String category;
  const GlobalProduct({required this.barcode, required this.name, this.brand = '', this.category = ''});

  @override
  String toString() => 'GlobalProduct($barcode, $name)';
}

/// Состояние справочника для экрана «Профиль»: сколько товаров, от какой выгрузки, сколько добавлено позже.
class GlobalCatalogStatus {
  final int count;

  /// Дата выгрузки встроенной базы (ГГГГ-ММ-ДД).
  final String exportedAt;

  /// Позиции, которых не было во встроенной базе: подтянуты с сервера по штрихкоду или добавлены новой версией приложения.
  final int added;
  const GlobalCatalogStatus({required this.count, required this.exportedAt, required this.added});
}

/// Мусорный штрихкод старого импорта («!», «:», «$^)#@…»): нет цифр или длина вне 6–14 — такой код не отсканировать.
bool isJunkGlobalCode(String code) => code.length < 6 || code.length > 14 || !RegExp(r'\d').hasMatch(code);

class _CatalogDb extends GeneratedDatabase {
  _CatalogDb(super.executor);

  // Своя отдельная база справочника (не база кассы): таблицы создаются вручную, схема drift не используется.
  @override
  Iterable<TableInfo<Table, dynamic>> get allTables => const [];

  @override
  int get schemaVersion => 1;
}

/// Справочник готовых наименований NurCRM: встроенный файл (assets/data/global-products.sqlite, 128 276 товаров от 26.10.2026)
/// копируется в каталог данных приложения и открывается отдельной базой — схема базы кассы не меняется.
/// Поиск по штрихкоду — по первичному ключу; кода нет локально и есть связь — один запрос
/// GET api/main/products/global-barcode/{код}/, найденное дописывается в локальную копию, «не найден» (404) помнится 24 часа.
/// Любая ошибка — молча в журнал, поиск отвечает null (справочник — подсказка, он не должен ломать работу).
class GlobalProducts {
  static const assetPath = 'assets/data/global-products.sqlite';
  static const versionAssetPath = 'assets/data/global-products.version';
  static const fileName = 'global-products.sqlite';

  /// Сколько помнить «в справочнике нет» (404 сервера).
  static const missTtl = Duration(hours: 24);

  /// Файл базы пользователя (если ещё нет — копирует встроенный).
  final Future<File> Function() openFile;

  /// Как открыть файл: в приложении — в фоновом изолейте, в тестах — в этом.
  final QueryExecutor Function(File) executorFor;

  /// Дата выгрузки встроенной базы в этой версии приложения; null — нечем сверять.
  final Future<String?> Function()? bundledVersion;

  /// Встроенная база как временный файл — для слияния (null — нет). Файл после слияния удаляется.
  final Future<File?> Function()? bundledCopy;

  /// Запрос к серверу: ответ 200 (Map) или исключение ApiException (404 — нет в справочнике); null — ответить нечем
  /// (нет входа). Без связи — любое исключение, кроме 404.
  final Future<dynamic> Function(String code)? fetch;

  /// Починка испорченного файла: удалить его, чтобы открылся заново с встроенной базы.
  final Future<void> Function()? repair;

  final DateTime Function() clock;

  GlobalProducts({
    required this.openFile,
    QueryExecutor Function(File)? executorFor,
    this.bundledVersion,
    this.bundledCopy,
    this.fetch,
    this.repair,
    DateTime Function()? clock,
  }) : executorFor = executorFor ?? NativeDatabase.createInBackground,
       clock = clock ?? DateTime.now;

  /// В приложении: файл — в каталоге данных, версия и база — из assets, сервер — через ApiClient.
  factory GlobalProducts.production(ApiClient api) {
    Future<File> target() async {
      final dir = await getApplicationSupportDirectory();
      return File('${dir.path}${Platform.pathSeparator}$fileName');
    }

    return GlobalProducts(
      openFile: () async {
        final f = await target();
        if (!await f.exists() || await f.length() == 0) await _writeAsset(f);
        return f;
      },
      repair: () async {
        final f = await target();
        if (await f.exists()) await f.delete();
      },
      bundledVersion: () async => (await rootBundle.loadString(versionAssetPath)).trim(),
      bundledCopy: () async {
        final f = File('${(await target()).path}.bundle');
        await _writeAsset(f);
        return f;
      },
      fetch: (code) async {
        if (api.session == null) return null; // без входа сервер ответит 401 и приложение разлогинит — не спрашиваем
        return api.get('api/main/products/global-barcode/${Uri.encodeComponent(code)}/', timeout: const Duration(seconds: 4));
      },
    );
  }

  static Future<void> _writeAsset(File target) async {
    final data = await rootBundle.load(assetPath);
    final tmp = File('${target.path}.tmp');
    await tmp.writeAsBytes(data.buffer.asUint8List(data.offsetInBytes, data.lengthInBytes), flush: true);
    if (await target.exists()) await target.delete();
    await tmp.rename(target.path);
  }

  Future<_CatalogDb?>? _opening;
  DateTime? _failedAt;
  Future<void> _tail = Future.value();

  Future<_CatalogDb?> _db() {
    final failed = _failedAt;
    // после сбоя повторяем не чаще раза в 10 минут: диск полон или файл не открывается — не копируем 8 МБ на каждый скан
    if (failed != null && clock().difference(failed) < const Duration(minutes: 10)) return Future.value(null);
    return _opening ??= _open().then((db) {
      _ready = db != null;
      if (db == null) {
        _failedAt = clock();
        _opening = null;
      }
      return db;
    });
  }

  Future<_CatalogDb?> _open() async {
    for (var attempt = 0; attempt < 2; attempt++) {
      _CatalogDb? db;
      try {
        final file = await openFile();
        db = _CatalogDb(executorFor(file));
        await _ensureSchema(db);
        await _mergeNewerBundle(db);
        return db;
      } catch (e) {
        debugPrint('GlobalProducts: не открылся справочник ($e)');
        try {
          await db?.close();
        } catch (_) {}
        if (attempt == 0 && repair != null) {
          try {
            await repair!();
          } catch (e2) {
            debugPrint('GlobalProducts: не удалось пересоздать справочник ($e2)');
            return null;
          }
        } else {
          return null;
        }
      }
    }
    return null;
  }

  Future<void> _ensureSchema(_CatalogDb db) async {
    await db.customStatement(
      'CREATE TABLE IF NOT EXISTS products(barcode TEXT PRIMARY KEY, name TEXT NOT NULL, brand TEXT NOT NULL DEFAULT \'\', '
      'category TEXT NOT NULL DEFAULT \'\') WITHOUT ROWID',
    );
    await db.customStatement('CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)');
    // коды, которых нет и на сервере (404): не спрашиваем снова 24 часа
    await db.customStatement('CREATE TABLE IF NOT EXISTS misses(barcode TEXT PRIMARY KEY, at INTEGER NOT NULL) WITHOUT ROWID');
    await db.customSelect('SELECT 1 FROM products LIMIT 1').get(); // проверка, что файл читается
  }

  Future<String?> _meta(_CatalogDb db, String key) async {
    final rows = await db.customSelect('SELECT value FROM meta WHERE key = ?', variables: [Variable.withString(key)]).get();
    return rows.isEmpty ? null : rows.first.read<String?>('value');
  }

  Future<void> _setMeta(_CatalogDb db, String key, String value) =>
      db.customStatement('INSERT OR REPLACE INTO meta(key, value) VALUES(?, ?)', [key, value]);

  Future<int> _count(_CatalogDb db) async => (await db.customSelect('SELECT COUNT(*) AS c FROM products').getSingle()).read<int>('c');

  /// Приложение обновилось и принесло более свежую выгрузку: новые строки добавляются в копию пользователя
  /// (INSERT OR IGNORE) — то, что подтянуто с сервера, и названия, что уже есть, не теряются и не перезаписываются.
  Future<void> _mergeNewerBundle(_CatalogDb db) async {
    final version = await bundledVersion?.call();
    if (version == null || version.isEmpty) return;
    final have = await _meta(db, 'exported_at') ?? '';
    if (version.compareTo(have) <= 0) return;
    final bundle = await bundledCopy?.call();
    if (bundle == null) return;
    try {
      final before = await _count(db);
      await db.customStatement('ATTACH DATABASE ? AS bundle', [bundle.path]);
      try {
        await db.customStatement(
          'INSERT OR IGNORE INTO products(barcode, name, brand, category) SELECT barcode, name, brand, category FROM bundle.products',
        );
      } finally {
        await db.customStatement('DETACH DATABASE bundle');
      }
      final gained = await _count(db) - before;
      await _setMeta(db, 'exported_at', version);
      if (gained > 0) await _setMeta(db, 'added', '${(int.tryParse(await _meta(db, 'added') ?? '') ?? 0) + gained}');
    } finally {
      try {
        await bundle.delete();
      } catch (_) {}
    }
  }

  // ------------------------------------------------------------------ поиск

  /// Нормализованный код и его допустимые написания (EAN-13 ↔ UPC-A, GTIN-14, UPC-E) без мусорных.
  static List<String> variantsOf(String raw) {
    final code = normalizeCode(raw);
    if (isJunkGlobalCode(code)) return const [];
    return [for (final v in barcodeVariants(code)) if (!isJunkGlobalCode(v)) v].take(8).toList();
  }

  /// Товар справочника по штрихкоду. [network] — при отсутствии локально спросить сервер (один запрос за раз).
  /// Любая ошибка — null.
  Future<GlobalProduct?> find(String raw, {bool network = true}) async {
    try {
      final variants = variantsOf(raw);
      if (variants.isEmpty) return null;
      final db = await _db();
      final local = db == null ? null : await _local(db, variants);
      if (local != null || !network || fetch == null) return local;
      // 13 цифр на «2» — внутренний код магазина или этикетка весов: в общем справочнике их нет
      final code = normalizeCode(raw);
      if (RegExp(r'^2\d{12}$').hasMatch(code)) return null;
      return await _serial(() => _fromServer(db, code, variants));
    } catch (e) {
      debugPrint('GlobalProducts: поиск «$raw» не удался ($e)');
      return null;
    }
  }

  /// Только локальная копия, без сети (быстро; для подсказок, где ждать сервер нельзя).
  Future<GlobalProduct?> local(String raw) => find(raw, network: false);

  /// Открыть справочник заранее (при запуске приложения, не ожидая): первый запуск копирует 8 МБ из assets.
  void warmUp() => unawaited(_db());

  /// Как [local], но если справочник ещё не открыт — сразу null, не ждём (подсказка на экране продажи не должна тормозить).
  Future<GlobalProduct?> localIfReady(String raw) => _ready ? local(raw) : Future.value(null);

  bool _ready = false;

  Future<GlobalProduct?> _local(_CatalogDb db, List<String> variants) async {
    final rows = await db
        .customSelect(
          'SELECT barcode, name, brand, category FROM products WHERE barcode IN (${List.filled(variants.length, '?').join(',')})',
          variables: [for (final v in variants) Variable.withString(v)],
        )
        .get();
    if (rows.isEmpty) return null;
    final byCode = {for (final r in rows) r.read<String>('barcode'): r};
    final r = byCode[variants.firstWhere(byCode.containsKey)]!;
    return GlobalProduct(barcode: r.read<String>('barcode'), name: r.read<String>('name'), brand: r.read<String>('brand'), category: r.read<String>('category'));
  }

  /// Не больше одного запроса к серверу одновременно: следующий ждёт предыдущий.
  Future<T> _serial<T>(Future<T> Function() job) {
    final prev = _tail;
    final done = Completer<void>();
    _tail = done.future;
    return prev.then((_) => job()).whenComplete(done.complete);
  }

  /// [db] == null — справочник на телефоне не открылся: сервер всё равно спросим, но запоминать нечем.
  Future<GlobalProduct?> _fromServer(_CatalogDb? db, String code, List<String> variants) async {
    final now = clock().millisecondsSinceEpoch;
    if (db != null) {
      // пока ждали очередь, этот код могли уже найти или признать отсутствующим
      final again = await _local(db, variants);
      if (again != null) return again;
      final missed = await db.customSelect('SELECT at FROM misses WHERE barcode = ?', variables: [Variable.withString(code)]).get();
      if (missed.isNotEmpty && now - missed.first.read<int>('at') < missTtl.inMilliseconds) return null;
    }
    dynamic data;
    try {
      data = await fetch!(code).timeout(const Duration(seconds: 6));
    } on ApiException catch (e) {
      if (e.status == 404 && db != null) {
        await db.customStatement('DELETE FROM misses WHERE at < ?', [now - missTtl.inMilliseconds]);
        await db.customStatement('INSERT OR REPLACE INTO misses(barcode, at) VALUES(?, ?)', [code, now]);
      }
      return null; // нет связи, 401, 5xx — не «нет в справочнике», ничего не запоминаем
    } catch (_) {
      return null; // тайм-аут, обрыв
    }
    final p = _parse(data, code);
    if (p == null || db == null) return p;
    await db.customStatement('INSERT OR REPLACE INTO products(barcode, name, brand, category) VALUES(?, ?, ?, ?)', [p.barcode, p.name, p.brand, p.category]);
    await db.customStatement('DELETE FROM misses WHERE barcode = ?', [code]);
    await _setMeta(db, 'added', '${(int.tryParse(await _meta(db, 'added') ?? '') ?? 0) + 1}');
    return p;
  }

  static GlobalProduct? _parse(dynamic data, String asked) {
    if (data is! Map) return null;
    final m = data['product'] is Map ? data['product'] as Map : data;
    String text(String k) {
      final v = m[k];
      if (v is Map) return '${v['name'] ?? ''}'.trim();
      return v == null ? '' : '$v'.trim();
    }

    final name = text('name');
    if (name.isEmpty) return null;
    final serverCode = normalizeCode(text('barcode'));
    return GlobalProduct(barcode: serverCode.isNotEmpty && !isJunkGlobalCode(serverCode) ? serverCode : asked, name: name, brand: text('brand'), category: text('category'));
  }

  // ------------------------------------------------------------------ состояние

  Future<GlobalCatalogStatus?> status() async {
    try {
      final db = await _db();
      if (db == null) return null;
      return GlobalCatalogStatus(
        count: await _count(db),
        exportedAt: await _meta(db, 'exported_at') ?? '',
        added: int.tryParse(await _meta(db, 'added') ?? '') ?? 0,
      );
    } catch (e) {
      debugPrint('GlobalProducts: состояние справочника не прочитано ($e)');
      return null;
    }
  }

  Future<void> close() async {
    final opening = _opening;
    _opening = null;
    _ready = false;
    try {
      await (await opening)?.close();
    } catch (_) {}
  }
}

/// Справочник готовых наименований NurCRM — один на приложение (закрывается вместе с контейнером).
final globalProductsProvider = Provider<GlobalProducts>((ref) {
  final g = GlobalProducts.production(ref.watch(apiProvider));
  ref.onDispose(g.close);
  return g;
});

/// Состояние справочника для «Профиля»; null — справочник не открылся.
final globalCatalogStatusProvider = FutureProvider.autoDispose<GlobalCatalogStatus?>((ref) => ref.watch(globalProductsProvider).status());

/// 128276 → «128 276» (неразрывный пробел).
String groupThousands(int n) => n.toString().replaceAllMapped(RegExp(r'\B(?=(\d{3})+(?!\d))'), (_) => ' ');

/// «2026-10-26» → «26.10.2026»; не дата — как есть.
String niceCatalogDate(String iso) {
  final m = RegExp(r'^(\d{4})-(\d{2})-(\d{2})').firstMatch(iso);
  return m == null ? iso : '${m[3]}.${m[2]}.${m[1]}';
}
