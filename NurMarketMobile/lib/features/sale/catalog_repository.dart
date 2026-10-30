import 'dart:convert';

import 'package:drift/drift.dart' show Value;

import '../../core/api/api_client.dart';
import '../../core/catalog/search_match.dart';
import '../../core/db/database.dart';
import '../../core/sync/account_switch.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/util/audit.dart';
import '../stock/product_packages.dart';
import 'cart.dart' show PromoRule;

/// Каталог: страницы api/main/products/list/ по 300 → локальная база (ТЗ, раздел 8).
/// 2026-10-29 (аудит мобилки, п. 2): целиком каталог качается только при первом входе, смене магазина, ошибке ленты и раз в
/// [fullEvery] как страховка; всё остальное время — лента изменений api/main/products/changes/ (как CatalogFeedSync кассы:
/// только изменённые и удалённые товары, курсор в настройках телефона). Сервер ленту может не поддерживать (404/405/501,
/// непонятный ответ) — тогда прежняя полная загрузка, но если api/main/products/catalog-version/ не менялась, то не чаще раза в
/// [versionRecheck]. Штрихкод, артикул и другие поля, которых нет в присланной карточке, не затираются (урок Б-1 кассы).
class CatalogRepository {
  final ApiClient api;
  final AppDatabase db;
  CatalogRepository(this.api, this.db);

  static const _pageSize = 300;
  static const _maxPages = 600; // при 100 строках на странице — до 60 000 товаров

  /// Последняя полная загрузка дошла до конца списка (иначе её не считаем полной: см. _fullSync).
  bool _fullComplete = true;
  static const feedPageLimit = 500;
  static const maxFeedPages = 40;
  static const fullEvery = Duration(hours: 12);
  static const versionRecheck = Duration(minutes: 30);
  static const feedRetry = Duration(minutes: 10);

  static const feedScopeKey = 'catalog.feed.scope';
  static const feedCursorKey = 'catalog.feed.cursor';
  static const fullAtKey = 'catalog.fullAt';
  static const checkedAtKey = 'catalog.checkedAt';
  static const versionKey = 'catalog.version';
  static const etagKey = 'catalog.feed.etag'; // «курсор|ETag» последнего ответа ленты
  static const scopeKey = 'catalog.scope'; // чьи (аккаунт|магазин|сервер) товары лежат в базе телефона

  DateTime _feedPausedUntil = DateTime.fromMillisecondsSinceEpoch(0);

  /// Лента работает (последний опрос ответил) — SyncService спрашивает её раз в минуту, а не полную загрузку раз в 5 минут.
  bool feedActive = false;

  // 2026-10-29 (аудит мобилки, п. 4): замок на загрузку каталога. Главная, склад, новый товар, умная камера и ИИ вызывали sync() мимо
  // SyncService; две пересёкшиеся загрузки вычитали неотправленные продажи (_reapplyPending) дважды. Теперь загрузки идут по одной:
  // вызов во время загрузки ждёт её и делает ровно одну следующую (общую для всех ожидающих) — после своего изменения каталог читается заново.
  Future<int>? _syncRun;
  Future<int>? _syncNext;

  /// Обновить каталог: лента изменений, а если её нет или она сбилась — полная загрузка. [full] — принудительно целиком.
  /// Возвращает число изменённых (при полной загрузке — всех) товаров.
  Future<int> sync({bool full = false}) {
    final cur = _syncRun;
    if (cur == null) return _startSync(full);
    return _syncNext ??= cur.then<void>((_) {}, onError: (_) {}).then((_) {
      _syncNext = null;
      return _startSync(full);
    });
  }

  Future<int> _startSync(bool full) {
    final f = _syncOnce(full: full);
    _syncRun = f;
    f.then<void>((_) => _endSync(f), onError: (_) => _endSync(f));
    return f;
  }

  void _endSync(Future<int> f) {
    if (identical(_syncRun, f)) _syncRun = null;
  }

  String _scope() {
    final s = api.session;
    return '${s?.email ?? ''}|${s?.companyName ?? ''}|${ApiClient.defaultBaseUrl}';
  }

  Future<int> _syncOnce({required bool full}) async {
    // 2026-10-29 (срочно): товары другого аккаунта на телефоне (вход сменили, а стирание не сработало) — не показываем и не
    // принимаем за свои: стираем до загрузки, дальше — только целиком.
    final holder = await AccountSwitch.catalogOwner(db);
    final me = (api.session?.email ?? '').trim().toLowerCase();
    if (holder.isNotEmpty && me.isNotEmpty && holder != me) await db.wipeCatalogForAccountSwitch();
    // первый вход, пустая база или другой аккаунт/магазин — только целиком (ни лента, ни «версия та же» не годятся)
    if (!full && await db.productCount() > 0 && await db.getSetting(scopeKey) == _scope()) {
      final n = await _feedTick();
      if (n != null) return n;
      if (await _versionUnchanged()) return 0;
    }
    return _fullSync();
  }

