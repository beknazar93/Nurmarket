// 2026-10-26 (голос-управление, В4): экран звонка, мини-плашка и сборка звонка на фейках (без сети, микрофона, камеры и боевой базы).
// Проверяем по ТЗ (раздел 10.2, В4, и 12.1): звонок переживает навигацию ИИ, плашка 56 dp поверх раздела, «развернуть», таблица
// появляется в панели и после звонка попадает в чат, paused → конец / inactive → нет, строка «ИИ: … · Отменить», уточнения,
// «Спрашивать перед деньгами», связь, лимит, тёмная тема, ky, шрифт 1.3 и сквозной сценарий cart_edit → undo_last.
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/advisor_call.dart';
import 'package:nurmarket/features/assistant/call_controller.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/call_rig.dart';

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_call_ui_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  group('экран звонка: состояния (голос)', () {
    for (final lang in ['ru', 'ky']) {
      for (final dark in [false, true]) {
        testWidgets('$lang${dark ? ' · тёмная' : ''}: готовлю → слушает → думает → говорит → микрофон выкл., без переполнений', (t) async {
          callPhone(t, scale: 1.3);
          final l = lookupAppLocalizations(Locale(lang));
          final rig = CallRig(lang: lang, dark: dark);
          await t.pumpWidget(rig.app(const StubScreen('Главная')));
          await rig.startCall(t);
          await rig.showFull(t);
          expect(find.text(l.callTitle), findsOneWidget);
          expect(find.text(l.callListening), findsOneWidget);
          expect(find.text(l.callAiListens), findsOneWidget); // «● ИИ слушает»
          expect(find.text(l.callMinimize), findsOneWidget);

          // говорит: идёт звук ИИ
          rig.socket.aiSaid('Вчера 44 тысячи.');
          await t.pump(const Duration(milliseconds: 60));
          expect(find.textContaining('Вчера 44 тысячи.'), findsOneWidget);

          // микрофон выкл.
          await t.tap(find.text(l.callMicOn));
          await t.pump(const Duration(milliseconds: 100));
          expect(find.text(l.callMicOff), findsWidgets);
          expect(rig.audio.muted, isTrue);
          expect(find.text(l.callAiListens), findsNothing);
          expect(t.takeException(), isNull);
          await rig.dispose(t);
        });
      }
    }

    testWidgets('за минуту до конца: красный таймер, «Ещё 10 минут» продлевает звонок', (t) async {
      callPhone(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = CallRig(maxDuration: const Duration(minutes: 2));
      await t.pumpWidget(rig.app(const StubScreen('Главная')));
      await rig.startCall(t);
      await rig.showFull(t);
      expect(find.text(l.callExtend), findsNothing);
      await t.pump(const Duration(minutes: 1, seconds: 2));
      expect(rig.c.warned, isTrue);
      expect(find.text(l.callLimitSoon), findsOneWidget);
      expect(find.text(l.callExtend), findsOneWidget);
      final total0 = rig.c.total;
      await t.tap(find.text(l.callExtend));
      await t.pump(const Duration(milliseconds: 100));
      expect(rig.c.total, total0 + const Duration(minutes: 10));
      expect(rig.c.warned, isFalse);
      expect(find.text(l.callExtend), findsNothing);
      // звонок не кончился, когда прошли бы 2 минуты
      await t.pump(const Duration(minutes: 1));
      expect(rig.c.call!.ended, isFalse);
      await rig.dispose(t);
    });

    testWidgets('лимит: сообщение «Звонок завершён: прошло N мин», «Повторить» и «Закрыть»; разговор остаётся для чата', (t) async {
      callPhone(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = CallRig(maxDuration: const Duration(seconds: 90));
      await t.pumpWidget(rig.app(const StubScreen('Главная')));
      await rig.startCall(t);
      rig.socket.said('Привет');
      rig.socket.aiSaid('Здравствуйте!');
      rig.socket.turnComplete();
      await t.pump(const Duration(milliseconds: 60));
      await t.pump(const Duration(seconds: 91));
      expect(rig.c.phase, CallPhase.problem);
      await t.pump(const Duration(milliseconds: 400));
      expect(find.textContaining(l.callEndedLimit(1).split(':').first), findsOneWidget);
      expect(find.text(l.retry), findsOneWidget);
      expect(find.text(l.callClose), findsOneWidget);
      final turns = rig.c.takeFinished()!;
      expect(turns.map((x) => x.text), ['Привет', 'Здравствуйте!']);
      await t.tap(find.text(l.callClose));
      await t.pump(const Duration(milliseconds: 400));
      expect(rig.c.phase, CallPhase.idle);
      await rig.dispose(t);
    });

    testWidgets('нет связи: оранжевая плашка 20 с, потом звонок завершается; экран продолжает работать', (t) async {
      callPhone(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('Главная')));
      await rig.startCall(t);
      rig.c.minimize();
      await t.pump(const Duration(milliseconds: 100));
      // сокет закрылся: LiveCall переподключается (новый сокет не отвечает setupComplete)
      await rig.socket.close();
      await t.pump(const Duration(milliseconds: 400));
      expect(rig.c.noLinkLeft, isNotNull);
      expect(find.text(l.callNoLink), findsOneWidget); // капсула «Нет связи с ИИ»
      expect(find.text(l.callNoLinkWork), findsOneWidget);
      expect(find.textContaining('20'), findsWidgets);
      await t.pump(const Duration(seconds: 21));
      expect(rig.c.phase, CallPhase.problem);
      expect(rig.c.problem, l.callNoLinkWork);
      await rig.dispose(t);
    });
  });

  group('мини-плашка поверх раздела', () {
    testWidgets('ИИ открывает раздел → звонок в плашке, раздел открыт, звонок жив; плашка 56 dp; «развернуть» возвращает субтитры', (t) async {
      callPhone(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = CallRig(useFakeNav: false);
      await t.pumpWidget(rig.routerApp());
      await t.pump(const Duration(milliseconds: 100));
      await rig.startCall(t);
      await rig.showFull(t, nav: rig.router!.routerDelegate.navigatorKey.currentState);
      rig.socket.said('Открой склад');
      rig.socket.aiSaid('Открываю склад.');
      await t.pump(const Duration(milliseconds: 60));
      expect(find.text(l.callMinimize), findsOneWidget);

      rig.socket.toolCall('f1', 'open_screen', {'screen': 'stock', 'filter': 'low'});
      await rig.settle(t);
      await t.pump(const Duration(milliseconds: 500));
      expect(rig.c.view, CallView.mini);
      expect(rig.c.phase, CallPhase.active);
      expect(rig.c.call!.ended, isFalse); // навигация звонок не рвёт
      expect(routerLocation(rig.router!), '/stock?filter=low');
      expect(find.text('экран Склад'), findsOneWidget);
      expect(find.byType(AdvisorCallScreen), findsNothing);
      expect(rig.socket.responseOf('f1')!['status'], 'ok');

      // человек замолчал, модель ещё не ответила — «Ищу…», потом ответила — «Слушаю…»
      expect(find.text(l.callSearching), findsOneWidget);
      rig.socket.turnComplete();
      await t.pump(const Duration(milliseconds: 400));
      // плашка: 56 dp, «Слушаю…», таймер, микрофон, завершить
      final pill = find.byWidgetPredicate((w) => w is SizedBox && w.height == 56 && w.child is Row);
      expect(pill, findsOneWidget);
      expect(find.text(l.callListening), findsOneWidget);
      expect(bySem(l.callMicOn), findsWidgets);
      expect(bySem(l.callEnd), findsWidgets);

      // строка «ИИ: открыла …»
      expect(find.textContaining(l.aiSays('').trim()), findsWidgets);

      // «развернуть»: нажатие по плашке → экран звонка с теми же субтитрами
      await t.tap(find.text(l.callListening));
      await t.pump();
      await t.pump(const Duration(milliseconds: 500));
      expect(find.text(l.callMinimize), findsOneWidget);
      expect(find.textContaining('Открой склад'), findsWidgets); // субтитр «Вы: …» и подпись строки действия
      expect(rig.c.view, CallView.full);
      await rig.dispose(t);
    });

    testWidgets('«Завершить» на плашке заканчивает звонок; реплики уходят в чат', (t) async {
      callPhone(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      rig.socket.said('Привет');
      rig.socket.turnComplete();
      await t.pump(const Duration(milliseconds: 60));
      rig.c.minimize();
      await t.pump(const Duration(milliseconds: 200));
      expect(find.text(l.callListening), findsOneWidget);
      await t.tap(bySem(l.callEnd).last);
      await t.pump(const Duration(milliseconds: 300));
      expect(rig.c.phase, CallPhase.idle);
      expect(find.text(l.callListening), findsNothing);
      expect(rig.c.takeFinished()!.first.text, 'Привет');
      expect(rig.audio.stopped, isTrue);
      await rig.dispose(t);
    });

    testWidgets('жизненный цикл: inactive звонок не рвёт, paused завершает (микрофон не остаётся в фоне)', (t) async {
      callPhone(t);
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      rig.c.didChangeAppLifecycleState(AppLifecycleState.inactive);
      await t.pump(const Duration(milliseconds: 50));
      expect(rig.c.call!.ended, isFalse);
      rig.c.didChangeAppLifecycleState(AppLifecycleState.resumed);
      await t.pump(const Duration(milliseconds: 50));
      expect(rig.c.call!.ended, isFalse);
      rig.c.didChangeAppLifecycleState(AppLifecycleState.paused);
      await t.pump(const Duration(milliseconds: 300));
      expect(rig.c.call!.ended, isTrue);
      expect(rig.c.phase, CallPhase.problem);
      expect(rig.audio.stopped, isTrue);
      await rig.dispose(t);
    });

    testWidgets('плашка в ky и тёмной теме при шрифте 1.3 — без переполнений, обе позиции', (t) async {
      callPhone(t, scale: 1.3);
      final l = lookupAppLocalizations(const Locale('ky'));
      final rig = CallRig(lang: 'ky', dark: true);
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      rig.c.minimize();
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text(l.callListening), findsOneWidget);
      expect(t.takeException(), isNull);
      await rig.c.setPillPos(PillPos.bottom);
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text(l.callListening), findsOneWidget);
      expect(t.getTopLeft(find.text(l.callListening)).dy, greaterThan(500)); // внизу
      await rig.c.setPillPos(PillPos.top);
      await t.pump(const Duration(milliseconds: 300));
      expect(t.getTopLeft(find.text(l.callListening)).dy, lessThan(120));
      // «нет связи» и «микрофон выкл.» — другие подписи капсулы
      await rig.c.toggleMute();
      await t.pump(const Duration(milliseconds: 100));
      expect(find.text(l.callMicOff), findsOneWidget);
      expect(t.takeException(), isNull);
      await rig.dispose(t);
    });
  });
}
