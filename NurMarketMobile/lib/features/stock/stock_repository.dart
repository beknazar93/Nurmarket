import 'dart:async';
import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:uuid/uuid.dart';

import '../../core/api/api_client.dart';
import '../../core/catalog/search_match.dart';
import '../../core/db/database.dart';
import '../../core/sync/outbox_meta.dart';
import '../scan/invoice_bind.dart';

class StockLine {
  final String productId;
  final String name;
  final double before;
  double qty;

  /// 2026-11-08: единица товара из карточки («л», «кг», «м»…) — только для подписей приёмки; на сервер уходит прежнее (productId, qty).
  final String unit;
  StockLine({required this.productId, required this.name, required this.before, this.qty = 1, this.unit = 'шт'});

  Map<String, dynamic> toJson() => {'productId': productId, 'name': name, 'qty': qty, 'done': false};
}

/// 2026-10-22 (мобилка, деньги, К-7): исход прибавки остатка неясен — прошлый PATCH остался без ответа, а остаток
/// с тех пор изменился. Повторно не прибавляем (как PendingStockPatch кассы), запись — «Не принято» с подсказкой.
class StockUnclearException extends ApiException {
  StockUnclearException(super.message) : super(status: 409);
  @override
  bool get isTransient => false;
}

/// 2026-11-21 (ответ сервера): 409 `barcode_duplicate` — код уже у другого товара; data: product_id, name, code, source (product | alternate), barcode.
class BarcodeDuplicate {
  final String productId;
  final String name;
  final String code;

  /// `product` — основной код того товара, `alternate` — его дополнительный.
  final String source;
  const BarcodeDuplicate({required this.productId, required this.name, required this.code, required this.source});

  static Map<String, dynamic>? _map(Object? v) => v is Map ? v.cast<String, dynamic>() : null;

  /// Тело ошибки: `{code: barcode_duplicate, data: {…}}` (допускается вложение в `error`). null — это другая ошибка.
  static BarcodeDuplicate? from(Object e) {
    if (e is! ApiException || e.status != 409) return null;
    final p = _map(e.payload);
    final root = _map(p?['error']) ?? p;
    if (root == null || '${root['code']}' != 'barcode_duplicate') return null;
    final d = _map(root['data']) ?? root;
    return BarcodeDuplicate(
      productId: '${d['product_id'] ?? ''}',
      name: '${d['name'] ?? ''}',
      code: '${d['barcode'] ?? d['code'] ?? ''}',
      source: '${d['source'] ?? 'product'}',
    );
  }

  /// 409 `version_conflict` (If-Match не совпал): карточку перечитать и повторить.
  static bool isVersionConflict(Object e) {
    if (e is! ApiException || e.status != 409) return false;
    final p = _map(e.payload);
    final root = _map(p?['error']) ?? p;
    return '${root?['code']}' == 'version_conflict';
  }
}

/// Код занят другим товаром: отказ по существу (409 сам по себе «временный», тут повтор не поможет).
class BarcodeDuplicateException extends ApiException {
  final BarcodeDuplicate info;
  BarcodeDuplicateException(super.message, this.info) : super(status: 409);
  @override
  bool get isTransient => false;
}

/// 2026-10-29 (аудит мобилки, п. 6): ревизия не проведена вовремя — из очереди её не проводим (остатки могли измениться).
class StaleAuditException extends ApiException {
  StaleAuditException(super.message) : super(status: 409);
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

  /// 2026-10-29: сервер ответил 404/405 на mass-incoming — приход идёт прежним путём (прибавка к свежему остатку).
  bool _noMassIncoming = false;

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

