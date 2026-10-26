// 2026-10-26 (видеозвонок, В2/В3 + голос-управление, В4): видео-часть экрана звонка на фейках (сокет, звук, камера, распознавание —
// без сети, камеры и микрофона): кнопка «Камера», согласие (по пользователю и версии), метки «● Камера» и «ИИ видит», «Другая
// камера» и «Фонарик», «Экономия», быстрые кнопки и карточки, жизненный цикл, ошибки. Макет: Mobile-Call-Video.
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/live_video.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/admin/admin_receive.dart' show ReceiveLine;
import 'package:nurmarket/features/assistant/call_consent.dart';
import 'package:nurmarket/features/assistant/call_controller.dart';
import 'package:nurmarket/features/assistant/call_video_bridge.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/call_rig.dart';

class FakeRecognizer implements VideoRecognizer {
  VideoCard card;
  final asks = <VideoAsk>[];
  FakeRecognizer(this.card);
  @override
  Future<VideoCard> run(VideoAsk ask, VideoStill still, VideoAskContext ctx) async {
    asks.add(ask);
    return card;
  }
}

/// Кадр с запасом: два кадра (старт анимации, потом сама анимация).
Future<void> ui(WidgetTester t) async {
  await t.pump();
  await t.pump(const Duration(milliseconds: 500));
}

