// 2026-11-05 (E2E-тесты мобилки): поддельный сервер NurCRM на loopback самого устройства (127.0.0.1:порт).
// Никакого обращения к app.nurcrm.kg и настоящим учёткам: адрес приложению задаётся при сборке
// (`--dart-define=NURMARKET_API_URL=http://127.0.0.1:порт`, см. ApiClient.defaultBaseUrl), а сервер поднимается внутри теста.
// Отвечает так, как нужно экранам: вход, профиль, компания, касса, смены, каталог, checkout, чеки, возврат.
// Отказы 500/503, медленный ответ, обрыв ответа после записи продажи и «сервер недоступен» (stop/start) — по сценарию теста.
import 'dart:async';
import 'dart:convert';
import 'dart:io';

/// Что делать с очередным запросом checkout (очередь [FakeNurServer.checkoutPlan]; пусто — обычный успех).
sealed class CheckoutStep {
  const CheckoutStep();
}

/// Ответить кодом (503, 500…) и ничего не записывать.
class CheckoutStatus extends CheckoutStep {
  final int status;
  const CheckoutStatus(this.status);
}

/// Продажу записать, а соединение оборвать, не отправив ответ (кассир не знает, прошла ли оплата).
class CheckoutDropAfterCommit extends CheckoutStep {
  const CheckoutDropAfterCommit();
}

/// Ответить успехом, но не сразу.
class CheckoutSlow extends CheckoutStep {
  final Duration delay;
  const CheckoutSlow(this.delay);
}

class RecordedRequest {
  final String method;
  final String path;
  final Map<String, String> query;
  final Map<String, String> headers;
  final dynamic body;
  final int status;
  const RecordedRequest(this.method, this.path, this.query, this.headers, this.body, this.status);

  @override
  String toString() => '$method $path → $status';
}

class FakeProduct {
  final String id;
  final String name;
  final double price;
  final String unit;
  final bool isWeight;
  final double quantity;
  final String barcode;
  const FakeProduct(this.id, this.name, this.price, this.unit, this.isWeight, this.quantity, this.barcode);

  Map<String, dynamic> toJson() => {
    'id': id,
    'name': name,
    'price': price.toStringAsFixed(2),
    'wholesale_price': '0.00',
    'unit': unit,
    'is_weight': isWeight,
    'quantity': quantity.toStringAsFixed(3),
    'barcode': barcode,
    'sku': 'A${id.padLeft(3, '0')}',
    'category': {'name': isWeight ? 'Сыры' : 'Бакалея'},
    'minimum_quantity': '0',
  };
}

/// Пароль и логин тестового кассира — вымышленные, настоящих учёток здесь нет.
const fakeEmail = 'e2e.kassir@example.test';
const fakePassword = 'e2e-pass-123';
const fakeUserId = 'u-e2e-1';
const fakeCashboxId = 'cb-e2e-1';

/// Доступы владельца в профиле, как их отдаёт сервер (флаги can_*): без них возврат и скидки в приложении закрыты.
const _ownerRights = {
  'can_view_products': true,
  'can_view_sale': true,
  'can_view_cashier': true,
  'can_use_pos': true,
  'can_view_market_discount': true,
  'can_view_market_delete_cart_item': true,
  'can_view_market_employee_return': true,
  'can_view_market_edit_price': true,
};

class FakeNurServer {
  final int port;
  HttpServer? _server;

  FakeNurServer(this.port);

  // ------------------------------------------------------------------ состояние сервера