  /// 2026-11-20 (владелец: «у товара в накладной нет штрихкода — сканировать и привязать»): штрихкод существующему товару — запись в
  /// очереди (kind 'barcode'), как приход и ревизия: на телефоне товар получает код сразу (его находит сканер и без связи), на сервер
  /// код уходит при первой возможности (фоновая очередь SyncService). Основной код, если у товара его нет, иначе дополнительный
  /// ([planBarcodeBind]). Повтор того же кода тому же товару — та же запись, второй не создаётся. false — писать нечего (код уже у товара).
  /// 2026-11-20: [name] — название варианта («клубника»), если код дополнительный (вариант одного товара): уходит в `alternate_barcodes`.
  Future<bool> queueBarcodeBind(Product p, String code, {String name = ''}) async {
    final c = code.trim();
    if (c.isEmpty || planBarcodeBind(p, c) == BindPlan.already) return false;
    final id = 'bc-${p.id}-${sameCodeKey(c)}';
    final old = await _row(id);
    if (old != null && old.status != 'failed') {
      await _writeLocalBind(p.id, c, name: name.trim());
      return true;
    }
    if (old != null) await (db.delete(db.outbox)..where((o) => o.id.equals(id))).go(); // «Не принято» — записываем заново
    await db.enqueue(
      OutboxCompanion.insert(
        id: id,
        kind: 'barcode',
        body: jsonEncode({'product_id': p.id, 'code': c, if (name.trim().isNotEmpty) 'name': name.trim(), OutboxMeta.key: _meta()}),
        createdAt: DateTime.now(),
        summary: Value('${p.name} · $c'),
      ),
    );
    await _writeLocalBind(p.id, c, name: name.trim());
    final row = await _row(id);
    if (row != null) {
      // сразу, если есть связь; нет связи — запись остаётся в очереди, дошлёт SyncService
      unawaited(() async {
        try {
          await replayBarcodeBind(row);
        } on ApiException catch (e) {
          await db.markAttempt(id, e.message, failed: !e.isTransient);
        } catch (_) {
          // повторит очередь
        }
      }());
    }
    return true;
  }

  /// Код в базе телефона: основным или дополнительным (по состоянию товара сейчас), и в ключе поиска.
  Future<void> _writeLocalBind(String productId, String code, {String name = ''}) async {
    final local = await db.productById(productId);
    if (local == null) return;
    switch (planBarcodeBind(local, code)) {
      case BindPlan.already:
        return;
      case BindPlan.main:
        await (db.update(db.products)..where((p) => p.id.equals(productId))).write(
          ProductsCompanion(barcode: Value(code), searchKey: Value(SearchMatch.key([local.searchKey, code]))),
        );
      case BindPlan.alt:
        final alt = [...local.altBarcodes.split('\n').where((l) => l.trim().isNotEmpty), name.isEmpty ? code : '$code|0|$name'];
        await (db.update(db.products)..where((p) => p.id.equals(productId))).write(ProductsCompanion(altBarcodes: Value(alt.join('\n'))));
    }
  }

  /// Версия карточки для If-Match (в GET products/{id}/: version или ETag).
  static String? _versionOf(Map<String, dynamic> card) {
    for (final k in const ['version', 'etag', 'ETag', 'row_version']) {
      final v = card[k];
      if (v != null && '$v'.trim().isNotEmpty) return '$v'.trim();
    }
    return null;
  }

  /// 2026-11-21 (Умар): POST products/{id}/barcodes/ добавляет ОДИН код, не трогая остальные; If-Match: версия карточки; 409
  /// `version_conflict` — карточку перечитать и повторить один раз; 404/405 (сервер без этого пути) — прежний PATCH `alternate_barcodes`
  /// списком целиком; повтор того же кода сервер принимает (200).
  Future<void> _addAltCode(String id, String code, String name, Map<String, dynamic> card, List<Map<String, Object?>> alt) async {
    var cur = card;
    for (var attempt = 0;; attempt++) {
      final v = _versionOf(cur);
      try {
        await api.post('api/main/products/$id/barcodes/', body: altBarcodeBody(code, name: name), headers: {'If-Match': ?v});
        return;
      } on ApiException catch (e) {
        if (e.status == 404 || e.status == 405) {
          await api.patch('api/main/products/$id/', body: {
            'alternate_barcodes': [...alt, altBarcodeBody(code, name: name)],
          });
          return;
        }
        if (attempt == 0 && BarcodeDuplicate.isVersionConflict(e)) {
          cur = ((await api.get('api/main/products/$id/')) as Map).cast<String, dynamic>();
          if (_hasCode(cur, code)) return; // за это время код добавили (с кассы, другим телефоном)
          continue;
        }
        rethrow;
      }
    }
  }

  static bool _hasCode(Map<String, dynamic> card, String code) {
    final key = sameCodeKey(code);
    if (sameCodeKey('${card['barcode'] ?? ''}') == key) return true;
    final raw = card['alternate_barcodes'];
    return raw is List && raw.any((a) => sameCodeKey('${a is Map ? (a['barcode'] ?? a['code'] ?? '') : a}') == key);
  }

