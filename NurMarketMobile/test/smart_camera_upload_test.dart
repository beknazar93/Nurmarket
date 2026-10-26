// 2026-10-26 (П0, ошибка денег Б1): «Загрузить на склад» в умной камере. Раньше каждое нажатие строило строки прихода
// заново: после частичной ошибки повтор второй раз прибавлял остаток проведённым строкам, а товар с потерянным ответом
// создавался вторым. Здесь — склад NurCRM в памяти (StockServer из admin_stock_fixes_test.dart), сети и боевой базы нет.
import 'package:drift/native.dart';
import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:nurmarket/core/ai/vision.dart';
import 'package:nurmarket/core/api/api_client.dart';
import 'package:nurmarket/core/api/session_store.dart';
import 'package:nurmarket/core/db/database.dart';
import 'package:nurmarket/core/providers.dart';
import 'package:nurmarket/core/theme/theme.dart';
import 'package:nurmarket/features/scan/smart_camera.dart';
import 'package:nurmarket/l10n/app_localizations.dart';

import 'admin_stock_fixes_test.dart' show FakeApi, StockServer, stockOf;

Product _p(String id, String name, double qty) => Product(
  id: id,
  name: name,
  price: 100,
  wholesalePrice: 0,
  promoRules: '',
  minQuantity: 0,
  quantity: qty,
  unit: 'шт',
  isWeight: false,
  isService: false,
  isFavorite: false,
  altBarcodes: '',
  category: '',
  searchKey: name.toLowerCase(),
);

SeenMatch _known(Product p, int count) => SeenMatch(SeenItem(name: p.name, count: count, labelPrice: 40), [(p, 1.0)]);

const _admin = Session(email: 'a', access: '', refresh: '', role: 'owner', isOwner: true, perms: []);

