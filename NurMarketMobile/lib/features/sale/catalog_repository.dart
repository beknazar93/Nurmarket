import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:flutter/foundation.dart' show ValueNotifier;

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
  static const _maxPages = 1500; // 2026-10-31: при 100 строках на странице — до 150 000 товаров (было 600 = 60 000)

  /// 2026-10-31 («в мобилке не отображаются все товары», 15 451 товар = 155 страниц ≈ 3,5 минуты подряд): страницы читаются по
  /// несколько сразу, каждая пишется в базу телефона сразу (а не одним куском в конце), а сбой страницы (429, 5xx, обрыв)
  /// повторяется. Прервалась загрузка — следующая продолжает с места обрыва, а не начинает заново.
  static const pageParallel = 3;
  static const pageRetries = 6;
  static Duration pageRetryBase = const Duration(seconds: 2); // тесты ставят ноль
  static const resumeTtl = Duration(hours: 6);
  static const resumeKey = 'catalog.resume'; // «область|следующая страница|всего|время»
  static const incompleteKey = 'catalog.incomplete'; // '1' — на телефоне не весь каталог (загрузка не дошла до конца)
  static const totalKey = 'catalog.serverTotal'; // сколько товаров насчитал сервер (count списка)
  static const gapKey = 'catalog.gap'; // «сервер − телефон» после полной загрузки: товары, которые сервер считает, но не отдаёт
  static const countAtKey = 'catalog.countAt';
  static const shortfallKey = 'catalog.shortfall'; // «всего|получено» прошлой загрузки, дошедшей до конца с недобором
  static const countEvery = Duration(minutes: 30);

  /// Ход загрузки каталога для плашки «Загружено N из M» ([CatalogProgress]); обновляется во время загрузки и после неё.
  final progress = ValueNotifier<CatalogProgress>(const CatalogProgress());

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
    // первый вход, пустая база или другой аккаунт/магазин — только целиком (ни лента, ни «версия та же» не годятся).
    // 2026-10-31: прерванная полная загрузка (на телефоне не весь каталог) тоже идёт сначала — с места обрыва, лента её не заменяет
    if (!full && await db.productCount() > 0 && await db.getSetting(scopeKey) == _scope() && !await _isIncomplete()) {
      final n = await _feedTick();
      if (n != null) {
        await publishIdle();
        return n;
      }
      if (await _versionUnchanged()) {
        await publishIdle();
        return 0;
      }
    }
    try {
      return await _fullSync(fresh: full);
    } finally {
      await publishIdle();
    }
  }

  Future<bool> _isIncomplete() async => (await db.getSetting(incompleteKey) ?? '').isNotEmpty;

  bool _restored = false;

  /// Состояние для плашки после запуска приложения (до первой синхронизации): неполный каталог виден сразу.
  Future<void> restoreProgress() async {
    if (_restored || progress.value.running) return;
    _restored = true;
    await publishIdle();
  }

  /// Записать в [progress] итог: сколько товаров на телефоне, сколько насчитал сервер, дошла ли загрузка до конца.
  Future<void> publishIdle() async {
    if (progress.value.running) return;
    try {
      final total = int.tryParse(await db.getSetting(totalKey) ?? '') ?? 0;
      final gap = int.tryParse(await db.getSetting(gapKey) ?? '') ?? 0;
      final local = await db.productCount();
      final incomplete = await _isIncomplete();
      progress.value = CatalogProgress(loaded: local, total: total > 0 ? total - (incomplete ? 0 : gap) : 0, incomplete: incomplete && local < (total > 0 ? total : local + 1));
    } catch (_) {
      // база закрыта (выход из аккаунта) — плашка просто не обновится
    }
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
    final n = await _walkFeed();
    if (n == null) return null;
    // 2026-10-31: число товаров на телефоне сверяется с сервером и между полными загрузками — телефон со 100 товарами из 15 451
    // (старая версия, оборванная загрузка) раньше ждал страховочных 12 часов
    if (await _countDiverged()) return null;
    return n;
  }

  /// 2026-10-31: сколько товаров насчитал сервер (count списка на странице из одного товара); null — сервер не сказал.
  Future<int?> _serverCount() async {
    final data = await api.get('api/main/products/list/', query: {'page': 1, 'page_size': 1}, timeout: const Duration(seconds: 15));
    final total = data is Map && data['count'] is num ? (data['count'] as num).toInt() : null;
    if (total != null) await db.setSetting(totalKey, '$total');
    return total;
  }

  /// 2026-10-31: товаров на телефоне заметно меньше (или больше), чем насчитал сервер, — нужна полная загрузка. Сравнивается разница
  /// «сервер − телефон» с той, что была после последней полной загрузки (gapKey): товары, которые сервер считает, но не отдаёт
  /// списком, не вызывают загрузку каждые полчаса. Мелкий дрейф (до 3 товаров — не успела лента) не в счёт. Проверка — раз в [countEvery],
  /// а пока разницы (gapKey) на телефоне нет (первое обновление до этой версии) — при каждом опросе.
  Future<bool> _countDiverged() async {
    try {
      final hasGap = await db.getSetting(gapKey) != null;
      final last = DateTime.tryParse(await db.getSetting(countAtKey) ?? '');
      if (hasGap && last != null && DateTime.now().difference(last) < countEvery) return false;
      final total = await _serverCount();
      if (total == null) return false;
      await db.setSetting(countAtKey, DateTime.now().toIso8601String());
      final local = await db.productCount();
      final gap = int.tryParse(await db.getSetting(gapKey) ?? '') ?? 0;
      final drift = (total - local) - gap;
      if (drift.abs() <= 3) {
        if (!hasGap) await db.setSetting(gapKey, '${total - local}');
        return false;
      }
      // защита от петли: полная загрузка закончилась недавно, а разница осталась — заново не качаем раньше чем через 2 часа
      final fullAt = DateTime.tryParse(await db.getSetting(fullAtKey) ?? '');
      if (hasGap && fullAt != null && DateTime.now().difference(fullAt) < const Duration(hours: 2)) return false;
      Audit.log('sync', 'Каталог: на телефоне $local товаров, на сервере $total (прежняя разница $gap) — полная загрузка');
      await db.setSetting(incompleteKey, '1');
      return true;
    } catch (_) {
      return false; // нет связи и т. п. — лента скажет сама
    }
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
      final total = await _serverCount();
      if (total == null) return false;
      final local = await db.productCount();
      final gap = int.tryParse(await db.getSetting(gapKey) ?? '') ?? 0; // 2026-10-31: известная разница «сервер − телефон» не повод качать заново
      if (total - local != gap) {
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

  /// Поля ответа каталога с признаком «весовой товар» (is_weight и его прежние названия).
  static const _weightFlagKeys = ['is_weight', 'is_wait', 'is_weigh', 'is_weight_product', 'sale_as_weight', 'sells_by_weight'];

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
      // 2026-11-01: чистый признак is_weight (без единицы кг/г) — для округления весовых в подытоге чека
      isWeightFlag: has(_weightFlagKeys) ? row.isWeightFlag : const Value.absent(),
      isService: has(const ['is_service', 'kind']) ? row.isService : const Value.absent(),
      isFavorite: has(const ['is_favorite']) ? row.isFavorite : const Value.absent(),
      quantity: has(const ['quantity']) ? row.quantity : const Value.absent(),
      category: has(const ['category', 'category_name']) ? row.category : const Value.absent(),
      imageUrl: Value(row.imageUrl.value ?? prev.imageUrl),
      searchKey: Value(_searchKey(row.name.value, sku, barcode, category, str(m, 'brand_name'))),
      promoRules: has(const ['promotion_rules']) ? row.promoRules : const Value.absent(),
      minQuantity: has(const ['minimum_quantity', 'min_quantity']) ? row.minQuantity : const Value.absent(),
      purchasePrice: row.purchasePrice, // 2026-10-30: absent, если в карточке закупки нет (_map)
      expiresOn: has(const ['expiration_date']) ? row.expiresOn : const Value.absent(), // 2026-10-31: срок годности
    );
  }

  /// 2026-10-31: срок годности из ответа сервера → «ГГГГ-ММ-ДД»; пусто/мусор → null.
  static String? parseExpiry(Object? v) {
    if (v is! String || v.trim().length < 8) return null;
    final d = DateTime.tryParse(v.trim());
    if (d == null) return null;
    String two(int x) => x.toString().padLeft(2, '0');
    return '${d.year}-${two(d.month)}-${two(d.day)}';
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

  Future<int> _fullSync({bool fresh = false}) async {
    String? version;
    try {
      version = await _readVersion();
    } on ApiException catch (e) {
      if (e.status == null) rethrow; // нет связи — полная загрузка всё равно не пройдёт
    } catch (_) {
      // версия — только запасной путь
    }
    final n = await _fullDownload(fresh: fresh);
    if (_fullComplete && (n > 0 || await db.productCount() > 0)) {
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

  /// «область|следующая страница|всего|время» → запись для докачки; пусто или битая строка — null.
  static ({String scope, int page, int total, DateTime at})? _parseResume(String? raw) {
    if (raw == null || raw.isEmpty) return null;
    final parts = raw.split('|');
    if (parts.length < 4) return null;
    // область («почта|магазин|сервер») сама содержит «|»: страница, всего и время — последние три поля
    final at = DateTime.tryParse(parts[parts.length - 1]);
    final total = int.tryParse(parts[parts.length - 2]);
    final page = int.tryParse(parts[parts.length - 3]);
    if (at == null || total == null || page == null) return null;
    return (scope: parts.sublist(0, parts.length - 3).join('|'), page: page, total: total, at: at);
  }

  /// Одна страница списка: сбой (429, 5xx, обрыв) повторяется с нарастающей паузой; без связи вовсе — только два повтора, чтобы
  /// не висеть минутами. Возвращает ответ или ошибку — не бросает (страницы идут пачкой параллельно).
  Future<({dynamic data, ApiException? error})> _fetchPage(int page, bool Function(ApiException e) isEnd) async {
    for (var attempt = 1; ; attempt++) {
      try {
        final data = await api.get('api/main/products/list/', query: {'page': page, 'page_size': _pageSize}, timeout: const Duration(seconds: 40));
        return (data: data, error: null);
      } on ApiException catch (e) {
        final maxAttempts = e.status == null ? 3 : pageRetries;
        final code = e.status;
        final transient = code == null || code == 408 || code == 429 || code >= 500; // 409/423/425 к списку товаров не относятся
        if (isEnd(e) || !transient || attempt >= maxAttempts) return (data: null, error: e);
        await Future<void>.delayed(pageRetryBase * attempt);
      }
    }
  }

  Future<int> _fullDownload({bool fresh = false}) async {
    final seen = <String>{};
    final noMin = <String>{};
    final noCost = <String>{}; // 2026-10-30: товары, у которых список закупку не прислал — прежнюю закупку не стираем
    // 2026-10-27 (М4): поштучная продажа из упаковки (packages) по товарам; listHasPackages — список вообще отдаёт это поле
    final packs = <String, PiecePack>{};
    var listHasPackages = false;
    Map<String, ({double minQuantity, double? purchasePrice})>? kept;
    var rejected = 0; // карточки без названия/id — в базу не попадают
    var written = 0;
    // 2026-10-29 (срочно, «Скорость синхронизации»): сервер отдаёт не больше 100 строк на страницу, а цикл останавливался, раз пришло
    // меньше 300 — грузилось 100 товаров из 369, остальные стирались. Теперь идём до конца по next/count, а не по размеру страницы;
    // неполная загрузка (обрыв, лимит страниц, count не набран) локальные товары не стирает — только добавляет и обновляет.
    // 2026-10-31: страницы пишутся в базу по мере чтения, обрыв оставляет «докачку» (resumeKey), а не потерю всего.
    var complete = false;
    int? total;
    final scope = _scope();
    // база телефона была полной до этой загрузки (плановая страховочная, 12 ч): сбой не должен останавливать ленту → докачку не ведём
    final wasComplete = await db.getSetting(scopeKey) == scope && DateTime.tryParse(await db.getSetting(fullAtKey) ?? '') != null && !await _isIncomplete();
    final resume = wasComplete ? null : _parseResume(await db.getSetting(resumeKey));
    var startPage = 1;
    if (resume != null && resume.scope == scope && resume.page > 1 && DateTime.now().difference(resume.at) < resumeTtl) {
      startPage = resume.page;
      total = resume.total > 0 ? resume.total : null;
    }
    final resumed = startPage > 1;
    final base = resumed ? await db.productCount() : 0; // уже лежащие на телефоне с прошлой попытки
    int haveNow() => base + seen.length;
    progress.value = CatalogProgress(running: true, loaded: base, total: total ?? 0);
    var nextPage = startPage;
    var expectedLast = 0; // последняя страница по count и размеру первой; 0 — пока неизвестна
    var endReached = false;
    var failed = true;
    try {
      var page = startPage;
      while (page <= _maxPages && !endReached) {
        final batch = <int>[page];
        for (var k = page + 1; expectedLast > 0 && k < page + pageParallel && k <= expectedLast; k++) {
          batch.add(k);
        }
        // страница за концом списка бывает с ответом 500 — это конец, если всё обещанное уже получено
        final first = batch.first;
        bool isEnd(ApiException e) => first > 1 && (e.status ?? 0) >= 500 && (total == null || haveNow() >= total);
        final results = await Future.wait([for (final pg in batch) _fetchPage(pg, isEnd)]);
        for (var i = 0; i < batch.length; i++) {
          final pg = batch[i];
          final err = results[i].error;
          if (err != null) {
            if (pg > 1 && (err.status ?? 0) >= 500 && (total == null || haveNow() >= total)) {
              complete = true;
              endReached = true;
              break;
            }
            throw err; // finally сохранит место, откуда продолжить
          }
          final data = results[i].data;
          final items = asList(data);
          if (data is Map && data['count'] is num) total = (data['count'] as num).toInt();
          if (expectedLast == 0 && pg == startPage && items.isNotEmpty && total != null && total > items.length) {
            expectedLast = (total / items.length).ceil();
          }
          final before = seen.length;
          final chunk = <ProductsCompanion>[];
          for (final p in items) {
            final row = _map(p);
            if (row == null) {
              rejected++;
              continue;
            }
            if (seen.add(row.id.value)) {
              chunk.add(row);
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
          if (chunk.isNotEmpty) {
            // 2026-10-30 (оптимизация, п. 10): минимум и закупка прежних товаров читаются одним запросом по id и только если эти поля
            // сервер в списке не прислал; 2026-10-31: читаются один раз, до первой записи страницы
            if ((noMin.isNotEmpty || noCost.isNotEmpty) && kept == null) kept = await db.keptFieldsById();
            final old = kept;
            if (old != null) {
              for (var j = 0; j < chunk.length; j++) {
                final id = chunk[j].id.value;
                final prev = old[id];
                if (prev == null) continue;
                // 2026-10-30 (ревизия в деньгах): закупка, которой в списке нет (кассиру сервер её не отдаёт), остаётся прежней
                if (noMin.contains(id) && prev.minQuantity > 0) chunk[j] = chunk[j].copyWith(minQuantity: Value(prev.minQuantity));
                if (noCost.contains(id) && prev.purchasePrice != null) chunk[j] = chunk[j].copyWith(purchasePrice: Value(prev.purchasePrice));
              }
            }
            await db.applyCatalogChanges(inserts: chunk);
            await _reapplyPending(only: {for (final c in chunk) c.id.value});
            written += chunk.length;
          }
          nextPage = pg + 1;
          if (!wasComplete) await db.setSetting(resumeKey, '$scope|$nextPage|${total ?? 0}|${DateTime.now().toIso8601String()}');
          progress.value = CatalogProgress(running: true, loaded: haveNow(), total: total ?? 0);
          // дальше: есть next, либо count ещё не набран; список без next — пока страницы не пусты. Страница без новых товаров — конец
          // (сервер повторяет последнюю страницу).
          final more = data is Map ? (data['next'] != null || (total != null && haveNow() < total)) : items.isNotEmpty;
          if (items.isEmpty || seen.length == before || !more) {
            complete = total == null || haveNow() >= total;
            if (!complete) complete = await _acceptShortfall(total, haveNow());
            endReached = true;
            break;
          }
        }
        page += batch.length;
      }
      failed = false;
    } finally {
      // упаковки: полная загрузка заменяет их все, неполная и докачка — добавляют к лежащим
      if (listHasPackages || packs.isNotEmpty) {
        final fullReplace = complete && !resumed;
        await PackageStore.replaceAll(db, fullReplace ? packs : {...await PackageStore.read(db), ...packs});
      }
      if (failed) {
        // обрыв/ошибка: товары уже записаны постранично; на телефоне не весь каталог — плашка и докачка с этой страницы
        if (!wasComplete) {
          await db.setSetting(incompleteKey, '1');
          await db.setSetting(resumeKey, '$scope|$nextPage|${total ?? 0}|${DateTime.now().toIso8601String()}');
        }
        if (total != null) await db.setSetting(totalKey, '$total');
        progress.value = const CatalogProgress();
      }
    }
    if (seen.isEmpty && !resumed) complete = false; // пустой ответ — не повод считать каталог загруженным или стирать телефон
    _fullComplete = complete;
    if (total != null) await db.setSetting(totalKey, '$total');
    if (complete) {
      // полный список получен: товары, которых в нём нет, удалены на сервере — убираем и с телефона (раньше это делал replaceProducts)
      if (!resumed) await _deleteMissing(seen);
      await db.setSetting(resumeKey, '');
      await db.setSetting(incompleteKey, '');
      await db.setSetting(shortfallKey, '');
      await db.setSetting(gapKey, '${(total ?? 0) > 0 ? total! - await db.productCount() : 0}');
      await db.setSetting(countAtKey, DateTime.now().toIso8601String());
    } else {
      // дошли до конца списка, но товаров меньше, чем насчитал сервер (или упёрлись в лимит страниц): на телефоне неполный каталог
      await db.setSetting(resumeKey, ''); // докачивать нечего — следующая попытка начнёт с первой страницы
      if (seen.isNotEmpty) {
        if (!wasComplete) await db.setSetting(incompleteKey, '1');
        Audit.log('sync', 'Каталог: загружено ${haveNow()} из ${total ?? '?'}${rejected > 0 ? ' (без названия: $rejected)' : ''} — загрузка не завершена');
      }
    }
    if (written > 0) await db.setSetting('catalog.syncedAt', DateTime.now().toIso8601String());
    progress.value = const CatalogProgress();
    return written;
  }

  /// Загрузка дошла до конца списка, но получено меньше, чем сказал count. Два раза подряд одно и то же («всего|получено») —
  /// сервер считает товары, которых не отдаёт (удалённые, служебные): принимаем как полную, иначе каталог качался бы каждые 5 минут.
  Future<bool> _acceptShortfall(int total, int got) async {
    final key = '$total|$got';
    if (await db.getSetting(shortfallKey) == key) return true;
    await db.setSetting(shortfallKey, key);
    return false;
  }

  /// Удалить с телефона товары, которых нет среди [seen] (полная загрузка без докачки). Пачками — у SQLite лимит параметров.
  Future<void> _deleteMissing(Set<String> seen) async {
    final gone = [for (final id in await db.allProductIds()) if (!seen.contains(id)) id];
    if (gone.isNotEmpty) await db.applyCatalogChanges(deletedIds: gone);
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
      // 2026-11-01: чистый is_weight сервера отдельно от единицы кг/г; поля в ответе нет — null (весовой по isWeight, как раньше)
      isWeightFlag: _hasAny(p, _weightFlagKeys) ? Value(_truthy(p, _weightFlagKeys)) : const Value.absent(),
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
      expiresOn: p.containsKey('expiration_date') ? Value(parseExpiry(p['expiration_date'])) : const Value.absent(),
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

/// 2026-10-31: ход загрузки каталога. [running] — идёт полная загрузка ([loaded] из [total] товаров); иначе [loaded] — сколько товаров
/// на телефоне, [total] — сколько насчитал сервер (0 — не знаем), [incomplete] — прошлая загрузка не дошла до конца.
class CatalogProgress {
  final bool running;
  final int loaded;
  final int total;
  final bool incomplete;
  const CatalogProgress({this.running = false, this.loaded = 0, this.total = 0, this.incomplete = false});
}

class ProductVariant {
  final String id;
  final String size;
  final String color;
  final double quantity;
  final double? price;
  final bool active;

  /// 2026-10-31 (одежда): штрихкод размера и товар, которому он принадлежит.
  final String? barcode;
  final String? productId;
  const ProductVariant(this.id, this.size, this.color, this.quantity, this.price, this.active, {this.barcode, this.productId});
  String get label => [size, color].where((x) => x.isNotEmpty).join(', ');

  /// Подпись для строки чека: «M · Красный».
  String get lineLabel => [size, color].where((x) => x.isNotEmpty).join(' · ');
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