  /// Сервер не принял код: убираем его у товара на телефоне (иначе сканер находил бы товар по коду, которого у него нет).
  Future<void> _revertLocalBind(String productId, String code) async {
    final local = await db.productById(productId);
    if (local == null) return;
    final key = sameCodeKey(code);
    if (sameCodeKey(local.barcode ?? '') == key) {
      await (db.update(db.products)..where((p) => p.id.equals(productId))).write(const ProductsCompanion(barcode: Value(null)));
    }
    final alt = local.altBarcodes.split('\n').where((l) => l.trim().isNotEmpty && sameCodeKey(l.split('|').first) != key).toList();
    if (alt.length != local.altBarcodes.split('\n').where((l) => l.trim().isNotEmpty).length) {
      await (db.update(db.products)..where((p) => p.id.equals(productId))).write(ProductsCompanion(altBarcodes: Value(alt.join('\n'))));
    }
  }

  /// Отправка записи 'barcode': свежая карточка с сервера → код уже там? (тогда готово) → основной код, если пуст, иначе дополнительный
  /// (список дополнительных передаётся целиком, как в карточке товара). Код занят другим товаром — сервер откажет (400): «Не принято» с причиной.
  Future<void> replayBarcodeBind(OutboxData row) => _once(row.id, () async {
    final fresh = await _row(row.id);
    if (fresh == null || fresh.status == 'sent') return;
    final body = (jsonDecode(fresh.body) as Map).cast<String, dynamic>();
    final id = body['product_id'] as String;
    final code = body['code'] as String;
    final variantName = '${body['name'] ?? ''}';
    final server = ((await api.get('api/main/products/$id/')) as Map).cast<String, dynamic>();
    final serverMain = (str(server, 'barcode') ?? '').trim();
    final alt = <Map<String, Object?>>[];
    final raw = server['alternate_barcodes'];
    if (raw is List) {
      for (final a in raw) {
        final m = a is Map ? a.cast<String, dynamic>() : <String, dynamic>{'barcode': a};
        final b = '${m['barcode'] ?? m['code'] ?? ''}'.trim();
        if (b.isEmpty) continue;
        alt.add(altBarcodeBody(b, name: '${m['name'] ?? ''}', qty: num.tryParse('${m['quantity'] ?? ''}')));
      }
    }
    final key = sameCodeKey(code);
    final has = (serverMain.isNotEmpty && sameCodeKey(serverMain) == key) || alt.any((a) => sameCodeKey('${a['barcode']}') == key);
    if (!has) {
      try {
        if (serverMain.isEmpty) {
          await api.patch('api/main/products/$id/', body: {'barcode': code}); // основного кода нет — он и становится основным
        } else {
          await _addAltCode(id, code, variantName, server, alt);
        }
      } on ApiException catch (e) {
        // 2026-11-21: код уже у другого товара — понятная причина с названием из ответа сервера (не искать в каталоге)
        final dup = BarcodeDuplicate.from(e);
        if (dup != null) {
          await _revertLocalBind(id, code);
          final text = ApiText.l?.bindTakenServer(code, dup.name) ?? 'Штрихкод $code уже у товара «${dup.name}»';
          throw BarcodeDuplicateException(text, dup);
        }
        if (!e.isTransient) await _revertLocalBind(id, code); // сервер отказал по существу — на телефоне кода у товара быть не должно
        rethrow;
      }
    }
    await _writeLocalBind(id, code);
    await db.markSent(row.id, serverId: id);
  });

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
    // 2026-10-29 (аудит мобилки, п. 7): строки без начатой старой прибавки (target) — одним запросом POST products/mass-incoming/
    // с Idempotency-Key: сервер сам прибавляет к актуальному остатку в одной транзакции. Раньше остаток писался абсолютным
    // числом «прочитанное + принятое» — продажа на кассе между чтением и записью затиралась. Повтор тем же ключом не удваивает.
    // Сервер без mass-incoming (404/405) — прежний путь ниже (прибавка к свежему остатку по строке).
    final fresh0 = lines.where((l) => l['done'] != true && l['target'] is! num).toList();
    if (fresh0.isNotEmpty && !_noMassIncoming) {
      final key = lines.where((l) => l['done'] == true).isEmpty ? row.id : '${row.id}-${fresh0.length}';
      try {
        await api.post(
          'api/main/products/mass-incoming/',
          body: {
            'items': [
              for (final l in fresh0) {'product_id': l['productId'], 'quantity': _fmt((l['qty'] as num).toDouble())},
            ],
          },
          headers: {'Idempotency-Key': key},
          timeout: const Duration(seconds: 30),
        );
        for (final l in fresh0) {
          l['done'] = true;
        }
        await _saveBody(fresh, body);
      } on ApiException catch (e) {
        if (e.status != 404 && e.status != 405) rethrow;
        _noMassIncoming = true; // сервер не знает mass-incoming — дальше прежним путём
      }
    }
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

