import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:uuid/uuid.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/sync/outbox_meta.dart';
import '../../core/util/audit.dart';
import '../sale/cart.dart';
import 'receipt_qr.dart';

/// Способ оплаты: как у кассы (cash | transfer | debt). QR и терминал — часть 21 бэкенда, этап 3.
enum PayMethod { cash, card, debt }

extension PayMethodApi on PayMethod {
  String get api => switch (this) {
    PayMethod.cash => 'cash',
    PayMethod.card => 'transfer',
    PayMethod.debt => 'debt',
  };
}

class Client {
  final String id;
  final String name;
  final String phone;

  /// 2026-10-21: для КП и счёта — компания (поле llc NurCRM) и ИНН покупателя.
  final String company;
  final String inn;

  /// 2026-10-26 (живой аудит, П4): текущий долг клиента (из списка должников) — окно выбора и оплата в долг его показывают.
  final double debt;
  const Client(this.id, this.name, this.phone, {this.company = '', this.inn = '', this.debt = 0});
  String get display => phone.isEmpty ? name : '$name · $phone';
  Client withDebt(double d) => Client(id, name, phone, company: company, inn: inn, debt: d);
}

/// 2026-10-22 (мобилка, деньги): сервер не ответил на продажу, которую нельзя поставить в очередь (долг, бонусы) —
/// она МОГЛА пройти. Ключ попытки сохранён: повтор той же оплаты сервер второй раз не проведёт.
class CheckoutUnknownException extends ApiException {
  final bool debt;
  CheckoutUnknownException(super.message, {this.debt = false});
}

/// 2026-10-29 (аудит мобилки, п. 8): строка чека, цена которой на сервере другая (было → стало).
class PriceChange {
  final String productId;
  final String name;
  final double oldPrice;
  final double newPrice;

  /// Новые розничная и оптовая цены товара (для обновления строки чека); null — сервер их не прислал.
  final double? retail;
  final double? wholesale;
  const PriceChange(this.productId, this.name, this.oldPrice, this.newPrice, {this.retail, this.wholesale});
}

/// 2026-10-29 (аудит мобилки, п. 8): сервер отказал в продаже — 409 price_changed (строгая проверка цены,
/// заголовок X-NurMarket-Strict-Price). Продажа НЕ проведена: ключ забыт, цены нужно обновить и оплатить заново.
class PriceChangedException extends ApiException {
  final List<PriceChange> changes;
  PriceChangedException(super.message, {this.changes = const []}) : super(status: 409);
  @override
  bool get isTransient => false;
}

/// 2026-10-29 (аудит мобилки, п. 8): итог сверки цен чека с сервером перед оплатой.
class PriceCheck {
  final List<PriceChange> changes;

  /// Сервер не ответил ни по одному товару — цена в чеке может быть устаревшей.
  final bool offline;
  const PriceCheck(this.changes, {this.offline = false});
}

/// 2026-10-27 (ревью М-6): расхождение остатка после продажи штуки из упаковки.
class PackStockMismatch {
  final String productId;
  final String name;

  /// Ожидали (остаток телефона до продажи минус доля упаковки) и получили от сервера — в упаковках.
  final double expected;
  final double actual;
  const PackStockMismatch(this.productId, this.name, this.expected, this.actual);
}

class CheckoutResult {
  final bool queued;
  final String key;
  final String? number;
  final double total;

  /// Ссылка на электронный чек, если сервер её выдал (часть 20, раздел 8).
  final String? receiptUrl;

  /// Ответ сервера целиком — для чека: смена, фискальные реквизиты (13.10).
  final Map<String, dynamic>? response;

  /// 2026-10-27 (ревью М-6): товары, у которых остаток на сервере после продажи штуки из пачки не совпал с ожидаемым
  /// (штука должна списать долю пачки, а не пачку). Пусто — совпало или проверить не удалось. Экран «Оплачено» показывает жёлтую плашку.
  final List<PackStockMismatch> stockWarnings;

