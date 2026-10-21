import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/features/admin/quotes.dart';
import 'package:nurmarket/features/admin/waybill_pdf.dart';
import 'package:nurmarket/features/payment/payment_screen.dart' show samePhone;

// 13.10: КП и счета на телефоне — итог со скидкой, остаток к оплате, просрочка, сохранение в JSON.
void main() {
  QuoteDoc doc({bool quote = false, DateTime? until}) => QuoteDoc(
    id: '1',
    number: 'СЧ-0001',
    isQuote: quote,
    lines: [QuoteLine(productId: 'a', name: 'Вода', qty: 12, price: 62), QuoteLine(productId: 'b', name: 'Сок', qty: 2, price: 110.5)],
    discount: 21,
    until: until ?? DateTime.now().add(const Duration(days: 3)),
    status: 'issued',
    createdAt: DateTime(2026, 10, 13),
  );

  test('total minus discount and left to pay', () {
    final d = doc()..paid = 500;
    expect(d.total, 744 + 221 - 21);
    expect(d.left, 944 - 500);
  });
  test('overdue only for unpaid invoices', () {
    expect(doc(until: DateTime(2026, 1, 1)).overdue, isTrue);
    expect(doc(quote: true, until: DateTime(2026, 1, 1)).overdue, isFalse);
    expect((doc(until: DateTime(2026, 1, 1))..status = 'paid').overdue, isFalse);
  });
  test('json round trip', () {
    final d = QuoteDoc.fromJson(doc().toJson());
    expect(d.lines.length, 2);
    expect(d.lines[1].price, 110.5);
    expect(d.number, 'СЧ-0001');
  });

  // 2026-10-21: проверка КП и счетов — скидка процентом, округление строк, номера без повторов, дубли строк, PDF.
  test('скидка: сумма или процент, не больше суммы строк, до тыйына', () {
    expect(parseDiscount('', 965), 0);
    expect(parseDiscount('10%', 965), 96.5);
    expect(parseDiscount('12,5', 965), 12.5);
    expect(parseDiscount('3%', 333.33), 10);
    expect(parseDiscount('2000', 965), 965);
    expect(parseDiscount('-50', 965), 0);
    expect(parseDiscount('абв', 965), 0);
  });

  test('итог = сумма округлённых строк, как в таблице PDF', () {
    final d = QuoteDoc(
      id: '2',
      number: 'КП-0002',
      isQuote: true,
      lines: [QuoteLine(productId: 'a', name: 'Сыр', unit: 'кг', qty: 0.333, price: 47.5), QuoteLine(productId: 'b', name: 'Сыр 2', unit: 'кг', qty: 0.333, price: 47.5)],
      until: DateTime(2026, 11, 1),
      status: 'draft',
      createdAt: DateTime(2026, 10, 21),
    );
    // 0,333 × 47,5 = 15,8175 → 15,82 в каждой строке; итог 31,64, а не 31,635 → 31,64/31,63.
    expect(d.lines.first.sum, 15.82);
    expect(d.subtotal, 31.64);
    final r = quoteReceipt(d);
    expect(r.lines.fold<double>(0, (s, l) => s + l.total), closeTo(d.total, 0.001));
  });

  test('тот же товар — количество в одной строке, «Из текущего чека» дважды не двоит строки', () {
    final lines = <QuoteLine>[];
    addQuoteLine(lines, QuoteLine(productId: 'w', name: 'Вода', price: 85));
    addQuoteLine(lines, QuoteLine(productId: 'w', name: 'Вода', price: 85, qty: 2));
    expect(lines.length, 1);
    expect(lines.single.qty, 3);
    // Другая цена (например, со скидкой в чеке) — отдельная строка, цену не теряем.
    addQuoteLine(lines, QuoteLine(productId: 'w', name: 'Вода', price: 80));
    expect(lines.length, 2);
    // Из каталога — к строке товара, даже если цену в ней поменяли.
    addQuoteLine(lines, QuoteLine(productId: 'w', name: 'Вода', price: 85), anyPrice: true);
    expect(lines.length, 2);
    expect(lines.first.qty, 4);
    // Строки без товара (вручную) не сливаются.
    addQuoteLine(lines, QuoteLine(productId: '', name: 'Доставка', price: 200));
    addQuoteLine(lines, QuoteLine(productId: '', name: 'Доставка', price: 200));
    expect(lines.length, 4);
  });

  test('номер документа не повторяется, даже если счётчик отстал', () async {
    final db = AppDatabase(NativeDatabase.memory());
    final store = QuotesStore(db);
    expect(await store.nextNumber(true), 'КП-0001');
    expect(await store.nextNumber(true), 'КП-0002');
    expect(await store.nextNumber(false), 'СЧ-0001');
    // База восстановлена: документы есть, счётчик сброшен.
    // 2026-10-21: разные id — у одного id теперь один документ (список общий с ПК, слияние по id).
    await store.save([
      QuoteDoc.fromJson({...doc(quote: true).toJson(), 'id': 'k41', 'number': 'КП-0041'}),
      QuoteDoc.fromJson({...doc().toJson(), 'id': 's7', 'number': 'СЧ-0007'}),
    ]);
    await db.setSetting('quotes.counter.kp', '0');
    await db.setSetting('quotes.counter.inv', '0');
    expect(await store.nextNumber(true), 'КП-0042');
    expect(await store.nextNumber(false), 'СЧ-0008');
    await db.close();
  });

  test('компания и поиск: номер, клиент, компания, ИНН, телефон цифрами', () {
    final d = QuoteDoc.fromJson((doc()
          ..company = 'ОсОО «Үмүт»'
          ..client = 'Бакыт'
          ..phone = '+996 (555) 77-78-88'
          ..inn = '01234567890123')
        .toJson());
    expect(d.company, 'ОсОО «Үмүт»');
    expect(d.buyer, 'ОсОО «Үмүт» · Бакыт');
    expect(d.matches('үмүт'), isTrue);
    expect(d.matches('сч-0001'), isTrue);
    expect(d.matches('0555 777 888'.substring(5)), isTrue);
    expect(d.matches('0123456789'), isTrue);
    expect(d.matches('Асель'), isFalse);
    expect(quoteReceipt(d).client, 'ОсОО «Үмүт», Бакыт, ИНН 01234567890123, +996 (555) 77-78-88');
  });

  test('КП после срока — «срок истёк», это не просрочка долга', () {
    final q = doc(quote: true, until: DateTime(2026, 1, 1))..status = 'sent';
    expect(q.expired, isTrue);
    expect(q.overdue, isFalse);
    expect((doc(quote: true, until: DateTime(2026, 1, 1))..status = 'accepted').expired, isFalse);
  });

  test('тот же телефон в разной записи', () {
    expect(samePhone('0555 777 888', '+996 555 777 888'), isTrue);
    expect(samePhone('0555777888', '0555777889'), isFalse);
    expect(samePhone('', ''), isFalse);
  });

  test('PDF: 80 строк с кыргызскими буквами — несколько страниц, ничего не обрезано', () async {
    TestWidgetsFlutterBinding.ensureInitialized();
    final d = doc(quote: true)
      ..company = 'ОсОО «Үңкүр-Өнөр»'
      ..lines = [for (var i = 0; i < 80; i++) QuoteLine(productId: '$i', name: 'Түштүк жемиши ${i + 1} — очень длинное название товара с упаковкой и весом 1,5 кг', qty: 2, price: 85)];
    final r = quoteReceipt(d, shop: 'Нур Маркет', inn: '01234567890123', address: 'Бишкек, Чүй 1');
    final one = await docPdfBytes(quoteReceipt(doc(quote: true), shop: 'Нур Маркет'), DocKind.quote, note: 'до 01.11.2026');
    final many = await docPdfBytes(r, DocKind.quote, note: 'до 01.11.2026', requisites: 'р/с 1234567890\nБИК 123001', paid: 0);
    int pages(List<int> b) => RegExp(r'/Type\s*/Page(?!s)').allMatches(String.fromCharCodes(b)).length;
    expect(pages(one), 1);
    expect(pages(many), greaterThan(1));
  });
}
