import '../../core/api/api_client.dart';
import '../../core/api/session_store.dart';
import '../../core/util/ui.dart';
import '../payment/receipt_qr.dart' as qr;
import '../sale/cart.dart';

/// 13.10, владелец: «отображение чека сделай красивым и приведи чек по стандартам Кыргызстана».
/// Один чек — три вида: экран (ReceiptView), PDF (receipt_pdf.dart) и текст для мессенджера/QR.
///
/// По Порядку применения ККМ (ПКМ КР от 08.04.2022 № 193): в чеке — реквизиты продавца (наименование, ИНН, адрес),
/// номер, дата и время, позиции (наименование, количество, цена, стоимость), итог и форма оплаты; у фискального чека —
/// регистрационный номер ККМ, номер фискального модуля, ФД и ФПД, налоги и QR для проверки. Пока продажа не прошла
/// через ККМ (сервер не прислал ФД), документ называется «Товарный чек» — не выдаём его за кассовый.
/// Язык документа — русский, как у печатного чека кассы.
class ReceiptLine {
  final String name;
  final double qty;
  final String unit;
  final double price;
  final double discount;
  final double total;
  const ReceiptLine({required this.name, required this.qty, this.unit = '', required this.price, this.discount = 0, required this.total});
}

class ReceiptTax {
  final String name;
  final double amount;
  const ReceiptTax(this.name, this.amount);
}

/// Фискальные реквизиты (часть 20 ТЗ бэкенда) — есть, только когда продажа зарегистрирована в ККМ.
class ReceiptFiscal {
  final String regNumber;
  final String fiscalModule;
  final String fd;
  final String fpd;
  final String? checkUrl;
  final List<ReceiptTax> taxes;
  const ReceiptFiscal({this.regNumber = '', this.fiscalModule = '', this.fd = '', this.fpd = '', this.checkUrl, this.taxes = const []});

  static ReceiptFiscal? parse(Map<String, dynamic>? sale) {
    if (sale == null) return null;
    final f = sale['fiscal'] is Map ? (sale['fiscal'] as Map).cast<String, dynamic>() : sale;
    final fd = str(f, 'fd') ?? str(f, 'fd_number') ?? str(f, 'fiscal_document');
    if (fd == null) return null;
    final taxes = <ReceiptTax>[
      for (final t in asList(f['taxes'] ?? sale['taxes'])) ReceiptTax(str(t, 'name') ?? str(t, 'title') ?? 'Налог', num0(t, 'amount')),
    ];
    return ReceiptFiscal(
      regNumber: str(f, 'reg_number') ?? str(f, 'rn_kkm') ?? str(f, 'registration_number') ?? '',
      fiscalModule: str(f, 'fiscal_module') ?? str(f, 'fm') ?? str(f, 'fm_number') ?? '',
      fd: fd,
      fpd: str(f, 'fpd') ?? str(f, 'fiscal_sign') ?? '',
      checkUrl: str(f, 'check_url') ?? str(f, 'qr') ?? str(f, 'qr_url'),
      taxes: taxes,
    );
  }
}

class Receipt {
  final String shop;
  final String inn;
  final String address;
  final bool isReturn;
  final String number;
  final String shift;
  final DateTime at;
  final String cashier;
  final String cashbox;
  final String client;
  final List<ReceiptLine> lines;
  final double discount;
  final double total;
  final String payment; // cash | card | transfer | debt | mixed
  final double? received;
  final double change;
  final ReceiptFiscal? fiscal;
  final String? eReceiptUrl;
  final bool offline;

  /// 2026-10-22 (мобилка, деньги): продажа в долг — срок «Вернуть до» (как в чеке кассы; у сервера в продаже его нет).
  final DateTime? debtDue;

  const Receipt({
    required this.shop,
    this.inn = '',
    this.address = '',
    this.isReturn = false,
    this.number = '',
    this.shift = '',
    required this.at,
    this.cashier = '',
    this.cashbox = '',
    this.client = '',
    required this.lines,
    this.discount = 0,
    required this.total,
    this.payment = 'cash',
    this.received,
    this.change = 0,
    this.fiscal,
    this.eReceiptUrl,
    this.offline = false,
    this.debtDue,
  });