  /// 2026-10-29 (аудит мобилки, п. 1): сервер ответил 409 idempotency_conflict — продажа с этим ключом у него уже есть,
  /// номер и сумма неизвестны. Это «проведено, сверьте в «Чеки»», а не «не принято».
  final bool reconcile;
  const CheckoutResult({required this.queued, required this.key, this.number, required this.total, this.receiptUrl, this.response, this.stockWarnings = const [], this.reconcile = false});
}

/// Продажа одним запросом: POST api/main/pos/checkout/ с заголовком Idempotency-Key.
/// Повтор с тем же ключом сервер не проводит второй раз — поэтому без связи чек встаёт в очередь
/// с этим же ключом и досылается им же (как SyncService.QuickReplay кассы).
class SalesRepository {
  final ApiClient api;
  final AppDatabase db;
  SalesRepository(this.api, this.db);

  static Map<String, dynamic> buildBody({required Cart cart, required String shiftId, required PayMethod method, double? received, String? clientId}) {
    // Строки одного товара по одной цене — одной позицией.
    final merged = <String, Map<String, dynamic>>{};
    final items = <Map<String, dynamic>>[];
    for (final l in cart.lines) {
      if (l.custom) {
        items.add({'custom': true, 'name': l.name, 'price': l.price.toStringAsFixed(2), 'qty': _qty(l)});
        continue;
      }
      // 2026-10-27 (М4): штука из упаковки — отдельная позиция (своя упаковка), как PushGroupKey кассы
      final key = '${l.productId}|${l.price.toStringAsFixed(2)}|${l.variantId ?? ''}${l.salePackageId == null ? '' : '|${l.salePackageId}'}';
      final existing = merged[key];
      if (existing != null) {
        existing['qty'] = (double.parse(existing['qty'] as String) + l.qty).toStringAsFixed(l.isWeight ? 3 : 0);
        if (!l.hasPromo && l.discount > 0) existing['discount'] = ((double.tryParse(existing['discount'] as String? ?? '0') ?? 0) + l.discount).toStringAsFixed(2);
        continue;
      }
      final line = <String, dynamic>{'product': l.productId, 'qty': _qty(l), 'price': l.price.toStringAsFixed(2)};
      // 13.10: строка по оптовой цене — как у кассы (is_wholesale в строке чека).
      if (l.wholesale) line['is_wholesale'] = true;
      // 13.10: размер/цвет одежды — поле variant, как у кассы (QuickCheckoutBody).
      if (l.variantId != null) line['variant'] = l.variantId;
      // 2026-10-27 (М4): штука из упаковки (количество — штуки, цена — цена штуки): sale_package_id, как строка чека кассы
      // (StagingCartService.PushItemsFromSnapshotAsync); price_override — цена строки намеренно не равна цене товара
      // (QuickCheckoutBody.IsIntentionalPrice кассы). Остальные строки не меняются.
      if (l.salePackageId != null) {
        line['sale_package_id'] = l.salePackageId;
        line['price_override'] = true;
      }
      // 18.10: у товара с акцией скидку строки назначает сервер — ручную не отправляем.
      if (!l.hasPromo && l.discount > 0) line['discount'] = l.discount.toStringAsFixed(2);
      merged[key] = line;
      items.add(line);
    }
    final body = <String, dynamic>{'shift': shiftId, 'items': items, 'print_receipt': false};
    if (clientId != null && clientId.isNotEmpty) body['client'] = clientId;
    if (cart.orderDiscountPercent > 0) {
      body['order_discount_percent'] = cart.orderDiscountPercent.toStringAsFixed(2);
    } else if (cart.orderDiscountSum > 0) {
      // 2026-10-22 (мобилка, деньги): скидка суммой — не больше итога строк (cart.orderDiscount обрезает), как у кассы.
      body['order_discount_total'] = cart.orderDiscount.toStringAsFixed(2);
    }
    final payment = <String, dynamic>{'method': method.api};
    if (received != null && received > 0) payment['received'] = received.toStringAsFixed(2);
    if (method == PayMethod.debt && received != null && received > 0) {
      // Внесённое сразу при продаже в долг — наличными (AN-01).
      payment['cash_amount'] = received.toStringAsFixed(2);
      payment['card_amount'] = '0.00';
    }
    body['payment'] = payment;
    return body;
  }

