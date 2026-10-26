// 2026-10-26 (голос-управление, В2): модель таблицы звонка — форматы на 5 языках, сортировка и фильтр голосом,
// «покажи ещё», итоги, коды вместо имён для Google, карточка, хранилище таблиц. Без сети и без экранов.
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/call_table.dart';

CallTable _debts({String lang = 'ru', int n = 3}) {
  final names = <String, String>{};
  final rows = <List<Object?>>[];
  final amounts = [500.0, 5200.0, 1500.0, 80.5, 12345.5];
  for (var i = 0; i < n; i++) {
    final code = '[Д${i + 1}]';
    names[code] = 'Клиент ${i + 1} (тел. 0555-00${i + 1})';
    rows.add([code, amounts[i % amounts.length], i + 1]);
  }
  return CallTable.build(
    id: 't1',
    title: 'Долги',
    columns: [
      CallColumn('client', 'Должник', CellKind.code),
      CallColumn('debt', 'Долг', CellKind.money, sum: true),
      CallColumn('checks', 'Чеков', CellKind.qty),
    ],
    rows: rows,
    names: names,
    lang: lang,
    at: DateTime(2026, 10, 26, 12),
  );
}

void main() {
  const nb = ' ';

  group('форматы', () {
    test('деньги: пробел-разделитель, без копеек если их нет, копейки двумя цифрами', () {
      expect(CallTable.formatCell(12345, CellKind.money, 'ru'), '12${nb}345$nbсом');
      expect(CallTable.formatCell(12345.5, CellKind.money, 'ru'), '12${nb}345,50$nbсом');
      expect(CallTable.formatCell(0, CellKind.money, 'ru'), '0$nbсом');
      expect(CallTable.formatCell(999.999, CellKind.money, 'ru'), '1${nb}000$nbсом');
      expect(CallTable.formatCell(-1500, CellKind.money, 'ru'), '-1${nb}500$nbсом');
    });
    test('деньги на ky, uz, en, tr', () {
      expect(CallTable.formatCell(12345, CellKind.money, 'ky'), '12${nb}345$nbсом');
      expect(CallTable.formatCell(12345.5, CellKind.money, 'uz'), "12${nb}345,50${nb}so'm");
      expect(CallTable.formatCell(12345.5, CellKind.money, 'en'), '12,345.50${nb}som');
      expect(CallTable.formatCell(12345, CellKind.money, 'tr'), '12.345${nb}som');
    });
    test('количество с единицей на языке пользователя', () {
      expect(CallTable.formatCell(2.5, CellKind.qty, 'ru', unit: 'кг'), '2,5$nbкг');
      expect(CallTable.formatCell(2, CellKind.qty, 'ky', unit: 'шт'), '2$nbдаана');
      expect(CallTable.formatCell(2, CellKind.qty, 'uz', unit: 'шт'), '2${nb}dona');
      expect(CallTable.formatCell(2, CellKind.qty, 'en', unit: 'шт'), '2${nb}pcs');
      expect(CallTable.formatCell(2, CellKind.qty, 'tr', unit: 'шт'), '2${nb}adet');
      expect(CallTable.formatCell(1.5, CellKind.qty, 'en', unit: 'л'), '1.5${nb}L');
      expect(CallTable.formatCell(7, CellKind.qty, 'ru'), '7');
      expect(CallTable.formatCell(0.125, CellKind.qty, 'ru', unit: 'кг'), '0,125$nbкг');
    });
    test('проценты со знаком; без знака; отрицательные; десятые только для малых', () {
      expect(CallTable.formatCell(12, CellKind.percent, 'ru'), '+12$nb%');
      expect(CallTable.formatCell(12.4, CellKind.percent, 'ru'), '+12$nb%');
      expect(CallTable.formatCell(2.46, CellKind.percent, 'ru'), '+2,5$nb%');
      expect(CallTable.formatCell(-5, CellKind.percent, 'en'), '-5$nb%');
      expect(CallTable.formatCell(40, CellKind.percent, 'ru', signed: false), '40$nb%');
      expect(CallTable.formatCell(0, CellKind.percent, 'ru'), '0$nb%');
    });
    test('даты, время, статусы', () {
      final d = DateTime(2026, 10, 6, 14, 5);
      expect(CallTable.formatCell(d, CellKind.date, 'ru'), '06.10');
      expect(CallTable.formatCell(d, CellKind.date, 'ru', year: 2026), '06.10');
      expect(CallTable.formatCell(d, CellKind.date, 'ru', year: 2027), '06.10.26');
      expect(CallTable.formatCell(d, CellKind.time, 'ru'), '14:05');
      expect(CallTable.formatCell('cash', CellKind.status, 'ru'), 'наличные');
      expect(CallTable.formatCell('cash', CellKind.status, 'ky'), 'накталай');
      expect(CallTable.formatCell('cash', CellKind.status, 'uz'), 'naqd');
      expect(CallTable.formatCell('cash', CellKind.status, 'en'), 'cash');
      expect(CallTable.formatCell('cash', CellKind.status, 'tr'), 'nakit');
      expect(CallTable.formatCell('новая_штука', CellKind.status, 'ru'), 'новая_штука');
      expect(CallTable.formatCell(null, CellKind.money, 'ru'), '—');
    });
    test('минус — признак в модели (панель красит красным), не только знак', () {
      final t = CallTable.build(
        id: 't9',
        title: 'x',
        columns: [CallColumn('a', 'A', CellKind.text), CallColumn('b', 'B', CellKind.money), CallColumn('c', 'C', CellKind.percent)],
        rows: [
          ['a', -100.0, -3.0],
          ['b', 100.0, 0.0],
          ['c', const Cell(-5, CellKind.money), null],
        ],
      );
      expect(t.isNegative(0, 1), isTrue);
      expect(t.isNegative(0, 2), isTrue);
      expect(t.isNegative(1, 1), isFalse);
      expect(t.isNegative(1, 2), isFalse);
      expect(t.isNegative(0, 0), isFalse);
      expect(t.isNegative(2, 1), isTrue);
      expect(t.isNegative(2, 2), isFalse);
    });
  });

  group('сортировка', () {
    test('число — от большего; нажатие на заголовок (toggle) переворачивает, голосом — нет; явное направление главнее', () {
      var t = _debts(n: 5);
      t = t.sortedBy('debt')!;
      expect(t.sortKey, 'debt');
      expect(t.desc, isTrue);
      expect([for (final i in t.visibleIndices) t.rows[i][1]], [12345.5, 5200.0, 1500.0, 500.0, 80.5]);
      t = t.sortedBy('debt', toggle: true)!;
      expect(t.desc, isFalse);
      expect(t.rows[t.visibleIndices.first][1], 80.5);
      t = t.sortedBy('debt', desc: true)!;
      expect(t.rows[t.visibleIndices.first][1], 12345.5);
    });
    test('колонку находят по названию, номеру и смыслу («сумма», «долг», 3)', () {
      final t = _debts();
      expect(t.resolveColumn('Долг'), 1);
      expect(t.resolveColumn('долг'), 1);
      expect(t.resolveColumn('сумма'), 1);
      expect(t.resolveColumn('sum'), 1);
      expect(t.resolveColumn('3'), 2);
      expect(t.resolveColumn('колич'), 2);
      expect(t.resolveColumn('чего-то нет'), -1);
      expect(t.sortedBy('нет такой'), isNull);
    });
    test('текст по алфавиту, пустые всегда в конце, равные — в прежнем порядке', () {
      final t = CallTable.build(
        id: 't2',
        title: 'x',
        columns: [CallColumn('name', 'Товар', CellKind.text), CallColumn('n', 'Н', CellKind.qty)],
        rows: [
          ['яблоко', 1],
          [null, 5],
          ['Банан', 1],
          ['арбуз', 2],
        ],
      );
      final asc = t.sortedBy('name')!;
      expect([for (final i in asc.visibleIndices) asc.rows[i][0]], ['арбуз', 'Банан', 'яблоко', null]);
      final desc = t.sortedBy('name', desc: true)!;
      expect(desc.rows[desc.visibleIndices.last][0], isNull);
      final byN = t.sortedBy('n', desc: true)!;
      // 1 и 1: «яблоко» стоит раньше «Банан» и остаётся раньше
      expect([for (final i in byN.visibleIndices) byN.rows[i][0]], [null, 'арбуз', 'яблоко', 'Банан']);
    });
    test('значения в Cell и даты сортируются по значению', () {
      final t = CallTable.build(
        id: 't3',
        title: 'x',
        columns: [CallColumn('q', 'К', CellKind.qty), CallColumn('d', 'Д', CellKind.date)],
        rows: [
          [const Cell(10, CellKind.qty, unit: 'кг'), DateTime(2026, 10, 1)],
          [const Cell(2, CellKind.qty, unit: 'шт'), DateTime(2026, 10, 20)],
        ],
      );
      expect(t.sortedBy('q')!.visibleIndices, [0, 1]);
      expect(t.sortedBy('d')!.visibleIndices, [1, 0]);
    });
    test('карточку не сортируют', () {
      final c = CallTable.build(id: 'c', title: 'Карточка', card: true, columns: [CallColumn('f', 'Поле', CellKind.text), CallColumn('v', 'Значение', CellKind.text)], rows: [
        ['Цена', const Cell(60, CellKind.money)],
      ]);
      expect(c.sortedBy('Поле'), isNull);
      expect(c.filteredBy('Поле', 'x'), isNull);
      expect(c.hasMore, isFalse);
      expect(c.cellText(0, 1), '60$nbсом');
    });
  });

  group('фильтр', () {
    test('«>1000», «больше 1000», «<=500», «=80,5»', () {
      final t = _debts(n: 5);
      expect(t.filteredBy('debt', '>1000')!.viewCount, 3);
      expect(t.filteredBy('debt', 'больше 1000')!.viewCount, 3);
      expect(t.filteredBy('долг', 'more than 1000')!.viewCount, 3);
      expect(t.filteredBy('debt', '<=500')!.viewCount, 2);
      expect(t.filteredBy('debt', '=80,5')!.viewCount, 1);
      expect(t.filteredBy('debt', '1 000', op: 'gt')!.viewCount, 3);
      // колонка не названа — первая денежная
      expect(t.filteredBy(null, '>5000')!.viewCount, 2);
    });
    test('слово ищется в текстовых колонках, в том числе по имени за кодом', () {
      final t = _debts(n: 3);
      final f = t.filteredBy(null, 'клиент 2')!;
      expect(f.viewCount, 1);
      expect(f.rows[f.visibleIndices.first][0], '[Д2]');
      expect(t.filteredBy('client', 'нет такого')!.viewCount, 0);
    });
    test('пустое значение снимает фильтр, итоги считаются по оставшимся', () {
      final t = _debts(n: 5);
      final f = t.filteredBy('debt', '>1000')!;
      expect(f.viewTotals['debt'], 12345.5 + 5200 + 1500);
      expect(t.viewTotals['debt'], 500 + 5200 + 1500 + 80.5 + 12345.5);
      final back = f.filteredBy('debt', '')!;
      expect(back.filter, isNull);
      expect(back.viewCount, 5);
    });
    test('непонятное условие — null (инструмент переспросит)', () {
      final t = _debts();
      expect(t.filteredBy('debt', 'много'), isNull); // слово в числовой колонке — не условие
      expect(t.filteredBy(null, 'много'), isNotNull); // колонка не названа — поиск слова в тексте
      expect(CallFilter.parse(t, 'debt', '>'), isNull);
    });
  });

  group('«покажи ещё» и обрезка', () {
    CallTable big(int n) => CallTable.build(
      id: 'tb',
      title: 'Большая',
      columns: [CallColumn('name', 'Товар', CellKind.text), CallColumn('sum', 'Сумма', CellKind.money, sum: true)],
      rows: [for (var i = 0; i < n; i++) ['Товар $i', 10.0]],
    );
    test('сначала 20, потом по 20', () {
      var t = big(45);
      expect(t.visibleIndices.length, 20);
      expect(t.hasMore, isTrue);
      expect(t.remaining, 25);
      t = t.more();
      expect(t.visibleIndices.length, 40);
      t = t.more();
      expect(t.visibleIndices.length, 45);
      expect(t.hasMore, isFalse);
    });
    test('в таблице хранится до 500 строк, итоги — по всем исходным, truncated помнит', () {
      final t = big(800);
      expect(t.rows.length, 500);
      expect(t.totalRows, 800);
      expect(t.truncated, isTrue);
      expect(t.totals['sum'], 8000.0);
    });
    test('сортировка и фильтр начинают показ заново с 20', () {
      final t = big(100).more().more();
      expect(t.visibleIndices.length, 60);
      expect(t.sortedBy('sum')!.visibleIndices.length, 20);
      expect(t.filteredBy('name', 'Товар')!.visibleIndices.length, 20);
    });
    test('подпись вида: порядок, фильтр, сколько показано', () {
      final t = big(100).sortedBy('sum')!;
      expect(t.viewNote(), contains('Сумма'));
      expect(t.viewNote(), contains('показано 20 из 100'));
    });
  });

  group('итоги и экспорт', () {
    test('«Итого» по денежным колонкам, готовым текстом', () {
      final t = _debts(n: 3);
      expect(t.totalText(1), '7${nb}200$nbсом');
      expect(t.totalText(0), isNull);
      final row = t.totalsRow()!;
      expect(row.first, 'Итого');
      expect(row[1], '7${nb}200$nbсом');
      expect(_debts(lang: 'ky').totalsRow()!.first, 'Жыйынтык');
    });
    test('коды: имя видно только на экране, в справке для Google — коды', () {
      final t = _debts(n: 3);
      expect(t.cellText(0, 0), 'Клиент 1 (тел. 0555-001)');
      expect(t.cellText(0, 0, revealNames: false), '[Д1]');
      final brief = t.modelBrief().toString();
      expect(brief, contains('[Д1]'));
      expect(brief, isNot(contains('Клиент')));
      expect(brief, isNot(contains('0555')));
      expect(t.toPlainText(), contains('Клиент 1'));
      expect(t.toPlainText(revealNames: false), isNot(contains('Клиент')));
    });
    test('справка модели короткая: 5 строк предпросмотра, колонки по ключам, итоги', () {
      final rows = [for (var i = 0; i < 100; i++) ['Очень длинное название товара номер $i для проверки обрезки текста', 1.0]];
      final t = CallTable.build(id: 'tb', title: 'Т', columns: [CallColumn('name', 'Товар', CellKind.text), CallColumn('s', 'Сумма', CellKind.money, sum: true)], rows: rows);
      final b = t.modelBrief();
      expect((b['preview'] as List).length, 5);
      expect(b['columns'], ['name', 's']);
      expect(b['rows'], 100);
      expect((b['totals'] as Map)['s'], '100$nbсом');
      for (final r in b['preview'] as List) {
        expect(((r as List).first as String).length, lessThanOrEqualTo(40));
      }
    });
    test('formattedRows: видимые или все строки; plain text с итогом и «ещё»', () {
      final t = CallTable.build(
        id: 'tb',
        title: 'Т',
        columns: [CallColumn('name', 'Товар', CellKind.text), CallColumn('s', 'Сумма', CellKind.money, sum: true)],
        rows: [for (var i = 0; i < 30; i++) ['Т$i', 2.0]],
      );
      expect(t.formattedRows().length, 20);
      expect(t.formattedRows(all: true).length, 30);
      final text = t.toPlainText(maxRows: 10);
      expect(text, contains('Товар — Сумма'));
      expect(text, contains('ещё 20'));
      expect(text, contains('60$nbсом'));
    });
  });

  group('хранилище таблиц звонка', () {
    CallTable mk(String id, {Map<String, String> names = const {}}) =>
        CallTable.build(id: id, title: 'T$id', columns: [CallColumn('a', 'A', CellKind.code)], rows: [['[Д1]']], names: names);

    test('новая таблица становится текущей, панель открывается, ids растут', () {
      final s = TableStore();
      expect(s.nextId(), 't1');
      expect(s.nextId(), 't2');
      expect(s.isClosed, isTrue);
      s.add(mk('t1'));
      s.add(mk('t2'));
      expect(s.current!.id, 't2');
      expect(s.position, 2);
      expect(s.count, 2);
      expect(s.isClosed, isFalse);
      s.step(-1);
      expect(s.current!.id, 't1');
      s.step(-5);
      expect(s.current!.id, 't1');
      s.step(5);
      expect(s.current!.id, 't2');
    });
    test('хранится до 10, закреплённые не вытесняются', () {
      final s = TableStore();
      s.add(mk('p'));
      s.pin('p');
      for (var i = 0; i < 12; i++) {
        s.add(mk('n$i'));
      }
      expect(s.count, 10);
      expect(s.byId('p'), isNotNull);
      expect(s.byId('n0'), isNull);
      expect(s.byId('n11'), isNotNull);
      s.unpin('p');
      expect(s.isPinned('p'), isFalse);
    });
    test('replace после сортировки; show возвращает старую; close скрывает панель, таблица остаётся', () {
      final s = TableStore();
      s.add(mk('t1'));
      s.add(mk('t2'));
      s.replace(mk('t1').sortedBy('a')!);
      expect(s.byId('t1')!.sortKey, 'a');
      expect(s.current!.id, 't2');
      expect(s.show('t1'), isTrue);
      expect(s.current!.id, 't1');
      expect(s.show('нет'), isFalse);
      s.close();
      expect(s.isClosed, isTrue);
      expect(s.byId('t1'), isNotNull);
    });
    test('размер текста 1.0 → 1.3 → 1.6 и обратно, в пределах', () {
      final s = TableStore();
      expect(s.resize(true), 1.3);
      expect(s.resize(true), 1.6);
      expect(s.resize(true), 1.6);
      expect(s.resize(false), 1.3);
      expect(s.resize(false), 1.0);
      expect(s.resize(false), 1.0);
    });
    test('коды раскрываются по именам своей таблицы, новые раньше старых', () {
      final s = TableStore();
      s.add(mk('t1', names: {'[Д1]': 'Асан'}));
      s.add(mk('t2', names: {'[Д1]': 'Бакыт'}));
      expect(s.reveal('должник [Д1]'), 'должник Бакыт');
      s.show('t1');
      expect(s.reveal('должник [Д1]'), 'должник Асан');
      expect(s.reveal('[Д9]'), '[Д9]');
    });
    test('слушатели получают уведомления; reset очищает', () {
      final s = TableStore();
      var n = 0;
      s.addListener(() => n++);
      s.add(mk('t1'));
      s.pin('t1');
      s.close();
      expect(n, 3);
      s.reset();
      expect(s.count, 0);
      expect(s.current, isNull);
      expect(s.nextId(), 't1');
    });
  });
}