  Future<void> _markChecked() => db.setSetting(checkedAtKey, DateTime.now().toIso8601String());

  /// Ключ поиска товара — нормализованный (ё=е, кыргызские буквы), как SearchMatch кассы; считается один раз при записи.
  static String _searchKey(String name, String? sku, String? barcode, String? category, String? brand) =>
      SearchMatch.key([name, sku ?? '', barcode ?? '', category ?? '', brand ?? '']);

  // ------------------------------------------------------------------ лента изменений

  /// null — ленты нет или она сбилась: нужна полная загрузка. Число — сколько товаров лента изменила.
  Future<int?> _feedTick() async {
    if (DateTime.now().isBefore(_feedPausedUntil)) {
      feedActive = false;
      return null;
    }
    if (await db.productCount() == 0) return null;
    final scope = _scope();
    if (await db.getSetting(feedScopeKey) != scope) return null; // первый вход или другой магазин — сначала целиком
    final fullAt = DateTime.tryParse(await db.getSetting(fullAtKey) ?? '');
    if (fullAt == null) return null;
    // 2026-10-30 (оптимизация, п. 15): страховочная полная загрузка раз в 12 часов (150 запросов, 2–4 МБ) — только если версия каталога
    // или число товаров разошлись с сервером; совпали — срок просто продлевается (лента изменений и так держит каталог свежим)
    if (DateTime.now().difference(fullAt) > fullEvery && !await _fullStillValid()) return null;
    return _walkFeed();
  }

  /// 2026-10-30 (оптимизация, п. 15): полная загрузка не нужна — версия каталога (catalog-version) та же, что при последней полной
  /// загрузке, и число товаров на телефоне равно серверному (count списка). Любое расхождение (или сервер версию не отдаёт) —
  /// false и запись в журнал («Журнал действий», вид «Синхронизация»). Нет связи — исключение наверх, как у полной загрузки.
  Future<bool> _fullStillValid() async {
    try {
      final v = await _readVersion();
      if (v == null || v.isEmpty) return false; // сервер версию не отдаёт — как раньше, целиком
      final stored = await db.getSetting(versionKey) ?? '';
      if (v != stored) {
        Audit.log('sync', 'Каталог: версия сервера $v, на телефоне ${stored.isEmpty ? 'нет' : stored} — полная загрузка');
        return false;
      }
      final data = await api.get('api/main/products/list/', query: {'page': 1, 'page_size': 1}, timeout: const Duration(seconds: 15));
      final total = data is Map && data['count'] is num ? (data['count'] as num).toInt() : null;
      if (total == null) return false;
      final local = await db.productCount();
      if (local != total) {
        Audit.log('sync', 'Каталог: на телефоне $local товаров, на сервере $total — полная загрузка');
        return false;
      }
      await db.setSetting(fullAtKey, DateTime.now().toIso8601String());
      await _markChecked();
      return true;
    } on ApiException catch (e) {
      if (e.status == null) rethrow;
      return false;
    } catch (_) {
      return false;
    }
  }