  static String _qty(CartLine l) => l.isWeight ? l.qty.toStringAsFixed(3) : l.qty.round().toString();

  /// 2026-10-22 (мобилка, деньги, К-2): ключ попытки оплаты — как PendingIdempotencyKey кассы. Пока исход прошлой
  /// попытки с тем же телом неизвестен (тайм-аут, обрыв, 5xx), повтор идёт ТЕМ ЖЕ ключом — сервер не проведёт продажу
  /// второй раз. Раньше ключ был новым при каждом нажатии: двойное нажатие или повтор после «только при связи» давали
  /// две продажи и два долга. Ключ хранится в базе телефона (переживает закрытие экрана и перезапуск) 24 часа.
  static const pendingKeySetting = 'checkout.pendingKey';
  static const _pendingKeyLife = Duration(hours: 24);

  /// 2026-10-29 (аудит мобилки, пп. 1, 9): попытка оплаты — ключ + тело, которое уходило (или уйдёт) под этим ключом, и время продажи.
  /// Раньше ключ считался от всего тела, включая «получено»: другая введённая сумма давала новый ключ и вторую продажу; а первая
  /// попытка онлайн шла без sold_at/cashbox, досылка — с ними (сервер мог ответить 409 на проведённую продажу). Теперь тело, время
  /// и касса запоминаются вместе с ключом, и повтор (и досылка) отправляют ровно то же.
  static String signatureOf(Map<String, dynamic> body, PayMethod method) {
    final c = (jsonDecode(jsonEncode(body)) as Map).cast<String, dynamic>();
    // Наличные/карта: «получено» не меняет продажу (сумма — итог чека) — в подпись не входит. В долг: внесённое — часть продажи.
    if (method != PayMethod.debt && c['payment'] is Map) (c['payment'] as Map).remove('received');
    return jsonEncode(c);
  }

  Future<_Attempt> _attempt(String signature, Map<String, dynamic> body) async {
    try {
      final raw = await db.getSetting(pendingKeySetting);
      if (raw != null && raw.isNotEmpty) {
        final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
        final at = DateTime.tryParse(m['at'] as String? ?? '');
        final sold = DateTime.tryParse(m['sold'] as String? ?? '');
        if (m['sig'] == signature && m['key'] is String && m['body'] is Map && sold != null && at != null && DateTime.now().difference(at) < _pendingKeyLife) {
          return _Attempt(m['key'] as String, (m['body'] as Map).cast<String, dynamic>(), sold, m['cashbox'] as String?, m['sent'] == true, signature, at);
        }
      }
    } catch (_) {
      // испорченная запись — новый ключ
    }
    final key = const Uuid().v4().replaceAll('-', '');
    final n = DateTime.now();
    final att = _Attempt(key, body, DateTime(n.year, n.month, n.day, n.hour, n.minute, n.second), api.session?.cashboxId, false, signature, n);
    await _saveAttempt(att);
    return att;
  }

  Future<void> _saveAttempt(_Attempt a, {bool sent = false}) => db.setSetting(
    pendingKeySetting,
    jsonEncode({'key': a.key, 'sig': a.signature, 'at': a.at.toIso8601String(), 'body': a.body, 'sold': a.soldAt.toIso8601String(), 'cashbox': a.cashbox, 'sent': sent || a.sentBefore}),
  );

  /// Продажа проведена, встала в очередь или сервер отказал по существу — следующая оплата будет новой.
  Future<void> _forgetKey() => db.setSetting(pendingKeySetting, '');

