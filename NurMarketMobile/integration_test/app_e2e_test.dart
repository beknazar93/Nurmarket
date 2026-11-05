// 2026-11-05 (E2E-тесты мобилки, первый этап; разрешено владельцем): сценарии на эмуляторе Pixel_7_API_35 с поддельным сервером.
//   а) вход и главный экран;  б) открытие смены и продажа наличными с чеком («Округление» для весового);  в) возврат;
//   г) офлайн-очередь: сервер недоступен -> чек в очереди -> сервер вернулся -> уходит один раз; 503 — очередь ждёт, не теряет;
//   д) миграция базы 9 -> 10, данные целы.
// Сервер — fake_nur_server.dart внутри самого теста (127.0.0.1 устройства); боевой сервер, настоящие учётки и ключи не используются.
// Запуск: tools/qa/mobile-e2e.ps1 (поднимает эмулятор, подставляет NURMARKET_API_URL, останавливает эмулятор).
import 'dart:io';

import 'package:drift/drift.dart' show Value;
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/util/ui.dart';
import 'package:nurmarket/features/receipt/receipt.dart' show roundingSigned;

import 'support/fake_nur_server.dart';
import 'support/harness.dart';

late FakeNurServer server;

// -------------------------------------------------------------------- общие шаги

Finder get _dialog => find.byType(AlertDialog);

/// Вход через экран входа и ожидание главного (касса выбирается сама — она одна).
Future<void> signIn(WidgetTester t, Launched app) async {
  await pumpUntil(t, find.byKey(const Key('loginEmail')), reason: 'экран входа');
  await t.enterText(find.byKey(const Key('loginEmail')), fakeEmail);
  await t.enterText(find.byKey(const Key('loginPassword')), fakePassword);
  await t.tap(find.byKey(const Key('loginSubmit')));
  await pumpUntil(t, find.byKey(const Key('openShiftBtn')), reason: 'главный экран с кнопкой «Открыть смену»');
}

/// «Открыть смену» с суммой 1000 сом.
Future<void> openShift(WidgetTester t, Launched app) async {
  await t.tap(find.byKey(const Key('openShiftBtn')));
  await pumpUntil(t, _dialog, reason: 'окно суммы в кассе');
  await t.enterText(find.descendant(of: _dialog, matching: find.byType(TextField)), '1000');
  await t.pump(const Duration(milliseconds: 200));
  await t.tap(find.descendant(of: _dialog, matching: find.byType(FilledButton)));
  await pumpUntil(t, find.text(app.l.closeShift), reason: 'кнопка «Закрыть смену» (смена открыта)');
}

Future<void> signInAndOpenShift(WidgetTester t, Launched app) async {
  await signIn(t, app);
  await openShift(t, app);
}

/// Вкладка «Продажа» в нижней панели и ожидание каталога на экране.
Future<void> goToSale(WidgetTester t, Launched app) async {
  await t.tap(find.byIcon(Icons.shopping_cart_outlined).last);
  await pumpUntil(t, find.text(server.products.first.name), reason: 'товары каталога на экране продажи');
}

Future<void> addByName(WidgetTester t, Launched app, String name) async {
  final before = app.container.read(cartProvider).lines.length;
  await t.tap(find.text(name).first);
  // без связи товар добавляется чуть дольше (сначала безуспешный запрос размеров) — ждём, пока он реально попал в чек
  await pumpUntilTrue(t, () => app.container.read(cartProvider).lines.length > before, reason: 'товар «$name» в чеке');
  await pumpFor(t, const Duration(milliseconds: 300));
}

/// Нажать кнопку по ключу; если она за краем экрана (длинный чек) — сначала прокрутить к ней.
Future<void> tapKey(WidgetTester t, Key key) async {
  final f = find.byKey(key);
  await t.ensureVisible(f);
  await t.pump(const Duration(milliseconds: 200));
  await t.tap(f);
}

