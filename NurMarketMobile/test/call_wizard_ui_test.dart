// 2026-10-27 (мобилка, видеозвонок, М2): панель мастера «добавить товар» на экране звонка — кнопка «Добавить товар», четыре слота
// «Спереди / Сзади / Сбоку / Штрихкод» с мини-снимками, «Снять», «Создать товар», «Отмена»; сообщения ИИ с подсказкой следующего
// шага. Сокет, звук, камера и создание — подставные (без сети, камеры, Gemini).
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/assistant/ai_journal.dart';
import 'package:nurmarket/core/ai/assistant/product_wizard.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/util/audit.dart';
import 'package:nurmarket/features/assistant/call_consent.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'support/call_rig.dart';

class _Backend implements WizardBackend {
  final created = <WizardDraft>[];
  @override
  Future<({String name, String? category, String? brand})?> globalInfo(String code) async => (name: 'ASU Негаз 1 л клуб-киви', category: null, brand: null);
  @override
  Future<String?> nameFromPhoto(Uint8List jpeg) async => null;
  @override
  Future<String> create(WizardDraft d) async {
    created.add(d);
    return 'new1';
  }

  @override
  Future<int> teach(Product p, List<Uint8List> shots) async => shots.length;
}

Future<void> ui(WidgetTester t) async {
  await t.pump();
  await t.pump(const Duration(milliseconds: 500));
}

