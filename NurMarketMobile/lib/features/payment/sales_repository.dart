import 'dart:convert';

import 'package:drift/drift.dart' show Value;
import 'package:uuid/uuid.dart';

import '../../core/api/api_client.dart';
import '../../core/db/database.dart';
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
      body['order_discount_total'] = cart.orderDiscountSum.toStringAsFixed(2);
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

  /// Провести чек. Без связи (или сервер не ответил) — в очередь с тем же ключом; отказ по существу — исключение.
  Future<CheckoutResult> checkout({required Cart cart, required String shiftId, required PayMethod method, double? received, Client? client}) async {
    final key = const Uuid().v4().replaceAll('-', '');
    final body = buildBody(cart: cart, shiftId: shiftId, method: method, received: received, clientId: client?.id);
    final summary = cart.lines.take(3).map((l) => l.name).join(', ') + (cart.count > 3 ? '…' : '');
    final row = OutboxCompanion.insert(
      id: key,
      kind: 'sale',
      body: jsonEncode(body),
      createdAt: DateTime.now(),
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

    try {
      final resp = await _send(body, key);
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
    } on ApiException catch (e) {
      if (!e.isTransient) rethrow;
      // Долг без сервера не оформляется: нужна сделка клиента (как у кассы).
      if (method == PayMethod.debt) throw ApiException('Продажа в долг — только при связи с сервером.');
      await db.enqueue(row);
      await _applyStock(cart);
      return CheckoutResult(queued: true, key: key, total: cart.total);
    }
  }

  Future<Map<String, dynamic>> _send(Map<String, dynamic> body, String key) async {
    final resp = await api.post('api/main/pos/checkout/', body: body, headers: {'Idempotency-Key': key}, timeout: const Duration(seconds: 15));
    return (resp as Map).cast<String, dynamic>();
  }

  /// Досылка чека из очереди тем же ключом (SyncService).
  Future<Map<String, dynamic>> replay(OutboxData row) => _send((jsonDecode(row.body) as Map).cast<String, dynamic>(), row.id);

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
