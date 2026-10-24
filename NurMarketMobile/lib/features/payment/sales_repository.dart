import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:uuid/uuid.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
import '../../core/sync/outbox_meta.dart';
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
  const Client(this.id, this.name, this.phone, {this.company = '', this.inn = ''});
  String get display => phone.isEmpty ? name : '$name · $phone';
}

/// 2026-10-22 (мобилка, деньги): сервер не ответил на продажу, которую нельзя поставить в очередь (долг, бонусы) —
/// она МОГЛА пройти. Ключ попытки сохранён: повтор той же оплаты сервер второй раз не проведёт.
class CheckoutUnknownException extends ApiException {
  final bool debt;
  CheckoutUnknownException(super.message, {this.debt = false});
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
  const CheckoutResult({required this.queued, required this.key, this.number, required this.total, this.receiptUrl, this.response});
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
      final key = '${l.productId}|${l.price.toStringAsFixed(2)}|${l.variantId ?? ''}';
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

  Future<String> _attemptKey(String signature) async {
    try {
      final raw = await db.getSetting(pendingKeySetting);
      if (raw != null && raw.isNotEmpty) {
        final m = (jsonDecode(raw) as Map).cast<String, dynamic>();
        final at = DateTime.tryParse(m['at'] as String? ?? '');
        if (m['sig'] == signature && m['key'] is String && at != null && DateTime.now().difference(at) < _pendingKeyLife) return m['key'] as String;
      }
    } catch (_) {
      // испорченная запись — новый ключ
    }
    final key = const Uuid().v4().replaceAll('-', '');
    await db.setSetting(pendingKeySetting, jsonEncode({'key': key, 'sig': signature, 'at': DateTime.now().toIso8601String()}));
    return key;
  }

  /// Продажа проведена, встала в очередь или сервер отказал по существу — следующая оплата будет новой.
  Future<void> _forgetKey() => db.setSetting(pendingKeySetting, '');

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
    final body = buildBody(cart: cart, shiftId: shiftId, method: method, received: received, clientId: client?.id);
    final key = await _attemptKey(jsonEncode(body));
    final session = api.session;
    final now = DateTime.now();
    final summary = cart.lines.take(3).map((l) => l.name).join(', ') + (cart.count > 3 ? '…' : '');
    final row = OutboxCompanion.insert(
      id: key,
      kind: 'sale',
      // 2026-10-22: в очереди — кто продал и на какой кассе (cashbox при досылке, привязка к аккаунту, К-6).
      body: jsonEncode(OutboxMeta.attach(body, owner: OutboxMeta.ownerOf(session), cashboxId: session?.cashboxId, at: now)),
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

    Map<String, dynamic> resp;
    try {
      resp = await _send(body, key);
    } on ApiException catch (e) {
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
    return CheckoutResult(
      queued: false,
      key: key,
      number: str(resp, 'number'),
      total: num0(resp, 'total') > 0 ? num0(resp, 'total') : cart.total,
      receiptUrl: eReceiptUrl(resp),
      response: resp,
    );
  }

  Future<Map<String, dynamic>> _send(Map<String, dynamic> body, String key, {Duration timeout = const Duration(seconds: 15)}) async {
    final resp = await api.post('api/main/pos/checkout/', body: body, headers: {'Idempotency-Key': key}, timeout: timeout);
    // Ответ не того вида — исход неизвестен (как обрыв): повтор тем же ключом.
    if (resp is! Map) throw ApiException('Ошибка сервера 502.', status: 502);
    return resp.cast<String, dynamic>();
  }

  /// Досылка чека из очереди тем же ключом (SyncService). 2026-10-22 (мобилка, деньги): как SyncService.QuickReplay кассы —
  /// время продажи (sold_at) и касса: сервер относит чек к смене этой кассы, открытой в момент продажи, даже если она
  /// уже закрыта (раньше чек, досланный после закрытия смены, отклонялся — деньги взяты, продажи нет). Досылка — 45 с.
  Future<Map<String, dynamic>> replay(OutboxData row) => _send(replayBody(row), row.id, timeout: const Duration(seconds: 45));

  static Map<String, dynamic> replayBody(OutboxData row) {
    final raw = (jsonDecode(row.body) as Map).cast<String, dynamic>();
    final meta = OutboxMeta.read(row.body);
    final body = OutboxMeta.strip(raw);
    body['sold_at'] = isoWithOffset(row.createdAt);
    final cashbox = meta?['cashbox'];
    if (cashbox is String && cashbox.isNotEmpty) body['cashbox'] = cashbox;
    return body;
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

  Future<void> _applyStock(Cart cart) async {
    for (final l in cart.lines) {
      if (!l.custom) await db.adjustStock(l.productId, -l.qty);
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