  /// Читает страницы ленты от сохранённого курсора. Сеть оборвалась (status null) — исключение наверх, как у полной загрузки.
  Future<int?> _walkFeed() async {
    var cursor = await db.getSetting(feedCursorKey) ?? '';
    var changed = 0;
    var missing = false;
    for (var page = 0; page < maxFeedPages; page++) {
      dynamic data;
      // ETag прошлого ответа по этому же курсору → If-None-Match (без W/): ничего нового — 304, без разбора и записи
      final savedEtag = (await db.getSetting(etagKey)) ?? '';
      final cut = savedEtag.indexOf('|');
      final etag = cut >= 0 && savedEtag.substring(0, cut) == cursor ? savedEtag.substring(cut + 1) : '';
      try {
        api.ifNoneMatch = etag.isEmpty ? null : etag;
        data = await api.get('api/main/products/changes/', query: {'limit': feedPageLimit, if (cursor.isNotEmpty) 'since': cursor}, timeout: const Duration(seconds: 20));
      } on ApiException catch (e) {
        final code = e.status;
        if (code == null || code == 401) rethrow;
        if (code == 429) {
          _feedPausedUntil = DateTime.now().add(const Duration(seconds: 30));
          return changed;
        }
        // 404/405/501 — сервер ленты не знает; 410 — курсор устарел; прочее — лента сбилась: полная загрузка, лента позже
        feedActive = false;
        _feedPausedUntil = DateTime.now().add(code == 410 ? Duration.zero : feedRetry);
        if (code == 410) await db.setSetting(fullAtKey, ''); // курсор устарел — версия «та же» не повод пропустить загрузку
        return null;
      }
      if (identical(data, ApiClient.notModified)) {
        feedActive = true;
        break;
      }
      final newEtag = api.lastEtag;
      final parsed = FeedPage.parse(data);
      if (parsed == null) {
        feedActive = false;
        _feedPausedUntil = DateTime.now().add(feedRetry);
        return null;
      }
      feedActive = true;
      if (parsed.items.isNotEmpty || parsed.deleted.isNotEmpty) {
        final r = await _applyFeed(parsed);
        changed += r.changed;
        missing |= r.missing;
      }
      final requested = cursor;
      final moved = parsed.cursor.isNotEmpty && parsed.cursor != cursor;
      if (moved) {
        cursor = parsed.cursor;
        await db.setSetting(feedCursorKey, cursor);
      }
      // ETag относится к ответу на запрос с курсором requested — уйдёт вместе с ним, пока курсор не сдвинется
      await db.setSetting(etagKey, newEtag == null || newEtag.isEmpty ? '' : '$requested|${ApiClient.stripWeak(newEtag)}');
      if (!parsed.hasMore || !moved) break;
    }
    await _markChecked();
    if (changed > 0) await db.setSetting('catalog.syncedAt', DateTime.now().toIso8601String());
    if (missing) {
      // в ленте товар без карточки, которого нет на телефоне — нужна полная загрузка (не чаще раза в 5 минут)
      final fullAt = DateTime.tryParse(await db.getSetting(fullAtKey) ?? '');
      if (fullAt == null || DateTime.now().difference(fullAt) > const Duration(minutes: 5)) return null;
    }
    return changed;
  }

  /// Применить страницу ленты: карточка с названием — как при полной загрузке, но поля, которых в ней нет, остаются прежними;
  /// без названия — только присланные цена, опт, остаток, минимум. Остаток не отправленных продаж вычитается заново.
  Future<({int changed, bool missing})> _applyFeed(FeedPage page) async {
    final ids = <String>{for (final m in page.items) ?(str(m, 'id') ?? str(m, 'product_id'))};
    final old = await db.productsByIds(ids);
    final inserts = <ProductsCompanion>[];
    final updates = <ProductsCompanion>[];
    final packs = <String, PiecePack?>{};
    final touched = <String>{};
    var missing = false;
    for (final m in page.items) {
      final id = str(m, 'id') ?? str(m, 'product_id');
      if (id == null) continue;
      final prev = old[id];
      final hasName = str(m, 'name') != null || str(m, 'title') != null || str(m, 'display_name') != null;
      if (hasName) {
        final row = _map({...m, 'id': id});
        if (row == null) continue;
        if (prev == null) {
          inserts.add(row);
        } else {
          updates.add(mergeCard(m, row, prev));
        }
        if (m.containsKey('packages')) packs[id] = str(m, 'kind') == 'bundle' ? null : PiecePack.first(m['packages']);
      } else if (prev == null) {
        missing = true;
        continue;
      } else {
        String? qtyKey;
        for (final k in const ['quantity', 'stock', 'stock_quantity']) {
          if (m.containsKey(k)) {
            qtyKey = k;
            break;
          }
        }
        final minKey = m.containsKey('minimum_quantity') ? 'minimum_quantity' : (m.containsKey('min_quantity') ? 'min_quantity' : null);
        updates.add(
          ProductsCompanion(
            id: Value(id),
            price: m.containsKey('price') ? Value(num0(m, 'price')) : const Value.absent(),
            wholesalePrice: m.containsKey('wholesale_price') ? Value(num0(m, 'wholesale_price')) : const Value.absent(),
            quantity: qtyKey != null ? Value(num0(m, qtyKey)) : const Value.absent(),
            minQuantity: minKey != null ? Value(num0(m, minKey)) : const Value.absent(),
            // 2026-10-30: закупка — только если пришла (урок Б-1: нет поля — не затираем прежнее значение)
            purchasePrice: _hasCost(m) ? Value(num0(m, 'purchase_price')) : const Value.absent(),
          ),
        );
      }
      touched.add(id);
    }
    await db.applyCatalogChanges(inserts: inserts, updates: updates, deletedIds: page.deleted);
    if (packs.isNotEmpty || page.deleted.isNotEmpty) {
      final all = await PackageStore.read(db);
      var dirty = false;
      for (final e in packs.entries) {
        if (e.value == null) {
          dirty |= all.remove(e.key) != null;
        } else {
          all[e.key] = e.value!;
          dirty = true;
        }
      }
      for (final d in page.deleted) {
        dirty |= all.remove(d) != null;
      }
      if (dirty) await PackageStore.replaceAll(db, all);
    }
    // остаток пришёл с сервера, а свои неотправленные продажи и приходы он ещё не знает — только для изменённых товаров
    if (touched.isNotEmpty) await _reapplyPending(only: touched);
    return (changed: touched.length + page.deleted.length, missing: missing);
  }