  /// 2026-10-29 (аудит мобилки, п. 9): есть ли живая попытка оплаты именно этого чека (исход неизвестен). Тогда цены чека
  /// сверять и менять нельзя — повтор должен уйти тем же телом и ключом.
  Future<bool> hasLiveAttempt({required Cart cart, required String shiftId, required PayMethod method, double? received, Client? client}) async {
    try {
      final body = buildBody(cart: cart, shiftId: shiftId, method: method, received: received, clientId: client?.id);
      final raw = await db.getSetting(pendingKeySetting);
      if (raw == null || raw.isEmpty) return false;
      final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
      final at = DateTime.tryParse(m['at'] as String? ?? '');
      return m['sig'] == signatureOf(body, method) && m['body'] is Map && at != null && DateTime.now().difference(at) < _pendingKeyLife;
    } catch (_) {
      return false;
    }
  }

  /// 2026-10-29 (аудит мобилки, п. 8): 409 price_changed — строгая проверка цены отказала (продажа не проведена).
  static bool isPriceChanged(Object? payload) => _payloadHas(payload, 'price_changed');

  /// 2026-10-29 (аудит мобилки, п. 1): 409 idempotency_conflict — продажа с этим ключом у сервера уже есть (тело отличается).
  static bool isKeyConflict(Object? payload) => _payloadHas(payload, 'idempotency_conflict');

  static bool _payloadHas(Object? payload, String code) {
    if (payload == null) return false;
    try {
      return (payload is String ? payload : jsonEncode(payload)).toLowerCase().contains(code);
    } catch (_) {
      return false;
    }
  }

  /// price_mismatch из ответа сервера (на верхнем уровне или внутри detail/error/errors/data): [{product_id, name, sent, current}].
  static List<PriceChange> parseMismatch(Object? payload, Cart cart) {
    final out = <PriceChange>[];
    List? find(Object? o, int depth) {
      if (o is Map) {
        if (o['price_mismatch'] is List) return o['price_mismatch'] as List;
        if (depth < 2) {
          for (final v in o.values) {
            final r = find(v, depth + 1);
            if (r != null) return r;
          }
        }
      }
      return null;
    }

    for (final r in find(payload, 0) ?? const []) {
      if (r is! Map) continue;
      final row = r.cast<String, dynamic>();
      final id = str(row, 'product_id') ?? str(row, 'product') ?? str(row, 'id');
      final current = num0(row, 'current');
      if (id == null || current <= 0) continue;
      final line = cart.lines.where((l) => l.productId == id).firstOrNull;
      out.add(PriceChange(id, str(row, 'name') ?? line?.name ?? id, row['sent'] == null ? (line?.price ?? 0) : num0(row, 'sent'), current));
    }
    return out;
  }

  /// 2026-10-29 (аудит мобилки, п. 8): сверка цен чека с сервером перед оплатой, как SyncPricesBeforePaymentAsync кассы:
  /// карточки товаров (до 2 с на запрос, все сразу). Сервер не ответил ни по одному товару — [PriceCheck.offline].
  Future<PriceCheck> checkPrices(Cart cart, {Duration timeout = const Duration(seconds: 2)}) async {
    final lines = cart.lines.where((l) => !l.custom && l.variantId == null && l.salePackageId == null).toList();
    final ids = {for (final l in lines) l.productId}.toList();
    if (ids.isEmpty) return const PriceCheck([]);
    final fresh = <String, Map<String, dynamic>>{};
    await Future.wait([
      for (final id in ids)
        () async {
          try {
            final m = await api.get('api/main/products/$id/', timeout: timeout).timeout(timeout);
            if (m is Map) fresh[id] = m.cast<String, dynamic>();
          } catch (_) {
            // нет ответа по этому товару — цену не сверили
          }
        }(),
    ]);
    if (fresh.isEmpty) return const PriceCheck([], offline: true);
    final changes = <PriceChange>[];
    for (final l in lines) {
      final srv = fresh[l.productId];
      if (srv == null) continue;
      final retail = num0(srv, 'price');
      final wholesale = num0(srv, 'wholesale_price');
      final now = l.wholesale ? wholesale : retail;
      if (now <= 0 || (now - l.price).abs() < 0.005) continue;
      changes.add(PriceChange(l.productId, l.name, l.price, now, retail: retail > 0 ? retail : null, wholesale: wholesale));
    }
    return PriceCheck(changes);
  }

