// 2026-10-22 (мобилка, админ): проверки пакетов П6 (печать), П13 (КП, счета, накладные), П15 (прокат), П16 (журнал и фото).
// Сервер NurCRM не трогается — всё на фейках.
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/print/escpos.dart';
import 'package:nurmarket/core/print/printer_service.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/core/util/photo_finder.dart';
import 'package:nurmarket/features/admin/quotes.dart';
import 'package:nurmarket/features/admin/waybill_pdf.dart';
import 'package:nurmarket/features/receipt/receipt.dart';
import 'package:nurmarket/features/rentals/rentals.dart';

bool _contains(List<int> hay, List<int> needle) {
  for (var i = 0; i + needle.length <= hay.length; i++) {
    var ok = true;
    for (var j = 0; j < needle.length && ok; j++) {
      ok = hay[i + j] == needle[j];
    }
    if (ok) return true;
  }
  return false;
}

Receipt _receipt({String payment = 'cash', String? url, bool isReturn = false, String number = '77'}) => Receipt(
  shop: 'NBS',
  number: number,
  at: DateTime(2026, 10, 22, 10),
  lines: const [ReceiptLine(name: 'Вода', qty: 1, price: 50, total: 50)],
  total: 50,
  payment: payment,
  eReceiptUrl: url,
  isReturn: isReturn,
);

const _drawerEsc = [0x1B, 0x70, 0, 25, 250];
const _drawerDle = [0x10, 0x14, 0x01, 0, 2];
const _qrPrint = [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30];

Product _p(String id, double price) => Product(
  id: id,
  name: 'p$id',
  price: price,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: 5,
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: 'p$id',
);

/// Сервер прокатов в памяти: страницы по 2, фильтр status.
class _RentalsApi extends ApiClient {
  _RentalsApi(this.rows) : super(SessionStore());
  final List<Map<String, dynamic>> rows;
  final calls = <String>[];
  @override
  Future<dynamic> get(String path, {Map<String, dynamic>? query, Duration? timeout}) async {
    calls.add('$path ${query ?? {}}');
    final status = query?['status'];
    final page = (query?['page'] as int?) ?? 1;
    final match = rows.where((r) => status == null || r['status'] == status).toList();
    final slice = match.skip((page - 1) * 2).take(2).toList();
    return {'results': slice, 'next': page * 2 < match.length ? 'next' : null};
  }
}