  static bool _hasAny(Map<String, dynamic> m, List<String> keys) => keys.any(m.containsKey);

  /// 2026-10-30 (ревизия в деньгах): закупка пришла числом. Нет поля или null (кассиру сервер её не отдаёт) — прежнее значение не трогаем.
  static bool _hasCost(Map<String, dynamic> m) => m['purchase_price'] != null && m['purchase_price'] != '';

  /// Карточка ленты поверх записи телефона: поле, источника которого в карточке нет вовсе, остаётся прежним (штрихкод, артикул,
  /// PLU, фото, минимум, акции …). Ключ пришёл (даже пустой) — применяется как пришло. Ключ поиска собирается из итоговых значений.
  static ProductsCompanion mergeCard(Map<String, dynamic> m, ProductsCompanion row, Product prev) {
    bool has(List<String> keys) => _hasAny(m, keys);
    final barcode = has(const ['barcode', 'ean']) ? row.barcode.value : prev.barcode;
    final sku = has(const ['sku', 'article', 'code']) ? row.sku.value : prev.sku;
    final category = has(const ['category', 'category_name']) ? row.category.value : prev.category;
    return ProductsCompanion(
      id: row.id,
      name: row.name,
      price: has(const ['price']) ? row.price : const Value.absent(),
      wholesalePrice: has(const ['wholesale_price']) ? row.wholesalePrice : const Value.absent(),
      barcode: has(const ['barcode', 'ean']) ? row.barcode : const Value.absent(),
      altBarcodes: has(const ['alternate_barcodes']) ? row.altBarcodes : const Value.absent(),
      sku: has(const ['sku', 'article', 'code']) ? row.sku : const Value.absent(),
      plu: has(const ['plu']) ? row.plu : const Value.absent(),
      unit: has(const ['unit', 'unit_display']) ? row.unit : const Value.absent(),
      isWeight: has(const ['unit', 'unit_display', 'is_weight', 'is_wait', 'is_weigh', 'is_weight_product', 'sale_as_weight', 'sells_by_weight'])
          ? row.isWeight
          : const Value.absent(),
      isService: has(const ['is_service', 'kind']) ? row.isService : const Value.absent(),
      isFavorite: has(const ['is_favorite']) ? row.isFavorite : const Value.absent(),
      quantity: has(const ['quantity']) ? row.quantity : const Value.absent(),
      category: has(const ['category', 'category_name']) ? row.category : const Value.absent(),
      imageUrl: Value(row.imageUrl.value ?? prev.imageUrl),
      searchKey: Value(_searchKey(row.name.value, sku, barcode, category, str(m, 'brand_name'))),
      promoRules: has(const ['promotion_rules']) ? row.promoRules : const Value.absent(),
      minQuantity: has(const ['minimum_quantity', 'min_quantity']) ? row.minQuantity : const Value.absent(),
      purchasePrice: row.purchasePrice, // 2026-10-30: absent, если в карточке закупки нет (_map)
    );
  }

  // ------------------------------------------------------------------ версия каталога (запасной путь без ленты)

  /// Ленты нет, но есть api/main/products/catalog-version/: версия та же и полная загрузка была недавно — качать нечего.
  Future<bool> _versionUnchanged() async {
    final fullAt = DateTime.tryParse(await db.getSetting(fullAtKey) ?? '');
    if (fullAt == null || DateTime.now().difference(fullAt) > versionRecheck) return false;
    if (await db.productCount() == 0) return false;
    try {
      final v = await _readVersion();
      if (v == null || v.isEmpty) return false;
      if (v != await db.getSetting(versionKey)) return false;
      await _markChecked();
      return true;
    } catch (_) {
      return false;
    }
  }

