import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/invoice/invoice_match.dart';
import 'package:nurmarket/core/invoice/invoice_parser.dart';

// 2026-10-27: сопоставление строк накладной со складом (штрихкод → артикул → название → нечётко), поставщики по ИНН/названию,
// защита от повторной загрузки. База — в памяти, сеть не используется.
void main() {
  late AppDatabase db;
  late InvoiceMatcher matcher;

  Future<void> add(String id, String name, {String? barcode, String? sku, double price = 100, double qty = 5}) => db.upsertProduct(
    ProductsCompanion.insert(
      id: id,
      name: name,
      barcode: Value(barcode),
      sku: Value(sku),
      price: Value(price),
      quantity: Value(qty),
      searchKey: Value('${name.toLowerCase()} ${barcode ?? ''} ${sku ?? ''}'),
    ),
  );

  setUp(() async {
    db = AppDatabase(NativeDatabase.memory());
    matcher = InvoiceMatcher(db);
    await add('p1', 'Вода Нур негазированная 1,5 л', barcode: '4870200221261', sku: 'SKU-1');
    await add('p2', 'Кока-Кола 2л', barcode: '5449000000996');
    await add('p4', 'Жвачка Orbit', barcode: '012345678905');
    await add('p5', 'Печенье Юбилейное 112 г', barcode: '4600000000015');
    await add('p6', 'Молоко Простоквашино 3,2% 1 л', barcode: '4600000000022');
  });
  tearDown(() async => db.close());

  InvoiceLine line({String name = '', String barcode = '', String article = ''}) => InvoiceLine(row: 1, name: name, barcode: barcode, article: article, qty: 1, cost: 10);

  group('товар склада', () {
    test('по штрихкоду; UPC-A ↔ EAN-13; ведущие нули, потерянные Excel', () async {
      expect((await matcher.find(line(barcode: '4870200221261', name: 'другое имя'))).product?.id, 'p1');
      expect((await matcher.find(line(barcode: '0012345678905'))).product?.id, 'p4', reason: 'EAN-13 с нулями ↔ UPC-A в базе');
      expect((await matcher.find(line(barcode: '12345678905'))).product?.id, 'p4', reason: 'Excel съел два нуля');
      final m = await matcher.find(line(barcode: '4870200221261'));
      expect(m.kind, MatchKind.barcode);
    });
    test('по артикулу (sku), когда штрихкода в файле нет', () async {
      final m = await matcher.find(line(article: 'SKU-1', name: 'что-то'));
      expect(m.kind, MatchKind.article);
      expect(m.product?.id, 'p1');
    });
    test('штрихкод важнее названия: код другого товара побеждает похожее имя', () async {
      final m = await matcher.find(line(barcode: '5449000000996', name: 'Вода Нур негазированная 1,5 л'));
      expect(m.product?.id, 'p2');
    });
    test('точное и нормализованное название (регистр, знаки, пробелы)', () async {
      expect((await matcher.find(line(name: 'Кока-Кола 2л'))).kind, MatchKind.name);
      final m = await matcher.find(line(name: 'кока кола 2 Л'));
      expect(m.kind, MatchKind.name);
      expect(m.product?.id, 'p2');
      expect((await matcher.find(line(name: 'ВОДА НУР негазированная 1.5 л'))).product?.id, 'p1');
    });
    test('нечётко: похожее название предлагается, но не принимается само, если уверенность ниже 0,9', () async {
      final m = await matcher.find(line(name: 'Печенье Юбилейное классическое 112 г'));
      expect(m.kind, MatchKind.fuzzy);
      expect(m.product?.id, 'p5');
      expect(m.score, greaterThanOrEqualTo(fuzzyOffer));
      expect(m.score, lessThan(fuzzyAuto));
      final w = await matcher.find(line(name: 'Простоквашино Молоко 3.2% 1л'));
      expect(w.product?.id, 'p6');
    });
    test('Ск-13: два близких кандидата («Winston Blue» и «Winston Blue SS») — автоподстановки нет, выбирает человек', () async {
      await db.upsertProduct(ProductsCompanion.insert(id: 'w1', name: 'Winston Blue', searchKey: const Value('winston blue')));
      await db.upsertProduct(ProductsCompanion.insert(id: 'w2', name: 'Winston Blue SS', searchKey: const Value('winston blue ss')));
      final m = await matcher.find(line(name: 'Winston Blue S'));
      expect(m.kind, MatchKind.fuzzy);
      expect(m.second, greaterThan(0.5), reason: 'второй кандидат близко');
      expect(m.autoPick, isFalse);
    });
    test('Ск-13: один явный кандидат с оценкой ≥ 0,9 подставляется сам', () {
      expect(LineMatch(MatchKind.fuzzy, score: 0.95, second: 0.5).autoPick, isTrue);
      expect(LineMatch(MatchKind.fuzzy, score: 0.95, second: 0.9).autoPick, isFalse);
      expect(LineMatch(MatchKind.fuzzy, score: 0.85, second: 0.1).autoPick, isFalse);
    });
    test('другая фасовка — не тот же товар: «Кока-Кола 1л» не подбирается к «2л»', () async {
      final m = await matcher.find(line(name: 'Кока-Кола 1л'));
      expect(m.kind, MatchKind.none);
      expect(m.product, isNull);
    });
    test('незнакомый товар — none; услуги не подбираются', () async {
      expect((await matcher.find(line(name: 'Ноутбук бизнес-серии 15.6"'))).kind, MatchKind.none);
      expect((await matcher.find(line())).kind, MatchKind.none);
    });
  });

  group('похожесть названий', () {
    test('порядок слов, регистр, знаки не важны; разные числа снижают оценку', () {
      expect(nameSimilarity('Молоко Простоквашино 1 л', 'простоквашино МОЛОКО 1 л'), greaterThan(0.95));
      expect(nameSimilarity('Сок 1л', 'Сок 2л'), lessThan(0.7));
      expect(nameSimilarity('', 'Сок'), 0);
      expect(nameSimilarity('Совсем другое', 'Ноутбук'), lessThan(0.4));
    });
  });

  group('поставщики', () {
    const list = [
      SupplierRef('s1', 'ОсОО «Бишкек Трейд»', '01234567890123'),
      SupplierRef('s2', 'ИП Иванов', '22222222222222'),
      SupplierRef('s3', 'ОсОО Ала-Тоо Опт', ''),
      SupplierRef('s4', 'Ала-Тоо Азык', ''),
    ];
    test('по ИНН (даже с другим названием), затем по названию без «ОсОО», кавычек и регистра', () {
      var r = matchSupplier(list, name: 'совсем другое', inn: '0123 4567 890123');
      expect((r.supplier?.id, r.by), ('s1', SupplierBy.inn));
      r = matchSupplier(list, name: 'ОСОО "Бишкек Трейд"', inn: '');
      expect((r.supplier?.id, r.by), ('s1', SupplierBy.name));
      r = matchSupplier(list, name: 'иванов', inn: '');
      expect(r.supplier?.id, 's2');
    });
    test('не нашёлся или названию отвечают несколько — не угадываем', () {
      expect(matchSupplier(list, name: 'Неизвестная фирма', inn: '999').supplier, isNull);
      expect(matchSupplier(list, name: 'Ала-Тоо', inn: '').supplier, isNull, reason: 'два «Ала-Тоо…»');
      expect(matchSupplier(list, name: '', inn: '').by, SupplierBy.none);
    });
  });

  group('«Накладная уже загружалась»', () {
    ParsedInvoice inv({String hash = 'h1', String number = '104', DateTime? date}) =>
        ParsedInvoice(format: InvoiceFormat.xlsx, number: number, date: date ?? DateTime(2026, 10, 27), lines: const [], fileHash: hash);

    setUp(InvoiceGuard.resetPending);

    test('ключи зависят от файла, номера и даты, но НЕ от поставщика в окне (ревью Ск-2)', () {
      final a = InvoiceGuard.keys(inv());
      expect(InvoiceGuard.keys(inv()), a);
      expect(InvoiceGuard.keys(inv(number: '105')).first, a.first, reason: 'K1 — по файлу');
      expect(InvoiceGuard.keys(inv(number: '105')), isNot(a));
      expect(InvoiceGuard.keys(inv(hash: 'h2')).first, isNot(a.first));
      expect(InvoiceGuard.keys(inv(number: '')).length, 1, reason: 'без номера второго ключа нет');
    });
    test('формула ключей — та же, что у кассы: K1 file, K2 doc (с ИНН файла), K3 sum (с итогом)', () {
      String h(String s) => sha256.convert(utf8.encode(s)).toString();
      final withTotal = ParsedInvoice(format: InvoiceFormat.xlsx, number: ' А-104 ', date: DateTime(2026, 10, 7), supplierInn: '0123 4567 890123', lines: const [], fileTotal: 1234.56, fileHash: 'ABC');
      expect(InvoiceGuard.keys(withTotal), [
        h('file|abc'),
        h('doc|а-104|2026-10-7|01234567890123'),
        h('sum|а-104|2026-10-7|123456'),
      ]);
      // ИНН не из 9–14 цифр не считается; итога нет — K2 без итога, K3 нет
      final noInn = ParsedInvoice(format: InvoiceFormat.xlsx, number: '7', supplierInn: '12', lines: const [], fileHash: 'x');
      expect(InvoiceGuard.keys(noInn), [h('file|x'), h('doc|7||')]);
      // итог есть, ИНН нет: K2 не строится (иначе совпало бы с чужой накладной того же номера), остаются file и sum
      final noInnTotal = ParsedInvoice(format: InvoiceFormat.xlsx, number: '7', lines: const [], fileTotal: 10, fileHash: 'x');
      expect(InvoiceGuard.keys(noInnTotal), [h('file|x'), h('sum|7||1000')]);
    });
    test('та же накладная после смены поставщика («без поставщика» → «＋ создан») узнаётся', () async {
      // раньше ключ содержал поставщика — вторая загрузка проходила, приход и долг удваивались
      await InvoiceGuard.markPosted(db, InvoiceGuard.keys(inv()));
      expect(await InvoiceGuard.status(db, InvoiceGuard.keys(inv())), 'posted');
      expect(await InvoiceGuard.isPosted(db, InvoiceGuard.keys(inv())), isTrue);
      InvoiceGuard.unmarkPending(InvoiceGuard.keys(inv()));
    });
    test('накладная, проведённая прежней версией (ключи с поставщиком), тоже узнаётся через legacyKeys', () async {
      await InvoiceGuard.markPosted(db, InvoiceGuard.legacyKeys(inv(), 's1'));
      expect(await InvoiceGuard.status(db, [...InvoiceGuard.keys(inv()), ...InvoiceGuard.legacyKeys(inv(), 's1')]), 'posted');
    });
    test('частичное проведение: повторная отметка не дублирует запись', () async {
      final keys = InvoiceGuard.keys(inv());
      await InvoiceGuard.markPosted(db, keys, number: '104');
      await InvoiceGuard.markPosted(db, keys, number: '104');
      expect((jsonDecode((await db.getSetting(InvoiceGuard.setting))!) as List).length, 1);
    });
    test('добавлена в приход → «ещё не проведён»; проведена → «уже загружалась», переживает перезапуск (настройки базы)', () async {
      final keys = InvoiceGuard.keys(inv());
      expect(await InvoiceGuard.status(db, keys), isNull);
      InvoiceGuard.markPending(keys);
      expect(await InvoiceGuard.status(db, keys), 'pending');
      await InvoiceGuard.markPosted(db, keys, number: '104', supplier: 'Бишкек Трейд');
      InvoiceGuard.resetPending(); // «перезапуск»: память забыта, база помнит
      expect(await InvoiceGuard.status(db, keys), 'posted');
    });
    test('та же накладная другим файлом (другой хэш) узнаётся по номеру + дате; другой номер — нет', () async {
      await InvoiceGuard.markPosted(db, InvoiceGuard.keys(inv(hash: 'xlsx')));
      expect(await InvoiceGuard.status(db, InvoiceGuard.keys(inv(hash: 'xml'))), 'posted');
      expect(await InvoiceGuard.status(db, InvoiceGuard.keys(inv(hash: 'xml', number: '999'))), isNull);
    });
    test('испорченная запись в настройках не роняет проверку; хранится не больше 300', () async {
      await db.setSetting(InvoiceGuard.setting, '{не json');
      expect(await InvoiceGuard.status(db, InvoiceGuard.keys(inv())), isNull);
      for (var i = 0; i < InvoiceGuard.keep + 20; i++) {
        await InvoiceGuard.markPosted(db, ['k$i']);
      }
      expect(await InvoiceGuard.status(db, ['k0']), isNull, reason: 'самые старые вытеснены');
      expect(await InvoiceGuard.status(db, ['k${InvoiceGuard.keep + 19}']), 'posted');
    });
  });
}
