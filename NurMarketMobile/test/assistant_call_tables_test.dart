// 2026-10-26 (голос-управление, В4): таблицы во время звонка на фейках — нижняя панель (96 / половина / весь экран), сортировка,
// «Показать ещё», вкладки закреплённых, размер текста 1.0 / 1.3 / 1.6, пустая и широкая таблица, карточка объекта, нажатие на
// строку, «В чат» и перенос таблиц в чат после звонка. Макет: Mobile-Call-Table.
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/call_table.dart';
import 'package:nurmarket/core/theme/tokens.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/assistant/call_controller.dart';
import 'package:nurmarket/features/assistant/call_table_panel.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/call_rig.dart';

CallTable sales({String id = 't1', int rows = 26, String lang = 'ru', String title = 'Продажи по дням'}) => CallTable.build(
  id: id,
  title: title,
  subtitle: '01.10–26.10',
  columns: [
    CallColumn('day', 'День', CellKind.text),
    CallColumn('checks', 'Чеков', CellKind.qty, sum: true),
    CallColumn('sum', 'Сумма', CellKind.money, sum: true),
  ],
  rows: [
    for (var i = 0; i < rows; i++) ['${(26 - i).toString().padLeft(2, '0')}.10', 38 + i % 7, 31000.0 + i * 700],
  ],
  source: 'tab=sales',
  lang: lang,
);