  Future<String?> _readVersion() async {
    final data = await api.get('api/main/products/catalog-version/', timeout: const Duration(seconds: 8));
    if (data is! Map) return null;
    final root = data['data'] is Map ? (data['data'] as Map).cast<String, dynamic>() : data.cast<String, dynamic>();
    for (final k in const ['catalog_version', 'catalogVersion', 'version']) {
      final v = root[k];
      if (v is String && v.isNotEmpty) return v;
      if (v is num) return v.toString();
    }
    return null;
  }

  // ------------------------------------------------------------------ полная загрузка

  Future<int> _fullSync() async {
    String? version;
    try {
      version = await _readVersion();
    } on ApiException catch (e) {
      if (e.status == null) rethrow; // нет связи — полная загрузка всё равно не пройдёт
    } catch (_) {
      // версия — только запасной путь
    }
    final n = await _fullDownload();
    if (n > 0 && _fullComplete) {
      await db.setSetting(fullAtKey, DateTime.now().toIso8601String());
      await db.setSetting(scopeKey, _scope());
      await db.setSetting(versionKey, version ?? '');
      await _markChecked();
      // курсор ленты — с начала: первый проход применит свежие карточки поверх загруженных (без потерь за время загрузки)
      if (DateTime.now().isAfter(_feedPausedUntil)) {
        await db.setSetting(feedCursorKey, '');
        await db.setSetting(feedScopeKey, _scope());
        try {
          await _walkFeed();
        } catch (_) {
          // лента подождёт следующего опроса
        }
      }
    }
    return n;
  }

  Future<int> _fullDownload() async {
    final rows = <ProductsCompanion>[];
    final seen = <String>{};
    final noMin = <String>{};
    final noCost = <String>{}; // 2026-10-30: товары, у которых список закупку не прислал — прежнюю закупку не стираем
    // 2026-10-27 (М4): поштучная продажа из упаковки (packages) по товарам; listHasPackages — список вообще отдаёт это поле
    final packs = <String, PiecePack>{};
    var listHasPackages = false;
    // 2026-10-29 (срочно, «Скорость синхронизации»): сервер отдаёт не больше 100 строк на страницу, а цикл останавливался, раз пришло
    // меньше 300 — грузилось 100 товаров из 369, остальные стирались. Теперь идём до конца по next/count, а не по размеру страницы;
    // неполная загрузка (обрыв, лимит страниц, count не набран) локальные товары не стирает — только добавляет и обновляет.
    var complete = false;
    int? total;
    for (var page = 1; page <= _maxPages; page++) {
      dynamic data;
      try {
        data = await api.get('api/main/products/list/', query: {'page': page, 'page_size': _pageSize}, timeout: const Duration(seconds: 40));
      } on ApiException catch (e) {
        // страница сразу за последней бывает с ответом 500 — это конец списка, если всё обещанное уже получено
        if (page > 1 && (e.status ?? 0) >= 500 && (total == null || seen.length >= total)) {
          complete = true;
          break;
        }
        rethrow;
      }
      final items = asList(data);
      if (data is Map && data['count'] is num) total = (data['count'] as num).toInt();
      final before = seen.length;
      for (final p in items) {
        final row = _map(p);
        if (row != null && seen.add(row.id.value)) {
          rows.add(row);
          if (p.containsKey('packages')) {
            listHasPackages = true;
            // комплект (bundle): packages — его состав, а не штуки из упаковки
            final pk = str(p, 'kind') == 'bundle' ? null : PiecePack.first(p['packages']);
            if (pk != null) packs[row.id.value] = pk;
          }
          // 2026-10-22 (мобилка, админ): список каталога может не отдавать minimum_quantity — тогда прежний минимум
          // товара не сбрасываем (ниже подставляем из базы телефона).
          if (!p.containsKey('minimum_quantity') && !p.containsKey('min_quantity')) noMin.add(row.id.value);
          if (!_hasCost(p)) noCost.add(row.id.value);
        }
      }
      // дальше: есть next, либо count ещё не набран; список без next — пока страницы не пусты. Страница без новых товаров — конец
      // (сервер повторяет последнюю страницу).
      final more = data is Map ? (data['next'] != null || (total != null && seen.length < total)) : items.isNotEmpty;
      if (items.isEmpty || seen.length == before || !more) {
        complete = total == null || seen.length >= total;
        break;
      }
    }
    _fullComplete = complete;
    if (rows.isNotEmpty) {
      // 2026-10-30 (оптимизация, п. 10): раньше дважды читалась вся таблица товаров целиком (2 × 15 000 объектов) ради двух
      // полей; теперь один запрос только по id, минимуму и закупке — и только если эти поля сервер в списке не прислал.
      if (noMin.isNotEmpty || noCost.isNotEmpty) {
        final old = await db.keptFieldsById();
        if (noMin.isNotEmpty) {
          for (var i = 0; i < rows.length; i++) {
            final keep = old[rows[i].id.value]?.minQuantity;
            if (keep != null && keep > 0 && noMin.contains(rows[i].id.value)) rows[i] = rows[i].copyWith(minQuantity: Value(keep));
          }
        }
        // 2026-10-30 (ревизия в деньгах): закупка, которой в списке нет (кассиру сервер её не отдаёт), остаётся прежней
        if (noCost.isNotEmpty) {
          for (var i = 0; i < rows.length; i++) {
            final keep = old[rows[i].id.value]?.purchasePrice;
            if (keep != null && noCost.contains(rows[i].id.value)) rows[i] = rows[i].copyWith(purchasePrice: Value(keep));
          }
        }
      }
      if (complete) {
        await db.replaceProducts(rows);
        if (listHasPackages) await PackageStore.replaceAll(db, packs);
      } else {
        // неполная загрузка: ничего не стираем, добавляем и обновляем полученное
        await db.applyCatalogChanges(inserts: rows);
        if (packs.isNotEmpty) await PackageStore.replaceAll(db, {...await PackageStore.read(db), ...packs});
      }
      await _reapplyPending();
      await db.setSetting('catalog.syncedAt', DateTime.now().toIso8601String());
    }
    return rows.length;
  }