Uint8List jpeg() => Uint8List.fromList([0xFF, 0xD8, 1, 2, 3, 4, 5, 6]);

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_call_vid_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  group('videoIntent: фразы вместо кнопок', () {
    test('товар: «что это», «сколько стоит этот», ky/uz/en/tr', () {
      for (final s in ['Что это?', 'это что такое', 'Сколько стоит этот?', 'сколько осталось этого', 'найди это', 'бул эмне', 'бул канча турат', 'мунун баасы', 'bu nima', 'bu qancha turadi', 'buning narxi', 'what is this', 'how much is this', 'is this in stock', 'bu ne', 'bu kaç para']) {
        expect(videoIntent(s), VideoAsk.product, reason: s);
      }
    });
    test('накладная, полка, ценник', () {
      for (final s in ['прими накладную', 'накладная', 'смотри накладную', 'invoice', 'irsaliye']) {
        expect(videoIntent(s), VideoAsk.invoice, reason: s);
      }
      for (final s in ['пересчитай полку', 'посчитай полку', 'текче', 'javon', 'shelf', 'raf', 'пересчитай это']) {
        expect(videoIntent(s), VideoAsk.shelf, reason: s);
      }
      for (final s in ['ценник', 'проверь ценник', 'совпадает ценник', 'баа белгиси', 'price tag', 'fiyat etiketi', 'этикетка']) {
        expect(videoIntent(s), VideoAsk.label, reason: s);
      }
    });
    test('ловушки: вопросы с цифрами без «это», печать этикетки, длинные фразы — не видео', () {
      for (final s in [
        'сколько продали вчера',
        'какой долг у Асана',
        'бул айда канча сатылды',
        'пересчитай выручку за неделю',
        'какие накладные были вчера',
        'что в наличии',
        'напечатай этикетку для колы',
        'покажи склад',
        'how many sold yesterday',
        'bugün ne kadar satıldı',
        'bugun qancha sotildi',
        '',
      ]) {
        expect(videoIntent(s), isNull, reason: s);
      }
    });
  });

  group('согласие на камеру', () {
    testWidgets('хранится по пользователю и версии текста', (t) async {
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('x')));
      await t.runAsync(() async {
        expect(await VideoConsent.has(rig.db, 'u1'), isFalse);
        await VideoConsent.accept(rig.db, 'u1');
        expect(await VideoConsent.has(rig.db, 'u1'), isTrue);
        expect(await VideoConsent.has(rig.db, 'u2'), isFalse); // другой пользователь — снова окно
        expect(VideoConsent.key('u1'), contains('.${VideoConsent.version}.'));
        expect(VideoConsent.key('u1'), isNot(VideoConsent.key('u2')));
        // новая версия текста — прежнее согласие не действует
        expect(await rig.db.getSetting('advisor.video.consent.v2.u1'), isNull);
      });
      await t.runAsync(() => VideoPrefs.setEconomy(rig.db, true));
      expect(await t.runAsync(() => VideoPrefs.economy(rig.db)), isTrue);
      await rig.dispose(t);
    });
  });

  group('экран с камерой', () {
    Future<CallRig> screen(WidgetTester t, {String lang = 'ru', bool dark = false, double scale = 1.0, Session? session, Size size = const Size(720, 1480)}) async {
      callPhone(t, scale: scale, size: size);
      final rig = CallRig(lang: lang, dark: dark, camera: true, session: session);
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      await rig.showFull(t);
      return rig;
    }

    testWidgets('без камеры на телефоне кнопки «Камера» нет и экран как раньше', (t) async {
      callPhone(t);
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = CallRig();
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      await rig.showFull(t);
      expect(find.text(l.vcCamera), findsNothing);
      expect(find.text(l.callMicOn), findsOneWidget);
      expect(find.text(l.callSpeakerOn), findsOneWidget);
      expect(rig.c.video, isNull);
      await rig.dispose(t);
    });

    testWidgets('кнопка «Камера» → окно согласия; «Не сейчас» камеру не включает; «Включить» — включает, метка «● Камера»; второй раз окна нет', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await screen(t);
      expect(find.text(l.vcCamera), findsOneWidget);
      await t.tap(find.text(l.vcCamera));
      await t.pump();
      await ui(t);
      expect(find.text(l.vcConsentTitle), findsOneWidget);
      expect(find.text(l.vcConsentPtSends), findsOneWidget);
      expect(find.text(l.vcConsentAgree), findsOneWidget);
      await t.tap(find.text(l.vcConsentLater));
      await ui(t);
      expect(rig.frames.starts, 0);
      expect(find.text(l.vcConsentTitle), findsNothing);
      // снова — окно
      await t.tap(find.text(l.vcCamera));
      await t.pump();
      await ui(t);
      await t.tap(find.text(l.vcConsentAgree));
      await ui(t);
      await t.pump(const Duration(milliseconds: 300));
      expect(rig.frames.starts, 1);
      expect(find.text(l.vcRecording), findsOneWidget); // «● Камера»
      expect(find.text(l.vcCameraOn), findsOneWidget);
      // выключить и включить снова — окна больше нет
      await t.tap(find.text(l.vcCameraOn));
      await t.pump(const Duration(milliseconds: 300));
      expect(rig.frames.stops, greaterThan(0));
      await t.tap(find.text(l.vcCamera));
      await ui(t);
      expect(find.text(l.vcConsentTitle), findsNothing);
      expect(rig.frames.starts, 2);
      await rig.dispose(t);
    });

    testWidgets('первый кадр ушёл → «ИИ видит»; кадры идут в сокет как realtimeInput.video; «Другая камера» и «Фонарик»', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await screen(t);
      await t.runAsync(() => VideoConsent.accept(rig.db, 'u1'));
      await t.tap(find.text(l.vcCamera));
      await ui(t);
      await ui(t);
      expect(find.text(l.vcAiSees), findsOneWidget);
      expect(rig.socket.sent.where((m) => (m['realtimeInput'] as Map?)?['video'] != null), isNotEmpty);
      // «Другая камера» (две камеры) и «Фонарик» (задняя)
      expect(bySem(l.vcSwitchCamera), findsOneWidget);
      expect(bySem(l.vcTorch), findsOneWidget);
      await t.tap(bySem(l.vcTorch));
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.frames.torch, isTrue);
      await t.tap(bySem(l.vcSwitchCamera));
      await ui(t);
      expect(rig.frames.lens.name, 'front');
      expect(bySem(l.vcTorch), findsNothing); // фронтальная — фонарика нет
      await rig.dispose(t);
    });

    testWidgets('«Экономия трафика» запоминается; подсказка «1 кадр в 3 секунды…»', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await screen(t);
      await t.runAsync(() => VideoConsent.accept(rig.db, 'u1'));
      await t.tap(find.text(l.vcCamera));
      await ui(t);
      expect(find.text(l.vcEconomy), findsOneWidget);
      await t.tap(find.text(l.vcEconomy));
      await t.pump(const Duration(milliseconds: 300));
      expect(rig.c.economy, isTrue);
      expect(rig.c.video!.economy, isTrue);
      expect(find.text(l.vcEconomyHint), findsOneWidget);
      expect(await t.runAsync(() => VideoPrefs.economy(rig.db)), isTrue);
      await rig.dispose(t);
    });

    testWidgets('жизненный цикл: inactive отпускает камеру, звонок жив; resumed возвращает; paused — конец звонка и камера выключена', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await screen(t);
      await t.runAsync(() => VideoConsent.accept(rig.db, 'u1'));
      await t.tap(find.text(l.vcCamera));
      await ui(t);
      expect(rig.frames.starts, 1);
      rig.c.didChangeAppLifecycleState(AppLifecycleState.inactive);
      await t.pump(const Duration(milliseconds: 200));
      expect(rig.frames.stops, greaterThan(0));
      expect(rig.c.call!.ended, isFalse);
      rig.c.didChangeAppLifecycleState(AppLifecycleState.resumed);
      await ui(t);
      expect(rig.frames.starts, 2);
      final stops = rig.frames.stops;
      rig.c.didChangeAppLifecycleState(AppLifecycleState.paused);
      await ui(t);
      expect(rig.c.call!.ended, isTrue);
      expect(rig.frames.stops, greaterThan(stops));
      expect(rig.frames.open, isFalse);
      await rig.dispose(t);
    });

    testWidgets('нет права камеры: плашка и «Открыть настройки», звонок продолжается голосом', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await screen(t);
      await t.runAsync(() => VideoConsent.accept(rig.db, 'u1'));
      rig.frames.failStart = const FrameException(FrameFail.denied);
      await t.tap(find.text(l.vcCamera));
      await ui(t);
      expect(find.textContaining(videoMsg('cameraDenied', 'ru')), findsOneWidget);
      expect(find.text(l.callOpenSettings), findsOneWidget);
      expect(rig.c.call!.ended, isFalse);
      await rig.dispose(t);
    });

    testWidgets('камера занята → «Повторить» включает снова', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await screen(t);
      await t.runAsync(() => VideoConsent.accept(rig.db, 'u1'));
      rig.frames.failStart = const FrameException(FrameFail.busy);
      await t.tap(find.text(l.vcCamera));
      await ui(t);
      expect(find.textContaining(videoMsg('cameraBusy', 'ru')), findsOneWidget);
      rig.frames.failStart = null;
      await t.tap(find.text(l.retry));
      await ui(t);
      expect(rig.frames.starts, 1);
      await rig.dispose(t);
    });

    testWidgets('модель не принимает видео: плашка, кнопка «Камера» недоступна до конца звонка, разговор идёт', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await screen(t);
      await t.runAsync(() => VideoConsent.accept(rig.db, 'u1'));
      await t.tap(find.text(l.vcCamera));
      await t.pump(const Duration(milliseconds: 1400)); // кадр ушёл
      final s = rig.socket;
      s.closeCode = 1007;
      s.closeReason = 'video input is not supported';
      await s.close();
      await t.pump(const Duration(milliseconds: 100));
      rig.sockets.last.push({'setupComplete': {}});
      await ui(t);
      expect(rig.c.call!.videoActive.value, isFalse);
      expect(find.textContaining(videoMsg('videoRejected', 'ru')), findsOneWidget);
      expect(rig.c.call!.ended, isFalse);
      await rig.dispose(t);
    });

    testWidgets('лимит ключа (RESOURCE_EXHAUSTED) после кадров: звонок завершается, текст про «Экономию»', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await screen(t);
      await t.runAsync(() => VideoConsent.accept(rig.db, 'u1'));
      await t.tap(find.text(l.vcCamera));
      await t.pump(const Duration(milliseconds: 1400));
      final s = rig.socket;
      s.closeCode = 1011;
      s.closeReason = 'RESOURCE_EXHAUSTED: quota exceeded';
      await s.close();
      await ui(t);
      expect(rig.c.phase, CallPhase.problem);
      expect(rig.c.problem, videoMsg('quota', 'ru'));
      expect(find.text(videoMsg('quota', 'ru')), findsWidgets);
      await rig.dispose(t);
    });
  });

  group('быстрые кнопки и карточки', () {
    Future<CallRig> withCam(WidgetTester t, VideoCard card, {Session? session, String lang = 'ru', bool dark = false, double scale = 1.0}) async {
      callPhone(t, scale: scale);
      final rig = CallRig(lang: lang, dark: dark, camera: true, session: session);
      rig.frames.still0 = jpeg();
      await t.pumpWidget(rig.app(const StubScreen('Склад')));
      await rig.startCall(t);
      rig.c.recognizer = FakeRecognizer(card);
      await rig.showFull(t);
      await t.runAsync(() => VideoConsent.accept(rig.db, session?.userId ?? 'u1'));
      await t.tap(find.text(lookupAppLocalizations(Locale(lang)).vcCamera));
      await t.pump(const Duration(milliseconds: 700));
      return rig;
    }

    testWidgets('«Что это?» → карточка «Товар» с ценой, остатком и «✓ точно»; ИИ получает [Программа] с turnComplete', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final p = testProduct('p1', 'Кока-Кола 1 л', price: 95, qty: 12, barcode: '5449000000996');
      final rig = await withCam(t, VideoProductCard(product: p, name: p.name, price: 95, stock: 12, unit: 'шт', exactBarcode: true));
      expect(find.text(l.vcWhatIsIt), findsOneWidget);
      expect(find.text(l.vcShelf), findsOneWidget); // право «Склад» есть
      await t.tap(find.text(l.vcWhatIsIt));
      await ui(t);
      expect(find.text('Кока-Кола 1 л'), findsOneWidget);
      expect(find.text(l.vcPrice('95')), findsOneWidget);
      expect(find.text(l.vcStock('12 шт')), findsOneWidget);
      expect(find.textContaining(l.vcExact), findsOneWidget);
      final msg = rig.socket.sent.lastWhere((m) => m['clientContent'] != null)['clientContent'] as Map;
      expect(msg['turnComplete'], true);
      expect(((msg['turns'] as List).first as Map)['parts'][0]['text'], contains('[Программа] Камера: товар «Кока-Кола 1 л», цена 95 сом, остаток 12 шт'));
      expect(rig.c.turns.where((x) => x.table), isNotEmpty); // карточка уйдёт в чат
      // ✕ закрывает
      await t.tap(find.byIcon(Icons.close_rounded).last);
      await t.pump(const Duration(milliseconds: 300));
      expect(find.text(l.vcPrice('95')), findsNothing);
      await rig.dispose(t);
    });

    testWidgets('без права «Склад»: остатка нет ни в карточке, ни в сообщении ИИ; кнопки «Полка» нет', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final p = testProduct('p1', 'Кока-Кола 1 л', price: 95, qty: 12);
      final rig = await withCam(t, VideoProductCard(product: p, name: p.name, price: 95, stock: null, unit: 'шт', exactBarcode: true), session: testCashier(perms: const [Session.sale]));
      expect(find.text(l.vcShelf), findsNothing);
      await t.tap(find.text(l.vcWhatIsIt));
      await ui(t);
      expect(find.textContaining('Остаток'), findsNothing);
      expect(rig.socket.texts.last, isNot(contains('остаток')));
      await rig.dispose(t);
    });

    testWidgets('«Похоже на:» — три варианта-кнопки; выбор даёт карточку товара и сообщение ИИ', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final a = testProduct('p1', 'Кока-Кола 1 л', price: 95);
      final b = testProduct('p2', 'Кока-Кола 0,5 л', price: 60);
      final rig = await withCam(t, VideoProductCard(alternatives: [a, b]));
      await t.tap(find.text(l.vcWhatIsIt));
      await ui(t);
      expect(find.text(l.vcLooksLike), findsOneWidget);
      await t.tap(find.text('Кока-Кола 0,5 л'));
      await ui(t);
      expect(find.text(l.vcLooksLike), findsNothing);
      expect(find.text(l.vcPrice('60')), findsOneWidget);
      await rig.dispose(t);
    });

    testWidgets('не найден: плашка «В каталоге не нашёл…» и «Повторить»', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await withCam(t, const VideoProductCard());
      await t.tap(find.text(l.vcWhatIsIt));
      await ui(t);
      expect(find.text(l.vcNotFound), findsOneWidget);
      expect(find.text(l.retry), findsOneWidget);
      await rig.dispose(t);
    });

    testWidgets('«Накладная»: с правом «Закупки» — «Открыть приход (2)»: камера отпускается, звонок завершается; без права — кнопки нет', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final lines = [
        ReceiveLine(productId: 'p1', name: 'Кока-Кола 1 л', barcode: '', qty: 24, cost: 70, price: 95),
        ReceiveLine(productId: 'p2', name: 'Fanta 1 л', barcode: '', qty: 24, cost: 68, price: 90),
      ];
      final card = VideoInvoiceCard(
        lines: 3,
        known: 2,
        fresh: 1,
        preview: const [VideoInvoiceLine('Кока-Кола 1 л', 24, 70, true), VideoInvoiceLine('Fanta 1 л', 24, 68, true), VideoInvoiceLine('Burn 0,25 л', 24, 55, false)],
        ready: lines,
        canProcure: true,
      );
      final rig = await withCam(t, card);
      await t.tap(find.text(l.vcInvoice));
      await ui(t);
      expect(find.text(l.vcInvoiceSummary(3, 2, 1)), findsOneWidget);
      expect(find.text(l.vcNewTag), findsOneWidget);
      expect(find.text(l.vcNewItemsHint), findsOneWidget);
      expect(find.text(l.vcReceiveEndsCall), findsOneWidget);
      await t.tap(find.text(l.vcOpenReceive(2)));
      await ui(t);
      expect(rig.c.call!.ended, isTrue);
      expect(rig.frames.open, isFalse);
      await rig.dispose(t);
    });

    testWidgets('«Накладная» без права «Закупки»: «Открыть приход» нет, подсказка «приход проводит администратор»', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final card = VideoInvoiceCard(
        lines: 1,
        known: 1,
        fresh: 0,
        preview: const [VideoInvoiceLine('Кока-Кола 1 л', 24, 70, true)],
        ready: [ReceiveLine(productId: 'p1', name: 'Кока-Кола 1 л', barcode: '', qty: 24, cost: 70, price: 95)],
      );
      final rig = await withCam(t, card, session: testCashier());
      await t.tap(find.text(l.vcInvoice));
      await ui(t);
      expect(find.textContaining(l.vcOpenReceive(1).split(' (').first), findsNothing);
      expect(find.text(l.vcNoProcure), findsOneWidget);
      expect(rig.c.call!.ended, isFalse);
      await rig.dispose(t);
    });

    testWidgets('«Полка»: строки «видно / в программе» и подпись «ИИ считает на глаз»', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final rig = await withCam(t, const VideoShelfCard([VideoShelfRow('Fanta 1 л', 2, 4), VideoShelfRow('Mars 50 г', 3, 1)]));
      await t.ensureVisible(find.text(l.vcShelf));
      await t.tap(find.text(l.vcShelf));
      await ui(t);
      expect(find.text('Fanta 1 л'), findsOneWidget);
      expect(find.text(l.vcShelfHint), findsOneWidget);
      expect(find.text(l.vcShelfCols), findsOneWidget);
      await rig.dispose(t);
    });

    for (final ok in [true, false]) {
      testWidgets('«Ценник»: ${ok ? 'совпадает ✓' : 'не совпадает ⚠'} — две цены, кнопки «Исправить цену» нет', (t) async {
        final l = lookupAppLocalizations(const Locale('ru'));
        final rig = await withCam(t, VideoTagCard('Сок Gracio 1 л', 120, ok ? 120 : 135));
        await t.ensureVisible(find.text(l.vcPriceTag));
        await t.tap(find.text(l.vcPriceTag));
        await ui(t);
        expect(find.text(l.vcOnTag), findsOneWidget);
        expect(find.text(l.vcInProgram), findsOneWidget);
        expect(find.text(ok ? l.vcTagOk : l.vcTagMismatch('120', '135')), findsOneWidget);
        expect(find.byIcon(ok ? Icons.check_rounded : Icons.warning_amber_rounded), findsOneWidget);
        expect(find.textContaining('Исправить'), findsNothing);
        await rig.dispose(t);
      });
    }

    testWidgets('фраза «что это?» при включённой камере = нажатие кнопки', (t) async {
      final l = lookupAppLocalizations(const Locale('ru'));
      final p = testProduct('p1', 'Сок', price: 120);
      final rig = await withCam(t, VideoProductCard(product: p, name: p.name, price: 120, unit: 'шт', exactBarcode: true));
      rig.socket.said('Что это?');
      await t.pump(const Duration(milliseconds: 40));
      rig.socket.turnComplete();
      await t.pump(const Duration(milliseconds: 800));
      expect(find.text(l.vcPrice('120')), findsOneWidget);
      await rig.dispose(t);
    });

    for (final lang in ['ru', 'ky']) {
      for (final dark in [false, true]) {
        testWidgets('$lang${dark ? ' · тёмная' : ''}: камера включена, шрифт 1.3, вертикально и 844×390 — без переполнений', (t) async {
          final l = lookupAppLocalizations(Locale(lang));
          final p = testProduct('p1', 'Кока-Кола 1 л', price: 95, qty: 12);
          final rig = await withCam(t, VideoProductCard(product: p, name: p.name, price: 95, stock: 12, unit: 'шт', exactBarcode: true), lang: lang, dark: dark, scale: 1.3);
          await t.tap(find.text(l.vcWhatIsIt));
          await ui(t);
          expect(t.takeException(), isNull);
          // горизонтально
          t.view.physicalSize = const Size(1688, 780);
          await ui(t);
          expect(t.takeException(), isNull);
          await rig.dispose(t);
        });
      }
    }
  });
}