  /// 2026-10-22 (мобилка, деньги): строки оплаты для экрана, PDF, текста и печати. В долг — «Внесено / Долг / Вернуть до»
  /// (раньше «В долг: 300» при чеке 1000 и предоплате 300 — будто весь чек 300).
  List<(String, String)> get paymentLines {
    if (payment == 'debt') {
      final paid = received ?? 0;
      final debt = roundMoney(total - paid);
      final d = debtDue;
      return [
        ('Оплата', paymentText),
        if (paid > 0) ('Внесено', money(paid)),
        ('Долг', money(debt < 0 ? 0 : debt)),
        if (d != null) ('Вернуть до', '${d.day.toString().padLeft(2, '0')}.${d.month.toString().padLeft(2, '0')}.${d.year}'),
      ];
    }
    return [(paymentText, money(received ?? total))];
  }

  double get subtotal => roundMoney(lines.fold(0.0, (s, l) => s + l.qty * l.price));
  int get itemCount => lines.length;

  String get title {
    if (fiscal == null) return isReturn ? 'ТОВАРНЫЙ ЧЕК · ВОЗВРАТ' : 'ТОВАРНЫЙ ЧЕК';
    return isReturn ? 'КАССОВЫЙ ЧЕК · ВОЗВРАТ ПРИХОДА' : 'КАССОВЫЙ ЧЕК · ПРИХОД';
  }

  String get paymentText => switch (payment) {
    'cash' => 'Наличными',
    'card' || 'transfer' => 'Безналичными',
    'debt' => 'В долг',
    'mixed' => 'Смешанная оплата',
    _ => payment,
  };

  String get dateText =>
      '${at.day.toString().padLeft(2, '0')}.${at.month.toString().padLeft(2, '0')}.${at.year} ${at.hour.toString().padLeft(2, '0')}:${at.minute.toString().padLeft(2, '0')}';

  /// Номер с нулями, как на кассе: 1744 → 001744.
  String get numberText => number.isEmpty ? '—' : (int.tryParse(number) != null ? number.padLeft(6, '0') : number);

  /// Чек сразу после оплаты — из корзины (сервер мог ещё не ответить: offline).
  factory Receipt.fromCart({
    required Session? session,
    required Cart cart,
    required String? number,
    required String payment,
    double? received,
    String client = '',
    Map<String, dynamic>? response,
    bool offline = false,
    DateTime? debtDue,
  }) {
    final lines = [for (final l in cart.lines) ReceiptLine(name: l.name, qty: l.qty, unit: l.unit, price: l.price, discount: l.discount, total: l.total)];
    final total = cart.total;
    return Receipt(
      shop: _shop(session),
      inn: session?.companyInn ?? '',
      address: session?.companyAddress ?? '',
      number: number ?? '',
      shift: _shiftNo(response),
      at: DateTime.now(),
      cashier: session?.userName ?? '',
      cashbox: session?.cashboxName ?? '',
      client: client,
      lines: lines,
      discount: roundMoney(cart.lineDiscounts + cart.orderDiscount),
      total: total,
      payment: payment,
      received: received,
      change: payment == 'cash' && received != null && received > total ? roundMoney(received - total) : 0,
      fiscal: ReceiptFiscal.parse(response),
      eReceiptUrl: qr.eReceiptUrl(response),
      offline: offline,
      debtDue: debtDue,
    );
  }