  /// 2026-10-22 (сканер, аудит С-4): товар с сервера в базу телефона по той же раскладке, что и каталог.
  static ProductsCompanion? mapProduct(Map<String, dynamic> p) => _map(p);

  /// 2026-10-22 (мобилка, деньги): каталог с сервера ещё не знает о продажах и приходах из очереди телефона — остатки
  /// «откатывались» до досылки (продал 5 без связи — через 5 минут остаток снова прежний). Не отправленное — поверх.
  Future<void> _reapplyPending({Set<String>? only}) async {
    final me = OutboxMeta.ownerOf(api.session);
    final legacy = await db.getSetting(OutboxMeta.legacyOwnerSetting);
    for (final row in await db.pending()) {
      // 2026-10-29 (срочно): неотправленное другого аккаунта остаток товаров этого аккаунта не меняет
      if (me.isNotEmpty && !OutboxMeta.belongsTo(row, me, legacyOwner: legacy)) continue;
      try {
        final body = (jsonDecode(row.body) as Map).cast<String, dynamic>();
        if (row.kind == 'sale') {
          final meta = OutboxMeta.read(row.body);
          for (final it in asList(body['items'])) {
            final id = str(it, 'product');
            final q = OutboxMeta.stockQty(it, meta); // 2026-10-27 (М4): штуки из упаковки — доля упаковки
            if (id != null && q > 0 && (only == null || only.contains(id))) await db.adjustStock(id, -q);
          }
        } else if (row.kind == 'receive') {
          for (final l in asList(body['lines'])) {
            final id = str(l, 'productId');
            final q = num0(l, 'qty');
            if (id != null && q > 0 && l['done'] != true && (only == null || only.contains(id))) await db.adjustStock(id, q);
          }
        }
      } catch (_) {
        // повреждённая запись — без поправки остатка
      }
    }
  }