CallTable wide(String lang) => CallTable.build(
  id: 'w1',
  title: 'Зарплата за октябрь',
  columns: [
    CallColumn('name', 'Сотрудник', CellKind.text),
    CallColumn('scheme', 'Схема', CellKind.text),
    CallColumn('base', 'Оклад', CellKind.money, sum: true),
    CallColumn('pct', '%', CellKind.percent),
    CallColumn('items', 'За товары', CellKind.money, sum: true),
    CallColumn('acc', 'Начислено', CellKind.money, sum: true),
    CallColumn('paid', 'Выплачено', CellKind.money, sum: true),
    CallColumn('left', 'Осталось', CellKind.money, sum: true),
  ],
  rows: [
    ['Айбек Т.', 'оклад + %', 20000.0, 2.0, 3800.0, 24300.0, 5800.0, 18500.0],
    ['Айгерим С.', '% от продаж', 0.0, 5.0, 0.0, 21150.0, 21150.0, 0.0],
    ['Нурлан Б.', 'оклад', 18000.0, 0.0, 0.0, 18000.0, 9000.0, 9000.0],
  ],
  source: 'tab=salary',
  lang: lang,
  note: 'проверено частично',
);

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_call_tbl_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  Widget host(CallRig rig) => rig.app(
    Scaffold(
      body: ListenableBuilder(
        listenable: rig.c.tables,
        builder: (_, _) => Stack(children: [Positioned.fill(child: CallTablePanel(c: rig.c))]),
      ),
    ),
  );

  Future<CallRig> panel(WidgetTester t, {String lang = 'ru', bool dark = false, double scale = 1.0, CallTable? table, bool call = false}) async {
    callPhone(t, scale: scale);
    final rig = CallRig(lang: lang, dark: dark);
    // со звонком панель рисует слой звонка (AssistantOverlay); без звонка — панель напрямую
    await t.pumpWidget(call ? rig.app(const StubScreen('Склад')) : host(rig));
    if (call) await rig.startCall(t);
    rig.c.tables.add(table ?? sales(lang: lang));
    await t.pump();
    await t.pump(const Duration(milliseconds: 400));
    return rig;
  }

  double height(WidgetTester t) => t.getSize(find.descendant(of: find.byType(CallTablePanel), matching: find.byType(AnimatedContainer)).first).height;

  group('нижняя панель', () {
    testWidgets('половина по умолчанию: заголовок, 20 из 26 строк, «Итого», «Показать ещё 6», источник', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t);
      expect(find.text('Продажи по дням'), findsOneWidget);
      expect(find.textContaining('01.10–26.10'), findsOneWidget);
      expect(find.text('26.10'), findsOneWidget);
      expect(find.text('07.10'), findsOneWidget); // 20-я строка
      expect(find.text('06.10'), findsNothing); // 21-я не показана
      expect(find.text('Итого'), findsOneWidget);
      expect(find.text('${l.tblMore(6)} · ${l.tblMoreLeft(6)}'), findsOneWidget);
      expect(find.textContaining('tab=sales'), findsOneWidget);
      expect(height(t), closeTo(740 * .54, 2)); // 54 % экрана (низкий телефон)
      expect(t.takeException(), isNull);
      await rig.dispose(t);
    });

    testWidgets('нажатие на шапку — сортировка; «Показать ещё» — следующие строки; потом «Все строки показаны»', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t);
      await t.tap(find.text('Сумма'));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.c.tables.current!.sortKey, 'sum');
      expect(rig.c.tables.current!.desc, isTrue);
      await t.tap(find.text('${l.tblMore(6)} · ${l.tblMoreLeft(6)}'));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.c.tables.current!.shown, 40);
      expect(find.text(l.tblAllShown), findsOneWidget);
      expect(find.text('06.10'), findsOneWidget);
      await rig.dispose(t);
    });

    testWidgets('«Крупнее» → 1.3 → 1.6, потом «Мельче»; без переполнения', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t);
      await t.tap(find.text(l.tblBigger));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.c.tables.textScale, 1.3);
      await t.tap(find.text(l.tblBigger));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.c.tables.textScale, 1.6);
      expect(find.text(l.tblBigger), findsNothing);
      expect(find.text(l.tblSmaller), findsOneWidget);
      expect(t.takeException(), isNull);
      await rig.dispose(t);
    });

    testWidgets('нажатие на затемнение — панель сворачивается до 96 dp: название и «Итого»; нажатие на неё — снова половина', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t);
      await t.tapAt(const Offset(180, 40));
      await t.pump(const Duration(milliseconds: 400));
      expect(height(t), 96);
      expect(find.text('Продажи по дням'), findsOneWidget);
      expect(find.textContaining(l.tblRowsN(26)), findsOneWidget);
      expect(find.text(l.tblBigger), findsNothing);
      await t.tap(find.text('Продажи по дням'));
      await t.pump(const Duration(milliseconds: 400));
      expect(height(t), greaterThan(300));
      await rig.dispose(t);
    });

    testWidgets('жест вверх — на весь экран: стрелка «свернуть», таймер звонка и красная «Завершить» вместо кнопок звонка', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t, call: true);
      await t.fling(find.text('Продажи по дням'), const Offset(0, -700), 3000);
      await t.pump(const Duration(milliseconds: 500));
      expect(height(t), closeTo(740, 1));
      expect(bySem(l.tblCollapse), findsOneWidget);
      expect(bySem(l.callEnd), findsOneWidget);
      expect(rig.c.panelInset.value, closeTo(740, 1));
      await t.tap(bySem(l.tblCollapse));
      await t.pump();
      await t.pump(const Duration(milliseconds: 500));
      expect(height(t), lessThan(740));
      await rig.dispose(t);
    });

    testWidgets('закрыть (✕) скрывает панель; «покажи таблицу» (show) возвращает', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t);
      await t.tap(bySem(l.tblClose));
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text('Продажи по дням'), findsNothing);
      expect(rig.c.tables.isClosed, isTrue);
      rig.c.tables.show('t1');
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text('Продажи по дням'), findsOneWidget);
      await rig.dispose(t);
    });
  });

  group('несколько таблиц', () {
    testWidgets('«‹ 2/2 ›» переключает; «Закрепить» — новая открывается вкладкой рядом', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t);
      await t.ensureVisible(find.text(l.tblPin));
      await t.tap(find.text(l.tblPin));
      await t.pump(const Duration(milliseconds: 200));
      expect(find.text(l.tblPinned), findsOneWidget);
      rig.c.tables.add(sales(id: 't2', rows: 5, title: 'Должники'));
      await t.pump(const Duration(milliseconds: 300));
      expect(rig.c.tables.position, 2);
      expect(find.text('2/2'), findsOneWidget);
      // вкладки: закреплённая (с кнопкой) и текущая
      expect(find.byIcon(Icons.push_pin_rounded), findsOneWidget);
      expect(find.textContaining('Продажи по дням'), findsOneWidget); // вкладка
      await t.tap(bySem(l.tblPrev));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.c.tables.current!.id, 't1');
      await rig.dispose(t);
    });
  });

  group('виды таблицы', () {
    testWidgets('пустая по фильтру: «Пока пусто», запрос и «Снять фильтр»', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t);
      final filtered = rig.c.tables.current!.filteredBy('sum', '>1000000')!;
      rig.c.tables.replace(filtered);
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text(l.tblEmpty), findsOneWidget);
      expect(find.text(l.tblClearFilter), findsWidgets);
      await t.tap(find.widgetWithText(OutlinedButton, l.tblClearFilter).last);
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text(l.tblEmpty), findsNothing);
      expect(rig.c.tables.current!.filter, isNull);
      await rig.dispose(t);
    });

    testWidgets('карточка объекта: «поле — значение» и «Открыть» (товар открывается, звонок сворачивается)', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      callPhone(t);
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      rig.c.tables.add(
        CallTable.build(
          id: 'c1',
          title: 'Кока-Кола 1 л',
          columns: [CallColumn('f', 'Поле', CellKind.text), CallColumn('v', 'Значение', CellKind.text)],
          rows: [
            ['Цена', const Cell(95.0, CellKind.money)],
            ['Остаток', const Cell(12.0, CellKind.qty, unit: 'шт')],
          ],
          refs: ['p1', 'p1'],
          objectRoute: 'product',
          card: true,
        ),
      );
      await t.pump(const Duration(milliseconds: 400));
      expect(find.text('Цена'), findsOneWidget);
      expect(textN('95 сом'), findsOneWidget);
      expect(textN('12 шт'), findsOneWidget);
      await t.tap(find.text(l.tblOpenObject));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.fakeNav!.products, ['p1']);
      expect(rig.c.view, CallView.mini);
      await rig.dispose(t);
    });

    testWidgets('нажатие на строку открывает объект и сворачивает звонок', (t) async {
      callPhone(t);
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      rig.c.tables.add(
        CallTable.build(
          id: 'p',
          title: 'Остатки',
          columns: [CallColumn('name', 'Товар', CellKind.text), CallColumn('qty', 'Остаток', CellKind.qty)],
          rows: [
            ['Сникерс', 2.0],
            ['Кола', 1.0],
          ],
          refs: ['p1', 'p2'],
          objectRoute: 'product',
        ),
      );
      await t.pump(const Duration(milliseconds: 400));
      await t.tap(find.text('Кола'));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.fakeNav!.products, ['p2']);
      expect(rig.c.view, CallView.mini);
      expect(rig.c.call!.ended, isFalse);
      await rig.dispose(t);
    });

    testWidgets('отрицательные суммы — красным (T.danger), не только минусом', (t) async {
      callPhone(t);
      final rig = CallRig();
      await t.pumpWidget(host(rig));
      rig.c.tables.add(
        CallTable.build(
          id: 'n',
          title: 'Долги',
          columns: [CallColumn('name', 'Клиент', CellKind.text), CallColumn('debt', 'Долг', CellKind.money, sum: true)],
          rows: [
            ['Асан', -500.0],
            ['Айбек', 1200.0],
          ],
        ),
      );
      await t.pump(const Duration(milliseconds: 400));
      final neg = t.widget<Text>(textN('-500 сом'));
      expect(neg.style!.color, const Palette(false).danger);
      final pos = t.widget<Text>(textN('1 200 сом'));
      expect(pos.style!.color, isNot(const Palette(false).danger));
      await rig.dispose(t);
    });

    for (final lang in ['ru', 'ky']) {
      for (final dark in [false, true]) {
        testWidgets('$lang${dark ? ' · тёмная' : ''}: широкая таблица (8 колонок) при шрифте 1.3 — горизонтальная прокрутка, без переполнений', (t) async {
          final rig = await panel(t, lang: lang, dark: dark, scale: 1.3, table: wide(lang));
          expect(find.text('Айбек Т.'), findsOneWidget);
          expect(find.text('Сотрудник'), findsOneWidget);
          final h = find.byWidgetPredicate((w) => w is SingleChildScrollView && w.scrollDirection == Axis.horizontal);
          expect(h, findsWidgets);
          // первая колонка закреплена: после прокрутки вбок имя остаётся на месте
          final before = t.getTopLeft(find.text('Айбек Т.'));
          await t.drag(h.first, const Offset(-300, 0));
          await t.pump(const Duration(milliseconds: 200));
          expect(t.getTopLeft(find.text('Айбек Т.')), before);
          expect(find.text('проверено частично'), findsOneWidget);
          expect(t.takeException(), isNull);
          await rig.dispose(t);
        });
      }
    }
  });

  group('таблицы и чат', () {
    testWidgets('инструмент stock_list: таблица на панели во время звонка, ИИ получает table_id; после звонка таблица в чате', (t) async {
      callPhone(t);
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      rig.socket.said('что заканчивается');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('s1', 'stock_list', {'filter': 'low'});
      await rig.settle(t);
      final resp = rig.socket.responseOf('s1')!;
      expect(resp['status'], 'ok');
      expect(resp['table_id'], isNotNull);
      expect(rig.c.tables.current, isNotNull);
      expect(find.byType(CallTablePanel), findsOneWidget);
      expect(find.text('Сникерс'), findsOneWidget);
      rig.c.endCall();
      await t.pump(const Duration(milliseconds: 300));
      final turns = rig.c.takeFinished()!;
      final table = turns.where((x) => x.table).single;
      expect(table.text, contains('Сникерс'));
      expect(find.byType(CallTablePanel), findsNothing); // панель ушла вместе со звонком
      await rig.dispose(t);
    });

    testWidgets('«В чат» кладёт таблицу в разговор один раз; после звонка не дублируется', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await panel(t, call: true);
      final notices = <String>[];
      final sub = rig.c.notices.listen(notices.add);
      addTearDown(sub.cancel);
      await t.tap(find.text(l.tblToChat));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.c.turns.where((x) => x.table).length, 1);
      expect(notices, [l.tblSentChat]);
      rig.c.endCall();
      await t.pump(const Duration(milliseconds: 300));
      expect(rig.c.takeFinished()!.where((x) => x.table).length, 1);
      await rig.dispose(t);
    });
  });

  group('экран звонка с открытой панелью', () {
    for (final lang in ['ru', 'ky']) {
      testWidgets('$lang: звонок уплотняется (круг 96, кнопки «Микрофон / Громкая / Завершить»), панель снизу, без переполнений при 1.3', (t) async {
        callPhone(t, scale: 1.3);
        final l = lookupAppLocalizations(Locale(lang));
        final rig = CallRig(lang: lang);
        await t.pumpWidget(rig.app(const StubScreen('Склад')));
        await rig.startCall(t);
        await rig.showFull(t);
        rig.socket.said('покажи продажи по дням');
        rig.socket.aiSaid('За октябрь 312 тысяч.');
        await t.pump(const Duration(milliseconds: 60));
        rig.c.tables.add(sales(lang: lang));
        await t.pump();
        await t.pump(const Duration(milliseconds: 500));
        expect(find.text(l.callTitle), findsOneWidget);
        expect(find.text(l.callEnd), findsOneWidget); // красная кнопка в ряду
        expect(find.text('Продажи по дням'), findsOneWidget);
        expect(rig.c.panelInset.value, greaterThan(300));
        expect(t.takeException(), isNull);
        await rig.dispose(t);
      });
    }
  });
}