  /// Чек из «Чеков» — по ответу сервера `pos/sales/{id}/`.
  factory Receipt.fromSale({
    required Session? session,
    required Map<String, dynamic> sale,
    required String number,
    required double total,
    required DateTime? at,
    required String payment,
    String status = '',
  }) {
    dynamic items = sale['items'] ?? sale['lines'];
    if (items == null && sale['cart'] is Map) items = (sale['cart'] as Map)['items'];
    final lines = <ReceiptLine>[];
    for (final m in asList(items)) {
      final qty = num0(m, 'quantity') > 0 ? num0(m, 'quantity') : num0(m, 'qty');
      final price = num0(m, 'unit_price') > 0 ? num0(m, 'unit_price') : num0(m, 'price');
      final lineTotal = num0(m, 'total') > 0 ? num0(m, 'total') : (num0(m, 'line_total') > 0 ? num0(m, 'line_total') : roundMoney(qty * price));
      lines.add(
        ReceiptLine(
          name: str(m, 'product_name') ?? str(m, 'name') ?? str(m, 'title') ?? '',
          qty: qty,
          // 2026-10-27 (М4): штука из упаковки (sale_package_id в строке продажи) — «3 шт × 12», а не единица упаковки товара
          unit: (str(m, 'sale_package_id') ?? '').isNotEmpty ? 'шт' : (str(m, 'unit') ?? ''),
          price: price,
          discount: num0(m, 'discount_total') > 0 ? num0(m, 'discount_total') : roundMoney((qty * price - lineTotal).clamp(0, double.infinity).toDouble()),
          total: lineTotal,
        ),
      );
    }
    final gross = roundMoney(lines.fold(0.0, (s, l) => s + l.qty * l.price));
    final client = sale['client'] is Map
        ? (str((sale['client'] as Map).cast<String, dynamic>(), 'full_name') ?? str((sale['client'] as Map).cast<String, dynamic>(), 'name') ?? '')
        : (str(sale, 'client_name') ?? '');
    final received = num0(sale, 'cash_received') > 0 ? num0(sale, 'cash_received') : (num0(sale, 'received') > 0 ? num0(sale, 'received') : null);
    return Receipt(
      shop: _shop(session),
      inn: session?.companyInn ?? '',
      address: session?.companyAddress ?? '',
      isReturn: status.contains('return') || str(sale, 'kind') == 'return',
      number: number,
      shift: _shiftNo(sale),
      at: at ?? DateTime.tryParse(str(sale, 'created_at') ?? '')?.toLocal() ?? DateTime.now(),
      cashier:
          // 2026-10-26 (живой аудит, П4): сервер отдаёт кассира в cashier_display, а кассу — в cashbox_name
          str(sale, 'cashier_display') ??
          str(sale, 'cashier_name') ??
          str(sale, 'user_display') ??
          (sale['user'] is Map ? str((sale['user'] as Map).cast<String, dynamic>(), 'full_name') ?? '' : ''),
      cashbox: str(sale, 'cashbox_name') ?? str(sale, 'cashbox_display') ?? '',
      client: client,
      lines: lines,
      discount: roundMoney((gross - total).clamp(0, double.infinity).toDouble()),
      total: total,
      payment: payment,
      received: received,
      change: num0(sale, 'change') > 0 ? num0(sale, 'change') : (received != null && received > total ? roundMoney(received - total) : 0),
      fiscal: ReceiptFiscal.parse(sale),
      eReceiptUrl: qr.eReceiptUrl(sale),
    );
  }

  static String _shop(Session? s) => (s?.companyName.isNotEmpty ?? false) ? s!.companyName : 'NurCRM';

  static String _shiftNo(Map<String, dynamic>? m) {
    if (m == null) return '';
    final shift = m['shift'];
    if (shift is Map) return str(shift.cast<String, dynamic>(), 'number') ?? '';
    return str(m, 'shift_number') ?? '';
  }

  /// Текст для мессенджера и QR без ссылки — те же реквизиты строками.
  String toText() {
    const dash = '--------------------------------';
    final b = StringBuffer()..writeln(shop);
    if (inn.isNotEmpty) b.writeln('ИНН $inn');
    if (address.isNotEmpty) b.writeln(address);
    b
      ..writeln(title)
      ..writeln('Чек № $numberText${shift.isEmpty ? '' : ' · смена № $shift'}')
      ..writeln(dateText);
    if (cashier.isNotEmpty) b.writeln('Кассир: $cashier');
    if (client.isNotEmpty) b.writeln('Покупатель: $client');
    b.writeln(dash);
    for (var i = 0; i < lines.length; i++) {
      final l = lines[i];
      b
        ..writeln('${i + 1}. ${l.name}')
        ..writeln('  ${qtyText(l.qty)}${l.unit.isEmpty ? '' : ' ${l.unit}'} × ${money(l.price)} = ${money(l.total)}');
      if (l.discount > 0) b.writeln('  скидка −${money(l.discount)}');
    }
    b.writeln(dash);
    if (discount > 0) b.writeln('Скидка: −${money(discount)}');
    b.writeln('ИТОГО: ${money(total)} сом');
    for (final (k, v) in paymentLines) {
      b.writeln('$k: $v');
    }
    if (change > 0) b.writeln('Сдача: ${money(change)}');
    final f = fiscal;
    if (f != null) {
      for (final t in f.taxes) {
        b.writeln('${t.name}: ${money(t.amount)}');
      }
      if (f.regNumber.isNotEmpty) b.writeln('РН ККМ: ${f.regNumber}');
      if (f.fiscalModule.isNotEmpty) b.writeln('ФМ: ${f.fiscalModule}');
      b.writeln('ФД: ${f.fd}${f.fpd.isEmpty ? '' : '  ФПД: ${f.fpd}'}');
    }
    if (eReceiptUrl != null) b.writeln('Электронный чек: $eReceiptUrl');
    b.writeln('Спасибо за покупку!');
    return b.toString();
  }

  String get fileName => '${isReturn ? 'Возврат' : 'Чек'} №$numberText';
}