  static ProductsCompanion? _map(Map<String, dynamic> p) {
    final id = str(p, 'id');
    final name = str(p, 'name') ?? str(p, 'title') ?? str(p, 'display_name');
    if (id == null || name == null) return null;
    final unit = (str(p, 'unit') ?? str(p, 'unit_display') ?? 'шт').trim();
    final u = unit.toLowerCase();
    final isWeight =
        _truthy(p, ['is_weight', 'is_wait', 'is_weigh', 'is_weight_product', 'sale_as_weight', 'sells_by_weight']) ||
        u == 'кг' ||
        u == 'kg' ||
        u == 'г' ||
        u == 'g';
    final alt = <String>[];
    final altRaw = p['alternate_barcodes'];
    if (altRaw is List) {
      for (final a in altRaw) {
        final code = a is Map ? (a['barcode'] ?? a['code'])?.toString() : a?.toString();
        if (code == null || code.trim().isEmpty) continue;
        // 13.10: доп. штрихкод упаковки (коробка, блок) — с количеством, как у кассы: «код|кол-во|название»
        final q = a is Map ? (num.tryParse('${a['quantity'] ?? ''}') ?? 0) : 0;
        alt.add(q > 1 ? '${code.trim()}|${q.toString()}|${a is Map ? (a['name'] ?? '') : ''}' : code.trim());
      }
    }
    final cat = p['category'];
    final category = cat is Map ? (cat['name']?.toString() ?? '') : (str(p, 'category_name') ?? str(p, 'category') ?? '');
    final image = productImageUrl(p);
    return ProductsCompanion.insert(
      id: id,
      name: name.trim(),
      price: Value(num0(p, 'price')),
      wholesalePrice: Value(num0(p, 'wholesale_price')),
      barcode: Value(str(p, 'barcode') ?? str(p, 'ean')),
      altBarcodes: Value(alt.join('\n')),
      sku: Value(str(p, 'sku') ?? str(p, 'article') ?? str(p, 'code')),
      plu: Value(str(p, 'plu')),
      unit: Value(unit.isEmpty ? 'шт' : unit),
      isWeight: Value(isWeight),
      isService: Value(_truthy(p, ['is_service']) || str(p, 'kind') == 'service'),
      isFavorite: Value(_truthy(p, ['is_favorite'])),
      quantity: Value(num0(p, 'quantity')),
      category: Value(category),
      imageUrl: Value(image),
      // 2026-10-29 (аудит, п. 10): ключ нормализован (ё=е, ү→у …) — SearchMatch.key
      searchKey: Value(_searchKey(name, str(p, 'sku') ?? str(p, 'article'), str(p, 'barcode'), category, str(p, 'brand_name'))),
      promoRules: Value(PromoRule.encodeFromApi(p['promotion_rules'])),
      minQuantity: Value(num0(p, 'minimum_quantity') > 0 ? num0(p, 'minimum_quantity') : num0(p, 'min_quantity')),
      // 2026-10-30 (ревизия в деньгах): закупка товара; нет в ответе — absent (null у нового, прежняя у известного)
      purchasePrice: _hasCost(p) ? Value(num0(p, 'purchase_price')) : const Value.absent(),
    );
  }

  /// 13.10, владелец: «почему в мобилке не отображаются фотки товаров?» — сервер отдаёт фото не строкой в image_url, а
  /// списком images (объекты с адресом) или вложенным объектом; искали только строки верхнего уровня, фото не находилось.
  /// Правила — как у кассы (ProductImageUrl.TryGet): все поля-кандидаты, относительный адрес — от адреса сервера,
  /// лучший кандидат — jpg/png выше webp (webp Flutter тоже показывает, но берётся, только если другого нет).
  static String? productImageUrl(Map<String, dynamic> p) {
    final candidates = <String>[];
    void add(Object? v) {
      if (v is! String) return;
      var u = v.trim();
      if (u.isEmpty) return;
      if (u.startsWith('//')) {
        u = 'https:$u';
      } else if (!u.startsWith('http://') && !u.startsWith('https://')) {
        u = '${ApiClient.defaultBaseUrl}/${u.replaceFirst(RegExp(r'^/+'), '')}';
      }
      if (!candidates.any((c) => c.toLowerCase() == u.toLowerCase())) candidates.add(u);
    }

    const urlKeys = ['url', 'src', 'image', 'file', 'path', 'thumbnail', 'full', 'download_url', 'href'];
    void addFrom(Object? v) {
      if (v is String) add(v);
      if (v is Map) {
        for (final k in urlKeys) {
          add(v[k]);
        }
      }
    }

    for (final k in [
      'primary_image_url',
      'image_url',
      'thumbnail_url',
      'photo_url',
      'picture_url',
      'main_image_url',
      'cover_url',
      'preview_url',
      'image_path',
      'media_url',
    ]) {
      add(p[k]);
    }
    for (final k in ['image', 'photo', 'thumbnail', 'picture', 'cover', 'img', 'primary_image', 'main_image', 'cover_image', 'image_data', 'photo_data']) {
      addFrom(p[k]);
    }
    for (final k in ['images', 'photos', 'gallery', 'media', 'attachments']) {
      final arr = p[k];
      if (arr is List) {
        for (final it in arr.take(8)) {
          addFrom(it);
        }
      }
    }
    if (candidates.isEmpty) return null;
    int score(String url) {
      final raw = url.toLowerCase();
      var s = raw.contains('thumb') ? 15 : 0;
      if (raw.contains('.jpg') || raw.contains('.jpeg')) {
        s += 100;
      } else if (raw.contains('.png')) {
        s += 90;
      } else if (raw.contains('.gif')) {
        s += 70;
      } else if (raw.contains('.webp')) {
        s -= 50;
      } else {
        s += 40;
      }
      return s;
    }

    candidates.sort((x, y) => score(y).compareTo(score(x)));
    return candidates.first;
  }