  final products = <FakeProduct>[
    const FakeProduct('p01', 'Вода «Нур» 1,5 л', 85, 'шт', false, 40, '4870001112223'),
    const FakeProduct('p02', 'Сок яблочный 1 л', 140, 'шт', false, 25, '4870001119999'),
    const FakeProduct('p03', 'Хлеб «Бишкек»', 32, 'шт', false, 60, '4870001110011'),
    const FakeProduct('p04', 'Молоко 3,2 % 1 л', 98, 'шт', false, 30, '4870001110028'),
    const FakeProduct('p05', 'Кефир 1 л', 92, 'шт', false, 20, '4870001110035'),
    const FakeProduct('p06', 'Масло сливочное 180 г', 245, 'шт', false, 18, '4870001110042'),
    const FakeProduct('p07', 'Яйца С1, десяток', 130, 'шт', false, 50, '4870001110059'),
    const FakeProduct('p08', 'Сахар 1 кг', 78, 'шт', false, 35, '4870001110066'),
    const FakeProduct('p09', 'Соль 1 кг', 24, 'шт', false, 45, '4870001110073'),
    const FakeProduct('p10', 'Чай чёрный 100 пакетиков', 215, 'шт', false, 22, '4870001110080'),
    const FakeProduct('p11', 'Печенье «Овсяное»', 66, 'шт', false, 55, '4870001110097'),
    const FakeProduct('p12', 'Макароны 400 г', 58, 'шт', false, 41, '4870001110103'),
    const FakeProduct('p13', 'Рис круглый 1 кг', 112, 'шт', false, 28, '4870001110110'),
    const FakeProduct('p14', 'Сыр «Сулугуни»', 75, 'кг', true, 12.5, '4870001110127'),
  ];

  String? openShiftId;
  int _shiftCounter = 0;
  int _saleCounter = 1600;
  int _returnCounter = 0;

  /// Продажи сервера: id → запись (items, total, status…).
  final sales = <String, Map<String, dynamic>>{};
  final _salesByKey = <String, String>{};
  final returns = <Map<String, dynamic>>[];

  /// Сколько раз пришёл checkout с каждым ключом повтора (в том числе повторы).
  final checkoutHitsByKey = <String, int>{};

  /// Порядок действий для очередных checkout (см. [CheckoutStep]).
  final checkoutPlan = <CheckoutStep>[];

  /// Задержка ответа на любой запрос (для проверки «медленного сервера»).
  Duration latency = Duration.zero;

  /// 2026-11-06 (аудит №5, М7-19): правило сервера ceba67c — признаки `weight_line_total_floor`, `checkout_branch`, `checkout_lines_merged`
  /// в `GET api/main/capabilities/`. Включено: `line_total` весовой строки = FLOOR(цена × вес до шага весов) − скидка, одинаковые строки
  /// склеиваются, один товар по двум ценам — 400 `duplicate_line`, сумма строк = subtotal. Выключено — старый сервер (FLOOR от суммы весовых,
  /// признаков нет).
  bool weightLineFloor = true;

  /// Шаг весов компании (`scale_barcode_amount_unit`): false — tiyin (0,01), true — som (целый сом).
  bool weightStepSom = false;

  /// Все пришедшие запросы (для утверждений в тестах).
  final log = <RecordedRequest>[];

  /// Пути, которых сервер не знает (ответ 404) — для сведения в конце прогона.
  final unknownPaths = <String>{};

  // ------------------------------------------------------------------ запуск и остановка

  bool get running => _server != null;

  Future<void> start() async {
    if (_server != null) return;
    _server = await HttpServer.bind(InternetAddress.loopbackIPv4, port, shared: true);
    _server!.listen(_handle, onError: (_) {});
  }

  /// «Сервер недоступен»: порт закрыт, соединение отклоняется (в приложении — обрыв связи).
  Future<void> stop() async {
    final s = _server;
    _server = null;
    await s?.close(force: true);
  }

  void resetState() {
    openShiftId = null;
    sales.clear();
    _salesByKey.clear();
    returns.clear();
    checkoutHitsByKey.clear();
    checkoutPlan.clear();
    latency = Duration.zero;
    weightLineFloor = true;
    weightStepSom = false;
    log.clear();
  }

  /// Продаж, созданных сервером (без повторов с тем же ключом).
  int get salesCount => sales.length;

  Iterable<RecordedRequest> requests(String method, String pathPart) => log.where((r) => r.method == method && r.path.contains(pathPart));

  // ------------------------------------------------------------------ разбор запросов