  /// 2026-11-05 (аудит №4, М6-09): «5.00» как и раньше, а вес с долями грамма — три знака («1.235»), а не округление до двух.
  static String _fact(double v) {
    final two = v.toStringAsFixed(2);
    return double.parse(two) == v ? two : v.toStringAsFixed(3);
  }

  static String _fmt(double v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(3);

  /// 2026-10-22 (мобилка, деньги, К-10): ревизия кассира проводится, как на ПК (WarehouseViewModel): акт
  /// инвентаризации (POST api/main/inventory/sessions/) и сразу проведение (POST …/{id}/apply/, allow_negative: false) —
  /// сервер сам считает разницу с остатком. Раньше уходил только черновик «администратору», а провести его было негде.
  /// Только при связи: пересчёт, проведённый часами позже, стёр бы продажи между пересчётом и отправкой.
  /// id созданного акта сохраняется в записи — повтор после обрыва не создаёт второй акт и не проводит его дважды.
  /// Возврат: true — проведено; false — акт создан, но НЕ проведён (2026-10-29: позже сам не проведётся — пересчитать заново).
  Future<bool> applyAudit(String note, Map<String, double> counted) async {
    final key = const Uuid().v4();
    final body = {
      'note': note,
      // 2026-11-05 (аудит №4, М6-09): три знака веса (1,235 кг) — раньше два, и остаток телефона расходился с сервером
      'items': counted.entries.map((e) => {'product_id': e.key, 'quantity_fact': _fact(e.value)}).toList(),
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
        // 2026-10-29 (аудит мобилки, п. 6): ответ на «провести» мог потеряться — смотрим статус акта. Не проведён — позже сам не
        // проведётся (очередь ревизию не проводит), запись «Не принято»: пересчитать заново, акт остался черновиком.
        final sid = OutboxMeta.read(now.body)?['session'] as String?;
        if (sid != null && await _sessionApplied(sid)) {
          await db.markSent(key, serverId: sid);
          for (final c in counted.entries) {
            await db.setStock(c.key, c.value);
          }
          return true;
        }
        await db.markAttempt(key, ApiText.auditStale, failed: true);
        return false;
      }
      // Акт не создан (нет связи или отказ) — запись убираем, пересчёт остаётся на экране.
      await (db.delete(db.outbox)..where((o) => o.id.equals(key))).go();
      rethrow;
    }
  }

  /// [fromQueue] — фоновая очередь (SyncService). 2026-10-29 (аудит мобилки, п. 6): очередь ревизию НЕ проводит и акт не создаёт —
  /// пересчёт часами позже затёр бы продажи между пересчётом и проведением. Акт, который успели провести (ответ потерялся),
  /// помечается отправленным; иначе запись «Не принято» с подсказкой «пересчитайте заново», акт остаётся черновиком.
  Future<void> replayAudit(OutboxData row, {bool fromQueue = false}) => _once(row.id, () async {
    final fresh = await _row(row.id);
    if (fresh == null || fresh.status == 'sent') return;
    final raw = (jsonDecode(fresh.body) as Map).cast<String, dynamic>();
    final meta = (raw[OutboxMeta.key] is Map ? (raw[OutboxMeta.key] as Map).cast<String, dynamic>() : <String, dynamic>{});
    var sessionId = meta['session'] as String?;
    if (fromQueue) {
      if (sessionId != null && meta['apply'] == true && await _sessionApplied(sessionId)) {
        await db.markSent(row.id, serverId: sessionId);
        return;
      }
      throw StaleAuditException(ApiText.auditStale);
    }
    if (sessionId == null) {
      // 2026-10-29: ключ повтора акта — потерянный ответ + повтор не создают второй черновик (ключ = id записи очереди).
      final resp = await api.post('api/main/inventory/sessions/', body: OutboxMeta.strip(raw), headers: {'Idempotency-Key': row.id});
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