Future<void> addWeighed(WidgetTester t, Launched app, String name, String kg) async {
  // список товаров ленивый: товар в конце каталога через поиск (поле поиска — первое поле на экране продажи)
  if (find.text(name).evaluate().isEmpty) {
    await t.enterText(find.byType(TextField).first, name.split(' ').last.replaceAll(RegExp('[«»]'), ''));
    await pumpUntil(t, find.text(name), reason: 'товар «$name» в результатах поиска');
  }
  final before = app.container.read(cartProvider).lines.length;
  await t.tap(find.text(name).first);
  await pumpUntil(t, _dialog, reason: 'окно веса');
  await t.enterText(find.descendant(of: _dialog, matching: find.byType(TextField)), kg);
  await t.pump(const Duration(milliseconds: 200));
  await t.tap(find.descendant(of: _dialog, matching: find.byType(FilledButton)));
  await pumpUntilTrue(t, () => app.container.read(cartProvider).lines.length > before, reason: 'взвешенный товар в чеке');
  await pumpFor(t, const Duration(milliseconds: 300));
}

/// «Оплатить» на продаже -> «Провести оплату» (наличные — способ по умолчанию).
Future<void> payCash(WidgetTester t) async {
  await tapKey(t, const Key('salePayBtn'));
  await pumpUntil(t, find.byKey(const Key('doPayBtn')), reason: 'экран оплаты');
  await pumpFor(t, const Duration(milliseconds: 300));
  await tapKey(t, const Key('doPayBtn'));
}