  Future<void> _handle(HttpRequest req) async {
    final path = req.uri.path;
    dynamic body;
    try {
      final text = await utf8.decoder.bind(req).join();
      if (text.isNotEmpty) body = jsonDecode(text);
    } catch (_) {
      body = null;
    }
    if (latency > Duration.zero) await Future<void>.delayed(latency);
    final headers = <String, String>{};
    req.headers.forEach((k, v) => headers[k.toLowerCase()] = v.join(','));
    final method = req.method.toUpperCase();
    final q = req.uri.queryParameters;

    Future<void> send(int status, Object? data) async {
      log.add(RecordedRequest(method, path, q, headers, body, status));
      try {
        req.response.statusCode = status;
        req.response.headers.contentType = ContentType('application', 'json', charset: 'utf-8');
        req.response.write(jsonEncode(data));
        await req.response.close();
      } catch (_) {
        // клиент уже ушёл
      }
    }

    Future<void> drop() async {
      log.add(RecordedRequest(method, path, q, headers, body, -1));
      try {
        final socket = await req.response.detachSocket(writeHeaders: false);
        socket.destroy();
      } catch (_) {
        // соединение и так закрыто
      }
    }

    // пути приходят со слешем в конце: /api/main/pos/checkout/
    final p = path.endsWith('/') ? path.substring(0, path.length - 1) : path;
    final seg = p.split('/').where((s) => s.isNotEmpty).toList();
    bool is_(String exact) => p == exact;

    // ---- вход
    if (method == 'POST' && is_('/api/users/auth/login')) {
      final m = body is Map ? body : const {};
      if (m['email'] == fakeEmail && m['password'] == fakePassword) {
        return send(200, {'access': 'e2e-access-1', 'refresh': 'e2e-refresh-1'});
      }
      return send(401, {'detail': 'No active account found with the given credentials'});
    }
    if (method == 'POST' && is_('/api/users/auth/refresh')) {
      return send(200, {'access': 'e2e-access-2', 'refresh': 'e2e-refresh-1'});
    }
    if (method == 'GET' && is_('/api/users/profile')) {
      return send(200, {
        'user': {'id': fakeUserId, 'first_name': 'Айбек', 'last_name': 'Тестов', 'email': fakeEmail, 'role': 'owner', 'role_display': 'Владелец', 'is_owner': true, 'language': 'ru', ..._ownerRights},
      });
    }
    if (method == 'GET' && is_('/api/users/company')) {
      return send(200, {'id': 'co-e2e', 'name': 'Магазин E2E', 'inn': '00000000000000', 'address': 'г. Бишкек, ул. Тестовая, 1', 'scale_barcode_amount_unit': weightStepSom ? 'som' : 'tiyin'});
    }
    // 2026-11-06 (М7-19): признаки возможностей сервера (после входа); у «старого» сервера список пуст
    if (method == 'GET' && is_('/api/main/capabilities')) {
      return send(200, {
        'server_version': 'ceba67c',
        'features': weightLineFloor ? ['weight_line_total_floor', 'checkout_branch', 'checkout_lines_merged'] : <String>[],
      });
    }

    // ---- касса и смены
    if (method == 'GET' && is_('/api/construction/cashboxes')) {
      return send(200, {
        'results': [
          {'id': fakeCashboxId, 'name': 'Касса 1'},
        ],
      });
    }
    if (method == 'GET' && is_('/api/construction/shifts')) {
      final status = q['status'];
      if (status == 'open' && openShiftId != null) return send(200, {'results': [_shiftJson(openShiftId!)]});
      return send(200, {'results': <dynamic>[]});
    }
    if (method == 'POST' && is_('/api/construction/shifts/open')) {
      if (openShiftId != null) return send(400, {'detail': 'Смена уже открыта'});
      openShiftId = 'sh-e2e-${++_shiftCounter}';
      return send(201, _shiftJson(openShiftId!, opening: body is Map ? '${body['opening_cash'] ?? '0'}' : '0'));
    }
    if (seg.length == 5 && method == 'POST' && seg[0] == 'api' && seg[1] == 'construction' && seg[2] == 'shifts' && seg[4] == 'close') {
      if (openShiftId == seg[3]) openShiftId = null;
      return send(200, {'id': seg[3], 'status': 'closed'});
    }
    if (method == 'GET' && is_('/api/construction/cashflows')) return send(200, {'results': <dynamic>[]});

    // ---- каталог
    if (method == 'GET' && is_('/api/main/products/catalog-version')) return send(200, {'catalog_version': 'e2e-v1'});
    if (method == 'GET' && is_('/api/main/products/list')) {
      final page = int.tryParse(q['page'] ?? '1') ?? 1;
      final size = int.tryParse(q['page_size'] ?? '100') ?? 100;
      final from = (page - 1) * size;
      final slice = from >= products.length ? const <FakeProduct>[] : products.skip(from).take(size).toList();
      final more = from + slice.length < products.length;
      return send(200, {'count': products.length, 'next': more ? 'next' : null, 'results': [for (final x in slice) x.toJson()]});
    }
    if (method == 'GET' && seg.length == 4 && seg[0] == 'api' && seg[1] == 'main' && seg[2] == 'products') {
      final found = products.where((x) => x.id == seg[3]).firstOrNull;
      if (found != null) return send(200, found.toJson());
    }

    // ---- продажа
    if (method == 'POST' && is_('/api/main/pos/checkout')) {
      final key = headers['idempotency-key'] ?? '';
      checkoutHitsByKey[key] = (checkoutHitsByKey[key] ?? 0) + 1;
      final step = checkoutPlan.isEmpty ? null : checkoutPlan.removeAt(0);
      if (step is CheckoutStatus) return send(step.status, {'detail': 'Сервис временно недоступен'});
      if (step is CheckoutSlow) await Future<void>.delayed(step.delay);
      final existingId = _salesByKey[key];
      // 2026-11-06 (М7-19, ответ Умара п. 4): тот же товар другой ценой — 400 duplicate_line (продажа не записывается)
      if (existingId == null && weightLineFloor && body is Map) {
        final dup = _duplicateLineIndex(body.cast<String, dynamic>());
        if (dup != null) return send(400, {'code': 'duplicate_line', 'detail': 'duplicate_line', 'duplicate_of': dup});
      }
      Map<String, dynamic> sale;
      if (existingId != null) {
        sale = sales[existingId]!; // повтор с тем же ключом — тот же чек, второй продажи нет
      } else {
        sale = _createSale(body is Map ? body.cast<String, dynamic>() : <String, dynamic>{});
        _salesByKey[key] = sale['id'] as String;
      }
      if (step is CheckoutDropAfterCommit) return drop();
      return send(201, sale);
    }
    if (method == 'GET' && is_('/api/main/pos/sales')) {
      final list = sales.values.toList().reversed.map(_saleRow).toList();
      return send(200, {'count': list.length, 'next': null, 'results': list});
    }
    if (seg.length == 5 && method == 'GET' && seg[3] == 'sales') {
      final s = sales[seg[4]];
      return s == null ? send(404, {'detail': 'Не найдено'}) : send(200, s);
    }
    if (seg.length == 6 && method == 'POST' && seg[3] == 'sales' && seg[5] == 'return') {
      return send(200, _doReturn(seg[4], body is Map ? body.cast<String, dynamic>() : <String, dynamic>{}));
    }
    if (method == 'GET' && is_('/api/main/pos/returns')) {
      final sale = q['sale'];
      final list = returns.where((r) => sale == null || r['sale'] == sale).toList();
      return send(200, {'count': list.length, 'next': null, 'results': list});
    }

    // ---- пустые списки, которые экраны спрашивают при входе
    if (method == 'GET' && (is_('/api/main/clients/debtors') || is_('/api/main/clients'))) return send(200, {'count': 0, 'next': null, 'results': <dynamic>[]});

    unknownPaths.add('$method $p');
    return send(404, {'detail': 'Не найдено (поддельный сервер не знает этот адрес)'});
  }