void main() {
  late AppDatabase db;
  late StockServer s;
  late FakeApi api;
  // журнал запросов: сколько раз и каким телом писали остаток
  late List<String> log;
  // товары, которым сервер отказывает по существу (400) при записи остатка
  late Set<String> rejectPatch;
  var offline = false;

  setUp(() {
    db = AppDatabase(NativeDatabase.memory());
    s = StockServer();
    log = [];
    rejectPatch = {};
    offline = false;
    api = FakeApi((m, p, b, q) async {
      if (offline) throw ApiException('Нет связи');
      log.add('$m $p');
      final pm = RegExp(r'^api/main/products/(p\d+)/$').firstMatch(p);
      if (m == 'PATCH' && pm != null && rejectPatch.contains(pm.group(1))) throw ApiException('Отказ сервера', status: 400);
      return s.handle(m, p, b, q);
    })..attach(_admin);
  });
  tearDown(() => db.close());

  int qtyPatches(String id) => log.where((e) => e == 'PATCH api/main/products/$id/').length;

  Future<void> pumpScreen(WidgetTester tester, List<SeenMatch> matches, {String mode = 'shelf', Session session = _admin}) async {
    tester.view.physicalSize = const Size(900, 3000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    api.attach(session);
    await tester.pumpWidget(
      ProviderScope(
        overrides: [databaseProvider.overrideWithValue(db), apiProvider.overrideWithValue(api)],
        child: MaterialApp(
          theme: buildTheme(dark: false),
          locale: const Locale('ru'),
          supportedLocales: AppLocalizations.supportedLocales,
          localizationsDelegates: const [AppLocalizations.delegate, GlobalMaterialLocalizations.delegate, GlobalWidgetsLocalizations.delegate, GlobalCupertinoLocalizations.delegate],
          home: SmartCameraScreen(debugMatches: matches, debugMode: mode),
        ),
      ),
    );
    await tester.pump();
  }

  // сеть/база работают на настоящих таймерах: чередуем ожидание и кадры, пока всё не осядет
  Future<void> settle(WidgetTester tester) async {
    for (var i = 0; i < 12; i++) {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 40)));
      await tester.pump();
    }
  }

  Future<void> tapUpload(WidgetTester tester) async {
    await tester.tap(find.byType(FilledButton));
    await settle(tester);
    // полка: вопрос «прибавить как поставку?» — отвечаем «Да»
    final yes = find.text('Добавить');
    if (yes.evaluate().isNotEmpty) {
      await tester.tap(yes);
      await settle(tester);
    }
  }

  testWidgets('частичный отказ: повтор не прибавляет проведённым и добавляет отказанной ровно один раз', (tester) async {
    final a = s.add('Вода', qty: 10), b = s.add('Сок', qty: 20), c = s.add('Чай', qty: 30);
    rejectPatch = {c};
    await pumpScreen(tester, [_known(_p(a, 'Вода', 10), 5), _known(_p(b, 'Сок', 20), 3), _known(_p(c, 'Чай', 30), 2)], mode: 'invoice');
    await tapUpload(tester);
    expect(stockOf(s, a), 15);
    expect(stockOf(s, b), 23);
    expect(stockOf(s, c), 30, reason: 'отказанная не изменилась');
    // проведённые ушли из списка, осталась одна карточка с причиной и итогом
    expect(find.text('Вода'), findsNothing);
    expect(find.text('Сок'), findsNothing);
    expect(find.text('Чай'), findsOneWidget);
    expect(find.textContaining('Проведено 2 из 3, не проведено 1'), findsWidgets);
    expect(find.textContaining('Отклонено: Отказ сервера'), findsWidgets);

    rejectPatch = {};
    await tapUpload(tester);
    expect(stockOf(s, a), 15, reason: 'проведённая строка не прибавлена второй раз');
    expect(stockOf(s, b), 23);
    expect(stockOf(s, c), 32, reason: 'отказанная прибавлена один раз');
    expect(qtyPatches(a), 1);
    expect(qtyPatches(b), 1);
    expect(find.text('Чай'), findsNothing, reason: 'всё проведено — позиция ушла');
  });

  testWidgets('потерян ответ на запись остатка: повтор тем же состоянием не прибавляет второй раз', (tester) async {
    final a = s.add('Вода', qty: 10), b = s.add('Сок', qty: 20);
    s.losePatch = 1; // сервер выполнит PATCH первой строки, ответ не дойдёт
    await pumpScreen(tester, [_known(_p(a, 'Вода', 10), 5), _known(_p(b, 'Сок', 20), 3)], mode: 'invoice');
    await tapUpload(tester);
    expect(stockOf(s, a), 15, reason: 'сервер записал, ответ потерялся');
    expect(stockOf(s, b), 23);
    expect(find.text('Вода'), findsOneWidget, reason: 'исход неизвестен — строка остаётся');
    expect(find.textContaining('Нет ответа сервера'), findsWidgets);
    expect(find.textContaining('Проведено 1 из 2, не проведено 1'), findsWidgets);

    await tapUpload(tester);
    expect(stockOf(s, a), 15, reason: 'повтор не прибавил второй раз');
    expect(stockOf(s, b), 23);
    expect(find.text('Вода'), findsNothing);
  });

  testWidgets('потерян ответ на создание товара: повтор не создаёт второй товар', (tester) async {
    s.loseCreate = 1; // товар создан на сервере, ответ потерян
    await pumpScreen(tester, [
      const SeenMatch(SeenItem(name: 'Новый сок', count: 2, labelPrice: 50), []),
    ], mode: 'invoice');
    await tapUpload(tester);
    expect(s.created, 1, reason: 'на сервере уже есть');
    expect(find.textContaining('Нет ответа сервера'), findsWidgets);

    await tapUpload(tester);
    expect(s.created, 1, reason: 'второй товар не создан');
    final id = s.products.keys.single;
    expect(stockOf(s, id), 2, reason: 'остаток прибавлен один раз');
    expect(find.text('Новый сок'), findsNothing);
  });

  testWidgets('двойное нажатие «Загрузить»: остаток прибавляется один раз', (tester) async {
    final a = s.add('Вода', qty: 10);
    await pumpScreen(tester, [_known(_p(a, 'Вода', 10), 5)], mode: 'invoice');
    await tester.tap(find.byType(FilledButton));
    await tester.tap(find.byType(FilledButton), warnIfMissed: false);
    await settle(tester);
    expect(stockOf(s, a), 15);
    expect(qtyPatches(a), 1);
  });

  testWidgets('нет связи: ничего не проводится, список остаётся черновиком', (tester) async {
    final a = s.add('Вода', qty: 10);
    await pumpScreen(tester, [_known(_p(a, 'Вода', 10), 5)], mode: 'invoice');
    offline = true;
    await tapUpload(tester);
    expect(log, isEmpty, reason: 'ни одного запроса записи');
    expect(stockOf(s, a), 10);
    expect(find.text('Вода'), findsOneWidget);
    expect(find.textContaining('Нет связи с сервером'), findsWidgets);

    offline = false;
    await tapUpload(tester);
    expect(stockOf(s, a), 15);
  });

  testWidgets('накладная: без права «Закупки» кнопки «Загрузить на склад» нет; полка — есть', (tester) async {
    final a = s.add('Вода', qty: 10);
    final keeper = Session(email: 'k', access: '', refresh: '', role: 'cashier', perms: [Session.viewProducts]);
    await pumpScreen(tester, [_known(_p(a, 'Вода', 10), 5)], mode: 'invoice', session: keeper);
    expect(find.byType(FilledButton), findsNothing);
    expect(find.text('В приёмку с поставщиком'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    await pumpScreen(tester, [_known(_p(a, 'Вода', 10), 5)], mode: 'shelf', session: keeper);
    expect(find.byType(FilledButton), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
    final buyer = Session(email: 'b', access: '', refresh: '', role: 'cashier', perms: [Session.viewProducts, Session.procurement]);
    await pumpScreen(tester, [_known(_p(a, 'Вода', 10), 5)], mode: 'invoice', session: buyer);
    expect(find.byType(FilledButton), findsOneWidget);
  });

  group('camSyncLine: строка одна и та же до успеха', () {
    final water = _p('p1', 'Вода', 10);
    final tea = _p('p2', 'Чай', 5);

    test('тот же объект при повторе, обновляются только количество и закупка', () {
      final l1 = camSyncLine(null, product: water, name: 'Вода', barcode: '', qty: 5, cost: 30, price: 0);
      final l2 = camSyncLine(l1, product: water, name: 'Вода', barcode: '', qty: 7, cost: 31, price: 0);
      expect(identical(l1, l2), isTrue);
      expect(l2.qty, 7);
      expect(l2.cost, 31);
      expect(l2.price, 100, reason: 'цена известного товара не меняется');
    });

    test('новая строка: тот же объект, пока название и штрихкод те же', () {
      final l1 = camSyncLine(null, product: null, name: 'Сок', barcode: '', qty: 2, cost: 50, price: 65);
      expect(identical(camSyncLine(l1, product: null, name: 'Сок', barcode: '', qty: 3, cost: 50, price: 70), l1), isTrue);
      expect(l1.price, 70);
    });

    test('выбрали «Это «…»?» до отправки — строка заменяется; после отправки без ответа — нет', () {
      final fresh = camSyncLine(null, product: null, name: 'Чай зелёный', barcode: '', qty: 1, cost: 20, price: 30);
      final known = camSyncLine(fresh, product: tea, name: 'Чай', barcode: '', qty: 1, cost: 20, price: 0);
      expect(identical(known, fresh), isFalse);
      expect(known.productId, 'p2');

      final sent = camSyncLine(null, product: null, name: 'Чай зелёный', barcode: '', qty: 1, cost: 20, price: 30)..createUnknown = true;
      expect(camLineLocked(sent), isTrue);
      expect(identical(camSyncLine(sent, product: tea, name: 'Чай', barcode: '', qty: 1, cost: 20, price: 0), sent), isTrue);

      final stock = camSyncLine(null, product: water, name: 'Вода', barcode: '', qty: 1, cost: 1, price: 0)..pendingTarget = 11;
      expect(camLineLocked(stock), isTrue);
    });

    test('состояния строки: ждёт / отклонена / нет ответа / проведена', () {
      final l = camSyncLine(null, product: water, name: 'Вода', barcode: '', qty: 1, cost: 1, price: 0);
      expect(camRowState(l, null), CamRowState.pending);
      expect(camRowState(l, 'Отказ'), CamRowState.rejected);
      l.pendingTarget = 11;
      expect(camRowState(l, null), CamRowState.unknown);
      l.pendingTarget = null;
      l.posted = true;
      expect(camRowState(l, 'старая'), CamRowState.posted);
    });
  });
}