  static bool _truthy(Map<String, dynamic> p, List<String> keys) {
    for (final k in keys) {
      final v = p[k];
      if (v == true || v == 1 || v == 'true' || v == '1') return true;
    }
    return false;
  }

  /// 13.10, макет С5: варианты товара (размер, цвет, остаток, цена) — GET api/main/products/{id}/variants/, как у кассы.
  Future<List<ProductVariant>> variants(String productId) async {
    final data = await api.get('api/main/products/$productId/variants/', timeout: const Duration(seconds: 4));
    return [
      for (final m in asList(data))
        ProductVariant(
          str(m, 'id') ?? '',
          str(m, 'size') ?? '',
          str(m, 'color') ?? '',
          num0(m, 'quantity'),
          m['price'] == null ? null : num0(m, 'price'),
          m['is_active'] != false,
        ),
    ].where((v) => v.id.isNotEmpty).toList();
  }
}

class ProductVariant {
  final String id;
  final String size;
  final String color;
  final double quantity;
  final double? price;
  final bool active;
  const ProductVariant(this.id, this.size, this.color, this.quantity, this.price, this.active);
  String get label => [size, color].where((x) => x.isNotEmpty).join(', ');
}

/// 2026-10-29: страница ленты изменений api/main/products/changes/ — `{cursor, server_time, catalog_version, changes[], deleted[],
/// has_more}`. Имена полей терпимые (как ParseFeed кассы). Разбор не удался (не тот вид ответа) — null: ленты как будто нет.
class FeedPage {
  final List<Map<String, dynamic>> items;
  final List<String> deleted;
  final String cursor;
  final bool hasMore;
  const FeedPage(this.items, this.deleted, this.cursor, this.hasMore);

  static const _itemKeys = ['changes', 'results', 'items', 'products', 'updated', 'upserts', 'changed', 'modified'];
  static const _deletedKeys = ['deleted', 'deleted_ids', 'removed', 'removed_ids', 'deleted_products', 'deletions'];
  static const _cursorKeys = ['cursor', 'next_cursor', 'nextCursor', 'last_cursor', 'new_cursor', 'next_since'];

  static FeedPage? parse(dynamic data) {
    if (data is! Map) return null;
    final scopes = <Map<String, dynamic>>[
      data.cast<String, dynamic>(),
      if (data['data'] is Map) (data['data'] as Map).cast<String, dynamic>(),
    ];
    List? rows;
    for (final sc in scopes) {
      for (final k in _itemKeys) {
        if (sc[k] is List) {
          rows = sc[k] as List;
          break;
        }
      }
      if (rows != null) break;
    }
    var cursor = '';
    for (final sc in scopes) {
      for (final k in _cursorKeys) {
        final v = sc[k];
        if ((v is String && v.trim().isNotEmpty) || v is num) {
          cursor = '$v'.trim();
          break;
        }
      }
      if (cursor.isNotEmpty) break;
    }
    if (rows == null && cursor.isEmpty) return null; // непонятный ответ
    final items = <Map<String, dynamic>>[];
    final deleted = <String>[];
    for (final r in rows ?? const []) {
      if (r is! Map) return null; // лента из одних идентификаторов — карточек нет, применять нечего
      final m = r.cast<String, dynamic>();
      final del = m['deleted'] == true ||
          m['is_deleted'] == true ||
          (m['deleted_at'] is String && (m['deleted_at'] as String).isNotEmpty) ||
          (m['status'] is String && (m['status'] as String).toLowerCase() == 'deleted');
      if (del) {
        final id = str(m, 'id') ?? str(m, 'product_id');
        if (id != null) deleted.add(id);
      } else {
        items.add(m);
      }
    }
    for (final sc in scopes) {
      List? d;
      for (final k in _deletedKeys) {
        if (sc[k] is List) {
          d = sc[k] as List;
          break;
        }
      }
      if (d == null) continue;
      for (final x in d) {
        String? id;
        if (x is Map) {
          final xm = x.cast<String, dynamic>();
          id = str(xm, 'id') ?? str(xm, 'product_id');
        } else if (x is String || x is num) {
          id = '$x'.trim();
        }
        if (id != null && id.isNotEmpty) deleted.add(id);
      }
      break;
    }
    var hasMore = false;
    for (final sc in scopes) {
      for (final k in const ['has_more', 'hasMore', 'has_next', 'hasNext', 'more']) {
        if (sc[k] is bool) hasMore = sc[k] as bool;
      }
    }
    return FeedPage(items, deleted, cursor, hasMore);
  }
}