  // ------------------------------------------------------------------ данные

  Map<String, dynamic> _shiftJson(String id, {String opening = '0.00'}) {
    var cash = 0.0, count = 0;
    for (final s in sales.values) {
      if (s['shift'] != id || s['status'] != 'completed') continue;
      cash += double.parse(s['total'] as String);
      count++;
    }
    return {
      'id': id,
      'status': 'open',
      'cashbox': fakeCashboxId,
      'cashbox_name': 'Касса 1',
      'cashier': fakeUserId,
      'opened_at': DateTime.now().toUtc().toIso8601String(),
      'opening_cash': opening,
      'expected_cash': (double.tryParse(opening) ?? 0) + cash,
      'sales_total': cash.toStringAsFixed(2),
      'cash_sales_total': cash.toStringAsFixed(2),
      'sales_count': count,
    };
  }

  /// Индекс первой строки, если тот же товар (и вариант) стоит в чеке с другой ценой; null — конфликта нет.
  int? _duplicateLineIndex(Map<String, dynamic> body) {
    final first = <String, int>{};
    final prices = <String, int>{};
    final list = body['items'] as List? ?? const [];
    for (var i = 0; i < list.length; i++) {
      final it = (list[i] as Map).cast<String, dynamic>();
      if (it['custom'] == true) continue;
      final k = '${it['product']}|${it['variant'] ?? ""}|${it['sale_package_id'] ?? ""}';
      final price = _fixed(it['price'], 2);
      if (!first.containsKey(k)) {
        first[k] = i;
        prices[k] = price;
      } else if (prices[k] != price) {
        return first[k];
      }
    }
    return null;
  }

