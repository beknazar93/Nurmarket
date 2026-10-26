// 2026-10-26 (приёмка 1.18.6, С-9н): таблица звонка на 360 dp при шрифте 1,3 (ky и ru) — главная денежная колонка («Сумма»)
// видна целиком без прокрутки вбок: закреплена рядом с первой колонкой, число не обрезается многоточием. Фейки, без сети.
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/call_table.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/assistant/call_table_panel.dart';

import 'support/call_rig.dart';

CallTable _sales(String lang) => CallTable.build(
  id: 't1',
  title: lang == 'ky' ? 'Күндөр боюнча сатуу' : 'Продажи по дням',
  subtitle: '01.10–26.10',
  columns: [
    CallColumn('day', lang == 'ky' ? 'Күн' : 'День', CellKind.text),
    CallColumn('checks', lang == 'ky' ? 'Чек' : 'Чеков', CellKind.qty, sum: true),
    CallColumn('sum', 'Сумма', CellKind.money, sum: true),
  ],
  rows: [
    for (var i = 0; i < 26; i++) ['${(26 - i).toString().padLeft(2, '0')}.10', 38 + i % 7, 31000.0 + i * 700],
  ],
  source: 'tab=sales',
  lang: lang,
);

CallTable _wide(String lang) => CallTable.build(
  id: 'w1',
  title: lang == 'ky' ? 'Октябрь айынын эмгек акысы' : 'Зарплата за октябрь',
  columns: [
    CallColumn('name', lang == 'ky' ? 'Кызматкер' : 'Сотрудник', CellKind.text),
    CallColumn('scheme', lang == 'ky' ? 'Схема' : 'Схема', CellKind.text),
    CallColumn('base', lang == 'ky' ? 'Айлык' : 'Оклад', CellKind.money, sum: true),
    CallColumn('pct', '%', CellKind.percent),
    CallColumn('acc', lang == 'ky' ? 'Эсептелди' : 'Начислено', CellKind.money, sum: true),
    CallColumn('left', lang == 'ky' ? 'Калды' : 'Осталось', CellKind.money, sum: true),
  ],
  rows: [
    ['Айбек Тологонов', 'оклад + %', 20000.0, 2.0, 24300.0, 18500.0],
    ['Айгерим Сыдыкова', '% от продаж', 0.0, 5.0, 21150.0, 0.0],
  ],
  source: 'tab=salary',
  lang: lang,
);

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_tbl1186_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  Future<CallRig> open(WidgetTester t, String lang, CallTable table, {double scale = 1.3}) async {
    callPhone(t, scale: scale); // 360×740 dp
    final rig = CallRig(lang: lang);
    await t.pumpWidget(
      rig.app(
        Scaffold(
          body: ListenableBuilder(
            listenable: rig.c.tables,
            builder: (_, _) => Stack(children: [Positioned.fill(child: CallTablePanel(c: rig.c, initial: PanelExtent.full))]),
          ),
        ),
      ),
    );
    rig.c.tables.add(table);
    await t.pump();
    await t.pump(const Duration(milliseconds: 400));
    return rig;
  }

  for (final lang in ['ky', 'ru']) {
    testWidgets('$lang, 360 dp, шрифт 1,3: «Сумма» (заголовок, строки, «Итого») видна без прокрутки вбок', (t) async {
      final table = _sales(lang);
      final rig = await open(t, lang, table);
      expect(t.takeException(), isNull);
      final head = t.getRect(find.text('Сумма'));
      expect(head.right, lessThanOrEqualTo(360), reason: 'заголовок «Сумма» в пределах экрана');
      expect(head.left, greaterThanOrEqualTo(0));
      final first = find.text(table.cellText(table.visibleIndices.first, 2));
      expect(first, findsOneWidget);
      expect(t.getRect(first).right, lessThanOrEqualTo(360), reason: 'число первой строки целиком в пределах экрана');
      final total = find.text(table.totalText(2)!);
      expect(total, findsOneWidget);
      expect(t.getRect(total).right, lessThanOrEqualTo(360), reason: 'итог по сумме виден');
      await rig.dispose(t);
    });

    testWidgets('$lang, 360 dp, шрифт 1,3: широкая таблица — «Осталось» закреплена рядом с именем, остальное прокручивается', (t) async {
      final table = _wide(lang);
      final rig = await open(t, lang, table);
      expect(t.takeException(), isNull);
      final last = find.text(table.cellText(table.visibleIndices.first, 5));
      expect(last, findsWidgets);
      expect(t.getRect(last.first).right, lessThanOrEqualTo(360), reason: 'последняя денежная колонка видна без прокрутки');
      expect(t.getRect(last.first).left, greaterThanOrEqualTo(0));
      final head = find.text(lang == 'ky' ? 'Калды' : 'Осталось');
      expect(head, findsOneWidget);
      expect(t.getRect(head).right, lessThanOrEqualTo(360), reason: 'и её заголовок');
      await rig.dispose(t);
    });
  }
}