void main() {
  setUpAll(() => Audit.testDirectory = Directory.systemTemp.createTempSync('nm_admin_docs_'));

  // ---------------------------------------------------------------- П6 печать
  group('П6 печать', () {
    test('ящик: ESC p и DLE DC4 после отрезки, только наличные; QR GS ( k с адресом чека', () {
      final b = receiptToEscPos(_receipt(url: 'https://e.kg/r/1'), openDrawer: true);
      expect(_contains(b, _drawerEsc), isTrue);
      expect(_contains(b, _drawerDle), isTrue);
      expect(_contains(b, _qrPrint), isTrue);
      expect(_contains(b, 'https://e.kg/r/1'.codeUnits), isTrue);
      // ящик — после отрезки GS V
      final cut = [0x1D, 0x56, 0x42, 0x00];
      int idx(List<int> n) {
        for (var i = 0; i + n.length <= b.length; i++) {
          if ([for (var j = 0; j < n.length; j++) b[i + j] == n[j]].every((x) => x)) return i;
        }
        return -1;
      }

      expect(idx(_drawerEsc) > idx(cut), isTrue);
      // без адреса — нет QR; выключено — нет QR; без флага — нет ящика
      expect(_contains(receiptToEscPos(_receipt()), _qrPrint), isFalse);
      expect(_contains(receiptToEscPos(_receipt(url: 'https://e.kg/r/1'), qr: false), _qrPrint), isFalse);
      expect(_contains(receiptToEscPos(_receipt()), _drawerEsc), isFalse);
      // длина данных QR: pL pH = длина + 3
      final store = [0x1D, 0x28, 0x6B, 'https://e.kg/r/1'.length + 3, 0x00, 0x31, 0x50, 0x30];
      expect(_contains(b, store), isTrue);
    });

    test('ящик нужен только для продажи за наличные или смешанной', () {
      expect(receiptNeedsDrawer(_receipt()), isTrue);
      expect(receiptNeedsDrawer(_receipt(payment: 'mixed')), isTrue);
      expect(receiptNeedsDrawer(_receipt(payment: 'card')), isFalse);
      expect(receiptNeedsDrawer(_receipt(payment: 'debt')), isFalse);
      expect(receiptNeedsDrawer(_receipt(isReturn: true)), isFalse);
      expect(drawerPulse(pin: 1), [0x1B, 0x40, 0x1B, 0x70, 1, 25, 250]);
    });

    test('настройки ящика и QR сохраняются; старые настройки — QR включён, ящик выключен', () {
      const c = PrinterConfig(kind: PrinterKind.network, address: '10.0.0.5', drawer: true, drawerPin: 1, qr: false);
      final back = PrinterConfig.fromJson(jsonString(c.toJson()));
      expect(back.drawer, isTrue);
      expect(back.drawerPin, 1);
      expect(back.qr, isFalse);
      final old = PrinterConfig.fromJson('{"kind":"bluetooth","address":"AA"}');
      expect(old.qr, isTrue);
      expect(old.drawer, isFalse);
    });

    test('обрыв после начала отправки — без повтора (нет второго чека); до подключения — один повтор', () async {
      final s = PrinterService(null)..retryDelay = Duration.zero;
      const c = PrinterConfig(kind: PrinterKind.network, address: '10.0.0.5');
      var calls = 0;
      s.sendOverride = (_, _) async {
        calls++;
        throw const PrinterException(PrinterProblem.io, 'обрыв', true);
      };
      await expectLater(s.send(c, Uint8List(3)), throwsA(isA<PrinterException>()));
      expect(calls, 1);
      calls = 0;
      s.sendOverride = (_, _) async {
        calls++;
        if (calls == 1) throw const PrinterException(PrinterProblem.timeout);
      };
      await s.send(c, Uint8List(3));
      expect(calls, 2);
    });

    test('двойное нажатие «Печать» — один чек; ящик по чеку — один раз', () async {
      final s = PrinterService(null)..retryDelay = Duration.zero;
      const c = PrinterConfig(kind: PrinterKind.network, address: '10.0.0.5', drawer: true);
      final sent = <Uint8List>[];
      s.sendOverride = (_, b) async {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        sent.add(b);
      };
      final r = _receipt();
      await Future.wait([s.printReceipt(r, config: c, afterSale: true), s.printReceipt(r, config: c, afterSale: true)]);
      expect(sent.length, 1);
      expect(_contains(sent.single, _drawerEsc), isTrue);
      // повторная печать того же чека позже — чек есть, ящика нет
      await s.printReceipt(r, config: c, afterSale: true);
      expect(sent.length, 2);
      expect(_contains(sent.last, _drawerEsc), isFalse);
      // без автопечати: ящик отдельно, один раз на чек
      final r2 = _receipt(number: '78');
      await s.drawerAfterSale(r2, c);
      await s.drawerAfterSale(r2, c);
      expect(sent.length, 3);
      // карта — ящик не нужен
      await s.drawerAfterSale(_receipt(number: '79', payment: 'card'), c);
      expect(sent.length, 3);
    });

    test('задания идут на принтер по одному', () async {
      final s = PrinterService(null)..retryDelay = Duration.zero;
      const c = PrinterConfig(kind: PrinterKind.network, address: '10.0.0.5');
      var busy = 0, maxBusy = 0;
      s.sendOverride = (_, _) async {
        busy++;
        maxBusy = busy > maxBusy ? busy : maxBusy;
        await Future<void>.delayed(const Duration(milliseconds: 10));
        busy--;
      };
      await Future.wait([for (var i = 0; i < 4; i++) s.printReceipt(_receipt(number: '${100 + i}'), config: c)]);
      expect(maxBusy, 1);
    });
  });

  // ---------------------------------------------------------------- П13 КП и счета
  group('П13 КП, счета, накладные', () {
    QuoteDoc doc({bool quote = false, Map<String, dynamic>? extra, DateTime? until}) => QuoteDoc(
      id: '1',
      number: quote ? 'КП-0001' : 'СЧ-0001',
      isQuote: quote,
      lines: [QuoteLine(productId: 'a', name: 'Вода', qty: 10, price: 50), QuoteLine(productId: 'zz', name: 'Нет в каталоге', qty: 1, price: 100)],
      discount: 60,
      until: until ?? DateTime(2026, 1, 1),
      status: 'issued',
      createdAt: DateTime(2026, 10, 1),
      extra: extra,
    );

    test('накладная ПК (kind: waybill) — не счёт: не просрочена, не «не оплачена», вид бланка — накладная', () {
      final wb = QuoteDoc.fromJson({...doc().toJson(), 'number': 'НК-0003', 'kind': 'waybill', 'status': 'sent'});
      expect(wb.isWaybill, isTrue);
      expect(wb.isInvoice, isFalse);
      expect(wb.overdue, isFalse);
      expect(quoteDocKind(wb), DocKind.waybill);
      // поле kind не теряется при записи (формат ПК)
      expect(wb.toJson()['kind'], 'waybill');
      expect(wb.toJson()['quote'], isFalse);
      // обычный счёт по-прежнему просрочен
      expect(doc().overdue, isTrue);
      expect(quoteDocKind(doc()), DocKind.invoice);
      expect(quoteDocKind(doc(quote: true)), DocKind.quote);
    });

    test('НДС «в том числе» как на ПК: итог × ставка / (100 + ставка)', () {
      expect(vatIncluded(1120, 12), 120);
      expect(vatIncluded(1000, 12), 107.14);
      expect(vatIncluded(1000, 0), 0);
      expect(vatLine(1120, 12), 'В т.ч. НДС 12%: 120 сом');
      expect(vatLine(1000, 0), isNull);
      final d = doc(extra: {'vat': 12});
      expect(d.vatPercent, 12);
      expect(doc().vatPercent, 0);
      expect(doc(extra: {'vat': '12,5'}).vatPercent, 12.5);
    });

    test('«Продать»: цена документа и скидка документа переходят в чек; чего нет в каталоге — список', () async {
      final c = ProviderContainer();
      addTearDown(c.dispose);
      final cart = c.read(cartProvider.notifier);
      final d = doc();
      final missing = await sellDocToCart(d, (id) async => id == 'a' ? _p('a', 62) : null, cart);
      final st = c.read(cartProvider);
      expect(missing, ['Нет в каталоге']);
      expect(st.lines.single.price, 50); // цена документа, а не каталога
      expect(st.lines.single.qty, 10);
      expect(st.orderDiscountSum, 60);
      expect(st.total, 500 - 60);
    });

    test('PDF с НДС собирается', () async {
      TestWidgetsFlutterBinding.ensureInitialized();
      final bytes = await docPdfBytes(quoteReceipt(doc(extra: {'vat': 12}), shop: 'NBS'), DocKind.invoice, vatPercent: 12);
      expect(bytes.length, greaterThan(1000));
    });
  });

  // ---------------------------------------------------------------- П15 прокат
  group('П15 прокат', () {
    Rental rental({double deposit = 500, bool document = false, DateTime? to}) =>
        Rental('r1', 5, 'Айбек', 'active', false, DateTime(2026, 10, 1), to ?? DateTime(2026, 10, 5), deposit, document ? 'Паспорт' : '', const [], documentDeposit: document);

    test('штраф сверх залога не теряется: 800 при залоге 500 — удержано 500, в чек 300', () {
      final s = rentalSettlement(rental(), 800);
      expect(s.withheld, 500);
      expect(s.back, 0);
      expect(s.excess, 300);
      final ok = rentalSettlement(rental(), 200);
      expect(ok.withheld, 200);
      expect(ok.back, 300);
      expect(ok.excess, 0);
      // залог документом — весь штраф в чек
      final doc = rentalSettlement(rental(deposit: 0, document: true), 150);
      expect(doc.withheld, 0);
      expect(doc.excess, 150);
      expect(rentalSettlement(rental(), -5).excess, 0);
    });

    test('пеня: сутки просрочки × пеня за сутки', () {
      expect(rentalLateFee(DateTime(2026, 10, 5), DateTime(2026, 10, 8, 18), 50), (days: 3, sum: 150.0));
      expect(rentalLateFee(DateTime(2026, 10, 5), DateTime(2026, 10, 5, 23), 50).days, 0);
      expect(rentalLateFee(DateTime(2026, 10, 5), DateTime(2026, 10, 9), 0).sum, 0);
      expect(rentalLateFee(null, DateTime(2026, 10, 9), 50).sum, 0);
    });

    test('список: все страницы active и overdue, без повторов и без возвращённых', () async {
      Map<String, dynamic> row(String id, String status) => {'id': id, 'number': int.parse(id), 'status': status, 'client_name': 'К$id', 'deposit_amount': '500.00', 'deposit_type': 'money'};
      final api = _RentalsApi([
        for (var i = 1; i <= 5; i++) row('$i', 'active'),
        row('6', 'overdue'),
        row('7', 'returned'),
      ]);
      final list = await RentalsRepository(api).list();
      expect(list.map((r) => r.id).toList(), ['1', '2', '3', '4', '5', '6']);
      expect(list.last.overdue, isTrue);
      expect(api.calls.where((c) => c.contains('active')).length, 3); // 3 страницы по 2
      expect(list.first.documentDeposit, isFalse);
    });

    test('двойной возврат: один запрос (флаг занятости в экране) — модель: возвращённый не активен', () {
      final r = Rental.parse({'id': 'x', 'number': 3, 'status': 'returned', 'deposit_type': 'document', 'deposit_document': 'ID'});
      expect(r.active, isFalse);
      expect(r.documentDeposit, isTrue);
    });
  });

  // ---------------------------------------------------------------- П16 журнал и фото
  group('П16 журнал и фото', () {
    test('обрезка журнала не теряет записи, сделанные во время чтения', () async {
      // 2026-10-27: запись журнала от прошлого теста могла ещё идти в старую папку и подхватить новые строки — дожидаемся её
      // и сбрасываем состояние перед сменой папки (нестабильный тест: аудит мобилки 27.10, раздел 6).
      await Audit.read();
      Audit.resetForTests();
      final dir = Directory.systemTemp.createTempSync('nm_audit_trim_');
      Audit.testDirectory = dir;
      final f = File('${dir.path}/audit.jsonl');
      final lines = StringBuffer();
      for (var i = 0; i < 5100; i++) {
        lines.writeln('{"at":"2026-10-22T10:00:00.000","user":"u","kind":"sale","text":"old $i"}');
      }
      f.writeAsStringSync(lines.toString());
      // чтение (с обрезкой) и новые записи одновременно
      final reading = Audit.read();
      final writes = [for (var i = 0; i < 20; i++) Audit.log('sale', 'new $i')];
      await reading;
      await Future.wait(writes);
      final after = await Audit.read();
      final texts = after.map((e) => e.text).toSet();
      for (var i = 0; i < 20; i++) {
        expect(texts.contains('new $i'), isTrue, reason: 'new $i');
      }
      expect(after.length, 5000);
      expect(File('${f.path}.tmp').existsSync(), isFalse);
    });

    test('тип картинки — по байтам, а не по заголовку', () {
      expect(PhotoFinder.imageType([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]), 'jpeg');
      expect(PhotoFinder.imageType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]), 'png');
      expect(PhotoFinder.imageType([0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50]), 'webp');
      expect(PhotoFinder.imageType('<!DOCTYPE html>'.codeUnits), isNull);
      expect(PhotoFinder.imageType('<svg xmlns='.codeUnits), isNull);
      expect(PhotoFinder.imageType([0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x41, 0x56, 0x49, 0x20]), isNull); // RIFF AVI
      expect(PhotoFinder.maxBytes, 8 * 1024 * 1024);
    });
  });
}

String jsonString(Map<String, Object?> m) {
  final b = StringBuffer('{');
  var first = true;
  m.forEach((k, v) {
    if (!first) b.write(',');
    first = false;
    b.write('"$k":${v is String ? '"$v"' : v}');
  });
  b.write('}');
  return b.toString();
}