  /// Правило итога сервера. Старое (чеки 1971–1988, см. Cart._subtotalUnits): сумма весовых строк округляется ВНИЗ до копейки одной суммой,
  /// остальные строки — HALF_UP до копейки; из этого вычитается скидка. Новое (ceba67c, [weightLineFloor]): одинаковые строки склеены,
  /// каждая весовая позиция — вниз до шага весов, сумма строк = subtotal. Единицы — 1e-5 сома (количество до 3 знаков × цена до 2).
  Map<String, dynamic> _createSale(Map<String, dynamic> body) {
    const unit = 100000; // 1 сом
    const cent = 1000; // 0,01 сом
    final step = weightStepSom ? unit : cent;
    var weighed = 0, other = 0, discount = 0;
    final items = <Map<String, dynamic>>[];
    // 2026-11-06 (М7-19): при новом правиле строки одного товара по одной цене склеиваются в одну позицию
    final rows = <Map<String, dynamic>>[];
    for (final raw in (body['items'] as List? ?? const [])) {
      final it = Map<String, dynamic>.from((raw as Map).cast<String, dynamic>());
      if (weightLineFloor && it['custom'] != true) {
        final same = rows.where((r) => r['product'] == it['product'] && _fixed(r['price'], 2) == _fixed(it['price'], 2) && r['variant'] == it['variant']).firstOrNull;
        if (same != null) {
          same['qty'] = ((_fixed(same['qty'], 3) + _fixed(it['qty'], 3)) / 1000).toStringAsFixed(3);
          same['discount'] = ((_fixed(same['discount'], 2) + _fixed(it['discount'], 2)) / 100).toStringAsFixed(2);
          continue;
        }
      }
      rows.add(it);
    }
    for (final it in rows) {
      final product = products.where((x) => x.id == it['product']).firstOrNull;
      final qty = _fixed(it['qty'], 3); // в тысячных
      final price = _fixed(it['price'], 2); // в сотых
      final gross = qty * price; // 1e-5 сома
      final lineDiscount = _fixed(it['discount'], 2) * 1000;
      var lineTotal = ((gross + cent ~/ 2) ~/ cent) * cent;
      if (product?.isWeight == true) {
        if (weightLineFloor) {
          lineTotal = (gross ~/ step) * step;
          weighed += lineTotal;
        } else {
          weighed += gross;
        }
      } else {
        other += gross;
      }
      if (weightLineFloor) lineTotal -= lineDiscount;
      discount += lineDiscount;
      items.add({
        'id': 'si-${_saleCounter + 1}-${items.length + 1}',
        'product': it['product'],
        'product_name': product?.name ?? '${it['name'] ?? ""}',
        'quantity': (qty / 1000).toStringAsFixed(3),
        'unit_price': (price / 100).toStringAsFixed(2),
        'total': (lineTotal / unit).toStringAsFixed(2),
        'returned_qty': '0',
      });
    }
    final subtotal = (weightLineFloor ? weighed : (weighed ~/ cent) * cent) + ((other + cent ~/ 2) ~/ cent) * cent;
    final total = subtotal - discount < 0 ? 0 : subtotal - discount;
    final id = 'sale-e2e-${++_saleCounter}';
    final payment = body['payment'] is Map ? (body['payment'] as Map).cast<String, dynamic>() : <String, dynamic>{};
    final sale = <String, dynamic>{
      'id': id,
      'number': '$_saleCounter',
      'shift': body['shift'] ?? openShiftId,
      'status': 'completed',
      'total': (total / unit).toStringAsFixed(2),
      'payment_method': payment['method'] ?? 'cash',
      'created_at': DateTime.now().toUtc().toIso8601String(),
      'cashier': fakeUserId,
      'items': items,
      'first_item_name': items.isEmpty ? '' : items.first['product_name'],
    };
    sales[id] = sale;
    return sale;
  }