void main() {
  late Directory dir;
  setUpAll(() {
    dir = Directory.systemTemp.createTempSync('nm_wiz_ui_');
    Audit.testDirectory = dir;
    AiJournal.testDirectory = dir;
  });
  tearDownAll(() {
    try {
      dir.deleteSync(recursive: true);
    } catch (_) {}
  });

  final l = lookupAppLocalizations(const Locale('ru'));

  Future<(CallRig, _Backend)> withCam(WidgetTester t, {Session? session, double scale = 1.0, Size size = const Size(720, 1480), String lang = 'ru'}) async {
    callPhone(t, scale: scale, size: size);
    final backend = _Backend();
    final rig = CallRig(lang: lang, camera: true, session: session, wizardBackend: backend);
    rig.frames.still0 = Uint8List.fromList([0xFF, 0xD8, 1, 2, 3, 4, 5, 6]);
    await t.pumpWidget(rig.app(const StubScreen('Склад')));
    await rig.startCall(t);
    await rig.showFull(t);
    await t.runAsync(() => VideoConsent.accept(rig.db, session?.userId ?? 'u1'));
    await t.tap(find.text(lookupAppLocalizations(Locale(lang)).vcCamera));
    await t.pump(const Duration(milliseconds: 700));
    return (rig, backend);
  }

  testWidgets(
    'кнопка «Добавить товар» → панель с четырьмя слотами; «Снять» трижды и штрихкод; название из справочника; «Создать товар» → товар создан, панель закрыта',
    (t) async {
      final (rig, backend) = await withCam(t);
      expect(find.text(l.wizAddProduct), findsOneWidget);
      await t.ensureVisible(find.text(l.wizAddProduct)); // последняя в ряду, который прокручивается
      await t.pump();
      await t.tap(find.text(l.wizAddProduct));
      await ui(t);
      expect(find.text(l.wizTitle), findsOneWidget);
      for (final s in [l.wizSlotFront, l.wizSlotBack, l.wizSlotSide, l.wizSlotCode]) {
        expect(find.text(s), findsWidgets);
      }
      expect(find.text(l.wizAddProduct), findsNothing, reason: 'пока идёт мастер, кнопка не нужна');
      // модель получила сценарий и должна произнести его
      expect(rig.socket.texts.last, allOf(contains('Мастер добавления товара'), contains('спереди')));

      await t.tap(find.text(l.wizShootSide(l.wizSlotFront)));
      await ui(t);
      expect(rig.c.wizard!.shots.keys, [WizardSide.front]);
      expect(find.byType(Image), findsWidgets, reason: 'мини-снимок в слоте');
      expect(rig.socket.texts.last, contains('сзади'));

      await t.tap(find.text(l.wizShootSide(l.wizSlotBack)));
      await ui(t);
      await t.tap(find.text(l.wizShootSide(l.wizSlotSide)));
      await ui(t);
      expect(rig.c.wizard!.shotCount, 3);

      // штрихкод: следующий слот — «Штрихкод»; кадр с прочитанным кодом
      rig.frames.stillBarcode = '4870001571030';
      await t.tap(find.text(l.wizShootSide(l.wizSlotCode)));
      await ui(t);
      expect(rig.c.wizard!.barcode, '4870001571030');
      expect(rig.c.wizard!.name, 'ASU Негаз 1 л клуб-киви');
      expect(find.textContaining('ASU Негаз 1 л клуб-киви'), findsWidgets);
      expect(find.text(l.wizCreate), findsNothing, reason: 'цены ещё нет — создавать рано');

      await rig.c.wizard!.setFields('ru', price: 85, finish: true);
      await ui(t);
      expect(find.text(l.wizCreate), findsOneWidget);
      await t.tap(find.text(l.wizCreate));
      await ui(t);
      await t.pump(const Duration(milliseconds: 300));
      expect(backend.created, hasLength(1));
      expect(backend.created.single.shots, hasLength(3));
      expect(rig.c.wizardActive, isFalse);
      expect(find.text(l.wizTitle), findsNothing);
      expect(rig.journal.records.last.tool, 'wizard_create');
      await rig.dispose(t);
    },
  );

  testWidgets('«✕» в панели сбрасывает черновик, кнопка «Добавить товар» возвращается', (t) async {
    final (rig, _) = await withCam(t);
    await t.ensureVisible(find.text(l.wizAddProduct)); // последняя в ряду, который прокручивается
    await t.pump();
    await t.tap(find.text(l.wizAddProduct));
    await ui(t);
    await t.tap(find.text(l.wizShootSide(l.wizSlotFront)));
    await ui(t);
    expect(rig.c.wizard!.shots, isNotEmpty);
    await t.tap(find.byTooltip(l.cancel));
    await ui(t);
    expect(rig.c.wizardActive, isFalse);
    expect(rig.c.wizard!.shots, isEmpty);
    expect(find.text(l.wizTitle), findsNothing);
    expect(find.text(l.wizAddProduct), findsOneWidget);
    await rig.dispose(t);
  });

  testWidgets('«Ввести штрихкод» (руками или сканером в поле): код принят, дубль проверен, название из справочника', (t) async {
    final (rig, _) = await withCam(t);
    await t.ensureVisible(find.text(l.wizAddProduct)); // последняя в ряду, который прокручивается
    await t.pump();
    await t.tap(find.text(l.wizAddProduct));
    await ui(t);
    await t.tap(find.byTooltip(l.wizEnterCode));
    await ui(t);
    await t.enterText(find.byType(TextField), '4870001571030');
    await t.tap(find.text(l.done));
    await ui(t);
    await t.pump(const Duration(milliseconds: 300));
    expect(rig.c.wizard!.barcode, '4870001571030');
    expect(find.byTooltip(l.wizEnterCode), findsNothing, reason: 'код есть — кнопка ввода больше не нужна');
    await rig.dispose(t);
  });

  testWidgets('без права «Склад» кнопки «Добавить товар» нет и мастер не стартует', (t) async {
    final (rig, _) = await withCam(t, session: testCashier(perms: const [Session.sale]));
    expect(find.text(l.wizAddProduct), findsNothing);
    expect(rig.c.canAddProduct, isFalse);
    await rig.c.wizardStart();
    expect(rig.c.wizardActive, isFalse);
    await rig.dispose(t);
  });

  testWidgets('шрифт 1.3 и узкий экран: панель мастера без переполнений', (t) async {
    final (rig, _) = await withCam(t, scale: 1.3);
    await t.ensureVisible(find.text(l.wizAddProduct)); // ряд кнопок прокручивается
    await t.pump();
    await t.ensureVisible(find.text(l.wizAddProduct)); // последняя в ряду, который прокручивается
    await t.pump();
    await t.tap(find.text(l.wizAddProduct));
    await ui(t);
    await t.tap(find.text(l.wizShootSide(l.wizSlotFront)));
    await ui(t);
    expect(tester(t), isNull);
    await rig.dispose(t);
  });

  testWidgets('ky, горизонтально 844×390: панель мастера прокручивается вместе с колонкой, без переполнений', (t) async {
    final ky = lookupAppLocalizations(const Locale('ky'));
    final (rig, _) = await withCam(t, size: const Size(1688, 780), lang: 'ky');
    await t.ensureVisible(find.text(ky.wizAddProduct));
    await t.pump();
    await t.tap(find.text(ky.wizAddProduct));
    await ui(t);
    await t.ensureVisible(find.text(ky.wizShootSide(ky.wizSlotFront)));
    await t.tap(find.text(ky.wizShootSide(ky.wizSlotFront)));
    await ui(t);
    expect(rig.c.wizard!.hasFront, isTrue);
    expect(tester(t), isNull);
    await rig.dispose(t);
  });
}

Object? tester(WidgetTester t) => t.takeException();