  /// Провести чек. Без связи (или сервер не ответил) — в очередь с тем же ключом; отказ по существу — исключение.
  /// [requireOnline] — бонусы к списанию: без сервера их не списать, такую продажу в очередь не ставим (как долг).
  Future<CheckoutResult> checkout({
    required Cart cart,
    required String shiftId,
    required PayMethod method,
    double? received,
    Client? client,
    bool requireOnline = false,
  }) {
    // 2026-10-22: оплаты — по одной: вторая ждёт исход первой и берёт её ключ, если исход неизвестен.
    final run = _lock.then(
      (_) => _checkout(cart: cart, shiftId: shiftId, method: method, received: received, client: client, requireOnline: requireOnline),
    );
    _lock = run.then((_) {}, onError: (_) {});
    return run;
  }

  Future<void> _lock = Future.value();

  Future<CheckoutResult> _checkout({
    required Cart cart,
    required String shiftId,
    required PayMethod method,
    double? received,
    Client? client,
    bool requireOnline = false,
  }) async {
    final plain = buildBody(cart: cart, shiftId: shiftId, method: method, received: received, clientId: client?.id);
    // 2026-10-29: тело, время продажи и касса — из запомненной попытки (повтор уходит ровно с теми же полями, что и первая отправка).
    final att = await _attempt(signatureOf(plain, method), plain);
    final key = att.key;
    final body = att.body;
    final session = api.session;
    final now = att.soldAt;
    final summary = cart.lines.take(3).map((l) => l.name).join(', ') + (cart.count > 3 ? '…' : '');
    final row = OutboxCompanion.insert(
      id: key,
      kind: 'sale',
      // 2026-10-22: в очереди — кто продал и на какой кассе (cashbox при досылке, привязка к аккаунту, К-6).
      body: jsonEncode(OutboxMeta.attach(body, owner: OutboxMeta.ownerOf(session), cashboxId: att.cashbox, at: now, packs: _packFactors(cart))),
      createdAt: now,
      total: Value(cart.total),
      summary: Value(summary),
      paymentMethod: Value(method.api),
      shiftId: Value(shiftId),
      display: Value(
        jsonEncode([
          for (final l in cart.lines) [l.name, l.qty, l.price, l.total],
        ]),
      ),
    );

    // 2026-10-27 (ревью М-6): остаток товаров со штуками из упаковки ДО продажи (сервер, иначе телефон) — для сверки после неё
    final before = await _packStockBefore(cart);
    Map<String, dynamic> resp;
    try {
      // 2026-10-29: помечаем «уже отправлялось» ДО запроса — 409 price_changed на повтор не доказывает «не проведено».
      await _saveAttempt(att, sent: true);
      resp = await _send(withSale(body, now, att.cashbox), key, strict: true);
    } on ApiException catch (e) {
      // 2026-10-29 (аудит мобилки, п. 8): строгая проверка цены — сервер ничего не создал; верим только первой отправке ключа.
      if (e.status == 409 && !att.sentBefore && isPriceChanged(e.payload)) {
        await _forgetKey();
        throw PriceChangedException(e.message, changes: parseMismatch(e.payload, cart));
      }
      // 2026-10-29 (аудит мобилки, п. 1): 409 idempotency_conflict — продажа с этим ключом уже проведена: «проведено, сверить».
      if (e.status == 409 && isKeyConflict(e.payload)) {
        await _forgetKey();
        await db.enqueue(row);
        await db.markSent(key);
        await _applyStock(cart);
        return CheckoutResult(queued: false, key: key, total: cart.total, reconcile: true);
      }
      if (!e.isTransient) {
        await _forgetKey();
        rethrow;
      }
      // Долг без сервера не оформляется: нужна сделка клиента (как у кассы). Ключ остаётся — повтор той же оплаты безопасен.
      if (method == PayMethod.debt || requireOnline) throw CheckoutUnknownException(e.message, debt: method == PayMethod.debt);
      await db.enqueue(row);
      await _applyStock(cart);
      await _forgetKey();
      return CheckoutResult(queued: true, key: key, total: cart.total);
    }
    // Сервер ответил — продажа проведена. Ключ забываем сразу: сбой записи ниже не должен вести к новой продаже.
    await _forgetKey();
    await db.enqueue(row);
    await db.markSent(key, serverId: str(resp, 'id'), serverNumber: str(resp, 'number'));
    await _applyStock(cart);
    final warnings = before.isEmpty ? const <PackStockMismatch>[] : await verifyPackStock(cart, before);
    return CheckoutResult(
      queued: false,
      key: key,
      number: str(resp, 'number'),
      total: num0(resp, 'total') > 0 ? num0(resp, 'total') : cart.total,
      receiptUrl: eReceiptUrl(resp),
      response: resp,
      stockWarnings: warnings,
    );
  }