  /// Десятичная строка/число → целое в 10^[digits] долях (без ошибок двоичной дроби).
  static int _fixed(Object? v, int digits) {
    final s = '${v ?? '0'}'.trim().replaceAll(',', '.');
    final parts = s.split('.');
    final whole = int.tryParse(parts[0].isEmpty ? '0' : parts[0]) ?? 0;
    var frac = parts.length > 1 ? parts[1] : '';
    frac = frac.length > digits ? frac.substring(0, digits) : frac.padRight(digits, '0');
    return whole * _pow10(digits) + (int.tryParse(frac) ?? 0);
  }

  static int _pow10(int n) {
    var r = 1;
    for (var i = 0; i < n; i++) {
      r *= 10;
    }
    return r;
  }

  Map<String, dynamic> _saleRow(Map<String, dynamic> s) => {
    'id': s['id'],
    'number': s['number'],
    'shift': s['shift'],
    'status': s['status'],
    'total': s['total'],
    'payment_method': s['payment_method'],
    'created_at': s['created_at'],
    'first_item_name': s['first_item_name'],
  };

  Map<String, dynamic> _doReturn(String saleId, Map<String, dynamic> body) {
    final sale = sales[saleId];
    if (sale == null) return {'detail': 'Не найдено'};
    final key = '${body['idempotency_key'] ?? ''}';
    final seen = returns.where((r) => r['idempotency_key'] == key && key.isNotEmpty).firstOrNull;
    if (seen != null) return {'refund': seen};
    final items = (sale['items'] as List).cast<Map<String, dynamic>>();
    final picks = body['items'] as List?;
    var amount = 0.0;
    if (picks == null || picks.isEmpty) {
      amount = double.parse(sale['total'] as String);
      for (final it in items) {
        it['returned_qty'] = it['quantity'];
        it['returnable_qty'] = '0';
      }
      sale['status'] = 'returned';
      sale['return_status'] = 'full';
    } else {
      for (final raw in picks) {
        final pick = (raw as Map).cast<String, dynamic>();
        final it = items.firstWhere((x) => x['id'] == pick['sale_item_id']);
        final qty = double.parse('${pick['quantity']}');
        amount += qty * double.parse(it['unit_price'] as String);
        it['returned_qty'] = (double.parse('${it['returned_qty']}') + qty).toStringAsFixed(3);
      }
      sale['status'] = 'partially_returned';
    }
    final doc = {
      'id': 'ret-e2e-${++_returnCounter}',
      'sale': saleId,
      'amount': amount.toStringAsFixed(2),
      'cash': amount.toStringAsFixed(2),
      'idempotency_key': key,
      'created_at': DateTime.now().toUtc().toIso8601String(),
    };
    returns.add(doc);
    return {'refund': doc, 'sale': saleId, 'status': sale['status']};
  }
}