/// Продажа воды (85) + сыр на вес двумя взвешиваниями (0,065 и 0,301 кг по 75 сом): сервер даст 112,45, на бумаге строки дадут 112,46.
Future<void> fillCheck(WidgetTester t, Launched app) async {
  await addByName(t, app, 'Вода «Нур» 1,5 л');
  await addWeighed(t, app, 'Сыр «Сулугуни»', '0.065');
  await addWeighed(t, app, 'Сыр «Сулугуни»', '0.301');
}

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  binding.framePolicy = LiveTestWidgetsFlutterBindingFramePolicy.fullyLive;

  setUpAll(() async {
    server = FakeNurServer(fakeServerPort());
    await server.start();
  });

  tearDownAll(() async {
    // что приложение спрашивало у сервера, а он не знает (для сведения; тест на этом не падает)
    // ignore: avoid_print
    print('E2E: адреса, которых поддельный сервер не знает: ${server.unknownPaths.toList()..sort()}');
    await server.stop();
  });

  setUp(() async {
    server.resetState();
    if (!server.running) await server.start();
  });

  // ------------------------------------------------------------------ а) вход и главный экран
  group('а) вход и главный экран', () {
    testWidgets('верный пароль: касса выбрана, главная показывает магазин и «Открыть смену»', (t) async {
      final app = await launchApp(t);
      await signIn(t, app);
      expect(find.text(app.l.openShift), findsWidgets);
      final login = server.requests('POST', '/api/users/auth/login').single;
      expect((login.body as Map)['email'], fakeEmail);
      expect(login.status, 200);
      final s = app.container.read(sessionProvider)!;
      expect(s.companyName, 'Магазин E2E');
      expect(s.cashboxId, fakeCashboxId, reason: 'единственная касса выбрана сама');
      expect(s.isOwner, isTrue);
      await closeApp(t, app);
    });

    testWidgets('неверный пароль: «Неверный логин или пароль», на главную не пускает', (t) async {
      final app = await launchApp(t);
      await pumpUntil(t, find.byKey(const Key('loginEmail')));
      await t.enterText(find.byKey(const Key('loginEmail')), fakeEmail);
      await t.enterText(find.byKey(const Key('loginPassword')), 'не-тот-пароль');
      await t.tap(find.byKey(const Key('loginSubmit')));
      await pumpUntil(t, find.text(app.l.loginWrong), reason: 'сообщение «Неверный логин или пароль»');
      expect(app.container.read(sessionProvider), isNull);
      expect(find.byKey(const Key('openShiftBtn')), findsNothing);
      await closeApp(t, app);
    });
  });

  // ------------------------------------------------------------------ б) смена и продажа наличными
  group('б) смена и продажа наличными', () {
    testWidgets('открытие смены, продажа, экран «Оплата проведена», чек с «Округлением» для весового', (t) async {
      final app = await launchApp(t);
      await signInAndOpenShift(t, app);
      expect(server.openShiftId, isNotNull, reason: 'смена открыта на сервере');
      final open = server.requests('POST', '/api/construction/shifts/open').single;
      expect((open.body as Map)['opening_cash'], '1000.00');

      await goToSale(t, app);
      await fillCheck(t, app);
      // итог на кнопке «Оплатить» — по правилу сервера: 85 + FLOOR(4,875 + 22,575) = 112,45
      expect(find.text(app.l.payAmount(money(112.45))), findsOneWidget);
      await payCash(t);
      await pumpUntil(t, find.text(app.l.paidOk), reason: 'экран «Оплата проведена»');
      expect(find.textContaining(money(112.45)), findsWidgets);

      expect(server.salesCount, 1);
      final checkout = server.requests('POST', '/api/main/pos/checkout').single;
      expect(checkout.status, 201);
      expect(checkout.headers['idempotency-key'], isNotEmpty);
      final body = checkout.body as Map;
      expect(body['shift'], server.openShiftId);
      expect((body['payment'] as Map)['method'], 'cash');
      expect(server.sales.values.single['total'], '112.45');
      expect(await app.db.pending(), isEmpty, reason: 'продажа ушла сразу, очередь пуста');

      // чек: «Округление» — строки на бумаге (4,88 + 22,58 + 85 = 112,46) на копейку больше итога сервера (112,45)
      await t.tap(find.text(app.l.showReceipt));
      await pumpUntil(t, find.text('Округление'), reason: 'строка «Округление» на чеке');
      expect(find.text(roundingSigned(0.01)), findsOneWidget);
      expect(find.textContaining(money(112.45)), findsWidgets);
      await closeApp(t, app);
    });
  });

  // ------------------------------------------------------------------ в) возврат
  group('в) возврат', () {
    testWidgets('весь чек из «Чеков»: сервер получил возврат, статус чека «возвращён»', (t) async {
      final app = await launchApp(t);
      await signInAndOpenShift(t, app);
      await goToSale(t, app);
      await fillCheck(t, app);
      await payCash(t);
      await pumpUntil(t, find.text(app.l.paidOk));
      final saleId = server.sales.keys.single;

      await t.tap(find.byKey(const Key('newCheckBtn')));
      await pumpUntil(t, find.byIcon(Icons.receipt_long_outlined), reason: 'нижняя панель');
      await t.tap(find.byIcon(Icons.receipt_long_outlined).last);
      final number = server.sales[saleId]!['number'];
      await pumpUntil(t, find.text('${app.l.check} №$number'), reason: 'чек в списке «Эта смена»');
      await t.tap(find.text('${app.l.check} №$number').first);
      await pumpUntil(t, find.byKey(const Key('checkReturnBtn')), reason: 'кнопка «Возврат» в карточке чека');
      await tapKey(t, const Key('checkReturnBtn'));
      await pumpUntil(t, find.byKey(const Key('returnAllBtn')), reason: 'экран возврата');
      await pumpFor(t, const Duration(milliseconds: 500));
      await tapKey(t, const Key('returnAllBtn'));
      await pumpUntil(t, _dialog, reason: 'вопрос «Вернуть?»');
      await t.tap(find.descendant(of: _dialog, matching: find.byType(FilledButton)));
      await pumpUntil(t, find.text(app.l.returnDone), reason: 'документ возврата');

      expect(server.returns, hasLength(1));
      expect(server.returns.single['amount'], '112.45');
      expect(server.sales[saleId]!['status'], 'returned');
      final post = server.requests('POST', '/return').single;
      expect((post.body as Map)['idempotency_key'], isNotEmpty);
      await closeApp(t, app);
    });
  });

  // ------------------------------------------------------------------ г) офлайн-очередь
  group('г) офлайн-очередь', () {
    Future<void> sellOneWater(WidgetTester t, Launched app) async {
      await addByName(t, app, 'Вода «Нур» 1,5 л');
      await payCash(t);
    }

    /// Вернулась связь: приложение шлёт очередь при возврате в приложение и при первом ответе сервера — здесь то же самое вручную.
    /// Старое соединение мёртвое после остановки сервера, поэтому первый проход может уйти в пустоту — повторяем (как таймер).
    Future<void> syncUntilEmpty(WidgetTester t, Launched app) async {
      for (var i = 0; i < 4; i++) {
        await app.container.read(syncProvider).runOnce();
        if ((await app.db.pending()).isEmpty) return;
        await pumpFor(t, const Duration(milliseconds: 300));
      }
    }

    testWidgets('сервер недоступен -> чек в очереди -> сервер вернулся -> ушёл один раз, без дубля', (t) async {
      final app = await launchApp(t);
      await signInAndOpenShift(t, app);
      await goToSale(t, app);
      await server.stop();
      await sellOneWater(t, app);
      await pumpUntil(t, find.text(app.l.savedOffline), reason: 'экран «Чек сохранён на телефоне»');
      final queued = await app.db.pending();
      expect(queued, hasLength(1));
      expect(queued.single.kind, 'sale');
      expect(server.salesCount, 0);

      await server.start();
      await syncUntilEmpty(t, app);
      expect(await app.db.pending(), isEmpty, reason: 'очередь ушла');
      expect(server.salesCount, 1, reason: 'на сервере ровно одна продажа');
      expect(server.checkoutHitsByKey[queued.single.id], 1, reason: 'под этим ключом запрос дошёл один раз');
      // ещё проходы очереди ничего не добавляют
      await app.container.read(syncProvider).runOnce();
      await app.container.read(syncProvider).runOnce();
      expect(server.salesCount, 1);
      await closeApp(t, app);
    });

    testWidgets('сервер отвечает 503: чек ждёт в очереди, не теряется и не «Не принято»; потом уходит один раз', (t) async {
      final app = await launchApp(t);
      await signInAndOpenShift(t, app);
      await goToSale(t, app);
      server.checkoutPlan.addAll([const CheckoutStatus(503), const CheckoutStatus(503)]);
      await sellOneWater(t, app);
      await pumpUntil(t, find.text(app.l.savedOffline), reason: 'при 503 чек встаёт в очередь');
      var queued = await app.db.pending();
      expect(queued, hasLength(1));
      final key = queued.single.id;
      expect(server.salesCount, 0);

      // досылка получает второй 503 — запись остаётся ждать (pending), а не «Не принято» (failed)
      await app.container.read(syncProvider).runOnce();
      queued = await app.db.pending();
      expect(queued, hasLength(1), reason: 'очередь ждёт, чек не потерян');
      expect(queued.single.status, 'pending');
      expect(app.container.read(syncProvider).serverDownSince, isNotNull, reason: 'приложение знает, что сервер не работает');
      expect(server.salesCount, 0);

      // сервер выздоровел
      await app.container.read(syncProvider).runOnce();
      expect(await app.db.pending(), isEmpty);
      expect(server.salesCount, 1);
      expect(server.checkoutHitsByKey[key], 3, reason: '3 запроса одним ключом: два отказа и один успех');
      await closeApp(t, app);
    });

    testWidgets('сервер записал продажу, но ответ не дошёл: повтор тем же ключом, второй продажи нет', (t) async {
      final app = await launchApp(t);
      await signInAndOpenShift(t, app);
      await goToSale(t, app);
      server.checkoutPlan.add(const CheckoutDropAfterCommit());
      await sellOneWater(t, app);
      await pumpUntil(t, find.text(app.l.savedOffline), reason: 'исход неизвестен — чек в очереди');
      expect(server.salesCount, 1, reason: 'сервер уже записал продажу');
      final queued = await app.db.pending();
      expect(queued, hasLength(1));

      await app.container.read(syncProvider).runOnce();
      expect(await app.db.pending(), isEmpty);
      expect(server.salesCount, 1, reason: 'повтор с тем же ключом вторую продажу не создал');
      expect(server.checkoutHitsByKey[queued.single.id], 2);
      await closeApp(t, app);
    });

    testWidgets('медленный ответ сервера (4 с): кассир ждёт, продажа одна', (t) async {
      final app = await launchApp(t);
      await signInAndOpenShift(t, app);
      await goToSale(t, app);
      server.checkoutPlan.add(const CheckoutSlow(Duration(seconds: 4)));
      await sellOneWater(t, app);
      await pumpUntil(t, find.text(app.l.payWaiting), reason: 'подпись «Проводим оплату…»', timeout: const Duration(seconds: 4));
      await pumpUntil(t, find.text(app.l.paidOk), reason: 'оплата проведена после ответа', timeout: const Duration(seconds: 20));
      expect(server.salesCount, 1);
      expect(await app.db.pending(), isEmpty);
      await closeApp(t, app);
    });
  });

  // ------------------------------------------------------------------ д) миграция базы 9 -> 10
  group('д) миграция базы 9 -> 10', () {
    testWidgets('база схемы 9 с товарами, настройками и очередью открывается новым кодом, данные целы', (t) async {
      final dir = await Directory.systemTemp.createTemp('nm-e2e-mig-');
      final file = File('${dir.path}/pos_v9.db');
      try {
        // 1) база текущей схемы, из неё делаем схему 9: без колонки product_code и её индекса, user_version = 9
        var db = AppDatabase(NativeDatabase(file));
        await db.customSelect('SELECT 1').get();
        await db.replaceProducts([
          ProductsCompanion.insert(id: 'm1', name: 'Вода «Нур»', price: const Value(85), quantity: const Value(12), barcode: const Value('4870001112223'), searchKey: const Value('вода нур')),
          ProductsCompanion.insert(id: 'm2', name: 'Сыр на вес', price: const Value(75), unit: const Value('кг'), isWeight: const Value(true), quantity: const Value(12.5), searchKey: const Value('сыр на вес')),
          ProductsCompanion.insert(id: 'm3', name: 'Хлеб', price: const Value(32), quantity: const Value(60), searchKey: const Value('хлеб')),
        ]);
        await db.setSetting('ui.lang', 'ky');
        await db.setSetting('catalog.fullAt', DateTime.now().toIso8601String());
        await db.enqueue(
          OutboxCompanion.insert(id: 'old-sale-1', kind: 'sale', body: '{"shift":"s1","items":[]}', createdAt: DateTime(2026, 11, 4, 10), total: const Value(85), summary: const Value('Вода')),
        );
        await db.customStatement('DROP INDEX IF EXISTS ix_products_pcode_int');
        await db.customStatement('ALTER TABLE products DROP COLUMN product_code');
        await db.customStatement('PRAGMA user_version = 9');
        expect((await db.customSelect('PRAGMA user_version').getSingle()).read<int>('user_version'), 9);
        await db.close();

        // 2) тот же файл открывает НОВЫЙ код: миграция 9 -> 10
        db = AppDatabase(NativeDatabase(file));
        expect(await db.productCount(), 3, reason: 'товары на месте');
        expect((await db.productById('m1'))!.name, 'Вода «Нур»');
        expect((await db.productById('m2'))!.isWeight, isTrue);
        expect((await db.productById('m2'))!.quantity, 12.5);
        expect(await db.getSetting('ui.lang'), 'ky', reason: 'настройки целы');
        expect((await db.pending()).map((e) => e.id), ['old-sale-1'], reason: 'очередь продаж цела');
        expect((await db.customSelect('PRAGMA user_version').getSingle()).read<int>('user_version'), 10);
        final cols = await db.customSelect('PRAGMA table_info(products)').get();
        expect(cols.any((r) => r.read<String>('name') == 'product_code'), isTrue, reason: 'колонка «код товара» добавлена');
        final idx = await db.customSelect("SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'ix_products_pcode_int'").get();
        expect(idx, hasLength(1), reason: 'индекс по коду товара создан');
        expect(await db.getSetting('catalog.fullAt'), isNull, reason: 'время полной загрузки каталога сброшено — каталог подтянется целиком');
        // запись в новую колонку работает
        await db.customStatement("UPDATE products SET product_code = '77' WHERE id = 'm1'");
        expect((await db.productById('m1'))!.productCode, '77');
        await db.close();
      } finally {
        await dir.delete(recursive: true);
      }
    });
  });
}