  /// 2026-10-27 (ревью М-6): остатки до продажи у товаров, которые продаются штукой из упаковки (id → остаток в упаковках):
  /// свежий с сервера (до 3 с), иначе остаток телефона. Без таких строк — пусто, ничего не запрашивается.
  Future<Map<String, double>> _packStockBefore(Cart cart) async {
    final out = <String, double>{};
    try {
      for (final l in cart.lines) {
        if (l.custom || l.salePackageId == null || out.containsKey(l.productId)) continue;
        try {
          const t = Duration(seconds: 3);
          final m = (await api.get('api/main/products/${l.productId}/', timeout: t).timeout(t)) as Map;
          out[l.productId] = num0(m.cast<String, dynamic>(), 'quantity');
          continue;
        } catch (_) {
          // сервер не ответил — берём остаток телефона
        }
        final p = await db.productById(l.productId);
        if (p != null) out[l.productId] = p.quantity;
      }
    } catch (_) {
      // без остатка сверять нечего
    }
    return out;
  }

  /// 2026-10-27 (ревью М-6): продажа штуки из упаковки — путь, который касса сама ещё не использует. Если сервер не знает
  /// `sale_package_id`, он спишет упаковки как штуки (3 сигареты → 3 пачки). После продажи сверяем остаток товара на сервере с
  /// ожидаемым (остаток телефона до продажи минус доля упаковки); расхождение — в журнал и жёлтую плашку. Сверка не держит
  /// оплату дольше нескольких секунд; нет связи — молча пропускается. Параллельная продажа того же товара на другой кассе
  /// даст ложное предупреждение — это только подсказка, на деньги не влияет.
  Future<List<PackStockMismatch>> verifyPackStock(Cart cart, Map<String, double> before, {Duration timeout = const Duration(seconds: 4)}) async {
    final out = <PackStockMismatch>[];
    final sold = <String, double>{};
    final names = <String, String>{};
    for (final l in cart.lines) {
      if (l.custom || l.salePackageId == null) continue;
      sold[l.productId] = (sold[l.productId] ?? 0) + l.stockQty;
      names[l.productId] = l.name;
    }
    for (final e in sold.entries) {
      final was = before[e.key];
      if (was == null) continue;
      try {
        final m = (await api.get('api/main/products/${e.key}/', timeout: timeout).timeout(timeout)) as Map;
        final actual = num0(m.cast<String, dynamic>(), 'quantity');
        final expected = was - e.value;
        if ((actual - expected).abs() > 0.011) {
          out.add(PackStockMismatch(e.key, names[e.key] ?? '', expected, actual));
          Audit.log('sale', 'Остаток после штуки из упаковки не сошёлся: ${names[e.key]} · ожидали ${expected.toStringAsFixed(3)}, сервер ${actual.toStringAsFixed(3)}');
        }
      } catch (_) {
        // нет связи или ответ не того вида — сверку пропускаем
      }
    }
    return out;
  }

