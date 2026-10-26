// 2026-10-26 (голос-управление, В4): строка итога «ИИ: … · Отменить», уточнения, «Спрашивать перед деньгами» — на фейках
// (сокет Gemini Live, звук, каталог и корзина в памяти; без сети и боевой базы). Макет: Mobile-Call-Action.
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/tool_registry.dart';
import 'package:nurmarket/core/ai/live_tools.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/assistant/assistant_mode.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/call_rig.dart';

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_call_act_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  group('действия ИИ: итог, отмена, уточнения', () {
    Future<CallRig> started(WidgetTester t, {Session? session, String lang = 'ru', bool dark = false, double scale = 1.0}) async {
      callPhone(t, scale: scale);
      final rig = CallRig(session: session, lang: lang, dark: dark);
      await t.pumpWidget(rig.app(const StubScreen('Продажа')));
      await rig.startCall(t);
      return rig;
    }

    testWidgets('«добавь Сникерс» → строка «ИИ: … · Отменить»; кнопка откатывает чек и ИИ узнаёт итог', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t);
      rig.socket.said('добавь в чек Сникерс');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('a1', 'cart_edit', {'op': 'add', 'query': 'Сникерс'});
      await rig.settle(t);
      expect(rig.actions.cart.count, 1);
      expect(rig.socket.responseOf('a1')!['status'], 'ok');
      expect(rig.socket.responseOf('a1')!['undo'], true);
      expect(find.text(l.aiUndo), findsOneWidget);
      expect(find.textContaining('Сникерс'), findsWidgets);
      await t.tap(find.text(l.aiUndo));
      await rig.settle(t);
      expect(rig.actions.cart.count, 0);
      expect(rig.socket.texts.where((x) => x.contains('Пользователь нажал «Отменить»')), isNotEmpty);
      expect(find.text(l.aiUndo), findsNothing); // строка стала «отменено», кнопки нет
      await t.pump(const Duration(seconds: 9));
      expect(rig.c.actionLine, isNull);
      await rig.dispose(t);
    });

    testWidgets('строка итога живёт 8 секунд и гаснет сама', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t);
      rig.socket.said('добавь Сникерс');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('a1', 'cart_edit', {'op': 'add', 'query': 'Сникерс'});
      await rig.settle(t);
      expect(find.text(l.aiUndo), findsOneWidget);
      await t.pump(const Duration(seconds: 7));
      expect(find.text(l.aiUndo), findsOneWidget);
      await t.pump(const Duration(seconds: 2));
      expect(find.text(l.aiUndo), findsNothing);
      await rig.dispose(t);
    });

    testWidgets('необратимое: «отменить нельзя» с замком, без кнопки', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t);
      rig.c.registry!.register(
        AssistantTool(
          decl: const ToolDecl('close_shift_test', 'test'),
          group: ToolGroup.money,
          intentWords: const ['закр'],
          run: (r) async => const ToolResult(ToolStatus.ok, 'Смену закрыла.'),
        ),
      );
      rig.socket.said('закрой смену');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('c1', 'close_shift_test');
      await rig.settle(t);
      expect(rig.socket.responseOf('c1')!['status'], 'ok');
      expect(find.textContaining(l.aiCantUndo), findsWidgets);
      expect(find.text(l.aiUndo), findsNothing);
      expect(find.byIcon(Icons.lock_outline_rounded), findsOneWidget);
      await rig.dispose(t);
    });

    testWidgets('нет права: красная строка с отказом (это отказ, не вопрос), кнопки нет', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t, session: testCashier());
      rig.socket.said('убери Сникерс');
      await t.pump(const Duration(milliseconds: 40));
      rig.actions.addToCart(rig.actions.products.first, 1);
      rig.socket.toolCall('d1', 'cart_edit', {'op': 'remove', 'query': 'Сникерс'});
      await rig.settle(t);
      expect(rig.socket.responseOf('d1')!['status'], 'denied');
      expect(find.byIcon(Icons.error_outline_rounded), findsOneWidget);
      expect(find.text(l.aiUndo), findsNothing);
      expect(rig.actions.cart.count, 1);
      await rig.dispose(t);
    });

    testWidgets('повтор той же команды за 10 с: серая строка «Уже сделано», действие не задваивается', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t);
      rig.socket.said('добавь Сникерс');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('a1', 'cart_edit', {'op': 'add', 'query': 'Сникерс'});
      await rig.settle(t);
      rig.socket.toolCall('a2', 'cart_edit', {'op': 'add', 'query': 'Сникерс'});
      await rig.settle(t);
      expect(rig.actions.calls.where((x) => x.startsWith('add:')).length, 1);
      expect(find.textContaining('Уже сделано'), findsWidgets);
      expect(find.text(l.aiUndo), findsNothing);
      await rig.dispose(t);
    });

    testWidgets('две Колы: панель вариантов с номерами; нажатие — ИИ получает выбор и повторяет с pick: 2', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t);
      rig.socket.said('добавь Колу');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('k1', 'cart_edit', {'op': 'add', 'query': 'Кола'});
      await rig.settle(t);
      expect(rig.socket.responseOf('k1')!['status'], 'ambiguous');
      expect(find.text(l.aiPickTitle), findsOneWidget);
      expect(find.text(l.aiAmbiguousHint), findsOneWidget);
      expect(find.text('Кола 0,5 л'), findsWidgets);
      expect(find.text('Кола 1,5 л'), findsWidgets);
      expect(find.text(l.aiNotThat), findsOneWidget);
      await t.tap(find.text('Кола 1,5 л').last);
      await rig.settle(t);
      expect(rig.socket.texts.where((x) => x.contains('вариант 2') && x.contains('pick: 2')), isNotEmpty);
      expect(find.text(l.aiPickTitle), findsNothing);
      rig.socket.toolCall('k2', 'cart_edit', {'op': 'add', 'query': 'Кола', 'pick': 2});
      await rig.settle(t);
      expect(rig.socket.responseOf('k2')!['status'], 'ok');
      expect(rig.actions.cart.lines.single.name, 'Кола 1,5 л');
      await rig.dispose(t);
    });

    testWidgets('«Не то» закрывает панель вариантов', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t);
      rig.socket.said('добавь Колу');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('k1', 'cart_edit', {'op': 'add', 'query': 'Кола'});
      await rig.settle(t);
      await t.tap(find.text(l.aiNotThat));
      await t.pump(const Duration(milliseconds: 200));
      expect(find.text(l.aiPickTitle), findsNothing);
      expect(rig.actions.cart.isEmpty, isTrue);
      await rig.dispose(t);
    });

    testWidgets('число не расслышано: «Число 5 — правильно?» → «Да, 5» открывает ворота и ИИ повторяет', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t);
      rig.socket.said('добавь Сникерс');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('n1', 'cart_edit', {'op': 'add', 'query': 'Сникерс', 'qty': 5});
      await rig.settle(t);
      expect(rig.socket.responseOf('n1')!['status'], 'clarify');
      expect(find.text(l.aiYesNumber('5')), findsOneWidget);
      expect(find.text(l.aiNoRepeat), findsOneWidget);
      await t.tap(find.text(l.aiYesNumber('5')));
      await rig.settle(t);
      expect(rig.socket.texts.where((x) => x.contains('Да, 5')), isNotEmpty);
      rig.socket.toolCall('n2', 'cart_edit', {'op': 'add', 'query': 'Сникерс', 'qty': 5});
      await rig.settle(t);
      expect(rig.socket.responseOf('n2')!['status'], 'ok');
      expect(rig.actions.cart.lines.single.qty, 5);
      await rig.dispose(t);
    });

    testWidgets('«Нет, скажу снова» не меняет данные', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await started(t);
      rig.socket.said('добавь Сникерс');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('n1', 'cart_edit', {'op': 'add', 'query': 'Сникерс', 'qty': 5});
      await rig.settle(t);
      await t.tap(find.text(l.aiNoRepeat));
      await rig.settle(t);
      expect(rig.actions.cart.isEmpty, isTrue);
      expect(find.text(l.aiNoRepeat), findsNothing);
      expect(rig.socket.texts.where((x) => x.contains('Нет')), isNotEmpty);
      await rig.dispose(t);
    });

    testWidgets('сквозной сценарий: добавил → «отмени последнее» голосом → чек пуст, ответы инструментов ушли модели', (t) async {
      final rig = await started(t);
      rig.socket.said('добавь Сникерс');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('e1', 'cart_edit', {'op': 'add', 'query': 'Сникерс'});
      await rig.settle(t);
      expect(rig.actions.cart.count, 1);
      rig.socket.turnComplete();
      rig.socket.said('отмени последнее');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('e2', 'undo_last');
      await rig.settle(t);
      expect(rig.actions.cart.isEmpty, isTrue);
      expect(rig.socket.responseOf('e2')!['status'], 'ok');
      expect(rig.socket.responses.map((r) => r['id']), ['e1', 'e2']);
      await rig.dispose(t);
    });
  });

  group('режим «Спрашивать перед деньгами»', () {
    Future<CallRig> askMoney(WidgetTester t, {String lang = 'ru', bool dark = false}) async {
      callPhone(t);
      final rig = CallRig(lang: lang, dark: dark);
      await t.pumpWidget(rig.app(const StubScreen('Касса')));
      await t.runAsync(() => AssistantModePrefs.save(rig.db, AssistantMode.askMoney));
      await rig.startCall(t);
      return rig;
    }

    for (final lang in ['ru', 'ky']) {
      for (final dark in [false, true]) {
        testWidgets('$lang${dark ? ' · тёмная' : ''}: карточка «Сделать: …»; «Сделать» проводит', (t) async {
          final l = lookupAppLocalizations(Locale(lang));
          final rig = await askMoney(t, lang: lang, dark: dark);
          expect(rig.c.mode, AssistantMode.askMoney);
          rig.socket.said('внеси в кассу 1000');
          await t.pump(const Duration(milliseconds: 40));
          rig.socket.toolCall('m1', 'cash_in_out', {'kind': 'in', 'amount': 1000});
          await rig.settle(t);
          expect(find.text(l.aiModeAskMoney), findsOneWidget);
          expect(find.text(l.aiConfirmDo(l.aiWhatCash)), findsOneWidget);
          expect(find.text(l.aiDo), findsOneWidget);
          expect(find.text(l.aiDont), findsOneWidget);
          expect(find.text(l.aiOrSayYes), findsOneWidget);
          expect(rig.actions.cashMoves, isEmpty);
          await t.tap(find.text(l.aiDo));
          await rig.settle(t, 10);
          expect(rig.actions.cashMoves.single.amount, 1000);
          expect(find.text(l.aiDo), findsNothing);
          expect(t.takeException(), isNull);
          await rig.dispose(t);
        });
      }
    }

    testWidgets('«Не надо» — ничего не проведено; голос «нет» тоже отвечает', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await askMoney(t);
      rig.socket.said('внеси в кассу 1000');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('m1', 'cash_in_out', {'kind': 'in', 'amount': 1000});
      await rig.settle(t);
      await t.tap(find.text(l.aiDont));
      await rig.settle(t);
      expect(rig.actions.cashMoves, isEmpty);
      expect(rig.socket.responseOf('m1')!['status'], 'clarify');
      rig.socket.turnComplete();
      rig.socket.said('внеси в кассу 500');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('m2', 'cash_in_out', {'kind': 'in', 'amount': 500});
      await rig.settle(t);
      expect(find.text(l.aiDo), findsOneWidget);
      rig.socket.turnComplete();
      rig.socket.said('нет');
      await rig.settle(t, 10);
      expect(find.text(l.aiDo), findsNothing);
      expect(rig.actions.cashMoves, isEmpty);
      await rig.dispose(t);
    });

    testWidgets('голос «да» подтверждает карточку', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await askMoney(t);
      rig.socket.said('внеси в кассу 700');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('m1', 'cash_in_out', {'kind': 'in', 'amount': 700});
      await rig.settle(t);
      expect(find.text(l.aiDo), findsOneWidget);
      rig.socket.turnComplete();
      rig.socket.said('да');
      await rig.settle(t, 10);
      expect(rig.actions.cashMoves.single.amount, 700);
      await rig.dispose(t);
    });

    testWidgets('в режиме по умолчанию («Выполнять по просьбе») карточки нет — просьба и есть одобрение', (t) async {
      callPhone(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('Касса')));
      await rig.startCall(t);
      expect(rig.c.mode, AssistantMode.act);
      rig.socket.said('внеси в кассу 1000');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.toolCall('m1', 'cash_in_out', {'kind': 'in', 'amount': 1000});
      await rig.settle(t, 10);
      expect(find.text(l.aiDo), findsNothing);
      expect(rig.actions.cashMoves.single.amount, 1000);
      await rig.dispose(t);
    });
  });
}