  /// 2026-10-29: тело для сервера = тело попытки + время продажи и касса (то же делает досылка, [replayBody]).
  static Map<String, dynamic> withSale(Map<String, dynamic> body, DateTime soldAt, String? cashbox) => {
    ...body,
    'sold_at': isoWithOffset(soldAt),
    if (cashbox != null && cashbox.isNotEmpty) 'cashbox': cashbox,
  };

  /// [strict] — онлайн-оплата: заголовок X-NurMarket-Strict-Price: 1, сервер не проведёт чек с устаревшей ценой (409 price_changed).
  /// Сервер без этой проверки заголовок не знает. Досылка из очереди строгой не бывает: покупатель уже заплатил.
  Future<Map<String, dynamic>> _send(Map<String, dynamic> body, String key, {Duration timeout = const Duration(seconds: 15), bool strict = false}) async {
    final resp = await api.post(
      'api/main/pos/checkout/',
      body: body,
      headers: {'Idempotency-Key': key, if (strict) 'X-NurMarket-Strict-Price': '1'},
      timeout: timeout,
    );
    // Ответ не того вида — исход неизвестен (как обрыв): повтор тем же ключом.
    if (resp is! Map) throw ApiException(ApiText.serverError(502), status: 502);
    return resp.cast<String, dynamic>();
  }

  /// Досылка чека из очереди тем же ключом (SyncService). 2026-10-22 (мобилка, деньги): как SyncService.QuickReplay кассы —
  /// время продажи (sold_at) и касса: сервер относит чек к смене этой кассы, открытой в момент продажи, даже если она
  /// уже закрыта (раньше чек, досланный после закрытия смены, отклонялся — деньги взяты, продажи нет). Досылка — 45 с.
  Future<Map<String, dynamic>> replay(OutboxData row) => _send(replayBody(row), row.id, timeout: const Duration(seconds: 45));

  static Map<String, dynamic> replayBody(OutboxData row) {
    final raw = (jsonDecode(row.body) as Map).cast<String, dynamic>();
    final meta = OutboxMeta.read(row.body);
    final cashbox = meta?['cashbox'];
    return withSale(OutboxMeta.strip(raw), row.createdAt, cashbox is String ? cashbox : null);
  }

  /// «2026-10-22T14:05:00+06:00» — как у кассы (yyyy-MM-ddTHH:mm:sszzz).
  static String isoWithOffset(DateTime t) {
    final l = t.toLocal();
    String two(int v) => v.toString().padLeft(2, '0');
    final off = l.timeZoneOffset;
    final m = off.inMinutes.abs();
    return '${l.year}-${two(l.month)}-${two(l.day)}T${two(l.hour)}:${two(l.minute)}:${two(l.second)}${off.isNegative ? '-' : '+'}${two(m ~/ 60)}:${two(m % 60)}';
  }

  /// 2026-10-22 (мобилка, деньги): срок долга «Вернуть до» — как DebtDueDateSync кассы. pos/checkout/ срок не принимает
  /// (сервер ставит 30 дней), поэтому после продажи меняем сделку долга: один платёж к дате. false — срок не записан
  /// (продажа уже проведена, срок можно поправить на сайте).
  Future<bool> setDebtDue({required String clientId, required String saleId, required DateTime due}) async {
    final d = '${due.year}-${due.month.toString().padLeft(2, '0')}-${due.day.toString().padLeft(2, '0')}';
    final body = {'first_due_date': d, 'debt_days': 1, 'interval_days': 1, 'debt_months': null};
    try {
      for (var attempt = 1; attempt <= 3; attempt++) {
        final rows = asList(await api.get('api/main/clients/$clientId/deals/', timeout: const Duration(seconds: 20)));
        for (final deal in rows) {
          if (str(deal, 'sale') != saleId && str(deal, 'sale_id') != saleId) continue;
          final dealId = str(deal, 'id');
          if (dealId == null) continue;
          await api.patch('api/main/clients/$clientId/deals/$dealId/', body: body);
          return true;
        }
        if (attempt < 3) await Future.delayed(Duration(seconds: 2 * attempt));
      }
    } catch (_) {
      // срок не записан — подсказка кассиру
    }
    return false;
  }

  /// 2026-10-27 (М4): {sale_package_id: штук в упаковке} по штукам из упаковки чека — для остатка телефона при снятии и пересчёте
  /// очереди (остаток товара хранится в упаковках). null — таких строк нет, тело очереди прежнее.
  static Map<String, double>? _packFactors(Cart cart) {
    final m = <String, double>{for (final l in cart.lines) if (l.salePackageId != null && l.perPack > 1) l.salePackageId!: l.perPack};
    return m.isEmpty ? null : m;
  }

  Future<void> _applyStock(Cart cart) async {
    for (final l in cart.lines) {
      // 2026-10-27 (М4): штука из упаковки списывает долю упаковки (stockQty = qty / штук в упаковке); у остальных строк = qty
      if (!l.custom) await db.adjustStock(l.productId, -l.stockQty);
    }
  }

  // ------------------------------------------------------------------ чеки

  /// Продажи с сервера: api/main/pos/sales/?page=N, по 80, новые первыми.
  Future<List<Map<String, dynamic>>> sales({int page = 1}) async => asList(await api.get('api/main/pos/sales/', query: {'page': page}));

  Future<Map<String, dynamic>> sale(String id) async => ((await api.get('api/main/pos/sales/$id/')) as Map).cast<String, dynamic>();

  /// Текст чека для отправки покупателю: GET pos/sales/{id}/receipt/.
  Future<String?> receiptText(String id) async {
    final data = await api.get('api/main/pos/sales/$id/receipt/');
    if (data is String) return data;
    if (data is Map) return (data['text'] ?? data['receipt'] ?? data['content'])?.toString();
    return null;
  }

  // ------------------------------------------------------------------ клиенты (продажа в долг)

  Future<List<Client>> clients(String search) async {
    final rows = await getAllPages(
      api,
      'api/main/clients/',
      query: {if (search.trim().isNotEmpty) 'search': search.trim()},
      maxPages: search.trim().isEmpty ? 30 : 2,
    );
    return rows
        .where((m) => !const {'supplier', 'suppliers'}.contains(str(m, 'type')))
        .map(
          (m) => Client(
            str(m, 'id') ?? '',
            str(m, 'full_name') ?? str(m, 'name') ?? str(m, 'llc') ?? str(m, 'phone') ?? '',
            str(m, 'phone') ?? '',
            company: str(m, 'llc') ?? '',
            inn: str(m, 'inn') ?? '',
          ),
        )
        .where((c) => c.id.isNotEmpty)
        .toList();
  }

  /// 2026-10-21: КП и счёт — новый клиент сразу с компанией (llc) и ИНН, те же поля, что у поставщика на кассе.
  Future<Client> createClient(String name, String phone, {String company = '', String inn = ''}) async {
    final body = {
      'full_name': name.trim(),
      'phone': phone.trim(),
      'type': 'client',
      if (company.trim().isNotEmpty) 'llc': company.trim(),
      if (inn.trim().isNotEmpty) 'inn': inn.trim(),
    };
    final m = ((await api.post('api/main/clients/', body: body)) as Map).cast<String, dynamic>();
    return Client(
      str(m, 'id') ?? '',
      str(m, 'full_name') ?? name.trim(),
      str(m, 'phone') ?? phone.trim(),
      company: str(m, 'llc') ?? company.trim(),
      inn: str(m, 'inn') ?? inn.trim(),
    );
  }
}

/// 2026-10-29: запомненная попытка оплаты (см. [SalesRepository.signatureOf]).
class _Attempt {
  final String key;
  final Map<String, dynamic> body;
  final DateTime soldAt;
  final String? cashbox;
  final bool sentBefore;
  final String signature;
  final DateTime at;
  _Attempt(this.key, this.body, this.soldAt, this.cashbox, this.sentBefore, this.signature, this.at);
}
